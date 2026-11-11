[<Xunit.Collection("Native project selection")>]
module Farscape.Tests.IntrospectionTests

open System
open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// This fixture mutates process-wide dependency selection. Its collection must
// not overlap any other collection that runs generation with the default inputs.
[<CollectionDefinition("Native project selection", DisableParallelization = true)>]
type NativeProjectSelectionCollection() = class end

let private gir = """<?xml version="1.0"?>
<repository version="1.2" xmlns="http://www.gtk.org/introspection/core/1.0" xmlns:c="http://www.gtk.org/introspection/c/1.0" xmlns:glib="http://www.gtk.org/introspection/glib/1.0">
  <namespace name="WebKit2" version="4.1" c:identifier-prefixes="WebKit" c:symbol-prefixes="webkit">
    <record name="JavascriptResult" c:type="WebKitJavascriptResult" glib:type-name="WebKitJavascriptResult"/>
    <enumeration name="LoadEvent" c:type="WebKitLoadEvent"><member name="started" value="0" c:identifier="WEBKIT_LOAD_STARTED"/></enumeration>
    <class name="UserContentManager" c:symbol-prefix="user_content_manager" c:type="WebKitUserContentManager" parent="GObject.Object">
      <glib:signal name="script-message-received" when="last">
        <doc xml:space="preserve">This signal is emitted when JavaScript in a web view calls a registered handler.
More text.</doc>
        <return-value transfer-ownership="none"><type name="none" c:type="void"/></return-value>
        <parameters>
          <parameter name="value" transfer-ownership="none"><type name="JavascriptResult"/></parameter>
        </parameters>
      </glib:signal>
      <glib:signal name="load-changed" when="last">
        <return-value transfer-ownership="none"><type name="gboolean" c:type="gboolean"/></return-value>
        <parameters>
          <parameter name="load_event" transfer-ownership="none"><type name="LoadEvent"/></parameter>
          <parameter name="uri" transfer-ownership="none" nullable="1"><type name="utf8" c:type="const gchar*"/></parameter>
        </parameters>
      </glib:signal>
      <glib:signal name="load-accepted" when="last">
        <return-value transfer-ownership="none"><type name="gboolean" c:type="gboolean"/></return-value>
        <parameters>
          <parameter name="load_event"><type name="LoadEvent"/></parameter>
          <parameter name="count"><type name="guint" c:type="unsigned int"/></parameter>
        </parameters>
      </glib:signal>
      <glib:signal name="progress-changed" when="last">
        <return-value><type name="none" c:type="void"/></return-value>
        <parameters><parameter name="progress"><type name="gdouble" c:type="double"/></parameter></parameters>
      </glib:signal>
      <glib:signal name="measure" when="last">
        <return-value><type name="gfloat" c:type="float"/></return-value>
      </glib:signal>
      <glib:signal name="pick-child" when="last">
        <return-value nullable="1"><type name="JavascriptResult"/></return-value>
      </glib:signal>
      <glib:signal name="closed" when="last">
        <return-value><type name="none" c:type="void"/></return-value>
      </glib:signal>
    </class>
  </namespace>
</repository>"""

let private headerDecls =
    [ CppParser.Declaration.Struct (mkStruct "WebKitUserContentManager" [] None)
      CppParser.Declaration.Struct (mkStruct "WebKitJavascriptResult" [] None)
      CppParser.Declaration.Struct (mkStruct "GClosure" [] None)
      CppParser.Declaration.Typedef (mkTypedef "gpointer" "void *")
      CppParser.Declaration.Typedef (mkTypedef "gchar" "char")
      CppParser.Declaration.Typedef (mkTypedef "gboolean" "int")
      CppParser.Declaration.Typedef (mkTypedef "gulong" "unsigned long")
      CppParser.Declaration.Typedef (mkTypedef "GCallback" "void (*)(void)")
      CppParser.Declaration.Typedef (mkTypedef "GClosureNotify" "void (*)(gpointer, GClosure *)")
      CppParser.Declaration.Typedef (mkTypedef "WebKitLoadEvent" "enum WebKitLoadEvent")
      // clang measures these declarations unsigned 32-bit (the clang-parsed header below).
      CppParser.Declaration.Enum (mkMeasuredEnum "WebKitLoadEvent" [ { Name = "WEBKIT_LOAD_STARTED"; Value = 0L; Documentation = None } ] 32 false)
      CppParser.Declaration.Enum (mkMeasuredEnum "GConnectFlags" [ { Name = "G_CONNECT_DEFAULT"; Value = 0L; Documentation = None } ] 32 false)
      CppParser.Declaration.Typedef (mkTypedef "GConnectFlags" "enum GConnectFlags")
      CppParser.Declaration.Function
          (mkFunc "g_signal_connect_data" "gulong"
              [ "instance", "gpointer"; "detailed_signal", "const gchar *"; "c_handler", "GCallback"
                "data", "gpointer"; "destroy_data", "GClosureNotify"; "connect_flags", "GConnectFlags" ]) ]

let private signalContext model =
    { FidelityCodeGenerator.buildGenerationContext headerDecls model Map.empty with
        PhantomMarkers = Set.ofList ["WebKitUserContentManager"; "WebKitJavascriptResult"; "GClosure"] }

let private selectedSignals document selections =
    let ns = IntrospectionParser.parseDocument document |> function Ok ns -> ns | Error error -> failwith error
    let repository : IntrospectionParser.Repository = { Namespaces = Map.ofList [ns.Name, ns] }
    IntrospectionParser.select repository selections |> function Ok signals -> signals | Error error -> failwith error

