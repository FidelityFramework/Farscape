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
    WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" NoErrors Types.LP64

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
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" NoErrors Types.LP64
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
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (UseErrno "Fidelity.Errno") Types.LP64

    [<Fact>]
    let ``errno-enabled wrapper uses Result<T, CError> return type`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("Result<nativeint, CError>", output)

    [<Fact>]
    let ``errno-enabled wrapper includes captureError helper`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("let captureError", output)
        Assert.Contains("NativePtr.read", output)
        Assert.Contains("__errno_location", output)
        Assert.Contains("Errno.describe", output)

    [<Fact>]
    let ``errno-enabled wrapper opens errno module`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("open Fidelity.Errno", output)
        Assert.Contains("open Microsoft.FSharp.NativeInterop", output)

    [<Fact>]
    let ``errno-enabled CountOrError calls captureError in error path`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("Error (captureError ())", output)
        Assert.Contains("Ok result", output)

    [<Fact>]
    let ``errno-enabled ZeroSuccessOrError calls captureError`` () =
        let output = generateWithErrno "fclose" "int"
                        [("stream", "void *")]
                        []
        Assert.Contains("Result<unit, CError>", output)
        Assert.Contains("Error (captureError ())", output)

    [<Fact>]
    let ``errno-enabled AllocatedPointer calls captureError`` () =
        let output = generateWithErrno "malloc" "void *"
                        [("size", "size_t")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.Contains("Result<nativeint, CError>", output)
        Assert.Contains("Error (captureError ())", output)

    [<Fact>]
    let ``errno-enabled captureError builds CError record`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        // captureError body should construct { Code = code; Description = Errno.describe code }
        Assert.Contains("Code = code", output)
        Assert.Contains("Description = Errno.describe code", output)

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
            (UseEnumError ("hipError_t", "hipSuccess", "HipError", "Fidelity.HIP.HipError")) Types.LP64

    [<Fact>]
    let ``enum error wrapper generates match expression`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("match result with", output)
        Assert.Contains("hipError_t.hipSuccess", output)

    [<Fact>]
    let ``enum error wrapper uses typed error struct in return`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("Result<unit, HipError>", output)

    [<Fact>]
    let ``enum error wrapper calls captureError on error path`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("captureError", output)
        Assert.Contains("Ok ()", output)

    [<Fact>]
    let ``enum error wrapper generates captureError helper`` () =
        let output = generateWithEnumError "hipMalloc" "hipError_t"
                        [("devPtr", "void **"); ("size", "size_t")]
                        []
        Assert.Contains("let captureError", output)
        Assert.Contains("Code = code", output)
        Assert.Contains("HipError.describe", output)

    [<Fact>]
    let ``enum error does not affect non-matching return type`` () =
        // A function returning int (not hipError_t) should not get enum error wrapping
        let output = generateWithEnumError "hipGetDeviceCount" "int"
                        [("count", "int *")]
                        []
        // Should use standard ZeroSuccessOrError pattern, not enum match
        Assert.DoesNotContain("match result with", output)
        Assert.DoesNotContain("hipError_t.hipSuccess", output)

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
            (UseNullWithReason "stbi_failure_reason") Types.LP64

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
        Assert.Contains("if result <> 0n then", output)

    [<Fact>]
    let ``null_with_reason does not generate captureError helper`` () =
        let output = generateWithNullReason "stbi_load" "void *"
                        [("filename", "const char *"); ("x", "int *"); ("y", "int *"); ("channels", "int *"); ("desired", "int")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.DoesNotContain("let captureError", output)
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
