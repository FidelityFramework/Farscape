module Farscape.Tests.DiagnosticsTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers
open Farscape.Core.Diagnostics

// ═══════════════════════════════════════════════════════════════════
// Structured diagnostics (issue #184): every FS91xx refusal is a record carrying its code, its
// message, its severity, where in a header its cause is declared (from clang's AST location, or
// the exact reason none is known), and the pilot file and key whose input caused it. The text
// Farscape prints is `render` of each record, unchanged.
// ═══════════════════════════════════════════════════════════════════

/// The 1-based line and byte column of `token` on the first line of `path` starting with `line`,
/// read from the file itself, independently of clang.
let private positionOf (path: string) (line: string) (token: string) : Diagnostics.SourcePosition =
    let lines = File.ReadAllLines path
    let index = lines |> Array.findIndex (fun text -> text.StartsWith line)
    { Line = index + 1; Column = lines.[index].IndexOf token + 1 }

/// Generate a pilot through `generateFromProjectDiagnosed`, and return the written pilot's path
/// with the structured failure.
let private diagnosedPilot (headers: string list) (library: string list) (functions: string list) =
    let mutable captured = None
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> refusalText)
        headers library [] functions [] [] (fun _ _ -> ())
    match captured with
    | Some (project, Error refusal) -> project, refusal
    | Some (_, Ok _) -> failwith "the pilot generated; a refusal was expected"
    | None -> failwith "the pilot was not generated"

let private only (refusal: Diagnostics.Refusal) =
    match refusal.Diagnostics with
    | [ d ] -> d
    | ds -> failwithf "one diagnostic expected, got %d: %s" ds.Length refusal.Text

[<Fact>]
let ``a header-located refusal carries clang's line and column of the declaration`` () =
    // FS9129, real clang: the record's location is clang's, confirmed against the file.
    let header = fixturePath "unnamed-bitfields/hidden.h"
    let _, refusal = diagnosedPilot [header] [] ["fx_hidden_pad_use"]
    let d = only refusal
    let message = "struct 'fx_hidden_pad' carries member 2, the unnamed bit-field 'int : 0', which moves bytes natural alignment of the declared field types cannot place, and clang has no measurement of 'fx_hidden_pad' in its declaring unit; an unnamed bit-field is never dropped from its record."
    Assert.Equal(Diagnostics.UnnamedBitFieldUnmeasured, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Diagnostics.Severity.Error, d.Severity)
    Assert.Equal(
        Diagnostics.Located.Known { Path = header; Position = Diagnostics.Located.Known (positionOf header "struct fx_hidden_pad" "fx_hidden_pad") },
        d.Source)
    Assert.Equal<Diagnostics.SourceLocation list>([], d.Related)
    Assert.Equal(None, d.Pilot)
    // The text is exactly what the CLI prints after "Error: ".
    Assert.Equal("Binding generation failed: FS9129: " + message, refusal.Text)
    TestHelpers.withPilot [header] [] ["fx_hidden_pad_use"] (fun result _ -> Assert.Equal(Error refusal.Text, result |> Result.map ignore))