[<Fact>]
let ``introspection data yields a signal's class, parameters and result with C spellings`` () =
    let repo = match IntrospectionParser.load [] with Ok r -> r | Error e -> failwith e
    let ns = match IntrospectionParser.parseDocument gir with Ok ns -> ns | Error e -> failwith e
    let repo = { repo with Namespaces = Map.add ns.Name ns repo.Namespaces }
    let signals =
        match IntrospectionParser.select repo [ "UserContentManager::script-message-received"; "UserContentManager::load-changed" ] with
        | Ok s -> s
        | Error e -> failwith e
    let received = signals |> List.find (fun s -> s.Name = "script-message-received")
    Assert.Equal("WebKitUserContentManager", received.ClassCType)
    Assert.Equal("webkit_user_content_manager", received.SymbolPrefix)
    Assert.Equal<(string * string * bool) list>([ "value", "WebKitJavascriptResult*", false ], received.Parameters)
    Assert.Equal("void", received.ReturnCType)
    Assert.False(received.ReturnNullable)
    Assert.Equal(Some "This signal is emitted when JavaScript in a web view calls a registered handler.", received.Documentation)
    let changed = signals |> List.find (fun s -> s.Name = "load-changed")
    Assert.Equal<(string * string * bool) list>([ "load_event", "WebKitLoadEvent", false; "uri", "const gchar*", true ], changed.Parameters)
    Assert.Equal("gboolean", changed.ReturnCType)
    match IntrospectionParser.select repo [ "UserContentManager::missing" ] with
    | Error message -> Assert.Contains("not declared", message)
    | Ok _ -> failwith "an unknown signal must be rejected"

[<Fact>]
let ``typed signal bridge declares the entry contract and a connect over g_signal_connect_data`` () =
    let ns = match IntrospectionParser.parseDocument gir with Ok ns -> ns | Error e -> failwith e
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ ns.Name, ns ] }
    let select names = match IntrospectionParser.select repo names with Ok s -> s | Error e -> failwith e
    let ctx = signalContext Types.LP64
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        let files = TypedSignalGenerator.generate "Fidelity.WebKit.Native" (select [ "UserContentManager::script-message-received"; "UserContentManager::load-accepted" ]) headerDecls ctx dir
        let text = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Equal<string list>([ Path.Combine(dir, "Bridge/Signals.clef") ], files)
        Assert.Contains("module Fidelity.WebKit.Native.Bridge.Signals", text)
        Assert.Contains("type UserContentManagerScriptMessageReceivedListener = { ScriptMessageReceived: FnPtr<option<CHandle<WebKitUserContentManager>> -> option<CHandle<WebKitJavascriptResult>> -> option<CHandle<unit>> -> unit> }", text)
        Assert.Contains("Record = \"UserContentManagerScriptMessageReceivedListener\"; Field = \"ScriptMessageReceived\"; Signature = { CName = \"WebKitUserContentManager::script-message-received\"", text)
        Assert.Contains("{ Name = \"value\"; Type = Pointer 64; PassBy = Value }; { Name = \"user_data\"; Type = Pointer 64; PassBy = Value } |]; ReturnType = Void", text)
        Assert.Contains("[<FidelityExtern(\"gobject-2.0\", \"g_signal_connect_data\")>]", text)
        Assert.Contains("let connectUserContentManagerScriptMessageReceived (instance: CHandle<WebKitUserContentManager>) (listener: UserContentManagerScriptMessageReceivedListener) : int =", text)
        Assert.Contains("connectUserContentManagerScriptMessageReceivedNative instance \"script-message-received\" listener.ScriptMessageReceived instance releaseEntry.Release 0", text)
        Assert.Contains("type private Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry = { Release: FnPtr<option<CHandle<unit>> -> option<CHandle<GClosure>> -> unit> }", text)
        Assert.Contains("Record = \"Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry\"; Field = \"Release\"; Signature = { CName = \"Fidelity.WebKit.Native::GClosureNotify\"; Parameters = [| { Name = \"data\"; Type = Pointer 64; PassBy = Value }; { Name = \"closure\"; Type = Pointer 64; PassBy = Value } |]; ReturnType = Void", text)
        Assert.Contains("let private releaseEntry: Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry = { Release = FnPtr.ofFunction releaseHandler }", text)
        Assert.Contains("type UserContentManagerLoadAcceptedListener = { LoadAccepted: FnPtr<option<CHandle<WebKitUserContentManager>> -> int -> int -> option<CHandle<unit>> -> int> }", text)
        Assert.Contains("{ Name = \"load_event\"; Type = Integer (Unsigned, 32); PassBy = Value }", text)
        Assert.Contains("{ Name = \"count\"; Type = Integer (Unsigned, 32); PassBy = Value }", text)
        Assert.Contains("ReturnType = Integer (Signed, 32)", text)
        Assert.Contains("ReturnType = Integer (Unsigned, 64)", text)
        Assert.Contains("OwnershipTransfer = Undeclared", text)
        Assert.DoesNotContain("OwnershipTransfer = Borrowed", text)
        Assert.DoesNotContain("nativeint", text)
        Assert.Contains("open Fidelity.WebKit.Native.Types", text)
        Assert.Contains("no callback environment is allocated or retained", text)
        Assert.DoesNotContain("module storage until", text)
        TypedSignalGenerator.generate "Fidelity.WebKit.Native" (select [ "UserContentManager::load-changed"; "UserContentManager::pick-child" ]) headerDecls ctx dir |> ignore
        let nullableText = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Contains("FnPtr<option<CHandle<WebKitUserContentManager>> -> int -> option<CHandle<int>> -> option<CHandle<unit>> -> int>", nullableText)
        Assert.Contains("FnPtr<option<CHandle<WebKitUserContentManager>> -> option<CHandle<unit>> -> option<CHandle<WebKitJavascriptResult>>>", nullableText)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``closed signal factories preserve payload arity and result while omitting native context`` () =
    let ns = IntrospectionParser.parseDocument gir |> function Ok ns -> ns | Error e -> failwith e
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ ns.Name, ns ] }
    let signals =
        IntrospectionParser.select repo [ "UserContentManager::closed"; "UserContentManager::script-message-received"; "UserContentManager::load-accepted" ]
        |> function Ok s -> s | Error e -> failwith e
    let ctx = signalContext Types.LP64
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        TypedSignalGenerator.generate "Fidelity.WebKit.Native" signals headerDecls ctx dir |> ignore
        let text = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Contains("let inline onUserContentManagerClosed (instance: CHandle<WebKitUserContentManager>) (handler: (unit -> unit)) : int =", text)
        Assert.Contains("let private makeUserContentManagerClosedEntry (handler: (unit -> unit)) : UserContentManagerClosedListener = NativeDefault.zeroed ()", text)
        Assert.Contains("let private adaptUserContentManagerClosed (handler: (unit -> unit)) (_instance: option<CHandle<WebKitUserContentManager>>)  (_data: option<CHandle<unit>>) : unit = handler ()", text)
        Assert.Contains("let inline onUserContentManagerScriptMessageReceived (instance: CHandle<WebKitUserContentManager>) (handler: (option<CHandle<WebKitJavascriptResult>> -> unit)) : int =", text)
        Assert.Contains("let private adaptUserContentManagerScriptMessageReceived (handler: (option<CHandle<WebKitJavascriptResult>> -> unit)) (_instance: option<CHandle<WebKitUserContentManager>>) (value0: option<CHandle<WebKitJavascriptResult>>) (_data: option<CHandle<unit>>) : unit = handler value0", text)
        Assert.Contains("let inline onUserContentManagerLoadAccepted (instance: CHandle<WebKitUserContentManager>) (handler: (int -> int -> int)) : int =", text)
        Assert.Contains("let private adaptUserContentManagerLoadAccepted (handler: (int -> int -> int)) (_instance: option<CHandle<WebKitUserContentManager>>) (value0: int) (value1: int) (_data: option<CHandle<unit>>) : int = handler value0 value1", text)
        for stem, field in [ "UserContentManagerClosed", "Closed"; "UserContentManagerScriptMessageReceived", "ScriptMessageReceived"; "UserContentManagerLoadAccepted", "LoadAccepted" ] do
            Assert.Contains($"Fidelity.WebKit.Native.Bridge.Signals.connect{stem} instance (Fidelity.WebKit.Native.Bridge.Signals.make{stem}Entry handler)", text)
            Assert.Contains($"Expr<ClosedCallbackDescriptor> = <@ {{ Binding = \"Fidelity.WebKit.Native.Bridge.Signals.make{stem}Entry\"; Adapter = \"Fidelity.WebKit.Native.Bridge.Signals.adapt{stem}\"; Record = \"{stem}Listener\"; Field = \"{field}\" }} @>", text)
        Assert.DoesNotContain("= handler _instance", text)
        Assert.DoesNotContain("= handler _data", text)
        Assert.DoesNotContain("FnPtr.ofFunction handler", text)
        TypedSignalGenerator.generate "Fidelity.WebKit_Native" signals headerDecls ctx dir |> ignore
        let underscored = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Contains("type private Fidelity_002E_WebKit_005F_Native_SignalReleaseEntry", underscored)
        Assert.DoesNotContain("type private Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry", underscored)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Theory>]
