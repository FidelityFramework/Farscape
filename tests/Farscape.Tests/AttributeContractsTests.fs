module Farscape.Tests.AttributeContractsTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Classified attribute contracts (issue #174): may_alias moves no byte, which clang confirms by
// measuring the record and a copy of it without the attribute in the same unit, and its aliasing
// fact is recorded in the record's descriptor. An unclassified attribute is still refused.
// ═══════════════════════════════════════════════════════════════════

/// The descriptor Documentation a confirmed may_alias record carries, before any header comment.
let private mayAliasContract =
    $"MayAliasAttr: no layout effect, confirmed by clang measuring this record and its copy without the attribute, in the same unit, at the same size, alignment and field offsets; {AttributeContracts.MayAliasFact}."

let private trimmed (source: string) =
    source.Split('\n') |> Array.map (fun line -> line.Trim()) |> String.concat "\n"

/// glibc's own <sys/socket.h>, parsed as the libc pilot parses it: /usr/include is the boundary.
let private glibcSocket () =
    let options : CppParser.HeaderParserOptions =
        { HeaderFile = "/usr/include/sys/socket.h"; IncludePaths = ["/usr/include"]; Defines = []; Verbose = false
          IncludeMacros = false; MacroPrefixes = []; IncludeRoot = Some "/usr/include"; CppMode = false }
    match CppParser.parseHeader options with
    | Ok declarations -> declarations
    | Error reason -> failwithf "glibc's sys/socket.h did not parse: %s" reason

let private recordIn (declarations: CppParser.Declaration list) name =
    declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = name -> Some s | _ -> None)

let private bytes (m: CppParser.StructLayoutInfo) =
    m.SizeBits / 8, m.AlignmentBits / 8, m.FieldOffsetsBits |> List.map (fun bits -> bits / 8)

/// Clang's measurement of the record itself.
let private measured (s: CppParser.StructDecl) =
    match s.Measured with
    | Some m -> bytes m
    | None -> failwithf "%s carries no clang measurement" s.Name

/// Clang's measurement of the record's copy without its layout-neutral attributes.
let private unattributed (s: CppParser.StructDecl) =
    match s.Unattributed with
    | Some (CppParser.UnattributedMeasured m) -> bytes m
    | other -> failwithf "%s carries no clang measurement of its unattributed copy: %A" s.Name other

let private parseFixture (relative: string) =
    match CppParser.parseHeader { HeaderFile = fixturePath relative; IncludePaths = []; Defines = []; Verbose = false
                                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false } with
    | Ok declarations -> declarations
    | Error reason -> failwithf "%s did not parse: %s" relative reason