[<Fact>]
let ``a refusal pilot input causes carries the declaration's location and the pilot file and key`` () =
    // FS9119, real clang: the function's location is clang's; the pilot omits the value type.
    let header = fixturePath "diagnostics/by-value.h"
    let project, refusal = diagnosedPilot [header] [] ["fx_pair_make"]
    let d = only refusal
    let message = $"struct 'fx_pair' is the type of the result of function 'fx_pair_make' in '{header}', but the pilot declares no value type for it, so neither its record type nor fx_pair.Descriptor would be emitted for the binding that cites them; declare it in [options] value_structs."
    Assert.Equal(Diagnostics.RecordWithoutValueType, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(
        Diagnostics.Located.Known { Path = header; Position = Diagnostics.Located.Known (positionOf header "struct fx_pair fx_pair_make" "fx_pair_make") },
        d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.value_structs"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    Assert.Equal("Binding generation failed: FS9119: " + message + " No bindings were written.", refusal.Text)

[<Fact>]
let ``a declaration clang locates inside a macro expansion carries the reason, not an invented line`` () =
    let header = fixturePath "diagnostics/by-value.h"
    let project, refusal = diagnosedPilot [header] [] ["fx_pair_zero"]
    let d = only refusal
    Assert.Equal(Diagnostics.RecordWithoutValueType, d.Code)
    Assert.Equal(Diagnostics.Located.Unknown "clang locates declaration 'fx_pair_zero' inside a macro expansion", d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.value_structs"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

[<Fact>]
let ``a pilot-located refusal carries the pilot file and key and no header position`` () =
    // FS9124: a malformed [library] defines entry, refused before any header is parsed.
    let header = fixturePath "unnamed-bitfields/hidden.h"
    let project, refusal = diagnosedPilot [header] ["defines = [\"FX X\"]"] ["fx_hidden_pad_use"]
    let d = only refusal
    let message = "define 'FX X' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option."
    Assert.Equal(Diagnostics.DefineMalformed, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Diagnostics.Severity.Error, d.Severity)
    Assert.Equal(Diagnostics.Located.Unknown "a define is pilot, command-line or pkg-config input, not a header declaration", d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "library.defines"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    Assert.Equal("FS9124: " + message + " No bindings were written.", refusal.Text)

[<Fact>]
let ``a refusal with neither a header nor a pilot cause says so`` () =
    // FS9125: a pkg-config module name from the command line.
    match PkgConfig.resolveDiagnosed "-fx" with
    | Ok _ -> failwith "a malformed module name was resolved"
    | Error refusal ->
        let d = only refusal
        Assert.Equal(Diagnostics.PkgConfigModuleMalformed, d.Code)
        Assert.Equal("pkg-config module name '-fx' is not a module name (letters, digits and _ . + -, not starting with -); it reaches pkg-config as one argument and carries no option.", d.Message)
        Assert.Equal(Diagnostics.Located.Unknown "a pkg-config module name is command-line input, not a header declaration", d.Source)
        Assert.Equal(None, d.Pilot)
        Assert.Equal(Diagnostics.render d, refusal.Text)
        Assert.Equal(Error refusal.Text, PkgConfig.resolve "-fx")

[<Fact>]
let ``a declaration's location is provenance, not part of its shape`` () =
    // Two headers declaring one record alike at different lines declare the same record: the
    // divergence checks and the merge compare what is declared.
    let at line = Diagnostics.Provenance (Diagnostics.Located.Known { Path = "a.h"; Position = Diagnostics.Located.Known { Line = line; Column = 8 } })
    let record = mkStruct "R" [mkField "a" "int"] None
    Assert.Equal({ record with Location = at 3 }, { record with Location = at 9 })
    Assert.Equal(Ok (), BindingGenerator.agreeRecords [ "a.h", [CppParser.Declaration.Struct { record with Location = at 3 }]; "b.h", [CppParser.Declaration.Struct { record with Location = at 9 }] ] |> refusalText)

// ═══════════════════════════════════════════════════════════════════
// Positional parameter names (#194) and structured callback and projection refusals (#195),
// end to end with real clang over the shapes sqlite3.h declares.
// ═══════════════════════════════════════════════════════════════════

/// `diagnosedPilot` with extra `[options]` lines (binding entries).
let private diagnosedPilotWith (headers: string list) (options: string list) (functions: string list) =
    let mutable captured = None
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> refusalText)
        headers [] options functions [] [] (fun _ _ -> ())
    match captured with
    | Some (project, Error refusal) -> project, refusal
    | Some (_, Ok _) -> failwith "the pilot generated; a refusal was expected"
    | None -> failwith "the pilot was not generated"

let private parsedFunction (header: string) (name: string) : CppParser.FunctionDecl =
    let options : CppParser.HeaderParserOptions = {
        HeaderFile = header; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
        IncludeMacros = false; MacroPrefixes = []; Verbose = false; CppMode = false }
    match CppParser.parseHeader options with
    | Ok declarations -> declarations |> List.pick (function CppParser.Declaration.Function f when f.Name = name -> Some f | _ -> None)
    | Error error -> failwith error

let private callbacksHeader = fixturePath "diagnostics/callbacks.h"

[<Fact>]
let ``unnamed C parameters are named by position, beside their named siblings`` () =
    // #194: clang reports no name for three of fx_bind_int's four parameters.
    let f = parsedFunction callbacksHeader "fx_bind_int"
    Assert.Equal<string list>(["arg0"; "arg1"; "value"; "arg3"], f.Parameters |> List.map fst)
    let blob = parsedFunction callbacksHeader "fx_bind_blob"
    Assert.Equal<string list>(["arg0"; "arg1"; "arg2"; "arg3"; "arg4"], blob.Parameters |> List.map fst)
    // A fully named prototype keeps every header name.
    let exec = parsedFunction callbacksHeader "fx_exec"
    Assert.Equal<string list>(["db"; "sql"; "callback"; "arg"; "errmsg"], exec.Parameters |> List.map fst)

[<Fact>]
let ``the generated binding and its descriptor name each unnamed parameter once, by position`` () =
    TestHelpers.withPilot [callbacksHeader] [] ["fx_bind_int"] (fun result output ->
        Assert.True(Result.isOk result, sprintf "%A" result)
        let source =
            Directory.GetFiles(output, "*.clef", SearchOption.AllDirectories)
            |> Array.map File.ReadAllText |> String.concat "\n"
        let binding = source.Split('\n') |> Array.find (fun line -> line.Contains "let fx_bind_int ")
        Assert.Contains("(arg1: int) (value: int) (arg3: int)", binding)
        Assert.Contains("(arg0: ", binding)
        Assert.DoesNotContain("(param:", source)
        for name in ["arg0"; "arg1"; "value"; "arg3"] do
            Assert.Equal(1, binding.Split($"({name}: ").Length - 1)
            Assert.Contains($"Name = \"{name}\"", source))

[<Fact>]
let ``a header name equal to an unnamed sibling's positional name is refused`` () =
    let header = fixturePath "diagnostics/parameter-clash.h"
    let options : CppParser.HeaderParserOptions = {
        HeaderFile = header; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
        IncludeMacros = false; MacroPrefixes = []; Verbose = false; CppMode = false }
    match CppParser.parseHeaderFullDiagnosed options with
    | Ok _ -> failwith "a colliding parameter name was admitted"
    | Error refusal ->
        let d = only refusal
        let message = "function 'fx_clash' names parameter 1 'arg0', which is the positional name of its unnamed parameter 0; the binding and its descriptor would bind 'arg0' twice."
        Assert.Equal(Diagnostics.ParameterNameCollision, d.Code)
        Assert.Equal(message, d.Message)
        Assert.Equal(
            Diagnostics.Located.Known { Path = header; Position = Diagnostics.Located.Known (positionOf header "int fx_clash" "fx_clash") },
            d.Source)
        Assert.Equal(None, d.Pilot)
        Assert.Equal("Failed to parse clang output: FS9131: " + message, refusal.Text)

let private execInvocation =
    [ "[[options.bindings]]"; "name = \"fx_exec\""
      "callback_invocations = [ { parameter = 2, identity = \"fx-exec-row\", version = \"1\", source = \"fixture\", scope = \"during_call\" } ]" ]

[<Fact>]
let ``a callback entry type refusal carries its code, clang's location and the pilot key and parameter`` () =
    // FS9144, sqlite3_exec's shape: the row callback's first argument is a void pointer.
    let project, refusal = diagnosedPilotWith [callbacksHeader] execInvocation ["fx_exec"]
    let d = only refusal
    let message = "fx_exec callback_invocations: unsupported native entry signature type 'void *'"
    Assert.Equal(Diagnostics.CallbackEntryTypeUnadmitted, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Diagnostics.Severity.Error, d.Severity)
    Assert.Equal(
        Diagnostics.Located.Known { Path = callbacksHeader; Position = Diagnostics.Located.Known (positionOf callbacksHeader "int fx_exec" "fx_exec") },
        d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_exec].callback_invocations"; Diagnostics.PilotLocation.Parameter = Some 2 }, d.Pilot)
    Assert.Equal("Binding generation failed: FS9144: " + message, refusal.Text)
    let position = positionOf callbacksHeader "int fx_exec" "fx_exec"
    Assert.Equal<string list>(
        [ $"FS9144 declared at {callbacksHeader}:{position.Line}:{position.Column}"
          $"FS9144 pilot key options.bindings[fx_exec].callback_invocations, parameter 2 in {project}" ],
        Diagnostics.renderLocation d)

[<Fact>]
let ``a callback parameter without nonnull evidence names its binding entry and parameter`` () =
    // FS9146, sqlite3_bind_blob's shape: an unnamed destructor callback, no pilot key for it.
    let project, refusal = diagnosedPilotWith [callbacksHeader] [] ["fx_bind_blob"]
    let d = only refusal
    Assert.Equal(Diagnostics.NullableCallbackUnadmitted, d.Code)
    Assert.Equal("fx_bind_blob parameter 4: a native function-pointer parameter requires explicit nonnull evidence; nullable callback parameters require a supported boundary adapter.", d.Message)
    Assert.Equal(
        Diagnostics.Located.Known { Path = callbacksHeader; Position = Diagnostics.Located.Known (positionOf callbacksHeader "int fx_bind_blob" "fx_bind_blob") },
        d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_bind_blob]"; Diagnostics.PilotLocation.Parameter = Some 4 }, d.Pilot)

[<Fact>]
let ``a parameter both writable and read-only is refused at its binding key`` () =
    // FS9158
    let project, refusal =
        diagnosedPilotWith [callbacksHeader] [ "[[options.bindings]]"; "name = \"fx_exec\""; "reference_parameters = [4]"; "read_only_reference_parameters = [4]" ] ["fx_exec"]
    let d = only refusal
    Assert.Equal(Diagnostics.ReferenceAccessConflict, d.Code)
    Assert.Equal("Binding projection 'fx_exec' declares a parameter both writable and read-only", d.Message)
    Assert.Equal(
        Diagnostics.Located.Known { Path = callbacksHeader; Position = Diagnostics.Located.Known (positionOf callbacksHeader "int fx_exec" "fx_exec") },
        d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_exec].read_only_reference_parameters"; Diagnostics.PilotLocation.Parameter = Some 4 }, d.Pilot)

[<Fact>]
let ``a binding projection index outside the native arity is refused at its key`` () =
    // FS9133 from the pilot projection check.
    let project, refusal =
        diagnosedPilotWith [callbacksHeader] [ "[[options.bindings]]"; "name = \"fx_exec\""; "string_parameters = [5]" ] ["fx_exec"]
    let d = only refusal
    Assert.Equal(Diagnostics.ContractParameterOutOfRange, d.Code)
    Assert.Equal("Binding projection 'fx_exec' has invalid parameter index 5", d.Message)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_exec].string_parameters"; Diagnostics.PilotLocation.Parameter = Some 5 }, d.Pilot)

[<Fact>]
let ``a binding naming a symbol no header declares is refused at its symbol key`` () =
    // FS9159
    let project, refusal = diagnosedPilotWith [callbacksHeader] [ "[[options.bindings]]"; "name = \"fx_missing\"" ] ["fx_exec"]
    let d = only refusal
    Assert.Equal(Diagnostics.BindingSymbolUndeclared, d.Code)
    Assert.Equal("Binding projection 'fx_missing' requires C symbol 'fx_missing' in the parsed headers", d.Message)
    Assert.Equal(Diagnostics.Located.Unknown "no parsed header declares C symbol 'fx_missing'", d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_missing].symbol"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

[<Fact>]
let ``two binding entries with a contract for one binding are refused`` () =
    // FS9160
    let project, refusal = diagnosedPilotWith [callbacksHeader] (execInvocation @ execInvocation) ["fx_exec"]
    let d = only refusal
    Assert.Equal(Diagnostics.BindingDuplicate, d.Code)
    Assert.Equal("fx_exec callback_invocations: duplicate binding projections", d.Message)
    Assert.Equal(
        Diagnostics.Located.Known { Path = callbacksHeader; Position = Diagnostics.Located.Known (positionOf callbacksHeader "int fx_exec" "fx_exec") },
        d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_exec].callback_invocations"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

[<Fact>]
let ``a binding with a contract that no namespace selects is refused`` () =
    // FS9161
    let project, refusal = diagnosedPilotWith [callbacksHeader] execInvocation ["fx_bind_int"]
    let d = only refusal
    Assert.Equal(Diagnostics.ContractBindingNotSelectedOnce, d.Code)
    Assert.Equal("fx_exec callback_invocations: requires exactly one selected native declaration", d.Message)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_exec].callback_invocations"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

[<Fact>]
let ``a location unknown to clang is rendered with its reason`` () =
    let d = Diagnostics.diagnostic Diagnostics.BindingSymbolUndeclared "m" (Diagnostics.Located.Unknown "why") |> Diagnostics.withPilotKey "options.bindings[x].symbol" None
    Assert.Equal<string list>(["FS9159 source unknown: why"; "FS9159 pilot key options.bindings[x].symbol"], Diagnostics.renderLocation d)
    let inFile = Diagnostics.diagnostic Diagnostics.BindingSymbolUndeclared "m" (Diagnostics.inFile "a.h" "no position")
    Assert.Equal<string list>(["FS9159 declared in a.h (no position)"], Diagnostics.renderLocation inFile)

/// A pilot generated with `wrappers`, extra `[options]` lines and extra `[[namespace]]` lines.
let private diagnosedPilotWrapped (headers: string list) (ns: string list) (functions: string list) (wrappers: bool) =
    let mutable captured = None
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false wrappers Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> refusalText)
        headers [] [] functions ns [] (fun _ _ -> ())
    match captured with
    | Some (project, Error refusal) -> project, refusal
    | Some (_, Ok _) -> failwith "the pilot generated; a refusal was expected"
    | None -> failwith "the pilot was not generated"

