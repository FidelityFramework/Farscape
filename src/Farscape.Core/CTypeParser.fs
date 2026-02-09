namespace Farscape.Core

open System
open XParsec
open XParsec.Parsers
open XParsec.Combinators
open XParsec.CharParsers

/// XParsec-based parsers for C type strings, macro definitions, and numeric literals.
///
/// These parsers replace all Regex usage in macro parsing and C type decomposition.
/// They are consumed by ActivePatterns.fs which exposes them as F# active patterns.
module CTypeParser =

    // =========================================================================
    // Structured Types
    // =========================================================================

    /// Parsed representation of a C type string.
    /// Produced by pCType from strings like "const char *", "unsigned long int", "void*"
    type CTypeInfo = {
        BaseType: string
        PointerDepth: int
    }

    // =========================================================================
    // Macro Value Classification (pure function, no parsers)
    // =========================================================================

    /// Classify an object macro's value into MacroKind
    let classifyObjectMacroValue (value: string) : CppParser.MacroKind =
        // Type cast pattern: ((Type*)value)
        if value.StartsWith("((") && value.Contains("*)") && value.EndsWith(")") then
            let inner = value.Substring(2, value.Length - 3)
            match inner.IndexOf("*)") with
            | -1 -> CppParser.SimpleValue value
            | idx ->
                let typeName = inner.Substring(0, idx).Trim()
                let castValue = inner.Substring(idx + 2).Trim()
                CppParser.TypeCast (typeName, castValue)
        elif value.Contains("+") || value.Contains("-") || value.Contains("<<") ||
             value.Contains(">>") || value.Contains("|") || value.Contains("&") then
            CppParser.Expression value
        else
            CppParser.SimpleValue value

    // =========================================================================
    // XParsec Parsers (generic class, same pattern as XParsec.Json.JsonParsers)
    // =========================================================================

    type Parsers<'Input, 'InputSlice
        when 'Input :> IReadable<char, 'InputSlice> and 'InputSlice :> IReadable<char, 'InputSlice>>() =

        // -- Atomic parsers --

        static let pIdentifier =
            many1Chars (satisfyL (fun c -> Char.IsLetterOrDigit c || c = '_') "identifier char")

        static let pQualifier =
            choice [
                stringReturn "__extension__ " ()
                stringReturn "__extension__" ()
                stringReturn "__restrict " ()
                stringReturn "__restrict" ()
                stringReturn "restrict " ()
                stringReturn "restrict" ()
                stringReturn "volatile " ()
                stringReturn "volatile" ()
                stringReturn " volatile" ()
                stringReturn "const " ()
                stringReturn "const" ()
                stringReturn " const" ()
            ]

        static let pTypeWord =
            many1Chars (satisfyL (fun c -> Char.IsLetterOrDigit c || c = '_') "type char")

        // -- C Type parser --

        static let pCType =
            parser {
                do! skipMany pQualifier
                let! firstWord = pTypeWord
                let! restWords = many (spaces1 >>. pTypeWord)
                do! spaces
                let! stars = many (skipChar '*')
                let baseType =
                    if restWords.Length = 0 then firstWord
                    else firstWord + " " + (restWords |> Seq.toArray |> String.concat " ")
                return { BaseType = baseType; PointerDepth = stars.Length }
            }

        // -- Macro parsers --

        static let pFunctionLikeMacro =
            parser {
                let! name = pIdentifier
                do! skipChar '('
                let! args, _ = sepBy (spaces >>. pIdentifier .>> spaces) (skipChar ',')
                do! skipChar ')'
                do! spaces1
                let! body = manyChars (satisfyL (fun _ -> true) "body char")
                let macro : CppParser.MacroDecl = {
                    Name = name
                    Kind = CppParser.FunctionLike (Seq.toList args, body)
                    RawValue = body
                }
                return macro
            }

        static let pObjectMacro =
            parser {
                let! name = pIdentifier
                do! spaces1
                let! value = manyChars (satisfyL (fun _ -> true) "value char")
                let trimmed = value.Trim()
                let macro : CppParser.MacroDecl = {
                    Name = name
                    Kind = classifyObjectMacroValue trimmed
                    RawValue = trimmed
                }
                return macro
            }

        static let pEmptyMacro =
            parser {
                let! name = pIdentifier
                do! eof
                let macro : CppParser.MacroDecl = {
                    Name = name
                    Kind = CppParser.SimpleValue ""
                    RawValue = ""
                }
                return macro
            }

        static let pMacroLine =
            parser {
                do! pstring "#define " >>% ()
                do! spaces
                return! choice [ pFunctionLikeMacro; pObjectMacro; pEmptyMacro ]
            }

        // -- Numeric literal parsers --

        static let pHexInt64 =
            parser {
                do! (pstring "0x" <|> pstring "0X") >>% ()
                let! hex = many1Chars (satisfyL (fun c -> Char.IsAsciiHexDigit c) "hex digit")
                return Int64.Parse(hex, Globalization.NumberStyles.HexNumber)
            }

        static let pIntegerLiteral = choice [ pHexInt64; pint64 ] .>> eof

        // -- Array size parser --

        static let pArraySize =
            skipMany (satisfyL (fun c -> c <> '[') "non-bracket")
            >>. (skipChar '[' >>. pint32 .>> skipChar ']')

        // -- Public members --

        static member CType = pCType
        static member MacroLine = pMacroLine
        static member IntegerLiteral = pIntegerLiteral
        static member ArraySize = pArraySize

    // =========================================================================
    // Public API Functions
    // =========================================================================

    /// Concrete instantiation for string-based parsing
    type private P = Parsers<ReadableString, ReadableStringSlice>

    /// Parse a macro line from a string. Returns None if the line isn't a #define.
    let parseMacroLine (line: string) : CppParser.MacroDecl option =
        if not (line.StartsWith("#define ")) then None
        else
            let reader = Reader.ofString line ()
            match P.MacroLine reader with
            | Ok result -> Some result.Parsed
            | Error _ -> None

    /// Try to parse a string as an integer literal. Returns None if not a valid integer.
    let tryParseInteger (s: string) : int64 option =
        let trimmed = s.Trim()
        if String.IsNullOrEmpty trimmed then None
        else
            let reader = Reader.ofString trimmed ()
            match P.IntegerLiteral reader with
            | Ok result -> Some result.Parsed
            | Error _ -> None

    /// Try to extract array size from a type string like "uint32_t[4]"
    let tryParseArraySize (typeStr: string) : int option =
        let reader = Reader.ofString typeStr ()
        match P.ArraySize reader with
        | Ok result -> Some result.Parsed
        | Error _ -> None

    /// Try to parse a C type string into structured CTypeInfo.
    let tryParseCType (s: string) : CTypeInfo option =
        let trimmed = s.Trim()
        if String.IsNullOrEmpty trimmed then None
        else
            let reader = Reader.ofString trimmed ()
            match P.CType reader with
            | Ok result -> Some result.Parsed
            | Error _ -> None
