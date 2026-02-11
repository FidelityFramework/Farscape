module Tests

open Xunit
open Farscape.Core

// =============================================================================
// CTypeParser Tests: XParsec monadic parsers for C type strings
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
// ActivePatterns Tests: structured decomposition backed by XParsec
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
            | TypedPointer _ -> () // correct; wchar_t is a typed pointer
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
// DeclarationAlgebra Tests: catamorphism over Declaration DU
// =============================================================================

module DeclarationAlgebraTests =

    let private mkFunc name retType parms : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

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

    [<Fact>]
    let ``mergeDeclarations deduplicates shared typedefs`` () =
        let typedef1 = CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        let typedef2 = CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        let fn1 = CppParser.Declaration.Function (mkFunc "read" "ssize_t" [("fd", "int")])
        let fn2 = CppParser.Declaration.Function (mkFunc "open" "int" [("path", "const char *")])
        let merged = DeclarationAlgebra.mergeDeclarations [[typedef1; fn1]; [typedef2; fn2]]
        Assert.Equal(3, merged.Length)

    [<Fact>]
    let ``mergeDeclarations preserves order from first list`` () =
        let merged = DeclarationAlgebra.mergeDeclarations [
            [CppParser.Declaration.Function (mkFunc "read" "int" [])
             CppParser.Declaration.Function (mkFunc "write" "int" [])]
            [CppParser.Declaration.Function (mkFunc "open" "int" [])]
        ]
        Assert.Equal(3, merged.Length)

    [<Fact>]
    let ``mergeDeclarations with single list is identity`` () =
        let input = [
            CppParser.Declaration.Function (mkFunc "read" "int" [])
            CppParser.Declaration.Function (mkFunc "write" "int" [])
        ]
        let merged = DeclarationAlgebra.mergeDeclarations [input]
        Assert.Equal(input.Length, merged.Length)

// =============================================================================
// CodeAST + CodeRenderer Tests: typed AST to F# source
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
                    DefaultOf (Named "int32"),
                    [])
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
                    DefaultOf (Generic("nativeptr", Named "byte")),
                    [])
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
            [ RecordType("Point", [("x", Named "int32"); ("y", Named "int32")], Some "A 2D point", []) ])
        let result = render decl
        Assert.Contains("type Point = {", result)
        Assert.Contains("x: int32", result)
        Assert.Contains("y: int32", result)
        Assert.Contains("/// A 2D point", result)

    [<Fact>]
    let ``render Module produces header comment`` () =
        let decl = Module("Fidelity.libc.Memory", "Fidelity binding for libc", [])
        let result = render decl
        Assert.Contains("module Fidelity.libc.Memory", result)
        Assert.Contains("Generated by Farscape", result)
        Assert.Contains("Fidelity binding for libc", result)

    [<Fact>]
    let ``render ExternDecl produces DllImport and extern`` () =
        let decl = Module("Test", "test",
            [ ExternDecl("getpid", [], Named "int32", "libc") ])
        let result = render decl
        Assert.Contains("[<DllImport(\"libc\", CallingConvention = CallingConvention.Cdecl)>]", result)
        Assert.Contains("extern int32 getpid()", result)

    [<Fact>]
    let ``render ExternDecl with params uses C-style syntax`` () =
        let decl = Module("Test", "test",
            [ ExternDecl("read", [
                { Name = "fd"; Type = Named "int32" }
                { Name = "buf"; Type = Named "nativeint" }
                { Name = "count"; Type = Named "unativeint" }
              ], Named "nativeint", "libc") ])
        let result = render decl
        Assert.Contains("extern nativeint read(int32 fd, nativeint buf, unativeint count)", result)

    [<Fact>]
    let ``render XmlDoc produces triple-slash comment`` () =
        let decl = Module("Test", "test",
            [ XmlDoc "void foo(int x)" ])
        let result = render decl
        Assert.Contains("/// void foo(int x)", result)

    [<Fact>]
    let ``render zero-param function uses unit`` () =
        let decl = Module("Test", "test",
            [ LetBinding("abort", [], Unit, DefaultOf Unit, []) ])
        let result = render decl
        Assert.Contains("let abort () : unit =", result)

    [<Fact>]
    let ``render LetBinding with attributes produces attribute lines`` () =
        let decl = Module("Test", "test",
            [ LetBinding("memcpy",
                [{ Name = "dest"; Type = Named "nativeint" }
                 { Name = "src"; Type = Named "nativeint" }
                 { Name = "n"; Type = Named "nativeint" }],
                Named "nativeint",
                DefaultOf (Named "nativeint"),
                ["FidelityExtern(\"libc\", \"memcpy\")"]) ])
        let result = render decl
        Assert.Contains("[<FidelityExtern(\"libc\", \"memcpy\")>]", result)
        Assert.Contains("let memcpy (dest: nativeint) (src: nativeint) (n: nativeint) : nativeint =", result)

// =============================================================================
// FidelityCodeGenerator Integration Tests: end-to-end declaration → source
// =============================================================================

