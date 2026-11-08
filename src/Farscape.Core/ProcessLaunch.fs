namespace Farscape.Core

open System
open System.Diagnostics

/// Start external tools without a shell and preserve each argument as one argument.
module ProcessLaunch =
    let startInfo (program: string) (args: string seq) : ProcessStartInfo =
        let info = ProcessStartInfo()
        info.FileName <- program
        for arg in args do info.ArgumentList.Add arg
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info

    /// The arguments as a shell would need them, for verbose output only.
    let commandLine (args: string seq) =
        args |> Seq.map (fun arg -> if arg |> Seq.exists Char.IsWhiteSpace then "'" + arg + "'" else arg) |> String.concat " "

/// Resolve the clang used for C header parsing and measurement.
module ClangToolchain =
    [<RequireQualifiedAccess>]
    type Source =
        | Override
        | Toolchain
        | Path

    type Resolution = { Path: string; Origin: Source; ResourceDir: string option }

    /// Explicit configuration must identify an existing file; a bad configuration is not
    /// silently replaced with a different compiler.
    let resolveWith (environment: string -> string option) (_home: string) (exists: string -> bool) : Resolution =
        let configured name = environment name |> Option.filter (String.IsNullOrWhiteSpace >> not)
        let selected origin name path =
            if exists path then { Path = path; Origin = origin; ResourceDir = None }
            else invalidOp $"{name} selects '{path}', which does not exist."
        match configured "FIDELITY_CLANG" with
        | Some path -> selected Source.Override "FIDELITY_CLANG" path
        | None ->
            match configured "FIDELITY_LLVM_BIN" with
            | Some directory -> selected Source.Toolchain "FIDELITY_LLVM_BIN" (IO.Path.Combine(directory, "clang"))
            | None ->
                let directories = configured "PATH" |> Option.defaultValue "" |> fun path -> path.Split(IO.Path.PathSeparator)
                directories
                |> Array.map (fun directory -> IO.Path.Combine((if directory = "" then "." else directory), "clang"))
                |> Array.tryFind exists
                |> Option.map (fun path -> { Path = path; Origin = Source.Path; ResourceDir = None })
                |> Option.defaultWith (fun () -> invalidOp "clang was not found on PATH; set FIDELITY_CLANG or FIDELITY_LLVM_BIN.")

    let private resolution =
        lazy (resolveWith
                (fun name -> Environment.GetEnvironmentVariable name |> Option.ofObj)
                (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
                IO.File.Exists)

    let resolve () : Resolution = resolution.Value
    let path () : string = resolution.Value.Path
    let argumentsFor (_resolved: Resolution) (args: string seq) : string list = List.ofSeq args

    let startInfo (args: string seq) : ProcessStartInfo =
        let resolved = resolve ()
        ProcessLaunch.startInfo resolved.Path (argumentsFor resolved args)

    let sha256 (file: string) : string =
        use stream = IO.File.OpenRead file
        Security.Cryptography.SHA256.HashData stream |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()

    let identity () : string * string =
        let resolved = resolve ()
        IO.Path.GetFullPath resolved.Path, sha256 resolved.Path
