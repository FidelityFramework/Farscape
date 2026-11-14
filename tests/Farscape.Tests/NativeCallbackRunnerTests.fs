module Farscape.Tests.NativeCallbackRunnerTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Farscape.Core
open Fidelity.Data.TOML

// ═══════════════════════════════════════════════════════════════════
// Native callback entry ABI acceptance (#207). This module replaces
// tests/native-callbacks/run.py:
// - MlirCallbackEntries reads Composer's `10_output.mlir` into typed records and checks the
//   scalar and the void entry of each witness module: `(i32, i64) -> i32` and
//   `(i32, i64) -> ()`, each defined once, its `func.constant` address the operand of every
//   call of its invoke extern. Each such call is inspected on its own.
// - DischargeReceipts reads Composer's retained discharge receipts into typed records and
//   checks them against the retained obligations and query files.
// - The journey Facts compile tests/native-callbacks/probe.c and other_probe.c with the
//   resolved clang, archive them with llvm-ar, generate bindings with the Farscape source
//   under test, compile the Clef witnesses with Composer.dll and run the linked executables.
// - The refusal Facts require Composer to refuse a mistyped entry, a closure passed to
//   `FnPtr.ofFunction`, and the opaque-handle and context entry that #189 generates and #211
//   has yet to settle.
// - The hygiene Fact refuses a Python file in the repository.
// Every journey works in native-callbacks/<label> under the scratch root of the test process
// (OS temporary storage, farscape-tests/<process>) and retains that directory until the
// process exits; an accepted journey records the SHA-256 of its artifacts beside it in
// <label>.sha256, and the Fact verifies that manifest against the retained files. The unit
// Facts' scratch directories are <name>/<guid> under the same root, deleted when the Fact
// ends. Nothing is written to the checkout (#215).
// Clang and llvm-ar compile and archive the foreign C probes only (ClangToolchain); Composer
// selects its own backend tools. A missing input (fixture, sibling project, Composer.dll,
// clang, llvm-ar, dotnet, git) fails the Fact that needs it; no Fact is skipped.
// ═══════════════════════════════════════════════════════════════════

/// Structured reading of Composer's `10_output.mlir`: a lexer, a parser of `func.func`
/// definitions and declarations with their `func.call` and `func.constant` operations, and
/// the callback entry ABI check over the parsed records.
module MlirCallbackEntries =

    [<RequireQualifiedAccess>]
    type MlirTokenKind =
        /// `@name` or `@"name"`; the token text is the name alone.
        | Symbol
        /// A bare identifier, keyword, number, `%value`, `^block`, `#alias` or `!alias`.
        | Word
        /// A string literal, quotes included.
        | Literal
        /// `->`.
        | Arrow
        /// One punctuation character.
        | Punct

    type MlirToken = { Kind: MlirTokenKind; Text: string; Line: int }

    /// Text the reader cannot structure, at its 1-based line.
    type MlirSyntaxError = { Line: int; Reason: string }

    /// `(inputs) -> results`. Every type is canonical text: the type's tokens joined without
    /// spaces, `, ` after a comma and ` -> ` for an arrow.
    type MlirFunctionType = { Inputs: string list; Results: string list }

    [<RequireQualifiedAccess>]
    type MlirOperation =
        /// `func.call @callee(arguments) : signature`. Each argument is its canonical text:
        /// `%name` for an SSA value.
        | Call of callee: string * arguments: string list * signature: MlirFunctionType
        /// `%result = func.constant @symbol : signature`. `result` is the SSA value the
        /// constant defines, `None` when no `%result =` stands before the operation.
        | Constant of result: string option * symbol: string * signature: MlirFunctionType

    /// One `func.func` parameter: `%name: type` in a definition, `type` in a declaration.
    type MlirParameter = { Name: string option; Type: string }

    type MlirFunction =
        { Symbol: string
          Visibility: string option
          Parameters: MlirParameter list
          /// No result (`-> ()` or no arrow) is the empty list.
          Results: string list
          /// The `func.call` and `func.constant` operations of the body, nested regions
          /// included; `None` for a declaration without a body.
          Body: MlirOperation list option
          Line: int }

    type MlirModule = { Functions: MlirFunction list }

    let private token (kind: MlirTokenKind) (text: string) (line: int) : MlirToken =
        { Kind = kind; Text = text; Line = line }

    let private syntaxError (line: int) (reason: string) : MlirSyntaxError =
        { Line = line; Reason = reason }

    // ─── Lexer ───

    let private isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_' || c = '$' || c = '.' || c = '?'

    let private isSigil (c: char) = c = '%' || c = '^' || c = '#' || c = '!'

    /// The tokens of `text`. Whitespace and `//` comments separate tokens; a `-` belongs to a
    /// word unless `>` follows it.
    let tokenize (text: string) : Result<MlirToken[], MlirSyntaxError> =
        let dash (at: int) = text[at] = '-' && not (at + 1 < text.Length && text[at + 1] = '>')
        let wordEnd (start: int) : int =
            let mutable index = start
            while index < text.Length && (isWordChar (text[index]) || dash index) do
                index <- index + 1
            index
        // The index after the literal whose opening quote is at `start`.
        let literalEnd (start: int) : int option =
            let mutable index = start + 1
            let mutable result = None
            while result.IsNone && index < text.Length && text[index] <> '\n' do
                if text[index] = '\\' then index <- index + 2
                elif text[index] = '"' then result <- Some(index + 1)
                else index <- index + 1
            result
        let tokens = ResizeArray<MlirToken>()
        let mutable failure : MlirSyntaxError option = None
        let mutable line = 1
        let mutable i = 0
        while failure.IsNone && i < text.Length do
            let c = text[i]
            if c = '\n' then
                line <- line + 1
                i <- i + 1
            elif Char.IsWhiteSpace c then
                i <- i + 1
            elif c = '/' && i + 1 < text.Length && text[i + 1] = '/' then
                while i < text.Length && text[i] <> '\n' do
                    i <- i + 1
            elif c = '"' then
                match literalEnd i with
                | Some next ->
                    tokens.Add(token MlirTokenKind.Literal (text.Substring(i, next - i)) line)
                    i <- next
                | None -> failure <- Some(syntaxError line "a string literal is not terminated")
            elif c = '@' && i + 1 < text.Length && text[i + 1] = '"' then
                match literalEnd (i + 1) with
                | Some next ->
                    tokens.Add(token MlirTokenKind.Symbol (text.Substring(i + 2, next - i - 3)) line)
                    i <- next
                | None -> failure <- Some(syntaxError line "a quoted symbol name is not terminated")
            elif c = '@' then
                let next = wordEnd (i + 1)
                if next = i + 1 then
                    failure <- Some(syntaxError line "`@` is not followed by a symbol name")
                else
                    tokens.Add(token MlirTokenKind.Symbol (text.Substring(i + 1, next - i - 1)) line)
                    i <- next
            elif c = '-' && i + 1 < text.Length && text[i + 1] = '>' then
                tokens.Add(token MlirTokenKind.Arrow "->" line)
                i <- i + 2
            elif isWordChar c || isSigil c || c = '-' then
                let next = max (i + 1) (wordEnd (if isSigil c then i + 1 else i))
                tokens.Add(token MlirTokenKind.Word (text.Substring(i, next - i)) line)
                i <- next
            else
                tokens.Add(token MlirTokenKind.Punct (string c) line)
                i <- i + 1
        match failure with
        | Some error -> Error error
        | None -> Ok(tokens.ToArray())

    // ─── Parser ───

    let private isPunct (text: string) (candidate: MlirToken) =
        candidate.Kind = MlirTokenKind.Punct && candidate.Text = text

    let private isWord (text: string) (candidate: MlirToken) =
        candidate.Kind = MlirTokenKind.Word && candidate.Text = text

    /// The index of the token that closes the bracket opened at `start`, counting the
    /// `opening` and `closing` punctuation between them.
    let private closingOf (opening: string list) (closing: string list) (tokens: MlirToken[]) (start: int) : int option =
        let mutable depth = 0
        let mutable index = start
        let mutable result = None
        while result.IsNone && index < tokens.Length do
            let candidate = tokens[index]
            if candidate.Kind = MlirTokenKind.Punct then
                if List.contains candidate.Text opening then
                    depth <- depth + 1
                elif List.contains candidate.Text closing then
                    depth <- depth - 1
                    if depth = 0 then result <- Some index
            index <- index + 1
        result

    let private brackets = [ "("; "["; "<"; "{" ]
    let private closers = [ ")"; "]"; ">"; "}" ]

    /// The end of a bracketed type or argument group, every bracket kind nested.
    let private groupEnd (tokens: MlirToken[]) (start: int) : int option = closingOf brackets closers tokens start

    /// The end of a region or attribute dictionary; only braces nest.
    let private braceEnd (tokens: MlirToken[]) (start: int) : int option = closingOf [ "{" ] [ "}" ] tokens start

    /// The token ranges of `tokens[first .. last - 1]` between its top-level commas.
    let private segments (tokens: MlirToken[]) (first: int) (last: int) : MlirToken[] list =
        if first >= last then []
        else
            let found = ResizeArray<MlirToken[]>()
            let mutable depth = 0
            let mutable from = first
            for index in first .. last - 1 do
                let candidate = tokens[index]
                if candidate.Kind = MlirTokenKind.Punct then
                    if List.contains candidate.Text brackets then
                        depth <- depth + 1
                    elif List.contains candidate.Text closers then
                        depth <- depth - 1
                    elif candidate.Text = "," && depth = 0 then
                        found.Add(tokens[from .. index - 1])
                        from <- index + 1
            found.Add(tokens[from .. last - 1])
            List.ofSeq found

    /// The canonical text of a token range.
    let private render (tokens: MlirToken[]) : string =
        tokens
        |> Array.map (fun candidate ->
            match candidate.Kind with
            | MlirTokenKind.Symbol -> "@" + candidate.Text
            | MlirTokenKind.Arrow -> " -> "
            | MlirTokenKind.Punct when candidate.Text = "," -> ", "
            | _ -> candidate.Text)
        |> String.concat ""

    /// A result list at `start`: a parenthesised type list, or one type (a word with its
    /// optional `<...>` parameters). The types and the index after them.
    let private resultsAt (tokens: MlirToken[]) (start: int) : (string list * int) option =
        if start >= tokens.Length then None
        elif isPunct "(" (tokens[start]) then
            groupEnd tokens start
            |> Option.map (fun close -> (segments tokens (start + 1) close |> List.map render), close + 1)
        elif tokens[start].Kind = MlirTokenKind.Word then
            if start + 1 < tokens.Length && isPunct "<" (tokens[start + 1]) then
                groupEnd tokens (start + 1)
                |> Option.map (fun close -> [ render (tokens[start .. close]) ], close + 1)
            else Some([ tokens[start].Text ], start + 1)
        else None

    /// A function type `(inputs) -> results` at `start`; the type and the index after it.
    let private functionTypeAt (tokens: MlirToken[]) (start: int) : (MlirFunctionType * int) option =
        if start < tokens.Length && isPunct "(" (tokens[start]) then
            match groupEnd tokens start with
            | Some close when close + 1 < tokens.Length && tokens[close + 1].Kind = MlirTokenKind.Arrow ->
                let inputs = segments tokens (start + 1) close |> List.map render
                resultsAt tokens (close + 2)
                |> Option.map (fun (results, next) -> ({ Inputs = inputs; Results = results }: MlirFunctionType), next)
            | _ -> None
        else None

    /// The `func.call` and `func.constant` operations among `tokens[first .. last - 1]`.
    let private operationsIn (tokens: MlirToken[]) (first: int) (last: int) : Result<MlirOperation list, MlirSyntaxError> =
        let found = ResizeArray<MlirOperation>()
        let mutable failure : MlirSyntaxError option = None
        let mutable index = first
        while failure.IsNone && index < last do
            let at = index
            let candidate = tokens[at]
            if isWord "func.call" candidate then
                let parsed =
                    if at + 2 < last && tokens[at + 1].Kind = MlirTokenKind.Symbol && isPunct "(" (tokens[at + 2]) then
                        match groupEnd tokens (at + 2) with
                        | Some close when close + 1 < last && isPunct ":" (tokens[close + 1]) ->
                            let arguments = segments tokens (at + 3) close |> List.map render
                            functionTypeAt tokens (close + 2)
                            |> Option.map (fun (signature, next) -> MlirOperation.Call(tokens[at + 1].Text, arguments, signature), next)
                        | _ -> None
                    else None
                match parsed with
                | Some(operation, next) ->
                    found.Add operation
                    index <- next
                | None ->
                    failure <- Some(syntaxError candidate.Line "func.call is not `@callee(arguments) : (inputs) -> results`")
            elif isWord "func.constant" candidate then
                // `%result =` before the operation, within the body.
                let result =
                    if at >= first + 2
                       && isPunct "=" (tokens[at - 1])
                       && tokens[at - 2].Kind = MlirTokenKind.Word
                       && tokens[at - 2].Text.StartsWith('%') then
                        Some(tokens[at - 2].Text)
                    else None
                let parsed =
                    if at + 2 < last && tokens[at + 1].Kind = MlirTokenKind.Symbol && isPunct ":" (tokens[at + 2]) then
                        functionTypeAt tokens (at + 3)
                        |> Option.map (fun (signature, next) -> MlirOperation.Constant(result, tokens[at + 1].Text, signature), next)
                    else None
                match parsed with
                | Some(operation, next) ->
                    found.Add operation
                    index <- next
                | None ->
                    failure <- Some(syntaxError candidate.Line "func.constant is not `@symbol : (inputs) -> results`")
            else
                index <- index + 1
        match failure with
        | Some error -> Error error
        | None -> Ok(List.ofSeq found)

    let private visibilities = [ "private"; "public"; "nested" ]

    /// The `func.func` whose keyword is at `start`; the function and the index after it.
    let private functionAt (tokens: MlirToken[]) (start: int) : Result<MlirFunction * int, MlirSyntaxError> =
        let line = tokens[start].Line
        let refuse (reason: string) : Result<MlirFunction * int, MlirSyntaxError> = Error(syntaxError line reason)
        let visibility, nameAt =
            if start + 1 < tokens.Length
               && tokens[start + 1].Kind = MlirTokenKind.Word
               && List.contains (tokens[start + 1].Text) visibilities then
                Some(tokens[start + 1].Text), start + 2
            else None, start + 1
        if nameAt + 1 >= tokens.Length
           || tokens[nameAt].Kind <> MlirTokenKind.Symbol
           || not (isPunct "(" (tokens[nameAt + 1])) then
            refuse "func.func is not followed by `@name(`"
        else
            let symbol = tokens[nameAt].Text
            match groupEnd tokens (nameAt + 1) with
            | None -> refuse $"the parameter list of @{symbol} is not closed"
            | Some close ->
                let parameters : MlirParameter list =
                    segments tokens (nameAt + 2) close
                    |> List.map (fun segment ->
                        if segment.Length >= 3
                           && segment[0].Kind = MlirTokenKind.Word
                           && segment[0].Text.StartsWith('%')
                           && isPunct ":" (segment[1]) then
                            { Name = Some(segment[0].Text); Type = render (segment[2..]) }
                        else { Name = None; Type = render segment })
                let results =
                    if close + 1 < tokens.Length && tokens[close + 1].Kind = MlirTokenKind.Arrow then
                        resultsAt tokens (close + 2)
                    else Some([], close + 1)
                match results with
                | None -> refuse $"the result of @{symbol} is neither a type nor a parenthesised type list"
                | Some(results, afterResults) ->
                    // `attributes { ... }` may stand between the signature and the body.
                    let bodyAt =
                        if afterResults + 1 < tokens.Length
                           && isWord "attributes" (tokens[afterResults])
                           && isPunct "{" (tokens[afterResults + 1]) then
                            braceEnd tokens (afterResults + 1) |> Option.map (fun attributesEnd -> attributesEnd + 1)
                        else Some afterResults
                    let build (body: MlirOperation list option) : MlirFunction =
                        { Symbol = symbol; Visibility = visibility; Parameters = parameters
                          Results = results; Body = body; Line = line }
                    match bodyAt with
                    | None -> refuse $"the attribute dictionary of @{symbol} is not closed"
                    | Some bodyAt when bodyAt < tokens.Length && isPunct "{" (tokens[bodyAt]) ->
                        match braceEnd tokens bodyAt with
                        | None -> refuse $"the body of @{symbol} is not closed"
                        | Some bodyEnd ->
                            operationsIn tokens (bodyAt + 1) bodyEnd
                            |> Result.map (fun operations -> build (Some operations), bodyEnd + 1)
                    | Some declarationEnd -> Ok(build None, declarationEnd)

    /// Every `func.func` of `text`, in source order.
    let parse (text: string) : Result<MlirModule, MlirSyntaxError> =
        tokenize text
        |> Result.bind (fun tokens ->
            let functions = ResizeArray<MlirFunction>()
            let mutable failure : MlirSyntaxError option = None
            let mutable index = 0
            while failure.IsNone && index < tokens.Length do
                if isWord "func.func" (tokens[index]) then
                    match functionAt tokens index with
                    | Ok(parsed, next) ->
                        functions.Add parsed
                        index <- next
                    | Error error -> failure <- Some error
                else
                    index <- index + 1
            match failure with
            | Some error -> Error error
            | None -> Ok { Functions = List.ofSeq functions })

    // ─── Callback entry ABI ───

    /// The native parameters of both entries, `(unsigned int value, long delta)` under LP64.
    let entryParameters = [ "i32"; "i64" ]

    /// The native result of the scalar entry, `int`. The void entry has none.
    let scalarEntryResults = [ "i32" ]

    /// The signatures of the entries' address constants.
    let scalarEntryAddress : MlirFunctionType = { Inputs = entryParameters; Results = scalarEntryResults }
    let voidEntryAddress : MlirFunctionType = { Inputs = entryParameters; Results = [] }

    /// The native result of both invoke externs, `int`.
    let invokeResults = [ "i32" ]

    /// The canonical text of `signature` as a type: `(i32, i64) -> i32`, `(i32, i64) -> ()`.
    let private functionTypeText (signature: MlirFunctionType) : string =
        let results =
            match signature.Results with
            | [ result ] -> result
            | results -> "(" + String.concat ", " results + ")"
        "(" + String.concat ", " signature.Inputs + ") -> " + results

    /// The signature of a `func.call` of the invoke extern whose one parameter is an entry of
    /// the signature `entry`: `((i32, i64) -> i32) -> i32` and `((i32, i64) -> ()) -> i32`.
    let invokeSignature (entry: MlirFunctionType) : MlirFunctionType =
        { Inputs = [ functionTypeText entry ]; Results = invokeResults }

    /// One witness module: the externs that receive the address of its scalar and of its void
    /// entry, and the extern its void entry calls with the value C then reads.
    type EntryExpectation = { Module: string; ScalarInvoke: string; VoidInvoke: string; Record: string }

    [<RequireQualifiedAccess>]
    type EntryRefusal =
        | Malformed of MlirSyntaxError
        /// No `func.func @<entry>` exists.
        | EntryMissing of entry: string
        | EntryAmbiguous of entry: string * definitions: int
        /// `@<entry>` is declared without a body.
        | EntryUndefined of entry: string
        | EntryParameters of entry: string * expected: string list * actual: string list
        | EntryResults of entry: string * expected: string list * actual: string list
        /// No `func.constant @<entry>` exists in the module.
        | EntryAddressMissing of entry: string
        /// `actual` holds each signature of a `func.constant @<entry>` other than `expected`.
        | EntryAddressSignature of entry: string * expected: MlirFunctionType * actual: MlirFunctionType list
        /// The module holds no `func.call @<invoke>`.
        | InvokeCallMissing of invoke: string
        /// A `func.call @<invoke>` in the body of `@<caller>` has the signature `actual`, not
        /// the invoke extern's signature `expected`.
        | InvokeSignature of invoke: string * caller: string * expected: MlirFunctionType * actual: MlirFunctionType
        /// A `func.call @<invoke>` in the body of `@<caller>` does not pass exactly one operand
        /// that exactly one `func.constant` of that body defines. `operands` holds the call's
        /// arguments: a function parameter, a value another operation or another function's
        /// body defines, a value two constants define, or another number of arguments than one.
        | InvokeOperandUnresolved of invoke: string * caller: string * operands: string list
        /// The calls of `@<invoke>` whose operand is resolved do not all receive the address
        /// of the one expected entry. `passed` holds each function whose address such a call
        /// receives, once, in source order: a function other than `@<module>.scalarEntry` for
        /// the scalar invoke extern, or several functions.
        | InvokeAddresses of invoke: string * passed: string list
        /// `@<thunk>` calls `@<entry>`: the address passed is a wrapper's, not the entry's.
        | EntryWrapped of entry: string * thunk: string
        /// `@<entry>` calls the record extern of another witness module.
        | ForeignRecordCall of entry: string * record: string
        /// The body of the void entry holds no `func.call @<record>`.
        | RecordCallMissing of entry: string * record: string

    /// The accepted entries of one Clef module.
    type CallbackEntryEvidence =
        { EntryModule: string
          ScalarEntry: MlirFunction
          ScalarEntryAddress: MlirFunctionType
          VoidEntry: MlirFunction
          VoidEntryAddress: MlirFunctionType
          RecordSymbol: string }

    /// `__clef_callback_<N>`: the prefix followed by one or more decimal digits.
    let isCallbackThunk (symbol: string) : bool =
        let prefix = "__clef_callback_"
        symbol.StartsWith(prefix, StringComparison.Ordinal)
        && symbol.Length > prefix.Length
        && (symbol.Substring(prefix.Length) |> Seq.forall Char.IsAsciiDigit)

    let private parameterTypes (f: MlirFunction) : string list = f.Parameters |> List.map (fun parameter -> parameter.Type)

    let private operationsOf (f: MlirFunction) : MlirOperation list = f.Body |> Option.defaultValue []

    /// The callee of every `func.call` in the body of `f`, nested regions included, in source order.
    let private calleesOf (f: MlirFunction) : string list =
        operationsOf f
        |> List.choose (fun operation ->
            match operation with
            | MlirOperation.Call(called, _, _) -> Some called
            | _ -> None)

    /// Whether the body of `f` holds a `func.call @<callee>`, nested regions included.
    let private calls (callee: string) (f: MlirFunction) : bool = List.contains callee (calleesOf f)

    /// The signature of every `func.constant @<symbol>` of the module, in source order.
    let private addressesOf (mlir: MlirModule) (symbol: string) : MlirFunctionType list =
        mlir.Functions
        |> List.collect operationsOf
        |> List.choose (fun operation ->
            match operation with
            | MlirOperation.Constant(_, constant, signature) when constant = symbol -> Some signature
            | _ -> None)

    /// Every `func.call @<invoke>` of the module, each inspected on its own, nested regions
    /// included:
    /// - its signature is `expected`, else `InvokeSignature`;
    /// - it passes one operand, and exactly one `func.constant` in the body of the calling
    ///   function defines that operand, else `InvokeOperandUnresolved`.
    /// A module without such a call yields `InvokeCallMissing`.
    /// The result is the functions whose address the resolved calls receive (each once, in
    /// source order) and the refusals in source order. Every call contributes a function or an
    /// `InvokeOperandUnresolved`, so an empty function list always comes with a refusal.
    let private inspectInvokeCalls (mlir: MlirModule) (invoke: string) (expected: MlirFunctionType)
        : string list * EntryRefusal list =
        let inspected =
            mlir.Functions
            |> List.collect (fun caller ->
                let operations = operationsOf caller
                let definitionsOf (operand: string) : string list =
                    operations
                    |> List.choose (fun operation ->
                        match operation with
                        | MlirOperation.Constant(Some result, symbol, _) when result = operand -> Some symbol
                        | _ -> None)
                operations
                |> List.choose (fun operation ->
                    match operation with
                    | MlirOperation.Call(called, arguments, signature) when called = invoke ->
                        let mistyped =
                            if signature = expected then []
                            else [ EntryRefusal.InvokeSignature(invoke, caller.Symbol, expected, signature) ]
                        let resolved =
                            match arguments with
                            | [ operand ] ->
                                match definitionsOf operand with
                                | [ symbol ] -> Some symbol
                                | _ -> None
                            | _ -> None
                        match resolved with
                        | Some symbol -> Some(Some symbol, mistyped)
                        | None -> Some(None, mistyped @ [ EntryRefusal.InvokeOperandUnresolved(invoke, caller.Symbol, arguments) ])
                    | _ -> None))
        if inspected.IsEmpty then [], [ EntryRefusal.InvokeCallMissing invoke ]
        else inspected |> List.choose fst |> List.distinct, inspected |> List.collect snd

    /// The one definition of the entry `symbol` with native results `results`, or its refusals
    /// in this order: undefined, parameters, results, address, wrappers, calls of a `foreign`
    /// record extern, then `further`.
    let private inspectEntry (mlir: MlirModule) (symbol: string) (results: string list) (foreign: string list)
                             (further: MlirFunction -> EntryRefusal list) : Result<MlirFunction, EntryRefusal list> =
        match mlir.Functions |> List.filter (fun f -> f.Symbol = symbol) with
        | [] -> Error [ EntryRefusal.EntryMissing symbol ]
        | [ entry ] ->
            let expected : MlirFunctionType = { Inputs = entryParameters; Results = results }
            let addresses = addressesOf mlir symbol
            let deviating = addresses |> List.filter (fun address -> address <> expected) |> List.distinct
            let wrappers =
                mlir.Functions
                |> List.filter (fun f -> isCallbackThunk f.Symbol && calls symbol f)
                |> List.map (fun f -> f.Symbol)
            let own =
                [ if entry.Body.IsNone then
                      EntryRefusal.EntryUndefined symbol
                  if parameterTypes entry <> entryParameters then
                      EntryRefusal.EntryParameters(symbol, entryParameters, parameterTypes entry)
                  if entry.Results <> results then
                      EntryRefusal.EntryResults(symbol, results, entry.Results)
                  if addresses.IsEmpty then
                      EntryRefusal.EntryAddressMissing symbol
                  elif not deviating.IsEmpty then
                      EntryRefusal.EntryAddressSignature(symbol, expected, deviating) ]
            let wrapped = wrappers |> List.map (fun thunk -> EntryRefusal.EntryWrapped(symbol, thunk))
            let crossed =
                foreign
                |> List.filter (fun record -> calls record entry)
                |> List.map (fun record -> EntryRefusal.ForeignRecordCall(symbol, record))
            match own @ wrapped @ crossed @ further entry with
            | [] -> Ok entry
            | refusals -> Error refusals
        | several -> Error [ EntryRefusal.EntryAmbiguous(symbol, several.Length) ]

    /// The entries of `expected.Module` in `mlir`. Composer (observed at v0.0.2+d17eb195) emits
    /// the scalar entry as `@<module>.scalarEntry` and a unit-returning entry as
    /// `@__clef_callback_<N>`, without a `@<module>.voidEntry`; the generated wrapper of each
    /// invoke extern takes the entry's address and passes it to the extern.
    /// (a) The module calls `@<expected.ScalarInvoke>` at least once. Every such call has the
    ///     signature `((i32, i64) -> i32) -> i32` and passes one operand that a `func.constant`
    ///     of the calling function defines, and every such operand is the address of
    ///     `@<module>.scalarEntry`. That entry is defined once, with a body, the parameters
    ///     `i32, i64` and the result `i32`.
    /// (b) The module calls `@<expected.VoidInvoke>` at least once. Every such call has the
    ///     signature `((i32, i64) -> ()) -> i32` and passes one operand that a `func.constant`
    ///     of the calling function defines, and every such operand is the address of one
    ///     function: the void entry. It is defined once, with a body, the parameters `i32, i64`
    ///     and no result.
    /// (c) Every `func.constant` of each entry in the module has the entry's own signature:
    ///     `(i32, i64) -> i32` and `(i32, i64) -> ()`.
    /// (d) No `__clef_callback_<N>` function calls either entry, and the void entry calls no
    ///     function of the witness module: each address is the entry's, not a wrapper's.
    /// (e) The body of the void entry calls `@<expected.Record>`, and neither entry calls a
    ///     record extern of `foreign`, the other witness modules.
    /// One call that satisfies (a) or (b) does not admit another call of the same extern that
    /// does not. The refusals are the scalar entry's, the scalar invoke calls', the void invoke
    /// calls' and the void entry's, in that order.
    let inspectModule (mlir: MlirModule) (foreign: string list) (expected: EntryExpectation)
        : Result<CallbackEntryEvidence, EntryRefusal list> =
        let scalarSymbol = expected.Module + ".scalarEntry"
        let ofWitness (symbol: string) = symbol.StartsWith(expected.Module + ".", StringComparison.Ordinal)
        let unwrappedAndRecording (entry: MlirFunction) : EntryRefusal list =
            let forwarded =
                calleesOf entry
                |> List.filter ofWitness
                |> List.distinct
                |> List.map (fun called -> EntryRefusal.EntryWrapped(called, entry.Symbol))
            let unrecorded =
                if entry.Body.IsSome && not (calls expected.Record entry) then
                    [ EntryRefusal.RecordCallMissing(entry.Symbol, expected.Record) ]
                else []
            forwarded @ unrecorded
        let scalarPassed, scalarCalls = inspectInvokeCalls mlir expected.ScalarInvoke (invokeSignature scalarEntryAddress)
        let scalarInvoked =
            match scalarPassed with
            // No call is resolved: `scalarCalls` holds the refusal of each call, or of their absence.
            | [] -> scalarCalls
            | [ passed ] when passed = scalarSymbol -> scalarCalls
            | passed -> scalarCalls @ [ EntryRefusal.InvokeAddresses(expected.ScalarInvoke, passed) ]
        let scalar =
            match inspectEntry mlir scalarSymbol scalarEntryResults foreign (fun _ -> []), scalarInvoked with
            | Ok entry, [] -> Ok entry
            | Ok _, refusals -> Error refusals
            | Error refusals, invoked -> Error(refusals @ invoked)
        let voidPassed, voidCalls = inspectInvokeCalls mlir expected.VoidInvoke (invokeSignature voidEntryAddress)
        let recorded =
            match voidPassed with
            // No call is resolved: `voidCalls` holds the refusal of each call, or of their absence.
            | [] -> Error voidCalls
            | [ voidSymbol ] ->
                match inspectEntry mlir voidSymbol [] foreign unwrappedAndRecording, voidCalls with
                | Ok entry, [] -> Ok entry
                | Ok _, refusals -> Error refusals
                | Error refusals, invoked -> Error(invoked @ refusals)
            | passed -> Error(voidCalls @ [ EntryRefusal.InvokeAddresses(expected.VoidInvoke, passed) ])
        match scalar, recorded with
        | Ok scalarEntry, Ok voidEntry ->
            Ok { EntryModule = expected.Module
                 ScalarEntry = scalarEntry
                 ScalarEntryAddress = scalarEntryAddress
                 VoidEntry = voidEntry
                 VoidEntryAddress = voidEntryAddress
                 RecordSymbol = expected.Record }
        | Error scalarRefusals, Error voidRefusals -> Error(scalarRefusals @ voidRefusals)
        | Error refusals, Ok _ -> Error refusals
        | Ok _, Error refusals -> Error refusals

    /// The entries of every module of `expected` in the MLIR `text`, or every refusal in module order.
    let inspect (text: string) (expected: EntryExpectation list) : Result<CallbackEntryEvidence list, EntryRefusal list> =
        match parse text with
        | Error error -> Error [ EntryRefusal.Malformed error ]
        | Ok mlir ->
            let inspected =
                expected
                |> List.map (fun own ->
                    let foreign =
                        expected
                        |> List.map (fun other -> other.Record)
                        |> List.filter (fun record -> record <> own.Record)
                        |> List.distinct
                    inspectModule mlir foreign own)
            let refusals = inspected |> List.collect (fun result -> match result with Error refusals -> refusals | Ok _ -> [])
            if refusals.IsEmpty then
                Ok(inspected |> List.choose (fun result -> match result with Ok evidence -> Some evidence | Error _ -> None))
            else Error refusals

