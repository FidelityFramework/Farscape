module Farscape.Tests.DocumentationExtractionTests

open Xunit
open Farscape.Core
open Farscape.Core.WrapperTypes
open TestHelpers

// =============================================================================
// Raw Header Comment Extraction Tests
// =============================================================================

module RawHeaderCommentTests =

    open Farscape.Core.CodeAST
    open Farscape.Core.CodeRenderer

    [<Fact>]
    let ``Generic2 renders two-parameter generic type`` () =
        let ty = Generic2("Result", Named "nativeint", Named "CError")
        Assert.Equal("Result<nativeint, CError>", renderType ty)

    [<Fact>]
    let ``RecordConstruction renders struct literal`` () =
        let expr = RecordConstruction [("Code", Literal "42"); ("Description", Literal "\"test\"")]
        let result = renderExpr "        " expr
        Assert.Equal("{ Code = 42; Description = \"test\" }", result)

    [<Fact>]
    let ``errno.h macros get documentation from raw header comments`` () =
        let options : CppParser.HeaderParserOptions = {
            HeaderFile = "/usr/include/errno.h"
            IncludePaths = []
            Defines = []
            Verbose = false
            IncludeMacros = true
            MacroPrefixes = ["E"]
            TransitiveHeaders = []
        }
        match CppParser.parseHeaderFull options with
        | Error err -> Assert.Fail $"Parse failed: {err}"
        | Ok result ->
            // Should find errno macros
            Assert.NotEmpty result.Macros
            // EPERM should have documentation "Operation not permitted"
            let eperm = result.Macros |> List.tryFind (fun m -> m.Name = "EPERM")
            Assert.True(eperm.IsSome, "EPERM macro not found")
            Assert.True(eperm.Value.Documentation.IsSome, "EPERM should have documentation")
            Assert.Equal("Operation not permitted", eperm.Value.Documentation.Value)
            // ENOENT should have documentation
            let enoent = result.Macros |> List.tryFind (fun m -> m.Name = "ENOENT")
            Assert.True(enoent.IsSome, "ENOENT macro not found")
            Assert.Equal("No such file or directory", enoent.Value.Documentation.Value)

// =============================================================================
// Documentation Extraction Tests (-fparse-all-comments)
// =============================================================================

[<Fact>]
let ``formatDocDecls with documentation produces description and C signature`` () =
    let func = mkFuncWithDoc "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                      (Some "Read NBYTES into BUF from FD. Return the number read, -1 for errors or 0 for EOF.")
    let decls = FidelityCodeGenerator.generate
                    [ CppParser.Declaration.Function func ]
                    "Fidelity.libc.Test" "libc" Types.LP64 Map.empty
    // Should contain the description from the header comment
    Assert.Contains("/// Read NBYTES into BUF from FD.", decls)
    // Should contain the C signature
    Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", decls)

[<Fact>]
let ``formatDocDecls without documentation produces only C signature`` () =
    let func = mkFuncWithDoc "getpid" "int" [] None
    let decls = FidelityCodeGenerator.generate
                    [ CppParser.Declaration.Function func ]
                    "Fidelity.libc.Test" "libc" Types.LP64 Map.empty
    Assert.Contains("/// C signature: int getpid()", decls)
    // Should NOT have a blank XML doc line (no description to separate from)
    Assert.DoesNotContain("/// \n", decls)

