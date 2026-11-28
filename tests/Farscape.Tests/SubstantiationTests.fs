module Farscape.Tests.SubstantiationTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Fidelity.Data.JSON
open Xunit
open Farscape.Core
open Fidelity.Data.TOML

// #208 owns the manifest and its narrow behavioral controls. All generated inputs,
// compiler artifacts and solver queries are in OS temporary storage, under the scratch root
// of the test process; nothing is written to the checkout (#215).
// The helpers without `private` are shared with SubstantiationDossierTests (#209).
let private repository = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

/// The scratch directory of this module. Its fixtures and retained compiler evidence are read
/// by more than one assertion, so no single test deletes it; it is removed with the scratch
/// root of the test process when the process exits.
let private scratchRoot = TestHelpers.TestScratch.create "substantiation-tests"

let private scratch label =
    let path = Path.Combine(scratchRoot, label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(path) |> ignore
    path

let accepted = function
    | Ok value -> value
    | Error (refusal: Diagnostics.Refusal) -> failwith refusal.Text

let refused code = function
    | Error (refusal: Diagnostics.Refusal) -> Assert.Equal<string list>([ code ], refusal.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code) |> List.distinct)
    | Ok _ -> failwithf "expected %s refusal, got success" code

/// The symbolic links that the evidence path policy refuses, created in `directory`: a link
/// whose target is absent, a link to that link, and two links that name each other. `Absent`
/// is the target of the broken link; no refusal may create it.
type RefusedLinks = { Broken: string; Chained: string; Cycle: string; Absent: string }

let refusedLinks (directory: string) : RefusedLinks =
    let absent = Path.Combine(directory, "absent-target")
    let broken = Path.Combine(directory, "broken-link")
    let chained = Path.Combine(directory, "link-to-broken-link")
    let cycle = Path.Combine(directory, "cycle-a")
    let back = Path.Combine(directory, "cycle-b")
    Directory.CreateSymbolicLink(broken, absent) |> ignore
    Directory.CreateSymbolicLink(chained, broken) |> ignore
    Directory.CreateSymbolicLink(cycle, back) |> ignore
    Directory.CreateSymbolicLink(back, cycle) |> ignore
    { Broken = broken; Chained = chained; Cycle = cycle; Absent = absent }

