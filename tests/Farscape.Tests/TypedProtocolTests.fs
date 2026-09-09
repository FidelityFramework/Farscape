module Farscape.Tests.TypedProtocolTests

open System
open System.IO
open Xunit
open Farscape.Core

[<Fact>]
let ``typed protocol derives opcodes and keeps listener and object metadata native`` () =
    let toml = """
[library]
name = "wayland-client"
headers = ["wayland-client-core.h"]
[output]
mode = "fidelity"
directory = "out"
[[namespace]]
name = "Fidelity.Wayland.Protocol"
library = "wayland-client"
description = "test"
xml_interfaces = ["wl_callback"]
"""
    let project = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail toml) |> function Ok p -> p | Error e -> failwith e
    let arg: ProtocolParser.ProtocolArg = { Name="callback_data"; Type=ProtocolParser.Uint; Interface=None; Enum=None; AllowNull=false; Summary=None }
    let event: ProtocolParser.ProtocolMessage = { Name="done"; Args=[arg]; IsDestructor=false; Since=1; Documentation=None }
    let created = { arg with Name="id"; Type=ProtocolParser.NewId; Interface=Some "wl_callback" }
    let noop = { event with Name="noop"; Args=[] }
    let spawn = { event with Name="spawn"; Args=[created] }
    let iface: ProtocolParser.ProtocolInterface = { Name="wl_callback"; Version=1; Requests=[noop;spawn]; Events=[event]; Enums=[]; Documentation=None }
    let measured name bits offsets: CppParser.StructLayoutInfo = {Name=name;SizeBits=bits;DataSizeBits=bits;AlignmentBits=64;FieldOffsetsBits=offsets}
    let layouts = [measured "wl_argument" 64 [0;0;0;0;0;0;0]; measured "wl_message" 192 [0;64;128]; measured "wl_interface" 320 [0;64;96;128;192;256]] |> List.map(fun l->l.Name,l) |> Map.ofList
    let declarations =
        [ "calloc", "void *", ["n","size_t";"size","size_t"]
          "strdup", "char *", ["s","const char *"]
          "free", "void", ["p","void *"]
          "mempcpy", "void *", ["d","void *";"s","const void *";"n","size_t"]
          "wl_proxy_marshal_array_flags", "struct wl_proxy *", ["p","struct wl_proxy *";"op","uint32_t";"i","const struct wl_interface *";"v","uint32_t";"f","uint32_t";"a","union wl_argument *"]
          "wl_proxy_add_listener", "int", ["p","struct wl_proxy *";"table","void **";"data","void *"]
          "wl_proxy_get_version", "uint32_t", ["p","struct wl_proxy *"] ]
        |> List.map (fun(n,r,p)->CppParser.Declaration.Function(TestHelpers.mkFunc n r p))
    let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 layouts
    let dir = Path.Combine(Path.GetTempPath(), "farscape-protocol-"+Guid.NewGuid().ToString("N"))
    try
        let files = TypedProtocolGenerator.generate project [{Name="test";Interfaces=[iface]}] declarations ctx dir
        let callbacks = File.ReadAllText(Path.Combine(dir,"Bridge/Callbacks.clef"))
        Assert.Contains("Field = \"Done\"", callbacks)
        Assert.Contains("Type = Integer (Unsigned, 32)", callbacks)
        Assert.Contains("Named \"WlCallbackListener\"; PassBy = ReadOnlyReference", callbacks)
        let requests = File.ReadAllText(Path.Combine(dir,"Bridge/Protocol.clef"))
        Assert.Contains("wl_callback_spawnNative proxy 1 ProtocolInterfaces.wl_callback", requests)
        Assert.Contains("OwnershipTransfer = CallerOwns", requests)
        let metadata = File.ReadAllText(Path.Combine(dir,"Bridge/ProtocolInterfaces.clef"))
        Assert.Contains("copyInterfacePointer", metadata)
        Assert.Contains("let wl_callbackMethods: option<CHandle<unit>>", metadata)
        Assert.Contains("let wl_callbackMethods_noopTypes: option<CHandle<unit>> = None", metadata)
        Assert.Contains("Types = wl_callbackEvents_doneTypes", metadata)
        Assert.Contains("shutdown (); false", metadata)
        for file in files do
            let text = File.ReadAllText file
            Assert.DoesNotContain("nativeint", text)
            Assert.DoesNotContain("NativePtr", text)
    finally
        if Directory.Exists dir then Directory.Delete(dir,true)