[<InlineData("progress-changed", "parameter 'progress'", "floating-point")>]
[<InlineData("measure", "result", "floating-point")>]
let ``unsupported signal entry types identify the parameter or result`` signalName position reason =
    let ns = match IntrospectionParser.parseDocument gir with Ok ns -> ns | Error e -> failwith e
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ ns.Name, ns ] }
    let signals = IntrospectionParser.select repo [ $"UserContentManager::{signalName}" ] |> function Ok s -> s | Error e -> failwith e
    let ctx = signalContext Types.LP64
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        let error = Assert.ThrowsAny<Exception>(fun () -> TypedSignalGenerator.generate "Fidelity.WebKit.Native" signals headerDecls ctx dir |> ignore)
        Assert.Contains($"WebKit2.UserContentManager::{signalName}", error.Message)
        Assert.Contains(position, error.Message)
        Assert.Contains(reason, error.Message)
        Assert.False(File.Exists(Path.Combine(dir, "Bridge/Signals.clef")))
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Theory>]
[<InlineData("nullable")>]
[<InlineData("allow-none")>]
let ``nullable signal results retain GIR nullability after type resolution`` attribute =
    let document = gir.Replace("<return-value nullable=\"1\">", $"<return-value {attribute}=\"1\">")
    let ns = IntrospectionParser.parseDocument document |> function Ok ns -> ns | Error e -> failwith e
    let raw = ns.Signals |> List.find (fun s -> s.Name = "pick-child")
    Assert.True(raw.ReturnNullable)
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ ns.Name, ns ] }
    let signals = IntrospectionParser.select repo [ "UserContentManager::pick-child" ] |> function Ok s -> s | Error e -> failwith e
    Assert.True(signals.Head.ReturnNullable)
    Assert.Equal("WebKitJavascriptResult*", signals.Head.ReturnCType)

