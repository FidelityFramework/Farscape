module Farscape.Tests.WrapperCodeGeneratorTests

open Xunit
open Farscape.Core
open Farscape.Core.WrapperTypes
open TestHelpers

// =============================================================================
// WrapperCodeGenerator Tests: End-to-end wrapper generation
// =============================================================================

let private generateSingle name retType parms attrs =
    let decls = [ mkDecl name retType parms attrs ]
    WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" NoErrors Types.LP64 None

[<Fact>]
let ``CountOrError wrapper without error convention generates direct passthrough`` () =
    let output = generateSingle "read" "ssize_t"
                    [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                    []
    Assert.Contains("let read", output)
    Assert.Contains("Platform.Bindings.Test.read", output)
    // NoErrors: direct passthrough, no Result wrapping
    Assert.DoesNotContain("Result<", output)
    Assert.DoesNotContain("if result", output)

[<Fact>]
let ``AllocatedPointer wrapper without error convention generates direct passthrough`` () =
    let output = generateSingle "malloc" "void *"
                    [("size", "size_t")]
                    [mkAttr "AllocSizeAttr" [0] None]
    Assert.Contains("let malloc", output)
    Assert.Contains("Platform.Bindings.Test.malloc", output)
    // NoErrors: direct passthrough, no null check
    Assert.DoesNotContain("Result<", output)
    Assert.DoesNotContain("if result", output)

[<Fact>]
let ``PureValue wrapper generates direct delegation`` () =
    let output = generateSingle "abs" "int"
                    [("x", "int")]
                    [mkAttr "PureAttr" [] None]
    Assert.Contains("let abs", output)
    Assert.Contains("Platform.Bindings.Test.abs", output)
    // Should NOT have Result wrapping
    Assert.DoesNotContain("Result<", output)
    Assert.DoesNotContain("if result", output)

[<Fact>]
let ``ZeroSuccessOrError wrapper without error convention generates direct passthrough`` () =
    let output = generateSingle "fclose" "int"
                    [("stream", "FILE *")]
                    []
    Assert.Contains("let fclose", output)
    Assert.Contains("Platform.Bindings.Test.fclose", output)
    // NoErrors: direct passthrough, no zero check
    Assert.DoesNotContain("Result<", output)
    Assert.DoesNotContain("if result", output)

[<Fact>]
let ``NeverReturns wrapper generates direct delegation with unit return`` () =
    let output = generateSingle "abort" "void" []
                    [mkAttr "NoReturnAttr" [] None]
    Assert.Contains("let abort", output)
    Assert.Contains(": unit =", output)
    Assert.Contains("Platform.Bindings.Test.abort", output)
    Assert.DoesNotContain("Result<", output)

[<Fact>]
let ``VoidReturn wrapper generates direct delegation`` () =
    let output = generateSingle "free" "void"
                    [("ptr", "void *")]
                    []
    Assert.Contains("let free", output)
    Assert.Contains(": unit =", output)
    Assert.Contains("Platform.Bindings.Test.free", output)
    Assert.DoesNotContain("Result<", output)

[<Fact>]
let ``wrapper module includes open declaration for bindings`` () =
    let output = generateSingle "abs" "int" [("x", "int")] [mkAttr "PureAttr" [] None]
    Assert.Contains("open Platform.Bindings.Test", output)

[<Fact>]
let ``wrapper module has correct namespace`` () =
    let output = generateSingle "abs" "int" [("x", "int")] [mkAttr "PureAttr" [] None]
    Assert.Contains("module Wrappers.Test", output)

[<Fact>]
let ``wrapper generates XML doc with original C signature`` () =
    let output = generateSingle "read" "ssize_t"
                    [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                    []
    Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", output)

[<Fact>]
let ``multiple functions are deduplicated by name`` () =
    let decls = [
        mkDecl "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")] []
        mkDecl "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")] []
        mkDecl "write" "ssize_t" [("fd", "int"); ("buf", "const void *"); ("count", "size_t")] []
    ]
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" NoErrors Types.LP64 None
    // Count occurrences of "let read"; should be exactly 1
    let readCount = output.Split("let read") |> Array.length
    Assert.Equal(2, readCount) // split produces N+1 parts for N occurrences
    Assert.Contains("let write", output)

