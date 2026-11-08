module Farscape.Tests.ByValueTypedefTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Typedef attributes on by-value parameters and results (issue #181): a by-value type that
// reaches a typedef carrying an attribute is measured by clang in its declaring unit, together
// with the type its descriptor claims (the typedefs' unattributed type). The claim is admitted,
// with clang's measurement recorded beside it, only when the two agree in size and alignment;
// otherwise, or without a measurement, the pilot is refused before any binding is written.
// ═══════════════════════════════════════════════════════════════════

let private parseFixture (relative: string) =
    match CppParser.parseHeader { HeaderFile = fixturePath relative; IncludePaths = []; SystemIncludePaths = []; Defines = []; Verbose = false
                                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false } with
    | Ok declarations -> declarations
    | Error reason -> failwithf "%s did not parse: %s" relative reason

let private functionIn (declarations: CppParser.Declaration list) name =
    declarations |> List.pick (function CppParser.Declaration.Function f when f.Name = name -> Some f | _ -> None)

/// Each measured site: where, the spelling, the attributed typedefs it reaches, the type its
/// descriptor claims, and clang's (size, alignment) of the spelling and of that type.
let private sites (f: CppParser.FunctionDecl) =
    f.ByValueTypedefs |> List.map (fun m ->
        m.Site, m.Spelling, (m.Reached |> List.map (fun (typedef, a) -> typedef, a.Kind)), m.Unattributed,
        m.Measured |> Result.map (fun (spelled, claimed) -> (spelled.Size, spelled.Alignment), (claimed.Size, claimed.Alignment)))

let private noOutput (output: string) =
    Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))

let private bindingSource (output: string) =
    System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))

[<Fact>]
let ``a by-value type reaching an attributed typedef that clang measures as its claimed type is admitted with the measurement`` () =
    let header = fixturePath "by-value-typedefs/admitted.h"
    let declarations = parseFixture "by-value-typedefs/admitted.h"
    let agrees size alignment : Result<(int * int) * (int * int), string> = Ok ((size, alignment), (size, alignment))
    Assert.Equal<_ list>(
        [ "result", "fx_bv_four", ["fx_bv_four", "AlignedAttr"], "int", agrees 4 4
          "parameter 'v'", "fx_bv_four_chain", ["fx_bv_four", "AlignedAttr"], "int", agrees 4 4
          "parameter 'w'", "fx_bv_alias", ["fx_bv_alias", "MayAliasAttr"], "int", agrees 4 4 ],
        sites (functionIn declarations "fx_bv_four_use"))
    Assert.Equal<_ list>(
        [ "parameter 'p'", "fx_bv_pair4", ["fx_bv_pair4", "AlignedAttr"], "struct fx_bv_pair", agrees 8 4 ],
        sites (functionIn declarations "fx_bv_pair_use"))
    // The pointer reaches nothing; its pointee, which a reference projection would claim, is
    // measured as its own site, at clang's alignment 16 against int's 4.
    Assert.Equal<_ list>(
        [ "pointee of parameter 'out'", "fx_bv_wide", ["fx_bv_wide", "AlignedAttr"], "int", Ok ((4, 16), (4, 4)) ],
        sites (functionIn declarations "fx_bv_pointer_use"))
    withPilot [header] ["value_structs = [\"fx_bv_pair\"]"] ["fx_bv_four_use"; "fx_bv_pair_use"; "fx_bv_pointer_use"] (fun result output ->
        match result with
        | Error error -> failwithf "The agreeing by-value typedefs were refused: %s" error
        | Ok _ ->
            let source = bindingSource output
            let measured typedef attribute size alignment claimed =
                $"type reaches typedef '{typedef}' carrying {attribute}; clang measures it at {size} bytes, alignment {alignment}, as '{claimed}'"
            let four = measured "fx_bv_four" "AlignedAttr" 4 4 "int"
            let alias = measured "fx_bv_alias" "MayAliasAttr" 4 4 "int"
            let pair = measured "fx_bv_pair4" "AlignedAttr" 8 4 "struct fx_bv_pair"
            Assert.Contains("{ Name = \"v\"; Type = Integer (Signed, 32); PassBy = Value } // width declared: int under LP64; " + four + "\n", source)
            Assert.Contains("{ Name = \"w\"; Type = Integer (Signed, 32); PassBy = Value } // width declared: int under LP64; " + alias + "\n", source)
            Assert.Contains("ReturnType = Integer (Signed, 32) // width declared: int under LP64; " + four + "\n", source)
            Assert.Contains("{ Name = \"p\"; Type = Named \"fx_bv_pair\"; PassBy = Value } // record: struct fx_bv_pair, laid out by fx_bv_pair.Descriptor; " + pair + "\n", source)
            Assert.Contains("{ Name = \"out\"; Type = Pointer 64; PassBy = Value } // width declared: pointer under LP64\n", source))
    clangConfirms header [
        "sizeof(fx_bv_four) == 4"; "_Alignof(fx_bv_four) == 4"; "_Alignof(fx_bv_four_chain) == 4"
        "sizeof(fx_bv_alias) == 4"; "_Alignof(fx_bv_alias) == 4"
        "sizeof(fx_bv_pair4) == 8"; "_Alignof(fx_bv_pair4) == 4"; "_Alignof(struct fx_bv_pair) == 4"
        "sizeof(fx_bv_wide) == 4"; "_Alignof(fx_bv_wide) == 16" ]

