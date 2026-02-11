# Farscape Binding Architecture

## Core Principle: Closed-Loop Fidelity Pipeline

Farscape is not a standalone binding generator. It is one component of a closed-loop native compilation system:

```
C Headers → Farscape → F# source with [<FidelityExtern>] → FNCS (type-check in NTU) → Baker (saturate) → Alex (MLIR) → LLVM → native binary
```

Every piece carries intent forward. `[<FidelityExtern>]` carries library name + symbol through the PSG so Alex can emit MLIR with `fidelity.binding_strategy` and `fidelity.library_name` attributes, and the linker auto-collects flags (`-lc`, `-lwayland-client`, etc.).

**There is NO P/Invoke in the Fidelity framework. Only FidelityExtern.**

P/Invoke mode exists as a separate output target for traditional .NET F# interop. It is not part of the Fidelity pipeline.

## Two-Layer Model

### Layer 1: Platform.Bindings (core infrastructure)
Raw extern stubs with `[<FidelityExtern>]` attributes:
```fsharp
[<FidelityExtern("libc", "write")>]
let write (fd: int32) (buf: nativeint) (count: nativeint) : int64 =
    Unchecked.defaultof<int64>
```

The `Unchecked.defaultof<T>` body is a placeholder. FNCS type-checks it. Baker recognizes the pattern. Alex emits platform-specific MLIR. The `[<FidelityExtern>]` attribute tells the pipeline which library and symbol this binding targets.

**Current state**: Stubs generate without `[<FidelityExtern>]` attribute. Alex infers from naming conventions. Adding the attribute is core infrastructure work that closes the loop.

### Layer 2: Idiomatic F# Wrappers (implemented)
Safe functional APIs that call Layer 1:
- C error codes → `Result<T, CError>` with errno capture + description from header comments
- `CError` is `[<Struct>]` (stack-allocated): Code (int32) + Description (rodata string pointer)
- `Errno.describe` function compiles to jump table over rodata strings — O(1), zero allocation
- Return semantic classification (7 patterns driven by 12 clang attribute types)
- Error convention configurable via Moya TOML `[error_conventions]` (errno vs return_code)
- All wrappers compile to zero-overhead native code via type erasure

Layer 2 is implemented via WrapperPatternAnalyzer + WrapperCodeGenerator + ErrnoModuleGenerator. CLI: `--output-mode fidelity-wrappers`

## Output Modes

| Mode | CLI Flag | Target |
|------|----------|--------|
| `fidelity` | `--output-mode fidelity` | F# Native / Fidelity pipeline (FidelityExtern) |
| `fidelity-wrappers` | `--output-mode fidelity-wrappers` | F# Native with Layer 2 wrappers |
| `pinvoke` | `--output-mode pinvoke` | Traditional .NET F# (DllImport, NOT Fidelity) |

## Moya Feature (completed)
- Namespace grouping by C function prefix patterns
- Moya TOML project files (.moya.toml) for scoped generation
- CLI: `moya analyze`, `moya init`, `project --project`
- FSharp.SystemCommandLine v2.1 pipeline API

Note: Moya TOML and the generated fidproj library TOML are distinct artifacts with separate roles.

## Core Infrastructure Under Development

These are not optional future features. They are what makes Farscape part of Fidelity rather than a standalone script:

1. **`[<FidelityExtern>]` attributes**: Library name + symbol metadata on stubs. Closes the pipeline loop.
2. **BAREWire memory/type layout capture**: Reading header AST for precise struct layout, access constraints, memory regions. Core to memory safety guarantees.
3. **CMSIS qualifier mapping**: `__I` → ReadOnly, `__O` → WriteOnly, `__IO` → ReadWrite. Hardware-enforced access constraints.

## Roadmap

- **Static binding**: LLVM LTO cross-language inlining for statically bound libraries (current focus: libc dynamic binding)
- **C++ support**: Plugify ABI intelligence for virtual tables, templates, RAII, exception boundaries
- **fsnx interactive mode**: Dynamic FFI for development, static binding for release builds

## Type System Separation (Feb 2026)

Two completely separate type dictionaries:
- **TypeMapper.fs** (NTU): `long` → `nativeint` (abstract), `char*` → `nativeptr<byte>`. Used by Fidelity + Layer 2.
- **PInvokeTypeMapper.fs** (CLR): `long` → `int64`/`int32` per `PlatformABI`. Used by P/Invoke only.
- `PlatformABI` type: LP64 | LLP64 | ILP32 | IP16

## FNCS Type System
- int → NTUint (platform word), size_t → NTUsize
- Strings are UTF-8 fat pointers (ptr + len), not .NET UTF-16
- Option/Result are stack-allocated tagged structs
- Memory regions (Stack, Arena, Peripheral) tracked at type level