module Farscape.Tests.PthreadBindingTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

let private createDecl =
    mkFunc "pthread_create" "int"
        [ "__newthread", "pthread_t *"; "__attr", "const pthread_attr_t *"
          "__start_routine", "void *(*)(void *)"; "__arg", "void *" ]

let private decls =
    [ CppParser.Declaration.Typedef (mkTypedef "pthread_t" "unsigned long")
      CppParser.Declaration.Function createDecl
      CppParser.Declaration.Function (mkFunc "pthread_join" "int" ["__th", "pthread_t"; "__thread_return", "void **"]) ]

[<Fact>]
let ``native carrier profile keeps callback type and C ABI widths`` () =
    let ctx = { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with NativePointerSurface = true }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test.Thread" "pthread" "test" []
    Assert.Contains("(start_routine: FnPtr<nativeint -> nativeint>) (arg: nativeint) : int32", source)
    Assert.Contains("(th: uint64) (thread_return: nativeint) : int32", source)
    Assert.Contains("ReturnType = Integer (Signed, 32)", source)
    Assert.Contains("Type = Integer (Unsigned, 64)", source)
    Assert.Contains("Type = Pointer 64", source)
    Assert.DoesNotContain("CHandle<", source)

[<Fact>]
let ``pthread handle storage is derived from its scalar typedef`` () =
    let ctx = { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with NativePointerSurface = true }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [List.head decls] "Test.Types" "pthread" "test" []
    Assert.Contains("module pthread_t =", source)
    Assert.Contains("let Size = 8", source)
    Assert.Contains("let Alignment = 8", source)
    Assert.Contains("width declared", source)

[<Fact>]
let ``native callback nested pointer arguments are address carriers`` () =
    let typ = FidelityCodeGenerator.mapCTypeToNativeSurface Map.empty Types.LP64 Map.empty "void *(*)(void **, void *)"
    Assert.Equal<CodeAST.FsType>(CodeAST.Generic("FnPtr", CodeAST.FunctionType(
        [CodeAST.Named "nativeint"; CodeAST.Named "nativeint"], CodeAST.Named "nativeint")), typ)

[<Fact>]
let ``native bridge binds cleaned arguments and preserves nonnull environment`` () =
    let spec : PilotTypes.CallbackSpec = {
        Registrations = [{ Function = "pthread_create"; CallbackParam = "__start_routine"; DataParam = Some "__arg" }]
        ListenerStructs = [] }
    let source = CallbackWrapperGenerator.generateNative spec decls "Test.Callbacks" Types.LP64 ["Test.Thread"] |> Option.get
    Assert.Contains("pthread_create newthread attr start_routine arg", source)
    Assert.Contains("(arg: nativeint)", source)
    Assert.Contains("FnPtr<nativeint -> nativeint>", source)
    Assert.DoesNotContain("__newthread", source)
    Assert.DoesNotContain("__attr", source)
    Assert.DoesNotContain("dlsym", source)
    Assert.DoesNotContain("0n", source)
    Assert.DoesNotContain("GCHandle", source)

[<Fact>]
let ``production pthread bridge forwards nullable opaque handles and typed entries`` () =
    let spec : PilotTypes.CallbackSpec = {
        Registrations = [{ Function = "pthread_create"; CallbackParam = "__start_routine"; DataParam = Some "__arg" }]
        ListenerStructs = [] }
    let ctx = FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty
    let source = CallbackWrapperGenerator.generateTyped spec decls "Test.Callbacks" ctx ["Test.Thread"] |> Option.get
    Assert.Contains("(newthread: option<CHandle<int>>)", source)
    Assert.Contains("(start_routine: Callback_Test_002E_Thread_003A__003A_pthread_005F_create_003A__003A__005F__005F_start_005F_routineEntry)", source)
    Assert.DoesNotContain("FnPtr<", source)
    Assert.Contains("(arg: option<CHandle<unit>>)", source)
    Assert.Contains("pthread_create newthread attr start_routine arg", source)
    Assert.DoesNotContain("nativeint", source)
    Assert.DoesNotContain("dlsym", source)
    Assert.DoesNotContain("GCHandle", source)

