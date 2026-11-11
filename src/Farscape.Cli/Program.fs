module Farscape.Cli.Program

open System
open System.IO
open Farscape.Core
open FSharp.SystemCommandLine
open Input
open Farscape.Core.BindingGenerator
open Farscape.Core.PilotTypes

let printLine (text: string) =
    Console.WriteLine(text)

let printColorLine (text: string) (color: ConsoleColor) =
    let originalColor = Console.ForegroundColor
    Console.ForegroundColor <- color
    Console.WriteLine(text)
    Console.ForegroundColor <- originalColor

let printHeader (text: string) =
    let width = Math.Min(Console.WindowWidth, 80)
    let line = new String('=', width)
    printColorLine line ConsoleColor.Cyan
    printColorLine text ConsoleColor.Cyan
    printColorLine line ConsoleColor.Cyan

let showHeader () =
    printHeader "Farscape: Clef Native Library Binding Generator"

let showConfiguration (options: GenerationOptions) =
    printHeader "Configuration"
    printLine ""

    printColorLine "Header File:" ConsoleColor.Yellow
    printLine $"  {options.HeaderFile}"

    printColorLine "Library Name:" ConsoleColor.Yellow
    printLine $"  {options.LibraryName}"

    printColorLine "Output Directory:" ConsoleColor.Yellow
    printLine $"  {options.OutputDirectory}"

    printColorLine "Namespace:" ConsoleColor.Yellow
    printLine $"  {options.Namespace}"

    printColorLine "Include Paths:" ConsoleColor.Yellow
    if options.IncludePaths = [] then
        printLine "  None"
    else
        options.IncludePaths
        |> List.iter (fun path -> printLine $"  {path}")

    printColorLine "Defines:" ConsoleColor.Yellow
    if options.Defines = [] then
        printLine "  None"
    else
        options.Defines
        |> List.iter (fun d -> printLine $"  {d}")

    printLine ""

/// Every parsed macro generation emitted as no constant, by name and kind (#193).
let printUnemittedMacros (result: GenerationResult) =
    if not result.UnemittedMacros.IsEmpty then
        printColorLine $"Macros emitted as no constant: {result.UnemittedMacros.Length}" ConsoleColor.Yellow
        for macro in result.UnemittedMacros do
            printColorLine $"  {macro.Name}: {FidelityCodeGenerator.describeUnemittedMacroKind macro.Kind}" ConsoleColor.Yellow
        printLine ""

let runGeneration (options: GenerationOptions) : Result<GenerationResult, Diagnostics.Refusal> =
    // Show generating message
    printHeader "Generating Clef bindings..."
    printLine ""

    match BindingGenerator.generateBindingsDiagnosed options with
    | Error refusal ->
        Error refusal
    | Ok result ->
        printLine ""
        printColorLine "Generation Complete" ConsoleColor.Green
        printLine ""
        printColorLine $"Parsed {result.DeclarationCount} declarations from header" ConsoleColor.White
        for file in result.OutputFiles do
            printColorLine $"  {file}" ConsoleColor.Cyan
        printLine ""
        printUnemittedMacros result
        Ok result

let showNextSteps (options: GenerationOptions) =
    // Show next steps
    printHeader "Next Steps"
    printLine ""

    printLine "The generated Fidelity binding declarations are ready for"
    printLine "integration with the Composer compilation pipeline."
    printLine ""

    printLine $"Output directory: {options.OutputDirectory}"
    printLine $"Namespace: {options.Namespace}"
    printLine ""

let showError (message: string) =
    printColorLine $"Error: {message}" ConsoleColor.Red
    printLine ""

