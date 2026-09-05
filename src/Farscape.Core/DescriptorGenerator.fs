namespace Farscape.Core

open CodeAST
open ActivePatterns
open Types
open TypeMapper

/// Emits the two declarations a C struct becomes, and the descriptor vocabulary functions become.
///
/// A C struct is never a Clef record (Farscape docs/14 §2: a packed record cannot overlay a C
/// layout, and one that looks usable and reads the wrong bytes is the worst artifact this
/// generator can produce). It is a layout module of literal offsets plus a BAREWire
/// `StructDescriptor` in the Hardware vocabulary (BAREWire/src/Hardware/Descriptors.fs), the
/// contract every processor sharing the memory agrees on (roadmap §4.4).
///
/// Every width fact names its stratum (docs/14 §8): offsets are measured when the pilot measured
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

    let private alignUp (offset: int) (align: int) =
        if align <= 1 then offset else ((offset + align - 1) / align) * align

    /// Element type of an array field: the declared type without its extent.
    let private elementType (f: CppParser.FieldDecl) =
        match f.Type.IndexOf('[') with
        | -1 -> f.Type
        | i -> f.Type.Substring(0, i).Trim()

    let private elementCount (f: CppParser.FieldDecl) =
        if f.IsArray then defaultArg f.ArraySize 0 else 1

    /// Bytes and alignment of a scalar representation. `long double` occupies 16 bytes at 16 on
    /// the x86-64 profile; every other scalar is naturally aligned at its own size.
    let private scalarExtent (r: AbiRepr) =
        match r.Family, r.Bits with
        | Float, 80 -> 16, 16
        | _ ->
            let bytes = max 1 ((r.Bits + 7) / 8)
            bytes, bytes

    /// One struct's layout: per-field byte offsets, size, alignment, and the stratum of the offsets.
    let rec layoutOf (src: LayoutSource) (depth: int) (s: CppParser.StructDecl)
        : int list * int * int * Stratum =
        match Map.tryFind s.Name src.Layouts with
        | Some measured ->
            let offsets =
                Seq.zip s.Fields measured.FieldOffsetsBits
                |> Seq.map (fun (_, bits) -> bits / 8)
                |> List.ofSeq
            offsets, measured.SizeBits / 8, measured.AlignmentBits / 8, Measured
        | None ->
            let fieldExtent (f: CppParser.FieldDecl) : (int * int * Stratum) option =
                let count = elementCount f
                match src.Shape (elementType f) with
                | ScalarField r ->
                    let bytes, align = scalarExtent r
                    let stratum = if f.IsBitfield then Inferred else Declared
                    Some (bytes * count, align, stratum)
                | StructField name when depth < 16 ->
                    match Map.tryFind name src.Structs with
                    | Some nested when not nested.Fields.IsEmpty ->
                        let _, size, align, _ = layoutOf src (depth + 1) nested
                        Some (size * count, align, Declared)
                    | _ -> None
                | StructField _ | UnknownField _ -> None
            let (endOffset, maxAlign, offsets, stratum) =
                s.Fields
                |> List.fold (fun (offset, maxAlign, offsets, stratum) f ->
                    match fieldExtent f with
                    | Some (size, align, fieldStratum) ->
                        let at = if s.IsUnion then 0 else alignUp offset align
                        let next = if s.IsUnion then max offset size else at + size
                        let worst = match stratum, fieldStratum with Inferred, _ | _, Inferred -> Inferred | _ -> Declared
                        next, max maxAlign align, offsets @ [at], worst
                    | None ->
                        offset, maxAlign, offsets @ [offset], Inferred)
                    (0, 1, [], Declared)
            offsets, alignUp endOffset maxAlign, maxAlign, stratum

    /// The BAREWire field descriptor of one field at its offset.
    let private fieldDescriptor (src: LayoutSource) (offsetStratum: Stratum) (f: CppParser.FieldDecl) (offset: int) : FsExpr =
        let access = if f.IsConst then "AccessKind.ReadOnly" else "AccessKind.ReadWrite"
        let count = elementCount f
        let repr, countSource, note =
            match src.Shape (elementType f) with
            | ScalarField r when f.IsBitfield ->
                let width = defaultArg f.BitWidth 0
                reprSource r, string count, $"repr inferred: bit field of {width} bits in a {r.CType} unit"
            | ScalarField r -> reprSource r, string count, $"repr {stratumName r.Stratum}: {r.CType} under {src.Model}"
            | StructField name ->
                let bytes =
                    match Map.tryFind name src.Structs with
                    | Some nested when not nested.Fields.IsEmpty ->
                        let _, size, _, _ = layoutOf src 1 nested
                        size * count
                    | _ -> 0
                "Repr.U8", string bytes, $"nested struct {name} by value, as bytes; see {name}.Descriptor"
            | UnknownField name -> "Repr.U8", "0", $"repr unknown: no representation for {name}"
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
            | Inferred -> "inferred (a field without a known representation or a bit field; verify against a measurement)"
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
                    "Fields", ArrayBlock (fieldPairs |> List.map (fun (f, offset) -> fieldDescriptor src stratum f offset))
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
