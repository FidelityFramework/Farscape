# Considering a Type Provider for Clef and Fidelity Framework

**SpeakEZ Technologies | Fidelity Framework**
**February 2026 | Architectural Analysis Memo**

---

## 1. Context and Motivation

The F# ecosystem provides type providers through libraries like FSharp.Data, offering compile-time type generation from external data sources: JSON, XML, CSV, HTML, and others. Type providers infer schemas from sample documents and generate F# types that provide IntelliSense, type-checked field access, and serialization, all without manually defining record types or writing parsing code. Recent additions to this ecosystem include TOML support, contributed in part through work that originated from Fidelity.TOML development.

Fidelity Framework's Composer toolchain introduces two capabilities for foreign code integration: **Transcribe** (full algorithmic port from C/C++/Rust/Python to Clef) and **Transpose** (typed dynamic binding that wraps foreign library interfaces with NTU type widths, BAREWire memory contracts, escape analysis, and lifetime inference). These subsume and eventually retire Farscape as a standalone binding generator.

The question this memo addresses: **does Clef need a third capability, analogous to F#'s type providers, specifically for ingesting structured data formats?** Or does the Transcribe/Transpose architecture, combined with BAREWire's schema descriptor system, absorb this use case?

The answer is nuanced. Three distinct tiers of data schema integration exist, each with different architectural requirements and different relationships to Fidelity's zero-runtime executable model.

---

## 2. What Type Providers Actually Do

F#'s type providers serve a specific function that is distinct from both Transcribe and Transpose.

Transcribe ingests *algorithms*: computational logic expressed in a foreign language, comprehended and re-expressed in Clef. Transpose ingests *library interfaces*: API surfaces from foreign libraries, wrapped with Fidelity's type machinery. Type providers ingest *data schemas*: structural descriptions of documents, configuration files, or data streams, from which typed accessor code is generated.

The input to a type provider is not code in any language. It is a sample document, a schema definition, or a live endpoint that returns structured data. There is no computation to comprehend and no library to bind. There is structure to infer and typed access to generate.

In the .NET world, type providers operate through compiler integration. An erased type provider generates types that exist at compile time for IntelliSense and type checking but disappear at runtime (the underlying representation is untyped). A generative type provider emits real .NET types that persist into the compiled assembly. Both mechanisms depend on the .NET runtime's type system and reflection capabilities.

Fidelity Framework explicitly eliminates the .NET CLR and BCL. There is no runtime type system, no reflection, no dynamic type generation. This constraint does not eliminate the need for typed access to structured data, but it fundamentally reshapes how that access is provided.

---

## 3. Tier 1: Compile-Time Known Schemas

### 3.1 The Solved Case

When the data schema is known before compilation, no type provider machinery is needed. The schema is defined as Clef types. The parser validates conformance. The typed accessor is statically compiled.

Fidelity.TOML exemplifies this tier. The `.fidproj` file has a known structure. The Fidelity.TOML library provides both deserialization (reading TOML into typed Clef values) and serialization (writing typed Clef values back to TOML). The BAREWire schema descriptor for the TOML structure is defined at compile time. The in-memory representation is a BAREWire region with typed field access at statically known offsets.

This pattern extends to every format where the schema is established before compilation:

| Format | Library | Schema Source |
|---|---|---|
| TOML | Fidelity.TOML | `.fidproj`, application config |
| JSON | Fidelity.JSON | API contracts, OpenAPI specs |
| XML | Fidelity.XML | XSD, WSDL, known document types |
| CSV | Fidelity.CSV | Known column headers and types |
| YAML | Fidelity.YAML | Kubernetes manifests, CI configs |

Each format library provides bidirectional serialization, BAREWire schema descriptors, and typed access. No runtime flexibility is needed or wanted. The compile-time type system provides full safety.

### 3.2 Architectural Characteristics

Tier 1 libraries share common properties:

