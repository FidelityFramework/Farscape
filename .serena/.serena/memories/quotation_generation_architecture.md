# Farscape Quotation Generation Architecture

## Primary Mission

Farscape transforms C/C++ headers into **F# quotations and active patterns** for the Fidelity nanopass pipeline.

This is NOT traditional FFI generation. Farscape produces:
1. `Expr<PeripheralDescriptor>` quotations encoding memory layout
2. Active patterns for PSG node recognition
3. `MemoryModel` records for fsnative integration

## The Four Outputs

### 1. Quotations (Expr<T>)

Quotations carry memory constraint information through the PSG:

```fsharp
// Generated from CMSIS GPIO header
let gpioPeripheralQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "GPIO"
      Instances = Map.ofList [
          ("GPIOA", 0x48000000un)
          ("GPIOB", 0x48000400un)
          ("GPIOC", 0x48000800un)
      ]
      Layout = {
          Size = 0x400
          Alignment = 4
          Fields = [
              { Name = "MODER"; Offset = 0x00; Type = U32; Access = ReadWrite; BitFields = None; Documentation = Some "Mode register" }
              { Name = "IDR"; Offset = 0x10; Type = U32; Access = ReadOnly; BitFields = None; Documentation = Some "Input data register" }
              { Name = "BSRR"; Offset = 0x18; Type = U32; Access = WriteOnly; BitFields = None; Documentation = Some "Bit set/reset register" }
          ]
      }
      MemoryRegion = Peripheral
  }
@>
```

### 2. Active Patterns

Compositional recognition patterns for PSG traversal:

```fsharp
// Generated from HAL function signatures
let (|GpioWritePin|_|) (node: PSGNode) : (string * int * bool) option =
    match node with
    | CallToExtern "HAL_GPIO_WritePin" [gpio; pin; state] ->
        Some (extractGpioInstance gpio, extractPinNum pin, extractState state)
    | _ -> None

let (|GpioReadPin|_|) (node: PSGNode) : (string * int) option =
    match node with
    | CallToExtern "HAL_GPIO_ReadPin" [gpio; pin] ->
        Some (extractGpioInstance gpio, extractPinNum pin)
    | _ -> None

// Composed patterns
let (|PeripheralAccess|_|) node =
    match node with
    | GpioWritePin info -> Some (GpioWrite info)
    | GpioReadPin info -> Some (GpioRead info)
    | UsartTransmit info -> Some (UsartTx info)
    | _ -> None
```

### 3. MemoryModel Record

Integration surface for fsnative nanopass pipeline:

```fsharp
// Generated for each target family
let stm32l5MemoryModel: MemoryModel = {
    TargetFamily = "STM32L5"
    PeripheralDescriptors = [
        gpioPeripheralQuotation
        usartPeripheralQuotation
        spiPeripheralQuotation
        i2cPeripheralQuotation
    ]
    RegisterConstraints = [
        gpioAccessConstraints
        usartAccessConstraints
    ]
    Regions = <@ [
        { Name = "Flash"; Start = 0x08000000un; Size = 512 * 1024; Kind = Flash }
        { Name = "SRAM1"; Start = 0x20000000un; Size = 256 * 1024; Kind = SRAM }
        { Name = "Peripherals"; Start = 0x40000000un; Size = 0x20000000; Kind = Peripheral }
    ] @>
    Recognize = recognizeSTM32L5MemoryOperation
    CacheTopology = None
    CoherencyModel = None
}
```

### 4. High-Level F# API (Optional)

Clean user-facing API that hides descriptor complexity:

```fsharp
// Generated Fidelity.STM32L5.GPIO module
module GPIO =
    let inline writePin port pin state =
        halGpioWritePin (getPortBase port) pin state
    
    let inline readPin port pin =
        halGpioReadPin (getPortBase port) pin
```

## Generation Pipeline

```
C/C++ Header
    ↓ XParsec parsing
Parsed AST (structs, functions, macros)
    ↓ Type mapping
BAREWire descriptor instances
    ↓ Quotation generation
Expr<PeripheralDescriptor> quotations
    ↓ Pattern generation
Active patterns for each function
    ↓ Model assembly
MemoryModel record
    ↓ Output
    ├── Quotations.fs
    ├── Patterns.fs
    ├── MemoryModel.fs
    └── API.fs (optional high-level)
```

## CMSIS Qualifier Mapping

| CMSIS | C Definition | Farscape Output |
|-------|--------------|-----------------|
| `__I` | `volatile const` | `Access = ReadOnly` in quotation |
| `__O` | `volatile` | `Access = WriteOnly` in quotation |
| `__IO` | `volatile` | `Access = ReadWrite` in quotation |

These map directly to `AccessKind` in generated quotations and inform active pattern validation.

## Canonical Reference

See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md` for the complete four-component architecture.
