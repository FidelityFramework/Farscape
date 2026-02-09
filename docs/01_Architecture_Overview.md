# Farscape Architecture Overview

## Purpose

Farscape generates F# bindings from C/C++ headers for the Fidelity native compilation ecosystem. Unlike traditional FFI tools that target runtime interop, Farscape generates code specifically for ahead-of-time native compilation via Firefly.

## Design Principles

### 1. Clang-Powered Parsing with XParsec Post-Processing

Farscape uses **clang** for robust C/C++ header parsing via a two-pass strategy (JSON AST + macro extraction). XParsec parser combinators handle **post-processing** — decomposing C type strings, classifying macro values, and parsing numeric literals from clang's output.

### 2. Four Architectural Patterns

Every module in Farscape is built from one of four patterns:

| Pattern | Module | Purpose |
|---------|--------|---------|
| **XParsec Parsers** | `CTypeParser.fs` | Decompose C type strings, parse macros and numeric literals |
| **Active Patterns** | `ActivePatterns.fs` | Classify types, filter macros, quote F# keywords |
| **Catamorphism** | `DeclarationAlgebra.fs` | Single fold over Declaration DU — the ONLY traversal |
| **Typed Code AST** | `CodeAST.fs` + `CodeRenderer.fs` | FsDecl tree → F# source (the ONLY StringBuilder) |

### 3. Fidelity-First Output

Output is designed for Firefly native compilation, not .NET runtime interop:

- Generates `Unchecked.defaultof<T>` stubs that Alex recognizes
- Alex provides platform-specific MLIR implementations for these bindings
- No BCL dependencies in generated code
- Also supports P/Invoke mode for traditional .NET interop

### 4. Deterministic Output

Generated F# source is byte-identical across runs. No hash-dependent ordering, no mutable state in the generation pipeline.

## Pipeline Architecture

```
                         Farscape Pipeline
 ┌────────────────────────────────────────────────────────────────────┐
 │                                                                    │
 │  C/C++ Header ──► Clang Two-Pass ──► Declaration AST              │
 │  (stdlib.h)       (CppParser.fs)     (functions, structs,         │
 │                                       enums, typedefs, macros)    │
 │                                           │                       │
 │              ┌────────────────────────────┤                       │
 │              ▼                            ▼                       │
 │   XParsec Post-Processing          TypeMapper.fs                  │
 │   (CTypeParser.fs)                 (type dictionary)              │
 │              │                            │                       │
 │              ▼                            │                       │
 │   Active Patterns                        │                       │
 │   (ActivePatterns.fs)                    │                       │
 │              │                            │                       │
 │              └────────────┬───────────────┘                       │
 │                           ▼                                       │
 │              Catamorphism (DeclarationAlgebra.fs)                  │
 │              Single fold over Declaration DU                      │
 │                           │                                       │
 │                           ▼                                       │
 │              FidelityCodeGenerator.fs                              │
 │              Declaration list → FsDecl AST                        │
 │                           │                                       │
 │                           ▼                                       │
 │              CodeRenderer.fs                                      │
 │              FsDecl → F# source string                            │
 │              (the ONLY StringBuilder)                              │
 │                                                                    │
 └────────────────────────────────────────────────────────────────────┘
```

## Core Modules

### CppParser.fs — Clang Two-Pass Parsing

Parses C/C++ headers using clang in two passes:

1. **Pass 1 (JSON AST)**: `clang -Xclang -ast-dump=json` extracts functions, structs, enums, typedefs, classes, namespaces
2. **Pass 2 (Macro extraction)**: `clang -dM -E` extracts `#define` macros

Produces a `Declaration` discriminated union:

```fsharp
type Declaration =
    | Function of FunctionDecl
    | Struct of StructDecl
    | Enum of EnumDecl
    | Typedef of TypedefInfo
    | Macro of MacroDecl
    | Namespace of NamespaceDecl
    | Class of ClassDecl
```

### CTypeParser.fs — XParsec Post-Processing

XParsec parsers for C type strings and macro values, using the generic class pattern:

```fsharp
type Parsers<'Input, 'InputSlice ...>() =
    static let pQualifier = choice [ stringReturn "const " (); ... ]
    static let pCType =
        parser {
            do! skipMany pQualifier
            let! firstWord = pTypeWord
            let! restWords = many (spaces1 >>. pTypeWord)
            let! stars = many (skipChar '*')
            return { BaseType = ...; PointerDepth = stars.Length }
        }
    static member CType = pCType

type private P = Parsers<ReadableString, ReadableStringSlice>
```

Provides four public API functions:
- `tryParseCType` — C type string → `CTypeInfo` (base type + pointer depth)
- `parseMacroLine` — `#define` line → `MacroDecl`
- `tryParseInteger` — decimal/hex string → `int64`
- `tryParseArraySize` — `"uint32_t[4]"` → `4`

### ActivePatterns.fs — Type Classification

Active patterns backed by XParsec parsers:

```fsharp
// C type decomposition
let (|ParsedCType|_|) (s: string) : CTypeInfo option = CTypeParser.tryParseCType s

let (|CharPointer|VoidPointer|TypedPointer|ValueType|) (info: CTypeInfo) =
    if info.PointerDepth > 0 then
        if info.BaseType.EndsWith("char") then CharPointer
        elif info.BaseType = "void" then VoidPointer
        else TypedPointer info.BaseType
    else ValueType info.BaseType

// Macro classification
let (|CompilerBuiltin|InternalMacro|PredefinedMacro|UserMacro|) (name: string) = ...

// Numeric parsing
let (|IntegerLiteral|_|) (s: string) : int64 option = CTypeParser.tryParseInteger s

// F# keyword quoting
let (|FSharpKeyword|CleanName|) (name: string) = ...
```

