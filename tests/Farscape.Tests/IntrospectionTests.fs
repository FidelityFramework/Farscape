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
    let signals =
        match IntrospectionParser.select repo [ "UserContentManager::script-message-received"; "UserContentManager::load-changed" ] with
        | Ok s -> s
        | Error e -> failwith e
    let ctx = FidelityCodeGenerator.buildGenerationContext headerDecls Types.LP64 Map.empty
    let dir = Path.Combine(Path.GetTempPath(), "farscape-signals-" + Guid.NewGuid().ToString("N"))
    try
        let files = TypedSignalGenerator.generate "Fidelity.WebKit.Native" signals headerDecls ctx dir
        let text = File.ReadAllText(Path.Combine(dir, "Bridge/Signals.clef"))
        Assert.Equal<string list>([ Path.Combine(dir, "Bridge/Signals.clef") ], files)
        Assert.Contains("module Fidelity.WebKit.Native.Bridge.Signals", text)
        Assert.Contains("type UserContentManagerScriptMessageReceivedListener = { ScriptMessageReceived: FnPtr<CHandle<unit> -> CHandle<unit> -> CHandle<unit> -> unit> }", text)
        Assert.Contains("Record = \"UserContentManagerScriptMessageReceivedListener\"; Field = \"ScriptMessageReceived\"; Signature = { CName = \"webkit_user_content_manager_script_message_received\"", text)
        Assert.Contains("{ Name = \"value\"; Type = Pointer 64; PassBy = Value }; { Name = \"user_data\"; Type = Pointer 64; PassBy = Value } |]; ReturnType = Void", text)
        Assert.Contains("type UserContentManagerLoadChangedListener = { LoadChanged: FnPtr<CHandle<unit> -> int -> CHandle<unit> -> CHandle<unit> -> int> }", text)
        Assert.Contains("{ Name = \"load_event\"; Type = Integer (Unsigned, 32); PassBy = Value }", text)
        Assert.Contains("ReturnType = Integer (Signed, 32)", text)
        Assert.Contains("[<FidelityExtern(\"gobject-2.0\", \"g_signal_connect_data\")>]", text)
        Assert.Contains("let connectUserContentManagerScriptMessageReceived (instance: CHandle<unit>) (listener: UserContentManagerScriptMessageReceivedListener) (data: option<CHandle<unit>>) : int =", text)
        Assert.Contains("connectUserContentManagerScriptMessageReceivedNative instance \"script-message-received\" listener.ScriptMessageReceived data (FnPtr.ofFunction releaseHandler) 0", text)
        Assert.Contains("ReturnType = Integer (Unsigned, 64)", text)
        Assert.DoesNotContain("nativeint", text)
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
