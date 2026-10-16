module Farscape.Tests.FidelityCodeGeneratorTests

open Xunit
open Farscape.Core
open TestHelpers

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``normalized nonnull positions mean explicit slots and empty means none in bindings and wrappers`` explicitPointer =
  let positions = if explicitPointer then [1] else []
  let func = mkFuncWithAttrs "inspect" "void" ["count", "int"; "p", "void *"] [mkAttr "NonNullAttr" positions None]
  let declarations = [CppParser.Declaration.Function func]
  let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
  let binding = FidelityCodeGenerator.generateModule ctx Set.empty declarations "Test.Bindings" "test" "test" []
  let wrapper = WrapperCodeGenerator.generateWithContext (Some ctx) false Set.empty declarations "Test.Api" "test" "Test.Bindings" WrapperTypes.NoErrors Types.LP64 None
  let expected = if explicitPointer then "(p: CHandle<unit>)" else "(p: option<CHandle<unit>>)"
  let forbidden = if explicitPointer then "(p: option<CHandle<unit>>)" else "(p: CHandle<unit>)"
  for source in [binding; wrapper] do
    Assert.Contains(expected, source)
    Assert.DoesNotContain(forbidden, source)
    Assert.Contains("(count: int)", source)

[<Theory>]
[<InlineData("void (*)(void)")>]
[<InlineData("Notify")>]
[<InlineData("Notify _Nullable")>]
let ``unannotated callback parameters refuse with a located boundary reason`` cType =
  let declarations = [
    CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(void)")
    CppParser.Declaration.Function (mkFunc "register" "void" ["notify", cType]) ]
  let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate declarations "Test.Native" "test" Types.LP64 Map.empty |> ignore)
  Assert.Contains("register parameter 0", error.Message)
  Assert.Contains("requires explicit nonnull evidence", error.Message)
  Assert.Contains("nullable callback parameters require a supported boundary adapter", error.Message)

[<Theory>]
[<InlineData("header")>]
[<InlineData("type")>]
[<InlineData("annotations")>]
[<InlineData("callbacks")>]
let ``each explicit callback nonnull source preserves its hypothesis provenance`` evidence =
  let callbackType = if evidence = "type" then "Notify _Nonnull" else "Notify"
  let func = mkFuncWithAttrs "register" "void" ["notify", callbackType] (if evidence = "header" then [mkAttr "NonNullAttr" [0] None] else [])
  let declarations = [CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(void)"); CppParser.Declaration.Function func]
  let projection : PilotTypes.BindingProjection =
    { Name = func.Name; Symbol = func.Name; ReferenceParameters = []; ReadOnlyReferenceParameters = []; ReferenceElements = []
      ConstantParameters = []; StringParameters = []; NonnullCallbacks = [0]; ParameterHandles = []; ReturnHandle = None; Ownership = None; ResultRange = None; Resource = None; WritableBuffers = []; CallbackInvocations = [] }
  let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
  let ctx =
    { ctx with
        Bindings = if evidence = "callbacks" then Map.ofList [func.Name, projection] else Map.empty
        NonnullAnnotations = if evidence = "annotations" then Some { Parameters = Map.ofList [func.Name, [0]]; Returns = Set.empty } else None }
  let source = FidelityCodeGenerator.generateModule ctx Set.empty declarations "Test.Native" "test" "test" []
  Assert.Contains("Invoke: FnPtr<unit -> unit>", source)
  Assert.Contains("external execution hypothesis", source)
  Assert.Contains((match evidence with "header" -> "header nonnull attribute" | "type" -> "native _Nonnull type" | "annotations" -> "pilot annotations.nonnull" | _ -> "pilot nonnull_callbacks"), source)
  Assert.Contains("OwnershipTransfer = Undeclared", source)
  if evidence = "callbacks" || evidence = "annotations" then
    let withdrawn = { ctx with Bindings = Map.empty; NonnullAnnotations = None }
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generateModule withdrawn Set.empty declarations "Test.Native" "test" "test" [] |> ignore)
    Assert.Contains("requires explicit nonnull evidence", error.Message)

[<Fact>]
let ``pilot callback nonnull cannot override native nullable evidence`` () =
  let func = mkFunc "register" "void" ["notify", "Notify _Nullable"]
  let declarations = [CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(void)"); CppParser.Declaration.Function func]
  let projection : PilotTypes.BindingProjection =
    { Name = func.Name; Symbol = func.Name; ReferenceParameters = []; ReadOnlyReferenceParameters = []; ReferenceElements = []
      ConstantParameters = []; StringParameters = []; NonnullCallbacks = [0]; ParameterHandles = []; ReturnHandle = None; Ownership = None; ResultRange = None; Resource = None; WritableBuffers = []; CallbackInvocations = [] }
  let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with Bindings = Map.ofList [func.Name, projection] }
  let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generateFunctionDecls ctx "test" func |> ignore)
  Assert.Contains("register parameter 0: nullable native type conflicts with a nonnull contract", error.Message)

[<Theory>]
[<InlineData("int", "int")>]
[<InlineData("void *", "void *")>]
[<InlineData("const char *", "const char *")>]
let ``missing ownership remains undeclared for scalar pointer and text signatures`` returnType parameterType =
  let declarations = [CppParser.Declaration.Function (mkFunc "inspect" returnType ["value", parameterType])]
  let source = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty
  Assert.Contains("OwnershipTransfer = Undeclared", source)
  Assert.Contains("No ownership hypothesis is declared", source)
  Assert.DoesNotContain("OwnershipTransfer = Borrowed", source)
  Assert.DoesNotContain("inferred default", source)

[<Fact>]
let ``explicit ownership hypotheses survive generation and withdrawal removes authority`` () =
  let func = mkFunc "acquire" "void *" ["context", "void *"]
  let ctx = FidelityCodeGenerator.buildGenerationContext [CppParser.Declaration.Function func] Types.LP64 Map.empty
  let projection : PilotTypes.BindingProjection =
    { Name = func.Name; Symbol = "native_acquire"; ReferenceParameters = []; ReadOnlyReferenceParameters = []; ReferenceElements = []
      ConstantParameters = []; StringParameters = []; NonnullCallbacks = []; ParameterHandles = []; ReturnHandle = None; Ownership = None; ResultRange = None; Resource = None; WritableBuffers = []; CallbackInvocations = [] }
  let emit ownership =
    let current = { ctx with Bindings = Map.ofList [func.Name, { projection with Ownership = ownership }] }
    FidelityCodeGenerator.generateFunctionDecls current "test" func |> List.map CodeRenderer.render |> String.concat "\n"
  for transfer in [PilotTypes.CallerOwns; PilotTypes.CalleeOwns; PilotTypes.Borrowed] do
    let source = emit (Some transfer)
    Assert.Contains($"OwnershipTransfer = {transfer}", source)
    Assert.Contains("is declared by the pilot as an external execution hypothesis", source)
    Assert.Contains("CName = \"native_acquire\"", source)
    Assert.DoesNotContain("OwnershipTransfer = Undeclared", source)
  let withdrawn = emit None
  Assert.Contains("OwnershipTransfer = Undeclared", withdrawn)
  Assert.DoesNotContain("OwnershipTransfer = Borrowed", withdrawn)

[<Fact>]
let ``callback descriptor does not invent borrowed ownership from its typedef`` () =
  let declarations = [
    CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(void *)")
    CppParser.Declaration.Function (mkFuncWithAttrs "register" "void" ["notify", "Notify"] [mkAttr "NonNullAttr" [0] None]) ]
  let source = FidelityCodeGenerator.generate declarations "Test.Native" "test" Types.LP64 Map.empty
  Assert.Contains("Expr<CallbackDescriptor>", source)
  Assert.Contains("OwnershipTransfer = Undeclared", source)
  Assert.DoesNotContain("OwnershipTransfer = Borrowed", source)

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
    Assert.Contains("let getpid () : int =", result)
    Assert.Contains("NativeDefault.zeroed ()", result)

[<Fact>]
let ``generate maps char pointer params to option<CHandle<int>> (nullable by default)`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "strlen" "unsigned long" [("__s", "const char *")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(s: option<CHandle<int>>)", result)

[<Fact>]
let ``generate maps void pointer params to option<CHandle<unit>> (nullable by default)`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "memset" "void *" [("__s", "void *"); ("__c", "int"); ("__n", "size_t")])
        CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(s: option<CHandle<unit>>)", result)
    Assert.Contains(": option<CHandle<unit>> =", result)

