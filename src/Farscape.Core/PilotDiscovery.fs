namespace Farscape.Core

open Fidelity.Data.XML

/// Generalized source asset discovery for Pilot projects.
///
/// Given a directory, Pilot scans for everything it knows how to parse,
/// classifies what it finds, flags ambiguities, and produces a curated
/// DiscoveryResult. The discovery is driven by Pilot's available parsers,
/// not by foreknowledge of the target library.
///
/// Architecture: Pure core (discover) takes injected IO functions for
/// testability. IO wrappers (walkDirectoryIO, readFileIO) provided for
/// CLI consumption. Same pattern as PilotAnalyzer.
module PilotDiscovery =

    // =========================================================================
    // Classification Types
    // =========================================================================

    /// Format of a discovered XML protocol file.
    type XmlProtocolFormat =
        | WaylandProtocol
        | DBusIntrospection
        | VulkanRegistry

    /// Metadata extracted from a pkg-config .pc file.
    type PkgConfigInfo = {
        Name: string
        LibraryName: string option
        IncludePaths: string list
        LinkFlags: string list
    }

    /// Kind of build system file.
    type BuildSystemKind = CMake | Meson

    /// Classification of a discovered file.
    type FileClassification =
        | CHeader of path: string * isUmbrella: bool * isInternal: bool
        | CppHeader of path: string * hasExternC: bool * isInternal: bool
        | ProtocolXml of path: string * format: XmlProtocolFormat
        | PkgConfig of path: string * metadata: PkgConfigInfo
        | BuildSystemFile of path: string * kind: BuildSystemKind

    // =========================================================================
    // Diagnostic Types
    // =========================================================================

    type DiscoveryError =
        | NoParseableFiles of directory: string
        | DirectoryNotFound of path: string

    type DiscoveryWarning =
        | NoUmbrellaHeader of headerCount: int
        | InternalHeadersFound of count: int
        | MixedLanguage
        | LargeHeaderCount of count: int

    type DiscoverySuggestion =
        | PkgConfigFound of name: string * libraryName: string option * includePath: string option
        | UmbrellaDetected of file: string * includeCount: int * declarationCount: int
        | ProtocolsFound of count: int
        | ExternCDetected of file: string

    type Diagnostic =
        | DiagError of DiscoveryError
        | DiagWarning of DiscoveryWarning
        | DiagSuggestion of DiscoverySuggestion

    // =========================================================================
    // Discovery Result
    // =========================================================================

    type DiscoveryResult = {
        RootDirectory: string
        Files: FileClassification list
        Diagnostics: Diagnostic list
        SuggestedLibraryName: string option
        SuggestedIncludePaths: string list
    }

    // =========================================================================
    // Classification Heuristics (pure, no IO)
    // =========================================================================

    /// Check if a relative path matches internal/private header patterns.
    let isInternalPath (relativePath: string) : bool =
        let normalized = relativePath.Replace('\\', '/')
        let fileName = System.IO.Path.GetFileName(normalized)
        let dirParts = normalized.Split('/')
        fileName.EndsWith("_internal.h") ||
        fileName.EndsWith("_internal.hpp") ||
        fileName.EndsWith("_private.h") ||
        fileName.EndsWith("_private.hpp") ||
        fileName.EndsWith("_impl.h") ||
        fileName.EndsWith("_impl.hpp") ||
        dirParts |> Array.exists (fun p -> p = "detail" || p = "internal" || p = "private")

    /// Classify header content as C or C++.
    /// Returns (isCpp, hasExternC).
    let classifyHeaderContent (content: string) : bool * bool =
        let hasCppIndicators =
            content.Contains("namespace ") ||
            content.Contains("class ") ||
            content.Contains("template<") ||
            content.Contains("template <") ||
            content.Contains("std::") ||
            content.Contains("public:") ||
            content.Contains("private:") ||
            content.Contains("protected:")
        let hasExternC = content.Contains("extern \"C\"")
        (hasCppIndicators, hasExternC)

    /// Count #include directives in header content.
    let countIncludes (content: string) : int =
        content.Split('\n')
        |> Array.filter (fun line -> line.TrimStart().StartsWith("#include"))
        |> Array.length

    /// Rough count of own declarations (functions, structs, enums, typedefs).
    let countDeclarations (content: string) : int =
        content.Split('\n')
        |> Array.filter (fun line ->
            let t = line.TrimStart()
            (t.StartsWith("typedef ") ||
             t.StartsWith("struct ") ||
             t.StartsWith("enum ") ||
             (t.Contains("(") && not (t.StartsWith("#")) && not (t.StartsWith("//")) && not (t.StartsWith("/*")))))
        |> Array.length

    /// Detect umbrella header: high include count relative to own declarations.
    let isUmbrellaHeader (content: string) : bool =
        let includes = countIncludes content
        let decls = countDeclarations content
        includes >= 5 && (decls < includes / 2 || decls < 3)

    /// Classify XML file by root element using Fidelity.Data.XML.
    let classifyXml (content: string) : XmlProtocolFormat option =
        match Xml.parse content with
        | Ok doc ->
            match XmlNode.name doc.Root with
            | Some "protocol" -> Some WaylandProtocol
            | Some "node" -> Some DBusIntrospection
            | Some "registry" -> Some VulkanRegistry
            | _ -> None
        | Error _ -> None

    /// Parse a pkg-config .pc file for metadata.
    let parsePkgConfig (content: string) : PkgConfigInfo =
        let lines = content.Split('\n')
        let variables = System.Collections.Generic.Dictionary<string, string>()
        // Helper: substitute ${variable} references using current variable table
        let substitute (s: string) =
            let mutable result = s
            for kv in variables do
                result <- result.Replace($"${{{kv.Key}}}", kv.Value)
            result
        // First pass: collect variable definitions (key=value lines),
        // resolving references as we go (variables are defined top-to-bottom).
        for line in lines do
            if not (line.TrimStart().StartsWith("#")) then
                let idx = line.IndexOf('=')
                // Only count as variable if no colon comes before the equals
                let colonIdx = line.IndexOf(':')
                if idx > 0 && (colonIdx < 0 || idx < colonIdx) then
                    let key = line.[..idx-1].Trim()
                    let value = line.[idx+1..].Trim() |> substitute
                    variables.[key] <- value
        // Second pass: extract pkg-config fields (key: value lines)
        let mutable name = ""
        let mutable libName = None
        let mutable includePaths = []
        let mutable linkFlags = []
        for line in lines do
            if not (line.TrimStart().StartsWith("#")) then
                let idx = line.IndexOf(':')
                if idx > 0 then
                    let key = line.[..idx-1].Trim()
                    let value = line.[idx+1..].Trim() |> substitute
                    match key with
                    | "Name" -> name <- value
                    | "Libs" ->
                        let parts = value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                        linkFlags <- parts |> Array.toList
                        libName <- parts |> Array.tryFind (fun p -> p.StartsWith("-l")) |> Option.map (fun p -> p.[2..])
                    | "Cflags" ->
                        let parts = value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                        includePaths <-
                            parts
                            |> Array.filter (fun p -> p.StartsWith("-I"))
                            |> Array.map (fun p -> p.[2..])
                            |> Array.toList
                    | _ -> ()
        { Name = name; LibraryName = libName; IncludePaths = includePaths; LinkFlags = linkFlags }

    // =========================================================================
    // File Classification
    // =========================================================================

    /// Classify a single file. Returns None if the file is not recognizable.
    /// rootDir: absolute path to discovery root (for computing relative paths).
    /// readFile: injected IO — reads file content or returns None on error.
    let classifyFile
        (rootDir: string)
        (filePath: string)
        (readFile: string -> string option)
        : FileClassification option =
        let relativePath =
            System.IO.Path.GetRelativePath(rootDir, filePath).Replace('\\', '/')
        let ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant()
        let fileName = System.IO.Path.GetFileName(filePath)
        match ext with
        | ".h" ->
            match readFile filePath with
            | Some content ->
                let isCpp, hasExternC = classifyHeaderContent content
                let isInternal = isInternalPath relativePath
                let isUmbrella = isUmbrellaHeader content
                if isCpp && not hasExternC then
                    Some (CppHeader (relativePath, false, isInternal))
                elif isCpp && hasExternC then
                    Some (CppHeader (relativePath, true, isInternal))
                else
                    Some (CHeader (relativePath, isUmbrella, isInternal))
            | None -> None
        | ".hpp" | ".hxx" | ".hh" ->
            match readFile filePath with
            | Some content ->
                let _, hasExternC = classifyHeaderContent content
                let isInternal = isInternalPath relativePath
                Some (CppHeader (relativePath, hasExternC, isInternal))
            | None -> None
        | ".xml" ->
            match readFile filePath with
            | Some content ->
                classifyXml content
                |> Option.map (fun format -> ProtocolXml (relativePath, format))
            | None -> None
        | ".pc" ->
            match readFile filePath with
            | Some content ->
                Some (PkgConfig (relativePath, parsePkgConfig content))
            | None -> None
        | _ when fileName = "CMakeLists.txt" ->
            Some (BuildSystemFile (relativePath, CMake))
        | _ when fileName = "meson.build" ->
            Some (BuildSystemFile (relativePath, Meson))
        | _ -> None

    // =========================================================================
    // Diagnostic Generation
    // =========================================================================

    /// Generate diagnostics from a set of classified files.
    let generateDiagnostics (files: FileClassification list) : Diagnostic list =
        let diagnostics = ResizeArray<Diagnostic>()

        let cHeaders =
            files |> List.choose (function CHeader (p, u, i) -> Some (p, u, i) | _ -> None)
        let cppHeaders =
            files |> List.choose (function CppHeader (p, e, i) -> Some (p, e, i) | _ -> None)
        let protocols =
            files |> List.choose (function ProtocolXml (p, f) -> Some (p, f) | _ -> None)
        let pkgConfigs =
            files |> List.choose (function PkgConfig (_, m) -> Some m | _ -> None)

        let allHeaderCount = cHeaders.Length + cppHeaders.Length

        // Umbrella detection / no-umbrella warning
        let umbrellas = cHeaders |> List.filter (fun (_, isU, _) -> isU)
        if allHeaderCount > 5 && umbrellas.IsEmpty then
            diagnostics.Add(DiagWarning (NoUmbrellaHeader allHeaderCount))
        for (path, _, _) in umbrellas do
            diagnostics.Add(DiagSuggestion (UmbrellaDetected (path, 0, 0)))

        // Internal headers found
        let internalCount =
            (cHeaders |> List.filter (fun (_, _, isI) -> isI) |> List.length) +
            (cppHeaders |> List.filter (fun (_, _, isI) -> isI) |> List.length)
        if internalCount > 0 then
            diagnostics.Add(DiagWarning (InternalHeadersFound internalCount))

        // Mixed C and C++ headers
        let publicC = cHeaders |> List.filter (fun (_, _, isI) -> not isI)
        let publicCpp = cppHeaders |> List.filter (fun (_, _, isI) -> not isI)
        if not publicC.IsEmpty && not publicCpp.IsEmpty then
            diagnostics.Add(DiagWarning MixedLanguage)

        // Large header count
        if allHeaderCount > 20 then
            diagnostics.Add(DiagWarning (LargeHeaderCount allHeaderCount))

        // Extern C detection for C++ headers
        for (path, hasExternC, _) in cppHeaders do
            if hasExternC then
                diagnostics.Add(DiagSuggestion (ExternCDetected path))

        // Protocol XML files found
        if not protocols.IsEmpty then
            diagnostics.Add(DiagSuggestion (ProtocolsFound protocols.Length))

        // Pkg-config metadata found
        for info in pkgConfigs do
            let includePath = info.IncludePaths |> List.tryHead
            diagnostics.Add(DiagSuggestion (PkgConfigFound (info.Name, info.LibraryName, includePath)))

        diagnostics |> Seq.toList

    // =========================================================================
    // Core Discovery (pure)
    // =========================================================================

    /// Discover parseable files in a directory tree.
    /// Pure function: IO is injected via walkDirectory and readFile.
    let discover
        (rootDirectory: string)
        (libraryNameHint: string option)
        (walkDirectory: string -> string list)
        (readFile: string -> string option)
        : DiscoveryResult =

        let allFiles = walkDirectory rootDirectory
        let classified =
            allFiles |> List.choose (fun f -> classifyFile rootDirectory f readFile)

        let diagnostics =
            if classified.IsEmpty then
                [ DiagError (NoParseableFiles rootDirectory) ]
            else
                generateDiagnostics classified

        // Suggest library name: explicit hint > pkg-config > directory name
        let suggestedLibraryName =
            match libraryNameHint with
            | Some name -> Some name
            | None ->
                let fromPkgConfig =
                    classified |> List.tryPick (function
                        | PkgConfig (_, info) -> info.LibraryName
                        | _ -> None)
                match fromPkgConfig with
                | Some _ -> fromPkgConfig
                | None -> Some (System.IO.Path.GetFileName(rootDirectory))

        // Suggest include paths from pkg-config files
        let suggestedIncludePaths =
            classified
            |> List.collect (function PkgConfig (_, info) -> info.IncludePaths | _ -> [])
            |> List.distinct

        { RootDirectory = rootDirectory
          Files = classified
          Diagnostics = diagnostics
          SuggestedLibraryName = suggestedLibraryName
          SuggestedIncludePaths = suggestedIncludePaths }

    // =========================================================================
    // Discovery → PilotProject Conversion
    // =========================================================================

    /// Convert a DiscoveryResult to a PilotProject skeleton.
    /// Prefers umbrella headers over individual headers.
    /// Includes XML protocols and pkg-config include paths automatically.
    let toPilotProject
        (libraryName: string)
        (outputMode: string)
        (outputDir: string)
        (result: DiscoveryResult)
        : PilotTypes.PilotProject =

        let root = result.RootDirectory

        // Collect non-internal C headers
        let cHeaders =
            result.Files |> List.choose (function
                | CHeader (path, _, isInternal) when not isInternal ->
                    Some (System.IO.Path.Combine(root, path))
                | _ -> None)

        // Collect non-internal C++ headers with extern "C" (bindable surface)
        let bindableCppHeaders =
            result.Files |> List.choose (function
                | CppHeader (path, hasExternC, isInternal) when hasExternC && not isInternal ->
                    Some (System.IO.Path.Combine(root, path))
                | _ -> None)

        // Collect Wayland protocol XML files
        let xmlProtocols =
            result.Files |> List.choose (function
                | ProtocolXml (path, WaylandProtocol) ->
                    Some (System.IO.Path.Combine(root, path))
                | _ -> None)

        // Prefer umbrella headers if any exist
        let umbrellaHeaders =
            result.Files |> List.choose (function
                | CHeader (path, true, false) -> Some (System.IO.Path.Combine(root, path))
                | _ -> None)

        let selectedHeaders =
            if not umbrellaHeaders.IsEmpty then umbrellaHeaders
            else cHeaders @ bindableCppHeaders

        { Library = {
            Name = libraryName
            Headers = selectedHeaders
            XmlProtocols = xmlProtocols
            IncludePaths = result.SuggestedIncludePaths
            Defines = []
            TransitiveHeaders = []
            MacroPrefixes = []
          }
          Output = { Mode = outputMode; Directory = outputDir }
          Namespaces = [
            { Name = $"Fidelity.{libraryName}.Core"
              Description = "Core functions"
              Library = libraryName
              Prefixes = []
              Functions = []
              XmlInterfaces = [] }
          ]
          ErrorConventions = None
          Options = None
          Callbacks = None }

    // =========================================================================
    // IO Layer (CLI consumption)
    // =========================================================================

    /// Walk a directory tree and return all file paths.
    let walkDirectoryIO (rootDir: string) : string list =
        if not (System.IO.Directory.Exists rootDir) then []
        else
            System.IO.Directory.EnumerateFiles(rootDir, "*", System.IO.SearchOption.AllDirectories)
            |> Seq.toList

    /// Read a file's content. Returns None on error.
    let readFileIO (path: string) : string option =
        try Some (System.IO.File.ReadAllText(path))
        with _ -> None

    /// Convenience: discover from a real directory with IO.
    let discoverFromDirectory
        (rootDirectory: string)
        (libraryNameHint: string option)
        : DiscoveryResult =
        if not (System.IO.Directory.Exists rootDirectory) then
            { RootDirectory = rootDirectory
              Files = []
              Diagnostics = [ DiagError (DirectoryNotFound rootDirectory) ]
              SuggestedLibraryName = libraryNameHint
              SuggestedIncludePaths = [] }
        else
            discover rootDirectory libraryNameHint walkDirectoryIO readFileIO
