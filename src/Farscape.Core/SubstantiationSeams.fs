namespace Farscape.Core

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Fidelity.Data.TOML

/// Physical ABI, link, lifecycle and refusal-control seam runner (#219).
/// Consumes a sealed substantiation manifest, revalidates live proof and boundary
/// digests, compiles and executes minimal Clef seam probes via Composer, inspects
/// ELF64 symbol tables and dynamic sections in managed code, executes live negative
/// refusal controls, and emits dossier-compatible JSON receipts plus a seams ledger.
module SubstantiationSeams =

    [<Literal>]
    let LedgerSchema = "farscape-seams-ledger-v1"

    [<Literal>]
    let SeamChecksSchema = "farscape-seam-checks-v1"

    [<Literal>]
    let RefusalControlsSchema = "farscape-refusal-controls-v1"

    [<Literal>]
    let private RefusalControlsCategory = "falsification-refusal-controls"

    type Request =
        { ManifestPath: string
          SpecPath: string
          OutputDirectory: string option
          WorkDirectory: string option
          Verbose: bool }

    [<CLIMutable>]
    type SeamCheckEntry =
        { Id: string
          Category: string
          Summary: string
          BinarySha256: string
          Passed: bool
          Command: string }

    [<CLIMutable>]
    type SeamChecksReceipt =
        { SchemaVersion: int
          Package: string
          ManifestIntegritySha256: string
          TargetTriple: string
          Checks: SeamCheckEntry[] }

    [<CLIMutable>]
    type RefusalControlEntry =
        { Id: string
          ExpectedCode: string
          ObservedCode: string
          MisuseSummary: string
          ProtectionSummary: string
          Passed: bool }

    [<CLIMutable>]
    type RefusalControlsReceipt =
        { SchemaVersion: int
          Package: string
          ManifestIntegritySha256: string
          Controls: RefusalControlEntry[] }

    [<CLIMutable>]
    type DischargeSummary =
        { ReceiptPath: string
          ReceiptSha256: string
          PremisesOutputPath: string
          PremisesOutputSha256: string
          TotalObligations: int
          DischargedUnsatCount: int
          PremisesSatCount: int
          LibraryObligations: int
          LibraryDischargedUnsatCount: int
          LibraryPremisesSatCount: int
          PendingOpenCount: int
          RefutedCount: int }

    [<CLIMutable>]
    type SharedLibraryComparison =
        { CheckId: string
          SharedLibraryPath: string
          SharedLibrarySha256: string
          DynamicProbeBinaryPath: string
          DynamicProbeBinarySha256: string }

    [<CLIMutable>]
    type RetainedProbeBinary =
        { CheckId: string
          BinaryPath: string
          BinarySha256: string }

    [<CLIMutable>]
    type SeamsLedgerReceipt =
        { Schema: string
          SchemaVersion: int
          SeamChecksSchema: string
          RefusalControlsSchema: string
          Package: string
          ManifestIntegritySha256: string
          TargetTriple: string
          RequiredSeamCategories: string[]
          VerifiedPhysicalCategories: string[]
          Discharge: DischargeSummary
          RetainedBinaries: RetainedProbeBinary[]
          SharedLibraries: SharedLibraryComparison[]
          SeamChecksPath: string
          SeamChecksSha256: string
          RefusalControlsPath: string
          RefusalControlsSha256: string }

    type ElfExecutableInspection =
        { Path: string
          Sha256: string
          ElfType: uint16
          Machine: uint16
          HasSymtab: bool
          DefinedSymtab: Set<string>
          UndefinedSymtab: Set<string>
          DefinedDynsym: Set<string>
          UndefinedDynsym: Set<string>
          NeededLibraries: string[] }

    type Generated =
        { Manifest: Substantiation.Manifest
          Discharge: DischargeSummary
          SeamChecks: SeamChecksReceipt
          RefusalControls: RefusalControlsReceipt
          Ledger: SeamsLedgerReceipt
          SeamChecksPath: string
          RefusalControlsPath: string
          LedgerPath: string }

    // =========================================================================
    // Diagnostics and path policy
    // =========================================================================

    let private fail (path: string) (message: string) : 'T =
        Diagnostics.diagnostic Diagnostics.SeamSuiteFailure message (Diagnostics.inFile path "seam suite input or evidence")
        |> Diagnostics.refuse

    let private protect (path: string) (action: unit -> 'T) : Result<'T, Diagnostics.Refusal> =
        try
            Ok(action ())
        with
        | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
        | error ->
            Diagnostics.diagnostic Diagnostics.SeamSuiteFailure error.Message (Diagnostics.inFile path "seam suite input or evidence")
            |> Diagnostics.refusalOf
            |> Error

    let private evidencePath (path: string) : string =
        try
            ScratchDirectory.canonicalEvidencePath path
        with :? Diagnostics.RefusalException as refused ->
            let detail = refused.Refusal.Diagnostics |> List.map _.Message |> String.concat " "
            fail path detail

    let private recodeToSeamFailure (defaultDiagnosticPath: string) (refusal: Diagnostics.Refusal) : 'T =
        match refusal.Diagnostics with
        | [] -> fail defaultDiagnosticPath refusal.Text
        | diagnostics ->
            diagnostics
            |> List.map (fun diagnostic ->
                { diagnostic with
                    Diagnostics.Diagnostic.Code = Diagnostics.SeamSuiteFailure
                    Diagnostics.Diagnostic.Message = diagnostic.Message + " (origin " + diagnostic.Code + ")" }
                |> Diagnostics.refusalOf)
            |> Diagnostics.joined "\n"
            |> Diagnostics.raiseRefusal

    let private shaText (text: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> _.ToLowerInvariant()

    let private isSha256 (value: string) : bool =
        not (isNull value) && Regex.IsMatch(value, @"\A[a-fA-F0-9]{64}\z")

    let private receiptFail code path message : 'T =
        Diagnostics.diagnostic code message (Diagnostics.inFile path "dossier input") |> Diagnostics.refuse

    let private receiptEvidencePath path =
        try ScratchDirectory.canonicalEvidencePath path
        with :? Diagnostics.RefusalException as refused ->
            receiptFail Diagnostics.DossierOutputFailure path
                (refused.Refusal.Diagnostics |> List.map (fun diagnostic -> diagnostic.Message) |> String.concat " ")

    let private parseStrict<'T> label path : 'T =
        if not (File.Exists path) then receiptFail Diagnostics.DossierInputMalformed path (label + " does not exist.")
        try
            let text = File.ReadAllText(path, UTF8Encoding(false, true))
            use document = JsonDocument.Parse text
            Substantiation.requireJsonShape typeof<'T> document.RootElement
            JsonSerializer.Deserialize<'T> text
        with
        | :? Diagnostics.RefusalException -> reraise ()
        | error -> receiptFail Diagnostics.DossierInputMalformed path (label + " violates its required JSON schema: " + error.Message)

    let private requireSeamChecks (manifest: Substantiation.Manifest) (path: string) (receipt: SeamChecksReceipt) =
        let invalid message = receiptFail Diagnostics.DossierCheckReceiptInvalid path message
        if receipt.SchemaVersion <> 1 then invalid "Unsupported seam checks receipt schema version."
        if receipt.Package <> manifest.Package then invalid ("The seam checks receipt names package '" + receipt.Package + "', not '" + manifest.Package + "'.")
        if not (isSha256 receipt.ManifestIntegritySha256) || not (String.Equals(receipt.ManifestIntegritySha256, manifest.IntegritySha256, StringComparison.OrdinalIgnoreCase)) then
            receiptFail Diagnostics.DossierDigestMismatch path "The seam checks receipt records the integrity digest of a different substantiation manifest."
        if String.IsNullOrWhiteSpace receipt.TargetTriple || receipt.TargetTriple = "not-measured" then
            invalid "The seam checks receipt names no measured target triple."
        if String.IsNullOrWhiteSpace manifest.Boundary.ClangTarget || manifest.Boundary.ClangTarget = "not-measured" then
            invalid "Physical seam checks cannot substantiate an unmeasured manifest (ClangTarget = 'not-measured')."
        if receipt.TargetTriple <> manifest.Boundary.ClangTarget then
            invalid ("The seam checks receipt names target triple '" + receipt.TargetTriple + "', which disagrees with manifest ClangTarget '" + manifest.Boundary.ClangTarget + "'.")
        if isNull receipt.Checks || receipt.Checks.Length = 0 then invalid "The seam checks receipt must contain checks."
        let identities = HashSet<string>(StringComparer.Ordinal)
        for check in receipt.Checks do
            if String.IsNullOrWhiteSpace check.Id then invalid "A seam check has an empty Id."
            if not (identities.Add check.Id) then invalid ("Seam check Id '" + check.Id + "' occurs more than once.")
            if check.Category = RefusalControlsCategory || not (Array.contains check.Category manifest.Boundary.RequiredSeamChecks) then
                invalid ("Seam check '" + check.Id + "' names category '" + check.Category + "', which is no physical seam category this library requires.")
            if not (isSha256 check.BinarySha256) then invalid ("Seam check '" + check.Id + "' has no 64-digit hexadecimal BinarySha256.")
            if String.IsNullOrWhiteSpace check.Summary || String.IsNullOrWhiteSpace check.Command then
                invalid ("Seam check '" + check.Id + "' requires a nonempty Summary and Command.")
            if not check.Passed then invalid ("Seam check '" + check.Id + "' records Passed = false.")

    let private requireRefusalControls (manifest: Substantiation.Manifest) (path: string) (receipt: RefusalControlsReceipt) =
        let invalid message = receiptFail Diagnostics.DossierCheckReceiptInvalid path message
        if receipt.SchemaVersion <> 1 then invalid "Unsupported refusal controls receipt schema version."
        if receipt.Package <> manifest.Package then invalid ("The refusal controls receipt names package '" + receipt.Package + "', not '" + manifest.Package + "'.")
        if not (isSha256 receipt.ManifestIntegritySha256) || not (String.Equals(receipt.ManifestIntegritySha256, manifest.IntegritySha256, StringComparison.OrdinalIgnoreCase)) then
            receiptFail Diagnostics.DossierDigestMismatch path "The refusal controls receipt records the integrity digest of a different substantiation manifest."
        if isNull receipt.Controls || receipt.Controls.Length = 0 then invalid "The refusal controls receipt must contain controls."
        let identities = HashSet<string>(StringComparer.Ordinal)
        for control in receipt.Controls do
            if String.IsNullOrWhiteSpace control.Id then invalid "A refusal control has an empty Id."
            if not (identities.Add control.Id) then invalid ("Refusal control Id '" + control.Id + "' occurs more than once.")
            if not (Regex.IsMatch(control.ExpectedCode, @"\A[A-Z]+[0-9]+\z", RegexOptions.CultureInvariant)) then
                invalid ("Refusal control '" + control.Id + "' has no valid ExpectedCode.")
            if control.ExpectedCode <> control.ObservedCode then
                invalid ("Refusal control '" + control.Id + "' expected " + control.ExpectedCode + " and observed " + control.ObservedCode + ".")
            if String.IsNullOrWhiteSpace control.MisuseSummary || String.IsNullOrWhiteSpace control.ProtectionSummary then
                invalid ("Refusal control '" + control.Id + "' requires a nonempty MisuseSummary and ProtectionSummary.")
            if not control.Passed then invalid ("Refusal control '" + control.Id + "' records Passed = false.")

    /// Parse and validate a physical seam checks receipt against the given manifest (#209, #219).
    let readSeamReceipt (manifest: Substantiation.Manifest) (path: string) : Result<SeamChecksReceipt, Diagnostics.Refusal> =
        try
            let resolved = receiptEvidencePath path
            let receipt = parseStrict<SeamChecksReceipt> "The seam checks receipt" resolved
            requireSeamChecks manifest resolved receipt
            Ok receipt
        with
        | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
        | error ->
            Diagnostics.diagnostic Diagnostics.DossierInputMalformed error.Message
                (Diagnostics.inFile path "seam checks receipt")
            |> Diagnostics.refusalOf |> Error

    /// Parse and validate a compile-time refusal controls receipt against the given manifest (#209, #219).
    let readRefusalReceipt (manifest: Substantiation.Manifest) (path: string) : Result<RefusalControlsReceipt, Diagnostics.Refusal> =
        try
            let resolved = receiptEvidencePath path
            let receipt = parseStrict<RefusalControlsReceipt> "The refusal controls receipt" resolved
            requireRefusalControls manifest resolved receipt
            Ok receipt
        with
        | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
        | error ->
            Diagnostics.diagnostic Diagnostics.DossierInputMalformed error.Message
                (Diagnostics.inFile path "refusal controls receipt")
            |> Diagnostics.refusalOf |> Error

    let private readUtf8 (label: string) (path: string) : string =
        if not (File.Exists path) then
            fail path (label + " does not exist.")
        try
            let text = File.ReadAllText(path, UTF8Encoding(false, true))
            if text.StartsWith("\uFEFF", StringComparison.Ordinal) then text.Substring 1 else text
        with error ->
            fail path (label + " cannot be read as UTF-8: " + error.Message)

    let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

    let private serializeVerified<'T> (path: string) (value: 'T) : string =
        let json = JsonSerializer.Serialize(value, jsonOptions)
        use document = JsonDocument.Parse json
        Substantiation.requireJsonShape typeof<'T> document.RootElement
        if String.IsNullOrWhiteSpace json then fail path "Serialized JSON receipt is empty."
        json

    let private writeAtomic (destination: string) (contents: string) : unit =
        let directory = Path.GetDirectoryName destination
        Directory.CreateDirectory directory |> ignore
        let staging = Path.Combine(directory, "." + Path.GetFileName destination + "." + Guid.NewGuid().ToString("N") + ".tmp")
        File.WriteAllText(staging, contents, UTF8Encoding(false))
        File.Move(staging, destination, true)

    // =========================================================================
    // ELF64 binary inspection (System.Buffers.Binary.BinaryPrimitives)
    // =========================================================================

    type private ElfSection =
        { Type: uint32
          Offset: int
          Size: int
          Link: int
          EntSize: int }

    let private readU16 (bytes: byte[]) (offset: int) : uint16 =
        BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(bytes, offset, 2))

    let private readU32 (bytes: byte[]) (offset: int) : uint32 =
        BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(bytes, offset, 4))

    let private readU64 (bytes: byte[]) (offset: int) : uint64 =
        BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(bytes, offset, 8))

    let private readI64 (bytes: byte[]) (offset: int) : int64 =
        BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan<byte>(bytes, offset, 8))

    /// Inspect an ELF64 little-endian x86_64 executable (`ET_EXEC`) or shared/PIE object (`ET_DYN`),
    /// extracting `.symtab`, `.dynsym`, and `.dynamic` (`DT_NEEDED`) in managed code.
    let inspectElfExecutable (path: string) : Result<ElfExecutableInspection, Diagnostics.Refusal> =
        protect path (fun () ->
            let resolved = evidencePath path
            if not (File.Exists resolved) then
                fail resolved "ELF binary does not exist."
            let bytes =
                try File.ReadAllBytes resolved
                with error -> fail resolved ("ELF binary cannot be read: " + error.Message)
            if bytes.Length < 64 then
                fail resolved "File is shorter than a 64-byte ELF64 header."
            if bytes[0] <> 0x7Fuy || bytes[1] <> 0x45uy || bytes[2] <> 0x4Cuy || bytes[3] <> 0x46uy then
                fail resolved "File lacks the ELF magic header (0x7F 'E' 'L' 'F')."
            if bytes[4] <> 2uy then
                fail resolved $"Unsupported ELF class {bytes[4]}; expected ELFCLASS64 (2)."
            if bytes[5] <> 1uy then
                fail resolved $"Unsupported ELF data encoding {bytes[5]}; expected ELFDATA2LSB (1)."
            let elfType = readU16 bytes 16
            if elfType <> 2us && elfType <> 3us then
                fail resolved $"Unsupported ELF object type {elfType}; expected ET_EXEC (2) or ET_DYN (3)."
            let machine = readU16 bytes 18
            if machine <> 62us then
                fail resolved $"Unsupported ELF machine {machine}; expected EM_X86_64 (62)."
            let shoff = readU64 bytes 40
            let shentsize = readU16 bytes 58
            let shnum = readU16 bytes 60
            if shnum = 0us || shentsize < 64us then
                fail resolved "ELF64 binary lacks a valid section header table."
            let tableEnd = shoff + uint64 shnum * uint64 shentsize
            if shoff > uint64 Int32.MaxValue || tableEnd > uint64 bytes.Length || tableEnd < shoff then
                fail resolved "ELF64 section header table lies outside the file bounds."

            let sections =
                Array.init (int shnum) (fun index ->
                    let baseOffset = int shoff + index * int shentsize
                    let shType = readU32 bytes (baseOffset + 4)
                    let secOffset = readU64 bytes (baseOffset + 24)
                    let secSize = readU64 bytes (baseOffset + 32)
                    let shLink = readU32 bytes (baseOffset + 40)
                    let secEntSize = readU64 bytes (baseOffset + 56)
                    // SHT_NOBITS = 8u occupies no bytes in the file image.
                    if shType <> 8u then
                        let secEnd = secOffset + secSize
                        if secOffset > uint64 Int32.MaxValue || secSize > uint64 Int32.MaxValue || secEnd > uint64 bytes.Length || secEnd < secOffset then
                            fail resolved $"ELF64 section {index} exceeds file bounds."
                    if shLink >= uint32 shnum then
                        fail resolved $"ELF64 section {index} has out-of-range sh_link {shLink}."
                    if secEntSize > uint64 Int32.MaxValue then
                        fail resolved $"ELF64 section {index} has out-of-range sh_entsize {secEntSize}."
                    { Type = shType
                      Offset = int secOffset
                      Size = int secSize
                      Link = int shLink
                      EntSize = int secEntSize })

            let readCString (strSectionIndex: int) (nameOffset: uint32) : string =
                let strSec = sections[strSectionIndex]
                // SHT_STRTAB = 3u
                if strSec.Type <> 3u then
                    fail resolved $"ELF64 linked section {strSectionIndex} is not SHT_STRTAB (3)."
                if uint64 nameOffset >= uint64 strSec.Size then
                    fail resolved $"ELF64 string offset {nameOffset} exceeds string table section {strSectionIndex} size {strSec.Size}."
                let startPos = strSec.Offset + int nameOffset
                let limitPos = strSec.Offset + strSec.Size
                let mutable cursor = startPos
                while cursor < limitPos && bytes[cursor] <> 0uy do
                    cursor <- cursor + 1
                if cursor >= limitPos then
                    fail resolved $"ELF64 string table section {strSectionIndex} is missing a NUL terminator."
                UTF8Encoding(false, true).GetString(bytes, startPos, cursor - startPos)

            let normalizeSymbol (raw: string) : string =
                match raw.IndexOf('@') with
                | -1 -> raw
                | atIndex -> raw.Substring(0, atIndex)

            let readSymbolTable (section: ElfSection) : Set<string> * Set<string> =
                if section.Size = 0 then
                    Set.empty, Set.empty
                else
                    if section.EntSize <> 24 || section.Size % 24 <> 0 then
                        fail resolved $"ELF64 symbol table has invalid entry size {section.EntSize} for section size {section.Size}; expected sh_entsize = 24."
                    let defined = HashSet<string>(StringComparer.Ordinal)
                    let undefined = HashSet<string>(StringComparer.Ordinal)
                    let count = section.Size / 24
                    // Entry 0 is STN_UNDEF in every ELF symbol table.
                    for i in 1 .. count - 1 do
                        let entryOffset = section.Offset + i * 24
                        let stName = readU32 bytes entryOffset
                        let stShndx = readU16 bytes (entryOffset + 6)
                        if stName <> 0u then
                            let rawName = readCString section.Link stName
                            let name = normalizeSymbol rawName
                            if not (String.IsNullOrEmpty name) then
                                if stShndx = 0us then undefined.Add name |> ignore
                                else defined.Add name |> ignore
                    Set.ofSeq defined, Set.ofSeq undefined

            let mutable definedSymtab = Set.empty
            let mutable undefinedSymtab = Set.empty
            let mutable definedDynsym = Set.empty
            let mutable undefinedDynsym = Set.empty
            let needed = ResizeArray<string>()
            let mutable hasSymtab = false

            for section in sections do
                match section.Type with
                | 2u ->
                    // SHT_SYMTAB
                    hasSymtab <- true
                    let defSet, undefSet = readSymbolTable section
                    definedSymtab <- Set.union definedSymtab defSet
                    undefinedSymtab <- Set.union undefinedSymtab undefSet
                | 11u ->
                    // SHT_DYNSYM
                    let defSet, undefSet = readSymbolTable section
                    definedDynsym <- Set.union definedDynsym defSet
                    undefinedDynsym <- Set.union undefinedDynsym undefSet
                | 6u when section.Size > 0 ->
                    // SHT_DYNAMIC: Elf64_Dyn is strictly 16 bytes.
                    if section.EntSize <> 16 || section.Size % 16 <> 0 then
                        fail resolved $"ELF64 dynamic section has invalid entry size {section.EntSize} for section size {section.Size}; expected sh_entsize = 16."
                    let count = section.Size / 16
                    let mutable active = true
                    let mutable index = 0
                    while active && index < count do
                        let entryOffset = section.Offset + index * 16
                        let dTag = readI64 bytes entryOffset
                        let dVal = readU64 bytes (entryOffset + 8)
                        if dTag = 0L then
                            active <- false
                        elif dTag = 1L then
                            // DT_NEEDED = 1
                            if dVal > uint64 UInt32.MaxValue then
                                fail resolved $"ELF64 DT_NEEDED string offset {dVal} overflows uint32."
                            let libraryName = readCString section.Link (uint32 dVal)
                            if String.IsNullOrWhiteSpace libraryName then
                                fail resolved "ELF64 DT_NEEDED entry names an empty library."
                            if not (needed.Contains libraryName) then
                                needed.Add libraryName
                        index <- index + 1
                | _ -> ()

            let digest = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
            { Path = resolved
              Sha256 = digest
              ElfType = elfType
              Machine = machine
              HasSymtab = hasSymtab
              DefinedSymtab = definedSymtab
              UndefinedSymtab = undefinedSymtab
              DefinedDynsym = definedDynsym
              UndefinedDynsym = undefinedDynsym
              NeededLibraries = needed.ToArray() })

    // =========================================================================
    // Manifest and live proof discharge verification
    // =========================================================================

    let private verifyDischargeCore (manifest: Substantiation.Manifest) : DischargeSummary =
        let receiptPath = evidencePath (Path.Combine(manifest.ArtifactsDirectory, "06c_psg_discharge.json"))
        let premisesPath = evidencePath (Path.Combine(manifest.ArtifactsDirectory, "06b_premises_sat.stdout"))
        if not (File.Exists receiptPath) then
            fail receiptPath "Retained discharge receipt 06c_psg_discharge.json does not exist."
        if not (File.Exists premisesPath) then
            fail premisesPath "Retained SAT(H) output 06b_premises_sat.stdout does not exist."
        let receiptSha = ClangToolchain.sha256 receiptPath
        if not (String.Equals(receiptSha, manifest.Proof.ReceiptSha256, StringComparison.OrdinalIgnoreCase)) then
            fail receiptPath "06c_psg_discharge.json SHA-256 differs from manifest.Proof.ReceiptSha256."
        let premisesSha = ClangToolchain.sha256 premisesPath
        if not (String.Equals(premisesSha, manifest.Proof.PremisesOutputSha256, StringComparison.OrdinalIgnoreCase)) then
            fail premisesPath "06b_premises_sat.stdout SHA-256 differs from manifest.Proof.PremisesOutputSha256."

        let receiptText = readUtf8 "Retained discharge receipt" receiptPath
        let unsatCount, refutedCount, pendingCount =
            try
                use doc = JsonDocument.Parse receiptText
                let root = doc.RootElement
                let mutable stageProp = Unchecked.defaultof<JsonElement>
                if not (root.TryGetProperty("Stage", &stageProp)) || stageProp.ValueKind <> JsonValueKind.String || stageProp.GetString() <> "psg" then
                    fail receiptPath "06c_psg_discharge.json does not declare Stage = 'psg'."
                let mutable outcomesProp = Unchecked.defaultof<JsonElement>
                if not (root.TryGetProperty("Outcomes", &outcomesProp)) || outcomesProp.ValueKind <> JsonValueKind.Array then
                    fail receiptPath "06c_psg_discharge.json lacks an Outcomes array."
                let mutable unsat = 0
                let mutable sat = 0
                let mutable other = 0
                for item in outcomesProp.EnumerateArray() do
                    let mutable verdictProp = Unchecked.defaultof<JsonElement>
                    if item.ValueKind <> JsonValueKind.Object || not (item.TryGetProperty("Verdict", &verdictProp)) || verdictProp.ValueKind <> JsonValueKind.String then
                        fail receiptPath "An outcome in 06c_psg_discharge.json lacks a string Verdict."
                    match verdictProp.GetString() with
                    | "unsat" -> unsat <- unsat + 1
                    | "sat" -> sat <- sat + 1
                    | _ -> other <- other + 1
                unsat, sat, other
            with
            | :? Diagnostics.RefusalException -> reraise ()
            | error -> fail receiptPath ("06c_psg_discharge.json is malformed: " + error.Message)

        let premisesLines =
            (readUtf8 "Retained SAT(H) output" premisesPath).Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun line -> line.Length > 0)
        if premisesLines |> Array.exists ((<>) "sat") then
            fail premisesPath "06b_premises_sat.stdout contains a non-sat verdict."
        let premisesSatCount = premisesLines.Length

        if unsatCount <= 0 || premisesSatCount <= 0 then
            fail receiptPath "Retained proof discharge must establish positive SAT(H) and UNSAT(H and not C) counts."
        if pendingCount <> 0 || refutedCount <> 0 || manifest.Proof.PendingOpenCount <> 0 || manifest.Proof.RefutedCount <> 0 then
            fail receiptPath $"Retained proof discharge has open ({pendingCount}) or refuted ({refutedCount}) obligations."
        if unsatCount <> manifest.Proof.DischargedUnsatCount || unsatCount <> manifest.Proof.TotalObligations then
            fail receiptPath $"06c_psg_discharge.json UNSAT count ({unsatCount}) disagrees with manifest.Proof ({manifest.Proof.DischargedUnsatCount})."
        if premisesSatCount <> manifest.Proof.PremisesSatCount || premisesSatCount <> manifest.Proof.TotalObligations then
            fail premisesPath $"06b_premises_sat.stdout SAT count ({premisesSatCount}) disagrees with manifest.Proof ({manifest.Proof.PremisesSatCount})."
        if manifest.Proof.LibraryObligations < 0
           || manifest.Proof.LibraryObligations > manifest.Proof.TotalObligations
           || manifest.Proof.LibraryDischargedUnsatCount <> manifest.Proof.LibraryObligations
           || manifest.Proof.LibraryPremisesSatCount <> manifest.Proof.LibraryObligations then
            fail receiptPath "manifest.Proof library obligation counts are inconsistent with full UNSAT/SAT discharge."

        { ReceiptPath = receiptPath
          ReceiptSha256 = receiptSha
          PremisesOutputPath = premisesPath
          PremisesOutputSha256 = premisesSha
          TotalObligations = manifest.Proof.TotalObligations
          DischargedUnsatCount = unsatCount
          PremisesSatCount = premisesSatCount
          LibraryObligations = manifest.Proof.LibraryObligations
          LibraryDischargedUnsatCount = manifest.Proof.LibraryDischargedUnsatCount
          LibraryPremisesSatCount = manifest.Proof.LibraryPremisesSatCount
          PendingOpenCount = pendingCount
          RefutedCount = refutedCount }

    /// Revalidate the retained SAT(H) and UNSAT(H and not C) proof discharge receipts
    /// against `manifest.Proof`, refusing any drift, non-psg stage, open/refuted verdict,
    /// or non-positive count with `FS9253` (#219).
    let verifyDischarge (manifest: Substantiation.Manifest) : Result<DischargeSummary, Diagnostics.Refusal> =
        protect manifest.FidprojPath (fun () -> verifyDischargeCore manifest)

    let private loadAndValidateManifest (manifestPath: string) : Substantiation.Manifest * DischargeSummary =
        let resolved = evidencePath manifestPath
        let text = readUtf8 "The substantiation manifest" resolved
        let parsed =
            match Path.GetExtension(resolved).ToLowerInvariant() with
            | ".json" -> Substantiation.parseJson text
            | ".toml" -> Substantiation.parseToml text
            | _ -> fail resolved "The substantiation manifest must have a .json or .toml extension."
        let manifest =
            match parsed with
            | Ok value -> value
            | Error refusal -> recodeToSeamFailure resolved refusal
        let discharge = verifyDischargeCore manifest
        match Substantiation.validateManifest manifest with
        | Ok () -> manifest, discharge
        | Error refusal -> recodeToSeamFailure resolved refusal

    // =========================================================================
    // Seam suite TOML specification (.seams.toml)
    // =========================================================================

    type internal ElfCheckSpec =
        { ElidedSymbols: string[]
          RetainedStaticSymbols: string[]
          ForbiddenNeededLibraries: string[]
          SharedLibrary: string option
          SharedLibrarySha256: string option
          DynamicProbeSource: string option
          RetainedDynamicImports: string[] }

    type internal PhysicalCheckSpec =
        { Id: string
          Category: string
          Summary: string
          EntrySource: string
          ExtraSources: string[]
          IncludeRuntimeSources: bool
          RequiredSymbols: string[]
          LoadingContract: string option
          ExpectedExitCode: int
          ExpectedStdout: string
          Elf: ElfCheckSpec option }

    [<RequireQualifiedAccess>]
    type private RefusalControlKind =
        | Pilot
        | Substantiation
        | Composer

    type private RefusalControlSpec =
        { Id: string
          Kind: RefusalControlKind
          TargetPath: string
          PilotPath: string option
          ExpectedCode: string
          MisuseSummary: string
          ProtectionSummary: string }

    type private SeamSuiteSpec =
        { TargetTriple: string
          DefaultLoadingContract: string option
          Checks: PhysicalCheckSpec[]
          RefusalControls: RefusalControlSpec[] }

    let private requireAllowedKeys (path: string) (context: string) (allowed: Set<string>) (table: TomlTable) : unit =
        for key, _ in Map.toList table do
            if not (allowed.Contains key) then
                fail path $"{context} contains unknown key '{key}'."

    let private requireStringField (path: string) (context: string) (key: string) (table: TomlTable) : string =
        match Map.tryFind key table with
        | Some(TomlValue.String value) when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> fail path $"{context}.{key} must be a nonempty string."

    let private optionalStringField (path: string) (context: string) (key: string) (table: TomlTable) : string option =
        match Map.tryFind key table with
        | None -> None
        | Some(TomlValue.String value) when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> fail path $"{context}.{key} must be a nonempty string when present."

    let private optionalBoolField (path: string) (context: string) (key: string) (defaultValue: bool) (table: TomlTable) : bool =
        match Map.tryFind key table with
        | None -> defaultValue
        | Some(TomlValue.Boolean value) -> value
        | _ -> fail path $"{context}.{key} must be a boolean."

    let private optionalIntField (path: string) (context: string) (key: string) (defaultValue: int) (table: TomlTable) : int =
        match Map.tryFind key table with
        | None -> defaultValue
        | Some(TomlValue.Integer value) when value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue -> int value
        | _ -> fail path $"{context}.{key} must be a 32-bit integer."

    let private optionalStringArrayField (path: string) (context: string) (key: string) (table: TomlTable) : string[] =
        match Map.tryFind key table with
        | None -> [||]
        | Some(TomlValue.Array items) ->
            let values =
                items
                |> List.map (function
                    | TomlValue.String value when not (String.IsNullOrWhiteSpace value) -> value
                    | _ -> fail path $"{context}.{key} must contain only nonempty strings.")
                |> List.toArray
            if (Array.distinct values).Length <> values.Length then
                fail path $"{context}.{key} contains duplicate entries."
            values
        | _ -> fail path $"{context}.{key} must be an array of nonempty strings."

    let private resolveSpecFile (specPath: string) (specDirectory: string) (context: string) (rawPath: string) : string =
        let combined = if Path.IsPathRooted rawPath then rawPath else Path.Combine(specDirectory, rawPath)
        let resolved = evidencePath combined
        if not (File.Exists resolved) then
            fail specPath $"{context} file '{resolved}' does not exist."
        resolved

    let private loadSpec (manifest: Substantiation.Manifest) (specPath: string) : SeamSuiteSpec =
        let resolved = evidencePath specPath
        let text = readUtf8 "The seam suite specification" resolved
        let document =
            match Toml.parse text with
            | Ok parsed -> parsed
            | Error reason -> fail resolved ("The seam suite specification is not valid TOML: " + reason)
        let specDir = Path.GetDirectoryName resolved
        requireAllowedKeys resolved "Seam suite specification"
            (set [ "schema_version"; "target_triple"; "loading_contract"; "checks"; "refusal_controls" ])
            document
        match Map.tryFind "schema_version" document with
        | Some(TomlValue.Integer 1L) -> ()
        | _ -> fail resolved "Seam suite specification requires schema_version = 1."
        let targetTriple = requireStringField resolved "Seam suite specification" "target_triple" document
        if String.IsNullOrWhiteSpace manifest.Boundary.ClangTarget || manifest.Boundary.ClangTarget = "not-measured" then
            fail resolved $"Physical seam suites require a Clang-measured target triple, but manifest '{manifest.Package}' records ClangTarget = '{manifest.Boundary.ClangTarget}'."
        if manifest.Boundary.ClangTarget <> targetTriple then
            fail resolved $"Seam suite target_triple '{targetTriple}' disagrees with manifest ClangTarget '{manifest.Boundary.ClangTarget}'."
        let defaultLoading = optionalStringField resolved "Seam suite specification" "loading_contract" document

        let parseElf (checkId: string) (category: string) (table: TomlTable) : ElfCheckSpec =
            let ctx = $"checks[{checkId}].elf"
            requireAllowedKeys resolved ctx
                (set [ "elided_symbols"; "retained_static_symbols"; "forbidden_needed_libraries"
                       "shared_library"; "shared_library_sha256"; "dynamic_probe_source"; "retained_dynamic_imports" ])
                table
            let elided = optionalStringArrayField resolved ctx "elided_symbols" table
            let retainedStatic = optionalStringArrayField resolved ctx "retained_static_symbols" table
            let forbiddenNeeded = optionalStringArrayField resolved ctx "forbidden_needed_libraries" table
            let sharedLib =
                optionalStringField resolved ctx "shared_library" table
                |> Option.map (resolveSpecFile resolved specDir (ctx + ".shared_library"))
            let sharedShaRaw = optionalStringField resolved ctx "shared_library_sha256" table
            let dynSource =
                optionalStringField resolved ctx "dynamic_probe_source" table
                |> Option.map (resolveSpecFile resolved specDir (ctx + ".dynamic_probe_source"))
            let retainedDyn = optionalStringArrayField resolved ctx "retained_dynamic_imports" table
            if (category = "static-dynamic-link-elision" || elided.Length > 0 || retainedStatic.Length > 0)
               && manifest.Boundary.LinkMode <> "static-archive" then
                fail resolved $"{ctx} static archive symbol elision and retention require manifest LinkMode = 'static-archive', not '{manifest.Boundary.LinkMode}'."
            if category = "static-dynamic-link-elision" && (elided.Length = 0 || retainedStatic.Length = 0) then
                fail resolved $"{ctx} for 'static-dynamic-link-elision' requires nonempty elided_symbols and retained_static_symbols."
            let sharedSha =
                match sharedLib, sharedShaRaw with
                | Some _, Some raw when isSha256 raw ->
                    if dynSource.IsNone || retainedDyn.Length = 0 then
                        fail resolved $"{ctx} with shared_library requires dynamic_probe_source and nonempty retained_dynamic_imports."
                    Some(raw.ToLowerInvariant())
                | Some _, Some _ ->
                    fail resolved $"{ctx}.shared_library_sha256 must be a 64-character hexadecimal SHA-256 digest."
                | Some _, None ->
                    fail resolved $"{ctx} with shared_library requires shared_library_sha256."
                | None, None ->
                    if dynSource.IsSome || retainedDyn.Length > 0 then
                        fail resolved $"{ctx} without shared_library cannot specify dynamic_probe_source or retained_dynamic_imports."
                    None
                | None, Some _ ->
                    fail resolved $"{ctx} without shared_library cannot specify shared_library_sha256."
            { ElidedSymbols = elided
              RetainedStaticSymbols = retainedStatic
              ForbiddenNeededLibraries = forbiddenNeeded
              SharedLibrary = sharedLib
              SharedLibrarySha256 = sharedSha
              DynamicProbeSource = dynSource
              RetainedDynamicImports = retainedDyn }

        let checkTables =
            match Map.tryFind "checks" document with
            | None -> [||]
            | Some(TomlValue.Array items) ->
                items
                |> List.mapi (fun index item ->
                    match item with
                    | TomlValue.Table tbl | TomlValue.InlineTable tbl -> index, tbl
                    | _ -> fail resolved $"checks[{index}] must be a TOML table.")
                |> List.toArray
            | _ -> fail resolved "checks must be an array of TOML tables."

        let checkIds = HashSet<string>(StringComparer.Ordinal)
        let checks =
            checkTables
            |> Array.map (fun (index, tbl) ->
                let prelimCtx = $"checks[{index}]"
                requireAllowedKeys resolved prelimCtx
                    (set [ "id"; "category"; "summary"; "entry_source"; "extra_sources"
                           "include_runtime_sources"; "required_symbols"
                           "loading_contract"; "expected_exit_code"; "expected_stdout"; "elf" ])
                    tbl
                let id = requireStringField resolved prelimCtx "id" tbl
                if not (checkIds.Add id) then
                    fail resolved $"Duplicate seam check id '{id}'."
                let ctx = $"checks[{id}]"
                let category = requireStringField resolved ctx "category" tbl
                if category = RefusalControlsCategory || not (Array.contains category manifest.Boundary.RequiredSeamChecks) then
                    fail resolved $"Seam check '{id}' names category '{category}', which is not a required physical seam category of package '{manifest.Package}'."
                if category = "static-dynamic-link-elision" && manifest.Boundary.LinkMode <> "static-archive" then
                    fail resolved $"Seam check '{id}' category 'static-dynamic-link-elision' requires manifest LinkMode = 'static-archive', not '{manifest.Boundary.LinkMode}'."
                let summary = requireStringField resolved ctx "summary" tbl
                let entrySource = resolveSpecFile resolved specDir (ctx + ".entry_source") (requireStringField resolved ctx "entry_source" tbl)
                let extraSources =
                    optionalStringArrayField resolved ctx "extra_sources" tbl
                    |> Array.map (resolveSpecFile resolved specDir (ctx + ".extra_sources"))
                if (Array.distinct extraSources).Length <> extraSources.Length then
                    fail resolved $"{ctx}.extra_sources resolves to duplicate file paths."
                let includeRuntime = optionalBoolField resolved ctx "include_runtime_sources" false tbl
                let requiredSymbols = optionalStringArrayField resolved ctx "required_symbols" tbl
                if (Array.distinct requiredSymbols).Length <> requiredSymbols.Length then
                    fail resolved $"{ctx}.required_symbols contains duplicate symbol names."
                for reqSym in requiredSymbols do
                    if not (Array.contains reqSym manifest.Boundary.ExternSymbols) then
                        fail resolved $"{ctx}.required_symbols names '{reqSym}', which is absent from manifest.Boundary.ExternSymbols."
                let loading =
                    match optionalStringField resolved ctx "loading_contract" tbl with
                    | Some explicit -> Some explicit
                    | None -> defaultLoading
                let expectedExit = optionalIntField resolved ctx "expected_exit_code" 0 tbl
                let expectedStdout = requireStringField resolved ctx "expected_stdout" tbl
                let elfSpec =
                    match Map.tryFind "elf" tbl with
                    | None ->
                        if category = "static-dynamic-link-elision" then
                            fail resolved $"Seam check '{id}' in category 'static-dynamic-link-elision' requires an [checks.elf] table."
                        None
                    | Some(TomlValue.Table elfTbl) | Some(TomlValue.InlineTable elfTbl) ->
                        Some(parseElf id category elfTbl)
                    | _ -> fail resolved $"{ctx}.elf must be a TOML table."
                if category = "static-dynamic-link-elision" && (elfSpec |> Option.exists (fun elf -> elf.SharedLibrary.IsSome) |> not) then
                    fail resolved $"{ctx}.elf requires shared_library for static-dynamic-link-elision."
                { Id = id
                  Category = category
                  Summary = summary
                  EntrySource = entrySource
                  ExtraSources = extraSources
                  IncludeRuntimeSources = includeRuntime
                  RequiredSymbols = requiredSymbols
                  LoadingContract = loading
                  ExpectedExitCode = expectedExit
                  ExpectedStdout = expectedStdout
                  Elf = elfSpec })

        let controlTables =
            match Map.tryFind "refusal_controls" document with
            | None -> [||]
            | Some(TomlValue.Array items) ->
                items
                |> List.mapi (fun index item ->
                    match item with
                    | TomlValue.Table tbl | TomlValue.InlineTable tbl -> index, tbl
                    | _ -> fail resolved $"refusal_controls[{index}] must be a TOML table.")
                |> List.toArray
            | _ -> fail resolved "refusal_controls must be an array of TOML tables."

        let controlIds = HashSet<string>(StringComparer.Ordinal)
        let controls =
            controlTables
            |> Array.map (fun (index, tbl) ->
                let prelimCtx = $"refusal_controls[{index}]"
                requireAllowedKeys resolved prelimCtx
                    (set [ "id"; "kind"; "target"; "pilot"; "expected_code"; "misuse_summary"; "protection_summary" ])
                    tbl
                let id = requireStringField resolved prelimCtx "id" tbl
                if not (controlIds.Add id) then
                    fail resolved $"Duplicate refusal control id '{id}'."
                let ctx = $"refusal_controls[{id}]"
                let kind =
                    match requireStringField resolved ctx "kind" tbl with
                    | "pilot" -> RefusalControlKind.Pilot
                    | "substantiation" -> RefusalControlKind.Substantiation
                    | "composer" -> RefusalControlKind.Composer
                    | unknown -> fail resolved $"{ctx}.kind '{unknown}' is unsupported; expected 'pilot', 'substantiation', or 'composer'."
                let targetPath = resolveSpecFile resolved specDir (ctx + ".target") (requireStringField resolved ctx "target" tbl)
                let pilotPath =
                    optionalStringField resolved ctx "pilot" tbl
                    |> Option.map (resolveSpecFile resolved specDir (ctx + ".pilot"))
                if kind <> RefusalControlKind.Substantiation && pilotPath.IsSome then
                    fail resolved $"{ctx}.pilot is supported only when kind = 'substantiation'."
                let expectedCode = requireStringField resolved ctx "expected_code" tbl
                if not (Regex.IsMatch(expectedCode, @"\A[A-Z]+[0-9]+\z", RegexOptions.CultureInvariant)) then
                    fail resolved $"{ctx}.expected_code '{expectedCode}' is not a valid diagnostic code."
                let misuseSummary = requireStringField resolved ctx "misuse_summary" tbl
                let protectionSummary = requireStringField resolved ctx "protection_summary" tbl
                { Id = id
                  Kind = kind
                  TargetPath = targetPath
                  PilotPath = pilotPath
                  ExpectedCode = expectedCode
                  MisuseSummary = misuseSummary
                  ProtectionSummary = protectionSummary })

        let requiredPhysical =
            manifest.Boundary.RequiredSeamChecks
            |> Array.filter ((<>) RefusalControlsCategory)
            |> Set.ofArray
        let coveredPhysical = checks |> Array.map _.Category |> Set.ofArray
        let missingPhysical = Set.difference requiredPhysical coveredPhysical
        if not missingPhysical.IsEmpty then
            let names = missingPhysical |> Set.toList |> String.concat ", "
            fail resolved $"Seam suite specification leaves required physical seam categories uncovered: {names}."
        if Array.contains RefusalControlsCategory manifest.Boundary.RequiredSeamChecks && controls.Length = 0 then
            fail resolved "Seam suite specification requires at least one [[refusal_controls]] entry for 'falsification-refusal-controls'."

        if Array.contains RefusalControlsCategory manifest.Boundary.RequiredSeamChecks then
            for pattern in [ @"\AFS9[0-9]{3}\z"; @"\ACCS84[0-9]{2}\z" ] do
                if controls |> Array.exists (fun control -> Regex.IsMatch(control.ExpectedCode, pattern)) |> not then
                    fail resolved $"Refusal controls must include a code matching {pattern}."

        { TargetTriple = targetTriple
          DefaultLoadingContract = defaultLoading
          Checks = checks
          RefusalControls = controls }

    // =========================================================================
    // Process execution and probe compilation
    // =========================================================================

    type private ProcessOutcome =
        { Command: string
          ExitCode: int
          Stdout: string
          Stderr: string }

    let private stripAnsi (text: string) : string =
        Regex.Replace(text, "\u001b\\[[0-9;]*m", "")

    let private runProcess (toolPath: string) (workingDirectory: string) (timeoutMs: int) (info: ProcessStartInfo) : ProcessOutcome =
        info.WorkingDirectory <- workingDirectory
        let command = info.FileName + " " + ProcessLaunch.commandLine info.ArgumentList
        use child = new Process(StartInfo = info)
        if not (child.Start()) then
            fail toolPath $"Process '{command}' failed to start."
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit timeoutMs) then
            child.Kill(true)
            child.WaitForExit()
            stdoutTask.GetAwaiter().GetResult() |> ignore
            stderrTask.GetAwaiter().GetResult() |> ignore
            fail toolPath $"Process '{command}' timed out after {timeoutMs} ms."
        child.WaitForExit()
        { Command = command
          ExitCode = child.ExitCode
          Stdout = stdoutTask.GetAwaiter().GetResult()
          Stderr = stderrTask.GetAwaiter().GetResult() }

    /// Execute a native seam binary under `RLIMIT_CORE = 0` (`ulimit -c 0`) and child-only
    /// `MALLOC_PERTURB_ = 165`, never mutating the parent .NET process environment.
    let private runNativeBinary (binaryPath: string) (probeWork: string) (extraLibraryDir: string option) : ProcessOutcome =
        let tempDir = Path.Combine(probeWork, "runtime-tmp")
        Directory.CreateDirectory tempDir |> ignore
        let info =
            ProcessLaunch.startInfo "/bin/sh"
                [ "-c"
                  "ulimit -c 0 && exec \"$@\""
                  "farscape-seam-probe"
                  binaryPath ]
        for variable in [ "TMPDIR"; "TMP"; "TEMP" ] do
            info.Environment[variable] <- tempDir
        info.Environment["MALLOC_PERTURB_"] <- "165"
        match extraLibraryDir with
        | Some libDir ->
            let existing = info.Environment.TryGetValue "LD_LIBRARY_PATH" |> function true, v when not (String.IsNullOrWhiteSpace v) -> Some v | _ -> None
            info.Environment["LD_LIBRARY_PATH"] <-
                match existing with
                | Some rest -> libDir + Path.PathSeparator.ToString() + rest
                | None -> libDir
        | None -> ()
        let outcome = runProcess binaryPath probeWork 60000 info
        File.WriteAllText(Path.Combine(probeWork, Path.GetFileName binaryPath + ".run.log"), outcome.Stdout + outcome.Stderr)
        outcome

    let private requireRecordedTools (manifest: Substantiation.Manifest) : unit =
        for tool in [ manifest.Dotnet; manifest.Compiler ] do
            if not (File.Exists tool.Path) || not (String.Equals(ClangToolchain.sha256 tool.Path, tool.Sha256, StringComparison.OrdinalIgnoreCase)) then
                fail tool.Path "Recorded compiler or dotnet host is missing or its SHA-256 changed."

    let private sanitizeIdentifier (raw: string) : string =
        raw
        |> Seq.map (fun c -> if Char.IsAsciiLetterOrDigit c || c = '_' then c else '_')
        |> Seq.toArray
        |> String

    let private requireNoShadowExterns (checkId: string) (probeSourcePath: string) : unit =
        let text = readUtf8 $"Seam check '{checkId}' probe source" probeSourcePath
        let lexed =
            try SubstantiationBoundary.scanSource probeSourcePath text
            with :? Diagnostics.RefusalException as refused -> recodeToSeamFailure probeSourcePath refused.Refusal
        if (lexed.Attributes |> Array.exists SubstantiationBoundary.hasFidelityExtern) || lexed.ExternIdentifierCount > 0 then
            fail probeSourcePath
                $"Seam check '{checkId}' probe source '{probeSourcePath}' declares [<FidelityExtern>] or extern bindings outside manifest.Sources; physical probes must consume the substantiated library's own bindings."

    /// Expand source-free platform bundle dependencies when `loading` must be injected into `[platform]`
    /// for a custom process-linked foreign library (`Composer` BoundaryAdmission).
    let internal buildProbeFidproj (manifest: Substantiation.Manifest) (check: PhysicalCheckSpec) (probeWork: string) : string =
        let fidprojPath = manifest.FidprojPath
        let fidprojDir = Path.GetDirectoryName fidprojPath
        let doc =
            match Toml.parse (readUtf8 "Library .fidproj" fidprojPath) with
            | Ok parsed -> parsed
            | Error reason -> fail fidprojPath ("Library .fidproj is not valid TOML: " + reason)

        let rawDeps =
            match Toml.getTable "dependencies" doc with
            | Some tbl -> tbl
            | None -> Map.empty

        let existingPlatform =
            match Toml.getTable "platform" doc with
            | Some tbl -> tbl
            | None -> Map.empty

        let addUniqueDep (target: Dictionary<string, TomlValue>) (sourcePath: string) (depName: string) (depVal: TomlValue) =
            match target.TryGetValue depName with
            | true, prev when prev <> depVal ->
                fail sourcePath $"Conflicting dependency '{depName}' encountered while synthesizing seam probe project."
            | _ -> target[depName] <- depVal

        let addUniquePlat (target: Dictionary<string, TomlValue>) (sourcePath: string) (platKey: string) (platVal: TomlValue) =
            match target.TryGetValue platKey with
            | true, prev when prev <> platVal ->
                fail sourcePath $"Conflicting [platform].{platKey} setting encountered while synthesizing seam probe project."
            | _ -> target[platKey] <- platVal

        let rebasedDeps, mergedPlatform =
            match check.LoadingContract with
            | None ->
                let deps =
                    rawDeps
                    |> Map.map (fun name entry ->
                        match entry with
                        | TomlValue.Table sub | TomlValue.InlineTable sub ->
                            match Map.tryFind "path" sub with
                            | Some(TomlValue.String rel) ->
                                let abs = if Path.IsPathRooted rel then Path.GetFullPath rel else Path.GetFullPath(Path.Combine(fidprojDir, rel))
                                TomlValue.InlineTable(Map.add "path" (TomlValue.String abs) sub)
                            | _ -> fail fidprojPath $"dependencies.{name}.path must be a string."
                        | _ -> entry)
                deps, existingPlatform
            | Some selector ->
                let expandedDeps = Dictionary<string, TomlValue>(StringComparer.Ordinal)
                let platformKeys = Dictionary<string, TomlValue>(StringComparer.Ordinal)
                for KeyValue(k, v) in existingPlatform do
                    platformKeys[k] <- v
                for KeyValue(name, entry) in rawDeps do
                    match entry with
                    | TomlValue.Table sub | TomlValue.InlineTable sub ->
                        match Map.tryFind "path" sub with
                        | Some(TomlValue.String rel) ->
                            let abs = if Path.IsPathRooted rel then Path.GetFullPath rel else Path.GetFullPath(Path.Combine(fidprojDir, rel))
                            if not (File.Exists abs) then
                                fail abs $"Dependency '{name}' at '{abs}' does not exist."
                            let depDoc =
                                match Toml.parse (readUtf8 $"Dependency '{name}'" abs) with
                                | Ok d -> d
                                | Error r -> fail abs $"Dependency '{name}' is not valid TOML: {r}"
                            let isPlatformBundle =
                                match Toml.getValue "build.sources" depDoc, Toml.getTable "platform" depDoc with
                                | Some(TomlValue.Array []), Some platTbl when not platTbl.IsEmpty -> Some platTbl
                                | _ -> None
                            match isPlatformBundle with
                            | Some platTbl ->
                                let bundleDir = Path.GetDirectoryName abs
                                match Toml.getTable "dependencies" depDoc with
                                | Some bundleDeps ->
                                    for KeyValue(subName, subVal) in bundleDeps do
                                        match subVal with
                                        | TomlValue.Table sTbl | TomlValue.InlineTable sTbl ->
                                            match Map.tryFind "path" sTbl with
                                            | Some(TomlValue.String subRel) ->
                                                let subAbs = if Path.IsPathRooted subRel then Path.GetFullPath subRel else Path.GetFullPath(Path.Combine(bundleDir, subRel))
                                                addUniqueDep expandedDeps abs subName (TomlValue.InlineTable(Map.add "path" (TomlValue.String subAbs) sTbl))
                                            | _ -> fail abs $"Platform bundle dependency '{subName}' lacks a string path."
                                        | _ -> addUniqueDep expandedDeps abs subName subVal
                                | None -> ()
                                for KeyValue(pk, pv) in platTbl do
                                    addUniquePlat platformKeys abs pk pv
                            | None ->
                                addUniqueDep expandedDeps fidprojPath name (TomlValue.InlineTable(Map.add "path" (TomlValue.String abs) sub))
                        | _ -> fail fidprojPath $"dependencies.{name}.path must be a string."
                    | _ -> addUniqueDep expandedDeps fidprojPath name entry
                if platformKeys.ContainsKey "loading" && platformKeys["loading"] <> TomlValue.String selector then
                    fail fidprojPath $"[platform].loading already selects a different contract than '{selector}'."
                platformKeys["loading"] <- TomlValue.String selector
                let depMap = expandedDeps |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                let platMap = platformKeys |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                depMap, platMap

        for probeSrc in Array.append check.ExtraSources [| check.EntrySource |] do
            requireNoShadowExterns check.Id probeSrc

        let librarySources =
            if check.IncludeRuntimeSources then
                manifest.Sources |> Array.map _.Path
            else
                manifest.Sources
                |> Array.filter (fun src -> src.RuntimeDeclarations.Length = 0)
                |> Array.map _.Path
        if librarySources.Length = 0 then
            fail fidprojPath $"Substantiated package '{manifest.Package}' exposes no library source modules for physical seam compilation."
        let allSources = Array.concat [ librarySources; check.ExtraSources; [| check.EntrySource |] ]
        if (Array.distinct allSources).Length <> allSources.Length then
            fail check.EntrySource $"Seam check '{check.Id}' lists duplicate Clef source files in build.sources."

        let archivesMap =
            if manifest.Boundary.LinkMode = "static-archive" then
                let archives = manifest.Boundary.Files |> Array.filter (fun f -> f.Kind = "static-archive")
                if archives.Length = 0 || manifest.Boundary.Libraries.Length = 0 then
                    fail fidprojPath "Static-archive manifest records no static-archive file or linked library identifier."
                manifest.Boundary.Libraries
                |> Array.map (fun libId ->
                    let expectedName = "lib" + libId + ".a"
                    let matched =
                        archives
                        |> Array.tryFind (fun a -> String.Equals(Path.GetFileName a.Path, expectedName, StringComparison.Ordinal))
                        |> Option.map _.Path
                    match matched with
                    | Some archivePath -> libId, TomlValue.String archivePath
                    | None -> fail fidprojPath $"Static-archive manifest records no archive matching library '{libId}'.")
                |> Map.ofArray
            else
                Map.empty

        let linkTable =
            let baseLink =
                Map.ofList
                    [ ("libraries", TomlValue.Array(manifest.Boundary.Libraries |> Array.map TomlValue.String |> List.ofArray)) ]
            if archivesMap.IsEmpty then baseLink
            else Map.add "archives" (TomlValue.InlineTable archivesMap) baseLink

        let packageName = "SeamProbe_" + sanitizeIdentifier check.Id
        let probeDoc =
            [ yield "package", TomlValue.Table(Map.ofList [ ("name", TomlValue.String packageName); ("version", TomlValue.String "0.1.0") ])
              yield "compilation", TomlValue.Table(Map.ofList [ ("target", TomlValue.String "cpu") ])
              yield "dependencies", TomlValue.Table rebasedDeps
              yield "build", TomlValue.Table(
                  Map.ofList
                      [ ("sources", TomlValue.Array(allSources |> Array.map TomlValue.String |> List.ofArray))
                        ("output_kind", TomlValue.String "console") ])
              if not mergedPlatform.IsEmpty then
                  yield "platform", TomlValue.Table mergedPlatform
              if manifest.Boundary.Libraries.Length > 0 || not archivesMap.IsEmpty then
                  yield "link", TomlValue.Table linkTable ]
            |> Map.ofList

        let probeProjectPath = Path.Combine(probeWork, packageName + ".fidproj")
        File.WriteAllText(probeProjectPath, Toml.serialize probeDoc, UTF8Encoding(false))
        probeProjectPath

    let private compileComposerProbe (manifest: Substantiation.Manifest) (probeProjectPath: string) (probeBinaryPath: string) (probeWork: string) (verbose: bool) : ProcessOutcome =
        requireRecordedTools manifest
        let toolTemp = Path.Combine(probeWork, "composer-tmp")
        Directory.CreateDirectory toolTemp |> ignore
        let args =
            [ manifest.Compiler.Path
              "compile"
              "--warn-as-error"
              "--no-color"
              probeProjectPath
              "-k"
              "-o"
              probeBinaryPath ]
        let outcome =
            ScratchDirectory.within probeWork (fun () ->
                let info = ProcessLaunch.startInfo manifest.Dotnet.Path args
                for variable in [ "TMPDIR"; "TMP"; "TEMP" ] do
                    info.Environment[variable] <- toolTemp
                info.Environment["NO_COLOR"] <- "1"
                runProcess manifest.Compiler.Path probeWork 300000 info)
        let logPath = Path.Combine(probeWork, "composer.log")
        File.WriteAllText(logPath, outcome.Stdout + outcome.Stderr, UTF8Encoding(false))
        if verbose then
            Console.WriteLine(outcome.Stdout + outcome.Stderr)
        outcome

    // =========================================================================
    // ELF link-elision & dynamic-import verification
    // =========================================================================

    let private sharedLibraryShortName (sharedLibPath: string) : string =
        let fileName = Path.GetFileName sharedLibPath
        if fileName.StartsWith("lib", StringComparison.Ordinal) && fileName.EndsWith(".so", StringComparison.Ordinal) && fileName.Length > 6 then
            fileName.Substring(3, fileName.Length - 6)
        else
            fail sharedLibPath $"Shared library filename '{fileName}' must follow 'lib<name>.so' convention."

    let internal verifyElfExpectations (manifest: Substantiation.Manifest) (check: PhysicalCheckSpec) (elf: ElfExecutableInspection) (probeWork: string) : SharedLibraryComparison option =
        match check.Elf with
        | None -> None
        | Some spec ->
            if spec.ElidedSymbols.Length > 0 || spec.RetainedStaticSymbols.Length > 0 then
                if manifest.Boundary.LinkMode <> "static-archive" then
                    fail manifest.FidprojPath
                        $"Seam check '{check.Id}' static symbol elision/retention requires manifest LinkMode = 'static-archive', not '{manifest.Boundary.LinkMode}'."
                if not elf.HasSymtab then
                    fail elf.Path
                        $"Seam check '{check.Id}' static symbol elision/retention requires an unstripped ELF .symtab section in '{elf.Path}'."
                let archiveFiles =
                    manifest.Boundary.Files
                    |> Array.filter (fun f -> f.Kind = "static-archive")
                if archiveFiles.Length = 0 then
                    fail manifest.FidprojPath "Static-archive manifest records no static-archive file."
                let archiveSymbols =
                    archiveFiles
                    |> Array.fold (fun acc archiveFile ->
                        let archiveProvenance =
                            match ArchiveInspection.inspect archiveFile.Path (Some archiveFile.Sha256) with
                            | Ok prov -> prov
                            | Error refusal -> recodeToSeamFailure archiveFile.Path refusal
                        (acc, archiveProvenance.Symbols)
                        ||> Map.fold (fun merged sym owners -> Map.add sym owners merged))
                        Map.empty
                for deadSymbol in spec.ElidedSymbols do
                    if not (Map.containsKey deadSymbol archiveSymbols) then
                        fail archiveFiles[0].Path
                            $"Seam check '{check.Id}' expects dead symbol '{deadSymbol}' to be elided, but linked static archives do not define '{deadSymbol}'."
                for retained in spec.RetainedStaticSymbols do
                    if not (Map.containsKey retained archiveSymbols) then
                        fail archiveFiles[0].Path
                            $"Seam check '{check.Id}' expects static symbol '{retained}' to be retained, but linked static archives do not define '{retained}'."

            for deadSymbol in spec.ElidedSymbols do
                if elf.DefinedSymtab.Contains deadSymbol || elf.DefinedDynsym.Contains deadSymbol || elf.UndefinedDynsym.Contains deadSymbol then
                    fail elf.Path $"Seam check '{check.Id}' expected dead symbol '{deadSymbol}' to be elided from '{elf.Path}', but it remains in the ELF symbol tables."

            for retained in spec.RetainedStaticSymbols do
                if not (elf.DefinedSymtab.Contains retained) then
                    fail elf.Path $"Seam check '{check.Id}' expected statically linked symbol '{retained}' in .symtab of '{elf.Path}', but it is absent."
                if elf.UndefinedDynsym.Contains retained then
                    fail elf.Path $"Seam check '{check.Id}' expected '{retained}' to be statically resolved, but it appears as SHN_UNDEF in .dynsym."

            for forbidden in spec.ForbiddenNeededLibraries do
                if Array.contains forbidden elf.NeededLibraries then
                    fail elf.Path $"Seam check '{check.Id}' forbids DT_NEEDED '{forbidden}' in '{elf.Path}', but it is present."

            match spec.SharedLibrary, spec.SharedLibrarySha256, spec.DynamicProbeSource with
            | Some sharedPath, Some expectedSharedSha, Some dynProbeSource ->
                let sharedElf =
                    match inspectElfExecutable sharedPath with
                    | Ok inspected -> inspected
                    | Error refusal -> Diagnostics.raiseRefusal refusal
                if not (String.Equals(sharedElf.Sha256, expectedSharedSha, StringComparison.OrdinalIgnoreCase)) then
                    fail sharedPath
                        $"Shared library '{sharedPath}' SHA-256 '{sharedElf.Sha256}' disagrees with checks[{check.Id}].elf.shared_library_sha256 '{expectedSharedSha}'."
                if sharedElf.ElfType <> 3us then
                    fail sharedPath $"Shared library '{sharedPath}' is ELF type {sharedElf.ElfType}; expected ET_DYN (3)."
                for deadSymbol in spec.ElidedSymbols do
                    if not (sharedElf.DefinedDynsym.Contains deadSymbol) then
                        fail sharedPath $"Shared library '{sharedPath}' does not export comparison symbol '{deadSymbol}' in .dynsym."
                for imported in spec.RetainedDynamicImports do
                    if not (sharedElf.DefinedDynsym.Contains imported) then
                        fail sharedPath $"Shared library '{sharedPath}' does not export dynamic import '{imported}' in .dynsym."
                let dynLibDir = Path.Combine(probeWork, "dynamic-lib")
                Directory.CreateDirectory dynLibDir |> ignore
                let sharedFileName = Path.GetFileName sharedPath
                let stagedShared = Path.Combine(dynLibDir, sharedFileName)
                File.Copy(sharedPath, stagedShared, true)
                if not (String.Equals(ClangToolchain.sha256 stagedShared, sharedElf.Sha256, StringComparison.OrdinalIgnoreCase)) then
                    fail stagedShared $"Staged shared library '{stagedShared}' SHA-256 drifted during copy."
                let shortName = sharedLibraryShortName sharedPath
                let dynBinary = Path.Combine(probeWork, "dynamic-probe-" + sanitizeIdentifier check.Id)
                let compileOutcome =
                    ScratchDirectory.within probeWork (fun () ->
                        let info =
                            ClangToolchain.startInfo
                                [ "-O2"
                                  "-Wall"
                                  "-Wextra"
                                  "-Werror"
                                  dynProbeSource
                                  "-L" + dynLibDir
                                  "-l" + shortName
                                  "-o"
                                  dynBinary ]
                        runProcess (ClangToolchain.path ()) probeWork 60000 info)
                File.WriteAllText(Path.Combine(probeWork, "dynamic-clang.log"), compileOutcome.Stdout + compileOutcome.Stderr, UTF8Encoding(false))
                if compileOutcome.ExitCode <> 0 || not (File.Exists dynBinary) then
                    fail dynProbeSource $"Dynamic comparison probe for '{check.Id}' failed to compile/link (exit {compileOutcome.ExitCode}): {compileOutcome.Stdout}{compileOutcome.Stderr}"
                let dynRun = runNativeBinary dynBinary probeWork (Some dynLibDir)
                if dynRun.ExitCode <> 0 then
                    fail dynBinary $"Dynamic comparison probe '{dynBinary}' exited with code {dynRun.ExitCode}: {dynRun.Stdout}{dynRun.Stderr}"
                let dynElf =
                    match inspectElfExecutable dynBinary with
                    | Ok inspected -> inspected
                    | Error refusal -> Diagnostics.raiseRefusal refusal
                if not (Array.contains sharedFileName dynElf.NeededLibraries) then
                    fail dynBinary $"Dynamic comparison binary '{dynBinary}' lacks DT_NEEDED '{sharedFileName}'."
                for imported in spec.RetainedDynamicImports do
                    if not (dynElf.UndefinedDynsym.Contains imported) then
                        fail dynBinary $"Dynamic comparison binary '{dynBinary}' lacks expected undefined .dynsym import '{imported}'."
                for deadSymbol in spec.ElidedSymbols do
                    if dynElf.UndefinedDynsym.Contains deadSymbol then
                        fail dynBinary $"Dynamic comparison binary '{dynBinary}' unexpectedly imports unreferenced symbol '{deadSymbol}'."
                Some
                    { CheckId = check.Id
                      SharedLibraryPath = sharedElf.Path
                      SharedLibrarySha256 = sharedElf.Sha256
                      DynamicProbeBinaryPath = dynElf.Path
                      DynamicProbeBinarySha256 = dynElf.Sha256 }
            | None, None, None -> None
            | _ -> fail check.EntrySource $"Seam check '{check.Id}' has an incomplete shared_library comparison specification."

    let internal buildProbeFidprojDiagnosed (manifest: Substantiation.Manifest) (check: PhysicalCheckSpec) (probeWork: string) : Result<string, Diagnostics.Refusal> =
        protect manifest.FidprojPath (fun () -> buildProbeFidproj manifest check probeWork)

    let internal verifyElfExpectationsDiagnosed (manifest: Substantiation.Manifest) (check: PhysicalCheckSpec) (elf: ElfExecutableInspection) (probeWork: string) : Result<SharedLibraryComparison option, Diagnostics.Refusal> =
        protect elf.Path (fun () -> verifyElfExpectations manifest check elf probeWork)

    // =========================================================================
    // Executing physical checks and refusal controls
    // =========================================================================

    let private readProbeWitnessedImports (probeWork: string) : Set<string> =
        let witnessPath = Path.Combine(probeWork, "targets", "intermediates", "10_witness_units.json")
        if not (File.Exists witnessPath) then
            fail witnessPath "Composer did not retain 10_witness_units.json for physical seam probe."
        use doc = JsonDocument.Parse(readUtf8 "Probe 10_witness_units.json" witnessPath)
        let symbols = HashSet<string>(StringComparer.Ordinal)
        match doc.RootElement.TryGetProperty "units" with
        | true, units when units.ValueKind = JsonValueKind.Array ->
            for unitNode in units.EnumerateArray() do
                for propName in [ "imports"; "resourceImports" ] do
                    match unitNode.TryGetProperty propName with
                    | true, arr when arr.ValueKind = JsonValueKind.Array ->
                        for item in arr.EnumerateArray() do
                            match item.TryGetProperty "symbol" with
                            | true, sym when sym.ValueKind = JsonValueKind.String ->
                                let name = sym.GetString()
                                if not (String.IsNullOrWhiteSpace name) then
                                    symbols.Add name |> ignore
                            | _ -> ()
                    | _ -> ()
        | _ -> fail witnessPath "Probe 10_witness_units.json lacks a 'units' array."
        Set.ofSeq symbols

    let private requireSubstantiatedLibraryCall (manifest: Substantiation.Manifest) (check: PhysicalCheckSpec) (elf: ElfExecutableInspection) (probeWork: string) : unit =
        if manifest.Boundary.ExternSymbols.Length = 0 then
            fail manifest.FidprojPath $"Manifest '{manifest.Package}' declares zero ExternSymbols for physical seam check '{check.Id}'."
        let witnessedImports = readProbeWitnessedImports probeWork
        let inElf sym =
            if manifest.Boundary.LinkMode = "static-archive" then
                elf.DefinedSymtab.Contains sym
            else
                elf.UndefinedDynsym.Contains sym || elf.DefinedSymtab.Contains sym
        let referenced =
            manifest.Boundary.ExternSymbols
            |> Array.exists (fun sym -> witnessedImports.Contains sym && inElf sym)
        if not referenced then
            fail elf.Path
                $"Seam check '{check.Id}' ({check.Category}) binary '{elf.Path}' references zero foreign symbols from manifest.Boundary.ExternSymbols; every physical probe must exercise the substantiated library."
        for reqSym in check.RequiredSymbols do
            if not (witnessedImports.Contains reqSym && inElf reqSym) then
                fail elf.Path
                    $"Seam check '{check.Id}' ({check.Category}) binary '{elf.Path}' omits required foreign symbol '{reqSym}' declared in checks[{check.Id}].required_symbols."

    let private runPhysicalCheck (manifest: Substantiation.Manifest) (workRoot: string) (verbose: bool) (index: int) (check: PhysicalCheckSpec) : SeamCheckEntry * RetainedProbeBinary * SharedLibraryComparison option =
        let probeWork = Path.Combine(workRoot, $"check-{index:D2}-" + sanitizeIdentifier check.Id)
        Directory.CreateDirectory probeWork |> ignore
        let probeProject = buildProbeFidproj manifest check probeWork
        let probeBinary = Path.Combine(probeWork, "probe-" + sanitizeIdentifier check.Id)
        let compileOutcome = compileComposerProbe manifest probeProject probeBinary probeWork verbose
        if compileOutcome.ExitCode <> 0 || not (File.Exists probeBinary) then
            let plain = stripAnsi (compileOutcome.Stdout + compileOutcome.Stderr)
            fail check.EntrySource
                $"Seam check '{check.Id}' ({check.Category}) failed Composer compilation (exit {compileOutcome.ExitCode}):\n{plain}"
        let elf =
            match inspectElfExecutable probeBinary with
            | Ok inspected -> inspected
            | Error refusal -> Diagnostics.raiseRefusal refusal
        requireSubstantiatedLibraryCall manifest check elf probeWork
        let sharedComparison = verifyElfExpectations manifest check elf probeWork
        let runOutcome = runNativeBinary probeBinary probeWork None
        if runOutcome.ExitCode <> check.ExpectedExitCode then
            fail probeBinary
                $"Seam check '{check.Id}' ({check.Category}) exited with code {runOutcome.ExitCode} (expected {check.ExpectedExitCode}). Output:\n{runOutcome.Stdout}{runOutcome.Stderr}"
        let actualNorm = runOutcome.Stdout.Replace("\r\n", "\n").Trim()
        let expectedNorm = check.ExpectedStdout.Replace("\r\n", "\n").Trim()
        if actualNorm <> expectedNorm then
            fail probeBinary
                $"Seam check '{check.Id}' ({check.Category}) stdout '{actualNorm}' did not match expected '{expectedNorm}'."
        let entry =
            { Id = check.Id
              Category = check.Category
              Summary = check.Summary
              BinarySha256 = elf.Sha256
              Passed = true
              Command = runOutcome.Command }
        let retained : RetainedProbeBinary =
            { CheckId = check.Id
              BinaryPath = elf.Path
              BinarySha256 = elf.Sha256 }
        entry, retained, sharedComparison

    let private extractCompilerErrorCodes (output: string) : string[] =
        let plain = stripAnsi output
        let seen = HashSet<string>(StringComparer.Ordinal)
        let ordered = ResizeArray<string>()
        let add (code: string) =
            if seen.Add code then ordered.Add code
        for line in plain.Split('\n') do
            let trimmed = line.TrimEnd('\r')
            // Standard Composer error diagnostic line: `<file>:<line>:<col>: error CCS8401: ...`
            let diagMatch = Regex.Match(trimmed, @"^.+?:[0-9]+(?::[0-9]+)?:\s*error\s+([A-Z]+[0-9]+):")
            if diagMatch.Success then
                add diagMatch.Groups[1].Value
            // Bracketed error summary line: `Error: [CCS8403] ...`
            let bracketMatch = Regex.Match(trimmed, @"^\s*Error:\s*\[([A-Z]+[0-9]+)\]")
            if bracketMatch.Success then
                add bracketMatch.Groups[1].Value
        ordered.ToArray()

    let private runRefusalControl (manifest: Substantiation.Manifest) (workRoot: string) (verbose: bool) (index: int) (control: RefusalControlSpec) : RefusalControlEntry =
        let controlWork = Path.Combine(workRoot, $"refusal-{index:D2}-" + sanitizeIdentifier control.Id)
        let controlOut = Path.Combine(controlWork, "out")
        Directory.CreateDirectory controlWork |> ignore
        Directory.CreateDirectory controlOut |> ignore
        let observedCodes =
            match control.Kind with
            | RefusalControlKind.Pilot ->
                let result =
                    ScratchDirectory.within controlWork (fun () ->
                        BindingGenerator.generateFromProjectDiagnosed control.TargetPath verbose false Types.LP64)
                match result with
                | Ok _ ->
                    fail control.TargetPath $"Refusal control '{control.Id}' (kind = pilot) unexpectedly succeeded without refusal (expected {control.ExpectedCode})."
                | Error refusal ->
                    refusal.Diagnostics |> List.map _.Code |> List.distinct |> List.toArray
            | RefusalControlKind.Substantiation ->
                let subRequest : Substantiation.Request =
                    { FidprojPath = control.TargetPath
                      PilotPath = control.PilotPath
                      LinkProvenancePath = None
                      OutputDirectory = Some controlOut
                      WorkDirectory = Some(Path.Combine(controlWork, "evidence"))
                      Verbose = verbose
                      AllowWarnings = false }
                match Substantiation.evaluate subRequest with
                | Ok _ ->
                    fail control.TargetPath $"Refusal control '{control.Id}' (kind = substantiation) unexpectedly succeeded without refusal (expected {control.ExpectedCode})."
                | Error refusal ->
                    refusal.Diagnostics |> List.map _.Code |> List.distinct |> List.toArray
            | RefusalControlKind.Composer ->
                let refusedBin = Path.Combine(controlWork, "refused-binary")
                let outcome = compileComposerProbe manifest control.TargetPath refusedBin controlWork verbose
                if outcome.ExitCode = 0 || File.Exists refusedBin then
                    fail control.TargetPath $"Refusal control '{control.Id}' (kind = composer) unexpectedly compiled cleanly (expected {control.ExpectedCode})."
                let combined = outcome.Stdout + outcome.Stderr
                if combined.Contains("Unhandled exception", StringComparison.Ordinal) then
                    fail control.TargetPath $"Refusal control '{control.Id}' caused an unhandled compiler exception rather than a coded refusal."
                extractCompilerErrorCodes combined

        let observedCode =
            match observedCodes with
            | [| singleCode |] when singleCode = control.ExpectedCode -> singleCode
            | [||] ->
                fail control.TargetPath
                    $"Refusal control '{control.Id}' expected diagnostic code {control.ExpectedCode}, but observed <none>."
            | [| singleCode |] ->
                fail control.TargetPath
                    $"Refusal control '{control.Id}' expected diagnostic code {control.ExpectedCode}, but observed {singleCode}."
            | multiple ->
                let actual = String.concat ", " multiple
                fail control.TargetPath
                    $"Refusal control '{control.Id}' expected exact diagnostic set {{{control.ExpectedCode}}}, but observed multiple codes {{{actual}}}."

        { Id = control.Id
          ExpectedCode = control.ExpectedCode
          ObservedCode = observedCode
          MisuseSummary = control.MisuseSummary
          ProtectionSummary = control.ProtectionSummary
          Passed = true }

    let private runBoundedParallel<'In, 'Out> (defaultDiagnosticPath: string) (items: 'In[]) (pathOf: 'In -> string) (worker: int -> 'In -> 'Out) : 'Out[] =
        if items.Length = 0 then
            [||]
        elif items.Length = 1 then
            [| worker 0 items[0] |]
        else
            let degree = min 2 (max 1 (Environment.ProcessorCount / 4))
            let mutable aborted = 0
            let outcomes =
                items
                |> Array.mapi (fun index item ->
                    async {
                        if Threading.Volatile.Read(&aborted) <> 0 then
                            return None
                        else
                            let result = protect (pathOf item) (fun () -> worker index item)
                            match result with
                            | Ok _ -> return Some result
                            | Error _ ->
                                Threading.Interlocked.Exchange(&aborted, 1) |> ignore
                                return Some result
                    })
                |> fun jobs -> Async.Parallel(jobs, maxDegreeOfParallelism = degree)
                |> Async.RunSynchronously
            let resolved = ResizeArray<'Out>(items.Length)
            for outcome in outcomes do
                match outcome with
                | Some(Ok value) -> resolved.Add value
                | Some(Error refusal) -> Diagnostics.raiseRefusal refusal
                | None -> ()
            if resolved.Count <> items.Length then
                fail defaultDiagnosticPath "Internal seam suite error: incomplete parallel evaluation result."
            resolved.ToArray()

    // =========================================================================
    // Ledger receipt reader and full evaluation pipeline
    // =========================================================================

    /// Parse and validate a seams ledger receipt, re-hashing and re-parsing its child
    /// receipts on disk and re-verifying its `Discharge` summary against `manifest.Proof`
    /// and the retained discharge artifacts (#219).
    let readLedgerReceipt (manifest: Substantiation.Manifest) (path: string) : Result<SeamsLedgerReceipt, Diagnostics.Refusal> =
        protect path (fun () ->
            let resolved = evidencePath path
            let text = readUtf8 "The seams ledger receipt" resolved
            let ledger =
                try
                    use doc = JsonDocument.Parse text
                    Substantiation.requireJsonShape typeof<SeamsLedgerReceipt> doc.RootElement
                    JsonSerializer.Deserialize<SeamsLedgerReceipt> text
                with
                | :? Diagnostics.RefusalException -> reraise ()
                | error -> fail resolved ("The seams ledger receipt violates its required JSON schema: " + error.Message)
            if ledger.Schema <> LedgerSchema || ledger.SchemaVersion <> 1 then
                fail resolved $"Unsupported seams ledger schema '{ledger.Schema}' version {ledger.SchemaVersion}."
            if ledger.SeamChecksSchema <> SeamChecksSchema || ledger.RefusalControlsSchema <> RefusalControlsSchema then
                fail resolved "Seams ledger records unexpected child receipt schema identifiers."
            if ledger.Package <> manifest.Package then
                fail resolved $"Seams ledger names package '{ledger.Package}', not '{manifest.Package}'."
            if not (String.Equals(ledger.ManifestIntegritySha256, manifest.IntegritySha256, StringComparison.OrdinalIgnoreCase)) then
                fail resolved "Seams ledger records a different ManifestIntegritySha256."
            if String.IsNullOrWhiteSpace manifest.Boundary.ClangTarget || manifest.Boundary.ClangTarget = "not-measured" then
                fail resolved $"Manifest '{manifest.Package}' lacks a measured ClangTarget."
            if ledger.TargetTriple <> manifest.Boundary.ClangTarget then
                fail resolved $"Seams ledger TargetTriple '{ledger.TargetTriple}' disagrees with manifest ClangTarget '{manifest.Boundary.ClangTarget}'."
            if ledger.RequiredSeamCategories <> manifest.Boundary.RequiredSeamChecks then
                fail resolved "Seams ledger RequiredSeamCategories disagrees with manifest.Boundary.RequiredSeamChecks."
            let requiredPhysical =
                manifest.Boundary.RequiredSeamChecks
                |> Array.filter ((<>) RefusalControlsCategory)
                |> Set.ofArray
            if isNull ledger.VerifiedPhysicalCategories
               || (Array.distinct ledger.VerifiedPhysicalCategories).Length <> ledger.VerifiedPhysicalCategories.Length
               || not (Set.isSubset (Set.ofArray ledger.VerifiedPhysicalCategories) requiredPhysical) then
                fail resolved "Seams ledger VerifiedPhysicalCategories contains invalid or duplicate physical seam categories."
            if not (isSha256 ledger.SeamChecksSha256) || not (isSha256 ledger.RefusalControlsSha256) then
                fail resolved "Seams ledger lacks valid 64-hex SHA-256 digests for child receipts."

            let seamChecksPath = evidencePath ledger.SeamChecksPath
            if not (File.Exists seamChecksPath) then
                fail seamChecksPath "Seams ledger child receipt SeamChecksPath does not exist."
            let liveSeamSha = ClangToolchain.sha256 seamChecksPath
            if not (String.Equals(liveSeamSha, ledger.SeamChecksSha256, StringComparison.OrdinalIgnoreCase)) then
                fail seamChecksPath $"Child seam-checks receipt SHA-256 '{liveSeamSha}' disagrees with ledger SeamChecksSha256 '{ledger.SeamChecksSha256}'."
            let seamReceipt =
                match readSeamReceipt manifest seamChecksPath with
                | Ok receipt -> receipt
                | Error refusal -> recodeToSeamFailure seamChecksPath refusal
            let recordedPhysical = seamReceipt.Checks |> Array.map _.Category |> Set.ofArray
            if Set.ofArray ledger.VerifiedPhysicalCategories <> recordedPhysical then
                fail resolved "Seams ledger VerifiedPhysicalCategories disagrees with the physical categories recorded in SeamChecksReceipt."

            let refusalControlsPath = evidencePath ledger.RefusalControlsPath
            if not (File.Exists refusalControlsPath) then
                fail refusalControlsPath "Seams ledger child receipt RefusalControlsPath does not exist."
            let liveRefusalSha = ClangToolchain.sha256 refusalControlsPath
            if not (String.Equals(liveRefusalSha, ledger.RefusalControlsSha256, StringComparison.OrdinalIgnoreCase)) then
                fail refusalControlsPath $"Child refusal-controls receipt SHA-256 '{liveRefusalSha}' disagrees with ledger RefusalControlsSha256 '{ledger.RefusalControlsSha256}'."
            match readRefusalReceipt manifest refusalControlsPath with
            | Ok _ -> ()
            | Error refusal -> recodeToSeamFailure refusalControlsPath refusal

            if isNull ledger.RetainedBinaries || ledger.RetainedBinaries.Length <> seamReceipt.Checks.Length then
                fail resolved "Seams ledger RetainedBinaries does not cover every physical seam check 1-to-1."
            let retainedByCheck =
                ledger.RetainedBinaries
                |> Array.map (fun item ->
                    if String.IsNullOrWhiteSpace item.CheckId || not (isSha256 item.BinarySha256) then
                        fail resolved "Seams ledger RetainedBinaries entry lacks a valid CheckId or 64-hex BinarySha256."
                    item.CheckId, item)
                |> Map.ofArray
            if retainedByCheck.Count <> seamReceipt.Checks.Length then
                fail resolved "Seams ledger RetainedBinaries contains duplicate CheckId entries."
            for check in seamReceipt.Checks do
                match Map.tryFind check.Id retainedByCheck with
                | None -> fail resolved $"Seams ledger RetainedBinaries omits physical check '{check.Id}'."
                | Some retained ->
                    if not (String.Equals(retained.BinarySha256, check.BinarySha256, StringComparison.OrdinalIgnoreCase)) then
                        fail resolved $"Seams ledger RetainedBinaries digest for '{check.Id}' disagrees with SeamChecksReceipt BinarySha256."
                    let binPath = evidencePath retained.BinaryPath
                    if not (File.Exists binPath) then
                        fail binPath $"Retained seam probe binary '{binPath}' for check '{check.Id}' does not exist."
                    let liveBinSha = ClangToolchain.sha256 binPath
                    if not (String.Equals(liveBinSha, check.BinarySha256, StringComparison.OrdinalIgnoreCase)) then
                        fail binPath $"Retained seam probe binary '{binPath}' SHA-256 '{liveBinSha}' disagrees with recorded BinarySha256 '{check.BinarySha256}'."

            if isNull ledger.SharedLibraries then
                fail resolved "Seams ledger SharedLibraries array is null."
            let expectedElisionCheckIds =
                seamReceipt.Checks
                |> Array.filter (fun c -> c.Category = "static-dynamic-link-elision")
                |> Array.map _.Id
                |> Set.ofArray
            let seenSharedCheckIds = HashSet<string>(StringComparer.Ordinal)
            for entry in ledger.SharedLibraries do
                if String.IsNullOrWhiteSpace entry.CheckId
                   || not (isSha256 entry.SharedLibrarySha256)
                   || not (isSha256 entry.DynamicProbeBinarySha256) then
                    fail resolved "Seams ledger SharedLibraries entry lacks a valid CheckId or 64-hex SHA-256 digest."
                if not (seenSharedCheckIds.Add entry.CheckId) then
                    fail resolved $"Seams ledger SharedLibraries contains duplicate CheckId '{entry.CheckId}'."
                if not (expectedElisionCheckIds.Contains entry.CheckId) then
                    fail resolved $"Seams ledger SharedLibraries CheckId '{entry.CheckId}' does not match any 'static-dynamic-link-elision' check in SeamChecksReceipt."
                let sharedPath = evidencePath entry.SharedLibraryPath
                if not (File.Exists sharedPath) then
                    fail sharedPath $"Seams ledger SharedLibraryPath '{sharedPath}' does not exist."
                let liveSharedSha = ClangToolchain.sha256 sharedPath
                if not (String.Equals(liveSharedSha, entry.SharedLibrarySha256, StringComparison.OrdinalIgnoreCase)) then
                    fail sharedPath $"Shared library '{sharedPath}' SHA-256 '{liveSharedSha}' disagrees with ledger SharedLibrarySha256 '{entry.SharedLibrarySha256}'."
                let dynBinPath = evidencePath entry.DynamicProbeBinaryPath
                if not (File.Exists dynBinPath) then
                    fail dynBinPath $"Seams ledger DynamicProbeBinaryPath '{dynBinPath}' does not exist."
                let liveDynBinSha = ClangToolchain.sha256 dynBinPath
                if not (String.Equals(liveDynBinSha, entry.DynamicProbeBinarySha256, StringComparison.OrdinalIgnoreCase)) then
                    fail dynBinPath $"Dynamic comparison binary '{dynBinPath}' SHA-256 '{liveDynBinSha}' disagrees with ledger DynamicProbeBinarySha256 '{entry.DynamicProbeBinarySha256}'."
            if Set.ofSeq seenSharedCheckIds <> expectedElisionCheckIds then
                fail resolved "Seams ledger SharedLibraries does not cover every 'static-dynamic-link-elision' check 1-to-1."

            if isNull (box ledger.Discharge) then
                fail resolved "Seams ledger Discharge summary is null."
            let liveDischarge = verifyDischargeCore manifest
            let ledgerReceiptPath = evidencePath ledger.Discharge.ReceiptPath
            let ledgerPremisesPath = evidencePath ledger.Discharge.PremisesOutputPath
            if ledgerReceiptPath <> liveDischarge.ReceiptPath
               || ledgerPremisesPath <> liveDischarge.PremisesOutputPath
               || not (String.Equals(ledger.Discharge.ReceiptSha256, liveDischarge.ReceiptSha256, StringComparison.OrdinalIgnoreCase))
               || not (String.Equals(ledger.Discharge.PremisesOutputSha256, liveDischarge.PremisesOutputSha256, StringComparison.OrdinalIgnoreCase))
               || ledger.Discharge.TotalObligations <> liveDischarge.TotalObligations
               || ledger.Discharge.DischargedUnsatCount <> liveDischarge.DischargedUnsatCount
               || ledger.Discharge.PremisesSatCount <> liveDischarge.PremisesSatCount
               || ledger.Discharge.LibraryObligations <> liveDischarge.LibraryObligations
               || ledger.Discharge.LibraryDischargedUnsatCount <> liveDischarge.LibraryDischargedUnsatCount
               || ledger.Discharge.LibraryPremisesSatCount <> liveDischarge.LibraryPremisesSatCount
               || ledger.Discharge.PendingOpenCount <> 0
               || ledger.Discharge.RefutedCount <> 0 then
                fail resolved "Seams ledger Discharge summary disagrees with manifest.Proof or the live retained discharge artifacts."

            ledger)

    /// Execute the complete physical ABI, link, lifecycle, and refusal-control seam suite
    /// for a substantiated library manifest and write verified JSON receipts (#219).
    let evaluate (request: Request) : Result<Generated, Diagnostics.Refusal> =
        protect request.ManifestPath (fun () ->
            let manifestPath = evidencePath request.ManifestPath
            let specPath = evidencePath request.SpecPath
            request.OutputDirectory |> Option.iter (evidencePath >> ignore)
            request.WorkDirectory |> Option.iter (evidencePath >> ignore)

            let manifest, discharge = loadAndValidateManifest manifestPath
            let spec = loadSpec manifest specPath

            let outputDir =
                request.OutputDirectory
                |> Option.defaultWith (fun () -> Path.GetDirectoryName manifestPath)
                |> evidencePath
            let workRoot =
                request.WorkDirectory
                |> Option.defaultWith (fun () -> Path.Combine(outputDir, manifest.Package + ".seams.evidence"))
                |> evidencePath
            let runWork = Path.Combine(workRoot, Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory runWork |> ignore

            let indexedControls = spec.RefusalControls |> Array.mapi (fun idx ctrl -> idx, ctrl)
            let inProcessControls =
                indexedControls
                |> Array.filter (fun (_, ctrl) -> ctrl.Kind <> RefusalControlKind.Composer)
                |> Array.map (fun (idx, ctrl) -> idx, runRefusalControl manifest runWork request.Verbose idx ctrl)

            let checkOutcomes =
                runBoundedParallel specPath spec.Checks _.EntrySource (runPhysicalCheck manifest runWork request.Verbose)
            let checkEntries = checkOutcomes |> Array.map (fun (entry, _, _) -> entry)
            let retainedBinaries = checkOutcomes |> Array.map (fun (_, retained, _) -> retained)
            let sharedLibraries = checkOutcomes |> Array.choose (fun (_, _, shared) -> shared)

            let composerControls =
                let subset = indexedControls |> Array.filter (fun (_, ctrl) -> ctrl.Kind = RefusalControlKind.Composer)
                runBoundedParallel specPath subset (fun (_, ctrl) -> ctrl.TargetPath) (fun _ (idx, ctrl) ->
                    idx, runRefusalControl manifest runWork request.Verbose idx ctrl)
            let controlEntries =
                Array.append inProcessControls composerControls
                |> Array.sortBy fst
                |> Array.map snd

            let seamChecksPath = Path.Combine(outputDir, manifest.Package + ".seam-checks.json")
            let refusalControlsPath = Path.Combine(outputDir, manifest.Package + ".refusal-controls.json")
            let ledgerPath = Path.Combine(outputDir, manifest.Package + ".seams-ledger.json")

            let seamReceipt : SeamChecksReceipt =
                { SchemaVersion = 1
                  Package = manifest.Package
                  ManifestIntegritySha256 = manifest.IntegritySha256
                  TargetTriple = spec.TargetTriple
                  Checks = checkEntries }

            let refusalReceipt : RefusalControlsReceipt =
                { SchemaVersion = 1
                  Package = manifest.Package
                  ManifestIntegritySha256 = manifest.IntegritySha256
                  Controls = controlEntries }

            let seamJson = serializeVerified seamChecksPath seamReceipt
            let refusalJson = serializeVerified refusalControlsPath refusalReceipt
            let seamSha = shaText seamJson
            let refusalSha = shaText refusalJson

            let verifiedPhysical =
                checkEntries
                |> Array.map _.Category
                |> Array.distinct

            let ledger : SeamsLedgerReceipt =
                { Schema = LedgerSchema
                  SchemaVersion = 1
                  SeamChecksSchema = SeamChecksSchema
                  RefusalControlsSchema = RefusalControlsSchema
                  Package = manifest.Package
                  ManifestIntegritySha256 = manifest.IntegritySha256
                  TargetTriple = spec.TargetTriple
                  RequiredSeamCategories = manifest.Boundary.RequiredSeamChecks
                  VerifiedPhysicalCategories = verifiedPhysical
                  Discharge = discharge
                  RetainedBinaries = retainedBinaries
                  SharedLibraries = sharedLibraries
                  SeamChecksPath = seamChecksPath
                  SeamChecksSha256 = seamSha
                  RefusalControlsPath = refusalControlsPath
                  RefusalControlsSha256 = refusalSha }

            let ledgerJson = serializeVerified ledgerPath ledger

            writeAtomic seamChecksPath seamJson
            writeAtomic refusalControlsPath refusalJson
            writeAtomic ledgerPath ledgerJson

            { Manifest = manifest
              Discharge = discharge
              SeamChecks = seamReceipt
              RefusalControls = refusalReceipt
              Ledger = ledger
              SeamChecksPath = seamChecksPath
              RefusalControlsPath = refusalControlsPath
              LedgerPath = ledgerPath })
