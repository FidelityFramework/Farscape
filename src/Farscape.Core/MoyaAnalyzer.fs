namespace Farscape.Core

/// Analyzes C declarations to discover rational namespace subdivisions
/// based on function name prefix and suffix patterns.
///
/// Architecture:
///   Catamorphism (DeclarationAlgebra) → function name list → clustering → PrefixGroup list
///   Suffix families handle cross-cutting patterns (printf/scanf).
///   Groups sharing a suggested name are merged, then minGroupSize applied.
///   Pure functions, no mutable state.
module MoyaAnalyzer =

    open MoyaTypes

    // =========================================================================
    // Prefix and Suffix Classification
    // =========================================================================

    /// Known suffix families — cross-cutting C naming patterns where
    /// the base function name appears as a suffix (fprintf = f + printf).
    let private knownSuffixFamilies =
        [ "printf"; "scanf" ]

    /// Known short prefixes for standard C library functions
    /// that don't use underscore separation.
    /// Sorted longest-first so "strftime" doesn't incorrectly match "str".
    let private knownCPrefixes =
        [ "realloc"; "calloc"; "malloc"
          "fwrite"; "fread"; "fopen"; "fclose"; "fseek"; "ftell"; "fflush"; "fgets"; "fputs"
          "strftime"; "str"
          "mem"
          "write"; "read"; "open"; "close"; "lseek"
          "tmp"; "set"
          "wcs"; "sem"; "sig"; "ato"; "div"
          "put"; "get" ]

    /// Detect prefix from a C function name.
    /// Strategies in priority order:
    ///   1. HAL-style: "HAL_GPIO_Init" → "HAL_GPIO"
    ///   2. Underscore-separated: "io_read" → "io"
    ///   3. Suffix family: "fprintf" → "printf", "sscanf" → "scanf"
    ///   4. Known C library prefix: "strlen" → "str", "fclose" → "fclose"
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
            // Try suffix families first — catches fprintf→"printf", vsnprintf→"printf", etc.
            match knownSuffixFamilies |> List.tryFind (fun suffix -> name.EndsWith suffix) with
            | Some suffix -> Some suffix
            | None ->
                // Try known C library prefixes (longest match first, exact match allowed)
                knownCPrefixes
                |> List.tryFind (fun prefix -> name.StartsWith(prefix) && name.Length >= prefix.Length)

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
        | "printf" -> "Format"
        | "scanf" -> "Scan"
        | "fwrite" | "fread" | "fopen" | "fclose" | "fseek" | "ftell" | "fflush" | "fgets" | "fputs" -> "FileIO"
        | "sig" -> "Signal"
        | "sem" -> "Semaphore"
        | "ato" -> "Conversion"
        | "put" | "get" -> "IO"
        | "div" -> "Math"
        | "tmp" -> "Temp"
        | "set" -> "Buffering"
        | p ->
            // For underscore-separated and HAL-style: capitalize each segment
            p.Split('_')
            |> Array.map (fun s ->
                if s.Length > 0 then
                    string (System.Char.ToUpperInvariant(s[0])) + s.Substring(1)
                else s)
            |> String.concat ""

    // =========================================================================
    // Clustering with Merge
    // =========================================================================

    /// Minimum number of functions sharing a pattern to form a group.
    let private minGroupSize = 2

    /// Cluster function names by extracted prefix, merge groups that map
    /// to the same suggested namespace name, then apply minGroupSize.
    let clusterByPrefix (names: string list) : PrefixGroup list * string list =
        let withPrefixes =
            names |> List.map (fun name -> name, extractPrefix name)

        // Group by raw prefix
        let rawGroups =
            withPrefixes
            |> List.choose (fun (name, prefix) -> prefix |> Option.map (fun p -> p, name))
            |> List.groupBy fst
            |> List.map (fun (prefix, pairs) -> prefix, pairs |> List.map snd)

        // Merge groups that share the same suggestedNamespaceName, then apply minGroupSize
        let merged =
            rawGroups
            |> List.groupBy (fun (prefix, _) -> suggestNamespaceName prefix)
            |> List.map (fun (suggestedName, groups) ->
                let allPrefixes = groups |> List.map fst
                let allFunctions = groups |> List.collect snd
                allPrefixes, allFunctions, suggestedName)
            |> List.filter (fun (_, fns, _) -> fns.Length >= minGroupSize)

        let groupedNames =
            merged |> List.collect (fun (_, fns, _) -> fns) |> Set.ofList

        let ungrouped =
            names |> List.filter (fun n -> not (Set.contains n groupedNames))

        let groups =
            merged |> List.map (fun (prefixes, fns, suggestedName) ->
                { Prefixes = prefixes
                  FunctionNames = fns
                  SuggestedName = suggestedName })

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
    /// Ungrouped functions are collected into a catch-all "Core" namespace.
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
                // Functions not covered by any prefix go into explicit functions list
                let coveredByPrefix (fn: string) =
                    g.Prefixes |> List.exists (fun p -> fn.StartsWith p && fn <> p)
                let explicitFunctions =
                    g.FunctionNames |> List.filter (fun fn -> not (coveredByPrefix fn))
                // Prefixes that actually extend to longer function names
                let effectivePrefixes =
                    g.Prefixes |> List.filter (fun p ->
                        g.FunctionNames |> List.exists (fun fn -> fn.StartsWith p && fn <> p))
                let description =
                    match effectivePrefixes with
                    | [p] -> sprintf "Functions with prefix '%s'" p
                    | ps when ps.Length > 0 ->
                        let matching = ps |> List.map (fun p -> p + "*") |> String.concat ", "
                        sprintf "Functions matching: %s" matching
                    | _ -> sprintf "%s functions" g.SuggestedName
                { Name = $"Fidelity.{libraryName}.{g.SuggestedName}"
                  Description = description
                  Library = libraryName
                  Prefixes = effectivePrefixes
                  Functions = explicitFunctions })
        // Add catch-all namespace for ungrouped functions
        let catchAll =
            if result.Ungrouped.IsEmpty then []
            else
                [ { Name = $"Fidelity.{libraryName}.Core"
                    Description = "Ungrouped functions"
                    Library = libraryName
                    Prefixes = []
                    Functions = result.Ungrouped } ]
        { Library =
            { Name = libraryName
              Header = headerFile
              IncludePaths = includePaths
              Defines = defines }
          Output =
            { Mode = outputMode
              Directory = outputDir }
          Namespaces = namespaces @ catchAll }

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
