module Farscape.Tests.ArchiveInspectionTests

open System.IO
open Xunit
open Farscape.Core
open Fidelity.Data.TOML
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Static archive inspection, link provenance and the Static Binding Elision Test (#204).
// The archives are built at test time from C sources by the resolved clang and the resolved
// llvm-ar (ClangToolchain selects both); the malformed ones are written byte by byte. Every
// fixture directory is deleted afterwards.
// ═══════════════════════════════════════════════════════════════════

// ─── Fixture construction ───

/// Run `body` over a fresh scratch directory in OS temporary storage, deleted afterwards.
let private withScratch (body: string -> unit) =
    let directory = TestHelpers.TestScratch.create "archive-inspection"
    Directory.CreateDirectory(directory) |> ignore
    try body directory
    finally Directory.Delete(directory, true)

/// Run a tool to completion; a nonzero exit fails the test with the tool's output.
let private run (info: System.Diagnostics.ProcessStartInfo) =
    use child = new System.Diagnostics.Process(StartInfo = info)
    child.Start() |> ignore
    let errors = child.StandardError.ReadToEndAsync()
    let output = child.StandardOutput.ReadToEnd()
    child.WaitForExit()
    if child.ExitCode <> 0 then
        failwithf "%s %s exited with code %d: %s %s" info.FileName (System.String.Join(" ", info.ArgumentList)) child.ExitCode output errors.Result

/// Compile C `source` to `<name>.o` in `directory` with extra clang `flags`; the object's path.
let private compile (directory: string) (name: string) (flags: string list) (source: string) : string =
    let sourcePath = Path.Combine(directory, name + ".c")
    let objectPath = Path.Combine(directory, name + ".o")
    File.WriteAllText(sourcePath, source)
    run (ClangToolchain.startInfo ([ "-c" ] @ flags @ [ sourcePath; "-o"; objectPath ]))
    objectPath

/// The directory of the clang that compiles the fixtures, its symbolic links followed.
let private clangDirectory () = ClangToolchain.realDirectory ()

/// Archive `objects` as `<directory>/<name>` with the `modifiers` (`rcs`, `rcsT`) of the llvm-ar
/// ClangToolchain resolves. This module selects no archiver of its own.
let private archive (directory: string) (name: string) (modifiers: string) (objects: string list) : string =
    let archivePath = Path.Combine(directory, name)
    run (ClangToolchain.arStartInfo (modifiers :: archivePath :: objects))
    archivePath

/// The llvm-nm that reads the bitcode the resolved clang writes: `FIDELITY_LLVM_NM` when the
/// process sets it, otherwise the llvm-nm beside the resolved clang when one exists, otherwise
/// the process environment's own resolution. The process environment is read, never changed.
let private resolveMatchingLlvmNm () : Result<ArchiveInspection.ToolIdentity, Diagnostics.Refusal> =
    let processEnvironment (name: string) = System.Environment.GetEnvironmentVariable name |> Option.ofObj
    let beside = Path.Combine(clangDirectory (), "llvm-nm")
    let environment (name: string) =
        if name = "FIDELITY_LLVM_NM" && (processEnvironment name).IsNone && File.Exists beside then Some beside
        else processEnvironment name
    ArchiveInspection.LlvmNmToolchain.resolveWith environment File.Exists

/// A GNU-format `ar` archive of `entries` (each the header's name field and the payload),
/// written without an archiver so a test controls every header byte.
let private handArchive (entries: (string * byte[]) list) : byte[] =
    use stream = new MemoryStream()
    let write (bytes: byte[]) = stream.Write(bytes, 0, bytes.Length)
    let ascii (text: string) = System.Text.Encoding.ASCII.GetBytes text
    write (ascii "!<arch>\n")
    for name, payload in entries do
        // name[16] mtime[12] uid[6] gid[6] mode[8] size[10] end magic[2]
        let size = string payload.Length
        write (ascii (name.PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "644".PadRight(8) + size.PadRight(10) + "`\n"))
        write payload
        if payload.Length % 2 = 1 then write [| 0x0Auy |]
    stream.ToArray()

/// A minimal ELF64 relocatable object defining `fixture_symbol`, with an optional
/// SHT_SYMTAB_SHNDX table and enough section headers to contain the extended index.
let private elf64SymbolObject (sectionIndex: uint16) (sectionCount: int) (extendedIndex: uint32 option) : byte[] =
    let sectionTable = 64
    let symbolTable = sectionTable + sectionCount * 64
    let stringTable = symbolTable + 48
    let names = System.Text.Encoding.ASCII.GetBytes "\u0000fixture_symbol\u0000"
    let extendedTable = (stringTable + names.Length + 3) / 4 * 4
    let bytes = Array.zeroCreate<byte> (extendedTable + (if extendedIndex.IsSome then 8 else 0))
    let u16 at value = System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(System.Span<byte>(bytes, at, 2), value)
    let u32 at value = System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(System.Span<byte>(bytes, at, 4), value)
    let u64 at value = System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(System.Span<byte>(bytes, at, 8), value)
    Array.blit [| 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy; 2uy; 1uy; 1uy |] 0 bytes 0 7
    u16 16 1us // ET_REL
    u16 18 62us // EM_X86_64
    u32 20 1u
    u64 40 (uint64 sectionTable)
    u16 52 64us
    u16 58 64us
    if sectionCount >= 0xFF00 then
        u16 60 0us
        u64 (sectionTable + 32) (uint64 sectionCount)
    else
        u16 60 (uint16 sectionCount)
    let symtab = sectionTable + 64
    u32 (symtab + 4) 2u // SHT_SYMTAB
    u64 (symtab + 24) (uint64 symbolTable)
    u64 (symtab + 32) 48UL
    u32 (symtab + 40) 2u // linked string table
    u32 (symtab + 44) 1u // first non-local symbol
    u64 (symtab + 48) 8UL
    u64 (symtab + 56) 24UL
    let strtab = sectionTable + 2 * 64
    u32 (strtab + 4) 3u // SHT_STRTAB
    u64 (strtab + 24) (uint64 stringTable)
    u64 (strtab + 32) (uint64 names.Length)
    u64 (strtab + 48) 1UL
    Array.blit names 0 bytes stringTable names.Length
    let symbol = symbolTable + 24
    u32 symbol 1u
    bytes[symbol + 4] <- 0x11uy // STB_GLOBAL, STT_OBJECT
    u16 (symbol + 6) sectionIndex
    if sectionIndex = 0xFFF2us then u64 (symbol + 8) 4UL // common alignment
    u64 (symbol + 16) 4UL
    match extendedIndex with
    | Some index ->
        let shndx = sectionTable + 3 * 64
        u32 (shndx + 4) 18u // SHT_SYMTAB_SHNDX
        u64 (shndx + 24) (uint64 extendedTable)
        u64 (shndx + 32) 8UL
        u32 (shndx + 40) 1u // linked symbol table
        u64 (shndx + 48) 4UL
        u64 (shndx + 56) 4UL
        u32 (extendedTable + 4) index
        let storage = sectionTable + int index * 64
        u32 (storage + 4) 8u // SHT_NOBITS
        u64 (storage + 32) 4UL
        u64 (storage + 48) 4UL
    | None -> ()
    bytes

let private addSource =
    String.concat "\n"
        [ "int add_i32(int a, int b) { return a + b; }"
          "__attribute__((weak)) int fx_hook(void) { return 1; }"
          "__attribute__((weak)) int fx_default(void) { return 10; }"
          "static int fx_local(int a) { return a + 1; }"
          "extern int fx_external(int);"
          "int fx_uses(int a) { return fx_external(fx_local(a)); }"
          // A C function whose name has the C++ mangling prefix: symbols are not filtered by name.
          "int _ZN2fx3addEii(int a, int b) { return a + b; }"
          "" ]

let private mulSource =
    String.concat "\n"
        [ "int mul_i32(int a, int b) { return a * b; }"
          "int fx_hook(void) { return 2; }"
          "__attribute__((weak)) int fx_default(void) { return 20; }"
          "int fx_counter = 7;"
          "" ]

// ─── Result helpers ───

let private only (refusal: Diagnostics.Refusal) : Diagnostics.Diagnostic =
    match refusal.Diagnostics with
    | [ d ] -> d
    | ds -> failwithf "one diagnostic expected, got %d: %s" ds.Length refusal.Text

let private inspected (result: Result<ArchiveInspection.ArchiveProvenance, Diagnostics.Refusal>) : ArchiveInspection.ArchiveProvenance =
    match result with
    | Ok provenance -> provenance
    | Error refusal -> failwithf "inspection was refused: %s" refusal.Text

/// The one diagnostic of a refused result, checked to carry `code`, error severity, a nonempty
/// message and the refusal text `CODE: message`.
let private refusedWith (code: string) (result: Result<'T, Diagnostics.Refusal>) : Diagnostics.Diagnostic =
    match result with
    | Ok _ -> failwithf "%s was expected; the input was accepted" code
    | Error refusal ->
        let d = only refusal
        Assert.Equal(code, d.Code)
        Assert.False(System.String.IsNullOrWhiteSpace d.Message)
        Assert.Equal(Diagnostics.Severity.Error, d.Severity)
        Assert.Equal(code + ": " + d.Message, refusal.Text)
        d

/// An archive refusal's source is the archive file, with the reason it has no position.
let private assertArchiveSource (archivePath: string) (d: Diagnostics.Diagnostic) =
    Assert.Equal(Diagnostics.inFile (Path.GetFullPath archivePath) "a static archive is a binary file with no line or column", d.Source)

/// The sorted names member `index` defines with `binding`.
let private definedBy (provenance: ArchiveInspection.ArchiveProvenance) (index: int) (binding: ArchiveInspection.SymbolBinding) : string list =
    provenance.Members[index].DefinedSymbols
    |> List.filter (fun symbol -> symbol.Binding = binding)
    |> List.map _.Name
    |> List.sort

/// The member and binding the archive resolves `name` to.
let private resolution (provenance: ArchiveInspection.ArchiveProvenance) (name: string) : string * ArchiveInspection.SymbolBinding =
    match Map.tryFind name provenance.Symbols with
    | Some definition -> definition.MemberName, definition.Binding
    | None -> failwithf "the archive defines no symbol '%s'" name

/// The symbols `addSource` and `mulSource` define, as members 0 and 1 of an archive.
let private assertFixtureSymbols (provenance: ArchiveInspection.ArchiveProvenance) =
    Assert.Equal<string list>([ "_ZN2fx3addEii"; "add_i32"; "fx_uses" ], definedBy provenance 0 ArchiveInspection.GlobalStrong)
    Assert.Equal<string list>([ "fx_default"; "fx_hook" ], definedBy provenance 0 ArchiveInspection.Weak)
    Assert.Equal<string list>([ "fx_counter"; "fx_hook"; "mul_i32" ], definedBy provenance 1 ArchiveInspection.GlobalStrong)
    Assert.Equal<string list>([ "fx_default" ], definedBy provenance 1 ArchiveInspection.Weak)
    // A static function and an undefined reference are no definitions.
    Assert.False(provenance.Symbols.ContainsKey "fx_local")
    Assert.False(provenance.Symbols.ContainsKey "fx_external")

// ─── Positive 1: ELF64 members ───

[<Fact>]
let ``a two-member ELF archive yields its members, digests, C symbols and strong-over-weak resolution`` () =
    withScratch (fun directory ->
        let addObject = compile directory "add" [] addSource
        let mulObject = compile directory "mul" [] mulSource
        let archivePath = archive directory "libfixture.a" "rcs" [ addObject; mulObject ]
        let digest = ClangToolchain.sha256 archivePath
        // The expected digest is compared without regard to hex case.
        let provenance = ArchiveInspection.inspect archivePath (Some (digest.ToUpperInvariant())) |> inspected
        Assert.Equal(archivePath, provenance.Path)
        Assert.Equal(Path.GetFullPath archivePath, provenance.ResolvedPath)
        Assert.Equal(digest, provenance.Sha256)
        Assert.True(provenance.BitcodeTool.IsNone)
        Assert.Equal<string list>([ "add.o"; "mul.o" ], provenance.Members |> List.map _.Name)
        Assert.Equal<int list>([ 0; 1 ], provenance.Members |> List.map _.Index)
        let archiveBytes = File.ReadAllBytes archivePath
        for archiveMember, objectPath in List.zip provenance.Members [ addObject; mulObject ] do
            let objectBytes = File.ReadAllBytes objectPath
            Assert.Equal(ArchiveInspection.ElfRelocatable, archiveMember.Format)
            Assert.Equal(ClangToolchain.sha256 objectPath, archiveMember.Sha256)
            Assert.Equal(int64 objectBytes.Length, archiveMember.Size)
            // The recorded offset and size locate the object's bytes in the archive.
            Assert.Equal<byte[]>(objectBytes, Array.sub archiveBytes (int archiveMember.Offset) (int archiveMember.Size))
        assertFixtureSymbols provenance
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("add.o", ArchiveInspection.GlobalStrong), resolution provenance "add_i32")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("mul.o", ArchiveInspection.GlobalStrong), resolution provenance "mul_i32")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("add.o", ArchiveInspection.GlobalStrong), resolution provenance "_ZN2fx3addEii")
        // The strong definition in the later member replaces the weak one in the earlier member.
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("mul.o", ArchiveInspection.GlobalStrong), resolution provenance "fx_hook")
        // Weak in both members and strong in neither: the first in archive order stands.
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("add.o", ArchiveInspection.Weak), resolution provenance "fx_default")
        // With the members in the other order the strong definition still stands, and the
        // first weak one is now the other member's.
        let reversed = ArchiveInspection.inspect (archive directory "libreversed.a" "rcs" [ mulObject; addObject ]) None |> inspected
        Assert.Equal<string list>([ "mul.o"; "add.o" ], reversed.Members |> List.map _.Name)
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("mul.o", ArchiveInspection.GlobalStrong), resolution reversed "fx_hook")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("mul.o", ArchiveInspection.Weak), resolution reversed "fx_default")
        // An archive of ELF members never resolves llvm-nm.
        let untouched = ArchiveInspection.inspectWith (fun () -> failwith "llvm-nm must not be resolved for ELF members") ignore archivePath None |> inspected
        Assert.Equal(provenance, untouched))

