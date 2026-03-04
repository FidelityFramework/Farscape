module Farscape.Tests.FidelityCodeGeneratorTests

open Xunit
open Farscape.Core
open TestHelpers

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
    let result = FidelityCodeGenerator.generate decls "Fidelity.libc.Test" "libc" Types.LP64 Map.empty
    Assert.Contains("module Fidelity.libc.Test", result)
    Assert.Contains("let getpid () : int32 =", result)
    Assert.Contains("Unchecked.defaultof<int32>", result)

[<Fact>]
let ``generate maps char pointer params to Option<nativeptr<byte>> (nullable by default)`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [("__s", "const char *")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(s: Option<nativeptr<byte>>)", result)

[<Fact>]
let ``generate maps void pointer params to Option<nativeint> (nullable by default)`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "memset" "void *" [("__s", "void *"); ("__c", "int"); ("__n", "size_t")])
        CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(s: Option<nativeint>)", result)
    Assert.Contains(": Option<nativeint> =", result)

[<Fact>]
let ``generate handles function pointer params as nativeint`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "atexit" "int" [("__func", "void (*)(void)")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
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
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(compar: nativeint)", result)

[<Fact>]
let ``generate does not map wchar_t pointer as nativeptr<byte>`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "mbtowc" "int" [("__pwc", "wchar_t *"); ("__s", "const char *"); ("__n", "size_t")])
        CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    // wchar_t * should be Option<nativeint>, not nativeptr<byte>
    Assert.Contains("(pwc: Option<nativeint>)", result)

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
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("[<Literal>]", result)
    Assert.Contains("let EXIT_SUCCESS = 0", result)
    Assert.Contains("let EXIT_FAILURE = 1", result)

[<Fact>]
let ``generate filters compiler builtin macros`` () =
    let decls = [
        CppParser.Declaration.Macro { Name = "__STDC__"; Kind = CppParser.SimpleValue "1"; RawValue = "1"; Documentation = None }
        CppParser.Declaration.Macro { Name = "FOO"; Kind = CppParser.SimpleValue "42"; RawValue = "42"; Documentation = None }
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.DoesNotContain("__STDC__", result)
    Assert.Contains("let FOO = 42", result)

[<Fact>]
let ``generate deduplicates functions by name`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "read" "int" [("__fd", "int")])
        CppParser.Declaration.Function (mkFunc "read" "int" [("__fd", "int")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
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
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("type Flags =", result)
    Assert.Contains("| A = 0L", result)
    Assert.Contains("| B = 1L", result)
    // Only 1 non-zero value — not enough for flags detection
    Assert.DoesNotContain("[<System.Flags>]", result)

[<Fact>]
let ``generate detects bitmask enum and emits Flags attribute`` () =
    let decls = [
        CppParser.Declaration.Enum (mkEnum "hipHostMallocFlags"
            [mkEnumVal "Default" 0L; mkEnumVal "Portable" 1L; mkEnumVal "Mapped" 2L; mkEnumVal "WriteCombined" 4L; mkEnumVal "Coherent" 64L]
            (Some "Host allocation flags"))
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("[<System.Flags>]", result)
    Assert.Contains("type hipHostMallocFlags =", result)

[<Fact>]
let ``generate does not emit Flags for sequential enum`` () =
    let decls = [
        CppParser.Declaration.Enum (mkEnum "hipError_t"
            [mkEnumVal "Success" 0L; mkEnumVal "InvalidValue" 1L; mkEnumVal "MemAlloc" 2L; mkEnumVal "InitError" 3L; mkEnumVal "Deinitialized" 4L; mkEnumVal "Disabled" 5L]
            None)
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.DoesNotContain("[<System.Flags>]", result)
    Assert.Contains("type hipError_t =", result)

[<Fact>]
let ``generate emits struct as record`` () =
    let decls = [
        CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] (Some "A point"))
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("type Point = {", result)
    Assert.Contains("x: int32", result)
    Assert.Contains("y: int32", result)

[<Fact>]
let ``generate emits FidelityExtern attribute on function bindings`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "memcpy" "void *" [("__dest", "void *"); ("__src", "const void *"); ("__n", "size_t")])
        CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    ]
    let result = FidelityCodeGenerator.generate decls "Fidelity.libc.Memory" "libc" Types.LP64 Map.empty
    Assert.Contains("[<FidelityExtern(\"libc\", \"memcpy\")>]", result)
    Assert.Contains("let memcpy", result)

