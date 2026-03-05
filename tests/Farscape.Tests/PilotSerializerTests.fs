module Farscape.Tests.PilotSerializerTests

open Xunit
open Farscape.Core

// =============================================================================
// PilotSerializer Tests: TOML round-trip and deserialization
// =============================================================================

let private sampleProject : PilotTypes.PilotProject = {
    Library = {
        Name = "libc"
        Headers = ["/usr/include/string.h"]
        XmlProtocols = []
        IncludePaths = ["/usr/include"]
        Defines = ["_GNU_SOURCE"]
        MacroPrefixes = []
        PkgConfig = []
    }
    Output = { Mode = "fidelity"; Directory = "./bindings" }
    Namespaces = [
        { Name = "Fidelity.libc.Memory"
          Description = "Memory operations"
          Library = "libc"
          Prefixes = ["mem"; "str"]
          Functions = []
          XmlInterfaces = [] }
        { Name = "Fidelity.libc.IO"
          Description = "I/O operations"
          Library = "libc"
          Prefixes = ["read"; "write"]
          Functions = ["pipe"]
          XmlInterfaces = [] }
    ]
    ErrorConventions = None
    Options = None
    Callbacks = None
    Nonnull = None
    ProtocolConfig = None
}

[<Fact>]
let ``serialize produces valid TOML with all sections`` () =
    let toml = PilotSerializer.toTomlString sampleProject
    Assert.Contains("name = \"libc\"", toml)
    Assert.Contains("header = \"/usr/include/string.h\"", toml)
    Assert.Contains("mode = \"fidelity\"", toml)
    Assert.Contains("directory = \"./bindings\"", toml)
    Assert.Contains("[[namespace]]", toml)
    Assert.Contains("Fidelity.libc.Memory", toml)
    Assert.Contains("Fidelity.libc.IO", toml)

[<Fact>]
let ``round-trip serialize then deserialize produces identical project`` () =
    let toml = PilotSerializer.toTomlString sampleProject
    match Fidelity.Data.TOML.Toml.parse toml with
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Ok roundTripped ->
            Assert.Equal(sampleProject.Library.Name, roundTripped.Library.Name)
            Assert.Equal(sampleProject.Library.Header, roundTripped.Library.Header)
            Assert.Equal<string list>(sampleProject.Library.IncludePaths, roundTripped.Library.IncludePaths)
            Assert.Equal<string list>(sampleProject.Library.Defines, roundTripped.Library.Defines)
            Assert.Equal(sampleProject.Output.Mode, roundTripped.Output.Mode)
            Assert.Equal(sampleProject.Output.Directory, roundTripped.Output.Directory)
            Assert.Equal(sampleProject.Namespaces.Length, roundTripped.Namespaces.Length)
            Assert.Equal(sampleProject.Namespaces[0].Name, roundTripped.Namespaces[0].Name)
            Assert.Equal<string list>(sampleProject.Namespaces[0].Prefixes, roundTripped.Namespaces[0].Prefixes)
            Assert.Equal<string list>(sampleProject.Namespaces[1].Functions, roundTripped.Namespaces[1].Functions)
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
    | Error e -> Assert.Fail $"Parse failed: {e}"

[<Fact>]
let ``deserialize returns Error for missing library section`` () =
    let doc = Fidelity.Data.TOML.Toml.parseOrFail "[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    match PilotSerializer.deserialize doc with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should return Error for missing [library]"

[<Fact>]
let ``deserialize returns Error for missing output section`` () =
    let doc = Fidelity.Data.TOML.Toml.parseOrFail "[library]\nname = \"test\"\nheader = \"test.h\""
    match PilotSerializer.deserialize doc with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should return Error for missing [output]"

[<Fact>]
let ``deserialize handles empty namespace array`` () =
    let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok project -> Assert.Empty(project.Namespaces)
    | Error e -> Assert.Fail $"Should succeed with no namespaces: {e}"

[<Fact>]
let ``deserialize handles optional functions field`` () =
    let toml = """
[library]
name = "test"
header = "test.h"
[output]
mode = "fidelity"
directory = "./out"
[[namespace]]
name = "Test.Str"
description = "String ops"
library = "test"
prefixes = ["str"]
"""
    let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok project ->
        Assert.Equal(1, project.Namespaces.Length)
        Assert.Empty(project.Namespaces[0].Functions)
    | Error e -> Assert.Fail $"Should succeed: {e}"

[<Fact>]
let ``loadFromFile returns Error for nonexistent file`` () =
    match PilotSerializer.loadFromFile "/nonexistent/path.pilot.toml" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should return Error for missing file"

