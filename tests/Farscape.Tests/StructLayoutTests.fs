module StructLayoutTests

open Xunit
open Farscape.Core
open Farscape.Core.CodeAST
open TestHelpers

// =========================================================================
// Layout Parser Tests (XParsec-based parseRecordLayouts)
// =========================================================================

module LayoutParser =

    [<Fact>]
    let ``parseRecordLayouts parses simple struct`` () =
        let input = """*** Dumping AST Record Layout
Type: struct Point
Layout: <ASTRecordLayout
  Size:64
  DataSize:64
  Alignment:32
  FieldOffsets: [0, 32]>
"""
        let result = CppParser.parseRecordLayouts input
        Assert.True(result.ContainsKey "Point")
        let layout = result.["Point"]
        Assert.Equal(64, layout.SizeBits)
        Assert.Equal(64, layout.DataSizeBits)
        Assert.Equal(32, layout.AlignmentBits)
        Assert.Equal<int list>([0; 32], layout.FieldOffsetsBits)

    [<Fact>]
    let ``parseRecordLayouts parses multiple structs`` () =
        let input = """*** Dumping AST Record Layout
Type: struct Point
Layout: <ASTRecordLayout
  Size:64
  DataSize:64
  Alignment:32
  FieldOffsets: [0, 32]>
*** Dumping AST Record Layout
Type: struct Rect
Layout: <ASTRecordLayout
  Size:128
  DataSize:128
  Alignment:32
  FieldOffsets: [0, 32, 64, 96]>
"""
        let result = CppParser.parseRecordLayouts input
        Assert.Equal(2, result.Count)
        Assert.True(result.ContainsKey "Point")
        Assert.True(result.ContainsKey "Rect")
        Assert.Equal<int list>([0; 32; 64; 96], result.["Rect"].FieldOffsetsBits)

    [<Fact>]
    let ``parseRecordLayouts handles empty field offsets`` () =
        let input = """*** Dumping AST Record Layout
Type: struct Empty
Layout: <ASTRecordLayout
  Size:0
  DataSize:0
  Alignment:8
  FieldOffsets: []>
"""
        let result = CppParser.parseRecordLayouts input
        Assert.True(result.ContainsKey "Empty")
        Assert.Equal<int list>([], result.["Empty"].FieldOffsetsBits)

    [<Fact>]
    let ``parseRecordLayouts skips malformed blocks`` () =
        let input = """*** Dumping AST Record Layout
garbage that should be skipped
*** Dumping AST Record Layout
Type: struct Valid
Layout: <ASTRecordLayout
  Size:32
  DataSize:32
  Alignment:32
  FieldOffsets: [0]>
"""
        let result = CppParser.parseRecordLayouts input
        Assert.Equal(1, result.Count)
        Assert.True(result.ContainsKey "Valid")

// =========================================================================
// CodeAST + Renderer Tests (ExplicitLayoutRecord)
// =========================================================================

module ExplicitLayoutRendering =

    [<Fact>]
    let ``render ExplicitLayoutRecord produces StructLayout attribute`` () =
        let decl = ExplicitLayoutRecord("Point", [
            { Name = "x"; Type = Named "int32"; OffsetBytes = 0 }
            { Name = "y"; Type = Named "int32"; OffsetBytes = 4 }
        ], 8, None)
        let output = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("[<StructLayout(LayoutKind.Explicit, Size = 8)>]", output)
        Assert.Contains("[<Struct>]", output)

    [<Fact>]
    let ``render ExplicitLayoutRecord produces FieldOffset per field`` () =
        let decl = ExplicitLayoutRecord("Point", [
            { Name = "x"; Type = Named "int32"; OffsetBytes = 0 }
            { Name = "y"; Type = Named "int32"; OffsetBytes = 4 }
        ], 8, None)
        let output = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("[<FieldOffset(0)>]", output)
        Assert.Contains("[<FieldOffset(4)>]", output)
        Assert.Contains("x: int32", output)
        Assert.Contains("y: int32", output)

    [<Fact>]
    let ``render ExplicitLayoutRecord with documentation`` () =
        let decl = ExplicitLayoutRecord("drm_mode_create_dumb", [
            { Name = "height"; Type = Named "uint32"; OffsetBytes = 0 }
            { Name = "width"; Type = Named "uint32"; OffsetBytes = 4 }
        ], 32, Some "Create a dumb buffer")
        let output = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("/// Create a dumb buffer", output)
        Assert.Contains("type drm_mode_create_dumb", output)

// =========================================================================
// FidelityCodeGenerator Tests (ABI-critical dispatch)
// =========================================================================

