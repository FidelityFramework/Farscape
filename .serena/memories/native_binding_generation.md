# Farscape's Role in the Fidelity Ecosystem

## Position in the Architecture

Farscape is the **binding generator** for the Fidelity native compilation pipeline. It transforms C headers into F# source with `[<FidelityExtern>]` attributes and BAREWire memory descriptors:

```
C Headers → Farscape → F# Source (FidelityExtern stubs + Wrappers + Descriptors)
```

Farscape runs at **generation time**, before Firefly compilation. It is a .NET tool (`dotnet tool install -g farscape`).

## Two-Layer Output (Both Implemented)

### Layer 1: Platform.Bindings
```fsharp
[<FidelityExtern("libc", "write")>]
let write (fd: int32) (buf: nativeint) (count: nativeint) : int64 =
    Unchecked.defaultof<int64>
```

### Layer 2: Idiomatic F# Wrappers
```fsharp
let write (fd: int32) (buf: nativeint) (count: nativeint) : Result<int64, int64> =
    let result = Platform.Bindings.Libc.IO.write fd buf count
    if result >= 0L then Ok result
    else Error result
```

Both layers flow through: FNCS → Baker → Alex → MLIR → LLVM → native binary. Wrappers compile to zero overhead via type erasure.

## What Farscape Parses from C Headers

| C Construct | F# Output |
|-------------|-----------|
| `typedef struct {...} xxx_ctrl_t` | PeripheralDescriptor + F# record |
| `__IO uint32_t FIELD` | Register with Access = ReadWrite |
| `__I uint32_t FIELD` | Register with Access = ReadOnly |
| `__O uint32_t FIELD` | Register with Access = WriteOnly |
| `#define XXX_BASE (addr)` | Instance address in Instances map |
| `#define XXX_Pos (n)` | BitField position |
| `#define XXX_Msk (m)` | BitField width (computed from mask) |
| `typedef enum {...}` | F# discriminated union |
| `RetType FuncName(params)` | FidelityExtern stub + optional wrapper |

## Sliced Package Architecture

Farscape generates categorized, source-based packages from C headers:

| Package | C Headers | Key Functions |
|---------|-----------|---------------|
| `Fidelity.libc.IO` | `<unistd.h>` | read, write, open, close, lseek, pipe |
| `Fidelity.libc.Memory` | `<string.h>` | memcpy, memset, strlen, strcpy |
| `Fidelity.libc.Alloc` | `<stdlib.h>` | malloc, free, calloc, realloc, abort, exit |

Reachability analysis means only functions actually called get MLIR emissions. Unused bindings cost zero in the final binary.

## TOML Artifacts (Distinct Roles)

- **Moya TOML** (`.moya.toml`): Namespace analysis and grouping by C function prefix patterns
- **fidproj TOML**: Generated library project file for the Fidelity build system

These are separate artifacts with completely different purposes.

## What Farscape Does NOT Do

- Generate MLIR or LLVM code (Alex does this)
- Make platform-specific code generation decisions (Alex does this)
- Compile the vendor library itself (pre-compiled by vendor)
- Use P/Invoke within the Fidelity framework (FidelityExtern only)

## Current Focus

Libc dynamic binding: generating FidelityExtern stubs for standard library headers (unistd.h, string.h, stdlib.h).

## Roadmap

- **Static binding**: LLVM LTO for statically bound libraries
- **C++ support**: Plugify ABI intelligence
- **fsnx interactive mode**: Dynamic FFI for dev, static for release

## Relationship to Other Projects

| Project | Relationship |
|---------|-------------|
| **BAREWire** | Farscape uses BAREWire types; generates memory descriptors |
| **Firefly** | Firefly compiles Farscape's output; Alex handles FidelityExtern |
| **FNCS** | Type-checks Farscape's output in NTU |
