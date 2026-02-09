# FNCS Integration

Farscape generates F# binding source files that feed into the Firefly compilation pipeline. The first consumer of this output is **FNCS (F# Native Compiler Services)**, a complete, standalone native type checker operating in the Native Type Universe (NTU).

## The Closed-Loop Pipeline

```mermaid
flowchart TD
    A["C Headers"] --> B["Farscape<br/>(generates F# source with<br/>[⟨FidelityExtern⟩] stubs)"]
    B --> C["FNCS<br/>(type-checks in NTU;<br/>BCL-free, freestanding)"]
    C --> D["PSG<br/>(Program Semantic Graph<br/>with native types +<br/>binding metadata attached)"]
    D --> E["Baker<br/>(saturates intrinsic operations,<br/>SRTP resolution)"]
    E --> F["Alex<br/>(MLIR emission with<br/>binding strategy attributes)"]
    F --> G["MLIR → LLVM → Native Binary"]
```

There is no P/Invoke in the Fidelity framework. Only `[<FidelityExtern>]`.

## What FNCS Is

FNCS is the native type checker for Firefly, a **complete compiler service** with:

- **Native Type Universe (NTU)**: NTUKind types (`NTUint`, `NTUuint`, `NTUptr<'T>`, `NTUsize`), BCL-free
- **SRTP resolution**: Statically resolved type parameters via native WitnessResolution
- **Union-Find constraint solving**: Type inference with occurs check
- **Expression checking**: Full SynExpr → SemanticGraph construction with types attached during construction
- **Intrinsic resolution**: Layer 1 operations (Sys.write, NativePtr.*, Array.*) that FNCS emits directly as MLIR operations

FNCS lives at `~/repos/fsnative/` and is registered as the `FNCS` Serena project.

## What Farscape Generates

### `[<FidelityExtern>]` Attributed Stubs (Core Infrastructure)

```fsharp
[<FidelityExtern("libc", "memcpy")>]
let memcpy (dest: nativeint) (src: nativeint) (n: nativeint) : nativeint =
    Unchecked.defaultof<nativeint>
```

FNCS recognizes the `[<FidelityExtern>]` attribute and carries library name + symbol through the PSG. Baker recognizes the `Unchecked.defaultof` pattern and elaborates it with intrinsic metadata. Alex emits MLIR with `fidelity.binding_strategy` and `fidelity.library_name` attributes. The linker auto-collects all referenced libraries and generates appropriate flags (`-lc`, `-lwayland-client`, etc.).

**Current state**: Stubs generate without the attribute; Alex infers from naming conventions. Adding `[<FidelityExtern>]` is core infrastructure that closes the pipeline loop.

## Sliced Package Architecture

Farscape generates categorized, source-based packages from C headers:

| Package | C Headers | Key Functions |
|---------|-----------|---------------|
| `Fidelity.libc.IO` | `<unistd.h>` | read, write, open, close, lseek, pipe |
| `Fidelity.libc.Memory` | `<string.h>` | memcpy, memset, strlen, strcpy |
| `Fidelity.libc.Alloc` | `<stdlib.h>` | malloc, free, calloc, realloc, abort, exit |

Reachability analysis in the pipeline means only functions actually called get MLIR emissions. Unused bindings cost zero in the final binary.

## Quotation Semantic Carriers (Core Infrastructure)

For embedded targets (CMSIS peripherals), Farscape generates quotation semantic carriers that carry memory layout and access constraint information through the pipeline:

```fsharp
let gpioQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "GPIO"
      Instances = Map.ofList [("GPIOA", 0x48000000un)]
      Layout = { Size = 0x400; Alignment = 4; Fields = gpioFields }
      MemoryRegion = Peripheral }
@>
```

FNCS nanopasses decompose these quotations to attach volatile semantics and access constraints to PSG nodes.

## CMSIS Access Constraints (Core Infrastructure)

Farscape maps `__I`/`__O`/`__IO` qualifiers to access constraints carried through the pipeline:

| CMSIS | C Definition | Pipeline Effect |
|-------|--------------|-----------------|
| `__I` | `volatile const` | FNCS marks read-only, Alex emits volatile load only |
| `__O` | `volatile` | FNCS marks write-only, Alex emits volatile store only |
| `__IO` | `volatile` | FNCS marks read-write, Alex emits volatile load/store |

These constraints are enforced at compile time through NTU's type system, FNCS's constraint checking, and Baker's intrinsic elaboration.

## Alex's Role

Alex works ONLY with MLIR. It does not work with F# source or P/Invoke. Alex receives binding metadata through MLIR attributes:

- `fidelity.binding_strategy`: static or dynamic
- `fidelity.library_name`: library identifier for linker flag collection

For the current focus (libc dynamic binding), Alex emits dynamic binding MLIR. Static binding (LLVM LTO cross-language inlining) is on the roadmap.

## Current State vs Target

| Aspect | Current | Target |
|--------|---------|--------|
| Output format | `Unchecked.defaultof` stubs | `[<FidelityExtern>]` attributed stubs |
| Library metadata | None: Alex infers from symbol names | Library name + symbol carried through PSG |
| CMSIS support | Structs/enums parsed, qualifiers not extracted | Full qualifier → access constraint mapping |
| Linker flags | Hard-coded | Auto-collected from `fidelity.library_name` attributes |
| Distribution | Manual file generation | Source-based packages via frgo.dev |

## Related Documents

| Document | Location |
|----------|----------|
| Architecture Overview | `./01_Architecture_Overview.md` |
| BAREWire Integration | `./02_BAREWire_Integration.md` |
| XParsec Architecture | `./04_XParsec_Architecture.md` |
| FNCS Architecture | `~/repos/fsnative/` (Serena memory: `fncs_architecture`) |
