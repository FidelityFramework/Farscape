# Farscape: F# Native Library Binding Generator

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Farscape is a command-line tool that automatically generates idiomatic F# bindings for C/C++ libraries. It leverages LibClang through CppSharp to parse C/C++ headers and produces F# code that can be directly used in F# applications targeting native compilation (via Firefly/Fidelity) or .NET runtime.

<table>
  <tr>
    <td align="center" width="100%">
      <strong>Experimental</strong><br>
      This project is in active development. The core parsing infrastructure is functional,
      but code generation is being refined for Fidelity native compilation.
    </td>
  </tr>
</table>

## Features

- **C/C++ Header Parsing**: Uses CppSharp/LibClang to accurately parse C and C++ header files
- **Idiomatic F# Code Generation**:
  - C/C++ namespaces to F# modules
  - C structs to F# record types with `[<Struct>]` attribute
  - C enums to F# enums with proper underlying types
  - C++ classes to F# types with method bindings
- **P/Invoke Support**: Automatically creates proper `[<DllImport>]` declarations
- **Type Mapping**: Precise numeric types with matching bit widths and signedness
- **Cross-Platform Bindings**: Generated code works with native libraries on Windows, Linux, macOS, and embedded platforms
- **Project Generation**: Creates complete F# projects ready for building

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           Farscape Pipeline                             │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  C/C++ Header    ──►  CppParser.fs   ──►  TypeMapper.fs                │
│  (e.g., gpio.h)       (CppSharp)          (C → F# types)               │
│                            │                    │                       │
│                            ▼                    ▼                       │
│                    Declaration list      TypeMapping list               │
│                            │                    │                       │
│                            └──────┬─────────────┘                       │
│                                   ▼                                     │
│                          CodeGenerator.fs                               │
│                          (F# binding generation)                        │
│                                   │                                     │
│                                   ▼                                     │
│                          BindingGenerator.fs                            │
│                          (Project orchestration)                        │
│                                   │                                     │
│                                   ▼                                     │
│                           F# Project Output                             │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Core Modules

| Module | Purpose |
|--------|---------|
| `CppParser.fs` | Parse C/C++ headers via CppSharp/LibClang |
| `TypeMapper.fs` | Map C types to F# equivalents |
| `CodeGenerator.fs` | Generate F# binding code |
| `BindingGenerator.fs` | Orchestrate full pipeline |
| `Project.fs` | Generate .fsproj and solution files |

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- CppSharp NuGet package (automatically restored)

## Installation

### From Source

```bash
# Clone the repository
git clone https://github.com/speakez-llc/farscape.git
cd farscape

# Build the project
dotnet build

# Install as a global tool
dotnet tool install --global --add-source ./src/Farscape.Cli/nupkg farscape
```

## Usage

```bash
# Basic usage
farscape generate --header path/to/header.h --library libname

# With include paths and defines (for CMSIS, etc.)
farscape generate --header stm32l5xx_hal_gpio.h \
    --library cmsis \
    --include-paths ./CMSIS/Core/Include,./STM32L5xx/Include \
    --defines STM32L552xx,USE_HAL_DRIVER \
    --verbose

# Full options
farscape generate [options]

Options:
  -h, --header <header> (REQUIRED)     Path to C/C++ header file
  -l, --library <library> (REQUIRED)   Name of native library to bind to
  -o, --output <output>                Output directory [default: ./output]
  -n, --namespace <namespace>          Namespace for generated code [default: NativeBindings]
  -i, --include-paths <paths>          Additional include paths (comma-separated)
  -d, --defines <defines>              Preprocessor definitions (comma-separated)
  -v, --verbose                        Verbose output [default: False]
  -?, --help                           Show help and usage information
```

## Examples

### Basic C Library

Given a simple C header:

```c
// math_lib.h
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// Adds two integers
int add(int a, int b);

// Multiplies two doubles
double multiply(double a, double b);

#ifdef __cplusplus
}
#endif
```

Generate F# bindings:

```bash
farscape generate --header math_lib.h --library mathlib
```

Generated F# code:

```fsharp
namespace NativeBindings

open System.Runtime.InteropServices

module NativeBindings =
    /// Adds two integers
    [<DllImport("mathlib", CallingConvention = CallingConvention.Cdecl)>]
    extern int add(int a, int b)

    /// Multiplies two doubles
    [<DllImport("mathlib", CallingConvention = CallingConvention.Cdecl)>]
    extern double multiply(double a, double b)
```

### Embedded/CMSIS Headers

For embedded development with CMSIS HAL:

```bash
farscape generate \
    --header STM32L5xx_HAL_Driver/Inc/stm32l5xx_hal_gpio.h \
    --library __cmsis \
    --include-paths CMSIS/Core/Include,STM32L5xx/Include,STM32L5xx_HAL_Driver/Inc \
    --defines STM32L552xx,USE_HAL_DRIVER \
    --namespace CMSIS.STM32L5.GPIO \
    --verbose
```

## Fidelity/Firefly Integration

Farscape is designed to generate bindings compatible with the Fidelity native compilation toolchain:

- **Library marker**: Use `__cmsis` or similar markers that Alex (Firefly's targeting layer) recognizes
- **Typed pointers**: Generate `nativeptr<T>` instead of raw `nativeint` where appropriate
- **Struct layout**: Use `[<Struct; StructLayout(LayoutKind.Sequential)>]` for C struct compatibility

See the Firefly documentation (`docs/Farscape_Assessment_January_Demo.md`) for detailed integration guidance.

## Roadmap

- [x] Core CppSharp parsing infrastructure
- [x] Basic type mapping (primitives, pointers, arrays)
- [x] P/Invoke declaration generation
- [ ] Macro/constant extraction
- [ ] Fidelity-specific output mode
- [ ] Improved struct handling with volatile semantics
- [ ] Function pointer to delegate mapping
- [ ] C++ template to F# generic mapping

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- [CppSharp](https://github.com/mono/CppSharp) for LibClang bindings
- [LLVM/Clang](https://llvm.org/) project for LibClang
- [Firefly](https://github.com/speakez-llc/firefly) F# native compiler
- F# community for inspiration and support
