namespace Farscape.Core

open CodeAST
open PilotTypes
open ActivePatterns

/// Generates Layer 2/3 callback wrappers for C callback parameters.
///
/// Three patterns:
///   Pattern A (dlsym registration): replaces function pointer param with string symbol name,
///     resolves via dlsym, passes nativeint to Layer 1 function.
///   Pattern B (listener struct): builds a struct of function pointers by resolving
///     each field's symbol name via dlsym, returns the populated struct.
///   Pattern C (managed trampoline): generates a delegate type matching the C callback
///     signature, accepts a managed function, pins via GCHandle, and converts to
///     nativeint via Marshal.GetFunctionPointerForDelegate.
///
/// Patterns A and B depend on Fidelity.Libc.DynamicLink.dlsym.
/// Pattern C depends on System.Runtime.InteropServices.Marshal and GCHandle.
module CallbackWrapperGenerator =

    /// Convert a C function name to a camelCase wrapper name.
    /// g_idle_add → idleAdd, wl_proxy_add_listener → proxyAddListener
    let private toWrapperName (cName: string) : string =
        let parts = cName.Split('_') |> Array.toList
        // Drop common short library prefixes (g_, wl_, xdg_, etc.)
        let meaningful =
            match parts with
            | p :: rest when p.Length <= 3 && rest.Length >= 2 -> rest
            | _ -> parts
        match meaningful with
        | [] -> cName
        | first :: rest ->
            first + (rest |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat "")

    /// Convert a snake_case struct name to a PascalCase builder function name.
    /// wl_pointer_listener → buildWlPointerListener
    /// xdg_surface_listener → buildXdgSurfaceListener
    let private toBuilderName (structName: string) : string =
        let parts = structName.Split('_') |> Array.toList
        let pascal =
            parts |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat ""
        "build" + pascal

    /// Map a C parameter type to a Clef type for wrapper signatures.
    /// Simplified version — pointers become nativeint, basic types map directly.
    let private mapParamType (cType: string) (model: Types.PlatformABI) : FsType =
        if cType.Contains("(*)") || cType.Contains("(**)") then Named "nativeint"
        elif cType.Contains("*") then Named "nativeint"
        elif cType.Contains("void") && not (cType.Contains("*")) then Unit
        else
            let trimmed = cType.Replace("const ", "").Replace("unsigned ", "u").Replace("signed ", "").Trim()
            match trimmed with
            | "int" -> Named "int"          // NTU register-width dimensional
            | "int32_t" -> Named "int32"
            | "uint" -> Named "uint"        // NTU register-width dimensional
            | "uint32_t" | "guint" -> Named "uint32"
            | "long" -> Named "int64"
            | "ulong" | "unsigned long" -> Named "uint64"
            | "uint64_t" | "gulong" -> Named "uint64"
            | "int64_t" -> Named "int64"
            | "char" | "uchar" | "gchar" -> Named "byte"
            | "short" | "int16_t" -> Named "int16"
            | "ushort" | "uint16_t" -> Named "uint16"
            | "float" -> Named "float32"
            | "double" -> Named "float"
            | "size_t" -> Named "unativeint"
            | "ssize_t" -> Named "nativeint"
            | _ -> Named "nativeint"

    /// Map a C return type to a Clef type for wrapper signatures.
    let private mapReturnType (cType: string) (model: Types.PlatformABI) : FsType =
        let trimmed = cType.Trim()
        if trimmed = "void" then Unit
        elif trimmed.Contains("*") then Named "nativeint"
        else mapParamType trimmed model

    // =========================================================================
    // Pattern A: Registration Function Wrappers
    // =========================================================================

    /// Generate a wrapper for a callback registration function.
    /// Looks up the full function signature from declarations, then:
    ///   - Replaces callback param with handlerSymbol: string
    ///   - Removes userdata param (passes 0n)
    ///   - Resolves symbol via dlsym, calls original L1 function
    let generateRegistrationWrapper
        (reg: CallbackRegistration)
        (funcDecl: CppParser.FunctionDecl)
        (model: Types.PlatformABI)
        : FsDecl list =

        let wrapperName = toWrapperName reg.Function

        // Build wrapper parameters: replace callback with string, remove userdata
        let wrapperParams =
            funcDecl.Parameters |> List.choose (fun (name, cType) ->
                if name = reg.CallbackParam then
                    Some { Name = "handlerSymbol"; Type = Named "string" }
                elif reg.DataParam = Some name then
                    None  // userdata removed — we pass 0n
                else
                    Some { Name = name; Type = mapParamType cType model })

        let returnType = mapReturnType funcDecl.ReturnType model

        // Body: let handler = match dlsym (Some 0n) (Some handlerSymbol.Pointer) with Some v -> v | None -> 0n
        //       in originalFunc arg1 handler arg2 ...
        // Fully qualified to L1 (L2 dlsym returns Result<nativeint, string>, but L1 returns Option)
        let dlsymCall =
            MatchExpr(
                FunctionCall("Fidelity.Libc.DynamicLink", "dlsym",
                    [FunctionCall("", "Some", [Literal "0n"])
                     FunctionCall("", "Some", [Identifier "handlerSymbol.Pointer"])]),
                [("Some v", Identifier "v"); ("None", Literal "0n")])
        let body =
            LetIn("handler", dlsymCall,
                FunctionCall("", reg.Function,
                    funcDecl.Parameters |> List.map (fun (name, _) ->
                        if name = reg.CallbackParam then Identifier "handler"
                        elif reg.DataParam = Some name then Literal "0n"
                        else Identifier name)))

        let doc =
            match reg.DataParam with
            | Some dp -> $"Register callback by symbol name. Resolves via dlsym; passes 0n for {dp}."
            | None -> $"Register callback by symbol name. Resolves via dlsym."

        [ XmlDoc doc
          LetBinding(wrapperName, wrapperParams, returnType, body, []) ]

    // =========================================================================
    // Pattern B: Listener Struct Builder
    // =========================================================================

    /// A field is a callback if it's a C function pointer or a known delegate type.
    let private isCallbackField (delegateNames: Set<string>) (f: CppParser.FieldDecl) =
        f.Type.Contains("(*)") || f.Type.Contains("(**)") || Set.contains f.Type delegateNames

    /// Generate a direct builder function for a listener struct (Layer 2).
    /// Each callback field becomes a nativeint parameter. Record construction
    /// happens here — in the same package where the struct types are defined,
    /// avoiding CCS cross-package record field resolution issues.
    let generateListenerDirectBuilder
        (ls: ListenerStruct)
        (structDecl: CppParser.StructDecl)
        (delegateNames: Set<string>)
        : FsDecl list =

        let builderName = toBuilderName ls.Name

        let callbackFields = structDecl.Fields |> List.filter (isCallbackField delegateNames)

        if callbackFields.IsEmpty then []
        else
            let params' =
                callbackFields |> List.map (fun f ->
                    { Name = f.Name; Type = Named "nativeint" })

            let fields =
                structDecl.Fields |> List.mapi (fun i f ->
                    let fieldName =
                        if i = 0 then $"{ls.Name}.{cleanParamName f.Name}"
                        else cleanParamName f.Name
                    if isCallbackField delegateNames f then
                        (fieldName, Identifier (cleanParamName f.Name))
                    else
                        (fieldName, Literal "NativeDefault.zeroed ()"))

            let body = RecordConstruction fields

            [ XmlDoc $"Build a {ls.Name} from resolved function pointers."
              LetBinding(builderName, params', Named ls.Name, body, []) ]

    /// Generate a bridge builder function for a listener struct (Layer 3).
    /// Each callback field becomes a string parameter (symbol name).
    /// The builder resolves symbols via dlsym then delegates to the L2 direct builder.
    let generateListenerBuilder
        (ls: ListenerStruct)
        (structDecl: CppParser.StructDecl)
        (delegateNames: Set<string>)
        (l2CallbackModule: string)
        : FsDecl list =

        let builderName = toBuilderName ls.Name

        let callbackFields = structDecl.Fields |> List.filter (isCallbackField delegateNames)

        if callbackFields.IsEmpty then []
        else
            let params' =
                callbackFields |> List.map (fun f ->
                    { Name = f.Name + "Sym"; Type = Named "string" })

            // Resolve each symbol via dlsym, binding each result to a local before calling L2 builder.
            // dlsym returns option<nativeint>; unwrap via match (None → 0n).
            let resolvedNames =
                callbackFields |> List.map (fun f -> f.Name + "Ptr")

            let builderCall =
                FunctionCall(l2CallbackModule, builderName,
                    resolvedNames |> List.map Identifier)

            // Chain LetIn bindings: let globalPtr = match dlsym ... | ... in let global_removePtr = ... in builderCall
            let body =
                List.foldBack2 (fun (f: CppParser.FieldDecl) resolvedName acc ->
                    let symParam = f.Name + "Sym"
                    let dlsymExpr =
                        MatchExpr(
                            FunctionCall("Fidelity.Libc.DynamicLink", "dlsym",
                                [FunctionCall("", "Some", [Literal "0n"])
                                 FunctionCall("", "Some", [Identifier $"{symParam}.Pointer"])]),
                            [("Some v", Identifier "v"); ("None", Literal "0n")])
                    LetIn(resolvedName, dlsymExpr, acc))
                    callbackFields resolvedNames builderCall

            [ XmlDoc $"Build a {ls.Name} by resolving C symbol names via dlsym(RTLD_DEFAULT)."
              XmlDoc "Each parameter is a C symbol name resolved at runtime."
              LetBinding(builderName, params', Named ls.Name, body, []) ]

    // =========================================================================
    // Pattern C: Managed Trampoline Wrappers
    // =========================================================================

    /// Parse a C function pointer type string into (returnType, paramTypes).
    /// Input: "void (*)(xrtRunHandle, enum ert_cmd_state, void *)"
    /// Output: Some ("void", ["xrtRunHandle"; "enum ert_cmd_state"; "void *"])
    let private parseFunctionPointerType (cType: string) : (string * string list) option =
        let trimmed = cType.Trim()
        // Pattern: returnType (*)(paramTypes)
        let parenStar = trimmed.IndexOf("(*)")
        if parenStar < 0 then None
        else
            let retType = trimmed.[..parenStar - 1].Trim()
            let afterStar = trimmed.[parenStar + 3..].Trim()
            if afterStar.Length < 2 || afterStar.[0] <> '(' then None
            else
                let inner = afterStar.[1..afterStar.Length - 2].Trim()
                let paramTypes =
                    if inner = "" || inner = "void" then []
                    else
                        inner.Split(',')
                        |> Array.map (fun s -> s.Trim())
                        |> Array.toList
                Some (retType, paramTypes)

    /// Strip the parameter name from a C type+name string, leaving just the type.
    /// "const char * bdf" → "const char *", "void * data" → "void *", "int index" → "int"
    let private stripParamName (cTypeWithName: string) : string =
        let trimmed = cTypeWithName.Trim()
        if trimmed.Contains("*") then
            // Pointer type: everything up to and including the last *
            let lastStar = trimmed.LastIndexOf('*')
            trimmed.[..lastStar].Trim()
        else
            // Non-pointer: last token is the name, rest is type. But if single token, it's a type.
            let parts = trimmed.Split([|' '|], System.StringSplitOptions.RemoveEmptyEntries)
            if parts.Length <= 1 then trimmed
            else parts.[..parts.Length - 2] |> String.concat " "

    /// Generate a PascalCase delegate name from a registration function name.
    /// xrtRunSetCallback → XrtRunCallbackDelegate
    let private toDelegateName (regFunction: string) (callbackParam: string) : string =
        let parts = regFunction.Split('_') |> Array.toList
        let pascal =
            parts |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat ""
        pascal + "Delegate"

    /// Generate a trampoline wrapper for a callback registration function.
    /// Produces:
    ///   1. A delegate type matching the C callback signature
    ///   2. A wrapper that accepts a managed function, pins it as a delegate,
    ///      converts to nativeint via Marshal.GetFunctionPointerForDelegate,
    ///      and calls the L1 registration function.
    let generateTrampolineWrapper
        (reg: CallbackRegistration)
        (funcDecl: CppParser.FunctionDecl)
        (model: Types.PlatformABI)
        : FsDecl list =

        // Find the callback parameter's C type string
        let callbackCType =
            funcDecl.Parameters
            |> List.tryFind (fun (name, _) -> name = reg.CallbackParam)
            |> Option.map snd

        match callbackCType |> Option.bind parseFunctionPointerType with
        | None -> []  // Can't parse the function pointer type; skip
        | Some (retType, paramCTypes) ->

        let delegateName = toDelegateName funcDecl.Name reg.CallbackParam

        // Strip the userdata param from the callback signature for the managed function type.
        // Convention: the last void* in the callback is the userdata param.
        let hasUserdata = reg.DataParam.IsSome
        let managedParamCTypes =
            if hasUserdata && paramCTypes.Length > 0 then
                // Remove the last void* parameter (userdata forwarded by the runtime)
                let lastType = paramCTypes |> List.last |> stripParamName
                if lastType = "void *" || lastType = "void*" then
                    paramCTypes |> List.take (paramCTypes.Length - 1)
                else paramCTypes
            else paramCTypes

        // Build delegate type: all original callback params (including userdata)
        let delegateParams =
            paramCTypes |> List.mapi (fun i cType ->
                let cleanType = stripParamName cType
                ($"p{i}", mapParamType cleanType model))

        let delegateRetType = mapReturnType retType model

        let delegateDecl =
            DelegateType(delegateName, delegateParams, delegateRetType,
                Some $"Native callback delegate for {funcDecl.Name}.")

        // Build the managed function parameter type as a Clef function type string.
        // For the wrapper, we accept a curried function: (p0Type -> p1Type -> retType)
        let managedParamTypes =
            managedParamCTypes |> List.map (fun cType ->
                let cleanType = stripParamName cType
                mapParamType cleanType model)

        let managedFnType =
            let paramStr =
                managedParamTypes
                |> List.map (fun t ->
                    match t with
                    | Named n -> n
                    | Unit -> "unit"
                    | _ -> "nativeint")
                |> String.concat " -> "
            let retStr =
                match delegateRetType with
                | Unit -> "unit"
                | Named n -> n
                | _ -> "nativeint"
            if paramStr = "" then retStr
            else $"{paramStr} -> {retStr}"

        // Wrapper function name: same as dlsym wrapper but with "Managed" suffix
        let wrapperName = toWrapperName funcDecl.Name + "Managed"

        // Build wrapper parameters: replace callback with managed function, remove userdata
        let wrapperParams =
            funcDecl.Parameters |> List.choose (fun (name, cType) ->
                if name = reg.CallbackParam then
                    Some { Name = "handler"; Type = Named $"({managedFnType})" }
                elif reg.DataParam = Some name then
                    None
                else
                    Some { Name = name; Type = mapParamType cType model })

        let returnType = mapReturnType funcDecl.ReturnType model

        // Body:
        //   let wrappedDelegate = DelegateName(fun p0 p1 p2 -> handler p0 p1)
        //   let pin = System.Runtime.InteropServices.GCHandle.Alloc(wrappedDelegate)
        //   let fnPtr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wrappedDelegate)
        //   originalFunc arg1 fnPtr 0n ...
        //
        // Note: The pin keeps the delegate alive. Caller is responsible for freeing via pin.Free().
        // We return a tuple of (result, pin) so the caller can manage lifetime.

        // For simplicity, generate as RawExpr since the nested delegate construction
        // and Marshal calls are complex to express in the current AST.
        let managedParamNames =
            managedParamCTypes |> List.mapi (fun i _ -> $"p{i}")
        let allParamNames =
            paramCTypes |> List.mapi (fun i _ -> $"p{i}")
        let handlerCallArgs =
            managedParamNames |> String.concat " "
        let lambdaParams =
            allParamNames |> String.concat " "

        // Build the userdata handling: if original has userdata, the lambda accepts it but ignores it
        let lambdaBody =
            if hasUserdata && managedParamNames.Length < allParamNames.Length then
                $"handler {handlerCallArgs}"
            else
                $"handler {handlerCallArgs}"

        let delegateConstruction = $"{delegateName}(fun {lambdaParams} -> {lambdaBody})"
        let l1CallArgs =
            funcDecl.Parameters |> List.map (fun (name, _) ->
                if name = reg.CallbackParam then "fnPtr"
                elif reg.DataParam = Some name then "0n"
                else name)
            |> String.concat " "

        let bodyCode =
            [ $"let wrappedDelegate = {delegateConstruction}"
              "let pin = System.Runtime.InteropServices.GCHandle.Alloc(wrappedDelegate)"
              "let fnPtr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wrappedDelegate)"
              $"let result = {funcDecl.Name} {l1CallArgs}"
              "(result, pin)" ]
            |> String.concat "\n        "

        // Return type is a tuple: (originalReturn * GCHandle)
        let retTypeStr =
            match returnType with
            | Named n -> n
            | Unit -> "unit"
            | _ -> "nativeint"
        let tupleReturnType = Named $"({retTypeStr} * System.Runtime.InteropServices.GCHandle)"

        let wrapperDecl =
            LetBinding(wrapperName, wrapperParams, tupleReturnType,
                RawExpr bodyCode, [])

        [ BlankLine
          delegateDecl
          XmlDoc $"Register a managed function as callback. Returns (result, pin)."
          XmlDoc "The caller must keep the GCHandle alive for the callback's lifetime"
          XmlDoc "and call pin.Free() when the callback is no longer needed."
          wrapperDecl ]

    // =========================================================================
    // Complete Module Generation
    // =========================================================================

    /// Collect delegate names from declarations for listener field type matching.
    let private collectDelegateNames (declarations: CppParser.Declaration list) =
        declarations |> List.choose (function
            | CppParser.Declaration.Delegate d -> Some d.Name
            | _ -> None)
        |> Set.ofList

    /// Look up a struct declaration by name.
    let private findStructDecl (declarations: CppParser.Declaration list) (name: string) =
        declarations |> List.tryPick (function
            | CppParser.Declaration.Struct s when s.Name = name -> Some s
            | _ -> None)

    /// Generate Layer 2 direct listener builder decls (record construction with nativeint params).
    /// These belong in the main package where the struct types are defined.
    let generateL2Decls
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        : FsDecl list =

        let delegateNames = collectDelegateNames declarations

        spec.ListenerStructs |> List.collect (fun ls ->
            match findStructDecl declarations ls.Name with
            | Some s -> generateListenerDirectBuilder ls s delegateNames
            | None -> [])

    /// Generate Layer 2 callback decls (registration wrappers + listener builders with dlsym).
    let generateDecls
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (model: Types.PlatformABI)
        (l2CallbackModule: string)
        : FsDecl list =

        let delegateNames = collectDelegateNames declarations

        let registrationDecls =
            spec.Registrations |> List.collect (fun reg ->
                let funcDecl =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Function f when f.Name = reg.Function -> Some f
                        | _ -> None)
                match funcDecl with
                | Some f -> generateRegistrationWrapper reg f model
                | None -> [])

        let trampolineDecls =
            spec.Registrations |> List.collect (fun reg ->
                let funcDecl =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Function f when f.Name = reg.Function -> Some f
                        | _ -> None)
                match funcDecl with
                | Some f -> generateTrampolineWrapper reg f model
                | None -> [])

        let listenerDecls =
            spec.ListenerStructs |> List.collect (fun ls ->
                match findStructDecl declarations ls.Name with
                | Some s -> generateListenerBuilder ls s delegateNames l2CallbackModule
                | None -> [])

        let sections =
            [ registrationDecls; trampolineDecls; listenerDecls ]
            |> List.filter (not << List.isEmpty)
        match sections with
        | [] -> []
        | _ -> sections |> List.reduce (fun a b -> a @ [ BlankLine ] @ b)

    /// Generate the complete Layer 2 callback wrappers module as a rendered source string.
    let generate
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (model: Types.PlatformABI)
        (openModules: string list)
        (l2CallbackModule: string)
        : string option =

        let decls = generateDecls spec declarations model l2CallbackModule
        if decls.IsEmpty then None
        else
            let opens = openModules |> List.map OpenModule
            let moduleDecl = Module(namespace', "Callback wrappers — dlsym symbol resolution and managed trampolines", opens @ decls)
            Some (CodeRenderer.render moduleDecl)

    /// Generate the Layer 2 direct listener builders module as a rendered source string.
    /// This goes in the main package where struct types are in scope.
    let generateL2
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (openModules: string list)
        : string option =

        let decls = generateL2Decls spec declarations
        if decls.IsEmpty then None
        else
            let opens = openModules |> List.map OpenModule
            let moduleDecl = Module(namespace', "Listener builder functions — direct record construction", opens @ decls)
            Some (CodeRenderer.render moduleDecl)
