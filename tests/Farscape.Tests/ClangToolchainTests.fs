module Farscape.Tests.ClangToolchainTests

open System.IO
open Xunit
open Farscape.Core

let private resolve variables installed =
    ClangToolchain.resolveWith (fun name -> Map.tryFind name variables) "/fixture" (fun path -> List.contains path installed)

[<Fact>]
let ``clang selection uses explicit compiler then LLVM bin then unversioned PATH`` () =
    let variables = Map.ofList [ "FIDELITY_CLANG", "/explicit/clang"; "FIDELITY_LLVM_BIN", "/llvm/bin"; "PATH", "/one:/two" ]
    let installed = [ "/explicit/clang"; "/llvm/bin/clang"; "/two/clang"; "/one/clang-21" ]
    let explicit = resolve variables installed
    Assert.Equal("/explicit/clang", explicit.Path)
    Assert.Equal(ClangToolchain.Source.Override, explicit.Origin)
    let toolchain = resolve (Map.remove "FIDELITY_CLANG" variables) installed
    Assert.Equal("/llvm/bin/clang", toolchain.Path)
    Assert.Equal(ClangToolchain.Source.Toolchain, toolchain.Origin)
    let path = resolve (variables |> Map.remove "FIDELITY_CLANG" |> Map.remove "FIDELITY_LLVM_BIN") installed
    Assert.Equal("/two/clang", path.Path)
    Assert.Equal(ClangToolchain.Source.Path, path.Origin)

[<Fact>]
let ``invalid explicit configuration does not silently select another compiler`` () =
    let variables = Map.ofList [ "FIDELITY_CLANG", "/missing/clang"; "FIDELITY_LLVM_BIN", "/llvm/bin" ]
    let error = Assert.Throws<System.InvalidOperationException>(fun () -> resolve variables [ "/llvm/bin/clang" ] |> ignore)
    Assert.Contains("FIDELITY_CLANG", error.Message)
    let missing = Map.ofList [ "FIDELITY_LLVM_BIN", "/missing"; "PATH", "/usr/bin" ]
    let error = Assert.Throws<System.InvalidOperationException>(fun () -> resolve missing [ "/usr/bin/clang" ] |> ignore)
    Assert.Contains("FIDELITY_LLVM_BIN", error.Message)

[<Fact>]
let ``versioned clang names on PATH do not satisfy resolution`` () =
    let variables = Map.ofList [ "PATH", "/usr/bin" ]
    let error = Assert.Throws<System.InvalidOperationException>(fun () -> resolve variables [ "/usr/bin/clang-21"; "/usr/bin/clang-20"; "/usr/bin/clang-19" ] |> ignore)
    Assert.Contains("clang was not found on PATH", error.Message)

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
