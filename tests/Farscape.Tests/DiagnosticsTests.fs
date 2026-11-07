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
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.value_structs" }, d.Pilot)
    Assert.Equal("Binding generation failed: FS9119: " + message + " No bindings were written.", refusal.Text)

[<Fact>]
let ``a declaration clang locates inside a macro expansion carries the reason, not an invented line`` () =
    let header = fixturePath "diagnostics/by-value.h"
    let project, refusal = diagnosedPilot [header] [] ["fx_pair_zero"]
    let d = only refusal
    Assert.Equal(Diagnostics.RecordWithoutValueType, d.Code)
    Assert.Equal(Diagnostics.Located.Unknown "clang locates declaration 'fx_pair_zero' inside a macro expansion", d.Source)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.value_structs" }, d.Pilot)

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
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "library.defines" }, d.Pilot)
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
