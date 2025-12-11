module Farscape.Cli.Program

open System
open System.IO
open Farscape.Core
open FSharp.SystemCommandLine
open System.CommandLine.Invocation
open Farscape.Core.BindingGenerator

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
        printColorLine $"Output: F# bindings generated in {options.OutputDirectory}" ConsoleColor.Cyan
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

    let handler (header, library, output, ns, includes, defines, verbose) =
        let options = {
            HeaderFile = header
            LibraryName = library
            OutputDirectory = output
            Namespace = ns
            IncludePaths = includes |> Array.toList
            Defines = defines |> Array.toList
            Verbose = verbose
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
        inputs (header, library, output, ns, includes, defines, verbose)
        setHandler handler
    }

[<EntryPoint>]
let main argv =
    rootCommand argv {
        description "Farscape: F# Native Library Binding Generator"
        setHandler id
        addCommand generateCommand
    }
