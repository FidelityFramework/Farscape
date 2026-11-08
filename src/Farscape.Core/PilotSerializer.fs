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
            if lib.Introspection.IsEmpty then table
            else TomlTable.add "introspection" (TomlValue.Array (lib.Introspection |> List.map TomlValue.String)) table
        let table =
            if lib.IncludePaths.IsEmpty then table
            else TomlTable.add "include_paths" (TomlValue.Array (lib.IncludePaths |> List.map TomlValue.String)) table
        let table =
            if lib.SystemIncludePaths.IsEmpty then table
            else TomlTable.add "system_include_paths" (TomlValue.Array (lib.SystemIncludePaths |> List.map TomlValue.String)) table
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
        let table =
            if ns.Signals.IsEmpty then table
            else TomlTable.add "signals" (TomlValue.Array (ns.Signals |> List.map TomlValue.String)) table
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

    let private serializeResultRange (range: BindingResultRange) =
        let endpoint = function
            | ResultRangeBound.Constant value -> TomlValue.InlineTable (Map.ofList ["constant", TomlValue.Integer (int64 value)])
            | ResultRangeBound.Argument index -> TomlValue.InlineTable (Map.ofList ["argument", TomlValue.Integer (int64 index)])
        TomlValue.InlineTable (Map.ofList [
            "identity", TomlValue.String range.Identity; "version", TomlValue.String range.Version
            "source", TomlValue.String range.Source; "lower", endpoint range.Lower; "upper", endpoint range.Upper ])

    let private serializeCallbackInvocation (contract: BindingCallbackInvocation) =
        TomlValue.InlineTable (Map.ofList [
            "parameter", TomlValue.Integer (int64 contract.Parameter)
            "identity", TomlValue.String contract.Identity
            "version", TomlValue.String contract.Version
            "source", TomlValue.String contract.Source
            "scope", TomlValue.String contract.Scope
            yield! contract.ExitBinding |> Option.map (fun value -> "exit_binding", TomlValue.String value) |> Option.toList
            yield! contract.SuccessStatus |> Option.map (fun value -> "success_status", TomlValue.Integer (int64 value)) |> Option.toList
            yield! contract.AbsenceHypothesis |> Option.map (fun hypothesis ->
                "absence_hypothesis", TomlValue.InlineTable (Map.ofList [
                    "identity", TomlValue.String hypothesis.Identity
                    "version", TomlValue.String hypothesis.Version
                    "source", TomlValue.String hypothesis.Source ])) |> Option.toList ])

    let private serializeResource (resource: BindingResource) =
        TomlValue.InlineTable (Map.ofList [
            "kind", TomlValue.String "owned_nullable_return"
            "release_binding", TomlValue.String resource.ReleaseBinding
            "release_parameter", TomlValue.Integer (int64 resource.ReleaseParameter)
            "hypothesis", TomlValue.InlineTable (Map.ofList [
                "identity", TomlValue.String resource.Identity
                "version", TomlValue.String resource.Version
                "source", TomlValue.String resource.Source ]) ])

    let private serializeWritableBuffer (contract: BindingWritableBuffer) =
        TomlValue.InlineTable (Map.ofList [
            "parameter", TomlValue.Integer (int64 contract.Parameter)
            "count_parameter", TomlValue.Integer (int64 contract.CountParameter)
            "hypothesis", TomlValue.InlineTable (Map.ofList [
                "identity", TomlValue.String contract.Identity
                "version", TomlValue.String contract.Version
                "source", TomlValue.String contract.Source ]) ])

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
            |> (fun t -> match binding.ResultRange with Some range -> TomlTable.add "result_range" (serializeResultRange range) t | None -> t)
            |> (fun t -> match binding.Resource with Some resource -> TomlTable.add "resource" (serializeResource resource) t | None -> t)
            |> (fun t -> if binding.WritableBuffers.IsEmpty then t else TomlTable.add "writable_buffers" (TomlValue.Array (binding.WritableBuffers |> List.map serializeWritableBuffer)) t)
            |> (fun t -> if binding.CallbackInvocations.IsEmpty then t else TomlTable.add "callback_invocations" (TomlValue.Array (binding.CallbackInvocations |> List.map serializeCallbackInvocation)) t)
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
        let flexibleArrays = opts.FlexibleArrays |> List.map (fun f ->
            TomlTable.empty
            |> TomlTable.add "record" (TomlValue.String f.Record)
            |> TomlTable.add "field" (TomlValue.String f.Field)
            |> TomlTable.add "count_field" (TomlValue.String f.CountField)
            |> TomlTable.add "count_rule" (TomlValue.String (match f.Rule with ElementCount -> "element_count" | RecordBytes -> "record_bytes"))
            |> TomlTable.add "source" (TomlValue.String f.Source)
            |> TomlValue.Table)
        let table = if flexibleArrays.IsEmpty then table else TomlTable.add "flexible_arrays" (TomlValue.Array flexibleArrays) table
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
    // Deserialization: TomlDocument → Result<PilotProject, Diagnostics.Refusal>
    // =========================================================================

    /// Raise a pilot TOML-shape refusal (#199): its code, the pilot key whose value causes it,
    /// and its message. A pilot's shape is no header declaration, so its source is unknown;
    /// `loadFromFileDiagnosed` binds the pilot file.
    let private refuse (code: string) (key: string) (message: string) : 'T =
        Diagnostics.diagnostic code message (Diagnostics.Located.Unknown "the refusal is in pilot TOML input, not a header declaration")
        |> Diagnostics.withPilotKey key None
        |> Diagnostics.refuse

    /// The table of a TOML value, or a refusal that the key needs a table.
    let private tableAt (key: string) (message: string) (value: TomlValue) : TomlTable =
        match value with
        | TomlValue.Table t | TomlValue.InlineTable t -> t
        | _ -> refuse Diagnostics.PilotValueType key message

    /// Refuse a key of `table` outside `allowed`.
    let private onlyKeys (key: string) (message: string) (allowed: string list) (table: TomlTable) =
        match table |> Map.toList |> List.tryFind (fun (name, _) -> not (List.contains name allowed)) with
        | Some (name, _) -> refuse Diagnostics.PilotKeyUnknown $"{key}.{name}" message
        | None -> ()

    /// A required nonempty string: absent is FS9198, another TOML type FS9199, empty FS9201.
    let private nonemptyText (key: string) (message: string) (name: string) (table: TomlTable) : string =
        match TomlTable.tryFind name table with
        | Some (TomlValue.String value) when not (System.String.IsNullOrWhiteSpace value) -> value
        | Some (TomlValue.String _) -> refuse Diagnostics.PilotValueUnadmitted $"{key}.{name}" message
        | Some _ -> refuse Diagnostics.PilotValueType $"{key}.{name}" message
        | None -> refuse Diagnostics.PilotKeyMissing $"{key}.{name}" message

    /// A hypothesis field (identity, version, source): an empty one is FS9134, the code the
    /// generator gives the same reason.
    let private hypothesisText (key: string) (message: string) (name: string) (table: TomlTable) : string =
        match TomlTable.tryFind name table with
        | Some (TomlValue.String value) when not (System.String.IsNullOrWhiteSpace value) -> value
        | Some (TomlValue.String _) -> refuse Diagnostics.HypothesisFieldEmpty $"{key}.{name}" message
        | Some _ -> refuse Diagnostics.PilotValueType $"{key}.{name}" message
        | None -> refuse Diagnostics.PilotKeyMissing $"{key}.{name}" message

    /// A nonnegative int index: absent FS9198, no integer FS9199, outside 0..Int32.MaxValue FS9201.
    let private indexAt (key: string) (message: string) (name: string) (table: TomlTable) : int =
        match TomlTable.tryFind name table with
        | Some (TomlValue.Integer value) when value >= 0L && value <= int64 System.Int32.MaxValue -> int value
        | Some (TomlValue.Integer _) -> refuse Diagnostics.PilotValueUnadmitted $"{key}.{name}" message
        | Some _ -> refuse Diagnostics.PilotValueType $"{key}.{name}" message
        | None -> refuse Diagnostics.PilotKeyMissing $"{key}.{name}" message

    /// Helper: require a string field from a TomlTable.
    let private requireString (fieldName: string) (sectionName: string) (table: TomlTable) : string =
        match TomlTable.tryFind fieldName table with
        | Some (TomlValue.String s) -> s
        | Some _ -> refuse Diagnostics.PilotValueType $"{sectionName}.{fieldName}" $"[{sectionName}].{fieldName} must be a string"
        | None -> refuse Diagnostics.PilotKeyMissing $"{sectionName}.{fieldName}" $"[{sectionName}].{fieldName} is required"

    /// Helper: get an optional string list from a TomlTable.
    let private optionalStringArray (fieldName: string) (table: TomlTable) : string list =
        match TomlTable.tryFind fieldName table with
        | Some (TomlValue.Array arr) ->
            arr |> List.choose (function TomlValue.String s -> Some s | _ -> None)
        | _ -> []

    /// Parse a LibrarySpec from the [library] table.
    /// Accepts either `headers = [...]` (new) or `header = "..."` (backward compat).
    let private deserializeLibrary (doc: TomlDocument) : LibrarySpec =
        match Toml.getTable "library" doc with
        | None -> refuse Diagnostics.PilotKeyMissing "library" "Missing [library] section"
        | Some table ->
            let name = requireString "name" "library" table
            let headers =
                match TomlTable.tryFind "headers" table with
                | Some (TomlValue.Array arr) ->
                    let headers = arr |> List.choose (function TomlValue.String s -> Some s | _ -> None)
                    if headers.IsEmpty then refuse Diagnostics.PilotValueUnadmitted "library.headers" "[library].headers must not be empty"
                    else headers
                | Some _ -> refuse Diagnostics.PilotValueType "library.headers" "[library].headers must be an array of strings"
                | None ->
                    match TomlTable.tryFind "header" table with
                    | Some (TomlValue.String h) -> [h]
                    | Some _ -> refuse Diagnostics.PilotValueType "library.header" "[library].header must be a string"
                    | None -> refuse Diagnostics.PilotKeyMissing "library.headers" "[library] requires either 'header' or 'headers'"
            { Name = name
              Headers = headers
              XmlProtocols = optionalStringArray "xml_protocols" table
              Introspection = optionalStringArray "introspection" table
              IncludePaths = optionalStringArray "include_paths" table
              SystemIncludePaths = optionalStringArray "system_include_paths" table
              Defines = optionalStringArray "defines" table
              MacroPrefixes = optionalStringArray "macro_prefixes" table
              PkgConfig = optionalStringArray "pkg_config" table }

    /// Parse an OutputSpec from the [output] table.
    let private deserializeOutput (doc: TomlDocument) : OutputSpec =
        match Toml.getTable "output" doc with
        | None -> refuse Diagnostics.PilotKeyMissing "output" "Missing [output] section"
        | Some table ->
            let mode = requireString "mode" "output" table
            { Mode = mode; Directory = requireString "directory" "output" table }

    /// Parse a NamespaceSpec from a TomlTable (one entry in [[namespace]] array).
    let private deserializeNamespace (table: TomlTable) : NamespaceSpec =
        let name = requireString "name" "namespace" table
        let description = requireString "description" "namespace" table
        { Name = name
          Description = description
          Library = requireString "library" "namespace" table
          Prefixes = optionalStringArray "prefixes" table
          Functions = optionalStringArray "functions" table
          XmlInterfaces = optionalStringArray "xml_interfaces" table
          Signals = optionalStringArray "signals" table }

    /// Parse the [[namespace]] array from a document. Every malformed entry is reported.
    let private deserializeNamespaces (doc: TomlDocument) : NamespaceSpec list =
        match Toml.getValue "namespace" doc with
        | None -> []
        | Some (TomlValue.Array items) ->
            let results =
                items |> List.map (fun item ->
                    try
                        match item with
                        | TomlValue.Table t -> Ok (deserializeNamespace t)
                        | _ -> refuse Diagnostics.PilotValueType "namespace" "Each [[namespace]] entry must be a table"
                    with :? Diagnostics.RefusalException as refused -> Error refused.Refusal)
            match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] -> results |> List.choose (function Ok ns -> Some ns | Error _ -> None)
            | errors -> Diagnostics.raiseRefusal (Diagnostics.joined "; " errors)
        | _ -> refuse Diagnostics.PilotValueType "namespace" "'namespace' must be an array of tables"

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

    let private deserializeResultRange (bindingKey: string) (binding: TomlTable) =
        let key = bindingKey + ".result_range"
        match TomlTable.tryFind "result_range" binding with
        | None -> None
        | Some value ->
            let range = tableAt key "result_range contract requires a table" value
            let text name = hypothesisText key $"result_range requires nonempty {name}" name range
            let endpoint name =
                let at = $"{key}.{name}"
                let fields =
                    TomlTable.tryFind name range
                    |> Option.map (tableAt at $"result_range {name} requires a table")
                    |> Option.defaultWith (fun () -> refuse Diagnostics.PilotKeyMissing at $"result_range requires {name}")
                match Map.toList fields with
                | [arm, TomlValue.Integer value] when value >= int64 System.Int32.MinValue && value <= int64 System.Int32.MaxValue ->
                    match arm with
                    | "constant" -> ResultRangeBound.Constant (int value)
                    | "argument" when value >= 0L -> ResultRangeBound.Argument (int value)
                    | "argument" -> refuse Diagnostics.PilotValueUnadmitted $"{at}.argument" $"result_range {name} argument requires a nonnegative index"
                    | _ -> refuse Diagnostics.PilotKeyUnknown $"{at}.{arm}" $"result_range {name} requires exactly one constant or argument integer"
                | [arm, TomlValue.Integer _] -> refuse Diagnostics.PilotValueUnadmitted $"{at}.{arm}" $"result_range {name} requires exactly one constant or argument integer within int range"
                | [arm, _] -> refuse Diagnostics.PilotValueType $"{at}.{arm}" $"result_range {name} requires exactly one constant or argument integer within int range"
                | _ -> refuse Diagnostics.PilotValueUnadmitted at $"result_range {name} requires exactly one constant or argument integer within int range"
            Some { Identity = text "identity"; Version = text "version"; Source = text "source"; Lower = endpoint "lower"; Upper = endpoint "upper" }

    let private deserializeResource (bindingKey: string) binding =
        let key = bindingKey + ".resource"
        match TomlTable.tryFind "resource" binding with
        | None -> None
        | Some value ->
            let resource = tableAt key "resource contract requires a table" value
            onlyKeys key "resource contract has unknown fields" ["kind"; "release_binding"; "release_parameter"; "hypothesis"] resource
            if nonemptyText key "resource requires nonempty kind" "kind" resource <> "owned_nullable_return" then
                refuse Diagnostics.PilotValueUnadmitted $"{key}.kind" "resource kind must be owned_nullable_return"
            let release = nonemptyText key "resource requires nonempty release_binding" "release_binding" resource
            if not (release.Contains '.') || release.Trim() <> release then
                refuse Diagnostics.PilotValueUnadmitted $"{key}.release_binding" "resource requires a qualified release_binding"
            let parameter =
                match TomlTable.tryFind "release_parameter" resource with
                | Some (TomlValue.Integer 0L) -> 0
                | _ -> refuse Diagnostics.ResourceReturnShape $"{key}.release_parameter" "resource owned_nullable_return requires release_parameter 0"
            let hypothesisKey = key + ".hypothesis"
            let hypothesis =
                TomlTable.tryFind "hypothesis" resource
                |> Option.map (tableAt hypothesisKey "resource hypothesis requires a table")
                |> Option.defaultWith (fun () -> refuse Diagnostics.PilotKeyMissing hypothesisKey "resource requires hypothesis")
            onlyKeys hypothesisKey "resource hypothesis has unknown fields" ["identity"; "version"; "source"] hypothesis
            let text name = hypothesisText hypothesisKey $"resource requires nonempty {name}" name hypothesis
            Some { ReleaseBinding = release; ReleaseParameter = parameter
                   Identity = text "identity"; Version = text "version"; Source = text "source" }

    let private deserializeWritableBuffers (bindingKey: string) binding =
        let key = bindingKey + ".writable_buffers"
        match TomlTable.tryFind "writable_buffers" binding with
        | None -> []
        | Some (TomlValue.Array values) ->
            let contracts = values |> List.map (fun value ->
                let contract = tableAt key "writable_buffers contract requires a table" value
                onlyKeys key "writable_buffers contract has unknown fields" ["parameter"; "count_parameter"; "hypothesis"] contract
                let index name = indexAt key $"writable_buffers requires nonnegative {name} within int range" name contract
                let parameter, count = index "parameter", index "count_parameter"
                if parameter = count then
                    refuse Diagnostics.WritableBufferSameParameter $"{key}.count_parameter" "writable_buffers requires distinct buffer and count parameters"
                let hypothesisKey = key + ".hypothesis"
                let hypothesis =
                    TomlTable.tryFind "hypothesis" contract
                    |> Option.map (tableAt hypothesisKey "writable_buffers hypothesis requires a table")
                    |> Option.defaultWith (fun () -> refuse Diagnostics.PilotKeyMissing hypothesisKey "writable_buffers requires hypothesis")
                onlyKeys hypothesisKey "writable_buffers hypothesis has unknown fields" ["identity"; "version"; "source"] hypothesis
                let text name = hypothesisText hypothesisKey $"writable_buffers requires nonempty {name}" name hypothesis
                { Parameter = parameter; CountParameter = count
                  Identity = text "identity"; Version = text "version"; Source = text "source" })
            if (contracts |> List.map _.Parameter |> Set.ofList).Count <> contracts.Length then
                refuse Diagnostics.ContractDuplicateParameter key "writable_buffers has duplicate parameter indices"
            contracts
        | Some _ -> refuse Diagnostics.PilotValueType key "writable_buffers requires an array"

    let private deserializeCallbackInvocations (bindingKey: string) binding =
        let key = bindingKey + ".callback_invocations"
        match TomlTable.tryFind "callback_invocations" binding with
        | None -> []
        | Some (TomlValue.Array values) ->
            let contracts = values |> List.map (fun value ->
                let fields = tableAt key "callback_invocations requires tables" value
                let text name = hypothesisText key $"callback_invocations requires nonempty {name}" name fields
                let index = indexAt key "callback_invocations requires a nonnegative parameter index within int range" "parameter" fields
                onlyKeys key "callback_invocations has unknown fields"
                    ["parameter"; "identity"; "version"; "source"; "scope"; "exit_binding"; "success_status"; "absence_hypothesis"] fields
                let scope =
                    match TomlTable.tryFind "scope" fields with
                    | Some (TomlValue.String scope) -> scope
                    | Some _ -> refuse Diagnostics.PilotValueType $"{key}.scope" "callback_invocations scope must be during_call or process_exit"
                    | None -> refuse Diagnostics.PilotKeyMissing $"{key}.scope" "callback_invocations requires nonempty scope"
                let exitBinding, successStatus =
                    match scope with
                    | "during_call" ->
                        if TomlTable.tryFind "exit_binding" fields |> Option.isSome
                           || TomlTable.tryFind "success_status" fields |> Option.isSome then
                            refuse Diagnostics.CallbackScopeFieldConflict key "callback_invocations during_call forbids exit_binding and success_status"
                        None, None
                    | "process_exit" ->
                        let exitBinding =
                            TomlTable.tryFind "exit_binding" fields
                            |> Option.map (fun _ -> nonemptyText key "callback_invocations requires nonempty exit_binding" "exit_binding" fields)
                        let successStatus =
                            match TomlTable.tryFind "success_status" fields with
                            | Some (TomlValue.Integer value) when value >= int64 System.Int32.MinValue && value <= int64 System.Int32.MaxValue -> int value
                            | Some (TomlValue.Integer _) -> refuse Diagnostics.PilotValueUnadmitted $"{key}.success_status" "callback_invocations process_exit requires signed32 success_status"
                            | Some _ -> refuse Diagnostics.PilotValueType $"{key}.success_status" "callback_invocations process_exit requires signed32 success_status"
                            | None -> refuse Diagnostics.CallbackScopeFieldConflict $"{key}.success_status" "callback_invocations process_exit requires signed32 success_status"
                        exitBinding, Some successStatus
                    | _ -> refuse Diagnostics.CallbackScopeUnknown $"{key}.scope" "callback_invocations scope must be during_call or process_exit"
                let absenceKey = key + ".absence_hypothesis"
                let absenceHypothesis =
                    TomlTable.tryFind "absence_hypothesis" fields
                    |> Option.map (fun value ->
                        let hypothesis = tableAt absenceKey "callback_invocations absence_hypothesis requires a table" value
                        onlyKeys absenceKey "callback_invocations absence_hypothesis has unknown fields" ["identity"; "version"; "source"] hypothesis
                        let required name = hypothesisText absenceKey $"callback_invocations absence_hypothesis requires nonempty {name}" name hypothesis
                        { Identity = required "identity"; Version = required "version"; Source = required "source" })
                { Parameter = index; Identity = text "identity"; Version = text "version"; Source = text "source"; Scope = scope
                  ExitBinding = exitBinding; SuccessStatus = successStatus; AbsenceHypothesis = absenceHypothesis })
            if (contracts |> List.map _.Parameter |> Set.ofList).Count <> contracts.Length then
                refuse Diagnostics.ContractDuplicateParameter key "callback_invocations has duplicate parameter indices"
            contracts
        | Some _ -> refuse Diagnostics.PilotValueType key "callback_invocations requires an array"

    let private deserializeOptions (doc: TomlDocument) : ProjectOptions option =
        match Toml.getValue "options" doc with
        | None -> None
        | Some (TomlValue.Table table) ->
            // Each flexible array member's declared count source (BAREWire #11): every key
            // required and nonempty, no unknown key, one entry per record.
            let flexibleArrays =
                let key = "options.flexible_arrays"
                match TomlTable.tryFind "flexible_arrays" table with
                | None -> []
                | Some (TomlValue.Array items) ->
                    let entries = items |> List.map (fun item ->
                        let entry = tableAt key "options.flexible_arrays requires tables" item
                        match entry |> Map.toList |> List.tryFind (fun (name, _) -> not (List.contains name ["record"; "field"; "count_field"; "count_rule"; "source"])) with
                        | Some (name, _) -> refuse Diagnostics.PilotKeyUnknown $"{key}.{name}" $"options.flexible_arrays has unknown field '{name}'"
                        | None -> ()
                        let text name = nonemptyText key $"options.flexible_arrays requires nonempty {name}" name entry
                        let rule =
                            match text "count_rule" with
                            | "element_count" -> ElementCount
                            | "record_bytes" -> RecordBytes
                            | other -> refuse Diagnostics.PilotValueUnadmitted $"{key}.count_rule" $"options.flexible_arrays count_rule '{other}' is neither element_count nor record_bytes"
                        { Record = text "record"; Field = text "field"; CountField = text "count_field"; Rule = rule; Source = text "source" })
                    match entries |> List.countBy _.Record |> List.tryFind (fun (_, n) -> n > 1) with
                    | Some (record, _) -> refuse Diagnostics.PilotEntryRepeated $"{key}.record" $"options.flexible_arrays declares record '{record}' more than once; a struct has at most one flexible array member"
                    | None -> entries
                | Some _ -> refuse Diagnostics.PilotValueType key "options.flexible_arrays requires an array of tables"
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
                        let name =
                            match TomlTable.tryFind "name" binding with
                            | Some (TomlValue.String name) -> name
                            | Some _ -> refuse Diagnostics.PilotValueType "options.bindings.name" "options.bindings requires name"
                            | None -> refuse Diagnostics.PilotKeyMissing "options.bindings.name" "options.bindings requires name"
                        let bindingKey = $"options.bindings[{name}]"
                        let indices key = match TomlTable.tryFind key binding with
                                          | Some (TomlValue.Array values) -> values |> List.map (function TomlValue.Integer i -> int i | _ -> refuse Diagnostics.PilotValueType $"{bindingKey}.{key}" $"{key} requires integer indices")
                                          | _ -> []
                        let strings key message =
                            match TomlTable.tryFind key binding with
                            | Some (TomlValue.Array values) -> values |> List.map (function TomlValue.String text -> text | _ -> refuse Diagnostics.PilotValueType $"{bindingKey}.{key}" message)
                            | _ -> []
                        { Name = name; Symbol = optionalString "symbol" binding |> Option.defaultValue name
                          ReferenceParameters = indices "reference_parameters"
                          ReadOnlyReferenceParameters = indices "read_only_reference_parameters"
                          ConstantParameters = strings "constant_parameters" "constant_parameters requires strings: an integer literal or a function-pointer sentinel macro name, or \"\" for an unfixed parameter"
                          ReferenceElements = strings "reference_elements" "reference_elements requires C element type names"
                          StringParameters = indices "string_parameters"; NonnullCallbacks = indices "nonnull_callbacks"
                          ParameterHandles = strings "parameter_handles" "parameter_handles requires type names"
                          ReturnHandle = optionalString "return_handle" binding
                          ResultRange = deserializeResultRange bindingKey binding
                          Resource = deserializeResource bindingKey binding
                          WritableBuffers = deserializeWritableBuffers bindingKey binding
                          CallbackInvocations = deserializeCallbackInvocations bindingKey binding
                          Ownership = optionalString "ownership" binding |> Option.map (function
                              | "caller_owns" -> CallerOwns | "callee_owns" -> CalleeOwns | "borrowed" -> Borrowed
                              | other -> refuse Diagnostics.PilotValueUnadmitted $"{bindingKey}.ownership" $"Unknown binding ownership '{other}'") }
                    | _ -> refuse Diagnostics.PilotValueType "options.bindings" "options.bindings requires tables")
                | _ -> []
            let descriptorOnly = TomlTable.tryFind "descriptor_only_dependencies" table = Some (TomlValue.Boolean true)
            let mappedReturns =
                match TomlTable.tryFind "mapped_returns" table with
                | Some (TomlValue.Array items) -> items |> List.map (function
                    | TomlValue.Table mapping ->
                        let key = "options.mapped_returns"
                        let text name =
                            match TomlTable.tryFind name mapping with
                            | Some (TomlValue.String value) -> value
                            | Some _ -> refuse Diagnostics.PilotValueType $"{key}.{name}" $"options.mapped_returns requires {name}"
                            | None -> refuse Diagnostics.PilotKeyMissing $"{key}.{name}" $"options.mapped_returns requires {name}"
                        let number name =
                            match TomlTable.tryFind name mapping with
                            | Some (TomlValue.Integer value) -> int value
                            | Some _ -> refuse Diagnostics.PilotValueType $"{key}.{name}" $"options.mapped_returns requires integer {name}"
                            | None -> refuse Diagnostics.PilotKeyMissing $"{key}.{name}" $"options.mapped_returns requires integer {name}"
                        { Name = text "name"; Acquire = text "acquire"; Release = text "release"
                          Schema = text "schema"; Element = text "element"; Alignment = number "alignment"; Access = text "access"
                          OwnerParameter = text "owner_parameter"; StrideParameter = text "stride_parameter"
                          RowsParameter = text "rows_parameter"; WidthParameter = text "width_parameter"
                          CookieParameter = text "cookie_parameter"; FailureStatus = number "failure_status" }
                    | _ -> refuse Diagnostics.PilotValueType "options.mapped_returns" "options.mapped_returns requires tables")
                | _ -> []
            Some { AbiCriticalStructs = abiStructs; GenerateDescriptors = generateDescriptors
                   NativePointerSurface = nativePointerSurface; CHeaderMode = cHeaderMode
                   Bindings = bindings; DescriptorOnlyDependencies = descriptorOnly
                   LinkLibraries = TomlTable.tryFind "link_libraries" table = Some (TomlValue.Boolean true)
                   TypedProtocol = TomlTable.tryFind "typed_protocol" table = Some (TomlValue.Boolean true)
                   MappedReturns = mappedReturns
                   FlexibleArrays = flexibleArrays
                   ValueStructs =
                       match TomlTable.tryFind "value_structs" table with
                       | Some (TomlValue.Array values) -> values |> List.map (function TomlValue.String name -> name | _ -> refuse Diagnostics.PilotValueType "options.value_structs" "value_structs requires type names")
                       | _ -> [] }
        | _ -> None

    /// Deserialize the optional [callbacks] section, including the documented inline tables.
    let private deserializeCallbacks (doc: TomlDocument) : CallbackSpec option =
        match Toml.getValue "callbacks" doc with
        | None -> None
        | Some (TomlValue.Table table) ->
            let registrations =
                match TomlTable.tryFind "registrations" table with
                | Some (TomlValue.Array items) ->
                    items |> List.choose (fun item ->
                        match item with
                        | TomlValue.Table t | TomlValue.InlineTable t ->
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
                        | TomlValue.Table t | TomlValue.InlineTable t ->
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
    let private deserializeCore (doc: TomlDocument) : PilotProject =
        let lib = deserializeLibrary doc
        let output = deserializeOutput doc
        { Library = lib
          Output = output
          Namespaces = deserializeNamespaces doc
          ErrorConventions = deserializeErrorConventions doc
          Options = deserializeOptions doc
          Callbacks = deserializeCallbacks doc
          Nonnull = deserializeNonnull doc
          ProtocolConfig = deserializeProtocolConfig doc
          Layer3 = None
          CppConfig = deserializeCppConfig doc }

    /// A pilot document's project, or its TOML-shape refusal (#199): each carries its FS code
    /// and the pilot key whose value causes it.
    let deserializeDiagnosed (doc: TomlDocument) : Result<PilotProject, Diagnostics.Refusal> =
        try Ok (deserializeCore doc)
        with
        | :? Diagnostics.RefusalException as refused -> Error refused.Refusal
        | ex -> Error (Diagnostics.uncoded ex.Message)

    /// Invalid explicit projection annotations are reported as project errors.
    let deserialize (doc: TomlDocument) : Result<PilotProject, string> =
        deserializeDiagnosed doc |> Result.mapError _.Text

    // =========================================================================
    // File I/O
    // =========================================================================

    /// Load a PilotProject from a TOML file path. A file that cannot be read or is no TOML
    /// document is FS9197; each refusal carries the pilot file (#199).
    let loadFromFileDiagnosed (path: string) : Result<PilotProject, Diagnostics.Refusal> =
        let pilotFile = try System.IO.Path.GetFullPath path with _ -> path
        let unreadable message =
            Diagnostics.diagnostic Diagnostics.PilotDocumentUnreadable message
                (Diagnostics.Located.Unknown "the refusal is in pilot TOML input, not a header declaration")
            |> Diagnostics.withPilot ({ PilotFile = pilotFile; Key = ""; Parameter = None } : Diagnostics.PilotLocation)
            |> Diagnostics.refusalOf
        let content =
            try Ok (System.IO.File.ReadAllText path)
            with ex -> Error (unreadable $"Failed to read file: {ex.Message}")
        content
        |> Result.bind (fun content ->
            match Toml.parse content with
            | Error err -> Error (unreadable $"TOML parse error: {err}")
            | Ok doc ->
                deserializeDiagnosed doc
                |> Result.mapError (Diagnostics.mapDiagnostics (fun d ->
                    match d.Pilot with
                    | Some pilot when pilot.PilotFile = "" -> Diagnostics.withPilot { pilot with PilotFile = pilotFile } d
                    | _ -> d)))

    /// `loadFromFileDiagnosed`, reporting a failure as its text.
    let loadFromFile (path: string) : Result<PilotProject, string> =
        loadFromFileDiagnosed path |> Result.mapError _.Text

    /// Save a PilotProject to a TOML file.
    let saveToFile (path: string) (project: PilotProject) : Result<unit, string> =
        try
            let content = toTomlString project
            System.IO.File.WriteAllText(path, content)
            Ok ()
        with ex ->
            Error $"Failed to write file: {ex.Message}"
