module Farscape.Tests.SubstantiationSeamSuiteTests

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.Text
open Fidelity.Data.JSON
open Xunit
open Farscape.Core
open Farscape.Tests.SubstantiationTests

// #219 owns the .NET physical ABI, link, lifecycle and refusal-control seam runner (`farscape seams`).
// Every fixture, C object, archive, shared library, Composer artifact, native probe executable and
// JSON receipt lives under the test process scratch root in OS temporary storage; nothing is written
// inside `/home/hhh/repos/Farscape` (#215).

let private repository = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let private scratchRoot = TestHelpers.TestScratch.create "substantiation-seams-tests"

let private scratch label =
    let path = Path.Combine(scratchRoot, label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let private writeLines path (lines: string list) =
    File.WriteAllText(path, String.concat "\n" lines + "\n", UTF8Encoding(false))

let private serializeSeamsLedgerReceipt (ledger: SubstantiationSeams.SeamsLedgerReceipt) : string =
    let dischargeJson (d: SubstantiationSeams.DischargeSummary) =
        JsonValue.Object
            [ "ReceiptPath", JsonValue.String d.ReceiptPath
              "ReceiptSha256", JsonValue.String d.ReceiptSha256
              "PremisesOutputPath", JsonValue.String d.PremisesOutputPath
              "PremisesOutputSha256", JsonValue.String d.PremisesOutputSha256
              "TotalObligations", Json.ofInt64 (int64 d.TotalObligations)
              "DischargedUnsatCount", Json.ofInt64 (int64 d.DischargedUnsatCount)
              "PremisesSatCount", Json.ofInt64 (int64 d.PremisesSatCount)
              "LibraryObligations", Json.ofInt64 (int64 d.LibraryObligations)
              "LibraryDischargedUnsatCount", Json.ofInt64 (int64 d.LibraryDischargedUnsatCount)
              "LibraryPremisesSatCount", Json.ofInt64 (int64 d.LibraryPremisesSatCount)
              "PendingOpenCount", Json.ofInt64 (int64 d.PendingOpenCount)
              "RefutedCount", Json.ofInt64 (int64 d.RefutedCount) ]
    let retainedJson (r: SubstantiationSeams.RetainedProbeBinary) =
        JsonValue.Object
            [ "CheckId", JsonValue.String r.CheckId
              "BinaryPath", JsonValue.String r.BinaryPath
              "BinarySha256", JsonValue.String r.BinarySha256 ]
    let sharedJson (s: SubstantiationSeams.SharedLibraryComparison) =
        JsonValue.Object
            [ "CheckId", JsonValue.String s.CheckId
              "SharedLibraryPath", JsonValue.String s.SharedLibraryPath
              "SharedLibrarySha256", JsonValue.String s.SharedLibrarySha256
              "DynamicProbeBinaryPath", JsonValue.String s.DynamicProbeBinaryPath
              "DynamicProbeBinarySha256", JsonValue.String s.DynamicProbeBinarySha256 ]
    JsonValue.Object
        [ "Schema", JsonValue.String ledger.Schema
          "SchemaVersion", Json.ofInt64 (int64 ledger.SchemaVersion)
          "SeamChecksSchema", JsonValue.String ledger.SeamChecksSchema
          "RefusalControlsSchema", JsonValue.String ledger.RefusalControlsSchema
          "Package", JsonValue.String ledger.Package
          "ManifestIntegritySha256", JsonValue.String ledger.ManifestIntegritySha256
          "TargetTriple", JsonValue.String ledger.TargetTriple
          "RequiredSeamCategories", JsonValue.Array (ledger.RequiredSeamCategories |> Array.toList |> List.map JsonValue.String)
          "VerifiedPhysicalCategories", JsonValue.Array (ledger.VerifiedPhysicalCategories |> Array.toList |> List.map JsonValue.String)
          "Discharge", dischargeJson ledger.Discharge
          "RetainedBinaries", JsonValue.Array (ledger.RetainedBinaries |> Array.toList |> List.map retainedJson)
          "SharedLibraries", JsonValue.Array (ledger.SharedLibraries |> Array.toList |> List.map sharedJson)
          "SeamChecksPath", JsonValue.String ledger.SeamChecksPath
          "SeamChecksSha256", JsonValue.String ledger.SeamChecksSha256
          "RefusalControlsPath", JsonValue.String ledger.RefusalControlsPath
          "RefusalControlsSha256", JsonValue.String ledger.RefusalControlsSha256 ]
    |> Json.serializePretty

let private runWithBound (timeoutMs: int) (work: string) (label: string) (info: ProcessStartInfo) =
    if String.IsNullOrWhiteSpace info.WorkingDirectory then info.WorkingDirectory <- work
    let toolScratch = Path.Combine(work, "tool-scratch")
    Directory.CreateDirectory toolScratch |> ignore
    info.Environment["TMPDIR"] <- toolScratch
    use child = new Process(StartInfo = info)
    Assert.True(child.Start(), "the required tool did not start")
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit timeoutMs) then
        child.Kill(true)
        failwithf "%s exceeded the %d ms process bound" label timeoutMs
    child.WaitForExit()
    let output = stdout.Result + stderr.Result
    File.WriteAllText(Path.Combine(work, label + ".log"), output, UTF8Encoding(false))
    Assert.True(child.ExitCode = 0, $"{label} failed with exit {child.ExitCode}: {output}")
    output

type private SeamFixture =
    { Work: string
      Header: string
      Source: string
      Archive: string
      SharedLib: string
      SharedLibSha256: string
      PartialSharedLib: string
      PartialSharedLibSha256: string
      MainObject: string
      DeadObject: string
      Pilot: string
      Provenance: string
      Fidproj: string
      Manifest: Substantiation.Manifest
      ManifestJson: string
      ManifestToml: string
      SpecPath: string
      DynamicProbeC: string
      ProbeScalar: string
      RefusalPilotMissingSym: string
      RefusalUnsettledProj: string
      RefusalSupersetComposerProj: string
      RefusalValidPilot: string
      RefusalValidSubstantiationProj: string
      RefusalValidComposerProj: string }

type private SharedLibcFixture =
    { Work: string
      Manifest: Substantiation.Manifest
      ManifestJson: string
      ProbeAbs: string
      SpecPath: string }

