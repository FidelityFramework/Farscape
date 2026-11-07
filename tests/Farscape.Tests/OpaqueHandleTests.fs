module Farscape.Tests.OpaqueHandleTests

open Xunit
open Farscape.Core
open Farscape.Core.CodeAST
open Farscape.Core.CodeRenderer
open TestHelpers

// =============================================================================
// isOpaqueHandleTypedef Detection Tests
// =============================================================================

module DetectionTests =

    let private emptyStructs = Set.empty<string>

    [<Fact>]
    let ``detects typedef to pointer of undefined struct as opaque handle`` () =
        let td : CppParser.TypedefInfo =
            { Name = "hipStream_t"; UnderlyingType = "struct ihipStream_t *"; Documentation = None; Attributes = [] }
        Assert.True(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects typedef to value type`` () =
        let td : CppParser.TypedefInfo =
            { Name = "pid_t"; UnderlyingType = "int"; Documentation = None; Attributes = [] }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects typedef to void pointer`` () =
        let td : CppParser.TypedefInfo =
            { Name = "generic_ptr"; UnderlyingType = "void *"; Documentation = None; Attributes = [] }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects typedef to defined struct pointer`` () =
        let knownStructs = Set.ofList ["Point"]
        let td : CppParser.TypedefInfo =
            { Name = "Point_ptr"; UnderlyingType = "struct Point *"; Documentation = None; Attributes = [] }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef knownStructs td)

    [<Fact>]
    let ``detects when struct is not in known set`` () =
        let knownStructs = Set.ofList ["OtherStruct"]
        let td : CppParser.TypedefInfo =
            { Name = "hipEvent_t"; UnderlyingType = "struct ihipEvent_t *"; Documentation = None; Attributes = [] }
        Assert.True(ActivePatterns.isOpaqueHandleTypedef knownStructs td)

    [<Fact>]
    let ``rejects non-pointer typedef`` () =
        let td : CppParser.TypedefInfo =
            { Name = "myint"; UnderlyingType = "unsigned long"; Documentation = None; Attributes = [] }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects char pointer typedef`` () =
        let td : CppParser.TypedefInfo =
            { Name = "cstring"; UnderlyingType = "const char *"; Documentation = None; Attributes = [] }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

// =============================================================================
// detectOpaqueHandles End-to-End Tests
// =============================================================================

module EndToEndDetectionTests =

    let private mkOpaqueTypedef name structName =
        CppParser.Declaration.Typedef
            { Name = name; UnderlyingType = $"struct {structName} *"; Documentation = None; Attributes = [] }

    [<Fact>]
    let ``detectOpaqueHandles finds opaque typedefs in declaration list`` () =
        let decls = [
            mkOpaqueTypedef "hipStream_t" "ihipStream_t"
            mkOpaqueTypedef "hipEvent_t" "ihipEvent_t"
            CppParser.Declaration.Typedef (mkTypedef "pid_t" "int")
        ]
        let handles = FidelityCodeGenerator.detectOpaqueHandles decls
        Assert.Equal(2, handles.Count)
        Assert.Contains("hipStream_t", handles)
        Assert.Contains("hipEvent_t", handles)

    [<Fact>]
    let ``detectOpaqueHandles excludes typedef to defined struct`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)
            CppParser.Declaration.Typedef
                { Name = "Point_ptr"; UnderlyingType = "struct Point *"; Documentation = None; Attributes = [] }
        ]
        let handles = FidelityCodeGenerator.detectOpaqueHandles decls
        Assert.Empty(handles)

    [<Fact>]
    let ``detectOpaqueHandles returns empty for declarations with no typedefs`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let handles = FidelityCodeGenerator.detectOpaqueHandles decls
        Assert.Empty(handles)

// =============================================================================
// generateOpaqueHandleDecls AST Tests
// =============================================================================

