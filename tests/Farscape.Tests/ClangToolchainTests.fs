module Farscape.Tests.ClangToolchainTests

open System.IO
open Xunit
open Farscape.Core
open TestHelpers

// ═══════════════════════════════════════════════════════════════════
// The clang Farscape launches (#200): FARSCAPE_CLANG, else the shared parity toolchain's
// clang 23.1.3 (identical bytes on Z13 and the workers) with its own resource directory. A host
// clang on PATH is never launched. Layout measurements compare between nodes only when both
// launch the same clang with the same builtin headers.
// ═══════════════════════════════════════════════════════════════════

/// The version string the pinned release's clang records (`__clang_version__`).
let private pinnedVersionString = "23.1.3 (https://github.com/llvm/llvm-project 0d261d1ca552c95a8f007e061c787ac7132fbcbc)"

let private home = "/home/fixture"
let private pinnedHome = "/home/fixture/.local/toolchains/llvm-23.1.3/bin/clang-23"
let private pinnedHomeResources = "/home/fixture/.local/toolchains/llvm-23.1.3/lib/clang/23"
let private workerTwo = "/mnt/apps/gpu-two/toolchains/llvm-23.1.3/bin/clang-23"
let private workerTwoResources = "/mnt/apps/gpu-two/toolchains/llvm-23.1.3/lib/clang/23"

/// A pinned clang installed with the `lib/clang/23/include` its release ships beside it.
let private withResources (clang: string) = [ clang; ClangToolchain.pinnedResourceDir clang + "/include" ]

let private resolveAmong (variables: Map<string, string>) (installed: string list) =
    ClangToolchain.resolveWith (fun name -> Map.tryFind name variables) home (fun path -> List.contains path installed)

let private launchOf (variables: Map<string, string>) (installed: string list) =
    let resolved = resolveAmong variables installed
    resolved.Path, resolved.Origin, resolved.ResourceDir

let private refusalOf (variables: Map<string, string>) (installed: string list) =
    (Assert.Throws<System.InvalidOperationException>(fun () -> resolveAmong variables installed |> ignore)).Message

[<Fact>]
let ``the override, then the pinned toolchain in node order, each pinned clang with its resource directory`` () =
    let pinned = ClangToolchain.Source.Pinned
    Assert.Equal(("/opt/clang", ClangToolchain.Source.Override, None),
                 launchOf (Map.ofList [ "FARSCAPE_CLANG", "/opt/clang" ]) ("/opt/clang" :: withResources pinnedHome))
    Assert.Equal((pinnedHome, pinned, Some pinnedHomeResources), launchOf Map.empty (withResources workerTwo @ withResources pinnedHome))
    Assert.Equal((workerTwo, pinned, Some workerTwoResources), launchOf Map.empty (withResources workerTwo))
    Assert.Equal((workerTwo, pinned, Some workerTwoResources), launchOf (Map.ofList [ "FARSCAPE_CLANG", " " ]) (withResources workerTwo))

[<Fact>]
let ``an override naming no file is refused, never replaced by another clang`` () =
    Assert.Equal("FARSCAPE_CLANG names '/opt/missing', which does not exist; unset it to launch the pinned clang 23.1.3.",
                 refusalOf (Map.ofList [ "FARSCAPE_CLANG", "/opt/missing" ]) (withResources pinnedHome))

[<Fact>]
let ``a node with no pinned clang is refused, naming every path checked, and PATH is never consulted`` () =
    Assert.Equal(
        "no pinned clang 23.1.3 is installed (checked /home/fixture/.local/toolchains/llvm-23.1.3/bin/clang-23, "
        + "/mnt/apps/gpu-one/toolchains/llvm-23.1.3/bin/clang-23, /mnt/apps/gpu-two/toolchains/llvm-23.1.3/bin/clang-23, "
        + "/mnt/apps/gpu-three/toolchains/llvm-23.1.3/bin/clang-23) and FARSCAPE_CLANG is unset; "
        + "Farscape never launches a host clang from PATH.",
        refusalOf Map.empty [ "clang"; "/usr/bin/clang"; "/usr/bin/clang-23" ])

[<Fact>]
let ``a pinned clang without its resource directory is refused, never paired with a host resource directory`` () =
    Assert.Equal(
        "the pinned clang 23.1.3 at '/mnt/apps/gpu-two/toolchains/llvm-23.1.3/bin/clang-23' has no resource directory "
        + "'/mnt/apps/gpu-two/toolchains/llvm-23.1.3/lib/clang/23/include'; install lib/clang/23/include from the same verified asset.",
        refusalOf Map.empty [ workerTwo; "/usr/lib/clang/23/include" ])

