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
        HeaderFile = fixturePath relative; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
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

// ═══════════════════════════════════════════════════════════════════
// C11 anonymous members (issue #173): an unnamed struct or union with no field name is a field
// of its record, typed by its own clang-measured record, with its members reachable; one that
// cannot be represented refuses the unit. None is dropped.
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``a C11 anonymous member is a field of its record, typed by its own measured record`` () =
    let header = fixturePath "anonymous-members/members.h"
    let found = records "anonymous-members/members.h"
    let am = found.["fx_am"]
    Assert.Equal<string list>(["tag"; "anonymous1"; "anonymous2"; "tail"], am.Fields |> List.map _.Name)
    Assert.Equal("union fx_am_anonymous1", am.Fields.[1].Type)
    Assert.Equal("struct fx_am_anonymous2", am.Fields.[2].Type)
    let union = found.["fx_am_anonymous1"]
    Assert.True(union.IsUnion)
    Assert.Equal<string list>(["i"; "f"], union.Fields |> List.map _.Name)
    let expected : CppParser.AnonymousRecordOrigin = { Parent = "fx_am"; Field = "anonymous1"; Location = $"{header}:6:3"; Member = true }
    Assert.Equal(Some expected, union.Anonymous)
    Assert.Equal<string list>(["lo"; "hi"], found.["fx_am_anonymous2"].Fields |> List.map _.Name)
    // The enclosing record's measured layout has an offset for each anonymous member.
    Assert.Equal((24, 8, [0; 4; 8; 16]), layoutOf am)
    Assert.Equal((4, 4, [0; 0]), layoutOf union)
    Assert.Equal((4, 2, [0; 2]), layoutOf found.["fx_am_anonymous2"])
    // An anonymous member nested in another, under a record C names only by a typedef.
    Assert.Equal<string list>(["head"; "anonymous1"], found.["fx_am_td"].Fields |> List.map _.Name)
    Assert.Equal<string list>(["word"; "anonymous1"], found.["fx_am_td_anonymous1"].Fields |> List.map _.Name)
    Assert.Equal<string list>(["low"; "high"], found.["fx_am_td_anonymous1_anonymous1"].Fields |> List.map _.Name)
    Assert.Equal(Some "fx_am_td_anonymous1", found.["fx_am_td_anonymous1_anonymous1"].Anonymous |> Option.map _.Parent)
    Assert.Equal((8, 4, [0; 4]), layoutOf found.["fx_am_td"])
    Assert.Equal((4, 4, [0; 0]), layoutOf found.["fx_am_td_anonymous1"])
    Assert.Equal((4, 2, [0; 2]), layoutOf found.["fx_am_td_anonymous1_anonymous1"])
    clangConfirms header [
        "sizeof(struct fx_am) == 24"; "_Alignof(struct fx_am) == 8"; "offsetof(struct fx_am, tag) == 0"
        "offsetof(struct fx_am, i) == 4"; "offsetof(struct fx_am, f) == 4"; "offsetof(struct fx_am, lo) == 8"
        "offsetof(struct fx_am, hi) == 10"; "offsetof(struct fx_am, tail) == 16"
        "sizeof(fx_am_td) == 8"; "_Alignof(fx_am_td) == 4"; "offsetof(fx_am_td, word) == 4"
        "offsetof(fx_am_td, low) == 4"; "offsetof(fx_am_td, high) == 6" ]

[<Fact>]
let ``anonymous members one macro expansion spells alike stay unmeasured`` () =
    let found = records "anonymous-members/macro-pair.h"
    Assert.Equal<string list>(["anonymous1"; "anonymous2"], found.["fx_mpair"].Fields |> List.map _.Name)
    // Clang dumps two different layouts under one location, so neither is attributable.
    Assert.Equal(None, found.["fx_mpair_anonymous1"].Measured)
    Assert.Equal(None, found.["fx_mpair_anonymous2"].Measured)
    Assert.Equal((16, 8, [0; 8]), layoutOf found.["fx_mpair"])

[<Fact>]
let ``a generated member name that names another member refuses the unit`` () =
    let header = fixturePath "anonymous-members/name-clash.h"
    Assert.Equal(
        Error $"FS9123: the generated member name 'anonymous1' of the anonymous member of 'fx_mclash' declared at {header}:5:3 in '{header}' names another member of that record. A generated name must name one member.",
        parse "anonymous-members/name-clash.h" |> Result.map ignore)

