# BAREWire Integration

Farscape generates BAREWire hardware descriptors from parsed C/C++ headers. This document describes how Farscape populates BAREWire types and how the generated descriptors are consumed by Firefly/Alex.

## Status

> **Implementation Status: PLANNED**
>
> BAREWire hardware descriptor types are not yet implemented. This document describes the design specification for Farscape's integration with BAREWire once those types exist.

## Dependency Chain

```
CMSIS/HAL Headers
       │
       ▼
   Farscape (parse)
       │
       ├──▶ Types.fs (F# structs using fsnative types)
       │
       ├──▶ Bindings.fs (Platform.Bindings declarations)
       │
       └──▶ Descriptors.fs (BAREWire types) ← REQUIRES BAREWire types
```

Farscape cannot generate complete hardware descriptors until BAREWire provides these types:

| BAREWire Type | Purpose | Status |
|---------------|---------|--------|
| `PeripheralDescriptor` | Complete peripheral definition | PLANNED |
| `PeripheralLayout` | Register set structure | PLANNED |
| `FieldDescriptor` | Individual register definition | PLANNED |
| `AccessKind` | Read/Write constraints | PLANNED |
| `MemoryRegionKind` | Memory classification | PLANNED |
| `BitFieldDescriptor` | Sub-register fields | PLANNED |
| `RegisterType` | Register data types | PLANNED |

See [BAREWire Hardware Descriptors](~/repos/BAREWire/docs/08%20Hardware%20Descriptors.md) for type definitions.

## Descriptor Generation Pipeline

### Stage 1: Header Parsing

Farscape's XParsec-based parser extracts C struct definitions:

```c
// Input: CMSIS header
typedef struct {
    __IO uint32_t MODER;
    __IO uint32_t OTYPER;
    __I  uint32_t IDR;
    __IO uint32_t ODR;
    __O  uint32_t BSRR;
} GPIO_TypeDef;
```

The parser produces an intermediate representation:

```fsharp
type CStructDef = {
    Name: string option           // Some "GPIO_TypeDef"
    Fields: CFieldDef list        // [MODER; OTYPER; IDR; ODR; BSRR]
}

type CFieldDef = {
    Qualifiers: CQualifier list   // [__IO], [__I], or [__O]
    Type: CType                   // uint32_t
    Name: string                  // "MODER"
    ArraySize: int option         // None for scalars
}

type CQualifier =
    | Volatile
    | Const
    | CMSIS_I    // __I - volatile const (read-only)
    | CMSIS_O    // __O - volatile (write-only)
    | CMSIS_IO   // __IO - volatile (read-write)
```

### Stage 2: Macro Extraction

Peripheral instance base addresses come from `#define` macros:

```c
// Input
#define GPIOA_BASE 0x48000000UL
#define GPIOB_BASE 0x48000400UL
#define GPIOA ((GPIO_TypeDef *) GPIOA_BASE)
#define GPIOB ((GPIO_TypeDef *) GPIOB_BASE)
```

Farscape extracts:

```fsharp
type CPeripheralInstance = {
    InstanceName: string      // "GPIOA"
    TypeName: string          // "GPIO_TypeDef"
    BaseAddress: unativeint   // 0x48000000un
}
```

### Stage 3: Layout Calculation

Farscape calculates field offsets and struct size:

```fsharp
let calculateLayout (fields: CFieldDef list) : PeripheralLayout =
    let mutable offset = 0
    let mutable maxAlign = 1

    let fieldDescriptors = [
        for field in fields do
            let (size, align) = getTypeMetrics field.Type
            // Align offset
            offset <- alignUp offset align
            maxAlign <- max maxAlign align

            yield {
                Name = field.Name
                Offset = offset
                Type = mapCTypeToRegisterType field.Type
                Access = mapQualifiersToAccess field.Qualifiers
                BitFields = None  // Extracted separately
                Documentation = None
            }

            offset <- offset + size
    ]

    {
        Size = alignUp offset maxAlign  // Total struct size
        Alignment = maxAlign
        Fields = fieldDescriptors
    }
```

### Stage 4: Descriptor Assembly

All parsed information is assembled into a `PeripheralDescriptor`:

```fsharp
let generatePeripheralDescriptor
    (structDef: CStructDef)
    (instances: CPeripheralInstance list)
    : PeripheralDescriptor =

    {
        Name = extractFamilyName structDef.Name  // "GPIO" from "GPIO_TypeDef"
        Instances =
            instances
            |> List.map (fun i -> i.InstanceName, i.BaseAddress)
            |> Map.ofList
        Layout = calculateLayout structDef.Fields
        MemoryRegion = Peripheral  // CMSIS peripherals are always volatile
    }
```

