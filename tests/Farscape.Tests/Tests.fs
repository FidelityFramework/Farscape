module Tests

open Xunit
open Farscape.Core

// =============================================================================
// CTypeParser Tests — XParsec monadic parsers for C type strings
// =============================================================================

module CTypeParserTests =

    [<Theory>]
    [<InlineData("int", "int", 0)>]
    [<InlineData("void", "void", 0)>]
    [<InlineData("unsigned long int", "unsigned long int", 0)>]
    [<InlineData("char *", "char", 1)>]
    [<InlineData("void *", "void", 1)>]
    [<InlineData("const char *", "char", 1)>]
    [<InlineData("unsigned char *", "unsigned char", 1)>]
    [<InlineData("const void *", "void", 1)>]
    [<InlineData("char **", "char", 2)>]
    [<InlineData("__off_t", "__off_t", 0)>]
    [<InlineData("size_t", "size_t", 0)>]
    let ``pCType parses base type and pointer depth`` (input: string) (expectedBase: string) (expectedDepth: int) =
        match CTypeParser.tryParseCType input with
        | Some info ->
            Assert.Equal(expectedBase, info.BaseType)
            Assert.Equal(expectedDepth, info.PointerDepth)
        | None ->
            Assert.Fail $"Failed to parse C type: '{input}'"

    [<Theory>]
    [<InlineData("volatile int", "int", 0)>]
    [<InlineData("const volatile char *", "char", 1)>]
    [<InlineData("__restrict void *", "void", 1)>]
    [<InlineData("const __restrict char *", "char", 1)>]
    let ``pCType strips qualifiers`` (input: string) (expectedBase: string) (expectedDepth: int) =
        match CTypeParser.tryParseCType input with
        | Some info ->
            Assert.Equal(expectedBase, info.BaseType)
            Assert.Equal(expectedDepth, info.PointerDepth)
        | None ->
            Assert.Fail $"Failed to parse C type: '{input}'"

    [<Theory>]
    [<InlineData("42", 42L)>]
    [<InlineData("0", 0L)>]
    [<InlineData("-1", -1L)>]
    [<InlineData("0xFF", 255L)>]
    [<InlineData("0x1A", 26L)>]
    [<InlineData("0X10", 16L)>]
    let ``tryParseInteger parses decimal and hex`` (input: string) (expected: int64) =
        match CTypeParser.tryParseInteger input with
        | Some n -> Assert.Equal(expected, n)
        | None -> Assert.Fail $"Failed to parse integer: '{input}'"

    [<Theory>]
    [<InlineData("hello")>]
    [<InlineData("")>]
    [<InlineData("0xZZ")>]
    [<InlineData("12.34")>]
    let ``tryParseInteger rejects non-integers`` (input: string) =
        Assert.True(CTypeParser.tryParseInteger(input).IsNone, $"Should not parse: '{input}'")

    [<Theory>]
    [<InlineData("uint32_t[4]", 4)>]
    [<InlineData("int[16]", 16)>]
    [<InlineData("char[256]", 256)>]
    let ``tryParseArraySize extracts array dimension`` (input: string) (expected: int) =
        match CTypeParser.tryParseArraySize input with
        | Some n -> Assert.Equal(expected, n)
        | None -> Assert.Fail $"Failed to parse array size: '{input}'"

    [<Fact>]
    let ``tryParseArraySize returns None for non-arrays`` () =
        Assert.True(CTypeParser.tryParseArraySize("int").IsNone)
        Assert.True(CTypeParser.tryParseArraySize("char *").IsNone)

    [<Fact>]
    let ``classifyObjectMacroValue detects expressions`` () =
        match CTypeParser.classifyObjectMacroValue "1 + 2" with
        | CppParser.Expression _ -> ()
        | other -> Assert.Fail $"Expected Expression, got {other}"

    [<Fact>]
    let ``classifyObjectMacroValue detects bitwise expressions`` () =
        match CTypeParser.classifyObjectMacroValue "0x01 << 3" with
        | CppParser.Expression _ -> ()
        | other -> Assert.Fail $"Expected Expression, got {other}"

    [<Fact>]
    let ``classifyObjectMacroValue detects type casts`` () =
        match CTypeParser.classifyObjectMacroValue "((void*)0)" with
        | CppParser.TypeCast ("void", "0") -> ()
        | other -> Assert.Fail $"Expected TypeCast(void, 0), got {other}"

    [<Fact>]
    let ``classifyObjectMacroValue passes through simple values`` () =
        match CTypeParser.classifyObjectMacroValue "42" with
        | CppParser.SimpleValue "42" -> ()
        | other -> Assert.Fail $"Expected SimpleValue 42, got {other}"

    [<Fact>]
    let ``parseMacroLine parses object macros`` () =
        match CTypeParser.parseMacroLine "#define FOO 42" with
        | Some m ->
            Assert.Equal("FOO", m.Name)
            Assert.Equal("42", m.RawValue)
        | None ->
            Assert.Fail "Failed to parse object macro"

    [<Fact>]
    let ``parseMacroLine parses function-like macros`` () =
        match CTypeParser.parseMacroLine "#define MAX(a, b) ((a) > (b) ? (a) : (b))" with
        | Some m ->
            Assert.Equal("MAX", m.Name)
            match m.Kind with
            | CppParser.FunctionLike (args, _body) ->
                Assert.Equal(2, args.Length)
                Assert.Equal("a", args.[0])
                Assert.Equal("b", args.[1])
            | other -> Assert.Fail $"Expected FunctionLike, got {other}"
        | None ->
            Assert.Fail "Failed to parse function-like macro"

    [<Fact>]
    let ``parseMacroLine parses empty macros`` () =
        match CTypeParser.parseMacroLine "#define GUARD" with
        | Some m ->
            Assert.Equal("GUARD", m.Name)
            Assert.Equal("", m.RawValue)
        | None ->
            Assert.Fail "Failed to parse empty macro"

    [<Fact>]
    let ``parseMacroLine rejects non-define lines`` () =
        Assert.True(CTypeParser.parseMacroLine("int x = 5;").IsNone)
        Assert.True(CTypeParser.parseMacroLine("#include <stdio.h>").IsNone)

