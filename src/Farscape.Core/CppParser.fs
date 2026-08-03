namespace Farscape.Core

open System
open System.Diagnostics
open System.IO
open FSharp.Data
open XParsec
open XParsec.Parsers
open XParsec.Combinators
open XParsec.CharParsers

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
        IsBitfield: bool      // clang JSON "isBitfield"
        BitWidth: int option  // Bit width if IsBitfield
    }

    /// Raw attribute data extracted from clang AST nodes.
    /// Stored as-is from clang JSON; semantic interpretation happens downstream
    /// in WrapperPatternAnalyzer.
    type AttributeData = {
        /// Clang AST node kind, e.g., "AllocSizeAttr", "NonNullAttr", "FormatAttr"
        Kind: string
        /// Integer arguments (parameter indices for NonNull, AllocSize, etc.)
        Args: int list
        /// String argument (archetype for FormatAttr, label for AsmLabelAttr)
        StringArg: string option
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
        /// Raw clang AST attributes (AllocSizeAttr, NonNullAttr, etc.)
        Attributes: AttributeData list
        /// Itanium-mangled symbol name from clang AST (e.g., "_ZN3xrt6deviceC1Ej").
        /// Present for C++ methods, constructors, and destructors; None for C functions
        /// (which use unmangled names directly).
        MangledName: string option
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
        /// Documentation comment from raw header text (e.g., /* Operation not permitted */)
        Documentation: string option
    }

    /// Delegate (function pointer type with a name). Used for Wayland event handlers
    /// and any other callback type parsed from structured API definitions.
    type DelegateDecl = {
        Name: string
        Parameters: (string * string) list
        ReturnType: string
        Documentation: string option
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
        | Delegate of DelegateDecl

    and NamespaceDecl = {
        Name: string
        Declarations: Declaration list
    }

    and ClassDecl = {
        Name: string
        Methods: Declaration list
        /// CXXConstructorDecl nodes parsed as FunctionDecl (name = class name).
        /// Includes both user-defined and implicit constructors.
        Constructors: Declaration list
        Fields: FieldDecl list
        Documentation: string option
        IsAbstract: bool
        /// True when a non-implicit (user-defined or explicitly-defaulted) destructor exists.
        /// Determines triviality: classes with user destructors are non-trivially copyable
        /// and use MEMORY return classification (sret) under SysV x86_64 ABI.
        HasUserDestructor: bool
        /// Itanium-mangled symbol for the D1 (complete object) destructor, if present.
        /// Extracted from clang AST "mangledName" field on CXXDestructorDecl.
        DestructorMangledName: string option
        /// True when a non-implicit copy constructor exists (user-defined or explicitly-defaulted).
        HasUserCopyConstructor: bool
        /// True when a non-implicit move constructor exists (user-defined or explicitly-defaulted).
        HasUserMoveConstructor: bool
        /// Base class type strings from clang AST "bases" array.
        /// Used to detect inherited pimpl pattern (e.g., "detail::pimpl<xclbin_impl>").
        BaseClasses: string list
    }

    // =========================================================================
    // Struct Layout Information (from clang -fdump-record-layouts-simple)
    // =========================================================================

    /// Layout information for a struct, extracted from clang's record layout dump.
    /// All sizes are in bits as reported by clang; byte conversion happens downstream.
    type StructLayoutInfo = {
        /// Struct name (e.g., "drm_mode_create_dumb")
        Name: string
        /// Total size in bits
        SizeBits: int
        /// Data size in bits (excludes padding at end)
        DataSizeBits: int
        /// Required alignment in bits
        AlignmentBits: int
        /// Per-field offsets in bits (order matches FieldDecl order)
        FieldOffsetsBits: int list
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
        /// Root directory for library boundary scoping.
        /// When set, only declarations from files under this path are extracted.
        /// Derived automatically from pkg-config include paths.
        IncludeRoot: string option
        /// When true, invoke clang in C++ mode (-x c++ -std=c++17).
        /// Required for .h headers that contain C++ class declarations
        /// behind #ifdef __cplusplus guards.
        CppMode: bool
    }

    /// Detect whether a header file should be parsed in C++ mode.
    /// Returns true for .hpp/.hxx/.hh extensions, or for .h files whose
    /// content contains C++ indicators (namespace, class, template, std::).
    let detectCppMode (headerFile: string) : bool =
        let ext = Path.GetExtension(headerFile).ToLowerInvariant()
        match ext with
        | ".hpp" | ".hxx" | ".hh" -> true
        | ".h" ->
            try
                let content = File.ReadAllText(headerFile)
                content.Contains("namespace ") ||
                content.Contains("class ") ||
                content.Contains("template<") ||
                content.Contains("template <") ||
                content.Contains("std::") ||
                // C++ stdlib includes: <cXXX> wrappers or extensionless STL headers
                System.Text.RegularExpressions.Regex.IsMatch(
                    content, @"#\s*include\s*<c(errno|string|stdlib|stdio|math|locale|type)\b") ||
                System.Text.RegularExpressions.Regex.IsMatch(
                    content, @"#\s*include\s*<(vector|string|memory|map|set|list|array|optional|variant|tuple|algorithm|functional|iostream|fstream|sstream|chrono|thread|mutex|atomic|utility|numeric|deque|queue|stack|bitset|span|ranges|format|any|regex|filesystem|future|exception|typeinfo|type_traits|initializer_list|string_view|unordered_map|unordered_set)>")
            with _ -> false
        | _ -> false

    /// Create default parser options for a header file
    let defaultOptions headerFile = {
        HeaderFile = headerFile
        IncludePaths = []
        Defines = []
        Verbose = false
        IncludeMacros = true
        MacroPrefixes = []
        IncludeRoot = None
        CppMode = detectCppMode headerFile
    }

    // =========================================================================
    // JSON AST Parsing Helpers
    // =========================================================================

    /// Get string property from JSON element
    let private getString (element: JsonValue) (prop: string) : string option =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.String s) -> Some s
        | _ -> None

    /// Get string property with default value
    let private getStringOr (element: JsonValue) (prop: string) (defaultVal: string) : string =
        getString element prop |> Option.defaultValue defaultVal

    /// Get integer property (signed)
    let private getInt64 (element: JsonValue) (prop: string) : int64 option =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Number d) -> Some (int64 d)
        | Some (JsonValue.String s) ->
            match System.Int64.TryParse(s) with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    /// Get boolean property
    let private getBool (element: JsonValue) (prop: string) : bool =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Boolean b) -> b
        | _ -> false

    /// Get array property
    let private getArray (element: JsonValue) (prop: string) : JsonValue seq =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Array elements) -> elements :> JsonValue seq
        | _ -> Seq.empty

    /// Recursively collect TextComment text from a FullComment AST node.
    /// FullComment → ParagraphComment → TextComment (with .text field).
    /// Multi-line comments produce multiple TextComment nodes; we join them.
    let rec private extractTextFromComment (node: JsonValue) : string list =
        let kind = getStringOr node "kind" ""
        if kind = "TextComment" then
            match getString node "text" with
            | Some t -> [t.Trim()]
            | None -> []
        else
            getArray node "inner"
            |> Seq.collect extractTextFromComment
            |> List.ofSeq

    /// Extract documentation string from a declaration's inner nodes.
    /// Looks for FullComment nodes (produced by -fparse-all-comments) and
    /// joins all TextComment text into a single documentation string.
    let private extractDocumentation (node: JsonValue) : string option =
        let texts =
            getArray node "inner"
            |> Seq.filter (fun inner -> getStringOr inner "kind" "" = "FullComment")
            |> Seq.collect extractTextFromComment
            |> Seq.filter (fun t -> t <> "")
            |> List.ofSeq
        match texts with
        | [] -> None
        | lines -> Some (String.concat " " lines)

    /// Get nested object property
    let private getObject (element: JsonValue) (prop: string) : JsonValue option =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Record _ as obj) -> Some obj
        | _ -> None

    /// Check if a declaration is from an included file (vs the main file)
    let private isFromIncludedFile (element: JsonValue) : bool =
        match getObject element "loc" with
        | Some loc ->
            match getObject loc "includedFrom" with
            | Some _ -> true
            | None -> false
        | None -> false

    /// Extract file from location info, tracking spillover from previous declarations
    /// Clang JSON only includes 'file' on the first decl from each file, subsequent ones just have line/offset
    let private getFileFromLoc (element: JsonValue) (lastKnownFile: string option) : string option =
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
    let private getQualType (element: JsonValue) : string =
        match getObject element "type" with
        | Some typeObj -> getStringOr typeObj "qualType" "unknown"
        | None -> "unknown"

    /// Extract return type from function type string (e.g., "void (int)" -> "void")
    let private extractReturnType (typeStr: string) : string =
        match typeStr.IndexOf('(') with
        | -1 -> typeStr.Trim()
        | idx -> typeStr.Substring(0, idx).Trim()

    /// Parse a field type string into structured info.
    /// Uses XParsec for array size extraction (no Regex).
    let private parseFieldType (typeStr: string) : FieldDecl =
        let isVolatile = typeStr.Contains("volatile") || typeStr.Contains("__IO")
        let isConst = typeStr.Contains("const") || typeStr.Contains("__I")

        // Check for array syntax using XParsec parser
        let pArraySize =
            skipMany (satisfyL (fun c -> c <> '[') "non-bracket")
            >>. (skipChar '[' >>. pint32 .>> skipChar ']')

        let isArray, arraySize, cleanType =
            let reader = Reader.ofString typeStr ()
            match pArraySize reader with
            | Ok result ->
                let baseType =
                    match typeStr.IndexOf('[') with
                    | -1 -> typeStr
                    | idx -> typeStr.Substring(0, idx)
                true, Some result.Parsed, baseType.Trim()
            | Error _ ->
                false, None, typeStr

        // Clean up qualifiers for the base type
        let cleanType =
            cleanType.Replace("volatile ", "").Replace("const ", "")
                     .Replace("__IO ", "").Replace("__I ", "").Replace("__O ", "")
                     .Trim()

        {
            Name = ""
            Type = cleanType
            IsVolatile = isVolatile
            IsConst = isConst
            IsArray = isArray
            ArraySize = arraySize
            IsBitfield = false
            BitWidth = None
        }

    // =========================================================================
    // AST Node Processing
    // =========================================================================

    /// Extract raw attribute data from a FunctionDecl's inner nodes.
    /// Filters for child nodes with kind ending in "Attr" (excluding BuiltinAttr).
    let private extractAttributes (node: JsonValue) : AttributeData list =
        getArray node "inner"
        |> Seq.filter (fun inner ->
            let kind = getStringOr inner "kind" ""
            kind.EndsWith("Attr") && kind <> "BuiltinAttr")
        |> Seq.choose (fun attr ->
            let kind = getStringOr attr "kind" ""
            // Extract integer args from inner ConstantExpr/IntegerLiteral nodes
            let args =
                getArray attr "inner"
                |> Seq.choose (fun arg ->
                    // Try direct value, then look for nested integer literals
                    match getInt64 arg "value" with
                    | Some v -> Some (int v)
                    | None ->
                        getArray arg "inner"
                        |> Seq.tryPick (fun nested -> getInt64 nested "value" |> Option.map int))
                |> List.ofSeq
            // Extract string argument (archetype for FormatAttr, label for AsmLabelAttr)
            let stringArg =
                getString attr "archetype"
                |> Option.orElseWith (fun () -> getString attr "label")
            Some { Kind = kind; Args = args; StringArg = stringArg })
        |> List.ofSeq

    /// Process FunctionDecl AST node
    let private processFunctionDecl (node: JsonValue) : FunctionDecl option =
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
            let attributes = extractAttributes node
            let documentation = extractDocumentation node
            let mangledName = getString node "mangledName"

            Some {
                Name = name
                ReturnType = returnType
                Parameters = parameters
                Documentation = documentation
                IsVirtual = false
                IsStatic = isStatic
                IsInline = isInline
                Attributes = attributes
                MangledName = mangledName
            }

    /// Process FieldDecl AST node
    let private processFieldDecl (node: JsonValue) : FieldDecl option =
        match getString node "name" with
        | None | Some "" -> None
        | Some fieldName ->
            let typeStr = getQualType node
            let fieldInfo = parseFieldType typeStr
            let isBitfield = getBool node "isBitfield"
            let bitWidth =
                if isBitfield then
                    node.TryGetProperty("inner")
                    |> Option.bind (fun inner ->
                        inner.AsArray()
                        |> Array.tryPick (fun child ->
                            match getString child "kind" with
                            | Some "ConstantExpr" | Some "IntegerLiteral" ->
                                getString child "value" |> Option.bind (fun v ->
                                    match System.Int32.TryParse(v) with
                                    | true, n -> Some n
                                    | _ -> None)
                            | _ -> None))
                else None
            Some { fieldInfo with Name = fieldName; IsBitfield = isBitfield; BitWidth = bitWidth }

    /// Process RecordDecl (struct/union) AST node
    let private processRecordDecl (node: JsonValue) : StructDecl option =
        let name = getString node "name"
        let tagUsed = getStringOr node "tagUsed" "struct"
        let isUnion = tagUsed = "union"
        let documentation = extractDocumentation node

        let fields =
            getArray node "inner"
            |> Seq.filter (fun inner ->
                getStringOr inner "kind" "" = "FieldDecl")
            |> Seq.choose processFieldDecl
            |> List.ofSeq

        match name, fields with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Fields = fields; Documentation = documentation; IsUnion = isUnion }
        | _, fs when not fs.IsEmpty ->
            Some { Name = ""; Fields = fields; Documentation = documentation; IsUnion = isUnion }
        | _ -> None

    /// Extract the explicit value from an EnumConstantDecl AST node, if present.
    /// Returns None when the enum constant has no initializer (implicit sequential value).
    let private extractEnumConstantValue (node: JsonValue) : int64 option =
        getArray node "inner"
        |> Seq.tryPick (fun inner ->
            match getStringOr inner "kind" "" with
            | "ConstantExpr" | "IntegerLiteral" ->
                getInt64 inner "value"
            | _ ->
                getArray inner "inner"
                |> Seq.tryPick (fun nested -> getInt64 nested "value"))

    /// Process EnumDecl AST node
    let private processEnumDecl (node: JsonValue) : EnumDecl option =
        let name = getString node "name"
        let documentation = extractDocumentation node
        // Process enum constants with auto-increment for implicit values.
        // C rule: first implicit value is 0, subsequent implicit values are previous + 1,
        // explicit values reset the counter.
        let values =
            let constants =
                getArray node "inner"
                |> Seq.filter (fun inner ->
                    getStringOr inner "kind" "" = "EnumConstantDecl")
            let mutable nextValue = 0L
            [ for constNode in constants do
                match getString constNode "name" with
                | None | Some "" -> ()
                | Some constName ->
                    let value =
                        match extractEnumConstantValue constNode with
                        | Some explicit -> explicit
                        | None -> nextValue
                    nextValue <- value + 1L
                    yield { Name = constName; Value = value; Documentation = None } ]

        // Try to get fixed underlying type
        let underlyingType =
            match getObject node "fixedUnderlyingType" with
            | Some ut -> getString ut "qualType"
            | None -> None

        match name, values with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Values = values; Documentation = documentation; UnderlyingType = underlyingType }
        | _, vs when not vs.IsEmpty ->
            Some { Name = ""; Values = values; Documentation = documentation; UnderlyingType = underlyingType }
        | _ -> None

    /// Process TypedefDecl AST node
    let private processTypedefDecl (node: JsonValue) : TypedefInfo option =
        match getString node "name" with
        | None | Some "" -> None
        | Some name ->
            let underlyingType = getQualType node
            Some {
                Name = name
                UnderlyingType = underlyingType
                Documentation = None
            }

    /// Walk AST tree and extract declarations from the target file.
    /// Uses mutable state to track file across sibling nodes (clang only emits file once per file change).
    /// Library boundary is determined by includeRoot (from pkg-config) or filename matching (fallback).
    let private walkAst
        (root: JsonValue)
        (targetFile: string)
        (includeRoot: string option)
        (verbose: bool)
        : Declaration list =

        let results = ResizeArray<Declaration>()
        let mutable currentFile: string option = None

        if verbose then
            match includeRoot with
            | Some root -> printfn "[CppParser] Include root scoping: %s" root
            | None -> printfn "[CppParser] No include root — using filename matching for: %s" targetFile

        /// Update file tracking from a node's location.
        /// Clang's JSON AST uses "loc.file" to indicate a change in source file.
        /// When "file" is absent, the node is in the same file as the previous node.
        let updateFileTracking (node: JsonValue) =
            match getObject node "loc" with
            | Some loc ->
                match getString loc "file" with
                | Some f -> currentFile <- Some f
                | None -> ()
            | None -> ()

        /// Check if current node belongs to this library.
        /// With includeRoot: any file under that directory tree belongs to the library.
        /// Without: fall back to matching the target filename (for simple single-file headers).
        let isFromTargetFile (node: JsonValue) =
            match currentFile with
            | Some f ->
                match includeRoot with
                | Some root -> f.StartsWith root
                | None -> f.EndsWith targetFile || f = targetFile
            | None -> false

        /// Process a single node
        let rec processNode (node: JsonValue) =
            updateFileTracking node

            let isImplicit = getBool node "isImplicit"
            let kind = getStringOr node "kind" ""

            // NamespaceDecl is a structural container, not a declaration.
            // Always recurse into namespace children regardless of file context,
            // because clang often elides the file attribute on NamespaceDecl nodes
            // (it only emits file on the first source-file change). The library
            // boundary check applies to leaf declarations inside the namespace.
            if kind = "NamespaceDecl" then
                match getString node "name" with
                | Some name when not (System.String.IsNullOrEmpty(name)) ->
                    for inner in getArray node "inner" do
                        processNode inner
                | _ -> ()

            elif not isImplicit && isFromTargetFile node then
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
                    | Some name when not (System.String.IsNullOrEmpty(name)) ->
                        let innerNodes = getArray node "inner"

                        // Extract base class types from clang's "bases" array.
                        // Each entry has type.qualType (written) and type.desugaredQualType (canonical).
                        // Use desugaredQualType when available for full namespace resolution.
                        let baseClasses =
                            match node.TryGetProperty("bases") with
                            | Some (JsonValue.Array basesArr) ->
                                [ for baseNode in basesArr do
                                    match baseNode.TryGetProperty("type") with
                                    | Some typeNode ->
                                        match getString typeNode "desugaredQualType" with
                                        | Some t -> yield t
                                        | None ->
                                            match getString typeNode "qualType" with
                                            | Some t -> yield t
                                            | None -> ()
                                    | None -> () ]
                            | _ -> []

                        let methods =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                let k = getStringOr inner "kind" ""
                                k = "CXXMethodDecl" || k = "FunctionDecl")
                            |> Seq.choose processFunctionDecl
                            |> Seq.map Function
                            |> List.ofSeq

                        // Capture constructors (CXXConstructorDecl) as FunctionDecl.
                        // The constructor name in clang AST is the class name.
                        let constructors =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl")
                            |> Seq.choose processFunctionDecl
                            |> Seq.map Function
                            |> List.ofSeq

                        let fields =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "FieldDecl")
                            |> Seq.choose processFieldDecl
                            |> List.ofSeq

                        let isAbstract =
                            innerNodes
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXMethodDecl" &&
                                getBool inner "pure")

                        // Detect non-implicit (user-defined) destructor and extract its
                        // mangled symbol. Under SysV x86_64 ABI, a user-defined destructor
                        // makes the type non-trivially copyable, triggering MEMORY classification
                        // and hidden sret pointer for return-by-value.
                        let userDtorNode =
                            innerNodes
                            |> Seq.tryFind (fun inner ->
                                getStringOr inner "kind" "" = "CXXDestructorDecl" &&
                                not (getBool inner "isImplicit"))
                        let hasUserDestructor = userDtorNode.IsSome
                        let destructorMangledName =
                            userDtorNode |> Option.bind (fun n -> getString n "mangledName")

                        // Detect non-implicit copy constructor.
                        // A copy ctor takes a single const-ref-to-self parameter.
                        let hasUserCopyConstructor =
                            innerNodes
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl" &&
                                not (getBool inner "isImplicit") &&
                                (match getString inner "explicitlyDefaulted" with
                                 | Some "deleted" -> false
                                 | _ ->
                                    let params =
                                        getArray inner "inner"
                                        |> Seq.filter (fun p -> getStringOr p "kind" "" = "ParmVarDecl")
                                        |> List.ofSeq
                                    params.Length = 1 &&
                                    (let t = getQualType params.[0]
                                     t.Contains("const") && t.Contains("&") && t.Contains(name))))

                        // Detect non-implicit move constructor.
                        // A move ctor takes a single rvalue-ref-to-self parameter.
                        let hasUserMoveConstructor =
                            innerNodes
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl" &&
                                not (getBool inner "isImplicit") &&
                                (match getString inner "explicitlyDefaulted" with
                                 | Some "deleted" -> false
                                 | _ ->
                                    let params =
                                        getArray inner "inner"
                                        |> Seq.filter (fun p -> getStringOr p "kind" "" = "ParmVarDecl")
                                        |> List.ofSeq
                                    params.Length = 1 &&
                                    (let t = getQualType params.[0]
                                     t.Contains("&&") && t.Contains(name) && not (t.Contains("const")))))

                        results.Add(Class {
                            Name = name
                            Methods = methods
                            Constructors = constructors
                            Fields = fields
                            Documentation = None
                            IsAbstract = isAbstract
                            HasUserDestructor = hasUserDestructor
                            DestructorMangledName = destructorMangledName
                            HasUserCopyConstructor = hasUserCopyConstructor
                            HasUserMoveConstructor = hasUserMoveConstructor
                            BaseClasses = baseClasses
                        })
                    | _ -> ()

                | _ -> ()

                // Recurse into children for non-namespace declarations
                for inner in getArray node "inner" do
                    processNode inner

            else
                // Outside the target file: still recurse to find target-file
                // declarations that may appear deeper in the AST (e.g. in
                // TranslationUnitDecl or LinkageSpecDecl wrappers).
                if kind <> "NamespaceDecl" then
                    for inner in getArray node "inner" do
                        processNode inner

        // Start processing from root
        processNode root
        List.ofSeq results

    // =========================================================================
    // Macro Parsing
    // =========================================================================

    // =========================================================================
    // XParsec Macro Parsers (private, defined inline for compile-order independence)
    // =========================================================================

    /// Classify an object macro's value into MacroKind
    let private classifyObjectMacroValue (value: string) : MacroKind =
        if value.StartsWith("((") && value.Contains("*)") && value.EndsWith(")") then
            let inner = value.Substring(2, value.Length - 3)
            match inner.IndexOf("*)") with
            | -1 -> SimpleValue value
            | idx ->
                let typeName = inner.Substring(0, idx).Trim()
                let castValue = inner.Substring(idx + 2).Trim()
                TypeCast (typeName, castValue)
        elif value.Contains("+") || value.Contains("-") || value.Contains("<<") ||
             value.Contains(">>") || value.Contains("|") || value.Contains("&") then
            Expression value
        else
            SimpleValue value

    /// XParsec parsers for macro line parsing (same pattern as XParsec.Json.JsonParsers)
    type private MacroParsers<'Input, 'InputSlice
        when 'Input :> IReadable<char, 'InputSlice> and 'InputSlice :> IReadable<char, 'InputSlice>>() =

        static let pIdentifier =
            many1Chars (satisfyL (fun c -> Char.IsLetterOrDigit c || c = '_') "identifier char")

        static let pFunctionLikeMacro =
            parser {
                let! name = pIdentifier
                do! skipChar '('
                let! args, _ = sepBy (spaces >>. pIdentifier .>> spaces) (skipChar ',')
                do! skipChar ')'
                do! spaces1
                let! body = manyChars (satisfyL (fun _ -> true) "body char")
                let macro : MacroDecl = {
                    Name = name
                    Kind = FunctionLike (Seq.toList args, body)
                    RawValue = body
                    Documentation = None
                }
                return macro
            }

        static let pObjectMacro =
            parser {
                let! name = pIdentifier
                do! spaces1
                let! value = manyChars (satisfyL (fun _ -> true) "value char")
                let trimmed = value.Trim()
                let macro : MacroDecl = {
                    Name = name
                    Kind = classifyObjectMacroValue trimmed
                    RawValue = trimmed
                    Documentation = None
                }
                return macro
            }

        static let pEmptyMacro =
            parser {
                let! name = pIdentifier
                do! eof
                let macro : MacroDecl = {
                    Name = name
                    Kind = SimpleValue ""
                    RawValue = ""
                    Documentation = None
                }
                return macro
            }

        static let pMacroLine =
            parser {
                do! pstring "#define " >>% ()
                do! spaces
                return! choice [ pFunctionLikeMacro; pObjectMacro; pEmptyMacro ]
            }

        static member MacroLine = pMacroLine

    type private MP = MacroParsers<ReadableString, ReadableStringSlice>

    /// Parse a single macro definition line using XParsec.
    let private parseMacroLine (line: string) : MacroDecl option =
        if not (line.StartsWith("#define ")) then None
        else
            let reader = Reader.ofString line ()
            match MP.MacroLine reader with
            | Ok result -> Some result.Parsed
            | Error _ -> None

    /// Filter macros to exclude compiler built-ins.
    let private isUserMacro (name: string) (prefixes: string list) : bool =
        // Exclude compiler built-ins (__STDC__, __GNUC__ etc.)
        if name.StartsWith("__") && name.EndsWith("__") then false
        // Exclude reserved identifiers (_POSIX_SOURCE, _GNU_SOURCE etc.)
        elif name.StartsWith("_") && name.Length > 1 && Char.IsUpper(name.[1]) then false
        elif prefixes.IsEmpty then true
        else prefixes |> List.exists (fun p -> name.StartsWith(p))

    // =========================================================================
    // Raw Header Comment Extraction
    // =========================================================================

    /// Extract trailing comment from a #define line in raw header text.
    /// Handles both /* ... */ and // ... comment styles.
    /// Returns just the comment text without delimiters.
    let private extractTrailingComment (line: string) : string option =
        // Look for /* ... */ comment
        match line.IndexOf("/*") with
        | -1 ->
            // Look for // comment
            match line.IndexOf("//") with
            | -1 -> None
            | idx ->
                let comment = line.Substring(idx + 2).Trim()
                if comment.Length > 0 then Some comment else None
        | idx ->
            let afterOpen = line.Substring(idx + 2)
            match afterOpen.IndexOf("*/") with
            | -1 -> None  // Unclosed comment — skip
            | endIdx ->
                let comment = afterOpen.Substring(0, endIdx).Trim()
                if comment.Length > 0 then Some comment else None

    /// Scan a single file's raw text for #define lines and extract macro name → comment mappings.
    let private extractMacroCommentsFromFile (filePath: string) : Map<string, string> =
        if not (File.Exists filePath) then Map.empty
        else
            try
                File.ReadAllLines(filePath)
                |> Array.choose (fun line ->
                    let trimmed = line.TrimStart()
                    if trimmed.StartsWith("#define") then
                        // Parse: #define NAME ...rest... /* comment */
                        let afterDefine = trimmed.Substring(7).TrimStart()
                        // Extract the macro name (first identifier)
                        let nameEnd =
                            afterDefine
                            |> Seq.tryFindIndex (fun c -> not (Char.IsLetterOrDigit c || c = '_'))
                            |> Option.defaultValue afterDefine.Length
                        if nameEnd > 0 then
                            let name = afterDefine.Substring(0, nameEnd)
                            match extractTrailingComment afterDefine with
                            | Some comment -> Some (name, comment)
                            | None -> None
                        else None
                    else None)
                |> Map.ofArray
            with _ -> Map.empty

    // =========================================================================
    // Struct Layout Parser (XParsec, monadic — parses clang -fdump-record-layouts-simple)
    // =========================================================================

    /// XParsec parsers for clang's -fdump-record-layouts-simple stderr output.
    ///
    /// Format (all values in bits):
    ///   *** Dumping AST Record Layout
    ///   Type: struct Point
    ///   Layout: <ASTRecordLayout
    ///     Size:128
    ///     DataSize:128
    ///     Alignment:64
    ///     FieldOffsets: [0, 32, 64]>
    type private LayoutParsers<'Input, 'InputSlice
        when 'Input :> IReadable<char, 'InputSlice> and 'InputSlice :> IReadable<char, 'InputSlice>>() =

        static let pSpaces = skipMany (satisfyL (fun c -> c = ' ' || c = '\t') "space")

        static let pNewline = satisfyL (fun c -> c = '\n' || c = '\r') "newline" >>% ()

        static let pSkipLine =
            skipMany (satisfyL (fun c -> c <> '\n' && c <> '\r') "non-newline")
            >>. optional pNewline

        static let pStructName =
            parser {
                do! pSpaces
                do! pstring "Type: " >>% ()
                do! optional (pstring "struct " >>% ()) >>% ()
                let! name = many1Chars (satisfyL (fun c -> c <> '\n' && c <> '\r' && c <> ' ') "name char")
                do! pSkipLine
                return name
            }

        static let pIntField label =
            parser {
                do! pSpaces
                do! pstring label >>% ()
                let! value = pint32
                do! pSkipLine
                return value
            }

        static let pFieldOffsets =
            parser {
                do! pSpaces
                do! pstring "FieldOffsets: [" >>% ()
                let! offsets, _ = sepBy (pSpaces >>. pint32 .>> pSpaces) (skipChar ',')
                do! skipChar ']'
                do! pSkipLine
                return Seq.toList offsets
            }

        static let pEmptyFieldOffsets =
            parser {
                do! pSpaces
                do! pstring "FieldOffsets: []" >>% ()
                do! pSkipLine
                return []
            }

        static let pLayoutBlock =
            parser {
                let! name = pStructName
                do! pSkipLine // "Layout: <ASTRecordLayout" line
                let! size = pIntField "Size:"
                let! dataSize = pIntField "DataSize:"
                let! alignment = pIntField "Alignment:"
                let! offsets = pEmptyFieldOffsets <|> pFieldOffsets
                do! pSkipLine // closing ">" line
                return {
                    Name = name
                    SizeBits = size
                    DataSizeBits = dataSize
                    AlignmentBits = alignment
                    FieldOffsetsBits = offsets
                }
            }

        static member LayoutBlock = pLayoutBlock

    type private LP = LayoutParsers<ReadableString, ReadableStringSlice>

    /// Parse clang's -fdump-record-layouts-simple stderr output into a map of struct layouts.
    /// Each record layout block is preceded by "*** Dumping AST Record Layout".
    /// Blocks that fail to parse are silently skipped.
    let parseRecordLayouts (stderr: string) : Map<string, StructLayoutInfo> =
        let blocks = stderr.Split("*** Dumping AST Record Layout")
        blocks
        |> Array.choose (fun block ->
            let trimmed = block.TrimStart([| '\n'; '\r' |])
            if String.IsNullOrWhiteSpace(trimmed) then None
            else
                let reader = Reader.ofString trimmed ()
                match LP.LayoutBlock reader with
                | Ok result -> Some (result.Parsed.Name, result.Parsed)
                | Error _ -> None)
        |> Map.ofArray

    // =========================================================================
    // Clang Invocation
    // =========================================================================

    /// Build common clang arguments
    let private buildClangArgs (options: HeaderParserOptions) : ResizeArray<string> =
        let args = ResizeArray<string>()

        // Force C++ mode for headers containing C++ content.
        // Without this, clang treats .h files as C and skips
        // #ifdef __cplusplus blocks entirely.
        if options.CppMode then
            args.Add("-x")
            args.Add("c++")
            args.Add("-std=c++17")

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
        runClang args ["-Xclang"; "-ast-dump=json"; "-fsyntax-only"; "-fparse-all-comments"; options.HeaderFile] options.Verbose

    /// Run clang preprocessor for macros
    let private runClangMacros (options: HeaderParserOptions) : Result<string, string> =
        let args = buildClangArgs options
        runClang args ["-E"; "-dM"; options.HeaderFile] options.Verbose

    /// Run clang with -fdump-record-layouts-simple to extract struct layout info.
    /// Generates a temporary C file that forces layout computation for the named structs
    /// by referencing sizeof() for each. Layout data appears on stderr.
    let extractStructLayouts
        (headerFile: string) (includePaths: string list) (defines: string list)
        (structNames: string list) (verbose: bool)
        : Result<Map<string, StructLayoutInfo>, string> =

        if structNames.IsEmpty then Ok Map.empty
        else

        let tempDir = Path.Combine(Path.GetTempPath(), "farscape-layout")
        Directory.CreateDirectory(tempDir) |> ignore
        let tempFile = Path.Combine(tempDir, "_farscape_layout_probe.c")

        let result =
            try
                // Generate a C file that forces layout computation via sizeof()
                let includeDirective = $"#include \"{headerFile}\""
                let sizeofRefs =
                    structNames
                    |> List.mapi (fun i name -> $"void *_fs_layout_{i} = (void*)sizeof(struct {name});")
                    |> String.concat "\n"
                File.WriteAllText(tempFile, $"{includeDirective}\n{sizeofRefs}\n")

                let args = ResizeArray<string>()
                for ip in includePaths do args.Add($"-I{ip}")
                for d in defines do args.Add($"-D{d}")
                for arg in ["-Xclang"; "-fdump-record-layouts-simple"; "-fsyntax-only"; tempFile] do
                    args.Add(arg)

                let startInfo = ProcessStartInfo()
                startInfo.FileName <- "clang"
                startInfo.Arguments <- String.Join(" ", args :> string seq)
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true

                if verbose then
                    printfn "[CppParser] Layout extraction: clang %s" startInfo.Arguments

                use proc = Process.Start(startInfo)
                proc.StandardOutput.ReadToEnd() |> ignore
                let stderr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()

                if verbose then
                    printfn "[CppParser] Layout stderr: %d bytes, exit code: %d" stderr.Length proc.ExitCode

                // Parse layout data from stderr (clang may return non-zero due to unused variables)
                let layouts = parseRecordLayouts stderr

                // Filter to only the requested struct names
                let filtered =
                    structNames
                    |> List.choose (fun name -> layouts |> Map.tryFind name |> Option.map (fun l -> (name, l)))
                    |> Map.ofList

                Ok filtered
            with ex ->
                Error $"Failed to extract struct layouts: {ex.Message}"
        try File.Delete(tempFile) with _ -> ()
        result

    // =========================================================================
    // Include Tree Discovery + Macro Documentation Enrichment
    // =========================================================================

    /// Parse clang -H output (from stderr) to get the include file list.
    /// Format: ". /path/to/file" with dots indicating depth.
    let private parseIncludeList (clangHOutput: string) : string list =
        clangHOutput.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let trimmed = line.TrimStart('.')
            let path = trimmed.TrimStart()
            if File.Exists path then Some path else None)
        |> Array.distinct
        |> List.ofArray

    /// Run clang -H to discover the include file tree (files listed on stderr).
    let private runClangIncludes (options: HeaderParserOptions) : string list =
        let clangArgs = buildClangArgs options
        for arg in ["-H"; "-E"; "-o"; "/dev/null"; options.HeaderFile] do
            clangArgs.Add arg

        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "clang"
        startInfo.Arguments <- String.Join(" ", clangArgs :> string seq)
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true

        try
            use proc = Process.Start(startInfo)
            proc.StandardOutput.ReadToEnd() |> ignore
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()
            options.HeaderFile :: parseIncludeList stderr
        with _ ->
            [options.HeaderFile]

    /// Build a complete macro name → documentation map by scanning raw header files.
    /// Uses clang -H to discover the include tree, then reads each file's #define comments.
    let private buildMacroDocumentationMap (options: HeaderParserOptions) : Map<string, string> =
        let files = runClangIncludes options
        files
        |> List.map extractMacroCommentsFromFile
        |> List.fold (fun acc map ->
            Map.fold (fun s k v -> Map.add k v s) acc map) Map.empty

    /// Enrich a list of MacroDecl with documentation from raw header comments.
    let private enrichMacrosWithDocumentation
        (docMap: Map<string, string>)
        (macros: MacroDecl list)
        : MacroDecl list =
        macros
        |> List.map (fun m ->
            match Map.tryFind m.Name docMap with
            | Some doc -> { m with Documentation = Some doc }
            | None -> m)

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

                    let root = JsonValue.Parse(jsonOutput)
                    let targetFile = Path.GetFileName(options.HeaderFile)

                    let declarations = walkAst root targetFile options.IncludeRoot options.Verbose

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

                                // Pass 3: Enrich macros with documentation from raw header comments
                                let docMap = buildMacroDocumentationMap options
                                let enriched = enrichMacrosWithDocumentation docMap allMacros

                                if options.Verbose then
                                    let docCount = enriched |> List.filter (fun m -> m.Documentation.IsSome) |> List.length
                                    printfn "[CppParser] Enriched %d/%d macros with documentation" docCount (List.length enriched)

                                enriched
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
            IncludeRoot = None
            CppMode = detectCppMode headerFile
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
            IncludeRoot = None
            CppMode = detectCppMode headerFile
        }
        parseHeader options

    /// Parse with include root scoping for library boundary detection
    let parseWithIncludeRoot
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (includeRoot: string option)
        (macroPrefixes: string list)
        (verbose: bool) : Result<Declaration list, string> =

        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = macroPrefixes
            IncludeRoot = includeRoot
            CppMode = detectCppMode headerFile
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
            MacroPrefixes = []
            IncludeRoot = None
            CppMode = false  // CMSIS headers are always C
        }
        parseHeaderFull options
