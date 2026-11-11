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
        XmlProtocols = []; Introspection = []
        IncludePaths = ["/usr/include"]
        SystemIncludePaths = []
        Defines = ["_GNU_SOURCE"]
        MacroPrefixes = []
        PkgConfig = []
        Artifact = None
    }
    Output = { Mode = "fidelity"; Directory = "./bindings" }
    Namespaces = [
        { Name = "Fidelity.libc.Memory"
          Description = "Memory operations"
          Library = "libc"
          Prefixes = ["mem"; "str"]
          Functions = []
          XmlInterfaces = []; Signals = [] }
        { Name = "Fidelity.libc.IO"
          Description = "I/O operations"
          Library = "libc"
          Prefixes = ["read"; "write"]
          Functions = ["pipe"]
          XmlInterfaces = []; Signals = [] }
    ]
    ErrorConventions = None
    Options = None
    Callbacks = None
    Nonnull = None
    ProtocolConfig = None
    Layer3 = None
    CppConfig = None
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
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = []; Artifact = None }
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
            Layer3 = None
            CppConfig = None
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
            Library = { Name = "hip"; Headers = ["/opt/rocm/include/hip/hip_runtime_api.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = []; Artifact = None }
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
            Layer3 = None
            CppConfig = None
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
            Library = { Name = "xrt"; Headers = ["xrt.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = []; Artifact = None }
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
            Layer3 = None
            CppConfig = None
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
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = []; Artifact = None }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some { Default = Errno; Overrides = Map.empty }
            Options = None
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
            Layer3 = None
            CppConfig = None
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
            Library = { Name = "stb_image"; Headers = ["/usr/include/stb/stb_image.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = []; Artifact = None }
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
            Layer3 = None
            CppConfig = None
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

let private resultContract = "{ identity = \"posix.read\", version = \"2024\", source = \"/contracts/read.pilot.toml\", lower = { constant = -1 }, upper = { argument = 2 } }"
let private resultPilot contract =
    PilotSerializer.toTomlString sampleProject + "\n[[options.bindings]]\nname = \"read\"\n" + (if contract = "absent" then "" else "result_range = " + contract + "\n")

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``result range profile roundtrips explicit provenance and absent stays absent`` absent =
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok project -> project | Error error -> failwith error
    let project = read (resultPilot (if absent then "absent" else resultContract))
    let range = project.Options.Value.Bindings.Head.ResultRange
    if absent then Assert.True(range.IsNone) else
        let expected : PilotTypes.BindingResultRange =
            { Identity = "posix.read"; Version = "2024"; Source = "/contracts/read.pilot.toml"
              Lower = PilotTypes.ResultRangeBound.Constant -1; Upper = PilotTypes.ResultRangeBound.Argument 2 }
        Assert.Equal(Some expected, range)
    Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("identity", "requires nonempty identity")>]
[<InlineData("version", "requires nonempty version")>]
[<InlineData("source", "requires nonempty source")>]
[<InlineData("lower", "requires lower")>]
[<InlineData("both", "exactly one constant or argument integer")>]
[<InlineData("negative", "argument requires a nonnegative index")>]
[<InlineData("overflow", "within int range")>]
[<InlineData("type", "exactly one constant or argument integer")>]
let ``result range profile refuses partial or invalid explicit contracts`` shape reason =
    let contract =
        match shape with
        | "identity" -> resultContract.Replace("identity = \"posix.read\", ", "")
        | "version" -> resultContract.Replace("version = \"2024\"", "version = \" \"")
        | "source" -> resultContract.Replace("source = \"/contracts/read.pilot.toml\", ", "")
        | "lower" -> resultContract.Replace("lower = { constant = -1 }, ", "")
        | "both" -> resultContract.Replace("{ argument = 2 }", "{ argument = 2, constant = 4 }")
        | "negative" -> resultContract.Replace("argument = 2", "argument = -1")
        | "overflow" -> resultContract.Replace("argument = 2", "argument = 2147483648")
        | _ -> resultContract.Replace("argument = 2", "argument = \"2\"")
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (resultPilot contract)) with
    | Error error -> Assert.Contains("result_range", error); Assert.Contains(reason, error)
    | Ok _ -> failwith "Invalid explicit result contract was accepted"

let private invocationProfile = "{ parameter = 0, identity = \"fixture.callback\", version = \"v1\", source = \"/contracts/call_twice.c\", scope = \"during_call\" }"
let private invocationPilot value =
    PilotSerializer.toTomlString sampleProject + "\n[[options.bindings]]\nname = \"call_twice\"\nnonnull_callbacks = [0]\n" + (if value = "absent" then "" else "callback_invocations = " + value + "\n")

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``callback invocation profile roundtrips explicit provenance and missing stays absent`` absent =
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    let value = read (invocationPilot (if absent then "absent" else "[" + invocationProfile + "]"))
    let binding = value.Options.Value.Bindings.Head
    Assert.Equal<int list>([0], binding.NonnullCallbacks)
    if absent then Assert.Empty(binding.CallbackInvocations) else
        let expected: PilotTypes.BindingCallbackInvocation = {Parameter = 0; Identity = "fixture.callback"; Version = "v1"; Source = "/contracts/call_twice.c"; Scope = "during_call"; ExitBinding = None; SuccessStatus = None; AbsenceHypothesis = None; Context = None; CountedArrays = []; Retention = None}
        Assert.Equal<PilotTypes.BindingCallbackInvocation list>([expected], binding.CallbackInvocations)
    Assert.Equal(value, read (PilotSerializer.toTomlString value))

[<Fact>]
let ``callback context, counted arrays and retention roundtrip`` () =
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    let contract =
        invocationProfile.Replace("during_call", "retained").Replace(" }",
            ", context = { argument = 0, parameter = 3 }, counted_arrays = [ { argument = 2, count_argument = 1, element = \"nullable_text\" }, { argument = 3, count_argument = 1, element = \"handle\" } ]"
            + ", retention = { owner_parameter = 0, redefined_by = [0, 1], releases = [ { binding = \"Test.close\", parameter = 0 } ] } }")
    let project = read (invocationPilot ("[" + contract + "]"))
    let actual = project.Options.Value.Bindings.Head.CallbackInvocations.Head
    let context : PilotTypes.BindingCallbackContext = { Argument = 0; Parameter = 3 }
    Assert.Equal(Some context, actual.Context)
    let arrays : PilotTypes.BindingCountedArray list =
        [ { Argument = 2; CountArgument = 1; Element = PilotTypes.CountedArrayElement.NullableText }
          { Argument = 3; CountArgument = 1; Element = PilotTypes.CountedArrayElement.Handle } ]
    Assert.Equal<PilotTypes.BindingCountedArray list>(arrays, actual.CountedArrays)
    let release : PilotTypes.BindingRetentionRelease = { Binding = "Test.close"; Parameter = 0 }
    let retention : PilotTypes.BindingCallbackRetention = { OwnerParameter = 0; RedefinedBy = [ 0; 1 ]; Releases = [ release ] }
    Assert.Equal(Some retention, actual.Retention)
    Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("retention = { owner_parameter = 0, releases = [] }", "during_call", "FS9137", "callback_invocations.retention", "callback_invocations during_call forbids retention")>]
[<InlineData("counted_arrays = [ { argument = 2, count_argument = 1, element = \"strings\" } ]", "during_call", "FS9201", "callback_invocations.counted_arrays.element", "callback_invocations counted_arrays element must be text, nullable_text, handle or nullable_handle")>]
[<InlineData("counted_arrays = [ { argument = 2, count_argument = 1, element = \"text\" }, { argument = 2, count_argument = 1, element = \"text\" } ]", "during_call", "FS9202", "callback_invocations.counted_arrays", "callback_invocations counted_arrays declares one argument more than once")>]
[<InlineData("context = { argument = 0 }", "during_call", "FS9198", "callback_invocations.context.parameter", "callback_invocations context requires a nonnegative parameter within int range")>]
[<InlineData("retention = { owner_parameter = 0, redefined_by = [0, 0] }", "retained", "FS9202", "callback_invocations.retention.redefined_by", "callback_invocations retention redefined_by names one parameter more than once")>]
let ``callback context, counted arrays and retention refuse malformed pilot input`` (field: string) (scope: string) (code: string) (key: string) (message: string) =
    let contract = invocationProfile.Replace("during_call", scope).Replace(" }", ", " + field + " }")
    match PilotSerializer.deserializeDiagnosed (Fidelity.Data.TOML.Toml.parseOrFail (invocationPilot ("[" + contract + "]"))) with
    | Error refusal ->
        let d = refusal.Diagnostics |> List.exactlyOne
        Assert.Equal(code, d.Code)
        Assert.Equal(message, d.Message)
        Assert.Equal(Some ("options.bindings[call_twice]." + key), d.Pilot |> Option.map _.Key)
    | Ok _ -> failwith "malformed callback contract input was accepted"

[<Fact>]
let ``callback absence hypothesis roundtrips separately from activation`` () =
    let hypothesis = "{ identity = \"fixture.absence\", version = \"absence-v2\", source = \"/contracts/absence.c\" }"
    let contract = invocationProfile.Replace(" }", ", absence_hypothesis = " + hypothesis + " }")
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    let project = read (invocationPilot ("[" + contract + "]"))
    let actual = project.Options.Value.Bindings.Head.CallbackInvocations.Head
    let expected : PilotTypes.BindingCallbackAbsenceHypothesis = {Identity = "fixture.absence"; Version = "absence-v2"; Source = "/contracts/absence.c"}
    Assert.Equal(Some expected, actual.AbsenceHypothesis)
    Assert.Equal("fixture.callback", actual.Identity)
    Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("{}", "requires nonempty identity")>]
[<InlineData("{ identity = \"absent\" }", "requires nonempty version")>]
[<InlineData("{ identity = \"absent\", version = \"v1\", source = \" \" }", "requires nonempty source")>]
[<InlineData("{ identity = \"absent\", version = \"v1\", source = \"contract\", extra = true }", "has unknown fields")>]
[<InlineData("true", "requires a table")>]
let ``callback absence hypothesis refuses malformed explicit provenance`` hypothesis reason =
    let contract = invocationProfile.Replace(" }", ", absence_hypothesis = " + hypothesis + " }")
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (invocationPilot ("[" + contract + "]"))) with
    | Error error -> Assert.Contains("callback_invocations absence_hypothesis " + reason, error)
    | Ok _ -> failwith "Malformed callback absence hypothesis was admitted"

