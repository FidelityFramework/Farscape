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

    let private withLayoutHeader (text: string) names inspect =
        let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../..", "artifacts", "array-extent-tests", System.Guid.NewGuid().ToString("N")))
        System.IO.Directory.CreateDirectory(directory) |> ignore
        try
            let header = System.IO.Path.Combine(directory, "arrays.h")
            System.IO.File.WriteAllText(header, text)
            let options : CppParser.HeaderParserOptions =
                { HeaderFile = header; IncludePaths = []; Defines = []; Verbose = false
                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false }
            let declarations = match CppParser.parseHeader options with Ok value -> value | Error reason -> failwith reason
            let layouts = match CppParser.extractStructLayouts header [] [] (names |> List.map (fun name -> "struct " + name)) false with Ok value -> value | Error reason -> failwith reason
            inspect declarations layouts
        finally
            System.IO.Directory.Delete(directory, true)

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

    [<Theory>]
    [<InlineData(false, "Mystery", "Mystery")>]
    [<InlineData(true, "Mystery", "Mystery")>]
    [<InlineData(false, "struct Mystery", "Mystery")>]
    [<InlineData(true, "struct Mystery", "Mystery")>]
    [<InlineData(false, "mystery_t", "mystery_t")>]
    [<InlineData(true, "mystery_t", "mystery_t")>]
    let ``unknown field representation is refused with its struct field and type`` measured cType unresolved =
        let holder = mkStruct "Holder" [mkField "payload" cType; mkField "tail" "int"] None
        let layouts = if measured then Map.ofList ["Holder", mkLayout "Holder" 96 32 [0; 64]] else Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate [CppParser.Declaration.Struct holder] "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal($"Struct 'Holder' field 'payload' has no known representation for type '{unresolved}'; a complete C representation contract is required.", error.Message)

    [<Fact>]
    let ``unknown field in the middle refuses before returning a descriptor`` () =
        let holder = mkStruct "Holder" [mkField "head" "int"; mkField "payload" "struct Mystery"; mkField "tail" "int"] None
        let layouts = Map.ofList ["Holder", mkLayout "Holder" 128 32 [0; 32; 96]]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate [CppParser.Declaration.Struct holder] "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Holder' field 'payload' has no known representation for type 'Mystery'; a complete C representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``complete nested representation retains payload extent and following offset`` measured =
        let data = {mkField "data" "unsigned char[8]" with IsArray = true; ArraySize = Some 8}
        let nested = mkStruct "Mystery" [data] None
        let holder = mkStruct "Holder" [mkField "payload" "struct Mystery"; mkField "tail" "int"] None
        let layouts = if measured then Map.ofList ["Mystery", mkLayout "Mystery" 64 8 [0]; "Holder", mkLayout "Holder" 96 32 [0; 64]] else Map.empty
        let output = FidelityCodeGenerator.generate [CppParser.Declaration.Struct nested; CppParser.Declaration.Struct holder] "Test" "test" Types.LP64 layouts
        Assert.Contains("Repr = Repr.U8; Count = 8", output)
        Assert.Contains("let tailOffset = 8", output)
        Assert.DoesNotContain("Count = 0", output)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``pointer to incomplete type remains a known pointer representation`` measured =
        let holder = mkStruct "Holder" [mkField "payload" "struct Incomplete *"; mkField "tail" "int"] None
        let layouts = if measured then Map.ofList ["Holder", mkLayout "Holder" 128 64 [0; 64]] else Map.empty
        let output = FidelityCodeGenerator.generate [CppParser.Declaration.Struct holder] "Test" "test" Types.LP64 layouts
        Assert.Contains("Repr = Repr.Pointer; Count = 1", output)
        Assert.Contains("let tailOffset = 8", output)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``opaque union needs measurement when a member representation is unknown`` measured =
        let union = {mkStruct "Variant" [mkField "payload" "mystery_t"; mkField "word" "unsigned int"] None with IsUnion = true}
        let declarations = [CppParser.Declaration.Struct union]
        if measured then
            let layouts = Map.ofList ["Variant", mkLayout "Variant" 64 32 [0; 0]]
            let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
            Assert.Contains("let StorageOffset = 0", output)
            Assert.Contains("Repr = Repr.U8; Count = 8", output)
            Assert.DoesNotContain("let payloadOffset", output)
        else
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
            Assert.Equal("Struct 'Variant' field 'payload' has no known representation for type 'mystery_t'; a complete C representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``nested record without declaration refuses at the representation owner`` measured =
        let holder = mkStruct "Holder" [mkField "payload" "Missing"] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList [holder.Name, holder]
              Layouts = if measured then Map.ofList [holder.Name, mkLayout holder.Name 32 32 [0]] else Map.empty
              Model = Types.LP64
              Shape = fun _ -> DescriptorGenerator.StructField "Missing" }
        let error = Assert.Throws<System.InvalidOperationException>(fun () ->
            if measured then DescriptorGenerator.structDecls source holder |> ignore
            else DescriptorGenerator.layoutOf source 0 holder |> ignore)
        Assert.Equal("Struct 'Holder' field 'payload' has nested record type 'Missing' with no available declaration; a complete C representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``fieldless nested record refuses in either layout stratum`` measured =
        let empty = mkStructSimple "Empty"
        let holder = mkStruct "Holder" [mkField "payload" "struct Empty"; mkField "tail" "int"] None
        let layouts = if measured then Map.ofList ["Empty", mkLayout "Empty" 0 8 []; "Holder", mkLayout "Holder" 32 32 [0; 0]] else Map.empty
        let declarations = [CppParser.Declaration.Struct empty; CppParser.Declaration.Struct holder]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Holder' field 'payload' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required.", error.Message)

    [<Fact>]
    let ``positive measured extent does not represent a fieldless nested record`` () =
        let empty = mkStructSimple "Empty"
        let holder = mkStruct "Holder" [mkField "payload" "Empty"] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList [empty.Name, empty; holder.Name, holder]
              Layouts = Map.ofList ["Empty", mkLayout "Empty" 8 8 []; "Holder", mkLayout "Holder" 8 8 [0]]
              Model = Types.LP64
              Shape = fun _ -> DescriptorGenerator.StructField "Empty" }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source holder |> ignore)
        Assert.Equal("Struct 'Holder' field 'payload' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required.", error.Message)

    [<Fact>]
    let ``fieldless nested record in the middle names the refusing field`` () =
        let empty = mkStructSimple "Empty"
        let holder = mkStruct "Holder" [mkField "head" "int"; mkField "payload" "Empty"; mkField "tail" "int"] None
        let declarations = [CppParser.Declaration.Struct empty; CppParser.Declaration.Struct holder]
        let layouts = Map.ofList ["Empty", mkLayout "Empty" 0 8 []; "Holder", mkLayout "Holder" 64 32 [0; 32; 32]]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Holder' field 'payload' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``nested recursion names the record holding the fieldless member`` measured =
        let empty = mkStructSimple "Empty"
        let middle = mkStruct "Mid" [mkField "e" "struct Empty"] None
        let outer = mkStruct "Outer" [mkField "mid" "struct Mid"] None
        let declarations = [CppParser.Declaration.Struct outer; CppParser.Declaration.Struct middle; CppParser.Declaration.Struct empty]
        let layouts = if measured then Map.ofList ["Empty", mkLayout "Empty" 0 8 []; "Mid", mkLayout "Mid" 0 8 [0]; "Outer", mkLayout "Outer" 0 8 [0]] else Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Mid' field 'e' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required.", error.Message)

    [<Fact>]
    let ``unmeasured layout entry refuses fieldless representation through a nested record`` () =
        let empty = mkStructSimple "Empty"
        let middle = mkStruct "Mid" [mkField "e" "Empty"] None
        let outer = mkStruct "Outer" [mkField "mid" "Mid"] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList ["Empty", empty; "Mid", middle; "Outer", outer]
              Layouts = Map.empty
              Model = Types.LP64
              Shape = fun name -> DescriptorGenerator.StructField name }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 outer |> ignore)
        Assert.Equal("Struct 'Mid' field 'e' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required.", error.Message)

    [<Fact>]
    let ``array count cannot represent a fieldless nested element`` () =
        let empty = mkStructSimple "Empty"
        let field = {mkField "e" "struct Empty[4]" with IsArray = true; ArraySize = Some 4}
        let holder = mkStruct "Holder" [field] None
        let declarations = [CppParser.Declaration.Struct empty; CppParser.Declaration.Struct holder]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
        Assert.Equal("Struct 'Holder' field 'e' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``fixed nested record array retains its full extent`` measured =
        let data = {mkField "data" "unsigned char[8]" with IsArray = true; ArraySize = Some 8}
        let nested = mkStruct "Mystery" [data] None
        let array = {mkField "arr" "struct Mystery[3]" with IsArray = true; ArraySize = Some 3}
        let holder = mkStruct "Holder" [array; mkField "tail" "int"] None
        let declarations = [CppParser.Declaration.Struct nested; CppParser.Declaration.Struct holder]
        let layouts = if measured then Map.ofList ["Mystery", mkLayout "Mystery" 64 8 [0]; "Holder", mkLayout "Holder" 224 32 [0; 192]] else Map.empty
        let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
        Assert.Contains("Repr = Repr.U8; Count = 24", output)
        Assert.Contains("let tailOffset = 24", output)
        Assert.DoesNotContain("Count = 0", output)

    [<Fact>]
    let ``real header forward declaration followed by definition retains nested layout`` () =
        let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../..", "artifacts", "nested-forward-definition", System.Guid.NewGuid().ToString("N")))
        System.IO.Directory.CreateDirectory(directory) |> ignore
        try
            let header = System.IO.Path.Combine(directory, "forward.h")
            System.IO.File.WriteAllText(header, "struct S; struct S { int a; }; struct H { struct S s; int tail; };\n")
            let options : CppParser.HeaderParserOptions =
                { HeaderFile = header; IncludePaths = []; Defines = []; Verbose = false
                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = false }
            let declarations = match CppParser.parseHeader options with Ok value -> value | Error reason -> failwith reason
            let layouts = match CppParser.extractStructLayouts header [] [] ["struct S"; "struct H"] false with Ok value -> value | Error reason -> failwith reason
            let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
            Assert.Contains("Repr = Repr.U8; Count = 4", output)
            Assert.Contains("let tailOffset = 4", output)
            Assert.DoesNotContain("Count = 0", output)
        finally
            System.IO.Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData("", false)>]
    [<InlineData("", true)>]
    [<InlineData("0", false)>]
    [<InlineData("0", true)>]
    [<InlineData("3", false)>]
    [<InlineData("3", true)>]
    let ``real array headers preserve extent and require a supported static count`` extent measured =
        let header = $"struct Payload {{ int length; unsigned char data[{extent}]; }};\n"
        withLayoutHeader header ["Payload"] (fun declarations layouts ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = "Payload" -> Some s | _ -> None)
            let field = record.Fields |> List.find (fun field -> field.Name = "data")
            let count = if extent = "" then None else Some (int extent)
            Assert.True(field.IsArray)
            Assert.Equal(count, field.ArraySize)
            Assert.Equal("unsigned char", field.Type)
            let generate () = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 (if measured then layouts else Map.empty)
            match count with
            | Some 3 ->
                let output = generate ()
                Assert.Contains("Repr = Repr.U8; Count = 3", output)
                Assert.Contains("let Size = 8", output)
                Assert.Contains("let dataOffset = 4", output)
            | _ ->
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> generate () |> ignore)
                let expected =
                    match count with
                    | None -> "Struct 'Payload' field 'data' has an array with no fixed element count; a supported dynamic array representation contract is required."
                    | Some value -> $"Struct 'Payload' field 'data' has array element count {value}; a positive fixed element count is required."
                Assert.Equal(expected, error.Message))

    [<Theory>]
    [<InlineData(false, 0, false)>]
    [<InlineData(false, 0, true)>]
    [<InlineData(true, 0, false)>]
    [<InlineData(true, 0, true)>]
    [<InlineData(true, -1, false)>]
    [<InlineData(true, -1, true)>]
    let ``array extent refuses at both direct representation entry points`` hasCount count measured =
        let field = {mkField "data" "unsigned char" with IsArray = true; ArraySize = if hasCount then Some count else None}
        let record = mkStruct "Payload" [field] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList [record.Name, record]
              Layouts = if measured then Map.ofList [record.Name, mkLayout record.Name 32 8 [0]] else Map.empty
              Model = Types.LP64
              Shape = fun _ -> DescriptorGenerator.ScalarField {Family = TypeMapper.Unsigned; Bits = 8; Stratum = TypeMapper.Declared; CType = "unsigned char"} }
        let expected =
            if hasCount then $"Struct 'Payload' field 'data' has array element count {count}; a positive fixed element count is required."
            else "Struct 'Payload' field 'data' has an array with no fixed element count; a supported dynamic array representation contract is required."
        let layoutError = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
        let descriptorError = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source record |> ignore)
        Assert.Equal(expected, layoutError.Message)
        Assert.Equal(expected, descriptorError.Message)

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    let ``nested array extent is required before multiplying record size`` hasCount measured =
        let nested = mkStruct "Mystery" [mkField "value" "int"] None
        let field = {mkField "arr" "struct Mystery[]" with IsArray = true; ArraySize = if hasCount then Some 0 else None}
        let holder = mkStruct "Holder" [field] None
        let declarations = [CppParser.Declaration.Struct nested; CppParser.Declaration.Struct holder]
        let layouts = if measured then Map.ofList ["Mystery", mkLayout "Mystery" 32 32 [0]; "Holder", mkLayout "Holder" 32 32 [0]] else Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts |> ignore)
        let expected =
            if hasCount then "Struct 'Holder' field 'arr' has array element count 0; a positive fixed element count is required."
            else "Struct 'Holder' field 'arr' has an array with no fixed element count; a supported dynamic array representation contract is required."
        Assert.Equal(expected, error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``field representation refusal precedes missing array extent`` fieldless =
        let field = {mkField "data" (if fieldless then "struct Empty[]" else "mystery_t[]") with IsArray = true; ArraySize = None}
        let holder = mkStruct "Holder" [field] None
        let declarations = [CppParser.Declaration.Struct (mkStructSimple "Empty"); CppParser.Declaration.Struct holder]
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
        let expected =
            if fieldless then "Struct 'Holder' field 'data' has fieldless nested record type 'Empty'; a supported empty or opaque record representation contract is required."
            else "Struct 'Holder' field 'data' has no known representation for type 'mystery_t'; a complete C representation contract is required."
        Assert.Equal(expected, error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``pointer to a flexible record needs no inline array extent`` measured =
        let header = "struct Flexible { int length; unsigned char data[]; }; struct Holder { struct Flexible *p; int tail; };\n"
        withLayoutHeader header ["Flexible"; "Holder"] (fun declarations layouts ->
            let holder = declarations |> List.filter (function CppParser.Declaration.Struct s -> s.Name = "Holder" | _ -> false)
            Assert.Single(holder) |> ignore
            let output = FidelityCodeGenerator.generate holder "Test" "test" Types.LP64 (if measured then layouts else Map.empty)
            Assert.Contains("Repr = Repr.Pointer; Count = 1", output)
            Assert.Contains("let tailOffset = 8", output))

    [<Theory>]
    [<InlineData("int payload[2][3]", "int[2][3]", false)>]
    [<InlineData("int payload[2][3]", "int[2][3]", true)>]
    [<InlineData("int (*payload)[3]", "int (*)[3]", false)>]
    [<InlineData("int (*payload)[3]", "int (*)[3]", true)>]
    [<InlineData("void (*payload[3])(int)", "void (*[3])(int)", false)>]
    [<InlineData("void (*payload[3])(int)", "void (*[3])(int)", true)>]
    [<InlineData("const int payload[2][3]", "const int[2][3]", false)>]
    [<InlineData("const int payload[2][3]", "const int[2][3]", true)>]
    let ``real unsupported array declarators retain spelling until field emission`` memberDeclaration spelling measured =
        withLayoutHeader $"struct Holder {{ {memberDeclaration}; }};\n" ["Holder"] (fun declarations layouts ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = "Holder" -> Some s | _ -> None)
            let field = Assert.Single(record.Fields)
            Assert.Equal(spelling, field.Type)
            Assert.False(field.IsArray)
            Assert.Equal(None, field.ArraySize)
            let error = Assert.Throws<System.InvalidOperationException>(fun () ->
                FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 (if measured then layouts else Map.empty) |> ignore)
            Assert.Equal($"Struct 'Holder' field 'payload' has array declarator '{spelling}'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required.", error.Message))

    [<Theory>]
    [<InlineData("int[2][3]", false)>]
    [<InlineData("int[2][3]", true)>]
    [<InlineData("int (*)[3]", false)>]
    [<InlineData("int (*)[3]", true)>]
    let ``direct descriptor entry points refuse unsupported declarators before shape resolution`` spelling measured =
        let holder = mkStruct "Holder" [mkField "payload" spelling] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList [holder.Name, holder]
              Layouts = if measured then Map.ofList [holder.Name, mkLayout holder.Name 192 64 [0]] else Map.empty
              Model = Types.LP64
              Shape = fun _ -> Assert.Fail("Shape must not receive an unsupported declarator"); DescriptorGenerator.UnknownField "unreachable" }
        let expected = $"Struct 'Holder' field 'payload' has array declarator '{spelling}'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required."
        let layoutError = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 holder |> ignore)
        let descriptorError = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source holder |> ignore)
        Assert.Equal(expected, layoutError.Message)
        Assert.Equal(expected, descriptorError.Message)

    [<Theory>]
    [<InlineData(0, false)>]
    [<InlineData(0, true)>]
    [<InlineData(1, false)>]
    [<InlineData(1, true)>]
    [<InlineData(2, false)>]
    [<InlineData(2, true)>]
    let ``declarator refusal preserves bitfield and declaration order precedence`` scenario measured =
        let fields, expected =
            match scenario with
            | 0 ->
                [mkField "payload" "mystery_t[2][3]"],
                "Struct 'Holder' field 'payload' has array declarator 'mystery_t[2][3]'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required."
            | 1 ->
                [mkField "payload" "int[2][3]"; mkBitField "flag" "unsigned int" 1],
                "Struct 'Holder' field 'flag' is a bitfield; a supported bit-precise descriptor representation is required."
            | _ ->
                [mkField "first" "mystery_t"; mkField "payload" "int[2][3]"],
                "Struct 'Holder' field 'first' has no known representation for type 'mystery_t'; a complete C representation contract is required."
        let holder = mkStruct "Holder" fields None
        let layouts = if measured then Map.ofList [holder.Name, mkLayout holder.Name 256 64 (List.replicate fields.Length 0)] else Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () ->
            FidelityCodeGenerator.generate [CppParser.Declaration.Struct holder] "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal(expected, error.Message)

    [<Fact>]
    let ``declarator refusal precedes a mismatched measured field count`` () =
        let holder = mkStruct "Holder" [mkField "payload" "int[2][3]"] None
        let layouts = Map.ofList [holder.Name, mkLayout holder.Name 192 32 []]
        let error = Assert.Throws<System.InvalidOperationException>(fun () ->
            FidelityCodeGenerator.generate [CppParser.Declaration.Struct holder] "Test" "test" Types.LP64 layouts |> ignore)
        Assert.Equal("Struct 'Holder' field 'payload' has array declarator 'int[2][3]'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData("int *payload[3]", "Repr.Pointer", 3, false, false, 24, false)>]
    [<InlineData("int *payload[3]", "Repr.Pointer", 3, false, false, 24, true)>]
    [<InlineData("const int payload[4]", "Repr.I32", 4, true, false, 16, false)>]
    [<InlineData("const int payload[4]", "Repr.I32", 4, true, false, 16, true)>]
    [<InlineData("volatile unsigned char payload[8]", "Repr.U8", 8, false, true, 8, false)>]
    [<InlineData("volatile unsigned char payload[8]", "Repr.U8", 8, false, true, 8, true)>]
    [<InlineData("void (*payload)(int)", "Repr.Pointer", 1, false, false, 8, false)>]
    [<InlineData("void (*payload)(int)", "Repr.Pointer", 1, false, false, 8, true)>]
    let ``supported array and function pointer fields retain representation`` memberDeclaration repr count readOnly isVolatile size measured =
        withLayoutHeader $"struct Holder {{ {memberDeclaration}; }};\n" ["Holder"] (fun declarations layouts ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = "Holder" -> Some s | _ -> None)
            let field = Assert.Single(record.Fields)
            Assert.Equal(readOnly, field.IsConst)
            Assert.Equal(isVolatile, field.IsVolatile)
            let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 (if measured then layouts else Map.empty)
            let access = if readOnly then "AccessKind.ReadOnly" else "AccessKind.ReadWrite"
            Assert.Contains($"Repr = {repr}; Count = {count}; Access = {access}", output)
            Assert.Contains($"let Size = {size}", output))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``unsupported union members require opaque storage measurement`` measured =
        withLayoutHeader "union Opaque { int matrix[2][3]; long tag; };\n" [] (fun declarations _ ->
            let layouts = if measured then Map.ofList ["Opaque", mkLayout "Opaque" 192 64 [0; 0]] else Map.empty
            let generate () = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
            if measured then
                let output = generate ()
                Assert.Contains("Repr = Repr.U8; Count = 24", output)
                Assert.DoesNotContain("let matrixOffset", output)
            else
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> generate () |> ignore)
                Assert.Equal("Struct 'Opaque' field 'matrix' has array declarator 'int[2][3]'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required.", error.Message))

    // One chain fixture exercises the existing layout and emission entry points with the
    // same declarations; only the selected leaf measurement or array multiplicity changes.
    let private depthChain last arrayAt =
        let records =
            [0 .. last] |> List.map (fun level ->
                let name = $"R{level}"
                let fields =
                    if level = last then [mkField "value" "int"]
                    else
                        let field = mkField "payload" $"struct R{level + 1}"
                        let field = if arrayAt = Some level then {field with IsArray = true; ArraySize = Some 3} else field
                        [field; mkField "tail" "int"]
                name, mkStruct name fields None) |> Map.ofList
        let source : DescriptorGenerator.LayoutSource =
            { Structs = records; Layouts = Map.empty; Model = Types.LP64
              Shape = fun typ ->
                  if typ = "int" then DescriptorGenerator.ScalarField {Family = TypeMapper.Signed; Bits = 32; Stratum = TypeMapper.Declared; CType = typ}
                  elif typ.StartsWith("struct ") then DescriptorGenerator.StructField(typ.Substring(7))
                  else DescriptorGenerator.UnknownField typ }
        source, records["R0"]

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``declared depth boundary retains scalar and array extents`` arrayLeaf =
        let source, root = depthChain 16 (if arrayLeaf then Some 15 else None)
        let expected = if arrayLeaf then 76 else 68
        let offsets, size, alignment, stratum = DescriptorGenerator.layoutOf source 0 root
        Assert.Equal<int list>([0; expected - 4], offsets)
        Assert.Equal(expected, size)
        Assert.Equal(4, alignment)
        Assert.Equal(TypeMapper.Declared, stratum)
        let output = CodeRenderer.render (Module("Test", "", DescriptorGenerator.structDecls source root))
        Assert.Contains($"let Size = {expected}", output)
        let declarations = source.Structs |> Map.toList |> List.map (snd >> CppParser.Declaration.Struct)
        FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore

    [<Fact>]
    let ``measured leaf supplies extent beyond the declared depth boundary`` () =
        let source, root = depthChain 17 None
        let source = {source with Layouts = Map.ofList ["R17", mkLayout "R17" 32 32 [0]]}
        let offsets, size, _, stratum = DescriptorGenerator.layoutOf source 0 root
        Assert.Equal<int list>([0; 68], offsets)
        Assert.Equal(72, size)
        Assert.Equal(TypeMapper.Declared, stratum)
        let output = CodeRenderer.render (Module("Test", "", DescriptorGenerator.structDecls source root))
        Assert.Contains("Repr = Repr.U8; Count = 68", output)

    [<Fact>]
    let ``all measured deep records keep their measured layout`` () =
        let source, root = depthChain 20 None
        let layouts = [0 .. 20] |> List.map (fun i -> $"R{i}", mkLayout $"R{i}" ((21 - i) * 32) 32 (if i = 20 then [0] else [0; (20 - i) * 32])) |> Map.ofList
        let source = {source with Layouts = layouts}
        let offsets, size, _, stratum = DescriptorGenerator.layoutOf source 30 root
        Assert.Equal<int list>([0; 80], offsets)
        Assert.Equal(84, size)
        Assert.Equal(TypeMapper.Measured, stratum)
        let declarations = source.Structs |> Map.toList |> List.map (snd >> CppParser.Declaration.Struct)
        let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
        Assert.Contains("let Size = 84", output)

    [<Theory>]
    [<InlineData(16, false)>]
    [<InlineData(17, false)>]
    [<InlineData(16, true)>]
    [<InlineData(17, true)>]
    let ``direct depth counter permits scalars and measured nested extents`` depth nested =
        let source, root = depthChain (if nested then 1 else 0) None
        let source = if nested then {source with Layouts = Map.ofList ["R1", mkLayout "R1" 32 32 [0]]} else source
        let offsets, size, _, stratum = DescriptorGenerator.layoutOf source depth root
        Assert.Equal<int list>((if nested then [0; 4] else [0]), offsets)
        Assert.Equal((if nested then 8 else 4), size)
        Assert.Equal(TypeMapper.Declared, stratum)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``unmeasured nesting beyond the limit refuses every representation entry point`` arrayLeaf =
        let source, root = depthChain 17 (if arrayLeaf then Some 16 else None)
        let expected = "Struct 'R16' field 'payload' nests record type 'R17' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required."
        let declarations = source.Structs |> Map.toList |> List.map (snd >> CppParser.Declaration.Struct)
        let calls =
            [ (fun () -> DescriptorGenerator.layoutOf source 0 root |> ignore)
              (fun () -> DescriptorGenerator.structDecls source root |> ignore)
              (fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore) ]
        for call in calls do
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> call ())
            Assert.Equal(expected, error.Message)

    [<Theory>]
    [<InlineData(16)>]
    [<InlineData(17)>]
    let ``direct layout entry refuses an unmeasured nested extent at the limit`` depth =
        let source, root = depthChain 1 None
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source depth root |> ignore)
        Assert.Equal("Struct 'R0' field 'payload' nests record type 'R1' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required.", error.Message)

    [<Fact>]
    let ``measured outer layout does not authorize an unmeasured nested field count`` () =
        let source, root = depthChain 18 None
        let source = {source with Layouts = Map.ofList ["R0", mkLayout "R0" 608 32 [0; 576]]}
        let _, size, _, stratum = DescriptorGenerator.layoutOf source 0 root
        Assert.Equal(76, size)
        Assert.Equal(TypeMapper.Measured, stratum)
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source root |> ignore)
        Assert.Equal("Struct 'R16' field 'payload' nests record type 'R17' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required.", error.Message)

    [<Fact>]
    let ``cyclic direct record input receives the finite depth refusal`` () =
        let source, _ = depthChain 0 None
        let record = mkStruct "A" [mkField "a" "struct A"] None
        let source = {source with Structs = Map.ofList ["A", record]}
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
        Assert.Equal("Struct 'A' field 'a' nests record type 'A' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    let ``representation and extent checks precede the depth fold`` scenario =
        let source, root = depthChain 1 None
        let source, root, expected =
            match scenario with
            | 0 ->
                {source with Structs = Map.remove "R1" source.Structs}, root,
                "Struct 'R0' field 'payload' has nested record type 'R1' with no available declaration; a complete C representation contract is required."
            | 1 ->
                source, {root with Fields = [{root.Fields.Head with IsArray = true; ArraySize = None}]},
                "Struct 'R0' field 'payload' has an array with no fixed element count; a supported dynamic array representation contract is required."
            | _ ->
                source, {root with Fields = root.Fields @ [mkField "later" "mystery_t"]},
                "Struct 'R0' field 'later' has no known representation for type 'mystery_t'; a complete C representation contract is required."
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 16 root |> ignore)
        Assert.Equal(expected, error.Message)

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
    let ``layout owner refuses an unknown representation in either layout stratum`` measured =
        let structure = mkStruct "Unknown" [mkField "payload" "missing_t"] None
        let source : DescriptorGenerator.LayoutSource =
            { Structs = Map.ofList [structure.Name, structure]
              Layouts = if measured then Map.ofList [structure.Name, mkLayout structure.Name 32 32 [0]] else Map.empty
              Model = Types.LP64
              Shape = fun _ -> DescriptorGenerator.UnknownField "missing_t" }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 structure |> ignore)
        Assert.Equal("Struct 'Unknown' field 'payload' has no known representation for type 'missing_t'; a complete C representation contract is required.", error.Message)

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    let ``nested unknown representation is refused independently of measurements`` outerMeasured innerMeasured =
        let inner = mkStruct "Inner" [mkField "payload" "missing_t"] None
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
                | _ -> DescriptorGenerator.UnknownField "missing_t" }
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source outer |> ignore)
        Assert.Equal("Struct 'Inner' field 'payload' has no known representation for type 'missing_t'; a complete C representation contract is required.", error.Message)

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
