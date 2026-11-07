namespace Farscape.Core

/// Farscape generator diagnostic codes, in the Farscape wiki's `FS` plus four digits format
/// (02_BAREWire_Integration's `FS8001`). FS8xxx is taken by Clef compiler diagnostics the wiki
/// documents (FS8001, FS8020), so generator refusals are allocated sequentially from FS9101.
///
/// | Code   | Refusal                                                                        |
/// |--------|--------------------------------------------------------------------------------|
/// | FS9101 | a macro is defined differently by two of a pilot's translation units          |
/// | FS9102 | a unit's typedef chain disagrees with the profile's scalar row for that name  |
/// | FS9103 | a C/C++ enum is used without a clang measurement of its representation        |
/// | FS9104 | a clang enum measurement contradicts the enum's declared underlying type      |
/// | FS9105 | an enumerator's value is outside the signed 64-bit carrier                    |
/// | FS9106 | a protocol enum is used without one declared wire type for it                 |
/// | FS9107 | a protocol enum's value does not fit its protocol's declared wire type        |
/// | FS9108 | a GIR-named C type is declared by no pilot unit                               |
/// | FS9109 | a GIR-named C type is declared differently by two pilot units                 |
/// | FS9110 | a C/C++ enumerator's value does not fit clang's measured representation       |
/// | FS9111 | a C/C++ enum's declared underlying type has no integer representation         |
/// | FS9112 | an enum is declared differently by two of a pilot's translation units         |
/// | FS9113 | an anonymous nested record has no clang-measured layout in its unit           |
/// | FS9114 | a generated anonymous record name names another declaration of its unit       |
/// | FS9115 | a by-value type resolves through no typedef, record or profile entry          |
/// | FS9116 | a record is defined differently by two of a pilot's translation units         |
/// | FS9117 | a clang built-in type alias in a result has no canonical type clang can give  |
/// | FS9118 | clang's measurement of an alias's canonical type contradicts its profile row  |
/// | FS9119 | a descriptor cites a record the pilot declares no value type for              |
/// | FS9120 | a result alias is unreadable: its function takes an incomplete type by value  |
/// | FS9121 | a layout-neutral attribute is unconfirmed by clang's measured record layout   |
/// | FS9122 | an anonymous struct or union member has no representation in its record       |
/// | FS9123 | a generated anonymous member name names another member of its record          |
/// | FS9124 | a pilot define is not NAME or NAME=VALUE, so it could carry a clang option    |
/// | FS9125 | a pkg-config module name is not a module name, so it could carry an option    |
/// | FS9126 | pkg-config --cflags output holds a word that is not an include path or define |
/// | FS9127 | a field's type reaches an attributed typedef, and clang has no layout of it   |
/// | FS9128 | a typedef naming a record carries an attribute its record layout dump omits   |
/// | FS9129 | a record carries an unnamed bit-field, and clang has no layout of it          |
/// | FS9130 | a protocol enum entry's value is no (negated) C integer constant in int64     |
///
/// Each refusal reason has exactly one code and each code one reason.
///
/// Every refusal is built as a `Diagnostic` (#184): its code, its message, its severity, where
/// in a header or protocol file its cause is declared (or the exact reason that is unknown), and
/// the pilot file and key whose input caused it. A `Refusal` carries the diagnostics of one
/// failure with the exact text Farscape prints for it; `RefusalException` carries a refusal
/// raised from inside generation. The text is `render` of each diagnostic, so the structure
/// changes nothing a caller reads as text.
module Diagnostics =

    [<Literal>]
    let MacroDivergent = "FS9101"          // macro defined differently across a pilot's units (#56)

    [<Literal>]
    let TypedefProfileConflict = "FS9102"  // unit typedef chain vs profile scalar row (#54)

    [<Literal>]
    let EnumUnmeasured = "FS9103"          // C/C++ enum without a clang measurement (#53)

    [<Literal>]
    let EnumReprConflict = "FS9104"        // clang enum measurement vs declared underlying type (#53)

    [<Literal>]
    let EnumValueRange = "FS9105"          // enumerator outside the signed 64-bit carrier (#53)

    [<Literal>]
    let ProtocolEnumUndeclared = "FS9106"  // protocol enum without one declared wire type (#53)

    [<Literal>]
    let ProtocolEnumValueConflict = "FS9107" // protocol enum value outside its declared wire type (#53)

    [<Literal>]
    let GirTypeUndeclared = "FS9108"       // GIR-named C type declared by no pilot unit (#54)

    [<Literal>]
    let GirTypeDivergent = "FS9109"        // GIR-named C type declared differently by two units (#54)

    [<Literal>]
    let EnumValueConflict = "FS9110"       // C/C++ enumerator outside clang's measured representation (#53)

    [<Literal>]
    let EnumUnderlyingUnrepresented = "FS9111" // C/C++ enum underlying type with no integer representation (#53)

    [<Literal>]
    let EnumDivergent = "FS9112"           // enum declared differently by two of a pilot's units (#53, #54)

    [<Literal>]
    let AnonymousRecordUnmeasured = "FS9113" // anonymous nested record without a clang-measured layout (#159)

    [<Literal>]
    let AnonymousNameCollision = "FS9114"  // generated anonymous record name names another declaration (#159)

    [<Literal>]
    let ByValueTypeUnresolved = "FS9115"   // by-value type resolving nowhere, so no descriptor width (#162)

    [<Literal>]
    let RecordDivergent = "FS9116"         // record defined differently by two of a pilot's units (#161)

    [<Literal>]
    let AliasUncanonicalized = "FS9117"    // clang built-in alias without a canonical type clang gives (#171)

    [<Literal>]
    let AliasMeasurementConflict = "FS9118" // alias canonical type: clang measurement vs profile row (#171)

    [<Literal>]
    let RecordWithoutValueType = "FS9119"  // descriptor cites a record with no emitted value type (#162)

    [<Literal>]
    let AliasIncompleteParameter = "FS9120" // result alias unreadable: function takes an incomplete type by value (#171)

    [<Literal>]
    let LayoutNeutralUnconfirmed = "FS9121" // layout-neutral attribute without clang's agreeing measurement (#174)

    [<Literal>]
    let AnonymousMemberUnrepresented = "FS9122" // C11 anonymous member with no representation in its record (#173)

    [<Literal>]
    let AnonymousMemberNameCollision = "FS9123" // generated anonymous member name names another member (#173)

    [<Literal>]
    let DefineMalformed = "FS9124"         // pilot define that is not NAME or NAME=VALUE (#178)

    [<Literal>]
    let PkgConfigModuleMalformed = "FS9125" // pkg-config module name that is not a module name (#178)

    [<Literal>]
    let PkgConfigFlagRefused = "FS9126"   // pkg-config --cflags word other than -I or -D (#178)

    [<Literal>]
    let TypedefAttributeUnmeasured = "FS9127" // field type reaches an attributed typedef, no clang layout (#175)

    [<Literal>]
    let TypedefNamingAttribute = "FS9128" // typedef naming a record carries an attribute its layout dump omits (#175)

    [<Literal>]
    let UnnamedBitFieldUnmeasured = "FS9129" // unnamed bit-field in a record without a clang layout (#176)

    [<Literal>]
    let ProtocolEnumValueMalformed = "FS9130" // protocol enum entry value no (negated) decimal, hex or octal C constant in int64 (#160)

    // =========================================================================
    // Structured diagnostics (#184)
    // =========================================================================

    /// How a diagnostic stops generation. Every FS91xx refusal is an `Error`.
    [<RequireQualifiedAccess>]
    type Severity =
        | Error
        | Warning

    /// A fact a diagnostic carries, or the exact reason it is unknown. A location is never
    /// invented: where clang (or the reader) gives none, the reason says so.
    [<RequireQualifiedAccess>]
    type Located<'T> =
        | Known of 'T
        | Unknown of reason: string

    /// A position in a source file as clang gives it: 1-based line and byte column.
    type SourcePosition = { Line: int; Column: int }

    /// Where a refusal's cause is declared: the header (or protocol file) path, and the
    /// position in it when one is known.
    type SourceLocation = { Path: string; Position: Located<SourcePosition> }

    /// The pilot input that caused a refusal: the `.pilot.toml` file and the TOML key (dotted,
    /// `library.defines`) whose value the refusal names.
    type PilotLocation = { PilotFile: string; Key: string }

    /// One structured refusal (#184).
    type Diagnostic = {
        /// The FS91xx code.
        Code: string
        /// The refusal's text after its code; `render` gives the text Farscape prints.
        Message: string
        Severity: Severity
        /// Where the declaration that caused the refusal is written, or why that is unknown.
        Source: Located<SourceLocation>
        /// Further declarations the refusal names, in the order it names them (a record or enum
        /// declared differently by several headers names each of them).
        Related: SourceLocation list
        /// The pilot file and key, when pilot input caused the refusal; None otherwise.
        Pilot: PilotLocation option
    }

    /// The text Farscape prints for a diagnostic: `CODE: message`.
    let render (d: Diagnostic) = d.Code + ": " + d.Message

    /// An FS91xx refusal with the given source and no pilot location.
    let diagnostic (code: string) (message: string) (source: Located<SourceLocation>) : Diagnostic =
        { Code = code; Message = message; Severity = Severity.Error; Source = source; Related = []; Pilot = None }

    /// A source that is known only by its file: `reason` says why no position is known.
    let inFile (path: string) (reason: string) : Located<SourceLocation> =
        Located.Known { Path = path; Position = Located.Unknown reason }

    /// The diagnostic with the given pilot cause.
    let withPilot (pilot: PilotLocation) (d: Diagnostic) = { d with Pilot = Some pilot }

    /// The diagnostic with further named declarations.
    let withRelated (related: SourceLocation list) (d: Diagnostic) = { d with Related = related }

    /// The diagnostics of one failure and the exact text Farscape prints for it. A failure
    /// that is no FS91xx refusal carries no diagnostics, only its text.
    type Refusal = { Diagnostics: Diagnostic list; Text: string }

    /// One diagnostic's refusal: its text is `render` of it.
    let refusalOf (d: Diagnostic) : Refusal = { Diagnostics = [ d ]; Text = render d }

    /// Several diagnostics' refusal, as the pilot checks report them: each rendered, joined by
    /// spaces, followed by `suffix`.
    let refusalsOf (ds: Diagnostic list) (suffix: string) : Refusal =
        { Diagnostics = ds; Text = String.concat " " (ds |> List.map render) + suffix }

    /// A failure that is no FS91xx refusal: its text only.
    let uncoded (text: string) : Refusal = { Diagnostics = []; Text = text }

    /// The refusal with `prefix` before its text, as an enclosing step reports it.
    let prefixed (prefix: string) (r: Refusal) = { r with Text = prefix + r.Text }

    /// The refusal with `suffix` after its text.
    let suffixed (suffix: string) (r: Refusal) = { r with Text = r.Text + suffix }

    /// The diagnostics of several refusals, with their texts joined by `separator`.
    let joined (separator: string) (rs: Refusal list) : Refusal =
        { Diagnostics = rs |> List.collect _.Diagnostics; Text = rs |> List.map _.Text |> String.concat separator }

    /// Each diagnostic of the refusal mapped by `f`; the text is unchanged.
    let mapDiagnostics (f: Diagnostic -> Diagnostic) (r: Refusal) = { r with Diagnostics = r.Diagnostics |> List.map f }

    /// A refusal raised from inside generation; its message is the refusal's text.
    type RefusalException(refusal: Refusal) =
        inherit System.Exception(refusal.Text)
        member _.Refusal = refusal

    /// Raise a refusal.
    let raiseRefusal (r: Refusal) : 'T = raise (RefusalException r)

    /// Raise one diagnostic's refusal.
    let refuse (d: Diagnostic) : 'T = raiseRefusal (refusalOf d)

    /// Where a declaration is written, as clang (or the reader that produced it) gives it. It
    /// is the declaration's provenance, not part of its shape: two declarations that differ only
    /// in where they are written compare equal, so every check that compares declarations (the
    /// divergence checks, the merge) compares what they declare, as before locations were kept.
    [<CustomEquality; CustomComparison>]
    type Provenance =
        | Provenance of Located<SourceLocation>
        member this.Location = match this with Provenance location -> location
        override _.Equals(other: obj) = other :? Provenance
        override _.GetHashCode() = 0
        interface System.IComparable with
            member _.CompareTo(other: obj) =
                match other with
                | :? Provenance -> 0
                | _ -> invalidArg "other" "a Provenance compares only with a Provenance"

    /// The provenance of a declaration no reader located; `reason` says why.
    let unlocated (reason: string) = Provenance (Located.Unknown reason)
