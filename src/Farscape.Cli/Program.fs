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

let runGeneration (options: GenerationOptions) : Result<GenerationResult, string> =
    // Show generating message
    printHeader "Generating Clef bindings..."
    printLine ""

    match BindingGenerator.generateBindings options with
    | Error errorMsg ->
        Error errorMsg
    | Ok result ->
        printLine ""
        printColorLine "Generation Complete" ConsoleColor.Green
        printLine ""
        printColorLine $"Parsed {result.DeclarationCount} declarations from header" ConsoleColor.White
        for file in result.OutputFiles do
            printColorLine $"  {file}" ConsoleColor.Cyan
        printLine ""
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
        | Error errorMsg ->
            showError errorMsg
            1
        | Ok _ ->
            showNextSteps options
            0

    command "generate" {
        description "Generate Clef bindings for a native library"
        inputs (header, library, output, ns, includes, defines, verbose, outputMode)
        setAction action
    }

/// Resolve include paths and defines from pkg-config.
/// Returns (includePaths, defines) extracted from `pkg-config --cflags <name>`.
let resolvePkgConfig (pkgName: string) (verbose: bool) : Result<string list * string list, string> =
    try
        let psi = System.Diagnostics.ProcessStartInfo("pkg-config", $"--cflags {pkgName}")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        let proc = System.Diagnostics.Process.Start(psi)
        let output = proc.StandardOutput.ReadToEnd().Trim()
        let stderr = proc.StandardError.ReadToEnd().Trim()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then
            Error $"pkg-config failed for '{pkgName}': {stderr}"
        else
            if verbose then
                printLine $"pkg-config --cflags {pkgName}:"
                printLine $"  {output}"
            let tokens = output.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            let includes = tokens |> Array.filter (fun t -> t.StartsWith("-I")) |> Array.map (fun t -> t.Substring(2)) |> Array.toList
            let defines = tokens |> Array.filter (fun t -> t.StartsWith("-D")) |> Array.map (fun t -> t.Substring(2)) |> Array.toList
            Ok (includes, defines)
    with ex ->
        Error $"Failed to run pkg-config: {ex.Message}"

let pilotAnalyzeCommand =
    let header =    Input.option<string[]> "--header"         |> desc "Path to C/C++ header file(s)" |> required
    let library =   Input.option<string> "--library"         |> alias "-l" |> desc "Library name (e.g., libc)" |> required
    let includes =  Input.option<string[]> "--include-paths" |> alias "-i" |> desc "Additional include paths" |> def [||]
    let defines =   Input.option<string[]> "--defines"       |> alias "-d" |> desc "Preprocessor definitions" |> def [||]
    let pkgConfig = Input.option<string[]> "--pkg-config"    |> alias "-p" |> desc "pkg-config package names (auto-resolves include paths and defines)" |> def [||]
    let transHdrs = Input.option<string[]> "--transitive-headers" |> alias "-t" |> desc "Filenames of transitively-included headers to also extract declarations from" |> def [||]
    let output =    Input.option<string> "--output"          |> alias "-o" |> desc "Output directory (default: ./<library>)" |> def ""
    let verbose =   Input.option<bool> "--verbose"           |> alias "-v" |> desc "Verbose output" |> def false

    let action (headers: string[], library, includes: string[], defines: string[], pkgConfig: string[], transitiveHeaders: string[], output, verbose) =
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
        let mutable pkgDefines = []
        let mutable pkgError = None
        for pkg in pkgConfig do
            match resolvePkgConfig pkg verbose with
            | Ok (incl, defs) ->
                pkgIncludes <- pkgIncludes @ incl
                pkgDefines <- pkgDefines @ defs
            | Error e ->
                pkgError <- Some e

        match pkgError with
        | Some e ->
            showError e
            1
        | None ->

        let includePaths = (includes |> Array.toList) @ pkgIncludes |> List.distinct
        let definesList = (defines |> Array.toList) @ pkgDefines |> List.distinct

        // Parse all headers and merge declarations
        let transitiveList = transitiveHeaders |> Array.toList
        let mutable allDeclarations = []
        let mutable parseError = None
        for headerPath in headers do
            match parseError with
            | Some _ -> ()
            | None ->
                match CppParser.parseWithTransitiveHeaders headerPath includePaths definesList transitiveList [] verbose with
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
            PilotAnalyzer.toPilotProject library headerFiles includePaths definesList (pkgConfig |> Array.toList) transitiveList "fidelity" outputDir result

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
        inputs (header, library, includes, defines, pkgConfig, transHdrs, output, verbose)
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
                XmlProtocols = []
                IncludePaths = []
                Defines = []
                TransitiveHeaders = []
                MacroPrefixes = []
                PkgConfig = []
            }
            Output = { Mode = "fidelity"; Directory = outputDir }
            Namespaces = [
                { Name = $"Fidelity.{library}.Core"
                  Description = "Core functions"
                  Library = library
                  Prefixes = []
                  Functions = []
                  XmlInterfaces = [] }
            ]
            ErrorConventions = None
            Options = None
            Callbacks = None
            Nonnull = None
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

        match BindingGenerator.generateFromProject project.FullName verbose wrappers Farscape.Core.Types.LP64 with
        | Error e ->
            showError e
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
            0

    command "project" {
        description "Generate bindings from a .pilot.toml project file"
        inputs (project, verbose, wrappers)
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
    }
