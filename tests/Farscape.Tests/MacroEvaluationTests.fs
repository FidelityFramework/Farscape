module Farscape.Tests.MacroEvaluationTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Object macros as clang evaluates them (#193, #192): a composed integer or an alias is emitted
// with the value clang computes, a function-pointer cast is recognized as a sentinel, and every
// other macro the pilot's macro_prefixes select is reported by name and kind. None is dropped.
// ═══════════════════════════════════════════════════════════════════

let private parseMacros (header: string) (prefixes: string list) : CppParser.MacroDecl list =
    let options =
        { CppParser.defaultOptions header with
            IncludeMacros = true; MacroPrefixes = prefixes; CppMode = false }
    match CppParser.parseHeaderFull options with
    | Ok result -> result.Macros
    | Error error -> failwithf "%s did not parse: %s" header error

/// The constants generation emits for `macros`, by name, read from the generated source.
let private emitted (macros: CppParser.MacroDecl list) : Map<string, string> * string =
    let source = FidelityCodeGenerator.generate (macros |> List.map CppParser.Declaration.Macro) "Test.Macros" "test" Types.LP64 Map.empty
    let literal = System.Text.RegularExpressions.Regex(@"^\s*let (\w+) = (.+)$")
    let lines = source.Split('\n')
    let constants =
        lines |> Array.indexed |> Array.choose (fun (i, line) ->
            let m = literal.Match line
            if m.Success && i > 0 && lines.[i - 1].Trim() = "[<Literal>]" then Some (m.Groups.[1].Value, m.Groups.[2].Value.Trim()) else None)
        |> Map.ofArray
    constants, source

/// The `#define` names `clang -dM -E` lists for `header` that start with `prefix`, read
/// independently of Farscape's macro reader.
let private clangMacroNames (header: string) (prefix: string) : Set<string> =
    let info = System.Diagnostics.ProcessStartInfo("clang")
    for arg in [ "-dM"; "-E"; "-x"; "c"; header ] do info.ArgumentList.Add arg
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use proc = System.Diagnostics.Process.Start info
    let output = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)
    output.Split('\n')
    |> Array.choose (fun line ->
        if not (line.StartsWith "#define ") then None
        else
            let name = line.Substring(8).Split([| ' '; '(' |]).[0]
            if name.StartsWith prefix then Some name else None)
    |> Set.ofArray

let private composedHeader = fixturePath "macros/composed.h"

[<Fact>]
let ``each object macro carries the kind and value clang evaluates for it`` () =
    let macros = parseMacros composedHeader [ "FX_" ] |> List.map (fun m -> m.Name, m.Kind) |> Map.ofList
    Assert.Equal(CppParser.SimpleValue "10", macros.["FX_BASE"])
    Assert.Equal(CppParser.IntegerConstant ("(FX_BASE | (1<<8))", 266L), macros.["FX_COMPOSED"])
    Assert.Equal(CppParser.IntegerConstant ("FX_COMPOSED", 266L), macros.["FX_ALIAS"])
    Assert.Equal(CppParser.IntegerConstant ("(-(FX_BASE * 3))", -30L), macros.["FX_NEGATIVE"])
    Assert.Equal(CppParser.StringConstant ("\"fixture\"", "char[8]"), macros.["FX_NAME"])
    Assert.Equal(CppParser.FunctionPointerSentinel ("((fx_destructor_type)0)", "fx_destructor_type", "void (*)(void *)", 0L), macros.["FX_STATIC"])
    Assert.Equal(CppParser.FunctionPointerSentinel ("((fx_destructor_type)-1)", "fx_destructor_type", "void (*)(void *)", -1L), macros.["FX_TRANSIENT"])
    Assert.Equal(CppParser.SimpleValue "", macros.["FX_API"])
    Assert.Equal(CppParser.Unevaluated ("1.5", "clang: integer constant expression must have integer type, not 'double'"), macros.["FX_RATIO"])
    Assert.Equal(CppParser.Unevaluated ("(0xFFFFFFFFFFFFFFFFULL)", "clang evaluates it to 18446744073709551615, outside the signed 64-bit carrier of a constant"), macros.["FX_HUGE"])
    Assert.Equal(CppParser.Unevaluated ("{ 0 }", "its definition holds a brace, a semicolon or unbalanced parentheses, so it is no expression clang can evaluate"), macros.["FX_BLOCK"])
    Assert.Equal(CppParser.TypeCast ("struct fx_registers", "0x40000000"), macros.["FX_REGISTERS"])
    match macros.["FX_MAX"] with
    | CppParser.FunctionLike (["a"; "b"], _) -> ()
    | other -> failwithf "FX_MAX is %A" other

