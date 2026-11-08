module Farscape.Tests.BindingGeneratorTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Macro agreement across a pilot's translation units (issue #56)
// ═══════════════════════════════════════════════════════════════════

let private macro name value : CppParser.Declaration =
    CppParser.Declaration.Macro { Name = name; Kind = CppParser.SimpleValue value; RawValue = value; Documentation = None }

[<Fact>]
let ``identical macro definitions from several headers merge silently`` () =
    Assert.Equal(Ok (), BindingGenerator.agreeMacros [ "a.h", false, [macro "X" "1"; macro "Y" "2"]; "b.h", false, [macro "X" "1"]; "c.h", false, [] ])

[<Fact>]
let ``a macro defined differently by two headers is refused by name, value and header`` () =
    let headers = [ "a.h", false, [macro "X" "1"]; "b.h", false, [macro "X" "2"]; "c.h", false, [macro "X" "1"; macro "Y" "3"] ]
    Assert.Equal(
        Error "FS9101: macro 'X' is defined differently by the pilot's headers: '1' in 'a.h', 'c.h'; '2' in 'b.h'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeMacros headers |> refusalText))

[<Fact>]
let ``function-like macros agree only when parameters and replacement agree`` () =
    let fn name args body : CppParser.Declaration =
        CppParser.Declaration.Macro { Name = name; Kind = CppParser.FunctionLike (args, body); RawValue = body; Documentation = None }
    Assert.Equal(Ok (), BindingGenerator.agreeMacros [ "a.h", false, [fn "F" ["x"] "((x)+1)"]; "b.h", false, [fn "F" ["x"] "((x)+1)"] ])
    Assert.Equal(
        Error "FS9101: macro 'F' is defined differently by the pilot's headers: '(x) ((x)+1)' in 'a.h'; '(y) ((x)+1)' in 'b.h'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeMacros [ "a.h", false, [fn "F" ["x"] "((x)+1)"]; "b.h", false, [fn "F" ["y"] "((x)+1)"] ] |> refusalText))

[<Fact>]
let ``macro definitions are compared as preprocessing tokens`` () =
    Assert.Equal<string list>(["1"; "+"; "2"], BindingGenerator.macroTokens false "1 + 2")
    Assert.Equal<string list>(["1"; "+"; "2"], BindingGenerator.macroTokens false "1+2")
    Assert.Equal<string list>(["("; "a"; "<<="; "0x1p-3"; ")"; "##"; "\"x y\""], BindingGenerator.macroTokens false "(a<<=0x1p-3) ## \"x y\"")
    // Insignificant whitespace between tokens does not make definitions differ.
    Assert.Equal(Ok (), BindingGenerator.agreeMacros [ "a.h", false, [macro "X" "1 + 2"]; "b.h", false, [macro "X" "1+2"] ])
    // Different spellings of one value are not assumed equivalent.
    Assert.Equal(
        Error "FS9101: macro 'X' is defined differently by the pilot's headers: '0x10' in 'a.h'; '16' in 'b.h'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeMacros [ "a.h", false, [macro "X" "0x10"]; "b.h", false, [macro "X" "16"] ] |> refusalText))
    // Whitespace inside a string literal is part of the token.
    Assert.Equal(
        Error "FS9101: macro 'S' is defined differently by the pilot's headers: '\"a b\"' in 'a.h'; '\"a  b\"' in 'b.h'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeMacros [ "a.h", false, [macro "S" "\"a b\""]; "b.h", false, [macro "S" "\"a  b\""] ] |> refusalText))

[<Fact>]
let ``prefixed literals are one preprocessing token`` () =
    Assert.Equal<string list>(["L\"x\""], BindingGenerator.macroTokens false "L\"x\"")
    Assert.Equal<string list>(["L"; "\"x\""], BindingGenerator.macroTokens false "L \"x\"")
    Assert.Equal<string list>(["u8\"y\""; "u\"z\""; "U\"w\""; "L'a'"; "u8'b'"; "u'c'"; "U'd'"], BindingGenerator.macroTokens false "u8\"y\" u\"z\" U\"w\" L'a' u8'b' u'c' U'd'")
    // C++ raw strings keep quotes and delimiters inside one token; a ud-suffix attaches.
    Assert.Equal<string list>(["R\"d(a \"b\" c)d\""; "uR\"(x)\""; "\"s\"_k"; "::"], BindingGenerator.macroTokens true "R\"d(a \"b\" c)d\" uR\"(x)\" \"s\"_k ::")
    // An identifier that is not an encoding prefix stays separate from the literal after it.
    Assert.Equal<string list>(["Lx"; "\"x\""], BindingGenerator.macroTokens false "Lx\"x\"")
    Assert.Equal(
        Error "FS9101: macro 'W' is defined differently by the pilot's headers: 'L\"x\"' in 'a.h'; 'L \"x\"' in 'b.h'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeMacros [ "a.h", false, [macro "W" "L\"x\""]; "b.h", false, [macro "W" "L \"x\""] ] |> refusalText))
    Assert.Equal(Ok (), BindingGenerator.agreeMacros [ "a.h", false, [macro "W" "( L'x' , u8\"y\" )"]; "b.h", false, [macro "W" "(L'x',u8\"y\")"] ])

