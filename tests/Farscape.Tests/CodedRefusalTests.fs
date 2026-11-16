module Farscape.Tests.CodedRefusalTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// The remaining non-callback generator and pilot refusals carry their own codes (#199): the
// retired native-pointer surface, value-struct shape, C++ class ABI, pilot TOML shape,
// introspection loading and signal selection, and abi_critical_structs.
// ═══════════════════════════════════════════════════════════════════

let private only (refusal: Diagnostics.Refusal) =
    match refusal.Diagnostics with
    | [ d ] -> d
    | ds -> failwithf "one diagnostic expected, got %d: %s" ds.Length refusal.Text

/// Generate a pilot through `generateFromProjectDiagnosed` with extra [library] and [options]
/// lines and namespace lines; return the pilot path and the structured refusal.
let private refusedPilot (headers: string list) (library: string list) (options: string list) (functions: string list) (ns: string list) =
    let mutable captured = None
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> Result.mapError _.Text)
        headers library options functions ns [] (fun _ output ->
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories)))
    match captured with
    | Some (project, Error refusal) -> project, refusal
    | Some (_, Ok _) -> failwith "the pilot generated; a refusal was expected"
    | None -> failwith "the pilot was not generated"

let private pilotKey (project: string) (key: string) =
    Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = key; Diagnostics.PilotLocation.Parameter = None }

// ─── The retired experimental native-pointer surface (FS9182) ───

[<Fact>]
let ``the experimental native-pointer surface is refused at its pilot key`` () =
    let project, refusal = refusedPilot [ fixturePath "macros/composed.h" ] [] [ "experimental_native_pointer_surface = true" ] [ "fx_bind_text" ] []
    let d = only refusal
    Assert.Equal(Diagnostics.NativePointerSurfaceRetired, d.Code)
    Assert.Equal("experimental_native_pointer_surface cannot emit Clef source: raw addresses and width-named carriers violate the FFI boundary contract. Remove this option and declare CHandle, callback, reference and ownership projections in the pilot.", d.Message)
    Assert.Equal(pilotKey project "options.experimental_native_pointer_surface", d.Pilot)
    Assert.Equal("FS9182: " + d.Message + " No bindings were written.", refusal.Text)

[<Fact>]
let ``generation with the native-pointer surface context is refused with the same code`` () =
    let func = mkFunc "fx_read" "int" [ "fd", "int" ]
    let ctx = { FidelityCodeGenerator.buildGenerationContext [ CppParser.Declaration.Function func ] Types.LP64 Map.empty with NativePointerSurface = true }
    let moduleError = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        FidelityCodeGenerator.generateModule ctx Set.empty [ CppParser.Declaration.Function func ] "Test" "test" "test" [] |> ignore)
    Assert.Equal(Diagnostics.NativePointerSurfaceRetired, (only moduleError.Refusal).Code)
    let functionError = Assert.Throws<Diagnostics.RefusalException>(fun () -> FidelityCodeGenerator.generateFunctionDecls ctx "test" func |> ignore)
    let d = only functionError.Refusal
    Assert.Equal(Diagnostics.NativePointerSurfaceRetired, d.Code)
    Assert.Equal(Some "options.experimental_native_pointer_surface", d.Pilot |> Option.map _.Key)

// ─── Value structs (FS9183) and C++ classes (FS9184) ───

[<Fact>]
let ``a value struct that is a union is refused at the record and the value_structs key`` () =
    let header = fixturePath "by-value/union-result.h"
    let project, refusal = refusedPilot [ header ] [] [ "value_structs = [\"fx_word\"]" ] [ "fx_get_word" ] []
    let d = only refusal
    Assert.Equal(Diagnostics.ValueStructUnrepresentable, d.Code)
    Assert.Equal("Value struct 'fx_word' is a union; a plain record with scalar or pointer fields is required.", d.Message)
    Assert.Equal(pilotKey project "options.value_structs", d.Pilot)
    match d.Source with
    | Diagnostics.Located.Known { Path = path; Position = Diagnostics.Located.Known _ } -> Assert.Equal(header, path)
    | other -> failwithf "the union's clang location is missing: %A" other

[<Fact>]
let ``a value struct with an array field is refused naming the field`` () =
    let s = mkStruct "Packet" [ { mkField "bytes" "unsigned char" with IsArray = true; ArraySize = Some 4 } ] None
    let decls = [ CppParser.Declaration.Struct s ]
    let ctx = { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with ValueStructs = Set.singleton "Packet" }
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> FidelityCodeGenerator.generateModule ctx Set.empty decls "Test" "test" "test" [] |> ignore)
    let d = only error.Refusal
    Assert.Equal(Diagnostics.ValueStructUnrepresentable, d.Code)
    Assert.Equal("Value struct 'Packet' field 'bytes' is an array; a plain record with scalar or pointer fields is required.", d.Message)
    Assert.Equal(Some "options.value_structs", d.Pilot |> Option.map _.Key)

[<Fact>]
let ``each C++ class shape without an admitted ABI contract is refused with its own code before any output is written`` () =
    let cases : (CppParser.ClassDecl * string * string) list = [
        mkClassPod "Widget" [ mkField "x" "int" ],
        Diagnostics.CppClassAbiUnadmitted,
        "C++ POD class 'Widget' (4 bytes) requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
        mkClassPimpl "Device" "device_impl",
        Diagnostics.CppPimplClassUnadmitted,
        "C++ pimpl class 'Device' (handle 'std::shared_ptr<device_impl>', 16 bytes) requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
        mkClassValue "Uuid" [ mkField "data" "uint8_t" ],
        Diagnostics.CppValueClassUnadmitted,
        "C++ value class 'Uuid' (1 bytes) requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
        mkClassAbstract "IPlugin",
        Diagnostics.CppInterfaceClassUnadmitted,
        "C++ abstract interface class 'IPlugin' requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
        mkClassEmpty "ForwardDeclared",
        Diagnostics.CppOpaqueClassUnadmitted,
        "C++ opaque class 'ForwardDeclared' requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source."
    ]
    for cls, expectedCode, expectedMessage in cases do
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            FidelityCodeGenerator.generate [ CppParser.Declaration.Class cls ] "Test" "test" Types.LP64 Map.empty |> ignore)
        let d = Assert.Single(error.Refusal.Diagnostics)
        Assert.Equal(expectedCode, d.Code)
        Assert.Equal(expectedMessage, d.Message)
    // Pilot generation refuses a C++ header before any output is written.
    let header = fixturePath "anonymous-members/class.hpp"
    let _, refusal = refusedPilot [ header ] [] [] [ "fx_cpp_am_read" ] []
    let d = Assert.Single(refusal.Diagnostics)
    Assert.Equal(Diagnostics.CppClassAbiUnadmitted, d.Code)
    Assert.Equal("C++ POD class 'fx_cpp_am' (8 bytes) requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source.", d.Message)
    match d.Source with
    | Diagnostics.Located.Known { Path = path; Position = Diagnostics.Located.Known _ } -> Assert.Equal(header, path)
    | other -> failwithf "the C++ class's clang location is missing: %A" other

// ─── Marshaled C++ object lifecycle (#115) ───

let private cppObjectContract () =
    let noexcept = [ mkAttr "NoExceptAttr" [] None ]
    let ctor = { mkFuncWithAttrs "Widget" "void" [ "value", "int" ] noexcept with MangledName = Some "_ZN6WidgetC1Ei" }
    let method' = { mkFuncWithAttrs "read" "int" [] noexcept with MangledName = Some "_ZN6Widget4readEv" }
    let cls =
        { mkClassValue "Widget" [ mkField "value" "int" ] with
            Constructors = [ CppParser.Declaration.Function ctor ]
            Methods = [ CppParser.Declaration.Function method' ]
            DestructorMangledName = Some "_ZN6WidgetD1Ev"
            Measured = Some (mkLayout "Widget" 32 32 [ 0 ]) }
    let spec : PilotTypes.CppClassSpec =
        { Name = "Widget"; Namespace = "Test"; Bind = true; KindOverride = None; SizeOverride = None }
    spec, cls

[<Fact>]
let ``marshaled C++ object use after D1 release is refused with FS9217`` () =
    let spec, cls = cppObjectContract ()
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.verifyCppObjectLifecycle spec cls [
            CppClassAnalysis.CppObjectOperation.Construct "object"
            CppClassAnalysis.CppObjectOperation.Release "object"
            CppClassAnalysis.CppObjectOperation.MethodCall ("object", "_ZN6Widget4readEv") ])
    let d = only error.Refusal
    Assert.Equal("FS9217", d.Code)
    Assert.Equal(Diagnostics.CppObjectUseAfterRelease, d.Code)
    Assert.Equal("C++ class 'Widget': handle 'object' is used after its D1 release.", d.Message)

[<Fact>]
let ``marshaled C++ object double D1 release is refused with FS9218`` () =
    let spec, cls = cppObjectContract ()
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.verifyCppObjectLifecycle spec cls [
            CppClassAnalysis.CppObjectOperation.Construct "object"
            CppClassAnalysis.CppObjectOperation.Release "object"
            CppClassAnalysis.CppObjectOperation.Release "object" ])
    let d = only error.Refusal
    Assert.Equal("FS9218", d.Code)
    Assert.Equal(Diagnostics.CppObjectDoubleRelease, d.Code)
    Assert.Equal("C++ class 'Widget': handle 'object' receives its D1 release more than once.", d.Message)

[<Fact>]
let ``marshaled C++ object paths require cleanup of every live identity at every exit`` () =
    let spec, cls = cppObjectContract ()
    let verify = CppClassAnalysis.verifyCppObjectLifecycle spec cls
    let construct = CppClassAnalysis.CppObjectOperation.Construct
    let release = CppClassAnalysis.CppObjectOperation.Release
    let exit = CppClassAnalysis.CppObjectOperation.ScopeExit
    verify [ exit ] // Nullable construction produced no object.
    verify [ construct "a"; construct "b"; release "a"
             CppClassAnalysis.CppObjectOperation.MethodCall ("b", "_ZN6Widget4readEv")
             release "b"; exit ]
    for path in [ [ construct "a"; exit ]; [ construct "a" ]; [ construct "a"; construct "b"; release "a"; exit ] ] do
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> verify path)
        Assert.Equal(Diagnostics.CppValueClassUnadmitted, (only error.Refusal).Code)