// =============================================================================
// ActivePatterns Tests — structured decomposition backed by XParsec
// =============================================================================

module ActivePatternsTests =

    open ActivePatterns

    [<Theory>]
    [<InlineData("char *")>]
    [<InlineData("const char *")>]
    [<InlineData("unsigned char *")>]
    let ``CharPointer matches char pointer types`` (input: string) =
        match input with
        | ParsedCType info ->
            match info with
            | CharPointer -> ()
            | other -> Assert.Fail $"Expected CharPointer for '{input}', got {other}"
        | _ -> Assert.Fail $"Failed to parse: '{input}'"

    [<Fact>]
    let ``CharPointer does NOT match wchar_t pointer`` () =
        match "wchar_t *" with
        | ParsedCType info ->
            match info with
            | CharPointer -> Assert.Fail "wchar_t * should NOT match CharPointer"
            | TypedPointer _ -> () // correct — wchar_t is a typed pointer
            | other -> Assert.Fail $"Unexpected: {other}"
        | _ -> Assert.Fail "Failed to parse wchar_t *"

    [<Fact>]
    let ``VoidPointer matches void pointer`` () =
        match "void *" with
        | ParsedCType info ->
            match info with
            | VoidPointer -> ()
            | other -> Assert.Fail $"Expected VoidPointer, got {other}"
        | _ -> Assert.Fail "Failed to parse void *"

    [<Fact>]
    let ``ValueType matches non-pointer types`` () =
        match "int" with
        | ParsedCType info ->
            match info with
            | ValueType "int" -> ()
            | other -> Assert.Fail $"Expected ValueType int, got {other}"
        | _ -> Assert.Fail "Failed to parse int"

    [<Theory>]
    [<InlineData("__STDC__")>]
    [<InlineData("__GNUC__")>]
    [<InlineData("__FILE__")>]
    let ``CompilerBuiltin matches double-underscore bookended names`` (name: string) =
        match name with
        | CompilerBuiltin -> ()
        | _ -> Assert.Fail $"Expected CompilerBuiltin for '{name}'"

    [<Theory>]
    [<InlineData("_POSIX_SOURCE")>]
    [<InlineData("_GNU_SOURCE")>]
    let ``InternalMacro matches underscore-uppercase names`` (name: string) =
        match name with
        | InternalMacro -> ()
        | _ -> Assert.Fail $"Expected InternalMacro for '{name}'"

    [<Theory>]
    [<InlineData("linux")>]
    [<InlineData("unix")>]
    let ``PredefinedMacro matches platform names`` (name: string) =
        match name with
        | PredefinedMacro -> ()
        | _ -> Assert.Fail $"Expected PredefinedMacro for '{name}'"

    [<Theory>]
    [<InlineData("FOO")>]
    [<InlineData("MY_CONSTANT")>]
    [<InlineData("EXIT_SUCCESS")>]
    let ``UserMacro matches everything else`` (name: string) =
        match name with
        | UserMacro -> ()
        | _ -> Assert.Fail $"Expected UserMacro for '{name}'"

    [<Fact>]
    let ``IntegerLiteral parses decimal`` () =
        match "42" with
        | IntegerLiteral 42L -> ()
        | IntegerLiteral n -> Assert.Fail $"Expected 42, got {n}"
        | _ -> Assert.Fail "Failed to match IntegerLiteral"

    [<Fact>]
    let ``IntegerLiteral parses hex`` () =
        match "0xFF" with
        | IntegerLiteral 255L -> ()
        | IntegerLiteral n -> Assert.Fail $"Expected 255, got {n}"
        | _ -> Assert.Fail "Failed to match IntegerLiteral"

    [<Fact>]
    let ``IntegerLiteral rejects non-numeric`` () =
        match "hello" with
        | IntegerLiteral _ -> Assert.Fail "Should not match non-numeric"
        | _ -> ()

    [<Fact>]
    let ``FSharpKeyword detects keywords`` () =
        match "base" with
        | FSharpKeyword "base" -> ()
        | _ -> Assert.Fail "Expected FSharpKeyword for 'base'"

    [<Fact>]
    let ``FSharpKeyword strips leading underscores`` () =
        match "__base" with
        | FSharpKeyword "base" -> ()
        | _ -> Assert.Fail "Expected FSharpKeyword for '__base'"

    [<Fact>]
    let ``CleanName for non-keywords`` () =
        match "myParam" with
        | CleanName "myParam" -> ()
        | _ -> Assert.Fail "Expected CleanName for 'myParam'"

    [<Fact>]
    let ``cleanParamName backtick-quotes keywords`` () =
        Assert.Equal("``base``", cleanParamName "base")
        Assert.Equal("``base``", cleanParamName "__base")
        Assert.Equal("count", cleanParamName "count")

    [<Fact>]
    let ``ArrayType extracts size`` () =
        match "uint32_t[4]" with
        | ArrayType 4 -> ()
        | ArrayType n -> Assert.Fail $"Expected 4, got {n}"
        | _ -> Assert.Fail "Failed to match ArrayType"

