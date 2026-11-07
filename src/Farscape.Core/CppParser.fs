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
    }

    /// A measured layout's offsets of the record's fields: clang's member offsets without those
    /// of its unnamed bit-fields. Offsets clang dumps for a different number of members are
    /// returned as dumped, so the count check refuses them.
    let fieldOffsetsBits (s: StructDecl) (layout: StructLayoutInfo) : int list =
        if s.UnnamedBitFields.IsEmpty || layout.FieldOffsetsBits.Length <> s.Fields.Length + s.UnnamedBitFields.Length then
            layout.FieldOffsetsBits
        else
            let padding = s.UnnamedBitFields |> List.map _.Position |> Set.ofList
            layout.FieldOffsetsBits |> List.indexed |> List.filter (fun (position, _) -> not (padding.Contains position)) |> List.map snd

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
    exception AnonymousNameCollision of string

    /// A unit with an anonymous member Farscape cannot represent (FS9122) or whose generated
    /// member name names another member (FS9123); carries the refusal.
    exception AnonymousMemberRefused of string

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
    type MacroKind =
        | SimpleValue of string           // #define FOO 42
        | Expression of string            // #define FOO (BAR + 1)
        | FunctionLike of string list * string  // #define FOO(x,y) (x+y)
        | TypeCast of string * string     // #define GPIOA ((GPIO_TypeDef*)0x...)

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
    }

    // =========================================================================
    // Parser Options
    // =========================================================================

    /// Options for parsing a C/C++ header file
    type HeaderParserOptions = {
        /// Path to the header file to parse
        HeaderFile: string
        /// Additional include paths for resolving #include directives
        IncludePaths: string list
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
                let content = File.ReadAllText(headerFile)
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

    /// Process FunctionDecl AST node
    let private processFunctionDecl (node: JsonValue) : FunctionDecl option =
        match getString node "name" with
        | None | Some "" -> None
        | Some name ->
            let parameters =
                getArray node "inner"
                |> Seq.filter (fun inner ->
                    getStringOr inner "kind" "" = "ParmVarDecl")
                |> Seq.map (fun param ->
                    let paramName = getStringOr param "name" "param"
                    let paramType = getQualType param
                    (paramName, paramType))
                |> List.ofSeq

            let returnType = extractReturnType (getQualType node)
            let isStatic = getStringOr node "storageClass" "" = "static"
            let isInline = getBool node "inline"
            let attributes = extractAttributes node
            let documentation = extractDocumentation node
            let mangledName = getString node "mangledName"

            Some {
                Name = name
                ReturnType = returnType
                Parameters = parameters
                Documentation = documentation
                IsVirtual = false
                IsStatic = isStatic
                IsInline = isInline
                Attributes = attributes
                MangledName = mangledName
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
    let private processRecordDecl (node: JsonValue) : StructDecl option =
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

        match name, fields with
        | Some n, _ when not (String.IsNullOrEmpty(n)) ->
            Some { Name = n; Fields = fields; Documentation = documentation; IsUnion = isUnion; Attributes = attributes; Anonymous = None; Measured = None; Unattributed = None
                   UnnamedBitFields = padding; TypedefAttributes = [] }
        | _, fs when not fs.IsEmpty ->
            Some { Name = ""; Fields = fields; Documentation = documentation; IsUnion = isUnion; Attributes = attributes; Anonymous = None; Measured = None; Unattributed = None
                   UnnamedBitFields = padding; TypedefAttributes = [] }
        | _ -> None

    /// Clang spells the type of a field declared with an unnamed record as
    /// `struct (unnamed at path:line:col)` (or `union`), possibly cv-qualified or with an
    /// array extent; `loc` is the unnamed record's own location.
    let private unnamedRecordSpelling =
        System.Text.RegularExpressions.Regex(@"^(?<pre>.*?)\b(?<tag>struct|union) \(unnamed at (?<loc>[^()]+)\)(?<post>.*)$")

    /// Clang spells the type of a C11 anonymous member, the implicit unnamed field it declares,
    /// as `union P::(anonymous at path:line:col)`, scoped by every enclosing record (`P::(anonymous
    /// union)::`), possibly cv-qualified; `loc` is the anonymous record's own location.
    let private anonymousMemberSpelling =
        System.Text.RegularExpressions.Regex(@"^(?<pre>(?:const |volatile )*)(?<tag>struct|union) (?:.+::)?\(anonymous at (?<loc>[^()]+)\)$")

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
    let private nameAnonymousRecords (header: string) (parent: string) (node: JsonValue) (record: StructDecl) : StructDecl * StructDecl list =
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
        let rec name (parent: string) (node: JsonValue) (record: StructDecl) : StructDecl * StructDecl list =
            let nested = ResizeArray<StructDecl>()
            let fields = ResizeArray<FieldDecl>()
            let declared = record.Fields |> List.map (fun f -> f.Name, f) |> Map.ofList
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
            for child in getArray node "inner" do
                match getStringOr child "kind" "" with
                | "RecordDecl" when getStringOr child "name" "" = "" && getBool child "completeDefinition" ->
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
                                processRecordDecl declaration |> Option.map (fun record -> declaration, record)
                            | _ -> None
                        match paired with
                        | Some (declaration, inner) ->
                            let generated = anonymousRecordName parent field
                            let origin = { Parent = parent; Field = field; Location = spelled.Groups.["loc"].Value; Member = false }
                            let named, deeper = name generated declaration { inner with Name = generated; Anonymous = Some origin }
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
                        raise (AnonymousMemberRefused $"{Diagnostics.AnonymousMemberNameCollision}: the generated member name '{field}' of the anonymous member of '{parent}' declared at {location} in '{header}' names another member of that record. A generated name must name one member.")
                    let paired =
                        match unnamed with
                        | Some declaration when spelled.Success && pairs declaration spelled.Groups.["tag"].Value location ->
                            processRecordDecl declaration |> Option.map (fun record -> declaration, record)
                        | _ -> None
                    match paired with
                    | Some (declaration, inner) ->
                        let generated = anonymousRecordName parent field
                        let origin = { Parent = parent; Field = field; Location = location; Member = true }
                        let named, deeper = name generated declaration { inner with Name = generated; Anonymous = Some origin }
                        nested.Add named
                        nested.AddRange deeper
                        let spelling = spelled.Groups.["pre"].Value + spelled.Groups.["tag"].Value + " " + generated
                        fields.Add { parseFieldType spelling with Name = field; Attributes = extractAttributes child }
                    | None ->
                        raise (AnonymousMemberRefused $"{Diagnostics.AnonymousMemberUnrepresented}: an anonymous member of '{parent}' in '{header}', of clang type '{clangType}', cannot be paired with a record clang declares for it, so '{parent}' has no representation of it. An anonymous member is never dropped from its record.")
                | _ -> ()
            { record with Fields = List.ofSeq fields }, List.ofSeq nested
        name parent node record

    /// Extract the explicit value from an EnumConstantDecl AST node, if present.
    /// Returns None when the enum constant has no initializer (implicit sequential value).
    /// A value clang reports that the signed 64-bit carrier cannot hold (an unsigned 64-bit
    /// enumerator above 2^63-1) is refused rather than read as an implicit successor.
    let private extractEnumConstantValue (node: JsonValue) : int64 option =
        let value (element: JsonValue) =
            match getInt64 element "value", getString element "value" with
            | Some v, _ -> Some v
            | None, Some text when text <> "" ->
                let name = getStringOr node "name" "<unnamed>"
                failwith $"{Diagnostics.EnumValueRange}: enumerator '{name}' has value {text}, outside the signed 64-bit range the enum representation carries; a wider enumerator representation contract is required."
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
    let private processEnumDecl (node: JsonValue) : EnumDecl option =
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
                        match extractEnumConstantValue constNode with
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
            Some { Name = n; Values = values; Documentation = documentation; UnderlyingType = underlyingType; Representation = ClangUnmeasured }
        | _, vs when not vs.IsEmpty ->
            Some { Name = ""; Values = values; Documentation = documentation; UnderlyingType = underlyingType; Representation = ClangUnmeasured }
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
        (enrichFunction: (string * string) list option -> JsonValue -> FunctionDecl -> FunctionDecl)
        : Declaration list * Set<string> * Declaration list =

        let results = ResizeArray<Declaration>()
        // Enums and records named only by a typedef (`typedef enum { ... } T;`,
        // `typedef union { ... } T;`): spelled `T`, not `enum T` or `union T`.
        let typedefNamedTags = System.Collections.Generic.HashSet<string>()
        // Typedefs and enums this unit's own parse sees through its includes but whose files
        // lie outside the library boundary: facts of this same translation unit, kept for
        // resolving its own declarations, never emitted as its declarations.
        let included = ResizeArray<Declaration>()
        let mutable currentFile: string option = None
        let processFunction scope node =
            processFunctionDecl node |> Option.map (enrichFunction scope node)

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

        /// Update file tracking from a node's location.
        /// Clang's JSON AST uses "loc.file" to indicate a change in source file.
        /// When "file" is absent, the node is in the same file as the previous node.
        let updateFileTracking (node: JsonValue) =
            match getObject node "loc" with
            | Some loc ->
                match getString loc "file" with
                | Some f -> currentFile <- Some f
                | None -> ()
            | None -> ()

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
            updateFileTracking node

            let isImplicit = getBool node "isImplicit"
            let kind = getStringOr node "kind" ""
            // Only named lexical declaration contexts participate in the join.
            // Do not infer template identities or declarations inside function bodies.
            let childScope =
                match kind with
                | "NamespaceDecl" | "RecordDecl" | "CXXRecordDecl" ->
                    match getString node "parentDeclContextId", getString node "name" with
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
                match getString node "name" with
                | Some name when not (System.String.IsNullOrEmpty(name)) ->
                    for inner in getArray node "inner" do
                        processNode childScope inner
                | _ -> ()

            elif not isImplicit && isFromTargetFile node then
                if verbose then
                    let name = getStringOr node "name" "<anonymous>"
                    printfn "[CppParser] Processing %s: %s (file: %A)" kind name currentFile

                match kind with
                | "FunctionDecl" ->
                    match processFunction scope node with
                    | Some func -> results.Add(Function func)
                    | None -> ()

                | "RecordDecl" ->
                    match processRecordDecl node with
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
                            let named, nested = nameAnonymousRecords header name node { structDecl with Name = name }
                            results.Add(Struct named)
                            for record in nested do results.Add(Struct record)
                    | None -> ()

                | "EnumDecl" ->
                    match processEnumDecl node with
                    | Some enumDecl ->
                        let name =
                            if enumDecl.Name <> "" then enumDecl.Name
                            else
                                let named = getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames) |> Option.defaultValue ""
                                if named <> "" then typedefNamedTags.Add named |> ignore
                                named
                        results.Add(Enum { enumDecl with Name = name })
                    | None -> ()

                | "TypedefDecl" ->
                    match processTypedefDecl node with
                    | Some typedef -> results.Add(Typedef typedef)
                    | None -> ()

                | "CXXRecordDecl" ->
                    match getString node "name" with
                    | Some name when not (System.String.IsNullOrEmpty(name)) ->
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

                        let methods =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                let k = getStringOr inner "kind" ""
                                k = "CXXMethodDecl" || k = "FunctionDecl")
                            |> Seq.choose (processFunction childScope)
                            |> Seq.map Function
                            |> List.ofSeq

                        // Capture constructors (CXXConstructorDecl) as FunctionDecl.
                        // The constructor name in clang AST is the class name.
                        let constructors =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "CXXConstructorDecl")
                            |> Seq.choose (processFunction childScope)
                            |> Seq.map Function
                            |> List.ofSeq

                        // The C++ record path has no anonymous-member representation (#173): a
                        // class with one is refused rather than given a field list without it.
                        innerNodes
                        |> Seq.tryFind (fun inner ->
                            getStringOr inner "kind" "" = "FieldDecl" && getBool inner "isImplicit" && getStringOr inner "name" "" = "")
                        |> Option.iter (fun inner ->
                            raise (AnonymousMemberRefused $"{Diagnostics.AnonymousMemberUnrepresented}: C++ record '{name}' in '{header}' has an anonymous member of clang type '{getQualType inner}'; the C++ record path does not represent anonymous members, and none is dropped from its record."))

                        let fields =
                            innerNodes
                            |> Seq.filter (fun inner ->
                                getStringOr inner "kind" "" = "FieldDecl")
                            |> Seq.choose processFieldDecl
                            |> List.ofSeq

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
                                    let params =
                                        getArray inner "inner"
                                        |> Seq.filter (fun p -> getStringOr p "kind" "" = "ParmVarDecl")
                                        |> List.ofSeq
                                    params.Length = 1 &&
                                    (let t = getQualType params.[0]
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
                                    let params =
                                        getArray inner "inner"
                                        |> Seq.filter (fun p -> getStringOr p "kind" "" = "ParmVarDecl")
                                        |> List.ofSeq
                                    params.Length = 1 &&
                                    (let t = getQualType params.[0]
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
                        })
                    | _ -> ()

                | _ -> ()

                // Recurse into children for non-namespace declarations
                for inner in getArray node "inner" do
                    processNode childScope inner

            else
                if not isImplicit then
                    match kind with
                    | "EnumDecl" ->
                        match processEnumDecl node with
                        | Some enumDecl ->
                            let name =
                                if enumDecl.Name <> "" then enumDecl.Name
                                else
                                    let named = getString node "id" |> Option.bind (fun id -> Map.tryFind id anonymousRecordNames) |> Option.defaultValue ""
                                    if named <> "" then typedefNamedTags.Add named |> ignore
                                    named
                            if name <> "" then included.Add(Enum { enumDecl with Name = name })
                        | None -> ()
                    | "TypedefDecl" ->
                        match processTypedefDecl node with
                        | Some typedef -> included.Add(Typedef typedef)
                        | None -> ()
                    | _ -> ()
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
        results, Set.ofSeq typedefNamedTags, included

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
                do! optional ((pstring "struct " <|> pstring "union ") >>% ()) >>% ()
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

        static let pLayoutBlock =
            parser {
                let! name = pStructName
                do! skipMany pNewline
                do! pSkipLine // "Layout: <ASTRecordLayout" line
                let! size = pIntField "Size:"
                let! dataSize = pIntField "DataSize:"
                let! alignment = pIntField "Alignment:"
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
    let defineRefusal (define: string) : string option =
        if System.Text.RegularExpressions.Regex.IsMatch(define, @"\A[A-Za-z_][A-Za-z0-9_]*(=[^\r\n\x00]*)?\z") then None
        else Some $"{Diagnostics.DefineMalformed}: define '{define}' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option."

    /// Every malformed define of a list, refused together, or Ok.
    let requireWellFormedDefines (defines: string list) : Result<unit, string> =
        match defines |> List.choose defineRefusal |> List.distinct with
        | [] -> Ok ()
        | refusals -> Error (String.concat " " refusals)

    /// A clang process whose arguments are each exactly one argument (#178).
    let private clangStartInfo (args: string seq) = ProcessLaunch.startInfo "clang" args

    let private commandLine (args: string seq) = ProcessLaunch.commandLine args

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

        for includePath in options.IncludePaths do
            args.Add($"-I{includePath}")

        for define in options.Defines do
            args.Add($"-D{define}")

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
        let normalize (signature: string) =
            signature |> Seq.filter (not << Char.IsWhiteSpace) |> Seq.toArray |> String
        let evidence = lazy (
            let args = buildClangArgs options
            let output =
                match runClang args ["-Xclang"; "-ast-dump"; "-fsyntax-only"; options.HeaderFile] options.Verbose with
                | Ok text -> text
                | Error error -> failwith $"Cannot establish nonnull parameter evidence: {error}"
            let pointerParameter (line: string) =
                // Text AST supplies the canonical type after a typedef spelling.
                // nonnull without positions applies only to native pointer slots.
                let quoted = line.Split('\'')
                if quoted.Length < 3 then failwith "Clang parameter lacks its native type"
                let nativeType = quoted.[quoted.Length - 2].Trim()
                let lastStar = nativeType.LastIndexOf('*')
                let firstParen = nativeType.IndexOf('(')
                (lastStar >= 0 &&
                    (nativeType.Substring(lastStar + 1).Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
                     |> Array.forall (fun word -> List.contains word ["const"; "volatile"; "restrict"; "__restrict"; "__restrict__"])))
                || (firstParen >= 0 && not (nativeType.Substring(0, firstParen).Contains('<'))
                    && nativeType.Substring(firstParen + 1).TrimStart().StartsWith("*", StringComparison.Ordinal))
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
                            if rangeEnd < 0 then failwith "Clang NonNullAttr lacks a semantic source range"
                            let positions =
                                line.Substring(rangeEnd + 1).Split([|' '; '\t'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                                |> Array.choose (fun token ->
                                    match Int32.TryParse token with
                                    | true, index when index > 0 -> Some (index - 1)
                                    | _ when token = "Inherited" || token = "Implicit" -> None
                                    | _ -> failwith $"Cannot read clang nonnull position '{token}'")
                                |> Array.toList
                                |> fun positions -> if positions.IsEmpty then List.rev pointers else positions
                            let previous = Map.tryFind key evidence |> Option.defaultValue []
                            scopes, current, Map.add key (positions :: previous) evidence
                        | _ -> scopes, current, evidence) ([], [], Map.empty)
                |> fun (_, _, evidence) -> evidence)
        fun scope node (f: FunctionDecl) ->
            if f.Attributes |> List.exists (fun a -> a.Kind = "NonNullAttr") then
                let scope =
                    match scope, getString node "parentDeclContextId" with
                    | Some scope, None -> scope
                    // Out-of-line declarations can have a different semantic parent.
                    // A lexical key cannot establish their identity.
                    | _ -> failwith $"Cannot establish lexical clang nonnull declaration scope for '{f.Name}'"
                // The function type can retain a builtin's canonical typedef spelling
                // while ParmVarDecl uses the header spelling. Do not reconstruct it.
                let signature = getQualType node |> normalize
                let positions =
                    Map.tryFind (scope, getStringOr node "kind" "", f.Name, signature) evidence.Value
                    |> Option.defaultWith (fun () -> failwith $"Cannot recover clang nonnull parameter evidence for '{f.Name}'")
                let indices = positions |> List.collect id |> List.distinct |> List.sort
                if indices |> List.exists (fun index -> index < 0 || index >= f.Parameters.Length) then
                    failwith $"Clang nonnull parameter evidence is out of range for '{f.Name}'"
                { f with Attributes = f.Attributes |> List.map (fun a -> if a.Kind = "NonNullAttr" then { a with Args = indices } else a) }
            else f

    /// Run clang preprocessor for macros
    let private runClangMacros (options: HeaderParserOptions) : Result<string, string> =
        let args = buildClangArgs options
        runClang args ["-E"; "-dM"; options.HeaderFile] options.Verbose

    /// Run clang with -fdump-record-layouts-simple to extract struct layout info.
    /// Generates a temporary C file that forces layout computation for the named structs
    /// by referencing sizeof() for each. Layout data appears on stderr.
    let extractStructLayouts
        (headerFile: string) (includePaths: string list) (defines: string list)
        (structNames: string list) (verbose: bool)
        : Result<Map<string, StructLayoutInfo>, string> =

        match requireWellFormedDefines defines with
        | Error refusal -> Error refusal
        | Ok () ->
        if structNames.IsEmpty then Ok Map.empty
        else

        let tempDir = Path.Combine(Path.GetTempPath(), "farscape-layout")
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
                for ip in includePaths do args.Add($"-I{ip}")
                for d in defines do args.Add($"-D{d}")
                for arg in ["-Xclang"; "-fdump-record-layouts-simple"; "-fsyntax-only"; tempFile] do
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

                // Parse layout data from stderr (clang may return non-zero due to unused variables)
                match parseRecordLayouts (stdout + "\n" + stderr) with
                | Error reason -> Error $"Record layout dump could not be parsed: {reason}"
                | Ok layouts ->
                    // Filter to only the requested struct names
                    let filtered =
                        structNames
                        |> List.choose (fun name ->
                            let key = name.Replace("typedef ", "").Replace("struct ", "").Replace("union ", "")
                            layouts |> Map.tryFind key |> Option.map (fun l -> (key, l)))
                        |> Map.ofList

                    Ok filtered
            with ex ->
                Error $"Failed to extract struct layouts: {ex.Message}"
        try File.Delete(tempFile) with _ -> ()
        result

    // =========================================================================
    // Enum Representation Measurement (clang evaluates sizeof and signedness)
    // =========================================================================

    /// Parse clang's filtered text AST of the probe variables: `_fs_enum_<kind>_<i> 'char[N]'`.
    let private parseEnumProbe (output: string) : Map<string, int> =
        output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let at = line.IndexOf(" _fs_enum_")
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
        let tempDir = Path.Combine(Path.GetTempPath(), "farscape-enum")
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
                    let facts = parseEnumProbe output
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
    /// computed from its declared fields. A batch clang cannot compile is measured one record at
    /// a time.
    let measureRecordLayouts (options: HeaderParserOptions) (typedefNamed: Set<string>) (declarations: Declaration list)
        : Declaration list =
        let records = declarations |> List.choose (function Struct s when s.Name <> "" -> Some s | _ -> None)
        let anonymous = records |> List.filter (fun s -> s.Anonymous.IsSome)
        let attributed =
            records |> List.filter (fun s ->
                not s.Fields.IsEmpty
                && (s.Attributes @ (s.Fields |> List.collect _.Attributes)) |> List.exists (fun a -> (AttributeContracts.neutralFact a.Kind).IsSome))
        // A record whose field type reaches an attributed typedef (#175), or carrying an unnamed
        // bit-field (#176): its layout is clang's, never computed from its declared fields.
        let reaching = records |> List.filter (fun s -> s.TypedefAttributes |> List.exists (fun a -> a.Site <> RecordName))
        let padded = records |> List.filter (fun s -> not s.UnnamedBitFields.IsEmpty)
        if anonymous.IsEmpty && attributed.IsEmpty && reaching.IsEmpty && padded.IsEmpty then declarations
        else
        let byName = records |> List.rev |> List.map (fun s -> s.Name, s) |> Map.ofList
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
        let tempDir = Path.Combine(Path.GetTempPath(), "farscape-layout")
        Directory.CreateDirectory(tempDir) |> ignore
        let probe (batch: StructDecl list) (copyBatch: (int * string * string list) list) : Result<StructLayoutInfo list, string> =
            let tempFile = Path.Combine(tempDir, "_farscape_record_probe_" + Guid.NewGuid().ToString("N") + ".c")
            try
                let body = batch |> List.mapi (fun i s -> $"char _fs_record_{i}[sizeof({spelling s})];") |> String.concat "\n"
                let undefs = copyBatch |> List.collect (fun (_, _, names) -> names) |> List.distinct |> List.map (fun n -> $"#undef {n}") |> String.concat "\n"
                let copyText = copyBatch |> List.map (fun (_, text, _) -> text) |> String.concat "\n"
                File.WriteAllText(tempFile, $"#include \"{Path.GetFullPath options.HeaderFile}\"\n{body}\n{undefs}\n{copyText}\n")
                runClang (buildClangArgs options) ["-Xclang"; "-fdump-record-layouts-simple"; "-fsyntax-only"; tempFile] options.Verbose
                |> Result.bind parseRecordLayoutBlocks
            finally
                try File.Delete tempFile with _ -> ()
        let blocks =
            match probe roots synthesized with
            | Ok blocks -> blocks
            | Error _ ->
                (roots |> List.collect (fun root -> match probe [root] [] with Ok blocks -> blocks | Error _ -> []))
                @ (synthesized |> List.collect (fun copy -> match probe [] [copy] with Ok blocks -> blocks | Error _ -> []))
        // A field's unnamed record is dumped `P::(unnamed at loc)`, an anonymous member's
        // record `P::(anonymous at loc)`.
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
                        let marker = if origin.Member then "(anonymous at " else "(unnamed at "
                        blocks |> List.filter (fun block -> unnamedAt marker block.Name = Some location)
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
        declarations |> List.map (function
            | Struct s when s.Name <> "" && measured.Contains s.Name ->
                Struct { s with Measured = measuredOf s; Unattributed = Map.tryFind s.Name unattributedOf }
            | decl -> decl)

    /// The names Farscape gives anonymous records must name nothing else in their unit: no
    /// other record, typedef or enum, own or reached through an include, and no other
    /// anonymous record (`a_b` + `c` and `a` + `b_c`). Otherwise the unit is refused (FS9114).
    let anonymousNameCollisions (header: string) (declarations: Declaration list) : string list =
        let generated = declarations |> List.choose (function Struct s when s.Anonymous.IsSome -> Some s | _ -> None)
        if generated.IsEmpty then []
        else
        let others =
            declarations |> List.choose (function
                | Struct s when s.Name <> "" && s.Anonymous.IsNone && not s.Fields.IsEmpty -> Some (s.Name, "record")
                | Struct s when s.Name <> "" && s.Anonymous.IsNone -> Some (s.Name, "record declaration")
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
            let clashes =
                (named |> List.distinctBy (fun s -> s.Anonymous) |> List.map describe)
                @ (others |> List.filter (fun (other, _) -> other = name) |> List.distinct |> List.map (fun (_, kind) -> $"a {kind}"))
            if clashes.Length < 2 then None
            else
                let listed = clashes |> String.concat "; "
                Some $"{Diagnostics.AnonymousNameCollision}: the generated record name '{name}' in '{header}' names more than one declaration: {listed}. A generated name must name one record.")

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
        let tempDir = Path.Combine(Path.GetTempPath(), "farscape-alias")
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
                    let sizes = parseEnumProbe (output.Replace(" _fs_alias_", " _fs_enum_"))
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
                            match canonical, sizes.TryFind $"_fs_enum_size_{i}", sizes.TryFind $"_fs_enum_sign_{i}" with
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

    /// Result of parsing containing both AST declarations and macros
    type ParseResult = {
        Declarations: Declaration list
        Macros: MacroDecl list
        /// Typedefs and enums (enums measured in this unit) the header's own parse reaches
        /// through includes outside the library boundary; facts for resolving this unit's
        /// declarations, never declarations of the unit.
        Included: Declaration list
        /// Clang built-in type aliases this unit's function results spell, read through clang.
        Aliases: ClangTypeAlias list
    }

    /// Parse a C/C++ header file and extract all declarations including macros
    let parseHeaderFull (options: HeaderParserOptions) : Result<ParseResult, string> =
        match requireWellFormedDefines options.Defines with
        | Error refusal -> Error refusal
        | Ok () ->
        if not (File.Exists(options.HeaderFile)) then
            Error $"Header file not found: {options.HeaderFile}"
        else
            if options.Verbose then
                printfn "[CppParser] Parsing header: %s" options.HeaderFile

            // Pass 1: AST for structs, enums, typedefs, functions
            match runClangAst options with
            | Error err -> Error err
            | Ok jsonOutput ->
                try
                    if options.Verbose then
                        printfn "[CppParser] Parsing JSON AST..."

                    let root = JsonValue.Parse(jsonOutput)
                    let targetFile = Path.GetFileName(options.HeaderFile)

                    let declarations, typedefNamedTags, included = walkAst root options.HeaderFile targetFile options.IncludeRoot options.Verbose (enrichNonNullAttributes options)
                    // Included enums are measured in this same unit, with its own enums.
                    let measured = measureEnums options typedefNamedTags (declarations @ included)
                    let declarations, included = List.splitAt declarations.Length measured
                    match anonymousNameCollisions options.HeaderFile (declarations @ included) with
                    | [] -> ()
                    | collisions -> raise (AnonymousNameCollision (String.concat " " collisions))
                    // Each record's typedef attributes, from this unit's typedefs (#175).
                    let declarations = annotateTypedefAttributes typedefNamedTags (declarations @ included) declarations
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
                        Macros = macros
                        Included = included
                        Aliases = measureResultAliases options declarations included
                    }
                with
                | AnonymousNameCollision reason -> Error reason
                | AnonymousMemberRefused reason -> Error reason
                | ex -> Error $"Failed to parse clang output: {ex.Message}"

    /// The declarations Farscape reads from one clang JSON AST of `header` (`clang -Xclang
    /// -ast-dump=json`), named and refused as a parse names and refuses them (FS9114, FS9122,
    /// FS9123). It is internal to Farscape.Core and visible only to its test assembly: it has
    /// none of the parse's clang measurements or nonnull evidence, so its declarations must
    /// never reach generation (#173). It admits an AST clang produced under options no pilot
    /// can select (#178), such as `-fms-extensions`, whose tagged anonymous members a parse must
    /// refuse rather than drop.
    let internal declarationsOfAst (header: string) (astJson: string) : Result<Declaration list, string> =
        try
            let root = JsonValue.Parse astJson
            let declarations, typedefNamed, included = walkAst root header (Path.GetFileName header) None false (fun _ _ f -> f)
            match anonymousNameCollisions header (declarations @ included) with
            | [] -> Ok (annotateTypedefAttributes typedefNamed (declarations @ included) declarations)
            | collisions -> Error (String.concat " " collisions)
        with
        | AnonymousMemberRefused reason -> Error reason

    /// Parse a C/C++ header as one translation unit: its declarations (with macros), and the
    /// typedefs and enums its parse reaches through includes outside the library boundary.
    let parseHeaderUnit (options: HeaderParserOptions) : Result<Declaration list * Declaration list * ClangTypeAlias list, string> =
        match parseHeaderFull options with
        | Error err -> Error err
        | Ok result ->
            let allDecls = result.Declarations @ (result.Macros |> List.map Macro)
            if allDecls.IsEmpty then
                Error $"Parse succeeded but no declarations found in {Path.GetFileName(options.HeaderFile)}."
            else
                Ok (allDecls, result.Included, result.Aliases)

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
            Defines = defines
            Verbose = verbose
            IncludeMacros = true
            MacroPrefixes = []
            IncludeRoot = None
            CppMode = false  // CMSIS headers are always C
        }
        parseHeaderFull options