[<Fact>]
let ``admission cannot override unsupported C++ shapes or throwing methods`` () =
    let spec, cls = cppObjectContract ()
    let method' = cls.Methods |> List.pick (function CppParser.Declaration.Function f -> Some f | _ -> None)
    let cases = [
        { cls with IsAbstract = true }, Diagnostics.CppInterfaceClassUnadmitted
        { cls with Methods = [ CppParser.Declaration.Function { method' with IsVirtual = true } ] }, Diagnostics.CppInterfaceClassUnadmitted
        mkClassEmpty "Widget", Diagnostics.CppOpaqueClassUnadmitted
        { cls with BaseClasses = [ "Other" ] }, Diagnostics.CppValueClassUnadmitted
        { cls with DestructorMangledName = Some "_ZN6WidgetD2Ev" }, Diagnostics.CppValueClassUnadmitted
        { cls with Constructors = [] }, Diagnostics.CppValueClassUnadmitted
        { cls with Methods = [ CppParser.Declaration.Function { method' with Attributes = [] } ] }, Diagnostics.CppValueClassUnadmitted
        { cls with HasUserDestructor = false; HasUserCopyConstructor = false; Methods = [ CppParser.Declaration.Function { method' with Attributes = [] } ] }, Diagnostics.CppClassAbiUnadmitted
        { mkClassPimpl "Widget" "impl" with
            Constructors = cls.Constructors
            DestructorMangledName = cls.DestructorMangledName
            Methods = [ CppParser.Declaration.Function { method' with Attributes = [] } ] }, Diagnostics.CppPimplClassUnadmitted ]
    for shape, code in cases do
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.verifyCppObjectLifecycle { spec with KindOverride = Some "pod" } shape [])
        Assert.Equal(code, (only error.Refusal).Code)
        let decls = [ CppParser.Declaration.Class shape ]
        let ctx =
            { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with
                CppClasses = Map.ofList [ spec.Name, spec ] }
        let generation = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            FidelityCodeGenerator.generateModule ctx Set.empty decls "Test" "test" "test" [] |> ignore)
        Assert.Equal(code, (only generation.Refusal).Code)

[<Fact>]
let ``nontrivial class pointers return in registers and by-value objects use sret`` () =
    let _, cls = cppObjectContract ()
    let classes = Map.ofList [ cls.Name, cls ]
    for result in [ "Widget *"; "const Widget *"; "Widget &" ] do
        let info = CppClassAnalysis.analyzeMethodReturn classes (mkFunc "get" result [])
        Assert.Equal(CppClassAnalysis.RegisterReturn, info.ReturnConvention)
        Assert.Equal(None, info.SretTypeName)
    let info = CppClassAnalysis.analyzeMethodReturn classes (mkFunc "get" "Widget" [])
    Assert.Equal(CppClassAnalysis.SretReturn, info.ReturnConvention)
    Assert.Equal(Some "Widget", info.SretTypeName)

[<Fact>]
let ``admitted C++ constructors and methods refuse unrepresentable parameters before emission`` () =
    let spec, cls = cppObjectContract ()
    let invalidParameter = function
        | CppParser.Declaration.Function f -> CppParser.Declaration.Function { f with Parameters = [ "value", "unsupported_template<int>" ] }
        | declaration -> declaration
    for shape in [ { cls with Constructors = cls.Constructors |> List.map invalidParameter }
                   { cls with Methods = cls.Methods |> List.map invalidParameter } ] do
        let declarations = [ CppParser.Declaration.Class shape ]
        let ctx =
            { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
                CppClasses = Map.ofList [ spec.Name, spec ] }
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            FidelityCodeGenerator.generateModule ctx Set.empty declarations "Test" "test" "test" [] |> ignore)
        Assert.Equal(Diagnostics.CppValueClassUnadmitted, (only error.Refusal).Code)

[<Fact>]
let ``admitted C++ constructor overloads own the same qualified release binding`` () =
    let spec, cls = cppObjectContract ()
    let ctor = cls.Constructors |> List.pick (function CppParser.Declaration.Function f -> Some f | _ -> None)
    let shape = { cls with Constructors = cls.Constructors @ [ CppParser.Declaration.Function { ctor with Parameters = []; MangledName = Some "_ZN6WidgetC1Ev" } ] }
    let declarations = [ CppParser.Declaration.Class shape ]
    let ctx =
        { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
            CppClasses = Map.ofList [ spec.Name, spec ] }
    let code = FidelityCodeGenerator.generateModule ctx Set.empty declarations "Test" "test" "test" []
    Assert.Contains("let Widget_ctor_1", code)
    Assert.Contains("let Widget_ctor_1ResourceDescriptor", code)
    Assert.Equal(2, code.Split("Binding = \"Test.Widget_dtor\"").Length - 1)
    Assert.DoesNotContain("nativeint", code)
    Assert.DoesNotContain("nativeptr", code)

[<Fact>]
let ``admitted C++ class emits marshaled bindings and its storage adapter runs native C1 methods and D1`` () =
    let directory = TestHelpers.TestScratch.create "pilot-fixture-tests"
    Directory.CreateDirectory directory |> ignore
    let run executable arguments =
        let info = System.Diagnostics.ProcessStartInfo(executable)
        info.WorkingDirectory <- directory
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in arguments do info.ArgumentList.Add argument
        use proc = System.Diagnostics.Process.Start info
        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        Assert.True(proc.ExitCode = 0, executable + ": " + stderr.Result + stdout.Result)
        stdout.Result
    try
        let header = Path.Combine(directory, "counter.hpp")
        let headerSource =
            String.concat "\n" [
                "#pragma once"
                "namespace demo {"
                "class Pair { public: int first; int second; };"
                "class Result { public: int value; explicit Result(int v) noexcept; ~Result() noexcept; };"
                "class Counter {"
                "  int value;"
                "public:"
                "  explicit Counter(int v) noexcept;"
                "  ~Counter() noexcept;"
                "  int add(int amount) noexcept;"
                "  Pair pair() noexcept;"
                "  Result result() noexcept;"
                "};"
                "}"
                "extern \"C\" int counter_destructions() noexcept;"
                "extern \"C\" int result_destructions() noexcept;"
            ]
        File.WriteAllText(header, headerSource)
        let implementation = Path.Combine(directory, "counter.cpp")
        let implSource =
            String.concat "\n" [
                "#include \"counter.hpp\""
                "static int counter_releases = 0, result_releases = 0;"
                "demo::Counter::Counter(int v) noexcept : value(v) {}"
                "demo::Counter::~Counter() noexcept { ++counter_releases; }"
                "int demo::Counter::add(int amount) noexcept { return value += amount; }"
                "demo::Pair demo::Counter::pair() noexcept { return {value, value + 1}; }"
                "demo::Result demo::Counter::result() noexcept { return demo::Result(value + 2); }"
                "demo::Result::Result(int v) noexcept : value(v) {}"
                "demo::Result::~Result() noexcept { ++result_releases; }"
                "extern \"C\" int counter_destructions() noexcept { return counter_releases; }"
                "extern \"C\" int result_destructions() noexcept { return result_releases; }"
            ]
        File.WriteAllText(implementation, implSource)
        let parsed =
            CppParser.parseHeader { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false }
            |> Result.defaultWith failwith
        let classes = parsed |> List.choose (function CppParser.Declaration.Class c -> Some c | _ -> None)
        let measured =
            CppParser.extractStructLayoutsForModeDiagnosed header [] [] [] [ "demo::Counter"; "demo::Pair"; "demo::Result" ] true false
            |> Result.defaultWith (fun refusal -> failwith refusal.Text)
        let classes = classes |> List.map (fun c ->
            let key = c.QualifiedName |> Option.defaultValue c.Name
            { c with Measured = measured.TryFind key })
        let counter = classes |> List.find (fun c -> c.Name = "Counter")
        Assert.True(counter.Measured.IsSome, "clang must measure the actual class layout")
        let spec : PilotTypes.CppClassSpec =
            { Name = "demo::Counter"; Namespace = "Test.Cpp"; Bind = true; SizeOverride = None; KindOverride = None }
        let ctx =
            { FidelityCodeGenerator.buildGenerationContext (classes |> List.map CppParser.Declaration.Class) Types.LP64 Map.empty with
                CppClasses = Map.ofList [ spec.Name, spec; "Counter", spec ] }
        let code = FidelityCodeGenerator.generateModule ctx Set.empty [ CppParser.Declaration.Class counter ] "Test.Cpp" "counter" "C++ fixture" []
        Assert.DoesNotContain("nativeint", code)
        Assert.DoesNotContain("nativeptr", code)
        for expected in [ "type demo_Counter"; "module demo_Counter ="; "let Size : int = 4"; "let Alignment : int = 4"
                          "let demo_Counter_ctor (v: int) : option<CHandle<demo_Counter>>"
                          "let demo_Counter_ctorResourceDescriptor : Expr<ResourceDescriptor>"
                          "OwnedNullableReturn"; "Binding = \"Test.Cpp.demo_Counter_dtor\""; "Parameter = 0"
                          "OwnershipTransfer = CallerOwns"; "OwnershipTransfer = CalleeOwns"
                          "let demo_Counter_dtor (self: CHandle<demo_Counter>) : unit"
                          "let demo_Counter_pair (self: CHandle<demo_Counter>) : demo_PairValue"
                          "let demo_Counter_result (retStorage: CHandle<demo_Result>) (self: CHandle<demo_Counter>) : unit"
                          "type demo_Result"; "type demo_PairValue"; "let demo_Counter_pairDescriptor" ] do
            Assert.Contains(expected, code)
        let constructors = counter.Constructors |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        Assert.NotEmpty constructors
        for f in constructors do
            Assert.Contains("C1", f.MangledName.Value)
            Assert.Contains("FidelityExtern(\"counter\", \"" + f.MangledName.Value + "\")", code)
        Assert.Contains("D1", counter.DestructorMangledName.Value)
        Assert.Contains(counter.DestructorMangledName.Value, code)
        for f in counter.Methods |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None) do
            Assert.Contains(f.MangledName.Value, code)
        File.WriteAllText(Path.Combine(directory, "shim.cpp"), CppClassAnalysis.renderCppObjectStorageShimC header ctx.KnownClasses spec counter)
        let runnerSource =
            String.concat "\n" [
                "#include \"counter.hpp\""
                "#include <cstdio>"
                "extern \"C\" void* demo_Counter_ctor(int) noexcept;"
                "extern \"C\" int demo_Counter_add(void*, int) noexcept;"
                "extern \"C\" demo::Pair demo_Counter_pair(void*) noexcept;"
                "extern \"C\" void demo_Counter_result(void*, void*) noexcept;"
                "extern \"C\" void demo_Counter_dtor(void*) noexcept;"
                "int main() {"
                "  void* object = demo_Counter_ctor(40);"
                "  if (!object) return 1;"
                "  int value = demo_Counter_add(object, 2);"
                "  demo::Pair pair = demo_Counter_pair(object);"
                "  alignas(demo::Result) unsigned char storage[sizeof(demo::Result)];"
                "  demo_Counter_result(storage, object);"
                "  auto* result = reinterpret_cast<demo::Result*>(storage);"
                "  int returned = result->value;"
                "  result->~Result();"
                "  demo_Counter_dtor(object);"
                "  std::printf(\"%d %d %d %d %d %d\\n\", value, pair.first, pair.second, returned,"
                "              counter_destructions(), result_destructions());"
                "  return value == 42 && pair.first == 42 && pair.second == 43 && returned == 44"
                "      && counter_destructions() == 1 && result_destructions() == 1 ? 0 : 2;"
                "}"
            ]
        File.WriteAllText(Path.Combine(directory, "runner.cpp"), runnerSource)
        run "/usr/bin/clang++" [ "-std=c++17"; "-shared"; "-fPIC"; "counter.cpp"; "-o"; "libcounter.so" ] |> ignore
        run "/usr/bin/clang++" [ "-std=c++17"; "-Wno-return-type-c-linkage"; "shim.cpp"; "runner.cpp"; "-L."; "-lcounter"; "-Wl,-rpath,$ORIGIN"; "-o"; "runner" ] |> ignore
        Assert.Equal("42 42 43 44 1 1", (run (Path.Combine(directory, "runner")) []).Trim())
    finally
        Directory.Delete(directory, true)

[<Fact>]
let ``shimmed C++ exception payload lifecycle refuses use-after-release with FS9217 double-release with FS9218 and leaked payload at exit`` () =
    let _, baseCls = cppObjectContract ()
    let vmethod = { mkFunc "compute" "int" [ "x", "int" ] with IsVirtual = true; MangledName = Some "_ZN6Widget7computeEi" }
    let cls = { baseCls with Methods = [ CppParser.Declaration.Function vmethod ] }
    let unshimmedSpec : PilotTypes.CppClassSpec =
        { Name = "Widget"; Namespace = "Test"; Bind = true; KindOverride = None; SizeOverride = None }
    let shimmedSpec : PilotTypes.CppClassSpec =
        { unshimmedSpec with KindOverride = Some "shim" }
    // Direct binding without shim still refuses virtual/throwing class with FS9213
    let directErr = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.verifyCppObjectLifecycle unshimmedSpec cls [])
    Assert.Equal(Diagnostics.CppInterfaceClassUnadmitted, (only directErr.Refusal).Code)
    // Valid shimmed lifecycle with object + exception payload cleanup succeeds
    CppClassAnalysis.verifyCppObjectLifecycle shimmedSpec cls [
        CppClassAnalysis.CppObjectOperation.Construct "obj"
        CppClassAnalysis.CppObjectOperation.MethodCall ("obj", "farscape_cpp_Widget_compute")
        CppClassAnalysis.CppObjectOperation.AcquireErrorPayload "err"
        CppClassAnalysis.CppObjectOperation.ReadErrorPayload "err"
        CppClassAnalysis.CppObjectOperation.ReleaseErrorPayload "err"
        CppClassAnalysis.CppObjectOperation.Release "obj"
        CppClassAnalysis.CppObjectOperation.ScopeExit ]
    // Reading error payload after release is refused with FS9217
    let uafErr = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.verifyCppObjectLifecycle shimmedSpec cls [
            CppClassAnalysis.CppObjectOperation.AcquireErrorPayload "err"
            CppClassAnalysis.CppObjectOperation.ReleaseErrorPayload "err"
            CppClassAnalysis.CppObjectOperation.ReadErrorPayload "err" ])
    Assert.Equal(Diagnostics.CppObjectUseAfterRelease, (only uafErr.Refusal).Code)
    // Releasing error payload twice is refused with FS9218
    let dblErr = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.verifyCppObjectLifecycle shimmedSpec cls [
            CppClassAnalysis.CppObjectOperation.AcquireErrorPayload "err"
            CppClassAnalysis.CppObjectOperation.ReleaseErrorPayload "err"
            CppClassAnalysis.CppObjectOperation.ReleaseErrorPayload "err" ])
    Assert.Equal(Diagnostics.CppObjectDoubleRelease, (only dblErr.Refusal).Code)
    // Leaking error payload at scope exit is refused with the class shape code (FS9212)
    let leakErr = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.verifyCppObjectLifecycle shimmedSpec cls [
            CppClassAnalysis.CppObjectOperation.AcquireErrorPayload "err"
            CppClassAnalysis.CppObjectOperation.ScopeExit ])
    Assert.Equal(Diagnostics.CppValueClassUnadmitted, (only leakErr.Refusal).Code)

[<Fact>]
let ``shimmed C++ class binds virtual dispatch base casts and throwing methods through generated native shim and runs natively`` () =
    let directory = TestHelpers.TestScratch.create "pilot-fixture-tests"
    Directory.CreateDirectory directory |> ignore
    let run executable arguments =
        let info = System.Diagnostics.ProcessStartInfo(executable)
        info.WorkingDirectory <- directory
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in arguments do info.ArgumentList.Add argument
        use proc = System.Diagnostics.Process.Start info
        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        Assert.True(proc.ExitCode = 0, executable + ": " + stderr.Result + stdout.Result)
        stdout.Result
    try
        let header = Path.Combine(directory, "processor.hpp")
        let headerSource =
            String.concat "\n" [
                "#pragma once"
                "#include <cstddef>"
                "#include <new>"
                "namespace demo {"
                "class Processor {"
                "protected:"
                "  int offset;"
                "public:"
                "  static void* operator new(std::size_t sz, const std::nothrow_t& tag) noexcept;"
                "  static void operator delete(void* p) noexcept;"
                "  explicit Processor(int off);"
                "  virtual ~Processor() noexcept(false);"
                "  virtual int compute(int x) noexcept;"
                "  virtual int checked_divide(int numerator, int divisor);"
                "};"
                "class ScaledProcessor : public Processor {"
                "  int scale;"
                "public:"
                "  explicit ScaledProcessor(int off, int s) noexcept;"
                "  ~ScaledProcessor() noexcept(false) override;"
                "  int compute(int x) noexcept override;"
                "};"
                "}"
                "extern \"C\" void set_fail_alloc(int fail) noexcept;"
                "extern \"C\" int processor_destructions() noexcept;"
                "extern \"C\" int scaled_destructions() noexcept;"
            ]
        File.WriteAllText(header, headerSource)
        let implSource =
            String.concat "\n" [
                "#include \"processor.hpp\""
                "#include <stdexcept>"
                "static int proc_dtors = 0, scaled_dtors = 0, fail_alloc = 0;"
                "void* demo::Processor::operator new(std::size_t sz, const std::nothrow_t& tag) noexcept {"
                "  if (fail_alloc) return nullptr;"
                "  return ::operator new(sz, tag);"
                "}"
                "void demo::Processor::operator delete(void* p) noexcept { ::operator delete(p); }"
                "extern \"C\" void set_fail_alloc(int fail) noexcept { fail_alloc = fail; }"
                "demo::Processor::Processor(int off) : offset(off) {"
                "  if (off < 0) throw std::invalid_argument(\"negative offset\");"
                "}"
                "demo::Processor::~Processor() noexcept(false) {"
                "  ++proc_dtors;"
                "  if (offset == 999) throw std::runtime_error(\"dtor failure\");"
                "}"
                "int demo::Processor::compute(int x) noexcept { return offset + x; }"
                "int demo::Processor::checked_divide(int numerator, int divisor) {"
                "  if (divisor == 0) throw std::runtime_error(\"divide by zero\");"
                "  if (divisor < 0) throw 42;"
                "  return offset + (numerator / divisor);"
                "}"
                "demo::ScaledProcessor::ScaledProcessor(int off, int s) noexcept : Processor(off), scale(s) {}"
                "demo::ScaledProcessor::~ScaledProcessor() noexcept(false) { ++scaled_dtors; }"
                "int demo::ScaledProcessor::compute(int x) noexcept { return offset + (x * scale); }"
                "extern \"C\" int processor_destructions() noexcept { return proc_dtors; }"
                "extern \"C\" int scaled_destructions() noexcept { return scaled_dtors; }"
            ]
        File.WriteAllText(Path.Combine(directory, "processor.cpp"), implSource)
        let parsed =
            CppParser.parseHeader { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false }
            |> Result.defaultWith failwith
        let classes = parsed |> List.choose (function CppParser.Declaration.Class c -> Some c | _ -> None)
        let measured =
            CppParser.extractStructLayoutsForModeDiagnosed header [] [] [] [ "demo::Processor"; "demo::ScaledProcessor" ] true false
            |> Result.defaultWith (fun refusal -> failwith refusal.Text)
        let classes = classes |> List.map (fun c ->
            let key = c.QualifiedName |> Option.defaultValue c.Name
            { c with Measured = measured.TryFind key })
        let processor = classes |> List.find (fun c -> c.Name = "Processor")
        let scaled = classes |> List.find (fun c -> c.Name = "ScaledProcessor")
        let procSpec : PilotTypes.CppClassSpec =
            { Name = "demo::Processor"; Namespace = "Test.Cpp"; Bind = true; SizeOverride = None; KindOverride = Some "shim" }
        let scaledSpec : PilotTypes.CppClassSpec =
            { Name = "demo::ScaledProcessor"; Namespace = "Test.Cpp"; Bind = true; SizeOverride = None; KindOverride = Some "shim" }
        let decls = classes |> List.map CppParser.Declaration.Class
        let ctx =
            { FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with
                CppClasses = Map.ofList [ procSpec.Name, procSpec; "Processor", procSpec; scaledSpec.Name, scaledSpec; "ScaledProcessor", scaledSpec ] }
        let code = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test.Cpp" "processor" "C++ shim fixture" []
        Assert.DoesNotContain("nativeint", code)
        Assert.DoesNotContain("nativeptr", code)
        for expected in [ "type demo_Processor"; "type demo_Processor_Error"; "type demo_ScaledProcessor"
                          "let StatusOk : int = 0"; "let StatusNullHandle : int = -1"; "let StatusStdException : int = 1"; "let StatusUnknownException : int = 2"; "let StatusAllocationFailed : int = 3"
                          "let demo_Processor_error_dtor (err: CHandle<demo_Processor_Error>) : unit"
                          "let demo_Processor_take_error () : option<CHandle<demo_Processor_Error>>"
                          "let demo_Processor_take_errorResourceDescriptor : Expr<ResourceDescriptor>"
                          "Binding = \"Test.Cpp.demo_Processor_error_dtor\""
                          "let demo_Processor_compute (self: CHandle<demo_Processor>) (x: int) : int"
                          "let demo_Processor_checked_divide (outResult: CHandle<int>) (self: CHandle<demo_Processor>) (numerator: int) (divisor: int) : int"
                          "let demo_ScaledProcessor_as_demo_Processor (self: CHandle<demo_ScaledProcessor>) : option<CHandle<demo_Processor>>" ] do
            Assert.Contains(expected, code)
        File.WriteAllText(Path.Combine(directory, "shim_proc.cpp"), CppClassAnalysis.renderCppObjectStorageShimC header ctx.KnownClasses procSpec processor)
        File.WriteAllText(Path.Combine(directory, "shim_scaled.cpp"), CppClassAnalysis.renderCppObjectStorageShimC header ctx.KnownClasses scaledSpec scaled)
        let runnerSource =
            String.concat "\n" [
                "#include <cstdio>"
                "#include <cstring>"
                "#include \"processor.hpp\""
                "extern \"C\" void* farscape_cpp_demo_Processor_ctor(int) noexcept;"
                "extern \"C\" void farscape_cpp_demo_Processor_dtor(void*) noexcept;"
                "extern \"C\" int farscape_cpp_demo_Processor_compute(void*, int) noexcept;"
                "extern \"C\" int farscape_cpp_demo_Processor_checked_divide(int*, void*, int, int) noexcept;"
                "extern \"C\" void* farscape_cpp_demo_Processor_take_error() noexcept;"
                "extern \"C\" int farscape_cpp_demo_Processor_error_code(void*) noexcept;"
                "extern \"C\" int farscape_cpp_demo_Processor_error_len(void*) noexcept;"
                "extern \"C\" int farscape_cpp_demo_Processor_error_byte(void*, int) noexcept;"
                "extern \"C\" void farscape_cpp_demo_Processor_error_dtor(void*) noexcept;"
                "extern \"C\" void* farscape_cpp_demo_ScaledProcessor_ctor(int, int) noexcept;"
                "extern \"C\" void* farscape_cpp_demo_ScaledProcessor_as_demo_Processor(void*) noexcept;"
                "int main() {"
                "  void* baseObj = farscape_cpp_demo_Processor_ctor(10);"
                "  void* scaledObj = farscape_cpp_demo_ScaledProcessor_ctor(10, 3);"
                "  if (!baseObj || !scaledObj) return 1;"
                "  void* scaledAsBase = farscape_cpp_demo_ScaledProcessor_as_demo_Processor(scaledObj);"
                "  if (!scaledAsBase) return 2;"
                "  int baseVal = farscape_cpp_demo_Processor_compute(baseObj, 5);"
                "  int polyVal = farscape_cpp_demo_Processor_compute(scaledAsBase, 5);"
                "  int divOut = 0;"
                "  int okStatus = farscape_cpp_demo_Processor_checked_divide(&divOut, scaledAsBase, 20, 4);"
                "  if (farscape_cpp_demo_Processor_take_error() != nullptr) return 3;"
                "  int stdExStatus = farscape_cpp_demo_Processor_checked_divide(&divOut, scaledAsBase, 20, 0);"
                "  void* err1 = farscape_cpp_demo_Processor_take_error();"
                "  if (!err1 || farscape_cpp_demo_Processor_take_error() != nullptr) return 4;"
                "  int err1Code = farscape_cpp_demo_Processor_error_code(err1);"
                "  int err1Len = farscape_cpp_demo_Processor_error_len(err1);"
                "  char msg[32] = {0};"
                "  for (int i = 0; i < err1Len && i < 31; ++i) msg[i] = static_cast<char>(farscape_cpp_demo_Processor_error_byte(err1, i));"
                "  farscape_cpp_demo_Processor_error_dtor(err1);"
                "  int unkExStatus = farscape_cpp_demo_Processor_checked_divide(&divOut, scaledAsBase, 20, -1);"
                "  void* err2 = farscape_cpp_demo_Processor_take_error();"
                "  int err2Code = farscape_cpp_demo_Processor_error_code(err2);"
                "  farscape_cpp_demo_Processor_error_dtor(err2);"
                "  void* badCtor = farscape_cpp_demo_Processor_ctor(-5);"
                "  void* err3 = farscape_cpp_demo_Processor_take_error();"
                "  int badCtorFailed = (badCtor == nullptr && err3 != nullptr && farscape_cpp_demo_Processor_error_code(err3) == 1) ? 1 : 0;"
                "  farscape_cpp_demo_Processor_error_dtor(err3);"
                "  set_fail_alloc(1);"
                "  void* allocCtor = farscape_cpp_demo_Processor_ctor(7);"
                "  set_fail_alloc(0);"
                "  void* errAlloc = farscape_cpp_demo_Processor_take_error();"
                "  int allocStatus = (allocCtor == nullptr && errAlloc != nullptr) ? farscape_cpp_demo_Processor_error_code(errAlloc) : 0;"
                "  farscape_cpp_demo_Processor_error_dtor(errAlloc);"
                "  void* throwDtorObj = farscape_cpp_demo_Processor_ctor(999);"
                "  farscape_cpp_demo_Processor_dtor(throwDtorObj);"
                "  void* errDtor = farscape_cpp_demo_Processor_take_error();"
                "  int dtorExStatus = errDtor ? farscape_cpp_demo_Processor_error_code(errDtor) : 0;"
                "  farscape_cpp_demo_Processor_error_dtor(errDtor);"
                "  farscape_cpp_demo_Processor_dtor(baseObj);"
                "  farscape_cpp_demo_Processor_dtor(scaledAsBase);"
                "  std::printf(\"%d %d %d %d %d %d %s %d %d %d %d %d %d %d\\n\","
                "              baseVal, polyVal, okStatus, divOut, stdExStatus, err1Code, msg,"
                "              unkExStatus, err2Code, badCtorFailed, allocStatus, dtorExStatus,"
                "              processor_destructions(), scaled_destructions());"
                "  return 0;"
                "}"
            ]
        File.WriteAllText(Path.Combine(directory, "runner.cpp"), runnerSource)
        run "/usr/bin/clang++" [ "-std=c++17"; "-shared"; "-fPIC"; "processor.cpp"; "-o"; "libprocessor.so" ] |> ignore
        run "/usr/bin/clang++" [ "-std=c++17"; "shim_proc.cpp"; "shim_scaled.cpp"; "runner.cpp"; "-L."; "-lprocessor"; "-Wl,-rpath,$ORIGIN"; "-o"; "runner" ] |> ignore
        Assert.Equal("15 25 0 15 1 1 divide by zero 2 2 1 3 1 3 1", (run (Path.Combine(directory, "runner")) []).Trim())
    finally
        Directory.Delete(directory, true)

// ─── Pilot TOML shape (FS9197 to FS9202, and the codes generation gives the same reasons) ───

let private pilotHead = [ "[library]"; "name = \"fixture\""; "headers = [\"x.h\"]"; "[output]"; "mode = \"fidelity\""; "directory = \"out\"" ]
let private withOptions (lines: string list) = pilotHead @ [ "[options]" ] @ lines
let private binding (lines: string list) = withOptions ([ "[[options.bindings]]"; "name = \"f\"" ] @ lines)
let private hypothesis = "hypothesis = { identity = \"i\", version = \"1\", source = \"s\" }"

let private shapes : (string * string list * string * string * string) list = [
    "no library", [ "[output]"; "mode = \"fidelity\""; "directory = \"out\"" ], Diagnostics.PilotKeyMissing, "library", "Missing [library] section"
    "no output", [ "[library]"; "name = \"fixture\""; "headers = [\"x.h\"]" ], Diagnostics.PilotKeyMissing, "output", "Missing [output] section"
    "name type", [ "[library]"; "name = 3"; "headers = [\"x.h\"]"; "[output]"; "mode = \"fidelity\""; "directory = \"out\"" ], Diagnostics.PilotValueType, "library.name", "[library].name must be a string"
    "no headers", [ "[library]"; "name = \"fixture\""; "[output]"; "mode = \"fidelity\""; "directory = \"out\"" ], Diagnostics.PilotKeyMissing, "library.headers", "[library] requires either 'header' or 'headers'"
    "empty headers", [ "[library]"; "name = \"fixture\""; "headers = []"; "[output]"; "mode = \"fidelity\""; "directory = \"out\"" ], Diagnostics.PilotValueUnadmitted, "library.headers", "[library].headers must not be empty"
    "namespace entry", "namespace = [1]" :: pilotHead, Diagnostics.PilotValueType, "namespace", "Each [[namespace]] entry must be a table"
    "namespace name", pilotHead @ [ "[[namespace]]"; "description = \"d\""; "library = \"fixture\"" ], Diagnostics.PilotKeyMissing, "namespace.name", "[namespace].name is required"
    "binding name", withOptions [ "[[options.bindings]]"; "symbol = \"f\"" ], Diagnostics.PilotKeyMissing, "options.bindings.name", "options.bindings requires name"
    "indices", binding [ "string_parameters = [\"0\"]" ], Diagnostics.PilotValueType, "options.bindings[f].string_parameters", "string_parameters requires integer indices"
    "constants", binding [ "constant_parameters = [1]" ], Diagnostics.PilotValueType, "options.bindings[f].constant_parameters", "constant_parameters requires strings: an integer literal or a function-pointer sentinel macro name, or \"\" for an unfixed parameter"
    "ownership", binding [ "ownership = \"maybe\"" ], Diagnostics.PilotValueUnadmitted, "options.bindings[f].ownership", "Unknown binding ownership 'maybe'"
    "resource field", binding [ "resource = { kind = \"owned_nullable_return\", release_binding = \"a.b\", release_parameter = 0, extra = 1, " + hypothesis + " }" ], Diagnostics.PilotKeyUnknown, "options.bindings[f].resource.extra", "resource contract has unknown fields"
    "resource kind", binding [ "resource = { kind = \"shared\", release_binding = \"a.b\", release_parameter = 0, " + hypothesis + " }" ], Diagnostics.PilotValueUnadmitted, "options.bindings[f].resource.kind", "resource kind must be owned_nullable_return"
    "resource release empty", binding [ "resource = { kind = \"owned_nullable_return\", release_binding = \"\", release_parameter = 0, " + hypothesis + " }" ], Diagnostics.PilotValueUnadmitted, "options.bindings[f].resource.release_binding", "resource requires nonempty release_binding"
    "resource parameter", binding [ "resource = { kind = \"owned_nullable_return\", release_binding = \"a.b\", release_parameter = 1, " + hypothesis + " }" ], Diagnostics.ResourceReturnShape, "options.bindings[f].resource.release_parameter", "resource owned_nullable_return requires release_parameter 0"
    "resource hypothesis", binding [ "resource = { kind = \"owned_nullable_return\", release_binding = \"a.b\", release_parameter = 0 }" ], Diagnostics.PilotKeyMissing, "options.bindings[f].resource.hypothesis", "resource requires hypothesis"
    "empty identity", binding [ "resource = { kind = \"owned_nullable_return\", release_binding = \"a.b\", release_parameter = 0, hypothesis = { identity = \"\", version = \"1\", source = \"s\" } }" ], Diagnostics.HypothesisFieldEmpty, "options.bindings[f].resource.hypothesis.identity", "resource requires nonempty identity"
    "range arm", binding [ "result_range = { identity = \"i\", version = \"1\", source = \"s\", lower = { minimum = 0 }, upper = { constant = 1 } }" ], Diagnostics.PilotKeyUnknown, "options.bindings[f].result_range.lower.minimum", "result_range lower requires exactly one constant or argument integer"
    "range argument", binding [ "result_range = { identity = \"i\", version = \"1\", source = \"s\", lower = { argument = -1 }, upper = { constant = 1 } }" ], Diagnostics.PilotValueUnadmitted, "options.bindings[f].result_range.lower.argument", "result_range lower argument requires a nonnegative index"
    "buffer same", binding [ "writable_buffers = [ { parameter = 1, count_parameter = 1, " + hypothesis + " } ]" ], Diagnostics.WritableBufferSameParameter, "options.bindings[f].writable_buffers.count_parameter", "writable_buffers requires distinct buffer and count parameters"
    "buffer index", binding [ "writable_buffers = [ { parameter = -1, count_parameter = 1, " + hypothesis + " } ]" ], Diagnostics.PilotValueUnadmitted, "options.bindings[f].writable_buffers.parameter", "writable_buffers requires nonnegative parameter within int range"
    "buffer duplicate", binding [ "writable_buffers = [ { parameter = 1, count_parameter = 2, " + hypothesis + " }, { parameter = 1, count_parameter = 3, " + hypothesis + " } ]" ], Diagnostics.ContractDuplicateParameter, "options.bindings[f].writable_buffers", "writable_buffers has duplicate parameter indices"
    "buffer array", binding [ "writable_buffers = 1" ], Diagnostics.PilotValueType, "options.bindings[f].writable_buffers", "writable_buffers requires an array"
    "scope", binding [ "callback_invocations = [ { parameter = 0, identity = \"i\", version = \"1\", source = \"s\", scope = \"forever\" } ]" ], Diagnostics.CallbackScopeUnknown, "options.bindings[f].callback_invocations.scope", "callback_invocations scope must be during_call, retained or process_exit"
    "scope field", binding [ "callback_invocations = [ { parameter = 0, identity = \"i\", version = \"1\", source = \"s\", scope = \"during_call\", success_status = 0 } ]" ], Diagnostics.CallbackScopeFieldConflict, "options.bindings[f].callback_invocations", "callback_invocations during_call forbids exit_binding and success_status"
    "invocation field", binding [ "callback_invocations = [ { parameter = 0, identity = \"i\", version = \"1\", source = \"s\", scope = \"during_call\", count = 1 } ]" ], Diagnostics.PilotKeyUnknown, "options.bindings[f].callback_invocations.count", "callback_invocations has unknown fields"
    "absence", binding [ "callback_invocations = [ { parameter = 0, identity = \"i\", version = \"1\", source = \"s\", scope = \"during_call\", absence_hypothesis = { identity = \" \", version = \"1\", source = \"s\" } } ]" ], Diagnostics.HypothesisFieldEmpty, "options.bindings[f].callback_invocations.absence_hypothesis.identity", "callback_invocations absence_hypothesis requires nonempty identity"
    "flexible repeated", withOptions [ "flexible_arrays = [ { record = \"r\", field = \"f\", count_field = \"n\", count_rule = \"element_count\", source = \"s\" }, { record = \"r\", field = \"f\", count_field = \"n\", count_rule = \"element_count\", source = \"s\" } ]" ], Diagnostics.PilotEntryRepeated, "options.flexible_arrays.record", "options.flexible_arrays declares record 'r' more than once; a struct has at most one flexible array member"
    "mapped key", withOptions [ "[[options.mapped_returns]]"; "name = \"m\"" ], Diagnostics.PilotKeyMissing, "options.mapped_returns.acquire", "options.mapped_returns requires acquire"
    "value structs", withOptions [ "value_structs = [1]" ], Diagnostics.PilotValueType, "options.value_structs", "value_structs requires type names" ]

[<Fact>]
let ``each pilot TOML-shape refusal carries its code, its pilot key and its exact reason`` () =
    for shape, lines, code, key, message in shapes do
        let doc = Fidelity.Data.TOML.Toml.parseOrFail (String.concat "\n" lines)
        match PilotSerializer.deserializeDiagnosed doc with
        | Ok _ -> failwithf "shape '%s' was admitted" shape
        | Error refusal ->
            let d = only refusal
            Assert.True((code, key, message) = (d.Code, (d.Pilot |> Option.map _.Key |> Option.defaultValue ""), d.Message),
                        $"shape '{shape}': expected {code} at {key}: {message}; got {d.Code} at {d.Pilot}: {d.Message}")
            Assert.Equal(code + ": " + message, refusal.Text)

[<Fact>]
let ``a pilot file that is no TOML document is refused naming the file`` () =
    let path = Path.Combine(Path.GetTempPath(), "farscape-shape-" + System.Guid.NewGuid().ToString("N") + ".pilot.toml")
    File.WriteAllText(path, "[library\nname = ")
    try
        match PilotSerializer.loadFromFileDiagnosed path with
        | Ok _ -> failwith "a malformed pilot was admitted"
        | Error refusal ->
            let d = only refusal
            Assert.Equal(Diagnostics.PilotDocumentUnreadable, d.Code)
            Assert.StartsWith("TOML parse error: ", d.Message)
            Assert.Equal(pilotKey path "" |> Option.map (fun p -> { p with Key = "" }), d.Pilot)
            Assert.Equal<string list>([ "FS9197 source unknown: the refusal is in pilot TOML input, not a header declaration"; $"FS9197 pilot file {path}" ], Diagnostics.renderLocation d)
    finally
        File.Delete path

[<Fact>]
let ``a pilot shape refusal from a file binds the pilot file to its key`` () =
    let path = Path.Combine(Path.GetTempPath(), "farscape-shape-" + System.Guid.NewGuid().ToString("N") + ".pilot.toml")
    File.WriteAllText(path, String.concat "\n" (binding [ "ownership = \"maybe\"" ]))
    try
        match PilotSerializer.loadFromFileDiagnosed path with
        | Ok _ -> failwith "an unknown ownership was admitted"
        | Error refusal ->
            let d = only refusal
            Assert.Equal(Diagnostics.PilotValueUnadmitted, d.Code)
            Assert.Equal(pilotKey (Path.GetFullPath path) "options.bindings[f].ownership", d.Pilot)
    finally
        File.Delete path

// ─── Introspection loading (FS9190 to FS9192) and signal selection (FS9193 to FS9196) ───

let private girWith (classBody: string) = $"""<?xml version="1.0"?>
<repository version="1.2" xmlns="http://www.gtk.org/introspection/core/1.0" xmlns:c="http://www.gtk.org/introspection/c/1.0" xmlns:glib="http://www.gtk.org/introspection/glib/1.0">
  <namespace name="Fx" version="1.0" c:identifier-prefixes="Fx" c:symbol-prefixes="fx">
    <class name="Widget" c:symbol-prefix="widget" c:type="FxWidget">{classBody}</class>
  </namespace>
</repository>"""

let private withGir (contents: string) (f: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), "farscape-gir-" + System.Guid.NewGuid().ToString("N") + ".gir")
    File.WriteAllText(path, contents)
    try f path finally File.Delete path

let private loadRefusal (paths: string list) =
    match IntrospectionParser.loadDiagnosed paths with
    | Ok _ -> failwith "the introspection files were admitted"
    | Error d -> d

[<Fact>]
let ``a missing introspection file is refused at the introspection key`` () =
    let path = Path.Combine(Path.GetTempPath(), "farscape-missing-" + System.Guid.NewGuid().ToString("N") + ".gir")
    let d = loadRefusal [ path ]
    Assert.Equal(Diagnostics.IntrospectionFileMissing, d.Code)
    Assert.Equal($"introspection file not found: {path}", d.Message)
    Assert.Equal(Some "library.introspection", d.Pilot |> Option.map _.Key)

[<Fact>]
let ``an introspection file that is no GIR document is refused in that file`` () =
    withGir "<repository/>" (fun path ->
        let d = loadRefusal [ path ]
        Assert.Equal(Diagnostics.IntrospectionNotGir, d.Code)
        Assert.Equal($"{path}: introspection XML has no <namespace> under <repository>", d.Message)
        Assert.Equal(Diagnostics.inFile path "the GIR reader gives no line position", d.Source)
        Assert.Equal(Some "library.introspection", d.Pilot |> Option.map _.Key))

[<Fact>]
let ``a signal parameter without a declared type is refused in its file`` () =
    let signal = """<glib:signal name="changed"><return-value><type name="none" c:type="void"/></return-value><parameters><parameter name="value"/></parameters></glib:signal>"""
    withGir (girWith signal) (fun path ->
        let d = loadRefusal [ path ]
        Assert.Equal(Diagnostics.SignalDeclarationIncomplete, d.Code)
        Assert.Equal($"{path}: signal 'Widget::changed': parameter 'value' has no declared type; the complete native callback arity cannot be established", d.Message))

[<Fact>]
let ``a signal with an unnamed parameter keeps the parameter-name code`` () =
    let signal = """<glib:signal name="changed"><return-value><type name="none" c:type="void"/></return-value><parameters><parameter><type name="gint" c:type="gint"/></parameter></parameters></glib:signal>"""
    withGir (girWith signal) (fun path -> Assert.Equal(Diagnostics.SignalParameterNamesInvalid, (loadRefusal [ path ]).Code))

let private selectionRefusal (selection: string) =
    let signals = """<glib:signal name="changed"><return-value><type name="none" c:type="void"/></return-value><parameters><parameter name="value"><type name="Missing.Thing"/></parameter></parameters></glib:signal>
                     <glib:signal name="listed"><return-value><type name="none" c:type="void"/></return-value><parameters><parameter name="values"><array><type name="gint"/></array></parameter></parameters></glib:signal>"""
    let ns = IntrospectionParser.parseDocument (girWith signals) |> function Ok ns -> ns | Error e -> failwith e
    let repository : IntrospectionParser.Repository = { Namespaces = Map.ofList [ "Fx", ns; "Other", { ns with Name = "Other" } ] }
    match IntrospectionParser.selectDiagnosed repository [ selection ] with
    | Ok _ -> failwith "the selection was admitted"
    | Error d -> d

[<Theory>]
[<InlineData("changed", "FS9193", "signal selection 'changed' must be spelled Namespace.Class::signal-name or Class::signal-name")>]
[<InlineData("Fx.Widget::missing", "FS9194", "signal 'Fx.Widget::missing' is not declared in the listed introspection files")>]
[<InlineData("Widget::changed", "FS9195", "signal 'Widget::changed' is ambiguous; select a namespace-qualified signal: Fx.Widget::changed, Other.Widget::changed")>]
[<InlineData("Fx.Widget::changed", "FS9196", "signal 'Fx.Widget::changed': parameter 'value': type 'Missing.Thing' is in namespace 'Missing', whose introspection file is not listed")>]
let ``each signal selection refusal carries its code and the signals key`` (selection: string) (code: string) (message: string) =
    let d = selectionRefusal selection
    Assert.Equal(code, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some "namespace.signals", d.Pilot |> Option.map _.Key)

[<Fact>]
let ``a missing introspection file named by a pilot is refused with the pilot file bound`` () =
    let missing = Path.Combine(Path.GetTempPath(), "farscape-missing-" + System.Guid.NewGuid().ToString("N") + ".gir")
    let project, refusal = refusedPilot [ fixturePath "macros/composed.h" ] [ $"introspection = [\"{missing}\"]" ] [] [ "fx_bind_text" ] [ "signals = [\"Fx.Widget::changed\"]" ]
    let d = only refusal
    Assert.Equal(Diagnostics.IntrospectionFileMissing, d.Code)
    Assert.Equal(pilotKey project "library.introspection", d.Pilot)
    Assert.Equal($"Binding generation failed: FS9190: introspection file not found: {missing}", refusal.Text)

// ─── abi_critical_structs (FS9185 to FS9189) ───

[<Fact>]
let ``an abi_critical_structs typedef no header declares is refused at its pilot key`` () =
    let project, refusal = refusedPilot [ fixturePath "macros/composed.h" ] [] [ "c_header_mode = true"; "abi_critical_structs = [\"typedef fx_absent_t\"]" ] [ "fx_bind_text" ] []
    let d = only refusal
    Assert.Equal(Diagnostics.AbiStorageUndeclared, d.Code)
    Assert.Equal("ABI-critical typedef 'fx_absent_t' is not declared.", d.Message)
    Assert.Equal(pilotKey project "options.abi_critical_structs", d.Pilot)
    Assert.Equal(Diagnostics.Located.Unknown "no parsed header declares a record named 'fx_absent_t'", d.Source)

[<Fact>]
let ``an abi_critical_structs entry naming another record kind is refused at the record`` () =
    let header = fixturePath "macros/composed.h"
    let project, refusal = refusedPilot [ header ] [] [ "c_header_mode = true"; "abi_critical_structs = [\"union fx_registers\"]" ] [ "fx_bind_text" ] []
    let d = only refusal
    Assert.Equal(Diagnostics.AbiStorageKindMismatch, d.Code)
    Assert.Equal("ABI-critical storage 'fx_registers' does not match the requested record kind.", d.Message)
    Assert.Equal(pilotKey project "options.abi_critical_structs", d.Pilot)
    match d.Source with
    | Diagnostics.Located.Known { Path = path } -> Assert.Equal(header, path)
    | other -> failwithf "the record's location is missing: %A" other

[<Fact>]
let ``a multi-header abi_critical_structs probe refuses with FS9187 when a declaring unit fails to measure the record`` () =
    let dir = Path.Combine(Path.GetTempPath(), "farscape-probe-fail-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        let h1 = Path.Combine(dir, "unit_ok.h")
        let h2 = Path.Combine(dir, "unit_poisoned.h")
        // Both units parse cleanly and declare `struct fx_dual` identically so `agreeRecords`
        // passes and `measurementUnits` assigns both headers, while `unit_poisoned.h` poisons
        // `fx_dual` after its declarations so clang's `sizeof(struct fx_dual)` layout probe fails
        // in `unit_poisoned.h` (#52).
        File.WriteAllText(h1, "struct fx_dual { int a; int tag; };\nint fx_use_one(struct fx_dual *p);\n")
        File.WriteAllText(h2, "struct fx_dual { int a; int tag; };\nint fx_use_two(struct fx_dual *p);\n#pragma GCC poison fx_dual\n")
        let project, refusal =
            refusedPilot [ h1; h2 ] [] [ "c_header_mode = true"; "abi_critical_structs = [\"struct fx_dual\"]" ] [ "fx_use_one"; "fx_use_two" ] []
        let d = only refusal
        Assert.Equal("FS9187", d.Code)
        Assert.Equal(Diagnostics.AbiStorageUnmeasured, d.Code)
        Assert.Equal(pilotKey project "options.abi_critical_structs", d.Pilot)
        Assert.Contains("unit_poisoned.h", d.Message)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``emitted Clef purity ignores literals comments and identifier primes but refuses forbidden code tokens`` () =
    let pureCode =
        String.concat "\n" [
            "// nativeint"
            "(* null box obj (* nativeptr *) *)"
            "let message = \"nativeptr null\""
            "let escaped = \"quote: \\\"obj\\\" and slash: \\\\ nativeint\""
            "let multiline = \"\"\"box \" null nativeptr\"\"\""
            "let letter = 'x'"
            "let newline = '\\n'"
            "let quote = '\\''"
            "let namespace' = 1"
            "let identity (value: 'T) : 'T = value"
            "let nativeinteger = namespace'"
            "let box' = 1" ]
    match BindingGenerator.verifyEmittedClefPurity [ "Pure.clef", pureCode ] with
    | Ok () -> ()
    | Error refusal -> failwithf "Pure Clef was refused: %s" refusal.Text
    for token in [ "obj"; "box"; "null"; "nativeint"; "nativeptr" ] do
        for prefix in [ ""; "let namespace' = 1\n"; "let identity (value: 'T) = value\n" ] do
            let code = prefix + "let rejected = " + token
            match BindingGenerator.verifyEmittedClefPurity [ "Pure.clef", pureCode; "Nested/Impure.clef", code ] with
            | Ok () -> failwithf "Forbidden code token '%s' was admitted after %A" token prefix
            | Error refusal ->
                let d = only refusal
                Assert.Equal(Diagnostics.EmittedClefImpure, d.Code)
                Assert.Equal("FS9231", d.Code)
                Assert.Contains("Nested/Impure.clef", d.Message)
                Assert.Contains(token, d.Message)

// #118: an AST template recipe never authorizes an absent binary instantiation.
let private xrtLibrary = "/usr/lib/libxrt_coreutil.so"
let private patchTemplate = "xrt::module_elf::patch_it_impl"
let private holderTemplate = "xrt::queue::event::event_holder"
let private patchSymbol = "_ZN3xrt10module_elf13patch_it_implINS_2boEEEbT_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEmmN8xrt_core7patcher8buf_typeEjb"
let private holderSymbols = [
    "_ZN3xrt5queue5event12event_holderIvED1Ev"
    "_ZN3xrt5queue5event12event_holderIvED2Ev"
]
let private xrtInventory = lazy (CppClassAnalysis.inspectTemplateInventory xrtLibrary [])

[<Fact>]
let xrtInventoryAndPositiveResolution () =
    let inventory = xrtInventory.Value
    Assert.Equal(xrtLibrary, inventory.Build.LibraryPath)
    Assert.Equal("/usr/lib/libxrt_coreutil.so.2.21.75", inventory.Build.ResolvedPath)
    Assert.Matches("^[0-9a-f]{64}$", inventory.Build.Sha256)
    Assert.Equal(ClangToolchain.sha256 inventory.Build.ResolvedPath, inventory.Build.Sha256)
    Assert.Equal(Some "libxrt_coreutil.so.2", inventory.Build.Soname)
    for name, arguments, expected in [
        patchTemplate, ["xrt::bo"], [patchSymbol]
        holderTemplate, ["void"], holderSymbols
    ] do
        let matched, build = CppClassAnalysis.requireInstantiation inventory name arguments None
        Assert.Equal(inventory.Build, build)
        Assert.Equal(name, matched.TemplateName)
        Assert.Equal<string list>(arguments, matched.TemplateArguments)
        for symbol in expected do Assert.Contains(symbol, matched.MangledSymbols)
        Assert.Equal<string list>(List.sort matched.MangledSymbols, matched.MangledSymbols)
        Assert.Equal(matched.MangledSymbols.Length, matched.DemangledSymbols.Length)
        Assert.False(matched.AstDeclared)

[<Fact>]
let missingXrtInstantiationHasExactCodedRefusal () =
    let inventory = xrtInventory.Value
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
        CppClassAnalysis.requireInstantiation inventory patchTemplate ["int"] None |> ignore)
    let d = Assert.Single(error.Refusal.Diagnostics)
    Assert.Equal("FS9215", d.Code)
    Assert.Equal(Diagnostics.CppTemplateInstantiationMissing, d.Code)
    Assert.Equal(
        $"C++ template instantiation 'xrt::module_elf::patch_it_impl<int>' is not exported by library build '/usr/lib/libxrt_coreutil.so (libxrt_coreutil.so.2)' (SHA-256 {inventory.Build.Sha256}).",
        d.Message)

let private withTemplateHeader action =
    let directory = TestHelpers.TestScratch.create "pilot-fixture-tests"
    Directory.CreateDirectory directory |> ignore
    try
        let header = Path.Combine(directory, "templates.hpp")
        File.WriteAllText(header, "namespace xrt {\nclass bo;\nclass module_elf {\npublic:\n  template<class T> bool patch_it_impl(T value);\n};\nnamespace queue { namespace event {\ntemplate<class T> struct event_holder {};\ntemplate<> struct event_holder<void> {};\n}}\ntemplate<class T> int unexported(T);\n}\n")
        action directory header
    finally
        Directory.Delete(directory, true)

[<Fact>]
let clangRecipesAnnotateExportsAndRefuseAbsentInstantiationAtHeaderLocation () =
    withTemplateHeader (fun _ header ->
        let options = { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false }
        let parsed = CppParser.parseHeaderFull options |> Result.defaultWith failwith
        for kind in ["ClassTemplateDecl"; "FunctionTemplateDecl"; "ClassTemplateSpecializationDecl"] do
            Assert.Contains(parsed.Templates, fun declaration -> declaration.Kind = kind)
        let inventory = CppClassAnalysis.inspectTemplateInventory xrtLibrary parsed.Templates
        let matched, _ = CppClassAnalysis.requireInstantiation inventory patchTemplate ["xrt::bo"] None
        Assert.True(matched.AstDeclared)
        let holder, _ = CppClassAnalysis.requireInstantiation inventory holderTemplate ["void"] None
        Assert.True(holder.AstDeclared)
        let declaration = parsed.Templates |> List.find (fun declaration -> declaration.TemplateName = patchTemplate)
        let location = declaration.Location
        match location with
        | Diagnostics.Located.Known source ->
            Assert.Equal(header, source.Path)
            Assert.Equal(Diagnostics.Located.Known ({ Line = 5; Column = 26 }: Diagnostics.SourcePosition), source.Position)
        | Diagnostics.Located.Unknown reason -> failwith reason
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.requireInstantiation inventory patchTemplate ["int"] (Some location) |> ignore)
        let d = Assert.Single(error.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppTemplateInstantiationMissing, d.Code)
        Assert.Equal(location, d.Source)
        Assert.Equal(
            $"C++ template instantiation 'xrt::module_elf::patch_it_impl<int>' is not exported by library build '/usr/lib/libxrt_coreutil.so (libxrt_coreutil.so.2)' (SHA-256 {inventory.Build.Sha256}). The template is declared in the header AST, but this instantiation is not exported by the binary.",
            d.Message)
        let inferred = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.requireInstantiation inventory patchTemplate ["int"] None |> ignore)
        Assert.Equal(location, (Assert.Single inferred.Refusal.Diagnostics).Source))

let private syntheticBuild : CppClassAnalysis.CppLibraryBuild =
    { LibraryPath = "fixture.a"; ResolvedPath = "/fixture.a"; Sha256 = String.replicate 64 "a"; Soname = None }

[<Fact>]
let nestedArgumentsAndConstructorDestructorRttiGrouping () =
    let signature = "demo::box<std::pair<int, long>, void (*)(int, int)>::"
    let symbols = [
        "_Zfixture2", signature + "~box()"
        "_Zfixture1", signature + "box()"
        "_Zfixture3", "vtable for demo::box<std::pair<int, long>, void (*)(int, int)>"
        "_Zfixture4", "typeinfo for demo::box<std::pair<int, long>, void (*)(int, int)>"
    ]
    let inventory = CppClassAnalysis.inventoryOfSymbols syntheticBuild [] (symbols @ symbols)
    let matched = Assert.Single inventory.Instantiations
    Assert.Equal("demo::box", matched.TemplateName)
    Assert.Equal<string list>(["std::pair<int,long>"; "void (*)(int,int)"], matched.TemplateArguments)
    Assert.Equal<string list>(["_Zfixture1"; "_Zfixture2"; "_Zfixture3"; "_Zfixture4"], matched.MangledSymbols)
    Assert.Equal<string list>(symbols |> List.sortBy fst |> List.map snd, matched.DemangledSymbols)

[<Fact>]
let parameterAndReturnTemplatesAreNotExports () =
    let inventory = CppClassAnalysis.inventoryOfSymbols syntheticBuild [] [
        "_Zplain", "std::vector<int> demo::plain(std::vector<long>)"
        "_Ztemplated", "std::vector<int> demo::work<void>(std::vector<long>)"
        "c_name", "demo::fake<int>()"
    ]
    let matched = Assert.Single inventory.Instantiations
    Assert.Equal("demo::work<void>", matched.InstantiationName)
    for name, args in ["std::vector", ["int"]; "std::vector", ["long"]; "demo::fake", ["int"]] do
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.requireInstantiation inventory name args None |> ignore)
        Assert.Equal("FS9215", (Assert.Single error.Refusal.Diagnostics).Code)

[<Fact>]
let exportOrderingAndSpacingDoNotChangeResolution () =
    let exports = [
        "_Zb", "bool demo::work<std::pair<int, long> >(int)"
        "_Za", "bool demo::work<std::pair<int, long> >(long)"
    ]
    let a = CppClassAnalysis.inventoryOfSymbols syntheticBuild [] exports
    let b = CppClassAnalysis.inventoryOfSymbols syntheticBuild [] (List.rev exports)
    Assert.Equal(a, b)
    let matched, provenance = CppClassAnalysis.requireInstantiation a "demo::work" ["std::pair< int , long >"] None
    Assert.Equal(syntheticBuild, provenance)
    Assert.Equal<string list>(["_Za"; "_Zb"], matched.MangledSymbols)

let private runTemplateFixtureTool program arguments =
    use child = new System.Diagnostics.Process(StartInfo = ProcessLaunch.startInfo program arguments)
    Assert.True(child.Start())
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    let output, errors = output.Result, errors.Result
    Assert.True(child.ExitCode = 0, errors)
    output

[<Fact>]
let sharedAndArchiveInventoriesExcludeUndefinedAndSharedHiddenSymbols () =
    withTemplateHeader (fun directory _ ->
        let source = Path.Combine(directory, "exports.cpp")
        File.WriteAllText(source, "\nnamespace demo {\ntemplate<class T> int present(T) { return sizeof(T); }\ntemplate int present<int>(int);\ntemplate<class T> __attribute__((visibility(\"hidden\"))) int hidden(T) { return sizeof(T); }\ntemplate int hidden<int>(int);\ntemplate<class T> int absent(T);\nint call() { return absent<long>(1); }\n}\n")
        let objectFile = Path.Combine(directory, "exports.o")
        let shared = Path.Combine(directory, "exports.so")
        let archive = Path.Combine(directory, "exports.a")
        runTemplateFixtureTool "/usr/bin/clang" ["-x"; "c++"; "-fPIC"; "-c"; source; "-o"; objectFile] |> ignore
        runTemplateFixtureTool "/usr/bin/clang" ["-shared"; objectFile; "-Wl,-soname,fixture.so.1"; "-o"; shared] |> ignore
        runTemplateFixtureTool "ar" ["rcs"; archive; objectFile] |> ignore
        let inventory = CppClassAnalysis.inspectTemplateInventory shared []
        let matched = Assert.Single inventory.Instantiations
        Assert.Equal("demo::present<int>", matched.InstantiationName)
        Assert.Equal(Some "fixture.so.1", inventory.Build.Soname)
        let nm = runTemplateFixtureTool "nm" ["-D"; "--defined-only"; "--format=posix"; shared]
        let exported =
            nm.Split('\n') |> Array.filter (fun line -> line.StartsWith("_Z") && line.Contains("present"))
            |> Array.map (fun line -> line.Split(' ').[0]) |> Array.sort |> Array.toList
        Assert.Equal<string list>(exported, matched.MangledSymbols)
        let staticInventory = CppClassAnalysis.inspectTemplateInventory archive []
        Assert.Equal(None, staticInventory.Build.Soname)
        Assert.Contains(staticInventory.Instantiations, fun entry -> entry.InstantiationName = "demo::present<int>")
        Assert.DoesNotContain(staticInventory.Instantiations, fun entry -> entry.InstantiationName = "demo::absent<long>")
        let missing = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.requireInstantiation staticInventory "demo::absent" ["long"] None |> ignore)
        Assert.Equal("FS9215", (Assert.Single missing.Refusal.Diagnostics).Code))