open MlirCallbackEntries

// ─── MLIR inspector Facts ───

let private boundaryModule = "Landing.Acceptance.Boundary"
let private otherBoundaryModule = "Landing.Acceptance.OtherBoundary"

/// The witness of `probe.h` and the witness of `other_probe.h`.
let private boundaryEntries : EntryExpectation =
    { Module = boundaryModule; ScalarInvoke = "landing_invoke"; VoidInvoke = "landing_invoke_void"; Record = "landing_record" }
let private otherBoundaryEntries : EntryExpectation =
    { Module = otherBoundaryModule; ScalarInvoke = "landing_other_invoke"; VoidInvoke = "landing_other_invoke_void"; Record = "landing_other_record" }

/// One witness module of the sample MLIR: its name, the namespace of its generated bindings,
/// the prefix of its externs (`landing` or `landing_other`) and the symbol of its void entry.
type private SampleWitness = { ModuleName: string; BindingsNamespace: string; ExternPrefix: string; VoidEntrySymbol: string }

let private boundaryWitness : SampleWitness =
    { ModuleName = boundaryModule; BindingsNamespace = "Landing.Boundary"; ExternPrefix = "landing"; VoidEntrySymbol = "__clef_callback_5314" }

let private otherBoundaryWitness : SampleWitness =
    { ModuleName = otherBoundaryModule; BindingsNamespace = "Landing.Other.Boundary"; ExternPrefix = "landing_other"; VoidEntrySymbol = "__clef_callback_5460" }

/// The functions of one witness module, modelled on the `10_output.mlir` Composer
/// v0.0.2+d17eb195 retains for tests/native-callbacks/Boundary.clef, in the shape Composer
/// prints it (two-space indented `func.func` headers, bodies closed at column 0, nested regions):
/// - the generated wrappers, which take each entry's address and pass it to the invoke extern;
/// - the scalar entry under its own name and the void entry as `@__clef_callback_<N>`;
/// - the witness functions, which take the same addresses and pass the wrappers a record.
let private witnessFunctions (witness: SampleWitness) : string list =
    let scalar = witness.ModuleName + ".scalarEntry"
    let wrapper = witness.BindingsNamespace + "." + witness.ExternPrefix
    [ "  func.func @" + wrapper + "_invoke(%arg0: memref<0xi8>) -> i32 {"
      "    %v40_0 = func.constant @" + scalar + " : (i32, i64) -> i32"
      "    %v40_1 = func.call @" + witness.ExternPrefix + "_invoke(%v40_0) : ((i32, i64) -> i32) -> i32"
      "    func.return %v40_1 : i32"
      "}"
      "  func.func @" + wrapper + "_invoke_void(%arg0: memref<0xi8>) -> i32 {"
      "    %v41_0 = func.constant @" + witness.VoidEntrySymbol + " : (i32, i64) -> ()"
      "    %v41_1 = func.call @" + witness.ExternPrefix + "_invoke_void(%v41_0) : ((i32, i64) -> ()) -> i32"
      "    func.return %v41_1 : i32"
      "}"
      "  func.func @" + scalar + "(%arg0: i32, %arg1: i64) -> i32 {"
      "    %v10_0 = arith.constant 4294967295 : i32"
      "    %v10_1 = arith.cmpi eq, %arg0, %v10_0 : i32"
      "    %v10_2 = scf.if %v10_1 -> (i32) {"
      "      %v10_3 = arith.constant -7 : i8"
      "      %v10_4 = arith.extsi %v10_3 : i8 to i32"
      "      scf.yield %v10_4 : i32"
      "    } else {"
      "      %v10_5 = arith.constant 9 : i8"
      "      %v10_6 = arith.extui %v10_5 : i8 to i32"
      "      scf.yield %v10_6 : i32"
      "    }"
      "    func.return %v10_2 : i32"
      "}"
      "  func.func @" + witness.VoidEntrySymbol + "(%arg0: i32, %arg1: i64) -> () {"
      "    %v20_0 = arith.constant -173 : i16"
      "    %v20_4 = arith.extsi %v20_0 : i16 to i64"
      "    %v20_1 = arith.cmpi eq, %arg1, %v20_4 : i64"
      "    scf.if %v20_1 {"
      "      %v20_2 = arith.constant 346 : i32"
      "      func.call @" + witness.ExternPrefix + "_record(%v20_2) : (i32) -> ()"
      "      scf.yield"
      "    } else {"
      "      %v20_3 = arith.constant -1 : i32"
      "      func.call @" + witness.ExternPrefix + "_record(%v20_3) : (i32) -> ()"
      "      scf.yield"
      "    }"
      "    func.return"
      "}"
      "  func.func @" + witness.ModuleName + ".invokeScalar() -> i32 {"
      "    %v30_0 = func.constant @" + scalar + " : (i32, i64) -> i32"
      "    %v30_1 = memref.alloc() : memref<0xi8>"
      "    %v30_2 = func.call @" + wrapper + "_invoke(%v30_1) : (memref<0xi8>) -> i32"
      "    func.return %v30_2 : i32"
      "}"
      "  func.func @" + witness.ModuleName + ".invokeVoid() -> i32 {"
      "    %v31_0 = func.constant @" + witness.VoidEntrySymbol + " : (i32, i64) -> ()"
      "    %v31_1 = memref.alloc() : memref<0xi8>"
      "    %v31_2 = func.call @" + wrapper + "_invoke_void(%v31_1) : (memref<0xi8>) -> i32"
      "    func.return %v31_2 : i32"
      "}"
      "  func.func @" + witness.ModuleName + ".run() -> i8 {"
      "    %v32_0 = func.call @" + witness.ModuleName + ".invokeScalar() : () -> i32"
      "    %v32_1 = func.call @" + witness.ModuleName + ".invokeVoid() : () -> i32"
      "    %v32_2 = arith.ori %v32_0, %v32_1 : i32"
      "    %v32_3 = arith.trunci %v32_2 : i32 to i8"
      "    func.return %v32_3 : i8"
      "}" ]

/// A module holding the functions of each witness, a global whose attribute dictionary and
/// string literals hold braces, and the extern declarations.
let private sampleMlir (witnesses: SampleWitness list) : string =
    [ [ "module @main {"
        "  func.func @main(%arg0: memref<?xmemref<?xi8>>) -> i64 {"
        "    %v1_0 = func.call @Landing.Acceptance.Main.main() : () -> i32"
        "    %v1_1 = arith.extsi %v1_0 : i32 to i64"
        "    func.return %v1_1 : i64"
        "}"
        "  // a comment holding a brace { and func.func @ignored("
        "  memref.global \"private\" constant @__clef_static_strings : memref<8xi8> = dense<[108, 97, 110, 100, 105, 110, 103, 0]> {alignment = 16 : i64, clef.obligations = [\"layout_user_strings\", \"a } and a { inside a literal\"]}" ]
      witnesses |> List.collect witnessFunctions
      witnesses
      |> List.collect (fun witness ->
          [ "  func.func private @" + witness.ExternPrefix + "_invoke((i32, i64) -> i32) -> i32"
            "  func.func private @" + witness.ExternPrefix + "_invoke_void((i32, i64) -> ()) -> i32"
            "  func.func private @" + witness.ExternPrefix + "_record(i32) -> ()" ])
      [ "}" ] ]
    |> List.concat
    |> String.concat "\n"

let private singleEntryMlir = sampleMlir [ boundaryWitness ]
let private duplicateEntryMlir = sampleMlir [ boundaryWitness; otherBoundaryWitness ]

/// `text` with its one occurrence of `original` replaced; another occurrence count fails.
let private mutate (original: string) (replacement: string) (text: string) : string =
    let occurrences = (text.Length - text.Replace(original, "").Length) / original.Length
    if occurrences <> 1 then
        failwithf "the mutation target occurs %d times, expected once: %s" occurrences original
    text.Replace(original, replacement)

let private same<'T when 'T: equality> (expected: 'T) (actual: 'T) =
    if expected <> actual then failwithf "expected %A\nactual   %A" expected actual

let private refusedWith (expected: EntryRefusal list) (modules: EntryExpectation list) (text: string) =
    same (Error expected: Result<CallbackEntryEvidence list, EntryRefusal list>) (inspect text modules)

let private functionNamed (symbol: string) (mlir: MlirModule) : MlirFunction =
    match mlir.Functions |> List.filter (fun f -> f.Symbol = symbol) with
    | [ found ] -> found
    | other -> failwithf "@%s is parsed %d times" symbol other.Length

let private typesOf (f: MlirFunction) : string list = f.Parameters |> List.map (fun parameter -> parameter.Type)

let private scalarSymbol = boundaryModule + ".scalarEntry"
let private voidSymbol = boundaryWitness.VoidEntrySymbol
let private otherScalarSymbol = otherBoundaryModule + ".scalarEntry"
let private otherVoidSymbol = otherBoundaryWitness.VoidEntrySymbol
let private scalarHeader = ".scalarEntry(%arg0: i32, %arg1: i64) -> i32 {"
let private voidHeader = "@" + voidSymbol + "(%arg0: i32, %arg1: i64) -> () {"
let private scalarAddress = "func.constant @" + scalarSymbol + " : (i32, i64) -> i32"
let private voidAddress = "func.constant @" + voidSymbol + " : (i32, i64) -> ()"

/// The sample's lines that take an entry's address: `%v40_0` and `%v41_0` in the generated
/// wrappers, whose values the invoke externs receive; `%v30_0` and `%v31_0` in the witness functions.
let private passedScalarAddress = "    %v40_0 = " + scalarAddress + "\n"
let private passedVoidAddress = "    %v41_0 = " + voidAddress + "\n"
let private witnessScalarAddress = "    %v30_0 = " + scalarAddress + "\n"
let private witnessVoidAddress = "    %v31_0 = " + voidAddress + "\n"

[<Fact>]
let ``the MLIR reader parses definitions, declarations, nested regions and their calls and constants`` () =
    let mlir =
        match parse singleEntryMlir with
        | Ok mlir -> mlir
        | Error error -> failwithf "the sample is refused at line %d: %s" error.Line error.Reason
    // The comment's `func.func @ignored(` and the literals' braces are not parsed.
    same
        [ "main"; "Landing.Boundary.landing_invoke"; "Landing.Boundary.landing_invoke_void"; scalarSymbol; voidSymbol
          boundaryModule + ".invokeScalar"; boundaryModule + ".invokeVoid"; boundaryModule + ".run"
          "landing_invoke"; "landing_invoke_void"; "landing_record" ]
        (mlir.Functions |> List.map (fun f -> f.Symbol))
    let entry = functionNamed "main" mlir
    same [ ({ Name = Some "%arg0"; Type = "memref<?xmemref<?xi8>>" }: MlirParameter) ] entry.Parameters
    same [ "i64" ] entry.Results
    same None entry.Visibility
    same (Some [ MlirOperation.Call("Landing.Acceptance.Main.main", [], { Inputs = []; Results = [ "i32" ] }) ]) entry.Body
    // A body without a call or a constant is a body, not a declaration.
    let scalar = functionNamed scalarSymbol mlir
    same [ ({ Name = Some "%arg0"; Type = "i32" }: MlirParameter); { Name = Some "%arg1"; Type = "i64" } ] scalar.Parameters
    same [ "i32" ] scalar.Results
    same (Some []: MlirOperation list option) scalar.Body
    same 19 scalar.Line
    // The nested scf.if regions do not end the body: the calls of both regions are the body's.
    let recorded = functionNamed voidSymbol mlir
    same ([]: string list) recorded.Results
    same
        (Some [ MlirOperation.Call("landing_record", [ "%v20_2" ], { Inputs = [ "i32" ]; Results = [] })
                MlirOperation.Call("landing_record", [ "%v20_3" ], { Inputs = [ "i32" ]; Results = [] }) ])
        recorded.Body
    // A generated wrapper takes the entry's address and passes that value to its extern.
    let wrapper = functionNamed "Landing.Boundary.landing_invoke" mlir
    same [ ({ Name = Some "%arg0"; Type = "memref<0xi8>" }: MlirParameter) ] wrapper.Parameters
    same
        (Some [ MlirOperation.Constant(Some "%v40_0", scalarSymbol, { Inputs = [ "i32"; "i64" ]; Results = [ "i32" ] })
                MlirOperation.Call("landing_invoke", [ "%v40_0" ], { Inputs = [ "(i32, i64) -> i32" ]; Results = [ "i32" ] }) ])
        wrapper.Body
    // A witness function takes the entry's address and passes the wrapper its record.
    let invokeVoid = functionNamed (boundaryModule + ".invokeVoid") mlir
    same
        (Some [ MlirOperation.Constant(Some "%v31_0", voidSymbol, { Inputs = [ "i32"; "i64" ]; Results = [] })
                MlirOperation.Call("Landing.Boundary.landing_invoke_void", [ "%v31_1" ], { Inputs = [ "memref<0xi8>" ]; Results = [ "i32" ] }) ])
        invokeVoid.Body
    let run = functionNamed (boundaryModule + ".run") mlir
    same [ "i8" ] run.Results
    same
        (Some [ MlirOperation.Call(boundaryModule + ".invokeScalar", [], { Inputs = []; Results = [ "i32" ] })
                MlirOperation.Call(boundaryModule + ".invokeVoid", [], { Inputs = []; Results = [ "i32" ] }) ])
        run.Body
    // A declaration has unnamed parameters, a visibility and no body.
    let declared = functionNamed "landing_invoke" mlir
    same (Some "private") declared.Visibility
    same [ ({ Name = None; Type = "(i32, i64) -> i32" }: MlirParameter) ] declared.Parameters
    same [ "i32" ] declared.Results
    same None declared.Body
    let record = functionNamed "landing_record" mlir
    same [ ({ Name = None; Type = "i32" }: MlirParameter) ] record.Parameters
    same ([]: string list) record.Results
    same None record.Body