/// Whether `path` is `directory` or beneath it.
let isWithin (directory: string) (path: string) : bool =
    let parent = directory.TrimEnd(Path.DirectorySeparatorChar)
    path = parent || path.StartsWith(parent + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

let private sha (text: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let quote (text: string) = Json.serialize (JsonValue.String text)

let private jProp (name: string) (v: JsonValue) : JsonValue =
    match Json.prop name v with
    | Some x -> x
    | None -> JsonValue.Null

let private jStr (name: string) (v: JsonValue) : string =
    v |> Json.prop name |> Option.bind Json.asString |> Option.defaultValue ""

let private jInt (name: string) (v: JsonValue) : int =
    v |> Json.prop name |> Option.bind Json.asInt |> Option.defaultValue 0L |> int

let private jSet (name: string) (value: JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Object fields ->
        if fields |> List.exists (fun (k, _) -> k = name) then
            fields |> List.map (fun (k, old) -> if k = name then k, value else k, old) |> JsonValue.Object
        else
            JsonValue.Object (fields @ [ name, value ])
    | other -> other

let private jRemove (name: string) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Object fields ->
        fields |> List.filter (fun (k, _) -> k <> name) |> JsonValue.Object
    | other -> other

let private jUpdate (name: string) (f: JsonValue -> JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Object fields ->
        fields |> List.map (fun (k, old) -> if k = name then k, f old else k, old) |> JsonValue.Object
    | other -> other

let private jAdd (item: JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Array items -> JsonValue.Array (items @ [ item ])
    | other -> other

let private jUpdateAt (idx: int) (f: JsonValue -> JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Array items ->
        items |> List.mapi (fun i x -> if i = idx then f x else x) |> JsonValue.Array
    | other -> other

let run work label (info: ProcessStartInfo) =
    if String.IsNullOrWhiteSpace info.WorkingDirectory then info.WorkingDirectory <- work
    let toolScratch = Path.Combine(work, "tool-scratch")
    Directory.CreateDirectory toolScratch |> ignore
    info.Environment["TMPDIR"] <- toolScratch
    use child = new Process(StartInfo = info)
    Assert.True(child.Start(), "the required tool did not start")
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit(300000)) then
        child.Kill(true)
        failwithf "%s exceeded the test's five-minute process bound" label
    child.WaitForExit()
    let output = stdout.Result + stderr.Result
    File.WriteAllText(Path.Combine(work, label + ".log"), output)
    Assert.True(child.ExitCode = 0, $"{label} failed with exit {child.ExitCode}: {output}")
    output

// Observe the actual compiler launch while forwarding every invocation to the real
// host. Only this CLI child's PATH changes; parallel tests keep their own tool paths.
let private recordComposerArguments work (launch: ProcessStartInfo) =
    let path = Environment.GetEnvironmentVariable "PATH"
    let dotnet =
        path.Split(Path.PathSeparator)
        |> Array.map (fun directory -> Path.Combine(directory, "dotnet"))
        |> Array.find File.Exists
        |> Path.GetFullPath
    let directory = Path.Combine(work, "observed-dotnet")
    Directory.CreateDirectory directory |> ignore
    let shim = Path.Combine(directory, "dotnet")
    let recorded = Path.Combine(work, "composer-arguments.txt")
    let shellQuote (value: string) = "'" + value.Replace("'", "'\\''") + "'"
    File.WriteAllText(shim,
        "#!/bin/sh\nif [ \"$2\" = compile ]; then\n  printf '%s\\n' \"$@\" > " + shellQuote recorded
        + "\nfi\nexec " + shellQuote dotnet + " \"$@\"\n")
    File.SetUnixFileMode(shim, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    launch.FileName <- dotnet
    launch.Environment["PATH"] <- directory + string Path.PathSeparator + path
    recorded

let private request project pilot provenance work : Substantiation.Request =
    { FidprojPath = project
      PilotPath = pilot
      LinkProvenancePath = provenance
      OutputDirectory = Some(Path.Combine(work, "manifest"))
      WorkDirectory = Some(Path.Combine(work, "substantiation"))
      AllowWarnings = false
      Verbose = false }

[<Theory>]
[<InlineData("")>]
[<InlineData("{")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("null")>]
let ``malformed JSON manifests refuse with FS9236`` text =
    Substantiation.parseJson text |> refused "FS9236"

[<Theory>]
[<InlineData("")>]
[<InlineData("[broken")>]
[<InlineData("schema_version = true")>]
let ``malformed TOML manifests refuse with FS9236`` text =
    Substantiation.parseToml text |> refused "FS9236"

[<Fact>]
let ``missing fidproj refuses with FS9232`` () =
    let work = scratch "missing-project"
    request (Path.Combine(work, "missing.fidproj")) None None work
    |> Substantiation.evaluate
    |> refused "FS9232"

[<Fact>]
let ``every listed source must exist even without an open`` () =
    let work = scratch "missing-source"
    let project = Path.Combine(work, "Missing.fidproj")
    File.WriteAllText(Path.Combine(work, "First.clef"), "module Missing.First\nlet value : int = 1\n")
    File.WriteAllText(project, "[package]\nname = \"Missing\"\nversion = \"1\"\n[build]\nsources = [\"First.clef\", \"Absent.clef\"]\n")
    request project None None work |> Substantiation.evaluate |> refused "FS9233"

[<Theory>]
[<InlineData("let identity (value: int) : int = value")>]
[<InlineData("let identity = fun (value: int) -> value")>]
[<InlineData("module private Hidden =\n    let callable (value: int) : int = value\nlet alias = Hidden.callable")>]
[<InlineData("let alias = (fun (value: int) -> value)")>]
[<InlineData("let alias =\n    fun (value: int) -> value")>]
[<InlineData("let make () = fun (value: int) -> value")>]
[<InlineData("let make () : (int -> int) = fun (value: int) -> value")>]
[<InlineData("type Alias = int -> int\nlet alias : Alias = fun (value: int) -> value")>]
[<InlineData("type int = int -> int\nlet escaped : int = fun value -> value")>]
[<InlineData("[<Inline>] let identity (value: int) : int = value")>]
[<InlineData("[<Inline>]\nlet identity (value: int) : int = value")>]
[<InlineData("type Counter =\n    static member Next (value: int) : int = value")>]
[<InlineData("type Boxed = { v: int } with\n    member this.Scale (factor: int) : int = this.v * factor")>]
[<InlineData("type Constructed =\n    new(value: int) = { }")>]
[<InlineData("type Counter =\n    member this.Value : int = 7")>]
[<InlineData("let quoteChar : char = '\"'\nlet hidden (value: int) : int = value\nlet quoteChar2 : char = '\"'")>]
[<InlineData("let slash : char = '\\\\'\nlet hidden (value: int) : int = value")>]
[<InlineData("let escaped : char = '\\n'\nlet hidden (value: int) : int = value")>]
[<InlineData("let text : string = @\"quote \"\" inside\"\nlet hidden (value: int) : int = value")>]
[<InlineData("let text : string = \"\"\"quote \" inside\"\"\"\nlet hidden (value: int) : int = value")>]
[<InlineData("module rec Inner =\n    let hidden (value: int) : int = value")>]
[<InlineData("[<AutoOpen>]\nmodule Inner =\n    let hidden (value: int) : int = value")>]
[<InlineData("[<AutoOpen>] module Inner =\n    let hidden (value: int) : int = value")>]
[<InlineData("module public Inner =\n    let hidden (value: int) : int = value")>]
[<InlineData("module rec internal Inner =\n    let hidden (value: int) : int = value")>]
[<InlineData("type First = { x: int }\nand int = int\nlet seven () : int = 7")>]
[<InlineData("let rec first () : int = 7\nand second (value: int) : int = value")>]
[<InlineData("type Box<'a> = { value: 'a }\nlet hidden (value: int) : int = value")>]
[<InlineData("let seven' : int = 7\nlet hidden (value: int) : int = value")>]
[<InlineData("[<Literal>] let Size = 8")>]
[<InlineData("[<Literal>]\nlet Size = 8")>]
[<InlineData("[<Literal>] let computed : int = seven ()\nlet seven () : int = 7")>]
[<InlineData("[<Literal>] let unitLiteral : unit = ()")>]
[<InlineData("[<Literal>] let inline inlineLiteral : int = 8")>]
[<InlineData("[<Literal>] let rec recLiteral : int = 8")>]
[<InlineData("let rec first () : int = 1\n[<Literal>] and andLiteral : int = 8")>]
[<InlineData("let mutable state : int = 0")>]
[<InlineData("type Pair = { left: int; right: int }\nlet zeroPair : Pair = { left = 0; right = 0 }")>]
[<InlineData("type Pair = { left: int; right: int }\nlet makePair () : Pair = { left = 0; right = 0 }")>]
[<InlineData("open BAREWire.Hardware\nlet Descriptor : StructDescriptor = { Name = \"Pair\"; Layout = { Size = 8; Alignment = 4; Fields = [||] }; MayAlias = false; FlexibleArray = None; Documentation = None }")>]
[<InlineData("open BAREWire.Hardware\nlet inline Descriptor : Expr<StructDescriptor> = <@ { Name = \"Pair\"; Layout = { Size = 8; Alignment = 4; Fields = [||] }; MayAlias = false; FlexibleArray = None; Documentation = None } @>")>]
let ``parameterized authored bodies refuse unavailable universal activation`` declaration =
    let work = scratch "unavailable-activation"
    let source = Path.Combine(work, "Parameterized.clef")
    let project = Path.Combine(work, "Parameterized.fidproj")
    File.WriteAllText(source, "module Round19.Parameterized\n" + declaration + "\n")
    File.WriteAllText(project, "[package]\nname = \"Parameterized\"\nversion = \"1\"\n[build]\nsources = [\"Parameterized.clef\"]\n")
    request project None None work |> Substantiation.evaluate |> refused "FS9246"

[<Theory>]
[<InlineData("exception Unexpected of int")>]
[<InlineData("extern int native_call(int value)")>]
[<InlineData("do ignore 7")>]
[<InlineData("printfn \"unclassified\"")>]
[<InlineData("[<Inline>]")>]
[<InlineData("[<Inline>]\nopen System")>]
[<InlineData("module Inner =\n    [<Inline>]\nlet seven () : int = 7")>]
[<InlineData("and orphan : int = 7")>]
[<InlineData("\"bare string\"")>]
[<InlineData("'x'")>]
let ``unclassified module statements and dangling attributes fail closed`` declaration =
    let work = scratch "unclassified-source"
    let project = Path.Combine(work, "Unclassified.fidproj")
    File.WriteAllText(Path.Combine(work, "Unclassified.clef"), "module Round19.Unclassified\n" + declaration + "\n")
    File.WriteAllText(project, "[package]\nname = \"Unclassified\"\nversion = \"1\"\n[build]\nsources = [\"Unclassified.clef\"]\n")
    request project None None work |> Substantiation.evaluate |> refused "FS9233"

[<Theory>]
[<InlineData("type First = { x: int }\nand Second = { y: int }\nlet seven () : int = 7")>]
[<InlineData("type First = {\n    x: int\n}\nand Second = {\n    y: int\n}\nlet seven () : int = 7")>]
[<InlineData("let quoteChar : char = '\"'\nlet seven : int = 7")>]
[<InlineData("[<Measure>]\ntype distance\ntype Choice = | A | B\ntype Code = | Zero = 0 | One = 1\ntype Alias = int\nlet seven : int = 7")>]
[<InlineData("type Box<'a> = { value: 'a }\nlet seven' : int = 7")>]
[<InlineData("open System\nopen type System.Math\n[<Literal>] let seven : int = 7")>]
[<InlineData("open BAREWire.Hardware\ntype Pair = { left: int; right: int }\nmodule Pair =\n    [<Literal>]\n    let Size : int = 8\n    [<Literal>]\n    let Alignment : int = 4\n    [<Literal>]\n    let Tag : string = \"pair\"\n    let Descriptor : Expr<StructDescriptor> = <@ { Name = \"Pair\"; Layout = { Size = 8; Alignment = 4; Fields = [||] }; MayAlias = false; FlexibleArray = None; Documentation = None } @>")>]
[<InlineData("let text : string = @\"first \"\"quoted\"\"\nlet hidden (value:int) = value\nlast\"\nlet seven : int = 7")>]
[<InlineData("let text : string = \"\"\"first \"quote\"\nlet hidden (value:int) = value\nlast\"\"\"\nlet seven : int = 7")>]
[<InlineData("(* outer (* nested *)\nlet hidden (value:int) = value\n*)\n// let omitted (value:int) = value\nlet seven : int = 7")>]
[<InlineData("type private Hidden =\n    static member Next (value:int) : int = value\nlet seven : int = 7")>]
[<InlineData("type Counter =\n    static member private Next (value:int) : int = value\nlet seven : int = 7")>]
let ``admitted source surfaces reach the subsequent project target check`` declaration =
    let work = scratch "admitted-source"
    let project = Path.Combine(work, "Admitted.fidproj")
    File.WriteAllText(Path.Combine(work, "Admitted.clef"), "module Round19.Admitted\n" + declaration + "\n")
    // The absent target makes this a source-admission control without launching Composer.
    File.WriteAllText(project, "[package]\nname = \"Admitted\"\nversion = \"1\"\n[build]\nsources = [\"Admitted.clef\"]\n")
    request project None None work |> Substantiation.evaluate |> refused "FS9232"

[<Theory>]
[<InlineData("")>]
[<InlineData("javascript")>]
let ``missing or unsupported compilation target refuses before tooling`` target =
    let work = scratch "target-selection"
    let source = Path.Combine(work, "Target.clef")
    let project = Path.Combine(work, "Target.fidproj")
    File.WriteAllText(source, "module Round19.Target\nlet value : int = 1\n")
    let compilation = if target = "" then "" else "[compilation]\ntarget = " + quote target + "\n"
    File.WriteAllText(project, "[package]\nname = \"Target\"\nversion = \"1\"\n" + compilation + "[build]\nsources = [\"Target.clef\"]\n")
    request project None None work |> Substantiation.evaluate |> refused "FS9232"

[<Fact>]
let ``work and output directories through broken or cyclic links are refused before writing there`` () =
    let work = scratch "refused-link-directories"
    let source = Path.Combine(work, "Local.clef")
    let project = Path.Combine(work, "Local.fidproj")
    File.WriteAllText(source, "module Round19.Local\nlet value : int = 1\n")
    File.WriteAllText(project,
        "[package]\nname = \"Local\"\nversion = \"1\"\n[compilation]\ntarget = \"cpu\"\n[build]\nsources = [\"Local.clef\"]\n")
    let selected = request project None None work
    let links = refusedLinks work
    for hidden in
        [ links.Broken; links.Chained; links.Cycle
          Path.Combine(links.Broken, "not-created", "nested"); Path.Combine(links.Cycle, "nested") ] do
        { selected with WorkDirectory = Some hidden }
        |> Substantiation.evaluate |> refused "FS9245"
        { selected with OutputDirectory = Some hidden }
        |> Substantiation.evaluate |> refused "FS9245"
    Assert.False(Directory.Exists links.Absent || File.Exists links.Absent, "a refusal must not create the target of a broken link")

[<Fact>]
let ``default tool scratch is a process-scoped directory in OS temporary storage outside the checkout`` () =
    let expected = Path.Combine(Path.GetTempPath(), $"farscape-{Environment.ProcessId}")
    let root = ScratchDirectory.root ()
    Assert.Equal(expected, root)
    Assert.True(isWithin (Path.GetTempPath()) root, $"{root} is not in OS temporary storage")
    Assert.False(isWithin repository root, $"{root} is inside the checkout {repository}")
    // A tool launch creates the directory and directs the tool's temporary files to it.
    let info = ProcessLaunch.startInfo "tool-that-is-not-started" []
    Assert.True(Directory.Exists root, $"{root} was not created by the tool launch")
    for variable in [ "TMPDIR"; "TMP"; "TEMP" ] do Assert.Equal(root, info.Environment[variable])
    // A selected directory replaces the default inside its scope only.
    let selected = scratch "selected-tool-scratch"
    Assert.Equal(selected, ScratchDirectory.within selected ScratchDirectory.root)
    Assert.Equal(expected, ScratchDirectory.root ())

[<Fact>]
let ``evidence path policy admits OS temporary storage and refuses empty, broken and cyclic paths`` () =
    let work = scratch "evidence-path-policy"
    let policy (path: string) =
        try Ok(ScratchDirectory.canonicalEvidencePath path)
        with :? Diagnostics.RefusalException as refusal -> Error refusal.Refusal
    // OS temporary storage is evidence and output storage (#215).
    Assert.True(isWithin (Path.GetTempPath()) work, $"{work} is not in OS temporary storage")
    let canonicalWork = policy work |> accepted
    Assert.True(Directory.Exists canonicalWork)
    Assert.Equal(Path.Combine(canonicalWork, "not-created", "leaf"), policy (Path.Combine(work, "not-created", "leaf")) |> accepted)
    policy (Path.GetTempPath()) |> accepted |> ignore
    // A link to an existing directory resolves to that directory.
    let real = Path.Combine(work, "real")
    let linked = Path.Combine(work, "linked")
    Directory.CreateDirectory real |> ignore
    Directory.CreateSymbolicLink(linked, real) |> ignore
    Assert.Equal(Path.Combine(canonicalWork, "real", "leaf"), policy (Path.Combine(linked, "leaf")) |> accepted)
    // An empty path, a link without its target and a link cycle are refused.
    for empty in [ ""; " "; "\t" ] do policy empty |> refused "FS9245"
    let links = refusedLinks work
    let selfLink = Path.Combine(work, "self-link")
    Directory.CreateSymbolicLink(selfLink, selfLink) |> ignore
    for prohibited in [ links.Broken; links.Chained; links.Cycle; selfLink ] do
        policy prohibited |> refused "FS9245"
        policy (Path.Combine(prohibited, "leaf")) |> refused "FS9245"
    Assert.False(Directory.Exists links.Absent || File.Exists links.Absent)

let private libraryDiagnosticFile = Path.Combine(scratchRoot, "diagnostics", "Second.clef")

[<Fact>]
let ``genuine compiler info is not a reachability demotion`` () =
    "Second.clef:7: info CCS8014: This expression evaluates to a value which is implicitly ignored.\n"
    |> Substantiation.validateCompilerDiagnostics true Set.empty [| libraryDiagnosticFile |] |> accepted

[<Fact>]
let ``demoted library diagnostic is refused using its absolute span`` () =
    "Second.clef:7: info CCS8003: Type mismatch [unreachable]\n  span: " + libraryDiagnosticFile + "(7,0)-(7,18)\n"
    |> Substantiation.validateCompilerDiagnostics false Set.empty [| libraryDiagnosticFile |] |> refused "FS9235"

[<Fact>]
let ``dependency demotion outside exported library does not claim a library failure`` () =
    let dependency = Path.Combine(scratchRoot, "dependency", "Second.clef")
    "Second.clef:7: info CCS8003: Type mismatch [unreachable]\n  span: " + dependency + "(7,0)-(7,18)\n"
    |> Substantiation.validateCompilerDiagnostics false Set.empty [| libraryDiagnosticFile |] |> accepted

[<Fact>]
let ``compiler error is refused whether emitted in the target or its dependency`` () =
    "\u001b[31mDependency.clef:7: error CCS8003: Type mismatch\u001b[0m\n"
    |> Substantiation.validateCompilerDiagnostics false Set.empty [| libraryDiagnosticFile |] |> refused "FS9235"

[<Fact>]
let ``unreferenced function warning in the library is independently refused`` () =
    "Second.clef:7: warning CCS8500: The function 'hidden' is unreferenced.\n  span: " + libraryDiagnosticFile + "(7,0)-(7,18)\n"
    |> Substantiation.validateCompilerDiagnostics false Set.empty [| libraryDiagnosticFile |] |> refused "FS9235"
    libraryDiagnosticFile + ":7:3: warning CCS8500: The function 'hidden' is unreferenced.\n"
    |> Substantiation.validateCompilerDiagnostics false Set.empty [| libraryDiagnosticFile |] |> refused "FS9235"

[<Fact>]
let ``registered warning remains elevated in a dependency with advisory warnings allowed`` () =
    let dependency = Path.Combine(scratchRoot, "dependency", "Second.clef")
    "Second.clef:7: warning CCS8500: The function 'hidden' is unreferenced.\n  span: " + dependency + "(7,0)-(7,18)\n"
    |> Substantiation.validateCompilerDiagnostics false Set.empty [| libraryDiagnosticFile |] |> refused "FS9235"

[<Theory>]
[<InlineData("Second.clef", "CCS8999")>]
[<InlineData("Dependency.clef", "CCS8999")>]
[<InlineData("Second.clef", "CCS8500")>]
let ``blanket warning policy refuses every warning`` source code =
    $"{source}:7: warning {code}: advisory diagnostic\n"
    |> Substantiation.validateCompilerDiagnostics true Set.empty [| libraryDiagnosticFile |] |> refused "FS9235"

[<Fact>]
let ``relaxed warning policy permits advisories but preserves selective elevation`` () =
    let diagnostic code = $"Second.clef:7: warning {code}: advisory diagnostic\n"
    diagnostic "CCS8999"
    |> Substantiation.validateCompilerDiagnostics false (set [ "CCS8777" ]) [| libraryDiagnosticFile |] |> accepted
    for code in [ "CCS8500"; "CCS8777" ] do
        diagnostic code
        |> Substantiation.validateCompilerDiagnostics false (set [ "CCS8777" ]) [| libraryDiagnosticFile |] |> refused "FS9235"

[<Theory>]
[<InlineData("warnings_as_errors = \"false\"")>]
[<InlineData("warnings_as_errors = 0")>]
[<InlineData("warn_as_error = \"CCS8777\"")>]
[<InlineData("warn_as_error = [7]")>]
[<InlineData("warn_as_error = [\"\"]")>]
[<InlineData("warn_as_error = [\"ccs8777\"]")>]
[<InlineData("warn_as_error = [\"CCS8777 \"]")>]
[<InlineData("warn_as_error = [\"CCS8777\\n\"]")>]
[<InlineData("warn_as_error = [\"CCS-8777\"]")>]
[<InlineData("warn_as_error = [\"CCS\"]")>]
[<InlineData("warn_as_error = [\"8777\"]")>]
let ``malformed warning policy refuses the project with FS9232`` policy =
    let work = scratch "malformed-warning-policy"
    let project = Path.Combine(work, "Warnings.fidproj")
    File.WriteAllText(Path.Combine(work, "Warnings.clef"), "module Round19.Warnings\nlet seven () : int = 7\n")
    File.WriteAllText(project,
        "[package]\nname = \"Warnings\"\nversion = \"1\"\n[compilation]\ntarget = \"cpu\"\n[build]\nsources = [\"Warnings.clef\"]\n[substantiation]\n" + policy + "\n")
    request project None None work |> Substantiation.evaluate |> refused "FS9232"

let private proofLibraryPath = Path.Combine(scratchRoot, "proof-fixture", "Boundary.clef")
let private proofHarnessPath = Path.Combine(scratchRoot, "proof-fixture", "SubstantiationRoot.clef")
let private proofSources = [| proofLibraryPath |]
let private proofSource = proofLibraryPath + ":2:1"

// The receipt fixture follows the exact PSG wire fields retained by Composer. Its
// digest is computed from the query bytes, never from an expected literal digest.
let private receiptWithTool executable digest version query outcomes =
    let tool =
        JsonValue.Object
            [ "Executable", JsonValue.String executable
              "Sha256", JsonValue.String digest
              "Version", JsonValue.String version
              "Arguments", JsonValue.Array [ JsonValue.String "--lang=smt2"; JsonValue.String "--tlimit-per=25000" ] ]
    let outcomeItems =
        outcomes
        |> List.map (fun (anchor, verdict) ->
            JsonValue.Object
                [ "Anchor", JsonValue.String anchor
                  "Source", JsonValue.String proofSource
                  "Verdict", JsonValue.String verdict ])
    JsonValue.Object
        [ "Invocation", JsonValue.String "1833a6cd-4da3-40f5-a744-83e7e6796c87"
          "Stage", JsonValue.String "psg"
          "InputSha256", JsonValue.String (sha query)
          "QuerySha256", JsonValue.String (sha query)
          "Tools", JsonValue.Array [ tool ]
          "Outcomes", JsonValue.Array outcomeItems
          "StandardOutput", JsonValue.String (outcomes |> List.map snd |> String.concat "\n")
          "StandardError", JsonValue.String "" ]
    |> Json.serialize

let private receipt query outcomes =
    receiptWithTool ("/" + "usr/bin/cvc5") (String.replicate 64 "a") "cvc5 test identity" query outcomes

let private obligations ids =
    let items =
        ids
        |> List.map (fun id ->
            JsonValue.Object
                [ "id", JsonValue.String id
                  "source", JsonValue.String proofSource ])
    JsonValue.Object [ "obligations", JsonValue.Array items ]
    |> Json.serialize

let private scalarStrata : SubstantiationBoundary.BoundaryStrata =
    { ForeignCalls = true
      MeasuredRecords = false
      StaticArchive = false
      ErrorConventions = false
      Ownership = false
      ZeroCopy = false
      Callbacks = false
      Nullability = false }

[<Fact>]
let ``scalar boundary requires only link call and refusal controls`` () =
    let hypotheses, seams = SubstantiationBoundary.deriveContract "scalar.pilot.toml" scalarStrata
    Assert.Equal<string[]>([| "scalar-handle-link-and-call"; "falsification-refusal-controls" |], seams)
    Assert.DoesNotContain(hypotheses, fun hypothesis -> hypothesis.Family = "H-Callback" || hypothesis.Family = "H-Contract")
    Assert.All(hypotheses, fun hypothesis ->
        Assert.False(String.IsNullOrWhiteSpace hypothesis.Id)
        Assert.Equal("scalar.pilot.toml", hypothesis.Origin)
        Assert.False(String.IsNullOrWhiteSpace hypothesis.Statement))

[<Fact>]
let ``measured callback error boundary adds exactly its three strata`` () =
    let strata = { scalarStrata with MeasuredRecords = true; Callbacks = true; ErrorConventions = true }
    let hypotheses, seams = SubstantiationBoundary.deriveContract "combined.pilot.toml" strata
    Assert.Equal<string[]>(
        [| "scalar-handle-link-and-call"; "measured-record-abi"; "error-sentinel-conformance"
           "callback-registration-invocation"; "falsification-refusal-controls" |], seams)
    Assert.Equal<string[]>([| "H-ABI"; "H-Callback"; "H-Contract"; "H-Link" |], hypotheses |> Array.map (fun hypothesis -> hypothesis.Family) |> Array.distinct |> Array.sort)
    Assert.Equal(hypotheses.Length, hypotheses |> Array.map (fun hypothesis -> hypothesis.Id) |> Array.distinct |> Array.length)
    let repeatedHypotheses, repeatedSeams = SubstantiationBoundary.deriveContract "combined.pilot.toml" strata
    Assert.Equal<SubstantiationBoundary.Hypothesis[]>(hypotheses, repeatedHypotheses)
    Assert.Equal<string[]>(seams, repeatedSeams)

[<Fact>]
let ``static owned and zero copy boundary requires each category only once`` () =
    let strata = { scalarStrata with StaticArchive = true; Ownership = true; ZeroCopy = true; Nullability = true }
    let _, seams = SubstantiationBoundary.deriveContract "owned.pilot.toml" strata
    Assert.Equal<string[]>(
        [| "scalar-handle-link-and-call"; "static-dynamic-link-elision"; "owned-handle-lifecycle"
           "barewire-zero-copy"; "falsification-refusal-controls" |], seams)

[<Fact>]
let ``shared library provenance binds identifiers and refuses unresolved extern libraries`` () =
    let work = scratch "shared-link"
    let project = Path.Combine(work, "Shared.fidproj")
    let source = Path.Combine(work, "Shared.clef")
    let text = "module Round19.Shared\n[<FidelityExtern(\"shared_probe\", \"shared_call\")>]\nlet shared_call () : unit = NativeDefault.zeroed ()\n"
    File.WriteAllText(source, text)
    File.WriteAllText(project,
        "[package]\nname = \"Shared\"\nversion = \"1\"\n[build]\nsources = [\"Shared.clef\"]\n[platform]\nos = \"linux\"\narch = \"x86_64\"\n[link]\nlibraries = [\"shared_probe\"]\n")
    let linked : SubstantiationBoundary.Request =
        { FidprojPath = project; PackageName = "Shared"; LinkLibraries = [| "shared_probe" |]
          LinkObjects = [||]; PilotPath = None; LinkProvenancePath = None
          Sources = [| source, text |]; WorkDirectory = work }
    let evidence = SubstantiationBoundary.evaluate linked |> accepted
    Assert.Equal("runtime-shared-library", evidence.LinkMode)
    Assert.Equal<string[]>([| "shared_probe" |], evidence.Libraries)
    Assert.Equal<string[]>([| "shared_call" |], evidence.ExternSymbols)
    Assert.Contains(evidence.Files, fun file -> file.Kind = "shared-link-contract" && file.Path = project)
    Assert.Contains(evidence.RestsOn, fun hypothesis -> hypothesis.Family = "H-Link" && hypothesis.Statement.Contains("shared_probe", StringComparison.Ordinal))
    Assert.Equal<string[]>([| "scalar-handle-link-and-call"; "falsification-refusal-controls" |], evidence.RequiredSeamChecks)
    { linked with Sources = [| source, text.Replace("shared_probe", "not_selected") |] }
    |> SubstantiationBoundary.evaluate |> refused "FS9243"
    let provenance = Path.Combine(work, "Shared.link-provenance.toml")
    File.WriteAllText(provenance, "[artifact]\nkind = \"shared-library\"\nlibraries = [\"shared_probe\"]\n")
    let explicitProvenance = { linked with LinkProvenancePath = Some provenance }
    SubstantiationBoundary.evaluate explicitProvenance |> accepted |> ignore
    File.WriteAllText(provenance, "[artifact]\nkind = \"shared-library\"\nlibraries = [\"not_selected\"]\n")
    SubstantiationBoundary.evaluate explicitProvenance |> refused "FS9242"

[<Fact>]
let ``combined pilot measures its record and derives exactly callback and error strata`` () =
    let work = scratch "combined-boundary"
    let header = Path.Combine(work, "seam.h")
    File.WriteAllText(header,
        "typedef struct SeamRecord { int value; } SeamRecord;\ntypedef int (*SeamCallback)(int);\nint seam_call(SeamRecord value, SeamCallback callback);\n")
    let project = Path.Combine(work, "Combined.fidproj")
    let source = Path.Combine(work, "Combined.clef")
    let sourceText =
        "module Round19.Combined\n[<Struct>]\ntype SeamRecord = { value: int }\n[<FidelityExtern(\"seam_probe\", \"seam_call\")>]\nlet seam_call (value: SeamRecord) (callback: FnPtr<int -> int>) : int = NativeDefault.zeroed ()\n"
    File.WriteAllText(source, sourceText)
    File.WriteAllText(project,
        "[package]\nname = \"Combined\"\nversion = \"1\"\n[build]\nsources = [\"Combined.clef\"]\n[platform]\nos = \"linux\"\narch = \"x86_64\"\n[link]\nlibraries = [\"seam_probe\"]\n")
    let pilot = Path.Combine(work, "Combined.pilot.toml")
    [ "[library]"; "name = \"seam_probe\""; "headers = [" + quote header + "]"
      "include_paths = [" + quote work + "]"
      "[output]"; "mode = \"fidelity\""; "directory = \"generated\""
      "[options]"; "c_header_mode = true"; "generate_descriptors = false"
      "value_structs = [\"SeamRecord\"]"; "abi_critical_structs = [\"SeamRecord\"]"
      "[[namespace]]"; "name = \"Round19.Combined\""; "library = \"seam_probe\""
      "description = \"Measured callback/error boundary\""; "functions = [\"seam_call\"]" ]
    |> String.concat "\n" |> fun text -> File.WriteAllText(pilot, text)
    let configured = PilotSerializer.loadFromFileDiagnosed pilot |> accepted
    let configured =
        { configured with
            ErrorConventions = Some { Default = PilotTypes.ReturnCode; Overrides = Map.empty }
            Callbacks = Some { Registrations = [ { Function = "seam_call"; CallbackParam = "callback"; DataParam = None } ]; ListenerStructs = [] } }
    match PilotSerializer.saveToFile pilot configured with
    | Ok () -> ()
    | Error message -> failwith message
    let evidence =
        SubstantiationBoundary.evaluate
            { FidprojPath = project; PackageName = "Combined"; LinkLibraries = [| "seam_probe" |]
              LinkObjects = [||]; PilotPath = Some pilot; LinkProvenancePath = None
              Sources = [| source, sourceText |]; WorkDirectory = work }
        |> accepted
    Assert.Equal<string[]>(
        [| "scalar-handle-link-and-call"; "measured-record-abi"; "error-sentinel-conformance"
           "callback-registration-invocation"; "falsification-refusal-controls" |], evidence.RequiredSeamChecks)
    Assert.Equal<string[]>([| "H-ABI"; "H-Callback"; "H-Contract"; "H-Link" |], evidence.RestsOn |> Array.map (fun h -> h.Family) |> Array.sort)
    let layout = Assert.Single(evidence.Layouts)
    Assert.Equal("SeamRecord", layout.Name)
    Assert.Equal(32, layout.SizeBits)
    Assert.Equal(32, layout.AlignmentBits)
    Assert.Equal<int[]>([| 0 |], layout.FieldOffsetsBits)
    for kind, path in [ "header", header; "pilot", pilot ] do
        Assert.Contains(evidence.Files, fun file -> file.Kind = kind && file.Path = path && file.Sha256 = ClangToolchain.sha256 path)
    Assert.Contains(evidence.RestsOn, fun h -> h.Family = "H-Contract" && h.Statement.Contains("ReturnCode", StringComparison.Ordinal))
    Assert.Contains(evidence.RestsOn, fun h -> h.Family = "H-Callback" && h.Statement.Contains("seam_call", StringComparison.Ordinal))

[<Fact>]
let ``foreign struct boundary requires measured pilot provenance`` () =
    let work = scratch "foreign-struct-without-pilot"
    let project = Path.Combine(work, "Unmeasured.fidproj")
    let source = Path.Combine(work, "Unmeasured.clef")
    let sourceText =
        "module Round19.Unmeasured\n[<Struct>]\ntype Pair = { value: int }\n[<FidelityExtern(\"pair\", \"get_value\")>]\nlet get_value (pair: Pair) : int = NativeDefault.zeroed ()\n"
    File.WriteAllText(source, sourceText)
    File.WriteAllText(project, "[package]\nname = \"Unmeasured\"\nversion = \"1\"\n[link]\nlibraries = [\"pair\"]\n")
    SubstantiationBoundary.evaluate
        { FidprojPath = project; PackageName = "Unmeasured"; LinkLibraries = [| "pair" |]
          LinkObjects = [||]; PilotPath = None; LinkProvenancePath = None
          Sources = [| source, sourceText |]; WorkDirectory = work }
    |> refused "FS9244"

// Keep refusal controls ahead of Composer: these fixtures intentionally require
// only source admission, explicit linkage and (where selected) Clang measurement.
let private lexicalBoundaryFixture label declarations libraries =
    let work = scratch label
    let project = Path.Combine(work, "Lexical.fidproj")
    let source = Path.Combine(work, "Lexical.clef")
    let sourceText = "module Round19.Lexical\n" + declarations + "\n"
    File.WriteAllText(source, sourceText)
    File.WriteAllText(project,
        "[package]\nname = \"Lexical\"\nversion = \"1\"\n[compilation]\ntarget = \"cpu\"\n"
        + "[build]\nsources = [\"Lexical.clef\"]\n[platform]\nos = \"linux\"\narch = \"x86_64\"\n"
        + "[link]\nlibraries = [" + (libraries |> Array.map quote |> String.concat ", ") + "]\n")
    let boundary : SubstantiationBoundary.Request =
        { FidprojPath = project; PackageName = "Lexical"; LinkLibraries = libraries
          LinkObjects = [||]; PilotPath = None; LinkProvenancePath = None
          Sources = [| source, sourceText |]; WorkDirectory = work }
    work, boundary

let private refuseBoth code work (boundary: SubstantiationBoundary.Request) =
    SubstantiationBoundary.evaluate boundary |> refused code
    request boundary.FidprojPath boundary.PilotPath boundary.LinkProvenancePath work
    |> Substantiation.evaluate |> refused code

let private divDeclaration attribute =
    attribute + "\ntype DivT = { quot: int; rem: int }\n"
    + "[<FidelityExtern(\"c\", \"div\")>]\n"
    + "let div (numer: int) (denom: int) : DivT = NativeDefault.zeroed ()"

[<Theory>]
[<InlineData("")>]
[<InlineData("[<Struct>]")>]
[<InlineData("[< Struct >]")>]
[<InlineData("[<NoComparison; Struct>]")>]
[<InlineData("[<Struct ; NoComparison>]")>]
[<InlineData("[<StructAttribute>]")>]
[<InlineData("[<Struct()>]")>]
[<InlineData("[<StructAttribute()>]")>]
[<InlineData("[<``Struct``>]")>]
[<InlineData("[<``StructAttribute``()>]")>]
[<InlineData("[<System.Runtime.CompilerServices.Struct>]")>]
[<InlineData("[<\n Struct \n>]")>]
let ``every foreign record spelling requires measured pilot provenance`` attribute =
    let work, boundary = lexicalBoundaryFixture "r19c-record-no-pilot" (divDeclaration attribute) [| "c" |]
    refuseBoth "FS9244" work boundary

[<Theory>]
[<InlineData("type DivT = private { quot: int; rem: int }", "let div (numer: int) (denom: int) : DivT")>]
[<InlineData("type DivT = internal { quot: int; rem: int }", "let div (value: DivT) : int")>]
[<InlineData("type DivT =\n    { quot: int\n      rem: int }", "let inline internal div (value: Round19.Lexical.DivT) : int")>]
[<InlineData("type Helper = { value: int }\nand DivT = { quot: int; rem: int }", "let rec div (numer: int) (denom: int) : Round19.Lexical.DivT")>]
[<InlineData("type DivT = { quot: int; rem: int }", "let div\n    (numer: int)\n    (denom: int)\n    : DivT\n   ")>]
let ``record detection covers accessibility multiline and recursive definitions`` record signature =
    let declarations = record + "\n[<FidelityExtern(\"c\", \"div\")>]\n" + signature + " = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "r19c-record-signature" declarations [| "c" |]
    refuseBoth "FS9244" work boundary

[<Fact>]
let ``struct type requires measurement even outside an extern signature`` () =
    let declarations =
        "[<NoComparison; StructAttribute()>]\ntype DivT = { quot: int; rem: int }\n"
        + "[<FidelityExtern(\"c\", \"abs\")>]\nlet abs (value: int) : int = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "r19c-struct-outside-signature" declarations [| "c" |]
    refuseBoth "FS9244" work boundary

[<Fact>]
let ``helper records outside foreign signatures need no measurement`` () =
    let declarations =
        "type First = { x: int }\nand Second = { y: int }\n"
        + "[<FidelityExtern(\"c\", \"abs\")>]\nlet abs (value: int) : int = NativeDefault.zeroed ()"
    let _, boundary = lexicalBoundaryFixture "r19c-helper-records" declarations [| "c" |]
    let evidence = SubstantiationBoundary.evaluate boundary |> accepted
    Assert.False(evidence.Strata.MeasuredRecords)
    Assert.Equal("not-measured", evidence.TargetDataModel)

let private recordPilot work (headerText: string) recordNames =
    let header = Path.Combine(work, "records.h")
    let pilot = Path.Combine(work, "Records.pilot.toml")
    File.WriteAllText(header, headerText)
    [ "[library]"; "name = \"c\""; "headers = [" + quote header + "]"
      "include_paths = [" + quote work + "]"
      "[output]"; "mode = \"fidelity\""; "directory = \"generated\""
      "[options]"; "c_header_mode = true"; "generate_descriptors = false"
      "value_structs = [" + (recordNames |> List.map quote |> String.concat ", ") + "]"
      "[[namespace]]"; "name = \"Round19.Lexical\""; "library = \"c\""
      "description = \"Lexical record layout control\""; "functions = [\"div\"]" ]
    |> String.concat "\n" |> fun text -> File.WriteAllText(pilot, text)
    pilot

[<Theory>]
[<InlineData("")>]
[<InlineData("[< Struct >]")>]
let ``pilot must measure each foreign record actually used`` attribute =
    let work, boundary = lexicalBoundaryFixture "r19c-record-missing-layout" (divDeclaration attribute) [| "c" |]
    let pilot = recordPilot work "typedef struct OtherRecord { int value; } OtherRecord;\nint abs(int value);\n" [ "OtherRecord" ]
    let selected = { boundary with PilotPath = Some pilot }
    let result = SubstantiationBoundary.evaluate selected
    result |> refused "FS9244"
    match result with
    | Error failure -> Assert.Contains("Foreign record 'DivT' has no per-unit Clang layout measurement in pilot headers", failure.Text)
    | Ok _ -> failwith "unmeasured DivT crossed the boundary"
    request boundary.FidprojPath (Some pilot) None work |> Substantiation.evaluate |> refused "FS9244"

[<Fact>]
let ``one measured record cannot cover another required struct`` () =
    let declarations =
        "[<Struct>]\ntype Second = { value: int }\n" + divDeclaration "[<Struct>]"
    let work, boundary = lexicalBoundaryFixture "r19c-partial-record-layouts" declarations [| "c" |]
    let pilot = recordPilot work "typedef struct DivT { int quot; int rem; } DivT;\nDivT div(int numer, int denom);\n" [ "DivT" ]
    refuseBoth "FS9244" work { boundary with PilotPath = Some pilot }

[<Fact>]
let ``records in another source still require foreign layout provenance`` () =
    let declarations =
        "[<FidelityExtern(\"c\", \"div\")>]\n"
        + "let div (numer: int) (denom: int) : Round19.Types.DivT = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "r19c-record-across-sources" declarations [| "c" |]
    let types = Path.Combine(work, "Types.clef")
    let typeText = "module Round19.Types\ntype DivT = { quot: int; rem: int }\n"
    File.WriteAllText(types, typeText)
    let projectText = File.ReadAllText boundary.FidprojPath
    File.WriteAllText(boundary.FidprojPath, projectText.Replace("sources = [\"Lexical.clef\"]", "sources = [\"Types.clef\", \"Lexical.clef\"]"))
    refuseBoth "FS9244" work { boundary with Sources = Array.append [| types, typeText |] boundary.Sources }

[<Theory>]
[<InlineData("")>]
[<InlineData("[<StructAttribute()>]")>]
let ``measured pilot admits record layout provenance`` attribute =
    let work, boundary = lexicalBoundaryFixture "r19c-record-measured" (divDeclaration attribute) [| "c" |]
    let pilot = recordPilot work "typedef struct DivT { int quot; int rem; } DivT;\nDivT div(int numer, int denom);\n" [ "DivT" ]
    let evidence = SubstantiationBoundary.evaluate { boundary with PilotPath = Some pilot } |> accepted
    Assert.True(evidence.Strata.MeasuredRecords)
    Assert.Contains(evidence.Layouts, fun layout -> layout.Kind = "record" && layout.Name = "DivT")
    Assert.Contains("measured-record-abi", evidence.RequiredSeamChecks)

[<Theory>]
[<InlineData("[<FidelityExtern(\"c\", \"div\")>]")>]
[<InlineData("[< FidelityExtern ( \"c\" , \"div\" ) >]")>]
[<InlineData("[<Inline; FidelityExtern(\"c\", \"div\")>]")>]
[<InlineData("[<FidelityExternAttribute(\"c\", \"div\")>]")>]
[<InlineData("[<``FidelityExtern``(\"c\", \"div\")>]")>]
[<InlineData("[<``FidelityExternAttribute``(\"c\", \"div\")>]")>]
[<InlineData("[<val: FidelityExtern(\"c\", \"div\")>]")>]
[<InlineData("[<let: FidelityExternAttribute(\"c\", \"div\")>]")>]
[<InlineData("[<\n Inline;\n FidelityExtern(\"c\", \"div\")\n>]")>]
let ``extern attribute spellings share source admission and boundary analysis`` attribute =
    let declarations =
        "type DivT = { quot: int; rem: int }\n" + attribute
        + "\nlet div (numer: int) (denom: int) : DivT = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "r19c-extern-spelling" declarations [| "c" |]
    refuseBoth "FS9244" work boundary

[<Theory>]
[<InlineData("lib>")>]
[<InlineData("lib;x")>]
[<InlineData("lib>];x")>]
let ``attribute argument literals cannot terminate or split an attribute`` library =
    let declarations =
        "[<Inline; FidelityExtern(\"" + library + "\", \"symbol\")>]\n"
        + "let symbol (value: int) : int = NativeDefault.zeroed ()"
    let _, boundary = lexicalBoundaryFixture "r19c-attribute-delimiters" declarations [| library |]
    let evidence = SubstantiationBoundary.evaluate boundary |> accepted
    Assert.Equal<string[]>([| library |], evidence.Libraries)
    Assert.Equal<string[]>([| "symbol" |], evidence.ExternSymbols)
    let work, mismatched = lexicalBoundaryFixture "r19c-attribute-delimiters-unlinked" declarations [| "different" |]
    refuseBoth "FS9243" work mismatched

[<Theory>]
[<InlineData("[<FidelityExtern>]")>]
[<InlineData("[<FidelityExtern()>]")>]
[<InlineData("[<FidelityExtern(\"c\")>]")>]
[<InlineData("[<FidelityExtern(\"c\", \"abs\", \"extra\")>]")>]
[<InlineData("[<FidelityExtern(\"\", \"abs\")>]")>]
[<InlineData("[<FidelityExtern(\"c\", \"\")>]")>]
[<InlineData("[<FidelityExtern(7, \"abs\")>]")>]
[<InlineData("[<FidelityExtern(library, symbol)>]")>]
[<InlineData("[<FidelityExternAttribute(\"c\", \"abs\",)>]")>]
[<InlineData("[<FidelityExtern(\"c\", \"abs\">]")>]
[<InlineData("[<FidelityExtern(\"c\", \"abs\"))>]")>]
[<InlineData("[<``FidelityExternAttribute``(\"c\", \"abs\">]")>]
[<InlineData("[<Inline; FidelityExternAttribute(\"c\", \"abs\"))>]")>]
let ``malformed extern argument lists fail closed`` attribute =
    let declarations = attribute + "\nlet abs (value: int) : int = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "r19c-malformed-extern" declarations [| "c" |]
    refuseBoth "FS9243" work boundary

[<Fact>]
let ``FidelityExtern token outside a recognized attribute fails closed`` () =
    let work, boundary = lexicalBoundaryFixture "r19c-unrecognized-extern" "let FidelityExtern : int = 7" [||]
    refuseBoth "FS9243" work boundary

[<Fact>]
let ``string comment opener cannot hide an unresolved foreign declaration`` () =
    let declarations =
        "let pattern : string = \"(*\"\n[<FidelityExtern(\"not_linked\", \"bad\")>]\n"
        + "let bad () : int = NativeDefault.zeroed ()\n(* block comment *)"
    let work, boundary = lexicalBoundaryFixture "r19c-string-comment" declarations [| "c" |]
    refuseBoth "FS9243" work boundary

[<Theory>]
[<InlineData("let text : string = \"[<Struct>]\"")>]
[<InlineData("let ``value (* // [<Struct>]`` : int = 7")>]
[<InlineData("let text : string = \"FidelityExtern(\\\"not_linked\\\", \\\"bad\\\")\"")>]
[<InlineData("let text : string = @\"[<Struct>]; FidelityExtern(\"\"not_linked\"\", \"\"bad\"\")\"")>]
[<InlineData("let text : string = \"\"\"[<Struct>]; FidelityExtern(\"not_linked\", \"bad\")\"\"\"")>]
[<InlineData("let text : string = \"FnPtr<int -> int> Nullable CNullable NonNull OwnershipTransfer = CallerOwns StructDescriptor BorrowedView MappedRegion\"")>]
[<InlineData("let text : string = \"(*\"\n(* outer (* [<Struct>] *) FidelityExtern(\"bad\", \"sym\") *)")>]
[<InlineData("let quote : char = '\"'\nlet slash : char = '\\\\'\nlet star : char = '*'\nlet text : string = \"//\"")>]
let ``literal contents do not invent foreign boundary strata`` declaration =
    let work, boundary = lexicalBoundaryFixture "r19c-literal-decoys" declaration [||]
    let evidence = SubstantiationBoundary.evaluate boundary |> accepted
    Assert.Empty(evidence.ExternSymbols)
    Assert.Equal("none", evidence.LinkMode)
    Assert.False(evidence.Strata.ForeignCalls)
    Assert.False(evidence.Strata.MeasuredRecords)
    Assert.False(evidence.Strata.Callbacks)
    Assert.False(evidence.Strata.Ownership)
    Assert.False(evidence.Strata.ZeroCopy)
    Assert.False(evidence.Strata.Nullability)
    let projectText = File.ReadAllText boundary.FidprojPath
    File.WriteAllText(boundary.FidprojPath, projectText.Replace("[compilation]\ntarget = \"cpu\"\n", ""))
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"

[<Fact>]
let ``shared lexical surface preserves positions and binds structured attributes`` () =
    let text =
        "module Round19.Scan\r\n// [<Struct>] ignored\r\n"
        + "let mul () : int = (*) 3 4\r\n"
        + "let text : string = \"(* [<Struct>]\"\r\n"
        + "let verbatim : string = @\"a \"\"quote\"\"\nnext\"\r\n"
        + "let triple : string = \"\"\"FidelityExtern(\"bad\", \"sym\")\nnext\"\"\"\r\n"
        + "let character : char = '\\n'\r\n"
        + "let ``value (* // [<Struct>]`` : int = 7\r\n"
        + "[<Other([value<int>])>]\r\n"
        + "[<Inline; FidelityExtern(\"lib>];x\", \"symbol\")>]\r\n"
        + "let call () : int = NativeDefault.zeroed ()\r\n"
    let lexical = SubstantiationBoundary.scanSource "lexical-surface.clef" text
    Assert.Equal(text.Length, lexical.CodeSurface.Length)
    Assert.Equal(text.Length, lexical.AnalysisSurface.Length)
    for index in 0 .. text.Length - 1 do
        if text[index] = '\r' || text[index] = '\n' then
            Assert.Equal(text[index], lexical.CodeSurface[index])
            Assert.Equal(text[index], lexical.AnalysisSurface[index])
    let operator = text.IndexOf("(*)", StringComparison.Ordinal)
    Assert.Equal("(*)", lexical.CodeSurface.Substring(operator, 3))
    let comment = "// [<Struct>] ignored"
    Assert.Equal(String.replicate comment.Length " ", lexical.CodeSurface.Substring(text.IndexOf(comment, StringComparison.Ordinal), comment.Length))
    let literal = "\"(* [<Struct>]\""
    Assert.Equal("?" + String.replicate (literal.Length - 1) " ", lexical.CodeSurface.Substring(text.IndexOf(literal, StringComparison.Ordinal), literal.Length))
    let identifier = "``value (* // [<Struct>]``"
    Assert.Equal(identifier, lexical.CodeSurface.Substring(text.IndexOf(identifier, StringComparison.Ordinal), identifier.Length))
    Assert.DoesNotContain("[<Struct>]", lexical.AnalysisSurface)
    Assert.Equal<string[]>([| "Other"; "Inline"; "FidelityExtern" |], lexical.Attributes |> Array.map _.Head)
    Assert.All(lexical.Attributes, fun attribute -> Assert.Equal(text.IndexOf("let call", StringComparison.Ordinal), attribute.DeclarationStart))
    Assert.Equal<(string * string)[]>([| "lib>];x", "symbol" |], SubstantiationBoundary.parseFidelityExtern "lexical-surface.clef" lexical)

[<Theory>]
[<InlineData("(* unterminated")>]
[<InlineData("(* outer (* nested *)")>]
[<InlineData("let text : string = \"unterminated")>]
[<InlineData("let text : string = @\"unterminated")>]
[<InlineData("let text : string = \"\"\"unterminated")>]
[<InlineData("let ``unterminated : int = 7")>]
[<InlineData("let character : char = '\\n")>]
let ``unterminated lexical constructs refuse consistently`` declaration =
    let work, boundary = lexicalBoundaryFixture "r19c-unterminated" declaration [||]
    refuseBoth "FS9233" work boundary

[<Fact>]
let ``multiplication operator section is admitted as code`` () =
    let work, boundary = lexicalBoundaryFixture "r19c-multiplication" "(**)\nlet mul () : int = (*) 3 4" [||]
    let evidence = SubstantiationBoundary.evaluate boundary |> accepted
    Assert.Empty(evidence.ExternSymbols)
    let projectText = File.ReadAllText boundary.FidprojPath
    File.WriteAllText(boundary.FidprojPath, projectText.Replace("[compilation]\ntarget = \"cpu\"\n", ""))
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"

[<Fact>]
let ``links whose targets lie below a broken link or a cycle are refused as work and output directories`` () =
    let work, boundary = lexicalBoundaryFixture "r19c-symlink-policy" "let value : int = 1" [||]
    let links = refusedLinks work
    // A link whose target lies below a broken link, and a link into a cycle.
    let nested = Path.Combine(work, "Nested")
    let intoCycle = Path.Combine(work, "IntoCycle")
    Directory.CreateSymbolicLink(nested, Path.Combine(links.Broken, "not-created")) |> ignore
    Directory.CreateSymbolicLink(intoCycle, Path.Combine(links.Cycle, "not-created")) |> ignore
    let selected = request boundary.FidprojPath None None work
    for prohibited in [ links.Chained; nested; Path.Combine(nested, "leaf"); links.Cycle; intoCycle ] do
        { selected with WorkDirectory = Some prohibited } |> Substantiation.evaluate |> refused "FS9245"
        { selected with OutputDirectory = Some prohibited } |> Substantiation.evaluate |> refused "FS9245"
    Assert.False(Directory.Exists links.Absent || File.Exists links.Absent, "a refusal must not create the target of a broken link")

[<Fact>]
let ``fidproj through a broken or cyclic link is refused despite explicit safe output`` () =
    let work = scratch "r19c-linked-project"
    let links = refusedLinks work
    // Refusal must precede reading or writing the refused location.
    for directory in [ links.Broken; links.Chained; links.Cycle ] do
        let project = Path.Combine(directory, "round19c-never-created-" + Guid.NewGuid().ToString("N") + ".fidproj")
        request project None None work |> Substantiation.evaluate |> refused "FS9245"
    Assert.False(Directory.Exists links.Absent || File.Exists links.Absent, "a refusal must not create the target of a broken link")

[<Theory>]
[<InlineData("(* (*) *)")>]
[<InlineData("(* \"(*\" *)")>]
[<InlineData("(* \"*)\" *)")>]
[<InlineData("(* @\"\"\"(*\" *)")>]
[<InlineData("(* @$\"\"\"(*\" *)")>]
[<InlineData("(* @\"(*\" *)")>]
[<InlineData("(* \"\"\"*)\"\"\" *)")>]
[<InlineData("(* @$\"(*\" *)")>]
[<InlineData("(* $@\"*)\" *)")>]
let ``comment strings and operator sections close at the real delimiter`` comment =
    let work, boundary = lexicalBoundaryFixture "r19d-comment-control" (comment + "\nlet seven () : int = 7") [||]
    SubstantiationBoundary.evaluate boundary |> accepted |> ignore
    let withoutTarget = File.ReadAllText(boundary.FidprojPath).Replace("[compilation]\ntarget = \"cpu\"\n", "")
    File.WriteAllText(boundary.FidprojPath, withoutTarget)
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"

[<Theory>]
[<InlineData("(* (*) *)")>]
[<InlineData("(* \"(*\" *)")>]
[<InlineData("(* \"*)\" *)")>]
[<InlineData("(* @\"\"\"(*\" *)")>]
[<InlineData("(* @$\"\"\"(*\" *)")>]
let ``comments cannot hide foreign symbols or record layouts`` comment =
    let foreign = "[<FidelityExtern(\"not_linked\", \"bad\")>]\nlet bad () : int = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "r19d-comment-extern" (comment + "\n" + foreign) [| "c" |]
    refuseBoth "FS9243" work boundary
    let work, boundary = lexicalBoundaryFixture "r19d-comment-empty-link" (comment + "\n" + foreign) [||]
    refuseBoth "FS9242" work boundary
    let work, boundary = lexicalBoundaryFixture "r19d-comment-record" (comment + "\n" + divDeclaration "") [| "c" |]
    refuseBoth "FS9244" work boundary

[<Theory>]
[<InlineData("(* '\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* '\"' *)\nlet seven () : int = 7\nlet text : string = \"x\"")>]
[<InlineData("(* '\\\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* a'\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* don't '\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* ''' '\"' *)\nlet seven () : int = 7")>]
let ``quote character literals close block comments without hiding code`` declarations =
    let work, boundary = lexicalBoundaryFixture "comment-character-control" declarations [||]
    let evidence = SubstantiationBoundary.evaluate boundary |> accepted
    Assert.Empty(evidence.ExternSymbols)
    Assert.False(evidence.Strata.ForeignCalls)
    File.WriteAllText(boundary.FidprojPath, File.ReadAllText(boundary.FidprojPath).Replace("[compilation]\ntarget = \"cpu\"\n", ""))
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"

[<Theory>]
[<InlineData("(* 'a'\"' *)")>]
[<InlineData("(* '''\"' *)")>]
let ``completed character before a quote still opens a comment string`` comment =
    let work, boundary = lexicalBoundaryFixture "comment-character-then-string" (comment + "\nlet seven () : int = 7") [||]
    refuseBoth "FS9233" work boundary

[<Theory>]
[<InlineData("\\\\")>]
[<InlineData("\\\"")>]
[<InlineData("\\'")>]
[<InlineData("\\n")>]
[<InlineData("\\r")>]
[<InlineData("\\t")>]
[<InlineData("\\b")>]
[<InlineData("\\a")>]
[<InlineData("\\f")>]
[<InlineData("\\v")>]
let ``simple comment character escapes preserve the following quote character`` escape =
    let declarations = "(* '" + escape + "' '\"' *)\nlet seven () : int = 7"
    let work, boundary = lexicalBoundaryFixture "simple-comment-escape" declarations [||]
    SubstantiationBoundary.evaluate boundary |> accepted |> ignore
    File.WriteAllText(boundary.FidprojPath, File.ReadAllText(boundary.FidprojPath).Replace("[compilation]\ntarget = \"cpu\"\n", ""))
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"

[<Theory>]
[<InlineData("\\034")>]
[<InlineData("\\x22")>]
[<InlineData("\\u0022")>]
[<InlineData("\\U00000022")>]
let ``numeric escapes follow Composer comment token boundaries`` escape =
    let adjacent = "(* '" + escape + "'\"' *)\nlet seven () : int = 7"
    let work, boundary = lexicalBoundaryFixture "numeric-comment-adjacent-quote" adjacent [||]
    SubstantiationBoundary.evaluate boundary |> accepted |> ignore
    File.WriteAllText(boundary.FidprojPath, File.ReadAllText(boundary.FidprojPath).Replace("[compilation]\ntarget = \"cpu\"\n", ""))
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"
    let spaced = "(* '" + escape + "' '\"' *)\nlet seven () : int = 7"
    let work, boundary = lexicalBoundaryFixture "numeric-comment-spaced-quote" spaced [||]
    refuseBoth "FS9233" work boundary

[<Fact>]
let ``XML documentation offsets exclude string block and ordinary comment decoys`` () =
    let text =
        "module Documentation.Surface\nlet prose : string = \"/// string decoy\"\n"
        + "(* /// block decoy *)\n//// ordinary comment decoy\n"
        + "/// First documentation line.\n/// Second documentation line.\n[<Inline>]\nlet seven () : int = 7\n"
        + "module Nested =\n    [<Inline>]\n    /// Documentation after an attribute.\n    let inner () : int = 7\n"
    let lexical = SubstantiationBoundary.scanSource "documentation-surface.clef" text
    let position (token: string) = text.IndexOf(token, StringComparison.Ordinal)
    Assert.Equal<(int * int)[]>(
        [| position "/// First", position "let seven"
           position "/// Second", position "let seven"
           position "/// Documentation", position "let inner" |], lexical.Documentation)

[<Theory>]
[<InlineData("(* don't \"(*\" *)")>]
[<InlineData("(* it's \"*)\" *)")>]
[<InlineData("(* '\"' *)")>]
[<InlineData("(* '\\\"' *)")>]
let ``apostrophes and quoted characters cannot hide following externs`` comment =
    let declarations = comment + "\n[<FidelityExtern(\"not_linked\", \"bad\")>]\n// \" *)\nlet private bad () : int = NativeDefault.zeroed ()"
    let work, boundary = lexicalBoundaryFixture "comment-character-extern" declarations [| "c" |]
    let path, text = boundary.Sources[0]
    let lexical = SubstantiationBoundary.scanSource path text
    Assert.Equal<(string * string)[]>([| "not_linked", "bad" |], SubstantiationBoundary.parseFidelityExtern path lexical)
    refuseBoth "FS9243" work boundary
    let work, boundary = lexicalBoundaryFixture "comment-character-empty-link" declarations [||]
    refuseBoth "FS9242" work boundary

[<Theory>]
[<InlineData("let seven () : int = 7\n(* '\"' *)\n[<FidelityExtern(\"not_linked\", \"bad\")>]\n// \" *)\nlet private bad () : int = NativeDefault.zeroed ()", false, "FS9242")>]
[<InlineData("let seven () : int = 7\nmodule private Hidden =\n    (* '\"' *)\n    [<FidelityExtern(\"not_linked\", \"bad\")>]\n    // \" *)\n    let bad () : int = NativeDefault.zeroed ()", false, "FS9242")>]
[<InlineData("type DivT = { quot: int; rem: int }\nlet seven () : int = 7\n(* '\"' *)\n[<FidelityExtern(\"c\", \"div\")>]\n// \" *)\nlet private div (numer: int) (denom: int) : DivT = NativeDefault.zeroed ()", true, "FS9244")>]
[<InlineData("(* '\"' *)\n[<FidelityExtern(\"c\", \"getpid\")>]\n// \" *)\nlet getpid () : int = NativeDefault.zeroed ()", false, "FS9242")>]
let ``attribute-only hiding refuses private nested and unmeasured externs`` declarations linked code =
    let libraries = if linked then [| "c" |] else [||]
    let work, boundary = lexicalBoundaryFixture "attribute-only-hiding" declarations libraries
    refuseBoth code work boundary

[<Theory>]
[<InlineData("#if NEVER\n(*\n#endif")>]
[<InlineData("    #if NEVER\n(*\n    #endif")>]
[<InlineData("#nowarn \"25\"")>]
[<InlineData("#elif NEVER")>]
[<InlineData("#else")>]
[<InlineData("#endif")>]
[<InlineData("#light \"off\"")>]
[<InlineData("#line 1 \"elsewhere\"")>]
[<InlineData("#load \"elsewhere.clef\"")>]
[<InlineData("#r \"elsewhere.dll\"")>]
[<InlineData("let text : string = $\"text\"")>]
[<InlineData("let text : string = @$\"\\\"")>]
[<InlineData("let text : string = $@\"\\\"")>]
[<InlineData("let text : string = $\"\"\"text\"\"\"")>]
[<InlineData("let text : string = $$\"\"\"text\"\"\"")>]
[<InlineData("let text : string = $\"{\"(*\"}\"")>]
[<InlineData("(* \"unterminated *)")>]
[<InlineData("(* @\"unterminated *)")>]
[<InlineData("(* \"\"\"unterminated *)")>]
let ``directives interpolations and unterminated comment strings refuse`` declaration =
    let work, boundary = lexicalBoundaryFixture "r19d-lexical-refusal" declaration [||]
    refuseBoth "FS9233" work boundary

[<Theory>]
[<InlineData("// #if NEVER $\"decoy\"\nlet seven () : int = 7")>]
[<InlineData("(* #if NEVER $\"decoy\" *)\nlet seven () : int = 7")>]
[<InlineData("let text : string = \"#if NEVER $\\\"decoy\\\"\"")>]
let ``directive and interpolation text inside comments and strings is harmless`` declaration =
    let work, boundary = lexicalBoundaryFixture "r19d-lexical-decoy" declaration [||]
    SubstantiationBoundary.evaluate boundary |> accepted |> ignore
    File.WriteAllText(boundary.FidprojPath, File.ReadAllText(boundary.FidprojPath).Replace("[compilation]\ntarget = \"cpu\"\n", ""))
    request boundary.FidprojPath None None work |> Substantiation.evaluate |> refused "FS9232"

let private r19dExtern signature = "[<FidelityExtern(\"c\", \"div\")>]\n" + signature + " = NativeDefault.zeroed ()"

[<Theory>]
[<InlineData("type DivT = { quot: int; rem: int }\ntype DivAlias = DivT", "let div () : DivAlias")>]
[<InlineData("type DivT = { quot: int; rem: int }\ntype Alias<'a> = 'a\ntype DivAlias = Alias<DivT>", "let div () : DivAlias")>]
[<InlineData("type Alias = Other\nand Other = Alias", "let div () : Alias")>]
[<InlineData("type Alias = int * int", "let div () : Alias")>]
[<InlineData("type DivT = { quot: int; rem: int }\nmodule Inner =\n    type DivT = int", "let div (value: DivT) : int")>]
[<InlineData("module A =\n    type DivT = int\n    type Alias<'T> = 'T\nmodule B =\n    type DivT = | Pair of int\n    [<FidelityExtern(\"c\", \"div\")>]\n    let hidden (value: A.Alias<DivT>) : int = NativeDefault.zeroed ()", "let div () : int")>]
[<InlineData("", "let div () : struct (int * int)")>]
[<InlineData("", "let div (value: (int * int)) : int")>]
[<InlineData("", "let div () : {| quot: int; rem: int |}")>]
[<InlineData("type DivT = struct val quot: int; val rem: int end", "let div () : DivT")>]
[<InlineData("", "let div () : External.Types.DivT")>]
[<InlineData("type DivT = { quot: int; rem: int }", "let div ([<Marker(Flag = true)>] value: DivT) : int")>]
[<InlineData("type DivT = | Pair of quot: int * rem: int", "let div () : DivT")>]
[<InlineData("type DivT = | One | Two", "let div () : DivT")>]
[<InlineData("type DivT = delegate of int -> int", "let div () : DivT")>]
[<InlineData("type Helper = int\nand [<Struct>] DivT = { quot: int; rem: int }", "let div () : DivT")>]
[<InlineData("type [<Struct>] DivT = { quot: int; rem: int }", "let div () : DivT")>]
[<InlineData("type Sqlite3 = private | Opaque_Sqlite3", "let div (value: Sqlite3) : int")>]
[<InlineData("type Sqlite3", "let div (value: nativeptr<Sqlite3>) : int")>]
[<InlineData("type DivT = { quot: int; rem: int }", "let div (value: FnPtr<nativeptr<DivT> -> int>) : int")>]
[<InlineData("type Choice = | Zero = 0 | One = 1L", "let div () : Choice")>]
[<InlineData("", "let div value : int")>]
[<InlineData("", "let div (value) : int")>]
[<InlineData("", "let div (value: int)")>]
let ``every foreign signature type must be classified and measured`` declarations signature =
    let work, boundary = lexicalBoundaryFixture "r19d-type-refusal" (declarations + "\n" + r19dExtern signature) [| "c" |]
    refuseBoth "FS9244" work boundary

[<Theory>]
[<InlineData("type DivT = { quot: int; rem: int }\ntype DivAlias = DivT", "let div () : DivAlias")>]
[<InlineData("type DivT = { quot: int; rem: int }\ntype Alias<'a> = 'a", "let div () : Alias<DivT>")>]
[<InlineData("type DivT = { quot: int; rem: int }", "let div ([<Marker(Flag = true)>] value: DivT) : int")>]
[<InlineData("type Helper = int\nand [<Struct>] DivT = { quot: int; rem: int }", "let div () : DivT")>]
[<InlineData("type [<Struct>] DivT = { quot: int; rem: int }", "let div () : DivT")>]
[<InlineData("module Sub =\n    type DivT = { quot: int; rem: int }\n    type Alias = DivT", "let div () : Sub.Alias")>]
[<InlineData("", "let div () : External.Types.DivT")>]
let ``aliases attributes nested and external types require the matching pilot record`` declarations signature =
    let work, boundary = lexicalBoundaryFixture "r19d-measured" (declarations + "\n" + r19dExtern signature) [| "c" |]
    let missing = recordPilot work "typedef struct Other { int value; } Other;\nint div(int value);\n" [ "Other" ]
    SubstantiationBoundary.evaluate { boundary with PilotPath = Some missing } |> refused "FS9244"
    let pilot = recordPilot work "typedef struct DivT { int quot; int rem; } DivT;\nDivT div(int numer, int denom);\n" [ "DivT" ]
    let measured = SubstantiationBoundary.evaluate { boundary with PilotPath = Some pilot } |> accepted
    Assert.True(measured.Strata.MeasuredRecords)
    Assert.Contains(measured.Layouts, fun layout -> layout.Kind = "record" && layout.Name = "DivT")

[<Theory>]
[<InlineData("type Sqlite3 = private | Opaque_Sqlite3", "CHandle<Sqlite3>")>]
[<InlineData("type Sqlite3", "CHandle<Sqlite3>")>]
[<InlineData("type Sqlite3 = private | Opaque_Sqlite3", "BorrowedView<Sqlite3>")>]
[<InlineData("type Sqlite3 = private | Opaque_Sqlite3", "MappedRegion<Sqlite3>")>]
[<InlineData("type Sqlite3 = private | Opaque_Sqlite3\ntype Alias = Sqlite3", "CHandle<Alias>")>]
[<InlineData("[<Measure>] type meters", "int<meters>")>]
[<InlineData("", "FnPtr<int -> int>")>]
[<InlineData("type Identity<'T> = 'T", "Identity<Identity<int>>")>]
[<InlineData("", "option<Nullable<nativeptr<int>>>")>]
let ``emitted opaque markers measures and recursive scalar carriers remain admitted`` declaration annotation =
    let _, boundary = lexicalBoundaryFixture "r19d-carriers" (declaration + "\n" + r19dExtern ("let div (value: " + annotation + ") : int")) [| "c" |]
    let evidence = SubstantiationBoundary.evaluate boundary |> accepted
    Assert.False(evidence.Strata.MeasuredRecords)

[<Theory>]
[<InlineData("source")>]
[<InlineData("pilot")>]
[<InlineData("header")>]
[<InlineData("include")>]
[<InlineData("system-include")>]
[<InlineData("artifact")>]
[<InlineData("provenance")>]
[<InlineData("dependency")>]
[<InlineData("archive")>]
let ``all input roles refuse paths through broken and cyclic links`` role =
    let work, original = lexicalBoundaryFixture "r19d-input-path" (r19dExtern "let div () : int") [| "c" |]
    let links = refusedLinks work
    for prefix in [ links.Broken; links.Chained; links.Cycle ] do
        let prohibited = Path.Combine(prefix, "r19d-never-created")
        let pilot = recordPilot work "int div(void);\n" []
        let mutable boundary = original
        match role with
        | "source" ->
            boundary <- { boundary with Sources = [| prohibited, snd original.Sources[0] |] }
            File.WriteAllText(original.FidprojPath, File.ReadAllText(original.FidprojPath).Replace("sources = [\"Lexical.clef\"]", "sources = [" + quote prohibited + "]"))
        | "pilot" -> boundary <- { boundary with PilotPath = Some prohibited }
        | "header" | "include" | "system-include" | "artifact" ->
            let text = File.ReadAllText pilot
            let changed =
                match role with
                | "header" -> text.Replace("headers = [" + quote (Path.Combine(work, "records.h")) + "]", "headers = [" + quote prohibited + "]")
                | "include" -> text.Replace("include_paths = [" + quote work + "]", "include_paths = [" + quote prohibited + "]")
                | "system-include" -> text.Replace("[output]", "system_include_paths = [" + quote prohibited + "]\n[output]")
                | _ -> text.Replace("[output]", "[library.artifact]\nkind = \"static-archive\"\npath = " + quote prohibited + "\nsha256 = " + quote (String.replicate 64 "0") + "\n[output]")
            File.WriteAllText(pilot, changed)
            boundary <- { boundary with PilotPath = Some pilot }
        | "provenance" -> boundary <- { boundary with LinkProvenancePath = Some prohibited }
        | "dependency" -> File.AppendAllText(original.FidprojPath, "\n[dependencies]\nunsafe = { path = " + quote prohibited + " }\n")
        | "archive" ->
            boundary <- { boundary with LinkObjects = [| prohibited |] }
            File.AppendAllText(original.FidprojPath, "archives = { c = " + quote prohibited + " }\n")
        | _ -> failwith "unclassified path fixture"
        refuseBoth "FS9245" work boundary
        // Each path variant starts from an identical project contract.
        File.WriteAllText(original.FidprojPath,
            "[package]\nname = \"Lexical\"\nversion = \"1\"\n[compilation]\ntarget = \"cpu\"\n[build]\nsources = [\"Lexical.clef\"]\n[platform]\nos = \"linux\"\narch = \"x86_64\"\n[link]\nlibraries = [\"c\"]\n")

[<Theory>]
[<InlineData("")>]
[<InlineData(" ")>]
let ``explicit empty evidence directories refuse instead of selecting cwd`` path =
    let work, boundary = lexicalBoundaryFixture "r19d-empty-path" "let seven () : int = 7" [||]
    SubstantiationBoundary.evaluate { boundary with WorkDirectory = path } |> refused "FS9245"
    let selected = request boundary.FidprojPath None None work
    { selected with WorkDirectory = Some path } |> Substantiation.evaluate |> refused "FS9245"
    { selected with OutputDirectory = Some path } |> Substantiation.evaluate |> refused "FS9245"

[<Fact>]
let ``a broken link on either reading of a parent traversal path is refused`` () =
    let work, boundary = lexicalBoundaryFixture "r19d-parent-traversal" "let seven () : int = 7" [||]
    let deep = Path.Combine(work, "real", "deep")
    Directory.CreateDirectory deep |> ignore
    Directory.CreateSymbolicLink(Path.Combine(work, "portal"), deep) |> ignore
    // `portal/..` is `real` on the filesystem and `work` once '..' is normalized lexically.
    // Each link below is broken and lies on exactly one of those two readings.
    let absent = Path.Combine(work, "absent-target")
    Directory.CreateSymbolicLink(Path.Combine(work, "lexical-only"), absent) |> ignore
    Directory.CreateSymbolicLink(Path.Combine(work, "real", "filesystem-only"), absent) |> ignore
    for link in [ "lexical-only"; "filesystem-only" ] do
        let path = Path.Combine(work, "portal", "..", link, "r19d-never-created")
        SubstantiationBoundary.evaluate { boundary with WorkDirectory = path } |> refused "FS9245"
        { request boundary.FidprojPath None None work with WorkDirectory = Some path }
        |> Substantiation.evaluate |> refused "FS9245"
        refuseBoth "FS9245" work { boundary with PilotPath = Some path }
    Assert.False(Directory.Exists absent || File.Exists absent, "a refusal must not create the target of a broken link")

[<Theory>]
[<InlineData("{| quot: int; rem: int |}")>]
[<InlineData("struct (int * int)")>]
[<InlineData("struct {| quot: int; rem: int |}")>]
let ``nominal measurement cannot authorize an inline aggregate alias`` aggregate =
    let work, boundary = lexicalBoundaryFixture "r19d-aggregate-alias" ("type DivT = " + aggregate + "\n" + r19dExtern "let div () : DivT") [| "c" |]
    let pilot = recordPilot work "typedef struct DivT { int quot; int rem; } DivT;\nDivT div(void);\n" [ "DivT" ]
    refuseBoth "FS9244" work { boundary with PilotPath = Some pilot }

[<Fact>]
let ``enum signatures require measured enum width and signedness`` () =
    let _, boundary = lexicalBoundaryFixture "r19d-enum" ("type Choice = | Zero = 0 | One = 1L\n" + r19dExtern "let div () : Choice") [| "c" |]
    let pilot = recordPilot boundary.WorkDirectory "typedef enum Choice { Zero = 0, One = 1 } Choice;\nChoice div(void);\n" []
    let evidence = SubstantiationBoundary.evaluate { boundary with PilotPath = Some pilot } |> accepted
    Assert.Contains(evidence.Enums, fun item -> item.Name = "Choice" && item.WidthBits = 32)
    Assert.False(evidence.Strata.MeasuredRecords)

let requiredFile label path =
    Assert.True(File.Exists path, $"{label} must exist: {path}")
    Path.GetFullPath path

let sibling variable relative =
    match Environment.GetEnvironmentVariable variable with
    | null -> requiredFile variable (Path.Combine(Path.GetDirectoryName repository, relative))
    | configured when String.IsNullOrWhiteSpace configured -> failwithf "%s is explicitly empty; no alternate project is selected" variable
    | configured -> requiredFile variable configured

let platformTables () =
    let project = sibling "FIDELITY_PLATFORM_PROJECT" "Fidelity.Platform/Environments/Linux/x86_64/Fidelity.Platform.CompilerSurface.fidproj"
    let document =
        match Toml.parse(File.ReadAllText project) with
        | Ok value -> value
        | Error reason -> failwithf "%s: %A" project reason
    let table key =
        match Toml.getValue key document with
        | Some(TomlValue.Table value) | Some(TomlValue.InlineTable value) -> value
        | value -> failwithf "%s: expected table %s, got %A" project key value
    match Toml.getValue "build.sources" document with
    | Some(TomlValue.Array []) -> ()
    | value -> failwithf "selected platform must be a source-free compiler selection, got %A" value
    let dependencies =
        table "dependencies" |> Map.toList |> List.map (fun (name, entry) ->
            let path =
                match entry with
                | TomlValue.Table values | TomlValue.InlineTable values ->
                    match Map.tryFind "path" values with
                    | Some(TomlValue.String path) -> requiredFile name (Path.Combine(Path.GetDirectoryName project, path))
                    | value -> failwithf "dependency %s path is %A" name value
                | value -> failwithf "dependency %s is %A" name value
            quote name + " = { path = " + quote path + " }")
    let platform =
        table "platform" |> Map.toList |> List.map (fun (name, entry) ->
            match entry with
            | TomlValue.String value -> quote name + " = " + quote value
            | value -> failwithf "platform key %s is %A" name value)
    dependencies, platform

let private writeProject work sources (archive: string option) =
    let dependencies, platform = platformTables ()
    let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
    let project = Path.Combine(work, "SubstantiationProbe.fidproj")
    [ "[package]"; "name = \"SubstantiationProbe\""; "version = \"1.0.0\""
      "[compilation]"; "target = \"cpu\""
      "[build]"; "output_kind = \"library\""
      "sources = [" + (sources |> List.map quote |> String.concat ", ") + "]"
      "[dependencies]"; yield! dependencies
      "metadata = { path = " + quote metadata + " }"
      "[platform]"; yield! platform
      if archive.IsSome then "loading = \"Landing.Acceptance.Loading.contract\""
      match archive with
      | Some path ->
          "[link]"; "libraries = [\"substantiation_probe\"]"
          "archives = { substantiation_probe = " + quote path + " }"
      | None -> () ]
    |> String.concat "\n"
    |> fun text -> File.WriteAllText(project, text)
    project

type private Probe = { Work: string; Project: string; Pilot: string; Provenance: string; Archive: string; Header: string; Sources: string list }

/// The static-archive probe library and its project. `paired` adds a second foreign function,
/// `substantiation_echo`, with its own FidelityExtern declaration, FunctionDescriptor, result
/// contract and authored caller, so the witness manifest carries two imports of one library (#209).
let private nativeProbeWith (paired: bool) =
    let work = scratch "native"
    let header = Path.Combine(work, "probe.h")
    let source = Path.Combine(work, "probe.c")
    let objectFile = Path.Combine(work, "probe.o")
    let archive = Path.Combine(work, "libsubstantiation_probe.a")
    File.WriteAllText(header, "typedef struct { int unused_field; } UnselectedRecord;\ntypedef void (*UnselectedCallback)(int);\nvoid unselected_callback(UnselectedCallback callback);\nint substantiation_identity(int value);\n"
        + (if paired then "int substantiation_echo(int value);\n" else ""))
    File.WriteAllText(source, "#include \"probe.h\"\nint substantiation_identity(int value) { return value; }\n"
        + (if paired then "int substantiation_echo(int value) { return value; }\n" else ""))
    run work "foreign-object" (ClangToolchain.startInfo [ "-c"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; source; "-o"; objectFile ]) |> ignore
    run work "foreign-archive" (ClangToolchain.arStartInfo [ "rcs"; archive; objectFile ]) |> ignore
    let pilot = Path.Combine(work, "Probe.pilot.toml")
    [ "[library]"; "name = \"substantiation_probe\""; "headers = [" + quote header + "]"
      "include_paths = [" + quote work + "]"
      "[library.artifact]"; "kind = \"static-archive\""; "path = " + quote archive
      "sha256 = " + quote (ClangToolchain.sha256 archive)
      "[output]"; "mode = \"fidelity\""; "directory = \"generated\""
      "[options]"; "c_header_mode = true"; "descriptor_only_dependencies = true"; "link_libraries = true"
      "[[namespace]]"; "name = \"Round19.Probe.Boundary\""; "library = \"substantiation_probe\""
      "description = \"Substantiation scalar link probe\""
      "functions = [\"substantiation_identity\"" + (if paired then ", \"substantiation_echo\"" else "") + "]" ]
    |> String.concat "\n" |> fun text -> File.WriteAllText(pilot, text)
    ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64)
    |> accepted |> ignore
    let provenance =
        match Directory.GetFiles(work, "*.link-provenance.toml", SearchOption.AllDirectories) with
        | [| path |] -> path
        | paths -> failwithf "expected one generated link provenance manifest, got %A" paths
    let loading = Path.Combine(work, "Loading.clef")
    (File.ReadAllText(Path.Combine(repository, "tests", "native-callbacks", "Loading.clef"))).Replace("landingprobe", "substantiation_probe")
    |> fun text -> File.WriteAllText(loading, text)
    let scalar = Path.Combine(work, "Scalar.clef")
    File.WriteAllText(scalar,
        "module Round19.Probe.Scalar\ntype First = { x: int }\nand Second = { y: int }\nlet quoteChar () : char = '\"'\n"
        + "let seven () : int = Round19.Probe.Boundary.substantiation_identity 7\nmodule Nested =\n    let nestedSeven () : int = 7\n"
        + "    /// First nested documentation line.\n    /// Second nested documentation line.\n"
        + "    [<Inline>]\n    let documentedNested () : int = 7\n"
        + "/// Earlier scalar documentation group.\n// Ordinary comment separates documentation groups.\n"
        + "/// Current scalar documentation group.\nlet documentedSeven () : int = 7\n"
        + "(* /// Block documentation decoy. *)\n//// Ordinary documentation decoy.\nlet undocumentedSeven () : int = 7\n"
        + (if paired then "let echoSeven () : int = Round19.Probe.Boundary.substantiation_echo 7\n" else ""))
    // The native implementation returns exactly its input. The compiler consumes this
    // explicit external hypothesis to emit conditional foreign-result obligations.
    let boundary = Path.Combine(work, "generated", "Boundary", "Boundary.clef")
    File.AppendAllText(boundary, "\nlet identityResultContract : Expr<ResultRangeDescriptor> = <@ {\n    Binding = \"Round19.Probe.Boundary.substantiation_identity\"\n    Role = ReturnValue\n    Lower = Argument 0\n    Upper = Argument 0\n    Hypothesis = ({ Identity = \"Round19.identity-returns-input\"; Version = \"1\"; Source = \"fixture:probe.c\" } : ExternalExecutionHypothesis)\n} @>\n")
    if paired then
        File.AppendAllText(boundary, "\nlet echoResultContract : Expr<ResultRangeDescriptor> = <@ {\n    Binding = \"Round19.Probe.Boundary.substantiation_echo\"\n    Role = ReturnValue\n    Lower = Argument 0\n    Upper = Argument 0\n    Hypothesis = ({ Identity = \"Round19.echo-returns-input\"; Version = \"1\"; Source = \"fixture:probe.c\" } : ExternalExecutionHypothesis)\n} @>\n")
    let sources =
        [ loading; Path.Combine(work, "generated", "Types.clef")
          Path.Combine(work, "generated", "Boundary", "Boundary.clef"); scalar ]
    sources |> List.iter (requiredFile "generated source" >> ignore)
    let project = writeProject work sources (Some archive)
    { Work = work; Project = project; Pilot = pilot; Provenance = provenance; Archive = archive; Header = header; Sources = sources }

let private nativeProbe () = nativeProbeWith false

[<Theory>]
[<InlineData("(* (*) *)")>]
[<InlineData("(* \"(*\" *)")>]
[<InlineData("(* \"*)\" *)")>]
let ``native compiler accepts exact closed comment forms and binds PSG0`` comment =
    let work = scratch "r19d-native-comment"
    let source = Path.Combine(work, "Native.clef")
    File.WriteAllText(source, "module Round19d.Native\n" + comment + "\nlet seven () : int = 7\n")
    let project = writeProject work [ source ] None
    let manifest = request project None None work |> Substantiation.evaluate |> accepted
    Assert.Equal(Path.Combine(manifest.ArtifactsDirectory, "01_psg0.json"), manifest.Psg0.Path)
    Assert.Equal(ClangToolchain.sha256 manifest.Psg0.Path, manifest.Psg0.Sha256)
    Substantiation.validateManifest manifest |> accepted

[<Theory>]
[<InlineData("(* '\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* '\\\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* a'\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* don't '\"' *)\nlet seven () : int = 7")>]
[<InlineData("(* ''' '\"' *)\nlet seven () : int = 7")>]
let ``native compiler admits quote characters inside block comments`` declarations =
    let work = scratch "native-comment-character"
    let source = Path.Combine(work, "Native.clef")
    File.WriteAllText(source, "module CommentCharacter.Native\n" + declarations + "\n")
    let project = writeProject work [ source ] None
    let manifest = request project None None work |> Substantiation.evaluate |> accepted
    Assert.Equal<string[]>([| "CommentCharacter.Native.seven" |], manifest.Sources[0].RuntimeDeclarations |> Array.filter (fun name -> name.EndsWith(".seven", StringComparison.Ordinal)))
    Assert.Empty(manifest.Sources[0].ForeignDeclarations)
    Assert.Empty(manifest.Boundary.ExternSymbols)
    Substantiation.validateManifest manifest |> accepted

[<Fact>]
let ``a CLI process removes its default tool scratch on exit and writes nothing to its working directory`` () =
    let work = scratch "cli-default-scratch"
    let header = Path.Combine(work, "fx.h")
    File.WriteAllText(header, "struct fx_point { int x; int y; };\nint fx_sum(struct fx_point point);\n")
    let cliDirectory = Path.Combine(work, "cli-working-directory")
    let output = Path.Combine(work, "generated")
    Directory.CreateDirectory cliDirectory |> ignore
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let launch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "generate"; "--header"; header; "--library"; "fx"; "--output"; output; "--namespace"; "Fx.Native" ]
    launch.WorkingDirectory <- cliDirectory
    run work "generate-cli" launch |> ignore
    requiredFile "generated binding" (Path.Combine(output, "Native.clef")) |> ignore
    // Header parsing in the child starts clang with no selected scratch directory, so the child
    // creates farscape-<pid> in its temporary directory, which `run` sets to <work>/tool-scratch.
    // The child removes that directory when it exits.
    Assert.Empty(Directory.GetDirectories(Path.Combine(work, "tool-scratch"), "farscape-*"))
    Assert.Empty(Directory.GetFileSystemEntries cliDirectory)

[<Fact>]
let ``a library in OS temporary storage substantiates while broken and cyclic links are refused`` () =
    let work = scratch "os-temporary-storage"
    let source = Path.Combine(work, "Native.clef")
    File.WriteAllText(source, "module Round20.Temporary\nlet seven () : int = 7\n")
    let project = writeProject work [ source ] None
    let selected = request project None None work
    // The project, the work directory and the output directory are in OS temporary storage (#215).
    for path in [ project; selected.WorkDirectory.Value; selected.OutputDirectory.Value ] do
        Assert.True(isWithin (Path.GetTempPath()) path, $"{path} is not in OS temporary storage")
        Assert.False(isWithin repository path, $"{path} is inside the checkout {repository}")
    let manifest = Substantiation.evaluate selected |> accepted
    Assert.True(isWithin selected.WorkDirectory.Value manifest.ArtifactsDirectory, $"{manifest.ArtifactsDirectory} is not in the selected work directory")
    requiredFile "JSON substantiation manifest" (Path.Combine(selected.OutputDirectory.Value, "SubstantiationProbe.substantiation.json")) |> ignore
    Substantiation.validateManifest manifest |> accepted
    // The same request and the same manifest through a broken or a cyclic link are refused.
    let links = refusedLinks work
    for prohibited in [ links.Broken; links.Cycle ] do
        { selected with WorkDirectory = Some prohibited } |> Substantiation.evaluate |> refused "FS9245"
        { selected with OutputDirectory = Some prohibited } |> Substantiation.evaluate |> refused "FS9245"
        { manifest with ArtifactsDirectory = prohibited }
        |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9245"
    Assert.False(Directory.Exists links.Absent || File.Exists links.Absent, "a refusal must not create the target of a broken link")

[<Fact>]
let ``an omitted work directory places the evidence under the output directory and creates nothing in the working directory`` () =
    let work = scratch "default-work-directory"
    let source = Path.Combine(work, "Native.clef")
    File.WriteAllText(source, "module Round20.DefaultWork\nlet seven () : int = 7\n")
    let project = writeProject work [ source ] None
    let output = Path.Combine(work, "published")
    // The former default was <working directory>/test-results/scratch/substantiation (#215).
    // A build output directory can hold a test-results directory left by an earlier build, so
    // the comparison is of its complete listing and not only of its existence.
    let legacy = Path.Combine(Directory.GetCurrentDirectory(), "test-results")
    let listing () =
        if Directory.Exists legacy then
            Some(Directory.GetFileSystemEntries(legacy, "*", SearchOption.AllDirectories) |> Array.sort |> List.ofArray)
        else None
    let before = listing ()
    let manifest =
        { request project None None work with OutputDirectory = Some output; WorkDirectory = None }
        |> Substantiation.evaluate |> accepted
    // The evidence root is <output>/<package>.substantiation.evidence, and the retained
    // compiler artifacts are <evidence root>/<run>/targets/intermediates.
    let evidence = Path.Combine(ScratchDirectory.canonicalEvidencePath output, "SubstantiationProbe.substantiation.evidence")
    Assert.True(Directory.Exists evidence, $"{evidence} was not created")
    Assert.True(isWithin evidence manifest.ArtifactsDirectory, $"{manifest.ArtifactsDirectory} is not under {evidence}")
    Assert.Equal(evidence, Path.GetFullPath(Path.Combine(manifest.ArtifactsDirectory, "..", "..", "..")))
    for retained in [ manifest.Psg0.Path; manifest.WitnessUnits.Path; manifest.CompilerLog.Path; manifest.Harness.Path; manifest.HarnessProject.Path ] do
        Assert.True(isWithin evidence retained, $"{retained} is not under {evidence}")
    Assert.True(isWithin (Path.GetTempPath()) manifest.ArtifactsDirectory, $"{manifest.ArtifactsDirectory} is not in OS temporary storage")
    Assert.False(isWithin repository manifest.ArtifactsDirectory, $"{manifest.ArtifactsDirectory} is inside the checkout {repository}")
    requiredFile "JSON substantiation manifest" (Path.Combine(output, "SubstantiationProbe.substantiation.json")) |> ignore
    // Nothing was created in the working directory of the process.
    Assert.True((before = listing ()), $"the default work directory changed {legacy}")
    Assert.False(Directory.Exists(Path.Combine(legacy, "scratch", "substantiation")), "the former default work directory was created in the working directory")
    Substantiation.validateManifest manifest |> accepted

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``descriptor extern after a quoted comment requires and records its link`` linked =
    let work = scratch "descriptor-comment-character"
    let source = Path.Combine(work, "Lib.clef")
    File.WriteAllText(source,
        "module Probe.Lib\nopen BAREWire.Descriptors\n"
        + "(* '\"' *)\n[<FidelityExtern(\"c\", \"getpid\")>]\n// \" *)\n"
        + "let getpid () : int = NativeDefault.zeroed ()\n"
        + "let getpidDescriptor : Expr<FunctionDescriptor> =\n    <@\n"
        + "        { CName = \"getpid\"\n          Parameters = [||]\n"
        + "          ReturnType = Integer (Signed, 32)\n          CallingConvention = CDecl\n"
        + "          OwnershipTransfer = Undeclared }\n    @>\nlet seven () : int = 7\n")
    let project = writeProject work [ source ] None
    if linked then File.AppendAllText(project, "\n[link]\nlibraries = [\"c\"]\n")
    let result = request project None None work |> Substantiation.evaluate
    if linked then
        let manifest = result |> accepted
        Assert.Equal<string[]>([| "Probe.Lib.getpid" |], manifest.Sources[0].ForeignDeclarations)
        Assert.DoesNotContain("Probe.Lib.getpid", manifest.Sources[0].RuntimeDeclarations)
        Assert.Equal<string[]>([| "getpid" |], manifest.Boundary.ExternSymbols)
        Assert.Equal<string[]>([| "c" |], manifest.Boundary.Libraries)
        Assert.True(manifest.Boundary.Strata.ForeignCalls)
        Assert.Contains("scalar-handle-link-and-call", manifest.Boundary.RequiredSeamChecks)
        Substantiation.validateManifest manifest |> accepted
    else
        result |> refused "FS9242"

[<Theory>]
[<InlineData("let seven () : int = 7\n(* '\"' *)\n[<FidelityExtern(\"not_linked\", \"bad\")>]\n// \" *)\nlet private bad () : int = NativeDefault.zeroed ()", false, "FS9242")>]
[<InlineData("let seven () : int = 7\nmodule private Hidden =\n    (* '\"' *)\n    [<FidelityExtern(\"not_linked\", \"bad\")>]\n    // \" *)\n    let bad () : int = NativeDefault.zeroed ()", false, "FS9242")>]
[<InlineData("type DivT = { quot: int; rem: int }\nlet seven () : int = 7\n(* '\"' *)\n[<FidelityExtern(\"c\", \"div\")>]\n// \" *)\nlet private div (numer: int) (denom: int) : DivT = NativeDefault.zeroed ()", true, "FS9244")>]
let ``native projects refuse hidden private and record-returning externs`` declarations linked code =
    let work = scratch "native-attribute-only-hiding"
    let source = Path.Combine(work, "Lib.clef")
    File.WriteAllText(source, "module Probe.Lib\n" + declarations + "\n")
    let project = writeProject work [ source ] None
    if linked then File.AppendAllText(project, "\n[link]\nlibraries = [\"c\"]\n")
    request project None None work |> Substantiation.evaluate |> refused code

[<Theory>]
[<InlineData("type DivT = { quot: int; rem: int }\ntype DivAlias = DivT\n[<FidelityExtern(\"c\", \"div\")>]\nlet div () : DivAlias = NativeDefault.zeroed ()", "FS9244")>]
[<InlineData("[<FidelityExtern(\"c\", \"div\")>]\nlet div () : struct (int * int) = NativeDefault.zeroed ()", "FS9244")>]
[<InlineData("[<FidelityExtern(\"c\", \"div\")>]\nlet div () : {| quot: int; rem: int |} = NativeDefault.zeroed ()", "FS9244")>]
[<InlineData("type DivT = | Pair of quot: int * rem: int\n[<FidelityExtern(\"c\", \"div\")>]\nlet div () : DivT = NativeDefault.zeroed ()", "FS9244")>]
[<InlineData("(* (*) *)\n[<FidelityExtern(\"not_linked\", \"bad\")>]\nlet bad () : int = NativeDefault.zeroed ()", "FS9243")>]
[<InlineData("(* \"(*\" *)\n[<FidelityExtern(\"not_linked\", \"bad\")>]\nlet bad () : int = NativeDefault.zeroed ()", "FS9243")>]
[<InlineData("let text : string = @$\"\\\"", "FS9233")>]
let ``native project refuses bypass families before sealing`` declarations code =
    let work = scratch "r19d-native-refusal"
    let source = Path.Combine(work, "Native.clef")
    File.WriteAllText(source, "module Round19d.Native\n" + declarations + "\n")
    let project = writeProject work [ source ] None
    File.AppendAllText(project, "\n[link]\nlibraries = [\"c\"]\n")
    request project None None work |> Substantiation.evaluate |> refused code

let private queryBlock anchor premise =
    String.concat "\n"
        [ "; obligation " + anchor
          "(set-logic QF_LIA)"
          "(declare-fun " + anchor + " () Bool)"
          "(assert " + premise + ")"
          "(assert (= " + anchor + " (>= 7 0)))"
          "(assert (not " + anchor + "))"
          "(check-sat)"; "(reset)"; "" ]

[<Fact>]
let ``premise extraction removes each negated conclusion and preserves premise assertions`` () =
    let query = queryBlock "first" "(not (= 7 0))" + queryBlock "second" "(>= 7 0)"
    let premises = SubstantiationProof.extractPremises [ "first"; "second" ] query |> accepted
    Assert.DoesNotContain("(assert (not first))", premises)
    Assert.DoesNotContain("(assert (not second))", premises)
    Assert.Contains("(assert (not (= 7 0)))", premises)
    Assert.Contains("(assert (= first (>= 7 0)))", premises)
    Assert.Equal(query.Replace("(assert (not first))", "").Replace("(assert (not second))", ""), premises)

[<Fact>]
let ``premise extraction refuses absent duplicate and wrong negated anchors`` () =
    let query = queryBlock "first" "true"
    for broken in
        [ query.Replace("(assert (not first))", "")
          query.Replace("(assert (not first))", "(assert (not first))\n(assert (not first))")
          query.Replace("(assert (not first))", "(assert (not other))")
          query + "(check-sat)\n" ] do
        SubstantiationProof.extractPremises [ "first" ] broken |> refused "FS9239"

[<Fact>]
let ``source receipt outcomes bind one to one to the complete obligation inventory`` () =
    let query = queryBlock "first" "true" + queryBlock "second" "true"
    let document = obligations [ "first"; "second" ]
    let inspect outcomes = SubstantiationProof.inspectReceipt proofSources proofHarnessPath document query (receipt query outcomes)
    let summary = inspect [ "first", "unsat"; "second", "unsat" ] |> accepted
    Assert.Equal(2, summary.TotalObligations)
    Assert.Equal(2, summary.LibraryObligations)
    Assert.Equal(2, summary.LibraryDischargedUnsatCount)
    Assert.Equal(0, summary.LibraryPremisesSatCount)
    Assert.Equal(0, summary.HarnessObligations)
    Assert.Equal(2, summary.DischargedUnsatCount)
    Assert.Equal(0, summary.PendingOpenCount)
    Assert.Equal(0, summary.RefutedCount)
    Assert.Equal(0, summary.PremisesSatCount)
    inspect [ "first", "unsat" ] |> refused "FS9237"
    inspect [ "first", "unsat"; "first", "unsat" ] |> refused "FS9237"
    inspect [ "first", "unsat"; "other", "unsat" ] |> refused "FS9237"

[<Fact>]
let ``source receipt attributes library harness and dependency obligations separately`` () =
    let ids = [ "library"; "harness"; "dependency" ]
    let query = ids |> List.map (fun id -> queryBlock id "true") |> String.concat ""
    let dependency = Path.Combine(scratchRoot, "dependency", "Imported.clef")
    let origins =
        [| proofSource; proofHarnessPath + ":7:3"
           dependency + ":4:2" |]
    let inventory =
        Json.parseOrFail(obligations ids)
        |> jUpdate "obligations" (fun arr ->
            match arr with
            | JsonValue.Array items ->
                items
                |> List.mapi (fun index row -> jSet "source" (JsonValue.String origins[index]) row)
                |> JsonValue.Array
            | other -> other)
    let discharge =
        Json.parseOrFail(receipt query (ids |> List.map (fun id -> id, "unsat")))
        |> jUpdate "Outcomes" (fun arr ->
            match arr with
            | JsonValue.Array items ->
                items
                |> List.mapi (fun index outcome -> jSet "Source" (JsonValue.String origins[index]) outcome)
                |> JsonValue.Array
            | other -> other)
    let inspect inputs =
        SubstantiationProof.inspectProof inputs proofSources proofHarnessPath
            (Json.serialize inventory) query (Json.serialize discharge)
        |> Result.map fst
    let requireRefusal (message: string) = function
        | Error (reason: Diagnostics.Refusal) -> Assert.Contains(message, reason.Text)
        | Ok _ -> failwith "Expected refusal of an unbound source role."
    let hash (text: string) = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))
    let invocation =
        match Json.prop "Invocation" discharge with
        | Some (JsonValue.String value) -> value
        | _ -> failwith "Expected the proof fixture invocation."
    let direct = Array.toList proofSources @ [proofHarnessPath]
    let inputs: SubstantiationProof.CheckedSourceInputs =
        { Project = Path.ChangeExtension(proofHarnessPath, ".fidproj")
          Sources = dependency :: direct
          Direct = direct
          Dependencies = [dependency]
          Files = []
          Archives = []
          Platform = None
          Origins = []
          Invocation = invocation
          QuerySha256 = hash query
          DischargeSha256 = hash (Json.serialize discharge) }
    inspect None |> requireRefusal "outside the bound library and harness"
    inspect (Some { inputs with Sources = direct; Dependencies = [] })
    |> requireRefusal "names a file outside the checked source inputs"
    inspect (Some { inputs with Direct = List.rev direct })
    |> requireRefusal "Checked direct source order differs"
    let summary = inspect (Some inputs) |> accepted
    Assert.Equal(3, summary.TotalObligations)
    Assert.Equal(1, summary.LibraryObligations)
    Assert.Equal(1, summary.LibraryDischargedUnsatCount)
    Assert.Equal(1, summary.HarnessObligations)
    Assert.Equal(3, summary.DischargedUnsatCount)

[<Theory>]
[<InlineData("sat")>]
[<InlineData("unknown")>]
[<InlineData("timeout")>]
let ``open or refuted obligations are never accepted`` verdict =
    let query = queryBlock "first" "true"
    SubstantiationProof.inspectReceipt proofSources proofHarnessPath (obligations [ "first" ]) query (receipt query [ "first", verdict ])
    |> refused "FS9238"

[<Fact>]
let ``receipt digests and solver identities are validated`` () =
    let query = queryBlock "first" "true"
    let document = obligations [ "first" ]
    let original = receipt query [ "first", "unsat" ]
    let inspect evidence = SubstantiationProof.inspectReceipt proofSources proofHarnessPath document query evidence
    let withArguments values =
        Json.parseOrFail original
        |> jUpdate "Tools" (jUpdateAt 0 (jSet "Arguments" (JsonValue.Array(List.map JsonValue.String values))))
        |> Json.serialize
    for limit in [ "--tlimit-per=25000"; "--tlimit=25000" ] do
        let actual = withArguments [ "--lang=smt2"; limit ] |> inspect |> accepted
        Assert.Equal<string[]>([| "--lang=smt2"; limit |], actual.Solver.Arguments)
    for broken in
        [ original.Replace(sha query, String.replicate 64 "0")
          original.Replace(String.replicate 64 "a", "not-a-digest")
          withArguments []
          withArguments [ "--lang=smt2" ]
          withArguments [ "--lang=smt2"; "--tlimit-per=1" ]
          withArguments [ "--lang=smt2"; "--tlimit=1" ]
          withArguments [ "--tlimit-per=25000"; "--lang=smt2" ]
          withArguments [ "--tlimit=25000"; "--lang=smt2" ]
          withArguments [ "--lang=smt2"; "--tlimit-per=25000"; "--incremental" ]
          withArguments [ "--lang=smt2"; "--tlimit=25000"; "--incremental" ]
          withArguments [ "--lang=smt2"; "--tlimit-per=25000"; "--tlimit=25000" ]
          withArguments [ "--lang=smt2"; "--tlimit=25000"; "--tlimit-per=25000" ] ] do
        broken |> inspect |> refused "FS9237"

let private cvc5OnPath () =
    let path = Environment.GetEnvironmentVariable "PATH"
    if String.IsNullOrWhiteSpace path then failwith "SAT(H) tests require PATH to locate cvc5"
    path.Split(Path.PathSeparator)
    |> Array.map (fun directory -> Path.GetFullPath(Path.Combine(directory, "cvc5")))
    |> Array.tryFind File.Exists
    |> function Some path -> path | None -> failwith "SAT(H) tests require cvc5 on PATH; no test is skipped"

[<Fact>]
let ``missing receipt bound solver is refused without substituting a tool`` () =
    let work = scratch "missing-solver"
    let query = queryBlock "first" "true"
    File.WriteAllText(Path.Combine(work, "06a_obligations.json"), obligations [ "first" ])
    File.WriteAllText(Path.Combine(work, "06b_obligations.smt2"), query)
    let unavailable = Path.Combine(work, "cvc5-absent")
    let unavailableReceipt = receiptWithTool unavailable (String.replicate 64 "a") "cvc5 missing fixture" query [ "first", "unsat" ]
    File.WriteAllText(Path.Combine(work, "06c_psg_discharge.json"), unavailableReceipt)
    SubstantiationProof.evaluate proofSources proofHarnessPath work |> refused "FS9241"
    let legacy = unavailableReceipt.Replace("--tlimit-per=25000", "--tlimit=25000")
    File.WriteAllText(Path.Combine(work, "06c_psg_discharge.json"), legacy)
    SubstantiationProof.evaluate proofSources proofHarnessPath work |> refused "FS9237"

[<Fact>]
let ``solver rejects vacuous H even when H and not C is unsat`` () =
    let work = scratch "vacuous-premise"
    let solver = cvc5OnPath ()
    let version = run work "solver-version" (ProcessLaunch.startInfo solver [ "--version" ])
    let query = queryBlock "first" "false"
    let queryPath = Path.Combine(work, "06b_obligations.smt2")
    File.WriteAllText(queryPath, query)
    let unsat = run work "vacuous-discharge" (ProcessLaunch.startInfo solver [ "--lang=smt2"; "--tlimit-per=25000"; queryPath ])
    Assert.Equal("unsat", unsat.Trim())
    File.WriteAllText(Path.Combine(work, "06a_obligations.json"), obligations [ "first" ])
    let compilerReceipt = receiptWithTool solver (ClangToolchain.sha256 solver) version query [ "first", "unsat" ]
    File.WriteAllText(Path.Combine(work, "06c_psg_discharge.json"), compilerReceipt)
    SubstantiationProof.inspectReceipt proofSources proofHarnessPath (obligations [ "first" ]) query compilerReceipt |> accepted |> ignore
    SubstantiationProof.evaluate proofSources proofHarnessPath work |> refused "FS9240"
    Assert.Contains("(assert false)", File.ReadAllText(Path.Combine(work, "06b_premises_sat.smt2")))
    Assert.Equal("unsat", (File.ReadAllText(Path.Combine(work, "06b_premises_sat.stdout"))).Trim())

[<Theory>]
[<InlineData("unknown")>]
[<InlineData("timeout")>]
let ``indeterminate premise solver responses are refused with FS9240`` verdict =
    let work = scratch "indeterminate-premises"
    let solver = Path.Combine(work, "cvc5-controlled-response")
    File.WriteAllText(solver, "#!/bin/sh\nprintf '%s\\n' '" + verdict + "'\n")
    File.SetUnixFileMode(solver, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let query = queryBlock "first" "true"
    File.WriteAllText(Path.Combine(work, "06a_obligations.json"), obligations [ "first" ])
    File.WriteAllText(Path.Combine(work, "06b_obligations.smt2"), query)
    File.WriteAllText(Path.Combine(work, "06c_psg_discharge.json"),
        receiptWithTool solver (ClangToolchain.sha256 solver) "cvc5 controlled refusal response" query [ "first", "unsat" ])
    SubstantiationProof.evaluate proofSources proofHarnessPath work |> refused "FS9240"
    Assert.Equal(verdict, File.ReadAllText(Path.Combine(work, "06b_premises_sat.stdout")).Trim())

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``coherent unselected file cannot acquire dependency proof ownership`` exists =
    let work = scratch "unselected-proof-source"
    let path = Path.Combine(work, "Unselected.clef")
    if exists then File.WriteAllText(path, "module Unselected\n")
    let query = queryBlock "first" "true"
    let original = obligations [ "first" ]
    let discharge = receipt query [ "first", "unsat" ]
    let inspect = SubstantiationProof.inspectReceipt proofSources proofHarnessPath
    let changed = path + ":2:1"
    let changedObligations = original.Replace(proofSource, changed)
    let changedDischarge = discharge.Replace(proofSource, changed)
    let summary = inspect original query discharge |> accepted
    Assert.Equal(1, summary.LibraryObligations)
    match inspect changedObligations query changedDischarge with
    | Error refusal ->
        Assert.Contains("FS9237: The file origin is outside the bound library and harness", refusal.Text)
    | Ok _ -> failwith "An unselected file acquired proof ownership."
    match inspect changedObligations query discharge with
    | Error refusal ->
        Assert.Contains("does not retain its obligation's source identity", refusal.Text)
    | Ok _ -> failwith "Different obligation and outcome source identities were accepted."

let private compilerStageFixture () =
    let work = scratch "compiler-stage-receipts"
    let solver = cvc5OnPath ()
    let version = run work "solver-version" (ProcessLaunch.startInfo solver [ "--version" ])
    let query = queryBlock "first" "true"
    let discharge = receiptWithTool solver (ClangToolchain.sha256 solver) version query [ "first", "unsat" ]
    File.WriteAllText(Path.Combine(work, "06a_obligations.json"), obligations [ "first" ])
    File.WriteAllText(Path.Combine(work, "06b_obligations.smt2"), query)
    File.WriteAllText(Path.Combine(work, "06c_psg_discharge.json"), discharge)
    let stageQuery = "(set-logic QF_LIA)\n(assert true)\n(check-sat)\n"
    for stage, prefix in [ "psg-premises", "06c_psg_premises_0"; "psg-nonvacuity", "06c_psg_nonvacuity_0" ] do
        let stageReceipt =
            Json.parseOrFail(receiptWithTool solver (ClangToolchain.sha256 solver) version stageQuery [ "first", "sat" ])
            |> jSet "Stage" (JsonValue.String stage)
        File.WriteAllText(Path.Combine(work, prefix + ".json"), Json.serialize stageReceipt)
        File.WriteAllText(Path.Combine(work, prefix + ".smt2"), stageQuery)
        File.WriteAllText(Path.Combine(work, prefix + ".stdout"), "sat")
        File.WriteAllText(Path.Combine(work, prefix + ".stderr"), "")
    work

[<Fact>]
let ``compiler premise and nonvacuity receipts bind every retained sidecar`` () =
    let work = compilerStageFixture ()
    let summary = SubstantiationProof.evaluate proofSources proofHarnessPath work |> accepted
    Assert.Equal(1, summary.CompilerPremiseReceiptsCount)
    Assert.Equal(1, summary.CompilerNonVacuityReceiptsCount)
    Assert.Equal(1, summary.LibraryObligations)
    Assert.Equal(1, summary.LibraryPremisesSatCount)
    Assert.Equal(8, summary.StageReceipts.Length)
    Assert.Equal<string[]>(summary.StageReceipts |> Array.map _.Path |> Array.sort, summary.StageReceipts |> Array.map _.Path)
    SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath summary work |> accepted
    let legacyArgs = [| "--lang=smt2"; "--tlimit=25000" |]
    let changedSummary = { summary with Solver = { summary.Solver with Arguments = legacyArgs } }
    SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath changedSummary work |> refused "FS9237"
    let receiptPath = Path.Combine(work, "06c_psg_discharge.json")
    let originalReceipt = File.ReadAllText receiptPath
    try
        File.WriteAllText(receiptPath, originalReceipt.Replace("--tlimit-per=25000", "--tlimit=25000"))
        SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath summary work |> refused "FS9237"
    finally
        File.WriteAllText(receiptPath, originalReceipt)
    for digest in summary.StageReceipts do
        let bytes = File.ReadAllBytes digest.Path
        try
            File.WriteAllBytes(digest.Path, Array.append bytes [| 10uy |])
            SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath summary work |> refused "FS9237"
            File.Delete digest.Path
            SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath summary work |> refused "FS9237"
        finally
            File.WriteAllBytes(digest.Path, bytes)
    SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath summary work |> accepted
    for path in [ receiptPath
                  Path.Combine(work, "06c_psg_premises_0.json")
                  Path.Combine(work, "06c_psg_nonvacuity_0.json") ] do
        File.WriteAllText(path, File.ReadAllText(path).Replace("--tlimit-per=25000", "--tlimit=25000"))
    let archived =
        { summary with
            Solver = { summary.Solver with Arguments = legacyArgs }
            ReceiptSha256 = (ClangToolchain.sha256 receiptPath).ToUpperInvariant()
            StageReceipts =
                summary.StageReceipts
                |> Array.map (fun digest -> { digest with Sha256 = (ClangToolchain.sha256 digest.Path).ToUpperInvariant() }) }
    SubstantiationProof.validateStoredEvidence proofSources proofHarnessPath archived work |> accepted

[<Theory>]
[<InlineData("Invocation")>]
[<InlineData("InputSha256")>]
[<InlineData("QuerySha256")>]
[<InlineData("SolverIdentity")>]
[<InlineData("SolverArguments")>]
[<InlineData("StandardError")>]
[<InlineData("Anchor")>]
[<InlineData("Source")>]
[<InlineData("Verdict")>]
let ``compiler conditional receipts refuse mismatched invocation tool query and outcomes`` field =
    let work = compilerStageFixture ()
    let path = Path.Combine(work, "06c_psg_premises_0.json")
    let document =
        let doc = Json.parseOrFail(File.ReadAllText path)
        match field with
        | "Invocation" -> jSet field (JsonValue.String(Guid.NewGuid().ToString())) doc
        | "InputSha256" | "QuerySha256" -> jSet field (JsonValue.String(String.replicate 64 "0")) doc
        | "SolverIdentity" -> jUpdate "Tools" (jUpdateAt 0 (jSet "Version" (JsonValue.String "cvc5 unrelated version"))) doc
        | "SolverArguments" ->
            let historical = JsonValue.Array [ JsonValue.String "--lang=smt2"; JsonValue.String "--tlimit=25000" ]
            jUpdate "Tools" (jUpdateAt 0 (jSet "Arguments" historical)) doc
        | "StandardError" -> jSet field (JsonValue.String "unexpected solver diagnostic") doc
        | "Anchor" -> jUpdate "Outcomes" (jUpdateAt 0 (jSet field (JsonValue.String "absent-obligation"))) doc
        | "Source" -> jUpdate "Outcomes" (jUpdateAt 0 (jSet field (JsonValue.String(proofHarnessPath + ":2:1")))) doc
        | "Verdict" -> jUpdate "Outcomes" (jUpdateAt 0 (jSet field (JsonValue.String "unsat"))) doc
        | _ -> failwithf "unknown fixture mutation %s" field
    File.WriteAllText(path, Json.serialize document)
    SubstantiationProof.evaluate proofSources proofHarnessPath work |> refused "FS9237"

[<Fact>]
let ``second unopened exported module type error is not demoted`` () =
    let work = scratch "unopened-module"
    let first = Path.Combine(work, "First.clef")
    let second = Path.Combine(work, "Unopened.clef")
    File.WriteAllText(first, "module Round19.First\nlet value () : int = 7\n")
    File.WriteAllText(second, "module Round19.Unopened\nlet broken : int = \"not an integer\"\n")
    let project = writeProject work [ first; second ] None
    let before = [ project; first; second ] |> List.map (fun path -> path, File.ReadAllBytes path)
    let outcome = request project None None work |> Substantiation.evaluate
    outcome |> refused "FS9235"
    match outcome with
    | Error refusal ->
        Assert.Contains("Unopened.clef", refusal.Text)
        Assert.Contains("CCS8003", refusal.Text)
    | Ok _ -> failwith "the unopened module's type mismatch was admitted"
    for path, bytes in before do Assert.Equal<byte[]>(bytes, File.ReadAllBytes path)

let changedBytes path (replacement: byte[] -> byte[]) body =
    let original = File.ReadAllBytes path
    try
        File.WriteAllBytes(path, replacement original)
        body ()
    finally
        File.WriteAllBytes(path, original)

let private boundaryRequest (probe: Probe) : SubstantiationBoundary.Request =
    { FidprojPath = probe.Project
      PackageName = "SubstantiationProbe"
      LinkLibraries = [| "substantiation_probe" |]
      LinkObjects = [| probe.Archive |]
      PilotPath = Some probe.Pilot
      LinkProvenancePath = Some probe.Provenance
      Sources = probe.Sources |> List.map (fun path -> path, File.ReadAllText path) |> List.toArray
      WorkDirectory = Path.Combine(probe.Work, "boundary-check") }

[<Fact>]
let ``native static probe substantiates both solver clauses and both manifest formats`` () =
    let probe = nativeProbe ()
    // Generated library projects can carry the library identifier without an archive
    // map. Only the separate synthetic harness adds the verified #204 archive selection.
    let projectLines = File.ReadAllLines probe.Project |> Array.filter (fun line -> not (line.StartsWith("archives =", StringComparison.Ordinal)))
    File.WriteAllLines(probe.Project, projectLines)
    let original = (probe.Project :: probe.Sources) |> List.map (fun path -> path, ClangToolchain.sha256 path)
    let output = Path.Combine(probe.Work, "manifest")
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let launch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "substantiate"; "--fidproj"; probe.Project; "--pilot"; probe.Pilot
              "--link-provenance"; probe.Provenance; "--output"; output
              "--work-dir"; Path.Combine(probe.Work, "substantiation") ]
    let cliDirectory = Path.Combine(probe.Work, "cli-working-directory")
    Directory.CreateDirectory cliDirectory |> ignore
    launch.WorkingDirectory <- cliDirectory
    launch.Environment["COMPOSER_DLL"] <- sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll"
    let argumentsPath = recordComposerArguments probe.Work launch
    let summary = run probe.Work "substantiate-cli" launch
    // The CLI writes only where its options direct it: nothing in its working directory, and
    // no default scratch directory left in its temporary directory, which `run` selects.
    Assert.Empty(Directory.GetFileSystemEntries cliDirectory)
    Assert.Empty(Directory.GetDirectories(Path.Combine(probe.Work, "tool-scratch"), "farscape-*"))
    Assert.Contains("Package: SubstantiationProbe", summary)
    Assert.Contains("UNSAT(H and not C)", summary)
    Assert.Contains("SAT(H)", summary)
    Assert.Contains("Pending: 0; refuted: 0", summary)
    let manifest =
        File.ReadAllText(Path.Combine(output, "SubstantiationProbe.substantiation.json"))
        |> Substantiation.parseJson |> accepted
    Assert.Equal(2, manifest.SchemaVersion)
    let legacy = { manifest with SchemaVersion = 1; SourceInputsSha256 = "" } |> Substantiation.seal
    let historical = legacy |> Substantiation.toJson |> Substantiation.parseJson |> accepted
    match Substantiation.validateManifest historical with
    | Error refusal -> Assert.Contains("Current proof verification requires a sealed checked source-input receipt.", refusal.Text)
    | Ok _ -> failwith "An archive-only manifest was admitted as current proof evidence."
    Assert.False(manifest.AllowWarnings)
    Assert.True(manifest.WarningsAsErrors)
    Assert.Equal<string[]>([| "CCS8500" |], manifest.ElevatedWarningCodes)
    let arguments = File.ReadAllLines argumentsPath
    Assert.Equal<string[]>([| manifest.Compiler.Path; "compile"; "--warn-as-error"; "--no-color"; "--retain-source-inputs"; manifest.HarnessProject.Path; "-k"; "-o"; Path.Combine(Path.GetDirectoryName manifest.HarnessProject.Path, "substantiation-root") |], arguments)
    Assert.Contains($"Library obligations: {manifest.Proof.LibraryObligations};", summary)
    Assert.Contains($"Total obligations: {manifest.Proof.TotalObligations};", summary)
    Assert.True(manifest.Proof.TotalObligations > 0, "the native compiler journey must emit obligations")
    Assert.True(manifest.Proof.LibraryObligations > 0, "library sources must contribute obligations independently of the harness")
    Assert.True(manifest.Proof.HarnessObligations > 0, "the harness must be accounted separately")
    Assert.Equal(manifest.Proof.LibraryObligations, manifest.Proof.LibraryDischargedUnsatCount)
    Assert.Equal(manifest.Proof.LibraryObligations, manifest.Proof.LibraryPremisesSatCount)
    Assert.True(manifest.Proof.CompilerPremiseReceiptsCount > 0, "the native foreign result must produce Composer premise receipts")
    Assert.True(manifest.Proof.CompilerNonVacuityReceiptsCount > 0, "the native foreign result must produce Composer non-vacuity receipts")
    Assert.Equal((manifest.Proof.CompilerPremiseReceiptsCount + manifest.Proof.CompilerNonVacuityReceiptsCount) * 4, manifest.Proof.StageReceipts.Length)
    Assert.Equal(ClangToolchain.sha256 manifest.Dotnet.Path, manifest.Dotnet.Sha256)
    Assert.Equal(manifest.Proof.TotalObligations, manifest.Proof.DischargedUnsatCount)
    Assert.Equal(manifest.Proof.TotalObligations, manifest.Proof.PremisesSatCount)
    Assert.Equal(0, manifest.Proof.PendingOpenCount)
    Assert.Equal(0, manifest.Proof.RefutedCount)
    let obligationDocument = Json.parseOrFail(File.ReadAllText(Path.Combine(manifest.ArtifactsDirectory, "06a_obligations.json")))
    let nestedOrigin = Path.Combine(probe.Work, "Scalar.clef") + ":7:"
    let nestedKinds =
        obligationDocument
        |> jProp "obligations"
        |> Json.items
        |> List.filter (fun obligation -> (jStr "source" obligation).StartsWith(nestedOrigin, StringComparison.Ordinal))
        |> List.map (jStr "kind")
        |> Set.ofList
    Assert.Contains("integer-literal-range", nestedKinds)
    Assert.Contains("integer-representation-coverage", nestedKinds)
    Assert.Equal(probe.Sources.Length, manifest.Sources.Length)
    Assert.All(manifest.Sources, fun source ->
        Assert.False(String.IsNullOrWhiteSpace source.ModuleName)
        Assert.Equal(ClangToolchain.sha256 source.Path, source.Sha256))
    Assert.Contains("static-dynamic-link-elision", manifest.Boundary.RequiredSeamChecks)
    Assert.DoesNotContain("measured-record-abi", manifest.Boundary.RequiredSeamChecks)
    Assert.DoesNotContain("callback-registration-invocation", manifest.Boundary.RequiredSeamChecks)
    Assert.Contains("Round19.Probe.Scalar.Nested.nestedSeven", File.ReadAllText manifest.Harness.Path)
    let boundaryPath = Path.Combine(probe.Work, "generated", "Boundary", "Boundary.clef")
    let boundarySource = manifest.Sources |> Array.find (fun source -> source.Path = boundaryPath)
    let descriptor = "Round19.Probe.Boundary.substantiation_identityDescriptor"
    Assert.Contains(descriptor, boundarySource.CompileTimeDeclarations)
    Assert.DoesNotContain(descriptor, boundarySource.RuntimeDeclarations)
    Assert.DoesNotContain("let _ = " + descriptor, File.ReadAllText manifest.Harness.Path)
    Assert.Contains("Round19.Probe.Boundary.substantiation_identity", boundarySource.ForeignDeclarations)
    Assert.DoesNotContain("Round19.Probe.Boundary.substantiation_identity", boundarySource.RuntimeDeclarations)
    let asJson = Substantiation.toJson manifest
    let asToml = Substantiation.toToml manifest
    Assert.Equal<Substantiation.Manifest>(manifest, Substantiation.parseJson asJson |> accepted)
    Assert.Equal<Substantiation.Manifest>(manifest, Substantiation.parseToml asToml |> accepted)
    let json = requiredFile "JSON substantiation manifest" (Path.Combine(output, manifest.Package + ".substantiation.json"))
    let toml = requiredFile "TOML substantiation manifest" (Path.Combine(output, manifest.Package + ".substantiation.toml"))
    Assert.Equal<Substantiation.Manifest>(manifest, File.ReadAllText json |> Substantiation.parseJson |> accepted)
    Assert.Equal<Substantiation.Manifest>(manifest, File.ReadAllText toml |> Substantiation.parseToml |> accepted)
    Substantiation.validateManifest manifest |> accepted
    Assert.Equal(Path.Combine(manifest.ArtifactsDirectory, "01_psg0.json"), manifest.Psg0.Path)
    Assert.Equal(ClangToolchain.sha256 manifest.Psg0.Path, manifest.Psg0.Sha256)
    changedBytes manifest.Psg0.Path (fun bytes -> Array.append bytes [| 10uy |]) (fun () ->
        Substantiation.validateManifest manifest |> refused "FS9234")
    let heldPsg0 = manifest.Psg0.Path + ".held"
    try
        File.Move(manifest.Psg0.Path, heldPsg0)
        Substantiation.validateManifest manifest |> refused "FS9234"
    finally
        File.Move(heldPsg0, manifest.Psg0.Path)
    let alternatePsg0 = Path.Combine(probe.Work, "alternate-psg0.json")
    File.Copy(manifest.Psg0.Path, alternatePsg0)
    { manifest with Psg0 = { manifest.Psg0 with Path = alternatePsg0 } }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    // Rebind the file digest and envelope to prove reconciliation is independent of tamper detection.
    let alteredPsg0 expected (alter: JsonValue list -> JsonValue list) =
        changedBytes manifest.Psg0.Path (fun bytes ->
            let json =
                Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))
                |> jUpdate "nodes" (fun arr -> JsonValue.Array (alter (Json.items arr)))
            Encoding.UTF8.GetBytes(Json.serialize json)) (fun () ->
                { manifest with Psg0 = { manifest.Psg0 with Sha256 = ClangToolchain.sha256 manifest.Psg0.Path } }
                |> Substantiation.seal |> Substantiation.validateManifest
                |> (if expected = "" then accepted >> ignore else refused expected))
    let isForeignNode (node: JsonValue) =
        System.Text.RegularExpressions.Regex.IsMatch(jStr "kind" node, "^Binding\\s*\\(\\s*\"substantiation_identity\"")
    let foreignNode (nodes: JsonValue list) = nodes |> List.find isForeignNode
    let isBindingNode name (node: JsonValue) =
        (jStr "kind" node).StartsWith("Binding (\"" + name + "\"", StringComparison.Ordinal)
    let bindingNode name (nodes: JsonValue list) = nodes |> List.find (isBindingNode name)
    let updateNode (target: JsonValue) (f: JsonValue -> JsonValue) (nodes: JsonValue list) =
        let tid = jInt "id" target
        nodes |> List.map (fun n -> if jInt "id" n = tid then f n else n)
    let removeNode (target: JsonValue) (nodes: JsonValue list) =
        let tid = jInt "id" target
        nodes |> List.filter (fun n -> jInt "id" n <> tid)
    let children (nodes: JsonValue list) (node: JsonValue) =
        let ids =
            node
            |> jProp "children"
            |> Json.items
            |> List.choose (fun child -> Json.asInt child |> Option.map int)
            |> Set.ofList
        nodes |> List.filter (fun child -> ids.Contains(jInt "id" child)) |> List.toArray
    let bindingBody nodes binding =
        let direct = children nodes binding
        let bodies =
            match direct |> Array.tryFind (fun child -> (jStr "kind" child).StartsWith("Lambda", StringComparison.Ordinal)) with
            | Some lambda -> children nodes lambda
            | None -> direct
        bodies |> Array.find (fun child -> not ((jStr "kind" child).StartsWith("PatternBinding", StringComparison.Ordinal)))
    let rangeParts (node: JsonValue) =
        let range = jStr "range" node
        let columnAt = range.LastIndexOf ':'
        let lineAt = range.LastIndexOf(':', columnAt - 1)
        range.Substring(0, lineAt), Int32.Parse(range.Substring(lineAt + 1, columnAt - lineAt - 1)), Int32.Parse(range.Substring(columnAt + 1))
    // Round 19e: a valid digest cannot authorize a compiler attribute before the
    // inventoried first token, including an earlier column on the same line.
    alteredPsg0 "FS9233" (fun nodes ->
        let binding = bindingNode "seven" nodes
        let path, line, column = rangeParts binding
        updateNode binding (jSet "range" (JsonValue.String($"{path}:{line - 1}:{column}"))) nodes)
    alteredPsg0 "FS9233" (fun nodes ->
        let binding = bindingNode "nestedSeven" nodes
        let path, line, _ = rangeParts binding
        updateNode binding (jSet "range" (JsonValue.String($"{path}:{line}:3"))) nodes)
    let scalarLines = File.ReadAllLines(Path.Combine(probe.Work, "Scalar.clef"))
    let documentationLine (text: string) = scalarLines |> Array.findIndex (fun line -> line.TrimStart() = text) |> (+) 1
    alteredPsg0 "FS9233" (fun nodes ->
        let binding = bindingNode "documentedSeven" nodes
        let path, line, column = rangeParts binding
        Assert.Equal(documentationLine ("/" + "// Current scalar documentation group."), line)
        Assert.Equal(0, column)
        let earlier = documentationLine ("/" + "// Earlier scalar documentation group.") - 1
        updateNode binding (jSet "range" (JsonValue.String($"{path}:{earlier}:0"))) nodes)
    alteredPsg0 "FS9233" (fun nodes ->
        let binding = bindingNode "documentedNested" nodes
        let path, line, column = rangeParts binding
        Assert.Equal(documentationLine ("/" + "// First nested documentation line."), line)
        Assert.Equal(4, column)
        updateNode binding (jSet "range" (JsonValue.String($"{path}:{line}:3"))) nodes)
    for precedingLines in [ 1; 2 ] do
        alteredPsg0 "FS9233" (fun nodes ->
            let binding = bindingNode "undocumentedSeven" nodes
            let path, line, _ = rangeParts binding
            updateNode binding (jSet "range" (JsonValue.String($"{path}:{line - precedingLines}:0"))) nodes)
    for invalidRange in [ ""; "missing-position"; "source.clef:one:0"; "source.clef:2" ] do
        alteredPsg0 "FS9233" (fun nodes -> updateNode (bindingNode "seven" nodes) (jSet "range" (JsonValue.String invalidRange)) nodes)
    alteredPsg0 "FS9233" (fun nodes -> updateNode (bindingNode "seven" nodes) (jRemove "range") nodes)
    // Keep the binding type intact: these controls exercise the body bijection,
    // independently of the foreign type mutations below.
    alteredPsg0 "FS9243" (fun nodes ->
        let binding = bindingNode "seven" nodes
        let body = bindingBody nodes binding
        updateNode body (jSet "kind" (JsonValue.String "Literal Unit") >> jSet "range" (jProp "range" binding) >> jSet "type" (JsonValue.String "NTUunit")) nodes)
    alteredPsg0 "FS9243" (fun nodes ->
        let binding = bindingNode "seven" nodes
        let body = bindingBody nodes binding
        updateNode body (jSet "kind" (JsonValue.String "Literal Unit") >> jSet "type" (JsonValue.String "TNum (Int32)")) nodes)
    alteredPsg0 "FS9243" (fun nodes ->
        let binding = bindingNode "seven" nodes
        let id = 2147483001
        let body =
            bindingBody nodes binding
            |> jSet "id" (Json.ofInt64 (int64 id))
            |> jSet "parent" (jProp "id" binding)
            |> jSet "kind" (JsonValue.String "Literal Unit")
            |> jSet "range" (jProp "range" binding)
            |> jSet "type" (JsonValue.String "NTUunit")
            |> jSet "children" (JsonValue.Array [])
        let updated = updateNode binding (jUpdate "children" (jAdd (Json.ofInt64 (int64 id)))) nodes
        updated @ [ body ])
    alteredPsg0 "FS9243" (fun nodes ->
        let body = bindingBody nodes (foreignNode nodes)
        Assert.Equal("Literal Unit", jStr "kind" body)
        updateNode body (jSet "kind" (JsonValue.String "Literal (Int32 7)")) nodes)
    alteredPsg0 "FS9243" (fun nodes ->
        let binding = foreignNode nodes
        let body = bindingBody nodes binding
        let path, line, column = rangeParts binding
        updateNode body (jSet "range" (JsonValue.String($"{path}:{line + 1}:{column}")) >> jSet "type" (JsonValue.String "NTUunit")) nodes)
    for ntu in
        [ "Unexpected"
          "TFun (TNum (Int32), TNum (Int32)"
          "TFun (TTuple [TInt; TInt], TInt)"
          "TFun (TInt, TAnon [\"quot\", TInt])"
          "TApp ({ Name = \"Unmeasured\"; Layout = Record }, [])"
          "TApp ({ Name = \"TagUnion\"; Layout = Union }, [])"
          "TApp ({ Name = \"nativeptr\"; Layout = Opaque }, [TApp ({ Name = \"Unmeasured\"; Layout = Record }, [])])"
          "TFun (TApp ({ Name = \"CHandle\"; Layout = Opaque }, [TApp ({ Name = \"Sqlite3\"; Layout = Union }, [])]), TApp ({ Name = \"Unmeasured\"; Layout = Record }, []))" ] do
        alteredPsg0 "FS9244" (fun nodes -> updateNode (foreignNode nodes) (jSet "type" (JsonValue.String ntu)) nodes)
    for carrier in [ "CHandle"; "BorrowedView"; "MappedRegion" ] do
        alteredPsg0 "" (fun nodes ->
            updateNode (foreignNode nodes) (jSet "type" (JsonValue.String(
                "TFun (TApp ({ Name = \"" + carrier + "\"; Layout = Opaque }, [TApp ({ Name = \"Sqlite3\"; Layout = Union }, [])]), TNum (Int32))"))) nodes)
    alteredPsg0 "FS9243" (fun nodes -> removeNode (foreignNode nodes) nodes)
    alteredPsg0 "FS9233" (fun nodes ->
        let unknown =
            foreignNode nodes
            |> jSet "id" (Json.ofInt64 2147483000L)
            |> jSet "kind" (JsonValue.String "Binding (\"unclassified_private\", ...)")
        nodes @ [ unknown ])
    alteredPsg0 "FS9233" (fun nodes ->
        let unknown =
            foreignNode nodes
            |> jSet "id" (Json.ofInt64 2147483000L)
            |> jSet "kind" (JsonValue.String "TypeDef (\"UnclassifiedType\", ...)")
        nodes @ [ unknown ])
    alteredPsg0 "FS9233" (fun nodes ->
        let foreign = foreignNode nodes
        let parent = jInt "parent" foreign
        let moduleNode = nodes |> List.find (fun node -> jInt "id" node = parent)
        updateNode moduleNode (jSet "kind" (JsonValue.String "ModuleDef (\"Wrong.Root\", ...)")) nodes)
    Assert.Equal(Path.Combine(manifest.ArtifactsDirectory, "10_witness_units.json"), manifest.WitnessUnits.Path)
    Assert.Equal(ClangToolchain.sha256 manifest.WitnessUnits.Path, manifest.WitnessUnits.Sha256)
    changedBytes manifest.WitnessUnits.Path (fun bytes -> Array.append bytes [| 10uy |]) (fun () ->
        Substantiation.validateManifest manifest |> refused "FS9234")
    let heldWitness = manifest.WitnessUnits.Path + ".held"
    try
        File.Move(manifest.WitnessUnits.Path, heldWitness)
        Substantiation.validateManifest manifest |> refused "FS9234"
    finally
        File.Move(heldWitness, manifest.WitnessUnits.Path)
    let alternateWitness = Path.Combine(probe.Work, "alternate-witness-units.json")
    File.Copy(manifest.WitnessUnits.Path, alternateWitness)
    { manifest with WitnessUnits = { manifest.WitnessUnits with Path = alternateWitness } }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    let witnessLinks = refusedLinks (scratch "witness-links")
    for prohibited in [ Path.Combine(witnessLinks.Broken, "10_witness_units.json"); Path.Combine(witnessLinks.Cycle, "10_witness_units.json") ] do
        { manifest with WitnessUnits = { manifest.WitnessUnits with Path = prohibited } }
        |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9245"
    let alteredWitness expected (alter: JsonValue -> JsonValue) =
        changedBytes manifest.WitnessUnits.Path (fun bytes ->
            let json = Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) |> alter
            Encoding.UTF8.GetBytes(Json.serialize json)) (fun () ->
                { manifest with WitnessUnits = { manifest.WitnessUnits with Sha256 = ClangToolchain.sha256 manifest.WitnessUnits.Path } }
                |> Substantiation.seal |> Substantiation.validateManifest
                |> (if expected = "" then accepted >> ignore else refused expected))
    let retainedPsg = Json.parseOrFail(File.ReadAllText(manifest.Psg0.Path).TrimStart('\uFEFF'))
    let retainedNodes = retainedPsg |> jProp "nodes" |> Json.items
    let authoredId = jInt "id" (bindingNode "seven" retainedNodes)
    let foreignId = jInt "id" (foreignNode retainedNodes)
    let appendImport (declaration: int) (library: string) (symbol: string) (json: JsonValue) =
        let importNode =
            JsonValue.Object
                [ "sourceBoundary",
                  JsonValue.Object
                      [ "declaration", Json.ofInt64 (int64 declaration)
                        "library", JsonValue.String library ]
                  "symbol", JsonValue.String symbol ]
        jUpdate "units" (jUpdateAt 0 (jUpdate "imports" (jAdd importNode))) json
    alteredWitness "FS9243" (appendImport authoredId "substantiation_probe" "substantiation_identity")
    alteredWitness "FS9243" (appendImport foreignId "not_linked" "substantiation_identity")
    alteredWitness "FS9243" (appendImport foreignId "substantiation_probe" "uninventoried_symbol")
    alteredWitness "FS9233" (jRemove "units")
    // A declaration outside the library's binding map is outside this check.
    alteredWitness "" (appendImport 2147483001 "platform-owned" "platform-symbol")
    alteredWitness "" (fun json ->
        let importNode =
            JsonValue.Object
                [ "sourceBoundary", JsonValue.Null
                  "symbol", JsonValue.String "no-source-boundary" ]
        jUpdate "units" (jUpdateAt 0 (jUpdate "imports" (jAdd importNode))) json)
    // #209: the compiler attributes the foreign import to the FunctionDescriptor RecordExpr
    // inside the descriptor binding, not to a module binding node. The parent walk must
    // resolve the emitted node to the library declaration that encloses it.
    let retainedWitness = Json.parseOrFail(File.ReadAllText(manifest.WitnessUnits.Path).TrimStart('\uFEFF'))
    let emittedImports (json: JsonValue) =
        json
        |> jProp "units"
        |> Json.items
        |> List.collect (fun unitNode -> unitNode |> jProp "imports" |> Json.items)
        |> List.filter (fun importNode -> jStr "symbol" importNode = "substantiation_identity")
        |> List.toArray
    let emittedImport = Assert.Single(emittedImports retainedWitness)
    let emittedDeclaration = jInt "declaration" (jProp "sourceBoundary" emittedImport)
    Assert.Equal("substantiation_probe", jStr "library" (jProp "sourceBoundary" emittedImport))
    let nodeWithId id = retainedNodes |> List.find (fun node -> jInt "id" node = id)
    let descriptorId = jInt "id" (bindingNode "substantiation_identityDescriptor" retainedNodes)
    Assert.Matches("^(RecordExpr|Binding)\\b", jStr "kind" (nodeWithId emittedDeclaration))
    let rec enclosingBinding id =
        if id = descriptorId then true
        else
            match (nodeWithId id) |> Json.prop "parent" |> Option.bind Json.asInt with
            | None -> false
            | Some parent -> enclosingBinding (int parent)
    Assert.True(enclosingBinding emittedDeclaration, "the emitted source boundary node must lie inside the descriptor binding")
    let correspondence = Substantiation.validateManifestCorrespondence manifest |> accepted
    let resolvedImport = Assert.Single(correspondence.Imports)
    Assert.Equal(emittedDeclaration, resolvedImport.DeclarationNode)
    Assert.Equal("Round19.Probe.Boundary.substantiation_identityDescriptor", resolvedImport.Declaration)
    Assert.Equal("substantiation_probe", resolvedImport.Library)
    Assert.Equal("substantiation_identity", resolvedImport.Symbol)
    Assert.False(resolvedImport.Resource)
    let descriptorDeclaration = correspondence.Declarations |> Array.find (fun declaration -> declaration.BindingNodeId = descriptorId)
    Assert.Equal("Round19.Probe.Boundary.substantiation_identityDescriptor", descriptorDeclaration.Qualified)
    let foreignDeclaration = correspondence.Declarations |> Array.find (fun declaration -> declaration.BindingNodeId = foreignId)
    Assert.Equal(Some("substantiation_probe", "substantiation_identity"), foreignDeclaration.Foreign)
    Assert.Equal(Substantiation.DescriptorSymbol.Named "substantiation_identity", descriptorDeclaration.Descriptor)
    Assert.Equal(Substantiation.DescriptorSymbol.NotDescriptor, foreignDeclaration.Descriptor)
    Assert.Contains(correspondence.Definitions, fun definition -> definition.Declaration = Some "Round19.Probe.Scalar.seven" && definition.SourceInLibrary && definition.RootInLibrary)
    Assert.Equal(Some manifest.Proof.TotalObligations, correspondence.Obligations |> Option.map Array.length)
    // A forged library or symbol on the node identity the compiler actually emitted is refused.
    let forgeEmitted (alter: JsonValue -> JsonValue) (json: JsonValue) =
        jUpdate "units" (fun units ->
            match units with
            | JsonValue.Array unitItems ->
                unitItems
                |> List.map (jUpdate "imports" (fun imps ->
                    match imps with
                    | JsonValue.Array impItems ->
                        impItems
                        |> List.map (fun imp -> if jStr "symbol" imp = "substantiation_identity" then alter imp else imp)
                        |> JsonValue.Array
                    | other -> other))
                |> JsonValue.Array
            | other -> other) json
    alteredWitness "FS9243" (forgeEmitted (jUpdate "sourceBoundary" (jSet "library" (JsonValue.String "not_linked"))))
    alteredWitness "FS9243" (forgeEmitted (jSet "symbol" (JsonValue.String "uninventoried_symbol")))
    // The same node identity, appended with a forged pairing, is refused as well.
    alteredWitness "FS9243" (appendImport emittedDeclaration "not_linked" "substantiation_identity")
    alteredWitness "FS9243" (appendImport emittedDeclaration "substantiation_probe" "uninventoried_symbol")
    // A descendant of an authored runtime binding is no boundary declaration.
    let authoredBody = jInt "id" (bindingBody retainedNodes (bindingNode "seven" retainedNodes))
    alteredWitness "FS9243" (appendImport authoredBody "substantiation_probe" "substantiation_identity")
    // A library node below no module binding (the root module, a type definition) is refused.
    let libraryModuleId = jInt "parent" (foreignNode retainedNodes)
    alteredWitness "FS9243" (appendImport libraryModuleId "substantiation_probe" "substantiation_identity")
    let libraryTypeId =
        retainedNodes
        |> List.find (fun node -> (jStr "kind" node).StartsWith("TypeDef", StringComparison.Ordinal) && (jStr "range" node).Contains("Scalar.clef:", StringComparison.Ordinal))
        |> jInt "id"
    alteredWitness "FS9243" (appendImport libraryTypeId "substantiation_probe" "substantiation_identity")
    // Resource imports are attributed through the same parent walk.
    let appendResource (sourceBoundary: JsonValue) (library: string) (symbol: string) (json: JsonValue) =
        let resource =
            JsonValue.Object
                [ "sourceBoundary", sourceBoundary
                  "library", JsonValue.String library
                  "symbol", JsonValue.String symbol ]
        jUpdate "units" (jUpdateAt 0 (jUpdate "resourceImports" (jAdd resource))) json
    alteredWitness "" (appendResource (Json.ofInt64 (int64 emittedDeclaration)) "substantiation_probe" "substantiation_identity")
    alteredWitness "FS9243" (appendResource (Json.ofInt64 (int64 emittedDeclaration)) "not_linked" "substantiation_identity")
    alteredWitness "FS9243" (appendResource (Json.ofInt64 (int64 authoredId)) "substantiation_probe" "substantiation_identity")
    alteredWitness "FS9243" (appendResource (Json.ofInt64 (int64 libraryModuleId)) "substantiation_probe" "substantiation_identity")
    alteredWitness "FS9243" (appendResource (JsonValue.String "not-a-node") "substantiation_probe" "substantiation_identity")
    alteredWitness "" (appendResource (Json.ofInt64 2147483001L) "platform-owned" "platform-symbol")
    // Parent edges that form a cycle or name an absent node cannot be walked and are refused.
    let harnessMain (nodes: JsonValue list) =
        nodes |> List.find (fun node ->
            (jStr "kind" node).StartsWith("Binding (\"main\"", StringComparison.Ordinal)
            && (jStr "range" node).Contains("SubstantiationRoot.clef:", StringComparison.Ordinal))
    alteredPsg0 "FS9233" (fun nodes ->
        let binding = harnessMain nodes
        let child = (children nodes binding)[0]
        updateNode binding (jSet "parent" (jProp "id" child)) nodes)
    alteredPsg0 "FS9233" (fun nodes -> updateNode (harnessMain nodes) (jSet "parent" (Json.ofInt64 2147483600L)) nodes)
    Substantiation.validateManifest manifest |> accepted
    Assert.False(Directory.Exists(Path.Combine(manifest.ArtifactsDirectory, "recheck-boundary")), "boundary validation must clean its temporary directory")
    let checkMalformed changed =
        let resealed = Substantiation.seal changed
        resealed |> Substantiation.toJson |> Substantiation.parseJson |> refused "FS9236"
        resealed |> Substantiation.validateManifest |> refused "FS9236"
    // Foreign source inventory and symbol inventory are independently bound.
    { manifest with Sources = manifest.Sources |> Array.map (fun source -> { source with ForeignDeclarations = [||] }) }
    |> checkMalformed
    { manifest with Boundary = { manifest.Boundary with ExternSymbols = [||] } } |> checkMalformed
    { manifest with Boundary = { manifest.Boundary with ExternSymbols = [| "invented_one"; "invented_two" |] } }
    |> checkMalformed
    let projectLinks = refusedLinks (scratch "project-links")
    for projectPath in [ Path.Combine(projectLinks.Broken, "Library.fidproj"); Path.Combine(projectLinks.Cycle, "Library.fidproj") ] do
        { manifest with FidprojPath = projectPath }
        |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9245"
    for codes in [ [||]; [| "CCS8500"; "CCS8500" |]; [| "CCS8777"; "CCS8500" |]; [| "CCS8500"; "invalid" |] ] do
        { manifest with ElevatedWarningCodes = codes } |> checkMalformed
    { manifest with ElevatedWarningCodes = [| "CCS8500"; "CCS8777" |] }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    { manifest with WarningsAsErrors = false }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    changedBytes manifest.CompilerLog.Path (fun bytes ->
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString bytes + "\nScalar.clef:5: warning CCS8999: retained advisory\n")) (fun () ->
            { manifest with CompilerLog = { manifest.CompilerLog with Sha256 = ClangToolchain.sha256 manifest.CompilerLog.Path } }
            |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9235")
    { manifest with Boundary = { manifest.Boundary with LinkMode = "banana"; TargetDataModel = "anything"; ClangTarget = "" } }
    |> checkMalformed
    { manifest with
        Boundary =
            { manifest.Boundary with
                Strata = { manifest.Boundary.Strata with StaticArchive = false }
                RequiredSeamChecks = manifest.Boundary.RequiredSeamChecks |> Array.filter ((<>) "static-dynamic-link-elision") } }
    |> checkMalformed
    { manifest with
        Boundary =
            { manifest.Boundary with
                Strata = { ForeignCalls = false; MeasuredRecords = false; StaticArchive = false; ErrorConventions = false
                           Ownership = false; ZeroCopy = false; Callbacks = false; Nullability = false }
                RestsOn = [||]; RequiredSeamChecks = [| "falsification-refusal-controls" |] } }
    |> checkMalformed
    { manifest with
        Proof =
            { manifest.Proof with LibraryObligations = 0; LibraryDischargedUnsatCount = 0; LibraryPremisesSatCount = 0 } }
    |> checkMalformed
    for mutate in
        [ (fun boundary -> { boundary with SubstantiationBoundary.BoundaryEvidence.LinkMode = "unsupported" })
          (fun boundary -> { boundary with TargetDataModel = "anything" })
          (fun boundary -> { boundary with ClangTarget = "" })
          (fun boundary -> { boundary with Files = boundary.Files |> Array.map (fun file -> { file with Kind = "unknown-kind" }) }) ] do
        { manifest with Boundary = mutate manifest.Boundary } |> checkMalformed
    // A structurally coherent reseal still cannot replace measured live boundary facts.
    // The witnessed foreign import now resolves to its library declaration (#209), so a
    // replaced extern symbol inventory is refused at witness reconciliation (FS9243), before
    // the fresh boundary evaluation that refused it with FS9236 while the import was skipped.
    { manifest with Boundary = { manifest.Boundary with ExternSymbols = [| "invented_symbol" |] } }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9243"
    { manifest with
        Boundary =
            { manifest.Boundary with RestsOn = manifest.Boundary.RestsOn |> Array.map (fun hypothesis -> { hypothesis with Statement = "invented evidence" }) } }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    Assert.False(Directory.Exists(Path.Combine(manifest.ArtifactsDirectory, "recheck-boundary")), "refused boundary validation must clean its temporary directory")
    { manifest with Dotnet = { manifest.Dotnet with Sha256 = String.replicate 64 "0" } }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9234"
    for stage in manifest.Proof.StageReceipts do
        changedBytes stage.Path (fun bytes -> Array.append bytes [| 10uy |]) (fun () ->
            Substantiation.validateManifest manifest |> refused "FS9237")
        let held = stage.Path + ".held"
        try
            File.Move(stage.Path, held)
            Substantiation.validateManifest manifest |> refused "FS9237"
        finally
            File.Move(held, stage.Path)
    Substantiation.validateManifest manifest |> accepted
    // Recomputing the envelope digest cannot authorize omitted project members or
    // changed declarations, and cannot remove an actually required boundary seam.
    { manifest with Sources = manifest.Sources |> Array.skip 1 }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    { manifest with
        Sources = manifest.Sources |> Array.map (fun source ->
            if source.ModuleName = "Round19.Probe.Scalar" then { source with RuntimeDeclarations = [||] }
            else source) }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    { manifest with
        Boundary = { manifest.Boundary with RequiredSeamChecks = [| "falsification-refusal-controls" |] } }
    |> Substantiation.seal |> Substantiation.toJson |> Substantiation.parseJson |> refused "FS9236"
    // Each recorded foreign or source input independently invalidates the old manifest.
    for path in [ probe.Sources |> List.last; probe.Archive; probe.Provenance; probe.Pilot; probe.Header ] do
        changedBytes path (fun bytes -> Array.append bytes [| 10uy |]) (fun () ->
            Substantiation.validateManifest manifest |> refused "FS9234")
    Substantiation.validateManifest manifest |> accepted
    // Fresh boundary checking independently rejects archive bytes that diverge from #204.
    changedBytes probe.Archive (fun bytes -> Array.append bytes [| 10uy |]) (fun () ->
        SubstantiationBoundary.evaluate (boundaryRequest probe) |> refused "FS9242")
    for path in [ probe.Pilot; probe.Header ] do
        let held = path + ".held"
        try
            File.Move(path, held)
            SubstantiationBoundary.evaluate (boundaryRequest probe) |> refused "FS9244"
        finally
            File.Move(held, path)
    // An extern in a second exported source must be represented in the pairing too.
    let unresolved =
        boundaryRequest probe
        |> fun request ->
            { request with
                Sources = Array.append request.Sources [| Path.Combine(probe.Work, "Unresolved.clef"),
                    "module Round19.Probe.Unresolved\n[<FidelityExtern(\"substantiation_probe\", \"not_in_archive\")>]\nlet missing (value: int) : int = 0\n" |] }
    SubstantiationBoundary.evaluate unresolved |> refused "FS9243"
    changedBytes boundaryPath (fun bytes ->
        let text = Encoding.UTF8.GetString bytes
        Assert.Contains("NativeDefault.zeroed ()", text)
        Encoding.UTF8.GetBytes(text.Replace("NativeDefault.zeroed ()", "value + 1"))) (fun () ->
            request probe.Project (Some probe.Pilot) (Some probe.Provenance) probe.Work
            |> Substantiation.evaluate |> refused "FS9233")
    // Descriptor quotations stay in the compiler's descriptor checker. Their fields
    // must still be checked even though quoting them gives no run-time value to touch.
    changedBytes boundaryPath (fun bytes ->
        let text = Encoding.UTF8.GetString bytes
        Assert.Contains("CallingConvention = CDecl", text)
        Encoding.UTF8.GetBytes(text.Replace("CallingConvention = CDecl", "CallingConvention = 7"))) (fun () ->
            let invalid = request probe.Project (Some probe.Pilot) (Some probe.Provenance) probe.Work |> Substantiation.evaluate
            invalid |> refused "FS9235"
            match invalid with
            | Error refusal -> Assert.Contains("The calling convention is not a declared case.", refusal.Text)
            | Ok _ -> failwith "the malformed compiler-read descriptor was admitted")
    for path, digest in original do Assert.Equal(digest, ClangToolchain.sha256 path)

[<Fact>]
let ``witnessed imports swapped between two FunctionDescriptors of one library are refused`` () =
    let probe = nativeProbeWith true
    // As for the static probe above, the library project carries no archive map.
    File.WriteAllLines(probe.Project, File.ReadAllLines probe.Project |> Array.filter (fun line -> not (line.StartsWith("archives =", StringComparison.Ordinal))))
    let manifest =
        request probe.Project (Some probe.Pilot) (Some probe.Provenance) probe.Work
        |> Substantiation.evaluate |> accepted
    let identity, echo = "substantiation_identity", "substantiation_echo"
    let qualified (name: string) = "Round19.Probe.Boundary." + name
    Assert.Equal<string[]>([| echo; identity |], manifest.Boundary.ExternSymbols |> Array.sort)
    Assert.Equal<string[]>([| "substantiation_probe" |], manifest.Boundary.Libraries)
    // Each import resolves to its own descriptor, and each descriptor names its own symbol.
    let correspondence = Substantiation.validateManifestCorrespondence manifest |> accepted
    Assert.Equal<(string * string * string)[]>(
        [| qualified (echo + "Descriptor"), "substantiation_probe", echo
           qualified (identity + "Descriptor"), "substantiation_probe", identity |],
        correspondence.Imports |> Array.map (fun import -> import.Declaration, import.Library, import.Symbol) |> Array.sort)
    let declaration (name: string) = correspondence.Declarations |> Array.find (fun node -> node.Qualified = qualified name)
    for symbol in [ identity; echo ] do
        Assert.Equal(Substantiation.DescriptorSymbol.Named symbol, (declaration (symbol + "Descriptor")).Descriptor)
        Assert.Equal(None, (declaration (symbol + "Descriptor")).Foreign)
        Assert.Equal(Substantiation.DescriptorSymbol.NotDescriptor, (declaration symbol).Descriptor)
        Assert.Equal(Some("substantiation_probe", symbol), (declaration symbol).Foreign)
    Assert.Equal(Substantiation.DescriptorSymbol.NotDescriptor, (declaration "identityResultContract").Descriptor)
    // Every alteration rebinds the file digests and the envelope, so each refusal below is the
    // reconciliation and not the digest check.
    let jsonBytes (alter: JsonValue -> JsonValue) (bytes: byte[]) =
        let json = Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) |> alter
        Encoding.UTF8.GetBytes(Json.serialize json)
    let rebound () =
        { manifest with
            Psg0 = { manifest.Psg0 with Sha256 = ClangToolchain.sha256 manifest.Psg0.Path }
            WitnessUnits = { manifest.WitnessUnits with Sha256 = ClangToolchain.sha256 manifest.WitnessUnits.Path } }
        |> Substantiation.seal |> Substantiation.validateManifest
    let expecting (expected: string) (result: Result<unit, Diagnostics.Refusal>) =
        if expected = "" then accepted result else refused expected result
    let alteredWitness expected (alter: JsonValue -> JsonValue) =
        changedBytes manifest.WitnessUnits.Path (jsonBytes alter) (fun () -> rebound () |> expecting expected)
    let alteredPsg0 expected (alter: JsonValue list -> JsonValue list) =
        changedBytes manifest.Psg0.Path (jsonBytes (jUpdate "nodes" (fun arr -> JsonValue.Array (alter (Json.items arr))))) (fun () -> rebound () |> expecting expected)
    let importOf (symbol: string) (json: JsonValue) =
        json
        |> jProp "units"
        |> Json.items
        |> List.collect (fun unitNode -> unitNode |> jProp "imports" |> Json.items)
        |> List.filter (fun importNode -> jStr "symbol" importNode = symbol)
        |> List.exactlyOne
    let updateImports (f: JsonValue -> JsonValue) (json: JsonValue) =
        jUpdate "units" (fun units ->
            match units with
            | JsonValue.Array unitItems ->
                unitItems
                |> List.map (jUpdate "imports" (fun imps ->
                    match imps with
                    | JsonValue.Array impItems -> impItems |> List.map f |> JsonValue.Array
                    | other -> other))
                |> JsonValue.Array
            | other -> other) json
    let nodeOf (importNode: JsonValue) = jInt "declaration" (jProp "sourceBoundary" importNode)
    alteredWitness "" id
    alteredPsg0 "" id
    // Both symbols are extern symbols of the one linked library, so the boundary inventory
    // admits either of them on either declaration. One import renamed to the other symbol:
    alteredWitness "FS9243" (updateImports (fun imp -> if jStr "symbol" imp = identity then jSet "symbol" (JsonValue.String echo) imp else imp))
    // The two symbols exchanged between the two descriptor nodes:
    alteredWitness "FS9243" (updateImports (fun imp ->
        let sym = jStr "symbol" imp
        if sym = identity then jSet "symbol" (JsonValue.String echo) imp
        elif sym = echo then jSet "symbol" (JsonValue.String identity) imp
        else imp))
    // The two descriptor node identities exchanged between the two symbols:
    alteredWitness "FS9243" (fun json ->
        let firstNode = nodeOf (importOf identity json)
        let secondNode = nodeOf (importOf echo json)
        updateImports (fun imp ->
            let sym = jStr "symbol" imp
            if sym = identity then jUpdate "sourceBoundary" (jSet "declaration" (Json.ofInt64 (int64 secondNode))) imp
            elif sym = echo then jUpdate "sourceBoundary" (jSet "declaration" (Json.ofInt64 (int64 firstNode))) imp
            else imp) json)
    // The same rule holds on the module binding nodes: a descriptor binding carries its
    // CName, and a FidelityExtern binding carries its own library and symbol.
    let retainedPsg = Json.parseOrFail(File.ReadAllText(manifest.Psg0.Path).TrimStart('\uFEFF'))
    let retainedNodes = retainedPsg |> jProp "nodes" |> Json.items
    let bindingId (name: string) =
        retainedNodes
        |> List.find (fun node -> (jStr "kind" node).StartsWith("Binding (\"" + name + "\"", StringComparison.Ordinal))
        |> jInt "id"
    let retarget (symbol: string) (node: int) (json: JsonValue) =
        updateImports (fun imp ->
            if jStr "symbol" imp = symbol then
                jUpdate "sourceBoundary" (jSet "declaration" (Json.ofInt64 (int64 node))) imp
            else imp) json
    alteredWitness "" (retarget identity (bindingId (identity + "Descriptor")))
    alteredWitness "FS9243" (retarget identity (bindingId (echo + "Descriptor")))
    alteredWitness "" (retarget identity (bindingId identity))
    alteredWitness "FS9243" (retarget identity (bindingId echo))
    // A resource import is attributed by the same rule.
    let retainedWitness = Json.parseOrFail(File.ReadAllText(manifest.WitnessUnits.Path).TrimStart('\uFEFF'))
    let identityRecord = nodeOf (importOf identity retainedWitness)
    let appendResource (node: int) (symbol: string) (json: JsonValue) =
        let resource =
            JsonValue.Object
                [ "sourceBoundary", Json.ofInt64 (int64 node)
                  "library", JsonValue.String "substantiation_probe"
                  "symbol", JsonValue.String symbol ]
        jUpdate "units" (jUpdateAt 0 (jUpdate "resourceImports" (jAdd resource))) json
    alteredWitness "" (appendResource identityRecord identity)
    alteredWitness "FS9243" (appendResource identityRecord echo)
    // The CName is the literal the compiler PSG publishes for the record's CName field, and
    // the source text at that literal's range spells it. A PSG literal renamed to the other
    // valid symbol, or moved off its source position, establishes no CName; the import that
    // resolves to the descriptor is then refused, whether or not the witness is renamed to match.
    let cnameLiteral (nodes: JsonValue list) =
        let record = nodes |> List.find (fun node -> jInt "id" node = identityRecord)
        let field = System.Text.RegularExpressions.Regex.Match(jStr "kind" record, @"\(""CName"",\s*NodeId\s+([0-9]+)\)")
        Assert.True(field.Success, "the witnessed node is not the FunctionDescriptor record")
        let literal = Int32.Parse field.Groups[1].Value
        nodes |> List.find (fun node -> jInt "id" node = literal)
    let updateLiteral (f: JsonValue -> JsonValue) (nodes: JsonValue list) =
        let litId = jInt "id" (cnameLiteral nodes)
        nodes |> List.map (fun n -> if jInt "id" n = litId then f n else n)
    let renameLiteral (nodes: JsonValue list) = updateLiteral (jSet "kind" (JsonValue.String("Literal (String \"" + echo + "\")"))) nodes
    let retainedLiteral = cnameLiteral retainedNodes
    Assert.Equal("Literal (String \"" + identity + "\")", jStr "kind" retainedLiteral)
    alteredPsg0 "FS9243" renameLiteral
    changedBytes manifest.Psg0.Path (jsonBytes (jUpdate "nodes" (fun arr -> JsonValue.Array (renameLiteral (Json.items arr))))) (fun () ->
        alteredWitness "FS9243" (updateImports (fun imp -> if jStr "symbol" imp = identity then jSet "symbol" (JsonValue.String echo) imp else imp)))
    alteredPsg0 "FS9243" (fun nodes ->
        let literal = cnameLiteral nodes
        let range = jStr "range" literal
        let columnAt = range.LastIndexOf ':'
        updateLiteral (jSet "range" (JsonValue.String(range.Substring(0, columnAt + 1) + string (Int32.Parse(range.Substring(columnAt + 1)) + 1)))) nodes)
    alteredPsg0 "FS9243" (updateLiteral (jSet "kind" (JsonValue.String "Literal (Int32 7)")))
    Substantiation.validateManifest manifest |> accepted