[<Fact>]
let realXrtTemplateSymbolSetsMatchIndependentNmObservation () =
    let nm = runTemplateFixtureTool "nm" ["-D"; "--defined-only"; "--format=posix"; xrtLibrary]
    let symbols =
        nm.Split('\n')
        |> Array.filter (fun line ->
            line.StartsWith("_Z") &&
            System.Text.RegularExpressions.Regex.IsMatch(line, "^_Z(NK?3xrt5queue5event12event_holderIvE|T[VIS]N3xrt5queue5event12event_holderIvE|N3xrt10module_elf13patch_it_implINS_2boEE)"))
        |> Array.map (fun line -> line.Split(' ').[0])
        |> Array.sort |> Array.toList
    let inventory = xrtInventory.Value
    let actual =
        inventory.Instantiations
        |> List.filter (fun entry ->
            entry.InstantiationName = patchTemplate + "<xrt::bo>" || entry.InstantiationName = holderTemplate + "<void>")
        |> List.collect _.MangledSymbols |> List.sort
    Assert.NotEmpty(symbols)
    Assert.Equal<string list>(symbols, actual)

[<Fact>]
let removingExportCannotBeRepairedByAnAstRecipe () =
    withTemplateHeader (fun _ header ->
        let parsed = CppParser.parseHeaderFull { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false }
                     |> Result.defaultWith failwith
        let inventory = CppClassAnalysis.inventoryOfSymbols syntheticBuild parsed.Templates []
        Assert.Empty(inventory.Instantiations)
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.requireInstantiation inventory patchTemplate ["xrt::bo"] None |> ignore)
        let d = Assert.Single error.Refusal.Diagnostics
        Assert.Equal("FS9215", d.Code)
        Assert.Contains("declared in the header AST", d.Message))

