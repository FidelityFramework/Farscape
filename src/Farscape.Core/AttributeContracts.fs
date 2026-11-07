namespace Farscape.Core

/// The classified layout contracts of the clang record attributes Farscape admits (#174).
///
/// A record's layout is computed from its declared fields under natural alignment only when
/// no attribute could move a byte. Each attribute clang attaches to a record or field is
/// either classified here or refused with its exact reason ("unclassified layout effect"):
/// an attribute is never ignored because it looks harmless.
module AttributeContracts =

    /// What an attribute does to the layout of the record that carries it.
    type LayoutContract =
        /// The attribute changes size, alignment or field offsets. Natural alignment cannot
        /// represent it, so only clang's measured layout of the record is admitted.
        | LayoutAffecting
        /// The attribute changes no size, alignment or offset, and carries a semantic fact
        /// instead. The claim is confirmed per record, never assumed: in the record's declaring
        /// unit clang measures the record and a copy of it synthesized without the attribute
        /// (the same members, each of its original type), and the two must agree in size,
        /// alignment and every field offset.
        /// The fact is recorded in the record's descriptor, because Layer 2 code that
        /// reinterprets storage through the record depends on it.
        | LayoutNeutral of fact: string

    /// `__attribute__((__may_alias__))`, glibc's `__attribute_struct_may_alias__` on
    /// `struct sockaddr` and `struct sockaddr_storage`: GCC and clang exempt accesses through
    /// the type from type-based alias analysis. It moves no byte of the record.
    [<Literal>]
    let MayAliasFact =
        "aliasing: may_alias (clang MayAliasAttr); access through this type is exempt from type-based alias analysis and may alias an object of any type, so Layer 2 may reinterpret another record's storage through it"

    /// The classification of one clang attribute kind, or None when it is unclassified.
    let contractOf (kind: string) : LayoutContract option =
        match kind with
        | "PackedAttr" | "AlignedAttr" | "MaxFieldAlignmentAttr" -> Some LayoutAffecting
        | "MayAliasAttr" -> Some (LayoutNeutral MayAliasFact)
        | _ -> None

    /// The fact a layout-neutral attribute carries, or None for any other attribute.
    let neutralFact (kind: string) : string option =
        match contractOf kind with
        | Some (LayoutNeutral fact) -> Some fact
        | _ -> None
