namespace Farscape.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit
open Farscape.Core
open Farscape.Tests.SubstantiationTests

// #210 / #45 / #46 / #57 / #58: Pure .NET/F# scaffolding for Fidelity.Libc (M1)
// and SQLite (M2): real system header binding generation, coded FS9xxx refusal
// controls, and SubstantiationSeams.verifyDischarge obligation-census verification.
// Runs strictly against Farscape + Clang without Composer.dll.
module private TargetLibraryCensusFixtures =
    open Farscape.Core
    open Farscape.Core.Substantiation
    open Farscape.Core.SubstantiationBoundary
    open Farscape.Core.SubstantiationProof

    let buildBoundary
        (pilotStub: SubstantiationBoundary.FileDigest)
        (clangTarget: string) : SubstantiationBoundary.BoundaryEvidence =
        { Strata =
              { ForeignCalls = true
                MeasuredRecords = false
                StaticArchive = false
                ErrorConventions = true
                Ownership = true
                ZeroCopy = true
                Callbacks = false
                Nullability = true }
          Files = [| pilotStub |]
          LinkMode = "runtime-shared-library"
          Libraries = [||]
          ExternSymbols = [||]
          ClangIdentity = "clang"
          ClangTarget = clangTarget
          TargetDataModel = "LP64"
          Layouts = [||]
          Enums = [||]
          RestsOn = [||]
          RequiredSeamChecks = [| "scalar-handle-link-and-call"; "owned-handle-lifecycle"; "barewire-zero-copy"; "falsification-refusal-controls" |] }

    let buildProof
        (total: int)
        (libCount: int)
        (obligationsSha: string)
        (querySha: string)
        (receiptSha: string)
        (premisesQuerySha: string)
        (premisesOutputSha: string) : SubstantiationProof.ProofSummary =
        { LibraryObligations = libCount
          LibraryDischargedUnsatCount = libCount
          LibraryPremisesSatCount = libCount
          HarnessObligations = total - libCount
          TotalObligations = total
          DischargedUnsatCount = total
          PendingOpenCount = 0
          RefutedCount = 0
          PremisesSatCount = total
          ObligationsSha256 = obligationsSha
          QuerySha256 = querySha
          ReceiptSha256 = receiptSha
          PremisesQuerySha256 = premisesQuerySha
          PremisesOutputSha256 = premisesOutputSha
          CompilerPremiseReceiptsCount = 2
          CompilerNonVacuityReceiptsCount = 2
          StageReceipts = [||]
          Solver = { Executable = "cvc5"; Sha256 = String.replicate 64 "0"; Version = "1.0"; Arguments = [||] } }

    let buildManifest
        (targetLib: string)
        (artifactsDir: string)
        (fidproj: Substantiation.FileDigest)
        (compiler: Substantiation.FileDigest)
        (dotnet: Substantiation.FileDigest)
        (harness: Substantiation.FileDigest)
        (harnessProj: Substantiation.FileDigest)
        (compilerLog: Substantiation.FileDigest)
        (psg0: Substantiation.FileDigest)
        (witness: Substantiation.FileDigest)
        (boundary: SubstantiationBoundary.BoundaryEvidence)
        (proof: SubstantiationProof.ProofSummary) : Substantiation.Manifest =
        { SchemaVersion = 1
          Package = targetLib
          FidprojPath = fidproj.Path
          FidprojSha256 = fidproj.Sha256
          Sources = [||]
          Compiler = compiler
          Dotnet = dotnet
          Harness = harness
          HarnessProject = harnessProj
          CompilerLog = compilerLog
          Psg0 = psg0
          WitnessUnits = witness
          AllowWarnings = false
          WarningsAsErrors = true
          ElevatedWarningCodes = [||]
          IntegritySha256 = ""
          ArtifactsDirectory = artifactsDir
          Boundary = boundary
          Proof = proof }
        |> Substantiation.seal