module FidelityCodeGeneratorTests =

    let private mkFunc name retType parms : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

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
                Name = "EXIT_SUCCESS"; Kind = CppParser.SimpleValue "0"; RawValue = "0"; Documentation = None
            }
            CppParser.Declaration.Macro {
                Name = "EXIT_FAILURE"; Kind = CppParser.SimpleValue "1"; RawValue = "1"; Documentation = None
            }
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("[<Literal>]", result)
        Assert.Contains("let EXIT_SUCCESS = 0", result)
        Assert.Contains("let EXIT_FAILURE = 1", result)

    [<Fact>]
    let ``generate filters compiler builtin macros`` () =
        let decls = [
            CppParser.Declaration.Macro { Name = "__STDC__"; Kind = CppParser.SimpleValue "1"; RawValue = "1"; Documentation = None }
            CppParser.Declaration.Macro { Name = "FOO"; Kind = CppParser.SimpleValue "42"; RawValue = "42"; Documentation = None }
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

    [<Fact>]
    let ``generate emits FidelityExtern attribute on function bindings`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "memcpy" "void *" [("__dest", "void *"); ("__src", "const void *"); ("__n", "size_t")])
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        ]
        let result = FidelityCodeGenerator.generate decls "Fidelity.libc.Memory" "libc"
        Assert.Contains("[<FidelityExtern(\"libc\", \"memcpy\")>]", result)
        Assert.Contains("let memcpy", result)

    [<Fact>]
    let ``generate emits FidelityExtern with correct library name`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "wl_display_connect" "void *" [("__name", "const char *")])
        ]
        let result = FidelityCodeGenerator.generate decls "Fidelity.wayland.Display" "wayland-client"
        Assert.Contains("[<FidelityExtern(\"wayland-client\", \"wl_display_connect\")>]", result)

// =============================================================================
// MoyaAnalyzer Tests: prefix analysis and declaration filtering
// =============================================================================

module MoyaAnalyzerTests =

    let private mkFunc name retType parms : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

    let private mkStruct name : CppParser.StructDecl =
        { Name = name; Fields = []; Documentation = None; IsUnion = false }

    let private mkEnum name : CppParser.EnumDecl =
        { Name = name; Values = []; Documentation = None; UnderlyingType = None }

    // --- functionNameAlgebra tests ---

    [<Fact>]
    let ``functionNameAlgebra extracts function names`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
            CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
        ]
        let result = MoyaAnalyzer.extractFunctionNames decls
        Assert.Equal(2, result.Length)
        Assert.Contains("strlen", result)
        Assert.Contains("memcpy", result)

    [<Fact>]
    let ``functionNameAlgebra ignores non-function declarations`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point")
            CppParser.Declaration.Function (mkFunc "read" "int" [])
            CppParser.Declaration.Enum (mkEnum "Flags")
        ]
        let result = MoyaAnalyzer.extractFunctionNames decls
        Assert.Equal(1, result.Length)
        Assert.Equal("read", result[0])

    // --- extractPrefix tests ---

    [<Theory>]
    [<InlineData("io_read", "io")>]
    [<InlineData("gpio_init", "gpio")>]
    [<InlineData("uart_send", "uart")>]
    let ``extractPrefix detects underscore-separated prefixes`` (name: string) (expected: string) =
        match MoyaAnalyzer.extractPrefix name with
        | Some prefix -> Assert.Equal(expected, prefix)
        | None -> Assert.Fail $"Expected prefix '{expected}' for '{name}'"

    [<Theory>]
    [<InlineData("strlen", "str")>]
    [<InlineData("strcmp", "str")>]
    [<InlineData("strcpy", "str")>]
    [<InlineData("memcpy", "mem")>]
    [<InlineData("memset", "mem")>]
    let ``extractPrefix detects known C library prefixes`` (name: string) (expected: string) =
        match MoyaAnalyzer.extractPrefix name with
        | Some prefix -> Assert.Equal(expected, prefix)
        | None -> Assert.Fail $"Expected prefix '{expected}' for '{name}'"

    [<Theory>]
    [<InlineData("HAL_GPIO_Init", "HAL_GPIO")>]
    [<InlineData("HAL_UART_Transmit", "HAL_UART")>]
    let ``extractPrefix detects HAL-style prefixes`` (name: string) (expected: string) =
        match MoyaAnalyzer.extractPrefix name with
        | Some prefix -> Assert.Equal(expected, prefix)
        | None -> Assert.Fail $"Expected prefix '{expected}' for '{name}'"

    // --- clusterByPrefix tests ---

    [<Fact>]
    let ``clusterByPrefix groups functions by shared prefix`` () =
        let names = ["strlen"; "strcmp"; "strcpy"; "memcpy"; "memset"; "abort"]
        let groups, ungrouped = MoyaAnalyzer.clusterByPrefix names
        Assert.True(groups |> List.exists (fun g -> g.Prefixes |> List.contains "str"))
        Assert.True(groups |> List.exists (fun g -> g.Prefixes |> List.contains "mem"))
        Assert.Contains("abort", ungrouped)

    [<Fact>]
    let ``clusterByPrefix respects minimum group size`` () =
        let names = ["strlen"; "abort"; "exit"]
        let groups, ungrouped = MoyaAnalyzer.clusterByPrefix names
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
            CppParser.Declaration.Struct (mkStruct "Point")
            CppParser.Declaration.Function (mkFunc "abort" "void" [])
        ]
        let result = MoyaAnalyzer.analyze decls
        Assert.Equal(5, result.TotalFunctions)
        Assert.True(result.Groups.Length >= 2) // str + mem groups
        Assert.Contains("abort", result.Ungrouped)

    // --- filterDeclarationsForNamespace tests ---

    [<Fact>]
    let ``filterDeclarationsForNamespace keeps functions matching prefix`` () =
        let spec : MoyaTypes.NamespaceSpec =
            { Name = "Test.String"; Description = ""; Library = "libc"
              Prefixes = ["str"]; Functions = [] }
        let decls = [
            CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
            CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
        ]
        let filtered = MoyaAnalyzer.filterDeclarationsForNamespace spec decls
        let funcNames = MoyaAnalyzer.extractFunctionNames filtered
        Assert.Contains("strlen", funcNames)
        Assert.DoesNotContain("memcpy", funcNames)

    [<Fact>]
    let ``filterDeclarationsForNamespace keeps explicitly listed functions`` () =
        let spec : MoyaTypes.NamespaceSpec =
            { Name = "Test.Misc"; Description = ""; Library = "libc"
              Prefixes = []; Functions = ["abort"; "exit"] }
        let decls = [
            CppParser.Declaration.Function (mkFunc "abort" "void" [])
            CppParser.Declaration.Function (mkFunc "exit" "void" [])
            CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [])
        ]
        let filtered = MoyaAnalyzer.filterDeclarationsForNamespace spec decls
        let funcNames = MoyaAnalyzer.extractFunctionNames filtered
        Assert.Contains("abort", funcNames)
        Assert.Contains("exit", funcNames)
        Assert.DoesNotContain("strlen", funcNames)

    [<Fact>]
    let ``filterDeclarationsForNamespace passes through non-function declarations`` () =
        let spec : MoyaTypes.NamespaceSpec =
            { Name = "Test.String"; Description = ""; Library = "libc"
              Prefixes = ["str"]; Functions = [] }
        let decls = [
            CppParser.Declaration.Struct (mkStruct "size_t")
            CppParser.Declaration.Enum (mkEnum "Flags")
            CppParser.Declaration.Function (mkFunc "memcpy" "void *" [])
        ]
        let filtered = MoyaAnalyzer.filterDeclarationsForNamespace spec decls
        // Structs and enums pass through, memcpy gets filtered out
        Assert.Equal(2, filtered.Length)