let generateCommand =
    let header =     Input.option<FileInfo> "--header"        |> desc "Path to C++ header file" |> required |> validateFileExists
    let library =    Input.option<string> "--library"         |> alias "-l" |> desc "Name of native library to bind to" |> required
    let output =     Input.option<string> "--output"          |> alias "-o" |> desc "Output directory for generated code" |> def "./output"
    let ns =         Input.option<string> "--namespace"       |> alias "-n" |> desc "Namespace for generated code" |> def "NativeBindings"
    let includes =   Input.option<string[]> "--include-paths" |> alias "-i" |> desc "Additional include paths" |> def [||]
    let defines =    Input.option<string[]> "--defines"       |> alias "-d" |> desc "Preprocessor definitions (e.g., STM32L552xx)" |> def [||]
    let verbose =    Input.option<bool> "--verbose"           |> alias "-v" |> desc "Verbose output" |> def false
    let outputMode = Input.option<string> "--output-mode"     |> alias "-m" |> desc "Output mode: fidelity, fidelity-wrappers" |> def "fidelity"

    let parseOutputMode (modeStr: string) =
        let lower = modeStr.ToLowerInvariant()
        match lower with
        | "fidelity-wrappers" -> true, Farscape.Core.Types.LP64
        | _ -> false, Farscape.Core.Types.LP64

    let action (header, library, output, ns, includes, defines, verbose, outputMode) =
        let wrappers, dataModel = parseOutputMode outputMode
        let options = {
            HeaderFile = header
            LibraryName = library
            OutputDirectory = output
            Namespace = ns
            IncludePaths = includes |> Array.toList
            Defines = defines |> Array.toList
            Verbose = verbose
            GenerateWrappers = wrappers
            DataModel = dataModel
        }
        showHeader()
        showConfiguration options

        match runGeneration options with
        | Error refusal ->
            showError refusal.Text
            // Each FS91xx refusal's clang location (#186), as project generation prints it.
            for d in refusal.Diagnostics do
                for line in Diagnostics.renderLocation d do
                    printColorLine $"  {line}" ConsoleColor.Red
            if not refusal.Diagnostics.IsEmpty then printLine ""
            1
        | Ok _ ->
            showNextSteps options
            0

    command "generate" {
        description "Generate Clef bindings for a native library"
        inputs (header, library, output, ns, includes, defines, verbose, outputMode)
        setAction action
    }

/// Resolve include paths, system include paths and defines from pkg-config
/// (`PkgConfig.resolve`, #178, #179): the module name is one argument, and output holding
/// anything but -I, -isystem and -D words is refused.
let resolvePkgConfig (pkgName: string) (verbose: bool) : Result<PkgConfig.Cflags, string> =
    let resolved = PkgConfig.resolve pkgName
    if verbose then
        match resolved with
        | Ok cflags ->
            printLine $"pkg-config --cflags {pkgName}:"
            for path in cflags.IncludePaths do printLine $"  -I{path}"
            for path in cflags.SystemIncludePaths do printLine $"  -isystem {path}"
            for define in cflags.Defines do printLine $"  -D{define}"
        | Error _ -> ()
    resolved

