module Farscape.Tests.LibcNativeJourneysTests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Farscape.Core
open Farscape.Tests.SubstantiationTests
open Farscape.Tests.NativeCallbackRunnerTests.DischargeReceipts

// Tranche 1: #163, #45, #46 and the malloc/free and process-exit portions of #164/#165.
// This module owns only real-libc journeys. Production receipt readers remain authoritative.
// All generated sources, binaries, logs and receipts use the process-owned test scratch root.
let private write path (text: string) = File.WriteAllText(path, text, UTF8Encoding(false))
let private lines path text = write path (String.concat "\n" text + "\n")
let private quoted values = values |> List.map quote |> String.concat ", "

let private accepted = function
    | Ok value -> value
    | Error (refusal: Diagnostics.Refusal) -> failwithf "%s\n%A" refusal.Text refusal.Diagnostics

let private withScratch label body =
    let work = TestHelpers.TestScratch.create ("libc-native-" + label)
    try ScratchDirectory.within work (fun () -> body work)
    finally Directory.Delete(work, true)

type private Outcome = { Id: int; ExitCode: int; Stdout: string; Stderr: string }

let private execute work label (timeout: int) (input: byte[] option) (info: ProcessStartInfo) =
    info.WorkingDirectory <- work
    info.Environment["MALLOC_PERTURB_"] <- "165"
    for key in [ "TMPDIR"; "TMP"; "TEMP" ] do info.Environment[key] <- work
    info.RedirectStandardInput <- input.IsSome
    use child = new Process(StartInfo = info)
    Assert.True(child.Start(), label)
    let id = child.Id
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    match input with
    | Some bytes ->
        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
        child.StandardInput.Close()
    | None -> ()
    if not (child.WaitForExit timeout) then
        child.Kill(true)
        child.WaitForExit()
        failwithf "%s exceeded %d ms" label timeout
    child.WaitForExit()
    let result = { Id = id; ExitCode = child.ExitCode; Stdout = stdout.Result; Stderr = stderr.Result }
    lines (Path.Combine(work, label + ".log"))
        [ sprintf "pid=%d exit=%d" id result.ExitCode; result.Stdout; result.Stderr ]
    result

let private succeeded label outcome =
    Assert.True(outcome.ExitCode = 0, sprintf "%s exited %d:\n%s%s" label outcome.ExitCode outcome.Stdout outcome.Stderr)
    Assert.Empty outcome.Stderr

let private project work name kind sources =
    let dependencies, platform = platformTables ()
    let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
    let path = Path.Combine(work, name + ".fidproj")
    lines path
        [ "[package]"; "name = " + quote name; "version = \"1.0.0\""
          "[compilation]"; "target = \"cpu\""
          "[build]"; "output_kind = " + quote kind; "sources = [" + quoted sources + "]"
          "[dependencies]"; yield! dependencies
          "metadata = { path = " + quote metadata + " }"
          "[platform]"; yield! platform
          if sources |> List.exists (fun path -> Path.GetFileName path = "Loading.clef") then
              "loading = \"Landing.Acceptance.Loading.contract\""
          "[link]"; "libraries = [\"c\"]" ]
    path

let private readContract = """
[[options.bindings]]
name = "read"
symbol = "read"
reference_parameters = [1]
reference_elements = ["", "unsigned char"]
writable_buffers = [{ parameter = 1, count_parameter = 2, hypothesis = { identity = "posix.read.borrow", version = "2024", source = "POSIX read(2)" } }]
result_range = { identity = "posix.read.range", version = "2024", source = "POSIX read(2)", lower = { constant = -1 }, upper = { argument = 2 } }
[[options.bindings]]
name = "close"
symbol = "close"
result_range = { identity = "posix.close.range", version = "2024", source = "POSIX close(2)", lower = { constant = -1 }, upper = { constant = 0 } }
"""

let private pilotText name headers functions contracts =
    String.concat "\n"
        [ "[library]"; "name = \"c\""; "headers = [" + quoted headers + "]"
          "[output]"; "mode = \"fidelity\""; "directory = \"generated\""
          "[error_conventions]"; "default = \"none\""
          "[annotations.nonnull]"; "free = [0]"; "atexit = [0]"
          "[options]"; "c_header_mode = true"; "descriptor_only_dependencies = true"; "link_libraries = true"
          contracts
          "[[namespace]]"; "name = " + quote (name + ".Boundary"); "library = \"c\""
          "description = \"Real system libc native journeys\""; "functions = [" + quoted functions + "]" ]