// =============================================================================
// MoyaSerializer Tests: TOML round-trip and deserialization
// =============================================================================

module MoyaSerializerTests =

    let private sampleProject : MoyaTypes.MoyaProject = {
        Library = {
            Name = "libc"
            Headers = ["/usr/include/string.h"]
            IncludePaths = ["/usr/include"]
            Defines = ["_GNU_SOURCE"]
        }
        Output = { Mode = "fidelity"; Directory = "./bindings" }
        Namespaces = [
            { Name = "Fidelity.libc.Memory"
              Description = "Memory operations"
              Library = "libc"
              Prefixes = ["mem"; "str"]
              Functions = [] }
            { Name = "Fidelity.libc.IO"
              Description = "I/O operations"
              Library = "libc"
              Prefixes = ["read"; "write"]
              Functions = ["pipe"] }
        ]
        ErrorConventions = None
    }

    [<Fact>]
    let ``serialize produces valid TOML with all sections`` () =
        let toml = MoyaSerializer.toTomlString sampleProject
        Assert.Contains("name = \"libc\"", toml)
        Assert.Contains("header = \"/usr/include/string.h\"", toml)
        Assert.Contains("mode = \"fidelity\"", toml)
        Assert.Contains("directory = \"./bindings\"", toml)
        Assert.Contains("[[namespace]]", toml)
        Assert.Contains("Fidelity.libc.Memory", toml)
        Assert.Contains("Fidelity.libc.IO", toml)

    [<Fact>]
    let ``round-trip serialize then deserialize produces identical project`` () =
        let toml = MoyaSerializer.toTomlString sampleProject
        match Fidelity.Toml.Toml.parse toml with
        | Ok doc ->
            match MoyaSerializer.deserialize doc with
            | Ok roundTripped ->
                Assert.Equal(sampleProject.Library.Name, roundTripped.Library.Name)
                Assert.Equal(sampleProject.Library.Header, roundTripped.Library.Header)
                Assert.Equal<string list>(sampleProject.Library.IncludePaths, roundTripped.Library.IncludePaths)
                Assert.Equal<string list>(sampleProject.Library.Defines, roundTripped.Library.Defines)
                Assert.Equal(sampleProject.Output.Mode, roundTripped.Output.Mode)
                Assert.Equal(sampleProject.Output.Directory, roundTripped.Output.Directory)
                Assert.Equal(sampleProject.Namespaces.Length, roundTripped.Namespaces.Length)
                Assert.Equal(sampleProject.Namespaces[0].Name, roundTripped.Namespaces[0].Name)
                Assert.Equal<string list>(sampleProject.Namespaces[0].Prefixes, roundTripped.Namespaces[0].Prefixes)
                Assert.Equal<string list>(sampleProject.Namespaces[1].Functions, roundTripped.Namespaces[1].Functions)
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Error e -> Assert.Fail $"Parse failed: {e}"

    [<Fact>]
    let ``deserialize returns Error for missing library section`` () =
        let doc = Fidelity.Toml.Toml.parseOrFail "[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        match MoyaSerializer.deserialize doc with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "Should return Error for missing [library]"

    [<Fact>]
    let ``deserialize returns Error for missing output section`` () =
        let doc = Fidelity.Toml.Toml.parseOrFail "[library]\nname = \"test\"\nheader = \"test.h\""
        match MoyaSerializer.deserialize doc with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "Should return Error for missing [output]"

    [<Fact>]
    let ``deserialize handles empty namespace array`` () =
        let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Toml.Toml.parseOrFail toml
        match MoyaSerializer.deserialize doc with
        | Ok project -> Assert.Empty(project.Namespaces)
        | Error e -> Assert.Fail $"Should succeed with no namespaces: {e}"

    [<Fact>]
    let ``deserialize handles optional functions field`` () =
        let toml = """
[library]
name = "test"
header = "test.h"
[output]
mode = "fidelity"
directory = "./out"
[[namespace]]
name = "Test.Str"
description = "String ops"
library = "test"
prefixes = ["str"]
"""
        let doc = Fidelity.Toml.Toml.parseOrFail toml
        match MoyaSerializer.deserialize doc with
        | Ok project ->
            Assert.Equal(1, project.Namespaces.Length)
            Assert.Empty(project.Namespaces[0].Functions)
        | Error e -> Assert.Fail $"Should succeed: {e}"

    [<Fact>]
    let ``loadFromFile returns Error for nonexistent file`` () =
        match MoyaSerializer.loadFromFile "/nonexistent/path.moya.toml" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "Should return Error for missing file"

    [<Fact>]
    let ``multi-header project serializes with headers array`` () =
        let project = { sampleProject with
                          Library = { sampleProject.Library with
                                        Headers = ["/usr/include/unistd.h"; "/usr/include/fcntl.h"] } }
        let toml = MoyaSerializer.toTomlString project
        Assert.Contains("headers", toml)
        Assert.Contains("/usr/include/unistd.h", toml)
        Assert.Contains("/usr/include/fcntl.h", toml)

    [<Fact>]
    let ``multi-header round-trip preserves all headers`` () =
        let project = { sampleProject with
                          Library = { sampleProject.Library with
                                        Headers = ["/usr/include/unistd.h"; "/usr/include/fcntl.h"] } }
        let toml = MoyaSerializer.toTomlString project
        match Fidelity.Toml.Toml.parse toml with
        | Ok doc ->
            match MoyaSerializer.deserialize doc with
            | Ok roundTripped ->
                Assert.Equal<string list>(project.Library.Headers, roundTripped.Library.Headers)
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Error e -> Assert.Fail $"Parse failed: {e}"

    [<Fact>]
    let ``single header backward compat still works`` () =
        let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Toml.Toml.parseOrFail toml
        match MoyaSerializer.deserialize doc with
        | Ok project -> Assert.Equal<string list>(["test.h"], project.Library.Headers)
        | Error e -> Assert.Fail $"Should parse single header: {e}"

    [<Fact>]
    let ``deserialize rejects empty headers array`` () =
        let toml = "[library]\nname = \"test\"\nheaders = []\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Toml.Toml.parseOrFail toml
        match MoyaSerializer.deserialize doc with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "Should reject empty headers"

