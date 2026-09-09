namespace Farscape.Core

open Fidelity.Data.TOML
open PilotTypes

/// Serialization and deserialization of PilotProject to/from TOML format.
///
/// Uses Fidelity.Data for type-safe TOML handling.
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
            if lib.XmlProtocols.IsEmpty then table
            else TomlTable.add "xml_protocols" (TomlValue.Array (lib.XmlProtocols |> List.map TomlValue.String)) table
        let table =
            if lib.IncludePaths.IsEmpty then table
            else TomlTable.add "include_paths" (TomlValue.Array (lib.IncludePaths |> List.map TomlValue.String)) table
        let table =
            if lib.Defines.IsEmpty then table
            else TomlTable.add "defines" (TomlValue.Array (lib.Defines |> List.map TomlValue.String)) table
        let table =
            if lib.MacroPrefixes.IsEmpty then table
            else TomlTable.add "macro_prefixes" (TomlValue.Array (lib.MacroPrefixes |> List.map TomlValue.String)) table
        let table =
            if lib.PkgConfig.IsEmpty then table
            else TomlTable.add "pkg_config" (TomlValue.Array (lib.PkgConfig |> List.map TomlValue.String)) table
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
        let table =
            if ns.XmlInterfaces.IsEmpty then table
            else TomlTable.add "xml_interfaces" (TomlValue.Array (ns.XmlInterfaces |> List.map TomlValue.String)) table
        TomlValue.Table table

    /// Serialize an ErrorConvention to its TOML string value.
    let private serializeConvention (c: ErrorConvention) : string =
        match c with
        | Errno -> "errno"
        | ReturnCode -> "return_code"
        | EnumErrorCode _ -> "enum_error_code"
        | NullWithReason _ -> "null_with_reason"
        | NoErrorConvention -> "none"

    /// Serialize ErrorConventionSpec to a TOML table value.
    let private serializeErrorConventions (spec: ErrorConventionSpec) : TomlValue =
        let table =
            TomlTable.empty
            |> TomlTable.add "default" (TomlValue.String (serializeConvention spec.Default))
        // For EnumErrorCode/NullWithReason, serialize additional fields at the top level
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
            | NullWithReason reasonFn ->
                table |> TomlTable.add "reason_function" (TomlValue.String reasonFn)
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
        let table =
            if opts.NativePointerSurface then
                TomlTable.add "experimental_native_pointer_surface" (TomlValue.Boolean true) table
            else table
        let table =
            if opts.CHeaderMode then TomlTable.add "c_header_mode" (TomlValue.Boolean true) table
            else table
        let bindings = opts.Bindings |> List.map (fun binding ->
            TomlTable.empty
            |> TomlTable.add "name" (TomlValue.String binding.Name)
            |> TomlTable.add "symbol" (TomlValue.String binding.Symbol)
            |> TomlTable.add "reference_parameters" (TomlValue.Array (binding.ReferenceParameters |> List.map (int64 >> TomlValue.Integer)))
            |> TomlTable.add "read_only_reference_parameters" (TomlValue.Array (binding.ReadOnlyReferenceParameters |> List.map (int64 >> TomlValue.Integer)))
            |> TomlTable.add "constant_parameters" (TomlValue.Array (binding.ConstantParameters |> List.map TomlValue.String))
            |> TomlTable.add "reference_elements" (TomlValue.Array (binding.ReferenceElements |> List.map TomlValue.String))
            |> TomlTable.add "string_parameters" (TomlValue.Array (binding.StringParameters |> List.map (int64 >> TomlValue.Integer)))
            |> TomlTable.add "nonnull_callbacks" (TomlValue.Array (binding.NonnullCallbacks |> List.map (int64 >> TomlValue.Integer)))
            |> TomlTable.add "parameter_handles" (TomlValue.Array (binding.ParameterHandles |> List.map TomlValue.String))
            |> (fun t -> match binding.ReturnHandle with Some name -> TomlTable.add "return_handle" (TomlValue.String name) t | None -> t)
            |> (fun t -> match binding.Ownership with
                         | Some transfer ->
                             let spelling = match transfer with CallerOwns -> "caller_owns" | CalleeOwns -> "callee_owns" | Borrowed -> "borrowed"
                             TomlTable.add "ownership" (TomlValue.String spelling) t
                         | None -> t)
            |> TomlValue.Table)
        let table = if bindings.IsEmpty then table else TomlTable.add "bindings" (TomlValue.Array bindings) table
        let table = if opts.DescriptorOnlyDependencies then TomlTable.add "descriptor_only_dependencies" (TomlValue.Boolean true) table else table
        let table = if opts.LinkLibraries then TomlTable.add "link_libraries" (TomlValue.Boolean true) table else table
        let table = if opts.TypedProtocol then TomlTable.add "typed_protocol" (TomlValue.Boolean true) table else table
        let table = if opts.ValueStructs.IsEmpty then table else TomlTable.add "value_structs" (TomlValue.Array (opts.ValueStructs |> List.map TomlValue.String)) table
        let mappings = opts.MappedReturns |> List.map (fun m ->
            [ "name", m.Name; "acquire", m.Acquire; "release", m.Release
              "schema", m.Schema; "element", m.Element; "access", m.Access
              "owner_parameter", m.OwnerParameter; "stride_parameter", m.StrideParameter
              "rows_parameter", m.RowsParameter; "width_parameter", m.WidthParameter
              "cookie_parameter", m.CookieParameter ]
            |> List.fold (fun t (k, v) -> TomlTable.add k (TomlValue.String v) t) TomlTable.empty
            |> TomlTable.add "alignment" (TomlValue.Integer (int64 m.Alignment))
            |> TomlTable.add "failure_status" (TomlValue.Integer (int64 m.FailureStatus))
            |> TomlValue.Table)
        let table = if mappings.IsEmpty then table else TomlTable.add "mapped_returns" (TomlValue.Array mappings) table
        TomlValue.Table table

    /// Serialize a CallbackSpec to a TOML table value.
    let private serializeCallbacks (spec: CallbackSpec) : TomlValue =
        let table = TomlTable.empty
        let table =
            if spec.Registrations.IsEmpty then table
            else
                let regs =
                    spec.Registrations |> List.map (fun reg ->
                        let t =
                            TomlTable.empty
                            |> TomlTable.add "function" (TomlValue.String reg.Function)
                            |> TomlTable.add "callback_param" (TomlValue.String reg.CallbackParam)
                        let t =
                            match reg.DataParam with
                            | Some dp -> TomlTable.add "data_param" (TomlValue.String dp) t
                            | None -> t
                        TomlValue.Table t)
                TomlTable.add "registrations" (TomlValue.Array regs) table
        let table =
            if spec.ListenerStructs.IsEmpty then table
            else
                let structs =
                    spec.ListenerStructs |> List.map (fun ls ->
                        let t =
                            TomlTable.empty
                            |> TomlTable.add "name" (TomlValue.String ls.Name)
                        let t =
                            match ls.RegistrationFunction with
                            | Some rf -> TomlTable.add "registration_function" (TomlValue.String rf) t
                            | None -> t
                        TomlValue.Table t)
                TomlTable.add "listener_structs" (TomlValue.Array structs) table
        TomlValue.Table table

    /// Serialize NonnullAnnotations to a TOML table value.
    let private serializeNonnull (spec: NonnullAnnotations) : TomlValue =
        let table = TomlTable.empty
        // Serialize per-function parameter indices
        let table =
            spec.Parameters
            |> Map.fold (fun t funcName indices ->
                TomlTable.add funcName (TomlValue.Array (indices |> List.map (fun i -> TomlValue.Integer (int64 i)))) t
            ) table
        // Serialize nonnull_returns as a string array
        let table =
            if spec.Returns.IsEmpty then table
            else TomlTable.add "nonnull_returns" (TomlValue.Array (spec.Returns |> Set.toList |> List.map TomlValue.String)) table
        TomlValue.Table table

    let serializeProtocolConfig (config: ProtocolConfig) : TomlValue =
        TomlTable.empty
        |> TomlTable.add "marshal_function" (TomlValue.String config.MarshalFunction)
        |> TomlTable.add "marshal_module" (TomlValue.String config.MarshalModule)
        |> TomlTable.add "version_function" (TomlValue.String config.VersionFunction)
        |> TomlTable.add "interface_resolution" (TomlValue.String config.InterfaceResolution)
        |> TomlTable.add "destroy_flag" (TomlValue.Integer (int64 config.DestroyFlag))
        |> TomlValue.Table

    /// Serialize a CppClassSpec to a TOML table value.
    let private serializeCppClassSpec (spec: CppClassSpec) : TomlValue =
        let table =
            TomlTable.empty
            |> TomlTable.add "name" (TomlValue.String spec.Name)
            |> TomlTable.add "namespace" (TomlValue.String spec.Namespace)
        let table =
            if not spec.Bind then TomlTable.add "bind" (TomlValue.Boolean false) table
            else table
        let table =
            match spec.KindOverride with
            | Some k -> TomlTable.add "kind" (TomlValue.String k) table
            | None -> table
        let table =
            match spec.SizeOverride with
            | Some s -> TomlTable.add "size" (TomlValue.Integer (int64 s)) table
            | None -> table
        TomlValue.Table table

    /// Serialize a CppConfig to a TOML table value.
    let private serializeCppConfig (config: CppConfig) : TomlValue =
        let table =
            TomlTable.empty
            |> TomlTable.add "symbol_library" (TomlValue.String config.SymbolLibrary)
        let table =
            if not config.PimplDetection then
                TomlTable.add "pimpl_detection" (TomlValue.Boolean false) table
            else table
        let table =
            if config.GapAnalysis then
                TomlTable.add "gap_analysis" (TomlValue.Boolean true) table
            else table
        let table =
            if config.Classes.IsEmpty then table
            else TomlTable.add "class" (TomlValue.Array (config.Classes |> List.map serializeCppClassSpec)) table
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
        let table =
            match project.Options with
            | Some opts -> table |> TomlTable.add "options" (serializeOptions opts)
            | None -> table
        let table =
            match project.Callbacks with
            | Some spec -> table |> TomlTable.add "callbacks" (serializeCallbacks spec)
            | None -> table
        let table =
            match project.Nonnull with
            | Some spec ->
                let annotationsTable =
                    match TomlTable.tryFind "annotations" table with
                    | Some (TomlValue.Table existing) -> existing
                    | _ -> TomlTable.empty
                let annotationsTable = TomlTable.add "nonnull" (serializeNonnull spec) annotationsTable
                table |> TomlTable.add "annotations" (TomlValue.Table annotationsTable)
            | None -> table
        let table =
            match project.ProtocolConfig with
            | Some config -> table |> TomlTable.add "protocol" (serializeProtocolConfig config)
            | None -> table
        let table =
            match project.CppConfig with
            | Some config -> table |> TomlTable.add "cpp" (serializeCppConfig config)
            | None -> table
        table

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
                     XmlProtocols = optionalStringArray "xml_protocols" table
                     IncludePaths = optionalStringArray "include_paths" table
                     Defines = optionalStringArray "defines" table
                     MacroPrefixes = optionalStringArray "macro_prefixes" table
                     PkgConfig = optionalStringArray "pkg_config" table }
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
                 Functions = optionalStringArray "functions" table
                 XmlInterfaces = optionalStringArray "xml_interfaces" table }
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
        | "null_with_reason" ->
            let reasonFn = optionalString "reason_function" table |> Option.defaultValue ""
            NullWithReason reasonFn
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
            let nativePointerSurface =
                match TomlTable.tryFind "experimental_native_pointer_surface" table with
                | Some (TomlValue.Boolean b) -> b
                | _ -> false
            let cHeaderMode =
                match TomlTable.tryFind "c_header_mode" table with
                | Some (TomlValue.Boolean b) -> b
                | _ -> false
            let bindings =
                match TomlTable.tryFind "bindings" table with
                | Some (TomlValue.Array items) -> items |> List.map (function
                    | TomlValue.Table binding ->
                        let name = optionalString "name" binding |> Option.defaultWith (fun () -> failwith "options.bindings requires name")
                        let indices key = match TomlTable.tryFind key binding with
                                          | Some (TomlValue.Array values) -> values |> List.map (function TomlValue.Integer i -> int i | _ -> failwith $"{key} requires integer indices")
                                          | _ -> []
                        let handles = match TomlTable.tryFind "parameter_handles" binding with
                                      | Some (TomlValue.Array values) -> values |> List.map (function TomlValue.String name -> name | _ -> failwith "parameter_handles requires type names")
                                      | _ -> []
                        { Name = name; Symbol = optionalString "symbol" binding |> Option.defaultValue name
                          ReferenceParameters = indices "reference_parameters"
                          ReadOnlyReferenceParameters = indices "read_only_reference_parameters"
                          ConstantParameters = match TomlTable.tryFind "constant_parameters" binding with Some (TomlValue.Array xs) -> xs |> List.map (function TomlValue.String x -> x | _ -> failwith "constant_parameters requires integer constants") | _ -> []
                          ReferenceElements = match TomlTable.tryFind "reference_elements" binding with Some (TomlValue.Array xs) -> xs |> List.map (function TomlValue.String x -> x | _ -> failwith "reference_elements requires C element type names") | _ -> []
                          StringParameters = indices "string_parameters"; NonnullCallbacks = indices "nonnull_callbacks"
                          ParameterHandles = handles; ReturnHandle = optionalString "return_handle" binding
                          Ownership = optionalString "ownership" binding |> Option.map (function
                              | "caller_owns" -> CallerOwns | "callee_owns" -> CalleeOwns | "borrowed" -> Borrowed
                              | other -> failwith $"Unknown binding ownership '{other}'") }
                    | _ -> failwith "options.bindings requires tables")
                | _ -> []
            let descriptorOnly = TomlTable.tryFind "descriptor_only_dependencies" table = Some (TomlValue.Boolean true)
            let mappedReturns =
                match TomlTable.tryFind "mapped_returns" table with
                | Some (TomlValue.Array items) -> items |> List.map (function
                    | TomlValue.Table mapping ->
                        let text key = optionalString key mapping |> Option.defaultWith (fun () -> failwith $"options.mapped_returns requires {key}")
                        let number key = match TomlTable.tryFind key mapping with Some (TomlValue.Integer value) -> int value | _ -> failwith $"options.mapped_returns requires integer {key}"
                        { Name = text "name"; Acquire = text "acquire"; Release = text "release"
                          Schema = text "schema"; Element = text "element"; Alignment = number "alignment"; Access = text "access"
                          OwnerParameter = text "owner_parameter"; StrideParameter = text "stride_parameter"
                          RowsParameter = text "rows_parameter"; WidthParameter = text "width_parameter"
                          CookieParameter = text "cookie_parameter"; FailureStatus = number "failure_status" }
                    | _ -> failwith "options.mapped_returns requires tables")
                | _ -> []
            Some { AbiCriticalStructs = abiStructs; GenerateDescriptors = generateDescriptors
                   NativePointerSurface = nativePointerSurface; CHeaderMode = cHeaderMode
                   Bindings = bindings; DescriptorOnlyDependencies = descriptorOnly
                   LinkLibraries = TomlTable.tryFind "link_libraries" table = Some (TomlValue.Boolean true)
                   TypedProtocol = TomlTable.tryFind "typed_protocol" table = Some (TomlValue.Boolean true)
                   MappedReturns = mappedReturns
                   ValueStructs = match TomlTable.tryFind "value_structs" table with Some (TomlValue.Array values) -> values |> List.map (function TomlValue.String name -> name | _ -> failwith "value_structs requires type names") | _ -> [] }
        | _ -> None

    /// Deserialize the optional [callbacks] section.
    let private deserializeCallbacks (doc: TomlDocument) : CallbackSpec option =
        match Toml.getValue "callbacks" doc with
        | None -> None
        | Some (TomlValue.Table table) ->
            let registrations =
                match TomlTable.tryFind "registrations" table with
                | Some (TomlValue.Array items) ->
                    items |> List.choose (fun item ->
                        match item with
                        | TomlValue.Table t ->
                            match TomlTable.tryFind "function" t, TomlTable.tryFind "callback_param" t with
                            | Some (TomlValue.String fn), Some (TomlValue.String cb) ->
                                let dp = optionalString "data_param" t
                                Some { Function = fn; CallbackParam = cb; DataParam = dp }
                            | _ -> None
                        | _ -> None)
                | _ -> []
            let listenerStructs =
                match TomlTable.tryFind "listener_structs" table with
                | Some (TomlValue.Array items) ->
                    items |> List.choose (fun item ->
                        match item with
                        | TomlValue.Table t ->
                            match TomlTable.tryFind "name" t with
                            | Some (TomlValue.String name) ->
                                let regFn = optionalString "registration_function" t
                                Some { Name = name; RegistrationFunction = regFn }
                            | _ -> None
                        | _ -> None)
                | _ -> []
            Some { Registrations = registrations; ListenerStructs = listenerStructs }
        | _ -> None

    /// Deserialize the optional [annotations.nonnull] section.
    let private deserializeNonnull (doc: TomlDocument) : NonnullAnnotations option =
        match Toml.getValue "annotations.nonnull" doc with
        | None ->
            // Try nested table access: [annotations] → nonnull
            match Toml.getTable "annotations" doc with
            | None -> None
            | Some annotationsTable ->
                match TomlTable.tryFind "nonnull" annotationsTable with
                | Some (TomlValue.Table table) ->
                    let parameters =
                        table
                        |> Map.toSeq
                        |> Seq.choose (fun (k, v) ->
                            if k = "nonnull_returns" then None
                            else
                                match v with
                                | TomlValue.Array items ->
                                    let indices = items |> List.choose (function
                                        | TomlValue.Integer i -> Some (int i)
                                        | _ -> None)
                                    Some (k, indices)
                                | _ -> None)
                        |> Map.ofSeq
                    let returns =
                        match TomlTable.tryFind "nonnull_returns" table with
                        | Some (TomlValue.Array items) ->
                            items |> List.choose (function TomlValue.String s -> Some s | _ -> None) |> Set.ofList
                        | _ -> Set.empty
                    Some { Parameters = parameters; Returns = returns }
                | _ -> None
        | Some (TomlValue.Table table) ->
            let parameters =
                table
                |> Map.toSeq
                |> Seq.choose (fun (k, v) ->
                    if k = "nonnull_returns" then None
                    else
                        match v with
                        | TomlValue.Array items ->
                            let indices = items |> List.choose (function
                                | TomlValue.Integer i -> Some (int i)
                                | _ -> None)
                            Some (k, indices)
                        | _ -> None)
                |> Map.ofSeq
            let returns =
                match TomlTable.tryFind "nonnull_returns" table with
                | Some (TomlValue.Array items) ->
                    items |> List.choose (function TomlValue.String s -> Some s | _ -> None) |> Set.ofList
                | _ -> Set.empty
            Some { Parameters = parameters; Returns = returns }
        | _ -> None

    let deserializeProtocolConfig (doc: TomlDocument) : ProtocolConfig option =
        match Toml.getTable "protocol" doc with
        | None -> None
        | Some table ->
            match TomlTable.tryFind "marshal_function" table,
                  TomlTable.tryFind "marshal_module" table,
                  TomlTable.tryFind "version_function" table with
            | Some (TomlValue.String marshalFn),
              Some (TomlValue.String marshalMod),
              Some (TomlValue.String versionFn) ->
                let interfaceRes =
                    match TomlTable.tryFind "interface_resolution" table with
                    | Some (TomlValue.String s) -> s
                    | _ -> "dlsym"
                let destroyFlag =
                    match TomlTable.tryFind "destroy_flag" table with
                    | Some (TomlValue.Integer i) -> uint32 i
                    | _ -> 1u
                Some { MarshalFunction = marshalFn
                       MarshalModule = marshalMod
                       VersionFunction = versionFn
                       InterfaceResolution = interfaceRes
                       DestroyFlag = destroyFlag }
            | _ -> None

    /// Deserialize the optional [cpp] section for C++ class binding configuration.
    let private deserializeCppConfig (doc: TomlDocument) : CppConfig option =
        match Toml.getTable "cpp" doc with
        | None -> None
        | Some table ->
            let symbolLibrary =
                match TomlTable.tryFind "symbol_library" table with
                | Some (TomlValue.String s) -> s
                | _ -> ""
            let pimplDetection =
                match TomlTable.tryFind "pimpl_detection" table with
                | Some (TomlValue.Boolean b) -> b
                | _ -> true
            let gapAnalysis =
                match TomlTable.tryFind "gap_analysis" table with
                | Some (TomlValue.Boolean b) -> b
                | _ -> false
            let classes =
                match TomlTable.tryFind "class" table with
                | Some (TomlValue.Array items) ->
                    items |> List.choose (fun item ->
                        match item with
                        | TomlValue.Table t ->
                            match TomlTable.tryFind "name" t with
                            | Some (TomlValue.String name) ->
                                let ns =
                                    match TomlTable.tryFind "namespace" t with
                                    | Some (TomlValue.String s) -> s
                                    | _ -> ""
                                let bind =
                                    match TomlTable.tryFind "bind" t with
                                    | Some (TomlValue.Boolean b) -> b
                                    | _ -> true
                                let kindOverride = optionalString "kind" t
                                let sizeOverride =
                                    match TomlTable.tryFind "size" t with
                                    | Some (TomlValue.Integer i) -> Some (int i)
                                    | _ -> None
                                Some { CppClassSpec.Name = name
                                       Namespace = ns
                                       Bind = bind
                                       KindOverride = kindOverride
                                       SizeOverride = sizeOverride }
                            | _ -> None
                        | _ -> None)
                | _ -> []
            Some { SymbolLibrary = symbolLibrary
                   PimplDetection = pimplDetection
                   GapAnalysis = gapAnalysis
                   Classes = classes }

    /// Deserialize a TomlDocument to a PilotProject.
    let private deserializeCore (doc: TomlDocument) : Result<PilotProject, string> =
        match deserializeLibrary doc, deserializeOutput doc, deserializeNamespaces doc with
        | Ok lib, Ok output, Ok namespaces ->
            Ok { Library = lib
                 Output = output
                 Namespaces = namespaces
                 ErrorConventions = deserializeErrorConventions doc
                 Options = deserializeOptions doc
                 Callbacks = deserializeCallbacks doc
                 Nonnull = deserializeNonnull doc
                 ProtocolConfig = deserializeProtocolConfig doc
                 Layer3 = None
                 CppConfig = deserializeCppConfig doc }
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error e

    /// Invalid explicit projection annotations are reported as project errors.
    let deserialize (doc: TomlDocument) : Result<PilotProject, string> =
        try deserializeCore doc
        with ex -> Error ex.Message

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
