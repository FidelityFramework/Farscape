module Farscape.Tests.SubstantiationDossierTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Fidelity.Data.JSON
open Xunit
open Farscape.Core
open Farscape.Tests.SubstantiationTests

// #209 owns the dossier generator. One real library is substantiated once per test run; the
// Rocq projects, receipts and mutated manifests are built per test. Everything is in OS
// temporary storage, under the scratch root of the test process; nothing is written to the
// checkout (#215).
let private repository = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

/// The scratch directory of this module. The substantiated library is one lazy fixture that
/// every test reads, so no single test deletes it; it is removed with the scratch root of the
/// test process when the process exits.
let private scratchRoot = TestHelpers.TestScratch.create "dossier-tests"

let private scratch label =
    let path = Path.Combine(scratchRoot, label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let private writeLines path (lines: string list) = File.WriteAllText(path, String.concat "\n" lines + "\n")

// ---------------------------------------------------------------------------
// The substantiated library: a static archive with a measured C struct, link provenance,
// generated FidelityExtern declarations and FunctionDescriptors, and an authored function.
// ---------------------------------------------------------------------------

type private Library =
    { Work: string
      Manifest: Substantiation.Manifest
      ManifestJson: string
      ManifestToml: string
      Archive: string
      SharedLib: string
      Header: string
      Provenance: string
      Types: string
      Scalar: string }

let private library =
    lazy
        (let work = scratch "library"
         let header = Path.Combine(work, "probe.h")
         let source = Path.Combine(work, "probe.c")
         let deadSource = Path.Combine(work, "dead.c")
         let objectFile = Path.Combine(work, "probe.o")
         let deadObjectFile = Path.Combine(work, "dead.o")
         let archive = Path.Combine(work, "libdossier_probe.a")
         let sharedDir = Path.Combine(work, "shared-lib")
         Directory.CreateDirectory sharedDir |> ignore
         let sharedLib = Path.Combine(sharedDir, "libdossier_probe.so")
         writeLines header
             [ "typedef struct DossierPair { int left; int right; } DossierPair;"
               "int dossier_identity(int value);"
               "int dossier_pair_sum(DossierPair pair);" ]
         writeLines source
             [ "#include \"probe.h\""
               "int dossier_identity(int value) { return value; }"
               "int dossier_pair_sum(DossierPair pair) { return pair.left + pair.right; }" ]
         writeLines deadSource
             [ "int dossier_unused_dead(int value) { return value * 13 + 5; }" ]
         run work "foreign-object" (ClangToolchain.startInfo [ "-c"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; source; "-o"; objectFile ]) |> ignore
         run work "foreign-dead-object" (ClangToolchain.startInfo [ "-c"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; deadSource; "-o"; deadObjectFile ]) |> ignore
         run work "foreign-archive" (ClangToolchain.arStartInfo [ "rcs"; archive; objectFile; deadObjectFile ]) |> ignore
         run work "foreign-shared" (ClangToolchain.startInfo [ "-shared"; "-fPIC"; objectFile; deadObjectFile; "-o"; sharedLib ]) |> ignore
         let pilot = Path.Combine(work, "Probe.pilot.toml")
         writeLines pilot
             [ "[library]"; "name = \"dossier_probe\""; "headers = [" + quote header + "]"
               "include_paths = [" + quote work + "]"
               "[library.artifact]"; "kind = \"static-archive\""; "path = " + quote archive
               "sha256 = " + quote (ClangToolchain.sha256 archive)
               "[output]"; "mode = \"fidelity\""; "directory = \"generated\""
               "[options]"; "c_header_mode = true"; "descriptor_only_dependencies = true"; "link_libraries = true"
               "value_structs = [\"DossierPair\"]"; "abi_critical_structs = [\"DossierPair\"]"
               "[[namespace]]"; "name = \"Dossier.Probe.Boundary\""; "library = \"dossier_probe\""
               "description = \"Dossier measured record probe\""; "functions = [\"dossier_identity\", \"dossier_pair_sum\"]" ]
         ScratchDirectory.within work (fun () -> BindingGenerator.generateFromProjectDiagnosed pilot false false Types.LP64)
         |> accepted |> ignore
         let provenance =
             match Directory.GetFiles(work, "*.link-provenance.toml", SearchOption.AllDirectories) with
             | [| path |] -> path
             | paths -> failwithf "expected one generated link provenance manifest, got %A" paths
         let loading = Path.Combine(work, "Loading.clef")
         (File.ReadAllText(Path.Combine(repository, "tests", "native-callbacks", "Loading.clef"))).Replace("landingprobe", "dossier_probe")
         |> fun text -> File.WriteAllText(loading, text)
         let types = Path.Combine(work, "generated", "Types.clef")
         let boundary = Path.Combine(work, "generated", "Boundary", "Boundary.clef")
         File.AppendAllText(boundary,
             "\nlet identityResultContract : Expr<ResultRangeDescriptor> = <@ {\n    Binding = \"Dossier.Probe.Boundary.dossier_identity\"\n    Role = ReturnValue\n    Lower = Argument 0\n    Upper = Argument 0\n    Hypothesis = ({ Identity = \"Dossier.identity-returns-input\"; Version = \"1\"; Source = \"fixture:probe.c\" } : ExternalExecutionHypothesis)\n} @>\n")
         let scalar = Path.Combine(work, "Scalar.clef")
         writeLines scalar
             [ "module Dossier.Probe.Scalar"
               "let seven () : int = Dossier.Probe.Boundary.dossier_identity 7"
               "let three () : int = 3" ]
         let sources = [ loading; types; boundary; scalar ]
         sources |> List.iter (requiredFile "library source" >> ignore)
         let dependencies, platform = platformTables ()
         let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
         let project = Path.Combine(work, "DossierProbe.fidproj")
         writeLines project
             [ "[package]"; "name = \"DossierProbe\""; "version = \"1.0.0\""
               "[compilation]"; "target = \"cpu\""
               "[build]"; "output_kind = \"library\""
               "sources = [" + (sources |> List.map quote |> String.concat ", ") + "]"
               "[dependencies]"; yield! dependencies
               "metadata = { path = " + quote metadata + " }"
               "[platform]"; yield! platform
               "loading = \"Landing.Acceptance.Loading.contract\""
               "[link]"; "libraries = [\"dossier_probe\"]" ]
         let output = Path.Combine(work, "manifest")
         let manifest =
             Substantiation.evaluate
                 { FidprojPath = project
                   PilotPath = Some pilot
                   LinkProvenancePath = Some provenance
                   OutputDirectory = Some output
                   WorkDirectory = Some(Path.Combine(work, "substantiation"))
                   AllowWarnings = false
                   Verbose = false }
             |> accepted
         { Work = work
           Manifest = manifest
           ManifestJson = requiredFile "JSON manifest" (Path.Combine(output, "DossierProbe.substantiation.json"))
           ManifestToml = requiredFile "TOML manifest" (Path.Combine(output, "DossierProbe.substantiation.toml"))
           Archive = archive
           SharedLib = sharedLib
           Header = header
           Provenance = provenance
           Types = types
           Scalar = scalar })

// ---------------------------------------------------------------------------
// The companion Rocq project. Each .glob carries the exact MD5 of its .v bytes on the
// DIGEST line, so FSharp.Data.Rocq accepts it as current without a Rocq process.
// ---------------------------------------------------------------------------

let private lawsSource =
    [ "(** * Probe laws *)"
      ""
      "Axiom H_c_abi : forall n : nat, n + 0 = n."
      ""
      "(** The identity result equals its argument <script>alert(1)</script> & holds for every input."
      "    - [n:] the value passed to the foreign identity function. *)"
      "Theorem identity_returns_input (n : nat) : n = n."
      "Proof. reflexivity. Qed."
      ""
      "(** The measured pair layout agrees with the C ABI."
      "    - [a:] the left field."
      "    - [b:] the right field,"
      "      measured at bit offset 32. *)"
      "Theorem pair_layout_sound : forall (a b : nat), a + 0 = a /\\ b = b."
      "Proof. intros a b. split. apply H_c_abi. reflexivity. Qed."
      ""
      "(** A helper fact about addition. *)"
      "Lemma add_zero_helper (n : nat) : n + 0 = n."
      "Proof. apply H_c_abi. Qed."
      "" ]
    |> String.concat "\n"

let private limitsSource =
    [ "(** Dropping the range precondition admits a failing input. *)"
      "Theorem unbounded_input_falsified : ~ (forall n : nat, n = 0)."
      "Proof. intro H. discriminate (H 1). Qed."
      ""
      "(** Seven is a valid identity argument. *Check, not a guarantee.* *)"
      "Theorem seven_is_admitted : 7 = 7."
      "Proof. reflexivity. Qed."
      "" ]
    |> String.concat "\n"

let private admittedSource =
    [ ""
      "(** The pair sum is commutative. *)"
      "Theorem pair_sum_commutes (a b : nat) : a + b = b + a."
      "Admitted."
      "" ]
    |> String.concat "\n"

/// Declarations outside the public theorem keywords whose proofs end in Admitted, while
/// every Theorem of the unit ends in Qed: an internal Lemma and a Definition built in proof mode.
let private unfinishedSource =
    [ ""
      "(** An internal helper that is not proved. *)"
      "Lemma helper_unfinished : 1 = 1."
      "Proof. Admitted."
      ""
      "(** A value built in proof mode and not finished. *)"
      "Definition def_unfinished : nat."
      "Proof. Admitted."
      "" ]
    |> String.concat "\n"

let private unfinishedSymbols = [ "prf", "Lemma", "helper_unfinished"; "def", "Definition", "def_unfinished" ]

/// Admitted proofs under the remaining declaration forms: a Remark, an Example, an Instance
/// and an obligation of a Program Definition, which the Program Definition owns.
let private unfinishedFormsSource =
    [ ""
      "Remark remark_unfinished : 2 = 2."
      "Proof. Admitted."
      ""
      "Example example_unfinished : 3 = 3."
      "Admitted."
      ""
      "Class Marker : Prop := marker : True."
      "Instance instance_unfinished : Marker."
      "Proof. Admitted."
      ""
      "Program Definition program_unfinished : nat := _."
      "Next Obligation. Admitted."
      "" ]
    |> String.concat "\n"

let private unfinishedFormsSymbols =
    [ "prf", "Remark", "remark_unfinished"; "def", "Example", "example_unfinished"
      "inst", "Instance", "instance_unfinished"; "def", "Definition", "program_unfinished" ]

let private md5 (bytes: byte[]) = Convert.ToHexStringLower(MD5.HashData bytes)

/// A `.glob` naming each `keyword name` declaration of `source` at its byte span.
let private glob (moduleName: string) (source: string) (symbols: (string * string * string) list) =
    let definitions =
        symbols |> List.map (fun (kind, keyword, name) ->
            let declared = source.IndexOf(keyword + " " + name, StringComparison.Ordinal)
            Assert.True(declared >= 0, $"{keyword} {name} is not written in the fixture source")
            let start = declared + keyword.Length + 1
            $"{kind} {start}:{start + name.Length - 1} <> {name}")
    String.concat "\n" ([ "DIGEST " + md5 (Encoding.UTF8.GetBytes source); "F" + moduleName ] @ definitions) + "\n"

type private Rocq =
    { Directory: string
      Project: string
      Facts: string
      Config: string
      Laws: string
      LawsGlob: string }

let private lawsSymbols = [ "ax", "Axiom", "H_c_abi"; "prf", "Theorem", "identity_returns_input"; "prf", "Theorem", "pair_layout_sound"; "prf", "Lemma", "add_zero_helper" ]
let private limitsSymbols = [ "prf", "Theorem", "unbounded_input_falsified"; "prf", "Theorem", "seven_is_admitted" ]

let private jProp (name: string) (v: JsonValue) : JsonValue =
    match Json.prop name v with
    | Some x -> x
    | None -> JsonValue.Null

let private jSet (name: string) (value: JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Object fields ->
        if fields |> List.exists (fun (k, _) -> k = name) then
            fields |> List.map (fun (k, old) -> if k = name then k, value else k, old) |> JsonValue.Object
        else
            JsonValue.Object (fields @ [ name, value ])
    | other -> other

let private jRemove (name: string) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Object fields ->
        fields |> List.filter (fun (k, _) -> k <> name) |> JsonValue.Object
    | other -> other

let private jUpdate (name: string) (f: JsonValue -> JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Object fields ->
        fields |> List.map (fun (k, old) -> if k = name then k, f old else k, old) |> JsonValue.Object
    | other -> other

let private jAdd (item: JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Array items -> JsonValue.Array (items @ [ item ])
    | other -> other

let private jUpdateAt (idx: int) (f: JsonValue -> JsonValue) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Array items ->
        items |> List.mapi (fun i x -> if i = idx then f x else x) |> JsonValue.Array
    | other -> other

let private jRemoveAt (idx: int) (v: JsonValue) : JsonValue =
    match v with
    | JsonValue.Array items ->
        items |> List.mapi (fun i x -> i, x) |> List.choose (fun (i, x) -> if i = idx then None else Some x) |> JsonValue.Array
    | other -> other

let private fact (proposition: string) (assumptions: JsonValue option) =
    JsonValue.Object
        [ yield "type", JsonValue.String proposition
          yield "binders", JsonValue.Object []
          match assumptions with
          | Some a -> yield "assumptions", a
          | None -> () ]

let private axioms (names: string list) =
    Some (JsonValue.Array (names |> List.map JsonValue.String))

let private closed () = Some (JsonValue.String "closed")

let private correspondenceEntry (symbol: string) (targets: string list) =
    JsonValue.Object
        [ "RocqSymbol", JsonValue.String symbol
          "Targets", JsonValue.Array (targets |> List.map JsonValue.String) ]

let private rocqWith (laws: string) (limits: string) (extraLimitsSymbols: (string * string * string) list) (alterFacts: JsonValue -> JsonValue) (alterConfig: JsonValue -> JsonValue) =
    let directory = scratch "rocq"
    let theories = Path.Combine(directory, "theories")
    Directory.CreateDirectory theories |> ignore
    let project = Path.Combine(directory, "_CoqProject")
    File.WriteAllText(project, "-Q theories DossierProofs\n\ntheories/Laws.v\ntheories/Limits.v\n")
    let lawsPath = Path.Combine(theories, "Laws.v")
    let limitsPath = Path.Combine(theories, "Limits.v")
    File.WriteAllText(lawsPath, laws)
    File.WriteAllText(limitsPath, limits)
    File.WriteAllText(Path.ChangeExtension(lawsPath, ".glob"), glob "DossierProofs.Laws" laws lawsSymbols)
    File.WriteAllText(Path.ChangeExtension(limitsPath, ".glob"), glob "DossierProofs.Limits" limits (limitsSymbols @ extraLimitsSymbols))
    let digests =
        JsonValue.Object
            [ "theories/Laws.v", JsonValue.String(md5 (Encoding.UTF8.GetBytes laws))
              "theories/Limits.v", JsonValue.String(md5 (Encoding.UTF8.GetBytes limits)) ]
    let symbols =
        JsonValue.Object
            [ "DossierProofs.Laws.H_c_abi", fact "forall n : nat, n + 0 = n" None
              "DossierProofs.Laws.identity_returns_input", fact "forall n : nat, n = n" (closed ())
              "DossierProofs.Laws.pair_layout_sound", fact ("forall a b : nat, a + 0 = a " + "/\\ b = b") (axioms [ "H_c_abi" ])
              "DossierProofs.Laws.add_zero_helper", fact "forall n : nat, n + 0 = n" (axioms [ "H_c_abi" ])
              "DossierProofs.Limits.unbounded_input_falsified", fact "~ (forall n : nat, n = 0)" (closed ())
              "DossierProofs.Limits.seven_is_admitted", fact "7 = 7" (closed ()) ]
    let facts =
        JsonValue.Object
            [ "rocqVersion", JsonValue.String "9.1.0"
              "digests", digests
              "symbols", symbols ]
        |> alterFacts
    let factsPath = Path.Combine(directory, "facts.json")
    File.WriteAllText(factsPath, Json.serialize facts)
    let config =
        JsonValue.Object
            [ "SchemaVersion", Json.ofInt64 1L
              "FalsifiedSuffix", JsonValue.String "_falsified"
              "CheckMarker", JsonValue.String "*Check, not a guarantee.*"
              "Correspondence",
              JsonValue.Array
                  [ correspondenceEntry "DossierProofs.Laws.identity_returns_input" [ "Dossier.Probe.Scalar.seven" ]
                    correspondenceEntry "DossierProofs.Laws.pair_layout_sound" [ "H-ABI-1"; "DossierPair" ] ] ]
        |> alterConfig
    let configPath = Path.Combine(directory, "rocq-dossier.json")
    File.WriteAllText(configPath, Json.serialize config)
    { Directory = directory; Project = project; Facts = factsPath; Config = configPath
      Laws = lawsPath; LawsGlob = Path.ChangeExtension(lawsPath, ".glob") }

let private rocq () = rocqWith lawsSource limitsSource [] id id

// ---------------------------------------------------------------------------
// Receipts and requests
// ---------------------------------------------------------------------------

let private seamReceipt (lib: Library) : SubstantiationDossier.SeamChecksReceipt =
    let check category : SubstantiationDossier.SeamCheckEntry =
        { Id = "seam-" + category
          Category = category
          Summary = "Linked target binary run for " + category
          BinarySha256 = ClangToolchain.sha256 lib.Archive
          Passed = true
          Command = "./dossier-seam --category " + category }
    { SchemaVersion = 1
      Package = lib.Manifest.Package
      ManifestIntegritySha256 = lib.Manifest.IntegritySha256
      TargetTriple = lib.Manifest.Boundary.ClangTarget
      Checks =
        lib.Manifest.Boundary.RequiredSeamChecks
        |> Array.filter (fun category -> category <> "falsification-refusal-controls")
        |> Array.map check }

let private refusalReceipt (lib: Library) : SubstantiationDossier.RefusalControlsReceipt =
    { SchemaVersion = 1
      Package = lib.Manifest.Package
      ManifestIntegritySha256 = lib.Manifest.IntegritySha256
      Controls =
        [| { Id = "unlinked-extern"
             ExpectedCode = "FS9243"
             ObservedCode = "FS9243"
             MisuseSummary = "A FidelityExtern names a symbol that no archive member defines."
             ProtectionSummary = "Substantiation refuses the library before any manifest is written."
             Passed = true } |] }

let private seamChecksToJsonValue (r: SubstantiationDossier.SeamChecksReceipt) : JsonValue =
    let checkJson (c: SubstantiationDossier.SeamCheckEntry) =
        JsonValue.Object
            [ "Id", JsonValue.String c.Id
              "Category", JsonValue.String c.Category
              "Summary", JsonValue.String c.Summary
              "BinarySha256", JsonValue.String c.BinarySha256
              "Passed", JsonValue.Bool c.Passed
              "Command", JsonValue.String c.Command ]
    JsonValue.Object
        [ "SchemaVersion", Json.ofInt64 (int64 r.SchemaVersion)
          "Package", JsonValue.String r.Package
          "ManifestIntegritySha256", JsonValue.String r.ManifestIntegritySha256
          "TargetTriple", JsonValue.String r.TargetTriple
          "Checks", JsonValue.Array (r.Checks |> Array.toList |> List.map checkJson) ]

let private seamSeamsChecksToJsonValue (r: SubstantiationSeams.SeamChecksReceipt) : JsonValue =
    let checkJson (c: SubstantiationSeams.SeamCheckEntry) =
        JsonValue.Object
            [ "Id", JsonValue.String c.Id
              "Category", JsonValue.String c.Category
              "Summary", JsonValue.String c.Summary
              "BinarySha256", JsonValue.String c.BinarySha256
              "Passed", JsonValue.Bool c.Passed
              "Command", JsonValue.String c.Command ]
    JsonValue.Object
        [ "SchemaVersion", Json.ofInt64 (int64 r.SchemaVersion)
          "Package", JsonValue.String r.Package
          "ManifestIntegritySha256", JsonValue.String r.ManifestIntegritySha256
          "TargetTriple", JsonValue.String r.TargetTriple
          "Checks", JsonValue.Array (r.Checks |> Array.toList |> List.map checkJson) ]

let private refusalControlsToJsonValue (r: SubstantiationDossier.RefusalControlsReceipt) : JsonValue =
    let ctrlJson (c: SubstantiationDossier.RefusalControlEntry) =
        JsonValue.Object
            [ "Id", JsonValue.String c.Id
              "ExpectedCode", JsonValue.String c.ExpectedCode
              "ObservedCode", JsonValue.String c.ObservedCode
              "MisuseSummary", JsonValue.String c.MisuseSummary
              "ProtectionSummary", JsonValue.String c.ProtectionSummary
              "Passed", JsonValue.Bool c.Passed ]
    JsonValue.Object
        [ "SchemaVersion", Json.ofInt64 (int64 r.SchemaVersion)
          "Package", JsonValue.String r.Package
          "ManifestIntegritySha256", JsonValue.String r.ManifestIntegritySha256
          "Controls", JsonValue.Array (r.Controls |> Array.toList |> List.map ctrlJson) ]

let private seamsLedgerToJsonValue (ledger: SubstantiationSeams.SeamsLedgerReceipt) : JsonValue =
    let dischargeJson (d: SubstantiationSeams.DischargeSummary) =
        JsonValue.Object
            [ "ReceiptPath", JsonValue.String d.ReceiptPath
              "ReceiptSha256", JsonValue.String d.ReceiptSha256
              "PremisesOutputPath", JsonValue.String d.PremisesOutputPath
              "PremisesOutputSha256", JsonValue.String d.PremisesOutputSha256
              "TotalObligations", Json.ofInt64 (int64 d.TotalObligations)
              "DischargedUnsatCount", Json.ofInt64 (int64 d.DischargedUnsatCount)
              "PremisesSatCount", Json.ofInt64 (int64 d.PremisesSatCount)
              "LibraryObligations", Json.ofInt64 (int64 d.LibraryObligations)
              "LibraryDischargedUnsatCount", Json.ofInt64 (int64 d.LibraryDischargedUnsatCount)
              "LibraryPremisesSatCount", Json.ofInt64 (int64 d.LibraryPremisesSatCount)
              "PendingOpenCount", Json.ofInt64 (int64 d.PendingOpenCount)
              "RefutedCount", Json.ofInt64 (int64 d.RefutedCount) ]
    let retainedJson (r: SubstantiationSeams.RetainedProbeBinary) =
        JsonValue.Object
            [ "CheckId", JsonValue.String r.CheckId
              "BinaryPath", JsonValue.String r.BinaryPath
              "BinarySha256", JsonValue.String r.BinarySha256 ]
    let sharedJson (s: SubstantiationSeams.SharedLibraryComparison) =
        JsonValue.Object
            [ "CheckId", JsonValue.String s.CheckId
              "SharedLibraryPath", JsonValue.String s.SharedLibraryPath
              "SharedLibrarySha256", JsonValue.String s.SharedLibrarySha256
              "DynamicProbeBinaryPath", JsonValue.String s.DynamicProbeBinaryPath
              "DynamicProbeBinarySha256", JsonValue.String s.DynamicProbeBinarySha256 ]
    JsonValue.Object
        [ "Schema", JsonValue.String ledger.Schema
          "SchemaVersion", Json.ofInt64 (int64 ledger.SchemaVersion)
          "SeamChecksSchema", JsonValue.String ledger.SeamChecksSchema
          "RefusalControlsSchema", JsonValue.String ledger.RefusalControlsSchema
          "Package", JsonValue.String ledger.Package
          "ManifestIntegritySha256", JsonValue.String ledger.ManifestIntegritySha256
          "TargetTriple", JsonValue.String ledger.TargetTriple
          "RequiredSeamCategories", JsonValue.Array (ledger.RequiredSeamCategories |> Array.toList |> List.map JsonValue.String)
          "VerifiedPhysicalCategories", JsonValue.Array (ledger.VerifiedPhysicalCategories |> Array.toList |> List.map JsonValue.String)
          "Discharge", dischargeJson ledger.Discharge
          "RetainedBinaries", JsonValue.Array (ledger.RetainedBinaries |> Array.toList |> List.map retainedJson)
          "SharedLibraries", JsonValue.Array (ledger.SharedLibraries |> Array.toList |> List.map sharedJson)
          "SeamChecksPath", JsonValue.String ledger.SeamChecksPath
          "SeamChecksSha256", JsonValue.String ledger.SeamChecksSha256
          "RefusalControlsPath", JsonValue.String ledger.RefusalControlsPath
          "RefusalControlsSha256", JsonValue.String ledger.RefusalControlsSha256 ]

let private writeSeamJson (label: string) (value: SubstantiationDossier.SeamChecksReceipt) =
    let path = Path.Combine(scratch label, label + ".json")
    File.WriteAllText(path, seamChecksToJsonValue value |> Json.serialize)
    path

let private writeRefusalJson (label: string) (value: SubstantiationDossier.RefusalControlsReceipt) =
    let path = Path.Combine(scratch label, label + ".json")
    File.WriteAllText(path, refusalControlsToJsonValue value |> Json.serialize)
    path

let private realSeams =
    lazy
        (let lib = library.Value
         let work = scratch "real-seams"
         let probesDir = Path.Combine(work, "probes")
         let controlsDir = Path.Combine(work, "controls")
         let outDir = Path.Combine(work, "out")
         Directory.CreateDirectory probesDir |> ignore
         Directory.CreateDirectory controlsDir |> ignore
         let probeScalar = Path.Combine(probesDir, "ProbeScalar.clef")
         writeLines probeScalar
             [ "module Dossier.Probe.RunScalar"
               "[<EntryPoint>]"
               "let main _ : int ="
               "    if Dossier.Probe.Boundary.dossier_identity 7 = 7 then"
               "        Console.writeln \"dossier:scalar:ok\""
               "        0"
               "    else 1" ]
         let probeRecord = Path.Combine(probesDir, "ProbeRecord.clef")
         writeLines probeRecord
             [ "module Dossier.Probe.RunRecord"
               "open Dossier.Probe.Boundary.Types"
               "[<EntryPoint>]"
               "let main _ : int ="
               "    if DossierPair.Size = 8 && DossierPair.Alignment = 4 && DossierPair.leftOffset = 0 && DossierPair.rightOffset = 4 && Dossier.Probe.Boundary.dossier_identity 8 = 8 then"
               "        Console.writeln \"dossier:record:ok\""
               "        0"
               "    else 2" ]
         let probeLink = Path.Combine(probesDir, "ProbeLink.clef")
         writeLines probeLink
             [ "module Dossier.Probe.RunLink"
               "[<EntryPoint>]"
               "let main _ : int ="
               "    if Dossier.Probe.Boundary.dossier_identity 9 = 9 then"
               "        Console.writeln \"dossier:link:ok\""
               "        0"
               "    else 3" ]
         let dynProbeC = Path.Combine(probesDir, "dynamic_probe.c")
         writeLines dynProbeC
             [ "#include " + quote lib.Header
               "int main(void) { return dossier_identity(9) == 9 ? 0 : 1; }" ]
         let probeZeroCopy = Path.Combine(probesDir, "ProbeZeroCopy.clef")
         writeLines probeZeroCopy
             [ "module Dossier.Probe.RunZeroCopy"
               "open Dossier.Probe.Boundary.Types"
               "[<EntryPoint>]"
               "let main _ : int ="
               "    if DossierPair.Size = 8 && Dossier.Probe.Boundary.dossier_identity 7 = 7 then"
               "        Console.writeln \"dossier:zerocopy:ok\""
               "        0"
               "    else 4" ]
         let missingHeader = Path.Combine(controlsDir, "missing.h")
         writeLines missingHeader [ "int dossier_missing_symbol(int value);" ]
         let missingPilot = Path.Combine(controlsDir, "Missing.pilot.toml")
         writeLines missingPilot
             [ "[library]"; "name = \"dossier_probe\""; "headers = [" + quote missingHeader + "]"
               "include_paths = [" + quote controlsDir + "]"
               "[library.artifact]"; "kind = \"static-archive\""; "path = " + quote lib.Archive
               "sha256 = " + quote (ClangToolchain.sha256 lib.Archive)
               "[output]"; "mode = \"fidelity\""; "directory = \"generated-missing\""
               "[options]"; "c_header_mode = true"
               "[[namespace]]"; "name = \"Dossier.Refusal\""; "library = \"dossier_probe\""
               "description = \"Missing symbol control\""; "functions = [\"dossier_missing_symbol\"]" ]
         let dependencies, platform = platformTables ()
         let rawAddrClef = Path.Combine(controlsDir, "RawAddr.clef")
         writeLines rawAddrClef
             [ "module Dossier.Refusal.RawAddr"
               "[<EntryPoint>]"
               "let main _ : int ="
               "    let mutable cell = 7"
               "    let p = &&cell"
               "    if p = p then 0 else 1" ]
         let rawAddrProj = Path.Combine(controlsDir, "RawAddr.fidproj")
         writeLines rawAddrProj
             [ "[package]"; "name = \"RawAddr\""; "version = \"0.1.0\""
               "[compilation]"; "target = \"cpu\""
               "[dependencies]"; yield! dependencies
               "[build]"; "output_kind = \"console\""; "sources = [" + quote rawAddrClef + "]"
               "[platform]"; yield! platform ]
         let specPath = Path.Combine(work, "DossierProbe.seams.toml")
         writeLines specPath
             [ "schema_version = 1"
               "target_triple = " + quote lib.Manifest.Boundary.ClangTarget
               "loading_contract = \"Landing.Acceptance.Loading.contract\""
               ""
               "[[checks]]"
               "id = \"seam-scalar-handle-link-and-call\""
               "category = \"scalar-handle-link-and-call\""
               "summary = \"Scalar call across dossier_probe static archive.\""
               "entry_source = " + quote probeScalar
               "required_symbols = [\"dossier_identity\"]"
               "expected_stdout = \"dossier:scalar:ok\""
               ""
               "[[checks]]"
               "id = \"seam-measured-record-abi\""
               "category = \"measured-record-abi\""
               "summary = \"Measured DossierPair layout constants verified in linked probe.\""
               "entry_source = " + quote probeRecord
               "required_symbols = [\"dossier_identity\"]"
               "expected_stdout = \"dossier:record:ok\""
               ""
               "[[checks]]"
               "id = \"seam-static-dynamic-link-elision\""
               "category = \"static-dynamic-link-elision\""
               "summary = \"Dead symbol dossier_unused_dead elided statically while exported in shared library.\""
               "entry_source = " + quote probeLink
               "required_symbols = [\"dossier_identity\"]"
               "expected_stdout = \"dossier:link:ok\""
               "[checks.elf]"
               "elided_symbols = [\"dossier_unused_dead\"]"
               "retained_static_symbols = [\"dossier_identity\"]"
               "forbidden_needed_libraries = [\"libdossier_probe.so\"]"
               "shared_library = " + quote lib.SharedLib
               "shared_library_sha256 = " + quote (ClangToolchain.sha256 lib.SharedLib)
               "dynamic_probe_source = " + quote dynProbeC
               "retained_dynamic_imports = [\"dossier_identity\"]"
               ""
               "[[checks]]"
               "id = \"seam-barewire-zero-copy\""
               "category = \"barewire-zero-copy\""
               "summary = \"StructDescriptor layout and result range boundary exercised in linked probe.\""
               "entry_source = " + quote probeZeroCopy
               "required_symbols = [\"dossier_identity\"]"
               "expected_stdout = \"dossier:zerocopy:ok\""
               ""
               "[[refusal_controls]]"
               "id = \"unlinked-extern\""
               "kind = \"pilot\""
               "target = " + quote missingPilot
               "expected_code = \"FS9228\""
               "misuse_summary = \"A pilot names a symbol that no archive member defines.\""
               "protection_summary = \"Static archive provenance refuses binding generation with FS9228.\""
               ""
               "[[refusal_controls]]"
               "id = \"raw-address-boundary\""
               "kind = \"composer\""
               "target = " + quote rawAddrProj
               "expected_code = \"CCS8401\""
               "misuse_summary = \"Authored Clef code takes a raw address-of operator outside safe descriptors.\""
               "protection_summary = \"Composer boundary admission refuses raw address-of with CCS8401.\"" ]
         SubstantiationSeams.evaluate
             { ManifestPath = lib.ManifestJson
               SpecPath = specPath
               OutputDirectory = Some outDir
               WorkDirectory = Some(Path.Combine(work, "seams-work"))
               Verbose = false }
         |> accepted)

let private writeText (label: string) (text: string) =
    let path = Path.Combine(scratch label, label + ".json")
    File.WriteAllText(path, text)
    path

let private requestFor (manifestPath: string) : SubstantiationDossier.Request =
    { ManifestPath = manifestPath
      OutputDirectory = Some(scratch "output")
      RocqProjectPath = None
      RocqFactsPath = None
      RocqConfigPath = None
      SeamChecksReceiptPath = None
      RefusalControlsReceiptPath = None }

let private withRocq (fixture: Rocq) (request: SubstantiationDossier.Request) =
    { request with RocqProjectPath = Some fixture.Project; RocqFactsPath = Some fixture.Facts; RocqConfigPath = Some fixture.Config }

/// The request is refused with exactly `code`, the refusal text holds `fragment`, and no
/// dossier file is written.
let private refusedWithText code (fragment: string) (request: SubstantiationDossier.Request) =
    let result = SubstantiationDossier.generate request
    refused code result
    match result with
    | Error refusal -> Assert.Contains(fragment, refusal.Text)
    | Ok _ -> ()
    match request.OutputDirectory with
    | Some output when Directory.Exists output && isNull (DirectoryInfo output).LinkTarget ->
        Assert.Empty(Directory.GetFiles(output, "*.dossier.*"))
    | _ -> ()

/// The request is refused with exactly `code`, and no dossier file is written.
let private refusedWith code (request: SubstantiationDossier.Request) = refusedWithText code "" request

/// A resealed copy of the manifest, written beside nothing else, as a dossier input.
let private resealed (lib: Library) (alter: Substantiation.Manifest -> Substantiation.Manifest) =
    let path = Path.Combine(scratch "manifest", "DossierProbe.substantiation.json")
    File.WriteAllText(path, lib.Manifest |> alter |> Substantiation.seal |> Substantiation.toJson)
    path

let private binderNote (name: string) (note: string) : SubstantiationDossier.DossierBinderNote = { Name = name; Note = note }

let private isAscii (text: string) = text |> Seq.forall (fun ch -> ch <= '\u007F')

let private sections =
    [ "1. Open -- Unfinished / Unproved Items"
      "2. Falsified -- Verified Guardrails and Operating Limits"
      "3. Checks -- Precondition Sanity and Real-Binary Seam Runs"
      "4. Guarantees -- Proven Safety Properties"
      "5. External C/OS and Domain Assumptions (Rests on)"
      "6. Symbol and Proof Traceability (Correspondence)" ]

// ---------------------------------------------------------------------------
// End-to-end generation
// ---------------------------------------------------------------------------

[<Fact>]
let ``dossier API and CLI write one reconciled report for a real library with Rocq and receipts`` () =
    let lib = library.Value
    let seams = realSeams.Value
    let fixture = rocq ()
    let seam = seams.SeamChecksPath
    let refusals = seams.RefusalControlsPath
    let request =
        { (requestFor lib.ManifestJson |> withRocq fixture) with
            SeamChecksReceiptPath = Some seam
            RefusalControlsReceiptPath = Some refusals }
    let generated = SubstantiationDossier.generate request |> accepted
    let dossier = generated.Dossier
    let cliOutput = scratch "cli-output"
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let launch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "dossier"; "--manifest"; lib.ManifestJson; "--output"; cliOutput
              "--rocq-project"; fixture.Project; "--rocq-facts"; fixture.Facts; "--rocq-config"; fixture.Config
              "--seam-checks"; seam; "--refusal-controls"; refusals ]
    let summary = run cliOutput "dossier-cli" launch
    let complete = "SUBSTANTIATED -- All compiler safety obligations are proven and all required target seam checks are recorded."
    Assert.Contains("Package: DossierProbe", summary)
    Assert.Contains("Overall status: " + complete, summary)
    Assert.Contains($"Open: {dossier.Counts.Open}", summary)
    Assert.Contains($"Falsified: {dossier.Counts.Falsified}", summary)
    Assert.Contains($"Checks: {dossier.Counts.Checks}", summary)
    Assert.Contains($"Guarantees: {dossier.Counts.Guarantees}", summary)
    // The CLI and the API write the same three documents, byte for byte.
    for extension, apiPath in [ "json", generated.JsonPath; "md", generated.MarkdownPath; "html", generated.HtmlPath ] do
        let cliPath = Path.Combine(cliOutput, "DossierProbe.dossier." + extension)
        Assert.Contains(cliPath, summary)
        Assert.Equal(Path.Combine(request.OutputDirectory.Value, "DossierProbe.dossier." + extension), apiPath)
        Assert.Equal<byte[]>(File.ReadAllBytes apiPath, File.ReadAllBytes cliPath)
    let json = File.ReadAllText generated.JsonPath
    let markdown = File.ReadAllText generated.MarkdownPath
    let html = File.ReadAllText generated.HtmlPath
    Assert.Equal<SubstantiationDossier.Dossier>(dossier, SubstantiationDossier.parseDossierJson json |> accepted)
    Assert.Equal(json, SubstantiationDossier.toJson dossier)
    Assert.Equal(1, dossier.SchemaVersion)
    Assert.Equal("Farscape Substantiation Report: DossierProbe", dossier.Title)
    Assert.Equal(complete, dossier.OverallStatus)
    Assert.Equal(lib.Manifest.IntegritySha256, dossier.ManifestIntegritySha256)
    // The fixture data is ASCII, so every byte of the three documents is 7-bit ASCII.
    for document in [ json; markdown; html ] do Assert.True(isAscii document, "a generated document carries a non-ASCII character")
    Assert.StartsWith("# Farscape Substantiation Report: DossierProbe\n", markdown)
    Assert.Contains("<h1>Farscape Substantiation Report: DossierProbe</h1>", html)
    Assert.Contains("**" + complete + "**", markdown)
    Assert.Contains("<p class=\"banner banner-good\">" + complete + "</p>", html)
    Assert.Contains("## How to Read This Report (No Formal-Methods Background Required)", markdown)
    for heading in sections do
        Assert.Contains("\n## " + heading + "\n", markdown)
        Assert.Contains("<h2>" + heading + "</h2>", html)
    Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\">", html)
    for element in [ "<header>"; "<section id=\"open\">"; "<section id=\"traceability\">"; "<table>"; "<style>"; "<span class=\"badge badge-good\">VERIFIED</span>" ] do
        Assert.Contains(element, html)
    // Legend health.
    Assert.Equal<string[]>(
        [| "Good (0 open)"; "Good (intentional protection)"; "Good (all recorded)"; "Good (proven)" |],
        dossier.Legend |> Array.map (fun row -> row.Health))
    Assert.Equal<string[]>(sections |> List.take 4 |> List.toArray, dossier.Legend |> Array.map (fun row -> row.Section))
    // Section 1.
    Assert.Equal(0, dossier.Open.CompilerOpenObligations)
    Assert.Equal(0, dossier.Open.CompilerRefutedObligations)
    Assert.Empty(dossier.Open.RocqOpen)
    Assert.Contains("- Compiler open safety obligations: 0\n- Compiler refuted safety obligations: 0\n- Companion Rocq open theorems (Admitted): 0. No unfinished proof obligations remain.\n", markdown)
    // Section 2.
    Assert.True(dossier.Falsified.CompilerNonVacuityReceipts > 0, "the result range contract must produce non-vacuity receipts")
    Assert.Contains($"{dossier.Falsified.CompilerNonVacuityReceipts} verified clause witness(es) confirming each safety conclusion can fail if its precondition is violated.", markdown)
    Assert.True(dossier.Falsified.RefusalControlsAttached)
    Assert.Equal<string[]>(
        [| "unlinked-extern"; "raw-address-boundary" |],
        dossier.Falsified.RefusalControls |> Array.map _.Id)
    Assert.Contains("| Id | ExpectedCode | MisuseSummary | ProtectionSummary |", markdown)
    Assert.DoesNotContain("No external compile-time refusal receipt was attached", markdown)
    let falsified = Assert.Single dossier.Falsified.RocqFalsified
    Assert.Equal("DossierProofs.Limits.unbounded_input_falsified", falsified.Symbol)
    Assert.Equal("~ (forall n : nat, n = 0)", falsified.Proposition)
    Assert.Equal("Dropping the range precondition admits a failing input.", falsified.Summary)
    // Section 3.
    let proof = lib.Manifest.Proof
    Assert.True(proof.CompilerPremiseReceiptsCount > 0, "the result range contract must produce premise receipts")
    Assert.Contains(
        $"cvc5 verified satisfiable preconditions on {proof.PremisesSatCount}/{proof.TotalObligations} total obligations ({proof.LibraryPremisesSatCount}/{proof.LibraryObligations} in exported library modules; {proof.CompilerPremiseReceiptsCount} conditional branch probe(s)).",
        markdown)
    Assert.Equal<string[]>(lib.Manifest.Boundary.RequiredSeamChecks, dossier.Checks.SeamCategories |> Array.map (fun category -> category.Category))
    Assert.Contains("measured-record-abi", lib.Manifest.Boundary.RequiredSeamChecks)
    Assert.All(dossier.Checks.SeamCategories, fun category ->
        Assert.Equal("VERIFIED", category.Status)
        Assert.NotEmpty category.Evidence)
    let measuredSeam = dossier.Checks.SeamCategories |> Array.find (fun category -> category.Category = "measured-record-abi")
    let measuredCheck = seams.SeamChecks.Checks |> Array.find (fun c -> c.Id = "seam-measured-record-abi")
    Assert.Equal("Pass and return by-value C structs matching probed byte size, alignment, and field offsets", measuredSeam.Description)
    Assert.Equal<string[]>([| $"seam-measured-record-abi (binary sha256:{measuredCheck.BinarySha256}; target {lib.Manifest.Boundary.ClangTarget})" |], measuredSeam.Evidence)
    let controlsSeam = dossier.Checks.SeamCategories |> Array.find (fun category -> category.Category = "falsification-refusal-controls")
    Assert.Equal<string[]>([| "unlinked-extern (FS9228)"; "raw-address-boundary (CCS8401)" |], controlsSeam.Evidence)
    Assert.Equal(0, dossier.Counts.PendingSeamCategories)
    let check = Assert.Single dossier.Checks.RocqChecks
    Assert.Equal("DossierProofs.Limits.seven_is_admitted", check.Symbol)
    // Section 4.
    let trust = "Depends on external C/OS contract (Rests on: H-ABI-1, H-Link-1, H-Contract-1)"
    Assert.Equal(trust, dossier.Guarantees.TrustScope)
    Assert.Equal(proof.LibraryObligations, dossier.Guarantees.LibraryObligations.Length)
    Assert.All(dossier.Guarantees.LibraryObligations, fun obligation ->
        Assert.Equal(trust, obligation.TrustScope)
        Assert.False(String.IsNullOrWhiteSpace obligation.Kind)
        Assert.StartsWith(lib.Scalar + ":", obligation.Source))
    let foreignResult = dossier.Guarantees.LibraryObligations |> Array.filter (fun obligation -> obligation.Kind = "foreign-result-coverage")
    Assert.NotEmpty foreignResult
    Assert.All(foreignResult, fun obligation ->
        Assert.Equal("Every foreign return sentinel and result branch is exhaustively handled", obligation.Guarantee)
        Assert.Equal("Dossier.Probe.Scalar.seven", obligation.Declaration)
        Assert.False obligation.ModuleLevel)
    Assert.Contains(dossier.Guarantees.LibraryObligations, fun obligation ->
        obligation.Kind = "integer-literal-range" && obligation.Guarantee = "Proven compiler obligation (integer-literal-range)" && obligation.Declaration = "Dossier.Probe.Scalar.three")
    Assert.Contains($"Activation harness obligations proven: {proof.HarnessObligations}/{proof.HarnessObligations} in Farscape.SubstantiationRoot.", markdown)
    Assert.Equal<string[]>(
        [| "DossierProofs.Laws.identity_returns_input"; "DossierProofs.Laws.pair_layout_sound" |],
        dossier.Guarantees.RocqGuarantees |> Array.map (fun entry -> entry.Symbol))
    let closedGuarantee = dossier.Guarantees.RocqGuarantees[0]
    Assert.Equal("closed", closedGuarantee.Assumptions)
    Assert.Equal("Self-contained (no external assumptions)", closedGuarantee.TrustScope)
    Assert.Equal("The identity result equals its argument <script>alert(1)</script> & holds for every input.", closedGuarantee.Summary)
    Assert.Equal("forall n : nat, n = n", closedGuarantee.Proposition)
    Assert.Equal<SubstantiationDossier.DossierBinderNote[]>([| binderNote "n" "the value passed to the foreign identity function." |], closedGuarantee.BinderNotes)
    Assert.Equal<string[]>([| "Dossier.Probe.Scalar.seven" |], closedGuarantee.CorrespondsTo)
    Assert.EndsWith("theories/Laws.v:7", closedGuarantee.Source)
    let axiomGuarantee = dossier.Guarantees.RocqGuarantees[1]
    Assert.Equal("rests-on", axiomGuarantee.Assumptions)
    Assert.Equal<string[]>([| "H_c_abi" |], axiomGuarantee.Axioms)
    Assert.Equal("Depends on external assumptions (Rests on: H_c_abi)", axiomGuarantee.TrustScope)
    Assert.Equal<SubstantiationDossier.DossierBinderNote[]>(
        [| binderNote "a" "the left field."; binderNote "b" "the right field, measured at bit offset 32." |],
        axiomGuarantee.BinderNotes)
    Assert.Equal<string[]>([| "H-ABI-1"; "DossierPair" |], axiomGuarantee.CorrespondsTo)
    // A Lemma is an internal helper: it is in no group and in no document.
    for document in [ json; markdown; html ] do Assert.DoesNotContain("add_zero_helper", document)
    // Section 5.
    Assert.Equal<string[]>([| "H-ABI-1"; "H-Link-1"; "H-Contract-1" |], dossier.Assumptions.Hypotheses |> Array.map (fun hypothesis -> hypothesis.Id))
    Assert.Equal("Binary Layout and Calling Convention (H-ABI)", dossier.Assumptions.Hypotheses[0].Category)
    Assert.Equal<string[]>([| "scalar-handle-link-and-call"; "measured-record-abi" |], dossier.Assumptions.Hypotheses[0].TestedBy)
    Assert.Equal("Linked Library Binary Identity (H-Link)", dossier.Assumptions.Hypotheses[1].Category)
    Assert.Equal<string[]>([| "scalar-handle-link-and-call"; "static-dynamic-link-elision" |], dossier.Assumptions.Hypotheses[1].TestedBy)
    Assert.Equal("barewire-zero-copy", Assert.Single dossier.Assumptions.Hypotheses[2].TestedBy)
    Assert.Equal<string[]>([| "H_c_abi" |], dossier.Assumptions.RocqAxioms)
    // Section 6: every exported declaration is one row, traced through the PSG and the witness.
    let exported =
        lib.Manifest.Sources |> Array.collect (fun source ->
            Array.concat [ source.RuntimeDeclarations; source.ForeignDeclarations; source.CompileTimeDeclarations ])
    Assert.Equal<Set<string>>(Set.ofArray exported, dossier.Traceability.Declarations |> Array.map (fun row -> row.Declaration) |> Set.ofArray)
    Assert.Equal(exported.Length, dossier.Traceability.Declarations.Length)
    let row name = dossier.Traceability.Declarations |> Array.find (fun declaration -> declaration.Declaration = name)
    // The compiler names the FunctionDescriptor RecordExpr as the import's source boundary; the
    // parent walk attributes the witnessed import to the descriptor declaration's row.
    let descriptor = row "Dossier.Probe.Boundary.dossier_identityDescriptor"
    Assert.Equal("descriptor", descriptor.Kind)
    let link = Assert.Single descriptor.ForeignLinks
    Assert.Equal("dossier_probe", link.Library)
    Assert.Equal("dossier_identity", link.Symbol)
    Assert.Equal("probe.o", link.Member)
    Assert.Equal("global-strong", link.Binding)
    Assert.Matches("^[0-9a-f]{64}$", link.MemberSha256)
    let witness = Json.parseOrFail(File.ReadAllText lib.Manifest.WitnessUnits.Path)
    let emitted =
        witness
        |> jProp "units"
        |> Json.items
        |> List.collect (fun unitNode -> unitNode |> jProp "imports" |> Json.items)
        |> List.filter (fun import -> (import |> Json.prop "symbol" |> Option.bind Json.asString) = Some "dossier_identity")
        |> List.exactlyOne
    let emittedNode =
        emitted
        |> jProp "sourceBoundary"
        |> Json.prop "declaration"
        |> Option.bind Json.asInt
        |> Option.defaultValue -1L
        |> int
    Assert.NotEqual(descriptor.PsgNode, emittedNode)
    let foreign = row "Dossier.Probe.Boundary.dossier_identity"
    Assert.Equal("foreign", foreign.Kind)
    Assert.Equal<SubstantiationDossier.DossierForeignLink[]>([| link |], foreign.ForeignLinks)
    let measured = row "Dossier.Probe.Boundary.dossier_pair_sum"
    Assert.Equal<string[]>([| "DossierPair" |], measured.MeasuredTypes)
    Assert.Equal("dossier_pair_sum", (Assert.Single measured.ForeignLinks).Symbol)
    let typesSource = lib.Manifest.Sources |> Array.find (fun source -> source.Path = Path.GetFullPath lib.Types)
    Assert.Empty typesSource.RuntimeDeclarations
    Assert.Empty typesSource.ForeignDeclarations
    let expectedTypesDescriptors =
        [| "Dossier.Probe.Boundary.Types.DossierPair.Size"
           "Dossier.Probe.Boundary.Types.DossierPair.Alignment"
           "Dossier.Probe.Boundary.Types.DossierPair.leftOffset"
           "Dossier.Probe.Boundary.Types.DossierPair.rightOffset"
           "Dossier.Probe.Boundary.Types.DossierPair.Descriptor" |]
    Assert.Equal<string[]>(expectedTypesDescriptors, typesSource.CompileTimeDeclarations)
    for name in expectedTypesDescriptors do
        let typesRow = row name
        Assert.Equal("descriptor", typesRow.Kind)
        Assert.Empty typesRow.ForeignLinks
        Assert.Empty typesRow.MlirSymbols
        Assert.StartsWith(Path.GetFullPath lib.Types + ":", typesRow.Source)
    let seven = row "Dossier.Probe.Scalar.seven"
    Assert.Equal("runtime", seven.Kind)
    Assert.Equal<string[]>([| "Dossier.Probe.Scalar.seven" |], seven.MlirSymbols)
    Assert.Equal<string[]>([| "DossierProofs.Laws.identity_returns_input" |], seven.RocqTheorems)
    Assert.Equal<string[]>(
        dossier.Guarantees.LibraryObligations |> Array.filter (fun obligation -> obligation.Declaration = seven.Declaration) |> Array.map (fun obligation -> obligation.Anchor),
        seven.Obligations)
    Assert.NotEmpty seven.Obligations
    Assert.StartsWith(lib.Scalar + ":2:", seven.Source)
    Assert.Contains("| `Dossier.Probe.Boundary.dossier_identityDescriptor` | descriptor |", markdown)
    Assert.Contains("`dossier_probe / dossier_identity (probe.o)`", markdown)
    Assert.Contains("<code>dossier_probe / dossier_identity (probe.o)</code>", html)
    let layout = Assert.Single dossier.Traceability.Layouts
    Assert.Equal("DossierPair", layout.Name)
    Assert.Equal(64, layout.SizeBits)
    Assert.Equal<int[]>([| 0; 32 |], layout.FieldOffsetsBits)
    Assert.Contains("| `DossierPair` | record |", markdown)
    Assert.True(dossier.Traceability.RocqAttached)
    Assert.Equal("9.1.0", dossier.Traceability.RocqVersion)
    Assert.Equal<string[]>([| "DossierProofs.Laws"; "DossierProofs.Limits" |], dossier.Traceability.RocqUnits |> Array.map (fun unitEntry -> unitEntry.Module))
    Assert.Equal(md5 (File.ReadAllBytes fixture.Laws), dossier.Traceability.RocqUnits[0].SourceMd5)
    Assert.Equal(md5 (File.ReadAllBytes fixture.LawsGlob), dossier.Traceability.RocqUnits[0].GlobMd5)
    let digestItems = dossier.Traceability.Digests |> Array.map (fun digest -> digest.Item)
    for item in [ "Package"; "Manifest Integrity SHA-256"; "Fidproj SHA-256"; "Composer SHA-256"; "cvc5 Version and SHA-256"; "Clang Identity and Target"; "Link Mode"
                  "link-provenance"; "static-archive"; "pilot"; "header"; "Seams Ledger SHA-256"; "Rocq Version"; "Rocq Unit DossierProofs.Laws"; "Rocq Unit DossierProofs.Limits" ] do
        Assert.Contains(item, digestItems)
    Assert.Contains(dossier.Traceability.Digests, fun digest -> digest.Item = "static-archive" && digest.Path = lib.Archive && digest.Value = ClangToolchain.sha256 lib.Archive)
    // Rocq documentation is data: markup characters never reach either document unescaped.
    Assert.Contains("The identity result equals its argument &lt;script&gt;alert(1)&lt;/script&gt; &amp; holds for every input.", html)
    Assert.DoesNotContain("<script", html)
    Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; holds", markdown)
    Assert.DoesNotContain("<script", markdown)
    // The counts the CLI prints.
    Assert.Equal(0, dossier.Counts.Open)
    Assert.Equal(proof.CompilerNonVacuityReceiptsCount + 3, dossier.Counts.Falsified)
    Assert.Equal(proof.PremisesSatCount + (lib.Manifest.Boundary.RequiredSeamChecks.Length - 1) + 1, dossier.Counts.Checks)
    Assert.Equal(proof.DischargedUnsatCount + 2, dossier.Counts.Guarantees)

[<Fact>]
let ``an admitted Rocq theorem is reported open with warning health and the action note`` () =
    let lib = library.Value
    let fixture = rocqWith lawsSource (limitsSource + admittedSource) [ "prf", "Theorem", "pair_sum_commutes" ] id id
    let generated = requestFor lib.ManifestToml |> withRocq fixture |> SubstantiationDossier.generate |> accepted
    let dossier = generated.Dossier
    let markdown = File.ReadAllText generated.MarkdownPath
    let html = File.ReadAllText generated.HtmlPath
    let status = "WARNING: PARTIAL MECHANIZATION -- Compiler safety obligations are proven, but 1 or more companion Rocq theorems remain Admitted (unfinished) in Section 1."
    Assert.Equal(status, dossier.OverallStatus)
    Assert.Contains("<p class=\"banner banner-warning\">" + status + "</p>", html)
    Assert.Equal("Warning (unfinished Rocq proof)", dossier.Legend[0].Health)
    // No seam receipt was supplied: the third legend row reports the pending runs.
    Assert.Equal("Action on target change (seam runs pending)", dossier.Legend[2].Health)
    // facts.json carries no entry for the admitted theorem; it is still listed as open.
    let admitted = Assert.Single dossier.Open.RocqOpen
    Assert.Equal("DossierProofs.Limits.pair_sum_commutes", admitted.Symbol)
    Assert.Equal("Open", admitted.Group)
    Assert.Equal("The pair sum is commutative.", admitted.Summary)
    Assert.Equal("(a b : nat) : a + b = b + a", admitted.Proposition)
    Assert.Equal("", admitted.TrustScope)
    Assert.Equal(1, dossier.Counts.Open)
    Assert.Contains("- Companion Rocq open theorems (Admitted): 1\n", markdown)
    Assert.DoesNotContain("No unfinished proof obligations remain.", markdown)
    Assert.Contains("| Rocq Symbol | Source | Statement Summary |", markdown)
    Assert.Contains("| `DossierProofs.Limits.pair_sum_commutes` |", markdown)
    Assert.Contains("\nCaller action: Do not rely on this theorem's claim yet. Maintainer action: Replace Admitted with Qed.\n", markdown)
    Assert.Contains("<p>Caller action: Do not rely on this theorem&#39;s claim yet. Maintainer action: Replace Admitted with Qed.</p>", html)
    // The other groups are unchanged by the open theorem.
    Assert.Equal(2, dossier.Guarantees.RocqGuarantees.Length)
    Assert.Single dossier.Falsified.RocqFalsified |> ignore
    Assert.Single dossier.Checks.RocqChecks |> ignore

[<Fact>]
let ``an admitted Lemma and an admitted proof-mode Definition are reported open while every Theorem is proved`` () =
    let lib = library.Value
    let status = "WARNING: PARTIAL MECHANIZATION -- Compiler safety obligations are proven, but 1 or more companion Rocq theorems remain Admitted (unfinished) in Section 1."
    let symbolsOf (entries: SubstantiationDossier.DossierRocqEntry[]) = entries |> Array.map (fun entry -> entry.Symbol)
    let generate (fixture: Rocq) = requestFor lib.ManifestJson |> withRocq fixture |> SubstantiationDossier.generate |> accepted
    // The same project without the unfinished declarations reports no open item.
    let proved = (generate (rocq ())).Dossier
    Assert.Empty proved.Open.RocqOpen
    Assert.Equal(0, proved.Counts.Open)
    Assert.Equal("Good (0 open)", proved.Legend[0].Health)
    Assert.NotEqual<string>(status, proved.OverallStatus)
    // Every Theorem of both units ends in Qed; the Lemma and the Definition end in Admitted.
    // facts.json carries no entry for either, as for any constant Rocq did not check.
    let generated = generate (rocqWith lawsSource (limitsSource + unfinishedSource) unfinishedSymbols id id)
    let dossier = generated.Dossier
    let markdown = File.ReadAllText generated.MarkdownPath
    let html = File.ReadAllText generated.HtmlPath
    Assert.Equal<string[]>(
        [| "DossierProofs.Limits.helper_unfinished"; "DossierProofs.Limits.def_unfinished" |],
        symbolsOf dossier.Open.RocqOpen)
    let helper, definition = dossier.Open.RocqOpen[0], dossier.Open.RocqOpen[1]
    Assert.Equal("Lemma", helper.Keyword)
    Assert.Equal("An internal helper that is not proved.", helper.Summary)
    Assert.Equal(": 1 = 1", helper.Proposition)
    Assert.Equal("Definition", definition.Keyword)
    Assert.Equal("A value built in proof mode and not finished.", definition.Summary)
    Assert.Equal(": nat", definition.Proposition)
    for entry in dossier.Open.RocqOpen do
        Assert.Equal("Open", entry.Group)
        Assert.Equal("not-reported", entry.Assumptions)
        Assert.Empty entry.Axioms
        Assert.Equal("", entry.TrustScope)
        Assert.Empty entry.CorrespondsTo
    Assert.Equal(proved.Counts.Open + 2, dossier.Counts.Open)
    Assert.Equal(status, dossier.OverallStatus)
    Assert.Equal("Warning (unfinished Rocq proof)", dossier.Legend[0].Health)
    Assert.Contains("<p class=\"banner banner-warning\">" + status + "</p>", html)
    Assert.Contains("- Companion Rocq open theorems (Admitted): 2\n", markdown)
    Assert.DoesNotContain("No unfinished proof obligations remain.", markdown)
    Assert.Contains("| `DossierProofs.Limits.helper_unfinished` |", markdown)
    Assert.Contains("| `DossierProofs.Limits.def_unfinished` |", markdown)
    // The proved groups hold the public theorems only, exactly as without the open items.
    Assert.Equal<string[]>(symbolsOf proved.Guarantees.RocqGuarantees, symbolsOf dossier.Guarantees.RocqGuarantees)
    Assert.Equal<string[]>(symbolsOf proved.Falsified.RocqFalsified, symbolsOf dossier.Falsified.RocqFalsified)
    Assert.Equal<string[]>(symbolsOf proved.Checks.RocqChecks, symbolsOf dossier.Checks.RocqChecks)
    Assert.Equal(proved.Counts.Guarantees, dossier.Counts.Guarantees)
    // Each of the two alone is one open item.
    let lemmaOnly = unfinishedSource.Substring(0, unfinishedSource.IndexOf("(** A value built", StringComparison.Ordinal))
    let definitionOnly = "\n" + unfinishedSource.Substring(unfinishedSource.IndexOf("(** A value built", StringComparison.Ordinal))
    for source, symbol, name in
        [ lemmaOnly, unfinishedSymbols[0], "DossierProofs.Limits.helper_unfinished"
          definitionOnly, unfinishedSymbols[1], "DossierProofs.Limits.def_unfinished" ] do
        let single = (generate (rocqWith lawsSource (limitsSource + source) [ symbol ] id id)).Dossier
        Assert.Equal<string[]>([| name |], symbolsOf single.Open.RocqOpen)
        Assert.Equal(proved.Counts.Open + 1, single.Counts.Open)
        Assert.Equal(status, single.OverallStatus)
    // The remaining declaration forms: a Remark, an Example, an Instance, and the Program
    // Definition that owns an admitted obligation.
    let forms = (generate (rocqWith lawsSource (limitsSource + unfinishedFormsSource) unfinishedFormsSymbols id id)).Dossier
    Assert.Equal<(string * string)[]>(
        [| "DossierProofs.Limits.remark_unfinished", "Remark"
           "DossierProofs.Limits.example_unfinished", "Example"
           "DossierProofs.Limits.instance_unfinished", "Instance"
           "DossierProofs.Limits.program_unfinished", "Definition" |],
        forms.Open.RocqOpen |> Array.map (fun entry -> entry.Symbol, entry.Keyword))
    Assert.Equal(proved.Counts.Open + 4, forms.Counts.Open)
    Assert.Equal(status, forms.OverallStatus)
    // An open item outside the public theorem keywords is no Correspondence symbol.
    let mapped =
        rocqWith lawsSource (limitsSource + unfinishedSource) unfinishedSymbols id (
            jUpdate "Correspondence" (jAdd (correspondenceEntry "DossierProofs.Limits.helper_unfinished" [ "Dossier.Probe.Scalar.seven" ])))
    requestFor lib.ManifestJson |> withRocq mapped |> refusedWithText "FS9250" "is no public theorem statement"

[<Fact>]
let ``a dossier without Rocq or receipts reports pending seam runs and the fixed absence texts`` () =
    let lib = library.Value
    let generated = requestFor lib.ManifestJson |> SubstantiationDossier.generate |> accepted
    let dossier = generated.Dossier
    let markdown = File.ReadAllText generated.MarkdownPath
    let pending = "SUBSTANTIATED (COMPILER PROOFS COMPLETE; TARGET SEAM RUNS PENDING) -- All compiler safety obligations are proven; run target binary seam checks in Section 3 before production deployment."
    Assert.Equal(pending, dossier.OverallStatus)
    Assert.Equal(lib.Manifest.Boundary.RequiredSeamChecks.Length, dossier.Counts.PendingSeamCategories)
    Assert.All(dossier.Checks.SeamCategories, fun category ->
        Assert.Equal("PENDING TARGET RUN", category.Status)
        Assert.Empty category.Evidence)
    Assert.False(dossier.Traceability.RocqAttached)
    Assert.False(dossier.Falsified.RefusalControlsAttached)
    for absence in
        [ "No external compile-time refusal receipt was attached to this dossier run (--refusal-controls)."
          "No companion Rocq counterexample theorems attached."
          "No companion Rocq check witnesses attached."
          "Companion Rocq open theorems (Admitted): 0. No unfinished proof obligations remain." ] do
        Assert.Contains(absence, markdown)
    Assert.DoesNotContain("### Companion Rocq Guarantees", markdown)
    Assert.Contains("### Companion Rocq counterexamples (\\*\\_falsified)", markdown)
    Assert.Contains("<h3>Companion Rocq counterexamples (*_falsified)</h3>", File.ReadAllText generated.HtmlPath)
    // The same manifest in its TOML form yields the same report.
    let fromToml = requestFor lib.ManifestToml |> SubstantiationDossier.generate |> accepted
    Assert.Equal<SubstantiationDossier.Dossier>({ dossier with ManifestPath = lib.ManifestToml; Traceability = fromToml.Dossier.Traceability }, fromToml.Dossier)
    Assert.Equal<SubstantiationDossier.DossierDeclaration[]>(dossier.Traceability.Declarations, fromToml.Dossier.Traceability.Declarations)
    // A partial receipt set leaves exactly the unrecorded categories pending, while still
    // requiring a co-located seams ledger binding the recorded probe binary on disk.
    let seams = realSeams.Value
    let partialDir = scratch "partial-seams-receipts"
    let firstRealCheck = seams.SeamChecks.Checks[0]
    let partialSeamReceipt = { seams.SeamChecks with Checks = [| firstRealCheck |] }
    let partialSeamPath = Path.Combine(partialDir, "DossierProbe.seam-checks.json")
    let partialRefusalPath = Path.Combine(partialDir, "DossierProbe.refusal-controls.json")
    let partialLedgerPath = Path.Combine(partialDir, "DossierProbe.seams-ledger.json")
    File.WriteAllText(partialSeamPath, seamSeamsChecksToJsonValue partialSeamReceipt |> Json.serializePretty)
    File.Copy(seams.RefusalControlsPath, partialRefusalPath, true)
    let partialLedger =
        { seams.Ledger with
            VerifiedPhysicalCategories = [| firstRealCheck.Category |]
            RetainedBinaries = seams.Ledger.RetainedBinaries |> Array.filter (fun b -> b.CheckId = firstRealCheck.Id)
            SharedLibraries = [||]
            SeamChecksPath = partialSeamPath
            SeamChecksSha256 = ClangToolchain.sha256 partialSeamPath
            RefusalControlsPath = partialRefusalPath
            RefusalControlsSha256 = ClangToolchain.sha256 partialRefusalPath }
    File.WriteAllText(partialLedgerPath, seamsLedgerToJsonValue partialLedger |> Json.serializePretty)
    let partly =
        { requestFor lib.ManifestJson with SeamChecksReceiptPath = Some partialSeamPath }
        |> SubstantiationDossier.generate |> accepted
    Assert.Equal(pending, partly.Dossier.OverallStatus)
    Assert.Equal(lib.Manifest.Boundary.RequiredSeamChecks.Length - 1, partly.Dossier.Counts.PendingSeamCategories)
    Assert.Equal("VERIFIED", partly.Dossier.Checks.SeamCategories[0].Status)

[<Fact>]
let ``obligation kinds map to the fixed guarantee texts`` () =
    for kind, text in
        [ "ForeignResultCoverage", "Every foreign return sentinel and result branch is exhaustively handled"
          "ResourceConversion", "Foreign handle acquisition and null/validity conversion preserve ownership invariants"
          "ResourceCleanup", "Owned foreign resource is released along every execution path"
          "SnapshotBorrowBound", "Borrowed memory view access stays within proven byte capacity and lifetime"
          "OptionalEntryArgumentConversion", "Optional foreign argument conversion preserves presence and pointer validity"
          "OptionalEntryAbsenceArgumentConversion", "Absent optional foreign argument passes the exact null/sentinel representation" ] do
        Assert.Equal(text, SubstantiationDossier.guaranteeOf (Some kind))
    // Composer writes kinds in kebab-case; both spellings of one kind select one text.
    Assert.Equal(SubstantiationDossier.guaranteeOf (Some "ResourceCleanup"), SubstantiationDossier.guaranteeOf (Some "resource-cleanup"))
    Assert.Equal("Proven compiler obligation (numeric-carrier-coverage)", SubstantiationDossier.guaranteeOf (Some "numeric-carrier-coverage"))
    Assert.Equal("Proven compiler safety obligation", SubstantiationDossier.guaranteeOf None)

[<Fact>]
let ``dossier JSON parsing is a strict round trip and HTML escapes every non-ASCII character`` () =
    let lib = library.Value
    let dossier = (requestFor lib.ManifestJson |> withRocq (rocq ()) |> SubstantiationDossier.generate |> accepted).Dossier
    let json = SubstantiationDossier.toJson dossier
    Assert.Equal<SubstantiationDossier.Dossier>(dossier, SubstantiationDossier.parseDossierJson json |> accepted)
    let edited (alter: JsonValue -> JsonValue) =
        Json.parseOrFail json |> alter |> Json.serialize
    for malformed in
        [ "{"
          "[]"
          edited (jRemove "Package")
          edited (jSet "Unknown" (Json.ofInt64 1L))
          edited (jSet "Package" JsonValue.Null)
          edited (jSet "SchemaVersion" (Json.ofInt64 2L))
          edited (jUpdate "Counts" (jRemove "Open"))
          json.Replace("\"SchemaVersion\": 1,", "\"SchemaVersion\": 1,\n  \"SchemaVersion\": 1,") ] do
        SubstantiationDossier.parseDossierJson malformed |> refused "FS9247"
    // A non-ASCII proposition and markup in a symbol reach HTML only as character references.
    let entry = { dossier.Guarantees.RocqGuarantees[0] with Proposition = "\u2200 n : nat, n = n \U0001D53D"; Symbol = "A.<b>\"'&" }
    let html = SubstantiationDossier.toHtml { dossier with Guarantees = { dossier.Guarantees with RocqGuarantees = [| entry |] } }
    Assert.True(isAscii html)
    Assert.Contains("<code>&#x2200; n : nat, n = n &#x1D53D;</code>", html)
    Assert.Contains("<code>A.&lt;b&gt;&quot;&#39;&amp;</code>", html)
    let markdown = SubstantiationDossier.toMarkdown { dossier with Guarantees = { dossier.Guarantees with RocqGuarantees = [| { entry with Summary = "a|b `c` <d> [e] *f* _g_ \\h"; Symbol = "x`y|z" } |] } }
    Assert.Contains("a\\|b \\`c\\` &lt;d&gt; \\[e\\] \\*f\\* \\_g\\_ \\\\h", markdown)
    Assert.Contains("``x`y\\|z``", markdown)

// ---------------------------------------------------------------------------
// Fail-closed controls
// ---------------------------------------------------------------------------

[<Fact>]
let ``stale manifest evidence refuses with FS9248`` () =
    let lib = library.Value
    let manifest = lib.Manifest
    let artifact name = Path.Combine(manifest.ArtifactsDirectory, name)
    let evidence =
        [ lib.Scalar
          artifact "06c_psg_discharge.json"
          artifact "06a_obligations.json"
          artifact "06b_premises_sat.stdout"
          manifest.WitnessUnits.Path
          manifest.Psg0.Path
          lib.Provenance
          lib.Archive
          manifest.FidprojPath
          manifest.Proof.StageReceipts[0].Path ]
    for path in evidence do
        changedBytes path (fun bytes -> Array.append bytes [| 10uy |]) (fun () ->
            requestFor lib.ManifestJson |> refusedWith "FS9248")
        let held = path + ".held"
        try
            File.Move(path, held)
            requestFor lib.ManifestJson |> refusedWith "FS9248"
        finally
            File.Move(held, path)
    // A tool whose bytes differ from the recorded digest is stale evidence as well.
    resealed lib (fun manifest -> { manifest with Compiler = { manifest.Compiler with Sha256 = String.replicate 64 "0" } })
    |> requestFor |> refusedWith "FS9248"
    requestFor lib.ManifestJson |> SubstantiationDossier.generate |> accepted |> ignore

[<Fact>]
let ``stale Rocq and receipt digests refuse with FS9248`` () =
    let lib = library.Value
    // The .v bytes no longer match the DIGEST line of the .glob.
    let stale = rocq ()
    File.AppendAllText(stale.Laws, "\n")
    requestFor lib.ManifestJson |> withRocq stale |> refusedWith "FS9248"
    // facts.json records a digest that differs from the .glob DIGEST line.
    let digest path (alter: JsonValue -> JsonValue) = rocqWith lawsSource limitsSource [] (jUpdate "digests" alter) id |> path
    for facts in
        [ digest id (jSet "theories/Laws.v" (JsonValue.String(String.replicate 32 "0")))
          digest id (jRemove "theories/Limits.v")
          digest id (jSet "theories/Extra.v" (JsonValue.String(String.replicate 32 "0"))) ] do
        requestFor lib.ManifestJson |> withRocq facts |> refusedWith "FS9248"
    // A receipt that records another manifest's integrity digest.
    let other = String.replicate 64 "a"
    { requestFor lib.ManifestJson with SeamChecksReceiptPath = Some(writeSeamJson "seam-checks" { seamReceipt lib with ManifestIntegritySha256 = other }) }
    |> refusedWith "FS9248"
    { requestFor lib.ManifestJson with RefusalControlsReceiptPath = Some(writeRefusalJson "refusal-controls" { refusalReceipt lib with ManifestIntegritySha256 = other }) }
    |> refusedWith "FS9248"

[<Fact>]
let ``an invalid companion Rocq project refuses with FS9249`` () =
    let lib = library.Value
    let refusedRocq (fixture: Rocq) = requestFor lib.ManifestJson |> withRocq fixture |> refusedWith "FS9249"
    // GlobMissing.
    let missingGlob = rocq ()
    File.Delete missingGlob.LawsGlob
    refusedRocq missingGlob
    // GlobHasNoDigest.
    let noDigest = rocq ()
    File.WriteAllText(noDigest.LawsGlob, (File.ReadAllText noDigest.LawsGlob).Replace("DIGEST " + md5 (File.ReadAllBytes noDigest.Laws), "DIGEST NO"))
    refusedRocq noDigest
    // SourceMissing and ProjectFileMissing.
    let missingSource = rocq ()
    File.Delete missingSource.Laws
    refusedRocq missingSource
    let missingProject = rocq ()
    File.Delete missingProject.Project
    refusedRocq missingProject
    // A project that lists no unit.
    let empty = rocqWith lawsSource limitsSource [] (jSet "digests" (JsonValue.Object [])) id
    File.WriteAllText(empty.Project, "-Q theories DossierProofs\n")
    refusedRocq empty
    // A proved public theorem without a fact, without a type, or without assumptions.
    let symbols (alter: JsonValue -> JsonValue) = rocqWith lawsSource limitsSource [] (jUpdate "symbols" alter) id
    refusedRocq (symbols (jRemove "DossierProofs.Laws.identity_returns_input"))
    refusedRocq (symbols (jRemove "DossierProofs.Limits.unbounded_input_falsified"))
    refusedRocq (symbols (jRemove "DossierProofs.Limits.seven_is_admitted"))
    refusedRocq (symbols (jUpdate "DossierProofs.Laws.identity_returns_input" (jRemove "assumptions")))
    refusedRocq (symbols (jUpdate "DossierProofs.Limits.seven_is_admitted" (jRemove "type")))
    // Binder documentation that names no written binder, repeats one, or reverses the order.
    let laws (before: string) (after: string) =
        Assert.Contains(before, lawsSource)
        rocqWith (lawsSource.Replace(before, after)) limitsSource [] id id
    refusedRocq (laws "- [n:] the value passed" "- [wrong_binder:] the value passed")
    refusedRocq (laws "    - [n:] the value passed to the foreign identity function. *)" "    - [n:] the value passed.\n    - [n:] the same binder again. *)")
    refusedRocq (laws "    - [a:] the left field.\n    - [b:] the right field,\n      measured at bit offset 32. *)" "    - [b:] the right field.\n    - [a:] the left field. *)")
    // The same check applies to an internal Lemma, which is no public entry.
    refusedRocq (laws "(** A helper fact about addition. *)" "(** A helper fact about addition.\n    - [m:] no such binder. *)")
    // A subsequence of the written binders, in written order, is accepted.
    let subsequence = laws "    - [a:] the left field.\n    - [b:] the right field,\n      measured at bit offset 32. *)" "    - [b:] the right field. *)"
    let generated = requestFor lib.ManifestJson |> withRocq subsequence |> SubstantiationDossier.generate |> accepted
    Assert.Equal<SubstantiationDossier.DossierBinderNote[]>([| binderNote "b" "the right field." |], generated.Dossier.Guarantees.RocqGuarantees[1].BinderNotes)

[<Fact>]
let ``an unterminated Rocq unit and an admission that no declaration owns refuse with FS9249`` () =
    let lib = library.Value
    // Each unit carries a current .glob and, where a theorem is named, a .glob symbol and a
    // facts.json entry for it. Without the refusal the theorem would be listed as proved.
    let limitsWith (extra: string) (symbols: (string * string * string) list) =
        rocqWith lawsSource (limitsSource + extra) symbols (fun facts ->
            symbols
            |> List.fold (fun acc (_, _, name) ->
                jUpdate "symbols" (jSet ("DossierProofs.Limits." + name) (fact "True" (closed ()))) acc) facts) id
    let refusedUnit (fragment: string) (fixture: Rocq) =
        requestFor lib.ManifestJson |> withRocq fixture |> refusedWithText "FS9249" fragment
    // Control: the same theorem, terminated and closed by Qed, is a guarantee.
    let complete = limitsWith "\nTheorem closed_proof : True.\nProof. exact I. Qed.\n" [ "prf", "Theorem", "closed_proof" ]
    let proved = (requestFor lib.ManifestJson |> withRocq complete |> SubstantiationDossier.generate |> accepted).Dossier
    Assert.Contains("DossierProofs.Limits.closed_proof", proved.Guarantees.RocqGuarantees |> Array.map (fun entry -> entry.Symbol))
    Assert.Empty proved.Open.RocqOpen
    // The unit ends inside a declaration sentence: the statement has no terminator.
    refusedUnit "ends inside a sentence that has no terminator" (limitsWith "\nTheorem dangling : True" [])
    refusedUnit "ends inside a sentence that has no terminator" (limitsWith "\nTheorem closed_proof : True.\nProof. exact I. Qed.\nLemma dangling : 1 = 1" [ "prf", "Theorem", "closed_proof" ])
    // The unit ends inside a proof: no Qed, Defined, Admitted or Abort closes it.
    refusedUnit "ends inside the proof of 'open_proof'" (limitsWith "\nTheorem open_proof : True.\nProof. exact I.\n" [ "prf", "Theorem", "open_proof" ])
    refusedUnit "ends inside the proof of 'open_definition'" (limitsWith "\nDefinition open_definition : nat.\nProof. exact 0.\n" [ "def", "Definition", "open_definition" ])
    // The statement is the last sentence of the unit, so the proof it opens has no sentence.
    refusedUnit "the file ends inside a proof" (limitsWith "\nTheorem last_statement : True.\n" [ "prf", "Theorem", "last_statement" ])
    // The unit ends inside a comment that encloses no sentence.
    refusedUnit "unterminated comment" (limitsWith "\n(* Lemma hidden : False. Admitted." [])
    // An Admitted sentence that closes no proof of a declaration: an obligation with no
    // Program declaration before it, and Admitted with no proof open.
    refusedUnit "This Admitted sentence closes no proof of a declaration" (limitsWith "\nNext Obligation. Admitted.\n" [])
    refusedUnit "This Admitted sentence closes no proof of a declaration" (limitsWith "\nAdmitted.\n" [])
    // Admit Obligations admits the obligations of a Program declaration without an Admitted sentence.
    refusedUnit "Admit Obligations admits obligations" (limitsWith "\nProgram Definition program_admitted : nat := _.\nAdmit Obligations.\n" [ "def", "Definition", "program_admitted" ])
    // An Admitted declaration that has no name cannot be listed.
    refusedUnit "declaration has no name" (limitsWith "\nLemma unnamed_in_glob : 1 = 1.\nAdmitted.\n" [])

[<Fact>]
let ``unreconciled correspondence refuses with FS9250`` () =
    let lib = library.Value
    let manifest = lib.Manifest
    // Rocq correspondence naming no Clef declaration, no Rocq statement, or an internal Lemma.
    let mapping (symbol: string) (target: string) =
        rocqWith lawsSource limitsSource [] id (
            jUpdate "Correspondence" (jAdd (correspondenceEntry symbol [ target ])))
    for fixture in
        [ mapping "DossierProofs.Limits.seven_is_admitted" "Dossier.Probe.Scalar.absent"
          mapping "DossierProofs.Limits.seven_is_admitted" "H-Callback-1"
          mapping "DossierProofs.Limits.absent" "Dossier.Probe.Scalar.seven"
          mapping "DossierProofs.Laws.add_zero_helper" "Dossier.Probe.Scalar.seven" ] do
        requestFor lib.ManifestJson |> withRocq fixture |> refusedWith "FS9250"
    // Each admitted target kind resolves: a declaration, a measured layout and a hypothesis.
    for target in [ "Dossier.Probe.Boundary.dossier_pair_sumDescriptor"; "DossierPair"; "H-Link-1" ] do
        requestFor lib.ManifestJson |> withRocq (mapping "DossierProofs.Limits.seven_is_admitted" target) |> SubstantiationDossier.generate |> accepted |> ignore
    // A witness manifest rebound to a valid digest still has to reconcile.
    let witness expected (alter: JsonValue -> JsonValue) =
        changedBytes manifest.WitnessUnits.Path (fun bytes ->
            let json = Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) |> alter
            Encoding.UTF8.GetBytes(Json.serialize json)) (fun () ->
                let request =
                    resealed lib (fun manifest -> { manifest with WitnessUnits = { manifest.WitnessUnits with Sha256 = ClangToolchain.sha256 manifest.WitnessUnits.Path } })
                    |> requestFor
                if expected = "" then SubstantiationDossier.generate request |> accepted |> ignore
                else refusedWith expected request)
    let updateObligations (f: JsonValue -> JsonValue) = jUpdate "proof" (jUpdate "obligations" f)
    witness "" id
    // The obligation list of the witness against the retained obligation inventory.
    witness "FS9250" (updateObligations (jUpdateAt 0 (jSet "kind" (JsonValue.String ""))))
    witness "FS9250" (updateObligations (jUpdateAt 0 (jSet "kind" (JsonValue.String "forged-kind"))))
    witness "FS9250" (updateObligations (jUpdateAt 0 (jSet "source" (JsonValue.String ("/" + "forged/Source.clef:1:1")))))
    witness "FS9250" (updateObligations (jUpdateAt 0 (jSet "anchor" (JsonValue.String "forged_anchor"))))
    witness "FS9250" (updateObligations (jRemoveAt 0))
    witness "FS9250" (updateObligations (fun arr ->
        match arr with
        | JsonValue.Array (first :: _ as items) -> JsonValue.Array (items @ [ first ])
        | other -> other))
    // The source proof receipt embedded in the witness against 06c_psg_discharge.json.
    witness "FS9250" (jUpdate "proof" (jUpdate "source" (jSet "Invocation" (JsonValue.String(Guid.NewGuid().ToString())))))
    witness "FS9250" (jUpdate "proof" (jUpdate "source" (jSet "QuerySha256" (JsonValue.String(String.replicate 64 "0")))))
    // A witnessed definition whose source node is a library node and whose traversal root is not.
    witness "FS9250" (fun json ->
        let defs = json |> jProp "units" |> Json.items |> List.head |> jProp "definitions" |> Json.items
        let mainRoot =
            defs
            |> List.find (fun entry -> (entry |> Json.prop "symbol" |> Option.bind Json.asString) = Some "main")
            |> jProp "traversalRoot"
        jUpdate "units" (jUpdateAt 0 (jUpdate "definitions" (fun arr ->
            match arr with
            | JsonValue.Array items ->
                items
                |> List.map (fun entry ->
                    if (entry |> Json.prop "symbol" |> Option.bind Json.asString) = Some "Dossier.Probe.Scalar.seven" then
                        jSet "traversalRoot" mainRoot entry
                    else
                        entry)
                |> JsonValue.Array
            | other -> other))) json)
    // Without a proof obligation list every obligation takes the fixed fallback text.
    changedBytes manifest.WitnessUnits.Path (fun bytes ->
        let json = Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) |> jSet "proof" JsonValue.Null
        Encoding.UTF8.GetBytes(Json.serialize json)) (fun () ->
            let generated =
                resealed lib (fun manifest -> { manifest with WitnessUnits = { manifest.WitnessUnits with Sha256 = ClangToolchain.sha256 manifest.WitnessUnits.Path } })
                |> requestFor |> SubstantiationDossier.generate |> accepted
            Assert.All(generated.Dossier.Guarantees.LibraryObligations, fun obligation ->
                Assert.Equal("", obligation.Kind)
                Assert.Equal("Proven compiler safety obligation", obligation.Guarantee)))
    // A forged pairing on the witnessed foreign import is a substantiation FS9243, reported
    // by the dossier as a correspondence failure.
    let updateEmittedImport (f: JsonValue -> JsonValue) (json: JsonValue) =
        jUpdate "units" (fun units ->
            match units with
            | JsonValue.Array unitItems ->
                unitItems
                |> List.map (jUpdate "imports" (fun imps ->
                    match imps with
                    | JsonValue.Array impItems ->
                        impItems
                        |> List.map (fun imp ->
                            if (imp |> Json.prop "symbol" |> Option.bind Json.asString) = Some "dossier_identity" then f imp else imp)
                        |> JsonValue.Array
                    | other -> other))
                |> JsonValue.Array
            | other -> other) json
    witness "FS9250" (updateEmittedImport (jUpdate "sourceBoundary" (jSet "library" (JsonValue.String "not_linked"))))
    witness "FS9250" (updateEmittedImport (jSet "symbol" (JsonValue.String "uninventoried_symbol")))
    // A valid symbol of the same library on the wrong FunctionDescriptor is refused as well:
    // the import of `dossier_identity` renamed to `dossier_pair_sum`, and the same import
    // moved to the `dossier_pair_sum` descriptor binding.
    let retainedPsg = Json.parseOrFail(File.ReadAllText(manifest.Psg0.Path).TrimStart('\uFEFF'))
    let pairSumDescriptor =
        retainedPsg
        |> jProp "nodes"
        |> Json.items
        |> List.find (fun node ->
            let kind = node |> Json.prop "kind" |> Option.bind Json.asString |> Option.defaultValue ""
            kind.StartsWith("Binding (\"dossier_pair_sumDescriptor\"", StringComparison.Ordinal))
        |> Json.prop "id"
        |> Option.bind Json.asInt
        |> Option.defaultValue -1L
        |> int
    witness "FS9250" (updateEmittedImport (jSet "symbol" (JsonValue.String "dossier_pair_sum")))
    witness "FS9250" (updateEmittedImport (jUpdate "sourceBoundary" (jSet "declaration" (Json.ofInt64 (int64 pairSumDescriptor)))))
    requestFor lib.ManifestJson |> SubstantiationDossier.generate |> accepted |> ignore

