# Farscape + BAREWire Integration Architecture

## Core Infrastructure, Not Optional

BAREWire integration is what makes Farscape part of the Fidelity framework rather than a standalone binding script. Reading header AST to capture precise memory/type layout is core to the memory safety guarantees that earn Fidelity its name.

Without BAREWire descriptors carrying mapped layout into the codebase, there is no memory safety story. There is no closed system.

**Primary unikernel target**: Renesas RA6M5 (ARM Cortex-M33).

## Design Principle: Invisible Memory Management

From "Memory Management By Choice": BAREWire provides an opt-in model where developers accept compiler-generated memory layouts for most code while taking explicit control only where it merits hand-curated optimization.

The developer writes clean Clef:
```fsharp
let toggleLed () =
    R_IOPORT_PinWrite(PORT0, PIN_LED, true)
```

The compiler (via tree-shaking/reachability) determines:
- Which peripherals are used
- Their memory-mapped addresses
- Register offsets and access constraints
- Volatile semantics

**The developer never sees BARELayout or offset calculations.**

## Three Artifact Types

For CMSIS/FSP and similar hardware targets, Farscape generates:

1. **Clef Types** - Struct definitions (`ioport_ctrl_t`, etc.)
2. **FidelityExtern Declarations** - Function bindings with `[<FidelityExtern>]` attributes
3. **Memory Descriptors** - BAREWire-compatible hardware memory catalog

## The Memory Descriptor Model

Farscape catalogs the entire hardware memory architecture:

```fsharp
type PeripheralDescriptor = {
    Name: string                          // "GPIO"
    Instances: Map<string, unativeint>    // PORT0 → 0x40040000, etc.
    Layout: PeripheralLayout
    MemoryRegion: MemoryRegionKind        // Peripheral, SRAM, Flash, System
}

and PeripheralLayout = {
    Size: int
    Alignment: int
    Fields: FieldDescriptor list
}

and FieldDescriptor = {
    Name: string
    Offset: int
    Type: RegisterType
    Access: AccessKind      // ReadOnly | WriteOnly | ReadWrite
    BitFields: BitFieldDescriptor list option
    Documentation: string option
}

and AccessKind =
    | ReadOnly   // __I - reads hardware state
    | WriteOnly  // __O - writes trigger hardware action, reads undefined
    | ReadWrite  // __IO - normal read/write

and MemoryRegionKind =
    | Flash           // Execute-in-place, read-only at runtime
    | SRAM            // Normal read/write memory
    | Peripheral      // Memory-mapped I/O, volatile, uncacheable
    | SystemControl   // ARM system peripherals (NVIC, etc.)
```

## CMSIS/FSP Qualifier Semantics

| Qualifier | Meaning | Code Gen Implication |
|-----------|---------|---------------------|
| `__I` (volatile const) | Read-only register | Writes are UB, emit read-only access |
| `__O` (volatile) | Write-only register | Reads return undefined, emit write-only |
| `__IO` (volatile) | Read-write register | Normal volatile access |

Writing to `PIDR` (port input data register) or reading from `POSR` (port output set register) is a hardware error. These constraints must be captured from headers and enforced at compile time.

## Renesas RA6M5 Memory Map

| Region | Address Range | Characteristics |
|--------|---------------|-----------------|
| Code Flash | `0x0000_0000` | Code + constants, read-only at runtime |
| SRAM | `0x2000_0000` | Stack, heap, .bss, .data |
| Peripherals | `0x4000_0000+` | Memory-mapped I/O, volatile, specific access widths |
| System | `0xE000_0000` | NVIC, SysTick, debug - ARM core peripherals |

## Dependency: Farscape → BAREWire

Farscape takes BAREWire as a dependency. The memory descriptor types live in BAREWire; Farscape populates them from parsed headers.

BAREWire development must advance in parallel to provide:
- `PeripheralDescriptor` and related types
- Memory region abstractions
- Hardware address mapping primitives

## Pipeline Flow

```
FSP/CMSIS Header (.h)
    ↓ Farscape parses (clang JSON AST + macros)
    ↓
Farscape Output:
    ├── Types.fs (Clef structs)
    ├── Bindings.fs (FidelityExtern declarations)
    └── Descriptors (BAREWire memory catalog)
    ↓
PSG (contains type defs + FidelityExtern markers + layout refs)
    ↓
Alex/Zipper traversal:
    ├── Peripheral access → MLIR volatile load/store
    ├── Layout info → correct offsets
    └── FSP functions → inline or linker symbol
    ↓
MLIR → LLVM → Native Binary
```

## Tree-Shaking Drives Inclusion

The FSP headers get tree-shaken to only what's used:

1. Reachability analysis identifies used peripherals/functions
2. Only referenced descriptors included in final artifact
3. Final binary is as tight as hand-written C

## Developer Experience

1. Add dependency in fidproj: `farscape-ra6m5 = { path = "..." }`
2. Write clean Clef using typed peripheral access
3. Compile - Composer handles all memory management

The infrastructure is invisible unless the developer explicitly opts into manual control.

## Current State

Reading headers to capture precise memory/type layout data is the foundational work. This includes:
- Struct field offsets and alignment
- CMSIS qualifier recognition (`__I`, `__O`, `__IO`)
- Base address extraction from macros
- Bit field extraction from `_Pos`/`_Msk` macros

This is core infrastructure under development, not an optional enhancement.
