namespace Farscape.Tests

open System
open System.IO
open System.Text
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
            writeLines admittedPath
                [ "module Fidelity.DescriptorAdmission.Test"
                  "let memSpace : Expr<MemorySpaceDescriptor> = <@ () @>"
                  "let viewLayout : Expr<ViewLayoutDescriptor> = <@ () @>"
                  "let closedCb : Expr<ClosedCallbackDescriptor> = <@ () @>"
                  "let borrowedView : Expr<BorrowedResultViewDescriptor> = <@ () @>"
                  "let readBuf : Expr<ReadOnlyBufferDescriptor> = <@ () @>"
                  "let bufAbs : Expr<BufferAbsenceContract> = <@ () @>"
                  "let countedArr : Expr<CountedEntryArrayDescriptor> = <@ () @>" ]
            let digest = Substantiation.inspectSource Set.empty admittedPath |> accepted
            for sym in [ "memSpace"; "viewLayout"; "closedCb"; "borrowedView"; "readBuf"; "bufAbs"; "countedArr" ] do
                Assert.Contains("Fidelity.DescriptorAdmission.Test." + sym, digest.CompileTimeDeclarations)

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

    [<Fact>]
    let ``Static Binding Elision Test preserves byte-identical SQLite Clef and fidproj outputs between dynamic and static archive modes`` () =
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
            writeLines dynPilot (sqlitePilotLines dynDir [])
            ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed dynPilot false false Types.LP64)
            |> accepted |> ignore

            let staticDir = Path.Combine(work, "static")
            let staticPilot = Path.Combine(work, "StaticSqlite.pilot.toml")
            writeLines staticPilot
                (sqlitePilotLines staticDir
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
