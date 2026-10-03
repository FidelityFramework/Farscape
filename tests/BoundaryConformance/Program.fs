module BoundaryConformance

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Farscape.Core
open Clef.Compiler.Project
open Clef.Compiler.NativeTypedTree.NativeTypes
open Clef.Compiler.NativeTypedTree.UnionFind
open Clef.Compiler.PSGSaturation.SemanticGraph.Types
module Diagnostics = Clef.Compiler.PSGSaturation.SemanticGraph.Diagnostics
module Boundary = Clef.Compiler.PSGSaturation.SemanticGraph.BoundaryEmission
module Records = Clef.Compiler.PSGSaturation.SemanticGraph.RecordInstances
module Abi = Farscape.Core.Types

let require condition message = if not condition then invalidOp message
let need = function Ok value -> value | Error error -> invalidOp (string error)
let json (value: 'a) = JsonSerializer.Serialize<'a>(value)
let hash path = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString
let loadedAssemblies () =
    [System.Reflection.Assembly.GetExecutingAssembly().Location; typeof<ProjectCheckResult>.Assembly.Location; typeof<PilotTypes.PilotProject>.Assembly.Location]
    |> List.map (fun path -> {| Path = path; Sha256 = hash path |})

type Observation = { Case: string; Expected: string; Passed: bool; Detail: string }
type Inputs = { Work: string; Platform: string; Metadata: string; Header: string; Pilot: PilotTypes.PilotProject; Declarations: CppParser.Declaration list }

let retainDiagnostics directory (result: ProjectCheckResult) =
    let diagnostics =
        result.CheckResult.Diagnostics |> List.map (fun diagnostic ->
            sprintf "%s:%d:%d %s %A [%A]: %s" diagnostic.Range.File diagnostic.Range.Start.Line diagnostic.Range.Start.Column
                diagnostic.Code (Diagnostics.Diagnostic.effectiveSeverity diagnostic) diagnostic.Reachability diagnostic.Message)
    File.WriteAllLines(Path.Combine(directory, "diagnostics.txt"), diagnostics)
    File.WriteAllText(Path.Combine(directory, "parse-errors.txt"), sprintf "%A" result.ParseErrors)

let checkExistingProject path =
    let work = Path.Combine(Path.GetTempPath(), "farscape-source-project-check-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory work |> ignore
    printfn "Source project check evidence: %s" work
    let result = ProjectChecker.checkProject (Path.GetFullPath path) |> need
    retainDiagnostics work result
    let failures = ProjectChecker.getErrorMessages result
    let evidence =
        {| Project = result.Options.ProjectPath; SourceErrors = failures.Length; NativeExecution = "not attempted"
           Assemblies = loadedAssemblies ()
           Inputs = result.InputFiles |> List.map (fun (path, captured) ->
               {| Path = path; CapturedUtf8Sha256 = Encoding.UTF8.GetBytes(captured) |> SHA256.HashData |> Convert.ToHexString |}) |}
    File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(evidence, JsonSerializerOptions(WriteIndented = true)))
    if ProjectChecker.hasErrors result then
        for failure in failures do eprintfn "%s" failure
        eprintfn "Source project check failed with %d error(s); native execution not attempted. Evidence: %s" failures.Length work
        1
    else
        printfn "Source project check passed; native execution not attempted. Evidence: %s" work
        0

let generatedWith (inputs: Inputs) name library valueStructs =
    let context =
        { FidelityCodeGenerator.buildGenerationContext inputs.Declarations Abi.LP64 Map.empty with
            NonnullAnnotations = inputs.Pilot.Nonnull
            ValueStructs = valueStructs
            Bindings = inputs.Pilot.Options |> Option.map _.Bindings |> Option.defaultValue [] |> List.map (fun binding -> binding.Name, binding) |> Map.ofList }
    let source = FidelityCodeGenerator.generateModule context context.OpaqueHandles inputs.Declarations "Conformance.Native" library "Actual parsed boundary fixture" []
    let path = Path.Combine(inputs.Work, name, "Native.clef")
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, source)
    path

let generated inputs name library = generatedWith inputs name library Set.empty

