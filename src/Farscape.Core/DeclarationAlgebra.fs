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
            | CppParser.Declaration.Class c     -> algebra.OnClass c)

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
    }

    /// Extract struct names from declarations.
    /// Non-struct declarations produce None.
    let structNameAlgebra : DeclarationAlgebra<string option> = {
        OnStruct    = fun s -> if s.Name <> "" then Some s.Name else None
        OnClass     = fun c -> if c.Methods.IsEmpty && c.Name <> "" then Some c.Name else None
        OnFunction  = fun _ -> None
        OnEnum      = fun _ -> None
        OnTypedef   = fun _ -> None
        OnMacro     = fun _ -> None
        OnNamespace = fun _ -> None
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
    }

    /// Merge declaration lists from multiple headers, deduplicating by name.
    /// First-occurrence-wins: if the same typedef/struct/enum/macro appears
    /// in multiple headers (common for shared system types like size_t, pid_t),
    /// the first parsed version is kept.
    let mergeDeclarations (declLists: CppParser.Declaration list list) : CppParser.Declaration list =
        let seen = System.Collections.Generic.HashSet<string>()
        declLists
        |> List.concat
        |> List.filter (fun decl ->
            let names = cataDeclarations declarationNameAlgebra [decl]
            match names with
            | [Some name] -> seen.Add(name)
            | _ -> true)
