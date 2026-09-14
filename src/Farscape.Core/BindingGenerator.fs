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

    /// Locate a platform dependency when a pilot lives in a nested experimental tree.
    let rec private findProjectAbove directory fileName =
        let candidate = Path.Combine(directory, fileName)
        if File.Exists candidate then Some candidate
        else
            let parent = Directory.GetParent directory
            if isNull parent then None else findProjectAbove parent.FullName fileName

    /// Generate a canonical .fidproj file for the binding library.
    /// Fully automatic — derives everything from what Farscape already knows.
    let private generateFidproj
        (project: PilotProject)
        (nsPrefix: string)
        (outputDir: string)
        (allFiles: string list)
        (verbose: bool) : string option =
        let packageName = nsPrefix
        // fidproj sits two levels up from the output directory when nested in
        // a platform tree (e.g. .../CPU/Linux/x86_64/Bindings/Gtk3 → .../CPU/Linux/x86_64/).
        // For flat output directories (e.g. /tmp/farscape-xrt), use outputDir directly.
        let fidprojDir =
            let candidate = Path.GetDirectoryName(Path.GetDirectoryName(outputDir))
            if System.String.IsNullOrEmpty(candidate) || candidate = "/" || candidate = Path.GetPathRoot(outputDir) then
                outputDir
            else
                candidate
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
        let description =
            if project.Options |> Option.exists (fun opts -> opts.NativePointerSurface) then
                $"Experimental legacy {project.Library.Name} ABI surface; not current Clef source."
            else $"Generated bindings for {project.Library.Name}."
        sb.AppendLine($"description = \"{description}\"") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[compilation]") |> ignore
        sb.AppendLine("target = \"cpu\"") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[build]") |> ignore
        sb.AppendLine("output_kind = \"library\"") |> ignore
        sb.AppendLine("sources = [") |> ignore
        for src in relativeSources do
            sb.AppendLine($"    \"{src}\",") |> ignore
        sb.AppendLine("]") |> ignore
        sb.AppendLine() |> ignore
        if project.Options |> Option.exists (fun o -> o.LinkLibraries) then
            let signalLibraries = if project.Namespaces |> List.exists (fun n -> not n.Signals.IsEmpty) then [ "gobject-2.0" ] else []
            let libraries = (project.Namespaces |> List.map (fun n -> n.Library)) @ signalLibraries |> List.filter ((<>) "c") |> List.distinct
            if not libraries.IsEmpty then
                sb.AppendLine("[link]") |> ignore
                let values = libraries |> List.map (fun name -> "\"" + name + "\"") |> String.concat ", "
                sb.AppendLine($"libraries = [{values}]") |> ignore
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
        let platformFidprojLocal = findProjectAbove fidprojDir "Fidelity.Platform.fidproj"
        let platformDepPath =
            match platformFidprojLocal with
            | Some path -> Path.GetRelativePath(fidprojDir, path).Replace('\\', '/')
            | None ->
                // Standard sibling repo layout: go up to repos root, then into Fidelity.Platform
                let reposDir = Path.GetFullPath(Path.Combine(fidprojDir, "../../../../"))
                let targetFidproj = Path.Combine(reposDir, "Fidelity.Platform/Environments/Linux/x86_64/Fidelity.Platform.fidproj")
                if File.Exists(targetFidproj) then
                    Path.GetRelativePath(fidprojDir, targetFidproj).Replace('\\', '/')
                else
                    // Absolute path as last resort
                    Path.GetFullPath(targetFidproj).Replace('\\', '/')
        let descriptorOnly = project.Options |> Option.exists (fun opts -> opts.DescriptorOnlyDependencies)
        if not descriptorOnly then
            sb.AppendLine($"Fidelity.Platform = {{ path = \"{platformDepPath}\" }}") |> ignore
        // The descriptors reference BAREWire's Hardware and Descriptors vocabularies
        let bareWireDepPath =
            let platformDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fidprojDir, platformDepPath)))
            let reposDir = Path.GetFullPath(Path.Combine(platformDirectory, "../../../../"))
            let metadataProject = if descriptorOnly then "BAREWire.BindingMetadata.fidproj" else "BAREWire.fidproj"
            let targetFidproj = Path.Combine(reposDir, "BAREWire/src", metadataProject)
            if File.Exists(targetFidproj) then
                Path.GetRelativePath(fidprojDir, targetFidproj).Replace('\\', '/')
            else
                Path.GetFullPath(targetFidproj).Replace('\\', '/')
        sb.AppendLine($"BAREWire = {{ path = \"{bareWireDepPath}\" }}") |> ignore

        let content = sb.ToString()
        let fidprojName = $"{packageName}.fidproj"
        let fidprojPath = Path.Combine(fidprojDir, fidprojName)
        File.WriteAllText(fidprojPath, content)
        logVerbose $"Generated fidproj: {fidprojPath}" verbose
        Some fidprojPath

    /// Derive the common namespace prefix from project namespace names.
    /// e.g., ["Fidelity.ROCm.Device"; "Fidelity.ROCm.Memory"] → "Fidelity.ROCm"
    /// Single-namespace with ≤2 segments (e.g., "Fidelity.GBM") → use the full name.
    /// Derive a PascalCase library prefix from the library name for naming
    /// error helper functions (e.g. "xrt_coreutil" → "Xrt", "amdhip64" → "Amdhip").
    let private deriveLibraryPrefix (libraryName: string) : string =
        let cleaned =
            libraryName.Split([|'_'; '-'; '.'|], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.head  // take first segment
        if cleaned.Length = 0 then "Lib"
        else System.Char.ToUpper(cleaned.[0]).ToString() + cleaned.[1..].ToLower()

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
                    if project.Options |> Option.exists (fun opts -> opts.NativePointerSurface || opts.CHeaderMode) then
                        // Native carrier pilots bind the C ABI. pthread.h also contains guarded
                        // C++ cleanup classes, which must not turn its public unions into C++ PODs.
                        CppParser.parseHeader {
                            HeaderFile = headerPath; IncludePaths = project.Library.IncludePaths
                            Defines = project.Library.Defines; IncludeRoot = includeRoot
                            MacroPrefixes = project.Library.MacroPrefixes; IncludeMacros = true
                            Verbose = verbose; CppMode = false }
                    else
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

                // GObject introspection data: the source of signal entry signatures (IntrospectionParser)
                let introspection =
                    if project.Library.Introspection.IsEmpty then None
                    else
                        match IntrospectionParser.load project.Library.Introspection with
                        | Ok repo -> Some repo
                        | Error e -> failwith e

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

                let typedProtocol = project.Options |> Option.exists (fun o -> o.TypedProtocol)
                let xmlTypeDecls = if typedProtocol then [] else xmlProtocols |> List.map ProtocolParser.toTypeDeclarations

                let allDeclLists = headerDeclLists @ xmlTypeDecls
                let declarations = DeclarationAlgebra.mergeDeclarations allDeclLists
                let projections = project.Options |> Option.map (fun opts -> opts.Bindings) |> Option.defaultValue []
                let aliases = projections |> List.choose (fun binding ->
                    let original = declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = binding.Symbol -> Some f | _ -> None)
                    match original with
                    | None -> failwith $"Binding projection '{binding.Name}' requires C symbol '{binding.Symbol}' in the parsed headers"
                    | Some original ->
                        for index in binding.ReferenceParameters @ binding.ReadOnlyReferenceParameters @ binding.NonnullCallbacks @ binding.StringParameters do
                            if index < 0 || index >= original.Parameters.Length then failwith $"Binding projection '{binding.Name}' has invalid parameter index {index}"
                        if binding.ReferenceParameters |> List.exists (fun index -> List.contains index binding.ReadOnlyReferenceParameters) then
                            failwith $"Binding projection '{binding.Name}' declares a parameter both writable and read-only"
                        if binding.ConstantParameters.Length > original.Parameters.Length then failwith $"Binding projection '{binding.Name}' has too many constant parameters"
                        if binding.ReferenceElements.Length > original.Parameters.Length then failwith $"Binding projection '{binding.Name}' has too many reference elements"
                        if binding.ParameterHandles.Length > original.Parameters.Length then failwith $"Binding projection '{binding.Name}' has too many parameter handles"
                        if binding.Name = binding.Symbol then None
                        else Some (CppParser.Declaration.Function { original with Name = binding.Name }))
                let declarations = declarations @ aliases
                let sourceCount = project.Library.Headers.Length + project.Library.XmlProtocols.Length
                logVerbose $"Merged {declarations.Length} declarations from {sourceCount} source(s)" verbose

                // Diagnostic: summarize C++ class extraction (verbose only)
                if verbose then
                    let classDecls = declarations |> List.choose (function CppParser.Declaration.Class c -> Some c | _ -> None)
                    logVerbose $"C++ classes in merged declarations: {classDecls.Length}" verbose
                    for c in classDecls do
                        let classification = CppClassAnalysis.classifyClass c
                        logVerbose $"  class: {c.Name} (fields={c.Fields.Length}, methods={c.Methods.Length}) -> {classification}" verbose

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
                    match project.Options with
                    | Some opts when opts.NativePointerSurface || opts.CHeaderMode ->
                        for requested in opts.AbiCriticalStructs do
                            let name = requested.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")
                            if baseCtx.Structs.ContainsKey name then
                                if not (structLayouts.ContainsKey name) then
                                    failwithf "Native carrier storage '%s' requires a measured layout; header probe did not provide it." name
                            else
                                match FidelityCodeGenerator.resolveCType baseCtx.TypedefMap dataModel Set.empty baseCtx.Delegates baseCtx.Enums name with
                                | FidelityCodeGenerator.Scalar r when r.Bits > 0 -> ()
                                | _ -> failwithf "Native carrier storage '%s' has no known scalar or measured record representation." name
                    | _ -> ()
                    let selectedNames = project.Namespaces |> List.collect (fun n -> n.Functions) |> Set.ofList
                    let rec markers = function
                        | CodeAST.Generic("CHandle", CodeAST.Named n) when not (Set.contains n (Set.ofList ["unit"; "int"; "float"; "bool"; "string"])) -> [n]
                        | CodeAST.Generic(_, t) -> markers t
                        | CodeAST.FunctionType(args, ret) -> List.collect markers (ret :: args)
                        | _ -> []
                    let phantomMarkers =
                        declarations |> List.collect (function
                        | CppParser.Declaration.Function f when selectedNames.Contains f.Name ->
                            (f.ReturnType :: (f.Parameters |> List.map snd)) |> List.collect (fun ty ->
                                FidelityCodeGenerator.resolveCType baseCtx.TypedefMap dataModel baseCtx.OpaqueHandles baseCtx.Delegates baseCtx.Enums ty
                                |> FidelityCodeGenerator.clefTypeOf |> markers)
                        | _ -> [])
                        |> Set.ofList
                    { baseCtx with
                        PhantomMarkers = if project.Options |> Option.exists (fun o -> o.CHeaderMode && not o.NativePointerSurface) then phantomMarkers else Set.empty
                        NonnullAnnotations = project.Nonnull
                        Bindings = projections |> List.map (fun binding -> binding.Name, binding) |> Map.ofList
                        ValueStructs = project.Options |> Option.map (fun opts -> Set.ofList opts.ValueStructs) |> Option.defaultValue Set.empty
                        NativePointerSurface = project.Options |> Option.exists (fun opts -> opts.NativePointerSurface) }

                // Generate protocol request implementations AFTER ctx is built,
                // so opaque handle types from XML interfaces are available for typed signatures
                let xmlRequestDecls = (if typedProtocol then [] else xmlProtocols) |> List.collect (fun p -> ProtocolParser.toRequestDecls p marshalConfig ctx.OpaqueHandles)
                if not xmlRequestDecls.IsEmpty then
                    logVerbose $"  {xmlRequestDecls.Length} protocol request implementations with typed handles" verbose

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
                            // Resolve the error enum — check both named enums and typedef'd anonymous enums
                            // C pattern: typedef enum { ... } name; produces anonymous enum + typedef
                            let enumExists =
                                declarations |> List.exists (function
                                    | CppParser.Declaration.Enum e when e.Name = errorType -> true
                                    | CppParser.Declaration.Typedef t when t.Name = errorType ->
                                        t.UnderlyingType.Contains("enum")
                                    | _ -> false)
                            if not enumExists then
                                failwithf "error_conventions declares error_type '%s' but no matching enum or typedef found in declarations. Fix the pilot TOML or ensure the header is parsed correctly." errorType

                            let structName = EnumErrorModuleGenerator.deriveErrorStructName errorType
                            // Look up success value's integer — check named enum first, then any enum containing the value
                            let successIntValue =
                                declarations |> List.tryPick (function
                                    | CppParser.Declaration.Enum e when e.Name = errorType ->
                                        e.Values |> List.tryPick (fun v ->
                                            if v.Name = successValue then Some v.Value else None)
                                    | CppParser.Declaration.Enum e when e.Name = "" ->
                                        // Anonymous enum (typedef'd) — search by success value name
                                        e.Values |> List.tryPick (fun v ->
                                            if v.Name = successValue then Some v.Value else None)
                                    | _ -> None)
                                |> Option.defaultValue 0L
                            WrapperTypes.UseEnumError (errorType, successIntValue, structName,
                                                      $"{nsPrefix}.{structName}")
                        | PilotTypes.NullWithReason reasonFn ->
                            WrapperTypes.UseNullWithReason reasonFn
                        | PilotTypes.ReturnCode ->
                            let libPrefix = deriveLibraryPrefix project.Library.Name
                            WrapperTypes.UseReturnCode (libPrefix, $"{nsPrefix}.ReturnCode")
                        | _ -> WrapperTypes.NoErrors
                    | None -> WrapperTypes.NoErrors

                // Classify types across namespaces: shared vs local
                let cHeaderMode = project.Options |> Option.exists (fun opts -> opts.CHeaderMode)
                let classification =
                    let classified = PilotAnalyzer.classifyProjectTypes project.Namespaces declarations
                    // ABI-critical storage is a library contract even if only one function
                    // namespace currently uses it. Keep its layout in the shared Types module.
                    let storageNames = ctx.StructLayouts |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    if ctx.NativePointerSurface || cHeaderMode then
                        // Native pointers do not expose a pointee type. Emit the requested
                        // storage contracts and public constants, avoiding unrelated libc internals.
                        let constants = declarations |> List.choose (function CppParser.Declaration.Macro m -> Some m.Name | _ -> None) |> Set.ofList
                        let requestedStorage =
                            project.Options |> Option.map (fun opts ->
                                opts.AbiCriticalStructs |> List.map (fun name -> name.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")) |> Set.ofList)
                            |> Option.defaultValue Set.empty
                        { classified with SharedTypes = Set.unionMany [constants; storageNames; requestedStorage]; LocalTypes = Map.empty }
                    else
                        { classified with
                            SharedTypes = Set.union classified.SharedTypes storageNames
                            LocalTypes = classified.LocalTypes |> Map.map (fun _ names -> Set.difference names storageNames) }
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
                let viewMarkers = MappedReturnGenerator.markers project |> List.map CodeRenderer.render |> String.concat "\n"
                if viewMarkers <> "" then File.AppendAllText(sharedTypesPath, "\n" + viewMarkers)
                logVerbose $"Shared types module: {sharedTypesPath} ({classification.SharedTypes.Count} types)" verbose

                // ── Error module ────────────────────────────────────────────
                let errorModuleFiles =
                    match errorHandling with
                    | WrapperTypes.UseErrno _ ->
                        // Extract errno macros from declarations; if none present
                        // (macro_prefixes didn't include "E"), parse errno.h directly
                        let macros =
                            declarations |> List.choose (function
                                | CppParser.Declaration.Macro m -> Some m
                                | _ -> None)
                        let errnoConstants = ErrnoModuleGenerator.filterErrnoMacros macros
                        let effectiveMacros =
                            if not errnoConstants.IsEmpty then macros
                            else
                                // Targeted parse of errno.h for errno constants
                                logVerbose "No errno macros in declarations; parsing errno.h directly" verbose
                                let errnoOptions : CppParser.HeaderParserOptions = {
                                    HeaderFile = "/usr/include/errno.h"
                                    IncludePaths = project.Library.IncludePaths
                                    Defines = project.Library.Defines
                                    Verbose = false
                                    IncludeMacros = true
                                    MacroPrefixes = ["E"]
                                    IncludeRoot = None
                                    CppMode = false  // errno.h is always C
                                }
                                match CppParser.parseHeaderFull errnoOptions with
                                | Ok result -> result.Macros
                                | Error e ->
                                    logVerbose $"Warning: errno.h parse failed: {e}" verbose
                                    []
                        let errnoNs = $"{nsPrefix}.Errno"
                        let output = ErrnoModuleGenerator.generate effectiveMacros errnoNs project.Library.Name
                        let errorPath = Path.Combine(outputDir, "Errno.clef")
                        File.WriteAllText(errorPath, output)
                        logVerbose $"Errno module: {errorPath}" verbose
                        [errorPath]
                    | WrapperTypes.UseEnumError (errorType, _, _, _) ->
                        // Find enum by name or by typedef (anonymous enum pattern)
                        let errorEnum =
                            declarations |> List.tryPick (function
                                | CppParser.Declaration.Enum e when e.Name = errorType -> Some e
                                | _ -> None)
                            |> Option.orElseWith (fun () ->
                                // typedef enum { ... } name; — find anonymous enum containing success value
                                let hasTypedef = declarations |> List.exists (function
                                    | CppParser.Declaration.Typedef t when t.Name = errorType -> true
                                    | _ -> false)
                                if hasTypedef then
                                    match project.ErrorConventions with
                                    | Some spec ->
                                        match spec.Default with
                                        | PilotTypes.EnumErrorCode (_, sv, _, _) ->
                                            declarations |> List.tryPick (function
                                                | CppParser.Declaration.Enum e when e.Name = "" ->
                                                    if e.Values |> List.exists (fun v -> v.Name = sv) then Some e
                                                    else None
                                                | _ -> None)
                                        | _ -> None
                                    | None -> None
                                else None)
                        match errorEnum with
                        | Some enumDecl ->
                            match project.ErrorConventions with
                            | Some spec ->
                                match spec.Default with
                                | PilotTypes.EnumErrorCode (et, sv, esFn, enFn) ->
                                    let config = EnumErrorModuleGenerator.makeConfig et sv esFn enFn
                                    let errorNs = $"{nsPrefix}.{config.ErrorStructName}"
                                    let typesModule = $"{nsPrefix}.Types"
                                    match EnumErrorModuleGenerator.generate enumDecl config errorNs [typesModule] with
                                    | Some output ->
                                        let errorPath = Path.Combine(outputDir, $"{config.ErrorStructName}.clef")
                                        File.WriteAllText(errorPath, output)
                                        logVerbose $"Error module: {errorPath}" verbose
                                        [errorPath]
                                    | None -> []
                                | _ -> []
                            | None -> []
                        | None ->
                            failwithf "error_conventions declares error_type '%s' but enum values not found in declarations" errorType
                    | WrapperTypes.UseReturnCode (libPrefix, _) ->
                        // Generate ReturnCode.clef — simple describe function for int return codes
                        let rcNs = $"{nsPrefix}.ReturnCode"
                        let sb = System.Text.StringBuilder()
                        sb.AppendLine($"module {rcNs}") |> ignore
                        sb.AppendLine() |> ignore
                        sb.AppendLine($"// Generated by Farscape — ReturnCode error module for {project.Library.Name}") |> ignore
                        sb.AppendLine() |> ignore
                        sb.AppendLine("    /// Describe a return code as a human-readable error string.") |> ignore
                        sb.AppendLine("    /// Override in an Overlay module for domain-specific error mapping.") |> ignore
                        sb.AppendLine("    let describe (code: int) : string =") |> ignore
                        sb.AppendLine($"        \"{libPrefix} error (code \" + Format.int code + \")\"") |> ignore
                        let output = sb.ToString()
                        let errorPath = Path.Combine(outputDir, "ReturnCode.clef")
                        File.WriteAllText(errorPath, output)
                        logVerbose $"ReturnCode module: {errorPath}" verbose
                        [errorPath]
                    | _ -> []

                // ── BAREWire descriptors ─────────────────────────────────────
                // Every struct's StructDescriptor is emitted inside its layout module in the
                // Types module (docs/14 §2); there is no separate Descriptors file to write.

                // ── Callback spec (resolved here, used by L2 marshaling) ────
                let callbackSpec =
                    if generateWrappers then
                        let spec =
                            match project.Callbacks with
                            | Some spec -> spec
                            | None ->
                                let rawSpec = PilotAnalyzer.discoverCallbacks declarations
                                let claimedFunctions =
                                    project.Namespaces |> List.collect (fun ns ->
                                        PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                                        |> List.choose (function
                                            | CppParser.Declaration.Function f -> Some f.Name
                                            | _ -> None))
                                    |> Set.ofList
                                { rawSpec with
                                    Registrations = rawSpec.Registrations |> List.filter (fun r -> claimedFunctions.Contains r.Function) }
                        if spec.Registrations.IsEmpty && spec.ListenerStructs.IsEmpty then
                            logVerbose "No callback patterns detected" verbose
                            None
                        else
                            logVerbose $"Callback patterns: {spec.Registrations.Length} registration(s), {spec.ListenerStructs.Length} listener struct(s)" verbose
                            Some spec
                    else None

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
                            if localTypeNames.IsEmpty then [sharedTypesNs]
                            else [sharedTypesNs; localTypesNs]
                        let funcDecls =
                            if typedProtocol && not ns.XmlInterfaces.IsEmpty then []
                            else PilotAnalyzer.filterDeclarationsWithTypes ns Set.empty declarations
                        let funcCount =
                            funcDecls |> List.filter (function CppParser.Declaration.Function _ -> true | _ -> false) |> List.length
                        let funcCode =
                            FidelityCodeGenerator.generateModule ctx Set.empty funcDecls
                                ns.Name ns.Library $"{lastSegment} function declarations" funcOpenModules
                        let mappedDecls = MappedReturnGenerator.generate project ctx declarations ns sharedTypesNs
                        let funcCode =
                            MappedReturnGenerator.mappings project
                            |> List.filter (fun mapping -> ns.Functions |> List.contains mapping.Acquire)
                            |> List.fold (fun (code: string) mapping ->
                                code.Replace("\nlet " + mapping.Acquire + " (", "\nlet private " + mapping.Acquire + " (")) funcCode
                        let funcCode = funcCode + "\n" + (mappedDecls |> List.map CodeRenderer.render |> String.concat "\n")

                        let funcPath = Path.Combine(nsDir, $"{lastSegment}.clef")
                        File.WriteAllText(funcPath, funcCode)
                        logVerbose $"  {lastSegment}/{lastSegment}.clef ({funcCount} functions)" verbose

                        // ── Wrappers (optional) ─────────────────────────────
                        let wrapperFiles =
                            if generateWrappers then
                                let allNsDecls = PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                                let wrapperNamespace = $"{ns.Name}.Api"
                                let passthroughFunctions =
                                    project.ErrorConventions
                                    |> Option.map (fun spec -> spec.Overrides |> Map.toList |> List.choose (fun (name, convention) ->
                                        if convention = PilotTypes.NoErrorConvention then Some name else None) |> Set.ofList)
                                    |> Option.defaultValue Set.empty
                                let wrapperCode =
                                    WrapperCodeGenerator.generateWithContext (Some ctx) ctx.NativePointerSurface passthroughFunctions allNsDecls wrapperNamespace ns.Library ns.Name errorHandling dataModel project.Nonnull
                                let wrapperPath = Path.Combine(nsDir, $"{lastSegment}Api.clef")
                                File.WriteAllText(wrapperPath, wrapperCode)
                                logVerbose $"  {lastSegment}/{lastSegment}Api.clef" verbose
                                [wrapperPath]
                            else []

                        localTypeFiles @ [funcPath] @ wrapperFiles)

                // ── L2 Listener Builders (in main package) ──────────────
                let l2CallbackModule = $"{nsPrefix}.Callbacks"
                let l2CallbackFiles =
                    match callbackSpec with
                    | Some spec when generateWrappers ->
                        let l2Opens = [$"{nsPrefix}.Types"]
                        match CallbackWrapperGenerator.generateL2 spec declarations l2CallbackModule l2Opens with
                        | Some output ->
                            let l2CallbackPath = Path.Combine(outputDir, "Callbacks.clef")
                            File.WriteAllText(l2CallbackPath, output)
                            logVerbose $"  Callbacks.clef (L2 listener builders)" verbose
                            [l2CallbackPath]
                        | None -> []
                    | _ -> []

                let typedProtocolFiles = if typedProtocol then TypedProtocolGenerator.generate project xmlProtocols declarations ctx outputDir else []
                let signalFiles =
                    let selected = project.Namespaces |> List.collect (fun n -> n.Signals)
                    if selected.IsEmpty then [] else
                    match introspection with
                    | None -> failwith "[[namespace]] signals require [library] introspection files"
                    | Some repo ->
                        match IntrospectionParser.select repo selected with
                        | Ok signals -> TypedSignalGenerator.generate nsPrefix signals declarations ctx outputDir
                        | Error e -> failwith e
                let allFiles = [sharedTypesPath] @ errorModuleFiles @ nsFiles @ l2CallbackFiles @ typedProtocolFiles @ signalFiles

                // Generate canonical fidproj for the binding library
                let fidprojFile = generateFidproj project (if typedProtocol then nsPrefix + ".Native" else nsPrefix) outputDir allFiles verbose

                // ── Layer 2 Marshaling Bridge package ─────────────────────
                let unpairedConstructors =
                    let allInterfaces = xmlProtocols |> List.collect (fun p -> p.Interfaces)
                    let withDestroy =
                        allInterfaces
                        |> List.filter (fun i -> i.Requests |> List.exists (fun r -> r.IsDestructor))
                        |> List.map (fun i -> i.Name) |> Set.ofList
                    let constructed =
                        allInterfaces |> List.collect (fun i ->
                            i.Requests |> List.collect (fun r ->
                                r.Args |> List.choose (fun a ->
                                    if a.Type = ProtocolParser.NewId then a.Interface else None)))
                        |> List.distinct
                    constructed |> List.filter (fun n -> not (Set.contains n withDestroy))

                let layer3Req = if typedProtocol then None else PilotAnalyzer.analyzeLayer3Requirements project callbackSpec unpairedConstructors

                let layer3Files =
                    match layer3Req with
                    | None -> []
                    | Some req ->
                        let bridgeName = $"{nsPrefix}.Bridge"
                        let libLastSegment = nsPrefix.Split('.') |> Array.last
                        let bridgeDir = Path.Combine(Path.GetDirectoryName(outputDir), $"{libLastSegment}Bridge")
                        Directory.CreateDirectory(bridgeDir) |> ignore

                        let fidprojDir = Path.GetDirectoryName(Path.GetDirectoryName(outputDir))

                        // Build function-to-namespace resolver for callbacks
                        let functionToNs =
                            project.Namespaces |> List.collect (fun ns ->
                                PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                                |> List.choose (function
                                    | CppParser.Declaration.Function f -> Some (f.Name, ns.Name)
                                    | _ -> None))
                            |> Map.ofList
                        let resolveModule funcName =
                            functionToNs |> Map.tryFind funcName |> Option.defaultValue nsPrefix

                        // Protocol dispatch files — one per namespace with XML interfaces
                        let dispatchFiles =
                            if req.HasProtocolDispatch then
                                project.Namespaces |> List.collect (fun ns ->
                                    if ns.XmlInterfaces.IsEmpty then []
                                    else
                                        let nsRequestDecls =
                                            xmlRequestDecls |> List.filter (fun decl ->
                                                match decl with
                                                | CodeAST.LetBinding(name, _, _, _, _) ->
                                                    ns.XmlInterfaces |> List.exists (fun iface ->
                                                        name.StartsWith(iface + "_"))
                                                | _ -> false)
                                        if nsRequestDecls.IsEmpty then []
                                        else
                                            let lastSeg = ns.Name.Split('.') |> Array.last
                                            let dispatchNs = $"{bridgeName}.{lastSeg}"
                                            // NativePtr.ofNativeInt/set are Clef intrinsics — no namespace import needed
                                            let openModules =
                                                [ $"{nsPrefix}.Types"
                                                  "Fidelity.Libc.DynamicLink"
                                                  "Fidelity.Libc.Memory"
                                                  match project.ProtocolConfig with
                                                  | Some cfg -> cfg.MarshalModule
                                                  | None -> () ]
                                            let moduleDecl =
                                                CodeAST.Module(dispatchNs,
                                                    $"{lastSeg} protocol dispatch — Layer 2 protocol marshaling",
                                                    [ for m in openModules -> CodeAST.OpenModule m ]
                                                    @ [CodeAST.BlankLine]
                                                    @ nsRequestDecls)
                                            let code = CodeRenderer.render moduleDecl
                                            let dispatchPath = Path.Combine(bridgeDir, $"{lastSeg}Dispatch.clef")
                                            File.WriteAllText(dispatchPath, code)
                                            logVerbose $"  L2 marshaling: {lastSeg}Dispatch.clef ({nsRequestDecls.Length} protocol requests)" verbose
                                            [dispatchPath])
                            else []

                        // Callback wrappers file
                        let callbackFiles =
                            match req.HasCallbackWrappers, callbackSpec with
                            | true, Some spec ->
                                let callbackNs = $"{bridgeName}.Callbacks"
                                // Open types module + Libc.DynamicLink (dlsym) + all bindings modules used by registrations
                                let registrationModules =
                                    spec.Registrations
                                    |> List.map (fun reg -> resolveModule reg.Function)
                                    |> List.distinct
                                let callbackOpens =
                                    [ if not l2CallbackFiles.IsEmpty then l2CallbackModule
                                      $"{nsPrefix}.Types"
                                      if not ctx.NativePointerSurface && not cHeaderMode then "Fidelity.Libc.DynamicLink" ] @ registrationModules
                                    |> List.distinct
                                let generated =
                                    if ctx.NativePointerSurface then
                                        CallbackWrapperGenerator.generateNative spec declarations callbackNs dataModel callbackOpens
                                    elif cHeaderMode then
                                        CallbackWrapperGenerator.generateTypedWithModules spec declarations callbackNs ctx callbackOpens resolveModule
                                    else CallbackWrapperGenerator.generate spec declarations callbackNs dataModel callbackOpens l2CallbackModule
                                match generated with
                                | Some output ->
                                    let callbackPath = Path.Combine(bridgeDir, "Callbacks.clef")
                                    File.WriteAllText(callbackPath, output)
                                    logVerbose $"  L2 marshaling: Callbacks.clef" verbose
                                    [callbackPath]
                                | None -> []
                            | _ -> []

                        let bridgeFiles = dispatchFiles @ callbackFiles

                        // Generate Bridge fidproj
                        if not bridgeFiles.IsEmpty then
                            let relativeSources =
                                bridgeFiles |> List.map (fun f ->
                                    let fullPath = Path.GetFullPath f
                                    let basePath = Path.GetFullPath fidprojDir
                                    if fullPath.StartsWith(basePath + "/") || fullPath.StartsWith(basePath + string Path.DirectorySeparatorChar) then
                                        fullPath.Substring(basePath.Length + 1)
                                    else fullPath)

                            let sb = System.Text.StringBuilder()
                            sb.AppendLine("[package]") |> ignore
                            sb.AppendLine($"name = \"{bridgeName}\"") |> ignore
                            sb.AppendLine("version = \"0.1.0\"") |> ignore
                            sb.AppendLine($"description = \"Layer 2 marshaling for {project.Library.Name} — protocol dispatch and callback wrappers.\"") |> ignore
                            sb.AppendLine() |> ignore
                            sb.AppendLine("[compilation]") |> ignore
                            sb.AppendLine("target = \"cpu\"") |> ignore
                            sb.AppendLine() |> ignore
                            sb.AppendLine("[build]") |> ignore
                            sb.AppendLine("output_kind = \"library\"") |> ignore
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

                            // Resolve dependency paths
                            let resolveDep name =
                                match findProjectAbove fidprojDir $"{name}.fidproj" with
                                | Some path -> Path.GetRelativePath(fidprojDir, path).Replace('\\', '/')
                                | None ->
                                    let reposDir = Path.GetFullPath(Path.Combine(fidprojDir, "../../../../"))
                                    let target = Path.Combine(reposDir, $"Fidelity.Platform/Environments/Linux/x86_64/{name}.fidproj")
                                    if File.Exists(target) then Path.GetRelativePath(fidprojDir, target).Replace('\\', '/')
                                    else Path.GetFullPath(target).Replace('\\', '/')

                            let platformDep = resolveDep "Fidelity.Platform"
                            let libDep = resolveDep nsPrefix
                            if not (project.Options |> Option.exists (fun opts -> opts.DescriptorOnlyDependencies)) then
                                sb.AppendLine($"Fidelity.Platform = {{ path = \"{platformDep}\" }}") |> ignore
                            sb.AppendLine($"{nsPrefix} = {{ path = \"{libDep}\" }}") |> ignore
                            let usesLibcBridge = (not ctx.NativePointerSurface && not cHeaderMode) || req.HasProtocolDispatch
                            if usesLibcBridge && (req.Dependencies |> List.exists (function LibcDynamicLink | LibcMemory -> true)) && nsPrefix <> "Fidelity.Libc" then
                                let libcDep = resolveDep "Fidelity.Libc"
                                sb.AppendLine($"Fidelity.Libc = {{ path = \"{libcDep}\" }}") |> ignore

                            let bridgeFidprojPath = Path.Combine(fidprojDir, $"{bridgeName}.fidproj")
                            File.WriteAllText(bridgeFidprojPath, sb.ToString())
                            logVerbose $"Generated L2 marshaling fidproj: {bridgeFidprojPath}" verbose

                            // Generate LAYER3-REPORT.md
                            let report = System.Text.StringBuilder()
                            report.AppendLine($"# Layer 2 Marshaling Report: {bridgeName}") |> ignore
                            report.AppendLine() |> ignore
                            report.AppendLine("## Generated") |> ignore
                            report.AppendLine() |> ignore
                            if req.HasProtocolDispatch then
                                let totalRequests = xmlRequestDecls.Length
                                let destructorCount =
                                    xmlProtocols |> List.sumBy (fun p ->
                                        p.Interfaces |> List.sumBy (fun i ->
                                            i.Requests |> List.filter (fun r -> r.IsDestructor) |> List.length))
                                let constructorCount =
                                    xmlProtocols |> List.sumBy (fun p ->
                                        p.Interfaces |> List.sumBy (fun i ->
                                            i.Requests |> List.filter (fun r ->
                                                r.Args |> List.exists (fun a -> a.Type = ProtocolParser.NewId)) |> List.length))
                                let voidCount = totalRequests - destructorCount - constructorCount
                                report.AppendLine($"### Protocol Dispatch ({totalRequests} requests)") |> ignore
                                report.AppendLine($"- Constructors: {constructorCount}") |> ignore
                                report.AppendLine($"- Destructors: {destructorCount}") |> ignore
                                report.AppendLine($"- Void requests: {voidCount}") |> ignore
                                report.AppendLine() |> ignore
                            if req.HasCallbackWrappers then
                                match callbackSpec with
                                | Some spec ->
                                    report.AppendLine("### Callback Wrappers") |> ignore
                                    report.AppendLine($"- Registration wrappers: {spec.Registrations.Length}") |> ignore
                                    report.AppendLine($"- Listener struct builders: {spec.ListenerStructs.Length}") |> ignore
                                    report.AppendLine() |> ignore
                                | None -> ()
                            if not req.UnpairedConstructors.IsEmpty then
                                report.AppendLine("## Unmapped — Developer Review Required") |> ignore
                                report.AppendLine() |> ignore
                                report.AppendLine("### Unpaired Constructors") |> ignore
                                report.AppendLine("These interfaces have constructors but no explicit destroy request.") |> ignore
                                report.AppendLine("The developer must determine lifecycle management:") |> ignore
                                for name in req.UnpairedConstructors do
                                    report.AppendLine($"- `{name}`") |> ignore
                                report.AppendLine() |> ignore
                            report.AppendLine("## Notes") |> ignore
                            if req.Dependencies |> List.contains LibcMemory then
                                report.AppendLine("- Protocol dispatch uses Fidelity.Libc.Memory for argument arrays (malloc/free)") |> ignore
                            if usesLibcBridge && (req.Dependencies |> List.contains LibcDynamicLink) then
                                report.AppendLine("- Interface globals resolved via Fidelity.Libc.DynamicLink.dlsym") |> ignore
                            if ctx.NativePointerSurface || cHeaderMode then
                                report.AppendLine("- Callback wrappers accept typed FnPtr entries and preserve explicit environment arguments.") |> ignore
                            if req.HasProtocolDispatch then
                                report.AppendLine("- NativeInterop.NativePtr used for argument array writes") |> ignore

                            let reportPath =
                                if ctx.NativePointerSurface || cHeaderMode then Path.Combine(bridgeDir, "REPORT.md")
                                else Path.Combine(fidprojDir, "LAYER3-REPORT.md")
                            File.WriteAllText(reportPath, report.ToString())
                            logVerbose $"L2 marshaling report: {reportPath}" verbose

                            bridgeFiles @ [bridgeFidprojPath; reportPath]
                        else []

                let advisories =
                    match errorHandling, generateWrappers with
                    | WrapperTypes.NoErrors, true ->
                        [$"No [error_conventions] defined for '{project.Library.Name}'. Layer 2 wrappers use direct passthrough. If this library reports errors through a query function, out-parameter, or other mechanism, add error handling in an Overlay module."]
                    | WrapperTypes.UseNullWithReason reasonFn, true ->
                        [$"Using null_with_reason convention for '{project.Library.Name}'. Functions returning pointers will call {reasonFn}() on null and wrap in Result<nativeint, nativeint>."]
                    | _ -> []

                let allOutputFiles =
                    let baseFiles =
                        match fidprojFile with
                        | Some fp -> allFiles @ [fp]
                        | None -> allFiles
                    baseFiles @ layer3Files

                Ok {
                    OutputFiles = allOutputFiles
                    DeclarationCount = declarations.Length
                    Advisories = advisories
                }