let private buildFixture (work: string) (packageName: string) (useCli: bool) : SeamFixture =
    let header = Path.Combine(work, "seam_probe.h")
    let source = Path.Combine(work, "seam_probe.c")
    let deadSource = Path.Combine(work, "seam_dead.c")
    let mainObj = Path.Combine(work, "seam_probe.o")
    let deadObj = Path.Combine(work, "seam_dead.o")
    let archiveDir = Path.Combine(work, "static-lib")
    let sharedDir = Path.Combine(work, "shared-lib")
    let partialSharedDir = Path.Combine(work, "partial-shared-lib")
    Directory.CreateDirectory archiveDir |> ignore
    Directory.CreateDirectory sharedDir |> ignore
    Directory.CreateDirectory partialSharedDir |> ignore
    let archive = Path.Combine(archiveDir, "libseam_probe.a")
    let sharedLib = Path.Combine(sharedDir, "libseam_probe.so")
    let partialSharedLib = Path.Combine(partialSharedDir, "libseam_probe.so")

    writeLines header
        [ "#ifndef SEAM_PROBE_H"
          "#define SEAM_PROBE_H"
          ""
          "typedef struct SeamPair {"
          "    int left;"
          "    int right;"
          "} SeamPair;"
          ""
          "struct SeamFrame {"
          "    unsigned int tag;"
          "    long payload;"
          "};"
          ""
          "typedef struct SeamHandle SeamHandle;"
          "typedef struct SeamRawToken SeamRawToken;"
          "typedef int (*SeamCallback)(unsigned int code, long delta);"
          ""
          "int seam_env_ok(void);"
          "int seam_scalar_add(int a, int b);"
          "int seam_checked_divide(int numerator, int denominator);"
          "int seam_verify_record_abi("
          "    int pair_size,"
          "    int pair_align,"
          "    int pair_left_off,"
          "    int pair_right_off,"
          "    int frame_size,"
          "    int frame_align,"
          "    int frame_tag_off,"
          "    int frame_payload_off);"
          "int seam_pair_sum(SeamPair pair);"
          "SeamPair seam_make_pair(int left, int right);"
          "SeamHandle *seam_handle_open(int seed);"
          "void seam_handle_close(SeamHandle *handle);"
          "int seam_handle_live_count(void);"
          "int seam_handle_release_count(void);"
          "int seam_handle_last_seed(void);"
          "SeamRawToken *seam_raw_token_open(int seed);"
          "long seam_frame_populate(int layout_code, void *buffer, unsigned long byte_length);"
          "int seam_frame_populated_summary(void);"
          "int seam_invoke_callback(SeamCallback callback);"
          "int seam_last_callback_result(void);"
          ""
          "#endif" ]

    writeLines source
        [ "#define _GNU_SOURCE"
          "#include \"seam_probe.h\""
          "#include <stddef.h>"
          "#include <stdint.h>"
          "#include <stdlib.h>"
          "#include <string.h>"
          "#include <sys/resource.h>"
          "#include <unistd.h>"
          ""
          "struct SeamHandle {"
          "    int magic;"
          "    int value;"
          "};"
          ""
          "struct SeamRawToken {"
          "    int value;"
          "};"
          ""
          "static int g_live_count = 0;"
          "static int g_release_count = 0;"
          "static int g_last_seed = -1;"
          "static int g_frame_populated_ok = 0;"
          "static int g_last_callback_result = -1;"
          ""
          "int seam_env_ok(void) {"
          "    struct rlimit rl;"
          "    if (getrlimit(RLIMIT_CORE, &rl) != 0) {"
          "        return 0;"
          "    }"
          "    if (rl.rlim_cur != 0) {"
          "        return 0;"
          "    }"
          "    const char *perturb = getenv(\"MALLOC_PERTURB_\");"
          "    if (perturb == NULL || strcmp(perturb, \"165\") != 0) {"
          "        return 0;"
          "    }"
          "    return 1;"
          "}"
          ""
          "int seam_scalar_add(int a, int b) {"
          "    if (!seam_env_ok()) return -999999;"
          "    return a + b;"
          "}"
          ""
          "int seam_checked_divide(int numerator, int denominator) {"
          "    if (!seam_env_ok()) return -999;"
          "    if (denominator == 0) return -22;"
          "    return numerator / denominator;"
          "}"
          ""
          "SeamPair seam_make_pair(int left, int right) {"
          "    SeamPair p = { left, right };"
          "    return p;"
          "}"
          ""
          "int seam_pair_sum(SeamPair pair) {"
          "    return pair.left + pair.right;"
          "}"
          ""
          "int seam_verify_record_abi("
          "    int pair_size,"
          "    int pair_align,"
          "    int pair_left_off,"
          "    int pair_right_off,"
          "    int frame_size,"
          "    int frame_align,"
          "    int frame_tag_off,"
          "    int frame_payload_off) {"
          "    if (!seam_env_ok()) return -1;"
          "    if ((int)sizeof(SeamPair) != pair_size) return -2;"
          "    if ((int)_Alignof(SeamPair) != pair_align) return -3;"
          "    if ((int)offsetof(SeamPair, left) != pair_left_off) return -4;"
          "    if ((int)offsetof(SeamPair, right) != pair_right_off) return -5;"
          "    if ((int)sizeof(struct SeamFrame) != frame_size) return -6;"
          "    if ((int)_Alignof(struct SeamFrame) != frame_align) return -7;"
          "    if ((int)offsetof(struct SeamFrame, tag) != frame_tag_off) return -8;"
          "    if ((int)offsetof(struct SeamFrame, payload) != frame_payload_off) return -9;"
          "    SeamPair p = seam_make_pair(19, 23);"
          "    return seam_pair_sum(p);"
          "}"
          ""
          "SeamHandle *seam_handle_open(int seed) {"
          "    if (!seam_env_ok() || seed < 0) return NULL;"
          "    struct SeamHandle *h = (struct SeamHandle *)malloc(sizeof(struct SeamHandle));"
          "    if (h != NULL) {"
          "        h->magic = 0x5EA1;"
          "        h->value = seed;"
          "        g_live_count += 1;"
          "        g_last_seed = seed;"
          "    }"
          "    return h;"
          "}"
          ""
          "void seam_handle_close(SeamHandle *handle) {"
          "    if (handle != NULL && handle->magic == 0x5EA1) {"
          "        handle->magic = 0;"
          "        free(handle);"
          "        g_live_count -= 1;"
          "        g_release_count += 1;"
          "    }"
          "}"
          ""
          "int seam_handle_live_count(void) { return g_live_count; }"
          "int seam_handle_release_count(void) { return g_release_count; }"
          "int seam_handle_last_seed(void) { return g_last_seed; }"
          ""
          "SeamRawToken *seam_raw_token_open(int seed) {"
          "    return (SeamRawToken *)seam_handle_open(seed);"
          "}"
          ""
          "long seam_frame_populate(int layout_code, void *buffer, unsigned long byte_length) {"
          "    g_frame_populated_ok = 0;"
          "    int expected_code ="
          "        (int)sizeof(struct SeamFrame) * 1000"
          "        + (int)_Alignof(struct SeamFrame) * 100"
          "        + (int)offsetof(struct SeamFrame, tag) * 10"
          "        + (int)offsetof(struct SeamFrame, payload);"
          "    if (!seam_env_ok() || buffer == NULL || byte_length != sizeof(struct SeamFrame) || layout_code != expected_code) {"
          "        return -1L;"
          "    }"
          "    unsigned char *bytes = (unsigned char *)buffer;"
          "    for (unsigned long i = 0; i < byte_length; ++i) {"
          "        if (bytes[i] != 0) {"
          "            return -1L;"
          "        }"
          "    }"
          "    memset(bytes, '-', sizeof(struct SeamFrame));"
          "    const unsigned char tag[4] = { 'S', 'E', 'A', 'M' };"
          "    const unsigned char payload[8] = { '-', 'Z', 'C', '1', '6', '8', '0', 0xFF };"
          "    memcpy(bytes + offsetof(struct SeamFrame, tag), tag, sizeof(tag));"
          "    memcpy(bytes + offsetof(struct SeamFrame, payload), payload, sizeof(payload));"
          "    g_frame_populated_ok ="
          "        memcmp(bytes + offsetof(struct SeamFrame, tag), tag, sizeof(tag)) == 0"
          "        && memcmp(bytes + offsetof(struct SeamFrame, payload), payload, sizeof(payload)) == 0 ? 1 : 0;"
          "    return 15L;"
          "}"
          ""
          "int seam_frame_populated_summary(void) {"
          "    if (!seam_env_ok() || g_frame_populated_ok != 1) {"
          "        return 0;"
          "    }"
          "    return write(1, \"seam:zero-copy:ok\\n\", 18) == 18 ? 1 : 0;"
          "}"
          ""
          "int seam_invoke_callback(SeamCallback callback) {"
          "    g_last_callback_result = -1;"
          "    if (!seam_env_ok() || callback == NULL) return -1;"
          "    int r = callback(4294967295U, -173L);"
          "    g_last_callback_result = r;"
          "    return r == 77 ? 0 : -2;"
          "}"
          ""
          "int seam_last_callback_result(void) {"
          "    if (!seam_env_ok()) return -999;"
          "    return g_last_callback_result;"
          "}" ]

    writeLines deadSource
        [ "int seam_unused_dead_symbol(int value);"
          "int seam_unused_dead_symbol(int value) {"
          "    return value * 91 + 7;"
          "}" ]

    run work "compile-seam-probe" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; source; "-o"; mainObj ]) |> ignore
    run work "compile-seam-dead" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; deadSource; "-o"; deadObj ]) |> ignore
    run work "archive-seam-probe" (ClangToolchain.arStartInfo [ "rcs"; archive; mainObj; deadObj ]) |> ignore
    run work "shared-seam-probe"
        (ClangToolchain.startInfo [ "-shared"; "-fPIC"; "-Wl,-soname,libseam_probe.so"; mainObj; deadObj; "-o"; sharedLib ]) |> ignore
    // Partial shared object omitting seam_dead.o for negative ELF symbol-in-dynsym tests (R4).
    run work "partial-shared-seam-probe"
        (ClangToolchain.startInfo [ "-shared"; "-fPIC"; "-Wl,-soname,libseam_probe.so"; mainObj; "-o"; partialSharedLib ]) |> ignore

    let sharedLibSha = ClangToolchain.sha256 sharedLib
    let partialSharedLibSha = ClangToolchain.sha256 partialSharedLib

    let pilot = Path.Combine(work, "SeamProbe.pilot.toml")
    writeLines pilot
        [ "[library]"
          "name = \"seam_probe\""
          "headers = [" + quote header + "]"
          "include_paths = [" + quote work + "]"
          "[library.artifact]"
          "kind = \"static-archive\""
          "path = " + quote archive
          "sha256 = " + quote (ClangToolchain.sha256 archive)
          "[output]"
          "mode = \"fidelity\""
          "directory = \"generated\""
          "[error_conventions]"
          "default = \"return_code\""
          "[error_conventions.overrides]"
          "seam_handle_open = \"none\""
          "seam_handle_close = \"none\""
          "seam_raw_token_open = \"none\""
          "seam_frame_populate = \"none\""
          "seam_frame_populated_summary = \"none\""
          "seam_invoke_callback = \"none\""
          "seam_last_callback_result = \"none\""
          "[annotations.nonnull]"
          "seam_handle_close = [0]"
          "seam_invoke_callback = [0]"
          "[options]"
          "c_header_mode = true"
          "opaque_handles = true"
          "descriptor_only_dependencies = true"
          "link_libraries = true"
          "value_structs = [\"SeamPair\"]"
          "abi_critical_structs = [\"SeamPair\", \"struct SeamFrame\"]"
          "[[options.bindings]]"
          "name = \"seam_env_ok\""
          "symbol = \"seam_env_ok\""
          "result_range = { identity = \"seam.env_ok_boolean\", version = \"1\", source = \"seam_probe.c\", lower = { constant = 0 }, upper = { constant = 1 } }"
          "[[options.bindings]]"
          "name = \"seam_handle_open\""
          "symbol = \"seam_handle_open\""
          "resource = { kind = \"owned_nullable_return\", release_binding = \"Seam.Probe.Boundary.seam_handle_close\", release_parameter = 0, hypothesis = { identity = \"seam_probe.seam_handle.owned\", version = \"1\", source = \"seam_probe.c\" } }"
          "[[options.bindings]]"
          "name = \"seam_frame_populate\""
          "symbol = \"seam_frame_populate\""
          "reference_parameters = [1]"
          "reference_elements = [\"\", \"unsigned char\"]"
          "writable_buffers = [{ parameter = 1, count_parameter = 2, hypothesis = { identity = \"seam_probe.frame_populate.borrow\", version = \"1\", source = \"seam_probe.c\" } }]"
          "result_range = { identity = \"seam_probe.frame_populate.range\", version = \"1\", source = \"seam_probe.c\", lower = { constant = -1 }, upper = { argument = 2 } }"
          "[[options.bindings]]"
          "name = \"seam_frame_populated_summary\""
          "symbol = \"seam_frame_populated_summary\""
          "result_range = { identity = \"seam_probe.frame_populated_summary.range\", version = \"1\", source = \"seam_probe.c\", lower = { constant = 0 }, upper = { constant = 1 } }"
          "[[options.bindings]]"
          "name = \"seam_invoke_callback\""
          "symbol = \"seam_invoke_callback\""
          "callback_invocations = [{ parameter = 0, identity = \"seam_probe.seam_invoke_callback.callback\", version = \"1\", source = \"seam_probe.c\", scope = \"during_call\" }]"
          "[[namespace]]"
          "name = \"Seam.Probe.Boundary\""
          "library = \"seam_probe\""
          "description = \"Full-strata physical seam probe library\""
          "functions = ["
          "    \"seam_env_ok\", \"seam_scalar_add\", \"seam_checked_divide\", \"seam_verify_record_abi\","
          "    \"seam_pair_sum\", \"seam_make_pair\","
          "    \"seam_handle_open\", \"seam_handle_close\", \"seam_handle_live_count\", \"seam_handle_release_count\", \"seam_handle_last_seed\","
          "    \"seam_raw_token_open\","
          "    \"seam_frame_populate\", \"seam_frame_populated_summary\","
          "    \"seam_last_callback_result\""
          "]"
          "[[namespace]]"
          "name = \"Seam.Probe.Callbacks\""
          "library = \"seam_probe\""
          "description = \"Callback invocation contract surface\""
          "functions = [\"seam_invoke_callback\"]" ]

    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let composerDll = sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll"

    if useCli then
        run work "cli-project-main" (ProcessLaunch.startInfo "dotnet" [ cli; "project"; "--project"; pilot ]) |> ignore
    else
        ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64)
        |> accepted |> ignore

    let provenance =
        match Directory.GetFiles(Path.Combine(work, "generated"), "*.link-provenance.toml", SearchOption.AllDirectories) with
        | [| path |] -> path
        | paths -> failwithf "expected one generated link provenance manifest, got %A" paths

    let boundary = requiredFile "generated Boundary.clef" (Path.Combine(work, "generated", "Boundary", "Boundary.clef"))
    let callbacks = requiredFile "generated Callbacks.clef" (Path.Combine(work, "generated", "Callbacks", "Callbacks.clef"))
    let generatedSources =
        [ Path.Combine(work, "generated", "Types.clef")
          Path.Combine(work, "generated", "ReturnCode.clef")
          Path.Combine(work, "generated", "Boundary", "Types.clef")
          boundary
          Path.Combine(work, "generated", "Callbacks", "Types.clef")
          callbacks ]
        |> List.filter File.Exists
    let types =
        generatedSources
        |> List.find (fun p -> Path.GetFileName p = "Types.clef" && (File.ReadAllText p).Contains("SeamPair"))

    let loading = Path.Combine(work, "Loading.clef")
    (File.ReadAllText(Path.Combine(repository, "tests", "native-callbacks", "Loading.clef"))).Replace("landingprobe", "seam_probe")
    |> fun text -> File.WriteAllText(loading, text, UTF8Encoding(false))

    let caller = Path.Combine(work, "Caller.clef")
    writeLines caller
        [ "module Seam.Probe.Caller"
          "open Seam.Probe.Types"
          "open Seam.Probe.Boundary"
          "open Seam.Probe.Callbacks"
          "let private scalarEntry (value: int) (delta: int) ="
          "    if value = 4294967295 && delta = -173 then 77 else -9"
          "let private invokeScalar () : int ="
          "    seam_invoke_callback { Callback_Seam_002E_Probe_002E_Callbacks_003A__003A_SeamCallbackEntry.Invoke = FnPtr.ofFunction scalarEntry }"
          "let envSample () : int ="
          "    match seam_env_ok () with"
          "    | 1 -> 1"
          "    | _ -> 0"
          "let layoutSummary () : int ="
          "    SeamPair.Size + SeamPair.rightOffset + SeamFrame.Size + SeamFrame.payloadOffset"
          "let callbackSample () : int ="
          "    let before = seam_last_callback_result ()"
          "    let rc = invokeScalar ()"
          "    let after = seam_last_callback_result ()"
          "    if before = -1 && rc = 0 && after = 77 then 77 else -9" ]

    let dependencies, platform = platformTables ()
    let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
    let fidproj = Path.Combine(work, packageName + ".fidproj")
    writeLines fidproj
        [ "[package]"
          "name = " + quote packageName
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
          "libraries = [\"seam_probe\"]" ]

    let manifestDir = Path.Combine(work, "manifest")
    let manifest =
        if useCli then
            let launch =
                ProcessLaunch.startInfo "dotnet"
                    [ cli; "substantiate"; "--fidproj"; fidproj; "--pilot"; pilot
                      "--link-provenance"; provenance; "--output"; manifestDir
                      "--work-dir"; Path.Combine(work, "substantiation") ]
            launch.Environment["COMPOSER_DLL"] <- composerDll
            run work "cli-substantiate" launch |> ignore
            Substantiation.parseJson (File.ReadAllText(Path.Combine(manifestDir, packageName + ".substantiation.json"))) |> accepted
        else
            Substantiation.evaluate
                { FidprojPath = fidproj
                  PilotPath = Some pilot
                  LinkProvenancePath = Some provenance
                  OutputDirectory = Some manifestDir
                  WorkDirectory = Some(Path.Combine(work, "substantiation"))
                  AllowWarnings = false
                  Verbose = false }
            |> accepted

    let manifestJson = requiredFile "manifest JSON" (Path.Combine(manifestDir, packageName + ".substantiation.json"))
    let manifestToml = requiredFile "manifest TOML" (Path.Combine(manifestDir, packageName + ".substantiation.toml"))

    // -----------------------------------------------------------------------
    // 7 physical seam probes covering every physical category in RequiredSeamChecks
    // -----------------------------------------------------------------------
    let probesDir = Path.Combine(work, "probes")
    Directory.CreateDirectory probesDir |> ignore

    // 1. scalar-handle-link-and-call
    let probeScalar = Path.Combine(probesDir, "ProbeScalar.clef")
    writeLines probeScalar
        [ "module Seam.Probe.RunScalar"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let env = Seam.Probe.Boundary.seam_env_ok ()"
          "    let sum = Seam.Probe.Boundary.seam_scalar_add 19 23"
          "    if env = 1 && sum = 42 then"
          "        Console.writeln \"seam:scalar:ok\""
          "        0"
          "    else 1" ]

    // 2. measured-record-abi (directly consumes #216's unmodified generated Types.clef)
    let probeRecord = Path.Combine(probesDir, "ProbeRecordAbi.clef")
    writeLines probeRecord
        [ "module Seam.Probe.RunRecordAbi"
          "open Seam.Probe.Types"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let rc ="
          "        Seam.Probe.Boundary.seam_verify_record_abi"
          "            SeamPair.Size"
          "            SeamPair.Alignment"
          "            SeamPair.leftOffset"
          "            SeamPair.rightOffset"
          "            SeamFrame.Size"
          "            SeamFrame.Alignment"
          "            SeamFrame.tagOffset"
          "            SeamFrame.payloadOffset"
          "    if rc = 42 then"
          "        Console.writeln \"seam:record-abi:ok\""
          "        0"
          "    else 2" ]

    // 3. static-dynamic-link-elision (Clef static probe + C dynamic comparison probe)
    let probeLink = Path.Combine(probesDir, "ProbeLinkElision.clef")
    writeLines probeLink
        [ "module Seam.Probe.RunLinkElision"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    if Seam.Probe.Boundary.seam_scalar_add 20 22 = 42 then"
          "        Console.writeln \"seam:link-elision:ok\""
          "        0"
          "    else 3" ]

    let dynamicProbeC = Path.Combine(probesDir, "dynamic_probe.c")
    writeLines dynamicProbeC
        [ "#include " + quote header
          "int main(void) {"
          "    return seam_scalar_add(20, 22) == 42 ? 0 : 1;"
          "}" ]

    // 4. error-sentinel-conformance
    let probeError = Path.Combine(probesDir, "ProbeErrorSentinel.clef")
    writeLines probeError
        [ "module Seam.Probe.RunErrorSentinel"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let zeroErr = Seam.Probe.Boundary.seam_checked_divide 18 0"
          "    let zeroNum = Seam.Probe.Boundary.seam_checked_divide 0 9"
          "    let posQuot = Seam.Probe.Boundary.seam_checked_divide 42 7"
          "    if zeroErr = -22 && zeroNum = 0 && posQuot = 6 then"
          "        Console.writeln \"seam:error-sentinel:ok\""
          "        0"
          "    else 4" ]

    // 5. owned-handle-lifecycle (real owned_nullable_return CHandle<SeamHandle> open/close)
    let probeLifecycle = Path.Combine(probesDir, "ProbeLifecycle.clef")
    writeLines probeLifecycle
        [ "module Seam.Probe.RunLifecycle"
          "let private checkDenied () : int ="
          "    match Seam.Probe.Boundary.seam_handle_open (-5) with"
          "    | Some leaked ->"
          "        Seam.Probe.Boundary.seam_handle_close leaked"
          "        0"
          "    | None -> 1"
          ""
          "let private openAndClose () : int ="
          "    match Seam.Probe.Boundary.seam_handle_open 77 with"
          "    | Some handle ->"
          "        Seam.Probe.Boundary.seam_handle_close handle"
          "        1"
          "    | None -> 0"
          ""
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let liveBefore = Seam.Probe.Boundary.seam_handle_live_count ()"
          "    let deniedOk = checkDenied ()"
          "    let liveAfterDenied = Seam.Probe.Boundary.seam_handle_live_count ()"
          "    let openedOk = openAndClose ()"
          "    let liveAfter = Seam.Probe.Boundary.seam_handle_live_count ()"
          "    let relCount = Seam.Probe.Boundary.seam_handle_release_count ()"
          "    let seedSeen = Seam.Probe.Boundary.seam_handle_last_seed ()"
          "    if liveBefore = 0 && deniedOk = 1 && liveAfterDenied = 0 && openedOk = 1 && liveAfter = 0 && relCount = 1 && seedSeen = 77 then"
          "        Console.writeln \"seam:lifecycle:ok\""
          "        0"
          "    else 5" ]

    // 6. barewire-zero-copy (BAREWire WritableBufferDescriptor + SeamFrame layout offsets + in-place buffer write)
    let probeZeroCopy = Path.Combine(probesDir, "ProbeZeroCopy.clef")
    writeLines probeZeroCopy
        [ "module Seam.Probe.RunZeroCopy"
          "open Seam.Probe.Types"
          ""
          "let private borrowSeamFrame () : int ="
          "    let buffer = Array.zeroCreate 16"
          "    let result = Seam.Probe.Boundary.seam_frame_populate 16808 buffer 16"
          "    if result < 0 then 11"
          "    elif result = 0 then 12"
          "    else"
          "        let prefix = eager (Array.sub buffer 0 result)"
          "        match String.tryFromBytes prefix with"
          "        | ValueSome text -> text.Length"
          "        | ValueNone -> 14"
          ""
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let layoutCode ="
          "        SeamFrame.Size * 1000"
          "        + SeamFrame.Alignment * 100"
          "        + SeamFrame.tagOffset * 10"
          "        + SeamFrame.payloadOffset"
          "    let borrowLen = borrowSeamFrame ()"
          "    if layoutCode = 16808 && borrowLen = 15 then"
          "        if Seam.Probe.Boundary.seam_frame_populated_summary () = 1 then 0"
          "        else 65"
          "    else 64" ]

    // 7. callback-registration-invocation
    let probeCallbackMain = Path.Combine(probesDir, "ProbeCallback.clef")
    writeLines probeCallbackMain
        [ "module Seam.Probe.RunCallback"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    if Seam.Probe.Caller.envSample () = 1"
          "       && Seam.Probe.Caller.layoutSummary () = 36"
          "       && Seam.Probe.Caller.callbackSample () = 77 then"
          "        Console.writeln \"seam:callback:ok\""
          "        0"
          "    else 7" ]

    // -----------------------------------------------------------------------
    // 5 live refusal controls (FS9228, FS9246, FS9244, CCS8010, CCS8403)
    // -----------------------------------------------------------------------
    let controlsDir = Path.Combine(work, "controls")
    Directory.CreateDirectory controlsDir |> ignore

    // Control 1: pilot missing symbol in static archive -> FS9228
    let missingHeader = Path.Combine(controlsDir, "missing_sym.h")
    writeLines missingHeader [ "int seam_nonexistent_symbol(int value);" ]
    let refusalPilotMissingSym = Path.Combine(controlsDir, "MissingSym.pilot.toml")
    writeLines refusalPilotMissingSym
        [ "[library]"
          "name = \"seam_probe\""
          "headers = [" + quote missingHeader + "]"
          "include_paths = [" + quote controlsDir + "]"
          "[library.artifact]"
          "kind = \"static-archive\""
          "path = " + quote archive
          "sha256 = " + quote (ClangToolchain.sha256 archive)
          "[output]"
          "mode = \"fidelity\""
          "directory = \"generated-refused\""
          "[options]"
          "c_header_mode = true"
          "[[namespace]]"
          "name = \"Seam.Refusal.Missing\""
          "library = \"seam_probe\""
          "description = \"Missing archive symbol negative control\""
          "functions = [\"seam_nonexistent_symbol\"]" ]

    // Control 2: substantiation parameterized callable -> FS9246
    let refusalSubCallableClef = Path.Combine(controlsDir, "BadCallable.clef")
    writeLines refusalSubCallableClef
        [ "module Seam.Refusal.BadCallable"
          "let unactivatable (value: int) : int = value + 1" ]
    let refusalSubCallableProj = Path.Combine(controlsDir, "BadCallable.fidproj")
    writeLines refusalSubCallableProj
        [ "[package]"
          "name = \"BadCallable\""
          "version = \"1.0.0\""
          "[compilation]"
          "target = \"cpu\""
          "[build]"
          "output_kind = \"library\""
          "sources = [" + quote refusalSubCallableClef + "]" ]

    // Control 3: substantiation unmeasured foreign record -> FS9244
    let refusalSubRecordClef = Path.Combine(controlsDir, "UnmeasuredRecord.clef")
    writeLines refusalSubRecordClef
        [ "module Seam.Refusal.UnmeasuredRecord"
          "type UnmeasuredPair = { a: int; b: int }"
          "[<FidelityExtern(\"c\", \"div\")>]"
          "let div (n: int) (d: int) : UnmeasuredPair = NativeDefault.zeroed ()" ]
    let refusalSubRecordProj = Path.Combine(controlsDir, "UnmeasuredRecord.fidproj")
    writeLines refusalSubRecordProj
        [ "[package]"
          "name = \"UnmeasuredRecord\""
          "version = \"1.0.0\""
          "[compilation]"
          "target = \"cpu\""
          "[build]"
          "output_kind = \"library\""
          "sources = [" + quote refusalSubRecordClef + "]"
          "[platform]"
          "os = \"linux\""
          "arch = \"x86_64\""
          "[link]"
          "libraries = [\"c\"]" ]

    // Control 4: composer forged non-null CHandle -> CCS8010
    let refusalForgedClef = Path.Combine(controlsDir, "ForgedHandle.clef")
    writeLines refusalForgedClef
        [ "module Seam.Refusal.ForgedHandle"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let forged: CHandle<unit> = NativeDefault.zeroed ()"
          "    if forged = forged then 0 else 1" ]
    let refusalForgedProj = Path.Combine(controlsDir, "ForgedHandle.fidproj")
    writeLines refusalForgedProj
        [ "[package]"
          "name = \"ForgedHandle\""
          "version = \"0.1.0\""
          "[compilation]"
          "target = \"cpu\""
          "[dependencies]"
          yield! dependencies
          "metadata = { path = " + quote metadata + " }"
          "[build]"
          "output_kind = \"console\""
          "sources = [" + quote refusalForgedClef + "]"
          "[platform]"
          yield! platform ]

    // Control 5: composer unsettled CHandle foreign boundary call -> CCS8403
    let refusalUnsettledClef = Path.Combine(controlsDir, "UnsettledBoundary.clef")
    writeLines refusalUnsettledClef
        [ "module Seam.Refusal.UnsettledBoundary"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    match Seam.Probe.Boundary.seam_raw_token_open 1 with"
          "    | None -> 1"
          "    | Some token ->"
          "        if token = token then 0 else 2" ]
    let refusalUnsettledProj = Path.Combine(controlsDir, "UnsettledBoundary.fidproj")
    writeLines refusalUnsettledProj
        [ "[package]"
          "name = \"UnsettledBoundary\""
          "version = \"0.1.0\""
          "[compilation]"
          "target = \"cpu\""
          "[dependencies]"
          yield! dependencies
          "metadata = { path = " + quote metadata + " }"
          "[build]"
          "output_kind = \"console\""
          "sources = [" + ([ loading; types; boundary; refusalUnsettledClef ] |> List.map quote |> String.concat ", ") + "]"
          "[platform]"
          yield! platform
          "loading = \"Landing.Acceptance.Loading.contract\""
          "[link]"
          "libraries = [\"seam_probe\"]"
          "archives = { seam_probe = " + quote archive + " }" ]

    // Multi-code Composer target emitting BOTH CCS8401 (raw address) and CCS8010 (forged CHandle) for R3 superset refusal tests.
    let refusalSupersetClef = Path.Combine(controlsDir, "SupersetControl.clef")
    writeLines refusalSupersetClef
        [ "module Seam.Refusal.SupersetControl"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    let mutable cell = 17"
          "    let rawAddr = &&cell"
          "    let forged: CHandle<unit> = NativeDefault.zeroed ()"
          "    if rawAddr = rawAddr && forged = forged then 0 else 1" ]
    let refusalSupersetComposerProj = Path.Combine(controlsDir, "SupersetControl.fidproj")
    writeLines refusalSupersetComposerProj
        [ "[package]"
          "name = \"SupersetControl\""
          "version = \"0.1.0\""
          "[compilation]"
          "target = \"cpu\""
          "[dependencies]"
          yield! dependencies
          "metadata = { path = " + quote metadata + " }"
          "[build]"
          "output_kind = \"console\""
          "sources = [" + quote refusalSupersetClef + "]"
          "[platform]"
          yield! platform ]

    // Valid artifacts for negative-of-negative tests (controls that unexpectedly pass -> FS9253)
    let refusalValidPilot = Path.Combine(controlsDir, "ValidControl.pilot.toml")
    writeLines refusalValidPilot
        [ "[library]"
          "name = \"seam_probe\""
          "headers = [" + quote header + "]"
          "include_paths = [" + quote work + "]"
          "[output]"
          "mode = \"fidelity\""
          "directory = \"generated-valid-control\""
          "[options]"
          "c_header_mode = true"
          "[[namespace]]"
          "name = \"Seam.ValidControl\""
          "library = \"seam_probe\""
          "description = \"Valid pilot\""
          "functions = [\"seam_env_ok\"]" ]

    let validSubClef = Path.Combine(controlsDir, "ValidSub.clef")
    writeLines validSubClef
        [ "module Seam.ValidSub"
          "let seven () : int = 7" ]
    let refusalValidSubstantiationProj = Path.Combine(controlsDir, "ValidSub.fidproj")
    writeLines refusalValidSubstantiationProj
        [ "[package]"
          "name = \"ValidSub\""
          "version = \"1.0.0\""
          "[compilation]"
          "target = \"cpu\""
          "[dependencies]"
          yield! dependencies
          "[build]"
          "output_kind = \"library\""
          "sources = [" + quote validSubClef + "]"
          "[platform]"
          yield! platform ]

    let validCompClef = Path.Combine(controlsDir, "ValidComp.clef")
    writeLines validCompClef
        [ "module Seam.ValidComp"
          "[<EntryPoint>]"
          "let main _ : int = 0" ]
    let refusalValidComposerProj = Path.Combine(controlsDir, "ValidComp.fidproj")
    writeLines refusalValidComposerProj
        [ "[package]"
          "name = \"ValidComp\""
          "version = \"0.1.0\""
          "[compilation]"
          "target = \"cpu\""
          "[dependencies]"
          yield! dependencies
          "[build]"
          "output_kind = \"console\""
          "sources = [" + quote validCompClef + "]"
          "[platform]"
          yield! platform ]

    // -----------------------------------------------------------------------
    // Seam suite specification (.seams.toml)
    // -----------------------------------------------------------------------
    let specPath = Path.Combine(work, packageName + ".seams.toml")
    writeLines specPath
        [ "schema_version = 1"
          "target_triple = " + quote manifest.Boundary.ClangTarget
          "loading_contract = \"Landing.Acceptance.Loading.contract\""
          ""
          "[[checks]]"
          "id = \"seam-scalar-call\""
          "category = \"scalar-handle-link-and-call\""
          "summary = \"Scalar foreign calls cross the static archive boundary with RLIMIT_CORE=0 and MALLOC_PERTURB_=165.\""
          "entry_source = " + quote probeScalar
          "required_symbols = [\"seam_env_ok\", \"seam_scalar_add\"]"
          "expected_stdout = \"seam:scalar:ok\""
          ""
          "[[checks]]"
          "id = \"seam-measured-record-abi\""
          "category = \"measured-record-abi\""
          "summary = \"Generated Types.clef sizeof, alignof and field offsets match compiled C struct layout for SeamPair and SeamFrame.\""
          "entry_source = " + quote probeRecord
          "required_symbols = [\"seam_verify_record_abi\"]"
          "expected_stdout = \"seam:record-abi:ok\""
          ""
          "[[checks]]"
          "id = \"seam-link-elision\""
          "category = \"static-dynamic-link-elision\""
          "summary = \"Static archive link elides unreferenced dead object symbols while paired shared library link retains dynamic imports.\""
          "entry_source = " + quote probeLink
          "required_symbols = [\"seam_scalar_add\"]"
          "expected_stdout = \"seam:link-elision:ok\""
          "[checks.elf]"
          "elided_symbols = [\"seam_unused_dead_symbol\"]"
          "retained_static_symbols = [\"seam_scalar_add\"]"
          "forbidden_needed_libraries = [\"libseam_probe.so\"]"
          "shared_library = " + quote sharedLib
          "shared_library_sha256 = " + quote sharedLibSha
          "dynamic_probe_source = " + quote dynamicProbeC
          "retained_dynamic_imports = [\"seam_scalar_add\"]"
          ""
          "[[checks]]"
          "id = \"seam-error-sentinel\""
          "category = \"error-sentinel-conformance\""
          "summary = \"Foreign return-code sentinel (-22), zero numerator (0), and positive quotient (6) preserve signedness and value.\""
          "entry_source = " + quote probeError
          "required_symbols = [\"seam_checked_divide\"]"
          "expected_stdout = \"seam:error-sentinel:ok\""
          ""
          "[[checks]]"
          "id = \"seam-owned-lifecycle\""
          "category = \"owned-handle-lifecycle\""
          "summary = \"Owned nullable CHandle<SeamHandle> refuses negative seed with None, opens live heap handle, and closes to zero live count.\""
          "entry_source = " + quote probeLifecycle
          "required_symbols = [\"seam_handle_open\", \"seam_handle_close\", \"seam_handle_live_count\", \"seam_handle_release_count\", \"seam_handle_last_seed\"]"
          "expected_stdout = \"seam:lifecycle:ok\""
          ""
          "[[checks]]"
          "id = \"seam-barewire-zero-copy\""
          "category = \"barewire-zero-copy\""
          "summary = \"Clef borrows a 16-byte zero-copy SeamFrame buffer across BAREWire WritableBufferDescriptor, C verifies layout and zeroed input and populates SeamFrame in place, and Clef observes the exact populated bytes via zero-copy prefix extraction.\""
          "entry_source = " + quote probeZeroCopy
          "required_symbols = [\"seam_frame_populate\", \"seam_frame_populated_summary\"]"
          "expected_stdout = \"seam:zero-copy:ok\""
          ""
          "[[checks]]"
          "id = \"seam-callback-invocation\""
          "category = \"callback-registration-invocation\""
          "summary = \"Clef FnPtr.ofFunction callback entry passes unsigned 32-bit and signed 64-bit values from C and returns scalar result.\""
          "entry_source = " + quote probeCallbackMain
          "include_runtime_sources = true"
          "required_symbols = [\"seam_invoke_callback\", \"seam_last_callback_result\"]"
          "expected_stdout = \"seam:callback:ok\""
          ""
          "[[refusal_controls]]"
          "id = \"refusal-pilot-missing-symbol\""
          "kind = \"pilot\""
          "target = " + quote refusalPilotMissingSym
          "expected_code = \"FS9228\""
          "misuse_summary = \"Pilot requests a foreign function symbol that no member of the static archive defines.\""
          "protection_summary = \"Farscape static archive symbol provenance refuses binding generation with FS9228.\""
          ""
          "[[refusal_controls]]"
          "id = \"refusal-substantiation-callable\""
          "kind = \"substantiation\""
          "target = " + quote refusalSubCallableProj
          "expected_code = \"FS9246\""
          "misuse_summary = \"Library exports a parameterized non-extern callable whose domain cannot be universally activated by root touch.\""
          "protection_summary = \"Substantiation refuses unactivatable library declarations with FS9246.\""
          ""
          "[[refusal_controls]]"
          "id = \"refusal-substantiation-unmeasured-record\""
          "kind = \"substantiation\""
          "target = " + quote refusalSubRecordProj
          "expected_code = \"FS9244\""
          "misuse_summary = \"Foreign extern returns a record type without Clang per-unit ABI layout measurement.\""
          "protection_summary = \"SubstantiationBoundary refuses unmeasured foreign records with FS9244.\""
          ""
          "[[refusal_controls]]"
          "id = \"refusal-composer-forged-handle\""
          "kind = \"composer\""
          "target = " + quote refusalForgedProj
          "expected_code = \"CCS8010\""
          "misuse_summary = \"Source attempts to fabricate a non-null CHandle<unit> via NativeDefault.zeroed ().\""
          "protection_summary = \"Clef compiler rejects forged non-null handles and function pointers with CCS8010.\""
          ""
          "[[refusal_controls]]"
          "id = \"refusal-composer-unsettled-handle-call\""
          "kind = \"composer\""
          "target = " + quote refusalUnsettledProj
          "expected_code = \"CCS8403\""
          "misuse_summary = \"Console program demands a foreign CHandle boundary call without a settled source adapter.\""
          "protection_summary = \"Clef BoundaryEmission rejects unsettled non-scalar boundary calls with CCS8403.\"" ]

    { Work = work
      Header = header
      Source = source
      Archive = archive
      SharedLib = sharedLib
      SharedLibSha256 = sharedLibSha
      PartialSharedLib = partialSharedLib
      PartialSharedLibSha256 = partialSharedLibSha
      MainObject = mainObj
      DeadObject = deadObj
      Pilot = pilot
      Provenance = provenance
      Fidproj = fidproj
      Manifest = manifest
      ManifestJson = manifestJson
      ManifestToml = manifestToml
      SpecPath = specPath
      DynamicProbeC = dynamicProbeC
      ProbeScalar = probeScalar
      RefusalPilotMissingSym = refusalPilotMissingSym
      RefusalUnsettledProj = refusalUnsettledProj
      RefusalSupersetComposerProj = refusalSupersetComposerProj
      RefusalValidPilot = refusalValidPilot
      RefusalValidSubstantiationProj = refusalValidSubstantiationProj
      RefusalValidComposerProj = refusalValidComposerProj }

