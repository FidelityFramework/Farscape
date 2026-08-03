# OpenAPI Binding Generation — Design Document

## Context

Farscape generates Clef bindings from C headers. This document extends Farscape's binding generation to cover **OpenAPI/REST API specifications**, producing strongly-typed Clef management clients for services like the Cloudflare API.

This design replaces the current approach of using [Hawaii](https://github.com/Zaid-Ajaj/Hawaii) (a capable but unmaintained F# OpenAPI client generator) augmented with a pipeline of 5+ postprocessors and a multi-pass bash preprocessor. The goal is a **single clean-pass transformer** that handles modern, large-scale OpenAPI specs natively — no pre/post processing.

### Why Not Just Fork Hawaii?

Hawaii is excellent pioneering work by Zaid Ajaj. However:

1. **Architectural mismatch**: Hawaii uses the full F# compiler AST (`FSharp.Compiler.Syntax`) + Fantomas formatting, which is heavyweight and couples to the .NET F# compiler. Farscape's `ClefDecl` typed AST + `CodeRenderer` is lighter and portable to Clef self-hosting.

2. **String-based normalization**: Hawaii uses ad-hoc string manipulation and regex for type name normalization, leading to the inconsistencies that require 5+ postprocessors. Farscape's XParsec + Active Pattern approach is structurally sound.

3. **No schema validation**: Hawaii silently falls back to `string` when it encounters schemas it can't handle (allOf compositions, missing operationIds, wildcard status codes). A clean-pass transformer should fail loudly or handle these natively.

4. **Self-hosting trajectory**: The Fidelity Framework and Clef language need a binding generation framework that can eventually run inside Composer. Hawaii's dependency on Fantomas, Microsoft.OpenApi.Readers (C#), and FSharp.Compiler.Service makes that impossible.

## Design Principles

### 1. Same Four Patterns as Farscape Core

| Pattern | C Header Binding | OpenAPI Binding |
|---------|------------------|-----------------|
| **XParsec Parsers** | C type strings, macro values | OpenAPI schema types, $ref resolution, allOf composition |
| **Active Patterns** | Type classification, keyword quoting | Schema classification, identifier sanitization, response pattern detection |
| **Catamorphism** | `Declaration` DU fold | `ApiDeclaration` DU fold |
| **Typed Code AST** | `ClefDecl` → Clef source | `ClefDecl` → Clef source (same AST, renderer-configured per target) |

### 2. Single-Pass Clean Transform

No preprocessors. No postprocessors. The transformer reads an OpenAPI spec and produces correct, compilable Clef in one pass. Every bug currently handled by external scripts is handled natively:

| Current External Fix | Clean-Pass Solution |
|---------------------|---------------------|
| `preprocess-openapi.sh` (allOf flattening) | Native allOf deep-merge during schema resolution |
| `preprocess-openapi.sh` (operationId generation) | Synthesize from path + method if missing |
| `auto-fix-types.fsx` (name normalization) | Single normalization function, applied once, used everywhere |
| `fix-dollar-identifiers.fsx` (backtick quoting) | Active Pattern: `(|NeedsQuoting|ValidIdent|)` applied at AST construction |
| `fix-list-separators.fsx` (formatting) | CodeRenderer handles list expression formatting correctly |
| `fix-jobject-query.fsx` (query param types) | Type mapper rejects complex types in query positions |
| `discriminators.fsx` (polymorphic types) | Native discriminator → DU synthesis |
| `missing-body-params.fsx` (body params) | Validate: every PUT/POST with requestBody gets a body parameter |
| `jobject-multipart.fsx` (JObject serialization) | Generate serialization calls inline for complex multipart fields |
| `fix-type-aliases.fsx` (normalization mismatch) | Eliminated — single normalization means no mismatches |

### 3. Large Spec Handling

Modern API specs (Cloudflare's is 30+ MB) require:

- **Streaming schema resolution**: Parse and resolve schemas on demand, not all at once
- **Pilot-style service partitioning**: Split a monolithic spec into service-scoped subsets using path patterns (analogous to Pilot's namespace prefixes)
- **Incremental generation**: Generate one service at a time, sharing a common type registry

### 4. Deterministic Output

Same guarantee as Farscape Core: byte-identical generation across runs. No hash-dependent ordering, no mutable state in the pipeline.

## Architecture

### Pipeline

```
OpenAPI Spec (JSON/YAML)
  |
  v
[Schema Reader]                    -- Parse JSON/YAML into OpenApiDocument
  |                                   (XParsec for $ref resolution, allOf merge)
  v
[Service Partitioner]              -- Split by path patterns (Pilot-style)
  |                                   Produces per-service ApiSpec
  v
[Schema Resolver]                  -- Resolve $ref chains, flatten allOf/oneOf/anyOf
  |                                   Deep-merge compositions into named schemas
  |                                   Detect discriminator patterns → tag for DU synthesis
  v
[ApiDeclaration AST]               -- Typed intermediate representation
  |                                   SchemaType | Operation | Parameter | Response
  v
[Active Patterns]                  -- Classify: EnumSchema | RecordSchema | PrimitiveAlias
  |                                   Identify: NeedsQuoting | ValidIdent
  |                                   Detect: EnvelopeResponse | RawResponse | PolymorphicResponse
  v
[Catamorphism]                     -- Single fold over ApiDeclaration DU
  |                                   Produces: type definitions, client methods, response DUs
  v
[ClefDecl AST]                     -- Same typed AST as Farscape Core
  |                                   (reused: NTUType, ClefExpr, ClefDecl, ClefModule)
  v
[CodeRenderer]                     -- ClefDecl → Clef source string
                                      (same StringBuilder-only renderer, configured per target)
```

### Core Types

```fsharp
/// Intermediate representation for OpenAPI schemas
type SchemaType =
    | Primitive of PrimitiveKind          // string, int, float, bool, etc.
    | Enum of name: string * values: string list
    | Record of name: string * fields: FieldDef list
    | Array of elementType: SchemaType
    | Map of valueType: SchemaType
    | TypeAlias of name: string * target: SchemaType
    | DiscriminatedUnion of name: string * cases: DUCase list
    | FreeForm                             // obj fallback (unstructured JSON)

and FieldDef = {
    name: string
    fieldType: SchemaType
    required: bool
    docs: string option
}

and DUCase = {
    caseName: string
    discriminatorValue: string
    payloadType: SchemaType
}

/// Intermediate representation for API operations
type ApiDeclaration =
    | TypeDef of SchemaType
    | Operation of OperationDef
    | ResponseDU of ResponseDef

and OperationDef = {
    operationId: string
    httpMethod: HttpMethod
    path: string
    parameters: ParamDef list
    requestBody: RequestBodyDef option
    responses: (int * SchemaType option) list
    docs: string option
}

and ParamDef = {
    name: string
    location: ParamLocation              // Path | Query | Header
    paramType: SchemaType
    required: bool
}

and RequestBodyDef = {
    contentType: ContentType
    schema: SchemaType
    required: bool
}

and ResponseDef = {
    operationId: string
    cases: (string * SchemaType option) list   // ("OK", Some type) | ("BadRequest", Some type)
}
```

### Schema Resolution (XParsec-Based)

Instead of relying on Microsoft.OpenApi.Readers (a C# library with limited allOf support), use XParsec combinators to resolve schemas:

```fsharp
/// Resolve a $ref path to its target schema
let resolveRef (root: JsonNode) (refPath: string) : JsonNode option =
    // Parse "#/components/schemas/foo" into path segments
    // Walk the JSON tree following each segment
    // Return the target node or None

/// Deep-merge allOf members into a single schema
let mergeAllOf (root: JsonNode) (members: JsonNode list) : SchemaType =
    // Resolve any $ref in members
    // Collect all properties from all members
    // Merge required arrays
    // Handle conflicts (last wins, with warning)
    // Return merged Record schema

/// Classify a schema node into SchemaType
let rec classifySchema (root: JsonNode) (node: JsonNode) : SchemaType =
    match node with
    | HasAllOf members -> mergeAllOf root members
    | HasOneOf members -> classifyOneOf root members
    | HasRef refPath ->
        match resolveRef root refPath with
        | Some target -> classifySchema root target
        | None -> FreeForm
    | HasEnum values -> Enum (extractName node, values)
    | HasProperties props -> Record (extractName node, classifyFields root props)
    | HasDiscriminator disc -> synthesizeDU root disc node
    | IsArray itemSchema -> Array (classifySchema root itemSchema)
    | IsPrimitive kind -> Primitive kind
    | _ -> FreeForm
```

### Identifier Sanitization (Active Patterns)

Single, consistent sanitization applied once during AST construction:

```fsharp
/// Classify whether an OpenAPI name needs backtick quoting
let (|NeedsQuoting|ValidIdent|) (name: string) =
    if containsSpecialChars name || isFSharpKeyword name then
        NeedsQuoting name
    else
        ValidIdent name

/// Normalize an OpenAPI schema name to a consistent Clef identifier
let normalizeTypeName (name: string) : string =
    // Single function. Applied once. No inconsistencies.
    name
    |> replaceInvalidChars          // $ → Dollar_, + → Plus_
    |> applyQuotingIfNeeded         // hyphens → backtick-quoted

/// Active pattern for response envelope detection
let (|CloudflareEnvelope|RawResponse|) (schema: SchemaType) =
    match schema with
    | Record (_, fields) when hasFields ["success"; "errors"; "result"] fields ->
        CloudflareEnvelope (findField "result" fields)
    | _ -> RawResponse schema
```

### Catamorphism

```fsharp
/// The algebra for folding over ApiDeclaration
type ApiAlgebra<'a> = {
    onTypeDef: SchemaType -> 'a
    onOperation: OperationDef -> 'a
    onResponseDU: ResponseDef -> 'a
}

/// Single canonical fold — the ONLY traversal over declarations
let foldApi (algebra: ApiAlgebra<'a>) (declarations: ApiDeclaration list) : 'a list =
    declarations |> List.map (fun decl ->
        match decl with
        | TypeDef schema -> algebra.onTypeDef schema
        | Operation op -> algebra.onOperation op
        | ResponseDU resp -> algebra.onResponseDU resp)
```

### Service Partitioning (Pilot-Style)

Reuse Pilot's TOML-driven namespace scoping concept for OpenAPI service splitting:

```toml
# services.pilot.toml — analogous to .pilot.toml for C headers

[spec]
source = "https://raw.githubusercontent.com/cloudflare/api-schemas/main/openapi.json"
cache_hours = 24

[[service]]
name = "D1"
namespace = "Fidelity.CloudEdge.Management.D1"
path_patterns = ["/accounts/{account_id}/d1/*"]
client_name = "D1Client"

[[service]]
name = "R2"
namespace = "Fidelity.CloudEdge.Management.R2"
path_patterns = ["/accounts/{account_id}/r2/*"]
client_name = "R2Client"

[[service]]
name = "Workers"
namespace = "Fidelity.CloudEdge.Management.Workers"
path_patterns = [
    "/accounts/{account_id}/workers/*",
    "/zones/{zone_id}/workers/*"
]
client_name = "WorkersClient"
```

This replaces the current `services.json` + `extract-service.sh` + Hawaii config file combination with a single declarative file.

### HTTP Library Generation

Instead of Hawaii's approach of copying a static `OpenApiHttp.fs` template, generate the HTTP library from the same `ClefDecl` AST:

```fsharp
/// Generate the RequestPart DU and OpenApiHttp module
let generateHttpLibrary (config: GenerationConfig) : ClefDecl list =
    // RequestPart union: Query | Path | Header | JsonContent | ...
    // OpenApiHttp module: getAsync, postAsync, putAsync, deleteAsync, patchAsync
    // Serializer module: serialize, deserialize (using configured serialization)
    // All generated as ClefDecl nodes, not copied from a template file
```

This means the HTTP library adapts to the compilation target:
- **Clef (native)**: Fidelity HTTP primitives, BAREWire serialization
- **Clef → JS (via Composer)**: Fetch API, JSON serialization (js_of_ocaml model)
- **Clef → Wasm**: Fidelity HTTP over WASI

## Integration with Farscape

### Shared Infrastructure

| Component | Farscape Core | OpenAPI Extension |
|-----------|---------------|-------------------|
| `CodeAST.fs` | NTUType, ClefExpr, ClefDecl | Same types, reused directly |
| `CodeRenderer.fs` | Renders Clef source | Renders Clef source (minor config differences) |
| Active Patterns | Type classification for C types | Schema classification for OpenAPI types |
| XParsec | C type parsing | $ref resolution, schema composition |
| Pilot | `.pilot.toml` for C SDKs | `.pilot.toml` for OpenAPI services |

### Module Layout

```
src/Farscape.Core/
  # Existing C header modules...
  CppParser.fs
  CTypeParser.fs
  ActivePatterns.fs
  DeclarationAlgebra.fs
  ...

  # New OpenAPI modules
  OpenApi/
    OpenApiTypes.fs           # SchemaType, ApiDeclaration, OperationDef
    OpenApiParser.fs          # JSON/YAML → OpenApiDocument (XParsec-based)
    SchemaResolver.fs         # $ref resolution, allOf merge, discriminator detection
    OpenApiActivePatterns.fs  # Schema classification, identifier sanitization
    OpenApiAlgebra.fs         # Catamorphism over ApiDeclaration
    ClientGenerator.fs        # ApiDeclaration → ClefDecl (client methods)
    TypeGenerator.fs          # SchemaType → ClefDecl (type definitions)
    HttpLibraryGenerator.fs   # Generate OpenApiHttp module
    ServicePartitioner.fs     # Pilot-style service splitting

  # Shared (already exists)
  CodeAST.fs
  CodeRenderer.fs
  PilotTypes.fs              # Extended with OpenAPI service definitions
  PilotSerializer.fs         # Extended for OpenAPI TOML
```

### CLI Extension

```bash
# Generate from OpenAPI spec
farscape openapi --spec cloudflare-openapi.json --output ./generated

# Generate with Pilot service partitioning
farscape openapi --pilot services.pilot.toml --output ./src/Management

# Discover services in an OpenAPI spec
farscape openapi discover --spec openapi.json

# Generate single service
farscape openapi --pilot services.pilot.toml --service d1 --output ./generated
```

## Lessons Learned Catalog

The following bugs were discovered during Fidelity.CloudEdge's migration from raw Hawaii to the postprocessor pipeline. Each represents a requirement for the clean-pass transformer.

### Schema Resolution Bugs

| Bug | Root Cause | Clean-Pass Requirement |
|-----|-----------|----------------------|
| allOf compositions produce `string` payloads | Hawaii can't deep-merge $ref + inline properties | `SchemaResolver.mergeAllOf` handles recursive resolution |
| Discriminator types not synthesized as DUs | Hawaii ignores OpenAPI `discriminator` field | `SchemaResolver.synthesizeDU` detects and generates DUs |
| Missing operationId causes crash | Hawaii requires operationId, OpenAPI makes it optional | Synthesize from path + method: `getAccountsWorkers` |
| Wildcard status codes (`4XX`) crash | Hawaii expects concrete codes | Map wildcards: `4XX` → `400`, `5XX` → `500` |
| Empty schema in content types | Malformed spec, Hawaii crashes | Default to `FreeForm` (`obj`) with warning |

### Type Name Normalization Bugs

| Bug | Root Cause | Clean-Pass Requirement |
|-----|-----------|----------------------|
| Definition says `workersfoo` but reference says `workers_foo` | Different normalization for defs vs refs | Single `normalizeTypeName` function, applied once |
| `$metadata` not backtick-quoted | Hawaii doesn't detect `$` as special | `(|NeedsQuoting|ValidIdent|)` active pattern |
| `Gre+icmp` DU case not renamed | Only type defs scanned, not DU cases | Scan ALL identifiers for invalid chars |
| Keyword stubs (`type when = string`) | False-positive type reference detection | Comprehensive keyword/exclusion set in resolver |

### Code Generation Bugs

| Bug | Root Cause | Clean-Pass Requirement |
|-----|-----------|----------------------|
| `RequestPart` items on same line without separator | Hawaii formatting bug | CodeRenderer handles list expressions correctly |
| JObject passed to `RequestPart.query` | Complex types in query position | Type mapper rejects complex query params |
| Missing body parameter on PUT/POST | Hawaii omits requestBody | Validate: PUT/POST + requestBody = body param |
| JObject multipart not serialized | Hawaii doesn't synthesize `.ToString()` | Generate serialization inline for complex multipart |

## Self-Hosting Trajectory

This design is intentionally structured for eventual self-hosting in Clef:

1. **No Fantomas dependency**: CodeRenderer is a simple StringBuilder walker, not a full formatter
2. **No Microsoft.OpenApi dependency**: XParsec-based schema parsing replaces the C# library
3. **No FSharp.Compiler.Service**: ClefDecl typed AST replaces SynType/SynExpr
4. **Pure functional pipeline**: No mutable state except final file I/O
5. **TOML configuration**: Already supported in Clef via Fidelity libraries

The entire OpenAPI → Clef pipeline is designed to self-host: a Clef program generating Clef source, running inside Composer.

## Relationship to Existing Work

- **Hawaii** (Zaid Ajaj): Pioneered OpenAPI client generation using discriminated unions for response types. This design builds on that key insight while replacing Hawaii's implementation with Farscape's architectural patterns.

- **Fidelity.CloudEdge**: Current consumer. The postprocessor pipeline developed for CloudEdge serves as the comprehensive bug catalog and test suite for this design.

- **Farscape Core**: Provides the four architectural patterns, the typed code AST, and the Pilot project system. OpenAPI generation is a natural extension of the same pipeline.

- **TypeScript Ingestion** ([Architecture](./13a_TypeScript_Ingestion_Architecture.md) | [Fearless JavaScript](./13b_Fearless_JavaScript.md) | [Output & Integration](./13c_TypeScript_Output_and_Integration.md)): Companion design using the same four patterns for TypeScript `.d.ts` ingestion.
