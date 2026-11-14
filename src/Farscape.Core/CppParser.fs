namespace Farscape.Core

open System
open System.Diagnostics
open System.IO
open FSharp.Data
open XParsec
open XParsec.Parsers
open XParsec.Combinators
open XParsec.CharParsers

/// C/C++ header parsing using native clang tooling.
///
/// This module invokes clang directly for parsing:
/// 1. `clang -Xclang -ast-dump=json` for structs, enums, typedefs, functions
/// 2. `clang -E -dM` for preprocessor macro definitions
/// NonNullAttr positions are recovered from clang's text AST when present: its JSON
/// AST omits them, including positions supplied through native header macros.
///
/// This approach works with any installed clang version without libclang
/// version compatibility issues.
module CppParser =

    // =========================================================================
    // Declaration Types
    // =========================================================================

    /// Raw attribute data extracted from clang AST nodes.
    /// Stored as-is from clang JSON; semantic interpretation belongs to consumers.
    type AttributeData = {
        /// Clang AST node kind, e.g., "AllocSizeAttr", "NonNullAttr", "FormatAttr"
        Kind: string
        /// Integer arguments. NonNullAttr parameter positions are normalized to zero-based indices.
        Args: int list
        /// String argument (archetype for FormatAttr, label for AsmLabelAttr)
        StringArg: string option
    }

    /// Represents a struct field with full type information
    type FieldDecl = {
        Name: string
        Type: string
        IsVolatile: bool      // __IO, volatile
        IsConst: bool         // __I, const
        IsArray: bool         // Array, including an unknown extent (e.g., payload[])
        ArraySize: int option // Declared element count when known
        IsBitfield: bool      // clang JSON "isBitfield"
        BitWidth: int option  // Bit width if IsBitfield
        Attributes: AttributeData list
    }

    /// Clang's measurement of one type in a translation unit, in bytes: `sizeof` and the
    /// alignment `_Alignof` (`alignof` in C++) gives.
    type TypeExtent = {
        Size: int
        Alignment: int
    }

    /// A by-value parameter or result whose type reaches a typedef carrying an attribute
    /// (`aligned(16)`, `may_alias`, `mode`), measured by clang in the declaring unit (#181). A
    /// descriptor claims the type the typedefs stand for, `Unattributed`, and has no slot for a
    /// typedef's attribute, so the claim is faithful only when clang measures the spelled type
    /// and `Unattributed` alike.
    type ByValueTypedef = {
        /// `parameter 'v'` or `result`, as a refusal names it.
        Site: string
        /// The type as the declaration spells it.
        Spelling: string
        /// Each typedef the spelling reaches that carries an attribute, with the attribute
        /// (`typedefAttributesOf`).
        Reached: (string * AttributeData) list
        /// The spelling with each typedef that carries or reaches an attribute replaced by its
        /// underlying type (`unattributedSpelling`): the type its descriptor claims.
        Unattributed: string
        /// Clang's measurement of `Spelling` and of `Unattributed` in the declaring unit, or the
        /// reason clang gave none.
        Measured: Result<TypeExtent * TypeExtent, string>
    }

    /// Represents a C/C++ function declaration
    type FunctionDecl = {
        Name: string
        ReturnType: string
        Parameters: (string * string) list
        Documentation: string option
        IsVirtual: bool
        IsStatic: bool
        IsInline: bool
        /// Raw clang AST attributes (AllocSizeAttr, NonNullAttr, etc.)
        Attributes: AttributeData list
        /// Itanium-mangled symbol name from clang AST (e.g., "_ZN3xrt6deviceC1Ej").
        /// Present for C++ methods, constructors, and destructors; None for C functions
        /// (which use unmangled names directly).
        MangledName: string option
        /// Where clang locates the declaration (#184); not part of its shape.
        Location: Diagnostics.Provenance
        /// Its by-value parameters and result whose types reach an attributed typedef, each with
        /// clang's measurement in the declaring unit (#181). Empty when none does.
        ByValueTypedefs: ByValueTypedef list
    }

    // =========================================================================
    // Struct Layout Information (from clang -fdump-record-layouts-simple)
    // =========================================================================

    /// Layout information for a struct, extracted from clang's record layout dump.
    /// All sizes are in bits as reported by clang; byte conversion happens downstream.
    type StructLayoutInfo = {
        /// Struct name (e.g., "drm_mode_create_dumb")
        Name: string
        /// Total size in bits
        SizeBits: int
        /// Data size in bits (excludes padding at end)
        DataSizeBits: int
        /// Required alignment in bits
        AlignmentBits: int
        /// Per-member offsets in bits, in clang's member order: one for each field and one for
        /// each unnamed bit-field (`StructDecl.UnnamedBitFields`), which clang dumps in place.
        FieldOffsetsBits: int list
    }

    /// Where a record C leaves unnamed comes from: an unnamed struct or union declared as the
    /// type of a named field (glibc's `__atomic_wide_counter.__value32`), or a C11 anonymous
    /// member, an unnamed struct or union with no field name whose members belong to the
    /// enclosing record (#173). Farscape names it `<Parent>_<Field>` (`anonymousRecordName`);
    /// an anonymous member's field is named `anonymous<n>` (`anonymousMemberName`). Neither is
    /// flattened into its parent's fields: the parent holds the record as one field, and its
    /// layout module also gives each member reached through an anonymous member its offset.
    type AnonymousRecordOrigin = {
        /// The enclosing record's name: its C name, or the generated name of an enclosing
        /// anonymous record.
        Parent: string
        /// The named field whose type the unnamed record is, or the generated field name of an
        /// anonymous member.
        Field: string
        /// Clang's location of the unnamed record, `path:line:col`, as it spells the type
        /// `struct (unnamed at path:line:col)`, or `struct P::(anonymous at path:line:col)` for
        /// an anonymous member.
        Location: string
        /// True for a C11 anonymous member: C gives the field no name, and its members are
        /// reached as members of the parent.
        Member: bool
    }

    /// Clang's evidence of a record's layout without its layout-neutral attributes (#174): its
    /// measurement of a copy of the record the parse synthesized in the same header context,
    /// or the exact reason no faithful copy could be synthesized.
    type UnattributedLayout =
        /// Clang's record layout of the copy: the same fields in the same order, each declared
        /// `__typeof__` the original member (so typedefs and their attributes are kept), with
        /// only the layout-neutral attributes left out.
        | UnattributedMeasured of StructLayoutInfo
        /// Why no faithful copy exists or clang measured none.
        | UnattributedUnsynthesized of reason: string

    /// An unnamed bit-field of a record (#176): `int : 3;` pads the record, and the zero-width
    /// `int : 0;` moves the next member to the next boundary of its type. C names it with no
    /// member, so it is no field of `StructDecl.Fields`, but it moves bytes of the layout and is
    /// never dropped: clang dumps an offset for it among the record's member offsets, a record
    /// carrying one is measured by clang in its declaring unit, and its layout is never computed
    /// from the declared fields.
    type UnnamedBitField = {
        /// Its position among the record's members as clang declares them, counted from 0:
        /// named fields, anonymous members and unnamed bit-fields alike, which is the index of its
        /// offset in clang's dumped member offsets.
        Position: int
        /// Clang's type of it.
        Type: string
        /// Its declared width in bits, when clang gives it as a constant.
        Width: int option
    }

    /// Where a typedef attribute reaches a record (#175).
    type TypedefAttributeSite =
        /// The type of field `field` is the typedef, or reaches it through typedefs, qualifiers
        /// and array extents (not through a pointer, whose own layout the typedef cannot move).
        | FieldType of field: string
        /// The typedef names the record itself (`typedef struct { ... } T __attribute__((...))`),
        /// so the type its name spells is the typedef, whose layout clang's record layout dump of
        /// the record does not measure.
        | RecordName

    /// An attribute clang attaches to a typedef a record reaches, in the record's declaring unit.
    type TypedefAttribute = {
        Site: TypedefAttributeSite
        /// The typedef carrying the attribute.
        Typedef: string
        Attribute: AttributeData
    }

    /// Represents a C/C++ struct declaration
    type StructDecl = {
        Name: string
        Fields: FieldDecl list
        Documentation: string option
        IsUnion: bool
        Attributes: AttributeData list
        /// Set when C leaves the record unnamed and Farscape named it from its parent and field.
        Anonymous: AnonymousRecordOrigin option
        /// Clang's record layout of this record in its declaring unit, when the parse measured
        /// it: every anonymous record, every record enclosing one, and every record carrying a
        /// layout-neutral attribute is measured (`measureRecordLayouts`). None for records the
        /// parse did not measure.
        Measured: StructLayoutInfo option
        /// For a record carrying a layout-neutral attribute, clang's layout of its copy without
        /// that attribute (`measureRecordLayouts`). None for every other record.
        Unattributed: UnattributedLayout option
        /// The record's unnamed bit-fields, in member order (#176).
        UnnamedBitFields: UnnamedBitField list
        /// The attributes of the typedefs its fields' types reach, and of a typedef naming it, in
        /// its declaring unit (#175, `typedefAttributesOf`).
        TypedefAttributes: TypedefAttribute list
        /// Where clang locates the record (#184): its declaration, or for a record C leaves
        /// unnamed the location clang spells for it. Not part of its shape.
        Location: Diagnostics.Provenance
    }

    /// A record clang defines: it declares members, named fields or only unnamed bit-fields
    /// (`struct pad { int : 8; }`, #183), and clang lays it out. A record with neither is a
    /// forward declaration, which has no layout.
    let isDefinedRecord (s: StructDecl) = not s.Fields.IsEmpty || not s.UnnamedBitFields.IsEmpty

    /// Clang's member offsets of a record without those of its unnamed bit-fields. Offsets
    /// clang dumps for a different number of members are returned as dumped, so the count check
    /// refuses them.
    let private namedMemberOffsetsBits (fieldCount: int) (padding: UnnamedBitField list) (layout: StructLayoutInfo) : int list =
        if padding.IsEmpty || layout.FieldOffsetsBits.Length <> fieldCount + padding.Length then
            layout.FieldOffsetsBits
        else
            let positions = padding |> List.map _.Position |> Set.ofList
            layout.FieldOffsetsBits |> List.indexed |> List.filter (fun (position, _) -> not (positions.Contains position)) |> List.map snd

    /// A measured layout's offsets of the record's fields: clang's member offsets without those
    /// of its unnamed bit-fields. Offsets clang dumps for a different number of members are
    /// returned as dumped, so the count check refuses them.
    let fieldOffsetsBits (s: StructDecl) (layout: StructLayoutInfo) : int list =
        namedMemberOffsetsBits s.Fields.Length s.UnnamedBitFields layout

    /// How a refusal names an unnamed bit-field: its member position, counted from 1, and its
    /// declaration.
    let describeUnnamedBitField (b: UnnamedBitField) =
        let width = match b.Width with Some w -> string w | None -> "?"
        $"member {b.Position + 1}, the unnamed bit-field '{b.Type} : {width}'"

    /// The name Farscape gives an unnamed record that is the type of a named field: the
    /// enclosing record's name and the field's name, joined by `_`.
    let anonymousRecordName (parent: string) (field: string) = parent + "_" + field

    /// The field name Farscape gives the `ordinal`-th C11 anonymous member of a record,
    /// counted from 1 in declaration order (#173).
    let anonymousMemberName (ordinal: int) = $"anonymous{ordinal}"

    /// How a refusal names an anonymous record's origin: its field, or its anonymous member.
    let describeOrigin (origin: AnonymousRecordOrigin) =
        if origin.Member then $"anonymous member '{origin.Field}' of '{origin.Parent}'"
        else $"field '{origin.Field}' of '{origin.Parent}'"

    /// A unit whose generated anonymous record names collide (FS9114); carries the refusal.


    /// A unit with an anonymous member Farscape cannot represent (FS9122) or whose generated
    /// member name names another member (FS9123); carries the refusal.


    /// Represents a C/C++ enum value with signed support
    type EnumValue = {
        Name: string
        Value: int64  // Signed to support negative IRQ numbers
        Documentation: string option
    }

    /// The representation clang chose for an enum type in its declaring translation unit:
    /// `sizeof` in bits and whether `(E)-1 < 0`, both evaluated by clang (C leaves an ordinary
    /// enum's compatible integer type to the implementation; `packed` and wide enumerators
    /// change it).
    type EnumRepresentation = {
        Bits: int
        IsSigned: bool
    }

    /// Where an enum's representation comes from. C and C++ enums are measured by clang in
    /// their declaring unit or have none; a protocol enum carries its protocol's declared wire
    /// type. The two provenances never substitute for each other, and none is inferred.
    type EnumRepresentationSource =
        /// A C/C++ enum clang measured in its declaring unit.
        | ClangMeasured of EnumRepresentation
        /// A C/C++ enum clang did not measure (incomplete, or not nameable by the probe).
        | ClangUnmeasured
        /// A protocol enum whose referencing arguments all declare one wire type; `evidence`
        /// names the protocol, the referencing arguments and their declared type.
        | ProtocolWire of EnumRepresentation * evidence: string
        /// A protocol enum no argument fixes to one declared wire type; `reason` says why.
        | ProtocolUndeclared of reason: string

    /// Represents a C/C++ enum declaration
    type EnumDecl = {
        Name: string
        Values: EnumValue list
        Documentation: string option
        UnderlyingType: string option  // e.g., "int", "uint32_t"
        /// The representation's provenance: clang's measurement in the declaring unit, or the
        /// protocol's declared wire type.
        Representation: EnumRepresentationSource
        /// Where clang (or the protocol reader) locates the enum (#184); not part of its shape.
        Location: Diagnostics.Provenance
    }

    /// Represents a C/C++ typedef declaration
    type TypedefInfo = {
        Name: string
        UnderlyingType: string
        Documentation: string option
        /// The attributes clang attaches to the typedef (`AlignedAttr`, `ModeAttr`, ...), each
        /// classified through `AttributeContracts` where a record reaches it (#175).
        Attributes: AttributeData list
    }

    /// Represents a C preprocessor macro
    /// What a macro's definition is. The first four come from the definition's text; the last
    /// four from clang's evaluation of an object macro in its own unit (`evaluateObjectMacros`,
    /// #193), so a composed constant carries the value clang computes and every other kind
    /// says what clang found instead of an integer.
    type MacroKind =
        | SimpleValue of string           // #define FOO 42
        | Expression of string            // #define FOO (BAR + 1)
        | FunctionLike of string list * string  // #define FOO(x,y) (x+y)
        | TypeCast of string * string     // #define GPIOA ((GPIO_TypeDef*)0x...)
        /// An integer constant expression and the value clang evaluates for it
        /// (`(SQLITE_IOERR | (1<<8))` is 266; an alias of another macro is that macro's value).
        | IntegerConstant of expression: string * value: int64
        /// A cast of an integer to a function-pointer type: no entry, but a sentinel the library
        /// gives a meaning (#192). `pointerType` is the type clang gives the expression (its
        /// typedef name when it has one), `canonical` clang's canonical spelling of it, and
        /// `value` the integer clang evaluates for its address (SQLITE_TRANSIENT is -1).
        | FunctionPointerSentinel of expression: string * pointerType: string * canonical: string * value: int64
        /// A string literal, with the array type clang gives it.
        | StringConstant of expression: string * clangType: string
        /// An object macro clang evaluates to no integer, sentinel or string: `reason` is clang's
        /// diagnostic for it, or why clang gave none.
        | Unevaluated of expression: string * reason: string

    type MacroDecl = {
        Name: string
        Kind: MacroKind
        RawValue: string
        /// Documentation comment from raw header text (e.g., /* Operation not permitted */)
        Documentation: string option
    }

    /// Delegate (function pointer type with a name). Used for Wayland event handlers
    /// and any other callback type parsed from structured API definitions.
    type DelegateDecl = {
        Name: string
        Parameters: (string * string) list
        ReturnType: string
        Documentation: string option
    }

    /// Union type for all supported declarations
    type Declaration =
        | Function of FunctionDecl
        | Struct of StructDecl
        | Enum of EnumDecl
        | Typedef of TypedefInfo
        | Macro of MacroDecl
        | Namespace of NamespaceDecl
        | Class of ClassDecl
        | Delegate of DelegateDecl

    and NamespaceDecl = {
        Name: string
        Declarations: Declaration list
    }

    and ClassDecl = {
        Name: string
        Methods: Declaration list
        /// CXXConstructorDecl nodes parsed as FunctionDecl (name = class name).
        /// Includes both user-defined and implicit constructors.
        Constructors: Declaration list
        Fields: FieldDecl list
        Documentation: string option
        IsAbstract: bool
        /// True when a non-implicit (user-defined or explicitly-defaulted) destructor exists.
        /// Determines triviality: classes with user destructors are non-trivially copyable
        /// and use MEMORY return classification (sret) under SysV x86_64 ABI.
        HasUserDestructor: bool
        /// Itanium-mangled symbol for the D1 (complete object) destructor, if present.
        /// Extracted from clang AST "mangledName" field on CXXDestructorDecl.
        DestructorMangledName: string option
        /// True when a non-implicit copy constructor exists (user-defined or explicitly-defaulted).
        HasUserCopyConstructor: bool
        /// True when a non-implicit move constructor exists (user-defined or explicitly-defaulted).
        HasUserMoveConstructor: bool
        /// Base class type strings from clang AST "bases" array.
        /// Used to detect inherited pimpl pattern (e.g., "detail::pimpl<xclbin_impl>").
        BaseClasses: string list
        /// The class's unnamed bit-fields, in member order (#182, as `StructDecl` for #176).
        UnnamedBitFields: UnnamedBitField list
        /// The attributes of the typedefs its fields' types reach in its declaring unit (#182,
        /// as `StructDecl` for #175). Clang lays out a base subobject by its record, not by a
        /// typedef naming it, so a base reaches no site.
        TypedefAttributes: TypedefAttribute list
        /// Clang's record layout of the class in its declaring unit, when the parse measured it:
        /// every class carrying an unnamed bit-field or whose field type reaches an attributed
        /// typedef (`measureRecordLayouts`). Its field offsets are its own members'; base
        /// subobjects lie inside its size. None for classes the parse did not measure.
        Measured: StructLayoutInfo option
        /// The class's name qualified by its enclosing namespaces and records (`ns::G`), as a
        /// probe at file scope spells it and clang's layout dump names it. None for a class
        /// inside a template or function, which no probe at file scope names.
        QualifiedName: string option
        /// Where clang locates the class (#184); not part of its shape.
        Location: Diagnostics.Provenance
    }

    /// A measured C++ class's offsets of its fields: clang's member offsets without those of its
    /// unnamed bit-fields (#182).
    let classFieldOffsetsBits (c: ClassDecl) (layout: StructLayoutInfo) : int list =
        namedMemberOffsetsBits c.Fields.Length c.UnnamedBitFields layout

    // =========================================================================
    // Parser Options
    // =========================================================================

    /// Options for parsing a C/C++ header file
    type HeaderParserOptions = {
        /// Path to the header file to parse
        HeaderFile: string
        /// Additional include paths for resolving #include directives
        IncludePaths: string list
        /// System include paths (#179): searched after `IncludePaths`, and clang treats the
        /// headers it finds there as system headers. Each is `-isystem` then the path.
        SystemIncludePaths: string list
        /// Preprocessor definitions (e.g., ["DEBUG"; "STM32L552xx"])
        Defines: string list
        /// Enable verbose output
        Verbose: bool
        /// Include macros from preprocessor (can be slow for large headers)
        IncludeMacros: bool
        /// Filter to only include macros matching these prefixes (empty = all)
        MacroPrefixes: string list
        /// Root directory for library boundary scoping.
        /// When set, only declarations from files under this path are extracted.
        /// Derived automatically from pkg-config include paths.
        IncludeRoot: string option
        /// When true, invoke clang in C++ mode (-x c++ -std=c++17).
        /// Required for .h headers that contain C++ class declarations
        /// behind #ifdef __cplusplus guards.
        CppMode: bool
    }

    /// Detect whether a header file should be parsed in C++ mode.
    /// Returns true for .hpp/.hxx/.hh extensions, or for .h files whose
    /// content contains C++ indicators (namespace, class, template, std::).
    let detectCppMode (headerFile: string) : bool =
        let ext = Path.GetExtension(headerFile).ToLowerInvariant()
        match ext with
        | ".hpp" | ".hxx" | ".hh" -> true
        | ".h" ->
            try
                // Comments and literals can mention C++ without making a C header C++.
                // Replace them with whitespace so tokens on either side do not join.
                let content =
                    System.Text.RegularExpressions.Regex.Replace(
                        File.ReadAllText(headerFile),
                        @"//[^\r\n]*|/\*[\s\S]*?\*/|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'",
                        " ")
                content.Contains("namespace ") ||
                content.Contains("class ") ||
                content.Contains("template<") ||
                content.Contains("template <") ||
                content.Contains("std::") ||
                // C++ stdlib includes: <cXXX> wrappers or extensionless STL headers
                System.Text.RegularExpressions.Regex.IsMatch(
                    content, @"#\s*include\s*<c(errno|string|stdlib|stdio|math|locale|type)\b") ||
                System.Text.RegularExpressions.Regex.IsMatch(
                    content, @"#\s*include\s*<(vector|string|memory|map|set|list|array|optional|variant|tuple|algorithm|functional|iostream|fstream|sstream|chrono|thread|mutex|atomic|utility|numeric|deque|queue|stack|bitset|span|ranges|format|any|regex|filesystem|future|exception|typeinfo|type_traits|initializer_list|string_view|unordered_map|unordered_set)>")
            with _ -> false
        | _ -> false

    /// Create default parser options for a header file
    let defaultOptions headerFile = {
        HeaderFile = headerFile
        IncludePaths = []
        SystemIncludePaths = []
        Defines = []
        Verbose = false
        IncludeMacros = true
        MacroPrefixes = []
        IncludeRoot = None
        CppMode = detectCppMode headerFile
    }

    // =========================================================================
    // JSON AST Parsing Helpers
    // =========================================================================

    /// Get string property from JSON element
    let private getString (element: JsonValue) (prop: string) : string option =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.String s) -> Some s
        | _ -> None

    /// Get string property with default value
    let private getStringOr (element: JsonValue) (prop: string) (defaultVal: string) : string =
        getString element prop |> Option.defaultValue defaultVal

    /// Get integer property (signed)
    let private getInt64 (element: JsonValue) (prop: string) : int64 option =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Number d) -> Some (int64 d)
        | Some (JsonValue.String s) ->
            match System.Int64.TryParse(s) with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    /// Get boolean property
    let private getBool (element: JsonValue) (prop: string) : bool =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Boolean b) -> b
        | _ -> false

    /// Get array property
    let private getArray (element: JsonValue) (prop: string) : JsonValue seq =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Array elements) -> elements :> JsonValue seq
        | _ -> Seq.empty

    /// Recursively collect TextComment text from a FullComment AST node.
    /// FullComment → ParagraphComment → TextComment (with .text field).
    /// Multi-line comments produce multiple TextComment nodes; we join them.
    let rec private extractTextFromComment (node: JsonValue) : string list =
        let kind = getStringOr node "kind" ""
        if kind = "TextComment" then
            match getString node "text" with
            | Some t -> [t.Trim()]
            | None -> []
        else
            getArray node "inner"
            |> Seq.collect extractTextFromComment
            |> List.ofSeq

    /// Extract documentation string from a declaration's inner nodes.
    /// Looks for FullComment nodes (produced by -fparse-all-comments) and
    /// joins all TextComment text into a single documentation string.
    let private extractDocumentation (node: JsonValue) : string option =
        let texts =
            getArray node "inner"
            |> Seq.filter (fun inner -> getStringOr inner "kind" "" = "FullComment")
            |> Seq.collect extractTextFromComment
            |> Seq.filter (fun t -> t <> "")
            |> List.ofSeq
        match texts with
        | [] -> None
        | lines -> Some (String.concat " " lines)

    /// Get nested object property
    let private getObject (element: JsonValue) (prop: string) : JsonValue option =
        match element.TryGetProperty(prop) with
        | Some (JsonValue.Record _ as obj) -> Some obj
        | _ -> None

    /// Check if a declaration is from an included file (vs the main file)
    let private isFromIncludedFile (element: JsonValue) : bool =
        match getObject element "loc" with
        | Some loc ->
            match getObject loc "includedFrom" with
            | Some _ -> true
            | None -> false
        | None -> false

    /// Extract file from location info, tracking spillover from previous declarations
    /// Clang JSON only includes 'file' on the first decl from each file, subsequent ones just have line/offset
    let private getFileFromLoc (element: JsonValue) (lastKnownFile: string option) : string option =
        match getString element "file" with
        | Some f -> Some f
        | None ->
            match getObject element "begin" with
            | Some beginObj ->
                match getString beginObj "file" with
                | Some f -> Some f
                | None ->
                    // If loc has offset but no file, use lastKnownFile
                    match getInt64 element "offset" with
                    | Some _ -> lastKnownFile
                    | None -> lastKnownFile
            | None ->
                // If loc has offset but no file, use lastKnownFile
                match getInt64 element "offset" with
                | Some _ -> lastKnownFile
                | None -> lastKnownFile

    /// Extract type string from type object
    let private getQualType (element: JsonValue) : string =
        match getObject element "type" with
        | Some typeObj -> getStringOr typeObj "qualType" "unknown"
        | None -> "unknown"

    /// Extract return type from function type string (e.g., "void (int)" -> "void")
    let private extractReturnType (typeStr: string) : string =
        match typeStr.IndexOf('(') with
        | -1 -> typeStr.Trim()
        | idx -> typeStr.Substring(0, idx).Trim()

    /// Array decomposition admitted by field descriptors. Unsupported spellings stay intact
    /// until a consumer requests a representation; parsing a header does not grant one.
    type FieldDeclarator =
        | PlainFieldType of string
        | TrailingArrayType of elementType: string * count: int option
        | UnsupportedArrayType of string

    let classifyFieldDeclarator (typeStr: string) : FieldDeclarator =
        if not (typeStr.Contains('[')) then PlainFieldType typeStr
        elif typeStr.Contains('(') || typeStr.Contains(')') then UnsupportedArrayType typeStr
        else
            let pArray =
                many1Chars (satisfyL (fun c -> c <> '[' && c <> ']') "array element type")
                .>>. (skipChar '[' >>. opt pint32 .>> skipChar ']')
                .>> spaces .>> eof
            let reader = Reader.ofString typeStr ()
            match pArray reader with
            | Ok result ->
                let struct (element, count) = result.Parsed
                let element = element.Trim()
                if element = "" then UnsupportedArrayType typeStr
                else
                    let count = match count with ValueSome value -> Some value | ValueNone -> None
                    TrailingArrayType (element, count)
            | Error _ -> UnsupportedArrayType typeStr

    /// Parse field metadata using the same declarator classification as descriptor emission.
    let private parseFieldType (typeStr: string) : FieldDecl =
        let isVolatile = typeStr.Contains("volatile") || typeStr.Contains("__IO")
        let isConst = typeStr.Contains("const") || typeStr.Contains("__I")

        let declarator = classifyFieldDeclarator typeStr
        let isArray, arraySize, cleanType =
            match declarator with
            | TrailingArrayType (element, count) -> true, count, element
            | PlainFieldType spelling | UnsupportedArrayType spelling -> false, None, spelling

        // Preserve an unsupported declarator verbatim for a located consumer refusal.
        let cleanType =
            match declarator with
            | UnsupportedArrayType spelling -> spelling
            | _ ->
                cleanType.Replace("volatile ", "").Replace("const ", "")
                         .Replace("__IO ", "").Replace("__I ", "").Replace("__O ", "")
                         .Trim()

        {
            Name = ""
            Type = cleanType
            IsVolatile = isVolatile
            IsConst = isConst
            IsArray = isArray
            ArraySize = arraySize
            IsBitfield = false
            BitWidth = None
            Attributes = []
        }

    // =========================================================================
    // AST Node Processing
    // =========================================================================

    /// Extract raw attribute data from a declaration's inner nodes.
    /// Filters for child nodes with kind ending in "Attr" (excluding BuiltinAttr).
    let private extractAttributes (node: JsonValue) : AttributeData list =
        getArray node "inner"
        |> Seq.filter (fun inner ->
            let kind = getStringOr inner "kind" ""
            kind.EndsWith("Attr") && kind <> "BuiltinAttr")
        |> Seq.choose (fun attr ->
            let kind = getStringOr attr "kind" ""
            // Extract integer args from inner ConstantExpr/IntegerLiteral nodes
            let args =
                getArray attr "inner"
                |> Seq.choose (fun arg ->
                    // Try direct value, then look for nested integer literals
                    match getInt64 arg "value" with
                    | Some v -> Some (int v)
                    | None ->
                        getArray arg "inner"
                        |> Seq.tryPick (fun nested -> getInt64 nested "value" |> Option.map int))
                |> List.ofSeq
            // Extract string argument (archetype for FormatAttr, label for AsmLabelAttr)
            let stringArg =
                getString attr "archetype"
                |> Option.orElseWith (fun () -> getString attr "label")
            Some { Kind = kind; Args = args; StringArg = stringArg })
        |> List.ofSeq

    /// The name an unnamed C parameter takes: its position, as callback entry descriptors name
    /// their arguments (`arg0`, `arg1`, …) (#194).
    let positionalParameterName (index: int) = $"arg{index}"

    /// A function's parameters with each unnamed one named by its position (#194), so no binding
    /// or descriptor binds one name twice. A header name equal to an unnamed sibling's positional
    /// name is refused (FS9131): it is never renamed, since the header's name is its spelling.
    let nameParametersByPosition (functionName: string) (location: Diagnostics.Provenance)
                                 (parameters: (string option * string) list) : (string * string) list =
        let positional =
            parameters |> List.indexed
            |> List.choose (fun (index, (name, _)) -> if name.IsNone then Some (positionalParameterName index, index) else None)
            |> Map.ofList
        parameters |> List.mapi (fun index (name, cType) ->
            match name with
            | None -> positionalParameterName index, cType
            | Some name ->
                match Map.tryFind name positional with
                | Some unnamed ->
                    Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.ParameterNameCollision
                                            $"function '{functionName}' names parameter {index} '{name}', which is the positional name of its unnamed parameter {unnamed}; the binding and its descriptor would bind '{name}' twice."
                                            location.Location)
                | None -> name, cType)

    /// Process FunctionDecl AST node
    let private processFunctionDecl (location: Diagnostics.Provenance) (node: JsonValue) : FunctionDecl option =
        match getString node "name" with
        | None | Some "" -> None
        | Some name ->
            let parameters =
                getArray node "inner"
                |> Seq.filter (fun inner ->
                    getStringOr inner "kind" "" = "ParmVarDecl")
                |> Seq.map (fun param ->
                    getString param "name" |> Option.filter (fun n -> n <> ""), getQualType param)
                |> List.ofSeq
                |> nameParametersByPosition name location

            let qualType = getQualType node
            let returnType = extractReturnType qualType
            let isVirtual = getBool node "virtual" || getBool node "pure"
            let isStatic = getStringOr node "storageClass" "" = "static"
            let isInline = getBool node "inline"
            let baseAttributes = extractAttributes node
            let attributes =
                if qualType.Contains("noexcept") && not (baseAttributes |> List.exists (fun a -> a.Kind = "NoExceptAttr")) then
                    baseAttributes @ [{ Kind = "NoExceptAttr"; Args = []; StringArg = None }]
                else baseAttributes
            let documentation = extractDocumentation node
            let mangledName = getString node "mangledName"

            Some {
                Name = name
                ReturnType = returnType
                Parameters = parameters
                Documentation = documentation
                IsVirtual = isVirtual
                IsStatic = isStatic
                IsInline = isInline
                Attributes = attributes
                MangledName = mangledName
                Location = location
                ByValueTypedefs = []
            }

    /// The constant width clang gives a bit-field FieldDecl.
    let private bitWidthOf (node: JsonValue) : int option =
        node.TryGetProperty("inner")
        |> Option.bind (fun inner ->
            inner.AsArray()
            |> Array.tryPick (fun child ->
                match getString child "kind" with
                | Some "ConstantExpr" | Some "IntegerLiteral" ->
                    getString child "value" |> Option.bind (fun v ->
                        match System.Int32.TryParse(v) with
                        | true, n -> Some n
                        | _ -> None)
                | _ -> None))

    /// Process FieldDecl AST node
    let private processFieldDecl (node: JsonValue) : FieldDecl option =
        match getString node "name" with
        | None | Some "" -> None
        | Some fieldName ->
            let typeStr = getQualType node
            let fieldInfo = parseFieldType typeStr
            let isBitfield = getBool node "isBitfield"
            let bitWidth = if isBitfield then bitWidthOf node else None
            Some { fieldInfo with Name = fieldName; IsBitfield = isBitfield; BitWidth = bitWidth; Attributes = extractAttributes node }

    /// A record's unnamed bit-fields (#176): each FieldDecl clang declares with no name that is
    /// not the implicit field of an anonymous member, at its position among all the record's
    /// FieldDecls, which is the position of its offset in clang's record layout dump.
    let private unnamedBitFieldsOf (node: JsonValue) : UnnamedBitField list =
        getArray node "inner"
        |> Seq.filter (fun inner -> getStringOr inner "kind" "" = "FieldDecl")
        |> Seq.indexed
        |> Seq.choose (fun (position, field) ->
            if getStringOr field "name" "" = "" && not (getBool field "isImplicit") then
                Some { Position = position; Type = getQualType field; Width = bitWidthOf field }
            else None)
        |> List.ofSeq

    /// Process RecordDecl (struct/union) AST node
    let private processRecordDecl (location: Diagnostics.Provenance) (node: JsonValue) : StructDecl option =
        let name = getString node "name"
        let tagUsed = getStringOr node "tagUsed" "struct"
        let isUnion = tagUsed = "union"
        let documentation = extractDocumentation node
        let attributes = extractAttributes node

        let fields =
            getArray node "inner"
            |> Seq.filter (fun inner ->
                getStringOr inner "kind" "" = "FieldDecl")
            |> Seq.choose processFieldDecl
            |> List.ofSeq
        let padding = unnamedBitFieldsOf node
        let hasAnonymousMember =
            getArray node "inner"
            |> Seq.exists (fun inner ->
                getStringOr inner "kind" "" = "FieldDecl" && getBool inner "isImplicit" && getStringOr inner "name" "" = "")

        match name, fields with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Fields = fields; Documentation = documentation; IsUnion = isUnion; Attributes = attributes; Anonymous = None; Measured = None; Unattributed = None
                   UnnamedBitFields = padding; TypedefAttributes = []; Location = location }
        // An unnamed record of only unnamed bit-fields or an anonymous member is a defined record too (#183, #177).
        | _, fs when not fs.IsEmpty || not padding.IsEmpty || hasAnonymousMember ->
            Some { Name = ""; Fields = fields; Documentation = documentation; IsUnion = isUnion; Attributes = attributes; Anonymous = None; Measured = None; Unattributed = None
                   UnnamedBitFields = padding; TypedefAttributes = []; Location = location }
        | _ -> None

    /// Clang spells the type of a field declared with an unnamed record as
    /// `struct (unnamed at path:line:col)` (Clang 22+) or `struct (unnamed struct at path:line:col)`
    /// (Clang 18), or scoped as `Parent::(unnamed struct at path:line:col)` in C++, possibly
    /// cv-qualified or with a pointer/array declarator suffix; `loc` is the unnamed record's location.
    let private unnamedRecordSpelling =
        System.Text.RegularExpressions.Regex(@"^(?<pre>(?:const |volatile )*)(?:(?<tag>struct|union) (?:.+::)?\(unnamed(?: (?:struct|union))? at (?<loc>[^()]+)\)|(?:.+::)?\(unnamed (?<tag>struct|union) at (?<loc>[^()]+)\))(?<post>.*)$")

    /// Clang spells the type of a C11 or C++ anonymous member, the implicit unnamed field it
    /// declares, as `union P::(anonymous at path:line:col)` in C or `P::(anonymous union at
    /// path:line:col)` in C++ (#173, #177), scoped by every enclosing record (`P::(anonymous
    /// union)::`), possibly cv-qualified; `loc` is the anonymous record's own location.
    let private anonymousMemberSpelling =
        System.Text.RegularExpressions.Regex(@"^(?<pre>(?:const |volatile )*)(?:(?<tag>struct|union) (?:.+::)?\(anonymous(?: (?:struct|union))? at (?<loc>[^()]+)\)|(?:.+::)?\(anonymous (?<tag>struct|union) at (?<loc>[^()]+)\))$")

    /// A clang `path:line:col` location with its path made absolute against the working
    /// directory: clang spells one file `x.h`, `./x.h` or `././x.h` depending on how the
    /// translation unit reached it, and every clang Farscape runs shares this directory.
    let clangLocation (location: string) : (string * int * int) option =
        let parts = location.Split(':')
        if parts.Length < 3 then None
        else
            match Int32.TryParse parts.[parts.Length - 2], Int32.TryParse parts.[parts.Length - 1] with
            | (true, line), (true, col) ->
                let path = String.Join(":", parts.[0 .. parts.Length - 3])
                let full = try Path.GetFullPath path with _ -> path
                Some (full, line, col)
            | _ -> None

    /// A clang `path:line:col` location as a diagnostic source (#184).
    let clangSourceLocation (location: string) : Diagnostics.Located<Diagnostics.SourceLocation> =
        match clangLocation location with
        | Some (path, line, col) ->
            Diagnostics.Located.Known { Path = path; Position = Diagnostics.Located.Known { Line = line; Column = col } }
        | None -> Diagnostics.Located.Unknown $"clang spells no path:line:col location for it ('{location}')"

    /// Clang's location of a declaration node (#184), read from its `loc`: the file (clang
    /// writes it only when it changes, so `currentFile` carries it otherwise) and the byte offset,
    /// from which the line and column are counted in that file. It is kept only when the token
    /// clang gives at that offset is the declaration's own name (for a declaration C leaves
    /// unnamed, its tag keyword) and clang's column, where it writes one, agrees; otherwise the
    /// reason is kept and no location is invented. A declaration clang locates inside a macro
    /// expansion is not located.
    let private nodeProvenance (files: System.Collections.Generic.Dictionary<string, (byte[] * int[]) option>)
                               (currentFile: string option) (node: JsonValue) : Diagnostics.Provenance =
        let expected =
            match getStringOr node "name" "" with
            | "" -> if getStringOr node "kind" "" = "EnumDecl" then "enum" else getStringOr node "tagUsed" "struct"
            | name -> name
        match getObject node "loc" with
        | None -> Diagnostics.unlocated $"clang gives declaration '{expected}' no location"
        | Some loc when (getObject loc "spellingLoc").IsSome || (getObject loc "expansionLoc").IsSome ->
            Diagnostics.unlocated $"clang locates declaration '{expected}' inside a macro expansion"
        | Some loc ->
            match (match getString loc "file" with Some f -> Some f | None -> currentFile), getInt64 loc "offset", getInt64 loc "tokLen" with
            | Some file, Some offset, Some tokLen ->
                let path = try Path.GetFullPath file with _ -> file
                let text =
                    match files.TryGetValue path with
                    | true, cached -> cached
                    | _ ->
                        let read =
                            try
                                let bytes = File.ReadAllBytes path
                                let starts = [| yield 0; for i in 0 .. bytes.Length - 1 do if bytes.[i] = byte '\n' then yield i + 1 |]
                                Some (bytes, starts)
                            with _ -> None
                        files.[path] <- read
                        read
                match text with
                | None -> Diagnostics.unlocated $"clang locates declaration '{expected}' in '{path}', which cannot be read to confirm the location"
                | Some (bytes, starts) ->
                    let offset, tokLen = int offset, int tokLen
                    if offset < 0 || tokLen < 0 || offset + tokLen > bytes.Length then
                        Diagnostics.unlocated $"clang's offset {offset} for declaration '{expected}' lies outside '{path}'"
                    else
                        let token = System.Text.Encoding.UTF8.GetString(bytes, offset, tokLen)
                        // The last line start at or before the offset.
                        let index = match Array.BinarySearch(starts, offset) with i when i >= 0 -> i | i -> ~~~i - 1
                        let line, column = index + 1, offset - starts.[index] + 1
                        if token <> expected then
                            Diagnostics.unlocated $"clang's location for declaration '{expected}' in '{path}' (offset {offset}) spells '{token}'"
                        elif getInt64 loc "col" |> Option.exists (fun col -> int col <> column) then
                            Diagnostics.unlocated $"clang's column for declaration '{expected}' in '{path}' disagrees with its offset {offset}"
                        else
                            Diagnostics.Provenance (Diagnostics.Located.Known { Path = path; Position = Diagnostics.Located.Known { Line = line; Column = column } })
            | None, _, _ -> Diagnostics.unlocated $"clang names no file for declaration '{expected}'"
            | _ -> Diagnostics.unlocated $"clang gives declaration '{expected}' no offset"

    /// Name the unnamed records a named record's fields are typed with (issue #159). Clang
    /// emits each unnamed RecordDecl immediately before the FieldDecl declared with it; that
    /// declaration is taken for the field only when its tag and its column (and its line, when
    /// clang prints one) are the location the field's type spells. The record becomes its own
    /// declaration named `anonymousRecordName parent field`, the field is respelled to name it,
    /// and records nested in it are named the same way from its generated name. A field whose
    /// unnamed record cannot be paired this way keeps clang's spelling, which no resolver
    /// represents, so its use is refused rather than flattened or guessed.
    ///
    /// A C11 anonymous member (#173), the implicit unnamed field clang declares for an unnamed
    /// struct or union with no field name, is paired the same way with the record declared just
    /// before it. It becomes the field `anonymousMemberName n` in its declaration position, typed
    /// by the record named `anonymousRecordName parent field`, so the enclosing record's measured
    /// layout has a field for every offset clang gives it. An anonymous member that cannot be
    /// paired refuses the unit (FS9122), and so does a generated member name that names another
    /// member of the record, direct or reached through an anonymous member (FS9123): an
    /// anonymous member is never dropped.
    let private nameAnonymousMemberFields
        (header: string)
        (parent: string)
        (parentLocation: Diagnostics.Located<Diagnostics.SourceLocation>)
        (node: JsonValue)
        (declaredFields: FieldDecl list)
        : Result<FieldDecl list * StructDecl list, Diagnostics.Diagnostic> =
        let position (declaration: JsonValue) (key: string) =
            getObject declaration "loc" |> Option.bind (fun loc ->
                match getObject loc "expansionLoc" with
                | Some expansion -> getInt64 expansion key
                | None -> getInt64 loc key)
        let pairs (declaration: JsonValue) (tag: string) (location: string) =
            match clangLocation location with
            | Some (_, line, col) ->
                getStringOr declaration "tagUsed" "struct" = tag
                && position declaration "col" = Some (int64 col)
                && (position declaration "line" |> Option.forall ((=) (int64 line)))
            | None -> false
        let rec name (parent: string) (parentLocation: Diagnostics.Located<Diagnostics.SourceLocation>) (node: JsonValue) (declaredFields: FieldDecl list) : Result<FieldDecl list * StructDecl list, Diagnostics.Diagnostic> =
            let nested = ResizeArray<StructDecl>()
            let fields = ResizeArray<FieldDecl>()
            let declared = declaredFields |> List.map (fun f -> f.Name, f) |> Map.ofList
            // Every member name of the record: its named fields and the members clang reaches
            // through its anonymous members (IndirectFieldDecl).
            let memberNames =
                getArray node "inner"
                |> Seq.filter (fun child -> let kind = getStringOr child "kind" "" in kind = "FieldDecl" || kind = "IndirectFieldDecl")
                |> Seq.map (fun child -> getStringOr child "name" "")
                |> Seq.filter ((<>) "")
                |> Set.ofSeq
            let mutable unnamed : JsonValue option = None
            let mutable members = 0
            let mutable refused : Diagnostics.Diagnostic option = None
            for child in getArray node "inner" do
                if refused.IsNone then
                    match getStringOr child "kind" "" with
                    | ("RecordDecl" | "CXXRecordDecl") when getStringOr child "name" "" = "" && getBool child "completeDefinition" ->
                        unnamed <- Some child
                    | "FieldDecl" when not (getBool child "isImplicit") ->
                        let field = getStringOr child "name" ""
                        match Map.tryFind field declared with
                        | None -> ()
                        | Some f ->
                            let spelled = unnamedRecordSpelling.Match(getQualType child)
                            let paired =
                                match unnamed with
                                | Some declaration when spelled.Success && pairs declaration spelled.Groups.["tag"].Value spelled.Groups.["loc"].Value ->
                                    processRecordDecl (Diagnostics.Provenance (clangSourceLocation spelled.Groups.["loc"].Value)) declaration |> Option.map (fun record -> declaration, record)
                                | _ -> None
                            match paired with
                            | Some (declaration, inner) ->
                                let generated = anonymousRecordName parent field
                                let origin = { Parent = parent; Field = field; Location = spelled.Groups.["loc"].Value; Member = false }
                                match name generated inner.Location.Location declaration inner.Fields with
                                | Error d -> refused <- Some d
                                | Ok (innerFields, deeper) ->
                                    let named = { inner with Name = generated; Fields = innerFields; Anonymous = Some origin }
                                    nested.Add named
                                    nested.AddRange deeper
                                    let spelling = spelled.Groups.["pre"].Value + spelled.Groups.["tag"].Value + " " + generated + spelled.Groups.["post"].Value
                                    fields.Add { parseFieldType spelling with Name = f.Name; IsBitfield = f.IsBitfield; BitWidth = f.BitWidth; Attributes = f.Attributes }
                            | None -> fields.Add f
                    | "FieldDecl" when getBool child "isImplicit" && getStringOr child "name" "" = "" ->
                        members <- members + 1
                        let field = anonymousMemberName members
                        let clangType = getQualType child
                        let spelled = anonymousMemberSpelling.Match clangType
                        let location = if spelled.Success then spelled.Groups.["loc"].Value else ""
                        if memberNames.Contains field then
                            refused <- Some (Diagnostics.diagnostic Diagnostics.AnonymousMemberNameCollision $"the generated member name '{field}' of the anonymous member of '{parent}' declared at {location} in '{header}' names another member of that record. A generated name must name one member." (clangSourceLocation location))
                        else
                            let paired =
                                match unnamed with
                                | Some declaration when spelled.Success && pairs declaration spelled.Groups.["tag"].Value location ->
                                    processRecordDecl (Diagnostics.Provenance (clangSourceLocation location)) declaration |> Option.map (fun record -> declaration, record)
                                | _ -> None
                            match paired with
                            | Some (declaration, inner) ->
                                let generated = anonymousRecordName parent field
                                let origin = { Parent = parent; Field = field; Location = location; Member = true }
                                match name generated inner.Location.Location declaration inner.Fields with
                                | Error d -> refused <- Some d
                                | Ok (innerFields, deeper) ->
                                    let named = { inner with Name = generated; Fields = innerFields; Anonymous = Some origin }
                                    nested.Add named
                                    nested.AddRange deeper
                                    let spelling = spelled.Groups.["pre"].Value + spelled.Groups.["tag"].Value + " " + generated
                                    fields.Add { parseFieldType spelling with Name = field; Attributes = extractAttributes child }
                            | None ->
                                // Clang's spelling locates the anonymous member's record; without it, the
                                // enclosing record's location stands for the refusal's source.
                                let source = if spelled.Success then clangSourceLocation location else parentLocation
                                refused <- Some (Diagnostics.diagnostic Diagnostics.AnonymousMemberUnrepresented $"an anonymous member of '{parent}' in '{header}', of clang type '{clangType}', cannot be paired with a record clang declares for it, so '{parent}' has no representation of it. An anonymous member is never dropped from its record." source)
                    | _ -> ()
            match refused with
            | Some d -> Error d
            | None -> Ok (List.ofSeq fields, List.ofSeq nested)
        name parent parentLocation node declaredFields

    let private nameAnonymousRecords (header: string) (parent: string) (node: JsonValue) (record: StructDecl) : Result<StructDecl * StructDecl list, Diagnostics.Diagnostic> =
        nameAnonymousMemberFields header parent record.Location.Location node record.Fields
        |> Result.map (fun (fields, nested) -> { record with Fields = fields }, nested)

    /// Extract the explicit value from an EnumConstantDecl AST node, if present.
    /// Returns None when the enum constant has no initializer (implicit sequential value).
    /// A value clang reports that the signed 64-bit carrier cannot hold (an unsigned 64-bit
    /// enumerator above 2^63-1) is refused rather than read as an implicit successor.
    let private extractEnumConstantValue (enumLocation: Diagnostics.Provenance) (node: JsonValue) : int64 option =
        let value (element: JsonValue) =
            match getInt64 element "value", getString element "value" with
            | Some v, _ -> Some v
            | None, Some "false" -> Some 0L
            | None, Some "true" -> Some 1L
            | None, Some text when text <> "" ->
                let name = getStringOr node "name" "<unnamed>"
                Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.EnumValueRange $"enumerator '{name}' has value {text}, outside the signed 64-bit range the enum representation carries; a wider enumerator representation contract is required." enumLocation.Location)
            | None, _ -> None
        getArray node "inner"
        |> Seq.tryPick (fun inner ->
            match getStringOr inner "kind" "" with
            | "ConstantExpr" | "IntegerLiteral" ->
                value inner
            | _ ->
                getArray inner "inner"
                |> Seq.tryPick value)

    /// Process EnumDecl AST node
    let private processEnumDecl (location: Diagnostics.Provenance) (node: JsonValue) : EnumDecl option =
        let name = getString node "name"
        let documentation = extractDocumentation node
        // Process enum constants with auto-increment for implicit values.
        // C rule: first implicit value is 0, subsequent implicit values are previous + 1,
        // explicit values reset the counter.
        let values =
            let constants =
                getArray node "inner"
                |> Seq.filter (fun inner ->
                    getStringOr inner "kind" "" = "EnumConstantDecl")
            let mutable nextValue = 0L
            [ for constNode in constants do
                match getString constNode "name" with
                | None | Some "" -> ()
                | Some constName ->
                    let value =
                        match extractEnumConstantValue location constNode with
                        | Some explicit -> explicit
                        | None -> nextValue
                    nextValue <- value + 1L
                    yield { Name = constName; Value = value; Documentation = None } ]

        // Try to get fixed underlying type
        let underlyingType =
            match getObject node "fixedUnderlyingType" with
            | Some ut -> getString ut "qualType"
            | None -> None

        match name, values with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Values = values; Documentation = documentation; UnderlyingType = underlyingType; Representation = ClangUnmeasured; Location = location }
        | _, vs when not vs.IsEmpty ->
            Some { Name = ""; Values = values; Documentation = documentation; UnderlyingType = underlyingType; Representation = ClangUnmeasured; Location = location }
        | _ -> None

    /// Process TypedefDecl AST node
    let private processTypedefDecl (node: JsonValue) : TypedefInfo option =
        match getString node "name" with
        | None | Some "" -> None
        | Some name ->
            let underlyingType = getQualType node
            Some {
                Name = name
                UnderlyingType = underlyingType
                Documentation = None
                Attributes = extractAttributes node
            }

    /// Walk AST tree and extract declarations from the target file.
    /// Uses mutable state to track file across sibling nodes (clang only emits file once per file change).
    /// Library boundary is determined by includeRoot (from pkg-config) or filename matching (fallback).
    let private walkAst
        (root: JsonValue)
        (header: string)
        (targetFile: string)
        (includeRoot: string option)
        (verbose: bool)
        (enrichFunction: (string * string) list option -> JsonValue -> FunctionDecl -> Result<FunctionDecl, Diagnostics.Refusal>)
        : Result<Declaration list * Set<string> * Declaration list, Diagnostics.Refusal> =

        let results = ResizeArray<Declaration>()
        let mutable astError : Diagnostics.Refusal option = None
        // Enums and records named only by a typedef (`typedef enum { ... } T;`,
        // `typedef union { ... } T;`): spelled `T`, not `enum T` or `union T`.
        let typedefNamedTags = System.Collections.Generic.HashSet<string>()
        // Typedefs and enums this unit's own parse sees through its includes but whose files
        // lie outside the library boundary: facts of this same translation unit, kept for
        // resolving its own declarations, never emitted as its declarations.
        let included = ResizeArray<Declaration>()
        let mutable currentFile: string option = None
        // Each file clang locates a declaration in, read once to count its lines (#184).
        let files = System.Collections.Generic.Dictionary<string, (byte[] * int[]) option>()
        let located node = nodeProvenance files currentFile node
        let processFunction scope node =
            match processFunctionDecl (located node) node with
            | None -> None
            | Some fn ->
                match enrichFunction scope node fn with
                | Ok enriched -> Some enriched
                | Error refusal ->
                    if astError.IsNone then astError <- Some refusal
                    None

        // Clang emits an anonymous typedef union or enum as a RecordDecl or EnumDecl followed
        // by a TypedefDecl whose RecordType or EnumType points back to that declaration.
        // The typedef's name is the declaration's name (pthread_mutex_t, GtkWindowType).
        let rec descendants node = seq {
            yield node
            for child in getArray node "inner" do yield! descendants child
        }
        // Only the typedef whose own type is the declaration names it: its type node, through
        // elaboration and qualifiers only. A later typedef that merely mentions the record
        // (`typedef fx_vec_t (*fx_vec_map)(fx_vec_t)`, a pointer typedef) never renames it, and
        // the first naming typedef wins.
        let rec ownTypeNodes (node: JsonValue) = seq {
            match getStringOr node "kind" "" with
            | "RecordType" | "EnumType" -> yield node
            | "ElaboratedType" | "QualType" -> for child in getArray node "inner" do yield! ownTypeNodes child
            | _ -> ()
        }
        let anonymousRecordNames =
            descendants root
            |> Seq.filter (fun node -> getStringOr node "kind" "" = "TypedefDecl")
            |> Seq.collect (fun node ->
                let name = getStringOr node "name" ""
                getArray node "inner" |> Seq.collect ownTypeNodes |> Seq.choose (fun child ->
                    getObject child "decl" |> Option.bind (fun decl ->
                        if getStringOr decl "name" "" <> "" then None
                        else getString decl "id" |> Option.map (fun id -> id, name))))
            |> Seq.fold (fun (acc: Map<string, string>) (id, name) -> if acc.ContainsKey id then acc else acc.Add(id, name)) Map.empty

        if verbose then
            match includeRoot with
            | Some root -> printfn "[CppParser] Include root scoping: %s" root
            | None -> printfn "[CppParser] No include root — using filename matching for: %s" targetFile

        /// Walk a Clang JSON AST value in dump order and update currentFile for any
        /// emitted "file" property that advances Clang's LastLocFilename state.
        /// Excludes "includedFrom" (Clang's IncludedLoc preserves LastLocFilename)
        /// and "inner" (traversed recursively by processNode).
        let rec updateFileFromValue (value: JsonValue) =
            match value with
            | JsonValue.Record props ->
                for k, v in props do
                    if k = "file" then
                        match v with
                        | JsonValue.String f -> currentFile <- Some f
                        | _ -> ()
                    elif k <> "includedFrom" && k <> "inner" then
                        updateFileFromValue v
            | JsonValue.Array items ->
                for item in items do
                    updateFileFromValue item
            | _ -> ()

        /// Update file tracking from a node's "loc" field (including macro spellingLoc/expansionLoc).
        let updateFileFromLoc (node: JsonValue) =
            match getObject node "loc" with
            | Some loc -> updateFileFromValue loc
            | None -> ()

        /// Update file tracking from non-"loc", non-"inner" node fields (notably "range")
        /// before recursing into "inner" or advancing to the next sibling node.
        let updateFileFromNodeTail (node: JsonValue) =
            match node with
            | JsonValue.Record props ->
                for k, v in props do
                    if k <> "loc" && k <> "includedFrom" && k <> "inner" then
                        updateFileFromValue v
            | _ -> ()

        /// Advance LastLocFilename across an unemitted AST subtree (e.g., an anonymous
        /// NamespaceDecl whose internal-linkage declarations are not bound) in Clang's
        /// pre-order dump sequence (loc, non-inner fields, then inner children).
        let rec advanceFileThroughSubtree (node: JsonValue) =
            updateFileFromLoc node
            updateFileFromNodeTail node
            for inner in getArray node "inner" do
                advanceFileThroughSubtree inner

        /// Check if current node belongs to this library.
        /// With includeRoot: any file under that directory tree belongs to the library.
        /// Without: fall back to matching the target filename (for simple single-file headers).
        let isFromTargetFile (node: JsonValue) =
            match currentFile with
            | Some f ->
                match includeRoot with
                | Some root -> f.StartsWith root
                | None -> f.EndsWith targetFile || f = targetFile
            | None -> false

        /// Process a single node
        let rec processNode scope (node: JsonValue) =
            updateFileFromLoc node

            let isImplicit = getBool node "isImplicit"
            let kind = getStringOr node "kind" ""
            // Only named lexical declaration contexts participate in the join.
            // Do not infer template identities or declarations inside function bodies.
            let childScope =
                match kind with
                | "NamespaceDecl" | "RecordDecl" | "CXXRecordDecl" ->
                    let declaredName =
                        match getString node "name" with
                        | Some n when not (String.IsNullOrEmpty n) -> Some n
                        | _ -> getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames)
                    match getString node "parentDeclContextId", declaredName with
                    | None, Some name when not (String.IsNullOrEmpty name) ->
                        scope |> Option.map (fun parents -> parents @ [kind, name])
                    | _ -> None
                | "FunctionTemplateDecl" | "ClassTemplateDecl"
                | "ClassTemplateSpecializationDecl" | "ClassTemplatePartialSpecializationDecl" -> None
                | "FunctionDecl" | "CXXMethodDecl" | "CXXConstructorDecl" -> None
                | _ -> scope

            // NamespaceDecl is a structural container, not a declaration.
            // Always recurse into namespace children regardless of file context,
            // because clang often elides the file attribute on NamespaceDecl nodes
            // (it only emits file on the first source-file change). The library
            // boundary check applies to leaf declarations inside the namespace.
            if kind = "NamespaceDecl" then
                updateFileFromNodeTail node
                match getString node "name" with
                | Some name when not (System.String.IsNullOrEmpty(name)) ->
                    for inner in getArray node "inner" do
                        processNode childScope inner
                | _ ->
                    for inner in getArray node "inner" do
                        advanceFileThroughSubtree inner

            elif not isImplicit && isFromTargetFile node then
                if verbose then
                    let name = getStringOr node "name" "<anonymous>"
                    let fileLabel = match currentFile with Some path -> path | None -> "<none>"
                    printfn "[CppParser] Processing %s: %s (file: %s)" kind name fileLabel

                match kind with
                | "FunctionDecl" ->
                    match processFunction scope node with
                    | Some func -> results.Add(Function func)
                    | None -> ()

                | "RecordDecl" ->
                    match processRecordDecl (located node) node with
                    | Some structDecl ->
                        let name =
                            if structDecl.Name <> "" then structDecl.Name
                            else
                                let named = getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames) |> Option.defaultValue ""
                                if named <> "" then typedefNamedTags.Add named |> ignore
                                named
                        if name = "" then results.Add(Struct structDecl)
                        else
                            // Unnamed records its fields are typed with become declarations of their own.
                            match nameAnonymousRecords header name node { structDecl with Name = name } with
                            | Error d ->
                                if astError.IsNone then astError <- Some (Diagnostics.refusalOf d)
                            | Ok (named, nested) ->
                                results.Add(Struct named)
                                for record in nested do results.Add(Struct record)
                    | None -> ()

                | "EnumDecl" ->
                    match processEnumDecl (located node) node with
                    | Some enumDecl ->
                        let name =
                            if enumDecl.Name <> "" then enumDecl.Name
                            else
                                let named = getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames) |> Option.defaultValue ""
                                if named <> "" then typedefNamedTags.Add named |> ignore
                                named
                        results.Add(Enum { enumDecl with Name = name })
                    | None -> ()

                | "TypedefDecl" | "TypeAliasDecl" ->
                    match processTypedefDecl node with
                    | Some typedef -> results.Add(Typedef typedef)
                    | None -> ()

                | "CXXRecordDecl" ->
                    let resolvedName =
                        match getString node "name" with
                        | Some n when not (System.String.IsNullOrEmpty(n)) -> Some n
                        | _ ->
                            let named = getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames) |> Option.defaultValue ""
                            if named <> "" then
                                typedefNamedTags.Add named |> ignore
                                Some named
                            else None
                    match resolvedName with
                    | Some name ->
                        let innerNodes = getArray node "inner"

                        // Extract base class types from clang's "bases" array.
                        // Each entry has type.qualType (written) and type.desugaredQualType (canonical).
                        // Use desugaredQualType when available for full namespace resolution.
                        let baseClasses =
                            match node.TryGetProperty("bases") with
                            | Some (JsonValue.Array basesArr) ->
                                [ for baseNode in basesArr do
                                    match baseNode.TryGetProperty("type") with
                                    | Some typeNode ->
                                        match getString typeNode "desugaredQualType" with
                                        | Some t -> yield t
                                        | None ->
                                            match getString typeNode "qualType" with
                                            | Some t -> yield t
                                            | None -> ()
                                    | None -> () ]
                            | _ -> []

                        let nestedEnums =
                            innerNodes
                            |> Seq.choose (fun inner ->
                                if getStringOr inner "kind" "" = "EnumDecl" then getString inner "name" else None)
                            |> Set.ofSeq
                        let qualifyNestedEnum (typ: string) =
                            if nestedEnums.Contains(typ.Trim()) then name + "::" + typ.Trim() else typ
                        let qualifyFunction (f: FunctionDecl) =
                            { f with
                                ReturnType = qualifyNestedEnum f.ReturnType
                                Parameters = f.Parameters |> List.map (fun (pName, pType) -> pName, qualifyNestedEnum pType) }

                        let methods =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                let k = getStringOr inner "kind" ""
                                let n = getStringOr inner "name" ""
                                let isOperator =
                                    n.StartsWith("operator") &&
                                    (n.Length = 8 || not (System.Char.IsLetterOrDigit n[8] || n[8] = '_'))
                                (k = "CXXMethodDecl" || k = "FunctionDecl") &&
                                not (getBool inner "isImplicit") &&
                                not isOperator)
                            |> Seq.choose (processFunction childScope)
                            |> Seq.map (qualifyFunction >> Function)
                            |> List.ofSeq

                        // Capture constructors (CXXConstructorDecl) as FunctionDecl.
                        // The constructor name in clang AST is the class name.
                        let constructors =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl" &&
                                not (getBool inner "isImplicit"))
                            |> Seq.choose (processFunction childScope)
                            |> Seq.map (qualifyFunction >> Function)
                            |> List.ofSeq

                        let declaredFields =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "FieldDecl")
                            |> Seq.choose processFieldDecl
                            |> List.ofSeq
                        let fields, nested =
                            match nameAnonymousMemberFields header name (located node).Location node declaredFields with
                            | Ok pair -> pair
                            | Error d ->
                                if astError.IsNone then astError <- Some (Diagnostics.refusalOf d)
                                [], []

                        let isAbstract =
                            innerNodes
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXMethodDecl" &&
                                getBool inner "pure")

                        // Detect non-implicit (user-defined) destructor and extract its
                        // mangled symbol. Under SysV x86_64 ABI, a user-defined destructor
                        // makes the type non-trivially copyable, triggering MEMORY classification
                        // and hidden sret pointer for return-by-value.
                        let userDtorNode =
                            innerNodes
                            |> Seq.tryFind (fun inner ->
                                getStringOr inner "kind" "" = "CXXDestructorDecl" &&
                                not (getBool inner "isImplicit"))
                        let hasUserDestructor = userDtorNode.IsSome
                        let destructorMangledName =
                            userDtorNode |> Option.bind (fun n -> getString n "mangledName")

                        // Detect non-implicit copy constructor.
                        // A copy ctor takes a single const-ref-to-self parameter.
                        let hasUserCopyConstructor =
                            innerNodes
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl" &&
                                not (getBool inner "isImplicit") &&
                                (match getString inner "explicitlyDefaulted" with
                                 | Some "deleted" -> false
                                 | _ ->
                                    let parameters =
                                        getArray inner "inner"
                                        |> Seq.filter (fun p -> getStringOr p "kind" "" = "ParmVarDecl")
                                        |> List.ofSeq
                                    parameters.Length = 1 &&
                                    (let t = getQualType parameters.[0]
                                     t.Contains("const") && t.Contains("&") && t.Contains(name))))

                        // Detect non-implicit move constructor.
                        // A move ctor takes a single rvalue-ref-to-self parameter.
                        let hasUserMoveConstructor =
                            innerNodes
                            |> Seq.exists (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl" &&
                                not (getBool inner "isImplicit") &&
                                (match getString inner "explicitlyDefaulted" with
                                 | Some "deleted" -> false
                                 | _ ->
                                    let parameters =
                                        getArray inner "inner"
                                        |> Seq.filter (fun p -> getStringOr p "kind" "" = "ParmVarDecl")
                                        |> List.ofSeq
                                    parameters.Length = 1 &&
                                    (let t = getQualType parameters.[0]
                                     t.Contains("&&") && t.Contains(name) && not (t.Contains("const")))))

                        results.Add(Class {
                            Name = name
                            Methods = methods
                            Constructors = constructors
                            Fields = fields
                            Documentation = None
                            IsAbstract = isAbstract
                            HasUserDestructor = hasUserDestructor
                            DestructorMangledName = destructorMangledName
                            HasUserCopyConstructor = hasUserCopyConstructor
                            HasUserMoveConstructor = hasUserMoveConstructor
                            BaseClasses = baseClasses
                            // Unnamed bit-fields are kept as padding, as the C record path keeps
                            // them (#182, #176); clang dumps an offset for each.
                            UnnamedBitFields = unnamedBitFieldsOf node
                            TypedefAttributes = []
                            Measured = None
                            QualifiedName = scope |> Option.map (fun parents -> (parents |> List.map snd) @ [name] |> String.concat "::")
                            Location = located node
                        })
                        for record in nested do results.Add(Struct record)
                    | _ -> ()

                | _ -> ()

                // Advance LastLocFilename from range/attributes before recursing into children
                updateFileFromNodeTail node
                for inner in getArray node "inner" do
                    processNode childScope inner

            else
                if not isImplicit then
                    match kind with
                    | "EnumDecl" ->
                        match processEnumDecl (located node) node with
                        | Some enumDecl ->
                            let name =
                                if enumDecl.Name <> "" then enumDecl.Name
                                else
                                    let named = getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames) |> Option.defaultValue ""
                                    if named <> "" then typedefNamedTags.Add named |> ignore
                                    named
                            if name <> "" then included.Add(Enum { enumDecl with Name = name })
                        | None -> ()
                    | "TypedefDecl" | "TypeAliasDecl" ->
                        match processTypedefDecl node with
                        | Some typedef -> included.Add(Typedef typedef)
                        | None -> ()
                    | _ -> ()
                // Advance LastLocFilename from range/attributes before recursing into children
                updateFileFromNodeTail node
                // Outside the target file: still recurse to find target-file
                // declarations that may appear deeper in the AST (e.g. in
                // TranslationUnitDecl or LinkageSpecDecl wrappers).
                if kind <> "NamespaceDecl" then
                    for inner in getArray node "inner" do
                        processNode childScope inner

        // Start processing from root
        processNode (Some []) root
        let results = List.ofSeq results
        let own = results |> List.choose (function Enum e -> Some ("enum", e.Name) | Typedef t -> Some ("typedef", t.Name) | _ -> None) |> Set.ofList
        let included =
            included
            |> Seq.filter (function
                | Enum e -> not (own.Contains ("enum", e.Name))
                | Typedef t -> not (own.Contains ("typedef", t.Name))
                | _ -> false)
            |> Seq.distinctBy (function Enum e -> "enum " + e.Name | Typedef t -> "typedef " + t.Name | _ -> "")
            |> List.ofSeq
        match astError with
        | Some refusal -> Error refusal
        | None -> Ok (results, Set.ofSeq typedefNamedTags, included)

    // =========================================================================
    // Macro Parsing
    // =========================================================================

    // =========================================================================
    // XParsec Macro Parsers (private, defined inline for compile-order independence)
    // =========================================================================

    /// Classify an object macro's value into MacroKind
    let private classifyObjectMacroValue (value: string) : MacroKind =
        if value.StartsWith("((") && value.Contains("*)") && value.EndsWith(")") then
            let inner = value.Substring(2, value.Length - 3)
            match inner.IndexOf("*)") with
            | -1 -> SimpleValue value
            | idx ->
                let typeName = inner.Substring(0, idx).Trim()
                let castValue = inner.Substring(idx + 2).Trim()
                TypeCast (typeName, castValue)
        elif value.Contains("+") || value.Contains("-") || value.Contains("<<") ||
             value.Contains(">>") || value.Contains("|") || value.Contains("&") then
            Expression value
        else
            SimpleValue value

    /// XParsec parsers for macro line parsing (same pattern as XParsec.Json.JsonParsers)
    type private MacroParsers<'Input, 'InputSlice
        when 'Input :> IReadable<char, 'InputSlice> and 'InputSlice :> IReadable<char, 'InputSlice>>() =

        static let pIdentifier =
            many1Chars (satisfyL (fun c -> Char.IsLetterOrDigit c || c = '_') "identifier char")

        static let pFunctionLikeMacro =
            parser {
                let! name = pIdentifier
                do! skipChar '('
                let! args, _ = sepBy (spaces >>. pIdentifier .>> spaces) (skipChar ',')
                do! skipChar ')'
                do! spaces1
                let! body = manyChars (satisfyL (fun _ -> true) "body char")
                let macro : MacroDecl = {
                    Name = name
                    Kind = FunctionLike (Seq.toList args, body)
                    RawValue = body
                    Documentation = None
                }
                return macro
            }

        static let pObjectMacro =
            parser {
                let! name = pIdentifier
                do! spaces1
                let! value = manyChars (satisfyL (fun _ -> true) "value char")
                let trimmed = value.Trim()
                let macro : MacroDecl = {
                    Name = name
                    Kind = classifyObjectMacroValue trimmed
                    RawValue = trimmed
                    Documentation = None
                }
                return macro
            }

        static let pEmptyMacro =
            parser {
                let! name = pIdentifier
                do! eof
                let macro : MacroDecl = {
                    Name = name
                    Kind = SimpleValue ""
                    RawValue = ""
                    Documentation = None
                }
                return macro
            }

        static let pMacroLine =
            parser {
                do! pstring "#define " >>% ()
                do! spaces
                return! choice [ pFunctionLikeMacro; pObjectMacro; pEmptyMacro ]
            }

        static member MacroLine = pMacroLine

    type private MP = MacroParsers<ReadableString, ReadableStringSlice>

    /// Parse a single macro definition line using XParsec.
    let private parseMacroLine (line: string) : MacroDecl option =
        if not (line.StartsWith("#define ")) then None
        else
            let reader = Reader.ofString line ()
            match MP.MacroLine reader with
            | Ok result -> Some result.Parsed
            | Error _ -> None

    /// Filter macros to exclude compiler built-ins.
    let private isUserMacro (name: string) (prefixes: string list) : bool =
        // Exclude compiler built-ins (__STDC__, __GNUC__ etc.)
        if name.StartsWith("__") && name.EndsWith("__") then false
        // Exclude reserved identifiers (_POSIX_SOURCE, _GNU_SOURCE etc.)
        elif name.StartsWith("_") && name.Length > 1 && Char.IsUpper(name.[1]) then false
        elif prefixes.IsEmpty then true
        else prefixes |> List.exists (fun p -> name.StartsWith(p))

    // =========================================================================
    // Raw Header Comment Extraction
    // =========================================================================

    /// Extract trailing comment from a #define line in raw header text.
    /// Handles both /* ... */ and // ... comment styles.
    /// Returns just the comment text without delimiters.
    let private extractTrailingComment (line: string) : string option =
        // Look for /* ... */ comment
        match line.IndexOf("/*") with
        | -1 ->
            // Look for // comment
            match line.IndexOf("//") with
            | -1 -> None
            | idx ->
                let comment = line.Substring(idx + 2).Trim()
                if comment.Length > 0 then Some comment else None
        | idx ->
            let afterOpen = line.Substring(idx + 2)
            match afterOpen.IndexOf("*/") with
            | -1 -> None  // Unclosed comment — skip
            | endIdx ->
                let comment = afterOpen.Substring(0, endIdx).Trim()
                if comment.Length > 0 then Some comment else None

    /// Scan a single file's raw text for #define lines and extract macro name → comment mappings.
    let private extractMacroCommentsFromFile (filePath: string) : Map<string, string> =
        if not (File.Exists filePath) then Map.empty
        else
            try
                File.ReadAllLines(filePath)
                |> Array.choose (fun line ->
                    let trimmed = line.TrimStart()
                    if trimmed.StartsWith("#define") then
                        // Parse: #define NAME ...rest... /* comment */
                        let afterDefine = trimmed.Substring(7).TrimStart()
                        // Extract the macro name (first identifier)
                        let nameEnd =
                            afterDefine
                            |> Seq.tryFindIndex (fun c -> not (Char.IsLetterOrDigit c || c = '_'))
                            |> Option.defaultValue afterDefine.Length
                        if nameEnd > 0 then
                            let name = afterDefine.Substring(0, nameEnd)
                            match extractTrailingComment afterDefine with
                            | Some comment -> Some (name, comment)
                            | None -> None
                        else None
                    else None)
                |> Map.ofArray
            with _ -> Map.empty

    // =========================================================================
    // Struct Layout Parser (XParsec, monadic — parses clang -fdump-record-layouts-simple)
    // =========================================================================

    /// XParsec parsers for clang's -fdump-record-layouts-simple stderr output.
    ///
    /// Format (all values in bits):
    ///   *** Dumping AST Record Layout
    ///   Type: struct Point
    ///   Layout: <ASTRecordLayout
    ///     Size:128
    ///     DataSize:128
    ///     Alignment:64
    ///     FieldOffsets: [0, 32, 64]>
    type private LayoutParsers<'Input, 'InputSlice
        when 'Input :> IReadable<char, 'InputSlice> and 'InputSlice :> IReadable<char, 'InputSlice>>() =

        static let pSpaces = skipMany (satisfyL (fun c -> c = ' ' || c = '\t') "space")

        static let pNewline = satisfyL (fun c -> c = '\n' || c = '\r') "newline" >>% ()

        static let pSkipLine =
            skipMany (satisfyL (fun c -> c <> '\n' && c <> '\r') "non-newline")
            >>. optional pNewline

        static let pStructName =
            parser {
                do! pSpaces
                do! pstring "Type: " >>% ()
                // A C++ record dumps as `class X` as well (#182).
                do! optional ((pstring "struct " <|> pstring "union " <|> pstring "class ") >>% ()) >>% ()
                // The whole rest of the line: an unnamed nested record is spelled
                // `Parent::(unnamed at path:line:col)`, which a first-space cut would collapse.
                let! name = many1Chars (satisfyL (fun c -> c <> '\n' && c <> '\r') "name char")
                do! pSkipLine
                return name.TrimEnd()
            }

        static let pIntField label =
            parser {
                do! pSpaces
                do! pstring label >>% ()
                let! value = pint32
                do! pSpaces
                do! pNewline
                do! skipMany pNewline
                return value
            }

        static let pFieldOffsets =
            let rec remaining values =
                parser {
                    do! pSpaces
                    let! comma = opt (skipChar ',')
                    match comma with
                    | ValueNone -> return List.rev values
                    | ValueSome _ ->
                        do! pSpaces
                        let! value = pint32
                        return! remaining (value :: values)
                }
            parser {
                do! pSpaces
                do! pstring "FieldOffsets: [" >>% ()
                do! pSpaces
                let! first = pint32
                let! offsets = remaining [first]
                do! skipChar ']'
                do! pSkipLine
                return offsets
            }

        static let pEmptyFieldOffsets =
            parser {
                do! pSpaces
                do! pstring "FieldOffsets: []" >>% ()
                do! pSkipLine
                return []
            }

        /// A C++ record's `BaseOffsets: [...]>` or `VBaseOffsets: [...]>` line (#182). Its base
        /// subobjects lie inside the record's measured size; their offsets are not carried.
        static let pBaseOffsets label =
            parser {
                do! pSpaces
                do! pstring label >>% ()
                do! pSkipLine
            }

        static let pLayoutBlock =
            parser {
                let! name = pStructName
                do! skipMany pNewline
                do! pSkipLine // "Layout: <ASTRecordLayout" line
                let! size = pIntField "Size:"
                let! dataSize = pIntField "DataSize:"
                let! alignment = pIntField "Alignment:"
                do! optional (pBaseOffsets "BaseOffsets: [") >>% ()
                do! optional (pBaseOffsets "VBaseOffsets: [") >>% ()
                let! offsets = pEmptyFieldOffsets <|> pFieldOffsets
                do! pSkipLine // closing ">" line
                return {
                    Name = name
                    SizeBits = size
                    DataSizeBits = dataSize
                    AlignmentBits = alignment
                    FieldOffsetsBits = offsets
                }
            }

        static member StructName = pStructName
        static member LayoutBlock = pLayoutBlock

    type private LP = LayoutParsers<ReadableString, ReadableStringSlice>

    /// Parse clang's -fdump-record-layouts-simple stderr output into a map of struct layouts.
    /// Each record layout block is preceded by "*** Dumping AST Record Layout".
    /// Text before the first marker is clang output, not a layout block.
    /// A marked block that cannot be parsed refuses the entire measurement.
    let rec parseRecordLayouts (stderr: string) : Result<Map<string, StructLayoutInfo>, string> =
        parseRecordLayoutBlocks stderr
        |> Result.map (List.fold (fun layouts (layout: StructLayoutInfo) -> Map.add layout.Name layout layouts) Map.empty)

    /// Every record layout block of a clang dump, in dump order. A name clang dumps twice
    /// (two records it spells alike) appears twice, so a consumer can tell them apart.
    and parseRecordLayoutBlocks (stderr: string) : Result<StructLayoutInfo list, string> =
        let blocks = stderr.Split("*** Dumping AST Record Layout")
        let rec outOfRange = function
            | ErrorType.Message "Integer out of range." -> true
            | ErrorType.Nested(parent, children) ->
                outOfRange parent || (children |> List.exists (fun error -> outOfRange error.Errors))
            | _ -> false
        let rec collect ordinal layouts =
            if ordinal >= blocks.Length then Ok layouts
            else
                let trimmed = blocks[ordinal].TrimStart([| '\n'; '\r' |])
                if String.IsNullOrWhiteSpace trimmed then
                    Error $"Record layout block {ordinal} is empty."
                else
                    match LP.LayoutBlock (Reader.ofString trimmed ()) with
                    | Ok result -> collect (ordinal + 1) (result.Parsed :: layouts)
                    | Error error ->
                        let name =
                            match LP.StructName (Reader.ofString trimmed ()) with
                            | Ok result -> $" for '{result.Parsed}'"
                            | Error _ -> ""
                        let position = min (max 0 error.Position.Index) (trimmed.Length - 1)
                        let position =
                            if position > 0 && (trimmed[position] = '\n' || trimmed[position] = '\r') then position - 1
                            else position
                        let start = trimmed.LastIndexOf('\n', position) + 1
                        let finish = match trimmed.IndexOf('\n', position) with -1 -> trimmed.Length | value -> value
                        let line = trimmed.Substring(start, finish - start).Trim()
                        let range =
                            if outOfRange error.Errors then
                                " Measured bit quantities are supported in 0..2147483647; a wider measured bit representation contract is required."
                            else ""
                        let excerpt = trimmed.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) |> Array.truncate 6 |> String.concat " | "
                        Error $"Record layout block {ordinal}{name} failed at '{line}': {error.Errors}.{range} Block: {excerpt}"
        collect 1 [] |> Result.map List.rev

    // =========================================================================
    // Clang Invocation
    // =========================================================================

    /// A pilot define is `NAME` or `NAME=VALUE`, with `NAME` a C identifier and a one-line
    /// `VALUE` (#178). It reaches clang as exactly one `-D` argument, so it can carry no other
    /// option; anything else is refused before any parse (FS9124), quoting the define.
    /// A define is pilot, command-line or pkg-config input, never a header declaration, so its refusal has
    /// no source position; a pilot's refusal carries the pilot key (`BindingGenerator`).
    let defineRefusal (define: string) : Diagnostics.Diagnostic option =
        if System.Text.RegularExpressions.Regex.IsMatch(define, @"\A[A-Za-z_][A-Za-z0-9_]*(=[^\r\n\x00]*)?\z") then None
        else
            Some (Diagnostics.diagnostic Diagnostics.DefineMalformed
                    $"define '{define}' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option."
                    (Diagnostics.Located.Unknown "a define is pilot, command-line or pkg-config input, not a header declaration"))

    /// Every malformed define of a list, refused together, or Ok.
    let requireWellFormedDefines (defines: string list) : Result<unit, Diagnostics.Refusal> =
        match defines |> List.choose defineRefusal |> List.distinctBy Diagnostics.render with
        | [] -> Ok ()
        | refusals -> Error (Diagnostics.refusalsOf refusals "")

    /// A clang process whose arguments are each exactly one argument (#178).
    /// It launches the pinned toolchain clang with its own resource directory (#200), never a host
    /// clang or a host resource directory.
    let clangStartInfo (args: string seq) = ClangToolchain.startInfo args

    let private commandLine (args: string seq) = ProcessLaunch.commandLine args

    /// The include, system include and define arguments clang receives for a pilot's include
    /// paths, system include paths and defines, in that order: each include path one `-I<path>`
    /// argument, each system include path the two arguments `-isystem` and the path (#179), so
    /// the path is always clang's next argument and never read as an option, and each define
    /// one `-D<define>` argument (#178).
    let internal searchArguments (includePaths: string list) (systemIncludePaths: string list) (defines: string list) : string list =
        [ for includePath in includePaths -> $"-I{includePath}" ]
        @ [ for systemPath in systemIncludePaths do yield "-isystem"; yield systemPath ]
        @ [ for define in defines -> $"-D{define}" ]

    /// Build common clang arguments
    let private buildClangArgs (options: HeaderParserOptions) : ResizeArray<string> =
        let args = ResizeArray<string>()

        // Force C++ mode for headers containing C++ content.
        // Without this, clang treats .h files as C and skips
        // #ifdef __cplusplus blocks entirely.
        if options.CppMode then
            args.Add("-x")
            args.Add("c++")
            args.Add("-std=c++17")
        else
            args.Add("-x")
            args.Add("c")

        args.AddRange(searchArguments options.IncludePaths options.SystemIncludePaths options.Defines)
        args

    /// Run clang with given arguments
    let private runClang (baseArgs: ResizeArray<string>) (extraArgs: string list) (verbose: bool) : Result<string, string> =
        let args = ResizeArray<string>(baseArgs)
        for arg in extraArgs do
            args.Add(arg)

        let startInfo = clangStartInfo args

        if verbose then
            printfn "[CppParser] Running: clang %s" (commandLine args)

        try
            use proc = Process.Start(startInfo)
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode <> 0 then
                let errMsg =
                    if String.IsNullOrWhiteSpace(stderr) then
                        $"clang exited with code {proc.ExitCode}"
                    else
                        stderr.Trim()
                Error $"clang failed: {errMsg}"
            else
                if verbose then
                    printfn "[CppParser] clang completed, output: %d bytes" stdout.Length
                Ok stdout
        with ex ->
            Error $"Failed to run clang: {ex.Message}"

    /// Run clang AST dump
    let private runClangAst (options: HeaderParserOptions) : Result<string, string> =
        let args = buildClangArgs options
        runClang args ["-Xclang"; "-ast-dump=json"; "-fsyntax-only"; "-fparse-all-comments"; options.HeaderFile] options.Verbose

    /// Clang's JSON AST omits the arguments of NonNullAttr. Its text AST prints the
    /// semantic positions after the source range, even for macro-expanded attributes.
    /// Match lexical declaration scope, kind and complete type so distinct functions cannot share evidence.
    let private enrichNonNullAttributes (options: HeaderParserOptions) =
        let parseFailure (message: string) =
            Error (Diagnostics.uncoded $"Failed to parse clang output: {message}")
        let normalize (signature: string) =
            signature |> Seq.filter (not << Char.IsWhiteSpace) |> Seq.toArray |> String
        let evidence = lazy (
            let args = buildClangArgs options
            match runClang args ["-Xclang"; "-ast-dump"; "-fsyntax-only"; options.HeaderFile] options.Verbose with
            | Error error -> parseFailure $"Cannot establish nonnull parameter evidence: {error}"
            | Ok output ->
                let mutable dumpError : Diagnostics.Refusal option = None
                let pointerParameter (line: string) =
                    // Text AST supplies the canonical type after a typedef spelling.
                    // nonnull without positions applies only to native pointer slots.
                    let quoted = line.Split('\'')
                    if quoted.Length < 3 then
                        if dumpError.IsNone then dumpError <- Some (Diagnostics.uncoded "Failed to parse clang output: Clang parameter lacks its native type")
                        false
                    else
                        let nativeType = quoted.[quoted.Length - 2].Trim()
                        let lastStar = nativeType.LastIndexOf('*')
                        let firstParen = nativeType.IndexOf('(')
                        (lastStar >= 0 &&
                            (nativeType.Substring(lastStar + 1).Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                             |> Array.forall (fun word -> List.contains word ["const"; "volatile"; "restrict"; "__restrict"; "__restrict__"])))
                        || (firstParen >= 0 && not (nativeType.Substring(0, firstParen).Contains('<'))
                            && nativeType.Substring(firstParen + 1).TrimStart().StartsWith("*", StringComparison.Ordinal))
                let _, _, collected =
                    output.Split('\n')
                    |> Array.fold (fun (scopes, current, evidence) (line: string) ->
                        let nodeDepth =
                            line |> Seq.tryFindIndex (fun c -> c <> ' ' && c <> '|' && c <> '`' && c <> '-')
                            |> Option.defaultValue 0
                        let scopes = scopes |> List.filter (fun (depth, _) -> depth < nodeDepth)
                        let words = line.Substring(nodeDepth).Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                        let nodeKind = if words.Length = 0 then "" else words.[0]
                        let hasParentContext =
                            let rangeStart = line.IndexOf('<')
                            rangeStart >= 0 &&
                            (line.Substring(0, rangeStart).Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                             |> Array.pairwise
                             |> Array.exists (fun (marker, address) -> marker = "parent" && address.StartsWith("0x", StringComparison.Ordinal)))
                        let identifier (name: string) =
                            name.Length > 0 && (Char.IsLetter name.[0] || name.[0] = '_') &&
                            (name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_'))
                        let scope =
                            if hasParentContext && List.contains nodeKind ["NamespaceDecl"; "RecordDecl"; "CXXRecordDecl"] then Some None
                            elif nodeKind = "NamespaceDecl" then
                                let suffix = line.Substring(line.LastIndexOf('>') + 1).Trim().Split([|' '; '\t'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                                let name =
                                    match List.ofArray suffix with
                                    | location :: "referenced" :: name :: tail
                                    | location :: "inline" :: name :: tail
                                    | location :: name :: tail
                                        when (location.StartsWith("col:", StringComparison.Ordinal) || location.StartsWith("line:", StringComparison.Ordinal)) && identifier name ->
                                        let tail = match tail with "inline" :: rest -> rest | _ -> tail
                                        let tail = match tail with "nested" :: rest -> rest | _ -> tail
                                        match tail with
                                        | [] | ["external-linkage"] -> Some (nodeKind, name)
                                        | ["original"; "Namespace"; address; quotedName]
                                            when address.StartsWith("0x", StringComparison.Ordinal) && quotedName = $"'{name}'" -> Some (nodeKind, name)
                                        | _ -> None
                                    | _ -> None
                                Some name
                            elif nodeKind = "RecordDecl" || nodeKind = "CXXRecordDecl" then
                                Some (
                                    words
                                    |> Array.tryFindIndex (fun word -> List.contains word ["struct"; "class"; "union"])
                                    |> Option.bind (fun index ->
                                        if index + 1 < words.Length && identifier words.[index + 1] && words.[index + 1] <> "definition" then
                                            Some (nodeKind, words.[index + 1])
                                        else None))
                            elif List.contains nodeKind ["FunctionTemplateDecl"; "ClassTemplateDecl"; "ClassTemplateSpecializationDecl"; "ClassTemplatePartialSpecializationDecl";
                                                        "FunctionDecl"; "CXXMethodDecl"; "CXXConstructorDecl"] then Some None
                            else None
                        let parentScope =
                            scopes |> List.rev |> List.fold (fun parents (_, identity) ->
                                match parents, identity with
                                | Some parents, Some identity -> Some (parents @ [identity])
                                | _ -> None) (Some [])
                        let scopes = match scope with Some identity -> (nodeDepth, identity) :: scopes | None -> scopes
                        // A function's own attributes keep its entry key. Its body and
                        // unsupported contexts cannot contribute evidence to that key.
                        let current = current |> List.filter (fun (_, depth, _, _) -> depth < nodeDepth)
                        let functionKind =
                            ["FunctionDecl "; "CXXMethodDecl "; "CXXConstructorDecl "]
                            |> List.tryPick (fun kind -> let index = line.IndexOf(kind, StringComparison.Ordinal) in if index < 0 then None else Some (kind.Trim(), index))
                        match functionKind with
                        | Some (kind, depth) ->
                            let quote = line.IndexOf('\'')
                            let endQuote = if quote < 0 then -1 else line.IndexOf('\'', quote + 1)
                            if endQuote < 0 then scopes, current, evidence else
                            let name = line.Substring(0, quote).Trim().Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries) |> Array.last
                            let signature = line.Substring(quote + 1, endQuote - quote - 1) |> normalize
                            let key =
                                if hasParentContext then None
                                else parentScope |> Option.map (fun scope -> scope, kind, name, signature)
                            scopes, (key, depth, [], 0) :: current, evidence
                        | None ->
                            let attribute = line.IndexOf("NonNullAttr ", StringComparison.Ordinal)
                            let parameter = line.IndexOf("ParmVarDecl ", StringComparison.Ordinal)
                            match current with
                            | (key, depth, pointers, count) :: parents when parameter = depth + 2 ->
                                let pointers = if pointerParameter line then count :: pointers else pointers
                                scopes, (key, depth, pointers, count + 1) :: parents, evidence
                            | (Some key, depth, pointers, _) :: _ when attribute = depth + 2 ->
                                let rangeEnd = line.LastIndexOf('>')
                                if rangeEnd < 0 then
                                    if dumpError.IsNone then dumpError <- Some (Diagnostics.uncoded "Failed to parse clang output: Clang NonNullAttr lacks a semantic source range")
                                    scopes, current, evidence
                                else
                                    let positions =
                                        line.Substring(rangeEnd + 1).Split([|' '; '\t'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                                        |> Array.choose (fun token ->
                                            match Int32.TryParse token with
                                            | true, index when index > 0 -> Some (index - 1)
                                            | _ when token = "Inherited" || token = "Implicit" -> None
                                            | _ ->
                                                if dumpError.IsNone then dumpError <- Some (Diagnostics.uncoded $"Failed to parse clang output: Cannot read clang nonnull position '{token}'")
                                                None)
                                        |> Array.toList
                                        |> fun positions -> if positions.IsEmpty then List.rev pointers else positions
                                    let previous = Map.tryFind key evidence |> Option.defaultValue []
                                    scopes, current, Map.add key (positions :: previous) evidence
                            | _ -> scopes, current, evidence) ([], [], Map.empty)
                match dumpError with
                | Some refusal -> Error refusal
                | None -> Ok collected)
        fun scope node (f: FunctionDecl) ->
            if f.Attributes |> List.exists (fun a -> a.Kind = "NonNullAttr") then
                match scope, getString node "parentDeclContextId" with
                | Some scope, None ->
                    match evidence.Value with
                    | Error refusal -> Error refusal
                    | Ok evidenceMap ->
                        let signature = getQualType node |> normalize
                        match Map.tryFind (scope, getStringOr node "kind" "", f.Name, signature) evidenceMap with
                        | None -> parseFailure $"Cannot recover clang nonnull parameter evidence for '{f.Name}'"
                        | Some positions ->
                            let indices = positions |> List.collect id |> List.distinct |> List.sort
                            if indices |> List.exists (fun index -> index < 0 || index >= f.Parameters.Length) then
                                parseFailure $"Clang nonnull parameter evidence is out of range for '{f.Name}'"
                            else
                                Ok { f with Attributes = f.Attributes |> List.map (fun a -> if a.Kind = "NonNullAttr" then { a with Args = indices } else a) }
                // Out-of-line declarations can have a different semantic parent.
                // A lexical key cannot establish their identity.
                | _ -> parseFailure $"Cannot establish lexical clang nonnull declaration scope for '{f.Name}'"
            else Ok f

    /// Run clang preprocessor for macros
    let private runClangMacros (options: HeaderParserOptions) : Result<string, string> =
        let args = buildClangArgs options
        runClang args ["-E"; "-dM"; options.HeaderFile] options.Verbose

    /// Run clang with -fdump-record-layouts-simple to extract struct layout info.
    /// Generates a temporary translation unit that forces layout computation for the named structs
    /// by referencing sizeof() for each. Layout data appears on stderr.
    /// A refusal keeps its structured diagnostics (#186): a malformed define is FS9124, refused
    /// before clang runs; a failure that is no FS91xx refusal carries its text only.
    let extractStructLayoutsForModeDiagnosed
        (headerFile: string) (includePaths: string list) (systemIncludePaths: string list) (defines: string list)
        (structNames: string list) (cppMode: bool) (verbose: bool)
        : Result<Map<string, StructLayoutInfo>, Diagnostics.Refusal> =

        match requireWellFormedDefines defines with
        | Error refusal -> Error refusal
        | Ok () ->
        if structNames.IsEmpty then Ok Map.empty
        else

        let tempDir = Path.Combine(ScratchDirectory.root (), "farscape-layout")
        Directory.CreateDirectory(tempDir) |> ignore
        // Each clang invocation must measure its own header, including concurrent probes.
        let tempFile = Path.Combine(tempDir, "_farscape_layout_probe_" + System.Guid.NewGuid().ToString("N") + ".c")

        let result =
            try
                // Generate a C file that forces layout computation via sizeof()
                let includeDirective = $"#include \"{headerFile}\""
                let sizeofRefs =
                    structNames
                    |> List.mapi (fun i name ->
                        let cType =
                            if name.StartsWith("typedef ") then name.Substring(8)
                            elif name.StartsWith("union ") || name.StartsWith("struct ") then name
                            else $"struct {name}"
                        $"void *_fs_layout_{i} = (void*)sizeof({cType});")
                    |> String.concat "\n"
                File.WriteAllText(tempFile, $"{includeDirective}\n{sizeofRefs}\n")

                let args = ResizeArray<string>()
                if cppMode then args.AddRange(["-x"; "c++"; "-std=c++17"])
                args.AddRange(searchArguments includePaths systemIncludePaths defines)
                for arg in ["-Wno-unused-variable"; "-Xclang"; "-fdump-record-layouts-simple"; "-fsyntax-only"; tempFile] do
                    args.Add(arg)

                let startInfo = clangStartInfo args

                if verbose then
                    printfn "[CppParser] Layout extraction: clang %s" (commandLine args)

                use proc = Process.Start(startInfo)
                let stdout = proc.StandardOutput.ReadToEnd()
                let stderr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()

                if verbose then
                    printfn "[CppParser] Layout stderr: %d bytes, exit code: %d" stderr.Length proc.ExitCode

                if proc.ExitCode <> 0 then
                    Error (Diagnostics.uncoded $"clang layout probe for '{headerFile}' exited with code {proc.ExitCode}: {stderr.Trim()}")
                else
                    match parseRecordLayouts (stdout + "\n" + stderr) with
                    | Error reason -> Error (Diagnostics.uncoded $"Record layout dump could not be parsed: {reason}")
                    | Ok layouts ->
                        let requestedKeys =
                            structNames
                            |> List.map (fun name -> name.Replace("typedef ", "").Replace("struct ", "").Replace("union ", ""))
                            |> List.distinct
                        let filtered =
                            requestedKeys
                            |> List.choose (fun key -> layouts |> Map.tryFind key |> Option.map (fun l -> (key, l)))
                            |> Map.ofList
                        match requestedKeys |> List.filter (fun key -> not (filtered.ContainsKey key)) with
                        | [] -> Ok filtered
                        | missing ->
                            let names = String.concat ", " (missing |> List.map (sprintf "'%s'"))
                            Error (Diagnostics.uncoded $"clang layout probe for '{headerFile}' produced no record layout for {names}")
            with ex ->
                Error (Diagnostics.uncoded $"Failed to extract struct layouts: {ex.Message}")
        try File.Delete(tempFile) with _ -> ()
        result

    /// Measure in the detected language when no explicit parse mode is supplied.
    let extractStructLayoutsDiagnosed headerFile includePaths systemIncludePaths defines structNames verbose =
        extractStructLayoutsForModeDiagnosed headerFile includePaths systemIncludePaths defines structNames (detectCppMode headerFile) verbose

    /// `extractStructLayoutsDiagnosed` with no system include paths, reporting a failure as its text.
    let extractStructLayouts
        (headerFile: string) (includePaths: string list) (defines: string list)
        (structNames: string list) (verbose: bool)
        : Result<Map<string, StructLayoutInfo>, string> =
        extractStructLayoutsDiagnosed headerFile includePaths [] defines structNames verbose |> Result.mapError _.Text

    // =========================================================================
    // Enum Representation Measurement (clang evaluates sizeof and signedness)
    // =========================================================================

    /// Parse clang's filtered text AST of the probe variables named `<prefix><kind>_<i> 'char[N]'` (`_fs_enum_`, `_fs_byvalue_`): each variable's array extent, which clang evaluated.
    let private parseProbeExtents (prefix: string) (output: string) : Map<string, int> =
        output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let at = line.IndexOf(" " + prefix)
            if not (line.Contains("VarDecl")) || at < 0 then None
            else
                let rest = line.Substring(at + 1)
                let space = rest.IndexOf(' ')
                let typeStart = rest.IndexOf("'char[")
                if space < 0 || typeStart < 0 then None
                else
                    let close = rest.IndexOf("]'", typeStart)
                    match Int32.TryParse(rest.Substring(typeStart + 6, close - typeStart - 6)) with
                    | true, n when close > typeStart -> Some (rest.Substring(0, space), n)
                    | _ -> None)
        |> Map.ofArray

    /// Measure each named enum of a parsed unit with clang in that same unit: the probe includes
    /// the header under the parse's own flags and declares, per enum, `char size[sizeof(E)]` and
    /// a sign array whose extent clang evaluates: `((E)-1 < 0) ? 1 : 2` in C, and
    /// `__is_signed(__underlying_type(E)) ? 1 : 2` in C++ (clang's builtins for
    /// `std::is_signed_v<std::underlying_type_t<E>>`), which also covers scoped enums and enums
    /// with a fixed underlying type. Clang's own evaluation is read back from its AST, so
    /// ordinary, `packed`, wide, fixed and scoped enums carry the representation clang chose.
    /// A batch clang cannot compile is measured one enum at a time; an enum clang still cannot
    /// measure (an incomplete C enum, or one the probe cannot name at file scope) stays
    /// `ClangUnmeasured`, and the generator refuses any use of it.
    let measureEnums (options: HeaderParserOptions) (typedefNamedEnums: Set<string>) (declarations: Declaration list)
        : Declaration list =
        let spellings =
            declarations
            |> List.choose (function
                | Enum e when e.Name <> "" ->
                    Some (if typedefNamedEnums.Contains e.Name then e.Name else "enum " + e.Name)
                | _ -> None)
            |> List.distinct
        if spellings.IsEmpty then declarations
        else
        let tempDir = Path.Combine(ScratchDirectory.root (), "farscape-enum")
        Directory.CreateDirectory(tempDir) |> ignore
        let probe (batch: (int * string) list) : Result<Map<string, EnumRepresentation>, string> =
            let tempFile = Path.Combine(tempDir, "_farscape_enum_probe_" + Guid.NewGuid().ToString("N") + ".c")
            try
                let body =
                    batch |> List.map (fun (i, spelling) ->
                        let signed =
                            if options.CppMode then $"__is_signed(__underlying_type({spelling}))"
                            else $"(({spelling})-1 < 0)"
                        $"char _fs_enum_size_{i}[sizeof({spelling})]; char _fs_enum_sign_{i}[{signed} ? 1 : 2];")
                    |> String.concat "\n"
                File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\n{body}\n")
                match runClang (buildClangArgs options) ["-Xclang"; "-ast-dump"; "-Xclang"; "-ast-dump-filter=_fs_enum_"; "-fsyntax-only"; tempFile] options.Verbose with
                | Error error -> Error error
                | Ok output ->
                    let facts = parseProbeExtents "_fs_enum_" output
                    batch |> List.fold (fun state (i, spelling) ->
                        state |> Result.bind (fun (acc: Map<string, EnumRepresentation>) ->
                            match facts.TryFind $"_fs_enum_size_{i}", facts.TryFind $"_fs_enum_sign_{i}" with
                            | Some bytes, Some sign when sign = 1 || sign = 2 ->
                                Ok (acc.Add(spelling, { Bits = bytes * 8; IsSigned = (sign = 1) }))
                            | _ -> Error $"clang reported no representation for '{spelling}'")) (Ok Map.empty)
            finally
                try File.Delete tempFile with _ -> ()
        let indexed = spellings |> List.mapi (fun i spelling -> i, spelling)
        let measured =
            match probe indexed with
            | Ok facts -> facts
            | Error _ ->
                indexed |> List.fold (fun (acc: Map<string, EnumRepresentation>) item ->
                    match probe [item] with
                    | Ok facts -> Map.fold (fun m k v -> Map.add k v m) acc facts
                    | Error _ -> acc) Map.empty
        declarations
        |> List.map (function
            | Enum e when e.Name <> "" ->
                let spelling = if typedefNamedEnums.Contains e.Name then e.Name else "enum " + e.Name
                Enum { e with Representation = match measured.TryFind spelling with Some m -> ClangMeasured m | None -> ClangUnmeasured }
            | decl -> decl)

    // =========================================================================
    // Object Macro Evaluation (clang evaluates each definition in its unit, #193)
    // =========================================================================

    /// A definition that is one integer literal (`42`, `0x1F`, `(-1)`, `8u`): the generator
    /// reads it as written, so clang is not asked.
    let private plainIntegerLiteral =
        System.Text.RegularExpressions.Regex(@"^\(*\s*[-+]?\s*(0[xX][0-9a-fA-F]+|0[bB][01]+|[0-9]+)[uUlL]*\s*\)*$")

    /// A plain integer literal whose magnitude fits the signed 64-bit carrier: the generator
    /// reads it as written. A wider one is left to clang, which reports its value.
    let private plainLiteralFits (definition: string) =
        let m = plainIntegerLiteral.Match definition
        m.Success &&
            (let digits = m.Groups.[1].Value
             let magnitude =
                 if digits.StartsWith "0x" || digits.StartsWith "0X" then
                     bigint.Parse("0" + digits.Substring 2, Globalization.NumberStyles.HexNumber)
                 elif digits.StartsWith "0b" || digits.StartsWith "0B" then
                     digits.Substring 2 |> Seq.fold (fun acc c -> acc * 2I + (if c = '1' then 1I else 0I)) 0I
                 else bigint.Parse digits
             magnitude <= bigint Int64.MaxValue)

    /// A definition holding tokens no expression has (a brace, a semicolon) or unbalanced
    /// parentheses: placed in a probe it would swallow the declarations after it.
    let private notAnExpression (definition: string) =
        definition.IndexOfAny([| '{'; '}'; ';' |]) >= 0
        || (definition |> Seq.sumBy (function '(' -> 1 | ')' -> -1 | _ -> 0)) <> 0

    /// What clang reports for each evaluated object macro of one probe.
    type private MacroFacts = {
        /// The integer clang evaluates for `(NAME)` as an enumerator.
        Values: Map<int, bigint>
        /// The type clang gives `(NAME)`: its spelling and its canonical spelling.
        Types: Map<int, string * string>
        /// The integer clang evaluates for `(__INTPTR_TYPE__)(NAME)`, a pointer's address.
        Addresses: Map<int, bigint>
        /// Clang's first error on the line evaluating `(NAME)` as an enumerator.
        Errors: Map<int, string>
    }

    /// Evaluate the object macros at `indexed` with clang in the unit the header's parse ran in
    /// (its own flags), one probe for all: per macro an enumerator `(NAME)` whose value clang
    /// evaluates, an `extern __typeof__((NAME))` whose type clang names, and an enumerator
    /// `(__INTPTR_TYPE__)(NAME)` whose value is a constant pointer's address. Each declaration is
    /// on its own line, so clang's errors (with `-ferror-limit=0`) are read back by line.
    let private probeObjectMacros (options: HeaderParserOptions) (indexed: (int * string) list) : Result<MacroFacts, string> =
        let tempDir = Path.Combine(ScratchDirectory.root (), "farscape-macro")
        Directory.CreateDirectory(tempDir) |> ignore
        let tempFile = Path.Combine(tempDir, "_farscape_macro_probe_" + Guid.NewGuid().ToString("N") + (if options.CppMode then ".cpp" else ".c"))
        try
            // Line 1 includes the header; macro i's three declarations follow on lines
            // 2 + 3k, 3 + 3k and 4 + 3k, where k is its position in the probe.
            let lines =
                indexed |> List.collect (fun (i, name) ->
                    [ $"enum {{ _fs_macro_value_{i} = ({name}) }};"
                      $"extern __typeof__(({name})) _fs_macro_type_{i};"
                      $"enum {{ _fs_macro_address_{i} = (__INTPTR_TYPE__)({name}) }};" ])
            File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\n" + String.concat "\n" lines + "\n")
            let valueLine = indexed |> List.mapi (fun k (i, _) -> 2 + 3 * k, i) |> Map.ofList
            let args = ResizeArray<string>(buildClangArgs options)
            for arg in [ "-ferror-limit=0"; "-fno-caret-diagnostics"; "-fno-color-diagnostics"; "-w"
                         "-Xclang"; "-ast-dump"; "-Xclang"; "-ast-dump-filter=_fs_macro_"; "-fsyntax-only"; tempFile ] do
                args.Add arg
            if options.Verbose then
                printfn "[CppParser] Macro evaluation: clang %s" (commandLine args)
            use proc = Process.Start(clangStartInfo args)
            // Errors are expected (a string is no enumerator): read both streams to their end.
            let stderrTask = proc.StandardError.ReadToEndAsync()
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = stderrTask.Result
            proc.WaitForExit()
            let number (prefix: string) =
                let pattern = System.Text.RegularExpressions.Regex(@"^Dumping " + prefix + @"(\d+):")
                let value = System.Text.RegularExpressions.Regex(@"value: Int (-?\d+)")
                let blocks = stdout.Split([| '\n' |])
                let mutable current = None
                let mutable found = Map.empty
                for line in blocks do
                    let m = pattern.Match line
                    if m.Success then current <- Some (int m.Groups.[1].Value)
                    elif line.StartsWith "Dumping " then current <- None
                    else
                        match current with
                        | Some i when not (found.ContainsKey i) ->
                            let v = value.Match line
                            if v.Success then found <- found.Add(i, bigint.Parse v.Groups.[1].Value)
                        | _ -> ()
                found
            // `__typeof__` keeps its operand as sugar (`typeof ((X))`): the type's own spelling is
            // read from the first sugared function-pointer type in the address probe's block,
            // which is the type the macro casts to (`sqlite3_destructor_type`).
            let castSpellings =
                let pattern = System.Text.RegularExpressions.Regex(@"^Dumping _fs_macro_address_(\d+):")
                let sugared = System.Text.RegularExpressions.Regex(@"'([^']*)':'([^']*\(\s*\*\s*\)\s*\([^']*)'")
                let mutable current = None
                let mutable found = Map.empty
                for line in stdout.Split([| '\n' |]) do
                    let m = pattern.Match line
                    if m.Success then current <- Some (int m.Groups.[1].Value)
                    elif line.StartsWith "Dumping " then current <- None
                    else
                        match current with
                        | Some i when not (found.ContainsKey i) ->
                            let t = sugared.Match line
                            if t.Success && not (t.Groups.[1].Value.StartsWith "typeof") then found <- found.Add(i, t.Groups.[1].Value)
                        | _ -> ()
                found
            let types =
                let pattern = System.Text.RegularExpressions.Regex(@"VarDecl .* _fs_macro_type_(\d+) '([^']*)'(?::'([^']*)')?")
                stdout.Split([| '\n' |])
                |> Array.choose (fun line ->
                    let m = pattern.Match line
                    if not m.Success then None
                    else
                        let i = int m.Groups.[1].Value
                        let canonical = if m.Groups.[3].Success then m.Groups.[3].Value else m.Groups.[2].Value
                        let spelled = castSpellings |> Map.tryFind i |> Option.defaultValue canonical
                        Some (i, (spelled, canonical)))
                |> Map.ofArray
            // An error clang places in the probe is on its macro's line; one it places inside a
            // header's macro body is followed by notes, the probe line among them.
            let errors =
                let located = System.Text.RegularExpressions.Regex(@"^(.*?):(\d+):\d+: (error|note): (.*)$")
                let full = Path.GetFullPath tempFile
                let mutable pending = None
                let mutable found = Map.empty
                for line in stderr.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
                    let m = located.Match line
                    if m.Success then
                        let inProbe = (try Path.GetFullPath m.Groups.[1].Value = full with _ -> false)
                        let lineNumber = int m.Groups.[2].Value
                        let attribute message =
                            match valueLine.TryFind lineNumber with
                            | Some i when not (found.ContainsKey i) -> found <- found.Add(i, message)
                            | _ -> ()
                        match m.Groups.[3].Value with
                        | "error" when inProbe -> attribute m.Groups.[4].Value; pending <- None
                        | "error" -> pending <- Some m.Groups.[4].Value
                        | _ when inProbe -> pending |> Option.iter attribute
                        | _ -> ()
                found
            Ok { Values = number "_fs_macro_value_"; Types = types; Addresses = number "_fs_macro_address_"; Errors = errors }
        finally
            try File.Delete tempFile with _ -> ()

    /// Each object macro of a unit as clang evaluates it (#193): a composed integer (or an alias
    /// of one) becomes `IntegerConstant` with clang's value; a cast of an integer to a
    /// function-pointer type becomes `FunctionPointerSentinel` (#192); a string literal
    /// `StringConstant`; anything else `Unevaluated` with clang's reason. A plain integer
    /// literal, an empty definition and a function-like macro are left as written, and a
    /// pointer cast clang finds no sentinel in stays `TypeCast`. No macro is dropped here.
    let evaluateObjectMacros (options: HeaderParserOptions) (macros: MacroDecl list) : MacroDecl list =
        let definitionOf (m: MacroDecl) =
            match m.Kind with
            | SimpleValue v when v <> "" && not (plainLiteralFits v) -> Some v
            | Expression v when not (plainLiteralFits v) -> Some v
            | TypeCast _ -> Some m.RawValue
            | _ -> None
        let candidates = macros |> List.indexed |> List.choose (fun (i, m) -> definitionOf m |> Option.map (fun d -> i, m, d))
        if candidates.IsEmpty then macros
        else
        let probed, refused = candidates |> List.partition (fun (_, _, d) -> not (notAnExpression d))
        let run batch =
            match probeObjectMacros options (batch |> List.map (fun (i, (m: MacroDecl), _) -> i, m.Name)) with
            | Ok facts -> facts
            | Error _ -> { Values = Map.empty; Types = Map.empty; Addresses = Map.empty; Errors = Map.empty }
        let batch = run probed
        // A macro the batch gives neither a value nor an error of its own is probed alone, so no
        // neighbour's failure stands in for its own.
        let silent = probed |> List.filter (fun (i, _, _) -> not (batch.Values.ContainsKey i) && not (batch.Errors.ContainsKey i))
        let alone =
            if silent.Length > 32 then Map.empty
            else silent |> List.map (fun (i, m, d) -> i, run [ i, m, d ]) |> Map.ofList
        let factsOf i = alone |> Map.tryFind i |> Option.defaultValue batch
        let int64Of (v: bigint) = if v >= bigint Int64.MinValue && v <= bigint Int64.MaxValue then Some (int64 v) else None
        let functionPointer = System.Text.RegularExpressions.Regex(@"\(\s*\*\s*\)\s*\(")
        let stringLiteral (d: string) =
            let t = d.TrimStart('(', ' ', '\t')
            t.StartsWith "\"" || t.StartsWith "L\"" || t.StartsWith "u8\"" || t.StartsWith "u\"" || t.StartsWith "U\""
        let evaluated =
            (refused |> List.map (fun (i, m, d) ->
                i, Unevaluated (d, "its definition holds a brace, a semicolon or unbalanced parentheses, so it is no expression clang can evaluate")))
            @ (probed |> List.map (fun (i, m, d) ->
                let facts = factsOf i
                let kind =
                    match facts.Values.TryFind i |> Option.map (fun v -> v, int64Of v), facts.Types.TryFind i with
                    | Some (_, Some v), _ -> IntegerConstant (d, v)
                    | Some (v, None), _ -> Unevaluated (d, $"clang evaluates it to {v}, outside the signed 64-bit carrier of a constant")
                    | None, Some (spelled, canonical) when functionPointer.IsMatch canonical && (facts.Addresses.TryFind i |> Option.bind int64Of).IsSome ->
                        FunctionPointerSentinel (d, spelled, canonical, (facts.Addresses.[i] |> int64Of).Value)
                    | None, Some (_, canonical) when canonical.EndsWith "]" && stringLiteral d -> StringConstant (d, canonical)
                    | None, _ ->
                        match m.Kind, facts.Errors.TryFind i with
                        | TypeCast _, _ -> m.Kind
                        | _, Some error -> Unevaluated (d, $"clang: {error}")
                        | _ -> Unevaluated (d, "clang gives it no integer value and no diagnostic")
                i, kind))
            |> Map.ofList
        macros |> List.mapi (fun i m -> match evaluated.TryFind i with Some kind -> { m with Kind = kind } | None -> m)

    // =========================================================================
    // Typedef Attributes a Record Reaches (#175)
    // =========================================================================

    let private identifierSpelling = System.Text.RegularExpressions.Regex(@"[A-Za-z_][A-Za-z0-9_]*")

    /// The attributes of every typedef a type spelling reaches, with the typedef carrying each
    /// (#175): the typedefs it names and, through each one's underlying type, the typedefs those
    /// name, in order. A tag name (`struct T`, `union T`, `enum T`) names no typedef. A pointer
    /// spelling reaches none: a pointer's layout is a pointer's, whatever typedef its pointee is.
    let typedefAttributesOf (typedefs: Map<string, TypedefInfo>) (spelling: string) : (string * AttributeData) list =
        let visited = System.Collections.Generic.HashSet<string>()
        let rec reach (spelling: string) : (string * AttributeData) list =
            if spelling.Contains '*' then []
            else
                let tokens = identifierSpelling.Matches spelling |> Seq.map _.Value |> List.ofSeq
                let previous = None :: (tokens |> List.map Some) |> List.truncate tokens.Length
                let typedefNames =
                    List.zip previous tokens
                    |> List.choose (fun (previous, token) ->
                        match previous with
                        | Some ("struct" | "union" | "enum") -> None
                        | _ -> Some token)
                typedefNames |> List.collect (fun name ->
                    match typedefs.TryFind name with
                    | Some t when visited.Add name -> (t.Attributes |> List.map (fun a -> name, a)) @ reach t.UnderlyingType
                    | _ -> [])
        reach spelling

    /// Record on each named record of a unit the typedef attributes it reaches (#175), from the
    /// typedefs of that same unit, its own and those reached through its includes: the
    /// attributes of the typedefs each field's type reaches (`typedefAttributesOf`), and those of
    /// the typedef that names a record C leaves untagged.
    let annotateTypedefAttributes (typedefNamed: Set<string>) (unit: Declaration list) (declarations: Declaration list) : Declaration list =
        let typedefs =
            (Map.empty, unit) ||> List.fold (fun (acc: Map<string, TypedefInfo>) decl ->
                match decl with
                | Typedef t when not (acc.ContainsKey t.Name) -> acc.Add(t.Name, t)
                | _ -> acc)
        if typedefs |> Map.forall (fun _ t -> t.Attributes.IsEmpty) then declarations
        else
        declarations |> List.map (function
            | Struct s when s.Name <> "" ->
                let named =
                    match s.Anonymous, typedefs.TryFind s.Name with
                    | None, Some t when typedefNamed.Contains s.Name ->
                        t.Attributes |> List.map (fun a -> { Site = RecordName; Typedef = t.Name; Attribute = a })
                    | _ -> []
                let fields =
                    s.Fields |> List.collect (fun f ->
                        typedefAttributesOf typedefs f.Type |> List.map (fun (typedef, a) -> { Site = FieldType f.Name; Typedef = typedef; Attribute = a }))
                Struct { s with TypedefAttributes = named @ fields }
            // A C++ class's fields reach typedefs as a C record's do (#182).
            | Class c when c.Name <> "" ->
                let fields =
                    c.Fields |> List.collect (fun f ->
                        typedefAttributesOf typedefs f.Type |> List.map (fun (typedef, a) -> { Site = FieldType f.Name; Typedef = typedef; Attribute = a }))
                Class { c with TypedefAttributes = fields }
            | decl -> decl)

    /// A type spelling with each typedef that carries or reaches an attribute replaced by its
    /// underlying type, recursively (#181): `fx_wide` (`typedef int fx_wide
    /// __attribute__((aligned(16)))`) becomes `int`, and `const fx_pair16` (`typedef struct
    /// fx_pair fx_pair16 __attribute__((aligned(16)))`) becomes `const struct fx_pair`. The result
    /// reaches no attributed typedef: it is the type a descriptor claims for the spelling. A tag
    /// name (`struct T`) names no typedef, and a typedef reaching no attribute is kept as spelled.
    let unattributedSpelling (typedefs: Map<string, TypedefInfo>) (spelling: string) : string =
        let rec strip (visited: Set<string>) (spelling: string) =
            identifierSpelling.Replace(spelling, System.Text.RegularExpressions.MatchEvaluator(fun m ->
                let before = spelling.Substring(0, m.Index).TrimEnd()
                let tagged = ["struct"; "union"; "enum"; "class"] |> List.exists (fun tag ->
                    before.EndsWith tag && (before.Length = tag.Length || not (Char.IsLetterOrDigit before[before.Length - tag.Length - 1] || before[before.Length - tag.Length - 1] = '_')))
                match typedefs.TryFind m.Value with
                | Some t when not tagged && not (visited.Contains t.Name) && not (typedefAttributesOf typedefs t.Name).IsEmpty ->
                    strip (visited.Add t.Name) t.UnderlyingType
                | _ -> m.Value))
        strip Set.empty spelling

    /// Measure with clang, in the parsed unit, every by-value parameter and result of the unit's
    /// own functions whose type reaches a typedef carrying an attribute (#181), from the typedefs
    /// of that same unit, its own and those reached through its includes. The probe includes the
    /// header under the parse's own flags and declares, for the spelled type and for its
    /// unattributed type (`unattributedSpelling`), `char size[sizeof(T)]` and `char
    /// align[_Alignof(T)]` (`alignof` in C++), and reads clang's evaluation back from its AST. A
    /// pointer, reference, array or function spelling is no by-value use of a typedef: a
    /// pointer's layout is a pointer's; its pointee, which a reference projection's descriptor
    /// claims, is measured as its own site. A batch clang cannot compile is measured one type at a
    /// time; a type clang still cannot measure carries that reason, and the generator refuses it.
    let measureByValueTypedefs (options: HeaderParserOptions) (unit: Declaration list) (declarations: Declaration list)
        : Declaration list =
        let typedefs =
            (Map.empty, unit) ||> List.fold (fun (acc: Map<string, TypedefInfo>) decl ->
                match decl with
                | Typedef t when not (acc.ContainsKey t.Name) -> acc.Add(t.Name, t)
                | _ -> acc)
        let byValue (spelling: string) =
            not (spelling.Contains '*' || spelling.Contains '(' || spelling.Contains '&' || spelling.Contains '[')
        // A pointer parameter's pointee, spelled as a reference projection's descriptor claims it
        // (`FidelityCodeGenerator.generateDescriptorDecls`), is measured too.
        let pointee (cType: string) =
            if not (cType.Contains '*') || cType.Contains '(' then None
            else Some (cType.Substring(0, cType.LastIndexOf '*').Trim())
        let sitesOf (f: FunctionDecl) =
            ("result", f.ReturnType)
            :: (f.Parameters |> List.collect (fun (name, cType) ->
                    ($"parameter '{name}'", cType)
                    :: (pointee cType |> Option.map (fun element -> $"pointee of parameter '{name}'", element) |> Option.toList)))
            |> List.choose (fun (site, spelling) ->
                if not (byValue spelling) then None
                else
                    match typedefAttributesOf typedefs spelling with
                    | [] -> None
                    | reached -> Some (site, spelling, reached, unattributedSpelling typedefs spelling))
        if typedefs |> Map.forall (fun _ t -> t.Attributes.IsEmpty) then declarations
        else
        let sites = declarations |> List.collect (function Function f -> sitesOf f | _ -> [])
        if sites.IsEmpty then declarations
        else
        let spellings = sites |> List.collect (fun (_, spelling, _, unattributed) -> [spelling; unattributed]) |> List.distinct |> List.indexed
        let alignOf = if options.CppMode then "alignof" else "_Alignof"
        let tempDir = Path.Combine(ScratchDirectory.root (), "farscape-byvalue")
        Directory.CreateDirectory(tempDir) |> ignore
        let probe (batch: (int * string) list) : Map<string, TypeExtent> =
            let tempFile = Path.Combine(tempDir, "_farscape_byvalue_probe_" + Guid.NewGuid().ToString("N") + ".c")
            try
                let body =
                    batch |> List.map (fun (i, spelling) ->
                        $"char _fs_byvalue_size_{i}[sizeof({spelling})]; char _fs_byvalue_align_{i}[{alignOf}({spelling})];")
                    |> String.concat "\n"
                File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\n{body}\n")
                match runClang (buildClangArgs options) ["-Xclang"; "-ast-dump"; "-Xclang"; "-ast-dump-filter=_fs_byvalue_"; "-fsyntax-only"; tempFile] options.Verbose with
                | Error _ -> Map.empty
                | Ok output ->
                    let facts = parseProbeExtents "_fs_byvalue_" output
                    batch |> List.choose (fun (i, spelling) ->
                        match facts.TryFind $"_fs_byvalue_size_{i}", facts.TryFind $"_fs_byvalue_align_{i}" with
                        | Some size, Some alignment -> Some (spelling, { Size = size; Alignment = alignment })
                        | _ -> None)
                    |> Map.ofList
            finally
                try File.Delete tempFile with _ -> ()
        let measured =
            let batch = probe spellings
            if batch.Count = spellings.Length then batch
            else spellings |> List.fold (fun acc item -> Map.fold (fun m k v -> Map.add k v m) acc (probe [item])) Map.empty
        let extentOf (spelling: string) (role: string) =
            match measured.TryFind spelling with
            | Some extent -> Ok extent
            | None -> Error $"clang measures no size and alignment of '{spelling}'{role} in its declaring unit"
        declarations |> List.map (function
            | Function f ->
                let measuredSites =
                    sitesOf f |> List.map (fun (site, spelling, reached, unattributed) ->
                        let measurement =
                            match extentOf spelling "", extentOf unattributed ", the type its typedefs stand for" with
                            | Ok spelled, Ok plain -> Ok (spelled, plain)
                            | Error reason, _ | _, Error reason -> Error reason
                        { Site = site; Spelling = spelling; Reached = reached; Unattributed = unattributed; Measured = measurement })
                Function { f with ByValueTypedefs = measuredSites }
            | decl -> decl)

    // =========================================================================
    // Record Measurement in the Declaring Unit (anonymous records; attribute contracts)
    // =========================================================================

    /// Measure every anonymous record of a parsed unit, every record enclosing one, every record
    /// carrying a layout-neutral attribute, every record whose field type reaches an attributed
    /// typedef (#175) and every record carrying an unnamed bit-field (#176), with clang in that
    /// same unit (issues #159, #174). A typedef's attribute and an unnamed bit-field move bytes
    /// that natural alignment of the declared field types cannot place, so such a record's
    /// layout is clang's or none, and the generator refuses it without one. A layout-neutral
    /// attribute (`AttributeContracts.LayoutNeutral`, `may_alias`) is admitted only when this measurement agrees with the record's layout without it, so the
    /// record must be measured where it is declared, and so must its copy without the attribute:
    /// the same probe declares `struct __farscape_unattributed_<n>` with one field per member,
    /// in order, each declared `__typeof__(((R*)0)->member) member`, which keeps the member's
    /// type with its typedefs and their attributes and leaves out only the record's own
    /// attributes. Each name the copy spells is `#undef`ined first, because the AST names are
    /// the members' real names. A record C leaves unnamed, a bit-field (which `__typeof__`
    /// cannot read), an anonymous member and an unnamed bit-field (which have no name to read
    /// them through) have no faithful copy, and the record carries that reason instead. The probe includes the header under the
    /// parse's own flags and takes `sizeof` of each outermost named enclosing record (by its typedef
    /// name when C names it only by a typedef), which makes clang lay out it and every record
    /// nested in it; `-fdump-record-layouts-simple` prints those layouts, the same dump measured
    /// ABI-critical layouts come from. An enclosing record is the dump block of its own name; an
    /// anonymous record is the block clang names `Parent::(unnamed at path:line:col)` at the
    /// location its field's type spells, and an anonymous member's record (#173) the block
    /// `Parent::(anonymous at path:line:col)` at its own location. A record with no such block,
    /// or with two blocks of different layouts (two unnamed records one macro expansion spells
    /// at one location), stays
    /// unmeasured, and the generator refuses it (FS9113): an anonymous record's layout is never
    /// computed from its declared fields. A C++ class carrying an unnamed bit-field or whose field
    /// type reaches an attributed typedef is measured the same way (#182), by its qualified name,
    /// which its dump block names; a class no probe at file scope names stays unmeasured, and
    /// the generator refuses it. A batch clang cannot compile is measured one record at a time.
    let measureRecordLayouts (options: HeaderParserOptions) (typedefNamed: Set<string>) (declarations: Declaration list)
        : Declaration list =
        let records = declarations |> List.choose (function Struct s when s.Name <> "" -> Some s | _ -> None)
        let anonymous = records |> List.filter (fun s -> s.Anonymous.IsSome)
        let attributed =
            records |> List.filter (fun s ->
                isDefinedRecord s
                && (s.Attributes @ (s.Fields |> List.collect _.Attributes)) |> List.exists (fun a -> (AttributeContracts.neutralFact a.Kind).IsSome))
        // A record whose field type reaches an attributed typedef (#175), or carrying an unnamed
        // bit-field (#176): its layout is clang's, never computed from its declared fields.
        let reaching = records |> List.filter (fun s -> s.TypedefAttributes |> List.exists (fun a -> a.Site <> RecordName))
        let padded = records |> List.filter (fun s -> not s.UnnamedBitFields.IsEmpty)
        let byName = records |> List.rev |> List.map (fun s -> s.Name, s) |> Map.ofList
        let classesByName =
            declarations
            |> List.choose (function Class c when c.Name <> "" -> Some (c.Name, c) | _ -> None)
            |> List.rev
            |> Map.ofList
        let rec outermostClass (visited: Set<string>) (s: StructDecl) =
            match s.Anonymous with
            | None -> None
            | Some origin ->
                match classesByName.TryFind origin.Parent with
                | Some c -> Some c
                | None when not (visited.Contains origin.Parent) ->
                    byName.TryFind origin.Parent |> Option.bind (outermostClass (visited.Add s.Name))
                | None -> None
        let enclosingClasses =
            anonymous |> List.choose (outermostClass Set.empty) |> List.choose _.QualifiedName
        // C++ classes carrying an unnamed bit-field, reaching an attributed typedef (#182), or
        // enclosing an anonymous record (#177), by the qualified name their dump block carries.
        let classes =
            ((declarations
              |> List.choose (function
                  | Class c when c.Name <> "" && (not c.UnnamedBitFields.IsEmpty || not c.TypedefAttributes.IsEmpty) -> c.QualifiedName
                  | _ -> None))
             @ enclosingClasses)
            |> List.distinct
        if anonymous.IsEmpty && attributed.IsEmpty && reaching.IsEmpty && padded.IsEmpty && classes.IsEmpty then declarations
        else
        let rec outermost (visited: Set<string>) (s: StructDecl) =
            match s.Anonymous with
            | None -> Some s
            | Some origin when not (visited.Contains origin.Parent) ->
                byName.TryFind origin.Parent |> Option.bind (outermost (visited.Add s.Name))
            | Some _ -> None
        let roots = (anonymous @ attributed @ reaching @ padded) |> List.choose (outermost Set.empty) |> List.distinctBy _.Name
        let spelling (s: StructDecl) =
            if typedefNamed.Contains s.Name then s.Name
            elif s.IsUnion then "union " + s.Name
            else "struct " + s.Name
        // The copy of each attributed record without its attributes (#174), or why none is faithful.
        let copyName (ordinal: int) = $"__farscape_unattributed_{ordinal}"
        let copies : (StructDecl * Result<int * string * string list, string>) list =
            attributed |> List.mapi (fun ordinal s ->
                let anonymousMember =
                    s.Fields |> List.tryFind (fun f ->
                        records |> List.exists (fun r ->
                            r.Anonymous |> Option.exists (fun o -> o.Member && o.Parent = s.Name && o.Field = f.Name)))
                match s.Anonymous, s.Fields |> List.tryFind _.IsBitfield, anonymousMember, s.UnnamedBitFields with
                | Some _, _, _, _ -> s, Error "C leaves the record unnamed, so no copy can name it to read its members' types"
                | None, Some f, _, _ -> s, Error $"field '{f.Name}' is a bit-field, whose type __typeof__ cannot read"
                | None, None, Some f, _ -> s, Error $"'{f.Name}' is an anonymous member, which has no C name a copy could read its type through"
                | None, None, None, b :: _ -> s, Error $"{describeUnnamedBitField b} has no member name a copy could read its type through"
                | None, None, None, [] ->
                    let tag = if s.IsUnion then "union" else "struct"
                    let members = s.Fields |> List.map (fun f -> $"__typeof__((({spelling s}*)0)->{f.Name}) {f.Name};") |> String.concat " "
                    let names = s.Name :: (s.Fields |> List.map _.Name)
                    s, Ok (ordinal, $"{tag} {copyName ordinal} {{ {members} }}; char _fs_copy_{ordinal}[sizeof({tag} {copyName ordinal})];", names))
        let synthesized = copies |> List.choose (fun (_, copy) -> match copy with Ok c -> Some c | Error _ -> None)
        let tempDir = Path.Combine(ScratchDirectory.root (), "farscape-layout")
        Directory.CreateDirectory(tempDir) |> ignore
        let probe (batch: string list) (copyBatch: (int * string * string list) list) : Result<StructLayoutInfo list, string> =
            let tempFile = Path.Combine(tempDir, "_farscape_record_probe_" + Guid.NewGuid().ToString("N") + ".c")
            try
                let body = batch |> List.mapi (fun i spelled -> $"char _fs_record_{i}[sizeof({spelled})];") |> String.concat "\n"
                let undefs = copyBatch |> List.collect (fun (_, _, names) -> names) |> List.distinct |> List.map (fun n -> $"#undef {n}") |> String.concat "\n"
                let copyText = copyBatch |> List.map (fun (_, text, _) -> text) |> String.concat "\n"
                File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\n{body}\n{undefs}\n{copyText}\n")
                runClang (buildClangArgs options) ["-Xclang"; "-fdump-record-layouts-simple"; "-fsyntax-only"; tempFile] options.Verbose
                |> Result.bind parseRecordLayoutBlocks
            finally
                try File.Delete tempFile with _ -> ()
        let spelled = (roots |> List.map spelling) @ classes
        let blocks =
            match probe spelled synthesized with
            | Ok blocks -> blocks
            | Error _ ->
                (spelled |> List.collect (fun root -> match probe [root] [] with Ok blocks -> blocks | Error _ -> []))
                @ (synthesized |> List.collect (fun copy -> match probe [] [copy] with Ok blocks -> blocks | Error _ -> []))
        // A field's unnamed record is dumped `P::(unnamed at loc)` (Clang 22+) or
        // `P::(unnamed struct|union at loc)` (Clang 18); an anonymous member's record is dumped
        // `P::(anonymous at loc)` or `P::(anonymous struct|union at loc)`.
        let unnamedAt (marker: string) (dumpName: string) =
            let at = dumpName.LastIndexOf marker
            if at < 0 || not (dumpName.EndsWith ")") then None
            else clangLocation (dumpName.Substring(at + marker.Length, dumpName.Length - at - marker.Length - 1))
        let measuredOf (s: StructDecl) =
            let candidates =
                match s.Anonymous with
                | Some origin ->
                    match clangLocation origin.Location with
                    | Some location ->
                        let markers =
                            if origin.Member then [ "(anonymous at "; "(anonymous struct at "; "(anonymous union at " ]
                            else [ "(unnamed at "; "(unnamed struct at "; "(unnamed union at " ]
                        blocks |> List.filter (fun block -> markers |> List.exists (fun marker -> unnamedAt marker block.Name = Some location))
                    | None -> []
                | None -> blocks |> List.filter (fun block -> block.Name = s.Name)
            match candidates |> List.distinctBy (fun l -> l.SizeBits, l.DataSizeBits, l.AlignmentBits, l.FieldOffsetsBits) with
            | [ layout ] -> Some { layout with Name = s.Name }
            | _ -> None
        let unattributedOf =
            copies |> List.map (fun (s, copy) ->
                let evidence =
                    match copy with
                    | Error reason -> UnattributedUnsynthesized reason
                    | Ok (ordinal, _, _) ->
                        match blocks |> List.filter (fun block -> block.Name = copyName ordinal)
                                     |> List.distinctBy (fun l -> l.SizeBits, l.DataSizeBits, l.AlignmentBits, l.FieldOffsetsBits) with
                        | [ layout ] -> UnattributedMeasured { layout with Name = s.Name }
                        | [] -> UnattributedUnsynthesized "clang measured no copy of it in its declaring unit"
                        | _ -> UnattributedUnsynthesized "clang dumped more than one layout under the copy's name"
                s.Name, evidence)
            |> Map.ofList
        let measured = Set.ofList ((roots @ anonymous @ attributed @ reaching @ padded) |> List.map _.Name)
        let classMeasured (qualified: string) =
            match blocks |> List.filter (fun block -> block.Name = qualified)
                         |> List.distinctBy (fun l -> l.SizeBits, l.DataSizeBits, l.AlignmentBits, l.FieldOffsetsBits) with
            | [ layout ] -> Some layout
            | _ -> None
        declarations |> List.map (function
            | Struct s when s.Name <> "" && measured.Contains s.Name ->
                Struct { s with Measured = measuredOf s; Unattributed = Map.tryFind s.Name unattributedOf }
            | Class c when c.QualifiedName |> Option.exists (fun q -> List.contains q classes) ->
                Class { c with Measured = c.QualifiedName |> Option.bind classMeasured |> Option.map (fun layout -> { layout with Name = c.Name }) }
            | decl -> decl)

    /// The names Farscape gives anonymous records must name nothing else in their unit: no
    /// other record, typedef or enum, own or reached through an include, and no other
    /// anonymous record (`a_b` + `c` and `a` + `b_c`). Otherwise the unit is refused (FS9114).
    let anonymousNameCollisions (header: string) (declarations: Declaration list) : Diagnostics.Diagnostic list =
        let generated = declarations |> List.choose (function Struct s when s.Anonymous.IsSome -> Some s | _ -> None)
        if generated.IsEmpty then []
        else
        let others =
            declarations |> List.choose (function
                | Struct s when s.Name <> "" && s.Anonymous.IsNone && isDefinedRecord s -> Some (s.Name, "record")
                | Struct s when s.Name <> "" && s.Anonymous.IsNone -> Some (s.Name, "record declaration")
                | Class c when c.Name <> "" && (not c.Fields.IsEmpty || not c.UnnamedBitFields.IsEmpty || not c.Methods.IsEmpty || not c.Constructors.IsEmpty || not c.BaseClasses.IsEmpty) -> Some (c.Name, "record")
                | Class c when c.Name <> "" -> Some (c.Name, "record declaration")
                | Typedef t -> Some (t.Name, "typedef")
                | Enum e when e.Name <> "" -> Some (e.Name, "enum")
                | _ -> None)
        let describe (s: StructDecl) =
            match s.Anonymous with
            | Some o when o.Member -> $"the record of anonymous member '{o.Field}' of '{o.Parent}' at {o.Location}"
            | Some o -> $"the unnamed record of field '{o.Field}' of '{o.Parent}' at {o.Location}"
            | None -> s.Name
        generated
        |> List.groupBy _.Name
        |> List.choose (fun (name, named) ->
            let records = named |> List.distinctBy (fun s -> s.Anonymous)
            let clashes =
                (records |> List.map describe)
                @ (others |> List.filter (fun (other, _) -> other = name) |> List.distinct |> List.map (fun (_, kind) -> $"a {kind}"))
            if clashes.Length < 2 then None
            else
                let listed = clashes |> String.concat "; "
                // The first generated record is the source; the others the refusal names follow.
                let related = records |> List.tail |> List.choose (fun s -> match s.Location.Location with Diagnostics.Located.Known l -> Some l | _ -> None)
                Diagnostics.diagnostic Diagnostics.AnonymousNameCollision
                    $"the generated record name '{name}' in '{header}' names more than one declaration: {listed}. A generated name must name one record."
                    (List.head records).Location.Location
                |> Diagnostics.withRelated related
                |> Some)

    // =========================================================================
    // Clang Built-in Type Aliases in Results (clang canonicalizes and measures them)
    // =========================================================================

    /// A type name clang spells as a function's result that no declaration of the unit names
    /// (#171): clang's predefined sugar such as `__size_t`, which clang gives the library
    /// functions it knows (`strlen`) and which source cannot name. `Canonical` is clang's
    /// canonical type of that result with the size and signedness clang measured for it, or
    /// the reason clang gave none.
    type ClangTypeAlias = {
        Name: string
        /// The first function whose result clang spells with the alias.
        Function: string
        Canonical: Result<string * EnumRepresentation, AliasFailure>
    }

    /// Why clang gave a result alias no canonical type. The probe types an unevaluated call,
    /// so it reads results of functions whose by-value parameters are complete types (no
    /// arguments, fixed arguments and the fixed arguments of a variadic function); a function
    /// taking an incomplete type by value cannot be called, even unevaluated.
    and AliasFailure =
        /// A by-value parameter's type is incomplete in the unit: clang refuses `sizeof` of it.
        | IncompleteParameter of parameter: string * spelled: string * reason: string
        /// Clang refused the probe for another reason (the name hidden by an object-like macro,
        /// a result clang cannot measure); `reason` is clang's first error.
        | ProbeRefused of reason: string

    /// C's own type keywords: never aliases.
    let private cTypeKeywords =
        set [ "void"; "char"; "short"; "int"; "long"; "float"; "double"; "signed"; "unsigned"; "_Bool"; "bool"
              "wchar_t"; "char8_t"; "char16_t"; "char32_t"; "__int128"; "_Float16"; "_Float32"; "_Float64"
              "_Float128"; "__float128"; "__fp16"; "__bf16"; "_Complex" ]

    /// Read every result alias of a parsed unit through clang, in that same unit and under the
    /// parse's own flags. A function's result is an alias candidate when its spelling, without
    /// cv-qualifiers, is one identifier that is no C type keyword and that no typedef, record or
    /// enum of the unit (its own or reached through an include) declares. For each candidate
    /// function the probe declares `__typeof__((f)(args))` (the name parenthesized, so no
    /// function-like macro expands, and each argument `*(__typeof__(T) *)0`, never evaluated),
    /// a `sizeof` array of it and the sign array `((R)-1 < 0) ? 1 : 2`, and reads clang's own
    /// canonical type and evaluations back from its AST. One alias name must canonicalize and
    /// measure identically through every function that spells it. A candidate clang cannot
    /// evaluate keeps the reason, and a use of it is refused (FS9117).
    let measureResultAliases (options: HeaderParserOptions) (declarations: Declaration list) (included: Declaration list)
        : ClangTypeAlias list =
        if options.CppMode then []
        else
        let declared =
            (declarations @ included) |> List.choose (function
                | Typedef t -> Some t.Name
                | Struct s when s.Name <> "" -> Some s.Name
                | Enum e when e.Name <> "" -> Some e.Name
                | Class c -> Some c.Name
                | _ -> None)
            |> Set.ofList
        let identifier = System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$")
        let candidates =
            declarations
            |> List.choose (function
                | Function f ->
                    let spelled = f.ReturnType.Replace("const ", "").Replace("volatile ", "").Trim()
                    if identifier.IsMatch spelled && not (cTypeKeywords.Contains spelled) && not (declared.Contains spelled)
                    then Some (spelled, f) else None
                | _ -> None)
            |> List.distinctBy (fun (_, f) -> f.Name)
        if candidates.IsEmpty then []
        else
        let tempDir = Path.Combine(ScratchDirectory.root (), "farscape-alias")
        Directory.CreateDirectory(tempDir) |> ignore
        let call (f: FunctionDecl) =
            let args = f.Parameters |> List.map (fun (_, t) -> $"*(__typeof__({t}) *)0") |> String.concat ", "
            $"__typeof__(({f.Name})({args}))"
        let firstError (text: string) =
            let at = text.IndexOf("error: ")
            if at < 0 then text.Trim()
            else
                let rest = text.Substring(at + 7)
                match rest.IndexOfAny([| '\n'; '\r' |]) with
                | -1 -> rest.Trim()
                | stop -> rest.Substring(0, stop).Trim()
        let probe (batch: (int * FunctionDecl) list) : Result<Map<int, string * EnumRepresentation>, string> =
            let tempFile = Path.Combine(tempDir, "_farscape_alias_probe_" + Guid.NewGuid().ToString("N") + ".c")
            try
                let body =
                    batch |> List.map (fun (i, f) ->
                        let result = call f
                        $"{result} _fs_alias_{i}; char _fs_alias_size_{i}[sizeof({result})]; char _fs_alias_sign_{i}[(({result})-1 < 0) ? 1 : 2];")
                    |> String.concat "\n"
                File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\n{body}\n")
                match runClang (buildClangArgs options) ["-Xclang"; "-ast-dump"; "-Xclang"; "-ast-dump-filter=_fs_alias_"; "-fsyntax-only"; tempFile] options.Verbose with
                | Error error -> Error (firstError error)
                | Ok output ->
                    let lines = output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                    let sizes = parseProbeExtents "_fs_alias_" output
                    batch |> List.fold (fun state (i, f) ->
                        state |> Result.bind (fun (acc: Map<int, string * EnumRepresentation>) ->
                            let canonical =
                                lines |> Array.tryPick (fun line ->
                                    let marker = $" _fs_alias_{i} '"
                                    let at = line.IndexOf marker
                                    if not (line.Contains "VarDecl") || at < 0 then None
                                    else
                                        let typeText = line.Substring(at + marker.Length - 1)
                                        // `'sugared':'canonical'`, or one quoted type already canonical.
                                        let parts = typeText.Split([| "':'" |], StringSplitOptions.None)
                                        let last = parts.[parts.Length - 1].TrimStart('\'')
                                        match last.IndexOf('\'') with
                                        | -1 -> None
                                        | close -> Some (last.Substring(0, close)))
                            match canonical, sizes.TryFind $"_fs_alias_size_{i}", sizes.TryFind $"_fs_alias_sign_{i}" with
                            | Some c, Some bytes, Some sign when sign = 1 || sign = 2 ->
                                Ok (acc.Add(i, (c, { Bits = bytes * 8; IsSigned = (sign = 1) })))
                            | _ -> Error $"clang reported no canonical type and measurement for the result of '{f.Name}'")) (Ok Map.empty)
            finally
                try File.Delete tempFile with _ -> ()
        let indexed = candidates |> List.mapi (fun i (_, f) -> i, f)
        // The first by-value parameter clang cannot take `sizeof` of: an incomplete type.
        let incompleteParameter (f: FunctionDecl) =
            f.Parameters |> List.tryPick (fun (name, t) ->
                if t.Contains '*' || t.Contains '(' || t.Contains '[' || t.Contains '&' then None
                else
                    let tempFile = Path.Combine(tempDir, "_farscape_alias_param_" + Guid.NewGuid().ToString("N") + ".c")
                    try
                        File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\nchar _fs_alias_param[sizeof({t})];\n")
                        match runClang (buildClangArgs options) ["-fsyntax-only"; tempFile] options.Verbose with
                        | Error reason -> Some (IncompleteParameter (name, t, firstError reason))
                        | Ok _ -> None
                    finally
                        try File.Delete tempFile with _ -> ())
        let results : Map<int, Result<string * EnumRepresentation, AliasFailure>> =
            match probe indexed with
            | Ok facts -> facts |> Map.map (fun _ fact -> Ok fact)
            | Error _ ->
                indexed |> List.map (fun (i, f) ->
                    i, (match probe [(i, f)] with
                        | Ok facts when facts.ContainsKey i -> Ok facts.[i]
                        | Ok _ -> Error (ProbeRefused $"clang reported no canonical type and measurement for the result of '{f.Name}'")
                        | Error reason ->
                            match incompleteParameter f with
                            | Some failure -> Error failure
                            | None -> Error (ProbeRefused $"clang could not evaluate the result type of '{f.Name}' at file scope: {reason}")))
                |> Map.ofList
        candidates
        |> List.mapi (fun i (name, f) -> name, f.Name, results.[i])
        |> List.groupBy (fun (name, _, _) -> name)
        |> List.map (fun (name, uses) ->
            let _, first, firstResult = List.head uses
            let canonical =
                match uses |> List.tryPick (fun (_, f, r) -> match r with Error reason -> Some (f, reason) | Ok _ -> None) with
                | Some (_, reason) -> Error reason
                | None ->
                    match uses |> List.map (fun (_, _, r) -> r) |> List.distinct with
                    | [ single ] -> single
                    | _ ->
                        let listed = uses |> List.map (fun (_, f, r) -> match r with Ok (c, m) -> $"'{f}': {c} ({m.Bits}-bit)" | Error _ -> $"'{f}'") |> String.concat ", "
                        Error (ProbeRefused $"clang gives the results that spell it different canonical types: {listed}")
            ignore firstResult
            { Name = name; Function = first; Canonical = canonical })

    // =========================================================================
    // Include Tree Discovery + Macro Documentation Enrichment
    // =========================================================================

    /// Parse clang -H output (from stderr) to get the include file list.
    /// Format: ". /path/to/file" with dots indicating depth.
    let private parseIncludeList (clangHOutput: string) : string list =
        clangHOutput.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let trimmed = line.TrimStart('.')
            let path = trimmed.TrimStart()
            if File.Exists path then Some path else None)
        |> Array.distinct
        |> List.ofArray

    /// Run clang -H to discover the include file tree (files listed on stderr).
    let private runClangIncludes (options: HeaderParserOptions) : string list =
        let clangArgs = buildClangArgs options
        for arg in ["-H"; "-E"; "-o"; "/dev/null"; options.HeaderFile] do
            clangArgs.Add arg

        let startInfo = clangStartInfo clangArgs

        try
            use proc = Process.Start(startInfo)
            proc.StandardOutput.ReadToEnd() |> ignore
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()
            options.HeaderFile :: parseIncludeList stderr
        with _ ->
            [options.HeaderFile]

    /// Build a complete macro name → documentation map by scanning raw header files.
    /// Uses clang -H to discover the include tree, then reads each file's #define comments.
    let private buildMacroDocumentationMap (options: HeaderParserOptions) : Map<string, string> =
        let files = runClangIncludes options
        files
        |> List.map extractMacroCommentsFromFile
        |> List.fold (fun acc map ->
            Map.fold (fun s k v -> Map.add k v s) acc map) Map.empty

    /// Enrich a list of MacroDecl with documentation from raw header comments.
    let private enrichMacrosWithDocumentation
        (docMap: Map<string, string>)
        (macros: MacroDecl list)
        : MacroDecl list =
        macros
        |> List.map (fun m ->
            match Map.tryFind m.Name docMap with
            | Some doc -> { m with Documentation = Some doc }
            | None -> m)

    // =========================================================================
    // Public API
    // =========================================================================

    /// A header template declaration is evidence of a recipe, never of an exported ABI.
    type TemplateDeclaration = {
        TemplateName: string
        Kind: string
        Location: Diagnostics.Located<Diagnostics.SourceLocation>
    }

    let private templateDeclarations (header: string) (root: JsonValue) =
        let files = System.Collections.Generic.Dictionary<string, (byte[] * int[]) option>()
        let mutable currentFile = None
        let rec walk scope node = [
            match getObject node "loc" |> Option.bind (fun loc -> getString loc "file") with
            | Some path -> currentFile <- Some (Path.GetFullPath path)
            | None -> ()
            let kind = getStringOr node "kind" ""
            let name = getStringOr node "name" ""
            let named = name <> "" && not (getBool node "isImplicit")
            let template =
                List.contains kind ["ClassTemplateDecl"; "FunctionTemplateDecl"; "ClassTemplateSpecializationDecl"]
            if template && named && currentFile = Some (Path.GetFullPath header) then
                yield {
                    TemplateName = String.concat "::" (scope @ [name])
                    Kind = kind
                    Location = (nodeProvenance files currentFile node).Location
                }
            // Function bodies, anonymous scopes and specialization bodies cannot supply
            // an unambiguous qualified primary-template name.
            if kind = "TranslationUnitDecl" || kind = "LinkageSpecDecl" then
                for child in getArray node "inner" do yield! walk scope child
            elif named && List.contains kind ["NamespaceDecl"; "CXXRecordDecl"; "RecordDecl"; "ClassTemplateDecl"] then
                let nested = scope @ [name]
                for child in getArray node "inner" do
                    if template && getStringOr child "name" "" = name && getStringOr child "kind" "" = "CXXRecordDecl" then
                        for memberNode in getArray child "inner" do yield! walk nested memberNode
                    else
                        yield! walk nested child
        ]
        walk [] root |> List.distinctBy (fun declaration -> declaration.TemplateName, declaration.Kind)

    /// Read template recipes from clang JSON without treating them as binary instantiations.
    let templateDeclarationsOfAst (header: string) (astJson: string) =
        templateDeclarations header (JsonValue.Parse astJson)

    /// Result of parsing containing both AST declarations and macros
    type ParseResult = {
        Declarations: Declaration list
        Templates: TemplateDeclaration list
        Macros: MacroDecl list
        /// Typedefs and enums (enums measured in this unit) the header's own parse reaches
        /// through includes outside the library boundary; facts for resolving this unit's
        /// declarations, never declarations of the unit.
        Included: Declaration list
        /// Clang built-in type aliases this unit's function results spell, read through clang.
        Aliases: ClangTypeAlias list
    }

    /// Parse a C/C++ header file and extract all declarations including macros. A failure
    /// carries the diagnostics of each FS91xx refusal with the exact text `parseHeaderFull`
    /// reports (#184).
    let parseHeaderFullDiagnosed (options: HeaderParserOptions) : Result<ParseResult, Diagnostics.Refusal> =
        match requireWellFormedDefines options.Defines with
        | Error refusal -> Error refusal
        | Ok () ->
        if not (File.Exists(options.HeaderFile)) then
            Error (Diagnostics.uncoded $"Header file not found: {options.HeaderFile}")
        else
            if options.Verbose then
                printfn "[CppParser] Parsing header: %s" options.HeaderFile

            // Pass 1: AST for structs, enums, typedefs, functions
            match runClangAst options with
            | Error err -> Error (Diagnostics.uncoded err)
            | Ok jsonOutput ->
                try
                    if options.Verbose then
                        printfn "[CppParser] Parsing JSON AST..."

                    let root = JsonValue.Parse(jsonOutput)
                    let targetFile = Path.GetFileName(options.HeaderFile)

                    match walkAst root options.HeaderFile targetFile options.IncludeRoot options.Verbose (enrichNonNullAttributes options) with
                    | Error refusal -> Error refusal
                    | Ok (declarations, typedefNamedTags, included) ->
                        // Included enums are measured in this same unit, with its own enums.
                        let measured = measureEnums options typedefNamedTags (declarations @ included)
                        let declarations, included = List.splitAt declarations.Length measured
                        match anonymousNameCollisions options.HeaderFile (declarations @ included) with
                        | collisions when not collisions.IsEmpty ->
                            Error (Diagnostics.refusalsOf collisions "")
                        | _ ->
                            // Each record's typedef attributes, from this unit's typedefs (#175).
                            let declarations = annotateTypedefAttributes typedefNamedTags (declarations @ included) declarations
                            // By-value parameters and results reaching an attributed typedef (#181),
                            // measured in this same unit.
                            let declarations = measureByValueTypedefs options (declarations @ included) declarations
                            // Anonymous records, the records enclosing them, records carrying a
                            // layout-neutral attribute, records whose field types reach an attributed
                            // typedef and records carrying an unnamed bit-field are measured in this
                            // same unit.
                            let declarations = measureRecordLayouts options typedefNamedTags declarations

                            if options.Verbose then
                                printfn "[CppParser] Extracted %d AST declarations" (List.length declarations)

                            // Pass 2: Macros (if requested)
                            let macros =
                                if options.IncludeMacros then
                                    match runClangMacros options with
                                    | Error err ->
                                        if options.Verbose then
                                            printfn "[CppParser] Warning: Failed to extract macros: %s" err
                                        []
                                    | Ok macroOutput ->
                                        let allMacros =
                                            macroOutput.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                                            |> Array.choose parseMacroLine
                                            |> Array.filter (fun m -> isUserMacro m.Name options.MacroPrefixes)
                                            |> List.ofArray
                                            // Each object macro as clang evaluates it in this unit (#193).
                                            |> evaluateObjectMacros options

                                        if options.Verbose then
                                            printfn "[CppParser] Extracted %d macros" (List.length allMacros)

                                        // Pass 3: Enrich macros with documentation from raw header comments
                                        let docMap = buildMacroDocumentationMap options
                                        let enriched = enrichMacrosWithDocumentation docMap allMacros

                                        if options.Verbose then
                                            let docCount = enriched |> List.filter (fun m -> m.Documentation.IsSome) |> List.length
                                            printfn "[CppParser] Enriched %d/%d macros with documentation" docCount (List.length enriched)

                                        enriched
                                else
                                    []

                            Ok {
                                Declarations = declarations
                                Templates = templateDeclarations options.HeaderFile root
                                Macros = macros
                                Included = included
                                Aliases = measureResultAliases options declarations included
                            }
                with
                | :? Diagnostics.RefusalException as refused -> Error (Diagnostics.prefixed "Failed to parse clang output: " refused.Refusal)
                | ex -> Error (Diagnostics.uncoded $"Failed to parse clang output: {ex.Message}")

    /// Parse a C/C++ header file and extract all declarations including macros
    let parseHeaderFull (options: HeaderParserOptions) : Result<ParseResult, string> =
        parseHeaderFullDiagnosed options |> Result.mapError _.Text

    /// The declarations Farscape reads from one clang JSON AST of `header` (`clang -Xclang
    /// -ast-dump=json`), named and refused as a parse names and refuses them (FS9114, FS9122,
    /// FS9123). It is internal to Farscape.Core and visible only to its test assembly: it has
    /// none of the parse's clang measurements or nonnull evidence, so its declarations must
    /// never reach generation (#173). It admits an AST clang produced under options no pilot
    /// can select (#178), such as `-fms-extensions`, whose tagged anonymous members a parse must
    /// refuse rather than drop.
    let internal declarationsOfAst (header: string) (astJson: string) : Result<Declaration list, string> =
        let root = JsonValue.Parse astJson
        match walkAst root header (Path.GetFileName header) None false (fun _ _ f -> Ok f) with
        | Error refusal -> Error refusal.Text
        | Ok (declarations, typedefNamed, included) ->
            match anonymousNameCollisions header (declarations @ included) with
            | [] -> Ok (annotateTypedefAttributes typedefNamed (declarations @ included) declarations)
            | collisions -> Error (Diagnostics.refusalsOf collisions "").Text

    /// Parse a C/C++ header as one translation unit: its declarations (with macros), and the
    /// typedefs and enums its parse reaches through includes outside the library boundary.
    let parseHeaderUnitDiagnosed (options: HeaderParserOptions) : Result<Declaration list * Declaration list * ClangTypeAlias list, Diagnostics.Refusal> =
        match parseHeaderFullDiagnosed options with
        | Error err -> Error err
        | Ok result ->
            let allDecls = result.Declarations @ (result.Macros |> List.map Macro)
            if allDecls.IsEmpty then
                Error (Diagnostics.uncoded $"Parse succeeded but no declarations found in {Path.GetFileName(options.HeaderFile)}.")
            else
                Ok (allDecls, result.Included, result.Aliases)

    /// `parseHeaderUnitDiagnosed`, reporting a failure as its text.
    let parseHeaderUnit (options: HeaderParserOptions) : Result<Declaration list * Declaration list * ClangTypeAlias list, string> =
        parseHeaderUnitDiagnosed options |> Result.mapError _.Text

    /// Parse a C/C++ header file and extract declarations (backward compatible)
    let parseHeader (options: HeaderParserOptions) : Result<Declaration list, string> =
        match parseHeaderFull options with
        | Error err -> Error err
        | Ok result ->
            let allDecls =
                result.Declarations @
                (result.Macros |> List.map Macro)

            if allDecls.IsEmpty then
                Error $"Parse succeeded but no declarations found in {Path.GetFileName(options.HeaderFile)}."
            else
                Ok allDecls

    /// Simplified parse function for common use cases
    let parse (headerFile: string) (includePaths: string list) (verbose: bool) : Result<Declaration list, string> =
        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            SystemIncludePaths = []
            Defines = []
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []
            IncludeRoot = None
            CppMode = detectCppMode headerFile
        }
        parseHeader options

    /// Parse with defines (useful for platform-specific headers like CMSIS)
    let parseWithDefines
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (verbose: bool) : Result<Declaration list, string> =

        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            SystemIncludePaths = []
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []
            IncludeRoot = None
            CppMode = detectCppMode headerFile
        }
        parseHeader options

    /// Parse with include root scoping for library boundary detection
    let parseWithIncludeRoot
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (includeRoot: string option)
        (macroPrefixes: string list)
        (verbose: bool) : Result<Declaration list, string> =

        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            SystemIncludePaths = []
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = macroPrefixes
            IncludeRoot = includeRoot
            CppMode = detectCppMode headerFile
        }
        parseHeader options

    /// Parse CMSIS header with appropriate options
    let parseCMSIS
        (headerFile: string)
        (includePaths: string list)
        (defines: string list)
        (verbose: bool) : Result<ParseResult, string> =

        let options = {
            HeaderFile = headerFile
            IncludePaths = includePaths
            SystemIncludePaths = []
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []
            IncludeRoot = None
            CppMode = false  // CMSIS headers are always C
        }
        parseHeaderFull options
