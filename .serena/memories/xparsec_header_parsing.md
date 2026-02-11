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
| `CppParser.fs` | Inline `MacroParsers` class + raw header comment extraction + `-fparse-all-comments` AST documentation extraction |
| `TypeMapper.fs` | Inline `pArrayLen` parser for array size extraction |
| `ErrnoModuleGenerator.fs` | Uses filtered macros with documentation to generate Errno module + CError struct |

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

## Raw Header Comment Extraction (Feb 2026)

CppParser.fs now includes a **raw header comment enrichment pipeline**:
1. `clang -H` discovers the include file tree from a root header
2. Each file is read and `#define NAME VALUE /* comment */` lines are parsed for trailing comments
3. Parsed `MacroDecl` records are enriched with `Documentation: string option` from raw header comments
4. This feeds into `ErrnoModuleGenerator` for errno constant descriptions and Errno.describe function

The pipeline also works for non-errno libraries (e.g. curl error codes with `/* description */` comments).

## AST Documentation Extraction via -fparse-all-comments (Feb 2026)

CppParser.fs now passes `-fparse-all-comments` to clang's JSON AST dump. This causes clang to attach
`FullComment` nodes to declarations that have preceding `/* */` or `//` comments. The extraction pipeline:
1. `extractTextFromComment` recursively collects `TextComment.text` values from `FullComment` → `ParagraphComment` → `TextComment` tree
2. `extractDocumentation` finds `FullComment` nodes in a declaration's `inner` array and joins text into a single string
3. `processFunctionDecl`, `processRecordDecl`, `processEnumDecl` all populate `Documentation: string option`
4. `formatDocDecls` in both `FidelityCodeGenerator.fs` and `WrapperCodeGenerator.fs` emits multi-line XML docs: description line + blank line + C signature line

This provides design-time XML documentation for LSP/IDE support on all generated function bindings.

## Tests

194+ unit tests in `tests/Farscape.Tests/Tests.fs` covering all XParsec parsers, active patterns, catamorphism, CodeRenderer, and FidelityCodeGenerator end-to-end.
