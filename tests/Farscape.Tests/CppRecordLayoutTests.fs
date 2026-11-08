module Farscape.Tests.CppRecordLayoutTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// C++ records (issue #182): the C++ record path keeps unnamed bit-fields (#176) and the typedef
// attributes its fields' types reach (#175) as the C path does. A class carrying either is
// measured by clang in its declaring unit, and its size is clang's; a class clang cannot
// measure is refused with the C path's code and its exact reason. A class whose only member is
// an unnamed bit-field (#183) is measured, never opaque.
// ═══════════════════════════════════════════════════════════════════

let private parseCpp (relative: string) =
    match CppParser.parseHeader { HeaderFile = fixturePath relative; IncludePaths = []; SystemIncludePaths = []; Defines = []; Verbose = false
                                  IncludeMacros = false; MacroPrefixes = []; IncludeRoot = None; CppMode = true } with
    | Ok declarations -> declarations
    | Error reason -> failwithf "%s did not parse: %s" relative reason

let private classIn (declarations: CppParser.Declaration list) name =
    declarations |> List.pick (function CppParser.Declaration.Class c when c.Name = name -> Some c | _ -> None)

/// Clang's measurement in bytes: size, alignment, every member offset (unnamed bit-fields
/// included), and the fields' offsets.
let private measured (c: CppParser.ClassDecl) =
    match c.Measured with
    | Some m ->
        m.SizeBits / 8, m.AlignmentBits / 8, m.FieldOffsetsBits |> List.map (fun bits -> bits / 8),
        CppParser.classFieldOffsetsBits c m |> List.map (fun bits -> bits / 8)
    | None -> failwithf "%s carries no clang measurement" c.Name

let private padding (c: CppParser.ClassDecl) = c.UnnamedBitFields |> List.map (fun b -> b.Position, b.Type, b.Width)

let private noOutput (output: string) =
    Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))

[<Fact>]
let ``C++ records keep unnamed bit-fields and typedef attributes and carry clang's layout`` () =
    let header = fixturePath "cpp-records/measured.hpp"
    let declarations = parseCpp "cpp-records/measured.hpp"
    let pad = classIn declarations "fx_cpp_pad"
    Assert.Equal<string list>(["a"; "b"], pad.Fields |> List.map _.Name)
    Assert.Equal<(int * string * int option) list>([1, "int", Some 3], padding pad)
    Assert.Equal((3, 1, [0; 1; 2], [0; 2]), measured pad)
    let wide = classIn declarations "fx_cpp_wide"
    Assert.Empty(wide.UnnamedBitFields)
    Assert.Equal<CppParser.TypedefAttribute list>(
        [ ({ Site = CppParser.FieldType "value"; Typedef = "fx_cpp_wide_int"; Attribute = mkAttr "AlignedAttr" [8] None } : CppParser.TypedefAttribute) ],
        wide.TypedefAttributes)
    Assert.Equal((16, 8, [0; 8], [0; 8]), measured wide)
    // A base subobject lies inside the measured size; the offsets are the class's own members'.
    let derived = classIn declarations "fx_cpp_derived"
    Assert.Equal<(int * string * int option) list>([1, "int", Some 0], padding derived)
    Assert.Equal((12, 4, [4; 8; 8], [4; 8]), measured derived)
    // A class of only an unnamed bit-field, named by its namespace (#183).
    let only = classIn declarations "only"
    Assert.Equal(Some "fx::only", only.QualifiedName)
    Assert.Empty(only.Fields)
    Assert.Equal<(int * string * int option) list>([0, "int", Some 8], padding only)
    Assert.Equal((1, 1, [0], []), measured only)
    // A class carrying neither is not measured.
    Assert.Equal(None, (classIn declarations "base").Measured)
    // The class analysis takes clang's size, not an estimate from the visible field types, and
    // a class of only padding is storage, not opaque.
    Assert.Equal(CppClassAnalysis.PODClass 3, CppClassAnalysis.classifyClass pad)
    Assert.Equal(CppClassAnalysis.PODClass 16, CppClassAnalysis.classifyClass wide)
    Assert.Equal(CppClassAnalysis.PODClass 12, CppClassAnalysis.classifyClass derived)
    Assert.Equal(CppClassAnalysis.PODClass 1, CppClassAnalysis.classifyClass only)
    clangConfirmsCpp header [
        "sizeof(fx_cpp_pad) == 3"; "alignof(fx_cpp_pad) == 1"; "offsetof(fx_cpp_pad, b) == 2"
        "sizeof(fx_cpp_wide) == 16"; "alignof(fx_cpp_wide) == 8"; "offsetof(fx_cpp_wide, value) == 8"
        "sizeof(fx_cpp_derived) == 12"; "alignof(fx_cpp_derived) == 4"
        "sizeof(fx::only) == 1"; "alignof(fx::only) == 1" ]

[<Fact>]
let ``a C++ record carrying an unnamed bit-field that clang cannot measure is refused`` () =
    let header = fixturePath "cpp-records/hidden-pad.hpp"
    let record = classIn (parseCpp "cpp-records/hidden-pad.hpp") "fx_cpp_hidden_pad"
    Assert.Equal<(int * string * int option) list>([1, "int", Some 0], padding record)
    Assert.Equal(None, record.Measured)
    withPilot [header] [] [] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9129: C++ record 'fx_cpp_hidden_pad' carries member 2, the unnamed bit-field 'int : 0', which moves bytes natural alignment of the declared field types cannot place, and clang has no measurement of 'fx_cpp_hidden_pad' in its declaring unit; an unnamed bit-field is never dropped from its record.",
            result |> Result.map ignore)
        noOutput output)

[<Fact>]
let ``a C++ record whose field type reaches an attributed typedef that clang cannot measure is refused`` () =
    let header = fixturePath "cpp-records/hidden-typedef.hpp"
    let record = classIn (parseCpp "cpp-records/hidden-typedef.hpp") "fx_cpp_hidden_wide"
    Assert.Equal<CppParser.TypedefAttribute list>(
        [ ({ Site = CppParser.FieldType "value"; Typedef = "fx_cpp_hidden_int"; Attribute = mkAttr "AlignedAttr" [8] None } : CppParser.TypedefAttribute) ],
        record.TypedefAttributes)
    Assert.Equal(None, record.Measured)
    withPilot [header] [] [] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9127: C++ record 'fx_cpp_hidden_wide' field 'value' has a type reaching typedef 'fx_cpp_hidden_int', which carries attribute 'AlignedAttr', and clang has no measurement of 'fx_cpp_hidden_wide' in its declaring unit; natural alignment of the declared field types never stands in for the typedef's attribute.",
            result |> Result.map ignore)
        noOutput output)
