namespace Farscape.Core

open CodeAST
open ActivePatterns
open Types

/// Generates F# source with traditional .NET P/Invoke bindings ([<DllImport>] + extern).
///
/// Output is a single .fs file with:
///   module {Namespace}
///   open System.Runtime.InteropServices
///   [<DllImport("library", CallingConvention = CallingConvention.Cdecl)>]
///   extern returnType functionName(type1 p1, type2 p2)
///
/// Uses the same catamorphism pipeline as FidelityCodeGenerator but produces ExternDecl
/// instead of LetBinding, and maps types for CLR marshalling (char* → string).
///
/// Type resolution uses PInvokeTypeMapper (CLR concrete types) instead of TypeMapper
/// (NTU abstract types). The PlatformABI parameter determines platform-specific widths
/// for C int, long, etc.
///
/// Architecture:
///   Catamorphism (DeclarationAlgebra) → FsDecl list → CodeRenderer.render
///   Shares typedef resolution and documentation infrastructure with FidelityCodeGenerator.
module PInvokeCodeGenerator =

    // =========================================================================
    // P/Invoke-Specific Type Mapping (uses PInvokeTypeMapper, NOT TypeMapper)
    // =========================================================================

    /// Map pointer/value type info to FsType for P/Invoke (CLR marshalling).
    /// CharPointer → string (CLR marshals), not nativeptr<byte> like Fidelity.
    let private mapTypeInfoPInvoke (baseTypeFn: string -> FsType) = function
        | CharPointer -> Named "string"
        | VoidPointer -> Named "nativeint"
        | TypedPointer _ -> Named "nativeint"
        | ValueType baseType -> baseTypeFn baseType

    /// Map a C type string to an FsType suitable for .NET P/Invoke marshalling.
    /// Key difference from Fidelity: char* → string (CLR handles marshalling),
    /// whereas Fidelity maps char* → nativeptr<byte> (no marshalling in NTU).
    /// Uses PInvokeTypeMapper with concrete platform-specific widths.
    let private mapCTypeToPInvokeType (typedefMap: Map<string, string>) (model: PlatformABI) (cType: string) : FsType =
        if cType.Contains("(*)") then Named "nativeint"
        else
        match cType with
        | ParsedCType info ->
            info |> mapTypeInfoPInvoke (fun baseType ->
                // Check CLR type dictionary FIRST — preserves platform-abstract types
                let direct = PInvokeTypeMapper.getFSharpType model baseType
                if direct <> baseType then
                    Named direct
                else
                    // Unknown type: try typedef resolution
                    let resolved = FidelityCodeGenerator.resolveType typedefMap baseType
                    if resolved.Contains("(*)") then Named "nativeint"
                    else
                    match resolved with
                    | ParsedCType resolvedInfo ->
                        resolvedInfo |> mapTypeInfoPInvoke (fun resolvedBase ->
                            Named (PInvokeTypeMapper.getFSharpType model resolvedBase))
                    | _ -> Named (PInvokeTypeMapper.getFSharpType model resolved))
        | _ -> Named (PInvokeTypeMapper.getFSharpType model cType)

    // =========================================================================
    // Declaration Generation Helpers (produce FsDecl, not strings)
    // =========================================================================

    /// Generate FsDecl list for a single P/Invoke extern declaration.
    let private generateFunctionDecls (typedefMap: Map<string, string>) (model: PlatformABI) (libraryName: string) (func: CppParser.FunctionDecl) : FsDecl list =
        let mapType = mapCTypeToPInvokeType typedefMap model
        let returnType = mapType func.ReturnType
        let parameters =
            func.Parameters
            |> List.map (fun (name, cType) ->
                { FsParam.Name = cleanParamName name; Type = mapType cType })
        FidelityCodeGenerator.formatDocDecls func @
        [ ExternDecl(func.Name, parameters, returnType, libraryName) ]

    /// Generate FsDecl list for an enum type.
    let private generateEnumDecl (e: CppParser.EnumDecl) : FsDecl list =
        [ EnumType(e.Name, e.Values |> List.map (fun v -> (v.Name, v.Value)), e.Documentation) ]

    /// Generate FsDecl list for a struct type (as F# record with StructLayout).
    let private generateStructDecl (typedefMap: Map<string, string>) (model: PlatformABI) (s: CppParser.StructDecl) : FsDecl list =
        let mapType = mapCTypeToPInvokeType typedefMap model
        let fields = s.Fields |> List.map (fun f -> (f.Name, mapType f.Type))
        [ RecordType(s.Name, fields, s.Documentation, ["Struct"; "StructLayout(LayoutKind.Sequential)"]) ]

    /// Generate FsDecl list for a macro constant (numeric values only).
    let private generateMacroDeclIfNumeric (m: CppParser.MacroDecl) : FsDecl list =
        match m.Name with
        | CompilerBuiltin | InternalMacro | PredefinedMacro -> []
        | UserMacro ->
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
    let private generationAlgebra (typedefMap: Map<string, string>) (model: PlatformABI) : DeclarationAlgebra.DeclarationAlgebra<DeclGroup> = {
        OnEnum = fun e -> if e.Name <> "" then GEnum (generateEnumDecl e) else GNone
        OnStruct = fun s -> if s.Name <> "" then GStruct (generateStructDecl typedefMap model s) else GNone
        OnFunction = fun f -> GFunc f
        OnMacro = fun m ->
            let decls = generateMacroDeclIfNumeric m
            if decls.IsEmpty then GNone else GMacro decls
        OnTypedef = fun _ -> GNone
        OnNamespace = fun _ -> GNone
        OnClass = fun _ -> GNone
    }

    /// Generate a complete P/Invoke binding source file from parsed declarations.
    /// Architecture: Catamorphism → FsDecl tree → CodeRenderer.render
    ///
    /// The PlatformABI parameter determines platform-specific type widths:
    /// LP64 (Linux/macOS), LLP64 (Windows x64), ILP32 (32-bit), IP16 (embedded).
    let generate
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (libraryName: string)
        (model: PlatformABI)
        : string =

        // Phase 1: Build typedef resolution map (catamorphism + pure recursion)
        let typedefMap = FidelityCodeGenerator.buildTypedefMap declarations

        // Phase 2: Categorize declarations via catamorphism (ONE pass)
        let groups =
            DeclarationAlgebra.cataDeclarations (generationAlgebra typedefMap model) declarations

        // Phase 3: Assemble categories (pure functional fold)
        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let structs = groups |> List.collect (function GStruct d -> d | _ -> [])
        let functions =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateFunctionDecls typedefMap model libraryName)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])

        // Phase 4: Build typed FsDecl tree
        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]

        let interopOpen = Comment "open System.Runtime.InteropServices"
        let allDecls = [interopOpen; BlankLine] @ enums @ structs @ functions @ macroSection
        let moduleDecl = Module(namespace', $".NET P/Invoke binding for {libraryName}", allDecls)

        // Phase 5: Render to string (the ONLY StringBuilder, in CodeRenderer)
        CodeRenderer.render moduleDecl
