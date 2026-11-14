namespace Farscape.Core

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Fidelity.Data.TOML

/// The substantiation report of one substantiated library (#209): JSON, Markdown and HTML
/// renderings of one typed record. Every input is revalidated against the files on disk
/// before anything is written. The report states only what the manifest, the Composer
/// receipts, the witness units and the optional Rocq and seam receipts establish.
module SubstantiationDossier =
    module RocqProject = FSharp.Data.Rocq.Project
    module RocqStructure = FSharp.Data.Rocq.Structure
    module RocqSource = FSharp.Data.Rocq.Source

    type Request =
        { ManifestPath: string
          OutputDirectory: string option
          RocqProjectPath: string option
          RocqFactsPath: string option
          RocqConfigPath: string option
          SeamChecksReceiptPath: string option
          RefusalControlsReceiptPath: string option }

    type SeamCheckEntry = SubstantiationSeams.SeamCheckEntry
    type SeamChecksReceipt = SubstantiationSeams.SeamChecksReceipt
    type RefusalControlEntry = SubstantiationSeams.RefusalControlEntry
    type RefusalControlsReceipt = SubstantiationSeams.RefusalControlsReceipt

    [<CLIMutable>]
    type RocqCorrespondenceEntry =
        { RocqSymbol: string
          Targets: string[] }

    [<CLIMutable>]
    type RocqDossierConfig =
        { SchemaVersion: int
          FalsifiedSuffix: string
          CheckMarker: string
          Correspondence: RocqCorrespondenceEntry[] }

    [<CLIMutable>]
    type DossierBinderNote = { Name: string; Note: string }

    /// One public Rocq theorem statement. `Group` is Open, Falsified, Checks or Guarantees.
    [<CLIMutable>]
    type DossierRocqEntry =
        { Symbol: string
          Source: string
          Keyword: string
          Group: string
          Summary: string
          Proposition: string
          BinderNotes: DossierBinderNote[]
          /// closed, rests-on or not-reported.
          Assumptions: string
          Axioms: string[]
          TrustScope: string
          CorrespondsTo: string[] }

    [<CLIMutable>]
    type DossierSeamCategory =
        { Category: string
          Description: string
          Status: string
          Evidence: string[] }

    [<CLIMutable>]
    type DossierObligation =
        { Declaration: string
          /// True when the obligation's source position lies in no single module binding.
          ModuleLevel: bool
          Anchor: string
          Source: string
          Kind: string
          Guarantee: string
          TrustScope: string }

    [<CLIMutable>]
    type DossierHypothesis =
        { Id: string
          Family: string
          Category: string
          Origin: string
          Statement: string
          TestedBy: string[] }

    [<CLIMutable>]
    type DossierForeignLink =
        { Library: string
          Symbol: string
          Member: string
          MemberSha256: string
          Binding: string }

    /// One exported Clef declaration. `Kind` is runtime, foreign or descriptor.
    [<CLIMutable>]
    type DossierDeclaration =
        { Declaration: string
          Kind: string
          Source: string
          PsgNode: int
          Obligations: string[]
          MlirSymbols: string[]
          ForeignLinks: DossierForeignLink[]
          MeasuredTypes: string[]
          RocqTheorems: string[] }

    [<CLIMutable>]
    type DossierDigest = { Item: string; Path: string; Value: string }

    [<CLIMutable>]
    type DossierRocqUnit =
        { Module: string
          Path: string
          /// The source MD5 the unit's `.glob` records on its DIGEST line.
          SourceMd5: string
          /// The MD5 of the `.glob` file itself.
          GlobMd5: string }

    [<CLIMutable>]
    type DossierLegendRow = { Section: string; Health: string; Meaning: string }

    [<CLIMutable>]
    type DossierCounts =
        { Open: int
          Falsified: int
          Checks: int
          Guarantees: int
          PendingSeamCategories: int }

    [<CLIMutable>]
    type DossierOpen =
        { CompilerOpenObligations: int
          CompilerRefutedObligations: int
          RocqOpen: DossierRocqEntry[] }

    [<CLIMutable>]
    type DossierFalsified =
        { CompilerNonVacuityReceipts: int
          RefusalControlsAttached: bool
          RefusalControls: RefusalControlEntry[]
          RocqFalsified: DossierRocqEntry[] }

    [<CLIMutable>]
    type DossierChecks =
        { PremisesSat: int
          TotalObligations: int
          LibraryPremisesSat: int
          LibraryObligations: int
          CompilerPremiseReceipts: int
          SeamChecksAttached: bool
          SeamTargetTriple: string
          SeamChecks: SeamCheckEntry[]
          SeamCategories: DossierSeamCategory[]
          RocqChecks: DossierRocqEntry[] }

    [<CLIMutable>]
    type DossierGuarantees =
        { TrustScope: string
          LibraryObligations: DossierObligation[]
          HarnessObligations: int
          RocqGuarantees: DossierRocqEntry[] }

    [<CLIMutable>]
    type DossierAssumptions =
        { Hypotheses: DossierHypothesis[]
          RocqAxioms: string[] }

    [<CLIMutable>]
    type DossierTraceability =
        { Declarations: DossierDeclaration[]
          Layouts: SubstantiationBoundary.LayoutSummary[]
          Enums: SubstantiationBoundary.EnumSummary[]
          Digests: DossierDigest[]
          RocqAttached: bool
          RocqVersion: string
          RocqUnits: DossierRocqUnit[] }

    [<CLIMutable>]
    type Dossier =
        { SchemaVersion: int
          Package: string
          Title: string
          OverallStatus: string
          ManifestPath: string
          ManifestIntegritySha256: string
          Counts: DossierCounts
          Legend: DossierLegendRow[]
          Open: DossierOpen
          Falsified: DossierFalsified
          Checks: DossierChecks
          Guarantees: DossierGuarantees
          Assumptions: DossierAssumptions
          Traceability: DossierTraceability }

    type Generated =
        { Dossier: Dossier
          JsonPath: string
          MarkdownPath: string
          HtmlPath: string }

    // =====================================================================
    // Fixed report text. Every heading, status string and guidance paragraph
    // of the Markdown and HTML renderings is one of these 7-bit ASCII values.
    // =====================================================================

    let private titlePrefix = "Farscape Substantiation Report: "
    let private statusComplete = "SUBSTANTIATED -- All compiler safety obligations are proven and all required target seam checks are recorded."
    let private statusSeamPending = "SUBSTANTIATED (COMPILER PROOFS COMPLETE; TARGET SEAM RUNS PENDING) -- All compiler safety obligations are proven; run target binary seam checks in Section 3 before production deployment."
    let private statusPartial = "WARNING: PARTIAL MECHANIZATION -- Compiler safety obligations are proven, but 1 or more companion Rocq theorems remain Admitted (unfinished) in Section 1."
    let private legendHeading = "How to Read This Report (No Formal-Methods Background Required)"

    let private openHeading = "1. Open -- Unfinished / Unproved Items"
    let private falsifiedHeading = "2. Falsified -- Verified Guardrails and Operating Limits"
    let private checksHeading = "3. Checks -- Precondition Sanity and Real-Binary Seam Runs"
    let private guaranteesHeading = "4. Guarantees -- Proven Safety Properties"
    let private assumptionsHeading = "5. External C/OS and Domain Assumptions (Rests on)"
    let private traceabilityHeading = "6. Symbol and Proof Traceability (Correspondence)"

    let private openMeaning = "Safety claims that are not yet proved. Compiler safety obligations must be 0. Do not rely on any unfinished Rocq theorem listed here."
    let private falsifiedMeaning = "Verified counterexamples and compile-time refusal controls (FS9xxx, CCS8xxx). Confirms invalid uses are rejected and shows why preconditions are required."
    let private checksMeaning = "Concrete sanity witnesses confirming preconditions are satisfiable in real callers and testing binary ABI, struct layout, and link behavior on this target."
    let private guaranteesMeaning = "Machine-checked safety properties of this library, each badged as either Self-contained or Depends on external C/OS contract."

    let private openHealthGood = "Good (0 open)"
    let private openHealthWarning = "Warning (unfinished Rocq proof)"
    let private falsifiedHealth = "Good (intentional protection)"
    let private checksHealthGood = "Good (all recorded)"
    let private checksHealthPending = "Action on target change (seam runs pending)"
    let private guaranteesHealth = "Good (proven)"

    let private openGuidance = "What this section means: Items here are unfinished proofs. A substantiated library has 0 open compiler obligations. If any companion Rocq theorem is listed below (Admitted), treat that specific property as unproved until a maintainer finishes the proof."
    let private openNoneRocq = "Companion Rocq open theorems (Admitted): 0. No unfinished proof obligations remain."
    let private openAction = "Caller action: Do not rely on this theorem's claim yet. Maintainer action: Replace Admitted with Qed."

    let private falsifiedGuidance = "What this section means: Items here are GOOD -- they are intentional guardrails, not broken tests. Each entry proves either that the compiler refuses a specific misuse at build time or that dropping a precondition produces a real failure."
    let private nonVacuityHeading = "Compiler non-vacuity guardrails"
    let private nonVacuitySuffix = " verified clause witness(es) confirming each safety conclusion can fail if its precondition is violated."
    let private refusalHeading = "Verified compile-time refusal controls"
    let private refusalNone = "No external compile-time refusal receipt was attached to this dossier run (--refusal-controls)."
    let private counterexampleHeading = "Companion Rocq counterexamples (*_falsified)"
    let private counterexampleNone = "No companion Rocq counterexample theorems attached."
    let private falsifiedFooter = "Caller action: None during normal use. Respect the preconditions documented on the corresponding APIs; if you hit a listed diagnostic code, follow its message."

    let private checksGuidance = "What this section means: Checks are concrete sanity runs, not universal proofs. Precondition witnesses confirm the library's rules are satisfiable in real callers (not vacuously true). Seam runs confirm the linked C/C++ binary matches header sizes, offsets, and calling conventions on this target."
    let private premisesHeading = "Precondition satisfiability witnesses"
    let private seamHeading = "Required foreign binary seam check categories"
    let private rocqChecksHeading = "Companion Rocq check witnesses"
    let private rocqChecksNone = "No companion Rocq check witnesses attached."
    let private checksFooter = "When to re-run: Re-run substantiation and physical seam checks whenever you upgrade the C/C++ library archive or shared object, change C compiler flags, or move to a new OS or CPU architecture."
    let private statusVerified = "VERIFIED"
    let private statusPending = "PENDING TARGET RUN"

    let private guaranteesGuidance = "What this section means: Every guarantee below is backed by a machine-checked proof receipt. Check the Trust Scope badge on each guarantee: Self-contained properties hold unconditionally; properties marked Depends on external C/OS contract hold whenever the listed Section 5 assumptions hold on your target."
    let private libraryObligationsHeading = "Library Obligations"
    let private rocqGuaranteesHeading = "Companion Rocq Guarantees"
    let private selfContained = "Self-contained (no external assumptions)"

    let private assumptionsGuidance = "What this section means: Compiler proofs cannot inspect prebuilt C/C++ machine code semantics on their own. The guarantees in Section 4 that cross the C/C++ boundary hold as long as the target environment satisfies the explicit assumptions below."
    let private assumptionsNone = "None -- this library makes no foreign C/C++ calls and relies on zero external C/OS hypotheses."

    let private traceabilityGuidance = "What this section means: Every public Clef declaration in this library is traced below from its source file through compiler proof obligations, witnessed portable MLIR symbols, linked C/C++ symbols, measured C struct/enum layouts, and companion Rocq theorems."
    let private declarationsHeading = "Public Clef Declarations"
    let private layoutsHeading = "Measured C Struct and Enum Layouts"
    let private digestsHeading = "Artifact and Toolchain Digests"

    let private refusalControlsCategory = "falsification-refusal-controls"

    let private seamDescriptions =
        [ "scalar-handle-link-and-call", "Link and call foreign functions with scalar and opaque-handle arguments"
          "measured-record-abi", "Pass and return by-value C structs matching probed byte size, alignment, and field offsets"
          "static-dynamic-link-elision", "Confirm statically archived symbols link without runtime shared-object dependency"
          "error-sentinel-conformance", "Map foreign errno or integer sentinel failures to typed Clef results"
          "owned-handle-lifecycle", "Acquire and release owned foreign handles exactly once without leak or double-free"
          "barewire-zero-copy", "Read and write shared BAREWire memory views within proven byte bounds and lifetimes"
          "callback-registration-invocation", "Register and invoke Clef callbacks across the C function-pointer ABI"
          refusalControlsCategory, "Confirm invalid foreign declarations or caller misuses fail closed with expected diagnostic codes" ]
        |> Map.ofList

    let private familyCategories =
        [ "H-ABI", "Binary Layout and Calling Convention (H-ABI)"
          "H-Link", "Linked Library Binary Identity (H-Link)"
          "H-Contract", "Foreign Runtime Ownership, Error, and Lifetime Contract (H-Contract)"
          "H-Callback", "Foreign Callback Invocation Contract (H-Callback)" ]
        |> Map.ofList

    /// The seam categories that exercise each hypothesis family, in `deriveContract` order.
    let private familySeams =
        [ "H-ABI", [ "scalar-handle-link-and-call"; "measured-record-abi" ]
          "H-Link", [ "scalar-handle-link-and-call"; "static-dynamic-link-elision" ]
          "H-Contract", [ "error-sentinel-conformance"; "owned-handle-lifecycle"; "barewire-zero-copy" ]
          "H-Callback", [ "callback-registration-invocation" ] ]
        |> Map.ofList

    let private guaranteeTexts =
        [ "ForeignResultCoverage", "Every foreign return sentinel and result branch is exhaustively handled"
          "ResourceConversion", "Foreign handle acquisition and null/validity conversion preserve ownership invariants"
          "ResourceCleanup", "Owned foreign resource is released along every execution path"
          "SnapshotBorrowBound", "Borrowed memory view access stays within proven byte capacity and lifetime"
          "OptionalEntryArgumentConversion", "Optional foreign argument conversion preserves presence and pointer validity"
          "OptionalEntryAbsenceArgumentConversion", "Absent optional foreign argument passes the exact null/sentinel representation" ]

    /// The reader text of one Composer obligation kind. `None` is a witness manifest without
    /// a proof obligation list. Composer writes kinds in kebab-case (`foreign-result-coverage`);
    /// the comparison ignores case and the `-` and `_` separators so that both spellings of one
    /// kind select the same text.
    let guaranteeOf (kind: string option) : string =
        match kind with
        | None -> "Proven compiler safety obligation"
        | Some kind ->
            let key = kind.Replace("-", "").Replace("_", "")
            match guaranteeTexts |> List.tryFind (fun (name, _) -> String.Equals(name, key, StringComparison.OrdinalIgnoreCase)) with
            | Some(_, text) -> text
            | None -> "Proven compiler obligation (" + kind + ")"

    let private compilerTrustScope (hypotheses: SubstantiationBoundary.Hypothesis[]) =
        if hypotheses.Length = 0 then selfContained
        else "Depends on external C/OS contract (Rests on: " + (hypotheses |> Array.map (fun hypothesis -> hypothesis.Id) |> String.concat ", ") + ")"

    let private rocqTrustScope (axioms: string[]) =
        if axioms.Length = 0 then selfContained
        else "Depends on external assumptions (Rests on: " + String.concat ", " axioms + ")"

    // =====================================================================
    // Refusals
    // =====================================================================

    let private fail (code: string) (path: string) (message: string) : 'T =
        Diagnostics.diagnostic code message (Diagnostics.inFile path "dossier input or retained evidence")
        |> Diagnostics.refuse

    /// Every dossier path is subject to the substantiation evidence path policy (FS9252).
    let private evidencePath (path: string) : string =
        try ScratchDirectory.canonicalEvidencePath path
        with :? Diagnostics.RefusalException as refused ->
            fail Diagnostics.DossierOutputFailure path
                (refused.Refusal.Diagnostics |> List.map (fun diagnostic -> diagnostic.Message) |> String.concat " ")

    /// A substantiation refusal reported under the dossier code assigned to its cause. The
    /// original code stays in the message.
    let private recoded (mapping: string -> string) (refusal: Diagnostics.Refusal) : Diagnostics.Refusal =
        match refusal.Diagnostics with
        | [] ->
            Diagnostics.diagnostic Diagnostics.DossierInputMalformed refusal.Text
                (Diagnostics.Located.Unknown "the substantiation refusal carried no diagnostic")
            |> Diagnostics.refusalOf
        | diagnostics ->
            diagnostics
            |> List.map (fun diagnostic ->
                { diagnostic with
                    Diagnostics.Diagnostic.Code = mapping diagnostic.Code
                    Diagnostics.Diagnostic.Message = diagnostic.Message + " (substantiation " + diagnostic.Code + ")" }
                |> Diagnostics.refusalOf)
            |> Diagnostics.joined "\n"

    let private manifestCode (code: string) =
        match code with
        | Diagnostics.SubstantiationDigestMismatch -> Diagnostics.DossierDigestMismatch
        | Diagnostics.SubstantiationOutputFailure -> Diagnostics.DossierOutputFailure
        | Diagnostics.SubstantiationExternUnresolved
        | Diagnostics.SubstantiationSourceInvalid
        | Diagnostics.SubstantiationLayoutInvalid
        | Diagnostics.SubstantiationLinkInvalid
        | Diagnostics.SubstantiationProofInvalid
        | Diagnostics.SubstantiationUndischarged
        | Diagnostics.SubstantiationPremisesMalformed
        | Diagnostics.SubstantiationPremisesUnsat -> Diagnostics.DossierCorrespondenceInvalid
        | _ -> Diagnostics.DossierInputMalformed

    // =====================================================================
    // Typed inputs
    // =====================================================================

    let private readText (label: string) (path: string) : string =
        if not (File.Exists path) then fail Diagnostics.DossierInputMalformed path (label + " does not exist.")
        try File.ReadAllText(path, UTF8Encoding(false, true))
        with error -> fail Diagnostics.DossierInputMalformed path (label + " is unreadable: " + error.Message)

    /// A record input with every field present exactly once, no unknown field and no null.
    let private parseStrict<'T> (label: string) (path: string) : 'T =
        let text = readText label path
        try
            use document = JsonDocument.Parse text
            Substantiation.requireJsonShape typeof<'T> document.RootElement
            JsonSerializer.Deserialize<'T> text
        with
        | :? Diagnostics.RefusalException -> reraise ()
        | error -> fail Diagnostics.DossierInputMalformed path (label + " violates its required JSON schema: " + error.Message)

    let private loadManifest (path: string) : Substantiation.Manifest =
        let text = readText "The substantiation manifest" path
        let parsed =
            match Path.GetExtension(path).ToLowerInvariant() with
            | ".json" -> Substantiation.parseJson text
            | ".toml" -> Substantiation.parseToml text
            | _ -> fail Diagnostics.DossierInputMalformed path "The substantiation manifest must be a .json or .toml file."
        match parsed with
        | Ok manifest -> manifest
        | Error refusal ->
            recoded (fun _ -> Diagnostics.DossierInputMalformed) refusal
            |> Diagnostics.mapDiagnostics (fun diagnostic ->
                { diagnostic with Diagnostics.Diagnostic.Source = Diagnostics.inFile path "substantiation manifest" })
            |> Diagnostics.raiseRefusal

    let private defaultConfig : RocqDossierConfig =
        { SchemaVersion = 1
          FalsifiedSuffix = "_falsified"
          CheckMarker = "*Check, not a guarantee.*"
          Correspondence = [||] }

    let private loadConfig (path: string) : RocqDossierConfig =
        let config = parseStrict<RocqDossierConfig> "The Rocq dossier config" path
        let invalid message = fail Diagnostics.DossierInputMalformed path message
        if config.SchemaVersion <> 1 then invalid "Unsupported Rocq dossier config schema version."
        if String.IsNullOrEmpty config.FalsifiedSuffix then invalid "FalsifiedSuffix must be nonempty."
        if String.IsNullOrEmpty config.CheckMarker then invalid "CheckMarker must be nonempty."
        for entry in config.Correspondence do
            if String.IsNullOrWhiteSpace entry.RocqSymbol then invalid "A Correspondence entry has an empty RocqSymbol."
            if entry.Targets.Length = 0 || (entry.Targets |> Array.exists String.IsNullOrWhiteSpace) then
                invalid ("Correspondence entry '" + entry.RocqSymbol + "' requires nonempty Targets.")
            if (Array.distinct entry.Targets).Length <> entry.Targets.Length then
                invalid ("Correspondence entry '" + entry.RocqSymbol + "' repeats a target.")
        if (config.Correspondence |> Array.map (fun entry -> entry.RocqSymbol) |> Array.distinct).Length <> config.Correspondence.Length then
            invalid "Correspondence names one RocqSymbol more than once."
        config

    type private FactAssumptions =
        | Closed
        | RestsOn of string[]

    type private SymbolFact =
        { Type: string option
          Assumptions: FactAssumptions option }

    type private Facts =
        { RocqVersion: string
          Digests: Map<string, string>
          Symbols: Map<string, SymbolFact> }

    /// `facts.json` as `Rocq.Navigator.Facts.write` produces it: `rocqVersion`, `digests`
    /// (project-relative source path to source MD5) and `symbols` (full Rocq name to an object
    /// with a required `binders` map and optional `type` and `assumptions`). Unlike the
    /// navigator's reader, an unknown field, a duplicate key, a null or an `assumptions` value
    /// other than "closed" or a nonempty string array is refused.
    let private loadFacts (path: string) : Facts =
        let text = readText "The Rocq facts file" path
        let invalid message = fail Diagnostics.DossierInputMalformed path ("The Rocq facts file violates its required JSON schema: " + message)
        try
            use document = JsonDocument.Parse text
            let members (label: string) (admitted: string list) (required: string list) (value: JsonElement) =
                if value.ValueKind <> JsonValueKind.Object then invalid (label + " must be an object.")
                let properties = value.EnumerateObject() |> Seq.toArray
                let names = properties |> Array.map (fun property -> property.Name)
                if (Array.distinct names).Length <> names.Length then invalid (label + " has a duplicate key.")
                if not admitted.IsEmpty then
                    for name in names do
                        if not (List.contains name admitted) then invalid (label + " has unknown field '" + name + "'.")
                for name in required do
                    if not (Array.contains name names) then invalid (label + " lacks required field '" + name + "'.")
                properties
            let text (label: string) (value: JsonElement) =
                if value.ValueKind <> JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()) then
                    invalid (label + " must be a nonempty string.")
                value.GetString()
            let strings (label: string) (value: JsonElement) =
                members label [] [] value |> Array.map (fun property -> property.Name, text (label + "." + property.Name) property.Value) |> Map.ofArray
            let root = document.RootElement
            members "The root" [ "rocqVersion"; "digests"; "symbols" ] [ "rocqVersion"; "digests"; "symbols" ] root |> ignore
            let fact (name: string) (value: JsonElement) =
                let label = "symbols." + name
                members label [ "type"; "binders"; "assumptions" ] [ "binders" ] value |> ignore
                strings (label + ".binders") (value.GetProperty "binders") |> ignore
                let reportedType =
                    match value.TryGetProperty "type" with
                    | true, kind -> Some(text (label + ".type") kind)
                    | _ -> None
                let assumptions =
                    match value.TryGetProperty "assumptions" with
                    | true, assumptions when assumptions.ValueKind = JsonValueKind.String && assumptions.GetString() = "closed" -> Some Closed
                    | true, assumptions when assumptions.ValueKind = JsonValueKind.Array && assumptions.GetArrayLength() > 0 ->
                        assumptions.EnumerateArray() |> Seq.map (text (label + ".assumptions")) |> Seq.toArray |> RestsOn |> Some
                    | true, _ -> invalid (label + ".assumptions must be \"closed\" or a nonempty array of axiom names.")
                    | _ -> None
                ({ Type = reportedType; Assumptions = assumptions } : SymbolFact)
            let symbols =
                members "symbols" [] [] (root.GetProperty "symbols")
                |> Array.map (fun property -> property.Name, fact property.Name property.Value)
                |> Map.ofArray
            ({ RocqVersion = text "rocqVersion" (root.GetProperty "rocqVersion")
               Digests = strings "digests" (root.GetProperty "digests")
               Symbols = symbols } : Facts)
        with
        | :? Diagnostics.RefusalException -> reraise ()
        | error -> invalid error.Message

    // =====================================================================
    // Live digests
    // =====================================================================

    let private requireLive (path: string) (expected: string) =
        if not (File.Exists path) || isNull expected || not (Regex.IsMatch(expected, @"\A[a-fA-F0-9]{64}\z"))
           || not (String.Equals(ClangToolchain.sha256 path, expected, StringComparison.OrdinalIgnoreCase)) then
            fail Diagnostics.DossierDigestMismatch path "The file is missing or its SHA-256 differs from the digest the substantiation manifest records."

    /// The UTF-8 text of a retained file whose bytes hash to the recorded digest at this read.
    let private readVerified (path: string) (expected: string) : string =
        let bytes =
            try File.ReadAllBytes path
            with error -> fail Diagnostics.DossierDigestMismatch path ("The recorded file is unreadable: " + error.Message)
        if not (String.Equals(Convert.ToHexString(SHA256.HashData bytes), expected, StringComparison.OrdinalIgnoreCase)) then
            fail Diagnostics.DossierDigestMismatch path "The file's SHA-256 differs from the digest the substantiation manifest records."
        let text = UTF8Encoding(false, true).GetString bytes
        if text.StartsWith("\uFEFF", StringComparison.Ordinal) then text.Substring 1 else text

    let private proofArtifacts (manifest: Substantiation.Manifest) =
        [ "06a_obligations.json", manifest.Proof.ObligationsSha256
          "06b_obligations.smt2", manifest.Proof.QuerySha256
          "06c_psg_discharge.json", manifest.Proof.ReceiptSha256
          "06b_premises_sat.smt2", manifest.Proof.PremisesQuerySha256
          "06b_premises_sat.stdout", manifest.Proof.PremisesOutputSha256 ]
        |> List.map (fun (name, digest) -> Path.Combine(manifest.ArtifactsDirectory, name), digest)

    /// Every file the manifest records, checked before any deeper validation: an invalid or
    /// cyclic symbolic link is FS9252, a missing or changed file is FS9248. The Clang executable
    /// is a host tool outside the evidence path policy; its digest is still checked.
    let private requireLiveDigests (manifest: Substantiation.Manifest) =
        let recorded =
            [ yield manifest.FidprojPath, manifest.FidprojSha256, true
              for source in manifest.Sources do yield source.Path, source.Sha256, true
              yield manifest.Compiler.Path, manifest.Compiler.Sha256, true
              yield manifest.Dotnet.Path, manifest.Dotnet.Sha256, true
              yield manifest.Harness.Path, manifest.Harness.Sha256, true
              yield manifest.HarnessProject.Path, manifest.HarnessProject.Sha256, true
              yield manifest.CompilerLog.Path, manifest.CompilerLog.Sha256, true
              yield manifest.Psg0.Path, manifest.Psg0.Sha256, true
              yield manifest.WitnessUnits.Path, manifest.WitnessUnits.Sha256, true
              for file in manifest.Boundary.Files do yield file.Path, file.Sha256, file.Kind <> "clang"
              yield manifest.Proof.Solver.Executable, manifest.Proof.Solver.Sha256, true
              for stage in manifest.Proof.StageReceipts do yield stage.Path, stage.Sha256, true
              for path, digest in proofArtifacts manifest do yield path, digest, true ]
        evidencePath manifest.ArtifactsDirectory |> ignore
        for path, _, policed in recorded do
            if policed then evidencePath path |> ignore
        for path, digest, _ in recorded do requireLive path digest

    // =====================================================================
    // Seam check and refusal control receipts
    // =====================================================================

    let readSeamReceipt = SubstantiationSeams.readSeamReceipt
    let readRefusalReceipt = SubstantiationSeams.readRefusalReceipt

    // =====================================================================
    // Compiler correspondence
    // =====================================================================

    type private LinkBinding = { Member: string; MemberSha256: string; Binding: string }

    type private LinkProvenance =
        | StaticArchive of library: string * bindings: Map<string, LinkBinding>
        | SharedLibrary of libraries: string list

    let private loadLinkProvenance (file: SubstantiationBoundary.FileDigest) : LinkProvenance =
        let invalid message = fail Diagnostics.DossierCorrespondenceInvalid file.Path message
        let document =
            match Toml.parse (readVerified file.Path file.Sha256) with
            | Ok document -> document
            | Error reason -> invalid ("The link provenance is no valid TOML: " + reason)
        let text (table: Map<string, TomlValue>) (key: string) =
            match Map.tryFind key table with
            | Some(TomlValue.String value) when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> invalid ("The link provenance requires a nonempty string '" + key + "'.")
        let artifact =
            match Toml.getValue "artifact" document with
            | Some(TomlValue.Table table) | Some(TomlValue.InlineTable table) -> table
            | _ -> invalid "The link provenance has no [artifact] table."
        match text artifact "kind" with
        | "static-archive" ->
            let bindings =
                match Toml.getValue "bindings" document with
                | Some(TomlValue.Array values) ->
                    values |> List.map (fun value ->
                        match value with
                        | TomlValue.Table table | TomlValue.InlineTable table ->
                            text table "symbol", { Member = text table "member"; MemberSha256 = text table "member_sha256"; Binding = text table "binding" }
                        | _ -> invalid "A link provenance [[bindings]] entry is no table.")
                | None -> []
                | _ -> invalid "The link provenance [[bindings]] value is no table array."
            if (bindings |> List.map fst |> List.distinct).Length <> bindings.Length then invalid "The link provenance binds one symbol more than once."
            StaticArchive(text artifact "library", Map.ofList bindings)
        | "shared-library" ->
            match Map.tryFind "libraries" artifact with
            | Some(TomlValue.Array values) ->
                values |> List.map (fun value ->
                    match value with
                    | TomlValue.String library -> library
                    | _ -> invalid "The link provenance artifact.libraries value holds a non-string.")
                |> SharedLibrary
            | _ -> invalid "A shared-library link provenance requires artifact.libraries."
        | kind -> invalid ("Unknown link provenance artifact kind '" + kind + "'.")

    type private CompilerEvidence =
        { Declarations: DossierDeclaration[]
          Obligations: DossierObligation[] }

    let private sourceLocation = Regex(@"\A(?<path>.+):(?<line>[0-9]+):(?<column>[0-9]+)\z", RegexOptions.CultureInvariant)

    /// Refuse every witnessed import that the declaration it names does not own (#209). A
    /// FidelityExtern declaration owns the import of its own (library, symbol), and a
    /// FunctionDescriptor owns the import of its established CName. No other module binding
    /// of the library owns an import: a binding that is neither (a result contract, another
    /// descriptor kind, a runtime function) declares no symbol to compare. A name that is no
    /// unique module binding owns none either.
    let internal requireImportOwners (witnessPath: string) (correspondence: Substantiation.Correspondence) : unit =
        let invalid (message: string) : unit = fail Diagnostics.DossierCorrespondenceInvalid witnessPath message
        let externs =
            correspondence.Declarations |> Array.choose (fun declaration -> declaration.Foreign) |> Set.ofArray
        for import in correspondence.Imports do
            let pair = "(" + import.Library + ", " + import.Symbol + ")"
            if not (externs.Contains((import.Library, import.Symbol))) then
                invalid ("Witnessed import " + pair + " matches no FidelityExtern declaration of the library.")
            match correspondence.Declarations |> Array.filter (fun declaration -> declaration.Qualified = import.Declaration) with
            | [| { Foreign = Some(library, symbol) } |] when (library, symbol) <> (import.Library, import.Symbol) ->
                invalid ("Witnessed import " + pair + " names FidelityExtern declaration '" + import.Declaration + "', which declares (" + library + ", " + symbol + ").")
            | [| { Descriptor = Substantiation.DescriptorSymbol.Named cname } |] when cname <> import.Symbol ->
                invalid ("Witnessed import " + pair + " names FunctionDescriptor '" + import.Declaration + "', whose CName is '" + cname + "'.")
            | [| { Descriptor = Substantiation.DescriptorSymbol.Unestablished reason } |] ->
                invalid ("Witnessed import " + pair + " names FunctionDescriptor '" + import.Declaration + "', whose CName is not established: " + reason)
            | [| { Foreign = None; Descriptor = Substantiation.DescriptorSymbol.NotDescriptor } |] ->
                invalid ("Witnessed import " + pair + " names '" + import.Declaration + "', which is neither a FidelityExtern declaration nor a FunctionDescriptor.")
            | [| _ |] -> ()
            | _ -> invalid ("Witnessed import " + pair + " names '" + import.Declaration + "', which is no unique module binding of the library.")

    /// Reconcile the exported declarations, the PSG bindings, the obligation inventory, the
    /// witnessed definitions and imports, and the link provenance of one validated manifest.
    let private reconcileCompiler
        (manifest: Substantiation.Manifest)
        (correspondence: Substantiation.Correspondence)
        : CompilerEvidence =
        let invalid (path: string) (message: string) = fail Diagnostics.DossierCorrespondenceInvalid path message
        let boundary = manifest.Boundary
        let provenance =
            match boundary.Files |> Array.filter (fun file -> file.Kind = "link-provenance") with
            | [||] -> None
            | [| file |] -> Some(file.Path, loadLinkProvenance file)
            | _ -> invalid manifest.FidprojPath "The manifest records more than one link provenance file."
        let link (origin: string) (library: string) (symbol: string) : DossierForeignLink =
            if not (boundary.Strata.ForeignCalls && Array.contains symbol boundary.ExternSymbols && Array.contains library boundary.Libraries) then
                invalid origin ("Foreign symbol (" + library + ", " + symbol + ") is absent from the manifest's extern symbols or libraries.")
            match provenance with
            | Some(path, StaticArchive(archiveLibrary, bindings)) ->
                if archiveLibrary <> library then
                    invalid path ("Foreign symbol '" + symbol + "' names library '" + library + "', and the link provenance records archive library '" + archiveLibrary + "'.")
                match Map.tryFind symbol bindings with
                | Some binding -> { Library = library; Symbol = symbol; Member = binding.Member; MemberSha256 = binding.MemberSha256; Binding = binding.Binding }
                | None -> invalid path ("Foreign symbol '" + symbol + "' has no [[bindings]] entry naming its defining archive member.")
            | Some(path, SharedLibrary libraries) ->
                if not (List.contains library libraries) then
                    invalid path ("Foreign symbol '" + symbol + "' names library '" + library + "', which the shared link provenance does not list.")
                { Library = library; Symbol = symbol; Member = ""; MemberSha256 = ""; Binding = "runtime-shared-library" }
            | None -> { Library = library; Symbol = symbol; Member = ""; MemberSha256 = ""; Binding = boundary.LinkMode }
        // Obligation inventory against the witness manifest and the discharge receipt.
        let obligationsPath, obligationsDigest = (proofArtifacts manifest)[0]
        let inventory =
            match SubstantiationProof.obligationInventory (readVerified obligationsPath obligationsDigest) with
            | Ok rows -> rows
            | Error refusal -> Diagnostics.raiseRefusal (recoded (fun _ -> Diagnostics.DossierCorrespondenceInvalid) refusal)
        let kinds =
            match correspondence.Obligations with
            | None -> None
            | Some witnessed ->
                if witnessed.Length <> inventory.Length then
                    invalid manifest.WitnessUnits.Path ("The witness manifest lists " + string witnessed.Length + " proof obligations and the obligation inventory lists " + string inventory.Length + ".")
                let byAnchor = witnessed |> Array.map (fun obligation -> obligation.Anchor, obligation) |> Map.ofArray
                if byAnchor.Count <> witnessed.Length then invalid manifest.WitnessUnits.Path "The witness manifest lists one obligation anchor more than once."
                for anchor, source, kind in inventory do
                    match Map.tryFind anchor byAnchor with
                    | Some obligation when obligation.Source = source && not (String.IsNullOrWhiteSpace obligation.Kind) && (kind = "" || kind = obligation.Kind) -> ()
                    | Some _ -> invalid manifest.WitnessUnits.Path ("Witnessed obligation '" + anchor + "' disagrees with the obligation inventory on its source or kind, or has an empty kind.")
                    | None -> invalid manifest.WitnessUnits.Path ("Obligation '" + anchor + "' is absent from the witness manifest.")
                Some(byAnchor |> Map.map (fun _ obligation -> obligation.Kind))
        match correspondence.SourceProof with
        | Some(invocation, query) ->
            let receiptPath, receiptDigest = (proofArtifacts manifest)[2]
            use receipt = JsonDocument.Parse(readVerified receiptPath receiptDigest)
            let field (name: string) =
                match receipt.RootElement.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> invalid receiptPath ("The discharge receipt has no string '" + name + "'.")
            if not (String.Equals(field "Invocation", invocation, StringComparison.OrdinalIgnoreCase))
               || not (String.Equals(field "QuerySha256", query, StringComparison.OrdinalIgnoreCase)) then
                invalid manifest.WitnessUnits.Path "The source proof receipt embedded in the witness manifest is not the retained 06c_psg_discharge.json receipt."
        | None -> ()
        // Each library obligation belongs to the module binding whose source lines hold it.
        let sourceModules = manifest.Sources |> Array.map (fun source -> Path.GetFullPath source.Path, source.ModuleName) |> Map.ofArray
        let trustScope = compilerTrustScope boundary.RestsOn
        let obligations =
            inventory |> Array.choose (fun (anchor, source, _) ->
                let location = sourceLocation.Match source
                if not location.Success then invalid obligationsPath ("Obligation '" + anchor + "' has no file:line:column source.")
                let path = Path.GetFullPath location.Groups["path"].Value
                match Map.tryFind path sourceModules with
                | None -> None
                | Some moduleName ->
                    let line = Int32.Parse location.Groups["line"].Value
                    let enclosing =
                        correspondence.Declarations
                        |> Array.filter (fun declaration -> declaration.SourcePath = path && declaration.FirstLine <= line && line <= declaration.LastLine)
                    let declaration, moduleLevel =
                        match enclosing with
                        | [||] -> moduleName, true
                        | [| declaration |] -> declaration.Qualified, false
                        | _ -> invalid obligationsPath ("Obligation '" + anchor + "' lies in more than one module binding.")
                    let kind = kinds |> Option.map (fun byAnchor -> byAnchor[anchor])
                    Some
                        ({ Declaration = declaration; ModuleLevel = moduleLevel; Anchor = anchor; Source = source
                           Kind = Option.defaultValue "" kind; Guarantee = guaranteeOf kind; TrustScope = trustScope } : DossierObligation))
        if obligations.Length <> manifest.Proof.LibraryObligations then
            invalid obligationsPath ("The obligation inventory attributes " + string obligations.Length + " obligations to the library sources and the manifest records " + string manifest.Proof.LibraryObligations + ".")
        // Witnessed definitions and imports. A definition below no module binding (a private
        // type member) has no declaration row; its source node and its traversal root must
        // still agree on library membership.
        for definition in correspondence.Definitions do
            if definition.SourceInLibrary <> definition.RootInLibrary then
                invalid manifest.WitnessUnits.Path ("Witnessed definition '" + definition.Symbol + "' has a source node and a traversal root of which only one lies inside the library's modules.")
        requireImportOwners manifest.WitnessUnits.Path correspondence
        let layoutNames =
            Array.append (boundary.Layouts |> Array.map (fun layout -> layout.Name)) (boundary.Enums |> Array.map (fun measured -> measured.Name))
            |> Set.ofArray
        let declarations =
            manifest.Sources |> Array.collect (fun source ->
                let exported =
                    [| for name in source.RuntimeDeclarations do yield name, "runtime"
                       for name in source.ForeignDeclarations do yield name, "foreign"
                       for name in source.CompileTimeDeclarations do yield name, "descriptor" |]
                if (exported |> Array.map fst |> Array.distinct).Length <> exported.Length then
                    invalid source.Path "One declaration is exported under more than one kind."
                exported
                |> Array.map (fun (name, kind) ->
                    let node =
                        match correspondence.Declarations |> Array.filter (fun declaration -> declaration.Qualified = name) with
                        | [| node |] when node.SourcePath = Path.GetFullPath source.Path -> node
                        | _ -> invalid source.Path ("Exported declaration '" + name + "' has no unique module binding of this source in the compiler PSG.")
                    let links =
                        match kind, node.Foreign with
                        | "foreign", Some(library, symbol) -> [| link source.Path library symbol |]
                        | "foreign", None -> invalid source.Path ("Foreign declaration '" + name + "' has no FidelityExtern library and symbol.")
                        | _ ->
                            correspondence.Imports
                            |> Array.filter (fun import -> import.Declaration = name)
                            |> Array.map (fun import -> import.Library, import.Symbol)
                            |> Array.distinct
                            |> Array.map (fun (library, symbol) -> link manifest.WitnessUnits.Path library symbol)
                    let measured =
                        Regex.Matches(node.Type, @"Name\s*=\s*""([^""]+)""")
                        |> Seq.map (fun found -> found.Groups[1].Value.Split('.') |> Array.last)
                        |> Seq.filter layoutNames.Contains
                        |> Seq.distinct |> Seq.sort |> Seq.toArray
                    node.FirstLine,
                    ({ Declaration = name; Kind = kind; Source = node.Range; PsgNode = node.BindingNodeId
                       Obligations = obligations |> Array.filter (fun obligation -> not obligation.ModuleLevel && obligation.Declaration = name) |> Array.map (fun obligation -> obligation.Anchor)
                       MlirSymbols =
                         correspondence.Definitions
                         |> Array.filter (fun definition -> definition.Declaration = Some name)
                         |> Array.map (fun definition -> definition.Symbol) |> Array.distinct |> Array.sort
                       ForeignLinks = links
                       MeasuredTypes = measured
                       RocqTheorems = [||] } : DossierDeclaration))
                |> Array.sortBy fst
                |> Array.map snd)
        { Declarations = declarations; Obligations = obligations }

    // =====================================================================
    // Companion Rocq mechanization
    // =====================================================================

    let private binderItem = Regex(@"\A\s*-\s+\[([^\]\s:]+):\]\s*(.*)\z", RegexOptions.CultureInvariant)

    /// The first paragraph of attached documentation and its `- [name:] note` binder items,
    /// in written order. A note continues over the indented lines that follow its item.
    let private documentationParts (text: string) : string * (string * string) list =
        let lines = text.Replace("\r\n", "\n").Split('\n')
        let summary = ResizeArray<string>()
        let mutable index = 0
        while index < lines.Length && String.IsNullOrWhiteSpace lines[index] do index <- index + 1
        while index < lines.Length && not (String.IsNullOrWhiteSpace lines[index]) && not (binderItem.IsMatch lines[index]) do
            summary.Add(lines[index].Trim())
            index <- index + 1
        let joined = String.Join(" ", summary)
        let joined =
            if joined.Length > 4 && joined.StartsWith("**", StringComparison.Ordinal) && joined.EndsWith("**", StringComparison.Ordinal) then
                joined.Substring(2, joined.Length - 4).Trim()
            else joined
        let notes = ResizeArray<string * StringBuilder>()
        let mutable continuing = false
        for line in lines do
            let item = binderItem.Match line
            if item.Success then
                notes.Add(item.Groups[1].Value, StringBuilder(item.Groups[2].Value.Trim()))
                continuing <- true
            elif continuing && not (String.IsNullOrWhiteSpace line) && Char.IsWhiteSpace line[0] && not (Regex.IsMatch(line, @"\A\s*-\s")) then
                let _, note = notes[notes.Count - 1]
                note.Append(' ').Append(line.Trim()) |> ignore
            else continuing <- false
        joined, notes |> Seq.map (fun (name, note) -> name, note.ToString()) |> List.ofSeq

    let private withoutRocqComments (text: string) =
        let builder = StringBuilder()
        let mutable depth = 0
        let mutable quoted = false
        let mutable index = 0
        while index < text.Length do
            let ch = text[index]
            let pair (first: char) (second: char) = ch = first && index + 1 < text.Length && text[index + 1] = second
            if depth = 0 && quoted then
                builder.Append ch |> ignore
                if ch = '"' then quoted <- false
                index <- index + 1
            elif pair '(' '*' then
                depth <- depth + 1
                index <- index + 2
            elif depth > 0 && pair '*' ')' then
                depth <- depth - 1
                if depth = 0 then builder.Append ' ' |> ignore
                index <- index + 2
            elif depth > 0 then index <- index + 1
            else
                if ch = '"' then quoted <- true
                builder.Append ch |> ignore
                index <- index + 1
        builder.ToString()

    let private isNamePart (ch: char) = Char.IsLetterOrDigit ch || ch = '_' || ch = '\''

    /// The names a binder list binds at bracket depth zero, in written order. The list ends at
    /// a depth-zero ':' or ','. A let-binder `(x := v)` binds no documented name, as
    /// `Rocq.Navigator.Facts.parseBinders` treats it; a pattern binder and an unnamed
    /// generalizing binder bind none either.
    let private binderNames (text: string) : string list =
        let names = ResizeArray<string>()
        let words (segment: string) =
            segment.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun word -> word <> "_" && (Char.IsLetter word[0] || word[0] = '_') && Seq.forall isNamePart word)
        let groupEnd (start: int) =
            let mutable depth = 0
            let mutable index = start
            let mutable found = -1
            while found < 0 && index < text.Length do
                match text[index] with
                | '(' | '{' | '[' -> depth <- depth + 1
                | ')' | '}' | ']' ->
                    depth <- depth - 1
                    if depth = 0 then found <- index
                | _ -> ()
                index <- index + 1
            if found < 0 then text.Length else found
        let topLevelColon (content: string) =
            let mutable depth = 0
            let mutable index = 0
            let mutable found = -1
            while found < 0 && index < content.Length do
                match content[index] with
                | '(' | '{' | '[' -> depth <- depth + 1
                | ')' | '}' | ']' -> depth <- depth - 1
                | ':' when depth = 0 -> found <- index
                | _ -> ()
                index <- index + 1
            found
        let mutable index = 0
        let mutable finished = false
        while not finished && index < text.Length do
            let ch = text[index]
            if ch = ':' || ch = ',' then finished <- true
            elif ch = '(' || ch = '{' || ch = '[' then
                let finish = groupEnd index
                let prefix = if index > 0 then text[index - 1] else ' '
                let content = text.Substring(index + 1, max 0 (finish - index - 1))
                if prefix <> '\'' && not (content.Contains(":=", StringComparison.Ordinal)) then
                    match topLevelColon content with
                    | colon when colon >= 0 -> names.AddRange(words (content.Substring(0, colon)))
                    | _ when prefix <> '`' -> names.AddRange(words content)
                    | _ -> ()
                index <- finish + 1
            elif Char.IsLetter ch || ch = '_' then
                let start = index
                while index < text.Length && isNamePart text[index] do index <- index + 1
                let word = text.Substring(start, index - start)
                if word <> "_" then names.Add word
            else index <- index + 1
        List.ofSeq names

    /// The binders of the `forall` (or U+2200) that opens a written statement type.
    let private quantifierNames (typeText: string) : string list =
        let text = typeText.TrimStart()
        if text.StartsWith("forall", StringComparison.Ordinal) && text.Length > 6 && not (isNamePart text[6]) then binderNames (text.Substring 6)
        elif text.StartsWith("\u2200", StringComparison.Ordinal) then binderNames (text.Substring 1)
        else []

    let rec private isSubsequence (documented: string list) (written: string list) =
        match documented, written with
        | [], _ -> true
        | _, [] -> false
        | name :: rest, candidate :: remaining ->
            if name = candidate then isSubsequence rest remaining else isSubsequence documented remaining

    type private RocqEvidence =
        { Version: string
          Units: DossierRocqUnit[]
          Entries: DossierRocqEntry[] }

    let private publicKeywords = set [ "Theorem"; "Corollary"; "Fact"; "Proposition" ]

    let private loadRocq
        (projectPath: string)
        (factsPath: string)
        (facts: Facts)
        (config: RocqDossierConfig)
        (knownTargets: Set<string>)
        : RocqEvidence =
        let invalid (path: string) (message: string) = fail Diagnostics.DossierRocqInvalid path message
        if File.Exists projectPath then
            let listed =
                try RocqProject.readProject projectPath
                with error -> invalid projectPath ("The _CoqProject file is unreadable: " + error.Message)
            for file in listed do
                evidencePath file.FullPath |> ignore
                evidencePath (Path.ChangeExtension(file.FullPath, ".glob")) |> ignore
        let loaded =
            try RocqProject.loadWith { RocqProject.AllowStale = false } projectPath
            with error -> invalid projectPath ("The companion Rocq project failed to load: " + error.Message)
        let project =
            match loaded with
            | Ok project -> project
            | Error failures ->
                let diagnostics =
                    failures |> Array.toList |> List.map (fun failure ->
                        let code =
                            match failure with
                            | RocqProject.GlobStaleForSource _ -> Diagnostics.DossierDigestMismatch
                            | _ -> Diagnostics.DossierRocqInvalid
                        Diagnostics.diagnostic code (RocqProject.describeFailure failure) (Diagnostics.inFile projectPath "companion Rocq project"))
                diagnostics |> List.map Diagnostics.refusalOf |> Diagnostics.joined "\n" |> Diagnostics.raiseRefusal
        if project.Files.Length = 0 then invalid projectPath "The companion Rocq project lists no .v unit."
        let units =
            project.Files |> Array.map (fun file ->
                match file.Glob |> Option.bind (fun glob -> glob.Digest) with
                | Some recorded ->
                    ({ Module = file.Module; Path = file.FullPath; SourceMd5 = recorded; GlobMd5 = file.GlobDigest } : DossierRocqUnit), file.Path
                | None -> invalid file.FullPath "The .glob of this unit records no source digest.")
        let expected = units |> Array.map snd |> Set.ofArray
        let recorded = facts.Digests |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if expected <> recorded then
            fail Diagnostics.DossierDigestMismatch factsPath
                ("facts.json digests name [" + String.concat ", " recorded + "] and the Rocq project has units [" + String.concat ", " expected + "].")
        for unitEntry, relative in units do
            if not (String.Equals(unitEntry.SourceMd5, facts.Digests[relative], StringComparison.OrdinalIgnoreCase)) then
                fail Diagnostics.DossierDigestMismatch factsPath
                    ("facts.json records digest " + facts.Digests[relative] + " for " + relative + ", and its .glob records " + unitEntry.SourceMd5 + ".")
        let symbols =
            project.Symbols
            |> Array.choose (fun symbol -> symbol.Declaration |> Option.map (fun declaration -> declaration, symbol))
            |> Array.groupBy fst
            |> Array.map (fun (declaration, owned) -> declaration, owned |> Array.map snd)
            |> Map.ofArray
        let lineStarts = project.Files |> Array.map (fun file -> RocqSource.lineStarts file.Model.Bytes)
        let locationAt (fileIndex: int) (offset: int) =
            let found = Array.BinarySearch(lineStarts[fileIndex], offset)
            project.Files[fileIndex].FullPath + ":" + string ((if found >= 0 then found else (~~~found) - 1) + 1)
        // A unit that ends inside a comment, a string, a sentence or a proof is not a unit
        // Rocq compiles, so its declarations and proof endings are not the ones Rocq checked.
        // FSharp.Data.Rocq marks the sentence (Category.Unterminated, Terminated = false),
        // the proof (ProofEnding.Unterminated) and the unit (its diagnostics); a proof that
        // would start after the last sentence gets no proof record, only the diagnostic.
        project.Files |> Array.iteri (fun fileIndex file ->
            for sentence in file.Model.Sentences do
                if not sentence.Terminated || sentence.Category = RocqStructure.Category.Unterminated then
                    invalid (locationAt fileIndex sentence.Span.Start) "The unit ends inside a sentence that has no terminator."
            for declaration in file.Model.Declarations do
                let label = declaration.Name |> Option.defaultValue "<unnamed>"
                if declaration.Category = RocqStructure.Category.Unterminated then
                    invalid (locationAt fileIndex declaration.KeywordSpan.Start) ("The declaration '" + label + "' has no terminator.")
                if declaration.Proofs |> Array.exists (fun proof -> proof.Ending = RocqStructure.ProofEnding.Unterminated) then
                    invalid (locationAt fileIndex declaration.KeywordSpan.Start) ("The unit ends inside the proof of '" + label + "'.")
            for diagnostic in file.Model.Diagnostics do
                if diagnostic.Code = "lex" || diagnostic.Code = "sentence-unterminated" || diagnostic.Code = "proof-unterminated" then
                    invalid (locationAt fileIndex (min diagnostic.Offset file.Model.Bytes.Length)) ("The unit is not terminated: " + diagnostic.Message + "."))
        // Every sentence that admits a proof must be attributed to a declaration, or the
        // unit is refused: the Open group below is built from the proofs of declarations.
        // An Admitted sentence that closes no declaration's proof (an obligation of no
        // Program declaration, a proof opened by a command the structure model gives no
        // proof) and an Admit Obligations command, which admits obligations without one
        // Admitted sentence each, have no declaration to list.
        project.Files |> Array.iteri (fun fileIndex file ->
            let closed =
                file.Model.Declarations
                |> Array.collect (fun declaration -> declaration.Proofs)
                |> Array.filter (fun proof -> proof.Ending = RocqStructure.ProofEnding.Admitted)
                |> Array.map (fun proof -> proof.LastSentence)
                |> Set.ofArray
            for sentence in file.Model.Sentences do
                if not (Array.contains "Fail" sentence.Modifiers) then
                    if sentence.Keyword = Some "Admitted" && not (closed.Contains sentence.Index) then
                        invalid (locationAt fileIndex sentence.Span.Start) "This Admitted sentence closes no proof of a declaration of the unit, so the admitted item cannot be listed."
                    if sentence.Keyword = Some "Admit Obligations" then
                        invalid (locationAt fileIndex sentence.Span.Start) "Admit Obligations admits obligations that no Admitted sentence attributes to a declaration, so the admitted items cannot be listed.")
        let targets = config.Correspondence |> Array.map (fun entry -> entry.RocqSymbol, entry.Targets) |> Map.ofArray
        let entries = ResizeArray<DossierRocqEntry>()
        // The symbols a Correspondence entry may name: the public theorem statements.
        let publicStatements = HashSet<string>()
        project.Declarations |> Array.iteri (fun index owned ->
            let declaration = owned.Declaration
            let file = project.Files[owned.File]
            let bytes = file.Model.Bytes
            let written (span: RocqSource.Span voption) =
                match span with
                | ValueSome range -> RocqSource.text bytes range
                | ValueNone -> ""
            let documentation =
                declaration.Docs |> Array.map (fun comment -> RocqSource.text bytes file.Model.Comments[comment].BodySpan) |> String.concat "\n"
            let summary, notes = documentationParts documentation
            let location = locationAt owned.File declaration.KeywordSpan.Start
            let label = declaration.Name |> Option.defaultValue "<unnamed>"
            // Binder documentation names the statement's written binders, in written order.
            if not notes.IsEmpty && (declaration.Category = RocqStructure.Category.Theorem || declaration.Category = RocqStructure.Category.Definition) then
                let documented = notes |> List.map fst
                let binders = binderNames (withoutRocqComments (written declaration.Binders)) @ quantifierNames (withoutRocqComments (written declaration.Type))
                if (List.distinct documented).Length <> documented.Length then
                    invalid location ("The documentation of '" + label + "' documents one binder more than once.")
                match documented |> List.tryFind (fun name -> not (List.contains name binders)) with
                | Some unknown -> invalid location ("The documentation of '" + label + "' documents binder '" + unknown + "', which the statement does not write.")
                | None -> ()
                if not (isSubsequence documented binders) then
                    invalid location ("The documentation of '" + label + "' documents its binders in an order other than the written order.")
            let symbolName =
                declaration.Name |> Option.bind (fun name ->
                    Map.tryFind index symbols |> Option.defaultValue [||]
                    |> Array.tryFind (fun symbol -> symbol.Symbol.Name = name)
                    |> Option.map (fun symbol -> file.Module + "." + symbol.Symbol.Qualified))
            let statement = Regex.Replace(written declaration.Statement, @"\s+", " ").Trim()
            let entry (name: string) (keyword: string) group (proposition: string) (assumptions: FactAssumptions option) trusted : DossierRocqEntry =
                let axioms =
                    match assumptions with
                    | Some(RestsOn names) -> names
                    | _ -> [||]
                { Symbol = name; Source = location; Keyword = keyword; Group = group
                  Summary = summary; Proposition = proposition
                  BinderNotes = notes |> List.map (fun (binder, note) -> ({ Name = binder; Note = note } : DossierBinderNote)) |> List.toArray
                  Assumptions =
                    match assumptions with
                    | Some Closed -> "closed"
                    | Some(RestsOn _) -> "rests-on"
                    | None -> "not-reported"
                  Axioms = axioms
                  TrustScope = if trusted then rocqTrustScope axioms else ""
                  CorrespondsTo = Map.tryFind name targets |> Option.defaultValue [||] }
            let isPublic =
                declaration.Category = RocqStructure.Category.Theorem
                && (declaration.Keyword |> Option.exists publicKeywords.Contains)
            if declaration.Proofs |> Array.exists (fun proof -> proof.Ending = RocqStructure.ProofEnding.Admitted) then
                // An Admitted proof is open whatever declares it: a Theorem, a Lemma, a
                // Definition built in proof mode, an Instance, a Goal, or the Program
                // declaration that owns an admitted obligation. The public keyword set
                // restricts the proved groups below, never this one.
                let keyword = declaration.Keyword |> Option.defaultValue (RocqStructure.categoryName declaration.Category)
                let name =
                    match symbolName, declaration.Name with
                    | Some qualified, _ -> qualified
                    | None, Some declared -> file.Module + "." + declared
                    | None, None -> invalid location ("An Admitted " + keyword + " declaration has no name, so it cannot be listed.")
                let proposition = Map.tryFind name facts.Symbols |> Option.bind (fun reported -> reported.Type) |> Option.defaultValue statement
                entries.Add(entry name keyword "Open" proposition None false)
                if isPublic then publicStatements.Add name |> ignore
            elif isPublic then
                let keyword = Option.get declaration.Keyword
                let aborted =
                    declaration.Proofs.Length > 0 && declaration.Proofs |> Array.forall (fun proof -> proof.Ending = RocqStructure.ProofEnding.Abort)
                match symbolName with
                | None when aborted -> ()
                | None -> invalid location ("Public " + keyword + " statement has no symbol in the unit's .glob.")
                | Some name ->
                    let reported =
                        match Map.tryFind name facts.Symbols with
                        | Some reported -> reported
                        | None -> invalid factsPath ("Rocq " + keyword + " '" + name + "' is not Admitted and facts.json symbols has no entry for it.")
                    let requireType () =
                        match reported.Type with
                        | Some proposition -> proposition
                        | None -> invalid factsPath ("facts.json reports no type for Rocq " + keyword + " '" + name + "'.")
                    publicStatements.Add name |> ignore
                    if (Option.get declaration.Name).EndsWith(config.FalsifiedSuffix, StringComparison.Ordinal) then
                        entries.Add(entry name keyword "Falsified" (requireType ()) reported.Assumptions false)
                    elif documentation.Contains(config.CheckMarker, StringComparison.Ordinal) then
                        entries.Add(entry name keyword "Checks" (requireType ()) reported.Assumptions false)
                    else
                        match reported.Assumptions with
                        | Some _ -> entries.Add(entry name keyword "Guarantees" (requireType ()) reported.Assumptions true)
                        | None -> invalid factsPath ("facts.json reports no assumptions for Rocq " + keyword + " '" + name + "'."))
        for mapping in config.Correspondence do
            if not (publicStatements.Contains mapping.RocqSymbol) then
                fail Diagnostics.DossierCorrespondenceInvalid projectPath
                    ("Correspondence names Rocq symbol '" + mapping.RocqSymbol + "', which is no public theorem statement of the Rocq project.")
            for target in mapping.Targets do
                if not (knownTargets.Contains target) then
                    fail Diagnostics.DossierCorrespondenceInvalid projectPath
                        ("Correspondence maps '" + mapping.RocqSymbol + "' to '" + target + "', which is no exported Clef declaration, measured layout or enum, or RestsOn hypothesis of the library.")
        { Version = facts.RocqVersion; Units = units |> Array.map fst; Entries = entries.ToArray() }

    // =====================================================================
    // Assembly
    // =====================================================================

    let private jsonOptions = JsonSerializerOptions(WriteIndented = true, NewLine = "\n")

    let toJson (dossier: Dossier) : string = JsonSerializer.Serialize(dossier, jsonOptions) + "\n"

    /// Parse a dossier JSON document. Every field is required exactly once; an unknown field,
    /// a null or another schema version is refused with FS9247.
    let parseDossierJson (text: string) : Result<Dossier, Diagnostics.Refusal> =
        try
            use document = JsonDocument.Parse text
            Substantiation.requireJsonShape typeof<Dossier> document.RootElement
            let dossier = JsonSerializer.Deserialize<Dossier> text
            if dossier.SchemaVersion <> 1 then fail Diagnostics.DossierInputMalformed "<dossier.json>" "Unsupported dossier schema version."
            Ok dossier
        with
        | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
        | error ->
            Diagnostics.diagnostic Diagnostics.DossierInputMalformed ("The dossier violates its required JSON schema: " + error.Message)
                (Diagnostics.inFile "<dossier.json>" "dossier document")
            |> Diagnostics.refusalOf |> Error

    let private build (request: Request) : Dossier * string =
        // 1. Path policy.
        let manifestPath = evidencePath request.ManifestPath
        let output =
            request.OutputDirectory
            |> Option.defaultWith (fun () -> Path.GetDirectoryName(Path.GetFullPath request.ManifestPath))
            |> evidencePath
        let supplied = Option.map evidencePath
        let rocqProjectPath = supplied request.RocqProjectPath
        let rocqFactsPath = supplied request.RocqFactsPath
        let rocqConfigPath = supplied request.RocqConfigPath
        let seamPath = supplied request.SeamChecksReceiptPath
        let refusalPath = supplied request.RefusalControlsReceiptPath
        // 2. Pairing and typed inputs.
        if rocqProjectPath.IsSome <> rocqFactsPath.IsSome then
            fail Diagnostics.DossierInputMalformed (Option.defaultValue "" (Option.orElse rocqFactsPath rocqProjectPath))
                "A companion Rocq project and its facts.json must be supplied together (--rocq-project with --rocq-facts)."
        if rocqConfigPath.IsSome && rocqProjectPath.IsNone then
            fail Diagnostics.DossierInputMalformed rocqConfigPath.Value "A Rocq dossier config requires a companion Rocq project (--rocq-config with --rocq-project)."
        let manifest = loadManifest manifestPath
        let config = rocqConfigPath |> Option.map loadConfig |> Option.defaultValue defaultConfig
        let facts = rocqFactsPath |> Option.map (fun path -> path, loadFacts path)
        let seam = seamPath |> Option.map (fun path -> path, (readSeamReceipt manifest path |> function Ok receipt -> receipt | Error refusal -> Diagnostics.raiseRefusal refusal))
        let refusals = refusalPath |> Option.map (fun path -> path, (readRefusalReceipt manifest path |> function Ok receipt -> receipt | Error refusal -> Diagnostics.raiseRefusal refusal))
        // 3. Live digests, then the complete manifest revalidation.
        requireLiveDigests manifest
        let correspondence =
            match Substantiation.validateManifestCorrespondence manifest with
            | Ok correspondence -> correspondence
            | Error refusal -> Diagnostics.raiseRefusal (recoded manifestCode refusal)
        // 4. Seam check, refusal control, and mandatory co-located seams ledger receipts.
        let seamsLedger =
            let suppliedReceiptPaths =
                [ seam |> Option.map fst; refusals |> Option.map fst ]
                |> List.choose id
            match suppliedReceiptPaths with
            | [] -> None
            | receiptPaths ->
                let candidates =
                    receiptPaths
                    |> List.map (fun receiptPath ->
                        let dir = Path.GetDirectoryName receiptPath
                        if String.IsNullOrWhiteSpace dir then
                            fail Diagnostics.DossierCheckReceiptInvalid receiptPath "Supplied seam or refusal receipt path has no parent directory for its co-located seams ledger."
                        receiptPath, Path.Combine(dir, manifest.Package + ".seams-ledger.json"))
                    |> List.distinctBy (fun (_, ledgerPath) -> Path.GetFullPath ledgerPath)
                match candidates with
                | [ (originReceiptPath, candidate) ] ->
                    let resolvedLedger = evidencePath candidate
                    if not (File.Exists resolvedLedger) then
                        fail Diagnostics.DossierCheckReceiptInvalid originReceiptPath
                            $"Supplied seam or refusal receipt lacks its required co-located {manifest.Package}.seams-ledger.json receipt."
                    let ledger =
                        match SubstantiationSeams.readLedgerReceipt manifest resolvedLedger with
                        | Ok receipt -> receipt
                        | Error refusal ->
                            let mapLedgerCode (code: string) =
                                if code = Diagnostics.DossierOutputFailure then Diagnostics.DossierOutputFailure
                                else Diagnostics.DossierCheckReceiptInvalid
                            Diagnostics.raiseRefusal (recoded mapLedgerCode refusal)
                    match seam with
                    | Some(path, _) ->
                        if Path.GetFullPath path <> Path.GetFullPath ledger.SeamChecksPath
                           || not (String.Equals(ClangToolchain.sha256 path, ledger.SeamChecksSha256, StringComparison.OrdinalIgnoreCase)) then
                            fail Diagnostics.DossierCheckReceiptInvalid path "Supplied seam-checks receipt disagrees with the co-located seams ledger."
                    | None -> ()
                    match refusals with
                    | Some(path, _) ->
                        if Path.GetFullPath path <> Path.GetFullPath ledger.RefusalControlsPath
                           || not (String.Equals(ClangToolchain.sha256 path, ledger.RefusalControlsSha256, StringComparison.OrdinalIgnoreCase)) then
                            fail Diagnostics.DossierCheckReceiptInvalid path "Supplied refusal-controls receipt disagrees with the co-located seams ledger."
                    | None -> ()
                    Some(resolvedLedger, ledger)
                | (firstOrigin, _) :: _ ->
                    fail Diagnostics.DossierCheckReceiptInvalid firstOrigin "Seam check and refusal control receipts resolve to distinct seams ledgers."
                | [] -> None
        // 5. PSG, obligation, witness and link provenance correspondence.
        let compilerOnly = reconcileCompiler manifest correspondence
        // 6. Companion Rocq mechanization.
        let boundary = manifest.Boundary
        let knownTargets =
            Array.concat
                [ manifest.Sources |> Array.collect (fun source ->
                      Array.concat [ source.RuntimeDeclarations; source.ForeignDeclarations; source.CompileTimeDeclarations ])
                  boundary.Layouts |> Array.map (fun layout -> layout.Name)
                  boundary.Enums |> Array.map (fun measured -> measured.Name)
                  boundary.RestsOn |> Array.map (fun hypothesis -> hypothesis.Id) ]
            |> Set.ofArray
        let rocq =
            match rocqProjectPath, facts with
            | Some projectPath, Some(factsPath, facts) -> Some(loadRocq projectPath factsPath facts config knownTargets)
            | _ -> None
        let rocqEntries = rocq |> Option.map (fun evidence -> evidence.Entries) |> Option.defaultValue [||]
        let rocqTheorems =
            rocqEntries
            |> Array.collect (fun entry -> entry.CorrespondsTo |> Array.map (fun target -> target, entry.Symbol))
            |> Array.groupBy fst
            |> Array.map (fun (target, symbols) -> target, symbols |> Array.map snd)
            |> Map.ofArray
        let compiler =
            { compilerOnly with
                Declarations =
                    compilerOnly.Declarations |> Array.map (fun declaration ->
                        { declaration with RocqTheorems = Map.tryFind declaration.Declaration rocqTheorems |> Option.defaultValue [||] }) }
        let group name = rocqEntries |> Array.filter (fun entry -> entry.Group = name)
        let rocqOpen, rocqFalsified, rocqChecks, rocqGuarantees = group "Open", group "Falsified", group "Checks", group "Guarantees"
        // Seam categories.
        let seamChecks = seam |> Option.map (fun (_, receipt) -> receipt.Checks) |> Option.defaultValue [||]
        let refusalControls = refusals |> Option.map (fun (_, receipt) -> receipt.Controls) |> Option.defaultValue [||]
        let targetTriple = seam |> Option.map (fun (_, receipt) -> receipt.TargetTriple) |> Option.defaultValue ""
        let seamCategories =
            boundary.RequiredSeamChecks |> Array.map (fun category ->
                let description =
                    match Map.tryFind category seamDescriptions with
                    | Some description -> description
                    | None -> fail Diagnostics.DossierInputMalformed manifestPath ("The manifest requires unknown seam check category '" + category + "'.")
                let evidence =
                    if category = refusalControlsCategory then
                        refusalControls |> Array.map (fun control -> control.Id + " (" + control.ExpectedCode + ")")
                    else
                        seamChecks |> Array.filter (fun check -> check.Category = category)
                        |> Array.map (fun check -> check.Id + " (binary sha256:" + check.BinarySha256.ToLowerInvariant() + "; target " + targetTriple + ")")
                ({ Category = category; Description = description
                   Status = (if evidence.Length > 0 then statusVerified else statusPending); Evidence = evidence } : DossierSeamCategory))
        let pendingSeams = seamCategories |> Array.filter (fun category -> category.Status = statusPending) |> Array.length
        let proof = manifest.Proof
        let status =
            if rocqOpen.Length > 0 then statusPartial
            elif pendingSeams > 0 then statusSeamPending
            else statusComplete
        let hypotheses =
            boundary.RestsOn |> Array.map (fun hypothesis ->
                let category =
                    match Map.tryFind hypothesis.Family familyCategories with
                    | Some category -> category
                    | None -> fail Diagnostics.DossierInputMalformed manifestPath ("The manifest names unknown hypothesis family '" + hypothesis.Family + "'.")
                ({ Id = hypothesis.Id; Family = hypothesis.Family; Category = category; Origin = hypothesis.Origin; Statement = hypothesis.Statement
                   TestedBy = familySeams[hypothesis.Family] |> List.filter (fun seam -> Array.contains seam boundary.RequiredSeamChecks) |> List.toArray } : DossierHypothesis))
        let digest (item: string) (path: string) (value: string) : DossierDigest = { Item = item; Path = path; Value = value }
        let digests =
            [| yield digest "Package" "" manifest.Package
               yield digest "Manifest Integrity SHA-256" manifestPath manifest.IntegritySha256
               yield digest "Fidproj SHA-256" manifest.FidprojPath manifest.FidprojSha256
               yield digest "Composer SHA-256" manifest.Compiler.Path manifest.Compiler.Sha256
               yield
                   digest "cvc5 Version and SHA-256" proof.Solver.Executable
                       ((proof.Solver.Version.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue "").Trim()
                        + "; sha256:" + proof.Solver.Sha256.ToLowerInvariant())
               yield digest "Clang Identity and Target" "" (boundary.ClangIdentity + "; " + boundary.ClangTarget)
               yield digest "Link Mode" "" boundary.LinkMode
               for file in boundary.Files do yield digest file.Kind file.Path file.Sha256
               match seam with
               | Some(path, _) -> yield digest "Seam Checks Receipt SHA-256" path (ClangToolchain.sha256 path)
               | None -> ()
               match refusals with
               | Some(path, _) -> yield digest "Refusal Controls Receipt SHA-256" path (ClangToolchain.sha256 path)
               | None -> ()
               match seamsLedger with
               | Some(path, _) -> yield digest "Seams Ledger SHA-256" path (ClangToolchain.sha256 path)
               | None -> ()
               match rocq with
               | Some evidence ->
                   yield digest "Rocq Version" "" evidence.Version
                   for unitEntry in evidence.Units do
                       yield digest ("Rocq Unit " + unitEntry.Module) unitEntry.Path ("md5:" + unitEntry.SourceMd5)
               | None -> () |]
        let legendRow (section: string) (health: string) (meaning: string) : DossierLegendRow = { Section = section; Health = health; Meaning = meaning }
        let legend =
            [| legendRow openHeading (if rocqOpen.Length = 0 then openHealthGood else openHealthWarning) openMeaning
               legendRow falsifiedHeading falsifiedHealth falsifiedMeaning
               legendRow checksHeading (if pendingSeams = 0 then checksHealthGood else checksHealthPending) checksMeaning
               legendRow guaranteesHeading guaranteesHealth guaranteesMeaning |]
        let counts : DossierCounts =
            { Open = proof.PendingOpenCount + proof.RefutedCount + rocqOpen.Length
              Falsified = proof.CompilerNonVacuityReceiptsCount + refusalControls.Length + rocqFalsified.Length
              Checks = proof.PremisesSatCount + seamChecks.Length + rocqChecks.Length
              Guarantees = proof.DischargedUnsatCount + rocqGuarantees.Length
              PendingSeamCategories = pendingSeams }
        let openItems : DossierOpen =
            { CompilerOpenObligations = proof.PendingOpenCount
              CompilerRefutedObligations = proof.RefutedCount
              RocqOpen = rocqOpen }
        let falsified : DossierFalsified =
            { CompilerNonVacuityReceipts = proof.CompilerNonVacuityReceiptsCount
              RefusalControlsAttached = refusals.IsSome
              RefusalControls = refusalControls
              RocqFalsified = rocqFalsified }
        let checks : DossierChecks =
            { PremisesSat = proof.PremisesSatCount
              TotalObligations = proof.TotalObligations
              LibraryPremisesSat = proof.LibraryPremisesSatCount
              LibraryObligations = proof.LibraryObligations
              CompilerPremiseReceipts = proof.CompilerPremiseReceiptsCount
              SeamChecksAttached = seam.IsSome
              SeamTargetTriple = targetTriple
              SeamChecks = seamChecks
              SeamCategories = seamCategories
              RocqChecks = rocqChecks }
        let guarantees : DossierGuarantees =
            { TrustScope = compilerTrustScope boundary.RestsOn
              LibraryObligations = compiler.Obligations
              HarnessObligations = proof.HarnessObligations
              RocqGuarantees = rocqGuarantees }
        let assumptions : DossierAssumptions =
            { Hypotheses = hypotheses
              RocqAxioms = rocqGuarantees |> Array.collect (fun entry -> entry.Axioms) |> Array.distinct |> Array.sort }
        let traceability : DossierTraceability =
            { Declarations = compiler.Declarations
              Layouts = boundary.Layouts
              Enums = boundary.Enums
              Digests = digests
              RocqAttached = rocq.IsSome
              RocqVersion = rocq |> Option.map (fun evidence -> evidence.Version) |> Option.defaultValue ""
              RocqUnits = rocq |> Option.map (fun evidence -> evidence.Units) |> Option.defaultValue [||] }
        let dossier : Dossier =
            { SchemaVersion = 1
              Package = manifest.Package
              Title = titlePrefix + manifest.Package
              OverallStatus = status
              ManifestPath = manifestPath
              ManifestIntegritySha256 = manifest.IntegritySha256
              Counts = counts
              Legend = legend
              Open = openItems
              Falsified = falsified
              Checks = checks
              Guarantees = guarantees
              Assumptions = assumptions
              Traceability = traceability }
        dossier, output

    // =====================================================================
    // Rendering
    // =====================================================================

    type private Inline =
        | Plain of string
        | Code of string
        | Badge of tone: string * text: string
        | Break

    type private Block =
        | Heading of level: int * text: string
        | Banner of tone: string * text: string
        | Paragraph of Inline list
        | Bullets of Inline list list
        | Table of headers: string list * rows: Inline list list list

    type private Document =
        { Title: string
          Lead: Block list
          Sections: (string * Block list) list }

    let private codes (values: string[]) : Inline list =
        if values.Length = 0 then [ Plain "-" ]
        else values |> Array.toList |> List.collect (fun value -> [ Break; Code value ]) |> List.tail

    let private text (value: string) : Inline list = if String.IsNullOrEmpty value then [ Plain "-" ] else [ Plain value ]

    let private code (value: string) : Inline list = if String.IsNullOrEmpty value then [ Plain "-" ] else [ Code value ]

    let private healthTone (health: string) =
        if health.StartsWith("Good", StringComparison.Ordinal) then "good"
        elif health.StartsWith("Warning", StringComparison.Ordinal) then "warning"
        else "pending"

    let private trustTone (scope: string) = if scope = selfContained then "good" else "pending"

    let private rocqTable (entries: DossierRocqEntry[]) : Block =
        Table(
            [ "Rocq Symbol"; "Summary"; "Proposition"; "Corresponds To" ],
            entries |> Array.toList |> List.map (fun entry ->
                [ code entry.Symbol; text entry.Summary; code entry.Proposition; codes entry.CorrespondsTo ]))

    let private document (dossier: Dossier) : Document =
        let statusTone =
            if dossier.OverallStatus = statusComplete then "good"
            elif dossier.OverallStatus = statusSeamPending then "pending"
            else "warning"
        let legend =
            [ Heading(2, legendHeading)
              Table(
                  [ "Section"; "Health"; "Meaning" ],
                  dossier.Legend |> Array.toList |> List.map (fun row ->
                      [ [ Plain row.Section ]; [ Badge(healthTone row.Health, row.Health) ]; [ Plain row.Meaning ] ])) ]
        let openSection =
            [ yield Heading(2, openHeading)
              yield Paragraph [ Plain openGuidance ]
              yield
                  Bullets
                      [ [ Plain("Compiler open safety obligations: " + string dossier.Open.CompilerOpenObligations) ]
                        [ Plain("Compiler refuted safety obligations: " + string dossier.Open.CompilerRefutedObligations) ]
                        (if dossier.Open.RocqOpen.Length = 0 then [ Plain openNoneRocq ]
                         else [ Plain("Companion Rocq open theorems (Admitted): " + string dossier.Open.RocqOpen.Length) ]) ]
              if dossier.Open.RocqOpen.Length > 0 then
                  yield
                      Table(
                          [ "Rocq Symbol"; "Source"; "Statement Summary" ],
                          dossier.Open.RocqOpen |> Array.toList |> List.map (fun entry ->
                              [ code entry.Symbol; code entry.Source; text (if entry.Summary = "" then entry.Proposition else entry.Summary) ]))
                  yield Paragraph [ Plain openAction ] ]
        let falsifiedSection =
            [ yield Heading(2, falsifiedHeading)
              yield Paragraph [ Plain falsifiedGuidance ]
              yield Heading(3, nonVacuityHeading)
              yield Paragraph [ Plain(string dossier.Falsified.CompilerNonVacuityReceipts + nonVacuitySuffix) ]
              yield Heading(3, refusalHeading)
              if dossier.Falsified.RefusalControlsAttached then
                  yield
                      Table(
                          [ "Id"; "ExpectedCode"; "MisuseSummary"; "ProtectionSummary" ],
                          dossier.Falsified.RefusalControls |> Array.toList |> List.map (fun control ->
                              [ code control.Id; code control.ExpectedCode; text control.MisuseSummary; text control.ProtectionSummary ]))
              else yield Paragraph [ Plain refusalNone ]
              yield Heading(3, counterexampleHeading)
              if dossier.Falsified.RocqFalsified.Length > 0 then yield rocqTable dossier.Falsified.RocqFalsified
              else yield Paragraph [ Plain counterexampleNone ]
              yield Paragraph [ Plain falsifiedFooter ] ]
        let checks = dossier.Checks
        let checksSection =
            [ yield Heading(2, checksHeading)
              yield Paragraph [ Plain checksGuidance ]
              yield Heading(3, premisesHeading)
              yield
                  Paragraph
                      [ Plain(
                            "cvc5 verified satisfiable preconditions on " + string checks.PremisesSat + "/" + string checks.TotalObligations
                            + " total obligations (" + string checks.LibraryPremisesSat + "/" + string checks.LibraryObligations
                            + " in exported library modules; " + string checks.CompilerPremiseReceipts + " conditional branch probe(s))."
                        ) ]
              yield Heading(3, seamHeading)
              yield
                  Table(
                      [ "Category"; "Description"; "Status"; "Evidence" ],
                      checks.SeamCategories |> Array.toList |> List.map (fun category ->
                          [ code category.Category
                            [ Plain category.Description ]
                            [ Badge((if category.Status = statusVerified then "good" else "pending"), category.Status) ]
                            codes category.Evidence ]))
              yield Heading(3, rocqChecksHeading)
              if checks.RocqChecks.Length > 0 then yield rocqTable checks.RocqChecks
              else yield Paragraph [ Plain rocqChecksNone ]
              yield Paragraph [ Plain checksFooter ] ]
        let guarantees = dossier.Guarantees
        let guaranteesSection =
            [ yield Heading(2, guaranteesHeading)
              yield Paragraph [ Plain guaranteesGuidance ]
              yield Heading(3, libraryObligationsHeading)
              yield
                  Table(
                      [ "Declaration"; "Obligation Anchor"; "Source"; "Safety Guarantee"; "Trust Scope" ],
                      guarantees.LibraryObligations |> Array.toList |> List.map (fun obligation ->
                          [ code obligation.Declaration
                            code obligation.Anchor
                            code obligation.Source
                            [ Plain obligation.Guarantee ]
                            [ Badge(trustTone obligation.TrustScope, obligation.TrustScope) ] ]))
              yield
                  Paragraph
                      [ Plain(
                            "Activation harness obligations proven: " + string guarantees.HarnessObligations + "/" + string guarantees.HarnessObligations
                            + " in Farscape.SubstantiationRoot."
                        ) ]
              if dossier.Traceability.RocqAttached then
                  yield Heading(3, rocqGuaranteesHeading)
                  yield
                      Table(
                          [ "Rocq Symbol"; "Summary"; "Proposition"; "Binder Notes"; "Trust Scope"; "Corresponds To" ],
                          guarantees.RocqGuarantees |> Array.toList |> List.map (fun entry ->
                              [ code entry.Symbol
                                text entry.Summary
                                code entry.Proposition
                                (if entry.BinderNotes.Length = 0 then [ Plain "-" ]
                                 else
                                     entry.BinderNotes |> Array.toList
                                     |> List.collect (fun note -> [ Break; Code note.Name; Plain(": " + note.Note) ])
                                     |> List.tail)
                                [ Badge(trustTone entry.TrustScope, entry.TrustScope) ]
                                codes entry.CorrespondsTo ])) ]
        let assumptions = dossier.Assumptions
        let assumptionsSection =
            [ yield Heading(2, assumptionsHeading)
              yield Paragraph [ Plain assumptionsGuidance ]
              if assumptions.Hypotheses.Length = 0 then yield Paragraph [ Plain assumptionsNone ]
              else
                  yield
                      Table(
                          [ "Id"; "Category"; "Origin"; "Plain-English Assumption"; "Tested By Seam Categories" ],
                          assumptions.Hypotheses |> Array.toList |> List.map (fun hypothesis ->
                              [ code hypothesis.Id
                                [ Plain hypothesis.Category ]
                                codes (hypothesis.Origin.Split(';', StringSplitOptions.RemoveEmptyEntries))
                                text hypothesis.Statement
                                codes hypothesis.TestedBy ]))
              if assumptions.RocqAxioms.Length > 0 then
                  yield Paragraph(Plain "Companion Rocq axioms (Rests on): " :: (assumptions.RocqAxioms |> Array.toList |> List.collect (fun axiom -> [ Plain ", "; Code axiom ]) |> List.tail)) ]
        let trace = dossier.Traceability
        let traceabilitySection =
            [ Heading(2, traceabilityHeading)
              Paragraph [ Plain traceabilityGuidance ]
              Heading(3, declarationsHeading)
              Table(
                  [ "Declaration"; "Kind"; "Source"; "PSG Node"; "Obligations"; "Witnessed MLIR Symbols"; "Foreign Library / Symbol"; "Measured Types"; "Rocq Theorems" ],
                  trace.Declarations |> Array.toList |> List.map (fun declaration ->
                      [ code declaration.Declaration
                        [ Plain declaration.Kind ]
                        code declaration.Source
                        [ Plain(string declaration.PsgNode) ]
                        [ Plain(string declaration.Obligations.Length) ]
                        codes declaration.MlirSymbols
                        codes (declaration.ForeignLinks |> Array.map (fun link ->
                            link.Library + " / " + link.Symbol + (if link.Member = "" then "" else " (" + link.Member + ")")))
                        codes declaration.MeasuredTypes
                        codes declaration.RocqTheorems ]))
              Heading(3, layoutsHeading)
              Table(
                  [ "Name"; "Kind"; "Header"; "Size Bits"; "Alignment Bits"; "Field Offsets Bits"; "Signed" ],
                  [ for layout in trace.Layouts do
                        yield
                            [ code layout.Name
                              [ Plain layout.Kind ]
                              code layout.Header
                              [ Plain(string layout.SizeBits) ]
                              [ Plain(string layout.AlignmentBits) ]
                              [ Plain(layout.FieldOffsetsBits |> Array.map string |> String.concat ", ") ]
                              [ Plain "-" ] ]
                    for enum in trace.Enums do
                        yield
                            [ code enum.Name
                              [ Plain "enum" ]
                              code enum.Header
                              [ Plain(string enum.WidthBits) ]
                              [ Plain "-" ]
                              [ Plain "-" ]
                              [ Plain(if enum.Signed then "true" else "false") ] ] ])
              Heading(3, digestsHeading)
              Table(
                  [ "Item"; "Path"; "Value" ],
                  trace.Digests |> Array.toList |> List.map (fun digest -> [ [ Plain digest.Item ]; code digest.Path; code digest.Value ])) ]
        { Title = dossier.Title
          Lead = [ Heading(1, dossier.Title); Banner(statusTone, dossier.OverallStatus) ]
          Sections =
            [ "legend", legend
              "open", openSection
              "falsified", falsifiedSection
              "checks", checksSection
              "guarantees", guaranteesSection
              "assumptions", assumptionsSection
              "traceability", traceabilitySection ] }

    let private markdownText (value: string) =
        let builder = StringBuilder()
        for ch in value do
            match ch with
            | '\\' | '`' | '*' | '_' | '[' | ']' | '|' | '#' | '~' -> builder.Append('\\').Append(ch) |> ignore
            | '<' -> builder.Append "&lt;" |> ignore
            | '>' -> builder.Append "&gt;" |> ignore
            | '&' -> builder.Append "&amp;" |> ignore
            | '\r' | '\n' | '\t' -> builder.Append ' ' |> ignore
            | other -> builder.Append other |> ignore
        builder.ToString()

    /// A Markdown code span whose delimiter is longer than every backtick run of its content.
    let private markdownCode (inTable: bool) (value: string) =
        let flat = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
        if flat = "" then ""
        else
            let mutable longest = 0
            let mutable run = 0
            for ch in flat do
                run <- if ch = '`' then run + 1 else 0
                longest <- max longest run
            let fence = String('`', longest + 1)
            let padded = if flat.StartsWith '`' || flat.EndsWith '`' || flat.StartsWith ' ' || flat.EndsWith ' ' then " " + flat + " " else flat
            fence + (if inTable then padded.Replace("|", "\\|") else padded) + fence

    let private markdownInline (inTable: bool) (parts: Inline list) =
        parts
        |> List.map (fun part ->
            match part with
            | Plain value -> markdownText value
            | Code value -> markdownCode inTable value
            | Badge(_, value) -> markdownText value
            | Break -> "<br>")
        |> String.concat ""

    let toMarkdown (dossier: Dossier) : string =
        let builder = StringBuilder()
        let line (value: string) = builder.Append(value).Append('\n') |> ignore
        let block (value: Block) =
            match value with
            | Heading(level, heading) -> line (String('#', level) + " " + markdownText heading)
            | Banner(_, banner) -> line ("**" + markdownText banner + "**")
            | Paragraph parts -> line (markdownInline false parts)
            | Bullets items -> for item in items do line ("- " + markdownInline false item)
            | Table(headers, rows) ->
                line ("| " + (headers |> List.map markdownText |> String.concat " | ") + " |")
                line ("| " + (headers |> List.map (fun _ -> "---") |> String.concat " | ") + " |")
                for row in rows do line ("| " + (row |> List.map (markdownInline true) |> String.concat " | ") + " |")
            line ""
        let rendered = document dossier
        for value in rendered.Lead do block value
        for _, blocks in rendered.Sections do
            for value in blocks do block value
        builder.ToString()

    /// HTML text with the five markup characters escaped and every character outside
    /// printable 7-bit ASCII written as a numeric character reference.
    let private htmlText (value: string) =
        let builder = StringBuilder()
        let mutable index = 0
        while index < value.Length do
            let ch = value[index]
            match ch with
            | '&' -> builder.Append "&amp;" |> ignore
            | '<' -> builder.Append "&lt;" |> ignore
            | '>' -> builder.Append "&gt;" |> ignore
            | '"' -> builder.Append "&quot;" |> ignore
            | '\'' -> builder.Append "&#39;" |> ignore
            | '\n' | '\t' -> builder.Append ' ' |> ignore
            | _ when Char.IsHighSurrogate ch && index + 1 < value.Length && Char.IsLowSurrogate value[index + 1] ->
                builder.Append("&#x").Append(Char.ConvertToUtf32(ch, value[index + 1]).ToString "X").Append(';') |> ignore
                index <- index + 1
            | _ when Char.IsSurrogate ch -> builder.Append "&#xFFFD;" |> ignore
            | _ when ch < ' ' || ch = '\u007F' -> builder.Append ' ' |> ignore
            | _ when ch > '~' -> builder.Append("&#x").Append((int ch).ToString "X").Append(';') |> ignore
            | _ -> builder.Append ch |> ignore
            index <- index + 1
        builder.ToString()

    let private htmlInline (parts: Inline list) =
        parts
        |> List.map (fun part ->
            match part with
            | Plain value -> htmlText value
            | Code value -> "<code>" + htmlText value + "</code>"
            | Badge(tone, value) -> "<span class=\"badge badge-" + htmlText tone + "\">" + htmlText value + "</span>"
            | Break -> "<br>")
        |> String.concat ""

    let private stylesheet =
        [ "body { font-family: system-ui, -apple-system, 'Segoe UI', Helvetica, Arial, sans-serif; line-height: 1.5; color: #1b1f24; background: #ffffff; margin: 0; }"
          "header, section { max-width: 76rem; margin: 0 auto; padding: 1rem 1.5rem; }"
          "h1 { font-size: 1.7rem; margin: 0.5rem 0; }"
          "h2 { font-size: 1.3rem; border-bottom: 1px solid #d0d7de; padding-bottom: 0.3rem; margin-top: 1.5rem; }"
          "h3 { font-size: 1.05rem; margin-top: 1.25rem; }"
          "table { border-collapse: collapse; width: 100%; margin: 0.75rem 0; font-size: 0.9rem; }"
          "th, td { border: 1px solid #d0d7de; padding: 0.4rem 0.6rem; text-align: left; vertical-align: top; }"
          "th { background: #f6f8fa; }"
          "code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 0.85em; overflow-wrap: anywhere; word-break: break-all; }"
          ".banner { font-weight: 600; padding: 0.75rem 1rem; border-radius: 6px; border: 1px solid; }"
          ".banner-good { background: #dafbe1; border-color: #2da44e; }"
          ".banner-pending { background: #fff8c5; border-color: #bf8700; }"
          ".banner-warning { background: #ffebe9; border-color: #cf222e; }"
          ".badge { display: inline-block; padding: 0.1rem 0.5rem; border-radius: 999px; font-size: 0.8rem; font-weight: 600; border: 1px solid; }"
          ".badge-good { background: #dafbe1; border-color: #2da44e; color: #116329; }"
          ".badge-pending { background: #fff8c5; border-color: #bf8700; color: #7d4e00; }"
          ".badge-warning { background: #ffebe9; border-color: #cf222e; color: #a40e26; }" ]

    let toHtml (dossier: Dossier) : string =
        let builder = StringBuilder()
        let line (value: string) = builder.Append(value).Append('\n') |> ignore
        let block (value: Block) =
            match value with
            | Heading(level, heading) -> line ("<h" + string level + ">" + htmlText heading + "</h" + string level + ">")
            | Banner(tone, banner) -> line ("<p class=\"banner banner-" + htmlText tone + "\">" + htmlText banner + "</p>")
            | Paragraph parts -> line ("<p>" + htmlInline parts + "</p>")
            | Bullets items ->
                line "<ul>"
                for item in items do line ("<li>" + htmlInline item + "</li>")
                line "</ul>"
            | Table(headers, rows) ->
                line "<table>"
                line ("<thead><tr>" + (headers |> List.map (fun header -> "<th scope=\"col\">" + htmlText header + "</th>") |> String.concat "") + "</tr></thead>")
                line "<tbody>"
                for row in rows do
                    line ("<tr>" + (row |> List.map (fun cell -> "<td>" + htmlInline cell + "</td>") |> String.concat "") + "</tr>")
                line "</tbody>"
                line "</table>"
        let rendered = document dossier
        line "<!DOCTYPE html>"
        line "<html lang=\"en\">"
        line "<head>"
        line "<meta charset=\"utf-8\">"
        line "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        line ("<title>" + htmlText rendered.Title + "</title>")
        line "<style>"
        for rule in stylesheet do line rule
        line "</style>"
        line "</head>"
        line "<body>"
        line "<header>"
        for value in rendered.Lead do block value
        line "</header>"
        for identity, blocks in rendered.Sections do
            line ("<section id=\"" + htmlText identity + "\">")
            for value in blocks do block value
            line "</section>"
        line "</body>"
        line "</html>"
        builder.ToString()

    // =====================================================================
    // Entry point
    // =====================================================================

    /// Validate every input, then write `<Package>.dossier.json`, `.md` and `.html`. Nothing
    /// is written when any validation refuses.
    let generate (request: Request) : Result<Generated, Diagnostics.Refusal> =
        let built =
            try Ok(build request)
            with
            | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
            | error ->
                Diagnostics.diagnostic Diagnostics.DossierInputMalformed error.Message (Diagnostics.inFile request.ManifestPath "dossier input or retained evidence")
                |> Diagnostics.refusalOf |> Error
        match built with
        | Error refusal -> Error refusal
        | Ok(dossier, output) ->
            try
                let jsonPath = Path.Combine(output, dossier.Package + ".dossier.json")
                let markdownPath = Path.Combine(output, dossier.Package + ".dossier.md")
                let htmlPath = Path.Combine(output, dossier.Package + ".dossier.html")
                let files =
                    [ jsonPath, toJson dossier
                      markdownPath, toMarkdown dossier
                      htmlPath, toHtml dossier ]
                Directory.CreateDirectory output |> ignore
                for path, content in files do File.WriteAllText(evidencePath path, content, UTF8Encoding false)
                Ok { Dossier = dossier; JsonPath = jsonPath; MarkdownPath = markdownPath; HtmlPath = htmlPath }
            with
            | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
            | error ->
                Diagnostics.diagnostic Diagnostics.DossierOutputFailure ("The dossier could not be written: " + error.Message) (Diagnostics.inFile output "dossier output directory")
                |> Diagnostics.refusalOf |> Error
