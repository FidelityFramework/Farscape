module Farscape.Tests.ClangToolchainTests

open System.IO
open Xunit
open Farscape.Core

// ═══════════════════════════════════════════════════════════════════
// ClangToolchain resolves the clang and the llvm-ar of foreign C and C++ compilation ahead of
// Composer. `FIDELITY_LLVM_BIN` selects Composer's backend tools (`opt`, `ld.lld`, `llvm-dis`)
// and is not read by either resolution.
// ═══════════════════════════════════════════════════════════════════

/// An environment over `variables` and the names read from it, in order.
let private recorded (variables: Map<string, string>) : (string -> string option) * ResizeArray<string> =
    let read = ResizeArray<string>()
    (fun name ->
        read.Add name
        Map.tryFind name variables), read

let private resolve (variables: Map<string, string>) (installed: string list) =
    ClangToolchain.resolveWith (fun name -> Map.tryFind name variables) "/fixture" (fun path -> List.contains path installed)

let private resolveArchiver (variables: Map<string, string>) (clangDir: string option) (installed: string list) =
    ClangToolchain.resolveArchiverWith (fun name -> Map.tryFind name variables) clangDir (fun path -> List.contains path installed)

/// Run `body` over a fresh scratch directory in OS temporary storage, deleted afterwards.
let private withScratch (body: string -> unit) =
    let directory = TestHelpers.TestScratch.create "clang-toolchain"
    Directory.CreateDirectory(directory) |> ignore
    try body directory
    finally Directory.Delete(directory, true)

// ─── clang ───

[<Fact>]
let ``clang selection uses FIDELITY_CLANG then unversioned clang on PATH`` () =
    let variables = Map.ofList [ "FIDELITY_CLANG", "/explicit/clang"; "PATH", "/one:/two" ]
    let installed = [ "/explicit/clang"; "/two/clang"; "/one/clang-21" ]
    let explicit = resolve variables installed
    Assert.Equal("/explicit/clang", explicit.Path)
    Assert.Equal(ClangToolchain.Source.Override, explicit.Origin)
    let path = resolve (Map.remove "FIDELITY_CLANG" variables) installed
    Assert.Equal("/two/clang", path.Path)
    Assert.Equal(ClangToolchain.Source.Path, path.Origin)
    // The first PATH directory holding `clang` is selected.
    Assert.Equal("/one/clang", (resolve (Map.ofList [ "PATH", "/one:/two" ]) [ "/two/clang"; "/one/clang" ]).Path)
    // A blank FIDELITY_CLANG is no selection.
    Assert.Equal("/two/clang", (resolve (Map.add "FIDELITY_CLANG" "  " variables) installed).Path)

[<Fact>]
let ``a FIDELITY_CLANG naming no file is refused without PATH being searched`` () =
    let environment, read = recorded (Map.ofList [ "FIDELITY_CLANG", "/missing/clang"; "PATH", "/usr/bin" ])
    let error =
        Assert.Throws<Diagnostics.RefusalException>(fun () ->
            ClangToolchain.resolveWith environment "/fixture" (fun path -> path = "/usr/bin/clang") |> ignore)
    Assert.Equal("FIDELITY_CLANG selects '/missing/clang', which does not exist; PATH is not searched.", error.Message)
    Assert.Equal<string list>([ "FIDELITY_CLANG" ], List.ofSeq read)

[<Fact>]
let ``versioned clang names on PATH do not satisfy resolution`` () =
    let variables = Map.ofList [ "PATH", "/usr/bin" ]
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> resolve variables [ "/usr/bin/clang-21"; "/usr/bin/clang-20"; "/usr/bin/clang-19" ] |> ignore)
    Assert.Equal("clang was not found on PATH; set FIDELITY_CLANG.", error.Message)

