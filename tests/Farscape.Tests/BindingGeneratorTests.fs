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
        BindingGenerator.agreeMacros headers)

[<Fact>]
let ``function-like macros agree only when parameters and replacement agree`` () =
    let fn name args body : CppParser.Declaration =
        CppParser.Declaration.Macro { Name = name; Kind = CppParser.FunctionLike (args, body); RawValue = body; Documentation = None }
    Assert.Equal(Ok (), BindingGenerator.agreeMacros [ "a.h", false, [fn "F" ["x"] "((x)+1)"]; "b.h", false, [fn "F" ["x"] "((x)+1)"] ])
    Assert.Equal(
        Error "FS9101: macro 'F' is defined differently by the pilot's headers: '(x) ((x)+1)' in 'a.h'; '(y) ((x)+1)' in 'b.h'. One definition per pilot is required. No bindings were written.",
        BindingGenerator.agreeMacros [ "a.h", false, [fn "F" ["x"] "((x)+1)"]; "b.h", false, [fn "F" ["y"] "((x)+1)"] ])

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
        BindingGenerator.agreeMacros [ "a.h", false, [macro "X" "0x10"]; "b.h", false, [macro "X" "16"] ])
    // Whitespace inside a string literal is part of the token.
    Assert.Equal(
        Error "FS9101: macro 'S' is defined differently by the pilot's headers: '\"a b\"' in 'a.h'; '\"a  b\"' in 'b.h'. One definition per pilot is required. No bindings were written.",
        BindingGenerator.agreeMacros [ "a.h", false, [macro "S" "\"a b\""]; "b.h", false, [macro "S" "\"a  b\""] ])

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
        BindingGenerator.agreeMacros [ "a.h", false, [macro "W" "L\"x\""]; "b.h", false, [macro "W" "L \"x\""] ])
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
        BindingGenerator.agreeMacros [ "a.hpp", true, [macro "N" "1'2'3"]; "b.hpp", true, [macro "N" "1 '2' 3"] ])
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