// =============================================================================
// DeclarationAlgebra Tests — catamorphism over Declaration DU
// =============================================================================

module DeclarationAlgebraTests =

    let private mkFunc name retType parms : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false }

    let private mkTypedef name underlying : CppParser.TypedefInfo =
        { Name = name; UnderlyingType = underlying; Documentation = None }

    let private mkEnum name values doc : CppParser.EnumDecl =
        { Name = name; Values = values; Documentation = doc; UnderlyingType = None }

    let private mkEnumVal name value : CppParser.EnumValue =
        { Name = name; Value = value; Documentation = None }

    let private mkStruct name fields doc : CppParser.StructDecl =
        { Name = name; Fields = fields; Documentation = doc; IsUnion = false }

    [<Fact>]
    let ``typedefAlgebra extracts typedef pairs`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
            CppParser.Declaration.Typedef (mkTypedef "__off_t" "long int")
            CppParser.Declaration.Function (mkFunc "foo" "void" [])
        ]
        let results =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra decls
            |> List.choose id
        Assert.Equal(2, results.Length)
        Assert.Equal(("size_t", "unsigned long"), results.[0])
        Assert.Equal(("__off_t", "long int"), results.[1])

    [<Fact>]
    let ``typedefAlgebra ignores non-typedef declarations`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "read" "int" [])
            CppParser.Declaration.Enum (mkEnum "Color" [] None)
        ]
        let results =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra decls
            |> List.choose id
        Assert.Empty(results)

    [<Fact>]
    let ``structNameAlgebra extracts struct names`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "MyStruct" [] None)
            CppParser.Declaration.Function (mkFunc "foo" "void" [])
            CppParser.Declaration.Class {
                Name = "MyClass"; Fields = []; Documentation = None; Methods = []; IsAbstract = false
            }
        ]
        let results =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.structNameAlgebra decls
            |> List.choose id
        Assert.Equal(2, results.Length)
        Assert.Contains("MyStruct", results)
        Assert.Contains("MyClass", results)

    [<Fact>]
    let ``cataDeclarations preserves order`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "A" "int")
            CppParser.Declaration.Typedef (mkTypedef "B" "char")
            CppParser.Declaration.Typedef (mkTypedef "C" "void")
        ]
        let results =
            DeclarationAlgebra.cataDeclarations DeclarationAlgebra.typedefAlgebra decls
            |> List.choose id
            |> List.map fst
        Assert.Equal<string list>(["A"; "B"; "C"], results)

