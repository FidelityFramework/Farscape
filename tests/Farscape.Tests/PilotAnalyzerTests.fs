module Farscape.Tests.PilotAnalyzerTests

open Xunit
open Farscape.Core
open TestHelpers

// --- functionNameAlgebra tests ---

[<Fact>]
let ``functionNameAlgebra extracts function names`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
        CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
    ]
    let result = PilotAnalyzer.extractFunctionNames decls
    Assert.Equal(2, result.Length)
    Assert.Contains("strlen", result)
    Assert.Contains("memcpy", result)

[<Fact>]
let ``functionNameAlgebra ignores non-function declarations`` () =
    let decls = [
        CppParser.Declaration.Struct (mkStructSimple "Point")
        CppParser.Declaration.Function (mkFunc "read" "int" [])
        CppParser.Declaration.Enum (mkEnumSimple "Flags")
    ]
    let result = PilotAnalyzer.extractFunctionNames decls
    Assert.Equal(1, result.Length)
    Assert.Equal("read", result[0])

// --- extractPrefix tests ---

[<Theory>]
[<InlineData("io_read", "io")>]
[<InlineData("gpio_init", "gpio")>]
[<InlineData("uart_send", "uart")>]
let ``extractPrefix detects underscore-separated prefixes`` (name: string) (expected: string) =
    match PilotAnalyzer.extractPrefix name with
    | Some prefix -> Assert.Equal(expected, prefix)
    | None -> Assert.Fail $"Expected prefix '{expected}' for '{name}'"

[<Theory>]
[<InlineData("strlen", "str")>]
[<InlineData("strcmp", "str")>]
[<InlineData("strcpy", "str")>]
[<InlineData("memcpy", "mem")>]
[<InlineData("memset", "mem")>]
let ``extractPrefix detects known C library prefixes`` (name: string) (expected: string) =
    match PilotAnalyzer.extractPrefix name with
    | Some prefix -> Assert.Equal(expected, prefix)
    | None -> Assert.Fail $"Expected prefix '{expected}' for '{name}'"

[<Theory>]
[<InlineData("HAL_GPIO_Init", "HAL_GPIO")>]
[<InlineData("HAL_UART_Transmit", "HAL_UART")>]
let ``extractPrefix detects HAL-style prefixes`` (name: string) (expected: string) =
    match PilotAnalyzer.extractPrefix name with
    | Some prefix -> Assert.Equal(expected, prefix)
    | None -> Assert.Fail $"Expected prefix '{expected}' for '{name}'"

// --- clusterByPrefix tests ---

[<Fact>]
let ``clusterByPrefix groups functions by shared prefix`` () =
    let names = ["strlen"; "strcmp"; "strcpy"; "memcpy"; "memset"; "abort"]
    let groups, ungrouped = PilotAnalyzer.clusterByPrefix names
    Assert.True(groups |> List.exists (fun g -> g.Prefixes |> List.contains "str"))
    Assert.True(groups |> List.exists (fun g -> g.Prefixes |> List.contains "mem"))
    Assert.Contains("abort", ungrouped)

[<Fact>]
let ``clusterByPrefix respects minimum group size`` () =
    let names = ["strlen"; "abort"; "exit"]
    let groups, ungrouped = PilotAnalyzer.clusterByPrefix names
    // "strlen" alone is only 1 function with "str" prefix, below minGroupSize, so it goes to ungrouped
    Assert.Contains("strlen", ungrouped)
    // But abort and exit are also singles
    Assert.Contains("abort", ungrouped)

// --- analyze integration test ---

[<Fact>]
let ``analyze produces correct AnalysisResult`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
        CppParser.Declaration.Function (mkFunc "strcmp" "int" [])
        CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
        CppParser.Declaration.Function (mkFunc "memset" "void *" [])
        CppParser.Declaration.Struct (mkStructSimple "Point")
        CppParser.Declaration.Function (mkFunc "abort" "void" [])
    ]
    let result = PilotAnalyzer.analyze decls
    Assert.Equal(5, result.TotalFunctions)
    Assert.True(result.Groups.Length >= 2) // str + mem groups
    Assert.Contains("abort", result.Ungrouped)

// --- filterDeclarationsForNamespace tests ---

[<Fact>]
let ``filterDeclarationsForNamespace keeps functions matching prefix`` () =
    let spec : PilotTypes.NamespaceSpec =
        { Name = "Test.String"; Description = ""; Library = "libc"
          Prefixes = ["str"]; Functions = [] }
    let decls = [
        CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
        CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
    ]
    let filtered = PilotAnalyzer.filterDeclarationsForNamespace spec decls
    let funcNames = PilotAnalyzer.extractFunctionNames filtered
    Assert.Contains("strlen", funcNames)
    Assert.DoesNotContain("memcpy", funcNames)

[<Fact>]
let ``filterDeclarationsForNamespace keeps explicitly listed functions`` () =
    let spec : PilotTypes.NamespaceSpec =
        { Name = "Test.Misc"; Description = ""; Library = "libc"
          Prefixes = []; Functions = ["abort"; "exit"] }
    let decls = [
        CppParser.Declaration.Function (mkFunc "abort" "void" [])
        CppParser.Declaration.Function (mkFunc "exit" "void" [])
        CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
    ]
    let filtered = PilotAnalyzer.filterDeclarationsForNamespace spec decls
    let funcNames = PilotAnalyzer.extractFunctionNames filtered
    Assert.Contains("abort", funcNames)
    Assert.Contains("exit", funcNames)
    Assert.DoesNotContain("strlen", funcNames)

[<Fact>]
let ``filterDeclarationsForNamespace passes through non-function declarations`` () =
    let spec : PilotTypes.NamespaceSpec =
        { Name = "Test.String"; Description = ""; Library = "libc"
          Prefixes = ["str"]; Functions = [] }
    let decls = [
        CppParser.Declaration.Struct (mkStructSimple "size_t")
        CppParser.Declaration.Enum (mkEnumSimple "Flags")
        CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
    ]
    let filtered = PilotAnalyzer.filterDeclarationsForNamespace spec decls
    // Structs and enums pass through, memcpy gets filtered out
    Assert.Equal(2, filtered.Length)