[<Fact>]
let ``composed macros are emitted with clang's value and every other macro is reported by name and kind`` () =
    let macros = parseMacros composedHeader [ "FX_" ]
    let constants, source = emitted macros
    Assert.Equal<Map<string, string>>(Map.ofList [ "FX_BASE", "10"; "FX_COMPOSED", "266"; "FX_ALIAS", "266"; "FX_NEGATIVE", "-30" ], constants)
    let reported =
        FidelityCodeGenerator.unemittedMacros (macros |> List.map CppParser.Declaration.Macro)
        |> List.map (fun u -> u.Name, FidelityCodeGenerator.describeUnemittedMacroKind u.Kind)
        |> Map.ofList
    let expected =
        Map.ofList [
            "FX_NAME", "string literal (char[8])"
            "FX_API", "empty definition (linkage or attribute marker)"
            "FX_MAX", "function-like macro"
            "FX_RATIO", "no integer constant: clang: integer constant expression must have integer type, not 'double'"
            "FX_HUGE", "no integer constant: clang evaluates it to 18446744073709551615, outside the signed 64-bit carrier of a constant"
            "FX_BLOCK", "no integer constant: its definition holds a brace, a semicolon or unbalanced parentheses, so it is no expression clang can evaluate"
            "FX_STATIC", "function-pointer sentinel (fx_destructor_type)0, a constant_parameters value for a parameter of that type"
            "FX_TRANSIENT", "function-pointer sentinel (fx_destructor_type)-1, a constant_parameters value for a parameter of that type"
            "FX_REGISTERS", "cast to data pointer type struct fx_registers*" ]
    Assert.Equal<Map<string, string>>(expected, reported)
    // The generated module reports each, by name and kind.
    for name, kind in Map.toList expected do
        Assert.Contains($"//   {name}: {kind}", source)
    // Every macro clang lists is emitted or reported, never both, never neither.
    let listed = clangMacroNames composedHeader "FX_"
    Assert.Equal<Set<string>>(listed, Set.union (constants |> Map.keys |> Set.ofSeq) (reported |> Map.keys |> Set.ofSeq))
    Assert.Empty(Set.intersect (constants |> Map.keys |> Set.ofSeq) (reported |> Map.keys |> Set.ofSeq))

let private sqliteHeader = "/usr/include/sqlite3.h"