let rejectedGeneration (inputs: Inputs) name (fragment: string) wrappers model (project: PilotTypes.PilotProject) =
    let directory = Path.Combine(inputs.Work, name)
    let output = Path.Combine(directory, "Bindings", "Rejected")
    Directory.CreateDirectory output |> ignore
    let sentinel = Path.Combine(output, "preserve-existing.clef")
    let contents = "// Existing consumer-owned source must survive a failed regeneration.\n"
    File.WriteAllText(sentinel, contents)
    let pilot = Path.Combine(directory, "rejected.pilot.toml")
    let project = { project with Output = { project.Output with Directory = output } }
    PilotSerializer.saveToFile pilot project |> need
    match BindingGenerator.generateFromProject pilot false wrappers model with
    | Error message ->
        File.WriteAllText(Path.Combine(directory, "generation-failure.txt"), message)
        require (message.Contains(fragment, StringComparison.Ordinal)) ("Unexpected generation failure: " + message)
    | Ok _ -> invalidOp "Unsupported pilot generated a source surface"
    require (File.ReadAllText sentinel = contents) "Failed regeneration changed existing binding source"
    require (Directory.GetFiles(output, "*", SearchOption.AllDirectories) = [|sentinel|]) "Failed regeneration wrote additional binding output"

let check (inputs: Inputs) name sources (main: string) =
    let directory = Path.Combine(inputs.Work, name)
    Directory.CreateDirectory directory |> ignore
    let entry = Path.Combine(directory, "Main.clef")
    File.WriteAllText(entry, main)
    let project = Path.Combine(directory, name + ".fidproj")
    File.WriteAllText(project,
        "[package]\nname = " + json name + "\nversion = \"0.1.0\"\n" +
        "[compilation]\ntarget = \"cpu\"\n[dependencies]\nplatform = { path = " + json inputs.Platform + " }\n" +
        "metadata = { path = " + json inputs.Metadata + " }\n[build]\noutput_kind = \"console\"\nsources = " + json (sources @ [entry]) + "\n")
    let result = ProjectChecker.checkProject project |> need
    retainDiagnostics directory result
    // Parser recovery, a missing dependency or an unrelated type error cannot
    // satisfy a negative source-admission gate.
    require result.ParseErrors.IsEmpty (sprintf "%s has parse failures: %A" name result.ParseErrors)
    result

let errors (result: ProjectCheckResult) =
    result.CheckResult.Diagnostics
    |> List.filter (fun diagnostic -> Diagnostics.Diagnostic.effectiveSeverity diagnostic = Diagnostics.NativeDiagnosticSeverity.Error)

let rejectWith code (fragment: string) result =
    let failures = errors result
    require (ProjectChecker.hasErrors result) "Unsupported source was silently admitted"
    require (failures |> List.exists (fun diagnostic -> diagnostic.Code = code && diagnostic.Message.Contains(fragment, StringComparison.Ordinal)))
        (sprintf "Missing owning %s diagnostic containing %A. Actual errors: %A" code fragment failures)
    match ProjectChecker.publish result with
    | Error _ -> ()
    | Ok _ -> invalidOp "A rejected project published a usable PSG revision"

let bindingType name path (result: ProjectCheckResult) =
    result.CheckResult.Graph.Nodes.Values
    |> Seq.filter (fun node -> node.Range.File = path && match node.Kind with SemanticKind.Binding(actual, _, _, _) -> actual = name | _ -> false)
    |> Seq.map (fun node -> applySubst node.Type) |> Seq.toList
    |> function [typ] -> typ | other -> invalidOp (sprintf "Expected one generated binding %s, got %A" name other)

let rec resultType = function
    | NativeType.TForall(_, body) -> resultType body
    | NativeType.TFun(_, range) -> resultType range
    | other -> other

let rec parameterTypes = function
    | NativeType.TForall(_, body) -> parameterTypes body
    | NativeType.TFun(domain, range) -> domain :: parameterTypes range
    | _ -> []

let constructor = function
    | NativeType.TApp(constructor, []) | NativeType.TUnion(constructor, _) -> Some constructor
    | _ -> None

