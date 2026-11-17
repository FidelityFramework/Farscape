namespace Farscape.Core

open WrapperTypes

/// Analyzes C function declarations to infer idiomatic F# wrapper patterns.
///
/// Maps raw `CppParser.AttributeData` to semantic `FunctionAttribute` values,
/// then uses active patterns to classify return semantics and parameter roles.
/// The output `WrapperPattern` drives code generation in `WrapperCodeGenerator`.
///
/// Architecture: Active patterns for classification, pure functions for inference.
/// Same pattern as `ActivePatterns.fs`: independent, testable classifiers.
module WrapperPatternAnalyzer =

    // =========================================================================
    // Raw → Semantic Attribute Mapping
    // =========================================================================

    /// Map a single raw `AttributeData` from CppParser to a semantic `FunctionAttribute`.
    let mapAttribute (attr: CppParser.AttributeData) : FunctionAttribute option =
        match attr.Kind with
        | "AllocSizeAttr"         -> Some (AllocSize attr.Args)
        | "NonNullAttr"           -> Some (NonNull attr.Args)
        | "FormatAttr" ->
            match attr.StringArg, attr.Args with
            | Some archetype, [fmtIdx; firstArgIdx] ->
                Some (Format (archetype, fmtIdx, firstArgIdx))
            | Some archetype, _ ->
                Some (Format (archetype, 0, 0))
            | _ -> None
        | "NoReturnAttr"          -> Some NoReturn
        | "NoThrowAttr"           -> Some NoThrow
        | "ColdAttr"              -> Some Cold
        | "RestrictAttr"          -> Some Restrict
        | "PureAttr"              -> Some Pure
        | "ConstAttr"             -> Some FunctionAttribute.Const
        | "WarnUnusedResultAttr"  -> Some WarnUnusedResult
        | "AllocAlignAttr" ->
            match attr.Args with
            | [idx] -> Some (AllocAlign idx)
            | _ -> None
        | "AsmLabelAttr" ->
            Some (AsmLabel (attr.StringArg |> Option.defaultValue ""))
        | _ -> None

    /// Map all raw attributes from a FunctionDecl to semantic attributes.
    let mapAttributes (attrs: CppParser.AttributeData list) : FunctionAttribute list =
        attrs |> List.choose mapAttribute

    // =========================================================================
    // Active Patterns for Parameter Name Classification
    // =========================================================================

    /// Classify parameter names by POSIX naming conventions.
    /// Max 7 cases for F# active pattern limit.
    let (|FdName|BufName|CountName|DstName|SrcName|PtrName|OtherName|) (name: string) =
        let lower = name.ToLowerInvariant().TrimStart('_')
        if lower = "fd" || lower = "fildes" || lower.EndsWith("_fd") || lower = "sockfd" then FdName
        elif lower = "buf" || lower = "buffer" || lower = "data" then BufName
        elif lower = "count" || lower = "n" || lower = "nbytes" || lower = "len" || lower = "length"
             || lower = "size" || lower = "sz" || lower = "nbyte" then CountName
        elif lower = "dst" || lower = "dest" || lower = "destination" then DstName
        elif lower = "src" || lower = "source" then SrcName
        elif lower = "ptr" || lower = "p" then PtrName
        else OtherName

    // =========================================================================
    // Return Semantic Analysis
    // =========================================================================

    /// Functions that return a meaningful int value (fd, pid, count) on success
    /// and -1 on error. These use the IntValueOrError pattern: result >= 0 is Ok,
    /// result < 0 is Error, and the success value is preserved in the Result.
    let private intValueReturningFunctions = Set.ofList [
        // fd-returning
        "open"; "openat"; "open64"; "openat64"; "creat"; "creat64"
        "socket"; "accept"; "accept4"
        "dup"; "dup2"; "dup3"
        "shm_open"; "memfd_create"
        "epoll_create"; "epoll_create1"
        "signalfd"; "timerfd_create"; "eventfd"; "inotify_init"; "inotify_init1"
        // pid-returning
        "fork"; "vfork"
        // count/value-returning
        "poll"; "ppoll"; "select"; "pselect"
        "epoll_wait"; "epoll_pwait"
        "fcntl"; "ioctl"; "prctl"
        // id-returning (always succeed, but use same pattern for consistency)
        "getpid"; "getppid"; "getuid"; "geteuid"; "getgid"; "getegid"
        "gettid"; "getpgrp"; "getpgid"; "getsid"
        // drm fd-returning
        "drmOpen"; "drmOpenControl"; "drmOpenRender"
    ]

    /// Infer ReturnSemantic from the return type string, function name, and semantic attributes.
    /// Uses the typedef map to resolve type aliases before classification.
    let analyzeReturn
        (returnType: string)
        (attrs: FunctionAttribute list)
        (typedefMap: Map<string, string>)
        (functionName: string)
        : ReturnSemantic =

        // Priority 1: NoReturn attribute
        if attrs |> List.exists (function NoReturn -> true | _ -> false) then
            NeverReturns
        // Priority 2: AllocSize attribute → allocated pointer
        elif attrs |> List.exists (function AllocSize _ -> true | _ -> false) then
            AllocatedPointer
        // Priority 3: Pure/Const → pure value (no error checking)
        elif attrs |> List.exists (function Pure | FunctionAttribute.Const -> true | _ -> false) then
            PureValue
        else
            // Resolve typedef to get underlying type
            let resolved =
                match Map.tryFind returnType typedefMap with
                | Some r -> r
                | None -> returnType

            let trimmed = resolved.Trim()

            // Priority 4: ssize_t → count or error
            if trimmed = "ssize_t" || trimmed = "__ssize_t" || trimmed = "long" then
                CountOrError
            // Priority 5: void → void return
            elif trimmed = "void" then
                VoidReturn
            // Priority 6: Pointer types
            elif trimmed.Contains("*") then
                // FILE* or other struct pointers with names → opaque handle
                if trimmed.Contains("FILE") || trimmed.Contains("DIR") then
                    OpaqueHandleReturn
                // void* → allocated pointer
                elif trimmed.StartsWith("void") then
                    AllocatedPointer
                // Other pointer → opaque handle
                else
                    OpaqueHandleReturn
            // Priority 7: int return → distinguish value-returning from zero-success
            elif trimmed = "int" || trimmed = "int32_t" then
                if intValueReturningFunctions.Contains functionName then
                    IntValueOrError
                else
                    ZeroSuccessOrError
            // Default: pure value
            else
                PureValue

    // =========================================================================
    // Parameter Role Analysis
    // =========================================================================

    /// Check if a type string represents a function pointer (callback).
    let private isFunctionPointer (typeStr: string) : bool =
        typeStr.Contains("(*)") || typeStr.Contains("(**)")

    /// Check if a parameter name matches common userdata/context naming patterns.
    let private isUserdataName (name: string) : bool =
        let lower = name.ToLowerInvariant().TrimStart('_')
        lower = "data" || lower = "user_data" || lower = "userdata"
        || lower = "user" || lower = "ctx" || lower = "context"
        || lower = "arg" || lower = "closure_data"

    /// Check if a type string represents a const pointer (input buffer).
    let private isConstPointer (typeStr: string) : bool =
        typeStr.Contains("const") && typeStr.Contains("*")

    /// Check if a type string represents a non-const pointer (output buffer).
    let private isMutablePointer (typeStr: string) : bool =
        not (typeStr.Contains("const")) && typeStr.Contains("*")

    /// Check if a type string represents a size/count type.
    let private isSizeType (typeStr: string) (typedefMap: Map<string, string>) : bool =
        let resolved =
            match Map.tryFind typeStr typedefMap with
            | Some r -> r
            | None -> typeStr
        let t = resolved.Trim()
        t = "size_t" || t = "ssize_t" || t = "__size_t" || t = "unsigned long" ||
        t = "unsigned int" || t = "int" || t = "unsigned long int"

    /// Find the adjacent buffer parameter for a length parameter.
    let private findAdjacentBuffer
        (paramIdx: int)
        (allParams: (string * string) list)
        : string option =
        // Look at the previous parameter, commonly the buffer
        if paramIdx > 0 then
            let (prevName, prevType) = allParams.[paramIdx - 1]
            if prevType.Contains("*") then Some prevName
            else None
        else None

    /// Find the adjacent length parameter for a buffer parameter.
    let private findAdjacentLength
        (paramIdx: int)
        (allParams: (string * string) list)
        (typedefMap: Map<string, string>)
        : string option =
        // Look at the next parameter, commonly the length
        if paramIdx < allParams.Length - 1 then
            let (nextName, nextType) = allParams.[paramIdx + 1]
            if isSizeType nextType typedefMap then Some nextName
            else None
        else None

    /// Infer ParamRole for each parameter in a function.
    let analyzeParameters
        (params': (string * string) list)
        (attrs: FunctionAttribute list)
        (typedefMap: Map<string, string>)
        : (string * ParamRole) list =

        let nonNullIndices =
            attrs |> List.collect (function NonNull indices -> indices | _ -> [])

        let formatInfo =
            attrs |> List.tryPick (function Format (_, fmtIdx, _) -> Some fmtIdx | _ -> None)

        // First pass: classify each parameter
        let firstPass =
            params'
            |> List.mapi (fun idx (name, typeStr) ->
                let role =
                    // Check FormatAttr first
                    match formatInfo with
                    | Some fmtIdx when idx = fmtIdx -> FormatString
                    | _ ->
                        // Function pointer parameters (callbacks) before general pointer checks
                        if isFunctionPointer typeStr then
                            CallbackParam
                        else
                        match name with
                        | FdName ->
                            // File descriptor parameter
                            FileDescriptor
                        | _ ->
                            // Check type for pointer patterns
                            if typeStr.Contains("FILE") && typeStr.Contains("*") then
                                OpaqueHandle
                            elif isConstPointer typeStr then
                                let lengthParam = findAdjacentLength idx params' typedefMap
                                InputBuffer lengthParam
                            elif isMutablePointer typeStr then
                                let lengthParam = findAdjacentLength idx params' typedefMap
                                OutputBuffer lengthParam
                            elif isSizeType typeStr typedefMap then
                                match name with
                                | CountName ->
                                    match findAdjacentBuffer idx params' with
                                    | Some bufParam -> BufferLength bufParam
                                    | None -> InputValue
                                | _ -> InputValue
                            else
                                InputValue
                (name, role))

        // Second pass: detect userdata companions for callback parameters
        let hasCallback = firstPass |> List.exists (fun (_, role) -> role = CallbackParam)
        if hasCallback then
            let callbackName =
                firstPass |> List.tryPick (fun (name, role) ->
                    if role = CallbackParam then Some name else None)
            firstPass |> List.map (fun (name, role) ->
                match role with
                | InputValue when isUserdataName name ->
                    (name, UserDataParam (callbackName |> Option.defaultValue ""))
                | InputBuffer None when isUserdataName name ->
                    (name, UserDataParam (callbackName |> Option.defaultValue ""))
                | OutputBuffer None when isUserdataName name ->
                    // void* userdata looks like a mutable pointer; reclassify
                    (name, UserDataParam (callbackName |> Option.defaultValue ""))
                | _ -> (name, role))
        else
            firstPass

    // =========================================================================
    // Complete Pattern Analysis
    // =========================================================================

    /// Compute complete WrapperPattern for a function declaration.
    let analyze
        (func: CppParser.FunctionDecl)
        (typedefMap: Map<string, string>)
        : WrapperPattern =

        let attrs = mapAttributes func.Attributes
        let returnSemantic = analyzeReturn func.ReturnType attrs typedefMap func.Name
        let paramRoles = analyzeParameters func.Parameters attrs typedefMap

        let isPure =
            attrs |> List.exists (function Pure | FunctionAttribute.Const -> true | _ -> false)

        let needsResultWrap =
            match returnSemantic with
            | CountOrError | ZeroSuccessOrError | IntValueOrError | AllocatedPointer | OpaqueHandleReturn | EnumReturnError _ -> true
            | PureValue | NeverReturns | VoidReturn -> false

        let needsResourceCleanup =
            attrs |> List.exists (function AllocSize _ -> true | _ -> false)

        {
            OriginalName = func.Name
            WrapperName = func.Name
            ParamRoles = paramRoles
            ReturnSemantic = returnSemantic
            Attributes = attrs
            NeedsResultWrap = needsResultWrap
            NeedsResourceCleanup = needsResourceCleanup
            IsPure = isPure
            Async = None
        }

    /// Protocol admission uses pilot contracts and parsed declarations, independently of
    /// the synchronous name/type heuristics above. Region domination remains compiler-owned.
    let selectAsyncWithFacts (qualifiedBinding: string -> string)
                            (scalarFamily: CppParser.FunctionDecl -> string -> TypeMapper.Family option)
                            (sameType: CppParser.FunctionDecl -> string -> CppParser.FunctionDecl -> string -> bool)
                            (library: string) (declarations: CppParser.Declaration list)
                            (bindings: Map<string, PilotTypes.BindingProjection>) (contract: PilotTypes.AsyncWrapper) =
        let key = $"options.async_wrappers[{contract.Binding}]"
        let refuse field message =
            Diagnostics.diagnostic Diagnostics.PilotValueUnadmitted message (Diagnostics.Located.Unknown "async pilot contract")
            |> Diagnostics.withPilotKey (key + "." + field) None |> Diagnostics.refuse
        let identifier field (value: string) =
            if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$")) then
                refuse field "async endpoint requires a source identifier"
        let functions =
            DeclarationAlgebra.cataDeclarations
                { OnFunction = Some
                  OnStruct = fun _ -> None
                  OnEnum = fun _ -> None
                  OnTypedef = fun _ -> None
                  OnMacro = fun _ -> None
                  OnNamespace = fun _ -> None
                  OnClass = fun _ -> None
                  OnDelegate = fun _ -> None } declarations
            |> List.choose id
        let find field name =
            functions |> List.tryFind (fun f -> f.Name = name)
            |> Option.defaultWith (fun () -> refuse field $"async endpoint '{name}' is not a declared native binding")
        let native = find "binding" contract.Binding
        let ordinal field index =
            if index < 0 || index >= native.Parameters.Length then refuse field $"async parameter ordinal {index} is outside '{native.Name}'"
            let constant = bindings.TryFind native.Name |> Option.bind (fun b -> List.tryItem index b.ConstantParameters)
            if constant |> Option.exists ((<>) "") then refuse field "async endpoint cannot reference a projected-away constant parameter"
        let validRange field (range: PilotTypes.AsyncStatusRange) =
            if range.Lower > range.Upper then refuse field "async range lower exceeds upper"
        identifier "name" contract.Name
        if contract.Name <> contract.Binding && functions |> List.exists (fun f -> f.Name = contract.Name) then
            refuse "name" "async wrapper name collides with another native wrapper"
        identifier "wait.binding" contract.Wait.Binding
        if scalarFamily native native.ReturnType <> Some TypeMapper.Signed then refuse "binding" "async native operation requires a declared signed scalar status/result"
        validRange "success" contract.Success
        match contract.StatusSource with
        | PilotTypes.AsyncStatusSource.ReturnValue -> ()
        | PilotTypes.AsyncStatusSource.ScalarAccessor accessor ->
            let f = find "status_binding" accessor
            if not f.Parameters.IsEmpty || scalarFamily f f.ReturnType <> Some TypeMapper.Signed then
                refuse "status_binding" "async status accessor must be a synchronous zero-argument signed scalar binding"
        ordinal "correlation_parameter" contract.CorrelationParameter
        contract.Wait.Arguments |> List.iter (ordinal "wait.arguments")
        if not (List.contains contract.CorrelationParameter contract.Wait.Arguments) then
            refuse "wait.arguments" "async wait must receive the declared completion/readiness correlation parameter"
        if List.distinct contract.Wait.Arguments <> contract.Wait.Arguments then refuse "wait.arguments" "async wait arguments must be distinct"
        if System.String.IsNullOrWhiteSpace contract.Wait.Library then refuse "wait.library" "async wait library is required"
        if contract.Wait.Module <> "" then
            contract.Wait.Module.Split('.') |> Array.iter (identifier "wait.module")
        elif contract.Wait.Library <> library || contract.Wait.Project.IsSome then
            refuse "wait" "a caller-supplied async wait parameter belongs to the current library and has no package dependency"
        let layer = if contract.Wait.Library = library then AsyncLayer.Layer2 else AsyncLayer.Layer3
        if layer = AsyncLayer.Layer3 then
            match contract.Wait.Project with
            | Some path when not (System.String.IsNullOrWhiteSpace path) && path.EndsWith(".fidproj") -> ()
            | _ -> refuse "wait.project" "cross-library async wait requires its explicit .fidproj dependency"
        elif contract.Wait.Project.IsSome then
            refuse "wait.project" "single-library async wrappers cannot introduce a package dependency"
        if contract.Wait.Module = "" && native.Parameters |> List.exists (fun (name, _) -> name = contract.Wait.Binding) then
            refuse "wait.binding" "async wait parameter collides with a native parameter"
        let projection = bindings.TryFind native.Name
        // Both the general wrapper path and callback forwarding consume this selection.
        // A declared retained environment must never bypass the capture refusal merely
        // because the pilot has no separate [callbacks] registrations entry.
        for callback in projection |> Option.map _.CallbackInvocations |> Option.defaultValue [] do
            match callback.Scope, callback.Context with
            | "retained", Some context ->
                Diagnostics.diagnostic Diagnostics.RetainedCallbackCaptures
                    "Async retained callback completion cannot capture an unbacked environment; the declared entry must be capture-free."
                    native.Location.Location
                |> Diagnostics.withPilotKey (key + ".callback_parameter") (Some context.Parameter)
                |> Diagnostics.refuse
            | _ -> ()
        for parameter in contract.BufferParameters do
            ordinal "buffer_parameters" parameter
            if not (projection |> Option.exists (fun b ->
                b.ReadOnlyBuffers |> List.exists (fun buffer -> buffer.Parameter = parameter) ||
                b.WritableBuffers |> List.exists (fun buffer -> buffer.Parameter = parameter))) then
                refuse "buffer_parameters" "async buffer reference requires the operation's existing read-only or writable buffer descriptor"
        let declaredBuffers = projection |> Option.map (fun b -> (b.ReadOnlyBuffers |> List.map _.Parameter) @ (b.WritableBuffers |> List.map _.Parameter)) |> Option.defaultValue [] |> Set.ofList
        if Set.ofList contract.BufferParameters <> declaredBuffers || List.distinct contract.BufferParameters <> contract.BufferParameters then
            refuse "buffer_parameters" "async buffer references must cover each declared operation buffer exactly once"
        for binding in contract.BorrowedResults do
            if not (bindings.TryFind binding |> Option.bind _.BorrowedResult |> Option.exists (fun view -> not view.InvalidatedBy.IsEmpty)) then
                refuse "borrowed_results" $"async borrowed-result reference '{binding}' requires an existing InvalidatedBy contract"
        match contract.Protocol, contract.CompletionSuccess, contract.Retirement with
        | PilotTypes.AsyncProtocol.Readiness pending, None, None ->
            if pending.IsEmpty then refuse "pending" "readiness requires pending status ranges"
            pending |> List.iter (validRange "pending")
            let ordered = pending |> List.sortBy _.Lower
            if ordered |> List.pairwise |> List.exists (fun (a, b) -> a.Upper >= b.Lower) then
                refuse "pending" "readiness pending ranges must not overlap"
            if contract.StatusSource = PilotTypes.AsyncStatusSource.ReturnValue && pending |> List.exists (fun r -> r.Lower <= contract.Success.Upper && r.Upper >= contract.Success.Lower) then
                refuse "pending" "readiness pending range overlaps native success"
        | (PilotTypes.AsyncProtocol.SubmissionCompletion | PilotTypes.AsyncProtocol.CallbackCompletion _), Some success, Some retirement ->
            validRange "completion_success" success
            ordinal "retirement.owner_parameter" retirement.OwnerParameter
            let release = find "retirement.binding" retirement.Binding
            if release.Parameters.Length <> 1 || not (sameType release (snd release.Parameters.Head) native (snd native.Parameters[retirement.OwnerParameter])) || scalarFamily release release.ReturnType <> Some TypeMapper.Void then
                refuse "retirement" "async retirement must be a void native endpoint taking exactly the declared owner"
            for buffer in projection |> Option.map _.ReadOnlyBuffers |> Option.defaultValue [] do
                match buffer.Retention with
                | PilotTypes.BindingBufferRetention.DuringCall -> () // The compiler must prove backing-region domination across the cut.
                | PilotTypes.BindingBufferRetention.RetainedUntil(owner, _, releases) ->
                    if owner <> retirement.OwnerParameter || not (releases |> List.exists (fun r -> r.Binding = qualifiedBinding retirement.Binding && r.Parameter = 0)) then
                        refuse "retirement" "async retirement must match the buffer's RetainedUntil owner and release endpoint"
            match contract.Protocol with
            | PilotTypes.AsyncProtocol.CallbackCompletion parameter ->
                ordinal "callback_parameter" parameter
                let callback = projection |> Option.bind (fun b -> b.CallbackInvocations |> List.tryFind (fun c -> c.Parameter = parameter && c.Scope = "retained"))
                match callback |> Option.bind _.Retention with
                | Some retention when retention.OwnerParameter = retirement.OwnerParameter && retention.Releases |> List.exists (fun r -> r.Binding = qualifiedBinding retirement.Binding && r.Parameter = 0) -> ()
                | _ -> refuse "callback_parameter" "async callback completion requires an existing retained callback contract with matching owner and retirement"
            | _ -> ()
        | _ -> refuse "protocol" "async protocol has missing or inapplicable completion/retirement metadata"
        AsyncSelection(contract, layer)

    /// Single-unit convenience API. Project generation supplies the existing source
    /// context's per-function type resolver to selectAsyncWithFacts instead.
    let selectAsyncWithModules qualifiedBinding library declarations bindings contract =
        let typedefs =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra declarations
            |> List.choose id |> Map.ofList
        let rec resolve seen name =
            if Set.contains name seen then name
            else match Map.tryFind name typedefs with Some next -> resolve (Set.add name seen) next | None -> name
        let family _ typ = TypeMapper.tryScalar Types.LP64 (resolve Set.empty typ) |> Option.map _.Family
        let same _ left _ right = resolve Set.empty left = resolve Set.empty right
        selectAsyncWithFacts qualifiedBinding family same library declarations bindings contract

    let selectAsync library declarations bindings contract =
        selectAsyncWithModules id library declarations bindings contract

    let analyzeWithAsync library declarations bindings contract func typedefMap =
        { analyze func typedefMap with Async = Some (selectAsync library declarations bindings contract) }