let pilotAnalyzeCommand =
    let header =    Input.option<string[]> "--header"         |> desc "Path to C/C++ header file(s)" |> required
    let library =   Input.option<string> "--library"         |> alias "-l" |> desc "Library name (e.g., libc)" |> required
    let includes =  Input.option<string[]> "--include-paths" |> alias "-i" |> desc "Additional include paths" |> def [||]
    let defines =   Input.option<string[]> "--defines"       |> alias "-d" |> desc "Preprocessor definitions" |> def [||]
    let pkgConfig = Input.option<string[]> "--pkg-config"    |> alias "-p" |> desc "pkg-config package names (auto-resolves include paths and defines)" |> def [||]
    let output =    Input.option<string> "--output"          |> alias "-o" |> desc "Output directory (default: ./<library>)" |> def ""
    let verbose =   Input.option<bool> "--verbose"           |> alias "-v" |> desc "Verbose output" |> def false

    let action (headers: string[], library, includes: string[], defines: string[], pkgConfig: string[], output, verbose) =
        showHeader ()
        printHeader "Pilot: Analyzing header for namespace subdivisions"
        printLine ""

        let outputDir =
            if String.IsNullOrEmpty output then $"./{library}"
            else output
        Directory.CreateDirectory(outputDir) |> ignore

        // Validate header files exist
        let missingHeaders = headers |> Array.filter (fun h -> not (File.Exists h))
        if missingHeaders.Length > 0 then
            for h in missingHeaders do
                showError $"Header file not found: {h}"
            1
        else

        // Resolve pkg-config flags and merge with explicit CLI flags
        let mutable pkgIncludes = []
        let mutable pkgSystemIncludes = []
        let mutable pkgDefines = []
        let mutable pkgError = None
        for pkg in pkgConfig do
            match resolvePkgConfig pkg verbose with
            | Ok cflags ->
                pkgIncludes <- pkgIncludes @ cflags.IncludePaths
                pkgSystemIncludes <- pkgSystemIncludes @ cflags.SystemIncludePaths
                pkgDefines <- pkgDefines @ cflags.Defines
            | Error e ->
                pkgError <- Some e

        match pkgError with
        | Some e ->
            showError e
            1
        | None ->

        let includePaths = (includes |> Array.toList) @ pkgIncludes |> List.distinct
        let systemIncludePaths = pkgSystemIncludes |> List.distinct
        let definesList = (defines |> Array.toList) @ pkgDefines |> List.distinct

        // Derive include root from pkg-config paths for library boundary scoping
        let deriveIncludeRoot (headerPath: string) =
            let fullPath = Path.GetFullPath headerPath
            includePaths @ systemIncludePaths
            |> List.tryFind (fun ip ->
                let fullIp = Path.GetFullPath ip
                fullPath.StartsWith fullIp)

        // Parse all headers and merge declarations
        let mutable allDeclarations = []
        let mutable parseError = None
        for headerPath in headers do
            match parseError with
            | Some _ -> ()
            | None ->
                let includeRoot = deriveIncludeRoot headerPath
                let options =
                    { CppParser.defaultOptions headerPath with
                        IncludePaths = includePaths; SystemIncludePaths = systemIncludePaths; Defines = definesList
                        IncludeRoot = includeRoot; Verbose = verbose }
                match CppParser.parseHeader options with
                | Error e -> parseError <- Some (headerPath, e)
                | Ok decls -> allDeclarations <- allDeclarations @ decls

        match parseError with
        | Some (path, e) ->
            showError $"Failed to parse header '{path}': {e}"
            1
        | None ->

        let declarations = allDeclarations
        let result = PilotAnalyzer.analyze declarations

        printColorLine $"Parsed {declarations.Length} declarations, {result.TotalFunctions} functions" ConsoleColor.White
        printLine ""

        printColorLine "Prefix Groups:" ConsoleColor.Yellow
        for g in result.Groups do
            let patterns = g.Prefixes |> List.map (fun p -> p + "*") |> String.concat ", "
            printLine $"  {g.SuggestedName} ({patterns}): {g.FunctionNames.Length} functions"
            if verbose then
                for fn in g.FunctionNames do
                    printLine $"    - {fn}"

        printLine ""
        printColorLine $"Ungrouped: {result.Ungrouped.Length} functions" ConsoleColor.Yellow
        if verbose then
            for fn in result.Ungrouped do
                printLine $"    - {fn}"

        let headerFiles = headers |> Array.toList
        let project =
            PilotAnalyzer.toPilotProject library headerFiles includePaths systemIncludePaths definesList (pkgConfig |> Array.toList) "fidelity" outputDir result

        let tomlPath = Path.Combine(outputDir, $"{library}.pilot.toml")

        match PilotSerializer.saveToFile tomlPath project with
        | Error e ->
            showError $"Failed to write project file: {e}"
            1
        | Ok () ->
            printLine ""
            printColorLine $"Output directory: {Path.GetFullPath(outputDir)}" ConsoleColor.Green
            printColorLine $"  Project file: {tomlPath}" ConsoleColor.Cyan
            printLine ""
            printLine "Next steps:"
            printLine $"  farscape project --project {tomlPath}"
            0

    command "analyze" {
        description "Analyze a header file and generate a .pilot.toml project file"
        inputs (header, library, includes, defines, pkgConfig, output, verbose)
        setAction action
    }

