# Farscape: TypeScript and OpenAPI Ingestion Roadmap

**SpeakEZ Technologies | Fidelity Framework**
**March 2026 | Horizon 2 Design Document**

## 1. Context

Farscape generates typed Clef bindings from C/C++ headers. This document extends Farscape's mandate to cover two additional foreign type systems: TypeScript declarations and OpenAPI specifications. The goal is a unified binding generation tool that produces Clef source from any external API surface, replacing Glutinum, Hawaii, and the compensatory pre/post-processing infrastructure currently maintained in Fidelity.CloudEdge.

This document is one half of the Horizon 2 plan. The companion document, *Composer JavaScript Backend*, describes how the Clef source produced here is compiled to deployable JavaScript. Both are required for Fidelity.CloudEdge to migrate from F#/Fable to native Clef.

### Horizon Context

**Horizon 1** (current, 8-12 days): Xantham replaces Glutinum within the existing F#/Fable pipeline. Fidelity.CloudEdge remains an F# project. The pre/post-processing burden is eliminated. This is tactical, delivering immediate value without architectural change.

**Horizon 2** (this document + companion): Farscape subsumes Xantham's domain (TypeScript) and Hawaii's domain (OpenAPI), producing Clef source. Composer's JavaScript backend compiles that Clef to deployable JavaScript/JSX. Fable exits the pipeline entirely. Fidelity.CloudEdge becomes a Clef project.

**Horizon 3** (future): Composer Transcribe absorbs foreign implementations. TypeScript library algorithms are comprehended and re-expressed in Clef for native compilation. Bindings give way to full ports where the computational intent justifies it.

### What This Replaces

| Current Tool | Role | Farscape Replacement |
|:-------------|:-----|:---------------------|
| Glutinum CLI | `.d.ts` → F# Fable bindings | TypeScript ingestion → `ClefDecl` → Clef source |
| Hawaii CLI | OpenAPI spec → F# HTTP clients | OpenAPI ingestion → `ClefDecl` → Clef source |
| `preprocess-typescript.js` | Cycle breaking, intersection truncation | Eliminated (Farscape handles cycles structurally) |
| `postprocess-runtime.sh` | Namespace injection, module name sanitization | Eliminated (Clef source generated correctly from the start) |
| `preprocess-openapi.sh` | Empty schema fixing, allOf flattening | Eliminated (Farscape handles spec normalization) |
| 8 Hawaii post-processor `.fsx` scripts | Discriminator DUs, type alias fixes, etc. | Eliminated (Farscape generates correct types directly) |

### Relationship to Xantham

Xantham's work is not discarded. The extraction layer (Xantham.Fable) is a Fable-compiled JavaScript module that calls the TypeScript Compiler API, which is exactly what Farscape's Phase 1 Pass 2 (tsc metadata extractor) requires. Xantham's schema (`Common.Types.fs`, 22 `TsType` cases, 32 `TsAstNode` cases) informs and validates Farscape's `TsDeclaration` types. The SimpleGenerator's experience with CircuitBreaker cycle-breaking, union decomposition, and type alias resolution translates directly to Farscape's binding generators.

Xantham serves as the proving ground in Horizon 1. Farscape absorbs the lessons in Horizon 2.

## 2. Architecture

### 2.1 The Four Patterns

Farscape's TypeScript and OpenAPI ingestion uses the same four functional programming patterns as the C/C++ pipeline:

| Pattern | C/C++ Headers | TypeScript Declarations | OpenAPI Specifications |
|:--------|:-------------|:-----------------------|:----------------------|
| XParsec Parsers | C type strings, macro values | TypeScript type expressions, generic constraints, mapped types | Schema `$ref` resolution, allOf/oneOf merging |
| Active Patterns | Type classification, keyword quoting | TS type classification, identifier sanitization, JS interop attribute inference | Schema classification, discriminator detection, status code patterns |
| Catamorphism | `Declaration` DU fold | `TsDeclaration` DU fold | `OpenApiSchema` DU fold |
| Typed Code AST | `ClefDecl` → Clef source | `ClefDecl` → Clef source (same AST, same renderer) | `ClefDecl` → Clef source (same AST, same renderer) |

