module Farscape.Tests.CppParserDetectionTests

open System.IO
open Xunit
open Farscape.Core

// =========================================================================
// detectCppMode
// =========================================================================

module DetectCppModeTests =

    let private withTempHeader (ext: string) (content: string) (f: string -> 'a) : 'a =
        let path = Path.Combine(Path.GetTempPath(), $"farscape_test_{System.Guid.NewGuid():N}{ext}")
        try
            File.WriteAllText(path, content)
            f path
        finally
            if File.Exists(path) then File.Delete(path)

    [<Fact>]
    let ``detectCppMode returns true for .hpp extension`` () =
        withTempHeader ".hpp" "" (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .hxx extension`` () =
        withTempHeader ".hxx" "" (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .hh extension`` () =
        withTempHeader ".hh" "" (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns false for pure C .h file`` () =
        let content = "#include <stdio.h>\nint main(void) { return 0; }\n"
        withTempHeader ".h" content (fun path ->
            Assert.False(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with namespace`` () =
        let content = "namespace foo { void bar(); }\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with class`` () =
        let content = "class Widget { public: void show(); };\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with template`` () =
        let content = "template<typename T>\nT* create();\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with std:: usage`` () =
        let content = "#include <string>\nstd::string getName();\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with cerrno include`` () =
        let content = "#include <cerrno>\nint get_error();\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with cstring include`` () =
        let content = "#include <cstring>\nvoid copy(char *d, const char *s);\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with vector include`` () =
        let content = "#include <vector>\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with memory include`` () =
        let content = "#include <memory>\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with optional include`` () =
        let content = "#include <optional>\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns false for .h with only C stdlib`` () =
        let content = "#include <stdlib.h>\n#include <string.h>\nvoid init();\n"
        withTempHeader ".h" content (fun path ->
            Assert.False(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns false for unknown extension`` () =
        withTempHeader ".txt" "namespace foo {}" (fun path ->
            Assert.False(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns false for nonexistent file`` () =
        Assert.False(CppParser.detectCppMode "/nonexistent/path/that/does/not/exist.h")

    [<Fact>]
    let ``detectCppMode returns true for .h with unordered_map include`` () =
        let content = "#include <unordered_map>\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

    [<Fact>]
    let ``detectCppMode returns true for .h with string_view include`` () =
        let content = "#include <string_view>\n"
        withTempHeader ".h" content (fun path ->
            Assert.True(CppParser.detectCppMode path))

// =========================================================================
// Typedef naming of anonymous declarations
// =========================================================================

module AnonymousEnumNamingTests =

    [<Fact>]
    let ``anonymous enum takes the name of its typedef and binds as an integer`` () =
        let path = Path.Combine(Path.GetTempPath(), $"farscape_enum_{System.Guid.NewGuid():N}.h")
        File.WriteAllText(path, "typedef enum { WINDOW_TOPLEVEL, WINDOW_POPUP } WindowType;\nvoid make(WindowType kind);\n")
        try
            let options : CppParser.HeaderParserOptions = {
                HeaderFile = path; IncludePaths = []; Defines = []; IncludeRoot = None
                IncludeMacros = false; MacroPrefixes = []; Verbose = false; CppMode = false }
            let declarations = match CppParser.parseHeader options with Ok ds -> ds | Error e -> failwith e
            Assert.Contains(declarations, fun d -> match d with CppParser.Declaration.Enum e -> e.Name = "WindowType" | _ -> false)
            let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
            let source = FidelityCodeGenerator.generateModule ctx Set.empty declarations "Test.Window" "gtk-3" "test" []
            Assert.Contains("let make (kind: int) : unit", source)
            Assert.Contains("Type = Integer (Unsigned, 32); PassBy = Value", source)
        finally
            if File.Exists path then File.Delete path
