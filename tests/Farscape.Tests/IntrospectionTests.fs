module Farscape.Tests.IntrospectionTests

open System
open System.IO
open Xunit
open Farscape.Core
open TestHelpers

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
    [ CppParser.Declaration.Typedef (mkTypedef "gpointer" "void *")
      CppParser.Declaration.Typedef (mkTypedef "gchar" "char")
      CppParser.Declaration.Typedef (mkTypedef "gboolean" "int")
      CppParser.Declaration.Typedef (mkTypedef "gulong" "unsigned long")
      CppParser.Declaration.Typedef (mkTypedef "GCallback" "void (*)(void)")
      CppParser.Declaration.Typedef (mkTypedef "GClosureNotify" "void (*)(gpointer, GClosure *)")
      CppParser.Declaration.Typedef (mkTypedef "WebKitLoadEvent" "enum WebKitLoadEvent")
      CppParser.Declaration.Enum (mkEnum "WebKitLoadEvent" [ { Name = "WEBKIT_LOAD_STARTED"; Value = 0L; Documentation = None } ] None)
      CppParser.Declaration.Enum (mkEnum "GConnectFlags" [ { Name = "G_CONNECT_DEFAULT"; Value = 0L; Documentation = None } ] None)
      CppParser.Declaration.Typedef (mkTypedef "GConnectFlags" "enum GConnectFlags")
      CppParser.Declaration.Function
          (mkFunc "g_signal_connect_data" "gulong"
              [ "instance", "gpointer"; "detailed_signal", "const gchar *"; "c_handler", "GCallback"
                "data", "gpointer"; "destroy_data", "GClosureNotify"; "connect_flags", "GConnectFlags" ]) ]

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
    let ctx = FidelityCodeGenerator.buildGenerationContext headerDecls Types.LP64 Map.empty
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        let files = TypedSignalGenerator.generate "Fidelity.WebKit.Native" (select [ "UserContentManager::script-message-received"; "UserContentManager::load-accepted" ]) headerDecls ctx dir
        let text = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Equal<string list>([ Path.Combine(dir, "Bridge/Signals.clef") ], files)
        Assert.Contains("module Fidelity.WebKit.Native.Bridge.Signals", text)
        Assert.Contains("type UserContentManagerScriptMessageReceivedListener = { ScriptMessageReceived: FnPtr<CHandle<unit> -> CHandle<unit> -> CHandle<unit> -> unit> }", text)
        Assert.Contains("Record = \"UserContentManagerScriptMessageReceivedListener\"; Field = \"ScriptMessageReceived\"; Signature = { CName = \"WebKitUserContentManager::script-message-received\"", text)
        Assert.Contains("{ Name = \"value\"; Type = Pointer 64; PassBy = Value }; { Name = \"user_data\"; Type = Pointer 64; PassBy = Value } |]; ReturnType = Void", text)
        Assert.Contains("[<FidelityExtern(\"gobject-2.0\", \"g_signal_connect_data\")>]", text)
        Assert.Contains("let connectUserContentManagerScriptMessageReceived (instance: CHandle<unit>) (listener: UserContentManagerScriptMessageReceivedListener) : int =", text)
        Assert.Contains("connectUserContentManagerScriptMessageReceivedNative instance \"script-message-received\" listener.ScriptMessageReceived instance releaseEntry.Release 0", text)
        Assert.Contains("type private Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry = { Release: FnPtr<CHandle<unit> -> CHandle<unit> -> unit> }", text)
        Assert.Contains("Record = \"Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry\"; Field = \"Release\"; Signature = { CName = \"Fidelity.WebKit.Native::GClosureNotify\"; Parameters = [| { Name = \"data\"; Type = Pointer 64; PassBy = Value }; { Name = \"closure\"; Type = Pointer 64; PassBy = Value } |]; ReturnType = Void", text)
        Assert.Contains("let private releaseEntry: Fidelity_002E_WebKit_002E_Native_SignalReleaseEntry = { Release = FnPtr.ofFunction releaseHandler }", text)
        Assert.Contains("type UserContentManagerLoadAcceptedListener = { LoadAccepted: FnPtr<CHandle<unit> -> int -> int -> CHandle<unit> -> int> }", text)
        Assert.Contains("{ Name = \"load_event\"; Type = Integer (Unsigned, 32); PassBy = Value }", text)
        Assert.Contains("{ Name = \"count\"; Type = Integer (Unsigned, 32); PassBy = Value }", text)
        Assert.Contains("ReturnType = Integer (Signed, 32)", text)
        Assert.Contains("ReturnType = Integer (Unsigned, 64)", text)
        Assert.DoesNotContain("nativeint", text)
        Assert.DoesNotContain("option<", text)
        // A nullable parameter has no interior representation at the entry: the signal is rejected, not projected.
        let error = Assert.ThrowsAny<Exception>(fun () -> TypedSignalGenerator.generate "Fidelity.WebKit.Native" (select [ "UserContentManager::load-changed" ]) headerDecls ctx dir |> ignore)
        Assert.Contains("'uri' is nullable", error.Message)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)