[<Fact>]
let templateOperatorsAndConversionTargetsKeepTheirOwningIdentity () =
    let inventory = CppClassAnalysis.inventoryOfSymbols syntheticBuild [] [
        "_Zcall", "bool demo::box<int>::operator()<long>(long)"
        "_Zshift", "bool demo::operator<< <int>(int)"
        "_Zconvert", "demo::box<int>::operator std::vector<long>() const"
    ]
    Assert.Contains(inventory.Instantiations, fun entry -> entry.InstantiationName = "demo::box<int>::operator()<long>")
    Assert.Contains(inventory.Instantiations, fun entry -> entry.InstantiationName = "demo::operator<<<int>")
    Assert.DoesNotContain(inventory.Instantiations, fun entry -> entry.TemplateName = "std::vector")
    let owner = inventory.Instantiations |> List.find (fun entry -> entry.InstantiationName = "demo::box<int>")
    Assert.Equal<string list>(["_Zcall"; "_Zconvert"], owner.MangledSymbols)

// ─── Plugify C++ ABI facts with provenance and FS9216 refusal (#119) ───

let private plugifyTool : CppClassAnalysis.CppAbiFactToolProvenance = {
    ToolName = "plugify"
    ToolVersion = "0.21.0"
    ManifestSource = "xrt-coreutil.pplugin.json"
}

let private withXrtAbiHeader action =
    let directory = TestHelpers.TestScratch.create "pilot-fixture-tests"
    Directory.CreateDirectory directory |> ignore
    try
        let header = Path.Combine(directory, "xrt_abi_facts.hpp")
        File.WriteAllText(
            header,
            "namespace xrt {\n" +
            "class bo {\n" +
            "  void* m_handle;\n" +
            "public:\n" +
            "  bo(void* h);\n" +
            "  ~bo();\n" +
            "  unsigned long size() const noexcept;\n" +
            "};\n" +
            "namespace device {\n" +
            "class error {\n" +
            "public:\n" +
            "  virtual ~error();\n" +
            "};\n" +
            "}\n" +
            "class module_elf {\n" +
            "public:\n" +
            "  template<class T> bool patch_it_impl(T value);\n" +
            "};\n" +
            "namespace queue { namespace event {\n" +
            "template<class T> struct event_holder { virtual ~event_holder(); };\n" +
            "template<> struct event_holder<void> { virtual ~event_holder(); };\n" +
            "}}\n" +
            "}\n")
        action header
    finally
        Directory.Delete(directory, true)

[<Fact>]
let plugifyAbiFactsForRealXrtClassesAndTemplateInstantiationCarryProvenanceAndNeverDischargeProof () =
    withXrtAbiHeader (fun header ->
        let parsed =
            CppParser.parseHeaderFull { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false }
            |> Result.defaultWith failwith
        let manifestJson =
            """{"generator":{"name":"plugify","version":"0.21.0"},"classes":["xrt::bo","xrt::device::error","xrt::queue::event::event_holder<void>"],"templates":[{"template":"xrt::module_elf::patch_it_impl","arguments":["xrt::bo"]},{"template":"xrt::queue::event::event_holder","arguments":["void"]}]}"""
        let bundle =
            CppClassAnalysis.generatePlugifyAbiFactsFromManifestJson
                xrtLibrary
                "xrt-coreutil.pplugin.json"
                manifestJson
                (Some parsed)
        let validated = CppClassAnalysis.validatePlugifyAbiFacts bundle xrtLibrary (Some parsed)
        Assert.Equal(bundle, validated)
        Assert.False(validated.DischargesProofObligations)
        Assert.Equal(CppClassAnalysis.CallMechanicsOnly_PsgDischargesProof, validated.ProofAuthority)
        Assert.All(validated.Facts, fun fact ->
            Assert.False(fact.DischargesProofObligations)
            Assert.Equal(validated.Provenance, fact.Provenance))
        Assert.Equal("plugify", validated.Provenance.Tool.ToolName)
        Assert.Equal("0.21.0", validated.Provenance.Tool.ToolVersion)
        Assert.StartsWith("xrt-coreutil.pplugin.json#sha256=", validated.Provenance.Tool.ManifestSource)
        Assert.Equal(xrtLibrary, validated.Provenance.Build.LibraryPath)
        Assert.Equal("/usr/lib/libxrt_coreutil.so.2.21.75", validated.Provenance.Build.ResolvedPath)
        Assert.Equal(Some "libxrt_coreutil.so.2", validated.Provenance.Build.Soname)
        Assert.Equal(ClangToolchain.sha256 validated.Provenance.Build.ResolvedPath, validated.Provenance.Build.Sha256)

        let boRaii =
            validated.Facts |> List.pick (fun f ->
                match f.Fact with
                | CppClassAnalysis.RaiiFact r when r.ClassName = "xrt::bo" -> Some r
                | _ -> None)
        Assert.Equal("_ZN3xrt2boD1Ev", boRaii.DestructorSymbol)
        Assert.Contains("_ZN3xrt2boC1ERKNS_6deviceEmNS0_5flagsEj", boRaii.ConstructorSymbols)
        Assert.Equal(Some 8, boRaii.StorageSizeBytes)

        let errorVtable =
            validated.Facts |> List.pick (fun f ->
                match f.Fact with
                | CppClassAnalysis.VirtualTableFact vt when vt.ClassName = "xrt::device::error" -> Some vt
                | _ -> None)
        Assert.Equal(Some "_ZTVN3xrt6device5errorE", errorVtable.VtableSymbol)
        Assert.Equal(Some "_ZTIN3xrt6device5errorE", errorVtable.TypeinfoSymbol)
        Assert.Equal<(int * string * string) list>(
            [
                (0, "xrt::device::error::~error", "_ZN3xrt6device5errorD1Ev")
                (1, "xrt::device::error::~error", "_ZN3xrt6device5errorD0Ev")
                (2, "xrt::device::error::what", "_ZNK3xrt6device5error4whatEv")
            ],
            errorVtable.VirtualMethods)

        let holderVtable =
            validated.Facts |> List.pick (fun f ->
                match f.Fact with
                | CppClassAnalysis.VirtualTableFact vt when vt.ClassName = "xrt::queue::event::event_holder<void>" -> Some vt
                | _ -> None)
        Assert.Equal(Some "_ZTVN3xrt5queue5event12event_holderIvEE", holderVtable.VtableSymbol)
        Assert.Equal(Some "_ZTIN3xrt5queue5event12event_holderIvEE", holderVtable.TypeinfoSymbol)
        Assert.Equal<(int * string * string) list>(
            [
                (0, "xrt::queue::event::event_holder<void>::~event_holder", "_ZN3xrt5queue5event12event_holderIvED1Ev")
                (1, "xrt::queue::event::event_holder<void>::~event_holder", "_ZN3xrt5queue5event12event_holderIvED0Ev")
                (2, "xrt::queue::event::event_holder<void>::wait", "_ZNK3xrt5queue5event12event_holderIvE4waitEv")
            ],
            holderVtable.VirtualMethods)

        let patchFact =
            validated.Facts |> List.pick (fun f ->
                match f.Fact with
                | CppClassAnalysis.TemplateInstanceFact ti when ti.InstantiationName = "xrt::module_elf::patch_it_impl<xrt::bo>" -> Some ti
                | _ -> None)
        Assert.Equal<string list>([patchSymbol], patchFact.MangledSymbols)

        let sizeBoundary =
            validated.Facts |> List.pick (fun f ->
                match f.Fact with
                | CppClassAnalysis.ExceptionBoundaryFact eb when eb.MangledSymbol = "_ZNK3xrt2bo4sizeEv" -> Some eb
                | _ -> None)
        Assert.Equal("xrt::bo::size", sizeBoundary.CallableName)
        Assert.True(sizeBoundary.IsNoExcept))

[<Fact>]
let plugifyAbiFactsRefuseMissingOrNonPlugifyToolProvenanceWithFS9216 () =
    for badTool, expectedMessage in [
        { plugifyTool with ToolName = "manual" }, "C++ ABI fact provenance tool must be 'plugify', got 'manual'."
        { plugifyTool with ToolVersion = "" }, "C++ ABI fact provenance from Plugify is missing a tool version."
        { plugifyTool with ManifestSource = "   " }, "C++ ABI fact provenance from Plugify is missing a manifest source."
    ] do
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.generatePlugifyAbiFactsFromSymbols syntheticBuild [] badTool None ["xrt::bo"] [] |> ignore)
        let d = Assert.Single(error.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, d.Code)
        Assert.Equal("FS9216", d.Code)
        Assert.Equal(expectedMessage, d.Message)

[<Fact>]
let plugifyAbiFactsRefuseConflictingBuildProvenanceAndConflictingOrMissingFactsWithFS9216 () =
    withXrtAbiHeader (fun header ->
        let parsed =
            CppParser.parseHeaderFull { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false }
            |> Result.defaultWith failwith
        let bundle =
            CppClassAnalysis.generatePlugifyAbiFacts
                xrtLibrary
                plugifyTool
                (Some parsed)
                ["xrt::bo"; "xrt::device::error"]
                [patchTemplate, ["xrt::bo"]]

        // 1. Conflicting SHA-256 build provenance is refused with FS9216.
        let tamperedBuild = { bundle.Provenance.Build with Sha256 = String.replicate 64 "0" }
        let tamperedProvenance = { bundle.Provenance with Build = tamperedBuild }
        let tamperedBundle = {
            bundle with
                Provenance = tamperedProvenance
                Facts = bundle.Facts |> List.map (fun f -> { f with Provenance = tamperedProvenance })
        }
        let errSha = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts tamperedBundle xrtLibrary (Some parsed) |> ignore)
        let dSha = Assert.Single(errSha.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dSha.Code)
        Assert.Contains($"claimed SHA-256 {tamperedBuild.Sha256}, actual SHA-256 {bundle.Provenance.Build.Sha256}", dSha.Message)

        // 2. Conflicting noexcept boundary against clang AST is refused with FS9216.
        let flippedNoExcept = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.map (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.ExceptionBoundaryFact eb when eb.MangledSymbol = "_ZNK3xrt2bo4sizeEv" ->
                            { f with Fact = CppClassAnalysis.ExceptionBoundaryFact { eb with IsNoExcept = false } }
                        | _ -> f)
        }
        let errNoExcept = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts flippedNoExcept xrtLibrary (Some parsed) |> ignore)
        let dNoExcept = Assert.Single(errNoExcept.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dNoExcept.Code)
        Assert.Equal("Plugify exception boundary ABI fact for 'xrt::bo::size' (_ZNK3xrt2bo4sizeEv) claims noexcept=false, which conflicts with clang AST noexcept=true.", dNoExcept.Message)

        // 3. Missing exported destructor symbol on RaiiFact is refused with FS9216.
        let badRaii = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.map (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.RaiiFact r when r.ClassName = "xrt::bo" ->
                            { f with Fact = CppClassAnalysis.RaiiFact { r with DestructorSymbol = "_ZN3xrt2bo_missingD1Ev" } }
                        | _ -> f)
        }
        let errRaii = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts badRaii xrtLibrary (Some parsed) |> ignore)
        let dRaii = Assert.Single(errRaii.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dRaii.Code)
        Assert.Contains("is not an exported complete-object destructor (D1)", dRaii.Message)

        // 4. Missing vtable symbol on VirtualTableFact is refused with FS9216.
        let badVtable = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.map (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.VirtualTableFact vt when vt.ClassName = "xrt::device::error" ->
                            { f with Fact = CppClassAnalysis.VirtualTableFact { vt with VtableSymbol = Some "_ZTVN3xrt6device5error_missingE" } }
                        | _ -> f)
        }
        let errVtable = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts badVtable xrtLibrary (Some parsed) |> ignore)
        let dVtable = Assert.Single(errVtable.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dVtable.Code)
        Assert.Contains("claims vtable symbol '_ZTVN3xrt6device5error_missingE', which is not exported", dVtable.Message)

        // 5. Requesting an absent template instantiation or undeclared AST class in Plugify generation is refused with FS9216.
        let errMissingTemplate = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.generatePlugifyAbiFacts xrtLibrary plugifyTool (Some parsed) [] [patchTemplate, ["int"]] |> ignore)
        let dMissingTemplate = Assert.Single(errMissingTemplate.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dMissingTemplate.Code)
        Assert.Contains("Plugify template ABI fact for 'xrt::module_elf::patch_it_impl<int>' is missing from library build", dMissingTemplate.Message)

        let errUndeclaredClass = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.generatePlugifyAbiFacts xrtLibrary plugifyTool (Some parsed) ["xrt::undeclared_widget"] [] |> ignore)
        let dUndeclaredClass = Assert.Single(errUndeclaredClass.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dUndeclaredClass.Code)
        Assert.Equal("C++ class 'xrt::undeclared_widget' is not declared in the parsed header AST.", dUndeclaredClass.Message)

        // 6. Dropping a required VirtualTableFact or RaiiFact or ExceptionBoundaryFact from a non-empty bundle is refused with FS9216.
        let missingVtableFactBundle = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.filter (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.VirtualTableFact vt when vt.ClassName = "xrt::device::error" -> false
                        | _ -> true)
        }
        let errMissingVtableFact = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts missingVtableFactBundle xrtLibrary (Some parsed) |> ignore)
        let dMissingVtableFact = Assert.Single(errMissingVtableFact.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dMissingVtableFact.Code)
        Assert.Equal("Plugify ABI fact bundle is incomplete: missing virtual table ABI fact for polymorphic C++ class 'xrt::device::error'.", dMissingVtableFact.Message)

        let missingBoundaryFactBundle = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.filter (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.ExceptionBoundaryFact eb when eb.MangledSymbol = "_ZN3xrt2boD1Ev" -> false
                        | _ -> true)
        }
        let errMissingBoundaryFact = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts missingBoundaryFactBundle xrtLibrary (Some parsed) |> ignore)
        let dMissingBoundaryFact = Assert.Single(errMissingBoundaryFact.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dMissingBoundaryFact.Code)
        Assert.Equal("Plugify ABI fact bundle is incomplete: missing exception boundary ABI fact for symbol '_ZN3xrt2boD1Ev'.", dMissingBoundaryFact.Message)

        // 7. Reordering or dropping a binary vtable slot on VirtualTableFact is refused with FS9216.
        let swappedSlotsBundle = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.map (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.VirtualTableFact vt when vt.ClassName = "xrt::device::error" ->
                            { f with Fact = CppClassAnalysis.VirtualTableFact { vt with VirtualMethods = [ (0, "xrt::device::error::~error", "_ZN3xrt6device5errorD0Ev") ] } }
                        | _ -> f)
        }
        let errSwappedSlots = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts swappedSlotsBundle xrtLibrary (Some parsed) |> ignore)
        let dSwappedSlots = Assert.Single(errSwappedSlots.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dSwappedSlots.Code)
        Assert.Contains("conflict with binary vtable '_ZTVN3xrt6device5errorE' layout", dSwappedSlots.Message)

        // 8. Omitting VtableSymbol (None) on VirtualTableFact to bypass binary vtable verification is refused with FS9216.
        let omittedVtableSymBundle = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.map (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.VirtualTableFact vt when vt.ClassName = "xrt::device::error" ->
                            { f with Fact = CppClassAnalysis.VirtualTableFact { vt with VtableSymbol = None } }
                        | _ -> f)
        }
        let errOmittedVtableSym = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts omittedVtableSymBundle xrtLibrary (Some parsed) |> ignore)
        let dOmittedVtableSym = Assert.Single(errOmittedVtableSym.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dOmittedVtableSym.Code)
        Assert.Equal("Plugify virtual table ABI fact for C++ class 'xrt::device::error' is missing required exported vtable symbol.", dOmittedVtableSym.Message)

        // 9. Removing all facts for a requested class ('xrt::bo') or removing a requested TemplateInstanceFact is refused with FS9216.
        let missingClassBundle = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.filter (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.RaiiFact r when r.ClassName = "xrt::bo" -> false
                        | _ -> true)
        }
        let errMissingClass = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts missingClassBundle xrtLibrary (Some parsed) |> ignore)
        let dMissingClass = Assert.Single(errMissingClass.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dMissingClass.Code)
        Assert.Equal("Plugify ABI fact bundle is incomplete: missing RAII ABI fact for C++ class 'xrt::bo'.", dMissingClass.Message)

        let missingTemplateFactBundle = {
            bundle with
                Facts =
                    bundle.Facts
                    |> List.filter (fun f ->
                        match f.Fact with
                        | CppClassAnalysis.TemplateInstanceFact _ -> false
                        | _ -> true)
        }
        let errMissingTemplateFact = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            CppClassAnalysis.validatePlugifyAbiFacts missingTemplateFactBundle xrtLibrary (Some parsed) |> ignore)
        let dMissingTemplateFact = Assert.Single(errMissingTemplateFact.Refusal.Diagnostics)
        Assert.Equal(Diagnostics.CppAbiFactInvalidOrConflicting, dMissingTemplateFact.Code)
        Assert.Equal("Plugify ABI fact bundle is incomplete: missing template ABI fact for requested instantiation 'xrt::module_elf::patch_it_impl<xrt::bo>'.", dMissingTemplateFact.Message))

// ─── Template containers remain opaque and operation-bound (#120) ───

let private vectorSpec : CppClassAnalysis.CppTemplateContainerSpec =
    { TemplateName = "std::vector"; ElementType = "int"; ElementClefType = "int"
      SafeName = "IntVector"; Namespace = "Test.Containers"; LibraryName = "vecfixture"
      FactorySymbol = Some "make_vector"; Source = Some (Diagnostics.Located.Unknown "container fixture") }

let private vectorInventory =
    CppClassAnalysis.inventoryOfSymbols syntheticBuild [] [
        "_Zvector_size", "std::vector<int, std::allocator<int> >::size() const" ]

let private containerRefusal code action =
    let error = Assert.Throws<Diagnostics.RefusalException>(System.Action action)
    let diagnostic = only error.Refusal
    Assert.Equal(code, diagnostic.Code)
    Assert.Equal(vectorSpec.Source.Value, diagnostic.Source)
    Assert.False(System.String.IsNullOrWhiteSpace diagnostic.Message)

[<Fact>]
let containerInstantiationMustBeExportedForEveryConsumer () =
    let missing = { vectorSpec with ElementType = "double"; ElementClefType = "float" }
    let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 Map.empty
    for action in [
        (fun () -> CppClassAnalysis.validateTemplateContainerContract vectorInventory missing)
        (fun () -> CppClassAnalysis.verifyCppContainerLifecycle vectorInventory missing [])
        (fun () -> CppClassAnalysis.renderCppTemplateContainerShimC "vector.hpp" vectorInventory missing |> ignore)
        (fun () -> FidelityCodeGenerator.generateCppTemplateContainerDecls ctx vectorInventory missing |> ignore) ] do
        containerRefusal Diagnostics.CppTemplateInstantiationMissing action
    CppClassAnalysis.validateTemplateContainerContract vectorInventory vectorSpec

[<Fact>]
let containerContractRefusesRawAddressesAndInvalidTypes () =
    for invalid in [ ""; " "; "nativeint"; "nativeptr<int>"; "_M_start"; "_M_finish"; "_M_end_of_storage"; "int*"; "int&"; "int;" ] do
        containerRefusal Diagnostics.CppContainerOutOfRange (fun () ->
            CppClassAnalysis.validateTemplateContainerContract vectorInventory { vectorSpec with ElementType = invalid })
    let boolInventory = CppClassAnalysis.inventoryOfSymbols syntheticBuild [] [
        "_Zboolvector", "std::vector<bool, std::allocator<bool> >::size() const" ]
    containerRefusal Diagnostics.CppContainerOutOfRange (fun () ->
        CppClassAnalysis.validateTemplateContainerContract boolInventory { vectorSpec with ElementType = "bool"; ElementClefType = "bool" })
    for invalid in [ "nativeint"; "nativeptr"; "_M_start"; "int*"; "" ] do
        containerRefusal Diagnostics.CppContainerOutOfRange (fun () ->
            CppClassAnalysis.validateTemplateContainerContract vectorInventory { vectorSpec with ElementClefType = invalid })

