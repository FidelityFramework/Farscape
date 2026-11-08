namespace Farscape.Core

open CodeAST
open ActivePatterns
open PilotTypes

/// Callback forwarding preserves the generated entry record, complete native
/// arity, and explicit environment. Symbol-to-address conversion and managed
/// delegate trampolines are not Clef source operations.
module CallbackWrapperGenerator =

    let private toWrapperName (cName: string) : string =
        if cName.Contains('_') then
            let parts = cName.Split('_') |> Array.toList
            match parts with
            | _ :: rest when not rest.IsEmpty ->
                rest.Head + (rest.Tail |> List.map (fun s ->
                    if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                    else "") |> String.concat "")
            | _ -> cName
        else cName

    /// The module resolver identifies the owner of the entry's source contract.
    let generateTypedWithModules
        (spec: CallbackSpec) (declarations: CppParser.Declaration list)
        (namespace': string) (ctx: FidelityCodeGenerator.GenerationContext)
        (openModules: string list) (resolveModule: string -> string) : string option =
        if ctx.NativePointerSurface then
            invalidOp "Integer-address callback wrappers violate the Clef FFI boundary."
        let wrappers =
            spec.Registrations |> List.collect (fun reg ->
                // A registration refusal names its [callbacks] registrations entry (#195).
                let key = $"callbacks.registrations[{reg.Function}]"
                let refuseAt code source key parameter message =
                    Diagnostics.diagnostic code message source |> Diagnostics.withPilotKey key parameter |> Diagnostics.refuse
                let f =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Function f when f.Name = reg.Function -> Some f
                        | _ -> None)
                    |> Option.defaultWith (fun () ->
                        refuseAt Diagnostics.BindingSymbolUndeclared (Diagnostics.Located.Unknown $"no parsed header declares C symbol '{reg.Function}'")
                                 (key + ".function") None $"Callback registration '{reg.Function}' is missing from the parsed declarations.")
                let refuse code field parameter message = refuseAt code f.Location.Location (key + "." + field) parameter message
                let indexOf field name =
                    f.Parameters |> List.tryFindIndex (fun (parameter, _) -> parameter = name)
                    |> Option.defaultWith (fun () ->
                        refuse Diagnostics.ParameterNameUndeclared field None $"Callback registration '{reg.Function}' names parameter '{name}', which its native declaration does not declare.")
                // The registration's types resolve through its declaring unit's typedefs.
                let unit = FidelityCodeGenerator.inFunctionUnit ctx f
                let resolve typ =
                    FidelityCodeGenerator.resolveCType unit.TypedefMap unit.DataModel unit.OpaqueHandles unit.Delegates unit.Enums typ
                let callbackIndex = indexOf "callback_param" reg.CallbackParam
                match resolve (snd f.Parameters[callbackIndex]) with
                | FidelityCodeGenerator.FunctionPointer _ -> ()
                | _ -> refuse Diagnostics.CallbackNotFunctionPointer "callback_param" (Some callbackIndex) $"Callback registration '{reg.Function}' requires function-pointer parameter '{reg.CallbackParam}'."
                match reg.DataParam with
                | None -> ()
                | Some name ->
                    let dataIndex = indexOf "data_param" name
                    match resolve (snd f.Parameters[dataIndex]) with
                    | FidelityCodeGenerator.DataPointer _ | FidelityCodeGenerator.OpaqueHandle _ -> ()
                    | _ -> refuse Diagnostics.CallbackEnvironmentNotDataPointer "data_param" (Some dataIndex) $"Callback registration '{reg.Function}' requires data-pointer environment parameter '{name}'."
                    // A retained entry outlives the call (#191): it must be capture-free, and an
                    // environment paired with it would be retained with no declared retirement.
                    let retained =
                        ctx.Bindings |> Map.tryFind f.Name
                        |> Option.exists (fun binding -> binding.CallbackInvocations |> List.exists (fun c -> c.Parameter = callbackIndex && c.Scope = "retained"))
                    if retained then
                        refuse Diagnostics.RetainedCallbackCaptures "data_param" (Some dataIndex)
                            $"Callback registration '{reg.Function}' pairs retained callback '{reg.CallbackParam}' with environment parameter '{name}': a retained entry must be capture-free (FnPtr.ofFunction on a closed module function), and a captured environment would be retained with no declared retirement."
                FidelityCodeGenerator.generateDeclaredFunctionDecls ctx "" (resolveModule f.Name) f
                |> List.choose (function
                    | LetBinding(name, parameters, returnType, _, _) when name = f.Name ->
                        Some (LetBinding(toWrapperName f.Name, parameters, returnType,
                            FunctionCall(resolveModule f.Name, f.Name, parameters |> List.map (fun p -> Identifier p.Name)), []))
                    | _ -> None))
        if wrappers.IsEmpty then None
        else
            Some (CodeRenderer.render (Module(namespace', "Layer 2 callbacks: declared entries and explicit environments",
                (openModules |> List.map OpenModule) @ wrappers)))

    let generateTyped spec declarations namespace' ctx openModules =
        let sourceModule =
            openModules |> List.tryLast
            |> Option.defaultWith (fun () -> invalidOp "Typed callback forwarding requires its source binding module.")
        generateTypedWithModules spec declarations namespace' ctx openModules (fun _ -> sourceModule)

    /// Legacy integer-address generation is diagnosed before any source is emitted.
    let generateNative
        (spec: CallbackSpec) (_declarations: CppParser.Declaration list)
        (_namespace': string) (_model: Types.PlatformABI) (_openModules: string list) : string option =
        if spec.Registrations.IsEmpty then None
        else invalidOp "Integer-address callback wrappers violate the Clef FFI boundary. Use generateTypedWithModules with declared entry and environment contracts."

    /// Legacy symbol/delegate wrappers have no admitted Clef contract.
    let generate
        (spec: CallbackSpec) (_declarations: CppParser.Declaration list)
        (_namespace': string) (_model: Types.PlatformABI)
        (_openModules: string list) (_l2CallbackModule: string) : string option =
        if spec.Registrations.IsEmpty && spec.ListenerStructs.IsEmpty then None
        else invalidOp "Symbol lookup and managed delegate callback wrappers are not admitted Clef source. Use typed entry declarations and explicit environment contracts."

    /// Listener construction needs a declared value layout and an entry contract
    /// for every field; an integer-address record builder cannot provide them.
    let generateL2
        (spec: CallbackSpec) (_declarations: CppParser.Declaration list)
        (_namespace': string) (_openModules: string list) : string option =
        if spec.ListenerStructs.IsEmpty then None
        else
            spec.ListenerStructs
            |> List.map (fun listener ->
                Diagnostics.diagnostic Diagnostics.GenericListenerUnadmitted
                    $"listener struct '{listener.Name}': generic listener builders require declared native entry records and initialized fields; integer-address listener builders are not admitted Clef source."
                    (Diagnostics.Located.Unknown "generateL2 receives listener names without their declarations")
                |> Diagnostics.withPilotKey "callbacks.listener_structs" None)
            |> fun ds -> Diagnostics.raiseRefusal (Diagnostics.refusalsOf ds "")
