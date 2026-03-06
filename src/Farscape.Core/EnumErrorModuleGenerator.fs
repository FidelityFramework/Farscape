namespace Farscape.Core

open CodeAST

/// Generates typed error struct, describe function, and capture helper
/// from C enum declarations with doc comments. Parallel to ErrnoModuleGenerator
/// but for typed enum error codes (HIP hipError_t, XRT xrt_error_code, etc.).
///
/// The generated describe function compiles to a jump table over rodata strings —
/// same zero-allocation architecture as the errno pipeline. Error descriptions flow
/// from C header doc comments at codegen time into the binary, serving dual purpose:
/// CCS/LSP tooltips and runtime error reporting.
module EnumErrorModuleGenerator =

    /// Configuration for enum error code generation.
    type EnumErrorConfig = {
        /// The enum type name, e.g. "hipError_t"
        ErrorType: string
        /// The success variant name, e.g. "hipSuccess"
        SuccessValue: string
        /// The derived error struct name, e.g. "HipError"
        ErrorStructName: string
        /// Optional runtime error string function, e.g. "hipGetErrorString"
        ErrorStringFn: string option
        /// Optional runtime error name function, e.g. "hipGetErrorName"
        ErrorNameFn: string option
    }

    /// Derive an error struct name from an enum type name.
    /// hipError_t → HipError, xrt_error_code → XrtError
    let deriveErrorStructName (enumName: string) : string =
        let trimmed =
            if enumName.EndsWith("_t") then enumName.[..enumName.Length-3]
            elif enumName.EndsWith("_code") then enumName.[..enumName.Length-6]
            elif enumName.EndsWith("_status") then enumName.[..enumName.Length-8]
            else enumName
        trimmed.Split('_')
        |> Array.map (fun part ->
            if part.Length > 0 then
                string (System.Char.ToUpper(part.[0])) + part.[1..]
            else "")
        |> String.concat ""

    /// Build config from PilotTypes convention fields and derived struct name.
    let makeConfig (errorType: string) (successValue: string)
                   (errorStringFn: string option) (errorNameFn: string option) : EnumErrorConfig =
        { ErrorType = errorType
          SuccessValue = successValue
          ErrorStructName = deriveErrorStructName errorType
          ErrorStringFn = errorStringFn
          ErrorNameFn = errorNameFn }

    /// Generate the describe function body as a MatchExpr over enum integer values.
    /// match code with | 0 -> "Success" | 1 -> "InvalidValue" | _ -> "Unknown error"
    /// Uses integer literal patterns because CCS does not yet register enum value bindings.
    let private generateDescribeBody (config: EnumErrorConfig) (values: CppParser.EnumValue list) : FsExpr =
        let cases =
            values
            |> List.map (fun v ->
                let description =
                    match v.Documentation with
                    | Some d -> d
                    | None -> v.Name
                ($"{v.Value}L", Literal $"\"{description}\""))
        let defaultCase = ("other", Literal $"\"Unknown {config.ErrorType} error\"")
        MatchExpr(Identifier "code", cases @ [ defaultCase ])

    /// Generate the describe function as FsDecl list.
    let generateCompanionDecls (config: EnumErrorConfig) (values: CppParser.EnumValue list) : FsDecl list =
        let describeBody = generateDescribeBody config values
        let describeFunc =
            LetBinding(
                "describe",
                [ { Name = "code"; Type = Named "int32" } ],
                Named "string",
                describeBody,
                [])
        [ XmlDoc $"Error code to description string. Generated from header comments."
          XmlDoc "Compiles to a jump table with string pointers into rodata. Zero allocation."
          describeFunc ]

    /// Generate the complete enum error infrastructure as FsDecl list.
    /// Returns: SubModule with describe function. No custom error struct —
    /// errors are marshaled to string at the boundary.
    let generateDecls (config: EnumErrorConfig) (values: CppParser.EnumValue list) : FsDecl list =
        if values.IsEmpty then []
        else
            let companion = generateCompanionDecls config values
            [ SubModule(config.ErrorStructName, companion); BlankLine ]

    /// Generate the complete enum error module as a rendered source string.
    /// Finds the error enum in declarations, generates error struct + describe + capture.
    let generate
        (enumDecl: CppParser.EnumDecl)
        (config: EnumErrorConfig)
        (namespace': string)
        (openModules: string list)
        : string option =

        if enumDecl.Values.IsEmpty then None
        else
            let opens = openModules |> List.map OpenModule
            let decls = opens @ generateDecls config enumDecl.Values
            let moduleDecl = Module(namespace', $"Error infrastructure for {config.ErrorType}", decls)
            Some (CodeRenderer.render moduleDecl)