let private sharedFixture = lazy (buildFixture (scratch "shared-full-strata") "SeamProbe" false)

let private sharedLibcFixture =
    lazy (
        let work = scratch "shared-libc-fixture"
        let header = Path.Combine(work, "libc_abs.h")
        writeLines header [ "int abs(int value);" ]
        let pilot = Path.Combine(work, "LibcAbs.pilot.toml")
        writeLines pilot
            [ "[library]"
              "name = \"c\""
              "headers = [" + quote header + "]"
              "include_paths = [" + quote work + "]"
              "[output]"
              "mode = \"fidelity\""
              "directory = \"generated\""
              "[options]"
              "c_header_mode = true"
              "descriptor_only_dependencies = true"
              "link_libraries = true"
              "[[namespace]]"
              "name = \"Seam.LibcAbs.Boundary\""
              "library = \"c\""
              "description = \"Minimal Clang-measured runtime-shared-library fixture\""
              "functions = [\"abs\"]" ]
        ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64)
        |> accepted |> ignore
        let libcTypes = requiredFile "generated libc Types.clef" (Path.Combine(work, "generated", "Types.clef"))
        let boundary = requiredFile "generated libc Boundary.clef" (Path.Combine(work, "generated", "Boundary", "Boundary.clef"))
        let dependencies, platform = platformTables ()
        let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
        let fidproj = Path.Combine(work, "LibcAbs.fidproj")
        writeLines fidproj
            [ "[package]"
              "name = \"LibcAbs\""
              "version = \"1.0.0\""
              "[compilation]"
              "target = \"cpu\""
              "[build]"
              "output_kind = \"library\""
              "sources = [" + ([ libcTypes; boundary ] |> List.map quote |> String.concat ", ") + "]"
              "[dependencies]"
              yield! dependencies
              "metadata = { path = " + quote metadata + " }"
              "[platform]"
              yield! platform
              "[link]"
              "libraries = [\"c\"]" ]
        let manifestDir = Path.Combine(work, "manifest")
        let manifest =
            Substantiation.evaluate
                { FidprojPath = fidproj
                  PilotPath = Some pilot
                  LinkProvenancePath = None
                  OutputDirectory = Some manifestDir
                  WorkDirectory = Some(Path.Combine(work, "substantiation"))
                  AllowWarnings = false
                  Verbose = false }
            |> accepted
        Assert.Equal("runtime-shared-library", manifest.Boundary.LinkMode)
        Assert.NotEqual<string>("not-measured", manifest.Boundary.ClangTarget)
        let manifestJson = requiredFile "libc manifest JSON" (Path.Combine(manifestDir, "LibcAbs.substantiation.json"))
        let probeAbs = Path.Combine(work, "ProbeAbs.clef")
        writeLines probeAbs
            [ "module Seam.LibcAbs.RunScalar"
              "[<EntryPoint>]"
              "let main _ : int ="
              "    if Seam.LibcAbs.Boundary.abs (-42) = 42 then"
              "        Console.writeln \"seam:libc-abs:ok\""
              "        0"
              "    else 1" ]
        let specPath = Path.Combine(work, "LibcAbs.seams.toml")
        writeLines specPath
            [ "schema_version = 1"
              "target_triple = " + quote manifest.Boundary.ClangTarget
              "[[checks]]"
              "id = \"libc-abs-call\""
              "category = \"scalar-handle-link-and-call\""
              "summary = \"Runtime shared library abs(-42) = 42.\""
              "entry_source = " + quote probeAbs
              "expected_stdout = \"seam:libc-abs:ok\"" ]
        { Work = work
          Manifest = manifest
          ManifestJson = manifestJson
          ProbeAbs = probeAbs
          SpecPath = specPath })

let private notMeasuredManifest =
    lazy (
        let fx = sharedFixture.Value
        let outDir = scratch "not-measured-manifest"
        let workDir = scratch "not-measured-substantiation"
        let manifest =
            Substantiation.evaluate
                { FidprojPath = fx.RefusalValidSubstantiationProj
                  PilotPath = None
                  LinkProvenancePath = None
                  OutputDirectory = Some outDir
                  WorkDirectory = Some workDir
                  AllowWarnings = false
                  Verbose = false }
            |> accepted
        Assert.Equal("not-measured", manifest.Boundary.ClangTarget)
        manifest, requiredFile "not-measured manifest JSON" (Path.Combine(outDir, "ValidSub.substantiation.json")))