[<Theory>]
[<InlineData("identity", "requires nonempty identity")>]
[<InlineData("version", "requires nonempty version")>]
[<InlineData("source", "requires nonempty source")>]
[<InlineData("scope", "requires nonempty scope")>]
[<InlineData("unknown-scope", "scope must be during_call, retained or process_exit")>]
[<InlineData("negative", "nonnegative parameter index")>]
[<InlineData("overflow", "nonnegative parameter index")>]
[<InlineData("duplicate", "duplicate parameter indices")>]
[<InlineData("array", "requires an array")>]
[<InlineData("boolean", "unknown fields")>]
let ``callback invocation profile refuses incomplete and unsupported contracts`` shape reason =
    let contract =
        match shape with
        | "identity" -> invocationProfile.Replace("identity = \"fixture.callback\", ", "")
        | "version" -> invocationProfile.Replace("version = \"v1\"", "version = \" \"")
        | "source" -> invocationProfile.Replace("source = \"/contracts/call_twice.c\", ", "")
        | "scope" -> invocationProfile.Replace(", scope = \"during_call\"", "")
        | "unknown-scope" -> invocationProfile.Replace("during_call", "forever")
        | "negative" -> invocationProfile.Replace("parameter = 0", "parameter = -1")
        | "overflow" -> invocationProfile.Replace("parameter = 0", "parameter = 2147483648")
        | "boolean" -> invocationProfile.Replace(" }", ", accepts_absence = true }")
        | _ -> invocationProfile
    let input = match shape with "duplicate" -> "[" + contract + "," + contract + "]" | "array" -> contract | _ -> "[" + contract + "]"
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (invocationPilot input)) with
    | Error error -> Assert.Contains("callback_invocations", error); Assert.Contains(reason, error)
    | Ok _ -> failwith "Unsupported callback invocation profile was admitted"

