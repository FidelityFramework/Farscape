namespace Farscape.Core

module Types =
    type OperationStatus =
        | Success = 0
        | Error = 1
        | InvalidArgument = 2
        | NotImplemented = 3
        | NotSupported = 4
        | MemoryError = 5
        | TimeoutError = 6

    /// Output mode for code generation.
    /// PInvoke: DllImport-based .NET bindings (original mode)
    /// Fidelity: Platform.Bindings pattern for Firefly native compilation
    type OutputMode =
        | PInvoke
        | Fidelity