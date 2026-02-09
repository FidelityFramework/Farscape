namespace Farscape.Core

/// Analyzes C declarations to discover rational namespace subdivisions
/// based on function name prefix patterns.
///
/// Architecture:
///   Catamorphism (DeclarationAlgebra) → function name list → prefix clustering → PrefixGroup list
///   Active patterns for prefix classification.
///   Pure functions, no mutable state.
module MoyaAnalyzer =

    open MoyaTypes

    // =========================================================================
    // Active Patterns for Prefix Classification
    // =========================================================================

    /// Known short prefixes for standard C library functions
    /// that don't use underscore separation.
    /// Sorted longest-first so "strftime" doesn't incorrectly match "str".
    let private knownCPrefixes =
        [ "realloc"; "calloc"; "malloc"
          "snprintf"; "sprintf"; "fprintf"; "printf"
          "sscanf"; "fscanf"; "scanf"
          "fwrite"; "fread"; "fopen"; "fclose"; "fseek"; "ftell"; "fflush"; "fgets"; "fputs"
          "strftime"; "str"
          "mem"
          "write"; "read"; "open"; "close"; "lseek"
          "wcs"; "sem"; "sig"; "ato"; "div"
          "put"; "get" ]

    /// Detect prefix from a C function name.
    /// Strategies in priority order:
    ///   1. HAL-style: "HAL_GPIO_Init" → "HAL_GPIO"
    ///   2. Underscore-separated: "io_read" → "io"
    ///   3. Known C library prefix: "strlen" → "str"
    let extractPrefix (name: string) : string option =
        // HAL-style: at least two underscore-separated segments before the "verb"
        let parts = name.Split('_')
        if parts.Length >= 3 && parts[0].Length >= 2 && System.Char.IsUpper(parts[0][0]) then
            // e.g., HAL_GPIO_Init → "HAL_GPIO"
            Some (parts[0] + "_" + parts[1])
        elif parts.Length >= 2 && parts[0].Length >= 2 then
            // Simple underscore-separated: io_read → "io"
            Some parts[0]
        else
            // Try known C library prefixes (longest match first)
            knownCPrefixes
            |> List.tryFind (fun prefix -> name.StartsWith(prefix) && name.Length > prefix.Length)

    // =========================================================================
    // Catamorphism-based Function Name Extraction
    // =========================================================================

    /// DeclarationAlgebra that extracts function names.
    /// Non-function declarations produce None.
    let functionNameAlgebra : DeclarationAlgebra.DeclarationAlgebra<string option> = {
        OnFunction  = fun f -> Some f.Name
        OnStruct    = fun _ -> None
        OnEnum      = fun _ -> None
        OnTypedef   = fun _ -> None
        OnMacro     = fun _ -> None
        OnNamespace = fun _ -> None
        OnClass     = fun _ -> None
    }

    /// Extract all function names from a declaration list using the catamorphism.
    let extractFunctionNames (declarations: CppParser.Declaration list) : string list =
        DeclarationAlgebra.cataDeclarations functionNameAlgebra declarations
        |> List.choose id

    // =========================================================================
    // Namespace Name Suggestion
    // =========================================================================

    /// Suggest a PascalCase namespace suffix from a prefix.
    let suggestNamespaceName (prefix: string) : string =
        match prefix.ToLowerInvariant() with
        | "str" | "strftime" -> "String"
        | "mem" -> "Memory"
        | "wcs" -> "WideString"
        | "io" | "read" | "write" | "open" | "close" | "lseek" -> "IO"
        | "malloc" | "calloc" | "realloc" | "free" -> "Alloc"
        | "printf" | "sprintf" | "fprintf" | "snprintf" -> "Format"
        | "scanf" | "sscanf" | "fscanf" -> "Scan"
        | "fwrite" | "fread" | "fopen" | "fclose" | "fseek" | "ftell" | "fflush" | "fgets" | "fputs" -> "FileIO"
        | "sig" -> "Signal"
        | "sem" -> "Semaphore"
        | "ato" -> "Conversion"
        | "put" | "get" -> "IO"
        | "div" -> "Math"
        | p ->
            // For underscore-separated and HAL-style: capitalize each segment
            p.Split('_')
            |> Array.map (fun s ->
                if s.Length > 0 then
                    string (System.Char.ToUpperInvariant(s[0])) + s.Substring(1)
                else s)
            |> String.concat ""

    // =========================================================================
    // Prefix Clustering
    // =========================================================================

    /// Minimum number of functions sharing a prefix to form a group.
    let private minGroupSize = 2

    /// Cluster function names by their extracted prefix.
    /// Returns groups with >= minGroupSize members and the list of ungrouped names.
    let clusterByPrefix (names: string list) : PrefixGroup list * string list =
        let withPrefixes =
            names |> List.map (fun name -> name, extractPrefix name)

        let grouped =
            withPrefixes
            |> List.choose (fun (name, prefix) -> prefix |> Option.map (fun p -> p, name))
            |> List.groupBy fst
            |> List.map (fun (prefix, pairs) ->
                prefix, pairs |> List.map snd)
            |> List.filter (fun (_, fns) -> fns.Length >= minGroupSize)

        let groupedNames =
            grouped |> List.collect snd |> Set.ofList

        let ungrouped =
            names |> List.filter (fun n -> not (Set.contains n groupedNames))

        let groups =
            grouped |> List.map (fun (prefix, fns) ->
                { Prefix = prefix
                  FunctionNames = fns
                  SuggestedName = suggestNamespaceName prefix })

        groups, ungrouped

    // =========================================================================
    // Public API
    // =========================================================================

    /// Analyze declarations and produce an AnalysisResult.
    /// This is the main entry point for `farscape moya analyze`.
    let analyze (declarations: CppParser.Declaration list) : AnalysisResult =
        let names = extractFunctionNames declarations
        let groups, ungrouped = clusterByPrefix names
        { Groups = groups
          Ungrouped = ungrouped
          TotalFunctions = names.Length }

    /// Convert an AnalysisResult to a MoyaProject with default settings.
    let toMoyaProject
        (libraryName: string)
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (outputMode: string)
        (outputDir: string)
        (result: AnalysisResult) : MoyaProject =
        let namespaces =
            result.Groups |> List.map (fun g ->
                { Name = $"Fidelity.{libraryName}.{g.SuggestedName}"
                  Description = $"Functions with prefix '{g.Prefix}'"
                  Library = libraryName
                  Prefixes = [ g.Prefix ]
                  Functions = [] })
        { Library =
            { Name = libraryName
              Header = headerFile
              IncludePaths = includePaths
              Defines = defines }
          Output =
            { Mode = outputMode
              Directory = outputDir }
          Namespaces = namespaces }

    // =========================================================================
    // Declaration Filtering (for scoped generation)
    // =========================================================================

    /// Filter declarations to only those matching a NamespaceSpec.
    /// A function matches if:
    ///   1. Its name starts with any prefix in spec.Prefixes, OR
    ///   2. Its name is explicitly listed in spec.Functions
    /// Non-function declarations (structs, enums, typedefs, macros) pass through
    /// unfiltered since they may be needed by any namespace.
    let filterDeclarationsForNamespace
        (spec: NamespaceSpec)
        (declarations: CppParser.Declaration list) : CppParser.Declaration list =
        let explicitSet = Set.ofList spec.Functions
        declarations |> List.filter (fun decl ->
            match decl with
            | CppParser.Declaration.Function f ->
                let matchesPrefix =
                    spec.Prefixes |> List.exists (fun prefix -> f.Name.StartsWith(prefix))
                let matchesExplicit = Set.contains f.Name explicitSet
                matchesPrefix || matchesExplicit
            | _ -> true)
