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
    let directory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "pilot-fixture-tests", System.Guid.NewGuid().ToString("N")))
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
        for expected in [ "type demo_Counter"; "module demo_Counter ="; "let Size = 4"; "let Alignment = 4"
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
    let directory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "pilot-fixture-tests", System.Guid.NewGuid().ToString("N")))
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
                          "let StatusOk = 0"; "let StatusNullHandle = -1"; "let StatusStdException = 1"; "let StatusUnknownException = 2"; "let StatusAllocationFailed = 3"
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
    let directory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "pilot-fixture-tests", System.Guid.NewGuid().ToString("N")))
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
    let directory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "pilot-fixture-tests", System.Guid.NewGuid().ToString("N")))
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
                      "StatusOutOfRange = 4"; "IntVector_size"; "IntVector_get"; "IntVector_iter_next"
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

[<Fact>]
let containerNativeLibraryInventoryShimAndExecution () =
    let directory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "test-results", "scratch", "cpp-container", System.Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory directory |> ignore
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
