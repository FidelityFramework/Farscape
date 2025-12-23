# Farscape Architecture Overview

## Purpose

Farscape generates F# bindings from C/C++ headers for the Fidelity native compilation ecosystem. Unlike traditional FFI tools that target runtime interop, Farscape generates code specifically for ahead-of-time native compilation via Firefly.

> **Architecture Update (December 2024)**: Farscape now generates **quotation-based output** with active patterns.
> See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md` for the unified four-component architecture.

## Design Principles

### 1. XParsec-Powered Parsing

Farscape uses XParsec, the same parser combinator library that powers Firefly's PSG traversal. This provides:

- Type-safe parsing with compile-time guarantees
- Composable parsers for complex C/C++ constructs
- Excellent error messages with precise locations
- Pure F# implementation with no external dependencies

### 2. Fidelity-First Output

Output is designed for Firefly native compilation, not .NET runtime interop:

- Uses fsnative phantom types for memory safety
- Generates BAREWire descriptors for hardware targets
- Produces `Platform.Bindings` declarations that Alex recognizes
- No BCL dependencies in generated code

### 3. Embedded Focus

First-class support for embedded targets:

- CMSIS HAL header parsing
- Peripheral register extraction
- Volatile qualifier preservation
- Memory-mapped I/O patterns

## Pipeline Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Farscape Pipeline                               │
│                                                                         │
│  Stage 1: Parsing                                                       │
│  ┌───────────────────────────────────────────────────────────────────┐ │
│  │  HeaderParser.fs (XParsec)                                        │ │
│  │  - C/C++ lexical analysis                                         │ │
│  │  - Declaration extraction                                          │ │
│  │  - Macro preprocessing (future)                                   │ │
│  └───────────────────────────────────────────────────────────────────┘ │
│                                │                                        │
│                                ▼                                        │
│  Stage 2: Type Mapping                                                  │
│  ┌───────────────────────────────────────────────────────────────────┐ │
│  │  TypeMapper.fs                                                    │ │
│  │  - C type → F# type conversion                                    │ │
│  │  - Pointer handling (const, volatile)                             │ │
│  │  - Struct field layout                                            │ │
│  └───────────────────────────────────────────────────────────────────┘ │
│                                │                                        │
│                                ▼                                        │
│  Stage 3: Code Generation                                               │
│  ┌───────────────────────────────────────────────────────────────────┐ │
│  │  CodeGenerator.fs                                                 │ │
│  │  - F# type definitions                                            │ │
│  │  - Platform.Bindings declarations                                 │ │
│  │  - Module structure                                               │ │
│  └───────────────────────────────────────────────────────────────────┘ │
│                                │                                        │
│                                ▼                                        │
│  Stage 4: Quotation & Pattern Generation                               │
│  ┌───────────────────────────────────────────────────────────────────┐ │
│  │  QuotationGenerator.fs                                            │ │
│  │  - Expr<PeripheralDescriptor> quotations                          │ │
│  │  - Active patterns for PSG recognition                            │ │
│  │  - MemoryModel record generation                                  │ │
│  └───────────────────────────────────────────────────────────────────┘ │
│                                │                                        │
│                                ▼                                        │
│  Output                                                                 │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐   │
│  │  Types.fs   │  │ Bindings.fs │  │Quotations.fs│  │ Patterns.fs │   │
│  └─────────────┘  └─────────────┘  └─────────────┘  └─────────────┘   │
└─────────────────────────────────────────────────────────────────────────┘
```

## Core Modules

### HeaderParser.fs

XParsec-based parser for C/C++ headers:

```fsharp
// Parse a C struct definition
let structParser =
    parse {
        do! keyword "struct"
        let! name = optional identifier
        do! symbol "{"
        let! fields = many fieldParser
        do! symbol "}"
        return StructDef { Name = name; Fields = fields }
    }

// Parse a field with qualifiers
let fieldParser =
    parse {
        let! qualifiers = many qualifier  // volatile, const, __I, __O, __IO
        let! baseType = typeSpecifier
        let! name = identifier
        let! arraySize = optional arrayDeclarator
        do! symbol ";"
        return { Qualifiers = qualifiers; Type = baseType; Name = name; ArraySize = arraySize }
    }
```

### TypeMapper.fs

Maps C types to F# equivalents:

```fsharp
let mapCType (cType: CType) : FSharpType =
    match cType with
    | CPrimitive "uint32_t" -> FSharpType.UInt32
    | CPrimitive "int32_t" -> FSharpType.Int32
    | CPointer (inner, qualifiers) ->
        let region = if hasVolatile qualifiers then "peripheral" else "sram"
        let access = extractAccess qualifiers  // from __I, __O, __IO
        FSharpType.NativePtr (mapCType inner, region, access)
    | CStruct fields ->
        FSharpType.Record (fields |> List.map mapField)
    | _ -> ...
```

### CodeGenerator.fs

Generates F# source code:

```fsharp
let generateStruct (name: string) (fields: FieldDef list) =
    sprintf """
[<Struct; StructLayout(LayoutKind.Sequential)>]
type %s = {
%s
}
""" name (fields |> List.map generateField |> String.concat "\n")

let generateBinding (func: FunctionDef) =
    sprintf """
    let %s %s : %s = Unchecked.defaultof<%s>
""" func.Name (generateParams func.Params) func.ReturnType func.ReturnType
```

### QuotationGenerator.fs (NEW - December 2024)

