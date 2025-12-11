# Farscape's Role in Native Library Binding

## Position in the Architecture

Farscape is the **binding generator** - it transforms C/C++ headers into F# source code:

```
C Headers → Farscape → F# Source (Fidelity.[Target] + BAREWire.[Target] + Externs)
```

Farscape runs at **generation time**, before Firefly compilation.

## Farscape's Three Outputs

### 1. Fidelity.[Target] - High-Level F# API
Developer-facing library with idiomatic F# types:

```fsharp
// Fidelity.STM32L5/GPIO.fs
module Fidelity.STM32L5.GPIO

type Port = GPIOA | GPIOB | GPIOC | ...
type Mode = Input | Output | Alternate | Analog

let init (port: Port) (pin: int) (mode: Mode) : Result<unit, GpioError> = ...
let inline writePin (port: Port) (pin: int) (state: bool) : unit = ...
```

### 2. BAREWire.[Target] - Memory Descriptors
Compile-time hardware memory map using BAREWire.Core types:

```fsharp
// BAREWire.STM32L5/Descriptors.fs
let GPIO : PeripheralDescriptor = {
    Name = "GPIO"
    Region = MemoryRegionKind.Peripheral
    Instances = Map.ofList [("GPIOA", 0x48000000un); ...]
    Registers = [
        { Name = "MODER"; Offset = 0x00; Width = 32; Access = ReadWrite; ... }
        { Name = "IDR"; Offset = 0x10; Width = 32; Access = ReadOnly; ... }
        { Name = "BSRR"; Offset = 0x18; Width = 32; Access = WriteOnly; ... }
    ]
}
```

### 3. Extern Declarations - Library Function Bindings
F# externs for pre-compiled C library functions:

```fsharp
// Fidelity.STM32L5/HAL.fs
[<DllImport("stm32l5xx_hal", CallingConvention = CallingConvention.Cdecl)>]
extern HAL_StatusTypeDef HAL_GPIO_Init(nativeint GPIOx, nativeint GPIO_Init)

[<DllImport("stm32l5xx_hal", CallingConvention = CallingConvention.Cdecl)>]
extern HAL_StatusTypeDef HAL_UART_Transmit(nativeint huart, nativeint pData, uint16 Size, uint32 Timeout)
```

## What Farscape Parses

From C headers, Farscape extracts:

| C Construct | F# Output |
|-------------|-----------|
| `typedef struct {...} XXX_TypeDef` | PeripheralDescriptor + F# record |
| `__IO uint32_t FIELD` | Register with Access = ReadWrite |
| `__I uint32_t FIELD` | Register with Access = ReadOnly |
| `__O uint32_t FIELD` | Register with Access = WriteOnly |
| `#define XXX_BASE (addr)` | Instance address in Instances map |
| `#define XXX_Pos (n)` | BitField position |
| `#define XXX_Msk (m)` | BitField width (computed from mask) |
| `typedef enum {...}` | F# discriminated union |
| `RetType FuncName(params)` | DllImport extern declaration |

## Key CMSIS Patterns

```c
// Access qualifiers → AccessKind
#define __IO volatile           // ReadWrite
#define __I  volatile const     // ReadOnly  
#define __O  volatile           // WriteOnly

// Bit field macros → BitFieldDescriptor
#define USART_CR1_UE_Pos  (0U)
#define USART_CR1_UE_Msk  (0x1UL << USART_CR1_UE_Pos)

// Peripheral instance → address in Instances map
#define GPIOA_BASE  (0x48000000UL)
#define GPIOA       ((GPIO_TypeDef *) GPIOA_BASE)
```

## Link-Time Consideration

The extern declarations reference a **library name** (e.g., `"stm32l5xx_hal"`).

At link time, the linker must find `libstm32l5xx_hal.a` containing:
- `HAL_GPIO_Init`
- `HAL_UART_Transmit`
- etc.

Farscape doesn't produce this library - it's pre-compiled by the vendor (STMicroelectronics). Farscape only produces the F# bindings that reference it.

## What Farscape Does NOT Do

- Generate MLIR or LLVM code
- Know about Alex's internal patterns
- Make platform-specific code generation decisions
- Compile the HAL library itself

## Relationship to Other Projects

| Project | Relationship |
|---------|--------------|
| **BAREWire** | Farscape uses BAREWire.Core types; generates BAREWire.[Target] |
| **Firefly** | Firefly compiles Farscape's output; Alex handles externs |
| **Alloy** | Generated Fidelity.[Target] may use Alloy types |

## Canonical Document

See Firefly `/docs/Native_Library_Binding_Architecture.md` for the complete binding architecture.
