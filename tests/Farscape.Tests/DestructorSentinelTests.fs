module Farscape.Tests.DestructorSentinelTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Function-pointer sentinels fixed through constant_parameters (#192): SQLite's destructor
// argument is SQLITE_TRANSIENT or SQLITE_STATIC, never an entry. A copying sentinel removes the
// parameter from the Clef signature and is recorded in the descriptor; the null sentinel, under
// which the library keeps using the bound value, is refused with no declared borrow lifetime.
// ═══════════════════════════════════════════════════════════════════

let private composedHeader = fixturePath "macros/composed.h"

/// The binding lines fixing parameter `index` of `name` to `constant`, its text at `text`.
let private sentinelBinding (name: string) (text: int) (index: int) (constant: string) =
    let constants = List.init (index + 1) (fun i -> if i = index then $"\"{constant}\"" else "\"\"") |> String.concat ", "
    [ "[[options.bindings]]"; $"name = \"{name}\""; $"string_parameters = [{text}]"; $"constant_parameters = [{constants}]" ]

/// Generate over `headers` with `prefix` macros, `bindings` and `trailing` sections, and return
/// every generated .clef file's text, concatenated.
let private generatedWith (headers: string list) (prefix: string) (bindings: string list) (functions: string list) (trailing: string list) =
    let mutable text = None
    withPilotSections headers [ $"macro_prefixes = [\"{prefix}\"]" ] ("c_header_mode = true" :: bindings) functions [] trailing (fun result output ->
        match result with
        | Error error -> failwithf "generation was refused: %s" error
        | Ok _ ->
            text <-
                Directory.GetFiles(output, "*.clef", SearchOption.AllDirectories)
                |> Array.sort |> Array.map File.ReadAllText |> String.concat "\n" |> Some)
    text.Value

let private generated headers prefix bindings functions = generatedWith headers prefix bindings functions []

/// Generate over `headers` and return the structured refusal and the pilot path.
let private refused (headers: string list) (prefix: string) (bindings: string list) (functions: string list) =
    let mutable captured = None
    withPilotGenerating (fun project ->
            let result = BindingGenerator.generateFromProjectDiagnosed project false false Types.LP64
            captured <- Some (Path.GetFullPath project, result)
            result |> Result.mapError _.Text)
        headers [ $"macro_prefixes = [\"{prefix}\"]" ] ("c_header_mode = true" :: bindings) functions [] [] (fun _ output ->
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories)))
    match captured with
    | Some (project, Error refusal) ->
        match refusal.Diagnostics with
        | [ d ] -> project, d, refusal.Text
        | ds -> failwithf "one diagnostic expected, got %d: %s" ds.Length refusal.Text
    | Some (_, Ok _) -> failwith "the pilot generated; a refusal was expected"
    | None -> failwith "the pilot was not generated"

[<Fact>]
let ``a parameter fixed to a copying sentinel leaves the signature and is recorded in the descriptor`` () =
    let text = generated [ composedHeader ] "FX_" (sentinelBinding "fx_bind_text" 2 4 "FX_TRANSIENT") [ "fx_bind_text" ]
    // Nothing proves the text nonnull, so it crosses as option<string>; with nonnull evidence, string.
    Assert.Contains("let fx_bind_text (statement: option<CHandle<fx_stmt>>) (index: int) (text: option<string>) (bytes: int) : int =", text)
    let nonnull = generatedWith [ composedHeader ] "FX_" (sentinelBinding "fx_bind_text" 2 4 "FX_TRANSIENT") [ "fx_bind_text" ] [ "[annotations.nonnull]"; "fx_bind_text = [0, 2]" ]
    Assert.Contains("let fx_bind_text (statement: CHandle<fx_stmt>) (index: int) (text: string) (bytes: int) : int =", nonnull)
    Assert.Contains("PassBy = Fixed (EntrySentinel (\"FX_TRANSIENT\", -1)) } // ", text)
    Assert.Contains("fixed by the pilot to sentinel FX_TRANSIENT = -1, which clang evaluates; no entry crosses", text)
    Assert.Contains("/// Parameter `destructor` is fixed by the pilot to the function-pointer sentinel FX_TRANSIENT (-1, as clang evaluates it)", text)
    // No entry record or callback descriptor exists for the fixed parameter.
    Assert.DoesNotContain("Callback_", text)
    Assert.DoesNotContain("destructor: ", text)

