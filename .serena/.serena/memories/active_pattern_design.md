# Active Pattern Design for PSG Recognition

## Why Active Patterns?

Active patterns are F#'s "hidden jewel" for compositional pattern matching. For Farscape, they provide:

1. **Compositional Recognition**: Patterns compose naturally
2. **Type-Safe Extraction**: Compiler verifies pattern matching
3. **Extensibility**: New patterns add without modifying existing code
4. **PSG Integration**: Natural fit for Firefly's semantic graph traversal

## Pattern Categories

### 1. Function Call Patterns

Match specific extern function calls:

```fsharp
let (|HalGpioInit|_|) (node: PSGNode) =
    match node with
    | CallToExtern "HAL_GPIO_Init" [gpio; init] ->
        Some { Port = extractPort gpio; Config = extractInitConfig init }
    | _ -> None

let (|HalGpioWritePin|_|) (node: PSGNode) =
    match node with
    | CallToExtern "HAL_GPIO_WritePin" [gpio; pin; state] ->
        Some { Port = extractPort gpio; Pin = extractPin pin; State = extractState state }
    | _ -> None
```

### 2. Memory Access Patterns

Match register read/write operations:

```fsharp
let (|RegisterRead|_|) (node: PSGNode) =
    match node with
    | PointerDeref { Base = PeripheralBase base; Offset = offset } ->
        Some { Address = base + offset; Width = inferWidth node }
    | _ -> None

let (|RegisterWrite|_|) (node: PSGNode) =
    match node with
    | Assignment { Target = PointerDeref { Base = PeripheralBase base; Offset = offset }; Value = value } ->
        Some { Address = base + offset; Value = value; Width = inferWidth node }
    | _ -> None
```

### 3. Constraint Validation Patterns

Validate access constraints at compile time:

```fsharp
let (|ReadOnlyViolation|_|) (node: PSGNode) =
    match node with
    | RegisterWrite { Address = addr } when isReadOnlyRegister addr ->
        Some { Register = lookupRegister addr; Operation = "write" }
    | _ -> None

let (|WriteOnlyViolation|_|) (node: PSGNode) =
    match node with
    | RegisterRead { Address = addr } when isWriteOnlyRegister addr ->
        Some { Register = lookupRegister addr; Operation = "read" }
    | _ -> None
```

### 4. Composite Patterns

Combine simpler patterns:

```fsharp
let (|GpioOperation|_|) node =
    match node with
    | HalGpioInit info -> Some (GpioInit info)
    | HalGpioWritePin info -> Some (GpioWrite info)
    | HalGpioReadPin info -> Some (GpioRead info)
    | HalGpioTogglePin info -> Some (GpioToggle info)
    | _ -> None

let (|PeripheralOperation|_|) node =
    match node with
    | GpioOperation op -> Some (GPIO op)
    | UsartOperation op -> Some (USART op)
    | SpiOperation op -> Some (SPI op)
    | I2cOperation op -> Some (I2C op)
    | _ -> None
```

## Integration with MemoryModel

The `Recognize` function in `MemoryModel` uses active patterns:

```fsharp
let recognizeSTM32L5MemoryOperation (node: PSGNode) : MemoryOperation option =
    match node with
    | PeripheralOperation op -> Some (PeripheralOp op)
    | DmaOperation op -> Some (DmaOp op)
    | SystemControlOperation op -> Some (SystemOp op)
    | _ -> None

let stm32l5MemoryModel: MemoryModel = {
    // ...
    Recognize = recognizeSTM32L5MemoryOperation
    // ...
}
```

## Generation from C Headers

Farscape generates patterns from parsed function signatures:

```c
// C header
void HAL_GPIO_WritePin(GPIO_TypeDef* GPIOx, uint16_t GPIO_Pin, GPIO_PinState PinState);
```

Generates:

```fsharp
// Pattern definition
let (|HalGpioWritePin|_|) (node: PSGNode) : GpioWritePinInfo option =
    match node with
    | CallToExtern "HAL_GPIO_WritePin" [gpio; pin; state] ->
        Some {
            Port = extractGpioPort gpio
            Pin = extractUInt16 pin
            State = extractPinState state
        }
    | _ -> None

// Info type
type GpioWritePinInfo = {
    Port: string
    Pin: uint16
    State: PinState
}
```

## Pattern Naming Convention

| C Function | Active Pattern | Info Type |
|------------|----------------|-----------|
| `HAL_GPIO_Init` | `(|HalGpioInit|_|)` | `GpioInitInfo` |
| `HAL_GPIO_WritePin` | `(|HalGpioWritePin|_|)` | `GpioWritePinInfo` |
| `HAL_UART_Transmit` | `(|HalUartTransmit|_|)` | `UartTransmitInfo` |

## Canonical Reference

See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md`, section "Active Patterns: The Recognition Substrate"
