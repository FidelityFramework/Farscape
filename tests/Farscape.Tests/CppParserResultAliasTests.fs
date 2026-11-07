module Farscape.Tests.CppParserResultAliasTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Clang built-in aliases in function results (issue #171): read through clang's canonical
// type and its measured size and signedness, never a hand-added profile row
// ═══════════════════════════════════════════════════════════════════

let private aliasesOf (relative: string) =
    match CppParser.parseHeaderFull {
            HeaderFile = fixturePath relative; IncludePaths = []; Defines = []; IncludeRoot = None
            MacroPrefixes = []; IncludeMacros = false; Verbose = false; CppMode = false } with
    | Ok result -> result.Aliases
    | Error error -> failwithf "Fixture %s did not parse: %s" relative error

[<Fact>]
let ``a built-in alias in a result is read through clang's canonical type and measurement`` () =
    // strlen's result is spelled `__size_t` by clang; fx_len's result is `unsigned long`, no alias.
    let expected : CppParser.ClangTypeAlias =
        { Name = "__size_t"; Function = "strlen"; Canonical = Ok ("unsigned long", { Bits = 64; IsSigned = false }) }
    Assert.Equal<CppParser.ClangTypeAlias list>([ expected ], aliasesOf "result-aliases/builtin-result.h")

[<Fact>]
let ``an alias clang cannot evaluate keeps clang's reason`` () =
    let alias = aliasesOf "result-aliases/hidden-result.h" |> List.exactlyOne
    Assert.Equal("__size_t", alias.Name)
    Assert.Equal(Error (CppParser.ProbeRefused "clang could not evaluate the result type of 'strlen' at file scope: called object type 'int' is not a function or function pointer"), alias.Canonical)

[<Fact>]
let ``an alias measurement contradicting its canonical type's profile row is refused`` () =
    let strlen = mkFunc "strlen" "__size_t" ["s", "const char *"]
    let alias : CppParser.ClangTypeAlias =
        { Name = "__size_t"; Function = "strlen"; Canonical = Ok ("unsigned long", { Bits = 32; IsSigned = false }) }
    Assert.Equal(
        Error "FS9118: clang measures the result of function 'strlen' in 'a.h' ('__size_t', canonically 'unsigned long') as unsigned 32-bit, but the LP64 profile row for 'unsigned long' is unsigned 64-bit; the profile and the target must agree. No bindings were written.",
        (BindingGenerator.requireCanonicalAliases Types.LP64 (Map.ofList ["a.h", [alias]]) ["a.h", [CppParser.Declaration.Function strlen]] [strlen] |> refusalText))
    let agreeing = { alias with Canonical = Ok ("unsigned long", { Bits = 64; IsSigned = false }) }
    Assert.Equal(Ok (), BindingGenerator.requireCanonicalAliases Types.LP64 (Map.ofList ["a.h", [agreeing]]) ["a.h", [CppParser.Declaration.Function strlen]] [strlen])

// The probe types an unevaluated call: these are its supported parameter shapes, and the
// shape it cannot type (an incomplete record taken by value).

[<Fact>]
let ``a result alias of a function with no arguments is canonicalized`` () =
    let expected : CppParser.ClangTypeAlias =
        { Name = "__uint128_t"; Function = "fx_wide_zero"; Canonical = Ok ("unsigned __int128", { Bits = 128; IsSigned = false }) }
    Assert.Equal<CppParser.ClangTypeAlias list>([ expected ], aliasesOf "result-aliases/noarg-result.h")

[<Fact>]
let ``a result alias of a variadic function is canonicalized from its fixed arguments`` () =
    let expected : CppParser.ClangTypeAlias =
        { Name = "__int128_t"; Function = "fx_wide_sum"; Canonical = Ok ("__int128", { Bits = 128; IsSigned = true }) }
    Assert.Equal<CppParser.ClangTypeAlias list>([ expected ], aliasesOf "result-aliases/variadic-result.h")

[<Fact>]
let ``a result alias of a function taking an incomplete record by value names that parameter`` () =
    let alias = aliasesOf "result-aliases/incomplete-param.h" |> List.exactlyOne
    Assert.Equal("__int128_t", alias.Name)
    match alias.Canonical with
    | Error (CppParser.IncompleteParameter (parameter, spelled, reason)) ->
        Assert.Equal("value", parameter)
        Assert.Equal("struct fx_opaque", spelled)
        Assert.Equal("invalid application of 'sizeof' to an incomplete type 'struct fx_opaque'", reason)
    | other -> failwithf "Expected the incomplete parameter, got %A" other

[<Fact>]
let ``a long double result is canonicalized with its 16-byte storage`` () =
    // clang spells the result `typeof (1.L)`; the parse keeps `typeof` as the result spelling.
    let expected : CppParser.ClangTypeAlias =
        { Name = "typeof"; Function = "fx_extended"; Canonical = Ok ("long double", { Bits = 128; IsSigned = true }) }
    Assert.Equal<CppParser.ClangTypeAlias list>([ expected ], aliasesOf "result-aliases/long-double-result.h")
