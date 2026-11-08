module Farscape.Tests.CppParserInvocationTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// Clang argument boundaries (issue #178): each pilot define is one -D argument and each include
// path one -I argument, so neither can carry a clang option; a define that is not NAME or
// NAME=VALUE is refused before any parse.
// ═══════════════════════════════════════════════════════════════════

[<Fact>]
let ``a define and an include path containing whitespace each reach clang as one argument`` () =
    // Split into separate arguments, either would turn on -fms-extensions, and clang would
    // declare fx_tagged_outer's `struct fx_inner;` an anonymous member the parse refuses (FS9122).
    let header = fixturePath "anonymous-members/tagged-member.h"
    let includePath = fixturePath "anonymous-members" + " -fms-extensions"
    Assert.Equal(None, CppParser.defineRefusal "FX_NOTE=a -fms-extensions")
    withPilotSections [header] [ "defines = [\"FX_NOTE=a -fms-extensions\"]"; $"include_paths = [\"{includePath}\"]" ] [] ["fx_tagged_read"] [] [] (fun result output ->
        match result with
        | Error error -> failwithf "The pilot was refused: %s" error
        | Ok _ ->
            let outer = recordModule (allTypesOf output) "fx_tagged_outer"
            Assert.Contains("let Size = 4\n", outer)
            Assert.Contains("let yOffset = 0\n", outer)
            Assert.DoesNotContain("anonymous", outer)
            // Clang in C mode, without the extension, agrees.
            clangConfirms header [ "sizeof(struct fx_tagged_outer) == 4"; "offsetof(struct fx_tagged_outer, y) == 0" ])

[<Fact>]
let ``a define carrying a clang option is refused before any parse`` () =
    let header = fixturePath "anonymous-members/tagged-member.h"
    withPilotSections [header] [ "defines = [\"AUDIT -fms-extensions\"]" ] [] ["fx_tagged_read"] [] [] (fun result output ->
        Assert.Equal(
            Error "FS9124: define 'AUDIT -fms-extensions' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option. No bindings were written.",
            result |> Result.map ignore)
        Assert.Empty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories)))
    // The parser refuses it too, before running clang.
    Assert.Equal(
        Error "FS9124: define 'AUDIT -fms-extensions' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option.",
        CppParser.parseHeader { HeaderFile = header; IncludePaths = []; SystemIncludePaths = []; Defines = ["AUDIT -fms-extensions"]; IncludeRoot = None
                                MacroPrefixes = []; IncludeMacros = false; Verbose = false; CppMode = false } |> Result.map ignore)

[<Theory>]
[<InlineData("FX", true)>]
[<InlineData("_FX_1", true)>]
[<InlineData("FX=1", true)>]
[<InlineData("FX=", true)>]
[<InlineData("FX=a b", true)>]
[<InlineData("", false)>]
[<InlineData("1FX", false)>]
[<InlineData("FX(x)=x", false)>]
[<InlineData("-fms-extensions", false)>]
[<InlineData("FX -fms-extensions", false)>]
[<InlineData("FX=1\n-fms-extensions", false)>]
let ``a define is NAME or NAME=VALUE with NAME a C identifier`` (define: string) (wellFormed: bool) =
    Assert.Equal(wellFormed, (CppParser.defineRefusal define).IsNone)