[<Fact>]
let ``multi-header project serializes with headers array`` () =
    let project = { sampleProject with
                      Library = { sampleProject.Library with
                                    Headers = ["/usr/include/unistd.h"; "/usr/include/fcntl.h"] } }
    let toml = PilotSerializer.toTomlString project
    Assert.Contains("headers", toml)
    Assert.Contains("/usr/include/unistd.h", toml)
    Assert.Contains("/usr/include/fcntl.h", toml)

[<Fact>]
let ``multi-header round-trip preserves all headers`` () =
    let project = { sampleProject with
                      Library = { sampleProject.Library with
                                    Headers = ["/usr/include/unistd.h"; "/usr/include/fcntl.h"] } }
    let toml = PilotSerializer.toTomlString project
    match Fidelity.Data.TOML.Toml.parse toml with
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Ok roundTripped ->
            Assert.Equal<string list>(project.Library.Headers, roundTripped.Library.Headers)
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
    | Error e -> Assert.Fail $"Parse failed: {e}"

[<Fact>]
let ``single header backward compat still works`` () =
    let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok project -> Assert.Equal<string list>(["test.h"], project.Library.Headers)
    | Error e -> Assert.Fail $"Should parse single header: {e}"

[<Fact>]
let ``deserialize rejects empty headers array`` () =
    let toml = "[library]\nname = \"test\"\nheaders = []\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should reject empty headers"

// =============================================================================
// Error Convention TOML Tests
// =============================================================================

