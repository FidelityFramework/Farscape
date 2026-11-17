module Farscape.Tests.WrapperPatternAnalyzerTests

open Xunit
open Farscape.Core
open Farscape.Core.WrapperTypes
open TestHelpers

// ── Attribute Mapping Tests ──

[<Theory>]
[<InlineData("AllocSizeAttr")>]
[<InlineData("NonNullAttr")>]
[<InlineData("NoReturnAttr")>]
[<InlineData("NoThrowAttr")>]
[<InlineData("ColdAttr")>]
[<InlineData("RestrictAttr")>]
[<InlineData("PureAttr")>]
[<InlineData("WarnUnusedResultAttr")>]
let ``mapAttribute recognizes known attribute kinds`` (kind: string) =
    let raw = mkAttr kind [] None
    let result = WrapperPatternAnalyzer.mapAttribute raw
    Assert.True(result.IsSome, $"Should recognize {kind}")

[<Fact>]
let ``mapAttribute returns None for unknown attribute kinds`` () =
    let raw = mkAttr "SomeUnknownAttr" [] None
    Assert.True((WrapperPatternAnalyzer.mapAttribute raw).IsNone)

[<Fact>]
let ``mapAttribute extracts FormatAttr archetype and indices`` () =
    let raw = mkAttr "FormatAttr" [1; 2] (Some "printf")
    match WrapperPatternAnalyzer.mapAttribute raw with
    | Some (Format ("printf", 1, 2)) -> ()
    | other -> Assert.Fail $"Expected Format(\"printf\", 1, 2) but got {other}"

[<Fact>]
let ``mapAttribute extracts AllocSize param indices`` () =
    let raw = mkAttr "AllocSizeAttr" [0] None
    match WrapperPatternAnalyzer.mapAttribute raw with
    | Some (AllocSize [0]) -> ()
    | other -> Assert.Fail $"Expected AllocSize [0] but got {other}"

[<Fact>]
let ``mapAttribute extracts NonNull param indices`` () =
    let raw = mkAttr "NonNullAttr" [1; 2] None
    match WrapperPatternAnalyzer.mapAttribute raw with
    | Some (NonNull [1; 2]) -> ()
    | other -> Assert.Fail $"Expected NonNull [1; 2] but got {other}"

// ── Return Semantic Tests ──

[<Fact>]
let ``analyzeReturn NoReturn attribute produces NeverReturns`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "void" [NoReturn] Map.empty ""
    Assert.Equal(NeverReturns, result)

[<Fact>]
let ``analyzeReturn AllocSize attribute produces AllocatedPointer`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "void *" [AllocSize [0]] Map.empty ""
    Assert.Equal(AllocatedPointer, result)

[<Fact>]
let ``analyzeReturn Pure attribute produces PureValue`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "int" [Pure] Map.empty ""
    Assert.Equal(PureValue, result)

[<Fact>]
let ``analyzeReturn ssize_t return produces CountOrError`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "ssize_t" [] Map.empty ""
    Assert.Equal(CountOrError, result)

[<Fact>]
let ``analyzeReturn void return produces VoidReturn`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "void" [] Map.empty ""
    Assert.Equal(VoidReturn, result)

[<Fact>]
let ``analyzeReturn void pointer produces AllocatedPointer`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "void *" [] Map.empty ""
    Assert.Equal(AllocatedPointer, result)

[<Fact>]
let ``analyzeReturn FILE pointer produces OpaqueHandleReturn`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "FILE *" [] Map.empty ""
    Assert.Equal(OpaqueHandleReturn, result)

[<Fact>]
let ``analyzeReturn int produces ZeroSuccessOrError`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "int" [] Map.empty "close"
    Assert.Equal(ZeroSuccessOrError, result)

[<Fact>]
let ``analyzeReturn int for fd-returning function produces IntValueOrError`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "int" [] Map.empty "open"
    Assert.Equal(IntValueOrError, result)

[<Fact>]
let ``analyzeReturn int for socket produces IntValueOrError`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "int" [] Map.empty "socket"
    Assert.Equal(IntValueOrError, result)

[<Fact>]
let ``analyzeReturn int for fork produces IntValueOrError`` () =
    let result = WrapperPatternAnalyzer.analyzeReturn "int" [] Map.empty "fork"
    Assert.Equal(IntValueOrError, result)

