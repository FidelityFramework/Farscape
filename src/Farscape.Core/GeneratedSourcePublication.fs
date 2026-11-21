/// Host publication for BindingGenerator's already rendered project outputs.
module internal Farscape.Core.GeneratedSourcePublication

open System
open System.IO
open Fidelity.Data.JSON

let private schema = "farscape.generated-sources/1"

type Owner = PilotFile of string | Standalone of libraryName: string * namespaceName: string

let private relative (root: string) (path: string) = Path.GetRelativePath(root, path)

let private under root path =
    let name = relative root path
    name <> "." && name <> ".." && not (Path.IsPathRooted name)
    && not (name.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))

let private linked anchors ownedRoots (path: string) =
    let contains root candidate = root = candidate || under root candidate
    let rec walk candidate =
        // Stop at the invocation's selected ancestry, after checking the leaf and every owned segment.
        if candidate <> path && List.exists (contains candidate) anchors
           && not (ownedRoots |> List.exists (fun root -> contains root candidate)) then false
        else
            not (isNull (FileInfo(candidate).LinkTarget))
            || (match Path.GetDirectoryName candidate with null -> false | parent -> walk parent)
    walk path

let private deltaDirectory (pilot: string) packageDir outputDir =
    let canonical = Path.Combine(packageDir, "src")
    let output = Path.TrimEndingDirectorySeparator(Path.GetFullPath outputDir)
    let outside =
        if (output = canonical || Path.GetDirectoryName output = canonical) && under canonical pilot then Some packageDir
        elif under output pilot then Path.GetDirectoryName output |> Option.ofObj
        else None
    match outside with
    | Some directory ->
        let name = Path.ChangeExtension(relative directory pilot, ".delta")
        Path.Combine(directory, Uri.EscapeDataString name)
    | None -> Path.ChangeExtension(pilot, ".delta")

let private record pilotDir pilotName packageDir outputDir bridgeDir sources bridgeSources =
    let entries root paths = paths |> List.map (relative root >> JsonValue.String) |> JsonValue.Array
    JsonValue.Object [
        "schema", JsonValue.String schema
        "owner", JsonValue.String pilotName
        "package", JsonValue.String (relative pilotDir packageDir)
        "sourceRoot", JsonValue.String (relative pilotDir outputDir)
        "sources", entries outputDir sources
        "bridgeRoot", JsonValue.String (relative pilotDir bridgeDir)
        "bridgeSources", entries bridgeDir bridgeSources ]
    |> Json.serialize

let private readMembers (pilot: string) text =
    let pilotDir = Path.GetDirectoryName pilot
    let pilotName = Path.GetFileName pilot
    let invalid = "Invalid generated-source ownership record."
    let members role rootName entries =
        let root = Path.GetFullPath(rootName, pilotDir)
        let paths =
            entries |> List.choose (function
                | JsonValue.String name when not (String.IsNullOrEmpty name || Path.IsPathRooted name) ->
                    let path = Path.GetFullPath(name, root)
                    let allowed = role <> "bridge" || name = "Callbacks.clef" || name = "Async.clef"
                    if allowed && under root path && relative root path = name && Path.GetExtension path = ".clef" then Some (path, Path.Combine(role, name))
                    else None
                | _ -> None)
        if relative pilotDir root = rootName && paths.Length = entries.Length then Ok paths else Error invalid
    match Json.parse text with
    | Ok (JsonValue.Object fields) ->
        match List.sortBy fst fields with
        | [ "bridgeRoot", JsonValue.String bridgeRoot; "bridgeSources", JsonValue.Array bridgeSources
            "owner", JsonValue.String owner; "package", JsonValue.String package; "schema", JsonValue.String version
            "sourceRoot", JsonValue.String sourceRoot; "sources", JsonValue.Array sources ]
            when owner = pilotName && version = schema && sourceRoot <> "" && bridgeRoot <> "" ->
            match members "source" sourceRoot sources, members "bridge" bridgeRoot bridgeSources with
            | Ok native, Ok bridge ->
                let all = native @ bridge
                let unique = Map.ofList all
                let packageDir = Path.GetFullPath(package, pilotDir)
                let outputDir = Path.GetFullPath(sourceRoot, pilotDir)
                let packageRoot = Path.TrimEndingDirectorySeparator packageDir
                let sourceRoot = Path.TrimEndingDirectorySeparator outputDir
                if unique.Count = all.Length && relative pilotDir packageDir = package && (sourceRoot = packageRoot || under packageRoot sourceRoot) then
                    Ok (unique, deltaDirectory pilot packageRoot sourceRoot, [sourceRoot; Path.GetFullPath(bridgeRoot, pilotDir)])
                else Error invalid
            | _ -> Error invalid
        | _ -> Error invalid
    | _ -> Error invalid

