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
///
/// Each refusal reason has exactly one code and each code one reason.
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