[<Fact>]
let containerLifecycleAcceptsBalancedHandlesAndEmptyRanges () =
    let verify = CppClassAnalysis.verifyCppContainerLifecycle vectorInventory vectorSpec
    let acquire = CppClassAnalysis.CppContainerOperation.Acquire
    let release = CppClassAnalysis.CppContainerOperation.Release
    verify [ acquire ("a", 3); acquire ("b", 0)
             CppClassAnalysis.CppContainerOperation.Size "a"
             CppClassAnalysis.CppContainerOperation.Get ("a", 0)
             CppClassAnalysis.CppContainerOperation.Get ("a", 2)
             CppClassAnalysis.CppContainerOperation.Iterate ("a", 0, 3)
             CppClassAnalysis.CppContainerOperation.Iterate ("a", 3, 0)
             CppClassAnalysis.CppContainerOperation.Iterate ("b", 0, 0)
             release "b"; release "a"; CppClassAnalysis.CppContainerOperation.ScopeExit ]
    verify []
    verify [ acquire ("a", 1); release "a" ]

[<Fact>]
let containerBoundsRefuseNegativeEndAndOverflowingRanges () =
    let verify operations = CppClassAnalysis.verifyCppContainerLifecycle vectorInventory vectorSpec operations
    for access in [
        CppClassAnalysis.CppContainerOperation.Get ("a", -1)
        CppClassAnalysis.CppContainerOperation.Get ("a", 3)
        CppClassAnalysis.CppContainerOperation.Get ("a", 99)
        CppClassAnalysis.CppContainerOperation.Iterate ("a", -1, 1)
        CppClassAnalysis.CppContainerOperation.Iterate ("a", 0, -1)
        CppClassAnalysis.CppContainerOperation.Iterate ("a", 2, 2)
        CppClassAnalysis.CppContainerOperation.Iterate ("a", 4, 0)
        CppClassAnalysis.CppContainerOperation.Iterate ("a", 1, System.Int32.MaxValue) ] do
        containerRefusal Diagnostics.CppContainerOutOfRange (fun () -> verify [
            CppClassAnalysis.CppContainerOperation.Acquire ("a", 3); access
            CppClassAnalysis.CppContainerOperation.Release "a" ])
    containerRefusal Diagnostics.CppContainerOutOfRange (fun () -> verify [
        CppClassAnalysis.CppContainerOperation.Acquire ("a", 0)
        CppClassAnalysis.CppContainerOperation.Get ("a", 0) ])

[<Fact>]
let containerLifecycleRefusesUseAfterReleaseAndDoubleRelease () =
    let verify = CppClassAnalysis.verifyCppContainerLifecycle vectorInventory vectorSpec
    let released = [ CppClassAnalysis.CppContainerOperation.Acquire ("a", 3); CppClassAnalysis.CppContainerOperation.Release "a" ]
    for access in [ CppClassAnalysis.CppContainerOperation.Size "a"
                    CppClassAnalysis.CppContainerOperation.Get ("a", 0)
                    CppClassAnalysis.CppContainerOperation.Iterate ("a", 0, 1) ] do
        containerRefusal Diagnostics.CppObjectUseAfterRelease (fun () -> verify (released @ [access]))
    containerRefusal Diagnostics.CppObjectDoubleRelease (fun () -> verify (released @ [ CppClassAnalysis.CppContainerOperation.Release "a" ]))

[<Fact>]
let containerLifecycleRefusesLeaksUnknownHandlesAndInvalidAcquisition () =
    let verify = CppClassAnalysis.verifyCppContainerLifecycle vectorInventory vectorSpec
    let acquire = CppClassAnalysis.CppContainerOperation.Acquire
    for path in [ [acquire ("a", 1)]; [acquire ("a", 1); CppClassAnalysis.CppContainerOperation.ScopeExit]
                  [acquire ("a", -1); CppClassAnalysis.CppContainerOperation.Release "a"]
                  [acquire ("", 1); CppClassAnalysis.CppContainerOperation.Release ""]
                  [acquire ("a", 1); acquire ("a", 1); CppClassAnalysis.CppContainerOperation.Release "a"]
                  [CppClassAnalysis.CppContainerOperation.Size "unknown"]
                  [CppClassAnalysis.CppContainerOperation.Release "unknown"]
                  [CppClassAnalysis.CppContainerOperation.ScopeExit; acquire ("a", 1)] ] do
        containerRefusal Diagnostics.CppContainerOutOfRange (fun () -> verify path)

[<Fact>]
let containerDeclsDescribeOpaqueOwnershipAndOperationAbi () =
    let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 Map.empty
    let declarations = FidelityCodeGenerator.generateCppTemplateContainerDecls ctx vectorInventory vectorSpec
    Assert.Contains(CodeAST.OpaqueMarker "IntVector", declarations)
    Assert.DoesNotContain(declarations, function CodeAST.RecordType _ -> true | _ -> false)
    let source = FidelityCodeGenerator.generateCppTemplateContainerModule ctx vectorInventory vectorSpec
    for forbidden in [ "nativeint"; "nativeptr"; "_M_start"; "_M_finish"; "_M_end_of_storage" ] do
        Assert.DoesNotContain(forbidden, source)
    for expected in [ "OwnedNullableReturn"; "Test.Containers.IntVector_release"; "CalleeOwns"; "CallerOwns"; "Borrowed"
                      "StatusOutOfRange : int = 4"; "IntVector_size"; "IntVector_get"; "IntVector_iter_next"
                      "option<CHandle<IntVector>>"; "outElem: CHandle<int>"; "inoutIndex: CHandle<int>" ] do
        Assert.Contains(expected, source)
    for elementType in [ "float"; "UnknownElement" ] do
        containerRefusal Diagnostics.CppContainerOutOfRange (fun () ->
            FidelityCodeGenerator.generateCppTemplateContainerDecls ctx vectorInventory { vectorSpec with ElementClefType = elementType } |> ignore)
    let bindings = declarations |> List.choose (function
        | CodeAST.LetBinding(name, parameters, returnType, _, attributes) -> Some (name, parameters, returnType, attributes)
        | _ -> None)
    Assert.Equal(5, bindings.Length)
    let handle name = CodeAST.Generic("CHandle", CodeAST.Named name)
    for suffix, parameterTypes, returnType in [
        "release", [handle "IntVector"], CodeAST.Named "unit"
        "acquire", [], CodeAST.Generic("option", handle "IntVector")
        "size", [handle "int"; handle "IntVector"], CodeAST.Named "int"
        "get", [handle "int"; handle "IntVector"; CodeAST.Named "int"], CodeAST.Named "int"
        "iter_next", [handle "int"; handle "int"; handle "IntVector"], CodeAST.Named "int" ] do
        let _, parameters, actualReturn, attributes = bindings |> List.find (fun (name, _, _, _) -> name = "IntVector_" + suffix)
        Assert.Equal<CodeAST.FsType list>(parameterTypes, parameters |> List.map _.Type)
        Assert.Equal(returnType, actualReturn)
        Assert.Contains($"FidelityExtern(\"vecfixture\", \"farscape_cpp_container_IntVector_{suffix}\")", attributes)
    let noFactory = { vectorSpec with FactorySymbol = None }
    Assert.DoesNotContain("_acquire", FidelityCodeGenerator.generateCppTemplateContainerModule ctx vectorInventory noFactory)
    Assert.DoesNotContain("_acquire", CppClassAnalysis.renderCppTemplateContainerShimC "vector.hpp" vectorInventory noFactory)

/// The body of `containerNativeLibraryInventoryShimAndExecution` over the scratch directory
/// that Fact creates and deletes.
let private containerNativeLibraryInventoryShimAndExecutionIn (directory: string) =
    let path file = Path.Combine(directory, file)
    let run program arguments = runTemplateFixtureTool program arguments
    File.WriteAllText(path "vecfixture.hpp", """
#pragma once
#include <vector>
extern template class std::vector<int>;
extern "C" std::vector<int>* make_vector();
extern "C" int vector_releases() noexcept;
extern "C" int vector_buffer_releases() noexcept;
extern "C" void vector_factory_mode(int) noexcept;
    """)
    File.WriteAllText(path "vecfixture.cpp", """
#include "vecfixture.hpp"
#include <cstdlib>
#include <new>
#include <stdexcept>
template class std::vector<int>;
static void* owned = nullptr;
static void* buffer = nullptr;
static int releases = 0, buffer_releases = 0, factory_mode = 0;
// Observe deletion of the exact library-owned vector, including its buffer lifetime.
void operator delete(void* p) noexcept { if (p && p == buffer) { ++buffer_releases; buffer = nullptr; } if (p && p == owned) { ++releases; owned = nullptr; } std::free(p); }
extern "C" std::vector<int>* make_vector() {
  if (factory_mode==1) throw std::bad_alloc();
  if (factory_mode==2) throw std::runtime_error("factory");
  if (factory_mode==3) throw 42;
  if (factory_mode==4) return nullptr;
  auto* v = new std::vector<int>{10,20,30}; owned = v; buffer = v->data(); return v;
}
extern "C" void vector_factory_mode(int mode) noexcept { factory_mode = mode; }
extern "C" int vector_releases() noexcept { return releases; }
extern "C" int vector_buffer_releases() noexcept { return buffer_releases; }
    """)
    let library = path "libvecfixture.so"
    run "/usr/bin/clang++" [ "-std=c++17"; "-shared"; "-fPIC"; path "vecfixture.cpp"; "-o"; library ] |> ignore
    let inventory = CppClassAnalysis.inspectTemplateInventory library []
    let matched, build = CppClassAnalysis.requireInstantiation inventory "std::vector" ["int"; "std::allocator<int>"] None
    Assert.Equal("std::vector<int,std::allocator<int>>", matched.InstantiationName)
    Assert.Equal(library, build.LibraryPath)
    Assert.NotEmpty(matched.MangledSymbols)
    containerRefusal Diagnostics.CppTemplateInstantiationMissing (fun () ->
        CppClassAnalysis.validateTemplateContainerContract inventory { vectorSpec with ElementType = "double"; ElementClefType = "float" })
    let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 Map.empty
    File.WriteAllText(path "Containers.clef", FidelityCodeGenerator.generateCppTemplateContainerModule ctx inventory vectorSpec)
    File.WriteAllText(path "shim.cpp", CppClassAnalysis.renderCppTemplateContainerShimC (path "vecfixture.hpp") inventory vectorSpec)
    File.WriteAllText(path "runner.cpp", """
#include "vecfixture.hpp"
#include <cstdio>
extern "C" void* farscape_cpp_container_IntVector_acquire() noexcept;
extern "C" void farscape_cpp_container_IntVector_release(void*) noexcept;
extern "C" int farscape_cpp_container_IntVector_size(int*,void*) noexcept;
extern "C" int farscape_cpp_container_IntVector_get(int*,void*,int) noexcept;
extern "C" int farscape_cpp_container_IntVector_iter_next(int*,int*,void*) noexcept;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"check failed at %d: %s\n",__LINE__,#x); return 1; } } while (false)
int main() {
  for (int mode : {1,2,3,4}) { vector_factory_mode(mode); CHECK(!farscape_cpp_container_IntVector_acquire()); }
  vector_factory_mode(0);
  auto* h = farscape_cpp_container_IntVector_acquire(); CHECK(h);
  int size=-99, value=-99, index=0;
  CHECK(farscape_cpp_container_IntVector_size(&size,h)==0 && size==3);
  CHECK(farscape_cpp_container_IntVector_size(nullptr,h)==-1);
  CHECK(farscape_cpp_container_IntVector_size(&size,nullptr)==-1 && size==3);
  for (int i=0;i<3;++i) CHECK(farscape_cpp_container_IntVector_get(&value,h,i)==0 && value==(i+1)*10);
  for (int i : {-1,3,99}) { value=-99; CHECK(farscape_cpp_container_IntVector_get(&value,h,i)==4 && value==-99); }
  CHECK(farscape_cpp_container_IntVector_get(nullptr,h,0)==-1);
  CHECK(farscape_cpp_container_IntVector_get(&value,nullptr,0)==-1);
  for (int i=0;i<3;++i) CHECK(farscape_cpp_container_IntVector_iter_next(&value,&index,h)==0 && value==(i+1)*10 && index==i+1);
  value=-99; CHECK(farscape_cpp_container_IntVector_iter_next(&value,&index,h)==4 && value==-99 && index==3);
  index=-1; CHECK(farscape_cpp_container_IntVector_iter_next(&value,&index,h)==4 && index==-1);
  CHECK(farscape_cpp_container_IntVector_iter_next(nullptr,&index,h)==-1);
  CHECK(farscape_cpp_container_IntVector_iter_next(&value,nullptr,h)==-1);
  CHECK(farscape_cpp_container_IntVector_iter_next(&value,&index,nullptr)==-1);
  CHECK(vector_releases()==0);
  farscape_cpp_container_IntVector_release(h);
  CHECK(vector_releases()==1 && vector_buffer_releases()==1);
  farscape_cpp_container_IntVector_release(nullptr); CHECK(vector_releases()==1 && vector_buffer_releases()==1);
  std::puts("container-native-ok size=3 values=10,20,30 bounds=4 releases=1");
}
    """)
    let runner = path "runner"
    run "/usr/bin/clang++" [ "-std=c++17"; path "shim.cpp"; path "runner.cpp"; "-L" + directory; "-lvecfixture"; "-Wl,-rpath,$ORIGIN"; "-o"; runner ] |> ignore
    let result = run runner []
    File.WriteAllText(path "native-result.txt", result)
    Assert.Contains("container-native-ok size=3 values=10,20,30 bounds=4 releases=1", result)

[<Fact>]
let containerNativeLibraryInventoryShimAndExecution () =
    let directory = TestHelpers.TestScratch.create "cpp-container"
    try containerNativeLibraryInventoryShimAndExecutionIn directory
    finally Directory.Delete(directory, true)

// ─── Fidelity.Platform and BAREWire zero-copy shared buffers (#123) ───

let private sharedTileFields : CppClassAnalysis.SharedBufferFieldSpec list =
    [ { Name = "sequence"; CType = "uint32_t"; ClefType = "int"; ByteOffset = 0; ByteWidth = 4; IsVolatile = false }
      { Name = "accumulator"; CType = "int64_t"; ClefType = "int"; ByteOffset = 8; ByteWidth = 8; IsVolatile = false }
      { Name = "scale"; CType = "int32_t"; ClefType = "int"; ByteOffset = 16; ByteWidth = 4; IsVolatile = false }
      { Name = "checksum"; CType = "uint32_t"; ClefType = "int"; ByteOffset = 20; ByteWidth = 4; IsVolatile = false } ]

let private sharedTileLayouts : Map<string, CppClassAnalysis.StructLayout> =
    Map.ofList [
        "SharedTensorTile", mkLayout "SharedTensorTile" 192 64 [ 0; 64; 128; 160 ]
    ]

let private sharedTileLifecycle : CppClassAnalysis.SharedBufferOperation list =
    [ CppClassAnalysis.SharedBufferOperation.MapRegion
      CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
      CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "accumulator")
      CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
      CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 4)
      CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
      CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 3
      CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
      CppClassAnalysis.SharedBufferOperation.ClefRead (0, "accumulator")
      CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 4)
      CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
      CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
      CppClassAnalysis.SharedBufferOperation.UnmapRegion
      CppClassAnalysis.SharedBufferOperation.ScopeExit ]

let private fileMappedLifecycle : CppClassAnalysis.SharedBufferOperation list =
    [ CppClassAnalysis.SharedBufferOperation.MapRegion
      CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
      CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 4)
      CppClassAnalysis.SharedBufferOperation.ClefRead (3, "accumulator")
      CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
      CppClassAnalysis.SharedBufferOperation.UnmapRegion
      CppClassAnalysis.SharedBufferOperation.ScopeExit ]

let private sharedTileSpec : CppClassAnalysis.SharedBufferSpec =
    { StructName = "SharedTensorTile"
      SafeName = "SharedTensorTileBuffer"
      Namespace = "Test.SharedBuffers"
      LibraryName = "sharedtile"
      HeaderFile = "shared_tile.hpp"
      MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.HostShared
      SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadWrite
      ByteOrder = "LittleEndian"
      SpaceOffset = 0L
      MeasuredSize = 24
      MeasuredAlignment = 8
      ElementCount = 4
      RegionByteExtent = 96
      RegionAlignment = 64
      RegionLifetimeDepth = 1
      LibraryRetentionDepth = 2
      Lifecycle = sharedTileLifecycle
      VerifiedLibraryPath = None
      Fields = sharedTileFields
      LibraryTransformSymbol = Some "shared_tile_library_transform_in_place"
      Source = Some (Diagnostics.Located.Unknown "shared buffer fixture") }

let private sharedBufferRefusal code action =
    let error = Assert.Throws<Diagnostics.RefusalException>(System.Action action)
    let diagnostic = only error.Refusal
    Assert.Equal(code, diagnostic.Code)
    match sharedTileSpec.Source with
    | Some expectedSource -> Assert.Equal(expectedSource, diagnostic.Source)
    | None -> Assert.Fail("Expected Source on sharedTileSpec")
    Assert.False(System.String.IsNullOrWhiteSpace diagnostic.Message)

[<Fact>]
let sharedBufferContractRefusesUnmeasuredMismatchedLayoutAlignmentAndRawPointers () =
    CppClassAnalysis.validateSharedBufferContract sharedTileLayouts sharedTileSpec
    // Unmeasured struct in targeted library build
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract Map.empty sharedTileSpec)
    // Size, alignment, field count, and field offset mismatches against clang measured layout
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with MeasuredSize = 32; RegionByteExtent = 128 })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with MeasuredAlignment = 4 })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with Fields = sharedTileFields.Tail })
    let wrongOffsetFields =
        sharedTileFields |> List.map (fun f -> if f.Name = "accumulator" then { f with ByteOffset = 4 } else f)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with Fields = wrongOffsetFields })
    // Placement alignment smaller than measured alignment, larger than 4096, or non-power-of-two, unaligned/negative space offset, lifecycle mutation mismatch, or byte-order mismatch
    for badAlign in [ 0; 3; 4; 12; -8; 8192 ] do
        sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
            CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with RegionAlignment = badAlign })
    for badOffset in [ -64L; 8L; 32L ] do
        sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
            CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with SpaceOffset = badOffset })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with LibraryTransformSymbol = None })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with Lifecycle = fileMappedLifecycle })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with ByteOrder = "BigEndian" })
    // Raw pointer/address tokens or empty names refused with FS9220
    for badType in [ ""; "nativeint"; "nativeptr<uint32>"; "uint32_t*"; "int&"; "Ptr<uint32>" ] do
        let badFields = { sharedTileFields.Head with CType = badType } :: sharedTileFields.Tail
        sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
            CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with Fields = badFields })
    // Unverified exports (VerifiedLibraryPath = None or missing binary) refused before emitting SharedBufferPlacement
    let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 sharedTileLayouts
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx sharedTileSpec |> ignore)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { sharedTileSpec with VerifiedLibraryPath = Some "/nonexistent/libsharedtile.so" } |> ignore)

[<Fact>]
let sharedBufferExtentRefusesUnderallocatedOverflowingAndOutOfBoundsAccess () =
    for badSpec in [
        { sharedTileSpec with ElementCount = 0 }
        { sharedTileSpec with ElementCount = -2 }
        { sharedTileSpec with MeasuredSize = 0 }
        { sharedTileSpec with ElementCount = System.Int32.MaxValue; RegionByteExtent = System.Int32.MaxValue }
        { sharedTileSpec with RegionByteExtent = 95 }
        { sharedTileSpec with RegionByteExtent = 24 }
        { sharedTileSpec with SpaceOffset = System.Int64.MaxValue - 63L }
        { sharedTileSpec with
            Lifecycle =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (-1, "sequence")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 4)
                  CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion ] }
        { sharedTileSpec with
            Lifecycle =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefWrite (4, "sequence")
                  CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 4)
                  CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
                  CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion ] } ] do
        sharedBufferRefusal Diagnostics.SharedBufferExtentInvalid (fun () ->
            CppClassAnalysis.validateSharedBufferContract sharedTileLayouts badSpec)
    let verify ops = CppClassAnalysis.verifySharedBufferLifecycle sharedTileLayouts sharedTileSpec ops
    for badOp in [
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ClefRead (-1, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ClefWrite (4, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 0)
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 5)
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
          CppClassAnalysis.SharedBufferOperation.LibraryMutate 2
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
          CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 3
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ] ] do
        sharedBufferRefusal Diagnostics.SharedBufferExtentInvalid (fun () -> verify badOp)

[<Fact>]
let sharedBufferLifetimeRefusesShortRetentionPrematureUnmapUseAfterUnmapAndLeaks () =
    for badLifetimeSpec in [
        { sharedTileSpec with RegionLifetimeDepth = 0 }
        { sharedTileSpec with LibraryRetentionDepth = 0 }
        { sharedTileSpec with RegionLifetimeDepth = 3; LibraryRetentionDepth = 1 }
        { sharedTileSpec with Lifecycle = [] }
        { sharedTileSpec with
            Lifecycle =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.UnmapRegion ] }
        { sharedTileSpec with
            Lifecycle =
                [ CppClassAnalysis.SharedBufferOperation.MapRegion
                  CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence") ] } ] do
        sharedBufferRefusal Diagnostics.SharedBufferLifetimeInvalid (fun () ->
            CppClassAnalysis.validateSharedBufferContract sharedTileLayouts badLifetimeSpec)
    let verify ops = CppClassAnalysis.verifySharedBufferLifecycle sharedTileLayouts sharedTileSpec ops
    for badLifecycle in [
        // Use before MapRegion
        [ CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence") ]
        [ CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2) ]
        [ CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // Unmap while library still retains an active shared view
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // Use after UnmapRegion
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.UnmapRegion
          CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence") ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.UnmapRegion
          CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence") ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.UnmapRegion
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // Unshare when no share is active
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // Leaked active share or mapped region at ScopeExit / end of operations
        [ CppClassAnalysis.SharedBufferOperation.MapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ScopeExit ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.ScopeExit ]
        // Missing ShareToLibrary/UnshareFromLibrary window or LastUseDepth exceeding LibraryRetentionEndDepth
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // Operations after terminal ScopeExit
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.UnmapRegion
          CppClassAnalysis.SharedBufferOperation.ScopeExit
          CppClassAnalysis.SharedBufferOperation.MapRegion ] ] do
        sharedBufferRefusal Diagnostics.SharedBufferLifetimeInvalid (fun () -> verify badLifecycle)

[<Fact>]
let sharedBufferAccessRefusesConcurrentClefMutationReadOnlyMutationAndOverlappingShares () =
    let verify spec ops = CppClassAnalysis.verifySharedBufferLifecycle sharedTileLayouts spec ops
    sharedBufferRefusal Diagnostics.SharedBufferConflictingAccess (fun () ->
        CppClassAnalysis.validateSharedBufferContract sharedTileLayouts { sharedTileSpec with SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadOnly })
    // Valid bidirectional lifecycle (Clef -> Library -> Clef + concurrent read-only share)
    verify sharedTileSpec [
        CppClassAnalysis.SharedBufferOperation.MapRegion
        CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
        CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "accumulator")
        CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
        CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 4)
        CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
        CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 0
        CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
        CppClassAnalysis.SharedBufferOperation.ClefRead (0, "accumulator")
        CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 4)
        CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
        CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
        CppClassAnalysis.SharedBufferOperation.UnmapRegion
        CppClassAnalysis.SharedBufferOperation.ScopeExit ]
    // Conflicting accesses refused with FS9223
    for badOps in [
        // ClefWrite while library holds an active shared view (even SharedReadOnly)
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // ClefRead while library holds SharedReadWrite or SharedExclusiveMutable
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
          CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedExclusiveMutable, 2)
          CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // LibraryMutate without active share or under SharedReadOnly share
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.LibraryMutate 0
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ]
        // Overlapping ShareToLibrary while already shared
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion ] ] do
        sharedBufferRefusal Diagnostics.SharedBufferConflictingAccess (fun () -> verify sharedTileSpec badOps)
    // SharedReadOnly placement refuses ClefWrite and mutable ShareToLibrary
    let readOnlySpec =
        { sharedTileSpec with
            SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadOnly
            Lifecycle = fileMappedLifecycle
            LibraryTransformSymbol = None }
    sharedBufferRefusal Diagnostics.SharedBufferConflictingAccess (fun () ->
        verify readOnlySpec [
            CppClassAnalysis.SharedBufferOperation.MapRegion
            CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
            CppClassAnalysis.SharedBufferOperation.UnmapRegion ])
    sharedBufferRefusal Diagnostics.SharedBufferConflictingAccess (fun () ->
        verify readOnlySpec [
            CppClassAnalysis.SharedBufferOperation.MapRegion
            CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
            CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
            CppClassAnalysis.SharedBufferOperation.UnmapRegion ])

