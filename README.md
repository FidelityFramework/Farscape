# Farscape

F# bindings generator for C/C++ libraries, powered by XParsec.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![License: Commercial](https://img.shields.io/badge/License-Commercial-orange.svg)](Commercial.md)

<p align="center">
🚧 <strong>Under Active Development</strong> 🚧<br>
<em>This project is in early development and not intended for production use.</em>
</p>

## Overview

Farscape automatically generates F# bindings from C/C++ header files. Using XParsec for header parsing, it produces type-safe F# code that integrates seamlessly with the Fidelity native compilation toolchain.

### Key Characteristics

- **XParsec-Powered Parsing**: Pure F# parser combinators for C/C++ headers
- **Idiomatic F# Output**: Generates proper F# types, not just raw P/Invoke
- **Fidelity Integration**: Output designed for Firefly native compilation
- **Embedded Focus**: First-class support for CMSIS HAL and peripheral headers
- **BAREWire Descriptors**: Generates peripheral descriptors for hardware targets

## The Fidelity Framework

Farscape is part of the **Fidelity** native F# compilation ecosystem:

| Project | Role |
|---------|------|
| **[Firefly](https://github.com/speakez-llc/firefly)** | AOT compiler: F# → PSG → MLIR → Native binary |
| **[Alloy](https://github.com/speakez-llc/alloy)** | Native standard library with platform bindings |
| **[BAREWire](https://github.com/speakez-llc/barewire)** | Binary encoding, memory mapping, zero-copy IPC |
| **Farscape** | C/C++ header parsing for native library bindings |
| **[XParsec](https://github.com/speakez-llc/xparsec)** | Parser combinators powering PSG traversal and header parsing |

The name "Fidelity" reflects the framework's core mission: **preserving type and memory safety** from source code through compilation to native execution.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           Farscape Pipeline                             │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  C/C++ Header    ──►  XParsec Parser  ──►  TypeMapper.fs               │
│  (e.g., gpio.h)       (HeaderParser)       (C → F# types)              │
│                            │                    │                       │
│                            ▼                    ▼                       │
│                    Declaration AST       TypeMapping list               │
│                            │                    │                       │
│                            └──────┬─────────────┘                       │
│                                   ▼                                     │
│                          CodeGenerator.fs                               │
│                          (F# binding generation)                        │
│                                   │                                     │
│                        ┌──────────┼──────────┐                         │
│                        ▼          ▼          ▼                         │
│                   F# Bindings  BAREWire   Fidelity                     │
│                   (.fs files)  Descriptors  Externs                    │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Core Modules

| Module | Purpose |
|--------|---------|
| `HeaderParser.fs` | XParsec-based C/C++ header parsing |
| `TypeMapper.fs` | Map C types to F# equivalents |
| `CodeGenerator.fs` | Generate F# binding code |
| `DescriptorGenerator.fs` | Generate BAREWire peripheral descriptors |
| `BindingGenerator.fs` | Orchestrate full pipeline |

## XParsec: The Parsing Foundation

Farscape uses [XParsec](https://github.com/speakez-llc/xparsec), the same parser combinator library that powers Firefly's PSG traversal. This provides:

- **Type-safe parsing**: Errors caught at compile time
- **Composable parsers**: Build complex parsers from simple primitives
- **Excellent error messages**: Precise location and context for parse failures
- **Pure F#**: No external dependencies or code generation

```fsharp
// Example: Parsing a C struct field
let fieldParser =
    parse {
        let! typ = typeSpecifier
        let! name = identifier
        let! arraySize = optional arrayDeclarator
        do! symbol ";"
        return { Type = typ; Name = name; ArraySize = arraySize }
    }
```

## Usage

```bash
# Basic usage
farscape generate --header path/to/header.h --library libname

# With include paths (for CMSIS, etc.)
farscape generate --header stm32l5xx_hal_gpio.h \
    --library __cmsis \
    --include-paths ./CMSIS/Core/Include,./STM32L5xx/Include \
    --defines STM32L552xx,USE_HAL_DRIVER \
    --verbose

# Generate BAREWire peripheral descriptors
farscape generate --header stm32l5xx_hal_gpio.h \
    --output-mode descriptors \
    --library __cmsis

# Full options
farscape generate [options]

Options:
  -h, --header <header>         Path to C/C++ header file (required)
  -l, --library <library>       Name of native library (required)
  -o, --output <output>         Output directory [default: ./output]
  -n, --namespace <namespace>   Namespace for generated code
  -i, --include-paths <paths>   Additional include paths (comma-separated)
  -d, --defines <defines>       Preprocessor definitions (comma-separated)
  -m, --output-mode <mode>      Output mode: bindings | descriptors | both
  -v, --verbose                 Verbose output
```

## Output Modes

### F# Bindings (Default)

Standard F# bindings for use with Firefly:

```fsharp
namespace CMSIS.STM32L5.GPIO

open Alloy

[<Struct; StructLayout(LayoutKind.Sequential)>]
type GPIO_InitTypeDef = {
    Pin: uint32
    Mode: uint32
    Pull: uint32
    Speed: uint32
    Alternate: uint32
}

module HAL =
    let GPIO_Init (gpio: nativeptr<GPIO_TypeDef>) (init: nativeptr<GPIO_InitTypeDef>) : unit =
        Platform.Bindings.halGpioInit gpio init

    let GPIO_WritePin (gpio: nativeptr<GPIO_TypeDef>) (pin: uint16) (state: GPIO_PinState) : unit =
        Platform.Bindings.halGpioWritePin gpio pin state
```

### BAREWire Peripheral Descriptors

For embedded targets, generate descriptors that Alex uses for memory-mapped access:

```fsharp
let gpioDescriptor: PeripheralDescriptor = {
    Name = "GPIO"
    Instances = Map.ofList [
        "GPIOA", 0x48000000un
        "GPIOB", 0x48000400un
        "GPIOC", 0x48000800un
    ]
    Layout = {
        Fields = [
            { Name = "MODER";  Offset = 0x00; Type = U32; Access = ReadWrite }
            { Name = "OTYPER"; Offset = 0x04; Type = U32; Access = ReadWrite }
            { Name = "OSPEEDR"; Offset = 0x08; Type = U32; Access = ReadWrite }
            { Name = "PUPDR";  Offset = 0x0C; Type = U32; Access = ReadWrite }
            { Name = "IDR";    Offset = 0x10; Type = U32; Access = ReadOnly }
            { Name = "ODR";    Offset = 0x14; Type = U32; Access = ReadWrite }
            { Name = "BSRR";   Offset = 0x18; Type = U32; Access = WriteOnly }
        ]
    }
    MemoryRegion = Peripheral
}
```

## Type Mapping

Farscape maps C types to appropriate F# equivalents:

| C Type | F# Type | Notes |
|--------|---------|-------|
| `int8_t` | `int8` | Signed 8-bit |
| `uint8_t` | `uint8` | Unsigned 8-bit |
| `int16_t` | `int16` | Signed 16-bit |
| `uint16_t` | `uint16` | Unsigned 16-bit |
| `int32_t` / `int` | `int32` | Signed 32-bit |
| `uint32_t` | `uint32` | Unsigned 32-bit |
| `int64_t` | `int64` | Signed 64-bit |
| `uint64_t` | `uint64` | Unsigned 64-bit |
| `float` | `float32` | 32-bit float |
| `double` | `float` | 64-bit float |
| `void*` | `nativeint` | Untyped pointer |
| `T*` | `nativeptr<T>` | Typed pointer |
| `const T*` | `nativeptr<T>` | Read-only typed pointer |
| `T[N]` | `T[]` or inline | Fixed-size array |

## Fidelity Integration

### Library Markers

Use special library names that Firefly's Alex component recognizes:

- `__cmsis` - CMSIS HAL functions → memory-mapped register access
- `__fidelity` - Alloy platform bindings → syscalls or platform APIs
- `libname` - Standard library → dynamic linking

### Generated Platform Bindings

For Fidelity targets, Farscape generates `Platform.Bindings` declarations:

```fsharp
module Platform.Bindings =
    let halGpioInit gpio init : unit = Unchecked.defaultof<unit>
    let halGpioWritePin gpio pin state : unit = Unchecked.defaultof<unit>
    let halGpioReadPin gpio pin : GPIO_PinState = Unchecked.defaultof<GPIO_PinState>
```

Alex provides the actual implementations based on target platform.

## Examples

### CMSIS HAL for STM32

```bash
# Generate bindings for GPIO
farscape generate \
    --header STM32L5xx_HAL_Driver/Inc/stm32l5xx_hal_gpio.h \
    --library __cmsis \
    --include-paths CMSIS/Core/Include,STM32L5xx/Include \
    --defines STM32L552xx,USE_HAL_DRIVER \
    --namespace CMSIS.STM32L5.GPIO \
    --output-mode both
```

### Using Generated Bindings

```fsharp
open Alloy
open CMSIS.STM32L5.GPIO

let blink () =
    // Initialize GPIO for LED
    let init = GPIO_InitTypeDef(
        Pin = GPIO_PIN_5,
        Mode = GPIO_MODE_OUTPUT_PP,
        Pull = GPIO_NOPULL,
        Speed = GPIO_SPEED_FREQ_LOW
    )
    HAL.GPIO_Init(GPIOA, &init)

    // Blink loop
    while true do
        HAL.GPIO_TogglePin(GPIOA, GPIO_PIN_5)
        Time.sleep 500
```

Compile with Firefly:
```bash
firefly compile Blinky.fidproj --target thumbv7em-none-eabihf
```

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- XParsec (automatically restored via NuGet)

## Installation

```bash
# Clone the repository
git clone https://github.com/speakez-llc/farscape.git
cd farscape

# Build
dotnet build

# Install as global tool
dotnet tool install --global --add-source ./nupkg farscape
```

## Development Status

Farscape is under active development. Current focus:

- [x] XParsec-based C header parsing
- [x] Basic type mapping (primitives, pointers, structs)
- [x] F# binding generation
- [ ] C++ header support (classes, templates)
- [ ] Macro/constant extraction
- [ ] BAREWire descriptor generation
- [ ] Function pointer to delegate mapping

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

Farscape is dual-licensed under both the Apache License 2.0 and a Commercial License.

### Open Source License

For open source projects, academic use, non-commercial applications, and internal tools, use Farscape under the **Apache License 2.0**.

### Commercial License

A Commercial License is required for incorporating Farscape into commercial products or services. See [Commercial.md](Commercial.md) for details.

### Patent Notice

Farscape generates BAREWire peripheral descriptors, which utilize technology covered by U.S. Patent Application No. 63/786,247 "System and Method for Zero-Copy Inter-Process Communication Using BARE Protocol". See BAREWire's [PATENTS.md](https://github.com/speakez-llc/barewire/blob/main/PATENTS.md) for licensing details.

## Acknowledgments

- **[XParsec](https://github.com/speakez-llc/xparsec)**: Parser combinators for F#
- **[Firefly](https://github.com/speakez-llc/firefly)**: F# native compiler
- **[BAREWire](https://github.com/speakez-llc/barewire)**: Memory descriptors and IPC
- **ARM CMSIS**: Standard interface for Cortex-M microcontrollers
