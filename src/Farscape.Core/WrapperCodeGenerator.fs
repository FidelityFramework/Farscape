namespace Farscape.Core

open CodeAST
open ActivePatterns
open Types
open WrapperTypes

/// Generates Layer 2 idiomatic F# wrapper functions that call Layer 1 Platform.Bindings stubs.
///
/// Output is a single .fs file with:
///   module Fidelity.Wrappers.{Library}.{Category}
///   open Platform.Bindings.{Library}.{Category}
///
/// Wrappers add:
///   - Result<T, E> error handling for C error codes
///   - Proper type conversions
///   - Null pointer checking for allocations
///
/// Architecture:
///   WrapperPatternAnalyzer.analyze → WrapperPattern → FsExpr tree → FsDecl → CodeRenderer.render
///   Same catamorphism + typed AST architecture as FidelityCodeGenerator.
///   No DllImport, no Marshal, no BCL. Flows through PSG → MLIR.
module WrapperCodeGenerator =

    // =========================================================================
    // Wrapper Return Type Computation
    // =========================================================================

    /// Compute the wrapper's F# return type from ReturnSemantic and the raw mapped return type.
    let private wrapperReturnType (semantic: ReturnSemantic) (rawRetType: FsType) (errorHandling: ErrorHandling) : FsType =
        let errorType =
            match errorHandling with
            | UseErrno _ -> Named "CError"
            | UseNullWithReason _ -> Named "nativeint"
            | _ -> Unit
        let hasErrors = match errorHandling with NoErrors -> false | _ -> true
        match semantic with
        | CountOrError ->
            if hasErrors then Generic2("Result", rawRetType, errorType)
            else Generic("Result", rawRetType)
        | ZeroSuccessOrError ->
            if hasErrors then Generic2("Result", Unit, errorType)
            else Generic("Result", Unit)
        | IntValueOrError ->
            if hasErrors then Generic2("Result", rawRetType, errorType)
            else Generic("Result", rawRetType)
        | AllocatedPointer | OpaqueHandleReturn ->
            if hasErrors then Generic2("Result", Named "nativeint", errorType)
            else Generic("Result", Named "nativeint")
        | EnumReturnError (_, _, errorStructName) ->
            Generic2("Result", Unit, Named errorStructName)
        | PureValue ->
            rawRetType
        | NeverReturns ->
            Unit
        | VoidReturn ->
            Unit

    // =========================================================================
    // Wrapper Body Generation (FsExpr trees)
    // =========================================================================

    /// Build the FunctionCall expression that delegates to the raw binding.
    let private buildRawCall (bindingsModule: string) (funcName: string) (paramNames: string list) : FsExpr =
        let args = paramNames |> List.map Identifier
        FunctionCall(bindingsModule, funcName, args)

    /// Build the error expression based on the error handling strategy.
    let private buildErrorExpr (errorHandling: ErrorHandling) (bindingsModule: string) (fallback: FsExpr) : FsExpr =
        match errorHandling with
        | UseErrno _ -> FunctionCall("", "captureError", [Literal "()"])
        | UseNullWithReason reasonFn -> FunctionCall(bindingsModule, reasonFn, [Literal "()"])
        | _ -> fallback

    /// Generate wrapper body for CountOrError pattern (e.g., read, write).
    /// let result = Bindings.read fd buf count
    /// if result >= 0n then Ok result
    /// else Error (captureError ())
    let private countOrErrorBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Identifier "result")
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", ">=", Literal "0n"),
                ResultOk(Identifier "result"),
                ResultError(errorExpr)))

    /// Generate wrapper body for ZeroSuccessOrError pattern (e.g., fclose, fseek).
    /// let result = Bindings.fclose stream
    /// if result = 0l then Ok ()
    /// else Error (captureError ())
    let private zeroSuccessBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Identifier "result")
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", "=", Literal "0l"),
                ResultOk(Literal "()"),
                ResultError(errorExpr)))

    /// Generate wrapper body for IntValueOrError pattern (e.g., open, socket, dup, fork).
    /// let result = Bindings.open file oflag
    /// if result >= 0l then Ok result
    /// else Error (captureError ())
    let private intValueOrErrorBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Identifier "result")
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", ">=", Literal "0l"),
                ResultOk(Identifier "result"),
                ResultError(errorExpr)))

    /// Generate wrapper body for AllocatedPointer pattern (e.g., malloc, stbi_load).
    /// let result = Bindings.malloc size
    /// if result <> 0n then Ok result
    /// else Error (captureReason ())
    let private allocatedPointerBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Literal "()")
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", "<>", Literal "0n"),
                ResultOk(Identifier "result"),
                ResultError(errorExpr)))

    /// Generate wrapper body for OpaqueHandleReturn pattern (e.g., fopen).
    /// Same structure as AllocatedPointer; null check.
    let private opaqueHandleBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        allocatedPointerBody bindingsModule funcName paramNames errorHandling

    /// Generate wrapper body for EnumReturnError pattern (e.g., HIP hipStreamCreate).
    /// let result = Bindings.hipStreamCreate stream
    /// match result with | hipError_t.hipSuccess -> Ok () | err -> Error (captureError err)
    let private enumReturnErrorBody (bindingsModule: string) (funcName: string) (paramNames: string list)
                                    (enumType: string) (successValue: string) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        LetIn("result", rawCall,
            MatchExpr(Identifier "result", [
                ($"{enumType}.{successValue}", ResultOk (Literal "()"))
                ("err", ResultError (FunctionCall("", "captureError", [Identifier "err"])))
            ]))

    /// Generate wrapper body for PureValue pattern (e.g., abs, strlen).
    /// Direct delegation: Bindings.strlen s
    let private pureValueBody (bindingsModule: string) (funcName: string) (paramNames: string list) : FsExpr =
        buildRawCall bindingsModule funcName paramNames

    /// Generate wrapper body for NeverReturns pattern (e.g., abort, _exit).
    /// Direct delegation: Bindings.abort ()
    let private neverReturnsBody (bindingsModule: string) (funcName: string) (paramNames: string list) : FsExpr =
        buildRawCall bindingsModule funcName paramNames

    /// Generate wrapper body for VoidReturn pattern (e.g., free).
    /// Direct delegation: Bindings.free ptr
    let private voidReturnBody (bindingsModule: string) (funcName: string) (paramNames: string list) : FsExpr =
        buildRawCall bindingsModule funcName paramNames

    /// Select and generate the wrapper body based on ReturnSemantic.
    let private generateBody
        (bindingsModule: string)
        (funcName: string)
        (paramNames: string list)
        (semantic: ReturnSemantic)
        (errorHandling: ErrorHandling)
        : FsExpr =
        match semantic with
        | CountOrError       -> countOrErrorBody bindingsModule funcName paramNames errorHandling
        | ZeroSuccessOrError -> zeroSuccessBody bindingsModule funcName paramNames errorHandling
        | IntValueOrError    -> intValueOrErrorBody bindingsModule funcName paramNames errorHandling
        | AllocatedPointer   -> allocatedPointerBody bindingsModule funcName paramNames errorHandling
        | OpaqueHandleReturn -> opaqueHandleBody bindingsModule funcName paramNames errorHandling
        | EnumReturnError (enumType, successValue, _) ->
            enumReturnErrorBody bindingsModule funcName paramNames enumType successValue
        | PureValue          -> pureValueBody bindingsModule funcName paramNames
        | NeverReturns       -> neverReturnsBody bindingsModule funcName paramNames
        | VoidReturn         -> voidReturnBody bindingsModule funcName paramNames

    // =========================================================================
    // Declaration Generation
    // =========================================================================

    /// Format XML doc declarations: description (from header comment) + C signature.
    let private formatDocDecls (func: CppParser.FunctionDecl) : FsDecl list =
        let paramStr =
            func.Parameters
            |> List.map (fun (name, typ) -> $"{typ} {name}")
            |> String.concat ", "
        let cSignature = $"C signature: {func.ReturnType} {func.Name}({paramStr})"
        match func.Documentation with
        | Some doc ->
            [ XmlDoc doc
              XmlDoc ""
              XmlDoc cSignature ]
        | None ->
            [ XmlDoc cSignature ]

    /// Generate FsDecl list for a single wrapper function.
    let private generateWrapperDecls
        (typedefMap: Map<string, string>)
        (model: PlatformABI)
        (opaqueHandles: Set<string>)
        (bindingsModule: string)
        (errorHandling: ErrorHandling)
        (func: CppParser.FunctionDecl)
        : FsDecl list =

        let mapType = FidelityCodeGenerator.mapCTypeToFidelityType typedefMap model opaqueHandles
        let pattern = WrapperPatternAnalyzer.analyze func typedefMap

        // Override ReturnSemantic based on error convention:
        // - EnumError: override return type when it matches the enum error type
        // - NoErrors: all semantics become direct passthrough (no null checks, no Result wrapping)
        // - NullWithReason: pointer-returning functions keep their null check semantics
        // - Attribute-driven semantics (Pure/Const/NoReturn) always take precedence
        let semantic =
            match errorHandling with
            | UseEnumError (enumType, successValue, errorStructName, _)
                when func.ReturnType = enumType && not pattern.IsPure && pattern.ReturnSemantic <> NeverReturns ->
                EnumReturnError (enumType, successValue, errorStructName)
            | NoErrors ->
                match pattern.ReturnSemantic with
                | AllocatedPointer | OpaqueHandleReturn | CountOrError | ZeroSuccessOrError -> PureValue
                | other -> other
            | _ -> pattern.ReturnSemantic

        // Parameter types match the raw stubs exactly
        let parameters =
            func.Parameters
            |> List.map (fun (name, cType) ->
                { FsParam.Name = cleanParamName name; Type = mapType cType })

        let rawRetType = mapType func.ReturnType
        let retType = wrapperReturnType semantic rawRetType errorHandling

        let paramNames = parameters |> List.map (fun p -> p.Name)
        let body = generateBody bindingsModule func.Name paramNames semantic errorHandling

        formatDocDecls func @
        [
            LetBinding(func.Name, parameters, retType, body, [])
        ]

    // =========================================================================
    // Catamorphism-Based Generation
    // =========================================================================

    /// Wrapper declaration group: only functions produce wrappers.
    type private WrapperGroup =
        | WFunc of CppParser.FunctionDecl
        | WNone

    /// Wrapper algebra: only functions are wrapped; all other declarations are ignored.
    let private wrapperAlgebra : DeclarationAlgebra.DeclarationAlgebra<WrapperGroup> = {
        OnFunction = fun f -> WFunc f
        OnEnum = fun _ -> WNone
        OnStruct = fun _ -> WNone
        OnMacro = fun _ -> WNone
        OnTypedef = fun _ -> WNone
        OnNamespace = fun _ -> WNone
        OnClass = fun _ -> WNone
        OnDelegate = fun _ -> WNone
    }

    /// Generate a complete wrapper module from parsed declarations.
    /// Architecture: Catamorphism → WrapperPattern → FsExpr tree → FsDecl → CodeRenderer.render
    /// PlatformABI determines concrete widths for C int/long in NTU output.
    let generate
        (declarations: CppParser.Declaration list)
        (wrapperNamespace: string)
        (libraryName: string)
        (bindingsModule: string)
        (errorHandling: ErrorHandling)
        (model: PlatformABI)
        : string =

        // Phase 1: Build typedef resolution map (shared with FidelityCodeGenerator)
        let typedefMap = FidelityCodeGenerator.buildTypedefMap declarations

        // Phase 1.5: Detect opaque handle typedefs (shared with FidelityCodeGenerator)
        let opaqueHandles = FidelityCodeGenerator.detectOpaqueHandles declarations

        // Phase 2: Extract functions via catamorphism (ONE pass)
        let groups =
            DeclarationAlgebra.cataDeclarations wrapperAlgebra declarations

        // Phase 3: Generate wrapper declarations for each function
        let functions =
            groups
            |> List.choose (function WFunc f -> Some f | WNone -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateWrapperDecls typedefMap model opaqueHandles bindingsModule errorHandling)

        // Phase 4: Build typed FsDecl tree; wrapper module opens the bindings module
        let openDecl = Comment $"open {bindingsModule}"

        // Error handling support: generate captureError helper based on strategy
        let errorDecls =
            match errorHandling with
            | UseErrno errnoModuleName ->
                let openErrno = Comment $"open {errnoModuleName}"
                let openNativeInterop = Comment "open Microsoft.FSharp.NativeInterop"
                // captureError helper: captures errno and builds CError with description
                let captureErrorBody =
                    LetIn("code",
                        MethodCall(
                            FunctionCall(bindingsModule, "__errno_location", [Literal "()"]),
                            "NativePtr.read"),
                        RecordConstruction [
                            ("Code", Identifier "code")
                            ("Description", FunctionCall("Errno", "describe", [Identifier "code"]))
                        ])
                let captureErrorDecl =
                    LetBinding("captureError", [], Named "CError", captureErrorBody, [])
                [ openErrno; openNativeInterop; BlankLine
                  XmlDoc "Capture errno and build CError with description from header comments."
                  captureErrorDecl; BlankLine ]
            | UseEnumError (enumType, _, errorStructName, describeModuleName) ->
                let openErrorModule = Comment $"open {describeModuleName}"
                // captureError helper: builds error struct from the return code directly
                let captureErrorBody =
                    RecordConstruction [
                        ("Code", Identifier "code")
                        ("Description", FunctionCall(errorStructName, "describe", [Identifier "code"]))
                    ]
                let captureErrorDecl =
                    LetBinding("captureError",
                        [ { Name = "code"; Type = Named enumType } ],
                        Named errorStructName, captureErrorBody, [])
                [ openErrorModule; BlankLine
                  XmlDoc $"Capture {enumType} error with compile-time description from header comments."
                  captureErrorDecl; BlankLine ]
            | UseNullWithReason reasonFn ->
                // NullWithReason: no helper needed — the reason function is called directly
                // in the wrapper body via bindingsModule.reasonFn (). The error value is the
                // raw nativeint pointer to the C string returned by the reason function.
                [ Comment $"// Error handling: null returns call {reasonFn}() for reason string"
                  BlankLine ]
            | NoErrors -> []

        let allDecls = openDecl :: errorDecls @ BlankLine :: functions
        let moduleDecl = Module(wrapperNamespace, libraryName, allDecls)

        // Phase 5: Render to string (the ONLY StringBuilder, in CodeRenderer)
        CodeRenderer.render moduleDecl