[<Fact>]
let ``evaluate runs all eight seam categories and produces dossier-ready receipts with SUBSTANTIATED overall status`` () =
    let fx = sharedFixture.Value
    let expectedCategories =
        [| "scalar-handle-link-and-call"
           "measured-record-abi"
           "static-dynamic-link-elision"
           "error-sentinel-conformance"
           "owned-handle-lifecycle"
           "barewire-zero-copy"
           "callback-registration-invocation"
           "falsification-refusal-controls" |]
    Assert.Equal<string[]>(expectedCategories, fx.Manifest.Boundary.RequiredSeamChecks)

    let runOut = scratch "evaluate-out"
    let runWork = scratch "evaluate-work"
    let parentPerturbBefore = Environment.GetEnvironmentVariable "MALLOC_PERTURB_"

    let generated =
        SubstantiationSeams.evaluate
            { ManifestPath = fx.ManifestJson
              SpecPath = fx.SpecPath
              OutputDirectory = Some runOut
              WorkDirectory = Some runWork
              Verbose = false }
        |> accepted

    let parentPerturbAfter = Environment.GetEnvironmentVariable "MALLOC_PERTURB_"
    Assert.Equal(parentPerturbBefore, parentPerturbAfter)

    let directDischarge = SubstantiationSeams.verifyDischarge fx.Manifest |> accepted
    Assert.Equal(directDischarge, generated.Discharge)
    Assert.True(generated.Discharge.TotalObligations > 0)
    Assert.Equal(generated.Discharge.TotalObligations, generated.Discharge.DischargedUnsatCount)
    Assert.Equal(generated.Discharge.TotalObligations, generated.Discharge.PremisesSatCount)
    Assert.True(generated.Discharge.LibraryObligations > 0)
    Assert.Equal(generated.Discharge.LibraryObligations, generated.Discharge.LibraryDischargedUnsatCount)
    Assert.Equal(generated.Discharge.LibraryObligations, generated.Discharge.LibraryPremisesSatCount)
    Assert.Equal(0, generated.Discharge.PendingOpenCount)
    Assert.Equal(0, generated.Discharge.RefutedCount)

    Assert.Equal(7, generated.SeamChecks.Checks.Length)
    Assert.Equal(5, generated.RefusalControls.Controls.Length)
    Assert.All(generated.RefusalControls.Controls, fun ctrl -> Assert.Equal(ctrl.ExpectedCode, ctrl.ObservedCode))
    Assert.Equal<string[]>(
        [| "FS9228"; "FS9246"; "FS9244"; "CCS8010"; "CCS8403" |],
        generated.RefusalControls.Controls |> Array.map _.ObservedCode)

    let sharedComparison = Assert.Single generated.Ledger.SharedLibraries
    Assert.Equal("seam-link-elision", sharedComparison.CheckId)
    Assert.Equal(fx.SharedLibSha256, sharedComparison.SharedLibrarySha256)
    Assert.Equal(64, sharedComparison.DynamicProbeBinarySha256.Length)

    let parsedSeam = SubstantiationDossier.readSeamReceipt fx.Manifest generated.SeamChecksPath |> accepted
    let parsedRefusal = SubstantiationDossier.readRefusalReceipt fx.Manifest generated.RefusalControlsPath |> accepted
    let parsedLedger = SubstantiationSeams.readLedgerReceipt fx.Manifest generated.LedgerPath |> accepted

    Assert.Equal(7, parsedSeam.Checks.Length)
    Assert.Equal(5, parsedRefusal.Controls.Length)
    Assert.All(parsedRefusal.Controls, fun ctrl -> Assert.Equal(ctrl.ExpectedCode, ctrl.ObservedCode))
    Assert.Equal(SubstantiationSeams.LedgerSchema, parsedLedger.Schema)
    Assert.Equal(SubstantiationSeams.SeamChecksSchema, parsedLedger.SeamChecksSchema)
    Assert.Equal(SubstantiationSeams.RefusalControlsSchema, parsedLedger.RefusalControlsSchema)
    Assert.Equal(fx.Manifest.IntegritySha256, parsedLedger.ManifestIntegritySha256)
    Assert.Equal(fx.SharedLibSha256, (Assert.Single parsedLedger.SharedLibraries).SharedLibrarySha256)

    let dossierOut = scratch "evaluate-dossier"
    let dossierRes =
        SubstantiationDossier.generate
            { ManifestPath = fx.ManifestJson
              OutputDirectory = Some dossierOut
              RocqProjectPath = None
              RocqFactsPath = None
              RocqConfigPath = None
              SeamChecksReceiptPath = Some generated.SeamChecksPath
              RefusalControlsReceiptPath = Some generated.RefusalControlsPath }
        |> accepted

    let dossier = dossierRes.Dossier
    Assert.Equal(fx.Manifest.Proof.CompilerNonVacuityReceiptsCount + 5, dossier.Counts.Falsified)
    Assert.Equal(fx.Manifest.Proof.CompilerNonVacuityReceiptsCount, dossier.Falsified.CompilerNonVacuityReceipts)
    Assert.True(dossier.Falsified.RefusalControlsAttached)
    Assert.Equal(5, dossier.Falsified.RefusalControls.Length)
    Assert.Empty(dossier.Falsified.RocqFalsified)
    Assert.Equal(fx.Manifest.Proof.PremisesSatCount + 7, dossier.Counts.Checks)
    Assert.Equal(fx.Manifest.Proof.DischargedUnsatCount, dossier.Counts.Guarantees)
    Assert.Equal(0, dossier.Counts.PendingSeamCategories)
    Assert.Equal(0, dossier.Counts.Open)
    Assert.Equal(0, dossier.Open.CompilerOpenObligations)
    Assert.Equal(0, dossier.Open.CompilerRefutedObligations)
    Assert.Equal(
        "SUBSTANTIATED -- All compiler safety obligations are proven and all required target seam checks are recorded.",
        dossier.OverallStatus)
    Assert.Equal(8, dossier.Checks.SeamCategories.Length)
    Assert.All(dossier.Checks.SeamCategories, fun cat -> Assert.Equal("VERIFIED", cat.Status))

[<Fact>]
let ``CLI end-to-end journey runs project, substantiate, seams and dossier with zero pending seam categories`` () =
    let work = scratch "cli-e2e"
    let fx = buildFixture work "SeamCliProbe" true
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let composerDll = sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll"

    let seamsOut = Path.Combine(work, "seams-out")
    let seamsWork = Path.Combine(work, "seams-work")
    // Exercise TOML substantiation manifest input via CLI.
    let seamsLaunch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "seams"; "--manifest"; fx.ManifestToml; "--spec"; fx.SpecPath
              "--output"; seamsOut; "--work-dir"; seamsWork ]
    seamsLaunch.Environment["COMPOSER_DLL"] <- composerDll
    let seamsStdout = runWithBound 600000 work "cli-seams" seamsLaunch
    Assert.Contains("Package: SeamCliProbe", seamsStdout)
    Assert.Contains(
        $"Library obligations: {fx.Manifest.Proof.LibraryObligations}; UNSAT(H and not C): {fx.Manifest.Proof.LibraryDischargedUnsatCount}; SAT(H): {fx.Manifest.Proof.LibraryPremisesSatCount}",
        seamsStdout)
    Assert.Contains(
        $"Total obligations: {fx.Manifest.Proof.TotalObligations}; UNSAT(H and not C): {fx.Manifest.Proof.DischargedUnsatCount}; SAT(H): {fx.Manifest.Proof.PremisesSatCount}",
        seamsStdout)
    Assert.Contains("Physical seam checks passed: 7", seamsStdout)
    Assert.Contains("Refusal controls passed: 5", seamsStdout)

    let seamChecksPath = requiredFile "CLI seam-checks.json" (Path.Combine(seamsOut, "SeamCliProbe.seam-checks.json"))
    let refusalControlsPath = requiredFile "CLI refusal-controls.json" (Path.Combine(seamsOut, "SeamCliProbe.refusal-controls.json"))
    let ledgerPath = requiredFile "CLI seams-ledger.json" (Path.Combine(seamsOut, "SeamCliProbe.seams-ledger.json"))
    SubstantiationSeams.readLedgerReceipt fx.Manifest ledgerPath |> accepted |> ignore

    let dossierOut = Path.Combine(work, "dossier-out")
    let dossierStdout =
        run work "cli-dossier"
            (ProcessLaunch.startInfo "dotnet"
                [ cli; "dossier"; "--manifest"; fx.ManifestJson
                  "--seam-checks"; seamChecksPath
                  "--refusal-controls"; refusalControlsPath
                  "--output"; dossierOut ])
    Assert.Contains(
        "Overall status: SUBSTANTIATED -- All compiler safety obligations are proven and all required target seam checks are recorded.",
        dossierStdout)

    // Negative CLI invocation: malformed spec exits 1 with FS9253 on stderr and empty stdout.
    let badSpec = Path.Combine(work, "bad.seams.toml")
    File.WriteAllText(badSpec, "schema_version = 99\n")
    let badOut = Path.Combine(work, "bad-out")
    let badLaunch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "seams"; "--manifest"; fx.ManifestJson; "--spec"; badSpec; "--output"; badOut ]
    badLaunch.WorkingDirectory <- work
    use child = new Process(StartInfo = badLaunch)
    Assert.True(child.Start())
    let outTask = child.StandardOutput.ReadToEndAsync()
    let errTask = child.StandardError.ReadToEndAsync()
    Assert.True(child.WaitForExit 60000)
    child.WaitForExit()
    Assert.Equal(1, child.ExitCode)
    Assert.Equal("", outTask.Result)
    Assert.StartsWith("FS9253: ", errTask.Result)
    Assert.False(Directory.Exists badOut)

[<Fact>]
let ``verifyDischarge and evaluate refuse tampered 06c_psg_discharge and 06b_premises_sat evidence with FS9253`` () =
    let fx = sharedFixture.Value
    let work = scratch "verify-discharge-tamper"

    let copyArtifactsDir label =
        let dst = Path.Combine(work, label + "-artifacts")
        Directory.CreateDirectory dst |> ignore
        for file in Directory.GetFiles fx.Manifest.ArtifactsDirectory do
            File.Copy(file, Path.Combine(dst, Path.GetFileName file), true)
        dst

    let assertTamperedDischargeRefused
        (label: string)
        (rehashProof: bool)
        (expectedSubstring: string)
        (tamper: string -> string -> Substantiation.Manifest -> Substantiation.Manifest) =
        let artDir = copyArtifactsDir label
        let receiptPath = Path.Combine(artDir, "06c_psg_discharge.json")
        let premisesPath = Path.Combine(artDir, "06b_premises_sat.stdout")
        let baseManifest = { fx.Manifest with ArtifactsDirectory = artDir }
        let editedManifest = tamper receiptPath premisesPath baseManifest
        let finalManifest =
            if rehashProof then
                let updatedProof =
                    { editedManifest.Proof with
                        ReceiptSha256 =
                            if File.Exists receiptPath then ClangToolchain.sha256 receiptPath else editedManifest.Proof.ReceiptSha256
                        PremisesOutputSha256 =
                            if File.Exists premisesPath then ClangToolchain.sha256 premisesPath else editedManifest.Proof.PremisesOutputSha256 }
                Substantiation.seal { editedManifest with Proof = updatedProof }
            else
                Substantiation.seal editedManifest

        match SubstantiationSeams.verifyDischarge finalManifest with
        | Ok _ -> failwithf "verifyDischarge unexpectedly accepted tampered case '%s'" label
        | Error refusal ->
            let diag = Assert.Single refusal.Diagnostics
            Assert.Equal("FS9253", diag.Code)
            Assert.Contains(expectedSubstring, diag.Message)

        let manifestFile = Path.Combine(work, label + ".substantiation.json")
        let outDir = Path.Combine(work, label + "-out")
        File.WriteAllText(manifestFile, Substantiation.toJson finalManifest, UTF8Encoding(false))
        match
            SubstantiationSeams.evaluate
                { ManifestPath = manifestFile
                  SpecPath = fx.SpecPath
                  OutputDirectory = Some outDir
                  WorkDirectory = Some(Path.Combine(work, label + "-work"))
                  Verbose = false }
        with
        | Ok _ -> failwithf "evaluate unexpectedly accepted tampered discharge case '%s'" label
        | Error refusal ->
            let diag = Assert.Single refusal.Diagnostics
            Assert.Equal("FS9253", diag.Code)
            Assert.Contains(expectedSubstring, diag.Message)
        Assert.False(Directory.Exists outDir)

    let replaceFirst (oldVal: string) (newVal: string) (text: string) =
        let idx = text.IndexOf(oldVal, StringComparison.Ordinal)
        Assert.True(idx >= 0, $"expected '{oldVal}' in artifact text")
        text.Substring(0, idx) + newVal + text.Substring(idx + oldVal.Length)

    // 1. 06c Stage other than "psg"
    assertTamperedDischargeRefused "06c-stage-baker" true "Stage = 'psg'" (fun receiptPath _ m ->
        let raw = File.ReadAllText receiptPath
        File.WriteAllText(receiptPath, replaceFirst "\"Stage\": \"psg\"" "\"Stage\": \"baker\"" raw, UTF8Encoding(false))
        m)

    // 2. 06c "sat" verdict (refuted obligation)
    assertTamperedDischargeRefused "06c-sat-verdict" true "open (0) or refuted (1)" (fun receiptPath _ m ->
        let raw = File.ReadAllText receiptPath
        File.WriteAllText(receiptPath, replaceFirst "\"Verdict\": \"unsat\"" "\"Verdict\": \"sat\"" raw, UTF8Encoding(false))
        m)

    // 3. 06c "unknown" verdict (pending open obligation)
    assertTamperedDischargeRefused "06c-unknown-verdict" true "open (1) or refuted (0)" (fun receiptPath _ m ->
        let raw = File.ReadAllText receiptPath
        File.WriteAllText(receiptPath, replaceFirst "\"Verdict\": \"unsat\"" "\"Verdict\": \"unknown\"" raw, UTF8Encoding(false))
        m)

    // 4. 06c outcome count disagrees with manifest.Proof.DischargedUnsatCount
    assertTamperedDischargeRefused "06c-count-mismatch" true "UNSAT count" (fun receiptPath _ m ->
        let doc = Json.parseOrFail(File.ReadAllText receiptPath)
        let outcomes = doc |> Json.prop "Outcomes" |> Option.map Json.items |> Option.defaultValue [] |> List.toArray
        Assert.True(outcomes.Length >= 2)
        let truncated = outcomes[0 .. outcomes.Length - 2] |> Array.toList
        let json =
            JsonValue.Object
                [ "Stage", JsonValue.String "psg"
                  "Outcomes", JsonValue.Array truncated ]
            |> Json.serializePretty
        File.WriteAllText(receiptPath, json + "\n", UTF8Encoding(false))
        m)

    // 5. 06c SHA-256 drift against manifest.Proof.ReceiptSha256
    assertTamperedDischargeRefused "06c-sha-drift" false "06c_psg_discharge.json SHA-256 differs" (fun receiptPath _ m ->
        File.AppendAllText(receiptPath, "\n")
        m)

    // 6. 06b non-sat line ("unsat")
    assertTamperedDischargeRefused "06b-non-sat-line" true "non-sat verdict" (fun _ premisesPath m ->
        let raw = File.ReadAllText premisesPath
        File.WriteAllText(premisesPath, replaceFirst "sat" "unsat" raw, UTF8Encoding(false))
        m)

    // 7. 06b SAT line count disagrees with manifest.Proof.PremisesSatCount
    assertTamperedDischargeRefused "06b-count-mismatch" true "SAT count" (fun _ premisesPath m ->
        File.AppendAllText(premisesPath, "\nsat\n")
        m)

    // 8. 06b SHA-256 drift against manifest.Proof.PremisesOutputSha256
    assertTamperedDischargeRefused "06b-sha-drift" false "06b_premises_sat.stdout SHA-256 differs" (fun _ premisesPath m ->
        File.AppendAllText(premisesPath, " ")
        m)

    // 9. Missing 06c and missing 06b files
    assertTamperedDischargeRefused "06c-missing" false "06c_psg_discharge.json does not exist" (fun receiptPath _ m ->
        File.Delete receiptPath
        m)
    assertTamperedDischargeRefused "06b-missing" false "06b_premises_sat.stdout does not exist" (fun _ premisesPath m ->
        File.Delete premisesPath
        m)

[<Fact>]
let ``inspectElfExecutable validates ELF64 headers, sections, strict sh_entsize, symtab, dynsym and DT_NEEDED and refuses malformed binaries`` () =
    let fx = sharedFixture.Value
    let sharedElf = SubstantiationSeams.inspectElfExecutable fx.SharedLib |> accepted
    Assert.Equal(3us, sharedElf.ElfType)
    Assert.Equal(62us, sharedElf.Machine)
    Assert.Contains("seam_scalar_add", sharedElf.DefinedDynsym)
    Assert.Contains("seam_unused_dead_symbol", sharedElf.DefinedDynsym)

    // ET_REL (.o relocatable object) is refused by inspectElfExecutable (expects ET_EXEC=2 or ET_DYN=3).
    SubstantiationSeams.inspectElfExecutable fx.DeadObject |> refused "FS9253"

    let work = scratch "elf-malformed"
    SubstantiationSeams.inspectElfExecutable (Path.Combine(work, "missing.elf")) |> refused "FS9253"

    let shortFile = Path.Combine(work, "short.elf")
    File.WriteAllBytes(shortFile, [| 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy |])
    SubstantiationSeams.inspectElfExecutable shortFile |> refused "FS9253"

    let validBytes = File.ReadAllBytes fx.SharedLib
    let shoff = int (BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(validBytes, 40, 8)))
    let shentsize = int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(validBytes, 58, 2)))
    let shnum = int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(validBytes, 60, 2)))

    let findSectionHeaderOffset (targetType: uint32) : int =
        [ 0 .. shnum - 1 ]
        |> List.pick (fun idx ->
            let off = shoff + idx * shentsize
            let shType = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(validBytes, off + 4, 4))
            let shSize = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(validBytes, off + 32, 8))
            if shType = targetType && shSize > 0UL then Some off else None)

    let dynHeaderOff = findSectionHeaderOffset 6u
    let dynsymHeaderOff = findSectionHeaderOffset 11u

    let corruptElf label (corrupt: byte[] -> unit) =
        let path = Path.Combine(work, label + ".elf")
        let copy = Array.copy validBytes
        corrupt copy
        File.WriteAllBytes(path, copy)
        SubstantiationSeams.inspectElfExecutable path |> refused "FS9253"

    corruptElf "bad-magic" (fun b -> b[0] <- 0x00uy)
    corruptElf "elf32" (fun b -> b[4] <- 1uy)
    corruptElf "big-endian" (fun b -> b[5] <- 2uy)
    corruptElf "wrong-machine" (fun b -> b[18] <- 3us |> byte; b[19] <- 0uy)
    corruptElf "zero-shnum" (fun b -> b[60] <- 0uy; b[61] <- 0uy)
    corruptElf "oob-shoff" (fun b ->
        for i in 40 .. 46 do b[i] <- 0xFFuy
        b[47] <- 0x00uy)

    // R6a: SHT_DYNAMIC sh_entsize = 0 and sh_entsize = 32 (even when sh_size % 32 = 0) are strictly refused.
    corruptElf "zero-dyn-entsize" (fun b ->
        BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(b, dynHeaderOff + 56, 8), 0UL))
    corruptElf "wrong-dyn-entsize-32" (fun b ->
        BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(b, dynHeaderOff + 32, 8), 32UL)
        BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(b, dynHeaderOff + 56, 8), 32UL))
    corruptElf "wrong-dynsym-entsize-48" (fun b ->
        BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(b, dynsymHeaderOff + 32, 8), 48UL)
        BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(b, dynsymHeaderOff + 56, 8), 48UL))

