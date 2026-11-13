namespace Farscape.Core

open System
open System.IO
open System.Text.RegularExpressions
open Fidelity.Data.TOML

/// The foreign assumptions and finite seam categories of a substantiated library (#208).
/// ArchiveInspection and CppParser remain the owners of archive and layout facts.
module SubstantiationBoundary =
    [<CLIMutable>]
    type FileDigest = { Path: string; Sha256: string; Kind: string }

    [<CLIMutable>]
    type Hypothesis = { Id: string; Family: string; Origin: string; Statement: string }

    [<CLIMutable>]
    type LayoutSummary =
        { Header: string; Name: string; Kind: string; SizeBits: int; AlignmentBits: int
          FieldOffsetsBits: int[] }

    /// CppParser measures enum width and signedness; this record makes no alignment claim.
    [<CLIMutable>]
    type EnumSummary = { Header: string; Name: string; WidthBits: int; Signed: bool }

    [<CLIMutable>]
    type BoundaryStrata =
        { ForeignCalls: bool; MeasuredRecords: bool; StaticArchive: bool
          ErrorConventions: bool; Ownership: bool; ZeroCopy: bool; Callbacks: bool; Nullability: bool }

    [<CLIMutable>]
    type BoundaryEvidence =
        { Strata: BoundaryStrata; Files: FileDigest[]; LinkMode: string; Libraries: string[]; ExternSymbols: string[]
          ClangIdentity: string; ClangTarget: string; TargetDataModel: string; Layouts: LayoutSummary[]; Enums: EnumSummary[]
          RestsOn: Hypothesis[]; RequiredSeamChecks: string[] }

    type Request =
        { FidprojPath: string; PackageName: string; LinkLibraries: string[]; LinkObjects: string[]
          PilotPath: string option; LinkProvenancePath: string option; Sources: (string * string)[]
          WorkDirectory: string }

    let private fail code path message =
        Diagnostics.diagnostic code message (Diagnostics.inFile path "substantiation boundary input")
        |> Diagnostics.refuse

    let private linkFailure path message = fail Diagnostics.SubstantiationLinkInvalid path message
    let private layoutFailure path message = fail Diagnostics.SubstantiationLayoutInvalid path message

    let private digest kind path : FileDigest =
        { Path = Path.GetFullPath path; Sha256 = ClangToolchain.sha256 path; Kind = kind }

    let private sameHash (left: string) (right: string) = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    /// One check category per used stratum, independent of the number of exported functions.
    let deriveContract (origin: string) (strata: BoundaryStrata) : Hypothesis[] * string[] =
        let hypotheses = [|
            if strata.ForeignCalls || strata.MeasuredRecords then
                yield { Id = "H-ABI-1"; Family = "H-ABI"; Origin = origin
                        Statement = "The foreign binary agrees with the probed headers on scalar widths, record layout and alignment, enum width, and calling convention." }
            if strata.ForeignCalls then
                yield { Id = "H-Link-1"; Family = "H-Link"; Origin = origin
                        Statement = if strata.StaticArchive then "The recorded static archive SHA-256 and member symbol bindings identify the binary linked by the consumer."
                                    else "The runtime shared-object identifiers resolve to the intended SONAMEs under the consumer's ProcessLinkContract." }
            if strata.ErrorConventions || strata.Ownership || strata.ZeroCopy || strata.Nullability then
                yield { Id = "H-Contract-1"; Family = "H-Contract"; Origin = origin
                        Statement = "The foreign implementation honors the declared error sentinels, ownership and release rules, nullability, and BAREWire region/view extent and lifetime contracts present in this boundary." }
            if strata.Callbacks then
                yield { Id = "H-Callback-1"; Family = "H-Callback"; Origin = origin
                        Statement = "The foreign implementation invokes callbacks with the declared function-pointer ABI, context, registration lifetime and invocation scope." }
        |]
        let checks = [|
            if strata.ForeignCalls then "scalar-handle-link-and-call"
            if strata.MeasuredRecords then "measured-record-abi"
            if strata.StaticArchive then "static-dynamic-link-elision"
            if strata.ErrorConventions then "error-sentinel-conformance"
            if strata.Ownership then "owned-handle-lifecycle"
            if strata.ZeroCopy then "barewire-zero-copy"
            if strata.Callbacks then "callback-registration-invocation"
            "falsification-refusal-controls"
        |]
        hypotheses, checks

    let private parseDocument code path =
        if not (File.Exists path) then fail code path $"Required file '{path}' does not exist."
        match Toml.parse (File.ReadAllText path) with
        | Ok doc -> doc
        | Error error -> fail code path $"Invalid TOML: {error}"

    let private text path key table =
        match TomlTable.tryFind key table with
        | Some (TomlValue.String value) when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> linkFailure path $"Link provenance requires a nonempty string '{key}'."

    let private integer path key table =
        match TomlTable.tryFind key table with
        | Some (TomlValue.Integer value) -> value
        | _ -> linkFailure path $"Link provenance requires integer '{key}'."

    let private strings path key table =
        match TomlTable.tryFind key table with
        | Some (TomlValue.Array values) ->
            values |> List.map (function TomlValue.String value -> value | _ -> linkFailure path $"'{key}' must contain only strings.")
        | _ -> linkFailure path $"Link provenance requires string array '{key}'."

    let private tables path key table =
        match TomlTable.tryFind key table with
        | Some (TomlValue.Array values) ->
            values |> List.map (function TomlValue.Table value -> value | _ -> linkFailure path $"'{key}' must contain only tables.")
        | _ -> linkFailure path $"Link provenance requires table array '{key}'."

    let private absolute (directory: string) (path: string) =
        ScratchDirectory.canonicalEvidencePath (if Path.IsPathRooted path then path else Path.Combine(directory, path))

    let private identifyClangTarget (workDirectory: string) (pilotPath: string) =
        let work = ScratchDirectory.canonicalEvidencePath workDirectory
        Directory.CreateDirectory work |> ignore
        let info = ClangToolchain.startInfo [ "-dumpmachine" ]
        info.WorkingDirectory <- work
        for variable in [ "TMPDIR"; "TMP"; "TEMP" ] do info.Environment[variable] <- work
        use child = new System.Diagnostics.Process(StartInfo = info)
        if not (child.Start()) then layoutFailure pilotPath "Clang target identity process did not start."
        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(10000)) then
            child.Kill(true)
            child.WaitForExit()
            output.GetAwaiter().GetResult() |> ignore
            errors.GetAwaiter().GetResult() |> ignore
            layoutFailure pilotPath "Clang target identity timed out."
        let target = output.GetAwaiter().GetResult().Trim()
        let diagnostics = errors.GetAwaiter().GetResult().Trim()
        if child.ExitCode <> 0 || diagnostics <> "" then
            layoutFailure pilotPath $"Clang target identity failed ({child.ExitCode}): {diagnostics}"
        match target.Split('-') with
        | [| "x86_64"; "linux"; ("gnu" | "musl") |]
        | [| "x86_64"; _; "linux"; ("gnu" | "musl") |] -> target
        | _ -> layoutFailure pilotPath $"Clang default target '{target}' does not establish the declared Linux x86_64 LP64 ABI."

    type SourceLiteral =
        { Start: int; EndExclusive: int; IsString: bool; Value: string option }

    type AttributeItem =
        { Start: int; EndExclusive: int; GroupStart: int; GroupEndExclusive: int
          DeclarationStart: int; Head: string; Arguments: string; Literals: SourceLiteral[] }

    type LexedSource =
        { CodeSurface: string; AnalysisSurface: string; Attributes: AttributeItem[]
          Documentation: (int * int)[]; ExternIdentifierCount: int }

    /// A positional lexical surface shared by declaration classification and boundary
    /// evidence. Literal values are available only through their lexed attribute items.
    let scanSource (path: string) (source: string) : LexedSource =
        let lexicalFailure message = fail Diagnostics.SubstantiationSourceInvalid path message
        let surface = source.ToCharArray()
        let identifiers = source.ToCharArray()
        let literals = ResizeArray<SourceLiteral>()
        let documentation = ResizeArray<int * int>()
        let length = source.Length
        let starts index (token: string) =
            index + token.Length <= length && String.CompareOrdinal(source, index, token, 0, token.Length) = 0
        let blank (target: char[]) first finish =
            for position in first .. finish - 1 do
                if target[position] <> '\r' && target[position] <> '\n' then target[position] <- ' '
        let mask first finish literal =
            blank surface first finish
            blank identifiers first finish
            if literal then
                surface[first] <- '?'
                identifiers[first] <- '?'
        let decodedString first finish =
            let value = System.Text.StringBuilder()
            let mutable index = first
            let mutable valid = true
            while index < finish && valid do
                if source[index] <> '\\' then
                    value.Append(source[index]) |> ignore
                    index <- index + 1
                elif index + 1 >= finish then valid <- false
                else
                    let escaped = source[index + 1]
                    let simple =
                        match escaped with
                        | '\\' -> Some '\\' | '"' -> Some '"' | '\'' -> Some '\''
                        | 'a' -> Some '\007' | 'b' -> Some '\b' | 'f' -> Some '\012'
                        | 'n' -> Some '\n' | 'r' -> Some '\r' | 't' -> Some '\t' | 'v' -> Some '\011'
                        | _ -> None
                    match simple with
                    | Some character -> value.Append(character) |> ignore; index <- index + 2
                    | None when escaped = '\r' || escaped = '\n' ->
                        index <- index + 2
                        if escaped = '\r' && index < finish && source[index] = '\n' then index <- index + 1
                        while index < finish && (source[index] = ' ' || source[index] = '\t') do index <- index + 1
                    | None ->
                        let digits, radix, offset =
                            if escaped = 'u' then 4, 16, 2
                            elif escaped = 'U' then 8, 16, 2
                            elif Char.IsDigit escaped then 3, 10, 1
                            else 0, 10, 2
                        if digits = 0 || index + offset + digits > finish then valid <- false
                        else
                            try
                                let code = Convert.ToInt32(source.Substring(index + offset, digits), radix)
                                value.Append(Char.ConvertFromUtf32 code) |> ignore
                                index <- index + offset + digits
                            with :? ArgumentException | :? FormatException | :? OverflowException -> valid <- false
            if valid then Some (value.ToString()) else None
        let addLiteral first finish isString value =
            mask first finish true
            literals.Add { Start = first; EndExclusive = finish; IsString = isString; Value = value }
        // Prefix recognition is shared with comment string lexing. Interpolation is
        // refused on the authored surface, but its quotes still delimit comment text.
        let stringPrefix first =
            let mutable at = first
            let mutable verbatim = false
            let mutable interpolated = false
            if at < length && source[at] = '@' then
                verbatim <- true
                at <- at + 1
            while at < length && source[at] = '$' do
                interpolated <- true
                at <- at + 1
            if not verbatim && at < length && source[at] = '@' then
                verbatim <- true
                at <- at + 1
            if at < length && source[at] = '"' then Some(at, verbatim, interpolated) else None
        let consumeCommentString quote verbatim =
            if not verbatim && starts quote "\"\"\"" then
                let mutable at = quote + 3
                while at < length && not (starts at "\"\"\"") do at <- at + 1
                if at >= length then lexicalFailure "Unterminated triple-quoted string in block comment."
                at + 3
            else
                let mutable at = quote + 1
                let mutable closed = false
                while at < length && not closed do
                    if verbatim && starts at "\"\"" then at <- at + 2
                    elif source[at] = '"' then closed <- true; at <- at + 1
                    elif not verbatim && source[at] = '\\' then at <- min length (at + 2)
                    else at <- at + 1
                if not closed then lexicalFailure "Unterminated string in block comment."
                at
        let commentCharacterEnd first =
            // Composer's comment rule recognizes only its char token, including
            // a raw apostrophe and simple escapes, with no identifier boundary.
            if source[first] <> '\'' || first + 2 >= length then None
            else
                let closing =
                    match source[first + 1] with
                    | '\\' ->
                        match source[first + 2] with
                        | '\\' | '"' | '\'' | 'n' | 'r' | 't' | 'b' | 'a' | 'f' | 'v' -> Some(first + 3)
                        | _ -> None
                    | '\r' | '\n' | '\t' | '\b' -> None
                    | _ -> Some(first + 2)
                closing
                |> Option.filter (fun at -> at < length && source[at] = '\'')
                |> Option.map (fun at -> at + 1)
        let startsDirective at =
            if source[at] <> '#' then false
            else
                let mutable lineStart = at - 1
                while lineStart >= 0 && source[lineStart] <> '\n' do lineStart <- lineStart - 1
                source.Substring(lineStart + 1, at - lineStart - 1)
                |> Seq.forall (fun character -> character = ' ' || character = '\t')
        let mutable index = 0
        let mutable quotedExternIdentifiers = 0
        while index < length do
            if starts index "//" then
                let first = index
                while index < length && source[index] <> '\r' && source[index] <> '\n' do index <- index + 1
                if starts first "///" && not (starts first "////") then documentation.Add(first, index)
                mask first index false
            elif starts index "(*)" then index <- index + 3
            elif starts index "(*" then
                let first = index
                let mutable depth = 1
                index <- index + 2
                while index < length && depth > 0 do
                    if starts index "(*)" then index <- index + 3
                    elif starts index "(*" then depth <- depth + 1; index <- index + 2
                    elif starts index "*)" then depth <- depth - 1; index <- index + 2
                    else
                        match commentCharacterEnd index with
                        | Some finish -> index <- finish
                        | None ->
                            match stringPrefix index with
                            | Some(quote, verbatim, _) -> index <- consumeCommentString quote verbatim
                            | None -> index <- index + 1
                if depth <> 0 then lexicalFailure "Unterminated block comment in substantiation source."
                mask first index false
            elif starts index "``" then
                let first = index
                index <- index + 2
                while index < length && not (starts index "``") && source[index] <> '\r' && source[index] <> '\n' do index <- index + 1
                if index >= length || not (starts index "``") then lexicalFailure "Unterminated quoted identifier in substantiation source."
                let name = source.Substring(first + 2, index - first - 2)
                if name = "FidelityExtern" || name = "FidelityExternAttribute" then
                    quotedExternIdentifiers <- quotedExternIdentifiers + 1
                index <- index + 2
                // Quoted identifiers remain readable by declaration classification, but
                // their contents are not lexical attribute or keyword occurrences.
                for position in first .. index - 1 do identifiers[position] <- 'Q'
            elif startsDirective index then
                lexicalFailure "Compiler and conditional directives are not admitted in substantiation sources."
            elif stringPrefix index |> Option.exists (fun (_, _, interpolated) -> interpolated) then
                lexicalFailure "Interpolated strings are not admitted in substantiation sources."
            elif starts index "\"\"\"" then
                let first = index
                index <- index + 3
                let content = index
                while index < length && not (starts index "\"\"\"") do index <- index + 1
                if index >= length then lexicalFailure "Unterminated triple-quoted string in substantiation source."
                let value = source.Substring(content, index - content)
                index <- index + 3
                addLiteral first index true (Some value)
            elif starts index "@\"" then
                let first = index
                let value = System.Text.StringBuilder()
                index <- index + 2
                let mutable closed = false
                while index < length && not closed do
                    if starts index "\"\"" then value.Append('"') |> ignore; index <- index + 2
                    elif source[index] = '"' then closed <- true; index <- index + 1
                    else value.Append(source[index]) |> ignore; index <- index + 1
                if not closed then lexicalFailure "Unterminated verbatim string in substantiation source."
                addLiteral first index true (Some (value.ToString()))
            elif source[index] = '"' then
                let first = index
                index <- index + 1
                let content = index
                while index < length && source[index] <> '"' do
                    if source[index] = '\\' then index <- min length (index + 2)
                    else index <- index + 1
                if index >= length then lexicalFailure "Unterminated string in substantiation source."
                let value = decodedString content index
                index <- index + 1
                addLiteral first index true value
            elif source[index] = '\''
                 && (index = 0 || not (Char.IsLetterOrDigit source[index - 1] || source[index - 1] = '_' || source[index - 1] = '\''))
                 && index + 1 < length
                 && (source[index + 1] = '\\' || (index + 2 < length && source[index + 2] = '\'')
                     || not (Char.IsLetter source[index + 1] || source[index + 1] = '_')) then
                let first = index
                index <- index + 1
                if source[index] = '\\' then
                    index <- index + 2
                    while index < length && source[index] <> '\'' && source[index] <> '\r' && source[index] <> '\n' do index <- index + 1
                else index <- index + 1
                if index >= length || source[index] <> '\'' then lexicalFailure "Unterminated character literal in substantiation source."
                index <- index + 1
                addLiteral first index false None
            elif source[index] = '\'' && index + 1 = length
                 && (index = 0 || not (Char.IsLetterOrDigit source[index - 1] || source[index - 1] = '_')) then
                lexicalFailure "Unterminated character literal in substantiation source."
            else index <- index + 1
        let code = String(surface)
        let tokens = String(identifiers)
        let attributes = ResizeArray<AttributeItem>()
        let groups = ResizeArray<int * int>()
        let attributeHead (item: string) =
            let identifier = @"(?:``[^`\r\n]+``|[A-Za-z_][A-Za-z0-9_']*)"
            let head = Regex.Match(item, @"^\s*(?:" + identifier + @"\s*:\s*)?((?:" + identifier + @"\s*\.\s*)*" + identifier + ")")
            let name =
                if head.Success then
                    let segments = Regex.Matches(head.Groups[1].Value, identifier)
                    let simple = segments[segments.Count - 1].Value.Trim([| '`' |])
                    if simple.EndsWith("Attribute", StringComparison.Ordinal) then simple.Substring(0, simple.Length - "Attribute".Length) else simple
                else ""
            head, name
        let malformedArguments first finish =
            let _, name = attributeHead (code.Substring(first, finish - first))
            if name = "FidelityExtern" then
                fail Diagnostics.SubstantiationExternUnresolved path "FidelityExtern must name exactly two nonempty string literals: library and symbol."
            lexicalFailure "Unbalanced attribute arguments in substantiation source."
        let addItem groupStart groupEnd first finish =
            let item = code.Substring(first, finish - first)
            let head, name = attributeHead item
            attributes.Add
                { Start = first; EndExclusive = finish; GroupStart = groupStart; GroupEndExclusive = groupEnd
                  DeclarationStart = groupEnd; Head = name
                  Arguments = if head.Success then item.Substring(head.Length) else item
                  Literals = literals |> Seq.filter (fun literal -> literal.Start >= first && literal.EndExclusive <= finish) |> Seq.toArray }
        index <- 0
        while index < length do
            if index + 1 < length && tokens[index] = '[' && tokens[index + 1] = '<' then
                let first = index
                index <- index + 2
                let mutable itemStart = index
                let itemSpans = ResizeArray<int * int>()
                let mutable depth = 0
                while index + 1 < length && not (depth = 0 && tokens[index] = '>' && tokens[index + 1] = ']') do
                    if depth <> 0 && tokens[index] = '>' && tokens[index + 1] = ']' then
                        let _, name = attributeHead (code.Substring(itemStart, index - itemStart))
                        if name = "FidelityExtern" then malformedArguments itemStart index
                    match tokens[index] with
                    | '(' | '[' | '{' -> depth <- depth + 1
                    | ')' | ']' | '}' ->
                        depth <- depth - 1
                        if depth < 0 then malformedArguments itemStart (index + 1)
                    | ';' when depth = 0 -> itemSpans.Add(itemStart, index); itemStart <- index + 1
                    | _ -> ()
                    index <- index + 1
                if index + 1 >= length then lexicalFailure "Unterminated attribute group in substantiation source."
                itemSpans.Add(itemStart, index)
                index <- index + 2
                groups.Add(first, index)
                for itemStart, itemEnd in itemSpans do addItem first index itemStart itemEnd
            else index <- index + 1
        let groupEnds = groups |> Map.ofSeq
        let declarationStart groupEnd =
            let mutable at = groupEnd
            let mutable searching = true
            while searching do
                while at < length && Char.IsWhiteSpace code[at] do at <- at + 1
                match groupEnds.TryFind at with
                | Some finish -> at <- finish
                | None -> searching <- false
            at
        { CodeSurface = code
          AnalysisSurface = tokens
          Attributes = attributes |> Seq.map (fun item -> { item with DeclarationStart = declarationStart item.GroupEndExclusive }) |> Seq.toArray
          Documentation = documentation |> Seq.map (fun (first, finish) -> first, declarationStart finish) |> Seq.toArray
          ExternIdentifierCount = Regex.Matches(tokens, @"\bFidelityExtern(?:Attribute)?\b").Count + quotedExternIdentifiers }

    let hasFidelityExtern (attribute: AttributeItem) = attribute.Head = "FidelityExtern"

    let isLiteralAttribute (attribute: AttributeItem) =
        attribute.Head = "Literal" && Regex.IsMatch(attribute.Arguments, @"^\s*(?:\(\s*\))?\s*$")

    let parseFidelityExtern path (source: LexedSource) =
        let attributes = source.Attributes |> Array.filter hasFidelityExtern
        let unresolved message = fail Diagnostics.SubstantiationExternUnresolved path message
        if source.ExternIdentifierCount <> attributes.Length then
            unresolved "Unrecognized FidelityExtern attribute; its link identity cannot be certified."
        attributes |> Array.map (fun attribute ->
            if not (Regex.IsMatch(attribute.Arguments, @"^\s*\(\s*\?\s*,\s*\?\s*\)\s*$")) then
                unresolved "FidelityExtern must name exactly two nonempty string literals: library and symbol."
            match attribute.Literals with
            | [| { IsString = true; Value = Some library }; { IsString = true; Value = Some symbol } |]
                when not (String.IsNullOrWhiteSpace library) && not (String.IsNullOrWhiteSpace symbol) -> library, symbol
            | _ -> unresolved "FidelityExtern must name exactly two nonempty string literals: library and symbol.")

    let private withoutAttributes (source: LexedSource) =
        let code = source.CodeSurface.ToCharArray()
        for item in source.Attributes do
            for index in item.GroupStart .. item.GroupEndExclusive - 1 do
                if code[index] <> '\r' && code[index] <> '\n' then code[index] <- ' '
        String(code)

    type private ForeignType =
        | NamedType of string * ForeignType list
        | FunctionType of ForeignType * ForeignType

    type private TypeKind =
        | OpaqueMarker
        | Measure
        | Record
        | Enum
        | Union
        | Abbreviation of string

    type private LocalType =
        { Name: string; QualifiedName: string; ModuleName: string; Parameters: string list
          Kind: TypeKind; StructAttribute: bool; Path: string }

    let private sourceIdentifier = @"(?:``[^`\r\n]+``|[A-Za-z_][A-Za-z0-9_']*)"
    let private cleanName (name: string) =
        Regex.Matches(name, sourceIdentifier) |> Seq.cast<Match>
        |> Seq.map (fun item -> item.Value.Trim([| '`' |])) |> String.concat "."

    let private moduleAt (source: LexedSource) (surface: string) position =
        let indentation at =
            let start = source.CodeSurface.LastIndexOf('\n', max 0 (at - 1)) + 1
            let mutable finish = start
            while finish < source.CodeSurface.Length && (source.CodeSurface[finish] = ' ' || source.CodeSurface[finish] = '\t') do finish <- finish + 1
            finish - start
        let scopes = ResizeArray<int * string>()
        let leaveScope indent =
            while scopes.Count > 0 && fst scopes[scopes.Count - 1] >= indent do scopes.RemoveAt(scopes.Count - 1)
        for declaration in Regex.Matches(surface.Substring(0, position), @"(?m)^[ \t]*module\s+(?:(?:rec|private|internal|public)\s+)*(" + sourceIdentifier + @"(?:\s*\.\s*" + sourceIdentifier + @")*)\s*(=)?") |> Seq.cast<Match> do
            let indent = indentation declaration.Index
            leaveScope indent
            let parent = if scopes.Count = 0 then "" else snd scopes[scopes.Count - 1] + "."
            let nested = declaration.Groups[2].Success
            scopes.Add((if nested then indent else -1), parent + cleanName declaration.Groups[1].Value)
        leaveScope (indentation position)
        if scopes.Count = 0 then "" else snd scopes[scopes.Count - 1]

    let private classifyTypes (sources: (string * LexedSource)[]) =
        sources |> Array.collect (fun (path, source) ->
            let surface = withoutAttributes source
            let tokenSurface =
                let value = surface.ToCharArray()
                for index in 0 .. value.Length - 1 do
                    if source.AnalysisSurface[index] = 'Q' && source.CodeSurface[index] <> 'Q' then value[index] <- 'Q'
                String(value)
            let events =
                Regex.Matches(tokenSurface, @"(?m)^[ \t]*(?<kind>module|namespace|let|open|exception)\b|\b(?<kind>type|and)\b")
                |> Seq.cast<Match> |> Seq.toArray
            let indentation position =
                let start = source.CodeSurface.LastIndexOf('\n', max 0 (position - 1)) + 1
                let mutable finish = start
                while finish < source.CodeSurface.Length && (source.CodeSurface[finish] = ' ' || source.CodeSurface[finish] = '\t') do finish <- finish + 1
                finish - start
            let preceding = Collections.Generic.Dictionary<int, bool>()
            [| for eventIndex in 0 .. events.Length - 1 do
                   let event = events[eventIndex]
                   let keyword = event.Groups["kind"]
                   let indent = indentation keyword.Index
                   let previousType = match preceding.TryGetValue indent with true, value -> value | _ -> false
                   let isType = keyword.Value = "type" || (keyword.Value = "and" && previousType)
                   preceding[indent] <- isType
                   if isType then
                       let finish =
                           events |> Array.skip (eventIndex + 1)
                           |> Array.tryFind (fun next -> indentation next.Groups["kind"].Index <= indent)
                           |> Option.map _.Index |> Option.defaultValue surface.Length
                       let declaration = surface.Substring(keyword.Index + keyword.Length, finish - keyword.Index - keyword.Length)
                       let header = Regex.Match(declaration, @"^\s*(?:(?:private|internal|public)\s+)*(?:(?<prefix>'[A-Za-z_][A-Za-z0-9_']*|\(\s*'[A-Za-z_][A-Za-z0-9_']*(?:\s*,\s*'[A-Za-z_][A-Za-z0-9_']*)*\s*\))\s+)?(?<name>" + sourceIdentifier + @")\s*(?:<(?<parameters>[^<>]*)>)?")
                       if not header.Success then layoutFailure path "A type declaration has no classifiable name."
                       let name = cleanName header.Groups["name"].Value
                       let moduleName = moduleAt source surface keyword.Index
                       let qualified = if moduleName = "" then name else moduleName + "." + name
                       let parameters =
                           Regex.Matches(header.Groups["prefix"].Value + " " + header.Groups["parameters"].Value, @"'[A-Za-z_][A-Za-z0-9_']*")
                           |> Seq.cast<Match> |> Seq.map _.Value |> Seq.toList
                       let headerEnd = keyword.Index + keyword.Length + header.Length
                       let attached attribute =
                           source.Attributes |> Array.exists (fun item ->
                               item.Head = attribute
                               && (item.DeclarationStart = keyword.Index || (item.GroupStart >= keyword.Index && item.GroupEndExclusive <= headerEnd)))
                       let body = declaration.Substring(header.Length).Trim()
                       let rhs = if body.StartsWith("=", StringComparison.Ordinal) then body.Substring(1).Trim() else ""
                       let structAttribute = attached "Struct"
                       let kind =
                           if attached "Measure" then Measure
                           elif not structAttribute && Regex.IsMatch(rhs, @"^(?:(?:private|internal|public)\s+)?(?:\{\||struct\s*(?:\(|\{\|))") then Abbreviation rhs
                           elif structAttribute || Regex.IsMatch(rhs, @"^(?:(?:private|internal|public)\s+)?(?:\{|struct\b)") then Record
                           elif body = "" then OpaqueMarker
                           elif not (body.StartsWith("=", StringComparison.Ordinal)) then layoutFailure path $"Type '{qualified}' has an unsupported declaration."
                           elif Regex.IsMatch(rhs, @"^private\s*\|\s*Opaque_" + Regex.Escape(name) + @"\s*$") then OpaqueMarker
                           elif Regex.IsMatch(rhs, @"^(?:(?:private|internal|public)\s+)?delegate\b") then Union
                           elif rhs.Contains('|') then
                               let cases = Regex.Replace(rhs, @"^(?:private|internal|public)\s+", "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                               let integer = @"[+-]?(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|[0-9][0-9_]*)(?:[uU]?[lLsSyY]|[uU]|[lL][uU])?"
                               if cases.Length > 0 && (cases |> Array.forall (fun item -> Regex.IsMatch(item, @"^\s*" + sourceIdentifier + @"\s*=\s*" + integer + @"\s*$"))) then Enum else Union
                           else Abbreviation rhs
                       yield { Name = name; QualifiedName = qualified; ModuleName = moduleName; Parameters = parameters
                               Kind = kind; StructAttribute = structAttribute; Path = path } |])

    let private parseForeignType path (annotation: string) =
        if annotation.Contains("*") || annotation.Contains("{|") || annotation.Contains("|}") || Regex.IsMatch(annotation, @"\bstruct\b") then
            layoutFailure path "Tuple, anonymous-record and inline struct types are not admitted in FidelityExtern signatures."
        let tokens = Regex.Matches(annotation, @"\s*(\[\]|->|[<>,()]|'[A-Za-z_][A-Za-z0-9_']*|" + sourceIdentifier + @"(?:\s*\.\s*" + sourceIdentifier + @")*|\S)") |> Seq.cast<Match> |> Seq.map (fun item -> item.Groups[1].Value) |> Seq.toArray
        let mutable index = 0
        let malformed () = layoutFailure path $"Unsupported or incomplete FidelityExtern type '{annotation.Trim()}'."
        let rec arrow () =
            let left = atom ()
            if index < tokens.Length && tokens[index] = "->" then
                index <- index + 1
                FunctionType(left, arrow ())
            else left
        and atom () =
            let mutable current = primary ()
            while index < tokens.Length && (tokens[index] = "array" || tokens[index] = "[]") do
                index <- index + 1
                current <- NamedType("array", [current])
            current
        and primary () =
            if index >= tokens.Length then malformed ()
            let token = tokens[index]
            index <- index + 1
            if token = "(" then
                if index < tokens.Length && tokens[index] = ")" then index <- index + 1; NamedType("unit", [])
                else
                    let inner = arrow ()
                    if index >= tokens.Length || tokens[index] <> ")" then malformed ()
                    index <- index + 1
                    inner
            elif Regex.IsMatch(token, @"^(?:'[A-Za-z_][A-Za-z0-9_']*|" + sourceIdentifier + @"(?:\s*\.\s*" + sourceIdentifier + @")*)$") then
                let arguments = ResizeArray<ForeignType>()
                if index < tokens.Length && tokens[index] = "<" then
                    index <- index + 1
                    arguments.Add(arrow ())
                    while index < tokens.Length && tokens[index] = "," do
                        index <- index + 1
                        arguments.Add(arrow ())
                    if index >= tokens.Length || tokens[index] <> ">" then malformed ()
                    index <- index + 1
                NamedType((if token.StartsWith("'", StringComparison.Ordinal) then token else cleanName token), List.ofSeq arguments)
            else malformed ()
        let result = arrow ()
        if index <> tokens.Length then malformed ()
        result

    let private requiredRecordNames (request: Request) (sources: (string * LexedSource)[]) (layouts: LayoutSummary[]) (enums: EnumSummary[]) =
        let declarations = classifyTypes sources
        let required = Collections.Generic.HashSet<string>()
        let scalars = Set.ofList [ "unit"; "bool"; "byte"; "uint8"; "sbyte"; "int8"; "int16"; "uint16"; "int"; "int32"; "uint"; "uint32"; "int64"; "uint64"; "nativeint"; "unativeint"; "usize"; "isize"; "float"; "float32"; "double"; "single"; "decimal"; "char"; "string"; "voidptr" ]
        let carriers = Set.ofList [ "CHandle"; "FnPtr"; "array"; "ReadOnlyView"; "Nullable"; "CNullable"; "NonNull"; "option"; "voption"; "byref"; "inref"; "outref"; "nativeptr"; "BorrowedView"; "MappedRegion" ]
        let phantomCarriers = Set.ofList [ "CHandle"; "BorrowedView"; "MappedRegion" ]
        let measuredRecord name = layouts |> Array.exists (fun layout -> layout.Kind = "record" && layout.Name = name)
        let measuredEnum name = enums |> Array.exists (fun item -> item.Name = name)
        let requireRecord path name =
            required.Add name |> ignore
            if request.PilotPath.IsNone then layoutFailure path $"Foreign record '{name}' requires a matching per-unit Clang layout measurement from --pilot."
            if not (measuredRecord name) then layoutFailure path $"Foreign record '{name}' has no per-unit Clang layout measurement in pilot headers."
        let resolve path moduleName name =
            let rec parents (scope: string) =
                if scope = "" then [name]
                else
                    let dot = scope.LastIndexOf('.')
                    (scope + "." + name) :: parents (if dot < 0 then "" else scope.Substring(0, dot))
            let candidates = parents moduleName
            let exact = candidates |> List.tryPick (fun candidate -> declarations |> Array.tryFind (fun item -> item.QualifiedName = candidate))
            match exact with
            | Some item -> Some item
            | None when not (name.Contains('.')) ->
                match declarations |> Array.filter (fun item -> item.Name = name) with
                | [|item|] -> Some item
                | [||] -> None
                | _ -> layoutFailure path $"Foreign type '{name}' is ambiguous across build.sources."
            | None -> None
        // Expand argument expressions in the caller's scope before substituting
        // them into an alias's scope. This also separates finite nested applications
        // (Identity<Identity<int>>) from cycles in abbreviation declarations.
        let rec expand path moduleName visited substitutions expression =
            match expression with
            | FunctionType(left, right) ->
                FunctionType(expand path moduleName visited substitutions left, expand path moduleName visited substitutions right)
            | NamedType(name, arguments) ->
                match Map.tryFind name substitutions with
                | Some substituted ->
                    if not arguments.IsEmpty then layoutFailure path "A substituted type parameter cannot itself take type arguments."
                    substituted
                | None ->
                    let arguments = arguments |> List.map (expand path moduleName visited substitutions)
                    match resolve path moduleName name with
                    | Some declaration ->
                        if declaration.Parameters.Length <> arguments.Length then layoutFailure path $"Foreign type '{name}' has the wrong number of type arguments."
                        match declaration.Kind with
                        | Abbreviation rhs ->
                            if Set.contains declaration.QualifiedName visited then layoutFailure path $"Cyclic foreign type abbreviation '{name}'."
                            let bindings = List.zip declaration.Parameters arguments |> Map.ofList
                            expand path declaration.ModuleName (Set.add declaration.QualifiedName visited) bindings (parseForeignType declaration.Path rhs)
                        | _ -> NamedType(declaration.QualifiedName, arguments)
                    | None -> NamedType(name, arguments)
        let rec check path moduleName phantom measureArgument expression =
            match expression with
            | FunctionType(left, right) ->
                if phantom || measureArgument then layoutFailure path "Function types cannot be phantom handle or measure arguments."
                check path moduleName false false left
                check path moduleName false false right
            | NamedType(name, arguments) ->
                match resolve path moduleName name with
                | Some declaration ->
                    match declaration.Kind with
                    | Abbreviation _ -> layoutFailure path $"Foreign abbreviation '{name}' was not expanded."
                    | Measure ->
                        if not (phantom || measureArgument) then layoutFailure path $"Measure '{name}' cannot cross a foreign boundary as a value."
                    | OpaqueMarker ->
                        if not phantom then layoutFailure path $"Opaque type '{name}' requires a CHandle, BorrowedView or MappedRegion carrier."
                        arguments |> List.iter (check path moduleName false false)
                    | Union -> layoutFailure path $"Union or delegate '{name}' is not an admitted foreign boundary type."
                    | Record ->
                        if measureArgument then layoutFailure path $"Record '{name}' is not a measure argument."
                        requireRecord path declaration.Name
                        arguments |> List.iter (check path moduleName false false)
                    | Enum ->
                        if measureArgument || request.PilotPath.IsNone || not (measuredEnum declaration.Name) then layoutFailure path $"Enum '{name}' requires matching measured width and signedness from --pilot."
                        arguments |> List.iter (check path moduleName false false)
                | None when scalars.Contains name ->
                    if measureArgument then layoutFailure path $"Scalar '{name}' is not a declared measure."
                    if arguments.Length > 1 then layoutFailure path $"Scalar '{name}' has unsupported type arguments."
                    arguments |> List.iter (check path moduleName false true)
                | None when carriers.Contains name ->
                    if measureArgument || arguments.Length <> 1 then layoutFailure path $"Carrier '{name}' requires exactly one admitted type argument."
                    arguments |> List.iter (check path moduleName (phantomCarriers.Contains name) false)
                | None ->
                    let simple = name.Split('.') |> Array.last
                    if name.StartsWith("'", StringComparison.Ordinal) || measureArgument || request.PilotPath.IsNone then layoutFailure path $"Undeclared foreign type '{name}' requires explicit measured pilot evidence."
                    if measuredRecord simple then requireRecord path simple
                    elif measuredEnum simple || phantom then ()
                    else layoutFailure path $"Undeclared foreign type '{name}' has no measured record, enum or phantom handle evidence."
                    arguments |> List.iter (check path moduleName false false)
        for declaration in declarations do
            if declaration.StructAttribute then requireRecord declaration.Path declaration.Name
        for path, source in sources do
            let surface = withoutAttributes source
            for attribute in source.Attributes |> Array.filter hasFidelityExtern do
                let declaration = surface.Substring(attribute.DeclarationStart)
                let header = Regex.Match(declaration, @"^let\s+(?:(?:rec|inline|private|internal|public)\s+)*" + sourceIdentifier)
                if not header.Success then fail Diagnostics.SubstantiationExternUnresolved path "FidelityExtern has no recognizable attached let declaration."
                let equals = declaration.IndexOf('=', header.Length)
                if equals < 0 then fail Diagnostics.SubstantiationExternUnresolved path "FidelityExtern declaration has no signature/body boundary."
                let signature = declaration.Substring(header.Length, equals - header.Length)
                let moduleName = moduleAt source surface attribute.DeclarationStart
                let checkAnnotation value =
                    parseForeignType path value |> expand path moduleName Set.empty Map.empty |> check path moduleName false false
                let mutable index = 0
                let skipWhitespace () = while index < signature.Length && Char.IsWhiteSpace signature[index] do index <- index + 1
                skipWhitespace ()
                while index < signature.Length && signature[index] = '(' do
                    let first = index + 1
                    let mutable depth = 1
                    index <- index + 1
                    while index < signature.Length && depth > 0 do
                        if signature[index] = '(' then depth <- depth + 1
                        elif signature[index] = ')' then depth <- depth - 1
                        index <- index + 1
                    if depth <> 0 then layoutFailure path "FidelityExtern parameter parentheses are unbalanced."
                    let parameter = signature.Substring(first, index - first - 1).Trim()
                    if parameter <> "" then
                        let annotation = Regex.Match(parameter, @"^(?:" + sourceIdentifier + @"|_)\s*:\s*(.+)$", RegexOptions.Singleline)
                        if not annotation.Success then layoutFailure path "Every non-unit FidelityExtern parameter requires an explicit named type annotation."
                        checkAnnotation annotation.Groups[1].Value
                    skipWhitespace ()
                if index >= signature.Length || signature[index] <> ':' then layoutFailure path "FidelityExtern requires parenthesized annotated parameters and an explicit return type."
                checkAnnotation (signature.Substring(index + 1))
        Set.ofSeq required

    let private inspectStatic (request: Request) (pilot: PilotTypes.PilotProject option) (externs: (string * string)[]) path =
        if not (File.Exists path) then linkFailure path "Static link provenance does not exist."
        let provenanceDigest = digest "link-provenance" path
        let doc = parseDocument Diagnostics.SubstantiationLinkInvalid path
        let artifact = match Toml.getTable "artifact" doc with Some value -> value | None -> linkFailure path "Missing [artifact] provenance."
        if text path "kind" artifact <> "static-archive" then linkFailure path "Expected artifact kind 'static-archive'."
        let library = text path "library" artifact
        let archive = absolute (Path.GetDirectoryName path) (text path "resolved_path" artifact)
        if absolute (Path.GetDirectoryName path) (text path "path" artifact) <> archive then
            linkFailure path "Artifact path and resolved_path do not identify the same archive."
        let expected = text path "sha256" artifact
        let inspected =
            match ArchiveInspection.inspect archive (Some expected) with
            | Ok value -> value
            | Error refusal -> linkFailure path refusal.Text
        if request.LinkObjects.Length > 0
           && not (request.LinkObjects |> Array.map (absolute (Path.GetDirectoryName request.FidprojPath)) |> Array.contains archive) then
            linkFailure path $"Archive '{archive}' disagrees with the project's explicit link archive."
        match pilot |> Option.bind (fun p -> p.Library.Artifact) with
        | Some declared ->
            let pilotPath = match request.PilotPath with Some value -> value | None -> linkFailure path "Pilot artifact has no pilot origin."
            if absolute (Path.GetDirectoryName(Path.GetFullPath pilotPath)) declared.Path <> archive
               || not (sameHash declared.Sha256 inspected.Sha256) then
                linkFailure path "Pilot artifact path or digest disagrees with link provenance."
        | _ -> ()
        let members = tables path "members" doc
        if members.Length <> inspected.Members.Length then linkFailure path "Archive member count disagrees with provenance."
        for memberInfo in inspected.Members do
            let matching = members |> List.filter (fun value -> integer path "index" value = int64 memberInfo.Index)
            let recorded = match matching with [value] -> value | _ -> linkFailure path $"Missing or duplicate archive member {memberInfo.Index}."
            let strong = memberInfo.DefinedSymbols |> List.filter (fun s -> s.Binding = ArchiveInspection.GlobalStrong) |> List.map _.Name |> List.sort
            let weak = memberInfo.DefinedSymbols |> List.filter (fun s -> s.Binding = ArchiveInspection.Weak) |> List.map _.Name |> List.sort
            let format =
                match memberInfo.Format with
                | ArchiveInspection.ElfRelocatable -> "elf64-relocatable"
                | ArchiveInspection.LlvmBitcode -> "llvm-bitcode"
                | ArchiveInspection.LlvmBitcodeWrapper -> "llvm-bitcode-wrapper"
            if text path "name" recorded <> memberInfo.Name
               || not (sameHash (text path "sha256" recorded) memberInfo.Sha256)
               || text path "format" recorded <> format
               || integer path "offset" recorded <> memberInfo.Offset || integer path "size" recorded <> memberInfo.Size
               || (strings path "strong_symbols" recorded |> List.sort) <> strong
               || (strings path "weak_symbols" recorded |> List.sort) <> weak then
                linkFailure path $"Archive member '{memberInfo.Name}' differs from recorded provenance."
        let bindings = tables path "bindings" doc
        let bindingNames = bindings |> List.map (text path "symbol")
        if (Set.ofList bindingNames).Count <> bindingNames.Length then linkFailure path "Duplicate link-provenance symbol bindings."
        for libraryId, symbol in externs do
            if libraryId <> library then fail Diagnostics.SubstantiationExternUnresolved path $"Extern '{symbol}' names library '{libraryId}', not provenance library '{library}'."
            let bound = bindings |> List.tryFind (fun value -> text path "symbol" value = symbol)
            let actual = inspected.Symbols |> Map.tryFind symbol
            match bound, actual with
            | Some record, Some definition ->
                let memberInfo = inspected.Members |> List.find (fun m -> m.Index = definition.MemberIndex)
                let binding = if definition.Binding = ArchiveInspection.GlobalStrong then "global-strong" else "weak"
                if text path "member" record <> definition.MemberName
                   || integer path "member_index" record <> int64 definition.MemberIndex
                   || not (sameHash (text path "member_sha256" record) memberInfo.Sha256)
                   || text path "binding" record <> binding then linkFailure path $"Symbol '{symbol}' member binding differs from the archive."
            | _ -> fail Diagnostics.SubstantiationExternUnresolved path $"Extern symbol '{symbol}' has no matching [[bindings]] and archive member definition."
        if not (File.Exists path) || not (sameHash provenanceDigest.Sha256 (ClangToolchain.sha256 path)) then
            linkFailure path "Link provenance changed while its archive bindings were checked."
        [| provenanceDigest; digest "static-archive" archive |], [|library|]

    let private measurePilot (request: Request) (pilotPath: string) =
        let pilotDigest = digest "pilot" pilotPath
        let pilot = match PilotSerializer.loadFromFileDiagnosed pilotPath with Ok value -> value | Error e -> layoutFailure pilotPath e.Text
        let baseDirectory = Path.GetDirectoryName pilotPath
        pilot.Library.Artifact |> Option.iter (fun artifact -> absolute baseDirectory artifact.Path |> ignore)
        let headerPaths = pilot.Library.Headers |> List.map (absolute baseDirectory)
        if headerPaths.IsEmpty then layoutFailure pilotPath "A substantiation pilot requires at least one measured header."
        let headerDigests =
            headerPaths |> List.map (fun path ->
                if not (File.Exists path) then layoutFailure path "Pilot header is missing."
                digest "header" path)
        let includes = pilot.Library.IncludePaths |> List.map (absolute baseDirectory)
        let systemIncludes = pilot.Library.SystemIncludePaths |> List.map (absolute baseDirectory)
        let toolPath, toolHash = ClangToolchain.identity ()
        let target = identifyClangTarget request.WorkDirectory pilotPath
        let doc = parseDocument Diagnostics.SubstantiationLayoutInvalid request.FidprojPath
        let platform = match Toml.getTable "platform" doc with Some value -> value | None -> layoutFailure request.FidprojPath "Layout measurement requires an explicit [platform]."
        match TomlTable.tryFind "arch" platform, TomlTable.tryFind "os" platform with
        | Some (TomlValue.String "x86_64"), Some (TomlValue.String "linux") when System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture = System.Runtime.InteropServices.Architecture.X64 -> ()
        | _ -> layoutFailure request.FidprojPath "This Clang measurement route requires the declared Linux x86_64 LP64 target on an x86_64 host."
        let bareRecordName (name: string) =
            if name.StartsWith("typedef ", StringComparison.Ordinal) then name.Substring(8).Trim()
            elif name.StartsWith("struct ", StringComparison.Ordinal) then name.Substring(7).Trim()
            elif name.StartsWith("union ", StringComparison.Ordinal) then name.Substring(6).Trim()
            else name
        let selected = pilot.Options |> Option.map (fun options -> options.AbiCriticalStructs @ options.ValueStructs) |> Option.toList |> List.concat |> Set.ofList
        let found = Collections.Generic.HashSet<string>()
        let layouts = ResizeArray<LayoutSummary>()
        let enums = ResizeArray<EnumSummary>()
        let units = ResizeArray<string * CppParser.Declaration list>()
        let included = ResizeArray<string * CppParser.Declaration list>()
        let measurements = ResizeArray<string * Map<string, CppParser.StructLayoutInfo>>()
        for header in headerPaths do
            let cppMode = not (pilot.Options |> Option.exists (fun o -> o.CHeaderMode)) && CppParser.detectCppMode header
            let options : CppParser.HeaderParserOptions =
                { HeaderFile = header; IncludePaths = includes; SystemIncludePaths = systemIncludes
                  Defines = pilot.Library.Defines; Verbose = false; IncludeMacros = false; MacroPrefixes = []
                  IncludeRoot = None; CppMode = cppMode }
            let parsed = match CppParser.parseHeaderFullDiagnosed options with Ok value -> value | Error e -> layoutFailure header e.Text
            units.Add(header, parsed.Declarations)
            included.Add(header, parsed.Included)
            let typedefs =
                (Map.empty, parsed.Declarations) ||> List.fold (fun acc decl ->
                    match decl with
                    | CppParser.Typedef t when not (acc.ContainsKey t.Name) -> acc.Add(t.Name, t.UnderlyingType)
                    | _ -> acc)
            let matchesSelected (s: CppParser.StructDecl) (entry: string) =
                let bare = bareRecordName entry
                if entry.StartsWith("struct ", StringComparison.Ordinal) then not s.IsUnion && s.Name = bare
                elif entry.StartsWith("union ", StringComparison.Ordinal) then s.IsUnion && s.Name = bare
                elif entry.StartsWith("typedef ", StringComparison.Ordinal) then
                    (s.Name = bare && typedefs.ContainsKey bare)
                    || (match typedefs.TryFind bare with Some underlying -> bareRecordName underlying = s.Name | None -> false)
                else
                    s.Name = bare
                    || (match typedefs.TryFind bare with Some underlying -> bareRecordName underlying = s.Name | None -> false)
            let records = parsed.Declarations |> List.choose (function CppParser.Struct s when CppParser.isDefinedRecord s -> Some s | _ -> None)
            let required = records |> List.filter (fun s -> s.Measured.IsSome || (selected |> Set.exists (matchesSelected s)))
            let named = required |> List.filter (fun s -> s.Anonymous.IsNone)
            let spellings = named |> List.map (fun s ->
                if typedefs.ContainsKey s.Name then "typedef " + s.Name
                elif s.IsUnion then "union " + s.Name
                elif not cppMode then "struct " + s.Name
                else s.Name)
            let measured =
                match CppParser.extractStructLayoutsForModeDiagnosed header includes systemIncludes pilot.Library.Defines spellings cppMode false with
                | Ok value -> value
                | Error e -> layoutFailure header e.Text
            measurements.Add(header, measured)
            for record in required do
                let layout =
                    match record.Anonymous, measured.TryFind record.Name, record.Measured with
                    | None, Some value, _ | Some _, _, Some value -> value
                    | _ -> layoutFailure header $"No per-unit measured layout for '{record.Name}'."
                for entry in selected do
                    if matchesSelected record entry then found.Add entry |> ignore
                layouts.Add { Header = header; Name = record.Name; Kind = "record"; SizeBits = layout.SizeBits
                              AlignmentBits = layout.AlignmentBits; FieldOffsetsBits = Array.ofList layout.FieldOffsetsBits }
            for declaration in parsed.Declarations do
                match declaration with
                | CppParser.Enum e when e.Name <> "" ->
                    match e.Representation with
                    | CppParser.ClangMeasured value ->
                        enums.Add { Header = header; Name = e.Name; WidthBits = value.Bits; Signed = value.IsSigned }
                    | _ -> layoutFailure header $"Enum '{e.Name}' has no measured width and signedness."
                | _ -> ()
        let requireAgreement = function Ok () -> () | Error (failure: Diagnostics.Refusal) -> layoutFailure pilotPath failure.Text
        BindingGenerator.agreeRecords (List.ofSeq units) |> requireAgreement
        BindingGenerator.agreeEnums (Map.ofSeq included) (List.ofSeq units) |> requireAgreement
        BindingGenerator.requireMeasuredEnumUses (Map.ofSeq included) (List.ofSeq units) |> requireAgreement
        BindingGenerator.requireMeasuredAnonymousRecords (List.ofSeq units) |> requireAgreement
        match BindingGenerator.agreeMeasurements (List.ofSeq measurements) with
        | Ok _ -> ()
        | Error reason -> layoutFailure pilotPath reason
        for name in selected do
            if not (found.Contains name) then layoutFailure pilotPath $"ABI-critical/value record '{name}' was measured in no pilot header."
        let files = [| yield pilotDigest; yield! headerDigests; yield { Path = toolPath; Sha256 = toolHash; Kind = "clang" } |]
        for file in files do
            if not (File.Exists file.Path) || not (sameHash file.Sha256 (ClangToolchain.sha256 file.Path)) then
                layoutFailure file.Path "Pilot, header or Clang executable changed during measurement."
        pilot, files, layouts.ToArray(), enums.ToArray(), toolPath + "@sha256:" + toolHash, target

    let evaluate (request: Request) : Result<BoundaryEvidence, Diagnostics.Refusal> =
        try
            let project = ScratchDirectory.canonicalEvidencePath request.FidprojPath
            let projectDirectory = ScratchDirectory.canonicalEvidencePath (Path.GetDirectoryName project)
            let request =
                { request with
                    FidprojPath = project
                    WorkDirectory = ScratchDirectory.canonicalEvidencePath request.WorkDirectory
                    Sources = request.Sources |> Array.map (fun (path, source) -> absolute projectDirectory path, source)
                    LinkObjects = request.LinkObjects |> Array.map (absolute projectDirectory)
                    PilotPath = request.PilotPath |> Option.map (absolute projectDirectory)
                    LinkProvenancePath = request.LinkProvenancePath |> Option.map (absolute projectDirectory) }
            ScratchDirectory.within request.WorkDirectory (fun () ->
                let projectDocument = parseDocument Diagnostics.SubstantiationLayoutInvalid project
                for key in [ "build.sources"; "link.objects" ] do
                    match Toml.getValue key projectDocument with
                    | Some(TomlValue.Array paths) ->
                        for value in paths do
                            match value with
                            | TomlValue.String path -> absolute projectDirectory path |> ignore
                            | _ -> linkFailure project $"Project '{key}' must contain path strings."
                    | None -> ()
                    | _ -> linkFailure project $"Project '{key}' must be a path array."
                match Toml.getValue "link.archives" projectDocument with
                | Some(TomlValue.Table archives) | Some(TomlValue.InlineTable archives) ->
                    for _, value in Map.toList archives do
                        match value with
                        | TomlValue.String path -> absolute projectDirectory path |> ignore
                        | _ -> linkFailure project "Project 'link.archives' must map library names to path strings."
                | None -> ()
                | _ -> linkFailure project "Project 'link.archives' must be a table of archive paths."
                match Toml.getTable "dependencies" projectDocument with
                | Some dependencies ->
                    for _, value in Map.toList dependencies do
                        match value with
                        | TomlValue.Table dependency | TomlValue.InlineTable dependency ->
                            match TomlTable.tryFind "path" dependency with
                            | Some(TomlValue.String path) -> absolute projectDirectory path |> ignore
                            | _ -> ()
                        | _ -> ()
                | None -> ()
                let sources = request.Sources |> Array.map (fun (path, source) -> path, scanSource path source)
                let externPairs = sources |> Array.collect (fun (path, source) -> parseFidelityExtern path source) |> Array.distinct |> Array.sort
                let sourceCode = sources |> Array.map (snd >> _.AnalysisSurface) |> String.concat "\n"
                let pilot, layoutFiles, layouts, enums, clang, clangTarget =
                    match request.PilotPath with
                    | Some path ->
                        let path = absolute projectDirectory path
                        if not (File.Exists path) then layoutFailure path "Pilot file does not exist."
                        let p, files, values, enums, identity, target = measurePilot request path
                        Some p, files, values, enums, identity, target
                    | None -> None, [||], [||], [||], "not-applicable", "not-measured"
                let requiredMeasuredRecords =
                    if externPairs.Length > 0 then requiredRecordNames request sources layouts enums else Set.empty
                let staticArtifact = pilot |> Option.bind (fun p -> p.Library.Artifact) |> Option.isSome
                let staticObjects = request.LinkObjects |> Array.filter (fun p -> p.EndsWith(".a", StringComparison.OrdinalIgnoreCase))
                let conventional = Path.ChangeExtension(request.FidprojPath, ".link-provenance.toml")
                let provenance =
                    match request.LinkProvenancePath with
                    | Some path -> Some (absolute projectDirectory path)
                    | None when staticArtifact || staticObjects.Length > 0 || File.Exists conventional -> Some conventional
                    | None -> None
                provenance |> Option.iter (fun path -> ScratchDirectory.canonicalEvidencePath path |> ignore)
                let staticProvenance =
                    provenance |> Option.exists (fun path ->
                        let document = parseDocument Diagnostics.SubstantiationLinkInvalid path
                        for key in [ "artifact.path"; "artifact.resolved_path" ] do
                            match Toml.getValue key document with
                            | Some(TomlValue.String artifactPath) -> absolute (Path.GetDirectoryName path) artifactPath |> ignore
                            | _ -> ()
                        match Toml.getValue "artifact.kind" document with
                        | Some (TomlValue.String "static-archive") -> true
                        | Some (TomlValue.String "shared-library") -> false
                        | _ -> linkFailure path "Link provenance must declare artifact.kind as static-archive or shared-library.")
                let hasStatic = staticArtifact || staticObjects.Length > 0 || staticProvenance
                let linkFiles, libraries, mode =
                    if hasStatic then
                        if request.LinkObjects.Length > 1 || staticObjects.Length <> request.LinkObjects.Length then linkFailure request.FidprojPath "Static substantiation requires one provenance-bound archive and no unaccounted link objects."
                        let path = match provenance with Some p -> p | None -> linkFailure request.FidprojPath "Missing static link provenance."
                        let files, ids = inspectStatic request pilot externPairs path
                        if request.LinkLibraries |> Array.exists (fun library -> not (Array.contains library ids)) then
                            linkFailure request.FidprojPath "Static provenance with additional unpaired dynamic libraries requires separate explicit provenance."
                        files, ids, "static-archive"
                    elif request.LinkObjects.Length > 0 then linkFailure request.FidprojPath "Unprovenanced link objects cannot be substantiated."
                    elif externPairs.Length = 0 && request.LinkLibraries.Length = 0 then
                        if provenance.IsSome then linkFailure request.FidprojPath "Link provenance is supplied for a library without a foreign link boundary."
                        [||], [||], "none"
                    else
                        if request.LinkLibraries.Length = 0 then linkFailure request.FidprojPath "Foreign externs require explicit [link] libraries or static archive provenance."
                        let libraries = request.LinkLibraries |> Array.distinct |> Array.sort
                        for lib, symbol in externPairs do
                            if not (Array.contains lib libraries) then fail Diagnostics.SubstantiationExternUnresolved request.FidprojPath $"Extern '{symbol}' names '{lib}', absent from [link] libraries."
                        let extraFiles =
                            match provenance with
                            | None -> [||]
                            | Some path ->
                                let doc = parseDocument Diagnostics.SubstantiationLinkInvalid path
                                let artifact = match Toml.getTable "artifact" doc with Some value -> value | None -> linkFailure path "Shared provenance needs [artifact]."
                                if text path "kind" artifact <> "shared-library" then linkFailure path "Dynamic linkage requires kind='shared-library'."
                                if (strings path "libraries" artifact |> List.sort |> List.toArray) <> libraries then linkFailure path "Shared provenance library identifiers disagree with [link]."
                                [|digest "link-provenance" path|]
                        Array.append [|digest "shared-link-contract" request.FidprojPath|] extraFiles, libraries, "runtime-shared-library"
                let options = pilot |> Option.bind _.Options
                let emitted name = Regex.IsMatch(sourceCode, @"\b" + Regex.Escape(name) + @"\b")
                let usedSymbol name = externPairs |> Array.exists (fun (_, symbol) -> symbol = name)
                let bindings = options |> Option.map _.Bindings |> Option.toList |> List.concat |> List.filter (fun b -> usedSymbol b.Symbol || emitted b.Name)
                let errors = externPairs.Length > 0 && (pilot |> Option.bind _.ErrorConventions |> Option.exists (fun e -> e.Default <> PilotTypes.NoErrorConvention || (e.Overrides |> Map.exists (fun name value -> usedSymbol name && value <> PilotTypes.NoErrorConvention))))
                let mappedReturns = options |> Option.map _.MappedReturns |> Option.toList |> List.concat |> List.filter (fun p -> usedSymbol p.Acquire || emitted p.Name)
                let flexibleArrays = options |> Option.map _.FlexibleArrays |> Option.toList |> List.concat |> List.filter (fun p -> emitted p.Record)
                let callbacks =
                    (pilot |> Option.bind _.Callbacks |> Option.exists (fun c -> (c.Registrations |> List.exists (fun r -> usedSymbol r.Function)) || (c.ListenerStructs |> List.exists (fun s -> emitted s.Name))))
                    || (bindings |> List.exists (fun b -> not b.CallbackInvocations.IsEmpty || not b.NonnullCallbacks.IsEmpty))
                    || Regex.IsMatch(sourceCode, @"\bFnPtr\s*<")
                let strata =
                    { ForeignCalls = externPairs.Length > 0; MeasuredRecords = (layouts |> Array.exists (fun l -> l.Kind = "record" && emitted l.Name)) || not requiredMeasuredRecords.IsEmpty
                      StaticArchive = hasStatic; ErrorConventions = errors
                      Ownership = (bindings |> List.exists (fun b -> b.Resource.IsSome || b.Ownership = Some PilotTypes.CallerOwns || b.Ownership = Some PilotTypes.CalleeOwns)) || Regex.IsMatch(sourceCode, @"\bOwnershipTransfer\s*=\s*(CallerOwns|CalleeOwns)\b")
                      ZeroCopy = not mappedReturns.IsEmpty || not flexibleArrays.IsEmpty || (bindings |> List.exists (fun b -> b.BorrowedResult.IsSome || not b.WritableBuffers.IsEmpty || not b.ReadOnlyBuffers.IsEmpty)) || Regex.IsMatch(sourceCode, @"\b(StructDescriptor|BorrowedView|MappedRegion)\b")
                      Callbacks = callbacks
                      Nullability = (pilot |> Option.bind _.Nonnull |> Option.exists (fun n -> (n.Parameters |> Map.exists (fun name _ -> usedSymbol name)) || (n.Returns |> Set.exists usedSymbol))) || Regex.IsMatch(sourceCode, @"\b(Nullable|CNullable|NonNull)\b") }
                let origin = String.concat ";" (Array.append [|request.FidprojPath|] (layoutFiles |> Array.filter (fun f -> f.Kind = "pilot" || f.Kind = "header") |> Array.map _.Path))
                let hypotheses, checks = deriveContract origin strata
                let files = Array.append linkFiles layoutFiles |> Array.distinctBy (fun f -> f.Path, f.Kind)
                let evidenceFiles kind = files |> Array.filter (fun f -> f.Kind = kind) |> Array.map (fun f -> f.Path + "@sha256:" + f.Sha256) |> String.concat ", "
                let contractDetails = [
                    yield "exported surface=" + (request.Sources |> Array.map fst |> String.concat ", ")
                    match pilot |> Option.bind _.ErrorConventions with
                    | Some e when errors ->
                        yield sprintf "error default=%A; selected overrides=%A" e.Default (e.Overrides |> Map.filter (fun name _ -> usedSymbol name))
                    | _ -> ()
                    for b in bindings do
                        if b.Ownership.IsSome then yield sprintf "%s ownership=%A" b.Name b.Ownership
                        match b.Resource with
                        | Some r -> yield $"{b.Name} resource={r.Identity}@{r.Version}; release={r.ReleaseBinding}[{r.ReleaseParameter}]; source={r.Source}"
                        | None -> ()
                        match b.BorrowedResult with
                        | Some r -> yield sprintf "%s borrowed=%s@%s; source=%s; extent=%A; invalidatedBy=%A" b.Name r.Identity r.Version r.Source r.Extent r.InvalidatedBy
                        | None -> ()
                        for buffer in b.WritableBuffers do
                            yield $"{b.Name} buffer[{buffer.Parameter}]={buffer.Identity}@{buffer.Version}; countParameter={buffer.CountParameter}; source={buffer.Source}"
                        for buffer in b.ReadOnlyBuffers do
                            yield sprintf "%s readOnlyBuffer[%d]=%s@%s; extent=%A; content=%A; retention=%A; source=%s" b.Name buffer.Parameter buffer.Identity buffer.Version buffer.Extent buffer.Content buffer.Retention buffer.Source
                        for absence in b.BufferAbsences do
                            yield $"{b.Name} bufferAbsence[{absence.Parameter}]={absence.Identity}@{absence.Version}; source={absence.Source}"
                    match pilot |> Option.bind _.Nonnull with
                    | Some n when strata.Nullability ->
                        yield sprintf "nonnull parameters=%A; returns=%A" (n.Parameters |> Map.filter (fun name _ -> usedSymbol name)) (n.Returns |> Set.filter usedSymbol)
                    | _ -> ()
                    for mapped in mappedReturns do
                        yield $"mapped {mapped.Name}; acquire={mapped.Acquire}; release={mapped.Release}; schema={mapped.Schema}; alignment={mapped.Alignment}; access={mapped.Access}"
                    for array in flexibleArrays do
                        yield sprintf "flexible %s.%s; count=%s; rule=%A; source=%s" array.Record array.Field array.CountField array.Rule array.Source
                ]
                let callbackDetails = [
                    yield "exported callback surface=" + (request.Sources |> Array.map fst |> String.concat ", ")
                    for b in bindings do
                        for callback in b.CallbackInvocations do
                            yield sprintf "%s[%d]=%s@%s; scope=%s; source=%s; exit=%A; retention=%A" b.Name callback.Parameter callback.Identity callback.Version callback.Scope callback.Source callback.ExitBinding callback.Retention
                    match pilot |> Option.bind _.Callbacks with
                    | Some callbacks ->
                        for registration in callbacks.Registrations do
                            if usedSymbol registration.Function then yield sprintf "registration=%A" registration
                        for listener in callbacks.ListenerStructs do
                            if emitted listener.Name then yield sprintf "listener=%A" listener
                    | None -> ()
                ]
                let boundHypotheses = hypotheses |> Array.map (fun hypothesis ->
                    let details =
                        match hypothesis.Family with
                        | "H-ABI" ->
                            let records = layouts |> Array.filter (fun l -> emitted l.Name) |> Array.map (fun l -> sprintf "%s:%s size=%d alignment=%d offsets=%A bits" l.Header l.Name l.SizeBits l.AlignmentBits l.FieldOffsetsBits) |> String.concat "; "
                            let enumFacts = enums |> Array.filter (fun e -> emitted e.Name) |> Array.map (fun e -> sprintf "%s:%s width=%d signed=%b" e.Header e.Name e.WidthBits e.Signed) |> String.concat "; "
                            sprintf "Clang=%s; target=%s; pilot=%s; headers=%s; records=[%s]; enums=[%s]" clang clangTarget (evidenceFiles "pilot") (evidenceFiles "header") records enumFacts
                        | "H-Link" ->
                            let symbols = externPairs |> Array.map (fun (library, symbol) -> library + ":" + symbol) |> String.concat ", "
                            sprintf "mode=%s; bindings=[%s]; archive=%s; provenance=%s; sharedContract=%s" mode symbols (evidenceFiles "static-archive") (evidenceFiles "link-provenance") (evidenceFiles "shared-link-contract")
                        | "H-Contract" -> String.concat "; " contractDetails
                        | "H-Callback" -> String.concat "; " callbackDetails
                        | family -> invalidOp $"Unknown hypothesis family '{family}'."
                    { hypothesis with Statement = hypothesis.Statement + " Evidence: " + details })
                Ok { Strata = strata; Files = files
                     LinkMode = mode; Libraries = libraries; ExternSymbols = externPairs |> Array.map snd |> Array.distinct |> Array.sort
                     ClangIdentity = clang; ClangTarget = clangTarget; TargetDataModel = if pilot.IsSome then "LP64/linux-x86_64" else "not-measured"
                     Layouts = layouts; Enums = enums; RestsOn = boundHypotheses; RequiredSeamChecks = checks })
        with
        | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
        | error ->
            Diagnostics.diagnostic Diagnostics.SubstantiationLayoutInvalid error.Message
                (Diagnostics.inFile request.FidprojPath "substantiation boundary evaluation")
            |> Diagnostics.refusalOf |> Error
