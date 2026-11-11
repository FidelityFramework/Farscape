namespace Farscape.Core

open System.IO
open Types
open PilotTypes


module BindingGenerator =

    /// Check the emitted Clef surface before publishing any part of a generation.
    let verifyEmittedClefPurity (emittedFiles: (string * string) list) : Result<unit, Diagnostics.Refusal> =
        let forbidden = set [ "obj"; "box"; "null"; "nativeint"; "nativeptr" ]
        let tokensIn (code: string) =
            let found = ResizeArray<string>()
            let mutable index = 0
            let at (text: string) =
                index + text.Length <= code.Length
                && System.String.CompareOrdinal(code, index, text, 0, text.Length) = 0
            let identifierPart c = System.Char.IsLetterOrDigit c || c = '_' || c = '\''
            let characterLength () =
                // A prime or type-variable prefix is not an opening character quote.
                // Admit only a single character or a complete escape followed by its quote.
                if index + 2 < code.Length && code[index + 1] <> '\\'
                   && code[index + 1] <> '\'' && code[index + 1] <> '\r'
                   && code[index + 1] <> '\n' && code[index + 2] = '\'' then 3
                elif index + 3 < code.Length && code[index + 1] = '\\' then
                    let isHex c =
                        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')
                    let digits start count predicate =
                        start + count < code.Length && code[start + count] = '\''
                        && Seq.forall (fun offset -> predicate code[start + offset]) (seq { 0 .. count - 1 })
                    match code[index + 2] with
                    | '\'' | '"' | '\\' | 'a' | 'b' | 'f' | 'n' | 'r' | 't' | 'v'
                        when code[index + 3] = '\'' -> 4
                    | 'x' when digits (index + 3) 2 isHex -> 6
                    | 'u' when digits (index + 3) 4 isHex -> 8
                    | 'U' when digits (index + 3) 8 isHex -> 12
                    | c when c >= '0' && c <= '9'
                             && digits (index + 2) 3 (fun digit -> digit >= '0' && digit <= '9') -> 6
                    | _ -> 0
                else 0
            while index < code.Length do
                if at "//" then
                    index <- index + 2
                    while index < code.Length && code[index] <> '\n' && code[index] <> '\r' do
                        index <- index + 1
                elif at "(*" then
                    index <- index + 2
                    let mutable depth = 1
                    while index < code.Length && depth > 0 do
                        if at "(*" then
                            depth <- depth + 1
                            index <- index + 2
                        elif at "*)" then
                            depth <- depth - 1
                            index <- index + 2
                        else index <- index + 1
                elif at "\"\"\"" then
                    index <- index + 3
                    while index < code.Length && not (at "\"\"\"") do
                        index <- index + 1
                    if index < code.Length then index <- index + 3
                elif code[index] = '"' then
                    index <- index + 1
                    let mutable closed = false
                    while index < code.Length && not closed do
                        match code[index] with
                        | '\\' -> index <- min code.Length (index + 2)
                        | '"' ->
                            closed <- true
                            index <- index + 1
                        | _ -> index <- index + 1
                else
                    let literalLength = if code[index] = '\'' then characterLength () else 0
                    if literalLength > 0 then index <- index + literalLength
                    elif System.Char.IsLetter code[index] || code[index] = '_' || code[index] = '\'' then
                        let start = index
                        index <- index + 1
                        while index < code.Length && identifierPart code[index] do
                            index <- index + 1
                        let token = code.Substring(start, index - start)
                        if forbidden.Contains token then found.Add token
                    else index <- index + 1
            found |> Set.ofSeq |> Set.toList
        let diagnostics =
            emittedFiles |> List.choose (fun (relativeFile, code) ->
                match tokensIn code with
                | [] -> None
                | tokens ->
                    let names = String.concat ", " tokens
                    Diagnostics.diagnostic Diagnostics.EmittedClefImpure
                        $"Emitted Clef file '{relativeFile}' contains forbidden token(s): {names}."
                        (Diagnostics.Located.Unknown "emitted Clef surface purity check; no header declaration location applies")
                    |> Some)
        if diagnostics.IsEmpty then Ok ()
        else Error (Diagnostics.refusalsOf diagnostics " No bindings were written.")

    /// A requested ABI layout probe cannot be replaced by declared layouts. Its failure is
    /// raised as a refusal keeping the probe's structured diagnostics (#186), its text after
    /// "ABI-critical struct layout extraction failed: ".
    let requireLayoutProbe (probe: Result<'T, Diagnostics.Refusal>) : 'T =
        match probe with
        | Ok layouts -> layouts
        | Error refusal -> Diagnostics.raiseRefusal (Diagnostics.prefixed "ABI-critical struct layout extraction failed: " refusal)

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
        // Unnamed bit-fields (#176) and the typedef attributes the record reaches in its unit
        // (#175) are part of its declared shape: they move its bytes.
        let padding = s.UnnamedBitFields |> List.map (fun b -> sprintf "@%d:%s:%A" b.Position b.Type b.Width) |> String.concat ","
        let typedefs = s.TypedefAttributes |> List.map (fun a -> sprintf "%A>%s>%s" a.Site a.Typedef (attributes [a.Attribute])) |> String.concat ","
        String.concat " " [ kind; s.Name; "{"; s.Fields |> List.map field |> String.concat "; "; "}"; attributes s.Attributes; padding; typedefs ]

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
            decls |> List.choose (function CppParser.Declaration.Struct s when s.Name <> "" && CppParser.isDefinedRecord s -> Some (s.Name, s) | _ -> None) |> Map.ofList
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

    /// The known locations among `sources`, in order: a refusal's related declarations.
    let private knownLocations (sources: Diagnostics.Located<Diagnostics.SourceLocation> list) =
        sources |> List.choose (function Diagnostics.Located.Known l -> Some l | Diagnostics.Located.Unknown _ -> None)

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
                                (headers: (string * CppParser.Declaration list) list) : Result<unit, Diagnostics.Refusal> =
        let refusals =
            headers |> List.collect (fun (header, decls) ->
                let facts = unitFactsWith included header decls
                // Each use with the location of the declaration making it.
                let uses =
                    decls |> List.collect (function
                        | CppParser.Declaration.Function f -> (f.ReturnType :: (f.Parameters |> List.map snd)) |> List.map (fun t -> $"function '{f.Name}'", t, f.Location.Location)
                        | CppParser.Declaration.Struct s -> s.Fields |> List.map (fun field -> $"record '{s.Name}' field '{field.Name}'", field.Type, s.Location.Location)
                        | CppParser.Declaration.Typedef t -> [ $"typedef '{t.Name}'", t.UnderlyingType, Diagnostics.inFile header $"Farscape's parse keeps no clang location for typedef '{t.Name}'" ]
                        | _ -> [])
                uses |> List.choose (fun (user, spelling, source) ->
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
                                Some (Diagnostics.diagnostic Diagnostics.EnumUnmeasured $"enum '{name}' is used by value by {user} in '{header}', but that unit's clang parse has no measurement of it; its width cannot be inferred." source)))
        match refusals |> List.distinctBy Diagnostics.render with
        | [] -> Ok ()
        | refusals -> Error (Diagnostics.refusalsOf refusals " No bindings were written.")

    /// No anonymous record without a clang measurement in the unit that declares it (#159).
    /// Every record a header's own parse named from its parent and field (glibc's
    /// `__atomic_wide_counter.__value32`) must carry the layout clang measured in that unit;
    /// otherwise the pilot is refused before any binding is written, naming the record, its
    /// parent, field and location, and the unit. Its layout is never computed from its fields.
    let requireMeasuredAnonymousRecords (headers: (string * CppParser.Declaration list) list) : Result<unit, Diagnostics.Refusal> =
        let refusals =
            headers |> List.collect (fun (header, decls) ->
                decls |> List.choose (function
                    | CppParser.Declaration.Struct ({ Anonymous = Some origin; Measured = None } as s) ->
                        Some (Diagnostics.diagnostic Diagnostics.AnonymousRecordUnmeasured $"anonymous record '{s.Name}' ({CppParser.describeOrigin origin}, declared at {origin.Location}) has no clang-measured layout in '{header}'; its layout is never computed from its declared fields." s.Location.Location)
                    | _ -> None))
        match refusals |> List.distinctBy Diagnostics.render with
        | [] -> Ok ()
        | refusals -> Error (Diagnostics.refusalsOf refusals " No bindings were written.")

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
                                          (functions: CppParser.FunctionDecl list) : Result<unit, Diagnostics.Refusal> =
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
                // A callback value type resolution refuses is located at this function (#195).
                FidelityCodeGenerator.relocateCallbackValue f.Location.Location (Some (FidelityCodeGenerator.bindingKey f.Name "", None)) (fun () ->
                    FidelityCodeGenerator.descriptorTypeSites ctx f
                    |> List.collect (fun (site, cType) ->
                      // A typedef's attribute the type reaches is admitted only on clang's
                      // measurement of the type in its declaring unit (#181).
                      (match FidelityCodeGenerator.typedefAttributeEvidence unit cType with
                       | Some (Error reason) ->
                           [ Diagnostics.diagnostic Diagnostics.ByValueTypedefAttribute $"by-value type '{cType}' of the {site} of function '{f.Name}' in '{unitOf f}' {reason}; a descriptor carries no attribute of a typedef." f.Location.Location ]
                       | _ -> [])
                      @ Option.toList (
                        match FidelityCodeGenerator.descriptorClaimOf unit cType with
                        | FidelityCodeGenerator.ResolvesNowhere ->
                            Some (Diagnostics.diagnostic Diagnostics.ByValueTypeUnresolved $"by-value type '{cType}' of the {site} of function '{f.Name}' in '{unitOf f}' resolves through no typedef, record or profile entry of that unit; a descriptor cannot carry an unknown width." f.Location.Location)
                        // A descriptor citing a record names X.Descriptor, and the binding names the
                        // record's type: both exist only for a declared value struct (its record
                        // type and layout module are emitted in every mode).
                        | FidelityCodeGenerator.RecordByValue (record, ("struct" | "union" as kind)) when not (ctx.ValueStructs.Contains record) ->
                            Some (Diagnostics.diagnostic Diagnostics.RecordWithoutValueType $"{kind} '{record}' is the type of the {site} of function '{f.Name}' in '{unitOf f}', but the pilot declares no value type for it, so neither its record type nor {record}.Descriptor would be emitted for the binding that cites them; declare it in [options] value_structs." f.Location.Location)
                        | _ -> None))))
        match refusals |> List.distinctBy Diagnostics.render with
        | [] -> Ok ()
        | refusals -> Error (Diagnostics.refusalsOf refusals " No bindings were written.")

    /// The typedef facts of a unit's canonicalized result aliases (#171): `__size_t` stands for
    /// clang's canonical type of the results that spell it (`unsigned long`). An alias clang
    /// gave no canonical type has none, and a use of it is refused (`requireCanonicalAliases`).
    let aliasTypedefs (aliases: CppParser.ClangTypeAlias list) : CppParser.Declaration list =
        aliases |> List.choose (fun alias ->
            match alias.Canonical with
            | Ok (canonical, _) ->
                Some (CppParser.Declaration.Typedef { Name = alias.Name; UnderlyingType = canonical; Documentation = Some "clang canonical type of a built-in alias"; Attributes = [] })
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
                                        (functions: CppParser.FunctionDecl list) : Result<unit, Diagnostics.Refusal> =
        let describe (signed: bool) (bits: int) = (if signed then "signed " else "unsigned ") + string bits + "-bit"
        let declaresIn (f: CppParser.FunctionDecl) (decls: CppParser.Declaration list) =
            decls |> List.exists (function
                | CppParser.Declaration.Function g -> g.Name = f.Name && g.ReturnType = f.ReturnType && g.Parameters = f.Parameters
                | _ -> false)
        let check (f: CppParser.FunctionDecl) (header: string) (alias: CppParser.ClangTypeAlias) =
            match alias.Canonical with
            | Error (CppParser.ProbeRefused reason) ->
                Some (Diagnostics.diagnostic Diagnostics.AliasUncanonicalized $"type '{alias.Name}' of the result of function '{f.Name}' in '{header}' is a clang built-in alias that no declaration of that unit names, and clang gives no canonical type for it: {reason}." f.Location.Location)
            | Error (CppParser.IncompleteParameter (parameter, spelled, reason)) ->
                Some (Diagnostics.diagnostic Diagnostics.AliasIncompleteParameter $"type '{alias.Name}' of the result of function '{f.Name}' in '{header}' is a clang built-in alias that no declaration of that unit names, and its canonical type cannot be read: clang cannot type a call of '{f.Name}', whose parameter '{parameter}' takes incomplete type '{spelled}' by value ({reason})." f.Location.Location)
            | Ok (canonical, measured) ->
                let conflict rowText measuredText =
                    Some (Diagnostics.diagnostic Diagnostics.AliasMeasurementConflict $"clang measures the result of function '{f.Name}' in '{header}' ('{alias.Name}', canonically '{canonical}') as {measuredText}, but the {model} profile row for '{canonical}' is {rowText}; the profile and the target must agree." f.Location.Location)
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
        match refusals |> List.distinctBy Diagnostics.render with
        | [] -> Ok ()
        | refusals -> Error (Diagnostics.refusalsOf refusals " No bindings were written.")

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
            | CppParser.Declaration.Struct s when s.Name <> "" && CppParser.isDefinedRecord s -> Some (("struct", s.Name), declaredShape s)
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
                                 (headers: (string * CppParser.Declaration list) list) : Result<unit, Diagnostics.Refusal> =
        let conflicts =
            headers |> List.collect (fun (header, decls) ->
                let own = decls |> List.choose (function CppParser.Declaration.Typedef t -> Some t.Name | _ -> None)
                if own.IsEmpty then [] else
                let facts = unitFactsWith included header decls
                FidelityCodeGenerator.unitProfileConflicts model header facts.Typedefs facts.Enums own)
        match conflicts with
        | [] -> Ok ()
        | conflicts -> Error (Diagnostics.refusalsOf conflicts "")

    /// One definition per enum name per pilot. Each header's unit declares its enums with the
    /// representation clang measured in that unit; two units declaring one name must agree on
    /// that representation, the fixed underlying type and every enumerator (name and value), or
    /// the pilot is refused naming each distinct definition and every header giving it. Checked
    /// while the declaration lists are still paired with their headers, before the merge keeps
    /// the first; past this check the merged enum facts agree with every declaring unit.
    let agreeEnums (included: Map<string, CppParser.Declaration list>) (headers: (string * CppParser.Declaration list) list) : Result<unit, Diagnostics.Refusal> =
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
                let locations = perHeader |> List.choose (fun (_, enums) -> enums.TryFind name |> Option.map (fun e -> e.Location.Location))
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
                    Diagnostics.diagnostic Diagnostics.EnumDivergent $"enum '{name}' is declared differently by the pilot's headers: {described}. One declaration per pilot is required." (List.head locations)
                    |> Diagnostics.withRelated (knownLocations (List.tail locations))
                    |> Some)
        if divergent.IsEmpty then Ok ()
        else Error (Diagnostics.refusalsOf divergent " No bindings were written.")

    /// One definition per record name per pilot (#161). Each header's unit declares its records
    /// as its own clang parse sees them; two units defining one struct or union name must agree
    /// on its kind, every field (name, type, extent, bit width and attributes, in order) and the
    /// record's attributes, whether or not the pilot measures the record, and on every layout
    /// clang measured for it in those units. Otherwise the pilot is refused naming the record,
    /// each distinct definition with every header giving it, and the first field at which a
    /// definition differs from the first. A forward declaration (no members) is not a definition.
    /// Checked while declaration lists are still paired with their headers, before the merge
    /// keeps the first definition; identical definitions merge.
    let agreeRecords (headers: (string * CppParser.Declaration list) list) : Result<unit, Diagnostics.Refusal> =
        let definitionsOf (decls: CppParser.Declaration list) =
            (Map.empty, decls) ||> List.fold (fun acc decl ->
                match decl with
                | CppParser.Declaration.Struct s when s.Name <> "" && CppParser.isDefinedRecord s && not (Map.containsKey s.Name acc) -> Map.add s.Name s acc
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
        // The record's unnamed bit-fields (#176) and the typedef attributes it reaches (#175).
        let paddingText (s: CppParser.StructDecl) =
            s.UnnamedBitFields |> List.map CppParser.describeUnnamedBitField
        let typedefText (s: CppParser.StructDecl) =
            s.TypedefAttributes |> List.map (fun a ->
                let site = match a.Site with CppParser.FieldType field -> $"field '{field}'" | CppParser.RecordName -> "its name"
                let attribute = attributes [a.Attribute] |> String.concat ""
                $"{site} reaches typedef '{a.Typedef}' {attribute}")
        let describe (s: CppParser.StructDecl) =
            let attrs = match attributes s.Attributes with [] -> "" | list -> " [" + String.concat ", " list + "]"
            let facts = match paddingText s @ typedefText s with [] -> "" | list -> " (" + String.concat "; " list + ")"
            $"""{kindOf s} {s.Name} {{ {s.Fields |> List.map fieldText |> String.concat "; "} }}{attrs}{facts}"""
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
            | None when paddingText first <> paddingText other ->
                let a = paddingText first |> String.concat ", "
                let b = paddingText other |> String.concat ", "
                $"its unnamed bit-fields: [{a}] in '{firstHeader}', [{b}] in '{otherHeader}'"
            | None when typedefText first <> typedefText other ->
                let a = typedefText first |> String.concat ", "
                let b = typedefText other |> String.concat ", "
                $"its typedef attributes: [{a}] in '{firstHeader}', [{b}] in '{otherHeader}'"
            | None ->
                let a = attributes first.Attributes |> String.concat ", "
                let b = attributes other.Attributes |> String.concat ", "
                $"its attributes: [{a}] in '{firstHeader}', [{b}] in '{otherHeader}'"
        // Where two clang measurements of one declared record first depart.
        let measuredDifference (firstHeader: string, first: CppParser.StructDecl, a: CppParser.StructLayoutInfo) (otherHeader: string, b: CppParser.StructLayoutInfo) =
            let fieldsOf layout = CppParser.fieldOffsetsBits first layout
            let offsets = List.zip3 first.Fields (fieldsOf a |> List.truncate first.Fields.Length) (fieldsOf b |> List.truncate first.Fields.Length)
            match offsets |> List.tryFind (fun (_, x, y) -> x <> y) with
            | Some (f, x, y) when a.FieldOffsetsBits.Length = b.FieldOffsetsBits.Length ->
                $"field '{f.Name}': clang-measured offset {x / 8} in '{firstHeader}', {y / 8} in '{otherHeader}'"
            | _ ->
                $"its clang-measured layout: {a.SizeBits / 8} bytes at alignment {a.AlignmentBits / 8} in '{firstHeader}', {b.SizeBits / 8} bytes at alignment {b.AlignmentBits / 8} in '{otherHeader}'"
        let headersOf (group: (string * CppParser.StructDecl) list) = group |> List.map (fun (h, _) -> $"'{h}'") |> String.concat ", "
        let divergent =
            names |> List.choose (fun name ->
                let givers = perHeader |> List.choose (fun (header, defs) -> defs.TryFind name |> Option.map (fun s -> header, s))
                // The first definition is the source; every other definition follows it.
                let refused message =
                    let locations = givers |> List.map (fun (_, s) -> s.Location.Location)
                    Diagnostics.diagnostic Diagnostics.RecordDivergent message (List.head locations)
                    |> Diagnostics.withRelated (knownLocations (List.tail locations))
                    |> Some
                let identity (s: CppParser.StructDecl) = kindOf s, s.Fields |> List.map fieldText, attributes s.Attributes, paddingText s, typedefText s
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
                        refused $"record '{name}' ({describe s1}) is measured differently by the pilot's headers: {listed}; first difference at {differences}. One definition per pilot is required."
                | [] -> None
                | firstGroup :: rest ->
                    let first = List.head firstGroup
                    let described = shapes |> List.map (fun group -> $"{headersOf group} ({describe (snd (List.head group))})") |> String.concat "; "
                    let differences = rest |> List.map (fun group -> firstDifference first (List.head group)) |> String.concat "; "
                    refused $"record '{name}' is defined differently by the pilot's headers: {described}; first difference at {differences}. One definition per pilot is required.")
        if divergent.IsEmpty then Ok ()
        else Error (Diagnostics.refusalsOf divergent " No bindings were written.")

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
    /// Identical text is not agreement on its own: an object macro is evaluated by clang in each
    /// unit (#192, #193), and the same text over a dependency each unit defines differently
    /// (`(PRIVATE + 7)`) evaluates differently. Two definitions agree only when clang's
    /// evaluations in their units agree too (the integer, the sentinel's type and address, the
    /// string's type, or that clang evaluates neither).
    let agreeMacros (headers: (string * bool * CppParser.Declaration list) list) : Result<unit, Diagnostics.Refusal> =
        let evaluation (m: CppParser.MacroDecl) =
            match m.Kind with
            | CppParser.IntegerConstant (_, value) -> Some ($"integer {value}", $"{value}")
            | CppParser.FunctionPointerSentinel (_, pointerType, canonical, value) -> Some ($"sentinel {pointerType} {canonical} {value}", $"({pointerType}){value}")
            | CppParser.StringConstant (_, clangType) -> Some ($"string {clangType}", $"a string of type {clangType}")
            // clang's reason can name the unit's own paths: the key is only that it found no value.
            | CppParser.Unevaluated _ -> Some ("unevaluated", "no value")
            | CppParser.SimpleValue _ | CppParser.Expression _ | CppParser.FunctionLike _ | CppParser.TypeCast _ -> None
        let definition cpp (m: CppParser.MacroDecl) =
            let (shape, spelling) =
                match m.Kind with
                | CppParser.FunctionLike (args, body) -> (Some args, macroTokens cpp body), "(" + String.concat ", " args + ") " + body
                | _ -> (None, macroTokens cpp m.RawValue), m.RawValue
            match evaluation m with
            | Some (key, described) -> (shape, Some key), ($"'{spelling}'", $"'{spelling}' (clang evaluates {described} in that unit)")
            | None -> (shape, None), ($"'{spelling}'", $"'{spelling}' (not evaluated by clang)")
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
                    // The evaluation is named only where the text alone does not tell the
                    // definitions apart.
                    let evaluated = (keys |> List.map fst |> List.distinct).Length < keys.Length
                    let described =
                        keys |> List.map (fun key ->
                            let same = givers |> List.filter (fun (k, _, _) -> k = key)
                            let _, (text, withEvaluation), _ = List.head same
                            let spelling = if evaluated then withEvaluation else text
                            let hs = same |> List.map (fun (_, _, h) -> $"'{h}'") |> String.concat ", "
                            $"{spelling} in {hs}")
                        |> String.concat "; "
                    // `clang -E -dM` lists macro definitions without locations: each defining
                    // header is known, no line in it.
                    let unlocated = $"clang -E -dM gives macro '{name}' no location"
                    let defining = givers |> List.map (fun (_, _, header) -> header)
                    Diagnostics.diagnostic Diagnostics.MacroDivergent $"macro '{name}' is defined differently by the pilot's headers: {described}. One definition per pilot is required." (Diagnostics.inFile (List.head defining) unlocated)
                    |> Diagnostics.withRelated (defining |> List.tail |> List.map (fun header -> { Diagnostics.SourceLocation.Path = header; Diagnostics.SourceLocation.Position = Diagnostics.Located.Unknown unlocated }))
                    |> Some)
        if divergent.IsEmpty then Ok ()
        else Error (Diagnostics.refusalsOf divergent " No bindings were written.")

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
        /// Every parsed macro (each one the pilot's `macro_prefixes` select) generation emits as
        /// no constant, by name and kind (#193): none is dropped without a word.
        UnemittedMacros: FidelityCodeGenerator.UnemittedMacro list
    }

    /// Generate Clef bindings from a C/C++ header file.
    /// Returns Result to enforce proper error handling - fails fast on parse errors.
    let private generateBindingsCore (options: GenerationOptions) : Result<GenerationResult, Diagnostics.Refusal> =
        logVerbose $"Starting binding generation for {options.HeaderFile}" options.Verbose
        logVerbose $"Target library: {options.LibraryName}" options.Verbose
        logVerbose $"Output directory: {options.OutputDirectory}" options.Verbose
        logVerbose $"Namespace: {options.Namespace}" options.Verbose

        logVerbose "Parsing header file..." options.Verbose

        let parseOptions =
            { CppParser.defaultOptions options.HeaderFile.FullName with
                IncludePaths = options.IncludePaths; Defines = options.Defines; Verbose = options.Verbose }
        let parsed =
            CppParser.parseHeaderFullDiagnosed parseOptions
            |> Result.bind (fun result ->
                match result.Declarations @ (result.Macros |> List.map CppParser.Declaration.Macro) with
                | [] -> Error (Diagnostics.uncoded $"Parse succeeded but no declarations found in {options.HeaderFile.Name}.")
                | declarations -> Ok declarations)
        match parsed with
        | Error refusal ->
            Error (Diagnostics.prefixed "Failed to parse header: " refusal)
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
            (outputPath, generatedCode) :: wrapperOutputs
            |> List.map (fun (path, code) -> Path.GetRelativePath(options.OutputDirectory, path), code)
            |> verifyEmittedClefPurity
            |> Result.defaultWith Diagnostics.raiseRefusal
            Directory.CreateDirectory(options.OutputDirectory) |> ignore
            for path, code in (outputPath, generatedCode) :: wrapperOutputs do
                File.WriteAllText(path, code)
                logVerbose $"Binding module written to: {path}" options.Verbose

            Ok {
                OutputFiles = outputPath :: (wrapperOutputs |> List.map fst)
                DeclarationCount = declarations.Length
                Advisories = []
                UnemittedMacros = FidelityCodeGenerator.unemittedMacros declarations
            }

    /// Generate bindings from one header (`farscape generate`). A failure carries the structured
    /// diagnostics of its FS91xx refusals (#186), whether the parse refuses (a malformed define,
    /// FS9124) or generation raises it (an unmeasured enum, FS9103; a by-value type resolving
    /// nowhere, FS9115), with the exact text `generateBindings` reports as its `Text`.
    let generateBindingsDiagnosed (options: GenerationOptions) : Result<GenerationResult, Diagnostics.Refusal> =
        try generateBindingsCore options
        with
        | :? Diagnostics.RefusalException as refused -> Error (Diagnostics.prefixed "Binding generation failed: " refused.Refusal)
        | ex -> Error (Diagnostics.uncoded $"Binding generation failed: {ex.Message}")

    /// `generateBindingsDiagnosed`, reporting a failure as its text.
    let generateBindings options : Result<GenerationResult, string> =
        generateBindingsDiagnosed options |> Result.mapError _.Text

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
        (verbose: bool) : string =
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
        fidprojPath

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
    let private generateFromProjectCore (aliasRows: string -> TypeMapper.AbiRepr option) (projectPath: string) (verbose: bool) (generateWrappers: bool) (dataModel: PlatformABI) : Result<GenerationResult, Diagnostics.Refusal> =
        let uncoded = Diagnostics.uncoded
        match PilotSerializer.loadFromFileDiagnosed projectPath with
        | Error refusal -> Error (Diagnostics.prefixed "Failed to load project: " refusal)
        | Ok _ when dataModel <> PlatformABI.LP64 ->
            Error (uncoded "Project generation currently declares a Linux x86_64 LP64 target. A different ABI requires an explicit target, header/sysroot and link contract; emitting it with the Linux manifest would be incorrect. No bindings were written.")
        | Ok project when project.Options |> Option.exists (fun opts -> opts.NativePointerSurface) ->
            FidelityCodeGenerator.nativePointerSurfaceRefusal (Diagnostics.Located.Unknown "experimental_native_pointer_surface is pilot input, not a header declaration")
            |> Diagnostics.refusalOf |> Diagnostics.suffixed " No bindings were written." |> Error
        | Ok project when not project.Library.XmlProtocols.IsEmpty
                          && not (project.Options |> Option.exists (fun opts -> opts.TypedProtocol)) ->
            Error (uncoded "XML protocol generation requires options.typed_protocol = true. The legacy protocol generator exposes pointer operations in Clef source. No bindings were written.")
        | Ok project when generateWrappers && (project.ErrorConventions |> Option.exists (fun spec -> spec.Default = PilotTypes.Errno)) ->
            Error (uncoded "errno wrappers require a declared native scalar accessor; an opaque errno handle cannot be dereferenced in Clef. No bindings were written.")
        | Ok project when generateWrappers && (project.ErrorConventions |> Option.exists (fun spec -> match spec.Default with PilotTypes.NullWithReason _ -> true | _ -> false)) ->
            Error (uncoded "null_with_reason wrappers require the declared reason return type and a native text extent/ownership projection. An assumed error-text handle is not an admitted contract. No bindings were written.")
        | Ok project ->
            // Each define reaches clang as one -D argument; a define that is not NAME or
            // NAME=VALUE is refused before any header is parsed (#178).
            match CppParser.requireWellFormedDefines project.Library.Defines with
            | Error refusal -> Error (Diagnostics.suffixed " No bindings were written." refusal)
            | Ok () ->
            let projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            let outputDir =
                if Path.IsPathRooted(project.Output.Directory) then project.Output.Directory
                else Path.GetFullPath(Path.Combine(projectDir, project.Output.Directory))
            let dependencies = resolveProjectDependencies project (projectDirectoryForOutput outputDir)
            let cHeaders = project.Options |> Option.exists (fun opts -> opts.NativePointerSurface || opts.CHeaderMode)
            let headerCppMode header = not cHeaders && CppParser.detectCppMode header
            // Parse C/C++ headers
            let headerResults =
                project.Library.Headers |> List.map (fun headerPath ->
                    logVerbose $"Parsing header: {headerPath}" verbose
                    let includeRoot =
                        let fullPath = Path.GetFullPath headerPath
                        project.Library.IncludePaths @ project.Library.SystemIncludePaths
                        |> List.tryFind (fun ip ->
                            let fullIp = Path.GetFullPath ip
                            fullPath.StartsWith fullIp)
                    if cHeaders then
                        // Native carrier pilots bind the C ABI. pthread.h also contains guarded
                        // C++ cleanup classes, which must not turn its public unions into C++ PODs.
                        CppParser.parseHeaderUnitDiagnosed {
                            HeaderFile = headerPath; IncludePaths = project.Library.IncludePaths
                            SystemIncludePaths = project.Library.SystemIncludePaths
                            Defines = project.Library.Defines; IncludeRoot = includeRoot
                            MacroPrefixes = project.Library.MacroPrefixes; IncludeMacros = true
                            Verbose = verbose; CppMode = false }
                    else
                        CppParser.parseHeaderUnitDiagnosed {
                            HeaderFile = headerPath; IncludePaths = project.Library.IncludePaths
                            SystemIncludePaths = project.Library.SystemIncludePaths
                            Defines = project.Library.Defines; IncludeRoot = includeRoot
                            MacroPrefixes = project.Library.MacroPrefixes; IncludeMacros = true
                            Verbose = verbose; CppMode = headerCppMode headerPath })

            let headerErrors = headerResults |> List.choose (function Error e -> Some e | _ -> None)
            if not headerErrors.IsEmpty then
                Error (Diagnostics.prefixed "Failed to parse headers: " (Diagnostics.joined "; " headerErrors))

            // Parse XML protocol files (protocol-defined APIs with marshal dispatch)
            else
            let xmlResults =
                project.Library.XmlProtocols |> List.map (fun xmlPath ->
                    logVerbose $"Parsing XML protocol: {xmlPath}" verbose
                    ProtocolParser.parseFileDiagnosedFor dataModel xmlPath)

            let xmlErrors = xmlResults |> List.choose (function Error e -> Some e | _ -> None)
            if not xmlErrors.IsEmpty then
                Error (Diagnostics.prefixed "Failed to parse XML protocols: " (Diagnostics.joined "; " xmlErrors))
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
                let units = List.zip project.Library.Headers headerDeclLists
                agreeMacros (units |> List.map (fun (header, decls) -> header, headerCppMode header, decls)) |> Result.defaultWith Diagnostics.raiseRefusal
                agreeEnums included units |> Result.defaultWith Diagnostics.raiseRefusal
                requireMeasuredEnumUses included units |> Result.defaultWith Diagnostics.raiseRefusal
                agreeRecords units |> Result.defaultWith Diagnostics.raiseRefusal
                requireMeasuredAnonymousRecords units |> Result.defaultWith Diagnostics.raiseRefusal
                let xmlProtocols = xmlResults |> List.choose (function Ok p -> Some p | _ -> None)

                // GObject introspection data: the source of signal entry signatures (IntrospectionParser)
                let introspection =
                    if project.Library.Introspection.IsEmpty then None
                    else
                        match IntrospectionParser.loadDiagnosed project.Library.Introspection with
                        | Ok repo -> Some repo
                        | Error d -> Diagnostics.refuse d

                let typedProtocol = project.Options |> Option.exists (fun o -> o.TypedProtocol)
                let declarations = DeclarationAlgebra.mergeDeclarations headerDeclLists
                let projections = project.Options |> Option.map (fun opts -> opts.Bindings) |> Option.defaultValue []
                let aliases = projections |> List.choose (fun binding ->
                    let original = declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = binding.Symbol -> Some f | _ -> None)
                    match original with
                    | None ->
                        Diagnostics.diagnostic Diagnostics.BindingSymbolUndeclared
                            $"Binding projection '{binding.Name}' requires C symbol '{binding.Symbol}' in the parsed headers"
                            (Diagnostics.Located.Unknown $"no parsed header declares C symbol '{binding.Symbol}'")
                        |> Diagnostics.withPilotKey (FidelityCodeGenerator.bindingKey binding.Name "symbol") None
                        |> Diagnostics.refuse
                    | Some original ->
                        let refuse code key parameter message =
                            FidelityCodeGenerator.refuseContract original code (Some (FidelityCodeGenerator.bindingKey binding.Name key, parameter)) message
                        for key, indices in [ "reference_parameters", binding.ReferenceParameters; "read_only_reference_parameters", binding.ReadOnlyReferenceParameters
                                              "nonnull_callbacks", binding.NonnullCallbacks; "string_parameters", binding.StringParameters ] do
                            for index in indices do
                                if index < 0 || index >= original.Parameters.Length then
                                    refuse Diagnostics.ContractParameterOutOfRange key (Some index) $"Binding projection '{binding.Name}' has invalid parameter index {index}"
                        match binding.ReferenceParameters |> List.tryFind (fun index -> List.contains index binding.ReadOnlyReferenceParameters) with
                        | Some index -> refuse Diagnostics.ReferenceAccessConflict "read_only_reference_parameters" (Some index) $"Binding projection '{binding.Name}' declares a parameter both writable and read-only"
                        | None -> ()
                        for key, length, what in [ "constant_parameters", binding.ConstantParameters.Length, "constant parameters"
                                                   "reference_elements", binding.ReferenceElements.Length, "reference elements"
                                                   "parameter_handles", binding.ParameterHandles.Length, "parameter handles" ] do
                            if length > original.Parameters.Length then
                                refuse Diagnostics.ContractParameterOutOfRange key (Some original.Parameters.Length) $"Binding projection '{binding.Name}' has too many {what}"
                        if binding.Name = binding.Symbol then None
                        else Some (CppParser.Declaration.Function { original with Name = binding.Name }))
                let declarations = declarations @ aliases
                // Each unit's own typedefs, kept per declaration through measurement and generation.
                let unitTypedefMaps =
                    unitTypedefMaps included declarations (List.zip project.Library.Headers headerDeclLists)
                        (projections |> List.map (fun binding -> binding.Name, binding.Symbol))
                agreeUnitProfileTypedefs dataModel included (List.zip project.Library.Headers headerDeclLists) |> Result.defaultWith Diagnostics.raiseRefusal
                if generateWrappers then
                    let callbacks = project.Callbacks |> Option.defaultWith (fun () -> PilotAnalyzer.discoverCallbacks declarations)
                    // Each listener struct is refused at its clang location (#195). The key is
                    // [callbacks] listener_structs: it names the struct, or, absent, the structs
                    // are discovered from the headers in its place.
                    if not callbacks.ListenerStructs.IsEmpty then
                        callbacks.ListenerStructs
                        |> List.map (fun listener ->
                            let source =
                                declarations |> List.tryPick (function CppParser.Declaration.Struct s when s.Name = listener.Name -> Some s.Location.Location | _ -> None)
                                |> Option.defaultValue (Diagnostics.Located.Unknown $"no parsed header declares listener struct '{listener.Name}'")
                            Diagnostics.diagnostic Diagnostics.GenericListenerUnadmitted
                                $"listener struct '{listener.Name}': generic listener builders require declared native entry records and initialized fields. The legacy integer-address builder is not admitted; use typed protocol or signal projections."
                                source
                            |> Diagnostics.withPilotKey "callbacks.listener_structs" None)
                        |> fun ds -> Diagnostics.raiseRefusal (Diagnostics.refusalsOf ds " No bindings were written.")
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
                let preflightAbiCritical (checkCtx: FidelityCodeGenerator.GenerationContext) =
                    match project.Options with
                    | Some opts ->
                        let nativeCarrier = opts.NativePointerSurface || opts.CHeaderMode
                        for requested in opts.AbiCriticalStructs do
                            let name = requested.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")
                            let expectedUnion =
                                if requested.StartsWith("struct ") then Some false
                                elif requested.StartsWith("union ") then Some true
                                else None
                            let declared = checkCtx.Structs.TryFind name
                            let refuse code message =
                                let source =
                                    declared |> Option.map _.Location.Location
                                    |> Option.defaultValue (Diagnostics.Located.Unknown $"no parsed header declares a record named '{name}'")
                                Diagnostics.diagnostic code message source
                                |> Diagnostics.withPilotKey "options.abi_critical_structs" None
                                |> Diagnostics.refuse
                            if requested.StartsWith("typedef ") && not (checkCtx.TypedefMap.ContainsKey name) then
                                refuse Diagnostics.AbiStorageUndeclared $"ABI-critical typedef '{name}' is not declared."
                            if expectedUnion |> Option.exists (fun expected -> declared |> Option.exists (fun record -> record.IsUnion <> expected)) then
                                refuse Diagnostics.AbiStorageKindMismatch $"ABI-critical storage '{name}' does not match the requested record kind."
                            if declared.IsNone then
                                let unit = FidelityCodeGenerator.inUnitOf "typedef" name { checkCtx with DeclarationUnits = unitTypedefMaps }
                                match FidelityCodeGenerator.resolveCType unit.TypedefMap dataModel Set.empty unit.Delegates unit.Enums name with
                                | FidelityCodeGenerator.Scalar r when expectedUnion.IsNone && r.Bits > 0 -> ()
                                | _ ->
                                    let what = if nativeCarrier then "Native carrier" else "ABI-critical"
                                    refuse Diagnostics.AbiStorageUndeclared $"{what} storage '{name}' has no known scalar or measured record representation."
                    | None -> ()

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
                            |> Result.defaultWith (fun message ->
                                Diagnostics.diagnostic Diagnostics.AbiStorageUnitDisagrees message
                                    (Diagnostics.Located.Unknown "the measuring unit and the pilot's declaring header are named in the message")
                                |> Diagnostics.withPilotKey "options.abi_critical_structs" None
                                |> Diagnostics.refuse)
                        preflightAbiCritical probeCtx
                        let headerDeclsByPath = List.zip project.Library.Headers headerDeclLists |> Map.ofList
                        units
                        |> List.map (fun (headerFile, names) ->
                            let ownDecls =
                                match headerDeclsByPath.TryFind headerFile with
                                | Some declarations -> declarations
                                | None ->
                                    Diagnostics.diagnostic Diagnostics.AbiStorageUnitDisagrees
                                        $"ABI layout measuring unit '{headerFile}' has no parsed declaration list."
                                        (Diagnostics.Located.Unknown "the measuring header is absent from the pilot's parsed declaring units")
                                    |> Diagnostics.withPilotKey "options.abi_critical_structs" None
                                    |> Diagnostics.refuse
                            let unitTypedefs = (unitFactsWith included headerFile ownDecls).Typedefs
                            let spelledNames =
                                names |> List.map (fun req ->
                                    if req.StartsWith("typedef ") || req.StartsWith("struct ") || req.StartsWith("union ") then req
                                    elif unitTypedefs.ContainsKey req then "typedef " + req
                                    else
                                        let record =
                                            ownDecls
                                            |> List.tryPick (function CppParser.Declaration.Struct s when s.Name = req -> Some s | _ -> None)
                                            |> Option.orElseWith (fun () -> probeCtx.Structs.TryFind req)
                                        match record with
                                        | Some record when record.IsUnion -> "union " + req
                                        | Some _ -> "struct " + req
                                        | None -> req)
                            let measured =
                                CppParser.extractStructLayoutsForModeDiagnosed headerFile project.Library.IncludePaths project.Library.SystemIncludePaths project.Library.Defines spelledNames (headerCppMode headerFile) verbose
                                |> Result.mapError (fun refusal ->
                                    if not refusal.Diagnostics.IsEmpty then refusal
                                    else
                                        Diagnostics.diagnostic Diagnostics.AbiStorageUnmeasured refusal.Text
                                            (Diagnostics.inFile headerFile "clang layout probe failed in this declaring unit")
                                        |> Diagnostics.withPilotKey "options.abi_critical_structs" None
                                        |> Diagnostics.refusalOf)
                                |> requireLayoutProbe
                            logVerbose $"Extracted layouts for {measured.Count} ABI-critical struct(s) in {headerFile}" verbose
                            headerFile, measured)
                        |> agreeMeasurements
                        |> Result.defaultWith (fun message ->
                            Diagnostics.diagnostic Diagnostics.AbiStorageMeasurementDivergent message
                                (Diagnostics.Located.Unknown "the two measuring units are named in the message")
                            |> Diagnostics.withPilotKey "options.abi_critical_structs" None
                            |> Diagnostics.refuse)

                let ctx =
                    let baseCtx = FidelityCodeGenerator.buildGenerationContext declarations dataModel structLayouts
                    preflightAbiCritical baseCtx
                    match project.Options with
                    | Some opts ->
                        let nativeCarrier = opts.NativePointerSurface || opts.CHeaderMode
                        for requested in opts.AbiCriticalStructs do
                            let name = requested.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")
                            let declared = baseCtx.Structs.TryFind name
                            if declared.IsSome && not (structLayouts.ContainsKey name) then
                                let source = declared |> Option.map _.Location.Location |> Option.defaultValue (Diagnostics.Located.Unknown $"no parsed header declares a record named '{name}'")
                                let what = if nativeCarrier then "Native carrier" else "ABI-critical"
                                Diagnostics.diagnostic Diagnostics.AbiStorageUnmeasured $"{what} storage '{name}' requires a measured layout; header probe did not provide it." source
                                |> Diagnostics.withPilotKey "options.abi_critical_structs" None
                                |> Diagnostics.refuse
                    | _ -> ()
                    // An external contract must survive neither duplicate-map collapse
                    // nor namespace filtering. Refuse before any generation effects.
                    let resultBindings = project.Options |> Option.map _.Bindings |> Option.defaultValue [] |> List.filter (fun binding -> binding.ResultRange.IsSome || not binding.CallbackInvocations.IsEmpty || not binding.WritableBuffers.IsEmpty)
                    let bindings = project.Options |> Option.map _.Bindings |> Option.defaultValue []
                    for binding in resultBindings do
                        let contract = if not binding.WritableBuffers.IsEmpty then "writable_buffers" elif binding.ResultRange.IsSome then "result_range" else "callback_invocations"
                        // Located at the binding's declaration when the headers declare it.
                        let refuse code message =
                            let source =
                                declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = binding.Name -> Some f.Location.Location | _ -> None)
                                |> Option.defaultValue (Diagnostics.Located.Unknown $"no parsed header declares '{binding.Name}'")
                            Diagnostics.diagnostic code message source
                            |> Diagnostics.withPilotKey (FidelityCodeGenerator.bindingKey binding.Name contract) None
                            |> Diagnostics.refuse
                        if bindings |> List.filter (fun other -> other.Name = binding.Name) |> List.length <> 1 then
                            refuse Diagnostics.BindingDuplicate $"{binding.Name} {contract}: duplicate binding projections"
                        let selected = project.Namespaces |> List.collect (fun ns ->
                            PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                            |> List.choose (function CppParser.Declaration.Function func when func.Name = binding.Name && not (func.Attributes |> List.exists (fun attribute -> attribute.Kind = "DeprecatedAttr")) -> Some(ns.Name, func) | _ -> None))
                        let unique = selected |> List.distinct
                        if unique.Length <> 1 then
                            refuse Diagnostics.ContractBindingNotSelectedOnce $"{binding.Name} {contract}: requires exactly one selected native declaration"
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
                        CppClasses =
                            project.CppConfig |> Option.map _.Classes |> Option.defaultValue []
                            |> List.filter _.Bind
                            |> List.collect (fun spec -> [ spec.Name, spec; (spec.Name.Split("::") |> Array.last), spec ])
                            |> Map.ofList
                        DeclarationUnits = unitTypedefMaps
                        TypeNameUnits = typeNameUnits included (List.zip project.Library.Headers headerDeclLists)
                        PhantomMarkers = if project.Options |> Option.exists (fun o -> o.CHeaderMode && not o.NativePointerSurface) then phantomMarkers else Set.empty
                        NonnullAnnotations = project.Nonnull
                        Bindings = projections |> List.map (fun binding -> binding.Name, binding) |> Map.ofList
                        ValueStructs = project.Options |> Option.map (fun opts -> Set.ofList opts.ValueStructs) |> Option.defaultValue Set.empty
                        FlexibleArrays = project.Options |> Option.map (fun opts -> opts.FlexibleArrays |> List.map (fun f -> f.Record, f) |> Map.ofList) |> Option.defaultValue Map.empty
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
                |> Result.defaultWith Diagnostics.raiseRefusal
                requireRepresentedDescriptorTypes ctx (List.zip project.Library.Headers headerDeclLists) emittedFunctions
                |> Result.defaultWith Diagnostics.raiseRefusal
                // A declared flexible array count source names a struct some header declares
                // (BAREWire #11); the struct's own layout then checks the member it names.
                for declared in ctx.FlexibleArrays |> Map.toList |> List.map snd do
                    if not (ctx.Structs.ContainsKey declared.Record) then
                        invalidOp $"options.flexible_arrays declares a count source for '{declared.Record}' field '{declared.Field}', but no parsed header declares struct '{declared.Record}'; a count source names a declared struct."

                // Derive common namespace prefix for the project
                let nsPrefix = deriveNamespacePrefix project

                // Validate every selected signal before any regeneration write.
                // GIR and the native header must establish the complete callback
                // shape and carriers; an unsupported signal preserves prior output.
                let signals =
                    let selected = project.Namespaces |> List.collect (fun n -> n.Signals)
                    if selected.IsEmpty then [] else
                    match introspection with
                    | None ->
                        Diagnostics.diagnostic Diagnostics.SignalsWithoutIntrospection "[[namespace]] signals require [library] introspection files"
                            (Diagnostics.Located.Unknown "a namespace's signals and the library's introspection files are pilot input, not a header declaration")
                        |> Diagnostics.withPilotKey "library.introspection" None
                        |> Diagnostics.refuse
                    | Some repo ->
                        match IntrospectionParser.selectDiagnosed repo selected with
                        | Ok signals -> signals
                        | Error d -> Diagnostics.refuse d
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
                        let cppClassNames = ctx.CppClasses |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                        { classified with SharedTypes = Set.unionMany [constants; storageNames; requestedStorage; signalTypes; valueStructs; cppClassNames]; LocalTypes = Map.empty }
                    else
                        { classified with
                            SharedTypes = Set.unionMany [classified.SharedTypes; storageNames; signalTypes]
                            LocalTypes = classified.LocalTypes |> Map.map (fun _ names -> Set.difference names (Set.union storageNames signalTypes)) }
                let sharedCount = classification.SharedTypes.Count
                let localCounts = classification.LocalTypes |> Map.toList |> List.sumBy (fun (_, s) -> s.Count)
                logVerbose $"Type classification: {sharedCount} shared, {localCounts} local across {project.Namespaces.Length} namespaces" verbose

                let sharedTypesNs = $"{nsPrefix}.Types"
                let selectedAnchors =
                    project.Namespaces
                    |> List.collect (fun ns ->
                        let selected =
                            if typedProtocol && not ns.XmlInterfaces.IsEmpty then []
                            else PilotAnalyzer.filterDeclarationsWithTypes ns Set.empty declarations
                        FidelityCodeGenerator.selectedAnchors ctx ns.Name ns.Library selected |> Map.toList)
                    |> Map.ofList
                let ctx = { ctx with Anchors = selectedAnchors }
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

                // Render shared Types.clef, error modules, and L2 callback wrappers in memory
                // before archive verification and disk writes so any refusal fails cleanly with zero writes.
                let sharedTypeDecls = PilotAnalyzer.filterTypesOnly classification.SharedTypes declarations
                let sharedHandles = Set.intersect ctx.OpaqueHandles classification.SharedTypes
                let sharedCode =
                    FidelityCodeGenerator.generateModule ctx sharedHandles sharedTypeDecls
                        sharedTypesNs project.Library.Name "Shared type definitions" []
                let viewMarkers = MappedReturnGenerator.markers project |> List.map CodeRenderer.render |> String.concat "\n"
                let sharedCode = if viewMarkers = "" then sharedCode else sharedCode + "\n" + viewMarkers

                // ── Error module (pure in-memory preparation) ────────────────
                let errorModulePlan =
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
                                    SystemIncludePaths = project.Library.SystemIncludePaths
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
                        [ ("Errno.clef", "Errno module", output) ]
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
                                        [ ($"{config.ErrorStructName}.clef", "Error module", output) ]
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
                        [ ("ReturnCode.clef", "ReturnCode module", sb.ToString()) ]
                    | _ -> []

                // ── Callback spec & L2 marshaling plan (pure in-memory preparation) ────
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
                                let retainedRegistrations =
                                    projections
                                    |> List.collect (fun binding ->
                                        match declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = binding.Name -> Some f | _ -> None) with
                                        | None -> []
                                        | Some func ->
                                            binding.CallbackInvocations
                                            |> List.choose (fun callback ->
                                                if callback.Scope = "retained" && callback.Parameter >= 0 && callback.Parameter < func.Parameters.Length then
                                                    Some (binding.Name, fst func.Parameters[callback.Parameter])
                                                else None))
                                    |> Set.ofList
                                { rawSpec with
                                    Registrations = rawSpec.Registrations |> List.filter (fun r -> claimedFunctions.Contains r.Function && not (retainedRegistrations.Contains (r.Function, r.CallbackParam))) }
                        if spec.Registrations.IsEmpty && spec.ListenerStructs.IsEmpty then
                            logVerbose "No callback patterns detected" verbose
                            None
                        else
                            logVerbose $"Callback patterns: {spec.Registrations.Length} registration(s), {spec.ListenerStructs.Length} listener struct(s)" verbose
                            Some spec
                    else None

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

                let callbackWrapperContent =
                    match layer3Req with
                    | None -> None
                    | Some req ->
                        match req.HasCallbackWrappers, callbackSpec with
                        | true, Some spec ->
                            let bridgeName = $"{nsPrefix}.Bridge"
                            let callbackNs = $"{bridgeName}.Callbacks"
                            let functionToNs =
                                project.Namespaces |> List.collect (fun ns ->
                                    PilotAnalyzer.filterDeclarationsForNamespace ns declarations
                                    |> List.choose (function
                                        | CppParser.Declaration.Function f -> Some (f.Name, ns.Name)
                                        | _ -> None))
                                |> Map.ofList
                            let resolveModule funcName =
                                functionToNs |> Map.tryFind funcName |> Option.defaultValue nsPrefix
                            let registrationModules =
                                spec.Registrations
                                |> List.map (fun reg -> resolveModule reg.Function)
                                |> List.distinct
                            let callbackOpens =
                                [ $"{nsPrefix}.Types" ] @ registrationModules
                                |> List.distinct
                            CallbackWrapperGenerator.generateTypedWithModules spec declarations callbackNs ctx callbackOpens resolveModule
                        | _ -> None

                let emittedTexts =
                    [ yield "Types.clef", sharedCode
                      for (fileName, _, errorOutput) in errorModulePlan do
                          yield fileName, errorOutput
                      for (ns, _, localCode, _, funcCode, wrapperCode) in namespaceCode do
                          let lastSegment = ns.Name.Split('.') |> Array.last
                          for code in Option.toList localCode do
                              yield Path.Combine(lastSegment, "Types.clef"), code
                          yield Path.Combine(lastSegment, $"{lastSegment}.clef"), funcCode
                          for code in Option.toList wrapperCode do
                              yield Path.Combine(lastSegment, $"{lastSegment}Api.clef"), code
                      for (path, protoCode) in typedProtocolContents do
                          yield Path.GetRelativePath(outputDir, path), protoCode
                      for code in Option.toList signalCode do
                          yield Path.Combine("Bridge", "Signals.clef"), code
                      for code in Option.toList callbackWrapperContent do
                          let libLastSegment = nsPrefix.Split('.') |> Array.last
                          yield Path.Combine("..", $"{libLastSegment}Bridge", "Callbacks.clef"), code ]
                verifyEmittedClefPurity emittedTexts |> Result.defaultWith Diagnostics.raiseRefusal

                // ── Static archive artifact (#204) ──────────────────────────
                // A declared [library.artifact] is inspected, and every extern symbol the
                // library's emitted bindings (shared types, error modules, C functions, C++ mangled
                // free functions, C++ class constructors/destructors/methods/shims, wrappers, mapped
                // returns, typed protocol externs, signal bridges, L2 callback wrappers) bind is
                // resolved to the archive member defining it, before any output write:
                // a refusal (FS9224 to FS9230) leaves prior output as it is. The artifact changes
                // no .clef file and nothing in the .fidproj; its only output is the link provenance
                // record written beside the .fidproj.
                let externAttributePattern =
                    System.Text.RegularExpressions.Regex(
                        @"^\s*\[<FidelityExtern\(""(?<lib>(?:\\.|[^""\\])*)"",\s*""(?<sym>(?:\\.|[^""\\])*)""\)>\]\s*$",
                        System.Text.RegularExpressions.RegexOptions.Multiline)
                let linkProvenance =
                    project.Library.Artifact |> Option.map (fun artifact ->
                        let artifactPath =
                            if Path.IsPathRooted artifact.Path then artifact.Path
                            else Path.GetFullPath(Path.Combine(projectDir, artifact.Path))
                        let requiredSymbols =
                            emittedTexts
                            |> List.collect (fun (_, code) ->
                                externAttributePattern.Matches(code)
                                |> Seq.cast<System.Text.RegularExpressions.Match>
                                |> Seq.choose (fun m ->
                                    if m.Groups["lib"].Value = project.Library.Name then Some m.Groups["sym"].Value
                                    else None)
                                |> Seq.toList)
                            |> List.distinct
                            |> List.sort
                        if requiredSymbols.IsEmpty then
                            Diagnostics.diagnostic Diagnostics.ArchiveMissingSymbol
                                $"Static archive contract for library '{project.Library.Name}' has no bound extern symbols in the selected Clef surface."
                                (Diagnostics.Located.Unknown "the emitted Clef surface binds no extern symbols for the declared library artifact")
                            |> Diagnostics.withPilotKey "library.artifact" None
                            |> Diagnostics.refuse
                        let provenance =
                            ArchiveInspection.inspect artifactPath (Some artifact.Sha256)
                            |> Result.defaultWith Diagnostics.raiseRefusal
                        let resolvedBindings =
                            ArchiveInspection.verifyRequiredSymbols provenance requiredSymbols
                            |> Result.defaultWith Diagnostics.raiseRefusal
                        ArchiveInspection.renderLinkProvenanceToml project.Library.Name provenance resolvedBindings [])

                // ── Shared Types.clef (root level) ──────────────────────────
                let sharedTypesPath = Path.Combine(outputDir, "Types.clef")
                Directory.CreateDirectory(outputDir) |> ignore
                File.WriteAllText(sharedTypesPath, sharedCode)
                logVerbose $"Shared types module: {sharedTypesPath} ({classification.SharedTypes.Count} types)" verbose

                // ── Error module ────────────────────────────────────────────
                let errorModuleFiles =
                    errorModulePlan
                    |> List.map (fun (fileName, label, output) ->
                        let errorPath = Path.Combine(outputDir, fileName)
                        File.WriteAllText(errorPath, output)
                        logVerbose $"{label}: {errorPath}" verbose
                        errorPath)

                // ── BAREWire descriptors ─────────────────────────────────────
                // Every struct's StructDescriptor is emitted inside its layout module in the
                // Types module (wiki 14_Binding_Generation_Gaps §2); there is no separate Descriptors file to write.

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

                // The link provenance record of a declared static archive (#204):
                // <Package>.link-provenance.toml beside <Package>.fidproj.
                let linkProvenanceFiles =
                    match linkProvenance with
                    | Some contents ->
                        let provenancePath = Path.ChangeExtension(fidprojFile, ".link-provenance.toml")
                        File.WriteAllText(provenancePath, contents)
                        logVerbose $"Link provenance: {provenancePath}" verbose
                        [provenancePath]
                    | None -> []

                // ── Layer 2 Marshaling Bridge package ─────────────────────
                let layer3Files =
                    match layer3Req with
                    | None -> []
                    | Some req ->
                        let bridgeName = $"{nsPrefix}.Bridge"
                        let libLastSegment = nsPrefix.Split('.') |> Array.last
                        let bridgeDir = Path.Combine(Path.GetDirectoryName(outputDir), $"{libLastSegment}Bridge")
                        Directory.CreateDirectory(bridgeDir) |> ignore

                        let fidprojDir = Path.GetDirectoryName fidprojFile

                        // Callback wrappers file
                        let callbackFiles =
                            match callbackWrapperContent with
                            | Some output ->
                                let callbackPath = Path.Combine(bridgeDir, "Callbacks.clef")
                                File.WriteAllText(callbackPath, output)
                                logVerbose $"  L2 marshaling: Callbacks.clef" verbose
                                [callbackPath]
                            | None -> []

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
                            // The binding project always exists: generateFidproj returns its path (#195).
                            let libDep = Path.GetRelativePath(fidprojDir, fidprojFile).Replace('\\', '/')
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
                    allFiles @ [fidprojFile] @ layer3Files @ linkProvenanceFiles

                Ok {
                    OutputFiles = allOutputFiles
                    DeclarationCount = declarations.Length
                    Advisories = advisories
                    UnemittedMacros = FidelityCodeGenerator.unemittedMacros declarations
                }

    /// The pilot key whose input causes each refusal code that pilot input causes (#184): a
    /// malformed define is a `[library] defines` entry, a record without a value type is
    /// missing from `[options] value_structs`, and a GIR-named C type comes from the
    /// `[library] introspection` files. A static archive refusal (FS9224 to FS9230, #204) comes
    /// from the `[library.artifact]` table.
    let pilotKeyOf (code: string) : string option =
        match code with
        | Diagnostics.DefineMalformed -> Some "library.defines"
        | Diagnostics.RecordWithoutValueType -> Some "options.value_structs"
        | Diagnostics.GirTypeUndeclared | Diagnostics.GirTypeDivergent -> Some "library.introspection"
        | Diagnostics.ArchiveMalformed | Diagnostics.ArchiveDigestMismatch | Diagnostics.ArchiveThinUnsupported
        | Diagnostics.ArchiveMemberFormatUnsupported | Diagnostics.ArchiveMissingSymbol
        | Diagnostics.ArchiveDuplicateStrongSymbol | Diagnostics.ArchiveBitcodeToolUnavailable -> Some "library.artifact"
        | _ -> None

    /// A project generation's failure: an FS91xx refusal raised inside generation keeps its
    /// diagnostics, any other failure keeps its text, both after "Binding generation failed: ";
    /// a failure the pilot checks report directly keeps its text as it is. Each diagnostic a
    /// pilot key causes carries the pilot file and that key; a diagnostic generation keyed
    /// (#195) gets the pilot file bound here.
    let private projectFailure (projectPath: string) (run: unit -> Result<GenerationResult, Diagnostics.Refusal>) =
        let failure =
            try run ()
            with
            | :? Diagnostics.RefusalException as refused -> Error (Diagnostics.prefixed "Binding generation failed: " refused.Refusal)
            | ex -> Error (Diagnostics.uncoded $"Binding generation failed: {ex.Message}")
        let pilotFile = Path.GetFullPath projectPath
        failure |> Result.mapError (Diagnostics.mapDiagnostics (fun d ->
            match d.Pilot, pilotKeyOf d.Code with
            | None, Some key -> Diagnostics.withPilot { PilotFile = pilotFile; Key = key; Parameter = None } d
            | Some pilot, _ when pilot.PilotFile = "" -> Diagnostics.withPilot { pilot with PilotFile = pilotFile } d
            | _ -> d))

    /// Generate bindings from a `.pilot.toml` project. A failure carries the structured
    /// diagnostics of its FS91xx refusals (#184), with the exact text `generateFromProject`
    /// reports as its `Text`: the public surface an editor reads refusals from.
    let generateFromProjectDiagnosed projectPath verbose generateWrappers dataModel : Result<GenerationResult, Diagnostics.Refusal> =
        projectFailure projectPath (fun () -> generateFromProjectCore (TypeMapper.tryScalar dataModel) projectPath verbose generateWrappers dataModel)

    /// Surface-contract failures are generation diagnostics, not uncaught tooling exceptions.
    let generateFromProject projectPath verbose generateWrappers dataModel =
        generateFromProjectDiagnosed projectPath verbose generateWrappers dataModel |> Result.mapError _.Text

    /// `generateFromProjectDiagnosed` with the profile rows clang's alias measurements are
    /// checked against (FS9118) supplied, everything else unchanged.
    let generateFromProjectCheckingAliasesAgainstDiagnosed (aliasRows: string -> TypeMapper.AbiRepr option) projectPath verbose generateWrappers dataModel =
        projectFailure projectPath (fun () -> generateFromProjectCore aliasRows projectPath verbose generateWrappers dataModel)

    /// `generateFromProject` with the profile rows clang's alias measurements are checked
    /// against (FS9118) supplied, everything else unchanged. On an LP64 host every integer row
    /// agrees with clang, so a contradiction is shown end to end only against a deliberately
    /// wrong row.
    let generateFromProjectCheckingAliasesAgainst (aliasRows: string -> TypeMapper.AbiRepr option) projectPath verbose generateWrappers dataModel =
        generateFromProjectCheckingAliasesAgainstDiagnosed aliasRows projectPath verbose generateWrappers dataModel |> Result.mapError _.Text
