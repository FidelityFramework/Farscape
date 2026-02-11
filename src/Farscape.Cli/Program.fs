module Farscape.Cli.Program

open System
open System.IO
open Farscape.Core
open FSharp.SystemCommandLine
open Input
open Farscape.Core.BindingGenerator
open Farscape.Core.MoyaTypes

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
    printHeader "Farscape: F# Native Library Binding Generator"

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
    printHeader "Generating F# bindings..."
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

    printLine "How to use the generated bindings:"
    printLine ""

    printLine "Build the project:"
    printLine $"  cd {options.OutputDirectory}"
    printLine "  dotnet build"
    printLine ""

    printLine "Use in your own project:"
    printLine $"  Add a reference to {options.LibraryName}.dll"
    printLine $"  open {options.Namespace}"
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
    let outputMode = Input.option<string> "--output-mode"     |> alias "-m" |> desc "Output mode: pinvoke, fidelity, or fidelity-wrappers" |> def "pinvoke"

    let action (header, library, output, ns, includes, defines, verbose, outputMode) =
        let mode, wrappers =
            match outputMode with
            | "fidelity-wrappers" | "Fidelity-Wrappers" -> Farscape.Core.Types.Fidelity, true
            | "fidelity" | "Fidelity" -> Farscape.Core.Types.Fidelity, false
            | _ -> Farscape.Core.Types.PInvoke, false
        let options = {
            HeaderFile = header
            LibraryName = library
            OutputDirectory = output
            Namespace = ns
            IncludePaths = includes |> Array.toList
            Defines = defines |> Array.toList
            Verbose = verbose
            OutputMode = mode
            GenerateWrappers = wrappers
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
        description "Generate F# bindings for a native library"
        inputs (header, library, output, ns, includes, defines, verbose, outputMode)
        setAction action
    }

let moyaAnalyzeCommand =
    let header =   Input.option<FileInfo> "--header"        |> desc "Path to C/C++ header file" |> required |> validateFileExists
    let library =  Input.option<string> "--library"         |> alias "-l" |> desc "Library name (e.g., libc)" |> required
    let includes = Input.option<string[]> "--include-paths" |> alias "-i" |> desc "Additional include paths" |> def [||]
    let defines =  Input.option<string[]> "--defines"       |> alias "-d" |> desc "Preprocessor definitions" |> def [||]
    let output =   Input.option<string> "--output"          |> alias "-o" |> desc "Output directory (default: ./<library>)" |> def ""
    let verbose =  Input.option<bool> "--verbose"           |> alias "-v" |> desc "Verbose output" |> def false

    let action (header: FileInfo, library, includes: string[], defines: string[], output, verbose) =
        showHeader ()
        printHeader "Moya: Analyzing header for namespace subdivisions"
        printLine ""

        let outputDir =
            if String.IsNullOrEmpty output then $"./{library}"
            else output
        Directory.CreateDirectory(outputDir) |> ignore

        let includePaths = includes |> Array.toList
        let definesList = defines |> Array.toList

        match CppParser.parseWithDefines header.FullName includePaths definesList verbose with
        | Error e ->
            showError $"Failed to parse header: {e}"
            1
        | Ok declarations ->
            let result = MoyaAnalyzer.analyze declarations

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

            let project =
                MoyaAnalyzer.toMoyaProject library header.FullName includePaths definesList "fidelity" outputDir result

            let tomlPath = Path.Combine(outputDir, $"{library}.moya.toml")

            match MoyaSerializer.saveToFile tomlPath project with
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
        description "Analyze a header file and generate a .moya.toml project file"
        inputs (header, library, includes, defines, output, verbose)
        setAction action
    }

let moyaInitCommand =
    let library = Input.option<string> "--library" |> alias "-l" |> desc "Library name" |> required
    let header =  Input.option<string> "--header"  |> desc "Path to header file" |> def ""
    let output =  Input.option<string> "--output"  |> alias "-o" |> desc "Output directory (default: ./<library>)" |> def ""

    let action (library, header, output) =
        let outputDir =
            if String.IsNullOrEmpty output then $"./{library}"
            else output
        Directory.CreateDirectory(outputDir) |> ignore

        let project : MoyaProject = {
            Library = {
                Name = library
                Headers = [if String.IsNullOrEmpty header then $"/usr/include/{library}.h" else header]
                IncludePaths = []
                Defines = []
            }
            Output = { Mode = "fidelity"; Directory = outputDir }
            Namespaces = [
                { Name = $"Fidelity.{library}.Core"
                  Description = "Core functions"
                  Library = library
                  Prefixes = []
                  Functions = [] }
            ]
            ErrorConventions = None
        }

        let tomlPath = Path.Combine(outputDir, $"{library}.moya.toml")

        match MoyaSerializer.saveToFile tomlPath project with
        | Error e ->
            showError $"Failed to write: {e}"
            1
        | Ok () ->
            printColorLine $"Skeleton project written: {tomlPath}" ConsoleColor.Green
            printLine "Edit the file to add namespace definitions, then run:"
            printLine $"  farscape project --project {tomlPath}"
            0

    command "init" {
        description "Create a skeleton .moya.toml project file"
        inputs (library, header, output)
        setAction action
    }

let moyaCommand =
    command "moya" {
        description "Namespace analysis and project file management"
        noAction
        addCommand moyaAnalyzeCommand
        addCommand moyaInitCommand
    }

let projectGenerateCommand =
    let project = Input.option<FileInfo> "--project" |> desc "Path to .moya.toml project file" |> required |> validateFileExists
    let verbose = Input.option<bool> "--verbose"     |> alias "-v" |> desc "Verbose output" |> def false
    let wrappers = Input.option<bool> "--wrappers"   |> alias "-w" |> desc "Also generate idiomatic F# wrappers (Layer 2)" |> def false

    let action (project: FileInfo, verbose, wrappers) =
        showHeader ()
        printHeader "Generating Fidelity bindings from project..."
        printLine ""

        match BindingGenerator.generateFromProject project.FullName verbose wrappers with
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
            0

    command "project" {
        description "Generate Fidelity bindings from a .moya.toml project file"
        inputs (project, verbose, wrappers)
        setAction action
    }

[<EntryPoint>]
let main argv =
    rootCommand argv {
        description "Farscape: F# Native Library Binding Generator"
        noAction
        addCommand generateCommand
        addCommand moyaCommand
        addCommand projectGenerateCommand
    }
