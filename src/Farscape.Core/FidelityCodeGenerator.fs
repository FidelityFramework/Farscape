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

    /// Give every native parameter a distinct Clef binding name. Uppercase identifiers
    /// are constructor patterns in parameter position, so lower their first character.
    let private parameterNames (parameters: (string * string) list) =
        let fresh used (name: string) =
            let stripped = if name.StartsWith "__" then name.TrimStart '_' else name
            let lowered =
                if stripped.Length > 0 then string (System.Char.ToLowerInvariant stripped[0]) + stripped.Substring 1
                else "arg"
            let rec choose suffix =
                let candidate = cleanParamName (if suffix = 0 then lowered else lowered + "_" + string suffix)
                if Set.contains candidate used then choose (suffix + 1) else candidate
            choose 0
        parameters
        |> List.mapFold (fun used (name, _) ->
            let name = fresh used name
            name, Set.add name used) Set.empty
        |> fst

    let private parameterName parameters index = (parameterNames parameters)[index]

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
        /// The unit's macros as clang evaluated them in that unit (#192, #193): a sentinel or
        /// constant a function names is the one its declaring unit evaluates, never another
        /// unit's evaluation of the same name.
        Macros: Map<string, CppParser.MacroDecl>
    }

    /// The unit's macros by name, first definition winning, as clang evaluated them in it.
    let macrosOf (declarations: CppParser.Declaration list) : Map<string, CppParser.MacroDecl> =
        (Map.empty, declarations) ||> List.fold (fun acc decl ->
            match decl with
            | CppParser.Declaration.Macro m when not (acc.ContainsKey m.Name) -> acc.Add(m.Name, m)
            | _ -> acc)

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
          TypedefInfos = typedefInfosOf declarations
          Macros = macrosOf declarations }

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
    /// The retired experimental native-pointer surface (#199): raw addresses and width-named
    /// carriers in Clef source violate the FFI boundary contract. Refused at `source`, under the
    /// pilot key that selects it.
    let nativePointerSurfaceRefusal (source: Diagnostics.Located<Diagnostics.SourceLocation>) : Diagnostics.Diagnostic =
        Diagnostics.diagnostic Diagnostics.NativePointerSurfaceRetired
            "experimental_native_pointer_surface cannot emit Clef source: raw addresses and width-named carriers violate the FFI boundary contract. Remove this option and declare CHandle, callback, reference and ownership projections in the pilot."
            source
        |> Diagnostics.withPilotKey "options.experimental_native_pointer_surface" None

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
        /// Explicitly selected [[cpp.class]] entries, indexed by qualified and short name.
        CppClasses: Map<string, CppClassSpec>
        /// Retired source profile, retained only so legacy configurations receive a precise error.
        NativePointerSurface: bool
        Bindings: Map<string, BindingProjection>
        Anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list>
        ValueStructs: Set<string>
        /// Declared count sources of flexible array members, by struct (BAREWire #11).
        FlexibleArrays: Map<string, FlexibleArrayCount>
        PhantomMarkers: Set<string>
        /// The typedefs of the context's unit as declared, with their attributes (#181).
        TypedefInfos: Map<string, CppParser.TypedefInfo>
        /// Clang's measurements of the by-value parameters and result of the function whose unit
        /// the context is in (`inFunctionUnit`) that reach an attributed typedef (#181).
        ByValueTypedefs: CppParser.ByValueTypedef list
        /// The parsed macros by name, as clang evaluated them: a `constant_parameters` entry on
        /// a function-pointer parameter names a `FunctionPointerSentinel` among them (#192).
        Macros: Map<string, CppParser.MacroDecl>
    }

    /// A context holding one unit's facts in place of the merged ones.
    let withUnitFacts (facts: UnitFacts) (ctx: GenerationContext) : GenerationContext =
        { ctx with TypedefMap = facts.Typedefs; Enums = facts.Enums; OpaqueHandles = facts.OpaqueHandles; TypedefInfos = facts.TypedefInfos; Macros = facts.Macros }

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
        | _ when (TypeMapper.tryScalar ctx.DataModel name).IsSome -> withUnitFacts { Typedefs = Map.empty; Enums = Map.empty; OpaqueHandles = Set.empty; TypedefInfos = Map.empty; Macros = ctx.Macros } ctx
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

    /// What a single-level pointer result addresses, read as a borrowed view (#196): `Some true`
    /// for an 8-bit character (`const unsigned char *`, `const char *`), which may be text or
    /// bytes; `Some false` for void (`const void *`), which is bytes only; None for anything else.
    let private borrowedPointeeIn (ctx: GenerationContext) (cType: string) : bool option =
        match stripNullability cType with
        | ParsedCType info when info.PointerDepth = 1 ->
            match resolveIn ctx info.BaseType with
            | Scalar r when r.Bits = 8 && (r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned) -> Some true
            | Scalar r when r.Family = TypeMapper.Void -> Some false
            | _ -> None
        | _ -> None

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

    // =========================================================================
    // Macro constants and the report of every macro not emitted (#193)
    // =========================================================================

    /// Why a macro the parse reports (every one its pilot's `macro_prefixes` select) is emitted
    /// as no constant. Nothing is dropped without one of these.
    [<RequireQualifiedAccess>]
    type UnemittedMacroKind =
        /// `#define SQLITE_API`: a linkage or attribute marker with no value.
        | EmptyDefinition
        | FunctionLike
        /// A string literal, with the array type clang gives it.
        | StringLiteral of clangType: string
        /// A cast of an integer to a function-pointer type: usable as a `constant_parameters`
        /// value on a parameter of that type (#192), but no Clef constant.
        | FunctionPointerSentinel of pointerType: string * value: int64
        /// A cast to a data pointer type: an address, which no Clef constant carries.
        | PointerCast of typeName: string
        /// No integer constant expression: clang's reason, or why it gave none.
        | NotIntegerConstant of reason: string
        /// A compiler-predefined or reserved name (`linux`, `_FOO`).
        | ReservedName

    /// One macro the parse reports and generation emits as no constant.
    type UnemittedMacro = { Name: string; Kind: UnemittedMacroKind; Definition: string }

    /// The kind as the report prints it.
    let describeUnemittedMacroKind (kind: UnemittedMacroKind) =
        match kind with
        | UnemittedMacroKind.EmptyDefinition -> "empty definition (linkage or attribute marker)"
        | UnemittedMacroKind.FunctionLike -> "function-like macro"
        | UnemittedMacroKind.StringLiteral t -> $"string literal ({t})"
        | UnemittedMacroKind.FunctionPointerSentinel (t, v) -> $"function-pointer sentinel ({t}){v}, a constant_parameters value for a parameter of that type"
        | UnemittedMacroKind.PointerCast t -> $"cast to data pointer type {t}"
        | UnemittedMacroKind.NotIntegerConstant reason -> $"no integer constant: {reason}"
        | UnemittedMacroKind.ReservedName -> "predefined or reserved name"

    /// A macro's disposition: the constant generation emits for it, or why it emits none.
    type MacroDisposition =
        | EmittedConstant of FsDecl list
        | NotEmitted of UnemittedMacro

    /// Each macro's disposition (#193). An integer literal is emitted as written; a composed
    /// integer or an alias with the value clang evaluates (`SQLITE_IOERR_READ = 266`); every
    /// other macro is reported with its kind.
    let macroDisposition (m: CppParser.MacroDecl) : MacroDisposition =
        let unemitted kind = NotEmitted { Name = m.Name; Kind = kind; Definition = m.RawValue }
        match m.Name with
        | CompilerBuiltin | InternalMacro | PredefinedMacro -> unemitted UnemittedMacroKind.ReservedName
        | UserMacro when m.Name.StartsWith "_" -> unemitted UnemittedMacroKind.ReservedName
        | UserMacro ->
            let integerMacroLiteral (value: int64) =
                if value >= int64 System.Int32.MinValue && value <= int64 System.Int32.MaxValue then
                    LiteralBinding(m.Name, Named "int", string value)
                else
                    LiteralBinding(m.Name, Named "int64", string value + "L")
            match m.Kind with
            | CppParser.SimpleValue "" -> unemitted UnemittedMacroKind.EmptyDefinition
            | CppParser.SimpleValue (IntegerLiteral n) -> EmittedConstant [ integerMacroLiteral n ]
            | CppParser.Expression v ->
                match v.Trim().TrimStart('(').TrimEnd(')') with
                | IntegerLiteral n -> EmittedConstant [ integerMacroLiteral n ]
                | _ -> unemitted (UnemittedMacroKind.NotIntegerConstant "clang has not evaluated it")
            | CppParser.SimpleValue _ -> unemitted (UnemittedMacroKind.NotIntegerConstant "its definition is no integer literal clang has evaluated")
            | CppParser.IntegerConstant (_, value) -> EmittedConstant [ integerMacroLiteral value ]
            | CppParser.FunctionLike _ -> unemitted UnemittedMacroKind.FunctionLike
            | CppParser.TypeCast (typeName, _) -> unemitted (UnemittedMacroKind.PointerCast (typeName + "*"))
            | CppParser.FunctionPointerSentinel (_, pointerType, _, value) -> unemitted (UnemittedMacroKind.FunctionPointerSentinel (pointerType, value))
            | CppParser.StringConstant (_, clangType) -> unemitted (UnemittedMacroKind.StringLiteral clangType)
            | CppParser.Unevaluated (_, reason) -> unemitted (UnemittedMacroKind.NotIntegerConstant reason)

    /// Every macro among `declarations` generation emits as no constant, in order, each with
    /// its name and kind (#193).
    let unemittedMacros (declarations: CppParser.Declaration list) : UnemittedMacro list =
        declarations |> List.choose (function
            | CppParser.Declaration.Macro m -> (match macroDisposition m with NotEmitted u -> Some u | EmittedConstant _ -> None)
            | _ -> None)

    /// The pilot's `constant_parameters` entry for parameter `index` of `func`, when it fixes one.
    let private constantParameterOf (ctx: GenerationContext) (func: CppParser.FunctionDecl) (index: int) : string option =
        ctx.Bindings |> Map.tryFind func.Name
        |> Option.bind (fun b -> List.tryItem index b.ConstantParameters)
        |> Option.filter (System.String.IsNullOrWhiteSpace >> not)

    /// The function-pointer sentinel parameter `index` of `func` is fixed to (#192): the macro
    /// the pilot's `constant_parameters` names and the integer clang evaluates for it. Such a
    /// parameter has no Clef parameter; its descriptor records `Fixed (EntrySentinel ...)`.
    /// `generateFunctionDecls` refuses every other constant on a function-pointer parameter.
    let fixedSentinelOf (ctx: GenerationContext) (func: CppParser.FunctionDecl) (index: int) : (string * int64) option =
        constantParameterOf ctx func index
        |> Option.bind (fun name ->
            match ctx.Macros.TryFind name |> Option.map _.Kind with
            | Some (CppParser.FunctionPointerSentinel (_, _, _, value)) -> Some (name, value)
            | _ -> None)

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
                let clefName = unquoteIdentifier (parameterName func.Parameters index)
                // A parameter fixed to a function-pointer sentinel crosses as that constant (#192):
                // the Clef signature has no parameter for it.
                let passBy, provenance =
                    match fixedSentinelOf ctx func index with
                    | Some (macro, value) ->
                        $"Fixed (EntrySentinel ({quoteString macro}, {value}))",
                        provenance + $"; fixed by the pilot to sentinel {macro} = {value}, which clang evaluates; no entry crosses"
                    | None -> (if readOnly then "ReadOnlyReference" elif reference then "Reference" else "Value"), provenance
                Commented(
                    RecordConstruction [
                        "Name", Literal (quoteString clefName)
                        "Type", Literal typeRef
                        "PassBy", Identifier passBy ],
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

    /// The record marker of a callback entry argument that is a single-level pointer to a named
    /// record (`sqlite3_context *`) or an opaque pointer typedef: the entry receives it as an
    /// opaque handle it never dereferences (#189). None for any other type.
    let private opaqueMarkerOf (ctx: GenerationContext) (cType: string) : string option =
        let bare = stripNullability cType
        match resolveIn ctx bare with
        | OpaqueHandle name -> Some name
        | DataPointer (Named name) when (bare |> Seq.filter ((=) '*') |> Seq.length) = 1 ->
            match bare with
            | ParsedCType info ->
                match resolveIn ctx info.BaseType with
                | Unresolved _ when not (ctx.Enums.ContainsKey (TypeMapper.cleanTypeName info.BaseType)) -> Some name
                | _ -> None
            | _ -> None
        | _ -> None

    /// An untyped data pointer (`void *`, or a typedef standing for one): a context the caller
    /// passes and gets back unchanged (#189).
    let private isUntypedPointerIn (ctx: GenerationContext) (cType: string) : bool =
        resolveIn ctx cType = DataPointer (Named "unit")

    /// How a counted array's elements read, in a refusal.
    let private describeCountedElement (element: CountedArrayElement) =
        match element with
        | CountedArrayElement.Text -> "text (char *)"
        | CountedArrayElement.NullableText -> "nullable text (char *)"
        | CountedArrayElement.Handle -> "opaque handles"
        | CountedArrayElement.NullableHandle -> "nullable opaque handles"

    /// A pointer-to-pointer entry argument's element as its declared counted-array element reads
    /// it (#190): the element's Clef carrier and its BAREWire `CountedElement`. None when the
    /// argument is no pointer array of that element (text needs `char **`, a handle `T **` with T
    /// a record).
    let private countedElementOf (ctx: GenerationContext) (element: CountedArrayElement) (argumentType: string) : (FsType * string) option =
        let bare = stripNullability argumentType
        if (bare |> Seq.filter ((=) '*') |> Seq.length) <> 2 then None else
        let pointee = bare.Substring(0, bare.LastIndexOf('*')).Trim()
        match element with
        | CountedArrayElement.Text when isCharPointerIn ctx pointee -> Some (Named "string", "Text")
        | CountedArrayElement.NullableText when isCharPointerIn ctx pointee -> Some (wrapOption (Named "string"), "NullableText")
        | CountedArrayElement.Handle ->
            opaqueMarkerOf ctx pointee |> Option.map (fun marker -> Generic("CHandle", Named marker), $"Handle {quoteString marker}")
        | CountedArrayElement.NullableHandle ->
            opaqueMarkerOf ctx pointee |> Option.map (fun marker -> wrapOption (Generic("CHandle", Named marker)), $"NullableHandle {quoteString marker}")
        | _ -> None

    /// The invocation contract a binding declares for its native parameter `index`.
    let private invocationAt (ctx: GenerationContext) (func: CppParser.FunctionDecl) (index: int) =
        ctx.Bindings |> Map.tryFind func.Name
        |> Option.bind (fun binding -> binding.CallbackInvocations |> List.tryFind (fun contract -> contract.Parameter = index))

    /// The Clef type of callback parameter `index`: its FnPtr, each counted pointer-array argument
    /// its invocation contract declares read as a `ReadOnlyView` of its elements rather than a
    /// nested CHandle (#190). An argument the contract declares wrongly keeps its type here; the
    /// contract's validation refuses it.
    let private callbackParameterType (ctx: GenerationContext) (func: CppParser.FunctionDecl) (index: int) (cType: string) : FsType =
        let resolved = resolveIn ctx cType
        match resolved, invocationAt ctx func index with
        | FunctionPointer (parameters, ret), Some contract when not contract.CountedArrays.IsEmpty ->
            let arguments = resolveCallbackNativeSignature ctx cType |> Option.map snd |> Option.defaultValue []
            let viewed =
                parameters |> List.mapi (fun argument clefType ->
                    match contract.CountedArrays |> List.tryFind (fun array -> array.Argument = argument), List.tryItem argument arguments with
                    | Some array, Some (_, argumentType) ->
                        countedElementOf ctx array.Element argumentType
                        |> Option.map (fun (element, _) -> Generic("ReadOnlyView", element))
                        |> Option.defaultValue clefType
                    | _ -> clefType)
            Generic("FnPtr", FunctionType(viewed, ret))
        | _ -> clefTypeOf resolved

    /// Anonymous entries belong to their parameter site.
    let private callbackEntries (ctx: GenerationContext) (namespace': string) (func: CppParser.FunctionDecl) =
        let ctx = inFunctionUnit ctx func
        func.Parameters |> List.mapi (fun index (name, cType) ->
          // A parameter a constant fixes carries no entry (#192); generateFunctionDecls admits
          // only a function-pointer sentinel there.
          if (constantParameterOf ctx func index).IsSome then None else
          relocateCallbackValue func.Location.Location (Some (bindingKey func.Name "", Some index)) (fun () ->
            resolveCallbackNativeSignature ctx cType |> Option.map (fun (ret, args) ->
                let identity = if cType.Contains "(*)" then func.Name + "::" + name else cType
                let recordName = "Callback_" + callbackIdentifier (namespace' + "::" + identity) + "Entry"
                let parameterInfos = args |> List.mapi (fun index (_, cType) ->
                    let typeRef, provenance = abiClaim ctx cType
                    Commented(RecordConstruction [
                        "Name", Literal (quoteString ((parameterName args index).Replace("``", "")))
                        "Type", Literal typeRef
                        "PassBy", Identifier "Value" ], provenance))
                let returnRef, returnProvenance = abiClaim ctx ret
                let entryType = callbackParameterType ctx func index cType
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
    /// admits only ordered integers, opaque handles, a declared context, declared counted arrays
    /// and void before it claims.
    let descriptorTypeSites (ctx: GenerationContext) (func: CppParser.FunctionDecl) : (string * string) list =
        let ctx = inFunctionUnit ctx func
        let parameters =
            func.Parameters |> List.mapi (fun index (name, cType) ->
                let reference, _ = referencePassing (ctx.Bindings |> Map.tryFind func.Name) index cType
                let element = ctx.Bindings |> Map.tryFind func.Name |> Option.bind (fun b -> List.tryItem index b.ReferenceElements) |> Option.filter ((<>) "")
                let represented = if reference then element |> Option.defaultWith (fun () -> cType.Substring(0, cType.LastIndexOf('*')).Trim()) else cType
                $"parameter '{name}'", represented)
        let callbacks =
            func.Parameters |> List.indexed |> List.collect (fun (index, (name, cType)) ->
                if (constantParameterOf ctx func index).IsSome then [] else
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
    /// `anchors` resolves exit and release bindings to selected generated declarations; None
    /// validates a contract whose declarations this call does not emit (the nullable-callback
    /// check before raw emission, and wrappers that keep only the public binding).
    let private callbackInvocationDecls (ctx: GenerationContext) namespace' (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list> option) (func: CppParser.FunctionDecl) nativeName =
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
                if contract.Scope <> "during_call" && contract.Scope <> "retained" then
                    refuse Diagnostics.CallbackScopeFieldConflict "absence_hypothesis requires during_call or retained"
            | None -> ()
            if contract.Retention.IsSome && contract.Scope <> "retained" then
                refuse Diagnostics.CallbackScopeFieldConflict $"{contract.Scope} forbids retention"
            let arity = func.Parameters.Length
            let scopeType, scopeFields =
                match contract.Scope, contract.ExitBinding, contract.SuccessStatus with
                | "during_call", None, None -> "ScopedCallbackDescriptor", []
                | "retained", None, None ->
                    // A registration the library keeps until a declared release retires it (#191).
                    let retention = contract.Retention |> Option.defaultWith (fun () -> refuse Diagnostics.CallbackScopeFieldConflict "retained requires retention")
                    let refuseRetention reason = refuse Diagnostics.RetentionInvalid $"retention {reason}"
                    let owner = retention.OwnerParameter
                    if owner < 0 || owner >= arity then refuseRetention $"owner_parameter {owner} is outside native arity {arity}"
                    if owner = contract.Parameter then refuseRetention $"owner_parameter {owner} is the callback parameter itself"
                    let ownerType = snd func.Parameters[owner]
                    if (opaqueMarkerOf ctx ownerType).IsNone then
                        refuseRetention $"owner_parameter {owner} ('{ownerType}') is no opaque handle that could hold the registration"
                    for index in retention.RedefinedBy do
                        if index < 0 || index >= arity then refuseRetention $"redefined_by {index} is outside native arity {arity}"
                        if index = contract.Parameter then refuseRetention $"redefined_by {index} is the callback parameter itself"
                    if not retention.RedefinedBy.IsEmpty && not (List.contains owner retention.RedefinedBy) then
                        refuseRetention $"redefined_by must include owner_parameter {owner}: a later call replaces a registration only on its own owner"
                    if retention.RedefinedBy.IsEmpty && retention.Releases.IsEmpty then
                        refuseRetention "names neither redefined_by nor a release; a registration nothing retires is no retained scope"
                    let releases =
                        retention.Releases |> List.map (fun release ->
                            let call binding = FunctionCall("", "OwnerRelease", [RecordConstruction [ "Binding", Literal (quoteString binding); "Parameter", Literal (string release.Parameter) ]])
                            match anchors with
                            | None -> call release.Binding
                            | Some anchors ->
                                match Map.tryFind release.Binding anchors with
                                | None -> refuse Diagnostics.ContractBindingUnselected $"retention release '{release.Binding}' must name a selected qualified generated declaration"
                                | Some (actual, releaseFunc, _) ->
                                    if release.Parameter < 0 || release.Parameter >= releaseFunc.Parameters.Length then
                                        refuseRetention $"release '{release.Binding}' parameter {release.Parameter} is outside its native arity {releaseFunc.Parameters.Length}"
                                    let received = resolveIn (inFunctionUnit ctx releaseFunc) (snd releaseFunc.Parameters[release.Parameter]) |> clefTypeOf
                                    let owned = resolveIn ctx ownerType |> clefTypeOf
                                    if received <> owned then
                                        refuseRetention $"release '{release.Binding}' parameter {release.Parameter} receives {CodeRenderer.renderType received}, not the owner's {CodeRenderer.renderType owned}"
                                    call actual)
                    let redefinition =
                        if retention.RedefinedBy.IsEmpty then []
                        else [ Literal ("Redefinition [|" + (retention.RedefinedBy |> List.map string |> String.concat "; ") + "|]") ]
                    "RetainedCallbackDescriptor", ["Owner", Literal (string owner); "Releases", ArrayBlock (redefinition @ releases)]
                | ("during_call" | "retained"), _, _ -> refuse Diagnostics.CallbackScopeFieldConflict $"{contract.Scope} forbids exit_binding and success_status"
                | "process_exit", anchor, Some status ->
                    match resolveIn ctx func.ReturnType with
                    | Scalar r when r.Family = TypeMapper.Signed && r.Bits = 32 -> ()
                    | _ -> refuse Diagnostics.ProcessExitSignature "process_exit requires a signed32 registration result"
                    let anchorValue =
                        match anchor, anchors with
                        | None, _ -> Identifier "None"
                        | Some selected, None -> Literal ("Some " + quoteString selected)
                        | Some selected, Some anchors ->
                            match Map.tryFind selected anchors with
                            | Some (actual, _, _) -> Literal ("Some " + quoteString actual)
                            | None -> refuse Diagnostics.ContractBindingUnselected "exit_binding must name a selected qualified generated declaration"
                    "ProcessExitCallbackDescriptor", ["ExitBinding", anchorValue; "SuccessStatus", Literal (string status)]
                | "process_exit", _, None -> refuse Diagnostics.CallbackScopeFieldConflict "process_exit requires signed32 success_status"
                | _ -> refuse Diagnostics.CallbackScopeUnknown "scope must be during_call, retained or process_exit"
            if contract.Parameter < 0 || contract.Parameter >= arity then
                refuse Diagnostics.ContractParameterOutOfRange $"parameter {contract.Parameter} is outside native arity {arity}"
            let projection = ctx.Bindings[func.Name]
            // Native ordinals survive string and reference projections (#189); a constant removes
            // the parameter from the Clef signature, so it still needs the untranslated list.
            if projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not) then
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
            let entryArity = parameters.Length
            // Counted pointer-array arguments (#190): each a pointer array of its declared element,
            // counted by a scalar integer argument of the same entry.
            let counted = contract.CountedArrays
            match counted |> List.countBy _.Argument |> List.tryFind (fun (_, count) -> count > 1) with
            | Some (argument, _) -> refuse Diagnostics.CountedArrayInvalid $"counted_arrays declares entry argument {argument} more than once"
            | None -> ()
            let contextArgument = contract.Context |> Option.map _.Argument
            for array in counted do
                let refuseArray reason = refuse Diagnostics.CountedArrayInvalid $"counted_arrays argument {array.Argument}: {reason}"
                for ordinal in [array.Argument; array.CountArgument] do
                    if ordinal < 0 || ordinal >= entryArity then refuseArray $"entry argument {ordinal} is outside the entry's arity {entryArity}"
                if array.Argument = array.CountArgument then refuseArray "an array cannot count itself"
                if counted |> List.exists (fun other -> other.Argument = array.CountArgument) then
                    refuseArray $"count_argument {array.CountArgument} is itself a counted array"
                if contextArgument = Some array.Argument || contextArgument = Some array.CountArgument then
                    refuseArray $"entry argument {contextArgument.Value} is the declared context"
                let argumentType = snd parameters[array.Argument]
                if (countedElementOf ctx array.Element argumentType).IsNone then
                    refuseArray $"entry argument '{fst parameters[array.Argument]}' ('{argumentType}') is no pointer array of {describeCountedElement array.Element}"
                let countType = snd parameters[array.CountArgument]
                match resolveIn ctx countType with
                | Scalar r when (r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned) && not (countType.Contains '*') -> ()
                | _ -> refuseArray $"count_argument {array.CountArgument} ('{countType}') is no scalar integer entry argument"
            // The caller's opaque context passed through to the entry (#189).
            match contract.Context with
            | None -> ()
            | Some context ->
                let refuseContext reason = refuse Diagnostics.CallbackContextInvalid $"context: {reason}"
                if context.Argument < 0 || context.Argument >= entryArity then
                    refuseContext $"entry argument {context.Argument} is outside the entry's arity {entryArity}"
                if context.Parameter < 0 || context.Parameter >= arity then
                    refuseContext $"parameter {context.Parameter} is outside native arity {arity}"
                if context.Parameter = contract.Parameter then
                    refuseContext $"parameter {context.Parameter} is the callback parameter itself"
                let argumentType = snd parameters[context.Argument]
                if not (isUntypedPointerIn ctx argumentType) then
                    refuseContext $"entry argument {context.Argument} ('{argumentType}') is no untyped data pointer (void *)"
                let parameterType = snd func.Parameters[context.Parameter]
                if not (isUntypedPointerIn ctx parameterType) then
                    refuseContext $"parameter {context.Parameter} ('{parameterType}') is no untyped data pointer (void *)"
                let projected =
                    List.contains context.Parameter projection.StringParameters
                    || List.contains context.Parameter projection.ReferenceParameters
                    || List.contains context.Parameter projection.ReadOnlyReferenceParameters
                    || (List.tryItem context.Parameter projection.ParameterHandles |> Option.exists ((<>) ""))
                if projected then
                    refuseContext $"parameter {context.Parameter} is projected; the context passes through untranslated as CHandle<unit>"
            // Each entry argument's admitted representation: a declared counted array or context,
            // an ordered 32- or 64-bit integer, or an opaque handle to a record (#189). The result:
            // such an integer or void.
            let nativeType (argument: int option) (cType: string) =
                if cType.Contains('&') then refuse Diagnostics.CallbackEntryTypeUnadmitted $"unsupported native entry reference signature type '{cType}'"
                let declared =
                    argument |> Option.exists (fun a -> contextArgument = Some a || counted |> List.exists (fun array -> array.Argument = a))
                if not declared then
                    match resolveIn ctx cType with
                    | Scalar r when (r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned) && (r.Bits = 32 || r.Bits = 64) -> ()
                    | Scalar r when r.Family = TypeMapper.Bool ->
                        refuse Diagnostics.CallbackEntryTypeUnadmitted "Boolean native entry signatures are outside the admitted ordered integer callback profile"
                    | Scalar r when argument.IsNone && r.Family = TypeMapper.Void -> ()
                    | DataPointer (Generic ("CHandle", _)) when argument.IsSome ->
                        refuse Diagnostics.PointerArrayUndeclared
                            $"pointer-to-pointer entry argument {argument.Value} ('{cType}') has no counted_arrays declaration; an undeclared pointer array has no admitted representation"
                    | (DataPointer _ | OpaqueHandle _) when argument.IsSome && (opaqueMarkerOf ctx cType).IsSome -> ()
                    | _ -> refuse Diagnostics.CallbackEntryTypeUnadmitted $"unsupported native entry signature type '{cType}'"
                abiClaim ctx cType
            let infos = parameters |> List.mapi (fun index (name, typ) ->
                let typeRef, provenance = nativeType (Some index) typ
                let role =
                    match contextArgument, counted |> List.tryFind (fun array -> array.Argument = index) with
                    | Some a, _ when a = index -> $"; the caller's opaque context, passed through from parameter {contract.Context.Value.Parameter}"
                    | _, Some array -> $"; read as a bounded read-only view of {describeCountedElement array.Element} counted by entry argument {array.CountArgument}"
                    | _ -> ""
                Commented(RecordConstruction [
                    "Name", Literal (quoteString ((parameterName parameters index).Replace("``", "")))
                    "Type", Literal typeRef; "PassBy", Identifier "Value" ], provenance + role))
            let returnRef, returnProvenance = nativeType None ret
            let prefix = nativeName + "Callback" + string contract.Parameter
            let contractName = prefix + "InvocationContract"
            let scopeName = prefix + "Scope"
            let binding = namespace' + "." + nativeName
            let scopeNote =
                if contract.Scope = "retained" then
                    " The entry is retained until a declared release: construct it with FnPtr.ofFunction on a closed module function; no environment is captured or retained."
                else ""
            contract.Parameter, namespace' + "." + contractName, [
                XmlDoc ("Declared foreign activation only: no argument values, invocation count, purity or functionhood are supplied." + scopeNote)
                ValueBinding(scopeName, Generic("Expr", Named scopeType), Quoted(RecordBlock ([
                    "BAREWire.Descriptors." + scopeType + ".Binding", Literal (quoteString binding)
                    "Parameter", Literal (quoteString ((parameterName func.Parameters contract.Parameter).Replace("``", ""))) ] @ scopeFields)))
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
                    ValueBinding(prefix + "AbsenceContract",
                        Generic("Expr", Named "CallbackAbsenceContract"), Quoted(RecordBlock [
                            "BAREWire.Descriptors.CallbackAbsenceContract.Binding", Literal (quoteString binding)
                            "Parameter", Literal (string contract.Parameter)
                            "Hypothesis", RecordBlock [
                                "Identity", Literal (quoteString hypothesis.Identity)
                                "Version", Literal (quoteString hypothesis.Version)
                                "Source", Literal (quoteString hypothesis.Source) ] ]))) |> Option.toList
                yield! contract.Context |> Option.map (fun context ->
                    ValueBinding(prefix + "Context", Generic("Expr", Named "CallbackContextDescriptor"), Quoted(RecordBlock [
                        "BAREWire.Descriptors.CallbackContextDescriptor.Binding", Literal (quoteString binding)
                        "Parameter", Literal (string contract.Parameter)
                        "Argument", Literal (string context.Argument)
                        "ContextParameter", Literal (string context.Parameter) ]))) |> Option.toList
                yield! counted |> List.map (fun array ->
                    let _, element = (countedElementOf ctx array.Element (snd parameters[array.Argument])).Value
                    ValueBinding(prefix + "Argument" + string array.Argument + "CountedArray", Generic("Expr", Named "CountedEntryArrayDescriptor"), Quoted(RecordBlock [
                        "BAREWire.Descriptors.CountedEntryArrayDescriptor.Binding", Literal (quoteString binding)
                        "Parameter", Literal (string contract.Parameter)
                        "Argument", Literal (string array.Argument)
                        "CountArgument", Literal (string array.CountArgument)
                        "Element", Literal element ]))) ])
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
            Diagnostics.refuse (nativePointerSurfaceRefusal func.Location.Location)
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

        // A constant on a function-pointer parameter fixes it to a sentinel the library defines
        // as a cast integer (#192): SQLite's SQLITE_TRANSIENT, `((sqlite3_destructor_type)-1)`.
        // It must name a parsed macro clang evaluates to a sentinel of the parameter's type, and
        // a null sentinel (SQLITE_STATIC) is refused: the library then keeps using the bound
        // value after the call, which only a declared borrow lifetime could admit.
        let sentinels =
            let compact (text: string) = text |> String.filter (System.Char.IsWhiteSpace >> not)
            let canonicalOf (cType: string) =
                let bare = stripNullability cType
                ctx.TypedefMap |> Map.tryFind (TypeMapper.cleanTypeName bare) |> Option.defaultValue bare |> compact
            let resolvedParams = func.Parameters |> List.map (snd >> resolveIn ctx)
            let sentinelIndices =
                resolvedParams
                |> List.mapi (fun j resolved ->
                    match projection |> Option.bind (fun b -> List.tryItem j b.ConstantParameters) |> Option.filter (System.String.IsNullOrWhiteSpace >> not), resolved with
                    | Some _, FunctionPointer _ -> Some j
                    | _ -> None)
                |> List.choose id
            let hasRetainedBorrowForSentinel idx =
                match projection with
                | None -> false
                | Some b ->
                    match b.ReadOnlyBuffers |> List.filter (fun rb -> rb.Parameter < idx) with
                    | [] -> false
                    | preceding ->
                        let nearest = preceding |> List.maxBy _.Parameter
                        match nearest.Retention with
                        | BindingBufferRetention.RetainedUntil (owner, _, _) ->
                            let hasPriorSentinelAfterBuffer =
                                sentinelIndices |> List.exists (fun sIdx -> sIdx > nearest.Parameter && sIdx < idx)
                            let hasInterveningPointer =
                                [ nearest.Parameter + 1 .. idx - 1 ]
                                |> List.exists (fun j ->
                                    j <> owner
                                    && match resolvedParams[j] with
                                       | DataPointer _ | OpaqueHandle _ -> true
                                       | _ -> false)
                            not hasPriorSentinelAfterBuffer && not hasInterveningPointer
                        | BindingBufferRetention.DuringCall -> false
            func.Parameters |> List.mapi (fun idx (name, cType) ->
                match projection |> Option.bind (fun b -> List.tryItem idx b.ConstantParameters) |> Option.filter (System.String.IsNullOrWhiteSpace >> not), resolvedParams[idx] with
                | Some value, FunctionPointer _ ->
                    let key = Some (bindingKey func.Name "constant_parameters")
                    let refuse code reason = refuse code key (Some idx) reason
                    let parameterType = canonicalOf cType
                    match ctx.Macros.TryFind value |> Option.map _.Kind with
                    | Some (CppParser.FunctionPointerSentinel (_, spelled, canonical, address))
                        when parameterType = canonicalOf spelled || parameterType = compact canonical ->
                        if address = 0L && not (hasRetainedBorrowForSentinel idx) then
                            refuse Diagnostics.SentinelBorrowUndeclared
                                $"constant '{value}' fixes function-pointer parameter '{name}' to the null sentinel ({spelled})0, under which the library uses the bound value after the call without copying it; no pilot key declares a borrow lifetime for that value, so the null sentinel is not admitted. Fix a copying sentinel (SQLite's SQLITE_TRANSIENT) instead."
                        Some (idx, (value, address))
                    | Some (CppParser.FunctionPointerSentinel (_, spelled, canonical, _)) ->
                        refuse Diagnostics.SentinelProjectionInvalid
                            $"constant '{value}' is a sentinel of type '{spelled}' ({canonical}), not of parameter '{name}''s type '{cType}'"
                    | Some _ ->
                        let kind =
                            match macroDisposition ctx.Macros.[value] with
                            | NotEmitted u -> describeUnemittedMacroKind u.Kind
                            | EmittedConstant _ -> "an integer constant"
                        refuse Diagnostics.SentinelProjectionInvalid
                            $"constant '{value}' on function-pointer parameter '{name}' names a macro clang evaluates to no function-pointer sentinel: {kind}"
                    | None ->
                        refuse Diagnostics.SentinelProjectionInvalid
                            $"constant '{value}' on function-pointer parameter '{name}' names no parsed macro; a function-pointer parameter is fixed only to a sentinel macro the pilot's macro_prefixes select"
                | _ -> None)
            |> List.choose id |> Map.ofList

        // Map parameters with nullability awareness. A parameter fixed to a sentinel has none.
        let slots =
            func.Parameters
            |> List.mapi (fun idx (name, cType) ->
              if sentinels.ContainsKey idx then idx, None else
              idx, Some (relocateCallbackValue func.Location.Location (Some (bindingKey func.Name "", Some idx)) (fun () ->
                let refuse code key reason = refuse code key (Some idx) reason
                let reference, _ = referencePassing projection idx cType
                let element = projection |> Option.bind (fun b -> List.tryItem idx b.ReferenceElements) |> Option.filter ((<>) "")
                let stringParameter = projection |> Option.exists (fun b -> List.contains idx b.StringParameters)
                let callbackNonnull = projection |> Option.exists (fun b -> List.contains idx b.NonnullCallbacks)
                let handle = projection |> Option.bind (fun b -> List.tryItem idx b.ParameterHandles) |> Option.filter ((<>) "")
                let readOnlyBuffer = projection |> Option.bind (fun b -> b.ReadOnlyBuffers |> List.tryFind (fun rb -> rb.Parameter = idx))
                let bufferAbsence = projection |> Option.bind (fun b -> b.BufferAbsences |> List.tryFind (fun ab -> ab.Parameter = idx))
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
                if provenNonnull && bufferAbsence.IsSome then
                    refuse Diagnostics.NonnullContradiction (nonnullKey |> Option.orElse (Some (bindingKey func.Name "buffer_absences"))) "buffer_absences conflicts with a nonnull contract"
                let nullableCallback =
                    match resolveIn ctx cType with
                    | FunctionPointer _ when not provenNonnull ->
                        let contracts = projection |> Option.map _.CallbackInvocations |> Option.defaultValue []
                        if not (contracts |> List.exists (fun contract -> contract.Parameter = idx)) then
                            refuse Diagnostics.NullableCallbackUnadmitted (Some (bindingKey func.Name "")) "a native function-pointer parameter requires explicit nonnull evidence; nullable callback parameters require a supported boundary adapter."
                        if contracts |> List.exists (fun contract -> contract.Scope <> "during_call" && contract.Scope <> "retained") then
                            refuse Diagnostics.NullableCallbackScope (Some (bindingKey func.Name "callback_invocations")) "nullable callback parameters require only during_call or retained invocation contracts."
                        // Reuse the complete receiving-contract validator before raw emission. Its
                        // declarations are emitted later, where release bindings are resolved.
                        callbackInvocationDecls ctx "" None func func.Name |> ignore
                        true
                    | _ -> false
                let referenceKey =
                    keyHolding idx [ "reference_parameters", _.ReferenceParameters; "read_only_reference_parameters", _.ReadOnlyReferenceParameters ]
                let fsType, isNullable =
                    match readOnlyBuffer with
                    | Some bufferContract ->
                        let roKey = Some (bindingKey func.Name "read_only_buffers")
                        if projection |> Option.exists (fun b -> List.contains idx b.ReferenceParameters) then
                            refuse Diagnostics.ReadOnlyBufferShape roKey "read_only_buffers parameter conflicts with writable reference_parameters"
                        if stringParameter then
                            refuse Diagnostics.ReadOnlyBufferShape roKey "read_only_buffers parameter conflicts with legacy string_parameters"
                        if handle.IsSome then
                            refuse Diagnostics.ReadOnlyBufferShape roKey "read_only_buffers parameter conflicts with parameter_handles"
                        let bufferFsType =
                            match borrowedPointeeIn ctx cType, bufferContract.Content with
                            | Some true, BorrowedContent.Text -> Named "string"
                            | Some _, BorrowedContent.Bytes -> Generic("ReadOnlyView", Named "int")
                            | _, BorrowedContent.Text ->
                                refuse Diagnostics.ReadOnlyBufferShape roKey $"text read-only buffer requires a single-level pointer to an 8-bit character, not '{cType}'"
                            | None, BorrowedContent.Bytes ->
                                refuse Diagnostics.ReadOnlyBufferShape roKey $"bytes read-only buffer requires a single-level pointer to an 8-bit character or void, not '{cType}'"
                        if bufferAbsence.IsNone && outerNullabilityIn ctx "_Nullable" cType then
                            refuse Diagnostics.NonnullContradiction roKey "nullable native parameter without buffer_absences conflicts with non-optional read_only_buffers contract"
                        bufferFsType, bufferAbsence.IsSome
                    | None ->
                        let ty =
                            if reference then
                                match cType with
                                | ParsedCType info when info.PointerDepth = 1 ->
                                    match resolveIn ctx (element |> Option.defaultValue info.BaseType) with
                                    | Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned || r.Family = TypeMapper.Float -> Named (TypeMapper.clefSpelling r.Family + " array")
                                    | (OpaqueHandle _ | DataPointer _) as pointee ->
                                        Named (CodeRenderer.renderType (wrapOption (clefTypeOf pointee)) + " array")
                                    | Scalar r when r.Family = TypeMapper.Pointer ->
                                        Named (CodeRenderer.renderType (wrapOption (clefTypeOf (Scalar r))) + " array")
                                    | Unresolved recordName when ctx.ValueStructs.Contains recordName && not (ctx.Structs.TryFind recordName |> Option.exists _.IsUnion) ->
                                        Named (recordName + " array")
                                    | _ -> refuse Diagnostics.ReferenceCellUnsupported referenceKey "Reference requires a scalar, opaque pointer, or declared value_structs record cell"
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
                                match callbackParameterType ctx func idx cType with
                                | Generic("FnPtr", _) as ty -> ty
                                | _ -> refuse Diagnostics.CallbackNotFunctionPointer (Some (bindingKey func.Name "nonnull_callbacks")) "nonnull_callbacks requires a C function pointer"
                            else
                                match resolveIn ctx cType with
                                | FunctionPointer _ -> callbackParameterType ctx func idx cType
                                | _ -> mapType cType
                        ty, (nullableCallback || (not reference && isDataPointerIn ctx cType && not provenNonnull))
                let finalType = if isNullable then wrapOption fsType else fsType
                { FsParam.Name = parameterName func.Parameters idx; Type = finalType })))
        let parameters = slots |> List.choose snd

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
        let borrowed = projection |> Option.bind _.BorrowedResult
        let returnType =
            match projection |> Option.bind (fun b -> b.ReturnHandle), borrowed with
            | Some _, Some _ ->
                refuseResult Diagnostics.BorrowedResultShape (Some (bindingKey func.Name "borrowed_result")) "borrowed_result conflicts with return_handle: a borrowed view is no handle"
            | Some name, None ->
                if not (isDataPointerIn ctx func.ReturnType) then refuseResult Diagnostics.HandleProjectionNotDataPointer (Some (bindingKey func.Name "return_handle")) "return_handle requires a C data pointer"
                Generic("CHandle", Named name)
            | None, Some borrowedResult ->
                // A borrowed view of the library's bytes (#196): its descriptor states extent and validity.
                let key = Some (bindingKey func.Name "borrowed_result")
                if projection |> Option.exists (fun b -> b.Resource.IsSome) then
                    refuseResult Diagnostics.BorrowedResultShape key "borrowed_result conflicts with resource: a borrowed view is never owned or released"
                match borrowedPointeeIn ctx func.ReturnType, borrowedResult.Content with
                | Some true, _ | Some false, BorrowedContent.Bytes -> Generic("ReadOnlyView", Named "int")
                | Some false, BorrowedContent.Text ->
                    refuseResult Diagnostics.BorrowedResultShape key $"borrowed_result text requires a pointer to an 8-bit character, not '{func.ReturnType}'"
                | None, _ ->
                    refuseResult Diagnostics.BorrowedResultShape key $"borrowed_result requires a single-level pointer to an 8-bit character or void, not '{func.ReturnType}'"
            | None, None -> mapType func.ReturnType
        let finalReturnType =
            if returnIsPointer && not provenReturnNonnull
            then wrapOption returnType
            else returnType

        let defaultSymbol =
            match func.MangledName with
            | Some m when m.StartsWith("_Z", System.StringComparison.Ordinal) -> m
            | _ -> func.Name
        let symbol = projection |> Option.map (fun b -> b.Symbol) |> Option.defaultValue defaultSymbol
        let constants = projection |> Option.map (fun p -> p.ConstantParameters) |> Option.defaultValue []
        // An integer constant: one the wrapper passes as a literal. A sentinel has no Clef value;
        // its descriptor carries it.
        let fixedAt index = if sentinels.ContainsKey index then None else constants |> List.tryItem index |> Option.filter ((<>) "")
        let projected = constants |> List.indexed |> List.exists (fun (index, _) -> (fixedAt index).IsSome)
        let nativeName = if projected then func.Name + "Native" else func.Name
        let nativeFunction = { func with Name = nativeName }
        let descriptorContext =
            match projection with
            | Some p when projected ->
                let sentinelsOnly = p.ConstantParameters |> List.mapi (fun index value -> if sentinels.ContainsKey index then value else "")
                { ctx with Bindings = Map.add nativeName { p with Name = nativeName; ConstantParameters = sentinelsOnly } ctx.Bindings }
            | _ -> ctx
        let wrapper =
            if not projected then [] else
            let args = slots |> List.choose (fun (index, p) ->
                p |> Option.map (fun p ->
                    match fixedAt index with
                    | None -> Identifier p.Name
                    | Some value ->
                        match System.Int64.TryParse value, p.Type with
                        | (true, _), Named "int" -> Literal value
                        | _ -> refuse Diagnostics.ConstantProjectionInvalid (Some (bindingKey func.Name "constant_parameters")) (Some index) "constant projection requires an integer literal and scalar integer parameter"))
            let publicParameters = slots |> List.choose (fun (i, p) -> if (fixedAt i).IsSome then None else p)
            [ LetBinding(func.Name, publicParameters, finalReturnType, FunctionCall("", nativeName, args), []) ]
        let callbackNotes =
            func.Parameters |> List.mapi (fun idx (name, cType) ->
                match sentinels.TryFind idx with
                | Some (macro, value) ->
                    Some (XmlDoc $"Parameter `{name}` is fixed by the pilot to the function-pointer sentinel {macro} ({value}, as clang evaluates it): no entry crosses, and the library's meaning for that sentinel applies.")
                | None ->
                match resolveIn ctx cType with
                | FunctionPointer _ ->
                    let evidence =
                        [ if clangNonnull.Contains idx then "header nonnull attribute"
                          if tomlNonnull.Contains idx then "pilot annotations.nonnull"
                          if outerNullabilityIn ctx "_Nonnull" cType then "native _Nonnull type"
                          if projection |> Option.exists (fun b -> List.contains idx b.NonnullCallbacks) then "pilot nonnull_callbacks" ]
                        |> String.concat ", "
                    let contract = invocationAt ctx func idx
                    let scope = contract |> Option.map _.Scope |> Option.defaultValue "during_call"
                    // Each counted array the entry reads, and the context it passes through (#189, #190).
                    let arguments = resolveCallbackNativeSignature ctx cType |> Option.map snd |> Option.defaultValue []
                    let roles =
                        contract |> Option.map (fun contract ->
                            [ for array in contract.CountedArrays do
                                let argument = arguments |> List.tryItem array.Argument |> Option.map fst |> Option.defaultValue $"arg{array.Argument}"
                                let count = arguments |> List.tryItem array.CountArgument |> Option.map fst |> Option.defaultValue $"arg{array.CountArgument}"
                                $" Entry argument `{argument}` is a read-only view of `{count}` elements borrowed until the entry returns."
                              match contract.Context with
                              | Some context ->
                                  let argument = arguments |> List.tryItem context.Argument |> Option.map fst |> Option.defaultValue $"arg{context.Argument}"
                                  let parameter = func.Parameters |> List.tryItem context.Parameter |> Option.map fst |> Option.defaultValue $"arg{context.Parameter}"
                                  $" Entry argument `{argument}` is the opaque `{parameter}` passed through unchanged."
                              | None -> () ] |> String.concat "")
                        |> Option.defaultValue ""
                    let activation =
                        if scope = "retained" then "the declared retained external hypothesis: the library may invoke it until a declared release retires the registration"
                        else $"the declared {scope} external hypothesis"
                    Some (XmlDoc ((if evidence = "" then
                                      $"Callback parameter `{name}` is optional; None supplies no callable entry. Activation relies on {activation}, not a proof of foreign behavior."
                                   else $"Callback parameter `{name}` nonnull is an external execution hypothesis from {evidence}; it is not a proof of foreign behavior.") + roles))
                | _ -> None) |> List.choose id
        // A character-pointer result nothing declares stays opaque, and says why (#196).
        let resultNote =
            match projection, borrowedPointeeIn ctx func.ReturnType with
            | Some p, _ when p.BorrowedResult.IsSome || p.ReturnHandle.IsSome || p.Resource.IsSome -> []
            | _, Some true ->
                [ XmlDoc $"Result `{func.ReturnType}` is kept opaque: no borrowed_result declares its owner, extent and validity, so it is not read as text or bytes." ]
            | _ -> []
        formatDocDecls { func with Name = symbol } @ callbackNotes @ resultNote @
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
        | _ -> Diagnostics.raiseRefusal (Diagnostics.uncoded "FunctionDescriptor body must be a quoted RecordBlock")

    /// Only a real callback/argument adapter needs a separate native declaration.
    /// An unchanged optional entry carrier is already the public binding surface.
    let private nativeDeclarationName (ctx: GenerationContext) (func: CppParser.FunctionDecl) (entries: (int * string * FsDecl list) list) (raw: FsDecl list) =
        let ctx = inFunctionUnit ctx func
        let name, parameters = raw |> List.pick (function LetBinding(name, parameters, _, _, attrs) when not attrs.IsEmpty -> Some(name, parameters) | _ -> None)
        let unchangedOptional =
            not entries.IsEmpty &&
            (ctx.Bindings |> Map.tryFind func.Name |> Option.bind _.Resource |> Option.isNone) &&
            (entries |> List.forall (fun (ordinal, _, _) ->
                // By name: a parameter fixed to a sentinel (#192) has no Clef parameter.
                match parameters |> List.tryFind (fun p -> p.Name = parameterName func.Parameters ordinal) |> Option.map _.Type with
                | Some (Generic("option", Generic("FnPtr", _))) -> true
                | _ -> false)) &&
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
            let validReleaseReturn =
                match result, resolveIn releaseCtx release.ReturnType with
                | Named "unit", Scalar repr when repr.Family = TypeMapper.Void -> true
                | Named "int", Scalar repr when repr.Family = TypeMapper.Signed && repr.Bits = 32 -> true
                | _ -> false
            match release.Parameters, parameters with
            | [_, typ], [parameter]
                when validReleaseReturn && isDataPointerIn releaseCtx typ
                     && parameter.Type = Generic("CHandle", Named marker) -> ()
            | _ -> refuse Diagnostics.ResourceReleaseShape "release requires unary matching CHandle Value parameter 0 and a raw Void or signed32 int result"
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

    /// The borrowed view a binding declares for its pointer result (#196): the owner parameter
    /// whose argument owns the bytes, the extent (an integer companion call on the borrowing
    /// call's own arguments, or the terminating NUL of text) and the selected calls that end its
    /// validity. The result type itself is projected in `generateFunctionDecls`.
    let private borrowedResultDecls (ctx: GenerationContext) (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list>) (func: CppParser.FunctionDecl) (qualifiedName: string) =
        let ctx = inFunctionUnit ctx func
        match ctx.Bindings |> Map.tryFind func.Name |> Option.bind _.BorrowedResult with
        | None -> []
        | Some borrowed ->
            let key = bindingKey func.Name "borrowed_result"
            let refuseAt code field parameter reason = refuseContract func code (Some (key + field, parameter)) $"{func.Name} borrowed_result: {reason}"
            for name, value in ["identity", borrowed.Identity; "version", borrowed.Version; "source", borrowed.Source] do
                if System.String.IsNullOrWhiteSpace value then refuseAt Diagnostics.HypothesisFieldEmpty ".hypothesis" None $"requires nonempty {name}"
            let arity = func.Parameters.Length
            let owner = borrowed.OwnerParameter
            let refuseOwner reason = refuseAt Diagnostics.BorrowedInvalidationInvalid "" (Some owner) reason
            if owner < 0 || owner >= arity then refuseAt Diagnostics.ContractParameterOutOfRange "" (Some owner) $"owner_parameter {owner} is outside native arity {arity}"
            let ownerType = snd func.Parameters[owner]
            if (opaqueMarkerOf ctx ownerType).IsNone then
                refuseOwner $"owner_parameter {owner} ('{ownerType}') is no opaque handle that could own the result"
            let owned = resolveIn ctx ownerType |> clefTypeOf
            let selected field (binding: string) =
                anchors |> Map.tryFind binding
                |> Option.defaultWith (fun () -> refuseAt Diagnostics.ContractBindingUnselected field None $"'{binding}' must name a selected qualified generated declaration")
            let extent =
                match borrowed.Extent with
                | BorrowedExtent.NulTerminated ->
                    if borrowed.Content = BorrowedContent.Bytes then
                        refuseAt Diagnostics.BorrowedExtentInvalid ".extent" None "nul_terminated requires text content: bytes may hold a NUL before their end"
                    Identifier "NulTerminated"
                | BorrowedExtent.Companion (binding, arguments) ->
                    let refuseExtent reason = refuseAt Diagnostics.BorrowedExtentInvalid ".extent" None $"extent '{binding}' {reason}"
                    let actual, companion, _ = selected ".extent" binding
                    let companionCtx = inFunctionUnit ctx companion
                    match resolveIn companionCtx companion.ReturnType with
                    | Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned -> ()
                    | _ -> refuseExtent $"returns '{companion.ReturnType}', not an integer byte count"
                    if arguments.Length <> companion.Parameters.Length then
                        refuseExtent $"takes {companion.Parameters.Length} arguments, not the {arguments.Length} arguments declared"
                    if not (List.contains owner arguments) then
                        refuseExtent $"arguments must include owner_parameter {owner}: the extent is measured on the same owner"
                    arguments |> List.iteri (fun index argument ->
                        if argument >= arity then refuseExtent $"argument {argument} is outside native arity {arity}"
                        let passed = resolveIn ctx (snd func.Parameters[argument]) |> clefTypeOf
                        let received = resolveIn companionCtx (snd companion.Parameters[index]) |> clefTypeOf
                        if passed <> received then
                            refuseExtent $"parameter {index} receives {CodeRenderer.renderType received}, not argument {argument}'s {CodeRenderer.renderType passed}")
                    Literal ($"CompanionCall ({quoteString actual}, [|" + (arguments |> List.map string |> String.concat "; ") + "|])")
            if borrowed.InvalidatedBy.IsEmpty then
                refuseOwner "invalidated_by is empty: a result nothing invalidates is no borrow"
            let invalidations =
                borrowed.InvalidatedBy |> List.map (function
                    | BorrowInvalidation.AnyOwnerCall -> Identifier "AnyOwnerCall"
                    | BorrowInvalidation.OwnerCall (binding, parameter) ->
                        let actual, call, _ = selected ".invalidated_by" binding
                        if parameter >= call.Parameters.Length then
                            refuseOwner $"invalidation '{binding}' parameter {parameter} is outside its native arity {call.Parameters.Length}"
                        let received = resolveIn (inFunctionUnit ctx call) (snd call.Parameters[parameter]) |> clefTypeOf
                        if received <> owned then
                            refuseOwner $"invalidation '{binding}' parameter {parameter} receives {CodeRenderer.renderType received}, not the owner's {CodeRenderer.renderType owned}"
                        FunctionCall("", "OwnerCall", [RecordConstruction [ "Binding", Literal (quoteString actual); "Parameter", Literal (string parameter) ]]))
            let content = match borrowed.Content with BorrowedContent.Text -> "Utf8Text" | BorrowedContent.Bytes -> "RawBytes"
            let ownerName = fst func.Parameters[owner]
            [ XmlDoc $"Borrowed result: the library's bytes, owned through `{ownerName}`, read-only and valid only until a declared invalidation; an external execution hypothesis, not a proof of foreign behavior."
              ValueBinding(func.Name + "BorrowedResult", Generic("Expr", Named "BorrowedResultViewDescriptor"),
                  Quoted(RecordBlock [
                      // Qualified: Binding and Owner are also RetainedCallbackDescriptor fields.
                      "BAREWire.Descriptors.BorrowedResultViewDescriptor.Binding", Literal (quoteString qualifiedName)
                      "Content", Identifier content
                      "Extent", extent
                      "Owner", Literal (string owner)
                      "InvalidatedBy", ArrayBlock invalidations
                      "Hypothesis", RecordBlock [
                          "Identity", Literal (quoteString borrowed.Identity)
                          "Version", Literal (quoteString borrowed.Version)
                          "Source", Literal (quoteString borrowed.Source) ] ])) ]

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

    /// Emit ReadOnlyBufferDescriptor quotations for zero-copy input buffer contracts (#57, #206).
    let private readOnlyBufferDecls (ctx: GenerationContext) namespace' (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list> option) (func: CppParser.FunctionDecl) nativeName hasAdapter =
        let ctx = inFunctionUnit ctx func
        let projection = ctx.Bindings |> Map.tryFind func.Name
        let contracts = projection |> Option.map _.ReadOnlyBuffers |> Option.defaultValue []
        let key = bindingKey func.Name "read_only_buffers"
        let refuseAt code parameter reason = refuseContract func code (Some (key, parameter)) $"{func.Name} read_only_buffers: {reason}"
        match contracts |> List.countBy _.Parameter |> List.tryFind (fun (_, count) -> count > 1) with
        | Some (duplicate, _) -> refuseAt Diagnostics.ContractDuplicateParameter (Some duplicate) "duplicate parameter indices"
        | None -> ()
        contracts |> List.collect (fun contract ->
            let refuse code reason = refuseAt code (Some contract.Parameter) reason
            for fieldName, value in ["identity", contract.Identity; "version", contract.Version; "source", contract.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty $"requires nonempty {fieldName}"
            let projection = projection.Value
            let nonSentinelConstant =
                projection.ConstantParameters
                |> List.indexed
                |> List.exists (fun (idx, value) -> not (System.String.IsNullOrWhiteSpace value) && (fixedSentinelOf ctx func idx).IsNone)
            if hasAdapter || nonSentinelConstant then
                refuse Diagnostics.ContractRequiresUntranslated "requires an untranslated native parameter list"
            let arity = func.Parameters.Length
            if contract.Parameter < 0 || contract.Parameter >= arity then
                refuseAt Diagnostics.ContractParameterOutOfRange (Some contract.Parameter) $"parameter {contract.Parameter} is outside native arity {arity}"
            let constantAt idx =
                projection.ConstantParameters |> List.tryItem idx |> Option.exists (System.String.IsNullOrWhiteSpace >> not)
            if constantAt contract.Parameter then
                refuse Diagnostics.ReadOnlyBufferShape "buffer parameter cannot be fixed via constant_parameters"
            if projection.WritableBuffers |> List.exists (fun w -> w.Parameter = contract.Parameter) then
                refuse Diagnostics.ReadOnlyBufferShape "parameter cannot declare both writable_buffers and read_only_buffers"
            if List.contains contract.Parameter projection.ReferenceParameters then
                refuse Diagnostics.ReadOnlyBufferShape "parameter cannot declare writable reference_parameters with read_only_buffers"
            if List.contains contract.Parameter projection.StringParameters then
                refuse Diagnostics.ReadOnlyBufferShape "parameter cannot declare string_parameters with read_only_buffers"
            if List.tryItem contract.Parameter projection.ParameterHandles |> Option.exists (System.String.IsNullOrWhiteSpace >> not) then
                refuse Diagnostics.ReadOnlyBufferShape "parameter cannot declare parameter_handles with read_only_buffers"
            let bufferType = snd func.Parameters[contract.Parameter]
            match borrowedPointeeIn ctx bufferType, contract.Content with
            | Some true, BorrowedContent.Text -> ()
            | Some _, BorrowedContent.Bytes -> ()
            | _, BorrowedContent.Text ->
                refuse Diagnostics.ReadOnlyBufferShape $"text read-only buffer requires a single-level pointer to an 8-bit character, not '{bufferType}'"
            | None, BorrowedContent.Bytes ->
                refuse Diagnostics.ReadOnlyBufferShape $"bytes read-only buffer requires a single-level pointer to an 8-bit character or void, not '{bufferType}'"
            let extentAst =
                match contract.Extent with
                | BindingReadOnlyBufferExtent.UncountedNulTerminated ->
                    if contract.Content <> BorrowedContent.Text then
                        refuse Diagnostics.ReadOnlyBufferShape "nul_terminated requires text content: bytes may hold a NUL before their end"
                    Identifier "UncountedNulTerminated"
                | BindingReadOnlyBufferExtent.ExplicitCount countParam ->
                    if countParam < 0 || countParam >= arity then
                        refuseAt Diagnostics.ContractParameterOutOfRange (Some countParam) $"count_parameter {countParam} is outside native arity {arity}"
                    if countParam = contract.Parameter then
                        refuse Diagnostics.ReadOnlyBufferCountInvalid "requires distinct buffer and count parameters"
                    if constantAt countParam then
                        refuseAt Diagnostics.ReadOnlyBufferCountInvalid (Some countParam) "count parameter cannot be fixed via constant_parameters"
                    let countType = snd func.Parameters[countParam]
                    let countRef, _ = referencePassing (Some projection) countParam countType
                    match resolveIn ctx countType with
                    | Scalar scalar when not countRef && (scalar.Family = TypeMapper.Signed || scalar.Family = TypeMapper.Unsigned) -> ()
                    | _ -> refuseAt Diagnostics.ReadOnlyBufferCountInvalid (Some countParam) "count parameter requires scalar integer Value passing"
                    Literal ($"ExplicitCount {countParam}")
            let retentionAst =
                match contract.Retention with
                | BindingBufferRetention.DuringCall -> Identifier "DuringCall"
                | BindingBufferRetention.RetainedUntil (owner, redefinedBy, releases) ->
                    let refuseRetention reason = refuse Diagnostics.ReadOnlyBufferRetentionInvalid $"retention {reason}"
                    if owner < 0 || owner >= arity then
                        refuseAt Diagnostics.ContractParameterOutOfRange (Some owner) $"retention owner_parameter {owner} is outside native arity {arity}"
                    if owner = contract.Parameter then
                        refuseRetention $"owner_parameter {owner} is the buffer parameter itself"
                    let ownerType = snd func.Parameters[owner]
                    if (opaqueMarkerOf ctx ownerType).IsNone then
                        refuseRetention $"owner_parameter {owner} ('{ownerType}') is no opaque handle that could retain the buffer"
                    for idx in redefinedBy do
                        if idx < 0 || idx >= arity then
                            refuseAt Diagnostics.ContractParameterOutOfRange (Some idx) $"retention redefined_by {idx} is outside native arity {arity}"
                        if idx = contract.Parameter then
                            refuseRetention $"redefined_by {idx} is the buffer parameter itself"
                    if not redefinedBy.IsEmpty && not (List.contains owner redefinedBy) then
                        refuseRetention $"redefined_by must include owner_parameter {owner}: a later call replaces a slot only on its own owner"
                    if redefinedBy.IsEmpty && releases.IsEmpty then
                        refuseRetention "names neither redefined_by nor a release; a buffer nothing retires has no bounded retention"
                    let releaseEntries =
                        releases |> List.map (fun release ->
                            let call binding =
                                $"OwnerRelease {{ Binding = {quoteString binding}; Parameter = {release.Parameter} }}"
                            match anchors with
                            | None -> call release.Binding
                            | Some anchors ->
                                match Map.tryFind release.Binding anchors with
                                | None -> refuse Diagnostics.ContractBindingUnselected $"retention release '{release.Binding}' must name a selected qualified generated declaration"
                                | Some (actual, releaseFunc, _) ->
                                    if release.Parameter < 0 || release.Parameter >= releaseFunc.Parameters.Length then
                                        refuseRetention $"release '{release.Binding}' parameter {release.Parameter} is outside its native arity {releaseFunc.Parameters.Length}"
                                    let received = resolveIn (inFunctionUnit ctx releaseFunc) (snd releaseFunc.Parameters[release.Parameter]) |> clefTypeOf
                                    let owned = resolveIn ctx ownerType |> clefTypeOf
                                    if received <> owned then
                                        refuseRetention $"release '{release.Binding}' parameter {release.Parameter} receives {CodeRenderer.renderType received}, not the owner's {CodeRenderer.renderType owned}"
                                    call actual)
                    let redefinitionEntries =
                        if redefinedBy.IsEmpty then []
                        else [ "Redefinition [| " + (redefinedBy |> List.map string |> String.concat "; ") + " |]" ]
                    let allEntries = (redefinitionEntries @ releaseEntries) |> String.concat "; "
                    Literal ($"RetainedUntil ({owner}, [| {allEntries} |])")
            let contentId = match contract.Content with BorrowedContent.Text -> "Utf8Text" | BorrowedContent.Bytes -> "RawBytes"
            [ XmlDoc "Read-only input buffer contract: call-bounded or owner-retained borrow over uncopied caller storage; requires independent source and discharge evidence."
              ValueBinding(nativeName + "ReadOnlyBuffer" + string contract.Parameter + "Descriptor", Generic("Expr", Named "ReadOnlyBufferDescriptor"),
                  Quoted(RecordBlock [
                      "BAREWire.Descriptors.ReadOnlyBufferDescriptor.Binding", Literal (quoteString (namespace' + "." + nativeName))
                      "Parameter", Literal (string contract.Parameter)
                      "Extent", extentAst
                      "Content", Identifier contentId
                      "Retention", retentionAst
                      "Hypothesis", RecordBlock [
                          "Identity", Literal (quoteString contract.Identity)
                          "Version", Literal (quoteString contract.Version)
                          "Source", Literal (quoteString contract.Source) ] ])) ])

    /// Emit BufferAbsenceContract quotations for optional input buffer parameters (#57).
    let private bufferAbsenceDecls (ctx: GenerationContext) namespace' (func: CppParser.FunctionDecl) nativeName hasAdapter =
        let ctx = inFunctionUnit ctx func
        let projection = ctx.Bindings |> Map.tryFind func.Name
        let absences = projection |> Option.map _.BufferAbsences |> Option.defaultValue []
        let buffers = projection |> Option.map _.ReadOnlyBuffers |> Option.defaultValue []
        let key = bindingKey func.Name "buffer_absences"
        let refuseAt code parameter reason = refuseContract func code (Some (key, parameter)) $"{func.Name} buffer_absences: {reason}"
        match absences |> List.countBy _.Parameter |> List.tryFind (fun (_, count) -> count > 1) with
        | Some (duplicate, _) -> refuseAt Diagnostics.ContractDuplicateParameter (Some duplicate) "duplicate parameter indices"
        | None -> ()
        absences |> List.collect (fun absence ->
            let refuse code reason = refuseAt code (Some absence.Parameter) reason
            for fieldName, value in ["identity", absence.Identity; "version", absence.Version; "source", absence.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse Diagnostics.HypothesisFieldEmpty $"requires nonempty {fieldName}"
            if hasAdapter then
                refuse Diagnostics.ContractRequiresUntranslated "requires an untranslated native parameter list"
            if absence.Parameter < 0 || absence.Parameter >= func.Parameters.Length then
                refuseAt Diagnostics.ContractParameterOutOfRange (Some absence.Parameter) $"parameter {absence.Parameter} is outside native arity {func.Parameters.Length}"
            match buffers |> List.tryFind (fun b -> b.Parameter = absence.Parameter) with
            | None -> refuse Diagnostics.BufferAbsenceInvalid "buffer_absences requires a matching read_only_buffers contract on the same parameter"
            | Some matchingBuffer ->
                if absence.Identity = matchingBuffer.Identity then
                    refuse Diagnostics.AbsenceIdentityReused "buffer_absences hypothesis identity must differ from read_only_buffers hypothesis identity"
            [ XmlDoc "Optional input buffer absence contract: NULL/None is an explicit admitted absence case."
              ValueBinding(nativeName + "BufferAbsence" + string absence.Parameter + "Contract", Generic("Expr", Named "BufferAbsenceContract"),
                  Quoted(RecordBlock [
                      "BAREWire.Descriptors.BufferAbsenceContract.Binding", Literal (quoteString (namespace' + "." + nativeName))
                      "Parameter", Literal (string absence.Parameter)
                      "Hypothesis", RecordBlock [
                          "Identity", Literal (quoteString absence.Identity)
                          "Version", Literal (quoteString absence.Version)
                          "Source", Literal (quoteString absence.Source) ] ])) ])

    /// `anchors` is None where only the public binding is kept (wrappers): no contract
    /// declaration is emitted there, so no exit or release binding is resolved.
    let private declaredFunctionDecls (ctx: GenerationContext) namespace' (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list> option) (func: CppParser.FunctionDecl) (entries: (int * string * FsDecl list) list) (raw: FsDecl list) =
        let ctx = inFunctionUnit ctx func
        let resourceAnchors = anchors |> Option.defaultValue Map.empty
        // A borrowed result's descriptor names selected declarations: emitted only where they are (#196).
        let borrowedDecls nativeName =
            match anchors with
            | Some anchors -> borrowedResultDecls ctx anchors func (namespace' + "." + nativeName)
            | None -> []
        if entries.IsEmpty then
            let nativeName = raw |> List.pick (function LetBinding(name, _, _, _, attrs) when not attrs.IsEmpty -> Some name | _ -> None)
            callbackInvocationDecls ctx namespace' anchors func nativeName |> ignore
            raw @ resultRangeDecls ctx namespace' func nativeName false @ resourceDecls ctx namespace' resourceAnchors func nativeName raw @ writableBufferDecls ctx namespace' func nativeName false
            @ readOnlyBufferDecls ctx namespace' anchors func nativeName false
            @ bufferAbsenceDecls ctx namespace' func nativeName false
            @ borrowedDecls nativeName
        else
        resultRangeDecls ctx namespace' func func.Name true |> ignore
        writableBufferDecls ctx namespace' func func.Name true |> ignore
        let nativeName, nativeParameters, returnType =
            raw |> List.pick (function LetBinding(name, parameters, ret, _, attrs) when not attrs.IsEmpty -> Some (name, parameters, ret) | _ -> None)
        let callbackTypes = entries |> List.choose (fun (i, name, _) ->
            match nativeParameters |> List.tryFind (fun p -> p.Name = parameterName func.Parameters i) |> Option.map _.Type with
            | Some (Generic("option", Generic("FnPtr", _))) -> None
            | _ -> Some (parameterName func.Parameters i, name)) |> Map.ofList
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
        declarations @ invocationDeclarations @ resourceDecls ctx namespace' resourceAnchors func emittedName raw @ readOnlyBufferDecls ctx namespace' anchors func emittedName needsAdapter @ bufferAbsenceDecls ctx namespace' func emittedName needsAdapter @ borrowedDecls emittedName @ adapter

    let generateDeclaredFunctionDecls (ctx: GenerationContext) libraryName namespace' (func: CppParser.FunctionDecl) =
        let ctx = inFunctionUnit ctx func
        declaredFunctionDecls ctx namespace' None func (callbackEntries ctx namespace' func) (generateFunctionDecls ctx libraryName func)

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
                // A value struct's Clef record of named fields is laid out from its field types
                // (#199): a union, an array or bit-field member, an unnamed bit-field (#176) or a
                // typedef attribute a field type reaches (#175) is none of those.
                let refuseShape message =
                    Diagnostics.diagnostic Diagnostics.ValueStructUnrepresentable message s.Location.Location
                    |> Diagnostics.withPilotKey "options.value_structs" None
                    |> Diagnostics.refuse
                if s.IsUnion then
                    refuseShape $"Value struct '{s.Name}' is a union; a plain record with scalar or pointer fields is required."
                match s.Fields |> List.tryFind (fun field -> field.IsArray || field.IsBitfield) with
                | Some field ->
                    let what = if field.IsArray then "an array" else "a bit-field"
                    refuseShape $"Value struct '{s.Name}' field '{field.Name}' is {what}; a plain record with scalar or pointer fields is required."
                | None -> ()
                match s.UnnamedBitFields with
                | b :: _ ->
                    refuseShape $"Value struct '{s.Name}' carries {CppParser.describeUnnamedBitField b}, which its Clef record of named fields cannot represent; a plain record with scalar or pointer fields is required."
                | [] -> ()
                match s.TypedefAttributes |> List.tryPick (fun a -> match a.Site with CppParser.FieldType field -> Some (field, a) | CppParser.RecordName -> None) with
                | Some (field, a) ->
                    refuseShape $"Value struct '{s.Name}' field '{field}' has a type reaching typedef '{a.Typedef}', which carries attribute '{a.Attribute.Kind}' that its Clef record's field type cannot represent; a plain record with scalar or pointer fields is required."
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
                    | Unresolved name ->
                        match byValueRecordOf unitCtx field.Type with
                        | Some (recordName, "union") ->
                            refuseShape $"Value struct '{s.Name}' field '{field.Name}' has union type '{recordName}'; a plain record with scalar or pointer fields is required."
                        | Some (recordName, "struct") when ctx.ValueStructs.Contains recordName ->
                            Named recordName
                        | Some (recordName, kind) ->
                            refuseShape $"Value struct '{s.Name}' field '{field.Name}' has {kind} type '{recordName}' not declared in options.value_structs; a plain record with scalar, pointer or value-struct fields is required."
                        | None ->
                            refuseShape $"Value struct '{s.Name}' field '{field.Name}' has unrepresented type '{name}'; a plain record with scalar or pointer fields is required."
                    | _ -> clefTypeOf resolved)
                [ RecordType(s.Name, s.Fields |> List.map (fun field -> cleanParamName field.Name, fieldType field), s.Documentation, []) ]
            else []
        valueDecls @ DescriptorGenerator.structDecls
            { Structs = ctx.Structs; Layouts = layouts; Model = ctx.DataModel; Shape = shape; FlexibleArrays = ctx.FlexibleArrays }
            s

    /// Generate opaque container operations and their ABI/ownership declarations (#120).
    let generateCppTemplateContainerDecls (ctx: GenerationContext)
                                         (inventory: CppClassAnalysis.CppTemplateInventory)
                                         (spec: CppClassAnalysis.CppTemplateContainerSpec) : FsDecl list =
        CppClassAnalysis.validateTemplateContainerContract inventory spec
        let element = resolveIn ctx spec.ElementType
        let admittedElement =
            match element with
            | Scalar _ | CEnum _ -> clefTypeOf element = Named spec.ElementClefType
            | Unresolved name when ctx.ValueStructs.Contains name -> name = spec.ElementClefType
            | _ -> false
        if not admittedElement then
            let source = spec.Source |> Option.defaultValue (Diagnostics.Located.Unknown "No header location was supplied for the container element.")
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.CppContainerOutOfRange
                $"C++ container '{spec.SafeName}': element '{spec.ElementType}' has no admitted value binding as '{spec.ElementClefType}'." source)
        let pointer = DescriptorGenerator.typeRefSource (TypeMapper.pointerRepr ctx.DataModel "void *")
        let intRef = abiReprOf ctx.DataModel "int" (resolveIn ctx "int") |> Option.get |> DescriptorGenerator.typeRefSource
        let handle name = Generic("CHandle", Named name)
        let self = { Name = "self"; Type = handle spec.SafeName }, pointer
        let output name typ = { Name = name; Type = handle typ }, pointer
        let prefix = "farscape_cpp_container_" + spec.SafeName
        let binding suffix ownership (parameters: (FsParam * string) list) returnType returnRef =
            let name = spec.SafeName + suffix
            let symbol = prefix + suffix
            [ LetBinding(name, List.map fst parameters, returnType, NativeZeroed,
                         [ $"FidelityExtern({quoteString spec.LibraryName}, {quoteString symbol})" ])
              ValueBinding(name + "Descriptor", Generic("Expr", Named "FunctionDescriptor"),
                Quoted(RecordBlock [
                    "CName", Literal (quoteString symbol)
                    "Parameters", ArrayBlock (parameters |> List.map (fun (p, repr) -> RecordConstruction [
                        "Name", Literal (quoteString (unquoteIdentifier p.Name)); "Type", Literal repr; "PassBy", Identifier "Value" ]))
                    "ReturnType", Literal returnRef
                    "CallingConvention", Identifier "CDecl"
                    "OwnershipTransfer", Identifier ownership ]))
              BlankLine ]
        let acquisition =
            match spec.FactorySymbol with
            | None -> []
            | Some _ ->
                binding "_acquire" "CallerOwns" [] (Generic("option", handle spec.SafeName)) pointer @
                [ ValueBinding(spec.SafeName + "_acquireResourceDescriptor", Generic("Expr", Named "ResourceDescriptor"),
                    Quoted(RecordBlock [
                        "Resource", FunctionCall("", "OwnedNullableReturn", [ RecordConstruction [
                            "Binding", Literal (quoteString (spec.Namespace + "." + spec.SafeName + "_release"))
                            "Parameter", Literal "0" ] ])
                        "Hypothesis", RecordBlock [
                            "Identity", Literal (quoteString (spec.TemplateName + "<" + spec.ElementType + ">"))
                            "Version", Literal (quoteString "1.0")
                            "Source", Literal (quoteString (prefix + "_acquire")) ] ]))
                  BlankLine ]
        [ OpaqueMarker spec.SafeName
          SubModule(spec.SafeName, [
              LiteralBinding("StatusOk", Named "int", "0")
              LiteralBinding("StatusNullHandle", Named "int", "-1")
              LiteralBinding("StatusStdException", Named "int", "1")
              LiteralBinding("StatusUnknownException", Named "int", "2")
              LiteralBinding("StatusAllocationFailed", Named "int", "3")
              LiteralBinding("StatusOutOfRange", Named "int", "4") ])
          BlankLine ] @
        binding "_release" "CalleeOwns" [ self ] (Named "unit") "Void" @ acquisition @
        binding "_size" "Borrowed" [ output "outSize" "int"; self ] (Named "int") intRef @
        binding "_get" "Borrowed" [ output "outElem" spec.ElementClefType; self; { Name = "index"; Type = Named "int" }, intRef ] (Named "int") intRef @
        binding "_iter_next" "Borrowed" [ output "outElem" spec.ElementClefType; output "inoutIndex" "int"; self ] (Named "int") intRef

    let generateCppTemplateContainerModule ctx inventory (spec: CppClassAnalysis.CppTemplateContainerSpec) =
        let declarations = generateCppTemplateContainerDecls ctx inventory spec
        CodeRenderer.render (Module(spec.Namespace, "Opaque C++ template container operations", OpenModule "BAREWire.Descriptors" :: BlankLine :: declarations))

    /// Generate Fidelity.Platform and BAREWire zero-copy shared buffer declarations (#123).
    let generateSharedBufferDecls (ctx: GenerationContext)
                                  (spec: CppClassAnalysis.SharedBufferSpec) : FsDecl list =
        CppClassAnalysis.validateSharedBufferContract ctx.StructLayouts spec
        let source = spec.Source |> Option.defaultValue (Diagnostics.Located.Unknown "No header location was supplied for the shared buffer contract.")
        let resolvedFields =
            spec.Fields |> List.map (fun field ->
                let resolved = resolveIn ctx field.CType
                match resolved with
                | Scalar r | CEnum (_, r) when clefTypeOf resolved = Named field.ClefType && (r.Bits + 7) / 8 = field.ByteWidth ->
                    field, r, DescriptorGenerator.typeRefSource r, DescriptorGenerator.reprSource r
                | _ ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.SharedBufferContractInvalid
                        $"Shared buffer '{spec.SafeName}': field '{field.Name}' C type '{field.CType}' has no admitted {field.ByteWidth}-byte scalar representation as Clef type '{field.ClefType}'." source))
        let pointer = DescriptorGenerator.typeRefSource (TypeMapper.pointerRepr ctx.DataModel "void *")
        let intRef = abiReprOf ctx.DataModel "int" (resolveIn ctx "int") |> Option.get |> DescriptorGenerator.typeRefSource
        let handle name = Generic("CHandle", Named name)
        let self = { Name = "self"; Type = handle spec.SafeName }, pointer
        let output name typ = { Name = name; Type = handle typ }, pointer
        let prefix = "farscape_shared_buffer_" + spec.SafeName
        let binding suffix ownership (parameters: (FsParam * string) list) returnType returnRef =
            let name = spec.SafeName + suffix
            let symbol = prefix + suffix
            [ LetBinding(name, List.map fst parameters, returnType, NativeZeroed,
                         [ $"FidelityExtern({quoteString spec.LibraryName}, {quoteString symbol})" ])
              ValueBinding(name + "Descriptor", Generic("Expr", Named "FunctionDescriptor"),
                Quoted(RecordBlock [
                    "CName", Literal (quoteString symbol)
                    "Parameters", ArrayBlock (parameters |> List.map (fun (p, repr) -> RecordConstruction [
                        "Name", Literal (quoteString (unquoteIdentifier p.Name)); "Type", Literal repr; "PassBy", Identifier "Value" ]))
                    "ReturnType", Literal returnRef
                    "CallingConvention", Identifier "CDecl"
                    "OwnershipTransfer", Identifier ownership ]))
              BlankLine ]
        let spaceName, memoryRegionSource =
            match spec.MemorySpace, spec.SpaceAccess with
            | CppClassAnalysis.SharedMemorySpaceKind.HostShared, _ -> "Fidelity.Platform.HostShared", "MemoryRegionKind.SRAM"
            | CppClassAnalysis.SharedMemorySpaceKind.DeviceUnified, _ -> "Fidelity.Platform.DeviceUnified", "MemoryRegionKind.DMA"
            | CppClassAnalysis.SharedMemorySpaceKind.FileMapped, CppClassAnalysis.SharedAccessMode.SharedReadOnly -> "Fidelity.Platform.FileMapped", "MemoryRegionKind.Flash"
            | CppClassAnalysis.SharedMemorySpaceKind.FileMapped, _ -> "Fidelity.Platform.FileMapped", "MemoryRegionKind.SRAM"
        let accessName =
            match spec.SpaceAccess with
            | CppClassAnalysis.SharedAccessMode.SharedReadOnly -> "ReadOnly"
            | CppClassAnalysis.SharedAccessMode.SharedWriteOnly -> "WriteOnly"
            | CppClassAnalysis.SharedAccessMode.SharedReadWrite -> "ReadWrite"
            | CppClassAnalysis.SharedAccessMode.SharedExclusiveMutable -> "ExclusiveMutable"
        let barewireFieldDescriptors =
            resolvedFields |> List.map (fun (field, r, _, repr) ->
                let fieldAccess =
                    match spec.SpaceAccess with
                    | CppClassAnalysis.SharedAccessMode.SharedReadOnly -> "AccessKind.ReadOnly"
                    | CppClassAnalysis.SharedAccessMode.SharedWriteOnly -> "AccessKind.WriteOnly"
                    | CppClassAnalysis.SharedAccessMode.SharedReadWrite
                    | CppClassAnalysis.SharedAccessMode.SharedExclusiveMutable -> "AccessKind.ReadWrite"
                let doc = $"offset measured; repr {DescriptorGenerator.stratumName r.Stratum}: {r.CType} under {ctx.DataModel}"
                RecordConstruction [
                    "Name", Literal (quoteString field.Name)
                    "Offset", Literal (string field.ByteOffset)
                    "Repr", Literal repr
                    "Count", Literal "1"
                    "Access", Literal fieldAccess
                    "BitFields", Literal "[||]"
                    "Documentation", Literal ($"Some {quoteString doc}") ])
        let structDoc = $"measured from targeted library {spec.LibraryName} for {spaceName} zero-copy placement"
        let sharedLayoutBlock =
            RecordBlock [
                "Size", Literal (string spec.MeasuredSize)
                "Alignment", Literal (string spec.MeasuredAlignment)
                "Fields", ArrayBlock barewireFieldDescriptors ]
        let structDescriptor =
            Quoted(RecordBlock [
                "Name", Literal (quoteString spec.StructName)
                "Layout", sharedLayoutBlock
                "MayAlias", Literal "false"
                "FlexibleArray", Literal "None"
                "Documentation", Literal ($"Some {quoteString structDoc}") ])
        let peripheralDescriptor =
            Quoted(RecordBlock [
                "Name", Literal (quoteString spec.SafeName)
                "Instances", Literal "[||]"
                "Layout", sharedLayoutBlock
                "MemoryRegion", Literal memoryRegionSource ])
        let memorySpaceDescriptor =
            Quoted(RecordBlock [
                "Space", Literal (quoteString spaceName)
                "MemoryRegion", Literal memoryRegionSource
                "Access", Literal (quoteString accessName)
                "ByteOrder", Literal (quoteString spec.ByteOrder)
                "ElementSize", Literal (string spec.MeasuredSize)
                "ElementCount", Literal (string spec.ElementCount)
                "RegionByteExtent", Literal (string spec.RegionByteExtent)
                "RegionAlignment", Literal (string spec.RegionAlignment)
                "RegionLifetimeDepth", Literal (string spec.RegionLifetimeDepth)
                "LibraryRetentionDepth", Literal (string spec.LibraryRetentionDepth) ])
        let offsetLiterals =
            spec.Fields |> List.map (fun field -> LiteralBinding(field.Name + "Offset", Named "int", string field.ByteOffset))
        let fieldBindings =
            resolvedFields |> List.collect (fun (field, _, fieldRepr, _) ->
                binding ("_clef_read_" + field.Name) "Borrowed"
                    [ output "outValue" field.ClefType; self; { Name = "index"; Type = Named "int" }, intRef ]
                    (Named "int") intRef
                @ binding ("_clef_write_" + field.Name) "Borrowed"
                    [ self; { Name = "index"; Type = Named "int" }, intRef; { Name = "value"; Type = Named field.ClefType }, fieldRepr ]
                    (Named "int") intRef)
        let mapParameters =
            match spec.MemorySpace with
            | CppClassAnalysis.FileMapped -> [ { Name = "fd"; Type = Named "int" }, intRef ]
            | CppClassAnalysis.HostShared
            | CppClassAnalysis.DeviceUnified -> []
        [ OpaqueMarker spec.SafeName
          SubModule(spec.SafeName,
              [ LiteralBinding("Size", Named "int", string spec.MeasuredSize)
                LiteralBinding("Alignment", Named "int", string spec.MeasuredAlignment)
                LiteralBinding("ElementCount", Named "int", string spec.ElementCount)
                LiteralBinding("RegionByteExtent", Named "int", string spec.RegionByteExtent)
                LiteralBinding("RegionAlignment", Named "int", string spec.RegionAlignment)
                LiteralBinding("MemorySpace", Named "string", quoteString spaceName)
                LiteralBinding("SpaceAccess", Named "string", quoteString accessName)
                LiteralBinding("ByteOrder", Named "string", quoteString spec.ByteOrder) ]
              @ offsetLiterals
              @ [ LiteralBinding("StatusOk", Named "int", "0")
                  LiteralBinding("StatusNullHandle", Named "int", "-1")
                  LiteralBinding("StatusOutOfRange", Named "int", "4")
                  LiteralBinding("StatusLifetimeError", Named "int", "5")
                  LiteralBinding("StatusAccessConflict", Named "int", "6")
                  ValueBinding("Descriptor", Generic("Expr", Named "StructDescriptor"), structDescriptor)
                  ValueBinding("PeripheralDescriptor", Generic("Expr", Named "PeripheralDescriptor"), peripheralDescriptor)
                  ValueBinding("MemorySpaceDescriptor", Generic("Expr", Named "MemorySpaceDescriptor"), memorySpaceDescriptor) ])
          BlankLine ]
        @ binding "_unmap" "CalleeOwns" [ self ] (Named "int") intRef
        @ [ ValueBinding(spec.SafeName + "_mapResourceDescriptor", Generic("Expr", Named "ResourceDescriptor"),
              Quoted(RecordBlock [
                  "Resource", FunctionCall("", "OwnedNullableReturn", [ RecordConstruction [
                      "Binding", Literal (quoteString (spec.Namespace + "." + spec.SafeName + "_unmap"))
                      "Parameter", Literal "0" ] ])
                  "Hypothesis", RecordBlock [
                      "Identity", Literal (quoteString spec.StructName)
                      "Version", Literal (quoteString "1.0")
                      "Source", Literal (quoteString (prefix + "_map")) ] ]))
            BlankLine ]
        @ binding "_map" "CallerOwns" mapParameters (Generic("option", handle spec.SafeName)) pointer
        @ binding "_share_to_library" "Borrowed"
            [ self; { Name = "accessMode"; Type = Named "int" }, intRef; { Name = "elementCount"; Type = Named "int" }, intRef ]
            (Named "int") intRef
        @ binding "_unshare_from_library" "Borrowed" [ self ] (Named "int") intRef
        @ binding "_library_transform_in_place" "Borrowed"
            [ self; { Name = "index"; Type = Named "int" }, intRef; { Name = "delta"; Type = Named "int" }, intRef ]
            (Named "int") intRef
        @ binding "_clef_address_identity" "Borrowed" [ output "outAddress" "int"; self ] (Named "int") intRef
        @ binding "_library_address_identity" "Borrowed" [ output "outAddress" "int"; self ] (Named "int") intRef
        @ binding "_copy_count" "Borrowed" [ output "outCopies" "int"; self ] (Named "int") intRef
        @ binding "_memory_space_kind" "Borrowed" [ output "outSpaceKind" "int"; self ] (Named "int") intRef
        @ fieldBindings

    let generateSharedBufferModule (ctx: GenerationContext) (spec: CppClassAnalysis.SharedBufferSpec) =
        let declarations = generateSharedBufferDecls ctx spec
        CodeRenderer.render (
            Module(
                spec.Namespace,
                "Fidelity.Platform and BAREWire zero-copy shared buffer operations",
                OpenModule "BAREWire.Hardware" :: OpenModule "BAREWire.Descriptors" :: BlankLine :: declarations))

    /// Marshaled class surface. C1/D1 identify the native operations; owned allocation
    /// and deallocation are realized by the companion native storage adapter.
    let private generateCppClassDecls (ctx: GenerationContext) namespace' libraryName
                                      (spec: CppClassSpec) (c: CppParser.ClassDecl) =
        CppClassAnalysis.validateMarshaledClass spec c
        let safe (cls: CppParser.ClassDecl) = (cls.QualifiedName |> Option.defaultValue cls.Name).Replace("::", "_")
        let safeName = safe c
        let refuse reason =
            Diagnostics.diagnostic (CppClassAnalysis.marshaledClassShapeCode c)
                $"C++ class '{c.Name}': {reason}" c.Location.Location |> Diagnostics.refuse
        let pointer = DescriptorGenerator.typeRefSource (TypeMapper.pointerRepr ctx.DataModel "void *")
        let handle name = Generic("CHandle", Named name)
        let mutable stdlibHandles = Map.empty<string, int * int>
        let rec scalarOrPointer (cType: string) =
            let stdlibRefusal () =
                Diagnostics.diagnostic Diagnostics.RetentionInvalid
                    $"C++ class '{c.Name}': standard-library parameter type '{cType}' has no admitted marshaled contract."
                    c.Location.Location |> Diagnostics.refuse
            let isPointer = cType.Contains("&") || cType.Contains("*")
            let cleanPointee = cType.Replace("const ", "").Replace("volatile ", "").Replace("class ", "").Replace("struct ", "").Replace("&", "").Replace("*", "").Trim()
            let shortName (name: string) = name.Split("::") |> Array.last
            let orShort name map = Map.tryFind name map |> Option.orElseWith (fun () -> Map.tryFind (shortName name) map)
            match CppClassAnalysis.tryResolveStdlibParameterType cType with
            | Some (stdKey, safeStdName, stdSize, stdAlign) ->
                match ctx.CppClasses.TryFind stdKey |> Option.orElseWith (fun () -> ctx.CppClasses.TryFind safeStdName) with
                | Some contract when contract.Bind && (contract.SizeOverride.IsNone || contract.SizeOverride = Some stdSize) && isPointer ->
                    stdlibHandles <- stdlibHandles.Add(safeStdName, (stdSize, stdAlign))
                    handle safeStdName, pointer
                | _ -> stdlibRefusal ()
            | None when cType.Contains("std::") -> stdlibRefusal ()
            | None when isPointer && (orShort cleanPointee ctx.KnownClasses).IsSome ->
                handle (safe (orShort cleanPointee ctx.KnownClasses).Value), pointer
            | None ->
            let cleanEnum = cType.Replace("const ", "").Replace("volatile ", "").Replace("enum ", "").Trim()
            match (if cType.Contains("::") && not isPointer then orShort cleanEnum ctx.Enums else None) with
            | Some e ->
                match e.Representation, e.UnderlyingType with
                | CppParser.ClangMeasured _, _ ->
                    let declaredScalar ut = match resolveIn ctx ut with Scalar repr -> Some repr | _ -> None
                    Named e.Name, DescriptorGenerator.typeRefSource (enumRepr ctx.DataModel declaredScalar e)
                | _, Some ut -> scalarOrPointer ut
                | _ -> refuse $"type '{cType}' has no measured enum representation."
            | None ->
            // C++ references are pointer arguments at this boundary.
            let resolved = resolveIn ctx (cType.Replace("&", "*"))
            match resolved with
            | Scalar _ | CEnum _ | DataPointer _ | OpaqueHandle _ | FunctionPointer _ ->
                let ty = clefTypeOf resolved
                let ty =
                    match resolved with
                    | DataPointer (Named n) ->
                        match ctx.KnownClasses.TryFind n with Some cls -> handle (safe cls) | None -> ty
                    | _ -> ty
                match abiReprOf ctx.DataModel cType resolved with
                | Some repr -> ty, DescriptorGenerator.typeRefSource repr
                | None -> refuse $"type '{cType}' has no marshaled representation."
            | _ -> refuse $"type '{cType}' has no marshaled representation."
        let parameters (f: CppParser.FunctionDecl) =
            List.zip (parameterNames f.Parameters) f.Parameters
            |> List.map (fun (name, (_, typ)) -> let ty, repr = scalarOrPointer typ in { Name = name; Type = ty }, repr)
        let descriptor name symbol ownership (parameters: (FsParam * string) list) returnRef =
            ValueBinding(name + "Descriptor", Generic("Expr", Named "FunctionDescriptor"),
                Quoted(RecordBlock [
                    "CName", Literal (quoteString symbol)
                    "Parameters", ArrayBlock (parameters |> List.map (fun (p, repr) -> RecordConstruction [
                        "Name", Literal (quoteString (unquoteIdentifier p.Name)); "Type", Literal repr; "PassBy", Identifier "Value" ]))
                    "ReturnType", Literal returnRef
                    "CallingConvention", Identifier "CDecl"
                    "OwnershipTransfer", Identifier ownership ]))
        let binding name symbol ownership parameters returnType returnRef =
            [ LetBinding(name, parameters |> List.map fst, returnType, NativeZeroed,
                         [ $"FidelityExtern({quoteString libraryName}, {quoteString symbol})" ])
              descriptor name symbol ownership parameters returnRef
              BlankLine ]
        let size = spec.SizeOverride |> Option.defaultWith (fun () -> CppClassAnalysis.estimateClassSize c)
        let align =
            match c.Measured with
            | Some m ->
                let measuredAlign = max 1 (m.AlignmentBits / 8)
                if CppClassAnalysis.usesStorageAdapter spec && size >= 16 then max 8 measuredAlign else measuredAlign
            | None -> 8
        let shimmed = CppClassAnalysis.usesNativeShim spec
        let shimPrefix = "farscape_cpp_" + safeName
        let _, intRepr = scalarOrPointer "int"
        let self = { Name = "self"; Type = handle safeName }, pointer
        let dtorName = safeName + "_dtor"
        let dtorSymbol = if shimmed then shimPrefix + "_dtor" else c.DestructorMangledName.Value
        let dtors = binding dtorName dtorSymbol "CalleeOwns" [ self ] (Named "unit") "Void"
        let errorDecls =
            if not shimmed then []
            else
                let errMarker = safeName + "_Error"
                let errParam = { Name = "err"; Type = handle errMarker }, pointer
                let errDtorName = safeName + "_error_dtor"
                let takeErrName = safeName + "_take_error"
                let takeErrSymbol = shimPrefix + "_take_error"
                OpaqueMarker errMarker ::
                binding errDtorName (shimPrefix + "_error_dtor") "CalleeOwns" [ errParam ] (Named "unit") "Void" @
                binding takeErrName takeErrSymbol "CallerOwns" [] (Generic("option", handle errMarker)) pointer @
                [ ValueBinding(takeErrName + "ResourceDescriptor", Generic("Expr", Named "ResourceDescriptor"),
                    Quoted(RecordBlock [
                        "Resource", FunctionCall("", "OwnedNullableReturn", [ RecordConstruction [
                            "Binding", Literal (quoteString (namespace' + "." + errDtorName))
                            "Parameter", Literal "0" ] ])
                        "Hypothesis", RecordBlock [
                            "Identity", Literal (quoteString ((c.QualifiedName |> Option.defaultValue c.Name) + "::error"))
                            "Version", Literal (quoteString "1.0")
                            "Source", Literal (quoteString takeErrSymbol) ] ]))
                  BlankLine ] @
                binding (safeName + "_error_code") (shimPrefix + "_error_code") "Borrowed" [ errParam ] (Named "int") intRepr @
                binding (safeName + "_error_len") (shimPrefix + "_error_len") "Borrowed" [ errParam ] (Named "int") intRepr @
                binding (safeName + "_error_byte") (shimPrefix + "_error_byte") "Borrowed"
                    [ errParam; { Name = "index"; Type = Named "int" }, intRepr ] (Named "int") intRepr
        let constructors = c.Constructors |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        let ctors =
            if shimmed && c.IsAbstract then []
            else
                constructors |> List.mapi (fun index f ->
                    let suffix = if index = 0 then "" else "_" + string index
                    let name = safeName + "_ctor" + suffix
                    let symbol = if shimmed then shimPrefix + "_ctor" + suffix else f.MangledName.Value
                    binding name symbol "CallerOwns" (parameters f) (Generic("option", handle safeName)) pointer @
                    [ ValueBinding(name + "ResourceDescriptor", Generic("Expr", Named "ResourceDescriptor"),
                        Quoted(RecordBlock [
                            "Resource", FunctionCall("", "OwnedNullableReturn", [ RecordConstruction [
                                "Binding", Literal (quoteString (namespace' + "." + dtorName))
                                "Parameter", Literal "0" ] ])
                            "Hypothesis", RecordBlock [
                                "Identity", Literal (quoteString (c.QualifiedName |> Option.defaultValue c.Name))
                                "Version", Literal (quoteString "1.0")
                                "Source", Literal (quoteString symbol) ] ])) ]) |> List.concat
        let upcastDecls =
            if not shimmed then []
            else
                c.BaseClasses
                |> List.choose (fun baseName ->
                    let cleanBase = baseName.Replace("class ", "").Replace("struct ", "").Trim()
                    ctx.KnownClasses.TryFind cleanBase)
                |> List.collect (fun baseCls ->
                    let baseSafe = safe baseCls
                    let upcastName = safeName + "_as_" + baseSafe
                    let upcastSymbol = shimPrefix + "_as_" + baseSafe
                    binding upcastName upcastSymbol "Borrowed" [ self ] (Generic("option", handle baseSafe)) pointer)
        let methods = c.Methods |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        let methodDecls = methods |> List.mapi (fun index f ->
            let overloads = methods |> List.filter (fun other -> other.Name = f.Name)
            let suffix = if overloads.Length > 1 then "_" + string index else ""
            let name = safeName + "_" + stripReservedPrefix f.Name + suffix
            let symbol = if shimmed then shimPrefix + "_" + f.Name + suffix else f.MangledName.Value
            let info = CppClassAnalysis.analyzeMethodReturn ctx.KnownClasses f
            let args = parameters f
            let receivers = if f.IsStatic then [] else [ self ]
            let isThrowing = shimmed && not (CppClassAnalysis.hasNoExcept f)
            match info.ReturnConvention, info.SretTypeName with
            | CppClassAnalysis.SretReturn, Some retName ->
                let retClass = ctx.KnownClasses[retName]
                let marker = safe retClass
                if isThrowing then
                    OpaqueMarker marker :: binding name symbol "Borrowed"
                        (({ Name = "outResult"; Type = handle marker }, pointer) :: receivers @ args) (Named "int") intRepr
                else
                    OpaqueMarker marker :: binding name symbol "Borrowed"
                        (({ Name = "retStorage"; Type = handle marker }, pointer) :: receivers @ args) (Named "unit") "Void"
            | _ ->
                let resultName = f.ReturnType.Replace("const ", "").Trim()
                let resultClass = if f.ReturnType.Contains("*") || f.ReturnType.Contains("&") then None else ctx.KnownClasses.TryFind resultName
                let valueRecord (record: CppParser.StructDecl) =
                    if record.Measured.IsNone then refuse $"by-value result '{f.ReturnType}' requires measured size and alignment."
                    if record.Fields |> List.exists (fun field -> field.IsArray || field.IsBitfield) then
                        refuse $"by-value result '{f.ReturnType}' has unsupported fields."
                    for field in record.Fields do scalarOrPointer field.Type |> ignore
                    generateStructDecl { ctx with ValueStructs = ctx.ValueStructs.Add record.Name } record,
                    Named record.Name, "Named " + quoteString record.Name
                let extra, retType, returnRef =
                    match resultClass with
                    | Some cls ->
                        let marker = safe cls
                        let record : CppParser.StructDecl =
                            { Name = marker + "Value"; Fields = cls.Fields; Documentation = cls.Documentation
                              IsUnion = false; Attributes = []; Anonymous = None; Measured = cls.Measured
                              Unattributed = None; UnnamedBitFields = cls.UnnamedBitFields
                              TypedefAttributes = cls.TypedefAttributes; Location = cls.Location }
                        let decls, typ, repr = valueRecord record
                        OpaqueMarker marker :: decls, typ, repr
                    | None ->
                        match ctx.Structs.TryFind resultName with
                        | Some record -> valueRecord record
                        | None -> let typ, repr = scalarOrPointer f.ReturnType in [], typ, repr
                if isThrowing then
                    if f.ReturnType.Trim() = "void" then
                        extra @ binding name symbol "Borrowed" (receivers @ args) (Named "int") intRepr
                    else
                        let outParam = { Name = "outResult"; Type = Generic("CHandle", retType) }, pointer
                        extra @ binding name symbol "Borrowed" (outParam :: receivers @ args) (Named "int") intRepr
                else
                    extra @ binding name symbol "Borrowed" (receivers @ args) retType returnRef) |> List.concat
        let moduleConstants =
            [ LiteralBinding("Size", Named "int", string size); LiteralBinding("Alignment", Named "int", string align) ] @
            (if shimmed then
                [ LiteralBinding("StatusOk", Named "int", "0")
                  LiteralBinding("StatusNullHandle", Named "int", "-1")
                  LiteralBinding("StatusStdException", Named "int", "1")
                  LiteralBinding("StatusUnknownException", Named "int", "2")
                  LiteralBinding("StatusAllocationFailed", Named "int", "3") ]
             else [])
        [ OpaqueMarker safeName
          SubModule(safeName, moduleConstants) ]
        @ (stdlibHandles |> Map.toList |> List.collect (fun (name, (size, align)) ->
            [ OpaqueMarker name; SubModule(name, [LiteralBinding("Size", Named "int", string size); LiteralBinding("Alignment", Named "int", string align)]) ]))
        @ dtors @ errorDecls @ ctors @ upcastDecls @ methodDecls

    // =========================================================================
    // Catamorphism-Based Generation
    // =========================================================================

    /// Declaration group: produced by catamorphism, consumed by assembler.
    type private DeclGroup =
        | GEnum of FsDecl list
        | GStruct of FsDecl list
        | GFunc of CppParser.FunctionDecl
        | GMacro of FsDecl list
        | GUnemittedMacro of UnemittedMacro
        | GNone

    /// Generation algebra: maps each Declaration variant to a DeclGroup.
    /// This is the SINGLE traversal of declarations for code generation.
    /// All context (typedef map, ABI model, opaque handles, struct layouts, C++ class map) is captured in the closure.
    let private generationAlgebra
        (ctx: GenerationContext) (namespace': string) (libraryName: string)
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
            match macroDisposition m with
            | EmittedConstant decls -> GMacro decls
            | NotEmitted u -> GUnemittedMacro u
        OnTypedef = fun td ->
            if ctx.Bindings.IsEmpty then GNone
            else
                match resolveIn (inUnitOf "typedef" td.Name ctx) td.UnderlyingType with
                | Scalar r when r.Bits > 0 ->
                    let size = if r.Family = TypeMapper.Float && r.Bits = 80 then 16 else (r.Bits + 7) / 8
                    GStruct [
                        Comment $"/// Storage for C typedef `{td.Name}`; {DescriptorGenerator.widthProvenance ctx.DataModel r}."
                        SubModule(td.Name, [LiteralBinding("Size", Named "int", string size); LiteralBinding("Alignment", Named "int", string size)])
                        BlankLine ]
                | _ -> GNone
        OnNamespace = fun _ -> GNone
        OnClass = fun c ->
            if c.Name = "" then GNone
            else
            let rec enclosedByClass (visited: Set<string>) (s: CppParser.StructDecl) =
                match s.Anonymous with
                | Some origin when origin.Parent = c.Name -> true
                | Some origin when not (visited.Contains origin.Parent) ->
                    ctx.Structs.TryFind origin.Parent |> Option.exists (enclosedByClass (visited.Add origin.Parent))
                | _ -> false
            let enclosedAnonymous =
                ctx.Structs.Values |> Seq.filter (enclosedByClass Set.empty) |> List.ofSeq
            match enclosedAnonymous |> List.tryFind (fun s -> s.Measured.IsNone) with
            | Some s ->
                let origin = s.Anonymous.Value
                Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.AnonymousRecordUnmeasured $"anonymous record '{s.Name}' ({CppParser.describeOrigin origin}, declared at {origin.Location}) has no clang-measured layout in its declaring unit; its layout is never computed from its declared fields." s.Location.Location)
            | None -> ()
            // A C++ record enclosing an anonymous record (#177), carrying an unnamed bit-field, or
            // having a field whose type reaches an attributed typedef has clang's layout or none, as
            // a C record does (#182).
            if c.Measured.IsNone then
                match enclosedAnonymous with
                | s :: _ ->
                    let origin = s.Anonymous.Value
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.AnonymousRecordUnmeasured $"C++ record '{c.Name}' encloses {CppParser.describeOrigin origin} (declared at {origin.Location}), and clang has no measurement of '{c.Name}' in its declaring unit; an anonymous record's layout is never computed from its declared fields." c.Location.Location)
                | [] -> ()
                match c.UnnamedBitFields with
                | b :: _ ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.UnnamedBitFieldUnmeasured $"C++ record '{c.Name}' carries {CppParser.describeUnnamedBitField b}, which moves bytes natural alignment of the declared field types cannot place, and clang has no measurement of '{c.Name}' in its declaring unit; an unnamed bit-field is never dropped from its record." c.Location.Location)
                | [] -> ()
                match c.TypedefAttributes |> List.tryPick (fun a -> match a.Site with CppParser.FieldType field -> Some (field, a) | CppParser.RecordName -> None) with
                | Some (field, a) ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.TypedefAttributeUnmeasured $"C++ record '{c.Name}' field '{field}' has a type reaching typedef '{a.Typedef}', which carries attribute '{a.Attribute.Kind}', and clang has no measurement of '{c.Name}' in its declaring unit; natural alignment of the declared field types never stands in for the typedef's attribute." c.Location.Location)
                | None -> ()
            let admitted =
                c.QualifiedName |> Option.bind (fun name -> Map.tryFind name ctx.CppClasses)
                |> Option.orElseWith (fun () -> Map.tryFind c.Name ctx.CppClasses)
            match admitted with
            | Some spec -> GStruct (generateCppClassDecls ctx namespace' libraryName spec c)
            | None ->
                let code, detail =
                    match CppClassAnalysis.classifyClass c with
                    | CppClassAnalysis.PODClass sizeBytes ->
                        Diagnostics.CppClassAbiUnadmitted, $"C++ POD class '{c.Name}' ({sizeBytes} bytes)"
                    | CppClassAnalysis.PimplClass (implType, sizeBytes) ->
                        Diagnostics.CppPimplClassUnadmitted, $"C++ pimpl class '{c.Name}' (handle '{implType}', {sizeBytes} bytes)"
                    | CppClassAnalysis.ValueClass (sizeBytes, _) ->
                        Diagnostics.CppValueClassUnadmitted, $"C++ value class '{c.Name}' ({sizeBytes} bytes)"
                    | CppClassAnalysis.InterfaceClass ->
                        Diagnostics.CppInterfaceClassUnadmitted, $"C++ abstract interface class '{c.Name}'"
                    | CppClassAnalysis.OpaqueClass ->
                        Diagnostics.CppOpaqueClassUnadmitted, $"C++ opaque class '{c.Name}'"
                Diagnostics.refuse (Diagnostics.diagnostic code $"{detail} requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source." c.Location.Location)
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
          Anchors = Map.empty
          ValueStructs = Set.empty
          FlexibleArrays = Map.empty
          PhantomMarkers = Set.empty
          KnownClasses = CppClassAnalysis.buildClassMap declarations
          CppClasses = Map.empty
          TypedefInfos = typedefInfosOf declarations
          ByValueTypedefs = []
          // First definition wins, as the merge keeps the first declaration of a name; a
          // declaration with a recorded unit uses that unit's own macros (`withUnitFacts`).
          Macros = macrosOf declarations }

    let private prepareFunction ctx namespace' libraryName func =
        func, callbackEntries ctx namespace' func, generateFunctionDecls ctx libraryName func

    let private anchorFor ctx namespace' ((func: CppParser.FunctionDecl), entries, raw) =
        namespace' + "." + func.Name, (namespace' + "." + nativeDeclarationName ctx func entries raw, func, raw)

    /// Prepare the selected native declarations that other namespaces can reference.
    let selectedAnchors ctx namespace' libraryName declarations =
        declarations
        |> List.choose (function
            | CppParser.Declaration.Function f when not (f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")) -> Some f
            | _ -> None)
        |> List.distinctBy _.Name
        |> List.map (prepareFunction ctx namespace' libraryName >> anchorFor ctx namespace')
        |> Map.ofList

    /// Topologically order struct/record declaration groups so nested or referenced
    /// child records are emitted before dependent parent records (cycle-safe).
    let private orderStructGroups (groups: FsDecl list list) : FsDecl list =
        let groupArr = groups |> List.filter (fun g -> not g.IsEmpty) |> Array.ofList
        let rec referencedNames = function
            | Named name -> [ name ]
            | Generic (_, inner) -> referencedNames inner
            | Generic2 (_, a, b) -> referencedNames a @ referencedNames b
            | Unit -> []
            | FunctionType (args, ret) -> List.collect referencedNames (ret :: args)
        let definedNames (decls: FsDecl list) =
            let recordNames = decls |> List.choose (function RecordType (name, _, _, _) -> Some name | _ -> None)
            if not recordNames.IsEmpty then recordNames
            else decls |> List.choose (function SubModule (name, _) -> Some name | _ -> None)
        let definedIndex =
            (Map.empty, [ 0 .. groupArr.Length - 1 ])
            ||> List.fold (fun acc idx ->
                (acc, definedNames groupArr[idx])
                ||> List.fold (fun m name -> if m.ContainsKey name then m else m.Add(name, idx)))
        let dependencies (idx: int) =
            groupArr[idx]
            |> List.collect (function
                | RecordType (_, fields, _, _) -> fields |> List.collect (snd >> referencedNames)
                | _ -> [])
            |> List.choose (fun name ->
                match definedIndex.TryFind name with
                | Some depIdx when depIdx <> idx -> Some depIdx
                | _ -> None)
            |> List.distinct
        let rec visit (visiting: Set<int>) (visited: Set<int>, acc: int list) idx =
            if visited.Contains idx || visiting.Contains idx then visited, acc
            else
                let visiting' = visiting.Add idx
                let visited', acc' =
                    dependencies idx
                    |> List.fold (fun state depIdx -> visit visiting' state depIdx) (visited, acc)
                visited'.Add idx, idx :: acc'
        let _, orderedRev =
            ([ 0 .. groupArr.Length - 1 ])
            |> List.fold (fun state idx -> visit Set.empty state idx) (Set.empty, [])
        orderedRev |> List.rev |> List.collect (fun idx -> groupArr[idx])

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
            Diagnostics.refuse (nativePointerSurfaceRefusal (Diagnostics.Located.Unknown "experimental_native_pointer_surface is pilot input, not a header declaration"))
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
            DeclarationAlgebra.cataDeclarations (generationAlgebra ctx namespace' libraryName) declarations

        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let structs =
            let declarations = groups |> List.choose (function GStruct d -> Some d | _ -> None) |> orderStructGroups
            if ctx.CppClasses.IsEmpty then declarations else List.distinct declarations
        let functionDeclarations =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
        let callbackDeclarations =
            functionDeclarations |> List.collect (callbackEntries ctx namespace')
            |> List.distinctBy (fun (_, name, _) -> name)
            |> List.collect (fun (_, _, declarations) -> declarations)
        let prepared = functionDeclarations |> List.map (prepareFunction ctx namespace' libraryName)
        let anchors = prepared |> List.map (anchorFor ctx namespace') |> List.fold (fun acc (key, value) -> Map.add key value acc) ctx.Anchors
        let functions = prepared |> List.collect (fun (func, entries, raw) -> declaredFunctionDecls ctx namespace' (Some anchors) func entries raw)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])

        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]
        // Every macro of this module emitted as no constant, by name and kind (#193).
        let unemittedSection =
            match groups |> List.choose (function GUnemittedMacro u -> Some u | _ -> None) with
            | [] -> []
            | unemitted ->
                Comment "// Macros emitted as no constant (name: kind)"
                :: (unemitted |> List.map (fun u -> Comment $"//   {u.Name}: {describeUnemittedMacroKind u.Kind}"))
                @ [BlankLine]

        let vocabularyOpens =
            [ if not structs.IsEmpty then "BAREWire.Hardware"
              if not functions.IsEmpty || not ctx.CppClasses.IsEmpty then "BAREWire.Descriptors" ]
        let openDecls =
            match (openModules @ vocabularyOpens) |> List.distinct with
            | [] -> []
            | opens -> (opens |> List.map OpenModule) @ [BlankLine]
        let allDecls = openDecls @ opaqueHandleDecls @ markerDecls @ enums @ structs @ callbackDeclarations @ functions @ macroSection @ unemittedSection
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
