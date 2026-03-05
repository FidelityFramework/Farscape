module Farscape.Tests.ErrnoModuleGeneratorTests

open Xunit
open Farscape.Core
open Farscape.Core.CodeAST
open Farscape.Core.CodeRenderer
open Farscape.Core.ErrnoModuleGenerator

[<Fact>]
let ``MatchExpr renders match expression with cases`` () =
    let expr = MatchExpr(
                    Identifier "code",
                    [ ("1", Literal "\"one\"")
                      ("2", Literal "\"two\"")
                      ("_", Literal "\"unknown\"") ])
    let result = renderExpr "        " expr
    Assert.Contains("match code with", result)
    Assert.Contains("| 1 -> \"one\"", result)
    Assert.Contains("| 2 -> \"two\"", result)
    Assert.Contains("| _ -> \"unknown\"", result)

[<Fact>]
let ``RecordType with Struct attribute renders correctly`` () =
    let decl = RecordType("CError", [("Code", Named "int"); ("Description", Named "string")], Some "Error type", ["Struct"])
    let rendered = CodeRenderer.render (Module("Test", "test", [decl]))
    Assert.Contains("[<Struct>]", rendered)
    Assert.Contains("type CError = {", rendered)
    Assert.Contains("Code: int", rendered)
    Assert.Contains("Description: string", rendered)

[<Fact>]
let ``filterErrnoMacros extracts E* constants with integer values`` () =
    let macros : CppParser.MacroDecl list = [
        { Name = "EPERM"; Kind = CppParser.SimpleValue "1"; RawValue = "1"; Documentation = Some "Operation not permitted" }
        { Name = "ENOENT"; Kind = CppParser.SimpleValue "2"; RawValue = "2"; Documentation = Some "No such file or directory" }
        { Name = "NOT_ERRNO"; Kind = CppParser.SimpleValue "42"; RawValue = "42"; Documentation = None }
        { Name = "EMAX_SOMETHING"; Kind = CppParser.SimpleValue "abc"; RawValue = "abc"; Documentation = None }
        { Name = "EAGAIN"; Kind = CppParser.SimpleValue "11"; RawValue = "11"; Documentation = Some "Try again" }
    ]
    let result = filterErrnoMacros macros
    Assert.Equal(3, result.Length)
    Assert.Equal("EPERM", result.[0].Name)
    Assert.Equal(1L, result.[0].Value)
    Assert.Equal(Some "Operation not permitted", result.[0].Description)
    Assert.Equal("ENOENT", result.[1].Name)
    Assert.Equal("EAGAIN", result.[2].Name)

[<Fact>]
let ``generateCErrorType produces Struct-attributed record`` () =
    let decls = generateCErrorType ()
    Assert.Equal(1, decls.Length)
    match decls.[0] with
    | RecordType (name, fields, doc, attrs) ->
        Assert.Equal("CError", name)
        Assert.Equal(2, fields.Length)
        Assert.Equal("Code", fst fields.[0])
        Assert.Equal("Description", fst fields.[1])
        Assert.Contains("Struct", attrs)
        Assert.True(doc.IsSome)
    | _ -> Assert.Fail "Expected RecordType"

[<Fact>]
let ``generateErrnoDecls produces Literal constants and describe function`` () =
    let constants = [
        { Name = "EPERM"; Value = 1L; Description = Some "Operation not permitted" }
        { Name = "ENOENT"; Value = 2L; Description = Some "No such file or directory" }
    ]
    let decls = generateErrnoDecls constants
    // Should have XmlDoc + LiteralBinding for each constant, then BlankLine + 2 XmlDocs + describe LetBinding
    let literals = decls |> List.choose (function LiteralBinding (n, v) -> Some (n, v) | _ -> None)
    Assert.Equal(2, literals.Length)
    Assert.Equal(("EPERM", "1"), literals.[0])
    Assert.Equal(("ENOENT", "2"), literals.[1])
    // Should have a describe LetBinding
    let letBindings = decls |> List.choose (function LetBinding (n, _, _, _, _) -> Some n | _ -> None)
    Assert.Contains("describe", letBindings)

[<Fact>]
let ``generate renders complete errno module from macros`` () =
    let macros : CppParser.MacroDecl list = [
        { Name = "EPERM"; Kind = CppParser.SimpleValue "1"; RawValue = "1"; Documentation = Some "Operation not permitted" }
        { Name = "ENOENT"; Kind = CppParser.SimpleValue "2"; RawValue = "2"; Documentation = Some "No such file or directory" }
    ]
    let result = ErrnoModuleGenerator.generate macros "Fidelity.Errno" "libc"
    Assert.True(result.IsSome)
    let output = result.Value
    Assert.Contains("[<Struct>]", output)
    Assert.Contains("type CError = {", output)
    Assert.Contains("Code: int", output)
    Assert.Contains("[<Literal>]", output)
    Assert.Contains("let EPERM = 1", output)
    Assert.Contains("let ENOENT = 2", output)
    Assert.Contains("/// Operation not permitted", output)
    Assert.Contains("let describe (code: int) : string =", output)
    Assert.Contains("| EPERM -> \"Operation not permitted\"", output)
    Assert.Contains("| ENOENT -> \"No such file or directory\"", output)
    Assert.Contains("| _ -> \"Unknown error\"", output)

[<Fact>]
let ``generate with live errno.h produces complete module`` () =
    let options : CppParser.HeaderParserOptions = {
        HeaderFile = "/usr/include/errno.h"
        IncludePaths = []
        Defines = []
        Verbose = false
        IncludeMacros = true
        MacroPrefixes = ["E"]; IncludeRoot = None
    }
    match CppParser.parseHeaderFull options with
    | Error err -> Assert.Fail $"Parse failed: {err}"
    | Ok result ->
        let output = ErrnoModuleGenerator.generate result.Macros "Fidelity.Errno" "errno"
        Assert.True(output.IsSome, "Should generate errno module")
        let rendered = output.Value
        // Verify structural elements
        Assert.Contains("[<Struct>]", rendered)
        Assert.Contains("type CError = {", rendered)
        Assert.Contains("let describe (code: int) : string =", rendered)
        // Verify specific constants with descriptions
        Assert.Contains("let EPERM = 1", rendered)
        Assert.Contains("| EPERM -> \"Operation not permitted\"", rendered)
        Assert.Contains("let ENOENT = 2", rendered)
        Assert.Contains("| ENOENT -> \"No such file or directory\"", rendered)