[<Fact>]
let ``wrapper with documented function produces description and C signature`` () =
    let decls = [ mkDeclWithDoc "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                          (Some "Read NBYTES into BUF from FD.") ]
    let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" NoErrors Types.LP64 None
    Assert.Contains("/// Read NBYTES into BUF from FD.", output)
    Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", output)

[<Fact>]
let ``struct documentation flows to RecordType output`` () =
    let s : CppParser.StructDecl =
        { Name = "Point"; Fields = [
            { Name = "x"; Type = "int"; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None; IsBitfield = false; BitWidth = None }
            { Name = "y"; Type = "int"; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None; IsBitfield = false; BitWidth = None }
          ]; Documentation = Some "A point in 2D space."; IsUnion = false }
    let decls = [ CppParser.Declaration.Struct s ]
    let output = FidelityCodeGenerator.generate decls "Fidelity.test" "test" Types.LP64 Map.empty
    Assert.Contains("/// A point in 2D space.", output)

[<Fact>]
let ``enum documentation flows to EnumType output`` () =
    let e : CppParser.EnumDecl =
        { Name = "Color"; Values = [
            { Name = "RED"; Value = 0L; Documentation = None }
            { Name = "GREEN"; Value = 1L; Documentation = None }
            { Name = "BLUE"; Value = 2L; Documentation = None }
          ]; Documentation = Some "Color values for rendering."; UnderlyingType = None }
    let decls = [ CppParser.Declaration.Enum e ]
    let output = FidelityCodeGenerator.generate decls "Fidelity.test" "test" Types.LP64 Map.empty
    Assert.Contains("/// Color values for rendering.", output)

[<Fact>]
let ``live header parse extracts function documentation`` () =
    // Create a temporary header with documented functions
    let headerContent = """
#include <stddef.h>
typedef long ssize_t;

/* Read NBYTES into BUF from FD. Return the number read, -1 for errors or 0 for EOF. */
extern ssize_t read(int fd, void *buf, size_t count);

/* Close a file descriptor. */
extern int close(int fd);
"""
    let tmpHeader = System.IO.Path.GetTempFileName() + ".h"
    System.IO.File.WriteAllText(tmpHeader, headerContent)
    try
        let options : CppParser.HeaderParserOptions = {
            HeaderFile = tmpHeader
            IncludePaths = []
            Defines = []
            Verbose = false
            IncludeMacros = false
            MacroPrefixes = []
            TransitiveHeaders = []
        }
        match CppParser.parseHeader options with
        | Error err -> Assert.Fail $"Parse failed: {err}"
        | Ok decls ->
            let funcs =
                decls |> List.choose (function
                    | CppParser.Declaration.Function f -> Some f
                    | _ -> None)
            // read should have documentation
            let readFunc = funcs |> List.tryFind (fun f -> f.Name = "read")
            Assert.True(readFunc.IsSome, "read function not found")
            Assert.True(readFunc.Value.Documentation.IsSome, "read should have documentation")
            Assert.Contains("Read NBYTES into BUF from FD", readFunc.Value.Documentation.Value)
            // close should have documentation
            let closeFunc = funcs |> List.tryFind (fun f -> f.Name = "close")
            Assert.True(closeFunc.IsSome, "close function not found")
            Assert.True(closeFunc.Value.Documentation.IsSome, "close should have documentation")
            Assert.Contains("Close a file descriptor", closeFunc.Value.Documentation.Value)
    finally
        System.IO.File.Delete(tmpHeader)

[<Fact>]
let ``live header parse extracts struct documentation`` () =
    let headerContent = """
/* A point in 2D space. */
struct Point {
    int x;
    int y;
};
"""
    let tmpHeader = System.IO.Path.GetTempFileName() + ".h"
    System.IO.File.WriteAllText(tmpHeader, headerContent)
    try
        let options : CppParser.HeaderParserOptions = {
            HeaderFile = tmpHeader
            IncludePaths = []
            Defines = []
            Verbose = false
            IncludeMacros = false
            MacroPrefixes = []
            TransitiveHeaders = []
        }
        match CppParser.parseHeader options with
        | Error err -> Assert.Fail $"Parse failed: {err}"
        | Ok decls ->
            let structs =
                decls |> List.choose (function
                    | CppParser.Declaration.Struct s -> Some s
                    | _ -> None)
            let point = structs |> List.tryFind (fun s -> s.Name = "Point")
            Assert.True(point.IsSome, "Point struct not found")
            Assert.True(point.Value.Documentation.IsSome, "Point should have documentation")
            Assert.Contains("A point in 2D space", point.Value.Documentation.Value)
    finally
        System.IO.File.Delete(tmpHeader)

[<Fact>]
let ``live header parse extracts enum documentation`` () =
    let headerContent = """
/* Color values for rendering. */
enum Color {
    RED = 0,
    GREEN = 1,
    BLUE = 2
};
"""
    let tmpHeader = System.IO.Path.GetTempFileName() + ".h"
    System.IO.File.WriteAllText(tmpHeader, headerContent)
    try
        let options : CppParser.HeaderParserOptions = {
            HeaderFile = tmpHeader
            IncludePaths = []
            Defines = []
            Verbose = false
            IncludeMacros = false
            MacroPrefixes = []
            TransitiveHeaders = []
        }
        match CppParser.parseHeader options with
        | Error err -> Assert.Fail $"Parse failed: {err}"
        | Ok decls ->
            let enums =
                decls |> List.choose (function
                    | CppParser.Declaration.Enum e -> Some e
                    | _ -> None)
            let color = enums |> List.tryFind (fun e -> e.Name = "Color")
            Assert.True(color.IsSome, "Color enum not found")
            Assert.True(color.Value.Documentation.IsSome, "Color should have documentation")
            Assert.Contains("Color values for rendering", color.Value.Documentation.Value)
    finally
        System.IO.File.Delete(tmpHeader)

[<Fact>]
let ``live header multi-line comment joins into single documentation string`` () =
    let headerContent = """
#include <stddef.h>

/* A multi-line comment.
   This function does something complex.
   Returns 0 on success. */
extern int multi_line_example(int x);
"""
    let tmpHeader = System.IO.Path.GetTempFileName() + ".h"
    System.IO.File.WriteAllText(tmpHeader, headerContent)
    try
        let options : CppParser.HeaderParserOptions = {
            HeaderFile = tmpHeader
            IncludePaths = []
            Defines = []
            Verbose = false
            IncludeMacros = false
            MacroPrefixes = []
            TransitiveHeaders = []
        }
        match CppParser.parseHeader options with
        | Error err -> Assert.Fail $"Parse failed: {err}"
        | Ok decls ->
            let funcs =
                decls |> List.choose (function
                    | CppParser.Declaration.Function f -> Some f
                    | _ -> None)
            let func = funcs |> List.tryFind (fun f -> f.Name = "multi_line_example")
            Assert.True(func.IsSome, "multi_line_example not found")
            Assert.True(func.Value.Documentation.IsSome, "should have documentation")
            let doc = func.Value.Documentation.Value
            Assert.Contains("multi-line comment", doc)
            Assert.Contains("something complex", doc)
            Assert.Contains("Returns 0 on success", doc)
    finally
        System.IO.File.Delete(tmpHeader)
