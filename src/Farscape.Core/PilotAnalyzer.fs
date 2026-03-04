namespace Farscape.Core

/// Analyzes C declarations to discover rational namespace subdivisions
/// based on function name prefix and suffix patterns.
///
/// Architecture:
///   Catamorphism (DeclarationAlgebra) → function name list → clustering → PrefixGroup list
///   Suffix families handle cross-cutting patterns (printf/scanf).
///   Groups sharing a suggested name are merged, then minGroupSize applied.
///   Pure functions, no mutable state.
module PilotAnalyzer =

    open PilotTypes

    // =========================================================================
    // Prefix and Suffix Classification
    // =========================================================================

    /// Known suffix families: cross-cutting C naming patterns where
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
            // Try suffix families first; catches fprintf->"printf", vsnprintf->"printf", etc.
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
        OnDelegate  = fun _ -> None
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

    /// Groups larger than this threshold are split by sub-prefix.
    /// This keeps namespaces granular for compile-time reachability:
    /// when Clef `open`s a namespace, only those declarations enter the PSG.
    let private subPrefixThreshold = 30

    /// Extract the sub-prefix segment from a function name given its parent prefix.
    /// e.g. extractSubPrefix "gtk" "gtk_window_set_title" = Some "window"
    let private extractSubPrefix (parentPrefix: string) (funcName: string) : string option =
        let stripped =
            if funcName.StartsWith(parentPrefix + "_") then
                funcName.Substring(parentPrefix.Length + 1)
            elif funcName.StartsWith(parentPrefix) then
                funcName.Substring(parentPrefix.Length)
            else
                funcName
        match stripped.IndexOf('_') with
        | idx when idx > 0 -> Some (stripped.Substring(0, idx))
        | _ -> None

    /// Split a large prefix group into sub-groups by the next underscore-delimited segment.
    /// Groups below the threshold pass through unchanged.
    let private splitLargeGroup (group: PrefixGroup) : PrefixGroup list =
        if group.FunctionNames.Length < subPrefixThreshold then
            [group]
        else
            // Use the first (usually only) prefix as the parent
            let parentPrefix =
                match group.Prefixes with
                | p :: _ -> p
                | [] -> ""
            // Group functions by their sub-prefix segment
            let subGrouped =
                group.FunctionNames
                |> List.map (fun fn -> fn, extractSubPrefix parentPrefix fn)
                |> List.groupBy snd
                |> List.map (fun (subSeg, pairs) -> subSeg, pairs |> List.map fst)
            // Build sub-groups for segments with enough functions
            let subGroups =
                subGrouped
                |> List.choose (fun (subSeg, fns) ->
                    match subSeg with
                    | Some seg when fns.Length >= minGroupSize ->
                        let subPrefix = parentPrefix + "_" + seg
                        Some { Prefixes = [subPrefix]
                               FunctionNames = fns
                               SuggestedName = suggestNamespaceName seg }
                    | _ -> None)
            // Collect remainder (functions that didn't fit any sub-group)
            let subGroupedNames =
                subGroups |> List.collect (fun g -> g.FunctionNames) |> Set.ofList
            let remainder =
                group.FunctionNames |> List.filter (fun fn -> not (Set.contains fn subGroupedNames))
            if remainder.IsEmpty then
                subGroups
            else
                let remainderGroup =
                    { Prefixes = group.Prefixes
                      FunctionNames = remainder
                      SuggestedName = group.SuggestedName + "Core" }
                subGroups @ [remainderGroup]

    /// Split all large groups into sub-groups.
    let private splitLargeGroups (groups: PrefixGroup list) : PrefixGroup list =
        groups |> List.collect splitLargeGroup

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

        // Build initial groups, then split large ones by sub-prefix
        let initialGroups =
            merged |> List.map (fun (prefixes, fns, suggestedName) ->
                { Prefixes = prefixes
                  FunctionNames = fns
                  SuggestedName = suggestedName })

        let groups = splitLargeGroups initialGroups

        let groupedNames =
            groups |> List.collect (fun g -> g.FunctionNames) |> Set.ofList

        let ungrouped =
            names |> List.filter (fun n -> not (Set.contains n groupedNames))

        groups, ungrouped

    // =========================================================================
    // Callback Pattern Discovery
    // =========================================================================

    /// Check if a C type string represents a function pointer.
    let private isFunctionPointerType (typeStr: string) : bool =
        typeStr.Contains("(*)") || typeStr.Contains("(**)")

    /// Check if a parameter name matches common userdata/context naming patterns.
    let private isUserdataName (name: string) : bool =
        let lower = name.ToLowerInvariant().TrimStart('_')
        lower = "data" || lower = "user_data" || lower = "userdata"
        || lower = "user" || lower = "ctx" || lower = "context"
        || lower = "arg" || lower = "closure_data"

    /// Discover callback registration functions and listener structs from declarations.
    let discoverCallbacks (declarations: CppParser.Declaration list) : CallbackSpec =
        // Collect delegate type names — Wayland XML listener fields use these instead of (*)
        let delegateNames =
            declarations |> List.choose (function
                | CppParser.Declaration.Delegate d -> Some d.Name
                | _ -> None)
            |> Set.ofList

        // Collect typedef names that resolve to function pointer types
        let functionPointerTypedefs =
            declarations |> List.choose (function
                | CppParser.Declaration.Typedef { Name = name; UnderlyingType = ut }
                    when isFunctionPointerType ut -> Some name
                | _ -> None)
            |> Set.ofList

        /// Check if a parameter type is a callback (raw function pointer, delegate, or typedef'd fn ptr).
        let isCallbackType (typeStr: string) =
            isFunctionPointerType typeStr
            || Set.contains typeStr delegateNames
            || Set.contains typeStr functionPointerTypedefs

        // Pattern A: Find functions with function pointer + optional userdata parameters
        let registrations =
            declarations |> List.choose (function
                | CppParser.Declaration.Function f ->
                    let callbackParams =
                        f.Parameters |> List.filter (fun (_, t) -> isCallbackType t)
                    let dataParams =
                        f.Parameters |> List.filter (fun (name, t) ->
                            t.Contains("void") && t.Contains("*") && isUserdataName name)
                    match callbackParams, dataParams with
                    | [(cbName, _)], [(dataName, _)] ->
                        Some { Function = f.Name; CallbackParam = cbName; DataParam = Some dataName }
                    | [(cbName, _)], [] ->
                        Some { Function = f.Name; CallbackParam = cbName; DataParam = None }
                    | _ -> None
                | _ -> None)

        // Pattern B: Find structs where >50% of fields are function pointers or delegate types
        let listenerStructs =
            declarations |> List.choose (function
                | CppParser.Declaration.Struct s when s.Name <> "" && s.Fields.Length >= 1 ->
                    let fpFields = s.Fields |> List.filter (fun f -> isCallbackType f.Type)
                    let ratio = float fpFields.Length / float s.Fields.Length
                    if ratio > 0.5 then
                        // Try to find companion add_listener/set_*_handler registration function
                        let regFn =
                            declarations |> List.tryPick (function
                                | CppParser.Declaration.Function f
                                    when (f.Name.EndsWith("_add_listener") || f.Name.Contains("_set_"))
                                      && f.Parameters |> List.exists (fun (_, t) ->
                                            t.Contains(s.Name)) -> Some f.Name
                                | _ -> None)
                        Some { Name = s.Name; RegistrationFunction = regFn }
                    else None
                | _ -> None)

        { Registrations = registrations; ListenerStructs = listenerStructs }

    // =========================================================================
    // Public API
    // =========================================================================

    /// Analyze declarations and produce an AnalysisResult.
    /// This is the main entry point for `farscape pilot analyze`.
    let analyze (declarations: CppParser.Declaration list) : AnalysisResult =
        let names = extractFunctionNames declarations
        let groups, ungrouped = clusterByPrefix names
        { Groups = groups
          Ungrouped = ungrouped
          TotalFunctions = names.Length }

    /// Convert a library name to a PascalCase namespace root segment.
    /// e.g. "gtk-3" → "Gtk3", "webkit2gtk-4.1" → "Webkit2gtk", "libc" → "Libc"
    let toNamespaceRoot (libraryName: string) : string =
        // Split on delimiters, filter out dotted version segments (e.g. "4.1", "2.0")
        // but keep simple numbers (e.g. "3" in "gtk-3") as they distinguish major versions
        let isDottedVersion (s: string) =
            s.Contains('.') && s |> Seq.forall (fun c -> System.Char.IsDigit c || c = '.')
        let segments =
            libraryName.Split([|'-'; '_'|], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun s -> not (isDottedVersion s))
        if segments.Length = 0 then libraryName
        else
            segments
            |> Array.map (fun s ->
                if s.Length > 0 then
                    string (System.Char.ToUpperInvariant(s[0])) + s.Substring(1)
                else s)
            |> String.concat ""

    /// Convert an AnalysisResult to a PilotProject with default settings.
    /// Ungrouped functions are collected into a catch-all "Core" namespace.
    let toPilotProject
        (libraryName: string)
        (headerFiles: string list)
        (includePaths: string list)
        (defines: string list)
        (pkgConfig: string list)
        (outputMode: string)
        (outputDir: string)
        (result: AnalysisResult) : PilotProject =
        let nsRoot = toNamespaceRoot libraryName
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
                { Name = $"Fidelity.{nsRoot}.{g.SuggestedName}"
                  Description = description
                  Library = libraryName
                  Prefixes = effectivePrefixes
                  Functions = explicitFunctions
                  XmlInterfaces = [] })
        // Add catch-all namespace for ungrouped functions
        let catchAll =
            if result.Ungrouped.IsEmpty then []
            else
                [ { Name = $"Fidelity.{nsRoot}.Core"
                    Description = "Ungrouped functions"
                    Library = libraryName
                    Prefixes = []
                    Functions = result.Ungrouped
                    XmlInterfaces = [] } ]
        { Library =
            { Name = libraryName
              Headers = headerFiles
              XmlProtocols = []
              IncludePaths = includePaths
              Defines = defines
              MacroPrefixes = []
              PkgConfig = pkgConfig }
          Output =
            { Mode = outputMode
              Directory = outputDir }
          Namespaces = namespaces @ catchAll
          ErrorConventions = None
          Options = None
          Callbacks = None
          Nonnull = None }

    // =========================================================================
    // Declaration Filtering (for scoped generation)
    // =========================================================================

    /// Extract the identifying name from any declaration (for filtering).
    let private declarationName (decl: CppParser.Declaration) : string option =
        match decl with
        | CppParser.Declaration.Function f -> Some f.Name
        | CppParser.Declaration.Struct s -> if s.Name <> "" then Some s.Name else None
        | CppParser.Declaration.Enum e -> if e.Name <> "" then Some e.Name else None
        | CppParser.Declaration.Typedef t -> Some t.Name
        | CppParser.Declaration.Macro m -> Some m.Name
        | CppParser.Declaration.Namespace n -> Some n.Name
        | CppParser.Declaration.Class c -> if c.Name <> "" then Some c.Name else None
        | CppParser.Declaration.Delegate d -> Some d.Name

    /// Convert snake_case to PascalCase (for matching delegate names to interfaces).
    let private toPascalCase (s: string) =
        s.Split('_')
        |> Array.map (fun part ->
            if part.Length = 0 then ""
            else (string (System.Char.ToUpper part[0])) + part[1..])
        |> String.concat ""

    /// Filter declarations to only those matching a NamespaceSpec.
    /// A function matches if:
    ///   1. Its name starts with any prefix in spec.Prefixes, OR
    ///   2. Its name is explicitly listed in spec.Functions
    /// When XmlInterfaces is non-empty, declarations from XML protocols are
    /// included if their name starts with any listed interface name (snake_case)
    /// or matches a delegate name pattern (PascalCase).
    /// Non-function declarations (structs, enums, typedefs, macros) pass through
    /// unfiltered when no XmlInterfaces filter is active.
    let filterDeclarationsForNamespace
        (spec: NamespaceSpec)
        (declarations: CppParser.Declaration list) : CppParser.Declaration list =
        let explicitSet = Set.ofList spec.Functions
        let xmlInterfacePascals =
            spec.XmlInterfaces |> List.map toPascalCase
        declarations |> List.filter (fun decl ->
            match decl with
            | CppParser.Declaration.Function f ->
                let matchesPrefix =
                    spec.Prefixes |> List.exists (fun prefix -> f.Name.StartsWith prefix)
                let matchesExplicit = Set.contains f.Name explicitSet
                let matchesXmlInterface =
                    spec.XmlInterfaces |> List.exists (fun iface ->
                        f.Name.StartsWith(iface + "_") || f.Name = iface)
                matchesPrefix || matchesExplicit || matchesXmlInterface
            | _ when not spec.XmlInterfaces.IsEmpty ->
                // When XML interface filtering is active, non-function declarations
                // must also match: name starts with interface name (snake_case)
                // or matches delegate PascalCase pattern
                match declarationName decl with
                | Some name ->
                    let matchesSnake =
                        spec.XmlInterfaces |> List.exists (fun iface ->
                            name.StartsWith(iface + "_") || name = iface)
                    let matchesPascal =
                        xmlInterfacePascals |> List.exists (fun pascal ->
                            name.StartsWith pascal)
                    matchesSnake || matchesPascal
                | None -> true
            | _ -> true)

    // =========================================================================
    // Type-Dependency Analysis (for multi-namespace projects)
    // =========================================================================

    /// Extract base C type names referenced by a function's parameters and return type.
    /// Uses CTypeParser to correctly strip qualifiers and pointer depth.
    let extractFunctionTypeRefs (func: CppParser.FunctionDecl) : Set<string> =
        let extractBase (cType: string) =
            match CTypeParser.tryParseCType cType with
            | Some info -> info.BaseType
            | None -> cType.Trim()
        let allRawTypes = func.ReturnType :: (func.Parameters |> List.map snd)
        allRawTypes |> List.map extractBase |> Set.ofList

    /// Get the set of all non-function declaration names (types, enums, structs, typedefs, macros).
    let declaredTypeNames (declarations: CppParser.Declaration list) : Set<string> =
        declarations
        |> List.choose (fun d ->
            match d with
            | CppParser.Declaration.Function _ -> None
            | _ -> declarationName d)
        |> Set.ofList

    /// Determine which declared type names are referenced by a list of functions.
    let referencedTypeNames (functions: CppParser.FunctionDecl list) (declNames: Set<string>) : Set<string> =
        let allRefs = functions |> List.map extractFunctionTypeRefs |> Set.unionMany
        Set.intersect allRefs declNames

    /// Result of type classification for multi-namespace code generation.
    type TypeClassification = {
        /// Types referenced by 2+ namespaces, plus orphan types (unreferenced by any).
        SharedTypes: Set<string>
        /// Namespace name -> set of type names local to that namespace only.
        LocalTypes: Map<string, Set<string>>
    }

    /// Classify types across namespaces for a multi-namespace project.
    /// Types referenced by 2+ namespaces go to shared; 1 namespace = local; 0 = shared (orphan).
    let classifyProjectTypes
        (namespaces: NamespaceSpec list)
        (declarations: CppParser.Declaration list) : TypeClassification =
        let allDeclNames = declaredTypeNames declarations

        // For each namespace, determine which types its functions reference
        let nsTypeRefs =
            namespaces |> List.map (fun ns ->
                let filtered = filterDeclarationsForNamespace ns declarations
                let funcs =
                    filtered |> List.choose (function
                        | CppParser.Declaration.Function f -> Some f
                        | _ -> None)
                (ns.Name, referencedTypeNames funcs allDeclNames))

        // Count how many namespaces reference each type
        let typeCounts =
            nsTypeRefs
            |> List.collect (fun (_, refs) -> Set.toList refs)
            |> List.countBy id
            |> Map.ofList

        let referencedByAny = nsTypeRefs |> List.map snd |> Set.unionMany
        let orphans = Set.difference allDeclNames referencedByAny

        let shared =
            typeCounts
            |> Map.toSeq
            |> Seq.filter (fun (_, count) -> count >= 2)
            |> Seq.map fst
            |> Set.ofSeq
            |> Set.union orphans

        let localTypes =
            nsTypeRefs |> List.map (fun (nsName, refs) ->
                (nsName, Set.difference refs shared))
            |> Map.ofList

        { SharedTypes = shared; LocalTypes = localTypes }

    /// Filter declarations to only types (no functions) whose names are in the given set.
    let filterTypesOnly (typeNames: Set<string>) (declarations: CppParser.Declaration list) : CppParser.Declaration list =
        declarations |> List.filter (fun decl ->
            match decl with
            | CppParser.Declaration.Function _ -> false
            | _ ->
                match declarationName decl with
                | Some name -> Set.contains name typeNames
                | None -> false)

    /// Filter declarations for a namespace: functions by prefix/name, types by explicit set.
    let filterDeclarationsWithTypes
        (spec: NamespaceSpec)
        (typeNames: Set<string>)
        (declarations: CppParser.Declaration list) : CppParser.Declaration list =
        let explicitSet = Set.ofList spec.Functions
        declarations |> List.filter (fun decl ->
            match decl with
            | CppParser.Declaration.Function f ->
                let matchesPrefix =
                    spec.Prefixes |> List.exists (fun prefix -> f.Name.StartsWith prefix)
                let matchesExplicit = Set.contains f.Name explicitSet
                let matchesXmlInterface =
                    spec.XmlInterfaces |> List.exists (fun iface ->
                        f.Name.StartsWith(iface + "_") || f.Name = iface)
                matchesPrefix || matchesExplicit || matchesXmlInterface
            | _ ->
                match declarationName decl with
                | Some name -> Set.contains name typeNames
                | None -> false)
