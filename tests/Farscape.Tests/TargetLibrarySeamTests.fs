namespace Farscape.Tests

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Fidelity.Data.JSON
open Xunit
open Farscape.Core
open Farscape.Tests.SubstantiationTests

// #210 / #45 / #46 / #57 / #58: Pure .NET/F# scaffolding for Fidelity.Libc (M1)
// and SQLite (M2): real system header binding generation, coded FS9xxx refusal
// controls, and SubstantiationSeams.verifyDischarge obligation-census verification.
// Runs strictly against Farscape + Clang without Composer.dll.
module private TargetLibraryCensusFixtures =
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
          SourceInputsSha256 = ""
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
                  "ReadOnlyView<int>" ] do
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

    let private sqlitePilotLines (outDir: string) (artifactLines: string list) =
        [ "[library]"
          "name = \"sqlite3\""
          "headers = [\"/usr/include/sqlite3.h\"]"
          "include_paths = [\"/usr/include\"]"
          "macro_prefixes = [\"SQLITE_\"]" ]
        @ artifactLines @
        [ "[output]"
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

    [<Fact>]
    let ``SQLite M2 pilot generates Phase B zero-copy contracts and coded refusal controls`` () =
        let sqliteHeader = "/usr/include/sqlite3.h"
        Assert.True(File.Exists sqliteHeader, $"Required system header missing: {sqliteHeader}")
        let work = TestHelpers.TestScratch.create "m2-sqlite-seam-scaffold"
        try
            let outDir = Path.Combine(work, "generated")
            let pilotPath = Path.Combine(work, "Sqlite.pilot.toml")
            writeLines pilotPath (sqlitePilotLines outDir [])

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
                  "ReadOnlyView<int>"
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
                    [ for i in 1 .. total ->
                        JsonValue.Object [
                            "ObligationId", JsonValue.String (sprintf "%s.ob.%d" targetLib i)
                            "Verdict", JsonValue.String "unsat"
                        ] ]
                let dischargePath = Path.Combine(artifactsDir, "06c_psg_discharge.json")
                let dischargeJson =
                    JsonValue.Object [
                        "Stage", JsonValue.String "psg"
                        "Outcomes", JsonValue.Array outcomes
                    ]
                    |> Json.serialize
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

    [<Fact>]
    let ``descriptor quotation admission accepts all BAREWire descriptors and refuses unknown owners with FS9233`` () =
        let work = TestHelpers.TestScratch.create "descriptor-quotation-admission"
        try
            let admittedPath = Path.Combine(work, "AdmittedDescriptors.clef")
            let admittedDescriptors =
                [ "d01", "StructDescriptor"
                  "d02", "PeripheralDescriptor"
                  "d03", "MemorySpaceDescriptor"
                  "d04", "FunctionDescriptor"
                  "d05", "ResultRangeDescriptor"
                  "d06", "ResourceDescriptor"
                  "d07", "WritableBufferDescriptor"
                  "d08", "ReadOnlyBufferDescriptor"
                  "d09", "BufferAbsenceContract"
                  "d10", "CallbackInvocationContract"
                  "d11", "CallbackDescriptor"
                  "d12", "ClosedCallbackDescriptor"
                  "d13", "ScopedCallbackDescriptor"
                  "d14", "RetainedCallbackDescriptor"
                  "d15", "ProcessExitCallbackDescriptor"
                  "d16", "CallbackAbsenceContract"
                  "d17", "CallbackContextDescriptor"
                  "d18", "CountedEntryArrayDescriptor"
                  "d19", "BorrowedResultViewDescriptor"
                  "d20", "ViewLayoutDescriptor"
                  "d21", "SharedBufferPlacement"
                  "d22", "SharedBufferRegionDescriptor"
                  "d23", "FlexibleArrayDescriptor"
                  "d24", "MappedReturnDescriptor" ]
            writeLines admittedPath
                ("module Fidelity.DescriptorAdmission.Test"
                 :: (admittedDescriptors |> List.map (fun (sym, ty) -> $"let {sym} : Expr<{ty}> = <@ () @>")))
            let digest = Substantiation.inspectSource Set.empty admittedPath |> accepted
            for (sym, _) in admittedDescriptors do
                Assert.Contains("Fidelity.DescriptorAdmission.Test." + sym, digest.CompileTimeDeclarations)

            // Both SharedBufferPlacement and SharedBufferRegionDescriptor independently activate the ZeroCopy boundary stratum.
            let fidproj = Path.Combine(work, "AdmittedDescriptors.fidproj")
            writeLines fidproj
                [ "[package]"
                  "name = \"AdmittedDescriptors\""
                  "version = \"0.1.0\""
                  "[build]"
                  "sources = [\"AdmittedDescriptors.clef\"]"
                  "[platform]"
                  "os = \"linux\""
                  "arch = \"x86_64\"" ]
            for descType in [ "SharedBufferPlacement"; "SharedBufferRegionDescriptor" ] do
                let descPath = Path.Combine(work, descType + "Probe.clef")
                let descText = $"module Fidelity.DescriptorAdmission.{descType}Probe\nlet d : Expr<{descType}> = <@ () @>\n"
                writeUtf8 descPath descText
                let evidence =
                    SubstantiationBoundary.evaluate
                        { FidprojPath = fidproj
                          PackageName = "AdmittedDescriptors"
                          LinkLibraries = [||]
                          LinkObjects = [||]
                          PilotPath = None
                          LinkProvenancePath = None
                          Sources = [| descPath, descText |]
                          WorkDirectory = work }
                    |> accepted
                Assert.True(evidence.Strata.ZeroCopy, $"Expected ZeroCopy stratum to be active for {descType}")
                Assert.Contains("barewire-zero-copy", evidence.RequiredSeamChecks)

            // Stale un-owned descriptor spelling must be refused with FS9233.
            let stalePath = Path.Combine(work, "StaleBorrowedDescriptor.clef")
            writeLines stalePath
                [ "module Fidelity.DescriptorAdmission.Stale"
                  "let staleView : Expr<BorrowedResultDescriptor> = <@ () @>" ]
            Substantiation.inspectSource Set.empty stalePath |> refused "FS9233"

            // Unknown descriptor quotation type must be refused with FS9233.
            let unknownPath = Path.Combine(work, "UnknownDescriptor.clef")
            writeLines unknownPath
                [ "module Fidelity.DescriptorAdmission.Unknown"
                  "let unknownDesc : Expr<BogusDescriptor> = <@ () @>" ]
            Substantiation.inspectSource Set.empty unknownPath |> refused "FS9233"
        finally
            Directory.Delete(work, true)

    [<Theory>]
    [<InlineData("src/", "Fidelity.Sqlite.Boundary.fidproj", "src/Types.clef")>]
    [<InlineData("src/Native/", "Fidelity.Sqlite.Boundary.fidproj", "src/Native/Types.clef")>]
    [<InlineData("Bindings/", "Fidelity.Sqlite.Boundary.fidproj", "Bindings/Types.clef")>]
    [<InlineData("Bindings/Native/", "Fidelity.Sqlite.Boundary.fidproj", "Bindings/Native/Types.clef")>]
    [<InlineData("generated/", "generated/Fidelity.Sqlite.Boundary.fidproj", "Types.clef")>]
    let ``Static Binding Elision Test preserves byte-identical SQLite Clef and fidproj outputs between dynamic and static archive modes``
        (output: string, expectedManifest: string, expectedTypes: string) =
        let work = TestHelpers.TestScratch.create "sqlite-static-elision"
        try
            Assert.True(File.Exists "/usr/include/sqlite3.h", "Required system header missing: /usr/include/sqlite3.h")

            let stubSource =
                [ "void *sqlite3_open_v2(const char *f, void **p, int fl, const char *v) { (void)f;(void)p;(void)fl;(void)v; return 0; }"
                  "int sqlite3_close_v2(void *db) { (void)db; return 0; }"
                  "int sqlite3_prepare_v2(void *db, const char *s, int n, void **st, const char **t) { (void)db;(void)s;(void)n;(void)st;(void)t; return 0; }"
                  "int sqlite3_prepare_v3(void *db, const char *s, int n, unsigned p, void **st, const char **t) { (void)db;(void)s;(void)n;(void)p;(void)st;(void)t; return 0; }"
                  "int sqlite3_step(void *st) { (void)st; return 0; }"
                  "int sqlite3_reset(void *st) { (void)st; return 0; }"
                  "int sqlite3_finalize(void *st) { (void)st; return 0; }"
                  "int sqlite3_clear_bindings(void *st) { (void)st; return 0; }"
                  "int sqlite3_bind_text(void *st, int i, const char *v, int n, void (*d)(void *)) { (void)st;(void)i;(void)v;(void)n;(void)d; return 0; }"
                  "int sqlite3_bind_blob(void *st, int i, const void *v, int n, void (*d)(void *)) { (void)st;(void)i;(void)v;(void)n;(void)d; return 0; }"
                  "const unsigned char *sqlite3_column_text(void *st, int c) { (void)st;(void)c; return 0; }"
                  "const void *sqlite3_column_blob(void *st, int c) { (void)st;(void)c; return 0; }"
                  "int sqlite3_column_bytes(void *st, int c) { (void)st;(void)c; return 0; }"
                  "const char *sqlite3_errmsg(void *db) { (void)db; return 0; }"
                  "int sqlite3_bind_parameter_index(void *st, const char *z) { (void)st;(void)z; return 0; }" ]
                |> String.concat "\n"
            let archivePath, archiveSha = writeProbeArchive work "sqlite3" stubSource

            let dynDir = Path.Combine(work, "dynamic")
            let dynPilot = Path.Combine(work, "DynamicSqlite.pilot.toml")
            writeLines dynPilot (sqlitePilotLines (Path.Combine(dynDir, output)) [])
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed dynPilot false false Types.LP64)
            |> accepted |> ignore

            let staticDir = Path.Combine(work, "static")
            let staticPilot = Path.Combine(work, "StaticSqlite.pilot.toml")
            writeLines staticPilot
                (sqlitePilotLines (Path.Combine(staticDir, output))
                    [ "[library.artifact]"
                      "kind = \"static-archive\""
                      "path = " + quote archivePath
                      "sha256 = " + quote archiveSha ])
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed staticPilot false false Types.LP64)
            |> accepted |> ignore

            let readSurface dir =
                Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun p -> p.EndsWith(".clef", StringComparison.Ordinal) || p.EndsWith(".fidproj", StringComparison.Ordinal))
                |> Seq.map (fun p -> Path.GetRelativePath(dir, p), File.ReadAllBytes p)
                |> Seq.sortBy fst
                |> Seq.toList

            let dynSurface = readSurface dynDir
            let staticSurface = readSurface staticDir
            let manifest, manifestBytes =
                dynSurface
                |> List.filter (fun (path, _) -> Path.GetExtension path = ".fidproj")
                |> Assert.Single
            Assert.Equal(expectedManifest, manifest)
            Assert.Contains($"    \"{expectedTypes}\"", Encoding.UTF8.GetString manifestBytes)
            Assert.True(dynSurface.Length >= 3)
            Assert.Equal<string list>(dynSurface |> List.map fst, staticSurface |> List.map fst)
            for (relPath, dynBytes), (_, staticBytes) in List.zip dynSurface staticSurface do
                Assert.True((dynBytes = staticBytes), $"Static Binding Elision Test failed: '{relPath}' bytes differed between dynamic and static generation.")
                Assert.Equal<byte[]>(dynBytes, staticBytes)

            Assert.Empty(Directory.GetFiles(dynDir, "*.link-provenance.toml", SearchOption.AllDirectories))
            let provenancePath = Assert.Single(Directory.GetFiles(staticDir, "*.link-provenance.toml", SearchOption.AllDirectories))
            let provenanceText = File.ReadAllText provenancePath
            Assert.Contains("kind = \"static-archive\"", provenanceText)
            Assert.Contains(archiveSha.ToLowerInvariant(), provenanceText)
            Assert.Contains("symbol = \"sqlite3_open_v2\"", provenanceText)
            Assert.Contains("symbol = \"sqlite3_bind_text\"", provenanceText)
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``Substantiation inspectSource admits StaticArchiveContract and ProcessLinkSpec loading contracts and refuses non-literal bodies with FS9233`` () =
        let work = TestHelpers.TestScratch.create "static-loading-contract-admission"
        try
            let sha64 = String.replicate 64 "a"
            let archivePath = Path.Combine(work, "ArchiveContract.clef")
            writeLines archivePath
                [ "module Fidelity.Platform.ArchiveContractTest"
                  "let archiveContract : BAREWire.Platform.StaticArchiveContract = {"
                  "    ArchivePath = \"libsqlite3.a\""
                  "    ArchiveSource = \"sqlite-3.53.4\""
                  "    Sha256 = \"" + sha64 + "\""
                  "    Format = \"llvm-bitcode\""
                  "    Visibility = \"default\""
                  "}" ]
            let archiveDigest =
                Substantiation.inspectSource (set [ "Fidelity.Platform.ArchiveContractTest.archiveContract" ]) archivePath
                |> accepted
            Assert.Contains("Fidelity.Platform.ArchiveContractTest.archiveContract", archiveDigest.CompileTimeDeclarations)

            let specPath = Path.Combine(work, "LoadingSpec.clef")
            writeLines specPath
                [ "module Fidelity.Platform.LoadingSpecTest"
                  "let linkSpec : BAREWire.Platform.ProcessLinkSpec = {"
                  "    Contract = {"
                  "        Identity = \"sqlite-static\""
                  "        Version = \"1\""
                  "        Source = \"test\""
                  "        Library = \"sqlite3\""
                  "        Runtime = \"libc\""
                  "        Os = \"linux\""
                  "        Arch = \"x86_64\""
                  "        LoadAt = \"StaticLink\""
                  "        LiveUntil = \"ProcessTermination\""
                  "        Unloading = \"Forbidden\""
                  "        ExportedLibrary = \"Forbidden\""
                  "    }"
                  "    LinkKind = \"static-archive\""
                  "    DynamicSharedObject = \"\""
                  "    StaticArchive = Some {"
                  "        ArchivePath = \"libsqlite3.a\""
                  "        ArchiveSource = \"sqlite-3.53.4\""
                  "        Sha256 = \"" + sha64 + "\""
                  "        Format = \"llvm-bitcode\""
                  "        Visibility = \"default\""
                  "    }"
                  "}" ]
            let specDigest =
                Substantiation.inspectSource (set [ "Fidelity.Platform.LoadingSpecTest.linkSpec" ]) specPath
                |> accepted
            Assert.Contains("Fidelity.Platform.LoadingSpecTest.linkSpec", specDigest.CompileTimeDeclarations)

            let dynSpecPath = Path.Combine(work, "DynamicLoadingSpec.clef")
            writeLines dynSpecPath
                [ "module Fidelity.Platform.DynamicLoadingSpecTest"
                  "let dynSpec : BAREWire.Platform.ProcessLinkSpec = {"
                  "    Contract = { Identity = \"sqlite-dyn\"; Version = \"1\"; Source = \"test\"; Library = \"sqlite3\"; Runtime = \"libc\"; Os = \"linux\"; Arch = \"x86_64\"; LoadAt = \"ProcessStartup\"; LiveUntil = \"ProcessTermination\"; Unloading = \"Forbidden\"; ExportedLibrary = \"Forbidden\" }"
                  "    LinkKind = \"dynamic-shared-object\""
                  "    DynamicSharedObject = \"libsqlite3.so.0\""
                  "    StaticArchive = None"
                  "}" ]
            let dynDigest =
                Substantiation.inspectSource (set [ "Fidelity.Platform.DynamicLoadingSpecTest.dynSpec" ]) dynSpecPath
                |> accepted
            Assert.Contains("Fidelity.Platform.DynamicLoadingSpecTest.dynSpec", dynDigest.CompileTimeDeclarations)

            let negativeBodies =
                [ "top-level-call", "ProcessLinkSpec.staticArchive baseContract archiveContract"
                  "nested-ident", "{\n    Contract = { Identity = \"x\" }\n    LinkKind = \"static-archive\"\n    DynamicSharedObject = \"\"\n    StaticArchive = Some archiveContract\n}"
                  "nested-helper-call", "{\n    Contract = { Identity = \"x\" }\n    LinkKind = \"static-archive\"\n    DynamicSharedObject = \"\"\n    StaticArchive = Some (StaticArchiveContract.create \"lib.a\" \"src\" \"" + sha64 + "\" \"llvm-bitcode\" \"default\")\n}"
                  "nested-contract-call", "{\n    Contract = mkContract ()\n    LinkKind = \"static-archive\"\n    DynamicSharedObject = \"\"\n    StaticArchive = None\n}"
                  "trailing-expr", "{ Identity = \"x\" } |> ignore" ]
            for name, body in negativeBodies do
                let badPath = Path.Combine(work, $"Bad_{name}.clef")
                writeLines badPath
                    [ "module Fidelity.Platform.BadLoadingTest"
                      $"let badSpec : BAREWire.Platform.ProcessLinkSpec = {body}" ]
                Substantiation.inspectSource (set [ "Fidelity.Platform.BadLoadingTest.badSpec" ]) badPath
                |> refused "FS9233"
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``inspectElfExecutable extracts DT_SONAME and verifyElfExpectations matches versioned DT_SONAME`` () =
        let work = TestHelpers.TestScratch.create "elf-dt-soname-probe"
        try
            let libC = Path.Combine(work, "soname_probe.c")
            let sharedPath = Path.Combine(work, "libsoname_probe.so")
            let dynProbeC = Path.Combine(work, "dyn_probe.c")
            let staticProbeC = Path.Combine(work, "static_probe.c")
            let staticBin = Path.Combine(work, "static_probe")
            writeUtf8 libC "int soname_probe_add(int a, int b) { return a + b; }\nint soname_probe_dead(void) { return 99; }\n"
            writeUtf8 dynProbeC "int soname_probe_add(int a, int b);\nint main(void) { return soname_probe_add(20, 22) == 42 ? 0 : 1; }\n"
            writeUtf8 staticProbeC "int main(void) { return 0; }\n"

            run work "compile-soname-so" (ClangToolchain.startInfo [ "-O2"; "-shared"; "-fPIC"; "-Wl,-soname,libsoname_probe.so.0"; libC; "-o"; sharedPath ]) |> ignore
            run work "compile-static-bin" (ClangToolchain.startInfo [ "-O2"; staticProbeC; "-o"; staticBin ]) |> ignore

            let sharedElf = SubstantiationSeams.inspectElfExecutable sharedPath |> accepted
            Assert.Equal(Some "libsoname_probe.so.0", sharedElf.Soname)

            let staticElf = SubstantiationSeams.inspectElfExecutable staticBin |> accepted
            Assert.Equal(None, staticElf.Soname)

            let stubFile name : Substantiation.FileDigest =
                let p = Path.Combine(work, name)
                writeUtf8 p name
                { Substantiation.Path = p; Sha256 = ClangToolchain.sha256 p }
            let pilotStub : SubstantiationBoundary.FileDigest =
                let p = Path.Combine(work, "pilot.toml")
                writeUtf8 p "pilot"
                { SubstantiationBoundary.Path = p; Sha256 = ClangToolchain.sha256 p; Kind = "pilot" }
            let artifactsDir = Path.Combine(work, "artifacts")
            Directory.CreateDirectory artifactsDir |> ignore
            let manifest =
                TargetLibraryCensusFixtures.buildManifest
                    "SonameProbe"
                    artifactsDir
                    (stubFile "SonameProbe.fidproj")
                    (stubFile "Composer.dll")
                    (stubFile "dotnet")
                    ({ Substantiation.Path = staticBin; Sha256 = ClangToolchain.sha256 staticBin } : Substantiation.FileDigest)
                    (stubFile "Harness.fidproj")
                    (stubFile "compiler.log")
                    (stubFile "01_psg0.json")
                    (stubFile "10_witness_units.json")
                    (TargetLibraryCensusFixtures.buildBoundary pilotStub (clangTargetTriple work))
                    (TargetLibraryCensusFixtures.buildProof 1 1 "0" "0" "0" "0" "0")

            let checkWith (id: string) (soPath: string) (soSha: string) (probeSrc: string) (imports: string[]) : SubstantiationSeams.PhysicalCheckSpec =
                { Id = id
                  Category = "static-dynamic-link-elision"
                  Summary = "Verify DT_SONAME matching in shared library comparison probe"
                  EntrySource = Path.Combine(work, "Entry.clef")
                  ExtraSources = [||]
                  IncludeRuntimeSources = false
                  RequiredSymbols = [||]
                  LoadingContract = None
                  ExpectedExitCode = 0
                  ExpectedStdout = ""
                  Elf =
                    Some
                        { ElidedSymbols = [||]
                          RetainedStaticSymbols = [||]
                          ForbiddenNeededLibraries = [| "libsoname_probe.so.0" |]
                          SharedLibrary = Some soPath
                          SharedLibrarySha256 = Some soSha
                          DynamicProbeSource = Some probeSrc
                          RetainedDynamicImports = imports } }

            let probeWork = Path.Combine(work, "probe-work")
            Directory.CreateDirectory probeWork |> ignore
            let comparison =
                match SubstantiationSeams.verifyElfExpectationsDiagnosed manifest (checkWith "soname-comparison" sharedPath sharedElf.Sha256 dynProbeC [| "soname_probe_add" |]) staticElf probeWork |> accepted with
                | Some cmp -> cmp
                | None -> failwith "Expected SharedLibraryComparison when SharedLibrary is configured"
            let dynBinElf = SubstantiationSeams.inspectElfExecutable comparison.DynamicProbeBinaryPath |> accepted
            Assert.Contains("libsoname_probe.so.0", dynBinElf.NeededLibraries)

            let refusedWithMessage (expectedCode: string) (expectedFragment: string) = function
                | Error (refusal: Diagnostics.Refusal) ->
                    Assert.Equal<string list>([ expectedCode ], refusal.Diagnostics |> List.map (fun d -> d.Code) |> List.distinct)
                    Assert.Contains(expectedFragment, refusal.Text, StringComparison.Ordinal)
                | Ok _ -> failwithf "expected %s refusal containing '%s', got success" expectedCode expectedFragment

            // Negative 1: empty DT_SONAME (d_val = 0 pointing to .dynstr[0] = "") refuses with FS9253.
            let emptyBytes = File.ReadAllBytes sharedPath
            let shoff = int (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(emptyBytes, 40, 8)))
            let shentsize = int (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(emptyBytes, 58, 2)))
            let shnum = int (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(emptyBytes, 60, 2)))
            let dynSecOff, dynSecSize =
                [ 0 .. shnum - 1 ]
                |> List.pick (fun idx ->
                    let off = shoff + idx * shentsize
                    if System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(emptyBytes, off + 4, 4)) = 6u then
                        Some (int (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(emptyBytes, off + 24, 8))),
                              int (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(emptyBytes, off + 32, 8))))
                    else None)
            let mutable clearedSoname = false
            for entryIdx in 0 .. (dynSecSize / 16) - 1 do
                let entryOff = dynSecOff + entryIdx * 16
                if not clearedSoname && System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan<byte>(emptyBytes, entryOff, 8)) = 14L then
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(emptyBytes, entryOff + 8, 8), 0UL)
                    clearedSoname <- true
            Assert.True(clearedSoname, "Expected DT_SONAME entry in shared object to zero its d_val")
            let emptySonameSo = Path.Combine(work, "libempty_soname.so")
            File.WriteAllBytes(emptySonameSo, emptyBytes)
            SubstantiationSeams.inspectElfExecutable emptySonameSo
            |> refusedWithMessage "FS9253" "ELF64 DT_SONAME entry names an empty library."

            // Negative 2: non-bare DT_SONAME containing '/', '.', or '..' refuses with FS9253 bare-filename guard in verifyElfExpectations.
            for label, badSoname in [ "slash", "sub/libbad.so.0"; "dot", "."; "dotdot", ".." ] do
                let badSonameSo = Path.Combine(work, $"lib{label}_soname.so")
                run work ("compile-" + label + "-soname") (ClangToolchain.startInfo [ "-O2"; "-shared"; "-fPIC"; $"-Wl,-soname,{badSoname}"; libC; "-o"; badSonameSo ]) |> ignore
                let badElf = SubstantiationSeams.inspectElfExecutable badSonameSo |> accepted
                Assert.Equal(Some badSoname, badElf.Soname)
                let badWork = Path.Combine(work, label + "-work")
                Directory.CreateDirectory badWork |> ignore
                SubstantiationSeams.verifyElfExpectationsDiagnosed manifest (checkWith (label + "-soname") badSonameSo badElf.Sha256 dynProbeC [| "soname_probe_add" |]) staticElf badWork
                |> refusedWithMessage "FS9253" "must be a bare filename."

            // Negative 3: conflicting DT_SONAME entries refuse with FS9253.
            let conflictBytes = File.ReadAllBytes sharedPath
            let mutable patchedNeeded = false
            for entryIdx in 0 .. (dynSecSize / 16) - 1 do
                let entryOff = dynSecOff + entryIdx * 16
                if not patchedNeeded && System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan<byte>(conflictBytes, entryOff, 8)) = 1L then
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(Span<byte>(conflictBytes, entryOff, 8), 14L)
                    patchedNeeded <- true
            Assert.True(patchedNeeded, "Expected DT_NEEDED entry in shared object to patch into conflicting DT_SONAME")
            let conflictPath = Path.Combine(work, "libconflict_soname.so")
            File.WriteAllBytes(conflictPath, conflictBytes)
            SubstantiationSeams.inspectElfExecutable conflictPath
            |> refusedWithMessage "FS9253" "conflicting DT_SONAME entries"
        finally
            Directory.Delete(work, true)

    let private duckdbPilotLines (outDir: string) =
        [ "[library]"
          "name = \"duckdb\""
          "headers = [\"/usr/include/duckdb.h\"]"
          "include_paths = [\"/usr/include\"]"
          "macro_prefixes = [\"DUCKDB_\"]"
          "[output]"
          "mode = \"fidelity\""
          "directory = " + quote outDir
          "[error_conventions]"
          "default = \"enum_error_code\""
          "error_type = \"duckdb_state\""
          "success_value = \"DuckDBSuccess\""
          "[error_conventions.overrides]"
          "duckdb_library_version = \"none\""
          "duckdb_close = \"none\""
          "duckdb_disconnect = \"none\""
          "duckdb_destroy_config = \"none\""
          "duckdb_destroy_result = \"none\""
          "duckdb_result_error = \"none\""
          "duckdb_column_name = \"none\""
          "duckdb_column_type = \"none\""
          "duckdb_column_count = \"none\""
          "duckdb_row_count = \"none\""
          "duckdb_result_chunk_count = \"none\""
          "duckdb_result_get_chunk = \"none\""
          "duckdb_fetch_chunk = \"none\""
          "duckdb_destroy_data_chunk = \"none\""
          "duckdb_data_chunk_get_size = \"none\""
          "duckdb_data_chunk_get_column_count = \"none\""
          "duckdb_destroy_prepare = \"none\""
          "duckdb_malloc = \"none\""
          "duckdb_free = \"none\""
          "[annotations.nonnull]"
          "duckdb_free = [0]"
          "[options]"
          "c_header_mode = true"
          "descriptor_only_dependencies = true"
          "generate_descriptors = true"
          "link_libraries = true"
          "abi_critical_structs = [\"typedef duckdb_string_t\", \"struct duckdb_result\"]"
          "value_structs = [\"duckdb_result\"]"
          "[[options.bindings]]"
          "name = \"duckdb_open\""
          "reference_parameters = [1]"
          "read_only_buffers = [{ parameter = 0, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.open.path\", version = \"1.5.6\", source = \"duckdb.h\" } }]"
          "buffer_absences = [{ parameter = 0, hypothesis = { identity = \"duckdb.open.path.inmemory\", version = \"1.5.6\", source = \"duckdb.h\" } }]"
          "[[options.bindings]]"
          "name = \"duckdb_close\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_connect\""
          "reference_parameters = [1]"
          "[[options.bindings]]"
          "name = \"duckdb_disconnect\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_create_config\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_destroy_config\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_set_config\""
          "read_only_buffers = ["
          "  { parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.set_config.name\", version = \"1.5.6\", source = \"duckdb.h\" } },"
          "  { parameter = 2, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.set_config.option\", version = \"1.5.6\", source = \"duckdb.h\" } }"
          "]"
          "[[options.bindings]]"
          "name = \"duckdb_query\""
          "reference_parameters = [2]"
          "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.query.sql\", version = \"1.5.6\", source = \"duckdb.h\" } }]"
          "[[options.bindings]]"
          "name = \"duckdb_destroy_result\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_result_error\""
          "read_only_reference_parameters = [0]"
          "borrowed_result = { content = \"text\", owner_parameter = 0, extent = { nul_terminated = true }, invalidated_by = [{ binding = \"Fidelity.DuckDb.Database.duckdb_destroy_result\", parameter = 0 }], hypothesis = { identity = \"duckdb.result_error\", version = \"1.5.6\", source = \"duckdb.h\" } }"
          "[[options.bindings]]"
          "name = \"duckdb_column_name\""
          "read_only_reference_parameters = [0]"
          "borrowed_result = { content = \"text\", owner_parameter = 0, extent = { nul_terminated = true }, invalidated_by = [{ binding = \"Fidelity.DuckDb.Database.duckdb_destroy_result\", parameter = 0 }], hypothesis = { identity = \"duckdb.column_name\", version = \"1.5.6\", source = \"duckdb.h\" } }"
          "[[options.bindings]]"
          "name = \"duckdb_column_type\""
          "read_only_reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_column_count\""
          "read_only_reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_row_count\""
          "read_only_reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_prepare\""
          "reference_parameters = [2]"
          "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.prepare.sql\", version = \"1.5.6\", source = \"duckdb.h\" } }]"
          "[[options.bindings]]"
          "name = \"duckdb_destroy_prepare\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_execute_prepared\""
          "reference_parameters = [1]"
          "[[options.bindings]]"
          "name = \"duckdb_bind_varchar\""
          "read_only_buffers = [{ parameter = 2, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.bind_varchar.val\", version = \"1.5.6\", source = \"duckdb.h\" } }]"
          "[[options.bindings]]"
          "name = \"duckdb_bind_varchar_length\""
          "read_only_buffers = [{ parameter = 2, content = \"text\", extent = { count_parameter = 3 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"duckdb.bind_varchar_length.val\", version = \"1.5.6\", source = \"duckdb.h\" } }]"
          "[[options.bindings]]"
          "name = \"duckdb_destroy_data_chunk\""
          "reference_parameters = [0]"
          "[[options.bindings]]"
          "name = \"duckdb_malloc\""
          "ownership = \"caller_owns\""
          "resource = { kind = \"owned_nullable_return\", release_binding = \"Fidelity.DuckDb.Database.duckdb_free\", release_parameter = 0, hypothesis = { identity = \"duckdb.malloc.resource\", version = \"1.5.6\", source = \"duckdb.h\" } }"
          "[[namespace]]"
          "name = \"Fidelity.DuckDb.Database\""
          "library = \"duckdb\""
          "description = \"DuckDB database, query, prepared statement, and chunk API\""
          "functions = [\"duckdb_library_version\", \"duckdb_open\", \"duckdb_close\", \"duckdb_connect\", \"duckdb_disconnect\", \"duckdb_create_config\", \"duckdb_destroy_config\", \"duckdb_set_config\", \"duckdb_query\", \"duckdb_destroy_result\", \"duckdb_result_error\", \"duckdb_column_name\", \"duckdb_column_type\", \"duckdb_column_count\", \"duckdb_row_count\", \"duckdb_result_chunk_count\", \"duckdb_result_get_chunk\", \"duckdb_fetch_chunk\", \"duckdb_destroy_data_chunk\", \"duckdb_data_chunk_get_size\", \"duckdb_data_chunk_get_column_count\", \"duckdb_prepare\", \"duckdb_destroy_prepare\", \"duckdb_execute_prepared\", \"duckdb_bind_int64\", \"duckdb_bind_varchar\", \"duckdb_bind_varchar_length\", \"duckdb_malloc\", \"duckdb_free\"]" ]

    [<Fact>]
    let ``DuckDB #198 pilot generates Schema 53 zero-pointer surface and classifies FS91xx/FS92xx refusal gates`` () =
        let duckdbHeader = "/usr/include/duckdb.h"
        if not (File.Exists duckdbHeader) then
            ()
        else
            let work = TestHelpers.TestScratch.create "m2-duckdb-pilot"
            try
                let refusedWith (expectedCode: string) (expectedFragment: string) = function
                    | Error (refusal: Diagnostics.Refusal) ->
                        Assert.Equal<string list>([ expectedCode ], refusal.Diagnostics |> List.map (fun d -> d.Code) |> List.distinct)
                        Assert.Contains(expectedFragment, refusal.Text, StringComparison.Ordinal)
                    | Ok _ -> failwithf "expected %s refusal containing '%s', got success" expectedCode expectedFragment

                let outDir = Path.Combine(work, "generated")
                let pilotPath = Path.Combine(work, "Duckdb.pilot.toml")
                writeLines pilotPath (duckdbPilotLines outDir)

                ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilotPath false false Types.LP64)
                |> accepted |> ignore

                let generatedFiles = readGeneratedTexts outDir
                Assert.True(generatedFiles.Length >= 3)
                assertNoRawPointers generatedFiles
                BindingGenerator.verifyEmittedClefPurity generatedFiles |> accepted |> ignore

                let typesCode = File.ReadAllText(Path.Combine(outDir, "Types.clef"))
                let databaseCode = File.ReadAllText(Path.Combine(outDir, "Database", "Database.clef"))
                let fidprojPath = Path.Combine(outDir, "Fidelity.DuckDb.Database.fidproj")
                Assert.True(File.Exists fidprojPath)
                let fidprojText = File.ReadAllText(fidprojPath).Replace("\r\n", "\n")
                Assert.Contains("[link]\nlibraries = [\"duckdb\"]", fidprojText)

                // Every referenced type and nested descriptor in Types.clef is declared (#198 audit).
                for expectedTypeDecl in
                    [ "type _duckdb_database = private | Opaque__duckdb_database"
                      "type _duckdb_connection = private | Opaque__duckdb_connection"
                      "type _duckdb_data_chunk = private | Opaque__duckdb_data_chunk"
                      "type duckdb_column = private | Opaque_duckdb_column"
                      "type duckdb_result = {"
                      "module duckdb_result ="
                      "module duckdb_string_t ="
                      "module duckdb_string_t_value ="
                      "module duckdb_string_t_value_pointer ="
                      "module duckdb_string_t_value_inlined =" ] do
                    Assert.Contains(expectedTypeDecl, typesCode)
                Assert.DoesNotContain("type duckdb_string_t = {", typesCode)
                Assert.DoesNotContain("CHandle<duckdb_result>", databaseCode)

                let combined = generatedFiles |> List.map snd |> String.concat "\n"
                for expectedFragment in
                    [ "module DuckdbState ="
                      "| 0 -> \"DuckDBSuccess\""
                      "Expr<ReadOnlyBufferDescriptor>"
                      "Expr<BufferAbsenceContract>"
                      "Expr<BorrowedResultViewDescriptor>"
                      "Expr<ResourceDescriptor>"
                      "let duckdb_open (path: option<string>)"
                      "let duckdb_query (connection: option<CHandle<_duckdb_connection>>) (query: string) (out_result: duckdb_result array) : int ="
                      "{ Name = \"out_result\"; Type = Named \"duckdb_result\"; PassBy = Reference }"
                      "let duckdb_execute_prepared (prepared_statement: option<CHandle<_duckdb_prepared_statement>>) (out_result: duckdb_result array) : int ="
                      "let duckdb_destroy_result (result: duckdb_result array) : unit ="
                      "let duckdb_result_error (result: duckdb_result array) : option<ReadOnlyView<int>> ="
                      "{ Name = \"result\"; Type = Named \"duckdb_result\"; PassBy = ReadOnlyReference }"
                      "let duckdb_column_name (result: duckdb_result array) (col: int) : option<ReadOnlyView<int>> ="
                      "let duckdb_column_type (result: duckdb_result array) (col: int) : int ="
                      "let duckdb_column_count (result: duckdb_result array) : int ="
                      "let duckdb_row_count (result: duckdb_result array) : int ="
                      "let duckdb_free (ptr: CHandle<unit>) : unit ="
                      "let duckdb_fetch_chunk (result: duckdb_result) : option<CHandle<_duckdb_data_chunk>> =" ] do
                    Assert.Contains(expectedFragment, combined)

                // Gate 1 (FS9119, duckdb.h:831-838,6671,6496): by-value struct & reference parameters refuse unless listed in options.value_structs.
                let missingValueStructsPilot = Path.Combine(work, "DuckdbMissingValueStructs.pilot.toml")
                File.ReadAllText(pilotPath).Replace("value_structs = [\"duckdb_result\"]", "")
                |> writeUtf8 missingValueStructsPilot
                ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed missingValueStructsPilot false false Types.LP64)
                |> refusedWith "FS9119" "value_structs"

                // Gate 2 (FS9146, duckdb.h:1951): unannotated callback-taking DuckDB APIs require explicit nonnull/absence evidence.
                let unannotatedCallbackOut = Path.Combine(work, "gen-cb-unannotated")
                let unannotatedCallbackPilot = Path.Combine(work, "DuckdbUnannotatedCallback.pilot.toml")
                writeLines unannotatedCallbackPilot
                    [ "[library]"
                      "name = \"duckdb\""
                      "headers = [\"/usr/include/duckdb.h\"]"
                      "include_paths = [\"/usr/include\"]"
                      "[output]"
                      "mode = \"fidelity\""
                      "directory = " + quote unannotatedCallbackOut
                      "[options]"
                      "c_header_mode = true"
                      "[[namespace]]"
                      "name = \"Fidelity.DuckDb.Callbacks\""
                      "library = \"duckdb\""
                      "description = \"DuckDB unannotated callback gate\""
                      "functions = [\"duckdb_aggregate_function_set_functions\"]" ]
                ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed unannotatedCallbackPilot false false Types.LP64)
                |> refusedWith "FS9146" "native function-pointer parameter requires explicit nonnull evidence"

                // Gate 3 (FS9144, duckdb.h:7290,7210): scalar callback entry taking duckdb_function_info / void* refuses outside admitted profile.
                for fnName, paramOrd, expectedTypeFragment in
                    [ "duckdb_scalar_function_set_function", 1, "duckdb_function_info"
                      "duckdb_scalar_function_set_extra_info", 2, "void *" ] do
                    let cbOut = Path.Combine(work, "gen-" + fnName)
                    let cbPilot = Path.Combine(work, fnName + ".pilot.toml")
                    writeLines cbPilot
                        [ "[library]"
                          "name = \"duckdb\""
                          "headers = [\"/usr/include/duckdb.h\"]"
                          "include_paths = [\"/usr/include\"]"
                          "[output]"
                          "mode = \"fidelity\""
                          "directory = " + quote cbOut
                          "[options]"
                          "c_header_mode = true"
                          "[[options.bindings]]"
                          "name = " + quote fnName
                          $"nonnull_callbacks = [{paramOrd}]"
                          $"callback_invocations = [{{ parameter = {paramOrd}, identity = \"duckdb.{fnName}\", version = \"1.5.6\", source = \"duckdb.h\", scope = \"during_call\" }}]"
                          "[[namespace]]"
                          "name = \"Fidelity.DuckDb.Callbacks\""
                          "library = \"duckdb\""
                          "description = \"DuckDB callback entry gate\""
                          "functions = [" + quote fnName + "]" ]
                    ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed cbPilot false false Types.LP64)
                    |> refusedWith "FS9144" expectedTypeFragment

                // Gate 4 (FS9154, duckdb.h:4393): pointer-to-handle release duckdb_destroy_data_chunk(duckdb_data_chunk*) does not match unary CHandle Value release.
                let ptrReleasePilot = Path.Combine(work, "DuckdbPtrToHandleRelease.pilot.toml")
                File.ReadAllText(pilotPath).Replace(
                    "[[namespace]]",
                    "[[options.bindings]]\nname = \"duckdb_fetch_chunk\"\nownership = \"caller_owns\"\nresource = { kind = \"owned_nullable_return\", release_binding = \"Fidelity.DuckDb.Database.duckdb_destroy_data_chunk\", release_parameter = 0, hypothesis = { identity = \"duckdb.fetch_chunk.resource\", version = \"1.5.6\", source = \"duckdb.h\" } }\n\n[[namespace]]")
                |> writeUtf8 ptrReleasePilot
                ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed ptrReleasePilot false false Types.LP64)
                |> refusedWith "FS9154" "unary matching CHandle Value parameter 0"

                // Gate 5 (FS9231): emitted Clef purity guard rejects raw nativeptr leakage.
                let (_, firstClef) = generatedFiles.Head
                BindingGenerator.verifyEmittedClefPurity [ "ImpureDuckdb.clef", firstClef + "\nlet leaked (p: nativeptr<byte>) : unit = ()\n" ]
                |> refusedWith "FS9231" "nativeptr"

                // Gate 6 (FS9183, duckdb.h:126-138,6796): duckdb_string_t contains anonymous union duckdb_string_t_value and is refused as a Clef value_structs record.
                let stringValueStructOut = Path.Combine(work, "gen-string-value-struct")
                let stringValueStructPilot = Path.Combine(work, "DuckdbStringValueStruct.pilot.toml")
                writeLines stringValueStructPilot
                    [ "[library]"
                      "name = \"duckdb\""
                      "headers = [\"/usr/include/duckdb.h\"]"
                      "include_paths = [\"/usr/include\"]"
                      "[output]"
                      "mode = \"fidelity\""
                      "directory = " + quote stringValueStructOut
                      "[options]"
                      "c_header_mode = true"
                      "abi_critical_structs = [\"typedef duckdb_string_t\"]"
                      "value_structs = [\"duckdb_string_t\"]"
                      "[[namespace]]"
                      "name = \"Fidelity.DuckDb.Strings\""
                      "library = \"duckdb\""
                      "description = \"DuckDB string_t value_structs union gate\""
                      "functions = [\"duckdb_string_is_inlined\"]" ]
                ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed stringValueStructPilot false false Types.LP64)
                |> refusedWith "FS9183" "duckdb_string_t_value"
            finally
                Directory.Delete(work, true)

    [<Fact>]
    let ``structural boundary and shape formatters emit deterministic non-reflection text without %A`` () =
        let work = TestHelpers.TestScratch.create "structural-formatters"
        try
            let headerA = Path.Combine(work, "probe_a.h")
            let headerB = Path.Combine(work, "probe_b.h")
            let headerC = Path.Combine(work, "probe_c.h")
            File.WriteAllText(headerA,
                "typedef unsigned int u32_aligned __attribute__((aligned(8)));\nstruct ProbeRec { int : 3; int flags : 5; u32_aligned single_val; unsigned int items[4]; };\nint probe_call(int fd, const char *buf, int len);\n")
            File.WriteAllText(headerB,
                "typedef unsigned int u32_aligned __attribute__((aligned(8)));\nstruct ProbeRec { int : 3; int flags : 5; u32_aligned single_val; unsigned int items[4]; };\nint probe_call(int fd, const char *buf, int len);\n")
            File.WriteAllText(headerC,
                "typedef unsigned int u32_aligned __attribute__((aligned(16)));\nstruct ProbeRec { int : 3; int flags : 6; u32_aligned single_val; unsigned int items[5]; };\nint probe_call(int fd, char *buf, int len);\n")
            let parseH path =
                match CppParser.parseHeader { HeaderFile = path; IncludePaths = [ work ]; SystemIncludePaths = []; Defines = []; Verbose = false; IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false } with
                | Ok decls -> decls
                | Error err -> failwith err
            let declsA = parseH headerA
            let declsB = parseH headerB
            let declsC = parseH headerC
            let unitMap = BindingGenerator.unitTypedefMaps (Map.ofList [ headerA, declsA; headerB, declsB ]) declsA [ headerA, declsA; headerB, declsB ] []
            Assert.True(unitMap.ContainsKey(("struct", "ProbeRec")), "unitTypedefMaps should match ProbeRec declared shape structurally")
            Assert.True(unitMap.ContainsKey(("function", "probe_call")), "unitTypedefMaps should match probe_call signature structurally")
            // Non-identical shapes (bitfield width, array length, alignment, parameter constness) must not collapse.
            Assert.True(Result.isError (BindingGenerator.agreeRecords [ headerA, declsA; headerC, declsC ]), "agreeRecords must refuse structurally different ProbeRec in headerC")
            let unitMapSelectC = BindingGenerator.unitTypedefMaps (Map.ofList [ headerA, declsA; headerC, declsC ]) declsC [ headerA, declsA; headerC, declsC ] []
            Assert.True((unitMapSelectC[("struct", "ProbeRec")].TypedefInfos["u32_aligned"].Attributes |> List.exists (fun a -> a.Args = [16])), "unitTypedefMaps must resolve ProbeRec to headerC rather than collapsing with headerA")

            let source = Path.Combine(work, "Probe.clef")
            let sourceText =
                "module FormatterProbe\n"
                + "[<FidelityExtern(\"probe_lib\", \"probe_call\")>]\n"
                + "let probe_call (fd: int) (buf: string) (len: int) : int = NativeDefault.zeroed ()\n"
            writeUtf8 source sourceText
            let fidproj = Path.Combine(work, "FormatterProbe.fidproj")
            writeLines fidproj
                [ "[package]"
                  "name = \"FormatterProbe\""
                  "version = \"0.1.0\""
                  "[build]"
                  "sources = [\"Probe.clef\"]"
                  "[platform]"
                  "os = \"linux\""
                  "arch = \"x86_64\""
                  "[link]"
                  "libraries = [\"probe_lib\"]" ]
            let pilotPath = Path.Combine(work, "FormatterProbe.pilot.toml")
            writeLines pilotPath
                [ "[library]"
                  "name = \"probe_lib\""
                  "headers = [" + quote headerA + "]"
                  "include_paths = [" + quote work + "]"
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"generated\""
                  "[error_conventions]"
                  "default = \"enum_error_code\""
                  "error_type = \"int\""
                  "success_value = \"0\""
                  "[error_conventions.overrides]"
                  "probe_call = \"return_code\""
                  "[annotations.nonnull]"
                  "probe_call = [1]"
                  "[options]"
                  "c_header_mode = true"
                  "generate_descriptors = false"
                  "value_structs = [\"ProbeRec\"]"
                  "abi_critical_structs = [\"ProbeRec\"]"
                  "[[options.bindings]]"
                  "name = \"probe_call\""
                  "ownership = \"borrowed\""
                  "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"probe.buf\", version = \"1\", source = \"probe_a.h\" } }]"
                  "[[namespace]]"
                  "name = \"FormatterProbe\""
                  "library = \"probe_lib\""
                  "description = \"Formatter test\""
                  "functions = [\"probe_call\"]" ]
            let evidence =
                SubstantiationBoundary.evaluate
                    { FidprojPath = fidproj
                      PackageName = "FormatterProbe"
                      LinkLibraries = [| "probe_lib" |]
                      LinkObjects = [||]
                      PilotPath = Some pilotPath
                      LinkProvenancePath = None
                      Sources = [| source, sourceText |]
                      WorkDirectory = work }
                |> accepted
            let contractHyp = evidence.RestsOn |> Array.find (fun h -> h.Family = "H-Contract")
            Assert.Contains("error default=EnumErrorCode(int, 0, None, None); selected overrides={probe_call=ReturnCode}", contractHyp.Statement, StringComparison.Ordinal)
            Assert.Contains("probe_call ownership=Borrowed", contractHyp.Statement, StringComparison.Ordinal)
            Assert.Contains("extent=ExplicitCount(2); content=Text; retention=DuringCall", contractHyp.Statement, StringComparison.Ordinal)
            Assert.Contains("nonnull parameters={probe_call:[1]}", contractHyp.Statement, StringComparison.Ordinal)
            Assert.DoesNotContain("Some ", contractHyp.Statement, StringComparison.Ordinal)
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``nested value_structs child records emit before enclosing parent records in Types.clef`` () =
        let work = TestHelpers.TestScratch.create "nested-value-struct-order"
        try
            let header = Path.Combine(work, "nested.h")
            File.WriteAllText(header,
                "struct Outer { struct { int x; } inner; };\nstruct Outer take_outer(struct Outer value);\n")
            let outDir = Path.Combine(work, "generated")
            let pilotPath = Path.Combine(work, "Nested.pilot.toml")
            writeLines pilotPath
                [ "[library]"
                  "name = \"nested\""
                  "headers = [" + quote header + "]"
                  "include_paths = [" + quote work + "]"
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = " + quote outDir
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "abi_critical_structs = [\"struct Outer\"]"
                  "value_structs = [\"Outer\", \"Outer_inner\"]"
                  "[[namespace]]"
                  "name = \"Fidelity.Nested.Test\""
                  "library = \"nested\""
                  "description = \"Nested value struct ordering\""
                  "functions = [\"take_outer\"]" ]
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilotPath false false Types.LP64)
            |> accepted |> ignore
            let typesCode = File.ReadAllText(Path.Combine(outDir, "Types.clef"))
            let childRecordIdx = typesCode.IndexOf("type Outer_inner = {", StringComparison.Ordinal)
            let childModuleIdx = typesCode.IndexOf("module Outer_inner =", StringComparison.Ordinal)
            let parentRecordIdx = typesCode.IndexOf("type Outer = {", StringComparison.Ordinal)
            Assert.True(childRecordIdx >= 0, "Types.clef must emit child record type Outer_inner")
            Assert.True(childModuleIdx > childRecordIdx, "Types.clef must emit module Outer_inner")
            Assert.True(parentRecordIdx > childModuleIdx, "Types.clef must emit Outer_inner record and Descriptor before type Outer =")
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``SQLite M2 static archive StaticArchiveContract, ProcessLinkSpec, and Static Binding Elision ELF verification`` () =
        let sqliteHeader = "/usr/include/sqlite3.h"
        Assert.True(File.Exists sqliteHeader, $"Required system header missing: {sqliteHeader}")
        let work = TestHelpers.TestScratch.create "m2-sqlite-static-seam"
        try
            let refusedWithMessage (expectedCode: string) (expectedFragment: string) = function
                | Error (refusal: Diagnostics.Refusal) ->
                    Assert.Equal<string list>([ expectedCode ], refusal.Diagnostics |> List.map (fun d -> d.Code) |> List.distinct)
                    Assert.Contains(expectedFragment, refusal.Text, StringComparison.Ordinal)
                | Ok _ -> failwithf "expected %s refusal containing '%s', got success" expectedCode expectedFragment

            let usedC = Path.Combine(work, "sqlite_used.c")
            let usedObj = Path.Combine(work, "sqlite_used.o")
            let deadC = Path.Combine(work, "sqlite_dead.c")
            let deadObj = Path.Combine(work, "sqlite_dead.o")
            let archivePath = Path.Combine(work, "libsqlite3.a")
            let sharedPath = Path.Combine(work, "libsqlite3.so")

            let usedBody =
                [ "int sqlite3_open_v2(const char *f, void **p, int fl, const char *v) { (void)f;(void)p;(void)fl;(void)v; return 0; }"
                  "int sqlite3_close_v2(void *db) { (void)db; return 0; }"
                  "int sqlite3_prepare_v2(void *db, const char *s, int n, void **st, const char **t) { (void)db;(void)s;(void)n;(void)st;(void)t; return 0; }"
                  "int sqlite3_prepare_v3(void *db, const char *s, int n, unsigned p, void **st, const char **t) { (void)db;(void)s;(void)n;(void)p;(void)st;(void)t; return 0; }"
                  "int sqlite3_step(void *st) { (void)st; return 0; }"
                  "int sqlite3_reset(void *st) { (void)st; return 0; }"
                  "int sqlite3_finalize(void *st) { (void)st; return 0; }"
                  "int sqlite3_clear_bindings(void *st) { (void)st; return 0; }"
                  "int sqlite3_bind_text(void *st, int i, const char *v, int n, void (*d)(void *)) { (void)st;(void)i;(void)v;(void)n;(void)d; return 0; }"
                  "int sqlite3_bind_blob(void *st, int i, const void *v, int n, void (*d)(void *)) { (void)st;(void)i;(void)v;(void)n;(void)d; return 0; }"
                  "const unsigned char *sqlite3_column_text(void *st, int c) { (void)st;(void)c; return 0; }"
                  "const void *sqlite3_column_blob(void *st, int c) { (void)st;(void)c; return 0; }"
                  "int sqlite3_column_bytes(void *st, int c) { (void)st;(void)c; return 0; }"
                  "const char *sqlite3_errmsg(void *db) { (void)db; return 0; }"
                  "int sqlite3_bind_parameter_index(void *st, const char *z) { (void)st;(void)z; return 0; }" ]
                |> String.concat "\n"
            let deadBody = "int sqlite3_unused_dead_symbol(int value) { return value * 17; }\n"

            writeUtf8 usedC usedBody
            writeUtf8 deadC deadBody
            run work "compile-sqlite-used" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-ffunction-sections"; "-fdata-sections"; usedC; "-o"; usedObj ]) |> ignore
            run work "compile-sqlite-dead" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-ffunction-sections"; "-fdata-sections"; deadC; "-o"; deadObj ]) |> ignore
            run work "ar-sqlite-static" (ClangToolchain.arStartInfo [ "rcs"; archivePath; usedObj; deadObj ]) |> ignore
            run work "compile-sqlite-so" (ClangToolchain.startInfo [ "-O2"; "-shared"; "-fPIC"; "-Wl,-soname,libsqlite3.so.0"; usedC; deadC; "-o"; sharedPath ]) |> ignore

            let archiveSha = ClangToolchain.sha256 archivePath
            let inspectedArchive = ArchiveInspection.inspect archivePath (Some archiveSha) |> accepted
            Assert.True(inspectedArchive.Symbols.ContainsKey "sqlite3_open_v2")
            Assert.True(inspectedArchive.Symbols.ContainsKey "sqlite3_unused_dead_symbol")

            let outDir = Path.Combine(work, "generated")
            let pilotPath = Path.Combine(work, "StaticSqliteSeam.pilot.toml")
            writeLines pilotPath
                (sqlitePilotLines outDir
                    [ "[library.artifact]"
                      "kind = \"static-archive\""
                      "path = " + quote archivePath
                      "sha256 = " + quote archiveSha ])

            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilotPath false false Types.LP64)
            |> accepted |> ignore

            let provenancePath = Assert.Single(Directory.GetFiles(outDir, "*.link-provenance.toml", SearchOption.AllDirectories))
            let provenanceText = File.ReadAllText provenancePath
            Assert.Contains("kind = \"static-archive\"", provenanceText)
            Assert.Contains(archiveSha.ToLowerInvariant(), provenanceText)
            Assert.Contains("sqlite_used.o", provenanceText)
            Assert.Contains("sqlite_dead.o", provenanceText)

            let loadingPath = Path.Combine(work, "Loading.clef")
            writeLines loadingPath
                [ "module Fidelity.Sqlite.StaticLoading"
                  "let archiveContract : BAREWire.Platform.StaticArchiveContract = {"
                  "    ArchivePath = " + quote archivePath
                  "    ArchiveSource = \"sqlite3-static-probe\""
                  "    Sha256 = " + quote archiveSha
                  "    Format = \"elf64-relocatable\""
                  "    Visibility = \"default\""
                  "}"
                  "let contract : BAREWire.Platform.ProcessLinkSpec = {"
                  "    Contract = {"
                  "        Identity = \"sqlite-static-seam\""
                  "        Version = \"1\""
                  "        Source = \"sqlite3.h\""
                  "        Library = \"sqlite3\""
                  "        Runtime = \"libc\""
                  "        Os = \"linux\""
                  "        Arch = \"x86_64\""
                  "        LoadAt = \"StaticLink\""
                  "        LiveUntil = \"ProcessTermination\""
                  "        Unloading = \"Forbidden\""
                  "        ExportedLibrary = \"Forbidden\""
                  "    }"
                  "    LinkKind = \"static-archive\""
                  "    DynamicSharedObject = \"\""
                  "    StaticArchive = Some {"
                  "        ArchivePath = " + quote archivePath
                  "        ArchiveSource = \"sqlite3-static-probe\""
                  "        Sha256 = " + quote archiveSha
                  "        Format = \"elf64-relocatable\""
                  "        Visibility = \"default\""
                  "    }"
                  "}" ]

            let loadingDigest =
                Substantiation.inspectSource (set [ "Fidelity.Sqlite.StaticLoading.archiveContract"; "Fidelity.Sqlite.StaticLoading.contract" ]) loadingPath
                |> accepted
            Assert.Contains("Fidelity.Sqlite.StaticLoading.archiveContract", loadingDigest.CompileTimeDeclarations)
            Assert.Contains("Fidelity.Sqlite.StaticLoading.contract", loadingDigest.CompileTimeDeclarations)

            let boundaryClef = Path.Combine(outDir, "Boundary", "Boundary.clef")
            let typesClef = Path.Combine(outDir, "Types.clef")
            let boundaryFidproj = Path.Combine(outDir, "Fidelity.Sqlite.Boundary.fidproj")
            Assert.True(File.Exists boundaryClef)
            Assert.True(File.Exists typesClef)
            Assert.True(File.Exists boundaryFidproj)

            let boundaryRequest : SubstantiationBoundary.Request =
                { FidprojPath = boundaryFidproj
                  PackageName = "Fidelity.Sqlite.Boundary"
                  LinkLibraries = [| "sqlite3" |]
                  LinkObjects = [| archivePath |]
                  PilotPath = Some pilotPath
                  LinkProvenancePath = Some provenancePath
                  Sources = [| loadingPath, File.ReadAllText loadingPath; typesClef, File.ReadAllText typesClef; boundaryClef, File.ReadAllText boundaryClef |]
                  WorkDirectory = Path.Combine(work, "boundary-eval") }
            let boundaryEvidence = ScratchDirectory.within work (fun () -> SubstantiationBoundary.evaluate boundaryRequest) |> accepted
            Assert.Equal("static-archive", boundaryEvidence.LinkMode)
            Assert.True boundaryEvidence.Strata.StaticArchive
            Assert.Contains("static-dynamic-link-elision", boundaryEvidence.RequiredSeamChecks)
            Assert.Contains(boundaryEvidence.RestsOn, fun h -> h.Family = "H-Link" && h.Statement.Contains(archivePath, StringComparison.Ordinal))

            let staticProbeC = Path.Combine(work, "sqlite_static_probe.c")
            let staticBin = Path.Combine(work, "sqlite_static_probe")
            let dynProbeC = Path.Combine(work, "sqlite_dynamic_probe.c")
            writeLines staticProbeC
                [ "int sqlite3_open_v2(const char *f, void **p, int fl, const char *v);"
                  "int sqlite3_close_v2(void *db);"
                  "int main(void) {"
                  "    void *db = 0;"
                  "    int rc = sqlite3_open_v2(\":memory:\", &db, 0, 0);"
                  "    return rc + sqlite3_close_v2(db);"
                  "}" ]
            writeLines dynProbeC
                [ "int sqlite3_open_v2(const char *f, void **p, int fl, const char *v);"
                  "int sqlite3_close_v2(void *db);"
                  "int main(void) {"
                  "    void *db = 0;"
                  "    int rc = sqlite3_open_v2(\":memory:\", &db, 0, 0);"
                  "    return rc + sqlite3_close_v2(db);"
                  "}" ]
            run work "compile-sqlite-static-probe" (ClangToolchain.startInfo [ "-O2"; "-Wl,--gc-sections"; staticProbeC; archivePath; "-o"; staticBin ]) |> ignore

            let staticElf = SubstantiationSeams.inspectElfExecutable staticBin |> accepted
            let sharedElf = SubstantiationSeams.inspectElfExecutable sharedPath |> accepted
            Assert.Equal(Some "libsqlite3.so.0", sharedElf.Soname)

            let stubFile name : Substantiation.FileDigest =
                let p = Path.Combine(work, name)
                writeUtf8 p name
                { Substantiation.Path = p; Sha256 = ClangToolchain.sha256 p }
            let artifactsDir = Path.Combine(work, "artifacts")
            Directory.CreateDirectory artifactsDir |> ignore
            let manifest =
                TargetLibraryCensusFixtures.buildManifest
                    "Fidelity.Sqlite.Boundary"
                    artifactsDir
                    (stubFile "SqliteStatic.fidproj")
                    (stubFile "Composer.dll")
                    (stubFile "dotnet")
                    ({ Substantiation.Path = staticBin; Sha256 = staticElf.Sha256 } : Substantiation.FileDigest)
                    (stubFile "Harness.fidproj")
                    (stubFile "compiler.log")
                    (stubFile "01_psg0.json")
                    (stubFile "10_witness_units.json")
                    boundaryEvidence
                    (TargetLibraryCensusFixtures.buildProof 1 1 "0" "0" "0" "0" "0")

            let seamCheck (id: string) (elided: string[]) (retained: string[]) : SubstantiationSeams.PhysicalCheckSpec =
                { Id = id
                  Category = "static-dynamic-link-elision"
                  Summary = "SQLite M2 static archive vs shared object binding elision check"
                  EntrySource = boundaryClef
                  ExtraSources = [||]
                  IncludeRuntimeSources = false
                  RequiredSymbols = [||]
                  LoadingContract = Some "Fidelity.Sqlite.StaticLoading.contract"
                  ExpectedExitCode = 0
                  ExpectedStdout = ""
                  Elf =
                    Some
                        { ElidedSymbols = elided
                          RetainedStaticSymbols = retained
                          ForbiddenNeededLibraries = [| "libsqlite3.so.0" |]
                          SharedLibrary = Some sharedPath
                          SharedLibrarySha256 = Some sharedElf.Sha256
                          DynamicProbeSource = Some dynProbeC
                          RetainedDynamicImports = [| "sqlite3_open_v2"; "sqlite3_close_v2" |] } }

            let probeWork = Path.Combine(work, "seam-probe-work")
            Directory.CreateDirectory probeWork |> ignore
            let comparison =
                match SubstantiationSeams.verifyElfExpectationsDiagnosed manifest (seamCheck "sqlite-static-elision" [| "sqlite3_unused_dead_symbol" |] [| "sqlite3_open_v2"; "sqlite3_close_v2" |]) staticElf probeWork |> accepted with
                | Some cmp -> cmp
                | None -> failwith "Expected SharedLibraryComparison for SQLite static-dynamic-link-elision seam"

            Assert.True(staticElf.DefinedSymtab.Contains "sqlite3_open_v2")
            Assert.True(staticElf.DefinedSymtab.Contains "sqlite3_close_v2")
            Assert.False(staticElf.DefinedSymtab.Contains "sqlite3_unused_dead_symbol")
            Assert.DoesNotContain("libsqlite3.so.0", staticElf.NeededLibraries)

            let dynElf = SubstantiationSeams.inspectElfExecutable comparison.DynamicProbeBinaryPath |> accepted
            Assert.Contains("libsqlite3.so.0", dynElf.NeededLibraries)
            Assert.True(dynElf.UndefinedDynsym.Contains "sqlite3_open_v2")
            Assert.True(dynElf.UndefinedDynsym.Contains "sqlite3_close_v2")
            Assert.False(dynElf.UndefinedDynsym.Contains "sqlite3_unused_dead_symbol")

            // Exact negative control 1 (FS9253): dead symbol pulled into static binary refuses elision.
            let leakyC = Path.Combine(work, "sqlite_leaky_probe.c")
            let leakyBin = Path.Combine(work, "sqlite_leaky_probe")
            writeLines leakyC
                [ "int sqlite3_open_v2(const char *f, void **p, int fl, const char *v);"
                  "int sqlite3_close_v2(void *db);"
                  "int sqlite3_unused_dead_symbol(int value);"
                  "int main(void) { void *db = 0; return sqlite3_open_v2(\":memory:\", &db, 0, 0) + sqlite3_close_v2(db) + sqlite3_unused_dead_symbol(0); }" ]
            run work "compile-sqlite-leaky" (ClangToolchain.startInfo [ "-O2"; leakyC; archivePath; "-o"; leakyBin ]) |> ignore
            let leakyElf = SubstantiationSeams.inspectElfExecutable leakyBin |> accepted
            SubstantiationSeams.verifyElfExpectationsDiagnosed manifest (seamCheck "sqlite-leaky-dead" [| "sqlite3_unused_dead_symbol" |] [| "sqlite3_open_v2"; "sqlite3_close_v2" |]) leakyElf probeWork
            |> refusedWithMessage "FS9253" "expected dead symbol 'sqlite3_unused_dead_symbol' to be elided"

            // Exact negative control 2 (FS9253): dynamic probe passed as static executable refuses DT_NEEDED libsqlite3.so.0.
            SubstantiationSeams.verifyElfExpectationsDiagnosed manifest (seamCheck "sqlite-dyn-leaked" [||] [||]) dynElf probeWork
            |> refusedWithMessage "FS9253" "forbids DT_NEEDED 'libsqlite3.so.0'"

            // Exact negative control 3 (FS9253): declared elided symbol absent from static archive refuses cleanly.
            SubstantiationSeams.verifyElfExpectationsDiagnosed manifest (seamCheck "sqlite-missing-dead" [| "sqlite3_absent_dead_symbol" |] [| "sqlite3_open_v2" |]) staticElf probeWork
            |> refusedWithMessage "FS9253" "do not define 'sqlite3_absent_dead_symbol'"
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``SQLite M2 static archive compiles Clef probes via Composer opt and ld.lld LTO elides dead symbols and reconciles dossier`` () =
        let work = TestHelpers.TestScratch.create "m2-sqlite-static-seam-lto"
        try
            let header = Path.Combine(work, "sqlite3_static.h")
            writeLines header
                [ "int sqlite3_libversion_number(void);"
                  "int sqlite3_threadsafe(void);"
                  "int sqlite3_sleep(int ms);"
                  "void *sqlite3_malloc(int n);"
                  "void sqlite3_free(void *ptr);"
                  "int sqlite3_unused_dead_symbol(int value);" ]
            let usedC = Path.Combine(work, "sqlite_used.c")
            writeLines usedC
                [ "#include <stdlib.h>"
                  "#include \"sqlite3_static.h\""
                  "int sqlite3_libversion_number(void) { return 3046001; }"
                  "int sqlite3_threadsafe(void) { return 1; }"
                  "int sqlite3_sleep(int ms) { return ms >= 0 ? ms : -22; }"
                  "void *sqlite3_malloc(int n) { return n > 0 ? malloc((size_t)n) : (void *)0; }"
                  "void sqlite3_free(void *ptr) { free(ptr); }"
                  "#ifdef SQLITE_STATIC_LTO"
                  "__attribute__((visibility(\"hidden\")))"
                  "#endif"
                  "int sqlite3_unused_dead_symbol(int value) { return value + 99; }" ]

            // Seam fixture (not full upstream SQLite): mark the dead symbol hidden only in the static LTO archive
            // (so ld.lld LTO elides it intra-object even under --export-dynamic) while libsqlite3.so retains it in .dynsym.
            let usedObj = Path.Combine(work, "sqlite_used.o")
            let usedPicObj = Path.Combine(work, "sqlite_used.pic.o")
            let archivePath = Path.Combine(work, "libsqlite3.a")
            let sharedPath = Path.Combine(work, "libsqlite3.so")
            let ltoTargetFlag =
                match System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture with
                | System.Runtime.InteropServices.Architecture.X64 -> "--target=x86_64-unknown-linux-gnu"
                | System.Runtime.InteropServices.Architecture.Arm64 -> "--target=aarch64-unknown-linux-gnu"
                | arch -> failwith ("Unsupported architecture for LTO target triple check: " + string arch)

            run work "cc-used-lto" (ClangToolchain.startInfo [ ltoTargetFlag; "-O2"; "-flto"; "-DSQLITE_STATIC_LTO"; "-c"; usedC; "-o"; usedObj ]) |> ignore
            run work "ar-sqlite3" (ClangToolchain.arStartInfo [ "rcs"; archivePath; usedObj ]) |> ignore
            run work "cc-used-pic" (ClangToolchain.startInfo [ "-O2"; "-fPIC"; "-c"; usedC; "-o"; usedPicObj ]) |> ignore
            run work "ld-sqlite3-so" (ClangToolchain.startInfo [ "-shared"; "-fPIC"; "-Wl,-soname,libsqlite3.so.0"; usedPicObj; "-o"; sharedPath ]) |> ignore

            let sharedSha = ClangToolchain.sha256 sharedPath
            let pilot = Path.Combine(work, "SqliteStatic.pilot.toml")
            writeLines pilot
                [ "[library]"
                  "name = \"sqlite3\""
                  "headers = [" + quote header + "]"
                  "include_paths = [" + quote work + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote (ClangToolchain.sha256 archivePath)
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"generated\""
                  "[error_conventions]"
                  "default = \"none\""
                  "[error_conventions.overrides]"
                  "sqlite3_sleep = \"return_code\""
                  "[annotations.nonnull]"
                  "sqlite3_free = [0]"
                  "[options]"
                  "c_header_mode = true"
                  "opaque_handles = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "[[options.bindings]]"
                  "name = \"sqlite3_malloc\""
                  "symbol = \"sqlite3_malloc\""
                  "return_handle = \"SqliteAlloc\""
                  "resource = { kind = \"owned_nullable_return\", release_binding = \"Fidelity.Sqlite.Boundary.sqlite3_free\", release_parameter = 0, hypothesis = { identity = \"sqlite.malloc.owned\", version = \"1\", source = \"sqlite3_static.h\" } }"
                  "[[options.bindings]]"
                  "name = \"sqlite3_free\""
                  "symbol = \"sqlite3_free\""
                  "parameter_handles = [\"SqliteAlloc\"]"
                  "[[namespace]]"
                  "name = \"Fidelity.Sqlite.Boundary\""
                  "library = \"sqlite3\""
                  "description = \"SQLite M2 static archive Composer LTO seam boundary\""
                  "functions = [\"sqlite3_libversion_number\", \"sqlite3_threadsafe\", \"sqlite3_sleep\", \"sqlite3_malloc\", \"sqlite3_free\"]" ]

            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64)
            |> accepted |> ignore

            let provenance =
                match Directory.GetFiles(Path.Combine(work, "generated"), "*.link-provenance.toml", SearchOption.AllDirectories) with
                | [| path |] -> path
                | paths -> failwithf "expected one generated link provenance manifest, got [%s]" (String.concat "; " paths)
            let generatedSources =
                [ Path.Combine(work, "generated", "Types.clef")
                  Path.Combine(work, "generated", "ReturnCode.clef")
                  Path.Combine(work, "generated", "Boundary", "Types.clef")
                  Path.Combine(work, "generated", "Boundary", "Boundary.clef") ]
                |> List.filter File.Exists
            Assert.NotEmpty generatedSources

            let loading = Path.Combine(work, "Loading.clef")
            let loadingTemplate = Path.Combine(__SOURCE_DIRECTORY__, "..", "native-callbacks", "Loading.clef")
            writeUtf8 loading
                (File.ReadAllText(loadingTemplate)
                    .Replace("landingprobe", "sqlite3")
                    .Replace("tests/native-callbacks/probe.c", header)
                    .Replace("prebuilt probe archive", "SQLite M2 static archive"))

            let caller = Path.Combine(work, "Caller.clef")
            writeLines caller
                [ "module Fidelity.Sqlite.Caller"
                  "let sample () : int ="
                  "    let ver = Fidelity.Sqlite.Boundary.sqlite3_libversion_number ()"
                  "    let safe = Fidelity.Sqlite.Boundary.sqlite3_threadsafe ()"
                  "    let slept = Fidelity.Sqlite.Boundary.sqlite3_sleep 5"
                  "    if ver = 3046001 && safe = 1 && slept = 5 then 1 else 0" ]

            let dependencies, platform = platformTables ()
            let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
            let fidproj = Path.Combine(work, "SqliteStatic.fidproj")
            writeLines fidproj
                [ "[package]"
                  "name = \"SqliteStatic\""
                  "version = \"1.0.0\""
                  "[compilation]"
                  "target = \"cpu\""
                  "[build]"
                  "output_kind = \"library\""
                  "sources = [" + (([ loading ] @ generatedSources @ [ caller ]) |> List.filter (fun p -> Path.GetFileName p <> "ReturnCode.clef") |> List.map quote |> String.concat ", ") + "]"
                  "[dependencies]"
                  yield! dependencies
                  "metadata = { path = " + quote metadata + " }"
                  "[platform]"
                  yield! platform
                  "loading = \"Landing.Acceptance.Loading.contract\""
                  "[link]"
                  "libraries = [\"sqlite3\"]" ]

            let manifestDir = Path.Combine(work, "manifest")
            let manifest =
                Substantiation.evaluate
                    { FidprojPath = fidproj
                      PilotPath = Some pilot
                      LinkProvenancePath = Some provenance
                      OutputDirectory = Some manifestDir
                      WorkDirectory = Some(Path.Combine(work, "substantiation"))
                      AllowWarnings = false
                      Verbose = false }
                |> accepted
            Assert.Equal("static-archive", manifest.Boundary.LinkMode)
            Assert.Contains("static-dynamic-link-elision", manifest.Boundary.RequiredSeamChecks)
            Assert.Equal(manifest.Proof.TotalObligations, manifest.Proof.DischargedUnsatCount)
            Assert.Equal(0, manifest.Proof.PendingOpenCount)
            let manifestJson = Path.Combine(manifestDir, "SqliteStatic.substantiation.json")

            let probesDir = Path.Combine(work, "probes")
            Directory.CreateDirectory probesDir |> ignore
            let probeScalar = Path.Combine(probesDir, "ProbeSqliteScalar.clef")
            writeLines probeScalar
                [ "module Fidelity.Sqlite.ProbeScalar"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    if Fidelity.Sqlite.Boundary.sqlite3_libversion_number () = 3046001"
                  "       && Fidelity.Sqlite.Boundary.sqlite3_threadsafe () = 1 then"
                  "        Console.writeln \"sqlite:scalar:ok\""
                  "        0"
                  "    else 1" ]
            let probeLink = Path.Combine(probesDir, "ProbeSqliteElision.clef")
            writeLines probeLink
                [ "module Fidelity.Sqlite.ProbeElision"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    if Fidelity.Sqlite.Boundary.sqlite3_libversion_number () = 3046001 then"
                  "        Console.writeln \"sqlite:elision:ok\""
                  "        0"
                  "    else 2" ]
            let probeError = Path.Combine(probesDir, "ProbeSqliteSentinel.clef")
            writeLines probeError
                [ "module Fidelity.Sqlite.ProbeSentinel"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let err = Fidelity.Sqlite.Boundary.sqlite3_sleep (-1)"
                  "    let ok = Fidelity.Sqlite.Boundary.sqlite3_sleep 5"
                  "    if err = -22 && ok = 5 then"
                  "        Console.writeln \"sqlite:sentinel:ok\""
                  "        0"
                  "    else 3" ]
            let probeLifecycle = Path.Combine(probesDir, "ProbeSqliteLifecycle.clef")
            writeLines probeLifecycle
                [ "module Fidelity.Sqlite.ProbeLifecycle"
                  "let private validAlloc () : int ="
                  "    match Fidelity.Sqlite.Boundary.sqlite3_malloc 64 with"
                  "    | Some ptr ->"
                  "        Fidelity.Sqlite.Boundary.sqlite3_free ptr"
                  "        1"
                  "    | None -> 0"
                  "let private zeroAlloc () : int ="
                  "    match Fidelity.Sqlite.Boundary.sqlite3_malloc 0 with"
                  "    | Some ptr ->"
                  "        Fidelity.Sqlite.Boundary.sqlite3_free ptr"
                  "        0"
                  "    | None -> 1"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    if validAlloc () = 1 && zeroAlloc () = 1 then"
                  "        Console.writeln \"sqlite:lifecycle:ok\""
                  "        0"
                  "    else 4" ]
            let dynamicProbeC = Path.Combine(probesDir, "sqlite_dyn_probe.c")
            writeLines dynamicProbeC
                [ "#include " + quote header
                  "int main(void) {"
                  "    return (sqlite3_libversion_number() == 3046001 && sqlite3_threadsafe() == 1) ? 0 : 1;"
                  "}" ]

            let controlsDir = Path.Combine(work, "controls")
            Directory.CreateDirectory controlsDir |> ignore
            let missingHeader = Path.Combine(controlsDir, "missing_sqlite.h")
            writeLines missingHeader [ "int sqlite3_nonexistent_symbol(int value);" ]
            let refusalMissing = Path.Combine(controlsDir, "MissingSym.pilot.toml")
            writeLines refusalMissing
                [ "[library]"
                  "name = \"sqlite3\""
                  "headers = [" + quote missingHeader + "]"
                  "include_paths = [" + quote controlsDir + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote (ClangToolchain.sha256 archivePath)
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"out\""
                  "[options]"
                  "c_header_mode = true"
                  "link_libraries = true"
                  "[[namespace]]"
                  "name = \"Refusal.Sqlite\""
                  "library = \"sqlite3\""
                  "description = \"Missing archive symbol negative control\""
                  "functions = [\"sqlite3_nonexistent_symbol\"]" ]
            let refusalCallableSrc = Path.Combine(controlsDir, "RefusalCallable.clef")
            writeLines refusalCallableSrc
                [ "module Refusal.SqliteCallable"
                  "let exportedHelper (x: int) : int = x + 1" ]
            let refusalSubProj = Path.Combine(controlsDir, "RefusalCallable.fidproj")
            writeLines refusalSubProj
                [ "[package]"
                  "name = \"RefusalSqliteCallable\""
                  "version = \"1.0.0\""
                  "[compilation]"
                  "target = \"cpu\""
                  "[build]"
                  "output_kind = \"library\""
                  "sources = [" + quote refusalCallableSrc + "]"
                  "[dependencies]"
                  yield! dependencies
                  "[platform]"
                  yield! platform ]
            let refusalLeakSrc = Path.Combine(controlsDir, "LeakSqliteAlloc.clef")
            writeLines refusalLeakSrc
                [ "module Refusal.LeakSqlite"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    match Fidelity.Sqlite.Boundary.sqlite3_malloc 32 with"
                  "    | Some ptr -> if ptr = ptr then 0 else 1"
                  "    | None -> 1" ]
            let refusalComposerProj = Path.Combine(controlsDir, "LeakSqliteAlloc.fidproj")
            writeLines refusalComposerProj
                [ "[package]"
                  "name = \"LeakSqliteAlloc\""
                  "version = \"1.0.0\""
                  "[compilation]"
                  "target = \"cpu\""
                  "[build]"
                  "output_kind = \"console\""
                  "sources = [" + (([ loading ] @ generatedSources @ [ refusalLeakSrc ]) |> List.filter (fun p -> Path.GetFileName p <> "ReturnCode.clef") |> List.map quote |> String.concat ", ") + "]"
                  "[dependencies]"
                  yield! dependencies
                  "metadata = { path = " + quote metadata + " }"
                  "[platform]"
                  yield! platform
                  "loading = \"Landing.Acceptance.Loading.contract\""
                  "[link]"
                  "libraries = [\"sqlite3\"]" ]

            let spec = Path.Combine(work, "SqliteStatic.seams.toml")
            writeLines spec
                [ "schema_version = 1"
                  "target_triple = " + quote manifest.Boundary.ClangTarget
                  ""
                  "[[checks]]"
                  "id = \"sqlite-scalar\""
                  "category = \"scalar-handle-link-and-call\""
                  "summary = \"SQLite M2 scalar version and threadsafe checks run through Composer LTO static archive link.\""
                  "entry_source = " + quote probeScalar
                  "required_symbols = [\"sqlite3_libversion_number\", \"sqlite3_threadsafe\"]"
                  "expected_stdout = \"sqlite:scalar:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sqlite-link-elision\""
                  "category = \"static-dynamic-link-elision\""
                  "summary = \"Composer opt + ld.lld LTO elides intra-object bitcode (-flto) dead symbol while shared library link keeps imports.\""
                  "entry_source = " + quote probeLink
                  "required_symbols = [\"sqlite3_libversion_number\"]"
                  "expected_stdout = \"sqlite:elision:ok\""
                  "[checks.elf]"
                  "elided_symbols = [\"sqlite3_unused_dead_symbol\"]"
                  "retained_static_symbols = [\"sqlite3_libversion_number\"]"
                  "forbidden_needed_libraries = [\"libsqlite3.so.0\"]"
                  "shared_library = " + quote sharedPath
                  "shared_library_sha256 = " + quote sharedSha
                  "dynamic_probe_source = " + quote dynamicProbeC
                  "retained_dynamic_imports = [\"sqlite3_libversion_number\", \"sqlite3_threadsafe\"]"
                  ""
                  "[[checks]]"
                  "id = \"sqlite-sentinel\""
                  "category = \"error-sentinel-conformance\""
                  "summary = \"SQLite M2 return_code convention preserves negative sentinel (-22) and positive count (5).\""
                  "entry_source = " + quote probeError
                  "required_symbols = [\"sqlite3_sleep\"]"
                  "expected_stdout = \"sqlite:sentinel:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sqlite-lifecycle\""
                  "category = \"owned-handle-lifecycle\""
                  "summary = \"SQLite M2 owned_nullable_return sqlite3_malloc and sqlite3_free pair handles Some and None safely.\""
                  "entry_source = " + quote probeLifecycle
                  "required_symbols = [\"sqlite3_malloc\", \"sqlite3_free\"]"
                  "expected_stdout = \"sqlite:lifecycle:ok\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-refusal-missing-symbol\""
                  "kind = \"pilot\""
                  "target = " + quote refusalMissing
                  "expected_code = \"FS9228\""
                  "misuse_summary = \"Pilot declares sqlite3_nonexistent_symbol not defined in libsqlite3.a.\""
                  "protection_summary = \"Archive provenance refuses missing symbol with FS9228.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-refusal-callable\""
                  "kind = \"substantiation\""
                  "target = " + quote refusalSubProj
                  "expected_code = \"FS9246\""
                  "misuse_summary = \"Parameterized non-extern library declaration cannot be universally activated.\""
                  "protection_summary = \"Substantiation refuses unactivatable declaration with FS9246.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-refusal-leak\""
                  "kind = \"composer\""
                  "target = " + quote refusalComposerProj
                  "expected_code = \"CCS8403\""
                  "misuse_summary = \"Owned CHandle<SqliteAlloc> returned by sqlite3_malloc escapes without sqlite3_free release.\""
                  "protection_summary = \"Composer refuses leaked SQLite foreign allocation handle with CCS8403.\"" ]

            let evaluated =
                SubstantiationSeams.evaluate
                    { ManifestPath = manifestJson
                      SpecPath = spec
                      OutputDirectory = Some(Path.Combine(work, "seams-out"))
                      WorkDirectory = Some(Path.Combine(work, "seams-work"))
                      Verbose = false }
                |> accepted
            Assert.Equal(4, evaluated.SeamChecks.Checks.Length)
            Assert.Equal(3, evaluated.RefusalControls.Controls.Length)
            Assert.All(evaluated.RefusalControls.Controls, fun c -> Assert.Equal(c.ExpectedCode, c.ObservedCode))
            let sharedComparison = Assert.Single evaluated.Ledger.SharedLibraries
            let ltoRetained =
                evaluated.Ledger.RetainedBinaries
                |> Array.find (fun item -> item.CheckId = "sqlite-link-elision")
            let ltoElf = SubstantiationSeams.inspectElfExecutable ltoRetained.BinaryPath |> accepted
            Assert.Contains("sqlite3_libversion_number", ltoElf.DefinedSymtab)
            Assert.DoesNotContain("sqlite3_unused_dead_symbol", ltoElf.DefinedSymtab)
            Assert.DoesNotContain("sqlite3_unused_dead_symbol", ltoElf.DefinedDynsym)
            Assert.DoesNotContain("sqlite3_unused_dead_symbol", ltoElf.UndefinedDynsym)
            let sharedElf = SubstantiationSeams.inspectElfExecutable sharedComparison.SharedLibraryPath |> accepted
            Assert.Contains("sqlite3_unused_dead_symbol", sharedElf.DefinedDynsym)

            let dossierOut = Path.Combine(work, "dossier-out")
            let generated =
                SubstantiationDossier.generate
                    { ManifestPath = manifestJson
                      OutputDirectory = Some dossierOut
                      RocqProjectPath = None
                      RocqFactsPath = None
                      RocqConfigPath = None
                      SeamChecksReceiptPath = Some evaluated.SeamChecksPath
                      RefusalControlsReceiptPath = Some evaluated.RefusalControlsPath }
                |> accepted
            Assert.StartsWith("SUBSTANTIATED --", generated.Dossier.OverallStatus)
            Assert.Equal(0, generated.Dossier.Counts.Open)
            Assert.Equal(0, generated.Dossier.Counts.PendingSeamCategories)
            Assert.Equal(manifest.Proof.DischargedUnsatCount, generated.Dossier.Counts.Guarantees)

            // Negative control (FS9253): requiring the unreferenced elided symbol fails seam evaluation with the exact symbol message.
            let badSpec = Path.Combine(work, "SqliteStaticMissingReq.seams.toml")
            writeUtf8 badSpec
                (File.ReadAllText(spec).Replace(
                    "required_symbols = [\"sqlite3_libversion_number\"]",
                    "required_symbols = [\"sqlite3_unused_dead_symbol\"]"))
            match SubstantiationSeams.evaluate
                      { ManifestPath = manifestJson
                        SpecPath = badSpec
                        OutputDirectory = Some(Path.Combine(work, "seams-bad-out"))
                        WorkDirectory = Some(Path.Combine(work, "seams-bad-work"))
                        Verbose = false } with
            | Error (refusal: Diagnostics.Refusal) ->
                Assert.Equal<string list>([ "FS9253" ], refusal.Diagnostics |> List.map (fun d -> d.Code) |> List.distinct)
                Assert.Contains(
                    "checks[sqlite-link-elision].required_symbols names 'sqlite3_unused_dead_symbol', which is absent from manifest.Boundary.ExternSymbols.",
                    refusal.Text,
                    StringComparison.Ordinal)
            | Ok _ -> failwith "Expected FS9253 refusal when requiring elided symbol 'sqlite3_unused_dead_symbol'"
        finally
            Directory.Delete(work, true)

    let private writeFileMappedBackingImage (path: string) (prefix64: byte array) (records: (int32 * int64 * int32 * int32) array) =
        Assert.Equal(64, prefix64.Length)
        Assert.Equal(4, records.Length)
        let bytes = Array.zeroCreate<byte> 160
        Buffer.BlockCopy(prefix64, 0, bytes, 0, 64)
        for idx = 0 to records.Length - 1 do
            let f0, f1, f2, f3 = records[idx]
            let baseOff = 64 + idx * 24
            Buffer.BlockCopy(BitConverter.GetBytes f0, 0, bytes, baseOff, 4)
            Buffer.BlockCopy(BitConverter.GetBytes f1, 0, bytes, baseOff + 8, 8)
            Buffer.BlockCopy(BitConverter.GetBytes f2, 0, bytes, baseOff + 16, 4)
            Buffer.BlockCopy(BitConverter.GetBytes f3, 0, bytes, baseOff + 20, 4)
        File.WriteAllBytes(path, bytes)

    let private writeZeroCopyPlatformTables (work: string) (bufferSpecs: (string * string * string * string) list) =
        let _, platform = platformTables ()
        let envProject = sibling "FIDELITY_PLATFORM_ENV_PROJECT" "Fidelity.Platform/Environments/Linux/x86_64/Fidelity.Platform.Environment.fidproj"
        let budgetsTemplate = sibling "FIDELITY_PLATFORM_BUDGETS" "Fidelity.Platform/Profiles/Linux_x86_64_Default/CompatibilityBudgets.clef"
        let descTemplate = sibling "FIDELITY_PLATFORM_DESC" "Fidelity.Platform/Profiles/Linux_x86_64_Default/Description.clef"
        let platDir = Path.Combine(work, "platform-desc")
        Directory.CreateDirectory platDir |> ignore
        let rawBudgets = File.ReadAllText budgetsTemplate
        let cutIdx = rawBudgets.IndexOf("let sharedTensorTileHost: BufferSchema =", StringComparison.Ordinal)
        Assert.True(cutIdx >= 0, "Expected 'let sharedTensorTileHost: BufferSchema =' marker in CompatibilityBudgets.clef")
        let budgetsPrefix = rawBudgets.Substring(0, cutIdx)
        let customBuffers =
            bufferSpecs
            |> List.map (fun (bufName, schemaName, spaceName, accessExpr) ->
                String.concat "\n"
                    [ $"let {bufName}: BufferSchema = {{"
                      $"    Name = {quote bufName}"
                      $"    Schema = {quote schemaName}"
                      "    Capacity = SharedTileCapacityBytes"
                      "    Framing = Framing.Fixed"
                      "    Delimiter = UnframedDelimiterSentinel"
                      "    TrimDelimiter = false"
                      $"    Space = {quote spaceName}"
                      "    Lifetime = Lifetime.Session"
                      $"    Access = {accessExpr}"
                      "    Slot = UnslottedBufferSlot"
                      "}" ])
            |> String.concat "\n\n"
        writeUtf8 (Path.Combine(platDir, "CompatibilityBudgets.clef")) (budgetsPrefix + "\n" + customBuffers + "\n")
        let bufferArrayExpr =
            "Buffers = [| consoleReadln; consoleWrite; "
            + (bufferSpecs |> List.map (fun (bufName, _, _, _) -> bufName) |> String.concat "; ")
            + " |]"
        let rawDesc = File.ReadAllText descTemplate
        let origBuffersLine = "Buffers = [| consoleReadln; consoleWrite; sharedTensorTileHost; sharedTensorTileDevice; sharedTensorTileFile |]"
        Assert.Contains(origBuffersLine, rawDesc)
        let updatedDesc = rawDesc.Replace(origBuffersLine, bufferArrayExpr)
        writeUtf8 (Path.Combine(platDir, "Description.clef")) updatedDesc
        let platFidproj = Path.Combine(platDir, "PlatformDescription.fidproj")
        writeLines platFidproj
            [ "[package]"
              "name = \"Fidelity.Platform.Profiles.Linux_x86_64_Default.Description\""
              "version = \"0.1.0\""
              "[compilation]"
              "target = \"library\""
              "[build]"
              "sources = [\"CompatibilityBudgets.clef\", \"Description.clef\"]"
              "output_kind = \"library\""
              "[dependencies]"
              "environment = { path = " + quote envProject + " }" ]
        [ "\"description\" = { path = " + quote platFidproj + " }" ], platform

    let private assertSharedBufferProofAndDossier
        (manifest: Substantiation.Manifest)
        (reconciled: SubstantiationDossier.StaticLinkDossierReconciliation)
        (expectedPlacements: (string * string) list)
        (generatedSources: string list) =
        // 1. Direct unit verification of SubstantiationProof shared-buffer helpers and ambiguity refusals.
        let canonicalGenSources = generatedSources |> List.map Path.GetFullPath |> List.toArray
        let extractedDecls =
            SubstantiationProof.sharedBufferPlacementDeclarations canonicalGenSources
        Assert.Equal(expectedPlacements.Length, extractedDecls.Length)
        for (expectedName, expectedSpace) in expectedPlacements do
            Assert.Contains(extractedDecls, fun (_, line, pName, pSpace) -> line > 0 && pName = expectedName && pSpace = expectedSpace)
        Assert.Equal(
            Some(false, "SharedTensorTileBuffer"),
            SubstantiationProof.matchSharedBufferObligation "fs9220_layout_sharedtensortilebuffer" "cpu-linux-x86_64:SharedTensorTileBuffer")
        Assert.Equal(
            Some(true, "Fidelity.Platform.HostShared"),
            SubstantiationProof.matchSharedBufferObligation "fs9221_space_fidelity_platform_hostshared_2" "cpu-linux-x86_64:Fidelity.Platform.HostShared")
        // Mismatched code/form pair must be rejected.
        Assert.Equal(
            None,
            SubstantiationProof.matchSharedBufferObligation "fs9220_lifetime_sharedtensortilebuffer" "cpu-linux-x86_64:SharedTensorTileBuffer")
        Assert.Equal(
            None,
            SubstantiationProof.matchSharedBufferObligation "fs9223_space_fidelity_platform_hostshared" "cpu-linux-x86_64:Fidelity.Platform.HostShared")
        // Verify dynamic platform placement extraction: explicit custom PlatformDescription.fidproj has 0 placements,
        // empty anchor list returns 0 placements, and default Fidelity.Platform.fidproj yields its 3 placements from source.
        let customPlatDecls =
            SubstantiationProof.platformSharedBufferPlacements (Set.ofArray canonicalGenSources) [| manifest.FidprojPath |]
        Assert.Empty(customPlatDecls)
        Assert.Empty(SubstantiationProof.platformSharedBufferPlacements Set.empty [||])
        let defaultPlatFidproj =
            sibling "FIDELITY_PLATFORM_DEFAULT_FIDPROJ" "Fidelity.Platform/Profiles/Linux_x86_64_Default/Fidelity.Platform.fidproj"
        let defaultPlatDecls =
            SubstantiationProof.platformSharedBufferPlacements Set.empty [| defaultPlatFidproj |]
        Assert.Equal(3, defaultPlatDecls.Length)
        // Verify SubstantiationProof.inspectReceipt refuses duplicate placement names and library/harness ambiguity with FS9237.
        let firstSrc = canonicalGenSources[0]
        let (firstPlacementName, firstSpaceName) = expectedPlacements.Head
        let firstSlug = SubstantiationProof.sharedBufferSlug firstPlacementName
        let dupObsJson =
            $"{{\"obligations\":[{{\"id\":\"fs9220_layout_{firstSlug}\",\"source\":\"cpu-linux-x86_64:{firstPlacementName}\"}}]}}"
        match SubstantiationProof.inspectReceipt [| firstSrc |] firstSrc dupObsJson "" "{}" with
        | Error refusal ->
            Assert.Equal<string list>([ "FS9237" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
            Assert.Contains("cannot also be a library source", refusal.Text, StringComparison.Ordinal)
        | Ok _ -> failwith "Expected FS9237 refusal when harness and library share the same source path"
        let ambigWork = Path.Combine(Path.GetDirectoryName manifest.ArtifactsDirectory, "ambig-unit-check")
        Directory.CreateDirectory ambigWork |> ignore
        let dupHarmonyClef = Path.Combine(ambigWork, "DupHarmony.clef")
        File.Copy(firstSrc, dupHarmonyClef, true)
        match SubstantiationProof.inspectReceipt [| firstSrc |] dupHarmonyClef dupObsJson "" "{}" with
        | Error refusal ->
            Assert.Equal<string list>([ "FS9237" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
            Assert.Contains("ambiguous between library and harness placements", refusal.Text, StringComparison.Ordinal)
        | Ok _ -> failwith "Expected FS9237 refusal when library and harness both declare the same placement"
        let spaceSlug = SubstantiationProof.sharedBufferSlug firstSpaceName
        let spaceObsJson =
            $"{{\"obligations\":[{{\"id\":\"fs9221_space_{spaceSlug}\",\"source\":\"cpu-linux-x86_64:{firstSpaceName}\"}}]}}"
        match SubstantiationProof.inspectReceipt [| firstSrc |] dupHarmonyClef spaceObsJson "" "{}" with
        | Error refusal ->
            Assert.Equal<string list>([ "FS9237" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
            Assert.Contains("ambiguous between library and harness placements", refusal.Text, StringComparison.Ordinal)
        | Ok _ -> failwith "Expected FS9237 refusal when library and harness share a MemorySpace"
        let dupLibClef = Path.Combine(ambigWork, "DupLib.clef")
        File.Copy(firstSrc, dupLibClef, true)
        let unusedHarClef = Path.Combine(ambigWork, "UnusedHar.clef")
        File.WriteAllText(unusedHarClef, "module UnusedHar\n")
        match SubstantiationProof.inspectReceipt [| firstSrc; dupLibClef |] unusedHarClef spaceObsJson "" "{}" with
        | Error refusal ->
            Assert.Equal<string list>([ "FS9237" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
            Assert.Contains("declared multiple times in library sources", refusal.Text, StringComparison.Ordinal)
        | Ok _ -> failwith "Expected FS9237 refusal when two library sources declare the same placement Name"
        // Verify brace-depth extraction when StructLayout = { Name = "InnerStruct" } precedes top-level Name = "OuterPlacement".
        let reorderedClef = Path.Combine(ambigWork, "ReorderedPlacement.clef")
        File.WriteAllText(
            reorderedClef,
            "module ReorderedMod\nlet p : Expr<SharedBufferPlacement> =\n    <@\n        { StructLayout = { Name = \"InnerStruct\"; Layout = { Size = 24; Alignment = 8; Fields = [||] }; MayAlias = false; FlexibleArray = None; Documentation = None }\n          Name = \"OuterPlacement\"\n          SpaceDescriptor = { Space = \"Fidelity.Platform.HostShared\"; MemoryRegion = MemoryRegionKind.SRAM; Access = \"ReadWrite\"; ByteOrder = \"little\"; ElementSize = 24; ElementCount = 4; RegionByteExtent = 96; RegionAlignment = 64; RegionLifetimeDepth = 1; LibraryRetentionDepth = 2 } }\n    @>\n")
        let reorderedDecls = SubstantiationProof.sharedBufferPlacementDeclarations [| reorderedClef |]
        Assert.Equal<(string * string) array>([| "OuterPlacement", "Fidelity.Platform.HostShared" |], reorderedDecls |> Array.map (fun (_, _, n, s) -> n, s))
        // Verify: (i) two library .clef files with distinct placement Names in the same MemorySpace succeed in
        // SubstantiationProof.inspectReceipt (attributing all 2 * 4 + 1 = 9 obligations to Library), and
        // (ii) a library placement in firstSpaceName coexisting with default Fidelity.Platform CompatibilityBudgets.clef
        // attributes fs9221_space_* to Library while attributing hostSharedTilePlacement to Dependency.
        let buildSyntheticReceipt (rows: (string * string) list) =
            let obsJson =
                "{\"obligations\":["
                + (rows |> List.map (fun (id, src) -> $"{{\"id\":\"{id}\",\"source\":\"{src}\"}}") |> String.concat ",")
                + "]}"
            let query =
                rows
                |> List.map (fun (id, _) -> $"(declare-const {id} Bool)\n(assert {id})\n(assert (not {id}))\n(check-sat)\n(reset)")
                |> String.concat "\n"
            let queryHash =
                System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes query))
            let outcomesJson =
                rows
                |> List.map (fun (id, src) -> $"{{\"Anchor\":\"{id}\",\"Source\":\"{src}\",\"Verdict\":\"unsat\"}}")
                |> String.concat ","
            let stdoutJson = rows |> List.map (fun _ -> "unsat") |> String.concat "\\n"
            let receiptJson =
                $"{{\"Stage\":\"psg\",\"Invocation\":\"11111111-1111-1111-1111-111111111111\",\"InputSha256\":\"{queryHash}\",\"QuerySha256\":\"{queryHash}\",\"Tools\":[{{\"Executable\":\"/usr/bin/cvc5\",\"Sha256\":\"{queryHash}\",\"Version\":\"cvc5 1.3.1\",\"Arguments\":[\"--lang=smt2\",\"--tlimit=25000\"]}}],\"Outcomes\":[{outcomesJson}],\"StandardOutput\":\"{stdoutJson}\",\"StandardError\":\"\"}}"
            obsJson, query, receiptJson
        let secondPlacementName = "Second" + firstPlacementName
        let secondSlug = SubstantiationProof.sharedBufferSlug secondPlacementName
        let secondLibClef = Path.Combine(ambigWork, "SecondLib.clef")
        File.WriteAllText(
            secondLibClef,
            File.ReadAllText(firstSrc).Replace($"Name = \"{firstPlacementName}\"", $"Name = \"{secondPlacementName}\""))
        let multiRows =
            [ for prefix in [ "fs9220_layout_"; "fs9221_extent_"; "fs9222_lifetime_"; "fs9223_exclusivity_" ] do
                  yield prefix + firstSlug, $"cpu-linux-x86_64:{firstPlacementName}"
                  yield prefix + secondSlug, $"cpu-linux-x86_64:{secondPlacementName}"
              yield "fs9221_space_" + spaceSlug, $"cpu-linux-x86_64:{firstSpaceName}" ]
        let multiObs, multiQuery, multiReceipt = buildSyntheticReceipt multiRows
        match SubstantiationProof.inspectReceipt [| firstSrc; secondLibClef |] unusedHarClef multiObs multiQuery multiReceipt with
        | Ok summary ->
            Assert.Equal(9, summary.TotalObligations)
            Assert.Equal(9, summary.LibraryObligations)
            Assert.Equal(9, summary.LibraryDischargedUnsatCount)
        | Error refusal -> failwith $"Expected multi-placement MemorySpace receipt to succeed, got: {refusal.Text}"
        match SubstantiationDossier.inspectDossierObligations [| firstSrc; dupLibClef |] unusedHarClef spaceObsJson with
        | Error refusal ->
            Assert.Equal<string list>([ "FS9250" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
            Assert.Contains("declared multiple times in library sources", refusal.Text, StringComparison.Ordinal)
        | Ok _ -> failwith "Expected FS9250 refusal when two library sources declare the same placement Name in SubstantiationDossier"
        match SubstantiationDossier.inspectDossierObligations [| firstSrc |] dupHarmonyClef spaceObsJson with
        | Error refusal ->
            Assert.Equal<string list>([ "FS9250" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
            Assert.Contains("ambiguous between library and harness placements", refusal.Text, StringComparison.Ordinal)
        | Ok _ -> failwith "Expected FS9250 refusal when library and harness both declare the same placement Name in SubstantiationDossier"
        match SubstantiationDossier.inspectDossierObligations [| firstSrc; secondLibClef |] unusedHarClef multiObs with
        | Ok obligations ->
            Assert.Equal(9, obligations.Length)
            let spaceRow = obligations |> Array.find (fun row -> row.Anchor = "fs9221_space_" + spaceSlug)
            Assert.Equal(Path.GetFileNameWithoutExtension firstSrc, spaceRow.Declaration)
        | Error refusal -> failwith $"Expected SubstantiationDossier multi-placement attribution to succeed, got: {refusal.Text}"
        let defaultPlatCheckDir = Path.Combine(Path.GetTempPath(), "farscape-default-plat-check-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory defaultPlatCheckDir |> ignore
        try
            let defaultDescProj =
                sibling "FIDELITY_PLATFORM_DEFAULT_DESC_FIDPROJ" "Fidelity.Platform/Profiles/Linux_x86_64_Default/Fidelity.Platform.Description.fidproj"
            File.WriteAllText(
                Path.Combine(defaultPlatCheckDir, "DefaultPlatTest.fidproj"),
                $"[package]\nname = \"DefaultPlatTest\"\nversion = \"1.0.0\"\n[dependencies]\n\"description\" = {{ path = \"{defaultDescProj}\" }}\n")
            let isolatedLibClef = Path.Combine(defaultPlatCheckDir, "IsolatedLib.clef")
            let isolatedHarClef = Path.Combine(defaultPlatCheckDir, "IsolatedHar.clef")
            File.Copy(firstSrc, isolatedLibClef, true)
            File.WriteAllText(isolatedHarClef, "module IsolatedHar\n")
            let coexistRows =
                [ for prefix in [ "fs9220_layout_"; "fs9221_extent_"; "fs9222_lifetime_"; "fs9223_exclusivity_" ] do
                      yield prefix + firstSlug, $"cpu-linux-x86_64:{firstPlacementName}"
                  yield "fs9221_space_" + spaceSlug, $"cpu-linux-x86_64:{firstSpaceName}"
                  yield "fs9220_layout_hostsharedtileplacement", "cpu-linux-x86_64:hostSharedTilePlacement" ]
            let coexistObs, coexistQuery, coexistReceipt = buildSyntheticReceipt coexistRows
            match SubstantiationProof.inspectReceipt [| isolatedLibClef |] isolatedHarClef coexistObs coexistQuery coexistReceipt with
            | Ok summary ->
                Assert.Equal(6, summary.TotalObligations)
                Assert.Equal(5, summary.LibraryObligations)
                Assert.Equal(5, summary.LibraryDischargedUnsatCount)
                Assert.Equal(0, summary.HarnessObligations)
            | Error refusal -> failwith $"Expected default Fidelity.Platform coexistence receipt to succeed, got: {refusal.Text}"
        finally
            if Directory.Exists defaultPlatCheckDir then Directory.Delete(defaultPlatCheckDir, true)

        // 2. Verify published Composer witness unit artifacts and compiled probe LLVM IR (08_output.ll / module.ll)
        // dereference the mapped pointer directly via GEP/load/store with zero llvm.memcpy/memmove on region-derived pointers.
        let witnessUnitsText = File.ReadAllText manifest.WitnessUnits.Path
        Assert.Contains("_map", witnessUnitsText, StringComparison.Ordinal)
        Assert.Contains("_share_to_library", witnessUnitsText, StringComparison.Ordinal)
        Assert.Contains("_unshare_from_library", witnessUnitsText, StringComparison.Ordinal)
        Assert.Contains("_unmap", witnessUnitsText, StringComparison.Ordinal)
        let seamsWorkRoot = Path.Combine(Path.GetDirectoryName manifest.FidprojPath, "seams-work")
        Assert.True(Directory.Exists seamsWorkRoot, $"Expected seams-work directory at {seamsWorkRoot}")
        let mapCall = Regex(@"^\s*(%[-\w.$]+)\s*=\s*(?:tail\s+|musttail\s+|notail\s+)?call\b[^\n]*@farscape_shared_buffer_\w+_map\(", RegexOptions.Multiline)
        let derivation =
            Regex(@"^\s*(%[-\w.$]+)\s*=\s*(?:insertvalue|extractvalue|getelementptr|bitcast|addrspacecast|ptrtoint|inttoptr|phi|select|load)\b(.*)$", RegexOptions.Multiline)
        let storeToPtr =
            Regex(@"^\s*store\b([^\n]*),\s*ptr\s+(%[-\w.$]+)", RegexOptions.Multiline)
        let mentions (names: Set<string>) (text: string) =
            names |> Set.exists (fun n -> Regex.IsMatch(text, Regex.Escape n + @"(?![-\w.$])"))
        let assertZeroRegionCopyInRetainedIr (modules: (string * string) array) =
            Assert.NotEmpty(modules)
            for (llPath, llText) in modules do
                let seeds = mapCall.Matches llText |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
                let rec close (tainted: Set<string>) =
                    let fromInstructions =
                        derivation.Matches llText
                        |> Seq.filter (fun m -> mentions tainted m.Groups[2].Value)
                        |> Seq.map (fun m -> m.Groups[1].Value)
                        |> Set.ofSeq
                    let fromStores =
                        storeToPtr.Matches llText
                        |> Seq.filter (fun m -> mentions tainted m.Groups[1].Value)
                        |> Seq.map (fun m -> m.Groups[2].Value)
                        |> Set.ofSeq
                    let next = Set.unionMany [ tainted; fromInstructions; fromStores ]
                    if next.Count = tainted.Count then tainted else close next
                let regionPointers = close seeds
                let copiesOfRegion =
                    llText.Split('\n')
                    |> Array.filter (fun line -> line.Contains("@llvm.memcpy", StringComparison.Ordinal) || line.Contains("@llvm.memmove", StringComparison.Ordinal))
                    |> Array.filter (mentions regionPointers)
                let copiedLines = String.concat " | " copiesOfRegion
                Assert.True(copiesOfRegion.Length = 0, $"{llPath} copies a shared-buffer region pointer: {copiedLines}")
        let seededCopyIr =
            String.concat "\n"
                [ "define i32 @seeded_bad() {"
                  "  %slot = alloca ptr, align 8"
                  "  %r0 = call ptr @farscape_shared_buffer_Seeded_map()"
                  "  %desc = insertvalue { ptr, ptr } poison, ptr %r0, 0"
                  "  %p1 = extractvalue { ptr, ptr } %desc, 0"
                  "  store ptr %p1, ptr %slot, align 8"
                  "  %p2 = load ptr, ptr %slot, align 8"
                  "  %p3 = getelementptr i8, ptr %p2, i64 8"
                  "  call void @llvm.memcpy.p0.p0.i64(ptr %dst, ptr %p3, i64 24, i1 false)"
                  "  ret i32 0"
                  "}" ]
        let seededEx =
            Assert.ThrowsAny<Exception>(fun () ->
                assertZeroRegionCopyInRetainedIr [| "seeded-control.ll", seededCopyIr |])
        Assert.Contains("copies a shared-buffer region pointer", seededEx.Message, StringComparison.Ordinal)
        let candidateLlFiles =
            Array.append
                (Directory.GetFiles(manifest.ArtifactsDirectory, "*.ll", SearchOption.AllDirectories))
                (Directory.GetFiles(seamsWorkRoot, "*.ll", SearchOption.AllDirectories))
        let regionModules =
            candidateLlFiles
            |> Array.map (fun path -> path, File.ReadAllText path)
            |> Array.filter (fun (_, text) -> mapCall.IsMatch text)
        assertZeroRegionCopyInRetainedIr regionModules

        // 3. Verify cvc5 solver identity and discharged fs9220-fs9223 obligations per library placement and distinct space.
        Assert.Contains("cvc5", Path.GetFileName manifest.Proof.Solver.Executable, StringComparison.OrdinalIgnoreCase)
        let expectedSharedBufferLibCount =
            4 * expectedPlacements.Length
            + (expectedPlacements |> List.map snd |> List.distinct |> List.length)
        Assert.True(manifest.Proof.LibraryObligations >= expectedSharedBufferLibCount)
        Assert.Equal(manifest.Proof.LibraryObligations, manifest.Proof.LibraryDischargedUnsatCount)
        Assert.Equal(manifest.Proof.LibraryObligations, manifest.Proof.LibraryPremisesSatCount)
        let dischargePath = Path.Combine(manifest.ArtifactsDirectory, "06c_psg_discharge.json")
        let dischargeText = File.ReadAllText dischargePath
        let libGuarantees = reconciled.Dossier.Dossier.Guarantees.LibraryObligations
        let sharedBufferGuarantees =
            libGuarantees |> Array.filter (fun g -> g.Anchor.StartsWith("fs922", StringComparison.Ordinal))
        Assert.Equal(expectedSharedBufferLibCount, sharedBufferGuarantees.Length)
        for (placementName, spaceName) in expectedPlacements do
            let pSlug = SubstantiationProof.sharedBufferSlug placementName
            let sSlug = SubstantiationProof.sharedBufferSlug spaceName
            let primaryPlacementInSpace =
                expectedPlacements |> List.find (fun (_, sp) -> sp = spaceName) |> fst
            for prefix in [ "fs9220_layout_"; "fs9221_extent_"; "fs9222_lifetime_"; "fs9223_exclusivity_" ] do
                let expectedAnchor = prefix + pSlug
                Assert.Contains($"\"Anchor\": \"{expectedAnchor}\"", dischargeText.Replace("\":\"", "\": \""), StringComparison.Ordinal)
                Assert.Contains(sharedBufferGuarantees, fun g -> g.Anchor = expectedAnchor && g.Declaration.Contains(placementName, StringComparison.Ordinal))
            Assert.Contains(
                sharedBufferGuarantees,
                fun g ->
                    (g.Anchor = "fs9221_space_" + sSlug || g.Anchor.StartsWith("fs9221_space_" + sSlug + "_", StringComparison.Ordinal))
                    && g.Declaration.Contains(primaryPlacementInSpace, StringComparison.Ordinal))
        let expectedGuaranteeStrings =
            Set.ofList
                [ "Shared-memory struct layout, alignment, byte order, and pointer-free fields match the target ABI"
                  "Shared-memory region extent fits the declared memory space and all element accesses stay in bounds"
                  "Shared-memory region acquisition, synchronization, and release bracket all uses and library retention"
                  "Shared-memory access permissions exclude concurrent or read-only mutation conflicts" ]
        Assert.All(sharedBufferGuarantees, fun g -> Assert.Contains(g.Guarantee, expectedGuaranteeStrings))

    [<Fact>]
    let ``M10 SharedTensorTileBuffer HostShared DeviceUnified and FileMapped SpaceOffset 64L compile via Composer ld.lld LTO execute zero-copy and reconcile static link dossier`` () =
        let work = TestHelpers.TestScratch.create "m10-shared-tile-static-seam-lto"
        try
            let scalarHeader = Path.Combine(work, "shared_tile_receipts.h")
            writeLines scalarHeader
                [ "int shared_tile_open_backing_file(void);"
                  "int shared_tile_close_backing_file(int fd);"
                  "int shared_tile_host_zero_copy_receipt(void);"
                  "int device_tile_clef_write_zero_copy_receipt(void);" ]
            let header = Path.Combine(work, "shared_tile_static.h")
            writeLines header
                [ "#pragma once"
                  "#include <stdint.h>"
                  "#ifdef __cplusplus"
                  "extern \"C\" {"
                  "#endif"
                  "#include \"shared_tile_receipts.h\""
                  "#ifdef __cplusplus"
                  "}"
                  "#endif"
                  "struct SharedTensorTile {"
                  "    uint32_t sequence;"
                  "    int64_t accumulator;"
                  "    int32_t scale;"
                  "    uint32_t checksum;"
                  "};"
                  "struct DeviceTensorTile {"
                  "    uint32_t sequence;"
                  "    int64_t accumulator;"
                  "    int32_t scale;"
                  "    uint32_t checksum;"
                  "};"
                  "struct FileTensorTile {"
                  "    uint32_t sequence;"
                  "    int64_t accumulator;"
                  "    int32_t scale;"
                  "    uint32_t checksum;"
                  "};"
                  "#ifdef __cplusplus"
                  "extern \"C\" {"
                  "#endif"
                  "int shared_tile_library_transform_in_place(struct SharedTensorTile* base, int count, int index, int delta, uintptr_t* out_library_addr, int* inout_copy_count);"
                  "int device_tile_library_transform_in_place(struct DeviceTensorTile* base, int count, int index, int delta, uintptr_t* out_library_addr, int* inout_copy_count);"
                  "int shared_tile_unused_dead_symbol(int value);"
                  "#ifdef __cplusplus"
                  "}"
                  "#endif" ]

            let backingBinPath = Path.Combine(work, "file_tensor_tile_backing.bin")
            writeFileMappedBackingImage
                backingBinPath
                (Array.create 64 0xAAuy)
                [| 4242, 9001L, 15, (4242 ^^^ 9001)
                   0, 0L, 0, 0
                   0, 0L, 0, 0
                   8484, 7777L, 20, (8484 ^^^ 7777) |]
            let escapedBackingPath = backingBinPath.Replace("\\", "/")
            let implCpp = Path.Combine(work, "shared_tile_used.cpp")
            writeLines implCpp
                [ "#include \"shared_tile_static.h\""
                  "#include <fcntl.h>"
                  "#include <unistd.h>"
                  "static uintptr_t g_host_last_addr = 0;"
                  "static int g_host_last_copies = -1;"
                  "static uintptr_t g_device_last_addr = 0;"
                  "static int g_device_last_copies = -1;"
                  "static uint32_t g_device_observed_clef_seq = 0;"
                  $"extern \"C\" int shared_tile_open_backing_file(void) {{ return ::open(\"{escapedBackingPath}\", O_RDONLY); }}"
                  "extern \"C\" int shared_tile_close_backing_file(int fd) { return fd >= 0 ? ::close(fd) : -1; }"
                  "extern \"C\" int shared_tile_library_transform_in_place(SharedTensorTile* base, int count, int index, int delta, uintptr_t* out_library_addr, int* inout_copy_count) {"
                  "    if (!base || !out_library_addr || !inout_copy_count) return -1;"
                  "    if (index < 0 || index >= count) return 4;"
                  "    *out_library_addr = reinterpret_cast<uintptr_t>(base);"
                  "    *inout_copy_count = 0;"
                  "    SharedTensorTile& tile = base[index];"
                  "    tile.sequence += static_cast<uint32_t>(delta);"
                  "    tile.accumulator += static_cast<int64_t>(delta) * static_cast<int64_t>(tile.scale);"
                  "    tile.checksum = tile.sequence ^ static_cast<uint32_t>(tile.accumulator & 0xFFFFFFFFLL);"
                  "    g_host_last_addr = *out_library_addr;"
                  "    g_host_last_copies = *inout_copy_count;"
                  "    return 0;"
                  "}"
                  "extern \"C\" int shared_tile_host_zero_copy_receipt(void) {"
                  "    return (g_host_last_addr != 0 && (g_host_last_addr & 63u) == 0u && g_host_last_copies == 0) ? 1 : 0;"
                  "}"
                  "// DeviceUnified tests the Clef -> Library zero-copy FieldSet direction: Clef writes sequence"
                  "// in place, shares the region ReadOnly (mprotect PROT_READ), and the foreign library verifies the Clef-written"
                  "// field directly from the read-only shared mapping at the identical 64-byte-aligned address with 0 copies."
                  "extern \"C\" int device_tile_library_transform_in_place(DeviceTensorTile* base, int count, int index, int delta, uintptr_t* out_library_addr, int* inout_copy_count) {"
                  "    if (!base || !out_library_addr || !inout_copy_count) return -1;"
                  "    if (index < 0 || index >= count) return 4;"
                  "    *out_library_addr = reinterpret_cast<uintptr_t>(base);"
                  "    *inout_copy_count = 0;"
                  "    const DeviceTensorTile& tile = base[index];"
                  "    if (tile.sequence != static_cast<uint32_t>(delta)) return 6;"
                  "    g_device_last_addr = *out_library_addr;"
                  "    g_device_last_copies = *inout_copy_count;"
                  "    g_device_observed_clef_seq = tile.sequence;"
                  "    return 0;"
                  "}"
                  "extern \"C\" int device_tile_clef_write_zero_copy_receipt(void) {"
                  "    return (g_device_last_addr != 0 && (g_device_last_addr & 63u) == 0u && g_device_last_copies == 0 && g_device_observed_clef_seq == 100000u) ? 1 : 0;"
                  "}"
                  "#ifdef SHARED_TILE_STATIC_LTO"
                  "__attribute__((visibility(\"hidden\")))"
                  "#endif"
                  "extern \"C\" int shared_tile_unused_dead_symbol(int value) { return value + 73; }" ]

            let fields : CppClassAnalysis.SharedBufferFieldSpec list =
                [ { Name = "sequence"; CType = "uint32_t"; ClefType = "int"; ByteOffset = 0; ByteWidth = 4; IsVolatile = false }
                  { Name = "accumulator"; CType = "int64_t"; ClefType = "int"; ByteOffset = 8; ByteWidth = 8; IsVolatile = false }
                  { Name = "scale"; CType = "int32_t"; ClefType = "int"; ByteOffset = 16; ByteWidth = 4; IsVolatile = false }
                  { Name = "checksum"; CType = "uint32_t"; ClefType = "int"; ByteOffset = 20; ByteWidth = 4; IsVolatile = false } ]
            let hostLifecycle : CppClassAnalysis.SharedBufferOperation list =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 4)
                  CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
                  CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 3
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "accumulator")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 4)
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion
                  CppClassAnalysis.SharedBufferOperation.ScopeExit ]
            // DeviceUnified lifecycle exercises ClefWrite (FieldSet) -> ShareToLibrary(SharedReadOnly) -> foreign inspection without LibraryMutate (LibraryMutatesBuffer = false).
            let deviceLifecycle : CppClassAnalysis.SharedBufferOperation list =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
                  CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
                  CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 1
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion
                  CppClassAnalysis.SharedBufferOperation.ScopeExit ]
            let fileLifecycle : CppClassAnalysis.SharedBufferOperation list =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 4)
                  CppClassAnalysis.SharedBufferOperation.ClefRead (3, "accumulator")
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion
                  CppClassAnalysis.SharedBufferOperation.ScopeExit ]
            let baseHostSpec : CppClassAnalysis.SharedBufferSpec =
                { StructName = "SharedTensorTile"
                  SafeName = "SharedTensorTileBuffer"
                  Namespace = "SharedTileHost"
                  LibraryName = "sharedtile"
                  HeaderFile = header
                  MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.HostShared
                  SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadWrite
                  ByteOrder = "LittleEndian"
                  SpaceOffset = 0L
                  MeasuredSize = 24
                  MeasuredAlignment = 8
                  ElementCount = 4
                  RegionByteExtent = 96
                  RegionAlignment = 64
                  RegionLifetimeDepth = 1
                  LibraryRetentionDepth = 2
                  Lifecycle = hostLifecycle
                  VerifiedLibraryPath = None
                  Fields = fields
                  LibraryTransformSymbol = Some "shared_tile_library_transform_in_place"
                  Source = None }
            let baseDeviceSpec =
                { baseHostSpec with
                    StructName = "DeviceTensorTile"
                    SafeName = "DeviceTensorTileBuffer"
                    Namespace = "SharedTileDevice"
                    MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.DeviceUnified
                    Lifecycle = deviceLifecycle
                    LibraryTransformSymbol = Some "device_tile_library_transform_in_place" }
            let baseFileSpec =
                { baseHostSpec with
                    StructName = "FileTensorTile"
                    SafeName = "FileTensorTileBuffer"
                    Namespace = "SharedTileFile"
                    MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.FileMapped
                    SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadOnly
                    SpaceOffset = 64L
                    Lifecycle = fileLifecycle
                    LibraryTransformSymbol = None }

            let shimHostCpp = Path.Combine(work, "shim_host.cpp")
            let shimDeviceCpp = Path.Combine(work, "shim_device.cpp")
            let shimFileCpp = Path.Combine(work, "shim_file.cpp")
            writeUtf8 shimHostCpp (CppClassAnalysis.renderSharedBufferZeroCopyShimC baseHostSpec)
            writeUtf8 shimDeviceCpp (CppClassAnalysis.renderSharedBufferZeroCopyShimC baseDeviceSpec)
            writeUtf8 shimFileCpp (CppClassAnalysis.renderSharedBufferZeroCopyShimC baseFileSpec)

            let ltoTargetFlag =
                match System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture with
                | System.Runtime.InteropServices.Architecture.X64 -> "--target=x86_64-unknown-linux-gnu"
                | System.Runtime.InteropServices.Architecture.Arm64 -> "--target=aarch64-unknown-linux-gnu"
                | arch -> failwith ("Unsupported architecture for LTO target triple check: " + string arch)
            let archivePath = Path.Combine(work, "libsharedtile.a")
            let sharedPath = Path.Combine(work, "libsharedtile.so")
            let ltoObjs =
                [ "used", implCpp; "host", shimHostCpp; "device", shimDeviceCpp; "file", shimFileCpp ]
                |> List.map (fun (tag, src) ->
                    let obj = Path.Combine(work, $"shared_tile_{tag}.lto.o")
                    run work $"cc-{tag}-lto" (ClangToolchain.startInfo [ "-x"; "c++"; "-std=c++17"; "-fno-exceptions"; "-fno-rtti"; ltoTargetFlag; "-O2"; "-flto"; "-DSHARED_TILE_STATIC_LTO"; "-c"; src; "-o"; obj ]) |> ignore
                    obj)
            run work "ar-sharedtile" (ClangToolchain.arStartInfo ("rcs" :: archivePath :: ltoObjs)) |> ignore
            let picObjs =
                [ "used", implCpp; "host", shimHostCpp; "device", shimDeviceCpp; "file", shimFileCpp ]
                |> List.map (fun (tag, src) ->
                    let obj = Path.Combine(work, $"shared_tile_{tag}.pic.o")
                    run work $"cc-{tag}-pic" (ClangToolchain.startInfo [ "-x"; "c++"; "-std=c++17"; "-fno-exceptions"; "-fno-rtti"; "-O2"; "-fPIC"; "-c"; src; "-o"; obj ]) |> ignore
                    obj)
            run work "ld-sharedtile-so" (ClangToolchain.startInfo ([ "-shared"; "-fPIC"; "-Wl,-soname,libsharedtile.so.0" ] @ picObjs @ [ "-o"; sharedPath ])) |> ignore

            let archiveSha = ClangToolchain.sha256 archivePath
            let sharedSha = ClangToolchain.sha256 sharedPath
            let receiptsPilot = Path.Combine(work, "SharedTileReceipts.pilot.toml")
            writeLines receiptsPilot
                [ "[library]"
                  "name = \"sharedtile\""
                  "headers = [" + quote scalarHeader + "]"
                  "include_paths = [" + quote work + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote archiveSha
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"generated\""
                  "[error_conventions]"
                  "default = \"none\""
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "[[namespace]]"
                  "name = \"Fidelity.SharedTile.Receipts\""
                  "library = \"sharedtile\""
                  "description = \"M10 zero-copy address and copy-count receipt queries\""
                  "functions = [\"shared_tile_open_backing_file\", \"shared_tile_close_backing_file\", \"shared_tile_host_zero_copy_receipt\", \"device_tile_clef_write_zero_copy_receipt\"]" ]

            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed receiptsPilot false false Types.LP64)
            |> accepted |> ignore

            let measuredLayouts =
                match CppParser.extractStructLayouts header [] [] [ "SharedTensorTile"; "DeviceTensorTile"; "FileTensorTile" ] false with
                | Ok layouts -> layouts
                | Error err -> failwith $"Failed to extract clang layouts for SharedTensorTile suite: {err}"
            let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 measuredLayouts
            let hostSpec = { baseHostSpec with VerifiedLibraryPath = Some archivePath }
            let deviceSpec = { baseDeviceSpec with VerifiedLibraryPath = Some archivePath }
            let fileSpec = { baseFileSpec with VerifiedLibraryPath = Some archivePath }

            let genDir = Path.Combine(work, "generated")
            Directory.CreateDirectory genDir |> ignore
            let hostClef = Path.Combine(genDir, "HostSharedBuffers.clef")
            let deviceClef = Path.Combine(genDir, "DeviceSharedBuffers.clef")
            let fileClef = Path.Combine(genDir, "FileSharedBuffers.clef")
            writeUtf8 hostClef (FidelityCodeGenerator.generateSharedBufferModule ctx hostSpec)
            writeUtf8 deviceClef (FidelityCodeGenerator.generateSharedBufferModule ctx deviceSpec)
            writeUtf8 fileClef (FidelityCodeGenerator.generateSharedBufferModule ctx fileSpec)
            let sharedBufferSources = [ hostClef; deviceClef; fileClef ]
            let generatedSources =
                [ Path.Combine(genDir, "Types.clef")
                  Path.Combine(genDir, "Receipts", "Types.clef")
                  Path.Combine(genDir, "Receipts", "Receipts.clef")
                  hostClef
                  deviceClef
                  fileClef ]
                |> List.filter File.Exists

            let pilot = Path.Combine(work, "SharedTileStatic.pilot.toml")
            writeLines pilot
                [ "[library]"
                  "name = \"sharedtile\""
                  "headers = [" + quote header + "]"
                  "include_paths = [" + quote work + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote archiveSha
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"generated\""
                  "[error_conventions]"
                  "default = \"none\""
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "abi_critical_structs = [\"struct SharedTensorTile\", \"struct DeviceTensorTile\", \"struct FileTensorTile\"]"
                  "[[namespace]]"
                  "name = \"Fidelity.SharedTile.Receipts\""
                  "library = \"sharedtile\""
                  "description = \"M10 zero-copy shared buffer static archive seam\""
                  "functions = [\"shared_tile_open_backing_file\", \"shared_tile_close_backing_file\", \"shared_tile_host_zero_copy_receipt\", \"device_tile_clef_write_zero_copy_receipt\"]" ]

            let requiredSymbols =
                ([ "shared_tile_open_backing_file"; "shared_tile_close_backing_file"; "shared_tile_host_zero_copy_receipt"; "device_tile_clef_write_zero_copy_receipt" ]
                 @ ([ hostSpec; deviceSpec; fileSpec ] |> List.collect CppClassAnalysis.sharedBufferExportedSymbols))
                |> List.distinct
                |> List.sort
            let archiveProvenance = ArchiveInspection.inspect archivePath (Some archiveSha) |> accepted
            let resolvedBindings = ArchiveInspection.verifyRequiredSymbols archiveProvenance requiredSymbols |> accepted
            let provenance = Path.Combine(work, "SharedTileStatic.link-provenance.toml")
            writeUtf8 provenance (ArchiveInspection.renderLinkProvenanceToml "sharedtile" archiveProvenance resolvedBindings [])

            let loading = Path.Combine(work, "Loading.clef")
            let loadingTemplate = Path.Combine(__SOURCE_DIRECTORY__, "..", "native-callbacks", "Loading.clef")
            writeUtf8 loading
                (File.ReadAllText(loadingTemplate)
                    .Replace("landingprobe", "sharedtile")
                    .Replace("tests/native-callbacks/probe.c", scalarHeader)
                    .Replace("prebuilt probe archive", "M10 shared-buffer static archive"))

            let caller = Path.Combine(work, "Caller.clef")
            writeLines caller
                [ "module SharedTileCaller"
                  "open Fidelity.SharedTile.Receipts"
                  "open SharedTileHost"
                  "open SharedTileDevice"
                  "open SharedTileFile"
                  "let sampleHost () : int ="
                  "    let tile = SharedTensorTileBuffer_map ()"
                  "    let before = tile.sequence"
                  "    let _ = SharedTensorTileBuffer_share_to_library tile 3 4"
                  "    let _ = SharedTensorTileBuffer_library_transform_in_place tile 0 7"
                  "    let _ = SharedTensorTileBuffer_unshare_from_library tile"
                  "    let after = tile.sequence"
                  "    let _ = SharedTensorTileBuffer_unmap tile"
                  "    let receipt = shared_tile_host_zero_copy_receipt ()"
                  "    if after - before = 7 && receipt = 1 then"
                  "        Console.writeln \"sharedtile:caller:host:ok\""
                  "        1"
                  "    else 0"
                  "let sampleDevice () : int ="
                  "    let tile = DeviceTensorTileBuffer_map ()"
                  "    let before = tile.sequence"
                  "    tile.sequence <- 100000"
                  "    let _ = DeviceTensorTileBuffer_share_to_library tile 1 2"
                  "    let _ = DeviceTensorTileBuffer_library_transform_in_place tile 0 100000"
                  "    let _ = DeviceTensorTileBuffer_unshare_from_library tile"
                  "    let after = tile.sequence"
                  "    let _ = DeviceTensorTileBuffer_unmap tile"
                  "    let receipt = device_tile_clef_write_zero_copy_receipt ()"
                  "    if after - before = 100000 && receipt = 1 then 1 else 0"
                  "let sampleFile () : int ="
                  "    let fd = shared_tile_open_backing_file ()"
                  "    if fd < 0 then 0"
                  "    else"
                  "        let tile = FileTensorTileBuffer_map fd"
                  "        let seq0 = tile.sequence"
                  "        let _ = FileTensorTileBuffer_share_to_library tile 1 4"
                  "        let acc0 = tile.accumulator"
                  "        let _ = FileTensorTileBuffer_unshare_from_library tile"
                  "        let _ = FileTensorTileBuffer_unmap tile"
                  "        let c = shared_tile_close_backing_file fd"
                  "        if c = 0 && seq0 = 4242 && acc0 = 9001 then 1 else 0" ]

            let dependencies, platform =
                writeZeroCopyPlatformTables work
                    [ "sharedTensorTileHost", "SharedTensorTile", "Fidelity.Platform.HostShared", "Access.ReadWrite"
                      "sharedTensorTileDevice", "DeviceTensorTile", "Fidelity.Platform.DeviceUnified", "Access.ReadWrite"
                      "sharedTensorTileFile", "FileTensorTile", "Fidelity.Platform.FileMapped", "Access.ReadOnly" ]
            let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
            let fidproj = Path.Combine(work, "SharedTileStatic.fidproj")
            writeLines fidproj
                [ "[package]"
                  "name = \"SharedTileStatic\""
                  "version = \"1.0.0\""
                  "[compilation]"
                  "target = \"cpu\""
                  "[build]"
                  "output_kind = \"library\""
                  "sources = [" + (([ loading ] @ generatedSources @ [ caller ]) |> List.map quote |> String.concat ", ") + "]"
                  "[dependencies]"
                  yield! dependencies
                  "metadata = { path = " + quote metadata + " }"
                  "[platform]"
                  yield! platform
                  "loading = \"Landing.Acceptance.Loading.contract\""
                  "[link]"
                  "libraries = [\"sharedtile\"]" ]

            let manifestDir = Path.Combine(work, "manifest")
            let manifest =
                Substantiation.evaluate
                    { FidprojPath = fidproj
                      PilotPath = Some pilot
                      LinkProvenancePath = Some provenance
                      OutputDirectory = Some manifestDir
                      WorkDirectory = Some(Path.Combine(work, "substantiation"))
                      AllowWarnings = false
                      Verbose = false }
                |> accepted
            Assert.Equal("static-archive", manifest.Boundary.LinkMode)
            Assert.True manifest.Boundary.Strata.ZeroCopy
            Assert.True manifest.Boundary.Strata.MeasuredRecords
            Assert.True manifest.Boundary.Strata.StaticArchive
            Assert.Contains("barewire-zero-copy", manifest.Boundary.RequiredSeamChecks)
            Assert.Contains("measured-record-abi", manifest.Boundary.RequiredSeamChecks)
            Assert.Contains("static-dynamic-link-elision", manifest.Boundary.RequiredSeamChecks)
            Assert.Equal(manifest.Proof.TotalObligations, manifest.Proof.DischargedUnsatCount)
            Assert.Equal(0, manifest.Proof.PendingOpenCount)
            let manifestJson = Path.Combine(manifestDir, "SharedTileStatic.substantiation.json")

            let probesDir = Path.Combine(work, "probes")
            Directory.CreateDirectory probesDir |> ignore
            let probeHost = Path.Combine(probesDir, "ProbeSharedTileHost.clef")
            writeLines probeHost
                [ "module ProbeSharedTileHost"
                  "open Fidelity.SharedTile.Receipts"
                  "open SharedTileHost"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let tile = SharedTensorTileBuffer_map ()"
                  "    let before = tile.sequence"
                  "    let _ = SharedTensorTileBuffer_share_to_library tile 3 4"
                  "    let _ = SharedTensorTileBuffer_library_transform_in_place tile 0 7"
                  "    let _ = SharedTensorTileBuffer_unshare_from_library tile"
                  "    let after = tile.sequence"
                  "    let _ = SharedTensorTileBuffer_unmap tile"
                  "    let receipt = shared_tile_host_zero_copy_receipt ()"
                  "    if after - before = 7 && receipt = 1 then"
                  "        Console.writeln \"sharedtile:host:zero-copy:ok\""
                  "        0"
                  "    else 1" ]
            let probeDevice = Path.Combine(probesDir, "ProbeSharedTileDevice.clef")
            writeLines probeDevice
                [ "module ProbeSharedTileDevice"
                  "open Fidelity.SharedTile.Receipts"
                  "open SharedTileDevice"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let tile = DeviceTensorTileBuffer_map ()"
                  "    let before = tile.sequence"
                  "    tile.sequence <- 100000"
                  "    let _ = DeviceTensorTileBuffer_share_to_library tile 1 2"
                  "    let _ = DeviceTensorTileBuffer_library_transform_in_place tile 0 100000"
                  "    let _ = DeviceTensorTileBuffer_unshare_from_library tile"
                  "    let after = tile.sequence"
                  "    let _ = DeviceTensorTileBuffer_unmap tile"
                  "    let receipt = device_tile_clef_write_zero_copy_receipt ()"
                  "    if after - before = 100000 && receipt = 1 then"
                  "        Console.writeln \"sharedtile:device:zero-copy:ok\""
                  "        0"
                  "    else 2" ]
            let probeFile = Path.Combine(probesDir, "ProbeSharedTileFile.clef")
            writeLines probeFile
                [ "module ProbeSharedTileFile"
                  "open Fidelity.SharedTile.Receipts"
                  "open SharedTileFile"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let fd = shared_tile_open_backing_file ()"
                  "    if fd < 0 then 3"
                  "    else"
                  "        let tile = FileTensorTileBuffer_map fd"
                  "        let seq0 = tile.sequence"
                  "        let _ = FileTensorTileBuffer_share_to_library tile 1 4"
                  "        let acc0 = tile.accumulator"
                  "        let scale0 = tile.scale"
                  "        let _ = FileTensorTileBuffer_unshare_from_library tile"
                  "        let _ = FileTensorTileBuffer_unmap tile"
                  "        let c = shared_tile_close_backing_file fd"
                  "        if c = 0 && seq0 = 4242 && acc0 = 9001 && scale0 = 15 then"
                  "            Console.writeln \"sharedtile:file:offset64:ok\""
                  "            0"
                  "        else 3" ]
            let probeScalar = Path.Combine(probesDir, "ProbeSharedTileScalar.clef")
            writeLines probeScalar
                [ "module ProbeSharedTileScalar"
                  "open SharedTileHost"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let tile = SharedTensorTileBuffer_map ()"
                  "    let v = tile.sequence"
                  "    let _ = SharedTensorTileBuffer_unmap tile"
                  "    if v = 0 then"
                  "        Console.writeln \"sharedtile:scalar:ok\""
                  "        0"
                  "    else 4" ]
            let probeElision = Path.Combine(probesDir, "ProbeSharedTileElision.clef")
            writeLines probeElision
                [ "module ProbeSharedTileElision"
                  "open Fidelity.SharedTile.Receipts"
                  "open SharedTileHost"
                  "open SharedTileDevice"
                  "open SharedTileFile"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let h = SharedTensorTileBuffer_map ()"
                  "    let hv = h.sequence"
                  "    let _ = SharedTensorTileBuffer_unmap h"
                  "    let d = DeviceTensorTileBuffer_map ()"
                  "    let dv = d.sequence"
                  "    let _ = DeviceTensorTileBuffer_unmap d"
                  "    let fd = shared_tile_open_backing_file ()"
                  "    if fd < 0 then 5"
                  "    else"
                  "        let f = FileTensorTileBuffer_map fd"
                  "        let fv = f.sequence"
                  "        let _ = FileTensorTileBuffer_unmap f"
                  "        let cf = shared_tile_close_backing_file fd"
                  "        if cf = 0 && hv = 0 && dv = 0 && fv = 4242 then"
                  "            Console.writeln \"sharedtile:elision:ok\""
                  "            0"
                  "        else 5" ]
            let dynamicProbeC = Path.Combine(probesDir, "shared_tile_dyn_probe.c")
            writeLines dynamicProbeC
                [ "#include " + quote header
                  "void *farscape_shared_buffer_SharedTensorTileBuffer_map(void);"
                  "int farscape_shared_buffer_SharedTensorTileBuffer_unmap(void *self);"
                  "int main(void) {"
                  "    void *p = farscape_shared_buffer_SharedTensorTileBuffer_map();"
                  "    return (p != 0 && farscape_shared_buffer_SharedTensorTileBuffer_unmap(p) == 0) ? 0 : 1;"
                  "}" ]

            let controlsDir = Path.Combine(work, "controls")
            Directory.CreateDirectory controlsDir |> ignore
            let unusedMain = Path.Combine(controlsDir, "UnusedMain.clef")
            writeLines unusedMain
                [ "module RefusalUnusedMain"
                  "open SharedTileHost"
                  "[<EntryPoint>]"
                  "let main _ : int = 0" ]
            let bad9220Clef = Path.Combine(controlsDir, "Bad9220.clef")
            writeUtf8 bad9220Clef (File.ReadAllText(hostClef).Replace("HasRawPointerFields = false", "HasRawPointerFields = true"))
            let bad9221Clef = Path.Combine(controlsDir, "Bad9221.clef")
            writeUtf8 bad9221Clef (File.ReadAllText(hostClef).Replace("MaxAccessedElementIndex = 3", "MaxAccessedElementIndex = 4"))
            let useAfterUnmapClef = Path.Combine(controlsDir, "UseAfterUnmap9222.clef")
            writeLines useAfterUnmapClef
                [ "module RefusalUseAfterUnmap"
                  "open SharedTileHost"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let tile = SharedTensorTileBuffer_map ()"
                  "    let _ = SharedTensorTileBuffer_share_to_library tile 3 4"
                  "    let _ = SharedTensorTileBuffer_library_transform_in_place tile 0 5"
                  "    let _ = SharedTensorTileBuffer_unshare_from_library tile"
                  "    let _ = SharedTensorTileBuffer_unmap tile"
                  "    let after = tile.sequence"
                  "    if after = 5 then 0 else 1" ]
            // Program-driven FS9223 refusal: consumer executes a direct FieldSet (tile.sequence <- 100000) inside the active
            // mutable library share window on SharedTensorTileBuffer (where LibraryMutatesBuffer = true and ConcurrentClefMutation = false).
            let writeConflict9223Clef = Path.Combine(controlsDir, "WriteConflict9223.clef")
            writeLines writeConflict9223Clef
                [ "module RefusalWriteConflict9223"
                  "open SharedTileHost"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let tile = SharedTensorTileBuffer_map ()"
                  "    let _ = SharedTensorTileBuffer_share_to_library tile 3 4"
                  "    let _ = SharedTensorTileBuffer_library_transform_in_place tile 0 5"
                  "    tile.sequence <- 100000"
                  "    let _ = SharedTensorTileBuffer_unshare_from_library tile"
                  "    let after = tile.sequence"
                  "    let _ = SharedTensorTileBuffer_unmap tile"
                  "    if after = 100000 then 0 else 1" ]
            let rawAddressClef = Path.Combine(controlsDir, "RawAddressSharedTile8401.clef")
            writeLines rawAddressClef
                [ "module RefusalSharedTileRawAddress"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let mutable cell = 17"
                  "    let rawAddr = &&cell"
                  "    if rawAddr = rawAddr then 0 else 1" ]
            let writeControlFidproj name sourcesList =
                let projPath = Path.Combine(controlsDir, name + ".fidproj")
                writeLines projPath
                    [ "[package]"
                      "name = " + quote name
                      "version = \"1.0.0\""
                      "[compilation]"
                      "target = \"cpu\""
                      "[build]"
                      "output_kind = \"console\""
                      "sources = [" + (sourcesList |> List.map quote |> String.concat ", ") + "]"
                      "[dependencies]"
                      yield! dependencies
                      "metadata = { path = " + quote metadata + " }"
                      "[platform]"
                      yield! platform
                      "loading = \"Landing.Acceptance.Loading.contract\""
                      "[link]"
                      "libraries = [\"sharedtile\"]"
                      "archives = { sharedtile = " + quote archivePath + " }" ]
                projPath
            let refusal9220Proj = writeControlFidproj "RefusalSharedTile9220" [ loading; bad9220Clef; unusedMain ]
            let refusal9221Proj = writeControlFidproj "RefusalSharedTile9221" [ loading; bad9221Clef; unusedMain ]
            let refusal9222Proj = writeControlFidproj "RefusalSharedTile9222" [ loading; hostClef; useAfterUnmapClef ]
            let refusal9223Proj = writeControlFidproj "RefusalSharedTile9223" [ loading; hostClef; writeConflict9223Clef ]
            let refusal8401Proj = writeControlFidproj "RefusalSharedTile8401" [ loading; rawAddressClef ]

            let spec = Path.Combine(work, "SharedTileStatic.seams.toml")
            writeLines spec
                [ "schema_version = 1"
                  "target_triple = " + quote manifest.Boundary.ClangTarget
                  ""
                  "[[checks]]"
                  "id = \"sharedtile-host-zero-copy\""
                  "category = \"barewire-zero-copy\""
                  "summary = \"HostShared SharedTensorTileBuffer zero-copy FieldGet projection after in-place foreign mutation with address and copy-count receipt.\""
                  "entry_source = " + quote probeHost
                  "required_symbols = [\"shared_tile_host_zero_copy_receipt\", \"farscape_shared_buffer_SharedTensorTileBuffer_map\", \"farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place\", \"farscape_shared_buffer_SharedTensorTileBuffer_unmap\"]"
                  "expected_stdout = \"sharedtile:host:zero-copy:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sharedtile-device-zero-copy\""
                  "category = \"measured-record-abi\""
                  "summary = \"DeviceUnified DeviceTensorTileBuffer zero-copy FieldSet write projection observed in-place by foreign library under PROT_READ sharing.\""
                  "entry_source = " + quote probeDevice
                  "required_symbols = [\"device_tile_clef_write_zero_copy_receipt\", \"farscape_shared_buffer_DeviceTensorTileBuffer_map\", \"farscape_shared_buffer_DeviceTensorTileBuffer_library_transform_in_place\", \"farscape_shared_buffer_DeviceTensorTileBuffer_unmap\"]"
                  "expected_stdout = \"sharedtile:device:zero-copy:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sharedtile-file-offset64\""
                  "category = \"owned-handle-lifecycle\""
                  "summary = \"FileMapped FileTensorTileBuffer at SpaceOffset = 64L maps fd 42, projects fields in-place, and unmaps.\""
                  "entry_source = " + quote probeFile
                  "required_symbols = [\"farscape_shared_buffer_FileTensorTileBuffer_map\", \"farscape_shared_buffer_FileTensorTileBuffer_share_to_library\", \"farscape_shared_buffer_FileTensorTileBuffer_unmap\"]"
                  "expected_stdout = \"sharedtile:file:offset64:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sharedtile-scalar-contract\""
                  "category = \"scalar-handle-link-and-call\""
                  "summary = \"SharedTensorTileBuffer map and unmap symbols link and verify initial mapped region state.\""
                  "entry_source = " + quote probeScalar
                  "required_symbols = [\"farscape_shared_buffer_SharedTensorTileBuffer_map\", \"farscape_shared_buffer_SharedTensorTileBuffer_unmap\"]"
                  "expected_stdout = \"sharedtile:scalar:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sharedtile-link-elision\""
                  "category = \"static-dynamic-link-elision\""
                  "summary = \"Composer opt + ld.lld LTO elides hidden dead symbol while retaining all three shared-buffer spaces.\""
                  "entry_source = " + quote probeElision
                  "required_symbols = [\"farscape_shared_buffer_SharedTensorTileBuffer_map\", \"farscape_shared_buffer_DeviceTensorTileBuffer_map\", \"farscape_shared_buffer_FileTensorTileBuffer_map\"]"
                  "expected_stdout = \"sharedtile:elision:ok\""
                  "[checks.elf]"
                  "elided_symbols = [\"shared_tile_unused_dead_symbol\"]"
                  "retained_static_symbols = [\"farscape_shared_buffer_SharedTensorTileBuffer_map\", \"farscape_shared_buffer_DeviceTensorTileBuffer_map\", \"farscape_shared_buffer_FileTensorTileBuffer_map\"]"
                  "forbidden_needed_libraries = [\"libsharedtile.so.0\"]"
                  "shared_library = " + quote sharedPath
                  "shared_library_sha256 = " + quote sharedSha
                  "dynamic_probe_source = " + quote dynamicProbeC
                  "retained_dynamic_imports = [\"farscape_shared_buffer_SharedTensorTileBuffer_map\", \"farscape_shared_buffer_SharedTensorTileBuffer_unmap\"]"
                  ""
                  "[[refusal_controls]]"
                  "id = \"sharedtile-refusal-fs9220-layout\""
                  "kind = \"composer\""
                  "target = " + quote refusal9220Proj
                  "expected_code = \"FS9220\""
                  "misuse_summary = \"SharedBufferPlacement declares HasRawPointerFields = true.\""
                  "protection_summary = \"Composer and clef refuse pointer-bearing shared-buffer layout with FS9220.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sharedtile-refusal-fs9221-extent\""
                  "kind = \"composer\""
                  "target = " + quote refusal9221Proj
                  "expected_code = \"FS9221\""
                  "misuse_summary = \"SharedBufferPlacement declares MaxAccessedElementIndex = 4 on ElementCount = 4.\""
                  "protection_summary = \"Composer and clef refuse out-of-range shared-buffer extent with FS9221.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sharedtile-refusal-fs9222-lifetime\""
                  "kind = \"composer\""
                  "target = " + quote refusal9222Proj
                  "expected_code = \"FS9222\""
                  "misuse_summary = \"Clef projects tile.sequence after SharedTensorTileBuffer_unmap releases the region.\""
                  "protection_summary = \"Composer and clef refuse use-after-unmap shared-buffer projection with FS9222.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sharedtile-refusal-fs9223-exclusivity\""
                  "kind = \"composer\""
                  "target = " + quote refusal9223Proj
                  "expected_code = \"FS9223\""
                  "misuse_summary = \"Clef mutates tile.sequence via FieldSet on a placement where LibraryMutatesBuffer = true and ConcurrentClefMutation = false.\""
                  "protection_summary = \"Composer and clef refuse conflicting Clef and foreign writers with FS9223.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sharedtile-refusal-ccs8401-raw-address\""
                  "kind = \"composer\""
                  "target = " + quote refusal8401Proj
                  "expected_code = \"CCS8401\""
                  "misuse_summary = \"Consumer module takes a raw native address (&&cell) outside BAREWire shared-buffer projection.\""
                  "protection_summary = \"Composer and clef refuse raw address-of in native consumer code with CCS8401.\"" ]

            let reconciled =
                SubstantiationDossier.reconcileStaticLinkDossier
                    { ManifestPath = manifestJson
                      SpecPath = spec
                      SeamsOutputDirectory = Some(Path.Combine(work, "seams-out"))
                      SeamsWorkDirectory = Some(Path.Combine(work, "seams-work"))
                      DossierOutputDirectory = Some(Path.Combine(work, "dossier-out"))
                      Verbose = false }
                |> accepted
            Assert.Equal(5, reconciled.Seams.SeamChecks.Checks.Length)
            Assert.Equal(5, reconciled.Seams.RefusalControls.Controls.Length)
            Assert.All(reconciled.Seams.RefusalControls.Controls, fun c -> Assert.Equal(c.ExpectedCode, c.ObservedCode))
            Assert.StartsWith("SUBSTANTIATED --", reconciled.Dossier.Dossier.OverallStatus)
            Assert.Equal(0, reconciled.Dossier.Dossier.Counts.Open)
            Assert.Equal(0, reconciled.Dossier.Dossier.Counts.PendingSeamCategories)
            Assert.Equal(manifest.Proof.DischargedUnsatCount, reconciled.Dossier.Dossier.Counts.Guarantees)
            assertSharedBufferProofAndDossier
                manifest
                reconciled
                [ "SharedTensorTileBuffer", "Fidelity.Platform.HostShared"
                  "DeviceTensorTileBuffer", "Fidelity.Platform.DeviceUnified"
                  "FileTensorTileBuffer", "Fidelity.Platform.FileMapped" ]
                sharedBufferSources
            let ltoRetained =
                reconciled.Seams.Ledger.RetainedBinaries
                |> Array.find (fun item -> item.CheckId = "sharedtile-link-elision")
            let ltoElf = SubstantiationSeams.inspectElfExecutable ltoRetained.BinaryPath |> accepted
            Assert.Contains("farscape_shared_buffer_SharedTensorTileBuffer_map", ltoElf.DefinedSymtab)
            Assert.Contains("farscape_shared_buffer_DeviceTensorTileBuffer_map", ltoElf.DefinedSymtab)
            Assert.Contains("farscape_shared_buffer_FileTensorTileBuffer_map", ltoElf.DefinedSymtab)
            Assert.DoesNotContain("shared_tile_unused_dead_symbol", ltoElf.DefinedSymtab)
        finally
            Directory.Delete(work, true)

    [<Fact>]
    let ``M10 SQLite zero-copy mmap and WAL shared-memory probes compile via Composer ld.lld LTO against libsqlite3.a and reconcile static link dossier`` () =
        let work = TestHelpers.TestScratch.create "m10-sqlite-zero-copy-mmap-lto"
        try
            let sqliteAmalgamationCandidates =
                [ Environment.GetEnvironmentVariable "SQLITE_AMALGAMATION_C"
                  "/home/hhh/repos/twelve-dollar-server-challenge/submissions/c-iouring-le0nvfx/src/vendor/sqlite-3.54.1/sqlite3.c" ]
                |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p) && File.Exists p)
            Assert.NotEmpty(sqliteAmalgamationCandidates)
            let sqliteAmalgamationC = Path.GetFullPath sqliteAmalgamationCandidates.Head
            let sqliteAmalgamationDir = Path.GetDirectoryName sqliteAmalgamationC
            let sqliteAmalgamationH = Path.Combine(sqliteAmalgamationDir, "sqlite3.h")
            Assert.True(File.Exists sqliteAmalgamationH, $"Expected sqlite3.h alongside {sqliteAmalgamationC}")

            let liveDbPath = Path.Combine(work, "live_wal.db")
            let dbShmPath = Path.Combine(work, "live_wal.db-shm")
            let escapedLiveDbPath = liveDbPath.Replace("\\", "/")
            let escapedDbShmPath = dbShmPath.Replace("\\", "/")

            let header = Path.Combine(work, "sqlite3_mmap_static.h")
            writeLines header
                [ "int sqlite3_libversion_number(void);"
                  "int sqlite3_threadsafe(void);"
                  "int sqlite3_wal_open_shm_fd(void);"
                  "int sqlite3_wal_close_shm_fd(int fd);"
                  "int sqlite3_mmap_open_db_fd(void);"
                  "int sqlite3_mmap_close_db_fd(int fd);"
                  "int sqlite3_wal_ckpt_clef_write_receipt(void);"
                  "int sqlite3_mmap_frame_socket_receipt(int wal_ver, int wal_mxf, int db_app_id, int db_ver);" ]
            let buffersHeader = Path.Combine(work, "sqlite3_mmap_buffers.h")
            writeLines buffersHeader
                [ "#pragma once"
                  "#include <stdint.h>"
                  "#ifdef __cplusplus"
                  "extern \"C\" {"
                  "#endif"
                  "#include \"sqlite3_mmap_static.h\""
                  "#ifdef __cplusplus"
                  "}"
                  "#endif"
                  "// Exact 48-byte SQLite WAL-index header layout (sqlite3.c WalIndexHdr, lines 67193-67205)."
                  "// Stored twice at bytes 0..47 and 48..95 of the -shm file in native host byte order."
                  "struct SqliteWalIndexHdr {"
                  "    uint32_t iVersion;"
                  "    uint32_t unused;"
                  "    uint32_t iChange;"
                  "    uint8_t isInit;"
                  "    uint8_t bigEndCksum;"
                  "    uint16_t szPage;"
                  "    uint32_t mxFrame;"
                  "    uint32_t nPage;"
                  "    uint32_t aFrameCksum0;"
                  "    uint32_t aFrameCksum1;"
                  "    uint32_t aSalt0;"
                  "    uint32_t aSalt1;"
                  "    uint32_t aCksum0;"
                  "    uint32_t aCksum1;"
                  "};"
                  "// Exact 40-byte SQLite WAL checkpoint info layout (sqlite3.c WalCkptInfo, lines 67266-67272)."
                  "struct SqliteWalCkptInfo {"
                  "    uint32_t nBackfill;"
                  "    uint32_t aReadMark0;"
                  "    uint32_t aReadMark1;"
                  "    uint32_t aReadMark2;"
                  "    uint32_t aReadMark3;"
                  "    uint32_t aReadMark4;"
                  "    uint8_t aLock0;"
                  "    uint8_t aLock1;"
                  "    uint8_t aLock2;"
                  "    uint8_t aLock3;"
                  "    uint8_t aLock4;"
                  "    uint8_t aLock5;"
                  "    uint8_t aLock6;"
                  "    uint8_t aLock7;"
                  "    uint32_t nBackfillAttempted;"
                  "    uint32_t notUsed0;"
                  "};"
                  "// Exact 48-byte slice at SpaceOffset = 64L of the checkpointed SQLite database file (live_wal.db):"
                  "// spans database header bytes 64..99 (incrementalVacuumMode, applicationId, reservedExpansion0..4,"
                  "// versionValidFor, sqliteVersionNumber) and page-1 B-tree header bytes 100..111 (leaf table flag 0x0D,"
                  "// freeblock offset, cell count = 1, cell content start, and first cell pointer)."
                  "struct SqliteMmapDbPage1Slice64 {"
                  "    uint32_t incrementalVacuumMode;"
                  "    uint32_t applicationId;"
                  "    uint32_t reservedExpansion0;"
                  "    uint32_t reservedExpansion1;"
                  "    uint32_t reservedExpansion2;"
                  "    uint32_t reservedExpansion3;"
                  "    uint32_t reservedExpansion4;"
                  "    uint32_t versionValidFor;"
                  "    uint32_t sqliteVersionNumber;"
                  "    uint8_t btreePageFlags;"
                  "    uint8_t freeblockHi;"
                  "    uint16_t freeblockLoAndCellsHi;"
                  "    uint8_t cellCountLo;"
                  "    uint8_t cellContentStartHi;"
                  "    uint16_t cellContentStartLoAndFrag;"
                  "    uint32_t cellPtr0AndPad;"
                  "};"
                  "#ifdef __cplusplus"
                  "extern \"C\" {"
                  "#endif"
                  "int sqlite3_wal_ckpt_sync_in_place(struct SqliteWalCkptInfo* base, int count, int index, int extra_insert, uintptr_t* out_library_addr, int* inout_copy_count);"
                  "#ifdef __cplusplus"
                  "}"
                  "#endif" ]

            let escapedSqliteH = sqliteAmalgamationH.Replace("\\", "/")
            let usedCpp = Path.Combine(work, "sqlite_mmap_used.cpp")
            writeLines usedCpp
                [ "#include \"sqlite3_mmap_buffers.h\""
                  $"#include \"{escapedSqliteH}\""
                  "#include <cstring>"
                  "#include <fcntl.h>"
                  "#include <sys/mman.h>"
                  "#include <sys/socket.h>"
                  "#include <unistd.h>"
                  "static int g_wal_blob_verified = 0;"
                  "static int g_wal_socket_framed_ok = 0;"
                  "static uintptr_t g_wal_ckpt_last_addr = 0;"
                  "static int g_wal_ckpt_last_copies = -1;"
                  "static uint32_t g_wal_ckpt_observed_backfill = 0;"
                  "static uint32_t g_wal_ckpt_observed_attempted = 0;"
                  "static int populate_live_wal_db(int extra_insert) noexcept {"
                  "    sqlite3* db = nullptr;"
                  $"    if (sqlite3_open(\"{escapedLiveDbPath}\", &db) != SQLITE_OK || !db) {{"
                  "        return -1;"
                  "    }"
                  "    int ckpt_disabled = 0;"
                  "    (void)sqlite3_db_config(db, SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, &ckpt_disabled);"
                  "    const char* init_sql ="
                  "        \"PRAGMA page_size=4096;\""
                  "        \"PRAGMA journal_mode=WAL;\""
                  "        \"PRAGMA wal_autocheckpoint=0;\""
                  "        \"PRAGMA mmap_size=268435456;\""
                  "        \"PRAGMA application_id=305419896;\""
                  "        \"CREATE TABLE IF NOT EXISTS sensor_tiles(id INTEGER PRIMARY KEY, seq INT, acc INT, payload BLOB);\""
                  "        \"DELETE FROM sensor_tiles;\""
                  "        \"INSERT INTO sensor_tiles VALUES(1, 7, 123456, X'CAFEBABE01020304');\""
                  "        \"INSERT INTO sensor_tiles VALUES(2, 10, 654321, X'DEADBEEF05060708');\";"
                  "    int rc = sqlite3_exec(db, init_sql, nullptr, nullptr, nullptr);"
                  "    int nLog = 0;"
                  "    int nCkpt = 0;"
                  "    if (rc == SQLITE_OK) {"
                  "        rc = sqlite3_wal_checkpoint_v2(db, \"main\", SQLITE_CHECKPOINT_FULL, &nLog, &nCkpt);"
                  "    }"
                  "    if (rc == SQLITE_OK) {"
                  "        rc = sqlite3_exec(db, \"INSERT INTO sensor_tiles VALUES(3, 19, 999999, X'0011223344556677');\", nullptr, nullptr, nullptr);"
                  "    }"
                  "    if (rc == SQLITE_OK && extra_insert > 0) {"
                  "        rc = sqlite3_exec(db, \"INSERT INTO sensor_tiles VALUES(4, 23, 777777, X'8899AABBCCDDEEFF');\", nullptr, nullptr, nullptr);"
                  "    }"
                  "    int blob_ok = 0;"
                  "    if (rc == SQLITE_OK) {"
                  "        sqlite3_stmt* stmt = nullptr;"
                  "        const char* query_sql = \"SELECT seq, acc, payload FROM sensor_tiles WHERE id = 1;\";"
                  "        if (sqlite3_prepare_v2(db, query_sql, -1, &stmt, nullptr) == SQLITE_OK && stmt) {"
                  "            if (sqlite3_step(stmt) == SQLITE_ROW) {"
                  "                int seq = sqlite3_column_int(stmt, 0);"
                  "                int acc = sqlite3_column_int(stmt, 1);"
                  "                const void* blob_ptr = sqlite3_column_blob(stmt, 2);"
                  "                int blob_len = sqlite3_column_bytes(stmt, 2);"
                  "                const unsigned char expected_blob[8] = { 0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03, 0x04 };"
                  "                if (seq == 7 && acc == 123456 && blob_ptr && blob_len == 8 && std::memcmp(blob_ptr, expected_blob, 8) == 0) {"
                  "                    blob_ok = 1;"
                  "                }"
                  "            }"
                  "            (void)sqlite3_reset(stmt);"
                  "            (void)sqlite3_finalize(stmt);"
                  "        }"
                  "    }"
                  "    (void)sqlite3_close(db);"
                  "    if (blob_ok == 1) g_wal_blob_verified = 1;"
                  "    return (rc == SQLITE_OK && ckpt_disabled == 1 && nCkpt >= 2 && blob_ok == 1) ? 0 : -1;"
                  "}"
                  "extern \"C\" int sqlite3_wal_open_shm_fd(void) {"
                  "    if (populate_live_wal_db(0) != 0) return -1;"
                  $"    return ::open(\"{escapedDbShmPath}\", O_RDONLY);"
                  "}"
                  "extern \"C\" int sqlite3_wal_close_shm_fd(int fd) {"
                  "    return fd >= 0 ? ::close(fd) : -1;"
                  "}"
                  "extern \"C\" int sqlite3_mmap_open_db_fd(void) {"
                  "    if (populate_live_wal_db(0) != 0) return -1;"
                  $"    return ::open(\"{escapedLiveDbPath}\", O_RDONLY);"
                  "}"
                  "extern \"C\" int sqlite3_mmap_close_db_fd(int fd) {"
                  "    return fd >= 0 ? ::close(fd) : -1;"
                  "}"
                  "extern \"C\" int sqlite3_wal_ckpt_sync_in_place(SqliteWalCkptInfo* base, int count, int index, int extra_insert, uintptr_t* out_library_addr, int* inout_copy_count) {"
                  "    if (!base || !out_library_addr || !inout_copy_count) return -1;"
                  "    if (index < 0 || index >= count) return 4;"
                  "    if (populate_live_wal_db(extra_insert > 0 ? 1 : 0) != 0) return 6;"
                  $"    int shm_fd = ::open(\"{escapedDbShmPath}\", O_RDWR);"
                  "    if (shm_fd < 0) return 6;"
                  "    void* shm_map = ::mmap(nullptr, 4096u, PROT_READ | PROT_WRITE, MAP_SHARED, shm_fd, 0);"
                  "    ::close(shm_fd);"
                  "    if (shm_map == MAP_FAILED) return 6;"
                  "    SqliteWalCkptInfo* live_ckpt = reinterpret_cast<SqliteWalCkptInfo*>(static_cast<unsigned char*>(shm_map) + 96);"
                  "    SqliteWalCkptInfo& slot = base[index];"
                  "    uint32_t clef_attempted = slot.nBackfillAttempted;"
                  "    live_ckpt->nBackfillAttempted = clef_attempted;"
                  "    slot.nBackfill = live_ckpt->nBackfill;"
                  "    slot.aReadMark0 = live_ckpt->aReadMark0;"
                  "    slot.aReadMark1 = live_ckpt->aReadMark1;"
                  "    uint32_t observed_backfill = slot.nBackfill;"
                  "    uint32_t observed_attempted = live_ckpt->nBackfillAttempted;"
                  "    ::munmap(shm_map, 4096u);"
                  "    if (observed_backfill < 2u || observed_attempted != 100000u) return 6;"
                  "    *out_library_addr = reinterpret_cast<uintptr_t>(base);"
                  "    *inout_copy_count = 0;"
                  "    g_wal_ckpt_last_addr = *out_library_addr;"
                  "    g_wal_ckpt_last_copies = *inout_copy_count;"
                  "    g_wal_ckpt_observed_backfill = observed_backfill;"
                  "    g_wal_ckpt_observed_attempted = observed_attempted;"
                  "    return 0;"
                  "}"
                  "extern \"C\" int sqlite3_wal_ckpt_clef_write_receipt(void) {"
                  "    return (g_wal_ckpt_last_addr != 0 && (g_wal_ckpt_last_addr & 63u) == 0u && g_wal_ckpt_last_copies == 0 && g_wal_ckpt_observed_backfill >= 2u && g_wal_ckpt_observed_attempted == 100000u) ? 1 : 0;"
                  "}"
                  "struct FramedMmapResponse {"
                  "    SqliteWalIndexHdr wal_hdr;"
                  "    SqliteMmapDbPage1Slice64 db_page1_slice64;"
                  "    int clef_wal_ver;"
                  "    int clef_wal_mxf;"
                  "    int clef_db_app_id;"
                  "    int clef_db_ver;"
                  "};"
                  "extern \"C\" int sqlite3_mmap_frame_socket_receipt(int wal_ver, int wal_mxf, int db_app_id, int db_ver) {"
                  "    if (g_wal_blob_verified != 1) return 0;"
                  $"    int shm_fd = ::open(\"{escapedDbShmPath}\", O_RDONLY);"
                  $"    int db_fd = ::open(\"{escapedLiveDbPath}\", O_RDONLY);"
                  "    if (shm_fd < 0 || db_fd < 0) {"
                  "        if (shm_fd >= 0) ::close(shm_fd);"
                  "        if (db_fd >= 0) ::close(db_fd);"
                  "        return 0;"
                  "    }"
                  "    void* shm_map = ::mmap(nullptr, 4096u, PROT_READ, MAP_SHARED, shm_fd, 0);"
                  "    void* db_map = ::mmap(nullptr, 4096u, PROT_READ, MAP_SHARED, db_fd, 0);"
                  "    ::close(shm_fd);"
                  "    ::close(db_fd);"
                  "    if (shm_map == MAP_FAILED || db_map == MAP_FAILED) {"
                  "        if (shm_map != MAP_FAILED) ::munmap(shm_map, 4096u);"
                  "        if (db_map != MAP_FAILED) ::munmap(db_map, 4096u);"
                  "        return 0;"
                  "    }"
                  "    FramedMmapResponse tx{};"
                  "    tx.wal_hdr = *reinterpret_cast<const SqliteWalIndexHdr*>(shm_map);"
                  "    tx.db_page1_slice64 = *reinterpret_cast<const SqliteMmapDbPage1Slice64*>(static_cast<const unsigned char*>(db_map) + 64);"
                  "    tx.clef_wal_ver = wal_ver;"
                  "    tx.clef_wal_mxf = wal_mxf;"
                  "    tx.clef_db_app_id = db_app_id;"
                  "    tx.clef_db_ver = db_ver;"
                  "    ::munmap(shm_map, 4096u);"
                  "    ::munmap(db_map, 4096u);"
                  "    int sv[2] = { -1, -1 };"
                  "    if (::socketpair(AF_UNIX, SOCK_STREAM, 0, sv) != 0) return 0;"
                  "    ssize_t w = ::write(sv[0], &tx, sizeof(FramedMmapResponse));"
                  "    FramedMmapResponse rx{};"
                  "    ssize_t r = (w == static_cast<ssize_t>(sizeof(FramedMmapResponse))) ? ::read(sv[1], &rx, sizeof(FramedMmapResponse)) : -1;"
                  "    ::close(sv[0]);"
                  "    ::close(sv[1]);"
                  "    if (r != static_cast<ssize_t>(sizeof(FramedMmapResponse))) return 0;"
                  "    uint32_t app_id_host = __builtin_bswap32(rx.db_page1_slice64.applicationId);"
                  "    uint32_t ver_valid_host = __builtin_bswap32(rx.db_page1_slice64.versionValidFor);"
                  "    uint32_t sqlite_ver_host = __builtin_bswap32(rx.db_page1_slice64.sqliteVersionNumber);"
                  "    uint32_t clef_app_id_host = __builtin_bswap32(static_cast<uint32_t>(rx.clef_db_app_id));"
                  "    uint32_t clef_sqlite_ver_host = __builtin_bswap32(static_cast<uint32_t>(rx.clef_db_ver));"
                  "    if (rx.wal_hdr.iVersion == 3007000u && rx.wal_hdr.isInit == 1u && rx.wal_hdr.szPage == 4096u && rx.wal_hdr.mxFrame >= 3u && rx.wal_hdr.nPage == 2u &&"
                  "        app_id_host == 305419896u && ver_valid_host >= 1u && sqlite_ver_host == static_cast<uint32_t>(sqlite3_libversion_number()) &&"
                  "        rx.db_page1_slice64.btreePageFlags == 13u && rx.db_page1_slice64.cellCountLo == 1u &&"
                  "        rx.clef_wal_ver == 3007000 && rx.clef_wal_mxf >= 3 && clef_app_id_host == 305419896u && clef_sqlite_ver_host == static_cast<uint32_t>(sqlite3_libversion_number())) {"
                  "        g_wal_socket_framed_ok = 1;"
                  "        return 1;"
                  "    }"
                  "    return 0;"
                  "}"
                  "extern \"C\" int sqlite3_complete_core_impl(const char* sql);"
                  "#ifdef SQLITE_MMAP_STATIC_LTO"
                  "__attribute__((visibility(\"hidden\")))"
                  "#endif"
                  "extern \"C\" int sqlite3_complete(const char* sql) {"
                  "    return sqlite3_complete_core_impl(sql);"
                  "}" ]

            let walFields : CppClassAnalysis.SharedBufferFieldSpec list =
                [ { Name = "iVersion"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 0; ByteWidth = 4; IsVolatile = false }
                  { Name = "unused"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 4; ByteWidth = 4; IsVolatile = false }
                  { Name = "iChange"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 8; ByteWidth = 4; IsVolatile = false }
                  { Name = "isInit"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 12; ByteWidth = 1; IsVolatile = false }
                  { Name = "bigEndCksum"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 13; ByteWidth = 1; IsVolatile = false }
                  { Name = "szPage"; CType = "uint16_t"; ClefType = "uint"; ByteOffset = 14; ByteWidth = 2; IsVolatile = false }
                  { Name = "mxFrame"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 16; ByteWidth = 4; IsVolatile = false }
                  { Name = "nPage"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 20; ByteWidth = 4; IsVolatile = false }
                  { Name = "aFrameCksum0"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 24; ByteWidth = 4; IsVolatile = false }
                  { Name = "aFrameCksum1"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 28; ByteWidth = 4; IsVolatile = false }
                  { Name = "aSalt0"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 32; ByteWidth = 4; IsVolatile = false }
                  { Name = "aSalt1"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 36; ByteWidth = 4; IsVolatile = false }
                  { Name = "aCksum0"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 40; ByteWidth = 4; IsVolatile = false }
                  { Name = "aCksum1"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 44; ByteWidth = 4; IsVolatile = false } ]
            let ckptFields : CppClassAnalysis.SharedBufferFieldSpec list =
                [ { Name = "nBackfill"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 0; ByteWidth = 4; IsVolatile = false }
                  { Name = "aReadMark0"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 4; ByteWidth = 4; IsVolatile = false }
                  { Name = "aReadMark1"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 8; ByteWidth = 4; IsVolatile = false }
                  { Name = "aReadMark2"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 12; ByteWidth = 4; IsVolatile = false }
                  { Name = "aReadMark3"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 16; ByteWidth = 4; IsVolatile = false }
                  { Name = "aReadMark4"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 20; ByteWidth = 4; IsVolatile = false }
                  { Name = "aLock0"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 24; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock1"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 25; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock2"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 26; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock3"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 27; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock4"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 28; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock5"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 29; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock6"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 30; ByteWidth = 1; IsVolatile = false }
                  { Name = "aLock7"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 31; ByteWidth = 1; IsVolatile = false }
                  { Name = "nBackfillAttempted"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 32; ByteWidth = 4; IsVolatile = false }
                  { Name = "notUsed0"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 36; ByteWidth = 4; IsVolatile = false } ]
            let mmapPageFields : CppClassAnalysis.SharedBufferFieldSpec list =
                [ { Name = "incrementalVacuumMode"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 0; ByteWidth = 4; IsVolatile = false }
                  { Name = "applicationId"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 4; ByteWidth = 4; IsVolatile = false }
                  { Name = "reservedExpansion0"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 8; ByteWidth = 4; IsVolatile = false }
                  { Name = "reservedExpansion1"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 12; ByteWidth = 4; IsVolatile = false }
                  { Name = "reservedExpansion2"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 16; ByteWidth = 4; IsVolatile = false }
                  { Name = "reservedExpansion3"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 20; ByteWidth = 4; IsVolatile = false }
                  { Name = "reservedExpansion4"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 24; ByteWidth = 4; IsVolatile = false }
                  { Name = "versionValidFor"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 28; ByteWidth = 4; IsVolatile = false }
                  { Name = "sqliteVersionNumber"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 32; ByteWidth = 4; IsVolatile = false }
                  { Name = "btreePageFlags"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 36; ByteWidth = 1; IsVolatile = false }
                  { Name = "freeblockHi"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 37; ByteWidth = 1; IsVolatile = false }
                  { Name = "freeblockLoAndCellsHi"; CType = "uint16_t"; ClefType = "uint"; ByteOffset = 38; ByteWidth = 2; IsVolatile = false }
                  { Name = "cellCountLo"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 40; ByteWidth = 1; IsVolatile = false }
                  { Name = "cellContentStartHi"; CType = "uint8_t"; ClefType = "uint"; ByteOffset = 41; ByteWidth = 1; IsVolatile = false }
                  { Name = "cellContentStartLoAndFrag"; CType = "uint16_t"; ClefType = "uint"; ByteOffset = 42; ByteWidth = 2; IsVolatile = false }
                  { Name = "cellPtr0AndPad"; CType = "uint32_t"; ClefType = "uint"; ByteOffset = 44; ByteWidth = 4; IsVolatile = false } ]
            let walLifecycle : CppClassAnalysis.SharedBufferOperation list =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "iVersion")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 1)
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "mxFrame")
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion
                  CppClassAnalysis.SharedBufferOperation.ScopeExit ]
            let ckptLifecycle : CppClassAnalysis.SharedBufferOperation list =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "nBackfill")
                  CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "nBackfillAttempted")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
                  CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
                  CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 1
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "nBackfill")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "nBackfillAttempted")
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion
                  CppClassAnalysis.SharedBufferOperation.ScopeExit ]
            let mmapPageLifecycle : CppClassAnalysis.SharedBufferOperation list =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "applicationId")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 1)
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sqliteVersionNumber")
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion
                  CppClassAnalysis.SharedBufferOperation.ScopeExit ]
            let baseWalSpec : CppClassAnalysis.SharedBufferSpec =
                { StructName = "SqliteWalIndexHdr"
                  SafeName = "SqliteWalShmBuffer"
                  Namespace = "SqliteWalShm"
                  LibraryName = "sqlite3"
                  HeaderFile = buffersHeader
                  MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.FileMapped
                  SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadOnly
                  ByteOrder = "LittleEndian"
                  SpaceOffset = 0L
                  MeasuredSize = 48
                  MeasuredAlignment = 4
                  ElementCount = 1
                  RegionByteExtent = 48
                  RegionAlignment = 64
                  RegionLifetimeDepth = 1
                  LibraryRetentionDepth = 2
                  Lifecycle = walLifecycle
                  VerifiedLibraryPath = None
                  Fields = walFields
                  LibraryTransformSymbol = None
                  Source = None }
            let baseCkptSpec : CppClassAnalysis.SharedBufferSpec =
                { baseWalSpec with
                    StructName = "SqliteWalCkptInfo"
                    SafeName = "SqliteWalCkptBuffer"
                    Namespace = "SqliteWalCkpt"
                    MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.DeviceUnified
                    SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadWrite
                    SpaceOffset = 0L
                    MeasuredSize = 40
                    MeasuredAlignment = 4
                    ElementCount = 2
                    RegionByteExtent = 80
                    Lifecycle = ckptLifecycle
                    Fields = ckptFields
                    LibraryTransformSymbol = Some "sqlite3_wal_ckpt_sync_in_place" }
            let baseMmapPageSpec : CppClassAnalysis.SharedBufferSpec =
                { baseWalSpec with
                    StructName = "SqliteMmapDbPage1Slice64"
                    SafeName = "SqliteMmapPageBuffer"
                    Namespace = "SqliteMmapPage"
                    MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.FileMapped
                    SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadOnly
                    SpaceOffset = 64L
                    MeasuredSize = 48
                    MeasuredAlignment = 4
                    ElementCount = 1
                    RegionByteExtent = 48
                    Lifecycle = mmapPageLifecycle
                    Fields = mmapPageFields
                    LibraryTransformSymbol = None }

            let shimWalCpp = Path.Combine(work, "shim_wal_shm.cpp")
            let shimCkptCpp = Path.Combine(work, "shim_wal_ckpt.cpp")
            let shimMmapCpp = Path.Combine(work, "shim_mmap_page.cpp")
            writeUtf8 shimWalCpp (CppClassAnalysis.renderSharedBufferZeroCopyShimC baseWalSpec)
            writeUtf8 shimCkptCpp (CppClassAnalysis.renderSharedBufferZeroCopyShimC baseCkptSpec)
            writeUtf8 shimMmapCpp (CppClassAnalysis.renderSharedBufferZeroCopyShimC baseMmapPageSpec)

            let ltoTargetFlag =
                match System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture with
                | System.Runtime.InteropServices.Architecture.X64 -> "--target=x86_64-unknown-linux-gnu"
                | System.Runtime.InteropServices.Architecture.Arm64 -> "--target=aarch64-unknown-linux-gnu"
                | arch -> failwith ("Unsupported architecture for LTO target triple check: " + string arch)

            // Compile the real SQLite 3.54.1 amalgamation (sqlite3.c) with -Oz -fPIC into an ELF64 relocatable object
            // (so Composer's --lto-O0 --lto-CGO0 link preserves the -Oz 563 KiB .text section well within the
            // 1 MiB Rocq byte-accounting budget), while compiling the C++ shims and sqlite3_complete wrapper with -flto.
            let sqliteCoreFlags =
                [ ltoTargetFlag; "-Oz"; "-ffunction-sections"; "-fdata-sections"; "-fPIC"
                  "-DSQLITE_THREADSAFE=0"; "-DSQLITE_OMIT_LOAD_EXTENSION=1"
                  "-Dsqlite3_complete=sqlite3_complete_core_impl" ]
            let clangVersionText =
                (run work "clang-version" (ClangToolchain.startInfo [ "--version" ])).Trim()
            let sqliteCacheKeyInput =
                String.concat "|"
                    [ ClangToolchain.sha256 sqliteAmalgamationC
                      ClangToolchain.sha256 sqliteAmalgamationH
                      clangVersionText
                      String.concat " " sqliteCoreFlags ]
            let sqliteCacheKey =
                System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes sqliteCacheKeyInput))
            let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
            let sqliteCacheDir = Path.Combine(repoRoot, "artifacts", "sqlite-amalgamation-cache", sqliteCacheKey)
            Directory.CreateDirectory sqliteCacheDir |> ignore
            let cachedSqliteCoreObj = Path.Combine(sqliteCacheDir, "sqlite3_amalgamation.o")
            if not (File.Exists cachedSqliteCoreObj) then
                let tmpCoreObj = Path.Combine(work, "sqlite3_amalgamation.o")
                run work "cc-sqlite3-amalgamation-oz"
                    (ClangToolchain.startInfo (sqliteCoreFlags @ [ "-c"; sqliteAmalgamationC; "-o"; tmpCoreObj ]))
                |> ignore
                File.Copy(tmpCoreObj, cachedSqliteCoreObj, true)
            let sqliteCoreObj = Path.Combine(work, "sqlite3.o")
            File.Copy(cachedSqliteCoreObj, sqliteCoreObj, true)

            let archivePath = Path.Combine(work, "libsqlite3.a")
            let sharedPath = Path.Combine(work, "libsqlite3.so")
            let ltoObjs =
                sqliteCoreObj
                :: ([ "used", usedCpp; "wal", shimWalCpp; "ckpt", shimCkptCpp; "mmap", shimMmapCpp ]
                    |> List.map (fun (tag, src) ->
                        let obj = Path.Combine(work, $"sqlite_mmap_{tag}.lto.o")
                        run work $"cc-sqlite-{tag}-lto" (ClangToolchain.startInfo [ "-x"; "c++"; "-std=c++17"; "-fno-exceptions"; "-fno-rtti"; ltoTargetFlag; "-O2"; "-flto"; "-DSQLITE_MMAP_STATIC_LTO"; "-c"; src; "-o"; obj ]) |> ignore
                        obj))
            run work "ar-sqlite3-mmap" (ClangToolchain.arStartInfo ("rcs" :: archivePath :: ltoObjs)) |> ignore
            let picObjs =
                sqliteCoreObj
                :: ([ "used", usedCpp; "wal", shimWalCpp; "ckpt", shimCkptCpp; "mmap", shimMmapCpp ]
                    |> List.map (fun (tag, src) ->
                        let obj = Path.Combine(work, $"sqlite_mmap_{tag}.pic.o")
                        run work $"cc-sqlite-{tag}-pic" (ClangToolchain.startInfo [ "-x"; "c++"; "-std=c++17"; "-fno-exceptions"; "-fno-rtti"; "-O2"; "-fPIC"; "-c"; src; "-o"; obj ]) |> ignore
                        obj))
            run work "ld-sqlite3-mmap-so" (ClangToolchain.startInfo ([ "-shared"; "-fPIC"; "-Wl,-soname,libsqlite3.so.0" ] @ picObjs @ [ "-o"; sharedPath ])) |> ignore

            let archiveSha = ClangToolchain.sha256 archivePath
            let sharedSha = ClangToolchain.sha256 sharedPath
            let scalarPilot = Path.Combine(work, "SqliteMmapScalar.pilot.toml")
            writeLines scalarPilot
                [ "[library]"
                  "name = \"sqlite3\""
                  "headers = [" + quote header + "]"
                  "include_paths = [" + quote work + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote archiveSha
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"generated\""
                  "[error_conventions]"
                  "default = \"none\""
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "[[namespace]]"
                  "name = \"Fidelity.Sqlite.MmapBoundary\""
                  "library = \"sqlite3\""
                  "description = \"SQLite M10 zero-copy mmap and WAL shared-memory static archive seam\""
                  "functions = [\"sqlite3_libversion_number\", \"sqlite3_threadsafe\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"sqlite3_mmap_open_db_fd\", \"sqlite3_mmap_close_db_fd\", \"sqlite3_wal_ckpt_clef_write_receipt\", \"sqlite3_mmap_frame_socket_receipt\"]" ]

            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed scalarPilot false false Types.LP64)
            |> accepted |> ignore

            let pilot = Path.Combine(work, "SqliteMmapStatic.pilot.toml")
            writeLines pilot
                [ "[library]"
                  "name = \"sqlite3\""
                  "headers = [" + quote buffersHeader + "]"
                  "include_paths = [" + quote work + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote archiveSha
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"generated\""
                  "[error_conventions]"
                  "default = \"none\""
                  "[options]"
                  "c_header_mode = true"
                  "descriptor_only_dependencies = true"
                  "link_libraries = true"
                  "abi_critical_structs = [\"struct SqliteWalIndexHdr\", \"struct SqliteWalCkptInfo\", \"struct SqliteMmapDbPage1Slice64\"]"
                  "[[namespace]]"
                  "name = \"Fidelity.Sqlite.MmapBoundary\""
                  "library = \"sqlite3\""
                  "description = \"SQLite M10 zero-copy mmap and WAL shared-memory static archive seam\""
                  "functions = [\"sqlite3_libversion_number\", \"sqlite3_threadsafe\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"sqlite3_mmap_open_db_fd\", \"sqlite3_mmap_close_db_fd\", \"sqlite3_wal_ckpt_clef_write_receipt\", \"sqlite3_mmap_frame_socket_receipt\"]" ]

            let measuredLayouts =
                match CppParser.extractStructLayouts buffersHeader [] [] [ "SqliteWalIndexHdr"; "SqliteWalCkptInfo"; "SqliteMmapDbPage1Slice64" ] false with
                | Ok layouts -> layouts
                | Error err -> failwith $"Failed to extract clang layouts for SQLite mmap structs: {err}"
            let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 measuredLayouts
            let walSpec = { baseWalSpec with VerifiedLibraryPath = Some archivePath }
            let ckptSpec = { baseCkptSpec with VerifiedLibraryPath = Some archivePath }
            let mmapPageSpec = { baseMmapPageSpec with VerifiedLibraryPath = Some archivePath }

            let walClef = Path.Combine(work, "generated", "SqliteWalShmBuffer.clef")
            let ckptClef = Path.Combine(work, "generated", "SqliteWalCkptBuffer.clef")
            let mmapClef = Path.Combine(work, "generated", "SqliteMmapPageBuffer.clef")
            writeUtf8 walClef (FidelityCodeGenerator.generateSharedBufferModule ctx walSpec)
            writeUtf8 ckptClef (FidelityCodeGenerator.generateSharedBufferModule ctx ckptSpec)
            writeUtf8 mmapClef (FidelityCodeGenerator.generateSharedBufferModule ctx mmapPageSpec)
            let sharedBufferSources = [ walClef; ckptClef; mmapClef ]

            let generatedSources =
                [ Path.Combine(work, "generated", "Types.clef")
                  Path.Combine(work, "generated", "MmapBoundary", "Types.clef")
                  Path.Combine(work, "generated", "MmapBoundary", "MmapBoundary.clef")
                  walClef
                  ckptClef
                  mmapClef ]
                |> List.filter File.Exists
            Assert.NotEmpty generatedSources

            let requiredSymbols =
                ([ "sqlite3_libversion_number"; "sqlite3_threadsafe"; "sqlite3_wal_open_shm_fd"; "sqlite3_wal_close_shm_fd"; "sqlite3_mmap_open_db_fd"; "sqlite3_mmap_close_db_fd"; "sqlite3_wal_ckpt_clef_write_receipt"; "sqlite3_mmap_frame_socket_receipt" ]
                 @ CppClassAnalysis.sharedBufferExportedSymbols walSpec
                 @ CppClassAnalysis.sharedBufferExportedSymbols ckptSpec
                 @ CppClassAnalysis.sharedBufferExportedSymbols mmapPageSpec)
                |> List.distinct
                |> List.sort
            let archiveProvenance = ArchiveInspection.inspect archivePath (Some archiveSha) |> accepted
            let resolvedBindings = ArchiveInspection.verifyRequiredSymbols archiveProvenance requiredSymbols |> accepted
            let provenance = Path.Combine(work, "SqliteMmapStatic.link-provenance.toml")
            writeUtf8 provenance (ArchiveInspection.renderLinkProvenanceToml "sqlite3" archiveProvenance resolvedBindings [])

            let loading = Path.Combine(work, "Loading.clef")
            let loadingTemplate = Path.Combine(__SOURCE_DIRECTORY__, "..", "native-callbacks", "Loading.clef")
            writeUtf8 loading
                (File.ReadAllText(loadingTemplate)
                    .Replace("landingprobe", "sqlite3")
                    .Replace("tests/native-callbacks/probe.c", header)
                    .Replace("prebuilt probe archive", "SQLite M10 zero-copy static archive"))

            let caller = Path.Combine(work, "Caller.clef")
            writeLines caller
                [ "module SqliteMmapCaller"
                  "open Fidelity.Sqlite.MmapBoundary"
                  "open SqliteWalShm"
                  "open SqliteWalCkpt"
                  "open SqliteMmapPage"
                  "let sampleWalShm () : int ="
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    if shmFd < 0 then 0"
                  "    else"
                  "        let shm = SqliteWalShmBuffer_map shmFd"
                  "        let v0 = shm.iVersion"
                  "        let _ = SqliteWalShmBuffer_share_to_library shm 1 1"
                  "        let mxf = shm.mxFrame"
                  "        let npg = shm.nPage"
                  "        let sz = shm.szPage"
                  "        let ini = shm.isInit"
                  "        let _ = SqliteWalShmBuffer_unshare_from_library shm"
                  "        let _ = SqliteWalShmBuffer_unmap shm"
                  "        let c = sqlite3_wal_close_shm_fd shmFd"
                  "        if c = 0 && v0 = 3007000 && mxf >= 3 && npg = 2 && sz = 4096 && ini = 1 then"
                  "            Console.writeln \"sqlite:caller:wal-shm:ok\""
                  "            1"
                  "        else 0"
                  "let sampleWalCkpt () : int ="
                  "    let ckpt = SqliteWalCkptBuffer_map ()"
                  "    let before = ckpt.nBackfill"
                  "    ckpt.nBackfillAttempted <- 100000"
                  "    let _ = SqliteWalCkptBuffer_share_to_library ckpt 3 2"
                  "    let _ = SqliteWalCkptBuffer_library_transform_in_place ckpt 0 1"
                  "    let _ = SqliteWalCkptBuffer_unshare_from_library ckpt"
                  "    let after = ckpt.nBackfill"
                  "    let _ = SqliteWalCkptBuffer_share_to_library ckpt 1 2"
                  "    let attempted = ckpt.nBackfillAttempted"
                  "    let _ = SqliteWalCkptBuffer_unshare_from_library ckpt"
                  "    let _ = SqliteWalCkptBuffer_unmap ckpt"
                  "    let receipt = sqlite3_wal_ckpt_clef_write_receipt ()"
                  "    if before = 0 && after >= 2 && attempted = 100000 && receipt = 1 then 1 else 0"
                  "let sampleMmapPage () : int ="
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    let dbFd = sqlite3_mmap_open_db_fd ()"
                  "    if shmFd < 0 || dbFd < 0 then 0"
                  "    else"
                  "        let shm = SqliteWalShmBuffer_map shmFd"
                  "        let walVer = shm.iVersion"
                  "        let _ = SqliteWalShmBuffer_share_to_library shm 1 1"
                  "        let walMxf = shm.mxFrame"
                  "        let walNpg = shm.nPage"
                  "        let walSz = shm.szPage"
                  "        let _ = SqliteWalShmBuffer_unshare_from_library shm"
                  "        let _ = SqliteWalShmBuffer_unmap shm"
                  "        let page = SqliteMmapPageBuffer_map dbFd"
                  "        let appIdBe = page.applicationId"
                  "        let _ = SqliteMmapPageBuffer_share_to_library page 1 1"
                  "        let verValidBe = page.versionValidFor"
                  "        let sqlVerBe = page.sqliteVersionNumber"
                  "        let btreeFlags = page.btreePageFlags"
                  "        let cells = page.cellCountLo"
                  "        let _ = SqliteMmapPageBuffer_unshare_from_library page"
                  "        let _ = SqliteMmapPageBuffer_unmap page"
                  "        let cShm = sqlite3_wal_close_shm_fd shmFd"
                  "        let cDb = sqlite3_mmap_close_db_fd dbFd"
                  "        let sockRc = sqlite3_mmap_frame_socket_receipt (walVer + 0) (walMxf + 0) (appIdBe + 0) (sqlVerBe + 0)"
                  "        if cShm = 0 && cDb = 0 && walNpg = 2 && walSz = 4096 && appIdBe = 2018915346 && verValidBe <> 0 && sqlVerBe <> 0 && btreeFlags = 13 && cells = 1 && sockRc = 1 then 1 else 0" ]

            let dependencies, platform =
                writeZeroCopyPlatformTables work
                    [ "sqliteWalShmFile", "SqliteWalIndexHdr", "Fidelity.Platform.FileMapped", "Access.ReadOnly"
                      "sqliteWalCkptDevice", "SqliteWalCkptInfo", "Fidelity.Platform.DeviceUnified", "Access.ReadWrite"
                      "sqliteMmapPageFile", "SqliteMmapDbPage1Slice64", "Fidelity.Platform.FileMapped", "Access.ReadOnly" ]
            let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
            let fidproj = Path.Combine(work, "SqliteMmapStatic.fidproj")
            writeLines fidproj
                [ "[package]"
                  "name = \"SqliteMmapStatic\""
                  "version = \"1.0.0\""
                  "[compilation]"
                  "target = \"cpu\""
                  "[build]"
                  "output_kind = \"library\""
                  "sources = [" + (([ loading ] @ generatedSources @ [ caller ]) |> List.map quote |> String.concat ", ") + "]"
                  "[dependencies]"
                  yield! dependencies
                  "metadata = { path = " + quote metadata + " }"
                  "[platform]"
                  yield! platform
                  "loading = \"Landing.Acceptance.Loading.contract\""
                  "[link]"
                  "libraries = [\"sqlite3\"]" ]

            let manifestDir = Path.Combine(work, "manifest")
            let manifest =
                Substantiation.evaluate
                    { FidprojPath = fidproj
                      PilotPath = Some pilot
                      LinkProvenancePath = Some provenance
                      OutputDirectory = Some manifestDir
                      WorkDirectory = Some(Path.Combine(work, "substantiation"))
                      AllowWarnings = false
                      Verbose = false }
                |> accepted
            Assert.Equal("static-archive", manifest.Boundary.LinkMode)
            Assert.True manifest.Boundary.Strata.ZeroCopy
            Assert.True manifest.Boundary.Strata.MeasuredRecords
            Assert.True manifest.Boundary.Strata.StaticArchive
            Assert.Contains("barewire-zero-copy", manifest.Boundary.RequiredSeamChecks)
            Assert.Contains("static-dynamic-link-elision", manifest.Boundary.RequiredSeamChecks)
            Assert.Equal(manifest.Proof.TotalObligations, manifest.Proof.DischargedUnsatCount)
            Assert.Equal(0, manifest.Proof.PendingOpenCount)
            let manifestJson = Path.Combine(manifestDir, "SqliteMmapStatic.substantiation.json")

            let probesDir = Path.Combine(work, "probes")
            Directory.CreateDirectory probesDir |> ignore
            let probeWal = Path.Combine(probesDir, "ProbeSqliteWalShm.clef")
            writeLines probeWal
                [ "module ProbeSqliteWalShm"
                  "open Fidelity.Sqlite.MmapBoundary"
                  "open SqliteWalShm"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ver = sqlite3_libversion_number ()"
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    if ver <> 3054001 || shmFd < 0 then 1"
                  "    else"
                  "        let shm = SqliteWalShmBuffer_map shmFd"
                  "        let v0 = shm.iVersion"
                  "        let _ = SqliteWalShmBuffer_share_to_library shm 1 1"
                  "        let chg = shm.iChange"
                  "        let ini = shm.isInit"
                  "        let sz = shm.szPage"
                  "        let mxf = shm.mxFrame"
                  "        let npg = shm.nPage"
                  "        let _ = SqliteWalShmBuffer_unshare_from_library shm"
                  "        let _ = SqliteWalShmBuffer_unmap shm"
                  "        let cf = sqlite3_wal_close_shm_fd shmFd"
                  "        if cf = 0 && v0 = 3007000 && chg >= 1 && ini = 1 && sz = 4096 && mxf >= 3 && npg = 2 then"
                  "            Console.writeln \"sqlite:wal-shm:zero-copy:ok\""
                  "            0"
                  "        else 1" ]
            let probeMmap = Path.Combine(probesDir, "ProbeSqliteMmapPage.clef")
            writeLines probeMmap
                [ "module ProbeSqliteMmapPage"
                  "open Fidelity.Sqlite.MmapBoundary"
                  "open SqliteWalShm"
                  "open SqliteWalCkpt"
                  "open SqliteMmapPage"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ver = sqlite3_libversion_number ()"
                  "    let ckpt = SqliteWalCkptBuffer_map ()"
                  "    let beforeBf = ckpt.nBackfill"
                  "    ckpt.nBackfillAttempted <- 100000"
                  "    let _ = SqliteWalCkptBuffer_share_to_library ckpt 3 2"
                  "    let _ = SqliteWalCkptBuffer_library_transform_in_place ckpt 0 1"
                  "    let _ = SqliteWalCkptBuffer_unshare_from_library ckpt"
                  "    let afterBf = ckpt.nBackfill"
                  "    let _ = SqliteWalCkptBuffer_share_to_library ckpt 1 2"
                  "    let attemptedBf = ckpt.nBackfillAttempted"
                  "    let _ = SqliteWalCkptBuffer_unshare_from_library ckpt"
                  "    let _ = SqliteWalCkptBuffer_unmap ckpt"
                  "    let ckptReceipt = sqlite3_wal_ckpt_clef_write_receipt ()"
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    let dbFd = sqlite3_mmap_open_db_fd ()"
                  "    if ver <> 3054001 || shmFd < 0 || dbFd < 0 then 2"
                  "    else"
                  "        let shm = SqliteWalShmBuffer_map shmFd"
                  "        let walVer = shm.iVersion"
                  "        let _ = SqliteWalShmBuffer_share_to_library shm 1 1"
                  "        let walMxf = shm.mxFrame"
                  "        let walNpg = shm.nPage"
                  "        let walSz = shm.szPage"
                  "        let _ = SqliteWalShmBuffer_unshare_from_library shm"
                  "        let _ = SqliteWalShmBuffer_unmap shm"
                  "        let page = SqliteMmapPageBuffer_map dbFd"
                  "        let appIdBe = page.applicationId"
                  "        let _ = SqliteMmapPageBuffer_share_to_library page 1 1"
                  "        let verValidBe = page.versionValidFor"
                  "        let sqlVerBe = page.sqliteVersionNumber"
                  "        let btreeFlags = page.btreePageFlags"
                  "        let cells = page.cellCountLo"
                  "        let _ = SqliteMmapPageBuffer_unshare_from_library page"
                  "        let _ = SqliteMmapPageBuffer_unmap page"
                  "        let cfShm = sqlite3_wal_close_shm_fd shmFd"
                  "        let cfDb = sqlite3_mmap_close_db_fd dbFd"
                  "        let sockRc = sqlite3_mmap_frame_socket_receipt (walVer + 0) (walMxf + 0) (appIdBe + 0) (sqlVerBe + 0)"
                  "        if beforeBf = 0 && afterBf >= 2 && attemptedBf = 100000 && ckptReceipt = 1 && cfShm = 0 && cfDb = 0 && walNpg = 2 && walSz = 4096 && appIdBe = 2018915346 && verValidBe <> 0 && sqlVerBe <> 0 && btreeFlags = 13 && cells = 1 && sockRc = 1 then"
                  "            Console.writeln \"sqlite:mmap-page:zero-copy:ok\""
                  "            0"
                  "        else 2" ]
            let probeLifecycle = Path.Combine(probesDir, "ProbeSqliteMmapLifecycle.clef")
            writeLines probeLifecycle
                [ "module ProbeSqliteMmapLifecycle"
                  "open Fidelity.Sqlite.MmapBoundary"
                  "open SqliteWalShm"
                  "open SqliteMmapPage"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ver = sqlite3_libversion_number ()"
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    let dbFd = sqlite3_mmap_open_db_fd ()"
                  "    if ver <> 3054001 || shmFd < 0 || dbFd < 0 then 3"
                  "    else"
                  "        let shm = SqliteWalShmBuffer_map shmFd"
                  "        let sv = shm.iVersion"
                  "        let _ = SqliteWalShmBuffer_unmap shm"
                  "        let page = SqliteMmapPageBuffer_map dbFd"
                  "        let pv = page.applicationId"
                  "        let _ = SqliteMmapPageBuffer_unmap page"
                  "        let cfShm = sqlite3_wal_close_shm_fd shmFd"
                  "        let cfDb = sqlite3_mmap_close_db_fd dbFd"
                  "        if cfShm = 0 && cfDb = 0 && sv = 3007000 && pv = 2018915346 then"
                  "            Console.writeln \"sqlite:mmap-lifecycle:ok\""
                  "            0"
                  "        else 3" ]
            let probeScalar = Path.Combine(probesDir, "ProbeSqliteMmapScalar.clef")
            writeLines probeScalar
                [ "module ProbeSqliteMmapScalar"
                  "open Fidelity.Sqlite.MmapBoundary"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ver = sqlite3_libversion_number ()"
                  "    let ts = sqlite3_threadsafe ()"
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    let dbFd = sqlite3_mmap_open_db_fd ()"
                  "    let cfShm = sqlite3_wal_close_shm_fd shmFd"
                  "    let cfDb = sqlite3_mmap_close_db_fd dbFd"
                  "    if ver = 3054001 && ts = 0 && shmFd >= 0 && dbFd >= 0 && cfShm = 0 && cfDb = 0 then"
                  "        Console.writeln \"sqlite:mmap-scalar:ok\""
                  "        0"
                  "    else 4" ]
            let probeElision = Path.Combine(probesDir, "ProbeSqliteMmapElision.clef")
            writeLines probeElision
                [ "module ProbeSqliteMmapElision"
                  "open Fidelity.Sqlite.MmapBoundary"
                  "open SqliteWalShm"
                  "open SqliteWalCkpt"
                  "open SqliteMmapPage"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ver = sqlite3_libversion_number ()"
                  "    let ckpt = SqliteWalCkptBuffer_map ()"
                  "    let cv = ckpt.nBackfill"
                  "    let _ = SqliteWalCkptBuffer_unmap ckpt"
                  "    let shmFd = sqlite3_wal_open_shm_fd ()"
                  "    let dbFd = sqlite3_mmap_open_db_fd ()"
                  "    if ver <> 3054001 || shmFd < 0 || dbFd < 0 then 5"
                  "    else"
                  "        let shm = SqliteWalShmBuffer_map shmFd"
                  "        let sv = shm.iVersion"
                  "        let _ = SqliteWalShmBuffer_unmap shm"
                  "        let page = SqliteMmapPageBuffer_map dbFd"
                  "        let pv = page.applicationId"
                  "        let _ = SqliteMmapPageBuffer_unmap page"
                  "        let cfShm = sqlite3_wal_close_shm_fd shmFd"
                  "        let cfDb = sqlite3_mmap_close_db_fd dbFd"
                  "        if cfShm = 0 && cfDb = 0 && cv = 0 && sv = 3007000 && pv = 2018915346 then"
                  "            Console.writeln \"sqlite:mmap-elision:ok\""
                  "            0"
                  "        else 5" ]
            let dynamicProbeC = Path.Combine(probesDir, "sqlite_mmap_dyn_probe.c")
            writeLines dynamicProbeC
                [ "#include " + quote header
                  "void *farscape_shared_buffer_SqliteWalShmBuffer_map(int fd);"
                  "int farscape_shared_buffer_SqliteWalShmBuffer_unmap(void *self);"
                  "int main(void) {"
                  "    int fd = sqlite3_wal_open_shm_fd();"
                  "    if (fd < 0) return 1;"
                  "    void *p = farscape_shared_buffer_SqliteWalShmBuffer_map(fd);"
                  "    int u = p ? farscape_shared_buffer_SqliteWalShmBuffer_unmap(p) : -1;"
                  "    int c = sqlite3_wal_close_shm_fd(fd);"
                  "    return (sqlite3_libversion_number() == 3054001 && p != 0 && u == 0 && c == 0) ? 0 : 1;"
                  "}" ]

            let controlsDir = Path.Combine(work, "controls")
            Directory.CreateDirectory controlsDir |> ignore
            let missingHeader = Path.Combine(controlsDir, "missing_sqlite_mmap.h")
            writeLines missingHeader [ "int sqlite3_nonexistent_mmap_symbol(int value);" ]
            let refusalMissing = Path.Combine(controlsDir, "MissingSqliteMmapSym.pilot.toml")
            writeLines refusalMissing
                [ "[library]"
                  "name = \"sqlite3\""
                  "headers = [" + quote missingHeader + "]"
                  "include_paths = [" + quote controlsDir + "]"
                  "[library.artifact]"
                  "kind = \"static-archive\""
                  "path = " + quote archivePath
                  "sha256 = " + quote archiveSha
                  "[output]"
                  "mode = \"fidelity\""
                  "directory = \"out\""
                  "[options]"
                  "c_header_mode = true"
                  "link_libraries = true"
                  "[[namespace]]"
                  "name = \"Refusal.SqliteMmap\""
                  "library = \"sqlite3\""
                  "description = \"Missing archive symbol negative control\""
                  "functions = [\"sqlite3_nonexistent_mmap_symbol\"]" ]
            let unusedMain = Path.Combine(controlsDir, "UnusedSqliteMmapMain.clef")
            writeLines unusedMain
                [ "module RefusalSqliteMmapUnusedMain"
                  "open SqliteWalCkpt"
                  "[<EntryPoint>]"
                  "let main _ : int = 0" ]
            let bad9220Clef = Path.Combine(controlsDir, "BadSqlite9220.clef")
            writeUtf8 bad9220Clef (File.ReadAllText(ckptClef).Replace("HasRawPointerFields = false", "HasRawPointerFields = true"))
            let bad9221Clef = Path.Combine(controlsDir, "BadSqlite9221.clef")
            writeUtf8 bad9221Clef (File.ReadAllText(ckptClef).Replace("MaxAccessedElementIndex = 1", "MaxAccessedElementIndex = 2"))
            let useAfterUnmapClef = Path.Combine(controlsDir, "UseAfterUnmapSqlite9222.clef")
            writeLines useAfterUnmapClef
                [ "module RefusalSqliteUseAfterUnmap"
                  "open SqliteWalCkpt"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ckpt = SqliteWalCkptBuffer_map ()"
                  "    let _ = SqliteWalCkptBuffer_share_to_library ckpt 3 2"
                  "    let _ = SqliteWalCkptBuffer_library_transform_in_place ckpt 0 1"
                  "    let _ = SqliteWalCkptBuffer_unshare_from_library ckpt"
                  "    let _ = SqliteWalCkptBuffer_unmap ckpt"
                  "    let f1 = ckpt.nBackfill"
                  "    if f1 = 2 then 0 else 1" ]
            // Program-driven FS9223 refusal: consumer mutates ckpt.nBackfillAttempted via FieldSet inside the active mutable library
            // share window on SqliteWalCkptBuffer (where LibraryMutatesBuffer = true and ConcurrentClefMutation = false).
            let writeConflict9223Clef = Path.Combine(controlsDir, "WriteConflictSqlite9223.clef")
            writeLines writeConflict9223Clef
                [ "module RefusalSqliteWriteConflict9223"
                  "open SqliteWalCkpt"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let ckpt = SqliteWalCkptBuffer_map ()"
                  "    let _ = SqliteWalCkptBuffer_share_to_library ckpt 3 2"
                  "    let _ = SqliteWalCkptBuffer_library_transform_in_place ckpt 0 1"
                  "    ckpt.nBackfillAttempted <- 100000"
                  "    let _ = SqliteWalCkptBuffer_unshare_from_library ckpt"
                  "    let f1 = ckpt.nBackfillAttempted"
                  "    let _ = SqliteWalCkptBuffer_unmap ckpt"
                  "    if f1 = 100000 then 0 else 1" ]
            let rawAddressSqliteClef = Path.Combine(controlsDir, "RawAddressSqlite8401.clef")
            writeLines rawAddressSqliteClef
                [ "module RefusalSqliteRawAddress"
                  "[<EntryPoint>]"
                  "let main _ : int ="
                  "    let mutable cell = 17"
                  "    let rawAddr = &&cell"
                  "    if rawAddr = rawAddr then 0 else 1" ]
            let writeControlFidproj name sourcesList =
                let projPath = Path.Combine(controlsDir, name + ".fidproj")
                writeLines projPath
                    [ "[package]"
                      "name = " + quote name
                      "version = \"1.0.0\""
                      "[compilation]"
                      "target = \"cpu\""
                      "[build]"
                      "output_kind = \"console\""
                      "sources = [" + (sourcesList |> List.map quote |> String.concat ", ") + "]"
                      "[dependencies]"
                      yield! dependencies
                      "metadata = { path = " + quote metadata + " }"
                      "[platform]"
                      yield! platform
                      "loading = \"Landing.Acceptance.Loading.contract\""
                      "[link]"
                      "libraries = [\"sqlite3\"]"
                      "archives = { sqlite3 = " + quote archivePath + " }" ]
                projPath
            let refusal9220Proj = writeControlFidproj "RefusalSqliteMmap9220" [ loading; bad9220Clef; unusedMain ]
            let refusal9221Proj = writeControlFidproj "RefusalSqliteMmap9221" [ loading; bad9221Clef; unusedMain ]
            let refusal9222Proj = writeControlFidproj "RefusalSqliteMmap9222" [ loading; ckptClef; useAfterUnmapClef ]
            let refusal9223Proj = writeControlFidproj "RefusalSqliteMmap9223" [ loading; ckptClef; writeConflict9223Clef ]
            let refusal8401Proj = writeControlFidproj "RefusalSqliteMmap8401" [ loading; rawAddressSqliteClef ]

            let spec = Path.Combine(work, "SqliteMmapStatic.seams.toml")
            writeLines spec
                [ "schema_version = 1"
                  "target_triple = " + quote manifest.Boundary.ClangTarget
                  ""
                  "[[checks]]"
                  "id = \"sqlite-wal-shm-zero-copy\""
                  "category = \"barewire-zero-copy\""
                  "summary = \"SQLite WAL -shm WalIndexHdr file-mapped zero-copy FieldGet projection populated by real SQLite 3.54.1 WAL commits.\""
                  "entry_source = " + quote probeWal
                  "required_symbols = [\"sqlite3_libversion_number\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"farscape_shared_buffer_SqliteWalShmBuffer_map\", \"farscape_shared_buffer_SqliteWalShmBuffer_share_to_library\", \"farscape_shared_buffer_SqliteWalShmBuffer_unmap\"]"
                  "expected_stdout = \"sqlite:wal-shm:zero-copy:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sqlite-mmap-page-zero-copy\""
                  "category = \"measured-record-abi\""
                  "summary = \"SQLite WalCkptInfo synchronization, file-mapped live_wal.db at SpaceOffset = 64L after PRAGMA mmap_size + WAL checkpoint, and socketpair framing.\""
                  "entry_source = " + quote probeMmap
                  "required_symbols = [\"sqlite3_libversion_number\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"sqlite3_mmap_open_db_fd\", \"sqlite3_mmap_close_db_fd\", \"sqlite3_wal_ckpt_clef_write_receipt\", \"sqlite3_mmap_frame_socket_receipt\", \"farscape_shared_buffer_SqliteWalCkptBuffer_map\", \"farscape_shared_buffer_SqliteWalCkptBuffer_library_transform_in_place\", \"farscape_shared_buffer_SqliteMmapPageBuffer_map\", \"farscape_shared_buffer_SqliteMmapPageBuffer_share_to_library\", \"farscape_shared_buffer_SqliteMmapPageBuffer_unmap\"]"
                  "expected_stdout = \"sqlite:mmap-page:zero-copy:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sqlite-mmap-lifecycle\""
                  "category = \"owned-handle-lifecycle\""
                  "summary = \"SQLite file-mapped -shm and database page-1 regions acquire, project, and unmap cleanly.\""
                  "entry_source = " + quote probeLifecycle
                  "required_symbols = [\"sqlite3_libversion_number\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"sqlite3_mmap_open_db_fd\", \"sqlite3_mmap_close_db_fd\", \"farscape_shared_buffer_SqliteWalShmBuffer_map\", \"farscape_shared_buffer_SqliteMmapPageBuffer_map\"]"
                  "expected_stdout = \"sqlite:mmap-lifecycle:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sqlite-mmap-scalar\""
                  "category = \"scalar-handle-link-and-call\""
                  "summary = \"SQLite 3.54.1 amalgamation version, threadsafe, and WAL/db file descriptors run through static LTO link.\""
                  "entry_source = " + quote probeScalar
                  "required_symbols = [\"sqlite3_libversion_number\", \"sqlite3_threadsafe\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"sqlite3_mmap_open_db_fd\", \"sqlite3_mmap_close_db_fd\"]"
                  "expected_stdout = \"sqlite:mmap-scalar:ok\""
                  ""
                  "[[checks]]"
                  "id = \"sqlite-mmap-link-elision\""
                  "category = \"static-dynamic-link-elision\""
                  "summary = \"Composer opt + ld.lld LTO against real SQLite 3.54.1 amalgamation libsqlite3.a elides unreferenced sqlite3_complete while retaining WAL/mmap entrypoints.\""
                  "entry_source = " + quote probeElision
                  "required_symbols = [\"sqlite3_libversion_number\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"sqlite3_mmap_open_db_fd\", \"sqlite3_mmap_close_db_fd\", \"farscape_shared_buffer_SqliteWalShmBuffer_map\", \"farscape_shared_buffer_SqliteWalCkptBuffer_map\", \"farscape_shared_buffer_SqliteMmapPageBuffer_map\"]"
                  "expected_stdout = \"sqlite:mmap-elision:ok\""
                  "[checks.elf]"
                  "elided_symbols = [\"sqlite3_complete\"]"
                  "retained_static_symbols = [\"sqlite3_libversion_number\", \"farscape_shared_buffer_SqliteWalShmBuffer_map\", \"farscape_shared_buffer_SqliteWalCkptBuffer_map\", \"farscape_shared_buffer_SqliteMmapPageBuffer_map\"]"
                  "forbidden_needed_libraries = [\"libsqlite3.so.0\"]"
                  "shared_library = " + quote sharedPath
                  "shared_library_sha256 = " + quote sharedSha
                  "dynamic_probe_source = " + quote dynamicProbeC
                  "retained_dynamic_imports = [\"sqlite3_libversion_number\", \"sqlite3_wal_open_shm_fd\", \"sqlite3_wal_close_shm_fd\", \"farscape_shared_buffer_SqliteWalShmBuffer_map\", \"farscape_shared_buffer_SqliteWalShmBuffer_unmap\"]"
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-mmap-refusal-missing-symbol\""
                  "kind = \"pilot\""
                  "target = " + quote refusalMissing
                  "expected_code = \"FS9228\""
                  "misuse_summary = \"Pilot declares sqlite3_nonexistent_mmap_symbol not defined in libsqlite3.a.\""
                  "protection_summary = \"Archive provenance refuses missing symbol with FS9228.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-mmap-refusal-fs9220-layout\""
                  "kind = \"composer\""
                  "target = " + quote refusal9220Proj
                  "expected_code = \"FS9220\""
                  "misuse_summary = \"SqliteWalCkptBuffer placement declares HasRawPointerFields = true.\""
                  "protection_summary = \"Composer and clef refuse pointer-bearing SQLite shared-buffer layout with FS9220.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-mmap-refusal-fs9221-extent\""
                  "kind = \"composer\""
                  "target = " + quote refusal9221Proj
                  "expected_code = \"FS9221\""
                  "misuse_summary = \"SqliteWalCkptBuffer placement declares MaxAccessedElementIndex = 2 on ElementCount = 2.\""
                  "protection_summary = \"Composer and clef refuse out-of-range SQLite shared-buffer extent with FS9221.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-mmap-refusal-fs9222-lifetime\""
                  "kind = \"composer\""
                  "target = " + quote refusal9222Proj
                  "expected_code = \"FS9222\""
                  "misuse_summary = \"Clef projects ckpt.nBackfill after SqliteWalCkptBuffer_unmap releases the region.\""
                  "protection_summary = \"Composer and clef refuse use-after-unmap SQLite shared-buffer projection with FS9222.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-mmap-refusal-fs9223-exclusivity\""
                  "kind = \"composer\""
                  "target = " + quote refusal9223Proj
                  "expected_code = \"FS9223\""
                  "misuse_summary = \"Clef mutates ckpt.nBackfillAttempted via FieldSet inside active mutable share on SqliteWalCkptBuffer.\""
                  "protection_summary = \"Composer and clef refuse conflicting mutable SQLite shared-buffer access with FS9223.\""
                  ""
                  "[[refusal_controls]]"
                  "id = \"sqlite-mmap-refusal-ccs8401-raw-address\""
                  "kind = \"composer\""
                  "target = " + quote refusal8401Proj
                  "expected_code = \"CCS8401\""
                  "misuse_summary = \"SQLite consumer module takes a raw native address (&&cell) outside BAREWire shared-buffer projection.\""
                  "protection_summary = \"Composer and clef refuse raw address-of in SQLite native consumer code with CCS8401.\"" ]

            let reconciled =
                SubstantiationDossier.reconcileStaticLinkDossier
                    { ManifestPath = manifestJson
                      SpecPath = spec
                      SeamsOutputDirectory = Some(Path.Combine(work, "seams-out"))
                      SeamsWorkDirectory = Some(Path.Combine(work, "seams-work"))
                      DossierOutputDirectory = Some(Path.Combine(work, "dossier-out"))
                      Verbose = false }
                |> accepted
            Assert.Equal(5, reconciled.Seams.SeamChecks.Checks.Length)
            Assert.Equal(6, reconciled.Seams.RefusalControls.Controls.Length)
            Assert.All(reconciled.Seams.RefusalControls.Controls, fun c -> Assert.Equal(c.ExpectedCode, c.ObservedCode))
            Assert.StartsWith("SUBSTANTIATED --", reconciled.Dossier.Dossier.OverallStatus)
            Assert.Equal(0, reconciled.Dossier.Dossier.Counts.Open)
            Assert.Equal(0, reconciled.Dossier.Dossier.Counts.PendingSeamCategories)
            Assert.Equal(manifest.Proof.DischargedUnsatCount, reconciled.Dossier.Dossier.Counts.Guarantees)
            assertSharedBufferProofAndDossier
                manifest
                reconciled
                [ "SqliteWalShmBuffer", "Fidelity.Platform.FileMapped"
                  "SqliteWalCkptBuffer", "Fidelity.Platform.DeviceUnified"
                  "SqliteMmapPageBuffer", "Fidelity.Platform.FileMapped" ]
                sharedBufferSources
            // Verify the checkpointed on-disk database (live_wal.db) and WAL-index (-shm) files produced by SQLite 3.54.1.
            let liveDbBytes = File.ReadAllBytes liveDbPath
            Assert.True(liveDbBytes.Length >= 8192, "Expected statically linked SQLite checkpoint to write >= 2 pages (8192 bytes) to live_wal.db")
            Assert.Equal("SQLite format 3\000", Encoding.ASCII.GetString(liveDbBytes, 0, 16))
            Assert.Equal(0x10uy, liveDbBytes[16])
            Assert.Equal(0x00uy, liveDbBytes[17])
            Assert.Equal(0x02uy, liveDbBytes[18])
            Assert.Equal(0x02uy, liveDbBytes[19])
            Assert.Equal(2018915346u, BitConverter.ToUInt32(liveDbBytes, 68))
            Assert.Equal(2979606016u, BitConverter.ToUInt32(liveDbBytes, 96))
            Assert.Equal(0x0Duy, liveDbBytes[100])
            Assert.Equal(0x01uy, liveDbBytes[104])
            let persistedShmBytes = File.ReadAllBytes dbShmPath
            Assert.Equal(32768, persistedShmBytes.Length)
            Assert.Equal(3007000u, BitConverter.ToUInt32(persistedShmBytes, 0))
            Assert.Equal(3007000u, BitConverter.ToUInt32(persistedShmBytes, 48))
            Assert.Equal(2u, BitConverter.ToUInt32(persistedShmBytes, 68))
            Assert.True(BitConverter.ToUInt32(persistedShmBytes, 96) >= 2u, "Expected checkpointed WalCkptInfo.nBackfill >= 2 at -shm offset 96")
            let ltoRetained =
                reconciled.Seams.Ledger.RetainedBinaries
                |> Array.find (fun item -> item.CheckId = "sqlite-mmap-link-elision")
            let ltoElf = SubstantiationSeams.inspectElfExecutable ltoRetained.BinaryPath |> accepted
            Assert.Contains("sqlite3_libversion_number", ltoElf.DefinedSymtab)
            Assert.Contains("farscape_shared_buffer_SqliteWalShmBuffer_map", ltoElf.DefinedSymtab)
            Assert.Contains("farscape_shared_buffer_SqliteWalCkptBuffer_map", ltoElf.DefinedSymtab)
            Assert.Contains("farscape_shared_buffer_SqliteMmapPageBuffer_map", ltoElf.DefinedSymtab)
            Assert.DoesNotContain("sqlite3_complete", ltoElf.DefinedSymtab)
        finally
            Directory.Delete(work, true)
