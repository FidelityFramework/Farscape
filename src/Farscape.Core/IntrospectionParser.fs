namespace Farscape.Core

open System.IO
open Fidelity.Data.XML

/// Reads GObject introspection data (`*.gir`) into signal contracts. A signal
/// handler is declared in C as `GCallback`, `void (*)(void)`, so the entry's
/// parameters, result and C types come from the installed introspection data of
/// the class that emits the signal.
module IntrospectionParser =

    /// A type reference as GIR spells it: a name, optionally its C spelling.
    type TypeRef = {
        Name: string
        CType: string option
        IsArray: bool
    }

    /// One parameter of a signal's C entry, after the instance and before user data.
    type SignalParameter = {
        Name: string
        Type: TypeRef
        Nullable: bool
    }

    /// A signal as its namespace declares it.
    type RawSignal = {
        Class: string
        ClassCType: string
        /// The class's C symbol prefix under the namespace's, e.g. `user_content_manager`.
        SymbolPrefix: string
        Name: string
        Parameters: SignalParameter list
        Return: TypeRef
        Documentation: string option
    }

    /// One introspection namespace: its named types with their C spellings and its signals.
    type Namespace = {
        Name: string
        SymbolPrefix: string
        /// GIR type name to C spelling; object and record types carry their pointer star.
        Types: Map<string, string>
        Signals: RawSignal list
    }

    type Repository = {
        Namespaces: Map<string, Namespace>
    }

    /// A signal's C entry with every type resolved to its C spelling.
    type SignalDecl = {
        Namespace: string
        Class: string
        ClassCType: string
        /// The C symbol prefix of the entry, e.g. `webkit_user_content_manager`.
        SymbolPrefix: string
        Name: string
        Parameters: (string * string * bool) list
        ReturnCType: string
        Documentation: string option
    }

    let private attr name node = XmlNode.attr name node |> Option.filter (fun v -> v <> "")
    let private attrOr name fallback node = attr name node |> Option.defaultValue fallback

    let private typeRefOf (node: XmlNode) : TypeRef option =
        match XmlNode.element "type" node, XmlNode.element "array" node with
        | Some t, _ -> Some { Name = attrOr "name" "" t; CType = attr "c:type" t; IsArray = false }
        | None, Some a -> Some { Name = attrOr "name" "" a; CType = attr "c:type" a; IsArray = true }
        | None, None -> None

    let private firstDocLine (node: XmlNode) : string option =
        XmlNode.element "doc" node
        |> Option.map XmlNode.innerText
        |> Option.map (fun text -> text.Trim().Split('\n').[0].Trim())
        |> Option.filter (fun line -> line <> "")

    let private signalsOf (nsSymbolPrefix: string) (cls: XmlNode) : RawSignal list =
        match attr "name" cls, attr "c:type" cls with
        | Some className, Some cType ->
            let symbolPrefix = attrOr "c:symbol-prefix" (className.ToLowerInvariant()) cls
            XmlNode.elementsNamed "glib:signal" cls
            |> List.choose (fun signal ->
                let parameters =
                    XmlNode.element "parameters" signal
                    |> Option.map (XmlNode.elementsNamed "parameter")
                    |> Option.defaultValue []
                    |> List.choose (fun p ->
                        typeRefOf p |> Option.map (fun t ->
                            { Name = attrOr "name" "" p; Type = t
                              Nullable = attr "nullable" p = Some "1" || attr "allow-none" p = Some "1" }))
                let result =
                    XmlNode.element "return-value" signal |> Option.bind typeRefOf
                match attr "name" signal, result with
                | Some name, Some ret ->
                    Some { Class = className; ClassCType = cType; SymbolPrefix = nsSymbolPrefix + "_" + symbolPrefix
                           Name = name; Parameters = parameters; Return = ret; Documentation = firstDocLine signal }
                | _ -> None)
        | _ -> []

    /// Parse one introspection document.
    let parseDocument (content: string) : Result<Namespace, string> =
        match Xml.parse content with
        | Error e -> Error $"introspection XML: {e}"
        | Ok doc ->
            match XmlNode.element "namespace" doc.Root with
            | None -> Error "introspection XML has no <namespace> under <repository>"
            | Some ns ->
                let name = attrOr "name" "" ns
                let symbolPrefix = (attrOr "c:symbol-prefixes" (name.ToLowerInvariant()) ns).Split(',').[0]
                let pointerTypes =
                    [ "class"; "interface"; "record"; "union" ]
                    |> List.collect (fun kind -> XmlNode.elementsNamed kind ns)
                    |> List.choose (fun node ->
                        match attr "name" node, attr "c:type" node with
                        | Some n, Some c -> Some (n, c + "*")
                        | _ -> None)
                let valueTypes =
                    [ "enumeration"; "bitfield"; "alias" ]
                    |> List.collect (fun kind -> XmlNode.elementsNamed kind ns)
                    |> List.choose (fun node ->
                        match attr "name" node, attr "c:type" node with
                        | Some n, Some c -> Some (n, c)
                        | _ -> None)
                let signals =
                    [ "class"; "interface" ]
                    |> List.collect (fun kind -> XmlNode.elementsNamed kind ns)
                    |> List.collect (signalsOf symbolPrefix)
                Ok { Name = name; SymbolPrefix = symbolPrefix
                     Types = Map.ofList (pointerTypes @ valueTypes); Signals = signals }

    /// Load the listed introspection files.
    let load (paths: string list) : Result<Repository, string> =
        let parsed =
            paths |> List.map (fun path ->
                if not (File.Exists path) then Error $"introspection file not found: {path}"
                else parseDocument (File.ReadAllText path) |> Result.mapError (fun e -> $"{path}: {e}"))
        match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some e -> Error e
        | None ->
            Ok { Namespaces = parsed |> List.choose (function Ok ns -> Some (ns.Name, ns) | Error _ -> None) |> Map.ofList }

    /// C spellings of GIR's fundamental types when the data carries none.
    let private fundamental (name: string) : string option =
        match name with
        | "none" -> Some "void"
        | "utf8" | "filename" -> Some "const gchar*"
        | "gboolean" | "gchar" | "guchar" | "gint" | "guint" | "gshort" | "gushort" | "glong" | "gulong"
        | "gint8" | "guint8" | "gint16" | "guint16" | "gint32" | "guint32" | "gint64" | "guint64"
        | "gsize" | "gssize" | "gfloat" | "gdouble" | "gpointer" | "gunichar" | "GType" -> Some name
        | _ -> None

    /// The C spelling of a type reference, through the listed namespaces.
    let private resolveType (repo: Repository) (current: string) (t: TypeRef) : Result<string, string> =
        if t.IsArray then Error $"array type '{t.Name}' is not projected"
        else
        match t.CType with
        | Some c -> Ok c
        | None ->
            let ns, local =
                match t.Name.IndexOf '.' with
                | -1 -> current, t.Name
                | i -> t.Name.Substring(0, i), t.Name.Substring(i + 1)
            match Map.tryFind ns repo.Namespaces with
            | None -> Error $"type '{t.Name}' is in namespace '{ns}', whose introspection file is not listed"
            | Some space ->
                match Map.tryFind local space.Types with
                | Some c -> Ok c
                | None ->
                    match fundamental local with
                    | Some c -> Ok c
                    | None -> Error $"type '{t.Name}' has no C spelling in the introspection data"

    /// The selected signals, named `Class::signal-name` in GIR names, with resolved C types.
    let select (repo: Repository) (selection: string list) : Result<SignalDecl list, string> =
        let resolveSignal (entry: string) : Result<SignalDecl, string> =
            match entry.Split([| "::" |], System.StringSplitOptions.None) with
            | [| className; signalName |] ->
                let found =
                    repo.Namespaces |> Map.toList |> List.tryPick (fun (nsName, ns) ->
                        ns.Signals |> List.tryFind (fun s -> s.Class = className && s.Name = signalName)
                        |> Option.map (fun s -> nsName, s))
                match found with
                | None -> Error $"signal '{entry}' is not declared in the listed introspection files"
                | Some (nsName, s) ->
                    let parameters =
                        s.Parameters |> List.map (fun p ->
                            resolveType repo nsName p.Type
                            |> Result.map (fun c -> p.Name, c, p.Nullable)
                            |> Result.mapError (fun e -> $"signal '{entry}': parameter '{p.Name}': {e}"))
                    match parameters |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                    | Some e -> Error e
                    | None ->
                        resolveType repo nsName s.Return
                        |> Result.mapError (fun e -> $"signal '{entry}': result: {e}")
                        |> Result.map (fun ret ->
                            { Namespace = nsName; Class = s.Class; ClassCType = s.ClassCType; SymbolPrefix = s.SymbolPrefix
                              Name = s.Name
                              Parameters = parameters |> List.choose (function Ok p -> Some p | Error _ -> None)
                              ReturnCType = ret; Documentation = s.Documentation })
            | _ -> Error $"signal selection '{entry}' must be spelled Class::signal-name"
        let resolved = selection |> List.map resolveSignal
        match resolved |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some e -> Error e
        | None -> Ok (resolved |> List.choose (function Ok s -> Some s | Error _ -> None))
