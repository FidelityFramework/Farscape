# Farscape's Role in the Fidelity Ecosystem

## Position in the Architecture

Farscape is the **binding generator** for the Fidelity native compilation pipeline. It transforms C headers into Clef source with `[<FidelityExtern>]` attributes and BAREWire memory descriptors:

```
C Headers → Farscape → Clef Source (FidelityExtern declarations + Wrappers + Descriptors)
```

Farscape runs at **generation time**, before Composer compilation.

## Two-Layer Output (Both Implemented)

### Layer 1: Platform.Bindings
```fsharp
[<FidelityExtern("libc", "write")>]
let write (fd: int32) (buf: nativeint) (count: nativeint) : int64 =
    Unchecked.defaultof<int64>
```

### Layer 2: Idiomatic Clef Wrappers
```fsharp
/// ssize_t write(int fd, const void *buf, size_t count)
let write (fd: int32) (buf: nativeint) (count: nativeint) : Result<nativeint, CError> =
    let result = Platform.Bindings.Libc.IO.write fd buf count
    if result >= 0n then Ok result
    else Error (captureError ())
```

Error path returns `CError` struct with errno code + description string from header comments.
Both layers flow through: CCS → Baker → Alex → MLIR → LLVM → native binary. Wrappers compile to zero overhead via type erasure.

## What Farscape Parses from C Headers

| C Construct | Clef Output |
|-------------|-----------|
| `typedef struct {...} xxx_ctrl_t` | PeripheralDescriptor + Clef record |
| `__IO uint32_t FIELD` | Register with Access = ReadWrite |
| `__I uint32_t FIELD` | Register with Access = ReadOnly |
| `__O uint32_t FIELD` | Register with Access = WriteOnly |
| `#define XXX_BASE (addr)` | Instance address in Instances map |
| `#define XXX_Pos (n)` | BitField position |
| `#define XXX_Msk (m)` | BitField width (computed from mask) |
| `typedef enum {...}` | Clef discriminated union |
| `RetType FuncName(params)` | FidelityExtern binding declaration + optional wrapper |

## Sliced Package Architecture

Farscape generates categorized, source-based packages from C headers:

| Package | C Headers | Key Functions |
|---------|-----------|---------------|
| `Fidelity.libc.IO` | `<unistd.h>` | read, write, open, close, lseek, pipe |
| `Fidelity.libc.Memory` | `<string.h>` | memcpy, memset, strlen, strcpy |
| `Fidelity.libc.Alloc` | `<stdlib.h>` | malloc, free, calloc, realloc, abort, exit |

Reachability analysis means only functions actually called get MLIR emissions. Unused bindings cost zero in the final binary.

## TOML Artifacts (Distinct Roles)

- **Pilot TOML** (`.pilot.toml`): Namespace analysis and grouping by C function prefix patterns
- **fidproj TOML**: Generated library project file for the Fidelity build system

These are separate artifacts with completely different purposes.

## What Farscape Does NOT Do

- Generate MLIR or LLVM code (Alex does this)
- Make platform-specific code generation decisions (Alex does this)
- Compile the vendor library itself (pre-compiled by vendor)
- Use P/Invoke (Farscape is Fidelity-only, no .NET interop)

## Library Verification (Mar 2026)

Generated libraries must pass CCS type-checking with zero errors before they are trusted. The `farscape verify` command runs CCS against a library's fidproj, reporting diagnostics at true severity (bypassing CCS's reachability-based demotion). Verification failures are bugs in the generation pipeline, fixed systematically. See `library_verification_clefpak` memory.

## fidproj Generation (Mar 2026)

Farscape generates **one fidproj per pilot TOML invocation**. The fidproj name is derived from the namespace prefix in the pilot TOML:
- Single-namespace: full name (e.g., `Fidelity.Image.Stb` → `Fidelity.Image.Stb.fidproj`)
- Multi-namespace: common prefix (e.g., `Fidelity.libc.*` → `Fidelity.libc.fidproj`)

Library authors compose parent fidproj files at whatever hierarchical level makes sense. Farscape does not generate parent/consolidated fidproj files.

## Current Focus

Library verification pipeline and WrenHello compilation with Farscape-generated binding libraries.

## Roadmap

- **Library verification**: `farscape verify` with CCS integration
- **clefpak**: Signed source artifacts for package management
- **Static binding**: LLVM LTO for statically bound libraries
- **C++ support**: Plugify ABI intelligence
- **Atelier evolution**: Farscape → Composer's Transpose (typed dynamic binding) and Transcribe (algorithmic port)

## Relationship to Other Projects

| Project | Relationship |
|---------|-------------|
| **BAREWire** | Farscape uses BAREWire types; generates memory descriptors |
| **Composer** | Composer compiles Farscape's output; Alex (Composer's middle-end) handles FidelityExtern |
| **CCS** | Clef Compiler Service; type-checks Farscape's output in NTU. Baker operates within CCS |