[<Fact>]
let ``a witnessed import retargeted to a result contract or a runtime binding is refused`` () =
    let probe = nativeProbe ()
    // As for the static probe above, the library project carries no archive map.
    File.WriteAllLines(probe.Project, File.ReadAllLines probe.Project |> Array.filter (fun line -> not (line.StartsWith("archives =", StringComparison.Ordinal))))
    let manifest =
        request probe.Project (Some probe.Pilot) (Some probe.Provenance) probe.Work
        |> Substantiation.evaluate |> accepted
    let library, identity = "substantiation_probe", "substantiation_identity"
    let correspondence = Substantiation.validateManifestCorrespondence manifest |> accepted
    let declaration (qualified: string) = correspondence.Declarations |> Array.find (fun node -> node.Qualified = qualified)
    let foreign = declaration ("Round19.Probe.Boundary." + identity)
    let descriptor = declaration ("Round19.Probe.Boundary." + identity + "Descriptor")
    let contract = declaration "Round19.Probe.Boundary.identityResultContract"
    let runtime = declaration "Round19.Probe.Scalar.seven"
    Assert.Equal(Some(library, identity), foreign.Foreign)
    Assert.Equal(Substantiation.DescriptorSymbol.Named identity, descriptor.Descriptor)
    // The result contract and the runtime function are library bindings that declare no
    // foreign symbol: neither a FidelityExtern declaration nor a FunctionDescriptor.
    for binding in [ contract; runtime ] do
        Assert.Equal(None, binding.Foreign)
        Assert.Equal(Substantiation.DescriptorSymbol.NotDescriptor, binding.Descriptor)
    // The result contract is a compile-time declaration of the boundary inventory, and the
    // import's library and symbol stay those of the retained boundary. Only the comparison
    // with the declaration that the import's node resolves to can refuse the retargeting.
    let exported (select: Substantiation.SourceDigest -> string[]) = manifest.Sources |> Array.collect select
    Assert.Contains(contract.Qualified, exported (fun source -> source.CompileTimeDeclarations))
    Assert.Contains(runtime.Qualified, exported (fun source -> source.RuntimeDeclarations))
    let alteredWitness expected (alter: JsonValue -> JsonValue) =
        changedBytes manifest.WitnessUnits.Path (fun bytes ->
            let json = Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('﻿')) |> alter
            Encoding.UTF8.GetBytes(Json.serialize json)) (fun () ->
                let result =
                    { manifest with WitnessUnits = { manifest.WitnessUnits with Sha256 = ClangToolchain.sha256 manifest.WitnessUnits.Path } }
                    |> Substantiation.seal |> Substantiation.validateManifest
                if expected = "" then accepted result else refused expected result)
    let retarget (node: int) (json: JsonValue) =
        jUpdate "units" (fun units ->
            match units with
            | JsonValue.Array unitItems ->
                unitItems
                |> List.map (jUpdate "imports" (fun imps ->
                    match imps with
                    | JsonValue.Array impItems ->
                        impItems
                        |> List.map (fun imp ->
                            if jStr "symbol" imp = identity then
                                jUpdate "sourceBoundary" (jSet "declaration" (Json.ofInt64 (int64 node))) imp
                            else imp)
                        |> JsonValue.Array
                    | other -> other))
                |> JsonValue.Array
            | other -> other) json
    let appendResource (node: int) (json: JsonValue) =
        let resource =
            JsonValue.Object
                [ "sourceBoundary", Json.ofInt64 (int64 node)
                  "library", JsonValue.String library
                  "symbol", JsonValue.String identity ]
        jUpdate "units" (jUpdateAt 0 (jUpdate "resourceImports" (jAdd resource))) json
    // A descendant of the result contract binding resolves to that binding by the parent walk.
    let retainedPsg = Json.parseOrFail(File.ReadAllText(manifest.Psg0.Path).TrimStart('﻿'))
    let contractChild =
        retainedPsg
        |> jProp "nodes"
        |> Json.items
        |> List.find (fun node ->
            match node |> Json.prop "parent" |> Option.bind Json.asInt with
            | None -> false
            | Some parent -> int parent = contract.BindingNodeId)
        |> jInt "id"
    // Controls: the unaltered witness, and the import on each of its two owning bindings.
    alteredWitness "" id
    alteredWitness "" (retarget descriptor.BindingNodeId)
    alteredWitness "" (retarget foreign.BindingNodeId)
    alteredWitness "" (appendResource descriptor.BindingNodeId)
    // The same import, with its library and symbol unchanged, on a binding that owns none.
    alteredWitness "FS9243" (retarget contract.BindingNodeId)
    alteredWitness "FS9243" (retarget contractChild)
    alteredWitness "FS9243" (retarget runtime.BindingNodeId)
    alteredWitness "FS9243" (appendResource contract.BindingNodeId)
    alteredWitness "FS9243" (appendResource runtime.BindingNodeId)
    Substantiation.validateManifest manifest |> accepted