[<Fact>]
let ``FIDELITY_LLVM_BIN does not select clang`` () =
    let backend = Map.ofList [ "FIDELITY_LLVM_BIN", "/llvm/bin"; "PATH", "/one:/two" ]
    // A clang in the backend directory is not selected: the clang on PATH is.
    let selected = resolve backend [ "/llvm/bin/clang"; "/two/clang" ]
    Assert.Equal("/two/clang", selected.Path)
    Assert.Equal(ClangToolchain.Source.Path, selected.Origin)
    // With no clang on PATH, the clang in the backend directory is not selected either.
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> resolve backend [ "/llvm/bin/clang" ] |> ignore)
    Assert.Equal("clang was not found on PATH; set FIDELITY_CLANG.", error.Message)
    // A backend directory that does not exist is no refusal of the clang resolution.
    Assert.Equal("/two/clang", (resolve (Map.add "FIDELITY_LLVM_BIN" "/missing" backend) [ "/two/clang" ]).Path)
    // FIDELITY_CLANG is selected whatever the backend directory holds.
    Assert.Equal("/explicit/clang", (resolve (Map.add "FIDELITY_CLANG" "/explicit/clang" backend) [ "/explicit/clang"; "/llvm/bin/clang"; "/two/clang" ]).Path)
    // The variable is never read.
    let environment, read = recorded backend
    ClangToolchain.resolveWith environment "/fixture" (fun path -> path = "/two/clang") |> ignore
    Assert.Equal<string list>([ "FIDELITY_CLANG"; "PATH" ], List.ofSeq read)

// ─── llvm-ar ───

[<Fact>]
let ``llvm-ar selection uses FIDELITY_LLVM_AR then the clang directory then PATH`` () =
    let variables = Map.ofList [ "FIDELITY_LLVM_AR", "/explicit/llvm-ar"; "PATH", "/one:/two" ]
    let installed = [ "/explicit/llvm-ar"; "/clang/bin/llvm-ar"; "/two/llvm-ar" ]
    Assert.Equal("/explicit/llvm-ar", resolveArchiver variables (Some "/clang/bin") installed)
    let unset = Map.remove "FIDELITY_LLVM_AR" variables
    Assert.Equal("/clang/bin/llvm-ar", resolveArchiver unset (Some "/clang/bin") installed)
    // A clang directory holding no llvm-ar, and no clang directory, leave the PATH search.
    Assert.Equal("/two/llvm-ar", resolveArchiver unset (Some "/clang/bin") [ "/two/llvm-ar" ])
    Assert.Equal("/two/llvm-ar", resolveArchiver unset None installed)
    // A blank FIDELITY_LLVM_AR is no selection.
    Assert.Equal("/clang/bin/llvm-ar", resolveArchiver (Map.add "FIDELITY_LLVM_AR" " " variables) (Some "/clang/bin") installed)

[<Fact>]
let ``a FIDELITY_LLVM_AR naming no file is refused without another llvm-ar being selected`` () =
    let variables = Map.ofList [ "FIDELITY_LLVM_AR", "/missing/llvm-ar"; "PATH", "/two" ]
    let installed = [ "/clang/bin/llvm-ar"; "/two/llvm-ar" ]
    let environment, read = recorded variables
    let probed = ResizeArray<string>()
    let exists (path: string) =
        probed.Add path
        List.contains path installed
    let error =
        Assert.Throws<Diagnostics.RefusalException>(fun () ->
            ClangToolchain.resolveArchiverWith environment (Some "/clang/bin") exists |> ignore)
    Assert.Equal("FIDELITY_LLVM_AR selects '/missing/llvm-ar', which does not exist; no other llvm-ar is searched for.", error.Message)
    // `PATH` is not read, and the selected path is the only one tested for existence: neither
    // the llvm-ar beside clang nor the one on `PATH` is looked at.
    Assert.Equal<string list>([ "FIDELITY_LLVM_AR" ], List.ofSeq read)
    Assert.Equal<string list>([ "/missing/llvm-ar" ], List.ofSeq probed)
    // The refusal does not depend on a clang directory being known.
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> resolveArchiver variables None installed |> ignore)
    Assert.Equal("FIDELITY_LLVM_AR selects '/missing/llvm-ar', which does not exist; no other llvm-ar is searched for.", error.Message)
    // Control: the same installation without the selection resolves, beside clang and then on
    // `PATH`, so the refusal is the invalid selection's and not a missing llvm-ar's.
    let unset = Map.remove "FIDELITY_LLVM_AR" variables
    Assert.Equal("/clang/bin/llvm-ar", resolveArchiver unset (Some "/clang/bin") installed)
    Assert.Equal("/two/llvm-ar", resolveArchiver unset None installed)

