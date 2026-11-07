module Farscape.Tests.UnnamedBitFieldTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Unnamed bit-fields (issue #176): `int : 3;` pads a record and the zero-width `int : 0;` moves
// the next member to its type's boundary. Each is kept as padding at its member position, the
// record is laid out only from clang's measurement in its declaring unit, and a record clang
// does not measure is refused with its exact reason: an unnamed bit-field is never dropped.
// ═══════════════════════════════════════════════════════════════════

let private parseFixture (relative: string) =
    match CppParser.parseHeader { HeaderFile = fixturePath relative; IncludePaths = []; Defines = []; Verbose = false
                                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false } with
    | Ok declarations -> declarations
    | Error reason -> failwithf "%s did not parse: %s" relative reason

let private recordIn (declarations: CppParser.Declaration list) name =
    declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = name -> Some s | _ -> None)

/// Clang's measurement in bytes: size, alignment, every member offset (unnamed bit-fields
/// included), and the fields' offsets.
let private measured (s: CppParser.StructDecl) =
    match s.Measured with
    | Some m ->
        m.SizeBits / 8, m.AlignmentBits / 8, m.FieldOffsetsBits |> List.map (fun bits -> bits / 8),
        CppParser.fieldOffsetsBits s m |> List.map (fun bits -> bits / 8)
    | None -> failwithf "%s carries no clang measurement" s.Name

let private noOutput (output: string) =
    Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))

[<Fact>]
let ``unnamed bit-fields are kept as padding and their records emitted at clang's layout`` () =
    let header = fixturePath "unnamed-bitfields/padding.h"
    let declarations = parseFixture "unnamed-bitfields/padding.h"
    let padding (s: CppParser.StructDecl) = s.UnnamedBitFields |> List.map (fun b -> b.Position, b.Type, b.Width)
    let fields (s: CppParser.StructDecl) = s.Fields |> List.map _.Name
    let pad = recordIn declarations "fx_pad"
    Assert.Equal<string list>(["a"; "b"], fields pad)
    Assert.Equal<(int * string * int option) list>([1, "int", Some 3], padding pad)
    Assert.Equal((3, 1, [0; 1; 2], [0; 2]), measured pad)
    let zero = recordIn declarations "fx_zero"
    Assert.Equal<(int * string * int option) list>([1, "int", Some 0], padding zero)
    Assert.Equal((5, 1, [0; 4; 4], [0; 4]), measured zero)
    let wide = recordIn declarations "fx_zero_wide"
    Assert.Equal<(int * string * int option) list>([1, "long long", Some 0], padding wide)
    Assert.Equal((9, 1, [0; 8; 8], [0; 8]), measured wide)
    // In an unnamed record, through the anonymous-record naming of #159.
    let inner = recordIn declarations "fx_nested_inner"
    Assert.Equal<string list>(["b"; "c"], fields inner)
    Assert.Equal<(int * string * int option) list>([1, "int", Some 0], padding inner)
    Assert.Equal((5, 1, [0; 4; 4], [0; 4]), measured inner)
    Assert.Equal((6, 1, [0; 1], [0; 1]), measured (recordIn declarations "fx_nested"))
    // After a C11 anonymous member (#173), which is clang's second member.
    let mem = recordIn declarations "fx_member_pad"
    Assert.Equal<string list>(["a"; "anonymous1"; "d"], fields mem)
    Assert.Equal<(int * string * int option) list>([2, "int", Some 0], padding mem)
    Assert.Equal((6, 2, [0; 2; 4; 4], [0; 2; 4]), measured mem)
    withPilot [header] [] ["fx_pad_use"] (fun result output ->
        match result with
        | Error error -> failwithf "The padded records were refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            for name, size, offsets in [ "fx_pad", 3, ["a", 0; "b", 2]
                                         "fx_zero", 5, ["a", 0; "b", 4]
                                         "fx_zero_wide", 9, ["a", 0; "b", 8]
                                         "fx_nested_inner", 5, ["b", 0; "c", 4]
                                         "fx_nested", 6, ["a", 0; "inner", 1]
                                         "fx_member_pad", 6, ["a", 0; "anonymous1", 2; "d", 4] ] do
                let record = recordModule types name
                Assert.Contains($"/// C struct `{name}`: layout measured (clang record layout dump).", types)
                Assert.Contains($"let Size = {size}\n", record)
                let alignment = if name = "fx_member_pad" then 2 else 1
                Assert.Contains($"let Alignment = {alignment}\n", record)
                for field, offset in offsets do
                    Assert.Contains($"let {field}Offset = {offset}\n", record)
                // Padding is no field of the descriptor.
                Assert.Equal(offsets.Length, record.Split("\"; Offset = ").Length - 1))
    clangConfirms header [
        "sizeof(struct fx_pad) == 3"; "_Alignof(struct fx_pad) == 1"; "offsetof(struct fx_pad, b) == 2"
        "sizeof(struct fx_zero) == 5"; "_Alignof(struct fx_zero) == 1"; "offsetof(struct fx_zero, b) == 4"
        "sizeof(struct fx_zero_wide) == 9"; "_Alignof(struct fx_zero_wide) == 1"; "offsetof(struct fx_zero_wide, b) == 8"
        "sizeof(struct fx_nested) == 6"; "offsetof(struct fx_nested, inner) == 1"
        "sizeof(((struct fx_nested *)0)->inner) == 5"; "offsetof(struct fx_nested, inner.c) == 5"
        "sizeof(struct fx_member_pad) == 6"; "_Alignof(struct fx_member_pad) == 2"; "offsetof(struct fx_member_pad, c) == 2"
        "offsetof(struct fx_member_pad, d) == 4" ]