[<Fact>]
let ``the MLIR reader reads the arguments of a call and the result of a constant`` () =
    let bodyOf (lines: string list) : MlirOperation list option =
        match parse (String.concat "\n" ([ "module @main {"; "  func.func @f(%arg0: i32) -> i32 {" ] @ lines @ [ "}"; "}" ])) with
        | Ok mlir -> (functionNamed "f" mlir).Body
        | Error error -> failwithf "the body is refused at line %d: %s" error.Line error.Reason
    let signature : MlirFunctionType = { Inputs = [ "i32"; "i64" ]; Results = [] }
    // Several arguments, none, and a call whose result is not bound.
    same
        (Some [ MlirOperation.Call("g", [ "%arg0"; "%v1" ], { Inputs = [ "i32"; "i32" ]; Results = [ "i32" ] })
                MlirOperation.Call("h", [], { Inputs = []; Results = [] }) ])
        (bodyOf [ "    %v2 = func.call @g(%arg0, %v1) : (i32, i32) -> i32"; "    func.call @h() : () -> ()"; "    func.return %v2 : i32" ])
    // The value a constant defines is the name before `=`.
    same
        (Some [ MlirOperation.Constant(Some "%v3", "__clef_callback_7", signature) ])
        (bodyOf [ "    %v3 = func.constant @__clef_callback_7 : (i32, i64) -> ()"; "    func.return %arg0 : i32" ])
    // A constant at the start of a body, with no `%value =` before it, defines no value.
    same
        (Some [ MlirOperation.Constant(None, "__clef_callback_7", signature) ])
        (bodyOf [ "    func.constant @__clef_callback_7 : (i32, i64) -> ()"; "    func.return %arg0 : i32" ])

[<Fact>]
let ``the MLIR reader reads an absent result and an empty result list as no result`` () =
    let resultsOf (header: string) : string list =
        match parse ("module @main {\n  " + header + "\n    func.return\n}\n}") with
        | Ok mlir -> (functionNamed "f" mlir).Results
        | Error error -> failwithf "'%s' is refused at line %d: %s" header error.Line error.Reason
    same ([]: string list) (resultsOf "func.func @f(%arg0: i32, %arg1: i64) {")
    same ([]: string list) (resultsOf "func.func @f(%arg0: i32, %arg1: i64) -> () {")
    same [ "i32" ] (resultsOf "func.func @f(%arg0: i32, %arg1: i64) -> i32 {")
    same [ "i32"; "i64" ] (resultsOf "func.func @f(%arg0: i32, %arg1: i64) -> (i32, i64) {")
    // An attribute dictionary between the signature and the body is no result.
    same ([]: string list) (resultsOf "func.func @f(%arg0: i32, %arg1: i64) attributes {llvm.emit_c_interface} {")

[<Fact>]
let ``the MLIR reader refuses an unterminated literal, an unclosed body and a malformed operation at their lines`` () =
    same (Error { Line = 2; Reason = "a string literal is not terminated" }: Result<MlirModule, MlirSyntaxError>)
        (parse "module @main {\n  memref.global \"private constant @g : memref<1xi8>\n}")
    same (Error { Line = 2; Reason = "the body of @open is not closed" }: Result<MlirModule, MlirSyntaxError>)
        (parse "module @main {\n  func.func @open() -> () {\n    func.return\n")
    same (Error { Line = 3; Reason = "func.constant is not `@symbol : (inputs) -> results`" }: Result<MlirModule, MlirSyntaxError>)
        (parse "module @main {\n  func.func @f() -> () {\n    %v0 = func.constant @g : i32\n    func.return\n}\n}")
    same (Error { Line = 3; Reason = "func.call is not `@callee(arguments) : (inputs) -> results`" }: Result<MlirModule, MlirSyntaxError>)
        (parse "module @main {\n  func.func @f() -> () {\n    func.call @g(%v0) : i32\n    func.return\n}\n}")
    refusedWith
        [ EntryRefusal.Malformed { Line = 2; Reason = "func.func is not followed by `@name(`" } ]
        [ boundaryEntries ]
        "module @main {\n  func.func private (i32) -> ()\n}"

[<Fact>]
let ``single-entry MLIR is accepted with both entries, their addresses and the record extern`` () =
    match inspect singleEntryMlir [ boundaryEntries ] with
    | Ok [ evidence ] ->
        same boundaryModule evidence.EntryModule
        same scalarSymbol evidence.ScalarEntry.Symbol
        same [ "i32"; "i64" ] (typesOf evidence.ScalarEntry)
        same [ "i32" ] evidence.ScalarEntry.Results
        same ({ Inputs = [ "i32"; "i64" ]; Results = [ "i32" ] }: MlirFunctionType) evidence.ScalarEntryAddress
        // The void entry is the function whose address `@landing_invoke_void` receives.
        same voidSymbol evidence.VoidEntry.Symbol
        same [ "i32"; "i64" ] (typesOf evidence.VoidEntry)
        same ([]: string list) evidence.VoidEntry.Results
        same ({ Inputs = [ "i32"; "i64" ]; Results = [] }: MlirFunctionType) evidence.VoidEntryAddress
        same "landing_record" evidence.RecordSymbol
    | other -> failwithf "expected one accepted module, got %A" other
    // The void entry's empty result list without `-> ()` is the same entry.
    let spelled = singleEntryMlir |> mutate voidHeader ("@" + voidSymbol + "(%arg0: i32, %arg1: i64) {")
    match inspect spelled [ boundaryEntries ] with
    | Ok [ evidence ] -> same ([]: string list) evidence.VoidEntry.Results
    | other -> failwithf "expected one accepted module, got %A" other
    // The void entry is identified by the address the extern receives, not by its symbol.
    let named = boundaryModule + ".voidEntry"
    match inspect (singleEntryMlir.Replace("@" + voidSymbol, "@" + named)) [ boundaryEntries ] with
    | Ok [ evidence ] -> same named evidence.VoidEntry.Symbol
    | other -> failwithf "expected one accepted module, got %A" other

[<Fact>]
let ``duplicate-entry MLIR is accepted with the entries and the record extern of each module`` () =
    match inspect duplicateEntryMlir [ boundaryEntries; otherBoundaryEntries ] with
    | Ok [ first; second ] ->
        same (boundaryModule, "landing_record") (first.EntryModule, first.RecordSymbol)
        same (scalarSymbol, voidSymbol) (first.ScalarEntry.Symbol, first.VoidEntry.Symbol)
        same (otherBoundaryModule, "landing_other_record") (second.EntryModule, second.RecordSymbol)
        same (otherScalarSymbol, otherVoidSymbol) (second.ScalarEntry.Symbol, second.VoidEntry.Symbol)
        same (first.ScalarEntryAddress, first.VoidEntryAddress) (second.ScalarEntryAddress, second.VoidEntryAddress)
    | other -> failwithf "expected two accepted modules, got %A" other

[<Fact>]
let ``a module absent from the MLIR is refused for both of its entries`` () =
    singleEntryMlir
    |> refusedWith
        [ EntryRefusal.EntryMissing otherScalarSymbol
          EntryRefusal.InvokeCallMissing "landing_other_invoke"
          EntryRefusal.InvokeCallMissing "landing_other_invoke_void" ]
        [ boundaryEntries; otherBoundaryEntries ]

[<Fact>]
let ``a mutated scalar entry parameter list is refused with the expected and actual ABI`` () =
    let refusedParameters (header: string) (actual: string list) =
        singleEntryMlir
        |> mutate scalarHeader header
        |> refusedWith [ EntryRefusal.EntryParameters(scalarSymbol, [ "i32"; "i64" ], actual) ] [ boundaryEntries ]
    refusedParameters ".scalarEntry(%arg0: i64, %arg1: i64) -> i32 {" [ "i64"; "i64" ]
    refusedParameters ".scalarEntry(%arg0: i32, %arg1: i32) -> i32 {" [ "i32"; "i32" ]
    // The parameters in the other order, one parameter missing and one more are refused.
    refusedParameters ".scalarEntry(%arg0: i64, %arg1: i32) -> i32 {" [ "i64"; "i32" ]
    refusedParameters ".scalarEntry(%arg0: i32) -> i32 {" [ "i32" ]
    refusedParameters ".scalarEntry(%arg0: i32, %arg1: i64, %arg2: index) -> i32 {" [ "i32"; "i64"; "index" ]
    // Pointer-width and buffer-typed parameters are refused.
    refusedParameters ".scalarEntry(%arg0: index, %arg1: i64) -> i32 {" [ "index"; "i64" ]
    refusedParameters ".scalarEntry(%arg0: memref<?xi8>, %arg1: i64) -> i32 {" [ "memref<?xi8>"; "i64" ]

[<Fact>]
let ``a mutated scalar entry result is refused with the expected and actual result`` () =
    let refusedResults (header: string) (actual: string list) =
        singleEntryMlir
        |> mutate scalarHeader header
        |> refusedWith [ EntryRefusal.EntryResults(scalarSymbol, [ "i32" ], actual) ] [ boundaryEntries ]
    refusedResults ".scalarEntry(%arg0: i32, %arg1: i64) -> i64 {" [ "i64" ]
    refusedResults ".scalarEntry(%arg0: i32, %arg1: i64) -> () {" []
    refusedResults ".scalarEntry(%arg0: i32, %arg1: i64) {" []
    refusedResults ".scalarEntry(%arg0: i32, %arg1: i64) -> (i32, i32) {" [ "i32"; "i32" ]
    // A mutated parameter and a mutated result are both reported.
    singleEntryMlir
    |> mutate scalarHeader ".scalarEntry(%arg0: i64, %arg1: i64) -> i64 {"
    |> refusedWith
        [ EntryRefusal.EntryParameters(scalarSymbol, [ "i32"; "i64" ], [ "i64"; "i64" ])
          EntryRefusal.EntryResults(scalarSymbol, [ "i32" ], [ "i64" ]) ]
        [ boundaryEntries ]

[<Fact>]
let ``a void entry with mutated parameters or a result is refused`` () =
    let header (signature: string) = "@" + voidSymbol + signature
    singleEntryMlir
    |> mutate voidHeader (header "(%arg0: i32, %arg1: i32) -> () {")
    |> refusedWith [ EntryRefusal.EntryParameters(voidSymbol, [ "i32"; "i64" ], [ "i32"; "i32" ]) ] [ boundaryEntries ]
    singleEntryMlir
    |> mutate voidHeader (header "(%arg0: index, %arg1: index) -> () {")
    |> refusedWith [ EntryRefusal.EntryParameters(voidSymbol, [ "i32"; "i64" ], [ "index"; "index" ]) ] [ boundaryEntries ]
    singleEntryMlir
    |> mutate voidHeader (header "(%arg0: i32, %arg1: i64) -> i32 {")
    |> refusedWith [ EntryRefusal.EntryResults(voidSymbol, [], [ "i32" ]) ] [ boundaryEntries ]
    singleEntryMlir
    |> mutate voidHeader (header "(%arg0: i32) -> i32 {")
    |> refusedWith
        [ EntryRefusal.EntryParameters(voidSymbol, [ "i32"; "i64" ], [ "i32" ])
          EntryRefusal.EntryResults(voidSymbol, [], [ "i32" ]) ]
        [ boundaryEntries ]

[<Fact>]
let ``an entry that is absent, only declared or defined twice is refused`` () =
    singleEntryMlir
    |> mutate ("func.func @" + scalarSymbol + "(") ("func.func @" + boundaryModule + ".renamedEntry(")
    |> refusedWith [ EntryRefusal.EntryMissing scalarSymbol ] [ boundaryEntries ]
    // The extern receives the address of a void entry that no `func.func` defines.
    singleEntryMlir
    |> mutate ("func.func @" + voidSymbol + "(") "func.func @__clef_callback_9999("
    |> refusedWith [ EntryRefusal.EntryMissing voidSymbol ] [ boundaryEntries ]
    // A declaration has the entry's types and no body. Nothing takes the scalar entry's
    // address or calls the scalar invoke extern here; a wrapper passes the declared void entry's.
    [ "module @main {"
      "  func.func private @M.scalarEntry(i32, i64) -> i32"
      "  func.func private @__clef_callback_7(i32, i64)"
      "  func.func @B.invoke_void(%arg0: memref<0xi8>) -> i32 {"
      "    %v0 = func.constant @__clef_callback_7 : (i32, i64) -> ()"
      "    %v1 = func.call @invoke_void(%v0) : ((i32, i64) -> ()) -> i32"
      "    func.return %v1 : i32"
      "}"
      "}" ]
    |> String.concat "\n"
    |> refusedWith
        [ EntryRefusal.EntryUndefined "M.scalarEntry"
          EntryRefusal.EntryAddressMissing "M.scalarEntry"
          EntryRefusal.InvokeCallMissing "invoke"
          EntryRefusal.EntryUndefined "__clef_callback_7" ]
        [ { Module = "M"; ScalarInvoke = "invoke"; VoidInvoke = "invoke_void"; Record = "record" } ]
    sampleMlir [ boundaryWitness; boundaryWitness ]
    |> refusedWith
        [ EntryRefusal.EntryAmbiguous(scalarSymbol, 2); EntryRefusal.EntryAmbiguous(voidSymbol, 2) ]
        [ boundaryEntries ]

/// `singleEntryMlir` with `functions` inserted before the witness module's `run`.
let private withFunctionsBeforeRun (functions: string list) : string =
    let run = "  func.func @" + boundaryModule + ".run() -> i8 {"
    singleEntryMlir |> mutate run (String.concat "\n" (functions @ [ run ]))

/// The generated wrappers of the sample, whose bodies hold the calls of the invoke externs.
let private scalarWrapper = "Landing.Boundary.landing_invoke"
let private voidWrapper = "Landing.Boundary.landing_invoke_void"

/// The sample's calls of the invoke externs, each in its generated wrapper.
let private passedScalarCall = "    %v40_1 = func.call @landing_invoke(%v40_0) : ((i32, i64) -> i32) -> i32\n"
let private passedVoidCall = "    %v41_1 = func.call @landing_invoke_void(%v41_0) : ((i32, i64) -> ()) -> i32\n"

[<Fact>]
let ``an invoke extern that does not receive the address of its one entry is refused`` () =
    // No `func.constant` of the scalar entry in the module: the call's operand has no definition.
    singleEntryMlir
    |> mutate passedScalarAddress ""
    |> mutate witnessScalarAddress ""
    |> refusedWith
        [ EntryRefusal.EntryAddressMissing scalarSymbol
          EntryRefusal.InvokeOperandUnresolved("landing_invoke", scalarWrapper, [ "%v40_0" ]) ]
        [ boundaryEntries ]
    // The address the witness function takes does not reach the extern: the wrapper's does.
    singleEntryMlir
    |> mutate passedScalarAddress ""
    |> refusedWith [ EntryRefusal.InvokeOperandUnresolved("landing_invoke", scalarWrapper, [ "%v40_0" ]) ] [ boundaryEntries ]
    singleEntryMlir
    |> mutate passedVoidAddress ""
    |> refusedWith [ EntryRefusal.InvokeOperandUnresolved("landing_invoke_void", voidWrapper, [ "%v41_0" ]) ] [ boundaryEntries ]
    // The witness function's constant is not what identifies the entry.
    match inspect (singleEntryMlir |> mutate witnessVoidAddress "") [ boundaryEntries ] with
    | Ok [ evidence ] -> same voidSymbol evidence.VoidEntry.Symbol
    | other -> failwithf "expected one accepted module, got %A" other
    // An argument that no `func.constant` of the calling function defines is no address.
    singleEntryMlir
    |> mutate "func.call @landing_invoke_void(%v41_0)" "func.call @landing_invoke_void(%arg0)"
    |> refusedWith [ EntryRefusal.InvokeOperandUnresolved("landing_invoke_void", voidWrapper, [ "%arg0" ]) ] [ boundaryEntries ]
    // A module that defines both entries and takes their addresses, and calls neither invoke extern.
    singleEntryMlir
    |> mutate passedScalarCall "    %v40_1 = arith.constant 0 : i32\n"
    |> mutate passedVoidCall "    %v41_1 = arith.constant 0 : i32\n"
    |> refusedWith
        [ EntryRefusal.InvokeCallMissing "landing_invoke"; EntryRefusal.InvokeCallMissing "landing_invoke_void" ]
        [ boundaryEntries ]
    // The scalar invoke extern receiving the address of another function than the scalar entry.
    singleEntryMlir
    |> mutate passedScalarAddress ("    %v40_0 = func.constant @" + boundaryModule + ".otherEntry : (i32, i64) -> i32\n")
    |> refusedWith [ EntryRefusal.InvokeAddresses("landing_invoke", [ boundaryModule + ".otherEntry" ]) ] [ boundaryEntries ]
    // The void invoke extern receiving the addresses of two functions has no one void entry.
    withFunctionsBeforeRun
        [ "  func.func @Landing.Boundary.landing_invoke_again(%arg0: memref<0xi8>) -> i32 {"
          "    %v42_0 = func.constant @__clef_callback_41 : (i32, i64) -> ()"
          "    %v42_1 = func.call @landing_invoke_void(%v42_0) : ((i32, i64) -> ()) -> i32"
          "    func.return %v42_1 : i32"
          "}" ]
    |> refusedWith [ EntryRefusal.InvokeAddresses("landing_invoke_void", [ voidSymbol; "__clef_callback_41" ]) ] [ boundaryEntries ]
    // Each module's extern needs the address of its own module's entry.
    duplicateEntryMlir
    |> mutate ("    %v40_0 = func.constant @" + otherScalarSymbol + " : (i32, i64) -> i32\n") passedScalarAddress
    |> refusedWith [ EntryRefusal.InvokeAddresses("landing_other_invoke", [ scalarSymbol ]) ] [ boundaryEntries; otherBoundaryEntries ]

/// `singleEntryMlir` with a second function shaped like a generated wrapper,
/// `@Landing.Boundary.landing_invoke_again`, whose body is `lines` and which returns `%v42_1`.
/// The sample's own calls of both invoke externs stay as they are.
let private secondCaller = "Landing.Boundary.landing_invoke_again"
let private withSecondCaller (lines: string list) : string =
    withFunctionsBeforeRun
        ([ "  func.func @" + secondCaller + "(%arg0: memref<0xi8>) -> i32 {" ] @ lines @ [ "    func.return %v42_1 : i32"; "}" ])

