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
    let ``discovers single-field listener struct`` () =
        // xdg_wm_base_listener, xdg_surface_listener, wl_callback_listener etc.
        // have exactly one callback field — they must still be detected
        let decls = [
            CppParser.Declaration.Struct (
                mkStruct "xdg_surface_listener" [
                    mkField "configure" "void (*)(void *, struct xdg_surface *, uint32_t)"
                ] None)
        ]
        let spec = PilotAnalyzer.discoverCallbacks decls
        Assert.Equal(1, spec.ListenerStructs.Length)
        Assert.Equal("xdg_surface_listener", spec.ListenerStructs.[0].Name)

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
                Documentation = None
                Attributes = [] }
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
            Library = { Name = "gtk"; Headers = ["gtk.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
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
            ProtocolConfig = None
            Layer3 = None
            CppConfig = None
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
            Library = { Name = "wayland"; Headers = ["wayland-client.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
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
            ProtocolConfig = None
            Layer3 = None
            CppConfig = None
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
            Library = { Name = "gtk"; Headers = ["gtk.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; SystemIncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
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
                let spec = rt.Callbacks.Value
                Assert.Equal(1, spec.Registrations.Length)
                Assert.Equal(1, spec.ListenerStructs.Length)

// =============================================================================
// Boundary-preserving callback generation
// =============================================================================

module BoundaryGenerationTests =

    open PilotTypes
    open Farscape.Core.Types

    let private registration : CallbackRegistration =
        { Function = "register_event"; CallbackParam = "handler"; DataParam = Some "data" }
    let private spec : CallbackSpec = { Registrations = [registration]; ListenerStructs = [] }
    let private declarations =
        [ CppParser.Declaration.Function (
            mkFuncWithAttrs "register_event" "int" ["handler", "void (*)(unsigned int, void *)"; "data", "void *"] [mkAttr "NonNullAttr" [0] None]) ]
    let private render model registrations =
        let ctx = FidelityCodeGenerator.buildGenerationContext declarations model Map.empty
        CallbackWrapperGenerator.generateTypedWithModules {spec with Registrations = registrations}
            declarations "Test.Callbacks" ctx ["Test.Bindings"] (fun _ -> "Test.Bindings")
        |> Option.get

    [<Theory>]
    [<InlineData("LP64")>]
    [<InlineData("LLP64")>]
    [<InlineData("ILP32")>]
    [<InlineData("IP16")>]
    let ``typed forwarding preserves environment without exposing an address`` modelName =
        let model = match modelName with "LP64" -> LP64 | "LLP64" -> LLP64 | "ILP32" -> ILP32 | _ -> IP16
        let output = render model [registration]
        Assert.Contains("(handler: Callback_", output)
        Assert.Contains("(data: option<CHandle<unit>>)", output)
        Assert.Contains("Test.Bindings.register_event handler data", output)
        for forbidden in ["nativeint"; "nativeptr"; "NativePtr"; "dlsym"; "GCHandle"; "Marshal"; "0n"; ".Pointer"] do
            Assert.DoesNotContain(forbidden, output)

    [<Fact>]
    let ``registrations retain the owner of each callback contract`` () =
        let second = {registration with Function = "other_event"}
        let extra = CppParser.Declaration.Function (
            mkFuncWithAttrs "other_event" "int" ["handler", "void (*)(unsigned int, void *)"; "data", "void *"] [mkAttr "NonNullAttr" [0] None])
        let all = declarations @ [extra]
        let ctx = FidelityCodeGenerator.buildGenerationContext all LP64 Map.empty
        let output = CallbackWrapperGenerator.generateTypedWithModules
                         {spec with Registrations = [registration; second]} all "Test.Callbacks" ctx []
                         (fun name -> if name = "register_event" then "First.Bindings" else "Second.Bindings") |> Option.get
        Assert.Contains("First.Bindings.register_event handler data", output)
        Assert.Contains("Second.Bindings.other_event handler data", output)
        Assert.Contains("Callback_First", output)
        Assert.Contains("Callback_Second", output)

    /// The one structured refusal (#195) a registration meets.
    let private refused registrations : Diagnostics.Diagnostic =
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> render LP64 registrations |> ignore)
        match error.Refusal.Diagnostics with
        | [ d ] -> Assert.Equal(Diagnostics.render d, error.Refusal.Text); d
        | ds -> failwithf "one diagnostic expected, got %d" ds.Length

    let private registrationKey field parameter =
        Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "callbacks.registrations[register_event]." + field; Diagnostics.PilotLocation.Parameter = parameter }

    [<Fact>]
    let ``missing registration declaration is diagnosed instead of silently omitted`` () =
        let d = refused [{registration with Function = "missing"}]
        Assert.Equal(Diagnostics.BindingSymbolUndeclared, d.Code)
        Assert.Equal("Callback registration 'missing' is missing from the parsed declarations.", d.Message)
        Assert.Equal(Diagnostics.Located.Unknown "no parsed header declares C symbol 'missing'", d.Source)
        Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "callbacks.registrations[missing].function"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

    [<Theory>]
    [<InlineData("missing", "FS9164", -1, "Callback registration 'register_event' names parameter 'missing', which its native declaration does not declare.")>]
    [<InlineData("data", "FS9142", 1, "Callback registration 'register_event' requires function-pointer parameter 'data'.")>]
    let ``callback parameter must exist and be a function pointer`` name (code: string) (parameter: int) (message: string) =
        let d = refused [{registration with CallbackParam = name}]
        Assert.Equal(code, d.Code)
        Assert.Equal(message, d.Message)
        Assert.Equal(TestHelpers.constructed.Location, d.Source)
        Assert.Equal(registrationKey "callback_param" (if parameter >= 0 then Some parameter else None), d.Pilot)

    [<Theory>]
    [<InlineData("missing", "FS9164", -1, "Callback registration 'register_event' names parameter 'missing', which its native declaration does not declare.")>]
    [<InlineData("handler", "FS9165", 0, "Callback registration 'register_event' requires data-pointer environment parameter 'handler'.")>]
    let ``environment parameter must exist and be a data pointer`` name (code: string) (parameter: int) (message: string) =
        let d = refused [{registration with DataParam = Some name}]
        Assert.Equal(code, d.Code)
        Assert.Equal(message, d.Message)
        Assert.Equal(registrationKey "data_param" (if parameter >= 0 then Some parameter else None), d.Pilot)

    [<Fact>]
    let ``missing binding module cannot produce an unowned callback entry`` () =
        let ctx = FidelityCodeGenerator.buildGenerationContext declarations LP64 Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> CallbackWrapperGenerator.generateTyped spec declarations "Test.Callbacks" ctx [] |> ignore)
        Assert.Contains("requires its source binding module", error.Message)

    [<Fact>]
    let ``legacy symbol and managed delegate bridge cannot emit Clef source`` () =
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> CallbackWrapperGenerator.generate spec declarations "Test.Callbacks" LP64 [] "Test.Listener" |> ignore)
        Assert.Contains("not admitted Clef source", error.Message)

    [<Fact>]
    let ``legacy integer-address callbacks cannot emit Clef source`` () =
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> CallbackWrapperGenerator.generateNative spec declarations "Test.Callbacks" LP64 [] |> ignore)
        Assert.Contains("violate the Clef FFI boundary", error.Message)

    [<Fact>]
    let ``listener fields cannot be defaulted into foreign function addresses`` () =
        let listeners : CallbackSpec = { Registrations = []; ListenerStructs = [{ Name = "listener"; RegistrationFunction = None }] }
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> CallbackWrapperGenerator.generateL2 listeners [] "Test.Listeners" [] |> ignore)
        let d = error.Refusal.Diagnostics |> List.exactlyOne
        Assert.Equal(Diagnostics.GenericListenerUnadmitted, d.Code)
        Assert.Contains("listener struct 'listener': generic listener builders require declared native entry records and initialized fields", d.Message)
        Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "callbacks.listener_structs"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

    [<Fact>]
    let ``empty callback configuration generates no bridge`` () =
        let empty : CallbackSpec = { Registrations = []; ListenerStructs = [] }
        Assert.True((CallbackWrapperGenerator.generate empty [] "Test.Callbacks" LP64 [] "Test.Listeners").IsNone)
        Assert.True((CallbackWrapperGenerator.generateL2 empty [] "Test.Listeners" []).IsNone)
