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
