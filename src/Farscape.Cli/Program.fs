module Farscape.Cli.Program

open System
open System.IO
open Farscape.Core
open FSharp.SystemCommandLine
open System.CommandLine.Invocation
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
    let header = 
        Input.Option<FileInfo>(["-h"; "--header"], 
            // Manually edit underlying S.CL option to add validator logic.
            fun o -> 
                o.Description <- "Path to C++ header file"
                o.IsRequired <- true
                o.AddValidator(fun result -> 
                    let file = result.GetValueForOption<FileInfo> o
                    if not file.Exists then
                        result.ErrorMessage <- $"Header file not found: {file.FullName}"
                )
        )
    let library =   Input.OptionRequired<string>(["-l"; "--library"], description = "Name of native library to bind to")
    let output =    Input.Option<string>(["-o"; "--output"], description = "Output directory for generated code", defaultValue = "./output")
    let ns =        Input.Option<string>(["-n"; "--namespace"], description = "Namespace for generated code", defaultValue = "NativeBindings")
    let includes =  Input.Option<string[]>(["-i"; "--include-paths"], description = "Additional include paths")
    let defines =   Input.Option<string[]>(["-d"; "--defines"], description = "Preprocessor definitions (e.g., STM32L552xx)")
    let verbose =   Input.Option<bool>(["-v"; "--verbose"], description = "Verbose output", defaultValue = false)
    let outputMode = Input.Option<string>(["-m"; "--output-mode"], description = "Output mode: pinvoke or fidelity", defaultValue = "pinvoke")

    let handler (header, library, output, ns, includes, defines, verbose, outputMode) =
        let mode =
            match outputMode with
            | "fidelity" | "Fidelity" -> Farscape.Core.Types.Fidelity
            | _ -> Farscape.Core.Types.PInvoke
        let options = {
            HeaderFile = header
            LibraryName = library
            OutputDirectory = output
            Namespace = ns
            IncludePaths = includes |> Array.toList
            Defines = defines |> Array.toList
            Verbose = verbose
            OutputMode = mode
        }
        showHeader()
        showConfiguration options

        match runGeneration options with
        | Error errorMsg ->
            showError errorMsg
            1 // Exit with error code
        | Ok _ ->
            showNextSteps options
            0 // Success exit code

    command "generate" {
        description "Generate F# bindings for a native library"
        inputs (header, library, output, ns, includes, defines, verbose, outputMode)
        setHandler handler
    }

let moyaAnalyzeCommand =
    let header =
        Input.Option<FileInfo>(["-h"; "--header"],
            fun o ->
                o.Description <- "Path to C/C++ header file"
                o.IsRequired <- true
                o.AddValidator(fun result ->
                    let file = result.GetValueForOption<FileInfo> o
                    if not file.Exists then
                        result.ErrorMessage <- $"Header file not found: {file.FullName}"
                )
        )
    let library =   Input.OptionRequired<string>(["-l"; "--library"], description = "Library name (e.g., libc)")
    let includes =  Input.Option<string[]>(["-i"; "--include-paths"], description = "Additional include paths")
    let defines =   Input.Option<string[]>(["-d"; "--defines"], description = "Preprocessor definitions")
    let output =    Input.Option<string>(["-o"; "--output"], description = "Output directory (default: ./<library>)")
    let verbose =   Input.Option<bool>(["-v"; "--verbose"], description = "Verbose output", defaultValue = false)

    let handler (header: FileInfo, library, includes: string[], defines: string[], output, verbose) =
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
                printLine $"  {g.SuggestedName} ({g.Prefix}*): {g.FunctionNames.Length} functions"
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
        setHandler handler
    }

let moyaInitCommand =
    let library =   Input.OptionRequired<string>(["-l"; "--library"], description = "Library name")
    let header =    Input.Option<string>(["-h"; "--header"], description = "Path to header file", defaultValue = "")
    let output =    Input.Option<string>(["-o"; "--output"], description = "Output directory (default: ./<library>)")

    let handler (library, header, output) =
        let outputDir =
            if String.IsNullOrEmpty output then $"./{library}"
            else output
        Directory.CreateDirectory(outputDir) |> ignore

        let project : MoyaProject = {
            Library = {
                Name = library
                Header = if String.IsNullOrEmpty header then $"/usr/include/{library}.h" else header
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
        setHandler handler
    }

let moyaCommand =
    command "moya" {
        description "Namespace analysis and project file management"
        setHandler id
        addCommand moyaAnalyzeCommand
        addCommand moyaInitCommand
    }

let projectGenerateCommand =
    let project =   Input.OptionRequired<FileInfo>(["--project"], description = "Path to .moya.toml project file")
    let verbose =   Input.Option<bool>(["-v"; "--verbose"], description = "Verbose output", defaultValue = false)

    let handler (project: FileInfo, verbose) =
        showHeader ()
        printHeader "Generating Fidelity bindings from project..."
        printLine ""

        match BindingGenerator.generateFromProject project.FullName verbose with
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
        inputs (project, verbose)
        setHandler handler
    }

[<EntryPoint>]
let main argv =
    rootCommand argv {
        description "Farscape: F# Native Library Binding Generator"
        setHandler id
        addCommand generateCommand
        addCommand moyaCommand
        addCommand projectGenerateCommand
    }