[<Fact>]
let ``a refused call of an invoke extern is not admitted by an accepted call of the same extern`` () =
    let scalarInvoke : MlirFunctionType = { Inputs = [ "(i32, i64) -> i32" ]; Results = [ "i32" ] }
    let voidInvoke : MlirFunctionType = { Inputs = [ "(i32, i64) -> ()" ]; Results = [ "i32" ] }
    same scalarInvoke (invokeSignature scalarEntryAddress)
    same voidInvoke (invokeSignature voidEntryAddress)
    let scalarCall (operands: string) = "    %v42_1 = func.call @landing_invoke(" + operands + ") : ((i32, i64) -> i32) -> i32"
    let voidCall (operands: string) = "    %v42_1 = func.call @landing_invoke_void(" + operands + ") : ((i32, i64) -> ()) -> i32"
    let unresolved (invoke: string) (operands: string list) = EntryRefusal.InvokeOperandUnresolved(invoke, secondCaller, operands)
    // Control: a second call that receives the same entry's address from a constant of its own
    // body is accepted, so each refusal below is of the second call's operand or signature.
    for body in [ [ "    %v42_0 = " + scalarAddress; scalarCall "%v42_0" ]; [ "    %v42_0 = " + voidAddress; voidCall "%v42_0" ] ] do
        match inspect (withSecondCaller body) [ boundaryEntries ] with
        | Ok [ evidence ] -> same (scalarSymbol, voidSymbol) (evidence.ScalarEntry.Symbol, evidence.VoidEntry.Symbol)
        | other -> failwithf "expected one accepted module, got %A" other
    // The second call passes a value that nothing in the module defines.
    withSecondCaller [ scalarCall "%v_unresolved" ]
    |> refusedWith [ unresolved "landing_invoke" [ "%v_unresolved" ] ] [ boundaryEntries ]
    withSecondCaller [ voidCall "%v_unresolved" ]
    |> refusedWith [ unresolved "landing_invoke_void" [ "%v_unresolved" ] ] [ boundaryEntries ]
    // It passes a parameter of the calling function.
    withSecondCaller [ scalarCall "%arg0" ]
    |> refusedWith [ unresolved "landing_invoke" [ "%arg0" ] ] [ boundaryEntries ]
    withSecondCaller [ voidCall "%arg0" ]
    |> refusedWith [ unresolved "landing_invoke_void" [ "%arg0" ] ] [ boundaryEntries ]
    // It passes the value a constant of another function's body defines: `%v40_0` and `%v41_0`
    // are the generated wrappers' values.
    withSecondCaller [ scalarCall "%v40_0" ]
    |> refusedWith [ unresolved "landing_invoke" [ "%v40_0" ] ] [ boundaryEntries ]
    withSecondCaller [ voidCall "%v41_0" ]
    |> refusedWith [ unresolved "landing_invoke_void" [ "%v41_0" ] ] [ boundaryEntries ]
    // It passes the result of an operation that is no `func.constant`.
    withSecondCaller [ "    %v42_0 = func.call @Landing.Boundary.selected() : () -> ((i32, i64) -> i32)"; scalarCall "%v42_0" ]
    |> refusedWith [ unresolved "landing_invoke" [ "%v42_0" ] ] [ boundaryEntries ]
    // It passes a value that two constants of its body define.
    withSecondCaller [ "    %v42_0 = " + scalarAddress; "    %v42_0 = func.constant @" + boundaryModule + ".otherEntry : (i32, i64) -> i32"; scalarCall "%v42_0" ]
    |> refusedWith [ unresolved "landing_invoke" [ "%v42_0" ] ] [ boundaryEntries ]
    // The refused call stands in the body that holds the accepted call.
    singleEntryMlir
    |> mutate passedScalarCall (passedScalarCall + "    %v40_9 = func.call @landing_invoke(%v_unresolved) : ((i32, i64) -> i32) -> i32\n")
    |> refusedWith [ EntryRefusal.InvokeOperandUnresolved("landing_invoke", scalarWrapper, [ "%v_unresolved" ]) ] [ boundaryEntries ]
    singleEntryMlir
    |> mutate passedVoidCall (passedVoidCall + "    %v41_9 = func.call @landing_invoke_void(%arg0) : ((i32, i64) -> ()) -> i32\n")
    |> refusedWith [ EntryRefusal.InvokeOperandUnresolved("landing_invoke_void", voidWrapper, [ "%arg0" ]) ] [ boundaryEntries ]
    // The second call receives the entry's address under another signature than the extern's:
    // an entry parameter, the entry result, the extern's result, and the other extern's signature.
    let mistyped (invoke: string) (address: string) (signature: string) (expected: MlirFunctionType) (actual: MlirFunctionType) =
        withSecondCaller [ "    %v42_0 = " + address; "    %v42_1 = func.call @" + invoke + "(%v42_0) : " + signature ]
        |> refusedWith [ EntryRefusal.InvokeSignature(invoke, secondCaller, expected, actual) ] [ boundaryEntries ]
    mistyped "landing_invoke" scalarAddress "((i32, i32) -> i32) -> i32" scalarInvoke { Inputs = [ "(i32, i32) -> i32" ]; Results = [ "i32" ] }
    mistyped "landing_invoke" scalarAddress "((i32, i64) -> i64) -> i32" scalarInvoke { Inputs = [ "(i32, i64) -> i64" ]; Results = [ "i32" ] }
    mistyped "landing_invoke" scalarAddress "((i32, i64) -> i32) -> i64" scalarInvoke { Inputs = [ "(i32, i64) -> i32" ]; Results = [ "i64" ] }
    mistyped "landing_invoke" scalarAddress "((i32, i64) -> i32) -> ()" scalarInvoke { Inputs = [ "(i32, i64) -> i32" ]; Results = [] }
    mistyped "landing_invoke" scalarAddress "((i32, i64) -> ()) -> i32" scalarInvoke voidInvoke
    mistyped "landing_invoke_void" voidAddress "((i32, i64) -> i32) -> i32" voidInvoke scalarInvoke
    mistyped "landing_invoke_void" voidAddress "((index, index) -> ()) -> i32" voidInvoke { Inputs = [ "(index, index) -> ()" ]; Results = [ "i32" ] }
    mistyped "landing_invoke_void" voidAddress "(i64) -> i32" voidInvoke { Inputs = [ "i64" ]; Results = [ "i32" ] }
    // Another number of arguments than one: the signature and the operands are both refused.
    withSecondCaller [ "    %v42_0 = " + scalarAddress; "    %v42_1 = func.call @landing_invoke(%v42_0, %arg0) : ((i32, i64) -> i32, memref<0xi8>) -> i32" ]
    |> refusedWith
        [ EntryRefusal.InvokeSignature("landing_invoke", secondCaller, scalarInvoke, { Inputs = [ "(i32, i64) -> i32"; "memref<0xi8>" ]; Results = [ "i32" ] })
          unresolved "landing_invoke" [ "%v42_0"; "%arg0" ] ]
        [ boundaryEntries ]
    withSecondCaller [ "    %v42_1 = func.call @landing_invoke_void() : () -> i32" ]
    |> refusedWith
        [ EntryRefusal.InvokeSignature("landing_invoke_void", secondCaller, voidInvoke, { Inputs = []; Results = [ "i32" ] })
          unresolved "landing_invoke_void" [] ]
        [ boundaryEntries ]
    // An unresolved operand under a wrong signature: both are reported for the one call.
    withSecondCaller [ "    %v42_1 = func.call @landing_invoke(%v_unresolved) : (index) -> i32" ]
    |> refusedWith
        [ EntryRefusal.InvokeSignature("landing_invoke", secondCaller, scalarInvoke, { Inputs = [ "index" ]; Results = [ "i32" ] })
          unresolved "landing_invoke" [ "%v_unresolved" ] ]
        [ boundaryEntries ]
    // A refused call and a call that receives another function's address are both reported,
    // the calls' refusals before the addresses.
    withSecondCaller
        [ "    %v42_0 = func.constant @__clef_callback_41 : (i32, i64) -> ()"
          "    %v42_2 = func.call @landing_invoke_void(%v42_0) : ((i32, i64) -> ()) -> i32"
          voidCall "%v_unresolved" ]
    |> refusedWith
        [ unresolved "landing_invoke_void" [ "%v_unresolved" ]
          EntryRefusal.InvokeAddresses("landing_invoke_void", [ voidSymbol; "__clef_callback_41" ]) ]
        [ boundaryEntries ]
    // Refused calls of both externs are reported in entry order, the scalar extern's first.
    withSecondCaller [ "    %v42_2 = func.call @landing_invoke_void(%arg0) : ((i32, i64) -> ()) -> i32"; scalarCall "%v_unresolved" ]
    |> refusedWith
        [ unresolved "landing_invoke" [ "%v_unresolved" ]; unresolved "landing_invoke_void" [ "%arg0" ] ]
        [ boundaryEntries ]
    // The second witness module's externs are inspected the same way: the first module's
    // accepted calls do not admit a refused call of `@landing_other_invoke`.
    duplicateEntryMlir
    |> mutate
        "    %v40_1 = func.call @landing_other_invoke(%v40_0) : ((i32, i64) -> i32) -> i32\n"
        ("    %v40_1 = func.call @landing_other_invoke(%v40_0) : ((i32, i64) -> i32) -> i32\n"
         + "    %v40_9 = func.call @landing_other_invoke(%v_unresolved) : ((i32, i64) -> i32) -> i32\n")
    |> refusedWith
        [ EntryRefusal.InvokeOperandUnresolved("landing_other_invoke", "Landing.Other.Boundary.landing_other_invoke", [ "%v_unresolved" ]) ]
        [ boundaryEntries; otherBoundaryEntries ]

[<Fact>]
let ``an entry address constant with a mutated signature is refused with both signatures`` () =
    let scalarExpected : MlirFunctionType = { Inputs = [ "i32"; "i64" ]; Results = [ "i32" ] }
    let voidExpected : MlirFunctionType = { Inputs = [ "i32"; "i64" ]; Results = [] }
    // The constant whose value the extern receives.
    singleEntryMlir
    |> mutate ("%v40_0 = " + scalarAddress) ("%v40_0 = func.constant @" + scalarSymbol + " : (i32, i32) -> i32")
    |> refusedWith
        [ EntryRefusal.EntryAddressSignature(scalarSymbol, scalarExpected, [ { Inputs = [ "i32"; "i32" ]; Results = [ "i32" ] } ]) ]
        [ boundaryEntries ]
    singleEntryMlir
    |> mutate ("%v40_0 = " + scalarAddress) ("%v40_0 = func.constant @" + scalarSymbol + " : (i32, i64) -> ()")
    |> refusedWith
        [ EntryRefusal.EntryAddressSignature(scalarSymbol, scalarExpected, [ { Inputs = [ "i32"; "i64" ]; Results = [] } ]) ]
        [ boundaryEntries ]
    singleEntryMlir
    |> mutate ("%v41_0 = " + voidAddress) ("%v41_0 = func.constant @" + voidSymbol + " : (i32, i64) -> i32")
    |> refusedWith
        [ EntryRefusal.EntryAddressSignature(voidSymbol, voidExpected, [ { Inputs = [ "i32"; "i64" ]; Results = [ "i32" ] } ]) ]
        [ boundaryEntries ]
    singleEntryMlir
    |> mutate ("%v41_0 = " + voidAddress) ("%v41_0 = func.constant @" + voidSymbol + " : (index, index) -> ()")
    |> refusedWith
        [ EntryRefusal.EntryAddressSignature(voidSymbol, voidExpected, [ { Inputs = [ "index"; "index" ]; Results = [] } ]) ]
        [ boundaryEntries ]
    // The constant of the witness function: one constant of the entry's signature does not
    // admit another of a different one.
    singleEntryMlir
    |> mutate ("%v30_0 = " + scalarAddress) ("%v30_0 = func.constant @" + scalarSymbol + " : (i64, i64) -> i32")
    |> refusedWith
        [ EntryRefusal.EntryAddressSignature(scalarSymbol, scalarExpected, [ { Inputs = [ "i64"; "i64" ]; Results = [ "i32" ] } ]) ]
        [ boundaryEntries ]
    // Each deviating signature is reported once.
    singleEntryMlir
    |> mutate witnessVoidAddress
        (witnessVoidAddress + "    %v31_8 = func.constant @" + voidSymbol + " : (i64, i64) -> ()\n    %v31_9 = func.constant @" + voidSymbol + " : (i64, i64) -> ()\n")
    |> refusedWith
        [ EntryRefusal.EntryAddressSignature(voidSymbol, voidExpected, [ { Inputs = [ "i64"; "i64" ]; Results = [] } ]) ]
        [ boundaryEntries ]

[<Fact>]
let ``an entry passed through a wrapper is refused as wrapped`` () =
    same true (isCallbackThunk "__clef_callback_0")
    same true (isCallbackThunk "__clef_callback_41")
    same false (isCallbackThunk "__clef_callback_")
    same false (isCallbackThunk "__clef_callback_4a")
    same false (isCallbackThunk "x__clef_callback_4")
    // A `__clef_callback_<N>` function that calls an entry.
    withFunctionsBeforeRun
        [ "  func.func @__clef_callback_41(%arg0: i32, %arg1: i64) {"
          "    func.call @" + voidSymbol + "(%arg0, %arg1) : (i32, i64) -> ()"
          "    func.return"
          "}" ]
    |> refusedWith [ EntryRefusal.EntryWrapped(voidSymbol, "__clef_callback_41") ] [ boundaryEntries ]
    withFunctionsBeforeRun
        [ "  func.func @__clef_callback_7(%arg0: i32, %arg1: i64) -> i32 {"
          "    %v0 = func.call @" + scalarSymbol + "(%arg0, %arg1) : (i32, i64) -> i32"
          "    func.return %v0 : i32"
          "}"
          "  func.func @__clef_callback_8(%arg0: index, %arg1: index) {"
          "    func.call @" + voidSymbol + "(%arg0, %arg1) : (i32, i64) -> ()"
          "    func.return"
          "}" ]
    |> refusedWith
        [ EntryRefusal.EntryWrapped(scalarSymbol, "__clef_callback_7"); EntryRefusal.EntryWrapped(voidSymbol, "__clef_callback_8") ]
        [ boundaryEntries ]
    // The function whose address the extern receives calls a function of the witness module:
    // `@<module>.voidEntry` holds the body and the `__clef_callback_<N>` function forwards to it.
    let recording (value: string) = "func.call @landing_record(" + value + ") : (i32) -> ()"
    let forwarding (result: string) = result + " = func.call @" + boundaryModule + ".voidEntry(%arg0, %arg1) : (i32, i64) -> i32"
    withFunctionsBeforeRun
        [ "  func.func @" + boundaryModule + ".voidEntry(%arg0: i32, %arg1: i64) -> i32 {"
          "    func.call @landing_record(%arg0) : (i32) -> ()"
          "    %v50_0 = arith.constant 0 : i32"
          "    func.return %v50_0 : i32"
          "}" ]
    |> mutate (recording "%v20_2") (forwarding "%v20_5")
    |> mutate (recording "%v20_3") (forwarding "%v20_6")
    |> refusedWith
        [ EntryRefusal.EntryWrapped(boundaryModule + ".voidEntry", voidSymbol)
          EntryRefusal.RecordCallMissing(voidSymbol, "landing_record") ]
        [ boundaryEntries ]
    // A `__clef_callback_<N>` function that calls neither entry, and a caller of an entry that
    // is no `__clef_callback_<N>` function, wrap nothing.
    let unrelated =
        withFunctionsBeforeRun
            [ "  func.func @__clef_callback_9(%arg0: i32) {"
              "    func.call @landing_record(%arg0) : (i32) -> ()"
              "    func.return"
              "}"
              "  func.func @" + boundaryModule + ".replay(%arg0: i32, %arg1: i64) {"
              "    func.call @" + voidSymbol + "(%arg0, %arg1) : (i32, i64) -> ()"
              "    func.return"
              "}" ]
    match inspect unrelated [ boundaryEntries ] with
    | Ok [ evidence ] -> same voidSymbol evidence.VoidEntry.Symbol
    | other -> failwithf "expected one accepted module, got %A" other

[<Fact>]
let ``a void entry that does not call its record extern, or calls another module's, is refused`` () =
    let firstCall (record: string) = "func.call @" + record + "(%v20_2) : (i32) -> ()"
    let secondCall (record: string) = "func.call @" + record + "(%v20_3) : (i32) -> ()"
    singleEntryMlir
    |> mutate (firstCall "landing_record") (firstCall "landing_flush")
    |> mutate (secondCall "landing_record") (secondCall "landing_flush")
    |> refusedWith [ EntryRefusal.RecordCallMissing(voidSymbol, "landing_record") ] [ boundaryEntries ]
    // A body without any call.
    singleEntryMlir
    |> mutate ("      " + firstCall "landing_record" + "\n") ""
    |> mutate ("      " + secondCall "landing_record" + "\n") ""
    |> refusedWith [ EntryRefusal.RecordCallMissing(voidSymbol, "landing_record") ] [ boundaryEntries ]
    // The second module's void entry calling the first module's record extern in both regions.
    duplicateEntryMlir
    |> mutate (firstCall "landing_other_record") (firstCall "landing_record")
    |> mutate (secondCall "landing_other_record") (secondCall "landing_record")
    |> refusedWith
        [ EntryRefusal.ForeignRecordCall(otherVoidSymbol, "landing_record"); EntryRefusal.RecordCallMissing(otherVoidSymbol, "landing_other_record") ]
        [ boundaryEntries; otherBoundaryEntries ]
    // In one region only: its own record extern is still called, and the foreign call is refused.
    duplicateEntryMlir
    |> mutate (secondCall "landing_other_record") (secondCall "landing_record")
    |> refusedWith [ EntryRefusal.ForeignRecordCall(otherVoidSymbol, "landing_record") ] [ boundaryEntries; otherBoundaryEntries ]

[<Fact>]
let ``the refusals of both entries and of both modules are reported in module and entry order`` () =
    duplicateEntryMlir
    |> mutate ("@" + scalarSymbol + "(%arg0: i32, %arg1: i64) -> i32 {") ("@" + scalarSymbol + "(%arg0: i64, %arg1: i64) -> i32 {")
    |> mutate voidHeader ("@" + voidSymbol + "(%arg0: i32, %arg1: i64) -> i32 {")
    |> mutate ("    %v41_0 = func.constant @" + otherVoidSymbol + " : (i32, i64) -> ()\n") ""
    |> refusedWith
        [ EntryRefusal.EntryParameters(scalarSymbol, [ "i32"; "i64" ], [ "i64"; "i64" ])
          EntryRefusal.EntryResults(voidSymbol, [], [ "i32" ])
          EntryRefusal.InvokeOperandUnresolved("landing_other_invoke_void", "Landing.Other.Boundary.landing_other_invoke_void", [ "%v41_0" ]) ]
        [ boundaryEntries; otherBoundaryEntries ]

// ═══════════════════════════════════════════════════════════════════
// Composer's retained discharge receipts
// ═══════════════════════════════════════════════════════════════════

