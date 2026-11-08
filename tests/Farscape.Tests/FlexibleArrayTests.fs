module Farscape.Tests.FlexibleArrayTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Flexible array members (BAREWire #11): a C struct's last field with no element count is a
// trailing, extent-carrying view. Its count is a run-time fact read from a preceding integer
// field, declared by the pilot in [[options.flexible_arrays]] and emitted as the descriptor's
// FlexibleArray. A flexible array member without a declared count source is refused.
// ═══════════════════════════════════════════════════════════════════

let private trimmed (source: string) =
    source.Split('\n') |> Array.map (fun line -> line.Trim()) |> String.concat "\n"

/// glibc's own <sys/socket.h>, parsed as the libc pilot parses it: /usr/include is the boundary.
let private glibcSocket () =
    let header, includes = glibcSocketHeader ()
    let options : CppParser.HeaderParserOptions =
        { HeaderFile = header; IncludePaths = includes; SystemIncludePaths = []; Defines = []; Verbose = false
          IncludeMacros = false; MacroPrefixes = []; IncludeRoot = Some "/usr/include"; CppMode = false }
    match CppParser.parseHeader options with
    | Ok declarations -> declarations
    | Error reason -> failwithf "glibc's sys/socket.h did not parse: %s" reason

/// The count source the libc pilot declares for glibc's struct cmsghdr.
let private cmsgCount : PilotTypes.FlexibleArrayCount =
    { Record = "cmsghdr"; Field = "__cmsg_data"; CountField = "cmsg_len"; Rule = PilotTypes.RecordBytes
      Source = "glibc bits/socket.h:285 cmsg_len: Length of data in cmsg_data plus length of cmsghdr structure" }

let private generateWith (declarations: CppParser.Declaration list) (counts: PilotTypes.FlexibleArrayCount list) =
    let ctx =
        { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
            FlexibleArrays = counts |> List.map (fun c -> c.Record, c) |> Map.ofList }
    FidelityCodeGenerator.generateModule ctx ctx.OpaqueHandles declarations "Test" "test" "test" [] |> trimmed

let private records (names: string list) (declarations: CppParser.Declaration list) =
    declarations |> List.filter (function
        | CppParser.Declaration.Struct s -> List.contains s.Name names
        | CppParser.Declaration.Typedef _ -> true
        | _ -> false)

[<Fact>]
let ``glibc's cmsghdr is emitted at clang's layout with a trailing flexible array counted by cmsg_len`` () =
    let output = generateWith (records ["cmsghdr"] (glibcSocket ())) [cmsgCount]
    let header = recordModule output "cmsghdr"
    Assert.Contains("let Size = 16\n", header)
    Assert.Contains("let Alignment = 8\n", header)
    Assert.Contains("let cmsg_lenOffset = 0\n", header)
    Assert.Contains("let cmsg_levelOffset = 8\n", header)
    Assert.Contains("let cmsg_typeOffset = 12\n", header)
    Assert.Contains("let __cmsg_dataOffset = 16\n", header)
    // Three fixed fields; the flexible array member is none of them.
    Assert.Contains("{ Name = \"cmsg_len\"; Offset = 0; Repr = Repr.U64; Count = 1;", header)
    Assert.Contains("{ Name = \"cmsg_type\"; Offset = 12; Repr = Repr.I32; Count = 1;", header)
    Assert.DoesNotContain("{ Name = \"__cmsg_data\"; Offset = 16; Repr = Repr.U8; Count", header)
    Assert.Contains("FlexibleArray = Some ({ Name = \"__cmsg_data\"; Offset = 16; Repr = Repr.U8; Access = AccessKind.ReadWrite; CountField = \"cmsg_len\"; CountRule = CountRule.RecordBytes; Documentation = Some \"offset declared;", header)
    Assert.Contains($"flexible array member, count from 'cmsg_len' (record bytes through the array's last element), declared by options.flexible_arrays: {cmsgCount.Source}\" }})", header)
    Assert.Contains("MayAlias = false\n", header)
    // Clang's own sizeof, _Alignof and offsetof, and the element size of the array.
    clangConfirms (fst (glibcSocketHeader ())) [
        "sizeof(struct cmsghdr) == 16"; "_Alignof(struct cmsghdr) == 8"
        "offsetof(struct cmsghdr, cmsg_len) == 0"; "offsetof(struct cmsghdr, cmsg_level) == 8"
        "offsetof(struct cmsghdr, cmsg_type) == 12"; "offsetof(struct cmsghdr, __cmsg_data) == 16"
        "sizeof(((struct cmsghdr *)0)->__cmsg_data[0]) == 1" ]

[<Fact>]
let ``glibc's cmsghdr without a declared count source is refused with its exact reason`` () =
    let error = Assert.Throws<System.InvalidOperationException>(fun () -> generateWith (records ["cmsghdr"] (glibcSocket ())) [] |> ignore)
    Assert.Equal(
        "Struct 'cmsghdr' field '__cmsg_data' is a flexible array member with no declared count source; an options.flexible_arrays entry naming its count field and count rule is required.",
        error.Message)

/// `[[options.flexible_arrays]]` lines for one record of the fixture.
let private entry record field countField rule =
    [ "[[options.flexible_arrays]]"; $"record = \"{record}\""; $"field = \"{field}\""
      $"count_field = \"{countField}\""; $"count_rule = \"{rule}\""; "source = \"fixture: the length counts the header and the data\"" ]

let private cmsgHeader = fixturePath "flexible-arrays/cmsg.h"
let private cmsgFunctions = ["fx_cmsg_use"; "fx_short_use"; "fx_wide_use"]
let private cmsgEntries =
    entry "fx_cmsghdr" "cmsg_data" "cmsg_len" "record_bytes"
    @ entry "fx_short" "data" "count" "element_count"
    @ entry "fx_wide" "data" "count" "element_count"

[<Fact>]
let ``a pilot's declared count sources emit each flexible array at clang's offset`` () =
    withPilot [cmsgHeader] cmsgEntries cmsgFunctions (fun result output ->
        match result with
        | Error error -> failwithf "the declared flexible arrays were refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            let cmsg = recordModule types "fx_cmsghdr"
            Assert.Contains("let Size = 16\n", cmsg)
            Assert.Contains("let Alignment = 8\n", cmsg)
            Assert.Contains("let cmsg_dataOffset = 16\n", cmsg)
            Assert.Contains("FlexibleArray = Some ({ Name = \"cmsg_data\"; Offset = 16; Repr = Repr.U8; Access = AccessKind.ReadWrite; CountField = \"cmsg_len\"; CountRule = CountRule.RecordBytes;", cmsg)
            // Placed before the rounded size: 8 bytes, data at 5.
            let short = recordModule types "fx_short"
            Assert.Contains("let Size = 8\n", short)
            Assert.Contains("let Alignment = 4\n", short)
            Assert.Contains("let dataOffset = 5\n", short)
            Assert.Contains("FlexibleArray = Some ({ Name = \"data\"; Offset = 5; Repr = Repr.I8; Access = AccessKind.ReadWrite; CountField = \"count\"; CountRule = CountRule.ElementCount;", short)
            // The element alignment joins the record's: 4 bytes at 4, data at 4.
            let wide = recordModule types "fx_wide"
            Assert.Contains("let Size = 4\n", wide)
            Assert.Contains("let Alignment = 4\n", wide)
            Assert.Contains("let dataOffset = 4\n", wide)
            Assert.Contains("FlexibleArray = Some ({ Name = \"data\"; Offset = 4; Repr = Repr.U32; Access = AccessKind.ReadWrite; CountField = \"count\"; CountRule = CountRule.ElementCount;", wide))
    clangConfirms cmsgHeader [
        "sizeof(struct fx_cmsghdr) == 16"; "_Alignof(struct fx_cmsghdr) == 8"; "offsetof(struct fx_cmsghdr, cmsg_data) == 16"
        "sizeof(struct fx_short) == 8"; "_Alignof(struct fx_short) == 4"; "offsetof(struct fx_short, data) == 5"
        "sizeof(struct fx_wide) == 4"; "_Alignof(struct fx_wide) == 4"; "offsetof(struct fx_wide, data) == 4" ]

/// Generation over the fixture is refused with exactly `expected`, and writes no file.
let private refusedWith (headers: string list) (options: string list) (functions: string list) (expected: string) =
    withPilot headers options functions (fun result output ->
        Assert.Equal(Error ("Binding generation failed: " + expected), result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a flexible array member without a declared count source is refused before output`` () =
    refusedWith [cmsgHeader] (entry "fx_short" "data" "count" "element_count" @ entry "fx_wide" "data" "count" "element_count") cmsgFunctions
        "Struct 'fx_cmsghdr' field 'cmsg_data' is a flexible array member with no declared count source; an options.flexible_arrays entry naming its count field and count rule is required."

[<Fact>]
let ``a count field that names no preceding field is refused before output`` () =
    refusedWith [cmsgHeader] (entry "fx_cmsghdr" "cmsg_data" "cmsg_size" "record_bytes" @ entry "fx_short" "data" "count" "element_count" @ entry "fx_wide" "data" "count" "element_count") cmsgFunctions
        "Struct 'fx_cmsghdr' flexible array member 'cmsg_data' declares count field 'cmsg_size', which names no preceding field of 'fx_cmsghdr'; a preceding integer field of the same struct is required."

[<Fact>]
let ``a count field that is no integer is refused before output`` () =
    refusedWith [fixturePath "flexible-arrays/float-count.h"] (entry "fx_float_count" "data" "length" "element_count") ["fx_float_count_use"]
        "Struct 'fx_float_count' flexible array member 'data' declares count field 'length', which is not one integer scalar; a preceding integer field of the same struct is required."

[<Fact>]
let ``a count source naming another field than the flexible array member is refused`` () =
    refusedWith [cmsgHeader] (entry "fx_cmsghdr" "cmsg_type" "cmsg_len" "record_bytes" @ entry "fx_short" "data" "count" "element_count" @ entry "fx_wide" "data" "count" "element_count") cmsgFunctions
        "Struct 'fx_cmsghdr' field 'cmsg_data' is a flexible array member, but options.flexible_arrays declares the count source of 'fx_cmsghdr' for field 'cmsg_type'; a count source naming 'cmsg_data' is required."

[<Fact>]
let ``a count source naming a struct no header declares is refused`` () =
    refusedWith [cmsgHeader] (cmsgEntries @ entry "fx_missing" "data" "count" "element_count") cmsgFunctions
        "options.flexible_arrays declares a count source for 'fx_missing' field 'data', but no parsed header declares struct 'fx_missing'; a count source names a declared struct."

// Shapes C or clang never hands the generator from a header, stated directly.

let private scalar family bits cType = DescriptorGenerator.ScalarField { Family = family; Bits = bits; Stratum = TypeMapper.Declared; CType = cType }

let private source (records: CppParser.StructDecl list) (counts: PilotTypes.FlexibleArrayCount list) : DescriptorGenerator.LayoutSource =
    { Structs = records |> List.map (fun r -> r.Name, r) |> Map.ofList
      Layouts = Map.empty
      Model = Types.LP64
      FlexibleArrays = counts |> List.map (fun c -> c.Record, c) |> Map.ofList
      Shape = function
        | "unsigned long" -> scalar TypeMapper.Unsigned 64 "unsigned long"
        | "int" -> scalar TypeMapper.Signed 32 "int"
        | "unsigned char" -> scalar TypeMapper.Unsigned 8 "unsigned char"
        | "struct Packet" -> DescriptorGenerator.StructField "Packet"
        | spelling -> DescriptorGenerator.UnknownField spelling }

let private flexible name typ = { mkField name typ with IsArray = true; ArraySize = None }
let private count record field countField = { Record = record; Field = field; CountField = countField; Rule = PilotTypes.ElementCount; Source = "constructed" } : PilotTypes.FlexibleArrayCount
let private packet = mkStruct "Packet" [mkField "length" "unsigned long"; flexible "data" "unsigned char"] None

let private refusal (src: DescriptorGenerator.LayoutSource) (record: CppParser.StructDecl) =
    (Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls src record |> ignore)).Message

[<Fact>]
let ``a flexible array member followed by a field is refused`` () =
    let record = mkStruct "Packet" [mkField "length" "unsigned long"; flexible "data" "unsigned char"; mkField "tail" "int"] None
    Assert.Equal(
        "Struct 'Packet' field 'data' is a flexible array member followed by field 'tail'; a flexible array member must be the last field of its struct.",
        refusal (source [record] [count "Packet" "data" "length"]) record)

[<Fact>]
let ``a flexible array member of a union is refused`` () =
    let record = { mkStruct "Packet" [mkField "length" "unsigned long"; flexible "data" "unsigned char"] None with IsUnion = true }
    Assert.Equal(
        "Union 'Packet' field 'data' is a flexible array member; a flexible array member trails the fixed fields of a struct, and a union has none to trail.",
        refusal (source [record] [count "Packet" "data" "length"]) record)

[<Fact>]
let ``a count field after the flexible array member is no preceding field`` () =
    // The count source must precede the member it counts; naming the member itself fails too.
    Assert.Equal(
        "Struct 'Packet' flexible array member 'data' declares count field 'data', which names no preceding field of 'Packet'; a preceding integer field of the same struct is required.",
        refusal (source [packet] [count "Packet" "data" "data"]) packet)

[<Fact>]
let ``a count source for a struct with no flexible array member is refused`` () =
    let record = mkStruct "Packet" [mkField "length" "unsigned long"; mkField "kind" "int"] None
    Assert.Equal(
        "options.flexible_arrays declares a count source for 'Packet' field 'data', but 'Packet' has no flexible array member; a count source names the last, element-count-free field of its struct.",
        refusal (source [record] [count "Packet" "data" "length"]) record)

[<Fact>]
let ``a record ending in a flexible array member is not held by value`` () =
    let holder = mkStruct "Holder" [mkField "packet" "struct Packet"] None
    Assert.Equal(
        "Struct 'Holder' field 'packet' holds record type 'Packet' by value, which ends in a flexible array member; a record ending in a flexible array member has no fixed extent to hold by value.",
        refusal (source [packet; holder] [count "Packet" "data" "length"]) holder)

[<Fact>]
let ``a flexible array member of record elements is refused`` () =
    let element = mkStruct "Element" [mkField "kind" "int"] None
    let record = mkStruct "Packet" [mkField "length" "unsigned long"; flexible "items" "struct Element"] None
    let src = { source [element; record] [count "Packet" "items" "length"] with
                  Shape = function
                    | "unsigned long" -> scalar TypeMapper.Unsigned 64 "unsigned long"
                    | "int" -> scalar TypeMapper.Signed 32 "int"
                    | "struct Element" -> DescriptorGenerator.StructField "Element"
                    | spelling -> DescriptorGenerator.UnknownField spelling }
    Assert.Equal(
        "Struct 'Packet' flexible array member 'items' has elements of record type 'Element'; a flexible array member of scalar elements is required.",
        refusal src record)

// The pilot vocabulary.

let private pilotWith (lines: string list) =
    String.concat "\n" ([ "[library]"; "name = \"fixture\""; "headers = [\"x.h\"]"; "[output]"; "mode = \"fidelity\""; "directory = \"out\""; "[options]" ] @ lines)

let private parsePilot (text: string) =
    PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text)

[<Fact>]
let ``options.flexible_arrays round-trips through the pilot serializer`` () =
    match parsePilot (pilotWith (entry "cmsghdr" "__cmsg_data" "cmsg_len" "record_bytes" @ entry "fx_short" "data" "count" "element_count")) with
    | Error error -> failwithf "the pilot did not parse: %s" error
    | Ok project ->
        let declared = project.Options.Value.FlexibleArrays
        let expected : PilotTypes.FlexibleArrayCount list =
            [ { Record = "cmsghdr"; Field = "__cmsg_data"; CountField = "cmsg_len"; Rule = PilotTypes.RecordBytes; Source = "fixture: the length counts the header and the data" }
              { Record = "fx_short"; Field = "data"; CountField = "count"; Rule = PilotTypes.ElementCount; Source = "fixture: the length counts the header and the data" } ]
        Assert.Equal<PilotTypes.FlexibleArrayCount list>(expected, declared)
        match parsePilot (PilotSerializer.toTomlString project) with
        | Ok restored -> Assert.Equal<PilotTypes.FlexibleArrayCount list>(declared, restored.Options.Value.FlexibleArrays)
        | Error error -> failwithf "the serialized pilot did not parse: %s" error

[<Theory>]
[<InlineData("rule")>]
[<InlineData("unknown")>]
[<InlineData("empty")>]
[<InlineData("duplicate")>]
let ``a malformed options.flexible_arrays entry is refused with its exact reason`` shape =
    let lines, expected =
        match shape with
        | "rule" -> entry "cmsghdr" "__cmsg_data" "cmsg_len" "bytes", "FS9201: options.flexible_arrays count_rule 'bytes' is neither element_count nor record_bytes"
        | "unknown" -> entry "cmsghdr" "__cmsg_data" "cmsg_len" "record_bytes" @ ["bias = 16"], "FS9200: options.flexible_arrays has unknown field 'bias'"
        | "empty" -> entry "cmsghdr" "__cmsg_data" "" "record_bytes", "FS9201: options.flexible_arrays requires nonempty count_field"
        | _ -> entry "cmsghdr" "__cmsg_data" "cmsg_len" "record_bytes" @ entry "cmsghdr" "__cmsg_data" "cmsg_len" "record_bytes", "FS9202: options.flexible_arrays declares record 'cmsghdr' more than once; a struct has at most one flexible array member"
    Assert.Equal(Error expected, parsePilot (pilotWith lines) |> Result.map ignore)