### DeclarationAlgebra.fs — Catamorphism

A fold algebra over the Declaration DU — the single, canonical traversal:

```fsharp
type DeclarationAlgebra<'R> = {
    OnFunction:  FunctionDecl  -> 'R
    OnStruct:    StructDecl    -> 'R
    OnEnum:      EnumDecl      -> 'R
    OnTypedef:   TypedefInfo   -> 'R
    OnMacro:     MacroDecl     -> 'R
    OnNamespace: NamespaceDecl -> 'R
    OnClass:     ClassDecl     -> 'R
}

let cataDeclarations (algebra: DeclarationAlgebra<'R>) (decls: Declaration list) : 'R list =
    decls |> List.map (fun decl ->
        match decl with
        | Declaration.Function f -> algebra.OnFunction f
        | Declaration.Struct s   -> algebra.OnStruct s
        | ...)
```

Pre-built algebras: `typedefAlgebra` (extract typedef pairs), `structNameAlgebra` (extract struct names).

### CodeAST.fs — Typed Code AST

Typed representation of generated F# code:

```fsharp
type FsType = Named of string | Generic of string * FsType | Unit
type FsExpr = DefaultOf of FsType
type FsDecl =
    | Module of name: string * comment: string * decls: FsDecl list
    | XmlDoc of text: string
    | LetBinding of name: string * params': FsParam list * returnType: FsType * body: FsExpr
    | LiteralBinding of name: string * value: string
    | RecordType of name: string * fields: (string * FsType) list * doc: string option
    | EnumType of name: string * values: (string * int64) list * doc: string option
    | Comment of text: string
    | BlankLine
```

### CodeRenderer.fs — Single Renderer

The ONLY `StringBuilder` in Farscape. Converts `FsDecl` tree to F# source string:

```fsharp
let render (decl: FsDecl) : string =
    let sb = StringBuilder()
    renderDecl sb 0 decl
    sb.ToString().TrimEnd() + "\n"
```

### FidelityCodeGenerator.fs — Orchestration

Wires everything together using one catamorphism pass:

1. Build typedef resolution map (catamorphism + pure recursive chain resolution)
2. Categorize declarations via `generationAlgebra` (one pass)
3. Assemble categories into `FsDecl` list
4. Wrap in `FsDecl.Module`
5. Render via `CodeRenderer.render`

### TypeMapper.fs — Type Dictionary

Pure data mapping from C type names to F# type names. No parsing logic — just a dictionary lookup:

```fsharp
let getFSharpType (cType: string) : string =
    match cType with
    | "int" | "int32_t" -> "int32"
    | "unsigned int" | "uint32_t" -> "uint32"
    | "char" | "signed char" -> "byte"
    | "void" -> "unit"
    | ...
```

## Output Modes

### Fidelity Mode

Generates `Unchecked.defaultof` stubs for Firefly/Alex consumption:

```fsharp
module Fidelity.libc.Memory

// Generated by Farscape — Fidelity binding for libc
// Alex provides platform-specific MLIR implementations for these bindings.

    /// void * memcpy(void *restrict __dest, const void *restrict __src, size_t __n)
    let memcpy (dest: nativeint) (src: nativeint) (n: nativeint) : nativeint =
        Unchecked.defaultof<nativeint>
```

### P/Invoke Mode

Traditional .NET P/Invoke bindings with DllImport attributes (via `CodeGenerator.fs`).

## File Compile Order

```xml
<Compile Include="ProjectOptions.fs" />
<Compile Include="Types.fs" />
<Compile Include="CppParser.fs" />
<Compile Include="TypeMapper.fs" />
<Compile Include="CTypeParser.fs" />
<Compile Include="ActivePatterns.fs" />
<Compile Include="DeclarationAlgebra.fs" />
<Compile Include="CodeAST.fs" />
<Compile Include="CodeRenderer.fs" />
<Compile Include="FidelityCodeGenerator.fs" />
<Compile Include="MemoryManager.fs" />
<Compile Include="DelegatePointer.fs" />
<Compile Include="CodeGenerator.fs" />
<Compile Include="Project.fs" />
<Compile Include="BindingGenerator.fs" />
```

Key constraint: `CppParser.fs` compiles before `CTypeParser.fs` — the XParsec parsers are in a separate module downstream of the parser types.

## Current Limitations

1. **No BAREWire descriptors** — awaiting BAREWire hardware descriptor types
2. **No `[<FidelityExtern>]` attributes** — binding metadata not yet carried through pipeline
3. **No quotation output** — planned for PSG recognition patterns (quotation semantic carriers)
4. **No C++ class/template support** — class declarations parsed but not generated
5. **No CMSIS qualifier extraction** — `__I`/`__O`/`__IO` not yet mapped to `AccessKind`

## Related Documents

- [BAREWire Integration](./02_BAREWire_Integration.md) — Hardware descriptor generation design
- [fsnative Integration](./03_fsnative_Integration.md) — Native type system coordination
- [XParsec Architecture](./04_XParsec_Architecture.md) — How Farscape uses XParsec throughout
