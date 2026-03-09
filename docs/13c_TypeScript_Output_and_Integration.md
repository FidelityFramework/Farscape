# TypeScript Ingestion — Output Targets & Integration

> **Part 3 of 3** — See also: [Architecture](./13a_TypeScript_Ingestion_Architecture.md) | [Fearless JavaScript](./13b_Fearless_JavaScript.md)

## Output Target 1: JavaScript/JSX Bindings

### What Gets Generated

For each TypeScript declaration, generate Clef code with JavaScript interop attributes:

| TypeScript | Clef JS Binding Output |
|-----------|-----------------|
| `interface Foo { bar: string }` | `[<JsInterface>] type Foo = abstract bar: string with get, set` |
| `type Color = "red" \| "blue"` | `[<JsStringEnum>] [<RequireQualifiedAccess>] type Color = \| Red \| Blue` |
| `function greet(name: string): void` | `[<JsImport("greet", "module")>] let greet (name: string): unit = jsNative` |
| `export default express` | `[<JsImportDefault("express")>] let express: Express.IExports = jsNative` |
| `class EventEmitter { on(event: string, cb: Function): this }` | `[<JsInterface>] type EventEmitter = abstract on: ...` |
| `foo(x: string \| number)` | `abstract foo: x: U2<string, float> -> unit` |
| `{ [key: string]: any }` | `[<JsIndexer>] abstract Item: key: string -> obj with get, set` |
| `type Partial<T>` | Resolved: all properties become optional |
| `type Record<K, V>` | Mapped to `Dictionary<K, V>` or generated interface |

> **Note on Clef interop attributes**: These are Clef-native attributes (`JsImport`, `JsInterface`, `JsStringEnum`, etc.) — not inherited from Fable. The attribute vocabulary is Clef's own, designed for Composer's JS backend. Composer understands these attributes and generates appropriate JavaScript interop code, following the same compilation model as js_of_ocaml's FFI bindings.

### ClefDecl Extensions for JS Bindings

The existing `ClefDecl` AST needs extensions for JavaScript interop constructs:

```fsharp
/// Extended ClefDecl variants for JavaScript interop bindings
type ClefDecl =
    // ... existing variants from CodeAST.fs ...

    /// Abstract interface type: type [<JsInterface>] Foo = abstract bar: string
    | AbstractInterface of name: string * members: ClefAbstractMember list
                         * typeParams: string list * inheritance: string list
                         * doc: string option * attributes: string list

    /// Erased union type: [<JsErase>] type MyUnion = | Case1 of T1 | Case2 of T2
    | ErasedUnion of name: string * cases: (string * NTUType) list
                   * attributes: string list

    /// Import binding: [<JsImport("name", "module")>] let x : T = jsNative
    | ImportBinding of name: string * importSpec: ImportSpec * bindingType: NTUType

and ClefAbstractMember =
    | AbstractProperty of name: string * propType: NTUType * accessor: Accessor
                        * isOptional: bool * isStatic: bool * attributes: string list
    | AbstractMethod of name: string * params': ClefParam list * returnType: NTUType
                      * typeParams: string list * isOptional: bool * isStatic: bool
                      * attributes: string list
    | AbstractIndexer of paramName: string * paramType: NTUType * returnType: NTUType
                       * isReadonly: bool * attributes: string list

and ImportSpec =
    | Named of selector: string * from: string
    | Default of from: string
    | All of from: string
    | Global

and Accessor = ReadOnly | WriteOnly | ReadWrite
```

### Cascading Generic Overloads

Glutinum's Express bindings demonstrate a pattern where TypeScript's generic defaults need cascading type aliases:

```typescript
// TypeScript
interface Request<P = ParamsDictionary, ResBody = any, ReqBody = any> { ... }
```

The generator produces cascading aliases so developers can specify partial generics:

```fsharp
type Request = Request<ParamsDictionary, obj, obj>
type Request<'P> = Request<'P, obj, obj>
type Request<'P, 'ResBody> = Request<'P, 'ResBody, obj>
type Request<'P, 'ResBody, 'ReqBody> = (* full definition *)
```

This is handled by detecting default type parameters and generating the alias chain.

## Output Target 2: Clef Native Source

### What Gets Generated

For Clef output, TypeScript declarations are translated to native Clef types without JavaScript runtime dependencies:

| TypeScript | Clef Output |
|-----------|-------------|
| `interface Foo { bar: string }` | `type Foo = { bar: string }` (record type) |
| `type Color = "red" \| "blue"` | `type Color = \| Red \| Blue` (real DU) |
| `enum Direction { Up, Down }` | `type Direction = \| Up = 0 \| Down = 1` (enum) |
| `type Result<T, E> = Success<T> \| Error<E>` | `type Result<'T, 'E> = \| Success of 'T \| Error of 'E` |
| `foo(x: string \| number)` | Overloaded functions or real DU parameter |
| `class EventEmitter` | Clef class/record with methods |