[<Fact>]
let ``broken, cyclic and empty evidence paths are refused before writing any receipt`` () =
    let fx = sharedFixture.Value
    let work = scratch "refused-paths"
    let links = refusedLinks work
    let baseOut = Path.Combine(work, "clean-out")
    let baseWork = Path.Combine(work, "clean-work")
    let baseReq : SubstantiationSeams.Request =
        { ManifestPath = fx.ManifestJson
          SpecPath = fx.SpecPath
          OutputDirectory = Some baseOut
          WorkDirectory = Some baseWork
          Verbose = false }

    for bad in [ ""; " "; links.Broken; links.Chained; links.Cycle; Path.Combine(links.Broken, "nested") ] do
        SubstantiationSeams.evaluate { baseReq with ManifestPath = bad } |> refused "FS9253"
        SubstantiationSeams.evaluate { baseReq with SpecPath = bad } |> refused "FS9253"
        SubstantiationSeams.evaluate { baseReq with OutputDirectory = Some bad } |> refused "FS9253"
        SubstantiationSeams.evaluate { baseReq with WorkDirectory = Some bad } |> refused "FS9253"
        SubstantiationSeams.inspectElfExecutable bad |> refused "FS9253"
        SubstantiationSeams.readLedgerReceipt fx.Manifest bad |> refused "FS9253"
        SubstantiationDossier.readSeamReceipt fx.Manifest bad |> refused "FS9252"
        SubstantiationDossier.readRefusalReceipt fx.Manifest bad |> refused "FS9252"

    Assert.False(File.Exists links.Absent)
    Assert.False(Directory.Exists links.Absent)
    Assert.False(Directory.Exists baseOut)

[<Fact>]
let ``spec schema, not-measured ClangTarget, non-static link elision, shared_library_sha256, and missing seam categories fail closed with FS9253`` () =
    let fx = sharedFixture.Value
    let libcFx = sharedLibcFixture.Value
    let notMeasured, notMeasuredJson = notMeasuredManifest.Value
    let work = scratch "spec-validation"
    let baseSpec = File.ReadAllText fx.SpecPath

    let expectSpecRefusedWith manifestJson label (specText: string) =
        let specFile = Path.Combine(work, label + ".seams.toml")
        let outDir = Path.Combine(work, label + "-out")
        File.WriteAllText(specFile, specText, UTF8Encoding(false))
        SubstantiationSeams.evaluate
            { ManifestPath = manifestJson
              SpecPath = specFile
              OutputDirectory = Some outDir
              WorkDirectory = Some(Path.Combine(work, label + "-work"))
              Verbose = false }
        |> refused "FS9253"
        Assert.False(Directory.Exists outDir)

    let expectSpecRefused = expectSpecRefusedWith fx.ManifestJson

    expectSpecRefused "missing-stdout" (baseSpec.Replace("expected_stdout = \"seam:scalar:ok\"\n", ""))
    expectSpecRefused "empty-stdout" (baseSpec.Replace("expected_stdout = \"seam:scalar:ok\"", "expected_stdout = \"   \""))
    expectSpecRefused "missing-shared-library" (baseSpec.Replace("shared_library = " + quote fx.SharedLib + "\n", "").Replace("shared_library_sha256 = " + quote fx.SharedLibSha256 + "\n", "").Replace("dynamic_probe_source = " + quote fx.DynamicProbeC + "\n", "").Replace("retained_dynamic_imports = [\"seam_scalar_add\"]\n", ""))
    let removeControlsWithCodePrefix prefix =
        let blocks = baseSpec.Split([| "[[refusal_controls]]" |], StringSplitOptions.None)
        blocks |> Array.filter (fun block -> not (block.Contains("expected_code = \"" + prefix))) |> String.concat "[[refusal_controls]]"
    expectSpecRefused "missing-fs9-family" (removeControlsWithCodePrefix "FS9")
    expectSpecRefused "missing-ccs84-family" (removeControlsWithCodePrefix "CCS84")

    expectSpecRefused "invalid-toml" "[broken"
    expectSpecRefused "wrong-schema" (baseSpec.Replace("schema_version = 1", "schema_version = 2"))
    expectSpecRefused "unknown-top-key" ("unexpected_flag = true\n" + baseSpec)
    expectSpecRefused "wrong-target" (baseSpec.Replace("target_triple = " + quote fx.Manifest.Boundary.ClangTarget, "target_triple = \"aarch64-unknown-linux-gnu\""))
    expectSpecRefused "unknown-check-key" (baseSpec.Replace("id = \"seam-scalar-call\"", "id = \"seam-scalar-call\"\nunknown_field = 1"))
    expectSpecRefused "unknown-elf-key" (baseSpec.Replace("[checks.elf]", "[checks.elf]\nunknown_elf = true"))
    expectSpecRefused "unknown-control-key" (baseSpec.Replace("id = \"refusal-pilot-missing-symbol\"", "id = \"refusal-pilot-missing-symbol\"\nrogue = \"x\""))
    expectSpecRefused "duplicate-check-id" (baseSpec.Replace("id = \"seam-measured-record-abi\"", "id = \"seam-scalar-call\""))
    expectSpecRefused "duplicate-control-id" (baseSpec.Replace("id = \"refusal-substantiation-callable\"", "id = \"refusal-pilot-missing-symbol\""))
    expectSpecRefused "refusal-category-in-checks" (baseSpec.Replace("category = \"scalar-handle-link-and-call\"", "category = \"falsification-refusal-controls\""))

    // R6b: Manifest with ClangTarget = "not-measured" is unconditionally refused even if spec echoes "not-measured".
    let notMeasuredSpec echoTriple =
        String.concat "\n"
            [ "schema_version = 1"
              "target_triple = " + quote echoTriple
              "[[refusal_controls]]"
              "id = \"ctrl\""
              "kind = \"pilot\""
              "target = " + quote fx.RefusalPilotMissingSym
              "expected_code = \"FS9228\""
              "misuse_summary = \"missing sym\""
              "protection_summary = \"refused\"" ]
    expectSpecRefusedWith notMeasuredJson "clang-target-not-measured-echo" (notMeasuredSpec "not-measured")
    expectSpecRefusedWith notMeasuredJson "clang-target-not-measured-x86" (notMeasuredSpec "x86_64-unknown-linux-gnu")
    Assert.Equal("not-measured", notMeasured.Boundary.ClangTarget)

    // R4: Static link elision and elided_symbols/retained_static_symbols are refused when LinkMode <> "static-archive".
    let libcBaseSpec =
        File.ReadAllText libcFx.SpecPath
        + "\n[[refusal_controls]]\nid = \"ctrl-pilot\"\nkind = \"pilot\"\ntarget = " + quote fx.RefusalPilotMissingSym
        + "\nexpected_code = \"FS9228\"\nmisuse_summary = \"m\"\nprotection_summary = \"p\"\n"
        + "\n[[refusal_controls]]\nid = \"ctrl-composer\"\nkind = \"composer\"\ntarget = " + quote fx.RefusalUnsettledProj
        + "\nexpected_code = \"CCS8403\"\nmisuse_summary = \"m\"\nprotection_summary = \"p\"\n"
    expectSpecRefusedWith libcFx.ManifestJson "elided-on-shared-link-mode"
        (libcBaseSpec.Replace(
            "expected_stdout = \"seam:libc-abs:ok\"",
            "expected_stdout = \"seam:libc-abs:ok\"\n[checks.elf]\nelided_symbols = [\"nonexistent_symbol\"]"))
    expectSpecRefusedWith libcFx.ManifestJson "retained-static-on-shared-link-mode"
        (libcBaseSpec.Replace(
            "expected_stdout = \"seam:libc-abs:ok\"",
            "expected_stdout = \"seam:libc-abs:ok\"\n[checks.elf]\nretained_static_symbols = [\"abs\"]"))

    // R5: shared_library requires valid 64-hex shared_library_sha256, and shared_library_sha256 without shared_library is refused.
    expectSpecRefused "shared-without-sha256"
        (baseSpec.Replace("shared_library_sha256 = " + quote fx.SharedLibSha256 + "\n", ""))
    expectSpecRefused "shared-invalid-sha256"
        (baseSpec.Replace("shared_library_sha256 = " + quote fx.SharedLibSha256, "shared_library_sha256 = \"not-a-hex-digest\""))
    expectSpecRefused "sha256-without-shared-lib"
        (baseSpec
            .Replace("shared_library = " + quote fx.SharedLib + "\n", "")
            .Replace("dynamic_probe_source = " + quote fx.DynamicProbeC + "\n", "")
            .Replace("retained_dynamic_imports = [\"seam_scalar_add\"]\n", ""))

    // Missing physical category (`callback-registration-invocation` omitted)
    let withoutCallback =
        let idx = baseSpec.IndexOf("[[checks]]\nid = \"seam-callback-invocation\"", StringComparison.Ordinal)
        let ctrlIdx = baseSpec.IndexOf("[[refusal_controls]]", StringComparison.Ordinal)
        baseSpec.Substring(0, idx) + baseSpec.Substring(ctrlIdx)
    expectSpecRefused "missing-physical-category" withoutCallback

    // Missing refusal_controls when `falsification-refusal-controls` is required
    let withoutControls =
        let ctrlIdx = baseSpec.IndexOf("[[refusal_controls]]", StringComparison.Ordinal)
        baseSpec.Substring(0, ctrlIdx)
    expectSpecRefused "missing-refusal-controls" withoutControls

    // `static-dynamic-link-elision` without `[checks.elf]`
    let withoutElfBlock =
        let elfIdx = baseSpec.IndexOf("[checks.elf]", StringComparison.Ordinal)
        let nextCheckIdx = baseSpec.IndexOf("[[checks]]\nid = \"seam-error-sentinel\"", StringComparison.Ordinal)
        baseSpec.Substring(0, elfIdx) + baseSpec.Substring(nextCheckIdx)
    expectSpecRefused "missing-elf-table" withoutElfBlock

    // `shared_library` without `dynamic_probe_source`
    let sharedWithoutDynSource =
        baseSpec.Replace("dynamic_probe_source = " + quote fx.DynamicProbeC + "\n", "")
    expectSpecRefused "shared-without-dyn-source" sharedWithoutDynSource

    // B1: `include_library_sources` bypass key is forbidden in `[[checks]]` and refused with FS9253.
    let forbiddenIncludeLibKey =
        baseSpec.Replace("id = \"seam-scalar-call\"", "id = \"seam-scalar-call\"\ninclude_library_sources = false")
    expectSpecRefused "forbidden-include-library-sources-key" forbiddenIncludeLibKey

[<Fact>]
let ``ELF link-elision check refuses absent archive symbol, retained dead symbol, shared_library SHA mismatch, absent shared dynsym, forbidden DT_NEEDED, and missing dynamic import`` () =
    let fx = sharedFixture.Value
    let work = scratch "elf-negatives"
    let rawSpec = File.ReadAllText fx.SpecPath
    // Place `seam-link-elision` first in `[[checks]]` so each negative ELF case fails immediately on check #1.
    let firstCheckIdx = rawSpec.IndexOf("[[checks]]\nid = \"seam-scalar-call\"", StringComparison.Ordinal)
    let linkCheckIdx = rawSpec.IndexOf("[[checks]]\nid = \"seam-link-elision\"", StringComparison.Ordinal)
    let afterLinkIdx = rawSpec.IndexOf("[[checks]]\nid = \"seam-error-sentinel\"", StringComparison.Ordinal)
    let baseSpec =
        rawSpec.Substring(0, firstCheckIdx)
        + rawSpec.Substring(linkCheckIdx, afterLinkIdx - linkCheckIdx)
        + rawSpec.Substring(firstCheckIdx, linkCheckIdx - firstCheckIdx)
        + rawSpec.Substring(afterLinkIdx)

    let expectElfRefused label (transform: string -> string) =
        let updated = transform baseSpec
        Assert.NotEqual<string>(baseSpec, updated)
        let specFile = Path.Combine(work, label + ".seams.toml")
        let outDir = Path.Combine(work, label + "-out")
        File.WriteAllText(specFile, updated, UTF8Encoding(false))
        SubstantiationSeams.evaluate
            { ManifestPath = fx.ManifestJson
              SpecPath = specFile
              OutputDirectory = Some outDir
              WorkDirectory = Some(Path.Combine(work, label + "-work"))
              Verbose = false }
        |> refused "FS9253"
        Assert.False(Directory.Exists outDir)

    // 1. Claiming a live retained symbol (`seam_scalar_add`) was elided in static-only ELF check
    expectElfRefused "live-symbol-claimed-elided" (fun s ->
        s
            .Replace("elided_symbols = [\"seam_unused_dead_symbol\"]", "elided_symbols = [\"seam_scalar_add\"]"))

    // 2. R5: Wrong 64-hex shared_library_sha256 disagrees with actual libseam_probe.so on disk
    expectElfRefused "wrong-shared-lib-sha256" (fun s ->
        s.Replace(
            "shared_library_sha256 = " + quote fx.SharedLibSha256,
            "shared_library_sha256 = " + quote (String.replicate 64 "e")))

    // 3. Elided symbol is not even in the static archive (guards against vacuous elision claims)
    expectElfRefused "elided-not-in-archive" (fun s ->
        s
            .Replace("elided_symbols = [\"seam_unused_dead_symbol\"]", "elided_symbols = [\"symbol_never_in_archive\"]"))

    // 4. Claiming an elided dead symbol (`seam_unused_dead_symbol`) was retained statically
    expectElfRefused "dead-symbol-claimed-retained" (fun s ->
        s
            .Replace("retained_static_symbols = [\"seam_scalar_add\"]", "retained_static_symbols = [\"seam_unused_dead_symbol\"]"))

    // 5. Forbidding `libc.so.6`, which the runtime executable actually links dynamically
    expectElfRefused "forbidden-needed-present" (fun s ->
        s
            .Replace("forbidden_needed_libraries = [\"libseam_probe.so\"]", "forbidden_needed_libraries = [\"libc.so.6\"]"))

    // 6. Claiming the dynamic probe imports unreferenced `seam_unused_dead_symbol`
    expectElfRefused "missing-dynamic-import" (fun s ->
        s.Replace("retained_dynamic_imports = [\"seam_scalar_add\"]", "retained_dynamic_imports = [\"seam_unused_dead_symbol\"]"))

    // 7. R4: Shared library exists with matching SHA-256, and elided symbol is in static archive,
    //    but shared library DefinedDynsym lacks the comparison symbol (`libseam_probe.so` built without `seam_dead.o`).
    expectElfRefused "elided-absent-from-shared-dynsym" (fun s ->
        s.Replace("shared_library = " + quote fx.SharedLib, "shared_library = " + quote fx.PartialSharedLib)
         .Replace("shared_library_sha256 = " + quote fx.SharedLibSha256, "shared_library_sha256 = " + quote fx.PartialSharedLibSha256))