[<Fact>]
let ``signal selectors require a namespace when class and signal names are ambiguous`` () =
    let first = IntrospectionParser.parseDocument gir |> function Ok ns -> ns | Error e -> failwith e
    let second = IntrospectionParser.parseDocument (gir.Replace("WebKit2", "Other")) |> function Ok ns -> ns | Error e -> failwith e
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ first.Name, first; second.Name, second ] }
    match IntrospectionParser.select repo [ "UserContentManager::load-accepted" ] with
    | Ok _ -> failwith "an ambiguous signal must be rejected"
    | Error error ->
        Assert.Contains("ambiguous", error)
        Assert.Contains("WebKit2.UserContentManager::load-accepted", error)
        Assert.Contains("Other.UserContentManager::load-accepted", error)
    let selected =
        IntrospectionParser.select repo [ "WebKit2.UserContentManager::load-accepted"; "Other.UserContentManager::load-accepted" ]
        |> function Ok s -> s | Error e -> failwith e
    Assert.Equal<string list>([ "WebKit2"; "Other" ], selected |> List.map (fun s -> s.Namespace))
    match IntrospectionParser.select repo [ "Missing.UserContentManager::load-accepted" ] with
    | Error error -> Assert.Contains("not declared", error)
    | Ok _ -> failwith "a namespace qualifier must not fall back to another namespace"
    let ctx = signalContext Types.LP64
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        TypedSignalGenerator.generate "Fidelity.WebKit.Native" selected headerDecls ctx dir |> ignore
        let text = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Contains("type WebKit2UserContentManagerLoadAcceptedListener", text)
        Assert.Contains("type OtherUserContentManagerLoadAcceptedListener", text)
        Assert.Contains("let connectWebKit2UserContentManagerLoadAccepted ", text)
        Assert.Contains("let connectOtherUserContentManagerLoadAccepted ", text)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``pilot keys for introspection and signals round-trip`` () =
    let toml = """
[library]
name = "webkit2gtk-4.1"
headers = ["webkit2.h"]
introspection = ["/usr/share/gir-1.0/WebKit2-4.1.gir"]
[output]
mode = "fidelity"
directory = "out"
[[namespace]]
name = "Fidelity.WebKit.Native"
library = "webkit2gtk-4.1"
description = "test"
signals = ["UserContentManager::script-message-received"]
"""
    let project = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail toml) |> function Ok p -> p | Error e -> failwith e
    Assert.Equal<string list>([ "/usr/share/gir-1.0/WebKit2-4.1.gir" ], project.Library.Introspection)
    Assert.Equal<string list>([ "UserContentManager::script-message-received" ], project.Namespaces.Head.Signals)
    let text = PilotSerializer.toTomlString project
    Assert.Contains("introspection = [", text)
    Assert.Contains("signals = [", text)

[<Theory>]
[<InlineData("nullable")>]
[<InlineData("allow-none")>]
let ``explicit GIR nonnull evidence narrows only its native pointer value`` attribute =
    let document = gir.Replace("<parameter name=\"value\" transfer-ownership=\"none\">", $"<parameter name=\"value\" transfer-ownership=\"none\" {attribute}=\"0\">")
    let signals = selectedSignals document ["UserContentManager::script-message-received"]
    Assert.Contains("value", signals.Head.NonnullParameters)
    Assert.False(signals.Head.ReturnNonnull)
    let source = TypedSignalGenerator.render "Fidelity.WebKit.Native" signals headerDecls (signalContext Types.LP64) |> Option.get
    Assert.Contains("FnPtr<option<CHandle<WebKitUserContentManager>> -> CHandle<WebKitJavascriptResult> -> option<CHandle<unit>> -> unit>", source)
    let unknown = selectedSignals gir ["UserContentManager::script-message-received"]
    Assert.Empty(unknown.Head.NonnullParameters)
    let unknownSource = TypedSignalGenerator.render "Fidelity.WebKit.Native" unknown headerDecls (signalContext Types.LP64) |> Option.get
    Assert.Contains("FnPtr<option<CHandle<WebKitUserContentManager>> -> option<CHandle<WebKitJavascriptResult>> -> option<CHandle<unit>> -> unit>", unknownSource)

[<Fact>]
let ``native release callback typedef evidence survives into the generated entry`` () =
    let declarations = headerDecls |> List.map (function
        | CppParser.Declaration.Typedef typedef when typedef.Name = "GClosureNotify" ->
            CppParser.Declaration.Typedef { typedef with UnderlyingType = "void (*)(gpointer _Nonnull, GClosure * _Nonnull)" }
        | declaration -> declaration)
    let context = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with PhantomMarkers = (signalContext Types.LP64).PhantomMarkers }
    let signals = selectedSignals gir ["UserContentManager::closed"]
    let source = TypedSignalGenerator.render "Fidelity.WebKit.Native" signals declarations context |> Option.get
    Assert.Contains("Release: FnPtr<CHandle<unit> -> CHandle<GClosure> -> unit>", source)
    Assert.Contains("let private releaseHandler (_data: CHandle<unit>) (_closure: CHandle<GClosure>) : unit = ()", source)
    Assert.Contains("no callback environment is allocated or retained", source)
    Assert.DoesNotContain("module storage until", source)

[<Fact>]
let ``GIR cannot silently erase a missing native callback parameter type`` () =
    let document = gir.Replace("<type name=\"JavascriptResult\"/>", "")
    match IntrospectionParser.parseDocument document with
    | Ok _ -> failwith "An incomplete GIR parameter must not shorten the emitted native callback signature"
    | Error error ->
        Assert.Contains("parameter 'value' has no declared type", error)
        Assert.Contains("complete native callback arity", error)

[<Fact>]
let ``contradictory GIR nullability evidence is rejected`` () =
    let document = gir.Replace("nullable=\"1\"", "nullable=\"1\" allow-none=\"0\"")
    match IntrospectionParser.parseDocument document with
    | Ok _ -> failwith "Contradictory GIR evidence must not select a convenient representation"
    | Error error -> Assert.Contains("contradictory GIR nullability evidence", error)

[<Fact>]
let ``duplicate GIR participant names cannot transfer nonnull evidence to another input`` () =
    let parameter = "<parameter name=\"value\" transfer-ownership=\"none\"><type name=\"JavascriptResult\"/></parameter>"
    let document = gir.Replace(parameter, parameter + "<parameter name=\"value\" nullable=\"0\"><type name=\"JavascriptResult\"/></parameter>")
    match IntrospectionParser.parseDocument document with
    | Ok _ -> failwith "One parameter's nonnull evidence must not narrow a different input with the same name"
    | Error error -> Assert.Contains("duplicate callback parameter names", error)
    let signal = (selectedSignals gir ["UserContentManager::script-message-received"]).Head
    let duplicate = { signal with Parameters = signal.Parameters @ signal.Parameters; NonnullParameters = Set.singleton "value" }
    let error = Assert.ThrowsAny<Exception>(fun () ->
        TypedSignalGenerator.render "Fidelity.WebKit.Native" [duplicate] headerDecls (signalContext Types.LP64) |> ignore)
    Assert.Contains("present and unique for nullability admission", error.Message)