type private Fixture =
    { Work: string; Name: string; Pilot: string; Project: string; Boundary: string; Sources: string list }

let private generate work name headers functions contracts =
    Directory.CreateDirectory work |> ignore
    for header in headers do requiredFile "system libc header" header |> ignore
    let pilot = Path.Combine(work, name + ".pilot.toml")
    write pilot (pilotText name headers functions contracts)
    BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64 |> accepted |> ignore
    let at relative = Path.Combine(work, "generated", relative)
    let boundary = requiredFile "generated Boundary.clef" (at "Boundary/Boundary.clef")
    let loading = Path.Combine(work, "Loading.clef")
    let loadingTemplate = Path.Combine(__SOURCE_DIRECTORY__, "..", "native-callbacks", "Loading.clef")
    File.ReadAllText(loadingTemplate).Replace("landingprobe", "c")
        .Replace("tests/native-callbacks/probe.c", "/usr/include/stdlib.h")
        .Replace("prebuilt probe archive", "system libc")
    |> write loading
    let sources = [ loading; at "Types.clef"; at "Boundary/Types.clef"; boundary ] |> List.filter File.Exists
    let caller = Path.Combine(work, "Caller.clef")
    let callbackType =
        sources |> List.map File.ReadAllText |> String.concat "\n"
        |> fun source -> Regex.Match(source, @"type\s+(Callback_\w+)\s*=").Groups[1].Value
    lines caller
        [ "module " + name + ".Caller"
          "open " + name + ".Boundary"
          if callbackType <> "" then "open " + name + ".Boundary.Types"
          if List.contains "atexit" functions then
              if List.contains "_exit" functions then
                  "let private atExit () : unit = _exit 7"
              else
                  "let private atExit () : unit = ()"
              "let registrationSample () : int ="
              "    atexit { " + callbackType + ".Invoke = FnPtr.ofFunction atExit }"
          elif List.contains "getpid" functions then
              "let sample () : int ="
              "    if getpid () > 0 then 1 else 0" ]
    let path = project work name "library" (sources @ [ caller ])
    { Work = work; Name = name; Pilot = pilot; Project = path; Boundary = boundary; Sources = sources }

let private substantiate fx =
    let manifest =
        Substantiation.evaluate
            { FidprojPath = fx.Project; PilotPath = Some fx.Pilot; LinkProvenancePath = None
              OutputDirectory = Some(Path.Combine(fx.Work, "manifest"))
              WorkDirectory = Some(Path.Combine(fx.Work, "substantiation"))
              AllowWarnings = false; Verbose = false }
        |> accepted
    Assert.False manifest.AllowWarnings
    Assert.True manifest.WarningsAsErrors
    Assert.Equal("runtime-shared-library", manifest.Boundary.LinkMode)
    Assert.NotEqual<string>("not-measured", manifest.Boundary.ClangTarget)
    let proof = manifest.Proof
    Assert.True(proof.TotalObligations > 0)
    Assert.True(proof.LibraryObligations > 0)
    Assert.True(proof.HarnessObligations > 0)
    Assert.Equal(proof.TotalObligations, proof.LibraryObligations + proof.HarnessObligations)
    Assert.Equal(proof.TotalObligations, proof.PremisesSatCount)
    Assert.Equal(proof.TotalObligations, proof.DischargedUnsatCount)
    Assert.Equal(proof.LibraryObligations, proof.LibraryPremisesSatCount)
    Assert.Equal(proof.LibraryObligations, proof.LibraryDischargedUnsatCount)
    Assert.Equal(0, proof.PendingOpenCount)
    Assert.Equal(0, proof.RefutedCount)
    SubstantiationSeams.verifyDischarge manifest |> accepted |> ignore
    manifest

let private readReceipt = function
    | Ok value -> value
    | Error reason -> failwithf "Discharge receipt refused: %A" reason

let private census checkPremises artifacts =
    let at name = requiredFile name (Path.Combine(artifacts, name))
    let ids = File.ReadAllText(at "06a_obligations.json") |> obligationIds |> readReceipt
    let receipt = File.ReadAllText(at "06c_psg_discharge.json") |> parse |> readReceipt
    let querySha = ClangToolchain.sha256 (at "06b_obligations.smt2")
    let expected =
        { ExpectedStage = "psg"; InputFileSha256 = querySha; QueryFileSha256 = querySha; ObligationIds = ids }
    Assert.Empty(refusalsOf expected receipt)
    Assert.NotEmpty ids
    if checkPremises then
        let premises = File.ReadAllLines(at "06b_premises_sat.stdout") |> Array.filter (String.IsNullOrWhiteSpace >> not)
        Assert.Equal(ids.Length, premises.Length)
        Assert.All(premises, fun verdict -> Assert.Equal("sat", verdict.Trim()))
    for tool in receipt.Tools do
        Assert.Equal(tool.ExecutableSha256.ToLowerInvariant(), (ClangToolchain.sha256 tool.Executable).ToLowerInvariant())
    ids.Length

