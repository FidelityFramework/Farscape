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
/// only `int`, `uint`, `float`, `bool`, `unit`, `CHandle<'T>` and `FnPtr<'F>`; the width of every
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

    /// Generate wrapper struct + companion module declarations for opaque handle types.
    let generateOpaqueHandleDecls (handleNames: Set<string>) : FsDecl list =
        handleNames
        |> Set.toList
        |> List.sort
        |> List.collect (fun name ->
            [ XmlDoc "Opaque handle wrapping a native pointer."
              RecordType(name, [("Handle", Named "nativeint")], None, ["Struct"])
              SubModule(name, [
                  LetBinding("zero", [],
                      Named name,
                      RecordConstruction [($"{name}.Handle", Literal "0n")],
                      [])
                  LetBinding("isNull", [{ Name = "h"; Type = Named name }],
                      Named "bool",
                      Comparison(Identifier "h.Handle", "=", Literal "0n"),
                      [])
                  LetBinding("ofHandle", [{ Name = "h"; Type = Named "nativeint" }],
                      Named name,
                      RecordConstruction [($"{name}.Handle", Identifier "h")],
                      [])
              ])
              BlankLine ])

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
        /// An opaque handle typedef, spelled by its own name, a Pointer at the ABI.
        | OpaqueHandle of string
        /// A C enum, spelled by its name; an integer of its underlying representation at the ABI.
        | CEnum of name: string * TypeMapper.AbiRepr
        /// A named type the generator cannot represent (a struct by value, a union): no ABI claim.
        | Unresolved of string

    /// The return and parameter type strings of a C function-pointer type "R (*)(A, B)".
    let private splitFunctionPointer (cType: string) : (string * string list) option =
        match cType.IndexOf("(*)") with
        | -1 -> None
        | i ->
            let ret = cType.Substring(0, i).Trim()
            let rest = cType.Substring(i + 3).Trim()
            if rest.StartsWith("(") && rest.EndsWith(")") then
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
                Some (ret, all)
            else None

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
    /// pointers become FnPtr signatures (pointer parameters nullable, ffi-boundary.md §1); data
    /// pointers become CHandle of their pointee; scalars come from the one-kind table, through the
    /// typedef map when the spelling is not in it; enums carry their underlying representation.
    let rec resolveCType
        (typedefMap: Map<string, string>) (model: PlatformABI)
        (opaqueHandles: Set<string>) (delegates: Map<string, CppParser.DelegateDecl>)
        (enums: Map<string, CppParser.EnumDecl>)
        (cType: string) : ResolvedCType =
        let recur = resolveCType typedefMap model opaqueHandles delegates enums
        let cleaned = TypeMapper.cleanTypeName cType
        let nullableClef (resolved: ResolvedCType) =
            match resolved with
            | DataPointer _ -> Generic("option", clefTypeOf resolved)
            | _ -> clefTypeOf resolved
        let functionPointer (ret: string) (parameters: string list) =
            FunctionPointer (parameters |> List.map (recur >> nullableClef), nullableClef (recur ret))
        // A pointer's pointee: a scalar's spelling, a named type, or what a typedef stands for
        let pointee (baseType: string) : FsType =
            match TypeMapper.tryScalar model baseType with
            | Some r -> Named (TypeMapper.clefSpelling r.Family)
            | None ->
                let name = TypeMapper.cleanTypeName baseType
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
        if opaqueHandles.Contains cType || opaqueHandles.Contains cleaned then OpaqueHandle cleaned
        else
        match Map.tryFind cleaned delegates with
        | Some d -> functionPointer d.ReturnType (d.Parameters |> List.map snd)
        | None ->
        match splitFunctionPointer cType with
        | Some (ret, parameters) -> functionPointer ret parameters
        | None when cType.Contains("(**)") -> DataPointer (Generic("CHandle", Named "unit"))
        | None ->
        match cType with
        | ParsedCType info when info.PointerDepth > 0 ->
            let inner = pointee info.BaseType
            DataPointer (List.fold (fun t _ -> Generic("CHandle", t)) inner [ 2 .. info.PointerDepth ])
        | ParsedCType info -> scalarOrNamed info.BaseType
        | _ when cType.Contains("*") -> DataPointer (Named "unit")
        | _ -> scalarOrNamed cleaned

    /// The Clef spelling of a resolved C type: the one kind per family, never a width.
    and clefTypeOf (resolved: ResolvedCType) : FsType =
        match resolved with
        | Scalar r -> Named (TypeMapper.clefSpelling r.Family)
        | DataPointer pointee -> Generic("CHandle", pointee)
        | FunctionPointer (parameters, ret) -> Generic("FnPtr", FunctionType(parameters, ret))
        | OpaqueHandle name | CEnum (name, _) | Unresolved name -> Named name

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
        /// Explicit native substrate profile; default source uses CHandle and one-kind scalars.
        NativePointerSurface: bool
        Bindings: Map<string, BindingProjection>
        ValueStructs: Set<string>
        PhantomMarkers: Set<string>
    }

    /// ABI carriers for native substrate bindings. Function pointers remain typed and carry
    /// the complete callback signature; data pointers (including pointer-to-pointer) are addresses.
    let rec mapCTypeToNativeSurface
        (typedefMap: Map<string, string>) (model: PlatformABI)
        (delegates: Map<string, CppParser.DelegateDecl>) (cType: string) : FsType =
        let recur = mapCTypeToNativeSurface typedefMap model delegates
        let callback ret args = Generic("FnPtr", FunctionType(List.map recur args, recur ret))
        match splitFunctionPointer cType with
        | Some (ret, args) -> callback ret args
        | None ->
            match Map.tryFind (TypeMapper.cleanTypeName cType) delegates with
            | Some d -> callback d.ReturnType (List.map snd d.Parameters)
            | None ->
                match resolveCType typedefMap model Set.empty delegates Map.empty cType with
                | Scalar r ->
                    match r.Family with
                    | TypeMapper.Signed -> Named $"int{r.Bits}"
                    | TypeMapper.Unsigned -> Named $"uint{r.Bits}"
                    | TypeMapper.Float -> Named (if r.Bits = 32 then "float32" else "float")
                    | TypeMapper.Void -> Unit
                    | TypeMapper.Bool -> Named "bool"
                    | TypeMapper.Pointer -> Named "nativeint"
                | DataPointer _ | OpaqueHandle _ -> Named "nativeint"
                | _ -> mapCTypeToFidelityType typedefMap model Set.empty delegates cType

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
    /// Function pointers contain "(*)" and are excluded — they map to FnPtr<'F>
    /// and have different nullability semantics (use Option<FnPtr<'F>> instead).
    let isCDataPointer (cType: string) : bool =
        cType.Contains("*") && not (cType.Contains("(*)") || cType.Contains("(**)"))

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
            | Some transfer -> string transfer, $"Ownership {transfer} is declared by the pilot."
            | None -> "Borrowed", "The header declares no ownership; Borrowed is the inferred default until a pilot declaration says otherwise."
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
        let mapType =
            if ctx.NativePointerSurface then mapCTypeToNativeSurface ctx.TypedefMap ctx.DataModel ctx.Delegates
            else resolveIn ctx >> clefTypeOf
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
                let fsType =
                    if reference then
                        match cType with
                        | ParsedCType info when info.PointerDepth = 1 ->
                            match resolveIn ctx (element |> Option.defaultValue info.BaseType) with
                            | Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned || r.Family = TypeMapper.Float -> Named (TypeMapper.clefSpelling r.Family + " array")
                            | _ -> failwith $"{func.Name} parameter {idx}: Reference requires a scalar pointee"
                        | ParsedCType info when info.PointerDepth = 2 ->
                            let pointerType = cType.Substring(0, cType.LastIndexOf('*')).Trim()
                            let element = resolveIn ctx pointerType |> clefTypeOf |> wrapOption
                            Named (CodeRenderer.renderType element + " array")
                        | _ -> failwith $"{func.Name} parameter {idx}: Reference requires a scalar cell or one opaque pointer cell"
                    elif stringParameter then
                        match cType with
                        | ParsedCType info when info.PointerDepth = 1 && info.BaseType = "char" -> Named "string"
                        | _ -> failwith $"{func.Name} parameter {idx}: string projection requires char pointer"
                    elif handle.IsSome then
                        if not (isCDataPointer cType) then failwith $"{func.Name} parameter {idx}: handle projection requires a C data pointer"
                        Generic("CHandle", Named handle.Value)
                    elif callbackNonnull then
                        let strip = function Generic("option", inner) -> inner | ty -> ty
                        match mapType cType with
                        | Generic("FnPtr", FunctionType(args, ret)) -> Generic("FnPtr", FunctionType(List.map strip args, strip ret))
                        | _ -> failwith $"{func.Name} parameter {idx}: nonnull_callbacks requires a C function pointer"
                    else mapType cType
                let isNullable = not ctx.NativePointerSurface && not reference && isCDataPointer cType && not (nonnullIndices.Contains idx)
                let finalType = if isNullable then wrapOption fsType else fsType
                { FsParam.Name = cleanParamName name; Type = finalType })

        // Return type: nullable unless proven nonnull
        let returnIsPointer = isCDataPointer func.ReturnType
        let returnNonnull =
            nonnullAnnotations
            |> Option.map (fun a -> a.Returns.Contains func.Name)
            |> Option.defaultValue false
        let hasReturnsNonnullAttr =
            func.Attributes |> List.exists (fun a -> a.Kind = "ReturnsNonNullAttr")
        let returnType =
            match projection |> Option.bind (fun b -> b.ReturnHandle) with
            | Some name ->
                if not (isCDataPointer func.ReturnType) then failwith $"{func.Name}: return_handle requires a C data pointer"
                Generic("CHandle", Named name)
            | None -> mapType func.ReturnType
        let finalReturnType =
            if not ctx.NativePointerSurface && returnIsPointer && not returnNonnull && not hasReturnsNonnullAttr
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
        formatDocDecls { func with Name = symbol } @
        [ LetBinding(nativeName, parameters, finalReturnType, NativeZeroed,
                     [$"FidelityExtern(\"{libraryName}\", \"{symbol}\")"]) ] @
        generateDescriptorDecls descriptorContext nativeFunction @ wrapper

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
                [ RecordType(s.Name, s.Fields |> List.map (fun field -> cleanParamName field.Name, (resolveIn ctx field.Type |> clefTypeOf)), s.Documentation, []) ]
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
    // C++ Class Binding Generation
    // =========================================================================

    /// Helper: build a FidelityExtern attribute string for a library and symbol.
    let private fidelityExternAttr (lib: string) (symbol: string) : string =
        "FidelityExtern(\"" + lib + "\", \"" + symbol + "\")"

    /// Helper: build a CppPimpl attribute string.
    let private cppPimplAttr (lib: string) (size: int) : string =
        "CppPimpl(\"" + lib + "\", " + string size + ")"

    /// Helper: build a CppValue attribute string.
    let private cppValueAttr (lib: string) (size: int) : string =
        "CppValue(\"" + lib + "\", " + string size + ")"

    /// Helper: sanitize C++ class name for use as F# identifier.
    let private safeClassName (name: string) : string =
        name.Replace("::", "_")

    /// Generate FidelityExtern declarations for a pimpl class method, accounting
    /// for sret return convention when the return type is a non-trivially-copyable class.
    ///
    /// For SretReturn: the hidden sret pointer becomes the first parameter (rdi),
    /// shifting this-pointer to rsi and all other args by one register.
    /// For RegisterReturn: no sret; this-pointer is in rdi as normal.
    let private generateCppMethodDecl
        (libraryName: string)
        (className: string)
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (method: CppParser.FunctionDecl)
        : FsDecl list =
        let returnInfo = CppClassAnalysis.analyzeMethodReturn knownClasses method

        let thisParam = { FsParam.Name = "this"; Type = Named "nativeint" }

        // Map C++ parameter types to Fidelity types (conservative: all pointers as nativeint)
        let methodParams =
            method.Parameters
            |> List.map (fun (name, _cType) ->
                { FsParam.Name = cleanParamName name; Type = Named "nativeint" })

        let sretComment, allParams, returnType =
            match returnInfo.ReturnConvention with
            | CppClassAnalysis.SretReturn ->
                let sretParam = { FsParam.Name = "retStorage"; Type = Named "nativeint" }
                let comment = "sret: caller provides return storage for " + returnInfo.ReturnType
                (comment, sretParam :: thisParam :: methodParams, Unit)
            | CppClassAnalysis.RegisterReturn ->
                let retType =
                    if method.ReturnType = "void" then Unit
                    else Named "nativeint"
                ("", thisParam :: methodParams, retType)

        // Use mangled symbol from clang AST when available; fall back to method name
        // for C functions or if clang did not emit mangledName.
        let mangledName = method.MangledName |> Option.defaultValue method.Name
        let safe = safeClassName className
        let bindingName = safe + "_" + method.Name

        let docLines =
            [ if method.Documentation.IsSome then
                XmlDoc method.Documentation.Value
              XmlDoc ("C++ method: " + className + "::" + method.Name)
              if sretComment <> "" then
                XmlDoc sretComment ]

        docLines @
        [ LetBinding(
            bindingName,
            allParams,
            returnType,
            NativeZeroed,
            [fidelityExternAttr libraryName mangledName])
        ]

    /// Generate FidelityExtern declarations for pimpl constructor(s).
    /// Constructor mangled symbols use C1 (complete object constructor).
    let private generateCppConstructorDecls
        (libraryName: string)
        (className: string)
        (ctors: CppParser.Declaration list)
        : FsDecl list =
        let safe = safeClassName className
        ctors
        |> List.choose (function
            | CppParser.Declaration.Function f -> Some f
            | _ -> None)
        |> List.mapi (fun idx ctor ->
            let suffix = if idx = 0 then "" else "_" + string idx
            let thisParam = { FsParam.Name = "this"; Type = Named "nativeint" }
            let ctorParams =
                ctor.Parameters
                |> List.map (fun (name, _) ->
                    { FsParam.Name = cleanParamName name; Type = Named "nativeint" })
            let mangledName = ctor.MangledName |> Option.defaultValue ctor.Name
            let paramSig = ctor.Parameters |> List.map snd |> String.concat ", "

            [ XmlDoc ("C++ constructor: " + className + "(" + paramSig + ")")
              LetBinding(
                safe + "_ctor" + suffix,
                thisParam :: ctorParams,
                Unit,
                NativeZeroed,
                [fidelityExternAttr libraryName mangledName])
            ])
        |> List.concat

    /// Generate the complete binding set for a pimpl-pattern C++ class.
    /// Produces: type struct (16 bytes), constructor externs, destructor extern,
    /// method externs (with sret awareness), and a companion module.
    let private generatePimplBindings
        (libraryName: string)
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (cls: CppParser.ClassDecl)
        (size: int)
        : FsDecl list =
        let safeName = safeClassName cls.Name

        // Opaque struct type (16 bytes for shared_ptr pimpl)
        let structDecl =
            RecordType(safeName, [("Handle", Named "nativeint")], cls.Documentation,
                      [cppPimplAttr libraryName size; "Struct"])

        // Constructors
        let ctorDecls = generateCppConstructorDecls libraryName cls.Name cls.Constructors

        // Destructor (D1 = complete object destructor)
        let dtorDecls =
            if cls.HasUserDestructor then
                let shortName = cls.Name.Split("::") |> Array.last
                let dtorDoc = "C++ destructor: " + cls.Name + "::~" + shortName + "()"
                // Use mangled symbol from clang AST when available; fall back to
                // fabricated name if the AST did not emit mangledName.
                let dtorSymbol =
                    cls.DestructorMangledName |> Option.defaultValue (safeName + "_D1Ev")
                [ XmlDoc dtorDoc
                  LetBinding(
                    safeName + "_dtor",
                    [{ FsParam.Name = "this"; Type = Named "nativeint" }],
                    Unit,
                    NativeZeroed,
                    [fidelityExternAttr libraryName dtorSymbol]) ]
            else []

        // Methods (with sret analysis, excluding deprecated)
        let methodDecls =
            cls.Methods
            |> List.choose (function
                | CppParser.Declaration.Function f -> Some f
                | _ -> None)
            |> List.filter (fun f ->
                not (f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")))
            |> List.collect (generateCppMethodDecl libraryName cls.Name knownClasses)

        [ Comment ("// C++ class: " + cls.Name + " (pimpl, " + string size + " bytes)")
          BlankLine
          structDecl
          BlankLine ] @
        ctorDecls @
        (if ctorDecls.IsEmpty then [] else [BlankLine]) @
        dtorDecls @
        (if dtorDecls.IsEmpty then [] else [BlankLine]) @
        methodDecls @
        [ BlankLine ]

    /// Generate bindings for a POD (trivially copyable) C++ class.
    /// POD classes are mapped to explicit-layout structs with no destructor obligation.
    let private generatePodBindings
        (cls: CppParser.ClassDecl)
        (size: int)
        : FsDecl list =
        let safeName = safeClassName cls.Name
        // For POD types, generate a fixed-size struct with field mappings
        let fields = cls.Fields |> List.map (fun f -> (f.Name, Named "nativeint"))
        let comment = "// C++ POD type: " + cls.Name + " (" + string size + " bytes)"
        if fields.IsEmpty then
            // Opaque POD with known size but no visible fields
            let byteTypeName = "byte_" + string size
            [ Comment comment
              RecordType(safeName, [("Bytes", Generic("InlineArray", Named byteTypeName))],
                        cls.Documentation, ["Struct"])
              BlankLine ]
        else
            [ Comment comment
              RecordType(safeName, fields, cls.Documentation, ["Struct"])
              BlankLine ]

    /// Generate bindings for a value class (non-trivial but not pimpl).
    /// These have sret implications for return convention.
    let private generateValueClassBindings
        (libraryName: string)
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (cls: CppParser.ClassDecl)
        (size: int)
        (rc: CppClassAnalysis.ReturnConvention)
        : FsDecl list =
        let safeName = safeClassName cls.Name
        let rcComment =
            match rc with
            | CppClassAnalysis.SretReturn -> "sret (non-trivially copyable)"
            | CppClassAnalysis.RegisterReturn -> "register return"

        let comment = "// C++ value type: " + cls.Name + " (" + string size + " bytes, " + rcComment + ")"

        let ctorDecls = generateCppConstructorDecls libraryName cls.Name cls.Constructors

        // Destructor (D1 = complete object destructor)
        // Value classes are non-trivial by definition (user dtor, copy ctor, or move ctor),
        // so they need explicit destructor bindings.
        let dtorDecls =
            if cls.HasUserDestructor then
                let shortName = cls.Name.Split("::") |> Array.last
                let dtorDoc = "C++ destructor: " + cls.Name + "::~" + shortName + "()"
                let dtorSymbol =
                    cls.DestructorMangledName |> Option.defaultValue (safeName + "_D1Ev")
                [ XmlDoc dtorDoc
                  LetBinding(
                    safeName + "_dtor",
                    [{ FsParam.Name = "this"; Type = Named "nativeint" }],
                    Unit,
                    NativeZeroed,
                    [fidelityExternAttr libraryName dtorSymbol]) ]
            else []

        let methodDecls =
            cls.Methods
            |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
            |> List.filter (fun f ->
                not (f.Attributes |> List.exists (fun a -> a.Kind = "DeprecatedAttr")))
            |> List.collect (generateCppMethodDecl libraryName cls.Name knownClasses)

        [ Comment comment
          BlankLine
          RecordType(safeName, [("Handle", Named "nativeint")], cls.Documentation,
                    [cppValueAttr libraryName size; "Struct"])
          BlankLine ] @
        ctorDecls @
        (if ctorDecls.IsEmpty then [] else [BlankLine]) @
        dtorDecls @
        (if dtorDecls.IsEmpty then [] else [BlankLine]) @
        methodDecls @
        [ BlankLine ]

    // =========================================================================
    // Catamorphism-Based Generation
    // =========================================================================

    /// Declaration group: produced by catamorphism, consumed by assembler.
    type private DeclGroup =
        | GEnum of FsDecl list
        | GStruct of FsDecl list
        | GFunc of CppParser.FunctionDecl
        | GMacro of FsDecl list
        | GCppClassBindings of FsDecl list
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
            if not ctx.NativePointerSurface && ctx.Bindings.IsEmpty then GNone
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
            else
                match CppClassAnalysis.classifyClass c with
                | CppClassAnalysis.PimplClass(_, size) ->
                    GCppClassBindings (generatePimplBindings libraryName ctx.KnownClasses c size)
                | CppClassAnalysis.PODClass size ->
                    GCppClassBindings (generatePodBindings c size)
                | CppClassAnalysis.ValueClass(size, rc) ->
                    GCppClassBindings (generateValueClassBindings libraryName ctx.KnownClasses c size rc)
                | CppClassAnalysis.InterfaceClass ->
                    GNone  // Abstract classes cannot be bound directly
                | CppClassAnalysis.OpaqueClass ->
                    GNone  // No visible structure; requires developer intervention
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

        let opaqueHandleDecls = generateOpaqueHandleDecls handlesToDeclare
        let markerDecls =
            if ctx.NativePointerSurface then []
            else
                let declared =
                    if ctx.Bindings.IsEmpty then []
                    else declarations |> List.choose (function
                        | CppParser.Declaration.Struct s when s.Name <> "" && not (ctx.ValueStructs.Contains s.Name) -> Some s.Name
                        | _ -> None)
                declared @ (if openModules.IsEmpty then Set.toList ctx.PhantomMarkers else [])
                |> List.distinct |> List.filter (fun name -> not (ctx.ValueStructs.Contains name)) |> List.map OpaqueMarker

        let groups =
            DeclarationAlgebra.cataDeclarations (generationAlgebra ctx libraryName) declarations

        let enums = groups |> List.collect (function GEnum d -> d | _ -> [])
        let structs = groups |> List.collect (function GStruct d -> d | _ -> [])
        let functions =
            groups
            |> List.choose (function GFunc f -> Some f | _ -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateFunctionDecls ctx libraryName)
        let macros = groups |> List.collect (function GMacro d -> d | _ -> [])
        let cppClasses = groups |> List.collect (function GCppClassBindings d -> d | _ -> [])

        let macroSection =
            if macros.IsEmpty then []
            else Comment "// Macro constants" :: macros @ [BlankLine]

        let cppSection =
            if cppClasses.IsEmpty then []
            else Comment "// C++ class bindings" :: BlankLine :: cppClasses

        let vocabularyOpens =
            [ if not structs.IsEmpty then "BAREWire.Hardware"
              if not functions.IsEmpty then "BAREWire.Descriptors" ]
        let openDecls =
            match (openModules @ vocabularyOpens) |> List.distinct with
            | [] -> []
            | opens -> (opens |> List.map OpenModule) @ [BlankLine]
        let allDecls = openDecls @ opaqueHandleDecls @ markerDecls @ enums @ structs @ functions @ macroSection @ cppSection
        let surfaceComment =
            if ctx.NativePointerSurface then comment + " — EXPERIMENTAL legacy ABI surface; not current Clef source"
            else comment
        let moduleDecl = Module(namespace', surfaceComment, allDecls)

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