### Key Differences from JS Binding Output

| Aspect | JS Binding | Clef Native |
|--------|-----------|-------------|
| Union types | `U2<T1, T2>` (erased) | Real discriminated union |
| String enums | `[<StringEnum>]` attribute | Standard enum or DU |
| Imports | `[<Import>]` attribute | `open` module directive |
| Optional properties | `Option<T>` with JS semantics | `Option<T>` with Clef semantics |
| Classes | `[<AllowNullLiteral>]` abstract interface | Record type + companion module |
| Arrays | `ResizeArray<T>` | `array<T>` or `ImmutableArray<T>` |
| Function types | `Func<T1, ..., TN, TResult>` | Clef function type |
| `any` | `obj` | `obj` (with warning) |
| `never` | `obj` | `unit` (bottom type) |

## Service Partitioning (Pilot-Style)

For large TypeScript libraries (React, Express, AWS SDK), use Pilot-style TOML configuration:

```toml
[library]
name = "express"
entry = "node_modules/@types/express/index.d.ts"
# OR: entries from multiple .d.ts files
entries = [
    "node_modules/@types/express/index.d.ts",
    "node_modules/@types/express-serve-static-core/index.d.ts",
]
transitive_declarations = ["@types/node"]

[output]
mode = "js"                       # "js" or "clef"
directory = "../Bindings"
module_prefix = "Fidelity.Web"

[[namespace]]
name = "Fidelity.Web.Express"
description = "Express.js HTTP framework"
declaration_patterns = ["express", "express-serve-static-core"]
```

## Comparison: Glutinum vs. Farscape TypeScript Ingestion

