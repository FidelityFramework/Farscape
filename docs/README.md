# Farscape Documentation

## Overview

Farscape is the C header binding generator for the Fidelity native F# compilation ecosystem. It parses C headers using clang and generates type-safe F# bindings with `[<FidelityExtern>]` attributes, with XParsec parser combinators handling C type decomposition and macro classification.

There is no P/Invoke in the Fidelity framework. Farscape generates `[<FidelityExtern>]` stubs for the native pipeline (FNCS, Baker, Alex, MLIR, LLVM). A separate P/Invoke mode exists for traditional .NET F# interop only.

## Table of Contents

### Architecture

1. [Architecture Overview](./01_Architecture_Overview.md): Pipeline structure, four architectural patterns, module roles
2. [BAREWire Integration](./02_BAREWire_Integration.md): Hardware memory/type layout capture from headers
3. [FNCS Integration](./03_fsnative_Integration.md): How Farscape output feeds the FNCS compilation pipeline
4. [XParsec Architecture](./04_XParsec_Architecture.md): Parser combinators, active patterns, catamorphisms, typed code AST
5. [Wrapper Generation](./05_Wrapper_Generation.md): Layer 2 idiomatic F# wrapper generation

## Position in the Fidelity Closed-Loop Pipeline

```mermaid
flowchart TD
    A["C Headers<br/>libc, CMSIS HAL, vendor SDKs"] --> B

    subgraph B["Farscape"]
        B1["Clang Parser"] --> B2["XParsec +<br/>Active Patterns"] --> B3["Catamorphism + AST<br/>→ CodeRenderer"]
    end

    B --> C["Fidelity Mode<br/>[⟨FidelityExtern⟩] stubs"]
    B --> D["P/Invoke Mode<br/>DllImport attrs<br/>(traditional .NET only)"]

    C --> E["FNCS → PSG → Baker → Alex → MLIR"]
    E --> G["LLVM → Native Binary"]
```

Farscape is one component of a closed-loop native compilation system. `[<FidelityExtern>]` carries library name + symbol through the PSG so Alex can emit MLIR with binding metadata, and the linker auto-collects library flags.

## Two-Layer Binding Model

**Layer 1: Platform.Bindings** - `[<FidelityExtern>]` attributed stubs with `Unchecked.defaultof<T>` bodies. Core infrastructure.

**Layer 2: Idiomatic F# Wrappers** (implemented) - Safe functional APIs with Result types, null checking, error handling. Driven by 12 clang attribute types and 7 return semantic patterns.

## Core Infrastructure Under Development

These are not optional features. They are what makes Farscape part of Fidelity:

1. **`[<FidelityExtern>]` attributes**: Library name + symbol metadata that closes the pipeline loop
2. **BAREWire memory/type layout capture**: Precise struct layout, access constraints, memory regions from header AST
3. **CMSIS qualifier mapping**: `__I` → ReadOnly, `__O` → WriteOnly, `__IO` → ReadWrite

## Dependencies

### FNCS (F# Native Compiler Services)

FNCS is the native type checker that processes Farscape's F# source. It operates in the Native Type Universe (NTU), a BCL-free, freestanding type system with NTUKind types, SRTP resolution, and union-find constraint solving.

### BAREWire (Memory Descriptor Types)

BAREWire provides hardware descriptor types (`PeripheralDescriptor`, `FieldDescriptor`, `AccessKind`) that Farscape populates from C headers. BAREWire development advances in parallel.

## Output Modes

| Mode | CLI Flag | Target |
|------|----------|--------|
| `fidelity` | `--output-mode fidelity` | F# Native / Fidelity pipeline (FidelityExtern) |
| `fidelity-wrappers` | `--output-mode fidelity-wrappers` | F# Native with Layer 2 wrappers |
| `pinvoke` | `--output-mode pinvoke` | Traditional .NET F# (DllImport, NOT Fidelity) |

## What's Implemented

- Clang two-pass C header parsing (JSON AST + macro extraction)
- XParsec post-processing for C type strings and macro values
- Active pattern decomposition (type classification, macro filtering)
- Catamorphism-based declaration traversal
- Typed code AST (FsDecl/FsType) with single CodeRenderer
- Fidelity binding generation (`Unchecked.defaultof` pattern)
- Layer 2 wrapper generation (WrapperPatternAnalyzer + WrapperCodeGenerator)
- P/Invoke binding generation (DllImport) for traditional .NET
- Typedef chain resolution, macro constant extraction
- Moya namespace analysis and TOML project files
- 89 unit tests covering all architectural patterns

## Roadmap

- `[<FidelityExtern>]` attribute generation
- BAREWire peripheral descriptor generation from header AST
- CMSIS qualifier extraction (`__I`, `__O`, `__IO` → `AccessKind`)
- Static binding support (LLVM LTO cross-language inlining)
- C++ support via Plugify ABI intelligence

## Related Documentation

| Document | Location |
|----------|----------|
| BAREWire Hardware Descriptors | `~/repos/BAREWire/docs/08 Hardware Descriptors.md` |
| FNCS Architecture | `~/repos/fsnative/` (Serena project: FNCS) |
| Memory Interlock Requirements | `~/repos/Firefly/docs/Memory_Interlock_Requirements.md` |
