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
/// layout module plus a BAREWire `StructDescriptor` (docs/14 §2), never a record.
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
                { Name = ""; UnderlyingType = underlyingType; Documentation = None }
            ActivePatterns.isOpaqueHandleTypedef knownStructNames td)
        |> List.map fst
        |> Set.ofList

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

    /// The ABI representation of an enum: its underlying type when clang reports one (declared),
    /// else the profile's int, unsigned unless an enumerator is negative (inferred: C leaves the
    /// choice to the implementation).
    let private enumRepr (model: PlatformABI) (e: CppParser.EnumDecl) : TypeMapper.AbiRepr =
        match e.UnderlyingType |> Option.bind (TypeMapper.tryScalar model) with
        | Some r -> { r with CType = $"enum {e.Name} : {r.CType}" }
        | None ->
            let negative = e.Values |> List.exists (fun v -> v.Value < 0L)
            { Family = (if negative then TypeMapper.Signed else TypeMapper.Unsigned)
              Bits = TypeMapper.intWidth model
              Stratum = TypeMapper.Inferred
              CType = $"enum {e.Name} without a declared underlying type" }

    /// Resolve a C type string once. Opaque handles keep their names; delegates and function
    /// pointers become FnPtr signatures with nullable-by-default native data values; data
    /// pointers become CHandle of their pointee; scalars come from the one-kind table, through the
    /// typedef map when the spelling is not in it; enums carry their underlying representation.
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
                failwith $"Nullable function-pointer contract '{originalType}' requires a supported boundary adapter; it cannot be represented as a nonnull native entry."
            let nativeValueType typ =
                let resolved = recur typ
                let nonnull = nullabilityIn typedefMap "_Nonnull" typ
                let nullable = nullabilityIn typedefMap "_Nullable" typ
                if nonnull && nullable then
                    failwith $"Callback value '{typ}' has contradictory native nullability evidence."
                match resolved with
                | DataPointer _ | OpaqueHandle _ when not nonnull -> Generic("option", clefTypeOf resolved)
                | Scalar r when r.Family = TypeMapper.Pointer && not nonnull -> Generic("option", clefTypeOf resolved)
                | FunctionPointer _ when not nonnull ->
                    failwith $"Nested native function-pointer value '{typ}' requires explicit nonnull evidence; nullable callback values require a supported boundary adapter."
                | _ -> clefTypeOf resolved
            FunctionPointer (parameters |> List.map nativeValueType, nativeValueType ret)
        // A pointer's pointee: a scalar's spelling, a named type, or what a typedef stands for
        let pointee (baseType: string) : FsType =
            match TypeMapper.tryScalar model baseType with
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
            match TypeMapper.tryScalar model baseType with
            | Some r -> Scalar r
            | None ->
                let name = TypeMapper.cleanTypeName baseType
                match Map.tryFind name enums with
                | Some e -> CEnum (name, enumRepr model e)
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
        PhantomMarkers: Set<string>
    }

    /// Resolve a C type under the context.
    let private resolveIn (ctx: GenerationContext) (cType: string) : ResolvedCType =
        resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums cType

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

    /// The `TypeRef` source and the provenance note of one parameter or return.
    let private abiClaim (ctx: GenerationContext) (cType: string) : string * string =
        let resolved = resolveIn ctx cType
        match abiReprOf ctx.DataModel cType resolved with
        | Some r -> DescriptorGenerator.typeRefSource r, DescriptorGenerator.widthProvenance ctx.DataModel r
        | None ->
            let name = match resolved with Unresolved n -> n | _ -> cType
            $"Named {quoteString name}", $"width unknown: no representation for {name}"

    /// The Layer 2 `Expr<FunctionDescriptor>` quotation beside an extern (platform-bindings.md,
    /// "Binding Libraries"): the C name, each parameter's and the return's ABI representation
    /// (family and bits under the profile) and passing, the calling convention, and ownership.
    /// Each width fact carries its stratum as a trailing comment; the spec's record has no
    /// provenance field.
    let private generateDescriptorDecls (ctx: GenerationContext) (func: CppParser.FunctionDecl) : FsDecl list =
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

    /// Generate FsDecl list for a single function binding, followed by its descriptor.
    /// Pointer parameters are nullable (Option<>) by default unless proven non-null via:
    /// 1. Clang NonNullAttr — parameter indices explicitly marked non-null
    /// 2. Pilot TOML [annotations.nonnull] — developer-asserted non-null
    let generateFunctionDecls (ctx: GenerationContext) (libraryName: string) (func: CppParser.FunctionDecl) : FsDecl list =
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

        // Map parameters with nullability awareness
        let parameters =
            func.Parameters
            |> List.mapi (fun idx (name, cType) ->
                let reference, _ = referencePassing projection idx cType
                let element = projection |> Option.bind (fun b -> List.tryItem idx b.ReferenceElements) |> Option.filter ((<>) "")
                let stringParameter = projection |> Option.exists (fun b -> List.contains idx b.StringParameters)
                let callbackNonnull = projection |> Option.exists (fun b -> List.contains idx b.NonnullCallbacks)
                let handle = projection |> Option.bind (fun b -> List.tryItem idx b.ParameterHandles) |> Option.filter ((<>) "")
                let provenNonnull = nonnullIndices.Contains idx || outerNullabilityIn ctx "_Nonnull" cType || callbackNonnull
                if provenNonnull && outerNullabilityIn ctx "_Nullable" cType then
                    failwith $"{func.Name} parameter {idx}: nullable native type conflicts with a nonnull contract"
                match resolveIn ctx cType with
                | FunctionPointer _ when not provenNonnull ->
                    failwith $"{func.Name} parameter {idx}: a native function-pointer parameter requires explicit nonnull evidence; nullable callback parameters require a supported boundary adapter."
                | _ -> ()
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
                            | _ -> failwith $"{func.Name} parameter {idx}: Reference requires a scalar or opaque pointer cell"
                        | ParsedCType info when info.PointerDepth = 2 ->
                            let pointerType = cType.Substring(0, cType.LastIndexOf('*')).Trim()
                            let element = resolveIn ctx pointerType |> clefTypeOf |> wrapOption
                            Named (CodeRenderer.renderType element + " array")
                        | _ -> failwith $"{func.Name} parameter {idx}: Reference requires a scalar cell or one opaque pointer cell"
                    elif stringParameter then
                        if isCharPointerIn ctx cType then Named "string"
                        else failwith $"{func.Name} parameter {idx}: string projection requires char pointer"
                    elif handle.IsSome then
                        if not (isDataPointerIn ctx cType) then failwith $"{func.Name} parameter {idx}: handle projection requires a C data pointer"
                        Generic("CHandle", Named handle.Value)
                    elif callbackNonnull then
                        match mapType cType with
                        | Generic("FnPtr", _) as ty -> ty
                        | _ -> failwith $"{func.Name} parameter {idx}: nonnull_callbacks requires a C function pointer"
                    else mapType cType
                let isNullable = not reference && isDataPointerIn ctx cType && not provenNonnull
                let finalType = if isNullable then wrapOption fsType else fsType
                { FsParam.Name = cleanParamName name; Type = finalType })

        // Return type: nullable unless proven nonnull
        let returnIsPointer = isDataPointerIn ctx func.ReturnType
        let returnNonnull =
            nonnullAnnotations
            |> Option.map (fun a -> a.Returns.Contains func.Name)
            |> Option.defaultValue false
        let hasReturnsNonnullAttr =
            func.Attributes |> List.exists (fun a -> a.Kind = "ReturnsNonNullAttr")
        let provenReturnNonnull = returnNonnull || hasReturnsNonnullAttr || outerNullabilityIn ctx "_Nonnull" func.ReturnType
        if provenReturnNonnull && outerNullabilityIn ctx "_Nullable" func.ReturnType then
            failwith $"{func.Name}: nullable native result conflicts with a nonnull contract"
        match resolveIn ctx func.ReturnType with
        | FunctionPointer _ when not provenReturnNonnull ->
            failwith $"{func.Name}: a native function-pointer result requires explicit nonnull evidence; nullable callback results require a supported boundary adapter."
        | _ -> ()
        let returnType =
            match projection |> Option.bind (fun b -> b.ReturnHandle) with
            | Some name ->
                if not (isDataPointerIn ctx func.ReturnType) then failwith $"{func.Name}: return_handle requires a C data pointer"
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
                    | _ -> failwith $"{func.Name} parameter {index}: constant projection requires an integer literal and scalar integer parameter")
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
                    Some (XmlDoc $"Callback parameter `{name}` nonnull is an external execution hypothesis from {evidence}; it is not a proof of foreign behavior.")
                | _ -> None) |> List.choose id
        formatDocDecls { func with Name = symbol } @ callbackNotes @
        [ LetBinding(nativeName, parameters, finalReturnType, NativeZeroed,
                     [$"FidelityExtern(\"{libraryName}\", \"{symbol}\")"]) ] @
        generateDescriptorDecls descriptorContext nativeFunction @ wrapper

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
        func.Parameters |> List.mapi (fun index (name, cType) ->
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
                index, recordName, declarations)) |> List.choose id

    /// Render a supplied result promise against the exact emitted declaration.
    /// ABI widths and placeholder bodies provide no behavioral contract.
    let private resultRangeDecls (ctx: GenerationContext) namespace' (func: CppParser.FunctionDecl) nativeName hasAdapter =
        match Map.tryFind func.Name ctx.Bindings |> Option.bind _.ResultRange with
        | None -> []
        | Some range ->
            let refuse reason = failwith $"{func.Name} result_range: {reason}"
            for name, value in ["identity", range.Identity; "version", range.Version; "source", range.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse $"requires nonempty {name}"
            let projection = ctx.Bindings[func.Name]
            if hasAdapter || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) then
                refuse "requires an ordinary untranslated native binding"
            let integer cType =
                match resolveIn ctx cType with
                | Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned -> true
                | _ -> false
            if not (integer func.ReturnType) then refuse "requires a scalar integer native result"
            let endpoint = function
                | ResultRangeBound.Constant value -> Literal (if value < 0 then $"Constant ({value})" else $"Constant {value}")
                | ResultRangeBound.Argument index ->
                    if index < 0 || index >= func.Parameters.Length then refuse $"argument {index} is outside native arity {func.Parameters.Length}"
                    let reference, _ = referencePassing (Some projection) index (snd func.Parameters[index])
                    if reference || not (integer (snd func.Parameters[index])) then refuse $"argument {index} requires a scalar integer Value parameter"
                    Literal $"Argument {index}"
            let lower, upper = endpoint range.Lower, endpoint range.Upper
            match range.Lower, range.Upper with
            | ResultRangeBound.Constant lo, ResultRangeBound.Constant hi when lo > hi -> refuse "constant bounds are contradictory"
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
        let contracts = ctx.Bindings |> Map.tryFind func.Name |> Option.map _.CallbackInvocations |> Option.defaultValue []
        let refuse reason = failwith $"{func.Name} callback_invocations: {reason}"
        if (contracts |> List.map _.Parameter |> Set.ofList).Count <> contracts.Length then refuse "duplicate parameter indices"
        let entries = contracts |> List.map (fun contract ->
            for key, value in ["identity", contract.Identity; "version", contract.Version; "source", contract.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse $"requires nonempty {key}"
            let scopeType, scopeFields =
                match contract.Scope, contract.ExitBinding, contract.SuccessStatus with
                | "during_call", None, None -> "ScopedCallbackDescriptor", []
                | "during_call", _, _ -> refuse "during_call forbids exit_binding and success_status"
                | "process_exit", anchor, Some status ->
                    match resolveIn ctx func.ReturnType with
                    | Scalar r when r.Family = TypeMapper.Signed && r.Bits = 32 -> ()
                    | _ -> refuse "process_exit requires a signed32 registration result"
                    let anchorValue =
                        match anchor with
                        | None -> Identifier "None"
                        | Some selected ->
                            match Map.tryFind selected anchors with
                            | Some (actual, _, _) -> Literal ("Some " + quoteString actual)
                            | None -> refuse "exit_binding must name a selected qualified generated declaration"
                    "ProcessExitCallbackDescriptor", ["ExitBinding", anchorValue; "SuccessStatus", Literal (string status)]
                | "process_exit", _, None -> refuse "process_exit requires signed32 success_status"
                | _ -> refuse "scope must be during_call or process_exit"
            if contract.Parameter < 0 || contract.Parameter >= func.Parameters.Length then
                refuse $"parameter {contract.Parameter} is outside native arity {func.Parameters.Length}"
            let projection = ctx.Bindings[func.Name]
            if not projection.ReferenceParameters.IsEmpty || not projection.ReadOnlyReferenceParameters.IsEmpty
               || not projection.StringParameters.IsEmpty || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) then
                refuse "requires an untranslated native parameter list"
            let name, cType = func.Parameters[contract.Parameter]
            match resolveIn ctx cType with
            | FunctionPointer _ -> ()
            | _ -> refuse $"parameter {contract.Parameter} is not a native function pointer"
            let ret, parameters = resolveCallbackNativeSignature ctx cType |> Option.defaultWith (fun () -> refuse "native entry signature is unavailable")
            if parameters.IsEmpty then
                let declaredType =
                    Map.tryFind (TypeMapper.cleanTypeName (stripNullability cType)) ctx.TypedefMap
                    |> Option.defaultValue cType
                match splitFunctionPointerPrototype declaredType with
                | Some (_, [], true) -> ()
                | _ -> refuse "zero-argument native entries require an explicit (void) prototype"
                match resolveIn ctx ret with
                | Scalar r when r.Family = TypeMapper.Void -> ()
                | _ -> refuse "zero-argument native entries require a void result"
            if contract.Scope = "process_exit" then
                match parameters, resolveIn ctx ret with
                | [], Scalar r when r.Family = TypeMapper.Void -> ()
                | _ -> refuse "process_exit requires a zero-argument void callback"
            let nativeType isReturn (cType: string) =
                if cType.Contains('&') then refuse $"unsupported native entry reference signature type '{cType}'"
                match resolveIn ctx cType with
                | Scalar r when (r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned) && (r.Bits = 32 || r.Bits = 64) -> ()
                | Scalar r when r.Family = TypeMapper.Bool ->
                    refuse "Boolean native entry signatures are outside the admitted ordered integer callback profile"
                | Scalar r when isReturn && r.Family = TypeMapper.Void -> ()
                | _ -> refuse $"unsupported native entry signature type '{cType}'"
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
                    "Binding", Literal (quoteString binding)
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
                    "Scope", Literal (quoteString (namespace' + "." + scopeName)) ])) ])
        entries |> List.map (fun (index, binding, _) -> index, binding) |> Map.ofList,
        entries |> List.collect (fun (_, _, declarations) -> declarations)

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

    /// Keep the full native arity private while carrying declared entry provenance through a
    /// record projection. This is membrane plumbing, not the application callback interface.
    let private nativeDeclarationName (func: CppParser.FunctionDecl) (entries: (int * string * FsDecl list) list) (raw: FsDecl list) =
        let name = raw |> List.pick (function LetBinding(name, _, _, _, attrs) when not attrs.IsEmpty -> Some name | _ -> None)
        if not (List.isEmpty entries) && name = func.Name then name + "Native" else name

    /// Consume the same selected declaration account as callback anchors. Native
    /// representation stays in its original descriptor; this quotation adds a promise.
    let private resourceDecls (ctx: GenerationContext) namespace' (anchors: Map<string, string * CppParser.FunctionDecl * FsDecl list>) (func: CppParser.FunctionDecl) nativeName raw =
        match ctx.Bindings |> Map.tryFind func.Name |> Option.bind _.Resource with
        | None -> []
        | Some resource ->
            let refuse reason = failwith $"{func.Name} resource: {reason}"
            for key, value in ["identity", resource.Identity; "version", resource.Version; "source", resource.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse $"requires nonempty {key}"
            if resource.ReleaseParameter <> 0 then refuse "owned_nullable_return requires release_parameter 0"
            let signature declarations =
                declarations |> List.pick (function
                    | LetBinding(_, parameters, result, _, attrs) when not attrs.IsEmpty -> Some (parameters, result)
                    | _ -> None)
            let marker =
                match signature raw |> snd with
                | Generic("option", Generic("CHandle", Named marker)) when isDataPointerIn ctx func.ReturnType -> marker
                | _ -> refuse "owned_nullable_return requires a native data pointer returned as option<CHandle<marker>>"
            let releaseName, release, releaseRaw =
                anchors |> Map.tryFind resource.ReleaseBinding
                |> Option.defaultWith (fun () -> refuse "release_binding must name an exact selected qualified generated declaration")
            let parameters, result = signature releaseRaw
            match release.Parameters, parameters, result, resolveIn ctx release.ReturnType with
            | [_, typ], [parameter], Named "unit", Scalar repr
                when repr.Family = TypeMapper.Void && isDataPointerIn ctx typ
                     && parameter.Type = Generic("CHandle", Named marker) -> ()
            | _ -> refuse "release requires unary matching CHandle Value parameter 0 and a raw Void result"
            match ctx.Bindings |> Map.tryFind release.Name with
            | Some projection when not projection.ReferenceParameters.IsEmpty || not projection.ReadOnlyReferenceParameters.IsEmpty
                                   || not projection.StringParameters.IsEmpty
                                   || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) ->
                refuse "release requires an untranslated unary Value parameter"
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
        let projection = ctx.Bindings |> Map.tryFind func.Name
        let contracts = projection |> Option.map _.WritableBuffers |> Option.defaultValue []
        let refuse reason = failwith $"{func.Name} writable_buffers: {reason}"
        if (contracts |> List.map _.Parameter |> Set.ofList).Count <> contracts.Length then
            refuse "duplicate parameter indices"
        contracts |> List.collect (fun contract ->
            for key, value in ["identity", contract.Identity; "version", contract.Version; "source", contract.Source] do
                if System.String.IsNullOrWhiteSpace value then refuse $"requires nonempty {key}"
            let projection = projection.Value
            if hasAdapter || not projection.StringParameters.IsEmpty
               || (projection.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not)) then
                refuse "requires an untranslated native parameter list"
            let parameter index =
                if index < 0 || index >= func.Parameters.Length then refuse $"parameter {index} is outside native arity {func.Parameters.Length}"
                snd func.Parameters[index]
            let buffer, count = parameter contract.Parameter, parameter contract.CountParameter
            if contract.Parameter = contract.CountParameter then refuse "requires distinct buffer and count parameters"
            let reference, readOnly = referencePassing (Some projection) contract.Parameter buffer
            if not reference || readOnly || not (isDataPointerIn ctx buffer) then
                refuse "buffer parameter requires writable Reference passing"
            match List.tryItem contract.Parameter projection.ReferenceElements |> Option.map (resolveIn ctx) with
            | Some (Scalar element) when element.Family = TypeMapper.Unsigned && element.Bits = 8 -> ()
            | _ -> refuse "buffer parameter requires an explicit unsigned8 byte reference element contract"
            let countReference, _ = referencePassing (Some projection) contract.CountParameter count
            match resolveIn ctx count with
            | Scalar scalar when not countReference && (scalar.Family = TypeMapper.Signed || scalar.Family = TypeMapper.Unsigned) -> ()
            | _ -> refuse "count parameter requires scalar integer Value passing"
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
        if entries.IsEmpty then
            let nativeName = raw |> List.pick (function LetBinding(name, _, _, _, attrs) when not attrs.IsEmpty -> Some name | _ -> None)
            callbackInvocationDecls ctx namespace' anchors func nativeName |> ignore
            raw @ resultRangeDecls ctx namespace' func nativeName false @ resourceDecls ctx namespace' anchors func nativeName raw @ writableBufferDecls ctx namespace' func nativeName false
        else
        resultRangeDecls ctx namespace' func func.Name true |> ignore
        writableBufferDecls ctx namespace' func func.Name true |> ignore
        let callbackTypes = entries |> List.map (fun (i, name, _) -> cleanParamName (fst func.Parameters.[i]), name) |> Map.ofList
        let nativeName, nativeParameters, returnType =
            raw |> List.pick (function LetBinding(name, parameters, ret, _, attrs) when not attrs.IsEmpty -> Some (name, parameters, ret) | _ -> None)
        let privateName = nativeDeclarationName func entries raw
        let entryReferences, invocationDeclarations = callbackInvocationDecls ctx namespace' anchors func privateName
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
                Some (LetBinding("private " + privateName, parameters, ret, body, attrs))
            | ValueBinding(name, ty, body) when name = nativeName + "Descriptor" -> Some (ValueBinding(privateName + "Descriptor", ty, entryParameterReferences entryReferences body))
            | LetBinding(name, _, _, _, []) when name = func.Name -> None
            | declaration -> Some declaration)
        declarations @ invocationDeclarations @ resourceDecls ctx namespace' anchors func privateName raw @ [ LetBinding(func.Name, publicParameters, returnType, FunctionCall("", privateName, callArguments), []) ]

    let generateDeclaredFunctionDecls (ctx: GenerationContext) libraryName namespace' (func: CppParser.FunctionDecl) =
        declaredFunctionDecls ctx namespace' Map.empty func (callbackEntries ctx namespace' func) (generateFunctionDecls ctx libraryName func)

    /// Generate FsDecl list for an enum type.
    /// Automatically detects bitmask (flags) enums via value pattern analysis.
    let private generateEnumDecl (e: CppParser.EnumDecl) : FsDecl list =
        let values = e.Values |> List.map (fun v -> (v.Name, v.Value))
        let isFlags = ActivePatterns.isBitmaskEnum values
        [ EnumType(e.Name, values, e.Documentation, isFlags) ]

    /// Generate FsDecl list for a C struct: a layout module of literal offsets plus its
    /// BAREWire StructDescriptor (docs/14 §2), measured when the pilot measured it.
    let private generateStructDecl (ctx: GenerationContext) (s: CppParser.StructDecl) : FsDecl list =
        // Measured synchronization unions are opaque storage. Their public
        // extent/alignment is measured; overlapping implementation members are not fields
        // consumers may access through a BAREWire struct view.
        let s =
            match s.IsUnion, Map.tryFind s.Name ctx.StructLayouts with
            | true, Some layout ->
                let storage : CppParser.FieldDecl =
                    { Name = "Storage"; Type = "unsigned char"; IsConst = false; IsVolatile = false
                      IsArray = true; ArraySize = Some (layout.SizeBits / 8); IsBitfield = false; BitWidth = None }
                { s with Fields = [storage] }
            | _ -> s
        let shape (cType: string) : DescriptorGenerator.FieldShape =
            match resolveIn ctx cType with
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
                let fieldType (field: CppParser.FieldDecl) =
                    let resolved = resolveIn ctx field.Type
                    let nonnull = outerNullabilityIn ctx "_Nonnull" field.Type
                    if nonnull && outerNullabilityIn ctx "_Nullable" field.Type then
                        failwith $"Value struct '{s.Name}' field '{field.Name}' has contradictory native nullability evidence."
                    match resolved with
                    | DataPointer _ | OpaqueHandle _ when not nonnull -> wrapOption (clefTypeOf resolved)
                    | Scalar r when r.Family = TypeMapper.Pointer && not nonnull -> wrapOption (clefTypeOf resolved)
                    | FunctionPointer _ when not nonnull ->
                        failwith $"Value struct '{s.Name}' field '{field.Name}' requires explicit nonnull callback-slot evidence; nullable callback fields require a supported boundary adapter."
                    | _ -> clefTypeOf resolved
                [ RecordType(s.Name, s.Fields |> List.map (fun field -> cleanParamName field.Name, fieldType field), s.Documentation, []) ]
            else []
        valueDecls @ DescriptorGenerator.structDecls
            { Structs = ctx.Structs; Layouts = ctx.StructLayouts; Model = ctx.DataModel; Shape = shape }
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
            elif s.Fields.IsEmpty then GNone  // Fieldless structs are opaque — no layout (nothing to lay out)
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
                match resolveIn ctx td.UnderlyingType with
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
            else failwith $"C++ class '{c.Name}' requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
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
          PhantomMarkers = Set.empty
          KnownClasses = CppClassAnalysis.buildClassMap declarations }

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
            namespace' + "." + func.Name, (namespace' + "." + nativeDeclarationName func entries raw, func, raw)) |> Map.ofList
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