[<Theory>]
[<InlineData(true, true)>]
[<InlineData(false, false)>]
[<InlineData(true, false)>]
let ``explicit warning relaxation retains custom elevations through the native CLI`` allowWarnings projectWarningsAsErrors =
    let work = scratch "warning-policy-cli"
    let source = Path.Combine(work, "Warnings.clef")
    File.WriteAllText(source, "module Round19.Warnings\nlet seven () : int = 7\n")
    let project = writeProject work [ source ] None
    let blanket = if projectWarningsAsErrors then "true" else "false"
    File.AppendAllText(project,
        "\n[substantiation]\nwarnings_as_errors = " + blanket
        + "\nwarn_as_error = [\"CCS8777\", \"CCS8500\", \"CCS8777\"]\n")
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let output = Path.Combine(work, "manifest")
    let launch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "substantiate"; "--fidproj"; project; "--output"; output
              "--work-dir"; Path.Combine(work, "substantiation")
              if allowWarnings then "--allow-warnings" ]
    launch.Environment["COMPOSER_DLL"] <- sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll"
    let argumentsPath = recordComposerArguments work launch
    run work "warning-policy-cli" launch |> ignore
    let manifest =
        File.ReadAllText(Path.Combine(output, "SubstantiationProbe.substantiation.json"))
        |> Substantiation.parseJson |> accepted
    Assert.Equal(allowWarnings, manifest.AllowWarnings)
    Assert.False(manifest.WarningsAsErrors)
    Assert.Equal<string[]>([| "CCS8500"; "CCS8777" |], manifest.ElevatedWarningCodes)
    let arguments = File.ReadAllLines argumentsPath
    Assert.Contains("--no-color", arguments)
    Assert.DoesNotContain("--warn-as-error", arguments)
    Substantiation.validateManifest manifest |> accepted
    for code in [ "CCS8999"; "CCS8500"; "CCS8777" ] do
        changedBytes manifest.CompilerLog.Path (fun bytes ->
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString bytes + $"\nWarnings.clef:2: warning {code}: retained advisory\n")) (fun () ->
                let resealed =
                    { manifest with CompilerLog = { manifest.CompilerLog with Sha256 = ClangToolchain.sha256 manifest.CompilerLog.Path } }
                    |> Substantiation.seal
                if code = "CCS8999" then Substantiation.validateManifest resealed |> accepted
                else Substantiation.validateManifest resealed |> refused "FS9235")
    { manifest with ElevatedWarningCodes = [| "CCS8500" |] }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    { manifest with WarningsAsErrors = true }
    |> Substantiation.seal |> Substantiation.validateManifest |> refused "FS9236"
    Substantiation.validateManifest manifest |> accepted