/// The body of `sharedBufferDeclsAndNativeZeroCopyBidirectionalExecution` over the scratch
/// directory that Fact creates and deletes.
let private sharedBufferDeclsAndNativeZeroCopyBidirectionalExecutionIn (directory: string) =
    let path file = Path.Combine(directory, file)
    let run program arguments = runTemplateFixtureTool program arguments
    let headerPath = path "shared_tile.hpp"
    File.WriteAllText(headerPath, """
#pragma once
#include <cstdint>
#include <cstddef>

struct SharedTensorTile {
    std::uint32_t sequence;
    std::int64_t accumulator;
    std::int32_t scale;
    std::uint32_t checksum;
};

extern "C" int shared_tile_library_transform_in_place(
    SharedTensorTile* base,
    int count,
    int index,
    int delta,
    std::uintptr_t* out_library_addr,
    int* inout_copy_count) noexcept;

extern "C" int shared_tile_library_copy_and_transform(
    const SharedTensorTile* src,
    int count,
    int index,
    int delta,
    std::uintptr_t* out_library_addr,
    int* inout_copy_count) noexcept;

extern "C" int shared_tile_library_observed_copies() noexcept;
extern "C" std::uintptr_t shared_tile_library_last_address() noexcept;
    """)
    File.WriteAllText(path "shared_tile.cpp", """
#include "shared_tile.hpp"
#include <cstring>

static int g_observed_copies = 0;
static std::uintptr_t g_last_address = 0;
static SharedTensorTile g_staging_copy[16];

extern "C" int shared_tile_library_transform_in_place(
    SharedTensorTile* base,
    int count,
    int index,
    int delta,
    std::uintptr_t* out_library_addr,
    int* inout_copy_count) noexcept {
    if (!base || !out_library_addr || !inout_copy_count) return -1;
    if (index < 0 || index >= count) return 4;
    if (base == g_staging_copy) {
        ++g_observed_copies;
        ++(*inout_copy_count);
    }
    g_last_address = reinterpret_cast<std::uintptr_t>(base);
    *out_library_addr = g_last_address;
    SharedTensorTile& tile = base[index];
    tile.sequence += static_cast<std::uint32_t>(delta);
    tile.accumulator += static_cast<std::int64_t>(delta) * static_cast<std::int64_t>(tile.scale);
    tile.checksum = tile.sequence ^ static_cast<std::uint32_t>(tile.accumulator & 0xFFFFFFFFLL);
    return 0;
}

extern "C" int shared_tile_library_copy_and_transform(
    const SharedTensorTile* src,
    int count,
    int index,
    int delta,
    std::uintptr_t* out_library_addr,
    int* inout_copy_count) noexcept {
    if (!src || !out_library_addr || !inout_copy_count) return -1;
    if (count <= 0 || count > 16 || index < 0 || index >= count) return 4;
    std::memcpy(g_staging_copy, src, static_cast<std::size_t>(count) * sizeof(SharedTensorTile));
    return shared_tile_library_transform_in_place(g_staging_copy, count, index, delta, out_library_addr, inout_copy_count);
}

extern "C" int shared_tile_library_observed_copies() noexcept {
    return g_observed_copies;
}

extern "C" std::uintptr_t shared_tile_library_last_address() noexcept {
    return g_last_address;
}
    """)
    // Measure layout from the real targeted library header via clang -fdump-record-layouts-simple
    let measuredLayouts =
        match CppParser.extractStructLayouts headerPath [] [] [ "SharedTensorTile" ] false with
        | Ok layouts -> layouts
        | Error err ->
            Assert.Fail($"Failed to extract clang layout for SharedTensorTile: {err}")
            Map.empty
    let measuredTile = measuredLayouts["SharedTensorTile"]
    Assert.Equal(192, measuredTile.SizeBits)
    Assert.Equal(64, measuredTile.AlignmentBits)
    Assert.Equal<int list>([ 0; 64; 128; 160 ], measuredTile.FieldOffsetsBits)
    let baseHostSpec = { sharedTileSpec with HeaderFile = headerPath }
    let deviceBoundedLifecycle =
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ClefWrite (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.ClefWrite (1, "sequence")
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadWrite, 2)
          CppClassAnalysis.SharedBufferOperation.LibraryMutate 1
          CppClassAnalysis.SharedBufferOperation.SyncFromLibrary 1
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.ClefRead (1, "sequence")
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.ClefRead (1, "sequence")
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion
          CppClassAnalysis.SharedBufferOperation.ScopeExit ]
    let baseDeviceSpec =
        { baseHostSpec with
            SafeName = "DeviceTensorTileBuffer"
            Namespace = "Test.DeviceSharedBuffers"
            MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.DeviceUnified
            SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadWrite
            Lifecycle = deviceBoundedLifecycle }
    let baseFileMappedSpec =
        { baseHostSpec with
            SafeName = "FileTensorTileBuffer"
            Namespace = "Test.FileSharedBuffers"
            MemorySpace = CppClassAnalysis.SharedMemorySpaceKind.FileMapped
            SpaceAccess = CppClassAnalysis.SharedAccessMode.SharedReadOnly
            SpaceOffset = 64L
            Lifecycle = fileMappedLifecycle
            LibraryTransformSymbol = None }
    let hostShimSource = CppClassAnalysis.renderSharedBufferZeroCopyShimC baseHostSpec
    let deviceShimSource = CppClassAnalysis.renderSharedBufferZeroCopyShimC baseDeviceSpec
    let fileShimSource = CppClassAnalysis.renderSharedBufferZeroCopyShimC baseFileMappedSpec
    Assert.DoesNotContain("MAP_ANONYMOUS", fileShimSource)
    Assert.Contains("MAP_SHARED, fd, 0", fileShimSource)
    Assert.DoesNotContain("FileTensorTileBuffer_library_transform_in_place", fileShimSource)
    Assert.DoesNotContain("4096u", hostShimSource)
    File.WriteAllText(path "shim_host.cpp", hostShimSource)
    File.WriteAllText(path "shim_device.cpp", deviceShimSource)
    File.WriteAllText(path "shim_file.cpp", fileShimSource)
    let library = path "libsharedtile.so"
    // First compile without shims to verify validateSharedBufferLibraryExports and generateSharedBufferDecls refuse missing FFI exports with FS9220
    let incompleteLibrary = path "libsharedtile_incomplete.so"
    run "/usr/bin/clang++" [ "-std=c++17"; "-shared"; "-fPIC"; path "shared_tile.cpp"; "-o"; incompleteLibrary ] |> ignore
    let ctx = FidelityCodeGenerator.buildGenerationContext [] Types.LP64 measuredLayouts
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports incompleteLibrary measuredLayouts baseHostSpec)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { baseHostSpec with VerifiedLibraryPath = Some incompleteLibrary } |> ignore)
    // Compile targeted library with generated HostShared, DeviceUnified and FileMapped zero-copy shims so libsharedtile.so exports all FidelityExtern symbols
    run "/usr/bin/clang++" [ "-std=c++17"; "-shared"; "-fPIC"; path "shared_tile.cpp"; path "shim_host.cpp"; path "shim_device.cpp"; path "shim_file.cpp"; "-o"; library ] |> ignore
    let hostSpec = { baseHostSpec with VerifiedLibraryPath = Some library }
    let deviceSpec = { baseDeviceSpec with VerifiedLibraryPath = Some library }
    let fileMappedSpec = { baseFileMappedSpec with VerifiedLibraryPath = Some library }
    CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts hostSpec
    CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts deviceSpec
    CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts fileMappedSpec
    // Isolated filename-stem, version-suffix, truncated/malformed ELF, and unavailable symbol-tool checks
    let wrongStemLibrary = path "libwrongstem.so"
    let badSuffixLibrary = path "libsharedtile.so.bak"
    let validVersionedLibrary = path "libsharedtile.so.1.2"
    let truncatedLibrary = path "libsharedtile_trunc.so"
    let invalidMagicLibrary = path "libsharedtile_badmagic.so"
    let malformedElfLibrary = path "libsharedtile_malformed.so"
    File.Copy(library, wrongStemLibrary, true)
    File.Copy(library, badSuffixLibrary, true)
    File.Copy(library, validVersionedLibrary, true)
    File.WriteAllBytes(truncatedLibrary, [| 0x7Fuy; 0x45uy; 0x4Cuy |])
    File.WriteAllBytes(invalidMagicLibrary, System.Text.Encoding.ASCII.GetBytes("NOT_AN_ELF_FILE!"))
    File.WriteAllBytes(malformedElfLibrary, [| 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |])
    CppClassAnalysis.validateSharedBufferLibraryExports validVersionedLibrary measuredLayouts hostSpec
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports wrongStemLibrary measuredLayouts hostSpec)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports badSuffixLibrary measuredLayouts hostSpec)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports truncatedLibrary measuredLayouts hostSpec)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports invalidMagicLibrary measuredLayouts hostSpec)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports malformedElfLibrary measuredLayouts hostSpec)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExportsWithTool "/nonexistent/readelf" library measuredLayouts hostSpec)
    // Contract fingerprint and library stem binding refuses a spec whose LibraryName, SpaceOffset, access mode, lifecycle, or field IsVolatile flag disagrees with the compiled shim
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts { hostSpec with LibraryName = "wronglib" })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { hostSpec with LibraryName = "wronglib" } |> ignore)
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts { fileMappedSpec with SpaceOffset = 0L })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { fileMappedSpec with SpaceOffset = 0L } |> ignore)
    let alteredFileLifecycle =
        [ CppClassAnalysis.SharedBufferOperation.MapRegion
          CppClassAnalysis.SharedBufferOperation.ClefRead (0, "sequence")
          CppClassAnalysis.SharedBufferOperation.ShareToLibrary (CppClassAnalysis.SharedAccessMode.SharedReadOnly, 2)
          CppClassAnalysis.SharedBufferOperation.ClefRead (1, "accumulator")
          CppClassAnalysis.SharedBufferOperation.UnshareFromLibrary
          CppClassAnalysis.SharedBufferOperation.UnmapRegion
          CppClassAnalysis.SharedBufferOperation.ScopeExit ]
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts { fileMappedSpec with Lifecycle = alteredFileLifecycle })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { fileMappedSpec with Lifecycle = alteredFileLifecycle } |> ignore)
    let volatileMismatchFields = { sharedTileFields.Head with IsVolatile = true } :: sharedTileFields.Tail
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        CppClassAnalysis.validateSharedBufferLibraryExports library measuredLayouts { hostSpec with Fields = volatileMismatchFields })
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { hostSpec with Fields = volatileMismatchFields } |> ignore)
    let hostFacts = CppClassAnalysis.deriveSharedBufferPlacementFacts measuredLayouts hostSpec
    Assert.Equal(1, hostFacts.MappedDepth)
    Assert.Equal(2, hostFacts.FirstUseDepth)
    Assert.Equal(11, hostFacts.LastUseDepth)
    Assert.Equal(12, hostFacts.LibraryRetentionEndDepth)
    Assert.Equal(13, hostFacts.UnmapDepth)
    Assert.Equal(false, hostFacts.LeakedAtExit)
    Assert.Equal(false, hostFacts.ConcurrentClefMutation)
    Assert.Equal(true, hostFacts.LibraryMutatesBuffer)
    Assert.Equal(false, hostFacts.OverlappingMutableShare)
    Assert.Equal(3, hostFacts.MaxAccessedElementIndex)
    Assert.Equal(true, hostFacts.HasVerifiedExports)
    let deviceFacts = CppClassAnalysis.deriveSharedBufferPlacementFacts measuredLayouts deviceSpec
    Assert.Equal(1, deviceFacts.MappedDepth)
    Assert.Equal(2, deviceFacts.FirstUseDepth)
    Assert.Equal(10, deviceFacts.LastUseDepth)
    Assert.Equal(11, deviceFacts.LibraryRetentionEndDepth)
    Assert.Equal(12, deviceFacts.UnmapDepth)
    Assert.Equal(1, deviceFacts.MaxAccessedElementIndex)
    Assert.Equal(true, deviceFacts.HasVerifiedExports)
    let fileFacts = CppClassAnalysis.deriveSharedBufferPlacementFacts measuredLayouts fileMappedSpec
    Assert.Equal(1, fileFacts.MappedDepth)
    Assert.Equal(2, fileFacts.FirstUseDepth)
    Assert.Equal(4, fileFacts.LastUseDepth)
    Assert.Equal(5, fileFacts.LibraryRetentionEndDepth)
    Assert.Equal(6, fileFacts.UnmapDepth)
    Assert.Equal(false, fileFacts.LeakedAtExit)
    Assert.Equal(false, fileFacts.ConcurrentClefMutation)
    Assert.Equal(false, fileFacts.LibraryMutatesBuffer)
    Assert.Equal(false, fileFacts.OverlappingMutableShare)
    Assert.Equal(3, fileFacts.MaxAccessedElementIndex)
    Assert.Equal(true, fileFacts.HasVerifiedExports)
    let mismatchedClefType = { sharedTileFields.Head with ClefType = "int64" } :: sharedTileFields.Tail
    sharedBufferRefusal Diagnostics.SharedBufferContractInvalid (fun () ->
        FidelityCodeGenerator.generateSharedBufferDecls ctx { hostSpec with Fields = mismatchedClefType } |> ignore)
    let declarations = FidelityCodeGenerator.generateSharedBufferDecls ctx hostSpec
    Assert.Contains(CodeAST.OpaqueMarker "SharedTensorTileBuffer", declarations)
    Assert.DoesNotContain(declarations, function CodeAST.RecordType _ -> true | _ -> false)
    let externBodies =
        declarations
        |> List.choose (function CodeAST.LetBinding(_, _, _, body, attrs) when not attrs.IsEmpty -> Some body | _ -> None)
    Assert.NotEmpty(externBodies)
    Assert.All(externBodies, fun body -> Assert.Equal(CodeAST.NativeZeroed, body))
    let clefSource = FidelityCodeGenerator.generateSharedBufferModule ctx hostSpec
    let deviceClefSource = FidelityCodeGenerator.generateSharedBufferModule ctx deviceSpec
    let fileClefSource = FidelityCodeGenerator.generateSharedBufferModule ctx fileMappedSpec
    File.WriteAllText(path "SharedBuffers.clef", clefSource)
    File.WriteAllText(path "DeviceSharedBuffers.clef", deviceClefSource)
    File.WriteAllText(path "FileSharedBuffers.clef", fileClefSource)
    for source in [ clefSource; deviceClefSource; fileClefSource ] do
        for forbidden in [ "nativeint"; "nativeptr"; "Ptr<"; "box "; "box("; ": obj"; "obj array"; "null"; "FidelityExtern.invoke"; "DllImport"; "Marshal"; "UnmanagedFunctionPointer"; "\"LittleEndian\"" ] do
            Assert.DoesNotContain(forbidden, source)
        Assert.Contains("NativeDefault.zeroed ()", source)
    Assert.Contains(CppClassAnalysis.sharedBufferContractSymbol hostSpec, clefSource)
    Assert.Contains(CppClassAnalysis.sharedBufferContractSymbol deviceSpec, deviceClefSource)
    Assert.Contains(CppClassAnalysis.sharedBufferContractSymbol fileMappedSpec, fileClefSource)
    for expected in [
        "open BAREWire.Hardware"
        "open BAREWire.Descriptors"
        "open BAREWire.Platform"
        "Fidelity.Platform.HostShared"
        "MemoryRegionKind.SRAM"
        "ByteOrder = \"little\""
        "LayoutByteOrder = \"little\""
        "LibraryMutatesBuffer = true"
        "SpaceOffset = 0L\n"
        "MappedDepth = 1\n"
        "FirstUseDepth = 2\n"
        "LastUseDepth = 11\n"
        "LibraryRetentionEndDepth = 12\n"
        "UnmapDepth = 13\n"
        "MaxAccessedElementIndex = 3 }"
        "PeripheralDescriptor"
        "MemorySpaceDescriptor"
        "StructDescriptor"
        "ViewLayoutDescriptor"
        "SharedBufferPlacement"
        "SharedBufferRegionDescriptor"
        "Count = 1"
        "BitFields = [||]"
        "sequenceOffset : int = 0"
        "accumulatorOffset : int = 8"
        "scaleOffset : int = 16"
        "checksumOffset : int = 20"
        "OwnedNullableReturn"
        "Test.SharedBuffers.SharedTensorTileBuffer_unmap"
        "SharedTensorTileBuffer_share_to_library"
        "SharedTensorTileBuffer_unshare_from_library"
        "SharedTensorTileBuffer_library_transform_in_place"
        "SharedTensorTileBuffer_memory_space_kind"
        "SharedTensorTileBuffer_clef_read_accumulator"
        "SharedTensorTileBuffer_clef_write_accumulator" ] do
        Assert.Contains(expected, clefSource)
    for expected in [
        "Fidelity.Platform.DeviceUnified"
        "MemoryRegionKind.SRAM"
        "ByteOrder = \"little\""
        "LayoutByteOrder = \"little\""
        "LibraryMutatesBuffer = true"
        "MaxAccessedElementIndex = 1 }"
        "DeviceTensorTileBuffer_map"
        "DeviceTensorTileBuffer_memory_space_kind" ] do
        Assert.Contains(expected, deviceClefSource)
    Assert.DoesNotContain("MemoryRegionKind.DMA", deviceClefSource)
    for expected in [
        "Fidelity.Platform.FileMapped"
        "MemoryRegionKind.SRAM"
        "Access = \"ReadOnly\""
        "AccessKind.ReadOnly"
        "ByteOrder = \"little\""
        "LayoutByteOrder = \"little\""
        "LibraryMutatesBuffer = false"
        "SpaceOffset = 64L\n"
        "MappedDepth = 1\n"
        "FirstUseDepth = 2\n"
        "LastUseDepth = 4\n"
        "LibraryRetentionEndDepth = 5\n"
        "UnmapDepth = 6\n"
        "MaxAccessedElementIndex = 3 }"
        "ForeignUses = [||]"
        "FileTensorTileBuffer_map"
        "let FileTensorTileBuffer_map (fd: int)"
        "Name = \"fd\""
        "FileTensorTileBuffer_memory_space_kind"
        "FileTensorTileBuffer_clef_read_accumulator" ] do
        Assert.Contains(expected, fileClefSource)
    Assert.DoesNotContain("MemoryRegionKind.Flash", fileClefSource)
    Assert.DoesNotContain("FileTensorTileBuffer_clef_write_", fileClefSource)
    Assert.DoesNotContain("FileTensorTileBuffer_library_transform_in_place", fileClefSource)
    for (generatedPath, modulePrefix) in [
        path "SharedBuffers.clef", "Test.SharedBuffers.SharedTensorTileBuffer"
        path "DeviceSharedBuffers.clef", "Test.DeviceSharedBuffers.DeviceTensorTileBuffer"
        path "FileSharedBuffers.clef", "Test.FileSharedBuffers.FileTensorTileBuffer" ] do
        match Substantiation.inspectSource Set.empty generatedPath with
        | Ok digest ->
            Assert.NotEmpty(digest.CompileTimeDeclarations)
            Assert.NotEmpty(digest.ForeignDeclarations)
            for descSym in [ "Descriptor"; "PeripheralDescriptor"; "MemorySpaceDescriptor"; "ViewLayoutDescriptor"; "PlacementDescriptor"; "RegionDescriptor" ] do
                Assert.Contains(modulePrefix + "." + descSym, digest.CompileTimeDeclarations)
        | Error refusal ->
            Assert.Fail(refusal.Text)

    let shortBackingPath = (path "short_tile.bin").Replace("\\", "/")
    let validBackingPath = (path "valid_tile.bin").Replace("\\", "/")
    let hostContractSymbol = CppClassAnalysis.sharedBufferContractSymbol hostSpec
    let runnerSource =
        ("""
#include "shared_tile.hpp"
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <fcntl.h>
#include <unistd.h>

extern "C" void* farscape_shared_buffer_SharedTensorTileBuffer_map() noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_unmap(void*) noexcept;
extern "C" int __HOST_CONTRACT_SYM__(void*) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(void*, int, std::uint32_t) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_read_sequence(std::uint32_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_write_accumulator(void*, int, std::int64_t) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_read_accumulator(std::int64_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_write_scale(void*, int, std::int32_t) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_read_scale(std::int32_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_write_checksum(void*, int, std::uint32_t) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_read_checksum(std::uint32_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_share_to_library(void*, int, int) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_unshare_from_library(void*) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place(void*, int, int) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_clef_address_identity(std::uintptr_t*, void*) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_library_address_identity(std::uintptr_t*, void*) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_copy_count(int*, void*) noexcept;
extern "C" int farscape_shared_buffer_SharedTensorTileBuffer_memory_space_kind(int*, void*) noexcept;

extern "C" void* farscape_shared_buffer_DeviceTensorTileBuffer_map() noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_unmap(void*) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_memory_space_kind(int*, void*) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_clef_write_sequence(void*, int, std::uint32_t) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_clef_read_sequence(std::uint32_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_share_to_library(void*, int, int) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_unshare_from_library(void*) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_library_transform_in_place(void*, int, int) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_clef_address_identity(std::uintptr_t*, void*) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_library_address_identity(std::uintptr_t*, void*) noexcept;
extern "C" int farscape_shared_buffer_DeviceTensorTileBuffer_copy_count(int*, void*) noexcept;

extern "C" void* farscape_shared_buffer_FileTensorTileBuffer_map(int) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_unmap(void*) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_memory_space_kind(int*, void*) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_clef_write_sequence(void*, int, std::uint32_t) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_clef_read_sequence(std::uint32_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_clef_read_accumulator(std::int64_t*, void*, int) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_share_to_library(void*, int, int) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_unshare_from_library(void*) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_clef_address_identity(std::uintptr_t*, void*) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_library_address_identity(std::uintptr_t*, void*) noexcept;
extern "C" int farscape_shared_buffer_FileTensorTileBuffer_copy_count(int*, void*) noexcept;

#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "check failed at %d: %s\n", __LINE__, #x); return 1; } } while (false)

int main() {
    void* h = farscape_shared_buffer_SharedTensorTileBuffer_map();
    CHECK(h != nullptr);
    CHECK(__HOST_CONTRACT_SYM__(h) == 0);
    int space_kind = 0;
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_memory_space_kind(&space_kind, h) == 0 && space_kind == 1);

    void* dev_h = farscape_shared_buffer_DeviceTensorTileBuffer_map();
    CHECK(dev_h != nullptr);
    int dev_space_kind = 0;
    std::uint32_t dev_seq1 = 0u;
    std::uintptr_t devClefAddr = 0, devLibAddr = 0;
    int devCopies = -1;
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_memory_space_kind(&dev_space_kind, dev_h) == 0 && dev_space_kind == 2);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_clef_write_sequence(dev_h, 0, 10u) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_clef_write_sequence(dev_h, 1, 55u) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_clef_write_sequence(dev_h, 2, 55u) == 4);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_share_to_library(dev_h, 3, 3) == 4);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_share_to_library(dev_h, 3, 2) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_library_transform_in_place(dev_h, 1, 3) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_unshare_from_library(dev_h) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_clef_read_sequence(&dev_seq1, dev_h, 1) == 0 && dev_seq1 == 58u);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_share_to_library(dev_h, 1, 2) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_clef_read_sequence(&dev_seq1, dev_h, 1) == 0 && dev_seq1 == 58u);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_unshare_from_library(dev_h) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_clef_address_identity(&devClefAddr, dev_h) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_library_address_identity(&devLibAddr, dev_h) == 0);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_copy_count(&devCopies, dev_h) == 0);
    CHECK(devClefAddr != 0 && devClefAddr == devLibAddr && devCopies == 0);

    // FileMapped requires a real file descriptor whose fstat size covers SpaceOffset (64) + RegionByteExtent (4 * sizeof(SharedTensorTile) = 96) = 160 bytes.
    CHECK(farscape_shared_buffer_FileTensorTileBuffer_map(-1) == nullptr);
    {
        // A 96-byte file covers RegionByteExtent alone but is smaller than SpaceOffset (64) + RegionByteExtent (96) = 160 bytes.
        int short_wfd = ::open("__SHORT_BACKING__", O_CREAT | O_TRUNC | O_WRONLY, 0600);
        CHECK(short_wfd >= 0);
        SharedTensorTile short_tiles[4];
        std::memset(short_tiles, 0x55, sizeof(short_tiles));
        CHECK(::write(short_wfd, short_tiles, sizeof(short_tiles)) == static_cast<ssize_t>(sizeof(short_tiles)));
        ::close(short_wfd);
        int short_rfd = ::open("__SHORT_BACKING__", O_RDONLY);
        CHECK(short_rfd >= 0);
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_map(short_rfd) == nullptr);
        ::close(short_rfd);
    }
    {
        unsigned char prefix_pad[64];
        std::memset(prefix_pad, 0xAA, sizeof(prefix_pad));
        SharedTensorTile backing_tiles[4];
        std::memset(backing_tiles, 0, sizeof(backing_tiles));
        backing_tiles[0].sequence = 4242u;
        backing_tiles[0].accumulator = 9001LL;
        backing_tiles[1].sequence = 8484u;
        backing_tiles[1].accumulator = -333LL;
        backing_tiles[3].accumulator = 7777LL;
        int valid_wfd = ::open("__VALID_BACKING__", O_CREAT | O_TRUNC | O_WRONLY, 0600);
        CHECK(valid_wfd >= 0);
        CHECK(::write(valid_wfd, prefix_pad, sizeof(prefix_pad)) == static_cast<ssize_t>(sizeof(prefix_pad)));
        CHECK(::write(valid_wfd, backing_tiles, sizeof(backing_tiles)) == static_cast<ssize_t>(sizeof(backing_tiles)));
        ::close(valid_wfd);

        int valid_rfd = ::open("__VALID_BACKING__", O_RDONLY);
        CHECK(valid_rfd >= 0);
        void* file_h = farscape_shared_buffer_FileTensorTileBuffer_map(valid_rfd);
        CHECK(file_h != nullptr);
        int file_space_kind = 0;
        std::uint32_t file_seq0 = 0u;
        std::int64_t file_acc3 = 0LL;
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_memory_space_kind(&file_space_kind, file_h) == 0 && file_space_kind == 3);
        // Negative refusal check (does not mutate state): read-only FileMapped space rejects writes with StatusAccessConflict (6).
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_clef_write_sequence(file_h, 0, 1u) == 6);
        // Step 2 of fileMappedLifecycle: ClefRead(0, "sequence") before ShareToLibrary.
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_clef_read_sequence(&file_seq0, file_h, 0) == 0 && file_seq0 == 4242u);
        // Step 3 of fileMappedLifecycle: ShareToLibrary(SharedReadOnly, 4).
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_share_to_library(file_h, 1, 4) == 0);
        // Step 4 of fileMappedLifecycle: ClefRead(3, "accumulator") inside the read-only share window (LastUseDepth = 4).
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_clef_read_accumulator(&file_acc3, file_h, 3) == 0 && file_acc3 == 7777LL);
        std::uintptr_t fileClefAddr = 0, fileLibAddr = 0;
        int fileCopies = -1;
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_clef_address_identity(&fileClefAddr, file_h) == 0);
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_library_address_identity(&fileLibAddr, file_h) == 0);
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_copy_count(&fileCopies, file_h) == 0);
        const auto* mapped_tiles = reinterpret_cast<const SharedTensorTile*>(fileClefAddr);
        CHECK(mapped_tiles[0].sequence == 4242u && mapped_tiles[1].sequence == 8484u && mapped_tiles[3].accumulator == 7777LL);
        CHECK(fileClefAddr == fileLibAddr);
        CHECK(fileClefAddr != 0);
        CHECK((fileClefAddr & 63u) == 0u);
        CHECK((fileClefAddr & 4095u) == 64u);
        CHECK(fileCopies == 0);
        // Step 5 of fileMappedLifecycle: UnshareFromLibrary (LibraryRetentionEndDepth = 5).
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_unshare_from_library(file_h) == 0);
        // Step 6 of fileMappedLifecycle: UnmapRegion (UnmapDepth = 6).
        CHECK(farscape_shared_buffer_FileTensorTileBuffer_unmap(file_h) == 0);
        ::close(valid_rfd);
    }""").Replace("__SHORT_BACKING__", shortBackingPath).Replace("__VALID_BACKING__", validBackingPath).Replace("__HOST_CONTRACT_SYM__", hostContractSymbol)
    File.WriteAllText(path "runner.cpp", runnerSource + """

    // 1. Clef writes initial values into tile 0 and tile 1 in the HostShared region.
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(h, 0, 100u) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_accumulator(h, 0, 1000LL) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_scale(h, 0, 10) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_checksum(h, 0, 0u) == 0);

    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(h, 1, 200u) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_accumulator(h, 1, 5000LL) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_scale(h, 1, 25) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_checksum(h, 1, 0u) == 0);

    // Out-of-bounds element index is refused with StatusOutOfRange (4).
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(h, -1, 1u) == 4);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(h, 4, 1u) == 4);

    // Library mutation before sharing is refused with StatusAccessConflict (6).
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place(h, 0, 5) == 6);

    // 2. Share buffer to targeted library in ReadWrite mode (3) for 4 elements.
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_share_to_library(h, 3, 5) == 4);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_share_to_library(h, 3, 4) == 0);

    // While shared ReadWrite, Clef read/write is refused with StatusAccessConflict (6),
    // and unmapping while retained by the library is refused with StatusLifetimeError (5).
    std::uint32_t seq = 0;
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(h, 0, 999u) == 6);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_sequence(&seq, h, 0) == 6);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_unmap(h) == 5);

    // 3. Targeted library mutates tile 0 and tile 1 in place without copying.
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place(h, 0, 7) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place(h, 1, 4) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place(h, 4, 1) == 4);

    // 4. Unshare from library so Clef regains access to the same memory region.
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_unshare_from_library(h) == 0);

    // 5. Clef reads library-mutated fields directly without copying.
    std::int64_t acc0 = 0, acc1 = 0;
    std::uint32_t seq0 = 0, seq1 = 0, chk0 = 0, chk1 = 0;
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_sequence(&seq0, h, 0) == 0 && seq0 == 107u);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_accumulator(&acc0, h, 0) == 0 && acc0 == 1070LL);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_checksum(&chk0, h, 0) == 0 && chk0 == (107u ^ 1070u));

    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_sequence(&seq1, h, 1) == 0 && seq1 == 204u);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_accumulator(&acc1, h, 1) == 0 && acc1 == 5100LL);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_checksum(&chk1, h, 1) == 0 && chk1 == (204u ^ 5100u));

    // 6. Share ReadOnly (1): Clef read is permitted, but Clef write and library mutate are refused (6).
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_share_to_library(h, 1, 4) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_sequence(&seq0, h, 0) == 0 && seq0 == 107u);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_write_sequence(h, 0, 1u) == 6);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_library_transform_in_place(h, 0, 1) == 6);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_unshare_from_library(h) == 0);

    // 7. Verify zero copies and identical address identity between Clef and targeted library,
    // and then verify that the staging-copy control increments both copy counters and yields a distinct address.
    std::uintptr_t clef_addr = 0, lib_addr = 0;
    int copies = -1;
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_address_identity(&clef_addr, h) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_library_address_identity(&lib_addr, h) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_copy_count(&copies, h) == 0);
    CHECK(clef_addr != 0 && clef_addr == lib_addr && clef_addr == shared_tile_library_last_address());
    CHECK((clef_addr % 64u) == 0u);
    CHECK(copies == 0 && shared_tile_library_observed_copies() == 0);
    std::uintptr_t copy_control_addr = 0;
    int copy_control_copies = 0;
    CHECK(shared_tile_library_copy_and_transform(reinterpret_cast<const SharedTensorTile*>(clef_addr), 4, 0, 1, &copy_control_addr, &copy_control_copies) == 0);
    CHECK(copy_control_copies == 1 && shared_tile_library_observed_copies() == 1 && copy_control_addr != clef_addr);
    std::uintptr_t dev_copy_control_addr = 0;
    int dev_copy_control_copies = 0;
    CHECK(shared_tile_library_copy_and_transform(reinterpret_cast<const SharedTensorTile*>(devClefAddr), 2, 0, 1, &dev_copy_control_addr, &dev_copy_control_copies) == 0);
    CHECK(dev_copy_control_copies == 1 && shared_tile_library_observed_copies() == 2 && dev_copy_control_addr != devClefAddr);
    CHECK(farscape_shared_buffer_DeviceTensorTileBuffer_unmap(dev_h) == 0);

    // 8. Unmap region and verify use-after-unmap is refused with StatusLifetimeError (5).
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_unmap(h) == 0);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_unmap(h) == 5);
    CHECK(farscape_shared_buffer_SharedTensorTileBuffer_clef_read_sequence(&seq0, h, 0) == 5);

    std::printf("shared-buffer-native-ok size=%zu align=%zu seq0=%u acc0=%lld seq1=%u acc1=%lld copies=%d same_addr=%d spaces=1,2,3\n",
        sizeof(SharedTensorTile), alignof(SharedTensorTile),
        seq0, static_cast<long long>(acc0), seq1, static_cast<long long>(acc1),
        copies, clef_addr == lib_addr ? 1 : 0);
    return 0;
}
    """)
    let mismatchDir = Path.Combine(directory, "mismatch")
    Directory.CreateDirectory(mismatchDir) |> ignore
    let mismatchShimPath = Path.Combine(mismatchDir, "shim_host_mismatch.cpp")
    let mismatchLibPath = Path.Combine(mismatchDir, "libsharedtile.so")
    File.WriteAllText(mismatchShimPath, CppClassAnalysis.renderSharedBufferZeroCopyShimC { baseHostSpec with SpaceOffset = 64L })
    run "/usr/bin/clang++" [ "-std=c++17"; "-I" + directory; "-shared"; "-fPIC"; path "shared_tile.cpp"; mismatchShimPath; path "shim_device.cpp"; path "shim_file.cpp"; "-o"; mismatchLibPath ] |> ignore
    let linkFailure =
        Assert.ThrowsAny<System.Exception>(fun () ->
            run "/usr/bin/clang++" [ "-std=c++17"; path "runner.cpp"; "-L" + mismatchDir; "-lsharedtile"; "-o"; Path.Combine(mismatchDir, "runner_mismatch") ] |> ignore)
    Assert.Contains(hostContractSymbol, linkFailure.Message)
    let runner = path "runner"
    run "/usr/bin/clang++" [ "-std=c++17"; path "runner.cpp"; "-L" + directory; "-lsharedtile"; "-Wl,-rpath,$ORIGIN"; "-o"; runner ] |> ignore
    let result = run runner []
    File.WriteAllText(path "native-result.txt", result)
    Assert.Contains("shared-buffer-native-ok size=24 align=8 seq0=107 acc0=1070 seq1=204 acc1=5100 copies=0 same_addr=1 spaces=1,2,3", result)