[<Fact>]
let ``a listener struct met by wrapper generation is refused at its declaration`` () =
    // FS9163, real clang: the struct is discovered from its function-pointer fields.
    let header = fixturePath "diagnostics/listener.h"
    let project, refusal = diagnosedPilotWrapped [header] [] ["fx_add_listener"] true
    let d = only refusal
    let message = "listener struct 'fx_listener': generic listener builders require declared native entry records and initialized fields. The legacy integer-address builder is not admitted; use typed protocol or signal projections."
    Assert.Equal(Diagnostics.GenericListenerUnadmitted, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(
        Diagnostics.Located.Known { Path = header; Position = Diagnostics.Located.Known (positionOf header "struct fx_listener {" "fx_listener") },
        d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "callbacks.listener_structs"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    Assert.Equal("Binding generation failed: FS9163: " + message + " No bindings were written.", refusal.Text)

[<Fact>]
let ``a namespace listing signals without introspection files is refused at the introspection key`` () =
    // FS9172
    let project, refusal = diagnosedPilotWrapped [callbacksHeader] ["signals = [\"UserContentManager::closed\"]"] ["fx_bind_int"] false
    let d = only refusal
    Assert.Equal(Diagnostics.SignalsWithoutIntrospection, d.Code)
    Assert.Equal("[[namespace]] signals require [library] introspection files", d.Message)
    Assert.Equal(Diagnostics.Located.Unknown "a namespace's signals and the library's introspection files are pilot input, not a header declaration", d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "library.introspection"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

// ═══════════════════════════════════════════════════════════════════
// GIR attribution end to end (#185): a GIR-named C type no pilot unit declares (FS9108), or two
// units declare differently (FS9109), is caused by the pilot's `[library] introspection` files.
// Real clang over the signal-provenance fixtures, through `generateFromProjectDiagnosed`.
// ═══════════════════════════════════════════════════════════════════

/// A signal pilot over the signal-provenance `headers` selecting `Probe::<signal>`, generated
/// through `generateFromProjectDiagnosed`: the written pilot's path, the header paths, the
/// structured refusal and whether the output directory stayed empty.
let private diagnosedSignalPilot (headers: string list) (signal: string) =
    let paths = headers |> List.map (fun h -> fixturePath ("signal-provenance/" + h))
    let mutable captured = None
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> refusalText)
        paths
        [ "introspection = [\"" + fixturePath "signal-provenance/probe.gir" + "\"]" ]
        [ "c_header_mode = true"; "descriptor_only_dependencies = true" ] []
        [ "signals = [\"Probe::" + signal + "\"]" ]
        [ "[annotations.nonnull]"; "g_signal_connect_data = [2, 4]" ]
        (fun _ output -> Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories)))
    match captured with
    | Some (project, Error refusal) -> project, paths, refusal
    | Some (_, Ok _) -> failwith "the pilot generated; a refusal was expected"
    | None -> failwith "the pilot was not generated"