module TargetLibrarySeamScaffoldingTests =

    let private writeUtf8 path (text: string) =
        File.WriteAllText(path, text, UTF8Encoding(false))

    let private writeLines path (lines: string list) =
        writeUtf8 path (String.concat "\n" lines + "\n")

    let private readGeneratedTexts outputDir =
        Directory.EnumerateFiles(outputDir, "*.clef", SearchOption.AllDirectories)
        |> Seq.sort
        |> Seq.map (fun p -> p, File.ReadAllText p)
        |> Seq.toList

    let private assertNoRawPointers (sources: (string * string) list) =
        for path, text in sources do
            Assert.False(text.Contains("nativeint", StringComparison.Ordinal), $"raw nativeint leaked in {path}")
            Assert.False(text.Contains("nativeptr", StringComparison.Ordinal), $"raw nativeptr leaked in {path}")

    let private writeProbeArchive (work: string) (name: string) (cBody: string) =
        let cPath = Path.Combine(work, name + ".c")
        let objPath = Path.Combine(work, name + ".o")
        let arPath = Path.Combine(work, "lib" + name + ".a")
        writeUtf8 cPath cBody
        run work ("compile-" + name) (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; cPath; "-o"; objPath ]) |> ignore
        run work ("ar-" + name) (ClangToolchain.arStartInfo [ "rcs"; arPath; objPath ]) |> ignore
        arPath, ClangToolchain.sha256 arPath

    let private clangTargetTriple (work: string) =
        (run work "clang-dumpmachine" (ClangToolchain.startInfo [ "-dumpmachine" ])).Trim()

    [<Fact>]
    let ``Fidelity.Libc M1 pilot generates zero-raw-pointer surface and coded refusal controls`` () =
        let work = TestHelpers.TestScratch.create "m1-libc-seam-scaffold"
        try
            for hdr in [ "/usr/include/stdlib.h"; "/usr/include/stdio.h"; "/usr/include/unistd.h"; "/usr/include/fcntl.h" ] do
                Assert.True(File.Exists hdr, $"Required system header missing: {hdr}")

            let outDir = Path.Combine(work, "generated")
            let pilotPath = Path.Combine(work, "FidelityLibc.pilot.toml")
            writeLines pilotPath
                [ "[library]"
                  "name = \"c\""
                  "headers = [\"/usr/include/stdlib.h\", \"/usr/include/stdio.h\", \"/usr/include/unistd.h\", \"/usr/include/fcntl.h\"]"
                  "include_paths = [\"/usr/include\"]"
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = " + quote outDir
                  "[error_conventions]"
                  "default = \"none\""
                  "[annotations.nonnull]"
                  "free = [0]"
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "[[options.bindings]]"
                  "name = \"malloc\""
                  "return_handle = \"MallocBuffer\""
                  "resource = { kind = \"owned_nullable_return\", release_binding = \"Fidelity.Libc.Boundary.free\", release_parameter = 0, hypothesis = { identity = \"libc.malloc.owned\", version = \"1\", source = \"stdlib.h\" } }"
                  "[[options.bindings]]"
                  "name = \"free\""
                  "parameter_handles = [\"MallocBuffer\"]"
                  "[[options.bindings]]"
                  "name = \"fopen\""
                  "return_handle = \"FILE\""
                  "resource = { kind = \"owned_nullable_return\", release_binding = \"Fidelity.Libc.Boundary.fclose\", release_parameter = 0, hypothesis = { identity = \"libc.file.owned\", version = \"1\", source = \"stdio.h\" } }"
                  "read_only_buffers = ["
                  "  { parameter = 0, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"libc.fopen.path\", version = \"1\", source = \"stdio.h\" } },"
                  "  { parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"libc.fopen.mode\", version = \"1\", source = \"stdio.h\" } }"
                  "]"
                  "[[options.bindings]]"
                  "name = \"fclose\""
                  "parameter_handles = [\"FILE\"]"
                  "[[options.bindings]]"
                  "name = \"read\""
                  "reference_parameters = [1]"
                  "reference_elements = [\"\", \"unsigned char\"]"
                  "writable_buffers = [{ parameter = 1, count_parameter = 2, hypothesis = { identity = \"posix.read.borrow\", version = \"2024\", source = \"unistd.h\" } }]"
                  "result_range = { identity = \"posix.read.range\", version = \"2024\", source = \"unistd.h\", lower = { constant = -1 }, upper = { argument = 2 } }"
                  "[[options.bindings]]"
                  "name = \"write\""
                  "read_only_buffers = [{ parameter = 1, content = \"bytes\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"posix.write.buf\", version = \"2024\", source = \"unistd.h\" } }]"
                  "[[options.bindings]]"
                  "name = \"close\""
                  "result_range = { identity = \"posix.close.range\", version = \"2024\", source = \"unistd.h\", lower = { constant = -1 }, upper = { constant = 0 } }"
                  "[[options.bindings]]"
                  "name = \"atexit\""
                  "nonnull_callbacks = [0]"
                  "callback_invocations = [{ parameter = 0, scope = \"process_exit\", exit_binding = \"Fidelity.Libc.Boundary.exit\", success_status = 0, identity = \"libc.atexit.exit\", version = \"2024\", source = \"stdlib.h\" }]"
                  "[[namespace]]"
                  "name = \"Fidelity.Libc.Boundary\""
                  "library = \"c\""
                  "description = \"Fidelity.Libc M1 seam validation boundary\""
                  "functions = [\"abs\", \"malloc\", \"free\", \"fopen\", \"fclose\", \"read\", \"write\", \"close\", \"atexit\", \"exit\", \"putchar\"]" ]

            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilotPath false false Types.LP64)
            |> accepted |> ignore

            let generatedFiles = readGeneratedTexts outDir
            Assert.True(generatedFiles.Length >= 2)
            assertNoRawPointers generatedFiles
            let combined = generatedFiles |> List.map snd |> String.concat "\n"
            for expectedFragment in
                [ "Expr<ResourceDescriptor>"
                  "Expr<WritableBufferDescriptor>"
                  "Expr<ReadOnlyBufferDescriptor>"
                  "Expr<ResultRangeDescriptor>"
                  "Expr<ProcessExitCallbackDescriptor>"
                  "ReadOnlyView<byte>" ] do
                Assert.Contains(expectedFragment, combined)

            // Exact refusal control 1: FS9154 mismatched handle release (fopen + free).
            let badReleasePilot = Path.Combine(work, "BadRelease.pilot.toml")
            File.ReadAllText(pilotPath)
                .Replace("release_binding = \"Fidelity.Libc.Boundary.fclose\"", "release_binding = \"Fidelity.Libc.Boundary.free\"")
            |> writeUtf8 badReleasePilot
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed badReleasePilot false false Types.LP64)
            |> refused "FS9154"

            // Exact refusal control 2: FS9228 missing symbol against a static archive contract.
            let partialArchive, partialSha = writeProbeArchive work "libc_partial" "int libc_stub_only(int x) { return x; }\n"
            let missingSymPilot = Path.Combine(work, "MissingArchiveSym.pilot.toml")
            let withArtifact =
                File.ReadAllText(pilotPath).Replace(
                    "[output]",
                    "[library.artifact]\nkind = \"static-archive\"\npath = " + quote partialArchive + "\nsha256 = " + quote partialSha + "\n[output]")
            writeUtf8 missingSymPilot withArtifact
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed missingSymPilot false false Types.LP64)
            |> refused "FS9228"

            // Exact refusal control 3: FS9137 process_exit callback missing success_status.
            let missingStatusPilot = Path.Combine(work, "MissingExitStatus.pilot.toml")
            File.ReadAllText(pilotPath).Replace("success_status = 0, ", "")
            |> writeUtf8 missingStatusPilot
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed missingStatusPilot false false Types.LP64)
            |> refused "FS9137"
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``SQLite M2 pilot generates Phase B zero-copy contracts and coded refusal controls`` () =
        let sqliteHeader = "/usr/include/sqlite3.h"
        Assert.True(File.Exists sqliteHeader, $"Required system header missing: {sqliteHeader}")
        let work = TestHelpers.TestScratch.create "m2-sqlite-seam-scaffold"
        try
            let outDir = Path.Combine(work, "generated")
            let pilotPath = Path.Combine(work, "Sqlite.pilot.toml")
            writeLines pilotPath
                [ "[library]"
                  "name = \"sqlite3\""
                  "headers = [\"/usr/include/sqlite3.h\"]"
                  "include_paths = [\"/usr/include\"]"
                  "macro_prefixes = [\"SQLITE_\"]"
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = " + quote outDir
                  "[error_conventions]"
                  "default = \"none\""
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "[[options.bindings]]"
                  "name = \"sqlite3_open_v2\""
                  "reference_parameters = [1]"
                  "read_only_buffers = ["
                  "  { parameter = 0, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.open.filename\", version = \"1\", source = \"sqlite3.h\" } },"
                  "  { parameter = 3, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.open.zvfs\", version = \"1\", source = \"sqlite3.h\" } }"
                  "]"
                  "buffer_absences = [{ parameter = 3, hypothesis = { identity = \"sqlite.open.zvfs.absent\", version = \"1\", source = \"sqlite3.h\" } }]"
                  "[[options.bindings]]"
                  "name = \"sqlite3_prepare_v2\""
                  "reference_parameters = [3, 4]"
                  "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.prepare_v2.sql\", version = \"1\", source = \"sqlite3.h\" } }]"
                  "[[options.bindings]]"
                  "name = \"sqlite3_prepare_v3\""
                  "reference_parameters = [4, 5]"
                  "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.prepare_v3.sql\", version = \"1\", source = \"sqlite3.h\" } }]"
                  "[[options.bindings]]"
                  "name = \"sqlite3_bind_text\""
                  "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
                  "read_only_buffers = [{ parameter = 2, content = \"text\", extent = { count_parameter = 3 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Fidelity.Sqlite.Boundary.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_finalize\", parameter = 0 }] }, hypothesis = { identity = \"sqlite.bind.text.static\", version = \"1\", source = \"sqlite3.h\" } }]"
                  "[[options.bindings]]"
                  "name = \"sqlite3_bind_blob\""
                  "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
                  "read_only_buffers = [{ parameter = 2, content = \"bytes\", extent = { count_parameter = 3 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Fidelity.Sqlite.Boundary.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_finalize\", parameter = 0 }] }, hypothesis = { identity = \"sqlite.bind.blob.static\", version = \"1\", source = \"sqlite3.h\" } }]"
                  "[[options.bindings]]"
                  "name = \"sqlite3_column_text\""
                  "borrowed_result = { content = \"text\", owner_parameter = 0, extent = { binding = \"Fidelity.Sqlite.Boundary.sqlite3_column_bytes\", arguments = [0, 1] }, invalidated_by = [{ binding = \"Fidelity.Sqlite.Boundary.sqlite3_step\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_reset\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_finalize\", parameter = 0 }], hypothesis = { identity = \"sqlite.column.text\", version = \"1\", source = \"sqlite3.h\" } }"
                  "[[options.bindings]]"
                  "name = \"sqlite3_column_blob\""
                  "borrowed_result = { content = \"bytes\", owner_parameter = 0, extent = { binding = \"Fidelity.Sqlite.Boundary.sqlite3_column_bytes\", arguments = [0, 1] }, invalidated_by = [{ binding = \"Fidelity.Sqlite.Boundary.sqlite3_step\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_reset\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_finalize\", parameter = 0 }], hypothesis = { identity = \"sqlite.column.blob\", version = \"1\", source = \"sqlite3.h\" } }"
                  "[[options.bindings]]"
                  "name = \"sqlite3_errmsg\""
                  "borrowed_result = { content = \"text\", owner_parameter = 0, extent = { nul_terminated = true }, invalidated_by = [{ any_owner_call = true }], hypothesis = { identity = \"sqlite.errmsg\", version = \"1\", source = \"sqlite3.h\" } }"
                  "[[options.bindings]]"
                  "name = \"sqlite3_bind_parameter_index\""
                  "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.bind_param_index.zname\", version = \"1\", source = \"sqlite3.h\" } }]"
                  "[[namespace]]"
                  "name = \"Fidelity.Sqlite.Boundary\""
                  "library = \"sqlite3\""
                  "description = \"SQLite M2 Phase B zero-copy boundary\""
                  "functions = [\"sqlite3_open_v2\", \"sqlite3_close_v2\", \"sqlite3_prepare_v2\", \"sqlite3_prepare_v3\", \"sqlite3_step\", \"sqlite3_reset\", \"sqlite3_finalize\", \"sqlite3_clear_bindings\", \"sqlite3_bind_text\", \"sqlite3_bind_blob\", \"sqlite3_column_text\", \"sqlite3_column_blob\", \"sqlite3_column_bytes\", \"sqlite3_errmsg\", \"sqlite3_bind_parameter_index\"]" ]

            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilotPath false false Types.LP64)
            |> accepted |> ignore

            let generatedFiles = readGeneratedTexts outDir
            Assert.True(generatedFiles.Length >= 2)
            assertNoRawPointers generatedFiles
            let combined = generatedFiles |> List.map snd |> String.concat "\n"
            for expectedFragment in
                [ "Expr<ReadOnlyBufferDescriptor>"
                  "Expr<BufferAbsenceContract>"
                  "Expr<BorrowedResultViewDescriptor>"
                  "EntrySentinel (\"SQLITE_STATIC\", 0)"
                  "ReadOnlyView<byte>"
                  "let sqlite3_open_v2 (filename: string)" ] do
                Assert.Contains(expectedFragment, combined)

            // Negative refusal control 1: FS9180 SQLITE_STATIC paired with during_call.
            let duringStaticPilot = Path.Combine(work, "SqliteDuringStatic.pilot.toml")
            File.ReadAllText(pilotPath).Replace(
                "{ kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Fidelity.Sqlite.Boundary.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Fidelity.Sqlite.Boundary.sqlite3_finalize\", parameter = 0 }] }",
                "{ kind = \"during_call\" }")
            |> writeUtf8 duringStaticPilot
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed duringStaticPilot false false Types.LP64)
            |> refused "FS9180"

            // Negative refusal control 2: FS9257 BufferAbsence without matching ReadOnlyBuffer.
            let orphanAbsencePilot = Path.Combine(work, "SqliteOrphanAbsence.pilot.toml")
            File.ReadAllText(pilotPath).Replace(
                "buffer_absences = [{ parameter = 3, hypothesis = { identity = \"sqlite.open.zvfs.absent\", version = \"1\", source = \"sqlite3.h\" } }]",
                "buffer_absences = [{ parameter = 2, hypothesis = { identity = \"sqlite.open.flags.absent\", version = \"1\", source = \"sqlite3.h\" } }]")
            |> writeUtf8 orphanAbsencePilot
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed orphanAbsencePilot false false Types.LP64)
            |> refused "FS9257"

            // Negative refusal control 3: FS9228 missing SQLite symbol in static archive contract.
            let stubArchive, stubSha = writeProbeArchive work "sqlite_incomplete" "int sqlite3_libversion_number(void) { return 0; }\n"
            let missingArchiveSymPilot = Path.Combine(work, "SqliteMissingArchive.pilot.toml")
            File.ReadAllText(pilotPath).Replace(
                "[output]",
                "[library.artifact]\nkind = \"static-archive\"\npath = " + quote stubArchive + "\nsha256 = " + quote stubSha + "\n[output]")
            |> writeUtf8 missingArchiveSymPilot
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed missingArchiveSymPilot false false Types.LP64)
            |> refused "FS9228"
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``obligation census helper reconciles SAT(H), UNSAT(H and not C), and refuses open or mismatched verdicts with FS9253`` () =
        let work = TestHelpers.TestScratch.create "obligation-census"
        try
            let verifyCensusFor targetLib total libCount =
                let artifactsDir = Path.Combine(work, targetLib + "-artifacts")
                Directory.CreateDirectory artifactsDir |> ignore
                let outcomes =
                    [| for i in 1 .. total -> {| ObligationId = sprintf "%s.ob.%d" targetLib i; Verdict = "unsat" |} |]
                let dischargePath = Path.Combine(artifactsDir, "06c_psg_discharge.json")
                let dischargeJson = JsonSerializer.Serialize({| Stage = "psg"; Outcomes = outcomes |})
                writeUtf8 dischargePath dischargeJson
                let premisesPath = Path.Combine(artifactsDir, "06b_premises_sat.stdout")
                writeLines premisesPath [ for _ in 1 .. total -> "sat" ]
                let stubFile name : Substantiation.FileDigest =
                    let p = Path.Combine(work, targetLib + "-" + name)
                    writeUtf8 p (targetLib + ":" + name)
                    { Substantiation.Path = p; Sha256 = ClangToolchain.sha256 p }
                let pilotStub : SubstantiationBoundary.FileDigest =
                    let p = Path.Combine(work, targetLib + "-pilot.toml")
                    writeUtf8 p (targetLib + ":pilot")
                    { SubstantiationBoundary.Path = p; Sha256 = ClangToolchain.sha256 p; Kind = "pilot" }
                let boundary = TargetLibraryCensusFixtures.buildBoundary pilotStub (clangTargetTriple work)
                let proof =
                    TargetLibraryCensusFixtures.buildProof
                        total
                        libCount
                        (stubFile "06a.json").Sha256
                        (stubFile "06b.smt2").Sha256
                        (ClangToolchain.sha256 dischargePath)
                        (stubFile "06b_sat.smt2").Sha256
                        (ClangToolchain.sha256 premisesPath)
                let manifest =
                    TargetLibraryCensusFixtures.buildManifest
                        targetLib
                        artifactsDir
                        (stubFile "proj.fidproj")
                        (stubFile "compiler.dll")
                        (stubFile "dotnet")
                        (stubFile "harness.clef")
                        (stubFile "harness.fidproj")
                        (stubFile "compiler.log")
                        (stubFile "01_psg0.json")
                        (stubFile "10_witness_units.json")
                        boundary
                        proof
                let summary = SubstantiationSeams.verifyDischarge manifest |> accepted
                Assert.Equal(total, summary.TotalObligations)
                Assert.Equal(total, summary.DischargedUnsatCount)
                Assert.Equal(total, summary.PremisesSatCount)
                Assert.Equal(libCount, summary.LibraryObligations)
                Assert.Equal(libCount, summary.LibraryDischargedUnsatCount)
                Assert.Equal(libCount, summary.LibraryPremisesSatCount)
                Assert.Equal(0, summary.PendingOpenCount)
                Assert.Equal(0, summary.RefutedCount)

                // Negative census control: mismatch between SAT(H) lines and UNSAT count refuses FS9253.
                let badPremisesManifest =
                    { manifest with Proof = { manifest.Proof with PremisesSatCount = total - 1 } }
                    |> Substantiation.seal
                SubstantiationSeams.verifyDischarge badPremisesManifest |> refused "FS9253"

                // Negative census control: non-zero PendingOpenCount refuses FS9253.
                let openObligationManifest =
                    { manifest with Proof = { manifest.Proof with PendingOpenCount = 1 } }
                    |> Substantiation.seal
                SubstantiationSeams.verifyDischarge openObligationManifest |> refused "FS9253"

            verifyCensusFor "Fidelity.Libc" 12 8
            verifyCensusFor "Fidelity.Sqlite" 18 14
        finally
            Directory.Delete(work, true)
