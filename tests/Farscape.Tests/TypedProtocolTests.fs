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
    let arg: ProtocolParser.ProtocolArg = { Name="callback_data"; Type=ProtocolParser.Uint; Interface=None; Enum=None; TypeAttribute=Some "uint"; AllowNull=false; Summary=None }
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
    let dir = TestHelpers.TestScratch.path "typed-existing"
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

let private nativeHeader = """
typedef unsigned long size_t;
typedef unsigned int uint32_t;
typedef int int32_t;
struct wl_proxy;
struct wl_interface;
union wl_argument { int32_t i; uint32_t u; void *o; };
struct wl_message { const char *name; const char *signature; const struct wl_interface **types; };
struct wl_interface { const char *name; int version; int method_count; const struct wl_message *methods; int event_count; const struct wl_message *events; };
void *calloc(size_t n, size_t size);
char *strdup(const char *s);
void free(void *p);
void *mempcpy(void *d, const void *s, size_t n);
struct wl_proxy *wl_proxy_marshal_array_flags(struct wl_proxy *p, uint32_t op, const struct wl_interface *i, uint32_t v, uint32_t f, union wl_argument *a);
int wl_proxy_add_listener(struct wl_proxy *p, void **table, void *data);
uint32_t wl_proxy_get_version(struct wl_proxy *p);
"""

[<Theory>]
[<InlineData("positive")>]
[<InlineData("missing-native")>]
[<InlineData("constructor-string")>]
[<InlineData("constructor-string-errno")>]
let ``XML-only whole project validates complete protocol before changing output`` shape =
  let directory = TestHelpers.TestScratch.path "typed-project"
  try
    Directory.CreateDirectory directory |> ignore
    let header = Path.Combine(directory, "native.h")
    let declarations = if shape = "missing-native" then nativeHeader.Replace("char *strdup(const char *s);", "") else nativeHeader
    File.WriteAllText(header, declarations)
    let xml = Path.Combine(directory, "protocol.xml")
    let extra = if shape.StartsWith "constructor-string" then "<arg name=\"title\" type=\"string\"/>" else ""
    File.WriteAllText(xml, "<protocol name=\"test\"><interface name=\"wl_callback\" version=\"1\"><request name=\"noop\"/><request name=\"spawn\"><arg name=\"id\" type=\"new_id\" interface=\"wl_callback\"/>"+extra+"</request><event name=\"done\"><arg name=\"callback_data\" type=\"uint\"/></event></interface></protocol>")
    let output = Path.Combine(directory, "out")
    let project: PilotTypes.PilotProject =
      { Library = { Name="wayland-client"; Headers=[header]; XmlProtocols=[xml]; Introspection=[]; IncludePaths=[directory]; SystemIncludePaths=[]; Defines=[]; MacroPrefixes=[]; PkgConfig=[]; Artifact=None }
        Output = {Mode="fidelity"; Directory=output}
        Namespaces = [{Name="Fidelity.Wayland.Protocol"; Description="Whole XML project"; Library="wayland-client"; Prefixes=[]; Functions=[]; XmlInterfaces=["wl_callback"]; Signals=[]}]
        ErrorConventions=(if shape = "constructor-string-errno" then Some {Default=PilotTypes.Errno; Overrides=Map.empty} else None)
        Callbacks=None; Nonnull=None; ProtocolConfig=None; Layer3=None; CppConfig=None
        Options=Some {AbiCriticalStructs=["union wl_argument"; "struct wl_message"; "struct wl_interface"]; GenerateDescriptors=true; NativePointerSurface=false; CHeaderMode=true; Bindings=[]; DescriptorOnlyDependencies=true; ValueStructs=[]; TypedProtocol=true; LinkLibraries=false; MappedReturns=[]; FlexibleArrays=[]; AsyncWrappers=[]} }
    let pilot = Path.Combine(directory, "xml.pilot.toml")
    PilotSerializer.saveToFile pilot project |> function Ok () -> () | Error error -> failwith error
    Directory.CreateDirectory output |> ignore
    let sentinel = Path.Combine(output, "Types.clef")
    File.WriteAllText(sentinel, "// Retained XML bindings\n")
    match BindingGenerator.generateFromProject pilot false false Types.LP64 with
    | Ok result when shape = "positive" ->
      let callbacks = File.ReadAllText(Path.Combine(output, "Bridge/Callbacks.clef"))
      Assert.Contains("Field = \"Done\"", callbacks)
      Assert.Contains("wl_callback_spawnNative", File.ReadAllText(Path.Combine(output, "Bridge/Protocol.clef")))
      Assert.Contains(Path.Combine(output, "Bridge/NativeStorage.clef"), result.OutputFiles)
      Assert.Contains(Path.Combine(output, "Bridge/ProtocolInterfaces.clef"), result.OutputFiles)
      Assert.NotEqual<string>("// Retained XML bindings\n", File.ReadAllText sentinel)
    | Ok _ -> failwith "Unsupported XML projection must refuse before changing output"
    | Error error when shape <> "positive" ->
      let reason = if shape = "missing-native" then "Typed protocol requires parsed C declaration 'strdup'" else "Typed constructor string arguments need an explicit ownership profile"
      Assert.Contains(reason, error)
      Assert.Equal("// Retained XML bindings\n", File.ReadAllText sentinel)
      Assert.Equal<string array>([|sentinel|], Directory.GetFiles(output, "*", SearchOption.AllDirectories))
    | Error error -> failwith error
  finally
    if Directory.Exists directory then Directory.Delete(directory, true)