let markerConstructor path (result: ProjectCheckResult) =
    result.CheckResult.Graph.Nodes.Values
    |> Seq.choose (fun node ->
        match node.Kind with
        | SemanticKind.TypeDef(_, TypeDefKind.UnionDef ["Opaque_Token", []], _) when node.Range.File = path -> constructor (applySubst node.Type)
        | _ -> None)
    |> Seq.toList
    |> function
       | [marker] -> marker
       | markers ->
           let definitions = result.CheckResult.Graph.Nodes.Values |> Seq.filter (fun node -> node.Range.File = path && match node.Kind with SemanticKind.TypeDef _ -> true | _ -> false) |> Seq.map _.Kind |> Seq.toList
           invalidOp (sprintf "Expected one generated Token marker definition, got %A; definitions: %A" markers definitions)

let handle marker = function
    | NativeType.TApp(constructor', [inner]) when constructor'.Name = "CHandle" ->
        constructor inner |> Option.exists (fun actual -> actual = marker && actual.FieldCount = 0 && actual.CaseCount = 1)
    | _ -> false

let nullableHandle marker = function
    | NativeType.TApp(constructor, [inner]) when constructor.Name = "option" -> handle marker inner
    | _ -> false

let rec rawType = function
    | NativeType.TNativePtr _ -> true
    | NativeType.TNum(CarrierRef.Carrier constructor, _) ->
        List.contains constructor.Name ["nativeint"; "unativeint"]
    | NativeType.TApp(constructor, args) ->
        List.contains constructor.Name ["nativeint"; "unativeint"; "nativeptr"] || List.exists rawType args
    | NativeType.TFun(domain, range) -> rawType domain || rawType range
    | NativeType.TForall(_, body) -> rawType body
    | NativeType.TTuple(elements, _) -> List.exists rawType elements
    | NativeType.TAnon(fields, _) -> fields |> List.exists (snd >> rawType)
    | _ -> false

let scalarMain = """module Conformance.Main
[<EntryPoint>]
let main _ = if Conformance.Native.abs (-17) = 17 then 0 else 1
"""

let prepare work platform metadata =
    let header = Path.Combine(work, "boundary.h")
    File.Copy(Path.Combine(__SOURCE_DIRECTORY__, "boundary.h"), header)
    let pilot =
        File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "boundary.pilot.toml"))
            .Replace("\"@HEADER@\"", json header)
            .Replace("\"@INCLUDE@\"", json work)
            .Replace("\"@OUTPUT@\"", json (Path.Combine(work, "pilot-generated", "Bindings", "Native")))
    let pilotPath = Path.Combine(work, "boundary.pilot.toml")
    File.WriteAllText(pilotPath, pilot)
    let project = PilotSerializer.loadFromFile pilotPath |> need
    let options: CppParser.HeaderParserOptions =
        { HeaderFile = header; IncludePaths = [work]; Defines = []; Verbose = false
          IncludeMacros = false; MacroPrefixes = []; IncludeRoot = Some work; CppMode = false }
    let declarations = CppParser.parseHeader options |> need
    require (declarations |> List.exists (function CppParser.Declaration.Function function' -> function'.Name = "abs" | _ -> false)) "clang did not parse the fixture scalar entry"
    { Work = work; Platform = platform; Metadata = metadata; Header = header; Pilot = project; Declarations = declarations }

