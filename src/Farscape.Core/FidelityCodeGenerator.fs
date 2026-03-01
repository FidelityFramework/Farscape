namespace Farscape.Core

open CodeAST
open ActivePatterns
open Types

/// Generates F# source in the Platform.Bindings pattern for Fidelity/Firefly consumption.
///
/// Output is a single .fs file with:
///   module Platform.Bindings.{Library}.{Category}
///   [<FidelityExtern("library", "symbol")>]
///   let functionName (param1: type1) (param2: type2) : returnType =
///       Unchecked.defaultof<returnType>
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
    let mapCTypeToFidelityType (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (cType: string) : FsType =
        // Opaque handle types preserve their wrapper struct name
        if opaqueHandles.Contains(cType) then Named cType
        // Function pointer types like "void (*)(void)" contain (*); always nativeint
        elif cType.Contains("(*)") then Named "nativeint"
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

    /// Generate FsDecl list for a single function binding.
    let private generateFunctionDecls (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (libraryName: string) (func: CppParser.FunctionDecl) : FsDecl list =
        let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles
        let returnType = mapType func.ReturnType
        let parameters =
            func.Parameters
            |> List.map (fun (name, cType) ->
                { FsParam.Name = cleanParamName name; Type = mapType cType })
        formatDocDecls func @
        [
            LetBinding(func.Name, parameters, returnType, DefaultOf returnType,
                      [$"FidelityExtern(\"{libraryName}\", \"{func.Name}\")"])
        ]

    /// Generate FsDecl list for an enum type.
    /// Automatically detects bitmask (flags) enums via value pattern analysis.
    let private generateEnumDecl (e: CppParser.EnumDecl) : FsDecl list =
        let values = e.Values |> List.map (fun v -> (v.Name, v.Value))
        let isFlags = ActivePatterns.isBitmaskEnum values
        [ EnumType(e.Name, values, e.Documentation, isFlags) ]

    /// Generate FsDecl list for a struct type (as F# record).
    let private generateStructDecl (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (s: CppParser.StructDecl) : FsDecl list =
        let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles
        let fields = s.Fields |> List.map (fun f -> (f.Name, mapType f.Type))
        [ RecordType(s.Name, fields, s.Documentation, []) ]

    /// Generate FsDecl list for an ABI-critical struct with explicit layout.
    /// Uses StructLayoutInfo (from clang -fdump-record-layouts-simple) for byte offsets.
    let private generateExplicitStructDecl
        (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>)
        (layoutInfo: CppParser.StructLayoutInfo) (s: CppParser.StructDecl) : FsDecl list =
        let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles
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
        | GDelegate of FsDecl list
        | GNone

    /// Generation algebra: maps each Declaration variant to a DeclGroup.
    /// This is the SINGLE traversal of declarations for code generation.
    /// All context (typedef map, ABI model, opaque handles, struct layouts) is captured in the closure.
    let private generationAlgebra
        (typedefMap: Map<string, string>) (model: PlatformABI)
        (opaqueHandles: Set<string>) (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        : DeclarationAlgebra.DeclarationAlgebra<DeclGroup> = {
        OnEnum = fun e -> if e.Name <> "" then GEnum (generateEnumDecl e) else GNone
        OnStruct = fun s ->
            if s.Name = "" then GNone
            else
                match Map.tryFind s.Name structLayouts with
                | Some layout -> GStruct (generateExplicitStructDecl typedefMap model opaqueHandles layout s)
                | None -> GStruct (generateStructDecl typedefMap model opaqueHandles s)
        OnFunction = fun f -> GFunc f
        OnMacro = fun m ->
            let decls = generateMacroDeclIfNumeric m
            if decls.IsEmpty then GNone else GMacro decls
        OnTypedef = fun _ -> GNone
        OnNamespace = fun _ -> GNone
        OnClass = fun _ -> GNone
        OnDelegate = fun d ->
            let mapType = mapCTypeToFidelityType typedefMap model opaqueHandles
            let params' = d.Parameters |> List.map (fun (name, cType) -> (name, mapType cType))
            let retType = mapType d.ReturnType
            GDelegate [ DelegateType(d.Name, params', retType, d.Documentation) ]
    }

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

        // Phase 1: Build typedef resolution map (catamorphism + pure recursion)
        let typedefMap = buildTypedefMap declarations

        // Phase 1.5: Detect opaque handle typedefs (pre-pass)
        let opaqueHandles = detectOpaqueHandles declarations
        let opaqueHandleDecls = generateOpaqueHandleDecls opaqueHandles

        // Phase 2: Categorize declarations via catamorphism (ONE pass)
        // All context (typedef map, model, opaque handles, struct layouts) captured in algebra closure
        let groups =
            DeclarationAlgebra.cataDeclarations (generationAlgebra typedefMap model opaqueHandles structLayouts) declarations

        // Phase 3: Assemble categories (pure functional fold)
        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let delegates = groups |> List.collect (function GDelegate d -> d | _ -> [])
        let structs = groups |> List.collect (function GStruct d -> d | _ -> [])
        let functions =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateFunctionDecls typedefMap model opaqueHandles libraryName)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])

        // Phase 4: Build typed FsDecl tree
        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]

        let allDecls = opaqueHandleDecls @ enums @ delegates @ structs @ functions @ macroSection
        let moduleDecl = Module(namespace', $"Fidelity binding for {libraryName}", allDecls)

        // Phase 5: Render to string (the ONLY StringBuilder, in CodeRenderer)
        CodeRenderer.render moduleDecl