// =============================================================================
// CodeAST + CodeRenderer Tests — typed AST to F# source
// =============================================================================

module CodeRendererTests =

    open CodeAST
    open CodeRenderer

    [<Fact>]
    let ``renderType handles Named types`` () =
        Assert.Equal("int32", renderType (Named "int32"))
        Assert.Equal("nativeint", renderType (Named "nativeint"))

    [<Fact>]
    let ``renderType handles Generic types`` () =
        Assert.Equal("nativeptr<byte>", renderType (Generic("nativeptr", Named "byte")))

    [<Fact>]
    let ``renderType handles Unit`` () =
        Assert.Equal("unit", renderType Unit)

    [<Fact>]
    let ``render LetBinding produces correct F# function`` () =
        let decl = Module("Test.Module", "test",
            [
                LetBinding("myFunc",
                    [{ Name = "x"; Type = Named "int32" }; { Name = "y"; Type = Named "int32" }],
                    Named "int32",
                    DefaultOf (Named "int32"))
            ])
        let result = render decl
        Assert.Contains("let myFunc (x: int32) (y: int32) : int32 =", result)
        Assert.Contains("Unchecked.defaultof<int32>", result)

    [<Fact>]
    let ``render LetBinding with nativeptr param`` () =
        let decl = Module("Test.Module", "test",
            [
                LetBinding("strcpy",
                    [{ Name = "dest"; Type = Generic("nativeptr", Named "byte") }
                     { Name = "src"; Type = Generic("nativeptr", Named "byte") }],
                    Generic("nativeptr", Named "byte"),
                    DefaultOf (Generic("nativeptr", Named "byte")))
            ])
        let result = render decl
        Assert.Contains("let strcpy (dest: nativeptr<byte>) (src: nativeptr<byte>) : nativeptr<byte> =", result)

    [<Fact>]
    let ``render LiteralBinding produces Literal attribute`` () =
        let decl = Module("Test.Module", "test",
            [ LiteralBinding("EXIT_SUCCESS", "0") ])
        let result = render decl
        Assert.Contains("[<Literal>]", result)
        Assert.Contains("let EXIT_SUCCESS = 0", result)

    [<Fact>]
    let ``render EnumType produces F# enum`` () =
        let decl = Module("Test.Module", "test",
            [ EnumType("Color", [("Red", 0L); ("Green", 1L); ("Blue", 2L)], Some "A color enum") ])
        let result = render decl
        Assert.Contains("type Color =", result)
        Assert.Contains("| Red = 0L", result)
        Assert.Contains("| Green = 1L", result)
        Assert.Contains("| Blue = 2L", result)
        Assert.Contains("/// A color enum", result)

    [<Fact>]
    let ``render RecordType produces F# record`` () =
        let decl = Module("Test.Module", "test",
            [ RecordType("Point", [("x", Named "int32"); ("y", Named "int32")], Some "A 2D point") ])
        let result = render decl
        Assert.Contains("type Point = {", result)
        Assert.Contains("x: int32", result)
        Assert.Contains("y: int32", result)
        Assert.Contains("/// A 2D point", result)

    [<Fact>]
    let ``render Module produces header comment`` () =
        let decl = Module("Fidelity.libc.Memory", "libc", [])
        let result = render decl
        Assert.Contains("module Fidelity.libc.Memory", result)
        Assert.Contains("Generated by Farscape", result)
        Assert.Contains("Fidelity binding for libc", result)

    [<Fact>]
    let ``render XmlDoc produces triple-slash comment`` () =
        let decl = Module("Test", "test",
            [ XmlDoc "void foo(int x)" ])
        let result = render decl
        Assert.Contains("/// void foo(int x)", result)

    [<Fact>]
    let ``render zero-param function uses unit`` () =
        let decl = Module("Test", "test",
            [ LetBinding("abort", [], Unit, DefaultOf Unit) ])
        let result = render decl
        Assert.Contains("let abort () : unit =", result)

// =============================================================================
// FidelityCodeGenerator Integration Tests — end-to-end declaration → source
// =============================================================================