// The replacement inode is created in the target directory, including across mounted volumes.
let private replaceFile source (target: string) =
    let parent = Path.GetDirectoryName target
    Directory.CreateDirectory parent |> ignore
    let temporary = Path.Combine(parent, ".farscape-" + Guid.NewGuid().ToString("N"))
    try
        File.Copy(source, temporary)
        File.Move(temporary, target, true)
    finally
        if File.Exists temporary then File.Delete temporary

let private install (target, staged) =
    match staged with
    | Some file -> replaceFile file target
    | None -> if File.Exists target then File.Delete target

let private commit backups changes =
    let rec apply undo pending =
        match pending with
        | [] -> Ok ()
        | ((target, _) as change) :: rest ->
            let result = try install change; Ok () with ex -> Error ex.Message
            match result with
            | Ok () -> apply ((target, Map.find target backups) :: undo) rest
            | Error failure ->
                let recovery =
                    undo |> List.choose (fun previous ->
                        try install previous; None with ex -> Some ex.Message)
                Error (failure, recovery)
    apply [] changes

// Move each reserved review folder aside before installing the next review or release.
let rec private withReviewFolders folders action =
    match folders with
    | [] -> action ()
    | (folder: string) :: rest ->
        let backup = folder + ".farscape-old-" + Guid.NewGuid().ToString("N")
        let saved =
            try
                let existed = Directory.Exists folder
                if existed then Directory.Move(folder, backup)
                Ok existed
            with ex -> Error (ex.Message, [])
        match saved with
        | Error why -> Error why
        | Ok existed ->
            let result = try withReviewFolders rest action with ex -> Error (ex.Message, [])
            match result with
            | Ok () ->
                if existed then
                    try Directory.Delete(backup, true) with :? IOException -> () | :? UnauthorizedAccessException -> ()
                Ok ()
            | Error (why, issues) ->
                try
                    if Directory.Exists folder then Directory.Delete(folder, true)
                    if existed then Directory.Move(backup, folder)
                    Error (why, issues)
                with ex -> Error (why, (backup + ": " + ex.Message) :: issues)

