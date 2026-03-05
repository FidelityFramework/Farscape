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
                      RecordConstruction [("Handle", Literal "0n")],
                      [])
                  LetBinding("isNull", [{ Name = "h"; Type = Named name }],
                      Named "bool",
                      Comparison(Identifier "h.Handle", "=", Literal "0n"),
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

    /// Wrap an FsType in Option<> for nullable pointer parameters.
    let wrapOption (ty: FsType) : FsType = Generic("Option", ty)

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
    // Catamorphism-Based Generation
    // =========================================================================

    /// Declaration group: produced by catamorphism, consumed by assembler.
    type private DeclGroup =
        | GEnum of FsDecl list
        | GStruct of FsDecl list
        | GFunc of CppParser.FunctionDecl
        | GMacro of FsDecl list
        | GNone

    /// Generation algebra: maps each Declaration variant to a DeclGroup.
    /// This is the SINGLE traversal of declarations for code generation.
    /// All context (typedef map, ABI model, opaque handles, struct layouts) is captured in the closure.
    let private generationAlgebra
        (typedefMap: Map<string, string>) (model: PlatformABI)
        (opaqueHandles: Set<string>) (delegateNames: Set<string>) (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        : DeclarationAlgebra.DeclarationAlgebra<DeclGroup> = {
        OnEnum = fun e -> if e.Name <> "" then GEnum (generateEnumDecl e) else GNone
        OnStruct = fun s ->
            if s.Name = "" then GNone
            elif s.Fields.IsEmpty then GNone  // Fieldless structs are opaque — no record type (empty records are invalid syntax)
            else
                match Map.tryFind s.Name structLayouts with
                | Some layout -> GStruct (generateExplicitStructDecl typedefMap model opaqueHandles delegateNames layout s)
                | None -> GStruct (generateStructDecl typedefMap model opaqueHandles delegateNames s)
        OnFunction = fun f -> GFunc f
        OnMacro = fun m ->
            let decls = generateMacroDeclIfNumeric m
            if decls.IsEmpty then GNone else GMacro decls
        OnTypedef = fun _ -> GNone
        OnNamespace = fun _ -> GNone
        OnClass = fun _ -> GNone
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
          NonnullAnnotations = None }

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
                (generationAlgebra ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.DelegateNames ctx.StructLayouts)
                declarations

        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let structs = groups |> List.collect (function GStruct d -> d | _ -> [])
        let functions =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateFunctionDecls ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles libraryName ctx.NonnullAnnotations)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])

        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]

        let openDecls = openModules |> List.map OpenModule
        let allDecls = openDecls @ opaqueHandleDecls @ enums @ structs @ functions @ macroSection
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
