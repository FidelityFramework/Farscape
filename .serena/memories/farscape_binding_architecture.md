# Farscape Binding Architecture

## Core Principle: Closed-Loop Fidelity Pipeline

Farscape is not a standalone binding generator. It is one component of a closed-loop native compilation system:

```
C Headers → Farscape → Clef source with [<FidelityExtern>] → CCS (type-check in NTU) → Baker (saturate) → Alex (MLIR) → LLVM → native binary
```

Composer is the compilation app. CCS (Clef Compiler Service) is the type-checking service within it. Baker operates within CCS. Alex is Composer's middle-end (MLIR emission).

Every piece carries intent forward. `[<FidelityExtern>]` carries library name + symbol through the PSG so Alex can emit MLIR with `fidelity.binding_strategy` and `fidelity.library_name` attributes, and the linker auto-collects flags (`-lc`, `-lwayland-client`, etc.).

**There is NO P/Invoke in the Fidelity framework. Only FidelityExtern.**

## Two-Layer Model

### Layer 1: Platform.Bindings (core infrastructure)
Raw extern binding declarations with `[<FidelityExtern>]` attributes:
```fsharp
[<FidelityExtern("libc", "write")>]
let write (fd: int32) (buf: nativeint) (count: nativeint) : int64 =
    Unchecked.defaultof<int64>
```

The `Unchecked.defaultof<T>` body is a compiler-recognized placeholder. CCS type-checks the declaration. Baker recognizes the pattern. Alex emits platform-specific MLIR. The `[<FidelityExtern>]` attribute tells the pipeline which library and symbol this binding targets.

**Current state**: Declarations generate without `[<FidelityExtern>]` attribute. Alex infers from naming conventions. Adding the attribute is core infrastructure work that closes the loop.

### Layer 2: Idiomatic Clef Wrappers (implemented)
Safe functional APIs that call Layer 1:
- C error codes → `Result<T, CError>` with errno capture + description from header comments
- `CError` is `[<Struct>]` (stack-allocated): Code (int32) + Description (rodata string pointer)
- `Errno.describe` function compiles to jump table over rodata strings — O(1), zero allocation
- Return semantic classification (7 patterns driven by 12 clang attribute types)
- Error convention configurable via Pilot TOML `[error_conventions]` (errno vs return_code)
- All wrappers compile to zero-overhead native code via type erasure

Layer 2 is implemented via WrapperPatternAnalyzer + WrapperCodeGenerator + ErrnoModuleGenerator. CLI: `--output-mode fidelity-wrappers`

## Output Modes

| Mode | CLI Flag | Target |
|------|----------|--------|
| `fidelity` | `--output-mode fidelity` | Clef / Fidelity pipeline (FidelityExtern) |
| `fidelity-wrappers` | `--output-mode fidelity-wrappers` | Clef with Layer 2 wrappers |

## Pilot Feature (completed)
- Namespace grouping by C function prefix patterns
- Pilot TOML project files (.pilot.toml) for scoped generation
- CLI: `pilot analyze`, `pilot init`, `project --project`
- FSharp.SystemCommandLine v2.1 pipeline API

Note: Pilot TOML and the generated fidproj library TOML are distinct artifacts with separate roles.

## Core Infrastructure Under Development

These are not optional future features. They are what makes Farscape part of Fidelity rather than a standalone script:

1. **`[<FidelityExtern>]` attributes**: Library name + symbol metadata on stubs. Closes the pipeline loop.
2. **BAREWire memory/type layout capture**: Reading header AST for precise struct layout, access constraints, memory regions. Core to memory safety guarantees.
3. **CMSIS qualifier mapping**: `__I` → ReadOnly, `__O` → WriteOnly, `__IO` → ReadWrite. Hardware-enforced access constraints.

## Roadmap

- **Static binding**: LLVM LTO cross-language inlining for statically bound libraries (current focus: libc dynamic binding)
- **C++ support**: Plugify ABI intelligence for virtual tables, templates, RAII, exception boundaries
- **Interactive mode**: Dynamic FFI for development, static binding for release builds (naming TBD)

## Type System (Feb 2026)

Single type dictionary (P/Invoke support removed):
- **TypeMapper.fs** (NTU): PlatformABI-parameterized. `long` → `int64` (LP64) / `int32` (LLP64/ILP32). `char*` → `nativeptr<byte>`. Used by Fidelity + Layer 2.
- `PlatformABI` type: LP64 | LLP64 | ILP32 | IP16

## CCS Type System (Width-as-Dimension, Feb 2026)
- `int` → `NTUint (Resolved Register)` (platform word), `size_t` → `NTUsize`
- `int32` → `NTUint (Fixed 32)`, `int64` → `NTUint (Fixed 64)` (width-fixed)
- `nativeint` → `NTUint (Resolved Pointer)` (pointer-sized)
- Width is a first-class dimension: `NTUWidth = Fixed of bits | Resolved of WidthDimension`
- `PlatformContext.Dimensions: Map<WidthDimension, int>` resolves `Resolved` widths
- `NTUother` eliminated — no escape hatch
- Strings are UTF-8 fat pointers (ptr + len), not .NET UTF-16
- Option/Result are stack-allocated tagged structs
- Memory regions (Stack, Arena, Peripheral) tracked at type level

## Farscape's Role in the Dimensional Architecture (Feb 2026)

**Farscape is a second-order consumer of the NTU's dimensional type system, not a driver.**

Farscape exposed the C `int`/`long` width problem that catalyzed the broader NTU dimensional
rethinking. But the solution lives in the NTU and Fidelity.Platform, not in Farscape.

**For Fidelity output**: Farscape uses PlatformABI to resolve C-specific widths at generation
time, emitting Fixed-width NTU types. C `int` on LP64 → `int32` (Fixed 32). C `long` on LP64
→ `int64` (Fixed 64). Only genuinely platform-abstract types (`size_t → unativeint`,
`intptr_t → nativeint`) use Resolved dimensions.

**The bigger picture**: The NTU is evolving into a multi-dimensional type substrate where width
is the first axis, with memory space, access pattern, alignment, and tensor shape as future
axes. This supports multi-stack targeting (CPU/GPU/FPGA/NPU) with BAREWire reconciliation
between graph sections. Full design in NTU dimensional architecture spec.

**What this means for Farscape**: No changes needed beyond PlatformABI-aware Fidelity output.
New NTU dimensions come from Fidelity.Platform, not from binding generators. Farscape binds
C/C++ libraries for targets where C FFI exists (desktop, MCU, GPU host-side). Targets without
C FFI (FPGA fabric, NPU tiles) don't need Farscape — they use Fidelity.Platform + BAREWire.