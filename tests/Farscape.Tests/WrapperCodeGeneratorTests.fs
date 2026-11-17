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
let ``descriptor backed passthrough preserves source declaration identity and native status`` () =
    let declarations = [mkDecl "retire" "int" [("owner", "void *")] []]
    let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    let output = WrapperCodeGenerator.generateWithContext (Some context) false Set.empty declarations
                    "Wrappers.Test" "testlib" "Platform.Bindings.Test" NoErrors Types.LP64 None
    Assert.Contains("let retire :", output)
    Assert.Contains("-> int =\n    Platform.Bindings.Test.retire", output)
    Assert.DoesNotContain("let retire (", output)
    Assert.DoesNotContain("Region.release", output)
    Assert.DoesNotContain("withFinalize", output)

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

[<Fact>]
let ``async readiness defers native call captures scalar errno and retries only after readiness`` () =
    let output = AsyncFixture.render AsyncFixture.declarations Map.empty AsyncFixture.readiness
    Assert.Contains(": Async<Result<int, int>>", output)
    Assert.Contains("(awaitReady: int -> Async<unit>)", output)
    Assert.Contains("do! awaitReady token", output)
    Assert.Contains("return! farscapeRetry ()", output)
    Assert.True(output.IndexOf("async {") < output.IndexOf("Test.Native.perform owner token"))
    Assert.True(output.IndexOf("Test.Native.status_value ()") < output.IndexOf("do! awaitReady"))
    Assert.Contains("farscapeStatus >= 11 && farscapeStatus <= 11", output)
    Assert.Contains("farscapeStatus >= 35 && farscapeStatus <= 35", output)
    Assert.DoesNotContain("NativePtr", output)

[<Fact>]
let ``async submission carries correlation and retires successful and failed completions`` () =
    let output = AsyncFixture.render AsyncFixture.declarations Map.empty AsyncFixture.submission
    Assert.Contains("let! farscapeCompletion = awaitCompletion token", output)
    Assert.Contains("return Ok farscapeCompletion", output)
    Assert.Contains("return Error farscapeCompletion", output)
    Assert.True(output.IndexOf("async {") < output.IndexOf("Test.Native.perform"))
    Assert.True(output.IndexOf("let! farscapeCompletion") < output.IndexOf("Test.Native.retire owner"))
    Assert.True(output.IndexOf("Test.Native.retire owner") < output.IndexOf("return Error farscapeCompletion"))

[<Fact>]
let ``async syntax is represented by typed AST nodes`` () =
    let openCode = CodeAST.Async (CodeAST.LetBang("value", CodeAST.Identifier "operation",
                        CodeAST.DoBang(CodeAST.Identifier "completion", CodeAST.Return(CodeAST.ResultOk(CodeAST.Identifier "value")))))
    let output = CodeRenderer.renderExpr "" openCode
    Assert.Equal("async {\n    let! value = operation\n    do! completion\n    return Ok value\n}", output)
    Assert.Equal("return! operation", CodeRenderer.renderExpr "" (CodeAST.ReturnBang(CodeAST.Identifier "operation")))

/// Execute the portable scalar workflow surface as an F# source fixture. This proves
/// activation/order/result behavior, not native Clef lowering or CCS lifetime discharge.
let private runAsyncScalarFixture contract (body: string) =
    let directory = TestScratch.create "async-scalar-behavior"
    try
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "Native.fs"), """module Test.Native
let mutable calls = 0
let mutable retired = 0
let mutable status = 0
let mutable operation : int -> int -> int = fun _ _ -> 0
let perform owner token =
    calls <- calls + 1
    operation owner token
let status_value () = status
let retire _ = retired <- retired + 1
        """)
        // This scalar fixture has no native carrier types; provide the otherwise empty
        // shared module that ordinary generated wrappers open.
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "Types.fs"), "module Test.Types\nlet marker = ()\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "Generated.fs"), AsyncFixture.render AsyncFixture.declarations Map.empty contract)
        let script = System.IO.Path.Combine(directory, "Run.fsx")
        System.IO.File.WriteAllText(script, "#load \"Types.fs\"\n#load \"Native.fs\"\n#load \"Generated.fs\"\n" + body)
        let start = System.Diagnostics.ProcessStartInfo("dotnet")
        start.WorkingDirectory <- directory
        start.ArgumentList.Add "fsi"
        start.ArgumentList.Add "--exec"
        start.ArgumentList.Add script
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use child = System.Diagnostics.Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit 30000) then
            child.Kill(true)
            failwith "scalar async behavior fixture timed out"
        Assert.True(child.ExitCode = 0, stdout.Result + stderr.Result)
    finally
        System.IO.Directory.Delete(directory, true)