[<Fact>]
let ``a GIR-named type no pilot unit declares is refused at the pilot's introspection key`` () =
    // FS9108
    let project, _, refusal = diagnosedSignalPilot ["gobject.h"; "count16.h"] "missing-changed"
    let d = only refusal
    let message = "GObject signal contract: C type 'fx_missing_t' is declared by no pilot unit; a GIR spelling cannot supply its representation."
    Assert.Equal(Diagnostics.GirTypeUndeclared, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Diagnostics.Located.Unknown "no pilot header declares C type 'fx_missing_t'; the pilot's introspection data names it", d.Source)
    Assert.Equal<Diagnostics.SourceLocation list>([], d.Related)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "library.introspection"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    Assert.Equal("Binding generation failed: FS9108: " + message, refusal.Text)
    Assert.Equal<string list>(
        [ "FS9108 source unknown: no pilot header declares C type 'fx_missing_t'; the pilot's introspection data names it"
          $"FS9108 pilot key library.introspection in {project}" ],
        Diagnostics.renderLocation d)

[<Fact>]
let ``a GIR-named type two pilot units declare differently is refused at the pilot's introspection key`` () =
    // FS9109
    let project, paths, refusal = diagnosedSignalPilot ["gobject.h"; "count16.h"; "count32.h"] "count-changed"
    let d = only refusal
    let unlocated = "Farscape's parse keeps no clang location for the declarations a GIR-named type resolves through"
    let message = $"GObject signal contract: C type 'fx_count_t' is declared differently by '{paths.[1]}' (unsigned 16-bit), '{paths.[2]}' (unsigned 32-bit); one declaration per pilot is required."
    Assert.Equal(Diagnostics.GirTypeDivergent, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Diagnostics.inFile paths.[1] unlocated, d.Source)
    Assert.Equal<Diagnostics.SourceLocation list>([ { Path = paths.[2]; Position = Diagnostics.Located.Unknown unlocated } ], d.Related)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "library.introspection"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    Assert.Equal("Binding generation failed: FS9109: " + message, refusal.Text)
    Assert.Equal<string list>(
        [ $"FS9109 declared in {paths.[1]} ({unlocated})"
          $"FS9109 pilot key library.introspection in {project}" ],
        Diagnostics.renderLocation d)