// =====================================================================
// #228 controls for the explicit type-directed Fidelity.Data codecs.
// =====================================================================

let private codecStrata : SubstantiationBoundary.BoundaryStrata =
    { ForeignCalls = false; MeasuredRecords = false; StaticArchive = false; ErrorConventions = false;
      Ownership = false; ZeroCopy = false; Callbacks = false; Nullability = false }

let private codecProof : SubstantiationProof.ProofSummary =
    { LibraryObligations = 1; LibraryDischargedUnsatCount = 1; LibraryPremisesSatCount = 1;
      HarnessObligations = 0; TotalObligations = 1; DischargedUnsatCount = 1; PendingOpenCount = 0;
      RefutedCount = 0; PremisesSatCount = 1; ObligationsSha256 = "aa"; QuerySha256 = "bb";
      ReceiptSha256 = "cc"; PremisesQuerySha256 = "dd"; PremisesOutputSha256 = "ee";
      CompilerPremiseReceiptsCount = 1; CompilerNonVacuityReceiptsCount = 1;
      StageReceipts = [| { Path = "stage.json"; Sha256 = "ff" } |];
      Solver = { Executable = "cvc5"; Sha256 = "gg"; Version = "2.11.0"; Arguments = [| "--mode=strict" |] } }

let private codecBoundary : SubstantiationBoundary.BoundaryEvidence =
    let hypotheses, seamChecks = SubstantiationBoundary.deriveContract "<codec-fixture>" codecStrata
    { Strata = codecStrata; Files = [||]; LinkMode = "none"; Libraries = [||]; ExternSymbols = [||];
      ClangIdentity = "not-applicable"; ClangTarget = "not-measured"; TargetDataModel = "not-measured";
      Layouts = [||]; Enums = [||]; RestsOn = hypotheses; RequiredSeamChecks = seamChecks }