[<Fact>]
let ``pthread wrappers decode returned code and never inspect errno`` () =
    let source = WrapperCodeGenerator.generateWithSurface true decls "Test.Thread.Api" "pthread" "Test.Thread"
                     (WrapperTypes.UseReturnCode ("pthread", "Test.ReturnCode")) Types.LP64 None
    Assert.Contains("captureReturnCode (int (result))", source)
    Assert.DoesNotContain("captureErrno", source)
    Assert.DoesNotContain("__errno_location", source)
    Assert.DoesNotContain("open Test.ReturnCode.ReturnCode", source)
    Assert.Contains("if result = 0l then", source)

[<Fact>]
let ``libc affinity status is not decoded as a pthread error`` () =
    let fn = CppParser.Declaration.Function (mkFunc "sched_getaffinity" "int" ["pid", "int"; "size", "size_t"; "mask", "void *"])
    let source = WrapperCodeGenerator.generateWithOverrides true (Set.singleton "sched_getaffinity") [fn]
                     "Test.Affinity.Api" "c" "Test.Affinity" (WrapperTypes.UseReturnCode ("pthread", "Test.ReturnCode")) Types.LP64 None
    Assert.Contains("(mask: nativeint) : int32", source)
    Assert.Contains("Test.Affinity.sched_getaffinity pid size mask", source)
    Assert.DoesNotContain("else Error", source)

[<Fact>]
let ``native pointer profile option round trips`` () =
    let toml = "[library]\nname = \"pthread\"\nheaders = [\"pthread.h\"]\n[output]\nmode = \"fidelity\"\ndirectory = \"out\"\n[options]\nexperimental_native_pointer_surface = true\n"
    let project =
        match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail toml) with
        | Ok project -> project
        | Error e -> failwith e
    Assert.True(project.Options.Value.NativePointerSurface)
    Assert.Contains("experimental_native_pointer_surface = true", PilotSerializer.toTomlString project)

[<Fact>]
let ``documented inline callback tables round trip without losing entries`` () =
    let toml = """
[library]
name = "test"
headers = ["test.h"]
[output]
mode = "fidelity"
directory = "out"
[callbacks]
registrations = [{ function = "register", callback_param = "handler", data_param = "data" }]
listener_structs = [{ name = "listener", registration_function = "install" }]
"""
    let parse source = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail source) |> function Ok project -> project | Error e -> failwith e
    let project = parse toml
    let expected : PilotTypes.CallbackSpec = {
        Registrations = [{ Function = "register"; CallbackParam = "handler"; DataParam = Some "data" }]
        ListenerStructs = [{ Name = "listener"; RegistrationFunction = Some "install" }] }
    Assert.Equal(Some expected, project.Callbacks)
    Assert.Equal(project.Callbacks, (PilotSerializer.toTomlString project |> parse).Callbacks)

[<Fact>]
let ``Linux x64 pthread storage comes from actual header unions and measured layouts`` () =
    if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux)
       && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture = System.Runtime.InteropServices.Architecture.X64
       && File.Exists "/usr/include/pthread.h" then
        let options : CppParser.HeaderParserOptions = {
            HeaderFile = "/usr/include/pthread.h"; IncludePaths = ["/usr/include"]; Defines = ["_GNU_SOURCE"]
            IncludeRoot = Some "/usr/include"; IncludeMacros = false; MacroPrefixes = []; Verbose = false; CppMode = false }
        let declarations =
            match CppParser.parseHeader options with Ok ds -> ds | Error e -> failwith e
        let layouts =
            match CppParser.extractStructLayouts options.HeaderFile options.IncludePaths options.Defines
                      ["typedef pthread_mutex_t"; "typedef pthread_cond_t"; "typedef cpu_set_t"] false with
            | Ok layouts -> layouts | Error e -> failwith e
        for name, size in ["pthread_mutex_t", 40; "pthread_cond_t", 48; "cpu_set_t", 128] do
            let structure = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = name -> Some s | _ -> None)
            let layout = layouts.[name]
            Assert.Equal(size * 8, layout.SizeBits)
            Assert.Equal(64, layout.AlignmentBits)
            if name <> "cpu_set_t" then
                Assert.True(structure.IsUnion)
                Assert.Contains(structure.Fields, fun f -> f.IsArray && f.ArraySize = Some size)
        let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 layouts with NativePointerSurface = true }
        let contracts = declarations |> List.filter (function CppParser.Declaration.Struct s -> layouts.ContainsKey s.Name | _ -> false)
        let source = FidelityCodeGenerator.generateModule ctx Set.empty contracts "Test.Types" "pthread" "storage" []
        Assert.Contains("module pthread_mutex_t =", source)
        Assert.Contains("let Size = 40", source)
        Assert.Contains("let Size = 48", source)
        Assert.Contains("let Alignment = 8", source)
        Assert.Contains("Count = 48", source)
        Assert.DoesNotContain("type pthread_mutex_t =", source)