let run inputs =
    let observations = ResizeArray<Observation>()
    let gate name expected body =
        try
            body ()
            observations.Add { Case = name; Expected = expected; Passed = true; Detail = "" }
            printfn "PASS %s (%s)" name expected
        with error ->
            observations.Add { Case = name; Expected = expected; Passed = false; Detail = error.ToString() }
            eprintfn "FAIL %s: %s" name error.Message

    gate "generated-libc-scalar" "admitted source and published scalar call" (fun () ->
        let native = generated inputs "generated-libc-scalar" "c"
        let result = check inputs "generated-libc-scalar" [native] scalarMain
        require (not (ProjectChecker.hasErrors result)) (String.concat "\n" (ProjectChecker.getErrorMessages result))
        let boundary = Boundary.project result.CheckResult.Graph |> need
        let imports = boundary.Imports.Values |> Seq.filter (fun import -> import.Symbol = "abs") |> Seq.toList
        let imported = match imports with [imported] -> imported | _ -> invalidOp "Generated libc abs import was not uniquely settled"
        require (imported.Library = "c" && imported.Parameters.Length = 1 && imported.Result.IsSome) "Generated scalar ABI was not preserved"
        let calls = boundary.Calls.Values |> Seq.filter (fun call -> call.Import = imported.Identity) |> Seq.toList
        require (calls.Length = 1 && calls.Head.Arguments.Length = 1) "The demanded generated scalar call was not settled"
        let revision = ProjectChecker.publish result |> need
        require (revision.Emission.Boundary.Imports.Values |> Seq.exists (fun import -> import.Library = "c" && import.Symbol = "abs")) "Public PSG lost the generated import"
        require (not revision.Emission.Boundary.Calls.IsEmpty) "Public PSG lost the reachable boundary call")

    gate "pilot-generator-libc-scalar" "project-generated declaration consumed by current Clef" (fun () ->
        let directory = Path.Combine(inputs.Work, "pilot-generator-libc-scalar")
        Directory.CreateDirectory directory |> ignore
        let project =
            { inputs.Pilot with
                Output = { inputs.Pilot.Output with Directory = Path.Combine(directory, "Bindings", "Native") }
                Namespaces = inputs.Pilot.Namespaces |> List.map (fun namespace' -> { namespace' with Functions = ["abs"] })
                Options = inputs.Pilot.Options |> Option.map (fun options -> { options with Bindings = [] }) }
        let pilot = Path.Combine(directory, "scalar.pilot.toml")
        PilotSerializer.saveToFile pilot project |> need
        let generation = BindingGenerator.generateFromProject pilot false false Abi.LP64 |> need
        let manifests = generation.OutputFiles |> List.filter (fun path -> Path.GetExtension(path) = ".fidproj")
        require (not manifests.IsEmpty) "Project generator returned no binding manifest"
        for manifest in manifests do
            let options = FidprojLoader.load manifest |> need
            require (not options.Dependencies.IsEmpty) "Generated binding manifest omitted its declaration vocabulary dependency"
            for dependency in options.Dependencies do
                match dependency.Path with
                | Some path -> require (File.Exists path) (sprintf "Generated manifest names missing dependency %s: %s" dependency.Name path)
                | None -> invalidOp (sprintf "Fixture manifest dependency %s did not name an explicit source project" dependency.Name)
        let sources = generation.OutputFiles |> List.filter (fun path -> Path.GetExtension(path) = ".clef")
        require (not sources.IsEmpty) "Project generator returned no Clef declarations"
        let result = check inputs "pilot-generator-libc-scalar" sources scalarMain
        require (not (ProjectChecker.hasErrors result)) (String.concat "\n" (ProjectChecker.getErrorMessages result))
        let revision = ProjectChecker.publish result |> need
        require (revision.Emission.Boundary.Imports.Values |> Seq.exists (fun import -> import.Symbol = "abs" && import.Library = "c")) "Pilot-generated scalar import was not published")

    gate "legacy-profile-generation" "raw pointer pilot rejected before writing output" (fun () ->
        let project =
            { inputs.Pilot with
                Options = inputs.Pilot.Options |> Option.map (fun options -> { options with NativePointerSurface = true }) }
        rejectedGeneration inputs "legacy-profile-generation" "experimental_native_pointer_surface" false Abi.LP64 project)

    gate "legacy-protocol-generation" "untyped XML protocol rejected before writing output" (fun () ->
        let project =
            { inputs.Pilot with
                Library = { inputs.Pilot.Library with XmlProtocols = [Path.Combine(inputs.Work, "unopened-protocol.xml")] }
                Options = inputs.Pilot.Options |> Option.map (fun options -> { options with TypedProtocol = false }) }
        rejectedGeneration inputs "legacy-protocol-generation" "typed_protocol = true" false Abi.LP64 project)

    gate "opaque-errno-generation" "errno dereference wrapper rejected before writing output" (fun () ->
        let project = { inputs.Pilot with ErrorConventions = Some { Default = PilotTypes.Errno; Overrides = Map.empty } }
        rejectedGeneration inputs "opaque-errno-generation" "declared native scalar accessor" true Abi.LP64 project)

    gate "unsettled-error-reason-generation" "error-text return contract rejected before writing output" (fun () ->
        let project = { inputs.Pilot with ErrorConventions = Some { Default = PilotTypes.NullWithReason "token_reason"; Overrides = Map.empty } }
        rejectedGeneration inputs "unsettled-error-reason-generation" "declared reason return type" true Abi.LP64 project)

    gate "unsupported-project-abi" "LLP64 pilot rejected before emitting a Linux LP64 manifest" (fun () ->
        rejectedGeneration inputs "unsupported-project-abi" "Linux x86_64" false Abi.LLP64 inputs.Pilot)

    gate "unsupported-cpp-object-generation" "single-header unsupported object projection returns Error before writes" (fun () ->
        let directory = Path.Combine(inputs.Work, "unsupported-cpp-object-generation")
        let output = Path.Combine(directory, "Bindings")
        Directory.CreateDirectory output |> ignore
        let sentinel = Path.Combine(output, "preserve-existing.clef")
        let contents = "// Preserve the previous output when an object ABI cannot be admitted.\n"
        File.WriteAllText(sentinel, contents)
        let header = Path.Combine(directory, "object.hpp")
        File.Copy(Path.Combine(__SOURCE_DIRECTORY__, "object.hpp"), header)
        let options: BindingGenerator.GenerationOptions =
            { HeaderFile = FileInfo header; LibraryName = "boundaryprobe"; OutputDirectory = output
              Namespace = "Conformance.Object"; IncludePaths = [directory]; Defines = []; Verbose = false
              GenerateWrappers = true; DataModel = Abi.LP64 }
        match BindingGenerator.generateBindings options with
        | Error message ->
            File.WriteAllText(Path.Combine(directory, "generation-failure.txt"), message)
            require (message.Contains("requires a supported object storage, ownership and method ABI contract", StringComparison.Ordinal)) ("Unexpected object generation failure: " + message)
        | Ok _ -> invalidOp "Unsupported C++ object generated an address-based source surface"
        require (File.ReadAllText sentinel = contents) "Failed object generation changed the previous output"
        require (Directory.GetFiles(output, "*", SearchOption.AllDirectories) = [|sentinel|]) "Failed object generation wrote additional output")

    gate "generated-opaque-contracts" "typed declarations only; handle execution remains unsupported" (fun () ->
        let native = generated inputs "generated-opaque-contracts" "c"
        let result = check inputs "generated-opaque-contracts" [native] scalarMain
        require (not (ProjectChecker.hasErrors result)) (String.concat "\n" (ProjectChecker.getErrorMessages result))
        let marker = markerConstructor native result
        let nullable = bindingType "token_open" native result |> resultType
        require (nullableHandle marker nullable) (sprintf "Unannotated opaque return did not become option<CHandle<Token>>: %A" nullable)
        require (bindingType "token_nonnull" native result |> resultType |> handle marker) "Pilot nonnull evidence did not produce CHandle<Token>"
        require (bindingType "token_read" native result |> parameterTypes |> List.head |> handle marker) "Nonnull opaque parameter lost its phantom type"
        let callback = bindingType "token_visit" native result |> parameterTypes |> List.item 1
        let fields = Records.tryFields callback result.CheckResult.Graph |> Option.defaultWith (fun () -> invalidOp "Generated callback lost its nominal entry record")
        match fields with
        | ["Invoke", NativeType.TApp(constructor, [signature])] when constructor.Name = "FnPtr" ->
            require ((parameterTypes signature).Length = 3) "Generated callback signature lost an ABI parameter"
            require (parameterTypes signature |> List.head |> nullableHandle marker) "Unannotated callback argument lost its nullable opaque token contract"
            require (not (rawType signature)) "Generated callback signature leaked raw address types"
        | _ -> invalidOp (sprintf "Generated callback field lost its typed FnPtr: %A" fields)
        for node in result.CheckResult.Graph.Nodes.Values do
            if node.Range.File = native then require (not (rawType node.Type)) (sprintf "Generated source has a raw pointer type at %A" node.Range)
        require (File.ReadAllText(native).Contains("OwnershipTransfer = CalleeOwns", StringComparison.Ordinal)) "Pilot release ownership disappeared from the generated declaration")

    gate "generated-value-record-contracts" "record pointer fields preserve nullable and annotated handle contracts" (fun () ->
        let native = generatedWith inputs "generated-value-record-contracts" "c" (Set.singleton "BoundaryRecord")
        let result = check inputs "generated-value-record-contracts" [native] scalarMain
        require (not (ProjectChecker.hasErrors result)) (String.concat "\n" (ProjectChecker.getErrorMessages result))
        let marker = markerConstructor native result
        let recordType =
            result.CheckResult.Graph.Nodes.Values
            |> Seq.filter (fun node ->
                node.Range.File = native &&
                match node.Kind with
                | SemanticKind.TypeDef(_, TypeDefKind.RecordDef fields, _) ->
                    fields |> List.map fst |> Set.ofList = Set.ofList ["nullableToken"; "requiredToken"; "count"]
                | _ -> false)
            |> Seq.map (fun node -> applySubst node.Type) |> Seq.toList
            |> function [recordType] -> recordType | rows -> invalidOp (sprintf "Expected one generated value record, got %A" rows)
        let fields = Records.tryFields recordType result.CheckResult.Graph |> Option.defaultWith (fun () -> invalidOp "Generated value record has no settled fields") |> Map.ofList
        require (fields.TryFind "nullableToken" |> Option.exists (nullableHandle marker)) "Unannotated value-record pointer field did not become option<CHandle<Token>>"
        require (fields.TryFind "requiredToken" |> Option.exists (handle marker)) "Annotated nonnull value-record field lost CHandle<Token>"
        require (fields.Count = 3 && (fields.Values |> Seq.forall (rawType >> not))) "Generated value record contains a raw address field")

    gate "generated-opaque-execution" "explicit unsupported boundary diagnostic" (fun () ->
        let native = generated inputs "generated-opaque-execution" "c"
        let result = check inputs "generated-opaque-execution" [native] """module Conformance.Main
[<EntryPoint>]
let main _ =
    match Conformance.Native.token_open () with
    | None -> 1
    | Some token ->
        let value = Conformance.Native.token_read token
        Conformance.Native.token_release token
        if value = 173 then 0 else 2
"""
        rejectWith "CCS8403" "BoundaryEmission: This TypeRef requires a source-settled adapter beyond the scalar C contract" result
        rejectWith "CCS8403" "BoundaryEmission: A boundary parameter has no admitted integer or boolean declaration" result
        require (errors result |> List.forall (fun diagnostic -> diagnostic.Code <> "CCS8010")) "Compiler treated a declaration-only extern placeholder as an executable fabricated handle")

    gate "generated-foreign-scalar" "explicit unsupported library diagnostic" (fun () ->
        let native = generated inputs "generated-foreign-scalar" "boundaryprobe"
        let result = check inputs "generated-foreign-scalar" [native] scalarMain
        rejectWith "CCS8403" "BoundaryEmission: This C library requires a source-settled link/load contract beyond the admitted libc boundary" result)

    gate "source-forged-handle" "CCS8010 for demanded fabricated CHandle" (fun () ->
        let result = check inputs "source-forged-handle" [] """module Conformance.Main
[<EntryPoint>]
let main _ =
    let value: CHandle<unit> = NativeDefault.zeroed ()
    if value = value then 0 else 1
"""
        rejectWith "CCS8010" "NativeDefault.zeroed cannot construct a non-null CHandle or FnPtr" result)

    gate "source-forged-entry" "CCS8010 for demanded fabricated FnPtr" (fun () ->
        let result = check inputs "source-forged-entry" [] """module Conformance.Main
[<EntryPoint>]
let main _ =
    let callback: FnPtr<int -> int> = NativeDefault.zeroed ()
    if FnPtr.invoke callback 17 = 17 then 0 else 1
"""
        rejectWith "CCS8010" "NativeDefault.zeroed cannot construct a non-null CHandle or FnPtr" result)

    gate "source-forged-listener-record" "CCS8010 for zeroed record containing native entry" (fun () ->
        let result = check inputs "source-forged-listener-record" [] """module Conformance.Main
type Listener = { Invoke: FnPtr<int -> int>; Enabled: bool }
[<EntryPoint>]
let main _ =
    let listener: Listener = NativeDefault.zeroed ()
    if FnPtr.invoke listener.Invoke 17 = 17 then 0 else 1
"""
        rejectWith "CCS8010" "NativeDefault.zeroed cannot construct a non-null CHandle or FnPtr" result)

    gate "source-forged-generic-union" "CCS8010 for fabricated handle through generic union consumer" (fun () ->
        let result = check inputs "source-forged-generic-union" [] """module Conformance.Main
type Holder<'T> = Entry of 'T
let construct holder : CHandle<unit> =
    match holder with
    | Entry factory -> factory ()
[<EntryPoint>]
let main _ =
    let handle = construct (Entry NativeDefault.zeroed)
    if handle = handle then 0 else 1
"""
        rejectWith "CCS8010" "NativeDefault.zeroed cannot construct a non-null CHandle or FnPtr" result)

    gate "source-raw-address" "CCS8401 for reachable raw address formation" (fun () ->
        let result = check inputs "source-raw-address" [] """module Conformance.Main
[<EntryPoint>]
let main _ =
    let mutable value = 17
    let address = &&value
    if address = address then 0 else 1
"""
        rejectWith "CCS8401" "Raw addresses are not denotable in Clef" result)

    let evidence =
        {| Runner = "F# / .NET"; Generation = "actual clang header plus current-source Farscape.Core"
           Verification = "current-source Clef ProjectChecker and public PSG publication; no native execution"
           Platform = inputs.Platform; Metadata = inputs.Metadata
           Header = {| Path = inputs.Header; Sha256 = hash inputs.Header |}
           Assemblies = loadedAssemblies ()
           Observations = observations.ToArray() |}
    File.WriteAllText(Path.Combine(inputs.Work, "results.json"), JsonSerializer.Serialize(evidence, JsonSerializerOptions(WriteIndented = true)))
    printfn "%d/%d source conformance gates passed. Evidence: %s" (observations |> Seq.filter _.Passed |> Seq.length) observations.Count inputs.Work
    if observations |> Seq.forall _.Passed then 0 else 1

