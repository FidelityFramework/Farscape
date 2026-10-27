# Namespace-Scoped PSG Loading — Design Document

## Status: Design Notes (March 2026; corrected September 25 and October 3, 2026)

**Correction, September 25, 2026:** the former proposal for public `.clefi`
signature/cache files and hand-written Clef interfaces is withdrawn. Clef has no
separate authored signature file. Any inferred module view or reuse cache below
is compiler-owned design work, not a settled serialized API or package extension.
Source remains `.clef`; the planned `clefx` command consumes `.clefx` scripts.
The [workbench contract](../../Composer/docs/Interactive_Compiler_Workbench.md)
preserves CCS/Baker construction and Alex ctx pull witnessing throughout loading
and reuse. The March loading sketch is not an implemented service boundary.

**Further correction, October 3, 2026:** nothing below depends on an authored
module signature, separate or inline. Reuse must also satisfy the
specification's revalidation rule
([program hypergraph](../../clef-lang-spec/spec/program-hypergraph.md) §5.1),
Fidelity.PSG's [scoped publication contract](../../Fidelity.PSG/docs/Scoped_Publication.md)
and Composer's [nanopass incremental contract](../../Composer/docs/Nanopass_Incremental_Contract_Direction.md)
§2. Fidelity.FSharp.Incremental tracks declared dependencies and work lifetime;
the compiler decides whether a result remains valid. An `open` changes
visibility; it does not demand a module's bodies. Problem 1 below is
historical, and the later sections are corrected in place.

## Problem Statement

CCS currently loads ALL source files from ALL transitive dependencies into a single
flat TypeEnv. This creates three categories of problems:

1. **Type inference pollution (historical)** — 28 opaque handle records with
   `{ Handle: nativeint }` all populated the same `FieldLabels` map, and record
   construction resolved ambiguous field names by "last definition wins". This was an
   adversarial input to Hindley-Milner unification.

   > **Superseded.** `docs/14_Binding_Generation_Gaps.md` §3 established that these records
   > should not be emitted at all — a single-word handle record is memref-backed and reaches
   > a C callee as the address of a slot rather than as the handle, and `ofHandle` allocated
   > and leaked eight bytes per construction. Its interim fix, bare `nativeint`, is itself
   > superseded: `nativeint`-as-pointer is not denotable in Clef. An opaque pointer
   > typedef (a pointer to a struct the headers do not define) now becomes a field-less
   > nominal marker (`type Marker = private | Opaque_Marker`) used through
   > `CHandle<Marker>`; native widths and passing modes belong to the descriptor
   > ([FFI boundary](../../clef-lang-spec/spec/ffi-boundary.md) §5.3). Markers add no field
   > labels. Many of the 28 records were also debris from the declaration-shadowing defect
   > in doc 14 §1 rather than deliberate design. Legacy packages that predate regeneration
   > still declare such records (30 in Fidelity.Platform's `Bindings/Wayland/Types.clef`).
   >
   > CCS now rejects ambiguous record labels (CCS8704) instead of letting the last
   > definition win. The label-scope gap that remains is a conformance fix, not a loading
   > problem: the specification adds a record's labels when its declaration group is
   > opened, while CCS adds every checked record's labels to one environment-wide table.
   > Problems 2 and 3 below are unaffected and remain the case for this design.

2. **Memory and graph bloat** — A library that needs only `Fidelity.Libc.Memory.malloc`
   pulls in ALL of `Fidelity.Libc` (errno, dynamic linking, signal handling, etc).
   Each loaded file adds to TypeDefs, RecordDefs, FieldLabels monotonically.

3. **Reachability over-approximation** — Multi-target compilation (CPU/GPU/FPGA/NPU)
   requires precise reachability boundaries. Graph sections targeting different devices
   should carry only their actual type surface area. Exhaustive loading defeats this.

## Current Architecture

```
Source Loading (SourceResolver.getAllSourcesInOrder):
  For each dependency (transitive-first):
    Load ALL source files listed in fidproj
  Then load project source files
  → Flat list: [all_dep_sources @ project_sources]

Type Checking (checkParsedInputsWithPlatform):
  Single TypeEnv threaded through ALL files
  FieldLabels: accumulated, never pruned
  Constraints: shared ref cell across all files
  open: only modifies Resolution (name lookup), NOT FieldLabels/RecordDefs

Reachability (post-hoc):
  pruneUnreachable() after type-checking
  Too late — type pollution already happened
```

Three specification gaps sit on this path. The specification orders files by
syntactic dependency analysis
([program structure](../../clef-lang-spec/spec/program-structure.md), Compilation
Pipeline step 5); the native checker folds them in resolver order. The
specification adds a record's labels to `FieldLabels` when its declaration group
is opened ([name resolution](../../clef-lang-spec/spec/inference-name-resolution.md));
CCS adds them at definition, and they remain after the module scope closes. The
specification marks unreachable nodes instead of removing them
([PSG](../../clef-lang-spec/spec/program-semantic-graph.md) §8.1); `pruneUnreachable`
removes them when the graph has neither edges nor a platform context.

## Prior Art: ML Module Signatures

The ML family (OCaml, F*, OxCaml) solves this with **module signatures**:

- OCaml: `.mli` files define module interfaces (type defs + function signatures, NO bodies)
- F*: `.fsti` files serve the same role for dependently-typed modules
- OCaml and OxCaml (Jane Street): `.cmi` compiled interfaces, built from an `.mli` or
  inferred from the `.ml`, support separate compilation

The key mechanism: when you refer to a dependency, qualified or after `open`, you
type-check against its **signature**, not its implementation. The signature IS the
inference boundary. HM sees a declared type such as
`val wl_surface_attach : CHandle<wl_surface> -> option<CHandle<wl_buffer>> -> int -> int -> unit`
— never the body, never the constraint pollution from the implementation.

This gives you:
1. **Demand-driven loading** — only load interfaces for referenced modules
2. **Inference isolation** — HM works against signatures, which are small and precise
3. **Compilation boundary** — signature = sealed interface (the ML analog of .NET
   assembly metadata, but at module granularity)
4. **Separate compilation** — modules compiled independently against dependency signatures

This design uses only part of that model. A Clef module's view is inferred from
checked source, with no separate interface file; `private` representation hides a
representation; and native compilation needs settled facts beyond any type
interface, much as OCaml's native compiler consults implementation summaries
(`.cmx`) for cross-module inlining.

OCaml's `ocamldep` records module references, including qualified paths, to build
the dependency graph before compilation begins. Clef's specified analog is
syntactic dependency analysis over resolved cross-file references. An `open`
changes visibility; it is not a demand signal.

### Clef Adaptation

This design's analog of an ML module signature:

```
Developer writes:                     Compiler sees:
─────────────────                     ──────────────
fidproj [dependencies]         →      Search path (where to find modules)
open M                         →      Visibility; Baker determines the lookup,
                                      membership and absence judgments it affects
M.x, or x after open M         →      Resolved reference: demand for M's view
module body                    →      Inferred module view plus settled PSG facts
private representation         →      Representation hidden from other modules
                                      (specified; CCS enforcement pending)
```

For Clef, the candidate boundary is a **compiler-inferred module view** from
checked source. No separate interface file is authored or distributed. A future
compiler-owned cache would need dependency, compiler, target and semantic
provenance invalidation; neither its encoding nor storage location is settled.
Farscape's knowledge of generated declarations can help investigation, but simply
removing bodies does not establish all inferred semantics or proof premises.
Like the exported interface of a compiler region in Composer's nanopass
incremental contract (§2), the view must carry the facts downstream work consumes
and their evidence dependencies. Equal physical signatures alone do not justify
reuse of a changed semantic contract (program hypergraph §5.1).

## Target Architecture

### Core Principle

A resolved reference, qualified or through an `open`, is the **demand signal**.
An `open` changes visibility; it neither demands every member of a module nor
authorizes mapping a whole library. Baker determines the lookup, membership and
absence judgments a change affects and the newly demanded closure
([scoped publication contract](../../Fidelity.PSG/docs/Scoped_Publication.md)).
Consumers can be checked against a dependency's inferred view. Native compilation
still needs every demanded body admitted, elaborated and saturated by CCS/Baker
before publication; Alex reads only the published PSG.

### Module Index

Each dependency needs a **module index** mapping module names to the files that
declare them. The specification's dependency analysis builds the same kind of map
for the files it orders: an export map of the names each parsed file declares
([program structure](../../clef-lang-spec/spec/program-structure.md), Compilation
Pipeline step 5). Library dependencies instead enter the initial environment when
imported (step 6), so reusing the map for dependency sources is this design's
extension. CCS does not implement the export-map pass yet. Building the map
requires parsing, because one file can declare several modules or namespace
groups. Any persisted index would be a compiler-owned cache keyed by source,
compiler, target and dependency provenance. Packaging is future work: `clefpak` is
a Farscape proposal with no specification, and CCS resolves dependencies only
through local `.fidproj` paths.

```
Fidelity.Wayland.fidproj module index:
  Fidelity.Wayland.Types          → Bindings/Wayland/Types.clef
  Fidelity.Wayland.Core           → Bindings/Wayland/Core/Core.clef
  Fidelity.Wayland.Core.Api       → Bindings/Wayland/Core/CoreApi.clef
  Fidelity.Wayland.Protocol       → Bindings/Wayland/Protocol/Protocol.clef
  Fidelity.Wayland.Protocol.Api   → Bindings/Wayland/Protocol/ProtocolApi.clef
  ...
```

### Two-Phase Use: Inferred Views, then Elaboration

**Phase 1 — Inferred views (type checking)**:

When CCS resolves a reference to `Fidelity.Wayland.Types`, qualified or through an
`open` that `checkModuleDecl` processes:

1. Look up `Fidelity.Wayland.Types` in the module indexes of declared dependencies
2. If this check has not yet loaded the module's view:
   a. Check source through CCS, or reuse a compiler-owned result with validated freshness
   b. Read the inferred module view and retain its semantic/proof dependencies
   c. Add its type definitions for resolution; its record labels enter scope only
      where the consumer opens the module
   d. Obtain the views of modules whose types appear in this view; the dependency's
      own `open` declarations do not enter the consumer's scope
3. For an `open`, add the module to Resolution (existing `addOpen` behavior)

A consumer depends on more than HM types: generalized schemes and constraints,
arity, `inline` bodies, SRTP and member resolutions, measures, accessibility and
representation. Native compilation also needs settled layout, boundary,
escape/lifetime and obligation facts. A view may omit a body only where no
consumer judgment depends on it; `inline` and SRTP-constrained bodies cannot be
omitted.

**Phase 2 — Elaboration before publication (CCS/Baker)**:

Any deferred source needed for native compilation must be admitted and elaborated
by CCS/Baker before Alex observes it. Alex consumes the resulting PSG; it does not
load source bodies or reconstruct semantics during code generation. The proposal
must establish complete dependency and obligation coverage before claiming that
an inferred interface view is sufficient for selective loading.

### Transitive Demand

When `Fidelity.Wayland.Bridge.Protocol` refers to `Fidelity.Wayland.Types`,
qualified or through `open`, CCS needs that module's view and the views of the
modules its types mention. The closure follows resolved references and their
membership and absence support — NOT the fidproj dependency list, and not the
`open` declarations inside dependencies. An unused `open` adds no bodies.

The fidproj dependency list becomes a **search path** for module resolution, not
a loading directive. "This library MAY use modules from these dependencies" rather
than "load all sources from these dependencies."

### FieldLabels Scoping

The specification already scopes record labels to opened declaration groups, so
this needs a CCS name-resolution fix, not demand-driven loading. The granularity
is the opened module or namespace group; a `.clef` file need not hold a single
module:

- **After the fix**: Cross-library pollution is eliminated. If you don't open
  `Fidelity.Libc.Errno`, its record labels don't enter FieldLabels.
- **Until then**: `[<RequireQualifiedAccess>]` keeps a record's labels out of
  FieldLabels, type-qualified construction names the record explicitly, and
  CCS8704 rejects any ambiguity that remains.
- **Generated handles**: Regenerated handles are field-less markers and add no
  labels. Value, argument and callback records still carry fields; splitting
  them into separate modules is a later optimization, not required for
  correctness.

### Reachability and the Demanded Closure

A demanded closure bounds which facts are elaborated. Reachability remains a
supported conclusion computed from roots
([PSG](../../clef-lang-spec/spec/program-semantic-graph.md) §8; Composer's nanopass
incremental contract §2.1):
- Loading a module does not make it reachable
- Newly demanded code must be elaborated and admitted before it is delivered
- Dead code elimination becomes a refinement of an already-narrow graph,
  not the primary narrowing mechanism

### Multi-Target Implications

For a StrixHalo profile targeting CPU + iGPU + NPU:
- Each program-graph section resolves its NTU types against its own
  PlatformContext during saturation
  ([NTU dimensional architecture](../../clef-lang-spec/spec/ntu-dimensional-architecture.md) §3)
- Section membership follows CCS's target assignment, not `open`
- BAREWire supplies the inter-section data contract, and the specification
  requires CCS to verify dimensional compatibility at each boundary
- A module's inferred view is not a section contract: a Clef type fixes no width
  or layout
- Where sections or processes are built from different sources or toolchains, an
  emitted `.bare` schema can serve as the interchange artifact

## Hindley-Milner Inference in the Layered Model

### The Problem

HM inference works by unifying type constraints across an expression graph.
When the constraint set includes 28 records with identical field structure
(`{ Handle: nativeint }`), the unifier faces ambiguity that degrades inference
quality — "last definition wins" is not HM, it's a heuristic escape hatch.
(Historical: CCS now rejects ambiguous record labels with CCS8704, and regenerated
bindings carry no handle fields.)

### Layer-Aware Inference Strategy

The inferred-view model naturally creates inference layers:

**Layer 1 — Type Surface (inferred views)**

L1 declarations are fully type-annotated (FidelityExtern signatures are explicit).
Their Clef types use value kinds such as `int` and opaque carriers such as
`CHandle<_>`; native widths and passing modes live in the `FunctionDescriptor`.
The inferred view of an L1 module contains:
- Field-less nominal markers (`type wl_surface = private | Opaque_wl_surface`) used
  as `CHandle<wl_surface>` or `option<CHandle<wl_surface>>`
- FidelityExtern declarations (explicit Clef types, placeholder bodies), each with
  an `Expr<FunctionDescriptor>` descriptor
- Value, argument and callback records (callback slots are `FnPtr` fields described
  by `CallbackDescriptor` quotations)

The inferred view IS the inference surface. Descriptors are quotations that CCS
reads during compilation; they are never evaluated.

Farscape should **measure** the inference load of its generated L1 declarations:
- Count of records sharing field names (value, argument and callback records)
- Count of identically-shaped types per module
- This measurement informs L2 structure decisions

**Layer 2 — Inference Articulation (inferred views as boundaries)**

L2 inferred views sit between L1 types and L3 developers. When L3 code is checked
against an L2 view, the developer sees:

```
module Fidelity.Wayland.Bridge.Protocol:
  val wl_compositor_create_surface : CHandle<wl_compositor> -> option<CHandle<wl_surface>>
  val wl_surface_attach : CHandle<wl_surface> -> option<CHandle<wl_buffer>> -> int -> int -> unit
  val wl_surface_commit : CHandle<wl_surface> -> unit
```

TypedProtocolGenerator currently emits `CHandle<unit>` for every proxy. The
nominal form above needs per-interface markers in the generator, not a signature
mechanism.

L3 checking depends on the L2 view, not L2 bodies; CCS/Baker still elaborate L2
bodies for native compilation. Generated L2 bodies hold `CHandle` values, never
raw pointers.

L2 structure decisions:
1. **Explicit type annotations on all L2 functions** — already the case for
   protocol dispatch. These annotations become the inferred view.
2. **Type-qualified record construction in L2 bodies** — ensures L2 compiles
   cleanly even in the flat model (before label scoping is fixed).
3. **No handle companions** — a marker exposes no public constructor, address
   accessor or null value (its representation is `private`, which CCS does not yet
   enforce); only the boundary produces handles.

**Layer 3 — Clean Inference**

L3 developers should never fight the type checker. With spec-conformant label
scoping and demanded scopes:
- `open Fidelity.Wayland.Bridge.Protocol` makes that module's names visible, and
  its record labels enter scope only through that `open`
- HM inference uses declared and inferred types; ambiguity is diagnosed (CCS8704),
  never resolved by declaration order
- Record construction sees only labels of the current and opened modules, so labels
  from unopened dependencies cannot collide; an ambiguity that remains among those
  records needs an expected type or a qualified label
- The need for type annotations on L3 let bindings is measured, not assumed away

### Inference Measurement Metrics

Farscape should emit (optionally) an inference complexity report per generated library:

```
Fidelity.Wayland L1 Surface Report:
  Module: Fidelity.Wayland.Types
    Nominal markers: N (no fields)
    Records with fields: value, argument and callback records
    Shared field names: each label with the records that declare it
    Descriptors: FunctionDescriptor and CallbackDescriptor counts

  Recommendation: construct shared-label records with an expected type or qualifier
  Recommendation: L3 consumers should open Bridge modules, not Types directly
```

This guides both the L2 generator and the developer toward minimal type surface.

## Implementation Phases

### Phase A: Module Index (CCS — SourceResolver and Dependency Analysis)

Build the specification's per-file export map, and extend it to dependency sources:
- Parse dependency sources and record the names each file declares
- Use the map for the specified syntactic dependency order, which the native
  checker does not implement yet
- Keep any persisted index in a compiler-owned cache keyed by source, compiler,
  target and dependency provenance; Fidelity.FSharp.Incremental can track the
  declared dependencies, while CCS decides validity
- Packaging (`clefpak`) remains unspecified

This requires parsing: one file can declare several modules.

### Phase B: Inferred Module View (CCS/Baker)

Extend the type checker:
- After checking a module, record its view: type definitions, generalized value
  types with their constraints, arities, `inline` bodies, accessibility and
  module structure
- Investigate compiler-owned reuse with complete dependency and provenance keys;
  no public cache format or extension is specified
- Establish which inferred facts and proof dependencies the view must retain
- Infer the view from checked source; no authored signature-file counterpart
- Enforce accessibility so a view omits `private` values and hides `private`
  representations; the specification defines both, and CCS does not enforce them
  yet

### Phase C: Demand from Resolved References (CCS/Baker — Name Resolution)

Extend reference resolution:
- When CCS resolves a reference to `Fidelity.Wayland.Types`, qualified or through `open`:
  1. Look up the module in the indexes of declared dependencies
  2. Read a current compiler-inferred module view or check its source
  3. Add its type definitions for resolution; add record labels only for opened
     modules
  4. Obtain the views of modules named in that view's types, not the
     dependency's own `open` declarations
  5. For an `open`, add to Resolution (existing addOpen behavior)
  6. Record the lookup, membership and absence judgments for invalidation
- Project's OWN source files: full type-check as today
- Dependency source files: checked by CCS; consumers check against their views,
  and CCS/Baker elaborate the bodies native compilation demands

### Phase D: Inference Articulation (Farscape)

Add L2 inference helpers to generated code:
- Type-qualified record construction for ambiguous types
- Explicit type annotations on all L2 function signatures
- Optional inference complexity report
- Per-interface nominal markers in typed protocol projections (currently
  `CHandle<unit>`)

### Phase E: Graph Section Scoping (CCS — Multi-Target)

Extend demanded scopes to multi-target graphs:
- Each program-graph section resolves NTU types against its own PlatformContext
  during saturation
- Section membership follows CCS's target assignment; module views are not
  section contracts
- BAREWire contracts and dimensional checking govern data crossing sections
- Emit `.bare` schemas where sections or processes do not share source

## Immediate Tactical Guidance (Pre-Phase A)

Until CCS scopes record labels by `open`, Farscape should generate L1/L2 code
that is robust in the current flat compilation model:

1. **Field-less markers for handles** — opaque pointer typedefs become `CHandle<_>`
   over field-less nominal markers. Emit no handle records, address constructors,
   `nativeint` handles or `NativePtr` code. The current generator does this.

2. **Type-qualified record construction** — construct records that share labels
   with an expected type or `{ R.field = … }`

3. **Explicit type annotations** on all intermediate bindings in protocol dispatch

4. **RequireQualifiedAccess** on generated records whose labels collide — CCS
   supports this attribute on records (no CCS test covers it yet). It keeps field
   names out of FieldLabels; it does not hide a representation. Representation
   hiding is a separate concern: the specification defines `private`
   representation, which CCS does not yet enforce.

## Relationship to Existing Architecture

- **Farscape → Composer evolution**: Inference articulation becomes part of
  Transpose (typed dynamic binding) and Transcribe (algorithmic port) when
  Farscape's core is hosted in Composer; the Atelier IDE is in preliminary design
- **BAREWire**: Inter-section and inter-process data contracts, separate from
  module-loading scope
- **Lattice**: Presents compiler diagnostics and maintains no checker of its
  own. CCS could diagnose an over-broad `open` ("you opened Fidelity.Libc but
  only used Memory.malloc — narrow to Fidelity.Libc.Memory"), as it already
  reports unused values (CCS8500); Lattice would carry that diagnostic
- **Packaging**: `clefpak` is a Farscape proposal with no specification; a
  packaged module index would be compiler-owned metadata with provenance
