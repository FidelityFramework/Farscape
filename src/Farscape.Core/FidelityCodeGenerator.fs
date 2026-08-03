namespace Farscape.Core

open CodeAST
open ActivePatterns
open PilotTypes
open Types

/// Generates F# source in the Platform.Bindings pattern for Fidelity/Firefly consumption.
///
/// Output is a single .fs file with:
///   module Platform.Bindings.{Library}.{Category}
///   [<FidelityExtern("library", "symbol")>]
///   let functionName (param1: type1) (param2: type2) : returnType =
///       NativeDefault.zeroed ()
///
/// [<FidelityExtern>] carries library name + symbol through the PSG so Alex can emit
/// MLIR with fidelity.binding_strategy and fidelity.library_name attributes.
/// No DllImport, no Marshal, no BCL dependencies.
///
/// Architecture:
///   Catamorphism (DeclarationAlgebra) → FsDecl list → CodeRenderer.render
///   Active patterns for type classification and macro filtering.
///   Zero StringBuilder. Zero Regex. Zero mutable state.
module FidelityCodeGenerator =

    // =========================================================================
    // Typedef Resolution (catamorphism + pure recursive chain resolution)
    // =========================================================================

    /// Pure recursive typedef chain resolution.
    /// Follows chains: __off_t → __OFF_T_TYPE → long int, resolving to fixed point.
    let rec private resolveTypedefChains (maxDepth: int) (m: Map<string, string>) : Map<string, string> =
        if maxDepth = 0 then m
        else
            let resolved =
                m |> Map.map (fun _ underlyingType ->
                    match Map.tryFind underlyingType m with
                    | Some deeper -> deeper
                    | None -> underlyingType)
            if resolved = m then m
            else resolveTypedefChains (maxDepth - 1) resolved

    /// Build a typedef resolution map from parsed declarations.
    /// Uses catamorphism (DeclarationAlgebra.typedefAlgebra) for extraction,
    /// then pure recursive resolution for chain following.
    let buildTypedefMap (declarations: CppParser.Declaration list) : Map<string, string> =
        DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra declarations
        |> List.choose id
        |> Map.ofList
        |> resolveTypedefChains 10

    /// Resolve a C type through the typedef map.
    let resolveType (typedefMap: Map<string, string>) (baseType: string) : string =
        match Map.tryFind baseType typedefMap with
        | Some resolved -> resolved
        | None -> baseType

    // =========================================================================
    // Opaque Handle Detection (pre-pass over typedefs)
    // =========================================================================

    /// Detect opaque handle typedefs from the declaration list.
    /// Returns the set of typedef names that are opaque handles (pointer to undefined struct).
    let detectOpaqueHandles (declarations: CppParser.Declaration list) : Set<string> =
        let knownStructNames =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.definedStructNameAlgebra declarations
            |> List.choose id
            |> Set.ofList
        DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra declarations
        |> List.choose id
        |> List.filter (fun (_, underlyingType) ->
            let td : CppParser.TypedefInfo =
                { Name = ""; UnderlyingType = underlyingType; Documentation = None }
            ActivePatterns.isOpaqueHandleTypedef knownStructNames td)
        |> List.map fst
        |> Set.ofList

    /// Generate wrapper struct + companion module declarations for opaque handle types.
    let generateOpaqueHandleDecls (handleNames: Set<string>) : FsDecl list =
        handleNames
        |> Set.toList
        |> List.sort
        |> List.collect (fun name ->
            [ XmlDoc "Opaque handle wrapping a native pointer."
              RecordType(name, [("Handle", Named "nativeint")], None, ["Struct"])
              SubModule(name, [
                  LetBinding("zero", [],
                      Named name,
                      RecordConstruction [($"{name}.Handle", Literal "0n")],
                      [])
                  LetBinding("isNull", [{ Name = "h"; Type = Named name }],
                      Named "bool",
                      Comparison(Identifier "h.Handle", "=", Literal "0n"),
                      [])
                  LetBinding("ofHandle", [{ Name = "h"; Type = Named "nativeint" }],
                      Named name,
                      RecordConstruction [($"{name}.Handle", Identifier "h")],
                      [])
              ])
              BlankLine ])

    // =========================================================================
    // Fidelity-Specific Type Mapping (active patterns, zero Regex)
    // =========================================================================

    /// Map pointer/value type info to FsType using active pattern decomposition.
    let mapTypeInfo (baseTypeFn: string -> FsType) = function
        | CharPointer -> Generic("nativeptr", Named "byte")
        | VoidPointer -> Named "nativeint"
        | TypedPointer _ -> Named "nativeint"
        | ValueType baseType -> baseTypeFn baseType

    /// Map a C type string to an FsType suitable for Fidelity native compilation.
    /// Uses ParsedCType active pattern (XParsec-backed) instead of Regex/string munging.
    /// PlatformABI determines concrete widths for C int/long (resolved at generation time).
    /// Used by both FidelityCodeGenerator (Layer 1) and WrapperCodeGenerator (Layer 2).
    let mapCTypeToFidelityType (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (delegateNames: Set<string>) (cType: string) : FsType =
        // Opaque handle types preserve their wrapper struct name
        if opaqueHandles.Contains(cType) then Named cType
        // Delegate types (callback function pointers) → nativeint at ABI level.
        // CCS doesn't have CLR delegates; these are raw function pointers.
        // Type safety is provided by Layer 2 callback builders using FnPtr.fromSymbol.
        elif delegateNames.Contains(cType) then Named "nativeint"
        // Function pointer types: "void (*)(void)" or "void (**)(void)"; always nativeint
        elif cType.Contains("(*)") || cType.Contains("(**)") then Named "nativeint"
        else
        match cType with
        | ParsedCType info ->
            info |> mapTypeInfo (fun baseType ->
                // Check type dictionary FIRST — preserves platform-abstract types
                // e.g. size_t → unativeint directly, skipping typedef chain size_t → unsigned long → unativeint
                let direct = TypeMapper.getFSharpType model baseType
                if direct <> baseType then
                    Named direct
                else
                    // Unknown type: try typedef resolution
                    let resolved = resolveType typedefMap baseType
                    if resolved.Contains("(*)") then Named "nativeint"
                    else
                    match resolved with
                    | ParsedCType resolvedInfo ->
                        resolvedInfo |> mapTypeInfo (fun resolvedBase ->
                            Named (TypeMapper.getFSharpType model resolvedBase))
                    | _ -> Named (TypeMapper.getFSharpType model resolved))
        | _ -> Named (TypeMapper.getFSharpType model cType)

    // =========================================================================
    // Declaration Generation Helpers (produce FsDecl, not strings)
    // =========================================================================

    /// Format XML doc declarations: description (from header comment) + C signature.
    let formatDocDecls (func: CppParser.FunctionDecl) : FsDecl list =
        let paramStr =
            func.Parameters
            |> List.map (fun (name, typ) -> $"{typ} {name}")
            |> String.concat ", "
        let cSignature = $"C signature: {func.ReturnType} {func.Name}({paramStr})"
        match func.Documentation with
        | Some doc ->
            [ XmlDoc doc
              XmlDoc ""
              XmlDoc cSignature ]
        | None ->
            [ XmlDoc cSignature ]

    /// Wrap an FsType in option<> for nullable pointer parameters.
    let wrapOption (ty: FsType) : FsType = Generic("option", ty)

    /// Check if a C type string represents a data pointer (not a function pointer).
    /// Function pointers contain "(*)" and are excluded — they map to nativeint
    /// and have different nullability semantics (use Option<FnPtr<'F>> instead).
    let isCDataPointer (cType: string) : bool =
        cType.Contains("*") && not (cType.Contains("(*)") || cType.Contains("(**)"))

    /// Generate FsDecl list for a single function binding.
    /// Pointer parameters are nullable (Option<>) by default unless proven non-null via:
    /// 1. Clang NonNullAttr — parameter indices explicitly marked non-null
    /// 2. Pilot TOML [annotations.nonnull] — developer-asserted non-null
    let private generateFunctionDecls (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (libraryName: string) (nonnullAnnotations: NonnullAnnotations option) (func: CppParser.FunctionDecl) : FsDecl list =
        let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles Set.empty

        // Collect proven-nonnull parameter indices from clang attributes
        let clangNonnull =
            func.Attributes
            |> List.collect (fun a -> if a.Kind = "NonNullAttr" then a.Args else [])
            |> Set.ofList
        // Collect proven-nonnull parameter indices from pilot TOML
        let tomlNonnull =
            nonnullAnnotations
            |> Option.bind (fun a -> Map.tryFind func.Name a.Parameters)
            |> Option.defaultValue []
            |> Set.ofList
        let nonnullIndices = Set.union clangNonnull tomlNonnull

        // Map parameters with nullability awareness
        let parameters =
            func.Parameters
            |> List.mapi (fun idx (name, cType) ->
                let fsType = mapType cType
                let isNullable = isCDataPointer cType && not (nonnullIndices.Contains idx)
                let finalType = if isNullable then wrapOption fsType else fsType
                { FsParam.Name = cleanParamName name; Type = finalType })

        // Return type: nullable unless proven nonnull
        let returnIsPointer = isCDataPointer func.ReturnType
        let returnNonnull =
            nonnullAnnotations
            |> Option.map (fun a -> a.Returns.Contains func.Name)
            |> Option.defaultValue false
        let hasReturnsNonnullAttr =
            func.Attributes |> List.exists (fun a -> a.Kind = "ReturnsNonNullAttr")
        let returnType = mapType func.ReturnType
        let finalReturnType =
            if returnIsPointer && not returnNonnull && not hasReturnsNonnullAttr
            then wrapOption returnType
            else returnType

        formatDocDecls func @
        [
            LetBinding(func.Name, parameters, finalReturnType, NativeZeroed,
                      [$"FidelityExtern(\"{libraryName}\", \"{func.Name}\")"])
        ]

    /// Generate FsDecl list for an enum type.
    /// Automatically detects bitmask (flags) enums via value pattern analysis.
    let private generateEnumDecl (e: CppParser.EnumDecl) : FsDecl list =
        let values = e.Values |> List.map (fun v -> (v.Name, v.Value))
        let isFlags = ActivePatterns.isBitmaskEnum values
        [ EnumType(e.Name, values, e.Documentation, isFlags) ]

    /// Generate FsDecl list for a struct type (as F# record).
    let private generateStructDecl (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (delegateNames: Set<string>) (s: CppParser.StructDecl) : FsDecl list =
        let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles delegateNames
        let fields = s.Fields |> List.map (fun f -> (f.Name, mapType f.Type))
        [ RecordType(s.Name, fields, s.Documentation, []) ]

    /// Generate FsDecl list for an ABI-critical struct with explicit layout.
    /// Uses StructLayoutInfo (from clang -fdump-record-layouts-simple) for byte offsets.
    let private generateExplicitStructDecl
        (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (delegateNames: Set<string>)
        (layoutInfo: CppParser.StructLayoutInfo) (s: CppParser.StructDecl) : FsDecl list =
        let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles delegateNames
        let fields =
            List.zip s.Fields layoutInfo.FieldOffsetsBits
            |> List.map (fun (f, offsetBits) ->
                { CodeAST.ExplicitField.Name = f.Name
                  Type = mapType f.Type
                  OffsetBytes = offsetBits / 8 })
        [ ExplicitLayoutRecord(s.Name, fields, layoutInfo.SizeBits / 8, s.Documentation) ]

    /// Generate FsDecl list for a macro constant (numeric values only).
    /// Uses CompilerBuiltin/InternalMacro/UserMacro active patterns for classification
    /// and IntegerLiteral active pattern for numeric parsing.
    let private generateMacroDeclIfNumeric (m: CppParser.MacroDecl) : FsDecl list =
        match m.Name with
        | CompilerBuiltin | InternalMacro | PredefinedMacro -> []
        | UserMacro ->
            // Also filter names starting with underscore (matches existing broad filter)
            if m.Name.StartsWith("_") then []
            else
                match m.Kind with
                | CppParser.SimpleValue v ->
                    match v with
                    | IntegerLiteral n -> [ LiteralBinding(m.Name, string n) ]
                    | _ -> []
                | CppParser.Expression v ->
                    let trimmed = v.Trim().TrimStart('(').TrimEnd(')')
                    match trimmed with
                    | IntegerLiteral n -> [ LiteralBinding(m.Name, string n) ]
                    | _ -> []
                | _ -> []

    // =========================================================================
    // C++ Class Binding Generation
    // =========================================================================

    /// Helper: build a FidelityExtern attribute string for a library and symbol.
    let private fidelityExternAttr (lib: string) (symbol: string) : string =
        "FidelityExtern(\"" + lib + "\", \"" + symbol + "\")"

    /// Helper: build a CppPimpl attribute string.
    let private cppPimplAttr (lib: string) (size: int) : string =
        "CppPimpl(\"" + lib + "\", " + string size + ")"

    /// Helper: build a CppValue attribute string.
    let private cppValueAttr (lib: string) (size: int) : string =
        "CppValue(\"" + lib + "\", " + string size + ")"

    /// Helper: sanitize C++ class name for use as F# identifier.
    let private safeClassName (name: string) : string =
        name.Replace("::", "_")

    /// Generate FidelityExtern declarations for a pimpl class method, accounting
    /// for sret return convention when the return type is a non-trivially-copyable class.
    ///
    /// For SretReturn: the hidden sret pointer becomes the first parameter (rdi),
    /// shifting this-pointer to rsi and all other args by one register.
    /// For RegisterReturn: no sret; this-pointer is in rdi as normal.
    let private generateCppMethodDecl
        (libraryName: string)
        (className: string)
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (method: CppParser.FunctionDecl)
        : FsDecl list =
        let returnInfo = CppClassAnalysis.analyzeMethodReturn knownClasses method

        let thisParam = { FsParam.Name = "this"; Type = Named "nativeint" }

        // Map C++ parameter types to Fidelity types (conservative: all pointers as nativeint)
        let methodParams =
            method.Parameters
            |> List.map (fun (name, _cType) ->
                { FsParam.Name = cleanParamName name; Type = Named "nativeint" })

        let sretComment, allParams, returnType =
            match returnInfo.ReturnConvention with
            | CppClassAnalysis.SretReturn ->
                let sretParam = { FsParam.Name = "retStorage"; Type = Named "nativeint" }
                let comment = "sret: caller provides return storage for " + returnInfo.ReturnType
                (comment, sretParam :: thisParam :: methodParams, Unit)
            | CppClassAnalysis.RegisterReturn ->
                let retType =
                    if method.ReturnType = "void" then Unit
                    else Named "nativeint"
                ("", thisParam :: methodParams, retType)

        // Use mangled symbol from clang AST when available; fall back to method name
        // for C functions or if clang did not emit mangledName.
        let mangledName = method.MangledName |> Option.defaultValue method.Name
        let safe = safeClassName className
        let bindingName = safe + "_" + method.Name

        let docLines =
            [ if method.Documentation.IsSome then
                XmlDoc method.Documentation.Value
              XmlDoc ("C++ method: " + className + "::" + method.Name)
              if sretComment <> "" then
                XmlDoc sretComment ]

        docLines @
        [ LetBinding(
            bindingName,
            allParams,
            returnType,
            NativeZeroed,
            [fidelityExternAttr libraryName mangledName])
        ]

    /// Generate FidelityExtern declarations for pimpl constructor(s).
    /// Constructor mangled symbols use C1 (complete object constructor).
    let private generateCppConstructorDecls
        (libraryName: string)
        (className: string)
        (ctors: CppParser.Declaration list)
        : FsDecl list =
        let safe = safeClassName className
        ctors
        |> List.choose (function
            | CppParser.Declaration.Function f -> Some f
            | _ -> None)
        |> List.mapi (fun idx ctor ->
            let suffix = if idx = 0 then "" else "_" + string idx
            let thisParam = { FsParam.Name = "this"; Type = Named "nativeint" }
            let ctorParams =
                ctor.Parameters
                |> List.map (fun (name, _) ->
                    { FsParam.Name = cleanParamName name; Type = Named "nativeint" })
            let mangledName = ctor.MangledName |> Option.defaultValue ctor.Name
            let paramSig = ctor.Parameters |> List.map snd |> String.concat ", "

            [ XmlDoc ("C++ constructor: " + className + "(" + paramSig + ")")
              LetBinding(
                safe + "_ctor" + suffix,
                thisParam :: ctorParams,
                Unit,
                NativeZeroed,
                [fidelityExternAttr libraryName mangledName])
            ])
        |> List.concat

    /// Generate the complete binding set for a pimpl-pattern C++ class.
    /// Produces: type struct (16 bytes), constructor externs, destructor extern,
    /// method externs (with sret awareness), and a companion module.
    let private generatePimplBindings
        (libraryName: string)
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (cls: CppParser.ClassDecl)
        (size: int)
        : FsDecl list =
        let safeName = safeClassName cls.Name

        // Opaque struct type (16 bytes for shared_ptr pimpl)
        let structDecl =
            RecordType(safeName, [("Handle", Named "nativeint")], cls.Documentation,
                      [cppPimplAttr libraryName size; "Struct"])

        // Constructors
        let ctorDecls = generateCppConstructorDecls libraryName cls.Name cls.Constructors

        // Destructor (D1 = complete object destructor)
        let dtorDecls =
            if cls.HasUserDestructor then
                let shortName = cls.Name.Split("::") |> Array.last
                let dtorDoc = "C++ destructor: " + cls.Name + "::~" + shortName + "()"
                // Use mangled symbol from clang AST when available; fall back to
                // fabricated name if the AST did not emit mangledName.
                let dtorSymbol =
                    cls.DestructorMangledName |> Option.defaultValue (safeName + "_D1Ev")
                [ XmlDoc dtorDoc
                  LetBinding(
                    safeName + "_dtor",
                    [{ FsParam.Name = "this"; Type = Named "nativeint" }],
                    Unit,
                    NativeZeroed,
                    [fidelityExternAttr libraryName dtorSymbol]) ]
            else []

        // Methods (with sret analysis, excluding deprecated)
        let methodDecls =
            cls.Methods
            |> List.choose (function
                | CppParser.Declaration.Function f -> Some f
                | _ -> None)
            |> List.filter (fun f ->
                not (f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")))
            |> List.collect (generateCppMethodDecl libraryName cls.Name knownClasses)

        [ Comment ("// C++ class: " + cls.Name + " (pimpl, " + string size + " bytes)")
          BlankLine
          structDecl
          BlankLine ] @
        ctorDecls @
        (if ctorDecls.IsEmpty then [] else [BlankLine]) @
        dtorDecls @
        (if dtorDecls.IsEmpty then [] else [BlankLine]) @
        methodDecls @
        [ BlankLine ]

    /// Generate bindings for a POD (trivially copyable) C++ class.
    /// POD classes are mapped to explicit-layout structs with no destructor obligation.
    let private generatePodBindings
        (cls: CppParser.ClassDecl)
        (size: int)
        : FsDecl list =
        let safeName = safeClassName cls.Name
        // For POD types, generate a fixed-size struct with field mappings
        let fields = cls.Fields |> List.map (fun f -> (f.Name, Named "nativeint"))
        let comment = "// C++ POD type: " + cls.Name + " (" + string size + " bytes)"
        if fields.IsEmpty then
            // Opaque POD with known size but no visible fields
            let byteTypeName = "byte_" + string size
            [ Comment comment
              RecordType(safeName, [("Bytes", Generic("InlineArray", Named byteTypeName))],
                        cls.Documentation, ["Struct"])
              BlankLine ]
        else
            [ Comment comment
              RecordType(safeName, fields, cls.Documentation, ["Struct"])
              BlankLine ]

    /// Generate bindings for a value class (non-trivial but not pimpl).
    /// These have sret implications for return convention.
    let private generateValueClassBindings
        (libraryName: string)
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (cls: CppParser.ClassDecl)
        (size: int)
        (rc: CppClassAnalysis.ReturnConvention)
        : FsDecl list =
        let safeName = safeClassName cls.Name
        let rcComment =
            match rc with
            | CppClassAnalysis.SretReturn -> "sret (non-trivially copyable)"
            | CppClassAnalysis.RegisterReturn -> "register return"

        let comment = "// C++ value type: " + cls.Name + " (" + string size + " bytes, " + rcComment + ")"

        let ctorDecls = generateCppConstructorDecls libraryName cls.Name cls.Constructors

        // Destructor (D1 = complete object destructor)
        // Value classes are non-trivial by definition (user dtor, copy ctor, or move ctor),
        // so they need explicit destructor bindings.
        let dtorDecls =
            if cls.HasUserDestructor then
                let shortName = cls.Name.Split("::") |> Array.last
                let dtorDoc = "C++ destructor: " + cls.Name + "::~" + shortName + "()"
                let dtorSymbol =
                    cls.DestructorMangledName |> Option.defaultValue (safeName + "_D1Ev")
                [ XmlDoc dtorDoc
                  LetBinding(
                    safeName + "_dtor",
                    [{ FsParam.Name = "this"; Type = Named "nativeint" }],
                    Unit,
                    NativeZeroed,
                    [fidelityExternAttr libraryName dtorSymbol]) ]
            else []

        let methodDecls =
            cls.Methods
            |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
            |> List.filter (fun f ->
                not (f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")))
            |> List.collect (generateCppMethodDecl libraryName cls.Name knownClasses)

        [ Comment comment
          BlankLine
          RecordType(safeName, [("Handle", Named "nativeint")], cls.Documentation,
                    [cppValueAttr libraryName size; "Struct"])
          BlankLine ] @
        ctorDecls @
        (if ctorDecls.IsEmpty then [] else [BlankLine]) @
        dtorDecls @
        (if dtorDecls.IsEmpty then [] else [BlankLine]) @
        methodDecls @
        [ BlankLine ]

    // =========================================================================
    // Catamorphism-Based Generation
    // =========================================================================

    /// Declaration group: produced by catamorphism, consumed by assembler.
    type private DeclGroup =
        | GEnum of FsDecl list
        | GStruct of FsDecl list
        | GFunc of CppParser.FunctionDecl
        | GMacro of FsDecl list
        | GCppClassBindings of FsDecl list
        | GNone

    /// Generation algebra: maps each Declaration variant to a DeclGroup.
    /// This is the SINGLE traversal of declarations for code generation.
    /// All context (typedef map, ABI model, opaque handles, struct layouts, C++ class map) is captured in the closure.
    let private generationAlgebra
        (typedefMap: Map<string, string>) (model: PlatformABI)
        (opaqueHandles: Set<string>) (delegateNames: Set<string>) (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        (libraryName: string) (knownClasses: Map<string, CppParser.ClassDecl>)
        : DeclarationAlgebra.DeclarationAlgebra<DeclGroup> = {
        OnEnum = fun e -> if e.Name <> "" then GEnum (generateEnumDecl e) else GNone
        OnStruct = fun s ->
            if s.Name = "" then GNone
            elif s.Fields.IsEmpty then GNone  // Fieldless structs are opaque — no record type (empty records are invalid syntax)
            else
                match Map.tryFind s.Name structLayouts with
                | Some layout -> GStruct (generateExplicitStructDecl typedefMap model opaqueHandles delegateNames layout s)
                | None -> GStruct (generateStructDecl typedefMap model opaqueHandles delegateNames s)
        OnFunction = fun f ->
            // Skip functions marked with __attribute__((deprecated))
            let isDeprecated = f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")
            if isDeprecated then GNone else GFunc f
        OnMacro = fun m ->
            let decls = generateMacroDeclIfNumeric m
            if decls.IsEmpty then GNone else GMacro decls
        OnTypedef = fun _ -> GNone
        OnNamespace = fun _ -> GNone
        OnClass = fun c ->
            if c.Name = "" then GNone
            else
                match CppClassAnalysis.classifyClass c with
                | CppClassAnalysis.PimplClass(_, size) ->
                    GCppClassBindings (generatePimplBindings libraryName knownClasses c size)
                | CppClassAnalysis.PODClass size ->
                    GCppClassBindings (generatePodBindings c size)
                | CppClassAnalysis.ValueClass(size, rc) ->
                    GCppClassBindings (generateValueClassBindings libraryName knownClasses c size rc)
                | CppClassAnalysis.InterfaceClass ->
                    GNone  // Abstract classes cannot be bound directly
                | CppClassAnalysis.OpaqueClass ->
                    GNone  // No visible structure; requires developer intervention
        OnDelegate = fun _ -> GNone // Delegates are CLR constructs; callback fields use nativeint at ABI level
    }

    // =========================================================================
    // Generation Context (pre-computed from full declaration list)
    // =========================================================================

    /// Pre-computed resolution context built once from the full declaration list.
    /// Passed to generateModule so each sub-file gets correct type resolution
    /// even when it only contains a subset of declarations.
    type GenerationContext = {
        TypedefMap: Map<string, string>
        OpaqueHandles: Set<string>
        /// Names of delegate types (callback function pointers) — mapped to nativeint in struct fields.
        /// CCS doesn't support CLR delegates; these are raw function pointers at the ABI level.
        DelegateNames: Set<string>
        DataModel: PlatformABI
        StructLayouts: Map<string, CppParser.StructLayoutInfo>
        /// Nonnull annotations from pilot TOML (None = all pointers nullable by default)
        NonnullAnnotations: NonnullAnnotations option
        /// C++ class lookup map for sret return type analysis (empty for C-only libraries)
        KnownClasses: Map<string, CppParser.ClassDecl>
    }

    /// Build a GenerationContext from the full, unfiltered declaration list.
    let buildGenerationContext
        (declarations: CppParser.Declaration list)
        (model: PlatformABI)
        (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        : GenerationContext =
        let delegateNames =
            declarations |> List.choose (function
                | CppParser.Declaration.Delegate d -> Some d.Name
                | _ -> None)
            |> Set.ofList
        { TypedefMap = buildTypedefMap declarations
          OpaqueHandles = detectOpaqueHandles declarations
          DelegateNames = delegateNames
          DataModel = model
          StructLayouts = structLayouts
          NonnullAnnotations = None
          KnownClasses = CppClassAnalysis.buildClassMap declarations }

    /// Generate a Clef module with explicit control over what gets emitted.
    /// Uses the full GenerationContext for type resolution, but only declares
    /// the opaque handles in handlesToDeclare and the declarations passed in.
    /// openModules are emitted as `open` directives after the module header.
    let generateModule
        (ctx: GenerationContext)
        (handlesToDeclare: Set<string>)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (libraryName: string)
        (comment: string)
        (openModules: string list)
        : string =

        let opaqueHandleDecls = generateOpaqueHandleDecls handlesToDeclare

        let groups =
            DeclarationAlgebra.cataDeclarations
                (generationAlgebra ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.DelegateNames ctx.StructLayouts libraryName ctx.KnownClasses)
                declarations

        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let structs = groups |> List.collect (function GStruct d -> d | _ -> [])
        let functions =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateFunctionDecls ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles libraryName ctx.NonnullAnnotations)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])
        let cppClasses = groups |> List.collect (function GCppClassBindings d -> d | _ -> [])

        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]

        let cppSection =
            if cppClasses.IsEmpty then []
            else Comment "// C++ class bindings" :: BlankLine :: cppClasses

        let openDecls = openModules |> List.map OpenModule
        let allDecls = openDecls @ opaqueHandleDecls @ enums @ structs @ functions @ macroSection @ cppSection
        let moduleDecl = Module(namespace', comment, allDecls)

        CodeRenderer.render moduleDecl

    // =========================================================================
    // Original API (backward compatible, used for single-file generation)
    // =========================================================================

    /// Generate a complete Fidelity binding source file from parsed declarations.
    /// Architecture: Pre-passes build context → Algebra captures context in closure → Catamorphism → FsDecl tree → Render
    /// PlatformABI determines concrete widths for C int/long in NTU output.
    /// structLayouts: pre-computed layout data for ABI-critical structs (empty for normal generation).
    let generate
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (libraryName: string)
        (model: PlatformABI)
        (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        : string =
        let ctx = buildGenerationContext declarations model structLayouts
        generateModule ctx ctx.OpaqueHandles declarations namespace' libraryName $"Fidelity binding for {libraryName}" []
