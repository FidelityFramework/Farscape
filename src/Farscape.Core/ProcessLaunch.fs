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
