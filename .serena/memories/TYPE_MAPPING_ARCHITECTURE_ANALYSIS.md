# Type Mapping Architecture (Feb 2026 — RESOLVED)

## Architecture: Two Separate Type Dictionaries

Farscape uses **two completely separate type dictionaries** for its two output modes:

### 1. TypeMapper.fs — NTU Type Dictionary (Fidelity/Native)
- Maps C types to **NTU-abstract F# types** for Fidelity pipeline
- `long` → `nativeint` (platform-abstract, deferred to NTU width resolution)
- `char*` → `nativeptr<byte>` (raw native pointer, no marshalling)
- `size_t` → `unativeint` (pointer-width, NTU resolves)
- Used by: FidelityCodeGenerator, WrapperCodeGenerator (Layer 2)
- Types flow through FNCS → Baker → Alex → MLIR with width resolved at codegen

### 2. PInvokeTypeMapper.fs — CLR Type Dictionary (P/Invoke/.NET)
- Maps C types to **concrete CLR-marshallable F# types** per platform ABI
- Takes `PlatformABI` parameter: LP64, LLP64, ILP32, IP16
- `long` → `int64` (LP64) or `int32` (LLP64/ILP32/IP16)
- `char*` → `string` (CLR handles marshalling)
- `int` → `int32` (LP64/LLP64/ILP32) or `int16` (IP16)
- Used by: PInvokeCodeGenerator only

### PlatformABI Type
```fsharp
type PlatformABI =
    | LP64   // Linux, macOS, most Unix: int=32, long=64, ptr=64
    | LLP64  // Windows x64: int=32, long=32, ptr=64
    | ILP32  // 32-bit systems: int=32, long=32, ptr=32
    | IP16   // AVR, MSP430, embedded: int=16, long=32, ptr=16/32
```

### Type Flow Summary

| Component | Type Mapper | char* | long (LP64) | long (LLP64) |
|-----------|------------|-------|-------------|--------------|
| Fidelity (Layer 1) | TypeMapper | nativeptr&lt;byte&gt; | nativeint | nativeint |
| Wrappers (Layer 2) | TypeMapper | nativeptr&lt;byte&gt; | nativeint | nativeint |
| P/Invoke | PInvokeTypeMapper | string | int64 | int32 |

### CLI Integration
- `farscape generate --output-mode dotnet:lp64` — P/Invoke with LP64 ABI
- `farscape project --dotnet --data-model llp64` — project mode with LLP64
- Default: LP64 when no ABI specified

## Previous Bug (FIXED)
PInvokeCodeGenerator previously used TypeMapper.getFSharpType (NTU dictionary) for base type resolution. This produced NTU-abstract types (`nativeint` for C `long`) in P/Invoke output — wrong because P/Invoke needs concrete CLR types matching the target ABI. Fixed by creating PInvokeTypeMapper with `PlatformABI` parameter.
