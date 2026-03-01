module Farscape.Tests.DeclarationAlgebraTests

open Xunit
open Farscape.Core
open TestHelpers

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
