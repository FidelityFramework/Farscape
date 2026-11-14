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

[<Fact>]
let ``SQLite SQLITE_STATIC zero-copy borrow lifetime is admitted with RetainedUntil and types string and ReadOnlyView`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #192 input")
    let bindings =
        [ "[[options.bindings]]"
          "name = \"sqlite3_bind_text\""
          "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 2, content = \"text\", extent = { count_parameter = 3 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Test.Fixture.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Test.Fixture.sqlite3_finalize\", parameter = 0 }] }, hypothesis = { identity = \"sqlite.bind.text.static\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"
          "name = \"sqlite3_bind_blob\""
          "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 2, content = \"bytes\", extent = { count_parameter = 3 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Test.Fixture.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Test.Fixture.sqlite3_finalize\", parameter = 0 }] }, hypothesis = { identity = \"sqlite.bind.blob.static\", version = \"1\", source = \"sqlite3.h\" } }]" ]
    let text = generated [ sqliteHeader ] "SQLITE_" bindings [ "sqlite3_bind_text"; "sqlite3_bind_blob"; "sqlite3_clear_bindings"; "sqlite3_finalize" ]
    Assert.Contains("let sqlite3_bind_text (arg0: option<CHandle<sqlite3_stmt>>) (arg1: int) (arg2: string) (arg3: int) : int =", text)
    Assert.Contains("let sqlite3_bind_blob (arg0: option<CHandle<sqlite3_stmt>>) (arg1: int) (arg2: ReadOnlyView<int>) (n: int) : int =", text)
    Assert.Contains("PassBy = Fixed (EntrySentinel (\"SQLITE_STATIC\", 0)) }", text)
    Assert.Contains("Retention = RetainedUntil (0, [| Redefinition [| 0; 1 |]; OwnerRelease { Binding = \"Test.Fixture.sqlite3_clear_bindings\"; Parameter = 0 }; OwnerRelease { Binding = \"Test.Fixture.sqlite3_finalize\"; Parameter = 0 } |])", text)

[<Fact>]
let ``SQLite SQLITE_STATIC paired with during_call retention is still refused with FS9180`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #192 input")
    let bindings =
        [ "[[options.bindings]]"
          "name = \"sqlite3_bind_text\""
          "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 2, content = \"text\", extent = { count_parameter = 3 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.bind.text.bad\", version = \"1\", source = \"sqlite3.h\" } }]" ]
    let _, d, _ = refused [ sqliteHeader ] "SQLITE_" bindings [ "sqlite3_bind_text" ]
    Assert.Equal(Diagnostics.SentinelBorrowUndeclared, d.Code)

