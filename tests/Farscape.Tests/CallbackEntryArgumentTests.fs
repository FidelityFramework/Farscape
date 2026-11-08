module Farscape.Tests.CallbackEntryArgumentTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Callback entry arguments beyond ordered integers, and callbacks kept past their call:
// - #189: an opaque record handle crosses as CHandle<record>; the caller's `void*` context
//   crosses as an opaque CHandle<unit> pass-through, declared by `context`; string projections
//   stand beside callback_invocations; an argument with no admitted representation is FS9144.
// - #190: a pointer array counted by another entry argument is a ReadOnlyView of its declared
//   element, described by a CountedEntryArrayDescriptor; an undeclared T** is FS9205.
// - #191: `retained` scope keeps a callback that may be absent until a named release; the entry
//   is capture-free, and a registration pairing it with an environment is FS9207.
// ═══════════════════════════════════════════════════════════════════

let private entryHeader = fixturePath "callbacks/entry_arguments.h"
let private sqliteHeader = "/usr/include/sqlite3.h"

let private generatedWith = generatedClefWith
let private refused = refusedOnce
let private binding = pilotBinding

/// One callback_invocations contract at `parameter` with `scope` and further `fields`.
let private invocation (parameter: int) (identity: string) (scope: string) (fields: string list) =
    let extra = fields |> List.map (fun field -> ", " + field) |> String.concat ""
    $"{{ parameter = {parameter}, identity = \"{identity}\", version = \"1\", source = \"fixture\", scope = \"{scope}\"{extra} }}"

let private absence (identity: string) = $"absence_hypothesis = {{ identity = \"{identity}\", version = \"1\", source = \"fixture\" }}"

let private invocations (contracts: string list) = "callback_invocations = [ " + String.concat ", " contracts + " ]"

let private declaration = declarationText

let private assertLocated (header: string) (d: Diagnostics.Diagnostic) =
    match d.Source with
    | Diagnostics.Located.Known { Path = path } -> Assert.Equal(header, path)
    | other -> failwithf "the refusal is not located at its declaration: %A" other

// ── #189: opaque context and opaque handle entry arguments ──────────

/// sqlite3_exec's honest pilot: an optional row callback activated during the call, its context
/// passed through from parameter 3, its row values and column names counted by the column count.
let private execContract absent =
    invocation 2 "sqlite.exec.row" "during_call"
        [ if absent then absence "sqlite.exec.no-row-callback"
          "context = { argument = 0, parameter = 3 }"
          "counted_arrays = [ { argument = 2, count_argument = 1, element = \"nullable_text\" }, { argument = 3, count_argument = 1, element = \"text\" } ]" ]

[<Fact>]
let ``sqlite3_exec's optional row callback passes its context through as CHandle<unit> and projects sql as string`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #189 input")
    let bindings = binding "sqlite3_exec" [ "string_parameters = [1]"; invocations [ execContract true ] ]
    let text = generatedWith [ sqliteHeader ] bindings [ "sqlite3_exec" ] [ "[annotations.nonnull]"; "sqlite3_exec = [1]" ]
    Assert.Contains(
        "let sqlite3_exec (arg0: option<CHandle<sqlite3>>) (sql: string) (callback: option<FnPtr<option<CHandle<unit>> -> int -> ReadOnlyView<option<string>> -> ReadOnlyView<string> -> int>>) (arg3: option<CHandle<unit>>) (errmsg: option<CHandle<CHandle<int>>>) : int =",
        text)
    // One public extern: an optional entry needs no adapter, and no entry is forwarded.
    Assert.Equal(1, text.Split("let sqlite3_exec ").Length - 1)
    for forbidden in [ "sqlite3_execNative"; ".Invoke"; "nativeint"; "nativeptr" ] do Assert.DoesNotContain(forbidden, text)
    let context = declaration text "sqlite3_execCallback2Context"
    for fact in [ "Expr<CallbackContextDescriptor>"; "BAREWire.Descriptors.CallbackContextDescriptor.Binding = \"Test.Fixture.sqlite3_exec\""
                  "Parameter = 2"; "Argument = 0"; "ContextParameter = 3" ] do
        Assert.Contains(fact, context)
    let contract = declaration text "sqlite3_execCallback2InvocationContract"
    Assert.Contains("{ Name = \"arg0\"; Type = Pointer 64; PassBy = Value } // width declared: pointer under LP64; the caller's opaque context, passed through from parameter 3", contract)
    Assert.Contains("Scope = \"Test.Fixture.sqlite3_execCallback2Scope\"", contract)
    Assert.Contains("Expr<ScopedCallbackDescriptor>", declaration text "sqlite3_execCallback2Scope")
    Assert.Contains("Identity = \"sqlite.exec.no-row-callback\"", declaration text "sqlite3_execCallback2AbsenceContract")
    Assert.Contains("Entry argument `arg0` is the opaque `arg3` passed through unchanged.", text)

