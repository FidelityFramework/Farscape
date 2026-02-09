# Farscape

F# bindings generator for C libraries, part of the Fidelity native compilation ecosystem.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![License: Commercial](https://img.shields.io/badge/License-Commercial-orange.svg)](Commercial.md)

<p align="center">
Under Active Development<br>
<em>This project is in early development and the API may undergo breaking changes.</em>
</p>

## Overview

Farscape automatically generates F# bindings from C header files. It uses **clang** for robust header parsing and **XParsec** parser combinators for post-processing C type strings and macro values, producing type-safe F# code that integrates with the Fidelity native compilation toolchain.

The codebase is structured around four functional programming patterns that compose cleanly:

- **XParsec Parser Combinators** (`CTypeParser.fs`): Monadic parsers decompose C type strings (`const char *` → `{ BaseType = "char"; PointerDepth = 1 }`), classify macro values, and parse numeric literals, replacing all Regex usage
- **Active Patterns** (`ActivePatterns.fs`): XParsec-backed active patterns (`ParsedCType`, `CharPointer|VoidPointer|TypedPointer|ValueType`, `CompilerBuiltin|InternalMacro|UserMacro`, `IntegerLiteral`) provide structural decomposition at match sites
- **Catamorphism** (`DeclarationAlgebra.fs`): A fold algebra over the Declaration DU; one traversal function serves typedef extraction, function collection, and full code generation through composable algebras
- **Typed Code AST** (`CodeAST.fs` → `CodeRenderer.fs`): Generation produces `FsDecl` values (typed, inspectable, testable AST nodes), not strings. The ONLY `StringBuilder` in the codebase is the final `CodeRenderer.render`