[<Fact>]
let ``a witnessed import retargeted to a result contract or a runtime binding refuses with FS9250`` () =
    let lib = library.Value
    let manifest = lib.Manifest
    let linked, identity = "dossier_probe", "dossier_identity"
    let correspondence = Substantiation.validateManifestCorrespondence manifest |> accepted
    let declaration (qualified: string) = correspondence.Declarations |> Array.find (fun node -> node.Qualified = qualified)
    let foreign = declaration "Dossier.Probe.Boundary.dossier_identity"
    let descriptor = declaration "Dossier.Probe.Boundary.dossier_identityDescriptor"
    let contract = declaration "Dossier.Probe.Boundary.identityResultContract"
    let runtime = declaration "Dossier.Probe.Scalar.seven"
    Assert.Equal(Some(linked, identity), foreign.Foreign)
    Assert.Equal(Substantiation.DescriptorSymbol.Named identity, descriptor.Descriptor)
    // The result contract and the runtime function declare no foreign symbol.
    for binding in [ contract; runtime ] do
        Assert.Equal(None, binding.Foreign)
        Assert.Equal(Substantiation.DescriptorSymbol.NotDescriptor, binding.Descriptor)
    let witnessed = Assert.Single(correspondence.Imports |> Array.filter (fun import -> import.Symbol = identity))
    Assert.Equal((linked, descriptor.Qualified), (witnessed.Library, witnessed.Declaration))
    // 1. The dossier's own reconciliation of a verified correspondence. The import keeps its
    //    library and symbol, which the library's FidelityExtern declaration declares; only
    //    the declaration it names changes.
    let owners (altered: Substantiation.Correspondence) : Result<unit, Diagnostics.Refusal> =
        try Ok(SubstantiationDossier.requireImportOwners manifest.WitnessUnits.Path altered)
        with :? Diagnostics.RefusalException as refusal -> Error refusal.Refusal
    let retargeted (owner: string) (node: int) : Substantiation.Correspondence =
        { correspondence with
            Imports =
                correspondence.Imports |> Array.map (fun import ->
                    if import.Symbol = identity then { import with Declaration = owner; DeclarationNode = node } else import) }
    let refusedOwner (fragment: string) (altered: Substantiation.Correspondence) =
        let result = owners altered
        refused "FS9250" result
        match result with
        | Error refusal -> Assert.Contains(fragment, refusal.Text)
        | Ok () -> ()
    owners correspondence |> accepted
    owners (retargeted descriptor.Qualified descriptor.BindingNodeId) |> accepted
    owners (retargeted foreign.Qualified foreign.BindingNodeId) |> accepted
    refusedOwner "is neither a FidelityExtern declaration nor a FunctionDescriptor" (retargeted contract.Qualified contract.BindingNodeId)
    refusedOwner "is neither a FidelityExtern declaration nor a FunctionDescriptor" (retargeted runtime.Qualified runtime.BindingNodeId)
    refusedOwner "is no unique module binding of the library" (retargeted "Dossier.Probe.Boundary.absent" 0)
    // 2. The same retargeting in the retained witness manifest, rebound to a valid digest
    //    and a valid envelope, through the complete generator.
    let witness expected (fragment: string) (node: int) =
        changedBytes manifest.WitnessUnits.Path (fun bytes ->
            let json =
                Json.parseOrFail(Encoding.UTF8.GetString(bytes).TrimStart('﻿'))
                |> jUpdate "units" (fun units ->
                    match units with
                    | JsonValue.Array unitItems ->
                        unitItems
                        |> List.map (jUpdate "imports" (fun imps ->
                            match imps with
                            | JsonValue.Array impItems ->
                                impItems
                                |> List.map (fun imp ->
                                    if (imp |> Json.prop "symbol" |> Option.bind Json.asString) = Some identity then
                                        jUpdate "sourceBoundary" (jSet "declaration" (Json.ofInt64 (int64 node))) imp
                                    else imp)
                                |> JsonValue.Array
                            | other -> other))
                        |> JsonValue.Array
                    | other -> other)
            Encoding.UTF8.GetBytes(Json.serialize json)) (fun () ->
                let request =
                    resealed lib (fun manifest -> { manifest with WitnessUnits = { manifest.WitnessUnits with Sha256 = ClangToolchain.sha256 manifest.WitnessUnits.Path } })
                    |> requestFor
                if expected = "" then SubstantiationDossier.generate request |> accepted |> ignore
                else refusedWithText expected fragment request)
    witness "" "" witnessed.DeclarationNode
    witness "" "" descriptor.BindingNodeId
    witness "" "" foreign.BindingNodeId
    witness "FS9250" "nor a FunctionDescriptor. (substantiation FS9243)" contract.BindingNodeId
    witness "FS9250" "(substantiation FS9243)" runtime.BindingNodeId
    requestFor lib.ManifestJson |> SubstantiationDossier.generate |> accepted |> ignore