[<Fact>]
let ``probe compile failure, nonzero exit, stdout mismatch, multi-code superset, and refusal control mismatch or unexpected pass fail closed`` () =
    let fx = sharedFixture.Value
    let libcFx = sharedLibcFixture.Value
    let work = scratch "runtime-and-control-negatives"
    let libcCheckBlock =
        File.ReadAllText libcFx.SpecPath
        + "\n[[refusal_controls]]\nid = \"base-pilot\"\nkind = \"pilot\"\ntarget = " + quote fx.RefusalPilotMissingSym
        + "\nexpected_code = \"FS9228\"\nmisuse_summary = \"Baseline pilot control\"\nprotection_summary = \"Refused\"\n"
        + "\n[[refusal_controls]]\nid = \"base-composer\"\nkind = \"composer\"\ntarget = " + quote fx.RefusalUnsettledProj
        + "\nexpected_code = \"CCS8403\"\nmisuse_summary = \"Baseline composer control\"\nprotection_summary = \"Refused\"\n"

    // 1. Refusal controls exercised over minimal Clang-measured libc manifest:
    //    wrong code, unexpected pass, AND multi-code superset (R3: {CCS8401, CCS8010} when expecting singleton {CCS8010}).
    let expectControlRefused label kind targetPath expectedCode expectedMessageFragment =
        let specFile = Path.Combine(work, label + ".seams.toml")
        let outDir = Path.Combine(work, label + "-out")
        let specText =
            libcCheckBlock
            + "\n[[refusal_controls]]\n"
            + "id = " + quote label + "\n"
            + "kind = " + quote kind + "\n"
            + "target = " + quote targetPath + "\n"
            + "expected_code = " + quote expectedCode + "\n"
            + "misuse_summary = \"Negative test control\"\n"
            + "protection_summary = \"Must fail closed\"\n"
        File.WriteAllText(specFile, specText, UTF8Encoding(false))
        let outcome =
            SubstantiationSeams.evaluate
                { ManifestPath = libcFx.ManifestJson
                  SpecPath = specFile
                  OutputDirectory = Some outDir
                  WorkDirectory = Some(Path.Combine(work, label + "-work"))
                  Verbose = false }
        outcome |> refused "FS9253"
        Assert.False(Directory.Exists outDir)
        match outcome with
        | Error refusal -> Assert.Contains(expectedMessageFragment, refusal.Text)
        | Ok _ -> ()

    expectControlRefused "control-wrong-code-pilot" "pilot" fx.RefusalPilotMissingSym "FS9999" "expected diagnostic code FS9999, but observed FS9228"
    expectControlRefused "control-wrong-code-composer" "composer" fx.RefusalUnsettledProj "CCS8999" "expected diagnostic code CCS8999, but observed CCS8403"
    expectControlRefused "control-superset-codes-composer" "composer" fx.RefusalSupersetComposerProj "CCS8010" "expected exact diagnostic set {CCS8010}, but observed multiple codes"

    // 2. Refusal controls that unexpectedly succeed (pilot, substantiation, composer)
    expectControlRefused "pilot-control-unexpected-pass" "pilot" fx.RefusalValidPilot "FS9228" "unexpectedly succeeded without refusal"
    expectControlRefused "substantiation-control-unexpected-pass" "substantiation" fx.RefusalValidSubstantiationProj "FS9246" "unexpectedly succeeded without refusal"
    expectControlRefused "composer-control-unexpected-pass" "composer" fx.RefusalValidComposerProj "CCS8010" "unexpectedly compiled cleanly"

    let expectRunRefused label expectedMessageFragment (specText: string) =
        let specFile = Path.Combine(work, label + ".seams.toml")
        let outDir = Path.Combine(work, label + "-out")
        File.WriteAllText(specFile, specText, UTF8Encoding(false))
        let outcome =
            SubstantiationSeams.evaluate
                { ManifestPath = libcFx.ManifestJson
                  SpecPath = specFile
                  OutputDirectory = Some outDir
                  WorkDirectory = Some(Path.Combine(work, label + "-work"))
                  Verbose = false }
        outcome |> refused "FS9253"
        Assert.False(Directory.Exists outDir)
        match outcome with
        | Error refusal -> Assert.Contains(expectedMessageFragment, refusal.Text)
        | Ok _ -> ()

    // 3. Broken Clef probe fails Composer compilation
    let brokenProbe = Path.Combine(work, "BrokenProbe.clef")
    writeLines brokenProbe
        [ "module Seam.Probe.Broken"
          "[<EntryPoint>]"
          "let main _ : int = \"not-an-int\"" ]
    expectRunRefused "probe-compile-fail" "failed Composer compilation"
        (libcCheckBlock.Replace("entry_source = " + quote libcFx.ProbeAbs, "entry_source = " + quote brokenProbe))

    // 4. Probe exits with unexpected exit code
    expectRunRefused "probe-exit-mismatch" "exited with code 0 (expected 99)"
        (libcCheckBlock.Replace("expected_stdout = \"seam:libc-abs:ok\"", "expected_exit_code = 99\nexpected_stdout = \"seam:libc-abs:ok\""))

    // 5. Probe prints unexpected stdout
    expectRunRefused "probe-stdout-mismatch" "did not match expected 'wrong:stdout'"
        (libcCheckBlock.Replace("expected_stdout = \"seam:libc-abs:ok\"", "expected_stdout = \"wrong:stdout\""))

[<Fact>]
let ``tampered manifest, child receipt SHA drift, ledger Discharge drift, and receipt schema mismatches fail closed`` () =
    let fx = sharedFixture.Value
    let libcFx = sharedLibcFixture.Value
    let work = scratch "receipt-readers"

    // Bad manifest extension and tampered manifest digest
    let txtManifest = Path.Combine(work, "SeamProbe.substantiation.txt")
    File.Copy(fx.ManifestJson, txtManifest)
    SubstantiationSeams.evaluate
        { ManifestPath = txtManifest
          SpecPath = fx.SpecPath
          OutputDirectory = Some(Path.Combine(work, "out-txt"))
          WorkDirectory = Some(Path.Combine(work, "work-txt"))
          Verbose = false }
    |> refused "FS9253"

    let tamperedManifest = Path.Combine(work, "Tampered.substantiation.json")
    File.WriteAllText(
        tamperedManifest,
        (File.ReadAllText fx.ManifestJson).Replace(fx.Manifest.IntegritySha256, String.replicate 64 "a"))
    SubstantiationSeams.evaluate
        { ManifestPath = tamperedManifest
          SpecPath = fx.SpecPath
          OutputDirectory = Some(Path.Combine(work, "out-tampered"))
          WorkDirectory = Some(Path.Combine(work, "work-tampered"))
          Verbose = false }
    |> refused "FS9253"

    // Receipt readers reject unknown JSON properties, wrong manifest digests, and ExpectedCode <> ObservedCode (R3).
    let unknownJson = Path.Combine(work, "unknown.json")
    File.WriteAllText(unknownJson, "{ \"Extra\": 1 }")
    SubstantiationDossier.readSeamReceipt fx.Manifest unknownJson |> refused "FS9247"
    SubstantiationDossier.readRefusalReceipt fx.Manifest unknownJson |> refused "FS9247"
    SubstantiationSeams.readLedgerReceipt fx.Manifest unknownJson |> refused "FS9253"

    let wrongDigestSeam = Path.Combine(work, "wrong-digest.seam-checks.json")
    writeLines wrongDigestSeam
        [ "{"
          "  \"SchemaVersion\": 1,"
          "  \"Package\": " + quote fx.Manifest.Package + ","
          "  \"ManifestIntegritySha256\": " + quote (String.replicate 64 "b") + ","
          "  \"TargetTriple\": \"x86_64-unknown-linux-gnu\","
          "  \"Checks\": []"
          "}" ]
    SubstantiationDossier.readSeamReceipt fx.Manifest wrongDigestSeam |> refused "FS9248"

    let mismatchedObservedRefusal = Path.Combine(work, "mismatch.refusal-controls.json")
    writeLines mismatchedObservedRefusal
        [ "{"
          "  \"SchemaVersion\": 1,"
          "  \"Package\": " + quote fx.Manifest.Package + ","
          "  \"ManifestIntegritySha256\": " + quote fx.Manifest.IntegritySha256 + ","
          "  \"Controls\": ["
          "    {"
          "      \"Id\": \"c1\","
          "      \"ExpectedCode\": \"FS9228\","
          "      \"ObservedCode\": \"FS9229\","
          "      \"MisuseSummary\": \"missing\","
          "      \"ProtectionSummary\": \"refused\","
          "      \"Passed\": true"
          "    }"
          "  ]"
          "}" ]
    SubstantiationDossier.readRefusalReceipt fx.Manifest mismatchedObservedRefusal |> refused "FS9251"

    // R7: Generate a real verified ledger over libcFx, then tamper child receipts on disk and ledger Discharge/SharedLibraries.
    let cleanSpec = Path.Combine(work, "clean-libc.seams.toml")
    File.WriteAllText(
        cleanSpec,
        File.ReadAllText libcFx.SpecPath
        + "\n[[refusal_controls]]\nid = \"ctrl-pilot\"\nkind = \"pilot\"\ntarget = " + quote fx.RefusalPilotMissingSym
        + "\nexpected_code = \"FS9228\"\nmisuse_summary = \"missing sym\"\nprotection_summary = \"refused\"\n"
        + "\n[[refusal_controls]]\nid = \"ctrl-composer\"\nkind = \"composer\"\ntarget = " + quote fx.RefusalUnsettledProj
        + "\nexpected_code = \"CCS8403\"\nmisuse_summary = \"unsettled CHandle boundary call\"\nprotection_summary = \"refused\"\n",
        UTF8Encoding(false))
    let cleanOut = Path.Combine(work, "clean-ledger-out")
    let generated =
        SubstantiationSeams.evaluate
            { ManifestPath = libcFx.ManifestJson
              SpecPath = cleanSpec
              OutputDirectory = Some cleanOut
              WorkDirectory = Some(Path.Combine(work, "clean-ledger-work"))
              Verbose = false }
        |> accepted
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest generated.LedgerPath |> accepted |> ignore

    let cloneLedgerCase label =
        let dir = Path.Combine(work, "ledger-case-" + label)
        Directory.CreateDirectory dir |> ignore
        let seamCopy = Path.Combine(dir, "LibcAbs.seam-checks.json")
        let refusalCopy = Path.Combine(dir, "LibcAbs.refusal-controls.json")
        let ledgerCopy = Path.Combine(dir, "LibcAbs.seams-ledger.json")
        File.Copy(generated.SeamChecksPath, seamCopy, true)
        File.Copy(generated.RefusalControlsPath, refusalCopy, true)
        let rebasedLedger =
            { generated.Ledger with
                SeamChecksPath = seamCopy
                RefusalControlsPath = refusalCopy }
        File.WriteAllText(ledgerCopy, serializeSeamsLedgerReceipt rebasedLedger, UTF8Encoding(false))
        SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy |> accepted |> ignore
        seamCopy, refusalCopy, ledgerCopy, rebasedLedger

    // R7a-1: Valid JSON whitespace edit on SeamChecksPath alters on-disk SHA-256 -> refused with FS9253.
    let seamCopy1, _, ledgerCopy1, _ = cloneLedgerCase "seam-sha-drift"
    File.AppendAllText(seamCopy1, " ")
    SubstantiationDossier.readSeamReceipt libcFx.Manifest seamCopy1 |> accepted |> ignore
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy1 |> refused "FS9253"

    // R7a-2: Valid JSON whitespace edit on RefusalControlsPath alters on-disk SHA-256 -> refused with FS9253.
    let _, refusalCopy2, ledgerCopy2, _ = cloneLedgerCase "refusal-sha-drift"
    File.AppendAllText(refusalCopy2, " ")
    SubstantiationDossier.readRefusalReceipt libcFx.Manifest refusalCopy2 |> accepted |> ignore
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy2 |> refused "FS9253"

    // R7b-1: Ledger Discharge.DischargedUnsatCount disagrees with manifest.Proof -> refused with FS9253.
    let _, _, ledgerCopy3, baseLedger3 = cloneLedgerCase "discharge-count-drift"
    let badDischargeCount =
        { baseLedger3 with
            Discharge = { baseLedger3.Discharge with DischargedUnsatCount = baseLedger3.Discharge.DischargedUnsatCount + 1 } }
    File.WriteAllText(ledgerCopy3, serializeSeamsLedgerReceipt badDischargeCount, UTF8Encoding(false))
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy3 |> refused "FS9253"

    // R7b-2: Ledger Discharge.ReceiptSha256 disagrees with manifest.Proof.ReceiptSha256 -> refused with FS9253.
    let _, _, ledgerCopy4, baseLedger4 = cloneLedgerCase "discharge-sha-drift"
    let badDischargeSha =
        { baseLedger4 with
            Discharge = { baseLedger4.Discharge with ReceiptSha256 = String.replicate 64 "c" } }
    File.WriteAllText(ledgerCopy4, serializeSeamsLedgerReceipt badDischargeSha, UTF8Encoding(false))
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy4 |> refused "FS9253"

    // R5 in ledger reader: Unexpected SharedLibraries entry on non-elision check -> refused with FS9253.
    let _, _, ledgerCopy5, baseLedger5 = cloneLedgerCase "shared-lib-sha-drift"
    let badSharedEntry : SubstantiationSeams.SharedLibraryComparison =
        { CheckId = "libc-abs-call"
          SharedLibraryPath = fx.SharedLib
          SharedLibrarySha256 = String.replicate 64 "d"
          DynamicProbeBinaryPath = fx.SharedLib
          DynamicProbeBinarySha256 = String.replicate 64 "f" }
    let badSharedLedger = { baseLedger5 with SharedLibraries = [| badSharedEntry |] }
    File.WriteAllText(ledgerCopy5, serializeSeamsLedgerReceipt badSharedLedger, UTF8Encoding(false))
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy5 |> refused "FS9253"

    // R6b in ledger reader: Unmeasured ClangTarget ("not-measured") is unconditionally refused with FS9253.
    let unmeasuredManifest, _ = notMeasuredManifest.Value
    let _, _, ledgerCopy6, baseLedger6 = cloneLedgerCase "not-measured-ledger"
    let unmeasuredLedger =
        { baseLedger6 with
            Package = unmeasuredManifest.Package
            ManifestIntegritySha256 = unmeasuredManifest.IntegritySha256
            TargetTriple = "not-measured" }
    File.WriteAllText(ledgerCopy6, serializeSeamsLedgerReceipt unmeasuredLedger, UTF8Encoding(false))
    SubstantiationSeams.readLedgerReceipt unmeasuredManifest ledgerCopy6 |> refused "FS9253"

    // R7c-1 (B3): RetainedBinaries digest drift in ledger is refused by readLedgerReceipt (FS9253)
    // and by SubstantiationDossier.generate (FS9251) when co-located with seam-checks/refusal-controls.
    let seamCopy7, refusalCopy7, ledgerCopy7, baseLedger7 = cloneLedgerCase "retained-bin-sha-drift"
    let tamperedRetained =
        baseLedger7.RetainedBinaries
        |> Array.map (fun item -> { item with BinarySha256 = String.replicate 64 "b" })
    let badRetainedLedger = { baseLedger7 with RetainedBinaries = tamperedRetained }
    File.WriteAllText(ledgerCopy7, serializeSeamsLedgerReceipt badRetainedLedger, UTF8Encoding(false))
    SubstantiationSeams.readLedgerReceipt libcFx.Manifest ledgerCopy7 |> refused "FS9253"
    SubstantiationDossier.generate
        { ManifestPath = libcFx.ManifestJson
          OutputDirectory = Some(Path.Combine(work, "dossier-bad-retained"))
          RocqProjectPath = None
          RocqFactsPath = None
          RocqConfigPath = None
          SeamChecksReceiptPath = Some seamCopy7
          RefusalControlsReceiptPath = Some refusalCopy7 }
    |> refused "FS9251"

    // R7c-2 (B3): Valid co-located ledger succeeds in SubstantiationDossier.generate and records
    // Seams Ledger SHA-256 in Traceability.Digests, whereas child receipt drift is refused with FS9251.
    let seamCopy8, refusalCopy8, ledgerCopy8, _ = cloneLedgerCase "dossier-colocated-ok"
    let colocatedDossier =
        SubstantiationDossier.generate
            { ManifestPath = libcFx.ManifestJson
              OutputDirectory = Some(Path.Combine(work, "dossier-colocated-out"))
              RocqProjectPath = None
              RocqFactsPath = None
              RocqConfigPath = None
              SeamChecksReceiptPath = Some seamCopy8
              RefusalControlsReceiptPath = Some refusalCopy8 }
        |> accepted
    Assert.Contains(
        colocatedDossier.Dossier.Traceability.Digests,
        fun d -> d.Item = "Seams Ledger SHA-256" && d.Path = ledgerCopy8 && d.Value = ClangToolchain.sha256 ledgerCopy8)
    File.AppendAllText(seamCopy8, " ")
    SubstantiationDossier.generate
        { ManifestPath = libcFx.ManifestJson
          OutputDirectory = Some(Path.Combine(work, "dossier-colocated-tampered"))
          RocqProjectPath = None
          RocqFactsPath = None
          RocqConfigPath = None
          SeamChecksReceiptPath = Some seamCopy8
          RefusalControlsReceiptPath = Some refusalCopy8 }
    |> refused "FS9251"

