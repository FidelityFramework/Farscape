module Farscape.Tests.PkgConfigTests

open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// pkg-config include paths and defines (issue #178): the module name is one argument, output
// is split as pkg-config quotes it for a shell, and only -I and -D words are carried.
// ═══════════════════════════════════════════════════════════════════

let private fixtureModules = [ "PKG_CONFIG_PATH", fixturePath "pkg-config" ]

[<Fact>]
let ``a pkg-config include path containing spaces is read as one path`` () =
    // pkg-config prints `-I/opt/fx\ lib/include\ dir`; splitting on blanks would break it in three.
    Assert.Equal(
        Ok (["/opt/fx lib/include dir"; "/opt/plain"], ["FX_PKG=1"]),
        PkgConfig.run fixtureModules "fx-spaced")

[<Fact>]
let ``pkg-config output carrying a language-mode flag is refused`` () =
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'fx-inject' gives '-fms-extensions', which is not -I<path> or -D<define>; a pilot carries only include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.run fixtureModules "fx-inject")
    // -isystem has no pilot representation, so it is refused too, never turned into -I or dropped.
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'm' gives '-isystem', which is not -I<path> or -D<define>; a pilot carries only include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.parseCflags "m" "-I/a -isystem /b")
    Assert.Equal(
        Error "FS9124: define 'FX(x)=x' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option.",
        PkgConfig.parseCflags "m" "-DFX(x)=x")
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'm' ends inside a quotation or an escape: '-I'/a'; a pilot carries only include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.parseCflags "m" "-I'/a")

[<Fact>]
let ``a pkg-config module name carrying an option is refused before pkg-config runs`` () =
    for name in [ "fx-spaced -fms-extensions"; "--cflags-only-other"; "" ] do
        Assert.Equal(
            Error $"FS9125: pkg-config module name '{name}' is not a module name (letters, digits and _ . + -, not starting with -); it reaches pkg-config as one argument and carries no option.",
            PkgConfig.run fixtureModules name)
    for name in [ "gtk+-3.0"; "webkit2gtk-4.1"; "libdrm"; "fx_spaced" ] do
        Assert.Equal(None, PkgConfig.moduleNameRefusal name)

[<Fact>]
let ``pkg-config output is split into words as a shell would split it, without expansion`` () =
    Assert.Equal(Some ["-I/a b"; "-I/c d"; "-I/e \"f"; "-DX=$Y"], PkgConfig.words "-I/a\\ b  -I'/c d'\t-I\"/e \\\"f\" -DX=$Y\n")
    Assert.Equal(Some [], PkgConfig.words "  \n")
    Assert.Equal(None, PkgConfig.words "-I\"/a")
    Assert.Equal(None, PkgConfig.words "-I/a\\")