[<Fact>]
let ``an invalid FIDELITY_LLVM_AR is refused on a file system holding llvm-ar beside clang and on PATH`` () =
    withScratch (fun directory ->
        let beside = Path.Combine(directory, "clang-bin")
        let searched = Path.Combine(directory, "path-bin")
        let selected = Path.Combine(directory, "selected-bin")
        for holder in [ beside; searched; selected ] do
            Directory.CreateDirectory(holder) |> ignore
            File.WriteAllText(Path.Combine(holder, "llvm-ar"), "fixture")
        let resolveOver (clangDir: string option) (selection: string option) =
            let variables =
                match selection with
                | Some path -> Map.ofList [ "FIDELITY_LLVM_AR", path; "PATH", searched ]
                | None -> Map.ofList [ "PATH", searched ]
            ClangToolchain.resolveArchiverWith (fun name -> Map.tryFind name variables) clangDir File.Exists
        // Controls: without a selection the llvm-ar beside clang is resolved, then the one on
        // `PATH`; a selection naming a file is resolved to that file.
        Assert.Equal(Path.Combine(beside, "llvm-ar"), resolveOver (Some beside) None)
        Assert.Equal(Path.Combine(searched, "llvm-ar"), resolveOver None None)
        Assert.Equal(Path.Combine(selected, "llvm-ar"), resolveOver (Some beside) (Some(Path.Combine(selected, "llvm-ar"))))
        // A selection naming a missing file, a directory, or a directory that holds an llvm-ar
        // is refused; neither other llvm-ar is resolved in its place.
        for invalid in [ Path.Combine(directory, "missing", "llvm-ar"); selected; beside; searched ] do
            for clangDir in [ Some beside; None ] do
                let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> resolveOver clangDir (Some invalid) |> ignore)
                Assert.Equal($"FIDELITY_LLVM_AR selects '{invalid}', which does not exist; no other llvm-ar is searched for.", error.Message))

[<Fact>]
let ``versioned llvm-ar and GNU ar names do not satisfy archiver resolution`` () =
    let installed = [ "/clang/bin/ar"; "/clang/bin/llvm-ar-21"; "/usr/bin/ar"; "/usr/bin/llvm-ar-21"; "/usr/bin/llvm-ar-18"; "/usr/bin/gcc-ar" ]
    let error =
        Assert.Throws<Diagnostics.RefusalException>(fun () ->
            resolveArchiver (Map.ofList [ "PATH", "/usr/bin" ]) (Some "/clang/bin") installed |> ignore)
    Assert.Equal("llvm-ar was not found beside clang or on PATH; set FIDELITY_LLVM_AR.", error.Message)
    // Without a PATH variable there is still no selection.
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> resolveArchiver Map.empty None installed |> ignore)
    Assert.Equal("llvm-ar was not found beside clang or on PATH; set FIDELITY_LLVM_AR.", error.Message)

[<Fact>]
let ``FIDELITY_LLVM_BIN does not select llvm-ar`` () =
    let backend = Map.ofList [ "FIDELITY_LLVM_BIN", "/llvm/bin"; "PATH", "/one:/two" ]
    // An llvm-ar in the backend directory is not selected: the one beside clang, then PATH's, is.
    Assert.Equal("/clang/bin/llvm-ar", resolveArchiver backend (Some "/clang/bin") [ "/llvm/bin/llvm-ar"; "/clang/bin/llvm-ar"; "/two/llvm-ar" ])
    Assert.Equal("/two/llvm-ar", resolveArchiver backend (Some "/clang/bin") [ "/llvm/bin/llvm-ar"; "/two/llvm-ar" ])
    // With no llvm-ar beside clang or on PATH, the one in the backend directory is not selected either.
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> resolveArchiver backend (Some "/clang/bin") [ "/llvm/bin/llvm-ar" ] |> ignore)
    Assert.Equal("llvm-ar was not found beside clang or on PATH; set FIDELITY_LLVM_AR.", error.Message)
    // A backend directory that does not exist is no refusal of the archiver resolution.
    Assert.Equal("/two/llvm-ar", resolveArchiver (Map.add "FIDELITY_LLVM_BIN" "/missing" backend) None [ "/two/llvm-ar" ])
    // The variable is never read.
    let environment, read = recorded backend
    ClangToolchain.resolveArchiverWith environment (Some "/clang/bin") (fun path -> path = "/two/llvm-ar") |> ignore
    Assert.Equal<string list>([ "FIDELITY_LLVM_AR"; "PATH" ], List.ofSeq read)