[<Fact>]
let ``pp-numbers follow the unit's language`` () =
    // C++: a ' digit separator continues the pp-number; separated, it opens a character literal.
    Assert.Equal<string list>(["1'2'3"], BindingGenerator.macroTokens true "1'2'3")
    Assert.Equal<string list>(["1"; "'2'"; "3"], BindingGenerator.macroTokens true "1 '2' 3")
    Assert.Equal<string list>(["("; "1'000"; "+"; "0x1'0p-1"; ")"], BindingGenerator.macroTokens true "( 1'000 + 0x1'0p-1 )")
    Assert.Equal<string list>(["1e+5"; "0x1p-3"; "10ULL"; "1.5e-3f"], BindingGenerator.macroTokens true "1e+5 0x1p-3 10ULL 1.5e-3f")
    // C: no separators, so ' always opens a character literal, and the two spellings are one sequence.
    Assert.Equal<string list>(["1"; "'2'"; "3"], BindingGenerator.macroTokens false "1'2'3")
    Assert.Equal<string list>(["1"; "'2'"; "3"], BindingGenerator.macroTokens false "1 '2' 3")
    // C has no raw strings or ud-suffixes.
    Assert.Equal<string list>(["R"; "\"(x)\""; "\"s\""; "_k"], BindingGenerator.macroTokens false "R\"(x)\" \"s\"_k")
    Assert.Equal(
        Error "FS9101: macro 'N' is defined differently by the pilot's headers: '1'2'3' in 'a.hpp'; '1 '2' 3' in 'b.hpp'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeMacros [ "a.hpp", true, [macro "N" "1'2'3"]; "b.hpp", true, [macro "N" "1 '2' 3"] ] |> refusalText))
    Assert.Equal(Ok (), BindingGenerator.agreeMacros [ "a.h", false, [macro "N" "1'2'3"]; "b.h", false, [macro "N" "1 '2' 3"] ])

[<Fact>]
let ``clang-parsed wide literal and separated prefix are refused`` () =
    let joined = fixturePath "macro-divergence/wide-joined.h"
    let split = fixturePath "macro-divergence/wide-split.h"
    withPilot [joined; split] ["c_header_mode = true"] ["fx_wide_joined"; "fx_wide_split"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9101: macro 'FX_WIDE' is defined differently by the pilot's headers: 'L\"x\"' in '{joined}'; 'L \"x\"' in '{split}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``clang-parsed macros differing only in whitespace between tokens generate`` () =
    withPilot [fixturePath "macro-divergence/spaced.h"; fixturePath "macro-divergence/unspaced.h"] ["c_header_mode = true"] ["fx_spaced"; "fx_unspaced"] (fun result _ ->
        match result with
        | Ok generated -> Assert.NotEmpty generated.OutputFiles
        | Error error -> failwithf "Token-identical macro definitions were refused: %s" error)

[<Fact>]
let ``clang-parsed macros spelling one value differently are refused`` () =
    let first = fixturePath "macro-divergence/first.h"
    let hex = fixturePath "macro-divergence/hex.h"
    withPilot [first; hex] ["c_header_mode = true"] ["fx_first"; "fx_hex"] (fun result _ ->
        Assert.Equal(
            Error $"Binding generation failed: FS9101: macro 'FX_LIMIT' is defined differently by the pilot's headers: '16' in '{first}'; '0x10' in '{hex}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore))

[<Fact>]
let ``clang-parsed headers with the same macro definitions generate`` () =
    let first = fixturePath "macro-divergence/first.h"
    let same = fixturePath "macro-divergence/same.h"
    withPilot [first; same] ["c_header_mode = true"] ["fx_first"; "fx_same"] (fun result _ ->
        match result with
        | Ok generated -> Assert.NotEmpty generated.OutputFiles
        | Error error -> failwithf "Identical macro definitions were refused: %s" error)

[<Fact>]
let ``clang-parsed headers with a divergent macro are refused before any output`` () =
    let first = fixturePath "macro-divergence/first.h"
    let differs = fixturePath "macro-divergence/differs.h"
    withPilot [first; differs] ["c_header_mode = true"] ["fx_first"; "fx_differs"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9101: macro 'FX_LIMIT' is defined differently by the pilot's headers: '16' in '{first}'; '32' in '{differs}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// Field types resolve through the declaring unit's typedefs (issue #54)
// ═══════════════════════════════════════════════════════════════════

let private typesOf (output: string) =
    System.IO.File.ReadAllLines(System.IO.Path.Combine(output, "Types.clef")) |> Array.map (fun line -> line.Trim())

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``retention and borrowed results resolve selected anchors in another namespace`` includeExtent =
    let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../..", "test-results", "scratch", "cross-namespace", System.Guid.NewGuid().ToString("N")))
    System.IO.Directory.CreateDirectory(directory) |> ignore
    try
        let header = System.IO.Path.Combine(directory, "anchors.h")
        System.IO.File.WriteAllText(header, "typedef struct fx_owner fx_owner;\ntypedef void (*fx_callback)(void *, int);\nvoid fx_register(fx_owner *owner, fx_callback callback, void *userdata);\nconst char *fx_text(fx_owner *owner);\nint fx_length(fx_owner *owner);\nvoid fx_release(fx_owner *owner);\n")
        let bindings =
            [ "c_header_mode = true"
              "[[options.bindings]]"; "name = \"fx_register\""
              "callback_invocations = [ { parameter = 1, identity = \"fx.callback\", version = \"1\", source = \"fixture\", scope = \"retained\", absence_hypothesis = { identity = \"fx.absent\", version = \"1\", source = \"fixture\" }, context = { argument = 0, parameter = 2 }, retention = { owner_parameter = 0, releases = [ { binding = \"Test.Release.fx_release\", parameter = 0 } ] } } ]"
              "[[options.bindings]]"; "name = \"fx_text\""
              "borrowed_result = { content = \"text\", owner_parameter = 0, extent = { binding = \"Test.Release.fx_length\", arguments = [0] }, invalidated_by = [ { binding = \"Test.Release.fx_release\", parameter = 0 } ], hypothesis = { identity = \"fx.text\", version = \"1\", source = \"fixture\" } }" ]
        let otherFunctions = if includeExtent then "\"fx_length\", \"fx_release\"" else "\"fx_release\""
        let other = [ "[[namespace]]"; "name = \"Test.Release\""; "description = \"release\""; "library = \"fixture\""; $"functions = [{otherFunctions}]" ]
        withPilotGenerating (fun project -> BindingGenerator.generateFromProject project false true Types.LP64)
            [header] [] bindings ["fx_register"; "fx_text"] other [] (fun result output ->
                match includeExtent, result with
                | false, Error error ->
                    Assert.Contains("FS9141", error)
                    Assert.Contains("Test.Release.fx_length", error)
                    Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories))
                | false, Ok _ -> failwith "an unselected extent must be refused"
                | true, Error error -> failwithf "cross-namespace generation failed: %s" error
                | true, Ok _ ->
                    let text = System.IO.Directory.GetFiles(output, "*.clef", System.IO.SearchOption.AllDirectories) |> Array.map System.IO.File.ReadAllText |> String.concat "\n"
                    Assert.Contains("OwnerRelease ({ Binding = \"Test.Release.fx_release\"; Parameter = 0 })", text)
                    Assert.Contains("CompanionCall (\"Test.Release.fx_length\", [|0|])", text)
                    Assert.Contains("OwnerCall ({ Binding = \"Test.Release.fx_release\"; Parameter = 0 })", text))
    finally
        System.IO.Directory.Delete(directory, true)

[<Theory>]
[<InlineData(false, 16, 8)>]
[<InlineData(true, 8, 4)>]
let ``measured layout uses the header's actual parse language`` forceC expectedSize expectedOffset =
    let directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../..", "test-results", "scratch", "layout-language", System.Guid.NewGuid().ToString("N")))
    System.IO.Directory.CreateDirectory(directory) |> ignore
    try
        let header = System.IO.Path.Combine(directory, "language.hpp")
        System.IO.File.WriteAllText(header, "#ifdef __cplusplus\nnamespace fx_marker {}\n#endif\nstruct fx_language { char prefix;\n#ifdef __cplusplus\nlong value;\n#else\nint value;\n#endif\n};\nvoid fx_inspect(struct fx_language *value);\n")
        let parserOptions = { CppParser.defaultOptions header with CppMode = not forceC }
        let declarations =
            match CppParser.parseHeader parserOptions with
            | Ok value -> value
            | Error error -> failwithf "Header parsing failed: %s" error
        let fields =
            declarations |> List.pick (function
                | CppParser.Declaration.Struct record when record.Name = "fx_language" -> Some record.Fields
                | CppParser.Declaration.Class record when record.Name = "fx_language" -> Some record.Fields
                | _ -> None)
        let field = fields |> List.find (fun field -> field.Name = "value")
        Assert.Equal((if forceC then "int" else "long"), field.Type)
        let layouts =
            match CppParser.extractStructLayoutsForModeDiagnosed header [] [] [] ["struct fx_language"] (not forceC) false with
            | Ok value -> value
            | Error error -> failwithf "Layout measurement failed: %s" error.Text
        Assert.Equal(expectedSize * 8, layouts.["fx_language"].SizeBits)
        Assert.Equal<int list>([0; expectedOffset * 8], layouts.["fx_language"].FieldOffsetsBits)
        let options = ["abi_critical_structs = [\"struct fx_language\"]"] @ (if forceC then ["c_header_mode = true"] else [])
        withPilot [header] options ["fx_inspect"] (fun result output ->
            match forceC, result with
            | false, Error error -> Assert.Contains("FS9185", error)
            | false, Ok _ -> failwith "Expected the unsupported C++ record to be refused"
            | true, Error error -> failwithf "Layout generation failed: %s" error
            | true, Ok _ ->
                let text = typesOf output |> String.concat "\n"
                Assert.Contains("layout measured", text)
                Assert.Contains($"let Size = {expectedSize}", text)
                Assert.Contains($"let valueOffset = {expectedOffset}", text))
    finally
        System.IO.Directory.Delete(directory, true)

[<Fact>]
let ``a unit declaring dev_t as unsigned short is refused naming both widths`` () =
    let header = fixturePath "typedef-profile/short-dev.h"
    withPilot [header] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_rec\"]"] [] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9102: typedef 'dev_t' is declared by '{header}' as 'unsigned short' (unsigned 16-bit), but the LP64 profile row for 'dev_t' is unsigned 64-bit; the unit's declaration and the profile must agree.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a unit declaring dev_t as the profile does generates the measured 64-bit field`` () =
    let header = fixturePath "typedef-profile/long-dev.h"
    withPilot [header] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_rec\"]"] [] (fun result output ->
        match result with
        | Error error -> failwithf "An agreeing dev_t declaration was refused: %s" error
        | Ok _ ->
            let types = typesOf output
            Assert.Contains("/// C struct `fx_rec`: layout measured (clang record layout dump).", types)
            Assert.Contains("let Size = 24", types)
            Assert.Contains("let flagsOffset = 8", types)
            let text = String.concat "\n" types
            Assert.Contains("{ Name = \"device\"; Offset = 0; Repr = Repr.U64; Count = 1;", text)
            Assert.Contains("repr declared: dev_t under LP64", text))

[<Fact>]
let ``each record's fields resolve through its own unit's typedefs`` () =
    let narrow = fixturePath "typedef-profile/word16.h"
    let wide = fixturePath "typedef-profile/word32.h"
    withPilot [narrow; wide] [] ["fx_narrow_read"; "fx_wide_read"] (fun result output ->
        match result with
        | Error error -> failwithf "Unit-local typedefs were refused: %s" error
        | Ok _ ->
            let text = typesOf output |> String.concat "\n"
            let narrowAt = text.IndexOf("module fx_narrow")
            let wideAt = text.IndexOf("module fx_wide")
            Assert.True(narrowAt >= 0 && wideAt > narrowAt, text)
            // The merged map keeps word16.h's fx_word; fx_wide is declared by word32.h's unit.
            Assert.Contains("Name = \"value\"; Offset = 0; Repr = Repr.U16;", text.Substring(narrowAt, wideAt - narrowAt))
            Assert.Contains("Name = \"value\"; Offset = 0; Repr = Repr.U32;", text.Substring(wideAt)))

[<Fact>]
let ``a second unit declaring dev_t differently is refused naming that unit`` () =
    let first = fixturePath "typedef-profile/unit-long.h"
    let second = fixturePath "typedef-profile/unit-short.h"
    let expected = $"Binding generation failed: FS9102: typedef 'dev_t' is declared by '{second}' as 'unsigned short' (unsigned 16-bit), but the LP64 profile row for 'dev_t' is unsigned 64-bit; the unit's declaration and the profile must agree."
    // Whether or not the second unit's function is selected, its unit disagrees with the profile.
    for functions in [ ["fx_first"; "fx_second"]; ["fx_first"] ] do
        withPilot [first; second] ["c_header_mode = true"] functions (fun result output ->
            Assert.Equal(Error expected, result |> Result.map ignore)
            Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a unit declaring dev_t with the profile width but opposite signedness is refused`` () =
    let header = fixturePath "typedef-profile/signed-dev.h"
    withPilot [header] ["c_header_mode = true"] ["fx_signed_dev"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9102: typedef 'dev_t' is declared by '{header}' as 'long' (signed 64-bit), but the LP64 profile row for 'dev_t' is unsigned 64-bit; the unit's declaration and the profile must agree.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``each function's signature resolves through its own unit's typedefs`` () =
    let narrow = fixturePath "typedef-profile/word16.h"
    let wide = fixturePath "typedef-profile/word32.h"
    withPilot [narrow; wide] ["c_header_mode = true"] ["fx_narrow_take"; "fx_wide_take"] (fun result output ->
        match result with
        | Error error -> failwithf "Unit-local function typedefs were refused: %s" error
        | Ok _ ->
            let source = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))
            let descriptor name =
                let at = source.IndexOf($"let {name}Descriptor")
                Assert.True(at >= 0, source)
                source.Substring(at, source.IndexOf("@>", at) - at)
            // The merged map keeps word16.h's fx_word; fx_wide_take is declared by word32.h's unit.
            Assert.Contains("{ Name = \"value\"; Type = Integer (Unsigned, 16); PassBy = Value }", descriptor "fx_narrow_take")
            Assert.Contains("{ Name = \"value\"; Type = Integer (Unsigned, 32); PassBy = Value }", descriptor "fx_wide_take"))

// ═══════════════════════════════════════════════════════════════════
// Enum fields of measured records carry clang's widths (issue #53)
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``a packed enum field in a measured record is one byte`` () =
    withPilot [fixturePath "enum-representation/packed.h"] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_packed_rec\"]"] [] (fun result output ->
        match result with
        | Error error -> failwithf "The packed enum record was refused: %s" error
        | Ok _ ->
            let text = typesOf output |> String.concat "\n"
            Assert.Contains("let Size = 4", text)
            Assert.Contains("let tailOffset = 1", text)
            Assert.Contains("{ Name = \"kind\"; Offset = 0; Repr = Repr.U8; Count = 1;", text)
            Assert.Contains("repr measured: enum fx_packed under LP64", text))

