namespace Farscape.Core

open System
open System.Diagnostics

/// Every external tool Farscape runs (clang, pkg-config) is started here, with each argument
/// exactly one process argument (#178): a define, an include path, a file path or a package
/// name containing whitespace is never split into further options.
module ProcessLaunch =

    /// A redirected, shell-free process of `program` whose arguments are `args`, one each.
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

/// The clang Farscape launches (#200): the shared parity toolchain's clang 23.1.3, taken from the
/// verified `LLVM-23.1.3-Linux-X64.tar.zst` and installed as identical bytes on every node, so a
/// layout clang measures on Z13 is the layout it measures on a worker. A host clang on `PATH` is
/// whatever the distribution ships (Arch's 23.1.3, Ubuntu's 18.1.3) and is never launched: with no
/// override and no pinned toolchain installed, every launch is refused, naming the paths checked.
module ClangToolchain =

    /// The pinned clang release.
    [<Literal>]
    let PinnedVersion = "23.1.3"

    /// The pinned release's major version, which names its resource directory `lib/clang/23`.
    [<Literal>]
    let PinnedMajor = "23"

    /// The environment variable naming a clang to launch instead of the pinned toolchain's.
    [<Literal>]
    let OverrideVariable = "FARSCAPE_CLANG"

    /// Where a resolved clang came from.
    [<RequireQualifiedAccess>]
    type Source =
        /// `FARSCAPE_CLANG` named it: its own resource directory is the override's responsibility.
        | Override
        /// The shared parity toolchain installed at this path's directory.
        | Pinned

    /// The clang Farscape launches, where it came from, and the resource directory (builtin headers
    /// such as `stddef.h`) every launch passes as `-resource-dir`.
    type Resolution = { Path: string; Origin: Source; ResourceDir: string option }

    /// The pinned toolchain's clang on each node, in order: the user's toolchain under `home`
    /// (Z13: `~/.local/toolchains/llvm-23.1.3`), then the shared install on the worker VMs
    /// gpu-one, gpu-two and gpu-three (10.0.50.202, .201, .203), readable by every worker account.
    let pinnedCandidates (home: string) : string list =
        IO.Path.Combine(home, ".local", "toolchains", "llvm-" + PinnedVersion, "bin", "clang-" + PinnedMajor)
        :: [ for node in [ "gpu-one"; "gpu-two"; "gpu-three" ] -> $"/mnt/apps/{node}/toolchains/llvm-{PinnedVersion}/bin/clang-{PinnedMajor}" ]

    /// The resource directory beside a pinned `bin/clang-23`: `<toolchain>/lib/clang/23`.
    let pinnedResourceDir (clang: string) : string =
        let toolchain = IO.Path.GetDirectoryName(IO.Path.GetDirectoryName clang)
        IO.Path.Combine(toolchain, "lib", "clang", PinnedMajor)

    /// The clang to launch under `environment` (a variable's value, if set), `home` and `exists`
    /// (a file or directory exists): `FARSCAPE_CLANG` when set, else the first installed pinned
    /// candidate with its `lib/clang/23/include` beside it. An override that names nothing, a pinned
    /// clang without its resource directory, and a node with neither are refused, never replaced by
    /// another clang.
    let resolveWith (environment: string -> string option) (home: string) (exists: string -> bool) : Resolution =
        match environment OverrideVariable |> Option.filter (String.IsNullOrWhiteSpace >> not) with
        | Some path when exists path -> { Path = path; Origin = Source.Override; ResourceDir = None }
        | Some path -> invalidOp $"{OverrideVariable} names '{path}', which does not exist; unset it to launch the pinned clang {PinnedVersion}."
        | None ->
            let candidates = pinnedCandidates home
            match candidates |> List.tryFind exists with
            | Some path ->
                let resourceDir = pinnedResourceDir path
                let builtinHeaders = IO.Path.Combine(resourceDir, "include")
                if exists builtinHeaders then
                    { Path = path; Origin = Source.Pinned; ResourceDir = Some resourceDir }
                else
                    invalidOp $"the pinned clang {PinnedVersion} at '{path}' has no resource directory '{builtinHeaders}'; install lib/clang/{PinnedMajor}/include from the same verified asset."
            | None ->
                let checkedPaths = String.concat ", " candidates
                invalidOp $"no pinned clang {PinnedVersion} is installed (checked {checkedPaths}) and {OverrideVariable} is unset; Farscape never launches a host clang from PATH."

    let private resolution =
        lazy (resolveWith
                (fun name -> Environment.GetEnvironmentVariable name |> Option.ofObj)
                (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
                (fun path -> IO.File.Exists path || IO.Directory.Exists path))

    /// The clang this process launches, resolved once (a refusal is raised again on every call).
    let resolve () : Resolution = resolution.Value

    /// The program path every clang launch uses.
    let path () : string = resolution.Value.Path

    /// `args` as `resolved` launches them: a pinned clang is always told its resource directory, so
    /// it never looks for builtin headers anywhere else.
    let argumentsFor (resolved: Resolution) (args: string seq) : string list =
        match resolved.ResourceDir with
        | Some resourceDir -> "-resource-dir" :: resourceDir :: List.ofSeq args
        | None -> List.ofSeq args

    /// The process every clang launch uses (the parser's and the test harness's alike).
    let startInfo (args: string seq) : Diagnostics.ProcessStartInfo =
        let resolved = resolve ()
        ProcessLaunch.startInfo resolved.Path (argumentsFor resolved args)

    /// The lowercase hexadecimal sha256 of the file at `file`.
    let sha256 (file: string) : string =
        use stream = IO.File.OpenRead file
        Security.Cryptography.SHA256.HashData stream |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()

    /// The file the resolved clang launches and its sha256, for receipts: a suite's count on two
    /// nodes compares only with the same digest on both.
    let identity () : string * string =
        let resolved = resolve ()
        IO.Path.GetFullPath resolved.Path, sha256 resolved.Path