[<Fact>]
let ``generate handles function pointer params as FnPtr`` () =
    let decls = [
        CppParser.Declaration.Function (mkFuncWithAttrs "atexit" "int" [("__func", "void (*)(void)")] [mkAttr "NonNullAttr" [0] None])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(func: FnPtr<unit -> unit>)", result)

[<Fact>]
let ``generate resolves typedef to function pointer as FnPtr`` () =
    let decls = [
        CppParser.Declaration.Typedef (mkTypedef "__compar_fn_t" "int (*)(const void *, const void *)")
        CppParser.Declaration.Function (mkFuncWithAttrs "qsort" "void" [
            ("__base", "void *"); ("__nmemb", "size_t")
            ("__size", "size_t"); ("__compar", "__compar_fn_t")
        ] [mkAttr "NonNullAttr" [3] None])
        CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(compar: FnPtr<option<CHandle<unit>> -> option<CHandle<unit>> -> int>)", result)
    Assert.Contains("Expr<CallbackDescriptor>", result)
    Assert.Contains("compar.Invoke", result)

[<Fact>]
let ``direct callback typedef declares one entry and preserves native scalar representations`` () =
    let declarations = [
        CppParser.Declaration.Typedef (mkTypedef "Notify" "int (*)(unsigned int, int, void *)")
        CppParser.Declaration.Function (mkFuncWithAttrs "register" "int" ["notify", "Notify"; "context", "void *"] [mkAttr "NonNullAttr" [0] None])
        CppParser.Declaration.Function (mkFuncWithAttrs "replace" "void" ["notify", "Notify"] [mkAttr "NonNullAttr" [0] None]) ]
    let source = FidelityCodeGenerator.generate declarations "Test.Native" "test" Types.LP64 Map.empty
    let name = "Callback_Test_002E_Native_003A__003A_NotifyEntry"
    Assert.Equal(2, source.Split("type " + name + " =").Length)
    Assert.Equal(2, source.Split("let " + name + "Descriptor").Length)
    Assert.Contains("Invoke: FnPtr<int -> int -> option<CHandle<unit>> -> int>", source)
    Assert.Contains("Record = \"" + name + "\"", source)
    Assert.Contains("Type = Integer (Unsigned, 32)", source)
    Assert.Contains("Type = Integer (Signed, 32)", source)
    Assert.Contains("ReturnType = Integer (Signed, 32)", source)
    Assert.Contains("CName = \"Test.Native::Notify\"", source)
    Assert.Contains("let private registerNative (notify: FnPtr<", source)
    Assert.Contains("registerNative notify.Invoke context", source)
    Assert.Contains("replaceNative notify.Invoke", source)

[<Fact>]
let ``callback data pointer arguments and results convert absence like extern results`` () =
    let declarations = [
        CppParser.Declaration.Typedef (mkTypedef "Transform" "void *(*)(void *)")
        CppParser.Declaration.Function (mkFuncWithAttrs "transform" "void *" ["callback", "Transform"] [mkAttr "NonNullAttr" [0] None]) ]
    let source = FidelityCodeGenerator.generate declarations "Test.Native" "test" Types.LP64 Map.empty
    Assert.Contains("Invoke: FnPtr<option<CHandle<unit>> -> option<CHandle<unit>>>", source)
    Assert.Contains(") : option<CHandle<unit>> =", source)
    Assert.Contains("ReturnType = Pointer 64", source)

[<Fact>]
let ``callback record identities distinguish namespace punctuation`` () =
    let declarations = [
        CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(void)")
        CppParser.Declaration.Function (mkFuncWithAttrs "register" "void" ["notify", "Notify"] [mkAttr "NonNullAttr" [0] None]) ]
    let recordName ns =
        let source = FidelityCodeGenerator.generate declarations ns "test" Types.LP64 Map.empty
        source.Split('\n') |> Array.find (fun line -> line.StartsWith "type Callback_")
    Assert.NotEqual<string>(recordName "Test.Native", recordName "Test_Native")

[<Fact>]
let ``generate maps wchar_t pointer as CHandle of int`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "mbtowc" "int" [("__pwc", "wchar_t *"); ("__s", "const char *"); ("__n", "size_t")])
        CppParser.Declaration.Typedef (mkTypedef "size_t" "unsigned long")
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    // wchar_t is an integer at the ABI: CHandle<int>, nullable by default
    Assert.Contains("(pwc: option<CHandle<int>>)", result)

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
let ``enum case names that are F# keywords are backtick-escaped`` () =
    let decls = [
        CppParser.Declaration.Enum (mkEnum "wl_shell_surface_fullscreen_method"
            [mkEnumVal "default" 0L; mkEnumVal "scale" 1L; mkEnumVal "driver" 2L; mkEnumVal "fill" 3L]
            None)
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("| ``default`` = 0L", result)
    Assert.Contains("| scale = 1L", result)

[<Fact>]
let ``enum case names starting with digit get underscore prefix`` () =
    let decls = [
        CppParser.Declaration.Enum (mkEnum "wl_output_transform"
            [mkEnumVal "normal" 0L; mkEnumVal "90" 1L; mkEnumVal "180" 2L; mkEnumVal "270" 3L]
            None)
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.Contains("| normal = 0L", result)
    Assert.Contains("| _90 = 1L", result)
    Assert.Contains("| _180 = 2L", result)
    Assert.Contains("| _270 = 3L", result)

[<Fact>]
let ``generate emits struct as layout module plus StructDescriptor, never a record`` () =
    let decls = [
        CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] (Some "A point"))
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
    Assert.DoesNotContain("type Point", result)
    Assert.Contains("module Point =", result)
    Assert.Contains("let Size = 8", result)
    Assert.Contains("let xOffset = 0", result)
    Assert.Contains("let yOffset = 4", result)
    Assert.Contains("let Descriptor : StructDescriptor =", result)
    Assert.Contains("layout declared", result)

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
let ``pointer param without NonNullAttr emits as option`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "read" "ssize_t" [("fd", "int"); ("buf", "void *"); ("count", "size_t")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains("(buf: option<CHandle<unit>>)", result)

[<Fact>]
let ``pointer param WITH NonNullAttr emits without Option`` () =
    let func = mkFuncWithAttrs "write" "ssize_t"
                [("fd", "int"); ("buf", "const void *"); ("count", "size_t")]
                [mkAttr "NonNullAttr" [1] None]
    let decls = [ CppParser.Declaration.Function func ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    // param index 1 (buf) is non-null via clang attr
    Assert.Contains("(buf: CHandle<unit>)", result)

[<Fact>]
let ``mixed nullable and nonnull params in same function`` () =
    let func = mkFuncWithAttrs "memcpy" "void *"
                [("dest", "void *"); ("src", "const void *"); ("n", "size_t")]
                [mkAttr "NonNullAttr" [0; 1] None]
    let decls = [ CppParser.Declaration.Function func ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    // dest (idx 0) and src (idx 1) are nonnull
    Assert.Contains("(dest: CHandle<unit>)", result)
    Assert.Contains("(src: CHandle<unit>)", result)

[<Fact>]
let ``pointer return type emits as option by default`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "malloc" "void *" [("size", "size_t")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains(": option<CHandle<unit>> =", result)

[<Fact>]
let ``const char pointer param emits as option of CHandle`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "puts" "int" [("s", "const char *")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains("(s: option<CHandle<int>>)", result)

[<Fact>]
let ``function pointer param does NOT get Option wrapping`` () =
    let decls = [
        CppParser.Declaration.Function (mkFuncWithAttrs "register_signal" "void" [("sig", "int"); ("handler", "void (*)(int)")] [mkAttr "NonNullAttr" [1] None])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    // function pointer → FnPtr<sig>, NOT option<FnPtr<sig>>
    Assert.Contains("(handler: FnPtr<int -> unit>)", result)

[<Theory>]
[<InlineData("void (* _Null_unspecified)(int)")>]
[<InlineData("void (* _Nonnull)(int)")>]
[<InlineData("void (* )(int)")>]
let ``qualified callback slots cannot accidentally enter the data pointer conversion`` cType =
    Assert.False(FidelityCodeGenerator.isCDataPointer cType)
    let declarations = [CppParser.Declaration.Function (mkFuncWithAttrs "register" "void" [("handler", cType)] [mkAttr "NonNullAttr" [0] None])]
    let source = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty
    Assert.Contains("(handler: FnPtr<int -> unit>)", source)
    Assert.DoesNotContain("option<FnPtr", source)
    Assert.Contains("Type = Pointer 64", source)

[<Fact>]
let ``TOML nonnull annotations override nullable default`` () =
    let func = mkFunc "render" "int" [("ctx", "void *"); ("buf", "void *"); ("size", "size_t")]
    let decls = [ CppParser.Declaration.Function func ]
    let nonnull : PilotTypes.NonnullAnnotations option = Some { Parameters = Map.ofList [("render", [0])]; Returns = Set.empty }
    let ctx : FidelityCodeGenerator.GenerationContext =
        { TypedefMap = FidelityCodeGenerator.buildTypedefMap decls
          OpaqueHandles = FidelityCodeGenerator.detectOpaqueHandles decls
          Delegates = Map.empty
          Enums = Map.empty
          Structs = Map.empty
          DataModel = Types.LP64
          StructLayouts = Map.empty
          NonnullAnnotations = nonnull
          KnownClasses = Map.empty
          NativePointerSurface = false; Bindings = Map.empty; ValueStructs = Set.empty; PhantomMarkers = Set.empty }
    let result = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test" "lib" "test" []
    // ctx (idx 0) is nonnull via TOML
    Assert.Contains("(ctx: CHandle<unit>)", result)
    // buf (idx 1) is still nullable
    Assert.Contains("(buf: option<CHandle<unit>>)", result)

[<Fact>]
let ``TOML nonnull_returns prevents Option on return type`` () =
    let func = mkFunc "create_thing" "void *" [("size", "size_t")]
    let decls = [ CppParser.Declaration.Function func ]
    let nonnull : PilotTypes.NonnullAnnotations option = Some { Parameters = Map.empty; Returns = Set.ofList ["create_thing"] }
    let ctx : FidelityCodeGenerator.GenerationContext =
        { TypedefMap = FidelityCodeGenerator.buildTypedefMap decls
          OpaqueHandles = FidelityCodeGenerator.detectOpaqueHandles decls
          Delegates = Map.empty
          Enums = Map.empty
          Structs = Map.empty
          DataModel = Types.LP64
          StructLayouts = Map.empty
          NonnullAnnotations = nonnull
          KnownClasses = Map.empty
          NativePointerSurface = false; Bindings = Map.empty; ValueStructs = Set.empty; PhantomMarkers = Set.empty }
    let result = FidelityCodeGenerator.generateModule ctx Set.empty decls "Test" "lib" "test" []
    // Return is nonnull via TOML
    Assert.Contains(": CHandle<unit> =", result)
    Assert.DoesNotContain("option<CHandle<unit>> =", result)

// ─── One-kind signature tests (Dimensional_Range_Design.md, CS-12 rulings) ────
// Every C integer is `int` in the signature; the width is the ABI
// representation the FunctionDescriptor quotation carries, never a Clef type.

[<Fact>]
let ``C int maps to NTU int (dimensional), not int32 (fixed-width)`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "getpriority" "int" [("which", "int"); ("who", "int")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains(": int =", result)
    Assert.DoesNotContain("int32", result)

[<Fact>]
let ``C unsigned int maps to int with unsigned descriptor`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "alarm" "unsigned int" [("seconds", "unsigned int")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "libc" Types.LP64 Map.empty
    Assert.Contains(": int =", result)
    Assert.Contains("(seconds: int)", result)
    Assert.Contains("Integer (Unsigned, 32)", result)
    Assert.DoesNotContain("uint32", result)

[<Fact>]
let ``C int32_t is int in the signature; the descriptor carries Signed 32`` () =
    let decls = [
        CppParser.Declaration.Function (mkFunc "resvg_parse" "int32_t" [("data", "const char *")])
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "resvg" Types.LP64 Map.empty
    Assert.Contains(": int =", result)
    Assert.DoesNotContain(": int32 =", result)
    Assert.Contains("let resvg_parseDescriptor : Expr<FunctionDescriptor> =", result)
    Assert.Contains("ReturnType = Integer (Signed, 32) // width declared: int32_t under LP64", result)
    Assert.Contains("{ Name = \"data\"; Type = Pointer 64; PassBy = Value } // width declared: pointer under LP64", result)

[<Fact>]
let ``C int dimensional type is consistent across all platform ABIs`` () =
    for abi in [ Types.LP64; Types.LLP64; Types.ILP32; Types.IP16 ] do
        let decls = [
            CppParser.Declaration.Function (mkFunc "close" "int" [("fd", "int")])
        ]
        let result = FidelityCodeGenerator.generate decls "Test" "libc" abi Map.empty
        Assert.Contains(": int =", result)
        Assert.DoesNotContain("int32", result)
        Assert.DoesNotContain("int16", result)

[<Fact>]
let ``delegate-typed struct fields are pointers in the descriptor`` () =
    let decls = [
        CppParser.Declaration.Delegate { Name = "wl_display_error_func_t"; Parameters = [("data", "void *"); ("code", "int")]; ReturnType = "void"; Documentation = None }
        CppParser.Declaration.Struct (mkStruct "wl_listener" [mkField "notify" "wl_display_error_func_t"] None)
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty
    Assert.Contains("let notifyOffset = 0", result)
    Assert.Contains("Repr = Repr.Pointer", result)
    Assert.DoesNotContain("delegate of", result)

[<Fact>]
let ``struct fields with C int are I32 at measured-or-declared offsets`` () =
    let decls = [
        CppParser.Declaration.Struct (mkStruct "Rect" [mkField "x" "int"; mkField "y" "int"; mkField "w" "int"; mkField "h" "int"] None)
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty
    Assert.Contains("let xOffset = 0", result)
    Assert.Contains("let hOffset = 12", result)
    Assert.Contains("let Size = 16", result)
    Assert.Contains("Repr = Repr.I32", result)
    Assert.DoesNotContain("int32", result)

[<Fact>]
let ``native value record pointer fields use outer nullability through typedef chains`` () =
    let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-record-" + System.Guid.NewGuid().ToString("N") + ".h")
    try
        System.IO.File.WriteAllText(path, """
typedef struct native_token *Token;
typedef void * _Nonnull RequiredContext;
typedef RequiredContext RequiredAlias;
typedef void *OptionalContext;
struct ValueFields {
    Token nullableToken;
    Token _Nonnull requiredToken;
    RequiredAlias requiredAlias;
    OptionalContext optionalAlias;
    void * _Nullable annotatedOptional;
    void * _Nonnull *nullableCell;
    int count;
};
        """)
        let options : CppParser.HeaderParserOptions =
            { HeaderFile = path; IncludePaths = []; Defines = []; Verbose = false; IncludeMacros = false
              MacroPrefixes = []; IncludeRoot = None; CppMode = false }
        let declarations = CppParser.parseHeader options |> function Ok declarations -> declarations | Error error -> failwith error
        let context = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with ValueStructs = Set.singleton "ValueFields" }
        let source = FidelityCodeGenerator.generateModule context context.OpaqueHandles declarations "Test.Fields" "test" "test" []
        Assert.Contains("nullableToken: option<CHandle<Token>>", source)
        Assert.Contains("requiredToken: CHandle<Token>", source)
        Assert.Contains("requiredAlias: CHandle<unit>", source)
        Assert.Contains("optionalAlias: option<CHandle<unit>>", source)
        Assert.Contains("annotatedOptional: option<CHandle<unit>>", source)
        Assert.Contains("nullableCell: option<CHandle<CHandle<unit>>>", source)
        Assert.Contains("count: int", source)
        Assert.DoesNotContain("nativeint", source)
    finally
        System.IO.File.Delete path

[<Theory>]
[<InlineData("void (*)(int)")>]
[<InlineData("void (*)(void * _Nonnull)")>]
[<InlineData("void (* _Nullable)(int)")>]
let ``native callback record fields require evidence for the entry slot itself`` fieldType =
    let declarations = [CppParser.Declaration.Struct (mkStruct "CallbackFields" [mkField "notify" fieldType] None)]
    let context = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with ValueStructs = Set.singleton "CallbackFields" }
    let error = Assert.ThrowsAny<System.Exception>(fun () ->
        FidelityCodeGenerator.generateModule context Set.empty declarations "Test.Fields" "test" "test" [] |> ignore)
    Assert.Contains("supported boundary adapter", error.Message)

[<Fact>]
let ``nonnull callback field preserves nullable native callback data values`` () =
    let declarations = [
        CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(void * _Nullable)")
        CppParser.Declaration.Typedef (mkTypedef "RequiredNotify" "Notify _Nonnull")
        CppParser.Declaration.Struct (mkStruct "CallbackFields" [mkField "notify" "RequiredNotify"] None) ]
    let context = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with ValueStructs = Set.singleton "CallbackFields" }
    let source = FidelityCodeGenerator.generateModule context Set.empty declarations "Test.Fields" "test" "test" []
    Assert.Contains("notify: FnPtr<option<CHandle<unit>> -> unit>", source)
    Assert.Contains("Repr = Repr.Pointer", source)

[<Fact>]
let ``fieldless struct emits no record type (opaque, handled via typedef path)`` () =
    let decls = [
        CppParser.Declaration.Struct (mkStruct "wl_proxy" [] None)
        CppParser.Declaration.Struct (mkStruct "wl_object" [] None)
        CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)
    ]
    let result = FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty
    // Fieldless structs should be suppressed — they are opaque types
    Assert.DoesNotContain("type wl_proxy", result)
    Assert.DoesNotContain("type wl_object", result)
    // Structs with fields are emitted as layout modules
    Assert.DoesNotContain("module wl_proxy", result)
    Assert.Contains("module Point =", result)

// ─── C++ Class Binding Tests ────────────────────────────────────────

[<Fact>]
let ``rejects unsupported pimpl class struct and destructor`` () =
    let cls = mkClassPimpl "device" "device_impl"
    let cls = { cls with DestructorMangledName = Some "_ZN3xrt6deviceD1Ev" }
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Fidelity.xrt.Core" "xrt_coreutil" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported pimpl class constructor with mangled name`` () =
    let ctor = { mkFunc "device" "void" [("id", "unsigned int")] with MangledName = Some "_ZN3xrt6deviceC1Ej" }
    let cls = mkClassWithCtors "device" [mkField "m_impl" "std::shared_ptr<device_impl>"] [ctor] true
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported pimpl class method with sret when return type is non-trivial`` () =
    let uuidCls = mkClassValue "uuid" [mkField "data" "uint8_t"]
    let method = mkFunc "get_uuid" "uuid" []
    let deviceCls = mkClassWithMethods "device" [mkField "m_impl" "std::shared_ptr<device_impl>"] [method] true
    let decls = [
        CppParser.Declaration.Class deviceCls
        CppParser.Declaration.Class uuidCls
    ]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported abstract classes`` () =
    let cls = mkClassAbstract "IPlugin"
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported opaque classes`` () =
    let cls = mkClassEmpty "ForwardDeclared"
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported POD class as struct`` () =
    let fields = [mkField "x" "float"; mkField "y" "float"; mkField "z" "float"]
    let cls = mkClassPod "Vec3" fields
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported value class with sret annotation`` () =
    let fields = [mkField "data" "uint8_t"]
    let cls = mkClassValue "uuid" fields
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects a C++ class with deprecated methods before unsafe source emission`` () =
    let normalMethod = mkFunc "start" "void" []
    let deprecatedMethod = mkFuncWithAttrs "old_start" "void" [] [mkAttr "DeprecatedAttr" [] None]
    let cls = mkClassWithMethods "device" [mkField "m_impl" "std::shared_ptr<device_impl>"] [normalMethod; deprecatedMethod] true
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``mangled symbols do not admit unsupported C++ method carriers`` () =
    let method = { mkFunc "start" "void" [] with MangledName = Some "_ZN3xrt6device5startEv" }
    let cls = mkClassWithMethods "device" [mkField "m_impl" "std::shared_ptr<device_impl>"] [method] true
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported unique_ptr pimpl with 8-byte size`` () =
    let cls = mkClassPimplUnique "stream" "stream_impl"
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "lib" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported destructor for value class`` () =
    let fields = [mkField "data" "uint8_t"]
    let cls = { mkClassValue "uuid" fields with DestructorMangledName = Some "_ZN3xrt4uuidD1Ev" }
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported pimpl bindings for inherited base-class pimpl`` () =
    let cls = mkClassPimplBase "hw_context" "xrt::detail::pimpl<xrt::hw_context_impl>"
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

[<Fact>]
let ``rejects unsupported pimpl struct for xclbin-like inherited pimpl with no dtor`` () =
    // Simulates xclbin: inherits detail::pimpl<T>, has constructors, no user dtor
    let ctor = mkFunc "xclbin" "void" [("this", "xclbin *")]
    let cls =
        { mkClass "xclbin" [] [] [CppParser.Declaration.Function ctor] false false false false None with
            BaseClasses = ["xrt::detail::pimpl<xrt::xclbin_impl>"] }
    let decls = [CppParser.Declaration.Class cls]
    let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generate decls "Test" "xrt" Types.LP64 Map.empty |> ignore)
    Assert.Contains("supported object storage, ownership and method ABI contract", error.Message)

let private rangeProjection : PilotTypes.BindingProjection =
    { Name = "read"; Symbol = "native_read"; ReferenceParameters = []; ReadOnlyReferenceParameters = []; ReferenceElements = []
      ConstantParameters = []; StringParameters = []; NonnullCallbacks = []; ParameterHandles = []; ReturnHandle = None; Ownership = None; Resource = None; WritableBuffers = []; CallbackInvocations = []
      ResultRange = Some { Identity = "posix.read"; Version = "2024"; Source = "/contracts/read.pilot.toml"
                           Lower = PilotTypes.ResultRangeBound.Constant -1; Upper = PilotTypes.ResultRangeBound.Argument 2 } }
let private renderResultRange projection func =
    let declarations = [CppParser.Declaration.Function func]
    let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    FidelityCodeGenerator.generateModule { ctx with Bindings = Map.ofList ["read", projection] } Set.empty declarations "Test.Native" "test" "test" []
let private readFunction = mkFunc "read" "long" ["fd", "int"; "buffer", "void *"; "count", "unsigned long"]

[<Fact>]
let ``result range sibling binds emitted Clef identity and exact supplied hypothesis`` () =
    let source = renderResultRange rangeProjection readFunction
    Assert.Contains("Binding = \"Test.Native.read\"", source)
    Assert.Contains("CName = \"native_read\"", source)
    Assert.Contains("Expr<ResultRangeDescriptor>", source)
    for text in ["Identity = \"posix.read\""; "Version = \"2024\""; "Source = \"/contracts/read.pilot.toml\""; "Constant (-1)"; "Argument 2"; "Role = ReturnValue"] do Assert.Contains(text, source)
    let absent = renderResultRange { rangeProjection with ResultRange = None } readFunction
    Assert.DoesNotContain("ResultRangeDescriptor", absent)
    Assert.Contains("OwnershipTransfer = Undeclared", source)
    let changed = renderResultRange { rangeProjection with ResultRange = rangeProjection.ResultRange |> Option.map (fun range -> { range with Version = "2025"; Upper = PilotTypes.ResultRangeBound.Constant 4 }) } readFunction
    Assert.Contains("Version = \"2025\"", changed)
    Assert.Contains("Upper = Constant 4", changed)
    Assert.Contains("CName = \"native_read\"", changed)

[<Theory>]
[<InlineData("arity", "outside native arity 3")>]
[<InlineData("pointer", "scalar integer Value parameter")>]
[<InlineData("reference", "scalar integer Value parameter")>]
[<InlineData("float", "scalar integer Value parameter")>]
[<InlineData("result", "scalar integer native result")>]
[<InlineData("constant", "ordinary untranslated native binding")>]
[<InlineData("callback", "ordinary untranslated native binding")>]
[<InlineData("contradiction", "constant bounds are contradictory")>]
[<InlineData("provenance", "requires nonempty source")>]
let ``result range refuses ineligible native slots and unsupported translation`` shape reason =
    let range = rangeProjection.ResultRange.Value
    let projection, func =
        match shape with
        | "arity" -> { rangeProjection with ResultRange = Some { range with Upper = PilotTypes.ResultRangeBound.Argument 3 } }, readFunction
        | "pointer" -> { rangeProjection with ResultRange = Some { range with Upper = PilotTypes.ResultRangeBound.Argument 1 } }, readFunction
        | "reference" -> { rangeProjection with ReferenceParameters = [2] }, { readFunction with Parameters = ["fd", "int"; "buffer", "void *"; "count", "unsigned long *"] }
        | "float" -> rangeProjection, { readFunction with Parameters = ["fd", "int"; "buffer", "void *"; "count", "double"] }
        | "result" -> rangeProjection, { readFunction with ReturnType = "double" }
        | "constant" -> { rangeProjection with ConstantParameters = [""; ""; "4"] }, readFunction
        | "callback" -> rangeProjection, { readFunction with Parameters = ["fd", "int"; "notify", "void (*)(void)"; "count", "unsigned long"]; Attributes = [mkAttr "NonNullAttr" [1] None] }
        | "contradiction" -> { rangeProjection with ResultRange = Some { range with Lower = PilotTypes.ResultRangeBound.Constant 5; Upper = PilotTypes.ResultRangeBound.Constant 4 } }, readFunction
        | _ -> { rangeProjection with ResultRange = Some { range with Source = " " } }, readFunction
    let error = Assert.ThrowsAny<System.Exception>(fun () -> renderResultRange projection func |> ignore)
    Assert.Contains("read result_range", error.Message)
    Assert.Contains(reason, error.Message)

let private invocationContract : PilotTypes.BindingCallbackInvocation =
    { Parameter = 0; Identity = "fixture.call_twice.callback"; Version = "v1"
      Source = "/contracts/call_twice.c"; Scope = "during_call"; ExitBinding = None; SuccessStatus = None }
let private invocationProjection =
    { rangeProjection with Name = "call_twice"; Symbol = "native_call_twice"
                           NonnullCallbacks = [0]; ResultRange = None; CallbackInvocations = [invocationContract] }
let private renderInvocation (projection: PilotTypes.BindingProjection) callbackType signature =
    let parameters = ["callback", callbackType] @ (if projection.ConstantParameters.Length > 1 then ["count", "int"] else [])
    let declarations = [
        CppParser.Declaration.Typedef (mkTypedef "NativeCallback" signature)
        CppParser.Declaration.Function (mkFunc "call_twice" "void" parameters) ]
    let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    FidelityCodeGenerator.generateModule {context with Bindings = Map.ofList [projection.Name, projection]} Set.empty declarations "Test.Native" "test" "test" []

[<Fact>]
let ``callback invocation contract binds exact native receiver scope and complete entry ABI`` () =
    let source = renderInvocation invocationProjection "NativeCallback" "long long (*)(int, unsigned long long)"
    for text in ["Type = Entry \"Test.Native.call_twiceNativeCallback0InvocationContract\""
                 "Expr<CallbackInvocationContract>"; "Expr<ScopedCallbackDescriptor>"
                 "Binding = \"Test.Native.call_twiceNative\""; "Parameter = 0"; "Parameter = \"callback\""
                 "Scope = \"Test.Native.call_twiceNativeCallback0Scope\""
                 "Name = \"arg0\"; Type = Integer (Signed, 32)"
                 "Name = \"arg1\"; Type = Integer (Unsigned, 64)"
                 "ReturnType = Integer (Signed, 64)"; "Invoke: FnPtr<int -> int -> int>"
                 "Identity = \"fixture.call_twice.callback\""; "Version = \"v1\""; "Source = \"/contracts/call_twice.c\""
                 "CName = \"native_call_twice\""; "call_twiceNative callback.Invoke"] do
        Assert.Contains(text, source)
    Assert.DoesNotContain("Binding = \"Test.Native.native_call_twice\"", source)
    let withdrawn = renderInvocation {invocationProjection with CallbackInvocations = []} "NativeCallback" "long long (*)(int, unsigned long long)"
    Assert.DoesNotContain("Expr<CallbackInvocationContract>", withdrawn)
    Assert.DoesNotContain("Type = Entry", withdrawn)
    Assert.Contains("Expr<CallbackDescriptor>", withdrawn)
    let changed = renderInvocation {invocationProjection with CallbackInvocations = [{invocationContract with Version = "v2"}]} "NativeCallback" "long long (*)(int, unsigned long long)"
    Assert.Contains("Version = \"v2\"", changed)

[<Fact>]
let ``optional callback invocation preserves ordinary Option and exact during call contract`` () =
    let projection = {invocationProjection with NonnullCallbacks = []}
    let source = renderInvocation projection "NativeCallback" "long long (*)(int, unsigned long long)"
    for text in ["(callback: option<FnPtr<int -> int -> int>>)"
                 "Type = Entry \"Test.Native.call_twiceCallback0InvocationContract\""
                 "Expr<CallbackInvocationContract>"; "Expr<ScopedCallbackDescriptor>"
                 "Binding = \"Test.Native.call_twice\""; "Parameter = 0"; "Parameter = \"callback\""
                 "Scope = \"Test.Native.call_twiceCallback0Scope\""
                 "Name = \"arg0\"; Type = Integer (Signed, 32)"
                 "Name = \"arg1\"; Type = Integer (Unsigned, 64)"
                 "ReturnType = Integer (Signed, 64)"; "CallingConvention = CDecl"
                 "Identity = \"fixture.call_twice.callback\""; "Version = \"v1\""; "Source = \"/contracts/call_twice.c\""
                 "let call_twice (callback: option<FnPtr<int -> int -> int>>)"; "[<FidelityExtern(\"test\", \"native_call_twice\")>]"
                 "None supplies no callable entry"] do
        Assert.Contains(text, source)
    Assert.Equal(1, source.Split("let call_twice ").Length - 1)
    for forbidden in ["call_twiceNative"; ".Invoke"; "llvm."; "nativeptr"; "voidptr"; "nativeint"; " = null"; "fromSymbol"] do
        Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``optional callback invocation preserves nullable alias with its exact contract`` () =
    let projection = {invocationProjection with NonnullCallbacks = []}
    let source = renderInvocation projection "NativeCallback _Nullable" "int (*)(int)"
    for text in ["(callback: option<FnPtr<int -> int>>)"; "NativeCallback _Nullable callback"
                 "Type = Entry \"Test.Native.call_twiceCallback0InvocationContract\""
                 "Expr<CallbackInvocationContract>"; "Expr<ScopedCallbackDescriptor>"
                 "Binding = \"Test.Native.call_twice\""; "Parameter = 0"; "Parameter = \"callback\""
                 "Scope = \"Test.Native.call_twiceCallback0Scope\""
                 "Name = \"arg0\"; Type = Integer (Signed, 32)"; "ReturnType = Integer (Signed, 32)"
                 "CallingConvention = CDecl"; "Identity = \"fixture.call_twice.callback\""
                 "Version = \"v1\""; "Source = \"/contracts/call_twice.c\""
                 "let call_twice (callback: option<FnPtr<int -> int>>)"; "[<FidelityExtern(\"test\", \"native_call_twice\")>]"] do
        Assert.Contains(text, source)
    Assert.DoesNotContain("call_twiceNative", source)

[<Fact>]
let ``optional zero argument callback emits one public declared native binding`` () =
    let source = renderInvocation {invocationProjection with NonnullCallbacks = []} "NativeCallback" "void (*)(void)"
    for text in ["let call_twice (callback: option<FnPtr<unit -> unit>>)"; "[<FidelityExtern(\"test\", \"native_call_twice\")>]"
                 "let call_twiceDescriptor"; "Type = Entry \"Test.Native.call_twiceCallback0InvocationContract\""
                 "Binding = \"Test.Native.call_twice\""; "Scope = \"Test.Native.call_twiceCallback0Scope\""
                 "[||]"; "ReturnType = Void"; "CallingConvention = CDecl"
                 "Identity = \"fixture.call_twice.callback\""; "Version = \"v1\""; "Source = \"/contracts/call_twice.c\""] do
        Assert.Contains(text, source)
    Assert.Equal(1, source.Split("let call_twice ").Length - 1)
    let contract = source.Substring(source.IndexOf("Expr<CallbackInvocationContract>"))
    let signature = contract.Substring(0, contract.IndexOf("Hypothesis ="))
    Assert.DoesNotContain("Name =", signature)
    for forbidden in ["call_twiceNative"; ".Invoke"; "llvm."; "nativeptr"; " = null"] do Assert.DoesNotContain(forbidden, source)

[<Fact>]
let ``mixed optional and nonnull callbacks preserve the required adapter`` () =
    let declarations = [CppParser.Declaration.Typedef(mkTypedef "NativeCallback" "int (*)(int)")
                        CppParser.Declaration.Function(mkFunc "call_twice" "void" ["optional", "NativeCallback"; "required", "NativeCallback"])]
    let projection =
        { invocationProjection with
            NonnullCallbacks = [1]
            CallbackInvocations = [invocationContract; {invocationContract with Parameter=1; Identity="fixture.call_twice.required"}] }
    let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    let source = FidelityCodeGenerator.generateModule {context with Bindings=Map.ofList [projection.Name,projection]} Set.empty declarations "Test.Native" "test" "test" []
    Assert.Contains("let private call_twiceNative (optional: option<FnPtr<int -> int>>)", source)
    Assert.Contains("call_twiceNative optional required.Invoke", source)
    Assert.DoesNotContain("optional.Invoke", source)
    Assert.Contains("Binding = \"Test.Native.call_twiceNative\"", source)
    Assert.Contains("Type = Entry \"Test.Native.call_twiceNativeCallback0InvocationContract\"", source)

[<Theory>]
[<InlineData("missing", "requires explicit nonnull evidence")>]
[<InlineData("wrong-ordinal", "requires explicit nonnull evidence")>]
[<InlineData("duplicate", "duplicate parameter indices")>]
[<InlineData("retained", "nullable callback parameters require only during_call invocation contracts")>]
[<InlineData("process-exit", "nullable callback parameters require only during_call invocation contracts")>]
[<InlineData("mixed", "nullable callback parameters require only during_call invocation contracts")>]
[<InlineData("profile", "unsupported native entry signature type 'void *'")>]
[<InlineData("source", "requires nonempty source")>]
[<InlineData("exit-fields", "during_call forbids exit_binding and success_status")>]
[<InlineData("constant", "requires an untranslated native parameter list")>]
let ``optional callback invocation refuses missing ambiguous retained and unsupported contracts`` shape reason =
    let projection = {invocationProjection with NonnullCallbacks = []}
    let projection, signature =
        match shape with
        | "missing" -> {projection with CallbackInvocations = []}, "int (*)(int)"
        | "wrong-ordinal" -> {projection with CallbackInvocations = [{invocationContract with Parameter = 1}]}, "int (*)(int)"
        | "duplicate" -> {projection with CallbackInvocations = [invocationContract; invocationContract]}, "int (*)(int)"
        | "retained" -> {projection with CallbackInvocations = [{invocationContract with Scope = "retained"}]}, "int (*)(int)"
        | "process-exit" -> {projection with CallbackInvocations = [{invocationContract with Scope = "process_exit"; SuccessStatus = Some 0}]}, "void (*)(void)"
        | "mixed" -> {projection with CallbackInvocations = [invocationContract; {invocationContract with Parameter = 1; Scope = "process_exit"; SuccessStatus = Some 0}]}, "int (*)(int)"
        | "profile" -> projection, "int (*)(void *)"
        | "source" -> {projection with CallbackInvocations = [{invocationContract with Source = " "}]}, "int (*)(int)"
        | "constant" -> {projection with ConstantParameters=[""; "3"]}, "int (*)(int)"
        | _ -> {projection with CallbackInvocations = [{invocationContract with ExitBinding = Some "Test.Native.exit"}]}, "int (*)(int)"
    let error = Assert.ThrowsAny<System.Exception>(fun () -> renderInvocation projection "NativeCallback" signature |> ignore)
    Assert.Contains("call_twice", error.Message)
    Assert.Contains(reason, error.Message)

[<Theory>]
[<InlineData("int (*)(int)", "Signed, 32", "Signed, 32")>]
[<InlineData("unsigned long long (*)(unsigned int)", "Unsigned, 32", "Unsigned, 64")>]
[<InlineData("unsigned int (*)(long long)", "Signed, 64", "Unsigned, 32")>]
[<InlineData("long long (*)(unsigned long long)", "Unsigned, 64", "Signed, 64")>]
let ``callback invocation admitted integer profile preserves declared widths and signedness`` signature parameter result =
    let source = renderInvocation invocationProjection "NativeCallback" signature
    Assert.Contains($"Name = \"arg0\"; Type = Integer ({parameter})", source)
    Assert.Contains($"ReturnType = Integer ({result})", source)
    Assert.Contains("Invoke: FnPtr<int -> int>", source)
    Assert.DoesNotContain("Name = \"arg1\"", source)

[<Theory>]
[<InlineData("long long (*)(int, unsigned long long)", "Signed, 32", "Unsigned, 64", "Signed, 64")>]
[<InlineData("unsigned int (*)(unsigned long long, int)", "Unsigned, 64", "Signed, 32", "Unsigned, 32")>]
[<InlineData("unsigned long long (*)(long long, unsigned int)", "Signed, 64", "Unsigned, 32", "Unsigned, 64")>]
[<InlineData("int (*)(unsigned int, long long)", "Unsigned, 32", "Signed, 64", "Signed, 32")>]
let ``callback invocation ordered integer formals preserve every native position`` signature first second result =
    let source = renderInvocation invocationProjection "NativeCallback" signature
    let contract = source.Substring(source.IndexOf("Expr<CallbackInvocationContract>"))
    let parameters = contract.Substring(0, contract.IndexOf("ReturnType ="))
    let firstField = $"Name = \"arg0\"; Type = Integer ({first})"
    let secondField = $"Name = \"arg1\"; Type = Integer ({second})"
    Assert.Contains(firstField, parameters)
    Assert.Contains(secondField, parameters)
    Assert.True(parameters.IndexOf(firstField) < parameters.IndexOf(secondField), "Native callback formal order changed")
    Assert.Equal(2, parameters.Split("Name = ").Length - 1)
    Assert.Contains($"ReturnType = Integer ({result})", contract)
    Assert.Contains("Invoke: FnPtr<int -> int -> int>", source)

[<Theory>]
[<InlineData("void (*)(int)", "Signed, 32", "", 1)>]
[<InlineData("void (*)(unsigned long long)", "Unsigned, 64", "", 1)>]
[<InlineData("void (*)(int, unsigned long long)", "Signed, 32", "Unsigned, 64", 2)>]
[<InlineData("void (*)(unsigned int, long long)", "Unsigned, 32", "Signed, 64", 2)>]
let ``callback invocation void result preserves complete ordered integer formals`` signature first second arity =
    let source = renderInvocation invocationProjection "NativeCallback" signature
    let contract = source.Substring(source.IndexOf("Expr<CallbackInvocationContract>"))
    let signatureText = contract.Substring(0, contract.IndexOf("Hypothesis ="))
    Assert.Contains($"Name = \"arg0\"; Type = Integer ({first})", signatureText)
    if arity = 2 then
        let secondField = $"Name = \"arg1\"; Type = Integer ({second})"
        Assert.Contains(secondField, signatureText)
        Assert.True(signatureText.IndexOf("Name = \"arg0\"") < signatureText.IndexOf(secondField))
    Assert.Equal(arity, signatureText.Split("Name = ").Length - 1)
    Assert.Contains("ReturnType = Void", signatureText)
    Assert.DoesNotContain("ReturnType = Integer", signatureText)
    Assert.Contains((if arity = 1 then "Invoke: FnPtr<int -> unit>" else "Invoke: FnPtr<int -> int -> unit>"), source)
    for text in ["CallingConvention = CDecl"; "Parameter = 0"; "Scope = \"Test.Native.call_twiceNativeCallback0Scope\""; "Identity = \"fixture.call_twice.callback\""] do
        Assert.Contains(text, contract)

[<Fact>]
let ``callback invocation explicit void prototype has logical unit and no native formals`` () =
    let source = renderInvocation invocationProjection "NativeCallback" "void (*)(void)"
    let contract = source.Substring(source.IndexOf("Expr<CallbackInvocationContract>"))
    let signatureText = contract.Substring(0, contract.IndexOf("Hypothesis ="))
    Assert.Contains("Invoke: FnPtr<unit -> unit>", source)
    Assert.Contains("[||]", signatureText)
    Assert.DoesNotContain("Name =", signatureText)
    Assert.Contains("ReturnType = Void", signatureText)
    for text in ["CallingConvention = CDecl"; "Parameter = 0"; "Scope = \"Test.Native.call_twiceNativeCallback0Scope\""; "Identity = \"fixture.call_twice.callback\""] do
        Assert.Contains(text, contract)

[<Theory>]
[<InlineData("bool-parameter", "int (*)(bool)", "Boolean native entry signatures")>]
[<InlineData("bool-result", "bool (*)(int)", "Boolean native entry signatures")>]
[<InlineData("zero", "int (*)(void)", "zero-argument native entries require a void result")>]
[<InlineData("ambiguous-zero", "void (*)()", "zero-argument native entries require an explicit (void) prototype")>]
let ``callback invocation rejects signatures outside admitted integer profile`` _shape signature reason =
    let error = Assert.ThrowsAny<System.Exception>(fun () -> renderInvocation invocationProjection "NativeCallback" signature |> ignore)
    Assert.Contains("call_twice callback_invocations", error.Message)
    Assert.Contains(reason, error.Message)

[<Theory>]
[<InlineData("index", "outside native arity 1")>]
[<InlineData("duplicate", "duplicate parameter indices")>]
[<InlineData("scope", "scope must be during_call or process_exit")>]
[<InlineData("source", "requires nonempty source")>]
[<InlineData("kind", "not a native function pointer")>]
[<InlineData("translated", "untranslated native parameter list")>]
[<InlineData("signature", "unsupported native entry signature type")>]
[<InlineData("reference", "unsupported native entry reference signature type 'int &'")>]
[<InlineData("return-reference", "unsupported native entry reference signature type 'int &'")>]
[<InlineData("nullable", "nullable native type conflicts with a nonnull contract")>]
let ``callback invocation contract refuses unsupported receiving correspondence precisely`` shape reason =
    let projection, callbackType, signature =
        match shape with
        | "index" -> {invocationProjection with CallbackInvocations = [{invocationContract with Parameter = 1}]}, "NativeCallback", "int (*)(int)"
        | "duplicate" -> {invocationProjection with CallbackInvocations = [invocationContract; invocationContract]}, "NativeCallback", "int (*)(int)"
        | "scope" -> {invocationProjection with CallbackInvocations = [{invocationContract with Scope = "retained"}]}, "NativeCallback", "int (*)(int)"
        | "source" -> {invocationProjection with CallbackInvocations = [{invocationContract with Source = " "}]}, "NativeCallback", "int (*)(int)"
        | "kind" -> {invocationProjection with NonnullCallbacks = []}, "int", "int (*)(int)"
        | "translated" -> {invocationProjection with ConstantParameters = [""; "4"]}, "NativeCallback", "int (*)(int)"
        | "signature" -> invocationProjection, "NativeCallback", "int (*)(void *)"
        | "reference" -> invocationProjection, "NativeCallback", "int (*)(int &)"
        | "return-reference" -> invocationProjection, "NativeCallback", "int & (*)(int)"
        | "nullable" -> invocationProjection, "NativeCallback _Nullable", "int (*)(int)"
        | _ -> {invocationProjection with NonnullCallbacks = []}, "NativeCallback", "int (*)(int)"
    let error = Assert.ThrowsAny<System.Exception>(fun () -> renderInvocation projection callbackType signature |> ignore)
    Assert.Contains(reason, error.Message)

[<Theory>]
[<InlineData("unselected", "requires exactly one selected native declaration")>]
[<InlineData("duplicate", "duplicate binding projections")>]
[<InlineData("signature", "unsupported native entry signature type")>]
[<InlineData("kind", "not a native function pointer")>]
[<InlineData("profile-bool-parameter", "Boolean native entry signatures")>]
[<InlineData("profile-bool-result", "Boolean native entry signatures")>]
[<InlineData("profile-zero", "zero-argument native entries require a void result")>]
[<InlineData("profile-ambiguous-zero", "zero-argument native entries require an explicit (void) prototype")>]
[<InlineData("profile-process-anchor", "exit_binding must name a selected qualified generated declaration")>]
[<InlineData("profile-process-status", "process_exit requires signed32 success_status")>]
[<InlineData("profile-process-mixed", "during_call forbids")>]
let ``callback invocation project refusal preserves every existing output`` shape reason =
    let dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "callback-receiving-tests", System.Guid.NewGuid().ToString("N")))
    System.IO.Directory.CreateDirectory(dir) |> ignore
    try
        let header = System.IO.Path.Combine(dir, "entry.h")
        let declaration =
            match shape with
            | "signature" -> "void call_twice(int (*callback)(void *));"
            | "kind" -> "void call_twice(int callback);"
            | "profile-bool-parameter" -> "void call_twice(int (*callback)(_Bool));"
            | "profile-bool-result" -> "void call_twice(_Bool (*callback)(int));"
            | "profile-zero" -> "void call_twice(int (*callback)(void));"
            | "profile-ambiguous-zero" -> "void call_twice(void (*callback)());"
            | "profile-process-anchor" | "profile-process-status" | "profile-process-mixed" -> "int call_twice(void (*callback)(void)); void exit(int status);"
            | _ -> "void call_twice(long long (*callback)(long long));"
        System.IO.File.WriteAllText(header, declaration)
        let output = System.IO.Path.Combine(dir, "output")
        System.IO.Directory.CreateDirectory(output) |> ignore
        let sentinel = System.IO.Path.Combine(output, "original.clef")
        System.IO.File.WriteAllText(sentinel, "held-original")
        let nonnull = if shape = "kind" then "" else "nonnull_callbacks = [0]\n"
        let scope = if shape = "profile-process-anchor" || shape = "profile-process-status" then "process_exit" else "during_call"
        let extra = match shape with
                    | "profile-process-anchor" -> ", success_status = 0, exit_binding = \"Test.Native.exit\""
                    | "profile-process-mixed" -> ", success_status = 0"
                    | _ -> ""
        let projection = "\n[[options.bindings]]\nname = \"call_twice\"\n" + nonnull + "callback_invocations = [{ parameter = 0, identity = \"fixture.callback\", version = \"v1\", source = \"fixture.c\", scope = \"" + scope + "\"" + extra + " }]\n"
        let pilot = System.String.Join("\n", [
            "[library]"; "name = \"call_twice\""; "header = \"" + header + "\""
            "[output]"; "mode = \"fidelity\""; "directory = \"" + output + "\""
            "[options]"; "c_header_mode = true"; "descriptor_only_dependencies = true"
            projection; (if shape = "duplicate" then projection else "")
            "[[namespace]]"; "name = \"Test.Native\""; "description = \"receiving contract sentinel\""; "library = \"call_twice\""
            "functions = [\"" + (if shape = "unselected" then "missing" else "call_twice") + "\"]" ])
        let project = System.IO.Path.Combine(dir, "entry.pilot.toml")
        System.IO.File.WriteAllText(project, pilot)
        match BindingGenerator.generateFromProject project false false Types.LP64 with
        | Error error -> Assert.Contains("callback_invocations", error); Assert.Contains(reason, error)
        | Ok _ -> failwith "Unsupported receiving contract generated output"
        Assert.Equal("held-original", System.IO.File.ReadAllText sentinel)
        Assert.Equal<string array>([|sentinel|], System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))
    finally
        System.IO.Directory.Delete(dir, true)

let private renderProcessExit anchor status receiverResult signature =
    let contract = {invocationContract with Scope = "process_exit"; ExitBinding = anchor; SuccessStatus = status}
    let receiver = {invocationProjection with Name = "atexit"; Symbol = "native_atexit"; CallbackInvocations = [contract]}
    let exit = {rangeProjection with Name = "exit"; Symbol = "native_exit"; ResultRange = None; ConstantParameters = ["0"]}
    let declarations = [
        CppParser.Declaration.Typedef (mkTypedef "ExitCallback" signature)
        CppParser.Declaration.Function (mkFunc "atexit" receiverResult ["func", "ExitCallback"])
        CppParser.Declaration.Function (mkFunc "exit" "void" ["status", "int"]) ]
    let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    FidelityCodeGenerator.generateModule {context with Bindings = Map.ofList ["atexit", receiver; "exit", exit]} Set.empty declarations "Test.Process" "c" "process exit fixture" []

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``process exit invocation emits optional exact generated native anchor`` anchored =
    let source = renderProcessExit (if anchored then Some "Test.Process.exit" else None) (Some 0) "int" "void (*)(void)"
    for text in ["Expr<ProcessExitCallbackDescriptor>"; "Binding = \"Test.Process.atexitNative\""; "Parameter = \"func\""; "SuccessStatus = 0"; "Invoke: FnPtr<unit -> unit>"; "Identity = \"fixture.call_twice.callback\""; "ReturnType = Integer (Signed, 32)"] do
        Assert.Contains(text, source)
    Assert.Contains((if anchored then "ExitBinding = Some \"Test.Process.exitNative\"" else "ExitBinding = None"), source)
    Assert.DoesNotContain("Expr<ScopedCallbackDescriptor>", source)
    Assert.DoesNotContain("Expr<ResultRangeDescriptor>", source)
    Assert.Contains("CName = \"native_exit\"", source)

[<Theory>]
[<InlineData("anchor", "exit_binding must name a selected qualified generated declaration")>]
[<InlineData("symbol", "exit_binding must name a selected qualified generated declaration")>]
[<InlineData("status", "process_exit requires signed32 success_status")>]
[<InlineData("result", "signed32 registration result")>]
[<InlineData("arity", "zero-argument void callback")>]
let ``process exit invocation refuses unsupported registration correspondence`` shape reason =
    let anchor = match shape with "anchor" -> Some "Other.Process.exit" | "symbol" -> Some "native_exit" | _ -> None
    let status = if shape = "status" then None else Some 0
    let receiver = if shape = "result" then "unsigned int" else "int"
    let signature = if shape = "arity" then "void (*)(int)" else "void (*)(void)"
    let error = Assert.ThrowsAny<System.Exception>(fun () -> renderProcessExit anchor status receiver signature |> ignore)
    Assert.Contains(reason, error.Message)

let private ownedResource : PilotTypes.BindingResource =
    { ReleaseBinding = "Test.Storage.freeFirst"; ReleaseParameter = 0
      Identity = "first-owned"; Version = "1"; Source = "owned-resource.pilot.toml" }

let private ownedResourceSource resource releaseType releaseParameterType releaseNonnull second profile =
    let acquire name = mkFunc name "void *" ["size", "unsigned long"]
    let release name = mkFunc name releaseType ["ptr", releaseParameterType]
    let acquisition name contract =
        { rangeProjection with Name = name; Symbol = "malloc"; ReturnHandle = Some "unit"; ResultRange = None; Resource = contract }
    let retirement name =
        { rangeProjection with Name = name; Symbol = "free"
                               ParameterHandles = (if releaseParameterType = "int" then [] else [if profile = "marker" then "other" else "unit"])
                               ConstantParameters = (if profile = "translated" then ["0"] else [])
                               ResultRange = None }
    let declarations =
        [ CppParser.Declaration.Function (acquire "mallocFirst")
          CppParser.Declaration.Function (release "freeFirst") ]
        @ (if second then [CppParser.Declaration.Function (acquire "mallocSecond"); CppParser.Declaration.Function (release "freeSecond")] else [])
    let bindings =
        [ "mallocFirst", acquisition "mallocFirst" resource; "freeFirst", retirement "freeFirst" ]
        @ (if second then ["mallocSecond", acquisition "mallocSecond" (Some {ownedResource with ReleaseBinding = "Test.Storage.freeSecond"; Identity = "second-owned"}); "freeSecond", retirement "freeSecond"] else [])
    let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    let context =
        { context with Bindings = Map.ofList bindings
                       NonnullAnnotations = Some {Parameters = (if releaseNonnull then Map.ofList ["freeFirst", [0]; "freeSecond", [0]] else Map.empty)
                                                  Returns = (if profile = "nonnull" then Set.singleton "mallocFirst" else Set.empty)} }
    FidelityCodeGenerator.generateModule context Set.empty declarations "Test.Storage" "c" "owned nullable fixture" []

[<Fact>]
let ``optional callback with owned nullable resource retains its exact native adapter`` () =
    let declarations =
        [ CppParser.Declaration.Typedef(mkTypedef "NativeCallback" "int (*)(int)")
          CppParser.Declaration.Function(mkFunc "call_twice" "void *" ["callback", "NativeCallback"])
          CppParser.Declaration.Function(mkFunc "freeFirst" "void" ["ptr", "void *"]) ]
    let projection =
        { invocationProjection with
            NonnullCallbacks = []; ReturnHandle = Some "unit"
            Resource = Some {ownedResource with ReleaseBinding = "Test.Native.freeFirst"} }
    let release =
        { rangeProjection with Name = "freeFirst"; Symbol = "free"; ResultRange = None
                               ParameterHandles = ["unit"] }
    let context = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
    let context =
        { context with
            Bindings = Map.ofList [projection.Name, projection; release.Name, release]
            NonnullAnnotations = Some {Parameters = Map.ofList ["freeFirst", [0]]; Returns = Set.empty} }
    let source = FidelityCodeGenerator.generateModule context Set.empty declarations "Test.Native" "test" "test" []
    for text in ["let private call_twiceNative (callback: option<FnPtr<int -> int>>)"
                 "let call_twice (callback: option<FnPtr<int -> int>>)"; "call_twiceNative callback"
                 "let call_twiceNativeResourceDescriptor"; "OwnedNullableReturn"
                 "Binding = \"Test.Native.freeFirst\""; "Binding = \"Test.Native.call_twiceNative\""
                 "Type = Entry \"Test.Native.call_twiceNativeCallback0InvocationContract\""
                 "Scope = \"Test.Native.call_twiceNativeCallback0Scope\""] do Assert.Contains(text, source)
    Assert.Equal(1, source.Split("let call_twice ").Length - 1)
    Assert.DoesNotContain("callback.Invoke", source)

[<Fact>]
let ``owned nullable resource companions distinguish identical native symbols and handle types`` () =
    let source = ownedResourceSource (Some ownedResource) "void" "void *" true true "normal"
    for acquisition, release, identity in ["mallocFirst", "freeFirst", "first-owned"; "mallocSecond", "freeSecond", "second-owned"] do
        let start = source.IndexOf("let " + acquisition + "ResourceDescriptor", System.StringComparison.Ordinal)
        Assert.True(start >= 0, source)
        let finish = source.IndexOf("@>", start, System.StringComparison.Ordinal)
        let companion = source.Substring(start, finish - start)
        Assert.Contains("Expr<ResourceDescriptor>", companion)
        Assert.Contains("OwnedNullableReturn", companion)
        Assert.Contains("Binding = \"Test.Storage." + release + "\"", companion)
        Assert.Contains("Parameter = 0", companion)
        Assert.Contains("Identity = \"" + identity + "\"", companion)
        Assert.DoesNotContain("CName", companion)
        Assert.DoesNotContain("Binding = \"Test.Storage." + acquisition + "\"", companion)
        Assert.Contains("let " + acquisition + " (size: int) : option<CHandle<unit>>", source)
        Assert.Contains("let " + release + " (ptr: CHandle<unit>) : unit", source)
    Assert.Contains("OwnershipTransfer = Undeclared", source)

[<Fact>]
let ``owned nullable resource absence emits no inferred companion`` () =
    let source = ownedResourceSource None "void" "void *" true false "normal"
    Assert.DoesNotContain("Expr<ResourceDescriptor>", source)
    Assert.Contains("OwnershipTransfer = Undeclared", source)

[<Theory>]
[<InlineData("unselected", "exact selected qualified generated declaration")>]
[<InlineData("symbol", "exact selected qualified generated declaration")>]
[<InlineData("ordinal", "requires release_parameter 0")>]
[<InlineData("result", "raw Void result")>]
[<InlineData("parameter", "unary matching CHandle")>]
[<InlineData("nullable", "unary matching CHandle")>]
[<InlineData("provenance", "requires nonempty source")>]
let ``owned nullable resource generation refuses unsupported release contracts`` shape reason =
    let resource =
        match shape with
        | "unselected" -> {ownedResource with ReleaseBinding = "Test.Storage.missing"}
        | "symbol" -> {ownedResource with ReleaseBinding = "free"}
        | "ordinal" -> {ownedResource with ReleaseParameter = 1}
        | "provenance" -> {ownedResource with Source = " "}
        | _ -> ownedResource
    let result = if shape = "result" then "int" else "void"
    let parameter = if shape = "parameter" then "int" else "void *"
    let error = Assert.ThrowsAny<System.Exception>(fun () -> ownedResourceSource (Some resource) result parameter (shape <> "nullable") false "normal" |> ignore)
    Assert.Contains("resource", error.Message)
    Assert.Contains(reason, error.Message)

[<Theory>]
[<InlineData("nonnull", "owned_nullable_return requires a native data pointer returned as option<CHandle<marker>>")>]
[<InlineData("marker", "release requires unary matching CHandle Value parameter 0 and a raw Void result")>]
[<InlineData("translated", "freeFirst parameter 0: constant projection requires an integer literal and scalar integer parameter")>]
let ``owned nullable resource generation refuses nonoptional marker and translated contracts`` profile reason =
    let error = Assert.ThrowsAny<System.Exception>(fun () ->
        ownedResourceSource (Some ownedResource) "void" "void *" true false profile |> ignore)
    if profile = "translated" then
        Assert.Equal(reason, error.Message)
    else
        Assert.Contains("resource", error.Message)
        Assert.Contains(reason, error.Message)

let private writableContract : PilotTypes.BindingWritableBuffer =
    { Parameter = 1; CountParameter = 2; Identity = "posix.read.borrow"; Version = "2024"; Source = "read-contract.pilot.toml" }
let private writableProjection =
    { rangeProjection with ReferenceParameters = [1]; ReferenceElements = [""; "unsigned char"]
                           WritableBuffers = [writableContract] }

[<Fact>]
let ``writable buffer quotation preserves exact declaration byte element and independent result promise`` () =
    let source = renderResultRange writableProjection readFunction
    for text in ["let read (fd: int) (buffer: int array) (count: int) : int"
                 "Expr<WritableBufferDescriptor>"; "Binding = \"Test.Native.read\""; "CName = \"native_read\""
                 "Parameter = 1"; "CountParameter = 2"; "Type = Integer (Unsigned, 8); PassBy = Reference"
                 "Identity = \"posix.read.borrow\""; "Version = \"2024\""; "Source = \"read-contract.pilot.toml\""
                 "Expr<ResultRangeDescriptor>"; "Upper = Argument 2"; "OwnershipTransfer = Undeclared"] do
        Assert.Contains(text, source)
    Assert.DoesNotContain("Binding = \"Test.Native.native_read\"", source)
    let absent = renderResultRange {writableProjection with WritableBuffers = []} readFunction
    Assert.DoesNotContain("Expr<WritableBufferDescriptor>", absent)
    Assert.Contains("Expr<ResultRangeDescriptor>", absent)
    let noResult = renderResultRange {writableProjection with ResultRange = None} readFunction
    Assert.Contains("Expr<WritableBufferDescriptor>", noResult)
    Assert.DoesNotContain("Expr<ResultRangeDescriptor>", noResult)
    let changed = renderResultRange {writableProjection with WritableBuffers = [{writableContract with Version = "2025"}]} readFunction
    Assert.Contains("Version = \"2025\"", changed)

let private writableProject shape =
    let dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "writable-buffer-tests", System.Guid.NewGuid().ToString("N")))
    System.IO.Directory.CreateDirectory(dir) |> ignore
    try
        let header = System.IO.Path.Combine(dir, "read.h")
        let bufferType = match shape with "const" -> "const void *" | "missing-element" -> "unsigned char *" | _ -> "void *"
        let countType = match shape with "count-reference" -> "unsigned long *" | "count-float" -> "double" | _ -> "unsigned long"
        System.IO.File.WriteAllText(header, $"long native_read(int fd, {bufferType} buffer, {countType} count);")
        let output = System.IO.Path.Combine(dir, "output")
        System.IO.Directory.CreateDirectory(output) |> ignore
        let sentinel = System.IO.Path.Combine(output, "original.clef")
        System.IO.File.WriteAllText(sentinel, "held-original")
        let parameter = if shape = "buffer-ordinal" then 3 else 1
        let count = match shape with "count-ordinal" -> 3 | "same" -> 1 | _ -> 2
        let hypothesis = if shape = "hypothesis" then "" else ", hypothesis = { identity = \"posix.read.borrow\", version = \"2024\", source = \"read-contract.pilot.toml\" }"
        let contract = $"{{ parameter = {parameter}, count_parameter = {count}{hypothesis}" + (if shape = "unknown" then ", retention = \"forever\"" else "") + " }"
        let contracts = if shape = "duplicate" then contract + ", " + contract else contract
        let reference = match shape with "not-reference" | "readonly" -> "[]" | "count-reference" -> "[1,2]" | _ -> "[1]"
        let element = match shape with "wide" -> "int" | "signed" -> "signed char" | _ -> "unsigned char"
        let projection = System.String.Join("\n", [
            "[[options.bindings]]"; "name = \"read\""; "symbol = \"native_read\""
            "reference_parameters = " + reference
            (if shape = "readonly" then "read_only_reference_parameters = [1]" else "")
            (if shape = "missing-element" then "" else "reference_elements = [\"\", \"" + element + "\"]")
            (if shape = "translated" then "constant_parameters = [\"\", \"\", \"4\"]" else "")
            "writable_buffers = [" + contracts + "]" ])
        let pilot = System.String.Join("\n", [
            "[library]"; "name = \"c\""; "header = \"" + header + "\""
            "[output]"; "mode = \"fidelity\""; "directory = \"" + output + "\""
            "[options]"; "c_header_mode = true"; "descriptor_only_dependencies = true"
            projection; (if shape = "duplicate-binding" then projection else "")
            "[[namespace]]"; "name = \"Test.Native\""; "description = \"writable byte contract fixture\""; "library = \"c\""
            "functions = [\"" + (if shape = "unselected" then "missing" else "read") + "\"]" ])
        let project = System.IO.Path.Combine(dir, "read.pilot.toml")
        System.IO.File.WriteAllText(project, pilot)
        match BindingGenerator.generateFromProject project false false Types.LP64 with
        | Error error ->
            Assert.Equal("held-original", System.IO.File.ReadAllText sentinel)
            Assert.Equal<string array>([|sentinel|], System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))
            Error error
        | Ok _ ->
            let generated = System.IO.Directory.GetFiles(output, "*.clef", System.IO.SearchOption.AllDirectories)
            let source = generated |> Array.map System.IO.File.ReadAllText |> String.concat "\n"
            if shape = "positive" then
                match System.Environment.GetEnvironmentVariable("FFI_WRITABLE_BUFFER_OUTPUT") with
                | null | "" -> ()
                | retained ->
                    System.IO.Directory.CreateDirectory(retained) |> ignore
                    System.IO.File.Copy(header, System.IO.Path.Combine(retained, "read.h"), true)
                    System.IO.File.Copy(project, System.IO.Path.Combine(retained, "read.pilot.toml"), true)
                    for file in generated do
                        let destination = System.IO.Path.Combine(retained, System.IO.Path.GetRelativePath(output, file))
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)) |> ignore
                        System.IO.File.Copy(file, destination, true)
            Ok source
    finally
        System.IO.Directory.Delete(dir, true)

