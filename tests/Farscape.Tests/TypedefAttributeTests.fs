module Farscape.Tests.TypedefAttributeTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Typedef attributes (issue #175): clang's attributes on a typedef are read at parse and
// classified through AttributeContracts. A record whose field type reaches an attributed typedef
// is laid out only from clang's measurement in its declaring unit, or refused with its exact
// reason; a typedef naming a record whose attribute the record layout dump omits is refused.
// ═══════════════════════════════════════════════════════════════════

let private parseFixture (relative: string) =
    match CppParser.parseHeader { HeaderFile = fixturePath relative; IncludePaths = []; SystemIncludePaths = []; Defines = []; Verbose = false
                                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false } with
    | Ok declarations -> declarations
    | Error reason -> failwithf "%s did not parse: %s" relative reason

let private recordIn (declarations: CppParser.Declaration list) name =
    declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = name -> Some s | _ -> None)

let private typedefIn (declarations: CppParser.Declaration list) name =
    declarations |> List.pick (function CppParser.Declaration.Typedef t when t.Name = name -> Some t | _ -> None)

/// Clang's measurement of the record in bytes: size, alignment and its fields' offsets.
let private measured (s: CppParser.StructDecl) =
    match s.Measured with
    | Some m -> m.SizeBits / 8, m.AlignmentBits / 8, CppParser.fieldOffsetsBits s m |> List.map (fun bits -> bits / 8)
    | None -> failwithf "%s carries no clang measurement" s.Name

let private aligned8 : CppParser.AttributeData = mkAttr "AlignedAttr" [8] None

let private reaching site typedef attribute : CppParser.TypedefAttribute =
    { Site = site; Typedef = typedef; Attribute = attribute }

let private noOutput (output: string) =
    Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))

[<Fact>]
let ``a record whose field type reaches an aligned typedef is measured and emitted at clang's layout`` () =
    let header = fixturePath "typedef-attributes/aligned-field.h"
    let declarations = parseFixture "typedef-attributes/aligned-field.h"
    // The parse reads the typedef's attribute; clang attaches none for packed on a typedef.
    Assert.Equal<CppParser.AttributeData list>([aligned8], (typedefIn declarations "fx_wide_int").Attributes)
    Assert.Empty((typedefIn declarations "fx_wide_chain").Attributes)
    Assert.Empty((typedefIn declarations "fx_packed_int").Attributes)
    // The field types reaching it, directly and through a second typedef, force clang's measurement.
    let reached = [ reaching (CppParser.FieldType "value") "fx_wide_int" aligned8 ]
    for name in ["fx_wide"; "fx_chained"] do
        let record = recordIn declarations name
        Assert.Equal<CppParser.TypedefAttribute list>(reached, record.TypedefAttributes)
        Assert.Equal((16, 8, [0; 8]), measured record)
    // A pointer to the aligned type, and the ignored packed, reach nothing.
    for name in ["fx_pointed"; "fx_ignored"] do
        let record = recordIn declarations name
        Assert.Empty(record.TypedefAttributes)
        Assert.Equal(None, record.Measured)
    withPilot [header] [] ["fx_wide_use"] (fun result output ->
        match result with
        | Error error -> failwithf "The aligned-typedef records were refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            for name in ["fx_wide"; "fx_chained"] do
                let record = recordModule types name
                Assert.Contains($"/// C struct `{name}`: layout measured (clang record layout dump).", types)
                Assert.Contains("let Size = 16\n", record)
                Assert.Contains("let Alignment = 8\n", record)
                Assert.Contains("let valueOffset = 8\n", record)
            let pointed = recordModule types "fx_pointed"
            Assert.Contains("/// C struct `fx_pointed`: layout declared", types)
            Assert.Contains("let Size = 16\n", pointed)
            Assert.Contains("let valueOffset = 8\n", pointed)
            let ignored = recordModule types "fx_ignored"
            Assert.Contains("/// C struct `fx_ignored`: layout declared", types)
            Assert.Contains("let Size = 8\n", ignored)
            Assert.Contains("let Alignment = 4\n", ignored)
            Assert.Contains("let valueOffset = 4\n", ignored))
    clangConfirms header [
        "sizeof(struct fx_wide) == 16"; "_Alignof(struct fx_wide) == 8"; "offsetof(struct fx_wide, value) == 8"
        "sizeof(struct fx_chained) == 16"; "_Alignof(struct fx_chained) == 8"; "offsetof(struct fx_chained, value) == 8"
        "sizeof(struct fx_pointed) == 16"; "_Alignof(struct fx_pointed) == 8"; "offsetof(struct fx_pointed, value) == 8"
        "sizeof(struct fx_ignored) == 8"; "_Alignof(struct fx_ignored) == 4"; "offsetof(struct fx_ignored, value) == 4" ]