// ─── Positive 2: LLVM bitcode members ───

[<Fact>]
let ``a bitcode archive is classified, read by llvm-nm and records the tool's identity`` () =
    withScratch (fun directory ->
        let thinObject = compile directory "add" [ "-flto=thin" ] addSource
        let fullObject = compile directory "mul" [ "-flto" ] mulSource
        let archivePath = archive directory "libbitcode.a" "rcs" [ thinObject; fullObject ]
        let provenance = ArchiveInspection.inspectWith resolveMatchingLlvmNm ignore archivePath None |> inspected
        Assert.Equal(ClangToolchain.sha256 archivePath, provenance.Sha256)
        Assert.Equal<string list>([ "add.o"; "mul.o" ], provenance.Members |> List.map _.Name)
        for archiveMember, objectPath in List.zip provenance.Members [ thinObject; fullObject ] do
            Assert.Contains(archiveMember.Format, [ ArchiveInspection.LlvmBitcode; ArchiveInspection.LlvmBitcodeWrapper ])
            Assert.Equal(ClangToolchain.sha256 objectPath, archiveMember.Sha256)
            Assert.Equal(FileInfo(objectPath).Length, archiveMember.Size)
        // The tool that read the members is recorded by absolute path and file digest.
        let tool =
            match resolveMatchingLlvmNm () with
            | Ok tool -> tool
            | Error refusal -> failwithf "llvm-nm was not resolved: %s" refusal.Text
        Assert.Equal(Some tool, provenance.BitcodeTool)
        Assert.True(Path.IsPathRooted tool.Path)
        Assert.Equal(ClangToolchain.sha256 tool.Path, tool.Sha256)
        assertFixtureSymbols provenance
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("add.o", ArchiveInspection.GlobalStrong), resolution provenance "add_i32")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("mul.o", ArchiveInspection.GlobalStrong), resolution provenance "fx_hook")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("add.o", ArchiveInspection.Weak), resolution provenance "fx_default"))

// ─── Positive 3: ELF64 and bitcode members in one archive ───

[<Fact>]
let ``a mixed ELF and bitcode archive resolves symbols across both member formats`` () =
    withScratch (fun directory ->
        let elfObject = compile directory "elf_member_with_a_long_name" [] addSource
        let bitcodeObject = compile directory "bitcode_member_with_a_long_name" [ "-flto" ] mulSource
        let archivePath = archive directory "libmixed.a" "rcs" [ elfObject; bitcodeObject ]
        // Both names exceed the 16-byte name field: the archive carries them in its extended
        // name table, each terminated by "/\n".
        Assert.Contains("elf_member_with_a_long_name.o/\n", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes archivePath))
        let provenance = ArchiveInspection.inspectWith resolveMatchingLlvmNm ignore archivePath None |> inspected
        Assert.Equal<string list>([ "elf_member_with_a_long_name.o"; "bitcode_member_with_a_long_name.o" ], provenance.Members |> List.map _.Name)
        Assert.Equal(ArchiveInspection.ElfRelocatable, provenance.Members[0].Format)
        Assert.Contains(provenance.Members[1].Format, [ ArchiveInspection.LlvmBitcode; ArchiveInspection.LlvmBitcodeWrapper ])
        Assert.Equal(ClangToolchain.sha256 elfObject, provenance.Members[0].Sha256)
        Assert.Equal(ClangToolchain.sha256 bitcodeObject, provenance.Members[1].Sha256)
        Assert.True(provenance.BitcodeTool.IsSome)
        assertFixtureSymbols provenance
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("elf_member_with_a_long_name.o", ArchiveInspection.GlobalStrong), resolution provenance "add_i32")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("bitcode_member_with_a_long_name.o", ArchiveInspection.GlobalStrong), resolution provenance "mul_i32")
        // The bitcode member's strong definition replaces the ELF member's weak one.
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("bitcode_member_with_a_long_name.o", ArchiveInspection.GlobalStrong), resolution provenance "fx_hook")
        match ArchiveInspection.verifyRequiredSymbols provenance [ "mul_i32"; "add_i32" ] with
        | Ok resolved -> Assert.Equal<string list>([ "bitcode_member_with_a_long_name.o"; "elf_member_with_a_long_name.o" ], resolved |> List.map (fun (_, definition) -> definition.MemberName))
        | Error refusal -> failwithf "both symbols are defined: %s" refusal.Text)