module ErrorConventionTomlTests =

    open PilotTypes

    [<Fact>]
    let ``error conventions round-trip through TOML`` () =
        let project : PilotProject = {
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some {
                Default = Errno
                Overrides = Map.ofList [("pthread_create", ReturnCode); ("strtol", NoErrorConvention)]
            }
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("error_conventions", toml)
        Assert.Contains("errno", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok roundTripped ->
                Assert.True(roundTripped.ErrorConventions.IsSome)
                let spec = roundTripped.ErrorConventions.Value
                Assert.Equal(Errno, spec.Default)
                Assert.Equal(ReturnCode, spec.Overrides.["pthread_create"])
                Assert.Equal(NoErrorConvention, spec.Overrides.["strtol"])

    [<Fact>]
    let ``missing error_conventions deserializes as None`` () =
        let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok project -> Assert.True(project.ErrorConventions.IsNone)

    [<Fact>]
    let ``enum error code convention round-trips through TOML`` () =
        let project : PilotProject = {
            Library = { Name = "hip"; Headers = ["/opt/rocm/include/hip/hip_runtime_api.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some {
                Default = EnumErrorCode ("hipError_t", "hipSuccess", Some "hipGetErrorString", Some "hipGetErrorName")
                Overrides = Map.empty
            }
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("enum_error_code", toml)
        Assert.Contains("error_type", toml)
        Assert.Contains("hipError_t", toml)
        Assert.Contains("success_value", toml)
        Assert.Contains("hipSuccess", toml)
        Assert.Contains("error_string_fn", toml)
        Assert.Contains("error_name_fn", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok roundTripped ->
                Assert.True(roundTripped.ErrorConventions.IsSome)
                let spec = roundTripped.ErrorConventions.Value
                match spec.Default with
                | EnumErrorCode (et, sv, esf, enf) ->
                    Assert.Equal("hipError_t", et)
                    Assert.Equal("hipSuccess", sv)
                    Assert.Equal(Some "hipGetErrorString", esf)
                    Assert.Equal(Some "hipGetErrorName", enf)
                | other -> Assert.Fail $"Expected EnumErrorCode, got {other}"

    [<Fact>]
    let ``enum error code with only required fields round-trips`` () =
        let project : PilotProject = {
            Library = { Name = "xrt"; Headers = ["xrt.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some {
                Default = EnumErrorCode ("xrt_error_code", "XRT_SUCCESS", None, None)
                Overrides = Map.empty
            }
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
        }
        let toml = PilotSerializer.toTomlString project
        // Should NOT contain optional fn fields
        Assert.DoesNotContain("error_string_fn", toml)
        Assert.DoesNotContain("error_name_fn", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok roundTripped ->
                match roundTripped.ErrorConventions.Value.Default with
                | EnumErrorCode (et, sv, esf, enf) ->
                    Assert.Equal("xrt_error_code", et)
                    Assert.Equal("XRT_SUCCESS", sv)
                    Assert.True(esf.IsNone)
                    Assert.True(enf.IsNone)
                | other -> Assert.Fail $"Expected EnumErrorCode, got {other}"

    [<Fact>]
    let ``error conventions with no overrides`` () =
        let project : PilotProject = {
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some { Default = Errno; Overrides = Map.empty }
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
        }
        let toml = PilotSerializer.toTomlString project
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok rt ->
                Assert.True(rt.ErrorConventions.IsSome)
                Assert.Equal(Errno, rt.ErrorConventions.Value.Default)
                Assert.True(rt.ErrorConventions.Value.Overrides.IsEmpty)

    [<Fact>]
    let ``null_with_reason convention round-trips through TOML`` () =
        let project : PilotProject = {
            Library = { Name = "stb_image"; Headers = ["/usr/include/stb/stb_image.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some {
                Default = NullWithReason "stbi_failure_reason"
                Overrides = Map.empty
            }
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("null_with_reason", toml)
        Assert.Contains("reason_function", toml)
        Assert.Contains("stbi_failure_reason", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok roundTripped ->
                Assert.True(roundTripped.ErrorConventions.IsSome)
                match roundTripped.ErrorConventions.Value.Default with
                | NullWithReason reasonFn ->
                    Assert.Equal("stbi_failure_reason", reasonFn)
                | other -> Assert.Fail $"Expected NullWithReason, got {other}"

// ─── Nonnull Annotations Tests ──────────────────────────────────────

[<Fact>]
let ``nonnull annotations round-trip through TOML`` () =
    let project = { sampleProject with
                        Nonnull = Some {
                            Parameters = Map.ofList [("render", [0; 2]); ("init", [0])]
                            Returns = Set.ofList ["create_ctx"]
                        } }
    let toml = PilotSerializer.toTomlString project
    Assert.Contains("annotations.nonnull", toml)
    Assert.Contains("nonnull_returns", toml)
    Assert.Contains("create_ctx", toml)
    match Fidelity.Data.TOML.Toml.parse toml with
    | Error e -> Assert.Fail $"Parse failed: {e}"
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok roundTripped ->
            Assert.True(roundTripped.Nonnull.IsSome)
            let nonnull = roundTripped.Nonnull.Value
            Assert.Equal<int list>([0; 2], nonnull.Parameters.["render"])
            Assert.Equal<int list>([0], nonnull.Parameters.["init"])
            Assert.True(nonnull.Returns.Contains "create_ctx")

[<Fact>]
let ``absent nonnull section deserializes as None`` () =
    let project = { sampleProject with Nonnull = None }
    let toml = PilotSerializer.toTomlString project
    Assert.DoesNotContain("annotations.nonnull", toml)
    match Fidelity.Data.TOML.Toml.parse toml with
    | Error e -> Assert.Fail $"Parse failed: {e}"
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok roundTripped ->
            Assert.True(roundTripped.Nonnull.IsNone)

// ─── Protocol Config Tests ─────────────────────────────────────────

[<Fact>]
let ``protocol config round-trips through TOML`` () =
    let project = { sampleProject with
                        ProtocolConfig = Some {
                            MarshalFunction = "wl_proxy_marshal_array_flags"
                            MarshalModule = "Fidelity.Wayland.Core"
                            VersionFunction = "wl_proxy_get_version"
                            InterfaceResolution = "dlsym"
                            DestroyFlag = 1u
                        } }
    let toml = PilotSerializer.toTomlString project
    Assert.Contains("marshal_function", toml)
    Assert.Contains("wl_proxy_marshal_array_flags", toml)
    match Fidelity.Data.TOML.Toml.parse toml with
    | Error e -> Assert.Fail $"Parse failed: {e}"
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok roundTripped ->
            Assert.True(roundTripped.ProtocolConfig.IsSome)
            let cfg = roundTripped.ProtocolConfig.Value
            Assert.Equal("wl_proxy_marshal_array_flags", cfg.MarshalFunction)
            Assert.Equal("Fidelity.Wayland.Core", cfg.MarshalModule)
            Assert.Equal("wl_proxy_get_version", cfg.VersionFunction)
            Assert.Equal("dlsym", cfg.InterfaceResolution)
            Assert.Equal(1u, cfg.DestroyFlag)

[<Fact>]
let ``absent protocol section deserializes as None`` () =
    let project = { sampleProject with ProtocolConfig = None }
    let toml = PilotSerializer.toTomlString project
    Assert.DoesNotContain("protocol", toml)
    match Fidelity.Data.TOML.Toml.parse toml with
    | Error e -> Assert.Fail $"Parse failed: {e}"
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok roundTripped ->
            Assert.True(roundTripped.ProtocolConfig.IsNone)
