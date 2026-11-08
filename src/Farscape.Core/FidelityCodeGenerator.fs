namespace Farscape.Core

open CodeAST
open ActivePatterns
open PilotTypes
open Types

/// Generates F# source in the Platform.Bindings pattern for Fidelity/Firefly consumption.
///
/// Output is a single .fs file with:
///   module Platform.Bindings.{Library}.{Category}
///   [<FidelityExtern("library", "symbol")>]
///   let functionName (param1: type1) (param2: type2) : returnType =
///       NativeDefault.zeroed ()
///
/// [<FidelityExtern>] carries library name + symbol through the PSG so Alex can emit
/// MLIR with fidelity.binding_strategy and fidelity.library_name attributes.
/// No DllImport, no Marshal, no BCL dependencies.
///
/// Widths (Dimensional_Range_Design.md, "Rulings for CS-12", the Farscape leg): a signature spells
/// only `int`, `float`, `bool`, `unit`, `CHandle<'T>` and `FnPtr<'F>`; the width of every
/// parameter and return is the ABI representation carried by the `Expr<FunctionDescriptor>`
/// quotation emitted beside each extern (platform-bindings.md, Layer 2), and a C struct is a
/// layout module plus a BAREWire `StructDescriptor` (wiki 14_Binding_Generation_Gaps §2), never a record.
///
/// Architecture:
///   Catamorphism (DeclarationAlgebra) → FsDecl list → CodeRenderer.render
///   Active patterns for type classification and macro filtering.
///   Zero StringBuilder. Zero Regex. Zero mutable state.
module FidelityCodeGenerator =

    // =========================================================================
    // Typedef Resolution (catamorphism + pure recursive chain resolution)
    // =========================================================================

    /// Pure recursive typedef chain resolution.
    /// Follows chains: __off_t → __OFF_T_TYPE → long int, resolving to fixed point.
    let rec private resolveTypedefChains (maxDepth: int) (m: Map<string, string>) : Map<string, string> =
        if maxDepth = 0 then m
        else
            let resolved =
                m |> Map.map (fun _ underlyingType ->
                    match Map.tryFind underlyingType m with
                    | Some deeper -> deeper
                    | None -> underlyingType)
            if resolved = m then m
            else resolveTypedefChains (maxDepth - 1) resolved

    /// Build a typedef resolution map from parsed declarations.
    /// Uses catamorphism (DeclarationAlgebra.typedefAlgebra) for extraction,
    /// then pure recursive resolution for chain following.
    let buildTypedefMap (declarations: CppParser.Declaration list) : Map<string, string> =
        DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra declarations
        |> List.choose id
        |> Map.ofList
        |> resolveTypedefChains 10

    /// Resolve a C type through the typedef map.
    let resolveType (typedefMap: Map<string, string>) (baseType: string) : string =
        match Map.tryFind baseType typedefMap with
        | Some resolved -> resolved
        | None -> baseType

    // =========================================================================
    // Opaque Handle Detection (pre-pass over typedefs)
    // =========================================================================

    /// Detect opaque handle typedefs from the declaration list.
    /// Returns the set of typedef names that are opaque handles (pointer to undefined struct).
    let detectOpaqueHandles (declarations: CppParser.Declaration list) : Set<string> =
        let knownStructNames =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.definedStructNameAlgebra declarations
            |> List.choose id
            |> Set.ofList
        DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra declarations
        |> List.choose id
        |> List.filter (fun (_, underlyingType) ->
            let td : CppParser.TypedefInfo =
                { Name = ""; UnderlyingType = underlyingType; Documentation = None; Attributes = [] }
            ActivePatterns.isOpaqueHandleTypedef knownStructNames td)
        |> List.map fst
        |> Set.ofList

    /// The type facts one translation unit itself declares: its typedef chains, its named enums
    /// with the representation clang measured in that unit, and its opaque handle typedefs.
    /// A declaration resolves through its own unit's facts; nothing is borrowed from another unit.
    type UnitFacts = {
        Typedefs: Map<string, string>
        Enums: Map<string, CppParser.EnumDecl>
        OpaqueHandles: Set<string>
        /// The unit's typedefs as declared, with the attributes clang attaches to each (#181):
        /// a by-value type reaching an attributed one is admitted only on clang's measurement.
        TypedefInfos: Map<string, CppParser.TypedefInfo>
    }

    /// The unit's typedefs as declared, first declaration of a name winning.
    let typedefInfosOf (declarations: CppParser.Declaration list) : Map<string, CppParser.TypedefInfo> =
        (Map.empty, declarations) ||> List.fold (fun acc decl ->
            match decl with
            | CppParser.Declaration.Typedef t when not (acc.ContainsKey t.Name) -> acc.Add(t.Name, t)
            | _ -> acc)

    /// The facts a unit's own declaration list declares (first declaration of an enum name wins,
    /// as within one translation unit a tag has one definition).
    let unitFactsOf (declarations: CppParser.Declaration list) : UnitFacts =
        { Typedefs = buildTypedefMap declarations
          Enums =
            (Map.empty, declarations) ||> List.fold (fun acc decl ->
                match decl with
                | CppParser.Declaration.Enum e when e.Name <> "" && not (acc.ContainsKey e.Name) -> acc.Add(e.Name, e)
                | _ -> acc)
          OpaqueHandles = detectOpaqueHandles declarations
          TypedefInfos = typedefInfosOf declarations }

    /// A C pointer typedef supplies a nominal marker, never a record exposing its address.
    /// Values of the typedef cross through CHandle<marker>; only the boundary constructs them.
    let generateOpaqueHandleDecls (handleNames: Set<string>) : FsDecl list =
        handleNames
        |> Set.toList
        |> List.sort
        |> List.collect (fun name -> [ OpaqueMarker name; BlankLine ])

    // =========================================================================
    // C type resolution: one resolution, two projections (Clef spelling, ABI representation)
    // =========================================================================

    /// A C type as the generator resolved it, once, for both the Clef signature and the descriptor.
    type ResolvedCType =
        /// A scalar with a representation in TypeMapper's one-kind table.
        | Scalar of TypeMapper.AbiRepr
        /// A data pointer: `CHandle<pointee>` in Clef, a Pointer at the ABI.
        | DataPointer of pointee: FsType
        /// A function pointer: `FnPtr<signature>` in Clef, a Pointer at the ABI.
        | FunctionPointer of parameters: FsType list * ret: FsType
        /// An opaque pointer typedef, spelled CHandle<its nominal marker>, a Pointer at the ABI.
        | OpaqueHandle of string
        /// A C enum, spelled by its name; an integer of its underlying representation at the ABI.
        | CEnum of name: string * TypeMapper.AbiRepr
        /// A named type the generator cannot represent (a struct by value, a union): no ABI claim.
        | Unresolved of string

    let private nullabilityQualifiers = Set.ofList ["_Nonnull"; "_Nullable"; "_Null_unspecified"]

    let private cTypeFragments (cType: string) =
        let fragments, word =
            cType |> Seq.fold (fun (fragments, word) c ->
                if System.Char.IsLetterOrDigit c || c = '_' then fragments, word + string c
                elif word = "" then string c :: fragments, ""
                else string c :: word :: fragments, "") ([], "")
        (if word = "" then fragments else word :: fragments) |> List.rev

    let private stripNullability (cType: string) =
        cTypeFragments cType
        |> List.filter (fun token -> not (nullabilityQualifiers.Contains token))
        |> String.concat ""
        |> fun text -> text.Replace("(* )", "(*)").Replace("(** )", "(**)")

    /// Only the qualifier of the outer pointer proves admission of the carried address.
    let private outerNullability qualifier (cType: string) =
        // A function-pointer declarator owns only the qualifiers inside (* ...).
        // A qualifier on a callback argument must not prove the entry slot nonnull.
        let callbackStar = cType.IndexOf("(*")
        let outer =
            if callbackStar >= 0 then
                let closing = cType.IndexOf(')', callbackStar)
                if closing < 0 then "" else cType.Substring(callbackStar + 2, closing - callbackStar - 2)
            else
                let lastStar = cType.LastIndexOf('*')
                if lastStar < 0 then cType else cType.Substring(lastStar + 1)
        cTypeFragments outer |> List.contains qualifier

    let private nullabilityIn (typedefMap: Map<string, string>) qualifier (cType: string) =
        let rec resolve seen typ =
            if outerNullability qualifier typ then true
            elif typ.Contains('*') || Set.contains typ seen then false
            else
                Map.tryFind (TypeMapper.cleanTypeName (stripNullability typ)) typedefMap
                |> Option.exists (resolve (Set.add typ seen))
        resolve Set.empty cType

    /// The return and parameter type strings of a C function-pointer type "R (*)(A, B)".
    let private splitFunctionPointerPrototype (cType: string) : (string * string list * bool) option =
        match cType.IndexOf("(*") with
        | -1 -> None
        | i ->
            let closing = cType.IndexOf(')', i)
            let ret = cType.Substring(0, i).Trim()
            let declarator = if closing < 0 then "*" else cType.Substring(i + 2, closing - i - 2)
            let rest = if closing < 0 then "" else cType.Substring(closing + 1).Trim()
            if (stripNullability declarator).Trim() = ""
               && rest.StartsWith("(") && rest.EndsWith(")") then
                let inner = rest.Substring(1, rest.Length - 2)
                // Split at depth zero: a parameter may itself be a function pointer
                let parts, last, _ =
                    inner
                    |> Seq.fold (fun (parts, current: string, depth) c ->
                        match c with
                        | '(' -> parts, current + string c, depth + 1
                        | ')' -> parts, current + string c, depth - 1
                        | ',' when depth = 0 -> parts @ [current.Trim()], "", depth
                        | _ -> parts, current + string c, depth) ([], "", 0)
                let all = parts @ [last.Trim()] |> List.filter (fun p -> p <> "" && p <> "void")
                Some (ret, all, inner.Trim() = "void")
            else None

    let private splitFunctionPointer cType =
        splitFunctionPointerPrototype cType |> Option.map (fun (ret, args, _) -> ret, args)

    /// The ABI representation of an enum, by provenance; no width is ever inferred.
    /// - A C/C++ enum (`ClangMeasured`) has the width and signedness clang measured in its
    ///   declaring unit (`CppParser.measureEnums`), which must agree with the facts the parser
    ///   extracted: a fixed underlying type's family and width, and every enumerator's value.
    /// - A C/C++ enum clang did not measure (`ClangUnmeasured`) is refused, whatever underlying
    ///   type spelling it carries: a spelling is not a measurement.
    /// - A protocol enum (`ProtocolWire`) has the wire type its protocol's referencing arguments
    ///   declare, at stratum Declared; its values must fit it. One without such a declaration
    ///   (`ProtocolUndeclared`) is refused.
    /// `declaredScalar` resolves a fixed underlying spelling under the declaration's unit.
    let enumRepr (model: PlatformABI) (declaredScalar: string -> TypeMapper.AbiRepr option) (e: CppParser.EnumDecl) : TypeMapper.AbiRepr =
        let describe signed bits = $"""{(if signed then "signed" else "unsigned")} {bits}-bit"""
        let requireValuesFit (m: CppParser.EnumRepresentation) code source =
            let lower, upper =
                if m.IsSigned then
                    (if m.Bits >= 64 then System.Int64.MinValue else -(1L <<< (m.Bits - 1))),
                    (if m.Bits >= 64 then System.Int64.MaxValue else (1L <<< (m.Bits - 1)) - 1L)
                else 0L, (if m.Bits >= 64 then System.Int64.MaxValue else (1L <<< m.Bits) - 1L)
            match e.Values |> List.tryFind (fun v -> v.Value < lower || v.Value > upper) with
            | Some v ->
                Diagnostics.refuse (Diagnostics.diagnostic code $"enumerator '{v.Name}' of enum '{e.Name}' has value {v.Value}, which {source} {describe m.IsSigned m.Bits} representation cannot hold." e.Location.Location)
            | None -> ()
        let integer (m: CppParser.EnumRepresentation) = if m.IsSigned then TypeMapper.Signed else TypeMapper.Unsigned
        match e.Representation with
        | CppParser.ClangUnmeasured ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.EnumUnmeasured $"enum '{e.Name}' has no clang measurement of its representation in its declaring unit; its width cannot be inferred." e.Location.Location)
        | CppParser.ProtocolUndeclared reason ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.ProtocolEnumUndeclared $"protocol enum '{e.Name}' has no declared wire type: {reason}." e.Location.Location)
        | CppParser.ProtocolWire (m, _evidence) ->
            requireValuesFit m Diagnostics.ProtocolEnumValueConflict "its protocol's declared"
            { Family = integer m
              Bits = m.Bits
              Stratum = TypeMapper.Declared
              // `evidence` stays on the declaration; the spelling names the wire type it fixed.
              CType = $"""enum {e.Name} : {(if m.IsSigned then "int" else "uint")}{m.Bits}_t""" }
        | CppParser.ClangMeasured m ->
            match e.UnderlyingType with
            | None -> ()
            | Some underlying ->
                match declaredScalar underlying with
                | Some r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned || r.Family = TypeMapper.Bool ->
                    if r.Bits <> m.Bits || (r.Family = TypeMapper.Signed) <> m.IsSigned then
                        Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.EnumReprConflict $"enum '{e.Name}' declares underlying type '{underlying}' ({describe (r.Family = TypeMapper.Signed) r.Bits}), but clang measured {describe m.IsSigned m.Bits}." e.Location.Location)
                | _ -> Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.EnumUnderlyingUnrepresented $"enum '{e.Name}' declares underlying type '{underlying}', which has no integer representation under {model}." e.Location.Location)
            requireValuesFit m Diagnostics.EnumValueConflict "clang's measured"
            { Family = integer m
              Bits = m.Bits
              Stratum = TypeMapper.Measured
              CType = $"enum {e.Name}" }

    let private describeRepr (r: TypeMapper.AbiRepr) =
        let family =
            match r.Family with
            | TypeMapper.Signed -> "signed"
            | TypeMapper.Unsigned -> "unsigned"
            | TypeMapper.Float -> "floating"
            | TypeMapper.Pointer -> "pointer"
            | TypeMapper.Bool -> "boolean"
            | TypeMapper.Void -> "void"
        $"{family} {r.Bits}-bit"

    /// The exact refusal when a unit's typedef chain and the profile row name different
    /// representations; `unit` names the declaring header when the caller knows it.
    let typedefProfileConflict (model: PlatformABI) (unit: string option) (name: string) (underlying: string)
                               (declared: TypeMapper.AbiRepr option) (profile: TypeMapper.AbiRepr) =
        let unitRepr = declared |> Option.map describeRepr |> Option.defaultValue "no scalar representation"
        let by = match unit with Some header -> $"'{header}'" | None -> "its translation unit"
        // The parse keeps typedefs without clang locations, so the unit is the known source.
        let source =
            match unit with
            | Some header -> Diagnostics.inFile header $"Farscape's parse keeps no clang location for typedef '{name}'"
            | None -> Diagnostics.Located.Unknown $"the resolver sees typedef '{name}' without its declaring unit"
        Diagnostics.diagnostic Diagnostics.TypedefProfileConflict
            $"typedef '{name}' is declared by {by} as '{underlying}' ({unitRepr}), but the {model} profile row for '{name}' is {describeRepr profile}; the unit's declaration and the profile must agree."
            source

    /// A binding entry's pilot key, or one key of it, as a refusal names it (#195):
    /// `options.bindings[<name>]`, `options.bindings[<name>].<key>`.
    let bindingKey (name: string) (key: string) =
        if key = "" then $"options.bindings[{name}]" else $"options.bindings[{name}].{key}"

    /// Refuse a callback or projection contract of `func` (#195): the code, the message, the
    /// function's clang location, and the pilot key and native parameter index whose input the
    /// refusal names (None when header evidence alone causes it).
    let refuseContract (func: CppParser.FunctionDecl) (code: string) (pilot: (string * int option) option) (message: string) : 'T =
        let d = Diagnostics.diagnostic code message func.Location.Location
        Diagnostics.refuse (match pilot with Some (key, parameter) -> Diagnostics.withPilotKey key parameter d | None -> d)

    /// Where type resolution meets a callback value it refuses, the declaration using the type is
    /// unknown to it: the refusal says so (FS9145, FS9146).
    let private unlocatedCallbackValue (typ: string) =
        Diagnostics.Located.Unknown $"type resolution sees callback type '{typ}' without the declaration that uses it"

    /// Run `f` for one site of a declaration: a callback-value refusal type resolution raised
    /// without the declaration that uses the type (FS9145, FS9146) is given `source` and `pilot`.
    let relocateCallbackValue (source: Diagnostics.Located<Diagnostics.SourceLocation>) (pilot: (string * int option) option) (f: unit -> 'T) : 'T =
        try f ()
        with :? Diagnostics.RefusalException as refused ->
            refused.Refusal
            |> Diagnostics.mapDiagnostics (fun d ->
                match d.Source with
                | Diagnostics.Located.Unknown _ when d.Code = Diagnostics.NonnullContradiction || d.Code = Diagnostics.NullableCallbackUnadmitted ->
                    let d = { d with Source = source }
                    match pilot, d.Pilot with
                    | Some (key, parameter), None -> Diagnostics.withPilotKey key parameter d
                    | _ -> d
                | _ -> d)
            |> Diagnostics.raiseRefusal

    /// The profile row of a scalar spelling, checked against the unit's own typedef chain for
    /// that name: the row stands when the chain's family and bits agree with it (or the unit
    /// does not declare the name); otherwise the name, its unit spelling, the unit's
    /// representation and the profile row are returned for the refusal. `resolve` resolves a
    /// spelling under the same unit typedef map.
    let private checkProfileRow (model: PlatformABI) (typedefMap: Map<string, string>)
                                (resolve: string -> ResolvedCType) (baseType: string)
        : Result<TypeMapper.AbiRepr option, string * string * TypeMapper.AbiRepr option * TypeMapper.AbiRepr> =
        match TypeMapper.tryScalar model baseType with
        | None -> Ok None
        | Some profile ->
            let name = TypeMapper.cleanTypeName baseType
            match Map.tryFind name typedefMap with
            | Some underlying when TypeMapper.cleanTypeName underlying <> name ->
                let declared =
                    match resolve underlying with
                    | Scalar r | CEnum (_, r) -> Some r
                    | DataPointer _ | FunctionPointer _ | OpaqueHandle _ -> Some (TypeMapper.pointerRepr model underlying)
                    | Unresolved _ -> None
                match declared with
                | Some r when r.Family = profile.Family && r.Bits = profile.Bits -> Ok (Some profile)
                | declared -> Error (name, underlying, declared, profile)
            | _ -> Ok (Some profile)

    /// Resolve a C type string once. Opaque handles keep their names; delegates and function
    /// pointers become FnPtr signatures with nullable-by-default native data values; data
    /// pointers become CHandle of their pointee; a scalar spelling the unit declares as a typedef
    /// resolves through the unit's typedef chain first and must agree with the profile's one-kind
    /// table row (refused otherwise); other scalars come from that table, and names the table
    /// lacks through the typedef map; enums carry their underlying representation.
    let rec resolveCType
        (typedefMap: Map<string, string>) (model: PlatformABI)
        (opaqueHandles: Set<string>) (delegates: Map<string, CppParser.DelegateDecl>)
        (enums: Map<string, CppParser.EnumDecl>)
        (cType: string) : ResolvedCType =
        let recur = resolveCType typedefMap model opaqueHandles delegates enums
        let originalType = cType
        let cType = stripNullability cType
        let cleaned = TypeMapper.cleanTypeName cType
        let functionPointer (ret: string) (parameters: string list) =
            if nullabilityIn typedefMap "_Nullable" originalType then
                Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.NullableCallbackUnadmitted $"Nullable function-pointer contract '{originalType}' requires a supported boundary adapter; it cannot be represented as a nonnull native entry." (unlocatedCallbackValue originalType))
            let nativeValueType typ =
                let resolved = recur typ
                let nonnull = nullabilityIn typedefMap "_Nonnull" typ
                let nullable = nullabilityIn typedefMap "_Nullable" typ
                if nonnull && nullable then
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.NonnullContradiction $"Callback value '{typ}' has contradictory native nullability evidence." (unlocatedCallbackValue typ))
                match resolved with
                | DataPointer _ | OpaqueHandle _ when not nonnull -> Generic("option", clefTypeOf resolved)
                | Scalar r when r.Family = TypeMapper.Pointer && not nonnull -> Generic("option", clefTypeOf resolved)
                | FunctionPointer _ when not nonnull ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.NullableCallbackUnadmitted $"Nested native function-pointer value '{typ}' requires explicit nonnull evidence; nullable callback values require a supported boundary adapter." (unlocatedCallbackValue typ))
                | _ -> clefTypeOf resolved
            FunctionPointer (parameters |> List.map nativeValueType, nativeValueType ret)
        // A profile scalar name the unit itself declares as a typedef resolves through the unit's
        // chain first; the profile row stands only when the chain agrees with it (family and bits).
        let tableScalar (baseType: string) : TypeMapper.AbiRepr option =
            match checkProfileRow model typedefMap recur baseType with
            | Ok row -> row
            | Error (name, underlying, declared, profile) ->
                Diagnostics.refuse (typedefProfileConflict model None name underlying declared profile)
        // A pointer's pointee: a scalar's spelling, a named type, or what a typedef stands for
        let pointee (baseType: string) : FsType =
            match tableScalar baseType with
            | Some r -> Named (TypeMapper.clefSpelling r.Family)
            | None ->
                let name = TypeMapper.cleanTypeName baseType
                if opaqueHandles.Contains name && not (baseType.Trim().StartsWith("struct ")) then
                    Generic("CHandle", Named name)
                else
                    match Map.tryFind name typedefMap with
                    | Some underlying when TypeMapper.cleanTypeName underlying <> name
                                           && not (opaqueHandles.Contains name) && not (enums.ContainsKey name) ->
                        match recur underlying with
                        | Scalar r -> Named (TypeMapper.clefSpelling r.Family)
                        | DataPointer _ | FunctionPointer _ as resolved -> clefTypeOf resolved
                        | _ -> Named name
                    | _ -> Named name
        let scalarOrNamed (baseType: string) : ResolvedCType =
            match tableScalar baseType with
            | Some r -> Scalar r
            | None ->
                let name = TypeMapper.cleanTypeName baseType
                match Map.tryFind name enums with
                | Some e ->
                    let declaredScalar underlying = match recur underlying with Scalar r -> Some r | _ -> None
                    CEnum (name, enumRepr model declaredScalar e)
                | None ->
                    match Map.tryFind name typedefMap with
                    | Some underlying when TypeMapper.cleanTypeName underlying <> name ->
                        match recur underlying with
                        | Unresolved _ -> Unresolved name
                        | CEnum (_, r) -> CEnum (name, r)
                        | resolved -> resolved
                    | _ -> Unresolved name
        if not (cType.Contains("*") || cType.Contains("&")) && opaqueHandles.Contains cleaned then OpaqueHandle cleaned
        else
        match Map.tryFind cleaned delegates with
        | Some d -> functionPointer d.ReturnType (d.Parameters |> List.map snd)
        | None ->
        match splitFunctionPointer originalType with
        | Some (ret, parameters) -> functionPointer ret parameters
        | None when cType.Contains("(**)") -> DataPointer (Generic("CHandle", Named "unit"))
        | None ->
        match cType with
        | ParsedCType info when info.PointerDepth > 0 ->
            let inner = pointee info.BaseType
            // Qualifier removal may leave whitespace between stars. The parser's
            // prefix count must not collapse an annotated multi-level data pointer.
            let pointerDepth = cType |> Seq.filter ((=) '*') |> Seq.length
            DataPointer (List.fold (fun t _ -> Generic("CHandle", t)) inner [ 2 .. pointerDepth ])
        | ParsedCType info -> scalarOrNamed info.BaseType
        | _ when cType.Contains("*") -> DataPointer (Named "unit")
        | _ -> scalarOrNamed cleaned

    /// The Clef spelling of a resolved C type: the one kind per family, never a width.
    and clefTypeOf (resolved: ResolvedCType) : FsType =
        match resolved with
        | Scalar r -> Named (TypeMapper.clefSpelling r.Family)
        | DataPointer pointee -> Generic("CHandle", pointee)
        | FunctionPointer (parameters, ret) -> Generic("FnPtr", FunctionType(parameters, ret))
        | CEnum (_, r) -> Named (TypeMapper.clefSpelling r.Family)
        | OpaqueHandle name -> Generic("CHandle", Named name)
        | Unresolved name -> Named name

    /// The ABI representation of a resolved C type, when the generator can claim one.
    let abiReprOf (model: PlatformABI) (cType: string) (resolved: ResolvedCType) : TypeMapper.AbiRepr option =
        match resolved with
        | Scalar r -> Some r
        | CEnum (_, r) -> Some r
        | DataPointer _ | FunctionPointer _ | OpaqueHandle _ -> Some (TypeMapper.pointerRepr model cType)
        | Unresolved _ -> None

    /// The profile conflicts of one translation unit: every profile scalar name the unit itself
    /// declares as a typedef, resolved through the unit's typedef map, must agree with its
    /// profile row. Returns the refusals, naming the unit, for names whose chain disagrees.
    let unitProfileConflicts (model: PlatformABI) (unit: string) (typedefMap: Map<string, string>)
                             (enums: Map<string, CppParser.EnumDecl>) (declaredNames: string list) : Diagnostics.Diagnostic list =
        let resolve = resolveCType typedefMap model Set.empty Map.empty enums
        declaredNames |> List.distinct |> List.choose (fun name ->
            match checkProfileRow model typedefMap resolve name with
            | Ok _ -> None
            | Error (name, underlying, declared, profile) -> Some (typedefProfileConflict model (Some unit) name underlying declared profile))

    /// Map a C type string to its Clef spelling.
    /// Used by FidelityCodeGenerator (Layer 1) and WrapperCodeGenerator (Layer 2).
    let mapCTypeToFidelityType (typedefMap: Map<string, string>) (model: PlatformABI) (opaqueHandles: Set<string>) (delegates: Map<string, CppParser.DelegateDecl>) (cType: string) : FsType =
        resolveCType typedefMap model opaqueHandles delegates Map.empty cType |> clefTypeOf

    // =========================================================================
    // Generation Context (pre-computed from full declaration list)
    // =========================================================================

    /// Pre-computed resolution context built once from the full declaration list.
    /// Passed to generateModule so each sub-file gets correct type resolution
    /// even when it only contains a subset of declarations.
    type GenerationContext = {
        TypedefMap: Map<string, string>
        /// The facts (typedefs, measured enums, opaque handles) of the translation unit that
        /// declares each declaration, keyed by declaration kind ("struct", "function",
        /// "typedef") and name. A declaration's type spellings are facts of its declaring unit,
        /// which may declare a name differently from the first unit the merged maps keep.
        /// Empty for a single unit, whose facts are the merged ones.
        DeclarationUnits: Map<string * string, UnitFacts>
        /// Every pilot unit declaring each C type name (typedef, record or enum), with that
        /// unit's own facts, for names a non-C source (GIR) supplies without a declaring unit of
        /// its own. Empty for a single unit, whose facts are the merged ones.
        TypeNameUnits: Map<string, (string * UnitFacts) list>
        OpaqueHandles: Set<string>
        /// Delegate types (callback function pointers) by name: FnPtr of their signature in Clef.
        Delegates: Map<string, CppParser.DelegateDecl>
        /// Named enums: an integer of the enum's underlying representation at the ABI.
        Enums: Map<string, CppParser.EnumDecl>
        /// Defined structs by name: a nested struct field's extent comes from its own layout.
        Structs: Map<string, CppParser.StructDecl>
        DataModel: PlatformABI
        StructLayouts: Map<string, CppParser.StructLayoutInfo>
        /// Nonnull annotations from pilot TOML (None = all pointers nullable by default)
        NonnullAnnotations: NonnullAnnotations option
        /// C++ class lookup map for sret return type analysis (empty for C-only libraries)
        KnownClasses: Map<string, CppParser.ClassDecl>
        /// Retired source profile, retained only so legacy configurations receive a precise error.
        NativePointerSurface: bool
        Bindings: Map<string, BindingProjection>
        ValueStructs: Set<string>
        /// Declared count sources of flexible array members, by struct (BAREWire #11).
        FlexibleArrays: Map<string, FlexibleArrayCount>
        PhantomMarkers: Set<string>
        /// The typedefs of the context's unit as declared, with their attributes (#181).
        TypedefInfos: Map<string, CppParser.TypedefInfo>
        /// Clang's measurements of the by-value parameters and result of the function whose unit
        /// the context is in (`inFunctionUnit`) that reach an attributed typedef (#181).
        ByValueTypedefs: CppParser.ByValueTypedef list
    }

    /// A context holding one unit's facts in place of the merged ones.
    let withUnitFacts (facts: UnitFacts) (ctx: GenerationContext) : GenerationContext =
        { ctx with TypedefMap = facts.Typedefs; Enums = facts.Enums; OpaqueHandles = facts.OpaqueHandles; TypedefInfos = facts.TypedefInfos }

    /// The context of one declaration: its declaring unit's facts (typedefs, measured enums,
    /// opaque handles) when the pilot recorded them, the merged facts otherwise (a single
    /// unit, or a declaration no C unit declares). Every resolver call made for that
    /// declaration (signature, parameters, return, callbacks, fields, descriptors) goes
    /// through this.
    let inUnitOf (kind: string) (name: string) (ctx: GenerationContext) : GenerationContext =
        match ctx.DeclarationUnits.TryFind (kind, name) with
        | Some facts -> withUnitFacts facts ctx
        | None -> ctx

    /// The context of a function declaration: its declaring unit's typedef map, and clang's
    /// measurements of its by-value types that reach an attributed typedef (#181).
    let inFunctionUnit (ctx: GenerationContext) (func: CppParser.FunctionDecl) : GenerationContext =
        { inUnitOf "function" func.Name ctx with ByValueTypedefs = func.ByValueTypedefs }

    /// The context in which a C type named by a non-C source (a GIR c:type) resolves: the unit
    /// that declares its base name, with that unit's own facts (typedefs, measured enums, opaque
    /// handles) and nothing borrowed from
    /// another unit. A name several units declare must resolve identically in each; a name no
    /// unit declares stands only as a profile scalar no unit redeclares; otherwise refused.
    /// A single-unit pilot's merged map is its unit's map.
    let declaringUnitOf (ctx: GenerationContext) (where: string) (cType: string) : GenerationContext =
        if ctx.TypeNameUnits.IsEmpty then ctx
        else
        let name = TypeMapper.cleanTypeName (stripNullability cType)
        let resolveUnder (facts: UnitFacts) = resolveCType facts.Typedefs ctx.DataModel facts.OpaqueHandles ctx.Delegates facts.Enums name
        let describe resolved =
            match resolved with
            | Unresolved _ -> "no representation"
            | resolved ->
                match abiReprOf ctx.DataModel name resolved with
                | Some r -> describeRepr r
                | None -> "no representation"
        match ctx.TypeNameUnits.TryFind name with
        | Some ((_, first) :: _ as units) ->
            let resolutions = units |> List.map (fun (header, facts) -> header, resolveUnder facts)
            match resolutions |> List.map snd |> List.distinct with
            | [ _ ] -> withUnitFacts first ctx
            | _ ->
                let described = resolutions |> List.map (fun (header, resolved) -> $"'{header}' ({describe resolved})") |> String.concat ", "
                let unlocated = "Farscape's parse keeps no clang location for the declarations a GIR-named type resolves through"
                Diagnostics.diagnostic Diagnostics.GirTypeDivergent
                    $"{where}: C type '{name}' is declared differently by {described}; one declaration per pilot is required."
                    (Diagnostics.inFile (fst (List.head resolutions)) unlocated)
                |> Diagnostics.withRelated (resolutions |> List.tail |> List.map (fun (header, _) -> { Diagnostics.SourceLocation.Path = header; Diagnostics.SourceLocation.Position = Diagnostics.Located.Unknown unlocated }))
                |> Diagnostics.refuse
        | _ when (TypeMapper.tryScalar ctx.DataModel name).IsSome -> withUnitFacts { Typedefs = Map.empty; Enums = Map.empty; OpaqueHandles = Set.empty; TypedefInfos = Map.empty } ctx
        | _ ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.GirTypeUndeclared
                                    $"{where}: C type '{name}' is declared by no pilot unit; a GIR spelling cannot supply its representation."
                                    (Diagnostics.Located.Unknown $"no pilot header declares C type '{name}'; the pilot's introspection data names it"))

    /// Resolve a C type under the context.
    /// The record a by-value C type names, through its unit's typedef chain: a C struct or
    /// union the pilot defines (its fields are declared), cited by the name of its layout
    /// module, or a C++ class. A tag spelling (`struct X`) is a record name; a bare name is a
    /// typedef when the unit declares one, else a record name. None when the type reaches
    /// neither: a record only forward-declared, or defined outside the pilot's declarations.
    let byValueRecordOf (ctx: GenerationContext) (cType: string) : (string * string) option =
        let rec follow (visited: Set<string>) (spelling: string) =
            if spelling.Contains '*' || spelling.Contains '(' || spelling.Contains '&' || spelling.Contains '[' then None
            else
            let name = TypeMapper.cleanTypeName spelling
            let bare = spelling.Replace("const ", "").Replace("volatile ", "").Trim()
            let record () =
                match ctx.Structs.TryFind name with
                | Some s when CppParser.isDefinedRecord s -> Some (name, if s.IsUnion then "union" else "struct")
                | _ when ctx.KnownClasses.ContainsKey name -> Some (name, "C++ class")
                | _ -> None
            if bare.StartsWith "struct " || bare.StartsWith "union " || bare.StartsWith "class " then record ()
            else
                match ctx.TypedefMap.TryFind name with
                | Some underlying when not (visited.Contains name) -> follow (visited.Add name) underlying
                | _ -> record ()
        follow Set.empty cType

    /// A C type under the context. A by-value spelling that names a typedef of a defined C
    /// struct or union (`typedef struct __pthread_internal_list {…} __pthread_list_t`, #172)
    /// resolves to that record, by the name of its layout module, so fields, signatures and
    /// descriptors all see the record the typedef stands for.
    let private resolveIn (ctx: GenerationContext) (cType: string) : ResolvedCType =
        match resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums cType with
        | Unresolved name ->
            match byValueRecordOf ctx cType with
            | Some (record, ("struct" | "union")) -> Unresolved record
            | _ -> Unresolved name
        | resolved -> resolved

    // =========================================================================
    // Declaration Generation Helpers (produce FsDecl, not strings)
    // =========================================================================

    /// Format XML doc declarations: description (from header comment) + C signature.
    let formatDocDecls (func: CppParser.FunctionDecl) : FsDecl list =
        let paramStr =
            func.Parameters
            |> List.map (fun (name, typ) -> $"{typ} {name}")
            |> String.concat ", "
        let cSignature = $"C signature: {func.ReturnType} {func.Name}({paramStr})"
        match func.Documentation with
        | Some doc ->
            [ XmlDoc doc
              XmlDoc ""
              XmlDoc cSignature ]
        | None ->
            [ XmlDoc cSignature ]

    /// Wrap an FsType in option<> for nullable pointer parameters.
    let wrapOption (ty: FsType) : FsType = Generic("option", ty)

    /// Check if a C type string represents a data pointer (not a function pointer).
    /// Qualified function-pointer declarators are classified independently;
    /// nullable entries need a supported boundary adapter.
    let isCDataPointer (cType: string) : bool =
        cType.Contains("*") && (splitFunctionPointer cType).IsNone && not (cType.Contains("(**)"))

    /// A data pointer under the context: spelled with `*`, or a typedef standing for one (`gpointer`).
    let private isDataPointerIn (ctx: GenerationContext) (cType: string) : bool =
        match resolveIn ctx cType with
        | DataPointer _ | OpaqueHandle _ -> true
        | Scalar r when r.Family = TypeMapper.Pointer -> true
        | _ -> false

    let private outerNullabilityIn (ctx: GenerationContext) qualifier (cType: string) =
        nullabilityIn ctx.TypedefMap qualifier cType

    /// A single-level pointer to `char` under the context, through typedefs (`const gchar *`).
    let private isCharPointerIn (ctx: GenerationContext) (cType: string) : bool =
        match stripNullability cType with
        | ParsedCType info when info.PointerDepth = 1 ->
            let name = TypeMapper.cleanTypeName info.BaseType
            TypeMapper.cleanTypeName (resolveType ctx.TypedefMap name) = "char"
        | _ -> false

    /// Const applies to the immediate referenced object, not a deeper pointee:
    /// const T** still exposes a writable pointer cell; T* const* does not.
    let private referencePassing (projection: BindingProjection option) index (cType: string) =
        let writable = projection |> Option.exists(fun p -> List.contains index p.ReferenceParameters)
        let explicitReadOnly = projection |> Option.exists(fun p -> List.contains index p.ReadOnlyReferenceParameters)
        let constPointee =
            let star = cType.LastIndexOf('*')
            if star < 0 then false else
            let pointee = cType.Substring(0,star)
            let topLevel = pointee.Substring(pointee.LastIndexOf('*') + 1)
            topLevel.Split([|' ';'\t';'\r';'\n'|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.contains "const"
        let reference = writable || explicitReadOnly
        reference, (reference && (explicitReadOnly || constPointee))

    let private quoteString (s: string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// How a descriptor claims a parameter's or result's type: its representation, a by-value
    /// enum without a clang measurement (FS9103), a by-value record cited by name, or a type
    /// that resolves through no typedef, record or profile entry (FS9115). A descriptor never
    /// carries an unknown width.
    type DescriptorClaim =
        | Represented of TypeMapper.AbiRepr
        | UnmeasuredEnum
        | RecordByValue of name: string * kind: string
        | ResolvesNowhere

    let descriptorClaimOf (ctx: GenerationContext) (cType: string) : DescriptorClaim =
        let resolved = resolveIn ctx cType
        match abiReprOf ctx.DataModel cType resolved with
        | Some r -> Represented r
        | None ->
            // A by-value enum never crosses with an unknown width: measured or refused.
            let isEnum (text: string) = text.Replace("const ", "").Replace("volatile ", "").Trim().StartsWith "enum "
            let chain = ctx.TypedefMap |> Map.tryFind (TypeMapper.cleanTypeName cType) |> Option.defaultValue cType
            if not (cType.Contains '*') && (isEnum cType || (isEnum chain && not (chain.Contains '*'))) then UnmeasuredEnum
            else
                match byValueRecordOf ctx cType with
                | Some (name, kind) -> RecordByValue (name, kind)
                | None -> ResolvesNowhere

    /// How a by-value type reaching an attributed typedef stands at its descriptor (#181): None
    /// when it reaches none; `Ok note` when clang measures it, in its declaring unit, at the size
    /// and alignment of the type its descriptor claims (`CppParser.unattributedSpelling`), the
    /// note recording the typedefs, their attributes and clang's measurement; `Error reason`
    /// when clang has no measurement of it there (a callback entry's types are not measured) or
    /// measures it otherwise. A descriptor has no slot for a typedef's attribute, so only clang's
    /// agreement admits the claim.
    let typedefAttributeEvidence (ctx: GenerationContext) (cType: string) : Result<string, string> option =
        match CppParser.typedefAttributesOf ctx.TypedefInfos cType with
        | [] -> None
        | reached ->
            let carried =
                reached
                |> List.map (fun (typedef, a) -> $"typedef '{typedef}' carrying {a.Kind}")
                |> List.distinct
                |> String.concat ", "
            let unattributed = CppParser.unattributedSpelling ctx.TypedefInfos cType
            let extent (e: CppParser.TypeExtent) = $"{e.Size} bytes, alignment {e.Alignment}"
            match ctx.ByValueTypedefs |> List.tryFind (fun m -> m.Spelling = cType) with
            | None ->
                Some (Error $"reaches {carried}, and clang has no measurement of '{cType}' in its declaring unit")
            | Some { Measured = Error reason } ->
                Some (Error $"reaches {carried}, and {reason}")
            | Some { Measured = Ok (spelled, plain); Unattributed = claimed } when spelled <> plain ->
                Some (Error $"reaches {carried}, and clang measures '{cType}' at {extent spelled} but '{claimed}', the type its descriptor claims, at {extent plain}")
            | Some { Measured = Ok (spelled, _) } ->
                Some (Ok $"type reaches {carried}; clang measures it at {extent spelled}, as '{unattributed}'")

    /// The `TypeRef` source and the provenance note a descriptor claim gives a type, before the
    /// typedef attributes it reaches are examined (`abiClaim`).
    let private claimOf (ctx: GenerationContext) (cType: string) : string * string =
        match descriptorClaimOf ctx cType with
        | Represented r -> DescriptorGenerator.typeRefSource r, DescriptorGenerator.widthProvenance ctx.DataModel r
        | UnmeasuredEnum ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.EnumUnmeasured
                                    $"by-value enum type '{cType}' has no clang measurement in its declaring unit; a descriptor cannot carry an unknown width."
                                    (Diagnostics.Located.Unknown $"the descriptor guard sees type '{cType}' without the declaration that uses it"))
        | RecordByValue (name, "C++ class") -> $"Named {quoteString name}", $"record: C++ class {name}, no width of its own"
        | RecordByValue (name, kind) -> $"Named {quoteString name}", $"record: {kind} {name}, laid out by {name}.Descriptor"
        | ResolvesNowhere ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.ByValueTypeUnresolved
                                    $"by-value type '{cType}' resolves through no typedef, record or profile entry of its declaring unit; a descriptor cannot carry an unknown width."
                                    (Diagnostics.Located.Unknown $"the descriptor guard sees type '{cType}' without the declaration that uses it"))

    /// The `TypeRef` source and the provenance note of one parameter or return. The last
    /// guard before emission: the pilot is refused before any binding is written
    /// (`BindingGenerator.requireRepresentedDescriptorTypes`) for the same claims, there
    /// naming the declaration and its unit.
    let private abiClaim (ctx: GenerationContext) (cType: string) : string * string =
        let typeRef, provenance = claimOf ctx cType
        // A typedef's attribute is admitted only on clang's measurement (#181).
        match typedefAttributeEvidence ctx cType with
        | None -> typeRef, provenance
        | Some (Ok note) -> typeRef, provenance + "; " + note
        | Some (Error reason) ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.ByValueTypedefAttribute
                                    $"by-value type '{cType}' {reason}; a descriptor carries no attribute of a typedef."
                                    (Diagnostics.Located.Unknown $"the descriptor guard sees type '{cType}' without the declaration that uses it"))

    /// The Layer 2 `Expr<FunctionDescriptor>` quotation beside an extern (platform-bindings.md,
    /// "Binding Libraries"): the C name, each parameter's and the return's ABI representation
    /// (family and bits under the profile) and passing, the calling convention, and ownership.
    /// Each width fact carries its stratum as a trailing comment; the spec's record has no
    /// provenance field.
    let private generateDescriptorDecls (ctx: GenerationContext) (func: CppParser.FunctionDecl) : FsDecl list =
        let ctx = inFunctionUnit ctx func
        let parameterInfos =
            func.Parameters
            |> List.mapi (fun index (name, cType) ->
                let reference, readOnly = referencePassing (ctx.Bindings |> Map.tryFind func.Name) index cType
                let element = ctx.Bindings |> Map.tryFind func.Name |> Option.bind (fun b -> List.tryItem index b.ReferenceElements) |> Option.filter ((<>) "")
                let represented = if reference then element |> Option.defaultWith (fun () -> cType.Substring(0, cType.LastIndexOf('*')).Trim()) else cType
                let typeRef, provenance = abiClaim ctx represented
                let clefName = (cleanParamName name).Replace("``", "")
                Commented(
                    RecordConstruction [
                        "Name", Literal (quoteString clefName)
                        "Type", Literal typeRef
                        "PassBy", Identifier (if readOnly then "ReadOnlyReference" elif reference then "Reference" else "Value") ],
                    provenance))
        let returnRef, returnProvenance = abiClaim ctx func.ReturnType
        let ownership, ownershipNote =
            match ctx.Bindings |> Map.tryFind func.Name |> Option.bind (fun binding -> binding.Ownership) with
            | Some transfer -> string transfer, $"Ownership {transfer} is declared by the pilot as an external execution hypothesis."
            | None -> "Undeclared", "No ownership hypothesis is declared; Undeclared grants no lifetime or transfer authority."
        [ XmlDoc $"ABI descriptor of `{func.Name}` (Layer 2): each parameter's and the return's representation under {ctx.DataModel}, every width naming its stratum. {ownershipNote}"
          ValueBinding($"{func.Name}Descriptor", Generic("Expr", Named "FunctionDescriptor"),
              Quoted(RecordBlock [
                  "CName", Literal (quoteString (ctx.Bindings |> Map.tryFind func.Name |> Option.map (fun b -> b.Symbol) |> Option.defaultValue func.Name))
                  "Parameters", ArrayBlock parameterInfos
                  "ReturnType", Commented(Literal returnRef, returnProvenance)
                  "CallingConvention", Identifier "CDecl"
                  "OwnershipTransfer", Identifier ownership ])) ]


    /// Escape punctuation reversibly: short record names must remain distinct across modules,
    /// because the compiler can fall back from qualified lookup to a short record name.
    let private callbackIdentifier (name: string) =
        name |> Seq.map (fun c -> if System.Char.IsLetterOrDigit c then string c else $"_{int c:X4}_") |> String.concat ""

    /// Recover a callback's declared native types without erasing nested nullability
    /// or reconstructing scalar widths from its Clef function type.
    let resolveCallbackNativeSignature (ctx: GenerationContext) cType =
        let rec signature seen cType =
            if Set.contains cType seen then None else
            let seen = Set.add cType seen
            let name = TypeMapper.cleanTypeName (stripNullability cType)
            match Map.tryFind name ctx.Delegates with
            | Some d -> Some (d.ReturnType, d.Parameters)
            | None ->
                match splitFunctionPointer cType with
                | Some (ret, args) -> Some (ret, List.mapi (fun i t -> $"arg{i}", t) args)
                | None -> Map.tryFind name ctx.TypedefMap |> Option.bind (signature seen)
        signature Set.empty cType

    /// Anonymous entries belong to their parameter site.
    let private callbackEntries (ctx: GenerationContext) (namespace': string) (func: CppParser.FunctionDecl) =
        let ctx = inFunctionUnit ctx func
        func.Parameters |> List.mapi (fun index (name, cType) ->
          relocateCallbackValue func.Location.Location (Some (bindingKey func.Name "", Some index)) (fun () ->
            resolveCallbackNativeSignature ctx cType |> Option.map (fun (ret, args) ->
                let identity = if cType.Contains "(*)" then func.Name + "::" + name else cType
                let recordName = "Callback_" + callbackIdentifier (namespace' + "::" + identity) + "Entry"
                let parameterInfos = args |> List.map (fun (name, cType) ->
                    let typeRef, provenance = abiClaim ctx cType
                    Commented(RecordConstruction [
                        "Name", Literal (quoteString ((cleanParamName name).Replace("``", "")))
                        "Type", Literal typeRef
                        "PassBy", Identifier "Value" ], provenance))
                let returnRef, returnProvenance = abiClaim ctx ret
                let entryType = resolveIn ctx cType |> clefTypeOf
                let declarations = [
                    RecordType(recordName, ["Invoke", entryType],
                        Some "Layer 2 native entry contract. Construct its field with FnPtr.ofFunction on a closed module function.", [])
                    ValueBinding(recordName + "Descriptor", Generic("Expr", Named "CallbackDescriptor"),
                        Quoted(RecordBlock [
                            "Record", Literal (quoteString recordName)
                            "Field", Literal "\"Invoke\""
                            "Signature", RecordBlock [
                                "CName", Literal (quoteString (namespace' + "::" + identity))
                                "Parameters", ArrayBlock parameterInfos
                                "ReturnType", Commented(Literal returnRef, returnProvenance)
                                "CallingConvention", Identifier "CDecl"
                                "OwnershipTransfer", Identifier "Undeclared" ] ])) ]
                index, recordName, declarations))) |> List.choose id

    /// Every type a function's descriptors claim, with the site that claims it: each parameter
    /// (the pointee a reference projection passes) and the result of its `FunctionDescriptor`,
    /// and each argument and result of the `CallbackDescriptor` of every callback parameter.
    /// These are all the `abiClaim` sites a claim can fail at: a callback invocation entry
    /// admits only ordered integers and void before it claims.
    let descriptorTypeSites (ctx: GenerationContext) (func: CppParser.FunctionDecl) : (string * string) list =
        let ctx = inFunctionUnit ctx func
        let parameters =
            func.Parameters |> List.mapi (fun index (name, cType) ->
                let reference, _ = referencePassing (ctx.Bindings |> Map.tryFind func.Name) index cType
                let element = ctx.Bindings |> Map.tryFind func.Name |> Option.bind (fun b -> List.tryItem index b.ReferenceElements) |> Option.filter ((<>) "")
                let represented = if reference then element |> Option.defaultWith (fun () -> cType.Substring(0, cType.LastIndexOf('*')).Trim()) else cType
                $"parameter '{name}'", represented)
        let callbacks =
            func.Parameters |> List.collect (fun (name, cType) ->
                match resolveCallbackNativeSignature ctx cType with
                | Some (ret, args) ->
                    (args |> List.map (fun (arg, t) -> $"argument '{arg}' of callback parameter '{name}'", t))
                    @ [ $"result of callback parameter '{name}'", ret ]
                | None -> [])
        parameters @ [ "result", func.ReturnType ] @ callbacks

    /// Render a supplied result promise against the exact emitted declaration.
    /// ABI widths and placeholder bodies provide no behavioral contract.
    let private resultRangeDecls (ctx: GenerationContext) namespace' (func: CppParser.FunctionDecl) nativeName hasAdapter =
        let ctx = inFunctionUnit ctx func
        match Map.tryFind func.Name ctx.Bindings |> Option.bind _.ResultRange with
        | None -> []
        | Some range ->
            let key = bindingKey func.Name "result_range"
            let refuse code parameter reason = refuseContract func code (Some (key, parameter)) $"{func.Name} result_range: {reason}"
            for name, value in ["identity", range.Identity; "version", range.Version; "source", range.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty None $"requires nonempty {name}"
            let projection = ctx.Bindings[func.Name]
            if hasAdapter || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) then
                refuse Diagnostics.ContractRequiresUntranslated None "requires an ordinary untranslated native binding"
            let integer cType =
                match resolveIn ctx cType with
                | Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned -> true
                | _ -> false
            if not (integer func.ReturnType) then refuse Diagnostics.ResultRangeNotInteger None "requires a scalar integer native result"
            let endpoint = function
                | ResultRangeBound.Constant value -> Literal (if value < 0 then $"Constant ({value})" else $"Constant {value}")
                | ResultRangeBound.Argument index ->
                    if index < 0 || index >= func.Parameters.Length then refuse Diagnostics.ContractParameterOutOfRange (Some index) $"argument {index} is outside native arity {func.Parameters.Length}"
                    let reference, _ = referencePassing (Some projection) index (snd func.Parameters[index])
                    if reference || not (integer (snd func.Parameters[index])) then refuse Diagnostics.ResultRangeNotInteger (Some index) $"argument {index} requires a scalar integer Value parameter"
                    Literal $"Argument {index}"
            let lower, upper = endpoint range.Lower, endpoint range.Upper
            match range.Lower, range.Upper with
            | ResultRangeBound.Constant lo, ResultRangeBound.Constant hi when lo > hi -> refuse Diagnostics.ResultRangeBoundsContradictory None "constant bounds are contradictory"
            | _ -> ()
            [ XmlDoc "Result range is a supplied external execution hypothesis; it grants no ABI, loading or lifetime authority."
              ValueBinding(nativeName + "ResultRange", Generic("Expr", Named "ResultRangeDescriptor"),
                  Quoted(RecordBlock [
                      "Binding", Literal (quoteString (namespace' + "." + nativeName))
                      "Role", Identifier "ReturnValue"
                      "Hypothesis", RecordBlock [
                          "Identity", Literal (quoteString range.Identity); "Version", Literal (quoteString range.Version); "Source", Literal (quoteString range.Source) ]
                      "Lower", lower; "Upper", upper ])) ]

    /// Declare receiving-site activation separately from the listener record and its ABI.
    /// The existing native signature owner supplies every formal, including unused ones.
    let private callbackInvocationDecls (ctx: GenerationContext) namespace' (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list>) (func: CppParser.FunctionDecl) nativeName =
        let ctx = inFunctionUnit ctx func
        let contracts = ctx.Bindings |> Map.tryFind func.Name |> Option.map _.CallbackInvocations |> Option.defaultValue []
        let key = bindingKey func.Name "callback_invocations"
        let refuseAt code parameter reason = refuseContract func code (Some (key, parameter)) $"{func.Name} callback_invocations: {reason}"
        match contracts |> List.countBy _.Parameter |> List.tryFind (fun (_, count) -> count > 1) with
        | Some (duplicate, _) -> refuseAt Diagnostics.ContractDuplicateParameter (Some duplicate) "duplicate parameter indices"
        | None -> ()
        let entries = contracts |> List.map (fun contract ->
            let refuse code reason = refuseAt code (Some contract.Parameter) reason
            for key, value in ["identity", contract.Identity; "version", contract.Version; "source", contract.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty $"requires nonempty {key}"
            match contract.AbsenceHypothesis with
            | Some hypothesis ->
                for key, value in ["identity", hypothesis.Identity; "version", hypothesis.Version; "source", hypothesis.Source] do
                    if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty $"absence_hypothesis requires nonempty {key}"
                if hypothesis.Identity = contract.Identity then
                    refuse Diagnostics.AbsenceIdentityReused "absence_hypothesis identity must differ from activation identity"
                if contract.Scope <> "during_call" then
                    refuse Diagnostics.CallbackScopeFieldConflict "absence_hypothesis requires during_call"
            | None -> ()
            let scopeType, scopeFields =
                match contract.Scope, contract.ExitBinding, contract.SuccessStatus with
                | "during_call", None, None -> "ScopedCallbackDescriptor", []
                | "during_call", _, _ -> refuse Diagnostics.CallbackScopeFieldConflict "during_call forbids exit_binding and success_status"
                | "process_exit", anchor, Some status ->
                    match resolveIn ctx func.ReturnType with
                    | Scalar r when r.Family = TypeMapper.Signed && r.Bits = 32 -> ()
                    | _ -> refuse Diagnostics.ProcessExitSignature "process_exit requires a signed32 registration result"
                    let anchorValue =
                        match anchor with
                        | None -> Identifier "None"
                        | Some selected ->
                            match Map.tryFind selected anchors with
                            | Some (actual, _, _) -> Literal ("Some " + quoteString actual)
                            | None -> refuse Diagnostics.ContractBindingUnselected "exit_binding must name a selected qualified generated declaration"
                    "ProcessExitCallbackDescriptor", ["ExitBinding", anchorValue; "SuccessStatus", Literal (string status)]
                | "process_exit", _, None -> refuse Diagnostics.CallbackScopeFieldConflict "process_exit requires signed32 success_status"
                | _ -> refuse Diagnostics.CallbackScopeUnknown "scope must be during_call or process_exit"
            if contract.Parameter < 0 || contract.Parameter >= func.Parameters.Length then
                refuse Diagnostics.ContractParameterOutOfRange $"parameter {contract.Parameter} is outside native arity {func.Parameters.Length}"
            let projection = ctx.Bindings[func.Name]
            if not projection.ReferenceParameters.IsEmpty || not projection.ReadOnlyReferenceParameters.IsEmpty
               || not projection.StringParameters.IsEmpty || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) then
                refuse Diagnostics.ContractRequiresUntranslated "requires an untranslated native parameter list"
            let name, cType = func.Parameters[contract.Parameter]
            match resolveIn ctx cType with
            | FunctionPointer _ -> ()
            | _ -> refuse Diagnostics.CallbackNotFunctionPointer $"parameter {contract.Parameter} is not a native function pointer"
            let ret, parameters = resolveCallbackNativeSignature ctx cType |> Option.defaultWith (fun () -> refuse Diagnostics.CallbackSignatureUnrecovered $"parameter {contract.Parameter} type '{cType}' resolves to a native function pointer, but no native entry signature is recoverable from it")
            if parameters.IsEmpty then
                let declaredType =
                    Map.tryFind (TypeMapper.cleanTypeName (stripNullability cType)) ctx.TypedefMap
                    |> Option.defaultValue cType
                match splitFunctionPointerPrototype declaredType with
                | Some (_, [], true) -> ()
                | _ -> refuse Diagnostics.CallbackZeroArgumentPrototype "zero-argument native entries require an explicit (void) prototype"
                match resolveIn ctx ret with
                | Scalar r when r.Family = TypeMapper.Void -> ()
                | _ -> refuse Diagnostics.CallbackZeroArgumentPrototype "zero-argument native entries require a void result"
            if contract.Scope = "process_exit" then
                match parameters, resolveIn ctx ret with
                | [], Scalar r when r.Family = TypeMapper.Void -> ()
                | _ -> refuse Diagnostics.ProcessExitSignature "process_exit requires a zero-argument void callback"
            let nativeType isReturn (cType: string) =
                if cType.Contains('&') then refuse Diagnostics.CallbackEntryTypeUnadmitted $"unsupported native entry reference signature type '{cType}'"
                match resolveIn ctx cType with
                | Scalar r when (r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned) && (r.Bits = 32 || r.Bits = 64) -> ()
                | Scalar r when r.Family = TypeMapper.Bool ->
                    refuse Diagnostics.CallbackEntryTypeUnadmitted "Boolean native entry signatures are outside the admitted ordered integer callback profile"
                | Scalar r when isReturn && r.Family = TypeMapper.Void -> ()
                | _ -> refuse Diagnostics.CallbackEntryTypeUnadmitted $"unsupported native entry signature type '{cType}'"
                abiClaim ctx cType
            let infos = parameters |> List.map (fun (name, typ) ->
                let typeRef, provenance = nativeType false typ
                Commented(RecordConstruction [
                    "Name", Literal (quoteString ((cleanParamName name).Replace("``", "")))
                    "Type", Literal typeRef; "PassBy", Identifier "Value" ], provenance))
            let returnRef, returnProvenance = nativeType true ret
            let contractName = nativeName + "Callback" + string contract.Parameter + "InvocationContract"
            let scopeName = nativeName + "Callback" + string contract.Parameter + "Scope"
            let binding = namespace' + "." + nativeName
            contract.Parameter, namespace' + "." + contractName, [
                XmlDoc "Declared foreign activation only: no argument values, invocation count, purity or functionhood are supplied."
                ValueBinding(scopeName, Generic("Expr", Named scopeType), Quoted(RecordBlock ([
                    "BAREWire.Descriptors." + scopeType + ".Binding", Literal (quoteString binding)
                    "Parameter", Literal (quoteString ((cleanParamName name).Replace("``", ""))) ] @ scopeFields)))
                ValueBinding(contractName, Generic("Expr", Named "CallbackInvocationContract"), Quoted(RecordBlock [
                    "Binding", Literal (quoteString binding)
                    "Parameter", Literal (string contract.Parameter)
                    "Signature", RecordBlock [
                        "Parameters", ArrayBlock infos
                        "ReturnType", Commented(Literal returnRef, returnProvenance)
                        "CallingConvention", Identifier "CDecl" ]
                    "Hypothesis", RecordBlock [
                        "Identity", Literal (quoteString contract.Identity)
                        "Version", Literal (quoteString contract.Version)
                        "Source", Literal (quoteString contract.Source) ]
                    "Scope", Literal (quoteString (namespace' + "." + scopeName)) ]))
                yield! contract.AbsenceHypothesis |> Option.map (fun hypothesis ->
                    ValueBinding(nativeName + "Callback" + string contract.Parameter + "AbsenceContract",
                        Generic("Expr", Named "CallbackAbsenceContract"), Quoted(RecordBlock [
                            "BAREWire.Descriptors.CallbackAbsenceContract.Binding", Literal (quoteString binding)
                            "Parameter", Literal (string contract.Parameter)
                            "Hypothesis", RecordBlock [
                                "Identity", Literal (quoteString hypothesis.Identity)
                                "Version", Literal (quoteString hypothesis.Version)
                                "Source", Literal (quoteString hypothesis.Source) ] ]))) |> Option.toList ])
        entries |> List.map (fun (index, binding, _) -> index, binding) |> Map.ofList,
        entries |> List.collect (fun (_, _, declarations) -> declarations)

    /// Generate FsDecl list for a single function binding, followed by its descriptor.
    /// Pointer parameters are nullable (Option<>) by default unless proven non-null via:
    /// 1. Clang NonNullAttr — parameter indices explicitly marked non-null
    /// 2. Pilot TOML [annotations.nonnull] — developer-asserted non-null
    let generateFunctionDecls (ctx: GenerationContext) (libraryName: string) (func: CppParser.FunctionDecl) : FsDecl list =
        // Every resolution for this declaration uses its declaring unit's typedefs.
        let ctx = inFunctionUnit ctx func
        if ctx.NativePointerSurface then
            failwith "experimental_native_pointer_surface cannot emit Clef source: raw addresses and width-named carriers violate the FFI boundary contract."
        let mapType = resolveIn ctx >> clefTypeOf
        let projection = Map.tryFind func.Name ctx.Bindings
        let nonnullAnnotations = ctx.NonnullAnnotations

        // Collect proven-nonnull parameter indices from clang attributes
        let clangNonnull =
            func.Attributes
            |> List.collect (fun a -> if a.Kind = "NonNullAttr" then a.Args else [])
            |> Set.ofList
        // Collect proven-nonnull parameter indices from pilot TOML
        let tomlNonnull =
            nonnullAnnotations
            |> Option.bind (fun a -> Map.tryFind func.Name a.Parameters)
            |> Option.defaultValue []
            |> Set.ofList
        let nonnullIndices = Set.union clangNonnull tomlNonnull

        let refuse code key parameter (reason: string) =
            refuseContract func code (key |> Option.map (fun key -> key, parameter)) $"{func.Name} parameter {parameter.Value}: {reason}"
        let refuseResult code key (reason: string) =
            refuseContract func code (key |> Option.map (fun key -> key, None)) $"{func.Name}: {reason}"
        // The pilot key carrying an index's projection: the first of `keys` whose list holds it.
        let keyHolding idx (keys: (string * (BindingProjection -> int list)) list) =
            keys |> List.tryPick (fun (key, indices) ->
                if projection |> Option.exists (fun b -> List.contains idx (indices b)) then Some (bindingKey func.Name key) else None)

        // Map parameters with nullability awareness
        let parameters =
            func.Parameters
            |> List.mapi (fun idx (name, cType) ->
              relocateCallbackValue func.Location.Location (Some (bindingKey func.Name "", Some idx)) (fun () ->
                let refuse code key reason = refuse code key (Some idx) reason
                let reference, _ = referencePassing projection idx cType
                let element = projection |> Option.bind (fun b -> List.tryItem idx b.ReferenceElements) |> Option.filter ((<>) "")
                let stringParameter = projection |> Option.exists (fun b -> List.contains idx b.StringParameters)
                let callbackNonnull = projection |> Option.exists (fun b -> List.contains idx b.NonnullCallbacks)
                let handle = projection |> Option.bind (fun b -> List.tryItem idx b.ParameterHandles) |> Option.filter ((<>) "")
                let provenNonnull = nonnullIndices.Contains idx || outerNullabilityIn ctx "_Nonnull" cType || callbackNonnull
                // Pilot nonnull evidence names its key; clang's attribute and _Nonnull are header evidence.
                let nonnullKey =
                    if tomlNonnull.Contains idx then Some $"annotations.nonnull.{func.Name}"
                    elif callbackNonnull then Some (bindingKey func.Name "nonnull_callbacks")
                    else None
                if provenNonnull && outerNullabilityIn ctx "_Nullable" cType then
                    refuse Diagnostics.NonnullContradiction nonnullKey "nullable native type conflicts with a nonnull contract"
                if provenNonnull && (projection |> Option.exists (fun binding ->
                    binding.CallbackInvocations |> List.exists (fun contract -> contract.Parameter = idx && contract.AbsenceHypothesis.IsSome))) then
                    refuse Diagnostics.NonnullContradiction (Some (bindingKey func.Name "callback_invocations")) "absence_hypothesis conflicts with a nonnull contract"
                let nullableCallback =
                    match resolveIn ctx cType with
                    | FunctionPointer _ when not provenNonnull ->
                        let contracts = projection |> Option.map _.CallbackInvocations |> Option.defaultValue []
                        if not (contracts |> List.exists (fun contract -> contract.Parameter = idx)) then
                            refuse Diagnostics.NullableCallbackUnadmitted (Some (bindingKey func.Name "")) "a native function-pointer parameter requires explicit nonnull evidence; nullable callback parameters require a supported boundary adapter."
                        if contracts |> List.exists (fun contract -> contract.Scope <> "during_call") then
                            refuse Diagnostics.NullableCallbackScope (Some (bindingKey func.Name "callback_invocations")) "nullable callback parameters require only during_call invocation contracts."
                        // Reuse the complete receiving-contract validator before raw emission.
                        // This profile has no process-exit anchors to resolve.
                        callbackInvocationDecls ctx "" Map.empty func func.Name |> ignore
                        true
                    | _ -> false
                let referenceKey =
                    keyHolding idx [ "reference_parameters", _.ReferenceParameters; "read_only_reference_parameters", _.ReadOnlyReferenceParameters ]
                let fsType =
                    if reference then
                        match cType with
                        | ParsedCType info when info.PointerDepth = 1 ->
                            match resolveIn ctx (element |> Option.defaultValue info.BaseType) with
                            | Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned || r.Family = TypeMapper.Float -> Named (TypeMapper.clefSpelling r.Family + " array")
                            | (OpaqueHandle _ | DataPointer _) as pointee ->
                                Named (CodeRenderer.renderType (wrapOption (clefTypeOf pointee)) + " array")
                            | Scalar r when r.Family = TypeMapper.Pointer ->
                                Named (CodeRenderer.renderType (wrapOption (clefTypeOf (Scalar r))) + " array")
                            | _ -> refuse Diagnostics.ReferenceCellUnsupported referenceKey "Reference requires a scalar or opaque pointer cell"
                        | ParsedCType info when info.PointerDepth = 2 ->
                            let pointerType = cType.Substring(0, cType.LastIndexOf('*')).Trim()
                            let element = resolveIn ctx pointerType |> clefTypeOf |> wrapOption
                            Named (CodeRenderer.renderType element + " array")
                        | _ -> refuse Diagnostics.ReferenceCellUnsupported referenceKey "Reference requires a scalar cell or one opaque pointer cell"
                    elif stringParameter then
                        if isCharPointerIn ctx cType then Named "string"
                        else refuse Diagnostics.StringProjectionNotCharPointer (Some (bindingKey func.Name "string_parameters")) "string projection requires char pointer"
                    elif handle.IsSome then
                        if not (isDataPointerIn ctx cType) then refuse Diagnostics.HandleProjectionNotDataPointer (Some (bindingKey func.Name "parameter_handles")) "handle projection requires a C data pointer"
                        Generic("CHandle", Named handle.Value)
                    elif callbackNonnull then
                        match mapType cType with
                        | Generic("FnPtr", _) as ty -> ty
                        | _ -> refuse Diagnostics.CallbackNotFunctionPointer (Some (bindingKey func.Name "nonnull_callbacks")) "nonnull_callbacks requires a C function pointer"
                    else mapType cType
                let isNullable = nullableCallback || (not reference && isDataPointerIn ctx cType && not provenNonnull)
                let finalType = if isNullable then wrapOption fsType else fsType
                { FsParam.Name = cleanParamName name; Type = finalType }))

        // Return type: nullable unless proven nonnull
        let returnIsPointer = isDataPointerIn ctx func.ReturnType
        let returnNonnull =
            nonnullAnnotations
            |> Option.map (fun a -> a.Returns.Contains func.Name)
            |> Option.defaultValue false
        let hasReturnsNonnullAttr =
            func.Attributes |> List.exists (fun a -> a.Kind = "ReturnsNonNullAttr")
        let provenReturnNonnull = returnNonnull || hasReturnsNonnullAttr || outerNullabilityIn ctx "_Nonnull" func.ReturnType
        let returnsKey = Some "annotations.nonnull.nonnull_returns"
        if provenReturnNonnull && outerNullabilityIn ctx "_Nullable" func.ReturnType then
            refuseResult Diagnostics.NonnullContradiction (if returnNonnull then returnsKey else None) "nullable native result conflicts with a nonnull contract"
        relocateCallbackValue func.Location.Location (if returnNonnull then Some (returnsKey.Value, None) else None) (fun () ->
            match resolveIn ctx func.ReturnType with
            | FunctionPointer _ when not provenReturnNonnull ->
                refuseResult Diagnostics.NullableCallbackUnadmitted returnsKey "a native function-pointer result requires explicit nonnull evidence; nullable callback results require a supported boundary adapter."
            | _ -> ())
        let returnType =
            match projection |> Option.bind (fun b -> b.ReturnHandle) with
            | Some name ->
                if not (isDataPointerIn ctx func.ReturnType) then refuseResult Diagnostics.HandleProjectionNotDataPointer (Some (bindingKey func.Name "return_handle")) "return_handle requires a C data pointer"
                Generic("CHandle", Named name)
            | None -> mapType func.ReturnType
        let finalReturnType =
            if returnIsPointer && not provenReturnNonnull
            then wrapOption returnType
            else returnType

        let symbol = projection |> Option.map (fun b -> b.Symbol) |> Option.defaultValue func.Name
        let constants = projection |> Option.map (fun p -> p.ConstantParameters) |> Option.defaultValue []
        let fixedAt index = constants |> List.tryItem index |> Option.filter ((<>) "")
        let projected = constants |> List.exists ((<>) "")
        let nativeName = if projected then func.Name + "Native" else func.Name
        let nativeFunction = { func with Name = nativeName }
        let descriptorContext =
            match projection with
            | Some p when projected -> { ctx with Bindings = Map.add nativeName { p with Name = nativeName; ConstantParameters = [] } ctx.Bindings }
            | _ -> ctx
        let wrapper =
            if not projected then [] else
            let args = parameters |> List.mapi (fun index p ->
                match fixedAt index with
                | None -> Identifier p.Name
                | Some value ->
                    match System.Int64.TryParse value, p.Type with
                    | (true, _), Named "int" -> Literal value
                    | _ -> refuse Diagnostics.ConstantProjectionInvalid (Some (bindingKey func.Name "constant_parameters")) (Some index) "constant projection requires an integer literal and scalar integer parameter")
            let publicParameters = parameters |> List.indexed |> List.choose (fun(i,p)->if (fixedAt i).IsSome then None else Some p)
            [ LetBinding(func.Name, publicParameters, finalReturnType, FunctionCall("", nativeName, args), []) ]
        let callbackNotes =
            func.Parameters |> List.mapi (fun idx (name, cType) ->
                match resolveIn ctx cType with
                | FunctionPointer _ ->
                    let evidence =
                        [ if clangNonnull.Contains idx then "header nonnull attribute"
                          if tomlNonnull.Contains idx then "pilot annotations.nonnull"
                          if outerNullabilityIn ctx "_Nonnull" cType then "native _Nonnull type"
                          if projection |> Option.exists (fun b -> List.contains idx b.NonnullCallbacks) then "pilot nonnull_callbacks" ]
                        |> String.concat ", "
                    Some (XmlDoc (if evidence = "" then
                                      $"Callback parameter `{name}` is optional; None supplies no callable entry. Activation relies on the declared during_call external hypothesis, not a proof of foreign behavior."
                                  else $"Callback parameter `{name}` nonnull is an external execution hypothesis from {evidence}; it is not a proof of foreign behavior."))
                | _ -> None) |> List.choose id
        formatDocDecls { func with Name = symbol } @ callbackNotes @
        [ LetBinding(nativeName, parameters, finalReturnType, NativeZeroed,
                     [$"FidelityExtern(\"{libraryName}\", \"{symbol}\")"]) ] @
        generateDescriptorDecls descriptorContext nativeFunction @ wrapper

    let private entryParameterReferences entries body =
        if Map.isEmpty entries then body else
        let parameter index = function
            | Commented(RecordConstruction fields, _) when Map.containsKey index entries ->
                let typeRef = Literal ("Entry " + quoteString entries[index])
                Commented(RecordConstruction (fields |> List.map (fun (name, value) -> name, if name = "Type" then typeRef else value)), "Declared native entry signature; physical representation and code residence require independent authority.")
            | value -> value
        match body with
        | Quoted(RecordBlock fields) -> Quoted(RecordBlock (fields |> List.map (function
            | "Parameters", ArrayBlock parameters -> "Parameters", ArrayBlock (parameters |> List.mapi parameter)
            | field -> field)))
        | _ -> invalidOp "A native entry reference requires the owning quoted function descriptor."

    /// Only a real callback/argument adapter needs a separate native declaration.
    /// An unchanged optional entry carrier is already the public binding surface.
    let private nativeDeclarationName (ctx: GenerationContext) (func: CppParser.FunctionDecl) (entries: (int * string * FsDecl list) list) (raw: FsDecl list) =
        let ctx = inFunctionUnit ctx func
        let name, parameters = raw |> List.pick (function LetBinding(name, parameters, _, _, attrs) when not attrs.IsEmpty -> Some(name, parameters) | _ -> None)
        let unchangedOptional =
            not entries.IsEmpty &&
            (ctx.Bindings |> Map.tryFind func.Name |> Option.bind _.Resource |> Option.isNone) &&
            (entries |> List.forall (fun (ordinal, _, _) ->
                match parameters[ordinal].Type with Generic("option", Generic("FnPtr", _)) -> true | _ -> false)) &&
            not (raw |> List.exists (function LetBinding(publicName, _, _, _, []) when publicName = func.Name -> true | _ -> false))
        if not entries.IsEmpty && name = func.Name && not unchangedOptional then name + "Native" else name

    /// Consume the same selected declaration account as callback anchors. Native
    /// representation stays in its original descriptor; this quotation adds a promise.
    let private resourceDecls (ctx: GenerationContext) namespace' (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list>) (func: CppParser.FunctionDecl) nativeName raw =
        let ctx = inFunctionUnit ctx func
        match ctx.Bindings |> Map.tryFind func.Name |> Option.bind _.Resource with
        | None -> []
        | Some resource ->
            let refuse code reason = refuseContract func code (Some (bindingKey func.Name "resource", None)) $"{func.Name} resource: {reason}"
            for key, value in ["identity", resource.Identity; "version", resource.Version; "source", resource.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty $"requires nonempty {key}"
            if resource.ReleaseParameter <> 0 then refuse Diagnostics.ResourceReturnShape "owned_nullable_return requires release_parameter 0"
            let signature declarations =
                declarations |> List.pick (function
                    | LetBinding(_, parameters, result, _, attrs) when not attrs.IsEmpty -> Some (parameters, result)
                    | _ -> None)
            let marker =
                match signature raw |> snd with
                | Generic("option", Generic("CHandle", Named marker)) when isDataPointerIn ctx func.ReturnType -> marker
                | _ -> refuse Diagnostics.ResourceReturnShape "owned_nullable_return requires a native data pointer returned as option<CHandle<marker>>"
            let releaseName, release, releaseRaw =
                anchors |> Map.tryFind resource.ReleaseBinding
                |> Option.defaultWith (fun () -> refuse Diagnostics.ContractBindingUnselected "release_binding must name an exact selected qualified generated declaration")
            let parameters, result = signature releaseRaw
            // The release declaration's return and parameter resolve in its own unit.
            let releaseCtx = inFunctionUnit ctx release
            match release.Parameters, parameters, result, resolveIn releaseCtx release.ReturnType with
            | [_, typ], [parameter], Named "unit", Scalar repr
                when repr.Family = TypeMapper.Void && isDataPointerIn releaseCtx typ
                     && parameter.Type = Generic("CHandle", Named marker) -> ()
            | _ -> refuse Diagnostics.ResourceReleaseShape "release requires unary matching CHandle Value parameter 0 and a raw Void result"
            match ctx.Bindings |> Map.tryFind release.Name with
            | Some projection when not projection.ReferenceParameters.IsEmpty || not projection.ReadOnlyReferenceParameters.IsEmpty
                                   || not projection.StringParameters.IsEmpty
                                   || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) ->
                refuse Diagnostics.ContractRequiresUntranslated "release requires an untranslated unary Value parameter"
            | _ -> ()
            [ XmlDoc "Owned nullable return contract: explicit source cleanup and alias retirement remain required; no spatial or retention authority."
              ValueBinding(nativeName + "ResourceDescriptor", Generic("Expr", Named "ResourceDescriptor"),
                  Quoted(RecordBlock [
                      "Resource", FunctionCall("", "OwnedNullableReturn", [RecordConstruction [
                          "Binding", Literal (quoteString releaseName)
                          "Parameter", Literal (string resource.ReleaseParameter) ]])
                      "Hypothesis", RecordBlock [
                          "Identity", Literal (quoteString resource.Identity)
                          "Version", Literal (quoteString resource.Version)
                          "Source", Literal (quoteString resource.Source) ] ])) ]

    /// Add only the explicit call-bounded byte promise; source owns actual capacity and use.
    let private writableBufferDecls (ctx: GenerationContext) namespace' (func: CppParser.FunctionDecl) nativeName hasAdapter =
        let ctx = inFunctionUnit ctx func
        let projection = ctx.Bindings |> Map.tryFind func.Name
        let contracts = projection |> Option.map _.WritableBuffers |> Option.defaultValue []
        let key = bindingKey func.Name "writable_buffers"
        let refuseAt code parameter reason = refuseContract func code (Some (key, parameter)) $"{func.Name} writable_buffers: {reason}"
        match contracts |> List.countBy _.Parameter |> List.tryFind (fun (_, count) -> count > 1) with
        | Some (duplicate, _) -> refuseAt Diagnostics.ContractDuplicateParameter (Some duplicate) "duplicate parameter indices"
        | None -> ()
        contracts |> List.collect (fun contract ->
            let refuse code reason = refuseAt code (Some contract.Parameter) reason
            for key, value in ["identity", contract.Identity; "version", contract.Version; "source", contract.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty $"requires nonempty {key}"
            let projection = projection.Value
            if hasAdapter || not projection.StringParameters.IsEmpty
               || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) then
                refuse Diagnostics.ContractRequiresUntranslated "requires an untranslated native parameter list"
            let parameter index =
                if index < 0 || index >= func.Parameters.Length then
                    refuseAt Diagnostics.ContractParameterOutOfRange (Some index) $"parameter {index} is outside native arity {func.Parameters.Length}"
                snd func.Parameters[index]
            let buffer, count = parameter contract.Parameter, parameter contract.CountParameter
            if contract.Parameter = contract.CountParameter then refuse Diagnostics.WritableBufferSameParameter "requires distinct buffer and count parameters"
            let reference, readOnly = referencePassing (Some projection) contract.Parameter buffer
            if not reference || readOnly || not (isDataPointerIn ctx buffer) then
                refuse Diagnostics.WritableBufferNotByteReference "buffer parameter requires writable Reference passing"
            match List.tryItem contract.Parameter projection.ReferenceElements |> Option.map (resolveIn ctx) with
            | Some (Scalar element) when element.Family = TypeMapper.Unsigned && element.Bits = 8 -> ()
            | _ -> refuse Diagnostics.WritableBufferNotByteReference "buffer parameter requires an explicit unsigned8 byte reference element contract"
            let countReference, _ = referencePassing (Some projection) contract.CountParameter count
            match resolveIn ctx count with
            | Scalar scalar when not countReference && (scalar.Family = TypeMapper.Signed || scalar.Family = TypeMapper.Unsigned) -> ()
            | _ -> refuseAt Diagnostics.WritableBufferCountNotInteger (Some contract.CountParameter) "count parameter requires scalar integer Value passing"
            [ XmlDoc "Call-bounded writable byte promise: actual capacity, guards, source uses and encoding require independent source evidence."
              ValueBinding(nativeName + "WritableBuffer" + string contract.Parameter + "Descriptor", Generic("Expr", Named "WritableBufferDescriptor"),
                  Quoted(RecordBlock [
                      "Binding", Literal (quoteString (namespace' + "." + nativeName))
                      "Parameter", Literal (string contract.Parameter)
                      "CountParameter", Literal (string contract.CountParameter)
                      "Hypothesis", RecordBlock [
                          "Identity", Literal (quoteString contract.Identity)
                          "Version", Literal (quoteString contract.Version)
                          "Source", Literal (quoteString contract.Source) ] ])) ])

    let private declaredFunctionDecls (ctx: GenerationContext) namespace' anchors (func: CppParser.FunctionDecl) (entries: (int * string * FsDecl list) list) (raw: FsDecl list) =
        let ctx = inFunctionUnit ctx func
        if entries.IsEmpty then
            let nativeName = raw |> List.pick (function LetBinding(name, _, _, _, attrs) when not attrs.IsEmpty -> Some name | _ -> None)
            callbackInvocationDecls ctx namespace' anchors func nativeName |> ignore
            raw @ resultRangeDecls ctx namespace' func nativeName false @ resourceDecls ctx namespace' anchors func nativeName raw @ writableBufferDecls ctx namespace' func nativeName false
        else
        resultRangeDecls ctx namespace' func func.Name true |> ignore
        writableBufferDecls ctx namespace' func func.Name true |> ignore
        let nativeName, nativeParameters, returnType =
            raw |> List.pick (function LetBinding(name, parameters, ret, _, attrs) when not attrs.IsEmpty -> Some (name, parameters, ret) | _ -> None)
        let callbackTypes = entries |> List.choose (fun (i, name, _) ->
            match nativeParameters[i].Type with
            | Generic("option", Generic("FnPtr", _)) -> None
            | _ -> Some (cleanParamName (fst func.Parameters.[i]), name)) |> Map.ofList
        let emittedName = nativeDeclarationName ctx func entries raw
        let needsAdapter = emittedName <> func.Name
        let entryReferences, invocationDeclarations = callbackInvocationDecls ctx namespace' anchors func emittedName
        let publicParameters, callArguments =
            match raw |> List.tryPick (function
                | LetBinding(name, parameters, _, FunctionCall(_, _, arguments), []) when name = func.Name -> Some (parameters, arguments)
                | _ -> None) with
            | Some wrapper -> wrapper
            | None -> nativeParameters, (nativeParameters |> List.map (fun p -> Identifier p.Name))
        let publicParameters = publicParameters |> List.map (fun p ->
            match Map.tryFind p.Name callbackTypes with Some name -> { p with Type = Named name } | None -> p)
        let callArguments = callArguments |> List.map (function
            | Identifier name when callbackTypes.ContainsKey name -> Identifier (name + ".Invoke")
            | arg -> arg)
        let declarations = raw |> List.choose (function
            | LetBinding(name, parameters, ret, body, attrs) when name = nativeName && not attrs.IsEmpty ->
                Some (LetBinding((if needsAdapter then "private " + emittedName else emittedName), parameters, ret, body, attrs))
            | ValueBinding(name, ty, body) when name = nativeName + "Descriptor" -> Some (ValueBinding(emittedName + "Descriptor", ty, entryParameterReferences entryReferences body))
            | LetBinding(name, _, _, _, []) when name = func.Name -> None
            | declaration -> Some declaration)
        let adapter = if needsAdapter then [LetBinding(func.Name, publicParameters, returnType, FunctionCall("", emittedName, callArguments), [])] else []
        declarations @ invocationDeclarations @ resourceDecls ctx namespace' anchors func emittedName raw @ adapter

    let generateDeclaredFunctionDecls (ctx: GenerationContext) libraryName namespace' (func: CppParser.FunctionDecl) =
        let ctx = inFunctionUnit ctx func
        declaredFunctionDecls ctx namespace' Map.empty func (callbackEntries ctx namespace' func) (generateFunctionDecls ctx libraryName func)

    /// Generate FsDecl list for an enum type.
    /// Automatically detects bitmask (flags) enums via value pattern analysis.
    let private generateEnumDecl (e: CppParser.EnumDecl) : FsDecl list =
        let values = e.Values |> List.map (fun v -> (v.Name, v.Value))
        let isFlags = ActivePatterns.isBitmaskEnum values
        [ EnumType(e.Name, values, e.Documentation, isFlags) ]

    /// Generate FsDecl list for a C struct: a layout module of literal offsets plus its
    /// BAREWire StructDescriptor (wiki 14_Binding_Generation_Gaps §2), measured when the pilot measured it.
    let private generateStructDecl (ctx: GenerationContext) (s: CppParser.StructDecl) : FsDecl list =
        // Measured synchronization unions are opaque storage. Their public
        // extent/alignment is measured; overlapping implementation members are not fields
        // consumers may access through a BAREWire struct view.
        let s, layouts =
            match s.IsUnion, Map.tryFind s.Name ctx.StructLayouts with
            | true, Some layout ->
                let size, _ = DescriptorGenerator.validateMeasuredLayout s layout
                let storage : CppParser.FieldDecl =
                    { Name = "Storage"; Type = "unsigned char"; IsConst = false; IsVolatile = false
                      IsArray = true; ArraySize = Some size; IsBitfield = false; BitWidth = None; Attributes = [] }
                // The storage is the union's only member: its unnamed bit-fields (#176) and the
                // typedef attributes its fields reach (#175) are inside the measured extent. A
                // typedef naming it stays, since the record layout dump does not measure it.
                { s with Fields = [storage]; Measured = None; UnnamedBitFields = []
                         TypedefAttributes = s.TypedefAttributes |> List.filter (fun a -> a.Site = CppParser.RecordName) },
                ctx.StructLayouts.Add(s.Name, { layout with FieldOffsetsBits = [0] })
            | _ -> s, ctx.StructLayouts
        // Field spellings resolve through the declaring unit's own typedef chain.
        let unitCtx = inUnitOf "struct" s.Name ctx
        let shape (cType: string) : DescriptorGenerator.FieldShape =
            match resolveIn unitCtx cType with
            | Unresolved name when ctx.Structs.ContainsKey name -> DescriptorGenerator.StructField name
            | Unresolved name -> DescriptorGenerator.UnknownField name
            | resolved ->
                match abiReprOf ctx.DataModel cType resolved with
                | Some r -> DescriptorGenerator.ScalarField r
                | None -> DescriptorGenerator.UnknownField cType
        let valueDecls =
            if ctx.ValueStructs.Contains s.Name then
                if s.IsUnion || s.Fields |> List.exists (fun field -> field.IsArray || field.IsBitfield) then
                    failwith $"Value struct '{s.Name}' requires a plain record with scalar or pointer fields"
                // A value struct's Clef record is laid out from its field types, which hold
                // neither an unnamed bit-field (#176) nor a typedef's attribute (#175).
                match s.UnnamedBitFields with
                | b :: _ ->
                    failwith $"Value struct '{s.Name}' carries {CppParser.describeUnnamedBitField b}, which its Clef record of named fields cannot represent; a plain record with scalar or pointer fields is required."
                | [] -> ()
                match s.TypedefAttributes |> List.tryPick (fun a -> match a.Site with CppParser.FieldType field -> Some (field, a) | CppParser.RecordName -> None) with
                | Some (field, a) ->
                    failwith $"Value struct '{s.Name}' field '{field}' has a type reaching typedef '{a.Typedef}', which carries attribute '{a.Attribute.Kind}' that its Clef record's field type cannot represent; a plain record with scalar or pointer fields is required."
                | None -> ()
                // A value struct's callback slot is refused at the record's clang location and
                // its `options.value_structs` entry (#195).
                let refuseField code message =
                    Diagnostics.diagnostic code message s.Location.Location
                    |> Diagnostics.withPilotKey "options.value_structs" None
                    |> Diagnostics.refuse
                let fieldType (field: CppParser.FieldDecl) =
                  relocateCallbackValue s.Location.Location (Some ("options.value_structs", None)) (fun () ->
                    let resolved = resolveIn unitCtx field.Type
                    let nonnull = outerNullabilityIn unitCtx "_Nonnull" field.Type
                    if nonnull && outerNullabilityIn unitCtx "_Nullable" field.Type then
                        refuseField Diagnostics.NonnullContradiction $"Value struct '{s.Name}' field '{field.Name}' has contradictory native nullability evidence."
                    match resolved with
                    | DataPointer _ | OpaqueHandle _ when not nonnull -> wrapOption (clefTypeOf resolved)
                    | Scalar r when r.Family = TypeMapper.Pointer && not nonnull -> wrapOption (clefTypeOf resolved)
                    | FunctionPointer _ when not nonnull ->
                        refuseField Diagnostics.NullableCallbackUnadmitted $"Value struct '{s.Name}' field '{field.Name}' requires explicit nonnull callback-slot evidence; nullable callback fields require a supported boundary adapter."
                    | _ -> clefTypeOf resolved)
                [ RecordType(s.Name, s.Fields |> List.map (fun field -> cleanParamName field.Name, fieldType field), s.Documentation, []) ]
            else []
        valueDecls @ DescriptorGenerator.structDecls
            { Structs = ctx.Structs; Layouts = layouts; Model = ctx.DataModel; Shape = shape; FlexibleArrays = ctx.FlexibleArrays }
            s

    /// Generate FsDecl list for a macro constant (numeric values only).
    /// Uses CompilerBuiltin/InternalMacro/UserMacro active patterns for classification
    /// and IntegerLiteral active pattern for numeric parsing.
    let private generateMacroDeclIfNumeric (m: CppParser.MacroDecl) : FsDecl list =
        match m.Name with
        | CompilerBuiltin | InternalMacro | PredefinedMacro -> []
        | UserMacro ->
            // Also filter names starting with underscore (matches existing broad filter)
            if m.Name.StartsWith("_") then []
            else
                match m.Kind with
                | CppParser.SimpleValue v ->
                    match v with
                    | IntegerLiteral n -> [ LiteralBinding(m.Name, string n) ]
                    | _ -> []
                | CppParser.Expression v ->
                    let trimmed = v.Trim().TrimStart('(').TrimEnd(')')
                    match trimmed with
                    | IntegerLiteral n -> [ LiteralBinding(m.Name, string n) ]
                    | _ -> []
                | _ -> []

    // =========================================================================
    // Catamorphism-Based Generation
    // =========================================================================

    /// Declaration group: produced by catamorphism, consumed by assembler.
    type private DeclGroup =
        | GEnum of FsDecl list
        | GStruct of FsDecl list
        | GFunc of CppParser.FunctionDecl
        | GMacro of FsDecl list
        | GNone

    /// Generation algebra: maps each Declaration variant to a DeclGroup.
    /// This is the SINGLE traversal of declarations for code generation.
    /// All context (typedef map, ABI model, opaque handles, struct layouts, C++ class map) is captured in the closure.
    let private generationAlgebra
        (ctx: GenerationContext) (libraryName: string)
        : DeclarationAlgebra.DeclarationAlgebra<DeclGroup> = {
        OnEnum = fun e -> if e.Name <> "" then GEnum (generateEnumDecl e) else GNone
        OnStruct = fun s ->
            if s.Name = "" then GNone
            // A forward declaration has no layout; a record of only unnamed bit-fields (#183) is
            // defined, and laid out by clang's measurement.
            elif not (CppParser.isDefinedRecord s) then GNone
            else GStruct (generateStructDecl ctx s)
        OnFunction = fun f ->
            // Skip functions marked with __attribute__((deprecated))
            let isDeprecated = f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")
            if isDeprecated then GNone else GFunc f
        OnMacro = fun m ->
            let decls = generateMacroDeclIfNumeric m
            if decls.IsEmpty then GNone else GMacro decls
        OnTypedef = fun td ->
            if ctx.Bindings.IsEmpty then GNone
            else
                match resolveIn (inUnitOf "typedef" td.Name ctx) td.UnderlyingType with
                | Scalar r when r.Bits > 0 ->
                    let size = if r.Family = TypeMapper.Float && r.Bits = 80 then 16 else (r.Bits + 7) / 8
                    GStruct [
                        Comment $"/// Storage for C typedef `{td.Name}`; {DescriptorGenerator.widthProvenance ctx.DataModel r}."
                        SubModule(td.Name, [LiteralBinding("Size", string size); LiteralBinding("Alignment", string size)])
                        BlankLine ]
                | _ -> GNone
        OnNamespace = fun _ -> GNone
        OnClass = fun c ->
            if c.Name = "" then GNone
            else
            // A C++ record carrying an unnamed bit-field or a field whose type reaches an
            // attributed typedef has clang's layout or none, as a C record does (#182).
            if c.Measured.IsNone then
                match c.UnnamedBitFields with
                | b :: _ ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.UnnamedBitFieldUnmeasured $"C++ record '{c.Name}' carries {CppParser.describeUnnamedBitField b}, which moves bytes natural alignment of the declared field types cannot place, and clang has no measurement of '{c.Name}' in its declaring unit; an unnamed bit-field is never dropped from its record." c.Location.Location)
                | [] -> ()
                match c.TypedefAttributes |> List.tryPick (fun a -> match a.Site with CppParser.FieldType field -> Some (field, a) | CppParser.RecordName -> None) with
                | Some (field, a) ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.TypedefAttributeUnmeasured $"C++ record '{c.Name}' field '{field}' has a type reaching typedef '{a.Typedef}', which carries attribute '{a.Attribute.Kind}', and clang has no measurement of '{c.Name}' in its declaring unit; natural alignment of the declared field types never stands in for the typedef's attribute." c.Location.Location)
                | None -> ()
            failwith $"C++ class '{c.Name}' requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
        OnDelegate = fun _ -> GNone // A delegate is spelled FnPtr<'F> where it is used; no type of its own
    }

    /// Build a GenerationContext from the full, unfiltered declaration list.
    let buildGenerationContext
        (declarations: CppParser.Declaration list)
        (model: PlatformABI)
        (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        : GenerationContext =
        let delegates =
            declarations |> List.choose (function
                | CppParser.Declaration.Delegate d -> Some (d.Name, d)
                | _ -> None)
            |> Map.ofList
        let enums =
            declarations |> List.choose (function
                | CppParser.Declaration.Enum e when e.Name <> "" -> Some (e.Name, e)
                | _ -> None)
            |> Map.ofList
        let structs =
            declarations |> List.choose (function
                | CppParser.Declaration.Struct s when s.Name <> "" -> Some (s.Name, s)
                | _ -> None)
            |> Map.ofList
        { TypedefMap = buildTypedefMap declarations
          DeclarationUnits = Map.empty
          TypeNameUnits = Map.empty
          OpaqueHandles = detectOpaqueHandles declarations
          Delegates = delegates
          Enums = enums
          Structs = structs
          DataModel = model
          StructLayouts = structLayouts
          NonnullAnnotations = None
          NativePointerSurface = false
          Bindings = Map.empty
          ValueStructs = Set.empty
          FlexibleArrays = Map.empty
          PhantomMarkers = Set.empty
          KnownClasses = CppClassAnalysis.buildClassMap declarations
          TypedefInfos = typedefInfosOf declarations
          ByValueTypedefs = [] }

    /// Generate a Clef module with explicit control over what gets emitted.
    /// Uses the full GenerationContext for type resolution, but only declares
    /// the opaque handles in handlesToDeclare and the declarations passed in.
    /// openModules are emitted as `open` directives after the module header; the
    /// BAREWire vocabularies are opened when a struct descriptor or a function descriptor is emitted.
    let generateModule
        (ctx: GenerationContext)
        (handlesToDeclare: Set<string>)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (libraryName: string)
        (comment: string)
        (openModules: string list)
        : string =

        if ctx.NativePointerSurface then
            failwith "experimental_native_pointer_surface cannot emit Clef source: raw addresses and width-named carriers violate the FFI boundary contract."
        let opaqueHandleDecls = generateOpaqueHandleDecls handlesToDeclare
        let markerDecls =
            let declared =
                if ctx.Bindings.IsEmpty then []
                else declarations |> List.choose (function
                    | CppParser.Declaration.Struct s when s.Name <> "" && not (ctx.ValueStructs.Contains s.Name) -> Some s.Name
                    | _ -> None)
            declared @ (if openModules.IsEmpty then Set.toList ctx.PhantomMarkers else [])
            |> List.distinct
            |> List.filter (fun name -> not (ctx.ValueStructs.Contains name) && not (handlesToDeclare.Contains name))
            |> List.map OpaqueMarker

        let groups =
            DeclarationAlgebra.cataDeclarations (generationAlgebra ctx libraryName) declarations

        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let structs = groups |> List.collect (function GStruct d -> d | _ -> [])
        let functionDeclarations =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
        let callbackDeclarations =
            functionDeclarations |> List.collect (callbackEntries ctx namespace')
            |> List.distinctBy (fun (_, name, _) -> name)
            |> List.collect (fun (_, _, declarations) -> declarations)
        let prepared = functionDeclarations |> List.map (fun func ->
            func, callbackEntries ctx namespace' func, generateFunctionDecls ctx libraryName func)
        let anchors = prepared |> List.map (fun (func, entries, raw) ->
            namespace' + "." + func.Name, (namespace' + "." + nativeDeclarationName ctx func entries raw, func, raw)) |> Map.ofList
        let functions = prepared |> List.collect (fun (func, entries, raw) -> declaredFunctionDecls ctx namespace' anchors func entries raw)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])

        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]

        let vocabularyOpens =
            [ if not structs.IsEmpty then "BAREWire.Hardware"
              if not functions.IsEmpty then "BAREWire.Descriptors" ]
        let openDecls =
            match (openModules @ vocabularyOpens) |> List.distinct with
            | [] -> []
            | opens -> (opens |> List.map OpenModule) @ [BlankLine]
        let allDecls = openDecls @ opaqueHandleDecls @ markerDecls @ enums @ structs @ callbackDeclarations @ functions @ macroSection
        let moduleDecl = Module(namespace', comment, allDecls)

        CodeRenderer.render moduleDecl

    // =========================================================================
    // Original API (backward compatible, used for single-file generation)
    // =========================================================================

    /// Generate a complete Fidelity binding source file from parsed declarations.
    /// Architecture: Pre-passes build context → Algebra captures context in closure → Catamorphism → FsDecl tree → Render
    /// PlatformABI fixes the ABI representation each descriptor declares; the signatures spell the one kind.
    /// structLayouts: measured layouts for the structs the pilot measured (declared layouts otherwise).
    let generate
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (libraryName: string)
        (model: PlatformABI)
        (structLayouts: Map<string, CppParser.StructLayoutInfo>)
        : string =
        let ctx = buildGenerationContext declarations model structLayouts
        generateModule ctx ctx.OpaqueHandles declarations namespace' libraryName $"Fidelity binding for {libraryName}" []