- **Schema is compile-time data.** The BAREWire schema descriptor is a constant embedded in the compiled binary.
- **Accessors are direct offset reads.** Field access compiles to pointer arithmetic at known offsets. No indirection, no type tag dispatch.
- **Validation is parse-time.** A non-conforming document produces a typed `Result` error during parsing. Once parsed, every access is guaranteed safe by construction.
- **No runtime dependency.** The compiled binary contains the parser, the schema, and the accessors. Nothing else is needed.

This is solved territory. The remaining question is whether the Tier 1 format libraries share enough infrastructure to warrant a common `Fidelity.Format` or similar base library, or whether each format library stands alone. The BAREWire schema descriptor system provides the natural shared infrastructure: each format library produces BAREWire-encoded data with format-specific parsing and format-generic typed access.

---

## 4. Tier 2: Runtime-Discovered, Structurally Constrained Schemas

### 4.1 The Problem

A user uploads a CSV file. The application does not know at compile time what columns the file contains, what types those columns hold, or how many rows are present. An API returns a JSON document whose structure varies by endpoint version. An XML document arrives without an accompanying XSD. The schema is unknown until runtime.

In F#, type providers address this by generating types from sample documents at compile time, creating the illusion that runtime-variable data has the same typed access as compile-time-known data. The illusion is effective but depends on compiler magic and .NET runtime support that Fidelity does not provide.

The question: can Fidelity handle this case without a runtime, without dynamic types, and without a JIT compiler?

### 4.2 The Key Observation: Finite Type Vocabularies

CSV is not truly unconstrained. It is always tabular: rows of uniform-length tuples, with a finite vocabulary of column types. A CSV column is one of: string, integer, floating-point number, boolean, date/time, or null. The *shape* of a CSV is always the same (rectangular table); only the *parameterization* varies (how many columns, what types, what names).

JSON has the same property. Its type vocabulary is closed: string, number, boolean, null, array, object. Every JSON document, regardless of complexity, is composed entirely of these six value types.

XML adds attributes, namespaces, and mixed content, but its value vocabulary is still finite. TOML is simpler than JSON. YAML is approximately JSON with reference semantics. Markdown front matter is YAML or TOML.

**For any data interchange format with a finite, closed type vocabulary, runtime schema discovery does not require runtime type generation.** The types already exist in the compiled binary. What varies at runtime is which types appear at which positions, and this variability can be captured as a runtime *value* (a schema descriptor) that parameterizes statically compiled accessor *code*.

### 4.3 Architecture: Schema Descriptors as Data

The design centers on a distinction between schema descriptors (runtime-constructed values with compile-time-known layout) and data regions (flat byte buffers whose interpretation depends on the schema descriptor).

**Schema descriptor.** A `CsvSchema` (or `JsonSchema`, `XmlSchema`, etc.) is a runtime-constructed value, not a compile-time type. For CSV:

```fsharp
type ColType =
    | StringCol
    | IntCol
    | FloatCol
    | BoolCol
    | DateCol
    | NullableOf of ColType

type FieldDescriptor = {
    Name: string
    ColType: ColType
    Offset: uint32
    Size: uint16
    Alignment: uint8
}

type CsvSchema = {
    Fields: FieldDescriptor array
    RowStride: uint32
    RowCount: uint32
}
```

The `CsvSchema` type itself has a compile-time-known BAREWire layout. It can be stored, transmitted, compared, and validated using statically compiled code. The schema descriptor is data with a fixed structure that describes a variable structure.

**Schema inference.** A schema inference pass reads the header row and samples N data rows, applies heuristics (does this column parse as integer? as float? as date?), and produces a `CsvSchema` value. This inference code is statically compiled. It examines runtime data and produces a runtime value. No code generation occurs.

**Data region.** The parsed CSV data lives in a BAREWire memory region. The region is a flat byte buffer with a known base address, typed as `BAREWireRegion` (a base pointer plus a length). The layout of data within the region is determined by the schema descriptor: column offsets, type widths, row stride. The region itself is a compile-time-known type; its *contents* are interpreted according to the runtime-discovered schema.

**Accessor function.** The accessor is statically compiled, parameterized by the schema descriptor:

