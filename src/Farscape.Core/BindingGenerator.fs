namespace Farscape.Core

open System.IO
open Types
open PilotTypes


module BindingGenerator =

    /// A requested ABI layout probe cannot be replaced by declared layouts.
    let requireLayoutProbe = function
        | Ok layouts -> layouts
        | Error error -> failwithf "ABI-critical struct layout extraction failed: %s" error

    /// The declared shape of a record: its kind, each field's name, type, extent and bit
    /// width, and the attributes clang attached with their serialized arguments. Two headers
    /// declare the same storage only when these agree; pack widths, which clang does not
    /// serialize, are settled by measuring each declaring unit (`agreeMeasurements`).
    let private declaredShape (s: CppParser.StructDecl) =
        let attributes (attrs: CppParser.AttributeData list) =
            attrs |> List.map (fun a -> a.Kind + sprintf "%A" a.Args + defaultArg a.StringArg "") |> String.concat ","
        let field (f: CppParser.FieldDecl) =
            let extent = if f.IsArray then sprintf "[%A]" f.ArraySize else ""
            let width = if f.IsBitfield then sprintf ":%A" f.BitWidth else ""
            f.Name + ":" + f.Type + extent + width + attributes f.Attributes
        let kind = if s.IsUnion then "union" else "struct"
        String.concat " " [ kind; s.Name; "{"; s.Fields |> List.map field |> String.concat "; "; "}"; attributes s.Attributes ]

    /// The records a record's fields hold by value, resolved through the same typedef
    /// chains the generator uses, so the agreement check sees the nesting the descriptor emits.
    let private nestedRecords (ctx: FidelityCodeGenerator.GenerationContext) (s: CppParser.StructDecl) =
        s.Fields |> List.choose (fun field ->
            match CppParser.classifyFieldDeclarator field.Type with
            | CppParser.PlainFieldType element | CppParser.TrailingArrayType (element, _) ->
                match FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums element with
                | FidelityCodeGenerator.Unresolved name when ctx.Structs.ContainsKey name -> Some name
                | FidelityCodeGenerator.Unresolved _ ->
                    // A typedef of a defined record nests that record (#172), as the descriptor does.
                    match FidelityCodeGenerator.byValueRecordOf ctx element with
                    | Some (record, ("struct" | "union")) -> Some record
                    | _ -> None
                | _ -> None
            | CppParser.UnsupportedArrayType _ -> None)
        |> List.distinct

    /// A measured layout is a fact about one translation unit. Each ABI-critical entry names
    /// a record (its tag, or what its typedef stands for); the record is measured in the unit
    /// of every pilot header whose own declaration list defines it and, for a `typedef` entry,
    /// declares that typedef or names the record by it; a typedef entry's complete chain must
    /// agree in every header that declares it). The merged declaration the generator
    /// emits for that record, and for every record nested in it by value, must have the shape
    /// that header declares, and every typedef their field spellings depend on must be declared
    /// there with the merged spelling; otherwise the pilot is refused by name and header. A record
    /// several headers declare is measured in each of their units, and `agreeMeasurements`
    /// requires one effective layout. An entry no pilot header declares is not measured, so
    /// the existing declaration refusals report it instead of a layout some unrelated unit
    /// reaches under the same name.
    let measurementUnits (ctx: FidelityCodeGenerator.GenerationContext) (merged: CppParser.Declaration list)
                         (headers: (string * CppParser.Declaration list) list) (requested: string list)
        : Result<(string * string list) list, string> =
        let bareName (name: string) = name.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")
        let definitions (decls: CppParser.Declaration list) =
            decls |> List.choose (function CppParser.Declaration.Struct s when s.Name <> "" && not s.Fields.IsEmpty -> Some (s.Name, s) | _ -> None) |> Map.ofList
        // First declaration wins, as the merge itself keeps the first typedef of a name.
        let typedefs (decls: CppParser.Declaration list) =
            (Map.empty, decls) ||> List.fold (fun acc decl ->
                match decl with
                | CppParser.Declaration.Typedef t when not (acc.ContainsKey t.Name) -> acc.Add(t.Name, t.UnderlyingType)
                | _ -> acc)
        let mergedDefinitions = definitions merged
        let mergedTypedefs = typedefs merged
        let units = headers |> List.map (fun (header, decls) -> header, definitions decls, typedefs decls)
        // The record an entry names: its tag, the record carrying the typedef's own name, or
        // what the typedef stands for.
        let recordOf (name: string) =
            let bare = bareName name
            if name.StartsWith "typedef " then
                let resolved =
                    if not (mergedTypedefs.ContainsKey bare) then None
                    else
                        match FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums bare with
                        | FidelityCodeGenerator.Unresolved record when mergedDefinitions.ContainsKey record -> Some record
                        | _ -> None
                match resolved with
                | Some record -> Some record
                | None -> if mergedDefinitions.ContainsKey bare then Some bare else None
            elif mergedDefinitions.ContainsKey bare then Some bare
            else None
        // Every record the descriptor of `record` emits: itself and its by-value nesting.
        let rec closure (visited: Set<string>) (record: string) =
            if visited.Contains record then visited
            else
                match mergedDefinitions.TryFind record with
                | None -> visited
                | Some s -> nestedRecords ctx s |> List.fold closure (visited.Add record)
        // The first pilot header declaring the merged shape of a record, for refusal messages.
        let mergedOrigin (record: string) =
            let shape = mergedDefinitions.[record] |> declaredShape
            units |> List.tryPick (fun (header, defs, _) ->
                match defs.TryFind record with
                | Some d when declaredShape d = shape -> Some header
                | _ -> None)
            |> Option.defaultValue "<no pilot header>"
        // Every merged typedef a field spelling depends on, through typedef chains: a
        // field's representation is a fact of the unit whose typedefs resolve it. Tags
        // named after `struct`, `union` or `enum` live in C's tag space and are not typedefs.
        let identifiers (spelling: string) =
            let names, word =
                (([], ""), spelling + " ") ||> Seq.fold (fun (names, word) c ->
                    if System.Char.IsLetterOrDigit c || c = '_' then names, word + string c
                    elif word = "" then names, ""
                    else word :: names, "")
            ignore word
            // A name after `struct`, `union` or `enum` is a tag, never a typedef.
            let rec withoutTags acc = function
                | ("struct" | "union" | "enum") :: _ :: rest -> withoutTags acc rest
                | ("struct" | "union" | "enum") :: rest -> withoutTags acc rest
                | token :: rest -> withoutTags (token :: acc) rest
                | [] -> List.rev acc
            List.rev names |> withoutTags [] |> List.distinct
        let rec typedefClosure (visited: Set<string>) (spelling: string) =
            (visited, identifiers spelling) ||> List.fold (fun acc typedefName ->
                if acc.Contains typedefName then acc
                else
                    match mergedTypedefs.TryFind typedefName with
                    | Some underlying -> typedefClosure (acc.Add typedefName) underlying
                    | None -> acc)
        let fieldTypedefs (record: string) =
            match mergedDefinitions.TryFind record with
            | Some s -> (Set.empty, s.Fields) ||> List.fold (fun acc f -> typedefClosure acc f.Type)
            | None -> Set.empty
        // The first pilot header declaring the merged spelling of a typedef, for refusal messages.
        let typedefOrigin (typedefName: string) =
            match mergedTypedefs.TryFind typedefName with
            | Some merged ->
                units |> List.tryPick (fun (h, _, t) -> if t.TryFind typedefName = Some merged then Some h else None)
                |> Option.defaultValue "<no pilot header>"
            | None -> "<no pilot header>"
        let typedefAgreement (differs: string -> string -> string) (missing: string -> string) (tds: Map<string, string>) (typedefName: string) =
            match tds.TryFind typedefName, mergedTypedefs.TryFind typedefName with
            | Some own, Some merged when own = merged -> Ok ()
            | Some _, Some _ -> Error (differs typedefName (typedefOrigin typedefName))
            | _ -> Error (missing typedefName)
        let fieldTypedefAgreement (storage: string) (header: string) (tds: Map<string, string>) =
            typedefAgreement
                (fun hop origin -> sprintf "ABI-critical storage '%s' is measured in '%s', but its field typedef '%s' is declared differently by '%s' and that header; one declaration per pilot is required." storage header hop origin)
                (fun hop -> sprintf "ABI-critical storage '%s' is measured in '%s', but its field typedef '%s' is not declared by that header; a measured layout requires every field representation in one unit." storage header hop)
                tds
        // A requested typedef's complete chain must agree in every header declaring it, whether
        // or not that header also defines the record.
        let entryTypedefAgreement (bare: string) (header: string) (tds: Map<string, string>) =
            typedefAgreement
                (fun hop origin ->
                    if hop = bare then sprintf "ABI-critical typedef '%s' is declared differently by '%s' and '%s'; one declaration per pilot is required." bare origin header
                    else sprintf "ABI-critical typedef '%s' is declared by '%s', but its typedef '%s' is declared differently by '%s' and that header; one declaration per pilot is required." bare header hop origin)
                (fun hop -> sprintf "ABI-critical typedef '%s' is declared by '%s', but its typedef '%s' is not declared by that header; a measured layout requires the complete typedef chain in one unit." bare header hop)
                tds
        let agreement (storage: string) (header: string) (definitions: Map<string, CppParser.StructDecl>) (record: string) =
            match definitions.TryFind record with
            | Some own when declaredShape own = declaredShape mergedDefinitions.[record] -> Ok ()
            | Some _ when record = storage ->
                Error (sprintf "ABI-critical storage '%s' is declared differently by '%s' and '%s'; one declaration per pilot is required." storage (mergedOrigin record) header)
            | Some _ ->
                Error (sprintf "ABI-critical storage '%s' is measured in '%s', but its nested record '%s' is declared differently by '%s' and that header; one declaration per pilot is required." storage header record (mergedOrigin record))
            | None ->
                Error (sprintf "ABI-critical storage '%s' is measured in '%s', but its nested record '%s' is not declared by that header; a measured layout requires every nested record in one unit." storage header record)
        let assignments =
            requested |> List.map (fun name ->
                let bare = bareName name
                let isTypedef = name.StartsWith "typedef "
                let entryChain = if isTypedef && mergedTypedefs.ContainsKey bare then typedefClosure Set.empty bare |> Set.toList else []
                let entryDisagreement =
                    units |> List.tryPick (fun (header, defs, tds) ->
                        ignore defs
                        if not (isTypedef && tds.ContainsKey bare) then None
                        else entryChain |> List.tryPick (fun hop -> match entryTypedefAgreement bare header tds hop with Error error -> Some error | Ok () -> None))
                match entryDisagreement, recordOf name with
                | Some error, _ -> Error error
                | None, None -> Ok []
                | None, Some record ->
                    let declaresTypedef (tds: Map<string, string>) = tds.ContainsKey bare
                    let candidates = units |> List.filter (fun (_, defs, tds) -> defs.ContainsKey record && (not isTypedef || declaresTypedef tds))
                    match candidates with
                    | [] when isTypedef ->
                        match units |> List.tryFind (fun (_, _, tds) -> tds.ContainsKey bare) with
                        | Some (header, _, _) ->
                            Error (sprintf "ABI-critical typedef '%s' is declared by '%s', but its record '%s' is not declared by that header; a measured layout requires both in one unit." bare header record)
                        | None -> Ok []
                    | [] -> Ok []
                    | candidates ->
                        let records = closure Set.empty record |> Set.toList
                        let dependencies = (Set.empty, records) ||> List.fold (fun acc r -> Set.union acc (fieldTypedefs r)) |> Set.toList
                        candidates |> List.map (fun (header, defs, tds) ->
                            (Ok (), records) ||> List.fold (fun state nested -> state |> Result.bind (fun () -> agreement record header defs nested))
                            |> fun state -> (state, dependencies) ||> List.fold (fun state typedefName -> state |> Result.bind (fun () -> fieldTypedefAgreement record header tds typedefName))
                            |> Result.map (fun () -> header, name))
                        |> List.fold (fun state item -> state |> Result.bind (fun acc -> item |> Result.map (fun pair -> pair :: acc))) (Ok [])
                        |> Result.map List.rev)
        match assignments |> List.tryPick (function Error error -> Some error | _ -> None) with
        | Some error -> Error error
        | None ->
            let assigned = assignments |> List.collect (function Ok pairs -> pairs | _ -> [])
            units
            |> List.choose (fun (header, _, _) ->
                match assigned |> List.filter (fun (declaring, _) -> declaring = header) |> List.map snd with
                | [] -> None
                | names -> Some (header, names))
            |> Ok

    /// One effective layout per storage: a record measured in several declaring units must
    /// measure identically in each, or the pilot is refused by name and headers.
    let agreeMeasurements (measured: (string * Map<string, CppParser.StructLayoutInfo>) list)
        : Result<Map<string, CppParser.StructLayoutInfo>, string> =
        (Ok Map.empty, measured) ||> List.fold (fun state (header, layouts) ->
            (state, Map.toList layouts) ||> List.fold (fun state (name, layout) ->
                state |> Result.bind (fun (origins: Map<string, string * CppParser.StructLayoutInfo>) ->
                    match origins.TryFind name with
                    | Some (first, existing) when existing <> layout ->
                        Error (sprintf "ABI-critical storage '%s' measures differently in '%s' (%d bytes, alignment %d) and '%s' (%d bytes, alignment %d); one effective layout per pilot is required."
                                   name first (existing.SizeBits / 8) (existing.AlignmentBits / 8) header (layout.SizeBits / 8) (layout.AlignmentBits / 8))
                    | Some _ -> Ok origins
                    | None -> Ok (origins.Add(name, (header, layout))))))
        |> Result.map (Map.map (fun _ (_, layout) -> layout))

    /// One unit's facts: its own declarations' typedefs, enums and opaque handles, plus the
    /// typedefs and enums (measured in this unit) its own parse reaches through includes.
    /// Opaque handles stay those its own declarations establish.
    let unitFactsWith (included: Map<string, CppParser.Declaration list>) (header: string) (own: CppParser.Declaration list)
        : FidelityCodeGenerator.UnitFacts =
        let reached = included |> Map.tryFind header |> Option.defaultValue []
        let facts = FidelityCodeGenerator.unitFactsOf (own @ reached)
        { facts with OpaqueHandles = FidelityCodeGenerator.detectOpaqueHandles own }

    /// No by-value enum without a clang measurement in the unit that uses it. Every type a
    /// header's own declarations use by value (function parameters and returns, record fields,
    /// typedef targets) that is, or through the unit's typedefs stands for, an enum must name
    /// an enum this unit's parse measured, its own or reached through an include. Otherwise
    /// the pilot is refused before any binding is written, naming the enum, the using
    /// declaration and the unit; a width-unknown descriptor is never emitted for it.
    let requireMeasuredEnumUses (included: Map<string, CppParser.Declaration list>)
                                (headers: (string * CppParser.Declaration list) list) : Result<unit, string> =
        let refusals =
            headers |> List.collect (fun (header, decls) ->
                let facts = unitFactsWith included header decls
                let uses =
                    decls |> List.collect (function
                        | CppParser.Declaration.Function f -> (f.ReturnType :: (f.Parameters |> List.map snd)) |> List.map (fun t -> $"function '{f.Name}'", t)
                        | CppParser.Declaration.Struct s -> s.Fields |> List.map (fun field -> $"record '{s.Name}' field '{field.Name}'", field.Type)
                        | CppParser.Declaration.Typedef t -> [ $"typedef '{t.Name}'", t.UnderlyingType ]
                        | _ -> [])
                uses |> List.choose (fun (user, spelling) ->
                    if spelling.Contains('*') || spelling.Contains('(') || spelling.Contains('&') then None
                    else
                        let element = match CppParser.classifyFieldDeclarator spelling with
                                      | CppParser.TrailingArrayType (e, _) -> e
                                      | _ -> spelling
                        let named = TypeMapper.cleanTypeName element
                        let final = facts.Typedefs |> Map.tryFind named |> Option.defaultValue element
                        // An enum spelling, `enum X` under any cv-qualifiers.
                        let isEnum (text: string) = text.Replace("const ", "").Replace("volatile ", "").Trim().StartsWith "enum "
                        let enumName =
                            if facts.Enums.ContainsKey named then Some named
                            elif isEnum element then Some (TypeMapper.cleanTypeName element)
                            elif isEnum final && not (final.Contains '*' || final.Contains '(') then Some (TypeMapper.cleanTypeName final)
                            else None
                        match enumName with
                        | None -> None
                        | Some name ->
                            match facts.Enums.TryFind name |> Option.map _.Representation with
                            | Some (CppParser.ClangMeasured _) -> None
                            | _ ->
                                Some $"{Diagnostics.EnumUnmeasured}: enum '{name}' is used by value by {user} in '{header}', but that unit's clang parse has no measurement of it; its width cannot be inferred."))
        match refusals |> List.distinct with
        | [] -> Ok ()
        | refusals -> Error (String.concat " " refusals + " No bindings were written.")

    /// No anonymous record without a clang measurement in the unit that declares it (#159).
    /// Every record a header's own parse named from its parent and field (glibc's
    /// `__atomic_wide_counter.__value32`) must carry the layout clang measured in that unit;
    /// otherwise the pilot is refused before any binding is written, naming the record, its
    /// parent, field and location, and the unit. Its layout is never computed from its fields.
    let requireMeasuredAnonymousRecords (headers: (string * CppParser.Declaration list) list) : Result<unit, string> =
        let refusals =
            headers |> List.collect (fun (header, decls) ->
                decls |> List.choose (function
                    | CppParser.Declaration.Struct ({ Anonymous = Some origin; Measured = None } as s) ->
                        Some $"{Diagnostics.AnonymousRecordUnmeasured}: anonymous record '{s.Name}' (field '{origin.Field}' of '{origin.Parent}', declared at {origin.Location}) has no clang-measured layout in '{header}'; its layout is never computed from its declared fields."
                    | _ -> None))
        match refusals |> List.distinct with
        | [] -> Ok ()
        | refusals -> Error (String.concat " " refusals + " No bindings were written.")

    /// No descriptor carries an unknown width (#162). Every type the descriptors of an emitted
    /// function claim (`FidelityCodeGenerator.descriptorTypeSites`: parameters, result, and the
    /// arguments and result of each callback entry), resolved in the function's declaring unit,
    /// has a representation, is a by-value enum (measured, or refused with FS9103), or names a
    /// record the pilot defines and declares as a value struct. A type that resolves through no
    /// typedef, record or profile entry (FS9115), or a record without a declared value type,
    /// whose record type and descriptor the binding would cite but no mode emits (FS9119), is
    /// refused before any binding is written, naming the type, its site, the function and its
    /// unit.
    let requireRepresentedDescriptorTypes (ctx: FidelityCodeGenerator.GenerationContext)
                                          (headers: (string * CppParser.Declaration list) list)
                                          (functions: CppParser.FunctionDecl list) : Result<unit, string> =
        let unitOf (f: CppParser.FunctionDecl) =
            let symbol = ctx.Bindings |> Map.tryFind f.Name |> Option.map _.Symbol |> Option.defaultValue f.Name
            headers |> List.tryPick (fun (header, decls) ->
                let declares = decls |> List.exists (function
                    | CppParser.Declaration.Function g -> g.Name = symbol && g.ReturnType = f.ReturnType && g.Parameters = f.Parameters
                    | _ -> false)
                if declares then Some header else None)
            |> Option.defaultValue "<no pilot header>"
        let refusals =
            functions |> List.collect (fun f ->
                let unit = FidelityCodeGenerator.inFunctionUnit ctx f
                FidelityCodeGenerator.descriptorTypeSites ctx f
                |> List.choose (fun (site, cType) ->
                    match FidelityCodeGenerator.descriptorClaimOf unit cType with
                    | FidelityCodeGenerator.ResolvesNowhere ->
                        Some $"{Diagnostics.ByValueTypeUnresolved}: by-value type '{cType}' of the {site} of function '{f.Name}' in '{unitOf f}' resolves through no typedef, record or profile entry of that unit; a descriptor cannot carry an unknown width."
                    // A descriptor citing a record names X.Descriptor, and the binding names the
                    // record's type: both exist only for a declared value struct (its record
                    // type and layout module are emitted in every mode).
                    | FidelityCodeGenerator.RecordByValue (record, ("struct" | "union" as kind)) when not (ctx.ValueStructs.Contains record) ->
                        Some $"{Diagnostics.RecordWithoutValueType}: {kind} '{record}' is the type of the {site} of function '{f.Name}' in '{unitOf f}', but the pilot declares no value type for it, so neither its record type nor {record}.Descriptor would be emitted for the binding that cites them; declare it in [options] value_structs."
                    | _ -> None))
        match refusals |> List.distinct with
        | [] -> Ok ()
        | refusals -> Error (String.concat " " refusals + " No bindings were written.")

    /// The typedef facts of a unit's canonicalized result aliases (#171): `__size_t` stands for
    /// clang's canonical type of the results that spell it (`unsigned long`). An alias clang
    /// gave no canonical type has none, and a use of it is refused (`requireCanonicalAliases`).
    let aliasTypedefs (aliases: CppParser.ClangTypeAlias list) : CppParser.Declaration list =
        aliases |> List.choose (fun alias ->
            match alias.Canonical with
            | Ok (canonical, _) ->
                Some (CppParser.Declaration.Typedef { Name = alias.Name; UnderlyingType = canonical; Documentation = Some "clang canonical type of a built-in alias" })
            | Error _ -> None)

    /// Every clang built-in alias an emitted function's result spells resolves through clang's
    /// canonical type, and clang's measurement of that result (size and signedness) agrees with
    /// the profile row of the canonical type (#171). An alias clang gave no canonical type is
    /// refused before any binding is written (FS9117), naming the alias, the function, its unit
    /// and clang's reason; a measurement contradicting the row is refused (FS9118). A
    /// canonical type with no profile row is left to the descriptor check (FS9115).
    let requireCanonicalAliasesAgainst (rowOf: string -> TypeMapper.AbiRepr option) (model: PlatformABI)
                                        (aliases: Map<string, CppParser.ClangTypeAlias list>)
                                        (headers: (string * CppParser.Declaration list) list)
                                        (functions: CppParser.FunctionDecl list) : Result<unit, string> =
        let describe (signed: bool) (bits: int) = (if signed then "signed " else "unsigned ") + string bits + "-bit"
        let declaresIn (f: CppParser.FunctionDecl) (decls: CppParser.Declaration list) =
            decls |> List.exists (function
                | CppParser.Declaration.Function g -> g.Name = f.Name && g.ReturnType = f.ReturnType && g.Parameters = f.Parameters
                | _ -> false)
        let check (f: CppParser.FunctionDecl) (header: string) (alias: CppParser.ClangTypeAlias) =
            match alias.Canonical with
            | Error (CppParser.ProbeRefused reason) ->
                Some $"{Diagnostics.AliasUncanonicalized}: type '{alias.Name}' of the result of function '{f.Name}' in '{header}' is a clang built-in alias that no declaration of that unit names, and clang gives no canonical type for it: {reason}."
            | Error (CppParser.IncompleteParameter (parameter, spelled, reason)) ->
                Some $"{Diagnostics.AliasIncompleteParameter}: type '{alias.Name}' of the result of function '{f.Name}' in '{header}' is a clang built-in alias that no declaration of that unit names, and its canonical type cannot be read: clang cannot type a call of '{f.Name}', whose parameter '{parameter}' takes incomplete type '{spelled}' by value ({reason})."
            | Ok (canonical, measured) ->
                let conflict rowText measuredText =
                    Some $"{Diagnostics.AliasMeasurementConflict}: clang measures the result of function '{f.Name}' in '{header}' ('{alias.Name}', canonically '{canonical}') as {measuredText}, but the {model} profile row for '{canonical}' is {rowText}; the profile and the target must agree."
                match rowOf canonical with
                | Some row when row.Family = TypeMapper.Signed || row.Family = TypeMapper.Unsigned ->
                    let rowSigned = (row.Family = TypeMapper.Signed)
                    if row.Bits <> measured.Bits || rowSigned <> measured.IsSigned
                    then conflict (describe rowSigned row.Bits) (describe measured.IsSigned measured.Bits)
                    else None
                | Some row when row.Family = TypeMapper.Float ->
                    // clang measures storage: an 80-bit long double occupies 16 bytes.
                    let storageBits = if row.Bits = 80 then 128 else row.Bits
                    if storageBits <> measured.Bits then conflict (string row.Bits + "-bit") (string measured.Bits + "-bit storage") else None
                | _ -> None
        let refusals =
            functions |> List.collect (fun f ->
                let spelled = f.ReturnType.Replace("const ", "").Replace("volatile ", "").Trim()
                headers
                |> List.filter (fun (_, decls) -> declaresIn f decls)
                |> List.truncate 1
                |> List.choose (fun (header, _) ->
                    aliases |> Map.tryFind header |> Option.defaultValue []
                    |> List.tryFind (fun a -> a.Name = spelled)
                    |> Option.bind (check f header)))
        match refusals |> List.distinct with
        | [] -> Ok ()
        | refusals -> Error (String.concat " " refusals + " No bindings were written.")

    /// `requireCanonicalAliasesAgainst` with the profile's own scalar rows under `model`.
    let requireCanonicalAliases (model: PlatformABI) aliases headers functions =
        requireCanonicalAliasesAgainst (TypeMapper.tryScalar model) model aliases headers functions

    /// The typedef map each merged declaration's types resolve through, keyed by kind
    /// ("struct", "function", "typedef") and name: that of the first pilot header whose own
    /// declaration list declares it as merged (a record with the merged shape, a function with
    /// the merged signature, a typedef with the merged spelling), and that unit's own typedefs
    /// only: a name the declaring unit does not declare is not borrowed from another unit's
    /// typedefs (the merged map keeps the first typedef of a name across all units). A binding alias
    /// (`symbol` renamed to `name`) resolves in the unit of the symbol it renames. A declaration
    /// no header declares as merged has no entry and resolves through the merged map.
    let unitTypedefMaps (included: Map<string, CppParser.Declaration list>)
                        (merged: CppParser.Declaration list) (headers: (string * CppParser.Declaration list) list)
                        (aliases: (string * string) list)
        : Map<string * string, FidelityCodeGenerator.UnitFacts> =
        let identity = function
            | CppParser.Declaration.Struct s when s.Name <> "" && not s.Fields.IsEmpty -> Some (("struct", s.Name), declaredShape s)
            | CppParser.Declaration.Function f -> Some (("function", f.Name), sprintf "%s(%A)" f.ReturnType f.Parameters)
            | CppParser.Declaration.Typedef t -> Some (("typedef", t.Name), t.UnderlyingType)
            | _ -> None
        let declared (decls: CppParser.Declaration list) =
            (Map.empty, decls) ||> List.fold (fun acc decl ->
                match identity decl with
                | Some (key, shape) when not (Map.containsKey key acc) -> Map.add key shape acc
                | _ -> acc)
        // A unit's facts are its own only: no other unit's declaration is borrowed.
        let units =
            headers |> List.map (fun (header, decls) -> declared decls, lazy (unitFactsWith included header decls))
        let maps =
            declared merged
            |> Map.toList
            |> List.choose (fun (key, shape) ->
                units |> List.tryPick (fun (own, typedefs) ->
                    match own.TryFind key with
                    | Some ownShape when ownShape = shape -> Some (key, typedefs.Force())
                    | _ -> None))
            |> Map.ofList
        (maps, aliases) ||> List.fold (fun acc (name, symbol) ->
            match maps.TryFind ("function", symbol) with
            | Some facts when name <> symbol -> Map.add ("function", name) facts acc
            | _ -> acc)

    /// Every unit declaring each C type name (typedef, defined record, named enum), with that
    /// unit's own facts, for names a non-C source (GIR) supplies. Empty for one unit.
    let typeNameUnits (included: Map<string, CppParser.Declaration list>) (headers: (string * CppParser.Declaration list) list)
        : Map<string, (string * FidelityCodeGenerator.UnitFacts) list> =
        if List.length headers < 2 then Map.empty
        else
        headers
        |> List.collect (fun (header, decls) ->
            let typedefs = lazy (unitFactsWith included header decls)
            decls |> List.choose (function
                | CppParser.Declaration.Typedef t -> Some t.Name
                | CppParser.Declaration.Struct s when s.Name <> "" -> Some s.Name
                | CppParser.Declaration.Enum e when e.Name <> "" -> Some e.Name
                | _ -> None)
            |> List.distinct
            |> List.map (fun name -> name, (header, typedefs)))
        |> List.groupBy fst
        |> List.map (fun (name, entries) -> name, entries |> List.map (fun (_, (header, typedefs)) -> header, typedefs.Force()))
        |> Map.ofList

    /// Every pilot unit's profile scalar names must agree with the profile: each typedef a
    /// header's own declaration list declares under a profile row name resolves through that
    /// unit's own typedef map and is compared with the row.
    /// This holds for every unit, whichever declarations a namespace later selects.
    let agreeUnitProfileTypedefs (model: PlatformABI) (included: Map<string, CppParser.Declaration list>)
                                 (headers: (string * CppParser.Declaration list) list) : Result<unit, string> =
        let conflicts =
            headers |> List.collect (fun (header, decls) ->
                let own = decls |> List.choose (function CppParser.Declaration.Typedef t -> Some t.Name | _ -> None)
                if own.IsEmpty then [] else
                let facts = unitFactsWith included header decls
                FidelityCodeGenerator.unitProfileConflicts model header facts.Typedefs facts.Enums own)
        match conflicts with
        | [] -> Ok ()
        | conflicts -> Error (String.concat " " conflicts)

    /// One definition per enum name per pilot. Each header's unit declares its enums with the
    /// representation clang measured in that unit; two units declaring one name must agree on
    /// that representation, the fixed underlying type and every enumerator (name and value), or
    /// the pilot is refused naming each distinct definition and every header giving it. Checked
    /// while the declaration lists are still paired with their headers, before the merge keeps
    /// the first; past this check the merged enum facts agree with every declaring unit.
    let agreeEnums (included: Map<string, CppParser.Declaration list>) (headers: (string * CppParser.Declaration list) list) : Result<unit, string> =
        let describe (e: CppParser.EnumDecl) =
            let repr =
                match e.Representation with
                | CppParser.ClangMeasured m -> $"""clang-measured {(if m.IsSigned then "signed" else "unsigned")} {m.Bits}-bit"""
                | CppParser.ClangUnmeasured -> "unmeasured"
                | CppParser.ProtocolWire (m, _) -> $"""protocol wire {(if m.IsSigned then "signed" else "unsigned")} {m.Bits}-bit"""
                | CppParser.ProtocolUndeclared _ -> "protocol, no wire type"
            let fixedType = match e.UnderlyingType with Some t -> $"; underlying type {t}" | None -> ""
            let values = e.Values |> List.map (fun v -> $"{v.Name}={v.Value}") |> String.concat ", "
            $"{repr}{fixedType}; enumerators {values}"
        let identity (e: CppParser.EnumDecl) = e.Representation, e.UnderlyingType, e.Values |> List.map (fun v -> v.Name, v.Value)
        let perHeader =
            headers |> List.map (fun (header, decls) -> header, (unitFactsWith included header decls).Enums)
        let names = perHeader |> List.collect (fun (_, enums) -> enums |> Map.toList |> List.map fst) |> List.distinct |> List.sort
        let divergent =
            names |> List.choose (fun name ->
                let givers = perHeader |> List.choose (fun (header, enums) -> enums.TryFind name |> Option.map (fun e -> identity e, describe e, header))
                let keys = givers |> List.map (fun (k, _, _) -> k) |> List.distinct
                if keys.Length < 2 then None
                else
                    let described =
                        keys |> List.map (fun key ->
                            let same = givers |> List.filter (fun (k, _, _) -> k = key)
                            let _, text, _ = List.head same
                            let hs = same |> List.map (fun (_, _, h) -> $"'{h}'") |> String.concat ", "
                            $"{hs} ({text})")
                        |> String.concat "; "
                    Some $"{Diagnostics.EnumDivergent}: enum '{name}' is declared differently by the pilot's headers: {described}. One declaration per pilot is required.")
        if divergent.IsEmpty then Ok ()
        else Error (String.concat " " divergent + " No bindings were written.")

    /// One definition per record name per pilot (#161). Each header's unit declares its records
    /// as its own clang parse sees them; two units defining one struct or union name must agree
    /// on its kind, every field (name, type, extent, bit width and attributes, in order) and the
    /// record's attributes, whether or not the pilot measures the record, and on every layout
    /// clang measured for it in those units. Otherwise the pilot is refused naming the record,
    /// each distinct definition with every header giving it, and the first field at which a
    /// definition differs from the first. A forward declaration (no fields) is not a definition.
    /// Checked while declaration lists are still paired with their headers, before the merge
    /// keeps the first definition; identical definitions merge.
    let agreeRecords (headers: (string * CppParser.Declaration list) list) : Result<unit, string> =
        let definitionsOf (decls: CppParser.Declaration list) =
            (Map.empty, decls) ||> List.fold (fun acc decl ->
                match decl with
                | CppParser.Declaration.Struct s when s.Name <> "" && not s.Fields.IsEmpty && not (Map.containsKey s.Name acc) -> Map.add s.Name s acc
                | _ -> acc)
        let perHeader = headers |> List.map (fun (header, decls) -> header, definitionsOf decls)
        let names = perHeader |> List.collect (fun (_, defs) -> defs |> Map.toList |> List.map fst) |> List.distinct |> List.sort
        let attributes (attrs: CppParser.AttributeData list) =
            attrs |> List.map (fun a -> a.Kind + (if a.Args.IsEmpty then "" else sprintf "(%s)" (a.Args |> List.map string |> String.concat ", ")) + (match a.StringArg with Some t -> $" \"{t}\"" | None -> ""))
        let fieldText (f: CppParser.FieldDecl) =
            let qualifiers = (if f.IsConst then "const " else "") + (if f.IsVolatile then "volatile " else "")
            let extent = if f.IsArray then (match f.ArraySize with Some n -> $"[{n}]" | None -> "[]") else ""
            let width = match f.BitWidth with Some w when f.IsBitfield -> $" : {w}" | _ when f.IsBitfield -> " : ?" | _ -> ""
            let attrs = match attributes f.Attributes with [] -> "" | list -> " [" + String.concat ", " list + "]"
            $"{qualifiers}{f.Type} {f.Name}{extent}{width}{attrs}"
        let kindOf (s: CppParser.StructDecl) = if s.IsUnion then "union" else "struct"
        let describe (s: CppParser.StructDecl) =
            let attrs = match attributes s.Attributes with [] -> "" | list -> " [" + String.concat ", " list + "]"
            $"""{kindOf s} {s.Name} {{ {s.Fields |> List.map fieldText |> String.concat "; "} }}{attrs}"""
        // Where `other` first departs from `first`: a field, the record's kind or its attributes.
        let firstDifference (firstHeader: string, first: CppParser.StructDecl) (otherHeader: string, other: CppParser.StructDecl) =
            let rec fields index (a: CppParser.FieldDecl list) (b: CppParser.FieldDecl list) =
                match a, b with
                | x :: xs, y :: ys when fieldText x = fieldText y -> fields (index + 1) xs ys
                | x :: _, y :: _ -> Some $"field {index} '{x.Name}': '{fieldText x}' in '{firstHeader}', '{fieldText y}' in '{otherHeader}'"
                | x :: _, [] -> Some $"field {index} '{x.Name}': '{fieldText x}' in '{firstHeader}', absent in '{otherHeader}'"
                | [], y :: _ -> Some $"field {index} '{y.Name}': absent in '{firstHeader}', '{fieldText y}' in '{otherHeader}'"
                | [], [] -> None
            match fields 1 first.Fields other.Fields with
            | Some field -> field
            | None when first.IsUnion <> other.IsUnion -> $"its kind: {kindOf first} in '{firstHeader}', {kindOf other} in '{otherHeader}'"
            | None ->
                let a = attributes first.Attributes |> String.concat ", "
                let b = attributes other.Attributes |> String.concat ", "
                $"its attributes: [{a}] in '{firstHeader}', [{b}] in '{otherHeader}'"
        // Where two clang measurements of one declared record first depart.
        let measuredDifference (firstHeader: string, first: CppParser.StructDecl, a: CppParser.StructLayoutInfo) (otherHeader: string, b: CppParser.StructLayoutInfo) =
            let offsets = List.zip3 first.Fields (a.FieldOffsetsBits |> List.truncate first.Fields.Length) (b.FieldOffsetsBits |> List.truncate first.Fields.Length)
            match offsets |> List.tryFind (fun (_, x, y) -> x <> y) with
            | Some (f, x, y) when a.FieldOffsetsBits.Length = b.FieldOffsetsBits.Length ->
                $"field '{f.Name}': clang-measured offset {x / 8} in '{firstHeader}', {y / 8} in '{otherHeader}'"
            | _ ->
                $"its clang-measured layout: {a.SizeBits / 8} bytes at alignment {a.AlignmentBits / 8} in '{firstHeader}', {b.SizeBits / 8} bytes at alignment {b.AlignmentBits / 8} in '{otherHeader}'"
        let headersOf (group: (string * CppParser.StructDecl) list) = group |> List.map (fun (h, _) -> $"'{h}'") |> String.concat ", "
        let divergent =
            names |> List.choose (fun name ->
                let givers = perHeader |> List.choose (fun (header, defs) -> defs.TryFind name |> Option.map (fun s -> header, s))
                let identity (s: CppParser.StructDecl) = kindOf s, s.Fields |> List.map fieldText, attributes s.Attributes
                let shapes = givers |> List.groupBy (fun (_, s) -> identity s) |> List.map snd
                match shapes with
                | [ group ] ->
                    let measured = group |> List.choose (fun (h, s) -> s.Measured |> Option.map (fun m -> h, s, m))
                    let layoutKey (m: CppParser.StructLayoutInfo) = m.SizeBits, m.AlignmentBits, m.FieldOffsetsBits
                    match measured |> List.groupBy (fun (_, _, m) -> layoutKey m) |> List.map snd with
                    | [] | [ _ ] -> None
                    | (firstGroup :: rest) ->
                        let (h1, s1, m1) = List.head firstGroup
                        let differences = rest |> List.map (fun g -> let (h, _, m) = List.head g in measuredDifference (h1, s1, m1) (h, m)) |> String.concat "; "
                        let listed = (firstGroup :: rest) |> List.map (fun g -> g |> List.map (fun (h, _, _) -> $"'{h}'") |> String.concat ", ") |> String.concat "; "
                        Some $"{Diagnostics.RecordDivergent}: record '{name}' ({describe s1}) is measured differently by the pilot's headers: {listed}; first difference at {differences}. One definition per pilot is required."
                | [] -> None
                | firstGroup :: rest ->
                    let first = List.head firstGroup
                    let described = shapes |> List.map (fun group -> $"{headersOf group} ({describe (snd (List.head group))})") |> String.concat "; "
                    let differences = rest |> List.map (fun group -> firstDifference first (List.head group)) |> String.concat "; "
                    Some $"{Diagnostics.RecordDivergent}: record '{name}' is defined differently by the pilot's headers: {described}; first difference at {differences}. One definition per pilot is required.")
        if divergent.IsEmpty then Ok ()
        else Error (String.concat " " divergent + " No bindings were written.")

    /// The preprocessing tokens of a replacement list (C11 6.4, C++ [lex.pptoken]): identifiers,
    /// pp-numbers, character and string literals with their encoding prefix (`L`, `u`, `U`,
    /// `u8`) as part of the one literal token, and punctuators by longest match. The unit's
    /// language decides the forms only one language has: in C++ (`cpp`), a pp-number continues
    /// through a `'` digit separator followed by a digit or identifier character, a literal
    /// takes an attached ud-suffix, and `R"d(...)d"` raw strings (with any encoding prefix) are
    /// one token; in C, `'` always opens a character literal, a suffix is its own identifier
    /// and `R` is an identifier. Whitespace between tokens carries no token; each token keeps
    /// its exact spelling, so `0x10` and `16` stay distinct, `1 + 2` and `1+2` are one
    /// sequence, `L"x"` differs from `L "x"`, and in C++ `1'2'3` (one number) differs from
    /// `1 '2' 3` (number, character literal, number).
    let macroTokens (cpp: bool) (text: string) : string list =
        let punctuators =
            [ "%:%:"; "..."; "<<="; ">>="; "->*"; "<=>"; "->"; "++"; "--"; "<<"; ">>"; "<="; ">="; "=="; "!="; "&&"; "||"
              "*="; "/="; "%="; "+="; "-="; "&="; "^="; "|="; "##"; "::"; ".*"; "<:"; ":>"; "<%"; "%>"; "%:" ]
        let isIdent c = System.Char.IsLetterOrDigit c || c = '_'
        let isIdentStart c = System.Char.IsLetter c || c = '_'
        let rec identEnd j = if j < text.Length && isIdent text.[j] then identEnd (j + 1) else j
        // A quoted literal from its opening quote; returns the index after its closing quote.
        let quotedEnd (j: int) =
            let quote = text.[j]
            let rec scan k =
                if k >= text.Length then text.Length
                elif text.[k] = '\\' then scan (k + 2)
                elif text.[k] = quote then k + 1
                else scan (k + 1)
            min text.Length (scan (j + 1))
        // A raw string from its opening quote: "delim( ... )delim".
        let rawEnd (j: int) =
            let openParen = text.IndexOf('(', j + 1)
            if openParen < 0 then text.Length
            else
                let terminator = ")" + text.Substring(j + 1, openParen - j - 1) + "\""
                match text.IndexOf(terminator, openParen + 1, System.StringComparison.Ordinal) with
                | -1 -> text.Length
                | close -> close + terminator.Length
        let withSuffix j = if cpp && j < text.Length && isIdentStart text.[j] then identEnd j else j
        let rec scan (i: int) (acc: string list) =
            if i >= text.Length then List.rev acc
            else
                let c = text.[i]
                let take j = scan j (text.Substring(i, j - i) :: acc)
                if System.Char.IsWhiteSpace c then scan (i + 1) acc
                elif System.Char.IsDigit c || (c = '.' && i + 1 < text.Length && System.Char.IsDigit text.[i + 1]) then
                    // pp-number: digits, identifier characters, '.', a sign after e/E/p/P, and in
                    // C++ a ' digit separator followed by a digit or identifier character.
                    let rec number j =
                        if j >= text.Length then j
                        elif isIdent text.[j] || text.[j] = '.' then number (j + 1)
                        elif (text.[j] = '+' || text.[j] = '-') && "eEpP".Contains(text.[j - 1]) then number (j + 1)
                        elif cpp && text.[j] = '\'' && j + 1 < text.Length && isIdent text.[j + 1] then number (j + 2)
                        else j
                    take (number (i + 1))
                elif isIdentStart c then
                    let j = identEnd i
                    let word = text.Substring(i, j - i)
                    let quoteNext = j < text.Length && (text.[j] = '"' || text.[j] = '\'')
                    match word with
                    | "L" | "u" | "U" | "u8" when quoteNext -> take (withSuffix (quotedEnd j))
                    | "R" | "LR" | "uR" | "UR" | "u8R" when cpp && j < text.Length && text.[j] = '"' -> take (withSuffix (rawEnd j))
                    | _ -> take j
                elif c = '"' || c = '\'' then take (withSuffix (quotedEnd i))
                else
                    let token =
                        punctuators |> List.tryFind (fun p -> System.String.CompareOrdinal(text, i, p, 0, p.Length) = 0)
                        |> Option.defaultValue (string c)
                    scan (i + token.Length) (token :: acc)
        scan 0 []

    /// One macro definition per pilot. Each header's macros are collected from its whole
    /// translation unit (`clang -E -dM`), so the same name can reach the pilot from several
    /// units; `mergeDeclarations` keeps the first and discards which header supplied it. Two
    /// definitions agree when they have the same parameter structure (object-like, or the same
    /// parameter names in order) and the same preprocessing-token sequence; whitespace between
    /// tokens is insignificant and token spellings are never assumed equivalent. A name with
    /// disagreeing definitions is refused here, while the declaration lists are still paired
    /// with their headers, naming the macro, each distinct definition (as first spelled) and
    /// every header that gives it. Agreeing definitions merge silently.
    let agreeMacros (headers: (string * bool * CppParser.Declaration list) list) : Result<unit, string> =
        let definition cpp (m: CppParser.MacroDecl) =
            match m.Kind with
            | CppParser.FunctionLike (args, body) -> (Some args, macroTokens cpp body), "(" + String.concat ", " args + ") " + body
            | _ -> (None, macroTokens cpp m.RawValue), m.RawValue
        let perHeader =
            headers |> List.map (fun (header, cpp, decls) ->
                header,
                ((Map.empty, decls) ||> List.fold (fun acc decl ->
                    match decl with
                    | CppParser.Declaration.Macro m when not (acc.ContainsKey m.Name) -> acc.Add(m.Name, definition cpp m)
                    | _ -> acc)))
        let names = perHeader |> List.collect (fun (_, macros) -> macros |> Map.toList |> List.map fst) |> List.distinct |> List.sort
        let divergent =
            names |> List.choose (fun name ->
                let givers = perHeader |> List.choose (fun (header, macros) -> macros.TryFind name |> Option.map (fun (key, spelling) -> key, spelling, header))
                let keys = givers |> List.map (fun (key, _, _) -> key) |> List.distinct
                if keys.Length < 2 then None
                else
                    let described =
                        keys |> List.map (fun key ->
                            let same = givers |> List.filter (fun (k, _, _) -> k = key)
                            let _, spelling, _ = List.head same
                            let hs = same |> List.map (fun (_, _, h) -> $"'{h}'") |> String.concat ", "
                            $"'{spelling}' in {hs}")
                        |> String.concat "; "
                    Some $"{Diagnostics.MacroDivergent}: macro '{name}' is defined differently by the pilot's headers: {described}. One definition per pilot is required.")
        if divergent.IsEmpty then Ok ()
        else Error (String.concat " " divergent + " No bindings were written.")

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
    let private generateBindingsCore (options: GenerationOptions) : Result<GenerationResult, string> =
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

            logVerbose "Generating Fidelity Clef source..." options.Verbose
            let generatedCode = FidelityCodeGenerator.generate declarations options.Namespace options.LibraryName options.DataModel Map.empty

            let lastSegment = options.Namespace.Split('.') |> Array.last
            let outputFileName = $"{lastSegment}.clef"
            let outputPath = Path.Combine(options.OutputDirectory, outputFileName)
            let wrapperOutputs =
                if options.GenerateWrappers then
                    logVerbose "Generating idiomatic wrappers..." options.Verbose
                    let wrapperNamespace = $"{options.Namespace}.Api"
                    let wrapperCode =
                        WrapperCodeGenerator.generate declarations wrapperNamespace options.LibraryName options.Namespace WrapperTypes.NoErrors options.DataModel None
                    let wrapperPath = Path.Combine(options.OutputDirectory, $"{lastSegment}Api.clef")
                    [wrapperPath, wrapperCode]
                else []

            // Settle every source projection before replacing any generated file.
            Directory.CreateDirectory(options.OutputDirectory) |> ignore
            for path, code in (outputPath, generatedCode) :: wrapperOutputs do
                File.WriteAllText(path, code)
                logVerbose $"Binding module written to: {path}" options.Verbose

            Ok {
                OutputFiles = outputPath :: (wrapperOutputs |> List.map fst)
                DeclarationCount = declarations.Length
                Advisories = []
            }

    let generateBindings options : Result<GenerationResult, string> =
        try generateBindingsCore options
        with ex -> Error $"Binding generation failed: {ex.Message}"

    /// Locate a platform dependency when a pilot lives in a nested experimental tree.
    let rec private findProjectAbove directory fileName =
        let candidate = Path.Combine(directory, fileName)
        if File.Exists candidate then Some candidate
        else
            let parent = Directory.GetParent directory
            if isNull parent then None else findProjectAbove parent.FullName fileName

    /// Resolve actual dependencies from the pilot/output tree or the running generator.
    /// Never publish a guessed path that cannot be loaded on regeneration.
    let private resolveProjectDependency directory fileName siblingPath environmentVariable overridePath =
        if not (System.String.IsNullOrWhiteSpace overridePath) then
            let path = Path.GetFullPath overridePath
            if File.Exists path then path
            else invalidOp $"{environmentVariable} names a missing dependency: {path}"
        else
            match findProjectAbove directory fileName with
            | Some path -> path
            | None ->
                let rec findSibling current =
                    let candidate = Path.Combine(current, siblingPath)
                    if File.Exists candidate then Some candidate
                    else
                        let parent = Directory.GetParent current
                        if isNull parent then None else findSibling parent.FullName
                [directory; Path.GetDirectoryName(typeof<GenerationOptions>.Assembly.Location)]
                |> List.tryPick findSibling
                |> Option.defaultWith (fun () ->
                    invalidOp $"Cannot locate {fileName}. Set {environmentVariable} to an existing project before generating bindings.")

    let private projectDirectoryForOutput outputDir =
        let fullPath = Path.GetFullPath outputDir
        let parent = Directory.GetParent fullPath
        // Platform packages share the manifest directory above Bindings/<package>.
        // Standalone generation must keep its manifest with its own output.
        if not (isNull parent) && parent.Name = "Bindings" then parent.Parent.FullName
        else fullPath

    let private resolveProjectDependencies (project: PilotProject) directory =
        let descriptorOnly = project.Options |> Option.exists (fun opts -> opts.DescriptorOnlyDependencies)
        // Retain the selected inputs once. Descriptor-only limits the BARE vocabulary;
        // it does not revoke an explicitly selected platform's graph declarations.
        let platformOverride = System.Environment.GetEnvironmentVariable "FIDELITY_PLATFORM_PROJECT"
        let metadataOverride = System.Environment.GetEnvironmentVariable "BAREWIRE_METADATA_PROJECT"
        let platformPath =
            if descriptorOnly && System.String.IsNullOrWhiteSpace platformOverride then None
            else
                resolveProjectDependency directory "Fidelity.Platform.fidproj"
                    "Fidelity.Platform/Environments/Linux/x86_64/Fidelity.Platform.fidproj"
                    "FIDELITY_PLATFORM_PROJECT" platformOverride |> Some
        // The dependency is not a platform selection by itself. Copy only the
        // description explicitly supplied by the selected project's manifest.
        let platformDescription =
            if System.String.IsNullOrWhiteSpace platformOverride then None
            else
                let path = platformPath |> Option.get
                let document =
                    Fidelity.Data.TOML.Toml.parse (File.ReadAllText path)
                    |> function
                        | Ok document -> document
                        | Error reason -> invalidOp $"FIDELITY_PLATFORM_PROJECT has invalid TOML: {path}: {reason}"
                match Fidelity.Data.TOML.Toml.getValue "platform.description" document with
                | None -> None
                | Some (Fidelity.Data.TOML.TomlValue.String description) when not (System.String.IsNullOrWhiteSpace description) -> Some description
                | Some _ -> invalidOp $"FIDELITY_PLATFORM_PROJECT platform.description must be a nonempty string: {path}"
        let metadataProject = if descriptorOnly then "BAREWire.BindingMetadata.fidproj" else "BAREWire.fidproj"
        let metadataPath =
            resolveProjectDependency directory metadataProject
                (Path.Combine("BAREWire", "src", metadataProject)) "BAREWIRE_METADATA_PROJECT" metadataOverride
        platformPath, platformDescription, metadataPath

    /// Generate a canonical .fidproj file for the binding library.
    /// Fully automatic — derives everything from what Farscape already knows.
    let private generateFidproj
        (project: PilotProject)
        (nsPrefix: string)
        (outputDir: string)
        (dependencies: string option * string option * string)
        (allFiles: string list)
        (verbose: bool) : string option =
        let packageName = nsPrefix
        let platformPath, platformDescription, metadataPath = dependencies
        let fidprojDir = projectDirectoryForOutput outputDir
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
        platformDescription |> Option.iter (fun description ->
            let value = Fidelity.Data.TOML.Toml.serializeValue (Fidelity.Data.TOML.TomlValue.String description)
            sb.AppendLine($"description = {value}") |> ignore)
        sb.AppendLine("runtime_model = \"libc\"") |> ignore
        sb.AppendLine("os = \"linux\"") |> ignore
        sb.AppendLine("arch = \"x86_64\"") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[dependencies]") |> ignore
        match platformPath with
        | Some platformPath ->
            let platformDepPath = Path.GetRelativePath(fidprojDir, platformPath).Replace('\\', '/')
            sb.AppendLine($"\"Fidelity.Platform\" = {{ path = \"{platformDepPath}\" }}") |> ignore
        | None -> ()
        // The descriptors reference BAREWire's Hardware and Descriptors vocabularies
        let bareWireDepPath = Path.GetRelativePath(fidprojDir, metadataPath).Replace('\\', '/')
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
    let private generateFromProjectCore (aliasRows: string -> TypeMapper.AbiRepr option) (projectPath: string) (verbose: bool) (generateWrappers: bool) (dataModel: PlatformABI) : Result<GenerationResult, string> =
        match PilotSerializer.loadFromFile projectPath with
        | Error e -> Error $"Failed to load project: {e}"
        | Ok _ when dataModel <> PlatformABI.LP64 ->
            Error "Project generation currently declares a Linux x86_64 LP64 target. A different ABI requires an explicit target, header/sysroot and link contract; emitting it with the Linux manifest would be incorrect. No bindings were written."
        | Ok project when project.Options |> Option.exists (fun opts -> opts.NativePointerSurface) ->
            Error "experimental_native_pointer_surface exposes addresses in Clef source and violates the FFI boundary contract. Remove this option and declare CHandle, callback, reference and ownership projections in the pilot. No bindings were written."
        | Ok project when not project.Library.XmlProtocols.IsEmpty
                          && not (project.Options |> Option.exists (fun opts -> opts.TypedProtocol)) ->
            Error "XML protocol generation requires options.typed_protocol = true. The legacy protocol generator exposes pointer operations in Clef source. No bindings were written."
        | Ok project when generateWrappers && (project.ErrorConventions |> Option.exists (fun spec -> spec.Default = PilotTypes.Errno)) ->
            Error "errno wrappers require a declared native scalar accessor; an opaque errno handle cannot be dereferenced in Clef. No bindings were written."
        | Ok project when generateWrappers && (project.ErrorConventions |> Option.exists (fun spec -> match spec.Default with PilotTypes.NullWithReason _ -> true | _ -> false)) ->
            Error "null_with_reason wrappers require the declared reason return type and a native text extent/ownership projection. An assumed error-text handle is not an admitted contract. No bindings were written."
        | Ok project ->
            let projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            let outputDir =
                if Path.IsPathRooted(project.Output.Directory) then project.Output.Directory
                else Path.GetFullPath(Path.Combine(projectDir, project.Output.Directory))
            let dependencies = resolveProjectDependencies project (projectDirectoryForOutput outputDir)
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
                        CppParser.parseHeaderUnit {
                            HeaderFile = headerPath; IncludePaths = project.Library.IncludePaths
                            Defines = project.Library.Defines; IncludeRoot = includeRoot
                            MacroPrefixes = project.Library.MacroPrefixes; IncludeMacros = true
                            Verbose = verbose; CppMode = false }
                    else
                        CppParser.parseHeaderUnit {
                            HeaderFile = headerPath; IncludePaths = project.Library.IncludePaths
                            Defines = project.Library.Defines; IncludeRoot = includeRoot
                            MacroPrefixes = project.Library.MacroPrefixes; IncludeMacros = true
                            Verbose = verbose; CppMode = CppParser.detectCppMode headerPath })

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

                let headerDeclLists = headerResults |> List.choose (function Ok (d, _, _) -> Some d | _ -> None)
                // Clang built-in aliases each unit's function results spell (#171), read by clang.
                let resultAliases =
                    List.zip project.Library.Headers (headerResults |> List.choose (function Ok (_, _, a) -> Some a | _ -> None))
                    |> Map.ofList
                // Typedefs and enums each header's own parse reaches through includes: facts of
                // that unit for resolving its own declarations, never its declarations. An alias
                // clang canonicalized stands for its canonical type in that unit.
                let included =
                    List.zip project.Library.Headers (headerResults |> List.choose (function Ok (_, i, _) -> Some i | _ -> None))
                    |> List.map (fun (header, facts) -> header, facts @ aliasTypedefs (resultAliases |> Map.tryFind header |> Option.defaultValue []))
                    |> Map.ofList
                // Before the merge discards provenance: one macro definition per pilot.
                // Each header is tokenized in the language it was parsed in.
                let cHeaders = project.Options |> Option.exists (fun opts -> opts.NativePointerSurface || opts.CHeaderMode)
                let units = List.zip project.Library.Headers headerDeclLists
                agreeMacros (units |> List.map (fun (header, decls) -> header, (not cHeaders && CppParser.detectCppMode header), decls)) |> Result.defaultWith failwith
                agreeEnums included units |> Result.defaultWith failwith
                requireMeasuredEnumUses included units |> Result.defaultWith failwith
                agreeRecords units |> Result.defaultWith failwith
                requireMeasuredAnonymousRecords units |> Result.defaultWith failwith
                let xmlProtocols = xmlResults |> List.choose (function Ok p -> Some p | _ -> None)

                // GObject introspection data: the source of signal entry signatures (IntrospectionParser)
                let introspection =
                    if project.Library.Introspection.IsEmpty then None
                    else
                        match IntrospectionParser.load project.Library.Introspection with
                        | Ok repo -> Some repo
                        | Error e -> failwith e

                let typedProtocol = project.Options |> Option.exists (fun o -> o.TypedProtocol)
                let declarations = DeclarationAlgebra.mergeDeclarations headerDeclLists
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
                // Each unit's own typedefs, kept per declaration through measurement and generation.
                let unitTypedefMaps =
                    unitTypedefMaps included declarations (List.zip project.Library.Headers headerDeclLists)
                        (projections |> List.map (fun binding -> binding.Name, binding.Symbol))
                agreeUnitProfileTypedefs dataModel included (List.zip project.Library.Headers headerDeclLists) |> Result.defaultWith failwith
                if generateWrappers then
                    let callbacks = project.Callbacks |> Option.defaultWith (fun () -> PilotAnalyzer.discoverCallbacks declarations)
                    if not callbacks.ListenerStructs.IsEmpty then
                        failwith "Generic listener builders require declared native entry records and initialized fields. The legacy integer-address builder is not admitted; use typed protocol or signal projections. No bindings were written."
                let sourceCount = project.Library.Headers.Length + project.Library.XmlProtocols.Length
                logVerbose $"Merged {declarations.Length} declarations from {sourceCount} source(s)" verbose

                // Diagnostic: summarize C++ class extraction (verbose only)
                if verbose then
                    let classDecls = declarations |> List.choose (function CppParser.Declaration.Class c -> Some c | _ -> None)
                    logVerbose $"C++ classes in merged declarations: {classDecls.Length}" verbose
                    for c in classDecls do
                        let classification = CppClassAnalysis.classifyClass c
                        logVerbose $"  class: {c.Name} (fields={c.Fields.Length}, methods={c.Methods.Length}) -> {classification}" verbose

                // Build generation context once from the full declaration list
                let structLayouts =
                    let abiStructNames =
                        match project.Options with
                        | Some opts when not opts.AbiCriticalStructs.IsEmpty -> opts.AbiCriticalStructs
                        | _ -> []
                    if abiStructNames.IsEmpty then Map.empty
                    else
                        // Measure each storage in its declaring headers' units (measurementUnits):
                        // the first header's unit may reach a different record under the same name.
                        let probeCtx = FidelityCodeGenerator.buildGenerationContext declarations dataModel Map.empty
                        let units =
                            measurementUnits probeCtx declarations (List.zip project.Library.Headers headerDeclLists) abiStructNames
                            |> Result.defaultWith failwith
                        units
                        |> List.map (fun (headerFile, names) ->
                            let measured =
                                CppParser.extractStructLayouts headerFile project.Library.IncludePaths project.Library.Defines names verbose
                                |> requireLayoutProbe
                            logVerbose $"Extracted layouts for {measured.Count} ABI-critical struct(s) in {headerFile}" verbose
                            headerFile, measured)
                        |> agreeMeasurements
                        |> Result.defaultWith failwith

                let ctx =
                    let baseCtx = FidelityCodeGenerator.buildGenerationContext declarations dataModel structLayouts
                    match project.Options with
                    | Some opts ->
                        let nativeCarrier = opts.NativePointerSurface || opts.CHeaderMode
                        for requested in opts.AbiCriticalStructs do
                            let name = requested.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")
                            let expectedUnion =
                                if requested.StartsWith("struct ") then Some false
                                elif requested.StartsWith("union ") then Some true
                                else None
                            if requested.StartsWith("typedef ") && not (baseCtx.TypedefMap.ContainsKey name) then
                                failwithf "ABI-critical typedef '%s' is not declared." name
                            let declared = baseCtx.Structs.TryFind name
                            if expectedUnion |> Option.exists (fun expected -> declared |> Option.exists (fun record -> record.IsUnion <> expected)) then
                                failwithf "ABI-critical storage '%s' does not match the requested record kind." name
                            if declared.IsSome then
                                if not (structLayouts.ContainsKey name) then
                                    if nativeCarrier then
                                        failwithf "Native carrier storage '%s' requires a measured layout; header probe did not provide it." name
                                    else
                                        failwithf "ABI-critical storage '%s' requires a measured layout; header probe did not provide it." name
                            else
                                let unit = FidelityCodeGenerator.inUnitOf "typedef" name { baseCtx with DeclarationUnits = unitTypedefMaps }
                                match FidelityCodeGenerator.resolveCType unit.TypedefMap dataModel Set.empty unit.Delegates unit.Enums name with
                                | FidelityCodeGenerator.Scalar r when expectedUnion.IsNone && r.Bits > 0 -> ()
                                | _ when nativeCarrier -> failwithf "Native carrier storage '%s' has no known scalar or measured record representation." name
                                | _ -> failwithf "ABI-critical storage '%s' has no known scalar or measured record representation." name
                    | _ -> ()
                    // An external contract must survive neither duplicate-map collapse
                    // nor namespace filtering. Refuse before any generation effects.
                    let resultBindings = project.Options |> Option.map _.Bindings |> Option.defaultValue [] |> List.filter (fun binding -> binding.ResultRange.IsSome || not binding.CallbackInvocations.IsEmpty || not binding.WritableBuffers.IsEmpty)
                    let bindings = project.Options |> Option.map _.Bindings |> Option.defaultValue []
                    for binding in resultBindings do
                        let contract = if not binding.WritableBuffers.IsEmpty then "writable_buffers" elif binding.ResultRange.IsSome then "result_range" else "callback_invocations"
                        if bindings |> List.filter (fun other -> other.Name = binding.Name) |> List.length <> 1 then
                            failwith $"{binding.Name} {contract}: duplicate binding projections"
                        let selected = project.Namespaces |> List.collect (fun ns ->
                            PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                            |> List.choose (function CppParser.Declaration.Function func when func.Name = binding.Name && not (func.Attributes |> List.exists (fun attribute -> attribute.Kind = "DeprecatedAttr")) -> Some(ns.Name, func) | _ -> None))
                        let unique = selected |> List.distinct
                        if unique.Length <> 1 then
                            failwith $"{binding.Name} {contract}: requires exactly one selected native declaration"
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
                                let unit = FidelityCodeGenerator.inFunctionUnit { baseCtx with DeclarationUnits = unitTypedefMaps } f
                                FidelityCodeGenerator.resolveCType unit.TypedefMap dataModel unit.OpaqueHandles unit.Delegates unit.Enums ty
                                |> FidelityCodeGenerator.clefTypeOf |> markers)
                        | _ -> [])
                        |> Set.ofList
                    { baseCtx with
                        DeclarationUnits = unitTypedefMaps
                        TypeNameUnits = typeNameUnits included (List.zip project.Library.Headers headerDeclLists)
                        PhantomMarkers = if project.Options |> Option.exists (fun o -> o.CHeaderMode && not o.NativePointerSurface) then phantomMarkers else Set.empty
                        NonnullAnnotations = project.Nonnull
                        Bindings = projections |> List.map (fun binding -> binding.Name, binding) |> Map.ofList
                        ValueStructs = project.Options |> Option.map (fun opts -> Set.ofList opts.ValueStructs) |> Option.defaultValue Set.empty
                        NativePointerSurface = project.Options |> Option.exists (fun opts -> opts.NativePointerSurface) }

                // Every descriptor an emitted function carries has a width or a record: refuse
                // a type that resolves nowhere before any binding is written (#162).
                let emittedFunctions =
                    project.Namespaces
                    |> List.collect (fun ns ->
                        if typedProtocol && not ns.XmlInterfaces.IsEmpty then []
                        else PilotAnalyzer.filterDeclarationsWithTypes ns Set.empty declarations)
                    |> List.choose (function
                        | CppParser.Declaration.Function f when not (f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")) -> Some f
                        | _ -> None)
                    |> List.distinct
                requireCanonicalAliasesAgainst aliasRows dataModel resultAliases (List.zip project.Library.Headers headerDeclLists) emittedFunctions
                |> Result.defaultWith failwith
                requireRepresentedDescriptorTypes ctx (List.zip project.Library.Headers headerDeclLists) emittedFunctions
                |> Result.defaultWith failwith

                // Derive common namespace prefix for the project
                let nsPrefix = deriveNamespacePrefix project

                // Validate every selected signal before any regeneration write.
                // GIR and the native header must establish the complete callback
                // shape and carriers; an unsupported signal preserves prior output.
                let signals =
                    let selected = project.Namespaces |> List.collect (fun n -> n.Signals)
                    if selected.IsEmpty then [] else
                    match introspection with
                    | None -> failwith "[[namespace]] signals require [library] introspection files"
                    | Some repo ->
                        match IntrospectionParser.select repo selected with
                        | Ok signals -> signals
                        | Error e -> failwith e
                let signalTypes = TypedSignalGenerator.requiredTypes signals declarations ctx
                let ctx = { ctx with PhantomMarkers = Set.union ctx.PhantomMarkers signalTypes }
                let signalCode = TypedSignalGenerator.render nsPrefix signals declarations ctx

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
                        // Declared value structs: the record types and descriptors by-value
                        // claims cite (#162), emitted though no namespace classifies them here.
                        let valueStructs = ctx.ValueStructs |> Set.filter ctx.Structs.ContainsKey
                        { classified with SharedTypes = Set.unionMany [constants; storageNames; requestedStorage; signalTypes; valueStructs]; LocalTypes = Map.empty }
                    else
                        { classified with
                            SharedTypes = Set.unionMany [classified.SharedTypes; storageNames; signalTypes]
                            LocalTypes = classified.LocalTypes |> Map.map (fun _ names -> Set.difference names (Set.union storageNames signalTypes)) }
                let sharedCount = classification.SharedTypes.Count
                let localCounts = classification.LocalTypes |> Map.toList |> List.sumBy (fun (_, s) -> s.Count)
                logVerbose $"Type classification: {sharedCount} shared, {localCounts} local across {project.Namespaces.Length} namespaces" verbose

                let sharedTypesNs = $"{nsPrefix}.Types"
                // Complete native declaration rendering before any output write.
                // Retain these same results for emission; preflight must not be a
                // second, weaker source of boundary-contract decisions.
                let namespaceCode =
                    project.Namespaces |> List.map (fun ns ->
                        let lastSegment = ns.Name.Split('.') |> Array.last
                        let localTypeNames = Map.tryFind ns.Name classification.LocalTypes |> Option.defaultValue Set.empty
                        let localTypesNs = $"{ns.Name}.Types"
                        let localCode =
                            if localTypeNames.IsEmpty then None else
                            let localTypeDecls = PilotAnalyzer.filterTypesOnly localTypeNames declarations
                            let localHandles = Set.intersect ctx.OpaqueHandles localTypeNames
                            Some (FidelityCodeGenerator.generateModule ctx localHandles localTypeDecls
                                      localTypesNs ns.Library "Local type definitions" [sharedTypesNs])
                        let funcOpenModules = if localTypeNames.IsEmpty then [sharedTypesNs] else [sharedTypesNs; localTypesNs]
                        let funcDecls =
                            if typedProtocol && not ns.XmlInterfaces.IsEmpty then []
                            else PilotAnalyzer.filterDeclarationsWithTypes ns Set.empty declarations
                        let funcCount = funcDecls |> List.filter (function CppParser.Declaration.Function _ -> true | _ -> false) |> List.length
                        let funcCode = FidelityCodeGenerator.generateModule ctx Set.empty funcDecls
                                           ns.Name ns.Library $"{lastSegment} function declarations" funcOpenModules
                        let mappedDecls = MappedReturnGenerator.generate project ctx declarations ns sharedTypesNs
                        let funcCode =
                            MappedReturnGenerator.mappings project
                            |> List.filter (fun mapping -> ns.Functions |> List.contains mapping.Acquire)
                            |> List.fold (fun (code: string) mapping ->
                                code.Replace("\nlet " + mapping.Acquire + " (", "\nlet private " + mapping.Acquire + " (")) funcCode
                        let funcCode = funcCode + "\n" + (mappedDecls |> List.map CodeRenderer.render |> String.concat "\n")
                        let wrapperCode =
                            if not generateWrappers then None else
                            let allNsDecls = PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                            let passthroughFunctions =
                                project.ErrorConventions
                                |> Option.map (fun spec -> spec.Overrides |> Map.toList |> List.choose (fun (name, convention) ->
                                    if convention = PilotTypes.NoErrorConvention then Some name else None) |> Set.ofList)
                                |> Option.defaultValue Set.empty
                            Some (WrapperCodeGenerator.generateWithContext (Some ctx) ctx.NativePointerSurface passthroughFunctions
                                      allNsDecls $"{ns.Name}.Api" ns.Library ns.Name errorHandling dataModel project.Nonnull)
                        ns, localTypeNames.Count, localCode, funcCount, funcCode, wrapperCode)

                let typedProtocolContents =
                    if typedProtocol then TypedProtocolGenerator.render project xmlProtocols declarations ctx outputDir
                    else []

                // ── Shared Types.clef (root level) ──────────────────────────
                let sharedTypeDecls = PilotAnalyzer.filterTypesOnly classification.SharedTypes declarations
                let sharedHandles = Set.intersect ctx.OpaqueHandles classification.SharedTypes
                let sharedCode =
                    FidelityCodeGenerator.generateModule ctx sharedHandles sharedTypeDecls
                        sharedTypesNs project.Library.Name "Shared type definitions" []
                let sharedTypesPath = Path.Combine(outputDir, "Types.clef")
                Directory.CreateDirectory(outputDir) |> ignore
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
                        let output = ErrnoModuleGenerator.generateWithModel effectiveMacros errnoNs project.Library.Name dataModel
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
                // Types module (wiki 14_Binding_Generation_Gaps §2); there is no separate Descriptors file to write.

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
                    namespaceCode |> List.collect (fun (ns, localTypeCount, localCode, funcCount, funcCode, wrapperCode) ->
                        let lastSegment = ns.Name.Split('.') |> Array.last
                        let nsDir = Path.Combine(outputDir, lastSegment)
                        Directory.CreateDirectory(nsDir) |> ignore

                        // ── Local Types.clef (if namespace has local types) ──
                        let localTypeFiles =
                            match localCode with
                            | None -> []
                            | Some localCode ->
                                let localTypesPath = Path.Combine(nsDir, "Types.clef")
                                File.WriteAllText(localTypesPath, localCode)
                                logVerbose $"  {lastSegment}/Types.clef ({localTypeCount} types)" verbose
                                [localTypesPath]

                        // ── Functions .clef ──────────────────────────────────
                        let funcPath = Path.Combine(nsDir, $"{lastSegment}.clef")
                        File.WriteAllText(funcPath, funcCode)
                        logVerbose $"  {lastSegment}/{lastSegment}.clef ({funcCount} functions)" verbose

                        // ── Wrappers (optional) ─────────────────────────────
                        let wrapperFiles =
                            match wrapperCode with
                            | Some wrapperCode ->
                                let wrapperPath = Path.Combine(nsDir, $"{lastSegment}Api.clef")
                                File.WriteAllText(wrapperPath, wrapperCode)
                                logVerbose $"  {lastSegment}/{lastSegment}Api.clef" verbose
                                [wrapperPath]
                            | None -> []

                        localTypeFiles @ [funcPath] @ wrapperFiles)

                let typedProtocolFiles = TypedProtocolGenerator.emit typedProtocolContents
                let signalFiles =
                    match signalCode with
                    | None -> []
                    | Some contents ->
                        let dir = Path.Combine(outputDir, "Bridge")
                        Directory.CreateDirectory(dir) |> ignore
                        let path = Path.Combine(dir, "Signals.clef")
                        File.WriteAllText(path, contents)
                        [path]
                let allFiles = [sharedTypesPath] @ errorModuleFiles @ nsFiles @ typedProtocolFiles @ signalFiles

                // Generate canonical fidproj for the binding library
                let fidprojFile = generateFidproj project (if typedProtocol then nsPrefix + ".Native" else nsPrefix) outputDir dependencies allFiles verbose

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

                        let fidprojDir = fidprojFile |> Option.map Path.GetDirectoryName |> Option.defaultValue outputDir

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

                        // Callback wrappers file
                        let callbackFiles =
                            match req.HasCallbackWrappers, callbackSpec with
                            | true, Some spec ->
                                let callbackNs = $"{bridgeName}.Callbacks"
                                // Forward typed entries through the binding module that owns each declaration.
                                let registrationModules =
                                    spec.Registrations
                                    |> List.map (fun reg -> resolveModule reg.Function)
                                    |> List.distinct
                                let callbackOpens =
                                    [ $"{nsPrefix}.Types" ] @ registrationModules
                                    |> List.distinct
                                let generated =
                                    CallbackWrapperGenerator.generateTypedWithModules spec declarations callbackNs ctx callbackOpens resolveModule
                                match generated with
                                | Some output ->
                                    let callbackPath = Path.Combine(bridgeDir, "Callbacks.clef")
                                    File.WriteAllText(callbackPath, output)
                                    logVerbose $"  L2 marshaling: Callbacks.clef" verbose
                                    [callbackPath]
                                | None -> []
                            | _ -> []

                        let bridgeFiles = callbackFiles

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
                            let platformPath, platformDescription, _ = dependencies
                            platformDescription |> Option.iter (fun description ->
                                let value = Fidelity.Data.TOML.Toml.serializeValue (Fidelity.Data.TOML.TomlValue.String description)
                                sb.AppendLine($"description = {value}") |> ignore)
                            sb.AppendLine("runtime_model = \"libc\"") |> ignore
                            sb.AppendLine("os = \"linux\"") |> ignore
                            sb.AppendLine("arch = \"x86_64\"") |> ignore
                            sb.AppendLine() |> ignore
                            sb.AppendLine("[dependencies]") |> ignore

                            // Use the same validated selection as the native binding project.
                            match platformPath with
                            | Some platformPath ->
                                let platformDep = Path.GetRelativePath(fidprojDir, platformPath).Replace('\\', '/')
                                sb.AppendLine($"\"Fidelity.Platform\" = {{ path = \"{platformDep}\" }}") |> ignore
                            | None -> ()
                            let libDep = fidprojFile |> Option.defaultWith (fun () -> invalidOp "Callback bridge requires its generated binding project")
                                         |> fun path -> Path.GetRelativePath(fidprojDir, path).Replace('\\', '/')
                            sb.AppendLine($"\"{nsPrefix}\" = {{ path = \"{libDep}\" }}") |> ignore

                            let bridgeFidprojPath = Path.Combine(fidprojDir, $"{bridgeName}.fidproj")
                            File.WriteAllText(bridgeFidprojPath, sb.ToString())
                            logVerbose $"Generated L2 marshaling fidproj: {bridgeFidprojPath}" verbose

                            // Generate LAYER3-REPORT.md
                            let report = System.Text.StringBuilder()
                            report.AppendLine($"# Layer 2 Marshaling Report: {bridgeName}") |> ignore
                            report.AppendLine() |> ignore
                            report.AppendLine("## Generated") |> ignore
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
                            report.AppendLine("- Callback wrappers preserve declared entry records and explicit environment arguments.") |> ignore

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
                        [$"Using null_with_reason convention for '{project.Library.Name}'. Functions returning nullable handles call {reasonFn}() on absence; the reason result retains its declared boundary type."]
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

    /// Surface-contract failures are generation diagnostics, not uncaught tooling exceptions.
    let generateFromProject projectPath verbose generateWrappers dataModel =
        try generateFromProjectCore (TypeMapper.tryScalar dataModel) projectPath verbose generateWrappers dataModel
        with ex -> Error $"Binding generation failed: {ex.Message}"

    /// `generateFromProject` with the profile rows clang's alias measurements are checked
    /// against (FS9118) supplied, everything else unchanged. On an LP64 host every integer row
    /// agrees with clang, so a contradiction is shown end to end only against a deliberately
    /// wrong row.
    let generateFromProjectCheckingAliasesAgainst (aliasRows: string -> TypeMapper.AbiRepr option) projectPath verbose generateWrappers dataModel =
        try generateFromProjectCore aliasRows projectPath verbose generateWrappers dataModel
        with ex -> Error $"Binding generation failed: {ex.Message}"
