# Farscape + BAREWire Integration Architecture

> **Architecture Update (December 2024)**: Farscape now generates **quotation-based output** with active patterns.
> See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md` for the unified architecture.

## Quotation-Based Output

Farscape generates three artifact types using F# quotations and active patterns:

1. **Expr<PeripheralDescriptor>** - Quotations encoding hardware memory layout
2. **Active Patterns** - PSG recognition patterns like `(|GpioWritePin|_|)`
3. **MemoryModel Record** - Integration surface for fsnative nanopass pipeline

```fsharp
// Generated quotation for GPIO peripheral
let gpioPeripheralQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "GPIO"
      Instances = Map.ofList [("GPIOA", 0x48000000un); ("GPIOB", 0x48000400un)]
      Layout = { Size = 0x400; Alignment = 4; Fields = gpioFields }
      MemoryRegion = Peripheral }
@>

// Active pattern for PSG recognition
let (|GpioWritePin|_|) (node: PSGNode) : (string * int * uint32) option = ...

// MemoryModel for fsnative integration
let stm32l5MemoryModel: MemoryModel = {
    TargetFamily = "STM32L5"
    PeripheralDescriptors = [gpioPeripheralQuotation; usartPeripheralQuotation]
    RegisterConstraints = [gpioConstraints]
    Regions = regionQuotation
    Recognize = recognizeMemoryOperation
    CacheTopology = None
    CoherencyModel = None
}
```

## Core Design Principle: Invisible Memory Management

Farscape is NOT just a C/C++ binding generator. For hardware targets like CMSIS/STM32, it builds a **complete model of the hardware memory system** that enables the Fidelity compiler to manage memory on behalf of the developer.

From "Memory Management By Choice":
> "BAREWire takes a fundamentally different approach. Rather than demanding constant attention to memory, it provides an opt-in model where developers can accept compiler-generated memory layouts for most code"

The developer writes clean F#:
```fsharp
let toggleLed () =
    HAL_GPIO_TogglePin(GPIOA, GPIO_PIN_5)
```

The compiler (via tree-shaking/reachability) determines:
- Which peripherals are used
- Their memory-mapped addresses
- Register offsets and access constraints
- Volatile semantics

**The developer never sees BARELayout or offset calculations.**

## Farscape Output: Three Artifacts

For CMSIS and similar hardware targets, Farscape generates:

1. **F# Types** - Struct definitions (`GPIO_TypeDef`, etc.)
2. **Extern Declarations** - Function bindings (`HAL_GPIO_Init`, etc.)
3. **Memory Descriptors** - BAREWire-compatible hardware memory catalog

## The Memory Descriptor Model

Farscape must didactically catalog the entire hardware memory architecture:

```fsharp
type PeripheralDescriptor = {
    Name: string                          // "GPIO"
    Instances: Map<string, unativeint>    // GPIOA → 0x48000000, etc.
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
    BitFields: BitFieldDescriptor list option  // For _Pos/_Msk macros
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

## CMSIS Qualifier Semantics

The `__I`, `__O`, `__IO` qualifiers encode hardware constraints:

| Qualifier | Meaning | Code Gen Implication |
|-----------|---------|---------------------|
| `__I` (volatile const) | Read-only register | Writes are UB, emit read-only access |
| `__O` (volatile) | Write-only register | Reads return undefined, emit write-only |
| `__IO` (volatile) | Read-write register | Normal volatile access |

Writing to `IDR` (input register) or reading from `BSRR` (bit set/reset) is a hardware error.

## Microcontroller Memory Map Reality

| Region | Address Range | Characteristics |
|--------|---------------|-----------------|
| Flash | `0x0800_0000` | Code + constants, read-only at runtime |
| SRAM | `0x2000_0000` | Stack, heap, .bss, .data |
| Peripherals | `0x4000_0000+` | Memory-mapped I/O, volatile, specific access widths |
| System | `0xE000_0000` | NVIC, SysTick, debug - ARM core peripherals |

## Dependency: Farscape → BAREWire

Farscape should take BAREWire as a dependency. The memory descriptor types live in BAREWire; Farscape populates them from parsed headers.

This means BAREWire development may need to advance in parallel with Farscape to provide:
- `PeripheralDescriptor` and related types
- Memory region abstractions
- Hardware address mapping primitives

## Pipeline Flow

```
CMSIS Header (.h)
    ↓ Farscape parses (clang JSON AST + macros)
    ↓
Farscape Output:
    ├── Types.fs (F# structs)
    ├── Bindings.fs (extern declarations)  
    └── Descriptors (BAREWire memory catalog)
    ↓
PSG (contains type defs + extern markers + layout refs)
    ↓
Alex/Zipper traversal:
    ├── Peripheral access → MLIR volatile load/store
    ├── Layout info → correct offsets
    └── HAL functions → inline or linker symbol
    ↓
MLIR → LLVM → Native Binary
```

## Tree-Shaking Drives Inclusion

The massive CMSIS headers (20K+ lines, 15K+ macros) get tree-shaken to only what's used:

1. Reachability analysis identifies used peripherals/functions
2. Only referenced descriptors included in final artifact
3. Final binary is as tight as hand-written C

## Developer Experience

1. Add dependency in fidproj: `farscape-stm32l5 = { path = "..." }`
2. Write clean F# using typed peripheral access
3. Compile - Firefly handles all memory management

The infrastructure is invisible unless the developer explicitly opts into manual control.
