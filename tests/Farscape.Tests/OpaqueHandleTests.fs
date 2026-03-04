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
            { Name = "hipStream_t"; UnderlyingType = "struct ihipStream_t *"; Documentation = None }
        Assert.True(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects typedef to value type`` () =
        let td : CppParser.TypedefInfo =
            { Name = "pid_t"; UnderlyingType = "int"; Documentation = None }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects typedef to void pointer`` () =
        let td : CppParser.TypedefInfo =
            { Name = "generic_ptr"; UnderlyingType = "void *"; Documentation = None }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects typedef to defined struct pointer`` () =
        let knownStructs = Set.ofList ["Point"]
        let td : CppParser.TypedefInfo =
            { Name = "Point_ptr"; UnderlyingType = "struct Point *"; Documentation = None }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef knownStructs td)

    [<Fact>]
    let ``detects when struct is not in known set`` () =
        let knownStructs = Set.ofList ["OtherStruct"]
        let td : CppParser.TypedefInfo =
            { Name = "hipEvent_t"; UnderlyingType = "struct ihipEvent_t *"; Documentation = None }
        Assert.True(ActivePatterns.isOpaqueHandleTypedef knownStructs td)

    [<Fact>]
    let ``rejects non-pointer typedef`` () =
        let td : CppParser.TypedefInfo =
            { Name = "myint"; UnderlyingType = "unsigned long"; Documentation = None }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

    [<Fact>]
    let ``rejects char pointer typedef`` () =
        let td : CppParser.TypedefInfo =
            { Name = "cstring"; UnderlyingType = "const char *"; Documentation = None }
        Assert.False(ActivePatterns.isOpaqueHandleTypedef emptyStructs td)

// =============================================================================
// detectOpaqueHandles End-to-End Tests
// =============================================================================

module EndToEndDetectionTests =

    let private mkOpaqueTypedef name structName =
        CppParser.Declaration.Typedef
            { Name = name; UnderlyingType = $"struct {structName} *"; Documentation = None }

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
                { Name = "Point_ptr"; UnderlyingType = "struct Point *"; Documentation = None }
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
    let ``generates Struct-attributed RecordType for opaque handle`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"])
        let records = decls |> List.choose (function RecordType (n, f, _, a) -> Some (n, f, a) | _ -> None)
        Assert.Equal(1, records.Length)
        let (name, fields, attrs) = records.[0]
        Assert.Equal("hipStream_t", name)
        Assert.Equal(1, fields.Length)
        Assert.Equal("Handle", fst fields.[0])
        Assert.Equal(Named "nativeint", snd fields.[0])
        Assert.Contains("Struct", attrs)

    [<Fact>]
    let ``generates companion SubModule with zero and isNull`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"])
        let submodules = decls |> List.choose (function SubModule (n, d) -> Some (n, d) | _ -> None)
        Assert.Equal(1, submodules.Length)
        let (name, children) = submodules.[0]
        Assert.Equal("hipStream_t", name)
        let bindings = children |> List.choose (function LetBinding (n, _, _, _, _) -> Some n | _ -> None)
        Assert.Contains("zero", bindings)
        Assert.Contains("isNull", bindings)

    [<Fact>]
    let ``multiple handles generate distinct types`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"; "hipEvent_t"])
        let records = decls |> List.choose (function RecordType (n, _, _, _) -> Some n | _ -> None)
        Assert.Equal(2, records.Length)
        Assert.Contains("hipEvent_t", records)
        Assert.Contains("hipStream_t", records)

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
    let ``opaque handle renders complete wrapper struct and companion`` () =
        let decls = FidelityCodeGenerator.generateOpaqueHandleDecls (Set.ofList ["hipStream_t"])
        // Wrap in a module to render
        let moduleDecl = Module("Test", "test", decls)
        let rendered = CodeRenderer.render moduleDecl
        Assert.Contains("[<Struct>]", rendered)
        Assert.Contains("type hipStream_t = {", rendered)
        Assert.Contains("Handle: nativeint", rendered)
        Assert.Contains("module hipStream_t =", rendered)
        Assert.Contains("let zero", rendered)
        Assert.Contains("let isNull", rendered)

// =============================================================================
// FidelityCodeGenerator Integration Tests
// =============================================================================

module IntegrationTests =

    let private mkOpaqueTypedef name structName =
        CppParser.Declaration.Typedef
            { Name = name; UnderlyingType = $"struct {structName} *"; Documentation = None }

    [<Fact>]
    let ``generate with opaque handle typedef produces wrapper type in output`` () =
        let decls = [
            mkOpaqueTypedef "hipStream_t" "ihipStream_t"
            CppParser.Declaration.Function
                { Name = "hipStreamCreate"
                  ReturnType = "int"
                  Parameters = [("stream", "hipStream_t")]
                  Documentation = None
                  IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.ROCm.Stream" "amdhip64" Types.LP64 Map.empty
        // Should contain the wrapper struct
        Assert.Contains("[<Struct>]", output)
        Assert.Contains("type hipStream_t = {", output)
        Assert.Contains("Handle: nativeint", output)
        // Function parameter should use wrapper type, not nativeint
        Assert.Contains("(stream: hipStream_t)", output)

    [<Fact>]
    let ``generate without opaque handles produces nativeint as before`` () =
        let decls = [
            CppParser.Declaration.Function (mkFunc "getpid" "int" [])
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.libc.Test" "libc" Types.LP64 Map.empty
        // Should NOT contain opaque handle infrastructure
        Assert.DoesNotContain("[<Struct>]", output)
        Assert.DoesNotContain("module ", output.Split('\n') |> Array.skip 2 |> String.concat "\n")

    [<Fact>]
    let ``opaque handle type survives typedef resolution`` () =
        // hipStream_t → struct ihipStream_t * would normally resolve to nativeint
        // With opaque handle detection, it should stay as hipStream_t
        let decls = [
            mkOpaqueTypedef "hipStream_t" "ihipStream_t"
            CppParser.Declaration.Function
                { Name = "hipStreamSynchronize"
                  ReturnType = "int"
                  Parameters = [("stream", "hipStream_t")]
                  Documentation = None
                  IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.ROCm" "amdhip64" Types.LP64 Map.empty
        Assert.Contains("(stream: hipStream_t)", output)
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
                  IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }
        ]
        let output = FidelityCodeGenerator.generate decls "Fidelity.ROCm" "amdhip64" Types.LP64 Map.empty
        Assert.Contains("type hipStream_t = {", output)
        Assert.Contains("type hipEvent_t = {", output)
        Assert.Contains("(event: hipEvent_t)", output)
        Assert.Contains("(stream: hipStream_t)", output)

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
            }
            match CppParser.parseHeader options with
            | Error err -> Assert.Fail $"Parse failed: {err}"
            | Ok decls ->
                let handles = FidelityCodeGenerator.detectOpaqueHandles decls
                // hipStream_t and hipEvent_t should be detected
                Assert.True(handles.Contains("hipStream_t"), "hipStream_t should be detected as opaque handle")
                Assert.True(handles.Contains("hipEvent_t"), "hipEvent_t should be detected as opaque handle")
