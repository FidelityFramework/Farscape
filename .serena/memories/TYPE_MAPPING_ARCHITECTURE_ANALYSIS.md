# Type Mapping Architecture (Feb 2026 — RESOLVED)

## Architecture: Single Type Dictionary (Fidelity-Only)

Farscape uses a single type dictionary for its Fidelity output (P/Invoke support removed Feb 2026):

### 1. TypeMapper.fs — NTU Type Dictionary (Fidelity/Native)
- Maps C types to **NTU Clef types** for Fidelity pipeline
- Takes `PlatformABI` parameter (for `long`/`unsigned long` only — no NTU `long` type exists)
- **DTS-correct mappings (Mar 2026)**:
  - `int` → `int` (NTU register-width dimensional — NOT `int32`)
  - `unsigned int` → `uint` (NTU register-width dimensional — NOT `uint32`)
  - `long` → `int64` (LP64) or `int32` (LLP64/ILP32/IP16) — resolved per platform (no NTU `long`)
  - `int32_t` → `int32` (genuinely fixed-width, correct)
  - `char*` → `nativeptr<byte>` (raw native pointer, no marshalling)
  - `size_t` → `unativeint` (pointer-width, NTU Resolved Pointer)
  - `intptr_t` → `nativeint` (pointer-width, NTU Resolved Pointer)
- **NTU dimensional types** (`int`, `uint`) carry `WidthDimension.Register` — resolved late by platform context
- **Zero literals**: Wrappers use `0` for `int`-returning functions, `0l` for `int32_t`-returning functions
- Used by: FidelityCodeGenerator, WrapperCodeGenerator (Layer 2)
- Types flow through CCS → Baker → Alex → MLIR

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
| Fidelity (Layer 1) | TypeMapper | nativeptr&lt;byte&gt; | int64 | int32 |
| Wrappers (Layer 2) | TypeMapper | nativeptr&lt;byte&gt; | int64 | int32 |

### CLI Integration
- `--output-mode fidelity` or `--output-mode fidelity-wrappers`
- Default: LP64 when no ABI specified

