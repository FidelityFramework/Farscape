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

/// Resolve the clang that parses and measures C and C++ headers and compiles foreign C and C++
/// sources ahead of Composer, and the `llvm-ar` that archives the objects it compiles.
/// `FIDELITY_LLVM_BIN` selects the tools of Composer's backend (`opt`, `ld.lld`, `llvm-dis`);
/// this module does not read it.
module ClangToolchain =
    [<RequireQualifiedAccess>]
    type Source =
        | Override
        | Path

    type Resolution = { Path: string; Origin: Source; ResourceDir: string option }

    /// The first file named `tool` in a directory of `PATH`. An empty `PATH` entry is the
    /// current directory. Only the exact name is searched for, never a versioned one.
    let private onPath (configured: string -> string option) (exists: string -> bool) (tool: string) : string option =
        let directories = configured "PATH" |> Option.defaultValue "" |> fun path -> path.Split(IO.Path.PathSeparator)
        directories
        |> Array.map (fun directory -> IO.Path.Combine((if directory = "" then "." else directory), tool))
        |> Array.tryFind exists

    /// `FIDELITY_CLANG` selects the compiler file, else `PATH` is searched for `clang`. A
    /// `FIDELITY_CLANG` naming no file is refused without `PATH` being searched.
    let resolveWith (environment: string -> string option) (_home: string) (exists: string -> bool) : Resolution =
        let configured name = environment name |> Option.filter (String.IsNullOrWhiteSpace >> not)
        match configured "FIDELITY_CLANG" with
        | Some path ->
            if exists path then { Path = path; Origin = Source.Override; ResourceDir = None }
            else invalidOp $"FIDELITY_CLANG selects '{path}', which does not exist; PATH is not searched."
        | None ->
            match onPath configured exists "clang" with
            | Some path -> { Path = path; Origin = Source.Path; ResourceDir = None }
            | None -> invalidOp "clang was not found on PATH; set FIDELITY_CLANG."

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

    /// The full path of the file `file` names once every symbolic link at its final component
    /// is followed; the full path of `file` itself when it is no symbolic link.
    let realPathOf (file: string) : string =
        match (IO.FileInfo file).ResolveLinkTarget(true) with
        | null -> IO.Path.GetFullPath file
        | target -> target.FullName

    /// The directory holding the file the resolved clang names after its symbolic links are
    /// followed: `/usr/lib/llvm-18/bin` where `/usr/bin/clang` links to `../lib/llvm-18/bin/clang`.
    let realDirectory () : string =
        IO.Path.GetDirectoryName(realPathOf (path ()))

    /// Resolve the `llvm-ar` that archives the objects clang compiles. `FIDELITY_LLVM_AR`
    /// selects the archiver file; a `FIDELITY_LLVM_AR` naming no file is refused and no other
    /// location is searched. Without it, the `llvm-ar` in `clangDir` is selected, else `PATH`
    /// is searched for `llvm-ar`. No other archiver name is accepted.
    let resolveArchiverWith (environment: string -> string option) (clangDir: string option) (exists: string -> bool) : string =
        let configured name = environment name |> Option.filter (String.IsNullOrWhiteSpace >> not)
        match configured "FIDELITY_LLVM_AR" with
        | Some path ->
            if exists path then path
            else invalidOp $"FIDELITY_LLVM_AR selects '{path}', which does not exist; no other llvm-ar is searched for."
        | None ->
            match clangDir |> Option.map (fun directory -> IO.Path.Combine(directory, "llvm-ar")) |> Option.filter exists with
            | Some beside -> beside
            | None ->
                match onPath configured exists "llvm-ar" with
                | Some path -> path
                | None -> invalidOp "llvm-ar was not found beside clang or on PATH; set FIDELITY_LLVM_AR."

    let private archiver =
        lazy (resolveArchiverWith
                (fun name -> Environment.GetEnvironmentVariable name |> Option.ofObj)
                (Some (realDirectory ()))
                IO.File.Exists)

    /// Start information for the resolved `llvm-ar`, each argument passed as one argument.
    let arStartInfo (args: string seq) : ProcessStartInfo =
        ProcessLaunch.startInfo archiver.Value args

    let archiverIdentity () : string * string =
        IO.Path.GetFullPath archiver.Value, sha256 archiver.Value
