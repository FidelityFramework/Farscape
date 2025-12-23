# Farscape Documentation

## Overview

Farscape is the C/C++ binding generator for the Fidelity native F# compilation ecosystem. It parses C/C++ headers and generates type-safe F# bindings along with BAREWire memory descriptors for hardware targets.

## Table of Contents

### Architecture

1. [Architecture Overview](./01_Architecture_Overview.md) - Pipeline structure and design principles
2. [BAREWire Integration](./02_BAREWire_Integration.md) - Hardware descriptor generation
3. [fsnative Integration](./03_fsnative_Integration.md) - Native type system coordination

### Reference

- [Type Mapping Reference](./Type_Mapping_Reference.md) - C to F# type mappings
- [CMSIS HAL Guide](./CMSIS_HAL_Guide.md) - STM32 and ARM peripheral bindings

## Position in Fidelity Ecosystem

Farscape sits at a critical junction in the Fidelity compilation pipeline:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    Farscape in Fidelity Ecosystem                       │
│                                                                         │
│  ┌─────────────┐                                                        │
│  │  C/C++      │  CMSIS headers, HAL libraries, vendor SDKs            │
│  │  Headers    │                                                        │
│  └──────┬──────┘                                                        │
│         │                                                               │
│         ▼                                                               │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                         Farscape                                 │   │
│  │  ┌───────────┐   ┌───────────┐   ┌───────────┐                  │   │
│  │  │ XParsec   │──▶│ TypeMapper│──▶│ CodeGen   │                  │   │
│  │  │ Parser    │   │           │   │           │                  │   │
│  │  └───────────┘   └───────────┘   └───────────┘                  │   │
│  │        │               │               │                         │   │
│  │        │         Uses fsnative   Uses BAREWire                   │   │
│  │        │         types           descriptors                     │   │
│  └────────┼───────────────┼───────────────┼─────────────────────────┘   │
│           │               │               │                             │
│           ▼               ▼               ▼                             │
│  ┌─────────────┐   ┌─────────────┐   ┌─────────────┐                   │
│  │  Types.fs   │   │ Bindings.fs │   │ Descriptors │                   │
│  │ (F# structs)│   │  (externs)  │   │ (BAREWire)  │                   │
│  └─────────────┘   └─────────────┘   └─────────────┘                   │
│         │                 │                 │                           │
│         └─────────────────┴─────────────────┘                           │
│                           │                                             │
│                           ▼                                             │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                      Firefly/Alex                                │   │
│  │  Consumes F# bindings and descriptors to generate native code   │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                           │                                             │
│                           ▼                                             │
│                    Native Binary                                        │
└─────────────────────────────────────────────────────────────────────────┘
```

## Dependencies

Farscape has two critical dependencies that must advance in lockstep:

### 1. fsnative (Type Provider)

fsnative provides the phantom type measures that make Farscape's output type-safe:

```fsharp
// fsnative provides
[<Measure>] type peripheral
[<Measure>] type readOnly
[<Measure>] type writeOnly
[<Measure>] type readWrite

type NativePtr<'T, [<Measure>] 'region, [<Measure>] 'access>

// Farscape generates using these types
type GPIO_TypeDef = {
    IDR: NativePtr<uint32, peripheral, readOnly>
    ODR: NativePtr<uint32, peripheral, readWrite>
    BSRR: NativePtr<uint32, peripheral, writeOnly>
}
```

**Without fsnative types**, Farscape can only generate untyped `nativeint` pointers.

### 2. BAREWire (Descriptor Types)

BAREWire provides the hardware descriptor types that Farscape populates:

```fsharp
// BAREWire provides
type PeripheralDescriptor = { ... }
type FieldDescriptor = { ... }
type AccessKind = ReadOnly | WriteOnly | ReadWrite
type MemoryRegionKind = Peripheral | SRAM | Flash | ...

// Farscape populates these from parsed headers
let gpioDescriptor = {
    Name = "GPIO"
    Instances = Map.ofList ["GPIOA", 0x48000000un; ...]
    Layout = { Fields = [...] }
    MemoryRegion = Peripheral
}
```

**Without BAREWire types**, Farscape cannot generate the memory catalog that Alex needs.

## The Interlock Requirement

The dependency chain must be maintained:

```
FSharp.UMX ──absorption──▶ fsnative ──types──▶ Farscape ──uses──▶ BAREWire
```

See [Memory Interlock Requirements](https://github.com/speakeztech/firefly/docs/Memory_Interlock_Requirements.md) for full details.

## Output Modes

Farscape supports three output modes:

### 1. F# Bindings (`--output-mode bindings`)

Standard F# type definitions and extern declarations:

```fsharp
[<Struct; StructLayout(LayoutKind.Sequential)>]
type GPIO_InitTypeDef = {
    Pin: uint32
    Mode: uint32
    Pull: uint32
    Speed: uint32
}

module Platform.Bindings =
    let halGpioInit gpio init : unit = Unchecked.defaultof<unit>
```

### 2. BAREWire Descriptors (`--output-mode descriptors`)

Hardware memory catalog for Alex:

```fsharp
let gpioDescriptor: PeripheralDescriptor = {
    Name = "GPIO"
    Instances = Map.ofList [
        "GPIOA", 0x48000000un
        "GPIOB", 0x48000400un
    ]
    Layout = {
        Fields = [
            { Name = "MODER"; Offset = 0x00; Type = U32; Access = ReadWrite }
            { Name = "IDR"; Offset = 0x10; Type = U32; Access = ReadOnly }
            { Name = "BSRR"; Offset = 0x18; Type = U32; Access = WriteOnly }
        ]
    }
    MemoryRegion = Peripheral
}
```

### 3. Both (`--output-mode both`)

Complete output for Fidelity integration.

## CMSIS Qualifier Handling

Farscape extracts access constraints from CMSIS `__I`, `__O`, `__IO` qualifiers:

| CMSIS | C Definition | Generated AccessKind |
|-------|--------------|---------------------|
| `__I` | `volatile const` | `ReadOnly` |
| `__O` | `volatile` | `WriteOnly` |
| `__IO` | `volatile` | `ReadWrite` |

These map to fsnative measures:
- `__I` → `readOnly` measure
- `__O` → `writeOnly` measure
- `__IO` → `readWrite` measure

## Related Documentation

| Document | Location |
|----------|----------|
| BAREWire Hardware Descriptors | `~/repos/BAREWire/docs/08 Hardware Descriptors.md` |
| fsnative Specification | `~/repos/fsnative-spec/docs/fidelity/FNCS_Specification.md` |
| UMX Integration Plan | `~/repos/FSharp.UMX/docs/fidelity/UMX_Integration_Plan.md` |
| Memory Interlock Requirements | `~/repos/Firefly/docs/Memory_Interlock_Requirements.md` |
| Staged Memory Model | `~/repos/Firefly/docs/Staged_Memory_Model.md` |

## Development Status

Current implementation status:

- [x] XParsec-based C header parsing (basic)
- [x] Basic type mapping (primitives, pointers, structs)
- [x] F# binding generation
- [ ] CppParser wired to XParsec (currently hardcoded to cJSON.h)
- [ ] Macro/constant extraction (#define values)
- [ ] CMSIS qualifier extraction (__I, __O, __IO)
- [ ] BAREWire descriptor generation (awaiting BAREWire types)
- [ ] fsnative type integration (awaiting fsnative maturation)