let private codecManifestBase : Substantiation.Manifest =
    { SchemaVersion = 2;
      Package = "CodecProbe";
      FidprojPath = "<codec-fixture>/CodecProbe.fidproj";
      FidprojSha256 = "11";
      Sources = [| { Path = "CodecProbe.clef"; Sha256 = "22"; ModuleName = "CodecProbe";
                     RuntimeDeclarations = [| "CodecProbe.run" |]; CompileTimeDeclarations = [| |]; ForeignDeclarations = [| |] } |];
      Compiler = { Path = "<codec-fixture>/compiler"; Sha256 = "33" };
      Dotnet = { Path = "<codec-fixture>/dotnet"; Sha256 = "44" };
      Harness = { Path = "<codec-fixture>/harness.clef"; Sha256 = "55" };
      HarnessProject = { Path = "<codec-fixture>/harness.fidproj"; Sha256 = "66" };
      SourceInputsSha256 = String('a', 64);
      CompilerLog = { Path = "<codec-fixture>/compiler.log"; Sha256 = "77" };
      Psg0 = { Path = "<codec-fixture>/artifacts/01_psg0.json"; Sha256 = "88" };
      WitnessUnits = { Path = "<codec-fixture>/artifacts/10_witness_units.json"; Sha256 = "99" };
      AllowWarnings = false;
      WarningsAsErrors = true;
      ElevatedWarningCodes = [| "CCS8500" |];
      ArtifactsDirectory = "<codec-fixture>/artifacts";
      Proof = codecProof;
      Boundary = codecBoundary;
      IntegritySha256 = "" }

