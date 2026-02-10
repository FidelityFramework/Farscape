namespace Farscape.Core

/// Types for Moya project analysis and TOML-driven scoped binding generation.
///
/// Moya analyzes C library headers to identify rational namespace subdivisions
/// based on function prefix patterns, then produces .moya.toml project files
/// that drive scoped binding generation via `farscape generate --project`.
module MoyaTypes =

    // =========================================================================
    // TOML Project Model
    // =========================================================================

    /// A single namespace subdivision, either discovered by analysis or hand-configured.
    type NamespaceSpec = {
        /// Fully qualified F# module name, e.g. "Fidelity.libc.Memory"
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

    /// Library-level metadata in a .moya.toml [library] section.
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

    /// Output configuration in a .moya.toml [output] section.
    type OutputSpec = {
        /// "fidelity" or "pinvoke"
        Mode: string
        /// Output directory for generated bindings
        Directory: string
    }

    /// Complete Moya project, corresponding to a .moya.toml file.
    type MoyaProject = {
        Library: LibrarySpec
        Output: OutputSpec
        Namespaces: NamespaceSpec list
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