// Typed protocol refusals are structured (#195): each names [options] typed_protocol and is
// located at the native declaration it concerns, or carries why no position is known.
[<Theory>]
[<InlineData("missing-native", "FS9159", "Typed protocol requires parsed C declaration 'strdup'")>]
[<InlineData("signature", "FS9174", "Typed protocol C signature 'free' does not match the declared LP64 projection")>]
[<InlineData("unmeasured", "FS9175", "Typed protocol requires measured wl_argument")>]
[<InlineData("layout", "FS9176", "Typed Wayland requires 64-bit measured wl_argument cells")>]
[<InlineData("constructor-string", "FS9177", "Typed constructor string arguments need an explicit ownership profile ('wl_callback_spawn')")>]
let ``each typed protocol refusal carries its code, its location and the typed_protocol key`` shape (code: string) (message: string) =
  let directory = TestHelpers.TestScratch.path "typed-diagnostics"
  try
    Directory.CreateDirectory directory |> ignore
    let header = Path.Combine(directory, "native.h")
    let source =
        match shape with
        | "missing-native" -> nativeHeader.Replace("char *strdup(const char *s);", "")
        | "signature" -> nativeHeader.Replace("void free(void *p);", "int free(void *p);")
        | "layout" -> nativeHeader.Replace("union wl_argument { int32_t i; uint32_t u; void *o; };", "union wl_argument { int32_t i; uint32_t u; };")
        | _ -> nativeHeader
    File.WriteAllText(header, source)
    let xml = Path.Combine(directory, "protocol.xml")
    let extra = if shape = "constructor-string" then "<arg name=\"title\" type=\"string\"/>" else ""
    File.WriteAllText(xml, "<protocol name=\"test\"><interface name=\"wl_callback\" version=\"1\"><request name=\"noop\"/><request name=\"spawn\"><arg name=\"id\" type=\"new_id\" interface=\"wl_callback\"/>"+extra+"</request><event name=\"done\"><arg name=\"callback_data\" type=\"uint\"/></event></interface></protocol>")
    let output = Path.Combine(directory, "out")
    let critical = if shape = "unmeasured" then ["struct wl_message"; "struct wl_interface"] else ["union wl_argument"; "struct wl_message"; "struct wl_interface"]
    let project: PilotTypes.PilotProject =
      { Library = { Name="wayland-client"; Headers=[header]; XmlProtocols=[xml]; Introspection=[]; IncludePaths=[directory]; SystemIncludePaths=[]; Defines=[]; MacroPrefixes=[]; PkgConfig=[]; Artifact=None }
        Output = {Mode="fidelity"; Directory=output}
        Namespaces = [{Name="Fidelity.Wayland.Protocol"; Description="Whole XML project"; Library="wayland-client"; Prefixes=[]; Functions=[]; XmlInterfaces=["wl_callback"]; Signals=[]}]
        ErrorConventions=None; Callbacks=None; Nonnull=None; ProtocolConfig=None; Layer3=None; CppConfig=None
        Options=Some {AbiCriticalStructs=critical; GenerateDescriptors=true; NativePointerSurface=false; CHeaderMode=true; Bindings=[]; DescriptorOnlyDependencies=true; ValueStructs=[]; TypedProtocol=true; LinkLibraries=false; MappedReturns=[]; FlexibleArrays=[]; AsyncWrappers=[]} }
    let pilot = Path.Combine(directory, "xml.pilot.toml")
    PilotSerializer.saveToFile pilot project |> function Ok () -> () | Error error -> failwith error
    match BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64 with
    | Ok _ -> failwith "the typed protocol generated; a refusal was expected"
    | Error refusal ->
      let d = refusal.Diagnostics |> List.exactlyOne
      Assert.Equal(code, d.Code)
      Assert.Equal(message, d.Message)
      Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = Path.GetFullPath pilot; Diagnostics.PilotLocation.Key = "options.typed_protocol"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
      match shape, d.Source with
      | "signature", Diagnostics.Located.Known { Path = path; Position = Diagnostics.Located.Known p } ->
          Assert.Equal(header, path)
          Assert.Equal(1 + (source.Split('\n') |> Array.findIndex (fun l -> l.StartsWith "int free(")), p.Line)
      | "signature", other -> failwithf "free's clang location expected, got %A" other
      | ("unmeasured" | "layout"), Diagnostics.Located.Known { Path = path } -> Assert.Equal(header, path)
      | ("unmeasured" | "layout"), other -> failwithf "wl_argument's clang location expected, got %A" other
      | _, Diagnostics.Located.Unknown _ -> ()
      | _, other -> failwithf "no position is known for %s, got %A" shape other
  finally
    if Directory.Exists directory then Directory.Delete(directory, true)

[<Fact>]
let ``the typed protocol projection under another profile than LP64 is refused at the typed_protocol key`` () =
  // FS9173: on an LP64 host a whole ILP32 project is refused earlier by measurement, so the
  // profile check is exercised at the generator itself.
  let project : PilotTypes.PilotProject =
    { Library = { Name="wayland-client"; Headers=[]; XmlProtocols=[]; Introspection=[]; IncludePaths=[]; SystemIncludePaths=[]; Defines=[]; MacroPrefixes=[]; PkgConfig=[]; Artifact=None }
      Output = {Mode="fidelity"; Directory="out"}
      Namespaces = []; ErrorConventions=None; Callbacks=None; Nonnull=None; ProtocolConfig=None; Layer3=None; CppConfig=None; Options=None }
  let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.ILP32 Map.empty
  let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> TypedProtocolGenerator.render project [] [] ctx "out" |> ignore)
  let d = error.Refusal.Diagnostics |> List.exactlyOne
  Assert.Equal(Diagnostics.TypedProtocolProfile, d.Code)
  Assert.Equal("Typed Wayland currently requires the measured LP64 host profile", d.Message)
  Assert.Equal(Diagnostics.Located.Unknown "the profile is generation input, not a header declaration", d.Source)
  Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "options.typed_protocol"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