let private projection name symbol : PilotTypes.BindingProjection =
    { Name = name; Symbol = symbol; ReferenceParameters = []; ReadOnlyReferenceParameters = []; ReferenceElements = []; ConstantParameters = []; StringParameters = []; NonnullCallbacks = []
      ParameterHandles = []; ReturnHandle = None; Ownership = None }

[<Fact>]
let ``pthread output array declares scalar reference ABI and nonnull typed callback`` () =
    let binding = { projection "pthread_create" "pthread_create" with ReferenceParameters = [0]; NonnullCallbacks = [2]; ParameterHandles = [""; "unit"] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with
                    Bindings = Map.ofList [binding.Name, binding]
                    NonnullAnnotations = Some { Parameters = Map.ofList ["pthread_create", [3]]; Returns = Set.empty } }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test.Thread" "c" "test" []
    Assert.Contains("(newthread: int array)", source)
    Assert.Contains("(attr: option<CHandle<unit>>)", source)
    Assert.Contains("FnPtr<CHandle<unit> -> CHandle<unit>>", source)
    Assert.Contains("(arg: CHandle<unit>)", source)
    Assert.Contains("Type = Integer (Unsigned, 64); PassBy = Reference", source)
    Assert.Contains("ReturnType = Integer (Signed, 32)", source)
    Assert.DoesNotContain("nativeint", source)
    Assert.DoesNotContain("uint", source)

[<Fact>]
let ``typed malloc specialization retains native symbol and pointer ABI`` () =
    let fn = CppParser.Declaration.Function (mkFunc "mallocMutex" "void *" ["size", "size_t"])
    let binding = { projection "mallocMutex" "malloc" with ReturnHandle = Some "pthread_mutex_t"; Ownership = Some PilotTypes.CallerOwns }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Storage" "c" "test" []
    Assert.Contains("FidelityExtern(\"c\", \"malloc\")", source)
    Assert.Contains("let mallocMutex (size: int) : option<CHandle<pthread_mutex_t>>", source)
    Assert.Contains("CName = \"malloc\"", source)
    Assert.Contains("ReturnType = Pointer 64", source)
    Assert.Contains("OwnershipTransfer = CallerOwns", source)
    Assert.Contains("Ownership CallerOwns is declared by the pilot", source)

[<Fact>]
let ``reference projection carries a nullable opaque pointer output cell`` () =
    let binding = { projection "pthread_join" "pthread_join" with ReferenceParameters = [1] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test.Thread" "c" "test" []
    Assert.Contains("option<CHandle<unit>> array", source)
    Assert.Contains("Type = Pointer 64; PassBy = Reference", source)

[<Fact>]
let ``binding projections round trip through pilot TOML`` () =
    let toml = "[library]\nname = \"pthread\"\nheaders = [\"pthread.h\"]\n[output]\nmode = \"fidelity\"\ndirectory = \"out\"\n[options]\ndescriptor_only_dependencies = true\nlink_libraries = true\n[[options.bindings]]\nname = \"pthread_create\"\nreference_parameters = [0]\nnonnull_callbacks = [2]\nparameter_handles = [\"\", \"unit\"]\n[[options.bindings]]\nname = \"mallocMutex\"\nsymbol = \"malloc\"\nreturn_handle = \"pthread_mutex_t\"\nownership = \"caller_owns\"\n"
    let parse source = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail source) |> function Ok project -> project | Error e -> failwith e
    let project = parse toml
    let again = PilotSerializer.toTomlString project |> parse
    Assert.Equal<PilotTypes.ProjectOptions option>(project.Options, again.Options)
    Assert.True(again.Options.Value.DescriptorOnlyDependencies)
    Assert.True(again.Options.Value.LinkLibraries)
    Assert.Equal(2, again.Options.Value.Bindings.Length)