[<Fact>]
let ``an entry argument with no admitted representation is refused with FS9144`` () =
    let project, d, text = refused [ entryHeader ] (binding "fx_measure" [ invocations [ invocation 0 "fx.measure" "during_call" [ absence "fx.measure.none" ] ] ]) [ "fx_measure" ]
    Assert.Equal(Diagnostics.CallbackEntryTypeUnadmitted, d.Code)
    Assert.Equal("fx_measure callback_invocations: unsupported native entry signature type 'double'", d.Message)
    Assert.Equal("Binding generation failed: FS9144: " + d.Message, text)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_measure].callback_invocations"; Diagnostics.PilotLocation.Parameter = Some 0 }, d.Pilot)
    assertLocated entryHeader d

[<Fact>]
let ``an untyped context argument without a context declaration is refused with FS9144`` () =
    let contract =
        invocation 2 "fx.exec.row" "during_call"
            [ absence "fx.exec.none"; "counted_arrays = [ { argument = 2, count_argument = 1, element = \"nullable_text\" }, { argument = 3, count_argument = 1, element = \"text\" } ]" ]
    let _, d, _ = refused [ entryHeader ] (binding "fx_exec" [ invocations [ contract ] ]) [ "fx_exec" ]
    Assert.Equal(Diagnostics.CallbackEntryTypeUnadmitted, d.Code)
    Assert.Equal("fx_exec callback_invocations: unsupported native entry signature type 'void *'", d.Message)

[<Theory>]
[<InlineData("fx_set_handler", 1, "{ argument = 1, parameter = 2 }", "fx_set_handler callback_invocations: context: entry argument 1 ('int') is no untyped data pointer (void *)")>]
[<InlineData("fx_exec", 2, "{ argument = 0, parameter = 1 }", "fx_exec callback_invocations: context: parameter 1 ('const char *') is no untyped data pointer (void *)")>]
[<InlineData("fx_exec", 2, "{ argument = 0, parameter = 2 }", "fx_exec callback_invocations: context: parameter 2 is the callback parameter itself")>]
[<InlineData("fx_exec", 2, "{ argument = 4, parameter = 3 }", "fx_exec callback_invocations: context: entry argument 4 is outside the entry's arity 4")>]
let ``a context that is no untyped pointer pass-through is refused with FS9203`` (name: string) (parameter: int) (context: string) (message: string) =
    let arrays =
        if name = "fx_exec" then [ "counted_arrays = [ { argument = 2, count_argument = 1, element = \"nullable_text\" }, { argument = 3, count_argument = 1, element = \"text\" } ]" ]
        else []
    let contract = invocation parameter $"{name}.entry" "during_call" ([ absence $"{name}.none"; $"context = {context}" ] @ arrays)
    let _, d, _ = refused [ entryHeader ] (binding name [ invocations [ contract ] ]) [ name ]
    Assert.Equal(Diagnostics.CallbackContextInvalid, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some $"options.bindings[{name}].callback_invocations", d.Pilot |> Option.map _.Key)
    Assert.Equal(Some (Some parameter), d.Pilot |> Option.map _.Parameter)

