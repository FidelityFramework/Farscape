namespace Farscape.Core

open CodeAST
open ActivePatterns
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
    let private wrapperReturnType (semantic: ReturnSemantic) (rawRetType: FsType) (useErrno: bool) : FsType =
        match semantic with
        | CountOrError ->
            if useErrno then Generic2("Result", rawRetType, Named "CError")
            else Generic("Result", rawRetType)
        | ZeroSuccessOrError ->
            if useErrno then Generic2("Result", Unit, Named "CError")
            else Generic("Result", Unit)
        | AllocatedPointer | OpaqueHandleReturn ->
            if useErrno then Generic2("Result", Named "nativeint", Named "CError")
            else Generic("Result", Named "nativeint")
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

    /// Generate wrapper body for CountOrError pattern (e.g., read, write).
    /// let result = Bindings.read fd buf count
    /// if result >= 0n then Ok result
    /// else Error result
    let private countOrErrorBody (bindingsModule: string) (funcName: string) (paramNames: string list) (useErrno: bool) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr =
            if useErrno then FunctionCall("", "captureError", [Literal "()"])
            else Identifier "result"
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", ">=", Literal "0n"),
                ResultOk(Identifier "result"),
                ResultError(errorExpr)))

    /// Generate wrapper body for ZeroSuccessOrError pattern (e.g., fclose, fseek).
    /// let result = Bindings.fclose stream
    /// if result = 0l then Ok ()
    /// else Error result
    let private zeroSuccessBody (bindingsModule: string) (funcName: string) (paramNames: string list) (useErrno: bool) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr =
            if useErrno then FunctionCall("", "captureError", [Literal "()"])
            else Identifier "result"
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", "=", Literal "0l"),
                ResultOk(Literal "()"),
                ResultError(errorExpr)))

    /// Generate wrapper body for AllocatedPointer pattern (e.g., malloc, calloc).
    /// let result = Bindings.malloc size
    /// if result <> 0n then Ok result
    /// else Error ()
    let private allocatedPointerBody (bindingsModule: string) (funcName: string) (paramNames: string list) (useErrno: bool) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr =
            if useErrno then FunctionCall("", "captureError", [Literal "()"])
            else Literal "()"
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", "<>", Literal "0n"),
                ResultOk(Identifier "result"),
                ResultError(errorExpr)))

    /// Generate wrapper body for OpaqueHandleReturn pattern (e.g., fopen).
    /// Same structure as AllocatedPointer; null check.
    let private opaqueHandleBody (bindingsModule: string) (funcName: string) (paramNames: string list) (useErrno: bool) : FsExpr =
        allocatedPointerBody bindingsModule funcName paramNames useErrno

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
        (useErrno: bool)
        : FsExpr =
        match semantic with
        | CountOrError       -> countOrErrorBody bindingsModule funcName paramNames useErrno
        | ZeroSuccessOrError -> zeroSuccessBody bindingsModule funcName paramNames useErrno
        | AllocatedPointer   -> allocatedPointerBody bindingsModule funcName paramNames useErrno
        | OpaqueHandleReturn -> opaqueHandleBody bindingsModule funcName paramNames useErrno
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
        (bindingsModule: string)
        (useErrno: bool)
        (func: CppParser.FunctionDecl)
        : FsDecl list =

        let mapType = FidelityCodeGenerator.mapCTypeToFidelityType typedefMap
        let pattern = WrapperPatternAnalyzer.analyze func typedefMap

        // Parameter types match the raw stubs exactly
        let parameters =
            func.Parameters
            |> List.map (fun (name, cType) ->
                { FsParam.Name = cleanParamName name; Type = mapType cType })

        let rawRetType = mapType func.ReturnType
        let retType = wrapperReturnType pattern.ReturnSemantic rawRetType useErrno

        let paramNames = parameters |> List.map (fun p -> p.Name)
        let body = generateBody bindingsModule func.Name paramNames pattern.ReturnSemantic useErrno

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
    }

    /// Generate a complete wrapper module from parsed declarations.
    /// Architecture: Catamorphism → WrapperPattern → FsExpr tree → FsDecl → CodeRenderer.render
    let generate
        (declarations: CppParser.Declaration list)
        (wrapperNamespace: string)
        (libraryName: string)
        (bindingsModule: string)
        (errnoModuleName: string option)
        : string =

        let useErrno = errnoModuleName.IsSome

        // Phase 1: Build typedef resolution map (shared with FidelityCodeGenerator)
        let typedefMap = FidelityCodeGenerator.buildTypedefMap declarations

        // Phase 2: Extract functions via catamorphism (ONE pass)
        let groups =
            DeclarationAlgebra.cataDeclarations wrapperAlgebra declarations

        // Phase 3: Generate wrapper declarations for each function
        let functions =
            groups
            |> List.choose (function WFunc f -> Some f | WNone -> None)
            |> List.distinctBy (fun f -> f.Name)
            |> List.collect (generateWrapperDecls typedefMap bindingsModule useErrno)

        // Phase 4: Build typed FsDecl tree; wrapper module opens the bindings module
        let openDecl = Comment $"open {bindingsModule}"

        // Errno support: open errno module and generate captureError helper
        let errnoDecls =
            match errnoModuleName with
            | Some modName ->
                let openErrno = Comment $"open {modName}"
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
            | None -> []

        let allDecls = openDecl :: errnoDecls @ BlankLine :: functions
        let moduleDecl = Module(wrapperNamespace, libraryName, allDecls)

        // Phase 5: Render to string (the ONLY StringBuilder, in CodeRenderer)
        CodeRenderer.render moduleDecl
