namespace Farscape.Core

open CodeAST

/// Generates Errno constants module and Errno.describe function
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

    // CError struct removed — errno errors are represented as native Result<T, string>.
    // Errno.describe returns the human-readable string directly.

    /// Generate [<Literal>] constants with XML doc comments from header.
    /// Each constant gets its description as an XML doc comment for IDE support.
    let private generateLiteralConstants (constants: ErrnoConstant list) : FsDecl list =
        constants
        |> List.collect (fun c ->
            let doc =
                match c.Description with
                | Some d -> [ XmlDoc d ]
                | None -> []
            doc @ [ LiteralBinding(c.Name, Named "int", string c.Value) ])

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
        let defaultCase = ("other", Literal "\"Unknown error\"")
        MatchExpr(Identifier "code", cases @ [ defaultCase ])

    /// Generate the Errno submodule: constants + describe function.
    /// Wrapped in SubModule("Errno", ...) so `Errno.describe` resolves after opening the parent module.
    let generateErrnoDecls (constants: ErrnoConstant list) : FsDecl list =
        let literals = generateLiteralConstants constants
        let describeBody = generateDescribeBody constants
        let describeFunc =
            LetBinding(
                "describe",
                [ { Name = "code"; Type = Named "int" } ],
                Named "string",
                describeBody,
                [])
        let innerDecls =
            literals
            @ [ BlankLine
                XmlDoc "Errno code to description string. Generated from header comments."
                XmlDoc "Compiles to a jump table with string pointers into rodata. Zero allocation."
                describeFunc ]
        [ SubModule("Errno", innerDecls) ]

    /// Declare the opaque thread-local errno cell. Reading it requires a native
    /// scalar accessor; this declaration grants no interior dereference operation.
    let generateErrnoLocationExternWithModel (model: Types.PlatformABI) (cLibraryName: string) : FsDecl list =
        let pointer = TypeMapper.pointerRepr model "int *" |> DescriptorGenerator.typeRefSource
        [ XmlDoc "Returns an opaque handle to the current thread's errno cell."
          LetBinding("__errno_location", [], Generic("CHandle", Named "int"), NativeZeroed,
                     [$"FidelityExtern(\"{cLibraryName}\", \"__errno_location\")"])
          ValueBinding("__errno_locationDescriptor", Generic("Expr", Named "FunctionDescriptor"),
              Quoted(RecordBlock [
                  "CName", Literal "\"__errno_location\""
                  "Parameters", ArrayBlock []
                  "ReturnType", Literal pointer
                  "CallingConvention", Identifier "CDecl"
                  "OwnershipTransfer", Identifier "Borrowed" ])) ]

    let generateErrnoLocationExtern cLibraryName =
        generateErrnoLocationExternWithModel Types.LP64 cLibraryName

    /// Generate the complete errno infrastructure as a rendered source string.
    /// Produces: __errno_location extern + Errno submodule (constants + describe).
    /// Always generates output — __errno_location and describe are needed even without errno constants.
    let generateWithModel
        (macros: CppParser.MacroDecl list)
        (namespace': string)
        (cLibraryName: string)
        (model: Types.PlatformABI)
        : string =

        let constants = filterErrnoMacros macros
        let externDecl = generateErrnoLocationExternWithModel model cLibraryName
        let errnoDecls = generateErrnoDecls constants
        let allDecls = [ OpenModule "BAREWire.Descriptors" ] @ externDecl @ [ BlankLine ] @ errnoDecls
        let moduleDecl = Module(namespace', $"Errno infrastructure for {cLibraryName}", allDecls)
        CodeRenderer.render moduleDecl

    let generate macros namespace' cLibraryName =
        generateWithModel macros namespace' cLibraryName Types.LP64
