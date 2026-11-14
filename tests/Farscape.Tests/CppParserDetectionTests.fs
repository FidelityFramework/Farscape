module Farscape.Tests.CppParserDetectionTests

open System.IO
open Xunit
open Farscape.Core

// =========================================================================
// detectCppMode
// =========================================================================

module DetectCppModeTests =

    let private withTempHeader (ext: string) (content: string) (f: string -> 'a) : 'a =
        let directory = TestHelpers.TestScratch.create "detection"
        let path = Path.Combine(directory, $"farscape_test_{System.Guid.NewGuid():N}{ext}")
        try
            File.WriteAllText(path, content)
            f path
        finally
            Directory.Delete(directory, true)

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
    let ``C++ indicators in comments and literals do not select C++`` () =
        let content = "/* namespace ids; class names; template<T>; std::vector; #include <vector> */\n// namespace ids\n#define TEXT \"class Widget and std::string\"\nstruct item { int value; };\n"
        withTempHeader ".h" content (fun path ->
            Assert.False(CppParser.detectCppMode path)
            let declarations =
                match CppParser.parseHeader (CppParser.defaultOptions path) with
                | Ok declarations -> declarations
                | Error error -> failwith error
            Assert.Contains(declarations, fun d -> match d with CppParser.Struct s -> s.Name = "item" | _ -> false)
            Assert.DoesNotContain(declarations, fun d -> match d with CppParser.Class c -> c.Name = "item" | _ -> false))

    [<Fact>]
    let ``C++ code following a comment still selects C++`` () =
        withTempHeader ".h" "/* namespace ids */\nclass Widget {};\n" (fun path ->
            Assert.True(CppParser.detectCppMode path)
            let declarations =
                match CppParser.parseHeader (CppParser.defaultOptions path) with
                | Ok declarations -> declarations
                | Error error -> failwith error
            let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
            Assert.Equal("FS9214: C++ opaque class 'Widget' requires a supported object storage, ownership and method ABI contract; legacy address-based class bindings cannot be emitted as Clef source.", error.Message))

    [<Fact>]
    let ``glibc dl_find_object is a C record with clang's layout`` () =
        let header = "/usr/include/dlfcn.h"
        Assert.False(CppParser.detectCppMode header)
        let options =
            { CppParser.defaultOptions header with
                Defines = ["_GNU_SOURCE"]; IncludePaths = ["/usr/include"]; SystemIncludePaths = []; IncludeRoot = Some "/usr/include"
                IncludeMacros = false }
        let declarations =
            match CppParser.parseHeader options with
            | Ok declarations -> declarations
            | Error error -> failwith error
        let record = declarations |> List.pick (function CppParser.Struct s when s.Name = "dl_find_object" -> Some s | _ -> None)
        Assert.DoesNotContain(declarations, fun d -> match d with CppParser.Class c -> c.Name = "dl_find_object" | _ -> false)
        let selected = declarations |> List.filter (function CppParser.Struct s -> s.Name = record.Name | CppParser.Typedef _ -> true | _ -> false)
        let output = FidelityCodeGenerator.generate selected "Test" "test" Types.LP64 Map.empty
        let value name =
            let m = System.Text.RegularExpressions.Regex.Match(output, @"\blet " + name + @" : int = (\d+)")
            Assert.True(m.Success, $"Missing {name} in generated record")
            m.Groups.[1].Value
        withTempHeader ".h" "#define _GNU_SOURCE\n#include <dlfcn.h>\n" (fun wrapper ->
            let size = value "Size"
            let alignment = value "Alignment"
            let checks =
                [ $"sizeof(struct dl_find_object) == {size}"
                  $"_Alignof(struct dl_find_object) == {alignment}"
                  for field in record.Fields do
                      let offset = value (field.Name + "Offset")
                      $"offsetof(struct dl_find_object, {field.Name}) == {offset}" ]
            let assertions = checks |> List.map (fun check -> $"_Static_assert({check}, \"layout\");") |> String.concat "\n"
            withTempHeader ".c" ($"#include <stddef.h>\n#include \"{wrapper}\"\n{assertions}\n") (fun probe ->
                let info = ClangToolchain.startInfo [ "-fsyntax-only"; probe ]
                use proc = System.Diagnostics.Process.Start info
                let errors = proc.StandardError.ReadToEnd()
                proc.WaitForExit()
                Assert.True(proc.ExitCode = 0, errors)))

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
                HeaderFile = path; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
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
        let directory = TestHelpers.TestScratch.create "nonnull-parser"
        let extension = if cppMode then ".hpp" else ".h"
        let path = Path.Combine(directory, $"{System.Guid.NewGuid():N}{extension}")
        try
            File.WriteAllText(path, content)
            let options : CppParser.HeaderParserOptions = {
                HeaderFile = path; IncludePaths = []; SystemIncludePaths = []; Defines = []; IncludeRoot = None
                IncludeMacros = false; MacroPrefixes = []; Verbose = false; CppMode = cppMode }
            match CppParser.parseHeader options with
            | Ok declarations -> declarations
            | Error error -> failwith error
        finally
            Directory.Delete(directory, true)

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

module LastLocFilenameTrackingTests =

    [<Fact>]
    let ``system stdio.h retains declarations following nested floatn includes`` () =
        let options =
            { CppParser.defaultOptions "/usr/include/stdio.h" with
                IncludePaths = [ "/usr/include" ]
                IncludeMacros = false }
        let declarations =
            match CppParser.parseHeader options with
            | Ok ds -> ds
            | Error err -> failwith err
        let names =
            declarations
            |> List.choose (function CppParser.Function f -> Some f.Name | _ -> None)
            |> Set.ofList
        for expected in [ "fopen"; "fclose"; "fread"; "fwrite"; "puts"; "putchar" ] do
            Assert.True(names.Contains expected, $"Expected stdio.h declaration '{expected}' after nested includes")
        Assert.False(names.Contains "__bswap_16", "Included bits/byteswap helper must remain excluded")

    [<Fact>]
    let ``declarations resuming after include and macro locations retain target provenance`` () =
        let directory = TestHelpers.TestScratch.create "lastloc-tracking"
        try
            let included = Path.Combine(directory, "included_types.h")
            let target = Path.Combine(directory, "target_api.h")
            File.WriteAllText(included, "int included_only_fn(int x);\n")
            File.WriteAllText(
                target,
                "#define DECL_FN(name) int name(int arg)\n" +
                "#include \"included_types.h\"\n" +
                "DECL_FN(resumed_via_macro);\n" +
                "void resumed_following_decl(int value);\n")
            let options =
                { CppParser.defaultOptions target with
                    IncludePaths = [ directory ]
                    IncludeMacros = false }
            let declarations =
                match CppParser.parseHeader options with
                | Ok ds -> ds
                | Error err -> failwith err
            let names =
                declarations
                |> List.choose (function CppParser.Function f -> Some f.Name | _ -> None)
                |> Set.ofList
            Assert.False(names.Contains "included_only_fn")
            Assert.True(names.Contains "resumed_via_macro")
            Assert.True(names.Contains "resumed_following_decl")
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    let ``anonymous namespace subtree updates LastLocFilename for following target declarations`` () =
        let directory = TestHelpers.TestScratch.create "lastloc-anon-ns"
        try
            let included = Path.Combine(directory, "included_decls.hpp")
            let target = Path.Combine(directory, "target_api.hpp")
            File.WriteAllText(
                included,
                "#ifdef INCLUDE_SECOND_FN\n" +
                "int included_after_anon(int x);\n" +
                "#else\n" +
                "int anon_included_fn(int x);\n" +
                "#endif\n")
            File.WriteAllText(
                target,
                "namespace {}\n" +
                "int target_before_anon(int value);\n" +
                "namespace {\n" +
                "#include \"included_decls.hpp\"\n" +
                "}\n" +
                "#define INCLUDE_SECOND_FN\n" +
                "#include \"included_decls.hpp\"\n" +
                "int target_after_anon(int value);\n")
            let options =
                { CppParser.defaultOptions target with
                    IncludePaths = [ directory ]
                    IncludeMacros = false }
            let declarations =
                match CppParser.parseHeader options with
                | Ok ds -> ds
                | Error err -> failwith err
            let names =
                declarations
                |> List.choose (function CppParser.Function f -> Some f.Name | _ -> None)
                |> Set.ofList
            Assert.True(names.Contains "target_before_anon")
            Assert.False(names.Contains "anon_included_fn")
            Assert.False(names.Contains "included_after_anon")
            Assert.True(names.Contains "target_after_anon")
        finally
            Directory.Delete(directory, true)
