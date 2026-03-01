module Farscape.Tests.ActivePatternsTests

open Xunit
open Farscape.Core
open ActivePatterns

[<Theory>]
[<InlineData("char *")>]
[<InlineData("const char *")>]
[<InlineData("unsigned char *")>]
let ``CharPointer matches char pointer types`` (input: string) =
    match input with
    | ParsedCType info ->
        match info with
        | CharPointer -> ()
        | other -> Assert.Fail $"Expected CharPointer for '{input}', got {other}"
    | _ -> Assert.Fail $"Failed to parse: '{input}'"

[<Fact>]
let ``CharPointer does NOT match wchar_t pointer`` () =
    match "wchar_t *" with
    | ParsedCType info ->
        match info with
        | CharPointer -> Assert.Fail "wchar_t * should NOT match CharPointer"
        | TypedPointer _ -> () // correct; wchar_t is a typed pointer
        | other -> Assert.Fail $"Unexpected: {other}"
    | _ -> Assert.Fail "Failed to parse wchar_t *"

[<Fact>]
let ``VoidPointer matches void pointer`` () =
    match "void *" with
    | ParsedCType info ->
        match info with
        | VoidPointer -> ()
        | other -> Assert.Fail $"Expected VoidPointer, got {other}"
    | _ -> Assert.Fail "Failed to parse void *"

[<Fact>]
let ``ValueType matches non-pointer types`` () =
    match "int" with
    | ParsedCType info ->
        match info with
        | ValueType "int" -> ()
        | other -> Assert.Fail $"Expected ValueType int, got {other}"
    | _ -> Assert.Fail "Failed to parse int"

[<Theory>]
[<InlineData("__STDC__")>]
[<InlineData("__GNUC__")>]
[<InlineData("__FILE__")>]
let ``CompilerBuiltin matches double-underscore bookended names`` (name: string) =
    match name with
    | CompilerBuiltin -> ()
    | _ -> Assert.Fail $"Expected CompilerBuiltin for '{name}'"

[<Theory>]
[<InlineData("_POSIX_SOURCE")>]
[<InlineData("_GNU_SOURCE")>]
let ``InternalMacro matches underscore-uppercase names`` (name: string) =
    match name with
    | InternalMacro -> ()
    | _ -> Assert.Fail $"Expected InternalMacro for '{name}'"

[<Theory>]
[<InlineData("linux")>]
[<InlineData("unix")>]
let ``PredefinedMacro matches platform names`` (name: string) =
    match name with
    | PredefinedMacro -> ()
    | _ -> Assert.Fail $"Expected PredefinedMacro for '{name}'"

[<Theory>]
[<InlineData("FOO")>]
[<InlineData("MY_CONSTANT")>]
[<InlineData("EXIT_SUCCESS")>]
let ``UserMacro matches everything else`` (name: string) =
    match name with
    | UserMacro -> ()
    | _ -> Assert.Fail $"Expected UserMacro for '{name}'"

[<Fact>]
let ``IntegerLiteral parses decimal`` () =
    match "42" with
    | IntegerLiteral 42L -> ()
    | IntegerLiteral n -> Assert.Fail $"Expected 42, got {n}"
    | _ -> Assert.Fail "Failed to match IntegerLiteral"

[<Fact>]
let ``IntegerLiteral parses hex`` () =
    match "0xFF" with
    | IntegerLiteral 255L -> ()
    | IntegerLiteral n -> Assert.Fail $"Expected 255, got {n}"
    | _ -> Assert.Fail "Failed to match IntegerLiteral"

[<Fact>]
let ``IntegerLiteral rejects non-numeric`` () =
    match "hello" with
    | IntegerLiteral _ -> Assert.Fail "Should not match non-numeric"
    | _ -> ()

[<Fact>]
let ``FSharpKeyword detects keywords`` () =
    match "base" with
    | FSharpKeyword "base" -> ()
    | _ -> Assert.Fail "Expected FSharpKeyword for 'base'"

[<Fact>]
let ``FSharpKeyword strips leading underscores`` () =
    match "__base" with
    | FSharpKeyword "base" -> ()
    | _ -> Assert.Fail "Expected FSharpKeyword for '__base'"

[<Fact>]
let ``CleanName for non-keywords`` () =
    match "myParam" with
    | CleanName "myParam" -> ()
    | _ -> Assert.Fail "Expected CleanName for 'myParam'"

[<Fact>]
let ``cleanParamName backtick-quotes keywords`` () =
    Assert.Equal("``base``", cleanParamName "base")
    Assert.Equal("``base``", cleanParamName "__base")
    Assert.Equal("count", cleanParamName "count")

[<Fact>]
let ``ArrayType extracts size`` () =
    match "uint32_t[4]" with
    | ArrayType 4 -> ()
    | ArrayType n -> Assert.Fail $"Expected 4, got {n}"
    | _ -> Assert.Fail "Failed to match ArrayType"