[<Fact>]
let ``invalid seam and refusal receipts refuse with FS9251`` () =
    let lib = library.Value
    let seam = seamReceipt lib
    let first = seam.Checks[0]
    let seamWith (check: SubstantiationDossier.SeamCheckEntry) = { seam with Checks = Array.append [| check |] (Array.tail seam.Checks) }
    for receipt in
        [ { seam with SchemaVersion = 2 }
          { seam with Package = "OtherPackage" }
          { seam with TargetTriple = " " }
          { seam with TargetTriple = "not-measured" }
          { seam with TargetTriple = "aarch64-unknown-linux-gnu" }
          seamWith { first with Category = "unknown-category" }
          seamWith { first with Category = "falsification-refusal-controls" }
          seamWith { first with Category = "callback-registration-invocation" }
          seamWith { first with Passed = false }
          seamWith { first with Id = "" }
          seamWith { first with Id = seam.Checks[1].Id }
          seamWith { first with BinarySha256 = "abc" }
          seamWith { first with Summary = "" }
          seamWith { first with Command = " " } ] do
        { requestFor lib.ManifestJson with SeamChecksReceiptPath = Some(writeSeamJson "seam-checks" receipt) } |> refusedWith "FS9251"
    let refusals = refusalReceipt lib
    let control = refusals.Controls[0]
    for receipt in
        [ { refusals with SchemaVersion = 0 }
          { refusals with Package = "OtherPackage" }
          { refusals with Controls = [| { control with ObservedCode = "FS9242" } |] }
          { refusals with Controls = [| { control with ExpectedCode = "fs9243"; ObservedCode = "fs9243" } |] }
          { refusals with Controls = [| { control with ExpectedCode = "FS"; ObservedCode = "FS" } |] }
          { refusals with Controls = [| { control with Passed = false } |] }
          { refusals with Controls = [| { control with Id = " " } |] }
          { refusals with Controls = [| control; control |] }
          { refusals with Controls = [| { control with MisuseSummary = "" } |] }
          { refusals with Controls = [| { control with ProtectionSummary = "" } |] } ] do
        { requestFor lib.ManifestJson with RefusalControlsReceiptPath = Some(writeRefusalJson "refusal-controls" receipt) } |> refusedWith "FS9251"
    // Schema-valid seam-checks or refusal-controls receipts supplied WITHOUT their mandatory
    // co-located <Package>.seams-ledger.json receipt fail closed with FS9251 (#219 B3).
    { requestFor lib.ManifestJson with SeamChecksReceiptPath = Some(writeSeamJson "orphan-seam-checks" seam) }
    |> refusedWithText "FS9251" "lacks its required co-located DossierProbe.seams-ledger.json"
    { requestFor lib.ManifestJson with RefusalControlsReceiptPath = Some(writeRefusalJson "orphan-refusal-controls" refusals) }
    |> refusedWithText "FS9251" "lacks its required co-located DossierProbe.seams-ledger.json"
    // Valid receipts placed in two distinct directories resolve to distinct ledgers -> FS9251.
    let seams = realSeams.Value
    let splitDir = scratch "split-ledger-dir"
    let splitRefusalPath = Path.Combine(splitDir, "DossierProbe.refusal-controls.json")
    File.Copy(seams.RefusalControlsPath, splitRefusalPath, true)
    { requestFor lib.ManifestJson with
        SeamChecksReceiptPath = Some seams.SeamChecksPath
        RefusalControlsReceiptPath = Some splitRefusalPath }
    |> refusedWithText "FS9251" "distinct seams ledgers"

