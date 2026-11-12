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
        /// Declared count sources of flexible array members, by struct (BAREWire #11).
        FlexibleArrays: Map<string, PilotTypes.FlexibleArrayCount>
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
            invalidOp $"Struct '{structName}' field '{f.Name}' is a flexible array member with no declared count source; an options.flexible_arrays entry naming its count field and count rule is required."
        | true, Some count ->
            invalidOp $"Struct '{structName}' field '{f.Name}' has array element count {count}; a positive fixed element count is required."

    /// A C flexible array member (C11 6.7.2.1p18): an array field with no element count.
    let isFlexibleArrayMember (f: CppParser.FieldDecl) = f.IsArray && f.ArraySize.IsNone

    /// The element extent of one field: a fixed inline count, or the declared count source of a
    /// flexible array member, whose count is a run-time fact (BAREWire #11).
    type FieldExtent =
        | FixedCount of int
        | FlexibleCount of PilotTypes.FlexibleArrayCount

    /// How a count rule is spelled in BAREWire source and in documentation.
    let private countRuleSource = function
        | PilotTypes.ElementCount -> "CountRule.ElementCount", "element count"
        | PilotTypes.RecordBytes -> "CountRule.RecordBytes", "record bytes through the array's last element"

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
        // Clang dumps an offset for each field and each unnamed bit-field (#176).
        if s.Fields.Length + s.UnnamedBitFields.Length <> measured.FieldOffsetsBits.Length then
            let members =
                if s.UnnamedBitFields.IsEmpty then $"{s.Fields.Length} fields"
                else $"{s.Fields.Length} fields and {s.UnnamedBitFields.Length} unnamed bit-fields"
            invalidOp $"Measured layout '{s.Name}' has {measured.FieldOffsetsBits.Length} field offsets for {members}."
        if measured.SizeBits % 8 <> 0 then
            invalidOp $"Measured layout '{s.Name}' has size {measured.SizeBits} bits, which is not whole bytes; a supported bit-precise descriptor representation is required."
        let size = descriptorInt $"Measured layout '{s.Name}' size" (int64 measured.SizeBits / 8L)
        // A record of only unnamed bit-fields (#183) clang may measure at 0 bytes: in C a
        // zero-width `int : 0` alone makes a GNU empty record.
        match s.Fields, s.UnnamedBitFields with
        | [], b :: _ when size = 0 ->
            let kind = if s.IsUnion then "union" else "struct"
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.PaddingOnlyRecordEmpty $"{kind} '{s.Name}' has no named field, only {CppParser.describeUnnamedBitField b}, and clang measures it at 0 bytes in its declaring unit (a GNU C empty record); a descriptor has no zero-byte record representation." s.Location.Location)
        | _ -> ()
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
            // A record of only unnamed bit-fields is defined (#183): clang's layout places it.
            | Some nested when not (CppParser.isDefinedRecord nested) ->
                invalidOp $"Struct '{structName}' field '{field.Name}' has fieldless nested record type '{name}'; a supported empty or opaque record representation contract is required."
            | Some nested when nested.Fields |> List.exists isFlexibleArrayMember ->
                invalidOp $"Struct '{structName}' field '{field.Name}' holds record type '{name}' by value, which ends in a flexible array member; a record ending in a flexible array member has no fixed extent to hold by value."
            | Some nested -> RepresentedStruct nested
        | UnknownField name ->
            invalidOp $"Struct '{structName}' field '{field.Name}' has no known representation for type '{name}'; a complete C representation contract is required."

    /// The extent of one field of `s`. A flexible array member is admitted only as the last field
    /// of a struct, of scalar elements, with a declared count source naming a preceding integer
    /// field of the same struct (BAREWire #11); each missing part is refused with its reason.
    let private fieldExtentOf (src: LayoutSource) (s: CppParser.StructDecl) (shape: RepresentedFieldShape) (f: CppParser.FieldDecl) : FieldExtent =
        if not (isFlexibleArrayMember f) then FixedCount (elementCount s.Name f)
        else
            match Map.tryFind s.Name src.FlexibleArrays with
            | None -> FixedCount (elementCount s.Name f)
            | Some declared when declared.Field <> f.Name ->
                invalidOp $"Struct '{s.Name}' field '{f.Name}' is a flexible array member, but options.flexible_arrays declares the count source of '{s.Name}' for field '{declared.Field}'; a count source naming '{f.Name}' is required."
            | Some declared ->
                if s.IsUnion then
                    invalidOp $"Union '{s.Name}' field '{f.Name}' is a flexible array member; a flexible array member trails the fixed fields of a struct, and a union has none to trail."
                let following = s.Fields |> List.skipWhile (fun field -> field.Name <> f.Name) |> List.tail
                match following with
                | next :: _ ->
                    invalidOp $"Struct '{s.Name}' field '{f.Name}' is a flexible array member followed by field '{next.Name}'; a flexible array member must be the last field of its struct."
                | [] -> ()
                match shape with
                | RepresentedStruct nested ->
                    invalidOp $"Struct '{s.Name}' flexible array member '{f.Name}' has elements of record type '{nested.Name}'; a flexible array member of scalar elements is required."
                | RepresentedScalar _ -> ()
                let preceding = s.Fields |> List.takeWhile (fun field -> field.Name <> f.Name)
                match preceding |> List.tryFind (fun field -> field.Name = declared.CountField) with
                | None ->
                    invalidOp $"Struct '{s.Name}' flexible array member '{f.Name}' declares count field '{declared.CountField}', which names no preceding field of '{s.Name}'; a preceding integer field of the same struct is required."
                | Some counter ->
                    let integer =
                        not counter.IsArray && not counter.IsBitfield
                        && (match src.Shape (elementType s.Name counter) with
                            | ScalarField r -> r.Family = Signed || r.Family = Unsigned
                            | _ -> false)
                    if not integer then
                        invalidOp $"Struct '{s.Name}' flexible array member '{f.Name}' declares count field '{counter.Name}', which is not one integer scalar; a preceding integer field of the same struct is required."
                FlexibleCount declared

    /// A declared count source names the flexible array member of its struct: refused when the
    /// struct has none.
    let private requireDeclaredFlexibleArray (src: LayoutSource) (s: CppParser.StructDecl) =
        match Map.tryFind s.Name src.FlexibleArrays with
        | Some declared when not (s.Fields |> List.exists isFlexibleArrayMember) ->
            invalidOp $"options.flexible_arrays declares a count source for '{s.Name}' field '{declared.Field}', but '{s.Name}' has no flexible array member; a count source names the last, element-count-free field of its struct."
        | _ -> ()

    /// The layout-neutral attributes (`AttributeContracts.LayoutNeutral`) a record or its fields
    /// carry, each kind once, with the fact it records in the record's descriptor (#174).
    let neutralAttributes (s: CppParser.StructDecl) : (string * string) list =
        (s.Attributes @ (s.Fields |> List.collect _.Attributes))
        |> List.choose (fun a -> AttributeContracts.neutralFact a.Kind |> Option.map (fun fact -> a.Kind, fact))
        |> List.distinct

    let private offsetsText (offsets: int list) = offsets |> List.map string |> String.concat ", "

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
                field.Name, shape, fieldExtentOf src s shape field)
        requireDeclaredFlexibleArray src s
        // A layout the pilot measured as ABI-critical storage, or the one the parse measured in
        // the record's declaring unit (anonymous records and the records enclosing them); two
        // measurements of one record must agree.
        let measuredLayout =
            match Map.tryFind s.Name src.Layouts, s.Measured with
            | Some requested, Some parsed
                when (requested.SizeBits, requested.AlignmentBits, requested.FieldOffsetsBits)
                     <> (parsed.SizeBits, parsed.AlignmentBits, parsed.FieldOffsetsBits) ->
                invalidOp $"Struct '{s.Name}' has two clang measurements that disagree: the ABI-critical probe measured {requested.SizeBits / 8} bytes at alignment {requested.AlignmentBits / 8}, its declaring unit's parse {parsed.SizeBits / 8} bytes at alignment {parsed.AlignmentBits / 8}; one effective layout is required."
            | Some requested, _ -> Some requested
            | None, parsed -> parsed
        // A layout-neutral attribute (`may_alias`) is admitted only when clang measures this
        // record and its copy without the attribute (`CppParser.UnattributedLayout`), synthesized
        // in the same unit, at the same size, alignment and field offsets (#174). An attribute
        // the table does not classify keeps its exact reason; a layout attribute beside it, or a
        // record with no faithful copy, leaves the contract unconfirmed. Farscape's own layout
        // model is never the evidence.
        let neutral = neutralAttributes s
        let neutralKinds = neutral |> List.map fst |> String.concat "', '"
        let kind = if s.IsUnion then "union" else "struct"
        // How a refusal names an attribute by its classification in `AttributeContracts`.
        let classified (attribute: string) =
            match AttributeContracts.contractOf attribute with
            | Some AttributeContracts.LayoutAffecting -> $"layout attribute '{attribute}'"
            | Some (AttributeContracts.LayoutNeutral _) -> $"layout-neutral attribute '{attribute}'"
            | None -> $"attribute '{attribute}' with unclassified layout effect"
        // A typedef naming the record (`typedef struct { ... } T __attribute__((aligned(16)))`)
        // gives the type `T` its attributes, while clang's record layout dump measures the
        // record without them; the descriptor of `T` would carry the record's layout (#175).
        match s.TypedefAttributes |> List.tryFind (fun a -> a.Site = CppParser.RecordName) with
        | Some a ->
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.TypedefNamingAttribute $"{kind} '{s.Name}' is named by typedef '{a.Typedef}', which carries {classified a.Attribute.Kind}; clang's record layout dump measures the record, not the typedef that names it, so '{s.Name}' has no measured layout as the type its name spells." s.Location.Location)
        | None -> ()
        let unconfirmed detail =
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.LayoutNeutralUnconfirmed $"{kind} '{s.Name}' carries layout-neutral attribute '{neutralKinds}', {detail}; the contract that '{neutralKinds}' moves no byte is unconfirmed." s.Location.Location)
        let layoutText (sizeBits: int) (alignmentBits: int) (offsetsBits: int list) =
            $"{sizeBits / 8} bytes, alignment {alignmentBits / 8}, offsets {offsetsText (offsetsBits |> List.map (fun bits -> bits / 8))}"
        let confirmNeutral (measured: CppParser.StructLayoutInfo) =
            if not neutral.IsEmpty then
                let unclassified subject (attributes: CppParser.AttributeData list) =
                    attributes |> List.iter (fun a ->
                        if (AttributeContracts.contractOf a.Kind).IsNone then
                            invalidOp $"{subject} carries attribute '{a.Kind}' with unclassified layout effect; a measured layout or a classified attribute contract is required.")
                unclassified $"Struct '{s.Name}'" s.Attributes
                s.Fields |> List.iter (fun f -> unclassified $"Struct '{s.Name}' field '{f.Name}'" f.Attributes)
                let affecting =
                    [ for a in s.Attributes do
                        if AttributeContracts.contractOf a.Kind = Some AttributeContracts.LayoutAffecting then yield $"'{a.Kind}'"
                      for f in s.Fields do
                        for a in f.Attributes do
                            if AttributeContracts.contractOf a.Kind = Some AttributeContracts.LayoutAffecting then yield $"'{a.Kind}' on field '{f.Name}'" ]
                match affecting, s.Unattributed with
                | first :: _, _ ->
                    unconfirmed $"and layout attribute {first}, which a copy without '{neutralKinds}' would have to reproduce, so no copy is synthesized for clang to measure"
                | [], None ->
                    unconfirmed "but clang has no measurement of its copy without the attribute in its declaring unit"
                | [], Some (CppParser.UnattributedUnsynthesized reason) ->
                    unconfirmed $"but no faithful copy without the attribute can be synthesized for clang to measure: {reason}"
                | [], Some (CppParser.UnattributedMeasured copy) ->
                    if (copy.SizeBits, copy.AlignmentBits, copy.FieldOffsetsBits) <> (measured.SizeBits, measured.AlignmentBits, measured.FieldOffsetsBits) then
                        unconfirmed $"but clang measures it at {layoutText measured.SizeBits measured.AlignmentBits measured.FieldOffsetsBits} and its copy without the attribute at {layoutText copy.SizeBits copy.AlignmentBits copy.FieldOffsetsBits}"
        match measuredLayout with
        | None when s.Anonymous.IsSome ->
            let origin = s.Anonymous.Value
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.AnonymousRecordUnmeasured $"anonymous record '{s.Name}' ({CppParser.describeOrigin origin}, declared at {origin.Location}) has no clang-measured layout in its declaring unit; its layout is never computed from its declared fields." s.Location.Location)
        | Some measured ->
            let size, alignment = validateMeasuredLayout s measured
            let offsets =
                Seq.zip s.Fields (CppParser.fieldOffsetsBits s measured)
                |> Seq.map (fun (field, bits) ->
                    if bits % 8 <> 0 then
                        invalidOp $"Struct '{s.Name}' field '{field.Name}' has measured bit offset {bits}, which is not byte-aligned; a supported bit-precise descriptor representation is required."
                    let offset = bits / 8
                    // A flexible array member adds no bytes: it may begin at the record's end.
                    let beyond = if isFlexibleArrayMember field then offset > size else offset >= size
                    if offset < 0 || beyond then
                        invalidOp $"Struct '{s.Name}' field '{field.Name}' has measured byte offset {offset} outside the measured storage 0..{size - 1}; a measured layout consistent with its record size is required."
                    offset)
                |> List.ofSeq
            confirmNeutral measured
            offsets, size, alignment, Measured
        | None ->
            if not neutral.IsEmpty then
                unconfirmed "but clang has no measurement of it in its declaring unit, which the contract is confirmed against together with clang's measurement of its copy without the attribute"
            let requireNaturalAttributes subject (attributes: CppParser.AttributeData list) =
                attributes |> List.iter (fun attribute ->
                    match AttributeContracts.contractOf attribute.Kind with
                    | Some AttributeContracts.LayoutAffecting ->
                        invalidOp $"{subject} carries layout attribute '{attribute.Kind}' that natural alignment cannot represent; a measured layout is required."
                    // Unreachable: a record carrying one is refused above unless clang measured it.
                    | Some (AttributeContracts.LayoutNeutral _) -> ()
                    | None ->
                        invalidOp $"{subject} carries attribute '{attribute.Kind}' with unclassified layout effect; a measured layout or a classified attribute contract is required.")
            requireNaturalAttributes $"Struct '{s.Name}'" s.Attributes
            s.Fields |> List.iter (fun field -> requireNaturalAttributes $"Struct '{s.Name}' field '{field.Name}'" field.Attributes)
            // An unnamed bit-field pads the record, and a zero-width one moves the next member
            // to its type's boundary (#176); only clang's layout places either.
            match s.UnnamedBitFields with
            | b :: _ ->
                Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.UnnamedBitFieldUnmeasured $"{kind} '{s.Name}' carries {CppParser.describeUnnamedBitField b}, which moves bytes natural alignment of the declared field types cannot place, and clang has no measurement of '{s.Name}' in its declaring unit; an unnamed bit-field is never dropped from its record." s.Location.Location)
            | [] -> ()
            // A typedef attribute a field's type reaches (`aligned(8)` on the field's typedef)
            // moves the field; only clang's layout places it (#175).
            match s.TypedefAttributes |> List.tryPick (fun a -> match a.Site with CppParser.FieldType field -> Some (field, a) | CppParser.RecordName -> None) with
            | Some (field, a) ->
                Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.TypedefAttributeUnmeasured $"{kind} '{s.Name}' field '{field}' has a type reaching typedef '{a.Typedef}', which carries {classified a.Attribute.Kind}, and clang has no measurement of '{s.Name}' in its declaring unit; natural alignment of the declared field types never stands in for the typedef's attribute." s.Location.Location)
            | None -> ()
            let fieldExtent fieldName extent shape : int * int =
                match extent, shape with
                // A flexible array member adds no bytes; its element alignment places it and
                // joins the record's (C11 6.7.2.1p18).
                | FlexibleCount _, RepresentedScalar r ->
                    let _, align = scalarExtent r
                    0, align
                | FlexibleCount _, RepresentedStruct nested ->
                    invalidOp $"Struct '{s.Name}' flexible array member '{fieldName}' has elements of record type '{nested.Name}'; a flexible array member of scalar elements is required."
                | FixedCount count, RepresentedScalar r ->
                    scalarFieldExtent s.Name fieldName count r
                | FixedCount _, RepresentedStruct nested when depth >= 16 && not (Map.containsKey nested.Name src.Layouts) ->
                    invalidOp $"Struct '{s.Name}' field '{fieldName}' nests record type '{nested.Name}' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required."
                | FixedCount count, RepresentedStruct nested ->
                    let _, size, align, _ = layoutOf src (depth + 1) nested
                    descriptorInt $"Struct '{s.Name}' field '{fieldName}' extent" (int64 size * int64 count), align
            let (endOffset, maxAlign, offsets) =
                fields
                |> List.fold (fun (offset, maxAlign, offsets) (fieldName, shape, extent) ->
                    let size, align = fieldExtent fieldName extent shape
                    let at = if s.IsUnion then 0 else descriptorInt $"Struct '{s.Name}' field '{fieldName}' aligned offset" (alignUp offset (int64 align))
                    let next = if s.IsUnion then max offset (int64 size) else int64 at + int64 size
                    next, max maxAlign align, offsets @ [at])
                    (0L, 1, [])
            offsets, descriptorInt $"Struct '{s.Name}' final size" (alignUp endOffset (int64 maxAlign)), maxAlign, Declared

    /// The BAREWire field descriptor of one field at its offset.
    let private fieldDescriptor (src: LayoutSource) structName (typedefAttributes: CppParser.TypedefAttribute list) (offsetStratum: Stratum) (size: int) (f: CppParser.FieldDecl) (offset: int) : FsExpr =
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
        // The fact of each layout-neutral attribute of a typedef the field's type reaches (#175).
        // Such a record is laid out only from clang's measurement, so the attribute's layout
        // effect is clang's; its fact belongs to the field's type, and is recorded here.
        let typedefFacts =
            typedefAttributes
            |> List.choose (fun (a: CppParser.TypedefAttribute) ->
                match a.Site, AttributeContracts.neutralFact a.Attribute.Kind with
                | CppParser.FieldType field, Some fact when field = f.Name ->
                    Some $"; type reaches typedef '{a.Typedef}' carrying {a.Attribute.Kind} (laid out by clang's measurement of this record): {fact}"
                | _ -> None)
            |> List.distinct
            |> String.concat ""
        let doc = $"offset {stratumName offsetStratum}; {note}{typedefFacts}"
        RecordConstruction [
            "Name", Literal (quote f.Name)
            "Offset", Literal (string offset)
            "Repr", Literal repr
            "Count", Literal countSource
            "Access", Literal access
            "BitFields", Literal "[||]"
            "Documentation", Literal (optionSource (Some doc))
        ]

    /// The BAREWire `FlexibleArrayDescriptor` of a struct's trailing flexible array member at its
    /// offset, with its declared count source (BAREWire #11). `layoutOf` has admitted it: last
    /// field, scalar elements, a preceding integer count field.
    let private flexibleArrayDescriptor (src: LayoutSource) structName (offsetStratum: Stratum) (size: int) (f: CppParser.FieldDecl) (offset: int) (declared: PilotTypes.FlexibleArrayCount) : FsExpr =
        let access = if f.IsConst then "AccessKind.ReadOnly" else "AccessKind.ReadWrite"
        let r =
            match representedFieldShape src structName f with
            | RepresentedScalar r -> r
            | RepresentedStruct nested ->
                invalidOp $"Struct '{structName}' flexible array member '{f.Name}' has elements of record type '{nested.Name}'; a flexible array member of scalar elements is required."
        if offset > size then
            invalidOp $"Struct '{structName}' flexible array member '{f.Name}' begins at {stratumName offsetStratum} offset {offset}, beyond the record size {size}; a measured layout consistent with its field representation is required."
        let rule, ruleText = countRuleSource declared.Rule
        let doc = $"offset {stratumName offsetStratum}; repr {stratumName r.Stratum}: {r.CType} under {src.Model}; flexible array member, count from '{declared.CountField}' ({ruleText}), declared by options.flexible_arrays: {declared.Source}"
        FunctionCall("", "Some", [
            RecordConstruction [
                "Name", Literal (quote f.Name)
                "Offset", Literal (string offset)
                "Repr", Literal (reprSource r)
                "Access", Literal access
                "CountField", Literal (quote declared.CountField)
                "CountRule", Literal rule
                "Documentation", Literal (optionSource (Some doc))
            ] ])

    /// True when the record itself carries `may_alias` (clang `MayAliasAttr`): the aliasing fact
    /// its descriptor's `MayAlias` field carries (BAREWire #12). `layoutOf` has confirmed it moves
    /// no byte. A field's own attribute, or one reached through a field's typedef, is not the
    /// record's.
    let mayAlias (s: CppParser.StructDecl) =
        s.Attributes |> List.exists (fun a -> a.Kind = AttributeContracts.MayAliasAttr)

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
        // The record of a C11 anonymous member of `parent` that field `f` holds (#173).
        let memberRecordOf (parent: string) (f: CppParser.FieldDecl) =
            match src.Shape (elementType parent f) with
            | StructField name ->
                Map.tryFind name src.Structs
                |> Option.filter (fun r -> r.Anonymous |> Option.exists (fun o -> o.Member && o.Parent = parent && o.Field = f.Name))
            | _ -> None
        // C reaches the members of an anonymous member as members of the enclosing record:
        // each, through any depth of anonymous members, at its offset in this record.
        let rec reachable (record: CppParser.StructDecl) (at: int) : (string * int) list =
            let nestedOffsets, _, _, _ = layoutOf src 1 record
            Seq.zip record.Fields nestedOffsets |> List.ofSeq |> List.collect (fun (f, offset) ->
                match memberRecordOf record.Name f with
                | Some deeper -> reachable deeper (at + offset)
                | None -> [ f.Name, at + offset ])
        let offsetLiterals =
            fieldPairs
            |> List.collect (fun (f, offset) ->
                [ XmlDoc $"Offset of `{f.Name}` in bytes ({strat})."
                  LiteralBinding($"{f.Name}Offset", Named "int", string offset) ]
                @ (match memberRecordOf s.Name f with
                   | Some record ->
                       reachable record offset |> List.collect (fun (name, at) ->
                           [ XmlDoc $"Offset of `{name}` in bytes ({strat}), a member of `{s.Name}` reached through its anonymous member `{f.Name}`."
                             LiteralBinding($"{name}Offset", Named "int", string at) ])
                   | None -> []))
        // Each confirmed layout-neutral attribute's fact (`may_alias`), first in the descriptor's
        // Documentation, which is the record's contract with Layer 2 (#174).
        let contracts =
            neutralAttributes s
            |> List.map (fun (attribute, fact) -> $"{attribute}: no layout effect, confirmed by clang measuring this record and its copy without the attribute, in the same unit, at the same size, alignment and field offsets; {fact}.")
        let documentation =
            match contracts, s.Documentation with
            | [], doc -> doc
            | facts, None -> Some (String.concat " " facts)
            | facts, Some doc -> Some (String.concat " " facts + " " + doc)
        let descriptor =
            let fixedPairs = fieldPairs |> List.filter (fun (f, _) -> not (isFlexibleArrayMember f))
            let flexible =
                match fieldPairs |> List.tryFind (fun (f, _) -> isFlexibleArrayMember f) with
                | Some (f, offset) -> flexibleArrayDescriptor src s.Name stratum size f offset (Map.find s.Name src.FlexibleArrays)
                | None -> Literal "None"
            RecordBlock [
                "Name", Literal (quote s.Name)
                "Layout", RecordBlock [
                    "Size", Literal (string size)
                    "Alignment", Literal (string align)
                    "Fields", ArrayBlock (fixedPairs |> List.map (fun (f, offset) -> fieldDescriptor src s.Name s.TypedefAttributes stratum size f offset))
                ]
                "MayAlias", Literal (if mayAlias s then "true" else "false")
                "FlexibleArray", flexible
                "Documentation", Literal (optionSource documentation)
            ]
        let headline = $"/// C {kind} `{s.Name}`: layout {how}."
        let docLine =
            (contracts |> List.map (fun fact -> Comment $"/// {fact}"))
            @ (s.Documentation |> Option.map (fun d -> Comment $"/// {d}") |> Option.toList)
        [ Comment headline ] @ docLine @
        [ SubModule(s.Name,
            [ XmlDoc $"Size in bytes ({strat})."
              LiteralBinding("Size", Named "int", string size)
              XmlDoc $"Alignment in bytes ({strat})."
              LiteralBinding("Alignment", Named "int", string align) ]
            @ offsetLiterals
            @ [ XmlDoc "BAREWire layout contract; each field's Documentation names the strata of its offset and representation."
                ValueBinding("Descriptor", Generic("Expr", Named "StructDescriptor"), Quoted descriptor) ])
          BlankLine ]