// ─── The clang directory ───

[<Fact>]
let ``the real path follows a chain of relative and absolute symbolic links and leaves a file as it is`` () =
    withScratch (fun directory ->
        let toolchain = Path.Combine(directory, "lib", "llvm", "bin")
        let bin = Path.Combine(directory, "bin")
        let alternatives = Path.Combine(directory, "alternatives")
        for created in [ toolchain; bin; alternatives ] do Directory.CreateDirectory(created) |> ignore
        let file = Path.Combine(toolchain, "clang")
        File.WriteAllText(file, "fixture")
        // bin/clang -> ../alternatives/clang (relative) -> <directory>/lib/llvm/bin/clang (absolute)
        File.CreateSymbolicLink(Path.Combine(alternatives, "clang"), file) |> ignore
        File.CreateSymbolicLink(Path.Combine(bin, "clang"), Path.Combine("..", "alternatives", "clang")) |> ignore
        Assert.Equal(file, ClangToolchain.realPathOf (Path.Combine(bin, "clang")))
        Assert.Equal(file, ClangToolchain.realPathOf (Path.Combine(alternatives, "clang")))
        Assert.Equal(file, ClangToolchain.realPathOf file)
        // A file that is no link keeps its own full path, `.` and `..` segments removed.
        Assert.Equal(file, ClangToolchain.realPathOf (Path.Combine(bin, "..", "lib", "llvm", "bin", "clang"))))

[<Fact>]
let ``the clang directory holds the file the resolved clang names after its links are followed`` () =
    let real = ClangToolchain.realPathOf (ClangToolchain.path ())
    Assert.True(File.Exists real, $"{real} does not exist")
    Assert.True(isNull (FileInfo(real).LinkTarget), $"{real} is a symbolic link")
    Assert.Equal(Path.GetDirectoryName real, ClangToolchain.realDirectory ())
    Assert.True(Path.IsPathFullyQualified(ClangToolchain.realDirectory ()))

[<Fact>]
let ``the archiver start information launches the resolved llvm-ar with each argument intact`` () =
    let path, digest = ClangToolchain.archiverIdentity ()
    let info = ClangToolchain.arStartInfo [ "rcs"; "lib with space.a"; "probe.o" ]
    Assert.Equal(path, Path.GetFullPath info.FileName)
    Assert.Equal<string list>([ "rcs"; "lib with space.a"; "probe.o" ], List.ofSeq info.ArgumentList)
    Assert.Equal(ClangToolchain.sha256 path, digest)
    // The launched archiver is the one `resolveArchiverWith` selects from this process's
    // environment and the resolved clang's directory: the tests that archive through
    // `arStartInfo` have no archiver selection of their own.
    let selected =
        ClangToolchain.resolveArchiverWith
            (fun name -> System.Environment.GetEnvironmentVariable name |> Option.ofObj)
            (Some(ClangToolchain.realDirectory ()))
            File.Exists
    Assert.Equal(Path.GetFullPath selected, path)

[<Fact>]
let ``the parser launches the resolved compiler with its own resource directory`` () =
    let resolved = ClangToolchain.resolve ()
    let info = CppParser.clangStartInfo [ "-print-resource-dir" ]
    Assert.Equal(resolved.Path, info.FileName)
    Assert.Equal<string list>([ "-print-resource-dir" ], List.ofSeq info.ArgumentList)
    use proc = System.Diagnostics.Process.Start info
    let resourceDir = proc.StandardOutput.ReadToEnd().Trim()
    let errors = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    Assert.True(proc.ExitCode = 0, errors)
    Assert.True(File.Exists(Path.Combine(resourceDir, "include", "stddef.h")))
    let path, digest = ClangToolchain.identity ()
    Assert.Equal(Path.GetFullPath resolved.Path, path)
    Assert.Equal(ClangToolchain.sha256 path, digest)