[<Theory>]
[<InlineData(false, 0)>]
[<InlineData(true, -2147483648)>]
let ``process exit invocation pilot roundtrips optional anchor and signed32 status`` anchored status =
    let contract = invocationProfile.Replace("during_call", "process_exit").Replace(" }", ", success_status = " + string status + (if anchored then ", exit_binding = \"Test.Process.exit\"" else "") + " }")
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    let project = read (invocationPilot ("[" + contract + "]"))
    let actual = project.Options.Value.Bindings.Head.CallbackInvocations.Head
    Assert.Equal(Some status, actual.SuccessStatus)
    Assert.Equal((if anchored then Some "Test.Process.exit" else None), actual.ExitBinding)
    Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("missing", "requires signed32 success_status")>]
[<InlineData("overflow", "requires signed32 success_status")>]
[<InlineData("string", "requires signed32 success_status")>]
[<InlineData("anchor", "requires nonempty exit_binding")>]
[<InlineData("mixed", "during_call forbids")>]
[<InlineData("unknown", "unknown fields")>]
let ``process exit invocation pilot rejects malformed and mixed fields`` shape reason =
    let extras =
        match shape with
        | "missing" -> ""
        | "overflow" -> ", success_status = 2147483648"
        | "string" -> ", success_status = \"0\""
        | "anchor" -> ", success_status = 0, exit_binding = \" \""
        | "unknown" -> ", success_status = 0, unexpected = true"
        | _ -> ", success_status = 0"
    let contract = invocationProfile.Replace("during_call", (if shape = "mixed" then "during_call" else "process_exit")).Replace(" }", extras + " }")
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (invocationPilot ("[" + contract + "]"))) with
    | Error error -> Assert.Contains(reason, error)
    | Ok _ -> failwith "Invalid process-exit invocation pilot was accepted"