[<Fact>]
let ``extended member names resolve through the name table and through BSD inline names`` () =
    withScratch (fun directory ->
        let objectPath = compile directory "add" [] addSource
        let objectBytes = File.ReadAllBytes objectPath
        let ascii (text: string) = System.Text.Encoding.ASCII.GetBytes text
        let written (name: string) (bytes: byte[]) =
            let path = Path.Combine(directory, name)
            File.WriteAllBytes(path, bytes)
            path
        // GNU: /<offset> into the // table; the second name starts after the first's "/\n".
        let otherPath = compile directory "mul" [] mulSource
        let table = "first_long_member_name.o/\nsecond_long_member_name.o/\n"
        let gnu =
            ArchiveInspection.inspect (written "libgnu.a" (handArchive [ "//", ascii table; "/26", objectBytes; "/0", File.ReadAllBytes otherPath ])) None
            |> inspected
        Assert.Equal<string list>([ "second_long_member_name.o"; "first_long_member_name.o" ], gnu.Members |> List.map _.Name)
        Assert.Equal<string list>([ ClangToolchain.sha256 objectPath; ClangToolchain.sha256 otherPath ], gnu.Members |> List.map _.Sha256)
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("second_long_member_name.o", ArchiveInspection.GlobalStrong), resolution gnu "add_i32")
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(("first_long_member_name.o", ArchiveInspection.GlobalStrong), resolution gnu "mul_i32")
        // BSD: #1/<length>, the name NUL-padded in the payload's first <length> bytes.
        let name = "bsd_member_with_a_long_name.o"
        let inlineName = Array.append (ascii name) (Array.zeroCreate<byte> (32 - name.Length))
        let bsd = ArchiveInspection.inspect (written "libbsd.a" (handArchive [ "#1/32", Array.append inlineName objectBytes ])) None |> inspected
        let archiveMember = Assert.Single(bsd.Members)
        Assert.Equal(name, archiveMember.Name)
        Assert.Equal(int64 (8 + 60 + 32), archiveMember.Offset)
        Assert.Equal(int64 objectBytes.Length, archiveMember.Size)
        Assert.Equal(ClangToolchain.sha256 objectPath, archiveMember.Sha256)
        Assert.Equal<string * ArchiveInspection.SymbolBinding>((name, ArchiveInspection.GlobalStrong), resolution bsd "add_i32"))

// ─── Positive 4: the pilot's [library.artifact] table ───

/// A pilot with `libraryLines` after its [library] keys.
let private pilotWith (libraryLines: string list) : string =
    String.concat "\n"
        ([ "[library]"; "name = \"fixture\""; "header = \"fixture.h\"" ] @ libraryLines @
         [ "[output]"; "mode = \"fidelity\""; "directory = \"./out\"" ])

let private deserialized (toml: string) : Result<PilotTypes.PilotProject, Diagnostics.Refusal> =
    PilotSerializer.deserializeDiagnosed (Toml.parseOrFail toml)

[<Fact>]
let ``a pilot's library artifact survives serialization and its digest is kept lowercase`` () =
    let digest = String.replicate 32 "aB"
    let expected : PilotTypes.LibraryArtifact =
        { Path = "lib/libfixture.a"; Sha256 = digest.ToLowerInvariant(); Kind = PilotTypes.StaticArtifactKind.StaticArchive }
    let table = [ "[library.artifact]"; "path = \"lib/libfixture.a\""; $"sha256 = \"{digest}\""; "kind = \"static-archive\"" ]
    let inlineTable = [ $"artifact = {{ path = \"lib/libfixture.a\", sha256 = \"{digest}\", kind = \"static-archive\" }}" ]
    for lines in [ table; inlineTable ] do
        match deserialized (pilotWith lines) with
        | Error refusal -> failwithf "the artifact table was refused: %s" refusal.Text
        | Ok project ->
            Assert.Equal(Some expected, project.Library.Artifact)
            let serialized = PilotSerializer.toTomlString project
            Assert.Contains("[library.artifact]", serialized)
            Assert.Contains("path = \"lib/libfixture.a\"", serialized)
            Assert.Contains($"sha256 = \"{digest.ToLowerInvariant()}\"", serialized)
            Assert.Contains("kind = \"static-archive\"", serialized)
            match deserialized serialized with
            | Ok again -> Assert.Equal(project.Library, again.Library)
            | Error refusal -> failwithf "the serialized pilot was refused: %s" refusal.Text
    // A pilot without the table has no artifact, and none is serialized.
    match deserialized (pilotWith []) with
    | Error refusal -> failwithf "the pilot was refused: %s" refusal.Text
    | Ok project ->
        Assert.True(project.Library.Artifact.IsNone)
        Assert.DoesNotContain("artifact", PilotSerializer.toTomlString project)

// ─── Pilot schema negatives (FS9198 to FS9201) ───

[<Fact>]
let ``library artifact shape refusals carry FS9198 to FS9201 at their keys`` () =
    let digest = String.replicate 64 "a"
    let check (code: string) (key: string) (libraryLines: string list) =
        match deserialized (pilotWith libraryLines) with
        | Ok _ -> failwithf "%s at %s was expected; the pilot was read" code key
        | Error refusal ->
            let d = only refusal
            Assert.Equal(code, d.Code)
            Assert.False(System.String.IsNullOrWhiteSpace d.Message)
            Assert.Equal(Some ({ PilotFile = ""; Key = key; Parameter = None } : Diagnostics.PilotLocation), d.Pilot)
    let path = "path = \"lib.a\""
    let sha256 = $"sha256 = \"{digest}\""
    let kind = "kind = \"static-archive\""
    let notHex = String.replicate 64 "g"
    // FS9198: a required key is absent.
    check Diagnostics.PilotKeyMissing "library.artifact.sha256" [ "[library.artifact]"; path; kind ]
    check Diagnostics.PilotKeyMissing "library.artifact.path" [ "[library.artifact]"; sha256; kind ]
    check Diagnostics.PilotKeyMissing "library.artifact.kind" [ "[library.artifact]"; path; sha256 ]
    // FS9199: a value of another TOML type.
    check Diagnostics.PilotValueType "library.artifact.path" [ "[library.artifact]"; "path = 5"; sha256; kind ]
    check Diagnostics.PilotValueType "library.artifact" [ "artifact = \"lib.a\"" ]
    // FS9200: a key the table does not admit.
    check Diagnostics.PilotKeyUnknown "library.artifact.mode" [ "[library.artifact]"; path; sha256; kind; "mode = \"lto\"" ]
    // FS9201: a kind or digest outside the admitted values.
    check Diagnostics.PilotValueUnadmitted "library.artifact.kind" [ "[library.artifact]"; path; sha256; "kind = \"shared-object\"" ]
    check Diagnostics.PilotValueUnadmitted "library.artifact.sha256" [ "[library.artifact]"; path; "sha256 = \"abc123\""; kind ]
    check Diagnostics.PilotValueUnadmitted "library.artifact.sha256" [ "[library.artifact]"; path; $"sha256 = \"{notHex}\""; kind ]
    check Diagnostics.PilotValueUnadmitted "library.artifact.path" [ "[library.artifact]"; "path = \"\""; sha256; kind ]
    Assert.Equal<string list>([ "FS9198"; "FS9199"; "FS9200"; "FS9201" ],
                              [ Diagnostics.PilotKeyMissing; Diagnostics.PilotValueType; Diagnostics.PilotKeyUnknown; Diagnostics.PilotValueUnadmitted ])

// ─── Positive 5: the Static Binding Elision Test ───

let private headerSource = "int add_i32(int a, int b);\nint mul_i32(int a, int b);\nint absent_i32(int a);\n"

/// The fixture header and the archive defining `add_i32` and `mul_i32` (not `absent_i32`):
/// the header, the archive, its digest and the two objects.
let private elisionFixture (directory: string) =
    let header = Path.Combine(directory, "fixture.h")
    File.WriteAllText(header, headerSource)
    let addObject = compile directory "add" [] "int add_i32(int a, int b) { return a + b; }\n"
    let mulObject = compile directory "mul" [] "int mul_i32(int a, int b) { return a * b; }\n"
    let archivePath = archive directory "libfixture.a" "rcs" [ addObject; mulObject ]
    header, archivePath, ClangToolchain.sha256 archivePath, addObject, mulObject

let private artifactTable (path: string) (digest: string) : string list =
    [ "[library.artifact]"; $"path = \"{path}\""; $"sha256 = \"{digest}\""; "kind = \"static-archive\"" ]