// =============================================================================
// Error Convention TOML Tests
// =============================================================================

module ErrorConventionTomlTests =

    open MoyaTypes

    [<Fact>]
    let ``error conventions round-trip through TOML`` () =
        let project : MoyaProject = {
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; IncludePaths = []; Defines = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some {
                Default = Errno
                Overrides = Map.ofList [("pthread_create", ReturnCode); ("strtol", NoErrorConvention)]
            }
        }
        let toml = MoyaSerializer.toTomlString project
        Assert.Contains("error_conventions", toml)
        Assert.Contains("errno", toml)
        match Fidelity.Toml.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match MoyaSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok roundTripped ->
                Assert.True(roundTripped.ErrorConventions.IsSome)
                let spec = roundTripped.ErrorConventions.Value
                Assert.Equal(Errno, spec.Default)
                Assert.Equal(ReturnCode, spec.Overrides.["pthread_create"])
                Assert.Equal(NoErrorConvention, spec.Overrides.["strtol"])

    [<Fact>]
    let ``missing error_conventions deserializes as None`` () =
        let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Toml.Toml.parseOrFail toml
        match MoyaSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok project -> Assert.True(project.ErrorConventions.IsNone)

    [<Fact>]
    let ``error conventions with no overrides`` () =
        let project : MoyaProject = {
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; IncludePaths = []; Defines = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some { Default = Errno; Overrides = Map.empty }
        }
        let toml = MoyaSerializer.toTomlString project
        match Fidelity.Toml.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match MoyaSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok rt ->
                Assert.True(rt.ErrorConventions.IsSome)
                Assert.Equal(Errno, rt.ErrorConventions.Value.Default)
                Assert.True(rt.ErrorConventions.Value.Overrides.IsEmpty)

// =============================================================================
// WrapperPatternAnalyzer Tests: Attribute mapping and inference
// =============================================================================