[<Fact>]
let ``projected wrapper signature matches the emitted extern contract`` () =
    let binding = { projection "pthread_create" "pthread_create" with ReferenceParameters = [0]; NonnullCallbacks = [2] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = WrapperCodeGenerator.generateWithContext (Some ctx) false Set.empty decls "Test.Api" "c" "Test.Thread" WrapperTypes.NoErrors Types.LP64 None
    Assert.Contains("(newthread: int array)", source)
    Assert.Contains("(start_routine: Callback_Test_002E_Thread_003A__003A_pthread_005F_create_003A__003A__005F__005F_start_005F_routineEntry)", source)
    Assert.Contains("Test.Thread.pthread_create newthread attr start_routine arg", source)

[<Fact>]
let ``free specialization declares callee ownership consumption`` () =
    let fn = CppParser.Declaration.Function (mkFunc "freeMutex" "void" ["pointer", "void *"])
    let binding = { projection "freeMutex" "free" with ParameterHandles = ["pthread_mutex_t"]; Ownership = Some PilotTypes.CalleeOwns }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Storage" "c" "test" []
    Assert.Contains("CName = \"free\"", source)
    Assert.Contains("OwnershipTransfer = CalleeOwns", source)

[<Fact>]
let ``unknown ownership spelling fails pilot parsing`` () =
    let toml = "[library]\nname = \"c\"\nheaders = [\"stdlib.h\"]\n[output]\nmode = \"fidelity\"\ndirectory = \"out\"\n[options]\n[[options.bindings]]\nname = \"malloc\"\nownership = \"maybe_owned\"\n"
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail toml) with
    | Error message -> Assert.Contains("Unknown binding ownership", message)
    | Ok _ -> failwith "Unknown ownership was silently accepted"

[<Fact>]
let ``opaque affinity count preserves C symbol and size_t return widths`` () =
    let fn = CppParser.Declaration.Function (mkFunc "cpuCount" "int" ["setsize", "size_t"; "setp", "const cpu_set_t *"])
    let binding = projection "cpuCount" "__sched_cpucount"
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with
                    Bindings = Map.ofList [binding.Name, binding]
                    NonnullAnnotations = Some { Parameters = Map.ofList ["cpuCount", [1]]; Returns = Set.empty } }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Affinity" "c" "test" []
    Assert.Contains("(setsize: int) (setp: CHandle<cpu_set_t>) : int", source)
    Assert.Contains("CName = \"__sched_cpucount\"", source)
    Assert.Contains("Type = Integer (Unsigned, 64)", source)
    Assert.Contains("ReturnType = Integer (Signed, 32)", source)