let private resourceProfile = "{ kind = \"owned_nullable_return\", release_binding = \"Test.Storage.freeFirst\", release_parameter = 0, hypothesis = { identity = \"first-owned\", version = \"1\", source = \"owned.pilot.toml\" } }"
let private resourcePilot value =
    PilotSerializer.toTomlString sampleProject + "\n[[options.bindings]]\nname = \"mallocFirst\"\nsymbol = \"malloc\"\n" + (if value = "absent" then "" else "resource = " + value + "\n")

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``owned nullable resource pilot roundtrips exact provenance and absence`` absent =
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    let project = read (resourcePilot (if absent then "absent" else resourceProfile))
    let resource = project.Options.Value.Bindings.Head.Resource
    if absent then Assert.True(resource.IsNone) else
        let expected: PilotTypes.BindingResource =
            { ReleaseBinding = "Test.Storage.freeFirst"; ReleaseParameter = 0; Identity = "first-owned"; Version = "1"; Source = "owned.pilot.toml" }
        Assert.Equal(Some expected, resource)
    Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("kind", "kind must be owned_nullable_return")>]
[<InlineData("missing-kind", "requires nonempty kind")>]
[<InlineData("release", "requires nonempty release_binding")>]
[<InlineData("symbol", "requires a qualified release_binding")>]
[<InlineData("ordinal", "requires release_parameter 0")>]
[<InlineData("missing-ordinal", "requires release_parameter 0")>]
[<InlineData("hypothesis", "requires hypothesis")>]
[<InlineData("identity", "requires nonempty identity")>]
[<InlineData("version", "requires nonempty version")>]
[<InlineData("source", "requires nonempty source")>]
[<InlineData("extra", "contract has unknown fields")>]
[<InlineData("hypothesis-extra", "hypothesis has unknown fields")>]
let ``owned nullable resource pilot refuses partial unknown and unsupported protocols`` shape reason =
    let contract =
        match shape with
        | "kind" -> resourceProfile.Replace("owned_nullable_return", "borrowed")
        | "missing-kind" -> resourceProfile.Replace("kind = \"owned_nullable_return\", ", "")
        | "release" -> resourceProfile.Replace("release_binding = \"Test.Storage.freeFirst\", ", "")
        | "symbol" -> resourceProfile.Replace("Test.Storage.freeFirst", "free")
        | "ordinal" -> resourceProfile.Replace("release_parameter = 0", "release_parameter = 1")
        | "missing-ordinal" -> resourceProfile.Replace("release_parameter = 0, ", "")
        | "hypothesis" -> resourceProfile.Replace(", hypothesis = { identity = \"first-owned\", version = \"1\", source = \"owned.pilot.toml\" }", "")
        | "identity" -> resourceProfile.Replace("identity = \"first-owned\", ", "")
        | "version" -> resourceProfile.Replace("version = \"1\"", "version = \" \"")
        | "source" -> resourceProfile.Replace(", source = \"owned.pilot.toml\"", "")
        | "extra" -> resourceProfile.Replace("kind =", "unexpected = true, kind =")
        | _ -> resourceProfile.Replace("identity =", "unexpected = true, identity =")
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (resourcePilot contract)) with
    | Error error -> Assert.Contains("resource", error); Assert.Contains(reason, error)
    | Ok _ -> failwith "Unsupported resource declaration was accepted"

