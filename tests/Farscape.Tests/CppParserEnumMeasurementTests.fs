module Farscape.Tests.CppParserEnumMeasurementTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Enum representations measured by clang in the declaring unit (issue #53)
// ═══════════════════════════════════════════════════════════════════

let private parse cppMode (relative: string) =
    CppParser.parseHeader {
        HeaderFile = fixturePath relative; IncludePaths = []; Defines = []; IncludeRoot = None
        MacroPrefixes = []; IncludeMacros = false; Verbose = false; CppMode = cppMode }

let private parseC = parse false

let private measuredIn cppMode (relative: string) =
    match parse cppMode relative with
    | Error error -> failwithf "Fixture %s did not parse: %s" relative error
    | Ok decls ->
        decls |> List.choose (function CppParser.Declaration.Enum e when e.Name <> "" -> Some (e.Name, e.Representation) | _ -> None) |> Map.ofList

let private measured = measuredIn false

let private repr bits isSigned = CppParser.ClangMeasured { Bits = bits; IsSigned = isSigned }

[<Fact>]
let ``ordinary enums carry clang's chosen width and signedness`` () =
    let enums = measured "enum-representation/ordinary.h"
    Assert.Equal(repr 32 false, enums.["fx_plain"])
    Assert.Equal(repr 32 true, enums.["fx_negative"])
    // A typedef-named anonymous enum is measured through its typedef spelling.
    Assert.Equal(repr 32 false, enums.["fx_anon_t"])

[<Fact>]
let ``a packed enum is measured at clang's one-byte width`` () =
    Assert.Equal(repr 8 false, (measured "enum-representation/packed.h").["fx_packed"])

[<Fact>]
let ``an enumerator beyond 32 bits is measured at clang's 64-bit width`` () =
    let enums = measured "enum-representation/wide.h"
    Assert.Equal(repr 64 false, enums.["fx_wide"])

[<Fact>]
let ``an enum clang cannot complete stays unmeasured`` () =
    Assert.Equal(CppParser.ClangUnmeasured, (measured "enum-representation/unmeasured.h").["fx_opaque"])

[<Fact>]
let ``an enumerator beyond the signed 64-bit carrier is refused, not resequenced`` () =
    Assert.Equal(
        Error "Failed to parse clang output: FS9105: enumerator 'FX_HUGE' has value 18446744073709551615, outside the signed 64-bit range the enum representation carries; a wider enumerator representation contract is required.",
        parseC "enum-representation/huge.h" |> Result.map ignore)

[<Fact>]
let ``C++ scoped and fixed-underlying-type enums are measured by clang`` () =
    let enums = measuredIn true "enum-representation/scoped.hpp"
    Assert.Equal(repr 8 false, enums.["fx_scoped"])
    Assert.Equal(repr 16 true, enums.["fx_scoped_signed"])
    // A scoped enum without a written type has clang's int.
    Assert.Equal(repr 32 true, enums.["fx_scoped_default"])
    Assert.Equal(repr 32 false, enums.["fx_cpp_plain"])

[<Fact>]
let ``a C++ scoped enum the probe cannot name stays unmeasured`` () =
    Assert.Equal(CppParser.ClangUnmeasured, (measuredIn true "enum-representation/scoped-hidden.hpp").["fx_hidden"])
