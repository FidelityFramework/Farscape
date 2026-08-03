# Namespace-Scoped PSG Loading — Design Document

## Status: Design Phase (March 2026)

## Problem Statement

CCS currently loads ALL source files from ALL transitive dependencies into a single
flat TypeEnv. This creates three categories of problems:

1. **Type inference pollution** — 28 opaque handle records with `{ Handle: nativeint }`
   all populate the same `FieldLabels` map. Record construction resolves via "last definition
   wins" when field names are ambiguous. This is an adversarial input to Hindley-Milner
   unification.

   > **Superseded.** `docs/14_Binding_Generation_Gaps.md` §3 establishes that these records
   > should not be emitted at all — a single-word handle record is memref-backed and reaches
   > a C callee as the address of a slot rather than as the handle, and `ofHandle` allocates
   > and leaks eight bytes per construction. The correct Layer 1 output is bare `nativeint`.
   > Many of these 28 records are also debris from the declaration-shadowing defect in §1
   > rather than deliberate design. If Layer 1 is corrected, this problem does not arise.
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

## Prior Art: ML Module Signatures

The ML family (OCaml, F*, OxCaml) solves this with **module signatures**:

- OCaml: `.mli` files define module interfaces (type defs + function signatures, NO bodies)
- F*: `.fsti` files serve the same role for dependently-typed modules
- OxCaml (Jane Street): `.cmi` compiled module interfaces for separate compilation

The key mechanism: when you `open` a dependency, you type-check against its
**signature**, not its implementation. The signature IS the inference boundary.
HM sees `val ofHandle : nativeint → wl_surface` — never the `NativeDefault.zeroed()`
body, never the constraint pollution from the implementation.

This gives you:
1. **Demand-driven loading** — only load signatures for opened modules
2. **Inference isolation** — HM works against signatures, which are small and precise
3. **Compilation boundary** — signature = sealed interface (the ML analog of .NET
   assembly metadata, but at module granularity)
4. **Separate compilation** — modules compiled independently against dependency signatures

OCaml's `ocamldep` scans `open` directives to build the dependency graph BEFORE
compilation begins — exactly the "open as demand signal" model.

### Clef Adaptation

Clef's module signature analog:

```
Developer writes:                     Compiler sees:
─────────────────                     ──────────────
fidproj [dependencies]         →      Search path (where to find modules)
open Fidelity.Wayland.Types    →      Demand signal (load this module's SIGNATURE)
module signature               →      Inference boundary (types + val decls, no bodies)
```