Generates F# quotations and active patterns for the nanopass pipeline:

```fsharp
// Generate quotation for peripheral descriptor
let generatePeripheralQuotation (peripheral: PeripheralDef) =
    sprintf """
let %sQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "%s"
      Instances = Map.ofList [%s]
      Layout = { Size = %d; Alignment = %d; Fields = %sFields }
      MemoryRegion = Peripheral }
@>
""" peripheral.Name peripheral.Name
    (generateInstanceList peripheral.Instances)
    peripheral.Size
    peripheral.Alignment
    peripheral.Name

// Generate active pattern for PSG recognition
let generateActivePattern (func: FunctionDef) =
    sprintf """
let (|%s|_|) (node: PSGNode) : %s option =
    match node with
    | CallToExtern "%s" args -> Some (extractArgs args)
    | _ -> None
""" func.PatternName func.ExtractedType func.ExternName

// Generate MemoryModel record
let generateMemoryModel (target: TargetDef) =
    sprintf """
let %sMemoryModel: MemoryModel = {
    TargetFamily = "%s"
    PeripheralDescriptors = [%s]
    RegisterConstraints = [%s]
    Regions = %sRegions
    Recognize = recognize%sMemoryOperation
    CacheTopology = None
    CoherencyModel = None
}
""" target.Name target.Family
    (target.Peripherals |> List.map (fun p -> p.Name + "Quotation") |> String.concat "; ")
    (generateConstraintList target)
    target.Name
    target.Name
```

### DescriptorGenerator.fs (Legacy)

Generates BAREWire hardware descriptors:

```fsharp
let generatePeripheralDescriptor (peripheral: PeripheralDef) =
    sprintf """
let %sDescriptor: PeripheralDescriptor = {
    Name = "%s"
    Instances = Map.ofList [
%s
    ]
    Layout = {
        Size = %d
        Alignment = %d
        Fields = [
%s
        ]
    }
    MemoryRegion = Peripheral
}
""" peripheral.Name peripheral.Name
    (generateInstances peripheral.Instances)
    peripheral.Size
    peripheral.Alignment
    (generateFields peripheral.Fields)
```

## Library Markers

Farscape uses special library names that Firefly's Alex recognizes:

| Library Name | Target | Code Generation |
|--------------|--------|-----------------|
| `__cmsis` | ARM CMSIS HAL | Memory-mapped register access |
| `__fidelity` | Alloy platform | Syscalls or platform APIs |
| `libname` | Standard library | Dynamic linking |

## Example: CMSIS GPIO

Input header:
```c
typedef struct {
    __IO uint32_t MODER;
    __IO uint32_t OTYPER;
    __I  uint32_t IDR;
    __IO uint32_t ODR;
    __O  uint32_t BSRR;
} GPIO_TypeDef;

#define GPIOA_BASE 0x48000000UL
#define GPIOA ((GPIO_TypeDef *) GPIOA_BASE)

void HAL_GPIO_Init(GPIO_TypeDef *GPIOx, GPIO_InitTypeDef *GPIO_Init);
void HAL_GPIO_WritePin(GPIO_TypeDef *GPIOx, uint16_t GPIO_Pin, GPIO_PinState PinState);
```

Generated Types.fs:
```fsharp
namespace CMSIS.STM32L5.GPIO

open Alloy

[<Struct; StructLayout(LayoutKind.Sequential)>]
type GPIO_TypeDef = {
    MODER: NativePtr<uint32, peripheral, readWrite>
    OTYPER: NativePtr<uint32, peripheral, readWrite>
    IDR: NativePtr<uint32, peripheral, readOnly>
    ODR: NativePtr<uint32, peripheral, readWrite>
    BSRR: NativePtr<uint32, peripheral, writeOnly>
}

let GPIOA_BASE = 0x48000000un
```

Generated Bindings.fs:
```fsharp
module Platform.Bindings =
    let halGpioInit gpio init : unit = Unchecked.defaultof<unit>
    let halGpioWritePin gpio pin state : unit = Unchecked.defaultof<unit>
```

Generated Descriptors.fs:
```fsharp
let gpioDescriptor: PeripheralDescriptor = {
    Name = "GPIO"
    Instances = Map.ofList ["GPIOA", 0x48000000un]
    Layout = {
        Size = 0x400
        Alignment = 4
        Fields = [
            { Name = "MODER"; Offset = 0x00; Type = U32; Access = ReadWrite; ... }
            { Name = "OTYPER"; Offset = 0x04; Type = U32; Access = ReadWrite; ... }
            { Name = "IDR"; Offset = 0x10; Type = U32; Access = ReadOnly; ... }
            { Name = "ODR"; Offset = 0x14; Type = U32; Access = ReadWrite; ... }
            { Name = "BSRR"; Offset = 0x18; Type = U32; Access = WriteOnly; ... }
        ]
    }
    MemoryRegion = Peripheral
}
```

## Current Limitations

1. **CppParser Hardcoded**: Currently only parses cJSON.h; needs XParsec wiring
2. **No Macro Extraction**: #define constants not yet extracted
3. **Missing CMSIS Qualifiers**: __I, __O, __IO not yet recognized
4. **Awaiting Dependencies**: Full output requires fsnative types and BAREWire descriptors

## Related Documents

- [BAREWire Integration](./02_BAREWire_Integration.md)
- [fsnative Integration](./03_fsnative_Integration.md)
- [Type Mapping Reference](./Type_Mapping_Reference.md)