module GenerationTests =

    [<Fact>]
    let ``generates only a nominal marker for an opaque pointer typedef`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"])
        let markers = decls |> List.choose (function OpaqueMarker name -> Some name | _ -> None)
        Assert.Equal<string list>(["hipStream_t"], markers)
        Assert.Empty(decls |> List.choose (function RecordType _ as d -> Some d | _ -> None))

    [<Fact>]
    let ``opaque typedef does not expose null or address constructors`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"])
        Assert.Empty(decls |> List.choose (function SubModule _ as d -> Some d | LetBinding _ as d -> Some d | _ -> None))

    [<Fact>]
    let ``multiple handles generate distinct types`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"; "hipEvent_t"])
        let markers = decls |> List.choose (function OpaqueMarker n -> Some n | _ -> None)
        Assert.Equal(2, markers.Length)
        Assert.Contains("hipEvent_t", markers)
        Assert.Contains("hipStream_t", markers)

// =============================================================================
// SubModule Rendering Tests
// =============================================================================

module SubModuleRenderTests =

    [<Fact>]
    let ``SubModule renders module Name = with indented children`` () =
        let decl = SubModule("MyModule", [
            LetBinding("value", [], Named "int32", Literal "42", [])
        ])
        let rendered = CodeRenderer.render decl
        Assert.Contains("module MyModule =", rendered)
        Assert.Contains("let value", rendered)

    [<Fact>]
    let ``opaque typedef marker has private construction and no native word`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"])
        // Wrap in a module to render
        let moduleDecl = Module("Test", "test", decls)
        let rendered = CodeRenderer.render moduleDecl
        Assert.Contains("type hipStream_t = private | Opaque_hipStream_t", rendered)
        Assert.DoesNotContain("nativeint", rendered)
        Assert.DoesNotContain("0n", rendered)
        Assert.DoesNotContain("let zero", rendered)
        Assert.DoesNotContain("let isNull", rendered)
        Assert.DoesNotContain("let ofHandle", rendered)

// =============================================================================
// FidelityCodeGenerator Integration Tests
// =============================================================================