let suite (args: string array) =
    require (args.Length <= 2) "Usage: BoundaryConformance [Platform.CompilerSurface.fidproj [BAREWire.BindingMetadata.fidproj]] | --check-project <fidproj>"
    let siblings = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../.."))
    let platform = if args.Length > 0 then Path.GetFullPath args[0] else Path.Combine(siblings, "Fidelity.Platform/Environments/Linux/x86_64/Fidelity.Platform.CompilerSurface.fidproj")
    let metadata = if args.Length > 1 then Path.GetFullPath args[1] else Path.Combine(siblings, "BAREWire/src/BAREWire.BindingMetadata.fidproj")
    require (File.Exists platform) ("Missing platform project: " + platform)
    require (File.Exists metadata) ("Missing binding metadata project: " + metadata)
    let work = Path.Combine(Path.GetTempPath(), "farscape-boundary-conformance-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory work |> ignore
    printfn "Boundary source conformance evidence: %s" work
    prepare work platform metadata |> run

[<EntryPoint>]
let main args =
    // Failures are data, including unexpected setup/compiler failures. Do not
    // turn a failed compiler gate into an unhandled .NET exception/core dump.
    try
        if args.Length = 2 && args[0] = "--check-project" then checkExistingProject args[1]
        else suite args
    with error ->
        eprintfn "Boundary source conformance setup failed: %s" (error.ToString())
        1
