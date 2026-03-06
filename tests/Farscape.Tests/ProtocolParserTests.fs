module Farscape.Tests.ProtocolParserTests

open Xunit
open Farscape.Core

// =============================================================================
// XML Parsing Tests (inline XML, no file system dependency)
// =============================================================================

let private simpleProtocol = """<?xml version="1.0" encoding="UTF-8"?>
<protocol name="wayland">
  <interface name="wl_display" version="1">
    <description summary="core global object"/>
    <request name="sync">
      <description summary="asynchronous roundtrip"/>
      <arg name="callback" type="new_id" interface="wl_callback"/>
    </request>
    <request name="get_registry">
      <arg name="registry" type="new_id" interface="wl_registry"/>
    </request>
    <enum name="error">
      <entry name="invalid_object" value="0" summary="server couldn't find object"/>
      <entry name="invalid_method" value="1" summary="method doesn't exist"/>
      <entry name="no_memory" value="2" summary="server ran out of memory"/>
      <entry name="implementation" value="3" summary="implementation error"/>
    </enum>
    <event name="error">
      <description summary="fatal error event"/>
      <arg name="object_id" type="object"/>
      <arg name="code" type="uint"/>
      <arg name="message" type="string"/>
    </event>
    <event name="delete_id">
      <arg name="id" type="uint"/>
    </event>
  </interface>
  <interface name="wl_callback" version="1">
    <event name="done">
      <arg name="callback_data" type="uint"/>
    </event>
  </interface>
</protocol>"""