[<Fact>]
let sharedBufferDeclsAndNativeZeroCopyBidirectionalExecution () =
    let directory = TestHelpers.TestScratch.create "shared-buffer"
    try sharedBufferDeclsAndNativeZeroCopyBidirectionalExecutionIn directory
    finally Directory.Delete(directory, true)

// #115: real installed declarations must retain their actual exception and layout facts.
let private installedXrtSurface =
    lazy (CppClassAnalysis.loadXrtCppSurface "/usr/include/xrt" "/usr/lib/libxrt_coreutil.so" true)

[<Fact>]
let ``XRT surface selection retains measured layouts exported symbols and throwing refusals`` () =
    let declarations, contracts = installedXrtSurface.Value
    Assert.Contains(declarations, function
        | CppParser.Declaration.Struct s -> s.Name = "axlf" && not (CppParser.isDefinedRecord s)
        | _ -> false)
    let classes = CppClassAnalysis.buildClassMap declarations
    let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with CppClasses = contracts }
    let _, symbols = CppClassAnalysis.inspectLibraryExports "/usr/lib/libxrt_coreutil.so"
    let exported = symbols |> List.map fst |> Set.ofList
    let code = FidelityCodeGenerator.generateModule ctx Set.empty declarations "Xrt" "xrt_coreutil" "XRT C++ bindings" []
    Assert.DoesNotContain("nativeint", code)
    Assert.DoesNotContain("nativeptr", code)
    for name in ["uuid"; "xclbin"; "device"; "hw_context"; "kernel"; "bo"; "run"] do
        let cls = classes["xrt::" + name]
        let safeName = "xrt_" + name
        Assert.Equal(16, CppClassAnalysis.estimateClassSize cls)
        Assert.Equal((if name = "uuid" then 8 else 64), cls.Measured.Value.AlignmentBits)
        Assert.Equal(Some 16, contracts["xrt::" + name].SizeOverride)
        Assert.Contains("type " + safeName, code)
        Assert.Contains("module " + safeName + " =", code)
        Assert.Contains("let " + safeName + "_dtor (self: CHandle<" + safeName + ">) : unit", code)
        Assert.Contains("Binding = \"Xrt." + safeName + "_dtor\"", code)
        for decl in cls.Constructors @ cls.Methods do
            match decl with
            | CppParser.Declaration.Function f ->
                Assert.False(CppClassAnalysis.hasNoExcept f)
                let symbol = f.MangledName.Value
                if not (symbol.StartsWith("farscape_cpp_")) then Assert.Contains(symbol, exported)
                Assert.Contains("FidelityExtern(\"xrt_coreutil\", \"" + symbol + "\")", code)
            | _ -> failwith "only selected callable declarations expected"
        let unadornedContracts = contracts.Add("xrt::" + name, { contracts["xrt::" + name] with KindOverride = None }).Add(name, { contracts[name] with KindOverride = None })
        let unadornedCtx = { ctx with CppClasses = unadornedContracts }
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            FidelityCodeGenerator.generateModule unadornedCtx Set.empty [CppParser.Declaration.Class cls] "Test.Xrt" "xrt_coreutil" "XRT" [] |> ignore)
        Assert.Equal((if name = "uuid" then "FS9212" else "FS9211"), (only error.Refusal).Code)
        Assert.Contains("may throw across the native boundary", (only error.Refusal).Message)
    for stdName, stdSize in ["std_string_view", 16; "std_string", 32; "std_chrono_milliseconds", 8] do
        Assert.Contains("type " + stdName, code)
        Assert.Contains("module " + stdName + " =", code)
        Assert.Contains("let Size : int = " + string stdSize, code)
        Assert.Contains("CHandle<" + stdName + ">", code)
    Assert.Contains("let xrt_device_register_xclbin (retStorage: CHandle<xrt_uuid>) (self: CHandle<xrt_device>) (xclbin: CHandle<xrt_xclbin>) : unit", code)
    Assert.Contains("FidelityExtern(\"xrt_coreutil\", \"_ZN3xrt6device15register_xclbinERKNS_6xclbinE\")", code)
    Assert.Contains("FidelityExtern(\"xrt_coreutil\", \"farscape_cpp_xrt_xclbin_dtor\")", code)
    Assert.Contains("FidelityExtern(\"xrt_coreutil\", \"farscape_cpp_xrt_uuid_dtor\")", code)
    Assert.Equal(CppClassAnalysis.SretReturn, CppClassAnalysis.classifyReturnConvention classes["xrt::uuid"])
    let register =
        classes["xrt::device"].Methods |> List.pick (function CppParser.Declaration.Function f -> Some f | _ -> None)
    Assert.Equal(CppClassAnalysis.SretReturn, (CppClassAnalysis.analyzeMethodReturn classes register).ReturnConvention)
    let forward = { mkClassEmpty "uuid" with QualifiedName = Some "xrt::uuid" }
    let retained = CppClassAnalysis.buildClassMap (declarations @ [CppParser.Declaration.Class forward])
    Assert.Equal<CppParser.ClassDecl>(classes["xrt::uuid"], retained["uuid"])
    Assert.Equal<CppParser.ClassDecl>(classes["xrt::uuid"], retained["xrt::uuid"])
    for name in ["uuid_t"; "xuid_t"] do
        Assert.Equal(16, CppClassAnalysis.estimateClassSize (mkClassValue "Uuid" [mkField "value" name]))
    for name, size in ["std::string_view", 16; "std_string_view", 16; "std::string", 32; "std_string", 32; "std::chrono::milliseconds", 8; "std_chrono_milliseconds", 8] do
        Assert.True(contracts[name].Bind)
        Assert.Equal(Some size, contracts[name].SizeOverride)
    let uncontractedDeclarations, uncontractedSpecs = CppClassAnalysis.loadXrtCppSurface "/usr/include/xrt" "/usr/lib/libxrt_coreutil.so" false
    let uncontractedClasses = CppClassAnalysis.buildClassMap uncontractedDeclarations
    for name in ["xrt::xclbin"; "xrt::kernel"; "xrt::run"] do
        let contracts = uncontractedSpecs.Add(name, { uncontractedSpecs[name] with KindOverride = Some "shim" })
        let ctx = { FidelityCodeGenerator.buildGenerationContext uncontractedDeclarations Types.LP64 Map.empty with CppClasses = contracts }
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
            FidelityCodeGenerator.generateModule ctx Set.empty [CppParser.Declaration.Class uncontractedClasses[name]] "Test.Xrt" "xrt_coreutil" "XRT" [] |> ignore)
        Assert.Equal("FS9206", (only error.Refusal).Code)
    // The exact installed classes admit lifecycle checking through the existing catching
    // shim contract, without changing any parsed noexcept attribute.
    for name in ["xrt::device"; "xrt::xclbin"] do
        let cls = classes[name]
        let spec = { contracts[name] with KindOverride = Some "shim" }
        let construct = CppClassAnalysis.CppObjectOperation.Construct "object"
        let release = CppClassAnalysis.CppObjectOperation.Release "object"
        CppClassAnalysis.verifyCppObjectLifecycle spec cls [construct; release; CppClassAnalysis.CppObjectOperation.ScopeExit]
        for operation, code in [CppClassAnalysis.CppObjectOperation.MethodCall ("object", "unused"), "FS9217"; release, "FS9218"] do
            let error = Assert.Throws<Diagnostics.RefusalException>(fun () ->
                CppClassAnalysis.verifyCppObjectLifecycle spec cls [construct; release; operation])
            Assert.Equal(code, (only error.Refusal).Code)