[<Fact>]
let ``explicit string projection retains pointer ABI and nullable source choice`` () =
    let fn = CppParser.Declaration.Function (mkFunc "connect" "void *" ["name", "const char *"])
    let binding = { projection "connect" "connect" with StringParameters = [0] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Display" "wayland-client" "test" []
    Assert.Contains("(name: option<string>)", source)
    Assert.Contains("Type = Pointer 64; PassBy = Value", source)

[<Fact>]
let ``byte buffer projection declares unsigned data independently of plain char`` () =
    let fn = CppParser.Declaration.Function (mkFunc "render" "void" ["pixels", "char *"])
    let binding = { projection "render" "render" with ReferenceParameters = [0]; ReferenceElements = ["uint8_t"] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Image" "resvg" "test" []
    Assert.Contains("(pixels: int array)", source)
    Assert.Contains("Type = Integer (Unsigned, 8); PassBy = Reference", source)

[<Fact>]
let ``constant profile preserves full foreign ABI behind ordinary wrapper`` () =
    let fn = CppParser.Declaration.Function (mkFunc "openRenderNode" "int" ["path", "const char *"; "flags", "int"])
    let binding = { projection "openRenderNode" "open" with StringParameters = [0]; ConstantParameters = [""; "2"] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Display" "c" "test" []
    Assert.Contains("let openRenderNodeNative (path: option<string>) (flags: int)", source)
    Assert.Contains("let openRenderNode (path: option<string>)", source)
    Assert.Contains("openRenderNodeNative path 2", source)
    Assert.Contains("let openRenderNodeNativeDescriptor", source)
    Assert.Contains("CName = \"open\"", source)

[<Fact>]
let ``constant profile rejects pointer or source expression replacement`` () =
    let fn = CppParser.Declaration.Function (mkFunc "release" "void" ["value", "void *"])
    let binding = { projection "release" "free" with ConstantParameters = ["0"] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Display" "c" "test" [] |> ignore) |> ignore

[<Fact>]
let ``const scalar buffer is a read only reference`` () =
    let fn = CppParser.Declaration.Function (mkFunc "consume" "void" ["buffer", "const unsigned char *"])
    let binding = { projection "consume" "consume" with ReferenceParameters = [0] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Buffer" "c" "test" []
    Assert.Contains("(buffer: int array)", source)
    Assert.Contains("Type = Integer (Unsigned, 8); PassBy = ReadOnlyReference", source)

[<Fact>]
let ``const deep pointee does not make its output pointer cell read only`` () =
    let fn = CppParser.Declaration.Function (mkFunc "getText" "void" ["cell", "const char **"])
    let binding = { projection "getText" "getText" with ReferenceParameters = [0] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Buffer" "c" "test" []
    Assert.Contains("Type = Pointer 64; PassBy = Reference", source)
    Assert.DoesNotContain("PassBy = ReadOnlyReference", source)

[<Fact>]
let ``explicit read only pointer cell projection remains typed`` () =
    let fn = CppParser.Declaration.Function (mkFunc "borrow" "void" ["cell", "void **"])
    let binding = { projection "borrow" "borrow" with ReadOnlyReferenceParameters = [0] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext [fn] Types.LP64 Map.empty with Bindings = Map.ofList [binding.Name, binding] }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [fn] "Test.Buffer" "c" "test" []
    Assert.Contains("(cell: option<CHandle<unit>> array)", source)
    Assert.Contains("Type = Pointer 64; PassBy = ReadOnlyReference", source)

[<Fact>]
let ``string and handle projections follow typedefs to char and data pointers`` () =
    let typedefs =
        [ CppParser.Declaration.Typedef (mkTypedef "gchar" "char")
          CppParser.Declaration.Typedef (mkTypedef "gpointer" "void *") ]
    let connect =
        CppParser.Declaration.Function
            (mkFunc "g_signal_connect_data" "unsigned long"
                [ "instance", "gpointer"; "detailed_signal", "const gchar *"; "data", "gpointer" ])
    let ref' = CppParser.Declaration.Function (mkFunc "g_object_ref" "gpointer" ["object", "gpointer"])
    let binding = { projection "g_signal_connect_data" "g_signal_connect_data" with StringParameters = [1]; ParameterHandles = ["unit"] }
    let ctx = { FidelityCodeGenerator.buildGenerationContext (typedefs @ [connect; ref']) Types.LP64 Map.empty with
                    Bindings = Map.ofList [binding.Name, binding]
                    NonnullAnnotations = Some { Parameters = Map.ofList ["g_signal_connect_data", [0; 1]]; Returns = Set.empty } }
    let source = FidelityCodeGenerator.generateModule ctx Set.empty [connect; ref'] "Test.GObject" "gobject-2.0" "test" []
    Assert.Contains("(instance: CHandle<unit>) (detailed_signal: string) (data: option<CHandle<unit>>)", source)
    Assert.Contains("let g_object_ref (object: option<CHandle<unit>>) : option<CHandle<unit>>", source)
    Assert.Contains("ReturnType = Integer (Unsigned, 64)", source)
