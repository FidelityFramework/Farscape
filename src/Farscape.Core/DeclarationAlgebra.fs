namespace Farscape.Core

/// Catamorphism (fold algebra) over the CppParser.Declaration discriminated union.
///
/// Instead of repeated `List.choose (function | Function f -> ... | _ -> None)` patterns,
/// define an algebra with one function per variant and fold it over a declaration list.
/// This is the ONLY traversal function; everything uses it.
module DeclarationAlgebra =

    /// Fold algebra: one function per Declaration variant.
    /// Each function maps a declaration to a result of type 'R.
    type DeclarationAlgebra<'R> = {
        OnFunction:  CppParser.FunctionDecl  -> 'R
        OnStruct:    CppParser.StructDecl    -> 'R
        OnEnum:      CppParser.EnumDecl      -> 'R
        OnTypedef:   CppParser.TypedefInfo   -> 'R
        OnMacro:     CppParser.MacroDecl     -> 'R
        OnNamespace: CppParser.NamespaceDecl -> 'R
        OnClass:     CppParser.ClassDecl     -> 'R
        OnDelegate:  CppParser.DelegateDecl  -> 'R
    }

    /// The catamorphism: apply an algebra to each declaration in a list.
    /// This is the single, canonical traversal of Declaration lists.
    let cataDeclarations (algebra: DeclarationAlgebra<'R>) (decls: CppParser.Declaration list) : 'R list =
        decls |> List.map (fun decl ->
            match decl with
            | CppParser.Declaration.Function f  -> algebra.OnFunction f
            | CppParser.Declaration.Struct s    -> algebra.OnStruct s
            | CppParser.Declaration.Enum e      -> algebra.OnEnum e
            | CppParser.Declaration.Typedef t   -> algebra.OnTypedef t
            | CppParser.Declaration.Macro m     -> algebra.OnMacro m
            | CppParser.Declaration.Namespace n -> algebra.OnNamespace n
            | CppParser.Declaration.Class c     -> algebra.OnClass c
            | CppParser.Declaration.Delegate d  -> algebra.OnDelegate d)

    // =========================================================================
    // Pre-built Algebras
    // =========================================================================

    /// Extract typedefs as (name, underlyingType) pairs.
    /// Non-typedef declarations produce None.
    let typedefAlgebra : DeclarationAlgebra<(string * string) option> = {
        OnTypedef   = fun t -> Some (t.Name, t.UnderlyingType)
        OnFunction  = fun _ -> None
        OnStruct    = fun _ -> None
        OnEnum      = fun _ -> None
        OnMacro     = fun _ -> None
        OnNamespace = fun _ -> None
        OnClass     = fun _ -> None
        OnDelegate  = fun _ -> None
    }

    /// Extract struct names from declarations (includes forward declarations).
    /// Non-struct declarations produce None.
    let structNameAlgebra : DeclarationAlgebra<string option> = {
        OnStruct    = fun s -> if s.Name <> "" then Some s.Name else None
        OnClass     = fun c -> if c.Methods.IsEmpty && c.Name <> "" then Some c.Name else None
        OnFunction  = fun _ -> None
        OnEnum      = fun _ -> None
        OnTypedef   = fun _ -> None
        OnMacro     = fun _ -> None
        OnNamespace = fun _ -> None
        OnDelegate  = fun _ -> None
    }

    /// Extract names of fully-defined structs (those with fields).
    /// Forward-declared structs (zero fields) are excluded.
    /// Used by opaque handle detection to distinguish defined vs incomplete types.
    let definedStructNameAlgebra : DeclarationAlgebra<string option> = {
        OnStruct    = fun s -> if s.Name <> "" && not s.Fields.IsEmpty then Some s.Name else None
        OnClass     = fun c -> if c.Methods.IsEmpty && c.Name <> "" then Some c.Name else None
        OnFunction  = fun _ -> None
        OnEnum      = fun _ -> None
        OnTypedef   = fun _ -> None
        OnMacro     = fun _ -> None
        OnNamespace = fun _ -> None
        OnDelegate  = fun _ -> None
    }

    /// Extract the identifying name from any declaration variant.
    /// Used for deduplication when merging declarations from multiple headers.
    let declarationNameAlgebra : DeclarationAlgebra<string option> = {
        OnFunction  = fun f -> Some f.Name
        OnStruct    = fun s -> if s.Name <> "" then Some s.Name else None
        OnEnum      = fun e -> if e.Name <> "" then Some e.Name else None
        OnTypedef   = fun t -> Some t.Name
        OnMacro     = fun m -> Some m.Name
        OnNamespace = fun n -> Some n.Name
        OnClass     = fun c -> if c.Name <> "" then Some c.Name else None
        OnDelegate  = fun d -> Some d.Name
    }

    /// Extract a composite (kind, name) key for deduplication.
    /// Different declaration kinds with the same name (e.g. an enum "device"
    /// and a class "device") are distinct entities and must not collide.
    let private declarationKeyAlgebra : DeclarationAlgebra<(string * string) option> = {
        OnFunction  = fun f -> Some ("func", f.Name)
        OnStruct    = fun s -> if s.Name <> "" then Some ("struct", s.Name) else None
        OnEnum      = fun e -> if e.Name <> "" then Some ("enum", e.Name) else None
        OnTypedef   = fun t -> Some ("typedef", t.Name)
        OnMacro     = fun m -> Some ("macro", m.Name)
        OnNamespace = fun n -> Some ("namespace", n.Name)
        OnClass     = fun c -> if c.Name <> "" then Some ("class", c.Name) else None
        OnDelegate  = fun d -> Some ("delegate", d.Name)
    }

    /// Compute a "completeness" score for a class declaration.
    /// Higher score = more structural information captured. When the same
    /// class appears in multiple translation units (via transitive includes),
    /// the version parsed from its primary header will have the most fields
    /// and methods because it sees the full definition.
    let private classCompleteness (decl: CppParser.Declaration) : int =
        match decl with
        | CppParser.Declaration.Class c ->
            // Fields are the most critical signal (pimpl detection depends on them)
            // Weight fields heavily, then methods + constructors
            c.Fields.Length * 10 + c.Methods.Length + c.Constructors.Length
        | _ -> 0

    /// Compute a "completeness" score for a struct declaration.
    let private structCompleteness (decl: CppParser.Declaration) : int =
        match decl with
        | CppParser.Declaration.Struct s -> s.Fields.Length
        | _ -> 0

    /// Merge declaration lists from multiple headers, deduplicating by (kind, name).
    /// First-occurrence-wins for most declarations. For classes and structs,
    /// the most complete definition wins: when the same class appears in multiple
    /// translation units via transitive includes, the version from its primary
    /// header (with fields visible) supersedes partial views from other headers.
    let mergeDeclarations (declLists: CppParser.Declaration list list) : CppParser.Declaration list =
        let seen = System.Collections.Generic.Dictionary<string * string, int>()
        let result = ResizeArray<CppParser.Declaration>()
        for decl in List.concat declLists do
            let keys = cataDeclarations declarationKeyAlgebra [decl]
            match keys with
            | [Some key] ->
                match seen.TryGetValue(key) with
                | false, _ ->
                    seen.[key] <- result.Count
                    result.Add(decl)
                | true, idx ->
                    // Already seen. Replace if the new declaration is more complete.
                    let existing = result.[idx]
                    let shouldReplace =
                        match fst key with
                        | "class" -> classCompleteness decl > classCompleteness existing
                        | "struct" -> structCompleteness decl > structCompleteness existing
                        | _ -> false
                    if shouldReplace then
                        result.[idx] <- decl
            | _ -> result.Add(decl)
        List.ofSeq result