/// Structured reading of the discharge receipts Composer retains under `-k`
/// (`06c_psg_discharge.json`, `09_mlir_discharge.json`) and of the obligations they discharge
/// (`06a_obligations.json`).
module DischargeReceipts =
    open Fidelity.Data.JSON

    /// One solver or translator a receipt names, with the SHA-256 of its executable.
    type DischargeTool = { Executable: string; ExecutableSha256: string }

    /// The verdict of one obligation. `Anchor` is the obligation's id.
    type DischargeOutcome = { Anchor: string; Source: string; Verdict: string }

    type DischargeReceipt =
        { Stage: string
          InputSha256: string
          QuerySha256: string
          Tools: DischargeTool list
          Outcomes: DischargeOutcome list }

    /// What a receipt must state: its stage, the SHA-256 of the retained input and query files
    /// and the ids of the retained obligations.
    type ReceiptExpectation =
        { ExpectedStage: string
          InputFileSha256: string
          QueryFileSha256: string
          ObligationIds: string list }

    [<RequireQualifiedAccess>]
    type ReceiptRefusal =
        /// The text is no JSON, or lacks a field the reader requires.
        | Malformed of reason: string
        | Stage of expected: string * actual: string
        | InputDigest of expected: string * actual: string
        | QueryDigest of expected: string * actual: string
        /// The receipt names no tool.
        | NoTool
        /// The digest of a tool's executable is no SHA-256 value.
        | ToolDigest of executable: string * digest: string
        /// The receipt holds no outcome: it discharges nothing.
        | NoOutcome
        /// An outcome's verdict is not `unsat`.
        | Undischarged of anchor: string * verdict: string
        /// A retained obligation has no outcome.
        | ObligationWithoutOutcome of obligation: string
        /// An outcome's anchor is no retained obligation, or repeats one already discharged.
        | OutcomeWithoutObligation of anchor: string

    let private kindName (value: JsonValue) =
        match value with
        | JsonValue.Object _ -> "Object"
        | JsonValue.Array _ -> "Array"
        | JsonValue.String _ -> "String"
        | JsonValue.Number _ | JsonValue.NumberLiteral _ -> "Number"
        | JsonValue.Bool true -> "True"
        | JsonValue.Bool false -> "False"
        | JsonValue.Null -> "Null"

    let private property (name: string) (owner: JsonValue) : Result<JsonValue, string> =
        match owner with
        | JsonValue.Object _ ->
            match Json.prop name owner with
            | Some value -> Ok value
            | None -> Error $"`{name}` is absent"
        | _ ->
            Error $"`{name}` is read from a JSON {kindName owner}, which is no object"

    let private text (name: string) (owner: JsonValue) : Result<string, string> =
        property name owner
        |> Result.bind (fun value ->
            match value with
            | JsonValue.String s -> Ok s
            | _ -> Error $"`{name}` is a JSON {kindName value}, which is no string")

    let private items (name: string) (item: JsonValue -> Result<'T, string>) (owner: JsonValue) : Result<'T list, string> =
        property name owner
        |> Result.bind (fun value ->
            match value with
            | JsonValue.Array elements ->
                (elements, Ok [])
                ||> List.foldBack (fun element collected ->
                    match item element, collected with
                    | Ok read, Ok rest -> Ok(read :: rest)
                    | Error reason, _ -> Error $"{name}: {reason}"
                    | _, Error reason -> Error reason)
            | _ ->
                Error $"`{name}` is a JSON {kindName value}, which is no array")

    let private notJson = ReceiptRefusal.Malformed "the text is no JSON"

    let parse (json: string) : Result<DischargeReceipt, ReceiptRefusal> =
        let tool (element: JsonValue) : Result<DischargeTool, string> =
            match text "Executable" element, text "Sha256" element with
            | Ok executable, Ok digest -> Ok { Executable = executable; ExecutableSha256 = digest }
            | Error reason, _ -> Error reason
            | _, Error reason -> Error reason
        let outcome (element: JsonValue) : Result<DischargeOutcome, string> =
            match text "Anchor" element, text "Source" element, text "Verdict" element with
            | Ok anchor, Ok source, Ok verdict -> Ok { Anchor = anchor; Source = source; Verdict = verdict }
            | Error reason, _, _ -> Error reason
            | _, Error reason, _ -> Error reason
            | _, _, Error reason -> Error reason
        match Json.parse json with
        | Error _ -> Error notJson
        | Ok root ->
            match text "Stage" root, text "InputSha256" root, text "QuerySha256" root, items "Tools" tool root, items "Outcomes" outcome root with
            | Ok stage, Ok input, Ok query, Ok tools, Ok outcomes ->
                Ok { Stage = stage; InputSha256 = input; QuerySha256 = query; Tools = tools; Outcomes = outcomes }
            | Error reason, _, _, _, _ -> Error(ReceiptRefusal.Malformed reason)
            | _, Error reason, _, _, _ -> Error(ReceiptRefusal.Malformed reason)
            | _, _, Error reason, _, _ -> Error(ReceiptRefusal.Malformed reason)
            | _, _, _, Error reason, _ -> Error(ReceiptRefusal.Malformed reason)
            | _, _, _, _, Error reason -> Error(ReceiptRefusal.Malformed reason)

    let obligationIds (json: string) : Result<string list, ReceiptRefusal> =
        match Json.parse json with
        | Error _ -> Error notJson
        | Ok root ->
            items "obligations" (text "id") root |> Result.mapError ReceiptRefusal.Malformed

    let private isSha256 (digest: string) : bool = digest.Length = 64 && digest |> Seq.forall Char.IsAsciiHexDigit

    /// SHA-256 values are compared without regard to the letter case of their hex digits.
    let private sameDigest (a: string) (b: string) : bool = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    /// `items` without one occurrence of each element of `removed` it holds.
    let private without (removed: string list) (items: string list) : string list =
        removed
        |> List.fold (fun (remaining: string list) (item: string) ->
            match List.tryFindIndex (fun candidate -> candidate = item) remaining with
            | Some index -> List.removeAt index remaining
            | None -> remaining) items

    /// Every way `receipt` differs from `expected`, in this order: stage, input digest, query
    /// digest, tools, outcomes that discharge nothing, obligations without an outcome and
    /// outcomes without an obligation. Each obligation id accounts for one outcome of that anchor.
    let refusalsOf (expected: ReceiptExpectation) (receipt: DischargeReceipt) : ReceiptRefusal list =
        let anchors = receipt.Outcomes |> List.map (fun outcome -> outcome.Anchor)
        let malformedTools =
            receipt.Tools
            |> List.filter (fun tool -> not (isSha256 tool.ExecutableSha256))
            |> List.map (fun tool -> ReceiptRefusal.ToolDigest(tool.Executable, tool.ExecutableSha256))
        let undischarged =
            receipt.Outcomes
            |> List.filter (fun outcome -> outcome.Verdict <> "unsat")
            |> List.map (fun outcome -> ReceiptRefusal.Undischarged(outcome.Anchor, outcome.Verdict))
        [ if receipt.Stage <> expected.ExpectedStage then
              ReceiptRefusal.Stage(expected.ExpectedStage, receipt.Stage)
          if not (sameDigest receipt.InputSha256 expected.InputFileSha256) then
              ReceiptRefusal.InputDigest(expected.InputFileSha256, receipt.InputSha256)
          if not (sameDigest receipt.QuerySha256 expected.QueryFileSha256) then
              ReceiptRefusal.QueryDigest(expected.QueryFileSha256, receipt.QuerySha256)
          if receipt.Tools.IsEmpty then
              ReceiptRefusal.NoTool ]
        @ malformedTools
        @ (if receipt.Outcomes.IsEmpty then [ ReceiptRefusal.NoOutcome ] else [])
        @ undischarged
        @ (without anchors expected.ObligationIds |> List.map ReceiptRefusal.ObligationWithoutOutcome)
        @ (without expected.ObligationIds anchors |> List.map ReceiptRefusal.OutcomeWithoutObligation)

open DischargeReceipts

// ─── Discharge receipt Facts ───

/// The SHA-256 of the retained query file as this runner computes it, and as Composer writes it.
let private queryDigest = "f8afe5e3df0bb279dbbfcfbe44ba16efad832c3943e7c4ca79266385a4ea15c2"
let private inputDigest = "1f6fa10efbe112d348d8b0dda1a41e5e9bba8c18dc567a455492190dedbb46ef"
let private toolDigest = "863175B251B36A48C57E728EDBE77CAF296A2CF5CA25E5DBAB3B1D5C1BA0F2DF"

/// A receipt in the shape Composer writes it, with one outcome per `(anchor, verdict)`.
let private receiptJson (stage: string) (input: string) (query: string) (outcomes: (string * string) list) : string =
    let outcome (anchor: string, verdict: string) =
        "    { \"Anchor\": \"" + anchor + "\", \"Source\": \"/work/Boundary.clef:7:8\", \"Verdict\": \"" + verdict + "\" }"
    [ "{"
      "  \"Invocation\": \"2a3fe619-d9f2-436d-8d76-c89d1975a008\","
      "  \"Stage\": \"" + stage + "\","
      "  \"InputSha256\": \"" + input + "\","
      "  \"QuerySha256\": \"" + query + "\","
      "  \"Tools\": ["
      "    { \"Executable\": \"/usr/bin/cvc5\", \"Sha256\": \"" + toolDigest + "\", \"Version\": \"cvc5 1.3.0\", \"Arguments\": [\"--lang=smt2\", \"--tlimit=25000\"] }"
      "  ],"
      "  \"Outcomes\": ["
      outcomes |> List.map outcome |> String.concat ",\n"
      "  ],"
      "  \"StandardOutput\": \"unsat\\nunsat\\n\","
      "  \"StandardError\": \"\""
      "}" ]
    |> String.concat "\n"

let private toolLine = "    { \"Executable\": \"/usr/bin/cvc5\", \"Sha256\": \"" + toolDigest + "\", \"Version\": \"cvc5 1.3.0\", \"Arguments\": [\"--lang=smt2\", \"--tlimit=25000\"] }\n"

let private mlirReceipt = receiptJson "mlir" (inputDigest.ToUpperInvariant()) (queryDigest.ToUpperInvariant()) [ "int_literal", "unsat"; "entry_abi", "unsat" ]

let private mlirExpectation : ReceiptExpectation =
    { ExpectedStage = "mlir"; InputFileSha256 = inputDigest; QueryFileSha256 = queryDigest; ObligationIds = [ "int_literal"; "entry_abi" ] }

let private parsed (json: string) : DischargeReceipt =
    match DischargeReceipts.parse json with
    | Ok receipt -> receipt
    | Error refusal -> failwithf "the receipt is refused: %A" refusal

let private receiptRefusedWith (expected: ReceiptRefusal list) (expectation: ReceiptExpectation) (json: string) =
    same expected (refusalsOf expectation (parsed json))

[<Fact>]
let ``a discharge receipt is read into its stage, digests, tools and outcomes`` () =
    let receipt = parsed mlirReceipt
    same "mlir" receipt.Stage
    same (inputDigest.ToUpperInvariant()) receipt.InputSha256
    same (queryDigest.ToUpperInvariant()) receipt.QuerySha256
    same [ ({ Executable = "/usr/bin/cvc5"; ExecutableSha256 = toolDigest }: DischargeTool) ] receipt.Tools
    same
        [ ({ Anchor = "int_literal"; Source = "/work/Boundary.clef:7:8"; Verdict = "unsat" }: DischargeOutcome)
          { Anchor = "entry_abi"; Source = "/work/Boundary.clef:7:8"; Verdict = "unsat" } ]
        receipt.Outcomes
    // The digests of the retained files are lowercase hex, the receipt's uppercase: the same values.
    mlirReceipt |> receiptRefusedWith [] mlirExpectation
    // The outcomes need not be in the obligations' order.
    mlirReceipt |> receiptRefusedWith [] { mlirExpectation with ObligationIds = [ "entry_abi"; "int_literal" ] }

[<Fact>]
let ``a receipt of another stage or of other input or query bytes is refused`` () =
    mlirReceipt
    |> receiptRefusedWith [ ReceiptRefusal.Stage("psg", "mlir") ] { mlirExpectation with ExpectedStage = "psg" }
    mlirReceipt
    |> receiptRefusedWith [ ReceiptRefusal.InputDigest(queryDigest, inputDigest.ToUpperInvariant()) ] { mlirExpectation with InputFileSha256 = queryDigest }
    mlirReceipt
    |> receiptRefusedWith [ ReceiptRefusal.QueryDigest(inputDigest, queryDigest.ToUpperInvariant()) ] { mlirExpectation with QueryFileSha256 = inputDigest }
    // All three at once, in that order.
    mlirReceipt
    |> receiptRefusedWith
        [ ReceiptRefusal.Stage("psg", "mlir")
          ReceiptRefusal.InputDigest(queryDigest, inputDigest.ToUpperInvariant())
          ReceiptRefusal.QueryDigest(inputDigest, queryDigest.ToUpperInvariant()) ]
        { mlirExpectation with ExpectedStage = "psg"; InputFileSha256 = queryDigest; QueryFileSha256 = inputDigest }

[<Fact>]
let ``a receipt without a tool, or whose tool digest is no SHA-256 value, is refused`` () =
    mlirReceipt |> mutate toolLine "" |> receiptRefusedWith [ ReceiptRefusal.NoTool ] mlirExpectation
    mlirReceipt
    |> mutate toolDigest (toolDigest.Substring(0, 63))
    |> receiptRefusedWith [ ReceiptRefusal.ToolDigest("/usr/bin/cvc5", toolDigest.Substring(0, 63)) ] mlirExpectation
    mlirReceipt
    |> mutate toolDigest ("G" + toolDigest.Substring(1))
    |> receiptRefusedWith [ ReceiptRefusal.ToolDigest("/usr/bin/cvc5", "G" + toolDigest.Substring(1)) ] mlirExpectation

[<Fact>]
let ``an outcome whose verdict is not unsat is refused as undischarged`` () =
    for verdict in [ "sat"; "unknown"; "timeout"; "" ] do
        receiptJson "mlir" inputDigest queryDigest [ "int_literal", "unsat"; "entry_abi", verdict ]
        |> receiptRefusedWith [ ReceiptRefusal.Undischarged("entry_abi", verdict) ] mlirExpectation
    receiptJson "mlir" inputDigest queryDigest [ "int_literal", "sat"; "entry_abi", "unknown" ]
    |> receiptRefusedWith
        [ ReceiptRefusal.Undischarged("int_literal", "sat"); ReceiptRefusal.Undischarged("entry_abi", "unknown") ]
        mlirExpectation

[<Fact>]
let ``a receipt whose outcomes are not exactly the retained obligations is refused`` () =
    // An obligation without an outcome.
    receiptJson "mlir" inputDigest queryDigest [ "int_literal", "unsat" ]
    |> receiptRefusedWith [ ReceiptRefusal.ObligationWithoutOutcome "entry_abi" ] mlirExpectation
    // An outcome that is no retained obligation.
    receiptJson "mlir" inputDigest queryDigest [ "int_literal", "unsat"; "entry_abi", "unsat"; "storage_formed", "unsat" ]
    |> receiptRefusedWith [ ReceiptRefusal.OutcomeWithoutObligation "storage_formed" ] mlirExpectation
    // One obligation discharged twice in place of another.
    receiptJson "mlir" inputDigest queryDigest [ "int_literal", "unsat"; "int_literal", "unsat" ]
    |> receiptRefusedWith
        [ ReceiptRefusal.ObligationWithoutOutcome "entry_abi"; ReceiptRefusal.OutcomeWithoutObligation "int_literal" ]
        mlirExpectation
    // A receipt without an outcome discharges nothing, whatever the obligations are.
    receiptJson "mlir" inputDigest queryDigest []
    |> receiptRefusedWith
        [ ReceiptRefusal.NoOutcome; ReceiptRefusal.ObligationWithoutOutcome "int_literal"; ReceiptRefusal.ObligationWithoutOutcome "entry_abi" ]
        mlirExpectation
    receiptJson "mlir" inputDigest queryDigest []
    |> receiptRefusedWith [ ReceiptRefusal.NoOutcome ] { mlirExpectation with ObligationIds = [] }

[<Fact>]
let ``text that is no receipt is refused as malformed with the field that is wrong`` () =
    let malformed (reason: string) (json: string) =
        same (Error(ReceiptRefusal.Malformed reason): Result<DischargeReceipt, ReceiptRefusal>) (DischargeReceipts.parse json)
    malformed "the text is no JSON" "{ \"Stage\": "
    malformed "the text is no JSON" ""
    malformed "`Stage` is read from a JSON Array, which is no object" "[]"
    malformed "`Stage` is absent" "{}"
    mlirReceipt |> mutate "\"Stage\": \"mlir\"" "\"Stage\": 9" |> malformed "`Stage` is a JSON Number, which is no string"
    mlirReceipt |> mutate "\"QuerySha256\"" "\"QueryDigest\"" |> malformed "`QuerySha256` is absent"
    malformed "`Tools` is a JSON Object, which is no array"
        "{ \"Stage\": \"mlir\", \"InputSha256\": \"a\", \"QuerySha256\": \"b\", \"Tools\": {}, \"Outcomes\": [] }"
    malformed "`Outcomes` is a JSON String, which is no array"
        "{ \"Stage\": \"mlir\", \"InputSha256\": \"a\", \"QuerySha256\": \"b\", \"Tools\": [], \"Outcomes\": \"unsat\" }"
    mlirReceipt |> mutate "\"Outcomes\": [" "\"Results\": [" |> malformed "`Outcomes` is absent"
    mlirReceipt |> mutate ("\"Sha256\": \"" + toolDigest + "\", ") "" |> malformed "Tools: `Sha256` is absent"
    mlirReceipt
    |> mutate "{ \"Anchor\": \"entry_abi\", \"Source\": \"/work/Boundary.clef:7:8\", \"Verdict\": \"unsat\" }" "{ \"Anchor\": \"entry_abi\", \"Source\": \"/work/Boundary.clef:7:8\" }"
    |> malformed "Outcomes: `Verdict` is absent"
    mlirReceipt
    |> mutate "{ \"Anchor\": \"entry_abi\", \"Source\": \"/work/Boundary.clef:7:8\", \"Verdict\": \"unsat\" }" "\"entry_abi\""
    |> malformed "Outcomes: `Anchor` is read from a JSON String, which is no object"

[<Fact>]
let ``the obligation ids are read from the obligations document in order`` () =
    let document =
        "{ \"version\": \"3\", \"description\": \"PSG obligations\", \"obligations\": [ "
        + "{ \"id\": \"int_literal\", \"kind\": \"int-literal-range\", \"refs\": [] }, "
        + "{ \"id\": \"entry_abi\", \"kind\": \"callback-entry\", \"refs\": [] } ] }"
    same (Ok [ "int_literal"; "entry_abi" ]: Result<string list, ReceiptRefusal>) (obligationIds document)
    same (Ok []: Result<string list, ReceiptRefusal>) (obligationIds "{ \"obligations\": [] }")
    same (Error(ReceiptRefusal.Malformed "`obligations` is absent"): Result<string list, ReceiptRefusal>) (obligationIds "{ \"version\": \"3\" }")
    same (Error(ReceiptRefusal.Malformed "obligations: `id` is absent"): Result<string list, ReceiptRefusal>)
        (obligationIds "{ \"obligations\": [ { \"id\": \"a\" }, { \"kind\": \"b\" } ] }")
    same (Error(ReceiptRefusal.Malformed "the text is no JSON"): Result<string list, ReceiptRefusal>) (obligationIds "obligations")

// ─── Inputs of the native journey ───

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let private fixtureDirectory = Path.Combine(repositoryRoot, "tests", "native-callbacks")

/// Run `body` over a fresh scratch directory in OS temporary storage, deleted afterwards
/// whether `body` returns or fails. Every directory and file this module writes is beneath the
/// scratch root of the test process; nothing is written to the checkout (#215).
let private withScratch (name: string) (body: string -> unit) : unit =
    let directory = TestHelpers.TestScratch.create name
    try body directory
    finally Directory.Delete(directory, true)

/// An input file that must exist; its absence fails the Fact.
let private required (role: string) (path: string) : string =
    if File.Exists path then Path.GetFullPath path
    else failwithf "%s is required and does not exist: %s" role path

/// A sibling checkout's file: the path `variable` selects, otherwise `relative` beside the
/// Farscape checkout. A configured path that does not exist is not replaced by the default.
let private sibling (role: string) (variable: string) (relative: string list) : string =
    match Environment.GetEnvironmentVariable variable |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not) with
    | Some configured ->
        if File.Exists configured then Path.GetFullPath configured
        else failwithf "%s selects '%s' as %s, which does not exist; no other location is searched." variable configured role
    | None ->
        let path = Path.Combine(Array.ofList (Path.GetDirectoryName repositoryRoot :: relative))
        if File.Exists path then path
        else failwithf "%s is required at '%s' (or select it with %s); it does not exist." role path variable

let private onPath (tool: string) : string option =
    (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split(Path.PathSeparator)
    |> Array.filter (fun directory -> directory <> "")
    |> Array.map (fun directory -> Path.Combine(directory, tool))
    |> Array.tryFind File.Exists

/// The `dotnet` host that runs Composer.dll: the host of this test process when that is
/// `dotnet`, otherwise the first `dotnet` on PATH.
let private dotnetHost () : string =
    match Environment.ProcessPath |> Option.ofObj |> Option.filter (fun path -> Path.GetFileNameWithoutExtension path = "dotnet") with
    | Some host -> host
    | None ->
        match onPath "dotnet" with
        | Some host -> host
        | None -> failwith "dotnet is required to run Composer.dll: this process is not hosted by dotnet and PATH holds none."

type ProcessOutcome = { Command: string; ExitCode: int; Stdout: string; Stderr: string }

/// Run `info` in `directory` to completion. A process that outlives `timeout` is killed with
/// its process tree and fails the Fact.
let private run (directory: string) (timeout: TimeSpan) (info: ProcessStartInfo) : ProcessOutcome =
    info.WorkingDirectory <- directory
    let command = info.FileName + " " + ProcessLaunch.commandLine info.ArgumentList
    use child = new Process(StartInfo = info)
    if not (child.Start()) then failwithf "%s did not start" command
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit(int timeout.TotalMilliseconds)) then
        child.Kill(true)
        failwithf "%s did not exit within %.0f seconds and was killed" command timeout.TotalSeconds
    child.WaitForExit()
    { Command = command; ExitCode = child.ExitCode; Stdout = stdout.Result; Stderr = stderr.Result }

/// `run`, with both streams written to `<work>/<label>.log`; the outcome and the log's path.
let private logged (work: string) (label: string) (timeout: TimeSpan) (info: ProcessStartInfo) : ProcessOutcome * string =
    let outcome = run work timeout info
    let log = Path.Combine(work, label + ".log")
    File.WriteAllText(log, outcome.Stdout + outcome.Stderr)
    outcome, log

/// `logged`, failing the Fact with the tool's output on a nonzero exit code.
let private succeeded (work: string) (label: string) (timeout: TimeSpan) (info: ProcessStartInfo) : ProcessOutcome =
    let outcome, log = logged work label timeout info
    if outcome.ExitCode <> 0 then
        failwithf "%s: %s exited with code %d (log %s):\n%s%s" label outcome.Command outcome.ExitCode log outcome.Stdout outcome.Stderr
    outcome

// ─── Composer output ───

/// One `<file>:<line>: <severity> <code>: <message>` line of Composer's output.
type CompilerDiagnostic = { File: string; Line: int; Severity: string; Code: string; Message: string }

/// `text` without ANSI SGR sequences (`ESC [ parameters m`), which colour Composer's output.
let private withoutSgr (text: string) : string =
    let plain = Text.StringBuilder()
    let mutable i = 0
    while i < text.Length do
        let mutable sequenceEnd = -1
        if text[i] = '\u001b' && i + 1 < text.Length && text[i + 1] = '[' then
            let mutable j = i + 2
            while j < text.Length && (Char.IsAsciiDigit(text[j]) || text[j] = ';') do
                j <- j + 1
            if j < text.Length && text[j] = 'm' then sequenceEnd <- j
        if sequenceEnd >= 0 then
            i <- sequenceEnd + 1
        else
            plain.Append(text[i]) |> ignore
            i <- i + 1
    plain.ToString()

let private tryDiagnostic (line: string) : CompilerDiagnostic option =
    let locationEnd = line.IndexOf(": ", StringComparison.Ordinal)
    if locationEnd < 0 then None
    else
        let location = line.Substring(0, locationEnd)
        let rest = line.Substring(locationEnd + 2)
        let colon = location.LastIndexOf(':')
        let headEnd = rest.IndexOf(": ", StringComparison.Ordinal)
        if colon < 0 || headEnd < 0 then None
        else
            match Int32.TryParse(location.Substring(colon + 1)), rest.Substring(0, headEnd).Split(' ') with
            | (true, number), [| severity; code |] when List.contains severity [ "error"; "warning"; "info" ] ->
                Some { File = location.Substring(0, colon); Line = number; Severity = severity; Code = code; Message = rest.Substring(headEnd + 2) }
            | _ -> None

/// The diagnostics of Composer's output, colour sequences removed.
let private diagnosticsOf (output: string) : CompilerDiagnostic list =
    (withoutSgr output).Split('\n') |> Array.choose (fun line -> tryDiagnostic (line.TrimEnd('\r'))) |> List.ofArray

/// The words of `text` in order, a word being a maximal run of letters, digits and underscores.
let private wordsOf (text: string) : string list =
    let words = ResizeArray<string>()
    let word = Text.StringBuilder()
    for c in text + " " do
        if Char.IsLetterOrDigit c || c = '_' then word.Append(c) |> ignore
        elif word.Length > 0 then
            words.Add(word.ToString())
            word.Clear() |> ignore
    List.ofSeq words

/// The lines of `output` holding the word `error` or `warning` in any letter case.
let private errorOrWarningLines (output: string) : string list =
    (withoutSgr output).Split('\n')
    |> Array.filter (fun line ->
        wordsOf line
        |> List.exists (fun candidate ->
            String.Equals(candidate, "error", StringComparison.OrdinalIgnoreCase)
            || String.Equals(candidate, "warning", StringComparison.OrdinalIgnoreCase)))
    |> List.ofArray

/// The CCS diagnostic codes `output` names, colour sequences removed: each word that is `CCS`
/// and four decimal digits, once, in order of first occurrence.
let private compilerCodes (output: string) : string list =
    wordsOf (withoutSgr output)
    |> List.filter (fun word ->
        word.Length = 7
        && word.StartsWith("CCS", StringComparison.Ordinal)
        && (word.Substring(3) |> Seq.forall Char.IsAsciiDigit))
    |> List.distinct

/// The distinct `severity code: message` texts of `diagnostics` with their counts.
let private summarized (diagnostics: CompilerDiagnostic list) : string =
    diagnostics
    |> List.countBy (fun d -> $"{d.Severity} {d.Code}: {d.Message}")
    |> List.map (fun (text, count) -> $"  {count} x {text}")
    |> String.concat "\n"

// ─── Platform selection ───

/// The platform a platform package selects: the projects it depends on and the string keys
/// of its `[platform]` table.
type PlatformSelection = { Dependencies: (string * string) list; Keys: (string * string) list }

/// The loading contract the witness project selects (tests/native-callbacks/Loading.clef).
let private loadingContract = "Landing.Acceptance.Loading.contract"

/// Read the selection of the platform package `project`. CCS admits a FidelityExtern library
/// other than libc only under a process-link loading contract whose library is that library,
/// selected by `[platform] loading`. With the platform package as a dependency, Composer
/// (observed at v0.0.2+d17eb195) does not apply a `loading` key of the dependent project. The
/// witness project therefore carries the package's `[platform]` keys and dependencies itself
/// and adds `loading`. A package that has sources of its own, or already selects a loading
/// contract, cannot be carried that way and fails the Fact.
let private platformSelection (project: string) : PlatformSelection =
    let document =
        match Toml.parse (File.ReadAllText project) with
        | Ok document -> document
        | Error reason -> failwithf "%s is not valid TOML: %A" project reason
    let tableAt (key: string) : TomlTable =
        match Toml.getValue key document with
        | Some(TomlValue.Table table)
        | Some(TomlValue.InlineTable table) -> table
        | other -> failwithf "%s: `%s` is %A, which is no table" project key other
    match Toml.getValue "build.sources" document with
    | Some(TomlValue.Array []) -> ()
    | other -> failwithf "%s: build.sources is %A; only a platform package without sources of its own can be carried by the witness project" project other
    let directory = Path.GetDirectoryName project
    let dependencies =
        tableAt "dependencies"
        |> Map.toList
        |> List.map (fun (name, value) ->
            match value with
            | TomlValue.Table entry
            | TomlValue.InlineTable entry ->
                match Map.tryFind "path" entry with
                | Some(TomlValue.String relative) ->
                    name, required $"the '{name}' dependency of {project}" (Path.GetFullPath(Path.Combine(directory, relative)))
                | other -> failwithf "%s: dependencies.%s.path is %A, which is no string" project name other
            | other -> failwithf "%s: dependencies.%s is %A, which is no table" project name other)
    let keys =
        tableAt "platform"
        |> Map.toList
        |> List.map (fun (name, value) ->
            match value with
            | TomlValue.String text -> name, text
            | other -> failwithf "%s: platform.%s is %A, which is no string" project name other)
    if keys |> List.exists (fun (name, _) -> name = "loading") then
        failwithf "%s already selects a loading contract; the witness project cannot select %s" project loadingContract
    if dependencies |> List.exists (fun (name, _) -> name = "metadata") then
        failwithf "%s has a dependency named 'metadata', the name the witness project gives the binding metadata project" project
    { Dependencies = dependencies; Keys = keys }

// ─── TOML text and artifact digests ───

/// A TOML basic string.
let private tomlString (value: string) : string =
    "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// A TOML key: bare when every character is a letter, digit, `_` or `-`, quoted otherwise.
let private tomlKey (name: string) : string =
    if name <> "" && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-') then name
    else tomlString name

type ArtifactDigest = { Path: string; Sha256: string }

let private digestOf (path: string) : ArtifactDigest =
    let path = required "a digested artifact" path
    { Path = path; Sha256 = ClangToolchain.sha256 path }

// ─── Retained proof artifacts ───

/// One discharge stage of Composer's retained intermediates: its receipt, and the retained
/// files whose SHA-256 the receipt states as its input and as its query.
type DischargeStage = { StageName: string; ReceiptFile: string; InputFile: string; QueryFile: string }

let private dischargeStages : DischargeStage list =
    [ { StageName = "psg"; ReceiptFile = "06c_psg_discharge.json"; InputFile = "06b_obligations.smt2"; QueryFile = "06b_obligations.smt2" }
      { StageName = "mlir"; ReceiptFile = "09_mlir_discharge.json"; InputFile = "09_obligations.mlir"; QueryFile = "09_obligations.smt2" } ]

/// The obligations document every discharge receipt is checked against.
let private obligationsFile = "06a_obligations.json"

/// The stage whose receipt (`06c_psg_discharge.json`) every accepted journey must retain.
let private requiredDischargeStage = "psg"