[<Fact>]
let ``writable buffer actual header emits current neutral array signature and exact companion`` () =
    match writableProject "positive" with
    | Error error -> failwith error
    | Ok source ->
        for text in ["let read (fd: int) (buffer: int array) (count: int) : int"
                     "Expr<WritableBufferDescriptor>"; "Binding = \"Test.Native.read\""
                     "Parameter = 1"; "CountParameter = 2"; "Type = Integer (Unsigned, 8); PassBy = Reference"
                     "Identity = \"posix.read.borrow\""; "OwnershipTransfer = Undeclared"] do
            Assert.Contains(text, source)
        Assert.DoesNotContain("Expr<ResultRangeDescriptor>", source)

[<Theory>]
[<InlineData("wide", "explicit unsigned8 byte reference element contract")>]
[<InlineData("signed", "explicit unsigned8 byte reference element contract")>]
[<InlineData("missing-element", "explicit unsigned8 byte reference element contract")>]
[<InlineData("not-reference", "writable Reference passing")>]
[<InlineData("readonly", "writable Reference passing")>]
[<InlineData("const", "writable Reference passing")>]
[<InlineData("count-reference", "scalar integer Value passing")>]
[<InlineData("count-float", "scalar integer Value passing")>]
[<InlineData("buffer-ordinal", "parameter 3 is outside native arity 3")>]
[<InlineData("count-ordinal", "parameter 3 is outside native arity 3")>]
[<InlineData("same", "distinct buffer and count parameters")>]
[<InlineData("duplicate", "duplicate parameter indices")>]
[<InlineData("unknown", "contract has unknown fields")>]
[<InlineData("hypothesis", "requires hypothesis")>]
[<InlineData("translated", "untranslated native parameter list")>]
[<InlineData("unselected", "exactly one selected native declaration")>]
[<InlineData("duplicate-binding", "duplicate binding projections")>]
let ``writable buffer project refusals preserve all existing output`` shape reason =
    match writableProject shape with
    | Error error -> Assert.Contains("writable_buffers", error); Assert.Contains(reason, error)
    | Ok _ -> failwith "Unsupported writable buffer generated output"