[<Fact>]
let ``a dossier is generated from and into OS temporary storage outside the checkout`` () =
    let lib = library.Value
    let request = requestFor lib.ManifestJson
    // Every input and output of this dossier is in OS temporary storage (#215).
    for path in [ lib.Work; lib.ManifestJson; lib.Manifest.FidprojPath; lib.Manifest.ArtifactsDirectory; request.OutputDirectory.Value ] do
        Assert.True(isWithin (Path.GetTempPath()) path, $"{path} is not in OS temporary storage")
        Assert.False(isWithin repository path, $"{path} is inside the checkout {repository}")
    let generated = SubstantiationDossier.generate request |> accepted
    for extension, written in [ "json", generated.JsonPath; "md", generated.MarkdownPath; "html", generated.HtmlPath ] do
        Assert.Equal(Path.Combine(request.OutputDirectory.Value, "DossierProbe.dossier." + extension), written)
        Assert.True(File.Exists written, $"{written} was not written")
    // A link to an existing directory is admitted, and the documents are in its target.
    let target = scratch "linked-output-target"
    let link = Path.Combine(scratch "linked-output", "link")
    Directory.CreateSymbolicLink(link, target) |> ignore
    let linked = SubstantiationDossier.generate { request with OutputDirectory = Some link } |> accepted
    Assert.Equal<SubstantiationDossier.Dossier>(generated.Dossier, linked.Dossier)
    Assert.Equal<string[]>(
        [| "DossierProbe.dossier.html"; "DossierProbe.dossier.json"; "DossierProbe.dossier.md" |],
        Directory.GetFiles target |> Array.map Path.GetFileName |> Array.sort)