let private codecManifest = Substantiation.seal codecManifestBase

let private codecKeys =
    [ "SchemaVersion"; "Package"; "FidprojPath"; "FidprojSha256"; "Sources"; "Compiler"; "Dotnet"; "Harness";
      "HarnessProject"; "SourceInputsSha256"; "CompilerLog"; "Psg0"; "WitnessUnits"; "AllowWarnings";
      "WarningsAsErrors"; "ElevatedWarningCodes"; "ArtifactsDirectory"; "Proof"; "Boundary"; "IntegritySha256" ]

let private refusalText (refusal: Diagnostics.Refusal) =
    refusal.Diagnostics |> List.map (fun diagnostic -> diagnostic.Message) |> String.concat " "

[<Fact>]
let ``explicit codec roundtrip preserves the sealed manifest and its checked source identity`` () =
    let manifest = codecManifest
    let json = Substantiation.toJson manifest
    let decoded = Substantiation.parseJson json |> accepted
    Assert.Equal(manifest, decoded)
    Assert.Equal(manifest.SourceInputsSha256, decoded.SourceInputsSha256)
    Assert.Equal(manifest.IntegritySha256, decoded.IntegritySha256)
    Assert.Equal(json, Substantiation.toJson decoded)
    let positions = codecKeys |> List.map (fun key -> json.IndexOf("\"" + key + "\":"))
    Assert.True(positions |> List.forall ((<) 0), "a published manifest key is missing")
    Assert.Equal(codecKeys.Length, (positions |> List.distinct |> List.length))
    Assert.True(positions |> List.pairwise |> List.forall (fun (a, b) -> a < b), "published manifest keys are out of order")

