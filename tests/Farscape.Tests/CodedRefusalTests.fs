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