[<Fact>]
let ``dossier paths that are empty or cross a broken or cyclic link refuse with FS9252`` () =
    let lib = library.Value
    let fixture = rocq ()
    let links = refusedLinks (scratch "refused-links")
    let valid = requestFor lib.ManifestJson
    for prefix in [ links.Broken; links.Chained; links.Cycle ] do
        for request in
            [ { valid with ManifestPath = Path.Combine(prefix, "DossierProbe.substantiation.json") }
              { valid with OutputDirectory = Some prefix }
              { valid with OutputDirectory = Some(Path.Combine(prefix, "nested")) }
              { (valid |> withRocq fixture) with RocqProjectPath = Some(Path.Combine(prefix, "_CoqProject")) }
              { (valid |> withRocq fixture) with RocqFactsPath = Some(Path.Combine(prefix, "facts.json")) }
              { (valid |> withRocq fixture) with RocqConfigPath = Some(Path.Combine(prefix, "config.json")) }
              { valid with SeamChecksReceiptPath = Some(Path.Combine(prefix, "seam.json")) }
              { valid with RefusalControlsReceiptPath = Some(Path.Combine(prefix, "controls.json")) } ] do
            refusedWith "FS9252" request
    // An explicit empty output directory does not select a default.
    for empty in [ ""; " " ] do
        refusedWith "FS9252" { valid with OutputDirectory = Some empty }
    // A unit of the Rocq project that is a symbolic link without its target.
    let linkedUnit = rocq ()
    File.Delete linkedUnit.Laws
    File.CreateSymbolicLink(linkedUnit.Laws, Path.Combine(scratch "absent-unit", "never-created.v")) |> ignore
    valid |> withRocq linkedUnit |> refusedWith "FS9252"
    // Evidence that the manifest records behind a refused link.
    resealed lib (fun manifest -> { manifest with WitnessUnits = { manifest.WitnessUnits with Path = Path.Combine(links.Broken, "10_witness_units.json") } })
    |> requestFor |> refusedWith "FS9252"
    resealed lib (fun manifest -> { manifest with Compiler = { manifest.Compiler with Path = Path.Combine(links.Cycle, "Composer.dll") } })
    |> requestFor |> refusedWith "FS9252"
    Assert.False(Directory.Exists links.Absent || File.Exists links.Absent, "a refusal must not create the target of a broken link")