[<Fact>]
let ``parseProtocolXml parses protocol name`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto -> Assert.Equal("wayland", proto.Name)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml parses multiple interfaces`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto -> Assert.Equal(2, proto.Interfaces.Length)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml parses interface name and version`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let display = proto.Interfaces.[0]
        Assert.Equal("wl_display", display.Name)
        Assert.Equal(1, display.Version)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml parses requests with new_id args`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let display = proto.Interfaces.[0]
        Assert.Equal(2, display.Requests.Length)
        let sync = display.Requests.[0]
        Assert.Equal("sync", sync.Name)
        Assert.Equal(1, sync.Args.Length)
        Assert.Equal(ProtocolParser.NewId, sync.Args.[0].Type)
        Assert.Equal(Some "wl_callback", sync.Args.[0].Interface)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml parses events with typed args`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let display = proto.Interfaces.[0]
        Assert.Equal(2, display.Events.Length)
        let errorEvt = display.Events.[0]
        Assert.Equal("error", errorEvt.Name)
        Assert.Equal(3, errorEvt.Args.Length)
        Assert.Equal(ProtocolParser.Object, errorEvt.Args.[0].Type)
        Assert.Equal(ProtocolParser.Uint, errorEvt.Args.[1].Type)
        Assert.Equal(ProtocolParser.String, errorEvt.Args.[2].Type)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml parses enum entries`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let display = proto.Interfaces.[0]
        Assert.Equal(1, display.Enums.Length)
        let err = display.Enums.[0]
        Assert.Equal("error", err.Name)
        Assert.Equal(4, err.Entries.Length)
        Assert.Equal("invalid_object", err.Entries.[0].Name)
        Assert.Equal("0", err.Entries.[0].Value)
        Assert.Equal("implementation", err.Entries.[3].Name)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml extracts description summary`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        Assert.Equal(Some "core global object", proto.Interfaces.[0].Documentation)
        Assert.Equal(Some "asynchronous roundtrip", proto.Interfaces.[0].Requests.[0].Documentation)
        Assert.Equal(Some "fatal error event", proto.Interfaces.[0].Events.[0].Documentation)
    | Error e -> failwith e

[<Fact>]
let ``parseProtocolXml rejects non-protocol root`` () =
    let bad = """<?xml version="1.0"?><notprotocol/>"""
    match ProtocolParser.parseProtocolXml bad with
    | Error e -> Assert.Contains("Root element must be <protocol>", e)
    | Ok _ -> failwith "Should have failed"

[<Fact>]
let ``parseProtocolXml returns error on malformed XML`` () =
    let bad = """<protocol><unclosed"""
    match ProtocolParser.parseProtocolXml bad with
    | Error _ -> () // expected
    | Ok _ -> failwith "Should have failed"

// =============================================================================
// Bitfield Enum Tests
// =============================================================================

let private bitfieldProtocol = """<?xml version="1.0"?>
<protocol name="test">
  <interface name="wl_output" version="1">
    <enum name="transform" bitfield="true">
      <entry name="normal" value="0"/>
      <entry name="rotate_90" value="1"/>
      <entry name="rotate_180" value="2"/>
    </enum>
  </interface>
</protocol>"""

[<Fact>]
let ``parseProtocolXml parses bitfield enum`` () =
    match ProtocolParser.parseProtocolXml bitfieldProtocol with
    | Ok proto ->
        let iface = proto.Interfaces.[0]
        Assert.True(iface.Enums.[0].IsBitfield)
    | Error e -> failwith e

// =============================================================================
// Declaration Mapping Tests
// =============================================================================

[<Fact>]
let ``toDeclarations produces typedef for interface handle`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let typedefs =
            decls |> List.choose (function
                | CppParser.Declaration.Typedef t -> Some t
                | _ -> None)
        // One typedef per interface: wl_display, wl_callback
        Assert.Equal(2, typedefs.Length)
        Assert.Equal("wl_display", typedefs.[0].Name)
        Assert.Equal("void *", typedefs.[0].UnderlyingType)
        Assert.Equal("wl_callback", typedefs.[1].Name)
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations produces delegate for each event`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let delegates =
            decls |> List.choose (function
                | CppParser.Declaration.Delegate d -> Some d
                | _ -> None)
        // wl_display has 2 events (error, delete_id) + wl_callback has 1 event (done)
        Assert.Equal(3, delegates.Length)
        Assert.Equal("WlDisplayErrorHandler", delegates.[0].Name)
        Assert.Equal("WlDisplayDeleteIdHandler", delegates.[1].Name)
        Assert.Equal("WlCallbackDoneHandler", delegates.[2].Name)
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations delegate has data and self parameters`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let errorDelegate =
            decls |> List.pick (function
                | CppParser.Declaration.Delegate d when d.Name = "WlDisplayErrorHandler" -> Some d
                | _ -> None)
        // Parameters: data (void*), wl_display (self), object_id, code, message
        Assert.Equal(5, errorDelegate.Parameters.Length)
        Assert.Equal(("data", "void *"), errorDelegate.Parameters.[0])
        Assert.Equal(("wl_display", "wl_display *"), errorDelegate.Parameters.[1])
        Assert.Equal("void", errorDelegate.ReturnType)
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations produces listener struct with delegate fields`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let listeners =
            decls |> List.choose (function
                | CppParser.Declaration.Struct s when s.Name.EndsWith("_listener") -> Some s
                | _ -> None)
        // wl_display_listener and wl_callback_listener
        Assert.Equal(2, listeners.Length)
        let displayListener = listeners.[0]
        Assert.Equal("wl_display_listener", displayListener.Name)
        Assert.Equal(2, displayListener.Fields.Length)
        Assert.Equal("error", displayListener.Fields.[0].Name)
        Assert.Equal("WlDisplayErrorHandler", displayListener.Fields.[0].Type)
        Assert.Equal("delete_id", displayListener.Fields.[1].Name)
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations produces enum with values`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let enums =
            decls |> List.choose (function
                | CppParser.Declaration.Enum e -> Some e
                | _ -> None)
        let errorEnum = enums |> List.find (fun e -> e.Name = "wl_display_error")
        Assert.Equal(4, errorEnum.Values.Length)
        Assert.Equal("invalid_object", errorEnum.Values.[0].Name)
        Assert.Equal(0L, errorEnum.Values.[0].Value)
        Assert.Equal("implementation", errorEnum.Values.[3].Name)
        Assert.Equal(3L, errorEnum.Values.[3].Value)
        Assert.Equal(Some "uint32_t", errorEnum.UnderlyingType)
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations produces request functions with typed parameters`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let funcs =
            decls |> List.choose (function
                | CppParser.Declaration.Function f -> Some f
                | _ -> None)
        let syncFunc = funcs |> List.find (fun f -> f.Name = "wl_display_sync")
        // sync has new_id arg → return type is wl_callback*
        // parameters: just self (new_id is the return, not a param)
        Assert.Equal("wl_callback *", syncFunc.ReturnType)
        Assert.Equal(1, syncFunc.Parameters.Length)
        Assert.Equal(("self", "wl_display *"), syncFunc.Parameters.[0])
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations request without new_id returns void`` () =
    // Build a simple protocol with a request that has no new_id
    let xml = """<?xml version="1.0"?>
<protocol name="test">
  <interface name="wl_surface" version="1">
    <request name="attach">
      <arg name="buffer" type="object" interface="wl_buffer"/>
      <arg name="x" type="int"/>
      <arg name="y" type="int"/>
    </request>
  </interface>
</protocol>"""
    match ProtocolParser.parseProtocolXml xml with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let attach =
            decls |> List.pick (function
                | CppParser.Declaration.Function f when f.Name = "wl_surface_attach" -> Some f
                | _ -> None)
        Assert.Equal("void", attach.ReturnType)
        // Parameters: self, buffer, x, y
        Assert.Equal(4, attach.Parameters.Length)
        Assert.Equal(("self", "wl_surface *"), attach.Parameters.[0])
        Assert.Equal(("buffer", "wl_buffer *"), attach.Parameters.[1])
        Assert.Equal(("x", "int32_t"), attach.Parameters.[2])
    | Error e -> failwith e

[<Fact>]
let ``toDeclarations hex enum values are parsed correctly`` () =
    let xml = """<?xml version="1.0"?>
<protocol name="test">
  <interface name="wl_shm" version="1">
    <enum name="format">
      <entry name="argb8888" value="0"/>
      <entry name="xrgb8888" value="1"/>
      <entry name="c8" value="0x20203843"/>
    </enum>
  </interface>
</protocol>"""
    match ProtocolParser.parseProtocolXml xml with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let formatEnum =
            decls |> List.pick (function
                | CppParser.Declaration.Enum e when e.Name = "wl_shm_format" -> Some e
                | _ -> None)
        Assert.Equal(0L, formatEnum.Values.[0].Value)
        Assert.Equal(1L, formatEnum.Values.[1].Value)
        Assert.Equal(0x20203843L, formatEnum.Values.[2].Value)
    | Error e -> failwith e

[<Fact>]
let ``interfaceToDeclarations produces declarations in correct order`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let displayDecls = ProtocolParser.interfaceToDeclarations proto.Interfaces.[0]
        // Order: typedef, enums, delegates, listener struct, request functions
        match displayDecls.[0] with
        | CppParser.Declaration.Typedef _ -> ()
        | other -> failwith $"Expected Typedef first, got {other}"
        match displayDecls.[1] with
        | CppParser.Declaration.Enum _ -> ()
        | other -> failwith $"Expected Enum second, got {other}"
        // delegates follow enums
        let delegateIdx = displayDecls |> List.tryFindIndex (function CppParser.Declaration.Delegate _ -> true | _ -> false)
        let structIdx = displayDecls |> List.tryFindIndex (function CppParser.Declaration.Struct _ -> true | _ -> false)
        let funcIdx = displayDecls |> List.tryFindIndex (function CppParser.Declaration.Function _ -> true | _ -> false)
        Assert.True(delegateIdx.Value < structIdx.Value, "Delegates should come before listener struct")
        Assert.True(structIdx.Value < funcIdx.Value, "Listener struct should come before request functions")
    | Error e -> failwith e

