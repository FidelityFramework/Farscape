module Farscape.Tests.BorrowedResultTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Borrowed result text and blobs with their extent (#196). SQLite returns memory it owns and
// keeps valid until the owner's next step, reset or finalize (sqlite3_column_text and
// sqlite3_column_blob), until the value converts (sqlite3_value_text), or until the connection's
// next call (sqlite3_errmsg). A pilot's `borrowed_result` projects the result as a read-only
// view of bytes, `option<ReadOnlyView<byte>>`, with a BorrowedResultViewDescriptor naming the
// owner, the extent (a companion call on the same arguments, or the terminating NUL) and the
// calls that end its validity. Undeclared, a character-pointer result stays opaque with its
// reason recorded; a declaration that does not fit is FS9208, FS9209 or FS9210.
// ═══════════════════════════════════════════════════════════════════

let private sqliteHeader = "/usr/include/sqlite3.h"

let private requireSqlite () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #196 input")

let private hypothesis (identity: string) = $"hypothesis = {{ identity = \"{identity}\", version = \"3\", source = \"/usr/include/sqlite3.h\" }}"

/// The statement's step, reset and finalize, each receiving the statement at parameter 0.
let private statementEvents =
    "invalidated_by = [ { binding = \"Test.Fixture.sqlite3_step\", parameter = 0 }, { binding = \"Test.Fixture.sqlite3_reset\", parameter = 0 }, { binding = \"Test.Fixture.sqlite3_finalize\", parameter = 0 } ]"

let private statementFunctions = [ "sqlite3_step"; "sqlite3_reset"; "sqlite3_finalize" ]

/// A `borrowed_result` from its fields.
let private borrowed (fields: string list) = "borrowed_result = { " + String.concat ", " fields + " }"

/// sqlite3_column_text's honest declaration: text measured by sqlite3_column_bytes on the same
/// statement and column, valid until the statement's next step, reset or finalize.
let private columnText =
    [ "content = \"text\""; "owner_parameter = 0"
      "extent = { binding = \"Test.Fixture.sqlite3_column_bytes\", arguments = [0, 1] }"
      statementEvents; hypothesis "sqlite.column_text" ]

let private stepResetFinalize =
    [ for name in [ "sqlite3_step"; "sqlite3_reset"; "sqlite3_finalize" ] ->
        $"OwnerCall ({{ Binding = \"Test.Fixture.{name}\"; Parameter = 0 }})" ]

[<Fact>]
let ``sqlite3_column_text is a borrowed text view measured by sqlite3_column_bytes until the statement steps, resets or finalizes`` () =
    requireSqlite ()
    let text = generatedClefWith [ sqliteHeader ] (pilotBinding "sqlite3_column_text" [ borrowed columnText ]) ([ "sqlite3_column_text"; "sqlite3_column_bytes" ] @ statementFunctions) []
    Assert.Contains("let sqlite3_column_text (arg0: option<CHandle<sqlite3_stmt>>) (iCol: int) : option<ReadOnlyView<byte>> =", text)
    // No raw handle text surface remains, and the ABI result is still the native pointer.
    let extern' = declarationText text "sqlite3_column_text"
    Assert.DoesNotContain("CHandle<byte>", extern')
    Assert.DoesNotContain("CHandle<int>", extern')
    Assert.Equal(1, text.Split("let sqlite3_column_text ").Length - 1)
    let descriptor = declarationText text "sqlite3_column_textBorrowedResult"
    for fact in [ "Expr<BorrowedResultViewDescriptor>"
                  "Binding = \"Test.Fixture.sqlite3_column_text\""
                  "Content = Utf8Text"
                  "Extent = CompanionCall (\"Test.Fixture.sqlite3_column_bytes\", [|0; 1|])"
                  "Owner = 0"
                  "Identity = \"sqlite.column_text\"" ] @ stepResetFinalize do
        Assert.Contains(fact, descriptor)
    Assert.Contains("Borrowed result: the library's bytes, owned through `arg0`, read-only and valid only until a declared invalidation", text)
    Assert.DoesNotContain("is kept opaque", extern')

[<Fact>]
let ``sqlite3_column_blob is a borrowed byte view of its void result, measured by sqlite3_column_bytes`` () =
    requireSqlite ()
    let fields =
        [ "content = \"bytes\""; "owner_parameter = 0"
          "extent = { binding = \"Test.Fixture.sqlite3_column_bytes\", arguments = [0, 1] }"
          statementEvents; hypothesis "sqlite.column_blob" ]
    let text = generatedClefWith [ sqliteHeader ] (pilotBinding "sqlite3_column_blob" [ borrowed fields ]) ([ "sqlite3_column_blob"; "sqlite3_column_bytes" ] @ statementFunctions) []
    Assert.Contains("let sqlite3_column_blob (arg0: option<CHandle<sqlite3_stmt>>) (iCol: int) : option<ReadOnlyView<byte>> =", text)
    let descriptor = declarationText text "sqlite3_column_blobBorrowedResult"
    for fact in [ "Content = RawBytes"; "Extent = CompanionCall (\"Test.Fixture.sqlite3_column_bytes\", [|0; 1|])" ] @ stepResetFinalize do
        Assert.Contains(fact, descriptor)

[<Fact>]
let ``sqlite3_value_text is borrowed text measured by sqlite3_value_bytes and invalidated by any later call on the value`` () =
    requireSqlite ()
    let fields =
        [ "content = \"text\""; "owner_parameter = 0"
          "extent = { binding = \"Test.Fixture.sqlite3_value_bytes\", arguments = [0] }"
          "invalidated_by = [ { any_owner_call = true } ]"; hypothesis "sqlite.value_text" ]
    let text = generatedClefWith [ sqliteHeader ] (pilotBinding "sqlite3_value_text" [ borrowed fields ]) [ "sqlite3_value_text"; "sqlite3_value_bytes" ] []
    Assert.Contains("let sqlite3_value_text (arg0: option<CHandle<sqlite3_value>>) : option<ReadOnlyView<byte>> =", text)
    let descriptor = declarationText text "sqlite3_value_textBorrowedResult"
    Assert.Contains("Extent = CompanionCall (\"Test.Fixture.sqlite3_value_bytes\", [|0|])", descriptor)
    Assert.Contains("AnyOwnerCall", descriptor)

[<Fact>]
let ``sqlite3_errmsg is NUL-terminated borrowed text that the connection's next call may overwrite`` () =
    requireSqlite ()
    let fields = [ "content = \"text\""; "owner_parameter = 0"; "extent = { nul_terminated = true }"; "invalidated_by = [ { any_owner_call = true } ]"; hypothesis "sqlite.errmsg" ]
    let text = generatedClefWith [ sqliteHeader ] (pilotBinding "sqlite3_errmsg" [ borrowed fields ]) [ "sqlite3_errmsg" ] []
    Assert.Contains("let sqlite3_errmsg (arg0: option<CHandle<sqlite3>>) : option<ReadOnlyView<byte>> =", text)
    let descriptor = declarationText text "sqlite3_errmsgBorrowedResult"
    for fact in [ "Content = Utf8Text"; "Extent = NulTerminated"; "Owner = 0"; "AnyOwnerCall" ] do
        Assert.Contains(fact, descriptor)

[<Fact>]
let ``an undeclared character-pointer result stays opaque and records why`` () =
    requireSqlite ()
    let text = generatedClefWith [ sqliteHeader ] [] [ "sqlite3_column_text"; "sqlite3_errmsg"; "sqlite3_column_blob" ] []
    Assert.DoesNotContain("ReadOnlyView", text)
    Assert.DoesNotContain("BorrowedResultViewDescriptor", text)
    Assert.Contains("Result `const unsigned char *` is kept opaque: no borrowed_result declares its owner, extent and validity, so it is not read as text or bytes.", text)
    Assert.Contains("Result `const char *` is kept opaque: no borrowed_result declares its owner, extent and validity, so it is not read as text or bytes.", text)
    // A void result is no character data: nothing is said about reading it as text.
    Assert.DoesNotContain("Result `const void *` is kept opaque", text)

// ── Declarations that do not fit their result ───────────────────────

/// The key a `key = value` line sets.
let private keyOf (line: string) = line.Substring(0, line.IndexOf(" =")).Trim()

/// sqlite3_column_text's fields with each `replacement` line setting its key: a key borrowed_result
/// carries replaces that field; any other key is a sibling key of the binding.
let private columnTextWith (replacement: string) =
    let lines = replacement.Split('\n') |> List.ofArray
    let inside, sibling = lines |> List.partition (fun line -> columnText |> List.exists (fun field -> keyOf field = keyOf line))
    let fields = columnText |> List.map (fun field -> inside |> List.tryFind (fun line -> keyOf line = keyOf field) |> Option.defaultValue field)
    fields, sibling

[<Theory>]
// FS9208: the result is no pointer to the content's bytes, or is also a handle.
[<InlineData("blob-text", "sqlite3_column_blob", "content", "content = \"text\"", "FS9208", "", "sqlite3_column_blob: borrowed_result text requires a pointer to an 8-bit character, not 'const void *'")>]
[<InlineData("integer-result", "sqlite3_column_bytes", "content", "content = \"bytes\"", "FS9208", "", "sqlite3_column_bytes: borrowed_result requires a single-level pointer to an 8-bit character or void, not 'int'")>]
[<InlineData("return-handle", "sqlite3_column_text", "content", "content = \"text\"\nreturn_handle = \"sqlite3_text\"", "FS9208", "", "sqlite3_column_text: borrowed_result conflicts with return_handle: a borrowed view is no handle")>]
// FS9209: the extent is no integer companion call on the borrowing call's own arguments.
[<InlineData("companion-result", "sqlite3_column_text", "extent", "extent = { binding = \"Test.Fixture.sqlite3_column_name\", arguments = [0, 1] }", "FS9209", ".extent", "sqlite3_column_text borrowed_result: extent 'Test.Fixture.sqlite3_column_name' returns 'const char *', not an integer byte count")>]
[<InlineData("companion-arity", "sqlite3_column_text", "extent", "extent = { binding = \"Test.Fixture.sqlite3_column_bytes\", arguments = [0] }", "FS9209", ".extent", "sqlite3_column_text borrowed_result: extent 'Test.Fixture.sqlite3_column_bytes' takes 2 arguments, not the 1 arguments declared")>]
[<InlineData("companion-owner", "sqlite3_column_text", "extent", "extent = { binding = \"Test.Fixture.sqlite3_column_bytes\", arguments = [1, 1] }", "FS9209", ".extent", "sqlite3_column_text borrowed_result: extent 'Test.Fixture.sqlite3_column_bytes' arguments must include owner_parameter 0: the extent is measured on the same owner")>]
[<InlineData("companion-order", "sqlite3_column_text", "extent", "extent = { binding = \"Test.Fixture.sqlite3_column_bytes\", arguments = [1, 0] }", "FS9209", ".extent", "sqlite3_column_text borrowed_result: extent 'Test.Fixture.sqlite3_column_bytes' parameter 0 receives CHandle<sqlite3_stmt>, not argument 1's int")>]
[<InlineData("bytes-nul", "sqlite3_column_text", "content", "content = \"bytes\"\nextent = { nul_terminated = true }", "FS9209", ".extent", "sqlite3_column_text borrowed_result: nul_terminated requires text content: bytes may hold a NUL before their end")>]
// FS9141: an extent or invalidation naming no selected declaration.
[<InlineData("companion-unselected", "sqlite3_column_text", "extent", "extent = { binding = \"Test.Fixture.sqlite3_column_bytes16\", arguments = [0, 1] }", "FS9141", ".extent", "sqlite3_column_text borrowed_result: 'Test.Fixture.sqlite3_column_bytes16' must name a selected qualified generated declaration")>]
// FS9210: the owner is no opaque handle, or an invalidation does not receive it.
[<InlineData("owner-integer", "sqlite3_column_text", "owner_parameter", "owner_parameter = 1", "FS9210", "", "sqlite3_column_text borrowed_result: owner_parameter 1 ('int') is no opaque handle that could own the result")>]
[<InlineData("invalidation-other-owner", "sqlite3_column_text", "invalidated_by", "invalidated_by = [ { binding = \"Test.Fixture.sqlite3_close_v2\", parameter = 0 } ]", "FS9210", "", "sqlite3_column_text borrowed_result: invalidation 'Test.Fixture.sqlite3_close_v2' parameter 0 receives CHandle<sqlite3>, not the owner's CHandle<sqlite3_stmt>")>]
[<InlineData("invalidation-empty", "sqlite3_column_text", "invalidated_by", "invalidated_by = []", "FS9210", "", "sqlite3_column_text borrowed_result: invalidated_by is empty: a result nothing invalidates is no borrow")>]
let ``a borrowed result that does not fit its declaration is refused with its code`` (_case: string) (name: string) (_field: string) (replacement: string) (code: string) (keySuffix: string) (message: string) =
    requireSqlite ()
    let fields, sibling = columnTextWith replacement
    let bindings = pilotBinding name ([ borrowed fields ] @ sibling)
    let functions = [ name; "sqlite3_column_bytes"; "sqlite3_column_name"; "sqlite3_close_v2" ] @ statementFunctions |> List.distinct
    let project, d, _ = refusedOnce [ sqliteHeader ] bindings functions
    Assert.Equal(code, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some ($"options.bindings[{name}].borrowed_result" + keySuffix), d.Pilot |> Option.map _.Key)
    Assert.Equal(Some project, d.Pilot |> Option.map _.PilotFile)
    match d.Source with
    | Diagnostics.Located.Known location -> Assert.Equal(sqliteHeader, location.Path)
    | other -> failwithf "the refusal is not located at its declaration: %A" other

// ── Pilot input ─────────────────────────────────────────────────────

let private pilotWith (borrowedResult: string) =
    String.concat "\n"
        [ "[library]"; "name = \"sqlite3\""; "headers = [\"/usr/include/sqlite3.h\"]"
          "[output]"; "mode = \"fidelity\""; "directory = \"out\""
          "[options]"; "[[options.bindings]]"; "name = \"sqlite3_column_text\""; borrowedResult
          "[[namespace]]"; "name = \"Fidelity.SQLite\""; "description = \"sqlite\""; "library = \"sqlite3\""; "functions = [\"sqlite3_column_text\"]" ]

[<Fact>]
let ``a borrowed result declaration roundtrips through the pilot`` () =
    let read text = PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) |> function Ok value -> value | Error error -> failwith error
    for fields, expected in
        [ columnText,
          ({ Content = PilotTypes.BorrowedContent.Text; OwnerParameter = 0
             Extent = PilotTypes.BorrowedExtent.Companion ("Test.Fixture.sqlite3_column_bytes", [ 0; 1 ])
             InvalidatedBy = [ for name in [ "sqlite3_step"; "sqlite3_reset"; "sqlite3_finalize" ] -> PilotTypes.BorrowInvalidation.OwnerCall ("Test.Fixture." + name, 0) ]
             Identity = "sqlite.column_text"; Version = "3"; Source = "/usr/include/sqlite3.h" } : PilotTypes.BindingBorrowedResult)
          [ "content = \"bytes\""; "owner_parameter = 0"; "extent = { nul_terminated = true }"; "invalidated_by = [ { any_owner_call = true } ]"; hypothesis "x" ],
          { Content = PilotTypes.BorrowedContent.Bytes; OwnerParameter = 0; Extent = PilotTypes.BorrowedExtent.NulTerminated
            InvalidatedBy = [ PilotTypes.BorrowInvalidation.AnyOwnerCall ]; Identity = "x"; Version = "3"; Source = "/usr/include/sqlite3.h" } ] do
        let project = read (pilotWith (borrowed fields))
        Assert.Equal(Some expected, project.Options.Value.Bindings.Head.BorrowedResult)
        Assert.Equal(project, read (PilotSerializer.toTomlString project))

[<Theory>]
[<InlineData("content = \"utf16\"", "FS9201", "borrowed_result.content", "borrowed_result content must be text or bytes")>]
[<InlineData("content = \"text\", owner_parameter = 0, extent = { nul_terminated = true }, " + "hypothesis = { identity = \"i\", version = \"1\", source = \"s\" }", "FS9198", "borrowed_result.invalidated_by", "borrowed_result requires invalidated_by")>]
[<InlineData("content = \"text\", owner_parameter = 0, extent = { nul_terminated = true, binding = \"x\" }", "FS9200", "borrowed_result.extent.binding", "borrowed_result extent is either { binding, arguments } or { nul_terminated = true }")>]
[<InlineData("content = \"text\", owner_parameter = 0, extent = { nul_terminated = false }", "FS9201", "borrowed_result.extent.nul_terminated", "borrowed_result extent nul_terminated admits only true")>]
[<InlineData("content = \"text\", owner_parameter = 0, extent = { nul_terminated = true }, invalidated_by = [ { any_owner_call = true }, { any_owner_call = true } ]", "FS9202", "borrowed_result.invalidated_by", "borrowed_result invalidated_by declares one event more than once")>]
[<InlineData("content = \"text\", owner = 0", "FS9200", "borrowed_result.owner", "borrowed_result has unknown fields")>]
let ``malformed borrowed result pilot input is refused at its key`` (fields: string) (code: string) (key: string) (message: string) =
    match PilotSerializer.deserializeDiagnosed (Fidelity.Data.TOML.Toml.parseOrFail (pilotWith ("borrowed_result = { " + fields + " }"))) with
    | Error refusal ->
        let d = refusal.Diagnostics |> List.exactlyOne
        Assert.Equal(code, d.Code)
        Assert.Equal(message, d.Message)
        Assert.Equal(Some ("options.bindings[sqlite3_column_text]." + key), d.Pilot |> Option.map _.Key)
    | Ok _ -> failwith "malformed borrowed_result input was accepted"
