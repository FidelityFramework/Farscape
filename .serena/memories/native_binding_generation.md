# Farscape's Role in Native Library Binding

> **Architecture Update (December 2025)**: Farscape now generates **quotation-based output** with active patterns,
> not just raw P/Invoke declarations. See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md`.
>
> **Target Update (January 2025)**: Primary unikernel target changed from STM32L5 to **Renesas RA6M5** (ARM Cortex-M33).

## Position in the Architecture

Farscape is the **binding generator** - it transforms C/C++ headers into F# source code with quotations:

```
C Headers → Farscape → F# Source (Fidelity.[Target] + BAREWire.[Target] + Externs)
```

Farscape runs at **generation time**, before Firefly compilation.

## Farscape's Current Output: Fidelity Bindings

> **STATUS (February 2026)**: Farscape currently generates `Platform.Bindings` pattern files with `Unchecked.defaultof`.
> Alex provides platform-specific MLIR implementations. Quotation-based output is PLANNED but not yet implemented.
> The generation pipeline uses XParsec, catamorphisms, active patterns, and a typed code AST (CodeRenderer).

## Aspirational: Quotation-Based Outputs (NOT YET IMPLEMENTED)

### 1. Expr<PeripheralDescriptor> Quotations
Memory layout information as quotations for nanopass consumption:

```fsharp
let gpioQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "GPIO"
      Instances = Map.ofList [("GPIOA", 0x40040000un)]
      Layout = { Size = 0x400; Alignment = 4; Fields = gpioFields }
      MemoryRegion = Peripheral }
@>
```

### 2. Active Patterns for Recognition
PSG pattern matching for Alex consumption:

```fsharp
let (|GpioWritePin|_|) (node: PSGNode) : (string * int * uint32) option = ...
let (|PeripheralAccess|_|) (node: PSGNode) : PeripheralAccessInfo option = ...
```

### 3. MemoryModel Record
Integration surface using pure F# record (not interface):

```fsharp
let ra6m5MemoryModel: MemoryModel = {
    TargetFamily = "RA6M5"
    PeripheralDescriptors = [gpioQuotation; sciQuotation]
    RegisterConstraints = [accessConstraints]
    Regions = regionQuotation
    Recognize = recognizeMemoryOperation
    CacheTopology = None
    CoherencyModel = None
}
```

### 4. Fidelity.[Target] - High-Level F# API
Developer-facing library with idiomatic F# types:

```fsharp
// Fidelity.RA6M5/GPIO.fs
module Fidelity.RA6M5.GPIO

type Port = Port0 | Port1 | Port2 | ...
type Mode = Input | Output | Alternate | Analog

let init (port: Port) (pin: int) (mode: Mode) : Result<unit, GpioError> = ...
let inline writePin (port: Port) (pin: int) (state: bool) : unit = ...
```

### 2. BAREWire.[Target] - Memory Descriptors
Compile-time hardware memory map using BAREWire.Core types:

```fsharp
// BAREWire.RA6M5/Descriptors.fs
let GPIO : PeripheralDescriptor = {
    Name = "GPIO"
    Region = MemoryRegionKind.Peripheral
    Instances = Map.ofList [("PORT0", 0x40040000un); ...]
    Registers = [
        { Name = "PODR"; Offset = 0x00; Width = 16; Access = ReadWrite; ... }
        { Name = "PIDR"; Offset = 0x02; Width = 16; Access = ReadOnly; ... }
        { Name = "POSR"; Offset = 0x06; Width = 16; Access = WriteOnly; ... }
    ]
}
```

### 3. FFI Bindings (Layer 2)
For external library bindings, Farscape generates quotation semantic carriers:

```fsharp
// Fidelity.RA6M5/HAL.fs - Quotation-based FFI binding
// Alex inspects quotations and generates platform-appropriate MLIR

/// Function descriptor - quotation carries calling convention
let fspGpioOpenDescriptor: Expr<FunctionDescriptor> = <@
    { CName = "R_IOPORT_Open"
      Parameters = [
          { Name = "ctrl"; Type = Ptr NativeInt; PassBy = Value }
          { Name = "cfg"; Type = Ptr NativeInt; PassBy = Value }
      ]
      ReturnType = U32
      CallingConvention = CDecl }
@>
```

> **Note**: The quotation semantic carriers below are PLANNED, not yet implemented.

## What Farscape Parses

From C headers, Farscape extracts:

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
| `RetType FuncName(params)` | Quotation semantic carrier (FFI descriptor) |

## Key CMSIS/FSP Patterns

```c
// Access qualifiers → AccessKind
#define __IO volatile           // ReadWrite
#define __I  volatile const     // ReadOnly  
#define __O  volatile           // WriteOnly

// Bit field macros → BitFieldDescriptor
#define R_SCI_SSR_RDRF_Pos  (6U)
#define R_SCI_SSR_RDRF_Msk  (0x1UL << R_SCI_SSR_RDRF_Pos)

// Peripheral instance → address in Instances map
#define R_PORT0_BASE  (0x40040000UL)
#define R_PORT0       ((R_PORT0_Type *) R_PORT0_BASE)
```

## Link-Time Consideration

The extern declarations reference a **library name** (e.g., `"ra_fsp"`).

At link time, the linker must find `libra_fsp.a` containing:
- `R_IOPORT_Open`
- `R_SCI_UART_Write`
- etc.

Farscape doesn't produce this library - it's pre-compiled by the vendor (Renesas). Farscape only produces the F# bindings that reference it.

## What Farscape Does NOT Do

- Generate MLIR or LLVM code
- Know about Alex's internal patterns
- Make platform-specific code generation decisions
- Compile the FSP library itself

## Relationship to Other Projects

| Project | Relationship |
|---------|--------------|
| **BAREWire** | Farscape uses BAREWire.Core types; generates BAREWire.[Target] |
| **Firefly** | Firefly compiles Farscape's output; Alex handles externs |
| **Fidelity.Platform** | Generated Fidelity.[Target] may use Fidelity.Platform types |

## Canonical Document

See Firefly `/docs/Quotation_Based_Memory_Architecture.md` for the complete binding architecture.
