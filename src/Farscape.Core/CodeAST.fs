namespace Farscape.Core

/// Typed representation of generated F# code.
///
/// Instead of building strings with StringBuilder, generation produces FsDecl values.
/// These are typed, inspectable, testable AST nodes.
/// The ONLY StringBuilder in Farscape is in CodeRenderer.fs which converts these to strings.
module CodeAST =

    /// F# type representation
    type FsType =
        /// Simple named type: "int32", "nativeint", "byte", "unit"
        | Named of string
        /// Generic type: "nativeptr<byte>" → Generic("nativeptr", Named "byte")
        | Generic of string * FsType
        /// Two-parameter generic: "Result<nativeint, CError>" → Generic2("Result", Named "nativeint", Named "CError")
        | Generic2 of string * FsType * FsType
        /// Unit type (special case for void returns)
        | Unit

    /// F# expression representation
    type FsExpr =
        /// Unchecked.defaultof<T>, the standard Platform.Bindings body
        | DefaultOf of FsType
        /// Function call: Module.func arg1 arg2 (module' = "" for unqualified)
        | FunctionCall of module': string * name: string * args: FsExpr list
        /// Variable reference
        | Identifier of string
        /// Type conversion: int32 x, nativeint x
        | TypeConversion of targetType: string * expr: FsExpr
        /// Static method call: NativePtr.toNativeInt buffer
        | MethodCall of receiver: FsExpr * method': string
        /// Conditional: if cond then thenExpr else elseExpr
        | IfThenElse of cond: FsExpr * thenExpr: FsExpr * elseExpr: FsExpr
        /// Binary comparison: result >= 0L
        | Comparison of left: FsExpr * op: string * right: FsExpr
        /// Literal value: 0L, 0n, ()
        | Literal of string
        /// Ok wrapper: Ok x
        | ResultOk of FsExpr
        /// Error wrapper: Error x
        | ResultError of FsExpr
        /// Let binding in expression: let result = binding in body
        | LetIn of name: string * binding: FsExpr * body: FsExpr
        /// Record/struct construction: { Field1 = expr1; Field2 = expr2 }
        | RecordConstruction of fields: (string * FsExpr) list
        /// Match expression: match scrutinee with | pattern1 -> body1 | pattern2 -> body2
        | MatchExpr of scrutinee: FsExpr * cases: (string * FsExpr) list

    /// A function parameter
    type FsParam = {
        Name: string
        Type: FsType
    }

    /// A field with explicit byte offset (for StructLayout.Explicit structs)
    type ExplicitField = {
        Name: string
        Type: FsType
        OffsetBytes: int
    }

    /// F# declaration representation: the core of the typed code AST
    type FsDecl =
        /// Module declaration with namespace, header comment, and child declarations
        | Module of name: string * comment: string * decls: FsDecl list
        /// XML doc comment: /// text
        | XmlDoc of text: string
        /// Regular comment: // text
        | Comment of text: string
        /// Blank line separator
        | BlankLine
        /// let binding: let name (p1: t1) (p2: t2) : retType = body
        /// Attributes are rendered as [<Attr>] lines before the let.
        | LetBinding of name: string * params': FsParam list * returnType: FsType * body: FsExpr * attributes: string list
        /// [<Literal>] let name = value
        | LiteralBinding of name: string * value: string
        /// type Name = { field1: type1; field2: type2 }
        /// Attributes are rendered as [<Attr>] lines before the type.
        | RecordType of name: string * fields: (string * FsType) list * doc: string option * attributes: string list
        /// type Name = | Case1 = 0L | Case2 = 1L
        /// When isFlags is true, renders with [<System.Flags>] attribute.
        | EnumType of name: string * values: (string * int64) list * doc: string option * isFlags: bool
        /// Nested module: module Name = \n    decls (for companion modules)
        | SubModule of name: string * decls: FsDecl list
        /// ABI-critical struct with explicit layout.
        /// Rendered with [<StructLayout(LayoutKind.Explicit, Size=N)>] [<Struct>] and per-field [<FieldOffset(N)>].
        | ExplicitLayoutRecord of name: string * fields: ExplicitField list * sizeBytes: int * doc: string option
