module Farscape.Tests.CallbackTests

open Xunit
open Farscape.Core
open TestHelpers

// =============================================================================
// WrapperPatternAnalyzer: Callback Parameter Detection
// =============================================================================

module CallbackParamDetection =

    [<Fact>]
    let ``function pointer parameter classified as CallbackParam`` () =
        let func = mkFunc "g_idle_add" "unsigned int"
                    [("function", "int (*)(void *)"); ("data", "void *")]
        let roles = WrapperPatternAnalyzer.analyzeParameters func.Parameters [] Map.empty
        let (_, role) = roles |> List.find (fun (n, _) -> n = "function")
        Assert.Equal(WrapperTypes.CallbackParam, role)

    [<Fact>]
    let ``void pointer with userdata name classified as UserDataParam`` () =
        let func = mkFunc "g_idle_add" "unsigned int"
                    [("function", "int (*)(void *)"); ("data", "void *")]
        let roles = WrapperPatternAnalyzer.analyzeParameters func.Parameters [] Map.empty
        let (_, role) = roles |> List.find (fun (n, _) -> n = "data")
        match role with
        | WrapperTypes.UserDataParam cbName ->
            Assert.Equal("function", cbName)
        | other -> Assert.Fail $"Expected UserDataParam, got {other}"

    [<Fact>]
    let ``function pointer without companion userdata has no UserDataParam`` () =
        let func = mkFunc "signal" "void (*)(int)"
                    [("signum", "int"); ("handler", "void (*)(int)")]
        let roles = WrapperPatternAnalyzer.analyzeParameters func.Parameters [] Map.empty
        let hasUserData = roles |> List.exists (fun (_, role) ->
            match role with WrapperTypes.UserDataParam _ -> true | _ -> false)
        Assert.False(hasUserData)

    [<Fact>]
    let ``user_data name variant detected`` () =
        let func = mkFunc "gtk_connect" "void"
                    [("callback", "void (*)(void *)"); ("user_data", "void *")]
        let roles = WrapperPatternAnalyzer.analyzeParameters func.Parameters [] Map.empty
        let (_, role) = roles |> List.find (fun (n, _) -> n = "user_data")
        match role with
        | WrapperTypes.UserDataParam _ -> ()
        | other -> Assert.Fail $"Expected UserDataParam, got {other}"

    [<Fact>]
    let ``context name variant detected as userdata`` () =
        let func = mkFunc "event_add" "void"
                    [("handler", "void (*)(int, void *)"); ("ctx", "void *")]
        let roles = WrapperPatternAnalyzer.analyzeParameters func.Parameters [] Map.empty
        let (_, role) = roles |> List.find (fun (n, _) -> n = "ctx")
        match role with
        | WrapperTypes.UserDataParam _ -> ()
        | other -> Assert.Fail $"Expected UserDataParam, got {other}"

    [<Fact>]
    let ``non-callback void pointer not classified as UserDataParam`` () =
        // No callback param present, so void* "data" should NOT be UserDataParam
        let func = mkFunc "memcpy" "void *"
                    [("dest", "void *"); ("src", "const void *"); ("n", "size_t")]
        let roles = WrapperPatternAnalyzer.analyzeParameters func.Parameters [] Map.empty
        let hasUserData = roles |> List.exists (fun (_, role) ->
            match role with WrapperTypes.UserDataParam _ -> true | _ -> false)
        Assert.False(hasUserData)

// =============================================================================
// PilotAnalyzer: Callback Discovery
// =============================================================================