[<Fact>]
let ``a parameter fixed to the null sentinel is refused without a declared borrow lifetime`` () =
    let project, d, text = refused [ composedHeader ] "FX_" (sentinelBinding "fx_bind_text" 2 4 "FX_STATIC") [ "fx_bind_text" ]
    let message = "fx_bind_text parameter 4: constant 'FX_STATIC' fixes function-pointer parameter 'destructor' to the null sentinel (fx_destructor_type)0, under which the library uses the bound value after the call without copying it; no pilot key declares a borrow lifetime for that value, so the null sentinel is not admitted. Fix a copying sentinel (SQLite's SQLITE_TRANSIENT) instead."
    Assert.Equal(Diagnostics.SentinelBorrowUndeclared, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal("Binding generation failed: FS9180: " + message, text)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_bind_text].constant_parameters"; Diagnostics.PilotLocation.Parameter = Some 4 }, d.Pilot)
    match d.Source with
    | Diagnostics.Located.Known { Path = path } -> Assert.Equal(composedHeader, path)
    | other -> failwithf "the refusal is not located at fx_bind_text: %A" other

[<Theory>]
[<InlineData("FX_COMPOSED", "fx_bind_text parameter 4: constant 'FX_COMPOSED' on function-pointer parameter 'destructor' names a macro clang evaluates to no function-pointer sentinel: an integer constant")>]
[<InlineData("FX_NAME", "fx_bind_text parameter 4: constant 'FX_NAME' on function-pointer parameter 'destructor' names a macro clang evaluates to no function-pointer sentinel: string literal (char[8])")>]
[<InlineData("-1", "fx_bind_text parameter 4: constant '-1' on function-pointer parameter 'destructor' names no parsed macro; a function-pointer parameter is fixed only to a sentinel macro the pilot's macro_prefixes select")>]
let ``a constant on a function-pointer parameter that names no sentinel is refused`` (constant: string) (message: string) =
    let _, d, _ = refused [ composedHeader ] "FX_" (sentinelBinding "fx_bind_text" 2 4 constant) [ "fx_bind_text" ]
    Assert.Equal(Diagnostics.SentinelProjectionInvalid, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some "options.bindings[fx_bind_text].constant_parameters", d.Pilot |> Option.map _.Key)

[<Fact>]
let ``a sentinel on an integer parameter keeps the integer constant refusal`` () =
    let _, d, _ = refused [ composedHeader ] "FX_" [ "[[options.bindings]]"; "name = \"fx_bind_text\""; "constant_parameters = [\"\", \"FX_TRANSIENT\"]"; "nonnull_callbacks = [4]" ] [ "fx_bind_text" ]
    Assert.Equal(Diagnostics.ConstantProjectionInvalid, d.Code)

let private sqliteHeader = "/usr/include/sqlite3.h"

[<Fact>]
let ``SQLite's text and blob routines bind with SQLITE_TRANSIENT and no destructor entry`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #192 input")
    let bindings =
        sentinelBinding "sqlite3_bind_text" 2 4 "SQLITE_TRANSIENT" @ sentinelBinding "sqlite3_result_text" 1 3 "SQLITE_TRANSIENT"
        @ [ "[[options.bindings]]"; "name = \"sqlite3_bind_blob\""; "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_TRANSIENT\"]"
            "[[options.bindings]]"; "name = \"sqlite3_result_blob\""; "constant_parameters = [\"\", \"\", \"\", \"SQLITE_TRANSIENT\"]" ]
    let text = generated [ sqliteHeader ] "SQLITE_" bindings [ "sqlite3_bind_text"; "sqlite3_result_text"; "sqlite3_bind_blob"; "sqlite3_result_blob" ]
    // The text crosses as a string SQLite copies during the call; no destructor parameter. A
    // NULL text binds SQL NULL (sqlite3.h:4948-4951), so without a nonnull claim it is optional.
    Assert.Contains("let sqlite3_bind_text (arg0: option<CHandle<sqlite3_stmt>>) (arg1: int) (arg2: option<string>) (arg3: int) : int =", text)
    Assert.Contains("let sqlite3_result_text (arg0: option<CHandle<sqlite3_context>>) (arg1: option<string>) (arg2: int) : unit =", text)
    let fixedSentinel = "PassBy = Fixed (EntrySentinel (\"SQLITE_TRANSIENT\", -1)) }"
    let descriptors = text.Split("Descriptor : Expr<FunctionDescriptor>") |> Array.skip 1
    Assert.Equal(4, descriptors.Length)
    for descriptor in descriptors do
        Assert.Contains(fixedSentinel, descriptor)
    Assert.DoesNotContain("Callback_", text)