[<Fact>]
let ``a wide enum field in a measured record is eight bytes`` () =
    withPilot [fixturePath "enum-representation/wide.h"] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_wide_rec\"]"] [] (fun result output ->
        match result with
        | Error error -> failwithf "The wide enum record was refused: %s" error
        | Ok _ ->
            let text = typesOf output |> String.concat "\n"
            Assert.Contains("let Size = 16", text)
            Assert.Contains("{ Name = \"kind\"; Offset = 0; Repr = Repr.U64; Count = 1;", text)
            Assert.Contains("repr measured: enum fx_wide under LP64", text))

[<Fact>]
let ``an enum clang did not measure is refused where it is used`` () =
    let unmeasuredHeader = fixturePath "enum-representation/unmeasured.h"
    withPilot [fixturePath "enum-representation/unmeasured.h"] ["c_header_mode = true"] ["fx_take"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9103: enum 'fx_opaque' is used by value by function 'fx_take' in '{unmeasuredHeader}', but that unit's clang parse has no measurement of it; its width cannot be inferred. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a C++ scoped enum parameter carries clang's measured width`` () =
    withPilot [fixturePath "enum-representation/scoped.hpp"] [] ["fx_take_scoped"] (fun result output ->
        match result with
        | Error error -> failwithf "The measured scoped enum was refused: %s" error
        | Ok _ ->
            let source = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))
            Assert.Contains("{ Name = \"value\"; Type = Integer (Unsigned, 8); PassBy = Value } // width measured: enum fx_scoped under LP64", source))

