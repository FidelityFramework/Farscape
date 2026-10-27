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

    [<Fact>]
    let ``concurrent layout probes retain their own header measurements`` () =
        let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../..", "artifacts", "layout-concurrency", System.Guid.NewGuid().ToString("N")))
        System.IO.Directory.CreateDirectory(directory) |> ignore
        try
            let probes =
                [1 .. 4]
                |> List.map (fun size ->
                    let header = System.IO.Path.Combine(directory, $"probe{size}.h")
                    System.IO.File.WriteAllText(header, $"struct Probe {{ unsigned char data[{size}]; }};\n")
                    size, header)
            use start = new System.Threading.Barrier(probes.Length)
            let results =
                probes
                |> List.map (fun (size, header) -> async {
                    do! Async.SwitchToNewThread()
                    start.SignalAndWait() |> ignore
                    return size, CppParser.extractStructLayouts header [] [] ["struct Probe"] false
                })
                |> Async.Parallel
                |> Async.RunSynchronously
            for size, result in results do
                match result with
                | Error error -> Assert.Fail(error)
                | Ok layouts ->
                    Assert.Equal(1, layouts.Count)
                    let layout = layouts.["Probe"]
                    Assert.Equal(size * 8, layout.SizeBits)
                    Assert.Equal(8, layout.AlignmentBits)
                    Assert.Equal<int list>([0], layout.FieldOffsetsBits)
        finally
            System.IO.Directory.Delete(directory, true)

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

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(3)>]
    let ``generate refuses measured layouts with missing or extra field offsets`` count =
        let decls = [CppParser.Declaration.Struct (mkStruct "Point" [mkField "x" "int"; mkField "y" "int"] None)]
        let layouts = Map.ofList ["Point", mkLayout "Point" 64 32 (List.replicate count 0)]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal($"Measured layout 'Point' has {count} field offsets for 2 fields.", error.Message)

    [<Fact>]
    let ``measured union retains opaque storage after verifying original field offsets`` () =
        let union = {mkStruct "Value" [mkField "a" "int"; mkField "b" "int"] None with IsUnion = true}
        let layouts = Map.ofList ["Value", mkLayout "Value" 32 32 [0; 0]]
        let output = FidelityCodeGenerator.generate [CppParser.Declaration.Struct union] "Test" "test" Types.LP64 layouts
        Assert.Contains("let StorageOffset = 0", output)
        Assert.DoesNotContain("let aOffset", output)
        let malformed = Map.ofList ["Value", mkLayout "Value" 32 32 [0]]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate [CppParser.Declaration.Struct union] "Test" "test" Types.LP64 malformed |> ignore)
        Assert.Equal("Measured layout 'Value' has 1 field offsets for 2 fields.", error.Message)

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

    [<Theory>]
    [<InlineData(false, 1)>]
    [<InlineData(false, 8)>]
    [<InlineData(true, 1)>]
    [<InlineData(true, 8)>]
    let ``generate refuses bitfields instead of widening them to scalar fields`` measured secondOffset =
        let widths = if secondOffset = 8 then 8, 8 else 1, 3
        let fields = [mkBitField "ready" "unsigned int" (fst widths); mkBitField "mode" "unsigned int" (snd widths)]
        let decls = [CppParser.Declaration.Struct (mkStruct "Flags" fields None)]
        let layouts = if measured then Map.ofList ["Flags", mkLayout "Flags" 32 32 [0; secondOffset]] else Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Flags' field 'ready' is a bitfield; a supported bit-precise descriptor representation is required.", error.Message)

    [<Fact>]
    let ``measured bitfield union remains opaque storage without member descriptors`` () =
        let union = {mkStruct "Bits" [mkBitField "flag" "unsigned int" 1; mkBitField "mode" "unsigned int" 3; mkField "word" "unsigned int"] None with IsUnion = true}
        let layouts = Map.ofList ["Bits", mkLayout "Bits" 32 32 [0; 0; 0]]
        let output = FidelityCodeGenerator.generate [CppParser.Declaration.Struct union] "Test" "test" Types.LP64 layouts
        Assert.Contains("let StorageOffset = 0", output)
        Assert.Contains("Repr = Repr.U8; Count = 4", output)
        Assert.DoesNotContain("let flagOffset", output)
        Assert.DoesNotContain("let modeOffset", output)
        Assert.DoesNotContain("let wordOffset", output)
        Assert.DoesNotContain("bit field", output)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``ordinary fixed array retains its byte layout`` measured =
        let array = {mkField "bytes" "unsigned char[4]" with IsArray = true; ArraySize = Some 4}
        let decls = [CppParser.Declaration.Struct (mkStruct "Buffer" [array; mkField "tag" "unsigned int"] None)]
        let layouts = if measured then Map.ofList ["Buffer", mkLayout "Buffer" 64 32 [0; 32]] else Map.empty
        let output = FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts
        Assert.Contains("let tagOffset = 4", output)
        Assert.Contains("Repr = Repr.U8; Count = 4", output)

    [<Fact>]
    let ``unflagged field with a measured bit offset is refused instead of truncated`` () =
        let decls = [CppParser.Declaration.Struct (mkStruct "Flags" [mkField "ready" "unsigned int"; mkField "mode" "unsigned int"] None)]
        let layouts = Map.ofList ["Flags", mkLayout "Flags" 32 32 [0; 1]]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate decls "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Flags' field 'mode' has measured bit offset 1, which is not byte-aligned; a supported bit-precise descriptor representation is required.", error.Message)

    [<Fact>]
    let ``unmeasured bitfield union is refused without an opaque storage measurement`` () =
        let union = {mkStruct "Bits" [mkBitField "flag" "unsigned int" 1; mkField "word" "unsigned int"] None with IsUnion = true}
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate [CppParser.Declaration.Struct union] "Test" "test" Types.LP64 Map.empty |> ignore)
        Assert.Equal("Struct 'Bits' field 'flag' is a bitfield; a supported bit-precise descriptor representation is required.", error.Message)

    [<Fact>]
    let ``value struct bitfield keeps its earlier plain record refusal`` () =
        let decls = [CppParser.Declaration.Struct (mkStruct "Flags" [mkBitField "ready" "unsigned int" 1] None)]
        let context = {FidelityCodeGenerator.buildGenerationContext decls Types.LP64 Map.empty with ValueStructs = Set.singleton "Flags"}
        let error = Assert.ThrowsAny<System.Exception>(fun () -> FidelityCodeGenerator.generateModule context Set.empty decls "Test" "test" "test" [] |> ignore)
        Assert.Equal("Value struct 'Flags' requires a plain record with scalar or pointer fields", error.Message)

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

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``layout owner refuses a bitfield even when its width is absent`` measured =
        let field = {mkBitField "flag" "unsigned int" 1 with BitWidth = None}
        let structure = mkStruct "Incomplete" [field] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList [structure.Name, structure]
              Layouts = if measured then Map.ofList [structure.Name, mkLayout structure.Name 32 32 [0]] else Map.empty
              Model = Types.LP64
              Shape = fun _ -> DescriptorGenerator.ScalarField (repr TypeMapper.Unsigned 32) }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 structure |> ignore)
        Assert.Equal("Struct 'Incomplete' field 'flag' is a bitfield; a supported bit-precise descriptor representation is required.", error.Message)

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    let ``descriptor refuses nested bitfields regardless of outer and inner measurements`` outerMeasured innerMeasured =
        let inner = mkStruct "Inner" [mkBitField "flag" "unsigned int" 1] None
        let outer = mkStruct "Outer" [mkField "inner" "Inner"] None
        let layouts =
            [if outerMeasured then "Outer", mkLayout "Outer" 32 32 [0]
             if innerMeasured then "Inner", mkLayout "Inner" 32 32 [0]] |> Map.ofList
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList ["Inner", inner; "Outer", outer]
              Layouts = layouts
              Model = Types.LP64
              Shape = function
                | "Inner" -> DescriptorGenerator.StructField "Inner"
                | _ -> DescriptorGenerator.ScalarField (repr TypeMapper.Unsigned 32) }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source outer |> ignore)
        Assert.Equal("Struct 'Inner' field 'flag' is a bitfield; a supported bit-precise descriptor representation is required.", error.Message)

    [<Fact>]
    let ``nested measured bitfield union is conservatively refused by the descriptor owner`` () =
        let union = {mkStruct "Bits" [mkBitField "flag" "unsigned int" 1; mkField "word" "unsigned int"] None with IsUnion = true}
        let outer = mkStruct "Outer" [mkField "bits" "Bits"] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList ["Bits", union; "Outer", outer]
              Layouts = Map.ofList ["Bits", mkLayout "Bits" 32 32 [0; 0]; "Outer", mkLayout "Outer" 32 32 [0]]
              Model = Types.LP64
              Shape = function
                | "Bits" -> DescriptorGenerator.StructField "Bits"
                | _ -> DescriptorGenerator.ScalarField (repr TypeMapper.Unsigned 32) }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source outer |> ignore)
        Assert.Equal("Struct 'Bits' field 'flag' is a bitfield; a supported bit-precise descriptor representation is required.", error.Message)

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