### 2.2 Shared Infrastructure

All three ingestion domains share:

- **`CodeAST.fs`**: The `ClefDecl`, `NTUType`, `ClefExpr` types that represent generated Clef code as typed, inspectable, testable AST nodes.
- **`CodeRenderer.fs`**: The single `StringBuilder` in the entire codebase. Produces Clef source from `ClefDecl` trees. Configured per output target (native bindings vs. JS interop bindings).
- **Pilot project system**: `.pilot.toml` files that drive scoped, multi-namespace binding generation with type classification analysis (shared vs. local vs. orphan types).
- **Deterministic output**: Byte-identical generation across runs. No mutable `TransformContext`, no `ResizeArray`-dependent ordering.

### 2.3 TypeScript Ingestion Pipeline

```
TypeScript .d.ts File(s)
    ↓
[Pass 1: XParsec]           Parse .d.ts syntax → TsDeclaration AST
    ↓                        (pure Clef, self-hostable at Phase 3)
[Pass 2: tsc metadata]      TypeScript type checker → JSON metadata
    ↓                        (small Node.js script, disposable)
[Enrichment merge]           Resolve fully qualified names,
    ↓                        merge augmented interfaces,
                             instantiate utility types,
                             flag standard library origins
    ↓
[TsDeclaration AST]          Immutable, fold-compatible IR
    ↓
[Active Patterns]            Classify: StringEnum | TaggedUnion | CallableObject
    ↓                        Sanitize: TsNeedsQuoting | TsValidIdent
                             Infer: DefaultExport | NamedExport | GlobalValue
    ↓
[Catamorphism]               Single fold over TsDeclaration DU
    ↓
[JS Binding Generator]      TsDeclaration → ClefDecl
    ↓                        with [<JsImport>], [<JsInterface>],
                             [<JsStringEnum>], [<JsErase>] attributes
    ↓
[CodeRenderer]               ClefDecl → Clef source string
```

**Two output targets, one pipeline.** The same `TsDeclaration` AST can produce:

1. **JS interop bindings**: Clef source with `[<JsImport>]` attributes, compiled by Composer's JS backend to JavaScript that calls into existing JS libraries.
2. **Clef native types**: Clef source with native types, compiled by Composer's LLVM backend to native code. Used when the TypeScript types are being ported, not just bound.

The pipeline diverges at the code generation stage, not the parsing or classification stage.

### 2.4 OpenAPI Ingestion Pipeline

```
OpenAPI 3.x Specification (JSON/YAML)
    ↓
[Schema Reader]              Parse spec, resolve $ref chains
    ↓
[Schema Normalizer]          Fix empty schemas, flatten allOf,
    ↓                        generate operationIds, expand wildcards
    ↓
[OpenApiSchema AST]          Immutable, fold-compatible IR
    ↓
[Active Patterns]            Classify: Discriminator | Enum | FlatObject
    ↓                        Detect: MultipartForm | JsonBody | QueryParams
    ↓
[Catamorphism]               Single fold over OpenApiSchema DU
    ↓
[HTTP Client Generator]      OpenApiSchema → ClefDecl
    ↓                        Type definitions, async client methods,
                             serialization helpers
    ↓
[CodeRenderer]               ClefDecl → Clef source string
```

### 2.5 Self-Hosting Trajectory

Both pipelines follow the same three-phase self-hosting path:

**Phase 1**: Clef implementation using external tooling (tsc for TypeScript semantics, JSON parsing for OpenAPI specs). Farscape's four patterns provide the structure.

**Phase 2**: TypeScript parsing moves to XParsec (`.d.ts` files are a tractable subset: type declarations only, no expressions, no control flow). The tsc metadata pass becomes optional. OpenAPI parsing is already XParsec-tractable (JSON/YAML schema).

**Phase 3**: Entire pipeline self-hosted in Clef, running inside Composer. No Node.js, no external tooling. Distributed as Clef binaries.