[<Fact>]
let ``a by-value scalar whose typedef clang measures at another alignment than its claimed type is refused before output`` () =
    let header = fixturePath "by-value-typedefs/refused.h"
    clangConfirms header [ "sizeof(fx_bv_wide_int) == 4"; "_Alignof(fx_bv_wide_int) == 16"; "_Alignof(int) == 4" ]
    withPilot [header] [] ["fx_bv_wide_int_use"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9178: by-value type 'fx_bv_wide_int' of the parameter 'v' of function 'fx_bv_wide_int_use' in '{header}' reaches typedef 'fx_bv_wide_int' carrying AlignedAttr, and clang measures 'fx_bv_wide_int' at 4 bytes, alignment 16 but 'int', the type its descriptor claims, at 4 bytes, alignment 4; a descriptor carries no attribute of a typedef. No bindings were written.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``a by-value record whose typedef clang measures at another alignment than the record is refused before output`` () =
    let header = fixturePath "by-value-typedefs/refused.h"
    clangConfirms header [ "sizeof(fx_bv_wide_pair16) == 8"; "_Alignof(fx_bv_wide_pair16) == 16"; "_Alignof(struct fx_bv_wide_pair) == 4" ]
    withPilot [header] ["value_structs = [\"fx_bv_wide_pair\"]"] ["fx_bv_wide_pair_use"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9178: by-value type 'fx_bv_wide_pair16' of the parameter 'p' of function 'fx_bv_wide_pair_use' in '{header}' reaches typedef 'fx_bv_wide_pair16' carrying AlignedAttr, and clang measures 'fx_bv_wide_pair16' at 8 bytes, alignment 16 but 'struct fx_bv_wide_pair', the type its descriptor claims, at 8 bytes, alignment 4; a descriptor carries no attribute of a typedef. No bindings were written.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``a by-value type reaching an attributed typedef that clang cannot measure is refused before output`` () =
    let header = fixturePath "by-value-typedefs/hidden.h"
    Assert.Equal<_ list>(
        [ "parameter 'v'", "fx_bv_hidden", ["fx_bv_hidden", "AlignedAttr"], "int",
          Error "clang measures no size and alignment of 'fx_bv_hidden' in its declaring unit" ],
        sites (functionIn (parseFixture "by-value-typedefs/hidden.h") "fx_bv_hidden_use"))
    withPilot [header] [] ["fx_bv_hidden_use"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9178: by-value type 'fx_bv_hidden' of the parameter 'v' of function 'fx_bv_hidden_use' in '{header}' reaches typedef 'fx_bv_hidden' carrying AlignedAttr, and clang measures no size and alignment of 'fx_bv_hidden' in its declaring unit; a descriptor carries no attribute of a typedef. No bindings were written.",
            result |> Result.map ignore)
        noOutput output)