[<Fact>]
let ``analyzeReturn resolves typedef to underlying type`` () =
    let tdMap = Map.ofList [("__ssize_t", "long")]
    let result = WrapperPatternAnalyzer.analyzeReturn "__ssize_t" [] tdMap ""
    Assert.Equal(CountOrError, result)

// ── Parameter Role Tests ──

[<Fact>]
let ``analyzeParameters identifies file descriptor by name`` () =
    let parms = [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
    let roles = WrapperPatternAnalyzer.analyzeParameters parms [] Map.empty
    match roles with
    | (_, FileDescriptor) :: _ -> ()
    | other -> Assert.Fail $"Expected FileDescriptor for fd but got {other}"

[<Fact>]
let ``analyzeParameters identifies const pointer as InputBuffer`` () =
    let parms = [("buf", "const void *"); ("count", "size_t")]
    let roles = WrapperPatternAnalyzer.analyzeParameters parms [] Map.empty
    match roles with
    | ("buf", InputBuffer (Some "count")) :: _ -> ()
    | other -> Assert.Fail $"Expected InputBuffer with length param but got {other}"

[<Fact>]
let ``analyzeParameters identifies mutable pointer as OutputBuffer`` () =
    let parms = [("buf", "void *"); ("count", "size_t")]
    let roles = WrapperPatternAnalyzer.analyzeParameters parms [] Map.empty
    match roles with
    | ("buf", OutputBuffer (Some "count")) :: _ -> ()
    | other -> Assert.Fail $"Expected OutputBuffer with length param but got {other}"

[<Fact>]
let ``analyzeParameters identifies FILE pointer as OpaqueHandle`` () =
    let parms = [("stream", "FILE *")]
    let roles = WrapperPatternAnalyzer.analyzeParameters parms [] Map.empty
    match roles with
    | [("stream", OpaqueHandle)] -> ()
    | other -> Assert.Fail $"Expected OpaqueHandle but got {other}"

[<Fact>]
let ``analyzeParameters identifies size_t named count as BufferLength`` () =
    let parms = [("buf", "void *"); ("count", "size_t")]
    let roles = WrapperPatternAnalyzer.analyzeParameters parms [] Map.empty
    match roles with
    | [_; ("count", BufferLength "buf")] -> ()
    | other -> Assert.Fail $"Expected BufferLength but got {other}"

// ── End-to-End Pattern Analysis Tests ──

[<Fact>]
let ``analyze read function produces CountOrError pattern`` () =
    let func = mkFuncWithAttrs "read" "ssize_t"
                [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                []
    let pattern = WrapperPatternAnalyzer.analyze func Map.empty
    Assert.Equal(CountOrError, pattern.ReturnSemantic)
    Assert.True(pattern.NeedsResultWrap)
    Assert.False(pattern.IsPure)

[<Fact>]
let ``analyze malloc with AllocSizeAttr produces AllocatedPointer pattern`` () =
    let func = mkFuncWithAttrs "malloc" "void *"
                [("size", "size_t")]
                [mkAttr "AllocSizeAttr" [0] None; mkAttr "NoThrowAttr" [] None]
    let pattern = WrapperPatternAnalyzer.analyze func Map.empty
    Assert.Equal(AllocatedPointer, pattern.ReturnSemantic)
    Assert.True(pattern.NeedsResultWrap)
    Assert.True(pattern.NeedsResourceCleanup)

[<Fact>]
let ``analyze abort with NoReturnAttr produces NeverReturns pattern`` () =
    let func = mkFuncWithAttrs "abort" "void" []
                [mkAttr "NoReturnAttr" [] None]
    let pattern = WrapperPatternAnalyzer.analyze func Map.empty
    Assert.Equal(NeverReturns, pattern.ReturnSemantic)
    Assert.False(pattern.NeedsResultWrap)

[<Fact>]
let ``analyze abs with PureAttr produces PureValue pattern`` () =
    let func = mkFuncWithAttrs "abs" "int"
                [("x", "int")]
                [mkAttr "PureAttr" [] None; mkAttr "NoThrowAttr" [] None]
    let pattern = WrapperPatternAnalyzer.analyze func Map.empty
    Assert.Equal(PureValue, pattern.ReturnSemantic)
    Assert.False(pattern.NeedsResultWrap)
    Assert.True(pattern.IsPure)

[<Fact>]
let ``async selection is explicit even for conventional read and submit names`` () =
    for name in ["read"; "io_uring_submit"; "register_callback"] do
        let pattern = WrapperPatternAnalyzer.analyze (mkFunc name "int" ["fd", "int"]) Map.empty
        Assert.True(pattern.Async.IsNone)
    let selected = AsyncFixture.select AsyncFixture.declarations Map.empty AsyncFixture.readiness
    Assert.Equal(AsyncLayer.Layer2, selected.Layer)
    let cross = { AsyncFixture.readiness with Wait = { AsyncFixture.readiness.Wait with Library = "Fidelity.Libc"; Module = "Fidelity.Libc.Readiness"; Project = Some "../libc.fidproj" } }
    Assert.Equal(AsyncLayer.Layer3, (AsyncFixture.select AsyncFixture.declarations Map.empty cross).Layer)

[<Theory>]
[<InlineData("correlation")>]
[<InlineData("range")>]
[<InlineData("accessor")>]
[<InlineData("retirement")>]
[<InlineData("dependency")>]
[<InlineData("buffer")>]
[<InlineData("borrowed")>]
let ``async selection rejects unbound contract endpoints`` mutation =
    let original = AsyncFixture.readiness
    let changed, reason =
        match mutation with
        | "correlation" -> { original with Wait = { original.Wait with Arguments = [0] } }, "correlation"
        | "range" -> { original with Protocol = PilotTypes.AsyncProtocol.Readiness [{ Lower = 12; Upper = 11 }] }, "lower exceeds upper"
        | "accessor" -> { original with StatusSource = PilotTypes.AsyncStatusSource.ScalarAccessor "retire" }, "zero-argument"
        | "retirement" -> { AsyncFixture.submission with Retirement = Some { Binding = "status_value"; OwnerParameter = 0 } }, "retirement"
        | "dependency" -> { original with Wait = { original.Wait with Library = "Other"; Module = "Other.Readiness" } }, ".fidproj"
        | "buffer" -> { original with BufferParameters = [0] }, "buffer descriptor"
        | _ -> { original with BorrowedResults = ["column_blob"] }, "InvalidatedBy"
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> AsyncFixture.select AsyncFixture.declarations Map.empty changed |> ignore)
    Assert.Equal(Diagnostics.PilotValueUnadmitted, error.Refusal.Diagnostics.Head.Code)
    Assert.Contains(reason, error.Message)

[<Fact>]
let ``async retirement is bound to the existing retained buffer owner and release`` () =
    let declarations =
        [ mkDecl "perform" "int" ["owner", "struct owner *"; "token", "int"; "bytes", "const unsigned char *"; "count", "int"] []
          mkDecl "status_value" "int" [] []
          mkDecl "retire" "void" ["owner", "struct owner *"] [] ]
    let buffer : PilotTypes.BindingReadOnlyBuffer =
        { Parameter = 2; Extent = PilotTypes.BindingReadOnlyBufferExtent.ExplicitCount 3; Content = PilotTypes.BorrowedContent.Bytes
          Retention = PilotTypes.BindingBufferRetention.RetainedUntil(0, [], [{ Binding = "retire"; Parameter = 0 }])
          Identity = "in-flight"; Version = "1"; Source = "fixture contract" }
    let projection = { AsyncFixture.projection "perform" with ReadOnlyBuffers = [buffer] }
    let contract = { AsyncFixture.submission with BufferParameters = [2] }
    AsyncFixture.select declarations (Map.ofList ["perform", projection]) contract |> ignore
    let changed = { projection with ReadOnlyBuffers = [{ buffer with Retention = PilotTypes.BindingBufferRetention.RetainedUntil(0, [], [{ Binding = "other_release"; Parameter = 0 }]) }] }
    let refusal = Assert.Throws<Diagnostics.RefusalException>(fun () -> AsyncFixture.select declarations (Map.ofList ["perform", changed]) contract |> ignore)
    Assert.Equal(Diagnostics.PilotValueUnadmitted, refusal.Refusal.Diagnostics.Head.Code)
    Assert.Contains("RetainedUntil owner and release", refusal.Message)

[<Fact>]
let ``async callback protocol requires its retained completion entry`` () =
    let selection = AsyncFixture.select AsyncFixture.callbackDeclarations (Map.ofList ["perform", AsyncFixture.callbackBinding]) AsyncFixture.callbackContract
    Assert.Equal(AsyncLayer.Layer2, selection.Layer)
    let refusal = Assert.Throws<Diagnostics.RefusalException>(fun () -> AsyncFixture.select AsyncFixture.callbackDeclarations Map.empty AsyncFixture.callbackContract |> ignore)
    Assert.Contains("existing retained callback contract", refusal.Message)
