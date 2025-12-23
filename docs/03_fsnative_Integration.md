# fsnative Integration

Farscape generates F# bindings that use fsnative's phantom type measures for type-safe peripheral access. This document describes how Farscape uses fsnative types and the dependency relationship.

## Status

> **Implementation Status: PLANNED**
>
> fsnative phantom type measures for memory regions and access constraints are in development. Full integration requires UMX absorption into fsnative.

## Dependency Chain

```
FSharp.UMX ──absorption──▶ fsnative ──provides types to──▶ Farscape
```

Farscape depends on fsnative providing:

| fsnative Type | Purpose | Status |
|---------------|---------|--------|
| `NativePtr<'T, 'region, 'access>` | Type-safe pointer with measures | In development |
| `[<Measure>] type peripheral` | Memory-mapped I/O region | Planned |
| `[<Measure>] type sram` | Normal RAM region | Planned |
| `[<Measure>] type flash` | Read-only storage region | Planned |
| `[<Measure>] type readOnly` | Read-only access | Planned |
| `[<Measure>] type writeOnly` | Write-only access | Planned |
| `[<Measure>] type readWrite` | Read-write access | Planned |

## The UMX Foundation

FSharp.UMX provides the `[<MeasureAnnotatedAbbreviation>]` attribute that allows measures on non-numeric types:

```fsharp
// FSharp.UMX pattern
[<MeasureAnnotatedAbbreviation>]
type string<[<Measure>] 'u> = string

// fsnative will provide
[<MeasureAnnotatedAbbreviation>]
type NativePtr<'T, [<Measure>] 'region, [<Measure>] 'access> = nativeptr<'T>
```

Without UMX absorption, Farscape can only generate untyped `nativeint` or `nativeptr<'T>` without region/access safety.

## Type Generation

### From C to F# Types

Farscape maps C types with CMSIS qualifiers to fsnative types:

| C Declaration | CMSIS Meaning | Generated F# Type |
|---------------|---------------|-------------------|
| `__IO uint32_t ODR` | Read-write volatile | `NativePtr<uint32, peripheral, readWrite>` |
| `__I uint32_t IDR` | Read-only volatile | `NativePtr<uint32, peripheral, readOnly>` |
| `__O uint32_t BSRR` | Write-only volatile | `NativePtr<uint32, peripheral, writeOnly>` |
| `uint32_t data` | Non-volatile | `uint32` (value type) |

### Struct Generation

For CMSIS struct definitions:

```c
typedef struct {
    __IO uint32_t MODER;
    __IO uint32_t OTYPER;
    __I  uint32_t IDR;
    __IO uint32_t ODR;
    __O  uint32_t BSRR;
} GPIO_TypeDef;
```

Farscape generates:

```fsharp
namespace CMSIS.STM32L5.GPIO

open Alloy
open fsnative.Measures

[<Struct; StructLayout(LayoutKind.Sequential)>]
type GPIO_TypeDef = {
    MODER:  NativePtr<uint32, peripheral, readWrite>
    OTYPER: NativePtr<uint32, peripheral, readWrite>
    IDR:    NativePtr<uint32, peripheral, readOnly>
    ODR:    NativePtr<uint32, peripheral, readWrite>
    BSRR:   NativePtr<uint32, peripheral, writeOnly>
}
```

### Base Address Constants

Peripheral instance addresses:

```c
#define GPIOA_BASE 0x48000000UL
#define GPIOA ((GPIO_TypeDef *) GPIOA_BASE)
```

Generates:

```fsharp
/// GPIOA base address
let GPIOA_BASE: unativeint = 0x48000000un

/// GPIOA peripheral instance
let GPIOA: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOA_BASE
```

## Compile-Time Safety

The phantom type measures provide compile-time enforcement of access constraints.

### Read-Only Violation

```fsharp
// Attempt to write to read-only register
let gpio = GPIOA
gpio.IDR <- 0x20u  // Compile error!

// Error FS8002: Cannot write to read-only pointer 'IDR'
// The field 'IDR' has access constraint 'readOnly' which does not permit writes.
```

### Write-Only Violation

```fsharp
// Attempt to read from write-only register
let gpio = GPIOA
let value = gpio.BSRR  // Compile error!

// Error FS8001: Cannot read from write-only pointer 'BSRR'
// The field 'BSRR' has access constraint 'writeOnly' which does not permit reads.
```

### Region Mismatch

```fsharp
// Attempt to assign peripheral pointer to SRAM pointer
let gpioPtr: NativePtr<GPIO_TypeDef, peripheral, readWrite> = GPIOA
let sramPtr: NativePtr<GPIO_TypeDef, sram, readWrite> = gpioPtr  // Compile error!

// Error FS8003: Memory region mismatch
// Cannot convert from 'peripheral' to 'sram' memory region.
```

## Generated Module Structure

Farscape generates a complete module hierarchy:

```
CMSIS/
└── STM32L5/
    ├── GPIO/
    │   ├── Types.fs        ← Type definitions
    │   ├── Bindings.fs     ← HAL function bindings
    │   └── Descriptors.fs  ← BAREWire descriptors
    ├── USART/
    │   ├── Types.fs
    │   ├── Bindings.fs
    │   └── Descriptors.fs
    └── RCC/
        ├── Types.fs
        ├── Bindings.fs
        └── Descriptors.fs
```

### Types.fs (Full Example)

```fsharp
namespace CMSIS.STM32L5.GPIO

open Alloy
open fsnative.Measures
open System.Runtime.InteropServices

// ============================================================================
// GPIO Register Types
// ============================================================================

/// GPIO port mode register bits
[<Struct>]
type GPIO_Mode =
    | Input   = 0b00u
    | Output  = 0b01u
    | AltFunc = 0b10u
    | Analog  = 0b11u

/// GPIO output type
[<Struct>]
type GPIO_OutputType =
    | PushPull  = 0u
    | OpenDrain = 1u

/// GPIO output speed
[<Struct>]
type GPIO_Speed =
    | Low      = 0b00u
    | Medium   = 0b01u
    | High     = 0b10u
    | VeryHigh = 0b11u

/// GPIO pull-up/pull-down
[<Struct>]
type GPIO_Pull =
    | None     = 0b00u
    | PullUp   = 0b01u
    | PullDown = 0b10u

/// GPIO pin state
[<Struct>]
type GPIO_PinState =
    | Reset = 0u
    | Set   = 1u

// ============================================================================
// GPIO Peripheral Structure
// ============================================================================

/// GPIO peripheral register structure
/// Matches CMSIS GPIO_TypeDef layout exactly
[<Struct; StructLayout(LayoutKind.Sequential)>]
type GPIO_TypeDef = {
    /// Port mode register
    MODER:   NativePtr<uint32, peripheral, readWrite>

    /// Output type register
    OTYPER:  NativePtr<uint32, peripheral, readWrite>

    /// Output speed register
    OSPEEDR: NativePtr<uint32, peripheral, readWrite>

    /// Pull-up/pull-down register
    PUPDR:   NativePtr<uint32, peripheral, readWrite>

    /// Input data register (read-only)
    IDR:     NativePtr<uint32, peripheral, readOnly>

    /// Output data register
    ODR:     NativePtr<uint32, peripheral, readWrite>

    /// Bit set/reset register (write-only)
    BSRR:    NativePtr<uint32, peripheral, writeOnly>

    /// Configuration lock register
    LCKR:    NativePtr<uint32, peripheral, readWrite>

    /// Alternate function low register (pins 0-7)
    AFRL:    NativePtr<uint32, peripheral, readWrite>

    /// Alternate function high register (pins 8-15)
    AFRH:    NativePtr<uint32, peripheral, readWrite>

    /// Bit reset register (write-only)
    BRR:     NativePtr<uint32, peripheral, writeOnly>
}

/// GPIO initialization structure
[<Struct>]
type GPIO_InitTypeDef = {
    Pin:       uint32
    Mode:      GPIO_Mode
    Pull:      GPIO_Pull
    Speed:     GPIO_Speed
    Alternate: uint32
}

// ============================================================================
// Peripheral Instances
// ============================================================================

/// GPIOA base address
let GPIOA_BASE: unativeint = 0x48000000un

/// GPIOB base address
let GPIOB_BASE: unativeint = 0x48000400un

/// GPIOC base address
let GPIOC_BASE: unativeint = 0x48000800un

/// GPIOD base address
let GPIOD_BASE: unativeint = 0x48000C00un

/// GPIOE base address
let GPIOE_BASE: unativeint = 0x48001000un

/// GPIOF base address
let GPIOF_BASE: unativeint = 0x48001400un

/// GPIOG base address
let GPIOG_BASE: unativeint = 0x48001800un

/// GPIOH base address
let GPIOH_BASE: unativeint = 0x48001C00un

/// GPIOA peripheral instance
let GPIOA: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOA_BASE

/// GPIOB peripheral instance
let GPIOB: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOB_BASE

/// GPIOC peripheral instance
let GPIOC: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOC_BASE

/// GPIOD peripheral instance
let GPIOD: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOD_BASE

/// GPIOE peripheral instance
let GPIOE: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOE_BASE

/// GPIOF peripheral instance
let GPIOF: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOF_BASE

/// GPIOG peripheral instance
let GPIOG: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOG_BASE

/// GPIOH peripheral instance
let GPIOH: NativePtr<GPIO_TypeDef, peripheral, readWrite> =
    NativePtr.ofAddress GPIOH_BASE

// ============================================================================
// Pin Definitions
// ============================================================================

let GPIO_PIN_0:  uint32 = 0x0001u
let GPIO_PIN_1:  uint32 = 0x0002u
let GPIO_PIN_2:  uint32 = 0x0004u
let GPIO_PIN_3:  uint32 = 0x0008u
let GPIO_PIN_4:  uint32 = 0x0010u
let GPIO_PIN_5:  uint32 = 0x0020u
let GPIO_PIN_6:  uint32 = 0x0040u
let GPIO_PIN_7:  uint32 = 0x0080u
let GPIO_PIN_8:  uint32 = 0x0100u
let GPIO_PIN_9:  uint32 = 0x0200u
let GPIO_PIN_10: uint32 = 0x0400u
let GPIO_PIN_11: uint32 = 0x0800u
let GPIO_PIN_12: uint32 = 0x1000u
let GPIO_PIN_13: uint32 = 0x2000u
let GPIO_PIN_14: uint32 = 0x4000u
let GPIO_PIN_15: uint32 = 0x8000u
let GPIO_PIN_All: uint32 = 0xFFFFu
```