### 2.6 Transition to Atelier Transpose

Farscape's binding generation capability is the foundation of the Transpose feature in Atelier (the Clef IDE). Transpose reads foreign library interfaces and generates Clef binding types that carry the full Fidelity machinery: NTU type widths, BAREWire memory layout contracts, escape analysis across the FFI boundary. Farscape retires as a standalone tool; its work continues as the Transpose capability within Composer, accessible through Atelier's editor interface.

The per-component decision generalizes: Transpose the infrastructure (API bindings, library interop), Transcribe the computation (algorithms, data transformations) where the type system and precision requirements justify the deeper commitment.

## 3. TypeScript Binding Generation for Fidelity.CloudEdge

### 3.1 Cloudflare Workers Runtime Bindings

Farscape ingests `@cloudflare/workers-types` (12,662 lines, ~727 types) and produces Clef source with JS interop attributes:

```
@cloudflare/workers-types (.d.ts)
    ↓ Farscape TypeScript ingestion
CloudEdge.Worker.Context.clef
    Contains: [<JsInterface>] type Request = ...
              [<JsInterface>] type Response = ...
              [<JsImport("fetch", "@cloudflare/workers")>] let fetch ...
              etc.
```

Cyclic references, intersection types, reserved keywords, module naming: all handled structurally by Farscape's pipeline, without pre/post-processing scripts.

### 3.2 Cloudflare Management API Clients

Farscape ingests the Cloudflare OpenAPI specification (8.3MB, 32 services) and produces Clef HTTP client source:

```
Cloudflare OpenAPI Spec (JSON)
    ↓ Farscape OpenAPI ingestion
CloudEdge.Management.Workers.clef
CloudEdge.Management.D1.clef
CloudEdge.Management.R2.clef
... (32 services)
    Contains: Type definitions, async client methods,
              discriminated unions from discriminator schemas,
              correctly sanitized type names
```

The 8 Hawaii post-processor scripts (`discriminators.fsx`, `auto-fix-types.fsx`, `jobject-multipart.fsx`, etc.) are eliminated. Farscape generates correct output directly.

### 3.3 Pilot Configuration

```toml
[library]
name = "cloudflare-workers"
entry = "node_modules/@cloudflare/workers-types/index.d.ts"
transitive_declarations = ["@types/node"]

[output]
mode = "js"
directory = "src/Runtime"
module_prefix = "Fidelity.CloudEdge"

[[namespace]]
name = "Fidelity.CloudEdge.Worker.Context"
description = "Cloudflare Workers runtime types"
declaration_patterns = ["cloudflare"]
```

## 4. Design Documents Required

The following documents need to be written to fully specify Farscape's TypeScript and OpenAPI ingestion:

1. **`TsDeclaration` Schema Specification**: Reconcile Xantham's `Common.Types.fs` (battle-tested against Cloudflare SDK) with Farscape's `TsTypes.fs` (designed from first principles in doc 13a). Produce a definitive schema that covers the full `.d.ts` type surface.

2. **JS Interop Attribute Vocabulary**: Define the complete set of Clef-native interop attributes (`[<JsImport>]`, `[<JsInterface>]`, `[<JsErase>]`, `[<JsStringEnum>]`, `[<JsEmit>]`, etc.) and their semantics. These are the contract between Farscape's binding output and Composer's JavaScript backend.

3. **OpenAPI Ingestion Specification**: Detailed design for schema resolution, service partitioning, discriminator DU synthesis, type name normalization, and HTTP client method generation. Absorbs the lessons from the 8 Hawaii post-processor scripts.

4. **Pilot Configuration for TypeScript and OpenAPI**: Extend the `.pilot.toml` format for TypeScript library and OpenAPI service scoping. Define namespace assignment, type classification, and output structure conventions.

5. **Migration Guide: Fidelity.CloudEdge from F# to Clef**: Step-by-step transition plan for converting Fidelity.CloudEdge's runtime and management layers from F#/Fable/Glutinum/Hawaii to Clef/Composer/Farscape.