module FidelityCodeGeneratorTests =

    let private mkFunc name retType parms : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false }

    let private mkTypedef name underlying : CppParser.TypedefInfo =
        { Name = name; UnderlyingType = underlying; Documentation = None }

    let private mkEnum name values doc : CppParser.EnumDecl =
        { Name = name; Values = values; Documentation = doc; UnderlyingType = None }

    let private mkEnumVal name value : CppParser.EnumValue =
        { Name = name; Value = value; Documentation = None }

    let private mkStruct name fields doc : CppParser.StructDecl =
        { Name = name; Fields = fields; Documentation = doc; IsUnion = false }

    let private mkField name typ : CppParser.FieldDecl =
        { Name = name; Type = typ; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None }

    [<Fact>]
    let ``buildTypedefMap resolves simple typedefs`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        ]
        let map = FidelityCodeGenerator.buildTypedefMap decls
        Assert.Equal("unsigned long", map.["size_t"])

    [<Fact>]
    let ``buildTypedefMap resolves typedef chains`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "__off_t" "__OFF_T_TYPE")
            CppParser.Declaration.Typedef (mkTypedef "__OFF_T_TYPE" "long int")
        ]
        let map = FidelityCodeGenerator.buildTypedefMap decls
        Assert.Equal("long int", map.["__off_t"])

    [<Fact>]
    let ``generate produces valid Fidelity binding for simple function`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = FidelityCodeGenerator.generate decls "Fidelity.libc.Test" "libc"
        Assert.Contains("module Fidelity.libc.Test", result)
        Assert.Contains("let getpid () : int32 =", result)
        Assert.Contains("Unchecked.defaultof<int32>", result)

    [<Fact>]
    let ``generate maps char pointer params to nativeptr<byte>`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [("__s", "const char *")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("(s: nativeptr<byte>)", result)

    [<Fact>]
    let ``generate maps void pointer params to nativeint`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "memset" "void *" [("__s", "void *"); ("__c", "int"); ("__n", "size_t")])
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("(s: nativeint)", result)
        Assert.Contains(": nativeint =", result)

    [<Fact>]
    let ``generate handles function pointer params as nativeint`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "atexit" "int" [("__func", "void (*)(void)")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("(func: nativeint)", result)

    [<Fact>]
    let ``generate resolves typedef to function pointer as nativeint`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "__compar_fn_t" "int (*)(const void *, const void *)")
            CppParser.Declaration.Function (mkFunc "qsort" "void" [
                ("__base", "void *"); ("__nmemb", "size_t")
                ("__size", "size_t"); ("__compar", "__compar_fn_t")
            ])
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("(compar: nativeint)", result)

    [<Fact>]
    let ``generate does not map wchar_t pointer as nativeptr<byte>`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "mbtowc" "int" [("__pwc", "wchar_t *"); ("__s", "const char *"); ("__n", "size_t")])
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        // wchar_t * should be nativeint, not nativeptr<byte>
        Assert.Contains("(pwc: nativeint)", result)

    [<Fact>]
    let ``generate emits numeric macro constants`` () =
        let decls = [
            CppParser.Declaration.Macro {
                Name = "EXIT_SUCCESS"; Kind = CppParser.SimpleValue "0"; RawValue = "0"
            }
            CppParser.Declaration.Macro {
                Name = "EXIT_FAILURE"; Kind = CppParser.SimpleValue "1"; RawValue = "1"
            }
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("[<Literal>]", result)
        Assert.Contains("let EXIT_SUCCESS = 0", result)
        Assert.Contains("let EXIT_FAILURE = 1", result)

    [<Fact>]
    let ``generate filters compiler builtin macros`` () =
        let decls = [
            CppParser.Declaration.Macro { Name = "__STDC__"; Kind = CppParser.SimpleValue "1"; RawValue = "1" }
            CppParser.Declaration.Macro { Name = "FOO"; Kind = CppParser.SimpleValue "42"; RawValue = "42" }
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.DoesNotContain("__STDC__", result)
        Assert.Contains("let FOO = 42", result)

    [<Fact>]
    let ``generate deduplicates functions by name`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "read" "int" [("__fd", "int")])
            CppParser.Declaration.Function (mkFunc "read" "int" [("__fd", "int")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        let occurrences =
            result.Split("let read ")
            |> Array.length
        // "let read " should appear exactly once (split gives 2 parts)
        Assert.Equal(2, occurrences)

    [<Fact>]
    let ``generate emits enums`` () =
        let decls = [
            CppParser.Declaration.Enum (mkEnum "Flags" [mkEnumVal "A" 0L; mkEnumVal "B" 1L] (Some "Test flags"))
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("type Flags =", result)
        Assert.Contains("| A = 0L", result)
        Assert.Contains("| B = 1L", result)

    [<Fact>]
    let ``generate emits struct as record`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] (Some "A point"))
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("type Point = {", result)
        Assert.Contains("x: int32", result)
        Assert.Contains("y: int32", result)