let pilotInitCommand =
    let library = Input.option<string> "--library" |> alias "-l" |> desc "Library name" |> required
    let header =  Input.option<string> "--header"  |> desc "Path to header file" |> def ""
    let output =  Input.option<string> "--output"  |> alias "-o" |> desc "Output directory (default: ./<library>)" |> def ""

    let action (library, header, output) =
        let outputDir =
            if String.IsNullOrEmpty output then $"./{library}"
            else output
        Directory.CreateDirectory(outputDir) |> ignore

        let project : PilotProject = {
            Library = {
                Name = library
                Headers = [if String.IsNullOrEmpty header then $"/usr/include/{library}.h" else header]
                XmlProtocols = []; Introspection = []
                IncludePaths = []
                SystemIncludePaths = []
                Defines = []
                MacroPrefixes = []
                PkgConfig = []
                Artifact = None
            }
            Output = { Mode = "fidelity"; Directory = outputDir }
            Namespaces = [
                { Name = $"Fidelity.{library}.Core"
                  Description = "Core functions"
                  Library = library
                  Prefixes = []
                  Functions = []
                  XmlInterfaces = []; Signals = [] }
            ]
            ErrorConventions = None
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
            Layer3 = None
            CppConfig = None
        }

        let tomlPath = Path.Combine(outputDir, $"{library}.pilot.toml")

        match PilotSerializer.saveToFile tomlPath project with
        | Error e ->
            showError $"Failed to write: {e}"
            1
        | Ok () ->
            printColorLine $"Skeleton project written: {tomlPath}" ConsoleColor.Green
            printLine "Edit the file to add namespace definitions, then run:"
            printLine $"  farscape project --project {tomlPath}"
            0

    command "init" {
        description "Create a skeleton .pilot.toml project file"
        inputs (library, header, output)
        setAction action
    }

let pilotDiscoverCommand =
    let directory = Input.option<DirectoryInfo> "--directory" |> desc "Root directory to scan for source assets" |> required
    let library =  Input.option<string> "--library"          |> alias "-l" |> desc "Library name hint (optional)" |> def ""
    let output =   Input.option<string> "--output"           |> alias "-o" |> desc "Output directory for generated .pilot.toml (default: ./<library>)" |> def ""
    let verbose =  Input.option<bool> "--verbose"            |> alias "-v" |> desc "Verbose: show all discovered files" |> def false

    let action (directory: DirectoryInfo, library, output, verbose) =
        showHeader ()
        printHeader "Pilot Discovery: Scanning for source assets"
        printLine ""

        let libraryHint = if String.IsNullOrEmpty library then None else Some library
        let result = PilotDiscovery.discoverFromDirectory directory.FullName libraryHint

        // Check for errors
        let hasErrors =
            result.Diagnostics |> List.exists (function
                | PilotDiscovery.DiagError _ -> true
                | _ -> false)

        if hasErrors then
            for diag in result.Diagnostics do
                match diag with
                | PilotDiscovery.DiagError (PilotDiscovery.DirectoryNotFound p) ->
                    showError $"Directory not found: {p}"
                | PilotDiscovery.DiagError (PilotDiscovery.NoParseableFiles p) ->
                    showError $"No parseable files found in: {p}"
                | _ -> ()
            1
        else
            // Classification summary
            let cHeaders = result.Files |> List.choose (function PilotDiscovery.CHeader (p, _, _) -> Some p | _ -> None)
            let cppHeaders = result.Files |> List.choose (function PilotDiscovery.CppHeader (p, _, _) -> Some p | _ -> None)
            let protocols = result.Files |> List.choose (function PilotDiscovery.ProtocolXml (p, _) -> Some p | _ -> None)
            let pkgConfigs = result.Files |> List.choose (function PilotDiscovery.PkgConfig (p, _) -> Some p | _ -> None)
            let buildFiles = result.Files |> List.choose (function PilotDiscovery.BuildSystemFile (p, _) -> Some p | _ -> None)

            printColorLine $"Scanned: {directory.FullName}" ConsoleColor.White
            printLine $"  C headers:     {cHeaders.Length}"
            printLine $"  C++ headers:   {cppHeaders.Length}"
            printLine $"  Protocol XMLs: {protocols.Length}"
            printLine $"  Pkg-config:    {pkgConfigs.Length}"
            printLine $"  Build files:   {buildFiles.Length}"
            printLine ""

            if verbose then
                if not cHeaders.IsEmpty then
                    printColorLine "C Headers:" ConsoleColor.Yellow
                    for p in cHeaders do printLine $"  {p}"
                if not cppHeaders.IsEmpty then
                    printColorLine "C++ Headers:" ConsoleColor.Yellow
                    for p in cppHeaders do printLine $"  {p}"
                if not protocols.IsEmpty then
                    printColorLine "Protocol XMLs:" ConsoleColor.Yellow
                    for p in protocols do printLine $"  {p}"
                printLine ""

            // Show diagnostics
            let warnings = result.Diagnostics |> List.choose (function PilotDiscovery.DiagWarning w -> Some w | _ -> None)
            let suggestions = result.Diagnostics |> List.choose (function PilotDiscovery.DiagSuggestion s -> Some s | _ -> None)

            if not warnings.IsEmpty then
                printColorLine "Warnings:" ConsoleColor.Yellow
                for w in warnings do
                    match w with
                    | PilotDiscovery.NoUmbrellaHeader n ->
                        printLine $"  No umbrella header found among {n} headers"
                    | PilotDiscovery.InternalHeadersFound n ->
                        printLine $"  {n} internal/private header(s) found (excluded)"
                    | PilotDiscovery.MixedLanguage ->
                        printLine "  Mixed C and C++ headers — verify intended API surface"
                    | PilotDiscovery.LargeHeaderCount n ->
                        printLine $"  {n} headers found — consider using an umbrella header"
                printLine ""

            if not suggestions.IsEmpty then
                printColorLine "Suggestions:" ConsoleColor.Green
                for s in suggestions do
                    match s with
                    | PilotDiscovery.PkgConfigFound (name, libName, incPath) ->
                        let lib = libName |> Option.map (fun l -> $", library: {l}") |> Option.defaultValue ""
                        let inc = incPath |> Option.map (fun p -> $", include: {p}") |> Option.defaultValue ""
                        printLine $"  pkg-config: {name}{lib}{inc}"
                    | PilotDiscovery.UmbrellaDetected (file, _, _) ->
                        printLine $"  Umbrella header detected: {file}"
                    | PilotDiscovery.ProtocolsFound n ->
                        printLine $"  {n} protocol XML file(s) found"
                    | PilotDiscovery.ExternCDetected file ->
                        printLine $"  C++ with extern \"C\" surface: {file}"
                printLine ""

            // Generate .pilot.toml
            let libName =
                match libraryHint with
                | Some n -> n
                | None -> result.SuggestedLibraryName |> Option.defaultValue "unknown"

            let outputDir =
                if String.IsNullOrEmpty output then $"./{libName}"
                else output
            Directory.CreateDirectory(outputDir) |> ignore

            let project = PilotDiscovery.toPilotProject libName "fidelity" outputDir result
            let tomlPath = Path.Combine(outputDir, $"{libName}.pilot.toml")

            match PilotSerializer.saveToFile tomlPath project with
            | Error e ->
                showError $"Failed to write project file: {e}"
                1
            | Ok () ->
                printColorLine $"Project file written: {tomlPath}" ConsoleColor.Green
                printLine ""
                printLine "Next steps:"
                printLine $"  1. Review and edit: {tomlPath}"
                printLine $"  2. Generate bindings: farscape project --project {tomlPath}"
                0

    command "discover" {
        description "Scan a directory tree and generate a .pilot.toml from discovered source assets"
        inputs (directory, library, output, verbose)
        setAction action
    }

