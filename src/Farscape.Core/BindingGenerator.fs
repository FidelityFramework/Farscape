namespace Farscape.Core

open System.IO
open Types
open PilotTypes


module BindingGenerator =

    type GenerationOptions = {
        HeaderFile: FileInfo
        LibraryName: string
        OutputDirectory: string
        Namespace: string
        IncludePaths: string list
        Defines: string list
        Verbose: bool
        GenerateWrappers: bool
        /// Platform ABI for type width resolution (C int/long).
        DataModel: PlatformABI
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

    /// Generate Clef bindings from a C/C++ header file.
    /// Returns Result to enforce proper error handling - fails fast on parse errors.
    let generateBindings (options: GenerationOptions) : Result<GenerationResult, string> =
        logVerbose $"Starting binding generation for {options.HeaderFile}" options.Verbose
        logVerbose $"Target library: {options.LibraryName}" options.Verbose
        logVerbose $"Output directory: {options.OutputDirectory}" options.Verbose
        logVerbose $"Namespace: {options.Namespace}" options.Verbose

        logVerbose "Parsing header file..." options.Verbose

        match CppParser.parseWithDefines options.HeaderFile.FullName options.IncludePaths options.Defines options.Verbose with
        | Error parseError ->
            Error $"Failed to parse header: {parseError}"
        | Ok declarations ->
            logVerbose $"Successfully parsed {declarations.Length} declarations" options.Verbose

            Directory.CreateDirectory(options.OutputDirectory) |> ignore

            logVerbose "Generating Fidelity Clef source..." options.Verbose
            let generatedCode = FidelityCodeGenerator.generate declarations options.Namespace options.LibraryName options.DataModel

            let lastSegment = options.Namespace.Split('.') |> Array.last
            let outputFileName = $"{lastSegment}.fs"
            let outputPath = Path.Combine(options.OutputDirectory, outputFileName)
            File.WriteAllText(outputPath, generatedCode)

            logVerbose $"Fidelity binding written to: {outputPath}" options.Verbose

            let wrapperFiles =
                if options.GenerateWrappers then
                    logVerbose "Generating idiomatic wrappers..." options.Verbose
                    let wrapperNamespace = $"{options.Namespace}.Wrappers"
                    let wrapperCode =
                        WrapperCodeGenerator.generate declarations wrapperNamespace options.LibraryName options.Namespace None options.DataModel
                    let wrapperPath = Path.Combine(options.OutputDirectory, $"{lastSegment}Wrappers.fs")
                    File.WriteAllText(wrapperPath, wrapperCode)
                    logVerbose $"Wrapper module written to: {wrapperPath}" options.Verbose
                    [wrapperPath]
                else []

            Ok {
                OutputFiles = outputPath :: wrapperFiles
                DeclarationCount = declarations.Length
            }

    /// Generate scoped Fidelity bindings from a .pilot.toml project file.
    /// Each [[namespace]] section produces a separate Clef module.
    /// Supports multi-header projects: parses each header independently, merges with dedup.
    /// When generateWrappers is true, also generates Layer 2 idiomatic wrappers.
    let generateFromProject (projectPath: string) (verbose: bool) (generateWrappers: bool) (dataModel: PlatformABI) : Result<GenerationResult, string> =
        match PilotSerializer.loadFromFile projectPath with
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
                    | Some spec when spec.Default = PilotTypes.Errno ->
                        Some $"Fidelity.{project.Library.Name}.Errno"
                    | _ -> None

                let allFiles =
                    project.Namespaces |> List.collect (fun ns ->
                        let filtered = PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                        logVerbose $"Namespace {ns.Name}: {filtered.Length} declarations" verbose
                        let code = FidelityCodeGenerator.generate filtered ns.Name ns.Library dataModel
                        let lastSegment = ns.Name.Split('.') |> Array.last
                        let fileName = $"{lastSegment}.fs"
                        let outputPath = Path.Combine(project.Output.Directory, fileName)
                        File.WriteAllText(outputPath, code)

                        if generateWrappers then
                            let wrapperNamespace = $"{ns.Name}.Wrappers"
                            let wrapperCode =
                                WrapperCodeGenerator.generate filtered wrapperNamespace ns.Library ns.Name errnoModuleName dataModel
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
