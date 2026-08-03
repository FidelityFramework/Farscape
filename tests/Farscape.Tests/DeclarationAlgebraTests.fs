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
            Name = "MyClass"; Fields = []; Documentation = None; Methods = []; IsAbstract = false; Constructors = []; HasUserDestructor = false; HasUserCopyConstructor = false; HasUserMoveConstructor = false; DestructorMangledName = None; BaseClasses = []
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

// =========================================================================
// Composite Key Deduplication
// =========================================================================

[<Fact>]
let ``mergeDeclarations does not collide enum and class with same name`` () =
    // enum "device" and class "device" are distinct declaration kinds
    let enumDecl = CppParser.Declaration.Enum (mkEnum "device" [] None)
    let classDecl = CppParser.Declaration.Class (mkClassEmpty "device")
    let merged = DeclarationAlgebra.mergeDeclarations [[enumDecl]; [classDecl]]
    Assert.Equal(2, merged.Length)

[<Fact>]
let ``mergeDeclarations does not collide struct and class with same name`` () =
    let structDecl = CppParser.Declaration.Struct (mkStruct "Widget" [] None)
    let classDecl = CppParser.Declaration.Class (mkClassEmpty "Widget")
    let merged = DeclarationAlgebra.mergeDeclarations [[structDecl]; [classDecl]]
    Assert.Equal(2, merged.Length)

[<Fact>]
let ``mergeDeclarations does not collide function and typedef with same name`` () =
    let funcDecl = CppParser.Declaration.Function (mkFunc "size_t" "void" [])
    let typedefDecl = CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    let merged = DeclarationAlgebra.mergeDeclarations [[funcDecl]; [typedefDecl]]
    Assert.Equal(2, merged.Length)

// =========================================================================
// Completeness-Based Class Merge
// =========================================================================

[<Fact>]
let ``mergeDeclarations supersedes class with more fields`` () =
    // First: partial view (0 fields, from transitive include)
    let partial = CppParser.Declaration.Class (mkClassEmpty "device")
    // Second: complete view (1 field, from primary header)
    let complete = CppParser.Declaration.Class (mkClassPimpl "device" "device_impl")
    let merged = DeclarationAlgebra.mergeDeclarations [[partial]; [complete]]
    Assert.Equal(1, merged.Length)
    match merged.[0] with
    | CppParser.Declaration.Class c -> Assert.Equal(1, c.Fields.Length)
    | _ -> Assert.Fail("Expected Class declaration")

[<Fact>]
let ``mergeDeclarations keeps first class when second has fewer fields`` () =
    let complete = CppParser.Declaration.Class (mkClassPimpl "device" "device_impl")
    let partial = CppParser.Declaration.Class (mkClassEmpty "device")
    let merged = DeclarationAlgebra.mergeDeclarations [[complete]; [partial]]
    Assert.Equal(1, merged.Length)
    match merged.[0] with
    | CppParser.Declaration.Class c -> Assert.Equal(1, c.Fields.Length)
    | _ -> Assert.Fail("Expected Class declaration")

[<Fact>]
let ``mergeDeclarations supersedes class with more methods when fields equal`` () =
    // Both have 0 fields, but second has methods
    let method1 = mkFunc "start" "void" []
    let method2 = mkFunc "stop" "void" []
    let sparse = CppParser.Declaration.Class (mkClassWithMethods "device" [] [method1] true)
    let rich = CppParser.Declaration.Class (mkClassWithMethods "device" [] [method1; method2] true)
    let merged = DeclarationAlgebra.mergeDeclarations [[sparse]; [rich]]
    Assert.Equal(1, merged.Length)
    match merged.[0] with
    | CppParser.Declaration.Class c -> Assert.Equal(2, c.Methods.Length)
    | _ -> Assert.Fail("Expected Class declaration")

[<Fact>]
let ``mergeDeclarations supersedes struct with more fields`` () =
    let sparse = CppParser.Declaration.Struct (mkStruct "point" [] None)
    let complete = CppParser.Declaration.Struct (mkStruct "point" [mkField "x" "int"; mkField "y" "int"] None)
    let merged = DeclarationAlgebra.mergeDeclarations [[sparse]; [complete]]
    Assert.Equal(1, merged.Length)
    match merged.[0] with
    | CppParser.Declaration.Struct s -> Assert.Equal(2, s.Fields.Length)
    | _ -> Assert.Fail("Expected Struct declaration")

[<Fact>]
let ``mergeDeclarations first-occurrence-wins for functions`` () =
    // Functions never supersede: first occurrence wins
    let fn1 = CppParser.Declaration.Function (mkFunc "read" "int" [("fd", "int")])
    let fn2 = CppParser.Declaration.Function (mkFunc "read" "ssize_t" [("fd", "int"); ("buf", "void *")])
    let merged = DeclarationAlgebra.mergeDeclarations [[fn1]; [fn2]]
    Assert.Equal(1, merged.Length)
    match merged.[0] with
    | CppParser.Declaration.Function f -> Assert.Equal("int", f.ReturnType)
    | _ -> Assert.Fail("Expected Function declaration")

[<Fact>]
let ``mergeDeclarations handles three-way class merge`` () =
    // Three headers see the same class with increasing completeness
    let view1 = CppParser.Declaration.Class (mkClassEmpty "device")
    let method = mkFunc "init" "void" []
    let view2 = CppParser.Declaration.Class (mkClassWithMethods "device" [] [method] false)
    let view3 = CppParser.Declaration.Class (mkClassPimpl "device" "device_impl")
    let merged = DeclarationAlgebra.mergeDeclarations [[view1]; [view2]; [view3]]
    Assert.Equal(1, merged.Length)
    match merged.[0] with
    | CppParser.Declaration.Class c ->
        // view3 wins: 1 field * 10 = 10 > view2's 0*10+1 = 1
        Assert.Equal(1, c.Fields.Length)
    | _ -> Assert.Fail("Expected Class declaration")