/// One inspected discharge receipt and the number of obligations it discharges.
type DischargeEvidence = { DischargedStage: string; Receipt: ArtifactDigest; Discharged: int }

/// Whether `discharges` holds a receipt of the required stage that discharges at least one
/// obligation. An absent receipt and a receipt without an outcome are both no discharge.
let private dischargesRequiredStage (discharges: DischargeEvidence list) : bool =
    discharges |> List.exists (fun evidence -> evidence.DischargedStage = requiredDischargeStage && evidence.Discharged > 0)

/// Inspect the discharge receipts Composer retained in `intermediates`.
/// - A retained receipt must state its stage, the SHA-256 of its retained input and query
///   files, a tool with a SHA-256 digest, and exactly one `unsat` outcome for each obligation
///   of `06a_obligations.json`; any other content fails the Fact.
/// - A stage whose input or query file is retained without its receipt fails the Fact.
/// - The required stage with none of its files retained fails the Fact.
/// - Another stage with none of its files retained has no receipt to inspect and yields no evidence.
let private inspectDischarges (label: string) (intermediates: string) : DischargeEvidence list =
    let at (name: string) = Path.Combine(intermediates, name)
    dischargeStages
    |> List.choose (fun stage ->
        if File.Exists(at stage.ReceiptFile) then
            let obligations =
                match obligationIds (File.ReadAllText(required "Composer's retained obligations" (at obligationsFile))) with
                | Ok ids -> ids
                | Error refusal -> failwithf "%s: %s is not read: %A" label (at obligationsFile) refusal
            let receipt =
                match DischargeReceipts.parse (File.ReadAllText(at stage.ReceiptFile)) with
                | Ok receipt -> receipt
                | Error refusal -> failwithf "%s: %s is not read: %A" label (at stage.ReceiptFile) refusal
            let expected : ReceiptExpectation =
                { ExpectedStage = stage.StageName
                  InputFileSha256 = ClangToolchain.sha256 (required $"the input of the {stage.StageName} discharge" (at stage.InputFile))
                  QueryFileSha256 = ClangToolchain.sha256 (required $"the query of the {stage.StageName} discharge" (at stage.QueryFile))
                  ObligationIds = obligations }
            match refusalsOf expected receipt with
            | [] -> Some { DischargedStage = stage.StageName; Receipt = digestOf (at stage.ReceiptFile); Discharged = receipt.Outcomes.Length }
            | refusals -> failwithf "%s: %s is refused: %A" label (at stage.ReceiptFile) refusals
        else
            match [ stage.InputFile; stage.QueryFile ] |> List.distinct |> List.filter (fun name -> File.Exists(at name)) with
            | [] when stage.StageName = requiredDischargeStage ->
                failwithf "%s: Composer retained no %s in %s: the journey has no %s discharge receipt"
                    label stage.ReceiptFile intermediates stage.StageName
            | [] -> None
            | undischarged ->
                failwithf "%s: Composer retained %s in %s and no %s: the %s obligations have no discharge receipt"
                    label (String.concat ", " undischarged) intermediates stage.ReceiptFile stage.StageName)

[<Fact>]
let ``a journey without a PSG discharge receipt that discharges an obligation is not accepted`` () =
    let evidence (stage: string) (discharged: int) : DischargeEvidence =
        { DischargedStage = stage; Receipt = { Path = "/work/targets/intermediates/" + stage + ".json"; Sha256 = inputDigest }; Discharged = discharged }
    // The required stage is the PSG stage, and its receipt is 06c_psg_discharge.json.
    same "psg" requiredDischargeStage
    same
        [ "06c_psg_discharge.json" ]
        (dischargeStages |> List.filter (fun stage -> stage.StageName = requiredDischargeStage) |> List.map (fun stage -> stage.ReceiptFile))
    // No receipt, a receipt of another stage only and a PSG receipt without an outcome are no discharge.
    same false (dischargesRequiredStage [])
    same false (dischargesRequiredStage [ evidence "mlir" 2 ])
    same false (dischargesRequiredStage [ evidence "psg" 0 ])
    same false (dischargesRequiredStage [ evidence "psg" 0; evidence "mlir" 2 ])
    same true (dischargesRequiredStage [ evidence "psg" 1 ])
    same true (dischargesRequiredStage [ evidence "mlir" 37; evidence "psg" 37 ])
    // Intermediates without any file of the PSG stage are refused, not read as nothing to inspect.
    withScratch "discharge-receipts" (fun intermediates ->
        let absent = Assert.ThrowsAny<exn>(fun () -> inspectDischarges "absent-receipt" intermediates |> ignore)
        Assert.Contains($"absent-receipt: Composer retained no 06c_psg_discharge.json in {intermediates}", absent.Message)
        // The obligations and the query retained without the receipt are refused as undischarged.
        File.WriteAllText(Path.Combine(intermediates, "06a_obligations.json"), "{ \"obligations\": [ { \"id\": \"int_literal\" } ] }")
        File.WriteAllText(Path.Combine(intermediates, "06b_obligations.smt2"), "(check-sat)\n")
        let undischarged = Assert.ThrowsAny<exn>(fun () -> inspectDischarges "undischarged" intermediates |> ignore)
        Assert.Contains("undischarged: Composer retained 06b_obligations.smt2", undischarged.Message)
        Assert.Contains("and no 06c_psg_discharge.json", undischarged.Message)
        // A PSG receipt without an outcome is refused: it discharges nothing.
        let query = ClangToolchain.sha256 (Path.Combine(intermediates, "06b_obligations.smt2"))
        File.WriteAllText(Path.Combine(intermediates, "06c_psg_discharge.json"), receiptJson "psg" query query [])
        let vacuous = Assert.ThrowsAny<exn>(fun () -> inspectDischarges "vacuous" intermediates |> ignore)
        Assert.Contains("NoOutcome", vacuous.Message)
        // The receipt with one `unsat` outcome for the one obligation is the journey's PSG discharge.
        File.WriteAllText(Path.Combine(intermediates, "06c_psg_discharge.json"), receiptJson "psg" query query [ "int_literal", "unsat" ])
        match inspectDischarges "discharged" intermediates with
        | [ discharged ] ->
            same ("psg", 1) (discharged.DischargedStage, discharged.Discharged)
            same true (dischargesRequiredStage [ discharged ])
        | other -> failwithf "expected the PSG discharge alone, got %A" other)

// ─── Journey stages ───

/// The inputs every journey resolves before it compiles anything.
type JourneyStage =
    { Work: string
      Dotnet: string
      Composer: string
      Platform: PlatformSelection
      Metadata: string
      Archive: string }

/// The C sources of the probe archive: one object each, archived together.
let private probeSources = [ "probe.c"; "other_probe.c" ]

let private pilotTemplates = [ "probe.pilot.toml"; "other_probe.pilot.toml" ]

let private stagedFixtures =
    [ "probe.h"; "other_probe.h" ] @ probeSources @ [ "Loading.clef"; "Boundary.clef"; "Main.clef"; "DuplicateMain.clef" ]

/// Resolve the sibling inputs and the tools, copy the fixtures into `work`, compile each probe
/// source with the resolved clang and archive the objects as liblandingprobe.a with llvm-ar.
/// Clang compiles the foreign probes only; Composer compiles and links everything generated.
let private stage (work: string) : JourneyStage =
    let composer = sibling "Composer.dll" "COMPOSER_DLL" [ "Composer"; "src"; "bin"; "Debug"; "net10.0"; "Composer.dll" ]
    let platform =
        sibling "the platform package" "FIDELITY_PLATFORM_PROJECT"
            [ "Fidelity.Platform"; "Environments"; "Linux"; "x86_64"; "Fidelity.Platform.CompilerSurface.fidproj" ]
    let metadata = sibling "the binding metadata project" "BAREWIRE_METADATA_PROJECT" [ "BAREWire"; "src"; "BAREWire.BindingMetadata.fidproj" ]
    let dotnet = dotnetHost ()
    for name in pilotTemplates do
        required "a pilot template" (Path.Combine(fixtureDirectory, name)) |> ignore
    for name in stagedFixtures do
        File.Copy(required "a native callback fixture" (Path.Combine(fixtureDirectory, name)), Path.Combine(work, name))
    let objects =
        probeSources
        |> List.map (fun source ->
            let name = Path.GetFileNameWithoutExtension source
            let objectFile = name + ".o"
            succeeded work ("build-" + name + "-object") (TimeSpan.FromSeconds 60.0)
                (ClangToolchain.startInfo [ "-c"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; source; "-o"; objectFile ])
            |> ignore
            objectFile)
    succeeded work "build-probe-archive" (TimeSpan.FromSeconds 60.0) (ClangToolchain.arStartInfo ([ "rcs"; "liblandingprobe.a" ] @ objects))
    |> ignore
    // The link can resolve `landingprobe` to the archive alone.
    Assert.False(File.Exists(Path.Combine(work, "liblandingprobe.so")), "the work directory holds a shared object of the probe")
    { Work = work; Dotnet = dotnet; Composer = composer; Platform = platformSelection platform; Metadata = metadata
      Archive = required "the probe archive" (Path.Combine(work, "liblandingprobe.a")) }

/// Write `pilotText` as `<work>/<output>.pilot.toml` and generate it with the Farscape source
/// under test. The pilot's output directory is `generated/<output>`, relative to the pilot;
/// the generated sources relative to `work`.
let private generateFrom (stage: JourneyStage) (output: string) (pilotText: string) : string list =
    let pilot = Path.Combine(stage.Work, output + ".pilot.toml")
    File.WriteAllText(pilot, pilotText)
    match BindingGenerator.generateFromProjectDiagnosed pilot false false Farscape.Core.Types.LP64 with
    | Error refusal -> failwithf "generating %s from %s is refused: %s" output pilot refusal.Text
    | Ok _ ->
        let sources = [ $"generated/{output}/Types.clef"; $"generated/{output}/Boundary/Boundary.clef" ]
        for source in sources do
            required $"the generated source of {output}" (Path.Combine(stage.Work, source)) |> ignore
        sources

/// Generate the bindings of the staged `header` under `namespaceName` into
/// `<work>/generated/<output>` from the pilot `template` of the fixture directory.
let private generate (stage: JourneyStage) (template: string) (header: string) (output: string) (namespaceName: string) : string list =
    let substitutions =
        [ "@HEADER@", Path.Combine(stage.Work, header)
          "@INCLUDE@", stage.Work
          "@OUTPUT@", "generated/" + output
          "@NAMESPACE@", namespaceName ]
    let templateText = File.ReadAllText(Path.Combine(fixtureDirectory, template))
    substitutions
    |> List.fold (fun (text: string) (placeholder, value) -> mutate ("\"" + placeholder + "\"") (tomlString value) text) templateText
    |> generateFrom stage output

/// Write `<work>/<label>.fidproj` over `sources` and compile it with
/// `dotnet Composer.dll compile <label>.fidproj --link-library-path <work> -k -o <work>/<label>`.
/// The project's `[link] archives` table selects the probe archive by path for `landingprobe`:
/// Composer (observed at v0.0.2+d17eb195) refuses a callback entry of a library that has no
/// selected archive ("Boundary entry loading: the custom library has no exact explicitly
/// selected static archive input") and links the selected file in place of `-llandingprobe`.
/// The outcome, the log's path and the binary's path.
let private compile (stage: JourneyStage) (label: string) (sources: string list) : ProcessOutcome * string * string =
    let dependencies =
        (stage.Platform.Dependencies @ [ ("metadata", stage.Metadata) ])
        |> List.map (fun (name, path) -> tomlKey name + " = { path = " + tomlString path + " }")
    let platform =
        (stage.Platform.Keys @ [ ("loading", loadingContract) ])
        |> List.map (fun (name, value) -> tomlKey name + " = " + tomlString value)
    let project = Path.Combine(stage.Work, label + ".fidproj")
    [ [ "[package]"; "name = " + tomlString label; "version = \"0.1.0\""
        "[compilation]"; "target = \"cpu\""
        "[dependencies]" ]
      dependencies
      [ "[build]"
        "sources = [" + (sources |> List.map tomlString |> String.concat ", ") + "]"
        "output_kind = \"console\""
        "[platform]" ]
      platform
      [ "[link]"
        "libraries = [\"landingprobe\"]"
        "archives = { landingprobe = " + tomlString stage.Archive + " }"
        "" ] ]
    |> List.concat
    |> String.concat "\n"
    |> fun text -> File.WriteAllText(project, text)
    let binary = Path.Combine(stage.Work, label)
    let outcome, log =
        logged stage.Work ("build-" + label) (TimeSpan.FromSeconds 300.0)
            (ProcessLaunch.startInfo stage.Dotnet [ stage.Composer; "compile"; project; "--link-library-path"; stage.Work; "-k"; "-o"; binary ])
    outcome, log, binary

/// The digests and the inspected evidence one accepted journey records.
type JourneyReceipt =
    { Label: string
      Clang: ArtifactDigest
      Archiver: ArtifactDigest
      Composer: ArtifactDigest
      ProbeSources: ArtifactDigest list
      ProbeArchive: ArtifactDigest
      GeneratedSources: ArtifactDigest list
      /// Every file Composer retained in `targets/intermediates`, in name order.
      Intermediates: ArtifactDigest list
      Mlir: ArtifactDigest
      Binary: ArtifactDigest
      /// Every `*.log` of the work directory, in name order: the probe object and archive
      /// builds, Composer's compile and the run of the executable.
      Logs: ArtifactDigest list
      Entries: CallbackEntryEvidence list
      Discharges: DischargeEvidence list
      Stdout: string }

/// The tools of a journey. They are the only files of a receipt outside its work directory.
let private receiptTools (receipt: JourneyReceipt) : ArtifactDigest list =
    [ receipt.Clang; receipt.Archiver; receipt.Composer ]

let private receiptDigests (receipt: JourneyReceipt) : ArtifactDigest list =
    receiptTools receipt
    @ receipt.ProbeSources
    @ [ receipt.ProbeArchive ]
    @ receipt.GeneratedSources
    @ receipt.Intermediates
    @ [ receipt.Mlir; receipt.Binary ]
    @ receipt.Logs

let private isSha256 (text: string) : bool =
    text.Length = 64 && text |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))

// ─── Retained evidence ───

/// Where the journey `label` keeps its evidence under one evidence directory: the work
/// directory `<label>`, and beside it the digest manifest `<label>.sha256` and the discharge
/// summary `<label>.discharges`.
type JourneyEvidence = { WorkDirectory: string; ManifestFile: string; DischargesFile: string }

let private evidenceIn (directory: string) (label: string) : JourneyEvidence =
    { WorkDirectory = Path.Combine(directory, label)
      ManifestFile = Path.Combine(directory, label + ".sha256")
      DischargesFile = Path.Combine(directory, label + ".discharges") }

/// The evidence of the journey `label`: under `native-callbacks` in the scratch root of the
/// test process, in OS temporary storage. That root is removed when the process exits.
let private evidenceOf (label: string) : JourneyEvidence = evidenceIn (TestHelpers.TestScratch.shared "native-callbacks") label

/// Write `digests` to `manifest` in the `sha256sum` line format: the digest, two spaces, the path.
let private writeManifest (manifest: string) (digests: ArtifactDigest list) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName manifest) |> ignore
    File.WriteAllLines(manifest, digests |> List.map (fun digest -> digest.Sha256 + "  " + digest.Path))

/// How a digest manifest differs from the files it names.
[<RequireQualifiedAccess>]
type ManifestRefusal =
    /// The manifest file does not exist.
    | ManifestMissing of manifest: string
    /// The manifest names no file.
    | ManifestEmpty of manifest: string
    /// The 1-based line `line` is not `<64 lowercase hex digits>  <fully qualified path>`.
    | LineMalformed of line: int * text: string
    /// A recorded path names no file.
    | ArtifactMissing of path: string
    /// The file's SHA-256 is `actual`; the manifest records `recorded`.
    | DigestMismatch of path: string * recorded: string * actual: string

/// The entries of `manifest`, in line order, or the refusal of the file or of each line that is no entry.
let private readManifest (manifest: string) : Result<ArtifactDigest list, ManifestRefusal list> =
    if not (File.Exists manifest) then Error [ ManifestRefusal.ManifestMissing manifest ]
    else
        let entry (index: int, line: string) : Result<ArtifactDigest, ManifestRefusal> =
            let separatorAt = 64
            if line.Length > separatorAt + 2
               && isSha256 (line.Substring(0, separatorAt))
               && line.Substring(separatorAt, 2) = "  "
               && Path.IsPathFullyQualified(line.Substring(separatorAt + 2)) then
                Ok { Path = line.Substring(separatorAt + 2); Sha256 = line.Substring(0, separatorAt) }
            else Error(ManifestRefusal.LineMalformed(index + 1, line))
        let entries = File.ReadAllLines manifest |> List.ofArray |> List.indexed |> List.map entry
        match entries |> List.choose (fun read -> match read with Error refusal -> Some refusal | Ok _ -> None) with
        | [] when entries.IsEmpty -> Error [ ManifestRefusal.ManifestEmpty manifest ]
        | [] -> Ok(entries |> List.choose (fun read -> match read with Ok digest -> Some digest | Error _ -> None))
        | refusals -> Error refusals

/// Read `manifest` from disk and hash every file it names. The entries when each recorded
/// path is an existing file whose SHA-256 is the recorded digest; otherwise every refusal, in
/// line order.
let private verifyManifest (manifest: string) : Result<ArtifactDigest list, ManifestRefusal list> =
    readManifest manifest
    |> Result.bind (fun entries ->
        let refusals =
            entries
            |> List.choose (fun recorded ->
                if not (File.Exists recorded.Path) then Some(ManifestRefusal.ArtifactMissing recorded.Path)
                else
                    let actual = ClangToolchain.sha256 recorded.Path
                    if actual = recorded.Sha256 then None
                    else Some(ManifestRefusal.DigestMismatch(recorded.Path, recorded.Sha256, actual)))
        if refusals.IsEmpty then Ok entries else Error refusals)

/// Write the receipt's digests to the manifest of its label, and one
/// `<stage> <outcomes> <receipt digest>` line per inspected discharge receipt to its
/// discharge summary.
let private recordReceipt (receipt: JourneyReceipt) : unit =
    let evidence = evidenceOf receipt.Label
    writeManifest evidence.ManifestFile (receiptDigests receipt)
    File.WriteAllLines(
        evidence.DischargesFile,
        receipt.Discharges |> List.map (fun evidence -> $"{evidence.DischargedStage} {evidence.Discharged} {evidence.Receipt.Sha256}"))

/// Require that Composer compiled and linked `label` without an error or a warning: exit code
/// 0, no diagnostic above `info`, no output line naming an error or a warning, and the linked
/// executable at `binary`.
let private requireCompiled (label: string) (outcome: ProcessOutcome) (log: string) (binary: string) : unit =
    let output = outcome.Stdout + outcome.Stderr
    let reported = diagnosticsOf output |> List.filter (fun d -> d.Severity <> "info")
    if outcome.ExitCode <> 0 then
        failwithf "%s: Composer exited with code %d (log %s):\n%s" label outcome.ExitCode log (summarized reported)
    if not reported.IsEmpty then
        failwithf "%s: Composer reported errors or warnings (log %s):\n%s" label log (summarized reported)
    match errorOrWarningLines output with
    | [] -> ()
    | lines -> failwithf "%s: Composer's output names an error or a warning (log %s):\n%s" label log (String.concat "\n" lines)
    Assert.True(File.Exists binary, $"{label}: Composer exited with code 0 and wrote no executable at {binary} (log {log})")

/// Compile `sources` as `label`, require a compile without error or warning, inspect the
/// callback entries of `expected` in the retained `10_output.mlir` and the retained discharge
/// receipts, run the executable and require exit code 0 and `expectedLine` as its whole
/// standard output.
let private accepted (stage: JourneyStage) (label: string) (generated: string list) (witnesses: string list)
                     (expected: EntryExpectation list) (expectedLine: string) : JourneyReceipt =
    let outcome, log, binary = compile stage label ([ "Loading.clef" ] @ generated @ witnesses)
    requireCompiled label outcome log binary
    let intermediates = Path.Combine(stage.Work, "targets", "intermediates")
    let mlir = Path.Combine(stage.Work, label + ".mlir")
    File.Copy(required "Composer's retained MLIR" (Path.Combine(intermediates, "10_output.mlir")), mlir)
    let entries =
        match inspect (File.ReadAllText mlir) expected with
        | Ok entries -> entries
        | Error refusals -> failwithf "%s: %s does not hold the callback entry ABI: %A" label mlir refusals
    let discharges = inspectDischarges label intermediates
    let retained = Directory.GetFiles(intermediates) |> Array.sort |> List.ofArray |> List.map digestOf
    let ran, runLog = logged stage.Work ("run-" + label) (TimeSpan.FromSeconds 15.0) (ProcessLaunch.startInfo (required "the linked executable" binary) [])
    if ran.ExitCode <> 0 then
        failwithf "%s: %s exited with code %d (log %s):\n%s%s" label binary ran.ExitCode runLog ran.Stdout ran.Stderr
    Assert.Equal(expectedLine, ran.Stdout.Trim())
    let logs = Directory.GetFiles(stage.Work, "*.log") |> Array.sort |> List.ofArray |> List.map digestOf
    for written in [ log; runLog ] do
        Assert.True(logs |> List.exists (fun digest -> digest.Path = written), $"{label}: the work directory does not hold the log {written}")
    let clang, _ = ClangToolchain.identity ()
    let archiver, _ = ClangToolchain.archiverIdentity ()
    { Label = label
      Clang = digestOf clang
      Archiver = digestOf archiver
      Composer = digestOf stage.Composer
      ProbeSources = probeSources |> List.map (fun source -> digestOf (Path.Combine(stage.Work, source)))
      ProbeArchive = digestOf stage.Archive
      GeneratedSources = generated |> List.map (fun source -> digestOf (Path.Combine(stage.Work, source)))
      Intermediates = retained
      Mlir = digestOf mlir
      Binary = digestOf binary
      Logs = logs
      Entries = entries
      Discharges = discharges
      Stdout = ran.Stdout }