[<Fact>]
let ``malformed or unpaired dossier inputs refuse with FS9247`` () =
    let lib = library.Value
    let fixture = rocq ()
    let valid = requestFor lib.ManifestJson
    // Pairing.
    refusedWith "FS9247" { valid with RocqProjectPath = Some fixture.Project }
    refusedWith "FS9247" { valid with RocqFactsPath = Some fixture.Facts }
    refusedWith "FS9247" { valid with RocqConfigPath = Some fixture.Config }
    // The manifest.
    refusedWith "FS9247" { valid with ManifestPath = Path.Combine(scratch "absent", "DossierProbe.substantiation.json") }
    let asText = Path.Combine(scratch "extension", "DossierProbe.substantiation.txt")
    File.Copy(lib.ManifestJson, asText)
    refusedWith "FS9247" { valid with ManifestPath = asText }
    let unsealed = Path.Combine(scratch "unsealed", "DossierProbe.substantiation.json")
    File.WriteAllText(unsealed, (File.ReadAllText lib.ManifestJson).Replace("\"Package\": \"DossierProbe\"", "\"Package\": \"Renamed\""))
    refusedWith "FS9247" { valid with ManifestPath = unsealed }
    refusedWith "FS9247" { valid with ManifestPath = writeText "broken-manifest" "{" }
    // A resealed manifest whose boundary contradicts its own source inventory.
    resealed lib (fun manifest -> { manifest with Boundary = { manifest.Boundary with ExternSymbols = [||] } })
    |> requestFor |> refusedWith "FS9247"
    // Receipts: a duplicate key, an unknown field, a missing field, a null and a wrong type.
    let seamJson = seamChecksToJsonValue (seamReceipt lib) |> Json.serialize
    let seamEdited (alter: JsonValue -> JsonValue) =
        Json.parseOrFail seamJson |> alter |> Json.serialize
    Assert.StartsWith("{\"SchemaVersion\":1,", seamJson)
    for text in
        [ "{\"SchemaVersion\":1," + seamJson.Substring 1
          seamEdited (jSet "Unknown" (JsonValue.Bool true))
          seamEdited (jRemove "TargetTriple")
          seamEdited (jSet "TargetTriple" JsonValue.Null)
          seamEdited (jSet "SchemaVersion" (JsonValue.String "1"))
          seamEdited (jUpdate "Checks" (jUpdateAt 0 (jRemove "Passed")))
          seamEdited (jUpdate "Checks" (jUpdateAt 0 (jSet "Passed" (JsonValue.String "true"))))
          "not json" ] do
        refusedWith "FS9247" { valid with SeamChecksReceiptPath = Some(writeText "seam-checks" text) }
    refusedWith "FS9247" { valid with SeamChecksReceiptPath = Some(Path.Combine(scratch "absent", "seam-checks.json")) }
    let refusalJson = refusalControlsToJsonValue (refusalReceipt lib) |> Json.serialize
    for text in
        [ "{\"Package\":\"DossierProbe\"," + refusalJson.Substring 1
          refusalJson.Replace("\"Passed\":true", "\"Passed\":null")
          refusalJson.Replace("\"Controls\":", "\"Extra\":0,\"Controls\":") ] do
        refusedWith "FS9247" { valid with RefusalControlsReceiptPath = Some(writeText "refusal-controls" text) }
    // The Rocq config.
    let config (alter: JsonValue -> JsonValue) = rocqWith lawsSource limitsSource [] id alter
    for malformed in
        [ config (jSet "SchemaVersion" (Json.ofInt64 2L))
          config (jSet "FalsifiedSuffix" (JsonValue.String ""))
          config (jSet "CheckMarker" (JsonValue.String ""))
          config (jRemove "CheckMarker")
          config (jSet "Extra" (Json.ofInt64 1L))
          config (jUpdate "Correspondence" (fun arr ->
              match arr with
              | JsonValue.Array (first :: _ as items) -> JsonValue.Array (items @ [ first ])
              | other -> other))
          config (jUpdate "Correspondence" (jUpdateAt 0 (jSet "Targets" (JsonValue.Array []))))
          config (jUpdate "Correspondence" (jUpdateAt 0 (jSet "RocqSymbol" (JsonValue.String " ")))) ] do
        valid |> withRocq malformed |> refusedWith "FS9247"
    // facts.json.
    let facts (alter: JsonValue -> JsonValue) = rocqWith lawsSource limitsSource [] alter id
    let updateTheorem (f: JsonValue -> JsonValue) = jUpdate "symbols" (jUpdate "DossierProofs.Laws.identity_returns_input" f)
    for malformed in
        [ facts (jSet "extra" (Json.ofInt64 1L))
          facts (jRemove "rocqVersion")
          facts (jSet "rocqVersion" (JsonValue.String ""))
          facts (jSet "digests" (JsonValue.Array []))
          facts (jUpdate "digests" (jSet "theories/Laws.v" (Json.ofInt64 7L)))
          facts (updateTheorem (jSet "assumptions" (JsonValue.String "open")))
          facts (updateTheorem (jSet "assumptions" (JsonValue.Array [])))
          facts (updateTheorem (jSet "assumptions" JsonValue.Null))
          facts (updateTheorem (jSet "type" (JsonValue.String "")))
          facts (updateTheorem (jRemove "binders"))
          facts (updateTheorem (jSet "binders" (JsonValue.Array [])))
          facts (updateTheorem (jSet "extra" (Json.ofInt64 1L))) ] do
        valid |> withRocq malformed |> refusedWith "FS9247"
    let duplicated = rocq ()
    File.WriteAllText(duplicated.Facts, (File.ReadAllText duplicated.Facts).Replace("\"rocqVersion\":\"9.1.0\",", "\"rocqVersion\":\"9.1.0\",\"rocqVersion\":\"9.1.0\","))
    valid |> withRocq duplicated |> refusedWith "FS9247"
    let absentFacts = rocq ()
    File.Delete absentFacts.Facts
    valid |> withRocq absentFacts |> refusedWith "FS9247"