[<Fact>]
let ``an anonymous member of a C++ record is a field of its class, typed by its own measured record`` () =
    let parseCpp (relative: string) =
        CppParser.parseHeader {
            HeaderFile = fixturePath relative; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
            MacroPrefixes = []; IncludeMacros = false; Verbose = false; CppMode = true }
    let classesAndRecords (relative: string) =
        match parseCpp relative with
        | Error error -> failwithf "C++ fixture %s did not parse: %s" relative error
        | Ok decls ->
            let classes = decls |> List.choose (function CppParser.Declaration.Class c when c.Name <> "" -> Some (c.Name, c) | _ -> None) |> Map.ofList
            let structs = decls |> List.choose (function CppParser.Declaration.Struct s when s.Name <> "" -> Some (s.Name, s) | _ -> None) |> Map.ofList
            classes, structs
    let classLayoutOf (c: CppParser.ClassDecl) =
        match c.Measured with
        | Some m ->
            m.SizeBits / 8, m.AlignmentBits / 8, CppParser.classFieldOffsetsBits c m |> List.map (fun bits -> bits / 8)
        | None -> failwithf "C++ record %s carries no clang measurement" c.Name

    let classHeader = fixturePath "anonymous-members/class.hpp"
    let classes, structs = classesAndRecords "anonymous-members/class.hpp"
    let cppAm = classes.["fx_cpp_am"]
    Assert.Equal<string list>(["tag"; "anonymous1"], cppAm.Fields |> List.map _.Name)
    Assert.Equal("union fx_cpp_am_anonymous1", cppAm.Fields.[1].Type)
    Assert.Equal((8, 4, [0; 4]), classLayoutOf cppAm)
    Assert.Equal(CppClassAnalysis.PODClass 8, CppClassAnalysis.classifyClass cppAm)
    let cppUnion = structs.["fx_cpp_am_anonymous1"]
    Assert.True(cppUnion.IsUnion)
    Assert.Equal<string list>(["i"; "f"], cppUnion.Fields |> List.map _.Name)
    let expectedOrigin : CppParser.AnonymousRecordOrigin = { Parent = "fx_cpp_am"; Field = "anonymous1"; Location = $"{classHeader}:5:3"; Member = true }
    Assert.Equal(Some expectedOrigin, cppUnion.Anonymous)
    Assert.Equal((4, 4, [0; 0]), layoutOf cppUnion)
    clangConfirmsCpp classHeader [
        "sizeof(fx_cpp_am) == 8"; "alignof(fx_cpp_am) == 4"
        "offsetof(fx_cpp_am, tag) == 0"; "offsetof(fx_cpp_am, i) == 4"; "offsetof(fx_cpp_am, f) == 4" ]

    // Nested and typedef-named anonymous members in C++ mode.
    let membersHeader = fixturePath "anonymous-members/members.h"
    let mClasses, mStructs = classesAndRecords "anonymous-members/members.h"
    let am = mClasses.["fx_am"]
    Assert.Equal<string list>(["tag"; "anonymous1"; "anonymous2"; "tail"], am.Fields |> List.map _.Name)
    Assert.Equal("union fx_am_anonymous1", am.Fields.[1].Type)
    Assert.Equal("struct fx_am_anonymous2", am.Fields.[2].Type)
    Assert.Equal((24, 8, [0; 4; 8; 16]), classLayoutOf am)
    Assert.Equal(CppClassAnalysis.PODClass 24, CppClassAnalysis.classifyClass am)
    Assert.Equal((4, 4, [0; 0]), layoutOf mStructs.["fx_am_anonymous1"])
    Assert.Equal((4, 2, [0; 2]), layoutOf mStructs.["fx_am_anonymous2"])
    let td = mClasses.["fx_am_td"]
    Assert.Equal<string list>(["head"; "anonymous1"], td.Fields |> List.map _.Name)
    Assert.Equal((8, 4, [0; 4]), classLayoutOf td)
    Assert.Equal(CppClassAnalysis.PODClass 8, CppClassAnalysis.classifyClass td)
    Assert.Equal<string list>(["word"; "anonymous1"], mStructs.["fx_am_td_anonymous1"].Fields |> List.map _.Name)
    Assert.Equal<string list>(["low"; "high"], mStructs.["fx_am_td_anonymous1_anonymous1"].Fields |> List.map _.Name)
    Assert.Equal((4, 4, [0; 0]), layoutOf mStructs.["fx_am_td_anonymous1"])
    Assert.Equal((4, 2, [0; 2]), layoutOf mStructs.["fx_am_td_anonymous1_anonymous1"])
    clangConfirmsCpp membersHeader [
        "sizeof(fx_am) == 24"; "alignof(fx_am) == 8"; "offsetof(fx_am, tag) == 0"
        "offsetof(fx_am, i) == 4"; "offsetof(fx_am, lo) == 8"; "offsetof(fx_am, hi) == 10"; "offsetof(fx_am, tail) == 16"
        "sizeof(fx_am_td) == 8"; "alignof(fx_am_td) == 4"; "offsetof(fx_am_td, word) == 4"
        "offsetof(fx_am_td, low) == 4"; "offsetof(fx_am_td, high) == 6" ]

