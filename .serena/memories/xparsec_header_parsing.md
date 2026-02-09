# XParsec in Farscape: Actual Implementation (February 2026)

## Architecture

Farscape uses a **two-stage parsing strategy**:

1. **Stage 1: Clang two-pass parsing** (`CppParser.fs`): Clang produces JSON AST + macro extraction. Clang does the heavy lifting for C/C++ syntax. XParsec is NOT used at this layer.

2. **Stage 2: XParsec post-processing** (`CTypeParser.fs`, `ActivePatterns.fs`): XParsec parsers decompose C type strings, macro values, and numeric literals extracted from Stage 1.

## XParsec Generic Class Pattern

Parsers are defined via a generic class (same pattern as `XParsec.Json.JsonParsers`):

```fsharp
type Parsers<'Input, 'InputSlice
    when 'Input :> IReadable<char, 'InputSlice>
    and 'InputSlice :> IReadable<char, 'InputSlice>>() =

    static let pQualifier = choice [ stringReturn "__restrict " (); ... ]
    static let pTypeWord = many1Chars (satisfyL (fun c -> Char.IsLetterOrDigit c || c = '_') "type char")

    static let pCType =
        parser {
            do! skipMany pQualifier
            let! firstWord = pTypeWord
            let! restWords = many (spaces1 >>. pTypeWord)
            do! spaces
            let! stars = many (skipChar '*')
            let baseType = if restWords.Length = 0 then firstWord
                           else firstWord + " " + (restWords |> Seq.toArray |> String.concat " ")
            return { BaseType = baseType; PointerDepth = stars.Length }
        }

    static member CType = pCType
```

Concrete instantiation: `type private P = Parsers<ReadableString, ReadableStringSlice>`

### CRITICAL: Never use `let inline` for parser definitions
Inline functions can't be passed as first-class values to combinators like `skipMany`, `many`, `choice`. Use `static let` in generic class instead.

## API Notes

- `many` returns `ImmutableArray<'A>`, NOT `'a list`
- `sepBy` returns `struct (ImmutableArray<'A> * ImmutableArray<'B>)`
- `manyChars`/`many1Chars` return `string`
- ParseResult: match as `Ok result -> result.Parsed`
- Invocation: `myParser reader` (parsers are plain functions)

## Files

| File | Purpose |
|------|---------|
| `CTypeParser.fs` | XParsec parsers: `pCType`, `pMacroLine`, `pIntegerLiteral`, `pArraySize` |
| `ActivePatterns.fs` | XParsec-backed active patterns: `ParsedCType`, `IntegerLiteral`, `ArrayType` |
| `CppParser.fs` | Inline `MacroParsers` class for macro line parsing (compile order constraint) |
| `TypeMapper.fs` | Inline `pArrayLen` parser for array size extraction |

## Compile Order Constraint

CppParser.fs compiles BEFORE CTypeParser.fs and ActivePatterns.fs. Therefore CppParser defines its own XParsec parsers inline via a private `MacroParsers` generic class. Other files can reference CTypeParser and ActivePatterns freely.

## Type Info

```fsharp
type CTypeInfo = { BaseType: string; PointerDepth: int }
```

## Key Decisions

- `CharPointer` active pattern uses `EndsWith("char")` NOT `Contains("char")`; excludes `wchar_t`
- Function pointer types (containing `(*)`) detected by string check before XParsec parsing; always map to `nativeint`
- Typedef-resolved types also checked for `(*)` to handle `__compar_fn_t` → `int (*)(const void *, const void *)`

## Tests

89 real unit tests in `tests/Farscape.Tests/Tests.fs` covering all XParsec parsers, active patterns, catamorphism, CodeRenderer, and FidelityCodeGenerator end-to-end.
