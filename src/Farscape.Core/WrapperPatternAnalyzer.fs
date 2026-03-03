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
        }