/// Generate the fixture pilot over `header` selecting `functions`, with `libraryLines` in its
/// [library] section: the pilot's path, the structured result, and every file under the output
/// directory by relative path.
let private generated (header: string) (libraryLines: string list) (functions: string list) =
    let mutable captured = None
    let mutable files : Map<string, byte[]> = Map.empty
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> Result.mapError _.Text)
        [ header ] libraryLines [ "c_header_mode = true" ] functions [] [] (fun _ output ->
            files <-
                Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                |> Array.map (fun path -> Path.GetRelativePath(output, path), File.ReadAllBytes path)
                |> Map.ofArray)
    match captured with
    | Some (project, result) -> project, result, files
    | None -> failwith "the pilot was not generated"

/// The tables of the array of tables at `key`.
let private tablesAt (key: string) (document: TomlDocument) : TomlTable list =
    match Toml.getValue key document with
    | Some (TomlValue.Array items) ->
        items |> List.map (fun item ->
            match item with
            | TomlValue.Table table | TomlValue.InlineTable table -> table
            | other -> failwithf "%s holds %A, which is no table" key other)
    | other -> failwithf "%s is %A, which is no array of tables" key other

let private textAt (name: string) (table: TomlTable) : string =
    match Map.tryFind name table with
    | Some (TomlValue.String value) -> value
    | other -> failwithf "%s is %A, which is no string" name other

let private integerAt (name: string) (table: TomlTable) : int64 =
    match Map.tryFind name table with
    | Some (TomlValue.Integer value) -> value
    | other -> failwithf "%s is %A, which is no integer" name other

let private textsAt (name: string) (table: TomlTable) : string list =
    match Map.tryFind name table with
    | Some (TomlValue.Array items) ->
        items |> List.map (fun item ->
            match item with
            | TomlValue.String value -> value
            | other -> failwithf "%s holds %A, which is no string" name other)
    | other -> failwithf "%s is %A, which is no array" name other

[<Fact>]
let ``static binding elision: an artifact changes no generated source and adds only the link provenance record`` () =
    withScratch (fun directory ->
        let header, archivePath, digest, addObject, mulObject = elisionFixture directory
        let functions = [ "add_i32"; "mul_i32" ]
        let _, plainResult, plainFiles = generated header [] functions
        let _, boundResult, boundFiles = generated header (artifactTable archivePath digest) functions
        let plain, bound =
            match plainResult, boundResult with
            | Ok plain, Ok bound -> plain, bound
            | Error refusal, _ -> failwithf "generation without the artifact was refused: %s" refusal.Text
            | _, Error refusal -> failwithf "generation with the artifact was refused: %s" refusal.Text
        let provenanceName = "Test.Fixture.link-provenance.toml"
        let isSource (path: string) = path.EndsWith(".clef") || path.EndsWith(".fidproj")
        let sources (files: Map<string, byte[]>) = files |> Map.filter (fun path _ -> isSource path)
        // The pilot generates bindings: a .fidproj and .clef files naming both functions.
        Assert.True((sources plainFiles).ContainsKey("Test.Fixture.fidproj"))
        let clef =
            plainFiles
            |> Map.toList
            |> List.filter (fun (path: string, _) -> path.EndsWith(".clef"))
            |> List.map (fun (_, bytes: byte[]) -> System.Text.Encoding.UTF8.GetString(bytes))
            |> String.concat "\n"
        Assert.Contains("add_i32", clef)
        Assert.Contains("mul_i32", clef)
        // Every .clef file and the .fidproj is byte-for-byte the same with and without the artifact.
        Assert.Equal<string list>(sources plainFiles |> Map.toList |> List.map fst, sources boundFiles |> Map.toList |> List.map fst)
        for path, bytes in sources plainFiles |> Map.toList do
            Assert.True((bytes = boundFiles[path]), $"{path} differs between the run with the artifact and the run without it")
        // The artifact's only output is the link provenance record beside the .fidproj.
        Assert.False(plainFiles |> Map.exists (fun path _ -> path.EndsWith(".link-provenance.toml")))
        Assert.Equal<string list>(provenanceName :: (plainFiles |> Map.toList |> List.map fst) |> List.sort, boundFiles |> Map.toList |> List.map fst |> List.sort)
        for path, bytes in plainFiles |> Map.toList do
            Assert.True((bytes = boundFiles[path]), $"{path} differs between the run with the artifact and the run without it")
        let names (result: BindingGenerator.GenerationResult) = result.OutputFiles |> List.map (fun (file: string) -> Path.GetFileName(file))
        Assert.DoesNotContain(provenanceName, names plain)
        Assert.Equal<string list>(names plain @ [ provenanceName ], names bound)
        // The record is a TOML document carrying the archive's digest, each member's digest
        // and the member each selected symbol binds to.
        let document =
            match Toml.parse (System.Text.Encoding.UTF8.GetString(boundFiles[provenanceName])) with
            | Ok document -> document
            | Error error -> failwithf "the link provenance record is no TOML document: %s" error
        Assert.Equal(Some "fixture", Toml.getString "artifact.library" document)
        Assert.Equal(Some "static-archive", Toml.getString "artifact.kind" document)
        Assert.Equal(Some digest, Toml.getString "artifact.sha256" document)
        Assert.Equal(Some archivePath, Toml.getString "artifact.path" document)
        Assert.Equal(Some (Path.GetFullPath archivePath), Toml.getString "artifact.resolved_path" document)
        Assert.True((Toml.getValue "artifact.bitcode_tool" document).IsNone)
        let addDigest = ClangToolchain.sha256 addObject
        let mulDigest = ClangToolchain.sha256 mulObject
        Assert.Equal<(int64 * string * string * string * int64) list>(
            [ 0L, "add.o", "elf64-relocatable", addDigest, FileInfo(addObject).Length
              1L, "mul.o", "elf64-relocatable", mulDigest, FileInfo(mulObject).Length ],
            tablesAt "members" document |> List.map (fun table ->
                integerAt "index" table, textAt "name" table, textAt "format" table, textAt "sha256" table, integerAt "size" table))
        Assert.Equal<string list list>([ [ "add_i32" ]; [ "mul_i32" ] ], tablesAt "members" document |> List.map (textsAt "strong_symbols"))
        Assert.Equal<string list list>([ []; [] ], tablesAt "members" document |> List.map (textsAt "weak_symbols"))
        Assert.Equal<(string * string * int64 * string * string) list>(
            [ "add_i32", "add.o", 0L, addDigest, "global-strong"
              "mul_i32", "mul.o", 1L, mulDigest, "global-strong" ],
            tablesAt "bindings" document |> List.map (fun table ->
                textAt "symbol" table, textAt "member" table, integerAt "member_index" table, textAt "member_sha256" table, textAt "binding" table))
        Assert.True((Toml.getValue "companion_recipes" document).IsNone))