let private writableProfile = "{ parameter = 1, count_parameter = 2, hypothesis = { identity = \"posix.read.borrow\", version = \"2024\", source = \"read-contract.pilot.toml\" } }"
let private writablePilot value =
    PilotSerializer.toTomlString sampleProject + "\n[[options.bindings]]\nname = \"read\"\nreference_parameters = [1]\nreference_elements = [\"\", \"unsigned char\"]\n" + (if value = "absent" then "" else "writable_buffers = " + value + "\n")

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``writable buffer pilot roundtrips explicit byte borrow and absence`` absent =
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    let project = read (writablePilot (if absent then "absent" else "[" + writableProfile + "]"))
    let binding = project.Options.Value.Bindings.Head
    if absent then Assert.Empty(binding.WritableBuffers) else
        let expected: PilotTypes.BindingWritableBuffer =
            { Parameter = 1; CountParameter = 2; Identity = "posix.read.borrow"; Version = "2024"; Source = "read-contract.pilot.toml" }
        Assert.Equal<PilotTypes.BindingWritableBuffer list>([expected], binding.WritableBuffers)
    Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("parameter", "nonnegative parameter")>]
[<InlineData("count", "nonnegative count_parameter")>]
[<InlineData("overflow", "nonnegative count_parameter")>]
[<InlineData("same", "distinct buffer and count parameters")>]
[<InlineData("hypothesis", "requires hypothesis")>]
[<InlineData("identity", "requires nonempty identity")>]
[<InlineData("version", "requires nonempty version")>]
[<InlineData("source", "requires nonempty source")>]
[<InlineData("extra", "contract has unknown fields")>]
[<InlineData("hypothesis-extra", "hypothesis has unknown fields")>]
[<InlineData("duplicate", "duplicate parameter indices")>]
[<InlineData("array", "requires an array")>]
let ``writable buffer pilot refuses malformed ambiguous and unknown contracts`` shape reason =
    let contract =
        match shape with
        | "parameter" -> writableProfile.Replace("parameter = 1", "parameter = -1")
        | "count" -> writableProfile.Replace("count_parameter = 2, ", "")
        | "overflow" -> writableProfile.Replace("count_parameter = 2", "count_parameter = 2147483648")
        | "same" -> writableProfile.Replace("count_parameter = 2", "count_parameter = 1")
        | "hypothesis" -> "{ parameter = 1, count_parameter = 2 }"
        | "identity" -> writableProfile.Replace("identity = \"posix.read.borrow\", ", "")
        | "version" -> writableProfile.Replace("version = \"2024\"", "version = \" \"")
        | "source" -> writableProfile.Replace(", source = \"read-contract.pilot.toml\"", "")
        | "extra" -> writableProfile.Replace("count_parameter =", "scope = \"retained\", count_parameter =")
        | "hypothesis-extra" -> writableProfile.Replace("identity =", "encoding = \"utf8\", identity =")
        | _ -> writableProfile
    let value = match shape with "duplicate" -> "[" + contract + "," + contract + "]" | "array" -> contract | _ -> "[" + contract + "]"
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail (writablePilot value)) with
    | Error error -> Assert.Contains("writable_buffers", error); Assert.Contains(reason, error)
    | Ok _ -> failwith "Invalid writable buffer contract was admitted"