module GeneratorDispatch =

    [<Fact>]
    let ``generate produces ExplicitLayoutRecord for ABI-critical struct`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)
        ]
        let layouts = Map.ofList [("Point", mkLayout "Point" 64 32 [0; 32])]
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts
        Assert.Contains("[<StructLayout(LayoutKind.Explicit", output)
        Assert.Contains("[<FieldOffset(0)>]", output)
        Assert.Contains("[<FieldOffset(4)>]", output)

    [<Fact>]
    let ``generate produces plain RecordType for non-ABI-critical struct`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)
        ]
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
        Assert.DoesNotContain("StructLayout", output)
        Assert.DoesNotContain("FieldOffset", output)
        Assert.Contains("type Point", output)

    [<Fact>]
    let ``generate handles mixed ABI-critical and normal structs`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "AbiStruct" [mkField "a" "uint32_t"] None)
            CppParser.Declaration.Struct (mkStruct "NormalStruct" [mkField "b" "int"] None)
        ]
        let layouts = Map.ofList [("AbiStruct", mkLayout "AbiStruct" 32 32 [0])]
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts
        // ABI-critical struct gets explicit layout
        Assert.Contains("[<StructLayout(LayoutKind.Explicit", output)
        // Normal struct does not
        Assert.Contains("type NormalStruct", output)
        // Count occurrences: exactly one StructLayout attribute
        let structLayoutCount = output.Split("[<StructLayout").Length - 1
        Assert.Equal(1, structLayoutCount)

// =========================================================================
// PilotSerializer Tests ([options] section)
// =========================================================================

module OptionsSerializer =

    open PilotTypes

    [<Fact>]
    let ``deserialize parses options section with abi_critical_structs`` () =
        let toml = """
[library]
name = "drm"
header = "xf86drm.h"

[output]
mode = "fidelity"
directory = "./out"

[options]
abi_critical_structs = ["drm_mode_create_dumb", "drm_mode_map_dumb"]
generate_descriptors = true
"""
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok project ->
                Assert.True(project.Options.IsSome)
                let opts = project.Options.Value
                Assert.Equal(2, opts.AbiCriticalStructs.Length)
                Assert.Contains("drm_mode_create_dumb", opts.AbiCriticalStructs)
                Assert.True(opts.GenerateDescriptors)

    [<Fact>]
    let ``round-trip options with generate_descriptors`` () =
        let project : PilotProject = {
            Library = { Name = "drm"; Headers = ["xf86drm.h"]; XmlProtocols = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = None
            Options = Some { AbiCriticalStructs = ["drm_mode_create_dumb"]; GenerateDescriptors = true }
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("abi_critical_structs", toml)
        Assert.Contains("generate_descriptors", toml)
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok result ->
                Assert.True(result.Options.IsSome)
                Assert.Equal<string list>(["drm_mode_create_dumb"], result.Options.Value.AbiCriticalStructs)

    [<Fact>]
    let ``missing options section produces None`` () =
        let toml = """
[library]
name = "libc"
header = "stdio.h"

[output]
mode = "fidelity"
directory = "./out"
"""
        match Fidelity.Data.TOML.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok project -> Assert.True(project.Options.IsNone)

// =========================================================================
// DescriptorGenerator Tests
// =========================================================================

module DescriptorGen =

    [<Fact>]
    let ``mapToNTUKindString maps int32 correctly`` () =
        Assert.Equal("NTUKind.NTUint32", DescriptorGenerator.mapToNTUKindString "int32")

    [<Fact>]
    let ``mapToNTUKindString maps uint64 correctly`` () =
        Assert.Equal("NTUKind.NTUuint64", DescriptorGenerator.mapToNTUKindString "uint64")

    [<Fact>]
    let ``mapToNTUKindString maps nativeint to int64`` () =
        Assert.Equal("NTUKind.NTUint64", DescriptorGenerator.mapToNTUKindString "nativeint")

    [<Fact>]
    let ``generate produces valid BAREWire StructDescriptor source`` () =
        let s = mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None
        let layout = mkLayout "Point" 64 32 [0; 32]
        let output = DescriptorGenerator.generate [(s, layout)] "Fidelity.Test.Descriptors" Map.empty Types.LP64 Set.empty
        Assert.Contains("module Fidelity.Test.Descriptors", output)
        Assert.Contains("open BAREWire.Hardware", output)
        Assert.Contains("let Point : StructDescriptor", output)
        Assert.Contains("Name = \"Point\"", output)

    [<Fact>]
    let ``generate includes correct field offsets and types`` () =
        let s = mkStruct "Pair" [mkField "a" "uint32_t"; mkField "b" "uint64_t"] None
        let layout = mkLayout "Pair" 128 64 [0; 32]
        let output = DescriptorGenerator.generate [(s, layout)] "Test.Descriptors" Map.empty Types.LP64 Set.empty
        Assert.Contains("Offset = 0", output)
        Assert.Contains("Offset = 4", output)
        Assert.Contains("NTUKind.NTUuint32", output)
        Assert.Contains("NTUKind.NTUuint64", output)
