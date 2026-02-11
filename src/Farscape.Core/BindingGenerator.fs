namespace Farscape.Core

open System.IO
open ProjectOptions
open CodeGenerator
open Types
open MoyaTypes


module BindingGenerator =

    type GenerationOptions = {
        HeaderFile: FileInfo
        LibraryName: string
        OutputDirectory: string
        Namespace: string
        IncludePaths: string list
        Defines: string list
        Verbose: bool
        OutputMode: OutputMode
        GenerateWrappers: bool
    }

    /// Extract struct/class type names from declarations using catamorphism.
    let extractStructTypes (declarations: CppParser.Declaration list) : string list =
        DeclarationAlgebra.cataDeclarations DeclarationAlgebra.structNameAlgebra declarations
        |> List.choose id
        |> List.distinct

    let logVerbose (message: string) (verbose: bool) =
        if verbose then
            printfn "%s" message

    /// Result type for binding generation
    type GenerationResult = {
        OutputFiles: string list
        DeclarationCount: int
    }

    /// Generate F# bindings from a C/C++ header file
    /// Returns Result to enforce proper error handling - fails fast on parse errors
    let generateBindings (options: GenerationOptions) : Result<GenerationResult, string> =
        logVerbose $"Starting binding generation for {options.HeaderFile}" options.Verbose
        logVerbose $"Target library: {options.LibraryName}" options.Verbose
        logVerbose $"Output directory: {options.OutputDirectory}" options.Verbose
        logVerbose $"Namespace: {options.Namespace}" options.Verbose
        logVerbose $"Output mode: {options.OutputMode}" options.Verbose

        logVerbose "Parsing header file..." options.Verbose

        match CppParser.parseWithDefines options.HeaderFile.FullName options.IncludePaths options.Defines options.Verbose with
        | Error parseError ->
            Error $"Failed to parse header: {parseError}"
        | Ok declarations ->
            logVerbose $"Successfully parsed {declarations.Length} declarations" options.Verbose

            Directory.CreateDirectory(options.OutputDirectory) |> ignore

            match options.OutputMode with
            | Fidelity ->
                logVerbose "Generating Fidelity F# source..." options.Verbose
                let generatedCode = FidelityCodeGenerator.generate declarations options.Namespace options.LibraryName

                let lastSegment = options.Namespace.Split('.') |> Array.last
                let outputFileName = $"{lastSegment}.fs"
                let outputPath = Path.Combine(options.OutputDirectory, outputFileName)
                File.WriteAllText(outputPath, generatedCode)

                logVerbose $"Fidelity binding written to: {outputPath}" options.Verbose

                let wrapperFiles =
                    if options.GenerateWrappers then
                        logVerbose "Generating idiomatic F# wrappers..." options.Verbose
                        let wrapperNamespace = $"{options.Namespace}.Wrappers"
                        let wrapperCode =
                            WrapperCodeGenerator.generate declarations wrapperNamespace options.LibraryName options.Namespace None
                        let wrapperPath = Path.Combine(options.OutputDirectory, $"{lastSegment}Wrappers.fs")
                        File.WriteAllText(wrapperPath, wrapperCode)
                        logVerbose $"Wrapper module written to: {wrapperPath}" options.Verbose
                        [wrapperPath]
                    else []

                Ok {
                    OutputFiles = outputPath :: wrapperFiles
                    DeclarationCount = declarations.Length
                }

            | PInvoke ->
                logVerbose "Generating P/Invoke F# code..." options.Verbose
                let generatedCode = generateCode declarations options.Namespace options.LibraryName

                logVerbose "Creating project files..." options.Verbose
                let projectOptions : ProjectOptions = {
                    ProjectName = options.LibraryName
                    Namespace = options.Namespace
                    OutputDirectory = options.OutputDirectory
                    References = []
                    NuGetPackages = [
                        ("System.Memory", "4.5.5")
                        ("System.Runtime.CompilerServices.Unsafe", "6.0.0")
                    ]
                    HeaderFile = options.HeaderFile.FullName
                    LibraryName = options.LibraryName
                    IncludePaths = options.IncludePaths
                    Verbose = options.Verbose
                }

                let (solutionPath, libraryPath, testPath) = Project.generateProject projectOptions generatedCode

                logVerbose "Binding generation completed successfully." options.Verbose

                Ok {
                    OutputFiles = [solutionPath; libraryPath; testPath]
                    DeclarationCount = declarations.Length
                }

    /// Generate scoped Fidelity bindings from a .moya.toml project file.
    /// Each [[namespace]] section produces a separate F# module.
    /// Supports multi-header projects: parses each header independently, merges with dedup.
    /// When generateWrappers is true, also generates Layer 2 idiomatic wrappers.
    let generateFromProject (projectPath: string) (verbose: bool) (generateWrappers: bool) : Result<GenerationResult, string> =
        match MoyaSerializer.loadFromFile projectPath with
        | Error e -> Error $"Failed to load project: {e}"
        | Ok project ->
            let parseResults =
                project.Library.Headers |> List.map (fun headerPath ->
                    logVerbose $"Parsing header: {headerPath}" verbose
                    CppParser.parseWithDefines headerPath project.Library.IncludePaths project.Library.Defines verbose)

            let errors = parseResults |> List.choose (function Error e -> Some e | _ -> None)
            if not errors.IsEmpty then
                let msg = String.concat "; " errors
                Error $"Failed to parse headers: {msg}"
            else
                let allDeclLists = parseResults |> List.choose (function Ok d -> Some d | _ -> None)
                let declarations = DeclarationAlgebra.mergeDeclarations allDeclLists
                logVerbose $"Merged {declarations.Length} declarations from {project.Library.Headers.Length} header(s)" verbose

                Directory.CreateDirectory(project.Output.Directory) |> ignore

                // Determine errno module name from error convention configuration
                let errnoModuleName =
                    match project.ErrorConventions with
                    | Some spec when spec.Default = MoyaTypes.Errno ->
                        Some $"Fidelity.{project.Library.Name}.Errno"
                    | _ -> None

                let allFiles =
                    project.Namespaces |> List.collect (fun ns ->
                        let filtered = MoyaAnalyzer.filterDeclarationsForNamespace ns declarations
                        logVerbose $"Namespace {ns.Name}: {filtered.Length} declarations" verbose
                        let code = FidelityCodeGenerator.generate filtered ns.Name ns.Library
                        let lastSegment = ns.Name.Split('.') |> Array.last
                        let fileName = $"{lastSegment}.fs"
                        let outputPath = Path.Combine(project.Output.Directory, fileName)
                        File.WriteAllText(outputPath, code)

                        if generateWrappers then
                            let wrapperNamespace = $"{ns.Name}.Wrappers"
                            let wrapperCode =
                                WrapperCodeGenerator.generate filtered wrapperNamespace ns.Library ns.Name errnoModuleName
                            let wrapperPath = Path.Combine(project.Output.Directory, $"{lastSegment}Wrappers.fs")
                            File.WriteAllText(wrapperPath, wrapperCode)
                            logVerbose $"Wrapper module: {wrapperPath}" verbose
                            [outputPath; wrapperPath]
                        else
                            [outputPath])

                Ok {
                    OutputFiles = allFiles
                    DeclarationCount = declarations.Length
                }