[<Fact>]
let ``the worker parity paths are the shared toolchain's clang on gpu-one, gpu-two and gpu-three`` () =
    Assert.Equal<string list>(
        [ pinnedHome
          "/mnt/apps/gpu-one/toolchains/llvm-23.1.3/bin/clang-23"
          workerTwo
          "/mnt/apps/gpu-three/toolchains/llvm-23.1.3/bin/clang-23" ],
        ClangToolchain.pinnedCandidates home)

[<Fact>]
let ``a pinned launch leads with -resource-dir, an override launches its arguments unchanged`` () =
    let pinned = resolveAmong Map.empty (withResources workerTwo)
    Assert.Equal<string list>([ "-resource-dir"; workerTwoResources; "-E"; "a.h" ], ClangToolchain.argumentsFor pinned [ "-E"; "a.h" ])
    let overridden = resolveAmong (Map.ofList [ "FARSCAPE_CLANG", "/opt/clang" ]) [ "/opt/clang" ]
    Assert.Equal<string list>([ "-E"; "a.h" ], ClangToolchain.argumentsFor overridden [ "-E"; "a.h" ])

/// The digest SHA256SUMS records for `bin/clang-23` in the toolchain holding `clang`.
let private recordedDigest (clang: string) =
    let sums = Path.Combine(Path.GetDirectoryName clang, "..", "SHA256SUMS")
    Assert.True(File.Exists sums, $"the pinned toolchain records no SHA256SUMS beside {clang}")
    File.ReadAllLines sums
    |> Array.tryPick (fun line ->
        match line.Split("  ") with
        | [| digest; "bin/clang-23" |] -> Some digest
        | _ -> None)
    |> Option.defaultWith (fun () -> failwith $"{sums} records no digest for bin/clang-23")

[<Fact>]
let ``Farscape launches the pinned clang 23.1.3, whose bytes its toolchain records`` () =
    let resolved = ClangToolchain.resolve ()
    // Every node that runs this suite has the pinned toolchain installed.
    Assert.Equal(ClangToolchain.Source.Pinned, resolved.Origin)
    let file, digest = ClangToolchain.identity ()
    Assert.Equal(resolved.Path, file)
    Assert.EndsWith("/llvm-23.1.3/bin/clang-23", file)
    Assert.Equal(recordedDigest file, digest)
    // The header parse itself runs that clang: the macros it evaluates name the release and the
    // version string the pinned build records (a distribution's clang 23.1.3 records another).
    let options =
        { CppParser.defaultOptions (fixturePath "toolchain/clang_identity.h") with
            IncludeMacros = true; MacroPrefixes = [ "PIN_" ]; CppMode = false }
    let macros =
        match CppParser.parseHeaderFull options with
        | Ok result -> result.Macros |> List.map (fun m -> m.Name, m.Kind) |> Map.ofList
        | Error error -> failwithf "the clang identity fixture did not parse: %s" error
    Assert.Equal(CppParser.IntegerConstant ("__clang_major__", 23L), macros.["PIN_CLANG_MAJOR"])
    Assert.Equal(CppParser.IntegerConstant ("__clang_minor__", 1L), macros.["PIN_CLANG_MINOR"])
    Assert.Equal(CppParser.IntegerConstant ("__clang_patchlevel__", 3L), macros.["PIN_CLANG_PATCHLEVEL"])
    // A built-in string macro is no integer constant; clang names its type, whose length is the
    // pinned build's version string (Arch's clang 23.1.3 records "23.1.3 ", a char[8]).
    Assert.Equal(
        CppParser.Unevaluated ("__clang_version__", $"clang: integer constant expression must have integer type, not 'char[{pinnedVersionString.Length + 1}]'"),
        macros.["PIN_CLANG_VERSION"])

[<Fact>]
let ``the parser's clang process is the pinned binary told the pinned resource directory, which clang reports back`` () =
    let resolved = ClangToolchain.resolve ()
    Assert.Equal(ClangToolchain.Source.Pinned, resolved.Origin)
    let resourceDir = ClangToolchain.pinnedResourceDir resolved.Path
    // The exact ProcessStartInfo every parser launch uses.
    let info = CppParser.clangStartInfo [ "-print-resource-dir" ]
    Assert.Equal(resolved.Path, info.FileName)
    Assert.Equal<string list>([ "-resource-dir"; resourceDir; "-print-resource-dir" ], List.ofSeq info.ArgumentList)
    use proc = System.Diagnostics.Process.Start info
    let reported = proc.StandardOutput.ReadToEnd().Trim()
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)
    Assert.Equal(resourceDir, reported)
    Assert.True(File.Exists(Path.Combine(reported, "include", "stddef.h")), $"{reported} holds no builtin stddef.h")