[<Fact>]
let ``the link provenance record round-trips through the TOML parser with a bitcode tool and companion recipes`` () =
    let memberName = "shim \"quoted\" member.o"
    let strong : ArchiveInspection.DefinedSymbol = { Name = "shim_ctor"; MemberName = memberName; MemberIndex = 0; Binding = ArchiveInspection.GlobalStrong }
    let weak : ArchiveInspection.DefinedSymbol = { Name = "_ZN4shim4weakEv"; MemberName = memberName; MemberIndex = 0; Binding = ArchiveInspection.Weak }
    let archiveMember : ArchiveInspection.ArchiveMember =
        { Index = 0; Name = memberName; Offset = 68L; Size = 1024L; Format = ArchiveInspection.LlvmBitcode
          Sha256 = String.replicate 64 "1"; DefinedSymbols = [ strong; weak ] }
    let llvmNm : ArchiveInspection.ToolIdentity = { Path = "/opt/llvm/bin/llvm-nm"; Sha256 = String.replicate 64 "2" }
    let compiler : ArchiveInspection.ToolIdentity = { Path = "/opt/llvm/bin/clang++"; Sha256 = String.replicate 64 "4" }
    let archiverTool : ArchiveInspection.ToolIdentity = { Path = "/opt/llvm/bin/llvm-ar"; Sha256 = String.replicate 64 "5" }
    let provenance : ArchiveInspection.ArchiveProvenance =
        { Path = "lib/libshim.a"; ResolvedPath = "/work/lib/libshim.a"; Sha256 = String.replicate 64 "3"
          Members = [ archiveMember ]; Symbols = Map.ofList [ strong.Name, strong; weak.Name, weak ]; BitcodeTool = Some llvmNm }
    let recipe : ArchiveInspection.CompanionArchiveRecipe =
        { Source = "shim/shim.cpp"; SourceSha256 = String.replicate 64 "6"; Compiler = compiler
          Arguments = [ "-c"; "-flto"; "-std=c++20"; "-DNAME=\"shim\"" ]; Archiver = archiverTool; Output = "lib/libshim.a" }
    let text = ArchiveInspection.renderLinkProvenanceToml "shim" provenance [ strong.Name, strong ] [ recipe ]
    let document =
        match Toml.parse text with
        | Ok document -> document
        | Error error -> failwithf "the link provenance record is no TOML document: %s\n%s" error text
    Assert.Equal(Some "shim", Toml.getString "artifact.library" document)
    Assert.Equal(Some provenance.Path, Toml.getString "artifact.path" document)
    Assert.Equal(Some provenance.ResolvedPath, Toml.getString "artifact.resolved_path" document)
    Assert.Equal(Some provenance.Sha256, Toml.getString "artifact.sha256" document)
    Assert.Equal(Some llvmNm.Path, Toml.getString "artifact.bitcode_tool.path" document)
    Assert.Equal(Some llvmNm.Sha256, Toml.getString "artifact.bitcode_tool.sha256" document)
    let memberTable = Assert.Single(tablesAt "members" document)
    Assert.Equal(memberName, textAt "name" memberTable)
    Assert.Equal(0L, integerAt "index" memberTable)
    Assert.Equal(68L, integerAt "offset" memberTable)
    Assert.Equal(1024L, integerAt "size" memberTable)
    Assert.Equal("llvm-bitcode", textAt "format" memberTable)
    Assert.Equal(archiveMember.Sha256, textAt "sha256" memberTable)
    Assert.Equal<string list>([ "shim_ctor" ], textsAt "strong_symbols" memberTable)
    Assert.Equal<string list>([ "_ZN4shim4weakEv" ], textsAt "weak_symbols" memberTable)
    let bindingTable = Assert.Single(tablesAt "bindings" document)
    Assert.Equal("shim_ctor", textAt "symbol" bindingTable)
    Assert.Equal(memberName, textAt "member" bindingTable)
    Assert.Equal(archiveMember.Sha256, textAt "member_sha256" bindingTable)
    Assert.Equal("global-strong", textAt "binding" bindingTable)
    let recipeTable = Assert.Single(tablesAt "companion_recipes" document)
    Assert.Equal(recipe.Source, textAt "source" recipeTable)
    Assert.Equal(recipe.SourceSha256, textAt "source_sha256" recipeTable)
    Assert.Equal<string list>(recipe.Arguments, textsAt "arguments" recipeTable)
    Assert.Equal(recipe.Output, textAt "output" recipeTable)
    Assert.Equal(Some compiler.Path, Toml.getString "compiler.path" recipeTable)
    Assert.Equal(Some compiler.Sha256, Toml.getString "compiler.sha256" recipeTable)
    Assert.Equal(Some archiverTool.Path, Toml.getString "archiver.path" recipeTable)
    Assert.Equal(Some archiverTool.Sha256, Toml.getString "archiver.sha256" recipeTable)
    // A binding to a member the provenance does not list is a caller error, not a rendered record.
    let stray = { strong with MemberIndex = 7 }
    Assert.Throws<Diagnostics.RefusalException>(fun () -> ArchiveInspection.renderLinkProvenanceToml "shim" provenance [ stray.Name, stray ] [] |> ignore) |> ignore

// ─── FS9224: malformed archives ───

[<Fact>]
let ``bytes that are no ar archive and truncated or malformed member headers are FS9224`` () =
    withScratch (fun directory ->
        let objectPath = compile directory "add" [] addSource
        let objectBytes = File.ReadAllBytes objectPath
        let archiveBytes = File.ReadAllBytes(archive directory "libfixture.a" "rcs" [ objectPath ])
        let ascii (text: string) = System.Text.Encoding.ASCII.GetBytes text
        let refusedBytes (name: string) (bytes: byte[]) (fragment: string) =
            let path = Path.Combine(directory, name)
            File.WriteAllBytes(path, bytes)
            let d = ArchiveInspection.inspect path None |> refusedWith Diagnostics.ArchiveMalformed
            Assert.Equal("FS9224", d.Code)
            assertArchiveSource path d
            Assert.Contains(fragment, d.Message)
        // Bytes with another magic.
        refusedBytes "not-an-archive.a" (ascii "this file is not an archive\n") "is no GNU ar archive"
        // The first member header cut short of its 60 bytes.
        refusedBytes "truncated-header.a" (Array.sub archiveBytes 0 (8 + 30)) "is truncated: 30 of 60 bytes remain"
        // A complete header whose payload extends past the end of the file.
        refusedBytes "truncated-payload.a" (Array.sub archiveBytes 0 (8 + 60 + 4)) "payload bytes, but 4 remain in the archive"
        // An empty file.
        refusedBytes "empty.a" [||] "is empty"
        // A header without the 0x60 0x0A end magic.
        let wrongEndMagic = handArchive [ "add.o/", objectBytes ]
        wrongEndMagic[8 + 58] <- 0x58uy
        refusedBytes "wrong-end-magic.a" wrongEndMagic "does not end with the 0x60 0x0A magic"
        // A size field that is no decimal number.
        let wrongSize = handArchive [ "add.o/", objectBytes ]
        wrongSize[8 + 48] <- 0x78uy
        refusedBytes "wrong-size.a" wrongSize "which is no decimal byte count"
        // An extended name reference with no extended name table before it.
        refusedBytes "no-name-table.a" (handArchive [ "/5", objectBytes ]) "no extended name table precedes it"
        // An extended name reference past the end of the table.
        refusedBytes "name-out-of-range.a" (handArchive [ "//", ascii "long_member_name.o/\n"; "/99", objectBytes ]) "outside the 20-byte extended name table"
        // An extended name with no terminating newline.
        refusedBytes "name-unterminated.a" (handArchive [ "//", ascii "long_member_name.o/ "; "/0", objectBytes ]) "is not terminated by a newline"
        // A BSD inline name longer than its payload.
        refusedBytes "bsd-name-too-long.a" (handArchive [ "#1/64", Array.zeroCreate<byte> 8 ]) "BSD extended name length 64, outside its 8-byte payload"
        // An ELF member cut short of its 64-byte header, and one whose section header table lies outside it.
        refusedBytes "truncated-elf-header.a" (handArchive [ "add.o/", Array.sub objectBytes 0 40 ]) "the ELF64 header is truncated: 40 of 64 bytes"
        refusedBytes "truncated-elf-sections.a" (handArchive [ "add.o/", Array.sub objectBytes 0 64 ]) "lies outside the 64-byte member"
        // A file that does not exist.
        let absent = Path.Combine(directory, "absent.a")
        let d = ArchiveInspection.inspect absent None |> refusedWith Diagnostics.ArchiveMalformed
        assertArchiveSource absent d
        Assert.Contains("does not exist", d.Message))

// ─── FS9225: digest mismatch ───

[<Fact>]
let ``a digest other than the expected one and a file changed during inspection are FS9225`` () =
    withScratch (fun directory ->
        let archivePath = archive directory "libfixture.a" "rcs" [ compile directory "add" [] addSource ]
        let digest = ClangToolchain.sha256 archivePath
        // Another digest than the archive's.
        let wrong = String.replicate 64 "0"
        let d = ArchiveInspection.inspect archivePath (Some wrong) |> refusedWith Diagnostics.ArchiveDigestMismatch
        Assert.Equal("FS9225", d.Code)
        assertArchiveSource archivePath d
        Assert.Contains($"has SHA-256 {digest}, not the expected {wrong}", d.Message)
        // An expected value that is no SHA-256.
        let d = ArchiveInspection.inspect archivePath (Some "abc123") |> refusedWith Diagnostics.ArchiveDigestMismatch
        Assert.Contains("is not 64 hexadecimal digits", d.Message)
        // The file's bytes change after its members are read and before the second digest.
        let mutable seen = None
        let mutate (path: string) =
            seen <- Some path
            File.AppendAllText(path, "\n")
        let d = ArchiveInspection.inspectWith ArchiveInspection.LlvmNmToolchain.resolve mutate archivePath (Some digest) |> refusedWith Diagnostics.ArchiveDigestMismatch
        Assert.Equal(Some (Path.GetFullPath archivePath), seen)
        assertArchiveSource archivePath d
        Assert.Contains("changed during inspection", d.Message)
        Assert.Contains($"not the pre-inspection {digest}", d.Message)
        // The file is deleted after its members are read.
        let rebuilt = archive directory "libdeleted.a" "rcs" [ Path.Combine(directory, "add.o") ]
        let d = ArchiveInspection.inspectWith ArchiveInspection.LlvmNmToolchain.resolve File.Delete rebuilt None |> refusedWith Diagnostics.ArchiveDigestMismatch
        Assert.Contains("could not be read again after inspection", d.Message))

// ─── FS9226: thin archives ───

[<Fact>]
let ``a thin archive is FS9226`` () =
    withScratch (fun directory ->
        let objectPath = compile directory "add" [] addSource
        let archivePath = archive directory "libthin.a" "rcsT" [ objectPath ]
        // The archiver wrote a thin archive: the magic, and no copy of the member's bytes.
        Assert.Equal("!<thin>\n", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes archivePath, 0, 8))
        Assert.True(FileInfo(archivePath).Length < FileInfo(objectPath).Length)
        let d = ArchiveInspection.inspect archivePath (Some (ClangToolchain.sha256 archivePath)) |> refusedWith Diagnostics.ArchiveThinUnsupported
        Assert.Equal("FS9226", d.Code)
        assertArchiveSource archivePath d
        Assert.Contains("is a thin archive", d.Message)
        // The magic alone decides it.
        let magicOnly = Path.Combine(directory, "magic-only.a")
        File.WriteAllText(magicOnly, "!<thin>\n")
        ArchiveInspection.inspect magicOnly None |> refusedWith Diagnostics.ArchiveThinUnsupported |> ignore)

