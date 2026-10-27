namespace Farscape.Core

open CodeAST
open ActivePatterns
open Types
open TypeMapper

/// Emits the two declarations a C struct becomes, and the descriptor vocabulary functions become.
///
/// A C struct is never a Clef record (Farscape wiki 14_Binding_Generation_Gaps §2: a packed record cannot overlay a C
/// layout, and one that looks usable and reads the wrong bytes is the worst artifact this
/// generator can produce). It is a layout module of literal offsets plus a BAREWire
/// `StructDescriptor` in the Hardware vocabulary (BAREWire/src/Hardware/Descriptors.fs), the
/// contract every processor sharing the memory agrees on (roadmap §4.4).
///
/// Every width fact names its stratum (wiki 14_Binding_Generation_Gaps §8): offsets are measured when the pilot measured
/// the struct (clang record layout dump), declared when computed by natural alignment under the
/// profile from the declared field types, inferred when a field's representation is unknown.
module DescriptorGenerator =

    /// What one struct field is, as the generator resolved its C type.
    type FieldShape =
        /// A scalar or pointer with an ABI representation.
        | ScalarField of AbiRepr
        /// A nested struct by value, named; its extent comes from its own layout.
        | StructField of name: string
        /// A type with no representation known to the generator.
        | UnknownField of name: string

    /// The facts a layout is computed from.
    type LayoutSource = {
        Structs: Map<string, CppParser.StructDecl>
        Layouts: Map<string, CppParser.StructLayoutInfo>
        Model: PlatformABI
        Shape: string -> FieldShape
    }

    /// The stratum's name as it appears in documentation.
    let stratumName = function
        | Measured -> "measured"
        | Declared -> "declared"
        | Inferred -> "inferred"

    /// The BAREWire `Repr` source for a representation. A width the vocabulary lacks is emitted as
    /// the bare tag string so the validator reports it rather than the generator rounding it.
    let reprSource (r: AbiRepr) : string =
        let tag prefix = $"Repr.{prefix}{r.Bits}"
        match r.Family, r.Bits with
        | Pointer, _ -> "Repr.Pointer"
        | Bool, _ -> "Repr.Bool"
        | Signed, (8 | 16 | 32 | 64) -> tag "I"
        | Unsigned, (8 | 16 | 32 | 64) -> tag "U"
        | Float, (32 | 64) -> tag "F"
        | Signed, n -> $"\"i{n}\""
        | Unsigned, n -> $"\"u{n}\""
        | Float, n -> $"\"f{n}\""
        | Void, _ -> "\"void\""

    /// The `TypeRef` source for a representation in a `FunctionDescriptor`: family and bits as data.
    let typeRefSource (r: AbiRepr) : string =
        match r.Family with
        | Signed -> $"Integer (Signed, {r.Bits})"
        | Unsigned -> $"Integer (Unsigned, {r.Bits})"
        | Float -> $"Float {r.Bits}"
        | Pointer -> $"Pointer {r.Bits}"
        | Bool -> "Bool"
        | Void -> "Void"

    /// The provenance note of one width fact: its stratum and the C type and profile it was read from.
    let widthProvenance (model: PlatformABI) (r: AbiRepr) : string =
        let subject = match r.Family with Pointer -> "pointer" | _ -> r.CType
        $"width {stratumName r.Stratum}: {subject} under {model}"

    let private quote (s: string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "") + "\""

    let private optionSource = function
        | Some (d: string) -> $"Some {quote d}"
        | None -> "None"

    // Products have int operands; checked extents and offsets bound the next sum.
    // These int64 intermediates are therefore at most 2^62 before narrowing.
    let private descriptorInt subject (value: int64) =
        if value < 0L || value > 2147483647L then
            invalidOp $"{subject} has byte quantity {value} outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required."
        int value

    let private alignUp (offset: int64) (align: int64) =
        if align <= 1L then offset else ((offset + align - 1L) / align) * align

    /// Only the field parser's supported declarators grant an element spelling.
    let private elementType structName (f: CppParser.FieldDecl) =
        match CppParser.classifyFieldDeclarator f.Type with
        | CppParser.PlainFieldType element | CppParser.TrailingArrayType (element, _) -> element
        | CppParser.UnsupportedArrayType spelling ->
            invalidOp $"Struct '{structName}' field '{f.Name}' has array declarator '{spelling}'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required."

    let private elementCount structName (f: CppParser.FieldDecl) =
        match f.IsArray, f.ArraySize with
        | false, _ -> 1
        | true, Some count when count > 0 -> count
        | true, None ->
            invalidOp $"Struct '{structName}' field '{f.Name}' has an array with no fixed element count; a supported dynamic array representation contract is required."
        | true, Some count ->
            invalidOp $"Struct '{structName}' field '{f.Name}' has array element count {count}; a positive fixed element count is required."

    /// Bytes and alignment of a scalar representation. `long double` occupies 16 bytes at 16 on
    /// the x86-64 profile; every other scalar is naturally aligned at its own size.
    let private scalarExtent (r: AbiRepr) =
        match r.Family, r.Bits with
        | Float, 80 -> 16, 16
        | _ ->
            let bytes = int (max 1L ((int64 r.Bits + 7L) / 8L))
            bytes, bytes

    let private scalarFieldExtent structName fieldName count r =
        let bytes, align = scalarExtent r
        descriptorInt $"Struct '{structName}' field '{fieldName}' extent" (int64 bytes * int64 count), align

    /// Admit the measured storage extent before any consumer uses its byte quantities.
    let validateMeasuredLayout (s: CppParser.StructDecl) (measured: CppParser.StructLayoutInfo) : int * int =
        if s.Fields.Length <> measured.FieldOffsetsBits.Length then
            invalidOp $"Measured layout '{s.Name}' has {measured.FieldOffsetsBits.Length} field offsets for {s.Fields.Length} fields."
        if measured.SizeBits % 8 <> 0 then
            invalidOp $"Measured layout '{s.Name}' has size {measured.SizeBits} bits, which is not whole bytes; a supported bit-precise descriptor representation is required."
        let size = descriptorInt $"Measured layout '{s.Name}' size" (int64 measured.SizeBits / 8L)
        if size = 0 then
            invalidOp $"Measured layout '{s.Name}' has size 0 bytes for {s.Fields.Length} fields; a supported empty or opaque record representation contract is required."
        if measured.AlignmentBits % 8 <> 0 then
            invalidOp $"Measured layout '{s.Name}' has alignment {measured.AlignmentBits} bits, which is not whole bytes; a supported bit-precise descriptor representation is required."
        if measured.AlignmentBits <= 0 then
            invalidOp $"Measured layout '{s.Name}' has alignment {measured.AlignmentBits} bits; a positive whole-byte measured alignment is required."
        size, measured.AlignmentBits / 8

    type private RepresentedFieldShape =
        | RepresentedScalar of AbiRepr
        | RepresentedStruct of CppParser.StructDecl

    let private representedFieldShape (src: LayoutSource) structName (field: CppParser.FieldDecl) =
        match src.Shape (elementType structName field) with
        | ScalarField repr -> RepresentedScalar repr
        | StructField name ->
            match Map.tryFind name src.Structs with
            | None ->
                invalidOp $"Struct '{structName}' field '{field.Name}' has nested record type '{name}' with no available declaration; a complete C representation contract is required."
            | Some nested when nested.Fields.IsEmpty ->
                invalidOp $"Struct '{structName}' field '{field.Name}' has fieldless nested record type '{name}'; a supported empty or opaque record representation contract is required."
            | Some nested -> RepresentedStruct nested
        | UnknownField name ->
            invalidOp $"Struct '{structName}' field '{field.Name}' has no known representation for type '{name}'; a complete C representation contract is required."

    /// One struct's layout: per-field byte offsets, size, alignment, and the stratum of the offsets.
    let rec layoutOf (src: LayoutSource) (depth: int) (s: CppParser.StructDecl)
        : int list * int * int * Stratum =
        match s.Fields |> List.tryFind (fun field -> field.IsBitfield) with
        | Some field ->
            invalidOp $"Struct '{s.Name}' field '{field.Name}' is a bitfield; a supported bit-precise descriptor representation is required."
        | None -> ()
        let fields =
            s.Fields |> List.map (fun field ->
                let shape = representedFieldShape src s.Name field
                field.Name, shape, elementCount s.Name field)
        match Map.tryFind s.Name src.Layouts with
        | Some measured ->
            let size, alignment = validateMeasuredLayout s measured
            let offsets =
                Seq.zip s.Fields measured.FieldOffsetsBits
                |> Seq.map (fun (field, bits) ->
                    if bits % 8 <> 0 then
                        invalidOp $"Struct '{s.Name}' field '{field.Name}' has measured bit offset {bits}, which is not byte-aligned; a supported bit-precise descriptor representation is required."
                    let offset = bits / 8
                    if offset < 0 || offset >= size then
                        invalidOp $"Struct '{s.Name}' field '{field.Name}' has measured byte offset {offset} outside the measured storage 0..{size - 1}; a measured layout consistent with its record size is required."
                    offset)
                |> List.ofSeq
            offsets, size, alignment, Measured
        | None ->
            let requireNaturalAttributes subject (attributes: CppParser.AttributeData list) =
                attributes |> List.iter (fun attribute ->
                    match attribute.Kind with
                    | "PackedAttr" | "AlignedAttr" | "MaxFieldAlignmentAttr" ->
                        invalidOp $"{subject} carries layout attribute '{attribute.Kind}' that natural alignment cannot represent; a measured layout is required."
                    | kind ->
                        invalidOp $"{subject} carries attribute '{kind}' with unclassified layout effect; a measured layout or a classified attribute contract is required.")
            requireNaturalAttributes $"Struct '{s.Name}'" s.Attributes
            s.Fields |> List.iter (fun field -> requireNaturalAttributes $"Struct '{s.Name}' field '{field.Name}'" field.Attributes)
            let fieldExtent fieldName count shape : int * int =
                match shape with
                | RepresentedScalar r ->
                    scalarFieldExtent s.Name fieldName count r
                | RepresentedStruct nested when depth >= 16 && not (Map.containsKey nested.Name src.Layouts) ->
                    invalidOp $"Struct '{s.Name}' field '{fieldName}' nests record type '{nested.Name}' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required."
                | RepresentedStruct nested ->
                    let _, size, align, _ = layoutOf src (depth + 1) nested
                    descriptorInt $"Struct '{s.Name}' field '{fieldName}' extent" (int64 size * int64 count), align
            let (endOffset, maxAlign, offsets) =
                fields
                |> List.fold (fun (offset, maxAlign, offsets) (fieldName, shape, count) ->
                    let size, align = fieldExtent fieldName count shape
                    let at = if s.IsUnion then 0 else descriptorInt $"Struct '{s.Name}' field '{fieldName}' aligned offset" (alignUp offset (int64 align))
                    let next = if s.IsUnion then max offset (int64 size) else int64 at + int64 size
                    next, max maxAlign align, offsets @ [at])
                    (0L, 1, [])
            offsets, descriptorInt $"Struct '{s.Name}' final size" (alignUp endOffset (int64 maxAlign)), maxAlign, Declared

    /// The BAREWire field descriptor of one field at its offset.
    let private fieldDescriptor (src: LayoutSource) structName (offsetStratum: Stratum) (size: int) (f: CppParser.FieldDecl) (offset: int) : FsExpr =
        let access = if f.IsConst then "AccessKind.ReadOnly" else "AccessKind.ReadWrite"
        let shape = representedFieldShape src structName f
        let count = elementCount structName f
        let repr, countSource, note, extent =
            match shape with
            | RepresentedScalar r ->
                let extent, _ = scalarFieldExtent structName f.Name count r
                reprSource r, string count, $"repr {stratumName r.Stratum}: {r.CType} under {src.Model}", extent
            | RepresentedStruct nested ->
                let _, size, _, _ = layoutOf src 1 nested
                let byteCount = descriptorInt $"Struct '{structName}' field '{f.Name}' nested byte count" (int64 size * int64 count)
                "Repr.U8", string byteCount, $"nested struct {nested.Name} by value, as bytes; see {nested.Name}.Descriptor", byteCount
        let endOffset = int64 offset + int64 extent
        if endOffset > int64 size then
            invalidOp $"Struct '{structName}' field '{f.Name}' has byte extent {extent} at {stratumName offsetStratum} offset {offset}, ending at byte {endOffset} beyond the record size {size}; a measured layout consistent with its field representation is required."
        let doc = $"offset {stratumName offsetStratum}; {note}"
        RecordConstruction [
            "Name", Literal (quote f.Name)
            "Offset", Literal (string offset)
            "Repr", Literal repr
            "Count", Literal countSource
            "Access", Literal access
            "BitFields", Literal "[||]"
            "Documentation", Literal (optionSource (Some doc))
        ]

    /// The declarations for one C struct: the layout module of literal offsets and, inside it,
    /// the `StructDescriptor` contract.
    let structDecls (src: LayoutSource) (s: CppParser.StructDecl) : FsDecl list =
        let offsets, size, align, stratum = layoutOf src 0 s
        let strat = stratumName stratum
        let how =
            match stratum with
            | Measured -> "measured (clang record layout dump)"
            | Declared -> $"declared (natural alignment under {src.Model} from the declared field types)"
            | Inferred -> "inferred (a field without a known representation; verify against a measurement)"
        let kind = if s.IsUnion then "union" else "struct"
        let fieldPairs = Seq.zip s.Fields offsets |> List.ofSeq
        let offsetLiterals =
            fieldPairs
            |> List.collect (fun (f, offset) ->
                [ XmlDoc $"Offset of `{f.Name}` in bytes ({strat})."
                  LiteralBinding($"{f.Name}Offset", string offset) ])
        let descriptor =
            RecordBlock [
                "Name", Literal (quote s.Name)
                "Layout", RecordBlock [
                    "Size", Literal (string size)
                    "Alignment", Literal (string align)
                    "Fields", ArrayBlock (fieldPairs |> List.map (fun (f, offset) -> fieldDescriptor src s.Name stratum size f offset))
                ]
                "Documentation", Literal (optionSource s.Documentation)
            ]
        let headline = $"/// C {kind} `{s.Name}`: layout {how}."
        let docLine = s.Documentation |> Option.map (fun d -> Comment $"/// {d}") |> Option.toList
        [ Comment headline ] @ docLine @
        [ SubModule(s.Name,
            [ XmlDoc $"Size in bytes ({strat})."
              LiteralBinding("Size", string size)
              XmlDoc $"Alignment in bytes ({strat})."
              LiteralBinding("Alignment", string align) ]
            @ offsetLiterals
            @ [ XmlDoc "BAREWire layout contract; each field's Documentation names the strata of its offset and representation."
                ValueBinding("Descriptor", Named "StructDescriptor", descriptor) ])
          BlankLine ]
