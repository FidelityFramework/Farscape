module Farscape.Tests.EnumErrorModuleGeneratorTests

open Xunit
open Farscape.Core
open TestHelpers

// =============================================================================
// deriveErrorStructName Tests
// =============================================================================

[<Fact>]
let ``deriveErrorStructName strips _t suffix and PascalCases`` () =
    Assert.Equal("HipError", EnumErrorModuleGenerator.deriveErrorStructName "hipError_t")

[<Fact>]
let ``deriveErrorStructName strips _code suffix and PascalCases`` () =
    Assert.Equal("XrtError", EnumErrorModuleGenerator.deriveErrorStructName "xrt_error_code")

[<Fact>]
let ``deriveErrorStructName strips _status suffix and PascalCases`` () =
    Assert.Equal("VkResult", EnumErrorModuleGenerator.deriveErrorStructName "vk_result_status")

[<Fact>]
let ``deriveErrorStructName handles no known suffix`` () =
    Assert.Equal("MyError", EnumErrorModuleGenerator.deriveErrorStructName "my_error")

// =============================================================================
// Error Type Generation Tests
// =============================================================================

let private makeHipConfig () : EnumErrorModuleGenerator.EnumErrorConfig =
    EnumErrorModuleGenerator.makeConfig "hipError_t" "hipSuccess" None None

let private hipValues = [
    mkEnumValWithDoc "hipSuccess" 0L "Successful completion"
    mkEnumValWithDoc "hipErrorInvalidValue" 1L "Invalid Value"
    mkEnumValWithDoc "hipErrorOutOfMemory" 2L "Out of memory range"
    mkEnumVal "hipErrorUnknown" 999L
]

[<Fact>]
let ``generates error struct with Struct attribute`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    Assert.True(output.IsSome)
    let text = output.Value
    Assert.Contains("[<Struct>]", text)
    Assert.Contains("type HipError = {", text)
    Assert.Contains("Code: hipError_t", text)
    Assert.Contains("Description: string", text)

[<Fact>]
let ``generates describe function with enum-qualified patterns`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    let text = output.Value
    Assert.Contains("let describe (code: hipError_t) : string =", text)
    Assert.Contains("| hipError_t.hipSuccess ->", text)
    Assert.Contains("\"Successful completion\"", text)
    Assert.Contains("| hipError_t.hipErrorInvalidValue ->", text)
    Assert.Contains("\"Invalid Value\"", text)

[<Fact>]
let ``describe falls back to variant name when no doc comment`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    let text = output.Value
    // hipErrorUnknown has no doc comment, should use the variant name
    Assert.Contains("| hipError_t.hipErrorUnknown ->", text)
    Assert.Contains("\"hipErrorUnknown\"", text)

[<Fact>]
let ``describe has default catch-all case`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    let text = output.Value
    Assert.Contains("| _ ->", text)
    Assert.Contains("\"Unknown hipError_t error\"", text)

[<Fact>]
let ``generates capture function building error record`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    let text = output.Value
    Assert.Contains("let capture (code: hipError_t) : HipError =", text)
    Assert.Contains("Code = code", text)
    Assert.Contains("Description = describe code", text)

[<Fact>]
let ``generate returns None for empty enum`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = []; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    Assert.True(output.IsNone)

[<Fact>]
let ``generate produces correct module namespace`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    let text = output.Value
    Assert.Contains("module Fidelity.HIP.Errors", text)

[<Fact>]
let ``generate produces companion submodule`` () =
    let config = makeHipConfig ()
    let output = EnumErrorModuleGenerator.generate { Name = "hipError_t"; Values = hipValues; Documentation = None; UnderlyingType = None } config "Fidelity.HIP.Errors"
    let text = output.Value
    Assert.Contains("module HipError =", text)

[<Fact>]
let ``makeConfig derives struct name correctly`` () =
    let config = EnumErrorModuleGenerator.makeConfig "hipError_t" "hipSuccess" (Some "hipGetErrorString") None
    Assert.Equal("hipError_t", config.ErrorType)
    Assert.Equal("hipSuccess", config.SuccessValue)
    Assert.Equal("HipError", config.ErrorStructName)
    Assert.Equal(Some "hipGetErrorString", config.ErrorStringFn)
    Assert.Equal(None, config.ErrorNameFn)
