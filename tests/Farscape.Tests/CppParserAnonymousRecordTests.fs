module Farscape.Tests.CppParserAnonymousRecordTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Unnamed records typed into named fields (issue #159): named from parent and field,
// measured by clang in the declaring unit, never flattened or laid out from their fields
// ═══════════════════════════════════════════════════════════════════

let private parse (relative: string) =
    CppParser.parseHeader {
        HeaderFile = fixturePath relative; IncludePaths = []; Defines = []; IncludeRoot = None
        MacroPrefixes = []; IncludeMacros = false; Verbose = false; CppMode = false }

let private records (relative: string) =
    match parse relative with
    | Error error -> failwithf "Fixture %s did not parse: %s" relative error
    | Ok decls ->
        decls |> List.choose (function CppParser.Declaration.Struct s when s.Name <> "" -> Some (s.Name, s) | _ -> None) |> Map.ofList

let private layoutOf (s: CppParser.StructDecl) =
    match s.Measured with
    | Some m -> m.SizeBits / 8, m.AlignmentBits / 8, m.FieldOffsetsBits |> List.map (fun bits -> bits / 8)
    | None -> failwithf "Record %s carries no clang measurement" s.Name

[<Fact>]
let ``an unnamed field record becomes a record named from its parent and field`` () =
    let found = records "anonymous-records/wide-counter.h"
    let counter = found.["fx_wide_counter"]
    Assert.True(counter.IsUnion)
    Assert.Equal<string list>(["__value64"; "__value32"], counter.Fields |> List.map _.Name)
    Assert.Equal("struct fx_wide_counter___value32", (counter.Fields |> List.find (fun f -> f.Name = "__value32")).Type)
    let halves = found.["fx_wide_counter___value32"]
    Assert.False(halves.IsUnion)
    Assert.Equal<string list>(["__low"; "__high"], halves.Fields |> List.map _.Name)
    let origin = halves.Anonymous |> Option.defaultWith (fun () -> failwith "no origin")
    Assert.Equal("fx_wide_counter", origin.Parent)
    Assert.Equal("__value32", origin.Field)
    Assert.Equal($"""{fixturePath "anonymous-records/wide-counter.h"}:6:3""", origin.Location)

[<Fact>]
let ``an anonymous record and its parent carry clang's measured layout`` () =
    let found = records "anonymous-records/wide-counter.h"
    Assert.Equal((8, 8, [0; 0]), layoutOf found.["fx_wide_counter"])
    Assert.Equal((8, 4, [0; 4]), layoutOf found.["fx_wide_counter___value32"])
    clangConfirms (fixturePath "anonymous-records/wide-counter.h") [
        "sizeof(fx_wide_counter) == 8"; "_Alignof(fx_wide_counter) == 8"
        "offsetof(fx_wide_counter, __value64) == 0"; "offsetof(fx_wide_counter, __value32) == 0"
        "sizeof(((fx_wide_counter*)0)->__value32) == 8"; "_Alignof(__typeof__(((fx_wide_counter*)0)->__value32)) == 4"
        "offsetof(fx_wide_counter, __value32.__low) == 0"; "offsetof(fx_wide_counter, __value32.__high) == 4" ]

[<Fact>]
let ``nested, array and qualified unnamed records are each named and measured`` () =
    let found = records "anonymous-records/nested.h"
    let outer = found.["fx_outer"]
    let field name = outer.Fields |> List.find (fun f -> f.Name = name)
    Assert.Equal("union fx_outer_slots", (field "slots").Type)
    Assert.True((field "slots").IsArray)
    Assert.Equal(Some 2, (field "slots").ArraySize)
    Assert.Equal("struct fx_outer_fixed", (field "fixed").Type)
    Assert.True((field "fixed").IsConst)
    Assert.Equal("struct fx_outer_slots_parts", (found.["fx_outer_slots"].Fields |> List.find (fun f -> f.Name = "parts")).Type)
    Assert.Equal(Some "fx_outer_slots", found.["fx_outer_slots_parts"].Anonymous |> Option.map _.Parent)
    Assert.Equal((24, 8, [0; 4; 16]), layoutOf outer)
    Assert.Equal((4, 4, [0; 0]), layoutOf found.["fx_outer_slots"])
    Assert.Equal((4, 2, [0; 2]), layoutOf found.["fx_outer_slots_parts"])
    Assert.Equal((8, 8, [0]), layoutOf found.["fx_outer_fixed"])
    clangConfirms (fixturePath "anonymous-records/nested.h") [
        "sizeof(struct fx_outer) == 24"; "_Alignof(struct fx_outer) == 8"
        "offsetof(struct fx_outer, slots) == 4"; "offsetof(struct fx_outer, fixed) == 16"
        "sizeof(((struct fx_outer*)0)->slots[0]) == 4"; "offsetof(struct fx_outer, slots[0].parts.hi) - offsetof(struct fx_outer, slots[0].parts) == 2"
        "_Alignof(__typeof__(((struct fx_outer*)0)->slots[0].parts)) == 2"; "_Alignof(__typeof__(((struct fx_outer*)0)->fixed)) == 8" ]

[<Fact>]
let ``unnamed records one macro expansion spells alike stay unmeasured`` () =
    let found = records "anonymous-records/macro-pair.h"
    // Clang dumps two different layouts under one location, so neither is attributable.
    Assert.Equal(None, found.["fx_pair_narrow"].Measured)
    Assert.Equal(None, found.["fx_pair_wide"].Measured)
    Assert.Equal((16, 8, [0; 8]), layoutOf found.["fx_pair"])

[<Fact>]
let ``a generated record name that names another declaration refuses the unit`` () =
    let header = fixturePath "anonymous-records/name-clash.h"
    Assert.Equal(
        Error $"FS9114: the generated record name 'fx_clash_inner' in '{header}' names more than one declaration: the unnamed record of field 'inner' of 'fx_clash' at {header}:10:3; a record. A generated name must name one record.",
        parse "anonymous-records/name-clash.h" |> Result.map ignore)