// =============================================================================
// Errno-Enabled Wrapper Generation Tests
// =============================================================================

module ErrnoWrapperTests =

    [<Theory>]
    [<InlineData("read", "ssize_t")>]
    [<InlineData("fclose", "int")>]
    [<InlineData("malloc", "void *")>]
    [<InlineData("strlen", "size_t")>]
    let ``errno wrapper emission requires a declared native scalar accessor`` name retType =
        let declarations = [mkDecl name retType [] []]
        let error = Assert.ThrowsAny<System.Exception>(fun () ->
            WrapperCodeGenerator.generate declarations "Wrappers.Test" "testlib" "Platform.Bindings.Test"
                (UseErrno "Fidelity.Errno") Types.LP64 None |> ignore)
        Assert.Contains("native scalar errno accessor", error.Message)
        Assert.Contains("cannot be dereferenced", error.Message)

// =============================================================================
// Enum Error Code Wrapper Generation Tests
// =============================================================================

module EnumErrorWrapperTests =

    // hipError_t is declared (as the HIP headers do) so the descriptor has its width; an
    // undeclared by-value result type is refused (FS9115). The measurement is synthetic: these
    // tests are about wrapper shape, not enum measurement.
    let private hipError =
        CppParser.Declaration.Enum (mkMeasuredEnum "hipError_t" [mkEnumVal "hipSuccess" 0L; mkEnumVal "hipErrorInvalidValue" 1L] 32 false)

    let private generateWithEnumError name retType parms attrs =
        let decls = [ hipError; mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test"
            (UseEnumError ("hipError_t", 0L, "HipError", "Fidelity.HIP.HipError")) Types.LP64 None

    [<Fact>]
    let ``enum error wrapper generates match expression`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("match result with", output)
        Assert.Contains("| 0 ->", output)
        Assert.DoesNotContain("0L", output)

    [<Fact>]
    let ``enum error wrapper uses string error type in return`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("Result<unit, string>", output)

    [<Fact>]
    let ``enum error wrapper calls capture function on error path`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("captureHipError", output)
        Assert.Contains("Ok ()", output)

    [<Fact>]
    let ``enum error wrapper generates capture function returning string`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("let captureHipError", output)
        Assert.Contains("describe", output)

    [<Fact>]
    let ``enum error applies to int-returning function in enum_error_code library`` () =
        // C APIs often declare int return type for functions that semantically return an error enum.
        // When pilot TOML declares enum_error_code, int-returning functions get enum wrapping.
        let output = generateWithEnumError "hipGetDeviceCount" "int"
                        [("count", "int *")]
                        []
        // Should use enum match pattern with int-appropriate literal (no suffix)
        Assert.Contains("match result with", output)
        Assert.Contains("| 0 ->", output)
        Assert.Contains("Result<unit, string>", output)

    [<Fact>]
    let ``enum error preserves an ordinary integer literal for fixed width native returns`` () =
        // resvg scenario: C function returns int32_t but semantically returns resvg_error enum
        let output = generateWithEnumError "hipMalloc" "int32_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        // The descriptor carries the width; the source comparison stays an int.
        Assert.Contains("match result with", output)
        Assert.Contains("| 0 ->", output)
        Assert.DoesNotContain("0l", output)
        Assert.Contains("Result<unit, string>", output)

    [<Fact>]
    let ``PureValue wrapper unchanged with enum error enabled`` () =
        let output = generateWithEnumError "hipDeviceSynchronize" "hipError_t"
                        []
                        [{ CppParser.AttributeData.Kind = "PureAttr"; Args = []; StringArg = None }]
        // Pure functions don't use Result wrapping — check only the function section
        Assert.Contains("let hipDeviceSynchronize", output)
        Assert.DoesNotContain("Result<", output.Split("let hipDeviceSynchronize").[1])

// =============================================================================
// NullWithReason Wrapper Generation Tests
// =============================================================================

module NullWithReasonWrapperTests =

    let private expectedReasonDiagnostic =
        "null_with_reason wrappers require a declared reason return type, nullability, native text extent and ownership projection for 'stbi_failure_reason'; an opaque CHandle cannot be treated as a reason string."

    let private allocation =
        mkDecl "stbi_load" "void *"
            [("filename", "const char *"); ("x", "int *"); ("y", "int *"); ("channels", "int *"); ("desired", "int")]
            [mkAttr "MallocAttr" [] None]

    let private assertRejected generate =
        let error = Assert.ThrowsAny<System.Exception>(fun () -> generate () |> ignore)
        Assert.Equal(expectedReasonDiagnostic, error.Message)

    let private generate declarations =
        WrapperCodeGenerator.generate declarations "Wrappers.Test" "testlib" "Platform.Bindings.Test"
            (UseNullWithReason "stbi_failure_reason") Types.LP64 None

    [<Fact>]
    let ``null reason convention cannot invent an undeclared reason return type`` () =
        assertRejected (fun () -> generate [allocation])

    [<Fact>]
    let ``nullable native reason declaration requires a supported text projection`` () =
        let reason = mkDecl "stbi_failure_reason" "const char *" [] []
        assertRejected (fun () -> generate [allocation; reason])

    [<Fact>]
    let ``nonnull reason evidence does not prove text extent or ownership`` () =
        let reason = mkDecl "stbi_failure_reason" "const char *" [] [mkAttr "ReturnsNonNullAttr" [] None]
        assertRejected (fun () -> generate [allocation; reason])

    [<Fact>]
    let ``null reason rejection also covers the explicit generation context`` () =
        let declarations = [allocation; mkDecl "stbi_failure_reason" "const char *" [] []]
        let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
        assertRejected (fun () ->
            WrapperCodeGenerator.generateWithContext (Some context) false Set.empty declarations
                "Wrappers.Test" "testlib" "Platform.Bindings.Test"
                (UseNullWithReason "stbi_failure_reason") Types.LP64 None)

    [<Fact>]
    let ``passthrough overrides do not silently discard an unsupported reason contract`` () =
        assertRejected (fun () ->
            WrapperCodeGenerator.generateWithOverrides false (Set.singleton "stbi_load") [allocation]
                "Wrappers.Test" "testlib" "Platform.Bindings.Test"
                (UseNullWithReason "stbi_failure_reason") Types.LP64 None)

    [<Theory>]
    [<InlineData("stbi_is_hdr", "int")>]
    [<InlineData("stbi_image_free", "void")>]
    let ``unsupported null reason configuration fails even for scalar or void functions`` name returnType =
        assertRejected (fun () -> generate [mkDecl name returnType [] []])

// =============================================================================
// Regression: zero-argument functions must emit () in raw call
// =============================================================================

[<Fact>]
let ``zero-arg function wrapper emits unit argument in raw call`` () =
    let output = generateSingle "resvg_options_create" "void *" [] []
    Assert.Contains("Platform.Bindings.Test.resvg_options_create ()", output)

// =============================================================================
// Returned error codes require no pointer reads.
// =============================================================================

let private generateWithReturnedError name retType parms attrs =
    let decls = [mkDecl name retType parms attrs]
    WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test"
        (UseReturnCode ("Test", "Fidelity.Test.ReturnCode")) Types.LP64 None

// ─── NTU DTS Zero Literal Tests ─────────────────────────────────────
// Wrappers must use `0` (NTU int), not `0l` (int32), for zero comparisons.

[<Fact>]
let ``ZeroSuccessOrError uses NTU int literal 0, not int32 literal 0l`` () =
    let output = generateWithReturnedError "fclose" "int"
                    [("stream", "FILE *")]
                    []
    Assert.Contains("= 0 then", output)
    Assert.DoesNotContain("0l", output)

[<Fact>]
let ``IntValueOrError uses NTU int literal 0, not int32 literal 0l`` () =
    let output = generateWithReturnedError "open" "int"
                    [("path", "const char *"); ("oflag", "int")]
                    []
    Assert.Contains(">= 0 then", output)
    Assert.DoesNotContain("0l", output)

[<Fact>]
let ``fixed width C return comparisons use an ordinary Clef integer literal`` () =
    let output = generateWithReturnedError "resvg_parse" "int32_t"
                    [("data", "const char *")]
                    []
    Assert.Contains("= 0 then", output)
    Assert.DoesNotContain("0l", output)

// =============================================================================
// ReturnCode Wrapper Generation Tests
// =============================================================================

module ReturnCodeWrapperTests =

    let private generateWithReturnCode name retType parms attrs =
        let decls = [ mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test"
            (UseReturnCode ("Xrt", "Fidelity.XRT.ReturnCode")) Types.LP64 None

    [<Fact>]
    let ``return code wrapper generates captureReturnCode helper`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.Contains("let captureReturnCode", output)
        Assert.Contains("describe", output)

    [<Fact>]
    let ``return code wrapper opens error module`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.Contains("open Fidelity.XRT.ReturnCode", output)

    [<Fact>]
    let ``ZeroSuccessOrError with return code generates Result<unit, string>`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.Contains("Result<unit, string>", output)
        Assert.Contains("= 0 then", output)
        Assert.Contains("Ok ()", output)
        Assert.Contains("captureReturnCode", output)

    [<Fact>]
    let ``return code wrapper uses int cast in error path`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.Contains("captureReturnCode (int ", output)

    [<Fact>]
    let ``AllocatedPointer with return code generates static error string`` () =
        let output = generateWithReturnCode "xrtDeviceOpen" "void *"
                        [("index", "unsigned int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        // Handle-returning functions use a static error string, not captureReturnCode
        Assert.Contains("Result<CHandle<unit>, string>", output)
        Assert.Contains("returned null handle", output)

    [<Fact>]
    let ``PureValue wrapper unchanged with return code enabled`` () =
        let output = generateWithReturnCode "xrtRunState" "int"
                        [("rhdl", "void *")]
                        [{ CppParser.AttributeData.Kind = "PureAttr"; Args = []; StringArg = None }]
        Assert.Contains("let xrtRunState", output)
        Assert.DoesNotContain("Result<", output.Split("let xrtRunState").[1])

    [<Fact>]
    let ``VoidReturn unchanged with return code enabled`` () =
        let output = generateWithReturnCode "xrtFree" "void"
                        [("ptr", "void *")]
                        []
        Assert.Contains("let xrtFree", output)
        Assert.Contains(": unit =", output)
        Assert.DoesNotContain("Result<", output.Split("let xrtFree").[1])

    [<Fact>]
    let ``return code captureReturnCode XML doc includes library prefix`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.Contains("Xrt return code", output)

    [<Fact>]
    let ``return code does not generate errno or enum error helpers`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.DoesNotContain("captureErrno", output)
        Assert.DoesNotContain("__errno_location", output)
        Assert.DoesNotContain("match result with", output)

// =============================================================================
// Opaque Handle Return Type Tests
// =============================================================================

[<Fact>]
let ``wrapper preserves nominal CHandle when unwrapping an opaque nullable return`` () =
    // Declaration with opaque handle typedef + function returning that type
    let decls = [
        CppParser.Declaration.Typedef
            { Name = "hipStream_t"; UnderlyingType = "struct ihipStream_t *"; Documentation = None; Attributes = [] }
        CppParser.Declaration.Function
            { Name = "hipStreamCreate"
              ReturnType = "hipStream_t"
              Parameters = [("flags", "unsigned int")]
              Documentation = None
              IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None; Location = TestHelpers.constructed; ByValueTypedefs = [] }
    ]
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseReturnCode ("Test", "Fidelity.Test.ReturnCode")) Types.LP64 None
    Assert.Contains("Result<CHandle<hipStream_t>, string>", output)
    Assert.Contains("match Platform.Bindings.Test.hipStreamCreate flags with", output)
    Assert.Contains("Some result -> Ok result", output)
    Assert.DoesNotContain("Result<nativeint, string>", output)

[<Fact>]
let ``pointer allocation wrapper returns an opaque CHandle result`` () =
    // Regular void* return without opaque handle typedef
    let decls = [
        CppParser.Declaration.Function
            { Name = "mmap"
              ReturnType = "void *"
              Parameters = [("addr", "void *"); ("length", "size_t")]
              Documentation = None
              IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None; Location = TestHelpers.constructed; ByValueTypedefs = [] }
    ]
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseReturnCode ("Test", "Fidelity.Test.ReturnCode")) Types.LP64 None
    // A C void pointer stays opaque in Clef.
    Assert.Contains("Result<CHandle<unit>, string>", output)