### Bindings.fs (Full Example)

```fsharp
namespace CMSIS.STM32L5.GPIO

open Alloy
open fsnative.Measures

/// HAL GPIO function bindings
/// These are Platform.Bindings declarations that Alex recognizes
module Platform.Bindings =

    /// Initialize GPIO peripheral
    let halGpioInit
        (gpio: NativePtr<GPIO_TypeDef, peripheral, readWrite>)
        (init: GPIO_InitTypeDef)
        : unit =
        Unchecked.defaultof<unit>

    /// De-initialize GPIO peripheral
    let halGpioDeInit
        (gpio: NativePtr<GPIO_TypeDef, peripheral, readWrite>)
        (pin: uint32)
        : unit =
        Unchecked.defaultof<unit>

    /// Read input pin state
    let halGpioReadPin
        (gpio: NativePtr<GPIO_TypeDef, peripheral, readWrite>)
        (pin: uint32)
        : GPIO_PinState =
        Unchecked.defaultof<GPIO_PinState>

    /// Write output pin state
    let halGpioWritePin
        (gpio: NativePtr<GPIO_TypeDef, peripheral, readWrite>)
        (pin: uint32)
        (state: GPIO_PinState)
        : unit =
        Unchecked.defaultof<unit>

    /// Toggle output pin
    let halGpioTogglePin
        (gpio: NativePtr<GPIO_TypeDef, peripheral, readWrite>)
        (pin: uint32)
        : unit =
        Unchecked.defaultof<unit>

    /// Lock GPIO configuration
    let halGpioLockPin
        (gpio: NativePtr<GPIO_TypeDef, peripheral, readWrite>)
        (pin: uint32)
        : int =  // HAL_StatusTypeDef
        Unchecked.defaultof<int>
```

## Fallback Mode

When fsnative types are not yet available, Farscape generates degraded output:

```fsharp
// Fallback: No phantom type measures
[<Struct; StructLayout(LayoutKind.Sequential)>]
type GPIO_TypeDef = {
    MODER:   nativeptr<uint32>  // No region/access info
    OTYPER:  nativeptr<uint32>
    IDR:     nativeptr<uint32>  // Should be read-only!
    ODR:     nativeptr<uint32>
    BSRR:    nativeptr<uint32>  // Should be write-only!
}
```

This compiles and works but provides NO compile-time safety for:
- Access constraint violations
- Memory region mismatches
- Volatile semantics

The `--require-fsnative` flag (future) will make fsnative types mandatory and fail if unavailable.

## Implementation Roadmap

### Phase 1: Basic Type Generation (Current)

- Map C primitives to F# primitives
- Generate struct layouts with `nativeptr<'T>`
- No phantom types yet

### Phase 2: fsnative Integration (After UMX Absorption)

- Reference fsnative types
- Generate `NativePtr<'T, 'region, 'access>`
- Map CMSIS qualifiers to access measures

### Phase 3: Full Safety (After fsnative Maturation)

- Compile-time access constraint checking
- Region mismatch detection
- Integration with Alex for volatile MLIR generation

## Coordination with fsnative Development

Farscape and fsnative must develop in lockstep:

| fsnative Milestone | Farscape Response |
|-------------------|-------------------|
| UMX absorption complete | Reference `[<MeasureAnnotatedAbbreviation>]` |
| Memory measures defined | Generate `NativePtr<'T, peripheral, _>` |
| Access measures defined | Generate `NativePtr<'T, _, readOnly>` etc. |
| Error codes defined | Document FS8001-FS8003 constraints |
| NativePtr.fs complete | Full type-safe output |

Communication channel: The `~/repos/Firefly/docs/Memory_Interlock_Requirements.md` document tracks this dependency chain.

## Related Documents

| Document | Location |
|----------|----------|
| Memory Interlock Requirements | `~/repos/Firefly/docs/Memory_Interlock_Requirements.md` |
| BAREWire Integration | `./02_BAREWire_Integration.md` |
| fsnative Specification | `~/repos/fsnative-spec/docs/fidelity/FNCS_Specification.md` |
| UMX Integration Plan | `~/repos/FSharp.UMX/docs/fidelity/UMX_Integration_Plan.md` |