[<Fact>]
let ``signal descriptor widths follow each selected ABI without changing source integer kinds`` () =
    let signals = selectedSignals gir ["UserContentManager::load-accepted"]
    for model in [Types.LP64; Types.LLP64; Types.ILP32; Types.IP16] do
        let source = TypedSignalGenerator.render "Fidelity.WebKit.Native" signals headerDecls (signalContext model) |> Option.get
        let pointerBits = TypeMapper.pointerWidth model
        let intBits = TypeMapper.intWidth model
        let longBits = TypeMapper.longWidth model
        Assert.Contains($"Type = Pointer {pointerBits}", source)
        Assert.Contains($"ReturnType = Integer (Unsigned, {longBits})", source)
        Assert.Contains($"Type = Integer (Unsigned, {intBits})", source)
        Assert.Contains("(flags: int)", source)
        if pointerBits <> 64 then Assert.DoesNotContain("Pointer 64", source)
        Assert.DoesNotContain("nativeint", source)
        Assert.DoesNotContain("NativePtr", source)

[<Fact>]
let ``required signal carriers come from headers and include the class payload and release closure`` () =
    let signals = selectedSignals gir ["UserContentManager::script-message-received"]
    let context = FidelityCodeGenerator.buildGenerationContext headerDecls Types.LP64 Map.empty
    let required = TypedSignalGenerator.requiredTypes signals headerDecls context
    Assert.Equal<Set<string>>(Set.ofList ["WebKitUserContentManager"; "WebKitJavascriptResult"; "GClosure"], required)
    let unknownDocument = gir.Replace("<type name=\"JavascriptResult\"/>", "<type name=\"Unknown\" c:type=\"UnknownNative *\"/>")
    let unknown = selectedSignals unknownDocument ["UserContentManager::script-message-received"]
    let error = Assert.ThrowsAny<Exception>(fun () -> TypedSignalGenerator.requiredTypes unknown headerDecls context |> ignore)
    Assert.Contains("UnknownNative", error.Message)
    Assert.Contains("GIR alone cannot supply its carrier", error.Message)

[<Fact>]
let ``failed signal regeneration preserves existing output before header shape validation`` () =
    let signals = selectedSignals gir ["UserContentManager::closed"]
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signal-sentinel-" + Guid.NewGuid().ToString("N"))
    let bridge = Path.Combine(dir, "Bridge")
    let output = Path.Combine(bridge, "Signals.clef")
    try
        Directory.CreateDirectory bridge |> ignore
        File.WriteAllText(output, "// Existing signal consumer source\n")
        let declarations = headerDecls |> List.map (function
            | CppParser.Declaration.Function f when f.Name = "g_signal_connect_data" -> CppParser.Declaration.Function { f with Parameters = f.Parameters |> List.take 5 }
            | declaration -> declaration)
        let context = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with PhantomMarkers = (signalContext Types.LP64).PhantomMarkers }
        let error = Assert.ThrowsAny<Exception>(fun () -> TypedSignalGenerator.generate "Fidelity.WebKit.Native" signals declarations context dir |> ignore)
        Assert.Contains("complete six-parameter native declaration", error.Message)
        Assert.Equal("// Existing signal consumer source\n", File.ReadAllText output)
        Assert.Equal<string array>([|output|], Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Theory>]
[<InlineData("destroy", "destroy_data")>]
[<InlineData("handler", "handler")>]
[<InlineData("text", "detailed_signal")>]
[<InlineData("result", "result")>]
[<InlineData("legacy", "experimental_native_pointer_surface")>]
let ``unsupported signal header contracts fail before creating output`` defect fragment =
    let declarations = headerDecls |> List.map (function
        | CppParser.Declaration.Typedef typedef when defect = "destroy" && typedef.Name = "GClosureNotify" ->
            CppParser.Declaration.Typedef { typedef with UnderlyingType = "int (*)(gpointer)" }
        | CppParser.Declaration.Function f when f.Name = "g_signal_connect_data" ->
            let parameters = f.Parameters |> List.mapi (fun index (name, typ) ->
                if (defect = "handler" && index = 2) || (defect = "text" && index = 1) then name, "void *" else name, typ)
            CppParser.Declaration.Function { f with Parameters = parameters; ReturnType = if defect = "result" then "void *" else f.ReturnType }
        | declaration -> declaration)
    let context =
        { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
            PhantomMarkers = (signalContext Types.LP64).PhantomMarkers; NativePointerSurface = defect = "legacy" }
    let signals = selectedSignals gir ["UserContentManager::closed"]
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-rejected-" + Guid.NewGuid().ToString("N"))
    let error = Assert.ThrowsAny<Exception>(fun () -> TypedSignalGenerator.generate "Fidelity.WebKit.Native" signals declarations context dir |> ignore)
    Assert.Contains(fragment, error.Message)
    Assert.False(Directory.Exists dir)

let private projectHeader = """
typedef struct WebKitUserContentManager WebKitUserContentManager;
typedef struct WebKitJavascriptResult WebKitJavascriptResult;
typedef struct GClosure GClosure;
typedef void *gpointer;
typedef char gchar;
typedef int gboolean;
typedef unsigned long gulong;
typedef void (*GCallback)(void);
typedef void (*GClosureNotify)(gpointer, GClosure *);
typedef enum { WEBKIT_LOAD_STARTED = 0 } WebKitLoadEvent;
typedef enum { G_CONNECT_DEFAULT = 0 } GConnectFlags;
gulong g_signal_connect_data(gpointer instance, const gchar *detailed_signal, GCallback c_handler,
    gpointer data, GClosureNotify destroy_data, GConnectFlags connect_flags);
"""

