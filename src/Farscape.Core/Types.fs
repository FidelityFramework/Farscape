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

    /// Platform ABI determines concrete widths for platform-dependent C types.
    /// Used by both TypeMapper (NTU/Fidelity output) and PInvokeTypeMapper (CLR output).
    /// LP64:  int=32, long=64, ptr=64 (Linux, macOS, most Unix)
    /// LLP64: int=32, long=32, ptr=64 (Windows x64)
    /// ILP32: int=32, long=32, ptr=32 (32-bit systems)
    /// IP16:  int=16, long=32, ptr=16/32 (AVR, MSP430, some embedded)
    type PlatformABI =
        | LP64
        | LLP64
        | ILP32
        | IP16