// ─── FS9227: unsupported member formats ───

[<Fact>]
let ``a member that is neither an ELF64 relocatable object nor LLVM bitcode is FS9227`` () =
    withScratch (fun directory ->
        let note = Path.Combine(directory, "note.txt")
        File.WriteAllText(note, "a plain text member\n")
        let archivePath = archive directory "libbad.a" "rcs" [ note ]
        let d = ArchiveInspection.inspect archivePath None |> refusedWith Diagnostics.ArchiveMemberFormatUnsupported
        Assert.Equal("FS9227", d.Code)
        assertArchiveSource archivePath d
        Assert.Contains("archive member 'note.txt'", d.Message)
        Assert.Contains("is neither an ELF64 relocatable object nor LLVM bitcode", d.Message)
        // ELF objects outside the admitted class, byte order and type, each made from a real
        // relocatable object by changing one identification or header byte.
        let objectBytes = File.ReadAllBytes(compile directory "add" [] addSource)
        let patched (index: int) (value: byte) =
            let copy = Array.copy objectBytes
            copy[index] <- value
            copy
        let refusedObject (name: string) (bytes: byte[]) (fragment: string) =
            let path = Path.Combine(directory, name)
            File.WriteAllBytes(path, handArchive [ "add.o/", bytes ])
            let d = ArchiveInspection.inspect path None |> refusedWith Diagnostics.ArchiveMemberFormatUnsupported
            assertArchiveSource path d
            Assert.Contains(fragment, d.Message)
        // e_type 3 (ET_DYN) and 2 (ET_EXEC).
        refusedObject "shared.a" (patched 16 3uy) "is an ELF64 object of e_type 3"
        refusedObject "executable.a" (patched 16 2uy) "is an ELF64 object of e_type 2"
        // EI_CLASS 1 (32-bit) and EI_DATA 2 (big-endian).
        refusedObject "elf32.a" (patched 4 1uy) "is an ELF object of class 1 and data encoding 1"
        refusedObject "big-endian.a" (patched 5 2uy) "is an ELF object of class 2 and data encoding 2"
        // The unchanged object in the same hand-written archive is admitted.
        let admitted = Path.Combine(directory, "admitted.a")
        File.WriteAllBytes(admitted, handArchive [ "add.o/", objectBytes ])
        Assert.Equal<string list>([ "add.o" ], (ArchiveInspection.inspect admitted None |> inspected).Members |> List.map _.Name))

// ─── FS9228: a selected symbol the archive does not define ───

/// The one diagnostic of a refused pilot generation, checked to carry `code`, a nonempty
/// message, the pilot file with the `library.artifact` key, and to have written nothing.
let private pilotRefusedWith (code: string) (outcome: string * Result<BindingGenerator.GenerationResult, Diagnostics.Refusal> * Map<string, byte[]>) : Diagnostics.Diagnostic =
    let project, result, files = outcome
    match result with
    | Ok _ -> failwithf "%s was expected; the pilot generated" code
    | Error refusal ->
        let d = only refusal
        Assert.Equal(code, d.Code)
        Assert.False(System.String.IsNullOrWhiteSpace d.Message)
        Assert.Equal(Some ({ PilotFile = project; Key = "library.artifact"; Parameter = None } : Diagnostics.PilotLocation), d.Pilot)
        Assert.Equal("Binding generation failed: " + code + ": " + d.Message, refusal.Text)
        Assert.Empty(files)
        d

[<Fact>]
let ``an artifact with no externs bound to its library is FS9228 before output creation`` () =
    withScratch (fun directory ->
        let header, archivePath, digest, _, _ = elisionFixture directory
        let output = Path.Combine(directory, "out-zero-symbols")
        let project = Path.Combine(directory, "zero-symbols.pilot.toml")
        File.WriteAllText(project, String.concat "\n"
            ([ "[library]"
               "name = \"fixture\""
               $"header = \"{header}\"" ] @
             artifactTable archivePath digest @
             [ "[output]"
               "mode = \"fidelity\""
               $"directory = \"{output}\""
               "[options]"
               "c_header_mode = true"
               "[[namespace]]"
               "name = \"Test.Other\""
               "description = \"An extern for another library\""
               "library = \"other\""
               "functions = [\"add_i32\"]" ]))
        Assert.False(Directory.Exists output)
        match BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64 with
        | Ok _ -> failwith "an artifact with zero bound extern symbols must be refused"
        | Error refusal ->
            let d = only refusal
            Assert.Equal(Diagnostics.ArchiveMissingSymbol, d.Code)
            Assert.Equal("FS9228", d.Code)
            Assert.Equal(Some ({ PilotFile = project; Key = "library.artifact"; Parameter = None } : Diagnostics.PilotLocation), d.Pilot)
            Assert.Contains("fixture", d.Message)
        Assert.False(Directory.Exists output))

[<Fact>]
let ``a selected function the archive does not define is FS9228 at the artifact key`` () =
    withScratch (fun directory ->
        let header, archivePath, digest, _, _ = elisionFixture directory
        // absent_i32 is declared by the header and selected by the pilot; no member defines it.
        let d = generated header (artifactTable archivePath digest) [ "add_i32"; "absent_i32" ] |> pilotRefusedWith Diagnostics.ArchiveMissingSymbol
        Assert.Equal("FS9228", d.Code)
        assertArchiveSource archivePath d
        Assert.Contains("extern symbol 'absent_i32' is defined by no member of static archive", d.Message)
        Assert.Contains("(2 members, 2 defined symbols)", d.Message)
        // Without the artifact the same selection generates: the refusal is the artifact's.
        let _, plainResult, plainFiles = generated header [] [ "add_i32"; "absent_i32" ]
        Assert.True(Result.isOk plainResult)
        Assert.NotEmpty(plainFiles)
        // Each missing symbol is one diagnostic, in the order required; the others resolve.
        let provenance = ArchiveInspection.inspect archivePath (Some digest) |> inspected
        match ArchiveInspection.verifyRequiredSymbols provenance [ "absent_a"; "add_i32"; "absent_b"; "absent_a" ] with
        | Ok _ -> failwith "two symbols are undefined"
        | Error refusal ->
            Assert.Equal<string list>([ "FS9228"; "FS9228" ], refusal.Diagnostics |> List.map _.Code)
            Assert.Contains("'absent_a'", refusal.Diagnostics[0].Message)
            Assert.Contains("'absent_b'", refusal.Diagnostics[1].Message)
        match ArchiveInspection.verifyRequiredSymbols provenance [ "mul_i32"; "add_i32"; "mul_i32" ] with
        | Ok resolved ->
            Assert.Equal<(string * string * int) list>(
                [ "mul_i32", "mul.o", 1; "add_i32", "add.o", 0 ],
                resolved |> List.map (fun (symbol, definition) -> symbol, definition.MemberName, definition.MemberIndex))
        | Error refusal -> failwithf "both symbols are defined: %s" refusal.Text)

[<Fact>]
let ``an artifact digest mismatch and a missing relative artifact are refused at the artifact key before any write`` () =
    withScratch (fun directory ->
        let header, archivePath, digest, _, _ = elisionFixture directory
        let d = generated header (artifactTable archivePath (String.replicate 64 "0")) [ "add_i32" ] |> pilotRefusedWith Diagnostics.ArchiveDigestMismatch
        Assert.Equal("FS9225", d.Code)
        Assert.Contains(digest, d.Message)
        // A relative artifact path is resolved against the pilot file's directory.
        let outcome = generated header (artifactTable "missing/libabsent.a" digest) [ "add_i32" ]
        let project, _, _ = outcome
        let d = outcome |> pilotRefusedWith Diagnostics.ArchiveMalformed
        Assert.Equal("FS9224", d.Code)
        let resolvedArtifact = Path.Combine(Path.GetDirectoryName project, "missing", "libabsent.a")
        Assert.Contains($"static archive '{resolvedArtifact}' does not exist", d.Message))

// ─── FS9229: duplicate strong definitions ───

[<Fact>]
let ``a symbol two members define strongly is FS9229 naming the symbol and both members`` () =
    withScratch (fun directory ->
        let first = compile directory "first" [] "int dup_i32(void) { return 1; }\nint first_only(void) { return 1; }\n"
        let second = compile directory "second" [] "int dup_i32(void) { return 2; }\nint second_only(void) { return 2; }\n"
        let archivePath = archive directory "libduplicate.a" "rcs" [ first; second ]
        let d = ArchiveInspection.inspect archivePath None |> refusedWith Diagnostics.ArchiveDuplicateStrongSymbol
        Assert.Equal("FS9229", d.Code)
        assertArchiveSource archivePath d
        Assert.Equal("symbol 'dup_i32' has strong global definitions in archive members 'first.o' (index 0) and 'second.o' (index 1)", d.Message))