// ── #190: counted pointer-array entry arguments ─────────────────────

[<Fact>]
let ``an undeclared pointer-to-pointer entry argument is refused with FS9205`` () =
    let contract = invocation 2 "fx.exec.row" "during_call" [ absence "fx.exec.none"; "context = { argument = 0, parameter = 3 }" ]
    let project, d, text = refused [ entryHeader ] (binding "fx_exec" [ invocations [ contract ] ]) [ "fx_exec" ]
    Assert.Equal(Diagnostics.PointerArrayUndeclared, d.Code)
    Assert.Equal("fx_exec callback_invocations: pointer-to-pointer entry argument 2 ('char **') has no counted_arrays declaration; an undeclared pointer array has no admitted representation", d.Message)
    Assert.Equal("Binding generation failed: FS9205: " + d.Message, text)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_exec].callback_invocations"; Diagnostics.PilotLocation.Parameter = Some 2 }, d.Pilot)
    assertLocated entryHeader d

[<Theory>]
[<InlineData("fx_exec", 2, "{ argument = 2, count_argument = 1, element = \"handle\" }", "fx_exec callback_invocations: counted_arrays argument 2: entry argument 'arg2' ('char **') is no pointer array of opaque handles")>]
[<InlineData("fx_register", 4, "{ argument = 2, count_argument = 1, element = \"text\" }", "fx_register callback_invocations: counted_arrays argument 2: entry argument 'arg2' ('fx_value **') is no pointer array of text (char *)")>]
[<InlineData("fx_register", 4, "{ argument = 2, count_argument = 0, element = \"handle\" }", "fx_register callback_invocations: counted_arrays argument 2: count_argument 0 ('fx_ctx *') is no scalar integer entry argument")>]
[<InlineData("fx_register", 4, "{ argument = 2, count_argument = 2, element = \"handle\" }", "fx_register callback_invocations: counted_arrays argument 2: an array cannot count itself")>]
[<InlineData("fx_register", 4, "{ argument = 2, count_argument = 3, element = \"handle\" }", "fx_register callback_invocations: counted_arrays argument 2: entry argument 3 is outside the entry's arity 3")>]
let ``a counted array that is no pointer array of its element counted by an integer is refused with FS9204`` (name: string) (parameter: int) (array: string) (message: string) =
    let fields =
        [ absence $"{name}.none"; $"counted_arrays = [ {array} ]" ]
        @ (if name = "fx_exec" then [ "context = { argument = 0, parameter = 3 }" ] else [])
    let scope, retention =
        if name = "fx_exec" then "during_call", []
        else "retained", [ "retention = { owner_parameter = 0, releases = [ { binding = \"Test.Fixture.fx_close\", parameter = 0 } ] }" ]
    let destroy =
        if name = "fx_register" then
            [ invocation 5 "fx.register.destroy" "retained"
                [ absence "fx.register.destroy.none"; "context = { argument = 0, parameter = 3 }"
                  "retention = { owner_parameter = 0, releases = [ { binding = \"Test.Fixture.fx_close\", parameter = 0 } ] }" ] ]
        else []
    let _, d, _ = refused [ entryHeader ] (binding name [ invocations (invocation parameter $"{name}.entry" scope (fields @ retention) :: destroy) ]) [ name; "fx_close" ]
    Assert.Equal(Diagnostics.CountedArrayInvalid, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some (Some parameter), d.Pilot |> Option.map _.Parameter)

// ── #191: registration-scoped callbacks that may be absent ──────────

let private closeRelease = "releases = [ { binding = \"Test.Fixture.sqlite3_close_v2\", parameter = 0 } ]"

/// One retained, possibly absent sqlite3_create_function_v2 callback.
let private functionCallback (parameter: int) (name: string) (fields: string list) =
    invocation parameter $"sqlite.function.{name}" "retained"
        ([ absence $"sqlite.function.{name}.absent"; $"retention = {{ owner_parameter = 0, redefined_by = [0, 1, 2, 3], {closeRelease} }}" ] @ fields)