Module signatures for Clef can be:
1. **Auto-generated** from source (like OCaml's `-i` flag) — extract type defs +
   function signatures, discard bodies
2. **Cached as `.clefi`** alongside source in clefpak — avoids re-parsing source
3. **Hand-written** for abstraction (future — allows hiding implementation types)

The auto-generated path is the pragmatic first step: Farscape already knows the
full type surface of every generated module. The signature IS the generated code
minus the bodies.

## Target Architecture

### Core Principle

The `open` declaration is the **demand signal**. It determines what enters the
type-checking graph. Module signatures are loaded **on demand** when their
namespace is first referenced. Full source is only needed for the developer's
own code and for final codegen/linking.

### Module Manifest

Each fidproj gains a **module manifest** — a lightweight index mapping module
names to source files and (optionally) cached signatures. This can be:

1. **Auto-generated** during compilation (cached alongside the fidproj)
2. **Derived from first-line scan** — each `.clef` file's `module` declaration
   declares its module name. A fast pre-scan (first non-comment line) builds the map.
3. **Embedded in clefpak** — signed source packages carry the manifest as metadata

```
Fidelity.Wayland.fidproj module manifest:
  Fidelity.Wayland.Types          → Bindings/Wayland/Types.clef
  Fidelity.Wayland.Core           → Bindings/Wayland/Core/Core.clef
  Fidelity.Wayland.Core.Api       → Bindings/Wayland/Core/CoreApi.clef
  Fidelity.Wayland.Protocol       → Bindings/Wayland/Protocol/Protocol.clef
  Fidelity.Wayland.Protocol.Api   → Bindings/Wayland/Protocol/ProtocolApi.clef
  ...
```

### Two-Phase Loading: Signature then Source

**Phase 1 — Signature loading (type checking)**:

When `checkModuleDecl` encounters `open Fidelity.Wayland.Types`:

1. Look up `Fidelity.Wayland.Types` in the module manifests of all dependencies
2. If the module's signature hasn't been loaded yet:
   a. Parse the source file (or load cached `.clefi`)
   b. Extract/check the signature (type defs + val signatures, skip bodies)
   c. Merge its TypeDefs/RecordDefs/FieldLabels into the current TypeEnv
   d. Recursively load signatures for this module's own `open` declarations
3. Add the module to Resolution (existing `addOpen` behavior)

The signature contains everything HM inference needs:
- Record type definitions (field names, field types)
- Function signatures (parameter types, return types)
- Module structure (companion modules, sub-modules)
- Type abbreviations and aliases

The signature does NOT contain:
- Function bodies (no `NativeDefault.zeroed()` constraint instantiations)
- Internal let bindings
- Implementation-only type variables

**Phase 2 — Source loading (codegen/linking)**:

After type checking succeeds, the backend (Alex) needs full source for codegen.
At this point, load source for reachable modules only. The type-checked
SemanticGraph tells us exactly which modules are referenced.

### Transitive Demand

When `Fidelity.Wayland.Bridge.Protocol` opens `Fidelity.Wayland.Types`, CCS loads
the Types module signature. If Types itself opens other modules (e.g., platform
types), their signatures are loaded transitively. The transitive closure of `open`
declarations determines the loaded set — NOT the fidproj dependency list.

The fidproj dependency list becomes a **search path** for module resolution, not
a loading directive. "This library MAY use modules from these dependencies" rather
than "load all sources from these dependencies."

### FieldLabels Scoping

With signature-based loading, `FieldLabels` only contains entries for records
defined in modules whose signatures have been loaded. The granularity is
module-level (one `.clef` file = one module):

- **Immediate win**: Cross-library pollution eliminated. If you don't open
  `Fidelity.Libc.Errno`, its error types don't enter FieldLabels.
- **Intra-library**: If Types.clef defines 28 handle types and you open
  `Fidelity.Wayland.Types`, all 28 enter FieldLabels. This is file-level
  granularity. For finer control, split types into separate modules
  (future optimization, not required for correctness).

### Reachability as Loading Boundary

The loaded set IS the reachability boundary:
- Only loaded modules contribute to the SemanticGraph
- Only loaded types are available for inference
- Dead code elimination becomes a refinement of an already-narrow graph,
  not the primary narrowing mechanism

### Multi-Target Implications

For a StrixHalo profile targeting CPU + iGPU + NPU:
- CPU graph section opens `Fidelity.Libc`, `Fidelity.Wayland` → loads those signatures
- GPU graph section opens `Fidelity.ROCm` → loads ROCm signatures
- NPU graph section opens `Fidelity.XDNA` → loads XDNA signatures
- Each section's TypeEnv contains only its target's type surface area
- BAREWire reconciles data contracts at section boundaries
- Signatures provide the type contract between sections

## Hindley-Milner Inference in the Layered Model

### The Problem

HM inference works by unifying type constraints across an expression graph.
When the constraint set includes 28 records with identical field structure
(`{ Handle: nativeint }`), the unifier faces ambiguity that degrades inference
quality — "last definition wins" is not HM, it's a heuristic escape hatch.

### Layer-Aware Inference Strategy

The module signature model naturally creates inference layers:

**Layer 1 — Type Surface (signatures auto-generated)**

L1 declarations are fully type-annotated (FidelityExtern signatures are explicit).
The auto-generated signature for an L1 module contains:
- Record type definitions (`type wl_surface = { Handle: nativeint }`)
- Companion module signatures (`module wl_surface : val ofHandle : nativeint → wl_surface`)
- FidelityExtern function signatures (no bodies — bodies are placeholder anyway)

The signature IS the inference surface. No `NativeDefault.zeroed()` calls,
no internal constraint variables, no implementation details.

Farscape should **measure** the inference load of its generated L1 signatures:
- Count of records sharing field names (Handle collision count)
- Count of identically-shaped types per module
- This measurement informs L2 structure decisions

**Layer 2 — Inference Articulation (signatures as boundaries)**

L2 module signatures sit between L1 types and L3 developers. When CCS loads
an L2 signature, the L3 developer sees:

```
module Fidelity.Wayland.Bridge.Protocol:
  val wl_compositor_create_surface : wl_compositor → option<wl_surface>
  val wl_surface_attach : wl_surface → wl_buffer → int32 → int32 → unit
  val wl_surface_commit : wl_surface → unit
```

The L2 BODY (which calls `wl_surface.ofHandle`, uses NativePtr, etc.) is never
seen by L3's type checker. The signature IS the inference boundary.

L2 structure decisions:
1. **Explicit signatures on all L2 functions** — already the case for protocol
   dispatch. These become the val declarations in the auto-generated signature.
2. **Type-qualified record construction in L2 bodies** — ensures L2 compiles
   cleanly even in the flat model (pre-signature-loading).
3. **Companion modules as val-only signatures** — L3 sees
   `val ofHandle : nativeint → wl_surface`, never the body.

**Layer 3 — Clean Inference**

L3 developers should never fight the type checker. With signature-based loading:
- `open Fidelity.Wayland.Bridge.Protocol` loads only the Protocol module signature
- HM inference works against val declarations — small, precise, unambiguous
- Record construction resolves cleanly because only opened module types are
  in FieldLabels
- No type annotations needed on L3 let bindings — inference "just works"

### Inference Measurement Metrics

Farscape should emit (optionally) an inference complexity report per generated library:

```
Fidelity.Wayland L1 Signature Report:
  Module: Fidelity.Wayland.Types
    Record types: 28 opaque handles + 17 listener structs + 5 data structs
    Shared field names:
      Handle: 28 types (wl_buffer, wl_callback, ..., zwp_linux_dmabuf_v1)
    Companion modules: 28 (zero, isNull, ofHandle each)
    Signature size: 50 types, 84 val declarations

  Recommendation: L2 modules should use type-qualified construction
  Recommendation: L3 consumers should open Bridge modules, not Types directly
```

This guides both the L2 generator and the developer toward minimal type surface.

## Implementation Phases

### Phase A: Module Manifest (CCS — SourceResolver)

Add first-line module scanning to SourceResolver:
- Pre-scan all dependency source files for `module` declarations
- Build module name → file path map per dependency fidproj
- Store as cached manifest alongside fidproj (regenerate if sources change)
- In clefpak: embed manifest in package metadata

This is lightweight — one line per file, no parsing required.

### Phase B: Signature Extraction (CCS — NativeService)

Add signature extraction to the type checker:
- After type-checking a module, extract its signature:
  type definitions, val declarations (function name + type), module structure
- Cache as `.clefi` alongside source (or in clefpak)
- Signature = everything HM inference needs, nothing it doesn't
- Auto-generated (like OCaml `-i`), not hand-written (initially)

### Phase C: Demand-Driven Signature Loading (CCS — checkModuleDecl)

Modify `open` processing:
- On `open Fidelity.Wayland.Types`:
  1. Look up module in manifests of declared dependencies
  2. Load signature (cached .clefi or extract from source)
  3. Merge signature's TypeDefs/RecordDefs/FieldLabels into TypeEnv
  4. Recursively load signatures for this module's own `open` declarations
  5. Add to Resolution (existing addOpen behavior)
- Project's OWN source files: full type-check as today
- Dependency source files: signature-only for type checking

### Phase D: Inference Articulation (Farscape)

Add L2 inference helpers to generated code:
- Type-qualified record construction for ambiguous types
- Explicit type annotations on all L2 function signatures
- Optional inference complexity report
- Companion module bodies structured for clean signature extraction

### Phase E: Graph Section Scoping (CCS — Multi-Target)

Extend signature-based loading to multi-target graphs:
- Each program graph section has its own loaded set
- BAREWire contracts define type surface at section boundaries
- Per-section TypeEnv isolation
- Signatures provide the type contract between sections

## Immediate Tactical Fix (Pre-Phase A)

Until signature-based loading is implemented, Farscape should generate
L1/L2 code that is robust in the current flat compilation model:

1. **Type-qualified record construction** in companion modules:
   `{ wl_surface.Handle = h }` instead of NativeDefault.zeroed + NativePtr tricks.
   Proven to work in CCS (L1 listener builders use this pattern).

2. **Explicit type annotations** on all intermediate bindings in protocol dispatch

3. **RequireQualifiedAccess consideration** — if CCS supports this attribute on
   records, it prevents field names from entering FieldLabels, eliminating
   the "28 Handle entries" problem entirely. This is the closest analog to
   OCaml's abstract types in signatures.

## Relationship to Existing Architecture

- **Farscape → Atelier evolution**: Inference articulation becomes part of
  Transpose (typed dynamic binding) and Transcribe (algorithmic port)
- **BAREWire**: Section boundary contracts are the multi-target analog of
  namespace-scoped loading boundaries
- **Lattice**: Namespace refinement warnings ("you opened Fidelity.Libc but
  only used Memory.malloc — narrow to Fidelity.Libc.Memory") guide developers
  toward minimal type surface
- **clefpak**: Namespace manifest becomes part of the signed source package
  metadata, enabling demand-driven loading without re-scanning sources
