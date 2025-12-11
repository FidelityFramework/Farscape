namespace Farscape.Core

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// C/C++ header parsing using native clang tooling.
///
/// This module invokes clang directly for two-pass parsing:
/// 1. `clang -Xclang -ast-dump=json` for structs, enums, typedefs, functions
/// 2. `clang -E -dM` for preprocessor macro definitions
///
/// This approach works with any installed clang version without libclang
/// version compatibility issues.
module CppParser =

    // =========================================================================
    // Declaration Types
    // =========================================================================

    /// Represents a struct field with full type information
    type FieldDecl = {
        Name: string
        Type: string
        IsVolatile: bool      // __IO, volatile
        IsConst: bool         // __I, const
        IsArray: bool         // Fixed-size array (e.g., RESERVED[4])
        ArraySize: int option // Size if IsArray
    }

    /// Represents a C/C++ function declaration
    type FunctionDecl = {
        Name: string
        ReturnType: string
        Parameters: (string * string) list
        Documentation: string option
        IsVirtual: bool
        IsStatic: bool
        IsInline: bool
    }

    /// Represents a C/C++ struct declaration
    type StructDecl = {
        Name: string
        Fields: FieldDecl list
        Documentation: string option
        IsUnion: bool
    }

    /// Represents a C/C++ enum value with signed support
    type EnumValue = {
        Name: string
        Value: int64  // Signed to support negative IRQ numbers
        Documentation: string option
    }

    /// Represents a C/C++ enum declaration
    type EnumDecl = {
        Name: string
        Values: EnumValue list
        Documentation: string option
        UnderlyingType: string option  // e.g., "int", "uint32_t"
    }

    /// Represents a C/C++ typedef declaration
    type TypedefInfo = {
        Name: string
        UnderlyingType: string
        Documentation: string option
    }

    /// Represents a C preprocessor macro
    type MacroKind =
        | SimpleValue of string           // #define FOO 42
        | Expression of string            // #define FOO (BAR + 1)
        | FunctionLike of string list * string  // #define FOO(x,y) (x+y)
        | TypeCast of string * string     // #define GPIOA ((GPIO_TypeDef*)0x...)

    type MacroDecl = {
        Name: string
        Kind: MacroKind
        RawValue: string
    }

    /// Union type for all supported declarations
    type Declaration =
        | Function of FunctionDecl
        | Struct of StructDecl
        | Enum of EnumDecl
        | Typedef of TypedefInfo
        | Macro of MacroDecl
        | Namespace of NamespaceDecl
        | Class of ClassDecl

    and NamespaceDecl = {
        Name: string
        Declarations: Declaration list
    }

    and ClassDecl = {
        Name: string
        Methods: Declaration list
        Fields: FieldDecl list
        Documentation: string option
        IsAbstract: bool
    }

    // =========================================================================
    // Parser Options
    // =========================================================================

    /// Options for parsing a C/C++ header file
    type HeaderParserOptions = {
        /// Path to the header file to parse
        HeaderFile: string
        /// Additional include paths for resolving #include directives
        IncludePaths: string list
        /// Preprocessor definitions (e.g., ["DEBUG"; "STM32L552xx"])
        Defines: string list
        /// Enable verbose output
        Verbose: bool
        /// Include macros from preprocessor (can be slow for large headers)
        IncludeMacros: bool
        /// Filter to only include macros matching these prefixes (empty = all)
        MacroPrefixes: string list
    }

    /// Create default parser options for a header file
    let defaultOptions headerFile = {
        HeaderFile = headerFile
        IncludePaths = []
        Defines = []
        Verbose = false
        IncludeMacros = true
        MacroPrefixes = []
    }

    // =========================================================================
    // JSON AST Parsing Helpers
    // =========================================================================

    /// Get string property from JSON element
    let private getString (element: JsonElement) (prop: string) : string option =
        match element.TryGetProperty(prop) with
        | true, value when value.ValueKind = JsonValueKind.String -> Some (value.GetString())
        | _ -> None

    /// Get string property with default value
    let private getStringOr (element: JsonElement) (prop: string) (defaultVal: string) : string =
        getString element prop |> Option.defaultValue defaultVal

    /// Get integer property (signed)
    let private getInt64 (element: JsonElement) (prop: string) : int64 option =
        match element.TryGetProperty(prop) with
        | true, value when value.ValueKind = JsonValueKind.Number -> Some (value.GetInt64())
        | _ -> None

    /// Get boolean property
    let private getBool (element: JsonElement) (prop: string) : bool =
        match element.TryGetProperty(prop) with
        | true, value when value.ValueKind = JsonValueKind.True -> true
        | _ -> false

    /// Get array property
    let private getArray (element: JsonElement) (prop: string) : JsonElement seq =
        match element.TryGetProperty(prop) with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.map id
        | _ -> Seq.empty

    /// Get nested object property
    let private getObject (element: JsonElement) (prop: string) : JsonElement option =
        match element.TryGetProperty(prop) with
        | true, value when value.ValueKind = JsonValueKind.Object -> Some value
        | _ -> None

    /// Check if a declaration is from an included file (vs the main file)
    let private isFromIncludedFile (element: JsonElement) : bool =
        match getObject element "loc" with
        | Some loc ->
            match getObject loc "includedFrom" with
            | Some _ -> true
            | None -> false
        | None -> false

    /// Extract file from location info, tracking spillover from previous declarations
    /// Clang JSON only includes 'file' on the first decl from each file, subsequent ones just have line/offset
    let private getFileFromLoc (element: JsonElement) (lastKnownFile: string option) : string option =
        match getString element "file" with
        | Some f -> Some f
        | None ->
            match getObject element "begin" with
            | Some beginObj ->
                match getString beginObj "file" with
                | Some f -> Some f
                | None ->
                    // If loc has offset but no file, use lastKnownFile
                    match getInt64 element "offset" with
                    | Some _ -> lastKnownFile
                    | None -> lastKnownFile
            | None ->
                // If loc has offset but no file, use lastKnownFile
                match getInt64 element "offset" with
                | Some _ -> lastKnownFile
                | None -> lastKnownFile

    /// Extract type string from type object
    let private getQualType (element: JsonElement) : string =
        match getObject element "type" with
        | Some typeObj -> getStringOr typeObj "qualType" "unknown"
        | None -> "unknown"

    /// Extract return type from function type string (e.g., "void (int)" -> "void")
    let private extractReturnType (typeStr: string) : string =
        match typeStr.IndexOf('(') with
        | -1 -> typeStr.Trim()
        | idx -> typeStr.Substring(0, idx).Trim()

    /// Parse a field type string into structured info
    let private parseFieldType (typeStr: string) : FieldDecl =
        let mutable isVolatile = typeStr.Contains("volatile") || typeStr.Contains("__IO")
        let mutable isConst = typeStr.Contains("const") || typeStr.Contains("__I")
        let mutable isArray = false
        let mutable arraySize = None
        let mutable cleanType = typeStr

        // Check for array syntax: type[N]
        let arrayMatch = Regex.Match(typeStr, @"\[(\d+)\]")
        if arrayMatch.Success then
            isArray <- true
            arraySize <- Some (int arrayMatch.Groups.[1].Value)
            cleanType <- Regex.Replace(typeStr, @"\[\d+\]", "").Trim()

        // Clean up qualifiers for the base type
        cleanType <- cleanType.Replace("volatile ", "").Replace("const ", "")
                              .Replace("__IO ", "").Replace("__I ", "").Replace("__O ", "")
                              .Trim()

        {
            Name = ""
            Type = cleanType
            IsVolatile = isVolatile
            IsConst = isConst
            IsArray = isArray
            ArraySize = arraySize
        }

    // =========================================================================
    // AST Node Processing
    // =========================================================================

    /// Process FunctionDecl AST node
    let private processFunctionDecl (node: JsonElement) : FunctionDecl option =
        match getString node "name" with
        | None | Some "" -> None
        | Some name ->
            let parameters =
                getArray node "inner"
                |> Seq.filter (fun inner ->
                    getStringOr inner "kind" "" = "ParmVarDecl")
                |> Seq.map (fun param ->
                    let paramName = getStringOr param "name" "param"
                    let paramType = getQualType param
                    (paramName, paramType))
                |> List.ofSeq

            let returnType = extractReturnType (getQualType node)
            let isStatic = getStringOr node "storageClass" "" = "static"
            let isInline = getBool node "inline"

            Some {
                Name = name
                ReturnType = returnType
                Parameters = parameters
                Documentation = None
                IsVirtual = false
                IsStatic = isStatic
                IsInline = isInline
            }

    /// Process FieldDecl AST node
    let private processFieldDecl (node: JsonElement) : FieldDecl option =
        match getString node "name" with
        | None | Some "" -> None
        | Some fieldName ->
            let typeStr = getQualType node
            let fieldInfo = parseFieldType typeStr
            Some { fieldInfo with Name = fieldName }

    /// Process RecordDecl (struct/union) AST node
    let private processRecordDecl (node: JsonElement) : StructDecl option =
        let name = getString node "name"
        let tagUsed = getStringOr node "tagUsed" "struct"
        let isUnion = tagUsed = "union"

        let fields =
            getArray node "inner"
            |> Seq.filter (fun inner ->
                getStringOr inner "kind" "" = "FieldDecl")
            |> Seq.choose processFieldDecl
            |> List.ofSeq

        match name, fields with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Fields = fields; Documentation = None; IsUnion = isUnion }
        | _, fs when not fs.IsEmpty ->
            Some { Name = ""; Fields = fields; Documentation = None; IsUnion = isUnion }
        | _ -> None

    /// Process EnumConstantDecl AST node
    let private processEnumConstant (node: JsonElement) : EnumValue option =
        match getString node "name" with
        | None | Some "" -> None
        | Some constName ->
            // Try to get value - handle both positive and negative
            let value =
                getArray node "inner"
                |> Seq.tryPick (fun inner ->
                    match getInt64 inner "value" with
                    | Some v -> Some v
                    | None ->
                        getArray inner "inner"
                        |> Seq.tryPick (fun nested -> getInt64 nested "value"))
                |> Option.defaultValue 0L

            Some {
                Name = constName
                Value = value
                Documentation = None
            }

    /// Process EnumDecl AST node
    let private processEnumDecl (node: JsonElement) : EnumDecl option =
        let name = getString node "name"
        let values =
            getArray node "inner"
            |> Seq.filter (fun inner ->
                getStringOr inner "kind" "" = "EnumConstantDecl")
            |> Seq.choose processEnumConstant
            |> List.ofSeq

        // Try to get fixed underlying type
        let underlyingType =
            match getObject node "fixedUnderlyingType" with
            | Some ut -> getString ut "qualType"
            | None -> None

        match name, values with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Values = values; Documentation = None; UnderlyingType = underlyingType }
        | _, vs when not vs.IsEmpty ->
            Some { Name = ""; Values = values; Documentation = None; UnderlyingType = underlyingType }
        | _ -> None

    /// Process TypedefDecl AST node
    let private processTypedefDecl (node: JsonElement) : TypedefInfo option =
        match getString node "name" with
        | None | Some "" -> None
        | Some name ->
            let underlyingType = getQualType node
            Some {
                Name = name
                UnderlyingType = underlyingType
                Documentation = None
            }

    /// Walk AST tree and extract declarations from the target file
    /// Uses mutable state to track file across sibling nodes (clang only emits file once per file change)
    let private walkAst
        (root: JsonElement)
        (targetFile: string)
        (verbose: bool)
        : Declaration list =

        let results = ResizeArray<Declaration>()
        let mutable currentFile: string option = None

        /// Update file tracking from a node's location
        let updateFileTracking (node: JsonElement) =
            match getObject node "loc" with
            | Some loc ->
                // Check if this is from an included file
                match getObject loc "includedFrom" with
                | Some incl ->
                    // This declaration is from an included file
                    match getString incl "file" with
                    | Some f -> currentFile <- Some f
                    | None -> ()
                | None ->
                    // Not from include, check for file field
                    match getString loc "file" with
                    | Some f -> currentFile <- Some f
                    | None -> () // Keep using current file for same-file decls
            | None -> ()

        /// Check if current node is from target file (not from include)
        let isFromTargetFile (node: JsonElement) =
            match getObject node "loc" with
            | Some loc ->
                // If has includedFrom, it's from a different file
                match getObject loc "includedFrom" with
                | Some _ -> false
                | None ->
                    // Check if we're in target file
                    match currentFile with
                    | Some f -> f.EndsWith(targetFile) || f = targetFile
                    | None -> false
            | None -> false

        /// Process a single node
        let rec processNode (node: JsonElement) =
            updateFileTracking node

            let isImplicit = getBool node "isImplicit"
            let kind = getStringOr node "kind" ""

            if not isImplicit && isFromTargetFile node then
                if verbose then
                    let name = getStringOr node "name" "<anonymous>"
                    printfn "[CppParser] Processing %s: %s (file: %A)" kind name currentFile

                match kind with
                | "FunctionDecl" ->
                    match processFunctionDecl node with
                    | Some func -> results.Add(Function func)
                    | None -> ()

                | "RecordDecl" ->
                    match processRecordDecl node with
                    | Some structDecl -> results.Add(Struct structDecl)
                    | None -> ()

                | "EnumDecl" ->
                    match processEnumDecl node with
                    | Some enumDecl -> results.Add(Enum enumDecl)
                    | None -> ()

                | "TypedefDecl" ->
                    match processTypedefDecl node with
                    | Some typedef -> results.Add(Typedef typedef)
                    | None -> ()

                | "CXXRecordDecl" ->
                    match getString node "name" with
                    | Some name when not (String.IsNullOrEmpty(name)) ->
                        let methods =
                            getArray node "inner"
                            |> Seq.filter (fun inner ->
                                let k = getStringOr inner "kind" ""
                                k = "CXXMethodDecl" || k = "FunctionDecl")
                            |> Seq.choose processFunctionDecl
                            |> Seq.map Function
                            |> List.ofSeq

                        let fields =
                            getArray node "inner"
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "FieldDecl")
                            |> Seq.choose processFieldDecl
                            |> List.ofSeq

                        let isAbstract =
                            getArray node "inner"
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXMethodDecl" &&
                                getBool inner "pure")

                        results.Add(Class {
                            Name = name
                            Methods = methods
                            Fields = fields
                            Documentation = None
                            IsAbstract = isAbstract
                        })
                    | _ -> ()

                | "NamespaceDecl" ->
                    match getString node "name" with
                    | Some name when not (String.IsNullOrEmpty(name)) ->
                        // Process namespace contents
                        for inner in getArray node "inner" do
                            processNode inner

                        // Collect namespace declarations (not implemented yet for simplicity)
                        ()
                    | _ -> ()

                | _ -> ()

            // Recurse into children (except already-handled namespace)
            if kind <> "NamespaceDecl" then
                for inner in getArray node "inner" do
                    processNode inner

        // Start processing from root
        processNode root
        List.ofSeq results

    // =========================================================================
    // Macro Parsing
    // =========================================================================

    /// Parse a single macro definition line
    let private parseMacroLine (line: string) : MacroDecl option =
        if not (line.StartsWith("#define ")) then None
        else
            let rest = line.Substring(8).TrimStart()

            // Check for function-like macro: NAME(args) body
            let funcMatch = Regex.Match(rest, @"^(\w+)\(([^)]*)\)\s+(.+)$")
            if funcMatch.Success then
                let name = funcMatch.Groups.[1].Value
                let args = funcMatch.Groups.[2].Value.Split(',') |> Array.map (fun s -> s.Trim()) |> List.ofArray
                let body = funcMatch.Groups.[3].Value
                Some {
                    Name = name
                    Kind = FunctionLike (args, body)
                    RawValue = body
                }
            else
                // Object-like macro: NAME value or NAME (expression)
                let objMatch = Regex.Match(rest, @"^(\w+)\s+(.+)$")
                if objMatch.Success then
                    let name = objMatch.Groups.[1].Value
                    let value = objMatch.Groups.[2].Value.Trim()

                    // Classify the macro kind
                    let kind =
                        // Type cast pattern: ((Type*)value)
                        let castMatch = Regex.Match(value, @"^\(\((\w+)\s*\*\)\s*(.+)\)$")
                        if castMatch.Success then
                            TypeCast (castMatch.Groups.[1].Value, castMatch.Groups.[2].Value)
                        // Expression with operators
                        elif value.Contains("+") || value.Contains("-") || value.Contains("<<") ||
                             value.Contains(">>") || value.Contains("|") || value.Contains("&") then
                            Expression value
                        else
                            SimpleValue value

                    Some {
                        Name = name
                        Kind = kind
                        RawValue = value
                    }
                else
                    // Empty macro: NAME (no value)
                    let emptyMatch = Regex.Match(rest, @"^(\w+)$")
                    if emptyMatch.Success then
                        Some {
                            Name = emptyMatch.Groups.[1].Value
                            Kind = SimpleValue ""
                            RawValue = ""
                        }
                    else None

    /// Filter macros to exclude compiler built-ins
    let private isUserMacro (name: string) (prefixes: string list) : bool =
        // Exclude compiler built-ins
        if name.StartsWith("__") && name.EndsWith("__") then false
        elif name.StartsWith("_") && Char.IsUpper(name.[1]) then false  // Reserved identifiers
        elif prefixes.IsEmpty then true
        else prefixes |> List.exists (fun p -> name.StartsWith(p))

    // =========================================================================
    // Clang Invocation
    // =========================================================================

    /// Build common clang arguments
    let private buildClangArgs (options: HeaderParserOptions) : ResizeArray<string> =
        let args = ResizeArray<string>()

        for includePath in options.IncludePaths do
            args.Add($"-I{includePath}")

        for define in options.Defines do
            args.Add($"-D{define}")

        args

    /// Run clang with given arguments
    let private runClang (baseArgs: ResizeArray<string>) (extraArgs: string list) (verbose: bool) : Result<string, string> =
        let args = ResizeArray<string>(baseArgs)
        for arg in extraArgs do
            args.Add(arg)

        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "clang"
        startInfo.Arguments <- String.Join(" ", args)
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true

        if verbose then
            printfn "[CppParser] Running: clang %s" startInfo.Arguments

        try
            use proc = Process.Start(startInfo)
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode <> 0 then
                let errMsg =
                    if String.IsNullOrWhiteSpace(stderr) then
                        $"clang exited with code {proc.ExitCode}"
                    else
                        stderr.Trim()
                Error $"clang failed: {errMsg}"
            else
                if verbose then
                    printfn "[CppParser] clang completed, output: %d bytes" stdout.Length
                Ok stdout
        with ex ->
            Error $"Failed to run clang: {ex.Message}"

    /// Run clang AST dump
    let private runClangAst (options: HeaderParserOptions) : Result<string, string> =
        let args = buildClangArgs options
        runClang args ["-Xclang"; "-ast-dump=json"; "-fsyntax-only"; options.HeaderFile] options.Verbose

    /// Run clang preprocessor for macros
    let private runClangMacros (options: HeaderParserOptions) : Result<string, string> =
        let args = buildClangArgs options
        runClang args ["-E"; "-dM"; options.HeaderFile] options.Verbose

    // =========================================================================
    // Public API
    // =========================================================================

    /// Result of parsing containing both AST declarations and macros
    type ParseResult = {
        Declarations: Declaration list
        Macros: MacroDecl list
    }

    /// Parse a C/C++ header file and extract all declarations including macros
    let parseHeaderFull (options: HeaderParserOptions) : Result<ParseResult, string> =
        if not (File.Exists(options.HeaderFile)) then
            Error $"Header file not found: {options.HeaderFile}"
        else
            if options.Verbose then
                printfn "[CppParser] Parsing header: %s" options.HeaderFile

            // Pass 1: AST for structs, enums, typedefs, functions
            match runClangAst options with
            | Error err -> Error err
            | Ok jsonOutput ->
                try
                    if options.Verbose then
                        printfn "[CppParser] Parsing JSON AST..."

                    let doc = JsonDocument.Parse(jsonOutput)
                    let root = doc.RootElement
                    let targetFile = Path.GetFileName(options.HeaderFile)

                    let declarations = walkAst root targetFile options.Verbose

                    if options.Verbose then
                        printfn "[CppParser] Extracted %d AST declarations" (List.length declarations)

                    // Pass 2: Macros (if requested)
                    let macros =
                        if options.IncludeMacros then
                            match runClangMacros options with
                            | Error err ->
                                if options.Verbose then
                                    printfn "[CppParser] Warning: Failed to extract macros: %s" err
                                []
                            | Ok macroOutput ->
                                let allMacros =
                                    macroOutput.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                                    |> Array.choose parseMacroLine
                                    |> Array.filter (fun m -> isUserMacro m.Name options.MacroPrefixes)
                                    |> List.ofArray

                                if options.Verbose then
                                    printfn "[CppParser] Extracted %d macros" (List.length allMacros)

                                allMacros
                        else
                            []

                    Ok {
                        Declarations = declarations
                        Macros = macros
                    }
                with ex ->
                    Error $"Failed to parse clang output: {ex.Message}"

    /// Parse a C/C++ header file and extract declarations (backward compatible)
    let parseHeader (options: HeaderParserOptions) : Result<Declaration list, string> =
        match parseHeaderFull options with
        | Error err -> Error err
        | Ok result ->
            let allDecls =
                result.Declarations @
                (result.Macros |> List.map Macro)

            if allDecls.IsEmpty then
                Error $"Parse succeeded but no declarations found in {Path.GetFileName(options.HeaderFile)}."
            else
                Ok allDecls

    /// Simplified parse function for common use cases
    let parse (headerFile: string) (includePaths: string list) (verbose: bool) : Result<Declaration list, string> =
        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            Defines = []
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []
        }
        parseHeader options

    /// Parse with defines (useful for platform-specific headers like CMSIS)
    let parseWithDefines
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (verbose: bool) : Result<Declaration list, string> =

        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []
        }
        parseHeader options

    /// Parse CMSIS header with appropriate options
    let parseCMSIS
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (verbose: bool) : Result<ParseResult, string> =

        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []  // Include all macros for CMSIS
        }
        parseHeaderFull options