[<Fact>]
let ``shadow FidelityExtern probes, probes omitting library extern calls, and C subject mutants across all seven physical seam categories are killed with FS9253`` () =
    let fx = sharedFixture.Value
    let libcFx = sharedLibcFixture.Value
    let work = scratch "subject-mutants"
    let baseLibcSpec =
        File.ReadAllText libcFx.SpecPath
        + "\n[[refusal_controls]]\nid = \"ctrl-pilot\"\nkind = \"pilot\"\ntarget = " + quote fx.RefusalPilotMissingSym
        + "\nexpected_code = \"FS9228\"\nmisuse_summary = \"missing sym\"\nprotection_summary = \"refused\"\n"
        + "\n[[refusal_controls]]\nid = \"ctrl-composer\"\nkind = \"composer\"\ntarget = " + quote fx.RefusalUnsettledProj
        + "\nexpected_code = \"CCS8403\"\nmisuse_summary = \"unsettled CHandle boundary call\"\nprotection_summary = \"refused\"\n"

    // B1-a: Probe prints expected stdout without calling any substantiated library extern -> refused with FS9253.
    let noCallProbe = Path.Combine(work, "NoCallProbe.clef")
    writeLines noCallProbe
        [ "module Seam.LibcAbs.NoCall"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    Console.writeln \"seam:libc-abs:ok\""
          "    0" ]
    let noCallSpec = Path.Combine(work, "no-call.seams.toml")
    File.WriteAllText(
        noCallSpec,
        baseLibcSpec.Replace("entry_source = " + quote libcFx.ProbeAbs, "entry_source = " + quote noCallProbe),
        UTF8Encoding(false))
    let noCallResult =
        SubstantiationSeams.evaluate
            { ManifestPath = libcFx.ManifestJson
              SpecPath = noCallSpec
              OutputDirectory = Some(Path.Combine(work, "no-call-out"))
              WorkDirectory = Some(Path.Combine(work, "no-call-work"))
              Verbose = false }
    noCallResult |> refused "FS9253"
    match noCallResult with
    | Error refusal -> Assert.Contains("references zero foreign symbols from manifest.Boundary.ExternSymbols", refusal.Text)
    | Ok _ -> ()

    // B1-b: Probe attempts to declare a shadow [<FidelityExtern>] -> refused with FS9253.
    let shadowExternProbe = Path.Combine(work, "ShadowExternProbe.clef")
    writeLines shadowExternProbe
        [ "module Seam.LibcAbs.Shadow"
          "[<FidelityExtern(\"c\", \"abs\")>]"
          "let private shadowAbs (x: int) : int = NativeDefault.zeroed ()"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    if shadowAbs (-42) = 42 then"
          "        Console.writeln \"seam:libc-abs:ok\""
          "        0"
          "    else 1" ]
    let shadowSpec = Path.Combine(work, "shadow-extern.seams.toml")
    File.WriteAllText(
        shadowSpec,
        baseLibcSpec.Replace("entry_source = " + quote libcFx.ProbeAbs, "entry_source = " + quote shadowExternProbe),
        UTF8Encoding(false))
    let shadowResult =
        SubstantiationSeams.evaluate
            { ManifestPath = libcFx.ManifestJson
              SpecPath = shadowSpec
              OutputDirectory = Some(Path.Combine(work, "shadow-out"))
              WorkDirectory = Some(Path.Combine(work, "shadow-work"))
              Verbose = false }
    shadowResult |> refused "FS9253"
    match shadowResult with
    | Error refusal -> Assert.Contains("declares [<FidelityExtern>] or extern bindings outside manifest.Sources", refusal.Text)
    | Ok _ -> ()

    // B2: 7 genuine C subject mutants (one per physical seam category).
    // Each compiles a mutated libseam_probe.a while Clef sources, PSG, witness units, and SMT
    // discharge receipts remain intact and verified by Substantiation.validateManifest.
    let baseSource = File.ReadAllText fx.Source
    let basePilotText = File.ReadAllText fx.Pilot
    let baseSpecText = File.ReadAllText fx.SpecPath
    let liveSources =
        fx.Manifest.Sources
        |> Array.map (fun s -> s.Path, File.ReadAllText s.Path)

    // Reorder [[checks]] so the targeted category runs first and aborts parallel execution immediately on kill.
    let prioritizeCheck (checkId: string) (specText: string) : string =
        let parts = specText.Split([| "[[checks]]" |], StringSplitOptions.None)
        let headerPart = parts[0]
        let rest = parts[1..]
        let lastPart = rest[rest.Length - 1]
        let ctrlIdx = lastPart.IndexOf("[[refusal_controls]]", StringComparison.Ordinal)
        let lastCheck, controlsSuffix =
            if ctrlIdx >= 0 then lastPart.Substring(0, ctrlIdx), lastPart.Substring(ctrlIdx)
            else lastPart, ""
        let allChecks = Array.append rest[0 .. rest.Length - 2] [| lastCheck |]
        let target, others =
            allChecks |> Array.partition (fun block -> block.Contains("id = " + quote checkId, StringComparison.Ordinal))
        Assert.Single target |> ignore
        headerPart
        + (Array.append target others |> Array.map (fun b -> "[[checks]]" + b) |> String.concat "")
        + controlsSuffix

    let assertSubjectMutantKilled (label: string) (checkId: string) (expectedFragment: string) (mutateC: string -> string) =
        let mutDir = Path.Combine(work, "mutant-" + label)
        let mutArchDir = Path.Combine(mutDir, "static-lib")
        Directory.CreateDirectory mutArchDir |> ignore
        let mutSource = Path.Combine(mutDir, "seam_probe_mut.c")
        let mutObj = Path.Combine(mutDir, "seam_probe.o")
        let mutArchive = Path.Combine(mutArchDir, "libseam_probe.a")
        let mutatedC = mutateC baseSource
        Assert.NotEqual<string>(baseSource, mutatedC)
        File.WriteAllText(mutSource, mutatedC, UTF8Encoding(false))
        run mutDir ("compile-" + label) (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; "-I"; fx.Work; mutSource; "-o"; mutObj ]) |> ignore
        run mutDir ("archive-" + label) (ClangToolchain.arStartInfo [ "rcs"; mutArchive; mutObj; fx.DeadObject ]) |> ignore
        let mutArchiveSha = ClangToolchain.sha256 mutArchive
        let mutPilot = Path.Combine(mutDir, "SeamProbe.pilot.toml")
        let updatedPilot =
            basePilotText
                .Replace("path = " + quote fx.Archive, "path = " + quote mutArchive)
                .Replace("sha256 = " + quote (ClangToolchain.sha256 fx.Archive), "sha256 = " + quote mutArchiveSha)
        File.WriteAllText(mutPilot, updatedPilot, UTF8Encoding(false))
        ScratchDirectory.within mutDir (fun () -> BindingGenerator.generateFromProjectDiagnosed mutPilot false false Types.LP64)
        |> accepted |> ignore
        let mutProvenance =
            Directory.GetFiles(Path.Combine(mutDir, "generated"), "*.link-provenance.toml", SearchOption.AllDirectories)
            |> Assert.Single
        let mutBoundary =
            SubstantiationBoundary.evaluate
                { FidprojPath = fx.Fidproj
                  PackageName = fx.Manifest.Package
                  LinkLibraries = fx.Manifest.Boundary.Libraries
                  LinkObjects = [||]
                  PilotPath = Some mutPilot
                  LinkProvenancePath = Some mutProvenance
                  Sources = liveSources
                  WorkDirectory = Path.Combine(mutDir, "boundary-work") }
            |> accepted
        let sealedMutant = { fx.Manifest with Boundary = mutBoundary } |> Substantiation.seal
        // Confirm static substantiation revalidation still accepts the defective C archive!
        Substantiation.validateManifest sealedMutant |> accepted
        let mutManifestJson = Path.Combine(mutDir, "SeamProbe.substantiation.json")
        File.WriteAllText(mutManifestJson, Substantiation.toJson sealedMutant, UTF8Encoding(false))
        let mutSpec = Path.Combine(mutDir, label + ".seams.toml")
        File.WriteAllText(mutSpec, prioritizeCheck checkId baseSpecText, UTF8Encoding(false))
        let mutOut = Path.Combine(mutDir, "out")
        let outcome =
            SubstantiationSeams.evaluate
                { ManifestPath = mutManifestJson
                  SpecPath = mutSpec
                  OutputDirectory = Some mutOut
                  WorkDirectory = Some(Path.Combine(mutDir, "seams-work"))
                  Verbose = false }
        outcome |> refused "FS9253"
        Assert.False(Directory.Exists mutOut)
        match outcome with
        | Error refusal -> Assert.Contains(expectedFragment, refusal.Text)
        | Ok _ -> ()

    // 1. scalar-handle-link-and-call mutant: seam_scalar_add subtracts instead of adding.
    assertSubjectMutantKilled "scalar-add-bug" "seam-scalar-call" "seam-scalar-call" (fun c ->
        c.Replace("return a + b;", "return a - b;"))

    // 2. measured-record-abi mutant: C implementation disagrees with header layout on SeamPair offset/size.
    assertSubjectMutantKilled "record-abi-drift" "seam-measured-record-abi" "seam-measured-record-abi" (fun c ->
        c.Replace("if ((int)sizeof(SeamPair) != pair_size) return -2;", "if ((int)(sizeof(SeamPair) + 4) != pair_size) return -2;"))

    // 3. static-dynamic-link-elision mutant: seam_scalar_add pulls seam_unused_dead_symbol into static binary!
    assertSubjectMutantKilled "link-elision-leak" "seam-link-elision" "seam_unused_dead_symbol" (fun c ->
        c.Replace(
            "int seam_scalar_add(int a, int b) {\n    if (!seam_env_ok()) return -999999;\n    return a + b;\n}",
            "extern int seam_unused_dead_symbol(int value);\nint seam_scalar_add(int a, int b) {\n    if (!seam_env_ok()) return seam_unused_dead_symbol(a);\n    return a + b;\n}"))

    // 4. error-sentinel-conformance mutant: seam_checked_divide returns 0 instead of -22 on zero divisor.
    assertSubjectMutantKilled "error-sentinel-drift" "seam-error-sentinel" "seam-error-sentinel" (fun c ->
        c.Replace("if (denominator == 0) return -22;", "if (denominator == 0) return 0;"))

    // 5. owned-handle-lifecycle mutant: seam_handle_close leaks handle without decrementing live count.
    assertSubjectMutantKilled "lifecycle-leak" "seam-owned-lifecycle" "seam-owned-lifecycle" (fun c ->
        c.Replace("free(handle);\n        g_live_count -= 1;\n        g_release_count += 1;", "g_release_count += 1;"))

    // 6. barewire-zero-copy mutant: seam_frame_populate writes corrupted invalid-UTF8 byte into borrowed buffer.
    assertSubjectMutantKilled "zero-copy-corrupt" "seam-barewire-zero-copy" "seam-barewire-zero-copy" (fun c ->
        c.Replace("    return 15L;", "    bytes[0] = 0xFF;\n    return 15L;"))

    // 7. callback-registration-invocation mutant: seam_invoke_callback passes wrong signed 64-bit delta to Clef entry.
    assertSubjectMutantKilled "callback-arg-drift" "seam-callback-invocation" "seam-callback-invocation" (fun c ->
        c.Replace("int r = callback(4294967295U, -173L);", "int r = callback(4294967295U, -172L);"))

