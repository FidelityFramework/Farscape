namespace Farscape.Core

open CodeAST

/// Generates CError struct type, Errno constants module, and Errno.describe function
/// from parsed C header macros enriched with raw header comments.
///
/// The generated Errno.describe function compiles to a jump table over rodata strings —
/// O(1) lookup, zero heap allocation at runtime. Error descriptions flow from C header
/// comments through Farscape's XParsec parser at codegen time into the binary.
module ErrnoModuleGenerator =

    /// An errno constant extracted from parsed macros: name, integer value, optional description
    type ErrnoConstant = {
        Name: string
        Value: int64
        Description: string option
    }

    /// Filter parsed macros to errno constants (E* names with integer values).
    /// Returns sorted by value for stable, readable output.
    let filterErrnoMacros (macros: CppParser.MacroDecl list) : ErrnoConstant list =
        macros
        |> List.choose (fun m ->
            // Errno constants are uppercase, start with E, second char is uppercase letter
            if m.Name.Length > 1 && m.Name.[0] = 'E' && System.Char.IsUpper(m.Name.[1]) then
                match m.Kind with
                | CppParser.SimpleValue v ->
                    match CTypeParser.tryParseInteger v with
                    | Some n when n > 0L && n < 4096L -> // Reasonable errno range
                        Some { Name = m.Name; Value = n; Description = m.Documentation }
                    | _ -> None
                | _ -> None
            else None)
        |> List.sortBy (fun c -> c.Value)
        |> List.distinctBy (fun c -> c.Name)

    /// Generate [<Struct>] type CError = { Code: int; Description: string }
    /// Code is NTU `int` (register-width dimensional) — errno is C `int`.
    let generateCErrorType () : FsDecl list =
        [
            RecordType(
                "CError",
                [ ("Code", Named "int"); ("Description", Named "string") ],
                Some "Stack-allocated FFI error — carries errno code and human-readable description.",
                [ "Struct" ])
        ]

    /// Generate [<Literal>] constants with XML doc comments from header.
    /// Each constant gets its description as an XML doc comment for IDE support.
    let private generateLiteralConstants (constants: ErrnoConstant list) : FsDecl list =
        constants
        |> List.collect (fun c ->
            let doc =
                match c.Description with
                | Some d -> [ XmlDoc d ]
                | None -> []
            doc @ [ LiteralBinding(c.Name, string c.Value) ])

    /// Generate the describe function body as a MatchExpr.
    /// match code with | 1 -> "Operation not permitted" | 2 -> "No such file..." | _ -> "Unknown error"
    let private generateDescribeBody (constants: ErrnoConstant list) : FsExpr =
        let cases =
            constants
            |> List.choose (fun c ->
                match c.Description with
                | Some desc ->
                    Some (c.Name, Literal $"\"{desc}\"")
                | None ->
                    Some (c.Name, Literal $"\"{c.Name}\""))
        let defaultCase = ("_", Literal "\"Unknown error\"")
        MatchExpr(Identifier "code", cases @ [ defaultCase ])

    /// Generate the complete Errno module: constants + describe function.
    /// Returns FsDecl list to be included in the output module.
    let generateErrnoDecls (constants: ErrnoConstant list) : FsDecl list =
        if constants.IsEmpty then []
        else
            let literals = generateLiteralConstants constants
            let describeBody = generateDescribeBody constants
            let describeFunc =
                LetBinding(
                    "describe",
                    [ { Name = "code"; Type = Named "int" } ],
                    Named "string",
                    describeBody,
                    [])
            literals
            @ [ BlankLine
                XmlDoc "Errno code to description string. Generated from header comments."
                XmlDoc "Compiles to a jump table with string pointers into rodata. Zero allocation."
                describeFunc ]

    /// Generate the complete errno infrastructure as a rendered F# source string.
    /// Produces: CError type + Errno module with constants and describe function.
    let generate
        (macros: CppParser.MacroDecl list)
        (namespace': string)
        (libraryName: string)
        : string option =

        let constants = filterErrnoMacros macros

        if constants.IsEmpty then None
        else
            let errorTypeDecls = generateCErrorType ()
            let errnoDecls = generateErrnoDecls constants
            let allDecls = errorTypeDecls @ [ BlankLine ] @ errnoDecls
            let moduleDecl = Module(namespace', libraryName, allDecls)
            Some (CodeRenderer.render moduleDecl)