[<Fact>]
let ``generate emits FidelityExtern with correct library name`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "wl_display_connect" "void *" [("__name", "const char *")])
    ]
    let result = FidelityCodeGenerator.generate decls "Fidelity.wayland.Display" "wayland-client" Types.LP64 Map.empty
    Assert.Contains("[<FidelityExtern(\"wayland-client\", \"wl_display_connect\")>]", result)

// ─── Nullable Pointer Architecture Tests ────────────────────────────

[<Fact>]
let ``pointer param without NonNullAttr emits as Option`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains("(buf: Option<nativeint>)", result)

[<Fact>]
let ``pointer param WITH NonNullAttr emits without Option`` () =
    let func = mkFuncWithAttrs "write" "ssize_t"
                [("fd", "int"); ("buf", "const void *"); ("count", "size_t")]
                [mkAttr "NonNullAttr" [1] None]
    let decls = [ CppParser.Declaration.Function func ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    // param index 1 (buf) is non-null via clang attr
    Assert.Contains("(buf: nativeint)", result)

[<Fact>]
let ``mixed nullable and nonnull params in same function`` () =
    let func = mkFuncWithAttrs "memcpy" "void *"
                [("dest", "void *"); ("src", "const void *"); ("n", "size_t")]
                [mkAttr "NonNullAttr" [0; 1] None]
    let decls = [ CppParser.Declaration.Function func ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    // dest (idx 0) and src (idx 1) are nonnull
    Assert.Contains("(dest: nativeint)", result)
    Assert.Contains("(src: nativeint)", result)

[<Fact>]
let ``pointer return type emits as Option by default`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "malloc" "void *" [("size", "size_t")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains(": Option<nativeint> =", result)

[<Fact>]
let ``const char pointer param emits as Option of nativeptr`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "puts" "int" [("s", "const char *")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains("(s: Option<nativeptr<byte>>)", result)

[<Fact>]
let ``function pointer param does NOT get Option wrapping`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "signal" "void (*)(int)" [("sig", "int"); ("handler", "void (*)(int)")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    // function pointer → nativeint, NOT Option<nativeint>
    Assert.Contains("(handler: nativeint)", result)

[<Fact>]
let ``TOML nonnull annotations override nullable default`` () =
    let func = mkFunc "render" "int" [("ctx", "void *"); ("buf", "void *"); ("size", "size_t")]
    let decls = [ CppParser.Declaration.Function func ]
    let nonnull : PilotTypes.NonnullAnnotations option = Some { Parameters = Map.ofList [("render", [0])]; Returns = Set.empty }
    let ctx : FidelityCodeGenerator.GenerationContext =
        { TypedefMap = FidelityCodeGenerator.buildTypedefMap decls
          OpaqueHandles = FidelityCodeGenerator.detectOpaqueHandles decls
          DataModel = Types.LP64
          StructLayouts = Map.empty
          NonnullAnnotations = nonnull }
    let result = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test" "lib" "test" []
    // ctx (idx 0) is nonnull via TOML
    Assert.Contains("(ctx: nativeint)", result)
    // buf (idx 1) is still nullable
    Assert.Contains("(buf: Option<nativeint>)", result)

[<Fact>]
let ``TOML nonnull_returns prevents Option on return type`` () =
    let func = mkFunc "create_thing" "void *" [("size", "size_t")]
    let decls = [ CppParser.Declaration.Function func ]
    let nonnull : PilotTypes.NonnullAnnotations option = Some { Parameters = Map.empty; Returns = Set.ofList ["create_thing"] }
    let ctx : FidelityCodeGenerator.GenerationContext =
        { TypedefMap = FidelityCodeGenerator.buildTypedefMap decls
          OpaqueHandles = FidelityCodeGenerator.detectOpaqueHandles decls
          DataModel = Types.LP64
          StructLayouts = Map.empty
          NonnullAnnotations = nonnull }
    let result = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test" "lib" "test" []
    // Return is nonnull via TOML
    Assert.Contains(": nativeint =", result)
    Assert.DoesNotContain("Option<nativeint> =", result)
