namespace Farscape.Core

open Fidelity.Toml
open PilotTypes

/// Serialization and deserialization of PilotProject to/from TOML format.
///
/// Uses Fidelity.Toml 0.1.1 for type-safe TOML handling.
/// All functions are pure: TomlDocument → Result<PilotProject, string>
/// and PilotProject → TomlDocument.
module PilotSerializer =

    // =========================================================================
    // Serialization: PilotProject → TomlDocument
    // =========================================================================

    /// Convert a LibrarySpec to a TomlTable.
    /// Single header serializes as `header = "..."` for backward compat.
    /// Multiple headers serialize as `headers = [...]`.
    let private serializeLibrary (lib: LibrarySpec) : TomlValue =
        let table =
            TomlTable.empty
            |> TomlTable.add "name" (TomlValue.String lib.Name)
        let table =
            match lib.Headers with
            | [single] -> TomlTable.add "header" (TomlValue.String single) table
            | multiple -> TomlTable.add "headers" (TomlValue.Array (multiple |> List.map TomlValue.String)) table
        let table =
            if lib.IncludePaths.IsEmpty then table
            else TomlTable.add "include_paths" (TomlValue.Array (lib.IncludePaths |> List.map TomlValue.String)) table
        let table =
            if lib.Defines.IsEmpty then table
            else TomlTable.add "defines" (TomlValue.Array (lib.Defines |> List.map TomlValue.String)) table
        TomlValue.Table table

    /// Convert an OutputSpec to a TomlTable.
    let private serializeOutput (output: OutputSpec) : TomlValue =
        TomlTable.empty
        |> TomlTable.add "mode" (TomlValue.String output.Mode)
        |> TomlTable.add "directory" (TomlValue.String output.Directory)
        |> TomlValue.Table

    /// Convert a NamespaceSpec to a TomlTable (for [[namespace]] array entries).
    let private serializeNamespace (ns: NamespaceSpec) : TomlValue =
        let table =
            TomlTable.empty
            |> TomlTable.add "name" (TomlValue.String ns.Name)
            |> TomlTable.add "description" (TomlValue.String ns.Description)
            |> TomlTable.add "library" (TomlValue.String ns.Library)
            |> TomlTable.add "prefixes" (TomlValue.Array (ns.Prefixes |> List.map TomlValue.String))
        let table =
            if ns.Functions.IsEmpty then table
            else TomlTable.add "functions" (TomlValue.Array (ns.Functions |> List.map TomlValue.String)) table
        TomlValue.Table table

    /// Serialize an ErrorConvention to its TOML string value.
    let private serializeConvention (c: ErrorConvention) : string =
        match c with
        | Errno -> "errno"
        | ReturnCode -> "return_code"
        | EnumErrorCode _ -> "enum_error_code"
        | NoErrorConvention -> "none"

    /// Serialize ErrorConventionSpec to a TOML table value.
    let private serializeErrorConventions (spec: ErrorConventionSpec) : TomlValue =
        let table =
            TomlTable.empty
            |> TomlTable.add "default" (TomlValue.String (serializeConvention spec.Default))
        // For EnumErrorCode, serialize additional fields at the top level
        let table =
            match spec.Default with
            | EnumErrorCode (errorType, successValue, errorStringFn, errorNameFn) ->
                let t = table
                         |> TomlTable.add "error_type" (TomlValue.String errorType)
                         |> TomlTable.add "success_value" (TomlValue.String successValue)
                let t =
                    match errorStringFn with
                    | Some fn -> TomlTable.add "error_string_fn" (TomlValue.String fn) t
                    | None -> t
                match errorNameFn with
                | Some fn -> TomlTable.add "error_name_fn" (TomlValue.String fn) t
                | None -> t
            | _ -> table
        let table =
            if spec.Overrides.IsEmpty then table
            else
                let overrideTable =
                    spec.Overrides
                    |> Map.fold (fun t k v -> TomlTable.add k (TomlValue.String (serializeConvention v)) t) TomlTable.empty
                TomlTable.add "overrides" (TomlValue.Table overrideTable) table
        TomlValue.Table table

    let private serializeOptions (opts: ProjectOptions) : TomlValue =
        let table = TomlTable.empty
        let table =
            if opts.AbiCriticalStructs.IsEmpty then table
            else TomlTable.add "abi_critical_structs" (TomlValue.Array (opts.AbiCriticalStructs |> List.map TomlValue.String)) table
        let table =
            if opts.GenerateDescriptors then
                TomlTable.add "generate_descriptors" (TomlValue.Boolean true) table
            else table
        TomlValue.Table table

    /// Serialize a complete PilotProject to a TomlDocument.
    let serialize (project: PilotProject) : TomlDocument =
        let table =
            TomlTable.empty
            |> TomlTable.add "library" (serializeLibrary project.Library)
            |> TomlTable.add "output" (serializeOutput project.Output)
            |> TomlTable.add "namespace" (TomlValue.Array (project.Namespaces |> List.map serializeNamespace))
        let table =
            match project.ErrorConventions with
            | Some spec -> table |> TomlTable.add "error_conventions" (serializeErrorConventions spec)
            | None -> table
        match project.Options with
        | Some opts -> table |> TomlTable.add "options" (serializeOptions opts)
        | None -> table

    /// Render a PilotProject to a TOML string.
    let toTomlString (project: PilotProject) : string =
        project |> serialize |> Toml.serialize

    // =========================================================================
    // Deserialization: TomlDocument → Result<PilotProject, string>
    // =========================================================================

    /// Helper: require a string field from a TomlTable.
    let private requireString (fieldName: string) (sectionName: string) (table: TomlTable) : Result<string, string> =
        match TomlTable.tryFind fieldName table with
        | Some (TomlValue.String s) -> Ok s
        | Some _ -> Error $"[{sectionName}].{fieldName} must be a string"
        | None -> Error $"[{sectionName}].{fieldName} is required"

    /// Helper: get an optional string list from a TomlTable.
    let private optionalStringArray (fieldName: string) (table: TomlTable) : string list =
        match TomlTable.tryFind fieldName table with
        | Some (TomlValue.Array arr) ->
            arr |> List.choose (function TomlValue.String s -> Some s | _ -> None)
        | _ -> []

    /// Parse a LibrarySpec from the [library] table.
    /// Accepts either `headers = [...]` (new) or `header = "..."` (backward compat).
    let private deserializeLibrary (doc: TomlDocument) : Result<LibrarySpec, string> =
        match Toml.getTable "library" doc with
        | None -> Error "Missing [library] section"
        | Some table ->
            let nameResult = requireString "name" "library" table
            let headersResult =
                match TomlTable.tryFind "headers" table with
                | Some (TomlValue.Array arr) ->
                    let headers = arr |> List.choose (function TomlValue.String s -> Some s | _ -> None)
                    if headers.IsEmpty then Error "[library].headers must not be empty"
                    else Ok headers
                | Some _ -> Error "[library].headers must be an array of strings"
                | None ->
                    match requireString "header" "library" table with
                    | Ok h -> Ok [h]
                    | Error _ -> Error "[library] requires either 'header' or 'headers'"
            match nameResult, headersResult with
            | Ok name, Ok headers ->
                Ok { Name = name
                     Headers = headers
                     IncludePaths = optionalStringArray "include_paths" table
                     Defines = optionalStringArray "defines" table }
            | Error e, _ | _, Error e -> Error e

    /// Parse an OutputSpec from the [output] table.
    let private deserializeOutput (doc: TomlDocument) : Result<OutputSpec, string> =
        match Toml.getTable "output" doc with
        | None -> Error "Missing [output] section"
        | Some table ->
            match requireString "mode" "output" table, requireString "directory" "output" table with
            | Ok mode, Ok directory ->
                Ok { Mode = mode; Directory = directory }
            | Error e, _ | _, Error e -> Error e

    /// Parse a NamespaceSpec from a TomlTable (one entry in [[namespace]] array).
    let private deserializeNamespace (table: TomlTable) : Result<NamespaceSpec, string> =
        match requireString "name" "namespace" table,
              requireString "description" "namespace" table,
              requireString "library" "namespace" table with
        | Ok name, Ok description, Ok library ->
            Ok { Name = name
                 Description = description
                 Library = library
                 Prefixes = optionalStringArray "prefixes" table
                 Functions = optionalStringArray "functions" table }
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e

    /// Parse the [[namespace]] array from a document.
    let private deserializeNamespaces (doc: TomlDocument) : Result<NamespaceSpec list, string> =
        match Toml.getValue "namespace" doc with
        | None -> Ok []
        | Some (TomlValue.Array items) ->
            let results =
                items |> List.map (fun item ->
                    match item with
                    | TomlValue.Table t -> deserializeNamespace t
                    | _ -> Error "Each [[namespace]] entry must be a table")
            // Collect all errors or all successes
            let errors = results |> List.choose (function Error e -> Some e | _ -> None)
            if errors.IsEmpty then
                Ok (results |> List.choose (function Ok ns -> Some ns | _ -> None))
            else
                Error (String.concat "; " errors)
        | _ -> Error "'namespace' must be an array of tables"

    /// Helper: get an optional string field from a TomlTable.
    let private optionalString (fieldName: string) (table: TomlTable) : string option =
        match TomlTable.tryFind fieldName table with
        | Some (TomlValue.String s) -> Some s
        | _ -> None

    /// Parse an error convention string to ErrorConvention.
    /// For "enum_error_code", the additional fields are parsed from the table separately.
    let private parseConvention (s: string) (table: TomlTable) : ErrorConvention =
        match s.ToLowerInvariant() with
        | "errno" -> Errno
        | "return_code" -> ReturnCode
        | "enum_error_code" ->
            let errorType = optionalString "error_type" table |> Option.defaultValue ""
            let successValue = optionalString "success_value" table |> Option.defaultValue ""
            let errorStringFn = optionalString "error_string_fn" table
            let errorNameFn = optionalString "error_name_fn" table
            EnumErrorCode (errorType, successValue, errorStringFn, errorNameFn)
        | _ -> NoErrorConvention

    /// Deserialize the optional [error_conventions] section.
    let private deserializeErrorConventions (doc: TomlDocument) : ErrorConventionSpec option =
        match Toml.getValue "error_conventions" doc with
        | None -> None
        | Some (TomlValue.Table table) ->
            let defaultConv =
                match TomlTable.tryFind "default" table with
                | Some (TomlValue.String s) -> parseConvention s table
                | _ -> NoErrorConvention
            let overrides =
                match TomlTable.tryFind "overrides" table with
                | Some (TomlValue.Table overrideTable) ->
                    overrideTable
                    |> Map.toSeq
                    |> Seq.choose (fun (k, v) ->
                        match v with
                        | TomlValue.String s -> Some (k, parseConvention s TomlTable.empty)
                        | _ -> None)
                    |> Map.ofSeq
                | _ -> Map.empty
            Some { Default = defaultConv; Overrides = overrides }
        | _ -> None

    let private deserializeOptions (doc: TomlDocument) : ProjectOptions option =
        match Toml.getValue "options" doc with
        | None -> None
        | Some (TomlValue.Table table) ->
            let abiStructs =
                match TomlTable.tryFind "abi_critical_structs" table with
                | Some (TomlValue.Array items) ->
                    items |> List.choose (function TomlValue.String s -> Some s | _ -> None)
                | _ -> []
            let generateDescriptors =
                match TomlTable.tryFind "generate_descriptors" table with
                | Some (TomlValue.Boolean b) -> b
                | _ -> false
            Some { AbiCriticalStructs = abiStructs; GenerateDescriptors = generateDescriptors }
        | _ -> None

    /// Deserialize a TomlDocument to a PilotProject.
    let deserialize (doc: TomlDocument) : Result<PilotProject, string> =
        match deserializeLibrary doc, deserializeOutput doc, deserializeNamespaces doc with
        | Ok lib, Ok output, Ok namespaces ->
            Ok { Library = lib
                 Output = output
                 Namespaces = namespaces
                 ErrorConventions = deserializeErrorConventions doc
                 Options = deserializeOptions doc }
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e

    // =========================================================================
    // File I/O
    // =========================================================================

    /// Load a PilotProject from a TOML file path.
    let loadFromFile (path: string) : Result<PilotProject, string> =
        try
            let content = System.IO.File.ReadAllText(path)
            match Toml.parse content with
            | Ok doc -> deserialize doc
            | Error err -> Error $"TOML parse error: {err}"
        with ex ->
            Error $"Failed to read file: {ex.Message}"

    /// Save a PilotProject to a TOML file.
    let saveToFile (path: string) (project: PilotProject) : Result<unit, string> =
        try
            let content = toTomlString project
            System.IO.File.WriteAllText(path, content)
            Ok ()
        with ex ->
            Error $"Failed to write file: {ex.Message}"