let private argvView = "counted_arrays = [ { argument = 2, count_argument = 1, element = \"handle\" } ]"

[<Fact>]
let ``sqlite3_create_function_v2's callbacks are optional capture-free entries retained until redefinition or close`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #191 input")
    let contracts =
        [ functionCallback 5 "xFunc" [ argvView ]; functionCallback 6 "xStep" [ argvView ]
          functionCallback 7 "xFinal" []; functionCallback 8 "xDestroy" [ "context = { argument = 0, parameter = 4 }" ] ]
    let bindings = binding "sqlite3_create_function_v2" [ "string_parameters = [1]"; invocations contracts ]
    let text = generatedWith [ sqliteHeader ] bindings [ "sqlite3_create_function_v2"; "sqlite3_close_v2" ] [ "[annotations.nonnull]"; "sqlite3_create_function_v2 = [0, 1]" ]
    let functionEntry = "FnPtr<option<CHandle<sqlite3_context>> -> int -> ReadOnlyView<CHandle<sqlite3_value>> -> unit>"
    Assert.Contains(
        $"let sqlite3_create_function_v2 (db: CHandle<sqlite3>) (zFunctionName: string) (nArg: int) (eTextRep: int) (pApp: option<CHandle<unit>>) (xFunc: option<{functionEntry}>) (xStep: option<{functionEntry}>) (xFinal: option<FnPtr<option<CHandle<sqlite3_context>> -> unit>>) (xDestroy: option<FnPtr<option<CHandle<unit>> -> unit>>) : int =",
        text)
    Assert.Equal(1, text.Split("let sqlite3_create_function_v2 ").Length - 1)
    for forbidden in [ "sqlite3_create_function_v2Native"; ".Invoke"; "CHandle<CHandle<sqlite3_value>>" ] do Assert.DoesNotContain(forbidden, text)
    for parameter, name in [ 5, "xFunc"; 6, "xStep"; 7, "xFinal"; 8, "xDestroy" ] do
        let scope = declaration text $"sqlite3_create_function_v2Callback{parameter}Scope"
        for fact in [ "Expr<RetainedCallbackDescriptor>"
                      "BAREWire.Descriptors.RetainedCallbackDescriptor.Binding = \"Test.Fixture.sqlite3_create_function_v2\""
                      $"Parameter = \"{name}\""; "Owner = 0"; "Redefinition [|0; 1; 2; 3|]"
                      "OwnerRelease ({ Binding = \"Test.Fixture.sqlite3_close_v2\"; Parameter = 0 })" ] do
            Assert.Contains(fact, scope)
        Assert.Contains($"Scope = \"Test.Fixture.sqlite3_create_function_v2Callback{parameter}Scope\"", declaration text $"sqlite3_create_function_v2Callback{parameter}InvocationContract")
        Assert.Contains($"Identity = \"sqlite.function.{name}.absent\"", declaration text $"sqlite3_create_function_v2Callback{parameter}AbsenceContract")
        Assert.Contains($"Callback parameter `{name}` is optional; None supplies no callable entry. Activation relies on the declared retained external hypothesis", text)
    for parameter in [ 5; 6 ] do
        let array = declaration text $"sqlite3_create_function_v2Callback{parameter}Argument2CountedArray"
        for fact in [ "Expr<CountedEntryArrayDescriptor>"; $"Parameter = {parameter}"; "Argument = 2"; "CountArgument = 1"; "Element = Handle \"sqlite3_value\"" ] do
            Assert.Contains(fact, array)
    Assert.Contains("ContextParameter = 4", declaration text "sqlite3_create_function_v2Callback8Context")
    Assert.Contains("FnPtr.ofFunction on a closed module function; no environment is captured or retained.", text)

