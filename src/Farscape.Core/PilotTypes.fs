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
    }

    /// Library-level metadata in a .pilot.toml [library] section.
    type LibrarySpec = {
        /// Library name, e.g. "libc"
        Name: string
        /// Paths to header files (one or more)
        Headers: string list
        /// Additional include paths for clang
        IncludePaths: string list
        /// Preprocessor defines
        Defines: string list
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
        /// No error convention; function doesn't report errors
        | NoErrorConvention

    /// Error convention configuration for a library.
    type ErrorConventionSpec = {
        /// Default convention for all functions in this library
        Default: ErrorConvention
        /// Per-function overrides (function name → convention)
        Overrides: Map<string, ErrorConvention>
    }

    /// Complete Pilot project, corresponding to a .pilot.toml file.
    type PilotProject = {
        Library: LibrarySpec
        Output: OutputSpec
        Namespaces: NamespaceSpec list
        /// Error convention configuration (None = no errno support)
        ErrorConventions: ErrorConventionSpec option
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
