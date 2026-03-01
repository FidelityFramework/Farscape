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

// =========================================================================
// Bitmask Enum Detection Tests
// =========================================================================

[<Fact>]
let ``isBitmaskEnum detects clear power-of-2 pattern`` () =
    // hipGraphicsRegisterFlags: 0, 1, 2, 4, 8
    let values = [("None", 0L); ("ReadOnly", 1L); ("WriteDiscard", 2L); ("SurfaceLoadStore", 4L); ("TextureGather", 8L)]
    Assert.True(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum detects all-power-of-2 without zero`` () =
    // hipGraphInstantiateFlags: 1, 2, 4, 8
    let values = [("A", 1L); ("B", 2L); ("C", 4L); ("D", 8L)]
    Assert.True(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum detects large flag enum`` () =
    // hipGraphDebugDotFlags: 1, 4, 8, 16, 32, 64, 128, 256, 512, 1024
    let values = [("A",1L);("B",4L);("C",8L);("D",16L);("E",32L);("F",64L);("G",128L);("H",256L);("I",512L);("J",1024L)]
    Assert.True(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum rejects sequential error codes`` () =
    // hipError_t: 0, 1, 2, 3, 4, 5
    let values = [("Success",0L);("InvalidValue",1L);("MemoryAlloc",2L);("InitError",3L);("Deinitialized",4L);("Disabled",5L)]
    Assert.False(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum rejects small sequential enum`` () =
    // Color: 0, 1, 2 — only 2 non-zero values, below minimum
    let values = [("Red", 0L); ("Green", 1L); ("Blue", 2L)]
    Assert.False(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum rejects mixed sequential with flag bits`` () =
    // __socket_type: 1,2,3,4,5,6,10,524288,2048 — 56% ratio, below threshold
    let values = [("STREAM",1L);("DGRAM",2L);("RAW",3L);("RDM",4L);("SEQPACKET",5L);("DCCP",6L);("PACKET",10L);("CLOEXEC",524288L);("NONBLOCK",2048L)]
    Assert.False(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum rejects consecutive integer run`` () =
    // 1,2,3,4 — consecutive despite 75% power-of-2
    let values = [("A",1L);("B",2L);("C",3L);("D",4L)]
    Assert.False(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum rejects empty values`` () =
    Assert.False(isBitmaskEnum [])

[<Fact>]
let ``isBitmaskEnum rejects single non-zero value`` () =
    let values = [("None", 0L); ("Only", 1L)]
    Assert.False(isBitmaskEnum values)

[<Fact>]
let ``isBitmaskEnum accepts flags with composite value`` () =
    // 1,2,4,7 — composite 7=1|2|4, 3 of 4 non-zero are powers of 2 (75%)
    let values = [("None",0L);("Read",1L);("Write",2L);("Execute",4L);("ReadWrite",7L)]
    Assert.True(isBitmaskEnum values)