[<Fact>]
let ``sqlite3.h extended result codes are emitted with clang's values and no SQLITE_ macro is dropped`` () =
    Assert.True(File.Exists sqliteHeader, $"{sqliteHeader} is required: the SQLite development header is the #193 input")
    let macros = parseMacros sqliteHeader [ "SQLITE_" ]
    let constants, source = emitted macros
    // (SQLITE_IOERR | (1<<8)), (SQLITE_CONSTRAINT | (8<<8)) and an alias of another macro.
    Assert.Equal("266", constants.["SQLITE_IOERR_READ"])
    Assert.Contains("let SQLITE_IOERR_READ = 266\n", source)
    Assert.Equal("2067", constants.["SQLITE_CONSTRAINT_UNIQUE"])
    Assert.Equal(constants.["SQLITE_FCNTL_LAST_ERRNO"], constants.["SQLITE_LAST_ERRNO"])
    let unemitted = FidelityCodeGenerator.unemittedMacros (macros |> List.map CppParser.Declaration.Macro)
    let reported = unemitted |> List.map _.Name |> Set.ofList
    let listed = clangMacroNames sqliteHeader "SQLITE_"
    let emittedNames = constants |> Map.keys |> Set.ofSeq
    // Every SQLITE_ macro clang lists is emitted or reported by name, never both.
    Assert.Equal<Set<string>>(listed, Set.union emittedNames reported)
    Assert.Empty(Set.intersect emittedNames reported)
    // The report: the string literals, the two destructor sentinels and the empty markers.
    let kinds = unemitted |> List.map (fun u -> u.Name, u.Kind) |> Map.ofList
    Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.FunctionPointerSentinel ("sqlite3_destructor_type", 0L), kinds.["SQLITE_STATIC"])
    Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.FunctionPointerSentinel ("sqlite3_destructor_type", -1L), kinds.["SQLITE_TRANSIENT"])
    match kinds.["SQLITE_VERSION"] with
    | FidelityCodeGenerator.UnemittedMacroKind.StringLiteral _ -> ()
    | other -> failwithf "SQLITE_VERSION is reported as %A" other
    Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.EmptyDefinition, kinds.["SQLITE_API"])
    let count kind = unemitted |> List.filter (fun u -> kind u.Kind) |> List.length
    Assert.Equal(5, count (function FidelityCodeGenerator.UnemittedMacroKind.StringLiteral _ -> true | _ -> false))
    Assert.Equal(2, count (function FidelityCodeGenerator.UnemittedMacroKind.FunctionPointerSentinel _ -> true | _ -> false))
    Assert.Equal(7, count (function FidelityCodeGenerator.UnemittedMacroKind.EmptyDefinition -> true | _ -> false))
    // The issue's nine linkage markers: the seven empty ones, `extern`, and an alias of an empty one.
    Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.NotIntegerConstant "clang: expected expression", kinds.["SQLITE_EXTERN"])
    Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.NotIntegerConstant "clang: expected expression", kinds.["SQLITE_STDCALL"])
    Assert.Equal(16, unemitted.Length)
    Assert.Equal(494, emittedNames.Count)
    for u in unemitted do
        Assert.Contains($"//   {u.Name}: {FidelityCodeGenerator.describeUnemittedMacroKind u.Kind}", source)

let private unitFirst = fixturePath "macros/unit_first.h"
let private unitSecond = fixturePath "macros/unit_second.h"

[<Fact>]
let ``a composed macro with the same text that two units evaluate differently is refused`` () =
    // (UNIT_PRIVATE + 7) is 6 in the first unit and 7 in the second: the text agrees, clang's
    // evaluations do not, so no unit's value may stand for the pilot's.
    withPilotSections [ unitFirst; unitSecond ] [ "macro_prefixes = [\"CODE_\"]" ] [ "c_header_mode = true" ] [ "unit_bind" ] [] [] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9101: macro 'CODE_SHIFTED' is defined differently by the pilot's headers: '(UNIT_PRIVATE + 7)' (clang evaluates 6 in that unit) in '{unitFirst}'; '(UNIT_PRIVATE + 7)' (clang evaluates 7 in that unit) in '{unitSecond}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories)))

[<Fact>]
let ``macros only one unit defines are emitted with that unit's evaluation`` () =
    let bindings = [ "c_header_mode = true"; "[[options.bindings]]"; "name = \"unit_bind\""; "string_parameters = [0]"; "constant_parameters = [\"\", \"LOCAL_SECOND_ONLY\"]" ]
    withPilotSections [ unitFirst; unitSecond ] [ "macro_prefixes = [\"LOCAL_\"]" ] bindings [ "unit_bind" ] [] [] (fun result _ ->
        match result with
        | Ok generated ->
            let reported = generated.UnemittedMacros |> List.map (fun u -> u.Name, u.Kind) |> Map.ofList
            Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.FunctionPointerSentinel ("unit_destructor", -1L), reported.["LOCAL_FIRST_ONLY"])
            Assert.Equal(FidelityCodeGenerator.UnemittedMacroKind.FunctionPointerSentinel ("unit_destructor", -1L), reported.["LOCAL_SECOND_ONLY"])
        | Error error -> failwithf "unit-local macros were refused: %s" error)