let pilotCommand =
    command "pilot" {
        description "Namespace analysis and project file management"
        noAction
        addCommand pilotAnalyzeCommand
        addCommand pilotInitCommand
        addCommand pilotDiscoverCommand
    }

let projectGenerateCommand =
    let project =   Input.option<FileInfo> "--project"     |> desc "Path to .pilot.toml project file" |> required |> validateFileExists
    let verbose =   Input.option<bool> "--verbose"         |> alias "-v" |> desc "Verbose output" |> def false
    let wrappers =  Input.option<bool> "--wrappers"        |> alias "-w" |> desc "Also generate idiomatic Clef wrappers (Layer 2)" |> def false

    let action (project: FileInfo, verbose, wrappers) =
        showHeader ()
        printHeader "Generating Fidelity bindings from project..."
        printLine ""

        match BindingGenerator.generateFromProjectDiagnosed project.FullName verbose wrappers Farscape.Core.Types.LP64 with
        | Error refusal ->
            showError refusal.Text
            // Each FS91xx refusal's clang location and pilot key (#195).
            for d in refusal.Diagnostics do
                for line in Diagnostics.renderLocation d do
                    printColorLine $"  {line}" ConsoleColor.Red
            if not refusal.Diagnostics.IsEmpty then printLine ""
            1
        | Ok result ->
            printColorLine "Generation Complete" ConsoleColor.Green
            printLine ""
            printColorLine $"Parsed {result.DeclarationCount} declarations" ConsoleColor.White
            for file in result.OutputFiles do
                printColorLine $"  {file}" ConsoleColor.Cyan
            printLine ""
            for advisory in result.Advisories do
                printColorLine $"Advisory: {advisory}" ConsoleColor.Yellow
            if not result.Advisories.IsEmpty then printLine ""
            printUnemittedMacros result
            0

    command "project" {
        description "Generate bindings from a .pilot.toml project file"
        inputs (project, verbose, wrappers)
        setAction action
    }

