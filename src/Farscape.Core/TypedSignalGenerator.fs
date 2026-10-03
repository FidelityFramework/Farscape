namespace Farscape.Core

open System.IO
open IntrospectionParser
open CodeAST

/// GObject signal bindings preserve declared native carriers and nullable inputs.
/// ClosedCallbackDescriptor admits only a known module handler without captures;
/// this path allocates no callback environment. Resource, absence and ABI admission
/// remain compiler obligations, and a no-op release entry proves no captured lifetime.
module TypedSignalGenerator =
    let private q (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
    let private pascal (s: string) =
        s.Split([| '-'; '_' |]) |> Array.map (fun p -> if p = "" then "" else p.Substring(0, 1).ToUpperInvariant() + p.Substring(1)) |> String.concat ""
    let private camel (s: string) = let p = pascal s in if p = "" then p else p.Substring(0, 1).ToLowerInvariant() + p.Substring(1)
    let private callbackIdentifier (name: string) =
        name |> Seq.map (fun c -> if System.Char.IsLetterOrDigit c then string c else $"_{int c:X4}_") |> String.concat ""
    let private line xs = String.concat "\n" xs + "\n"
    let private parameter (name, typ) = $"{{ Name = {q name}; Type = {typ}; PassBy = Value }}"
    let private functionRecord symbol parameters ret =
        "{ CName = " + q symbol + "; Parameters = [| " + (parameters |> List.map parameter |> String.concat "; ") + " |]; ReturnType = " + ret + "; CallingConvention = CDecl; OwnershipTransfer = Borrowed }"

    type private Carrier = { Clef: string; Ref: string; Type: FsType }

    let private resolve (ctx: FidelityCodeGenerator.GenerationContext) cType =
        FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums cType

    let private isDataPointer = function
        | FidelityCodeGenerator.DataPointer _ | FidelityCodeGenerator.OpaqueHandle _ -> true
        | FidelityCodeGenerator.Scalar r when r.Family = TypeMapper.Pointer -> true
        | _ -> false

    let private builtinPointees = Set.ofList ["unit"; "int"; "float"; "bool"]

    let rec private carrierNames = function
        | Generic("CHandle", Named name) when not (builtinPointees.Contains name) -> Set.singleton name
        | Generic(_, inner) -> carrierNames inner
        | _ -> Set.empty

    let private requireNativeType (ctx: FidelityCodeGenerator.GenerationContext) where name =
        if not (ctx.OpaqueHandles.Contains name || ctx.Structs.ContainsKey name || ctx.TypedefMap.ContainsKey name) then
            failwith $"{where}: native pointee '{name}' is not declared in the parsed headers; GIR alone cannot supply its carrier"

    /// Known nominal carriers which a project must include in its shared Types module.
    /// Names come from resolved header types; GIR-only pointer names are refused.
    let requiredTypes (signals: SignalDecl list) (declarations: CppParser.Declaration list)
                      (ctx: FidelityCodeGenerator.GenerationContext) : Set<string> =
        let signalTypes = signals |> List.collect (fun signal ->
            (signal.ClassCType + " *") :: signal.ReturnCType :: (signal.Parameters |> List.map (fun (_, cType, _) -> cType)))
        let releaseTypes =
            declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = "g_signal_connect_data" -> Some f | _ -> None)
            |> Option.bind (fun f -> f.Parameters |> List.tryItem 4)
            |> Option.bind (snd >> FidelityCodeGenerator.resolveCallbackNativeSignature ctx)
            |> Option.map (fun (_, parameters) -> parameters |> List.map snd)
            |> Option.defaultValue []
        let names =
            if signals.IsEmpty then Set.empty
            else signalTypes @ releaseTypes
                 |> List.map (resolve ctx >> FidelityCodeGenerator.clefTypeOf >> carrierNames)
                 |> List.fold Set.union Set.empty
        names |> Set.iter (requireNativeType ctx "GObject signal contract")
        names

    /// A nominal handle must name a carrier supplied by the binding's Types module.
    /// GIR's C spelling alone does not declare an interior marker or resource contract.
    let rec private requireDeclaredCarrier (ctx: FidelityCodeGenerator.GenerationContext) where = function
        | Generic("CHandle", Named name) when not (builtinPointees.Contains name) ->
            requireNativeType ctx where name
            let declared =
                ctx.OpaqueHandles.Contains name || ctx.PhantomMarkers.Contains name
                || (ctx.Structs.ContainsKey name && ctx.ValueStructs.Contains name)
            if not declared then
                failwith $"{where}: native pointee '{name}' has no declared carrier in the binding Types module"
        | Generic(_, inner) -> requireDeclaredCarrier ctx where inner
        | _ -> ()

    /// Incoming callback values use the same outer-nullability policy as header callbacks.
    /// Explicit GIR evidence supplements the native declaration; omission proves nothing.
    let private carrierOf (ctx: FidelityCodeGenerator.GenerationContext) where cType nullable nonnull : Carrier =
        let resolved = resolve ctx cType
        if nullable && nonnull then failwith $"{where}: contradictory GIR nullability evidence"
        match resolved with
        | FidelityCodeGenerator.Scalar r when r.Family = TypeMapper.Float ->
            failwith $"{where}: floating-point C type '{cType}' has no supported callback entry declaration, so the signal is not projected"
        | FidelityCodeGenerator.FunctionPointer _ -> failwith $"{where}: a function-pointer value '{cType}' requires a supported signal callback adapter"
        | _ -> ()
        if (nullable || nonnull) && not (isDataPointer resolved) then
            failwith $"{where}: GIR nullability evidence does not describe a native data pointer"
        let repr =
            FidelityCodeGenerator.abiReprOf ctx.DataModel cType resolved
            |> Option.defaultWith (fun () -> failwith $"{where}: the C type '{cType}' is not declared in the parsed headers")
        let qualified = cType + (if nullable then " _Nullable" elif nonnull then " _Nonnull" else "")
        let valueType =
            match resolve ctx (qualified + " (*)(void)") with
            | FidelityCodeGenerator.FunctionPointer([], ret) -> ret
            | _ -> failwith $"{where}: no complete native callback value contract for '{cType}'"
        requireDeclaredCarrier ctx where valueType
        { Clef = CodeRenderer.renderType valueType; Ref = DescriptorGenerator.typeRefSource repr; Type = valueType }

    let private requirePointer ctx where cType =
        if not (isDataPointer (resolve ctx cType)) then
            failwith $"{where}: requires a declared native data-pointer parameter"

    let private requireInteger ctx where cType =
        match resolve ctx cType with
        | FidelityCodeGenerator.CEnum _ -> ()
        | FidelityCodeGenerator.Scalar r when r.Family = TypeMapper.Signed || r.Family = TypeMapper.Unsigned -> ()
        | _ -> failwith $"{where}: requires a declared native integer representation"

    let private isCharPointer (ctx: FidelityCodeGenerator.GenerationContext) cType =
        match cType with
        | ActivePatterns.ParsedCType info when info.PointerDepth = 1 ->
            TypeMapper.cleanTypeName (FidelityCodeGenerator.resolveType ctx.TypedefMap info.BaseType) = "char"
        | _ -> false

    /// Validates every contract and returns source before any output directory is created.
    /// Projects may call this during their own preflight to avoid partial output on failure.
    let render (nsPrefix: string) (signals: SignalDecl list) (declarations: CppParser.Declaration list)
               (ctx: FidelityCodeGenerator.GenerationContext) : string option =
        if signals.IsEmpty then None else
        if ctx.NativePointerSurface then
            failwith "GObject signals cannot use experimental_native_pointer_surface; raw address carriers violate the Clef FFI boundary."
        let connect =
            declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = "g_signal_connect_data" -> Some f | _ -> None)
            |> Option.defaultWith (fun () -> failwith "GObject signals require `g_signal_connect_data`; list glib-object.h among the pilot headers")
        if connect.Parameters.Length <> 6 then
            failwith "g_signal_connect_data requires its complete six-parameter native declaration"
        let nativeTypes = connect.Parameters |> List.map snd
        requirePointer ctx "g_signal_connect_data instance" nativeTypes.[0]
        if not (isCharPointer ctx nativeTypes.[1]) then
            failwith "g_signal_connect_data detailed_signal requires a declared native char-pointer text parameter"
        requirePointer ctx "g_signal_connect_data data" nativeTypes.[3]
        requireInteger ctx "g_signal_connect_data flags" nativeTypes.[5]
        requireInteger ctx "g_signal_connect_data result" connect.ReturnType
        match resolve ctx nativeTypes.[2] with
        | FidelityCodeGenerator.FunctionPointer([], ret) when CodeRenderer.renderType ret = "unit" -> ()
        | _ -> failwith "g_signal_connect_data handler requires its declared generic void callback slot"
        let releaseRet, releaseArgs =
            FidelityCodeGenerator.resolveCallbackNativeSignature ctx nativeTypes.[4]
            |> Option.defaultWith (fun () -> failwith "g_signal_connect_data destroy_data requires its declared native release callback")
        if releaseArgs.Length <> 2 || CodeRenderer.renderType (resolve ctx releaseRet |> FidelityCodeGenerator.clefTypeOf) <> "unit" then
            failwith "g_signal_connect_data destroy_data requires a two-parameter callback with native void result"
        releaseArgs |> List.iteri (fun index (_, cType) -> requirePointer ctx $"g_signal_connect_data destroy_data parameter {index}" cType)
        // Resolving the release slot also rejects unsupported optional entry contracts.
        match resolve ctx nativeTypes.[4] with
        | FidelityCodeGenerator.FunctionPointer _ -> ()
        | _ -> failwith "g_signal_connect_data destroy_data is not a declared native callback slot"
        let releaseData = carrierOf ctx "GClosureNotify data" (snd releaseArgs.[0]) false false
        let releaseClosure = carrierOf ctx "GClosureNotify closure" (snd releaseArgs.[1]) false false
        let release = "FnPtr<" + releaseData.Clef + " -> " + releaseClosure.Clef + " -> unit>"
        let releaseRecord = callbackIdentifier nsPrefix + "_SignalReleaseEntry"
        let releaseSignature = functionRecord (nsPrefix + "::GClosureNotify") [ "data", releaseData.Ref; "closure", releaseClosure.Ref ] "Void"
        let flags = carrierOf ctx "g_signal_connect_data flags" nativeTypes.[5] false false
        let handlerId = carrierOf ctx "g_signal_connect_data result" connect.ReturnType false false
        let pointer = DescriptorGenerator.typeRefSource (TypeMapper.pointerRepr ctx.DataModel "native signal entry")
        let userData = carrierOf ctx "signal user_data" nativeTypes.[3] false false
        let signalDecls =
            signals |> List.collect (fun s ->
                let where = $"signal '{s.Namespace}.{s.Class}::{s.Name}'"
                let names = s.Parameters |> List.map (fun (name, _, _) -> name)
                if (names |> List.contains "") || (List.distinct names).Length <> names.Length then
                    failwith $"{where}: callback parameter names must be present and unique for nullability admission"
                let instance = carrierOf ctx ($"{where}: instance") (s.ClassCType + " *") false false
                let instanceHandle =
                    match instance.Type with Generic("option", inner) -> CodeRenderer.renderType inner | typ -> CodeRenderer.renderType typ
                let parameters = s.Parameters |> List.map (fun (name, cType, nullable) ->
                    name, carrierOf ctx ($"{where}: parameter '{name}'") cType nullable (s.NonnullParameters.Contains name))
                let result = carrierOf ctx ($"{where}: result") s.ReturnCType s.ReturnNullable s.ReturnNonnull
                let localName = pascal s.Class + pascal s.Name
                let stem =
                    if signals |> List.exists (fun other -> other.Namespace <> s.Namespace && pascal other.Class + pascal other.Name = localName) then
                        pascal s.Namespace + localName
                    else localName
                let record = stem + "Listener"
                let entry = "FnPtr<" + ((instance.Clef :: (parameters |> List.map (fun (_, c) -> c.Clef)) @ [ userData.Clef; result.Clef ]) |> String.concat " -> ") + ">"
                let signature =
                    functionRecord (s.ClassCType + "::" + s.Name)
                        (("instance", instance.Ref) :: (parameters |> List.map (fun (n, c) -> n, c.Ref)) @ [ "user_data", userData.Ref ]) result.Ref
                let native = "connect" + stem + "Native"
                let connectName = "connect" + stem
                let factory = "make" + stem + "Entry"
                let adapter = "adapt" + stem
                let moduleName = nsPrefix + ".Bridge.Signals"
                let handlerTypes = if parameters.IsEmpty then [ "unit" ] else parameters |> List.map (fun (_, c) -> c.Clef)
                let handlerType = "(" + String.concat " -> " (handlerTypes @ [ result.Clef ]) + ")"
                let arguments = parameters |> List.mapi (fun i (_, c) -> $"(value{i}: {c.Clef})") |> String.concat " "
                let applied = if parameters.IsEmpty then "()" else parameters |> List.mapi (fun i _ -> $"value{i}") |> String.concat " "
                let doc = s.Documentation |> Option.defaultValue $"`{s.Name}` on {s.ClassCType}."
                [ $"/// {doc}"
                  $"type {record} = {{ {pascal s.Name}: {entry} }}"
                  ""
                  "let " + camel stem + "HandlerDescriptor: Expr<CallbackDescriptor> = <@ { Record = " + q record + "; Field = " + q (pascal s.Name) + "; Signature = " + signature + " } @>"
                  ""
                  "[<FidelityExtern(\"gobject-2.0\", \"g_signal_connect_data\")>]"
                  $"let private {native} (instance: {instanceHandle}) (signal: string) (handler: {entry}) (data: {instanceHandle}) (destroy: {release}) (flags: int) : {handlerId.Clef} = NativeDefault.zeroed ()"
                  "let " + native + "Descriptor: Expr<FunctionDescriptor> = <@ " + functionRecord "g_signal_connect_data" [ "instance", pointer; "signal", pointer; "handler", pointer; "data", pointer; "destroy", pointer; "flags", flags.Ref ] handlerId.Ref + " @>"
                  ""
                  $"/// Requires a supplied nonnull instance and entry. Native callback absence and resource lifetime remain boundary admission obligations."
                  $"let {connectName} (instance: {instanceHandle}) (listener: {record}) : {handlerId.Clef} ="
                  $"    {native} instance {q s.Name} listener.{pascal s.Name} instance releaseEntry.Release 0"
                  ""
                  $"let private {adapter} (handler: {handlerType}) (_instance: {instance.Clef}) {arguments} (_data: {userData.Clef}) : {result.Clef} = handler {applied}"
                  $"let private {factory} (handler: {handlerType}) : {record} = NativeDefault.zeroed ()"
                  "let private " + factory + "Descriptor: Expr<ClosedCallbackDescriptor> = <@ { Binding = " + q (moduleName + "." + factory) + "; Adapter = " + q (moduleName + "." + adapter) + "; Record = " + q record + "; Field = " + q (pascal s.Name) + " } @>"
                  ""
                  $"/// Requires a known module handler without captures; no callback environment is allocated or retained."
                  $"let inline on{stem} (instance: {instanceHandle}) (handler: {handlerType}) : {handlerId.Clef} ="
                  $"    {moduleName}.{connectName} instance ({moduleName}.{factory} handler)"
                  "" ])
        let contents =
            [ $"module {nsPrefix}.Bridge.Signals"
              ""
              "// Generated by Farscape from installed GIR and native header declarations."
              "open BAREWire.Descriptors"
              $"open {nsPrefix}.Types"
              ""
              $"type private {releaseRecord} = {{ Release: {release} }}"
              "let private releaseHandlerDescriptor: Expr<CallbackDescriptor> = <@ { Record = " + q releaseRecord + "; Field = \"Release\"; Signature = " + releaseSignature + " } @>"
              ""
              "/// Closed static entries allocate no captured environment. This no-op releases no resource"
              "/// and does not implement captured callback retention or destruction."
              $"let private releaseHandler (_data: {releaseData.Clef}) (_closure: {releaseClosure.Clef}) : unit = ()"
              $"let private releaseEntry: {releaseRecord} = {{ Release = FnPtr.ofFunction releaseHandler }}"
              "" ] @ signalDecls
        Some (line contents)

    /// Writes only after the complete selected signal set passes contract validation.
    let generate nsPrefix signals declarations ctx outputDir : string list =
        match render nsPrefix signals declarations ctx with
        | None -> []
        | Some contents ->
            let dir = Path.Combine(outputDir, "Bridge")
            Directory.CreateDirectory dir |> ignore
            let path = Path.Combine(dir, "Signals.clef")
            File.WriteAllText(path, contents)
            [ path ]