[<Fact>]
let ``sqlite3_busy_handler's handler is retained until replaced or the connection closes, and may be absent`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #191 input")
    let contract =
        invocation 1 "sqlite.busy" "retained"
            [ absence "sqlite.busy.cleared"; "context = { argument = 0, parameter = 2 }"
              $"retention = {{ owner_parameter = 0, redefined_by = [0], {closeRelease} }}" ]
    let text = generatedWith [ sqliteHeader ] (binding "sqlite3_busy_handler" [ invocations [ contract ] ]) [ "sqlite3_busy_handler"; "sqlite3_close_v2" ] []
    Assert.Contains("let sqlite3_busy_handler (arg0: option<CHandle<sqlite3>>) (arg1: option<FnPtr<option<CHandle<unit>> -> int -> int>>) (arg2: option<CHandle<unit>>) : int =", text)
    let scope = declaration text "sqlite3_busy_handlerCallback1Scope"
    Assert.Contains("Redefinition [|0|]", scope)
    Assert.Contains("OwnerRelease ({ Binding = \"Test.Fixture.sqlite3_close_v2\"; Parameter = 0 })", scope)
    Assert.Contains("ContextParameter = 2", declaration text "sqlite3_busy_handlerCallback1Context")

/// fx_register's retained `fn` callback with `retention`, and its `destroy` callback retained
/// until the connection closes, receiving the `app` context.
let private registerContract (retention: string) =
    invocation 4 "fx.register.fn" "retained" [ absence "fx.register.none"; "counted_arrays = [ { argument = 2, count_argument = 1, element = \"handle\" } ]"; $"retention = {retention}" ]
    + ", " + invocation 5 "fx.register.destroy" "retained"
                [ absence "fx.register.destroy.none"; "context = { argument = 0, parameter = 3 }"
                  "retention = { owner_parameter = 0, releases = [ { binding = \"Test.Fixture.fx_close\", parameter = 0 } ] }" ]

[<Fact>]
let ``a retained entry's opaque handle and counted handle arguments generate`` () =
    let text = generatedWith [ entryHeader ] (binding "fx_register" [ invocations [ registerContract "{ owner_parameter = 0, releases = [ { binding = \"Test.Fixture.fx_close\", parameter = 0 } ] }" ] ]) [ "fx_register"; "fx_close" ] []
    Assert.Contains("(fn: option<FnPtr<option<CHandle<fx_ctx>> -> int -> ReadOnlyView<CHandle<fx_value>> -> unit>>) (destroy: option<FnPtr<option<CHandle<unit>> -> unit>>) : int =", text)
    let scope = declaration text "fx_registerCallback4Scope"
    Assert.Contains("OwnerRelease ({ Binding = \"Test.Fixture.fx_close\"; Parameter = 0 })", scope)
    Assert.DoesNotContain("Redefinition", scope)

[<Theory>]
[<InlineData("{ owner_parameter = 0 }", "FS9206", "fx_register callback_invocations: retention names neither redefined_by nor a release; a registration nothing retires is no retained scope")>]
[<InlineData("{ owner_parameter = 1, releases = [ { binding = \"Test.Fixture.fx_close\", parameter = 0 } ] }", "FS9206", "fx_register callback_invocations: retention owner_parameter 1 ('const char *') is no opaque handle that could hold the registration")>]
[<InlineData("{ owner_parameter = 0, redefined_by = [1], releases = [ { binding = \"Test.Fixture.fx_close\", parameter = 0 } ] }", "FS9206", "fx_register callback_invocations: retention redefined_by must include owner_parameter 0: a later call replaces a registration only on its own owner")>]
[<InlineData("{ owner_parameter = 0, releases = [ { binding = \"Test.Fixture.fx_reset\", parameter = 0 } ] }", "FS9206", "fx_register callback_invocations: retention release 'Test.Fixture.fx_reset' parameter 0 receives int, not the owner's CHandle<fx_db>")>]
[<InlineData("{ owner_parameter = 0, releases = [ { binding = \"Test.Fixture.fx_missing\", parameter = 0 } ] }", "FS9141", "fx_register callback_invocations: retention release 'Test.Fixture.fx_missing' must name a selected qualified generated declaration")>]
let ``a retention that does not fit its registration is refused`` (retention: string) (code: string) (message: string) =
    let _, d, _ = refused [ entryHeader ] (binding "fx_register" [ invocations [ registerContract retention ] ]) [ "fx_register"; "fx_close"; "fx_reset" ]
    Assert.Equal(code, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some "options.bindings[fx_register].callback_invocations", d.Pilot |> Option.map _.Key)
    Assert.Equal(Some (Some 4), d.Pilot |> Option.map _.Parameter)

