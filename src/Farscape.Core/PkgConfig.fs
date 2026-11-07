namespace Farscape.Core

open System

/// Include paths and defines from `pkg-config --cflags <module>` (#178). The module name is one
/// process argument, the output is split into words as pkg-config quotes and escapes them for a
/// shell, and every word must be an include path (`-I<path>`) or a define (`-D<define>`), the
/// only two facts a pilot carries. Any other word (`-fms-extensions`, `-isystem`, `-pthread`)
/// is refused: dropping it would hide a flag, and passing it would let pkg-config choose clang's
/// language mode.
module PkgConfig =

    /// A pkg-config module name: letters, digits and `_ . + -`, not starting with `-`
    /// (`gtk+-3.0`, `webkit2gtk-4.1`). Anything else is refused (FS9125).
    /// A module name and pkg-config's output are command-line and tool input, not header
    /// declarations, so their refusals have no source position (#184).
    let moduleNameRefusal (name: string) : Diagnostics.Diagnostic option =
        if Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9_][A-Za-z0-9_.+\-]*\z") then None
        else
            Some (Diagnostics.diagnostic Diagnostics.PkgConfigModuleMalformed
                    $"pkg-config module name '{name}' is not a module name (letters, digits and _ . + -, not starting with -); it reaches pkg-config as one argument and carries no option."
                    (Diagnostics.Located.Unknown "a pkg-config module name is command-line input, not a header declaration"))

    /// The words of pkg-config output under POSIX shell quoting, without any expansion: blanks
    /// separate words, a backslash escapes the next character (pkg-config escapes a space in a
    /// path as `\ `), single quotes are literal, and inside double quotes a backslash escapes
    /// only `"`, `\`, `$` and `` ` ``. None when the output ends inside a quote or an escape.
    let words (output: string) : string list option =
        let result = ResizeArray<string>()
        let current = Text.StringBuilder()
        let mutable inWord = false
        let mutable i = 0
        let mutable broken = false
        let n = output.Length
        while i < n && not broken do
            let c = output.[i]
            match c with
            | ' ' | '\t' | '\n' | '\r' ->
                if inWord then
                    result.Add(current.ToString())
                    current.Clear() |> ignore
                    inWord <- false
                i <- i + 1
            | '\\' ->
                if i + 1 >= n then broken <- true
                else
                    current.Append(output.[i + 1]) |> ignore
                    inWord <- true
                    i <- i + 2
            | '\'' ->
                let close = output.IndexOf('\'', i + 1)
                if close < 0 then broken <- true
                else
                    current.Append(output, i + 1, close - i - 1) |> ignore
                    inWord <- true
                    i <- close + 1
            | '"' ->
                inWord <- true
                i <- i + 1
                let mutable closed = false
                while i < n && not closed && not broken do
                    match output.[i] with
                    | '"' -> closed <- true; i <- i + 1
                    | '\\' when i + 1 < n && "\"\\$`".Contains(output.[i + 1]) ->
                        current.Append(output.[i + 1]) |> ignore
                        i <- i + 2
                    | ch -> current.Append(ch) |> ignore; i <- i + 1
                if not closed then broken <- true
            | ch ->
                current.Append(ch) |> ignore
                inWord <- true
                i <- i + 1
        if broken then None
        else
            if inWord then result.Add(current.ToString())
            Some (List.ofSeq result)

    /// The include paths and defines of one module's `--cflags` output, in order, or the refusal
    /// of the first word that is neither (FS9126), of a malformed define (FS9124), or of output
    /// that does not split into words (FS9126).
    let parseCflagsDiagnosed (moduleName: string) (output: string) : Result<string list * string list, Diagnostics.Diagnostic> =
        let refuse (detail: string) =
            Error (Diagnostics.diagnostic Diagnostics.PkgConfigFlagRefused
                     $"pkg-config --cflags for '{moduleName}' {detail}; a pilot carries only include paths and defines, so the output is refused rather than dropped or reinterpreted."
                     (Diagnostics.Located.Unknown $"pkg-config --cflags output for '{moduleName}' is tool output, not a header declaration"))
        match words output with
        | None -> refuse $"ends inside a quotation or an escape: '{output.Trim()}'"
        | Some tokens ->
            let rec classify (includes: string list) (defines: string list) = function
                | [] -> Ok (List.rev includes, List.rev defines)
                | (token: string) :: rest when token.StartsWith "-I" && token.Length > 2 ->
                    classify (token.Substring 2 :: includes) defines rest
                | token :: rest when token.StartsWith "-D" && token.Length > 2 ->
                    let define = token.Substring 2
                    match CppParser.defineRefusal define with
                    | Some refusal -> Error refusal
                    | None -> classify includes (define :: defines) rest
                | token :: _ -> refuse $"gives '{token}', which is not -I<path> or -D<define>"
            classify [] [] tokens

    /// `parseCflagsDiagnosed`, reporting a refusal as its text.
    let parseCflags (moduleName: string) (output: string) : Result<string list * string list, string> =
        parseCflagsDiagnosed moduleName output |> Result.mapError Diagnostics.render

    /// Run `pkg-config --cflags <moduleName>` with `environment` added to its own, and read its
    /// include paths and defines. A failure carries its diagnostic when it is an FS91xx refusal.
    let runDiagnosed (environment: (string * string) list) (moduleName: string) : Result<string list * string list, Diagnostics.Refusal> =
        match moduleNameRefusal moduleName with
        | Some refusal -> Error (Diagnostics.refusalOf refusal)
        | None ->
            try
                let info = ProcessLaunch.startInfo "pkg-config" ["--cflags"; moduleName]
                for name, value in environment do info.Environment.[name] <- value
                use proc = Diagnostics.Process.Start info
                let output = proc.StandardOutput.ReadToEnd()
                let stderr = proc.StandardError.ReadToEnd().Trim()
                proc.WaitForExit()
                if proc.ExitCode <> 0 then Error (Diagnostics.uncoded $"pkg-config failed for '{moduleName}': {stderr}")
                else parseCflagsDiagnosed moduleName output |> Result.mapError Diagnostics.refusalOf
            with ex ->
                Error (Diagnostics.uncoded $"Failed to run pkg-config: {ex.Message}")

    /// `runDiagnosed`, reporting a failure as its text.
    let run (environment: (string * string) list) (moduleName: string) : Result<string list * string list, string> =
        runDiagnosed environment moduleName |> Result.mapError _.Text

    /// `runDiagnosed` with pkg-config's own environment.
    let resolveDiagnosed (moduleName: string) = runDiagnosed [] moduleName

    /// `run` with pkg-config's own environment.
    let resolve (moduleName: string) = run [] moduleName