[<Fact>]
let ``sealed manifests are byte identical across repeated seals`` () =
    let manifest = codecManifest
    Assert.Equal(Substantiation.toJson manifest, Substantiation.toJson (Substantiation.seal manifest))

[<Fact>]
let ``null string fields are refused at encoding instead of publishing null JSON`` () =
    let nullManifest = { codecManifest with Package = Unchecked.defaultof<string> }
    let ex = Assert.Throws<Diagnostics.RefusalException>(fun () -> Substantiation.toJson nullManifest |> ignore)
    Assert.Contains("admits no null", ex.Message)

[<Fact>]
let ``malformed manifest JSON refuses with FS9236`` () =
    [ ""; "{"; "{\"SchemaVersion\": 2"; "[1,2"; "null" ]
    |> List.iter (fun text -> Substantiation.parseJson text |> refused "FS9236")

[<Fact>]
let ``a duplicate key in the top level manifest object refuses with FS9236`` () =
    let json = Substantiation.toJson codecManifest
    let duplicated = json.Replace("\"Package\":", "\"Package\": \"CodecProbe\", \"Package\":")
    match Substantiation.parseJson duplicated with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("repeats field 'Package'", refusalText refusal)
    | Ok _ -> failwith "a manifest with a duplicate top-level key was accepted"

[<Fact>]
let ``a duplicate key inside a nested source digest refuses with FS9236`` () =
    let json = Substantiation.toJson codecManifest
    let duplicated = json.Replace("\"ModuleName\":", "\"Sha256\": \"22\", \"ModuleName\":")
    match Substantiation.parseJson duplicated with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("repeats field 'Sha256'", refusalText refusal)
    | Ok _ -> failwith "a nested digest with a duplicate key was accepted"

[<Fact>]
let ``an unknown manifest key refuses with FS9236`` () =
    let json = (Substantiation.toJson codecManifest).Replace("\"Package\":", "\"UnknownField\": 1, \"Package\":")
    match Substantiation.parseJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("field 'UnknownField' outside its schema", refusalText refusal)
    | Ok _ -> failwith "a manifest with an unknown key was accepted"

[<Fact>]
let ``a missing required manifest field refuses with FS9236`` () =
    let json = System.Text.RegularExpressions.Regex.Replace(Substantiation.toJson codecManifest, "\"FidprojSha256\": \"11\",\n", "")
    match Substantiation.parseJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("missing required field 'FidprojSha256'", refusalText refusal)
    | Ok _ -> failwith "a manifest missing FidprojSha256 was accepted"

[<Fact>]
let ``a manifest field of the wrong JSON type refuses with FS9236 and names the field`` () =
    let json = (Substantiation.toJson codecManifest).Replace("\"AllowWarnings\": false", "\"AllowWarnings\": \"false\"")
    match Substantiation.parseJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("Field 'AllowWarnings'", refusalText refusal)
        Assert.Contains("Expected a boolean", refusalText refusal)
    | Ok _ -> failwith "a manifest whose AllowWarnings is a string was accepted"

[<Fact>]
let ``one substituted manifest field fails at the integrity validation`` () =
    let json = (Substantiation.toJson codecManifest).Replace("\"TotalObligations\": 1", "\"TotalObligations\": 2")
    match Substantiation.parseJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("integrity digest does not match", refusalText refusal)
    | Ok _ -> failwith "a one-field substitution of a sealed manifest was accepted"

[<Fact>]
let ``an omitted checked source-input receipt is admitted only as an archive and refused by validation`` () =
    let legacy = { codecManifest with SchemaVersion = 1; SourceInputsSha256 = "" } |> Substantiation.seal
    let archived = Substantiation.toJson legacy |> Substantiation.parseJson |> accepted
    Assert.Equal("", archived.SourceInputsSha256)
    match Substantiation.validateManifest archived with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9237" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("requires a sealed checked source-input receipt", refusalText refusal)
    | Ok _ -> failwith "an archive-only manifest was admitted as current proof evidence"

[<Fact>]
let ``a schema 1 document rebound with a checked source digest refuses with FS9236`` () =
    let json = (Substantiation.toJson codecManifest).Replace("\"SchemaVersion\": 2", "\"SchemaVersion\": 1")
    match Substantiation.parseJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("schema 1 does not admit a source-input digest", refusalText refusal)
    | Ok _ -> failwith "a schema 1 document carrying a checked source-input digest was accepted"

[<Fact>]
let ``a real library manifest refuses omitted and rebound schema 2 checked source-input receipts`` () =
    let probe = nativeProbe ()
    let manifest = request probe.Project (Some probe.Pilot) (Some probe.Provenance) probe.Work |> Substantiation.evaluate |> accepted
    // Positive: exact JSON round-trip preserves bytes and checked-source identity.
    let published = Substantiation.toJson manifest
    let reparsed = Substantiation.parseJson published |> accepted
    Assert.Equal(manifest, reparsed)
    Assert.Equal(published, Substantiation.toJson reparsed)
    Substantiation.validateManifest reparsed |> accepted
    // Exact-reason negative 1: schema-2 manifest with omitted (empty) SourceInputsSha256 refuses at validation.
    let omitted = { manifest with SourceInputsSha256 = "" } |> Substantiation.seal
    match Substantiation.validateManifest omitted with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("The source input digest is invalid", refusalText refusal)
    | Ok _ -> failwith "a schema 2 manifest with empty SourceInputsSha256 was accepted"
    // Exact-reason negative 2: resealed schema-2 manifest rebound to a different SourceInputsSha256 refuses at checked-source validation.
    let reboundDigest =
        if manifest.SourceInputsSha256.StartsWith("0", StringComparison.Ordinal) then String('1', 64) else String('0', 64)
    let rebound = { manifest with SourceInputsSha256 = reboundDigest } |> Substantiation.seal
    let reboundParsed = Substantiation.toJson rebound |> Substantiation.parseJson |> accepted
    match Substantiation.validateManifest reboundParsed with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9234" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("checked source-input receipt SHA-256 has changed", refusalText refusal)
    | Ok _ -> failwith "a schema 2 manifest rebound to a wrong checked source-input digest was accepted"

[<Fact>]
let ``a duplicate key inside a boundary layout object refuses with FS9236`` () =
    let layoutBoundary =
        { codecBoundary with Layouts = [| { Header = "probe.h"; Name = "Pair"; Kind = "struct"; SizeBits = 64; AlignmentBits = 32; FieldOffsetsBits = [| 0; 32 |] } |] }
    let withLayout = { codecManifest with Boundary = layoutBoundary } |> Substantiation.seal |> Substantiation.toJson
    let duplicated = withLayout.Replace("\"Kind\": \"struct\",", "\"Kind\": \"struct\", \"Kind\": \"struct\",")
    match Substantiation.parseJson duplicated with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9236" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("repeats field 'Kind'", refusalText refusal)
    | Ok _ -> failwith "a nested layout object with a duplicate key was accepted"

let private codecRocqEntry : SubstantiationDossier.DossierRocqEntry =
    { Symbol = "add_comm"; Source = "CodecProbe.v"; Keyword = "Theorem"; Group = "Falsified";
      Summary = "addition commutes"; Proposition = "forall x y, add x y = add y x";
      BinderNotes = [| { Name = "x"; Note = "first operand" } |]; Assumptions = "closed";
      Axioms = [||]; TrustScope = "library"; CorrespondsTo = [| "add_comm" |] }

let private codecDossier : SubstantiationDossier.Dossier =
    { SchemaVersion = 1;
      Package = "CodecProbe";
      Title = "CodecProbe substantiation dossier";
      OverallStatus = "SUBSTANTIATED";
      ManifestPath = "<codec-fixture>/substantiation.json";
      ManifestIntegritySha256 = codecManifest.IntegritySha256;
      Counts = { Open = 0; Falsified = 1; Checks = 1; Guarantees = 1; PendingSeamCategories = 0 };
      Legend = [| { Section = "Open"; Health = "none"; Meaning = "no obligation is open" } |];
      Open = { CompilerOpenObligations = 0; CompilerRefutedObligations = 0; RocqOpen = [||] };
      Falsified =
        { CompilerNonVacuityReceipts = 1; RefusalControlsAttached = true;
          RefusalControls = [| { Id = "errno-1"; ExpectedCode = "FS9241"; ObservedCode = "FS9241"; Passed = true;
                                MisuseSummary = "unclassified foreign call"; ProtectionSummary = "refused before output" } |];
          RocqFalsified = [| codecRocqEntry |] };
      Checks =
        { PremisesSat = 1; TotalObligations = 1; LibraryPremisesSat = 1; LibraryObligations = 1;
          CompilerPremiseReceipts = 1; SeamChecksAttached = true; SeamTargetTriple = "x86_64-linux-gnu";
          SeamChecks = [| { Id = "callback-1"; Category = "callbacks"; Summary = "entry arity";
                           BinarySha256 = "cc"; Passed = true; Command = "seam-callbacks" } |];
          SeamCategories = [| { Category = "callbacks"; Description = "callback ABI"; Status = "SUBSTANTIATED"; Evidence = [| "witness.json" |] } |];
          RocqChecks = [| codecRocqEntry |] };
      Guarantees =
        { TrustScope = "library"; HarnessObligations = 0; RocqGuarantees = [| codecRocqEntry |];
          LibraryObligations = [| { Declaration = "add"; ModuleLevel = false; Anchor = "add";
                                   Source = "CodecProbe.clef"; Kind = "runtime"; Guarantee = "add_comm";
                                   TrustScope = "library" } |] };
      Assumptions = { Hypotheses = [| { Id = "H-ABI-1"; Family = "H-ABI"; Category = "callbacks";
                                       Origin = "boundary"; Statement = "callbacks are ABI conformant";
                                       TestedBy = [| "callback-1" |] } |]; RocqAxioms = [||] };
      Traceability =
        { Declarations = [| { Declaration = "add"; Kind = "runtime"; Source = "CodecProbe.clef"; PsgNode = 1;
                             Obligations = [| "add_comm" |]; MlirSymbols = [| "@add" |]; ForeignLinks = [||];
                             MeasuredTypes = [||]; RocqTheorems = [| "add_comm" |] } |];
          Layouts = [| { Header = "probe.h"; Name = "Pair"; Kind = "struct"; SizeBits = 64;
                        AlignmentBits = 32; FieldOffsetsBits = [| 0; 32 |] } |];
          Enums = [| { Header = "probe.h"; Name = "Mode"; WidthBits = 32; Signed = true } |];
          Digests = [| { Item = "psg0"; Path = "<codec-fixture>/artifacts/01_psg0.json"; Value = "88" } |];
          RocqAttached = true; RocqVersion = "8.6";
          RocqUnits = [| { Module = "CodecProbe"; Path = "<codec-fixture>/CodecProbe.v"; SourceMd5 = "aa"; GlobMd5 = "bb" } |] } }

[<Fact>]
let ``dossier documents round trip through the explicit codec with unchanged bytes`` () =
    let text = SubstantiationDossier.toJson codecDossier
    let decoded = SubstantiationDossier.parseDossierJson text |> accepted
    Assert.Equal(codecDossier, decoded)
    Assert.Equal(text, SubstantiationDossier.toJson decoded)
    let nullDossier = { codecDossier with Title = Unchecked.defaultof<string> }
    let ex = Assert.Throws<Diagnostics.RefusalException>(fun () -> SubstantiationDossier.toJson nullDossier |> ignore)
    Assert.Contains("admits no null", ex.Message)

[<Fact>]
let ``a duplicate key inside a dossier seam category refuses with FS9247`` () =
    let duplicated = (SubstantiationDossier.toJson codecDossier).Replace("\"Status\": \"SUBSTANTIATED\",", "\"Status\": \"SUBSTANTIATED\", \"Status\": \"SUBSTANTIATED\",")
    match SubstantiationDossier.parseDossierJson duplicated with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9247" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("repeats field 'Status'", refusalText refusal)
    | Ok _ -> failwith "duplicate dossier seam category key was accepted"

[<Fact>]
let ``an unknown dossier key refuses with FS9247`` () =
    let json = (SubstantiationDossier.toJson codecDossier).Replace("\"Title\":", "\"UnknownField\": 1, \"Title\":")
    match SubstantiationDossier.parseDossierJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9247" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("field 'UnknownField' outside its schema", refusalText refusal)
    | Ok _ -> failwith "unknown dossier key was accepted"

[<Fact>]
let ``a missing dossier field refuses with FS9247`` () =
    let json = System.Text.RegularExpressions.Regex.Replace(SubstantiationDossier.toJson codecDossier, "\"RocqVersion\": \"8.6\",\n", "")
    match SubstantiationDossier.parseDossierJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9247" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("missing required field 'RocqVersion'", refusalText refusal)
    | Ok _ -> failwith "missing dossier field was accepted"

[<Fact>]
let ``a dossier field of the wrong JSON type refuses with FS9247 and names the field`` () =
    let json = (SubstantiationDossier.toJson codecDossier).Replace("\"RocqAttached\": true", "\"RocqAttached\": \"yes\"")
    match SubstantiationDossier.parseDossierJson json with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9247" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("Field 'RocqAttached'", refusalText refusal)
        Assert.Contains("Expected a boolean", refusalText refusal)
    | Ok _ -> failwith "wrong-type dossier field was accepted"

// =====================================================================
// #228 controls for the explicit SubstantiationSeams receipt codecs.
// =====================================================================

let private codecSeamChecksReceipt : SubstantiationSeams.SeamChecksReceipt =
    { SchemaVersion = 1
      Package = "CodecProbe"
      ManifestIntegritySha256 = codecManifest.IntegritySha256
      TargetTriple = "x86_64-linux-gnu"
      Checks =
        [| { Id = "seam-scalar-1"
             Category = "scalar-handle-link-and-call"
             Summary = "Scalar probe linked statically"
             BinarySha256 = String('b', 64)
             Passed = true
             Command = "<codec-fixture>/probe-scalar" } |] }

let private codecRefusalControlsReceipt : SubstantiationSeams.RefusalControlsReceipt =
    { SchemaVersion = 1
      Package = "CodecProbe"
      ManifestIntegritySha256 = codecManifest.IntegritySha256
      Controls =
        [| { Id = "unlinked-extern"
             ExpectedCode = "FS9228"
             ObservedCode = "FS9228"
             MisuseSummary = "Unlinked extern symbol"
             ProtectionSummary = "Refused before output"
             Passed = true } |] }

let private codecSeamsLedgerReceipt (seamJson: string) (refusalJson: string) : SubstantiationSeams.SeamsLedgerReceipt =
    let sha text = SHA256.HashData(Encoding.UTF8.GetBytes(text: string)) |> Convert.ToHexStringLower
    { Schema = SubstantiationSeams.LedgerSchema
      SchemaVersion = 1
      SeamChecksSchema = SubstantiationSeams.SeamChecksSchema
      RefusalControlsSchema = SubstantiationSeams.RefusalControlsSchema
      Package = "CodecProbe"
      ManifestIntegritySha256 = codecManifest.IntegritySha256
      TargetTriple = "x86_64-linux-gnu"
      RequiredSeamCategories = [| "scalar-handle-link-and-call" |]
      VerifiedPhysicalCategories = [| "scalar-handle-link-and-call" |]
      Discharge =
        { ReceiptPath = "<codec-fixture>/receipt.txt"
          ReceiptSha256 = String('c', 64)
          PremisesOutputPath = "<codec-fixture>/premises.txt"
          PremisesOutputSha256 = String('d', 64)
          TotalObligations = 1
          DischargedUnsatCount = 1
          PremisesSatCount = 1
          LibraryObligations = 1
          LibraryDischargedUnsatCount = 1
          LibraryPremisesSatCount = 1
          PendingOpenCount = 0
          RefutedCount = 0 }
      RetainedBinaries =
        [| { CheckId = "seam-scalar-1"
             BinaryPath = "<codec-fixture>/probe-scalar"
             BinarySha256 = String('b', 64) } |]
      SharedLibraries =
        [| { CheckId = "seam-link-1"
             SharedLibraryPath = "<codec-fixture>/libprobe.so"
             SharedLibrarySha256 = String('e', 64)
             DynamicProbeBinaryPath = "<codec-fixture>/dyn-probe"
             DynamicProbeBinarySha256 = String('f', 64) } |]
      SeamChecksPath = "<codec-fixture>/seams.json"
      SeamChecksSha256 = sha seamJson
      RefusalControlsPath = "<codec-fixture>/refusals.json"
      RefusalControlsSha256 = sha refusalJson }

[<Fact>]
let ``seam check, refusal control, and ledger receipts round trip through explicit codecs with exact key order and null refusal`` () =
    let assertOrdered (json: string) (keys: string list) =
        let positions = keys |> List.map (fun key -> json.IndexOf("\"" + key + "\":"))
        Assert.True(positions |> List.forall ((<) 0), "a published seam receipt key is missing")
        Assert.True(positions |> List.pairwise |> List.forall (fun (a, b) -> a < b), "published seam receipt keys are out of order")

    let seamJson = SubstantiationSeams.encodeSeamChecksReceipt codecSeamChecksReceipt |> Substantiation.prettyJson
    assertOrdered seamJson [ "SchemaVersion"; "Package"; "ManifestIntegritySha256"; "TargetTriple"; "Checks" ]
    match Substantiation.decodeJsonText seamJson SubstantiationSeams.decodeSeamChecksReceipt with
    | Ok decoded ->
        Assert.Equal(codecSeamChecksReceipt, decoded)
        Assert.Equal(seamJson, SubstantiationSeams.encodeSeamChecksReceipt decoded |> Substantiation.prettyJson)
    | Error reason -> failwith reason

    let refusalJson = SubstantiationSeams.encodeRefusalControlsReceipt codecRefusalControlsReceipt |> Substantiation.prettyJson
    assertOrdered refusalJson [ "SchemaVersion"; "Package"; "ManifestIntegritySha256"; "Controls" ]
    match Substantiation.decodeJsonText refusalJson SubstantiationSeams.decodeRefusalControlsReceipt with
    | Ok decoded ->
        Assert.Equal(codecRefusalControlsReceipt, decoded)
        Assert.Equal(refusalJson, SubstantiationSeams.encodeRefusalControlsReceipt decoded |> Substantiation.prettyJson)
    | Error reason -> failwith reason

    let ledger = codecSeamsLedgerReceipt seamJson refusalJson
    let ledgerJson = SubstantiationSeams.encodeSeamsLedgerReceipt ledger |> Substantiation.prettyJson
    assertOrdered ledgerJson
        [ "Schema"; "SchemaVersion"; "SeamChecksSchema"; "RefusalControlsSchema"; "Package"; "ManifestIntegritySha256"
          "TargetTriple"; "RequiredSeamCategories"; "VerifiedPhysicalCategories"; "Discharge"; "RetainedBinaries"
          "SharedLibraries"; "SeamChecksPath"; "SeamChecksSha256"; "RefusalControlsPath"; "RefusalControlsSha256" ]
    match Substantiation.decodeJsonText ledgerJson SubstantiationSeams.decodeSeamsLedgerReceipt with
    | Ok decoded ->
        Assert.Equal(ledger, decoded)
        Assert.Equal(ledgerJson, SubstantiationSeams.encodeSeamsLedgerReceipt decoded |> Substantiation.prettyJson)
    | Error reason -> failwith reason

    // Fail-closed null refusal across seam encoders.
    let nullSeam = { codecSeamChecksReceipt with Package = Unchecked.defaultof<string> }
    let ex1 = Assert.Throws<Diagnostics.RefusalException>(fun () -> SubstantiationSeams.encodeSeamChecksReceipt nullSeam |> ignore)
    Assert.Contains("admits no null", ex1.Message)
    let nullRefusal = { codecRefusalControlsReceipt with Package = Unchecked.defaultof<string> }
    let ex2 = Assert.Throws<Diagnostics.RefusalException>(fun () -> SubstantiationSeams.encodeRefusalControlsReceipt nullRefusal |> ignore)
    Assert.Contains("admits no null", ex2.Message)
    let nullLedger = { ledger with Schema = Unchecked.defaultof<string> }
    let ex3 = Assert.Throws<Diagnostics.RefusalException>(fun () -> SubstantiationSeams.encodeSeamsLedgerReceipt nullLedger |> ignore)
    Assert.Contains("admits no null", ex3.Message)

[<Fact>]
let ``seam receipt explicit codecs refuse duplicate, unknown, missing, and wrong-type JSON fields`` () =
    let seamJson = SubstantiationSeams.encodeSeamChecksReceipt codecSeamChecksReceipt |> Substantiation.prettyJson
    let refusalJson = SubstantiationSeams.encodeRefusalControlsReceipt codecRefusalControlsReceipt |> Substantiation.prettyJson
    let ledger = codecSeamsLedgerReceipt seamJson refusalJson
    let ledgerJson = SubstantiationSeams.encodeSeamsLedgerReceipt ledger |> Substantiation.prettyJson

    // Duplicate key in seam check entry
    match Substantiation.decodeJsonText (seamJson.Replace("\"Passed\": true,", "\"Passed\": true, \"Passed\": true,")) SubstantiationSeams.decodeSeamChecksReceipt with
    | Error reason -> Assert.Contains("repeats field 'Passed'", reason)
    | Ok _ -> failwith "duplicate seam check key was accepted"

    // Unknown key in refusal controls receipt
    match Substantiation.decodeJsonText (refusalJson.Replace("\"Package\":", "\"ExtraField\": 1, \"Package\":")) SubstantiationSeams.decodeRefusalControlsReceipt with
    | Error reason -> Assert.Contains("field 'ExtraField' outside its schema", reason)
    | Ok _ -> failwith "unknown refusal controls key was accepted"

    // Missing required field in ledger Discharge summary
    let missingField = System.Text.RegularExpressions.Regex.Replace(ledgerJson, "\"PendingOpenCount\": 0,\n", "")
    match Substantiation.decodeJsonText missingField SubstantiationSeams.decodeSeamsLedgerReceipt with
    | Error reason -> Assert.Contains("missing required field 'PendingOpenCount'", reason)
    | Ok _ -> failwith "ledger missing PendingOpenCount was accepted"

    // Wrong JSON type names the failing field
    match Substantiation.decodeJsonText (ledgerJson.Replace("\"TotalObligations\": 1", "\"TotalObligations\": \"one\"")) SubstantiationSeams.decodeSeamsLedgerReceipt with
    | Error reason ->
        Assert.Contains("Field 'TotalObligations'", reason)
        Assert.Contains("Expected a 32-bit integer.", reason)
    | Ok _ -> failwith "ledger with string TotalObligations was accepted"

    // File-level strict readers refuse duplicate keys with exact diagnostic codes (FS9247 / FS9253)
    let work = scratch "seam-receipt-codecs"
    let badSeamPath = Path.Combine(work, "bad-seams.json")
    File.WriteAllText(badSeamPath, seamJson.Replace("\"TargetTriple\":", "\"TargetTriple\": \"x86_64-linux-gnu\", \"TargetTriple\":"))
    match SubstantiationSeams.readSeamReceipt codecManifest badSeamPath with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9247" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("repeats field 'TargetTriple'", refusalText refusal)
    | Ok _ -> failwith "readSeamReceipt accepted duplicate key"

    let badRefusalPath = Path.Combine(work, "bad-refusals.json")
    File.WriteAllText(badRefusalPath, refusalJson.Replace("\"Package\":", "\"UnknownKey\": 1, \"Package\":"))
    match SubstantiationSeams.readRefusalReceipt codecManifest badRefusalPath with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9247" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("field 'UnknownKey' outside its schema", refusalText refusal)
    | Ok _ -> failwith "readRefusalReceipt accepted unknown key"

    let badLedgerPath = Path.Combine(work, "bad-ledger.json")
    File.WriteAllText(badLedgerPath, ledgerJson.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": \"1\""))
    match SubstantiationSeams.readLedgerReceipt codecManifest badLedgerPath with
    | Error refusal ->
        Assert.Equal<string list>([ "FS9253" ], refusal.Diagnostics |> List.map _.Code |> List.distinct)
        Assert.Contains("Field 'SchemaVersion'", refusalText refusal)
    | Ok _ -> failwith "readLedgerReceipt accepted string SchemaVersion"
