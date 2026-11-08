module Farscape.Tests.PkgConfigTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// pkg-config include paths, system include paths and defines (issues #178, #179): the module name
// is one argument, output is split as pkg-config quotes it for a shell, and only -I, -isystem and
// -D words are carried.
// ═══════════════════════════════════════════════════════════════════

let private fixtureModules = [ "PKG_CONFIG_PATH", fixturePath "pkg-config" ]

let private cflags includes systems defines : PkgConfig.Cflags =
    { IncludePaths = includes; SystemIncludePaths = systems; Defines = defines }

[<Fact>]
let ``a pkg-config include path containing spaces is read as one path`` () =
    // pkg-config prints `-I/opt/fx\ lib/include\ dir`; splitting on blanks would break it in three.
    Assert.Equal(
        Ok (cflags ["/opt/fx lib/include dir"; "/opt/plain"] [] ["FX_PKG=1"]),
        PkgConfig.run fixtureModules "fx-spaced")

[<Fact>]
let ``pkg-config output carrying a language-mode flag is refused`` () =
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'fx-inject' gives '-fms-extensions', which is not -I<path>, -isystem <path> or -D<define>; a pilot carries only include paths, system include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.run fixtureModules "fx-inject")
    // A -isystem is carried only with a path after it: never guessed, never dropped.
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'm' gives '-isystem' with no path after it; a pilot carries only include paths, system include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.parseCflags "m" "-I/a -isystem")
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'm' gives '-isystem' followed by '-I/b', which is not a path; a pilot carries only include paths, system include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.parseCflags "m" "-isystem -I/b")
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'm' gives '-pthread', which is not -I<path>, -isystem <path> or -D<define>; a pilot carries only include paths, system include paths and defines, so the output is refused rather than dropped or reinterpreted.",
        PkgConfig.parseCflags "m" "-isystem /b -pthread")
    Assert.Equal(
        Error "FS9124: define 'FX(x)=x' is not NAME or NAME=VALUE with NAME a C identifier; a define reaches clang as one -D argument and carries no other option.",
        PkgConfig.parseCflags "m" "-DFX(x)=x")
    Assert.Equal(
        Error "FS9126: pkg-config --cflags for 'm' ends inside a quotation or an escape: '-I'/a'; a pilot carries only include paths, system include paths and defines, so the output is refused rather than dropped or reinterpreted.",
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

// ─── System include paths (#179) ───

let private moduleDir = fixturePath "pkg-config/fx-isystem"
let private systemDir = Path.Combine(moduleDir, "sys dir")
let private includeDir = Path.Combine(moduleDir, "inc")

[<Fact>]
let ``a pkg-config system include path is read in either spelling, in order`` () =
    Assert.Equal(
        Ok (cflags ["/a"] ["/s 1"; "/s2"; "/s3"] ["X=1"]),
        PkgConfig.parseCflags "m" "-isystem /s\\ 1 -I/a -isystem/s2 -isystem '/s3' -DX=1")

[<Fact>]
let ``clang receives each system include path as -isystem and the path, after the include paths`` () =
    Assert.Equal<string list>(
        ["-I/a"; "-isystem"; "/s 1"; "-isystem"; "-s"; "-DX=1"],
        CppParser.searchArguments ["/a"] ["/s 1"; "-s"] ["X=1"])
    // Real clang: under these arguments the header the system path reaches is a system header
    // (line-marker flag 3); the same directory given as -I would not be.
    let header = Path.Combine(includeDir, "fx_sys_api.h")
    let info = ProcessLaunch.startInfo "clang" (CppParser.searchArguments [includeDir] [systemDir] [] @ ["-E"; header])
    use proc = System.Diagnostics.Process.Start info
    let output = proc.StandardOutput.ReadToEnd()
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)
    let systemHeader = Path.Combine(systemDir, "fx_sys_types.h")
    Assert.Contains($"# 1 \"{systemHeader}\" 1 3", output)

[<Fact>]
let ``a pilot's system include paths from real pkgconf reach clang`` () =
    // Real pkgconf against the fixture module: `-isystem <dir with a space>`, -I and -D.
    let resolved = PkgConfig.run fixtureModules "fx-isystem"
    Assert.Equal(Ok (cflags [includeDir] [systemDir] ["FX_SYS=1"]), resolved)
    let resolved = match resolved with Ok value -> value | Error error -> failwith error
    let quoted (items: string list) = items |> List.map (fun item -> "\"" + item + "\"") |> String.concat ", "
    let header = Path.Combine(includeDir, "fx_sys_api.h")
    let library withSystem =
        [ $"include_paths = [{quoted resolved.IncludePaths}]"; $"defines = [{quoted resolved.Defines}]" ]
        @ (if withSystem then [ $"system_include_paths = [{quoted resolved.SystemIncludePaths}]" ] else [])
    // The pilot field is read and passed to clang: the count type resolves through the system header.
    withPilotSections [header] (library true) ["c_header_mode = true"] ["fx_sys_count"] [] [] (fun result output ->
        match result with
        | Error error -> failwithf "A pilot carrying the module's system include path was refused: %s" error
        | Ok _ ->
            let source = File.ReadAllText(Path.Combine(output, "Fixture", "Fixture.clef"))
            Assert.Contains("ReturnType = Integer (Unsigned, 16)", source))
    // Without the field clang cannot find the system header, so the same pilot is refused.
    withPilotSections [header] (library false) ["c_header_mode = true"] ["fx_sys_count"] [] [] (fun result output ->
        match result with
        | Ok _ -> failwith "a pilot without the module's system include path generated"
        | Error error ->
            Assert.Contains("'fx_sys_types.h' file not found", error)
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories)))

[<Fact>]
let ``a pilot's system include paths survive serialization`` () =
    let project : PilotTypes.PilotProject =
        { Library = { Name = "fx"; Headers = ["fx.h"]; XmlProtocols = []; Introspection = []; IncludePaths = ["/a"]; SystemIncludePaths = ["/s 1"; "/s2"]
                      Defines = []; MacroPrefixes = []; PkgConfig = ["fx-isystem"] }
          Output = { Mode = "fidelity"; Directory = "out" }
          Namespaces = []; ErrorConventions = None; Options = None; Callbacks = None; Nonnull = None
          ProtocolConfig = None; Layer3 = None; CppConfig = None }
    let text = PilotSerializer.toTomlString project
    Assert.Contains("system_include_paths = [\"/s 1\", \"/s2\"]", text)
    match PilotSerializer.deserialize (Fidelity.Data.TOML.Toml.parseOrFail text) with
    | Ok read -> Assert.Equal<string list>(["/s 1"; "/s2"], read.Library.SystemIncludePaths)
    | Error error -> failwith error