[<Fact>]
let ``glibc's may_alias socket addresses are measured and emitted at clang's layout with their aliasing fact`` () =
    let declarations = glibcSocket ()
    let sockaddr = recordIn declarations "sockaddr"
    let storage = recordIn declarations "sockaddr_storage"
    for record in [sockaddr; storage] do
        Assert.Equal<string list>(["MayAliasAttr"], record.Attributes |> List.map _.Kind)
    // Clang's two measurements: the record, and its copy without the attribute.
    Assert.Equal((16, 2, [0; 2]), measured sockaddr)
    Assert.Equal((16, 2, [0; 2]), unattributed sockaddr)
    Assert.Equal((128, 8, [0; 2; 120]), measured storage)
    Assert.Equal((128, 8, [0; 2; 120]), unattributed storage)
    // The two records and the typedefs their fields resolve through, from the real parse.
    let selected =
        declarations |> List.filter (function
            | CppParser.Declaration.Struct s -> s.Name = "sockaddr" || s.Name = "sockaddr_storage"
            | CppParser.Declaration.Typedef _ -> true
            | _ -> false)
    let output = FidelityCodeGenerator.generate selected "Test" "test" Types.LP64 Map.empty |> trimmed
    let address = recordModule output "sockaddr"
    Assert.Contains("/// C struct `sockaddr`: layout measured (clang record layout dump).", output)
    Assert.Contains($"/// {mayAliasContract}", output)
    Assert.Contains("let Size = 16\n", address)
    Assert.Contains("let Alignment = 2\n", address)
    Assert.Contains("let sa_familyOffset = 0\n", address)
    Assert.Contains("let sa_dataOffset = 2\n", address)
    Assert.Contains("{ Name = \"sa_data\"; Offset = 2; Repr = Repr.I8; Count = 14;", address)
    Assert.Contains($"Documentation = Some \"{mayAliasContract}\"", address)
    // The aliasing fact is a typed field of the descriptor (BAREWire #12), not only prose.
    Assert.Contains("MayAlias = true\n", address)
    Assert.Contains("FlexibleArray = None\n", address)
    let storageModule = recordModule output "sockaddr_storage"
    Assert.Contains("MayAlias = true\n", storageModule)
    Assert.Contains("/// C struct `sockaddr_storage`: layout measured (clang record layout dump).", output)
    Assert.Contains("let Size = 128\n", storageModule)
    Assert.Contains("let Alignment = 8\n", storageModule)
    Assert.Contains("let ss_familyOffset = 0\n", storageModule)
    Assert.Contains("let __ss_paddingOffset = 2\n", storageModule)
    Assert.Contains("let __ss_alignOffset = 120\n", storageModule)
    Assert.Contains($"Documentation = Some \"{mayAliasContract}\"", storageModule)
    // Clang's own sizeof, _Alignof and offsetof: the emitted numbers, and the same layout for
    // glibc's members declared again without __attribute_struct_may_alias__.
    clangConfirms (fixturePath "attribute-contracts/socket-twins.h") [
        "sizeof(struct sockaddr) == 16"; "_Alignof(struct sockaddr) == 2"
        "offsetof(struct sockaddr, sa_family) == 0"; "offsetof(struct sockaddr, sa_data) == 2"
        "sizeof(struct sockaddr_storage) == 128"; "_Alignof(struct sockaddr_storage) == 8"
        "offsetof(struct sockaddr_storage, ss_family) == 0"; "offsetof(struct sockaddr_storage, __ss_padding) == 2"
        "offsetof(struct sockaddr_storage, __ss_align) == 120"
        "sizeof(struct fx_sockaddr_plain) == sizeof(struct sockaddr)"; "_Alignof(struct fx_sockaddr_plain) == _Alignof(struct sockaddr)"
        "offsetof(struct fx_sockaddr_plain, sa_data) == offsetof(struct sockaddr, sa_data)"
        "sizeof(struct fx_sockaddr_storage_plain) == sizeof(struct sockaddr_storage)"
        "_Alignof(struct fx_sockaddr_storage_plain) == _Alignof(struct sockaddr_storage)"
        "offsetof(struct fx_sockaddr_storage_plain, __ss_padding) == offsetof(struct sockaddr_storage, __ss_padding)"
        "offsetof(struct fx_sockaddr_storage_plain, __ss_align) == offsetof(struct sockaddr_storage, __ss_align)" ]

[<Fact>]
let ``a may_alias record is generated with clang's layout and the aliasing fact in its descriptor`` () =
    let header = fixturePath "attribute-contracts/may-alias.h"
    withPilot [header] [] ["fx_addr_use"] (fun result output ->
        match result with
        | Error error -> failwithf "The may_alias record was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            let aliased = recordModule types "fx_addr"
            Assert.Contains("/// C struct `fx_addr`: layout measured (clang record layout dump).", types)
            Assert.Contains("let Size = 16", aliased)
            Assert.Contains("let Alignment = 2", aliased)
            Assert.Contains("let dataOffset = 2", aliased)
            // The contract comes first; the header's own comment on the record follows it.
            Assert.Contains($"Documentation = Some \"{mayAliasContract} A may_alias record (glibc's struct sockaddr shape", aliased)
            Assert.Contains("MayAlias = true\n", aliased)
            // The unattributed record is laid out from its fields, with no aliasing fact.
            let plain = recordModule types "fx_addr_plain"
            Assert.Contains("/// C struct `fx_addr_plain`: layout declared", types)
            Assert.Contains("let Size = 16", plain)
            Assert.Contains("let dataOffset = 2", plain)
            Assert.DoesNotContain("may_alias", plain)
            Assert.Contains("MayAlias = false\n", plain)
            clangConfirms header [
                "sizeof(struct fx_addr) == 16"; "_Alignof(struct fx_addr) == 2"; "offsetof(struct fx_addr, data) == 2"
                "sizeof(struct fx_addr_plain) == 16"; "_Alignof(struct fx_addr_plain) == 2"; "offsetof(struct fx_addr_plain, data) == 2" ])

