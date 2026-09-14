namespace Farscape.Core

open System.IO
open IntrospectionParser

/// Typed GObject signal projection: for each selected signal a listener record,
/// the `CallbackDescriptor` fixing its C entry, and a connect over
/// `g_signal_connect_data`. The Tier C form of docs/10's Representation
/// section: the handler is a closed module function, the instance handle is the
/// entry's user data, and the release slot carries a generated no-op until
/// Composer's trampolines land. Null exists only at the boundary: every input of
/// an entry is a scalar or an opaque handle the C side guarantees, and a signal
/// whose introspection data marks a parameter nullable is rejected.
module TypedSignalGenerator =
    let private q (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
    let private pascal (s: string) =
        s.Split([| '-'; '_' |]) |> Array.map (fun p -> if p = "" then "" else p.Substring(0, 1).ToUpperInvariant() + p.Substring(1)) |> String.concat ""
    let private camel (s: string) = let p = pascal s in if p = "" then p else p.Substring(0, 1).ToLowerInvariant() + p.Substring(1)
    let private callbackIdentifier (name: string) =
        name |> Seq.map (fun c -> if System.Char.IsLetterOrDigit c then string c else $"_{int c:X4}_") |> String.concat ""
    let private line xs = String.concat "\n" xs + "\n"
    let private handle = "CHandle<unit>"
    let private pointer = "Pointer 64"
    let private parameter (name, typ) = $"{{ Name = {q name}; Type = {typ}; PassBy = Value }}"
    let private functionRecord symbol parameters ret =
        "{ CName = " + q symbol + "; Parameters = [| " + (parameters |> List.map parameter |> String.concat "; ") + " |]; ReturnType = " + ret + "; CallingConvention = CDecl; OwnershipTransfer = Borrowed }"

    /// A crossing value's Clef carrier and its ABI representation.
    type private Carrier = { Clef: string; Ref: string }

    let private carrierOf (ctx: FidelityCodeGenerator.GenerationContext) (where: string) (cType: string) : Carrier =
        let resolved = FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums cType
        match resolved, FidelityCodeGenerator.abiReprOf ctx.DataModel cType resolved with
        | FidelityCodeGenerator.Scalar r, _ when r.Family = TypeMapper.Float ->
            failwith $"{where}: floating-point C type '{cType}' has no supported callback entry declaration, so the signal is not projected"
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
        let release = "FnPtr<" + handle + " -> " + handle + " -> unit>"
        let releaseRecord = callbackIdentifier nsPrefix + "_SignalReleaseEntry"
        let releaseSignature = functionRecord (nsPrefix + "::GClosureNotify") [ "data", pointer; "closure", pointer ] "Void"
        let signalDecls =
            signals |> List.collect (fun s ->
                let where = $"signal '{s.Namespace}.{s.Class}::{s.Name}'"
                for name, _, nullable in s.Parameters do
                    if nullable then
                        failwith $"{where}: parameter '{name}' is nullable; a null has no interior representation at a callback entry, so the signal is not projected"
                if s.ReturnNullable then
                    failwith $"{where}: result is nullable; a null has no interior representation at a callback entry, so the signal is not projected"
                let parameters = s.Parameters |> List.map (fun (name, cType, _) -> name, carrierOf ctx ($"{where}: parameter '{name}'") cType)
                let result = carrierOf ctx ($"{where}: result") s.ReturnCType
                let localName = pascal s.Class + pascal s.Name
                let stem =
                    if signals |> List.exists (fun other -> other.Namespace <> s.Namespace && pascal other.Class + pascal other.Name = localName) then
                        pascal s.Namespace + localName
                    else localName
                let record = stem + "Listener"
                let entry = "FnPtr<" + ((handle :: (parameters |> List.map (fun (_, c) -> c.Clef)) @ [ handle; result.Clef ]) |> String.concat " -> ") + ">"
                let entryName = s.ClassCType + "::" + s.Name
                let signature =
                    functionRecord entryName
                        (("instance", pointer) :: (parameters |> List.map (fun (n, c) -> n, c.Ref)) @ [ "user_data", pointer ])
                        result.Ref
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
                  $"let private {native} (instance: {handle}) (signal: string) (handler: {entry}) (data: {handle}) (destroy: {release}) (flags: int) : {handlerId.Clef} = NativeDefault.zeroed ()"
                  "let " + native + "Descriptor: Expr<FunctionDescriptor> = <@ " + functionRecord "g_signal_connect_data" [ "instance", pointer; "signal", pointer; "handler", pointer; "data", pointer; "destroy", pointer; "flags", flags.Ref ] handlerId.Ref + " @>"
                  ""
                  $"/// Connects `{s.Name}` on `instance`, whose handle is the entry's user data; the result is the handler id GObject assigns."
                  $"let {connectName} (instance: {handle}) (listener: {record}) : {handlerId.Clef} ="
                  $"    {native} instance {q s.Name} listener.{pascal s.Name} instance releaseEntry.Release 0"
                  ""
                  $"let private {adapter} (handler: {handlerType}) (_instance: {handle}) {arguments} (_data: {handle}) : {result.Clef} = handler {applied}"
                  $"let private {factory} (handler: {handlerType}) : {record} = NativeDefault.zeroed ()"
                  "let private " + factory + "Descriptor: Expr<ClosedCallbackDescriptor> = <@ { Binding = " + q (moduleName + "." + factory) + "; Adapter = " + q (moduleName + "." + adapter) + "; Record = " + q record + "; Field = " + q (pascal s.Name) + " } @>"
                  ""
                  $"/// Connects a closed Clef handler; native instance and user data stay inside this binding."
                  $"let inline on{stem} (instance: {handle}) (handler: {handlerType}) : {handlerId.Clef} ="
                  $"    {moduleName}.{connectName} instance ({moduleName}.{factory} handler)"
                  "" ])
        let contents =
            [ $"module {nsPrefix}.Bridge.Signals"
              ""
              "// Generated by Farscape from installed GObject introspection data and native header declarations."
              "open BAREWire.Descriptors"
              ""
              $"type private {releaseRecord} = {{ Release: {release} }}"
              "let private releaseHandlerDescriptor: Expr<CallbackDescriptor> = <@ { Record = " + q releaseRecord + "; Field = \"Release\"; Signature = " + releaseSignature + " } @>"
              ""
              "/// The release slot of every connection: a Tier C stand-in for the Tier A release thunk"
              "/// (docs/10, Representation); the handler's environment is module storage until then."
              $"let private releaseHandler (_data: {handle}) (_closure: {handle}) : unit = ()"
              $"let private releaseEntry: {releaseRecord} = {{ Release = FnPtr.ofFunction releaseHandler }}"
              "" ] @ signalDecls
        let dir = Path.Combine(outputDir, "Bridge")
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, "Signals.clef")
        File.WriteAllText(path, line contents)
        [ path ]
