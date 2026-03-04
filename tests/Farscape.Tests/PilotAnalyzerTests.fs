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
          Prefixes = ["str"]; Functions = []; XmlInterfaces = [] }
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
          Prefixes = []; Functions = ["abort"; "exit"]; XmlInterfaces = [] }
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
          Prefixes = ["str"]; Functions = []; XmlInterfaces = [] }
    let decls = [
        CppParser.Declaration.Struct (mkStructSimple "size_t")
        CppParser.Declaration.Enum (mkEnumSimple "Flags")
        CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
    ]
    let filtered = PilotAnalyzer.filterDeclarationsForNamespace spec decls
    // Structs and enums pass through, memcpy gets filtered out
    Assert.Equal(2, filtered.Length)

// --- sub-prefix splitting tests ---

[<Fact>]
let ``clusterByPrefix splits large groups by sub-prefix`` () =
    // Simulate GTK-style: 35+ functions under "gtk" prefix, with clear subsystems
    let windowFns = [ for i in 1..10 -> $"gtk_window_fn{i}" ]
    let widgetFns = [ for i in 1..10 -> $"gtk_widget_fn{i}" ]
    let containerFns = [ for i in 1..10 -> $"gtk_container_fn{i}" ]
    let miscFns = [ for i in 1..5 -> $"gtk_misc_fn{i}" ]
    let allGtk = windowFns @ widgetFns @ containerFns @ miscFns
    let groups, _ = PilotAnalyzer.clusterByPrefix allGtk
    // Should produce sub-groups for window, widget, container (each ≥ 2)
    Assert.True(groups |> List.exists (fun g -> g.SuggestedName = "Window"),
        $"Expected Window group. Groups: {groups |> List.map (fun g -> g.SuggestedName)}")
    Assert.True(groups |> List.exists (fun g -> g.SuggestedName = "Widget"),
        $"Expected Widget group. Groups: {groups |> List.map (fun g -> g.SuggestedName)}")
    Assert.True(groups |> List.exists (fun g -> g.SuggestedName = "Container"),
        $"Expected Container group. Groups: {groups |> List.map (fun g -> g.SuggestedName)}")

[<Fact>]
let ``clusterByPrefix does not split small groups`` () =
    // Only 5 functions — below subPrefixThreshold
    let names = ["io_read"; "io_write"; "io_close"; "io_open"; "io_seek"]
    let groups, _ = PilotAnalyzer.clusterByPrefix names
    // Should stay as one group, not split
    let ioGroups = groups |> List.filter (fun g -> g.FunctionNames |> List.exists (fun f -> f.StartsWith "io_"))
    Assert.Equal(1, ioGroups.Length)

[<Fact>]
let ``sub-prefix splitting preserves all functions`` () =
    let windowFns = [ for i in 1..12 -> $"gtk_window_fn{i}" ]
    let widgetFns = [ for i in 1..12 -> $"gtk_widget_fn{i}" ]
    let singleFns = ["gtk_init"; "gtk_main"]
    let allGtk = windowFns @ widgetFns @ singleFns
    let groups, ungrouped = PilotAnalyzer.clusterByPrefix allGtk
    let allGrouped = groups |> List.collect (fun g -> g.FunctionNames)
    let total = (allGrouped @ ungrouped) |> List.distinct |> List.length
    Assert.Equal(allGtk.Length, total)

[<Fact>]
let ``sub-prefix groups have correct prefixes`` () =
    let fns = [ for i in 1..15 -> $"gtk_window_fn{i}" ] @ [ for i in 1..16 -> $"gtk_widget_fn{i}" ]
    let groups, _ = PilotAnalyzer.clusterByPrefix fns
    let windowGroup = groups |> List.tryFind (fun g -> g.SuggestedName = "Window")
    match windowGroup with
    | Some g -> Assert.Contains("gtk_window", g.Prefixes)
    | None -> Assert.Fail "Expected Window group"

// --- toNamespaceRoot tests ---

[<Theory>]
[<InlineData("gtk-3", "Gtk3")>]
[<InlineData("libc", "Libc")>]
[<InlineData("gobject-2.0", "Gobject")>]
[<InlineData("wayland-client", "WaylandClient")>]
let ``toNamespaceRoot PascalCases library names`` (input: string) (expected: string) =
    Assert.Equal(expected, PilotAnalyzer.toNamespaceRoot input)