/// Require the receipt's digests to be SHA-256 values of existing files, the staged probe
/// sources to be the fixtures, the retained intermediates to hold the inspected MLIR and
/// every inspected discharge receipt, a PSG discharge receipt that discharges at least one
/// obligation, one accepted entry set per expected module, and record the receipt. The
/// recorded receipt.
let private requireReceipt (expected: EntryExpectation list) (generatedCount: int) (receipt: JourneyReceipt) : JourneyReceipt =
    for digest in receiptDigests receipt do
        Assert.True(isSha256 digest.Sha256, $"{digest.Path} has the digest '{digest.Sha256}', which is no SHA-256 value")
        Assert.Equal(ClangToolchain.sha256 digest.Path, digest.Sha256)
    same
        (probeSources |> List.map (fun source -> ClangToolchain.sha256 (Path.Combine(fixtureDirectory, source))))
        (receipt.ProbeSources |> List.map (fun digest -> digest.Sha256))
    Assert.Equal(generatedCount, receipt.GeneratedSources.Length)
    let retained = receipt.Intermediates |> List.map (fun digest -> Path.GetFileName digest.Path, digest.Sha256)
    Assert.True(List.contains ("10_output.mlir", receipt.Mlir.Sha256) retained, $"the retained intermediates do not hold the inspected MLIR {receipt.Mlir.Path}")
    for evidence in receipt.Discharges do
        Assert.True(
            List.contains (Path.GetFileName evidence.Receipt.Path, evidence.Receipt.Sha256) retained,
            $"the retained intermediates do not hold the inspected receipt {evidence.Receipt.Path}")
        Assert.True(evidence.Discharged > 0, $"{evidence.Receipt.Path} discharges no obligation")
    // The PSG discharge is required of every accepted journey, whatever else is retained.
    Assert.True(
        dischargesRequiredStage receipt.Discharges,
        $"{receipt.Label}: no inspected receipt of the {requiredDischargeStage} stage discharges an obligation")
    same
        (expected |> List.map (fun entries -> entries.Module, entries.Record))
        (receipt.Entries |> List.map (fun entry -> entry.EntryModule, entry.RecordSymbol))
    recordReceipt receipt
    receipt

/// Whether `path` names a file beneath `directory`.
let private isBeneath (directory: string) (path: string) : bool =
    path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

/// Read the manifest of the receipt's label from disk, after its journey returned, and require:
/// - every recorded path is an existing file whose SHA-256 is the recorded digest;
/// - the recorded entries are the receipt's digests, in order;
/// - every recorded file other than the three tools is beneath the journey's work directory,
///   and the manifest names logs and retained intermediates;
/// - the discharge summary exists.
let private requireRetained (receipt: JourneyReceipt) : unit =
    let evidence = evidenceOf receipt.Label
    let recorded =
        match verifyManifest evidence.ManifestFile with
        | Ok recorded -> recorded
        | Error refusals ->
            failwithf "%s: %s names files that are not retained with their recorded digests (evidence in %s): %A"
                receipt.Label evidence.ManifestFile evidence.WorkDirectory refusals
    same (receiptDigests receipt) recorded
    let tools = receiptTools receipt |> List.map (fun digest -> digest.Path)
    let outside =
        recorded
        |> List.map (fun digest -> digest.Path)
        |> List.filter (fun path -> not (List.contains path tools) && not (isBeneath evidence.WorkDirectory path))
    same ([]: string list) outside
    Assert.False(receipt.Logs.IsEmpty, $"{receipt.Label}: the manifest names no log")
    Assert.False(receipt.Intermediates.IsEmpty, $"{receipt.Label}: the manifest names no retained intermediate")
    Assert.True(File.Exists evidence.DischargesFile, $"{receipt.Label}: {evidence.DischargesFile} does not exist")