[<Fact>]
let ``a may_alias record whose field type is an aligned typedef is confirmed by clang's two measurements`` () =
    // Codex's shape (#174 comment 5521): natural alignment would place `value` at 4; clang
    // measures the record and its copy without may_alias alike, with `value` at 8.
    let header = fixturePath "attribute-contracts/skewed.h"
    let skewed = recordIn (parseFixture "attribute-contracts/skewed.h") "fx_skewed"
    Assert.Equal((16, 8, [0; 8]), measured skewed)
    Assert.Equal((16, 8, [0; 8]), unattributed skewed)
    withPilot [header] [] ["fx_skewed_use"] (fun result output ->
        match result with
        | Error error -> failwithf "The aligned-typedef may_alias record was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            let record = recordModule types "fx_skewed"
            Assert.Contains("/// C struct `fx_skewed`: layout measured (clang record layout dump).", types)
            Assert.Contains("let Size = 16\n", record)
            Assert.Contains("let Alignment = 8\n", record)
            Assert.Contains("let valueOffset = 8\n", record)
            Assert.Contains($"Documentation = Some \"{mayAliasContract}\" }}", record))
    clangConfirms header [
        "sizeof(struct fx_skewed) == 16"; "_Alignof(struct fx_skewed) == 8"; "offsetof(struct fx_skewed, value) == 8"
        "sizeof(struct fx_skewed_plain) == 16"; "_Alignof(struct fx_skewed_plain) == 8"; "offsetof(struct fx_skewed_plain, value) == 8" ]