```fsharp
let readField (schema: CsvSchema) (region: BAREWireRegion) (row: int) (col: int) : CsvValue =
    let field = schema.Fields.[col]
    let offset = schema.RowStride * row + field.Offset
    match field.ColType with
    | IntCol      -> CsvInt (region.ReadInt32 offset)
    | FloatCol    -> CsvFloat (region.ReadFloat64 offset)
    | StringCol   -> CsvString (region.ReadString offset field.Size)
    | BoolCol     -> CsvBool (region.ReadBool offset)
    | DateCol     -> CsvDate (region.ReadDate offset)
    | NullableOf inner -> // nested match with null sentinel check
```

Every branch of that match is statically compiled. The schema descriptor determines which branch executes at runtime, but all branches exist in the binary. The `ReadInt32` at a computed offset is a pointer dereference with an addition. No vtable, no type metadata lookup, no garbage collector, no runtime.

### 4.4 The BAREWire Contract Is Not "Runtime Adaptive"

This is the critical resolution to the architectural tension that initially made this tier feel incompatible with Fidelity's zero-runtime model.

The concern: a BAREWire contract is a compile-time memory layout specification. If the schema is unknown until runtime, the contract cannot be fixed at compilation. Therefore a runtime-adaptive BAREWire contract requires dynamic dispatch or type metadata, which implies a runtime.

The resolution: the BAREWire contract is split in two.

1. **The contract for the schema descriptor** is compile-time fixed. `CsvSchema` has a known layout. This is a normal BAREWire type. The compiled binary knows exactly how to read, write, and transmit schema descriptors.

2. **The contract for the data region** is runtime-parameterized. The data region is a flat byte buffer whose interpretation depends on the schema descriptor. The compiled code treats it as `BAREWireRegion` (a base pointer plus a length), which is a compile-time-known type. The schema descriptor provides the interpretation.

This is the same pattern as reading any binary file format with a header. A PNG file has a fixed header layout and a variable pixel data region whose dimensions are specified in the header. No one would argue that reading a PNG file requires a runtime. The compiled code handles all possible pixel dimensions through parameterized access. The schema descriptor is the "header" of the data format.

The type vocabulary is finite and baked into the binary. `CsvValue`, `JsonValue`, `XmlValue` are closed discriminated unions. All possible types are compiled in; only the *selection* among them is runtime-determined. No new types are created at runtime. No type metadata is consulted. The DU match compiles to a jump table or branch chain on an integer tag, which is what all DU dispatch compiles to in Fidelity.

### 4.5 The BAREWire Actor Pattern

When the parsed data and its schema descriptor need to cross actor boundaries, BAREWire handles this naturally. The schema descriptor travels with the data as a BAREWire-encoded value. An actor receiving parsed CSV data receives two BAREWire regions: the schema descriptor (with compile-time-known layout) and the data region (interpreted according to the descriptor). The receiving actor can:

- Validate the schema against its own expectations (e.g., "I require a column named 'temperature' of type `FloatCol`")
- Access fields through the same statically compiled accessor function
- Transform the data into a different schema (e.g., projecting specific columns into a new region)
- Forward the data with schema to another actor

The message protocol at the actor boundary uses compile-time-known types: `SchemaDescriptor` and `DataRegion` are BAREWire types. The *contents* of the data region vary; the *contract* for transmitting and interpreting those contents is fixed.

### 4.6 The JSON Generalization

JSON's recursive structure adds nesting depth that CSV lacks, but the approach is the same. JSON's type vocabulary is closed:

```fsharp
type JsonValue =
    | JsonNull
    | JsonBool of bool
    | JsonNumber of float64
    | JsonString of string
    | JsonArray of JsonValue array
    | JsonObject of (string * JsonValue) array
```

A `JsonSchema` descriptor captures the expected structure at each nesting level: which fields an object should contain, what types those fields hold, whether arrays are homogeneous or heterogeneous. The accessor is a recursive DU traversal, statically compiled, parameterized by the schema descriptor at each level.

