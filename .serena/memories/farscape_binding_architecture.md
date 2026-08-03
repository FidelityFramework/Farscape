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
```clef
[<FidelityExtern("libc", "write")>]
let write (fd: int) (buf: nativeint) (count: nativeint) : int64 =
    NativeDefault.zeroed ()
```

The `NativeDefault.zeroed()` body is a CCS intrinsic (`unit → 'T`, polymorphic zero init). `Unchecked.defaultof<T>` is BCL and rejected by CCS. CCS type-checks the declaration. Baker recognizes the pattern. Alex emits platform-specific MLIR. The `[<FidelityExtern>]` attribute tells the pipeline which library and symbol this binding targets.

**Current state** (verified 2026-08-03): the attribute **is** emitted (`FidelityCodeGenerator.fs:221, 279`, `ErrnoModuleGenerator.fs:91`). Adding the attribute is core infrastructure work that closes the loop.

### Layer 2: Idiomatic Clef Wrappers (implemented)
Safe functional APIs that call Layer 1:
- C error codes → `Result<T, CError>` with errno capture + description from header comments
- `CError` is `[<Struct>]` (stack-allocated): Code (int, NTU register-width) + Description (rodata string pointer)
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

## Library Verification Pipeline (Mar 2026)

Farscape generates Clef source libraries that must be **verified error-free** via CCS before they are trusted in the Fidelity ecosystem. Two goals:

1. **Correctness gate**: CCS type-checks all sources with zero errors. Type mismatches are bugs in the generation pipeline, not the C headers.
2. **clefpak prerequisite**: Verified libraries become candidates for `clefpak` (signed, compressed source artifact for package management).

**The reachability trap**: CCS demotes diagnostics on unreachable nodes from Error→Info. A library with type errors appears clean as a dependency until application code `open`s its modules. Library verification must bypass this demotion.

> **Not implemented (verified 2026-08-03).** The CLI root command is `generate | pilot | project`; there is no `verify` subcommand. The description below is a design. See `docs/09_Library_Verification.md` and `docs/14_Binding_Generation_Gaps.md` §6.

**Command**: `farscape verify --fidproj <path>` — invokes CCS against library sources, reports diagnostics at true severity.

**Feedback loop**: Verification failures → fix TypeMapper/WrapperCodeGenerator → regenerate → re-verify. Each fix addresses a class of C API patterns, maturing the generator over time.

****fidproj resolution**: Dependencies point at **fidproj files, not directories**. Farscape generates one fidproj per pilot TOML invocation. Library authors compose parent fidproj files. Each fidproj is independently addressable. Directory-based discovery rejected — directories contain non-library artifacts (pilot TOMLs, Layer 3 overlays, regeneration hooks).

## Source-Based Linking (Fidelity Model)

No binary assemblies. Libraries are source packages:
- Composer's SourceResolver walks dependency fidproj files recursively
- `open` statements determine reachability within the PSG
- CCS type-checks all reachable source together in shared type environment
- "Compiling" a library = verification, not artifact production (until clefpak)

## Roadmap

- **Library verification**: `farscape verify` command with CCS integration
- **clefpak**: Signed, compressed source artifact for package management
- **Static binding**: LLVM LTO cross-language inlining for statically bound libraries
- **C++ support**: Plugify ABI intelligence for virtual tables, templates, RAII, exception boundaries
- **Atelier evolution**: Farscape folds into Composer's **Transpose** (typed dynamic binding) and **Transcribe** (full algorithmic port) features, accessible through Atelier IDE

## Farscape's Role in the Dimensional Architecture (Feb 2026)

**Farscape is a second-order consumer of the NTU's dimensional type system, not a driver.**

Farscape exposed the C `int`/`long` width problem that catalyzed the broader NTU dimensional
rethinking. But the solution lives in the NTU and Fidelity.Platform, not in Farscape.

**For Fidelity output**: Farscape uses PlatformABI to resolve C-specific widths at generation
time, emitting Fixed-width NTU types. C `int` → NTU `int` (register-width dimensional, NOT int32). C `unsigned int` → NTU `uint` (NOT uint32). C `long` on LP64 → `int64` (Fixed 64). Only genuinely fixed-width C types (`int32_t`, `uint16_t`) map to fixed NTU types. Platform-abstract types (`size_t → unativeint`, `intptr_t → nativeint`) use Resolved dimensions.

**Callback struct fields — ASPIRATIONAL, NOT IMPLEMENTED (verified 2026-08-03).** `FnPtr` appears in the source only in two comments (`FidelityCodeGenerator.fs:122, 171`) and is never emitted. The implemented mechanism is `dlsym` symbol resolution passing `nativeint` to Layer 1 (`CallbackWrapperGenerator.fs:10-18, 111-121`). The intended design was: callback struct fields use `FnPtr<'F>` — Clef-native typed function pointer. NOT CLR delegates (not in Clef's type algebra), NOT raw nativeint (loses callback signature). Callback builders use `FnPtr.fromSymbol` for runtime symbol resolution.

**What this means for Farscape**: No changes needed beyond PlatformABI-aware Fidelity output.
New NTU dimensions come from Fidelity.Platform, not from binding generators. Farscape binds
C/C++ libraries for targets where C FFI exists (desktop, MCU, GPU host-side). Targets without
C FFI (FPGA fabric, NPU tiles) don't need Farscape — they use Fidelity.Platform + BAREWire.