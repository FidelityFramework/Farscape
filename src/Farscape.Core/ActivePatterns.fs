namespace Farscape.Core

open System

/// Active patterns for structured decomposition of C types, macros, and F# identifiers.
///
/// These active patterns are backed by XParsec parsers from CTypeParser.fs.
/// They replace all if/elif/else chains and string.Contains checks throughout Farscape.
module ActivePatterns =

    // =========================================================================
    // C Type Active Patterns
    // =========================================================================

    /// Parse a C type string into structured CTypeInfo using XParsec.
    /// Usage: match "const char *" with ParsedCType info -> ...
    let (|ParsedCType|_|) (s: string) : CTypeParser.CTypeInfo option =
        CTypeParser.tryParseCType s

    /// Decompose a parsed CTypeInfo into binding categories.
    /// CharPointer: char*, unsigned char*, const char* → nativeptr<byte>
    /// VoidPointer: void* → nativeint
    /// TypedPointer: any other pointer → nativeint
    /// ValueType: non-pointer → look up in type dictionary
    let (|CharPointer|VoidPointer|TypedPointer|ValueType|) (info: CTypeParser.CTypeInfo) =
        if info.PointerDepth > 0 then
            if info.BaseType.EndsWith("char") then CharPointer
            elif info.BaseType = "void" then VoidPointer
            else TypedPointer info.BaseType
        else ValueType info.BaseType

    // =========================================================================
    // Macro Classification Active Patterns
    // =========================================================================

    /// Compiler-predefined macros that are not header-specific
    let private predefinedMacros = set [ "linux"; "unix"; "i386"; "i686"; "true"; "false" ]

    /// Classify a macro name into categories for filtering.
    /// CompilerBuiltin: __STDC__, __GNUC__, etc. (double underscore bookends)
    /// InternalMacro: _POSIX_SOURCE, _GNU_SOURCE, etc. (leading underscore + uppercase)
    /// PredefinedMacro: linux, unix, etc. (compiler-predefined names)
    /// UserMacro: everything else; these get emitted
    let (|CompilerBuiltin|InternalMacro|PredefinedMacro|UserMacro|) (name: string) =
        if name.StartsWith("__") && name.EndsWith("__") then CompilerBuiltin
        elif name.StartsWith("_") && name.Length > 1 && Char.IsUpper(name.[1]) then InternalMacro
        elif predefinedMacros.Contains(name) then PredefinedMacro
        else UserMacro

    // =========================================================================
    // Numeric Literal Active Pattern
    // =========================================================================

    /// Try to parse a string as an integer literal (decimal or hex).
    /// Uses XParsec parsers from CTypeParser.
    /// Usage: match "0xFF" with IntegerLiteral n -> ... | _ -> ...
    let (|IntegerLiteral|_|) (s: string) : int64 option =
        CTypeParser.tryParseInteger s

    // =========================================================================
    // F# Keyword Active Pattern
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

    /// Decompose a parameter name: strip leading underscores and detect F# keywords.
    /// FSharpKeyword: the stripped name is an F# keyword → needs backtick quoting
    /// CleanName: safe to use as-is
    let (|FSharpKeyword|CleanName|) (name: string) =
        let stripped = if name.StartsWith("__") then name.TrimStart('_') else name
        if fsharpKeywords.Contains(stripped) then FSharpKeyword stripped
        else CleanName stripped

    /// Clean a C parameter name for use in F#.
    /// Strips leading underscores, backtick-quotes F# keywords, and prefixes leading digits.
    let cleanParamName (name: string) : string =
        match name with
        | FSharpKeyword kw -> $"``{kw}``"
        | CleanName n ->
            if n.Length > 0 && System.Char.IsDigit(n.[0]) then $"_{n}"
            else n

    // =========================================================================
    // Opaque Handle Detection
    // =========================================================================

    /// Detect whether a typedef represents an opaque handle.
    /// True when the underlying type is a pointer to a struct not defined in the translation unit.
    /// Matches the canonical C pattern: typedef struct ihipStream_t* hipStream_t;
    let isOpaqueHandleTypedef (knownStructNames: Set<string>) (td: CppParser.TypedefInfo) : bool =
        match CTypeParser.tryParseCType td.UnderlyingType with
        | Some info when info.PointerDepth > 0 ->
            let baseType = info.BaseType.Trim()
            baseType.StartsWith("struct ") &&
            not (knownStructNames.Contains(baseType.Substring(7).Trim()))
        | _ -> false

    // =========================================================================
    // Bitmask Enum Detection
    // =========================================================================

    /// Test whether a 64-bit integer is an exact power of 2.
    let private isPowerOfTwo (v: int64) : bool =
        v > 0L && (v &&& (v - 1L)) = 0L

    /// Detect whether enum values represent a bitmask (flags) pattern.
    /// Three-part test on distinct non-zero values:
    ///   1. At least 3 non-zero values (fewer gives insufficient signal)
    ///   2. Values do NOT form a consecutive integer run (1,2,3,4 is sequential, not flags)
    ///   3. More than 60% are exact powers of 2 (conservative threshold)
    let isBitmaskEnum (values: (string * int64) list) : bool =
        let nonZero =
            values |> List.map snd |> List.distinct |> List.filter (fun v -> v <> 0L) |> List.sort
        if nonZero.Length < 3 then false
        else
            let isConsecutive =
                (List.last nonZero) - (List.head nonZero) + 1L = int64 nonZero.Length
            if isConsecutive then false
            else
                let p2Count = nonZero |> List.filter isPowerOfTwo |> List.length
                float p2Count / float nonZero.Length > 0.6

    // =========================================================================
    // Array Type Active Pattern
    // =========================================================================

    /// Detect and extract array size from a C type string like "uint32_t[4]".
    /// Uses XParsec array size parser from CTypeParser.
    let (|ArrayType|_|) (typeStr: string) : int option =
        CTypeParser.tryParseArraySize typeStr