[<Fact>]
let ``C++ anonymous members refuse member-name clashes, generated record name clashes, and unmeasured anonymous records`` () =
    let parseCpp (relative: string) =
        CppParser.parseHeader {
            HeaderFile = fixturePath relative; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
            MacroPrefixes = []; IncludeMacros = false; Verbose = false; CppMode = true }
    let mclashHeader = fixturePath "anonymous-members/name-clash.h"
    Assert.Equal(
        Error $"FS9123: the generated member name 'anonymous1' of the anonymous member of 'fx_mclash' declared at {mclashHeader}:5:3 in '{mclashHeader}' names another member of that record. A generated name must name one member.",
        parseCpp "anonymous-members/name-clash.h" |> Result.map ignore)
    let rclashHeader = fixturePath "anonymous-records/name-clash.h"
    Assert.Equal(
        Error $"FS9114: the generated record name 'fx_clash_inner' in '{rclashHeader}' names more than one declaration: the unnamed record of field 'inner' of 'fx_clash' at {rclashHeader}:10:3; a record. A generated name must name one record.",
        parseCpp "anonymous-records/name-clash.h" |> Result.map ignore)
    let classHeader = fixturePath "anonymous-members/class.hpp"
    let info = ClangToolchain.startInfo ["-x"; "c++"; "-std=c++17"; "-Xclang"; "-ast-dump=json"; "-fsyntax-only"; classHeader]
    use proc = System.Diagnostics.Process.Start info
    let cppAst = proc.StandardOutput.ReadToEnd()
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)
    Assert.True(CppParser.declarationsOfAst classHeader cppAst |> Result.isOk)
    let unpairedCppAst = cppAst.Replace("\"tagUsed\": \"union\"", "\"tagUsed\": \"struct\"")
    Assert.Equal(
        Error $"FS9122: an anonymous member of 'fx_cpp_am' in '{classHeader}', of clang type 'fx_cpp_am::(anonymous union at {classHeader}:5:3)', cannot be paired with a record clang declares for it, so 'fx_cpp_am' has no representation of it. An anonymous member is never dropped from its record.",
        CppParser.declarationsOfAst classHeader unpairedCppAst |> Result.map ignore)
    // An unmeasured anonymous record enclosed by a C++ class is refused with FS9113 before class-shape refusal.
    let origin : CppParser.AnonymousRecordOrigin = { Parent = "fx_cpp_am"; Field = "anonymous1"; Location = $"{mclashHeader}:5:3"; Member = true }
    let unmeasuredAnon = { mkStruct "fx_cpp_am_anonymous1" [mkField "i" "int"] None with IsUnion = true; Anonymous = Some origin; Measured = None }
    let parentClass = mkClassPod "fx_cpp_am" [mkField "tag" "int"; mkField "anonymous1" "union fx_cpp_am_anonymous1"]
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        FidelityCodeGenerator.generate [CppParser.Declaration.Class parentClass; CppParser.Declaration.Struct unmeasuredAnon] "Test" "test" Types.LP64 Map.empty |> ignore)
    let d = Assert.Single(error.Refusal.Diagnostics)
    Assert.Equal(Diagnostics.AnonymousRecordUnmeasured, d.Code)
    Assert.Equal($"anonymous record 'fx_cpp_am_anonymous1' (anonymous member 'anonymous1' of 'fx_cpp_am', declared at {mclashHeader}:5:3) has no clang-measured layout in its declaring unit; its layout is never computed from its declared fields.", d.Message)
    // A measured anonymous record inside an unmeasured enclosing C++ class is also refused with FS9113.
    let measuredAnon = { unmeasuredAnon with Measured = Some { Name = "fx_cpp_am_anonymous1"; SizeBits = 32; DataSizeBits = 32; AlignmentBits = 32; FieldOffsetsBits = [0] } }
    let errorParent = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        FidelityCodeGenerator.generate [CppParser.Declaration.Class { parentClass with Measured = None }; CppParser.Declaration.Struct measuredAnon] "Test" "test" Types.LP64 Map.empty |> ignore)
    let dParent = Assert.Single(errorParent.Refusal.Diagnostics)
    Assert.Equal(Diagnostics.AnonymousRecordUnmeasured, dParent.Code)
    Assert.Equal($"C++ record 'fx_cpp_am' encloses anonymous member 'anonymous1' of 'fx_cpp_am' (declared at {mclashHeader}:5:3), and clang has no measurement of 'fx_cpp_am' in its declaring unit; an anonymous record's layout is never computed from its declared fields.", dParent.Message)