let substantiateCommand =
    let fidproj = Input.option<FileInfo> "--fidproj" |> desc "Library .fidproj whose entire exported surface is substantiated" |> required
    let pilot = Input.option<FileInfo> "--pilot" |> desc "Pilot TOML providing measured foreign boundary contracts"
    let provenance = Input.option<FileInfo> "--link-provenance" |> desc "Exact library link-provenance TOML"
    let output = Input.option<DirectoryInfo> "--output" |> desc "Manifest output directory (default: library project directory)"
    let work = Input.option<DirectoryInfo> "--work-dir" |> desc "Retained harness and proof evidence directory (default: test-results/scratch/substantiation)"
    let verbose = Input.option<bool> "--verbose" |> desc "Print retained Composer diagnostics" |> def false
    let allowWarnings = Input.option<bool> "--allow-warnings" |> desc "Permit advisory warnings; required substantiation diagnostic codes remain errors" |> def false
    let action (fidproj: FileInfo, pilot: FileInfo, provenance: FileInfo, output: DirectoryInfo, work: DirectoryInfo, verbose, allowWarnings) =
        let request: Substantiation.Request =
            { FidprojPath = fidproj.FullName
              PilotPath = Option.ofObj pilot |> Option.map _.FullName
              LinkProvenancePath = Option.ofObj provenance |> Option.map _.FullName
              OutputDirectory = Option.ofObj output |> Option.map _.FullName
              WorkDirectory = Option.ofObj work |> Option.map _.FullName
              Verbose = verbose
              AllowWarnings = allowWarnings }
        match Substantiation.evaluate request with
        | Error refusal ->
            Console.Error.WriteLine refusal.Text
            for diagnostic in refusal.Diagnostics do
                for location in Diagnostics.renderLocation diagnostic do Console.Error.WriteLine location
            1
        | Ok manifest ->
            printLine $"Package: {manifest.Package}"
            printLine $"Exported modules: {manifest.Sources.Length}"
            printLine $"Library obligations: {manifest.Proof.LibraryObligations}; UNSAT(H and not C): {manifest.Proof.LibraryDischargedUnsatCount}; SAT(H): {manifest.Proof.LibraryPremisesSatCount}"
            printLine $"Total obligations: {manifest.Proof.TotalObligations}; harness: {manifest.Proof.HarnessObligations}; UNSAT(H and not C): {manifest.Proof.DischargedUnsatCount}; SAT(H): {manifest.Proof.PremisesSatCount}"
            printLine $"Compiler premise receipts: {manifest.Proof.CompilerPremiseReceiptsCount}; non-vacuity receipts: {manifest.Proof.CompilerNonVacuityReceiptsCount}"
            printLine $"Pending: {manifest.Proof.PendingOpenCount}; refuted: {manifest.Proof.RefutedCount}"
            printLine $"Link mode: {manifest.Boundary.LinkMode}"
            printLine $"RestsOn: {manifest.Boundary.RestsOn.Length}"
            printLine ("Required seam checks: " + String.concat ", " manifest.Boundary.RequiredSeamChecks)
            printLine $"Evidence: {manifest.ArtifactsDirectory}"
            let destination = request.OutputDirectory |> Option.defaultValue (Path.GetDirectoryName manifest.FidprojPath)
            printLine (Path.Combine(destination, manifest.Package + ".substantiation.toml"))
            printLine (Path.Combine(destination, manifest.Package + ".substantiation.json"))
            0
    command "substantiate" {
        description "Substantiate every library module, proof premise and foreign boundary contract"
        inputs (fidproj, pilot, provenance, output, work, verbose, allowWarnings)
        setAction action
    }

[<EntryPoint>]
let main argv =
    rootCommand argv {
        description "Farscape: Clef Native Library Binding Generator"
        noAction
        addCommand generateCommand
        addCommand pilotCommand
        addCommand projectGenerateCommand
        addCommand substantiateCommand
    }