module CallbackDiscovery =

    [<Fact>]
    let ``discovers registration function with callback and userdata`` () =
        let decls = [
            CppParser.Declaration.Function (
                mkFunc "g_signal_connect_data" "unsigned long"
                    [("instance", "void *"); ("detailed_signal", "const char *");
                     ("c_handler", "void (*)(void)"); ("data", "void *");
                     ("destroy_data", "void (*)(void *, void *)"); ("connect_flags", "int")])
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        // Should not discover: has 2 callback params (c_handler and destroy_data)
        // discoverCallbacks only picks single-callback functions
        Assert.Empty(spec.Registrations)

    [<Fact>]
    let ``discovers simple registration function`` () =
        let decls = [
            CppParser.Declaration.Function (
                mkFunc "g_idle_add" "unsigned int"
                    [("function", "int (*)(void *)"); ("data", "void *")])
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Equal(1, spec.Registrations.Length)
        let reg = spec.Registrations.[0]
        Assert.Equal("g_idle_add", reg.Function)
        Assert.Equal("function", reg.CallbackParam)
        Assert.Equal(Some "data", reg.DataParam)

    [<Fact>]
    let ``discovers callback without userdata`` () =
        let decls = [
            CppParser.Declaration.Function (
                mkFunc "signal" "void (*)(int)"
                    [("signum", "int"); ("handler", "void (*)(int)")])
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Equal(1, spec.Registrations.Length)
        Assert.Equal("handler", spec.Registrations.[0].CallbackParam)
        Assert.True(spec.Registrations.[0].DataParam.IsNone)

    [<Fact>]
    let ``discovers listener struct with >50% function pointer fields`` () =
        let decls = [
            CppParser.Declaration.Struct (
                mkStruct "wl_pointer_listener" [
                    mkField "enter" "void (*)(void *, struct wl_pointer *, uint32_t, struct wl_surface *, wl_fixed_t, wl_fixed_t)"
                    mkField "leave" "void (*)(void *, struct wl_pointer *, uint32_t, struct wl_surface *)"
                    mkField "motion" "void (*)(void *, struct wl_pointer *, uint32_t, wl_fixed_t, wl_fixed_t)"
                    mkField "button" "void (*)(void *, struct wl_pointer *, uint32_t, uint32_t, uint32_t)"
                    mkField "axis" "void (*)(void *, struct wl_pointer *, uint32_t, uint32_t, wl_fixed_t)"
                ] None)
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Equal(1, spec.ListenerStructs.Length)
        Assert.Equal("wl_pointer_listener", spec.ListenerStructs.[0].Name)

    [<Fact>]
    let ``does not classify struct with few function pointers as listener`` () =
        let decls = [
            CppParser.Declaration.Struct (
                mkStruct "my_config" [
                    mkField "name" "const char *"
                    mkField "value" "int"
                    mkField "callback" "void (*)(void)"
                    mkField "timeout" "int"
                ] None)
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Empty(spec.ListenerStructs)

    [<Fact>]
    let ``finds companion add_listener function for listener struct`` () =
        let decls = [
            CppParser.Declaration.Struct (
                mkStruct "wl_pointer_listener" [
                    mkField "enter" "void (*)(void *, struct wl_pointer *)"
                    mkField "leave" "void (*)(void *, struct wl_pointer *)"
                ] None)
            CppParser.Declaration.Function (
                mkFunc "wl_pointer_add_listener" "int"
                    [("pointer", "struct wl_pointer *");
                     ("listener", "const struct wl_pointer_listener *");
                     ("data", "void *")])
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Equal(1, spec.ListenerStructs.Length)
        Assert.Equal(Some "wl_pointer_add_listener", spec.ListenerStructs.[0].RegistrationFunction)

    [<Fact>]
    let ``discovers registration via typedef function pointer`` () =
        let decls = [
            CppParser.Declaration.Typedef {
                Name = "hipStreamCallback_t"
                UnderlyingType = "void (*)(int, int, void *)"
                Documentation = None }
            CppParser.Declaration.Function (
                mkFunc "hipStreamAddCallback" "int"
                    [("stream", "void *"); ("callback", "hipStreamCallback_t");
                     ("userData", "void *"); ("flags", "unsigned int")])
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Equal(1, spec.Registrations.Length)
        let reg = spec.Registrations.[0]
        Assert.Equal("hipStreamAddCallback", reg.Function)
        Assert.Equal("callback", reg.CallbackParam)
        Assert.Equal(Some "userData", reg.DataParam)

    [<Fact>]
    let ``empty declarations produce empty CallbackSpec`` () =
        let spec = PilotAnalyzer.discoverCallbacks []
        Assert.Empty(spec.Registrations)
        Assert.Empty(spec.ListenerStructs)

// =============================================================================
// PilotSerializer: Callback TOML Round-trip
// =============================================================================

module CallbackSerializerTests =

    open PilotTypes

    [<Fact>]
    let ``callback registrations round-trip through TOML`` () =
        let project : PilotProject = {
            Library = { Name = "gtk"; Headers = ["gtk.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; TransitiveHeaders = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = None
            Options = None
            Callbacks = Some {
                Registrations = [
                    { Function = "g_idle_add"; CallbackParam = "function"; DataParam = Some "data" }
                    { Function = "signal"; CallbackParam = "handler"; DataParam = None }
                ]
                ListenerStructs = []
            }
            Nonnull = None
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("callbacks", toml)
        Assert.Contains("g_idle_add", toml)
        Assert.Contains("callback_param", toml)
        Assert.Contains("data_param", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok rt ->
                Assert.True(rt.Callbacks.IsSome)
                let spec = rt.Callbacks.Value
                Assert.Equal(2, spec.Registrations.Length)
                Assert.Equal("g_idle_add", spec.Registrations.[0].Function)
                Assert.Equal("function", spec.Registrations.[0].CallbackParam)
                Assert.Equal(Some "data", spec.Registrations.[0].DataParam)
                Assert.Equal("signal", spec.Registrations.[1].Function)
                Assert.True(spec.Registrations.[1].DataParam.IsNone)

    [<Fact>]
    let ``listener structs round-trip through TOML`` () =
        let project : PilotProject = {
            Library = { Name = "wayland"; Headers = ["wayland-client.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; TransitiveHeaders = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = None
            Options = None
            Callbacks = Some {
                Registrations = []
                ListenerStructs = [
                    { Name = "wl_pointer_listener"; RegistrationFunction = Some "wl_pointer_add_listener" }
                    { Name = "xdg_toplevel_listener"; RegistrationFunction = None }
                ]
            }
            Nonnull = None
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("listener_structs", toml)
        Assert.Contains("wl_pointer_listener", toml)
        Assert.Contains("registration_function", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok rt ->
                Assert.True(rt.Callbacks.IsSome)
                let spec = rt.Callbacks.Value
                Assert.Equal(2, spec.ListenerStructs.Length)
                Assert.Equal("wl_pointer_listener", spec.ListenerStructs.[0].Name)
                Assert.Equal(Some "wl_pointer_add_listener", spec.ListenerStructs.[0].RegistrationFunction)
                Assert.True(spec.ListenerStructs.[1].RegistrationFunction.IsNone)

    [<Fact>]
    let ``missing callbacks section deserializes as None`` () =
        let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Data.TOML.Toml.parseOrFail toml
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok project -> Assert.True(project.Callbacks.IsNone)

    [<Fact>]
    let ``full callback spec with both registrations and listeners round-trips`` () =
        let project : PilotProject = {
            Library = { Name = "gtk"; Headers = ["gtk.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; TransitiveHeaders = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = None
            Options = None
            Callbacks = Some {
                Registrations = [
                    { Function = "g_signal_connect"; CallbackParam = "handler"; DataParam = Some "data" }
                ]
                ListenerStructs = [
                    { Name = "wl_pointer_listener"; RegistrationFunction = Some "wl_pointer_add_listener" }
                ]
            }
            Nonnull = None
        }
        let toml = PilotSerializer.toTomlString project
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok rt ->
                let spec = rt.Callbacks.Value
                Assert.Equal(1, spec.Registrations.Length)
                Assert.Equal(1, spec.ListenerStructs.Length)

// =============================================================================
// CallbackWrapperGenerator: Code Generation
// =============================================================================

module CallbackWrapperGeneratorTests =

    open PilotTypes
    open Farscape.Core.Types

    [<Fact>]
    let ``generates listener struct builder with dlsym calls`` () =
        let spec : CallbackSpec = {
            Registrations = []
            ListenerStructs = [
                { Name = "wl_pointer_listener"; RegistrationFunction = Some "wl_pointer_add_listener" }
            ]
        }
        let decls = [
            CppParser.Declaration.Struct (
                mkStruct "wl_pointer_listener" [
                    mkField "enter" "void (*)(void *, struct wl_pointer *)"
                    mkField "leave" "void (*)(void *, struct wl_pointer *)"
                    mkField "motion" "void (*)(void *, struct wl_pointer *, uint32_t, wl_fixed_t, wl_fixed_t)"
                ] None)
        ]
        match CallbackWrapperGenerator.generate spec decls "Fidelity.Wayland.Callbacks" "Fidelity.Wayland" LP64 with
        | None -> Assert.Fail "Expected some generated output"
        | Some code ->
            Assert.Contains("dlsym", code)
            Assert.Contains("enterSym", code)
            Assert.Contains("leaveSym", code)
            Assert.Contains("motionSym", code)
            Assert.Contains("wl_pointer_listener", code)

    [<Fact>]
    let ``generates listener builder for delegate-typed fields`` () =
        let spec : CallbackSpec = {
            Registrations = []
            ListenerStructs = [
                { Name = "wl_pointer_listener"; RegistrationFunction = None }
            ]
        }
        let decls = [
            CppParser.Declaration.Delegate {
                Name = "WlPointerEnterHandler"; Parameters = [("data", "void *")]; ReturnType = "void"; Documentation = None }
            CppParser.Declaration.Delegate {
                Name = "WlPointerLeaveHandler"; Parameters = [("data", "void *")]; ReturnType = "void"; Documentation = None }
            CppParser.Declaration.Struct (
                mkStruct "wl_pointer_listener" [
                    mkField "enter" "WlPointerEnterHandler"
                    mkField "leave" "WlPointerLeaveHandler"
                ] None)
        ]
        match CallbackWrapperGenerator.generate spec decls "Fidelity.Wayland.Callbacks" "Fidelity.Wayland" LP64 with
        | None -> Assert.Fail "Expected some generated output"
        | Some code ->
            Assert.Contains("dlsym", code)
            Assert.Contains("enterSym", code)
            Assert.Contains("leaveSym", code)
            Assert.Contains("buildPointerListener", code)

    [<Fact>]
    let ``skips listener builder when struct not in declarations`` () =
        let spec : CallbackSpec = {
            Registrations = []
            ListenerStructs = [
                { Name = "missing_listener"; RegistrationFunction = None }
            ]
        }
        // No struct found → no decls generated → None
        let result = CallbackWrapperGenerator.generate spec [] "Fidelity.Test.Callbacks" "Fidelity.Test" LP64
        Assert.True(result.IsNone)

    [<Fact>]
    let ``empty callback spec produces None`` () =
        let spec : CallbackSpec = { Registrations = []; ListenerStructs = [] }
        let result = CallbackWrapperGenerator.generate spec [] "Ns" "Mod" LP64
        Assert.True(result.IsNone)

    [<Fact>]
    let ``generates registration wrapper with real function body`` () =
        let spec : CallbackSpec = {
            Registrations = [
                { Function = "g_idle_add"; CallbackParam = "function"; DataParam = Some "data" }
            ]
            ListenerStructs = []
        }
        let decls = [
            CppParser.Declaration.Function (
                mkFunc "g_idle_add" "unsigned int"
                    [("function", "int (*)(void *)"); ("data", "void *")])
        ]
        match CallbackWrapperGenerator.generate spec decls "Fidelity.GTK.Callbacks" "Fidelity.GTK" LP64 with
        | None -> Assert.Fail "Expected some output"
        | Some code ->
            Assert.Contains("dlsym", code)
            Assert.Contains("handlerSymbol", code)
            Assert.Contains("handler", code)
            Assert.Contains("g_idle_add", code)
            Assert.Contains("0n", code)  // userdata = 0n

    [<Fact>]
    let ``registration wrapper without userdata keeps all other params`` () =
        let spec : CallbackSpec = {
            Registrations = [
                { Function = "signal"; CallbackParam = "handler"; DataParam = None }
            ]
            ListenerStructs = []
        }
        let decls = [
            CppParser.Declaration.Function (
                mkFunc "signal" "void (*)(int)"
                    [("signum", "int"); ("handler", "void (*)(int)")])
        ]
        match CallbackWrapperGenerator.generate spec decls "Fidelity.Libc.Callbacks" "Fidelity.Libc" LP64 with
        | None -> Assert.Fail "Expected some output"
        | Some code ->
            Assert.Contains("signum", code)
            Assert.Contains("handlerSymbol", code)
            Assert.Contains("dlsym", code)

    [<Fact>]
    let ``registration wrapper skipped when function not in declarations`` () =
        let spec : CallbackSpec = {
            Registrations = [
                { Function = "missing_func"; CallbackParam = "cb"; DataParam = None }
            ]
            ListenerStructs = []
        }
        let result = CallbackWrapperGenerator.generate spec [] "Ns" "Mod" LP64
        Assert.True(result.IsNone)