[<Fact>]
let ``stripped ELF symtab, probe fidproj synthesis conflicts, multi-archive static linkage, callback adapter reachability, required_symbols, and SharedLibraries ledger binding fail closed`` () =
    let fx = sharedFixture.Value
    let work = scratch "hardening-controls"

    // Run clean evaluation once on SeamProbe so we have retained static and dynamic probe binaries + ledger.
    let cleanOut = Path.Combine(work, "seam-probe-clean-out")
    let cleanRun =
        SubstantiationSeams.evaluate
            { ManifestPath = fx.ManifestJson
              SpecPath = fx.SpecPath
              OutputDirectory = Some cleanOut
              WorkDirectory = Some(Path.Combine(work, "seam-probe-clean-work"))
              Verbose = false }
        |> accepted
    SubstantiationSeams.readLedgerReceipt fx.Manifest cleanRun.LedgerPath |> accepted |> ignore

    // -----------------------------------------------------------------------
    // 1. N1: Stripped ELF binary (SHT_SYMTAB zeroed -> HasSymtab = false) is refused with FS9253.
    // -----------------------------------------------------------------------
    let retainedLinkBin =
        cleanRun.Ledger.RetainedBinaries
        |> Array.find (fun b -> b.CheckId = "seam-link-elision")
    let origBinBytes = File.ReadAllBytes retainedLinkBin.BinaryPath
    let origElf = SubstantiationSeams.inspectElfExecutable retainedLinkBin.BinaryPath |> accepted
    Assert.True origElf.HasSymtab
    let strippedBytes = Array.copy origBinBytes
    let shoff = int (BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(strippedBytes, 40, 8)))
    let shentsize = int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(strippedBytes, 58, 2)))
    let shnum = int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(strippedBytes, 60, 2)))
    let mutable symtabZeroed = false
    for idx in 0 .. shnum - 1 do
        let off = shoff + idx * shentsize
        let shType = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(strippedBytes, off + 4, 4))
        if shType = 2u then
            BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(strippedBytes, off + 4, 4), 0u)
            symtabZeroed <- true
    Assert.True symtabZeroed
    let strippedPath = Path.Combine(work, "probe-stripped-symtab")
    File.WriteAllBytes(strippedPath, strippedBytes)
    let strippedElf = SubstantiationSeams.inspectElfExecutable strippedPath |> accepted
    Assert.False strippedElf.HasSymtab
    let strippedCheckSpec : SubstantiationSeams.PhysicalCheckSpec =
        { Id = "seam-link-elision"
          Category = "static-dynamic-link-elision"
          Summary = "Stripped ELF negative control"
          EntrySource = fx.ProbeScalar
          ExtraSources = [||]
          IncludeRuntimeSources = false
          RequiredSymbols = [||]
          LoadingContract = Some "Landing.Acceptance.Loading.contract"
          ExpectedExitCode = 0
          ExpectedStdout = "seam:link-elision:ok"
          Elf =
            Some
                { ElidedSymbols = [| "seam_unused_dead_symbol" |]
                  RetainedStaticSymbols = [| "seam_scalar_add" |]
                  ForbiddenNeededLibraries = [||]
                  SharedLibrary = None
                  SharedLibrarySha256 = None
                  DynamicProbeSource = None
                  RetainedDynamicImports = [||] } }
    let strippedOutcome =
        SubstantiationSeams.verifyElfExpectationsDiagnosed fx.Manifest strippedCheckSpec strippedElf (Path.Combine(work, "stripped-work"))
    strippedOutcome |> refused "FS9253"
    match strippedOutcome with
    | Error refusal -> Assert.Contains("requires an unstripped ELF .symtab section", refusal.Text)
    | Ok _ -> ()

    // -----------------------------------------------------------------------
    // 2. N2: Dependency, [platform], and [platform].loading conflict refusals in probe fidproj synthesis.
    // -----------------------------------------------------------------------
    // N2-a: [platform].loading conflict through full SubstantiationSeams.evaluate -> FS9253.
    let loadingConflictSpec = Path.Combine(work, "loading-conflict.seams.toml")
    File.WriteAllText(
        loadingConflictSpec,
        (File.ReadAllText fx.SpecPath).Replace(
            "loading_contract = \"Landing.Acceptance.Loading.contract\"",
            "loading_contract = \"Other.Loading.contract\""),
        UTF8Encoding(false))
    let loadingConflictRes =
        SubstantiationSeams.evaluate
            { ManifestPath = fx.ManifestJson
              SpecPath = loadingConflictSpec
              OutputDirectory = Some(Path.Combine(work, "loading-conflict-out"))
              WorkDirectory = Some(Path.Combine(work, "loading-conflict-work"))
              Verbose = false }
    loadingConflictRes |> refused "FS9253"
    match loadingConflictRes with
    | Error refusal -> Assert.Contains("[platform].loading already selects a different contract", refusal.Text)
    | Ok _ -> ()

    // N2-b & N2-c: Platform bundle dependency conflict (:987) and [platform] key conflict (:993).
    let bundleDir = Path.Combine(work, "bundle-conflicts")
    Directory.CreateDirectory bundleDir |> ignore
    let subAPath = Path.Combine(bundleDir, "SubA.fidproj")
    let subBPath = Path.Combine(bundleDir, "SubB.fidproj")
    let bundlePath = Path.Combine(bundleDir, "Bundle.fidproj")
    writeLines subAPath [ "[package]"; "name = \"SubA\""; "version = \"0.1.0\""; "[build]"; "sources = []" ]
    writeLines subBPath [ "[package]"; "name = \"SubB\""; "version = \"0.1.0\""; "[build]"; "sources = []" ]
    writeLines bundlePath
        [ "[package]"
          "name = \"ScratchBundle\""
          "version = \"0.1.0\""
          "[build]"
          "sources = []"
          "[dependencies]"
          "sublib = { path = " + quote subAPath + " }"
          "[platform]"
          "os = \"linux\"" ]

    let depConflictProj = Path.Combine(bundleDir, "DepConflict.fidproj")
    writeLines depConflictProj
        [ "[package]"
          "name = \"DepConflict\""
          "version = \"0.1.0\""
          "[build]"
          "sources = [" + quote fx.ProbeScalar + "]"
          "[dependencies]"
          "bundle = { path = " + quote bundlePath + " }"
          "sublib = { path = " + quote subBPath + " }" ]
    let depConflictManifest = { fx.Manifest with FidprojPath = depConflictProj }
    let depConflictRes =
        SubstantiationSeams.buildProbeFidprojDiagnosed depConflictManifest strippedCheckSpec (Path.Combine(bundleDir, "dep-work"))
    depConflictRes |> refused "FS9253"
    match depConflictRes with
    | Error refusal -> Assert.Contains("Conflicting dependency 'sublib'", refusal.Text)
    | Ok _ -> ()

    let platConflictProj = Path.Combine(bundleDir, "PlatConflict.fidproj")
    writeLines platConflictProj
        [ "[package]"
          "name = \"PlatConflict\""
          "version = \"0.1.0\""
          "[build]"
          "sources = [" + quote fx.ProbeScalar + "]"
          "[dependencies]"
          "bundle = { path = " + quote bundlePath + " }"
          "[platform]"
          "os = \"freebsd\"" ]
    let platConflictManifest = { fx.Manifest with FidprojPath = platConflictProj }
    let platConflictRes =
        SubstantiationSeams.buildProbeFidprojDiagnosed platConflictManifest strippedCheckSpec (Path.Combine(bundleDir, "plat-work"))
    platConflictRes |> refused "FS9253"
    match platConflictRes with
    | Error refusal -> Assert.Contains("Conflicting [platform].os setting", refusal.Text)
    | Ok _ -> ()

    // -----------------------------------------------------------------------
    // 3. N3: Multi-archive static manifest + mismatched library archive name refusal.
    // -----------------------------------------------------------------------
    // N3-a: Even on a single-archive manifest, mismatched library name refuses with FS9253.
    let mismatchLibManifest =
        { fx.Manifest with
            Boundary = { fx.Manifest.Boundary with Libraries = [| "wrong_archive_name" |] } }
    let mismatchLibRes =
        SubstantiationSeams.buildProbeFidprojDiagnosed mismatchLibManifest strippedCheckSpec (Path.Combine(work, "mismatch-lib-work"))
    mismatchLibRes |> refused "FS9253"
    match mismatchLibRes with
    | Error refusal -> Assert.Contains("Static-archive manifest records no archive matching library 'wrong_archive_name'", refusal.Text)
    | Ok _ -> ()

    // N3-b: Two static archives (libseam_alpha.a + libseam_beta.a, with dead symbol in 2nd archive!)
    // synthesize accurate [link] archives mapping, compile via Composer, and verify union archive symbol elision.
    let multiDir = Path.Combine(work, "multi-archive")
    Directory.CreateDirectory multiDir |> ignore
    let alphaC = Path.Combine(multiDir, "alpha.c")
    let betaLiveC = Path.Combine(multiDir, "beta_live.c")
    let betaDeadC = Path.Combine(multiDir, "beta_dead.c")
    let alphaObj = Path.Combine(multiDir, "alpha.o")
    let betaLiveObj = Path.Combine(multiDir, "beta_live.o")
    let betaDeadObj = Path.Combine(multiDir, "beta_dead.o")
    let archiveAlpha = Path.Combine(multiDir, "libseam_alpha.a")
    let archiveBeta = Path.Combine(multiDir, "libseam_beta.a")
    writeLines alphaC [ "int seam_alpha_live(int x) { return x + 19; }" ]
    writeLines betaLiveC [ "int seam_beta_live(int x) { return x + 23; }" ]
    writeLines betaDeadC [ "int seam_beta_dead(int x) { return x * 99; }" ]
    run multiDir "compile-alpha" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; alphaC; "-o"; alphaObj ]) |> ignore
    run multiDir "compile-beta-live" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; betaLiveC; "-o"; betaLiveObj ]) |> ignore
    run multiDir "compile-beta-dead" (ClangToolchain.startInfo [ "-c"; "-O2"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; betaDeadC; "-o"; betaDeadObj ]) |> ignore
    run multiDir "ar-alpha" (ClangToolchain.arStartInfo [ "rcs"; archiveAlpha; alphaObj ]) |> ignore
    run multiDir "ar-beta" (ClangToolchain.arStartInfo [ "rcs"; archiveBeta; betaLiveObj; betaDeadObj ]) |> ignore

    let multiLoading = Path.Combine(multiDir, "MultiLoading.clef")
    File.WriteAllText(
        multiLoading,
        (File.ReadAllText(Path.Combine(repository, "tests", "native-callbacks", "Loading.clef")))
            .Replace("Landing.Acceptance.Loading", "Multi.Loading")
            .Replace("landingprobe", "seam_alpha"),
        UTF8Encoding(false))
    let multiBoundaryClef = Path.Combine(multiDir, "MultiBoundary.clef")
    writeLines multiBoundaryClef
        [ "module Multi.Boundary"
          "[<FidelityExtern(\"seam_alpha\", \"seam_alpha_live\")>]"
          "let seam_alpha_live (x: int) : int = NativeDefault.zeroed ()"
          "[<FidelityExtern(\"seam_beta\", \"seam_beta_live\")>]"
          "let seam_beta_live (x: int) : int = NativeDefault.zeroed ()" ]
    let multiProbeClef = Path.Combine(multiDir, "MultiProbe.clef")
    writeLines multiProbeClef
        [ "module Multi.RunProbe"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    if Multi.Boundary.seam_alpha_live 0 = 19 && Multi.Boundary.seam_beta_live 0 = 23 then 0 else 1" ]
    let dependencies, platform = platformTables ()
    let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
    let multiFidproj = Path.Combine(multiDir, "MultiLib.fidproj")
    writeLines multiFidproj
        [ "[package]"; "name = \"MultiLib\""; "version = \"0.1.0\""
          "[compilation]"; "target = \"cpu\""
          "[dependencies]"; yield! dependencies; "metadata = { path = " + quote metadata + " }"
          "[build]"; "output_kind = \"library\""
          "sources = [" + quote multiLoading + ", " + quote multiBoundaryClef + "]"
          "[platform]"; yield! platform; "loading = \"Multi.Loading.contract\""
          "[link]"; "libraries = [\"seam_alpha\", \"seam_beta\"]" ]
    let multiSources : Substantiation.SourceDigest[] =
        [| { Path = multiLoading
             Sha256 = ClangToolchain.sha256 multiLoading
             ModuleName = "Multi.Loading"
             RuntimeDeclarations = [||]
             CompileTimeDeclarations = [| "Multi.Loading.contract" |]
             ForeignDeclarations = [||] }
           { Path = multiBoundaryClef
             Sha256 = ClangToolchain.sha256 multiBoundaryClef
             ModuleName = "Multi.Boundary"
             RuntimeDeclarations = [||]
             CompileTimeDeclarations = [||]
             ForeignDeclarations = [| "Multi.Boundary.seam_alpha_live"; "Multi.Boundary.seam_beta_live" |] } |]
    let multiManifest =
        { fx.Manifest with
            Package = "MultiLib"
            FidprojPath = multiFidproj
            Sources = multiSources
            Boundary =
                { fx.Manifest.Boundary with
                    LinkMode = "static-archive"
                    Libraries = [| "seam_alpha"; "seam_beta" |]
                    ExternSymbols = [| "seam_alpha_live"; "seam_beta_live" |]
                    Files =
                        [| { Path = archiveAlpha; Sha256 = ClangToolchain.sha256 archiveAlpha; Kind = "static-archive" }
                           { Path = archiveBeta; Sha256 = ClangToolchain.sha256 archiveBeta; Kind = "static-archive" } |] } }
    let multiCheckSpec : SubstantiationSeams.PhysicalCheckSpec =
        { Id = "multi-archive-elision"
          Category = "static-dynamic-link-elision"
          Summary = "Two-archive static elision check"
          EntrySource = multiProbeClef
          ExtraSources = [||]
          IncludeRuntimeSources = false
          RequiredSymbols = [| "seam_alpha_live"; "seam_beta_live" |]
          LoadingContract = Some "Multi.Loading.contract"
          ExpectedExitCode = 0
          ExpectedStdout = ""
          Elf =
            Some
                { ElidedSymbols = [| "seam_beta_dead" |]
                  RetainedStaticSymbols = [| "seam_alpha_live"; "seam_beta_live" |]
                  ForbiddenNeededLibraries = [||]
                  SharedLibrary = None
                  SharedLibrarySha256 = None
                  DynamicProbeSource = None
                  RetainedDynamicImports = [||] } }
    let multiProbeWork = Path.Combine(multiDir, "probe-work")
    Directory.CreateDirectory multiProbeWork |> ignore
    let multiProbeProj = SubstantiationSeams.buildProbeFidprojDiagnosed multiManifest multiCheckSpec multiProbeWork |> accepted
    let multiProbeProjText = File.ReadAllText multiProbeProj
    Assert.Contains("seam_alpha = " + quote archiveAlpha, multiProbeProjText)
    Assert.Contains("seam_beta = " + quote archiveBeta, multiProbeProjText)
    // Also verify missing 2nd archive in multi-archive manifest refuses with FS9253.
    let missingSecondLibManifest =
        { multiManifest with
            Boundary = { multiManifest.Boundary with Libraries = [| "seam_alpha"; "missing_third" |] } }
    SubstantiationSeams.buildProbeFidprojDiagnosed missingSecondLibManifest multiCheckSpec (Path.Combine(multiDir, "missing-2nd-work"))
    |> refused "FS9253"

    // Link real ELF64 binary across BOTH static archives so `seam_beta_dead` (defined ONLY in 2nd archive)
    // is elided by static archive member selection while `seam_alpha_live` and `seam_beta_live` are retained.
    let multiLinkProbeC = Path.Combine(multiProbeWork, "multi_link_probe.c")
    writeLines multiLinkProbeC
        [ "int seam_alpha_live(int x);"
          "int seam_beta_live(int x);"
          "int main(void) { return seam_alpha_live(0) + seam_beta_live(0) == 42 ? 0 : 1; }" ]
    let multiBinary = Path.Combine(multiProbeWork, "multi-probe-bin")
    run multiProbeWork "link-multi-probe"
        (ClangToolchain.startInfo [ "-O2"; "-Wall"; "-Wextra"; "-Werror"; multiLinkProbeC; archiveAlpha; archiveBeta; "-o"; multiBinary ])
    |> ignore
    let multiElf = SubstantiationSeams.inspectElfExecutable multiBinary |> accepted
    let multiElfOutcome =
        SubstantiationSeams.verifyElfExpectationsDiagnosed multiManifest multiCheckSpec multiElf multiProbeWork
        |> accepted
    Assert.Equal(None, multiElfOutcome)
    // Claiming an elided symbol that NEITHER archive defines fails closed with FS9253.
    let badMultiCheckSpec =
        { multiCheckSpec with
            Elf = multiCheckSpec.Elf |> Option.map (fun e -> { e with ElidedSymbols = [| "never_in_either_archive" |] }) }
    SubstantiationSeams.verifyElfExpectationsDiagnosed multiManifest badMultiCheckSpec multiElf multiProbeWork
    |> refused "FS9253"

    // -----------------------------------------------------------------------
    // 4. Finding 5: Unreached Callback_*Entry adapter in library is refused with FS9246 in verifyWitnessUnits
    //    (and unactivated library without Caller.clef fails closed in Substantiation.evaluate).
    // -----------------------------------------------------------------------
    let witnessCopyDir = Path.Combine(work, "unreached-callback-witness")
    Directory.CreateDirectory witnessCopyDir |> ignore
    let copiedArtifacts = Path.Combine(witnessCopyDir, "intermediates")
    Directory.CreateDirectory copiedArtifacts |> ignore
    for file in Directory.GetFiles fx.Manifest.ArtifactsDirectory do
        File.Copy(file, Path.Combine(copiedArtifacts, Path.GetFileName file), true)
    let copiedWitnessPath = Path.Combine(copiedArtifacts, "10_witness_units.json")
    let witnessNode = Json.parseOrFail(File.ReadAllText(copiedWitnessPath).TrimStart('\uFEFF'))
    let mutable removedAdapterDef = false
    let updatedWitnessNode =
        match witnessNode with
        | JsonValue.Object rootFields ->
            rootFields
            |> List.map (fun (k, v) ->
                if k = "units" then
                    let units =
                        Json.items v
                        |> List.map (fun unitNode ->
                            match unitNode with
                            | JsonValue.Object unitFields ->
                                unitFields
                                |> List.map (fun (uk, uv) ->
                                    if uk = "definitions" then
                                        let keptDefs =
                                            Json.items uv
                                            |> List.filter (fun defNode ->
                                                let sym =
                                                    defNode
                                                    |> Json.prop "symbol"
                                                    |> Option.bind Json.asString
                                                    |> Option.defaultValue ""
                                                if sym.Contains("seam_invoke_callback", StringComparison.Ordinal) then
                                                    removedAdapterDef <- true
                                                    false
                                                else
                                                    true)
                                        uk, JsonValue.Array keptDefs
                                    else
                                        uk, uv)
                                |> JsonValue.Object
                            | other -> other)
                    k, JsonValue.Array units
                else
                    k, v)
            |> JsonValue.Object
        | other -> other
    Assert.True removedAdapterDef
    File.WriteAllText(copiedWitnessPath, Json.serialize updatedWitnessNode, UTF8Encoding(false))
    let tamperedAdapterManifest =
        { fx.Manifest with
            ArtifactsDirectory = copiedArtifacts
            Psg0 = { Path = Path.Combine(copiedArtifacts, "01_psg0.json"); Sha256 = ClangToolchain.sha256 (Path.Combine(copiedArtifacts, "01_psg0.json")) }
            WitnessUnits = { Path = copiedWitnessPath; Sha256 = ClangToolchain.sha256 copiedWitnessPath } }
        |> Substantiation.seal
    let unreachedRes = Substantiation.validateManifest tamperedAdapterManifest
    unreachedRes |> refused "FS9246"
    match unreachedRes with
    | Error refusal -> Assert.Contains("Exported callback-entry adapter 'Seam.Probe.Callbacks.seam_invoke_callback' was not reached", refusal.Text)
    | Ok _ -> ()

    // -----------------------------------------------------------------------
    // 5. Finding 6: `required_symbols` spec validation + missing symbol in compiled probe ELF -> FS9253.
    // -----------------------------------------------------------------------
    let unknownReqSymSpec = Path.Combine(work, "unknown-req-sym.seams.toml")
    File.WriteAllText(
        unknownReqSymSpec,
        (File.ReadAllText fx.SpecPath).Replace(
            "required_symbols = [\"seam_env_ok\", \"seam_scalar_add\"]",
            "required_symbols = [\"symbol_not_in_boundary\"]"),
        UTF8Encoding(false))
    let unknownReqSymRes =
        SubstantiationSeams.evaluate
            { ManifestPath = fx.ManifestJson
              SpecPath = unknownReqSymSpec
              OutputDirectory = Some(Path.Combine(work, "unknown-req-sym-out"))
              WorkDirectory = Some(Path.Combine(work, "unknown-req-sym-work"))
              Verbose = false }
    unknownReqSymRes |> refused "FS9253"
    match unknownReqSymRes with
    | Error refusal -> Assert.Contains("absent from manifest.Boundary.ExternSymbols", refusal.Text)
    | Ok _ -> ()

    // Probe calls `seam_env_ok` (so general library-call check passes!) while `required_symbols` also requires `seam_checked_divide`.
    let envOnlyProbe = Path.Combine(work, "EnvOnlyProbe.clef")
    writeLines envOnlyProbe
        [ "module Seam.Probe.EnvOnly"
          "[<EntryPoint>]"
          "let main _ : int ="
          "    if Seam.Probe.Boundary.seam_env_ok () = 1 then"
          "        Console.writeln \"seam:scalar:ok\""
          "        0"
          "    else 1" ]
    let missingReqSymSpec = Path.Combine(work, "missing-req-sym.seams.toml")
    File.WriteAllText(
        missingReqSymSpec,
        (File.ReadAllText fx.SpecPath)
            .Replace("entry_source = " + quote fx.ProbeScalar, "entry_source = " + quote envOnlyProbe)
            .Replace(
                "required_symbols = [\"seam_env_ok\", \"seam_scalar_add\"]",
                "required_symbols = [\"seam_env_ok\", \"seam_checked_divide\"]"),
        UTF8Encoding(false))
    let missingReqSymRes =
        SubstantiationSeams.evaluate
            { ManifestPath = fx.ManifestJson
              SpecPath = missingReqSymSpec
              OutputDirectory = Some(Path.Combine(work, "missing-req-sym-out"))
              WorkDirectory = Some(Path.Combine(work, "missing-req-sym-work"))
              Verbose = false }
    missingReqSymRes |> refused "FS9253"
    match missingReqSymRes with
    | Error refusal -> Assert.Contains("omits required foreign symbol 'seam_checked_divide'", refusal.Text)
    | Ok _ -> ()

    // -----------------------------------------------------------------------
    // 6. Finding 7: SharedLibraries 1-to-1 coverage and DynamicProbeBinarySha256 live binding in readLedgerReceipt.
    // -----------------------------------------------------------------------
    let sharedLedgerDir = Path.Combine(work, "shared-ledger-checks")
    Directory.CreateDirectory sharedLedgerDir |> ignore
    let writeModifiedProbeLedger label (mutate: SubstantiationSeams.SeamsLedgerReceipt -> SubstantiationSeams.SeamsLedgerReceipt) =
        let p = Path.Combine(sharedLedgerDir, label + ".seams-ledger.json")
        File.WriteAllText(p, serializeSeamsLedgerReceipt (mutate cleanRun.Ledger), UTF8Encoding(false))
        SubstantiationSeams.readLedgerReceipt fx.Manifest p

    // Omitting SharedLibraries entry for `seam-link-elision` -> refused with FS9253.
    writeModifiedProbeLedger "missing-shared-entry" (fun l -> { l with SharedLibraries = [||] })
    |> refused "FS9253"

    // Tampering with DynamicProbeBinarySha256 on real dynamic probe binary -> refused with FS9253.
    writeModifiedProbeLedger "tampered-dyn-bin-sha" (fun l ->
        { l with
            SharedLibraries =
                l.SharedLibraries |> Array.map (fun s -> { s with DynamicProbeBinarySha256 = String.replicate 64 "a" }) })
    |> refused "FS9253"

    // Tampering with SharedLibrarySha256 on valid CheckId `seam-link-elision` -> refused with FS9253.
    writeModifiedProbeLedger "tampered-shared-lib-sha" (fun l ->
        { l with
            SharedLibraries =
                l.SharedLibraries |> Array.map (fun s -> { s with SharedLibrarySha256 = String.replicate 64 "d" }) })
    |> refused "FS9253"