/// Run `body` over the work directory of `evidence`. The work directory, and the manifest and
/// discharge summary an earlier run recorded for it, are deleted first: from that point the
/// earlier manifest would name deleted files. The work directory is retained when `body`
/// returns and when it fails, so every file a recorded manifest names stays on disk until the
/// next run of the same journey, or until the test process exits and its scratch root is
/// removed. The result of `body`.
let private withJourneyAt (evidence: JourneyEvidence) (body: string -> 'T) : 'T =
    for earlier in [ evidence.ManifestFile; evidence.DischargesFile ] do
        if File.Exists earlier then File.Delete earlier
    if Directory.Exists evidence.WorkDirectory then Directory.Delete(evidence.WorkDirectory, true)
    Directory.CreateDirectory evidence.WorkDirectory |> ignore
    try
        body evidence.WorkDirectory
    with error ->
        raise (InvalidOperationException($"{error.Message}\nEvidence is kept in {evidence.WorkDirectory} until the test process exits", error))

/// `withJourneyAt` over `native-callbacks/<label>` in the scratch root of the test process.
let private withJourney (label: string) (body: string -> 'T) : 'T = withJourneyAt (evidenceOf label) body

[<Fact>]
let ``a digest manifest is accepted only when every file it names exists and has its recorded digest`` () =
    withScratch "digest-manifest" (fun directory ->
        let manifest = Path.Combine(directory, "journey.sha256")
        let work = Path.Combine(directory, "journey")
        Directory.CreateDirectory(Path.Combine(work, "targets", "intermediates")) |> ignore
        let write (relative: string) (text: string) : string =
            let path = Path.Combine(work, relative)
            File.WriteAllText(path, text)
            path
        let binary = write "journey" "linked executable"
        let mlir = write (Path.Combine("targets", "intermediates", "10_output.mlir")) "module @main {}"
        let log = write "build-journey.log" ""
        let digests = [ binary; mlir; log ] |> List.map digestOf
        let refusedWith (expected: ManifestRefusal list) =
            same (Error expected: Result<ArtifactDigest list, ManifestRefusal list>) (verifyManifest manifest)
        // No manifest, and a manifest that names no file.
        refusedWith [ ManifestRefusal.ManifestMissing manifest ]
        writeManifest manifest []
        refusedWith [ ManifestRefusal.ManifestEmpty manifest ]
        // Every named file exists with its recorded digest: the entries, in line order.
        writeManifest manifest digests
        same (Ok digests: Result<ArtifactDigest list, ManifestRefusal list>) (verifyManifest manifest)
        same [ ClangToolchain.sha256 binary + "  " + binary; ClangToolchain.sha256 mlir + "  " + mlir; ClangToolchain.sha256 log + "  " + log ] (File.ReadAllLines manifest |> List.ofArray)
        // The digest of an empty file is the SHA-256 of no bytes.
        same "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" (digestOf log).Sha256
        // A file whose bytes changed after the manifest was written.
        File.WriteAllText(mlir, "module @main { }")
        refusedWith [ ManifestRefusal.DigestMismatch(mlir, digests[1].Sha256, ClangToolchain.sha256 mlir) ]
        // A named file that no longer exists, with the changed one: both, in line order.
        File.Delete binary
        refusedWith [ ManifestRefusal.ArtifactMissing binary; ManifestRefusal.DigestMismatch(mlir, digests[1].Sha256, ClangToolchain.sha256 mlir) ]
        // The work directory deleted after the manifest was written: every entry is missing.
        Directory.Delete(work, true)
        refusedWith (digests |> List.map (fun digest -> ManifestRefusal.ArtifactMissing digest.Path))
        // A directory at a recorded path is no file.
        Directory.CreateDirectory binary |> ignore
        refusedWith (digests |> List.map (fun digest -> ManifestRefusal.ArtifactMissing digest.Path))
        // Lines that are no `<64 lowercase hex digits>  <fully qualified path>` are refused at
        // their line numbers, before any file is hashed.
        let digest = digests[0].Sha256
        let lines =
            [ digest + "  " + log
              digest.ToUpperInvariant() + "  " + log
              digest + " " + log
              digest.Substring(1) + "  " + log
              digest + "  "
              digest + "  journey/build-journey.log"
              ""
              log ]
        File.WriteAllLines(manifest, lines)
        refusedWith [ for index in 1 .. lines.Length - 1 -> ManifestRefusal.LineMalformed(index + 1, lines[index]) ])

[<Fact>]
let ``the work directory of a journey is emptied when the journey starts and retained when it returns or fails`` () =
    withScratch "journey-evidence" (fun directory ->
        let evidence = evidenceIn directory "retained"
        same (Path.Combine(directory, "retained")) evidence.WorkDirectory
        same (Path.Combine(directory, "retained.sha256")) evidence.ManifestFile
        same (Path.Combine(directory, "retained.discharges")) evidence.DischargesFile
        // The journeys of this module keep their evidence in OS temporary storage under
        // farscape-tests, outside the checkout (#215).
        let journeys = Path.Combine(TestHelpers.TestScratch.root (), "native-callbacks")
        let retained = evidenceOf "single-entry"
        same (Path.Combine(journeys, "single-entry")) retained.WorkDirectory
        same (Path.Combine(journeys, "single-entry.sha256")) retained.ManifestFile
        let temporary = Path.Combine(Path.GetTempPath(), "farscape-tests")
        Assert.True(isBeneath temporary retained.WorkDirectory, $"{retained.WorkDirectory} is not beneath {temporary}")
        Assert.False(isBeneath repositoryRoot retained.WorkDirectory, $"{retained.WorkDirectory} is beneath the checkout {repositoryRoot}")
        // A journey that returns: its result is the body's, and its files stay on disk with
        // the digests its manifest records.
        let record (work: string) : ArtifactDigest list =
            let digests =
                [ "journey"; "journey.mlir" ]
                |> List.map (fun name ->
                    File.WriteAllText(Path.Combine(work, name), name + " of " + Guid.NewGuid().ToString("N"))
                    digestOf (Path.Combine(work, name)))
            writeManifest evidence.ManifestFile digests
            File.WriteAllText(evidence.DischargesFile, "psg 1 " + digests[0].Sha256 + "\n")
            digests
        let first = withJourneyAt evidence record
        same (Ok first: Result<ArtifactDigest list, ManifestRefusal list>) (verifyManifest evidence.ManifestFile)
        for digest in first do
            Assert.True(isBeneath evidence.WorkDirectory digest.Path, $"{digest.Path} is not beneath {evidence.WorkDirectory}")
        // The next run starts from an empty work directory without the earlier manifest and
        // discharge summary, and its own files are the retained ones afterwards.
        let stray = Path.Combine(evidence.WorkDirectory, "stray.log")
        File.WriteAllText(stray, "left by the first run")
        let second =
            withJourneyAt evidence (fun work ->
                same ([]: string list) (Directory.GetFileSystemEntries work |> List.ofArray)
                same (false, false) (File.Exists evidence.ManifestFile, File.Exists evidence.DischargesFile)
                record work)
        Assert.False(File.Exists stray, $"{stray} survived the start of the second run")
        same (Ok second: Result<ArtifactDigest list, ManifestRefusal list>) (verifyManifest evidence.ManifestFile)
        Assert.NotEqual<ArtifactDigest list>(first, second)
        // A journey that fails: the failure names the retained work directory, the files
        // written before the failure stay, and the earlier manifest is not left naming them.
        let failure =
            Assert.Throws<InvalidOperationException>(fun () ->
                withJourneyAt evidence (fun work ->
                    File.WriteAllText(Path.Combine(work, "build-journey.log"), "error")
                    if File.Exists(Path.Combine(work, "build-journey.log")) then failwith "the compile is refused"))
        same ("the compile is refused\nEvidence is kept in " + evidence.WorkDirectory + " until the test process exits") failure.Message
        same [ Path.Combine(evidence.WorkDirectory, "build-journey.log") ] (Directory.GetFileSystemEntries evidence.WorkDirectory |> List.ofArray)
        same
            (Error [ ManifestRefusal.ManifestMissing evidence.ManifestFile ]: Result<ArtifactDigest list, ManifestRefusal list>)
            (verifyManifest evidence.ManifestFile))

/// `text` with each of its `count` occurrences of `original` replaced; another count fails.
let private replaceExactly (count: int) (original: string) (replacement: string) (text: string) : string =
    let occurrences = (text.Length - text.Replace(original, "").Length) / original.Length
    if occurrences <> count then
        failwithf "'%s' occurs %d times in the witness, expected %d; the second witness cannot be derived from it" original occurrences count
    text.Replace(original, replacement)

/// The witness of `Landing.Other.Boundary` (other_probe.h), derived from Boundary.clef: its
/// module, the opened binding namespace, the generated entry record names, the externs
/// (`landing_other_*`) and the values other_probe.c passes and expects (`-290`, `-19`, `580`).
let private otherBoundary (boundary: string) : string =
    [ 1, "module Landing.Acceptance.Boundary", "module Landing.Acceptance.OtherBoundary"
      1, "open Landing.Boundary", "open Landing.Other.Boundary"
      2, "Callback_Landing_002E_Boundary_", "Callback_Landing_002E_Other_002E_Boundary_"
      2, "landing_record", "landing_other_record"
      2, "landing_invoke", "landing_other_invoke"
      2, "delta = -173", "delta = -290"
      1, "then -7 else", "then -19 else"
      1, "landing_other_record 346", "landing_other_record 580" ]
    |> List.fold (fun (text: string) (count: int, original: string, replacement: string) -> replaceExactly count original replacement text) boundary

[<Fact>]
let ``the second witness is derived from Boundary.clef with its own module, namespace, externs and values`` () =
    let boundary = File.ReadAllText(required "the witness" (Path.Combine(fixtureDirectory, "Boundary.clef"))).Replace("\r\n", "\n")
    let derived = (otherBoundary boundary).Split('\n') |> List.ofArray
    let derivedLines =
        [ "module Landing.Acceptance.OtherBoundary"
          "open Landing.Other.Boundary"
          "let private scalarEntry (value: int) (delta: int) : int ="
          "    if value = 4294967295 && delta = -290 then -19 else 9"
          "let private voidEntry (value: int) (delta: int) : unit ="
          "    if value = 4294967295 && delta = -290 then"
          "        landing_other_record 580"
          "        landing_other_record (-1)"
          "let private invokeScalar () : int ="
          "    landing_other_invoke { Callback_Landing_002E_Other_002E_Boundary_003A__003A_LandingCallbackEntry.Invoke = FnPtr.ofFunction scalarEntry }"
          "let private invokeVoid () : int ="
          "    landing_other_invoke_void { Callback_Landing_002E_Other_002E_Boundary_003A__003A_LandingVoidEntry.Invoke = FnPtr.ofFunction voidEntry }"
          "    let scalarResult = invokeScalar ()"
          "    let voidResult = invokeVoid ()" ]
    for line in derivedLines do
        Assert.True(List.contains line derived, $"the derived witness lacks the line '{line}'")
    // Nothing of the first witness is left: its module, namespace, record names, externs and values.
    let text = String.concat "\n" derived
    let firstWitnessOnly =
        [ "Landing.Acceptance.Boundary"; "open Landing.Boundary"; "Callback_Landing_002E_Boundary_"
          "landing_record"; "landing_invoke "; "landing_invoke_void"; "-173"; "-7 "; "346" ]
    for absent in firstWitnessOnly do
        Assert.False(text.Contains(absent), $"the derived witness still holds '{absent}'")
    // A witness the derivation does not fit is refused, not derived in part.
    let error = Assert.ThrowsAny<exn>(fun () -> otherBoundary (boundary.Replace("landing_record 346", "landing_record 347")) |> ignore)
    Assert.Contains("'landing_other_record 346' occurs 0 times in the witness, expected 1", error.Message)

// ─── Native journey Facts ───

[<Fact>]
let ``single-entry: generated scalar and void callback entries compile, link the probe archive and run`` () =
    withJourney "single-entry" (fun work ->
        let stage = stage work
        let bindings = generate stage "probe.pilot.toml" "probe.h" "Bindings" "Landing.Boundary"
        accepted stage "single-entry" bindings [ "Boundary.clef"; "Main.clef" ] [ boundaryEntries ]
            "landing: scalar/void callback arguments and results passed"
        |> requireReceipt [ boundaryEntries ] 2)
    // After the journey returned: every file single-entry.sha256 names is on disk with its recorded digest.
    |> requireRetained

[<Fact>]
let ``duplicate-entry: the same callback typedef names of two headers generated under two namespaces compile, link and run together`` () =
    withJourney "duplicate-entry" (fun work ->
        let stage = stage work
        let first = generate stage "probe.pilot.toml" "probe.h" "Bindings" "Landing.Boundary"
        let second = generate stage "other_probe.pilot.toml" "other_probe.h" "OtherBindings" "Landing.Other.Boundary"
        // Both headers declare the typedef names; each namespace has an entry record of its own.
        for header in [ "probe.h"; "other_probe.h" ] do
            let declared = File.ReadAllText(Path.Combine(work, header))
            for typedef in [ "typedef int (*LandingCallback)(unsigned int value, long delta);"; "typedef void (*LandingVoid)(unsigned int value, long delta);" ] do
                Assert.Contains(typedef, declared)
        File.WriteAllText(Path.Combine(work, "OtherBoundary.clef"), otherBoundary (File.ReadAllText(Path.Combine(work, "Boundary.clef")).Replace("\r\n", "\n")))
        accepted stage "duplicate-entry" (first @ second) [ "Boundary.clef"; "OtherBoundary.clef"; "DuplicateMain.clef" ]
            [ boundaryEntries; otherBoundaryEntries ] "landing: duplicate typedefs in different namespaces passed"
        |> requireReceipt [ boundaryEntries; otherBoundaryEntries ] 4)
    // After the journey returned: every file duplicate-entry.sha256 names is on disk with its recorded digest.
    |> requireRetained

// ─── Refused witnesses ───

/// Require that Composer refused the witness `label`: a nonzero exit code, no binary and no
/// unhandled exception. Composer's whole output.
let private requireRefused (label: string) (outcome: ProcessOutcome) (log: string) (binary: string) : string =
    let output = outcome.Stdout + outcome.Stderr
    Assert.True(outcome.ExitCode <> 0, $"{label}: Composer exited with code 0 (log {log})")
    Assert.False(File.Exists binary, $"{label}: Composer wrote {binary} for a refused witness")
    Assert.False(output.Contains("Unhandled exception"), $"{label}: Composer raised an unhandled exception (log {log})")
    output

let private reportedIn (output: string) : string =
    summarized (diagnosticsOf output |> List.filter (fun d -> d.Severity <> "info"))

/// A mutated copy of Boundary.clef.
type BoundaryMutation = { Witness: string; Replacements: (string * string) list }

let private mutated (mutation: BoundaryMutation) (boundary: string) : string =
    mutation.Replacements |> List.fold (fun text (original, replacement) -> mutate original replacement text) boundary

// The shape of Boundary.clef is the one Composer (observed at v0.0.2+207e0eec) compiles and links:
// - Fidelity.PSG refuses an entry record held by a module-level binding
//   (`Emission.Callable.Aggregates.I5`), so each record is constructed at the call that passes it.
// - Alex previously refused a return type annotation on the scalar entry (`AX4001`), now fixed (#212):
//   `scalarEntry` declares `: int`.
// - Composer's artifact correspondence checks each callback record construction at its call site.
//   A single local binding in a function is accepted (`local-binding` below); placing two
//   distinct callback entries in one `run ()` is covered by the `#213` acceptance Fact
//   `multi-callback-in-one-caller` below (blocked on Composer `EnvironmentArtifacts.recordToken`
//   at Composer v0.0.2+207e0eec; filter pre-#213 green runs with `--filter "Category!=Pending213"`).

/// The record expression `invokeScalar` passes to `landing_invoke`, built from the function `entry`.
let private scalarRecord (entry: string) : string =
    "{ Callback_Landing_002E_Boundary_003A__003A_LandingCallbackEntry.Invoke = FnPtr.ofFunction " + entry + " }"

/// The record expression `invokeVoid` passes to `landing_invoke_void`.
let private voidRecord = "{ Callback_Landing_002E_Boundary_003A__003A_LandingVoidEntry.Invoke = FnPtr.ofFunction voidEntry }"

/// The functions of Boundary.clef that construct an entry record, each at the call it returns.
let private invokeScalarSource = "let private invokeScalar () : int =\n    landing_invoke " + scalarRecord "scalarEntry" + "\n"
let private invokeVoidSource = "let private invokeVoid () : int =\n    landing_invoke_void " + voidRecord + "\n"

/// The scalar entry record is built from the void entry: the callback returns unit where the C
/// typedef returns int.
let private wrongReturn : BoundaryMutation =
    { Witness = "wrong-return"
      Replacements = [ (scalarRecord "scalarEntry", scalarRecord "voidEntry") ] }

/// The scalar entry takes a `bool` where the generated entry passes an `int`; its body stays
/// well typed.
let private wrongArgument : BoundaryMutation =
    { Witness = "wrong-argument"
      Replacements =
        [ ("let private scalarEntry (value: int) (delta: int) : int =\n    if value = 4294967295 && delta = -173 then -7 else 9",
           "let private scalarEntry (value: bool) (delta: int) : int =\n    if value && delta = -173 then -7 else 9") ] }

/// Boundary.clef with `invokeVoid` moved before `invokeScalar`, and `invokeScalar` holding
/// `body` under its unchanged header. `invokeVoid ()` is the one source of a run-time `int`
/// that constructs no scalar entry record, so `invokeScalar` can bind its result as a local.
let private invokeScalarAfterVoid (witness: string) (body: string list) : BoundaryMutation =
    { Witness = witness
      Replacements =
        [ (invokeScalarSource + "\n" + invokeVoidSource,
           invokeVoidSource + "\n" + String.concat "\n" ([ "let private invokeScalar () : int =" ] @ body @ [ "" ])) ] }

/// The discriminating control of the closure refusals: `invokeScalar` binds the run-time
/// result of `invokeVoid ()` as a local and passes `landing_invoke` the record of the closed
/// module-level `scalarEntry`. Composer compiles and links it without an error or a warning,
/// so the local binding and the call of `invokeVoid` are not what a closure refusal reports.
let private localBinding : BoundaryMutation =
    invokeScalarAfterVoid "local-binding"
        [ "    let _offset = invokeVoid ()"
          "    landing_invoke " + scalarRecord "scalarEntry" ]

/// One closure passed to `FnPtr.ofFunction` in the one call of `invokeScalar`: the mutation,
/// the expression that follows `FnPtr.ofFunction`, and the message of the `CCS8096` error
/// Composer (observed at v0.0.2+d17eb195) reports at the line of that expression.
type ClosureRefusal = { Mutation: BoundaryMutation; Entry: string; Message: string }

/// The local binding of `localBinding`, with a function nested in `invokeScalar` that reads
/// the local `offset` in place of `scalarEntry`. The difference from `localBinding` is the
/// function `FnPtr.ofFunction` receives.
let private capturingFunction : ClosureRefusal =
    { Mutation =
        invokeScalarAfterVoid "capturing-function"
            [ "    let offset = invokeVoid ()"
              "    let capturingEntry (value: int) (delta: int) ="
              "        if value = 4294967295 && delta = -173 then offset - 7 else 9"
              "    landing_invoke " + scalarRecord "capturingEntry" ]
      Entry = "capturingEntry"
      Message = "FnPtr.ofFunction requires a named module function without captured state; pass the environment explicitly." }

/// The same local binding, with a lambda expression that reads the local `offset`.
let private capturingLambdaEntry = "(fun (value: int) (delta: int) -> if value = 4294967295 && delta = -173 then offset - 7 else 9)"
let private capturingLambda : ClosureRefusal =
    { Mutation =
        invokeScalarAfterVoid "capturing-lambda"
            [ "    let offset = invokeVoid ()"
              "    landing_invoke " + scalarRecord capturingLambdaEntry ]
      Entry = capturingLambdaEntry
      Message = "FnPtr.ofFunction requires a resolved named function; a closure cannot be converted to a native callback." }

/// CCS's type mismatch code.
let private typeMismatch = "CCS8003"

/// The code with which CCS refuses the argument of `FnPtr.ofFunction` that is no closed named
/// module function. Composer (observed at v0.0.2+d17eb195) reports it for a nested function and
/// for a lambda expression, whether or not the function reads a local of its enclosing function.
let private closureConversion = "CCS8096"

/// The code of the platform description's callback rules, and the rule CCS reports under it,
/// at the same line as `closureConversion`, for a callback that is no such function.
let private listenerRule = "CCS8207"
let private listenerRuleMessage = "A declared listener callback must be FnPtr.ofFunction of a named module function without captures."

/// The 1-based line of `witness` that passes `landing_invoke` the scalar entry record built
/// from `entry`; another count of such lines than one fails.
let private scalarRecordLine (entry: string) (witness: string) : int =
    match witness.Split('\n') |> Array.indexed |> Array.filter (fun (_, line) -> line = "    landing_invoke " + scalarRecord entry) with
    | [| (index, _) |] -> index + 1
    | found -> failwithf "the witness holds %d lines that pass landing_invoke the record of %s, expected 1" found.Length entry

[<Fact>]
let ``the boundary mutations change the witness they describe and nothing else`` () =
    let boundary = File.ReadAllText(required "the witness" (Path.Combine(fixtureDirectory, "Boundary.clef"))).Replace("\r\n", "\n")
    let lines (text: string) = text.Split('\n') |> List.ofArray
    // The lines of `text` that construct an entry record.
    let constructions (text: string) = lines text |> List.filter (fun line -> line.Contains("FnPtr.ofFunction"))
    // The witness holds the shape Composer compiles and links: `scalarEntry` declares `: int`,
    // and each record is constructed once, at the call its function returns.
    for held in [ "let private scalarEntry (value: int) (delta: int) : int =\n"; invokeScalarSource + "\n"; invokeVoidSource + "\n" ] do
        Assert.Contains(held, boundary)
    same [ "    landing_invoke " + scalarRecord "scalarEntry"; "    landing_invoke_void " + voidRecord ] (constructions boundary)
    Assert.Contains("let run () : int =\n    let scalarResult = invokeScalar ()\n    let voidResult = invokeVoid ()\n", boundary)
    // The type mutations keep every line number: the record's diagnostic line is the unmutated one.
    for mutation in [ wrongReturn; wrongArgument ] do
        let changed = List.zip (lines boundary) (lines (mutated mutation boundary)) |> List.filter (fun (before, after) -> before <> after)
        Assert.True(not changed.IsEmpty && changed.Length <= 2, $"{mutation.Witness} changes {changed.Length} lines")
    same
        [ "    landing_invoke " + scalarRecord "scalarEntry", "    landing_invoke " + scalarRecord "voidEntry" ]
        (List.zip (lines boundary) (lines (mutated wrongReturn boundary)) |> List.filter (fun (before, after) -> before <> after))
    // The discriminating control moves `invokeVoid` before `invokeScalar` and binds its result
    // there; `invokeScalar` still makes its one call with the record of the module-level entry.
    let control = mutated localBinding boundary
    Assert.Contains(invokeVoidSource + "\nlet private invokeScalar () : int =\n    let _offset = invokeVoid ()\n    landing_invoke " + scalarRecord "scalarEntry" + "\n\nlet run () : int =\n", control)
    same [ "    landing_invoke_void " + voidRecord; "    landing_invoke " + scalarRecord "scalarEntry" ] (constructions control)
    same 20 (scalarRecordLine "scalarEntry" control)
    // Each closure mutation is the control with a closure in place of `scalarEntry`: the lines
    // before `invokeScalar` and from `run` on are the control's, `invokeScalar` binds the same
    // run-time local under the name the closure reads, and makes one call of `landing_invoke`.
    let header = "let private invokeScalar () : int ="
    let split (text: string) : string list * string list * string list =
        let all = lines text
        let from = List.findIndex (fun line -> line = header) all
        let until = List.findIndex (fun line -> line = "let run () : int =") all
        List.take from all, all[from .. until - 1], List.skip until all
    let controlBefore, controlBody, controlAfter = split control
    same [ header; "    let _offset = invokeVoid ()"; "    landing_invoke " + scalarRecord "scalarEntry"; "" ] controlBody
    let nestedBody =
        [ header
          "    let offset = invokeVoid ()"
          "    let capturingEntry (value: int) (delta: int) ="
          "        if value = 4294967295 && delta = -173 then offset - 7 else 9"
          "    landing_invoke " + scalarRecord "capturingEntry"
          "" ]
    let lambdaBody =
        [ header
          "    let offset = invokeVoid ()"
          "    landing_invoke { Callback_Landing_002E_Boundary_003A__003A_LandingCallbackEntry.Invoke = FnPtr.ofFunction (fun (value: int) (delta: int) -> if value = 4294967295 && delta = -173 then offset - 7 else 9) }"
          "" ]
    for (closure, body, recordLine) in [ (capturingFunction, nestedBody, 22); (capturingLambda, lambdaBody, 20) ] do
        let witness = mutated closure.Mutation boundary
        let before, own, after = split witness
        same (controlBefore, controlAfter) (before, after)
        same body own
        same [ "    landing_invoke_void " + voidRecord; "    landing_invoke " + scalarRecord closure.Entry ] (constructions witness)
        same 1 (own |> List.filter (fun line -> line.Contains("landing_invoke ")) |> List.length)
        same recordLine (scalarRecordLine closure.Entry witness)
        // The closure reads the local the control binds and does not read.
        Assert.Contains("then offset - 7 else 9", witness)
    same [ "local-binding"; "capturing-function"; "capturing-lambda" ] [ localBinding.Witness; capturingFunction.Mutation.Witness; capturingLambda.Mutation.Witness ]
    // A witness with two lines that pass the record of one entry has no one record line.
    let twice = Assert.ThrowsAny<exn>(fun () -> scalarRecordLine "scalarEntry" (control + "    landing_invoke " + scalarRecord "scalarEntry" + "\n") |> ignore)
    Assert.Contains("the witness holds 2 lines that pass landing_invoke the record of scalarEntry, expected 1", twice.Message)
    same [ "CCS8003"; "CCS8413" ] (compilerCodes "Boundary.clef:16: error CCS8003: Type mismatch\n\u001b[31merror CCS8413\u001b[0m: unsettled; see CCS8003, FS9144 and XCCS8403")
    // The line format of the closure refusal, as Composer prints it with its span line.
    same
        [ ({ File = "capturing-function.clef"; Line = 22; Severity = "error"; Code = "CCS8096"; Message = capturingFunction.Message }: CompilerDiagnostic) ]
        (diagnosticsOf
            ("\u001b[38;5;9mcapturing-function.clef:22\u001b[0m: \u001b[38;5;9merror\u001b[0m \u001b[38;5;9mCCS8096\u001b[0m: "
             + capturingFunction.Message + "\n    span: /work/capturing-function.clef(22,93)-(22,124)\n"))

[<Fact>]
let ``a witness whose callback return or argument type differs from the generated entry is refused by Composer`` () =
    withJourney "refused-witness" (fun work ->
        let stage = stage work
        let bindings = generate stage "probe.pilot.toml" "probe.h" "Bindings" "Landing.Boundary"
        let boundary = File.ReadAllText(Path.Combine(work, "Boundary.clef")).Replace("\r\n", "\n")
        // The line of the scalar entry record expression, which no mutation moves.
        let listenerLine =
            match boundary.Split('\n') |> Array.indexed |> Array.filter (fun (_, line) -> line.Contains(scalarRecord "scalarEntry")) with
            | [| (index, _) |] -> index + 1
            | found -> failwithf "Boundary.clef holds %d lines with the scalar entry record expression, expected 1" found.Length
        let mismatchesOf (output: string) = diagnosticsOf output |> List.filter (fun d -> d.Severity = "error" && d.Code = typeMismatch)
        // Control: in this run the unmutated witness compiles and links without an error or a
        // warning, so a mismatch below is the mutation's.
        let control, controlLog, controlBinary = compile stage "control" ([ "Loading.clef" ] @ bindings @ [ "Boundary.clef"; "Main.clef" ])
        requireCompiled "control" control controlLog controlBinary
        match mismatchesOf (control.Stdout + control.Stderr) with
        | [] -> ()
        | found -> failwithf "the unmutated witness reports a type mismatch (log %s):\n%s" controlLog (summarized found)
        for mutation in [ wrongReturn; wrongArgument ] do
            let witness = mutation.Witness + ".clef"
            File.WriteAllText(Path.Combine(work, witness), mutated mutation boundary)
            let outcome, log, binary = compile stage mutation.Witness ([ "Loading.clef" ] @ bindings @ [ witness; "Main.clef" ])
            let output = requireRefused mutation.Witness outcome log binary
            let located =
                mismatchesOf output |> List.filter (fun d -> Path.GetFileName d.File = witness && d.Line = listenerLine)
            if located.IsEmpty then
                failwithf "%s: Composer reports no %s at %s:%d (log %s):\n%s" mutation.Witness typeMismatch witness listenerLine log (reportedIn output)
            for diagnostic in located do
                Assert.Contains("Type mismatch", diagnostic.Message))

[<Fact>]
let ``a closure over a run-time local passed to FnPtr.ofFunction is refused by Composer with CCS8096 at the record line`` () =
    withJourney "capturing-closure" (fun work ->
        let stage = stage work
        let bindings = generate stage "probe.pilot.toml" "probe.h" "Bindings" "Landing.Boundary"
        let boundary = File.ReadAllText(Path.Combine(work, "Boundary.clef")).Replace("\r\n", "\n")
        let sourcesWith (witness: string) = [ "Loading.clef" ] @ bindings @ [ witness; "Main.clef" ]
        let refusalsIn (output: string) =
            diagnosticsOf output |> List.filter (fun d -> d.Code = closureConversion || d.Code = listenerRule)
        // Control: in this run the unmutated witness compiles, links, and passes without an error or a warning.
        let control, controlLog, controlBinary = compile stage "control" (sourcesWith "Boundary.clef")
        requireCompiled "control" control controlLog controlBinary
        Assert.Equal(0, control.ExitCode)
        Assert.True(File.Exists controlBinary, $"control: Composer wrote no executable at {controlBinary} (log {controlLog})")
        same ([]: CompilerDiagnostic list) (refusalsIn (control.Stdout + control.Stderr))
        let ranControl, runControlLog = logged work "run-control" (TimeSpan.FromSeconds 15.0) (ProcessLaunch.startInfo controlBinary [])
        if ranControl.ExitCode <> 0 then
            failwithf "control: %s exited with code %d (log %s):\n%s%s" controlBinary ranControl.ExitCode runControlLog ranControl.Stdout ranControl.Stderr
        Assert.Equal("landing: scalar/void callback arguments and results passed", ranControl.Stdout.Trim())
        // Discriminating control: `invokeScalar` binds the run-time result of `invokeVoid ()` as
        // a local and still passes the closed module-level `scalarEntry`. It compiles and links
        // without an error or a warning, and its executable passes: the local binding and the
        // call of `invokeVoid` are accepted.
        let localWitness = localBinding.Witness + ".clef"
        File.WriteAllText(Path.Combine(work, localWitness), mutated localBinding boundary)
        let local, localLog, localBinary = compile stage localBinding.Witness (sourcesWith localWitness)
        requireCompiled localBinding.Witness local localLog localBinary
        Assert.Equal(0, local.ExitCode)
        Assert.True(File.Exists localBinary, $"{localBinding.Witness}: Composer wrote no executable at {localBinary} (log {localLog})")
        same ([]: CompilerDiagnostic list) (refusalsIn (local.Stdout + local.Stderr))
        let ran, runLog = logged work ("run-" + localBinding.Witness) (TimeSpan.FromSeconds 15.0) (ProcessLaunch.startInfo localBinary [])
        if ran.ExitCode <> 0 then
            failwithf "%s: %s exited with code %d (log %s):\n%s%s" localBinding.Witness localBinary ran.ExitCode runLog ran.Stdout ran.Stderr
        Assert.Equal("landing: scalar/void callback arguments and results passed", ran.Stdout.Trim())
        // Mutations: the same local binding, with a closure that reads the local in place of
        // `scalarEntry`. Composer refuses each with exactly one CCS8096 error, at the line of
        // the record expression that holds `FnPtr.ofFunction`, with the message of that closure
        // form, and reports the platform description's callback rule at the same line.
        for closure in [ capturingFunction; capturingLambda ] do
            let name = closure.Mutation.Witness
            let witness = name + ".clef"
            let text = mutated closure.Mutation boundary
            let recordLine = scalarRecordLine closure.Entry text
            File.WriteAllText(Path.Combine(work, witness), text)
            let outcome, log, binary = compile stage name (sourcesWith witness)
            let output = requireRefused name outcome log binary
            let conversions = diagnosticsOf output |> List.filter (fun d -> d.Code = closureConversion)
            match conversions with
            | [ refusal ] ->
                same
                    ("error", closureConversion, witness, recordLine, closure.Message)
                    (refusal.Severity, refusal.Code, Path.GetFileName refusal.File, refusal.Line, refusal.Message)
            | found ->
                failwithf "%s: Composer reports %d %s diagnostics, expected one at %s:%d (log %s):\n%s"
                    name found.Length closureConversion witness recordLine log (reportedIn output)
            let rules =
                diagnosticsOf output
                |> List.filter (fun d ->
                    d.Severity = "error" && d.Code = listenerRule && Path.GetFileName d.File = witness && d.Line = recordLine
                    && d.Message.EndsWith(": " + listenerRuleMessage, StringComparison.Ordinal))
            if rules.Length <> 1 then
                failwithf "%s: Composer reports %d %s errors '%s' at %s:%d, expected one (log %s):\n%s"
                    name rules.Length listenerRule listenerRuleMessage witness recordLine log (reportedIn output)
            // No other line of the witness, and no other file, is reported under either code.
            for reported in refusalsIn output do
                same (witness, recordLine, "error") (Path.GetFileName reported.File, reported.Line, reported.Severity))

[<Fact>]
[<Trait("Category", "Pending213")>]
let ``multi-callback-in-one-caller: landing_invoke and landing_invoke_void inside one run () compile, link and run`` () =
    withJourney "multi-callback-in-one-caller" (fun work ->
        let stage = stage work
        let bindings = generate stage "probe.pilot.toml" "probe.h" "Bindings" "Landing.Boundary"
        let combinedBoundary =
            [ "/// Layer 2 native-entry acceptance; both callbacks invoked in one caller (#213)."
              "/// Awaits Composer EnvironmentArtifacts.recordToken fix (refused on Composer v0.0.2+207e0eec)."
              "module Landing.Acceptance.Boundary"
              ""
              "open Landing.Boundary"
              ""
              "let private scalarEntry (value: int) (delta: int) : int ="
              "    if value = 4294967295 && delta = -173 then -7 else 9"
              ""
              "let private voidEntry (value: int) (delta: int) : unit ="
              "    if value = 4294967295 && delta = -173 then"
              "        landing_record 346"
              "    else"
              "        landing_record (-1)"
              ""
              "let run () : int ="
              "    let scalarResult ="
              "        landing_invoke " + scalarRecord "scalarEntry"
              "    let voidResult ="
              "        landing_invoke_void " + voidRecord
              "    if scalarResult = 0 && voidResult = 0 then 0 else 10"
              "" ]
            |> String.concat "\n"
        File.WriteAllText(Path.Combine(work, "CombinedBoundary.clef"), combinedBoundary)
        accepted stage "multi-callback-in-one-caller" bindings [ "CombinedBoundary.clef"; "Main.clef" ] [ boundaryEntries ]
            "landing: scalar/void callback arguments and results passed"
        |> requireReceipt [ boundaryEntries ] 2)
    |> requireRetained

/// The header of a callback entry Composer does not settle yet: an opaque record handle and
/// the caller's `void *` context (Farscape #189 generates the declarations; #211 tracks their
/// settlement in clef, Fidelity.PSG, Alex and Composer).
let private tokenHeader =
    [ "typedef struct LandingToken LandingToken;"
      "typedef int (*LandingTokenCallback)(LandingToken *token, void *context);"
      "int landing_token_invoke(LandingTokenCallback callback, void *context);"
      "" ]
    |> String.concat "\n"

/// The pilot of `token_probe.h` in `work`: a nonnull callback with a `during_call` invocation
/// contract whose entry argument 1 is the context passed through from native parameter 1.
let private tokenPilot (work: string) : string =
    [ "[library]"
      "name = \"landingprobe\""
      "headers = [" + tomlString (Path.Combine(work, "token_probe.h")) + "]"
      "include_paths = [" + tomlString work + "]"
      "[output]"
      "mode = \"fidelity\""
      "directory = \"generated/TokenBindings\""
      "[options]"
      "c_header_mode = true"
      "descriptor_only_dependencies = true"
      "link_libraries = true"
      "[[options.bindings]]"
      "name = \"landing_token_invoke\""
      "callback_invocations = [{ parameter = 0, identity = \"landingprobe.landing_token_invoke.callback\", version = \"1\", source = \"tests/Farscape.Tests/NativeCallbackRunnerTests.fs\", scope = \"during_call\", context = { argument = 1, parameter = 1 } }]"
      "[annotations.nonnull]"
      "landing_token_invoke = [0]"
      "[[namespace]]"
      "name = \"Landing.Token.Boundary\""
      "description = \"Opaque-handle and context callback entry, Layer 2\""
      "library = \"landingprobe\""
      "functions = [\"landing_token_invoke\"]"
      "" ]
    |> String.concat "\n"

/// A witness that is well typed against the generated declarations of `token_probe.h`.
let private tokenBoundary =
    [ "module Landing.Acceptance.TokenBoundary"
      ""
      "open Landing.Token.Boundary"
      "open Landing.Token.Boundary.Types"
      ""
      "let private tokenEntry (token: option<CHandle<LandingToken>>) (context: option<CHandle<unit>>) : int ="
      "    match token, context with"
      "    | Some _, Some _ -> 0"
      "    | _ -> 1"
      ""
      "let private tokenListener: Callback_Landing_002E_Token_002E_Boundary_003A__003A_LandingTokenCallbackEntry ="
      "    { Invoke = FnPtr.ofFunction tokenEntry }"
      ""
      "let run () : int ="
      "    landing_token_invoke tokenListener None"
      "" ]
    |> String.concat "\n"

let private tokenMain =
    [ "module Landing.Acceptance.Main"
      ""
      "[<EntryPoint>]"
      "let main _ ="
      "    Landing.Acceptance.TokenBoundary.run ()"
      "" ]
    |> String.concat "\n"

/// The codes with which CCS refuses a callback invocation contract it does not settle.
let private unsettledContract = [ "CCS8403"; "CCS8413" ]

[<Fact>]
let ``unsettled opaque-handle and context callback refusal: Farscape 189 generates the entry and Composer refuses it until Farscape 211 settles it`` () =
    withJourney "unsettled-entry" (fun work ->
        let stage = stage work
        File.WriteAllText(Path.Combine(work, "token_probe.h"), tokenHeader)
        let bindings = generateFrom stage "TokenBindings" (tokenPilot work)
        // Farscape #189: the entry, its invocation contract and its context descriptor are
        // generated, so the refusal below is of a declared contract, not of a missing one.
        let generated = File.ReadAllText(Path.Combine(work, "generated", "TokenBindings", "Boundary", "Boundary.clef"))
        let declarations =
            [ "Invoke: FnPtr<option<CHandle<LandingToken>> -> option<CHandle<unit>> -> int>"
              "let landing_token_invokeNativeCallback0InvocationContract : Expr<CallbackInvocationContract> ="
              "let landing_token_invokeNativeCallback0Context : Expr<CallbackContextDescriptor> ="
              "Argument = 1"
              "ContextParameter = 1"
              "let landing_token_invoke (callback: Callback_Landing_002E_Token_002E_Boundary_003A__003A_LandingTokenCallbackEntry) (context: option<CHandle<unit>>) : int =" ]
        for declared in declarations do
            Assert.Contains(declared, generated)
        File.WriteAllText(Path.Combine(work, "TokenBoundary.clef"), tokenBoundary)
        File.WriteAllText(Path.Combine(work, "TokenMain.clef"), tokenMain)
        // Farscape #211: clef settles during_call entries over scalar integers only.
        let outcome, log, binary = compile stage "unsettled-entry" ([ "Loading.clef" ] @ bindings @ [ "TokenBoundary.clef"; "TokenMain.clef" ])
        let output = requireRefused "unsettled-entry" outcome log binary
        let codes = compilerCodes output
        if not (codes |> List.exists (fun code -> List.contains code unsettledContract)) then
            failwithf "unsettled-entry: Composer reports neither %s (log %s); it reports %A:\n%s"
                (String.concat " nor " unsettledContract) log codes (reportedIn output))

// ─── Repository hygiene ───

/// The directories a Python file may not be in: the source trees, walked without their build
/// output. The root's own files are checked without descending into its other directories
/// (`artifacts` is ignored evidence output).
let private sourceTrees = [ "src"; "tests"; "scripts" ]
let private excludedDirectories = [ ".git"; "bin"; "obj"; "artifacts" ]

let rec private pythonFilesUnder (directory: string) : string list =
    [ yield! Directory.EnumerateFiles(directory, "*.py")
      for child in Directory.EnumerateDirectories directory do
          let isLink = File.GetAttributes(child).HasFlag FileAttributes.ReparsePoint
          if not isLink && not (List.contains (Path.GetFileName child) excludedDirectories) then
              yield! pythonFilesUnder child ]

[<Fact>]
let ``the repository tracks no Python file and its source trees hold none`` () =
    let git =
        match onPath "git" with
        | Some git -> git
        | None -> failwith "git is required on PATH to list the tracked files"
    let listed = run repositoryRoot (TimeSpan.FromSeconds 60.0) (ProcessLaunch.startInfo git [ "ls-files"; "-z"; "--"; "*.py" ])
    if listed.ExitCode <> 0 then
        failwithf "%s exited with code %d in %s: %s" listed.Command listed.ExitCode repositoryRoot listed.Stderr
    // `git ls-files` reads the index, which lists a file deleted from the working tree until
    // the deletion is committed. A listed path that no longer exists is such a deletion.
    let tracked =
        listed.Stdout.Split('\000', StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter (fun relative -> File.Exists(Path.Combine(repositoryRoot, relative)))
        |> List.ofArray
    same ([]: string list) tracked
    let held =
        [ yield! Directory.EnumerateFiles(repositoryRoot, "*.py")
          for tree in sourceTrees do
              let directory = Path.Combine(repositoryRoot, tree)
              if Directory.Exists directory then yield! pythonFilesUnder directory ]
        |> List.map (fun path -> Path.GetRelativePath(repositoryRoot, path))
    same ([]: string list) held
