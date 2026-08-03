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

    let private generateWithErrno name retType parms attrs =
        let decls = [ mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseErrno "Fidelity.Errno") Types.LP64 None

    [<Fact>]
    let ``errno-enabled wrapper uses Result<T, string> return type`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("Result<nativeint, string>", output)

    [<Fact>]
    let ``errno-enabled wrapper includes captureErrno helper`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("let captureErrno", output)
        Assert.Contains("NativePtr.read", output)
        Assert.Contains("__errno_location", output)
        Assert.Contains("describe", output)

    [<Fact>]
    let ``errno-enabled wrapper opens errno module and submodule`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("open Fidelity.Errno", output)
        Assert.Contains("open Fidelity.Errno.Errno", output)
        // NativePtr.read is a Clef intrinsic — no Microsoft.FSharp.NativeInterop import needed
        Assert.DoesNotContain("Microsoft.FSharp.NativeInterop", output)

    [<Fact>]
    let ``errno-enabled CountOrError calls captureErrno in error path`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("Error (captureErrno ())", output)
        Assert.Contains("Ok result", output)

    [<Fact>]
    let ``errno-enabled ZeroSuccessOrError calls captureErrno`` () =
        let output = generateWithErrno "fclose" "int"
                        [("stream", "void *")]
                        []
        Assert.Contains("Result<unit, string>", output)
        Assert.Contains("Error (captureErrno ())", output)

    [<Fact>]
    let ``errno-enabled AllocatedPointer calls captureErrno`` () =
        let output = generateWithErrno "malloc" "void *"
                        [("size", "size_t")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.Contains("Result<nativeint, string>", output)
        Assert.Contains("Error (captureErrno ())", output)

    [<Fact>]
    let ``errno-enabled captureErrno returns string via describe`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        // captureErrno calls describe to return human-readable string (Errno submodule opened)
        Assert.Contains("describe (NativePtr.read", output)
        Assert.Contains("NativePtr.ofNativeInt", output)

    [<Fact>]
    let ``PureValue wrapper unchanged with errno enabled`` () =
        let output = generateWithErrno "strlen" "size_t"
                        [("s", "const char *")]
                        [{ CppParser.AttributeData.Kind = "PureAttr"; Args = []; StringArg = None }]
        // Pure functions don't use Result wrapping — direct delegation
        Assert.Contains("let strlen", output)
        Assert.DoesNotContain("Result<", output.Split("let strlen").[1])

// =============================================================================
// Enum Error Code Wrapper Generation Tests
// =============================================================================

module EnumErrorWrapperTests =

    let private generateWithEnumError name retType parms attrs =
        let decls = [ mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test"
            (UseEnumError ("hipError_t", 0L, "HipError", "Fidelity.HIP.HipError")) Types.LP64 None

    [<Fact>]
    let ``enum error wrapper generates match expression`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("match result with", output)
        Assert.Contains("| 0L ->", output)

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
    let ``enum error applies to int32_t-returning function with int32 literal`` () =
        // resvg scenario: C function returns int32_t but semantically returns resvg_error enum
        let output = generateWithEnumError "hipMalloc" "int32_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        // Should use enum match pattern with int32-appropriate literal (l suffix)
        Assert.Contains("match result with", output)
        Assert.Contains("| 0l ->", output)
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

    let private generateWithNullReason name retType parms attrs =
        let decls = [ mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test"
            (UseNullWithReason "stbi_failure_reason") Types.LP64 None

    [<Fact>]
    let ``null_with_reason AllocatedPointer uses Result<nativeint, nativeint>`` () =
        let output = generateWithNullReason "stbi_load" "void *"
                        [("filename", "const char *"); ("x", "int *"); ("y", "int *"); ("channels", "int *"); ("desired", "int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.Contains("Result<nativeint, nativeint>", output)

    [<Fact>]
    let ``null_with_reason calls reason function on null`` () =
        let output = generateWithNullReason "stbi_load" "void *"
                        [("filename", "const char *"); ("x", "int *"); ("y", "int *"); ("channels", "int *"); ("desired", "int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.Contains("stbi_failure_reason", output)
        Assert.Contains("Error", output)
        Assert.Contains("Ok result", output)

    [<Fact>]
    let ``null_with_reason includes null check`` () =
        let output = generateWithNullReason "stbi_load" "void *"
                        [("filename", "const char *"); ("x", "int *"); ("y", "int *"); ("channels", "int *"); ("desired", "int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.Contains("| Some result ->", output)
        Assert.Contains("| None ->", output)

    [<Fact>]
    let ``null_with_reason does not generate captureError helper`` () =
        let output = generateWithNullReason "stbi_load" "void *"
                        [("filename", "const char *"); ("x", "int *"); ("y", "int *"); ("channels", "int *"); ("desired", "int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.DoesNotContain("let captureErrno", output)
        Assert.DoesNotContain("captureError", output)  // No capture*Error helper for null_with_reason
        Assert.DoesNotContain("__errno_location", output)

    [<Fact>]
    let ``null_with_reason PureValue unchanged`` () =
        let output = generateWithNullReason "stbi_is_hdr" "int"
                        [("filename", "const char *")]
                        [{ CppParser.AttributeData.Kind = "PureAttr"; Args = []; StringArg = None }]
        Assert.Contains("let stbi_is_hdr", output)
        Assert.DoesNotContain("Result<", output.Split("let stbi_is_hdr").[1])

    [<Fact>]
    let ``null_with_reason VoidReturn unchanged`` () =
        let output = generateWithNullReason "stbi_image_free" "void"
                        [("retval_from_stbi_load", "void *")]
                        []
        Assert.Contains("let stbi_image_free", output)
        Assert.DoesNotContain("Result<", output.Split("let stbi_image_free").[1])

// =============================================================================
// Regression: zero-argument functions must emit () in raw call
// =============================================================================

[<Fact>]
let ``zero-arg function wrapper emits unit argument in raw call`` () =
    let output = generateSingle "resvg_options_create" "void *" [] []
    Assert.Contains("Platform.Bindings.Test.resvg_options_create ()", output)

// =============================================================================
// Regression: ZeroSuccessOrError with errno must use Error (captureError ()) not Error result
// =============================================================================

let private generateWithErrno name retType parms attrs =
    let decls = [ mkDecl name retType parms attrs ]
    WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseErrno "Platform.Bindings.Test.Errno") Types.LP64 None

[<Fact>]
let ``ZeroSuccessOrError with errno generates Error captureError not Error result`` () =
    let output = generateWithErrno "fclose" "int"
                    [("stream", "FILE *")]
                    []
    // Should use captureError in the error branch, not bare result
    Assert.Contains("Error (captureErrno ())", output)
    Assert.DoesNotContain("Error result", output)

// ─── NTU DTS Zero Literal Tests ─────────────────────────────────────
// Wrappers must use `0` (NTU int), not `0l` (int32), for zero comparisons.

[<Fact>]
let ``ZeroSuccessOrError uses NTU int literal 0, not int32 literal 0l`` () =
    let output = generateWithErrno "fclose" "int"
                    [("stream", "FILE *")]
                    []
    Assert.Contains("= 0 then", output)
    Assert.DoesNotContain("0l", output)

[<Fact>]
let ``IntValueOrError uses NTU int literal 0, not int32 literal 0l`` () =
    let output = generateWithErrno "open" "int"
                    [("path", "const char *"); ("oflag", "int")]
                    []
    Assert.Contains(">= 0 then", output)
    Assert.DoesNotContain("0l", output)

[<Fact>]
let ``int32_t returning function uses int32 literal 0l for zero comparison`` () =
    let output = generateWithErrno "resvg_parse" "int32_t"
                    [("data", "const char *")]
                    []
    Assert.Contains("= 0l then", output)
    Assert.DoesNotContain("= 0 then", output)

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
    let ``return code wrapper uses int32 cast in error path`` () =
        let output = generateWithReturnCode "xrtDeviceClose" "int"
                        [("dhdl", "void *")]
                        []
        Assert.Contains("captureReturnCode (int32", output)

    [<Fact>]
    let ``AllocatedPointer with return code generates static error string`` () =
        let output = generateWithReturnCode "xrtDeviceOpen" "void *"
                        [("index", "unsigned int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        // Handle-returning functions use a static error string, not captureReturnCode
        Assert.Contains("Result<nativeint, string>", output)
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
let ``wrapper returns Result<HandleType, string> for opaque handle return with errno`` () =
    // Declaration with opaque handle typedef + function returning that type
    let decls = [
        CppParser.Declaration.Typedef
            { Name = "hipStream_t"; UnderlyingType = "struct ihipStream_t *"; Documentation = None }
        CppParser.Declaration.Function
            { Name = "hipStreamCreate"
              ReturnType = "hipStream_t"
              Parameters = [("flags", "unsigned int")]
              Documentation = None
              IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }
    ]
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseErrno "Fidelity.Errno") Types.LP64 None
    // Return type should use the handle type, not nativeint
    Assert.Contains("Result<hipStream_t, string>", output)
    Assert.DoesNotContain("Result<nativeint, string>", output)

[<Fact>]
let ``wrapper returns Result<nativeint, string> for non-opaque pointer with errno`` () =
    // Regular void* return without opaque handle typedef
    let decls = [
        CppParser.Declaration.Function
            { Name = "mmap"
              ReturnType = "void *"
              Parameters = [("addr", "void *"); ("length", "size_t")]
              Documentation = None
              IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }
    ]
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseErrno "Fidelity.Errno") Types.LP64 None
    // void* should use nativeint, not a handle type
    Assert.Contains("Result<nativeint, string>", output)
