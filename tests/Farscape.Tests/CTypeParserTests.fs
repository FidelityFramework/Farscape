module Farscape.Tests.CTypeParserTests

open Xunit
open Farscape.Core

[<Theory>]
[<InlineData("int", "int", 0)>]
[<InlineData("void", "void", 0)>]
[<InlineData("unsigned long int", "unsigned long int", 0)>]
[<InlineData("char *", "char", 1)>]
[<InlineData("void *", "void", 1)>]
[<InlineData("const char *", "char", 1)>]
[<InlineData("unsigned char *", "unsigned char", 1)>]
[<InlineData("const void *", "void", 1)>]
[<InlineData("char **", "char", 2)>]
[<InlineData("__off_t", "__off_t", 0)>]
[<InlineData("size_t", "size_t", 0)>]
let ``pCType parses base type and pointer depth`` (input: string) (expectedBase: string) (expectedDepth: int) =
    match CTypeParser.tryParseCType input with
    | Some info ->
        Assert.Equal(expectedBase, info.BaseType)
        Assert.Equal(expectedDepth, info.PointerDepth)
    | None ->
        Assert.Fail $"Failed to parse C type: '{input}'"

[<Theory>]
[<InlineData("volatile int", "int", 0)>]
[<InlineData("const volatile char *", "char", 1)>]
[<InlineData("__restrict void *", "void", 1)>]
[<InlineData("const __restrict char *", "char", 1)>]
let ``pCType strips qualifiers`` (input: string) (expectedBase: string) (expectedDepth: int) =
    match CTypeParser.tryParseCType input with
    | Some info ->
        Assert.Equal(expectedBase, info.BaseType)
        Assert.Equal(expectedDepth, info.PointerDepth)
    | None ->
        Assert.Fail $"Failed to parse C type: '{input}'"

[<Theory>]
[<InlineData("42", 42L)>]
[<InlineData("0", 0L)>]
[<InlineData("-1", -1L)>]
[<InlineData("0xFF", 255L)>]
[<InlineData("0x1A", 26L)>]
[<InlineData("0X10", 16L)>]
let ``tryParseInteger parses decimal and hex`` (input: string) (expected: int64) =
    match CTypeParser.tryParseInteger input with
    | Some n -> Assert.Equal(expected, n)
    | None -> Assert.Fail $"Failed to parse integer: '{input}'"

[<Theory>]
[<InlineData("hello")>]
[<InlineData("")>]
[<InlineData("0xZZ")>]
[<InlineData("12.34")>]
let ``tryParseInteger rejects non-integers`` (input: string) =
    Assert.True(CTypeParser.tryParseInteger(input).IsNone, $"Should not parse: '{input}'")

[<Theory>]
[<InlineData("uint32_t[4]", 4)>]
[<InlineData("int[16]", 16)>]
[<InlineData("char[256]", 256)>]
let ``tryParseArraySize extracts array dimension`` (input: string) (expected: int) =
    match CTypeParser.tryParseArraySize input with
    | Some n -> Assert.Equal(expected, n)
    | None -> Assert.Fail $"Failed to parse array size: '{input}'"

[<Fact>]
let ``tryParseArraySize returns None for non-arrays`` () =
    Assert.True(CTypeParser.tryParseArraySize("int").IsNone)
    Assert.True(CTypeParser.tryParseArraySize("char *").IsNone)

[<Fact>]
let ``classifyObjectMacroValue detects expressions`` () =
    match CTypeParser.classifyObjectMacroValue "1 + 2" with
    | CppParser.Expression _ -> ()
    | other -> Assert.Fail $"Expected Expression, got {other}"

[<Fact>]
let ``classifyObjectMacroValue detects bitwise expressions`` () =
    match CTypeParser.classifyObjectMacroValue "0x01 << 3" with
    | CppParser.Expression _ -> ()
    | other -> Assert.Fail $"Expected Expression, got {other}"

[<Fact>]
let ``classifyObjectMacroValue detects type casts`` () =
    match CTypeParser.classifyObjectMacroValue "((void*)0)" with
    | CppParser.TypeCast ("void", "0") -> ()
    | other -> Assert.Fail $"Expected TypeCast(void, 0), got {other}"

[<Fact>]
let ``classifyObjectMacroValue passes through simple values`` () =
    match CTypeParser.classifyObjectMacroValue "42" with
    | CppParser.SimpleValue "42" -> ()
    | other -> Assert.Fail $"Expected SimpleValue 42, got {other}"

[<Fact>]
let ``parseMacroLine parses object macros`` () =
    match CTypeParser.parseMacroLine "#define FOO 42" with
    | Some m ->
        Assert.Equal("FOO", m.Name)
        Assert.Equal("42", m.RawValue)
    | None ->
        Assert.Fail "Failed to parse object macro"

[<Fact>]
let ``parseMacroLine parses function-like macros`` () =
    match CTypeParser.parseMacroLine "#define MAX(a, b) ((a) > (b) ? (a) : (b))" with
    | Some m ->
        Assert.Equal("MAX", m.Name)
        match m.Kind with
        | CppParser.FunctionLike (args, _body) ->
            Assert.Equal(2, args.Length)
            Assert.Equal("a", args.[0])
            Assert.Equal("b", args.[1])
        | other -> Assert.Fail $"Expected FunctionLike, got {other}"
    | None ->
        Assert.Fail "Failed to parse function-like macro"

[<Fact>]
let ``parseMacroLine parses empty macros`` () =
    match CTypeParser.parseMacroLine "#define GUARD" with
    | Some m ->
        Assert.Equal("GUARD", m.Name)
        Assert.Equal("", m.RawValue)
    | None ->
        Assert.Fail "Failed to parse empty macro"

[<Fact>]
let ``parseMacroLine rejects non-define lines`` () =
    Assert.True(CTypeParser.parseMacroLine("int x = 5;").IsNone)
    Assert.True(CTypeParser.parseMacroLine("#include <stdio.h>").IsNone)
