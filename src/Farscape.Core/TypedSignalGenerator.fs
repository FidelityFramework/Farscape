namespace Farscape.Core

open System.IO
open IntrospectionParser

/// Typed GObject signal projection: for each selected signal a listener record,
/// the `CallbackDescriptor` fixing its C entry, and a connect over
/// `g_signal_connect_data`. The Tier C form of docs/10's Representation
/// section: the handler is a closed module function and the environment is an
/// explicit handle; the release slot carries a generated no-op until Composer's
/// trampolines land. Every pointer input of an entry, user data included, is an
/// opaque handle: the compiler's callback reader admits scalars and opaque handles.
module TypedSignalGenerator =
    let private q (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
    let private pascal (s: string) =
        s.Split([| '-'; '_' |]) |> Array.map (fun p -> if p = "" then "" else p.Substring(0, 1).ToUpperInvariant() + p.Substring(1)) |> String.concat ""
    let private camel (s: string) = let p = pascal s in if p = "" then p else p.Substring(0, 1).ToLowerInvariant() + p.Substring(1)
    let private snake (s: string) = s.Replace('-', '_')
    let private line xs = String.concat "\n" xs + "\n"
    let private handle = "CHandle<unit>"
    let private optional = "option<CHandle<unit>>"
    let private pointer = "Pointer 64"
    let private parameter (name, typ) = $"{{ Name = {q name}; Type = {typ}; PassBy = Value }}"
    let private functionRecord symbol parameters ret =
        "{ CName = " + q symbol + "; Parameters = [| " + (parameters |> List.map parameter |> String.concat "; ") + " |]; ReturnType = " + ret + "; CallingConvention = CDecl; OwnershipTransfer = Borrowed }"

    /// A crossing value's Clef carrier and its ABI representation.
    type private Carrier = { Clef: string; Ref: string }

    let private carrierOf (ctx: FidelityCodeGenerator.GenerationContext) (where: string) (cType: string) : Carrier =
        let resolved = FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums cType
        match resolved, FidelityCodeGenerator.abiReprOf ctx.DataModel cType resolved with
        | FidelityCodeGenerator.Scalar r, Some repr -> { Clef = TypeMapper.clefSpelling r.Family; Ref = DescriptorGenerator.typeRefSource repr }
        | FidelityCodeGenerator.CEnum _, Some repr -> { Clef = "int"; Ref = DescriptorGenerator.typeRefSource repr }
        | (FidelityCodeGenerator.DataPointer _ | FidelityCodeGenerator.OpaqueHandle _), Some repr ->
            { Clef = handle; Ref = DescriptorGenerator.typeRefSource repr }
        | FidelityCodeGenerator.FunctionPointer _, _ -> failwith $"{where}: a function-pointer type '{cType}' is not projected"
        | _ -> failwith $"{where}: the C type '{cType}' is not declared in the parsed headers"

    /// Writes `Bridge/Signals.clef` for the selected signals and returns its path.
    let generate (nsPrefix: string) (signals: SignalDecl list) (declarations: CppParser.Declaration list)
                 (ctx: FidelityCodeGenerator.GenerationContext) (outputDir: string) : string list =
        if signals.IsEmpty then [] else
        let connect =
            declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = "g_signal_connect_data" -> Some f | _ -> None)
            |> Option.defaultWith (fun () -> failwith "GObject signals require `g_signal_connect_data`; list glib-object.h among the pilot headers")
        let flags = carrierOf ctx "g_signal_connect_data" (connect.Parameters |> List.item 5 |> snd)
        let handlerId = carrierOf ctx "g_signal_connect_data" connect.ReturnType
        let release = "FnPtr<" + optional + " -> " + optional + " -> unit>"
        let signalDecls =
            signals |> List.collect (fun s ->
                let where = $"signal '{s.Class}::{s.Name}'"
                let parameters = s.Parameters |> List.map (fun (name, cType, _) -> name, carrierOf ctx where cType)
                let result = carrierOf ctx where s.ReturnCType
                let record = pascal s.Class + pascal s.Name + "Listener"
                let entry = "FnPtr<" + ((handle :: (parameters |> List.map (fun (_, c) -> c.Clef)) @ [ handle; result.Clef ]) |> String.concat " -> ") + ">"
                let entryName = s.SymbolPrefix + "_" + snake s.Name
                let signature =
                    functionRecord entryName
                        (("instance", pointer) :: (parameters |> List.map (fun (n, c) -> n, c.Ref)) @ [ "user_data", pointer ])
                        result.Ref
                let native = "connect" + pascal s.Class + pascal s.Name + "Native"
                let connectName = "connect" + pascal s.Class + pascal s.Name
                let doc = s.Documentation |> Option.defaultValue $"`{s.Name}` on {s.ClassCType}."
                [ $"/// {doc}"
                  $"type {record} = {{ {pascal s.Name}: {entry} }}"
                  ""
                  "let " + camel s.Class + pascal s.Name + "HandlerDescriptor: Expr<CallbackDescriptor> = <@ { Record = " + q record + "; Field = " + q (pascal s.Name) + "; Signature = " + signature + " } @>"
                  ""
                  "[<FidelityExtern(\"gobject-2.0\", \"g_signal_connect_data\")>]"
                  $"let private {native} (instance: {handle}) (signal: string) (handler: {entry}) (data: {optional}) (destroy: {release}) (flags: int) : {handlerId.Clef} = NativeDefault.zeroed ()"
                  "let " + native + "Descriptor: Expr<FunctionDescriptor> = <@ " + functionRecord "g_signal_connect_data" [ "instance", pointer; "signal", pointer; "handler", pointer; "data", pointer; "destroy", pointer; "flags", flags.Ref ] handlerId.Ref + " @>"
                  ""
                  $"/// Connects `{s.Name}` on `instance`; the result is the handler id GObject assigns."
                  $"let {connectName} (instance: {handle}) (listener: {record}) (data: {optional}) : {handlerId.Clef} ="
                  $"    {native} instance {q s.Name} listener.{pascal s.Name} data (FnPtr.ofFunction releaseHandler) 0"
                  "" ])
        let contents =
            [ $"module {nsPrefix}.Bridge.Signals"
              ""
              "// Generated by Farscape from installed GObject introspection data and native header declarations."
              "open BAREWire.Descriptors"
              ""
              "/// The release slot of every connection: a Tier C stand-in for the Tier A release thunk"
              "/// (docs/10, Representation); the handler's environment is module storage until then."
              $"let private releaseHandler (_data: {optional}) (_closure: {optional}) : unit = ()"
              "" ] @ signalDecls
        let dir = Path.Combine(outputDir, "Bridge")
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, "Signals.clef")
        File.WriteAllText(path, line contents)
        [ path ]