[<Fact>]
let ``async readiness behavior is cold retries EAGAIN and EWOULDBLOCK and captures terminal errno`` () =
    runAsyncScalarFixture AsyncFixture.readiness """
Test.Native.operation <- fun _ _ ->
    match Test.Native.calls with
    | 1 -> Test.Native.status <- 11; -1
    | 2 -> Test.Native.status <- 35; -1
    | _ -> 7
let mutable waits = 0
let wait token = async {
    if token <> 9 then failwith "lost correlation"
    waits <- waits + 1
    Test.Native.status <- 999
}
let operation = Test.Api.performAsync 3 9 wait
if Test.Native.calls <> 0 || waits <> 0 then failwith "eager native operation"
let result = Async.RunSynchronously operation
if result <> Ok 7 || waits <> 2 || Test.Native.calls <> 3 then failwith "readiness retry mismatch"
Test.Native.operation <- fun _ _ -> Test.Native.status <- 22; -1
let failed = Async.RunSynchronously (Test.Api.performAsync 3 9 wait)
if failed <> Error 22 || waits <> 2 then failwith "lost terminal errno or awaited terminal failure"
"""

[<Fact>]
let ``async completion behavior retires once after correlated completion including error`` () =
    runAsyncScalarFixture AsyncFixture.submission """
let mutable completed = false
let wait token = async {
    if token <> 9 || Test.Native.calls <> 1 || Test.Native.retired <> 0 then failwith "completion order/correlation"
    completed <- true
    return -9
}
let operation = Test.Api.performAsync 3 9 wait
if Test.Native.calls <> 0 then failwith "eager submission"
let result = Async.RunSynchronously operation
if result <> Error -9 || not completed || Test.Native.retired <> 1 then failwith "failed completion was not retired"
Test.Native.operation <- fun _ _ -> Test.Native.status <- 5; -1
let failed = Async.RunSynchronously (Test.Api.performAsync 3 9 (fun _ -> failwith "wait after failed submission"))
if failed <> Error 5 || Test.Native.retired <> 1 then failwith "submission failure retirement mismatch"
Test.Native.calls <- 0
Test.Native.operation <- fun _ _ -> 0
let success = Async.RunSynchronously (Test.Api.performAsync 3 9 (fun _ -> async { return 42 }))
if success <> Ok 42 || Test.Native.retired <> 2 then failwith "successful completion was not retired"
"""