module WrapperPatternAnalyzerTests =

    open WrapperTypes

    let private mkFunc name retType parms attrs : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false
          Attributes = attrs }

    let private mkAttr kind args strArg : CppParser.AttributeData =
        { Kind = kind; Args = args; StringArg = strArg }

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
        let result = WrapperPatternAnalyzer.analyzeReturn "void" [NoReturn] Map.empty
        Assert.Equal(NeverReturns, result)

    [<Fact>]
    let ``analyzeReturn AllocSize attribute produces AllocatedPointer`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "void *" [AllocSize [0]] Map.empty
        Assert.Equal(AllocatedPointer, result)

    [<Fact>]
    let ``analyzeReturn Pure attribute produces PureValue`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "int" [Pure] Map.empty
        Assert.Equal(PureValue, result)

    [<Fact>]
    let ``analyzeReturn ssize_t return produces CountOrError`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "ssize_t" [] Map.empty
        Assert.Equal(CountOrError, result)

    [<Fact>]
    let ``analyzeReturn void return produces VoidReturn`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "void" [] Map.empty
        Assert.Equal(VoidReturn, result)

    [<Fact>]
    let ``analyzeReturn void pointer produces AllocatedPointer`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "void *" [] Map.empty
        Assert.Equal(AllocatedPointer, result)

    [<Fact>]
    let ``analyzeReturn FILE pointer produces OpaqueHandleReturn`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "FILE *" [] Map.empty
        Assert.Equal(OpaqueHandleReturn, result)

    [<Fact>]
    let ``analyzeReturn int produces ZeroSuccessOrError`` () =
        let result = WrapperPatternAnalyzer.analyzeReturn "int" [] Map.empty
        Assert.Equal(ZeroSuccessOrError, result)

    [<Fact>]
    let ``analyzeReturn resolves typedef to underlying type`` () =
        let tdMap = Map.ofList [("__ssize_t", "long")]
        let result = WrapperPatternAnalyzer.analyzeReturn "__ssize_t" [] tdMap
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
        let func = mkFunc "read" "ssize_t"
                    [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                    []
        let pattern = WrapperPatternAnalyzer.analyze func Map.empty
        Assert.Equal(CountOrError, pattern.ReturnSemantic)
        Assert.True(pattern.NeedsResultWrap)
        Assert.False(pattern.IsPure)

    [<Fact>]
    let ``analyze malloc with AllocSizeAttr produces AllocatedPointer pattern`` () =
        let func = mkFunc "malloc" "void *"
                    [("size", "size_t")]
                    [mkAttr "AllocSizeAttr" [0] None; mkAttr "NoThrowAttr" [] None]
        let pattern = WrapperPatternAnalyzer.analyze func Map.empty
        Assert.Equal(AllocatedPointer, pattern.ReturnSemantic)
        Assert.True(pattern.NeedsResultWrap)
        Assert.True(pattern.NeedsResourceCleanup)

    [<Fact>]
    let ``analyze abort with NoReturnAttr produces NeverReturns pattern`` () =
        let func = mkFunc "abort" "void" []
                    [mkAttr "NoReturnAttr" [] None]
        let pattern = WrapperPatternAnalyzer.analyze func Map.empty
        Assert.Equal(NeverReturns, pattern.ReturnSemantic)
        Assert.False(pattern.NeedsResultWrap)

    [<Fact>]
    let ``analyze abs with PureAttr produces PureValue pattern`` () =
        let func = mkFunc "abs" "int"
                    [("x", "int")]
                    [mkAttr "PureAttr" [] None; mkAttr "NoThrowAttr" [] None]
        let pattern = WrapperPatternAnalyzer.analyze func Map.empty
        Assert.Equal(PureValue, pattern.ReturnSemantic)
        Assert.False(pattern.NeedsResultWrap)
        Assert.True(pattern.IsPure)

// =============================================================================
// WrapperCodeGenerator Tests: End-to-end wrapper generation
// =============================================================================

module WrapperCodeGeneratorTests =

    let private mkDecl name retType parms attrs =
        CppParser.Declaration.Function
            { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
              IsVirtual = false; IsStatic = false; IsInline = false; Attributes = attrs }

    let private mkAttr kind args strArg : CppParser.AttributeData =
        { Kind = kind; Args = args; StringArg = strArg }

    let private generateSingle name retType parms attrs =
        let decls = [ mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" None

    [<Fact>]
    let ``CountOrError wrapper generates Result return and comparison`` () =
        let output = generateSingle "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("let read", output)
        Assert.Contains("Result<", output)
        Assert.Contains("let result = Platform.Bindings.Test.read", output)
        Assert.Contains("if result >= 0n then Ok", output)
        Assert.Contains("else Error", output)

    [<Fact>]
    let ``AllocatedPointer wrapper generates null check`` () =
        let output = generateSingle "malloc" "void *"
                        [("size", "size_t")]
                        [mkAttr "AllocSizeAttr" [0] None]
        Assert.Contains("let malloc", output)
        Assert.Contains("Result<", output)
        Assert.Contains("let result = Platform.Bindings.Test.malloc", output)
        Assert.Contains("if result <> 0n then Ok", output)
        Assert.Contains("else Error ()", output)

    [<Fact>]
    let ``PureValue wrapper generates direct delegation`` () =
        let output = generateSingle "abs" "int"
                        [("x", "int")]
                        [mkAttr "PureAttr" [] None]
        Assert.Contains("let abs", output)
        Assert.Contains("Platform.Bindings.Test.abs", output)
        // Should NOT have Result wrapping
        Assert.DoesNotContain("Result<", output)
        Assert.DoesNotContain("if result", output)

    [<Fact>]
    let ``ZeroSuccessOrError wrapper generates zero check`` () =
        let output = generateSingle "fclose" "int"
                        [("stream", "FILE *")]
                        []
        Assert.Contains("let fclose", output)
        Assert.Contains("Result<", output)
        Assert.Contains("let result = Platform.Bindings.Test.fclose", output)
        Assert.Contains("if result = 0l then Ok ()", output)

    [<Fact>]
    let ``NeverReturns wrapper generates direct delegation with unit return`` () =
        let output = generateSingle "abort" "void" []
                        [mkAttr "NoReturnAttr" [] None]
        Assert.Contains("let abort", output)
        Assert.Contains(": unit =", output)
        Assert.Contains("Platform.Bindings.Test.abort", output)
        Assert.DoesNotContain("Result<", output)

    [<Fact>]
    let ``VoidReturn wrapper generates direct delegation`` () =
        let output = generateSingle "free" "void"
                        [("ptr", "void *")]
                        []
        Assert.Contains("let free", output)
        Assert.Contains(": unit =", output)
        Assert.Contains("Platform.Bindings.Test.free", output)
        Assert.DoesNotContain("Result<", output)

    [<Fact>]
    let ``wrapper module includes open declaration for bindings`` () =
        let output = generateSingle "abs" "int" [("x", "int")] [mkAttr "PureAttr" [] None]
        Assert.Contains("open Platform.Bindings.Test", output)

    [<Fact>]
    let ``wrapper module has correct namespace`` () =
        let output = generateSingle "abs" "int" [("x", "int")] [mkAttr "PureAttr" [] None]
        Assert.Contains("module Wrappers.Test", output)

    [<Fact>]
    let ``wrapper generates XML doc with original C signature`` () =
        let output = generateSingle "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", output)

    [<Fact>]
    let ``multiple functions are deduplicated by name`` () =
        let decls = [
            mkDecl "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")] []
            mkDecl "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")] []
            mkDecl "write" "ssize_t" [("fd", "int"); ("buf", "const void *"); ("count", "size_t")] []
        ]
        let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" None
        // Count occurrences of "let read"; should be exactly 1
        let readCount = output.Split("let read") |> Array.length
        Assert.Equal(2, readCount) // split produces N+1 parts for N occurrences
        Assert.Contains("let write", output)