let private signalProject (directory: string) (header: string) =
    let headerPath = Path.Combine(directory, "signals.h")
    let girPath = Path.Combine(directory, "signals.gir")
    let output = Path.Combine(directory, "Bindings")
    File.WriteAllText(headerPath, header)
    File.WriteAllText(girPath, gir)
    let project : PilotTypes.PilotProject =
        { Library = { Name = "gobject-2.0"; Headers = [headerPath]; XmlProtocols = []; Introspection = [girPath]
                      IncludePaths = [directory]; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = []; Artifact = None }
          Output = { Mode = "fidelity"; Directory = output }
          Namespaces = [ { Name = "Fidelity.WebKit.Native.Core"; Description = "Signal-only generation"; Library = "gobject-2.0"
                           Prefixes = []; Functions = []; XmlInterfaces = []; Signals = ["UserContentManager::script-message-received"] } ]
          ErrorConventions = None; Callbacks = None
          // This synthetic profile supplies both entries; it asserts neither nullable slot is used.
          Nonnull = Some { Parameters = Map.ofList ["g_signal_connect_data", [2; 4]]; Returns = Set.empty }
          ProtocolConfig = None; Layer3 = None; CppConfig = None
          Options = Some { AbiCriticalStructs = []; GenerateDescriptors = true; NativePointerSurface = false; CHeaderMode = true
                           Bindings = []; DescriptorOnlyDependencies = true; ValueStructs = []; TypedProtocol = false; LinkLibraries = false; MappedReturns = []; FlexibleArrays = [] } }
    let path = Path.Combine(directory, "signals.pilot.toml")
    PilotSerializer.saveToFile path project |> function Ok _ -> () | Error error -> failwith error
    path, output

[<Theory>]
[<InlineData("plain")>]
[<InlineData("callbacks")>]
[<InlineData("typed-protocol")>]
let ``withdrawn signal project callback hypothesis preserves existing output`` mode =
  let directory = Path.Combine(Path.GetTempPath(), "farscape-signal-project-unknown-" + Guid.NewGuid().ToString("N"))
  try
    Directory.CreateDirectory directory |> ignore
    let pilot, output = signalProject directory projectHeader
    let project =
      PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (File.ReadAllText pilot))
      |> function Ok project -> project | Error error -> failwith error
    let withdrawn =
      { project with
          Nonnull = None
          Callbacks =
            if mode = "callbacks" then Some { Registrations = [{ Function = "g_signal_connect_data"; CallbackParam = "c_handler"; DataParam = Some "data" }]; ListenerStructs = [] }
            else project.Callbacks
          Options = project.Options |> Option.map(fun options -> { options with TypedProtocol = mode = "typed-protocol" }) }
    PilotSerializer.saveToFile pilot withdrawn |> function Ok () -> () | Error error -> failwith error
    Directory.CreateDirectory output |> ignore
    let sentinel = Path.Combine(output, "preserve-existing.clef")
    File.WriteAllText(sentinel, "// Retained binding\n")
    let result = BindingGenerator.generateFromProject pilot false (mode = "callbacks") Types.LP64
    match result with
    | Ok _ -> failwith "Omitted callback nonnull contract must refuse before output"
    | Error error ->
      Assert.Contains("g_signal_connect_data parameter 2", error)
      Assert.Contains("requires explicit nonnull evidence", error)
    Assert.Equal("// Retained binding\n", File.ReadAllText sentinel)
    Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories)) |> ignore
  finally
    if Directory.Exists directory then Directory.Delete(directory, true)

[<Fact>]
let ``signal-only project emits each known nominal carrier in shared Types`` () =
    let directory = Path.Combine(Path.GetTempPath(), "farscape-signal-project-" + Guid.NewGuid().ToString("N"))
    try
        Directory.CreateDirectory directory |> ignore
        let pilot, output = signalProject directory projectHeader
        let result = BindingGenerator.generateFromProject pilot false false Types.LP64 |> function Ok result -> result | Error error -> failwith error
        let types = File.ReadAllText(Path.Combine(output, "Types.clef"))
        let signals = File.ReadAllText(Path.Combine(output, "Bridge", "Signals.clef"))
        for name in ["WebKitUserContentManager"; "WebKitJavascriptResult"; "GClosure"] do
            Assert.Contains($"type {name} = private | Opaque_{name}", types)
        Assert.Contains("module Fidelity.WebKit.Native.Core.Types", types)
        Assert.Contains("open Fidelity.WebKit.Native.Core.Types", signals)
        Assert.Contains("option<CHandle<WebKitJavascriptResult>>", signals)
        Assert.Contains(Path.Combine(output, "Bridge", "Signals.clef"), result.OutputFiles)
    finally
        if Directory.Exists directory then Directory.Delete(directory, true)

[<Fact>]
let ``project signal preflight leaves every existing output intact on unsupported native callback shape`` () =
    let directory = Path.Combine(Path.GetTempPath(), "farscape-signal-project-rejected-" + Guid.NewGuid().ToString("N"))
    try
        Directory.CreateDirectory directory |> ignore
        let pilot, output = signalProject directory (projectHeader.Replace("GCallback c_handler", "gpointer c_handler"))
        Directory.CreateDirectory output |> ignore
        let sentinel = Path.Combine(output, "preserve-existing.clef")
        File.WriteAllText(sentinel, "// Existing desktop bindings\n")
        match BindingGenerator.generateFromProject pilot false false Types.LP64 with
        | Ok _ -> failwith "An unsupported native signal callback contract must not regenerate bindings"
        | Error error -> Assert.Contains("declared generic void callback slot", error)
        Assert.Equal("// Existing desktop bindings\n", File.ReadAllText sentinel)
        Assert.Equal<string array>([|sentinel|], Directory.GetFiles(output, "*", SearchOption.AllDirectories))
    finally
        if Directory.Exists directory then Directory.Delete(directory, true)

