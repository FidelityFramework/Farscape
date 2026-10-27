module StructLayoutTests

open Xunit
open Farscape.Core
open Farscape.Core.CodeAST
open TestHelpers

// =========================================================================
// Layout Parser Tests (XParsec-based parseRecordLayouts)
// =========================================================================

module LayoutParser =

    let private parsed input =
        match CppParser.parseRecordLayouts input with
        | Ok layouts -> layouts
        | Error reason -> failwithf "Expected a parsed layout: %s" reason

    let private parseError input =
        match CppParser.parseRecordLayouts input with
        | Error reason -> reason
        | Ok layouts -> failwithf "Expected a layout parse error, got %A" layouts

    let private layout name size dataSize alignment offsets =
        $"""*** Dumping AST Record Layout
Type: struct {name}
Layout: <ASTRecordLayout
  Size:{size}
  DataSize:{dataSize}
  Alignment:{alignment}
  FieldOffsets: {offsets}>
"""

    [<Fact>]
    let ``parseRecordLayouts parses simple struct`` () =
        let result = parsed (layout "Point" "64" "64" "32" "[0, 32]")
        Assert.True(result.ContainsKey "Point")
        let point = result.["Point"]
        Assert.Equal(64, point.SizeBits)
        Assert.Equal(64, point.DataSizeBits)
        Assert.Equal(32, point.AlignmentBits)
        Assert.Equal<int list>([0; 32], point.FieldOffsetsBits)

    [<Fact>]
    let ``parseRecordLayouts parses multiple structs`` () =
        let input = layout "Point" "64" "64" "32" "[0, 32]" + layout "Rect" "128" "128" "32" "[0, 32, 64, 96]"
        let result = parsed input
        Assert.Equal(2, result.Count)
        Assert.True(result.ContainsKey "Point")
        Assert.True(result.ContainsKey "Rect")
        Assert.Equal<int list>([0; 32; 64; 96], result.["Rect"].FieldOffsetsBits)

    [<Fact>]
    let ``parseRecordLayouts handles empty field offsets`` () =
        let result = parsed (layout "Empty" "0" "0" "8" "[]")
        Assert.True(result.ContainsKey "Empty")
        Assert.Equal<int list>([], result.["Empty"].FieldOffsetsBits)

    [<Fact>]
    let ``parseRecordLayouts rejects the first malformed marked block`` () =
        let input = "*** Dumping AST Record Layout\ngarbage that should be rejected\n" + layout "Valid" "32" "32" "32" "[0]"
        let error = parseError input
        Assert.Contains("block 1", error)
        Assert.Contains("garbage", error)

    [<Fact>]
    let ``parseRecordLayouts accepts diagnostics around a marked block`` () =
        let input = "clang diagnostic before layouts\n" + layout "P" "32" "32" "32" "[0]" + "clang diagnostic after layouts\n"
        let result = parsed input
        Assert.Equal(1, result.Count)
        Assert.Equal(32, result.["P"].SizeBits)

    [<Fact>]
    let ``parseRecordLayouts distinguishes absent and empty marked blocks`` () =
        Assert.Empty(parsed "")
        Assert.Empty(parsed "clang diagnostic without a layout marker")
        let error = parseError "*** Dumping AST Record Layout\n  \n"
        Assert.Contains("block 1", error)
        Assert.Contains("empty", error)

    [<Fact>]
    let ``parseRecordLayouts accepts the Int32 bit limit`` () =
        let result = parsed (layout "Limit" "2147483647" "2147483647" "2147483647" "[2147483647]")
        let item = result.["Limit"]
        Assert.Equal(System.Int32.MaxValue, item.SizeBits)
        Assert.Equal(System.Int32.MaxValue, item.DataSizeBits)
        Assert.Equal(System.Int32.MaxValue, item.AlignmentBits)
        Assert.Equal<int list>([System.Int32.MaxValue], item.FieldOffsetsBits)

    [<Theory>]
    [<InlineData("2147483648", "32", "32", "[0]", "Size:")>]
    [<InlineData("32", "2147483648", "32", "[0]", "DataSize:")>]
    [<InlineData("32", "32", "2147483648", "[0]", "Alignment:")>]
    [<InlineData("32", "32", "32", "[0, 2147483648]", "FieldOffsets:")>]
    let ``parseRecordLayouts reports the first overflowing marked block`` size dataSize alignment offsets label =
        let input = layout "Q" "32" "32" "32" "[0]" + layout "P" size dataSize alignment offsets
        let error = parseError input
        Assert.Contains("block 2", error)
        Assert.Contains("'P'", error)
        Assert.Contains(label, error)
        Assert.Contains("0..2147483647", error)
        Assert.Contains("wider measured bit representation contract is required", error)

    [<Theory>]
    [<InlineData("abc", "[0]", "Size:")>]
    [<InlineData("32junk", "[0]", "Size:")>]
    [<InlineData("32", "[0, x]", "FieldOffsets:")>]
    [<InlineData("32", "[0, 32", "FieldOffsets:")>]
    let ``parseRecordLayouts reports malformed field values and offsets`` size offsets label =
        let error = parseError (layout "Malformed" size "32" "32" offsets)
        Assert.Contains("block 1", error)
        Assert.Contains("'Malformed'", error)
        Assert.Contains(label, error)

    [<Theory>]
    [<InlineData("[]", 0)>]
    [<InlineData("[0, 32,64]", 3)>]
    let ``parseRecordLayouts accepts supported offset spacing`` offsets count =
        let result = parsed (layout "Offsets" "96" "96" "32" offsets)
        Assert.Equal(count, result.["Offsets"].FieldOffsetsBits.Length)

    [<Fact>]
    let ``real clang layout probe reports measured bit range overflow`` () =
        let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../..", "artifacts", "layout-bit-range", System.Guid.NewGuid().ToString("N")))
        System.IO.Directory.CreateDirectory(directory) |> ignore
        try
            let before = System.IO.Path.Combine(directory, "before.h")
            let at = System.IO.Path.Combine(directory, "at.h")
            System.IO.File.WriteAllText(before, "struct BitsBefore { unsigned char bytes[268435455]; };\n")
            System.IO.File.WriteAllText(at, "struct BitsAt { unsigned char bytes[268435456]; };\n")
            match CppParser.extractStructLayouts before [] [] ["struct BitsBefore"] false with
            | Ok layouts -> Assert.Equal(2147483640, layouts.["BitsBefore"].SizeBits)
            | Error reason -> failwithf "Expected measured BitsBefore: %s" reason
            match CppParser.extractStructLayouts at [] [] ["struct BitsAt"] false with
            | Ok layouts -> failwithf "Expected BitsAt range error, got %A" layouts
            | Error reason ->
                Assert.Contains("Record layout dump could not be parsed:", reason)
                Assert.Contains("BitsAt", reason)
                Assert.Contains("Size:", reason)
                Assert.Contains("0..2147483647", reason)
        finally
            System.IO.Directory.Delete(directory, true)

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

    let private attributeLayoutSource records layouts : DescriptorGenerator.LayoutSource =
        let scalar cType bits = DescriptorGenerator.ScalarField {Family = TypeMapper.Signed; Bits = bits; Stratum = TypeMapper.Declared; CType = cType}
        { Structs = records |> List.map (fun (record: CppParser.StructDecl) -> record.Name, record) |> Map.ofList
          Layouts = layouts
          Model = Types.LP64
          Shape = function
            | "char" -> scalar "char" 8
            | "int" -> scalar "int" 32
            | "struct P" -> DescriptorGenerator.StructField "P"
            | spelling -> DescriptorGenerator.UnknownField spelling }

    [<Theory>]
    [<InlineData(0, false)>]
    [<InlineData(0, true)>]
    [<InlineData(1, false)>]
    [<InlineData(1, true)>]
    [<InlineData(2, false)>]
    [<InlineData(2, true)>]
    [<InlineData(3, false)>]
    [<InlineData(3, true)>]
    let ``inline scalar storage must fit the measured record extent`` scenario invalid =
        let name, fields, validSize, badSize, alignment, validOffsets, badOffsets, field, extent, offset, ending =
            match scenario with
            | 0 -> "Scalar", "int first; int second;", 64, 64, 32, [0; 32], [0; 56], "second", 4, 7, 11
            | 1 -> "Array", "int values[2];", 64, 32, 32, [0], [0], "values", 8, 0, 8
            | 2 -> "Pointer", "char a; void* p;", 80, 64, 8, [0; 8], [0; 8], "p", 8, 1, 9
            | _ -> "Wide", "long double x;", 128, 80, 128, [0], [0], "x", 16, 0, 16
        withLayoutHeader $"struct {name} {{ {fields} }};" [name] (fun declarations measured ->
            let size, offsets = if invalid then badSize, badOffsets else validSize, validOffsets
            let layouts = Map.ofList [name, mkLayout name size alignment offsets]
            let generate () = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
            if invalid then
                let expected = $"Struct '{name}' field '{field}' has byte extent {extent} at measured offset {offset}, ending at byte {ending} beyond the record size {size / 8}; a measured layout consistent with its field representation is required."
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> generate () |> ignore)
                Assert.Equal(expected, error.Message)
                if scenario < 2 then
                    let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = name -> Some s | _ -> None)
                    let source = attributeLayoutSource [record] layouts
                    DescriptorGenerator.layoutOf source 0 record |> ignore
                    let direct = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source record |> ignore)
                    Assert.Equal(expected, direct.Message)
            else
                if scenario <> 2 then Assert.Equal(measured.[name], layouts.[name])
                let output = generate ()
                Assert.Contains($"let Size = {size / 8}\n", output)
                Assert.Contains($"let {field}Offset = {List.last offsets / 8}\n", output)
                Assert.Contains((if scenario = 1 then "Count = 2" else "Count = 1"), output))

    [<Fact>]
    let ``measured packed field extent admits an unaligned exact end`` () =
        withLayoutHeader "struct __attribute__((packed)) Packed { char a; int b; };" ["Packed"] (fun declarations measured ->
            Assert.Equal(mkLayout "Packed" 40 8 [0; 8], measured.["Packed"])
            let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 measured
            Assert.Contains("let Size = 5\n", output)
            Assert.Contains("let bOffset = 1\n", output)
            Assert.Contains("Repr = Repr.I32; Count = 1", output))

    [<Fact>]
    let ``declared field extents retain natural layout and tail padding`` () =
        withLayoutHeader "struct Natural { char a; int b; char c; };" ["Natural"] (fun declarations _ ->
            let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty
            for literal in ["let Size = 12\n"; "let aOffset = 0\n"; "let bOffset = 4\n"; "let cOffset = 8\n"] do
                Assert.Contains(literal, output))

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    [<InlineData(4)>]
    let ``inline nested storage uses the admitted child extent before containment`` scenario =
        let fields, size, offsets, childMeasured, expected =
            match scenario with
            | 0 -> "struct P inner; int tail;", 64, [0; 32], false, None
            | 1 -> "struct P inner[2]; int tail;", 96, [0; 64], true, None
            | 2 -> "int head; struct P inner;", 48, [0; 32], true, Some "Struct 'Outer' field 'inner' has byte extent 4 at measured offset 4, ending at byte 8 beyond the record size 6; a measured layout consistent with its field representation is required."
            | 3 -> "struct P inner[2]; int head;", 64, [32; 0], true, Some "Struct 'Outer' field 'inner' has byte extent 8 at measured offset 4, ending at byte 12 beyond the record size 8; a measured layout consistent with its field representation is required."
            | _ -> "int head; struct P inner;", 48, [0; 32], true, Some "Measured layout 'P' size has byte quantity -1 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required."
        withLayoutHeader $"struct P {{ int payload; }}; struct Outer {{ {fields} }};" ["P"; "Outer"] (fun declarations measured ->
            let records = declarations |> List.choose (function CppParser.Declaration.Struct s -> Some s | _ -> None)
            let outer = records |> List.find (fun s -> s.Name = "Outer")
            let layouts = measured.Add("Outer", mkLayout "Outer" size 32 offsets)
            let layouts = if childMeasured then layouts else layouts.Remove "P"
            let layouts = if scenario = 4 then layouts.Add("P", mkLayout "P" -8 32 [0]) else layouts
            let source = attributeLayoutSource records layouts
            DescriptorGenerator.layoutOf source 0 outer |> ignore
            let emit () = DescriptorGenerator.structDecls source outer |> fun decls -> CodeRenderer.render (Module("Test", "test", decls))
            // Emit Outer first so its own nested field consumption establishes the refusal.
            let ordered = CppParser.Declaration.Struct outer :: (declarations |> List.filter (function CppParser.Declaration.Struct s when s.Name = "Outer" -> false | _ -> true))
            let generate () = FidelityCodeGenerator.generate ordered "Test" "test" Types.LP64 layouts
            match expected with
            | Some reason ->
                for run in [emit; generate] do
                    let error = Assert.Throws<System.InvalidOperationException>(fun () -> run () |> ignore)
                    Assert.Equal(reason, error.Message)
            | None ->
                for run in [emit; generate] do
                    let output = run ()
                    Assert.Contains((if scenario = 1 then "Repr = Repr.U8; Count = 8" else "Repr = Repr.U8; Count = 4"), output)
                    Assert.Contains((if scenario = 1 then "let tailOffset = 8\n" else "let tailOffset = 4\n"), output))

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    let ``inline extent refusal follows starting offset representation and range admission`` scenario =
        let fields, size, offsets, reason =
            match scenario with
            | 0 -> [mkField "first" "int"; mkField "second" "int"], 64, [0; 64], "Struct 'Probe' field 'second' has measured byte offset 8 outside the measured storage 0..7; a measured layout consistent with its record size is required."
            | 1 -> [mkField "unknown" "Mystery"; mkField "second" "int"], 64, [0; 56], "Struct 'Probe' field 'unknown' has no known representation for type 'Mystery'; a complete C representation contract is required."
            | _ -> [{ mkField "values" "int[2]" with IsArray = true; ArraySize = Some 1073741824 }], 64, [0], "Struct 'Probe' field 'values' extent has byte quantity 4294967296 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required."
        let record = mkStruct "Probe" fields None
        let source = attributeLayoutSource [record] (Map.ofList ["Probe", mkLayout "Probe" size 32 offsets])
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source record |> ignore)
        Assert.Equal(reason, error.Message)

    [<Theory>]
    [<InlineData(0, "Measured layout 'Value' has size 0 bytes for 2 fields; a supported empty or opaque record representation contract is required.")>]
    [<InlineData(-8, "Measured layout 'Value' size has byte quantity -1 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required.")>]
    let ``measured union rejects empty and negative size before opaque storage`` size expected =
        withLayoutHeader "union Value { int first; int second; }; struct Probe { int payload; };" ["Probe"] (fun declarations _ ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = "Value" -> Some s | _ -> None)
            let layouts = Map.ofList ["Value", mkLayout "Value" size 32 [0; 0]]
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate [CppParser.Declaration.Struct record] "Test" "test" Types.LP64 layouts |> ignore)
            Assert.Equal(expected, error.Message))

    [<Theory>]
    [<InlineData(-8, 32, 0, 32, "Measured layout 'Holder' size has byte quantity -1 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required.")>]
    [<InlineData(0, 32, 0, 32, "Measured layout 'Holder' has size 0 bytes for 2 fields; a supported empty or opaque record representation contract is required.")>]
    [<InlineData(65, 32, 0, 32, "Measured layout 'Holder' has size 65 bits, which is not whole bytes; a supported bit-precise descriptor representation is required.")>]
    [<InlineData(64, 0, 0, 32, "Measured layout 'Holder' has alignment 0 bits; a positive whole-byte measured alignment is required.")>]
    [<InlineData(64, -8, 0, 32, "Measured layout 'Holder' has alignment -8 bits; a positive whole-byte measured alignment is required.")>]
    [<InlineData(64, 33, 0, 32, "Measured layout 'Holder' has alignment 33 bits, which is not whole bytes; a supported bit-precise descriptor representation is required.")>]
    [<InlineData(64, 32, -8, 32, "Struct 'Holder' field 'first' has measured byte offset -1 outside the measured storage 0..7; a measured layout consistent with its record size is required.")>]
    [<InlineData(64, 32, -3, 32, "Struct 'Holder' field 'first' has measured bit offset -3, which is not byte-aligned; a supported bit-precise descriptor representation is required.")>]
    [<InlineData(64, 32, 0, 128, "Struct 'Holder' field 'second' has measured byte offset 16 outside the measured storage 0..7; a measured layout consistent with its record size is required.")>]
    [<InlineData(64, 32, 0, 64, "Struct 'Holder' field 'second' has measured byte offset 8 outside the measured storage 0..7; a measured layout consistent with its record size is required.")>]
    let ``measured storage quantities refuse before descriptor emission`` sizeBits alignmentBits firstOffset secondOffset expected =
        withLayoutHeader "struct Holder { int first; int second; };" ["Holder"] (fun declarations _ ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = "Holder" -> Some s | _ -> None)
            let layouts = Map.ofList ["Holder", mkLayout "Holder" sizeBits alignmentBits [firstOffset; secondOffset]]
            let source = attributeLayoutSource [record] layouts
            for emit in [ (fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
                          (fun () -> DescriptorGenerator.structDecls source record |> ignore)
                          (fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts |> ignore) ] do
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> emit ())
                Assert.Equal(expected, error.Message))

    [<Theory>]
    [<InlineData("int", 64, 32, 32)>]
    [<InlineData("char", 16, 8, 8)>]
    let ``valid measured storage retains every byte offset including size minus one`` cType sizeBits alignmentBits secondOffset =
        withLayoutHeader $"struct Holder {{ {cType} first; {cType} second; }};" ["Holder"] (fun declarations measured ->
            let layout = mkLayout "Holder" sizeBits alignmentBits [0; secondOffset]
            Assert.Equal(layout, measured.["Holder"])
            let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 (Map.ofList ["Holder", layout])
            Assert.Contains($"let Size = {sizeBits / 8}\n", output)
            Assert.Contains($"let Alignment = {alignmentBits / 8}\n", output)
            Assert.Contains($"let secondOffset = {secondOffset / 8}\n", output)
            Assert.Contains("layout measured (clang record layout dump)", output))

    [<Theory>]
    [<InlineData(32)>]
    [<InlineData(4)>]
    [<InlineData(65)>]
    let ``measured union admits storage size before replacing original members`` sizeBits =
        withLayoutHeader "union Value { int first; int second; }; struct Probe { int payload; };" ["Probe"] (fun declarations _ ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct record when record.Name = "Value" -> Some record | _ -> None)
            let layouts = Map.ofList ["Value", mkLayout "Value" sizeBits 32 [0; 0]]
            let generate () = FidelityCodeGenerator.generate [CppParser.Declaration.Struct record] "Test" "test" Types.LP64 layouts
            if sizeBits = 32 then
                let output = generate ()
                Assert.Contains("let Size = 4", output)
                Assert.Contains("Repr = Repr.U8; Count = 4", output)
                Assert.DoesNotContain("let firstOffset", output)
                Assert.DoesNotContain("let secondOffset", output)
            else
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> generate () |> ignore)
                Assert.Equal($"Measured layout 'Value' has size {sizeBits} bits, which is not whole bytes; a supported bit-precise descriptor representation is required.", error.Message))

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    let ``nested measured size is admitted at its own record before byte count`` outerMeasured invalidChild =
        withLayoutHeader "struct P { int payload; }; struct Outer { struct P inner; int tail; };" ["P"; "Outer"] (fun declarations measured ->
            let records = declarations |> List.choose (function CppParser.Declaration.Struct s -> Some s | _ -> None)
            let outer = records |> List.find (fun record -> record.Name = "Outer")
            let layouts = if outerMeasured then measured else measured.Remove "Outer"
            let layouts = if invalidChild then layouts.Add("P", {layouts.["P"] with SizeBits = -8; DataSizeBits = -8}) else layouts
            let source = attributeLayoutSource records layouts
            if outerMeasured then
                let offsets, size, alignment, stratum = DescriptorGenerator.layoutOf source 0 outer
                Assert.Equal<int list>([0; 4], offsets)
                Assert.Equal(8, size)
                Assert.Equal(4, alignment)
                Assert.Equal(TypeMapper.Measured, stratum)
            if invalidChild then
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source outer |> ignore)
                Assert.Equal("Measured layout 'P' size has byte quantity -1 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required.", error.Message)
            else
                let output = DescriptorGenerator.structDecls source outer |> fun decls -> CodeRenderer.render (Module("Test", "test", decls))
                Assert.Contains("Repr = Repr.U8; Count = 4", output)
                Assert.Contains("let tailOffset = 4", output))

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    [<InlineData(4)>]
    let ``measured input refusal preserves field and record precedence`` scenario =
        let ordinary = [mkField "first" "int"; mkField "second" "int"]
        let fields, size, offsets, expected =
            match scenario with
            | 0 -> [mkBitField "first" "int" 1; mkField "second" "int"], 65, [0; 32], "Struct 'Holder' field 'first' is a bitfield; a supported bit-precise descriptor representation is required."
            | 1 -> [mkField "first" "Mystery"; mkField "second" "int"], 65, [0; 32], "Struct 'Holder' field 'first' has no known representation for type 'Mystery'; a complete C representation contract is required."
            | 2 -> ordinary, 65, [0], "Measured layout 'Holder' has 1 field offsets for 2 fields."
            | 3 -> ordinary, 65, [-3; 32], "Measured layout 'Holder' has size 65 bits, which is not whole bytes; a supported bit-precise descriptor representation is required."
            | _ -> ordinary, 64, [-8; 128], "Struct 'Holder' field 'first' has measured byte offset -1 outside the measured storage 0..7; a measured layout consistent with its record size is required."
        let record = mkStruct "Holder" fields None
        let layouts = Map.ofList ["Holder", mkLayout "Holder" size 32 offsets]
        let source = attributeLayoutSource [record] layouts
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
        Assert.Equal(expected, error.Message)

    [<Fact>]
    let ``fieldless measured record remains absent from generated storage`` () =
        let record = mkStructSimple "Empty"
        let layouts = Map.ofList ["Empty", mkLayout "Empty" 0 8 []]
        let output = FidelityCodeGenerator.generate [CppParser.Declaration.Struct record] "Test" "test" Types.LP64 layouts
        Assert.DoesNotContain("module Empty", output)
        Assert.DoesNotContain("let Descriptor", output)

    [<Theory>]
    [<InlineData("P", false)>]
    [<InlineData("P", true)>]
    [<InlineData("Pragma", false)>]
    [<InlineData("Pragma", true)>]
    [<InlineData("A", false)>]
    [<InlineData("A", true)>]
    [<InlineData("F", false)>]
    [<InlineData("F", true)>]
    [<InlineData("G", false)>]
    [<InlineData("G", true)>]
    [<InlineData("Deprecated", false)>]
    [<InlineData("Deprecated", true)>]
    let ``real record and field attributes require measured descriptor layouts`` name measured =
        let header, kind, fieldAttribute, size, alignment, offsets, args =
            match name with
            | "P" -> "struct __attribute__((packed)) P { char lead; int payload; };", "PackedAttr", false, 5, 1, [0; 1], []
            | "Pragma" -> "#pragma pack(push, 1)\nstruct Pragma { char lead; int payload; };\n#pragma pack(pop)\n", "MaxFieldAlignmentAttr", false, 5, 1, [0; 1], []
            | "A" -> "struct __attribute__((aligned(32))) A { int payload; };", "AlignedAttr", false, 32, 32, [0], [32]
            | "F" -> "struct F { char lead; int payload __attribute__((aligned(16))); };", "AlignedAttr", true, 32, 16, [0; 16], [16]
            | "G" -> "struct G { char lead; int payload __attribute__((packed)); };", "PackedAttr", true, 5, 1, [0; 1], []
            | _ -> "struct __attribute__((deprecated)) Deprecated { char lead; int payload; };", "DeprecatedAttr", false, 8, 4, [0; 4], []
        withLayoutHeader header [name] (fun declarations layouts ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = name -> Some s | _ -> None)
            let attributes = if fieldAttribute then (record.Fields |> List.find (fun field -> field.Name = "payload")).Attributes else record.Attributes
            let attribute = Assert.Single(attributes)
            Assert.Equal(kind, attribute.Kind)
            Assert.Equal<int list>(args, attribute.Args)
            let source = attributeLayoutSource [record] (if measured then layouts else Map.empty)
            if measured then
                let actualOffsets, actualSize, actualAlignment, stratum = DescriptorGenerator.layoutOf source 0 record
                Assert.Equal<int list>(offsets, actualOffsets)
                Assert.Equal(size, actualSize)
                Assert.Equal(alignment, actualAlignment)
                Assert.Equal(TypeMapper.Measured, stratum)
                DescriptorGenerator.structDecls source record |> ignore
                let output = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts
                Assert.Contains($"let Size = {size}\n", output)
                Assert.Contains($"let Alignment = {alignment}\n", output)
                List.zip record.Fields offsets |> List.iter (fun (field, offset) -> Assert.Contains($"let {field.Name}Offset = {offset}\n", output))
            else
                let subject = if fieldAttribute then $"Struct '{name}' field 'payload'" else $"Struct '{name}'"
                let expected =
                    if kind = "DeprecatedAttr" then $"{subject} carries attribute '{kind}' with unclassified layout effect; a measured layout or a classified attribute contract is required."
                    else $"{subject} carries layout attribute '{kind}' that natural alignment cannot represent; a measured layout is required."
                for emit in [ (fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
                              (fun () -> DescriptorGenerator.structDecls source record |> ignore)
                              (fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore) ] do
                    let error = Assert.Throws<System.InvalidOperationException>(fun () -> emit ())
                    Assert.Equal(expected, error.Message))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``unused attributed record does not prevent ordinary record emission`` measured =
        withLayoutHeader "struct __attribute__((packed)) P { char lead; int payload; }; struct Ordinary { char lead; int payload; };" ["P"; "Ordinary"] (fun declarations layouts ->
            let records = declarations |> List.choose (function CppParser.Declaration.Struct s -> Some s | _ -> None)
            let ordinary = records |> List.find (fun record -> record.Name = "Ordinary")
            Assert.Empty(ordinary.Attributes)
            let measurements = if measured then layouts else Map.empty
            let source = attributeLayoutSource records measurements
            let offsets, size, alignment, stratum = DescriptorGenerator.layoutOf source 0 ordinary
            Assert.Equal<int list>([0; 4], offsets)
            Assert.Equal(8, size)
            Assert.Equal(4, alignment)
            Assert.Equal((if measured then TypeMapper.Measured else TypeMapper.Declared), stratum)
            DescriptorGenerator.structDecls source ordinary |> ignore
            let output = FidelityCodeGenerator.generate [CppParser.Declaration.Struct ordinary] "Test" "test" Types.LP64 measurements
            Assert.Contains("let payloadOffset = 4", output)
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
            Assert.Equal("Struct 'P' carries layout attribute 'PackedAttr' that natural alignment cannot represent; a measured layout is required.", error.Message))

    [<Fact>]
    let ``anonymous typedef record retains its layout attribute and public name`` () =
        withLayoutHeader "typedef struct __attribute__((packed)) { char lead; int payload; } AnonPacked; struct Ordinary { int value; };" ["Ordinary"] (fun declarations _ ->
            let record = declarations |> List.pick (function CppParser.Declaration.Struct s when s.Name = "AnonPacked" -> Some s | _ -> None)
            Assert.Equal("PackedAttr", Assert.Single(record.Attributes).Kind)
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore)
            Assert.Equal("Struct 'AnonPacked' carries layout attribute 'PackedAttr' that natural alignment cannot represent; a measured layout is required.", error.Message))

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    let ``nested attributed record requires its own measured extent`` outerMeasured innerMeasured =
        let inner = {mkStruct "P" [mkField "lead" "char"; mkField "payload" "int"] None with Attributes = [mkAttr "PackedAttr" [] None]}
        let outer = mkStruct "Outer" [mkField "inner" "struct P"; mkField "tail" "int"] None
        let layouts =
            [ if innerMeasured then "P", mkLayout "P" 40 8 [0; 8]
              if outerMeasured then "Outer", mkLayout "Outer" 96 32 [0; 64] ] |> Map.ofList
        let source = attributeLayoutSource [inner; outer] layouts
        if innerMeasured || outerMeasured then
            let offsets, size, alignment, stratum = DescriptorGenerator.layoutOf source 0 outer
            Assert.Equal<int list>([0; 8], offsets)
            Assert.Equal(12, size)
            Assert.Equal(4, alignment)
            Assert.Equal((if outerMeasured then TypeMapper.Measured else TypeMapper.Declared), stratum)
        if innerMeasured then
            let output = DescriptorGenerator.structDecls source outer |> fun decls -> CodeRenderer.render (Module("Test", "test", decls))
            Assert.Contains("Repr = Repr.U8; Count = 5", output)
        else
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source outer |> ignore)
            Assert.Equal("Struct 'P' carries layout attribute 'PackedAttr' that natural alignment cannot represent; a measured layout is required.", error.Message)

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    let ``field validity precedes record layout attribute refusal`` scenario =
        let field, expected =
            match scenario with
            | 0 -> mkField "payload" "Mystery", "Struct 'P' field 'payload' has no known representation for type 'Mystery'; a complete C representation contract is required."
            | 1 -> mkBitField "payload" "int" 1, "Struct 'P' field 'payload' is a bitfield; a supported bit-precise descriptor representation is required."
            | 2 -> mkField "payload" "int[2][3]", "Struct 'P' field 'payload' has array declarator 'int[2][3]'; a supported one-dimensional trailing array declarator or a complete declarator representation contract is required."
            | _ -> {mkField "payload" "int" with IsArray = true; ArraySize = None}, "Struct 'P' field 'payload' has an array with no fixed element count; a supported dynamic array representation contract is required."
        let record = {mkStruct "P" [mkField "lead" "char"; field] None with Attributes = [mkAttr "PackedAttr" [] None]}
        let source = attributeLayoutSource [record] Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
        Assert.Equal(expected, error.Message)

    [<Fact>]
    let ``layout attribute refusal precedes the declared nesting limit`` () =
        let record = {mkStruct "P" [mkField "self" "struct P"] None with Attributes = [mkAttr "PackedAttr" [] None]}
        let source = attributeLayoutSource [record] Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 16 record |> ignore)
        Assert.Equal("Struct 'P' carries layout attribute 'PackedAttr' that natural alignment cannot represent; a measured layout is required.", error.Message)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``unclassified field attribute requires measurement at the field boundary`` measured =
        let field = {mkField "payload" "int" with Attributes = [mkAttr "FutureAttr" [] None]}
        let record = mkStruct "P" [field] None
        let source = attributeLayoutSource [record] (if measured then Map.ofList ["P", mkLayout "P" 32 32 [0]] else Map.empty)
        if measured then
            DescriptorGenerator.structDecls source record |> ignore
        else
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source record |> ignore)
            Assert.Equal("Struct 'P' field 'payload' carries attribute 'FutureAttr' with unclassified layout effect; a measured layout or a classified attribute contract is required.", error.Message)

    [<Fact>]
    let ``record attributes precede field attributes in unmeasured layout refusal`` () =
        let field = {mkField "payload" "int" with Attributes = [mkAttr "AlignedAttr" [16] None]}
        let record = {mkStruct "P" [field] None with Attributes = [mkAttr "PackedAttr" [] None]}
        let source = attributeLayoutSource [record] Map.empty
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source 0 record |> ignore)
        Assert.Equal("Struct 'P' carries layout attribute 'PackedAttr' that natural alignment cannot represent; a measured layout is required.", error.Message)

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
        // Positive outer storage reaches the intended nested representation refusal.
        let layouts = if measured then Map.ofList ["Empty", mkLayout "Empty" 0 8 []; "Mid", mkLayout "Mid" 0 8 [0]; "Outer", mkLayout "Outer" 8 8 [0]] else Map.empty
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

    // Reuse parsed declarations across layout and emission without allocating the
    // storage described by the large C arrays exercising integer boundaries.
    let private extentSource declarations =
        let records = declarations |> List.choose (function CppParser.Declaration.Struct s -> Some (s.Name, s) | _ -> None) |> Map.ofList
        let source : DescriptorGenerator.LayoutSource =
            { Structs = records; Layouts = Map.empty; Model = Types.LP64
              Shape = fun typ ->
                  if typ.StartsWith("struct ") then DescriptorGenerator.StructField(typ.Substring(7))
                  elif typ = "char" || typ = "int" then
                      DescriptorGenerator.ScalarField {Family = TypeMapper.Signed; Bits = (if typ = "char" then 8 else 32); Stratum = TypeMapper.Declared; CType = typ}
                  else DescriptorGenerator.UnknownField typ }
        source, records["Holder"]

    [<Theory>]
    [<InlineData("int values[536870912];", false, "field 'values' extent")>]
    [<InlineData("struct Inner values[536870912];", false, "field 'values' extent")>]
    [<InlineData("char bytes[2147483647]; char tail;", false, "final size")>]
    [<InlineData("char bytes[2147483647]; int tail;", false, "field 'tail' aligned offset")>]
    [<InlineData("int tag; char bytes[2147483643];", false, "final size")>]
    [<InlineData("int values[536870912];", true, "field 'values' extent")>]
    [<InlineData("struct Inner values[536870912];", true, "field 'values' extent")>]
    let ``overflowing declared byte quantities refuse before emitting a layout`` fields isUnion subject =
        let kind = if isUnion then "union" else "struct"
        withLayoutHeader $"struct Inner {{ int value; }}; {kind} Holder {{ {fields} }};\n" [] (fun declarations _ ->
            let source, root = extentSource declarations
            let expected = $"Struct 'Holder' {subject} has byte quantity 2147483648 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required."
            let calls =
                [ (fun () -> DescriptorGenerator.layoutOf source 0 root |> ignore)
                  (fun () -> DescriptorGenerator.structDecls source root |> ignore)
                  (fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore) ]
            for call in calls do
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> call ())
                Assert.Equal(expected, error.Message))

    [<Theory>]
    [<InlineData("char bytes[2147483647];", 2147483647, 1)>]
    [<InlineData("int values[536870911];", 2147483644, 4)>]
    [<InlineData("struct Inner values[536870911];", 2147483644, 4)>]
    [<InlineData("int tag; char bytes[2147483639];", 2147483644, 4)>]
    [<InlineData("char bytes[2147483639]; int tail;", 2147483644, 4)>]
    let ``representable boundary extents retain exact layout and counts`` (fields: string) expected alignment =
        withLayoutHeader $"struct Inner {{ int value; }}; struct Holder {{ {fields} }};\n" [] (fun declarations _ ->
            let source, root = extentSource declarations
            let offsets, size, align, stratum = DescriptorGenerator.layoutOf source 0 root
            Assert.Equal(expected, size)
            Assert.Equal(alignment, align)
            Assert.Equal(TypeMapper.Declared, stratum)
            let expectedOffsets =
                if fields.StartsWith("int tag") then [0; 4]
                elif fields.Contains("int tail") then [0; 2147483640]
                else [0]
            Assert.Equal<int list>(expectedOffsets, offsets)
            let direct = CodeRenderer.render (Module("Test", "", DescriptorGenerator.structDecls source root))
            let generated = FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty
            for output in [direct; generated] do
                Assert.Contains($"let Size = {expected}", output)
                if fields.StartsWith("struct Inner") then Assert.Contains("Repr = Repr.U8; Count = 2147483644", output)
                elif fields = "char bytes[2147483647];" then Assert.Contains("Count = 2147483647", output)
                elif fields = "int values[536870911];" then Assert.Contains("Count = 536870911", output))

    [<Fact>]
    let ``four gigabyte extent cannot wrap to an apparently valid zero`` () =
        withLayoutHeader "struct Holder { int values[1073741824]; };\n" [] (fun declarations _ ->
            let source, root = extentSource declarations
            for call in [ (fun () -> DescriptorGenerator.layoutOf source 0 root |> ignore)
                          (fun () -> DescriptorGenerator.structDecls source root |> ignore)
                          (fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 Map.empty |> ignore) ] do
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> call ())
                Assert.Equal("Struct 'Holder' field 'values' extent has byte quantity 4294967296 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required.", error.Message))

    [<Fact>]
    let ``representable union retains its maximum extent`` () =
        withLayoutHeader "union Holder { char bytes[2147483644]; int tag; };\n" [] (fun declarations _ ->
            let source, root = extentSource declarations
            let offsets, size, align, stratum = DescriptorGenerator.layoutOf source 0 root
            Assert.Equal<int list>([0; 0], offsets)
            Assert.Equal(2147483644, size)
            Assert.Equal(4, align)
            Assert.Equal(TypeMapper.Declared, stratum)
            let output = CodeRenderer.render (Module("Test", "", DescriptorGenerator.structDecls source root))
            Assert.Contains("Count = 2147483644", output))

    [<Fact>]
    let ``empty top level layout retains zero size`` () =
        let source, _ = depthChain 0 None
        let root = mkStruct "Empty" [] None
        let offsets, size, align, stratum = DescriptorGenerator.layoutOf source 0 root
        Assert.Empty(offsets)
        Assert.Equal(0, size)
        Assert.Equal(1, align)
        Assert.Equal(TypeMapper.Declared, stratum)

    [<Fact>]
    let ``measured parent does not bypass the nested descriptor count range`` () =
        withLayoutHeader "struct Inner { int value; }; struct Holder { struct Inner values[536870912]; };\n" [] (fun declarations _ ->
            let source, root = extentSource declarations
            // Supplied direct metadata exercises emission after the measured branch;
            // this deliberately contradictory size is not asserted to be valid C.
            let layouts = Map.ofList ["Holder", mkLayout "Holder" 128 32 [0]]
            let source = {source with Layouts = layouts}
            let _, size, _, stratum = DescriptorGenerator.layoutOf source 0 root
            Assert.Equal(16, size)
            Assert.Equal(TypeMapper.Measured, stratum)
            for call in [ (fun () -> DescriptorGenerator.structDecls source root |> ignore)
                          (fun () -> FidelityCodeGenerator.generate declarations "Test" "test" Types.LP64 layouts |> ignore) ] do
                let error = Assert.Throws<System.InvalidOperationException>(fun () -> call ())
                Assert.Equal("Struct 'Holder' field 'values' nested byte count has byte quantity 2147483648 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required.", error.Message))

    [<Fact>]
    let ``measured parent still checks the nested declared extent`` () =
        withLayoutHeader "struct Product { int values[536870912]; }; struct Holder { struct Product payload; };\n" [] (fun declarations _ ->
            let source, root = extentSource declarations
            let source = {source with Layouts = Map.ofList ["Holder", mkLayout "Holder" 128 32 [0]]}
            let _, _, _, stratum = DescriptorGenerator.layoutOf source 0 root
            Assert.Equal(TypeMapper.Measured, stratum)
            let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.structDecls source root |> ignore)
            Assert.Equal("Struct 'Product' field 'values' extent has byte quantity 2147483648 outside the supported descriptor range 0..2147483647; a wider descriptor representation contract is required.", error.Message))

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    let ``existing representation refusals precede arithmetic narrowing`` scenario =
        let source, root = depthChain 1 None
        let payload = {root.Fields.Head with IsArray = true; ArraySize = Some 536870912}
        let root, depth, expected =
            match scenario with
            | 0 ->
                {root with Fields = [payload; mkField "later" "mystery_t"]}, 0,
                "Struct 'R0' field 'later' has no known representation for type 'mystery_t'; a complete C representation contract is required."
            | 1 ->
                {root with Fields = [payload]}, 16,
                "Struct 'R0' field 'payload' nests record type 'R1' beyond the supported declared-layout depth of 16; a measured layout or a shallower complete representation contract is required."
            | _ ->
                {root with Fields = [payload; {mkField "flags" "int" with IsBitfield = true}]}, 0,
                "Struct 'R0' field 'flags' is a bitfield; a supported bit-precise descriptor representation is required."
        let error = Assert.Throws<System.InvalidOperationException>(fun () -> DescriptorGenerator.layoutOf source depth root |> ignore)
        Assert.Equal(expected, error.Message)

    [<Fact>]
    let ``wide scalar bit rounding does not wrap before extent multiplication`` () =
        let source, root = depthChain 0 None
        let source = {source with Shape = fun typ -> DescriptorGenerator.ScalarField {Family = TypeMapper.Unsigned; Bits = 2147483647; Stratum = TypeMapper.Declared; CType = typ}}
        let _, size, align, _ = DescriptorGenerator.layoutOf source 0 root
        Assert.Equal(268435456, size)
        Assert.Equal(268435456, align)
        // The unsupported repr tag still belongs to the downstream vocabulary validator.
        let output = CodeRenderer.render (Module("Test", "", DescriptorGenerator.structDecls source root))
        Assert.Contains("Repr = \"u2147483647\"", output)

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