[<Fact>]
let ``a retained scope without its retention is refused at the pilot key`` () =
    let contract = invocation 4 "fx.register.fn" "retained" [ absence "fx.register.none"; "counted_arrays = [ { argument = 2, count_argument = 1, element = \"handle\" } ]" ]
    let project, d, _ = refused [ entryHeader ] (binding "fx_register" [ invocations [ contract ] ]) [ "fx_register" ]
    Assert.Equal(Diagnostics.CallbackScopeFieldConflict, d.Code)
    Assert.Equal("callback_invocations retained requires retention", d.Message)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = project; Diagnostics.PilotLocation.Key = "options.bindings[fx_register].callback_invocations.retention"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)

[<Fact>]
let ``a retained callback registered with a captured environment is refused with FS9207`` () =
    let options = { CppParser.defaultOptions entryHeader with IncludeMacros = false; CppMode = false }
    let declarations =
        match CppParser.parseHeader options with
        | Ok declarations -> declarations
        | Error error -> failwithf "%s did not parse: %s" entryHeader error
    let contract : PilotTypes.BindingCallbackInvocation =
        { Parameter = 1; Identity = "fx.handler"; Version = "1"; Source = "fixture"; Scope = "retained"; ExitBinding = None; SuccessStatus = None
          AbsenceHypothesis = Some { Identity = "fx.handler.cleared"; Version = "1"; Source = "fixture" }
          Context = Some { Argument = 0; Parameter = 2 }; CountedArrays = []
          Retention = Some { OwnerParameter = 0; RedefinedBy = [ 0 ]; Releases = [] } }
    let projection : PilotTypes.BindingProjection =
        { Name = "fx_set_handler"; Symbol = "fx_set_handler"; ReferenceParameters = []; ReadOnlyReferenceParameters = []; ReferenceElements = []
          ConstantParameters = []; StringParameters = []; NonnullCallbacks = []; ParameterHandles = []; ReturnHandle = None; Ownership = None
          ResultRange = None; Resource = None; WritableBuffers = []; CallbackInvocations = [ contract ]; BorrowedResult = None }
    let context = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with Bindings = Map.ofList [ projection.Name, projection ] }
    let generate (data: string option) =
        let spec : PilotTypes.CallbackSpec =
            { Registrations = [ { Function = "fx_set_handler"; CallbackParam = "handler"; DataParam = data } ]; ListenerStructs = [] }
        CallbackWrapperGenerator.generateTypedWithModules spec declarations "Test.Callbacks" context [ "Test.Fixture" ] (fun _ -> "Test.Fixture")
    // Without an environment the retained entry is forwarded capture-free.
    Assert.Contains("Test.Fixture.fx_set_handler db handler arg", (generate None).Value)
    let refusal = Assert.Throws<Diagnostics.RefusalException>(fun () -> generate (Some "arg") |> ignore)
    let d = refusal.Refusal.Diagnostics |> List.exactlyOne
    Assert.Equal(Diagnostics.RetainedCallbackCaptures, d.Code)
    Assert.Equal("Callback registration 'fx_set_handler' pairs retained callback 'handler' with environment parameter 'arg': a retained entry must be capture-free (FnPtr.ofFunction on a closed module function), and a captured environment would be retained with no declared retirement.", d.Message)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = "callbacks.registrations[fx_set_handler].data_param"; Diagnostics.PilotLocation.Parameter = Some 2 }, d.Pilot)
    assertLocated entryHeader d
