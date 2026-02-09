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

    /// A function parameter
    type FsParam = {
        Name: string
        Type: FsType
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
        | LetBinding of name: string * params': FsParam list * returnType: FsType * body: FsExpr
        /// [<Literal>] let name = value
        | LiteralBinding of name: string * value: string
        /// type Name = { field1: type1; field2: type2 }
        | RecordType of name: string * fields: (string * FsType) list * doc: string option
        /// type Name = | Case1 = 0L | Case2 = 1L
        | EnumType of name: string * values: (string * int64) list * doc: string option
