namespace Farscape.Core

open Types
open XParsec
open XParsec.Parsers
open XParsec.Combinators
open XParsec.CharParsers

/// C scalar types to the one Clef kind per family, with the ABI representation kept beside the
/// spelling as data.
///
/// Ruling (clef docs/fidelity/phg/Dimensional_Range_Design.md, "Rulings for CS-12", the Farscape
/// leg): every C integer becomes `int` (signed) or `uint` (unsigned) in the Clef signature and the
/// width is never in the type; floats become `float` likewise. The C type's ABI representation, its
/// family and bits under the target profile, is the declaration the binding descriptor carries in
/// its `ParameterInfo`, read by the compiler's C ABI boundary reader (design §4.1). docs/14 §2's
/// request for a fixed-width ABI map beside a register-width map dissolves: there is one table, and
/// its width column is descriptor data, not a Clef type.
module TypeMapper =

    /// The family of an ABI representation.
    type Family =
        | Signed
        | Unsigned
        | Float
        | Pointer
        | Bool
        | Void

    /// The provenance stratum of a width fact (Farscape docs/14 §8): measured outranks declared
    /// outranks inferred. A lower stratum refines where a higher is silent and never contradicts it.
    type Stratum =
        | Measured
        | Declared
        | Inferred

    /// The ABI representation of one C scalar under a profile: family, width in bits, the stratum
    /// of the width fact, and the C type the fact was read from.
    type AbiRepr = {
        Family: Family
        Bits: int
        Stratum: Stratum
        CType: string
    }

    /// How a table row's width is fixed: by the C standard, or by the profile.
    type private Width =
        | Fixed of int
        | IntWidth
        | LongWidth
        | PointerWidth
        | WcharWidth

    /// The profile's `int` width in bits.
    let intWidth = function
        | IP16 -> 16
        | LP64 | LLP64 | ILP32 -> 32

    /// The profile's `long` width in bits.
    let longWidth = function
        | LP64 -> 64
        | LLP64 | ILP32 | IP16 -> 32

    /// The profile's pointer width in bits.
    let pointerWidth = function
        | LP64 | LLP64 -> 64
        | ILP32 -> 32
        | IP16 -> 16

    let private bitsOf (model: PlatformABI) = function
        | Fixed n -> n
        | IntWidth -> intWidth model
        | LongWidth -> longWidth model
        | PointerWidth -> pointerWidth model
        | WcharWidth -> (match model with LLP64 -> 16 | _ -> 32)

    /// One row per C scalar spelling. The width stratum is Declared when the C standard, POSIX, or
    /// the profile fixes it; Inferred for `long double`, whose width is the x87 extended format on
    /// the profiles this generator targets and is not fixed by the profile alone.
    let private scalarRows : (string * Family * Width * Stratum) list = [
        "void", Void, Fixed 0, Declared
        "bool", Bool, Fixed 8, Declared
        "_Bool", Bool, Fixed 8, Declared
        "char", Signed, Fixed 8, Declared
        "signed char", Signed, Fixed 8, Declared
        "unsigned char", Unsigned, Fixed 8, Declared
        "short", Signed, Fixed 16, Declared
        "short int", Signed, Fixed 16, Declared
        "unsigned short", Unsigned, Fixed 16, Declared
        "unsigned short int", Unsigned, Fixed 16, Declared
        "int", Signed, IntWidth, Declared
        "signed", Signed, IntWidth, Declared
        "signed int", Signed, IntWidth, Declared
        "unsigned", Unsigned, IntWidth, Declared
        "unsigned int", Unsigned, IntWidth, Declared
        "long", Signed, LongWidth, Declared
        "long int", Signed, LongWidth, Declared
        "unsigned long", Unsigned, LongWidth, Declared
        "unsigned long int", Unsigned, LongWidth, Declared
        "long long", Signed, Fixed 64, Declared
        "long long int", Signed, Fixed 64, Declared
        "unsigned long long", Unsigned, Fixed 64, Declared
        "unsigned long long int", Unsigned, Fixed 64, Declared
        "float", Float, Fixed 32, Declared
        "double", Float, Fixed 64, Declared
        "long double", Float, Fixed 80, Inferred
        // Fixed-width types, the same on every profile
        "int8_t", Signed, Fixed 8, Declared
        "uint8_t", Unsigned, Fixed 8, Declared
        "int16_t", Signed, Fixed 16, Declared
        "uint16_t", Unsigned, Fixed 16, Declared
        "int32_t", Signed, Fixed 32, Declared
        "uint32_t", Unsigned, Fixed 32, Declared
        "int64_t", Signed, Fixed 64, Declared
        "uint64_t", Unsigned, Fixed 64, Declared
        "__int8_t", Signed, Fixed 8, Declared
        "__uint8_t", Unsigned, Fixed 8, Declared
        "__int16_t", Signed, Fixed 16, Declared
        "__uint16_t", Unsigned, Fixed 16, Declared
        "__int32_t", Signed, Fixed 32, Declared
        "__uint32_t", Unsigned, Fixed 32, Declared
        "__int64_t", Signed, Fixed 64, Declared
        "__uint64_t", Unsigned, Fixed 64, Declared
        // Pointer-width integers: integers, not addresses; the profile's Pointer width
        "size_t", Unsigned, PointerWidth, Declared
        "ssize_t", Signed, PointerWidth, Declared
        "ptrdiff_t", Signed, PointerWidth, Declared
        "intptr_t", Signed, PointerWidth, Declared
        "uintptr_t", Unsigned, PointerWidth, Declared
        "__ssize_t", Signed, PointerWidth, Declared
        "__intptr_t", Signed, PointerWidth, Declared
        // POSIX types, as glibc declares them
        "off_t", Signed, LongWidth, Declared
        "pid_t", Signed, Fixed 32, Declared
        "uid_t", Unsigned, Fixed 32, Declared
        "gid_t", Unsigned, Fixed 32, Declared
        "mode_t", Unsigned, Fixed 32, Declared
        "dev_t", Unsigned, Fixed 64, Declared
        "ino_t", Unsigned, LongWidth, Declared
        "nlink_t", Unsigned, LongWidth, Declared
        "blksize_t", Signed, LongWidth, Declared
        "blkcnt_t", Signed, LongWidth, Declared
        "time_t", Signed, LongWidth, Declared
        "clockid_t", Signed, Fixed 32, Declared
        "suseconds_t", Signed, LongWidth, Declared
        "useconds_t", Unsigned, Fixed 32, Declared
        "socklen_t", Unsigned, Fixed 32, Declared
        "__off_t", Signed, LongWidth, Declared
        "__off64_t", Signed, Fixed 64, Declared
        "__pid_t", Signed, Fixed 32, Declared
        "__uid_t", Unsigned, Fixed 32, Declared
        "__gid_t", Unsigned, Fixed 32, Declared
        "__mode_t", Unsigned, Fixed 32, Declared
        "__dev_t", Unsigned, Fixed 64, Declared
        "__ino_t", Unsigned, LongWidth, Declared
        "__ino64_t", Unsigned, Fixed 64, Declared
        "__nlink_t", Unsigned, LongWidth, Declared
        "__blksize_t", Signed, LongWidth, Declared
        "__blkcnt_t", Signed, LongWidth, Declared
        "__blkcnt64_t", Signed, Fixed 64, Declared
        "__time_t", Signed, LongWidth, Declared
        "__clockid_t", Signed, Fixed 32, Declared
        "__suseconds_t", Signed, LongWidth, Declared
        "__useconds_t", Unsigned, Fixed 32, Declared
        "__socklen_t", Unsigned, Fixed 32, Declared
        // Character types: integers at the C ABI
        "wchar_t", Signed, WcharWidth, Declared
        "char16_t", Unsigned, Fixed 16, Declared
        "char32_t", Unsigned, Fixed 32, Declared
    ]

    let private scalarTable : Map<string, Family * Width * Stratum> =
        scalarRows |> List.map (fun (n, f, w, s) -> n, (f, w, s)) |> Map.ofList

    /// Opaque pointer typedefs the C library spells as scalars: an address, never an integer.
    let private opaquePointerNames = set [ "locale_t"; "__locale_t" ]

    /// The one Clef spelling of a family (design §2: signedness and width are not kinds).
    let clefSpelling (family: Family) : string =
        match family with
        | Signed -> "int"
        | Unsigned -> "uint"
        | Float -> "float"
        | Bool -> "bool"
        | Void -> "unit"
        | Pointer -> "CHandle<unit>"

    /// The ABI representation of a machine pointer under the profile: declared by the profile.
    let pointerRepr (model: PlatformABI) (cType: string) : AbiRepr =
        { Family = Pointer; Bits = pointerWidth model; Stratum = Declared; CType = cType }

    let isPointerType (typeName: string) =
        typeName.Contains("*") || typeName.EndsWith("&")

    let isConstType (typeName: string) =
        typeName.StartsWith("const ") || typeName.Contains(" const")

    let isArrayType (typeName: string) =
        typeName.Contains("[") && typeName.Contains("]")

    let getArrayLength (typeName: string) =
        let pArrayLen =
            skipMany (satisfyL (fun c -> c <> '[') "non-bracket")
            >>. (skipChar '[' >>. pint32 .>> skipChar ']')
        let reader = Reader.ofString typeName ()
        match pArrayLen reader with
        | Ok result -> Some result.Parsed
        | Error _ -> None

    let cleanTypeName (typeName: string) =
        typeName
            .Replace("const ", "")
            .Replace(" const", "")
            .Replace("volatile ", "")
            .Replace(" volatile", "")
            .Replace("__restrict", "")
            .Replace("restrict", "")
            .Replace("__extension__ ", "")
            .Replace("&", "")
            .Replace("*", "")
            .Replace("struct ", "")
            .Replace("class ", "")
            .Replace("enum ", "")
            .Replace("union ", "")
            .Trim()

    /// The ABI representation of a C scalar spelling under the profile, when the table has it.
    /// A pointer typedef the library spells as a scalar (`locale_t`) is a Pointer.
    let tryScalar (model: PlatformABI) (cType: string) : AbiRepr option =
        let cleaned = cleanTypeName cType
        match Map.tryFind cleaned scalarTable with
        | Some (family, width, stratum) ->
            Some { Family = family; Bits = bitsOf model width; Stratum = stratum; CType = cleaned }
        | None when opaquePointerNames.Contains cleaned -> Some (pointerRepr model cleaned)
        | None -> None

    /// True when the spelling is a C scalar the table knows (independent of the profile).
    let isPrimitiveType (typeName: string) =
        (tryScalar LP64 typeName).IsSome