// =============================================================================
// Errno-Enabled Wrapper Generation Tests
// =============================================================================

module ErrnoWrapperTests =

    let private mkDecl name retType parms attrs =
        CppParser.Declaration.Function
            { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
              IsVirtual = false; IsStatic = false; IsInline = false; Attributes = attrs }

    let private generateWithErrno name retType parms attrs =
        let decls = [ mkDecl name retType parms attrs ]
        WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" (Some "Fidelity.Errno")

    [<Fact>]
    let ``errno-enabled wrapper uses Result<T, CError> return type`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("Result<nativeint, CError>", output)

    [<Fact>]
    let ``errno-enabled wrapper includes captureError helper`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("let captureError", output)
        Assert.Contains("NativePtr.read", output)
        Assert.Contains("__errno_location", output)
        Assert.Contains("Errno.describe", output)

    [<Fact>]
    let ``errno-enabled wrapper opens errno module`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("open Fidelity.Errno", output)
        Assert.Contains("open Microsoft.FSharp.NativeInterop", output)

    [<Fact>]
    let ``errno-enabled CountOrError calls captureError in error path`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        Assert.Contains("Error (captureError ())", output)
        Assert.Contains("Ok result", output)

    [<Fact>]
    let ``errno-enabled ZeroSuccessOrError calls captureError`` () =
        let output = generateWithErrno "fclose" "int"
                        [("stream", "void *")]
                        []
        Assert.Contains("Result<unit, CError>", output)
        Assert.Contains("Error (captureError ())", output)

    [<Fact>]
    let ``errno-enabled AllocatedPointer calls captureError`` () =
        let output = generateWithErrno "malloc" "void *"
                        [("size", "size_t")]
                        [{ CppParser.AttributeData.Kind = "MallocAttr"; Args = []; StringArg = None }]
        Assert.Contains("Result<nativeint, CError>", output)
        Assert.Contains("Error (captureError ())", output)

    [<Fact>]
    let ``errno-enabled captureError builds CError record`` () =
        let output = generateWithErrno "read" "ssize_t"
                        [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                        []
        // captureError body should construct { Code = code; Description = Errno.describe code }
        Assert.Contains("Code = code", output)
        Assert.Contains("Description = Errno.describe code", output)

    [<Fact>]
    let ``PureValue wrapper unchanged with errno enabled`` () =
        let output = generateWithErrno "strlen" "size_t"
                        [("s", "const char *")]
                        [{ CppParser.AttributeData.Kind = "PureAttr"; Args = []; StringArg = None }]
        // Pure functions don't use Result wrapping — direct delegation
        Assert.Contains("let strlen", output)
        Assert.DoesNotContain("Result<", output.Split("let strlen").[1])

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

module ErrnoModuleGeneratorTests =

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
        let decl = RecordType("CError", [("Code", Named "int32"); ("Description", Named "string")], Some "Error type", ["Struct"])
        let rendered = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("[<Struct>]", rendered)
        Assert.Contains("type CError = {", rendered)
        Assert.Contains("Code: int32", rendered)
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
        Assert.Contains("Code: int32", output)
        Assert.Contains("[<Literal>]", output)
        Assert.Contains("let EPERM = 1", output)
        Assert.Contains("let ENOENT = 2", output)
        Assert.Contains("/// Operation not permitted", output)
        Assert.Contains("let describe (code: int32) : string =", output)
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
            MacroPrefixes = ["E"]
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
            Assert.Contains("let describe (code: int32) : string =", rendered)
            // Verify specific constants with descriptions
            Assert.Contains("let EPERM = 1", rendered)
            Assert.Contains("| EPERM -> \"Operation not permitted\"", rendered)
            Assert.Contains("let ENOENT = 2", rendered)
            Assert.Contains("| ENOENT -> \"No such file or directory\"", rendered)

// =============================================================================
// Documentation Extraction Tests (-fparse-all-comments)
// =============================================================================

module DocumentationExtractionTests =

    open Farscape.Core

    let private mkFunc name retType parms doc : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = doc
          IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

    let private mkDecl name retType parms doc =
        CppParser.Declaration.Function (mkFunc name retType parms doc)

    [<Fact>]
    let ``formatDocDecls with documentation produces description and C signature`` () =
        let func = mkFunc "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                          (Some "Read NBYTES into BUF from FD. Return the number read, -1 for errors or 0 for EOF.")
        let decls = FidelityCodeGenerator.generate
                        [ CppParser.Declaration.Function func ]
                        "Fidelity.libc.Test" "libc"
        // Should contain the description from the header comment
        Assert.Contains("/// Read NBYTES into BUF from FD.", decls)
        // Should contain the C signature
        Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", decls)

    [<Fact>]
    let ``formatDocDecls without documentation produces only C signature`` () =
        let func = mkFunc "getpid" "int" [] None
        let decls = FidelityCodeGenerator.generate
                        [ CppParser.Declaration.Function func ]
                        "Fidelity.libc.Test" "libc"
        Assert.Contains("/// C signature: int getpid()", decls)
        // Should NOT have a blank XML doc line (no description to separate from)
        Assert.DoesNotContain("/// \n", decls)

    [<Fact>]
    let ``wrapper with documented function produces description and C signature`` () =
        let decls = [ mkDecl "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                              (Some "Read NBYTES into BUF from FD.") ]
        let output = WrapperCodeGenerator.generate decls "Wrappers.Test" "testlib" "Platform.Bindings.Test" None
        Assert.Contains("/// Read NBYTES into BUF from FD.", output)
        Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", output)

    [<Fact>]
    let ``struct documentation flows to RecordType output`` () =
        let s : CppParser.StructDecl =
            { Name = "Point"; Fields = [
                { Name = "x"; Type = "int"; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None }
                { Name = "y"; Type = "int"; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None }
              ]; Documentation = Some "A point in 2D space."; IsUnion = false }
        let decls = [ CppParser.Declaration.Struct s ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.test" "test"
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
        let output = FidelityCodeGenerator.generate decls "Fidelity.test" "test"
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

// =============================================================================
// PInvokeCodeGenerator Tests: P/Invoke binding generation
// =============================================================================

module PInvokeCodeGeneratorTests =

    let private mkFunc name retType parms : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
          IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

    let private mkFuncDoc name retType parms doc : CppParser.FunctionDecl =
        { Name = name; ReturnType = retType; Parameters = parms; Documentation = doc
          IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

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
    let ``generate produces ExternDecl with DllImport`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "NativeBindings.libc" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("[<DllImport(\"libc\", CallingConvention = CallingConvention.Cdecl)>]", result)
        Assert.Contains("extern int32 getpid()", result)

    [<Fact>]
    let ``generate maps char pointer to string for P/Invoke`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "puts" "int" [("s", "const char *")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("string s", result)

    [<Fact>]
    let ``generate maps void pointer to nativeint`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "memset" "void *" [("s", "void *"); ("c", "int"); ("n", "size_t")])
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("nativeint s", result)
        Assert.Contains("extern nativeint memset", result)

    [<Fact>]
    let ``generate produces module header with P/Invoke label`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "NativeBindings.libc" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("module NativeBindings.libc", result)
        Assert.Contains(".NET P/Invoke binding for libc", result)

    [<Fact>]
    let ``generate includes open System.Runtime.InteropServices`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("open System.Runtime.InteropServices", result)

    [<Fact>]
    let ``generate emits struct with StructLayout attribute`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] (Some "A point"))
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "test" PInvokeTypeMapper.LP64
        Assert.Contains("[<Struct>]", result)
        Assert.Contains("[<StructLayout(LayoutKind.Sequential)>]", result)
        Assert.Contains("type Point = {", result)

    [<Fact>]
    let ``generate emits enums same as Fidelity`` () =
        let decls = [
            CppParser.Declaration.Enum (mkEnum "Flags" [mkEnumVal "A" 0L; mkEnumVal "B" 1L] (Some "Test flags"))
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "test" PInvokeTypeMapper.LP64
        Assert.Contains("type Flags =", result)
        Assert.Contains("| A = 0L", result)
        Assert.Contains("| B = 1L", result)

    [<Fact>]
    let ``generate emits macros same as Fidelity`` () =
        let decls = [
            CppParser.Declaration.Macro {
                Name = "EXIT_SUCCESS"; Kind = CppParser.SimpleValue "0"; RawValue = "0"; Documentation = None
            }
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "test" PInvokeTypeMapper.LP64
        Assert.Contains("[<Literal>]", result)
        Assert.Contains("let EXIT_SUCCESS = 0", result)

    [<Fact>]
    let ``generate deduplicates functions by name`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "read" "int" [("fd", "int")])
            CppParser.Declaration.Function (mkFunc "read" "int" [("fd", "int")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "test" PInvokeTypeMapper.LP64
        let occurrences = result.Split("extern int32 read(") |> Array.length
        Assert.Equal(2, occurrences)

    [<Fact>]
    let ``generate includes documentation in extern declarations`` () =
        let decls = [
            CppParser.Declaration.Function (mkFuncDoc "read" "ssize_t"
                [("fd", "int"); ("buf", "void *"); ("count", "size_t")]
                (Some "Read from a file descriptor."))
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("/// Read from a file descriptor.", result)
        Assert.Contains("/// C signature: ssize_t read(int fd, void * buf, size_t count)", result)

    [<Fact>]
    let ``generate handles function pointer params as nativeint`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "atexit" "int" [("func", "void (*)(void)")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "test" PInvokeTypeMapper.LP64
        Assert.Contains("nativeint func", result)

    [<Fact>]
    let ``generate resolves typedefs`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
            CppParser.Declaration.Function (mkFunc "malloc" "void *" [("size", "size_t")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        // size_t resolves via dictionary (unativeint), NOT via typedef chain
        Assert.Contains("unativeint size", result)

    [<Fact>]
    let ``size_t stays platform-abstract despite typedef`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
            CppParser.Declaration.Function (mkFunc "write" "ssize_t" [("fd", "int"); ("count", "size_t")])
        ]
        let fidelityResult = FidelityCodeGenerator.generate decls "Test" "test"
        let pinvokeResult = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        // Both should use unativeint (dictionary-first), not uint64 (typedef-concretized)
        Assert.Contains("unativeint", fidelityResult)
        Assert.DoesNotContain("uint64", fidelityResult)
        Assert.Contains("unativeint", pinvokeResult)
        Assert.DoesNotContain("uint64", pinvokeResult)

    [<Fact>]
    let ``long stays platform-abstract`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "lseek" "long" [("fd", "int"); ("offset", "long")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        // long → nativeint (platform-abstract), not int64 (LP64-baked)
        Assert.Contains("nativeint", result)
        Assert.DoesNotContain("int64", result)

    [<Fact>]
    let ``unknown typedef resolves correctly`` () =
        let decls = [
            CppParser.Declaration.Typedef (mkTypedef "my_custom_t" "int")
            CppParser.Declaration.Function (mkFunc "custom_fn" "void" [("x", "my_custom_t")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        // my_custom_t not in dictionary, so typedef resolves: my_custom_t → int → int32
        Assert.Contains("int32", result)

    [<Fact>]
    let ``Fidelity char pointer maps to nativeptr byte`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "puts" "int" [("s", "const char *")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "test"
        Assert.Contains("nativeptr<byte>", result)

    [<Fact>]
    let ``PInvoke char pointer maps to string`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "puts" "int" [("s", "const char *")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("string s", result)

    // =========================================================================
    // Platform ABI Tests: PInvokeTypeMapper produces correct concrete types
    // per platform, completely separate from TypeMapper (NTU) output
    // =========================================================================

    [<Fact>]
    let ``LP64 long maps to int64`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "lseek" "long" [("fd", "int"); ("offset", "long")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("int64", result)
        Assert.DoesNotContain("nativeint", result)

    [<Fact>]
    let ``LLP64 long maps to int32`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "lseek" "long" [("fd", "int"); ("offset", "long")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LLP64
        Assert.Contains("int32", result)
        Assert.DoesNotContain("int64", result)
        Assert.DoesNotContain("nativeint", result)

    [<Fact>]
    let ``ILP32 long maps to int32`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "lseek" "long" [("fd", "int"); ("offset", "long")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.ILP32
        Assert.Contains("int32", result)
        Assert.DoesNotContain("int64", result)

    [<Fact>]
    let ``IP16 int maps to int16`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.IP16
        Assert.Contains("int16", result)
        Assert.DoesNotContain("int32", result)

    [<Fact>]
    let ``LP64 int maps to int32`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("int32", result)

    [<Fact>]
    let ``LP64 unsigned long maps to uint64`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "fn" "unsigned long" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.Contains("uint64", result)

    [<Fact>]
    let ``LLP64 unsigned long maps to uint32`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "fn" "unsigned long" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LLP64
        Assert.Contains("uint32", result)
        Assert.DoesNotContain("uint64", result)

    [<Fact>]
    let ``fixed-width types same across all platforms`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "fn" "int32_t" [("x", "uint64_t")])
        ]
        let lp64 = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        let llp64 = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LLP64
        let ilp32 = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.ILP32
        let ip16 = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.IP16
        // Fixed-width types identical on all platforms
        Assert.Equal(lp64, llp64)
        Assert.Equal(lp64, ilp32)
        Assert.Equal(lp64, ip16)

    [<Fact>]
    let ``PInvoke output never contains nativeptr`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "puts" "int" [("s", "const char *")])
            CppParser.Declaration.Function (mkFunc "memset" "void *" [("s", "void *"); ("c", "int")])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.DoesNotContain("nativeptr<", result)

    [<Fact>]
    let ``PInvoke output never contains FidelityExtern`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.DoesNotContain("FidelityExtern", result)

    [<Fact>]
    let ``PInvoke output never contains Unchecked.defaultof`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let result = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        Assert.DoesNotContain("Unchecked.defaultof", result)

    [<Fact>]
    let ``Fidelity long stays abstract while PInvoke long is concrete`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "lseek" "long" [("offset", "long")])
        ]
        let fidelity = FidelityCodeGenerator.generate decls "Test" "test"
        let pinvoke = PInvokeCodeGenerator.generate decls "Test" "libc" PInvokeTypeMapper.LP64
        // Fidelity: nativeint (abstract, deferred to NTU)
        Assert.Contains("nativeint", fidelity)
        // P/Invoke LP64: int64 (concrete, matches ABI)
        Assert.Contains("int64", pinvoke)
        Assert.DoesNotContain("nativeint", pinvoke)