[<Theory>]
[<InlineData("absent", false)>]
[<InlineData("selected", false)>]
[<InlineData("missing", false)>]
[<InlineData("selected", true)>]
[<InlineData("missing", true)>]
[<InlineData("uncommitted", false)>]
[<InlineData("malformed", false)>]
[<InlineData("malformed", true)>]
[<InlineData("invalid-description", false)>]
[<InlineData("sibling", false)>]
let ``descriptor-only project preserves explicit platform selection before writing`` selection callbacks =
  let checkout = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
  let directory = Path.Combine(checkout, "artifacts", "ffi-contract-20261013", "platform-selection-" + Guid.NewGuid().ToString("N"))
  let previous = Environment.GetEnvironmentVariable "FIDELITY_PLATFORM_PROJECT"
  try
    Directory.CreateDirectory directory |> ignore
    let pilot, output = signalProject directory projectHeader
    if selection = "sibling" then
      let project = PilotSerializer.loadFromFile pilot |> function Ok project -> project | Error error -> failwith error
      let ordinary = { project with Options = project.Options |> Option.map(fun options -> { options with DescriptorOnlyDependencies = false }) }
      PilotSerializer.saveToFile pilot ordinary |> function Ok () -> () | Error error -> failwith error
    if callbacks then
      let project = PilotSerializer.loadFromFile pilot |> function Ok project -> project | Error error -> failwith error
      let callbackProject =
        { project with Callbacks = Some { Registrations = [{ Function = "g_signal_connect_data"; CallbackParam = "c_handler"; DataParam = Some "data" }]; ListenerStructs = [] } }
      PilotSerializer.saveToFile pilot callbackProject |> function Ok () -> () | Error error -> failwith error
    let selected = Path.Combine(directory, "Selected.Platform.fidproj")
    if selection = "selected" then
      File.WriteAllText(selected, "[package]\nname = \"Selected.Platform\"\n[platform]\ndescription = \"Selected.Target.description\"\n[build]\nsources = []\n")
    elif selection = "uncommitted" then
      File.WriteAllText(selected, "[package]\nname = \"Uncommitted.Platform\"\n[build]\nsources = []\n")
    elif selection = "malformed" then File.WriteAllText(selected, "[platform\n")
    elif selection = "invalid-description" then File.WriteAllText(selected, "[platform]\ndescription = 64\n")
    Environment.SetEnvironmentVariable("FIDELITY_PLATFORM_PROJECT", if selection = "absent" then null elif selection = "sibling" then " " else selected)
    Directory.CreateDirectory output |> ignore
    let sentinel = Path.Combine(output, "preserve-existing.clef")
    File.WriteAllText(sentinel, "// Retained binding\n")
    match BindingGenerator.generateFromProject pilot false callbacks Types.LP64 with
    | Error error when selection = "missing" || selection = "malformed" || selection = "invalid-description" ->
      let reason =
        match selection with
        | "missing" -> $"FIDELITY_PLATFORM_PROJECT names a missing dependency: {selected}"
        | "malformed" -> $"FIDELITY_PLATFORM_PROJECT has invalid TOML: {selected}"
        | _ -> $"FIDELITY_PLATFORM_PROJECT platform.description must be a nonempty string: {selected}"
      Assert.Contains(reason, error)
      Assert.Equal("// Retained binding\n", File.ReadAllText sentinel)
      Assert.Equal<string array>([|sentinel|], Directory.GetFiles(output, "*", SearchOption.AllDirectories))
    | Error error -> failwith error
    | Ok _ when selection = "missing" || selection = "malformed" || selection = "invalid-description" -> failwith "An invalid explicit platform selection must refuse before output"
    | Ok result ->
      let manifests = result.OutputFiles |> List.filter(fun path -> path.EndsWith ".fidproj")
      Assert.NotEmpty manifests
      if callbacks then Assert.Equal(2, manifests.Length)
      for path in manifests do
        let contents = File.ReadAllText path
        let document = Fidelity.Data.TOML.Toml.parseOrFail contents
        Assert.Equal((if selection = "selected" then Some "Selected.Target.description" else None), Fidelity.Data.TOML.Toml.getString "platform.description" document)
        Assert.DoesNotContain("word_size", contents)
        if selection = "absent" then Assert.DoesNotContain("Fidelity.Platform", contents)
        elif selection = "sibling" then
          Assert.Contains("\"Fidelity.Platform\" =", contents)
          Assert.Contains("Fidelity.Platform.fidproj", contents)
        else
          let relative = Path.GetRelativePath(Path.GetDirectoryName path, selected).Replace('\\', '/')
          Assert.Contains($"\"Fidelity.Platform\" = {{ path = \"{relative}\" }}", contents)
          let dependencies = Fidelity.Data.TOML.Toml.getTable "dependencies" document |> Option.get
          match Map.tryFind "Fidelity.Platform" dependencies with
          | Some (Fidelity.Data.TOML.TomlValue.InlineTable dependency) ->
            Assert.Equal(Some (Fidelity.Data.TOML.TomlValue.String relative), Map.tryFind "path" dependency)
          | value -> failwithf "Dotted dependency key was not preserved as one package: %A" value
        if path.Contains ".Bridge.fidproj" then
          Assert.Contains("\"Fidelity.WebKit.Native.Core\" = { path =", contents)
      let native = manifests |> List.find(fun path -> not (path.Contains ".Bridge.fidproj")) |> File.ReadAllText
      Assert.Contains((if selection = "sibling" then "BAREWire.fidproj" else "BAREWire.BindingMetadata.fidproj"), native)
  finally
    Environment.SetEnvironmentVariable("FIDELITY_PLATFORM_PROJECT", previous)
    if Directory.Exists directory then Directory.Delete(directory, true)