[<Fact>]
let ``a record whose field type reaches an aligned typedef and that clang cannot measure is refused before output`` () =
    let header = fixturePath "typedef-attributes/hidden.h"
    let record = recordIn (parseFixture "typedef-attributes/hidden.h") "fx_hidden"
    Assert.Equal<CppParser.TypedefAttribute list>(
        [ reaching (CppParser.FieldType "value") "fx_wide_int" aligned8 ], record.TypedefAttributes)
    Assert.Equal(None, record.Measured)
    withPilot [header] [] ["fx_hidden_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9127: struct 'fx_hidden' field 'value' has a type reaching typedef 'fx_wide_int', which carries layout attribute 'AlignedAttr', and clang has no measurement of 'fx_hidden' in its declaring unit; natural alignment of the declared field types never stands in for the typedef's attribute.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``a record named by a typedef carrying aligned is refused, since its record layout dump omits the typedef`` () =
    let header = fixturePath "typedef-attributes/named.h"
    let record = recordIn (parseFixture "typedef-attributes/named.h") "fx_named_wide"
    Assert.Equal<CppParser.TypedefAttribute list>(
        [ reaching CppParser.RecordName "fx_named_wide" (mkAttr "AlignedAttr" [16] None) ], record.TypedefAttributes)
    // Clang's record layout dump of the record says alignment 4; the typedef's type has 16.
    match CppParser.extractStructLayouts header [] [] ["typedef fx_named_wide"] false with
    | Ok layouts -> Assert.Equal((64, 32), (layouts.["fx_named_wide"].SizeBits, layouts.["fx_named_wide"].AlignmentBits))
    | Error reason -> failwithf "clang's record layout dump failed: %s" reason
    clangConfirms header [ "sizeof(fx_named_wide) == 8"; "_Alignof(fx_named_wide) == 16" ]
    withPilot [header] [] ["fx_named_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9128: struct 'fx_named_wide' is named by typedef 'fx_named_wide', which carries layout attribute 'AlignedAttr'; clang's record layout dump measures the record, not the typedef that names it, so 'fx_named_wide' has no measured layout as the type its name spells.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``a field whose typedef carries may_alias is laid out by clang and records the aliasing fact`` () =
    let header = fixturePath "typedef-attributes/alias.h"
    let record = recordIn (parseFixture "typedef-attributes/alias.h") "fx_aliased"
    Assert.Equal((8, 4, [0; 4]), measured record)
    withPilot [header] [] ["fx_aliased_use"] (fun result output ->
        match result with
        | Error error -> failwithf "The may_alias-typedef record was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            let aliased = recordModule types "fx_aliased"
            Assert.Contains("/// C struct `fx_aliased`: layout measured (clang record layout dump).", types)
            Assert.Contains("let Size = 8\n", aliased)
            Assert.Contains("let valueOffset = 4\n", aliased)
            Assert.Contains(
                $"Documentation = Some \"offset measured; repr declared: int under LP64; type reaches typedef 'fx_alias_int' carrying MayAliasAttr (laid out by clang's measurement of this record): {AttributeContracts.MayAliasFact}\" }}",
                aliased)
            // The fact is the field type's, not the record's.
            Assert.Contains("Documentation = None", aliased)
            Assert.Contains("MayAlias = false\n", aliased))
    clangConfirms header [ "sizeof(struct fx_aliased) == 8"; "_Alignof(struct fx_aliased) == 4"; "offsetof(struct fx_aliased, value) == 4" ]

[<Fact>]
let ``a value struct whose field type reaches an aligned typedef is refused before output`` () =
    let header = fixturePath "typedef-attributes/value.h"
    withPilot [header] ["value_structs = [\"fx_wide_value\"]"] ["fx_wide_value_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: Value struct 'fx_wide_value' field 'value' has a type reaching typedef 'fx_wide_int', which carries attribute 'AlignedAttr' that its Clef record's field type cannot represent; a plain record with scalar or pointer fields is required.",
            result |> Result.map ignore)
        noOutput output)
    clangConfirms header [ "sizeof(struct fx_wide_value) == 16"; "offsetof(struct fx_wide_value, value) == 8" ]

[<Fact>]
let ``two headers whose one record reaches typedefs with different attributes are refused as divergent`` () =
    let aligned = fixturePath "typedef-attributes/divergent-aligned.h"
    let plain = fixturePath "typedef-attributes/divergent-plain.h"
    withPilot [aligned; plain] [] ["fx_div_aligned"; "fx_div_plain"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9116: record 'fx_div' is defined differently by the pilot's headers: '{aligned}' (struct fx_div {{ char tag; fx_div_int value }} (field 'value' reaches typedef 'fx_div_int' AlignedAttr(8))); '{plain}' (struct fx_div {{ char tag; fx_div_int value }}); first difference at its typedef attributes: [field 'value' reaches typedef 'fx_div_int' AlignedAttr(8)] in '{aligned}', [] in '{plain}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        noOutput output)