Farscape is part of the [Fidelity](https://github.com/FidelityFramework) native F# compilation ecosystem.

## Architecture

```mermaid
flowchart TD
    A["C Header<br/>(stdlib.h, unistd.h)"] --> B["Clang Two-Pass<br/>(CppParser.fs)"]
    B --> C["Declaration AST<br/>(functions, structs, enums,<br/>typedefs, macros)"]
    C --> D["XParsec Post-Processing<br/>(CTypeParser.fs)"]
    C --> E["TypeMapper.fs<br/>(type dictionary)"]
    D --> F["Active Patterns<br/>(ActivePatterns.fs)"]
    F --> G["Catamorphism<br/>(DeclarationAlgebra.fs)"]
    E --> G
    G --> H["FidelityCodeGenerator.fs<br/>Declaration list → FsDecl AST"]
    H --> I["CodeRenderer.fs<br/>FsDecl → F# source string<br/>(the ONLY StringBuilder)"]
```

### Functional Patterns in Action

**XParsec parser** decomposes a C type string:

```fsharp
// CTypeParser.fs: monadic parser for C types
static let pCType =
    parser {
        do! skipMany pQualifier          // strip const, restrict, volatile
        let! firstWord = pTypeWord       // "unsigned"
        let! restWords = many (spaces1 >>. pTypeWord)  // "long", "int"
        let! stars = many (skipChar '*') // pointer depth
        return { BaseType = baseType; PointerDepth = stars.Length }
    }
```

**Active pattern** classifies the parsed result:

```fsharp
// ActivePatterns.fs: structural decomposition via pattern matching
match "const char *" with
| ParsedCType (CharPointer) -> Generic("nativeptr", Named "byte")
| ParsedCType (VoidPointer) -> Named "nativeint"
| ParsedCType (ValueType t) -> Named (TypeMapper.getFSharpType t)
```

**Catamorphism** folds an algebra over all declarations in one pass:

```fsharp
// DeclarationAlgebra.fs: single traversal, composable algebras
let groups = cataDeclarations (generationAlgebra typedefMap) declarations
// One pass produces: enums, structs, functions, macros, all categorized
```

**Typed AST** separates generation from rendering:

```fsharp
// CodeAST.fs: typed nodes, not strings
LetBinding("memcpy",
    [{ Name = "dest"; Type = Named "nativeint" }; ...],
    Named "nativeint",
    DefaultOf (Named "nativeint"))
// CodeRenderer.fs renders to F# source (the ONLY StringBuilder)
```

### Core Modules

| Module | Purpose |
|--------|---------|
| `CppParser.fs` | Clang two-pass parsing: JSON AST + macro extraction |
| `CTypeParser.fs` | XParsec parsers for C type strings, macro values, numeric literals |
| `ActivePatterns.fs` | Type classification, macro filtering, keyword quoting via active patterns |
| `DeclarationAlgebra.fs` | Catamorphism: fold algebra over Declaration DU |
| `CodeAST.fs` | Typed code AST: `FsType`, `FsExpr`, `FsDecl` discriminated unions |
| `CodeRenderer.fs` | Single renderer: `FsDecl → string` (only StringBuilder in the codebase) |
| `FidelityCodeGenerator.fs` | Fidelity mode: catamorphism → FsDecl tree → rendered source |
| `TypeMapper.fs` | C-to-F# type dictionary and mapping |
| `CodeGenerator.fs` | P/Invoke mode: traditional DllImport generation |
| `BindingGenerator.fs` | Pipeline orchestration |

## Usage

```bash
# Generate Fidelity bindings from a standard library header
farscape generate \
    --header /usr/include/string.h \
    -l libc \
    -m fidelity \
    -n Fidelity.libc.Memory \
    -o ./output/

# Generate Fidelity bindings with idiomatic F# wrappers (Layer 2)
farscape generate \
    --header /usr/include/unistd.h \
    -l libc \
    -m fidelity-wrappers \
    -n Fidelity.libc.IO \
    -o ./output/

# Generate P/Invoke bindings (traditional .NET interop)
farscape generate \
    --header /usr/include/unistd.h \
    -l libc \
    -m pinvoke \
    -n LibC.IO \
    -o ./output/

# With include paths and defines (for CMSIS headers)
farscape generate \
    --header stm32l5xx_hal_gpio.h \
    -l __cmsis \
    -m fidelity \
    -i ./CMSIS/Core/Include,./STM32L5xx/Include \
    -d STM32L552xx,USE_HAL_DRIVER \
    -n Fidelity.CMSIS.GPIO \
    -v

Options:
      --header <header>         Path to C header file (required)
  -l, --library <library>       Name of native library (required)
  -o, --output <output>         Output directory [default: ./output]
  -n, --namespace <namespace>   Namespace for generated code [default: NativeBindings]
  -i, --include-paths <paths>   Additional include paths
  -d, --defines <defines>       Preprocessor definitions
  -m, --output-mode <mode>      Output mode: pinvoke | fidelity | fidelity-wrappers [default: pinvoke]
  -v, --verbose                 Verbose output
```

## Output Modes

### Fidelity Mode (`--output-mode fidelity`)

Generates `Unchecked.defaultof` stubs that Alex recognizes and replaces with platform-specific MLIR:

```fsharp
module Fidelity.libc.Memory

// Generated by Farscape, Fidelity binding for libc
// Alex provides platform-specific MLIR implementations for these bindings.

    /// void * memcpy(void *restrict __dest, const void *restrict __src, size_t __n)
    let memcpy (dest: nativeint) (src: nativeint) (n: nativeint) : nativeint =
        Unchecked.defaultof<nativeint>

    /// char * strcpy(char *restrict __dest, const char *restrict __src)
    let strcpy (dest: nativeptr<byte>) (src: nativeptr<byte>) : nativeptr<byte> =
        Unchecked.defaultof<nativeptr<byte>>

    /// unsigned long strlen(const char * __s)
    let strlen (s: nativeptr<byte>) : uint64 =
        Unchecked.defaultof<uint64>
```

### P/Invoke Mode (`--output-mode pinvoke`)

Traditional .NET P/Invoke bindings with DllImport attributes for use with the standard .NET runtime.

## Type Mapping

| C Type | F# Type | Notes |
|--------|---------|-------|
| `int` / `int32_t` | `int32` | Signed 32-bit |
| `unsigned int` / `uint32_t` | `uint32` | Unsigned 32-bit |
| `long` / `long int` | `int64` | Signed 64-bit |
| `unsigned long` | `uint64` | Unsigned 64-bit |
| `short` | `int16` | Signed 16-bit |
| `float` | `float32` | 32-bit float |
| `double` | `float` | 64-bit float |
| `char *` | `nativeptr<byte>` | Char pointer (active pattern: `CharPointer`) |
| `void *` | `nativeint` | Void pointer (active pattern: `VoidPointer`) |
| `T *` (other) | `nativeint` | Typed pointer (active pattern: `TypedPointer`) |
| `void (*)(...)` | `nativeint` | Function pointer (detected by `(*)`) |
| `size_t` | `nativeint` | Platform-sized (via typedef resolution) |

Type mapping uses XParsec-backed active patterns (`ParsedCType`, `CharPointer`/`VoidPointer`/`TypedPointer`/`ValueType`) with typedef chain resolution.

## Validated Output

Farscape's Fidelity mode has been validated against three real libc headers:

| Header | Output | Functions | Macros |
|--------|--------|-----------|--------|
| `/usr/include/unistd.h` | `IO.fs` | 80+ POSIX functions | File mode constants |
| `/usr/include/string.h` | `Memory.fs` | 40+ string/memory functions | - |
| `/usr/include/stdlib.h` | `Alloc.fs` | 70+ stdlib functions | EXIT_SUCCESS, EXIT_FAILURE, etc. |

Output is byte-identical across runs (deterministic).

## Testing

```bash
# Run the test suite (89 tests)
cd tests/Farscape.Tests && dotnet test

# Tests cover:
#   - CTypeParser: XParsec parsers for C types, macros, integers, arrays
#   - ActivePatterns: Type decomposition, macro classification, keyword quoting
#   - DeclarationAlgebra: Catamorphism, typedef/struct extraction, order preservation
#   - CodeRenderer: FsDecl → F# source for all declaration types
#   - FidelityCodeGenerator: End-to-end declaration → source generation
```

## License

Farscape is dual-licensed under both the Apache License 2.0 and a Commercial License.

### Open Source License

For open source projects, academic use, non-commercial applications, and internal tools, use Farscape under the **Apache License 2.0**.

### Commercial License

A Commercial License is required for incorporating Farscape into commercial products or services. See [Commercial.md](Commercial.md) for details.

### Patent Notice

Farscape generates BAREWire peripheral descriptors, which utilize technology covered by U.S. Patent Application No. 63/786,247 "System and Method for Zero-Copy Inter-Process Communication Using BARE Protocol". See BAREWire's [PATENTS.md](https://github.com/speakeztech/barewire/blob/main/PATENTS.md) for licensing details.
