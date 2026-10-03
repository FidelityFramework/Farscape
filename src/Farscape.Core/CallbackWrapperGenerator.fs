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
                let f =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Function f when f.Name = reg.Function -> Some f
                        | _ -> None)
                    |> Option.defaultWith (fun () -> invalidOp $"Callback registration '{reg.Function}' is missing from the parsed declarations.")
                let resolve typ =
                    FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums typ
                let callbackType =
                    f.Parameters |> List.tryFind (fun (name, _) -> name = reg.CallbackParam)
                    |> Option.map (snd >> resolve)
                match callbackType with
                | Some (FidelityCodeGenerator.FunctionPointer _) -> ()
                | _ -> invalidOp $"Callback registration '{reg.Function}' requires function-pointer parameter '{reg.CallbackParam}'."
                match reg.DataParam with
                | None -> ()
                | Some name ->
                    match f.Parameters |> List.tryFind (fun (parameter, _) -> parameter = name) |> Option.map (snd >> resolve) with
                    | Some (FidelityCodeGenerator.DataPointer _ | FidelityCodeGenerator.OpaqueHandle _) -> ()
                    | _ -> invalidOp $"Callback registration '{reg.Function}' requires data-pointer environment parameter '{name}'."
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
        else invalidOp "Generic listener builders require declared native entry records and initialized fields; integer-address listener builders are not admitted Clef source."