[<Fact>]
let ``interface with no events produces no listener struct`` () =
    let xml = """<?xml version="1.0"?>
<protocol name="test">
  <interface name="wl_shm_pool" version="1">
    <request name="destroy" type="destructor"/>
  </interface>
</protocol>"""
    match ProtocolParser.parseProtocolXml xml with
    | Ok proto ->
        let decls = ProtocolParser.toDeclarations proto
        let structs =
            decls |> List.choose (function
                | CppParser.Declaration.Struct _ -> Some true
                | _ -> None)
        Assert.Empty(structs)
    | Error e -> failwith e

[<Fact>]
let ``destructor request is parsed with IsDestructor attribute`` () =
    let xml = """<?xml version="1.0"?>
<protocol name="test">
  <interface name="wl_shm_pool" version="1">
    <request name="destroy" type="destructor"/>
  </interface>
</protocol>"""
    match ProtocolParser.parseProtocolXml xml with
    | Ok proto ->
        Assert.True(proto.Interfaces.[0].Requests.[0].IsDestructor)
    | Error e -> failwith e

// =============================================================================
// PilotSerializer XML Fields Round-trip Tests
// =============================================================================

[<Fact>]
let ``PilotSerializer round-trips xml_protocols`` () =
    let project : PilotTypes.PilotProject = {
        Library = {
            Name = "wayland"
            Headers = ["wayland-client.h"]
            XmlProtocols = ["wayland.xml"; "xdg-shell.xml"]
            IncludePaths = []
            Defines = []
            MacroPrefixes = []
            PkgConfig = []
        }
        Output = { Mode = "fidelity"; Directory = "./out" }
        Namespaces = []
        ErrorConventions = None
        Options = None
        Callbacks = None
        Nonnull = None
        ProtocolConfig = None
        Layer3 = None
    }
    let toml = PilotSerializer.toTomlString project
    Assert.Contains("xml_protocols", toml)
    let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok roundTripped ->
        Assert.Equal<string list>(["wayland.xml"; "xdg-shell.xml"], roundTripped.Library.XmlProtocols)
    | Error e -> failwith e