let publish owner packageDir outputDir bridgeDir retainDelta (sources: (string * string) list)
            (bridgeSources: (string * string) list) (metadata: (string * string) list) =
    try
        let pilot, lockFile, mode, access =
            match owner with
            | PilotFile path ->
                let path = Path.GetFullPath path
                path, path, FileMode.Open, FileAccess.Read
            | Standalone (library, namespaceName) ->
                let output = DirectoryInfo(Path.GetFullPath outputDir)
                let parent =
                    if isNull output.Parent then output
                    elif output.Parent.Name = "src" then output.Parent.Parent
                    else output.Parent
                let identity =
                    [relative parent.FullName output.FullName; library; namespaceName]
                    |> List.map JsonValue.String |> JsonValue.Array |> Json.serialize
                    |> System.Text.Encoding.UTF8.GetBytes |> System.Security.Cryptography.SHA256.HashData
                    |> Convert.ToHexString
                let anchor = Path.Combine(parent.FullName, ".farscape-" + identity + ".standalone")
                anchor, anchor + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite
        let pilotDir = Path.GetDirectoryName pilot
        let pilotName = Path.GetFileName pilot
        let receipt = pilot + ".generated-sources.json"
        let linkedOwnership = linked [pilotDir] []
        if linkedOwnership lockFile || linkedOwnership receipt then Error $"Generated ownership path traverses a symbolic link: {receipt}"
        else
            Directory.CreateDirectory pilotDir |> ignore
            use ownership = new FileStream(lockFile, mode, access, FileShare.None)
            let currentRecord = record pilotDir pilotName packageDir outputDir bridgeDir (List.map fst sources) (List.map fst bridgeSources)
            let existing = File.Exists receipt
            let previous =
                if existing then readMembers pilot (File.ReadAllText receipt)
                else Ok (Map.empty, deltaDirectory pilot packageDir outputDir, [])
            match previous, readMembers pilot currentRecord with
            | Error reason, _ | _, Error reason -> Error reason
            | Ok (before, oldDelta, previousRoots), Ok (current, newDelta, currentRoots) ->
                let packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath packageDir)
                let linked = linked [packageRoot; pilotDir] (currentRoots @ previousRoots)
                let fresh =
                    (sources @ bridgeSources @ metadata @ [receipt, currentRecord])
                    |> List.map (fun (path, contents) -> Path.GetFullPath path, contents)
                let paths = List.map fst fresh
                let prior = if existing then before else current |> Map.filter (fun path _ -> File.Exists path)
                let priorPaths = prior |> Map.toList |> List.map fst
                let affected = List.distinct (paths @ priorPaths)
                let deltaFolders = List.distinct [oldDelta; newDelta]
                if retainDelta && under (Path.GetFullPath outputDir) newDelta then Error "Review requires a delta outside the selected source root."
                elif (Set.ofList paths).Count <> paths.Length then Error "Generated output destinations overlap."
                elif deltaFolders |> List.exists (fun folder ->
                        linked folder || File.Exists folder || (affected |> List.exists (fun path -> path = folder || under folder path))) then
                    Error "Reserved review delta conflicts with a symbolic link, regular file, or generated destination."
                else
                    match affected |> List.tryFind (fun path -> linked path || Directory.Exists path) with
                    | Some path -> Error $"Generated output traverses a symbolic link or names a directory: {path}"
                    | None ->
                        let work = Directory.CreateDirectory(Path.Combine(pilotDir, ".farscape-stage-" + Guid.NewGuid().ToString("N"))).FullName
                        let result =
                            try
                                let writes =
                                    fresh |> List.mapi (fun index (path, contents) ->
                                        let staged = Path.Combine(work, $"new-{index}")
                                        File.WriteAllText(staged, contents)
                                        path, staged)
                                let collision =
                                    writes |> List.tryFind (fun (path, _) ->
                                        existing && Map.containsKey path current
                                        && not (Map.containsKey path before) && File.Exists path)
                                match collision with
                                | Some (path, _) -> Error ($"Existing Clef source has no matching generated ownership: {path}", [])
                                | None ->
                                    let backups =
                                        affected |> List.mapi (fun index path ->
                                            if File.Exists path then
                                                let saved = Path.Combine(work, $"old-{index}")
                                                File.Copy(path, saved)
                                                path, Some saved
                                            else path, None) |> Map.ofList
                                    withReviewFolders deltaFolders (fun () ->
                                        if retainDelta then
                                            Directory.CreateDirectory newDelta |> ignore
                                            for KeyValue(path, deltaName) in prior do
                                                match Map.find path backups with
                                                | Some saved -> replaceFile saved (Path.Combine(newDelta, deltaName))
                                                | None -> ()
                                        let changes =
                                            (priorPaths |> List.map (fun path -> path, None))
                                            @ (writes |> List.map (fun (path, staged) -> path, Some staged))
                                        commit backups changes)
                            with ex -> Error (ex.Message, [])
                        match result with
                        | Error (reason, issues) when not issues.IsEmpty ->
                            Error (sprintf "%s; restoration failed: %s; recovery files: %s" reason (String.concat "; " issues) work)
                        | outcome ->
                            try Directory.Delete(work, true) with :? IOException -> () | :? UnauthorizedAccessException -> ()
                            outcome
                            |> Result.map (fun () -> if retainDelta then Some newDelta else None)
                            |> Result.mapError fst
    with ex -> Error ex.Message