// ═══════════════════════════════════════════════════════════════════
// Structured refusals below the pilot project (#186): single-header generation (`farscape
// generate`) and the layout probe keep each FS91xx refusal's record, with the same text.
// ═══════════════════════════════════════════════════════════════════

let private singleHeader (header: string) (defines: string list) (output: string) : BindingGenerator.GenerationOptions =
    { HeaderFile = FileInfo header; LibraryName = "fx"; OutputDirectory = output; Namespace = "Fx.Native"
      IncludePaths = []; Defines = defines; Verbose = false; GenerateWrappers = false; DataModel = Types.LP64 }

/// Generate one header through `generateBindingsDiagnosed` in a fresh output directory, return
/// the structured refusal after checking `generateBindings` reports exactly its text and that
/// nothing was written.
let private singleHeaderRefusal (header: string) (defines: string list) =
    let output = Path.Combine(Path.GetTempPath(), "farscape-single-header-" + System.Guid.NewGuid().ToString("N"))
    try
        let refusal =
            match BindingGenerator.generateBindingsDiagnosed (singleHeader header defines output) with
            | Ok _ -> failwith "the header generated; a refusal was expected"
            | Error refusal -> refusal
        Assert.Equal(Error refusal.Text, BindingGenerator.generateBindings (singleHeader header defines output) |> Result.map ignore)
        if Directory.Exists output then Assert.Empty(Directory.EnumerateFileSystemEntries output)
        refusal
    finally
        if Directory.Exists output then Directory.Delete(output, true)