[<Fact>]
let ``a may_alias record with no faithful copy without the attribute is refused before output`` () =
    let header = fixturePath "attribute-contracts/anonymous-member.h"
    let record = recordIn (parseFixture "attribute-contracts/anonymous-member.h") "fx_alias_member"
    Assert.Equal(
        Some (CppParser.UnattributedUnsynthesized "'anonymous1' is an anonymous member, which has no C name a copy could read its type through"),
        record.Unattributed)
    withPilot [header] [] ["fx_alias_member_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9121: struct 'fx_alias_member' carries layout-neutral attribute 'MayAliasAttr', but no faithful copy without the attribute can be synthesized for clang to measure: 'anonymous1' is an anonymous member, which has no C name a copy could read its type through; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a may_alias record that also carries a layout attribute is refused before output`` () =
    let header = fixturePath "attribute-contracts/packed-alias.h"
    withPilot [header] [] ["fx_packed_addr_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9121: struct 'fx_packed_addr' carries layout-neutral attribute 'MayAliasAttr', and layout attribute 'PackedAttr', which a copy without 'MayAliasAttr' would have to reproduce, so no copy is synthesized for clang to measure; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``an unclassified attribute beside may_alias is still refused with its exact reason`` () =
    let header = fixturePath "attribute-contracts/unclassified.h"
    withPilot [header] [] ["fx_old_addr_use"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: Struct 'fx_old_addr' carries attribute 'DeprecatedAttr' with unclassified layout effect; a measured layout or a classified attribute contract is required.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a may_alias record without a clang measurement is refused, not laid out from its fields`` () =
    let record =
        { mkStruct "fx_addr" [mkField "family" "int"; mkField "data" "int"] None with Attributes = [mkAttr "MayAliasAttr" [] None] }
    let source : DescriptorGenerator.LayoutSource =
        { Structs = Map.ofList ["fx_addr", record]; Layouts = Map.empty; Model = Types.LP64; FlexibleArrays = Map.empty
          Shape = function
            | "int" -> DescriptorGenerator.ScalarField { Family = TypeMapper.Signed; Bits = 32; Stratum = TypeMapper.Declared; CType = "int" }
            | spelling -> DescriptorGenerator.UnknownField spelling }
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> DescriptorGenerator.structDecls source record |> ignore)
    Assert.Equal(
        "FS9121: struct 'fx_addr' carries layout-neutral attribute 'MayAliasAttr', but clang has no measurement of it in its declaring unit, which the contract is confirmed against together with clang's measurement of its copy without the attribute; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
        error.Message)

[<Fact>]
let ``a may_alias record clang measures unlike its copy without the attribute is refused`` () =
    let layout = mkLayout "fx_addr" 64 32 [0; 32]
    let record =
        { mkStruct "fx_addr" [mkField "family" "int"; mkField "data" "int"] None with
            Attributes = [mkAttr "MayAliasAttr" [] None]
            Measured = Some layout
            Unattributed = Some (CppParser.UnattributedMeasured (mkLayout "fx_addr" 128 64 [0; 64])) }
    let source : DescriptorGenerator.LayoutSource =
        { Structs = Map.ofList ["fx_addr", record]; Layouts = Map.empty; Model = Types.LP64; FlexibleArrays = Map.empty
          Shape = function
            | "int" -> DescriptorGenerator.ScalarField { Family = TypeMapper.Signed; Bits = 32; Stratum = TypeMapper.Declared; CType = "int" }
            | spelling -> DescriptorGenerator.UnknownField spelling }
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> DescriptorGenerator.structDecls source record |> ignore)
    Assert.Equal(
        "FS9121: struct 'fx_addr' carries layout-neutral attribute 'MayAliasAttr', but clang measures it at 8 bytes, alignment 4, offsets 0, 4 and its copy without the attribute at 16 bytes, alignment 8, offsets 0, 8; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
        error.Message)
    // The same two measurements in agreement confirm it.
    DescriptorGenerator.structDecls source { record with Unattributed = Some (CppParser.UnattributedMeasured layout) } |> ignore

[<Fact>]
let ``each attribute kind has one classification`` () =
    Assert.Equal(Some AttributeContracts.LayoutAffecting, AttributeContracts.contractOf "PackedAttr")
    Assert.Equal(Some AttributeContracts.LayoutAffecting, AttributeContracts.contractOf "AlignedAttr")
    Assert.Equal(Some AttributeContracts.LayoutAffecting, AttributeContracts.contractOf "MaxFieldAlignmentAttr")
    Assert.Equal(Some (AttributeContracts.LayoutNeutral AttributeContracts.MayAliasFact), AttributeContracts.contractOf "MayAliasAttr")
    Assert.Equal(None, AttributeContracts.contractOf "DeprecatedAttr")
    Assert.Equal(None, AttributeContracts.contractOf "TransparentUnionAttr")

[<Fact>]
let ``a may_alias record whose two clang measurements differ only in a field offset is refused with no output`` () =
    // Same size and alignment; only the second field's offset differs (Codex's offset-only mutant).
    let record =
        { mkStruct "fx_addr" [mkField "family" "int"; mkField "data" "int"] None with
            Attributes = [mkAttr "MayAliasAttr" [] None]
            Measured = Some (mkLayout "fx_addr" 64 32 [0; 32])
            Unattributed = Some (CppParser.UnattributedMeasured (mkLayout "fx_addr" 64 32 [0; 0])) }
    let declarations = [ CppParser.Declaration.Struct record ]
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
    Assert.Equal(
        "FS9121: struct 'fx_addr' carries layout-neutral attribute 'MayAliasAttr', but clang measures it at 8 bytes, alignment 4, offsets 0, 4 and its copy without the attribute at 8 bytes, alignment 4, offsets 0, 0; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
        error.Message)

[<Fact>]
let ``a may_alias record whose two clang measurements differ only in alignment is refused with no output`` () =
    // Same size and field offsets; only the alignment differs (Codex's alignment-only mutant).
    let record =
        { mkStruct "fx_addr" [mkField "family" "int"; mkField "data" "int"] None with
            Attributes = [mkAttr "MayAliasAttr" [] None]
            Measured = Some (mkLayout "fx_addr" 64 32 [0; 32])
            Unattributed = Some (CppParser.UnattributedMeasured (mkLayout "fx_addr" 64 64 [0; 32])) }
    let declarations = [ CppParser.Declaration.Struct record ]
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
    Assert.Equal(
        "FS9121: struct 'fx_addr' carries layout-neutral attribute 'MayAliasAttr', but clang measures it at 8 bytes, alignment 4, offsets 0, 4 and its copy without the attribute at 8 bytes, alignment 8, offsets 0, 4; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
        error.Message)

[<Fact>]
let ``a may_alias record whose two clang measurements differ only in size is refused with no output`` () =
    // Same alignment and field offsets; only the size differs (Codex's size-only mutant).
    let record =
        { mkStruct "fx_addr" [mkField "family" "int"; mkField "data" "int"] None with
            Attributes = [mkAttr "MayAliasAttr" [] None]
            Measured = Some (mkLayout "fx_addr" 64 32 [0; 32])
            Unattributed = Some (CppParser.UnattributedMeasured (mkLayout "fx_addr" 96 32 [0; 32])) }
    let declarations = [ CppParser.Declaration.Struct record ]
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
    Assert.Equal(
        "FS9121: struct 'fx_addr' carries layout-neutral attribute 'MayAliasAttr', but clang measures it at 8 bytes, alignment 4, offsets 0, 4 and its copy without the attribute at 12 bytes, alignment 4, offsets 0, 4; the contract that 'MayAliasAttr' moves no byte is unconfirmed.",
        error.Message)
