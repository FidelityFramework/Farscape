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
let ``toTypeDeclarations produces typedef for interface handle`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toTypeDeclarations proto
        let typedefs =
            decls |> List.choose (function
                | CppParser.Declaration.Typedef t -> Some t
                | _ -> None)
        // One typedef per interface: wl_display, wl_callback
        Assert.Equal(2, typedefs.Length)
        Assert.Equal("wl_display", typedefs.[0].Name)
        Assert.Equal("struct wl_display_opaque *", typedefs.[0].UnderlyingType)
        Assert.Equal("wl_callback", typedefs.[1].Name)
    | Error e -> failwith e

[<Fact>]
let ``toTypeDeclarations produces delegate for each event`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toTypeDeclarations proto
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
let ``toTypeDeclarations delegate has data and self parameters`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toTypeDeclarations proto
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
let ``toTypeDeclarations produces listener struct with delegate fields`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toTypeDeclarations proto
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
let ``toTypeDeclarations produces enum with values`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let decls = ProtocolParser.toTypeDeclarations proto
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
        // The XML declares no C underlying type, and no argument references this enum.
        Assert.Equal(None, errorEnum.UnderlyingType)
        Assert.Equal(CppParser.ProtocolUndeclared "no protocol argument references enum 'wl_display.error'", errorEnum.Representation)
    | Error e -> failwith e

[<Fact>]
let ``toTypeDeclarations hex enum values are parsed correctly`` () =
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
        let decls = ProtocolParser.toTypeDeclarations proto
        let formatEnum =
            decls |> List.pick (function
                | CppParser.Declaration.Enum e when e.Name = "wl_shm_format" -> Some e
                | _ -> None)
        Assert.Equal(0L, formatEnum.Values.[0].Value)
        Assert.Equal(1L, formatEnum.Values.[1].Value)
        Assert.Equal(0x20203843L, formatEnum.Values.[2].Value)
    | Error e -> failwith e

[<Fact>]
let ``interfaceTypeDeclarations preserves type metadata order without synthetic request symbols`` () =
    match ProtocolParser.parseProtocolXml simpleProtocol with
    | Ok proto ->
        let displayDecls = ProtocolParser.interfaceTypeDeclarations proto.Interfaces.[0]
        // Order: typedef, enums, delegates, listener struct; requests remain in the IR.
        match displayDecls.[0] with
        | CppParser.Declaration.Typedef _ -> ()
        | other -> failwith $"Expected Typedef first, got {other}"
        match displayDecls.[1] with
        | CppParser.Declaration.Enum _ -> ()
        | other -> failwith $"Expected Enum second, got {other}"
        // delegates follow enums
        let delegateIdx = displayDecls |> List.tryFindIndex (function CppParser.Declaration.Delegate _ -> true | _ -> false)
        let structIdx = displayDecls |> List.tryFindIndex (function CppParser.Declaration.Struct _ -> true | _ -> false)
        Assert.True(delegateIdx.Value < structIdx.Value, "Delegates should come before listener struct")
        Assert.DoesNotContain(displayDecls, fun declaration ->
            match declaration with CppParser.Declaration.Function _ -> true | _ -> false)
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
        let decls = ProtocolParser.toTypeDeclarations proto
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
            XmlProtocols = ["wayland.xml"; "xdg-shell.xml"]; Introspection = []
            IncludePaths = []
            SystemIncludePaths = []
            Defines = []
            MacroPrefixes = []
            PkgConfig = []
            Artifact = None
        }
        Output = { Mode = "fidelity"; Directory = "./out" }
        Namespaces = []
        ErrorConventions = None
        Options = None
        Callbacks = None
        Nonnull = None
        ProtocolConfig = None
        Layer3 = None
        CppConfig = None
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
            XmlProtocols = []; Introspection = []
            IncludePaths = []
            SystemIncludePaths = []
            Defines = []
            MacroPrefixes = []
            PkgConfig = []
            Artifact = None
        }
        Output = { Mode = "fidelity"; Directory = "./out" }
        Namespaces = [{
            Name = "Fidelity.Wayland.Core"
            Description = "Core Wayland protocol"
            Library = "wayland"
            Prefixes = ["wl"]
            Functions = []
            XmlInterfaces = ["wl_display"; "wl_registry"; "wl_surface"]; Signals = []
        }]
        ErrorConventions = None
        Options = None
        Callbacks = None
        Nonnull = None
        ProtocolConfig = None
        Layer3 = None
        CppConfig = None
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
        "ScalarHandler",
        [("value", CodeAST.FsType.Named "int")
         ("code", CodeAST.FsType.Named "uint")],
        CodeAST.FsType.Named "unit",
        None)
    let output = CodeRenderer.render decl
    Assert.Contains("type ScalarHandler =", output)
    Assert.Contains("delegate of", output)
    Assert.Contains("value: int", output)
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
// Retired Request Projection Contract Tests
// =============================================================================

let private marshalConfig : ProtocolParser.MarshalConfig = {
    MarshalFunction = "wl_proxy_marshal_array_flags"
    MarshalModule = "Fidelity.Wayland.Core"
    VersionFunction = "wl_proxy_get_version"
    InterfaceResolution = "dlsym"
    DestroyFlag = 1u
}

let private parseOrFail xml =
    match ProtocolParser.parseProtocolXml xml with
    | Ok protocol -> protocol
    | Error error -> failwith error

let private requestProtocol requestKind =
    let request =
        match requestKind with
        | "constructor" -> """<request name="create_surface"><arg name="id" type="new_id" interface="wl_surface"/></request>"""
        | "nullable-object" -> """<request name="attach"><arg name="buffer" type="object" interface="wl_buffer" allow-null="true"/><arg name="x" type="int"/><arg name="y" type="int"/></request>"""
        | "destructor" -> """<request name="destroy" type="destructor"/>"""
        | "untyped-constructor" -> """<request name="bind"><arg name="id" type="new_id"/></request>"""
        | "scalar" -> """<request name="set_scale"><arg name="scale" type="int"/></request>"""
        | _ -> failwith $"Unknown request fixture {requestKind}"
    // A request-free interface precedes the requesting one. Aggregate APIs must
    // inspect the entire protocol before returning either output stream.
    parseOrFail $"""<protocol name="boundary">
  <interface name="wl_callback" version="1">
    <event name="done"><arg name="serial" type="uint"/></event>
  </interface>
  <interface name="wl_surface" version="6">{request}</interface>
</protocol>"""

let private knownHandles = Set.ofList ["wl_callback"; "wl_surface"; "wl_buffer"]

let private invokeRetiredProjection entryPoint (protocol: ProtocolParser.Protocol) =
    let iface = protocol.Interfaces |> List.find (fun item -> not item.Requests.IsEmpty)
    match entryPoint with
    | "interfaceRequestDecls" -> ProtocolParser.interfaceRequestDecls iface marshalConfig knownHandles |> ignore
    | "toRequestDecls" -> ProtocolParser.toRequestDecls protocol marshalConfig knownHandles |> ignore
    | "protocolToOutput" -> ProtocolParser.protocolToOutput protocol marshalConfig knownHandles |> ignore
    | "interfaceToDeclarations" -> ProtocolParser.interfaceToDeclarations iface |> ignore
    | "toDeclarations" -> ProtocolParser.toDeclarations protocol |> ignore
    | _ -> failwith $"Unknown projection entry point {entryPoint}"

[<Theory>]
[<InlineData("interfaceRequestDecls", "constructor")>]
[<InlineData("interfaceRequestDecls", "nullable-object")>]
[<InlineData("interfaceRequestDecls", "destructor")>]
[<InlineData("interfaceRequestDecls", "untyped-constructor")>]
[<InlineData("interfaceRequestDecls", "scalar")>]
[<InlineData("toRequestDecls", "constructor")>]
[<InlineData("toRequestDecls", "nullable-object")>]
[<InlineData("toRequestDecls", "destructor")>]
[<InlineData("toRequestDecls", "untyped-constructor")>]
[<InlineData("toRequestDecls", "scalar")>]
[<InlineData("protocolToOutput", "constructor")>]
[<InlineData("protocolToOutput", "nullable-object")>]
[<InlineData("protocolToOutput", "destructor")>]
[<InlineData("protocolToOutput", "untyped-constructor")>]
[<InlineData("protocolToOutput", "scalar")>]
[<InlineData("interfaceToDeclarations", "constructor")>]
[<InlineData("interfaceToDeclarations", "nullable-object")>]
[<InlineData("interfaceToDeclarations", "destructor")>]
[<InlineData("interfaceToDeclarations", "untyped-constructor")>]
[<InlineData("interfaceToDeclarations", "scalar")>]
[<InlineData("toDeclarations", "constructor")>]
[<InlineData("toDeclarations", "nullable-object")>]
[<InlineData("toDeclarations", "destructor")>]
[<InlineData("toDeclarations", "untyped-constructor")>]
[<InlineData("toDeclarations", "scalar")>]
let ``public legacy request projections refuse before returning any AST`` entryPoint requestKind =
    let protocol = requestProtocol requestKind
    let iface = protocol.Interfaces.[1]
    let request = iface.Requests.Head
    let mutable returnedAst = false
    let error = Assert.Throws<System.InvalidOperationException>(fun () ->
        invokeRetiredProjection entryPoint protocol
        returnedAst <- true)
    Assert.False(returnedAst, "A rejected public API must not return any marshal or extern AST")
    Assert.Equal(
        $"ProtocolParser.{entryPoint} cannot generate XML request '{iface.Name}.{request.Name}': legacy marshal and synthetic extern projection is retired. Use TypedProtocolGenerator with an admitted typed protocol dispatch contract, or toTypeDeclarations for protocol type metadata.",
        error.Message)

[<Fact>]
let ``type metadata remains available for a refused request-bearing protocol`` () =
    let protocol = requestProtocol "nullable-object"
    let declarations = ProtocolParser.toTypeDeclarations protocol
    Assert.Equal(2, declarations |> List.sumBy (function CppParser.Declaration.Typedef _ -> 1 | _ -> 0))
    Assert.Contains(declarations, fun declaration ->
        match declaration with
        | CppParser.Declaration.Delegate handler -> handler.Name = "WlCallbackDoneHandler"
        | _ -> false)
    Assert.Contains(declarations, fun declaration ->
        match declaration with
        | CppParser.Declaration.Struct listener -> listener.Name = "wl_callback_listener"
        | _ -> false)
    Assert.DoesNotContain(declarations, fun declaration ->
        match declaration with CppParser.Declaration.Function _ -> true | _ -> false)
    // Retaining semantic input is distinct from claiming a native request ABI.
    Assert.True(protocol.Interfaces.[1].Requests.Head.Args.Head.AllowNull)

[<Fact>]
let ``legacy entry points retain metadata and empty bodies for request-free protocols`` () =
    let protocol = parseOrFail """<protocol name="metadata">
  <interface name="wl_output" version="1">
    <enum name="transform"><entry name="normal" value="0"/></enum>
    <event name="done"><arg name="serial" type="uint"/></event>
  </interface>
</protocol>"""
    let iface = protocol.Interfaces.Head
    let expected = ProtocolParser.toTypeDeclarations protocol
    Assert.Empty(ProtocolParser.interfaceRequestDecls iface marshalConfig knownHandles)
    Assert.Empty(ProtocolParser.toRequestDecls protocol marshalConfig knownHandles)
    let types, bodies = ProtocolParser.protocolToOutput protocol marshalConfig knownHandles
    Assert.Equal<CppParser.Declaration list>(expected, types)
    Assert.Empty(bodies)
    Assert.Equal<CppParser.Declaration list>(expected, ProtocolParser.interfaceToDeclarations iface)
    Assert.Equal<CppParser.Declaration list>(expected, ProtocolParser.toDeclarations protocol)
    Assert.DoesNotContain(types, fun declaration ->
        match declaration with CppParser.Declaration.Function _ -> true | _ -> false)

[<Fact>]
let ``retired aggregate projections preserve empty protocol metadata`` () =
    let protocol = parseOrFail """<protocol name="empty"/>"""
    Assert.Empty(ProtocolParser.toRequestDecls protocol marshalConfig Set.empty)
    let types, bodies = ProtocolParser.protocolToOutput protocol marshalConfig Set.empty
    Assert.Empty(types)
    Assert.Empty(bodies)
    Assert.Empty(ProtocolParser.toDeclarations protocol)


// =============================================================================
// Protocol enum wire representation from the protocol's own declarations (issue #53)
// =============================================================================

let private wireProtocol (formatType: string) (otherType: string) = $"""<?xml version="1.0"?>
<protocol name="wire_test">
  <interface name="wl_shm" version="1">
    <request name="create_pool"><arg name="size" type="int"/></request>
    <enum name="format"><entry name="argb8888" value="0"/><entry name="c8" value="0x20203843"/></enum>
    <enum name="error"><entry name="invalid_format" value="0"/></enum>
    <event name="format"><arg name="format" type="{formatType}" enum="format"/></event>
  </interface>
  <interface name="wl_shm_pool" version="1">
    <request name="create_buffer"><arg name="format" type="{otherType}" enum="wl_shm.format"/></request>
  </interface>
</protocol>"""

let private protocolEnums xml =
    match ProtocolParser.parseProtocolXml xml with
    | Error e -> failwith e
    | Ok proto ->
        ProtocolParser.toTypeDeclarations proto
        |> List.choose (function CppParser.Declaration.Enum e -> Some (e.Name, e) | _ -> None) |> Map.ofList

[<Fact>]
let ``a protocol enum's wire type is the type its referencing arguments declare`` () =
    let enums = protocolEnums (wireProtocol "uint" "uint")
    Assert.Equal(
        CppParser.ProtocolWire ({ Bits = 32; IsSigned = false }, "2 protocol argument(s) reference enum 'wl_shm.format', first wl_shm.format arg 'format', each with type=\"uint\""),
        enums.["wl_shm_format"].Representation)
    Assert.Equal(None, enums.["wl_shm_format"].UnderlyingType)
    let signed = protocolEnums (wireProtocol "int" "int")
    Assert.Equal(CppParser.ProtocolWire ({ Bits = 32; IsSigned = true }, "2 protocol argument(s) reference enum 'wl_shm.format', first wl_shm.format arg 'format', each with type=\"int\""), signed.["wl_shm_format"].Representation)

[<Fact>]
let ``a protocol enum without one declared wire type has none`` () =
    let enums = protocolEnums (wireProtocol "uint" "int")
    Assert.Equal(
        CppParser.ProtocolUndeclared "the protocol arguments referencing enum 'wl_shm.format' declare type=\"uint\", type=\"int\", not one of type=\"uint\" or type=\"int\"",
        enums.["wl_shm_format"].Representation)
    Assert.Equal(CppParser.ProtocolUndeclared "no protocol argument references enum 'wl_shm.error'", enums.["wl_shm_error"].Representation)

[<Fact>]
let ``protocol enums resolve to their declared wire type or are refused`` () =
    let declarations =
        match ProtocolParser.parseProtocolXml (wireProtocol "uint" "uint") with
        | Ok proto -> ProtocolParser.toTypeDeclarations proto
        | Error e -> failwith e
    let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    let resolve = FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums
    match resolve "wl_shm_format" with
    | FidelityCodeGenerator.CEnum (_, r) ->
        Assert.Equal((TypeMapper.Unsigned, 32, TypeMapper.Declared, "enum wl_shm_format : uint32_t"), (r.Family, r.Bits, r.Stratum, r.CType))
    | other -> failwithf "Expected a protocol enum, got %A" other
    let error = Assert.ThrowsAny<exn>(fun () -> resolve "wl_shm_error" |> ignore)
    Assert.Equal("FS9106: protocol enum 'wl_shm_error' has no declared wire type: no protocol argument references enum 'wl_shm.error'.", error.Message)

[<Fact>]
let ``a parsed protocol enum value beyond its wire type is refused with its own code`` () =
    let xml = (wireProtocol "uint" "uint").Replace("<entry name=\"c8\" value=\"0x20203843\"/>", "<entry name=\"wide\" value=\"0x100000000\"/>")
    let declarations =
        match ProtocolParser.parseProtocolXml xml with
        | Ok proto -> ProtocolParser.toTypeDeclarations proto
        | Error e -> failwith e
    let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    let error = Assert.ThrowsAny<exn>(fun () ->
        FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums "wl_shm_format" |> ignore)
    Assert.Equal("FS9107: enumerator 'wide' of enum 'wl_shm_format' has value 4294967296, which its protocol's declared unsigned 32-bit representation cannot hold.", error.Message)

// =============================================================================
// Enum entry values (issue #160): decimal, 0x hexadecimal or 0-prefixed octal, each optionally
// negated, the forms the Wayland message definition language gives, read as the C
// wayland-scanner emits reads them, or the protocol is refused (FS9130) naming the protocol, enum, entry and text.
// An entry value is never read as 0.
// =============================================================================

let private protocolFixture (name: string) = TestHelpers.fixturePath ("protocol-values/" + name)

[<Fact>]
let ``every entry value form the protocol definition gives is read exactly`` () =
    match ProtocolParser.parseFileDiagnosed (protocolFixture "values.xml") with
    | Error refusal -> failwith refusal.Text
    | Ok protocol ->
        let values =
            ProtocolParser.toTypeDeclarations protocol |> List.pick (function
                | CppParser.Declaration.Enum e when e.Name = "fx_shape_kind" -> Some (e.Values |> List.map (fun v -> v.Name, v.Value))
                | _ -> None)
        Assert.Equal<(string * int64) list>(
            [ "zero", 0L; "one", 1L; "minus_one", -1L; "hex_zero", 0L; "upper_hex", 31L; "fourcc", 0x20203843L
              "uint32_max", 4294967295L; "int64_max_hex", System.Int64.MaxValue; "int64_max", System.Int64.MaxValue
              "octal_eight", 8L; "octal_zero", 0L; "octal_mode", 493L; "int64_max_octal", System.Int64.MaxValue
              "neg_octal", -8L; "neg_hex", -1L; "neg_zero", 0L; "neg_int_hex", -2147483647L; "neg_long_hex", -4294967296L
              "min_decimal", -System.Int64.MaxValue; "min_hex", -System.Int64.MaxValue; "min_octal", -System.Int64.MaxValue ],
            values)

[<Fact>]
let ``clang reads every admitted entry value as the parser does, and every refused negation as non-negative`` () =
    // An independent oracle: the C wayland-scanner emits is each value verbatim, so clang's
    // constant evaluation of the same text must give the parser's value; a negation the parser
    // refuses as wrapping must not be negative in C.
    let protocol =
        match ProtocolParser.parseFileDiagnosed (protocolFixture "values.xml") with
        | Ok p -> p
        | Error refusal -> failwith refusal.Text
    let admitted =
        [ for iface in protocol.Interfaces do
            for enum in iface.Enums do
                for entry in enum.Entries do
                    match ProtocolParser.entryValue entry.Value with
                    | Ok v ->
                        // C has no literal for Int64.MinValue; none is admitted.
                        yield $"({entry.Value}) == ({v}LL) && ((({entry.Value}) < 0) == ({v}LL < 0))"
                    | Error why -> failwith why ]
    let wrapped = [ "-0x80000000"; "-0x8000000000000000"; "-01000000000000000000000" ] |> List.map (fun t -> $"({t}) > 0")
    let header = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-entry-values-" + System.Guid.NewGuid().ToString("N") + ".h")
    try
        System.IO.File.WriteAllText(header, "/* entry values as wayland-scanner emits them */\n")
        TestHelpers.clangConfirms header (admitted @ wrapped)
    finally
        System.IO.File.Delete header

[<Fact>]
let ``an entry value the protocol definition does not give refuses the protocol by name`` () =
    let path = protocolFixture "malformed.xml"
    let refused entry why = $"FS9130: protocol 'fx_malformed' enum 'fx_shape.kind' entry '{entry}' has {why}; an entry value is never read as 0."
    let form text = $"value '{text}', which is not a decimal, 0x hexadecimal or 0-prefixed octal integer, optionally negated, the forms the Wayland protocol definition gives an entry value in"
    let unsignedNegation text constant = $"value '{text}', whose constant '{constant}' C types as unsigned (C11 6.4.4.1), so the negation wayland-scanner emits wraps (C11 6.2.5) instead of giving a negative value"
    let octal text = $"value '{text}', which begins with 0 as an octal integer does but has a digit outside 0-7"
    let outside text = $"value '{text}', outside the signed 64-bit range enum values carry"
    let expected =
        [ refused "suffixed" (form "1u")
          refused "octal_eight_digit" (octal "08")
          refused "octal_nine_digit" (octal "0719")
          refused "octal_wide" (outside "01000000000000000000000")
          refused "spaced" (form " 1")
          refused "wide" (outside "0xFFFFFFFFFFFFFFFF")
          refused "empty" (form "")
          refused "absent" "no value attribute, which wayland.dtd requires (<!ATTLIST entry value CDATA #REQUIRED>)"
          refused "double_sign" (form "--1")
          refused "bare_sign" (form "-")
          refused "spaced_sign" (form "- 1")
          refused "neg_octal_digit" (octal "-08")
          refused "neg_unsigned_int_hex" (unsignedNegation "-0x80000000" "0x80000000")
          refused "neg_unsigned_long_hex" (unsignedNegation "-0x8000000000000000" "0x8000000000000000")
          refused "neg_unsigned_octal" (unsignedNegation "-01000000000000000000000" "01000000000000000000000")
          refused "neg_untyped_decimal" "value '-9223372036854775808', whose constant '9223372036854775808' has no standard C type (an unsuffixed decimal constant is int, long or long long, C11 6.4.4.1), so the value wayland-scanner's C gives it is not -9223372036854775808"
          refused "neg_wide_hex" (outside "-0x10000000000000000")
          refused "binary" (form "0b1") ]
    match ProtocolParser.parseFileDiagnosed path with
    | Ok _ -> failwith "a protocol with unreadable entry values was parsed"
    | Error refusal ->
        Assert.Equal<string list>(expected, refusal.Diagnostics |> List.map Diagnostics.render)
        Assert.Equal(String.concat " " expected, refusal.Text)
        for d in refusal.Diagnostics do
            Assert.Equal(Diagnostics.inFile path "Fidelity.Data.XML keeps no line positions for protocol elements", d.Source)
            Assert.Equal(None, d.Pilot)
    // The same refusal ends a pilot run with real clang, before any binding is written.
    TestHelpers.withPilotSections [protocolFixture "native.h"] [ $"xml_protocols = [\"{path}\"]" ] ["typed_protocol = true"] [] [] [] (fun result output ->
        Assert.Equal(Error ("Failed to parse XML protocols: " + String.concat " " expected), result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``an entry of a protocol built outside the parser is refused the same way`` () =
    let protocol : ProtocolParser.Protocol =
        { Name = "fx_built"
          Interfaces =
            [ { Name = "fx_shape"; Version = 1; Requests = []; Events = []; Documentation = None
                Enums = [ { Name = "kind"; IsBitfield = false; Documentation = None
                            Entries = [ { Name = "bad"; Value = "seven"; Summary = None } ] } ] } ] }
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> ProtocolParser.toTypeDeclarations protocol |> ignore)
    Assert.Equal("FS9130: protocol 'fx_built' enum 'fx_shape.kind' entry 'bad' has value 'seven', which is not a decimal, 0x hexadecimal or 0-prefixed octal integer, optionally negated, the forms the Wayland protocol definition gives an entry value in; an entry value is never read as 0.", error.Message)

[<Fact>]
let ``C integer-constant typing follows the target data model across LP64, ILP32, LLP64 and IP16`` () =
    let repr cType family bits : TypeMapper.AbiRepr =
        { Family = family; Bits = bits; Stratum = TypeMapper.Declared; CType = cType }
    let common32 =
        [ "0", "int", TypeMapper.Signed, 32
          "2147483647", "int", TypeMapper.Signed, 32
          "-2147483647", "int", TypeMapper.Signed, 32
          "0x7FFFFFFF", "int", TypeMapper.Signed, 32
          "-0x7FFFFFFF", "int", TypeMapper.Signed, 32
          "0x80000000", "unsigned int", TypeMapper.Unsigned, 32
          "0xFFFFFFFF", "unsigned int", TypeMapper.Unsigned, 32
          "020000000000", "unsigned int", TypeMapper.Unsigned, 32 ]
    for model in [ Types.LP64; Types.ILP32; Types.LLP64 ] do
        for text, cType, family, bits in common32 do
            Assert.Equal(Ok (repr cType family bits), ProtocolParser.constantTypeFor model text)
    let ip16Constants =
        [ "0", "int", TypeMapper.Signed, 16
          "32767", "int", TypeMapper.Signed, 16
          "0x7FFF", "int", TypeMapper.Signed, 16
          "0x8000", "unsigned int", TypeMapper.Unsigned, 16
          "0xFFFF", "unsigned int", TypeMapper.Unsigned, 16
          "32768", "long", TypeMapper.Signed, 32
          "2147483647", "long", TypeMapper.Signed, 32
          "0x10000", "long", TypeMapper.Signed, 32
          "0x80000000", "unsigned long", TypeMapper.Unsigned, 32 ]
    for text, cType, family, bits in ip16Constants do
        Assert.Equal(Ok (repr cType family bits), ProtocolParser.constantTypeFor Types.IP16 text)
    let wideSigned = [ "2147483648"; "9223372036854775807"; "0x100000000"; "-0x100000000"; "0x7FFFFFFFFFFFFFFF"; "040000000000" ]
    for text in wideSigned do
        Assert.Equal(Ok (repr "long" TypeMapper.Signed 64), ProtocolParser.constantTypeFor Types.LP64 text)
        Assert.Equal(Ok (repr "long long" TypeMapper.Signed 64), ProtocolParser.constantTypeFor Types.ILP32 text)
        Assert.Equal(Ok (repr "long long" TypeMapper.Signed 64), ProtocolParser.constantTypeFor Types.LLP64 text)
        Assert.Equal(Ok (repr "long long" TypeMapper.Signed 64), ProtocolParser.constantTypeFor Types.IP16 text)
    let wideUnsigned = [ "0x8000000000000000"; "0xFFFFFFFFFFFFFFFF"; "01000000000000000000000" ]
    for text in wideUnsigned do
        Assert.Equal(Ok (repr "unsigned long" TypeMapper.Unsigned 64), ProtocolParser.constantTypeFor Types.LP64 text)
        Assert.Equal(Ok (repr "unsigned long long" TypeMapper.Unsigned 64), ProtocolParser.constantTypeFor Types.ILP32 text)
        Assert.Equal(Ok (repr "unsigned long long" TypeMapper.Unsigned 64), ProtocolParser.constantTypeFor Types.LLP64 text)
        Assert.Equal(Ok (repr "unsigned long long" TypeMapper.Unsigned 64), ProtocolParser.constantTypeFor Types.IP16 text)
    let rankOf cType =
        match cType with
        | "int" -> 1
        | "unsigned int" -> 2
        | "long" -> 3
        | "unsigned long" -> 4
        | "long long" -> 5
        | "unsigned long long" -> 6
        | other -> failwith $"Unexpected C constant type '{other}'"
    let lp64Constants =
        [ "0"; "2147483647"; "0x7FFFFFFF"; "0x80000000"; "0xFFFFFFFF"; "020000000000"
          "2147483648"; "9223372036854775807"; "0x100000000"; "0x7FFFFFFFFFFFFFFF"; "040000000000"
          "0x8000000000000000"; "0xFFFFFFFFFFFFFFFF"; "01000000000000000000000" ]
    let clangChecks =
        lp64Constants |> List.map (fun c ->
            match ProtocolParser.constantTypeFor Types.LP64 c with
            | Ok r ->
                let expectedRank = rankOf r.CType
                $"_Generic(({c}), int: 1, unsigned int: 2, long: 3, unsigned long: 4, long long: 5, unsigned long long: 6) == {expectedRank}"
            | Error why -> failwith why)
    let header = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-constant-types-" + System.Guid.NewGuid().ToString("N") + ".h")
    try
        System.IO.File.WriteAllText(header, "\n")
        TestHelpers.clangConfirms header clangChecks
    finally
        System.IO.File.Delete header

[<Fact>]
let ``entry value signedness and negation honour the selected data model`` () =
    Assert.Equal(Ok -32768L, ProtocolParser.entryValueFor Types.LP64 "-0x8000")
    Assert.Equal(Ok -32768L, ProtocolParser.entryValueFor Types.ILP32 "-0x8000")
    Assert.Equal(Ok -32768L, ProtocolParser.entryValueFor Types.LLP64 "-0x8000")
    Assert.Equal(Ok -32767L, ProtocolParser.entryValueFor Types.IP16 "-0x7FFF")
    Assert.Equal(Ok -65536L, ProtocolParser.entryValueFor Types.IP16 "-0x10000")
    Assert.Equal(
        Error "value '-0x8000', whose constant '0x8000' C types as unsigned (C11 6.4.4.1), so the negation wayland-scanner emits wraps (C11 6.2.5) instead of giving a negative value",
        ProtocolParser.entryValueFor Types.IP16 "-0x8000")
    let xmlWithNeg0x8000 = bitfieldProtocol.Replace("value=\"2\"", "value=\"-0x8000\"")
    for model in [ Types.LP64; Types.ILP32; Types.LLP64 ] do
        match ProtocolParser.parseProtocolXmlFor model xmlWithNeg0x8000 with
        | Error e -> failwith e
        | Ok proto ->
            let enumDecl =
                ProtocolParser.toTypeDeclarationsFor model proto
                |> List.pick (function CppParser.Declaration.Enum e -> Some e | _ -> None)
            Assert.Equal(-32768L, enumDecl.Values.[2].Value)
    match ProtocolParser.parseProtocolXmlFor Types.IP16 xmlWithNeg0x8000 with
    | Ok _ -> failwith "IP16 must refuse -0x8000 because 0x8000 types as 16-bit unsigned int"
    | Error refusal ->
        Assert.Equal(
            "FS9130: protocol 'test' enum 'wl_output.transform' entry 'rotate_180' has value '-0x8000', whose constant '0x8000' C types as unsigned (C11 6.4.4.1), so the negation wayland-scanner emits wraps (C11 6.2.5) instead of giving a negative value; an entry value is never read as 0.",
            refusal)
    let ip16AdmittedXml = bitfieldProtocol.Replace("value=\"1\"", "value=\"-0x7FFF\"").Replace("value=\"2\"", "value=\"-0x10000\"")
    match ProtocolParser.parseProtocolXmlFor Types.IP16 ip16AdmittedXml with
    | Error e -> failwith e
    | Ok proto ->
        let enumDecl =
            ProtocolParser.toTypeDeclarationsFor Types.IP16 proto
            |> List.pick (function CppParser.Declaration.Enum e -> Some e | _ -> None)
        Assert.Equal<(string * int64) list>(
            [ "normal", 0L; "rotate_90", -32767L; "rotate_180", -65536L ],
            enumDecl.Values |> List.map (fun v -> v.Name, v.Value))
    let xmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-ip16-protocol-" + System.Guid.NewGuid().ToString("N") + ".xml")
    try
        System.IO.File.WriteAllText(xmlPath, xmlWithNeg0x8000)
        for model in [ Types.LP64; Types.ILP32; Types.LLP64 ] do
            match ProtocolParser.parseFileDiagnosedFor model xmlPath with
            | Error refusal -> failwith refusal.Text
            | Ok proto ->
                let enumDecl =
                    ProtocolParser.toTypeDeclarationsFor model proto
                    |> List.pick (function CppParser.Declaration.Enum e -> Some e | _ -> None)
                Assert.Equal(-32768L, enumDecl.Values.[2].Value)
        match ProtocolParser.parseFileDiagnosedFor Types.IP16 xmlPath with
        | Ok _ -> failwith "IP16 must refuse -0x8000 from file"
        | Error refusal ->
            Assert.Equal(
                "FS9130: protocol 'test' enum 'wl_output.transform' entry 'rotate_180' has value '-0x8000', whose constant '0x8000' C types as unsigned (C11 6.4.4.1), so the negation wayland-scanner emits wraps (C11 6.2.5) instead of giving a negative value; an entry value is never read as 0.",
                refusal.Text)
    finally
        System.IO.File.Delete xmlPath
