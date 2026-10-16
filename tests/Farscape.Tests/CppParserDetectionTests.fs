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

module NonNullIdentityTests =

    let private parse cppMode (content: string) =
        let directory = Path.Combine(__SOURCE_DIRECTORY__, "../../artifacts/nonnull-parser")
        Directory.CreateDirectory directory |> ignore
        let extension = if cppMode then ".hpp" else ".h"
        let path = Path.Combine(directory, $"{System.Guid.NewGuid():N}{extension}")
        try
            File.WriteAllText(path, content)
            let options : CppParser.HeaderParserOptions = {
                HeaderFile = path; IncludePaths = []; Defines = []; IncludeRoot = None
                IncludeMacros = false; MacroPrefixes = []; Verbose = false; CppMode = cppMode }
            match CppParser.parseHeader options with
            | Ok declarations -> declarations
            | Error error -> failwith error
        finally
            if File.Exists path then File.Delete path

    let private functions declarations =
        declarations |> List.choose (function CppParser.Function f -> Some f | _ -> None)

    let private positions (f: CppParser.FunctionDecl) =
        f.Attributes |> List.filter (fun a -> a.Kind = "NonNullAttr") |> List.collect _.Args

    [<Fact>]
    let ``builtin memcpy redeclaration retains exact nonnull identity`` () =
        let declarations = parse false """
typedef __SIZE_TYPE__ size_t;
void *memcpy(void *restrict destination, const void *restrict source, size_t count)
  __attribute__((nonnull(1, 2)));
"""
        let f = declarations |> functions |> List.find (fun f -> f.Name = "memcpy")
        Assert.Equal<int list>([0; 1], positions f)
        Assert.Equal("size_t", snd f.Parameters.[2])

    [<Fact>]
    let ``macro nonnull positions leave unannotated pointer nullable`` () =
        let declarations = parse false """
#define REQUIRED(...) __attribute__((nonnull(__VA_ARGS__)))
void copy_selected(char *first, char *optional, char *last) REQUIRED(1, 3);
void optional_only(char *value);
"""
        let fs = declarations |> functions
        let selected = fs |> List.find (fun f -> f.Name = "copy_selected")
        Assert.Equal<int list>([0; 2], positions selected)
        let optional = fs |> List.find (fun f -> f.Name = "optional_only")
        Assert.Empty(optional.Attributes |> List.filter (fun a -> a.Kind = "NonNullAttr"))

    [<Fact>]
    let ``same name variadic overloads keep separate nonnull positions`` () =
        let declarations = parse true """
void choose(char *first, char *second) __attribute__((nonnull(1)));
void choose(char *first, char *second, ...) __attribute__((nonnull(2)));
"""
        let fs = functions declarations
        Assert.Equal(2, fs.Length)
        Assert.Equal<int list list>([[0]; [1]], fs |> List.map positions)

    [<Fact>]
    let ``method and constructor use their original clang function types`` () =
        let declarations = parse true """
struct Receiver {
  Receiver(char *value) __attribute__((nonnull));
  void consume(char *value) __attribute__((nonnull));
};
"""
        let receiver = declarations |> List.pick (function CppParser.Class c when c.Name = "Receiver" -> Some c | _ -> None)
        let constructor = receiver.Constructors |> functions |> List.exactlyOne
        let method = receiver.Methods |> functions |> List.exactlyOne
        Assert.Equal<int list>([0], positions constructor)
        Assert.Equal<int list>([0], positions method)

    [<Fact>]
    let ``annotated namespace siblings keep separate nonnull positions`` () =
        let declarations = parse true """
namespace A { void f(char *requiredA, char *optionalA) __attribute__((nonnull(1))); }
namespace B { void f(char *optionalB, char *requiredB) __attribute__((nonnull(2))); }
namespace C { void f(char *optionalA, char *optionalB); }
"""
        let fs = functions declarations
        Assert.Equal(3, fs.Length)
        Assert.Equal(3, fs |> List.map _.MangledName |> List.distinct |> List.length)
        Assert.All(fs, fun f -> Assert.True(f.MangledName |> Option.exists (System.String.IsNullOrEmpty >> not)))
        Assert.Equal<int list list>([[0]; [1]; []], fs |> List.map positions)

    [<Fact>]
    let ``static methods in named classes keep separate nonnull positions`` () =
        let declarations = parse true """
namespace Group {
struct A { static void f(char *requiredA, char *optionalA) __attribute__((nonnull(1))); };
struct B { static void f(char *optionalB, char *requiredB) __attribute__((nonnull(2))); };
struct C { static void f(char *optionalA, char *optionalB); };
}
"""
        let methods =
            declarations
            |> List.choose (function CppParser.Class c -> Some c | _ -> None)
            |> List.collect (fun c -> functions c.Methods)
        Assert.Equal(3, methods.Length)
        Assert.Equal(3, methods |> List.map _.MangledName |> List.distinct |> List.length)
        Assert.Equal<int list list>([[0]; [1]; []], methods |> List.map positions)

    [<Fact>]
    let ``nonnull out of line namespace definition refuses unresolved lexical scope`` () =
        let header = """
namespace A { void f(char *value) __attribute__((nonnull(1))); }
void A::f(char *value) {}
"""
        let error = Assert.Throws<System.Exception>(fun () -> parse true header |> ignore)
        Assert.Contains("Cannot establish lexical clang nonnull declaration scope for 'f'", error.Message)

    [<Fact>]
    let ``nonnull function template refuses unresolved lexical scope`` () =
        let header = """
template<typename T> void f(T *value) __attribute__((nonnull(1)));
"""
        let error = Assert.Throws<System.Exception>(fun () -> parse true header |> ignore)
        Assert.Contains("Cannot establish lexical clang nonnull declaration scope for 'f'", error.Message)

    [<Fact>]
    let ``inline and anonymous namespaces cannot lend nonnull positions to siblings`` () =
        let declarations = parse true """
inline namespace A { void f(char *requiredA, char *optionalA) __attribute__((nonnull(1))); }
namespace { void f(char *optionalB, char *requiredB) __attribute__((nonnull(2))); }
namespace B { void f(char *optionalA, char *requiredB) __attribute__((nonnull(2))); }
"""
        let fs = functions declarations
        Assert.Equal(2, fs.Length)
        Assert.Equal<int list list>([[0]; [1]], fs |> List.map positions)

    [<Fact>]
    let ``same declaration redeclarations retain their nonnull union`` () =
        let declarations = parse true """
namespace A {
void f(char *first, char *second) __attribute__((nonnull(1)));
void f(char *first, char *second) __attribute__((nonnull(2)));
}
"""
        let fs = functions declarations
        Assert.Equal(2, fs.Length)
        Assert.Single(fs |> List.map _.MangledName |> List.distinct) |> ignore
        let attributes = fs |> List.map (fun f -> f.Attributes |> List.filter (fun a -> a.Kind = "NonNullAttr"))
        Assert.Equal<int list>([1; 2], attributes |> List.map List.length)
        for declaration in attributes do
            for attribute in declaration do
                Assert.Equal<int list>([0; 1], attribute.Args)

    [<Fact>]
    let ``nonnull out of line nested record refuses unresolved lexical scope`` () =
        let header = """
struct Outer { struct Inner; };
struct Outer::Inner {
  static void f(char *first, char *second) __attribute__((nonnull(1)));
};
struct Inner {
  static void f(char *first, char *second) __attribute__((nonnull(2)));
};
"""
        let error = Assert.Throws<System.Exception>(fun () -> parse true header |> ignore)
        Assert.Contains("Cannot establish lexical clang nonnull declaration scope for 'f'", error.Message)

    [<Fact>]
    let ``reopened and nested namespaces retain exact nonnull scope`` () =
        let declarations = parse true """
namespace A { void g(char *value); }
namespace A { void f(char *first, char *second) __attribute__((nonnull(1))); }
namespace B { void f(char *first, char *second) __attribute__((nonnull(2))); }
namespace N::M { void f(char *first, char *second) __attribute__((nonnull(2))); }
"""
        let fs = functions declarations |> List.filter (fun f -> f.Name = "f")
        Assert.Equal(3, fs.Length)
        Assert.Equal(3, fs |> List.map _.MangledName |> List.distinct |> List.length)
        Assert.Equal<int list list>([[0]; [1]; [1]], fs |> List.map positions)

    [<Fact>]
    let ``parent identifiers retain function and type nonnull evidence`` () =
        let declarations = parse true """
struct parent;
void parent(char *value) __attribute__((nonnull(1)));
void g(struct parent *value) __attribute__((nonnull(1)));
"""
        let fs = functions declarations
        Assert.Equal(2, fs.Length)
        Assert.Equal<int list list>([[0]; [0]], fs |> List.map positions)