[<Fact>]
let ``closed signal factories preserve payload arity and result while omitting native context`` () =
    let ns = IntrospectionParser.parseDocument gir |> function Ok ns -> ns | Error e -> failwith e
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ ns.Name, ns ] }
    let signals =
        IntrospectionParser.select repo [ "UserContentManager::closed"; "UserContentManager::script-message-received"; "UserContentManager::load-accepted" ]
        |> function Ok s -> s | Error e -> failwith e
    let ctx = FidelityCodeGenerator.buildGenerationContext headerDecls Types.LP64 Map.empty
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        TypedSignalGenerator.generate "Fidelity.WebKit.Native" signals headerDecls ctx dir |> ignore
        let text = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Contains("let inline onUserContentManagerClosed (instance: CHandle<unit>) (handler: (unit -> unit)) : int =", text)
        Assert.Contains("let private makeUserContentManagerClosedEntry (handler: (unit -> unit)) : UserContentManagerClosedListener = NativeDefault.zeroed ()", text)
        Assert.Contains("let private adaptUserContentManagerClosed (handler: (unit -> unit)) (_instance: CHandle<unit>)  (_data: CHandle<unit>) : unit = handler ()", text)
        Assert.Contains("let inline onUserContentManagerScriptMessageReceived (instance: CHandle<unit>) (handler: (CHandle<unit> -> unit)) : int =", text)
        Assert.Contains("let private adaptUserContentManagerScriptMessageReceived (handler: (CHandle<unit> -> unit)) (_instance: CHandle<unit>) (value0: CHandle<unit>) (_data: CHandle<unit>) : unit = handler value0", text)
        Assert.Contains("let inline onUserContentManagerLoadAccepted (instance: CHandle<unit>) (handler: (int -> int -> int)) : int =", text)
        Assert.Contains("let private adaptUserContentManagerLoadAccepted (handler: (int -> int -> int)) (_instance: CHandle<unit>) (value0: int) (value1: int) (_data: CHandle<unit>) : int = handler value0 value1", text)
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
[<InlineData("pick-child", "result", "nullable")>]
let ``unsupported signal entry types identify the parameter or result`` signalName position reason =
    let ns = match IntrospectionParser.parseDocument gir with Ok ns -> ns | Error e -> failwith e
    let repo : IntrospectionParser.Repository = { Namespaces = Map.ofList [ ns.Name, ns ] }
    let signals = IntrospectionParser.select repo [ $"UserContentManager::{signalName}" ] |> function Ok s -> s | Error e -> failwith e
    let ctx = FidelityCodeGenerator.buildGenerationContext headerDecls Types.LP64 Map.empty
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
    let ctx = FidelityCodeGenerator.buildGenerationContext headerDecls Types.LP64 Map.empty
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