[<Fact>]
let ``single-header generation keeps a generation refusal's record and clang location`` () =
    // FS9103, real clang, raised inside generation: the enum's location is clang's.
    let header = fixturePath "enum-representation/unmeasured.h"
    let refusal = singleHeaderRefusal header []
    let d = only refusal
    let message = "enum 'fx_opaque' has no clang measurement of its representation in its declaring unit; its width cannot be inferred."
    Assert.Equal(Diagnostics.EnumUnmeasured, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(
        Diagnostics.Located.Known { Path = header; Position = Diagnostics.Located.Known (positionOf header "enum fx_opaque;" "fx_opaque") },
        d.Source)
    Assert.Equal(None, d.Pilot)
    Assert.Equal("Binding generation failed: FS9103: " + message, refusal.Text)

[<Fact>]
let ``single-header generation keeps a parse refusal's record`` () =
    // FS9124: a malformed command-line define, refused before clang runs.
    let refusal = singleHeaderRefusal (fixturePath "by-value/record.h") ["FX X"]
    let d = only refusal
    let message = "define 'FX X' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option."
    Assert.Equal(Diagnostics.DefineMalformed, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Diagnostics.Located.Unknown "a define is pilot, command-line or pkg-config input, not a header declaration", d.Source)
    Assert.Equal("Failed to parse header: FS9124: " + message, refusal.Text)

[<Fact>]
let ``the layout probe keeps its refusal's record`` () =
    // FS9124 from extractStructLayoutsDiagnosed; the text-only surface reports the same text.
    let header = fixturePath "by-value/record.h"
    let message = "define 'FX X' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option."
    match CppParser.extractStructLayoutsDiagnosed header [] [] ["FX X"] ["fx_vec_t"] false with
    | Ok _ -> failwith "a malformed define was passed to the layout probe"
    | Error refusal ->
        let d = only refusal
        Assert.Equal(Diagnostics.DefineMalformed, d.Code)
        Assert.Equal(message, d.Message)
        Assert.Equal("FS9124: " + message, refusal.Text)
        Assert.Equal(Error refusal.Text, CppParser.extractStructLayouts header [] ["FX X"] ["fx_vec_t"] false |> Result.map ignore)
        // A required probe raises the refusal with its record; its text is the probe's failure.
        let raised = Assert.Throws<Diagnostics.RefusalException>(fun () -> BindingGenerator.requireLayoutProbe (Error refusal) |> ignore)
        Assert.Equal<Diagnostics.Diagnostic list>([ d ], raised.Refusal.Diagnostics)
        Assert.Equal("ABI-critical struct layout extraction failed: FS9124: " + message, raised.Message)

/// Every generator refusal code `Diagnostics` declares, with the name of the literal holding it.
let private declaredCodes () =
    let diagnostics = typeof<Diagnostics.Diagnostic>.DeclaringType
    diagnostics.GetFields(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Static)
    |> Array.filter (fun field -> field.IsLiteral && field.FieldType = typeof<string>)
    |> Array.choose (fun field ->
        match field.GetRawConstantValue() with
        | :? string as code when System.Text.RegularExpressions.Regex.IsMatch(code, "^FS9[0-9]{3}$") -> Some (field.Name, code)
        | _ -> None)
    |> List.ofArray

[<Fact>]
let ``each refusal reason has its own code, allocated sequentially from FS9101 with no reuse`` () =
    let codes = declaredCodes ()
    // One code per reason: no two literals share a code (a renumbering into a taken slot fails here).
    let shared = codes |> List.groupBy snd |> List.filter (fun (_, names) -> names.Length > 1)
    Assert.True(shared.IsEmpty, sprintf "codes shared by more than one reason: %A" shared)
    // Sequential: FS9101 onward with no gap, so the next reason takes the next code (FS91xx is
    // full at FS9199, and allocation continues as FS92xx).
    let numbers = codes |> List.map (snd >> fun code -> int (code.Substring 2)) |> List.sort
    Assert.Equal<int list>([ 9101 .. 9101 + numbers.Length - 1 ], numbers)
    Assert.Equal(9231, List.last numbers)
