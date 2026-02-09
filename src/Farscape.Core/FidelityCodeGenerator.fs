namespace Farscape.Core

open System.Text

/// Generates F# source in the Platform.Bindings pattern for Fidelity/Firefly consumption.
///
/// Output is a single .fs file with:
///   module Platform.Bindings.{Library}.{Category}
///   let functionName (param1: type1) (param2: type2) : returnType =
///       Unchecked.defaultof<returnType>
///
/// Alex recognizes this pattern and provides platform-specific MLIR implementations.
/// No DllImport, no Marshal, no BCL dependencies.
module FidelityCodeGenerator =

    // =========================================================================
    // Typedef Resolution
    // =========================================================================

    /// Build a typedef resolution map from parsed declarations.
    /// Follows chains: __off_t → long int, __pid_t → int, etc.
    let buildTypedefMap (declarations: CppParser.Declaration list) : Map<string, string> =
        let typedefs =
            declarations
            |> List.choose (function
                | CppParser.Declaration.Typedef t -> Some (t.Name, t.UnderlyingType)
                | _ -> None)

        let mutable resolvedMap = Map.ofList typedefs

        // Resolve chains: if __off_t → __OFF_T_TYPE and __OFF_T_TYPE → long int,
        // resolve __off_t → long int. Max 10 iterations to prevent infinite loops.
        let mutable changed = true
        let mutable iterations = 0
        while changed && iterations < 10 do
            changed <- false
            iterations <- iterations + 1
            resolvedMap <-
                resolvedMap
                |> Map.map (fun _name underlyingType ->
                    match Map.tryFind underlyingType resolvedMap with
                    | Some deeper ->
                        changed <- true
                        deeper
                    | None -> underlyingType)

        resolvedMap

    /// Resolve a C type through the typedef map, stripping qualifiers and normalizing.
    let resolveType (typedefMap: Map<string, string>) (cType: string) : string =
        let cleaned = TypeMapper.cleanTypeName cType
        match Map.tryFind cleaned typedefMap with
        | Some resolved -> resolved
        | None -> cType

    // =========================================================================
    // Fidelity-Specific Type Mapping
    // =========================================================================

    /// Map a C type string to an F# type suitable for Fidelity native compilation.
    /// Key differences from the P/Invoke mapper:
    ///   - char* → nativeptr<byte> (byte buffer, not .NET string)
    ///   - void* → nativeint (opaque pointer)
    ///   - All other pointers → nativeint
    ///   - No MarshalAs concerns
    let mapCTypeToFidelity (typedefMap: Map<string, string>) (cType: string) : string =
        // Strip qualifiers that don't affect binding signatures
        let stripped =
            cType
                .Replace("__restrict", "")
                .Replace("restrict", "")
                .Replace("volatile ", "")
                .Replace(" volatile", "")
                .Replace("__extension__ ", "")
                .Trim()

        // Normalize whitespace around pointer star: "char *" → "char*", "void *" → "void*"
        let normalized =
            System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+\*", "*")

        let isPointer = TypeMapper.isPointerType normalized

        if isPointer then
            // Buffer pointers: char*, unsigned char*, void* variants
            if normalized.Contains("char*") then "nativeptr<byte>"
            elif normalized.Contains("void*") then "nativeint"
            else "nativeint"
        else
            // Resolve through typedef chain first, then map to F#
            let resolved = resolveType typedefMap normalized
            // Normalize the resolved type too (it might have "long int" etc.)
            let resolvedNormalized =
                System.Text.RegularExpressions.Regex.Replace(resolved, @"\s+\*", "*")
            if TypeMapper.isPointerType resolvedNormalized then
                if resolvedNormalized.Contains("char*") then "nativeptr<byte>"
                elif resolvedNormalized.Contains("void*") then "nativeint"
                else "nativeint"
            else
                TypeMapper.getFSharpType resolvedNormalized

    // =========================================================================
    // Code Generation
    // =========================================================================

    /// F# keywords that need backtick quoting when used as parameter names
    let private fsharpKeywords =
        set [
            "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"
            "delegate"; "do"; "done"; "downcast"; "downto"; "elif"; "else"; "end"
            "exception"; "extern"; "false"; "finally"; "fixed"; "for"; "fun"; "function"
            "global"; "if"; "in"; "inherit"; "inline"; "interface"; "internal"; "lazy"
            "let"; "match"; "member"; "module"; "mutable"; "namespace"; "new"; "not"
            "null"; "of"; "open"; "or"; "override"; "private"; "public"; "rec"; "return"
            "select"; "static"; "struct"; "then"; "to"; "true"; "try"; "type"; "upcast"
            "use"; "val"; "void"; "when"; "while"; "with"; "yield"
        ]

    /// Strip leading underscores from parameter names and quote F# keywords.
    /// "__fd" → "fd", "__type" → "``type``"
    let private cleanParamName (name: string) : string =
        let stripped = if name.StartsWith("__") then name.TrimStart('_') else name
        if fsharpKeywords.Contains(stripped) then $"``{stripped}``"
        else stripped

    /// Compiler-predefined macros that are not header-specific
    let private predefinedMacros = set [ "linux"; "unix"; "i386"; "i686"; "true"; "false" ]

    /// Format the original C signature as an XML doc comment
    let private formatDocComment (func: CppParser.FunctionDecl) : string =
        let paramStr =
            func.Parameters
            |> List.map (fun (name, typ) -> $"{typ} {name}")
            |> String.concat ", "
        $"    /// {func.ReturnType} {func.Name}({paramStr})"

    /// Generate a single binding function in curried F# style
    let private generateFunction (typedefMap: Map<string, string>) (func: CppParser.FunctionDecl) : string =
        let sb = StringBuilder()

        // XML doc with original C signature
        sb.AppendLine(formatDocComment func) |> ignore

        // Function signature: let name (p1: t1) (p2: t2) : retType =
        let mapType = mapCTypeToFidelity typedefMap
        let returnType = mapType func.ReturnType
        let parameters =
            func.Parameters
            |> List.map (fun (name, cType) ->
                let fType = mapType cType
                let cleanName = cleanParamName name
                $"({cleanName}: {fType})")

        let paramStr =
            if parameters.IsEmpty then "()"
            else parameters |> String.concat " "

        sb.AppendLine($"    let {func.Name} {paramStr} : {returnType} =") |> ignore
        sb.Append($"        Unchecked.defaultof<{returnType}>") |> ignore

        sb.ToString()

    /// Generate a complete Fidelity binding source file from parsed declarations.
    /// Returns the F# source as a string.
    let generate
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (libraryName: string)
        : string =

        let sb = StringBuilder()

        // Build typedef resolution map from parsed declarations
        let typedefMap = buildTypedefMap declarations
        let mapType = mapCTypeToFidelity typedefMap

        // Module declaration
        sb.AppendLine($"module {namespace'}") |> ignore
        sb.AppendLine() |> ignore

        // Header comment
        sb.AppendLine($"// Generated by Farscape — Fidelity binding for {libraryName}") |> ignore
        sb.AppendLine("// Alex provides platform-specific MLIR implementations for these bindings.") |> ignore
        sb.AppendLine() |> ignore

        // Extract functions from declarations (deduplicated by name)
        let functions =
            declarations
            |> List.choose (function
                | CppParser.Declaration.Function f -> Some f
                | _ -> None)
            |> List.distinctBy (fun f -> f.Name)

        // Extract enums → F# discriminated unions
        let enums =
            declarations
            |> List.choose (function
                | CppParser.Declaration.Enum e when e.Name <> "" -> Some e
                | _ -> None)

        // Extract structs → F# records
        let structs =
            declarations
            |> List.choose (function
                | CppParser.Declaration.Struct s when s.Name <> "" -> Some s
                | _ -> None)

        // Extract macros → F# [<Literal>] constants (numeric SimpleValue and Expression only)
        let macros =
            declarations
            |> List.choose (function
                | CppParser.Declaration.Macro m ->
                    // Skip compiler builtins, internal macros, and predefined names
                    if m.Name.StartsWith("__") || m.Name.StartsWith("_") || predefinedMacros.Contains(m.Name) then None
                    else
                        match m.Kind with
                        | CppParser.SimpleValue v ->
                            // Only emit if the value is a simple integer
                            match System.Int64.TryParse(v) with
                            | true, _ -> Some (m.Name, v)
                            | _ ->
                                // Try hex: 0x...
                                if v.StartsWith("0x") || v.StartsWith("0X") then
                                    match System.Int64.TryParse(v.Substring(2), System.Globalization.NumberStyles.HexNumber, null) with
                                    | true, n -> Some (m.Name, string n)
                                    | _ -> None
                                else None
                        | CppParser.Expression v ->
                            // Only emit if the expression resolves to a simple integer
                            let trimmed = v.Trim().TrimStart('(').TrimEnd(')')
                            match System.Int64.TryParse(trimmed) with
                            | true, _ -> Some (m.Name, trimmed)
                            | _ -> None
                        | _ -> None
                | _ -> None)

        // Emit enum types
        for enum in enums do
            match enum.Documentation with
            | Some doc -> sb.AppendLine($"/// {doc}") |> ignore
            | None -> ()
            sb.AppendLine($"type {enum.Name} =") |> ignore
            for value in enum.Values do
                sb.AppendLine($"    | {value.Name} = {value.Value}L") |> ignore
            sb.AppendLine() |> ignore

        // Emit struct types as F# records
        for struct' in structs do
            match struct'.Documentation with
            | Some doc -> sb.AppendLine($"/// {doc}") |> ignore
            | None -> ()
            sb.AppendLine($"type {struct'.Name} = {{") |> ignore
            for field in struct'.Fields do
                let fType = mapType field.Type
                sb.AppendLine($"    {field.Name}: {fType}") |> ignore
            sb.AppendLine("}") |> ignore
            sb.AppendLine() |> ignore

        // Emit function bindings
        if not functions.IsEmpty then
            for func in functions do
                sb.AppendLine(generateFunction typedefMap func) |> ignore
                sb.AppendLine() |> ignore

        // Emit macro constants as [<Literal>] values
        if not macros.IsEmpty then
            sb.AppendLine("// Macro constants") |> ignore
            for (name, value) in macros do
                sb.AppendLine($"[<Literal>]") |> ignore
                sb.AppendLine($"let {name} = {value}") |> ignore
            sb.AppendLine() |> ignore

        sb.ToString().TrimEnd() + "\n"