[<Fact>]
let ``Phase B real libc and sqlite3 headers generate zero-copy ReadOnlyBuffer, BufferAbsence, SQLITE_STATIC and status Resource contracts`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required")
    let sqliteBindings =
        [ "[[options.bindings]]"; "name = \"sqlite3_open_v2\""; "reference_parameters = [1]"
          "read_only_buffers = [{ parameter = 0, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.open.filename\", version = \"1\", source = \"sqlite3.h\" } }, { parameter = 3, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.open.zvfs\", version = \"1\", source = \"sqlite3.h\" } }]"
          "buffer_absences = [{ parameter = 3, hypothesis = { identity = \"sqlite.open.zvfs.absent\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_prepare_v2\""; "reference_parameters = [3, 4]"
          "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.prepare_v2.sql\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_prepare_v3\""; "reference_parameters = [4, 5]"
          "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.prepare_v3.sql\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_bind_text\""; "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 2, content = \"text\", extent = { count_parameter = 3 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Test.Fixture.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Test.Fixture.sqlite3_finalize\", parameter = 0 }] }, hypothesis = { identity = \"sqlite.bind.text.static\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_bind_blob\""; "constant_parameters = [\"\", \"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 2, content = \"bytes\", extent = { count_parameter = 3 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0, 1], releases = [{ binding = \"Test.Fixture.sqlite3_clear_bindings\", parameter = 0 }, { binding = \"Test.Fixture.sqlite3_finalize\", parameter = 0 }] }, hypothesis = { identity = \"sqlite.bind.blob.static\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_result_text\""; "constant_parameters = [\"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0], releases = [] }, hypothesis = { identity = \"sqlite.result.text.static\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_result_blob\""; "constant_parameters = [\"\", \"\", \"\", \"SQLITE_STATIC\"]"
          "read_only_buffers = [{ parameter = 1, content = \"bytes\", extent = { count_parameter = 2 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0], releases = [] }, hypothesis = { identity = \"sqlite.result.blob.static\", version = \"1\", source = \"sqlite3.h\" } }]"
          "[[options.bindings]]"; "name = \"sqlite3_bind_parameter_index\""
          "read_only_buffers = [{ parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"sqlite.bind_param_index.zname\", version = \"1\", source = \"sqlite3.h\" } }]" ]
    let sqliteSource =
        generated [ sqliteHeader ] "SQLITE_" sqliteBindings
            [ "sqlite3_open_v2"; "sqlite3_close_v2"; "sqlite3_prepare_v2"; "sqlite3_prepare_v3"
              "sqlite3_bind_text"; "sqlite3_bind_blob"; "sqlite3_result_text"; "sqlite3_result_blob"
              "sqlite3_bind_parameter_index"; "sqlite3_clear_bindings"; "sqlite3_finalize" ]
    Assert.Contains("let sqlite3_open_v2 (filename: string) (ppDb: option<CHandle<sqlite3>> array) (flags: int) (zVfs: option<string>) : int =", sqliteSource)
    Assert.Contains("let sqlite3_prepare_v2 (db: option<CHandle<sqlite3>>) (zSql: string) (nByte: int)", sqliteSource)
    Assert.Contains("let sqlite3_prepare_v3 (db: option<CHandle<sqlite3>>) (zSql: string) (nByte: int)", sqliteSource)
    Assert.Contains("let sqlite3_result_text (arg0: option<CHandle<sqlite3_context>>) (arg1: string) (arg2: int) : unit =", sqliteSource)
    Assert.Contains("let sqlite3_result_blob (arg0: option<CHandle<sqlite3_context>>) (arg1: ReadOnlyView<int>) (arg2: int) : unit =", sqliteSource)
    Assert.Contains("let sqlite3_bind_parameter_index (arg0: option<CHandle<sqlite3_stmt>>) (zName: string) : int =", sqliteSource)
    Assert.Contains("Expr<BufferAbsenceContract>", sqliteSource)

    let libcBindings =
        [ "[[options.bindings]]"; "name = \"fopen\""; "return_handle = \"FILE\""
          "resource = { kind = \"owned_nullable_return\", release_binding = \"Test.Fixture.fclose\", release_parameter = 0, hypothesis = { identity = \"libc.file.owned\", version = \"1\", source = \"stdio.h\" } }"
          "read_only_buffers = [{ parameter = 0, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"libc.fopen.path\", version = \"1\", source = \"stdio.h\" } }, { parameter = 1, content = \"text\", extent = { nul_terminated = true }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"libc.fopen.mode\", version = \"1\", source = \"stdio.h\" } }]"
          "[[options.bindings]]"; "name = \"fclose\""; "parameter_handles = [\"FILE\"]"
          "[[options.bindings]]"; "name = \"write\""
          "read_only_buffers = [{ parameter = 1, content = \"bytes\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"posix.write.buf\", version = \"1\", source = \"unistd.h\" } }]"
          "[[options.bindings]]"; "name = \"send\""
          "read_only_buffers = [{ parameter = 1, content = \"bytes\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"posix.send.buf\", version = \"1\", source = \"sys/socket.h\" } }]"
          "[[options.bindings]]"; "name = \"sendto\""
          "read_only_buffers = [{ parameter = 1, content = \"bytes\", extent = { count_parameter = 2 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"posix.sendto.buf\", version = \"1\", source = \"sys/socket.h\" } }]" ]
    let libcSource =
        generated [ "/usr/include/stdio.h"; "/usr/include/unistd.h"; "/usr/include/sys/socket.h" ] "O_" libcBindings
            [ "fopen"; "fclose"; "write"; "send"; "sendto" ]
    Assert.Contains("let fopenDescriptor : Expr<FunctionDescriptor>", libcSource)
    Assert.Contains("let fopenResourceDescriptor : Expr<ResourceDescriptor>", libcSource)
    Assert.Contains("let fclose (stream: CHandle<FILE>) : int =", libcSource)
    Assert.Contains("(buf: ReadOnlyView<int>)", libcSource)
    Assert.DoesNotContain("(buf: option<CHandle<unit>>)", libcSource)

[<Fact>]
let ``null sentinel admission checks RetainedUntil per sentinel parameter rather than binding-wide`` () =
    let work = TestHelpers.TestScratch.create "per-param-sentinel"
    try
        let header = Path.Combine(work, "multi_buf.h")
        File.WriteAllText(
            header,
            String.concat "\n"
                [ "typedef struct owner_s owner_t;"
                  "typedef void (*buf_dtor_t)(void *);"
                  "#define BUF_STATIC ((buf_dtor_t)0)"
                  "void owner_release(owner_t *owner);"
                  "int two_buf_bind(owner_t *owner, const char *first, int first_len, buf_dtor_t dtor_first, const char *second, int second_len, buf_dtor_t dtor_second);"
                  "int one_buf_two_dtor(owner_t *owner, const char *first, int first_len, buf_dtor_t dtor_first, buf_dtor_t dtor_extra);" ])
        let bothRetained =
            [ "[[options.bindings]]"; "name = \"two_buf_bind\""
              "constant_parameters = [\"\", \"\", \"\", \"BUF_STATIC\", \"\", \"\", \"BUF_STATIC\"]"
              "read_only_buffers = ["
              "  { parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0], releases = [{ binding = \"Test.Fixture.owner_release\", parameter = 0 }] }, hypothesis = { identity = \"multi.buf.1\", version = \"1\", source = \"multi_buf.h\" } },"
              "  { parameter = 4, content = \"bytes\", extent = { count_parameter = 5 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0], releases = [{ binding = \"Test.Fixture.owner_release\", parameter = 0 }] }, hypothesis = { identity = \"multi.buf.2\", version = \"1\", source = \"multi_buf.h\" } }"
              "]" ]
        let okText = generated [ header ] "BUF_" bothRetained [ "two_buf_bind"; "owner_release" ]
        Assert.Contains("let two_buf_bind (owner: option<CHandle<owner_t>>) (first: string) (first_len: int) (second: ReadOnlyView<int>) (second_len: int) : int =", okText)

        let secondDuringCall =
            [ "[[options.bindings]]"; "name = \"two_buf_bind\""
              "constant_parameters = [\"\", \"\", \"\", \"BUF_STATIC\", \"\", \"\", \"BUF_STATIC\"]"
              "read_only_buffers = ["
              "  { parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0], releases = [{ binding = \"Test.Fixture.owner_release\", parameter = 0 }] }, hypothesis = { identity = \"multi.buf.1\", version = \"1\", source = \"multi_buf.h\" } },"
              "  { parameter = 4, content = \"bytes\", extent = { count_parameter = 5 }, retention = { kind = \"during_call\" }, hypothesis = { identity = \"multi.buf.2\", version = \"1\", source = \"multi_buf.h\" } }"
              "]" ]
        let _, dDuring, _ = refused [ header ] "BUF_" secondDuringCall [ "two_buf_bind"; "owner_release" ]
        Assert.Equal(Diagnostics.SentinelBorrowUndeclared, dDuring.Code)
        Assert.Equal(Some 6, dDuring.Pilot |> Option.bind _.Parameter)

        let extraNullSentinel =
            [ "[[options.bindings]]"; "name = \"one_buf_two_dtor\""
              "constant_parameters = [\"\", \"\", \"\", \"BUF_STATIC\", \"BUF_STATIC\"]"
              "read_only_buffers = ["
              "  { parameter = 1, content = \"text\", extent = { count_parameter = 2 }, retention = { kind = \"retained_until\", owner_parameter = 0, redefined_by = [0], releases = [{ binding = \"Test.Fixture.owner_release\", parameter = 0 }] }, hypothesis = { identity = \"multi.buf.1\", version = \"1\", source = \"multi_buf.h\" } }"
              "]" ]
        let _, dExtra, _ = refused [ header ] "BUF_" extraNullSentinel [ "one_buf_two_dtor"; "owner_release" ]
        Assert.Equal(Diagnostics.SentinelBorrowUndeclared, dExtra.Code)
        Assert.Equal(Some 4, dExtra.Pilot |> Option.bind _.Parameter)
    finally
        Directory.Delete(work, true)