[<Fact>]
let ``PilotSerializer round-trips xml_interfaces`` () =
    let project : PilotTypes.PilotProject = {
        Library = {
            Name = "wayland"
            Headers = ["wayland-client.h"]
            XmlProtocols = []
            IncludePaths = []
            Defines = []
            MacroPrefixes = []
            PkgConfig = []
        }
        Output = { Mode = "fidelity"; Directory = "./out" }
        Namespaces = [{
            Name = "Fidelity.Wayland.Core"
            Description = "Core Wayland protocol"
            Library = "wayland"
            Prefixes = ["wl"]
            Functions = []
            XmlInterfaces = ["wl_display"; "wl_registry"; "wl_surface"]
        }]
        ErrorConventions = None
        Options = None
        Callbacks = None
        Nonnull = None
        ProtocolConfig = None
        Layer3 = None
    }
    let toml = PilotSerializer.toTomlString project
    Assert.Contains("xml_interfaces", toml)
    let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok roundTripped ->
        Assert.Equal<string list>(["wl_display"; "wl_registry"; "wl_surface"], roundTripped.Namespaces.[0].XmlInterfaces)
    | Error e -> failwith e

// =============================================================================
// CodeRenderer DelegateType Tests
// =============================================================================

[<Fact>]
let ``render DelegateType produces delegate syntax`` () =
    let decl = CodeAST.DelegateType(
        "WlDisplayErrorHandler",
        [("data", CodeAST.FsType.Named "nativeint")
         ("display", CodeAST.FsType.Named "nativeint")
         ("code", CodeAST.FsType.Named "uint32")],
        CodeAST.FsType.Named "unit",
        None)
    let output = CodeRenderer.render decl
    Assert.Contains("type WlDisplayErrorHandler =", output)
    Assert.Contains("delegate of", output)
    Assert.Contains("data: nativeint", output)
    Assert.Contains("-> unit", output)

[<Fact>]
let ``render DelegateType with documentation`` () =
    let decl = CodeAST.DelegateType(
        "TestHandler",
        [("value", CodeAST.FsType.Named "int32")],
        CodeAST.FsType.Named "unit",
        Some "A test handler delegate")
    let output = CodeRenderer.render decl
    Assert.Contains("/// A test handler delegate", output)
    Assert.Contains("type TestHandler =", output)

// =============================================================================
// Request Code Generation Tests
// =============================================================================

let private marshalConfig : ProtocolParser.MarshalConfig = {
    MarshalFunction = "wl_proxy_marshal_array_flags"
    MarshalModule = "Fidelity.Wayland.Core"
    VersionFunction = "wl_proxy_get_version"
    InterfaceResolution = "dlsym"
    DestroyFlag = 1u
}

let private requestWithArgs = """<?xml version="1.0"?>
<protocol name="test">
  <interface name="wl_surface" version="6">
    <request name="attach">
      <arg name="buffer" type="object" interface="wl_buffer" allow-null="true"/>
      <arg name="x" type="int"/>
      <arg name="y" type="int"/>
    </request>
    <request name="destroy" type="destructor"/>
  </interface>
</protocol>"""

[<Fact>]
let ``request with args unwraps malloc Option`` () =
    match ProtocolParser.parseProtocolXml requestWithArgs with
    | Ok proto ->
        let iface = proto.Interfaces.[0]
        let decls = ProtocolParser.interfaceRequestDecls iface marshalConfig
        let output = decls |> List.map CodeRenderer.render |> String.concat "\n"
        // malloc returns Option<nativeint> — must be unwrapped via match
        Assert.Contains("match Fidelity.Libc.Memory.malloc", output)
        Assert.Contains("| Some v -> v", output)
        Assert.Contains("| None -> 0n", output)
        // Must NOT use raw malloc result directly as nativeint
        Assert.DoesNotContain("let argsRaw = malloc", output)
    | Error e -> failwith e

[<Fact>]
let ``destructor request does not use malloc`` () =
    match ProtocolParser.parseProtocolXml requestWithArgs with
    | Ok proto ->
        let iface = proto.Interfaces.[0]
        let decls = ProtocolParser.interfaceRequestDecls iface marshalConfig
        let output = decls |> List.map CodeRenderer.render |> String.concat "\n"
        let destroySection = output.Split("let wl_surface_destroy").[1].Split("let wl_surface_").[0]
        // Destructor with no args should not use malloc
        Assert.DoesNotContain("malloc", destroySection)
    | Error e -> failwith e