// ─── FS9230: llvm-nm unavailable ───

[<Fact>]
let ``llvm-nm resolution honors explicit configuration and consults no later source when it is wrong`` () =
    withScratch (fun directory ->
        let tool = Path.Combine(directory, "llvm-nm")
        File.WriteAllText(tool, "stand-in for the tool file; only its path and digest are read")
        let absentDirectory = Path.Combine(directory, "absent")
        let absentTool = Path.Combine(absentDirectory, "llvm-nm")
        let resolve (values: (string * string) list) =
            ArchiveInspection.LlvmNmToolchain.resolveWith (fun name -> values |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd) File.Exists
        let expected : ArchiveInspection.ToolIdentity = { Path = Path.GetFullPath tool; Sha256 = ClangToolchain.sha256 tool }
        let resolved (values: (string * string) list) =
            match resolve values with
            | Ok identity -> Assert.Equal(expected, identity)
            | Error refusal -> failwithf "llvm-nm was not resolved: %s" refusal.Text
        let unavailable (values: (string * string) list) (fragment: string) =
            let d = resolve values |> refusedWith Diagnostics.ArchiveBitcodeToolUnavailable
            Assert.Equal("FS9230", d.Code)
            Assert.Contains(fragment, d.Message)
        // FIDELITY_LLVM_NM selects the file, FIDELITY_LLVM_BIN its directory, PATH a search.
        resolved [ "FIDELITY_LLVM_NM", tool ]
        resolved [ "FIDELITY_LLVM_BIN", directory ]
        resolved [ "PATH", absentDirectory + string Path.PathSeparator + directory ]
        // FIDELITY_LLVM_NM wins over a FIDELITY_LLVM_BIN that holds no tool.
        resolved [ "FIDELITY_LLVM_NM", tool; "FIDELITY_LLVM_BIN", absentDirectory ]
        // A FIDELITY_LLVM_NM naming no file is refused although FIDELITY_LLVM_BIN and PATH hold the tool.
        unavailable [ "FIDELITY_LLVM_NM", absentTool; "FIDELITY_LLVM_BIN", directory; "PATH", directory ] $"FIDELITY_LLVM_NM selects '{absentTool}', which does not exist"
        // A FIDELITY_LLVM_BIN holding no tool is refused although PATH holds it.
        unavailable [ "FIDELITY_LLVM_BIN", absentDirectory; "PATH", directory ] $"FIDELITY_LLVM_BIN selects '{absentTool}', which does not exist"
        // No tool on PATH, and no PATH.
        unavailable [ "PATH", absentDirectory ] "llvm-nm was not found on PATH"
        unavailable [] "llvm-nm was not found on PATH")

[<Fact>]
let ``a bitcode member with no usable llvm-nm is FS9230`` () =
    withScratch (fun directory ->
        let archivePath = archive directory "libbitcode.a" "rcs" [ compile directory "add" [ "-flto" ] addSource ]
        // FIDELITY_LLVM_NM names no file; every other variable is the process's own, so a
        // fallback to FIDELITY_LLVM_BIN or PATH would resolve a tool here.
        let absentTool = Path.Combine(directory, "absent", "llvm-nm")
        let environment (name: string) =
            if name = "FIDELITY_LLVM_NM" then Some absentTool
            else System.Environment.GetEnvironmentVariable name |> Option.ofObj
        let resolveMissing () = ArchiveInspection.LlvmNmToolchain.resolveWith environment File.Exists
        let d = ArchiveInspection.inspectWith resolveMissing ignore archivePath None |> refusedWith Diagnostics.ArchiveBitcodeToolUnavailable
        Assert.Equal("FS9230", d.Code)
        Assert.Contains($"FIDELITY_LLVM_NM selects '{absentTool}', which does not exist", d.Message)
        // A resolved tool that exits nonzero: clang rejects llvm-nm's options.
        let clang = Path.GetFullPath(ClangToolchain.path ())
        let notLlvmNm : ArchiveInspection.ToolIdentity = { Path = clang; Sha256 = ClangToolchain.sha256 clang }
        let d = ArchiveInspection.inspectWith (fun () -> Ok notLlvmNm) ignore archivePath None |> refusedWith Diagnostics.ArchiveBitcodeToolUnavailable
        assertArchiveSource archivePath d
        Assert.Contains($"llvm-nm '{clang}' exited with code", d.Message)
        Assert.Contains("on bitcode member 'add.o'", d.Message)
        // A resolved tool that cannot be started.
        let notExecutable : ArchiveInspection.ToolIdentity = { Path = absentTool; Sha256 = String.replicate 64 "0" }
        let d = ArchiveInspection.inspectWith (fun () -> Ok notExecutable) ignore archivePath None |> refusedWith Diagnostics.ArchiveBitcodeToolUnavailable
        Assert.Contains("could not be started for bitcode member 'add.o'", d.Message)
        // The resolver is called once for an archive of several bitcode members.
        let both = archive directory "libtwo.a" "rcs" [ Path.Combine(directory, "add.o"); compile directory "mul" [ "-flto" ] mulSource ]
        let mutable calls = 0
        let counting () =
            calls <- calls + 1
            resolveMatchingLlvmNm ()
        ArchiveInspection.inspectWith counting ignore both None |> inspected |> ignore
        Assert.Equal(1, calls))

// ─── BSD ranlib symbol tables, STB_GNU_UNIQUE coalescing, and C++ mangled provenance (#204) ───

[<Fact>]
let ``BSD __.SYMDEF and extended __.SYMDEF SORTED index members are skipped without FS9227`` () =
    withScratch (fun directory ->
        let obj = compile directory "add" [] addSource
        let objBytes = File.ReadAllBytes obj
        let inlineSymdef = Path.Combine(directory, "libbsd_inline.a")
        File.WriteAllBytes(inlineSymdef, handArchive [ "__.SYMDEF", [| 0uy; 0uy; 0uy; 0uy |]; "add.o/", objBytes ])
        let pInline = ArchiveInspection.inspect inlineSymdef None |> inspected
        Assert.Equal(1, pInline.Members.Length)
        Assert.Equal("add.o", pInline.Members[0].Name)

        let extName = System.Text.Encoding.ASCII.GetBytes "__.SYMDEF SORTED\u0000\u0000\u0000\u0000"
        let extPayload = Array.append extName [| 1uy; 2uy; 3uy; 4uy |]
        let extSymdef = Path.Combine(directory, "libbsd_ext.a")
        File.WriteAllBytes(extSymdef, handArchive [ $"#1/{extName.Length}", extPayload; "add.o/", objBytes ])
        let pExt = ArchiveInspection.inspect extSymdef None |> inspected
        Assert.Equal(1, pExt.Members.Length)
        Assert.Equal("add.o", pExt.Members[0].Name))

[<Fact>]
let ``ELF64 STB_GNU_UNIQUE definitions coalesce as Weak across archive members without FS9229`` () =
    withScratch (fun directory ->
        // Inline variable in C++17 or explicit .gnu.linkonce / gnu_unique_object asm emits STB_GNU_UNIQUE (bind = 10).
        let uniqueAsm fnName =
            String.concat "\n"
                [ ".text"
                  $".globl {fnName}"
                  $".type {fnName}, @function"
                  $"{fnName}:"
                  "  movl $1, %eax"
                  "  ret"
                  ".data"
                  ".globl fx_gnu_unique_slot"
                  ".type fx_gnu_unique_slot, @gnu_unique_object"
                  ".size fx_gnu_unique_slot, 4"
                  "fx_gnu_unique_slot:"
                  "  .long 42"
                  "" ]
        let compileAsm (name: string) (asmText: string) =
            let sPath = Path.Combine(directory, name + ".s")
            let oPath = Path.Combine(directory, name + ".o")
            File.WriteAllText(sPath, asmText)
            run (ClangToolchain.startInfo [ "-c"; sPath; "-o"; oPath ])
            oPath
        let first = compileAsm "u1" (uniqueAsm "u1_fn")
        let second = compileAsm "u2" (uniqueAsm "u2_fn")
        let archivePath = archive directory "libunique.a" "rcs" [ first; second ]
        let p = ArchiveInspection.inspect archivePath None |> inspected
        match ArchiveInspection.verifyRequiredSymbols p [ "fx_gnu_unique_slot"; "u1_fn"; "u2_fn" ] with
        | Error r -> failwithf "expected STB_GNU_UNIQUE coalescing, got: %s" r.Text
        | Ok resolved ->
            let _, slot = resolved |> List.find (fun (sym, _) -> sym = "fx_gnu_unique_slot")
            Assert.Equal(ArchiveInspection.Weak, slot.Binding)
            Assert.Equal(0, slot.MemberIndex))

[<Fact>]
let ``ELF64 SHN_COMMON symbols are admitted as strong definitions`` () =
    withScratch (fun directory ->
        let archivePath = Path.Combine(directory, "libcommon.a")
        File.WriteAllBytes(archivePath, handArchive [ "common.o/", elf64SymbolObject 0xFFF2us 3 None ])
        let provenance = ArchiveInspection.inspect archivePath None |> inspected
        Assert.Equal<string * ArchiveInspection.SymbolBinding>(
            ("common.o", ArchiveInspection.GlobalStrong), resolution provenance "fixture_symbol"))

