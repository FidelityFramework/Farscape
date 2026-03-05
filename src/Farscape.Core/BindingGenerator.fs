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
        /// Advisory messages for the developer (e.g., missing error conventions)
        Advisories: string list
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
            let generatedCode = FidelityCodeGenerator.generate declarations options.Namespace options.LibraryName options.DataModel Map.empty

            let lastSegment = options.Namespace.Split('.') |> Array.last
            let outputFileName = $"{lastSegment}.clef"
            let outputPath = Path.Combine(options.OutputDirectory, outputFileName)
            File.WriteAllText(outputPath, generatedCode)

            logVerbose $"Fidelity binding written to: {outputPath}" options.Verbose

            let wrapperFiles =
                if options.GenerateWrappers then
                    logVerbose "Generating idiomatic wrappers..." options.Verbose
                    let wrapperNamespace = $"{options.Namespace}.Api"
                    let wrapperCode =
                        WrapperCodeGenerator.generate declarations wrapperNamespace options.LibraryName options.Namespace WrapperTypes.NoErrors options.DataModel None
                    let wrapperPath = Path.Combine(options.OutputDirectory, $"{lastSegment}Api.clef")
                    File.WriteAllText(wrapperPath, wrapperCode)
                    logVerbose $"Wrapper module written to: {wrapperPath}" options.Verbose
                    [wrapperPath]
                else []

            Ok {
                OutputFiles = outputPath :: wrapperFiles
                DeclarationCount = declarations.Length
                Advisories = []
            }

    /// Generate a canonical .fidproj file for the binding library.
    /// Fully automatic — derives everything from what Farscape already knows.
    let private generateFidproj
        (project: PilotProject)
        (nsPrefix: string)
        (outputDir: string)
        (allFiles: string list)
        (verbose: bool) : string option =
        let packageName = nsPrefix
        // fidproj sits two levels up from the output directory
        // (output is e.g. .../CPU/Linux/x86_64/Bindings/Gtk3, fidproj goes at .../CPU/Linux/x86_64/)
        let fidprojDir = Path.GetDirectoryName(Path.GetDirectoryName(outputDir))
        let relativeSources =
            allFiles
            |> List.map (fun f ->
                let fullPath = Path.GetFullPath f
                let basePath = Path.GetFullPath fidprojDir
                if fullPath.StartsWith(basePath + "/") || fullPath.StartsWith(basePath + string Path.DirectorySeparatorChar) then
                    fullPath.Substring(basePath.Length + 1)
                elif fullPath.StartsWith(basePath) then
                    fullPath.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar, '/')
                else fullPath)

        let sb = System.Text.StringBuilder()
        sb.AppendLine("[package]") |> ignore
        sb.AppendLine($"name = \"{packageName}\"") |> ignore
        sb.AppendLine("version = \"0.1.0\"") |> ignore
        sb.AppendLine($"description = \"Generated bindings for {project.Library.Name}.\"") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[compilation]") |> ignore
        sb.AppendLine("target = \"cpu\"") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[build]") |> ignore
        sb.AppendLine("sources = [") |> ignore
        for src in relativeSources do
            sb.AppendLine($"    \"{src}\",") |> ignore
        sb.AppendLine("]") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[platform]") |> ignore
        sb.AppendLine("runtime_model = \"libc\"") |> ignore
        sb.AppendLine("os = \"linux\"") |> ignore
        sb.AppendLine("arch = \"x86_64\"") |> ignore
        sb.AppendLine("word_size = 64") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[dependencies]") |> ignore
        // If Fidelity.Platform.fidproj exists in the same directory (e.g., sub-libraries within
        // Fidelity.Platform), reference it directly. Otherwise, compute relative path to the
        // sibling repo following the standard layout: <repo>/CPU/Linux/x86_64/
        let platformFidprojLocal = Path.Combine(fidprojDir, "Fidelity.Platform.fidproj")
        let platformDepPath =
            if File.Exists(platformFidprojLocal) then
                "Fidelity.Platform.fidproj"
            else
                // Standard sibling repo layout: go up to repos root, then into Fidelity.Platform
                let reposDir = Path.GetFullPath(Path.Combine(fidprojDir, "../../../../"))
                let targetFidproj = Path.Combine(reposDir, "Fidelity.Platform/CPU/Linux/x86_64/Fidelity.Platform.fidproj")
                if File.Exists(targetFidproj) then
                    Path.GetRelativePath(fidprojDir, targetFidproj).Replace('\\', '/')
                else
                    // Absolute path as last resort
                    Path.GetFullPath(targetFidproj).Replace('\\', '/')
        sb.AppendLine($"Fidelity.Platform = {{ path = \"{platformDepPath}\" }}") |> ignore

        let content = sb.ToString()
        let fidprojName = $"{packageName}.fidproj"
        let fidprojPath = Path.Combine(fidprojDir, fidprojName)
        File.WriteAllText(fidprojPath, content)
        logVerbose $"Generated fidproj: {fidprojPath}" verbose
        Some fidprojPath

    /// Derive the common namespace prefix from project namespace names.
    /// e.g., ["Fidelity.ROCm.Device"; "Fidelity.ROCm.Memory"] → "Fidelity.ROCm"
    /// Single-namespace with ≤2 segments (e.g., "Fidelity.GBM") → use the full name.
    let private deriveNamespacePrefix (project: PilotProject) : string =
        match project.Namespaces with
        | [] -> $"Fidelity.{project.Library.Name}"
        | [single] -> single.Name
        | first :: rest ->
            // Find common prefix across all namespaces
            let allNames = first.Name :: (rest |> List.map (fun ns -> ns.Name))
            let segments = first.Name.Split('.')
            let commonLength =
                segments
                |> Array.indexed
                |> Array.takeWhile (fun (i, seg) ->
                    allNames |> List.forall (fun name ->
                        let parts = name.Split('.')
                        i < parts.Length && parts.[i] = seg))
                |> Array.length
            if commonLength >= 1 then
                segments.[..commonLength-1] |> String.concat "."
            else first.Name

    /// Generate scoped Fidelity bindings from a .pilot.toml project file.
    /// Each [[namespace]] section produces a subfolder with Types.clef + functions .clef.
    /// Shared types (referenced by 2+ namespaces) go into a root Types.clef.
    /// Supports multi-header projects: parses each header independently, merges with dedup.
    /// When generateWrappers is true, also generates Layer 2 idiomatic wrappers.
    let generateFromProject (projectPath: string) (verbose: bool) (generateWrappers: bool) (dataModel: PlatformABI) : Result<GenerationResult, string> =
        match PilotSerializer.loadFromFile projectPath with
        | Error e -> Error $"Failed to load project: {e}"
        | Ok project ->
            // Parse C/C++ headers
            let headerResults =
                project.Library.Headers |> List.map (fun headerPath ->
                    logVerbose $"Parsing header: {headerPath}" verbose
                    let includeRoot =
                        let fullPath = Path.GetFullPath headerPath
                        project.Library.IncludePaths
                        |> List.tryFind (fun ip ->
                            let fullIp = Path.GetFullPath ip
                            fullPath.StartsWith fullIp)
                    CppParser.parseWithIncludeRoot headerPath project.Library.IncludePaths project.Library.Defines includeRoot project.Library.MacroPrefixes verbose)

            let headerErrors = headerResults |> List.choose (function Error e -> Some e | _ -> None)
            if not headerErrors.IsEmpty then
                let msg = String.concat "; " headerErrors
                Error $"Failed to parse headers: {msg}"

            // Parse XML protocol files (protocol-defined APIs with marshal dispatch)
            else
            let xmlResults =
                project.Library.XmlProtocols |> List.map (fun xmlPath ->
                    logVerbose $"Parsing XML protocol: {xmlPath}" verbose
                    ProtocolParser.parseFile xmlPath)

            let xmlErrors = xmlResults |> List.choose (function Error e -> Some e | _ -> None)
            if not xmlErrors.IsEmpty then
                let msg = String.concat "; " xmlErrors
                Error $"Failed to parse XML protocols: {msg}"
            else

                let headerDeclLists = headerResults |> List.choose (function Ok d -> Some d | _ -> None)
                let xmlProtocols = xmlResults |> List.choose (function Ok p -> Some p | _ -> None)

                // Split XML protocol output: type declarations flow through FidelityCodeGenerator,
                // request implementations are FsDecl with marshal call bodies (injected later)
                let marshalConfig =
                    match project.ProtocolConfig with
                    | Some cfg -> cfg
                    | None ->
                        // Default: Wayland-style marshal dispatch
                        ({ MarshalFunction = "wl_proxy_marshal_array_flags"
                           MarshalModule = "Fidelity.Wayland.Core"
                           VersionFunction = "wl_proxy_get_version"
                           InterfaceResolution = "dlsym"
                           DestroyFlag = 1u } : PilotTypes.ProtocolConfig)

                let xmlTypeDecls = xmlProtocols |> List.map ProtocolParser.toTypeDeclarations
                let xmlRequestDecls = xmlProtocols |> List.collect (fun p -> ProtocolParser.toRequestDecls p marshalConfig)

                let allDeclLists = headerDeclLists @ xmlTypeDecls
                let declarations = DeclarationAlgebra.mergeDeclarations allDeclLists
                let sourceCount = project.Library.Headers.Length + project.Library.XmlProtocols.Length
                logVerbose $"Merged {declarations.Length} declarations from {sourceCount} source(s) ({xmlRequestDecls.Length} protocol request implementations)" verbose

                // Resolve output directory relative to the project file location
                let projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))
                let outputDir =
                    if Path.IsPathRooted(project.Output.Directory) then
                        project.Output.Directory
                    else
                        Path.GetFullPath(Path.Combine(projectDir, project.Output.Directory))
                Directory.CreateDirectory(outputDir) |> ignore

                // Build generation context once from the full declaration list
                let structLayouts =
                    let abiStructNames =
                        match project.Options with
                        | Some opts when not opts.AbiCriticalStructs.IsEmpty -> opts.AbiCriticalStructs
                        | _ -> []
                    if abiStructNames.IsEmpty then Map.empty
                    else
                        let headerFile = project.Library.Headers |> List.head
                        match CppParser.extractStructLayouts headerFile project.Library.IncludePaths project.Library.Defines abiStructNames verbose with
                        | Ok layouts ->
                            logVerbose $"Extracted layouts for {layouts.Count} ABI-critical struct(s)" verbose
                            layouts
                        | Error e ->
                            logVerbose $"Warning: struct layout extraction failed: {e}" verbose
                            Map.empty

                let ctx =
                    let baseCtx = FidelityCodeGenerator.buildGenerationContext declarations dataModel structLayouts
                    { baseCtx with NonnullAnnotations = project.Nonnull }

                // Derive common namespace prefix for the project
                let nsPrefix = deriveNamespacePrefix project

                // Determine error handling strategy from convention configuration
                let errorHandling =
                    match project.ErrorConventions with
                    | Some spec ->
                        match spec.Default with
                        | PilotTypes.Errno ->
                            WrapperTypes.UseErrno $"{nsPrefix}.Errno"
                        | PilotTypes.EnumErrorCode (errorType, successValue, _, _) ->
                            let structName = EnumErrorModuleGenerator.deriveErrorStructName errorType
                            WrapperTypes.UseEnumError (errorType, successValue, structName,
                                                      $"{nsPrefix}.{structName}")
                        | PilotTypes.NullWithReason reasonFn ->
                            WrapperTypes.UseNullWithReason reasonFn
                        | _ -> WrapperTypes.NoErrors
                    | None -> WrapperTypes.NoErrors

                // Classify types across namespaces: shared vs local
                let classification = PilotAnalyzer.classifyProjectTypes project.Namespaces declarations
                let sharedCount = classification.SharedTypes.Count
                let localCounts = classification.LocalTypes |> Map.toList |> List.sumBy (fun (_, s) -> s.Count)
                logVerbose $"Type classification: {sharedCount} shared, {localCounts} local across {project.Namespaces.Length} namespaces" verbose

                // ── Shared Types.clef (root level) ──────────────────────────
                let sharedTypesNs = $"{nsPrefix}.Types"
                let sharedTypeDecls = PilotAnalyzer.filterTypesOnly classification.SharedTypes declarations
                let sharedHandles = Set.intersect ctx.OpaqueHandles classification.SharedTypes
                let sharedCode =
                    FidelityCodeGenerator.generateModule ctx sharedHandles sharedTypeDecls
                        sharedTypesNs project.Library.Name "Shared type definitions" []
                let sharedTypesPath = Path.Combine(outputDir, "Types.clef")
                File.WriteAllText(sharedTypesPath, sharedCode)
                logVerbose $"Shared types module: {sharedTypesPath} ({classification.SharedTypes.Count} types)" verbose

                // ── Error module ────────────────────────────────────────────
                let errorModuleFiles =
                    match errorHandling with
                    | WrapperTypes.UseEnumError (errorType, _, _, _) ->
                        let errorEnum =
                            declarations |> List.tryPick (function
                                | CppParser.Declaration.Enum e when e.Name = errorType -> Some e
                                | _ -> None)
                        match errorEnum with
                        | Some enumDecl ->
                            match project.ErrorConventions with
                            | Some spec ->
                                match spec.Default with
                                | PilotTypes.EnumErrorCode (et, sv, esFn, enFn) ->
                                    let config = EnumErrorModuleGenerator.makeConfig et sv esFn enFn
                                    let errorNs = $"{nsPrefix}.{config.ErrorStructName}"
                                    match EnumErrorModuleGenerator.generate enumDecl config errorNs with
                                    | Some output ->
                                        let errorPath = Path.Combine(outputDir, $"{config.ErrorStructName}.clef")
                                        File.WriteAllText(errorPath, output)
                                        logVerbose $"Error module: {errorPath}" verbose
                                        [errorPath]
                                    | None -> []
                                | _ -> []
                            | None -> []
                        | None ->
                            logVerbose $"Warning: error enum '{errorType}' not found in declarations" verbose
                            []
                    | _ -> []

                // ── BAREWire descriptors ─────────────────────────────────────
                let descriptorFiles =
                    let generateDescriptors =
                        match project.Options with
                        | Some opts -> opts.GenerateDescriptors && not structLayouts.IsEmpty
                        | None -> false
                    if generateDescriptors then
                        let abiStructDecls =
                            declarations |> List.choose (function
                                | CppParser.Declaration.Struct s when Map.containsKey s.Name structLayouts ->
                                    Some (s, structLayouts.[s.Name])
                                | _ -> None)
                        if abiStructDecls.IsEmpty then []
                        else
                            let descriptorNs = $"{nsPrefix}.Descriptors"
                            let descriptorCode =
                                DescriptorGenerator.generate abiStructDecls descriptorNs
                                    ctx.TypedefMap dataModel ctx.OpaqueHandles
                            let descriptorPath = Path.Combine(outputDir, "Descriptors.clef")
                            File.WriteAllText(descriptorPath, descriptorCode)
                            logVerbose $"Descriptor module: {descriptorPath}" verbose
                            [descriptorPath]
                    else []

                // ── Callback wrappers ──────────────────────────────────────
                let callbackFiles =
                    if generateWrappers then
                        // Use TOML-specified callbacks if present, otherwise auto-discover
                        let callbackSpec =
                            match project.Callbacks with
                            | Some spec -> spec
                            | None ->
                                let rawSpec = PilotAnalyzer.discoverCallbacks declarations
                                // Filter out transitively-leaked callbacks that don't belong to this library
                                let claimedFunctions =
                                    project.Namespaces |> List.collect (fun ns ->
                                        PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                                        |> List.choose (function
                                            | CppParser.Declaration.Function f -> Some f.Name
                                            | _ -> None))
                                    |> Set.ofList
                                { rawSpec with
                                    Registrations = rawSpec.Registrations |> List.filter (fun r -> claimedFunctions.Contains r.Function) }
                        if callbackSpec.Registrations.IsEmpty && callbackSpec.ListenerStructs.IsEmpty then
                            logVerbose "No callback patterns detected" verbose
                            []
                        else
                            let callbackNs = $"{nsPrefix}.Callbacks"
                            logVerbose $"Callback patterns: {callbackSpec.Registrations.Length} registration(s), {callbackSpec.ListenerStructs.Length} listener struct(s)" verbose
                            match CallbackWrapperGenerator.generate callbackSpec declarations callbackNs nsPrefix dataModel with
                            | Some output ->
                                let callbackPath = Path.Combine(outputDir, "Callbacks.clef")
                                File.WriteAllText(callbackPath, output)
                                logVerbose $"Callback module: {callbackPath}" verbose
                                [callbackPath]
                            | None -> []
                    else []

                // ── Namespace subfolders ─────────────────────────────────────
                let nsFiles =
                    project.Namespaces |> List.collect (fun ns ->
                        let lastSegment = ns.Name.Split('.') |> Array.last
                        let nsDir = Path.Combine(outputDir, lastSegment)
                        Directory.CreateDirectory(nsDir) |> ignore

                        let localTypeNames =
                            match Map.tryFind ns.Name classification.LocalTypes with
                            | Some names -> names
                            | None -> Set.empty

                        // ── Local Types.clef (if namespace has local types) ──
                        let localTypesNs = $"{ns.Name}.Types"
                        let localTypeFiles =
                            if localTypeNames.IsEmpty then []
                            else
                                let localTypeDecls = PilotAnalyzer.filterTypesOnly localTypeNames declarations
                                let localHandles = Set.intersect ctx.OpaqueHandles localTypeNames
                                let localCode =
                                    FidelityCodeGenerator.generateModule ctx localHandles localTypeDecls
                                        localTypesNs ns.Library "Local type definitions" [sharedTypesNs]
                                let localTypesPath = Path.Combine(nsDir, "Types.clef")
                                File.WriteAllText(localTypesPath, localCode)
                                logVerbose $"  {lastSegment}/Types.clef ({localTypeNames.Count} types)" verbose
                                [localTypesPath]

                        // ── Functions .clef ──────────────────────────────────
                        let funcOpenModules =
                            let baseModules =
                                if localTypeNames.IsEmpty then [sharedTypesNs]
                                else [sharedTypesNs; localTypesNs]
                            // Add marshal module if this namespace uses protocol requests
                            match project.ProtocolConfig with
                            | Some cfg when not ns.XmlInterfaces.IsEmpty ->
                                if List.contains cfg.MarshalModule baseModules then baseModules
                                else baseModules @ [cfg.MarshalModule]
                            | _ -> baseModules
                        let funcDecls =
                            PilotAnalyzer.filterDeclarationsWithTypes ns Set.empty declarations
                        let funcCount =
                            funcDecls |> List.filter (function CppParser.Declaration.Function _ -> true | _ -> false) |> List.length
                        let funcCode =
                            FidelityCodeGenerator.generateModule ctx Set.empty funcDecls
                                ns.Name ns.Library $"{lastSegment} function declarations" funcOpenModules

                        // Append protocol request implementations for this namespace's XML interfaces
                        let nsRequestDecls =
                            if ns.XmlInterfaces.IsEmpty then []
                            else
                                xmlRequestDecls |> List.filter (fun decl ->
                                    match decl with
                                    | CodeAST.LetBinding(name, _, _, _, _) ->
                                        ns.XmlInterfaces |> List.exists (fun iface ->
                                            name.StartsWith(iface + "_"))
                                    | _ -> false)
                        let funcCode =
                            if nsRequestDecls.IsEmpty then funcCode
                            else
                                let requestCode =
                                    nsRequestDecls
                                    |> List.map CodeRenderer.render
                                    |> String.concat "\n"
                                funcCode + "\n    // ── Protocol request implementations ──\n\n" + requestCode

                        let funcPath = Path.Combine(nsDir, $"{lastSegment}.clef")
                        File.WriteAllText(funcPath, funcCode)
                        logVerbose $"  {lastSegment}/{lastSegment}.clef ({funcCount} functions, {nsRequestDecls.Length} protocol requests)" verbose

                        // ── Wrappers (optional) ─────────────────────────────
                        let wrapperFiles =
                            if generateWrappers then
                                let allNsDecls = PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                                let wrapperNamespace = $"{ns.Name}.Api"
                                let wrapperCode =
                                    WrapperCodeGenerator.generate allNsDecls wrapperNamespace ns.Library ns.Name errorHandling dataModel project.Nonnull
                                let wrapperPath = Path.Combine(nsDir, $"{lastSegment}Api.clef")
                                File.WriteAllText(wrapperPath, wrapperCode)
                                logVerbose $"  {lastSegment}/{lastSegment}Api.clef" verbose
                                [wrapperPath]
                            else []

                        localTypeFiles @ [funcPath] @ wrapperFiles)

                let allFiles = [sharedTypesPath] @ errorModuleFiles @ descriptorFiles @ callbackFiles @ nsFiles

                // Generate canonical fidproj for the binding library
                let fidprojFile = generateFidproj project nsPrefix outputDir allFiles verbose

                let advisories =
                    match errorHandling, generateWrappers with
                    | WrapperTypes.NoErrors, true ->
                        [$"No [error_conventions] defined for '{project.Library.Name}'. Layer 2 wrappers use direct passthrough. If this library reports errors through a query function, out-parameter, or other mechanism, add error handling in an Overlay module."]
                    | WrapperTypes.UseNullWithReason reasonFn, true ->
                        [$"Using null_with_reason convention for '{project.Library.Name}'. Functions returning pointers will call {reasonFn}() on null and wrap in Result<nativeint, nativeint>."]
                    | _ -> []

                let allOutputFiles =
                    match fidprojFile with
                    | Some fp -> allFiles @ [fp]
                    | None -> allFiles

                Ok {
                    OutputFiles = allOutputFiles
                    DeclarationCount = declarations.Length
                    Advisories = advisories
                }