/// Clang's own JSON AST of `header` under `flags`, produced by the test harness: `-fms-extensions`
/// is an option no pilot can select (#178), so only the harness can give the parser its AST.
let private clangAst (header: string) (flags: string list) =
    let info = ClangToolchain.startInfo (["-x"; "c"] @ flags @ ["-Xclang"; "-ast-dump=json"; "-fsyntax-only"; header])
    use proc = System.Diagnostics.Process.Start info
    let json = proc.StandardOutput.ReadToEnd()
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)
    json

[<Fact>]
let ``an anonymous member clang declares with no unnamed record is refused, never dropped`` () =
    let header = fixturePath "anonymous-members/tagged-member.h"
    // Under -fms-extensions clang declares the implicit unnamed field `struct fx_inner` in
    // fx_tagged_outer; no unnamed record precedes it, so it cannot be paired.
    let ast = clangAst header ["-fms-extensions"]
    Assert.Contains("\"qualType\": \"struct fx_inner\"", ast)
    Assert.Equal(
        Error $"FS9122: an anonymous member of 'fx_tagged_outer' in '{header}', of clang type 'struct fx_inner', cannot be paired with a record clang declares for it, so 'fx_tagged_outer' has no representation of it. An anonymous member is never dropped from its record.",
        CppParser.declarationsOfAst header ast |> Result.map ignore)
    // Without the extension C declares no member, and the record holds only `y`.
    match CppParser.declarationsOfAst header (clangAst header []) with
    | Error error -> failwithf "The plain C AST was refused: %s" error
    | Ok decls ->
        let outer = decls |> List.pick (function CppParser.Declaration.Struct s when s.Name = "fx_tagged_outer" -> Some s | _ -> None)
        Assert.Equal<string list>(["y"], outer.Fields |> List.map _.Name)

[<Fact>]
let ``no public entry point reads a name-clash header past its FS9114 refusal`` () =
    // The AST reader that skips measurement is internal (#173 comment 5602): only the test
    // assembly can call it, and it refuses the clash as well.
    let header = fixturePath "anonymous-records/name-clash.h"
    let refusal = $"FS9114: the generated record name 'fx_clash_inner' in '{header}' names more than one declaration: the unnamed record of field 'inner' of 'fx_clash' at {header}:10:3; a record. A generated name must name one record."
    let parserType = typeof<CppParser.HeaderParserOptions>.DeclaringType
    let flags = System.Reflection.BindingFlags.Static ||| System.Reflection.BindingFlags.Public
    Assert.Null(parserType.GetMethod("declarationsOfAst", flags))
    let internalReader = parserType.GetMethod("declarationsOfAst", System.Reflection.BindingFlags.Static ||| System.Reflection.BindingFlags.NonPublic)
    Assert.NotNull(internalReader)
    Assert.True(internalReader.IsAssembly, "declarationsOfAst must be internal to Farscape.Core")
    Assert.Equal(Error refusal, CppParser.declarationsOfAst header (clangAst header []) |> Result.map ignore)
    // Every public parse and generation entry point refuses the same header with FS9114.
    let options : CppParser.HeaderParserOptions =
        { HeaderFile = header; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
          MacroPrefixes = []; IncludeMacros = true; Verbose = false; CppMode = false }
    Assert.Equal(Error refusal, CppParser.parseHeaderFull options |> Result.map ignore)
    Assert.Equal(Error refusal, CppParser.parseHeaderUnit options |> Result.map ignore)
    Assert.Equal(Error refusal, CppParser.parseHeader options |> Result.map ignore)
    Assert.Equal(Error refusal, CppParser.parse header [] false |> Result.map ignore)
    Assert.Equal(Error refusal, CppParser.parseWithDefines header [] [] false |> Result.map ignore)
    Assert.Equal(Error refusal, CppParser.parseWithIncludeRoot header [] [] None [] false |> Result.map ignore)
    Assert.Equal(Error refusal, CppParser.parseCMSIS header [] [] false |> Result.map ignore)
    let output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-clash-" + System.Guid.NewGuid().ToString("N"))
    try
        let generated =
            BindingGenerator.generateBindings
                { HeaderFile = System.IO.FileInfo header; LibraryName = "fixture"; OutputDirectory = output; Namespace = "Test.Fixture"
                  IncludePaths = []; Defines = []; Verbose = false; GenerateWrappers = false; DataModel = Types.LP64 }
        Assert.Equal(Error $"Failed to parse header: {refusal}", generated |> Result.map ignore)
        Assert.False(System.IO.Directory.Exists output && System.IO.Directory.EnumerateFileSystemEntries(output) |> Seq.isEmpty |> not)
    finally
        if System.IO.Directory.Exists output then System.IO.Directory.Delete(output, true)
    withPilot [header] [] ["fx_clash_read"] (fun result written ->
        Assert.Equal(Error $"Failed to parse headers: {refusal}", result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(written, "*", System.IO.SearchOption.AllDirectories)))