## CMSIS Qualifier Mapping

The critical mapping from CMSIS qualifiers to `AccessKind`:

| CMSIS | C Definition | AccessKind | Meaning |
|-------|--------------|------------|---------|
| `__I` | `volatile const` | `ReadOnly` | Hardware state; writes undefined |
| `__O` | `volatile` | `WriteOnly` | Trigger register; reads undefined |
| `__IO` | `volatile` | `ReadWrite` | Normal volatile register |

### Implementation

```fsharp
let mapQualifiersToAccess (qualifiers: CQualifier list) : AccessKind =
    match qualifiers with
    | q when List.contains CMSIS_I q -> ReadOnly
    | q when List.contains CMSIS_O q -> WriteOnly
    | q when List.contains CMSIS_IO q -> ReadWrite
    | q when List.contains Const q && List.contains Volatile q -> ReadOnly
    | q when List.contains Volatile q -> ReadWrite
    | _ -> ReadWrite  // Default for non-volatile (rare in CMSIS)
```

### Why This Matters

Access constraints are **hardware-enforced**. The generated `AccessKind` informs:

1. **fsnative type generation**: Fields get `readOnly`, `writeOnly`, or `readWrite` measures
2. **Alex code generation**: Prevents invalid read-modify-write on write-only registers
3. **Compile-time safety**: Attempts to read a write-only register fail with FS8001

Example error:

```fsharp
// F# code (using Farscape-generated bindings)
let value = gpio.BSRR  // Attempt to read write-only register

// Compile error:
// FS8001: Cannot read from write-only pointer 'BSRR'
```

## Output Format

### Descriptors.fs

Farscape generates a complete F# module:

```fsharp
namespace CMSIS.STM32L5.Descriptors

open BAREWire.Hardware

/// GPIO peripheral family descriptor
let gpioDescriptor: PeripheralDescriptor = {
    Name = "GPIO"
    Instances = Map.ofList [
        "GPIOA", 0x48000000un
        "GPIOB", 0x48000400un
        "GPIOC", 0x48000800un
        "GPIOD", 0x48000C00un
        "GPIOE", 0x48001000un
        "GPIOF", 0x48001400un
        "GPIOG", 0x48001800un
        "GPIOH", 0x48001C00un
    ]
    Layout = {
        Size = 0x400
        Alignment = 4
        Fields = [
            { Name = "MODER";   Offset = 0x00; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Mode register" }
            { Name = "OTYPER";  Offset = 0x04; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Output type register" }
            { Name = "OSPEEDR"; Offset = 0x08; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Output speed register" }
            { Name = "PUPDR";   Offset = 0x0C; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Pull-up/pull-down register" }
            { Name = "IDR";     Offset = 0x10; Type = U32; Access = ReadOnly;  BitFields = None; Documentation = Some "Input data register" }
            { Name = "ODR";     Offset = 0x14; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Output data register" }
            { Name = "BSRR";    Offset = 0x18; Type = U32; Access = WriteOnly; BitFields = None; Documentation = Some "Bit set/reset register" }
            { Name = "LCKR";    Offset = 0x1C; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Configuration lock register" }
            { Name = "AFRL";    Offset = 0x20; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Alternate function low register" }
            { Name = "AFRH";    Offset = 0x24; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Alternate function high register" }
            { Name = "BRR";     Offset = 0x28; Type = U32; Access = WriteOnly; BitFields = None; Documentation = Some "Bit reset register" }
        ]
    }
    MemoryRegion = Peripheral
}

/// USART peripheral family descriptor
let usartDescriptor: PeripheralDescriptor = {
    Name = "USART"
    Instances = Map.ofList [
        "USART1", 0x40013800un
        "USART2", 0x40004400un
        "USART3", 0x40004800un
        "UART4",  0x40004C00un
        "UART5",  0x40005000un
        "LPUART1", 0x40008000un
    ]
    Layout = {
        Size = 0x400
        Alignment = 4
        Fields = [
            { Name = "CR1";   Offset = 0x00; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Control register 1" }
            { Name = "CR2";   Offset = 0x04; Type = U32; Access = ReadWrite; BitFields = None; Documentation = None }
            { Name = "CR3";   Offset = 0x08; Type = U32; Access = ReadWrite; BitFields = None; Documentation = None }
            { Name = "BRR";   Offset = 0x0C; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Baud rate register" }
            { Name = "GTPR";  Offset = 0x10; Type = U32; Access = ReadWrite; BitFields = None; Documentation = None }
            { Name = "RTOR";  Offset = 0x14; Type = U32; Access = ReadWrite; BitFields = None; Documentation = None }
            { Name = "RQR";   Offset = 0x18; Type = U32; Access = WriteOnly; BitFields = None; Documentation = Some "Request register" }
            { Name = "ISR";   Offset = 0x1C; Type = U32; Access = ReadOnly;  BitFields = None; Documentation = Some "Interrupt and status register" }
            { Name = "ICR";   Offset = 0x20; Type = U32; Access = WriteOnly; BitFields = None; Documentation = Some "Interrupt flag clear register" }
            { Name = "RDR";   Offset = 0x24; Type = U32; Access = ReadOnly;  BitFields = None; Documentation = Some "Receive data register" }
            { Name = "TDR";   Offset = 0x28; Type = U32; Access = WriteOnly; BitFields = None; Documentation = Some "Transmit data register" }
        ]
    }
    MemoryRegion = Peripheral
}

/// All descriptors for STM32L5 family
let allDescriptors = [
    gpioDescriptor
    usartDescriptor
    // ... more peripherals
]
```