[<Theory>]
[<InlineData("positive")>]
[<InlineData("absent")>]
[<InlineData("partial")>]
[<InlineData("duplicate")>]
[<InlineData("unselected")>]
[<InlineData("ambiguous")>]
[<InlineData("arity")>]
[<InlineData("deprecated")>]
let ``result range project validates every supplied contract before output writes`` shape =
  let directory = Path.Combine(__SOURCE_DIRECTORY__, "../../artifacts/ffi-contract-20261013", "result-project-" + Guid.NewGuid().ToString("N")) |> Path.GetFullPath
  try
    Directory.CreateDirectory directory |> ignore
    let pilot, output = signalProject directory "long read(int fd, void *buffer, unsigned long count);"
    let project = PilotSerializer.loadFromFile pilot |> function Ok project -> project | Error error -> failwith error
    if shape = "deprecated" then File.WriteAllText(project.Library.Headers.Head, "long read(int fd, void *buffer, unsigned long count) __attribute__((deprecated));")
    let ns = { project.Namespaces.Head with Name = "Fidelity.Result.Native"; Functions = (if shape = "unselected" then ["other"] else ["read"]); Signals = [] }
    let project = { project with Library = { project.Library with Introspection = [] }
                                 Namespaces = if shape = "ambiguous" then [ns; { ns with Name = "Fidelity.Result.Other" }] else [ns]
                                 ErrorConventions = if shape = "positive" || shape = "absent" then None else Some { Default = PilotTypes.Errno; Overrides = Map.empty } }
    let contract =
      "result_range = { identity = \"posix.read\", version = \"2024\", source = \"/contracts/read.pilot.toml\", lower = { constant = -1 }, upper = { argument = " + (if shape = "arity" then "3" else "2") + " } }\n"
    let contract = if shape = "partial" then contract.Replace("version = \"2024\", ", "") else if shape = "absent" then "" else contract
    let binding = "\n[[options.bindings]]\nname = \"read\"\n" + contract
    let contents = PilotSerializer.toTomlString project + binding + (if shape = "duplicate" then binding else "")
    File.WriteAllText(pilot, contents)
    Directory.CreateDirectory output |> ignore
    let sentinel = Path.Combine(output, "Types.clef")
    File.WriteAllText(sentinel, "// Existing result bindings\n")
    match BindingGenerator.generateFromProject pilot false false Types.LP64 with
    | Ok result when shape = "positive" || shape = "absent" ->
      let source = File.ReadAllText(Path.Combine(output, "Native", "Native.clef"))
      if shape = "positive" then Assert.Contains("Binding = \"Fidelity.Result.Native.read\"", source)
      else Assert.DoesNotContain("ResultRangeDescriptor", source)
      Assert.Contains(Path.Combine(output, "Native", "Native.clef"), result.OutputFiles)
    | Ok _ -> failwith "Invalid result contract must refuse before writes"
    | Error error ->
      let reason =
        match shape with
        | "partial" -> "result_range requires nonempty version"
        | "duplicate" -> "read result_range: duplicate binding projections"
        | "arity" -> "read result_range: argument 3 is outside native arity 3"
        | "unselected" | "ambiguous" | "deprecated" -> "read result_range: requires exactly one selected native declaration"
        | _ -> failwith error
      Assert.Contains(reason, error)
      Assert.Equal("// Existing result bindings\n", File.ReadAllText sentinel)
      Assert.Equal<string array>([|sentinel|], Directory.GetFiles(output, "*", SearchOption.AllDirectories))
  finally
    if Directory.Exists directory then Directory.Delete(directory, true)

// Signal refusals are structured (#195): GIR evidence is sited at the introspection files
// (GIR keeps no positions); g_signal_connect_data's own shape at its clang declaration.
[<Theory>]
[<InlineData("carrier", "FS9168", "script-message-received", "native pointee 'WebKitJavascriptResult' has no declared carrier in the binding Types module")>]
[<InlineData("nullability", "FS9169", "load-accepted", "GIR nullability evidence does not describe a native data pointer")>]
[<InlineData("contradictory", "FS9145", "script-message-received", "contradictory GIR nullability evidence")>]
[<InlineData("float", "FS9144", "progress-changed", "has no supported callback entry declaration, so the signal is not projected")>]
[<InlineData("names", "FS9171", "script-message-received", "callback parameter names must be present and unique for nullability admission")>]
[<InlineData("connect", "FS9170", "closed", "g_signal_connect_data handler requires its declared generic void callback slot")>]
[<InlineData("connect-missing", "FS9159", "closed", "GObject signals require `g_signal_connect_data`; list glib-object.h among the pilot headers")>]
let ``each signal refusal carries its code, its site and its pilot key`` shape (code: string) (signalName: string) (reason: string) =
    let signal = (selectedSignals gir [ $"UserContentManager::{signalName}" ]).Head
    let signal =
        match shape with
        | "nullability" -> { signal with Parameters = signal.Parameters |> List.mapi (fun i (n, t, nullable) -> n, t, nullable || i = 1) }
        | "contradictory" -> { signal with Parameters = signal.Parameters |> List.map (fun (n, t, _) -> n, t, true); NonnullParameters = Set.singleton "value" }
        | "names" -> { signal with Parameters = signal.Parameters @ signal.Parameters }
        | _ -> signal
    let declarations =
        headerDecls |> List.choose (function
            | CppParser.Declaration.Function f when f.Name = "g_signal_connect_data" ->
                if shape = "connect-missing" then None
                elif shape = "connect" then Some (CppParser.Declaration.Function { f with Parameters = f.Parameters |> List.mapi (fun i (n, t) -> if i = 2 then n, "void *" else n, t) })
                else Some (CppParser.Declaration.Function f)
            | declaration -> Some declaration)
    let context =
        { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
            PhantomMarkers = if shape = "carrier" then Set.ofList ["WebKitUserContentManager"; "GClosure"] else (signalContext Types.LP64).PhantomMarkers }
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> TypedSignalGenerator.render "Fidelity.WebKit.Native" [signal] declarations context |> ignore)
    let d = error.Refusal.Diagnostics |> List.exactlyOne
    Assert.Equal(code, d.Code)
    Assert.EndsWith(reason, d.Message)
    match shape with
    | "connect" ->
        Assert.Equal(TestHelpers.constructed.Location, d.Source)
        Assert.Equal(None, d.Pilot)
    | "connect-missing" ->
        Assert.Equal(Diagnostics.Located.Unknown "no parsed header declares C symbol 'g_signal_connect_data'", d.Source)
        Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "library.headers"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    | _ ->
        Assert.StartsWith($"signal 'WebKit2.UserContentManager::{signalName}'", d.Message)
        match d.Source with
        | Diagnostics.Located.Unknown why -> Assert.StartsWith("GIR introspection data carries no source position for signal 'WebKit2.UserContentManager::", why)
        | known -> failwithf "GIR evidence has no position, got %A" known
        Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "library.introspection"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
