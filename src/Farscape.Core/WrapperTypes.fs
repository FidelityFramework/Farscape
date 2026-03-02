namespace Farscape.Core

/// Semantic types for Layer 2 wrapper generation.
///
/// These types represent the semantic interpretation of clang AST attributes
/// and C type/parameter patterns. CppParser extracts raw `AttributeData`;
/// WrapperPatternAnalyzer maps raw → semantic using these types.
///
/// The wrapper generator uses `WrapperPattern` to select the appropriate
/// code generation template for each function.
module WrapperTypes =

    // =========================================================================
    // Semantic Attribute Classification
    // =========================================================================

    /// Semantic attributes extracted from clang AST, driving wrapper pattern selection.
    /// Mapped from raw `CppParser.AttributeData` by `WrapperPatternAnalyzer`.
    type FunctionAttribute =
        /// Function allocates memory; param indices specify which params control size.
        | AllocSize of paramIndices: int list
        /// Parameters at these indices must not be null.
        | NonNull of paramIndices: int list
        /// Printf/scanf-style format string. Archetype is "printf", "scanf", etc.
        | Format of archetype: string * formatIdx: int * firstArgIdx: int
        /// Function never returns (abort, _exit).
        | NoReturn
        /// Function does not throw exceptions.
        | NoThrow
        /// Function is rarely called (error/diagnostic path).
        | Cold
        /// Return pointer has restrict qualification.
        | Restrict
        /// Function has no side effects (may read globals).
        | Pure
        /// Function has no side effects and does not read globals.
        | Const
        /// Caller must use the return value.
        | WarnUnusedResult
        /// Parameter at index specifies alignment for allocation.
        | AllocAlign of paramIdx: int
        /// Assembly label (symbol name in binary).
        | AsmLabel of label: string

    // =========================================================================
    // Parameter Semantic Roles
    // =========================================================================

    /// Parameter semantic role, inferred from type + attributes + name + position.
    type ParamRole =
        /// Read-only buffer with optional associated length parameter.
        | InputBuffer of lengthParam: string option
        /// Writable output buffer with optional associated length parameter.
        | OutputBuffer of lengthParam: string option
        /// Simple input value (int, enum, etc.).
        | InputValue
        /// POSIX file descriptor (int named fd, fildes, etc.).
        | FileDescriptor
        /// Opaque handle type (FILE*, etc.).
        | OpaqueHandle
        /// Printf/scanf format string parameter.
        | FormatString
        /// Buffer length/size parameter associated with a buffer.
        | BufferLength of bufferParam: string
        /// Nullable input (pointer without NonNull attribute).
        | NullableInput

    // =========================================================================
    // Return Value Semantics
    // =========================================================================

    /// Return value semantic, inferred from return type + attributes.
    type ReturnSemantic =
        /// ssize_t pattern: >= 0 is count, -1 is error.
        | CountOrError
        /// int pattern: 0 = success, nonzero = error code.
        | ZeroSuccessOrError
        /// int pattern: >= 0 is meaningful value (fd, pid, count), -1 is error.
        | IntValueOrError
        /// void* pattern: non-null = allocated pointer, null = error.
        | AllocatedPointer
        /// FILE*/handle pattern: non-null = handle, null = error.
        | OpaqueHandleReturn
        /// Typed enum return: success value = OK, everything else = error (HIP/XRT pattern).
        | EnumReturnError of enumType: string * successValue: string * errorStructName: string
        /// Pure value return; no error checking needed (abs, strlen).
        | PureValue
        /// Function never returns (abort, _exit).
        | NeverReturns
        /// void return; no return value to wrap.
        | VoidReturn

    // =========================================================================
    // Error Handling Strategy
    // =========================================================================

    /// Error handling strategy for wrapper generation.
    /// Replaces the previous `errnoModuleName: string option` parameter.
    type ErrorHandling =
        /// No error handling; wrappers use basic Result wrapping.
        | NoErrors
        /// Errno-based: captures errno via __errno_location and builds CError.
        | UseErrno of errnoModuleName: string
        /// Enum error code: matches return value against typed error enum.
        | UseEnumError of enumType: string * successValue: string
                        * errorStructName: string * describeModuleName: string

    // =========================================================================
    // Complete Wrapper Pattern
    // =========================================================================

    /// Complete wrapper pattern for a function, computed from attributes + types.
    /// This is the input to the wrapper code generator.
    type WrapperPattern = {
        /// Original C function name.
        OriginalName: string
        /// F# wrapper function name (may differ for keyword conflicts).
        WrapperName: string
        /// Parameter roles in order, paired with parameter names.
        ParamRoles: (string * ParamRole) list
        /// Return value semantic classification.
        ReturnSemantic: ReturnSemantic
        /// All semantic attributes for this function.
        Attributes: FunctionAttribute list
        /// Whether the wrapper should return Result<T, E>.
        NeedsResultWrap: bool
        /// Whether the wrapper involves resource lifecycle management.
        NeedsResourceCleanup: bool
        /// Whether the function is side-effect free.
        IsPure: bool
    }
