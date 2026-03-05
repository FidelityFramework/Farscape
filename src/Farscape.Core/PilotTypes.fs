namespace Farscape.Core

/// Types for Pilot project analysis and TOML-driven scoped binding generation.
///
/// Pilot analyzes C library headers to identify rational namespace subdivisions
/// based on function prefix patterns, then produces .pilot.toml project files
/// that drive scoped binding generation via `farscape generate --project`.
module PilotTypes =

    // =========================================================================
    // TOML Project Model
    // =========================================================================

    /// A single namespace subdivision, either discovered by analysis or hand-configured.
    type NamespaceSpec = {
        /// Fully qualified Clef module name, e.g. "Fidelity.libc.Memory"
        Name: string
        /// Human-readable description of this namespace group
        Description: string
        /// Native library name (for linking), e.g. "libc"
        Library: string
        /// Function name prefixes that belong to this namespace, e.g. ["mem"; "str"]
        Prefixes: string list
        /// Explicitly listed function names (no prefix match needed)
        Functions: string list
        /// XML interface names to include (e.g. ["wl_surface"; "wl_compositor"])
        XmlInterfaces: string list
    }

    /// Library-level metadata in a .pilot.toml [library] section.
    type LibrarySpec = {
        /// Library name, e.g. "libc"
        Name: string
        /// Paths to header files (one or more)
        Headers: string list
        /// Paths to XML protocol files (e.g. Wayland .xml), parsed separately from C headers
        XmlProtocols: string list
        /// Additional include paths for clang
        IncludePaths: string list
        /// Preprocessor defines
        Defines: string list
        /// Macro name prefixes to include (e.g. ["WL_"; "WAYLAND_"]).
        /// When empty, all user macros pass through (which can pull in system header noise).
        MacroPrefixes: string list
        /// pkg-config package names used to resolve include paths and defines.
        /// Stored for reproducibility (e.g. ["gtk+-3.0"] or ["webkit2gtk-4.1"; "gtk+-3.0"]).
        PkgConfig: string list
    }
    with
        /// Backward-compat: returns the single header (or first if multiple).
        member this.Header =
            match this.Headers with
            | h :: _ -> h
            | [] -> failwith "LibrarySpec.Headers must not be empty"

    /// Output configuration in a .pilot.toml [output] section.
    type OutputSpec = {
        /// Output mode: "fidelity" or "fidelity-wrappers"
        Mode: string
        /// Output directory for generated bindings
        Directory: string
    }

    // =========================================================================
    // Error Convention Types
    // =========================================================================

    /// Error handling convention for a library or function.
    type ErrorConvention =
        /// Errors reported via errno (POSIX/libc pattern)
        | Errno
        /// Nonzero return value IS the error code (pthread pattern)
        | ReturnCode
        /// Typed enum return: one value = success, all others = error codes (HIP/XRT pattern).
        /// errorType: enum name (e.g. "hipError_t"), successValue: success variant (e.g. "hipSuccess"),
        /// errorStringFn/errorNameFn: optional runtime fallback functions.
        | EnumErrorCode of errorType: string * successValue: string
                         * errorStringFn: string option * errorNameFn: string option
        /// Null return with companion reason function (stb_image, SDL pattern).
        /// reasonFunction: name of the function that returns the error string.
        | NullWithReason of reasonFunction: string
        /// No error convention; function doesn't report errors
        | NoErrorConvention

    /// Error convention configuration for a library.
    type ErrorConventionSpec = {
        /// Default convention for all functions in this library
        Default: ErrorConvention
        /// Per-function overrides (function name → convention)
        Overrides: Map<string, ErrorConvention>
    }

    /// Generation options from [options] section of .pilot.toml
    type ProjectOptions = {
        /// Struct names requiring ABI-exact layout (e.g., ioctl args, DMA descriptors)
        AbiCriticalStructs: string list
        /// Whether to generate BAREWire StructDescriptor values
        GenerateDescriptors: bool
    }

    // =========================================================================
    // Nonnull Annotations
    // =========================================================================

    /// Annotations for pointer parameters/returns proven non-null by developer knowledge.
    /// Used to override the default nullable policy: unannotated C pointers are nullable
    /// (Option<nativeptr<byte>> / Option<nativeint>), and NonNullAttr or these annotations
    /// prove non-null (nativeptr<byte> / nativeint).
    type NonnullAnnotations = {
        /// Function name → list of 0-based parameter indices that are proven non-null
        Parameters: Map<string, int list>
        /// Function names whose return value is proven non-null
        Returns: Set<string>
    }

    // =========================================================================
    // Callback Convention Types
    // =========================================================================

    /// A function that registers a callback (takes function pointer + userdata).
    type CallbackRegistration = {
        /// Function name (e.g., "g_signal_connect_data")
        Function: string
        /// Parameter name that's the function pointer (e.g., "c_handler")
        CallbackParam: string
        /// Companion userdata parameter name (e.g., "data")
        DataParam: string option
    }

    /// A struct that is a callback table (all/most fields are function pointers).
    type ListenerStruct = {
        /// Struct name (e.g., "wl_pointer_listener")
        Name: string
        /// Companion registration function name, if discovered
        RegistrationFunction: string option
    }

    /// Callback convention configuration for a library.
    type CallbackSpec = {
        /// Functions that register callbacks
        Registrations: CallbackRegistration list
        /// Structs that are listener/callback tables
        ListenerStructs: ListenerStruct list
    }

    /// Configuration for protocol-defined APIs that dispatch through a core marshal function.
    /// Comes from the pilot TOML [protocol] section.
    type ProtocolConfig = {
        /// Core marshal function name (e.g. "wl_proxy_marshal_array_flags")
        MarshalFunction: string
        /// Module containing the marshal function (e.g. "Fidelity.Wayland.Core")
        MarshalModule: string
        /// Core version query function (e.g. "wl_proxy_get_version")
        VersionFunction: string
        /// How to resolve interface globals — "dlsym" uses Fidelity.Libc.DynamicLink.dlsym
        InterfaceResolution: string
        /// Flag value for destructor requests (e.g. 1u for WL_MARSHAL_FLAG_DESTROY)
        DestroyFlag: uint32
    }

    // =========================================================================
    // Layer 3 Bridge Requirements
    // =========================================================================

    /// External dependencies required by Layer 3 bridge code.
    type Layer3Dependency =
        /// dlsym for interface resolution + callback binding
        | LibcDynamicLink
        /// malloc/free for protocol argument arrays
        | LibcMemory

    /// Layer 3 requirement analysis result — determines whether a Bridge package is needed.
    type Layer3Requirement = {
        /// External dependencies the bridge code needs
        Dependencies: Layer3Dependency list
        /// Protocol dispatch implementations needed
        HasProtocolDispatch: bool
        /// Callback wrappers (dlsym-based) needed
        HasCallbackWrappers: bool
        /// Interfaces with constructors but no paired destructor — developer must review
        UnpairedConstructors: string list
        /// Patterns the generator couldn't handle — developer must implement by hand
        UnmappedPatterns: string list
    }

    /// Complete Pilot project, corresponding to a .pilot.toml file.
    type PilotProject = {
        Library: LibrarySpec
        Output: OutputSpec
        Namespaces: NamespaceSpec list
        /// Error convention configuration (None = no errno support)
        ErrorConventions: ErrorConventionSpec option
        /// Generation options (None = defaults)
        Options: ProjectOptions option
        /// Callback pattern configuration (None = no callback wrappers)
        Callbacks: CallbackSpec option
        /// Nonnull annotations (None = all pointers are nullable by default)
        Nonnull: NonnullAnnotations option
        /// Protocol dispatch configuration (None = no XML protocol request generation)
        ProtocolConfig: ProtocolConfig option
        /// Layer 3 bridge requirements (None = no bridge package needed).
        /// Computed by PilotAnalyzer.analyzeLayer3Requirements, not serialized.
        Layer3: Layer3Requirement option
    }

    // =========================================================================
    // Analysis Result Types
    // =========================================================================

    /// A group of functions sharing a common naming pattern.
    type PrefixGroup = {
        /// Matching prefixes, e.g. ["str"] or ["fopen"; "fclose"; "fread"]
        Prefixes: string list
        /// Function names in this group
        FunctionNames: string list
        /// Suggested namespace suffix, e.g. "String"
        SuggestedName: string
    }

    /// Result of prefix analysis on a set of declarations.
    type AnalysisResult = {
        /// Groups discovered by prefix analysis
        Groups: PrefixGroup list
        /// Functions that did not match any prefix group
        Ungrouped: string list
        /// Total function count
        TotalFunctions: int
    }