let private compile work name sources =
    let path = project work name "console" sources
    let binary = Path.Combine(work, name)
    let composer = sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll"
    let launch = ProcessLaunch.startInfo "dotnet" [ composer; "compile"; "--warn-as-error"; "--no-color"; path; "-k"; "-o"; binary ]
    // Observe the backend's actual opt and LLD invocations, forwarding to fixed real tools.
    let toolDirectory = Path.Combine(work, name + "-tools")
    Directory.CreateDirectory toolDirectory |> ignore
    let searchPath = Environment.GetEnvironmentVariable "PATH"
    let shellQuote (value: string) = "'" + value.Replace("'", "'\\''") + "'"
    for tool in [ "opt"; "ld.lld" ] do
        let actual =
            searchPath.Split(Path.PathSeparator)
            |> Array.map (fun directory -> Path.Combine(directory, tool))
            |> Array.find File.Exists |> Path.GetFullPath
        let shim = Path.Combine(toolDirectory, tool)
        let observation = Path.Combine(toolDirectory, tool + ".invoked")
        write shim ("#!/bin/sh\nprintf '%s\\n' " + shellQuote actual + " >> " + shellQuote observation
                    + "\nexec " + shellQuote actual + " \"$@\"\n")
        File.SetUnixFileMode(shim, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        write (Path.Combine(toolDirectory, tool + ".sha256")) (ClangToolchain.sha256 actual)
    launch.Environment["PATH"] <- toolDirectory + string Path.PathSeparator + searchPath
    let outcome = execute work (name + "-compile") 300000 None launch
    binary, outcome

let private inspectBinary work binary symbols =
    let elf = SubstantiationSeams.inspectElfExecutable binary |> accepted
    Assert.Contains("libc.so.6", elf.NeededLibraries)
    for symbol in symbols do Assert.Contains(symbol, elf.UndefinedDynsym)
    Assert.Equal(ClangToolchain.sha256 binary, elf.Sha256)
    let onPath tool =
        Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
        |> Array.map (fun directory -> Path.Combine(directory, tool))
        |> Array.find File.Exists |> Path.GetFullPath
    let backendTools =
        let directory = Path.Combine(work, Path.GetFileName(binary) + "-tools")
        [ for tool in [ "opt"; "ld.lld" ] do
              let invokedPath = Path.Combine(directory, tool + ".invoked")
              if Directory.Exists directory && File.Exists invokedPath then
                  let invoked = File.ReadAllLines invokedPath
                  let actual = Assert.Single(Array.distinct invoked)
                  Assert.Equal(File.ReadAllText(Path.Combine(directory, tool + ".sha256")), ClangToolchain.sha256 actual)
                  yield "invoked." + tool, actual
              else
                  yield "path-candidate." + tool, onPath tool ]
    let tools =
        [ "clang", ClangToolchain.path ()
          "Composer.dll", sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll" ]
        @ backendTools
    let receipt =
        [ yield "binary=" + binary
          yield "sha256=" + elf.Sha256
          for name, path in tools do
              yield name + "=" + path
              yield name + ".sha256=" + ClangToolchain.sha256 path ]
    lines (Path.Combine(work, Path.GetFileName(binary) + ".toolchain-receipt.txt")) receipt
    Console.WriteLine(String.concat "\n" receipt)
    elf.Sha256

// Clef's generated spelling is int; the descriptor fixes signed64 ssize_t and unsigned64 size_t.
// Unsuffixed literals are constrained by those contracts, as in ProbeZeroCopy.
// Writable-buffer artifacts forbid intrinsic Console writes in the same binary, so this
// probe communicates positive (0), EOF (2), and error-path outcomes via distinct exit codes.
let private ioProbe = """module Libc163.Run
open Libc163.Boundary
let private readStep () : int =
    let buffer = Array.zeroCreate 15
    let count = read 0 buffer 15
    if count < 0 then 11
    elif count = 0 then 12
    else
        let prefix = eager (Array.sub buffer 0 count)
        match String.tryFromBytes prefix with
        | ValueSome text -> text.Length
        | ValueNone -> 14
[<EntryPoint>]
let main _ : int =
    let pid = getpid ()
    let step = readStep ()
    let closed = close 0
    let badClose = close 0
    let invalid = close (-1)
    if pid > 0 && closed = 0 && badClose = -1 && invalid = -1 then
        if step = 15 then 0
        elif step = 12 then 2
        else 1
    else 1
"""

let private ioEbadfProbe =
    ioProbe.Replace("module Libc163.Run", "module Libc163.Ebadf")
        .Replace("    let step = readStep ()\n    let closed = close 0\n    let badClose = close 0\n    let invalid = close (-1)\n    if pid > 0 && closed = 0 && badClose = -1 && invalid = -1 then\n        if step = 15 then 0\n        elif step = 12 then 2\n        else 1",
                 "    let closed = close 0\n    let step = readStep ()\n    let badClose = close 0\n    let invalid = close (-1)\n    if pid > 0 && closed = 0 && step = 11 && badClose = -1 && invalid = -1 then 0")

[<Fact>]
let ``163 real libc getpid close read positive EOF and EBADF retain ELF and discharge receipts`` () =
    withScratch "163" (fun work ->
        let fx = generate work "Libc163" [ "/usr/include/unistd.h" ] [ "getpid"; "close"; "read" ] readContract
        let boundary = File.ReadAllText fx.Boundary
        for evidence in [ "Expr<WritableBufferDescriptor>"; "Expr<ResultRangeDescriptor>"; "Upper = Argument 2"
                          "Integer (Unsigned, 8)"; "Integer (Unsigned, 64)"; "Integer (Signed, 64)" ] do
            Assert.Contains(evidence, boundary)
        let manifest = substantiate fx
        Assert.Equal(manifest.Proof.TotalObligations, census true manifest.ArtifactsDirectory)
        let entry = Path.Combine(work, "ProbeLibc163.clef")
        write entry ioProbe
        let binary, built = compile work "ProbeLibc163" (fx.Sources @ [ entry ])
        Assert.True(built.ExitCode = 0, built.Stdout + built.Stderr)
        let digest = inspectBinary work binary [ "getpid"; "close"; "read" ]
        census false (Path.Combine(work, "targets", "intermediates")) |> ignore
        let ebadfEntry = Path.Combine(work, "ProbeLibc163Ebadf.clef")
        Assert.NotEqual<string>(ioProbe, ioEbadfProbe)
        write ebadfEntry ioEbadfProbe
        let ebadfBinary, ebadfBuilt = compile work "ProbeLibc163Ebadf" (fx.Sources @ [ ebadfEntry ])
        Assert.True(ebadfBuilt.ExitCode = 0, ebadfBuilt.Stdout + ebadfBuilt.Stderr)
        let ebadfDigest = inspectBinary work ebadfBinary [ "getpid"; "close"; "read" ]
        census false (Path.Combine(work, "targets", "intermediates")) |> ignore
        let payload = Encoding.UTF8.GetBytes "farscape-163-ok"
        Assert.Equal(15, payload.Length)
        for label, exe, expectedDigest, input, expectedCode in
            [ "positive", binary, digest, payload, 0
              "empty", binary, digest, [||], 2
              "ebadf", ebadfBinary, ebadfDigest, [||], 0 ] do
            let outcome = ProcessLaunch.startInfo exe [] |> execute work label 10000 (Some input)
            Assert.True(outcome.ExitCode = expectedCode, sprintf "%s exited %d (expected %d):\n%s%s" label outcome.ExitCode expectedCode outcome.Stdout outcome.Stderr)
            Assert.Empty outcome.Stderr
            Assert.True(outcome.Id > 0)
            Assert.Equal(expectedDigest, ClangToolchain.sha256 exe))

let private compilerCodes text =
    Regex.Matches(text, @"\berror\s+((?:CCS|FS)\d{4}):")
    |> Seq.cast<Match> |> Seq.map (fun item -> item.Groups[1].Value) |> Set.ofSeq

let private expectCompilerRefusal codes binary outcome =
    Assert.NotEqual(0, outcome.ExitCode)
    Assert.False(File.Exists binary, "refused compilation produced a binary")
    let output = outcome.Stdout + outcome.Stderr
    Assert.DoesNotContain("Unhandled exception", output)
    Assert.True(compilerCodes output = Set.ofList codes, output)

let private descriptorControl work name contracts =
    let directory = Path.Combine(work, name)
    Directory.CreateDirectory directory |> ignore
    let pilot = Path.Combine(directory, name + ".pilot.toml")
    write pilot (pilotText "Libc163" [ "/usr/include/unistd.h" ] [ "getpid"; "close"; "read" ] contracts)
    pilot

[<Fact>]
let ``163 exact descriptor and compiler refusal controls fail without crashes or binaries`` () =
    withScratch "refusals" (fun work ->
        for code, before, after in
            [ "FS9155", "count_parameter = 2", "count_parameter = 1"
              "FS9156", "reference_elements = [\"\", \"unsigned char\"]", "reference_elements = [\"\", \"int\"]"
              "FS9152", "lower = { constant = -1 }, upper = { argument = 2 }", "lower = { constant = 5 }, upper = { constant = 4 }"
              "FS9151", "upper = { argument = 2 }", "upper = { argument = 1 }"
              "FS9134", "source = \"POSIX read(2)\"", "source = \" \""
              "FS9134", "identity = \"posix.read.borrow\"", "identity = \" \"" ] do
            let pilot = descriptorControl work code (readContract.Replace(before, after))
            BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64 |> refused code
        let fx = generate work "Libc163" [ "/usr/include/unistd.h" ] [ "getpid"; "close"; "read" ] readContract
        // Sixteen bytes cannot fit the exact fifteen-byte allocation.
        let entry = Path.Combine(work, "BadRead.clef")
        write entry "module Libc163.BadRead\n[<EntryPoint>]\nlet main _ : int =\n    let buffer = Array.zeroCreate 15\n    let result = Libc163.Boundary.read 0 buffer 16\n    if result = 0 then 0 else 1\n"
        let binary, outcome = compile work "BadRead" (fx.Sources @ [ entry ])
        expectCompilerRefusal [ "CCS8403" ] binary outcome
        // A floating witness signature contradicts the signed32 native close descriptor.
        let corrupted = Path.Combine(work, "CorruptedBoundary.clef")
        let original = File.ReadAllText fx.Boundary
        let altered = Regex.Replace(original, @"(let close\s+\([^\n]+\)\s*:\s*)int", "${1}float")
        Assert.NotEqual<string>(original, altered)
        write corrupted altered
        let entry = Path.Combine(work, "BadClose.clef")
        write entry "module Libc163.BadClose\n[<EntryPoint>]\nlet main _ : int =\n    let result = Libc163.Boundary.close (-1)\n    if result = 0.0 then 0 else 1\n"
        let sources = fx.Sources |> List.map (fun path -> if path = fx.Boundary then corrupted else path)
        let binary, outcome = compile work "BadClose" (sources @ [ entry ])
        expectCompilerRefusal [ "CCS8403"; "CCS8207" ] binary outcome)

let private mallocFreeContracts ns =
    $"""
[[options.bindings]]
name = "malloc"
symbol = "malloc"
return_handle = "unit"
resource = {{ kind = "owned_nullable_return", release_binding = "{ns}.Boundary.free", release_parameter = 0, hypothesis = {{ identity = "libc.malloc.owned", version = "1", source = "ISO C malloc/free" }} }}
[[options.bindings]]
name = "free"
symbol = "free"
parameter_handles = ["unit"]
"""

let private resourceContracts =
    mallocFreeContracts "LibcRealSuite" + """
[[options.bindings]]
name = "atexit"
symbol = "atexit"
nonnull_callbacks = [0]
callback_invocations = [{ parameter = 0, identity = "libc.atexit.callback", version = "1", source = "ISO C atexit/exit", scope = "process_exit", exit_binding = "LibcRealSuite.Boundary.exit", success_status = 0 }]
"""

let private scalarProbe = """module LibcRealSuite.Scalar
[<EntryPoint>]
let main _ : int =
    let pid = LibcRealSuite.Boundary.getpid ()
    let absolute = LibcRealSuite.Boundary.labs (-4294967297)
    if pid > 0 && absolute = 4294967297 then
        Console.writeln "scalar:ok"
        0
    else 1
"""

let private lifecycleProbe = """module LibcRealSuite.Lifecycle
open LibcRealSuite.Boundary
let private allocate () : int =
    match malloc 32 with
    | Some handle ->
        let alias = handle
        free alias
        1
    | None -> 0
let private impossible () : int =
    match malloc 18446744073709551615 with
    | Some handle ->
        free handle
        0
    | None -> 1
[<EntryPoint>]
let main _ : int =
    let allocated = allocate ()
    let denied = impossible ()
    if allocated = 1 && denied = 1 then
        Console.writeln "lifecycle:some:none:ok"
        0
    else 1
"""

let private rangeProbe descriptor =
    """module LibcRealSuite.Range
open LibcRealSuite.Boundary
let private readOnce () : int =
    let buffer = Array.zeroCreate 15
    let result = read @FD@ buffer 15
    if result < 0 then 11
    elif result = 0 then 12
    else
        let prefix = eager (Array.sub buffer 0 result)
        match String.tryFromBytes prefix with
        | ValueSome text -> text.Length
        | ValueNone -> 14
[<EntryPoint>]
let main _ : int =
    let positive = readOnce ()
    let closed = close @FD@
    let badClose = close @FD@
    if positive = 15 && closed = 0 && badClose = -1 then
        let _ = putchar 111
        let _ = putchar 107
        let _ = putchar 10
        0
    else 1
"""
    |> fun source -> source.Replace("@FD@", string descriptor)

let private callbackProbe =
    """module LibcRealSuite.Callback
open LibcRealSuite.Boundary
[<EntryPoint>]
let main _ : int =
    let registered = LibcRealSuite.Caller.registrationSample ()
    if registered = 0 then
        Console.writeln "callback:explicit-exit"
        exit 0
        91
    else 1
"""

let private checkSpec id category entry symbols exitCode expected =
    [ "[[checks]]"; "id = " + quote id; "category = " + quote category
      "summary = " + quote ("Real libc " + id); "entry_source = " + quote entry
      "include_runtime_sources = true"
      "required_symbols = [" + quoted symbols + "]"; "expected_exit_code = " + string exitCode
      "expected_stdout = " + quote expected ]

let private refusalSpec id kind target code =
    [ "[[refusal_controls]]"; "id = " + quote id; "kind = " + quote kind; "target = " + quote target
      "expected_code = " + quote code; "misuse_summary = " + quote ("Fault-injection negative control: " + id)
      "protection_summary = " + quote ("Exact coded refusal " + code) ]

let private mismatchedRelease = """
[[options.bindings]]
name = "fopen"
resource = { kind = "owned_nullable_return", release_binding = "LibcBadRelease.Boundary.free", release_parameter = 0, hypothesis = { identity = "libc.file.owned", version = "1", source = "ISO C fopen/fclose" } }
"""

[<Fact>]
let ``45 46 real libc multi-contract seams census and dossier reconcile with malloc and process exit`` () =
    withScratch "suite" (fun work ->
        let fx =
            generate work "LibcRealSuite" [ "/usr/include/unistd.h"; "/usr/include/stdlib.h"; "/usr/include/stdio.h" ]
                [ "getpid"; "labs"; "read"; "close"; "malloc"; "free"; "atexit"; "exit"; "_exit"; "putchar" ]
                (readContract + resourceContracts)
        let source = fx.Sources |> List.map File.ReadAllText |> String.concat "\n"
        let callbackType = Regex.Match(source, @"type\s+(Callback_\w+)\s*=").Groups[1].Value
        Assert.False(String.IsNullOrWhiteSpace callbackType, source)
        for evidence in [ "OwnedNullableReturn"; "Expr<ProcessExitCallbackDescriptor>"; "libc.malloc.owned"; "libc.atexit.callback" ] do
            Assert.Contains(evidence, source)
        let manifest = substantiate fx
        Assert.Equal(manifest.Proof.TotalObligations, census true manifest.ArtifactsDirectory)
        // The seam runner inherits only this pipe's read end. It supplies a real positive
        // libc read without changing the test process's stdin or ambient environment.
        use input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)
        let payload = Encoding.UTF8.GetBytes "farscape-163-ok"
        input.Write(payload, 0, payload.Length)
        input.SafePipeHandle.Close()
        let descriptor = input.ClientSafePipeHandle.DangerousGetHandle().ToInt32()
        Assert.True(descriptor > 2)
        let probe name callSample (text: string) =
            let path = Path.Combine(work, name + ".clef")
            let activated =
                if callSample then
                    Regex.Replace(
                        text,
                        @"(?m)^(let main (?:_|args) : int =)\n",
                        "$1\n    let _ = if LibcRealSuite.Boundary.getpid () < 0 then LibcRealSuite.Caller.registrationSample () else 1\n")
                else text
            if callSample then Assert.NotEqual<string>(text, activated)
            write path activated
            path
        let scalar = probe "ProbeScalar" true scalarProbe
        let lifecycle = probe "ProbeLifecycle" true lifecycleProbe
        let range = probe "ProbeRange" true (rangeProbe descriptor)
        let callback = probe "ProbeCallback" false callbackProbe
        let controlWork = Path.Combine(work, "control")
        let controlFx =
            generate controlWork "LibcControl" [ "/usr/include/stdlib.h" ]
                [ "malloc"; "free" ]
                (mallocFreeContracts "LibcControl")
        let leak = probe "Leak" false "module LibcControl.Leak\n[<EntryPoint>]\nlet main _ : int =\n    match LibcControl.Boundary.malloc 32 with\n    | Some handle -> if handle = handle then 0 else 1\n    | None -> 1\n"
        let leakProject = project controlWork "Leak" "console" (controlFx.Sources @ [ leak ])
        let closure =
            probe "Closure" false
                ("module LibcRealSuite.Closure\nopen LibcRealSuite.Boundary\nopen LibcRealSuite.Boundary.Types\n[<EntryPoint>]\nlet main _ : int =\n    let captured = LibcRealSuite.Boundary.getpid ()\n    let entry () : unit =\n        let _ = LibcRealSuite.Boundary.close captured\n        ()\n    LibcRealSuite.Boundary.atexit { " + callbackType + ".Invoke = FnPtr.ofFunction entry }\n")
        let forged =
            probe "Forged" false
                "module LibcControl.Forged\n[<EntryPoint>]\nlet main _ : int =\n    let handle: CHandle<unit> = NativeDefault.zeroed ()\n    if handle = handle then 0 else 1\n"
        let forgedProject = project controlWork "Forged" "console" (controlFx.Sources @ [ forged ])
        let badRelease = Path.Combine(work, "BadRelease.pilot.toml")
        write badRelease
            (pilotText "LibcBadRelease" [ "/usr/include/stdio.h"; "/usr/include/stdlib.h" ] [ "fopen"; "free" ] mismatchedRelease)
        // Exercise these controls even if a later physical seam cannot yet be admitted.
        BindingGenerator.generateFromProjectDiagnosed badRelease false false Types.LP64 |> refused "FS9154"
        let leakedBinary, leakedOutcome = compile controlWork "LeakedAllocation" (controlFx.Sources @ [ leak ])
        expectCompilerRefusal [ "CCS8403" ] leakedBinary leakedOutcome
        let closureBinary, closureOutcome = compile work "CapturingCallback" (fx.Sources @ [ closure ])
        Assert.NotEqual(0, closureOutcome.ExitCode)
        Assert.False(File.Exists closureBinary, "refused closure compilation produced a binary")
        let closureCodes = compilerCodes (closureOutcome.Stdout + closureOutcome.Stderr)
        Assert.Contains("CCS8096", closureCodes)
        Assert.Contains("CCS8207", closureCodes)
        let spec = Path.Combine(work, "LibcRealSuite.seams.toml")
        lines spec
            [ "schema_version = 1"; "target_triple = " + quote manifest.Boundary.ClangTarget
              yield! checkSpec "scalar" "scalar-handle-link-and-call" scalar [ "getpid"; "labs" ] 0 "scalar:ok"
              yield! checkSpec "lifecycle" "owned-handle-lifecycle" lifecycle [ "malloc"; "free" ] 0 "lifecycle:some:none:ok"
              yield! checkSpec "callback" "callback-registration-invocation" callback [ "atexit"; "exit"; "_exit" ] 7 "callback:explicit-exit"
              yield! checkSpec "range" "barewire-zero-copy" range [ "read"; "close" ] 0 "ok"
              yield! refusalSpec "release-mismatch" "pilot" badRelease "FS9154"
              yield! refusalSpec "leaked-allocation" "composer" leakProject "CCS8403"
              yield! refusalSpec "forged-handle" "composer" forgedProject "CCS8010" ]
        let generated =
            SubstantiationSeams.evaluate
                { ManifestPath = Path.Combine(work, "manifest", fx.Name + ".substantiation.json")
                  SpecPath = spec; OutputDirectory = Some(Path.Combine(work, "seams"))
                  WorkDirectory = Some(Path.Combine(work, "seams-work")); Verbose = false }
            |> accepted
        Assert.Equal(manifest.Proof.TotalObligations, generated.Discharge.TotalObligations)
        Assert.Equal(generated.Discharge.TotalObligations, generated.Discharge.PremisesSatCount)
        Assert.Equal(generated.Discharge.TotalObligations, generated.Discharge.DischargedUnsatCount)
        Assert.Equal(0, generated.Discharge.PendingOpenCount)
        Assert.Equal(0, generated.Discharge.RefutedCount)
        Assert.Equal(4, generated.SeamChecks.Checks.Length)
        Assert.Equal(3, generated.RefusalControls.Controls.Length)
        Assert.All(generated.RefusalControls.Controls, fun control ->
            Assert.True control.Passed
            Assert.Equal(control.ExpectedCode, control.ObservedCode))
        SubstantiationSeams.readLedgerReceipt manifest generated.LedgerPath |> accepted |> ignore
        for retained in generated.Ledger.RetainedBinaries do
            Assert.Equal(retained.BinarySha256, ClangToolchain.sha256 retained.BinaryPath)
            census false (Path.Combine(Path.GetDirectoryName retained.BinaryPath, "targets", "intermediates")) |> ignore
        let callbackBinary = generated.Ledger.RetainedBinaries |> Array.find (fun item -> item.CheckId = "callback")
        let callbackWork = Path.GetDirectoryName callbackBinary.BinaryPath
        inspectBinary callbackWork callbackBinary.BinaryPath [ "exit"; "_exit"; "atexit" ] |> ignore
        let callbackElf = SubstantiationSeams.inspectElfExecutable callbackBinary.BinaryPath |> accepted
        Assert.True(callbackElf.DefinedSymtab.Contains "atexit" || callbackElf.UndefinedDynsym.Contains "atexit",
                    "atexit must resolve either as a glibc nonshared wrapper or as a dynamic import")
        let callbackOutcome = ProcessLaunch.startInfo callbackBinary.BinaryPath [] |> execute callbackWork "callback" 10000 (Some [||])
        Assert.Equal(7, callbackOutcome.ExitCode)
        Assert.Equal("callback:explicit-exit\n", callbackOutcome.Stdout.Replace("\r\n", "\n"))
        let dossier =
            SubstantiationDossier.generate
                { ManifestPath = Path.Combine(work, "manifest", fx.Name + ".substantiation.json")
                  OutputDirectory = Some(Path.Combine(work, "dossier"))
                  RocqProjectPath = None; RocqFactsPath = None; RocqConfigPath = None
                  SeamChecksReceiptPath = Some generated.SeamChecksPath
                  RefusalControlsReceiptPath = Some generated.RefusalControlsPath }
            |> accepted
        Assert.StartsWith("SUBSTANTIATED --", dossier.Dossier.OverallStatus)
        Assert.Equal(0, dossier.Dossier.Counts.Open)
        Assert.Equal(0, dossier.Dossier.Counts.PendingSeamCategories)
        Assert.Equal(manifest.Proof.DischargedUnsatCount, dossier.Dossier.Counts.Guarantees)
        Assert.Equal(manifest.Proof.PremisesSatCount + generated.SeamChecks.Checks.Length, dossier.Dossier.Counts.Checks)
        Assert.Equal(manifest.Proof.CompilerNonVacuityReceiptsCount + generated.RefusalControls.Controls.Length, dossier.Dossier.Counts.Falsified)
        Assert.Equal(manifest.Proof.HarnessObligations, dossier.Dossier.Guarantees.HarnessObligations)
        Assert.NotEmpty dossier.Dossier.Assumptions.Hypotheses
        Assert.Equal<Set<string>>(
            Set.ofList [ "H-ABI-1"; "H-Link-1"; "H-Contract-1"; "H-Callback-1" ],
            dossier.Dossier.Assumptions.Hypotheses |> Array.map _.Id |> Set.ofArray)
        for hypothesis in dossier.Dossier.Assumptions.Hypotheses do
            Assert.NotEmpty hypothesis.TestedBy
        for identity in [ "posix.read.borrow"; "posix.read.range"; "posix.close.range"; "libc.malloc.owned"; "libc.atexit.callback" ] do
            Assert.Contains(identity, source)
        use json = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "dossier", fx.Name + ".dossier.json")))
        Assert.Equal(dossier.Dossier.OverallStatus, json.RootElement.GetProperty("OverallStatus").GetString())
        Assert.Equal(manifest.Proof.TotalObligations, json.RootElement.GetProperty("Counts").GetProperty("Guarantees").GetInt32())
        Assert.Equal(manifest.Proof.HarnessObligations, json.RootElement.GetProperty("Guarantees").GetProperty("HarnessObligations").GetInt32()))