[<Fact>]
let ``the dossier CLI reports a refusal on stderr and exits 1`` () =
    let lib = library.Value
    let fixture = rocq ()
    let output = scratch "cli-refusal"
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    let launch = ProcessLaunch.startInfo "dotnet" [ cli; "dossier"; "--manifest"; lib.ManifestJson; "--output"; output; "--rocq-project"; fixture.Project ]
    launch.WorkingDirectory <- output
    launch.Environment["TMPDIR"] <- output
    use child = new Process(StartInfo = launch)
    Assert.True(child.Start(), "the Farscape CLI did not start")
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    Assert.True(child.WaitForExit 120000, "the Farscape CLI did not exit")
    child.WaitForExit()
    Assert.Equal(1, child.ExitCode)
    Assert.Equal("", stdout.Result)
    Assert.StartsWith("FS9247: ", stderr.Result)
    Assert.Contains("FS9247 declared in " + fixture.Project, stderr.Result)
    Assert.Empty(Directory.GetFiles(output, "*.dossier.*"))

[<Fact>]
let ``generated measured value_structs and abi_critical_structs Types.clef substantiates end-to-end via CLI and refuses unsubstantiated callables with FS9246`` () =
    let work = scratch "generated-types-e2e"
    let header = Path.Combine(work, "measured_probe.h")
    let source = Path.Combine(work, "measured_probe.c")
    let objectFile = Path.Combine(work, "measured_probe.o")
    let archive = Path.Combine(work, "libmeasured_probe.a")
    writeLines header
        [ "typedef struct MeasuredPair { int left; int right; } MeasuredPair;"
          "struct CriticalFrame { unsigned int tag; long payload; };"
          "int measured_identity(int value);"
          "int measured_pair_sum(MeasuredPair pair);"
          "MeasuredPair measured_make_pair(int left, int right);" ]
    writeLines source
        [ "#include \"measured_probe.h\""
          "int measured_identity(int value) { return value; }"
          "int measured_pair_sum(MeasuredPair pair) { return pair.left + pair.right; }"
          "MeasuredPair measured_make_pair(int left, int right) { MeasuredPair p = { left, right }; return p; }" ]
    run work "measured-object" (ClangToolchain.startInfo [ "-c"; "-fPIC"; "-Wall"; "-Wextra"; "-Werror"; source; "-o"; objectFile ]) |> ignore
    run work "measured-archive" (ClangToolchain.arStartInfo [ "rcs"; archive; objectFile ]) |> ignore
    let pilot = Path.Combine(work, "Measured.pilot.toml")
    writeLines pilot
        [ "[library]"; "name = \"measured_probe\""; "headers = [" + quote header + "]"
          "include_paths = [" + quote work + "]"
          "[library.artifact]"; "kind = \"static-archive\""; "path = " + quote archive
          "sha256 = " + quote (ClangToolchain.sha256 archive)
          "[output]"; "mode = \"fidelity\""; "directory = \"generated\""
          "[options]"; "c_header_mode = true"; "descriptor_only_dependencies = true"; "link_libraries = true"
          "value_structs = [\"MeasuredPair\"]"
          "abi_critical_structs = [\"MeasuredPair\", \"struct CriticalFrame\"]"
          "[[namespace]]"; "name = \"Measured.Probe.Boundary\""; "library = \"measured_probe\""
          "description = \"Measured value and ABI-critical struct probe\""
          "functions = [\"measured_identity\", \"measured_pair_sum\", \"measured_make_pair\"]" ]
    let cli = requiredFile "built Farscape CLI" (Path.Combine(repository, "src", "Farscape.Cli", "bin", "Debug", "net10.0", "Farscape.Cli.dll"))
    run work "farscape-project-cli" (ProcessLaunch.startInfo "dotnet" [ cli; "project"; "--project"; pilot ]) |> ignore
    let provenance =
        match Directory.GetFiles(work, "*.link-provenance.toml", SearchOption.AllDirectories) with
        | [| path |] -> path
        | paths -> failwithf "expected one generated link provenance manifest, got %A" paths
    let types = requiredFile "generated Types.clef" (Path.Combine(work, "generated", "Types.clef"))
    let boundary = requiredFile "generated Boundary.clef" (Path.Combine(work, "generated", "Boundary", "Boundary.clef"))
    let typesText = File.ReadAllText types
    Assert.Contains("type MeasuredPair = {", typesText)
    Assert.Contains("module MeasuredPair =\n", typesText)
    Assert.Contains("    [<Literal>]\n    let Size : int = 8\n", typesText)
    Assert.Contains("    [<Literal>]\n    let Alignment : int = 4\n", typesText)
    Assert.Contains("    [<Literal>]\n    let leftOffset : int = 0\n", typesText)
    Assert.Contains("    [<Literal>]\n    let rightOffset : int = 4\n", typesText)
    Assert.Contains("    let Descriptor : Expr<StructDescriptor> =\n        <@\n            { Name = \"MeasuredPair\"", typesText)
    Assert.Contains("module CriticalFrame =\n", typesText)
    Assert.Contains("    [<Literal>]\n    let Size : int = 16\n", typesText)
    Assert.Contains("    [<Literal>]\n    let Alignment : int = 8\n", typesText)
    Assert.Contains("    [<Literal>]\n    let tagOffset : int = 0\n", typesText)
    Assert.Contains("    [<Literal>]\n    let payloadOffset : int = 8\n", typesText)
    Assert.Contains("    let Descriptor : Expr<StructDescriptor> =\n        <@\n            { Name = \"CriticalFrame\"", typesText)
    let loading = Path.Combine(work, "Loading.clef")
    (File.ReadAllText(Path.Combine(repository, "tests", "native-callbacks", "Loading.clef"))).Replace("landingprobe", "measured_probe")
    |> fun text -> File.WriteAllText(loading, text)
    let caller = Path.Combine(work, "Caller.clef")
    writeLines caller
        [ "module Measured.Probe.Caller"
          "open Measured.Probe.Boundary.Types"
          "let identitySample () : int ="
          "    match Measured.Probe.Boundary.measured_identity 42 with"
          "    | 42 -> 42"
          "    | _ -> 0"
          "let layoutSample () : int ="
          "    MeasuredPair.Size + MeasuredPair.rightOffset + CriticalFrame.Size + CriticalFrame.payloadOffset" ]
    let dependencies, platform = platformTables ()
    let metadata = sibling "BAREWIRE_METADATA_PROJECT" "BAREWire/src/BAREWire.BindingMetadata.fidproj"
    let writeFidproj projectPath (sources: string list) =
        writeLines projectPath
            [ "[package]"; "name = \"MeasuredProbe\""; "version = \"1.0.0\""
              "[compilation]"; "target = \"cpu\""
              "[build]"; "output_kind = \"library\""
              "sources = [" + (sources |> List.map quote |> String.concat ", ") + "]"
              "[dependencies]"; yield! dependencies
              "metadata = { path = " + quote metadata + " }"
              "[platform]"; yield! platform
              "loading = \"Landing.Acceptance.Loading.contract\""
              "[link]"; "libraries = [\"measured_probe\"]" ]
    let project = Path.Combine(work, "MeasuredProbe.fidproj")
    writeFidproj project [ loading; types; boundary; caller ]
    let composerDll = sibling "COMPOSER_DLL" "Composer/src/bin/Debug/net10.0/Composer.dll"
    let manifestOutput = Path.Combine(work, "manifest")
    let substantiateLaunch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "substantiate"; "--fidproj"; project; "--pilot"; pilot
              "--link-provenance"; provenance; "--output"; manifestOutput
              "--work-dir"; Path.Combine(work, "substantiation") ]
    substantiateLaunch.Environment["COMPOSER_DLL"] <- composerDll
    let substantiateSummary = run work "farscape-substantiate-cli" substantiateLaunch
    Assert.Contains("Package: MeasuredProbe", substantiateSummary)
    let manifestJson = requiredFile "substantiation JSON manifest" (Path.Combine(manifestOutput, "MeasuredProbe.substantiation.json"))
    let manifest = Substantiation.parseJson (File.ReadAllText manifestJson) |> accepted
    Substantiation.validateManifest manifest |> accepted
    let typesDigest = manifest.Sources |> Array.find (fun entry -> entry.Path = Path.GetFullPath types)
    Assert.Empty typesDigest.RuntimeDeclarations
    Assert.Empty typesDigest.ForeignDeclarations
    let expectedCompileTime =
        [| "Measured.Probe.Boundary.Types.MeasuredPair.Size"
           "Measured.Probe.Boundary.Types.MeasuredPair.Alignment"
           "Measured.Probe.Boundary.Types.MeasuredPair.leftOffset"
           "Measured.Probe.Boundary.Types.MeasuredPair.rightOffset"
           "Measured.Probe.Boundary.Types.MeasuredPair.Descriptor"
           "Measured.Probe.Boundary.Types.CriticalFrame.Size"
           "Measured.Probe.Boundary.Types.CriticalFrame.Alignment"
           "Measured.Probe.Boundary.Types.CriticalFrame.tagOffset"
           "Measured.Probe.Boundary.Types.CriticalFrame.payloadOffset"
           "Measured.Probe.Boundary.Types.CriticalFrame.Descriptor" |]
    Assert.Equal<string[]>(expectedCompileTime, typesDigest.CompileTimeDeclarations)
    let dossierOutput = Path.Combine(work, "dossier")
    run work "farscape-dossier-cli" (ProcessLaunch.startInfo "dotnet" [ cli; "dossier"; "--manifest"; manifestJson; "--output"; dossierOutput ]) |> ignore
    let dossier = SubstantiationDossier.parseDossierJson (File.ReadAllText(Path.Combine(dossierOutput, "MeasuredProbe.dossier.json"))) |> accepted
    Assert.Equal<string[]>([| "CriticalFrame"; "MeasuredPair" |], dossier.Traceability.Layouts |> Array.map (fun layout -> layout.Name) |> Array.sort)
    for expected in expectedCompileTime do
        Assert.Contains(dossier.Traceability.Declarations, fun row -> row.Declaration = expected && row.Kind = "descriptor")
    // Exact-code FS9246 refusal controls: generated wrapper callables, authored parameterized
    // callables, unannotated literals, unquoted descriptors, mutable bindings, and record values.
    let wrapperWork = Path.Combine(work, "wrappers")
    Directory.CreateDirectory wrapperWork |> ignore
    let wrapperPilot = Path.Combine(wrapperWork, "MeasuredWrappers.pilot.toml")
    File.WriteAllText(wrapperPilot, (File.ReadAllText pilot).Replace("directory = \"generated\"", "directory = \"generated-wrappers\""))
    ScratchDirectory.within wrapperWork (fun () -> BindingGenerator.generateFromProjectDiagnosed wrapperPilot false true Types.LP64)
    |> accepted |> ignore
    let generatedApi =
        Directory.GetFiles(Path.Combine(wrapperWork, "generated-wrappers"), "*Api.clef", SearchOption.AllDirectories)
        |> Assert.Single
    let evaluateSources label (sources: string list) =
        let refusalDir = Path.Combine(work, "refusal-" + label)
        Directory.CreateDirectory refusalDir |> ignore
        let refusalProj = Path.Combine(refusalDir, "MeasuredProbe.fidproj")
        writeFidproj refusalProj sources
        Substantiation.evaluate
            { FidprojPath = refusalProj
              PilotPath = Some pilot
              LinkProvenancePath = Some provenance
              OutputDirectory = Some(Path.Combine(refusalDir, "out"))
              WorkDirectory = Some(Path.Combine(refusalDir, "work"))
              AllowWarnings = false
              Verbose = false }
    evaluateSources "generated-wrapper-api" [ loading; types; boundary; caller; generatedApi ] |> refused "FS9246"
    let cliRefusalDir = Path.Combine(work, "cli-fs9246")
    Directory.CreateDirectory cliRefusalDir |> ignore
    let cliRefusalProj = Path.Combine(cliRefusalDir, "MeasuredProbe.fidproj")
    writeFidproj cliRefusalProj [ loading; types; boundary; caller; generatedApi ]
    let cliLaunch =
        ProcessLaunch.startInfo "dotnet"
            [ cli; "substantiate"; "--fidproj"; cliRefusalProj; "--pilot"; pilot
              "--link-provenance"; provenance; "--output"; Path.Combine(cliRefusalDir, "out")
              "--work-dir"; Path.Combine(cliRefusalDir, "work") ]
    cliLaunch.WorkingDirectory <- cliRefusalDir
    cliLaunch.Environment["TMPDIR"] <- cliRefusalDir
    cliLaunch.Environment["COMPOSER_DLL"] <- composerDll
    use cliChild = new Process(StartInfo = cliLaunch)
    Assert.True(cliChild.Start(), "the Farscape CLI did not start")
    let cliStdout = cliChild.StandardOutput.ReadToEndAsync()
    let cliStderr = cliChild.StandardError.ReadToEndAsync()
    Assert.True(cliChild.WaitForExit 120000, "the Farscape CLI did not exit")
    cliChild.WaitForExit()
    Assert.Equal(1, cliChild.ExitCode)
    Assert.Equal("", cliStdout.Result)
    Assert.StartsWith("FS9246: ", cliStderr.Result)
    let writeExtraSource label (lines: string list) =
        let extraPath = Path.Combine(work, label + ".clef")
        writeLines extraPath lines
        extraPath
    let writeModifiedTypes label (transform: string -> string) =
        let modifiedPath = Path.Combine(work, label + ".Types.clef")
        let updated = transform typesText
        Assert.NotEqual<string>(typesText, updated)
        File.WriteAllText(modifiedPath, updated)
        modifiedPath
    evaluateSources "parameterized-callable"
        [ loading; types; boundary
          writeExtraSource "Callable"
              [ "module Measured.Probe.Callable"
                "open Measured.Probe.Boundary.Types"
                "let callPairSum (pair: MeasuredPair) : int = Measured.Probe.Boundary.measured_pair_sum pair" ] ]
    |> refused "FS9246"
    evaluateSources "mutable-binding"
        [ loading; types; boundary
          writeExtraSource "MutableState"
              [ "module Measured.Probe.MutableState"
                "let mutable state : int = 0" ] ]
    |> refused "FS9246"
    evaluateSources "record-runtime-value"
        [ loading; types; boundary
          writeExtraSource "RecordValue"
              [ "module Measured.Probe.RecordValue"
                "open Measured.Probe.Boundary.Types"
                "let zeroPair : MeasuredPair = { left = 0; right = 0 }" ] ]
    |> refused "FS9246"
    evaluateSources "unannotated-literal"
        [ loading
          writeModifiedTypes "unannotated" (fun text -> text.Replace("let Size : int = 8", "let Size = 8"))
          boundary ]
    |> refused "FS9246"
    evaluateSources "unquoted-descriptor"
        [ loading
          writeModifiedTypes "unquoted" (fun text ->
              text.Replace("let Descriptor : Expr<StructDescriptor> =\n        <@\n", "let Descriptor : StructDescriptor =\n").Replace("\n        @>", ""))
          boundary ]
    |> refused "FS9246"