module IntegrationTests =

    let private mkOpaqueTypedef name structName =
        CppParser.Declaration.Typedef
            { Name = name; UnderlyingType = $"struct {structName} *"; Documentation = None; Attributes = [] }

    [<Fact>]
    let ``opaque typedef parameters use nullable CHandle with a nominal marker`` () =
        let decls = [
            mkOpaqueTypedef "hipStream_t" "ihipStream_t"
            CppParser.Declaration.Function
                { Name = "hipStreamCreate"
                  ReturnType = "int"
                  Parameters = [("stream", "hipStream_t")]
                  Documentation = None
                  IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.ROCm.Stream" "amdhip64" Types.LP64 Map.empty
        Assert.Contains("type hipStream_t = private | Opaque_hipStream_t", output)
        Assert.Contains("(stream: option<CHandle<hipStream_t>>)", output)
        Assert.DoesNotContain("nativeint", output)

    [<Fact>]
    let ``generation without opaque handles has no handle marker`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.libc.Test" "libc" Types.LP64 Map.empty
        // Should NOT contain opaque handle infrastructure
        Assert.DoesNotContain("[<Struct>]", output)
        Assert.DoesNotContain("module ", output.Split('\n') |> Array.skip 2 |> String.concat "\n")

    [<Fact>]
    let ``opaque handle type survives typedef resolution`` () =
        // The C typedef identity is a phantom marker inside the handle.
        let decls = [
            mkOpaqueTypedef "hipStream_t" "ihipStream_t"
            CppParser.Declaration.Function
                { Name = "hipStreamSynchronize"
                  ReturnType = "int"
                  Parameters = [("stream", "hipStream_t")]
                  Documentation = None
                  IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.ROCm" "amdhip64" Types.LP64 Map.empty
        Assert.Contains("(stream: option<CHandle<hipStream_t>>)", output)
        Assert.DoesNotContain("(stream: nativeint)", output)

    [<Fact>]
    let ``multiple opaque handles in same generation`` () =
        let decls = [
            mkOpaqueTypedef "hipStream_t" "ihipStream_t"
            mkOpaqueTypedef "hipEvent_t" "ihipEvent_t"
            CppParser.Declaration.Function
                { Name = "hipEventRecord"
                  ReturnType = "int"
                  Parameters = [("event", "hipEvent_t"); ("stream", "hipStream_t")]
                  Documentation = None
                  IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.ROCm" "amdhip64" Types.LP64 Map.empty
        Assert.Contains("type hipStream_t = private | Opaque_hipStream_t", output)
        Assert.Contains("type hipEvent_t = private | Opaque_hipEvent_t", output)
        Assert.Contains("(event: option<CHandle<hipEvent_t>>)", output)
        Assert.Contains("(stream: option<CHandle<hipStream_t>>)", output)

    [<Fact>]
    let ``opaque typedef returns are nullable until a contract proves nonnull`` () =
        let create = mkFunc "createStream" "hipStream_t" []
        let declarations = [mkOpaqueTypedef "hipStream_t" "ihipStream_t"; CppParser.Declaration.Function create]
        let nullable = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty
        Assert.Contains("let createStream () : option<CHandle<hipStream_t>>", nullable)
        let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
                        NonnullAnnotations = Some { Parameters = Map.empty; Returns = Set.singleton "createStream" } }
        let nonnull = FidelityCodeGenerator.generateModule ctx ctx.OpaqueHandles declarations "Test" "test" "test" []
        Assert.Contains("let createStream () : CHandle<hipStream_t>", nonnull)
        Assert.Contains("ReturnType = Pointer 64", nonnull)

    [<Fact>]
    let ``pointers to opaque typedefs retain both pointer levels and marker identity`` () =
        let declarations = [mkOpaqueTypedef "hipStream_t" "ihipStream_t"]
        let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
        let ty = FidelityCodeGenerator.resolveCType ctx.TypedefMap Types.LP64 ctx.OpaqueHandles Map.empty Map.empty "hipStream_t *"
        Assert.Equal<FsType>(Generic("CHandle", Generic("CHandle", Named "hipStream_t")), FidelityCodeGenerator.clefTypeOf ty)

    [<Fact>]
    let ``callback and output cell signatures retain the opaque typedef marker`` () =
        let callback = mkFuncWithAttrs "registerStream" "void" [("notify", "void (*)(hipStream_t)")] [mkAttr "NonNullAttr" [0] None]
        let output = mkFunc "createStream" "int" [("stream", "hipStream_t *")]
        let declarations = [mkOpaqueTypedef "hipStream_t" "ihipStream_t"; CppParser.Declaration.Function callback; CppParser.Declaration.Function output]
        let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
        let projection : PilotTypes.BindingProjection =
            { Name = output.Name; Symbol = output.Name; ReferenceParameters = [0]; ReadOnlyReferenceParameters = []; ReferenceElements = []
              ConstantParameters = []; StringParameters = []; NonnullCallbacks = []; ParameterHandles = []; ReturnHandle = None; Ownership = None; ResultRange = None; Resource = None; WritableBuffers = []; CallbackInvocations = [] }
        let ctx = { ctx with Bindings = Map.ofList [output.Name, projection] }
        let source = FidelityCodeGenerator.generateModule ctx ctx.OpaqueHandles declarations "Test" "test" "test" []
        Assert.Contains("Invoke: FnPtr<option<CHandle<hipStream_t>> -> unit>", source)
        Assert.Contains("(stream: option<CHandle<hipStream_t>> array)", source)
        Assert.DoesNotContain("nativeint", source)

    [<Fact>]
    let ``opaque handle markers are not redeclared as inferred phantom markers`` () =
        let declarations = [mkOpaqueTypedef "hipStream_t" "ihipStream_t"]
        let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with PhantomMarkers = Set.singleton "hipStream_t" }
        let source = FidelityCodeGenerator.generateModule ctx ctx.OpaqueHandles declarations "Test" "test" "test" []
        Assert.Equal(2, source.Split("type hipStream_t =").Length)

    [<Fact>]
    let ``native header nullability survives macros typedefs and clang attribute indexing`` () =
        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-boundary-" + System.Guid.NewGuid().ToString("N") + ".h")
        try
            System.IO.File.WriteAllText(path, """
typedef struct native_token *Token;
#define REQUIRED(params) __attribute__((nonnull params))
void consume(Token token, void *other) REQUIRED((1));
void consume_all(Token token, void *other) __attribute__((nonnull));
Token create(void);
Token create_required(void) __attribute__((returns_nonnull));
void qualifiers(Token _Nullable optional, Token _Nonnull required);
            """)
            let options : CppParser.HeaderParserOptions =
                { HeaderFile = path; IncludePaths = []; Defines = []; Verbose = false; IncludeMacros = false
                  MacroPrefixes = []; IncludeRoot = None; CppMode = false }
            let declarations = CppParser.parseHeader options |> function Ok declarations -> declarations | Error error -> failwith error
            let attrs name =
                declarations |> List.pick (function CppParser.Declaration.Function f when f.Name = name -> Some f.Attributes | _ -> None)
                |> List.find (fun a -> a.Kind = "NonNullAttr")
            Assert.Equal<int list>([0], (attrs "consume").Args)
            Assert.Equal<int list>([0; 1], (attrs "consume_all").Args)
            let source = FidelityCodeGenerator.generate declarations "Test.Header" "test" Types.LP64 Map.empty
            Assert.Contains("type Token = private | Opaque_Token", source)
            Assert.Contains("let consume (token: CHandle<Token>) (other: option<CHandle<unit>>)", source)
            Assert.Contains("let consume_all (token: CHandle<Token>) (other: CHandle<unit>)", source)
            Assert.Contains("let create () : option<CHandle<Token>>", source)
            Assert.Contains("let create_required () : CHandle<Token>", source)
            Assert.Contains("let qualifiers (optional: option<CHandle<Token>>) (required: CHandle<Token>)", source)
            Assert.DoesNotContain("nativeint", source)
            Assert.DoesNotContain("ofHandle", source)
        finally
            System.IO.File.Delete path

    [<Fact>]
    let ``native nonnull preserves restrict prototypes and applies empty arguments only to pointer slots`` () =
      let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "ffi-contract-20261013", "native-nonnull-" + System.Guid.NewGuid().ToString("N")))
      System.IO.Directory.CreateDirectory directory |> ignore
      let path = System.IO.Path.Combine(directory, "native.h")
      try
        System.IO.File.WriteAllText(path, """
typedef void *Token;
extern double strtod(const char *__restrict text, char **__restrict end) __attribute__((nonnull(1)));
void all_pointer_slots(Token token, int count, void *const destination, void (*notify)(void)) __attribute__((nonnull));
void scalar_only(int count) __attribute__((nonnull));
        """)
        let options : CppParser.HeaderParserOptions =
          { HeaderFile = path; IncludePaths = []; Defines = []; Verbose = false; IncludeMacros = false
            MacroPrefixes = []; IncludeRoot = None; CppMode = false }
        let declarations = CppParser.parseHeader options |> function Ok d -> d | Error e -> failwith e
        let positions name =
          declarations |> List.pick(function CppParser.Declaration.Function f when f.Name = name -> Some f | _ -> None)
          |> fun f -> f.Attributes |> List.filter(fun a -> a.Kind = "NonNullAttr") |> List.collect(fun a -> a.Args)
        Assert.Equal<int list>([0], positions "strtod")
        Assert.Equal<int list>([0; 2; 3], positions "all_pointer_slots")
        Assert.Empty(positions "scalar_only")
      finally
        System.IO.Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData("void (* _Nullable)(void *)")>]
    [<InlineData("void (*)(void (* _Nullable)(int))")>]
    let ``nullable native callbacks require a real boundary adapter`` cType =
        let declarations = [CppParser.Declaration.Function (mkFunc "register" "void" [("callback", cType)])]
        let error = Assert.ThrowsAny<System.Exception>(fun () ->
            FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
        Assert.Contains("Nullable function-pointer contract", error.Message)

    [<Fact>]
    let ``native callback data values retain nullable defaults and typedef annotation evidence`` () =
        let path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-callback-" + System.Guid.NewGuid().ToString("N") + ".h")
        try
            System.IO.File.WriteAllText(path, """
typedef struct native_token *Token;
typedef Token _Nonnull RequiredToken;
typedef RequiredToken RequiredAlias;
typedef Token _Nullable OptionalToken;
typedef OptionalToken OptionalAlias;
typedef Token (*Transform)(Token, OptionalAlias, RequiredAlias);
typedef RequiredAlias (*RequiredTransform)(RequiredAlias);
void register_transform(Transform _Nonnull transform);
void register_required(RequiredTransform _Nonnull transform);
            """)
            let options : CppParser.HeaderParserOptions =
                { HeaderFile = path; IncludePaths = []; Defines = []; Verbose = false; IncludeMacros = false
                  MacroPrefixes = []; IncludeRoot = None; CppMode = false }
            let declarations = CppParser.parseHeader options |> function Ok declarations -> declarations | Error error -> failwith error
            let source = FidelityCodeGenerator.generate declarations "Test.Callback" "test" Types.LP64 Map.empty
            Assert.Contains("FnPtr<option<CHandle<Token>> -> option<CHandle<Token>> -> CHandle<Token> -> option<CHandle<Token>>>", source)
            Assert.Contains("FnPtr<CHandle<Token> -> CHandle<Token>>", source)
            Assert.Contains("ReturnType = Pointer 64", source)
            Assert.DoesNotContain("nativeint", source)
        finally
            System.IO.File.Delete path

    [<Fact>]
    let ``unknown nested function pointer values are rejected until nullable entry adapters exist`` () =
        let declarations = [
            CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(int)")
            CppParser.Declaration.Function (mkFunc "register" "void" [("callback", "void (*)(Notify)")]) ]
        let error = Assert.ThrowsAny<System.Exception>(fun () ->
            FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
        Assert.Contains("Nested native function-pointer value", error.Message)
        Assert.Contains("explicit nonnull evidence", error.Message)

    [<Fact>]
    let ``nonnull nested function pointer values retain their exact native signature`` () =
        let declarations = [CppParser.Declaration.Function (mkFuncWithAttrs "register" "void" [("callback", "void (*)(void (* _Nonnull)(int))")] [mkAttr "NonNullAttr" [0] None])]
        let source = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty
        Assert.Contains("FnPtr<FnPtr<int -> unit> -> unit>", source)
        Assert.Contains("Type = Pointer 64", source)

    [<Fact>]
    let ``nullable native evidence cannot be erased by a contradictory pilot assertion`` () =
        let functionDecl = mkFunc "create" "void * _Nullable" []
        let declarations = [CppParser.Declaration.Function functionDecl]
        let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
                        NonnullAnnotations = Some { Parameters = Map.empty; Returns = Set.singleton "create" } }
        let error = Assert.ThrowsAny<System.Exception>(fun () ->
            FidelityCodeGenerator.generateFunctionDecls ctx "test" functionDecl |> ignore)
        Assert.Contains("nullable native result conflicts with a nonnull contract", error.Message)

    [<Fact>]
    let ``direct function generation cannot bypass the retired pointer surface`` () =
        let functionDecl = mkFunc "create" "void *" []
        let ctx = { FidelityCodeGenerator.buildGenerationContext [] Types.LP64 Map.empty with NativePointerSurface = true }
        let error = Assert.ThrowsAny<System.Exception>(fun () ->
            FidelityCodeGenerator.generateFunctionDecls ctx "test" functionDecl |> ignore)
        Assert.Contains("experimental_native_pointer_surface cannot emit Clef source", error.Message)

    [<Fact>]
    let ``foreign function pointer results require explicit nonnull evidence`` () =
        let callback = CppParser.Declaration.Typedef (mkTypedef "Notify" "void (*)(int)")
        let getCallback = mkFunc "get_callback" "Notify" []
        let declarations = [callback; CppParser.Declaration.Function getCallback]
        let ctx = FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty
        let error = Assert.ThrowsAny<System.Exception>(fun () ->
            FidelityCodeGenerator.generateFunctionDecls ctx "test" getCallback |> ignore)
        Assert.Contains("native function-pointer result requires explicit nonnull evidence", error.Message)
        let proven = { ctx with NonnullAnnotations = Some { Parameters = Map.empty; Returns = Set.singleton getCallback.Name } }
        let source = FidelityCodeGenerator.generateModule proven Set.empty declarations "Test" "test" "test" []
        Assert.Contains("let get_callback () : FnPtr<int -> unit>", source)
        Assert.Contains("ReturnType = Pointer 64", source)

// =============================================================================
// Live HIP Header Test (conditional on /opt/rocm availability)
// =============================================================================

module LiveHipTests =

    let private hipHeaderExists =
        System.IO.File.Exists("/opt/rocm/include/hip/hip_runtime_api.h")

    [<Fact>]
    let ``live HIP header detects opaque handle typedefs`` () =
        if not hipHeaderExists then
            // Skip on systems without ROCm installed
            ()
        else
            let options : CppParser.HeaderParserOptions = {
                HeaderFile = "/opt/rocm/include/hip/hip_runtime_api.h"
                IncludePaths = ["/opt/rocm/include"]
                Defines = ["__HIP_PLATFORM_AMD__"]
                Verbose = false
                IncludeMacros = false
                MacroPrefixes = []; IncludeRoot = None
                CppMode = false
            }
            match CppParser.parseHeader options with
            | Error err -> Assert.Fail $"Parse failed: {err}"
            | Ok decls ->
                let handles = FidelityCodeGenerator.detectOpaqueHandles decls
                // hipStream_t and hipEvent_t should be detected
                Assert.True(handles.Contains("hipStream_t"), "hipStream_t should be detected as opaque handle")
                Assert.True(handles.Contains("hipEvent_t"), "hipEvent_t should be detected as opaque handle")
