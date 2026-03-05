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
        let args =
            match paramNames with
            | [] -> [ Literal "()" ]
            | _ -> paramNames |> List.map Identifier
        FunctionCall(bindingsModule, funcName, args)

    /// Build the error expression based on the error handling strategy.
    let private buildErrorExpr (errorHandling: ErrorHandling) (bindingsModule: string) (fallback: FsExpr) : FsExpr =
        match errorHandling with
        | UseErrno _ -> FunctionCall("", "captureErrno", [Literal "()"])
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

    /// Select the zero literal matching the C return type's NTU mapping.
    /// C `int` → NTU `int` → literal `0`; C `int32_t` → NTU `int32` → literal `0l`.
    let private zeroLiteralForReturnType (cReturnType: string) =
        let trimmed = cReturnType.Replace("const ", "").Trim()
        match trimmed with
        | "int32_t" | "__int32_t" -> "0l"
        | _ -> "0"

    /// Generate wrapper body for ZeroSuccessOrError pattern (e.g., fclose, fseek).
    /// if result = 0 then Ok ()  (or 0l for int32_t-returning functions)
    let private zeroSuccessBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) (cReturnType: string) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Literal "()")
        let zero = zeroLiteralForReturnType cReturnType
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", "=", Literal zero),
                ResultOk(Literal "()"),
                ResultError(errorExpr)))

    /// Generate wrapper body for IntValueOrError pattern (e.g., open, socket, dup, fork).
    /// if result >= 0 then Ok result  (or 0l for int32_t-returning functions)
    let private intValueOrErrorBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) (cReturnType: string) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Identifier "result")
        let zero = zeroLiteralForReturnType cReturnType
        LetIn("result", rawCall,
            IfThenElse(
                Comparison(Identifier "result", ">=", Literal zero),
                ResultOk(Identifier "result"),
                ResultError(errorExpr)))

    /// Generate wrapper body for AllocatedPointer pattern (e.g., malloc, stbi_load).
    /// L1 returns option<nativeint>; match to unwrap:
    /// match Bindings.malloc size with
    /// | Some result -> Ok result
    /// | None -> Error (captureReason ())
    let private allocatedPointerBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let errorExpr = buildErrorExpr errorHandling bindingsModule (Literal "()")
        MatchExpr(rawCall,
            [("Some result", ResultOk(Identifier "result"))
             ("None", ResultError(errorExpr))])

    /// Generate wrapper body for OpaqueHandleReturn pattern (e.g., fopen).
    /// Same structure as AllocatedPointer; null check.
    let private opaqueHandleBody (bindingsModule: string) (funcName: string) (paramNames: string list) (errorHandling: ErrorHandling) : FsExpr =
        allocatedPointerBody bindingsModule funcName paramNames errorHandling

    /// Select the integer literal suffix for an enum success value based on C return type.
    /// Enum error functions may return the enum type directly or an integer type (e.g. int32_t).
    let private enumSuccessLiteral (successIntValue: int64) (cReturnType: string) =
        let trimmed = cReturnType.Replace("const ", "").Trim()
        match trimmed with
        | "int32_t" | "__int32_t" | "uint32_t" | "__uint32_t" -> $"{successIntValue}l"
        | "int" | "unsigned int" -> $"{successIntValue}"
        | _ -> $"{successIntValue}L"

    /// Generate wrapper body for EnumReturnError pattern (e.g., HIP hipStreamCreate).
    /// let result = Bindings.hipStreamCreate stream
    /// match result with | 0L -> Ok () | err -> Error (captureEnumError err)
    /// Uses integer literal pattern because CCS does not yet register enum value bindings.
    /// When C return type differs from the enum type (e.g. int32_t vs resvg_error),
    /// inlines error construction to avoid type mismatch with captureEnumError.
    let private enumReturnErrorBody (bindingsModule: string) (funcName: string) (paramNames: string list)
                                    (enumType: string) (successIntValue: int64) (errorStructName: string) (cReturnType: string) : FsExpr =
        let rawCall = buildRawCall bindingsModule funcName paramNames
        let successLit = enumSuccessLiteral successIntValue cReturnType
        let returnTypeMatchesEnum = cReturnType.Replace("const ", "").Trim() = enumType
        let errorExpr =
            if returnTypeMatchesEnum then
                FunctionCall("", "captureEnumError", [Identifier "err"])
            else
                // C return type is integer, not the enum — inline error construction
                RecordConstruction [
                    ("ErrorCode", Identifier "err")
                    ("ErrorMessage", FunctionCall(errorStructName, "describe", [Identifier "err"]))
                ]
        LetIn("result", rawCall,
            MatchExpr(Identifier "result", [
                (successLit, ResultOk (Literal "()"))
                ("err", ResultError errorExpr)
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
        (cReturnType: string)
        : FsExpr =
        match semantic with
        | CountOrError       -> countOrErrorBody bindingsModule funcName paramNames errorHandling
        | ZeroSuccessOrError -> zeroSuccessBody bindingsModule funcName paramNames errorHandling cReturnType
        | IntValueOrError    -> intValueOrErrorBody bindingsModule funcName paramNames errorHandling cReturnType
        | AllocatedPointer   -> allocatedPointerBody bindingsModule funcName paramNames errorHandling
        | OpaqueHandleReturn -> opaqueHandleBody bindingsModule funcName paramNames errorHandling
        | EnumReturnError (enumType, successIntValue, errorStructName) ->
            enumReturnErrorBody bindingsModule funcName paramNames enumType successIntValue errorStructName cReturnType
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
        (nonnullAnnotations: PilotTypes.NonnullAnnotations option)
        (func: CppParser.FunctionDecl)
        : FsDecl list =

        let mapType = FidelityCodeGenerator.mapCTypeToFidelityType typedefMap model opaqueHandles Set.empty
        let pattern = WrapperPatternAnalyzer.analyze func typedefMap

        // Collect proven-nonnull parameter indices (same logic as FidelityCodeGenerator)
        let clangNonnull =
            func.Attributes
            |> List.collect (fun a ->
                match a.Kind with
                | "NonNullAttr" -> a.Args
                | _ -> [])
            |> Set.ofList
        let tomlNonnull =
            nonnullAnnotations
            |> Option.bind (fun a -> Map.tryFind func.Name a.Parameters)
            |> Option.defaultValue []
            |> Set.ofList
        let nonnullIndices = Set.union clangNonnull tomlNonnull

        // Override ReturnSemantic based on error convention:
        // - EnumError: override return type when it matches the enum error type or a compatible integer type
        //   (C APIs often declare int/int32_t return type even when semantically returning an error enum)
        // - NoErrors: all semantics become direct passthrough (no null checks, no Result wrapping)
        // - NullWithReason: pointer-returning functions keep their null check semantics
        // - Attribute-driven semantics (Pure/Const/NoReturn) always take precedence
        let isEnumCompatibleReturnType (cType: string) (enumType: string) =
            cType = enumType ||
            (match cType.Replace("const ", "").Trim() with
             | "int" | "int32_t" | "__int32_t" | "unsigned int" | "uint32_t" | "__uint32_t" -> true
             | _ -> false)
        let semantic =
            match errorHandling with
            | UseEnumError (enumType, successIntValue, errorStructName, _)
                when isEnumCompatibleReturnType func.ReturnType enumType && not pattern.IsPure && pattern.ReturnSemantic <> NeverReturns ->
                EnumReturnError (enumType, successIntValue, errorStructName)
            | NoErrors ->
                match pattern.ReturnSemantic with
                | AllocatedPointer | OpaqueHandleReturn | CountOrError | ZeroSuccessOrError -> PureValue
                | other -> other
            | _ -> pattern.ReturnSemantic

        // Parameter types match Layer 1 declarations (nullable-by-default for pointers)
        let parameters =
            func.Parameters
            |> List.mapi (fun idx (name, cType) ->
                let fsType = mapType cType
                let isPointer = FidelityCodeGenerator.isCDataPointer cType
                let isNullable = isPointer && not (nonnullIndices.Contains idx)
                let finalType = if isNullable then FidelityCodeGenerator.wrapOption fsType else fsType
                { FsParam.Name = cleanParamName name; Type = finalType })

        let rawRetType =
            let baseType = mapType func.ReturnType
            let returnIsPointer = FidelityCodeGenerator.isCDataPointer func.ReturnType
            let returnNonnull =
                nonnullAnnotations
                |> Option.map (fun a -> a.Returns.Contains func.Name)
                |> Option.defaultValue false
            let hasReturnsNonnullAttr =
                func.Attributes |> List.exists (fun a -> a.Kind = "ReturnsNonNullAttr")
            if returnIsPointer && not returnNonnull && not hasReturnsNonnullAttr
            then FidelityCodeGenerator.wrapOption baseType
            else baseType
        let retType = wrapperReturnType semantic rawRetType errorHandling

        let paramNames = parameters |> List.map (fun p -> p.Name)
        let body = generateBody bindingsModule func.Name paramNames semantic errorHandling func.ReturnType

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
        (nonnullAnnotations: PilotTypes.NonnullAnnotations option)
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
            |> List.collect (generateWrapperDecls typedefMap model opaqueHandles bindingsModule errorHandling nonnullAnnotations)

        // Phase 4: Build typed FsDecl tree; wrapper module opens the bindings module
        let openDecl = Comment $"open {bindingsModule}"

        // Error handling support: generate captureError helper based on strategy
        let errorDecls =
            match errorHandling with
            | UseErrno errnoModuleName ->
                let openErrno = Comment $"open {errnoModuleName}"
                // captureError helper: captures errno and builds CError with description
                // NativePtr intrinsics are Clef builtins — no namespace import needed
                // __errno_location returns nativeint → cast to nativeptr<int> → NativePtr.read
                // Delegates record construction to Errno.capture to avoid record type ambiguity
                // when multiple error types (CError, HipError) share identical field names
                let captureErrorBody =
                    FunctionCall("Errno", "capture",
                        [FunctionCall("NativePtr", "read",
                            [FunctionCall("NativePtr", "ofNativeInt",
                                [FunctionCall("", "__errno_location", [Literal "()"])])])])
                let captureErrorDecl =
                    LetBinding("captureErrno", [], Named "CError", captureErrorBody, [])
                [ openErrno; BlankLine
                  XmlDoc "Capture errno and build CError with description from header comments."
                  captureErrorDecl; BlankLine ]
            | UseEnumError (enumType, _, errorStructName, describeModuleName) ->
                let openErrorModule = Comment $"open {describeModuleName}"
                // Delegates to companion module's capture function to avoid record type ambiguity
                let captureErrorBody =
                    FunctionCall(errorStructName, "capture", [Identifier "code"])
                let captureErrorDecl =
                    LetBinding("captureEnumError",
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