## Consumption by Firefly/Alex

### Memory Catalog

Alex uses the descriptors to build a memory catalog:

```fsharp
// In Alex/Bindings/MemoryCatalog.fs
type MemoryCatalog = {
    Peripherals: Map<string, PeripheralDescriptor>
    AddressToPeripheral: Map<unativeint, string * string>  // address → (family, instance)
}

let buildCatalog (descriptors: PeripheralDescriptor list) : MemoryCatalog =
    let peripherals = descriptors |> List.map (fun d -> d.Name, d) |> Map.ofList
    let addressMap = [
        for d in descriptors do
            for (instance, addr) in Map.toSeq d.Instances do
                yield addr, (d.Name, instance)
    ] |> Map.ofList
    { Peripherals = peripherals; AddressToPeripheral = addressMap }
```

### MLIR Generation

When Alex encounters peripheral access, it uses descriptor info:

```fsharp
// Alex sees: gpio.ODR <- 0x20u

// 1. Look up field in descriptor
let field = gpioDescriptor.Layout.Fields |> List.find (fun f -> f.Name = "ODR")

// 2. Verify access is legal
match field.Access with
| ReadOnly -> failwith "Cannot write to read-only register"
| WriteOnly | ReadWrite -> ()  // OK

// 3. Generate volatile store with correct offset
let baseAddr = Map.find "GPIOA" gpioDescriptor.Instances  // 0x48000000un
let ptr = builder.BuildIntToPtr baseAddr
let fieldPtr = builder.BuildGEP ptr [| int64 field.Offset |]
builder.BuildVolatileStore value fieldPtr
```

### Tree-Shaking

Only referenced descriptors are included in final binary:

1. Reachability analysis identifies used peripherals
2. Unused peripheral descriptors are eliminated
3. Final binary contains minimal metadata

## Bit Field Extraction (Future)

CMSIS headers define bit fields via macros:

```c
#define USART_CR1_UE_Pos    0U
#define USART_CR1_UE_Msk    (0x1UL << USART_CR1_UE_Pos)
#define USART_CR1_UE        USART_CR1_UE_Msk

#define USART_CR1_RE_Pos    2U
#define USART_CR1_RE_Msk    (0x1UL << USART_CR1_RE_Pos)
#define USART_CR1_RE        USART_CR1_RE_Msk
```

Future Farscape versions will extract these into `BitFieldDescriptor`:

```fsharp
{ Name = "UE"; Position = 0; Width = 1; Access = ReadWrite }
{ Name = "RE"; Position = 2; Width = 1; Access = ReadWrite }
```

## Implementation Roadmap

### Immediate (Required for BAREWire types)

1. BAREWire adds types to `src/Core/Hardware/`
2. Farscape references BAREWire
3. DescriptorGenerator.fs outputs `PeripheralDescriptor` instances

### Near-term

1. Macro extraction for base addresses
2. CMSIS qualifier recognition (`__I`, `__O`, `__IO`)
3. Struct layout calculation

### Future

1. Bit field extraction from `_Pos`/`_Msk` macros
2. Documentation extraction from comments
3. Peripheral dependency relationships

## Related Documents

| Document | Location |
|----------|----------|
| BAREWire Hardware Descriptors | `~/repos/BAREWire/docs/08 Hardware Descriptors.md` |
| fsnative Integration | `./03_fsnative_Integration.md` |
| Memory Interlock Requirements | `~/repos/Firefly/docs/Memory_Interlock_Requirements.md` |