[<Fact>]
let ``SQLite's SQLITE_STATIC is refused on sqlite3_bind_text without a declared borrow lifetime`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #192 input")
    let project, d, _ = refused [ sqliteHeader ] "SQLITE_" (sentinelBinding "sqlite3_bind_text" 2 4 "SQLITE_STATIC") [ "sqlite3_bind_text" ]
    Assert.Equal(Diagnostics.SentinelBorrowUndeclared, d.Code)
    Assert.StartsWith("sqlite3_bind_text parameter 4: constant 'SQLITE_STATIC' fixes function-pointer parameter 'arg4' to the null sentinel (sqlite3_destructor_type)0", d.Message)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[sqlite3_bind_text].constant_parameters"; Diagnostics.PilotLocation.Parameter = Some 4 }, d.Pilot)

let private unitFirst = fixturePath "macros/unit_first.h"
let private unitSecond = fixturePath "macros/unit_second.h"

[<Fact>]
let ``a sentinel with the same text that two units evaluate differently is refused before the binding`` () =
    // ((unit_destructor)UNIT_PRIVATE) is the copying sentinel -1 in the first unit and the null
    // sentinel 0 in the second, which declares unit_bind: the first unit's -1 never stands for it.
    let _, d, text = refused [ unitFirst; unitSecond ] "SENT_" (sentinelBinding "unit_bind" 0 1 "SENT_DESTRUCTOR") [ "unit_bind" ]
    Assert.Equal(Diagnostics.MacroDivergent, d.Code)
    Assert.Equal(
        $"Binding generation failed: FS9101: macro 'SENT_DESTRUCTOR' is defined differently by the pilot's headers: '((unit_destructor)UNIT_PRIVATE)' (clang evaluates (unit_destructor)-1 in that unit) in '{unitFirst}'; '((unit_destructor)UNIT_PRIVATE)' (clang evaluates (unit_destructor)0 in that unit) in '{unitSecond}'. One definition per pilot is required. No bindings were written.",
        text)

[<Fact>]
let ``a function's sentinel is its declaring unit's macro, never another unit's`` () =
    // unit_bind is declared in the second unit; LOCAL_SECOND_ONLY is that unit's own sentinel.
    let text = generated [ unitFirst; unitSecond ] "LOCAL_" (sentinelBinding "unit_bind" 0 1 "LOCAL_SECOND_ONLY") [ "unit_bind" ]
    Assert.Contains("PassBy = Fixed (EntrySentinel (\"LOCAL_SECOND_ONLY\", -1)) } // ", text)
    // LOCAL_FIRST_ONLY is defined only by the first unit: the second unit's clang never saw it.
    let _, d, _ = refused [ unitFirst; unitSecond ] "LOCAL_" (sentinelBinding "unit_bind" 0 1 "LOCAL_FIRST_ONLY") [ "unit_bind" ]
    Assert.Equal(Diagnostics.SentinelProjectionInvalid, d.Code)
    Assert.Equal("unit_bind parameter 1: constant 'LOCAL_FIRST_ONLY' on function-pointer parameter 'destroy' names no parsed macro; a function-pointer parameter is fixed only to a sentinel macro the pilot's macro_prefixes select", d.Message)