[<Fact>]
let ``ELF64 SHN_XINDEX resolves linked extended indices including 0xFF00`` () =
    withScratch (fun directory ->
        for sectionCount, extendedIndex in [ 5, 4u; 0xFF01, 0xFF00u ] do
            let objectBytes = elf64SymbolObject 0xFFFFus sectionCount (Some extendedIndex)
            if sectionCount = 0xFF01 then
                Assert.Equal(0us, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(System.ReadOnlySpan<byte>(objectBytes, 60, 2)))
                Assert.Equal(0xFF01UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(System.ReadOnlySpan<byte>(objectBytes, 64 + 32, 8)))
            let archivePath = Path.Combine(directory, $"libextended-{extendedIndex}.a")
            File.WriteAllBytes(archivePath, handArchive [ "extended.o/", objectBytes ])
            let provenance = ArchiveInspection.inspect archivePath None |> inspected
            match ArchiveInspection.verifyRequiredSymbols provenance [ "fixture_symbol" ] with
            | Error refusal -> failwithf "SHN_XINDEX definition was refused: %s" refusal.Text
            | Ok resolved ->
                let name, definition = Assert.Single(resolved)
                Assert.Equal("fixture_symbol", name)
                Assert.Equal("extended.o", definition.MemberName)
                Assert.Equal(ArchiveInspection.GlobalStrong, definition.Binding))

[<Fact>]
let ``ELF64 symbol with out-of-range st_shndx, unresolved SHN_XINDEX or unsupported reserved index is refused as FS9224`` () =
    withScratch (fun directory ->
        let objBytes = File.ReadAllBytes (compile directory "add" [] addSource)
        let u16 (b: byte[]) i = System.BitConverter.ToUInt16(b, i)
        let u32 (b: byte[]) i = System.BitConverter.ToUInt32(b, i)
        let u64 (b: byte[]) i = System.BitConverter.ToUInt64(b, i)
        let shoff = int (u64 objBytes 40)
        let shnum = int (u16 objBytes 60)
        let symtabHeader =
            [ 0 .. shnum - 1 ]
            |> List.map (fun idx -> shoff + idx * 64)
            |> List.find (fun hdr -> u32 objBytes (hdr + 4) = 2u)
        let symOff = int (u64 objBytes (symtabHeader + 24))
        let symCount = int (u64 objBytes (symtabHeader + 32) / 24UL)
        let targetEntryOffset =
            [ 0 .. symCount - 1 ]
            |> List.map (fun idx -> symOff + idx * 24)
            |> List.find (fun at ->
                let bind = objBytes[at + 4] >>> 4
                let shndx = u16 objBytes (at + 6)
                bind = 1uy && shndx <> 0us && shndx < 0xFF00us)
        let mutateShndx (fileName: string) (newShndx: uint16) (expectedFragment: string) =
            let mutated = Array.copy objBytes
            let shndxBytes = System.BitConverter.GetBytes(newShndx)
            mutated[targetEntryOffset + 6] <- shndxBytes[0]
            mutated[targetEntryOffset + 7] <- shndxBytes[1]
            let badArchive = Path.Combine(directory, fileName)
            File.WriteAllBytes(badArchive, handArchive [ "corrupt.o/", mutated ])
            let d = ArchiveInspection.inspect badArchive None |> refusedWith Diagnostics.ArchiveMalformed
            Assert.Equal("FS9224", d.Code)
            assertArchiveSource badArchive d
            Assert.Contains(expectedFragment, d.Message)
        mutateShndx "libbad_shndx.a" 999us "names section 999"
        mutateShndx "libbad_xindex.a" 0xFFFFus "SHN_XINDEX"
        mutateShndx "libbad_reserved.a" 0xFF00us "0xFF00")

[<Fact>]
let ``C++ mangled free function and class bindings resolve against a static archive and refuse missing class methods before writing`` () =
    withScratch (fun directory ->
        let header = Path.Combine(directory, "counter.hpp")
        File.WriteAllText(header, String.concat "\n" [
            "namespace demo {"
            "int scale_value(int x) noexcept;"
            "class Counter {"
            "  int state;"
            "public:"
            "  Counter(int init) noexcept;"
            "  ~Counter() noexcept;"
            "  int step(int delta) noexcept;"
            "};"
            "}"
            "" ])
        let completeCpp = Path.Combine(directory, "counter_complete.cpp")
        File.WriteAllText(completeCpp, String.concat "\n" [
            "#include \"counter.hpp\""
            "namespace demo {"
            "int scale_value(int x) noexcept { return x * 3; }"
            "Counter::Counter(int init) noexcept : state(init) {}"
            "Counter::~Counter() noexcept {}"
            "int Counter::step(int delta) noexcept { state += delta; return state; }"
            "}"
            "" ])
        let completeObj = Path.Combine(directory, "counter_complete.o")
        run (ClangToolchain.startInfo [ "-x"; "c++"; "-std=c++17"; "-c"; completeCpp; "-o"; completeObj ])
        let completeArchive = archive directory "libcounter.a" "rcs" [ completeObj ]
        let completeSha = ClangToolchain.sha256 completeArchive

        let outOk = Path.Combine(directory, "out-ok")
        let pilotOk = Path.Combine(directory, "counter_ok.pilot.toml")
        File.WriteAllText(pilotOk, String.concat "\n" [
            "[library]"
            "name = \"counter\""
            $"header = \"{header}\""
            ""
            "[library.artifact]"
            $"path = \"{completeArchive}\""
            $"sha256 = \"{completeSha}\""
            "kind = \"static-archive\""
            ""
            "[output]"
            "mode = \"fidelity\""
            $"directory = \"{outOk}\""
            ""
            "[cpp]"
            "symbol_library = \"counter\""
            ""
            "[[cpp.class]]"
            "name = \"demo::Counter\""
            "namespace = \"Demo.Core\""
            "kind = \"value\""
            "size = 4"
            ""
            "[[namespace]]"
            "name = \"Demo.Core\""
            "description = \"Demo C++ surface\""
            "library = \"counter\""
            "functions = [\"scale_value\"]"
            "" ])
        match BindingGenerator.generateFromProjectDiagnosed pilotOk false false Types.LP64 with
        | Error r -> failwithf "expected C++ archive provenance success, got: %s" r.Text
        | Ok res ->
            let provPath = res.OutputFiles |> List.find (fun p -> p.EndsWith(".link-provenance.toml"))
            let provDoc =
                match Toml.parse (File.ReadAllText provPath) with
                | Ok doc -> doc
                | Error err -> failwithf "link-provenance.toml failed to parse: %s" err
            let expectedSymbols = [ "_ZN4demo11scale_valueEi"; "_ZN4demo7Counter4stepEi"; "_ZN4demo7CounterC1Ei"; "_ZN4demo7CounterD1Ev" ]
            let boundSymbols = tablesAt "bindings" provDoc |> List.map (textAt "symbol") |> List.sort
            Assert.Equal<string list>(expectedSymbols, boundSymbols)
            let allClefText =
                res.OutputFiles
                |> List.filter (fun p -> p.EndsWith(".clef"))
                |> List.map File.ReadAllText
                |> String.concat "\n"
            for expectedSym in expectedSymbols do
                Assert.Contains($"[<FidelityExtern(\"counter\", \"{expectedSym}\")>]", allClefText)

        // Now omit Counter::step from the archive: FS9228 must refuse before creating out-missing.
        let incompleteCpp = Path.Combine(directory, "counter_incomplete.cpp")
        File.WriteAllText(incompleteCpp, String.concat "\n" [
            "#include \"counter.hpp\""
            "namespace demo {"
            "int scale_value(int x) noexcept { return x * 3; }"
            "Counter::Counter(int init) noexcept : state(init) {}"
            "Counter::~Counter() noexcept {}"
            "}"
            "" ])
        let incompleteObj = Path.Combine(directory, "counter_incomplete.o")
        run (ClangToolchain.startInfo [ "-x"; "c++"; "-std=c++17"; "-c"; incompleteCpp; "-o"; incompleteObj ])
        let incompleteArchive = archive directory "libcounter_incomplete.a" "rcs" [ incompleteObj ]
        let incompleteSha = ClangToolchain.sha256 incompleteArchive
        let outMissing = Path.Combine(directory, "out-missing")
        let pilotMissing = Path.Combine(directory, "counter_missing.pilot.toml")
        File.WriteAllText(pilotMissing, (File.ReadAllText pilotOk).Replace(completeArchive, incompleteArchive).Replace(completeSha, incompleteSha).Replace(outOk, outMissing))
        match BindingGenerator.generateFromProjectDiagnosed pilotMissing false false Types.LP64 with
        | Ok _ -> failwith "expected FS9228 when C++ class method symbol is absent from archive"
        | Error refusal ->
            let d = only refusal
            Assert.Equal("FS9228", d.Code)
            Assert.Contains("_ZN4demo7Counter4stepEi", d.Message)
            Assert.False(Directory.Exists outMissing))