[<Fact>]
let ``a C++ scoped enum clang cannot measure is refused, not taken from its spelled type`` () =
    let hiddenHeader = fixturePath "enum-representation/scoped-hidden.hpp"
    withPilot [fixturePath "enum-representation/scoped-hidden.hpp"] [] ["fx_take_hidden"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9103: enum 'fx_hidden' is used by value by function 'fx_take_hidden' in '{hiddenHeader}', but that unit's clang parse has no measurement of it; its width cannot be inferred. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// GIR signal payloads resolve only in their declaring unit (issue #54)
// ═══════════════════════════════════════════════════════════════════

let private signalPilot (headers: string list) (signal: string) check =
    let paths = headers |> List.map (fun h -> fixturePath ("signal-provenance/" + h))
    withPilotSections paths
        [ "introspection = [\"" + fixturePath "signal-provenance/probe.gir" + "\"]" ]
        [ "c_header_mode = true"; "descriptor_only_dependencies = true" ] []
        [ "signals = [\"Probe::" + signal + "\"]" ]
        [ "[annotations.nonnull]"; "g_signal_connect_data = [2, 4]" ]
        (fun result output -> check paths result output)

[<Fact>]
let ``a signal payload resolves in the unit that declares it`` () =
    signalPilot ["gobject.h"; "count16.h"] "count-changed" (fun _ result output ->
        match result with
        | Error error -> failwithf "A payload declared by one unit was refused: %s" error
        | Ok _ ->
            let signals = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Bridge", "Signals.clef"))
            Assert.Contains("{ Name = \"count\"; Type = Integer (Unsigned, 16); PassBy = Value }", signals))

[<Fact>]
let ``a signal payload two units declare differently is refused`` () =
    signalPilot ["gobject.h"; "count16.h"; "count32.h"] "count-changed" (fun paths result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9109: GObject signal contract: C type 'fx_count_t' is declared differently by '{paths.[1]}' (unsigned 16-bit), '{paths.[2]}' (unsigned 32-bit); one declaration per pilot is required.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a signal payload no unit declares is refused`` () =
    signalPilot ["gobject.h"; "count16.h"] "missing-changed" (fun _ result output ->
        Assert.Equal(
            Error "Binding generation failed: FS9108: GObject signal contract: C type 'fx_missing_t' is declared by no pilot unit; a GIR spelling cannot supply its representation.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// Round 4 reproducers (audit comments 4559, 4557, 4561)
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``clang-parsed C++ digit separators and separated tokens are refused`` () =
    let joined = fixturePath "macro-divergence/digits-joined.hpp"
    let split = fixturePath "macro-divergence/digits-split.hpp"
    withPilot [joined; split] [] [] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9101: macro 'FX_NUMBER' is defined differently by the pilot's headers: '1'2'3' in '{joined}'; '1 '2' 3' in '{split}'. One definition per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``clang-parsed C++ numbers with separators differing only in whitespace generate`` () =
    withPilot [fixturePath "macro-divergence/digits-spaced.hpp"; fixturePath "macro-divergence/digits-unspaced.hpp"] [] [] (fun result _ ->
        match result with
        | Ok generated -> Assert.NotEmpty generated.OutputFiles
        | Error error -> failwithf "Token-identical C++ numbers were refused: %s" error)

[<Fact>]
let ``an enum two units measure differently is refused before output`` () =
    let narrow = fixturePath "enum-units/kind8.h"
    let wide = fixturePath "enum-units/kind32.h"
    withPilot [narrow; wide] ["c_header_mode = true"] ["fx_take8"; "fx_take32"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9112: enum 'fx_kind' is declared differently by the pilot's headers: '{narrow}' (clang-measured unsigned 8-bit; enumerators FX_ZERO=0); '{wide}' (clang-measured unsigned 32-bit; enumerators FX_ZERO=0, FX_BIG=65536). One declaration per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``an included enum is measured in the unit that uses it`` () =
    // kind-enum.h is not a pilot header: kind-user.h's own parse reaches it and measures it.
    let user = fixturePath "enum-units/kind-user.h"
    for headers in [ [user]; [fixturePath "enum-units/kind-enum.h"; user] ] do
        withPilot headers ["c_header_mode = true"] ["fx_take_shared"] (fun result output ->
            match result with
            | Error error -> failwithf "An included, measurable enum was refused: %s" error
            | Ok _ ->
                let source = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))
                Assert.Contains("{ Name = \"value\"; Type = Integer (Unsigned, 32); PassBy = Value } // width measured: enum fx_shared_kind under LP64", source)
                Assert.DoesNotContain("width unknown", source))

[<Fact>]
let ``an included enum clang cannot measure in the using unit is refused`` () =
    let user = fixturePath "enum-units/never-user.h"
    withPilot [user] ["c_header_mode = true"] ["fx_take_never"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9103: enum 'fx_never' is used by value by function 'fx_take_never' in '{user}', but that unit's clang parse has no measurement of it; its width cannot be inferred. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``signal payload candidates naming one enum tag measured differently are refused`` () =
    signalPilot ["gobject.h"; "kind8.h"; "kind32.h"] "count-changed" (fun paths result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9112: enum 'fx_kind' is declared differently by the pilot's headers: '{paths.[1]}' (clang-measured unsigned 8-bit; enumerators FX_ZERO=0); '{paths.[2]}' (clang-measured unsigned 32-bit; enumerators FX_ZERO=0, FX_BIG=65536). One declaration per pilot is required. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``signal payload candidates resolving to different enums are refused`` () =
    signalPilot ["gobject.h"; "kind8-named.h"; "kind32-named.h"] "count-changed" (fun paths result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9109: GObject signal contract: C type 'fx_count_t' is declared differently by '{paths.[1]}' (unsigned 8-bit), '{paths.[2]}' (unsigned 32-bit); one declaration per pilot is required.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a signal enum payload resolves with its declaring unit's enum facts`` () =
    signalPilot ["gobject.h"; "kind8-named.h"] "count-changed" (fun _ result output ->
        match result with
        | Error error -> failwithf "A unit-declared enum payload was refused: %s" error
        | Ok _ ->
            let signals = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Bridge", "Signals.clef"))
            Assert.Contains("{ Name = \"count\"; Type = Integer (Unsigned, 8); PassBy = Value }", signals))

[<Fact>]
let ``a signal enum payload whose enum its unit includes is measured in that unit`` () =
    signalPilot ["gobject.h"; "kind-user.h"] "count-changed" (fun _ result output ->
        match result with
        | Error error -> failwithf "An included, measurable payload enum was refused: %s" error
        | Ok _ ->
            let signals = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Bridge", "Signals.clef"))
            Assert.Contains("{ Name = \"count\"; Type = Integer (Unsigned, 32); PassBy = Value }", signals))

// ═══════════════════════════════════════════════════════════════════
// Unnamed records typed into named fields (issue #159)
// ═══════════════════════════════════════════════════════════════════

/// Every emitted Types module (shared and per namespace), trimmed, as one text.
[<Fact>]
let ``an anonymous field record is emitted with its own clang-measured layout`` () =
    let header = fixturePath "anonymous-records/wide-counter.h"
    withPilot [header] [] ["fx_counter_read"] (fun result output ->
        match result with
        | Error error -> failwithf "The anonymous field record was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            Assert.Contains("/// C union `fx_wide_counter`: layout measured (clang record layout dump).", types)
            Assert.Contains("/// C struct `fx_wide_counter___value32`: layout measured (clang record layout dump).", types)
            let counter = recordModule types "fx_wide_counter"
            Assert.Contains("let Size = 8", counter)
            Assert.Contains("let Alignment = 8", counter)
            Assert.Contains("let __value64Offset = 0", counter)
            Assert.Contains("let __value32Offset = 0", counter)
            Assert.Contains("{ Name = \"__value32\"; Offset = 0; Repr = Repr.U8; Count = 8;", counter)
            Assert.Contains("nested struct fx_wide_counter___value32 by value, as bytes; see fx_wide_counter___value32.Descriptor", counter)
            let halves = recordModule types "fx_wide_counter___value32"
            Assert.Contains("let Size = 8", halves)
            Assert.Contains("let Alignment = 4", halves)
            Assert.Contains("let __lowOffset = 0", halves)
            Assert.Contains("let __highOffset = 4", halves)
            Assert.Contains("{ Name = \"__high\"; Offset = 4; Repr = Repr.U32; Count = 1;", halves)
            Assert.DoesNotContain("layout declared", counter + halves)
            // The emitted numbers are clang's: its own sizeof, _Alignof and offsetof agree.
            clangConfirms header [
                "sizeof(fx_wide_counter) == 8"; "_Alignof(fx_wide_counter) == 8"
                "offsetof(fx_wide_counter, __value64) == 0"; "offsetof(fx_wide_counter, __value32) == 0"
                "sizeof(((fx_wide_counter*)0)->__value32) == 8"; "_Alignof(__typeof__(((fx_wide_counter*)0)->__value32)) == 4"
                "offsetof(fx_wide_counter, __value32.__low) == 0"; "offsetof(fx_wide_counter, __value32.__high) == 4" ])

[<Fact>]
let ``nested anonymous records are emitted at clang's offsets`` () =
    let header = fixturePath "anonymous-records/nested.h"
    withPilot [header] [] ["fx_outer_read"] (fun result output ->
        match result with
        | Error error -> failwithf "The nested anonymous records were refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            let outer = recordModule types "fx_outer"
            Assert.Contains("let Size = 24", outer)
            Assert.Contains("let slotsOffset = 4", outer)
            Assert.Contains("let fixedOffset = 16", outer)
            Assert.Contains("{ Name = \"slots\"; Offset = 4; Repr = Repr.U8; Count = 8;", outer)
            Assert.Contains("{ Name = \"fixed\"; Offset = 16; Repr = Repr.U8; Count = 8; Access = AccessKind.ReadOnly;", outer)
            Assert.Contains("let hiOffset = 2", recordModule types "fx_outer_slots_parts")
            Assert.Contains("let Alignment = 2", recordModule types "fx_outer_slots_parts")
            Assert.Contains("let Size = 4", recordModule types "fx_outer_slots"))

[<Fact>]
let ``an anonymous record clang cannot attribute a layout to is refused before output`` () =
    let header = fixturePath "anonymous-records/macro-pair.h"
    withPilot [header] [] ["fx_pair_read"] (fun result output ->
        Assert.Equal(
            Error ($"Binding generation failed: FS9113: anonymous record 'fx_pair_narrow' (field 'narrow' of 'fx_pair', declared at {header}:7:3) has no clang-measured layout in '{header}'; its layout is never computed from its declared fields."
                   + $" FS9113: anonymous record 'fx_pair_wide' (field 'wide' of 'fx_pair', declared at {header}:7:3) has no clang-measured layout in '{header}'; its layout is never computed from its declared fields. No bindings were written."),
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a generated record name colliding with a declared record is refused before output`` () =
    let header = fixturePath "anonymous-records/name-clash.h"
    withPilot [header] [] ["fx_clash_read"] (fun result output ->
        Assert.Equal(
            Error $"Failed to parse headers: FS9114: the generated record name 'fx_clash_inner' in '{header}' names more than one declaration: the unnamed record of field 'inner' of 'fx_clash' at {header}:10:3; a record. A generated name must name one record.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// C11 anonymous members (issue #173)
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``anonymous members are emitted inside their record's measured layout, their members reachable`` () =
    let header = fixturePath "anonymous-members/members.h"
    withPilot [header] [] ["fx_am_read"] (fun result output ->
        match result with
        | Error error -> failwithf "The anonymous members were refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            Assert.Contains("/// C struct `fx_am`: layout measured (clang record layout dump).", types)
            let am = recordModule types "fx_am"
            for literal in [ "Size = 24"; "Alignment = 8"; "tagOffset = 0"; "anonymous1Offset = 4"; "iOffset = 4"; "fOffset = 4"
                             "anonymous2Offset = 8"; "loOffset = 8"; "hiOffset = 10"; "tailOffset = 16" ] do
                Assert.Contains($"let {literal}\n", am)
            Assert.Contains("/// Offset of `i` in bytes (measured), a member of `fx_am` reached through its anonymous member `anonymous1`.", am)
            Assert.Contains("{ Name = \"anonymous1\"; Offset = 4; Repr = Repr.U8; Count = 4;", am)
            Assert.Contains("see fx_am_anonymous1.Descriptor", am)
            Assert.Contains("{ Name = \"tail\"; Offset = 16; Repr = Repr.I64; Count = 1;", am)
            // Each anonymous member's record is emitted with its own measured layout.
            Assert.Contains("/// C union `fx_am_anonymous1`: layout measured (clang record layout dump).", types)
            Assert.Contains("let fOffset = 0\n", recordModule types "fx_am_anonymous1")
            Assert.Contains("let hiOffset = 2\n", recordModule types "fx_am_anonymous2")
            // Through two levels of anonymous members, under a record named only by a typedef.
            let td = recordModule types "fx_am_td"
            for literal in [ "Size = 8"; "Alignment = 4"; "headOffset = 0"; "anonymous1Offset = 4"; "wordOffset = 4"; "lowOffset = 4"; "highOffset = 6" ] do
                Assert.Contains($"let {literal}\n", td)
            Assert.Contains("let highOffset = 2\n", recordModule types "fx_am_td_anonymous1_anonymous1")
            Assert.DoesNotContain("layout declared", types)
            clangConfirms header [
                "sizeof(struct fx_am) == 24"; "_Alignof(struct fx_am) == 8"; "offsetof(struct fx_am, i) == 4"
                "offsetof(struct fx_am, f) == 4"; "offsetof(struct fx_am, lo) == 8"; "offsetof(struct fx_am, hi) == 10"
                "offsetof(struct fx_am, tail) == 16"; "sizeof(fx_am_td) == 8"; "offsetof(fx_am_td, word) == 4"
                "offsetof(fx_am_td, low) == 4"; "offsetof(fx_am_td, high) == 6" ])

[<Fact>]
let ``an anonymous member clang cannot attribute a layout to is refused before output`` () =
    let header = fixturePath "anonymous-members/macro-pair.h"
    withPilot [header] [] ["fx_mpair_read"] (fun result output ->
        Assert.Equal(
            Error ($"Binding generation failed: FS9113: anonymous record 'fx_mpair_anonymous1' (anonymous member 'anonymous1' of 'fx_mpair', declared at {header}:7:3) has no clang-measured layout in '{header}'; its layout is never computed from its declared fields."
                   + $" FS9113: anonymous record 'fx_mpair_anonymous2' (anonymous member 'anonymous2' of 'fx_mpair', declared at {header}:7:3) has no clang-measured layout in '{header}'; its layout is never computed from its declared fields. No bindings were written."),
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``an anonymous member Farscape cannot name or represent is refused before output`` () =
    let clash = fixturePath "anonymous-members/name-clash.h"
    withPilot [clash] [] ["fx_mclash_read"] (fun result output ->
        Assert.Equal(
            Error $"Failed to parse headers: FS9123: the generated member name 'anonymous1' of the anonymous member of 'fx_mclash' declared at {clash}:5:3 in '{clash}' names another member of that record. A generated name must name one member.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
    let cpp = fixturePath "anonymous-members/class.hpp"
    withPilot [cpp] [] ["fx_cpp_am_read"] (fun result output ->
        Assert.Equal(
            Error $"Failed to parse headers: FS9122: C++ record 'fx_cpp_am' in '{cpp}' has an anonymous member of clang type 'fx_cpp_am::(anonymous union at {cpp}:5:3)'; the C++ record path does not represent anonymous members, and none is dropped from its record.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// No descriptor carries an unknown width (issue #162)
// ═══════════════════════════════════════════════════════════════════

/// Every descriptor a generated binding cites exists in the output: each `laid out by
/// X.Descriptor` and `see X.Descriptor` names an emitted layout module, and a record cited by
/// a function descriptor also has its emitted record type, which the binding's signature names.
let private citedDescriptorsExist (output: string) =
    let sources =
        System.IO.Directory.GetFiles(output, "*.clef", System.IO.SearchOption.AllDirectories)
        |> Array.map System.IO.File.ReadAllText |> String.concat "\n"
    let types = allTypesOf output
    let cited pattern =
        System.Text.RegularExpressions.Regex.Matches(sources, pattern)
        |> Seq.map (fun m -> m.Groups.[1].Value) |> Seq.distinct |> List.ofSeq
    let laidOut = cited @"laid out by (\w+)\.Descriptor"
    for name in laidOut @ cited @"see (\w+)\.Descriptor" do
        Assert.True(types.Contains($"module {name} ="), $"{name}.Descriptor is cited but not emitted:\n{types}")
    for name in laidOut do
        Assert.True(types.Contains($"type {name} ="), $"record type {name} is cited but not emitted:\n{types}")
    laidOut

/// Farscape's own end-to-end, static consistency check of every cited record (#162). It reads
/// the generated package (it does not compile it: the closure cannot yet) and returns every
/// citing site it checked, as `(site, record)`, so a caller can require the exact site set.
/// - Each emitted `type X = { … }` has exactly the descriptor's fields, in order, and each field's
///   Clef type agrees with that field's descriptor representation (`Repr.I*`/`Repr.U*`: `int`,
///   `Repr.F*`: `float`, `Repr.Bool`: `bool`, `Repr.Pointer`: a handle, option or FnPtr);
///   `X.Size` and each `X.<f>Offset` literal equal the descriptor's Size and Offsets.
/// - Every site whose descriptor claims `Named "X" // record: … laid out by X.Descriptor` names
///   the type `X` in the Clef it describes: a function parameter `(p: X)` and result `: X =`,
///   and a callback entry's argument and result in its `Invoke: FnPtr<…>` signature.
let private citedRecordsConsistent (output: string) : (string * string) list =
    let rx (pattern: string) = System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Multiline)
    let types = allTypesOf output
    let bindings =
        System.IO.Directory.GetFiles(output, "*.clef", System.IO.SearchOption.AllDirectories)
        |> Array.filter (fun path -> System.IO.Path.GetFileName path <> "Types.clef")
        |> Array.sort |> Array.map System.IO.File.ReadAllText |> String.concat "\n"
    let claim = @"Named ""(\w+)"" // record: (?:struct|union) \1, laid out by \1\.Descriptor"
    let compatible (repr: string) (clef: string) =
        match repr with
        | r when r.StartsWith "Repr.I" -> clef = TypeMapper.clefSpelling TypeMapper.Signed
        | r when r.StartsWith "Repr.U" -> clef = TypeMapper.clefSpelling TypeMapper.Unsigned
        | r when r.StartsWith "Repr.F" -> clef = TypeMapper.clefSpelling TypeMapper.Float
        | "Repr.Bool" -> clef = TypeMapper.clefSpelling TypeMapper.Bool
        | "Repr.Pointer" -> clef.StartsWith "CHandle<" || clef.StartsWith "option<" || clef.StartsWith "FnPtr<"
        | _ -> false
    let checkRecord (record: string) =
        let recordType = rx($@"^type {record} = \{{\n((?:\w+: [^\n]+\n)+)\}}").Match(types)
        Assert.True(recordType.Success, $"record type {record} is cited but not emitted")
        let typeFields =
            recordType.Groups.[1].Value.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun line -> let at = line.IndexOf(':') in line.Substring(0, at).Trim(), line.Substring(at + 1).Trim())
            |> List.ofArray
        let layout = recordModule types record
        let descriptorFields =
            rx(@"\{ Name = ""(\w+)""; Offset = (\d+); Repr = ([\w.]+);").Matches(layout)
            |> Seq.map (fun m -> m.Groups.[1].Value, int m.Groups.[2].Value, m.Groups.[3].Value) |> List.ofSeq
        Assert.Equal<string list>(typeFields |> List.map fst, descriptorFields |> List.map (fun (n, _, _) -> n))
        for (field, clef), (_, offset, repr) in List.zip typeFields descriptorFields do
            Assert.True(compatible repr clef, $"{record}.{field} is emitted as '{clef}' but its descriptor representation is {repr}")
            Assert.Contains($"let {field}Offset = {offset}", layout)
        let size = rx(@"^let Size = (\d+)").Match(layout).Groups.[1].Value
        Assert.Contains($"{{ Size = {size}", layout)
    let sites = ResizeArray<string * string>()
    // Function descriptors: each one follows the extern it describes.
    for m in rx(@"^let (?:private )?(\w+) (.*) : (\S+) =\n(?:.*\n)*?let \1Descriptor[\s\S]*?@>").Matches(bindings) do
        let name, signature, result, descriptor = m.Groups.[1].Value, m.Groups.[2].Value, m.Groups.[3].Value, m.Value
        for p in rx(@"\{ Name = ""(\w+)""; Type = Named ""(\w+)""; PassBy = \w+ \} // record: (?:struct|union) \2, laid out by \2\.Descriptor").Matches(descriptor) do
            Assert.Contains($"({p.Groups.[1].Value}: {p.Groups.[2].Value})", signature)
            sites.Add($"{name} parameter {p.Groups.[1].Value}", p.Groups.[2].Value)
        let ret = rx("ReturnType = " + claim).Match(descriptor)
        if ret.Success then
            Assert.Equal(ret.Groups.[1].Value, result)
            sites.Add($"{name} result", ret.Groups.[1].Value)
    // Callback descriptors: each names its entry record, whose Invoke field carries the signature.
    for m in rx(@"Expr<CallbackDescriptor> =[\s\S]*?Record = ""(\w+)""[\s\S]*?@>").Matches(bindings) do
        let entry, descriptor = m.Groups.[1].Value, m.Value
        let invoke = rx($@"^type {entry} = \{{\n\s*Invoke: FnPtr<(.*)>\n").Match(bindings)
        Assert.True(invoke.Success, $"callback entry {entry} has no Invoke signature")
        let parts = invoke.Groups.[1].Value.Split([| " -> " |], System.StringSplitOptions.None)
        let args, result = parts.[.. parts.Length - 2], parts.[parts.Length - 1]
        let arguments = rx(@"\{ Name = ""(\w+)""; Type = ([^;]+); PassBy").Matches(descriptor) |> Seq.toArray
        for i, a in Array.indexed arguments do
            if a.Groups.[2].Value.StartsWith "Named " then
                let record = a.Groups.[2].Value.Substring(6).Trim('"')
                Assert.True(i < args.Length && args.[i] = record, $"callback {entry} argument {a.Groups.[1].Value} cites {record}, but its signature is {invoke.Groups.[1].Value}")
                sites.Add($"{entry} argument {a.Groups.[1].Value}", record)
        let ret = rx("ReturnType = " + claim).Match(descriptor)
        if ret.Success then
            Assert.Equal(ret.Groups.[1].Value, result)
            sites.Add($"{entry} result", ret.Groups.[1].Value)
    for record in sites |> Seq.map snd |> Seq.distinct do checkRecord record
    List.ofSeq sites

[<Fact>]
let ``a by-value record is cited by name, never with an unknown width`` () =
    let entry = "Callback_Test_002E_Fixture_003A__003A_fx_005F_vec_005F_mapEntry"
    for mode in [ []; ["c_header_mode = true"] ] do
        withPilot [fixturePath "by-value/record.h"] (mode @ ["value_structs = [\"fx_vec_t\", \"fx_point\"]"]) ["fx_take_vec"; "fx_make_point"; "fx_map_vec"] (fun result output ->
            match result with
            | Error error -> failwithf "By-value records were refused: %s" error
            | Ok _ ->
                let source = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))
                Assert.Contains("{ Name = \"v\"; Type = Named \"fx_vec_t\"; PassBy = Value } // record: struct fx_vec_t, laid out by fx_vec_t.Descriptor", source)
                // The typedef chain reaches the record; the signature names the record's type.
                Assert.Contains("Named \"fx_point\" // record: struct fx_point, laid out by fx_point.Descriptor", source)
                Assert.Contains(": fx_point =", source)
                Assert.DoesNotContain("width unknown", source)
                Assert.Equal<string list>(["fx_vec_t"; "fx_point"], citedDescriptorsExist output |> List.sort |> List.rev)
                // Every citing site, each checked: parameter, result, callback argument and callback result.
                Assert.Equal<(string * string) list>(
                    [ "fx_take_vec parameter v", "fx_vec_t"
                      "fx_make_point result", "fx_point"
                      $"{entry} argument arg0", "fx_vec_t"
                      $"{entry} result", "fx_vec_t" ],
                    citedRecordsConsistent output |> List.sort |> fun sites ->
                        // Order: function sites as emitted, then callback sites.
                        let order (site: string, _) = if site.StartsWith "fx_take_vec" then 0 elif site.StartsWith "fx_make_point" then 1 elif site.EndsWith "arg0" then 2 else 3
                        sites |> List.sortBy order))

[<Fact>]
let ``a by-value record without a declared value type is refused before output`` () =
    let header = fixturePath "by-value/record.h"
    for mode in [ []; ["c_header_mode = true"] ] do
        withPilot [header] mode ["fx_take_vec"] (fun result output ->
            Assert.Equal(
                Error $"Binding generation failed: FS9119: struct 'fx_vec_t' is the type of the parameter 'v' of function 'fx_take_vec' in '{header}', but the pilot declares no value type for it, so neither its record type nor fx_vec_t.Descriptor would be emitted for the binding that cites them; declare it in [options] value_structs. No bindings were written.",
                result |> Result.map ignore)
            Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
    // The audit's C-header-mode case: a nested record returned by value, never declared as a value type.
    let nested = fixturePath "by-value/nested-result.h"
    withPilot [nested] ["c_header_mode = true"] ["fx_use"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9119: struct 'fx_outer_v' is the type of the result of function 'fx_use' in '{nested}', but the pilot declares no value type for it, so neither its record type nor fx_outer_v.Descriptor would be emitted for the binding that cites them; declare it in [options] value_structs. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a by-value type that resolves nowhere is refused before output`` () =
    let header = fixturePath "by-value/unresolved.h"
    let refused (functions: string list) (expected: string) =
        withPilot [header] [] functions (fun result output ->
            Assert.Equal(Error ("Binding generation failed: " + expected + " No bindings were written."), result |> Result.map ignore)
            Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
    // A record defined only outside the pilot's declarations (an included, non-pilot header).
    refused ["fx_take_elsewhere"]
        $"FS9115: by-value type 'struct fx_elsewhere' of the parameter 'value' of function 'fx_take_elsewhere' in '{header}' resolves through no typedef, record or profile entry of that unit; a descriptor cannot carry an unknown width."
    // A record only forward-declared.
    refused ["fx_take_incomplete"]
        $"FS9115: by-value type 'struct fx_incomplete' of the parameter 'value' of function 'fx_take_incomplete' in '{header}' resolves through no typedef, record or profile entry of that unit; a descriptor cannot carry an unknown width."
    // A result (audit codex-audit-m2f: its result-site mutant survived without this).
    refused ["fx_make_elsewhere"]
        $"FS9115: by-value type 'struct fx_elsewhere' of the result of function 'fx_make_elsewhere' in '{header}' resolves through no typedef, record or profile entry of that unit; a descriptor cannot carry an unknown width."
    // A callback entry's argument.
    refused ["fx_register"]
        $"FS9115: by-value type 'struct fx_elsewhere' of the argument 'arg0' of callback parameter 'cb' of function 'fx_register' in '{header}' resolves through no typedef, record or profile entry of that unit; a descriptor cannot carry an unknown width."

// ═══════════════════════════════════════════════════════════════════
// One record definition per pilot, measured or not (issue #161)
// ═══════════════════════════════════════════════════════════════════

let private record name fields = CppParser.Declaration.Struct (mkStruct name fields None)

[<Fact>]
let ``identical record definitions from several headers merge`` () =
    let rec' = record "R" [mkField "a" "int"; mkField "b" "long"]
    // A forward declaration is not a definition, so it never disagrees.
    Assert.Equal(Ok (), BindingGenerator.agreeRecords [ "a.h", [rec']; "b.h", [rec']; "c.h", [CppParser.Declaration.Struct (mkStructSimple "R")] ])

[<Fact>]
let ``a record defined differently is refused naming each header and the first differing field`` () =
    let base' = record "R" [mkField "a" "int"; mkField "b" "long"]
    let refused (other: CppParser.Declaration) (expected: string) =
        Assert.Equal(Error (expected + " No bindings were written."), BindingGenerator.agreeRecords [ "a.h", [base']; "b.h", [other]; "c.h", [base'] ] |> refusalText)
    refused (record "R" [mkField "a" "int"; mkField "b" "long"; mkField "c" "int"])
        "FS9116: record 'R' is defined differently by the pilot's headers: 'a.h', 'c.h' (struct R { int a; long b }); 'b.h' (struct R { int a; long b; int c }); first difference at field 3 'c': absent in 'a.h', 'int c' in 'b.h'. One definition per pilot is required."
    refused (CppParser.Declaration.Struct { mkStruct "R" [mkField "a" "int"; mkField "b" "long"] None with IsUnion = true })
        "FS9116: record 'R' is defined differently by the pilot's headers: 'a.h', 'c.h' (struct R { int a; long b }); 'b.h' (union R { int a; long b }); first difference at its kind: struct in 'a.h', union in 'b.h'. One definition per pilot is required."
    refused (record "R" [mkField "a" "int"; { mkField "b" "long" with IsConst = true }])
        "FS9116: record 'R' is defined differently by the pilot's headers: 'a.h', 'c.h' (struct R { int a; long b }); 'b.h' (struct R { int a; const long b }); first difference at field 2 'b': 'long b' in 'a.h', 'const long b' in 'b.h'. One definition per pilot is required."

[<Fact>]
let ``a record defined alike but measured differently is refused`` () =
    let measured offsets size =
        CppParser.Declaration.Struct { mkStruct "R" [mkField "a" "char"; mkField "b" "int"] None with Measured = Some (mkLayout "R" size 32 offsets) }
    Assert.Equal(
        Error "FS9116: record 'R' (struct R { char a; int b }) is measured differently by the pilot's headers: 'a.h'; 'b.h'; first difference at field 'b': clang-measured offset 4 in 'a.h', 1 in 'b.h'. One definition per pilot is required. No bindings were written.",
        (BindingGenerator.agreeRecords [ "a.h", [measured [0; 32] 64]; "b.h", [measured [0; 8] 40] ] |> refusalText))

[<Fact>]
let ``clang-parsed headers defining one record alike generate it once`` () =
    withPilot [fixturePath "record-divergence/first.h"; fixturePath "record-divergence/same.h"] [] ["fx_first"; "fx_same"] (fun result output ->
        match result with
        | Error error -> failwithf "Identical record definitions were refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            let at = types.IndexOf("module fx_rec =")
            Assert.True(at >= 0, types)
            Assert.Equal(-1, types.IndexOf("module fx_rec =", at + 1)))

[<Fact>]
let ``clang-parsed headers defining one record differently are refused before output, measured or not`` () =
    let first = fixturePath "record-divergence/first.h"
    let differs = fixturePath "record-divergence/differs.h"
    let expected =
        $"Binding generation failed: FS9116: record 'fx_rec' is defined differently by the pilot's headers: '{first}' (struct fx_rec {{ int a; long b }}); '{differs}' (struct fx_rec {{ int a; short b }}); first difference at field 2 'b': 'long b' in '{first}', 'short b' in '{differs}'. One definition per pilot is required. No bindings were written."
    for options, functions in [ [], ["fx_first"; "fx_differs"]
                                [], ["fx_first"]
                                ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_rec\"]"], [] ] do
        withPilot [first; differs] options functions (fun result output ->
            Assert.Equal(Error expected, result |> Result.map ignore)
            Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// Clang built-in aliases in results resolve through clang's canonical type (issue #171)
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``a result clang spells with a built-in alias carries its canonical, measured width`` () =
    withPilot [fixturePath "result-aliases/builtin-result.h"] [] ["strlen"] (fun result output ->
        match result with
        | Error error -> failwithf "The canonicalized alias result was refused: %s" error
        | Ok _ ->
            let source = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))
            Assert.Contains("ReturnType = Integer (Unsigned, 64) // width declared: unsigned long under LP64", source)
            Assert.DoesNotContain("width unknown", source))

[<Fact>]
let ``a result alias clang gives no canonical type is refused before output`` () =
    let header = fixturePath "result-aliases/hidden-result.h"
    withPilot [header] [] ["strlen"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9117: type '__size_t' of the result of function 'strlen' in '{header}' is a clang built-in alias that no declaration of that unit names, and clang gives no canonical type for it: clang could not evaluate the result type of 'strlen' at file scope: called object type 'int' is not a function or function pointer. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// A field typed by a typedef of a tagged record resolves to that record (issue #172)
// ═══════════════════════════════════════════════════════════════════

let private listAgreesWithClang () =
    clangConfirms (fixturePath "typedef-records/list.h") [
        "sizeof(struct fx_holder) == 24"; "_Alignof(struct fx_holder) == 8"
        "offsetof(struct fx_holder, lock) == 0"; "offsetof(struct fx_holder, list) == 8"
        "sizeof(fx_node_t) == 16"; "offsetof(struct fx_node, next) == 8" ]

[<Fact>]
let ``a field typed by a typedef of a tagged record is that record by value`` () =
    withPilot [fixturePath "typedef-records/list.h"] [] ["fx_hold"] (fun result output ->
        match result with
        | Error error -> failwithf "The typedef'd record field was refused: %s" error
        | Ok _ ->
            let holder = recordModule (allTypesOf output) "fx_holder"
            Assert.Contains("let Size = 24", holder)
            Assert.Contains("let listOffset = 8", holder)
            Assert.Contains("{ Name = \"list\"; Offset = 8; Repr = Repr.U8; Count = 16;", holder)
            Assert.Contains("nested struct fx_node by value, as bytes; see fx_node.Descriptor", holder)
            citedDescriptorsExist output |> ignore
            listAgreesWithClang ())

[<Fact>]
let ``a measured record's typedef'd field takes the nested record's measured layout`` () =
    withPilot [fixturePath "typedef-records/list.h"] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_holder\", \"struct fx_node\"]"] [] (fun result output ->
        match result with
        | Error error -> failwithf "The measured typedef'd record field was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            Assert.Contains("/// C struct `fx_holder`: layout measured (clang record layout dump).", types)
            Assert.Contains("/// C struct `fx_node`: layout measured (clang record layout dump).", types)
            Assert.Contains("{ Name = \"list\"; Offset = 8; Repr = Repr.U8; Count = 16;", recordModule types "fx_holder")
            listAgreesWithClang ())

[<Fact>]
let ``a field typed by a typedef of a record no pilot declaration defines is refused`` () =
    withPilot [fixturePath "typedef-records/far-list.h"] [] ["fx_far_hold"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: Struct 'fx_far_holder' field 'node' has no known representation for type 'fx_far_t'; a complete C representation contract is required.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a measured record's typedef'd nested record must be declared in the measuring unit`` () =
    let node = fixturePath "typedef-records/node-only.h"
    let user = fixturePath "typedef-records/node-user.h"
    withPilot [node; user] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_user\"]"] [] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9188: ABI-critical storage 'fx_user' is measured in '{user}', but its nested record 'fx_shared_node' is not declared by that header; a measured layout requires every nested record in one unit.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``a result alias whose function takes an incomplete record by value is refused before output`` () =
    let header = fixturePath "result-aliases/incomplete-param.h"
    withPilot [header] [] ["fx_widen"] (fun result output ->
        Assert.Equal(
            Error $"Binding generation failed: FS9120: type '__int128_t' of the result of function 'fx_widen' in '{header}' is a clang built-in alias that no declaration of that unit names, and its canonical type cannot be read: clang cannot type a call of 'fx_widen', whose parameter 'value' takes incomplete type 'struct fx_opaque' by value (invalid application of 'sizeof' to an incomplete type 'struct fx_opaque'). No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

[<Fact>]
let ``clang's alias measurement contradicting a profile row is refused end to end`` () =
    // On LP64 every integer row agrees with clang, so the contradiction is shown against
    // deliberately wrong rows for the canonical type clang reports, `unsigned long`.
    let header = fixturePath "result-aliases/builtin-result.h"
    let wrong (edit: TypeMapper.AbiRepr -> TypeMapper.AbiRepr) (canonical: string) =
        TypeMapper.tryScalar Types.LP64 canonical |> Option.map (fun row -> if canonical = "unsigned long" then edit row else row)
    let refused edit (expected: string) =
        withPilotGenerating (fun project -> BindingGenerator.generateFromProjectCheckingAliasesAgainst (wrong edit) project false false Types.LP64)
            [header] [] [] ["strlen"] [] [] (fun result output ->
                Assert.Equal(Error ("Binding generation failed: " + expected + " No bindings were written."), result |> Result.map ignore)
                Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
    // Same width, opposite signedness.
    refused (fun row -> { row with Family = TypeMapper.Signed })
        $"FS9118: clang measures the result of function 'strlen' in '{header}' ('__size_t', canonically 'unsigned long') as unsigned 64-bit, but the LP64 profile row for 'unsigned long' is signed 64-bit; the profile and the target must agree."
    // Same signedness, another width (an ILP32 row on this LP64 target).
    refused (fun row -> { row with Bits = 32 })
        $"FS9118: clang measures the result of function 'strlen' in '{header}' ('__size_t', canonically 'unsigned long') as unsigned 64-bit, but the LP64 profile row for 'unsigned long' is unsigned 32-bit; the profile and the target must agree."

// ═══════════════════════════════════════════════════════════════════
// Round 3 coverage (audit codex-audit-m2g, comments 5363 and 5365)
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``a union returned by value without a declared value type is refused before output`` () =
    let header = fixturePath "by-value/union-result.h"
    for mode in [ []; ["c_header_mode = true"] ] do
        withPilot [header] mode ["fx_get_word"] (fun result output ->
            Assert.Equal(
                Error $"Binding generation failed: FS9119: union 'fx_word' is the type of the result of function 'fx_get_word' in '{header}', but the pilot declares no value type for it, so neither its record type nor fx_word.Descriptor would be emitted for the binding that cites them; declare it in [options] value_structs. No bindings were written.",
                result |> Result.map ignore)
            Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
    // Declaring it is not enough: a value type is a plain record, which a union is not.
    withPilot [header] ["value_structs = [\"fx_word\"]"] ["fx_get_word"] (fun result output ->
        Assert.Equal(Error "Binding generation failed: FS9183: Value struct 'fx_word' is a union; a plain record with scalar or pointer fields is required.", result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

let private chainAgreesWithClang () =
    clangConfirms (fixturePath "typedef-records/chain.h") [
        "sizeof(struct fx_chain_holder) == 24"; "_Alignof(struct fx_chain_holder) == 8"
        "offsetof(struct fx_chain_holder, link) == 8"; "sizeof(fx_link_alias_t) == 16"
        "offsetof(struct fx_link, second) == 8" ]

[<Fact>]
let ``a field typed through two typedef links takes the tagged record's measured layout`` () =
    withPilot [fixturePath "typedef-records/chain.h"] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_chain_holder\", \"struct fx_link\"]"] [] (fun result output ->
        match result with
        | Error error -> failwithf "The two-link typedef'd record field was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            Assert.Contains("/// C struct `fx_link`: layout measured (clang record layout dump).", types)
            let holder = recordModule types "fx_chain_holder"
            Assert.Contains("layout measured", types.Substring(types.IndexOf("C struct `fx_chain_holder`")))
            Assert.Contains("let linkOffset = 8", holder)
            Assert.Contains("{ Name = \"link\"; Offset = 8; Repr = Repr.U8; Count = 16;", holder)
            Assert.Contains("nested struct fx_link by value, as bytes; see fx_link.Descriptor", holder)
            citedDescriptorsExist output |> ignore
            chainAgreesWithClang ())

[<Fact>]
let ``a field typed through two typedef links to a record no pilot declaration defines is refused`` () =
    withPilot [fixturePath "typedef-records/far-chain.h"] [] ["fx_far_chain_hold"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: Struct 'fx_far_chain' field 'node' has no known representation for type 'fx_far_alias_t'; a complete C representation contract is required.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

// ═══════════════════════════════════════════════════════════════════
// Round 4 coverage (audit codex-audit-m2h, comments 5401 and 5402)
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``a long double result alias agrees with its row by storage and is accepted`` () =
    // The profile's long double row is an 80-bit value; clang measures its 16 bytes of storage.
    withPilot [fixturePath "result-aliases/long-double-result.h"] [] ["fx_extended"] (fun result output ->
        match result with
        | Error error -> failwithf "The long double alias result was refused: %s" error
        | Ok _ ->
            let source = System.IO.File.ReadAllText(System.IO.Path.Combine(output, "Fixture", "Fixture.clef"))
            Assert.Contains("ReturnType = Float 80 // width inferred: long double under LP64", source))

[<Fact>]
let ``a float row whose storage contradicts clang's measurement is refused end to end`` () =
    let header = fixturePath "result-aliases/long-double-result.h"
    let wrong (canonical: string) =
        TypeMapper.tryScalar Types.LP64 canonical |> Option.map (fun row -> if canonical = "long double" then { row with Bits = 64 } else row)
    withPilotGenerating (fun project -> BindingGenerator.generateFromProjectCheckingAliasesAgainst wrong project false false Types.LP64)
        [header] [] [] ["fx_extended"] [] [] (fun result output ->
            Assert.Equal(
                Error $"Binding generation failed: FS9118: clang measures the result of function 'fx_extended' in '{header}' ('typeof', canonically 'long double') as 128-bit storage, but the LP64 profile row for 'long double' is 64-bit; the profile and the target must agree. No bindings were written.",
                result |> Result.map ignore)
            Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))

let private unionChainAgreesWithClang () =
    clangConfirms (fixturePath "typedef-records/union-chain.h") [
        "sizeof(struct fx_uchain_holder) == 16"; "_Alignof(struct fx_uchain_holder) == 8"
        "offsetof(struct fx_uchain_holder, link) == 8"; "sizeof(fx_ulink_alias_t) == 8"; "_Alignof(union fx_ulink) == 8" ]

[<Fact>]
let ``a field typed through a qualified typedef link to a tagged union takes its measured layout`` () =
    withPilot [fixturePath "typedef-records/union-chain.h"] ["c_header_mode = true"; "abi_critical_structs = [\"struct fx_uchain_holder\", \"union fx_ulink\"]"] [] (fun result output ->
        match result with
        | Error error -> failwithf "The typedef'd union field was refused: %s" error
        | Ok _ ->
            let types = allTypesOf output
            Assert.Contains("/// C union `fx_ulink`: layout measured (clang record layout dump).", types)
            Assert.Contains("let Size = 8", recordModule types "fx_ulink")
            let holder = recordModule types "fx_uchain_holder"
            Assert.Contains("let linkOffset = 8", holder)
            Assert.Contains("{ Name = \"link\"; Offset = 8; Repr = Repr.U8; Count = 8;", holder)
            Assert.Contains("nested struct fx_ulink by value, as bytes; see fx_ulink.Descriptor", holder)
            citedDescriptorsExist output |> ignore
            unionChainAgreesWithClang ())

[<Fact>]
let ``a field typed through a qualified typedef link to a union no pilot declaration defines is refused`` () =
    withPilot [fixturePath "typedef-records/far-union-chain.h"] [] ["fx_far_u_hold"] (fun result output ->
        Assert.Equal(
            Error "Binding generation failed: Struct 'fx_far_u_holder' field 'link' has no known representation for type 'fx_far_u_alias_t'; a complete C representation contract is required.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