[<Fact>]
let ``stdlib reference contracts emit opaque storage and refuse absent disabled wrong size and by value contracts`` () =
    let options = { CppParser.defaultOptions "/not-a-global-enum-probe.h" with CppMode = true }
    Assert.Empty(CppParser.measureResultAliases options [CppParser.Declaration.Function (mkFunc "Nested::result" "alias" [])] [])
    let spec, baseClass = cppObjectContract ()
    let callable = baseClass.Methods |> List.pick (function CppParser.Declaration.Function f -> Some f | _ -> None)
    let enum =
        { mkEnum "mode" [mkEnumVal "Shared" 1L] None with
            UnderlyingType = Some "unsigned int"
            Representation = CppParser.ClangMeasured { Bits = 32; IsSigned = false } }
    let parameters =
        [ "view", "const std::string_view &"; "text", "const std::string &"
          "timeout", "const std::chrono::milliseconds &"; "other", "const demo::Widget &"; "mode", "demo::mode" ]
    let cls =
        { baseClass with Methods = [CppParser.Declaration.Function { callable with Parameters = parameters }] }
    let declarations = [CppParser.Declaration.Enum enum; CppParser.Declaration.Class cls]
    let generate contracts cls =
        let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with CppClasses = contracts }
        FidelityCodeGenerator.generateModule ctx Set.empty [CppParser.Declaration.Class cls] "Test" "test" "Test" []
    let contracts = CppClassAnalysis.defaultXrtStdlibContracts.Add("Widget", spec)
    let code = generate contracts cls
    Assert.DoesNotContain("nativeint", code)
    Assert.DoesNotContain("nativeptr", code)
    for name, size in ["std_string_view", 16; "std_string", 32; "std_chrono_milliseconds", 8] do
        Assert.Contains("CHandle<" + name + ">", code)
        Assert.Contains("module " + name + " =", code)
        Assert.Contains("let Size : int = " + string size, code)
    Assert.Contains("other: CHandle<Widget>", code)
    Assert.Contains("mode: mode", code)
    let stringContract = contracts["std::string"]
    for contracts, cls in [
        Map.ofList ["Widget", spec], cls
        contracts.Add("std::string", { stringContract with Bind = false }), cls
        contracts.Add("std::string", { stringContract with SizeOverride = Some 16 }), cls
        contracts, { cls with Methods = [CppParser.Declaration.Function { callable with Parameters = ["text", "std::string"] }] }
        contracts, { cls with Methods = [CppParser.Declaration.Function { callable with Parameters = ["values", "const std::vector<int> &"] }] } ] do
        let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> generate contracts cls |> ignore)
        Assert.Equal("FS9206", (only error.Refusal).Code)
        Assert.Contains("no admitted marshaled contract", (only error.Refusal).Message)
    // Safe-name lookup and an unspecified size are both admitted by the contract.
    let safeOnly = contracts.Remove("std::string").Add("std_string", { stringContract with Name = "std_string"; SizeOverride = None })
    Assert.Contains("CHandle<std_string>", generate safeOnly cls)

[<Fact>]
let ``inline companion storage constructors destructors and aliases execute with exact native lifetimes`` () =
    let directory = TestHelpers.TestScratch.create "round13-inline"
    Directory.CreateDirectory directory |> ignore
    let path name = Path.Combine(directory, name)
    let run executable arguments =
        let info = System.Diagnostics.ProcessStartInfo(executable)
        info.WorkingDirectory <- directory
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in arguments do info.ArgumentList.Add argument
        use proc = System.Diagnostics.Process.Start info
        let output = proc.StandardOutput.ReadToEndAsync()
        let errors = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        Assert.True(proc.ExitCode = 0, executable + ": " + errors.Result + output.Result)
        output.Result
    try
        let header = path "inline.hpp"
        File.WriteAllText(header, """
#pragma once
namespace fixture {
class Inline {
public:
  static int live;
  static int constructed;
  static int destroyed;
  int value;
  explicit Inline(int n) noexcept : value(n) { ++live; ++constructed; }
  ~Inline() noexcept { --live; ++destroyed; }
};
}
        """)
        let parsed = CppParser.parseHeaderFull { CppParser.defaultOptions header with CppMode = true; IncludeMacros = false } |> Result.defaultWith failwith
        let cls = (CppClassAnalysis.buildClassMap parsed.Declarations)["fixture::Inline"]
        let ctors = cls.Constructors |> List.map (function
            | CppParser.Declaration.Function f -> CppParser.Declaration.Function { f with MangledName = Some "farscape_cpp_fixture_Inline_ctor" }
            | other -> other)
        let cls = { cls with Constructors = ctors; DestructorMangledName = Some "farscape_cpp_fixture_Inline_dtor" }
        let spec : PilotTypes.CppClassSpec = { Name = "fixture::Inline"; Namespace = "Test"; Bind = true; KindOverride = None; SizeOverride = Some 8 }
        let shim = CppClassAnalysis.renderCppObjectStorageShimC header (Map.ofList ["fixture::Inline", cls]) spec cls
        Assert.Contains("::new (storage) fixture::Inline(arg0)", shim)
        Assert.Contains("static_cast<fixture::Inline*>(self)->~Inline()", shim)
        File.WriteAllText(path "shim.cpp", shim)
        File.WriteAllText(path "main.cpp", """
#include "inline.hpp"
#include <cassert>
#include <cstdint>
#include <cstdio>
int fixture::Inline::live = 0;
int fixture::Inline::constructed = 0;
int fixture::Inline::destroyed = 0;
extern "C" void* farscape_cpp_fixture_Inline_ctor(int) noexcept;
extern "C" void farscape_cpp_fixture_Inline_dtor(void*) noexcept;
extern "C" void* fixture_Inline_ctor(int) noexcept;
extern "C" void fixture_Inline_dtor(void*) noexcept;
int main() {
  void* a = farscape_cpp_fixture_Inline_ctor(17);
  void* b = fixture_Inline_ctor(29);
  assert(a && b && a != b);
  assert(reinterpret_cast<std::uintptr_t>(a) % 8 == 0);
  assert(reinterpret_cast<std::uintptr_t>(b) % 8 == 0);
  assert(static_cast<fixture::Inline*>(a)->value == 17);
  assert(static_cast<fixture::Inline*>(b)->value == 29);
  assert(fixture::Inline::live == 2);
  farscape_cpp_fixture_Inline_dtor(a);
  fixture_Inline_dtor(b);
  fixture_Inline_dtor(nullptr);
  assert(fixture::Inline::constructed == 2);
  assert(fixture::Inline::destroyed == 2);
  assert(fixture::Inline::live == 0);
  std::puts("inline-companion-ok constructed=2 destroyed=2 live=0");
}
        """)
        run "/usr/bin/clang++" ["-std=c++17"; path "shim.cpp"; path "main.cpp"; "-o"; path "runner"] |> ignore
        Assert.Contains("inline-companion-ok constructed=2 destroyed=2 live=0", run (path "runner") [])
    finally
        Directory.Delete(directory, true)

[<Fact>]
let ``generated XRT C++ marshaled storage adapters compile and execute C1 sret register_xclbin kernel bo run and D1 destruction natively`` () =
    let declarations, contracts = installedXrtSurface.Value
    let classes = CppClassAnalysis.buildClassMap declarations
    let directory = TestHelpers.TestScratch.create "xrt-cpp-round13"
    Directory.CreateDirectory directory |> ignore
    let path name = Path.Combine(directory, name)
    let run executable arguments =
        let info = System.Diagnostics.ProcessStartInfo(executable)
        info.WorkingDirectory <- directory
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in arguments do info.ArgumentList.Add argument
        use proc = System.Diagnostics.Process.Start info
        let output = proc.StandardOutput.ReadToEndAsync()
        let errors = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        Assert.True(proc.ExitCode = 0, executable + ": " + errors.Result + output.Result)
        output.Result
    try
        let shims =
            [ "uuid"; "xclbin"; "device"; "hw_context"; "kernel"; "bo"; "run" ]
            |> List.map (fun shortName ->
                let qname = "xrt::" + shortName
                CppClassAnalysis.renderCppObjectStorageShimC "/usr/include/xrt/xrt_kernel.h" classes contracts[qname] classes[qname])
            |> String.concat "\n\n"
        File.WriteAllText(path "xrt_shims.cpp", shims)
        File.WriteAllText(path "mock_xrt.cpp", """
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <string>
#include <string_view>
#include <chrono>
#include "/usr/include/xrt/xrt_kernel.h"

static int g_live_objects = 0;
static int g_total_constructed = 0;
static int g_total_destroyed = 0;
static std::int32_t g_bo_buffers[4][16];
static int g_next_bo = 0;
static int g_bound_bo_a = -1;
static int g_bound_bo_b = -1;
static int g_bound_bo_c = -1;
static std::uint32_t g_scalar_count = 0;

extern "C" int mock_xrt_live_objects() noexcept { return g_live_objects; }
extern "C" int mock_xrt_total_constructed() noexcept { return g_total_constructed; }
extern "C" int mock_xrt_total_destroyed() noexcept { return g_total_destroyed; }

struct MockHandle {
    std::uint64_t tag;
    std::uint64_t payload;
};

extern "C" {
void m_device_c1(void* self, unsigned int dindex) asm("_ZN3xrt6deviceC1Ej");
void m_device_c1(void* self, unsigned int dindex) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = 0xDE00ULL;
    h->payload = dindex;
    ++g_live_objects; ++g_total_constructed;
}

void m_device_d1(void* self) asm("_ZN3xrt6deviceD1Ev");
void m_device_d1(void* self) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = 0;
    --g_live_objects; ++g_total_destroyed;
}

xrt::uuid m_device_reg_xclbin(void* self, const xrt::xclbin& xclbin) asm("_ZN3xrt6device15register_xclbinERKNS_6xclbinE");
xrt::uuid m_device_reg_xclbin(void* self, const xrt::xclbin& xclbin) {
    auto* dev = static_cast<const MockHandle*>(self);
    auto* xb = reinterpret_cast<const MockHandle*>(&xclbin);
    xuid_t raw = {0};
    raw[0] = static_cast<unsigned char>((dev->payload + 1u) & 0xFFu);
    raw[1] = static_cast<unsigned char>(xb->tag & 0xFFu);
    raw[15] = 0x5Au;
    return xrt::uuid(raw);
}

void m_xclbin_c1_axlf(void* self, const axlf* top) asm("_ZN3xrt6xclbinC1EPK4axlf");
void m_xclbin_c1_axlf(void* self, const axlf* top) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = top ? 1ULL : 0ULL;
    h->payload = 0ULL;
    ++g_live_objects; ++g_total_constructed;
}

void m_xclbin_c1_sv(void* self, const std::string_view& sv) asm("_ZN3xrt6xclbinC1ERKSt17basic_string_viewIcSt11char_traitsIcEE");
void m_xclbin_c1_sv(void* self, const std::string_view& sv) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = sv.size();
    h->payload = 0ULL;
    ++g_live_objects; ++g_total_constructed;
}

void m_xclbin_c1_str(void* self, const std::string& s) asm("_ZN3xrt6xclbinC1ERKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEE");
void m_xclbin_c1_str(void* self, const std::string& s) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = s.size();
    h->payload = 0ULL;
    ++g_live_objects; ++g_total_constructed;
}

void m_hw_ctx_c1(void* self, const xrt::device& dev, const xrt::uuid& id, xrt::hw_context::access_mode mode) asm("_ZN3xrt10hw_contextC1ERKNS_6deviceERKNS_4uuidENS0_11access_modeE");
void m_hw_ctx_c1(void* self, const xrt::device& dev, const xrt::uuid& id, xrt::hw_context::access_mode mode) {
    auto* h = static_cast<MockHandle*>(self);
    const auto* raw_uuid = id.get();
    h->tag = raw_uuid[15];
    h->payload = static_cast<std::uint64_t>(mode) + raw_uuid[1];
    (void)dev;
    ++g_live_objects; ++g_total_constructed;
}

void m_hw_ctx_d1(void* self) asm("_ZN3xrt10hw_contextD1Ev");
void m_hw_ctx_d1(void* self) {
    static_cast<MockHandle*>(self)->tag = 0;
    --g_live_objects; ++g_total_destroyed;
}

void m_kernel_c1(void* self, const xrt::hw_context& ctx, const std::string& name) asm("_ZN3xrt6kernelC1ERKNS_10hw_contextERKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEE");
void m_kernel_c1(void* self, const xrt::hw_context& ctx, const std::string& name) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = reinterpret_cast<const MockHandle*>(&ctx)->tag;
    h->payload = name.size();
    ++g_live_objects; ++g_total_constructed;
}

void m_kernel_d1(void* self) asm("_ZN3xrt6kernelD1Ev");
void m_kernel_d1(void* self) {
    static_cast<MockHandle*>(self)->tag = 0;
    --g_live_objects; ++g_total_destroyed;
}

int m_kernel_group_id(const void* self, int argno) asm("_ZNK3xrt6kernel8group_idEi");
int m_kernel_group_id(const void* self, int argno) {
    (void)self;
    return 100 + argno;
}

void m_bo_c1(void* self, const xrt::device& dev, std::size_t sz, xrt::bo::flags flags, xrt::memory_group grp) asm("_ZN3xrt2boC1ERKNS_6deviceEmNS0_5flagsEj");
void m_bo_c1(void* self, const xrt::device& dev, std::size_t sz, xrt::bo::flags flags, xrt::memory_group grp) {
    (void)dev; (void)flags; (void)grp;
    auto* h = static_cast<MockHandle*>(self);
    int idx = g_next_bo++;
    h->tag = static_cast<std::uint64_t>(idx);
    h->payload = sz;
    std::memset(g_bo_buffers[idx], 0, sizeof(g_bo_buffers[idx]));
    ++g_live_objects; ++g_total_constructed;
}

void m_bo_d1(void* self) asm("_ZN3xrt2boD1Ev");
void m_bo_d1(void* self) {
    static_cast<MockHandle*>(self)->tag = 0xFFFFULL;
    --g_live_objects; ++g_total_destroyed;
}

void* m_bo_map(void* self) asm("_ZN3xrt2bo3mapEv");
void* m_bo_map(void* self) {
    auto* h = static_cast<MockHandle*>(self);
    return g_bo_buffers[h->tag];
}

void m_bo_sync(void* self, xclBOSyncDirection dir, std::size_t sz, std::size_t offset) asm("_ZN3xrt2bo4syncE18xclBOSyncDirectionmm");
void m_bo_sync(void* self, xclBOSyncDirection dir, std::size_t sz, std::size_t offset) {
    (void)self; (void)dir; (void)sz; (void)offset;
}

std::size_t m_bo_size(const void* self) asm("_ZNK3xrt2bo4sizeEv");
std::size_t m_bo_size(const void* self) {
    return static_cast<const MockHandle*>(self)->payload;
}

void m_run_c1(void* self, const xrt::kernel& krnl) asm("_ZN3xrt3runC1ERKNS_6kernelE");
void m_run_c1(void* self, const xrt::kernel& krnl) {
    auto* h = static_cast<MockHandle*>(self);
    h->tag = reinterpret_cast<const MockHandle*>(&krnl)->tag;
    h->payload = 0;
    ++g_live_objects; ++g_total_constructed;
}

void m_run_d1(void* self) asm("_ZN3xrt3runD1Ev");
void m_run_d1(void* self) {
    static_cast<MockHandle*>(self)->tag = 0;
    --g_live_objects; ++g_total_destroyed;
}

void m_run_set_arg_ptr(void* self, int index, const void* val, std::size_t bytes) asm("_ZN3xrt3run16set_arg_at_indexEiPKvm");
void m_run_set_arg_ptr(void* self, int index, const void* val, std::size_t bytes) {
    (void)self;
    if (index == 3 && val && bytes == sizeof(std::uint32_t)) {
        std::memcpy(&g_scalar_count, val, sizeof(std::uint32_t));
    }
}

void m_run_set_arg_bo(void* self, int index, const xrt::bo& bo) asm("_ZN3xrt3run16set_arg_at_indexEiRKNS_2boE");
void m_run_set_arg_bo(void* self, int index, const xrt::bo& bo) {
    (void)self;
    int bo_idx = static_cast<int>(reinterpret_cast<const MockHandle*>(&bo)->tag);
    if (index == 0) g_bound_bo_a = bo_idx;
    else if (index == 1) g_bound_bo_b = bo_idx;
    else if (index == 2) g_bound_bo_c = bo_idx;
}

void m_run_start(void* self) asm("_ZN3xrt3run5startEv");
void m_run_start(void* self) {
    (void)self;
    for (std::uint32_t i = 0; i < g_scalar_count; ++i) {
        g_bo_buffers[g_bound_bo_c][i] = g_bo_buffers[g_bound_bo_a][i] * g_bo_buffers[g_bound_bo_b][i];
    }
}

ert_cmd_state m_run_wait(const void* self, const std::chrono::milliseconds& timeout) asm("_ZNK3xrt3run4waitERKNSt6chrono8durationIlSt5ratioILl1ELl1000EEEE");
ert_cmd_state m_run_wait(const void* self, const std::chrono::milliseconds& timeout) {
    (void)self;
    return timeout.count() > 0 ? ERT_CMD_STATE_COMPLETED : ERT_CMD_STATE_ERROR;
}
}
        """)
        File.WriteAllText(path "runner.cpp", """
#include <cassert>
#include <cstdint>
#include <cstdio>
#include <string>
#include <string_view>
#include <chrono>
#include "/usr/include/xrt/xrt_kernel.h"

extern "C" {
void* farscape_cpp_xrt_uuid_ctor() noexcept;
void farscape_cpp_xrt_uuid_dtor(void*) noexcept;
void* xrt_xclbin_ctor_2(const std::string&) noexcept;
void farscape_cpp_xrt_xclbin_dtor(void*) noexcept;
void* xrt_device_ctor(unsigned int) noexcept;
void xrt_device_dtor(void*) noexcept;
void xrt_device_register_xclbin(void* retStorage, void* self, const xrt::xclbin& arg0) noexcept;
void* xrt_hw_context_ctor(const xrt::device&, const xrt::uuid&, xrt::hw_context::access_mode) noexcept;
void xrt_hw_context_dtor(void*) noexcept;
void* xrt_kernel_ctor(const xrt::hw_context&, const std::string&) noexcept;
void xrt_kernel_dtor(void*) noexcept;
int xrt_kernel_group_id(void*, int) noexcept;
void* xrt_bo_ctor(const xrt::device&, std::size_t, xrt::bo::flags, xrt::memory_group) noexcept;
void xrt_bo_dtor(void*) noexcept;
void* xrt_bo_map(void*) noexcept;
void xrt_bo_sync(void*, xclBOSyncDirection, std::size_t, std::size_t) noexcept;
std::size_t xrt_bo_size(void*) noexcept;
void* xrt_run_ctor(const xrt::kernel&) noexcept;
void xrt_run_dtor(void*) noexcept;
void xrt_run_set_arg_at_index_0(void*, int, const void*, std::size_t) noexcept;
void xrt_run_set_arg_at_index_1(void*, int, const xrt::bo&) noexcept;
void xrt_run_start(void*) noexcept;
ert_cmd_state xrt_run_wait(void*, const std::chrono::milliseconds&) noexcept;

int mock_xrt_live_objects() noexcept;
int mock_xrt_total_constructed() noexcept;
int mock_xrt_total_destroyed() noexcept;
}

int main() {
    void* dev = xrt_device_ctor(0u);
    std::string xclbin_path = "mul.xclbin";
    void* xclbin = xrt_xclbin_ctor_2(xclbin_path);
    void* uuid = farscape_cpp_xrt_uuid_ctor();
    assert(dev && xclbin && uuid);

    // SretReturn: caller passes uuid storage first, device second, xclbin third
    xrt_device_register_xclbin(uuid, dev, *static_cast<const xrt::xclbin*>(xclbin));
    const unsigned char* raw_uuid = static_cast<const xrt::uuid*>(uuid)->get();
    unsigned int u0 = raw_uuid[0], u1 = raw_uuid[1], u15 = raw_uuid[15];
    assert(u0 == 1u && u1 == xclbin_path.size() && u15 == 0x5Au);

    void* hw_ctx = xrt_hw_context_ctor(*static_cast<const xrt::device*>(dev), *static_cast<const xrt::uuid*>(uuid), xrt::hw_context::access_mode::shared);
    std::string krnl_name = "ewise_mul";
    void* krnl = xrt_kernel_ctor(*static_cast<const xrt::hw_context*>(hw_ctx), krnl_name);
    assert(hw_ctx && krnl);
    assert(xrt_kernel_group_id(krnl, 0) == 100);
    assert(xrt_kernel_group_id(krnl, 2) == 102);

    void* bo_a = xrt_bo_ctor(*static_cast<const xrt::device*>(dev), 16, xrt::bo::flags::host_only, 100u);
    void* bo_b = xrt_bo_ctor(*static_cast<const xrt::device*>(dev), 16, xrt::bo::flags::host_only, 101u);
    void* bo_c = xrt_bo_ctor(*static_cast<const xrt::device*>(dev), 16, xrt::bo::flags::host_only, 102u);
    assert(bo_a && bo_b && bo_c);
    assert(xrt_bo_size(bo_a) == 16 && xrt_bo_size(bo_b) == 16 && xrt_bo_size(bo_c) == 16);

    auto* ptr_a = static_cast<std::int32_t*>(xrt_bo_map(bo_a));
    auto* ptr_b = static_cast<std::int32_t*>(xrt_bo_map(bo_b));
    auto* ptr_c = static_cast<std::int32_t*>(xrt_bo_map(bo_c));
    for (int i = 0; i < 4; ++i) {
        ptr_a[i] = i + 2;
        ptr_b[i] = (i + 1) * 10;
    }
    xrt_bo_sync(bo_a, XCL_BO_SYNC_BO_TO_DEVICE, 16, 0);
    xrt_bo_sync(bo_b, XCL_BO_SYNC_BO_TO_DEVICE, 16, 0);

    void* run = xrt_run_ctor(*static_cast<const xrt::kernel*>(krnl));
    assert(run);
    xrt_run_set_arg_at_index_1(run, 0, *static_cast<const xrt::bo*>(bo_a));
    xrt_run_set_arg_at_index_1(run, 1, *static_cast<const xrt::bo*>(bo_b));
    xrt_run_set_arg_at_index_1(run, 2, *static_cast<const xrt::bo*>(bo_c));
    std::uint32_t count = 4u;
    xrt_run_set_arg_at_index_0(run, 3, &count, sizeof(count));
    xrt_run_start(run);
    std::chrono::milliseconds timeout(1000);
    assert(xrt_run_wait(run, timeout) == ERT_CMD_STATE_COMPLETED);
    xrt_bo_sync(bo_c, XCL_BO_SYNC_BO_FROM_DEVICE, 16, 0);
    assert(ptr_c[0] == 20 && ptr_c[1] == 60 && ptr_c[2] == 120 && ptr_c[3] == 200);

    xrt_run_dtor(run);
    xrt_bo_dtor(bo_c);
    xrt_bo_dtor(bo_b);
    xrt_bo_dtor(bo_a);
    xrt_kernel_dtor(krnl);
    xrt_hw_context_dtor(hw_ctx);
    farscape_cpp_xrt_xclbin_dtor(xclbin);
    farscape_cpp_xrt_uuid_dtor(uuid);
    xrt_device_dtor(dev);

    assert(mock_xrt_total_constructed() == 8);
    assert(mock_xrt_total_destroyed() == 7);
    assert(mock_xrt_live_objects() == 1);
    std::printf("xrt-marshaled-native-ok sret_uuid=%u,%u,0x%x out=%d,%d,%d,%d\n",
        u0, u1, u15, ptr_c[0], ptr_c[1], ptr_c[2], ptr_c[3]);
    return 0;
}
        """)
        run "/usr/bin/clang++" [ "-std=c++17"; "-I/usr/include/xrt"; path "xrt_shims.cpp"; path "mock_xrt.cpp"; path "runner.cpp"; "-luuid"; "-o"; path "runner" ] |> ignore
        let result = run (path "runner") []
        Assert.Contains("xrt-marshaled-native-ok sret_uuid=1,10,0x5a out=20,60,120,200", result)
    finally
        Directory.Delete(directory, true)