[<Fact>]
let ``generated async native lifetime gate`` () =
    // This gate deliberately uses the generated files unchanged. Composer owns the
    // LLVM/LLD route; clang below compiles only the independent foreign C fixture.
    let repository = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
    let work = System.IO.Path.Combine(repository, "test-results", "scratch", "r220-native", System.Guid.NewGuid().ToString("N"))
    System.IO.Directory.CreateDirectory work |> ignore
    let write name (text: string) = System.IO.File.WriteAllText(System.IO.Path.Combine(work, name), text)
    let invoke directory label (start: System.Diagnostics.ProcessStartInfo) =
        start.WorkingDirectory <- directory
        start.Environment["TMPDIR"] <- work
        use child = System.Diagnostics.Process.Start start
        let stdout, stderr = child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit 300000) then
            child.Kill(true)
            failwith $"{label} timed out; evidence: {work}"
        let output, errors = stdout.Result, stderr.Result
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, label + ".log"), output + errors)
        child.ExitCode, output, errors
    let requireTool label start =
        let exit, output, errors = invoke work label start
        Assert.True((exit = 0), $"{label}: {output}{errors}; evidence: {work}")
    let composer =
        match System.Environment.GetEnvironmentVariable "COMPOSER_DLL" with
        | null | "" -> System.IO.Path.GetFullPath(System.IO.Path.Combine(repository, "..", "Composer", "src", "bin", "Debug", "net10.0", "Composer.dll"))
        | configured -> System.IO.Path.GetFullPath configured
    Assert.True(System.IO.File.Exists composer, $"Composer is required at {composer}")
    let platform = System.IO.Path.GetFullPath(System.IO.Path.Combine(repository, "..", "Fidelity.Platform", "Environments", "Linux", "x86_64", "Fidelity.Platform.CompilerSurface.fidproj"))
    let quote text = Fidelity.Data.TOML.Toml.serializeValue (Fidelity.Data.TOML.TomlValue.String text)
    // Carry the selected compiler surface into the root project: Composer selects
    // platform keys at the dependency, so a dependent root's loading key is lost.
    // Include its sources as well as its dependencies; do not duplicate the profile.
    let platformDocument = Fidelity.Data.TOML.Toml.parse (System.IO.File.ReadAllText platform) |> Result.defaultWith failwith
    let platformDirectory = System.IO.Path.GetDirectoryName platform
    let platformPath path = System.IO.Path.GetFullPath(System.IO.Path.Combine(platformDirectory, path))
    let platformSources =
        match Fidelity.Data.TOML.Toml.getValue "build.sources" platformDocument with
        | Some (Fidelity.Data.TOML.TomlValue.Array values) ->
            values |> List.map (function Fidelity.Data.TOML.TomlValue.String path -> platformPath path | _ -> failwith "Invalid platform source")
        | _ -> failwith "Missing platform sources"
    let platformDependencies =
        match Fidelity.Data.TOML.Toml.getValue "dependencies" platformDocument with
        | Some (Fidelity.Data.TOML.TomlValue.Table entries) ->
            entries |> Map.toList |> List.map (fun (name, _) ->
                match Fidelity.Data.TOML.Toml.getValue ($"dependencies.{name}.path") platformDocument with
                | Some (Fidelity.Data.TOML.TomlValue.String path) -> $"{name} = {{ path = {quote (platformPath path)} }}"
                | _ -> failwith "Missing platform dependency path") |> String.concat "\n"
        | _ -> failwith "Missing platform dependencies"
    let header = """
#define ASYNC220_WIDE 4294967295
typedef struct AsyncOwner AsyncOwner;
typedef int (*AsyncProbe)(AsyncOwner * _Nonnull owner);
int run_probe(AsyncProbe _Nonnull callback);
int submit(AsyncOwner * _Nonnull owner, int token, const unsigned char * _Nonnull bytes, int count);
int try_send(AsyncOwner * _Nonnull owner, int token, const unsigned char * _Nonnull bytes, int count);
int complete(AsyncOwner * _Nonnull owner, int token);
void ready(AsyncOwner * _Nonnull owner);
void step(AsyncOwner * _Nonnull owner);
void retire(AsyncOwner * _Nonnull owner);
int retired_count(void);
const unsigned char * _Nullable column_blob(AsyncOwner * _Nonnull owner);
int column_bytes(AsyncOwner * _Nonnull owner);
"""
    write "probe.h" header
    write "probe.c" """
#include "probe.h"
struct AsyncOwner { const unsigned char *bytes; int count; int token; int ready; unsigned char row[2]; };
static struct AsyncOwner current;
static int retired;
static AsyncOwner *owner_open(void) { current = (struct AsyncOwner){0}; retired = 0; return &current; }
static void owner_close(AsyncOwner *owner) { owner->bytes=0; }
int run_probe(AsyncProbe callback) { AsyncOwner *owner=owner_open(); int result=callback(owner); int valid=retired == 1; owner_close(owner); return valid ? result : -8; }
int submit(AsyncOwner *o, int token, const unsigned char *b, int n) { o->bytes=b; o->count=n == 1 ? 1 : 0; o->token=token; o->row[0]=o->count && b[0] < 128 ? b[0] : 0; o->row[1]=0; return 0; }
int try_send(AsyncOwner *o, int token, const unsigned char *b, int n) { if (!o->ready) return -11; submit(o,token,b,n); return complete(o,token); }
int complete(AsyncOwner *o, int token) { return o->bytes && o->count == 1 && o->token == token ? o->bytes[0] : -9; }
void ready(AsyncOwner *o) { o->ready=1; }
void step(AsyncOwner *o) { o->ready=0; }
void retire(AsyncOwner *o) { o->bytes=0; if (retired < 2) retired++; }
int retired_count(void) { return retired; }
const unsigned char *column_blob(AsyncOwner *o) { return o->bytes ? o->row : 0; }
int column_bytes(AsyncOwner *o) { return o->count; }
"""
    requireTool "c-object" (ClangToolchain.startInfo ["-c"; "-fPIC"; "-Wall"; "-Werror"; "probe.c"; "-o"; "probe.o"])
    requireTool "c-archive" (ClangToolchain.arStartInfo ["rcs"; "libasync220.a"; "probe.o"])
    let providerProject = System.IO.Path.Combine(work, "Provider.fidproj")
    let buffer : PilotTypes.BindingReadOnlyBuffer =
        { Parameter = 2; Extent = PilotTypes.BindingReadOnlyBufferExtent.ExplicitCount 3
          Content = PilotTypes.BorrowedContent.Bytes
          Retention = PilotTypes.BindingBufferRetention.RetainedUntil(0, [], [{ Binding = "Async220.Native.retire"; Parameter = 0 }])
          Identity = "async220.inflight"; Version = "1"; Source = "probe.h:submit and try_send" }
    let borrowed : PilotTypes.BindingBorrowedResult =
        { Content = PilotTypes.BorrowedContent.Text; OwnerParameter = 0
          // The C owner provides a terminated row; column_bytes is an independent
          // bounded result consumed before the same declared invalidation.
          Extent = PilotTypes.BorrowedExtent.NulTerminated
          InvalidatedBy = [PilotTypes.BorrowInvalidation.OwnerCall("Async220.Native.step", 0); PilotTypes.BorrowInvalidation.OwnerCall("Async220.Native.retire", 0)]
          Identity = "async220.borrowed-row"; Version = "1"; Source = "probe.h:column_blob" }
    let contracts =
        [ { AsyncFixture.submission with Binding = "submit"; Name = "submitAsync"
                                         StatusSource = PilotTypes.AsyncStatusSource.ReturnValue
                                         Success = { Lower = 0; Upper = 0 }
                                         Wait = { AsyncFixture.submission.Wait with Library = "async220"; Module = "Async220.Provider"; Arguments = [0; 1] }
                                         BufferParameters = [2]; BorrowedResults = ["column_blob"] }
          { AsyncFixture.readiness with Binding = "try_send"; Name = "sendAsync"
                                        Protocol = PilotTypes.AsyncProtocol.Readiness [{ Lower = -11; Upper = -11 }]
                                        StatusSource = PilotTypes.AsyncStatusSource.ReturnValue
                                        Wait = { Library = "Async220.Provider"; Module = "Async220.Provider"; Binding = "awaitReady"; Project = Some providerProject; Arguments = [0; 1] }
                                        BufferParameters = [2]; BorrowedResults = ["column_blob"] } ]
    let resultRange lower upper : PilotTypes.BindingResultRange =
        { Identity = "async220.result"; Version = "1"; Source = "probe.c"
          Lower = PilotTypes.ResultRangeBound.Constant lower; Upper = PilotTypes.ResultRangeBound.Constant upper }
    let bindings =
        [ for name in ["submit"; "try_send"] do
              yield { AsyncFixture.projection name with ReadOnlyBuffers = [buffer]; ReadOnlyReferenceParameters = [2]
                                                        ResultRange = Some (if name = "submit" then resultRange 0 0 else resultRange -11 255) }
          yield { AsyncFixture.projection "column_blob" with BorrowedResult = Some borrowed }
          yield { AsyncFixture.projection "complete" with ResultRange = Some (resultRange -9 255) }
          yield { AsyncFixture.projection "column_bytes" with ResultRange = Some (resultRange 0 1) }
          yield { AsyncFixture.projection "retired_count" with ResultRange = Some (resultRange 0 2) } ]
        @ [ { AsyncFixture.projection "run_probe" with
                CallbackInvocations =
                    [{ Parameter = 0; Identity = "async220.probe"; Version = "1"; Source = "probe.c:run_probe"
                       Scope = "during_call"; ExitBinding = None; SuccessStatus = None; AbsenceHypothesis = None
                       Context = None; CountedArrays = []; Retention = None }] } ]
    let project : PilotTypes.PilotProject =
        { Library = { Name = "async220"; Headers = [System.IO.Path.Combine(work, "probe.h")]
                      XmlProtocols = []; Introspection = []; IncludePaths = [work]; SystemIncludePaths = []; Defines = []; MacroPrefixes = ["ASYNC220_"]; PkgConfig = []; Artifact = None }
          Output = { Mode = "fidelity"; Directory = System.IO.Path.Combine(work, "generated") }
          Namespaces = [{ Name = "Async220.Native"; Description = "native async lifetime gate"; Library = "async220"
                          Prefixes = []; Functions = ["run_probe"; "submit"; "try_send"; "complete"; "ready"; "step"; "retire"; "retired_count"; "column_blob"; "column_bytes"]; XmlInterfaces = []; Signals = [] }]
          Options = Some { AsyncFixture.options contracts with Bindings = bindings }
          Nonnull = Some { Returns = Set.empty
                           Parameters = Map.ofList [for name in ["run_probe"; "submit"; "try_send"; "complete"; "ready"; "step"; "retire"; "column_blob"; "column_bytes"] -> name, (if name = "submit" || name = "try_send" then [0; 2] else [0])] }
          ErrorConventions = None; Callbacks = None; ProtocolConfig = None; Layer3 = None; CppConfig = None }
    let pilot = System.IO.Path.Combine(work, "async.pilot.toml")
    PilotSerializer.saveToFile pilot project |> Result.defaultWith failwith
    let generated = BindingGenerator.generateFromProject pilot false true Types.LP64 |> Result.defaultWith failwith
    let sources = generated.OutputFiles |> List.filter (fun path -> path.EndsWith(".clef"))
    let generatedText = sources |> List.map System.IO.File.ReadAllText |> String.concat "\n"
    Assert.Contains("let ASYNC220_WIDE : int = 4294967295", generatedText)
    Assert.DoesNotContain("4294967295L", generatedText)
    let probeEntry = System.Text.RegularExpressions.Regex.Match(generatedText, @"let run_probe \(callback: ([A-Za-z0-9_]+)\)")
    Assert.True(probeEntry.Success, "Generated run_probe must expose its declared during-call entry type")
    let probeEntryType = probeEntry.Groups[1].Value
    Assert.Contains("FnPtr<CHandle<AsyncOwner> -> int>", generatedText)
    let nativeProject = generated.OutputFiles |> List.find (fun path -> path.EndsWith(".fidproj") && not (path.EndsWith(".Bridge.fidproj")))
    Assert.DoesNotContain("Async220.Provider", System.IO.File.ReadAllText nativeProject)
    for path in sources do
        if path.EndsWith("/Native.clef") then
            let text = System.IO.File.ReadAllText path
            Assert.Contains("NativeDefault.zeroed ()", text)
            Assert.Contains("RetainedUntil", text)
            Assert.Contains("BorrowedResultViewDescriptor", text)
            Assert.DoesNotContain("async {", text)
    write "Provider.clef" """module Async220.Provider
open Async220.Native.Types
let awaitReady (owner: CHandle<AsyncOwner>) (_token: int) = async {
    Async220.Native.ready owner
    return ()
}
let awaitCompletion (owner: CHandle<AsyncOwner>) (token: int) = async {
    do! awaitReady owner token
    let borrowed = Async220.Native.column_blob owner
    let count = Async220.Native.column_bytes owner
    let value =
        match borrowed with
        | Some row -> if count = 1 && row.[0] = 65 then 65 else -2
        | None -> -3
    Async220.Native.step owner
    let completion = Async220.Native.complete owner token
    if value = completion then return 65 else return -1
}
"""
    write "Provider.fidproj" ($"[package]\nname = \"Async220.Provider\"\nversion = \"0.1.0\"\n[compilation]\ntarget = \"cpu\"\n[build]\noutput_kind = \"library\"\nsources = [\"Provider.clef\"]\n[dependencies]\nnative = {{ path = {quote nativeProject} }}\n")
    let loading = System.IO.File.ReadAllText(System.IO.Path.Combine(repository, "tests", "native-callbacks", "Loading.clef"))
    write "Loading.clef" (loading.Replace("Landing.Acceptance.Loading", "Async220.Loading").Replace("landingprobe", "async220").Replace("tests/native-callbacks/probe.c", "probe.c"))
    let positiveBody = """
        let region = Region.create 1
        let bytes = Region.alloc<int> region 1
        bytes.[0] <- 65
        let owner = Async220.Native.owner_open ()
        let! result = Async220.Native.Api.submitAsync owner 7 (ReadOnlyView.ofArray bytes) 1
        let check = bytes.[0]
        Region.release region
        match result with
        | Ok value -> if value >= 65 && value <= 65 && check >= 65 && check <= 65 then return 65 else return -2
        | Error _ -> return -3
"""
    let readinessBody = positiveBody.Replace("Async220.Native.Api.submitAsync owner 7 (ReadOnlyView.ofArray bytes) 1", "Async220.Native.Bridge.Async.sendAsync owner 7 (ReadOnlyView.ofArray bytes) 1").Replace("let check = bytes.[0]", "Async220.Native.retire owner\n        let check = bytes.[0]")
    let unbackedBody = positiveBody.Replace("let region = Region.create 1\n        let bytes = Region.alloc<int> region 1", "let bytes = [|65|]\n        let view = ReadOnlyView.ofArray bytes").Replace("(ReadOnlyView.ofArray bytes)", "view").Replace("let check = bytes.[0]", "let check = view.[0]").Replace("        Region.release region\n", "")
    let invalidatedBody = """
        let region = Region.create 1
        let bytes = Region.alloc<int> region 1
        bytes.[0] <- 65
        let owner = Async220.Native.owner_open ()
        let _submitted = Async220.Native.submit owner 7 (ReadOnlyView.ofArray bytes) 1
        let borrowed = Async220.Native.column_blob owner
        let count = Async220.Native.column_bytes owner
        Async220.Native.step owner
        let! _completed = Async220.Native.Api.submitAsync owner 7 (ReadOnlyView.ofArray bytes) 1
        let stale =
            match borrowed with
            | Some row -> if count = 1 && row.[0] = 65 then 65 else -2
            | None -> -3
        Region.release region
        return stale
"""
    let earlyRetirement = positiveBody.Replace("        let! result =", "        Region.release region\n        let! result =").Replace("        Region.release region\n        match result", "        match result")
    let failures = ResizeArray<string>()
    let receipts = ResizeArray<string>()
    for label, body, expectedDiagnostic in
        [ "positive-layer2", positiveBody, None; "positive-layer3", readinessBody, None
          "unbacked-retention", unbackedBody, Some ("CCS8424", "without a dominating buffer retention or lifetime scope proof")
          "invalidated-row", invalidatedBody, Some ("CCS8425", "Borrowed result view read exceeds owner invalidation frontier")
          "early-region-retirement", earlyRetirement, Some ("CCS8425", "invalidated or released before its post-resumption use") ] do
        let directory = System.IO.Path.Combine(work, label)
        System.IO.Directory.CreateDirectory directory |> ignore
        let main = System.IO.Path.Combine(directory, "Main.clef")
        let ownedBody = body.Replace("        let owner = Async220.Native.owner_open ()\n", "")
        System.IO.File.WriteAllText(main, "module Async220.Main\nopen Async220.Native.Types\nopen Async220.Native\nlet run (owner: CHandle<AsyncOwner>) = async {\n" + ownedBody + "}\nlet private probeEntry (owner: CHandle<AsyncOwner>) : int = Async.RunSynchronously (run owner)\n[<EntryPoint>]\nlet main _ =\n    let result = Async220.Native.run_probe { " + probeEntryType + ".Invoke = FnPtr.ofFunction probeEntry }\n    if result = 65 && Async220.Native.retired_count () = 1 then\n        Console.writeln \"R220-NATIVE OK\"\n        0\n    else 1\n")
        let ordered = sources |> List.filter (fun path -> not (path.EndsWith("/Async.clef") || path.EndsWith("Api.clef")))
        let wrappers = sources |> List.filter (fun path -> path.EndsWith("Api.clef"))
        let composition = sources |> List.filter (fun path -> path.EndsWith("/Async.clef"))
        let allSources = platformSources @ [System.IO.Path.Combine(work, "Loading.clef")] @ ordered @ [System.IO.Path.Combine(work, "Provider.clef")] @ wrappers @ composition @ [main]
        let fixture = System.IO.Path.Combine(directory, "Native.fidproj")
        let sourceValues = allSources |> List.map quote |> String.concat ", "
        let archivePath = quote (System.IO.Path.Combine(work, "libasync220.a"))
        System.IO.File.WriteAllText(fixture, $"[package]\nname = \"Async220-{label}\"\nversion = \"0.1.0\"\n[compilation]\ntarget = \"cpu\"\n[dependencies]\n{platformDependencies}\n[build]\nsources = [{sourceValues}]\noutput_kind = \"console\"\n[platform]\ndescription = \"Fidelity.Platform.Linux_x86_64.Description.description\"\nloading = \"Async220.Loading.contract\"\nruntime_model = \"libc\"\nos = \"linux\"\narch = \"x86_64\"\n[link]\nlibraries = [\"async220\"]\narchives = {{ async220 = {archivePath} }}\n")
        let binary = System.IO.Path.Combine(directory, "native")
        let exit, output, errors = invoke directory "compile" (ProcessLaunch.startInfo "dotnet" [composer; "compile"; fixture; "--no-color"; "-k"; "-o"; binary])
        let diagnosticText = output + errors
        // Compiler debug context can mention diagnostic identifiers that were not
        // emitted. Inventory only actual error headers in the native receipt.
        let codes = System.Text.RegularExpressions.Regex.Matches(diagnosticText, @"\berror (CCS\d{4}):") |> Seq.cast<System.Text.RegularExpressions.Match> |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.distinct |> Seq.toList
        let codeText = String.concat "," codes
        receipts.Add($"{label}: compile={exit}; codes={codeText}")
        match expectedDiagnostic with
        | Some (code, reason) ->
            let exactDiagnostic =
                diagnosticText.Split('\n') |> Array.exists (fun line ->
                    System.Text.RegularExpressions.Regex.IsMatch(line, $"Main\\.clef:[0-9]+: error {code}:") && line.Contains reason)
            receipts.Add($"{label}: source-bound {code} ({reason})={exactDiagnostic}")
            if exit = 0 || not exactDiagnostic || System.IO.File.Exists binary then
                failures.Add($"{label}: expected Main.clef {code}: {reason}, no executable; compile={exit}, codes={codes}")
        | None when exit <> 0 -> failures.Add($"{label}: native compilation failed: {codes}")
        | None ->
            let runExit, stdout, stderr = invoke directory "native" (ProcessLaunch.startInfo binary [])
            receipts.Add($"{label}: native={runExit}; stdout={quote stdout}; stderr={quote stderr}")
            if runExit <> 0 || stdout.Replace("\r\n", "\n") <> "R220-NATIVE OK\n" || stderr <> "" then
                failures.Add($"{label}: native result mismatch")
    write "receipts.txt" (String.concat "\n" receipts)
    let digests =
        [ yield composer
          for name in ["Clef.Compiler.Service.dll"; "Alex.dll"; "Fidelity.PSG.dll"] do yield System.IO.Path.Combine(System.IO.Path.GetDirectoryName composer, name)
          yield! System.IO.Directory.GetFiles(work, "*", System.IO.SearchOption.AllDirectories) ]
        |> List.filter System.IO.File.Exists
        |> List.map (fun path -> ClangToolchain.sha256 path + "  " + path)
    write "SHA256SUMS" (String.concat "\n" digests)
    Assert.True(failures.Count = 0, $"R220-NATIVE failed; retained evidence: {work}\n" + String.concat "\n" failures)
