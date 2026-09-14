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
// CodeAST + Renderer Tests (layout module and descriptor nodes)
// =========================================================================

module LayoutModuleRendering =

    [<Fact>]
    let ``render ValueBinding produces a typed value, not a function`` () =
        let decl = ValueBinding("Descriptor", Named "StructDescriptor", RecordBlock [ "Name", Literal "\"Point\"" ])
        let output = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("let Descriptor : StructDescriptor =", output)
        Assert.Contains("{ Name = \"Point\" }", output)
        Assert.DoesNotContain("let Descriptor ()", output)

    [<Fact>]
    let ``render Quoted RecordBlock produces a quotation with one field per line`` () =
        let decl =
            ValueBinding("writeDescriptor", Generic("Expr", Named "FunctionDescriptor"),
                Quoted(RecordBlock [
                    "CName", Literal "\"write\""
                    "Parameters", ArrayBlock [ Commented(Literal "{ Name = \"fd\" }", "width declared: int under LP64") ]
                    "CallingConvention", Identifier "CDecl" ]))
        let output = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("<@", output)
        Assert.Contains("@>", output)
        Assert.Contains("{ CName = \"write\"\n", output)
        Assert.Contains("[|\n", output)
        Assert.Contains("{ Name = \"fd\" } // width declared: int under LP64\n", output)
        Assert.Contains("|]\n", output)
        Assert.Contains("CallingConvention = CDecl }", output)

    [<Fact>]
    let ``render SubModule of literal offsets aligns literals with the descriptor`` () =
        let decl = SubModule("drm_mode_create_dumb", [
            LiteralBinding("Size", "32")
            LiteralBinding("heightOffset", "0")
            ValueBinding("Descriptor", Named "StructDescriptor", RecordBlock [ "Name", Literal "\"drm_mode_create_dumb\"" ]) ])
        let output = CodeRenderer.render (Module("Test", "test", [decl]))
        Assert.Contains("module drm_mode_create_dumb =\n    [<Literal>]\n    let Size = 32\n", output)
        Assert.Contains("    let heightOffset = 0\n", output)
        Assert.Contains("    let Descriptor : StructDescriptor =", output)

// =========================================================================
// FidelityCodeGenerator Tests (ABI-critical dispatch)
// =========================================================================

module GeneratorDispatch =

    [<Fact>]
    let ``generate produces a measured layout module for a struct the pilot measured`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)
        ]
        let layouts = Map.ofList [("Point", mkLayout "Point" 64 32 [0; 32])]
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts
        Assert.Contains("layout measured (clang record layout dump)", output)
        Assert.Contains("module Point =", output)
        Assert.Contains("let xOffset = 0", output)
        Assert.Contains("let yOffset = 4", output)
        Assert.Contains("offset measured; repr declared: int under LP64", output)
        Assert.DoesNotContain("type Point", output)

    [<Fact>]
    let ``generate produces a declared layout module for an unmeasured struct`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)
        ]
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 Map.empty
        Assert.Contains("layout declared (natural alignment under LP64", output)
        Assert.Contains("let Size = 8", output)
        Assert.Contains("let Alignment = 4", output)
        Assert.Contains("let yOffset = 4", output)
        Assert.DoesNotContain("type Point", output)

    [<Fact>]
    let ``generate names the stratum per struct when measured and unmeasured mix`` () =
        let decls = [
            CppParser.Declaration.Struct (mkStruct "AbiStruct" [mkField "a" "uint32_t"] None)
            CppParser.Declaration.Struct (mkStruct "NormalStruct" [mkField "b" "int"] None)
        ]
        let layouts = Map.ofList [("AbiStruct", mkLayout "AbiStruct" 32 32 [0])]
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts
        Assert.Contains("module AbiStruct =", output)
        Assert.Contains("module NormalStruct =", output)
        Assert.Equal(1, output.Split("layout measured").Length - 1)
        Assert.Equal(1, output.Split("layout declared").Length - 1)

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
            Library = { Name = "drm"; Headers = ["xf86drm.h"]; XmlProtocols = []; Introspection = []; IncludePaths = []; Defines = []; MacroPrefixes = []; PkgConfig = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = None
            Options = Some { AbiCriticalStructs = ["drm_mode_create_dumb"]; GenerateDescriptors = true; NativePointerSurface = false; CHeaderMode = false; Bindings = []; DescriptorOnlyDependencies = false; ValueStructs = []; TypedProtocol = false; LinkLibraries = false; MappedReturns = [] }
            Callbacks = None
            Nonnull = None
            ProtocolConfig = None
            Layer3 = None
            CppConfig = None
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

    let private repr family bits : TypeMapper.AbiRepr =
        { Family = family; Bits = bits; Stratum = TypeMapper.Declared; CType = "t" }

    [<Fact>]
    let ``reprSource maps signed 32 to Repr.I32`` () =
        Assert.Equal("Repr.I32", DescriptorGenerator.reprSource (repr TypeMapper.Signed 32))

    [<Fact>]
    let ``reprSource maps unsigned 64 to Repr.U64`` () =
        Assert.Equal("Repr.U64", DescriptorGenerator.reprSource (repr TypeMapper.Unsigned 64))

    [<Fact>]
    let ``reprSource maps a pointer to Repr.Pointer`` () =
        Assert.Equal("Repr.Pointer", DescriptorGenerator.reprSource (repr TypeMapper.Pointer 64))

    [<Fact>]
    let ``generate produces valid BAREWire StructDescriptor source`` () =
        let decls = [ CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None) ]
        let layouts = Map.ofList [("Point", mkLayout "Point" 64 32 [0; 32])]
        let output = FidelityCodeGenerator.generate decls "Fidelity.Test.Types" "test" Types.LP64 layouts
        Assert.Contains("module Fidelity.Test.Types", output)
        Assert.Contains("open BAREWire.Hardware", output)
        Assert.Contains("let Descriptor : StructDescriptor", output)
        Assert.Contains("Name = \"Point\"", output)
        Assert.Contains("Size = 8", output)
        Assert.Contains("Alignment = 4", output)

    [<Fact>]
    let ``generate includes correct field offsets and representations`` () =
        let decls = [ CppParser.Declaration.Struct (mkStruct "Pair" [mkField "a" "uint32_t"; mkField "b" "uint64_t"] None) ]
        let layouts = Map.ofList [("Pair", mkLayout "Pair" 128 64 [0; 64])]
        let output = FidelityCodeGenerator.generate decls "Test.Types" "test" Types.LP64 layouts
        Assert.Contains("Offset = 0", output)
        Assert.Contains("Offset = 8", output)
        Assert.Contains("Repr = Repr.U32", output)
        Assert.Contains("Repr = Repr.U64", output)