[<Fact>]
let ``a record carrying an unnamed bit-field that clang cannot measure is refused before output`` () =
    let header = fixturePath "unnamed-bitfields/hidden.h"
    let record = recordIn (parseFixture "unnamed-bitfields/hidden.h") "fx_hidden_pad"
    Assert.Equal<(int * string * int option) list>([1, "int", Some 0], record.UnnamedBitFields |> List.map (fun b -> b.Position, b.Type, b.Width))
    Assert.Equal(None, record.Measured)
    withPilot [header] [] ["fx_hidden_pad_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9129: struct 'fx_hidden_pad' carries member 2, the unnamed bit-field 'int : 0', which moves bytes natural alignment of the declared field types cannot place, and clang has no measurement of 'fx_hidden_pad' in its declaring unit; an unnamed bit-field is never dropped from its record.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``a value struct carrying an unnamed bit-field is refused before output`` () =
    let header = fixturePath "unnamed-bitfields/value.h"
    withPilot [header] ["value_structs = [\"fx_pad_value\"]"] ["fx_pad_value_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: Value struct 'fx_pad_value' carries member 2, the unnamed bit-field 'int : 3', which its Clef record of named fields cannot represent; a plain record with scalar or pointer fields is required.",
            result |> Result.map ignore)
        noOutput output)
    clangConfirms header [ "sizeof(struct fx_pad_value) == 3"; "offsetof(struct fx_pad_value, b) == 2" ]

[<Fact>]
let ``a may_alias record carrying an unnamed bit-field has no faithful copy and is refused`` () =
    let header = fixturePath "unnamed-bitfields/alias.h"
    let record = recordIn (parseFixture "unnamed-bitfields/alias.h") "fx_alias_pad"
    Assert.Equal(
        Some (CppParser.UnattributedUnsynthesized "member 2, the unnamed bit-field 'int : 3' has no member name a copy could read its type through"),
        record.Unattributed)
    withPilot [header] [] ["fx_alias_pad_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9121: struct 'fx_alias_pad' carries layout-neutral attribute 'MayAliasAttr', but no faithful copy without the attribute can be synthesized for clang to measure: member 2, the unnamed bit-field 'int : 3' has no member name a copy could read its type through; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``two headers whose one record differs only in an unnamed bit-field are refused as divergent`` () =
    let padded = fixturePath "unnamed-bitfields/divergent-padded.h"
    let plain = fixturePath "unnamed-bitfields/divergent-plain.h"
    withPilot [padded; plain] [] ["fx_div_padded"; "fx_div_plain"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9116: record 'fx_div_pad' is defined differently by the pilot's headers: '{padded}' (struct fx_div_pad {{ char a; char b }} (member 2, the unnamed bit-field 'int : 3')); '{plain}' (struct fx_div_pad {{ char a; char b }}); first difference at its unnamed bit-fields: [member 2, the unnamed bit-field 'int : 3'] in '{padded}', [] in '{plain}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        noOutput output)