| Aspect | Glutinum CLI | Farscape TS Ingestion |
|--------|-------------|----------------------|
| **Runtime** | Node.js (Fable-compiled) | Clef (self-hosted via Composer) |
| **TS Parsing** | TypeScript Compiler API (JS) | XParsec (Clef, no JS dependency) |
| **Intermediate AST** | GlueAST (mutable, TS-named) | TsDeclaration (immutable, fold-compatible) |
| **Output AST** | FSharpAST (F#/Fable-specific) | ClefDecl (shared with C binding, OpenAPI binding) |
| **Printer** | StringBuilder class with Indent/Unindent | CodeRenderer (single StringBuilder, typed AST input) |
| **Transform** | Mutable TransformContext, ResizeArray | Catamorphism, pure functions, immutable state |
| **Output targets** | Fable only | JS/JSX binding + Clef native |
| **Deterministic** | Not guaranteed | Byte-identical across runs |
| **Self-hosting** | Not possible (requires Node.js) | Designed for Clef self-hosting |

## Integration with Farscape

### Shared Infrastructure

| Component | C Headers | OpenAPI | TypeScript |
|-----------|-----------|---------|------------|
| `CodeAST.fs` | NTUType, ClefExpr, ClefDecl | Same types | Same types + JS binding extensions |
| `CodeRenderer.fs` | Renders Clef | Renders Clef | Renders Clef (configured per target) |
| Active Patterns | C type classification | Schema classification | TS type classification |
| XParsec | C type strings | $ref resolution | `.d.ts` syntax |
| Pilot | `.pilot.toml` for C SDKs | `.pilot.toml` for APIs | `.pilot.toml` for TS libraries |

### Module Layout

```
src/Farscape.Core/
  # Existing modules...

  # TypeScript ingestion modules
  TypeScript/
    TsTypes.fs               # TsDeclaration, TsType, TsMember
    TsDeclarationReader.fs   # .d.ts → TsDeclaration AST (XParsec-based)
    TsTypeResolver.fs        # Type reference resolution, augmentation merge
    TsActivePatterns.fs      # TS type classification, identifier sanitization
    TsDeclarationAlgebra.fs  # Catamorphism over TsDeclaration DU
    JsBindingGenerator.fs    # TsDeclaration → ClefDecl (JS/JSX interop bindings)
    ClefBindingGenerator.fs  # TsDeclaration → ClefDecl (Clef native types)
    TsServicePartitioner.fs  # Pilot-style library splitting

  # Shared (already exists)
  CodeAST.fs                 # Extended with AbstractInterface, ErasedUnion, ImportBinding
  CodeRenderer.fs            # Extended to render new ClefDecl variants
```

### CLI Extension

```bash
# Generate JS/JSX interop bindings from .d.ts
farscape typescript --entry node_modules/@types/express/index.d.ts \
                    --target js --output ./Bindings

# Generate Clef native types from .d.ts
farscape typescript --entry types.d.ts --target clef --output ./Types

# Generate with Pilot configuration
farscape typescript --pilot express.pilot.toml --output ./src/Web

# Discover modules in a .d.ts file
farscape typescript discover --entry index.d.ts
```

## Relationship to Existing Compilers

### Not Fable — js_of_ocaml Model

This design follows the **js_of_ocaml** model from the OCaml ecosystem, not the Fable model:

| Aspect | Fable | js_of_ocaml | Composer (Clef) |
|--------|-------|------------|----------------|
| **Approach** | Transpile F# AST → JS AST → JS source | Compile OCaml bytecode → JavaScript | Compile Clef → JavaScript (among other targets) |
| **Source language** | F# (requires FSharp.Compiler.Service) | OCaml (standard compiler) | Clef (self-hosted compiler) |
| **JS interop** | Fable-specific attributes (`[<Import>]`, `[<Erase>]`) | OCaml FFI bindings (`Js.t`, `Js.Unsafe`) | Clef interop attributes (`[<JsImport>]`, `[<JsErase>]`), typed boundary |
| **Runtime** | Fable runtime library in JS | OCaml runtime in JS | Clef runtime (minimal, platform-specific) |

Fable **transpiles** — it walks the F# AST and emits equivalent JavaScript constructs. js_of_ocaml **compiles** — it takes compiled OCaml output and produces JavaScript as a compilation target. Composer follows the js_of_ocaml approach: Clef is the language, JavaScript is one of several compilation targets, and the type system (NTU, memory regions, access kinds) is preserved through compilation rather than erased.

### What This Means for TypeScript Ingestion

Farscape's TypeScript ingestion generates **Clef source code** — not Fable bindings. The JS/JSX binding output target uses interop attributes (`[<JsImport>]`, `[<JsEmit>]`) that describe how to call into JavaScript. These are Clef-native interop constructs with clean `Js` prefixed naming — distinct from Fable's `[<Import>]` / `[<Emit>]`. They are compiled by Composer, which understands the JS backend semantics the same way js_of_ocaml's compiler understands its `Js.t` FFI types.

```
TypeScript .d.ts → Farscape → Clef source (with JS interop attributes) → Composer → JavaScript/Wasm
TypeScript .d.ts → Farscape → Clef source (native types)               → Composer → Native binary
```

There is no Fable in the pipeline. Composer compiles Clef to JavaScript the same way js_of_ocaml compiles OCaml to JavaScript — as a backend target, not as a transpilation strategy.

## Lessons from Glutinum

Glutinum's codebase reveals specific challenges that this design addresses:

### Type Literal Naming

Glutinum's `TypeLiteralsMemory` tracks generated names for anonymous type literals using a mutable counter. When an interface has an anonymous property type like `{ x: number; y: number }`, a name must be synthesized. Glutinum uses `parentScope + "_" + counter`.

Farscape solution: Deterministic naming from path position. The type literal at `Express.Request.body` gets named `Express_Request_body`. No counter, no mutable state, byte-identical across runs.

### Intersection Type Flattening

TypeScript's `A & B` (intersection) means "has all members of A and all members of B." Glutinum flattens these into member lists during the Read phase. This design preserves intersections in the `TsType` AST and resolves them during the Type Resolution phase, which has access to the full type registry.

### Module Augmentation

TypeScript allows re-opening interfaces across files (`declare module "express" { interface Request { ... } }`). Glutinum handles this via the `Merge.fs` module. This design handles it in `TsTypeResolver.mergeAugmentations`, which runs as a resolution pass after all declarations are read.

### Generic Default Cascading

Express's `Request<P = ParamsDictionary, ResBody = any, ReqBody = any>` pattern requires generating cascading type aliases. Glutinum handles this manually in specific binding packages. This design detects default type parameters and generates the alias chain automatically.

## Related Documents

- [Architecture](./13a_TypeScript_Ingestion_Architecture.md): Pipeline, types, and parsing strategy
- [Fearless JavaScript](./13b_Fearless_JavaScript.md): Clef superset constraints on JS expression
- [Architecture Overview](./01_Architecture_Overview.md): Farscape's four patterns
- [XParsec Architecture](./04_XParsec_Architecture.md): How XParsec is used throughout
- [Pilot Project Setup](./07_Pilot_Project_Setup.md): TOML-driven project system
- [OpenAPI Binding Generation](./12_OpenAPI_Binding_Generation.md): REST API binding generation (companion design)
