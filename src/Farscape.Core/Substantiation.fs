namespace Farscape.Core

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Fidelity.Data.TOML

/// The library substantiation contract (#208). Compiler proof and foreign evidence retain
/// their original owners; this module binds them to every source of one library project.
module Substantiation =
    [<CLIMutable>]
    type FileDigest = { Path: string; Sha256: string }

    [<CLIMutable>]
    type SourceDigest =
        { Path: string; Sha256: string; ModuleName: string
          RuntimeDeclarations: string[]; CompileTimeDeclarations: string[]; ForeignDeclarations: string[] }

    [<CLIMutable>]
    type Manifest =
        { SchemaVersion: int
          Package: string
          FidprojPath: string
          FidprojSha256: string
          Sources: SourceDigest[]
          Compiler: FileDigest
          Dotnet: FileDigest
          Harness: FileDigest
          HarnessProject: FileDigest
          CompilerLog: FileDigest
          Psg0: FileDigest
          WitnessUnits: FileDigest
          AllowWarnings: bool
          WarningsAsErrors: bool
          ElevatedWarningCodes: string[]
          ArtifactsDirectory: string
          Proof: SubstantiationProof.ProofSummary
          Boundary: SubstantiationBoundary.BoundaryEvidence
          IntegritySha256: string }

    type Request =
        { FidprojPath: string
          PilotPath: string option
          LinkProvenancePath: string option
          OutputDirectory: string option
          WorkDirectory: string option
          Verbose: bool
          AllowWarnings: bool }

    /// Where a PSG node lies inside the library's root modules (#209). A node outside every
    /// library root module has no owner.
    [<RequireQualifiedAccess>]
    type internal PsgNodeOwner =
        /// The node is, or descends from, this module-level library binding.
        | Binding of bindingNodeId: int * qualified: string
        /// The node is inside a library root module and below no module-level binding.
        | NonBinding

    /// The foreign symbol that a module-level library binding describes as a FunctionDescriptor (#209).
    [<RequireQualifiedAccess>]
    type internal DescriptorSymbol =
        /// The binding is no FunctionDescriptor quotation.
        | NotDescriptor
        /// The CName string literal of the descriptor record: the compiler PSG publishes it as
        /// the literal node of the record's CName field, and the source text at that node's
        /// range spells the same literal.
        | Named of cname: string
        /// A FunctionDescriptor quotation whose CName is not established that way.
        | Unestablished of reason: string

    /// One module-level library binding as the compiler PSG and the source classifier agree on it.
    type internal DeclarationNode =
        { Qualified: string
          SourcePath: string
          BindingNodeId: int
          Range: string
          Type: string
          /// 1-based first and last source line of the declaration block.
          FirstLine: int
          LastLine: int
          /// The FidelityExtern (library, symbol) of a foreign declaration.
          Foreign: (string * string) option
          /// The symbol the binding describes when it is a FunctionDescriptor quotation.
          Descriptor: DescriptorSymbol }

    /// A witnessed MLIR definition whose source node or traversal root is a library node.
    type internal WitnessedDefinition =
        { Unit: string
          Symbol: string
          SourceNode: int
          TraversalRoot: int
          Declaration: string option
          SourceInLibrary: bool
          RootInLibrary: bool }

    /// A witnessed foreign import whose source boundary resolves to a library declaration.
    type internal WitnessedImport =
        { Unit: string
          Symbol: string
          Library: string
          DeclarationNode: int
          Declaration: string
          Resource: bool }

    type internal WitnessedObligation = { Anchor: string; Source: string; Kind: string }

    /// The verified PSG and witness-unit correspondence of one validated manifest.
    type internal Correspondence =
        { Declarations: DeclarationNode[]
          Definitions: WitnessedDefinition[]
          Imports: WitnessedImport[]
          /// None when the witness manifest carries no proof obligation list.
          Obligations: WitnessedObligation[] option
          /// The (Invocation, QuerySha256) of the source proof receipt the witness embeds.
          SourceProof: (string * string) option }

    let private refuse code path message =
        Diagnostics.diagnostic code message (Diagnostics.inFile path "substantiation input or retained evidence")
        |> Diagnostics.refuse

    let private protect code path action =
        try Ok(action ())
        with
        | :? Diagnostics.RefusalException as ex -> Error ex.Refusal
        | ex ->
            Diagnostics.diagnostic code ex.Message (Diagnostics.inFile path "substantiation input or retained evidence")
            |> Diagnostics.refusalOf |> Error

    let private required = function
        | Ok value -> value
        | Error refusal -> Diagnostics.raiseRefusal refusal

    let private shaText (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private digest path : FileDigest =
        { Path = Path.GetFullPath path; Sha256 = ClangToolchain.sha256 path }

    let private jsonOptions = JsonSerializerOptions(WriteIndented = true)
    let private canonical (manifest: Manifest) = JsonSerializer.Serialize({ manifest with IntegritySha256 = "" })

    /// Bind the manifest's metadata as well as the external file digests. This is integrity
    /// evidence, not a signature or an assertion that the hypotheses have been proved.
    let seal (manifest: Manifest) = { manifest with IntegritySha256 = shaText (canonical manifest) }

    let toJson (manifest: Manifest) = JsonSerializer.Serialize(manifest, jsonOptions)

    let rec private tomlOfJson (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            value.EnumerateObject() |> Seq.map (fun p -> p.Name, tomlOfJson p.Value) |> Map.ofSeq |> TomlValue.InlineTable
        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.map tomlOfJson |> List.ofSeq |> TomlValue.Array
        | JsonValueKind.String -> TomlValue.String(value.GetString())
        | JsonValueKind.Number -> TomlValue.Integer(value.GetInt64())
        | JsonValueKind.True -> TomlValue.Boolean true
        | JsonValueKind.False -> TomlValue.Boolean false
        | _ -> invalidOp "The substantiation schema admits no null or unsupported JSON value."

    let toToml (manifest: Manifest) =
        use document = JsonDocument.Parse(toJson manifest)
        match tomlOfJson document.RootElement with
        | TomlValue.InlineTable table -> Toml.serialize table
        | _ -> invalidOp "A substantiation manifest must be a table."

    let rec private objectOfToml = function
        | TomlValue.Table table | TomlValue.InlineTable table ->
            table |> Map.toSeq |> Seq.map (fun (key, value) -> key, objectOfToml value) |> dict |> box
        | TomlValue.Array values -> values |> List.map objectOfToml |> List.toArray |> box
        | TomlValue.String text -> box text
        | TomlValue.Integer number -> box number
        | TomlValue.Boolean value -> box value
        | _ -> invalidOp "The substantiation schema admits only tables, arrays, strings, integers and booleans."

    /// System.Text.Json's missing-field defaults are not an input contract. Require every
    /// record field, including empty arrays, and reject unknown or duplicate fields first.
    let rec internal requireJsonShape (schema: Type) (value: JsonElement) =
        if FSharpType.IsRecord schema then
            if value.ValueKind <> JsonValueKind.Object then invalidOp $"Expected a {schema.Name} object."
            let fields = FSharpType.GetRecordFields schema
            let properties = value.EnumerateObject() |> Seq.toArray
            if properties.Length <> fields.Length || (properties |> Array.map _.Name |> Array.distinct).Length <> fields.Length then
                invalidOp $"{schema.Name} has missing, duplicate or unknown fields."
            for field in fields do
                match value.TryGetProperty field.Name with
                | true, child -> requireJsonShape field.PropertyType child
                | _ -> invalidOp $"Missing {schema.Name}.{field.Name}."
        elif schema.IsArray then
            if value.ValueKind <> JsonValueKind.Array then invalidOp $"Expected an array of {schema.GetElementType().Name}."
            for child in value.EnumerateArray() do requireJsonShape (schema.GetElementType()) child
        elif schema = typeof<string> then
            if value.ValueKind <> JsonValueKind.String then invalidOp "Expected a string."
        elif schema = typeof<int> then value.GetInt32() |> ignore
        elif schema = typeof<int64> then value.GetInt64() |> ignore
        elif schema = typeof<bool> then value.GetBoolean() |> ignore
        else invalidOp $"Unadmitted schema type {schema.FullName}."

    let private validWarningCode code =
        not (isNull code) && Regex.IsMatch(code, @"\A[A-Z]+[0-9]+\z", RegexOptions.CultureInvariant)

    let private requireIntegrity (manifest: Manifest) =
        let fail message = refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath message
        if manifest.SchemaVersion <> 1 then fail "Unsupported substantiation schema version."
        if isNull manifest.ElevatedWarningCodes
           || (manifest.ElevatedWarningCodes |> Array.exists (validWarningCode >> not))
           || manifest.ElevatedWarningCodes <> (manifest.ElevatedWarningCodes |> Array.distinct |> Array.sort)
           || not (Array.contains "CCS8500" manifest.ElevatedWarningCodes) then
            fail "ElevatedWarningCodes must be sorted, distinct diagnostic codes including CCS8500."
        if String.IsNullOrWhiteSpace manifest.Package || manifest.Package <> Path.GetFileName manifest.Package then
            fail "Package must be a nonempty filename component."
        if manifest.Sources.Length = 0 then fail "The manifest records no exported modules."
        if (manifest.Sources |> Array.map _.Path |> Array.distinct).Length <> manifest.Sources.Length then fail "Duplicate exported source paths."
        if (manifest.Sources |> Array.map _.ModuleName |> Array.distinct).Length <> manifest.Sources.Length then fail "Duplicate exported module names."
        if manifest.IntegritySha256 <> shaText (canonical manifest) then fail "The manifest integrity digest does not match its contents."
        let proof = manifest.Proof
        if proof.TotalObligations <= 0 || proof.DischargedUnsatCount <> proof.TotalObligations
           || proof.PremisesSatCount <> proof.TotalObligations || proof.PendingOpenCount <> 0 || proof.RefutedCount <> 0
           || proof.LibraryObligations < 0 || proof.HarnessObligations < 0
           || proof.LibraryObligations + proof.HarnessObligations > proof.TotalObligations
           || proof.LibraryDischargedUnsatCount <> proof.LibraryObligations
           || proof.LibraryPremisesSatCount <> proof.LibraryObligations
           || proof.CompilerPremiseReceiptsCount < 0 || proof.CompilerNonVacuityReceiptsCount < 0 then
            fail "The manifest does not certify SAT(H) and UNSAT(H and not C) for every obligation."
        if (manifest.Sources |> Array.exists (fun source -> source.RuntimeDeclarations.Length > 0)) && proof.LibraryObligations = 0 then
            fail "Runtime declarations require obligations attributed to the library's build.sources."
        let boundary = manifest.Boundary
        let hasFile kind = boundary.Files |> Array.exists (fun file -> file.Kind = kind)
        if not (Set.contains boundary.LinkMode (set [ "none"; "static-archive"; "runtime-shared-library" ])) then
            fail "Unknown boundary LinkMode."
        if not (Set.contains boundary.TargetDataModel (set [ "not-measured"; "LP64/linux-x86_64" ])) then
            fail "Unknown boundary TargetDataModel."
        let fileKinds = set [ "pilot"; "header"; "clang"; "link-provenance"; "static-archive"; "shared-link-contract" ]
        if boundary.Files |> Array.exists (fun file -> not (fileKinds.Contains file.Kind)) then
            fail "Unknown boundary evidence file kind."
        let foreignDeclarationCount = manifest.Sources |> Array.sumBy (fun source -> source.ForeignDeclarations.Length)
        if boundary.ExternSymbols.Length > foreignDeclarationCount then
            fail "Extern symbols cannot outnumber classified foreign declarations."
        if boundary.Strata.ForeignCalls <> (boundary.ExternSymbols.Length > 0)
           || boundary.Strata.ForeignCalls <> (manifest.Sources |> Array.exists (fun source -> source.ForeignDeclarations.Length > 0)) then
            fail "Foreign-call strata must match both extern symbols and source declarations."
        if boundary.Strata.StaticArchive <> (boundary.LinkMode = "static-archive") then
            fail "Static-archive strata must match LinkMode."
        if boundary.LinkMode = "none" && (boundary.ExternSymbols.Length <> 0 || boundary.Libraries.Length <> 0 || boundary.Strata.ForeignCalls) then
            fail "A boundary without linkage cannot declare extern symbols or libraries."
        if boundary.LinkMode = "static-archive" && (not (hasFile "static-archive") || not (hasFile "link-provenance") || boundary.Libraries.Length <> 1) then
            fail "Static linkage requires archive and provenance digests and one library."
        if hasFile "pilot" then
            let measured value = not (String.IsNullOrWhiteSpace value) && value <> "not-applicable" && value <> "not-measured"
            if boundary.TargetDataModel <> "LP64/linux-x86_64" || not (measured boundary.ClangIdentity)
               || not (measured boundary.ClangTarget) || not (hasFile "clang") || not (hasFile "header") then
                fail "A pilot requires measured LP64 Clang identity, target and header evidence."
        elif boundary.TargetDataModel <> "not-measured" || boundary.ClangIdentity <> "not-applicable"
             || boundary.ClangTarget <> "not-measured" || boundary.Layouts.Length <> 0 || boundary.Enums.Length <> 0 then
            fail "Measured layout and Clang claims require a pilot."
        if boundary.Strata.ForeignCalls && boundary.Strata.MeasuredRecords && boundary.TargetDataModel <> "LP64/linux-x86_64" then
            fail "Foreign record boundaries require measured pilot provenance."
        let families = set [ "H-ABI"; "H-Link"; "H-Contract"; "H-Callback" ]
        for hypothesis in manifest.Boundary.RestsOn do
            if not (families.Contains hypothesis.Family) || String.IsNullOrWhiteSpace hypothesis.Id
               || String.IsNullOrWhiteSpace hypothesis.Origin || String.IsNullOrWhiteSpace hypothesis.Statement then
                fail "A RestsOn hypothesis is incomplete or has an unknown family."
        if (manifest.Boundary.RestsOn |> Array.map _.Id |> Array.distinct).Length <> manifest.Boundary.RestsOn.Length then
            fail "RestsOn hypothesis identities must be unique."
        if manifest.Boundary.RequiredSeamChecks |> Array.distinct |> Array.length <> manifest.Boundary.RequiredSeamChecks.Length then
            fail "RequiredSeamChecks contains duplicate categories."
        let hypotheses, seams = SubstantiationBoundary.deriveContract manifest.FidprojPath manifest.Boundary.Strata
        if manifest.Boundary.RequiredSeamChecks <> seams then
            fail "RequiredSeamChecks does not match the exact boundary strata."
        if (hypotheses |> Array.map (fun h -> h.Id, h.Family)) <> (manifest.Boundary.RestsOn |> Array.map (fun h -> h.Id, h.Family)) then
            fail "RestsOn does not match the boundary hypothesis families."

    let parseJson (text: string) =
        protect Diagnostics.SubstantiationManifestMalformed "<substantiation.json>" (fun () ->
            use document = JsonDocument.Parse text
            requireJsonShape typeof<Manifest> document.RootElement
            let manifest = JsonSerializer.Deserialize<Manifest>(text)
            requireIntegrity manifest
            manifest)

    let parseToml (text: string) =
        protect Diagnostics.SubstantiationManifestMalformed "<substantiation.toml>" (fun () ->
            let document = match Toml.parse text with Ok doc -> doc | Error reason -> invalidOp reason
            objectOfToml (TomlValue.Table document) |> JsonSerializer.Serialize |> parseJson |> required)

    let private checkDigest (path: string) (expected: string) =
        if not (File.Exists path) || not (Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$"))
           || not (String.Equals(ClangToolchain.sha256 path, expected, StringComparison.OrdinalIgnoreCase)) then
            refuse Diagnostics.SubstantiationDigestMismatch path "The retained file is missing or its SHA-256 has changed."

    let private loadProject path =
        protect Diagnostics.SubstantiationProjectInvalid path (fun () ->
            if not (File.Exists path) then invalidOp "The .fidproj file does not exist."
            match Toml.parse(File.ReadAllText path) with Ok doc -> doc | Error reason -> invalidOp reason)

    let private strings code path key document requiredField =
        match Toml.getValue key document with
        | Some(TomlValue.Array values) ->
            values |> List.map (function TomlValue.String value when not (String.IsNullOrWhiteSpace value) -> value
                                         | _ -> refuse code path $"{key} must contain nonempty strings.") |> List.toArray
        | None when not requiredField -> [||]
        | _ -> refuse code path $"{key} must be an explicit array of strings."

    let private warningPolicy path document =
        let warningsAsErrors =
            match Toml.getValue "substantiation.warnings_as_errors" document with
            | None -> true
            | Some(TomlValue.Boolean value) -> value
            | _ -> refuse Diagnostics.SubstantiationProjectInvalid path "substantiation.warnings_as_errors must be a boolean."
        let codes = strings Diagnostics.SubstantiationProjectInvalid path "substantiation.warn_as_error" document false
        if codes |> Array.exists (validWarningCode >> not) then
            refuse Diagnostics.SubstantiationProjectInvalid path "substantiation.warn_as_error must contain diagnostic codes matching ^[A-Z]+[0-9]+$."
        let elevatedCodes = Array.append [| "CCS8500" |] codes |> Array.distinct |> Array.sort
        warningsAsErrors, elevatedCodes

    type private SourceInventory =
        { Digest: SourceDigest
          Text: string
          Activations: string[]
          CallbackAdapters: Set<string>
          AllBindings: Set<string>
          BindingSpans: Map<string, (int * int) * (int * int) * bool>
          BindingExtents: Map<string, int * int>
          ForeignSymbols: Map<string, string * string>
          /// The compile-time declarations typed Expr<FunctionDescriptor>.
          FunctionDescriptors: Set<string>
          BindingDocumentation: Map<string, Set<int * int>>
          AllTypes: Set<string> }

    let private readSource (selectedMetadata: Set<string>) path =
        protect Diagnostics.SubstantiationSourceInvalid path (fun () ->
            ScratchDirectory.canonicalEvidencePath path |> ignore
            if Path.GetExtension path <> ".clef" || not (File.Exists path) then invalidOp "Every build.sources entry must name an existing .clef source."
            let bytes = File.ReadAllBytes path
            let decoded = UTF8Encoding(false, true).GetString bytes
            let text = if decoded.StartsWith("\uFEFF", StringComparison.Ordinal) then decoded.Substring(1) else decoded
            let sourceHash = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
            let lexed = SubstantiationBoundary.scanSource path text
            let externPairs = SubstantiationBoundary.parseFidelityExtern path lexed
            // The shared scanner owns attributes and their attachment. Mask their spans
            // for declaration classification without exposing arguments or changing lines.
            let chars = lexed.CodeSurface.ToCharArray()
            let lineStarts =
                Array.append [| 0 |]
                    (text |> Seq.mapi (fun index ch -> index, ch)
                     |> Seq.choose (fun (index, ch) -> if ch = '\n' then Some(index + 1) else None)
                     |> Seq.toArray)
            let lineAt offset =
                let found = Array.BinarySearch(lineStarts, offset)
                if found >= 0 then found else (~~~found) - 1
            let groups = lexed.Attributes |> Array.distinctBy (fun item -> item.GroupStart)
            for item in groups do
                for index in item.GroupStart .. item.GroupEndExclusive - 1 do
                    if chars[index] <> '\n' && chars[index] <> '\r' then chars[index] <- ' '
            let lines = (String chars).Split('\n')
            let columns = lines |> Array.map (fun line -> line.Length - line.TrimStart().Length)
            for item in groups do
                if item.DeclarationStart >= text.Length then invalidOp "Dangling declaration attributes at the end of a source."
                let declarationLine = lineAt item.DeclarationStart
                if lineAt (item.GroupEndExclusive - 1) = declarationLine then
                    columns[declarationLine] <- min columns[declarationLine] (item.GroupStart - lineStarts[lineAt item.GroupStart])
            let attributes =
                lexed.Attributes
                |> Array.filter (fun item ->
                    let line = lineAt item.DeclarationStart
                    item.DeclarationStart = lineStarts[line] + lines[line].Length - lines[line].TrimStart().Length)
                |> Array.groupBy (fun item -> lineAt item.DeclarationStart)
                |> Map.ofArray
            let declarationAttributes index = Map.tryFind index attributes |> Option.defaultValue [||]
            let documentation =
                lexed.Documentation
                |> Array.groupBy snd
                |> Array.map (fun (declaration, comments) ->
                    declaration,
                    comments |> Array.map (fun (offset, _) ->
                        let line = lineAt offset
                        line + 1, offset - lineStarts[line]) |> Set.ofArray)
                |> Map.ofArray
            let mutable first = 0
            while first < lines.Length && String.IsNullOrWhiteSpace lines[first] do first <- first + 1
            if first = lines.Length || columns[first] <> 0 then invalidOp "An exported file must declare a file-scoped module."
            let moduleLine = lines[first].Trim()
            let fileModule = Regex.Match(moduleLine, @"^module\s+(?:(?:rec|public|internal)\s+)*([A-Za-z_][A-Za-z0-9_'.]*)\s*$")
            if not fileModule.Success then invalidOp "An exported file must declare exactly one file-scoped module."
            let name = fileModule.Groups[1].Value
            let exports = ResizeArray<string>()
            let references = ResizeArray<string>()
            let descriptors = ResizeArray<string>()
            let foreignDeclarations = ResizeArray<string>()
            let allBindings = ResizeArray<string>()
            let bindingSpans = ResizeArray<string * ((int * int) * (int * int) * bool)>()
            let bindingExtents = ResizeArray<string * (int * int)>()
            let foreignSymbols = ResizeArray<string * (string * string)>()
            let functionDescriptors = ResizeArray<string>()
            let bindingDocumentation = ResizeArray<string * Set<int * int>>()
            let allTypes = ResizeArray<string>()
            let callbackEntryRecords = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let callbackAdapters = ResizeArray<string>()
            let descriptorTypes =
                set [ "StructDescriptor"; "PeripheralDescriptor"; "FunctionDescriptor"; "ResultRangeDescriptor"
                      "ResourceDescriptor"; "WritableBufferDescriptor"; "ReadOnlyBufferDescriptor"; "BufferAbsenceContract"
                      "CallbackInvocationContract"; "CallbackDescriptor"; "ScopedCallbackDescriptor"
                      "RetainedCallbackDescriptor"; "ProcessExitCallbackDescriptor"
                      "CallbackAbsenceContract"; "CallbackContextDescriptor"; "CountedEntryArrayDescriptor"
                      "BorrowedResultViewDescriptor"
                      "FlexibleArrayDescriptor"; "MappedReturnDescriptor" ]
            // Literal patterns make CCS verify the actual scalar kind, including
            // an imported abbreviation that shadows an annotation's spelling.
            let scalarPatterns =
                Map.ofList
                    [ "unit", "()"; "bool", "true"; "char", "'\\000'"; "string", "\"\""
                      "int", "0"; "int32", "0"; "int64", "0L"
                      "uint", "0u"; "uint32", "0u"; "uint64", "0UL"
                      "float", "0.0"; "float32", "0.0f"; "decimal", "0M" ]
            let isPrivate (group: Group) = group.Captures |> Seq.cast<Capture> |> Seq.exists (fun capture -> capture.Value = "private")
            let unavailable qualified =
                refuse Diagnostics.SubstantiationActivationUnavailable path
                    $"Exported declaration '{qualified}' requires an explicit admitted primitive scalar annotation, or an exact unit argument with an explicit primitive scalar result. Callable or unknown declarations need a compiler-owned universal activation root."
            // Offside-aligned closing delimiters still belong to their declaration.
            // Only a matching open delimiter grants that continuation; an arbitrary
            // statement at the module column must return to declaration classification.
            let declarationEnd start finish bodyColumn (header: string) =
                let closing = ResizeArray<string>()
                let scan (line: string) =
                    let mutable at = 0
                    let starts (token: string) = at + token.Length <= line.Length && String.CompareOrdinal(line, at, token, 0, token.Length) = 0
                    let push token count = closing.Add token; at <- at + count
                    let pop token count =
                        if closing.Count = 0 || closing[closing.Count - 1] <> token then invalidOp "Mismatched delimiter in source declaration."
                        closing.RemoveAt(closing.Count - 1)
                        at <- at + count
                    while at < line.Length do
                        if starts "``" then
                            let ending = line.IndexOf("``", at + 2, StringComparison.Ordinal)
                            if ending < 0 then invalidOp "A quoted identifier cannot span source lines."
                            at <- ending + 2
                        elif starts "<@@" then push "@@>" 3
                        elif starts "<@" then push "@>" 2
                        elif starts "@@>" then pop "@@>" 3
                        elif starts "@>" then pop "@>" 2
                        else
                            match line[at] with
                            | '(' -> push ")" 1
                            | '[' -> push "]" 1
                            | '{' -> push "}" 1
                            | ')' -> pop ")" 1
                            | ']' -> pop "]" 1
                            | '}' -> pop "}" 1
                            | _ -> at <- at + 1
                let matchingClosingLine (line: string) =
                    let mutable remainder = line.Trim()
                    let mutable position = closing.Count - 1
                    let mutable matched = false
                    while position >= 0 && remainder.StartsWith(closing[position], StringComparison.Ordinal) do
                        remainder <- remainder.Substring(closing[position].Length).TrimStart()
                        position <- position - 1
                        matched <- true
                    matched && (remainder = "" || remainder = "with")
                scan header
                let mutable next = start + 1
                while next < finish &&
                      (String.IsNullOrWhiteSpace lines[next] || columns[next] > bodyColumn
                       || (columns[next] = bodyColumn && matchingClosingLine lines[next])) do
                    scan lines[next]
                    next <- next + 1
                if closing.Count <> 0 then invalidOp "An unclosed declaration delimiter precedes another module statement."
                next
            let rec scope start finish bodyColumn parent parentPrivate =
                let mutable group = ""
                let mutable index = start
                while index < finish do
                    let line = lines[index]
                    if String.IsNullOrWhiteSpace line then index <- index + 1
                    else
                        if columns[index] <> bodyColumn then invalidOp $"Unclassified source block at line {index + 1}."
                        let content = line.Trim()
                        let attached = declarationAttributes index
                        let blockEnd = declarationEnd index finish bodyColumn content
                        let initialBody (inlineBody: string) =
                            if inlineBody.Trim() <> "" then inlineBody.Trim()
                            else lines[index + 1 .. blockEnd - 1] |> Array.tryFind (String.IsNullOrWhiteSpace >> not) |> Option.map _.Trim() |> Option.defaultValue ""
                        let nested = Regex.Match(content, @"^module\s+(?:(rec|public|internal|private)\s+)*([A-Za-z_][A-Za-z0-9_']*)\s*=\s*$")
                        if nested.Success then
                            group <- ""
                            let firstChild = [| index + 1 .. blockEnd - 1 |] |> Array.tryFind (fun child -> not (String.IsNullOrWhiteSpace lines[child]))
                            match firstChild with
                            | Some child -> scope (index + 1) blockEnd columns[child] (parent + "." + nested.Groups[2].Value) (parentPrivate || isPrivate nested.Groups[1])
                            | None -> invalidOp "A nested module must have an indented body."
                        elif Regex.IsMatch(content, @"^open\s+(?:type\s+)?[A-Za-z_][A-Za-z0-9_'.]*\s*$") then
                            if attached.Length <> 0 then invalidOp "Declaration attributes cannot attach to an open directive."
                            if lines[index + 1 .. blockEnd - 1] |> Array.exists (String.IsNullOrWhiteSpace >> not) then invalidOp "An open directive cannot own an indented block."
                            group <- ""
                        elif Regex.IsMatch(content, @"^type\s") || (group = "type" && Regex.IsMatch(content, @"^and\s")) then
                            group <- "type"
                            let declaration = Regex.Match(content, @"^(?:type|and)\s+(?:(private|internal|public)\s+)*(?:(?:'[A-Za-z_][A-Za-z0-9_']*|\(\s*'[A-Za-z_][A-Za-z0-9_']*(?:\s*,\s*'[A-Za-z_][A-Za-z0-9_']*)*\s*\))\s+)?(``[^`]+``|[A-Za-z_][A-Za-z0-9_']*)")
                            if not declaration.Success then invalidOp "An exported type has an unsupported declaration header."
                            let spelling = declaration.Groups[2].Value.Trim([| '`' |])
                            allTypes.Add spelling
                            if scalarPatterns.ContainsKey spelling then
                                refuse Diagnostics.SubstantiationActivationUnavailable path $"Type declaration '{spelling}' shadows an admitted scalar annotation."
                            if not parentPrivate && not (isPrivate declaration.Groups[1]) then
                                let tail = content.Substring(declaration.Length)
                                if Regex.IsMatch(tail, @"^\s*(?:<[^>]*>\s*)?(?:(?:public|internal)\s+)?\(") then unavailable (parent + "." + spelling + ".new")
                                let typeBody = Array.append [| tail |] lines[index + 1 .. blockEnd - 1]
                                let compactBody = (String.concat " " typeBody).Trim()
                                if Regex.IsMatch(compactBody, @"^=\s*\{\s*Invoke\s*:\s*FnPtr\s*<.+>\s*;?\s*\}\s*$") then
                                    callbackEntryRecords.Add spelling |> ignore
                                for bodyLine in typeBody do
                                    let memberLine = bodyLine.Trim()
                                    let members = Regex.Matches(memberLine, @"(?:^|\bwith\s+|\bclass\s+|;\s*)(?:(private|internal|public)\s+)?(?:static\s+)?(member|new|abstract|override|default)\b\s*(?:member\s+)?(?:(private|internal|public)\s+)?")
                                    for memberDeclaration in members do
                                        if memberDeclaration.Groups[1].Value <> "private" && memberDeclaration.Groups[3].Value <> "private" then
                                            unavailable (parent + "." + spelling + "." + memberDeclaration.Groups[2].Value)
                        elif Regex.IsMatch(content, @"^let\s") || (group = "let-rec" && Regex.IsMatch(content, @"^and\s")) then
                            let declaration = Regex.Match(content, @"^(let|and)\s+(?:(rec|inline|mutable|public|internal|private)\s+)*([A-Za-z_][A-Za-z0-9_']*|``[^`]+``)")
                            if not declaration.Success then invalidOp "An exported value has an unsupported binding pattern; substantiation cannot silently omit it."
                            let modifiers = declaration.Groups[2].Captures |> Seq.cast<Capture> |> Seq.map _.Value |> Set.ofSeq
                            group <- if declaration.Groups[1].Value = "and" || modifiers.Contains "rec" then "let-rec" else ""
                            let bindingName = declaration.Groups[3].Value.Trim([| '`' |])
                            allBindings.Add(parent + "." + bindingName)
                            let foreign = attached |> Array.exists SubstantiationBoundary.hasFidelityExtern
                            let letPos = index + 1, line.Length - line.TrimStart().Length
                            let firstPos =
                                if attached.Length = 0 then letPos
                                else
                                    let offset = (attached |> Array.minBy _.GroupStart).GroupStart
                                    let firstLine = lineAt offset
                                    firstLine + 1, offset - lineStarts[firstLine]
                            bindingSpans.Add(parent + "." + bindingName, (firstPos, letPos, foreign))
                            bindingExtents.Add(parent + "." + bindingName, (fst firstPos, blockEnd))
                            match Map.tryFind (lineStarts[index] + snd letPos) documentation with
                            | Some positions -> bindingDocumentation.Add(parent + "." + bindingName, positions)
                            | None -> ()
                            if foreign || (not parentPrivate && not (modifiers.Contains "private")) then
                                let qualified = parent + "." + declaration.Groups[3].Value
                                let exportedName = parent + "." + bindingName
                                let tail = content.Substring(declaration.Length)
                                let quoteType = Regex.Match(tail, @"^\s*:\s*Expr\s*<\s*([A-Za-z_][A-Za-z0-9_.]*)\s*>\s*=\s*(.*)$")
                                let loadingType = Regex.Match(tail, @"^\s*:\s*(?:[A-Za-z_][A-Za-z0-9_]*\.)*ProcessLinkContract\s*=\s*(.*)$")
                                let hasLiteral = attached |> Array.exists (fun item -> item.Head = "Literal")
                                if foreign then
                                    if modifiers.Contains "mutable" then
                                        invalidOp $"Foreign declaration {qualified} carries unsupported modifiers."
                                    let signatureAndBody = Array.append [| tail |] lines[index + 1 .. blockEnd - 1] |> String.concat " "
                                    let equals = signatureAndBody.IndexOf('=')
                                    if equals < 0 then invalidOp $"Foreign declaration {qualified} has no generated placeholder."
                                    let body = signatureAndBody.Substring(equals + 1)
                                    if not (Regex.IsMatch(body, @"^\s*NativeDefault\.zeroed\s*\(\s*\)\s*$")) then
                                        invalidOp $"Foreign declaration {qualified} has an authored or unrecognized body; it cannot be omitted from source checking."
                                    foreignDeclarations.Add exportedName
                                    match (attached |> Array.find SubstantiationBoundary.hasFidelityExtern).Literals with
                                    | [| { IsString = true; Value = Some library }; { IsString = true; Value = Some symbol } |] ->
                                        foreignSymbols.Add(exportedName, (library, symbol))
                                    | _ -> invalidOp $"Foreign declaration {qualified} has no library and symbol literal pair."
                                elif modifiers.Contains "mutable" then
                                    unavailable qualified
                                elif hasLiteral then
                                    if attached.Length <> 1 || not (SubstantiationBoundary.isLiteralAttribute attached[0]) then
                                        invalidOp $"Literal declaration {qualified} has unsupported attributes or arguments."
                                    if declaration.Groups[1].Value <> "let" || modifiers.Contains "rec" || modifiers.Contains "inline" then
                                        unavailable qualified
                                    let literalDecl = Regex.Match(tail, @"^\s*:\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$")
                                    let literalBody =
                                        if not literalDecl.Success then ""
                                        else (Array.append [| literalDecl.Groups[2].Value |] lines[index + 1 .. blockEnd - 1] |> String.concat " ").Trim()
                                    let isConstantLiteral (typeName: string) (body: string) =
                                        match typeName with
                                        | "char" | "string" -> body = "?"
                                        | "bool" -> body = "true" || body = "false"
                                        | "int" | "int32" | "int64" | "uint" | "uint32" | "uint64" | "float" | "float32" | "decimal" ->
                                            Regex.IsMatch(body, @"^[+-]?(?:0[xX][0-9A-Fa-f_]+|0[bB][01_]+|0[oO][0-7_]+|[0-9][0-9_]*(?:\.[0-9_]+)?(?:[eE][+-]?[0-9_]+)?)(?:UL|uL|ul|Lu|lU|u|U|L|f|F|M|m)?$")
                                        | _ -> false
                                    if not literalDecl.Success || not (isConstantLiteral literalDecl.Groups[1].Value literalBody) then
                                        unavailable qualified
                                    descriptors.Add exportedName
                                elif loadingType.Success && selectedMetadata.Contains qualified then
                                    if attached.Length <> 0 || declaration.Groups[1].Value <> "let" || modifiers.Contains "rec" || modifiers.Contains "inline" then
                                        unavailable qualified
                                    if not ((initialBody loadingType.Groups[1].Value).StartsWith("{", StringComparison.Ordinal)) then
                                        invalidOp $"Selected loading declaration {qualified} must have a literal contract body."
                                    descriptors.Add exportedName
                                elif quoteType.Success then
                                    if attached.Length <> 0 || declaration.Groups[1].Value <> "let" || modifiers.Contains "rec" || modifiers.Contains "inline" then
                                        unavailable qualified
                                    let typeName = quoteType.Groups[1].Value.Split('.') |> Array.last
                                    if not (descriptorTypes.Contains typeName) || not ((initialBody quoteType.Groups[2].Value).StartsWith("<@", StringComparison.Ordinal)) then
                                        invalidOp $"Compile-time declaration {qualified} has no admitted descriptor-reading owner."
                                    descriptors.Add exportedName
                                    if typeName = "FunctionDescriptor" then functionDescriptors.Add exportedName
                                elif Regex.IsMatch(tail, @"^\s*=\s*<@") then
                                    invalidOp $"Quotation {qualified} needs an explicit supported descriptor type."
                                else
                                    let activation = Regex.Match(tail, @"^\s*(\(\s*\))?\s*:\s*([A-Za-z_][A-Za-z0-9_]*)\s*=")
                                    match activation.Success, scalarPatterns.TryFind activation.Groups[2].Value with
                                    | true, Some pattern ->
                                        let operand = if activation.Groups[1].Success then qualified + " ()" else qualified
                                        exports.Add exportedName
                                        references.Add($"(match {operand} with | {pattern} -> () | _ -> ())")
                                    | _ ->
                                        let nativeSymbol = bindingName + "Native"
                                        let nativeQualified = parent + "." + nativeSymbol
                                        let hasPrivateNativeExtern =
                                            foreignSymbols |> Seq.exists (fun (declName, _) -> declName = nativeQualified)
                                        let isCallbackEntryAdapter =
                                            if not hasPrivateNativeExtern || modifiers.Contains "inline" || modifiers.Contains "rec" || declaration.Groups[1].Value <> "let" then false
                                            else
                                                let signatureAndBody = Array.append [| tail |] lines[index + 1 .. blockEnd - 1] |> String.concat " "
                                                let eqIdx = signatureAndBody.IndexOf('=')
                                                if eqIdx < 0 then false
                                                else
                                                    let sigPart = signatureAndBody.Substring(0, eqIdx).Trim()
                                                    let bodyPart = Regex.Replace(signatureAndBody.Substring(eqIdx + 1).Trim(), @"\s+", " ")
                                                    let colonIdx = sigPart.LastIndexOf(':')
                                                    if colonIdx < 0 then false
                                                    else
                                                        let paramPart = sigPart.Substring(0, colonIdx).Trim()
                                                        let paramMatches =
                                                            Regex.Matches(paramPart, @"\(\s*(``[^`]+``|[A-Za-z_][A-Za-z0-9_']*)\s*:\s*([^()]+?)\s*\)")
                                                            |> Seq.cast<Match> |> Seq.toArray
                                                        if paramMatches.Length = 0 then false
                                                        else
                                                            let reconstructedParams =
                                                                paramMatches |> Array.map _.Value |> String.concat " "
                                                            if Regex.Replace(paramPart, @"\s+", " ") <> Regex.Replace(reconstructedParams, @"\s+", " ") then false
                                                            else
                                                                let parsedParams =
                                                                    paramMatches |> Array.map (fun m -> m.Groups[1].Value, m.Groups[2].Value.Trim())
                                                                let hasEntryParam =
                                                                    parsedParams |> Array.exists (fun (_, ty) -> callbackEntryRecords.Contains ty)
                                                                let expectedArgs =
                                                                    parsedParams
                                                                    |> Array.map (fun (pName, ty) ->
                                                                        if callbackEntryRecords.Contains ty then pName + ".Invoke" else pName)
                                                                    |> String.concat " "
                                                                hasEntryParam && bodyPart = (nativeSymbol + " " + expectedArgs)
                                        if isCallbackEntryAdapter then
                                            exports.Add exportedName
                                            callbackAdapters.Add exportedName
                                        else unavailable qualified
                        else invalidOp $"Unrecognized module statement at line {index + 1}; every source declaration must be classified."
                        index <- blockEnd
            scope (first + 1) lines.Length 0 name false
            if foreignDeclarations.Count <> externPairs.Length then
                refuse Diagnostics.SubstantiationExternUnresolved path "Every FidelityExtern attribute must attach to exactly one classified foreign declaration."
            { Digest =
                { Path = Path.GetFullPath path; Sha256 = sourceHash; ModuleName = name
                  RuntimeDeclarations = exports.ToArray(); CompileTimeDeclarations = descriptors.ToArray()
                  ForeignDeclarations = foreignDeclarations.ToArray() }
              Text = text; Activations = references.ToArray(); CallbackAdapters = Set.ofSeq callbackAdapters
              AllBindings = Set.ofSeq allBindings; BindingSpans = Map.ofSeq bindingSpans
              BindingExtents = Map.ofSeq bindingExtents; ForeignSymbols = Map.ofSeq foreignSymbols
              FunctionDescriptors = Set.ofSeq functionDescriptors
              BindingDocumentation = Map.ofSeq bindingDocumentation
              AllTypes = Set.ofSeq allTypes })

    type private PsgInspection =
        { Declarations: DeclarationNode[]
          /// Every PSG node inside a library root module, by node identity.
          Owners: Map<int, PsgNodeOwner> }

    // The compiler owns the post-CCS type and module graph. Its parent edges, rather
    // than pretty-printed (and possibly truncated) child lists, bind that graph to
    // the source classifier and the boundary's measured layouts.
    let private verifyPsg0 path (sources: SourceInventory[]) (boundary: SubstantiationBoundary.BoundaryEvidence) : Result<PsgInspection, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationSourceInvalid path (fun () ->
            let fail message = refuse Diagnostics.SubstantiationSourceInvalid path message
            use document = JsonDocument.Parse(File.ReadAllText path)
            let nodes = document.RootElement.GetProperty("nodes").EnumerateArray() |> Seq.toArray
            let ids = nodes |> Array.map (fun node -> node.GetProperty("id").GetInt32())
            if Array.distinct ids |> Array.length <> ids.Length then fail "The compiler PSG contains duplicate node identities."
            let nodeById = nodes |> Array.map (fun node -> node.GetProperty("id").GetInt32(), node) |> Map.ofArray
            let named constructor (node: JsonElement) =
                let found = Regex.Match(node.GetProperty("kind").GetString(), "^" + constructor + @"\s*\(\s*""([^""]+)""")
                if found.Success then Some found.Groups[1].Value else None
            let parent (node: JsonElement) =
                match node.TryGetProperty "parent" with
                | true, value -> Some(value.GetInt32())
                | _ -> None
            let childrenOf =
                nodes
                |> Array.choose (fun node -> parent node |> Option.map (fun id -> id, node.GetProperty("id").GetInt32()))
                |> Array.groupBy fst
                |> Array.map (fun (id, children) -> id, children |> Array.map snd)
                |> Map.ofArray
            let children id =
                Map.tryFind id childrenOf |> Option.defaultValue [||] |> Array.map (fun child -> nodeById[child])
            let foreignPlaceholder (binding: JsonElement) =
                let bindingRange = binding.GetProperty("range").GetString()
                let direct = children (binding.GetProperty("id").GetInt32())
                let bodies =
                    direct |> Array.collect (fun node ->
                        if Regex.IsMatch(node.GetProperty("kind").GetString(), @"^Lambda\b") then
                            children (node.GetProperty("id").GetInt32())
                        else [| node |])
                bodies |> Array.exists (fun node ->
                    if node.GetProperty("kind").GetString() <> "Literal Unit" then false
                    else
                        let typeText = node.GetProperty("type").GetString()
                        node.GetProperty("range").GetString() = bindingRange
                        || (not (String.IsNullOrWhiteSpace typeText) && not (typeText.Contains("NTUunit", StringComparison.Ordinal))))
            let modules =
                nodes |> Array.choose (fun node -> named "ModuleDef" node |> Option.map (fun name -> node.GetProperty("id").GetInt32(), (name, node)))
                |> Map.ofArray
            let sourcePath (node: JsonElement) =
                match node.TryGetProperty "range" with
                | true, value ->
                    let found = Regex.Match(value.GetString(), @"^(.+):[0-9]+:[0-9]+$")
                    if found.Success then Some(Path.GetFullPath found.Groups[1].Value) else None
                | _ -> None
            let rec moduleIdentity visited id =
                if Set.contains id visited then fail "The compiler PSG module parents contain a cycle."
                let name, node = modules[id]
                match parent node with
                | Some enclosing when modules.ContainsKey enclosing ->
                    let parentName, file, root = moduleIdentity (Set.add id visited) enclosing
                    parentName + "." + name, file, root
                | _ -> name, sourcePath node, id
            let moduleIdentities = modules |> Map.map (fun id _ -> moduleIdentity Set.empty id)
            let verifyForeignType qualified (typeText: string) =
                let reject message = refuse Diagnostics.SubstantiationLayoutInvalid path $"Foreign declaration {qualified}: {message}"
                if String.IsNullOrWhiteSpace typeText then reject "the compiler published no NTU type."
                if Regex.IsMatch(typeText, @"\b(?:TTuple|TAnon)\b") then
                    reject "the compiler resolved an unmeasured tuple or anonymous record."
                let root = Regex.Match(typeText, @"\A\s*(?:TFun|TApp|TNum)\b")
                if not root.Success then reject "the compiler published an unsupported foreign NTU type constructor."
                let skipWhitespace start finish =
                    let mutable index = start
                    while index < finish && Char.IsWhiteSpace typeText[index] do index <- index + 1
                    index
                let closing start finish =
                    let expected = ResizeArray<char>()
                    let mutable index = start
                    let mutable quoted = false
                    let mutable complete = false
                    while index < finish && not complete do
                        let ch = typeText[index]
                        if quoted then
                            if ch = '\\' then index <- index + 1
                            elif ch = '"' then quoted <- false
                        else
                            match ch with
                            | '"' -> quoted <- true
                            | '(' -> expected.Add ')'
                            | '[' -> expected.Add ']'
                            | '{' -> expected.Add '}'
                            | ')' | ']' | '}' ->
                                if expected.Count = 0 || expected[expected.Count - 1] <> ch then reject "the compiler NTU type has mismatched delimiters."
                                expected.RemoveAt(expected.Count - 1)
                                complete <- expected.Count = 0
                            | _ -> ()
                        index <- index + 1
                    if not complete || quoted then reject "the compiler NTU type has an unclosed application."
                    index - 1
                let opening = skipWhitespace root.Length typeText.Length
                if opening >= typeText.Length || typeText[opening] <> '(' then reject "the compiler NTU type has no constructor arguments."
                let ending = closing opening typeText.Length
                if skipWhitespace (ending + 1) typeText.Length <> typeText.Length then reject "the compiler NTU type has an unexpected trailing expression."
                let phantomCarriers = set [ "CHandle"; "BorrowedView"; "MappedRegion" ]
                let rec applications start finish phantom =
                    let mutable index = start
                    while index < finish do
                        if typeText[index] = '"' then
                            index <- index + 1
                            let mutable closed = false
                            while index < finish && not closed do
                                if typeText[index] = '\\' then index <- index + 2
                                elif typeText[index] = '"' then closed <- true; index <- index + 1
                                else index <- index + 1
                            if not closed then reject "the compiler NTU type has an unterminated quoted name."
                        elif Char.IsLetter typeText[index] || typeText[index] = '_' then
                            let first = index
                            while index < finish && (Char.IsLetterOrDigit typeText[index] || typeText[index] = '_') do index <- index + 1
                            if typeText.Substring(first, index - first) = "TApp" then
                                let opening = skipWhitespace index finish
                                if opening >= finish || typeText[opening] <> '(' then reject "the compiler NTU application has no argument tuple."
                                let ending = closing opening finish
                                let recordStart = skipWhitespace (opening + 1) ending
                                if recordStart >= ending || typeText[recordStart] <> '{' then reject "the compiler NTU application has no constructor descriptor."
                                let recordEnd = closing recordStart ending
                                let descriptor = typeText.Substring(recordStart + 1, recordEnd - recordStart - 1)
                                let name = Regex.Match(descriptor, @"\A\s*Name\s*=\s*""([^""]+)""")
                                let layout = Regex.Match(descriptor, @"(?:\A|[\n;])\s*Layout\s*=\s*([A-Za-z]+)\b")
                                if not name.Success || not layout.Success then reject "the compiler NTU constructor lacks its name or layout."
                                let simple = name.Groups[1].Value.Split('.') |> Array.last
                                if not phantom then
                                    if layout.Groups[1].Value = "Union" && not (boundary.Enums |> Array.exists (fun item -> item.Name = simple)) then
                                        reject $"union '{simple}' has no measured enum entry."
                                    if layout.Groups[1].Value = "Record"
                                       && (not boundary.Strata.MeasuredRecords || not (boundary.Layouts |> Array.exists (fun item -> item.Kind = "record" && item.Name = simple))) then
                                        reject $"record '{simple}' has no measured record layout."
                                let comma = skipWhitespace (recordEnd + 1) ending
                                if comma >= ending || typeText[comma] <> ',' then reject "the compiler NTU application has no type argument list."
                                let argumentsStart = skipWhitespace (comma + 1) ending
                                if argumentsStart >= ending || typeText[argumentsStart] <> '[' then reject "the compiler NTU application has no type argument list."
                                let argumentsEnd = closing argumentsStart ending
                                if skipWhitespace (argumentsEnd + 1) ending <> ending then reject "the compiler NTU application has an unexpected argument tail."
                                applications (argumentsStart + 1) argumentsEnd (phantom || phantomCarriers.Contains simple)
                                index <- ending + 1
                        else index <- index + 1
                applications 0 typeText.Length false
            let libraryBindings = ResizeArray<DeclarationNode>()
            let libraryRoots = Collections.Generic.HashSet<int>()
            for source in sources do
                let path = Path.GetFullPath source.Digest.Path
                let roots =
                    moduleIdentities |> Map.toArray
                    |> Array.filter (fun (id, (_, file, root)) -> id = root && file = Some path)
                let root =
                    match roots with
                    | [| id, (name, _, _) |] when name = source.Digest.ModuleName -> id
                    | _ -> fail $"The compiler PSG root module does not match source module '{source.Digest.ModuleName}'."
                libraryRoots.Add root |> ignore
                let bindings = ResizeArray<string * string>()
                for node in nodes do
                    match parent node |> Option.bind (fun id -> Map.tryFind id moduleIdentities) with
                    | Some(moduleName, _, owner) when owner = root ->
                        match named "Binding" node, named "TypeDef" node with
                        | Some name, _ ->
                            let qualified = moduleName + "." + name.Trim([| '`' |])
                            if not (source.AllBindings.Contains qualified) then
                                fail $"The compiler parsed module binding '{qualified}' that the source classifier did not inventory."
                            let firstPos, letPos, isForeign = source.BindingSpans[qualified]
                            let range = Regex.Match(node.GetProperty("range").GetString(), @"^(.+):([0-9]+):([0-9]+)$")
                            if not range.Success then fail $"The compiler published no valid source range for module binding '{qualified}'."
                            let rangeLine, rangeCol = Int32.Parse range.Groups[2].Value, Int32.Parse range.Groups[3].Value
                            // Composer includes XML documentation in binding ranges. Only
                            // exact scanner-owned attached documentation positions qualify;
                            // intervening comments or hidden attributes grant no range.
                            let atDocumentation =
                                source.BindingDocumentation |> Map.tryFind qualified
                                |> Option.exists (Set.contains (rangeLine, rangeCol))
                            if rangeLine < 1 || ((rangeLine, rangeCol) < firstPos && not atDocumentation) || rangeLine > fst letPos then
                                fail $"The compiler attribute range for module binding '{qualified}' disagrees with the source classifier."
                            let placeholder = foreignPlaceholder node
                            if not isForeign && placeholder then
                                refuse Diagnostics.SubstantiationExternUnresolved path $"The compiler lowered module binding '{qualified}' as a foreign declaration that the source classifier did not inventory."
                            if isForeign && not placeholder then
                                refuse Diagnostics.SubstantiationExternUnresolved path $"Foreign declaration '{qualified}' was not lowered as a foreign binding by the compiler."
                            let firstLine, lastLine = source.BindingExtents[qualified]
                            libraryBindings.Add
                                { Qualified = qualified; SourcePath = path; BindingNodeId = node.GetProperty("id").GetInt32()
                                  Range = range.Value; Type = node.GetProperty("type").GetString()
                                  FirstLine = firstLine; LastLine = lastLine
                                  Foreign = Map.tryFind qualified source.ForeignSymbols
                                  Descriptor = DescriptorSymbol.NotDescriptor }
                            bindings.Add(qualified, node.GetProperty("type").GetString())
                        | _, Some name ->
                            let simple = name.Split('.') |> Array.last
                            if not (source.AllTypes.Contains simple) then
                                fail $"The compiler parsed type '{name}' that the source classifier did not inventory."
                        | _ -> ()
                    | _ -> ()
                for foreign in source.Digest.ForeignDeclarations do
                    match bindings |> Seq.filter (fun (name, _) -> name = foreign) |> Seq.toArray with
                    | [| _, typeText |] -> verifyForeignType foreign typeText
                    | _ -> refuse Diagnostics.SubstantiationExternUnresolved path $"Foreign declaration '{foreign}' has no unique module binding in the compiler PSG."
            // A witness attributes a foreign import to the FunctionDescriptor RecordExpr inside a
            // descriptor binding, not to the binding node (#209). Resolve every node through its
            // parent edges to the module-level library binding that encloses it, or to the
            // library module when no binding does. The walk is a loop, not recursion, so stack
            // use is independent of the length of a parent chain; a cycle and a parent identity
            // absent from the PSG are refused (FS9233).
            let bindingById = libraryBindings |> Seq.map (fun declaration -> declaration.BindingNodeId, declaration.Qualified) |> dict
            let libraryModules =
                moduleIdentities |> Map.toSeq |> Seq.filter (fun (_, (_, _, owner)) -> libraryRoots.Contains owner) |> Seq.map fst
                |> Collections.Generic.HashSet
            let resolved = Collections.Generic.Dictionary<int, PsgNodeOwner option>()
            for start in ids do
                let walked = ResizeArray<int>()
                let onWalk = Collections.Generic.HashSet<int>()
                let mutable current = start
                let mutable outcome = ValueNone
                while outcome.IsNone do
                    match resolved.TryGetValue current with
                    | true, known -> outcome <- ValueSome known
                    | _ ->
                        if not (onWalk.Add current) then fail "The compiler PSG parent edges contain a cycle."
                        walked.Add current
                        match bindingById.TryGetValue current with
                        | true, qualified -> outcome <- ValueSome(Some(PsgNodeOwner.Binding(current, qualified)))
                        | _ when libraryModules.Contains current -> outcome <- ValueSome(Some PsgNodeOwner.NonBinding)
                        | _ ->
                            match parent nodeById[current] with
                            | None -> outcome <- ValueSome None
                            | Some enclosing when not (nodeById.ContainsKey enclosing) ->
                                fail $"PSG node {current} names parent {enclosing}, which the compiler PSG does not contain."
                            | Some enclosing -> current <- enclosing
                for id in walked do resolved[id] <- outcome.Value
            // The symbol a FunctionDescriptor declaration describes is the CName field of its
            // record (#209). The compiler publishes that field as a string literal node below
            // the record; the source text at the node's range must spell the same literal, so
            // a change confined to the PSG or to the witness manifest does not change the
            // symbol. The owner map above locates the record without a recursive descent.
            let functionDescriptors = sources |> Array.map _.FunctionDescriptors |> Set.unionMany
            let sourceLines = sources |> Array.map (fun source -> Path.GetFullPath source.Digest.Path, source.Text.Split('\n')) |> Map.ofArray
            let cnameField = Regex(@"\(\s*""CName""\s*,\s*NodeId\s+([0-9]{1,9})\s*\)", RegexOptions.CultureInvariant)
            let descriptorRecords =
                nodes
                |> Array.choose (fun node ->
                    let kind = node.GetProperty("kind").GetString()
                    if not (Regex.IsMatch(kind, @"\ARecordExpr\b")) || not (cnameField.IsMatch kind) then None
                    else
                        match resolved.TryGetValue(node.GetProperty("id").GetInt32()) with
                        | true, Some(PsgNodeOwner.Binding(binding, _)) -> Some(binding, node)
                        | _ -> None)
                |> Array.groupBy fst
                |> Map.ofArray
            let descriptorSymbol (declaration: DeclarationNode) =
                if not (functionDescriptors.Contains declaration.Qualified) then DescriptorSymbol.NotDescriptor
                else
                    match Map.tryFind declaration.BindingNodeId descriptorRecords with
                    | None -> DescriptorSymbol.Unestablished "the compiler PSG holds no record with a CName field below the binding."
                    | Some [| _, record |] ->
                        let field = cnameField.Match(record.GetProperty("kind").GetString())
                        match Map.tryFind (Int32.Parse field.Groups[1].Value) nodeById with
                        | Some literal when parent literal = Some(record.GetProperty("id").GetInt32()) ->
                            let spelled = Regex.Match(literal.GetProperty("kind").GetString(), @"\ALiteral\s*\(\s*String\s+""([^""\\\r\n]+)""\s*\)\s*\z")
                            let position =
                                match literal.TryGetProperty "range" with
                                | true, value when value.ValueKind = JsonValueKind.String -> Regex.Match(value.GetString(), @"^(.+):([0-9]{1,9}):([0-9]{1,9})$")
                                | _ -> Match.Empty
                            if not spelled.Success then
                                DescriptorSymbol.Unestablished "the compiler PSG publishes its CName field as no plain string literal."
                            elif not position.Success || Path.GetFullPath position.Groups[1].Value <> declaration.SourcePath then
                                DescriptorSymbol.Unestablished "the compiler PSG publishes no range in the declaring source for its CName literal."
                            else
                                let line, column = Int32.Parse position.Groups[2].Value, Int32.Parse position.Groups[3].Value
                                let lines = sourceLines[declaration.SourcePath]
                                let expected = "\"" + spelled.Groups[1].Value + "\""
                                if line >= declaration.FirstLine && line <= declaration.LastLine && line <= lines.Length
                                   && column + expected.Length <= lines[line - 1].Length
                                   && String.CompareOrdinal(lines[line - 1], column, expected, 0, expected.Length) = 0 then
                                    DescriptorSymbol.Named spelled.Groups[1].Value
                                else
                                    DescriptorSymbol.Unestablished "the source text at the range of its CName literal does not spell the literal the compiler PSG publishes."
                        | _ -> DescriptorSymbol.Unestablished "its CName field names no child node of the descriptor record."
                    | Some _ -> DescriptorSymbol.Unestablished "the compiler PSG holds more than one record with a CName field below the binding."
            { Declarations = libraryBindings.ToArray() |> Array.map (fun declaration -> { declaration with Descriptor = descriptorSymbol declaration })
              Owners = resolved |> Seq.choose (fun entry -> entry.Value |> Option.map (fun owner -> entry.Key, owner)) |> Map.ofSeq })

    /// Reconcile every witnessed foreign import with the library declaration its source
    /// boundary node belongs to, and collect the witnessed definitions and proof obligations.
    let private verifyWitnessUnits witnessPath (psg: PsgInspection) (sources: SourceInventory[]) (boundary: SubstantiationBoundary.BoundaryEvidence) : Result<Correspondence, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationSourceInvalid witnessPath (fun () ->
            use document = JsonDocument.Parse(File.ReadAllText witnessPath)
            let definitions = ResizeArray<WitnessedDefinition>()
            let imports = ResizeArray<WitnessedImport>()
            let boundaryDeclaration qualified =
                sources |> Array.exists (fun source ->
                    Array.contains qualified source.Digest.ForeignDeclarations
                    || Array.contains qualified source.Digest.CompileTimeDeclarations)
            let textOf (owner: JsonElement) (name: string) =
                match owner.ValueKind, owner.TryGetProperty name with
                | JsonValueKind.Object, (true, value) when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> ""
            let declarationsByNode = psg.Declarations |> Array.map (fun node -> node.BindingNodeId, node) |> Map.ofArray
            let attribute unitId resource declaration (library: string) (symbol: string) =
                match Map.tryFind declaration psg.Owners with
                | Some(PsgNodeOwner.Binding(binding, qualified)) ->
                    if not (boundaryDeclaration qualified) then
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler emitted a foreign import for uninventoried declaration '{qualified}'."
                    if not (boundary.Strata.ForeignCalls && Array.contains symbol boundary.ExternSymbols && Array.contains library boundary.Libraries) then
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import '{qualified}' ({library}, {symbol}) disagrees with the retained boundary."
                    // Membership in the boundary inventory admits every symbol of the library on
                    // every declaration. The import must carry the symbol of the declaration its
                    // node resolves to (#209): the pair of a FidelityExtern declaration, or the
                    // CName of a FunctionDescriptor. No other library binding owns a foreign
                    // import: a binding that is neither (a result contract, another descriptor
                    // kind, a runtime function) declares no symbol to compare, so an import
                    // that resolves to it is refused.
                    match Map.tryFind binding declarationsByNode with
                    | Some { Foreign = Some(declaredLibrary, declaredSymbol) } when (declaredLibrary, declaredSymbol) <> (library, symbol) ->
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import ({library}, {symbol}) names FidelityExtern declaration '{qualified}', which declares ({declaredLibrary}, {declaredSymbol})."
                    | Some { Descriptor = DescriptorSymbol.Named cname } when cname <> symbol ->
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import ({library}, {symbol}) names FunctionDescriptor '{qualified}', whose CName is '{cname}'."
                    | Some { Descriptor = DescriptorSymbol.Unestablished reason } ->
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import ({library}, {symbol}) names FunctionDescriptor '{qualified}', whose CName is not established: {reason}"
                    | Some { Foreign = None; Descriptor = DescriptorSymbol.NotDescriptor } ->
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import ({library}, {symbol}) names declaration '{qualified}', which is neither a FidelityExtern declaration nor a FunctionDescriptor."
                    | Some _ -> ()
                    | None ->
                        refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import ({library}, {symbol}) names declaration '{qualified}', which the compiler PSG inspection did not inventory."
                    imports.Add { Unit = unitId; Symbol = symbol; Library = library; DeclarationNode = declaration; Declaration = qualified; Resource = resource }
                | Some PsgNodeOwner.NonBinding ->
                    refuse Diagnostics.SubstantiationExternUnresolved witnessPath $"The compiler foreign import ({library}, {symbol}) names PSG node {declaration}, which is inside a library module and inside no module binding."
                | None -> ()
            let ownerOf id =
                match Map.tryFind id psg.Owners with
                | Some(PsgNodeOwner.Binding(_, qualified)) -> true, Some qualified
                | Some PsgNodeOwner.NonBinding -> true, None
                | None -> false, None
            for unitNode in document.RootElement.GetProperty("units").EnumerateArray() do
                let unitId = textOf unitNode "id"
                match unitNode.TryGetProperty "definitions" with
                | true, values when values.ValueKind = JsonValueKind.Array ->
                    for definition in values.EnumerateArray() do
                        match definition.GetProperty("sourceNode"), definition.GetProperty("traversalRoot") with
                        | sourceNode, traversalRoot when sourceNode.ValueKind = JsonValueKind.Number && traversalRoot.ValueKind = JsonValueKind.Number ->
                            let sourceInLibrary, declaration = ownerOf (sourceNode.GetInt32())
                            let rootInLibrary, _ = ownerOf (traversalRoot.GetInt32())
                            if sourceInLibrary || rootInLibrary then
                                definitions.Add
                                    { Unit = unitId; Symbol = definition.GetProperty("symbol").GetString()
                                      SourceNode = sourceNode.GetInt32(); TraversalRoot = traversalRoot.GetInt32()
                                      Declaration = declaration; SourceInLibrary = sourceInLibrary; RootInLibrary = rootInLibrary }
                        | _ -> ()
                | _ -> ()
                for importNode in unitNode.GetProperty("imports").EnumerateArray() do
                    match importNode.TryGetProperty "sourceBoundary" with
                    | true, sourceBoundary when sourceBoundary.ValueKind <> JsonValueKind.Null ->
                        attribute unitId false (sourceBoundary.GetProperty("declaration").GetInt32())
                            (sourceBoundary.GetProperty("library").GetString()) (importNode.GetProperty("symbol").GetString())
                    | _ -> ()
                // A resource import names its source boundary either as the PSG node identity
                // or as an object carrying it. Its library and symbol must be present whenever
                // the node is a library node; an absent value cannot match the retained boundary.
                match unitNode.TryGetProperty "resourceImports" with
                | true, values when values.ValueKind = JsonValueKind.Array ->
                    for resourceNode in values.EnumerateArray() do
                        match resourceNode.TryGetProperty "sourceBoundary" with
                        | true, sourceBoundary when sourceBoundary.ValueKind = JsonValueKind.Number ->
                            attribute unitId true (sourceBoundary.GetInt32()) (textOf resourceNode "library") (textOf resourceNode "symbol")
                        | true, sourceBoundary when sourceBoundary.ValueKind = JsonValueKind.Object ->
                            let library =
                                match textOf sourceBoundary "library" with
                                | "" -> textOf resourceNode "library"
                                | named -> named
                            attribute unitId true (sourceBoundary.GetProperty("declaration").GetInt32()) library (textOf resourceNode "symbol")
                        | true, sourceBoundary when sourceBoundary.ValueKind <> JsonValueKind.Null ->
                            refuse Diagnostics.SubstantiationExternUnresolved witnessPath "A witnessed resource import has a source boundary that is neither a PSG node identity nor an object naming one."
                        | _ -> ()
                | _ -> ()
            let sourceProof =
                match document.RootElement.TryGetProperty "proof" with
                | true, proof when proof.ValueKind = JsonValueKind.Object ->
                    match proof.TryGetProperty "source" with
                    | true, source when source.ValueKind = JsonValueKind.Object -> Some(textOf source "Invocation", textOf source "QuerySha256")
                    | _ -> None
                | _ -> None
            let obligations =
                match document.RootElement.TryGetProperty "proof" with
                | true, proof when proof.ValueKind = JsonValueKind.Object ->
                    match proof.TryGetProperty "obligations" with
                    | true, values when values.ValueKind = JsonValueKind.Array ->
                        values.EnumerateArray()
                        |> Seq.map (fun entry ->
                            { Anchor = entry.GetProperty("anchor").GetString()
                              Source = entry.GetProperty("source").GetString()
                              Kind = entry.GetProperty("kind").GetString() })
                        |> Seq.toArray |> Some
                    | _ -> None
                | _ -> None
            for source in sources do
                for adapter in source.CallbackAdapters do
                    let witnessed =
                        definitions |> Seq.exists (fun d -> d.Declaration = Some adapter)
                    if not witnessed then
                        refuse Diagnostics.SubstantiationActivationUnavailable source.Digest.Path
                            $"Exported callback-entry adapter '{adapter}' was not reached by any activated zero-argument library export during substantiation."
            { Declarations = psg.Declarations; Definitions = definitions.ToArray(); Imports = imports.ToArray()
              Obligations = obligations; SourceProof = sourceProof })

    let private table path key document =
        match Toml.getValue key document with
        | Some(TomlValue.Table value) | Some(TomlValue.InlineTable value) -> value
        | None -> Map.empty
        | _ -> refuse Diagnostics.SubstantiationProjectInvalid path $"{key} must be a table."

    let private absolute (directory: string) (path: string) =
        ScratchDirectory.canonicalEvidencePath (Path.Combine(directory, path))

    let private evidencePath path = ScratchDirectory.canonicalEvidencePath path

    let private selectedMetadata path document =
        match Toml.getValue "platform.loading" document with
        | Some(TomlValue.String name) -> Set.singleton name
        | None -> Set.empty
        | _ -> refuse Diagnostics.SubstantiationProjectInvalid path "platform.loading must name an explicit compiler-owned metadata export."

    let private rebaseDependencies (path: string) document =
        let directory = Path.GetDirectoryName path
        table path "dependencies" document
        |> Map.map (fun name value ->
            match value with
            | TomlValue.Table dependency | TomlValue.InlineTable dependency ->
                match Map.tryFind "path" dependency with
                | Some(TomlValue.String relative) ->
                    let resolved = absolute directory relative
                    if not (File.Exists resolved) then refuse Diagnostics.SubstantiationProjectInvalid resolved $"Dependency {name} does not exist."
                    TomlValue.InlineTable(Map.add "path" (TomlValue.String resolved) dependency)
                | _ -> refuse Diagnostics.SubstantiationProjectInvalid path $"Dependency {name} requires an explicit project path."
            | _ -> refuse Diagnostics.SubstantiationProjectInvalid path $"Dependency {name} must be a table.")

    /// Ordinary informational diagnostics are distinct from the CLI's explicit
    /// `[unreachable]` severity demotion. Absolute spans bind demotion to this library,
    /// rather than to unrelated imported metadata modules.
    let validateCompilerDiagnostics (warningsAsErrors: bool) (elevatedCodes: Set<string>) (sourcePaths: string[]) (output: string) =
        protect Diagnostics.SubstantiationCompilerDiagnostics "<compiler output>" (fun () ->
            let elevatedCodes = Set.add "CCS8500" elevatedCodes
            let sources = sourcePaths |> Array.map Path.GetFullPath |> Set.ofArray
            let plain = Regex.Replace(output, "\u001b\\[[0-9;]*m", "")
            let lines = plain.Split('\n')
            for index in 0 .. lines.Length - 1 do
                let diagnostic = Regex.Match(lines[index].TrimEnd('\r'), @"^(.+?):([0-9]+)(?::[0-9]+)?:\s*(error|warning|info)\s+([A-Z]+[0-9]+):\s*(.*)$")
                if diagnostic.Success then
                    let span =
                        if index + 1 < lines.Length then Regex.Match(lines[index + 1], @"^\s*span:\s*(.+)\([0-9]+,[0-9]+\)-")
                        else Match.Empty
                    let source = if span.Success then span.Groups[1].Value else diagnostic.Groups[1].Value
                    let inLibrary =
                        if span.Success then sources.Contains(Path.GetFullPath source)
                        else
                            let resolved = Path.GetFullPath source
                            sources.Contains resolved
                            || (Path.GetFileName source = source && (sources |> Set.exists (fun path -> Path.GetFileName path = source)))
                    let demoted = diagnostic.Groups[5].Value.TrimEnd().EndsWith("[unreachable]", StringComparison.Ordinal)
                    let severity = diagnostic.Groups[3].Value
                    let code = diagnostic.Groups[4].Value
                    if severity = "error"
                       || (severity = "warning" && (warningsAsErrors || elevatedCodes.Contains code))
                       || (inLibrary && demoted) then
                        refuse Diagnostics.SubstantiationCompilerDiagnostics source (lines[index].Trim()))

    /// The body of `validateManifest`. It raises the refusal and returns the correspondence
    /// that `verifyPsg0` and `verifyWitnessUnits` established during the same pass.
    let private validateManifestEvidence (manifest: Manifest) : Correspondence =
            requireIntegrity manifest
            ScratchDirectory.canonicalEvidencePath manifest.FidprojPath |> ignore
            ScratchDirectory.canonicalEvidencePath (Path.GetDirectoryName manifest.FidprojPath) |> ignore
            for path in [ manifest.ArtifactsDirectory; manifest.Harness.Path; manifest.HarnessProject.Path; manifest.CompilerLog.Path; manifest.Psg0.Path; manifest.WitnessUnits.Path ] do
                evidencePath path |> ignore
            for source in manifest.Sources do evidencePath source.Path |> ignore
            for evidence in manifest.Boundary.Files do
                if evidence.Kind <> "clang" then evidencePath evidence.Path |> ignore
            if Path.GetFullPath manifest.Psg0.Path <> Path.GetFullPath(Path.Combine(manifest.ArtifactsDirectory, "01_psg0.json")) then
                refuse Diagnostics.SubstantiationManifestMalformed manifest.Psg0.Path "The retained PSG path does not match ArtifactsDirectory/01_psg0.json."
            checkDigest manifest.Psg0.Path manifest.Psg0.Sha256
            if Path.GetFullPath manifest.WitnessUnits.Path <> Path.GetFullPath(Path.Combine(manifest.ArtifactsDirectory, "10_witness_units.json")) then
                refuse Diagnostics.SubstantiationManifestMalformed manifest.WitnessUnits.Path "The retained witness path does not match ArtifactsDirectory/10_witness_units.json."
            checkDigest manifest.WitnessUnits.Path manifest.WitnessUnits.Sha256
            checkDigest manifest.FidprojPath manifest.FidprojSha256
            let project = loadProject manifest.FidprojPath |> required
            rebaseDependencies manifest.FidprojPath project |> ignore
            let projectWarningsAsErrors, elevatedCodes = warningPolicy manifest.FidprojPath project
            if manifest.WarningsAsErrors <> (not manifest.AllowWarnings && projectWarningsAsErrors)
               || manifest.ElevatedWarningCodes <> elevatedCodes then
                refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath "The retained warning policy differs from the project and recorded CLI switch."
            let listed = strings Diagnostics.SubstantiationProjectInvalid manifest.FidprojPath "build.sources" project true
                         |> Array.map (absolute (Path.GetDirectoryName manifest.FidprojPath))
            if listed <> (manifest.Sources |> Array.map _.Path) then
                refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath "Manifest source membership differs from the full build.sources list."
            let liveSources =
                manifest.Sources |> Array.map (fun source ->
                    checkDigest source.Path source.Sha256
                    let inspected = readSource (selectedMetadata manifest.FidprojPath project) source.Path |> required
                    if inspected.Digest <> source then
                        refuse Diagnostics.SubstantiationManifestMalformed source.Path "The exported module/declaration inventory does not match the source."
                    inspected)
            let psg = verifyPsg0 manifest.Psg0.Path liveSources manifest.Boundary |> required
            let correspondence = verifyWitnessUnits manifest.WitnessUnits.Path psg liveSources manifest.Boundary |> required
            for evidence in [ manifest.Compiler; manifest.Dotnet; manifest.Harness; manifest.HarnessProject; manifest.CompilerLog ] do
                checkDigest evidence.Path evidence.Sha256
            validateCompilerDiagnostics manifest.WarningsAsErrors (Set.ofArray manifest.ElevatedWarningCodes) listed (File.ReadAllText manifest.CompilerLog.Path) |> required
            for evidence in manifest.Boundary.Files do checkDigest evidence.Path evidence.Sha256
            for name, hash in
                [ "06a_obligations.json", manifest.Proof.ObligationsSha256
                  "06b_obligations.smt2", manifest.Proof.QuerySha256
                  "06c_psg_discharge.json", manifest.Proof.ReceiptSha256
                  "06b_premises_sat.smt2", manifest.Proof.PremisesQuerySha256 ] do
                checkDigest (Path.Combine(manifest.ArtifactsDirectory, name)) hash
            checkDigest manifest.Proof.Solver.Executable manifest.Proof.Solver.Sha256
            SubstantiationProof.validateStoredEvidence listed manifest.Harness.Path manifest.Proof manifest.ArtifactsDirectory |> required
            let directory = Path.GetDirectoryName manifest.FidprojPath
            let recordedFile kind =
                match manifest.Boundary.Files |> Array.filter (fun file -> file.Kind = kind) with
                | [||] -> None
                | [| file |] -> Some file.Path
                | _ -> refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath $"Multiple '{kind}' inputs are not admitted."
            let pilot =
                match Toml.getValue "substantiation.pilot" project with
                | Some(TomlValue.String path) -> Some(absolute directory path)
                | None -> recordedFile "pilot"
                | _ -> refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath "substantiation.pilot must be a path."
            let objects =
                table manifest.FidprojPath "link.archives" project |> Map.toArray |> Array.map (fun (name, value) ->
                    match value with
                    | TomlValue.String path -> absolute directory path
                    | _ -> refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath $"link.archives.{name} must be a path.")
            let validationRoot = Path.Combine(ScratchDirectory.canonicalEvidencePath manifest.ArtifactsDirectory, "recheck-boundary")
            let validationWork = Path.Combine(validationRoot, Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory validationWork |> ignore
            try
                let evaluated =
                    SubstantiationBoundary.evaluate
                        { FidprojPath = manifest.FidprojPath; PackageName = manifest.Package
                          LinkLibraries = strings Diagnostics.SubstantiationProjectInvalid manifest.FidprojPath "link.libraries" project false
                          LinkObjects = objects; PilotPath = pilot; LinkProvenancePath = recordedFile "link-provenance"
                          Sources = liveSources |> Array.map (fun source -> source.Digest.Path, source.Text); WorkDirectory = validationWork }
                    |> required
                for evidence in manifest.Boundary.Files do checkDigest evidence.Path evidence.Sha256
                if evaluated <> manifest.Boundary then
                    refuse Diagnostics.SubstantiationManifestMalformed manifest.FidprojPath "The retained boundary differs from fresh evaluation of the live project, sources and measured evidence."
            finally
                Directory.Delete(validationWork, true)
                if Directory.Exists validationRoot && Directory.GetFileSystemEntries(validationRoot).Length = 0 then
                    Directory.Delete validationRoot
            correspondence

    /// Recheck the complete project membership and every retained digest without silently
    /// recompiling or replacing evidence. A parser checks shape; this checks the live inputs.
    let validateManifest (manifest: Manifest) =
        protect Diagnostics.SubstantiationDigestMismatch manifest.FidprojPath (fun () ->
            validateManifestEvidence manifest |> ignore)

    /// `validateManifest`, returning the PSG and witness-unit correspondence that the same
    /// validation pass verified (#209).
    let internal validateManifestCorrespondence (manifest: Manifest) : Result<Correspondence, Diagnostics.Refusal> =
        protect Diagnostics.SubstantiationDigestMismatch manifest.FidprojPath (fun () ->
            validateManifestEvidence manifest)

    let private resolveComposer () =
        let path =
            match Environment.GetEnvironmentVariable "COMPOSER_DLL" with
            | null ->
                let primary =
                    ScratchDirectory.tryCheckoutDirectory ()
                    |> Option.defaultWith (fun () -> refuse Diagnostics.SubstantiationToolFailure "COMPOSER_DLL" "Set COMPOSER_DLL outside the Farscape primary checkout.")
                Path.GetFullPath(Path.Combine(primary, "..", "Composer", "src", "bin", "Debug", "net10.0", "Composer.dll"))
            | value when String.IsNullOrWhiteSpace value -> refuse Diagnostics.SubstantiationToolFailure "COMPOSER_DLL" "COMPOSER_DLL is empty."
            | value -> Path.GetFullPath value
        if not (File.Exists path) then refuse Diagnostics.SubstantiationToolFailure path "Composer.dll is required; set COMPOSER_DLL to its exact path."
        path

    let private resolveDotnet () =
        let candidates =
            (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split(Path.PathSeparator)
            |> Array.map (fun directory -> Path.Combine(directory, "dotnet"))
        candidates
        |> Array.tryFind (fun candidate ->
            File.Exists candidate &&
            (OperatingSystem.IsWindows() ||
             (File.GetUnixFileMode candidate &&& (UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute)) <> enum<UnixFileMode> 0))
        |> Option.map ScratchDirectory.canonicalPath
        |> Option.defaultWith (fun () -> refuse Diagnostics.SubstantiationToolFailure "dotnet" "No executable dotnet host is available on PATH.")

    let private compile dotnet composer project work verbose warningsAsErrors elevatedCodes sourcePaths =
        let arguments =
            [ yield composer; yield "compile"
              if warningsAsErrors then yield "--warn-as-error"
              yield "--no-color"
              yield project; yield "-k"; yield "-o"; yield Path.Combine(work, "substantiation-root") ]
        let info = ProcessLaunch.startInfo dotnet arguments
        info.WorkingDirectory <- work
        let temp = Path.Combine(work, "tool-scratch")
        Directory.CreateDirectory temp |> ignore
        info.Environment["TMPDIR"] <- temp
        info.Environment["NO_COLOR"] <- "1"
        use child = new Process(StartInfo = info)
        if not (child.Start()) then refuse Diagnostics.SubstantiationToolFailure composer "Composer did not start."
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(300000)) then
            child.Kill(true)
            child.WaitForExit()
            refuse Diagnostics.SubstantiationToolFailure composer "Composer timed out after 300 seconds."
        let output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()
        let log = Path.Combine(work, "composer.log")
        File.WriteAllText(log, output)
        if verbose then Console.WriteLine output
        validateCompilerDiagnostics warningsAsErrors elevatedCodes sourcePaths output |> required
        if child.ExitCode <> 0 then
            let plain = Regex.Replace(output, "\u001b\\[[0-9;]*m", "")
            let failures = plain.Split('\n') |> Array.filter (fun line -> line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            let details = if failures.Length > 0 then String.concat "\n" failures else plain
            refuse Diagnostics.SubstantiationCompilerDiagnostics project $"Whole-library compilation was refused (exit {child.ExitCode}; log {log}).\n{details}"
        log

    /// Create a separate root project and retain its complete evidence before publishing.
    let evaluate (request: Request) =
        protect Diagnostics.SubstantiationOutputFailure request.FidprojPath (fun () ->
            let projectPath = evidencePath request.FidprojPath
            for path in [ request.PilotPath; request.LinkProvenancePath; request.WorkDirectory; request.OutputDirectory ] do
                path |> Option.iter (evidencePath >> ignore)
            ScratchDirectory.canonicalEvidencePath (Path.GetDirectoryName projectPath) |> ignore
            ScratchDirectory.canonicalEvidencePath projectPath |> ignore
            let document = loadProject projectPath |> required
            let dependencies = rebaseDependencies projectPath document
            let projectWarningsAsErrors, elevatedCodes = warningPolicy projectPath document
            let warningsAsErrors = not request.AllowWarnings && projectWarningsAsErrors
            let projectHash = ClangToolchain.sha256 projectPath
            let directory = Path.GetDirectoryName projectPath
            let package =
                match Toml.getString "package.name" document with
                | Some name when not (String.IsNullOrWhiteSpace name) && Path.GetFileName name = name -> name
                | _ -> refuse Diagnostics.SubstantiationProjectInvalid projectPath "package.name must be a nonempty filename component."
            let sourcePaths = strings Diagnostics.SubstantiationProjectInvalid projectPath "build.sources" document true |> Array.map (absolute directory)
            if sourcePaths.Length = 0 || (Array.distinct sourcePaths).Length <> sourcePaths.Length then
                refuse Diagnostics.SubstantiationProjectInvalid projectPath "build.sources must name a nonempty, nonduplicate set of exported .clef files."
            let sources = sourcePaths |> Array.map (readSource (selectedMetadata projectPath document) >> required)
            match Toml.getValue "compilation.target" document with
            | Some(TomlValue.String "cpu") -> ()
            | _ -> refuse Diagnostics.SubstantiationProjectInvalid projectPath "Substantiation currently requires an explicit compilation.target = 'cpu'; changing the target would change the library being certified."
            let sourceDigests = sources |> Array.map _.Digest
            if (sourceDigests |> Array.map _.ModuleName |> Array.distinct).Length <> sourceDigests.Length then
                refuse Diagnostics.SubstantiationSourceInvalid projectPath "Two source files declare the same exported module."
            let output = request.OutputDirectory |> Option.defaultValue directory |> ScratchDirectory.canonicalEvidencePath
            let workRoot =
                request.WorkDirectory
                |> Option.defaultWith (fun () -> Path.Combine(output, package + ".substantiation.evidence"))
                |> ScratchDirectory.canonicalEvidencePath
            let work = Path.Combine(workRoot, Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory work |> ignore
            let libraries = strings Diagnostics.SubstantiationProjectInvalid projectPath "link.libraries" document false
            let archives = table projectPath "link.archives" document |> Map.map (fun name value ->
                match value with
                | TomlValue.String path -> TomlValue.String(absolute directory path)
                | _ -> refuse Diagnostics.SubstantiationProjectInvalid projectPath $"link.archives.{name} must be a path.")
            let objects = archives |> Map.toArray |> Array.map (fun (_, v) -> match v with TomlValue.String path -> path | _ -> invalidOp "archive path")
            let pilot =
                match request.PilotPath, Toml.getValue "substantiation.pilot" document with
                | Some path, _ -> Some(evidencePath path)
                | None, Some(TomlValue.String path) -> Some(absolute directory path)
                | None, None -> None
                | _ -> refuse Diagnostics.SubstantiationProjectInvalid projectPath "substantiation.pilot must be a path."
            let boundary =
                SubstantiationBoundary.evaluate
                    { FidprojPath = projectPath; PackageName = package; LinkLibraries = libraries; LinkObjects = objects
                      PilotPath = pilot; LinkProvenancePath = request.LinkProvenancePath
                      Sources = sources |> Array.map (fun source -> source.Digest.Path, source.Text); WorkDirectory = work }
                |> required
            let harnessArchives =
                if boundary.LinkMode = "static-archive" then
                    let files = boundary.Files |> Array.filter (fun file -> file.Kind = "static-archive")
                    if files.Length <> 1 || boundary.Libraries.Length <> 1 then
                        refuse Diagnostics.SubstantiationLinkInvalid projectPath "Static harness linkage requires one exact archive/library pairing."
                    Map.ofList [ boundary.Libraries[0], TomlValue.String files[0].Path ]
                else archives
            let entryPath = Path.Combine(work, "SubstantiationRoot.clef")
            let opens = sourceDigests |> Array.map (fun entry -> "open " + entry.ModuleName)
            let touches = sources |> Array.collect _.Activations |> Array.map (fun name -> "    let _ = " + name)
            File.WriteAllLines(entryPath, Array.concat [ [| "module Farscape.SubstantiationRoot"; "" |]; opens; [| ""; "[<EntryPoint>]"; "let main (args: string array) : int =" |]; touches; [| "    0" |] ])
            let build = table projectPath "build" document |> Map.add "sources" (TomlValue.Array(Array.append sourcePaths [| entryPath |] |> Array.map TomlValue.String |> List.ofArray)) |> Map.add "output_kind" (TomlValue.String "console")
            let rootDocument =
                document
                |> Map.add "build" (TomlValue.Table build)
                |> Map.add "package" (TomlValue.Table(table projectPath "package" document |> Map.add "name" (TomlValue.String "FarscapeSubstantiationRoot")))
                |> Map.add "dependencies" (TomlValue.Table dependencies)
                |> Map.add "compilation" (TomlValue.Table(table projectPath "compilation" document |> Map.add "target" (TomlValue.String "cpu")))
                |> Map.add "link" (TomlValue.Table(table projectPath "link" document
                    |> Map.add "archives" (TomlValue.InlineTable harnessArchives)
                    |> Map.add "libraries" (TomlValue.Array(boundary.Libraries |> Array.map TomlValue.String |> List.ofArray))))
            let harnessProject = Path.Combine(work, "SubstantiationRoot.fidproj")
            File.WriteAllText(harnessProject, Toml.serialize rootDocument)
            let composer = resolveComposer ()
            let compilerDigest = digest composer
            let dotnet = resolveDotnet ()
            let dotnetDigest = digest dotnet
            let log =
                protect Diagnostics.SubstantiationToolFailure composer (fun () ->
                    ScratchDirectory.within work (fun () -> compile dotnet composer harnessProject work request.Verbose warningsAsErrors (Set.ofArray elevatedCodes) sourcePaths))
                |> required
            checkDigest composer compilerDigest.Sha256
            checkDigest dotnet dotnetDigest.Sha256
            let artifacts = Path.Combine(work, "targets", "intermediates")
            let psg0Path = evidencePath (Path.Combine(artifacts, "01_psg0.json"))
            if not (File.Exists psg0Path) then
                refuse Diagnostics.SubstantiationDigestMismatch psg0Path "The compiler did not retain its post-CCS PSG evidence."
            let psg0 = digest psg0Path
            let witnessPath = evidencePath (Path.Combine(artifacts, "10_witness_units.json"))
            if not (File.Exists witnessPath) then
                refuse Diagnostics.SubstantiationDigestMismatch witnessPath "The compiler did not retain its witness-unit evidence."
            let witnessUnits = digest witnessPath
            let psg = verifyPsg0 psg0Path sources boundary |> required
            verifyWitnessUnits witnessPath psg sources boundary |> required |> ignore
            let proof = SubstantiationProof.evaluate sourcePaths entryPath artifacts |> required
            let manifest =
                seal
                    { SchemaVersion = 1; Package = package; FidprojPath = projectPath; FidprojSha256 = projectHash
                      Sources = sourceDigests; Compiler = compilerDigest; Dotnet = dotnetDigest; Harness = digest entryPath; HarnessProject = digest harnessProject
                      AllowWarnings = request.AllowWarnings; WarningsAsErrors = warningsAsErrors; ElevatedWarningCodes = elevatedCodes
                      CompilerLog = digest log; Psg0 = psg0; WitnessUnits = witnessUnits; ArtifactsDirectory = artifacts; Proof = proof; Boundary = boundary; IntegritySha256 = "" }
            validateManifest manifest |> required
            Directory.CreateDirectory output |> ignore
            File.WriteAllText(Path.Combine(output, package + ".substantiation.toml"), toToml manifest)
            File.WriteAllText(Path.Combine(output, package + ".substantiation.json"), toJson manifest)
            manifest)