XML extends this with attributes, namespaces, and mixed text/element content. The DU grows but remains closed. TOML is a subset of JSON's structural complexity. YAML adds reference semantics (anchors and aliases) that require a resolution pass before the DU-based accessor applies.

The generalization: **for any data interchange format with a finite, closed type vocabulary, the Tier 2 pattern provides typed runtime access without runtime code generation.** The cost is ergonomic (DU pattern matching at access sites); the benefit is architectural purity (no runtime, no JIT, no dynamic types).

### 4.7 The Fidelity.Schema Library

The Tier 2 pattern suggests a shared library:

**Fidelity.Schema** provides:

- Format-specific schema descriptors (`CsvSchema`, `JsonSchema`, `XmlSchema`, `TomlSchema`, `YamlSchema`) as BAREWire-typed values
- Schema inference functions for each format (sample-based heuristic inference)
- Schema validation functions (check a document against an expected schema, producing typed `Result` errors)
- Format-specific value DUs (`CsvValue`, `JsonValue`, `XmlValue`, etc.)
- Parameterized accessor functions for each format
- BAREWire region management: parse a document into a flat memory region with schema-determined layout
- Column-major and row-major layout selection for tabular formats (CSV, TSV)
- Schema comparison and migration utilities (detect when a new document's schema differs from an expected schema, identify added/removed/changed fields)

Each Tier 1 format library (Fidelity.TOML, Fidelity.JSON, etc.) handles the compile-time-known case with direct typed access. Fidelity.Schema handles the runtime-discovered case with DU-based access parameterized by runtime schema descriptors. Both produce BAREWire regions. Both are statically compiled. They differ in whether the schema is a compile-time constant or a runtime value.

---

## 5. Tier 3: Composer Daemon with JIT-Compiled Accessors

### 5.1 What Tier 3 Provides That Tier 2 Does Not

Tier 2's DU-based access pays a match cost on every field access. For a deeply nested JSON document with 200 fields, accessed frequently in a hot path, this cost accumulates. The match is a branch chain (or jump table) on a type tag, which is fast in isolation but measurable when the access pattern is millions of reads per second in an inner loop.

A JIT-compiled accessor that knows the specific schema can generate direct-offset memory reads, identical to what a Tier 1 compile-time-known schema produces. Column 3 is always `FloatCol` at offset 24; the compiled accessor emits `ReadFloat64 24` without the match.

Tier 3 uses the Composer daemon (the same infrastructure built for LSP design-time support and Jupyter notebook kernel mode) to provide this optimization:

1. The application receives a document (or schema definition) at runtime
2. The application's Prospero supervisor spawns a schema accessor actor
3. The actor sends the schema descriptor to the Composer daemon
4. The Composer daemon compiles (via LLVM ORC JIT) a specialized accessor: a Clef module with functions that read fields at known offsets from a BAREWire region conforming to that specific schema
5. The compiled accessor is loaded into the actor's execution context
6. The application communicates with the accessor actor through BAREWire messages

### 5.2 Actor Containment: Preserving the Zero-Runtime Guarantee

The Composer daemon and JIT-compiled accessor run inside a supervised actor, isolated from the compiled application's address space. The application never loads JIT-compiled code into its own memory. The optimization is contained behind an actor boundary.

The message protocol at the boundary uses compile-time-known types:

```fsharp
type SchemaRequest =
    | ReadField of row: int * col: int
    | ReadRow of row: int
    | GetSchema

type SchemaResponse =
    | FieldValue of CsvValue
    | RowValues of CsvValue array
    | Schema of CsvSchema
```

`SchemaRequest` and `SchemaResponse` are compile-time-known BAREWire types. The application sends requests and receives responses through a BAREWire channel. Everything inside the daemon actor (JIT compilation, schema-specific offsets, LLVM ORC infrastructure) is isolated. Everything outside operates on compile-time-known types with no runtime dependency.

If the daemon actor crashes, the Prospero supervisor restarts it. If the schema changes, the supervisor can request recompilation. The compiled application's execution model is unaffected.

### 5.3 When Tier 3 Is Justified

Tier 3 carries significant infrastructure cost:

- Composer must run as a persistent daemon/TSR service
- LLVM ORC JIT must be integrated into the service environment
- Actor supervision machinery must support hot-loading compiled code
- BAREWire schema negotiation must handle the JIT-compiled actor's lifecycle

The benefit is performance: eliminating DU match overhead in hot-path schema access.

This is justified when:

- The application processes high-volume data streams with runtime-discovered schemas (ETL pipelines, data lake ingestion, real-time analytics)
- Profiling demonstrates that Tier 2's DU match is a measured bottleneck, not a theoretical concern
- The Composer daemon is already running for other reasons (LSP support during development, Jupyter kernel for interactive analysis)

The last point is important. The marginal cost of adding schema JIT to an already-running Composer daemon is low. The fixed cost of standing up the daemon solely for schema JIT is high. Tier 3 becomes practical when the daemon exists for other purposes and schema optimization is an incremental capability.

### 5.4 Spawning the Daemon at Application Start

An application that requires Tier 3 performance can spawn the Composer daemon at start time as a supervised actor. The daemon is an Olivier worker under the application's Prospero supervision tree. Its lifecycle is managed by the same actor infrastructure that manages all other application services.

This model provides runtime independence without runtime dependence. The daemon is an external process (its own executable, its own address space) supervised through BAREWire IPC. The compiled application does not embed a runtime; it communicates with a runtime-capable service through a typed protocol. If the daemon is unavailable (deployment to an embedded target, constrained environment), the application falls back to Tier 2's statically compiled DU-based access. The capability degrades gracefully.

This pattern of spawning a Composer daemon at application start time has broader implications. It is the same architecture that supports:

- Live schema accessor compilation (this use case)
- Interactive Clef evaluation (Jupyter kernel mode)
- Hot-reload of application modules during development
- Runtime profiling and performance advisory (Composer analyzing execution traces and suggesting optimization opportunities)

Each of these is an incremental use of the daemon; none of them individually justifies the infrastructure cost; collectively they establish the Composer daemon as a general-purpose compilation service that applications can optionally leverage.

---

## 6. The Ergonomic Question

### 6.1 What F# Type Providers Provide

The deepest value of F# type providers is not schema inference (Tier 2 handles that) or performance (Tier 3 handles that). It is *ergonomics*: the illusion that runtime-discovered data has the same field-access syntax as compile-time-known data.

With an F# JSON type provider and a sample document `{ "name": "Alice", "age": 30 }`, the developer writes:

```fsharp
let person = JsonProvider<"sample.json">.Parse(input)
let name = person.Name     // string, with IntelliSense
let age = person.Age       // int, with IntelliSense
```

With Tier 2's DU-based approach in Clef, the equivalent is:

```fsharp
let schema = JsonSchema.infer sampleDocument
let doc = Json.parse schema input
match Json.field doc "name" with
| JsonString name -> // use name
| _ -> // handle unexpected type
match Json.field doc "age" with
| JsonNumber age -> // use age
| _ -> // handle unexpected type
```

The ergonomic gap is real. The F# version is concise, discoverable, and familiar. The Clef version is verbose, requires explicit matching, and forces the developer to handle type mismatches at every access site.

### 6.2 Options for Closing the Gap

**Option A: Accept the DU pattern.** The DU-based access is explicit, safe, and requires no compiler magic. The verbosity is the cost of operating without a runtime type system. Clef developers learn the pattern; libraries provide helper functions (`Json.fieldString`, `Json.fieldInt`, etc.) that combine the access and match into a single call returning `Result<'T, SchemaError>`. This is the minimal-investment option.

**Option B: Schema-aware computation expressions.** A computation expression builder parameterized by a schema descriptor could provide syntactic convenience:

```fsharp
let result = jsonAccess schema doc {
    let! name = field "name" asString
    let! age = field "age" asInt
    return { Name = name; Age = age }
}
// result : Result<Person, SchemaError>
```

This preserves explicit error handling (the computation expression is a `Result` builder) while reducing boilerplate. It is a library-level solution, requiring no compiler changes. The `field` function performs the DU match internally and returns `Result`.

**Option C: Compile-time schema hints.** If a sample document or schema definition is available at compile time (even though the actual documents arrive at runtime), Composer could use the schema to generate specialized accessor functions during compilation. This is closest to F#'s type provider model: the sample document provides the schema; the compiler generates typed access; runtime documents are validated against the expected schema.

In Clef, this might look like:

```fsharp
[<Schema("sample.json")>]
type PersonDoc = JsonSchemaProvider

let doc = PersonDoc.parse input  // Result<PersonDoc, SchemaError>
let name = doc.Name              // string
let age = doc.Age                // int
```

This requires Composer to support schema-driven type generation as a compile-time code generation step. The generated types would be real Clef types (not erased) with direct-offset BAREWire accessors. Runtime documents that do not conform to the schema produce parse-time errors, not access-time errors.

### 6.3 Recommended Approach

Option B (computation expressions) provides the best balance of ergonomics, safety, and implementation cost for the near term. It requires no compiler changes, no new language features, and no schema-specific code generation. It leverages Clef's existing computation expression machinery to provide a clean access pattern over DU-based runtime values.

Option C (compile-time schema hints) is the long-term direction. It converges with the type provider concept from F# but produces real types with BAREWire layout, not erased types over .NET objects. It requires Composer investment in schema-driven code generation, which is a natural extension of the Transcribe/Transpose pipeline (Transcribe ingests algorithms, Transpose ingests library interfaces, the schema provider ingests data structure definitions). This investment is justified once the Tier 1 format libraries and the Fidelity.Schema library are mature enough to establish the access patterns that the code generation must produce.

---

## 7. Roadmap

### 7.1 Phase 1: Tier 1 Format Libraries (Current)

Fidelity.TOML exists and provides the pattern. Equivalent libraries for JSON, XML, CSV, and YAML follow the same architecture: compile-time-known schemas, BAREWire-typed accessors, bidirectional serialization.

Each library is independent. Shared infrastructure (BAREWire schema descriptor encoding, region management, parse error types) may be factored into a common base as the libraries mature.

### 7.2 Phase 2: Fidelity.Schema for Tier 2 (Near-Term)

The runtime schema inference library. Provides:

- Format-specific schema descriptors as BAREWire-typed values
- Schema inference from sample documents
- DU-based value representations per format
- Parameterized accessor functions
- Computation expression builders for ergonomic access (Option B)
- Schema comparison, validation, and migration utilities

This library compiles statically. It requires no Composer daemon, no JIT, and no runtime. It handles the "user uploads an unknown CSV" case and its equivalents across all supported formats.

### 7.3 Phase 3: Compile-Time Schema Providers (Medium-Term)

Composer extension for schema-driven type generation. Given a sample document or explicit schema definition at compile time, Composer generates Clef types with direct-offset BAREWire accessors. This provides the ergonomic experience of F# type providers without .NET runtime dependency.

This phase depends on the Composer plugin architecture being stable enough to support compile-time code generation hooks. It is a natural extension point once Transcribe and Transpose establish the pattern for foreign artifact ingestion.

### 7.4 Phase 4: Composer Daemon JIT for Tier 3 (Late Roadmap)

Actor-supervised JIT compilation of schema-specific accessors. Shares infrastructure with:

- Jupyter kernel mode (LLVM ORC JIT for interactive evaluation)
- LSP/Atelier integration (live compilation service)
- Hot-reload during development

The daemon is spawned as an Olivier worker under Prospero supervision. Applications that need Tier 3 performance spawn the daemon at start time. Applications that do not need it fall back to Tier 2's statically compiled accessors. The capability degrades gracefully.

### 7.5 Phase 5: Runtime BAREWire Schema Negotiation (Late Roadmap)

Full actor-supervised runtime schema management. An application that processes documents with evolving schemas uses the Composer daemon to dynamically compile and load accessor actors as new schemas are discovered. The Prospero supervisor manages the lifecycle of these actors, including schema evolution, recompilation, and graceful migration when a schema version changes.

This phase requires mature actor supervision, stable BAREWire IPC, and a proven Composer daemon. It is the most advanced expression of the schema capability and the furthest from current implementation.

---

## 8. Relationship to Transcribe and Transpose

The schema capability is neither Transcribe nor Transpose. It occupies a third position in Composer's foreign artifact integration taxonomy:

| Capability | Input | Output | Operates On |
|---|---|---|---|
| Transcribe | Foreign source code | Clef source (full port) | Algorithms |
| Transpose | Foreign library interfaces | Typed bindings with NTU/BAREWire | Library APIs |
| Schema (TBD naming) | Data format samples/definitions | Schema descriptors + typed accessors | Data structures |

All three share infrastructure: BAREWire schema descriptors, NTU type widths, the Atelier editor surface for browsing and IntelliSense, and the Composer compilation pipeline. They differ in what they ingest and what they produce.

The schema capability's distinguishing characteristic is that it operates across the compile-time/runtime boundary. Transcribe and Transpose are purely build-time operations: they read foreign artifacts and produce Clef source or typed bindings that enter the compilation pipeline. The schema capability spans from compile-time (Tier 1 format libraries, Phase 3 schema providers) through runtime (Tier 2 inference, Tier 3 JIT), with a graceful degradation path between them.

Whether the schema capability earns a musical name to complement Transcribe and Transpose, or lives as a BAREWire subsystem with an Atelier editorial surface, is a naming decision that can follow the implementation. The architectural identity is clear regardless of what it is called: it is the Fidelity Framework's answer to the question "how do I work with data whose structure I don't know until the program runs?" and the answer is "schema descriptors are data, not types; the type vocabulary is finite and compiled in; the schema selects which types apply at which positions."

---

## 9. The Foundational Principle

The deepest insight from this analysis is that Fidelity's zero-runtime model is not in tension with runtime schema discovery. The tension is apparent, not real, and it dissolves once the distinction between *types* and *schema descriptors* is clear.

Types are a compile-time concept. They determine memory layout, accessor code generation, and type-checking rules. They are fixed when the binary is produced.

Schema descriptors are a runtime concept. They are *values* that describe which types appear at which positions in a data region. They are constructed from runtime input (document parsing, schema inference, explicit definition). But they are themselves instances of compile-time-known types. A `CsvSchema` is a type. A `JsonSchema` is a type. The compiled binary knows how to create, read, compare, and transmit these values.

The accessor function bridges the two: it is compile-time code (statically compiled, all branches present in the binary) parameterized by runtime data (the schema descriptor). The DU match selects the appropriate branch based on the schema's type tag for each field. This is ordinary functional programming: a function parameterized by a value, dispatching on a discriminated union. It requires no runtime, no reflection, no dynamic type generation.

The "runtime adaptive BAREWire contract" that seemed to threaten the zero-runtime model does not exist as a concept. What exists is a compile-time-fixed schema descriptor type, a compile-time-fixed data region type, and a compile-time-compiled accessor function that interprets one according to the other. The "adaptation" is a computed offset read and a DU branch selection, both of which are ordinary machine instructions in the compiled binary.

This principle, that schema descriptors are data within the type system (not extensions of the type system), is what makes Tiers 2 through 5 tractable without abandoning Fidelity's core architectural commitment. The Composer daemon and JIT (Tiers 3-5) are performance optimizations, not capability extensions. They eliminate the DU match overhead for hot-path access, which is valuable in data-intensive applications, but the *capability* to access runtime-discovered schemas exists fully within the statically compiled binary.

---

*This memo captures architectural analysis and design direction. Implementation specifics are subject to revision as the Fidelity.TOML pattern is validated across additional formats and the Fidelity.Schema library design matures.*

*SpeakEZ Technologies | Fidelity Framework*
*License: MIT*
