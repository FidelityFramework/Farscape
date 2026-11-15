namespace Farscape.Core

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Fidelity.Data.JSON

/// The retained Composer proof protocol consumed by library substantiation (#208).
/// This checks published obligations; it neither creates nor settles compiler facts.
module SubstantiationProof =

    type SolverIdentity =
        { Executable: string
          Sha256: string
          Version: string
          Arguments: string array }

    type StageReceiptDigest = { Path: string; Sha256: string }

    type ProofSummary =
        { LibraryObligations: int
          LibraryDischargedUnsatCount: int
          LibraryPremisesSatCount: int
          HarnessObligations: int
          TotalObligations: int
          DischargedUnsatCount: int
          PendingOpenCount: int
          RefutedCount: int
          PremisesSatCount: int
          ObligationsSha256: string
          QuerySha256: string
          ReceiptSha256: string
          PremisesQuerySha256: string
          PremisesOutputSha256: string
          CompilerPremiseReceiptsCount: int
          CompilerNonVacuityReceiptsCount: int
          StageReceipts: StageReceiptDigest array
          Solver: SolverIdentity }

    let private hashText (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString

    let private hashFile (path: string) =
        use stream = File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexString

    let private isDigest (value: string) =
        not (isNull value) && value.Length = 64 && Seq.forall Char.IsAsciiHexDigit value

    let private sameDigest (left: string) (right: string) =
        isDigest left && isDigest right && String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private fail code location message =
        Diagnostics.diagnostic code message (Diagnostics.inFile location "retained proof evidence")
        |> Diagnostics.refusalOf
        |> Diagnostics.raiseRefusal

    let private protect code location operation =
        try Ok(operation ())
        with
        | :? Diagnostics.RefusalException as error -> Error error.Refusal
        | error ->
            Diagnostics.diagnostic code error.Message (Diagnostics.inFile location "retained proof evidence")
            |> Diagnostics.refusalOf
            |> Error

    let private unwrap = function
        | Ok value -> value
        | Error refusal -> Diagnostics.raiseRefusal refusal

    type private SExpression =
        | Atom of string
        | Form of SExpression list

    type private Command = { Expression: SExpression; Start: int; Finish: int }

    /// Parse SMT-LIB command boundaries, including comments and quoted symbols.
    /// Keeping physical spans lets premise extraction remove only the one assertion.
    let private commands (query: string) =
        let location = "06b_obligations.smt2"
        let malformed message = fail Diagnostics.SubstantiationPremisesMalformed location message
        let mutable index = 0
        let rec whitespace () =
            while index < query.Length && Char.IsWhiteSpace query[index] do index <- index + 1
            if index < query.Length && query[index] = ';' then
                while index < query.Length && query[index] <> '\n' do index <- index + 1
                whitespace ()
        let quoted terminator =
            let start = index
            index <- index + 1
            let mutable closed = false
            while index < query.Length && not closed do
                if query[index] = terminator then
                    if terminator = '"' && index + 1 < query.Length && query[index + 1] = '"' then
                        index <- index + 2
                    else
                        index <- index + 1
                        closed <- true
                else index <- index + 1
            if not closed then malformed $"Unterminated SMT-LIB quoted token at character {start}."
            query.Substring(start, index - start)
        let rec expression depth =
            if depth > 512 then malformed "SMT-LIB nesting exceeds the supported proof protocol depth."
            whitespace ()
            if index >= query.Length then malformed "An SMT-LIB expression is incomplete."
            match query[index] with
            | '(' ->
                index <- index + 1
                let values = ResizeArray<SExpression>()
                whitespace ()
                while index < query.Length && query[index] <> ')' do
                    values.Add(expression (depth + 1))
                    whitespace ()
                if index >= query.Length then malformed "An SMT-LIB form is not closed."
                index <- index + 1
                Form(List.ofSeq values)
            | ')' -> malformed $"Unexpected ')' at character {index}."
            | '|' -> Atom(quoted '|')
            | '"' -> Atom(quoted '"')
            | _ ->
                let start = index
                while index < query.Length && not (Char.IsWhiteSpace query[index])
                      && query[index] <> '(' && query[index] <> ')' && query[index] <> ';' do
                    index <- index + 1
                Atom(query.Substring(start, index - start))
        let result = ResizeArray<Command>()
        whitespace ()
        while index < query.Length do
            let start = index
            let value = expression 0
            match value with
            | Form(Atom _ :: _) -> result.Add { Expression = value; Start = start; Finish = index }
            | _ -> malformed $"Expected an SMT-LIB command at character {start}."
            whitespace ()
        List.ofSeq result

    let private symbol = function
        | Atom value when value.StartsWith("|", StringComparison.Ordinal) && value.EndsWith("|", StringComparison.Ordinal) ->
            value.Substring(1, value.Length - 2)
        | Atom value -> value
        | _ -> ""

    /// Each Composer block is independent, ends in check-sat/reset, and declares
    /// one Boolean obligation anchor. Remove exactly its negated conclusion.
    /// All premises and the anchor/conclusion equation remain byte-for-byte intact.
    let extractPremises (ids: string list) (query: string) : Result<string, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationPremisesMalformed "06b_obligations.smt2" (fun () ->
            let malformed message = fail Diagnostics.SubstantiationPremisesMalformed "06b_obligations.smt2" message
            if ids.IsEmpty || (ids |> List.exists String.IsNullOrWhiteSpace) || Set.count (Set.ofList ids) <> ids.Length then
                malformed "The query requires a nonempty, unique obligation inventory."
            let removed = ResizeArray<Command>()
            let block = ResizeArray<Command>()
            let mutable ordinal = 0
            let checkBlock () =
                if ordinal >= ids.Length then malformed "The query has more blocks than retained obligations."
                let anchor = ids[ordinal]
                let declarations =
                    block |> Seq.filter (fun command ->
                        match command.Expression with
                        | Form [ Atom "declare-fun"; name; Form []; Atom "Bool" ]
                        | Form [ Atom "declare-const"; name; Atom "Bool" ] -> symbol name = anchor
                        | _ -> false) |> Seq.length
                if declarations <> 1 then malformed $"Obligation '{anchor}' does not have exactly one Boolean declaration in its block."
                let negations =
                    block |> Seq.filter (fun command ->
                        match command.Expression with
                        | Form [ Atom "assert"; Form [ Atom "not"; name ] ] -> symbol name = anchor
                        | _ -> false) |> Seq.toArray
                if negations.Length <> 1 then malformed $"Obligation '{anchor}' does not have exactly one negated-conclusion assertion."
                for command in block do
                    match command.Expression with
                    | Form(Atom ("set-logic" | "set-option" | "declare-fun" | "declare-const" | "define-fun" | "assert") :: _) -> ()
                    | _ -> malformed $"Unsupported command in obligation '{anchor}'; premise extraction refuses ambiguous solver state."
                let foreignAnchor =
                    block |> Seq.tryPick (fun command ->
                        match command.Expression with
                        | Form(Atom ("declare-fun" | "declare-const") :: name :: _) ->
                            let name = symbol name
                            if name <> anchor && List.contains name ids then Some name else None
                        | _ -> None)
                match foreignAnchor with
                | Some name -> malformed $"Obligation '{anchor}' contains another obligation anchor '{name}'."
                | None -> ()
                removed.Add negations[0]
                block.Clear()
                ordinal <- ordinal + 1
            let mutable needsReset = false
            for command in commands query do
                match command.Expression with
                | Form [ Atom "check-sat" ] when not needsReset ->
                    checkBlock ()
                    needsReset <- true
                | Form [ Atom "reset" ] when needsReset -> needsReset <- false
                | _ when needsReset -> malformed "Every obligation's check-sat must be followed by reset before another command."
                | Form [ Atom "reset" ] -> malformed "A reset occurs without a completed obligation block."
                | _ -> block.Add command
            if ordinal <> ids.Length || block.Count <> 0 then
                malformed $"Query block count {ordinal} does not match the {ids.Length} retained obligations."
            let builder = StringBuilder()
            let mutable position = 0
            for command in removed do
                builder.Append(query, position, command.Start - position) |> ignore
                position <- command.Finish
            builder.Append(query, position, query.Length - position).ToString())

    let private parseJson location (json: string) =
        match Json.parse json with
        | Ok value -> value
        | Error reason -> fail Diagnostics.SubstantiationProofInvalid location reason

    let private property location name (owner: JsonValue) =
        match owner with
        | JsonValue.Object properties ->
            match properties |> List.tryFind (fun (key, _) -> key = name) with
            | Some (_, value) -> value
            | None -> fail Diagnostics.SubstantiationProofInvalid location $"Required property '{name}' is absent."
        | _ -> fail Diagnostics.SubstantiationProofInvalid location $"'{name}' requires a JSON object."

    let private text location name owner =
        match property location name owner with
        | JsonValue.String value -> value
        | _ -> fail Diagnostics.SubstantiationProofInvalid location $"'{name}' must be a string."

    let private array location name owner =
        match property location name owner with
        | JsonValue.Array values -> Array.ofList values
        | _ -> fail Diagnostics.SubstantiationProofInvalid location $"'{name}' must be an array."

    let rec private uniqueProperties location (element: JsonValue) =
        match element with
        | JsonValue.Object properties ->
            if (properties |> List.map fst |> Set.ofList |> Set.count) <> properties.Length then
                fail Diagnostics.SubstantiationProofInvalid location "Duplicate JSON property names are not permitted."
            properties |> List.iter (fun (_, value) -> uniqueProperties location value)
        | JsonValue.Array values -> values |> List.iter (uniqueProperties location)
        | _ -> ()

    let private obligationRows (json: string) =
        let root = parseJson "06a_obligations.json" json
        uniqueProperties "06a_obligations.json" root
        let rows =
            array "06a_obligations.json" "obligations" root
            |> Array.map (fun row -> text "06a_obligations.json" "id" row, text "06a_obligations.json" "source" row)
        let ids = Array.map fst rows
        if ids.Length = 0 || (ids |> Array.exists String.IsNullOrWhiteSpace) || (ids |> Set.ofArray |> Set.count) <> ids.Length then
            fail Diagnostics.SubstantiationProofInvalid "06a_obligations.json" "Obligations must have a nonempty, unique anchor inventory."
        if rows |> Array.exists (snd >> String.IsNullOrWhiteSpace) then
            fail Diagnostics.SubstantiationProofInvalid "06a_obligations.json" "Every obligation must retain its source identity."
        rows

    let private obligationIds json = obligationRows json |> Array.map fst

    /// The retained obligation inventory as (anchor, source, kind) for the dossier (#209).
    /// The kind is empty when the inventory row carries none.
    let internal obligationInventory (json: string) : Result<(string * string * string) array, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationProofInvalid "06a_obligations.json" (fun () ->
            let rows = obligationRows json
            let root = parseJson "06a_obligations.json" json
            let kinds =
                array "06a_obligations.json" "obligations" root
                |> Array.map (fun row ->
                    match Json.prop "kind" row with
                    | Some (JsonValue.String kind) -> kind
                    | _ -> "")
            Array.map2 (fun (anchor, source) kind -> anchor, source, kind) rows kinds)

    let private verdicts (output: string) =
        output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim()

    type private ObligationOrigin = Library | Harness | Dependency

    let private origins (sourcePaths: string array) (harnessPath: string) (obligations: (string * string) array) =
        let canonical location (path: string) =
            if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
                fail Diagnostics.SubstantiationProofInvalid location "Obligation attribution requires absolute source paths."
            Path.GetFullPath path
        let libraryPaths = sourcePaths |> Array.map (canonical "build.sources") |> Set.ofArray
        let harness = canonical "SubstantiationRoot.clef" harnessPath
        if libraryPaths.Contains harness then
            fail Diagnostics.SubstantiationProofInvalid harness "The generated harness cannot also be a library source."
        obligations
        |> Array.map (fun (anchor, source) ->
            let location = Regex.Match(source, @"^(?<path>.+):[0-9]+:[0-9]+$")
            if not location.Success then
                fail Diagnostics.SubstantiationProofInvalid source "An obligation source must retain its file:line:column location."
            let path = canonical source location.Groups["path"].Value
            anchor, if libraryPaths.Contains path then Library elif path = harness then Harness else Dependency)
        |> Map.ofArray

    let private invocation location root =
        match Guid.TryParse(text location "Invocation" root) with
        | true, value when value <> Guid.Empty -> value
        | _ -> fail Diagnostics.SubstantiationProofInvalid location "The receipt lacks a nonempty compiler invocation identity."

    let private solverIdentity location root =
        let invalid message = fail Diagnostics.SubstantiationProofInvalid location message
        let tools = array location "Tools" root
        if tools.Length <> 1 then invalid "The source receipt must name exactly one cvc5 solver."
        let tool = tools[0]
        let arguments =
            array location "Arguments" tool |> Array.map (fun argument ->
                match argument with
                | JsonValue.String value -> value
                | _ -> invalid "Solver arguments must be strings.")
        let solver =
            { Executable = text location "Executable" tool
              Sha256 = text location "Sha256" tool
              Version = text location "Version" tool
              Arguments = arguments }
        if not (Path.IsPathFullyQualified solver.Executable) || not (Path.GetFileName(solver.Executable).StartsWith("cvc5", StringComparison.Ordinal))
           || not (isDigest solver.Sha256) || String.IsNullOrWhiteSpace solver.Version
           || not (solver.Version.Contains("cvc5", StringComparison.OrdinalIgnoreCase))
           || solver.Arguments <> [| "--lang=smt2"; "--tlimit=25000" |] then
            invalid "The receipt does not bind the required cvc5 executable, SHA-256, version and arguments."
        solver

    /// Inspect the compiler receipt without running a solver. PremisesSatCount is
    /// zero until evaluate supplies the independent SAT(H) evidence.
    let inspectReceipt (sourcePaths: string array) (harnessPath: string) (obligationsJson: string) (query: string) (receiptJson: string) : Result<ProofSummary, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationProofInvalid "06c_psg_discharge.json" (fun () ->
            let location = "06c_psg_discharge.json"
            let invalid message = fail Diagnostics.SubstantiationProofInvalid location message
            let obligations = obligationRows obligationsJson
            let attribution = origins sourcePaths harnessPath obligations
            let ids = Array.map fst obligations
            let premises = extractPremises (List.ofArray ids) query |> unwrap
            let root = parseJson location receiptJson
            uniqueProperties location root
            if text location "Stage" root <> "psg" then invalid "The receipt is not the source PSG discharge stage."
            invocation location root |> ignore
            let queryHash = hashText query
            // Composer's source protocol binds both input and query to 06b;
            // 06a has its own manifest digest and exact anchor correspondence.
            for field in [ "InputSha256"; "QuerySha256" ] do
                if not (sameDigest (text location field root) queryHash) then invalid $"{field} does not match retained 06b_obligations.smt2."
            let solver = solverIdentity location root
            let outcomes = array location "Outcomes" root
            let anchors = outcomes |> Array.map (text location "Anchor")
            if anchors.Length <> ids.Length || Set.ofArray anchors <> Set.ofArray ids || (anchors |> Set.ofArray |> Set.count) <> anchors.Length then
                invalid "Discharge outcomes do not correspond one-to-one with retained obligations."
            let sources = Map.ofArray obligations
            for outcome in outcomes do
                let anchor = text location "Anchor" outcome
                if text location "Source" outcome <> sources[anchor] then
                    invalid $"Outcome '{anchor}' does not retain its obligation's source identity."
            let results = outcomes |> Array.map (text location "Verdict")
            let pending = results |> Array.filter (fun verdict -> verdict <> "unsat" && verdict <> "sat") |> Array.length
            let refuted = results |> Array.filter ((=) "sat") |> Array.length
            let discharged = results |> Array.filter ((=) "unsat") |> Array.length
            if pending <> 0 || refuted <> 0 then
                fail Diagnostics.SubstantiationUndischarged location $"Required obligations remain open ({pending}) or refuted ({refuted}); every result must be unsat."
            let stdout = text location "StandardOutput" root |> verdicts
            if stdout <> results then invalid "The retained solver output disagrees with the receipt outcomes."
            if not (String.IsNullOrWhiteSpace(text location "StandardError" root)) then invalid "The source solver reported diagnostics on stderr."
            let count origin = attribution |> Map.values |> Seq.filter ((=) origin) |> Seq.length
            let libraryDischarged =
                outcomes |> Array.filter (fun outcome -> attribution[text location "Anchor" outcome] = Library && text location "Verdict" outcome = "unsat") |> Array.length
            { LibraryObligations = count Library; LibraryDischargedUnsatCount = libraryDischarged; LibraryPremisesSatCount = 0
              HarnessObligations = count Harness
              TotalObligations = ids.Length; DischargedUnsatCount = discharged; PendingOpenCount = pending; RefutedCount = refuted
              PremisesSatCount = 0; ObligationsSha256 = hashText obligationsJson; QuerySha256 = queryHash; ReceiptSha256 = hashText receiptJson
              PremisesQuerySha256 = hashText premises; PremisesOutputSha256 = hashText ""
              CompilerPremiseReceiptsCount = 0; CompilerNonVacuityReceiptsCount = 0; StageReceipts = [||]; Solver = solver })

    /// Bind every compiler-owned conditional query and its retained receipt/output
    /// to the same source proof invocation and cvc5 identity as the discharge.
    let private inspectStages artifactsDirectory obligationsJson receiptJson solver =
        let dischargeRoot = parseJson "06c_psg_discharge.json" (receiptJson: string)
        let expectedInvocation = invocation "06c_psg_discharge.json" dischargeRoot
        let obligations = obligationRows obligationsJson |> Map.ofArray
        let files =
            [| "06c_psg_premises_*"; "06c_psg_nonvacuity_*" |]
            |> Array.collect (fun pattern -> Directory.GetFiles(artifactsDirectory, pattern))
            |> Array.map Path.GetFullPath
            |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        let receipts = files |> Array.filter (fun path -> Path.GetExtension path = ".json")
        let expectedFiles =
            receipts |> Array.collect (fun path -> [| ".json"; ".smt2"; ".stdout"; ".stderr" |] |> Array.map (fun extension -> Path.ChangeExtension(path, extension)))
            |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        if files <> expectedFiles then
            fail Diagnostics.SubstantiationProofInvalid artifactsDirectory "Every conditional proof receipt requires exactly its retained JSON, SMT query, stdout and stderr files; orphaned or missing files are refused."
        let mutable premiseCount = 0
        let mutable nonVacuityCount = 0
        for path in receipts do
            let invalid message = fail Diagnostics.SubstantiationProofInvalid path message
            let name = Regex.Match(Path.GetFileNameWithoutExtension path, @"^06c_psg_(premises|nonvacuity)_[0-9]+$")
            if not name.Success then invalid "Unexpected conditional proof receipt filename."
            let stage = "psg-" + name.Groups[1].Value
            let json = File.ReadAllText path
            let root = parseJson path json
            uniqueProperties path root
            if text path "Stage" root <> stage then invalid "The receipt's stage does not match its retained filename."
            if invocation path root <> expectedInvocation then invalid "The conditional receipt belongs to a different compiler invocation."
            if solverIdentity path root <> solver then invalid "The conditional receipt does not use the discharge receipt's exact cvc5 identity."
            let queryPath = Path.ChangeExtension(path, ".smt2")
            let queryHash = hashFile queryPath
            for field in [ "InputSha256"; "QuerySha256" ] do
                if not (sameDigest (text path field root) queryHash) then invalid $"{field} does not match the retained conditional SMT query."
            let outcomes = array path "Outcomes" root
            let anchors = outcomes |> Array.map (text path "Anchor")
            if outcomes.Length = 0 || Set.count (Set.ofArray anchors) <> anchors.Length then
                invalid "A conditional receipt must retain nonempty, unique obligation outcomes."
            for outcome in outcomes do
                let anchor = text path "Anchor" outcome
                match obligations.TryFind anchor with
                | Some source when text path "Source" outcome = source -> ()
                | _ -> invalid $"Conditional outcome '{anchor}' is absent from the retained obligation inventory or has a different source."
                if text path "Verdict" outcome <> "sat" then invalid $"Conditional outcome '{anchor}' is not sat."
            let output = text path "StandardOutput" root
            let errors = text path "StandardError" root
            if verdicts output <> Array.create outcomes.Length "sat" || errors <> "" then
                invalid "Conditional solver output must contain exactly the recorded sat outcomes and empty stderr."
            if File.ReadAllText(Path.ChangeExtension(path, ".stdout")) <> output
               || File.ReadAllText(Path.ChangeExtension(path, ".stderr")) <> errors then
                invalid "The retained conditional solver output differs from its receipt."
            if not (sameDigest (hashFile path) (hashText json)) then invalid "The parsed receipt does not match its retained UTF-8 bytes."
            if stage = "psg-premises" then premiseCount <- premiseCount + 1
            else nonVacuityCount <- nonVacuityCount + 1
        premiseCount, nonVacuityCount, (files |> Array.map (fun path -> { Path = path; Sha256 = hashFile path }))

    let private checkSolver (solver: SolverIdentity) =
        if not (File.Exists solver.Executable) || not (sameDigest (hashFile solver.Executable) solver.Sha256) then
            fail Diagnostics.SubstantiationToolFailure solver.Executable "The receipt's cvc5 executable is absent or its SHA-256 has changed."

    let private checkPremisesOutput count (output: string) =
        let results = verdicts output
        if results.Length <> count || (results |> Array.exists ((<>) "sat")) then
            let received = String.concat ", " results
            fail Diagnostics.SubstantiationPremisesUnsat "06b_premises_sat.smt2"
                $"Every obligation premise H must be SAT: expected {count} sat outcomes; received [{received}]. Unsat means vacuous premises; unknown and timeout are refusals."

    let private runPremises (solver: SolverIdentity) path =
        checkSolver solver
        let start = ProcessStartInfo(solver.Executable)
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.WorkingDirectory <- Path.GetDirectoryName(Path.GetFullPath path)
        for variable in [ "TMPDIR"; "TMP"; "TEMP" ] do start.Environment[variable] <- start.WorkingDirectory
        for argument in solver.Arguments do start.ArgumentList.Add argument
        start.ArgumentList.Add(Path.GetFullPath path)
        use proc = new Process(StartInfo = start)
        if not (proc.Start()) then fail Diagnostics.SubstantiationToolFailure solver.Executable "cvc5 did not start."
        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        if not (proc.WaitForExit 30000) then
            proc.Kill(true)
            proc.WaitForExit()
            stdout.GetAwaiter().GetResult() |> ignore
            stderr.GetAwaiter().GetResult() |> ignore
            fail Diagnostics.SubstantiationPremisesUnsat path "cvc5 SAT(H) checking timed out; no premise is accepted."
        let output, errors = stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()
        File.WriteAllText(Path.ChangeExtension(path, ".stdout"), output)
        File.WriteAllText(Path.ChangeExtension(path, ".stderr"), errors)
        checkSolver solver
        if verdicts output |> Array.exists (fun value -> value = "unknown" || value = "timeout") then
            fail Diagnostics.SubstantiationPremisesUnsat path "cvc5 could not decide SAT(H): unknown and timeout outcomes are refusals."
        if proc.ExitCode <> 0 || not (String.IsNullOrWhiteSpace errors) then
            fail Diagnostics.SubstantiationToolFailure path $"cvc5 exited {proc.ExitCode}: {errors}{output}"
        output

    /// Evaluate precisely the retained source evidence and independently check SAT(H).
    /// The explicit artifacts directory owns every newly written proof file.
    let evaluate (sourcePaths: string array) (harnessPath: string) (artifactsDirectory: string) : Result<ProofSummary, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationProofInvalid artifactsDirectory (fun () ->
            let read name = File.ReadAllText(Path.Combine(artifactsDirectory, name))
            let obligations, query, receipt = read "06a_obligations.json", read "06b_obligations.smt2", read "06c_psg_discharge.json"
            let summary = inspectReceipt sourcePaths harnessPath obligations query receipt |> unwrap
            let premiseCount, nonVacuityCount, stages = inspectStages artifactsDirectory obligations receipt summary.Solver
            for name, digest in [ "06a_obligations.json", summary.ObligationsSha256; "06b_obligations.smt2", summary.QuerySha256; "06c_psg_discharge.json", summary.ReceiptSha256 ] do
                if not (sameDigest (hashFile (Path.Combine(artifactsDirectory, name))) digest) then
                    fail Diagnostics.SubstantiationProofInvalid name "The retained file bytes do not match the parsed UTF-8 proof evidence."
            let premises = extractPremises (obligationIds obligations |> List.ofArray) query |> unwrap
            let path = Path.Combine(artifactsDirectory, "06b_premises_sat.smt2")
            File.WriteAllText(path, premises, UTF8Encoding(false))
            let output =
                protect Diagnostics.SubstantiationToolFailure summary.Solver.Executable (fun () -> runPremises summary.Solver path)
                |> unwrap
            checkPremisesOutput summary.TotalObligations output
            for name, digest in [ "06a_obligations.json", summary.ObligationsSha256; "06b_obligations.smt2", summary.QuerySha256; "06c_psg_discharge.json", summary.ReceiptSha256; "06b_premises_sat.smt2", summary.PremisesQuerySha256 ] do
                if not (sameDigest (hashFile (Path.Combine(artifactsDirectory, name))) digest) then
                    fail Diagnostics.SubstantiationProofInvalid name "Retained proof evidence changed during SAT(H) checking."
            if inspectStages artifactsDirectory obligations receipt summary.Solver <> (premiseCount, nonVacuityCount, stages) then
                fail Diagnostics.SubstantiationProofInvalid artifactsDirectory "Retained conditional proof evidence changed during SAT(H) checking."
            { summary with
                PremisesSatCount = summary.TotalObligations; LibraryPremisesSatCount = summary.LibraryObligations
                PremisesOutputSha256 = hashText output
                CompilerPremiseReceiptsCount = premiseCount; CompilerNonVacuityReceiptsCount = nonVacuityCount; StageReceipts = stages })

    /// Revalidate stored evidence without substituting a new proof run for it.
    let validateStoredEvidence (sourcePaths: string array) (harnessPath: string) (summary: ProofSummary) (artifactsDirectory: string) : Result<unit, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationProofInvalid artifactsDirectory (fun () ->
            let read name = File.ReadAllText(Path.Combine(artifactsDirectory, name))
            let invalid name = fail Diagnostics.SubstantiationProofInvalid (Path.Combine(artifactsDirectory, name)) "The retained proof artifact or its manifest digest has changed."
            let hashes =
                [ "06a_obligations.json", summary.ObligationsSha256; "06b_obligations.smt2", summary.QuerySha256
                  "06c_psg_discharge.json", summary.ReceiptSha256; "06b_premises_sat.smt2", summary.PremisesQuerySha256
                  "06b_premises_sat.stdout", summary.PremisesOutputSha256 ]
            for name, digest in hashes do
                let path = Path.Combine(artifactsDirectory, name)
                if not (File.Exists path) || not (sameDigest (hashFile path) digest) then invalid name
            let obligations, receipt = read "06a_obligations.json", read "06c_psg_discharge.json"
            let parsed = inspectReceipt sourcePaths harnessPath obligations (read "06b_obligations.smt2") receipt |> unwrap
            let normalized =
                { summary with PremisesSatCount = 0; LibraryPremisesSatCount = 0; PremisesOutputSha256 = hashText ""
                               CompilerPremiseReceiptsCount = 0; CompilerNonVacuityReceiptsCount = 0; StageReceipts = [||] }
            if parsed <> normalized || summary.PremisesSatCount <> summary.TotalObligations
               || summary.LibraryPremisesSatCount <> summary.LibraryObligations then invalid "06c_psg_discharge.json"
            let stages = inspectStages artifactsDirectory obligations receipt summary.Solver
            if stages <> (summary.CompilerPremiseReceiptsCount, summary.CompilerNonVacuityReceiptsCount, summary.StageReceipts) then
                invalid "06c_psg_premises_*/06c_psg_nonvacuity_*"
            checkPremisesOutput summary.TotalObligations (read "06b_premises_sat.stdout")
            if not (String.IsNullOrWhiteSpace(read "06b_premises_sat.stderr")) then invalid "06b_premises_sat.stderr"
            checkSolver summary.Solver)
