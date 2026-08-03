# Farscape Phase 5+: MFEM Algorithmic Ingestion

## From Binding Generator to Computational Understanding

**SpeakEZ Technologies | Fidelity Framework**
**February 2026**

---

> **Schema caveat (added 2026-08-03).** The example `.pilot.toml` recipes in this document
> use section names the serializer does not read: `[sources]` (the real section is
> `[library]`, with `headers`) and `[error_convention]` singular (the real section is
> `[error_conventions]`). `PilotSerializer` performs no validation and silently drops
> unrecognized sections, so copying a recipe from this document verbatim yields a project
> with no headers and no error convention, and no warning. Several recipes also carry
> `opaque_handles` and `flags_enums`, which have never been keys. See
> `docs/07_Pilot_Project_Setup.md` for the authoritative schema and
> `docs/14_Binding_Generation_Gaps.md` for why these went unnoticed.

## 1. Context

Phases 0-4 of the Farscape Maturation Plan establish Farscape as a production-quality C binding generator:

- **Phase 0-1**: Pilot project system, opaque handles, bitmask enums, error text pipeline, BAREWire descriptors, Wayland XML parser
- **Phase 2**: ROCm/HIP binding
- **Phase 3**: libdrm, libgbm, Wayland bindings; HelloWayland milestone
- **Phase 4**: XRT/XDNA binding; CPU+GPU+NPU buffer sharing

At the end of Phase 4, Farscape can parse C headers and XML protocol definitions, generate type-safe Clef bindings with idiomatic wrappers, and produce BAREWire descriptors for cross-processor memory exchange. It reads *interfaces*. It does not read *implementations*.

Phase 5 begins the transition from interface binding to algorithmic comprehension. MFEM, the finite element discretization library maintained by Lawrence Livermore National Laboratory, is the case study that drives this transition. The choice is deliberate: MFEM is a deep, well-designed C++ codebase with mathematically well-specified algorithms, operating in a domain (physical simulation) where Clef's dimensional type system and heterogeneous compute model have maximum payoff.

---

## 2. Why MFEM as the Forcing Function

### 2.1 The Binding-Port Spectrum

For most C libraries, a binding is the correct product. The library's implementation is opaque; the consumer cares about the API contract, not the internal algorithms. HIP, XRT, libdrm, libgbm, Wayland: all are cases where the implementation is maintained by upstream (AMD, Mesa, freedesktop.org) and Fidelity consumes them as external services.

MFEM is different. Its computational kernels are the product. The algorithms inside MFEM's element integration, quadrature rules, and solver loops are the work the developer cares about. Wrapping them preserves their IEEE 754 arithmetic limitations. Porting them to Clef replaces those limitations with target-aware precision (b-posit on FPGA, quire accumulation for lossless multiply-accumulate) and heterogeneous dispatch (GPU for parallel element sweeps, FPGA for accumulation phases).

The spectrum:

| Library | Correct Product | Why |
|---|---|---|
| libdrm | Binding | Kernel ABI interface; implementation is the driver |
| ROCm/HIP | Binding | GPU dispatch runtime; implementation is AMD's |
| libgbm | Binding | Buffer allocation service; implementation is Mesa |
| MFEM mesh I/O | Binding | Combinatorial; IEEE adequate; upstream maintains it |
| MFEM quadrature rules | Port | Pure data + accumulation; quire path changes precision |
| MFEM element integration | Port | Multiply-accumulate kernels; GPU + FPGA lowering |
| MFEM solver kernels | Port | Inner product precision; convergence improvement |

The "first bind, then port" progression is the maturation story. Farscape starts by generating conventional call-through bindings for MFEM (the same capability exercised in Phases 2-4). Then it extends to read implementation files, identify computational structure, and produce Clef source that preserves the algorithm while enabling enhanced lowering.

### 2.2 Template Maturity

MFEM exercises the full spectrum of C++ template patterns:

- **Class template hierarchies**: `FiniteElement` → `ScalarFiniteElement` → `H1_SegmentElement`, with virtual dispatch at each level
- **Template specialization**: Element types specialized by dimension (1D, 2D, 3D) and polynomial order
- **Expression templates**: Operator composition in the `BilinearForm` / `LinearForm` system
- **CRTP patterns**: Static polymorphism in integrator dispatch
- **Policy-based design**: Memory management and device dispatch via template parameters

Successfully ingesting MFEM's template inventory validates Farscape's C++ understanding across the patterns most commonly encountered in production numerical libraries (BLAS, LAPACK, PETSc, FFTW, Eigen).

### 2.3 Dimensional Type Payoff

MFEM's `DiffusionIntegrator` computes `∫ k ∇u · ∇v dx` where `k` is a coefficient, `u` and `v` are trial/test functions, and `x` is spatial position. In C++, every quantity is `double`. In Clef:

- `k` carries its physical dimension (thermal conductivity: `W/(m·K)`, elastic modulus: `Pa`, or any user-defined material property)
- The gradient operator introduces `1/length`
- Spatial integration introduces `length^d`
- The resulting stiffness matrix entry has dimensions the compiler verifies against the force vector

The algorithm is identical. The type information that exists only in comments, documentation, or tacit LLNL engineering knowledge becomes machine-checked invariant. This is the class of improvement that a binding cannot provide. Only ingestion and re-expression delivers it.

---

## 3. Phased Approach

### Phase 5A: Selective Binding

Farscape generates conventional call-through bindings for MFEM components that gain nothing from Clef's type system:

- **Mesh I/O**: Reading Gmsh, MFEM, VTK mesh formats
- **Mesh topology**: Element connectivity, adjacency graphs, boundary markers
- **Adaptive refinement**: Conforming and non-conforming mesh refinement
- **Visualization hooks**: GLVis or ParaView integration

These components are combinatorial or I/O-bound. They operate on integer topology and floating-point coordinates where double precision is adequate. The Farscape clang parser handles their C++ interfaces; the generated bindings link against MFEM's compiled library.

**Farscape capability required**: Everything from Phases 0-4 is sufficient. MFEM's C API surface follows the same opaque-handle patterns as HIP and XRT. The `.pilot.toml` handles multi-header scoping. No new code generator extensions needed.

**Pilot project sketch**:

```toml
# mfem.pilot.toml

[library]
name = "mfem"

[sources]
headers = [
    "/usr/include/mfem/mfem.hpp",
    "/usr/include/mfem/general/communication.hpp",
    "/usr/include/mfem/mesh/mesh.hpp"
]
include_paths = ["/usr/include/mfem"]
defines = ["MFEM_USE_MPI=0"]

[output]
mode = "fidelity"
directory = "./bindings/mfem"

[[namespace]]
name = "Fidelity.MFEM.Mesh"
library = "mfem"
prefixes = ["Mesh"]

[[namespace]]
name = "Fidelity.MFEM.IO"
library = "mfem"
functions = ["Mesh_Load", "Mesh_Save", "Mesh_Print"]
```

This phase produces immediately usable FEM mesh infrastructure for Clef programs, while MFEM's compiled library handles the heavy computation.

### Phase 5B: C++ Class and Template Parsing

Farscape extends its clang-based parsing from flat C function declarations to C++ class hierarchies and template structures. The clang JSON AST already contains this information; the issue is that Farscape's `CppParser.fs` and `Declaration` DU do not yet model it with enough fidelity.

**New capabilities**:

| Capability | What Clang Already Provides | What Farscape Needs |
|---|---|---|
| Class hierarchy extraction | `CXXRecordDecl` with base classes | `ClassDecl` extended with base types, virtual method table |
| Virtual dispatch analysis | `CXXMethodDecl` with `virtual` flag | Method resolution order for idiomatic Clef interface mapping |
| Template instantiation | `ClassTemplateSpecializationDecl` | Enumeration of concrete instantiations used in MFEM |
| Namespace and scope resolution | Fully qualified names in AST | Namespace-aware type resolution in TypeMapper |
| Operator overloads | `CXXOperatorCallExpr` | Mapping to Clef operator definitions |
| RAII detection | Destructor presence, resource acquisition in constructor | Mapping to Clef `use`/`IDisposable` or lifetime scoping |

This phase is substantial. MFEM's class hierarchy is deep (5+ levels for finite elements), uses virtual dispatch heavily, and relies on template specialization for dimension and order selection. The parser extensions must handle these patterns correctly before implementation analysis can begin.

**Plugify ABI Intelligence**: This is where the Plugify integration becomes central. [Plugify](https://github.com/untrustedmodders/plugify) is a modern C++ plugin manager whose multi-language support (C++, C#/.NET, Go, Python, Rust, D, Lua, JavaScript) has required it to accumulate deep, battle-tested knowledge of C++ ABI mechanics: virtual table layouts across compilers, name mangling schemes, calling conventions, struct packing rules, RTTI formats, and platform-specific variations between Clang, GCC, MSVC, and Apple Clang on x86 and ARM.

In Phases 0-4, Farscape targets C APIs and XML protocols; the target libraries (HIP, XRT, libdrm, libgbm, Wayland) all provide C shim interfaces. MFEM does not. Its API is native C++ with virtual dispatch, templates, and RAII. The architectural roadmap outlined in the SpeakEZ blog ("Binding F# to C++ in Farscape," September 2025) describes how Farscape will combine clang's parsed AST with Plugify's runtime-proven ABI knowledge to produce an **ABI Analysis Engine**:

1. Enriches clang's AST with platform-specific vtable layout, compiler quirks, and C++ standard variations
2. Extends type mapping to handle templates, inheritance hierarchies, and RAII patterns as idiomatic F# representations
3. Enables virtual method dispatch bindings without requiring a C shim intermediary
4. Preserves ABI metadata through the Composer compilation pipeline for LLVM LTO optimization

For Pilot, this means a third enrichment path: C headers route to clang, XML protocols route to XParsec, and C++ headers route to clang + Plugify ABI Analysis. The same `Declaration` types flow downstream; the ABI engine adds the knowledge needed to generate correct vtable-aware bindings.

### Phase 5C: Implementation Analysis

Farscape reads C++ function bodies, not just signatures. The target: MFEM's element-level computational kernels.

**New capabilities in Farscape**:

1. **Control flow graph extraction**: Parse function bodies from clang AST, build CFG with basic blocks and edges.
2. **Loop nest analysis**: Identify iteration bounds, loop-carried dependencies, accumulation patterns (the `for (i) for (j) elmat(i,j) += w * shape(i) * shape(j)` inner loop).
3. **Data flow analysis**: Def-use chains, aliasing through pointers, escape analysis for local allocations.
4. **Idiom recognition**: Multiply-accumulate, reduction, stencil, scatter-gather. Each idiom maps to a Clef functional composition pattern.
5. **Side effect classification**: Which operations are pure, which mutate state, which perform I/O. Pure functions port directly to Clef; stateful operations require restructuring.

**Target kernels**:

| MFEM Component | LOC (approx) | Idiom | Clef Target |
|---|---|---|---|
| Shape function evaluation (`fem/fe/`) | ~15K | Pure function: geometry + reference point → values + gradients | Direct port; dimensional type annotations on geometric inputs |
| Quadrature rules (`fem/intrules.cpp`) | ~3K | Pure data + weighted summation | Port with quire accumulation path on FPGA |
| Local matrix assembly (`fem/bilininteg.cpp`) | ~8K | Multiply-accumulate kernel | GPU (parallel element sweep) + FPGA (accumulation) |
| Solver kernels (`linalg/solvers.cpp`) | ~4K | SpMV + inner product accumulation | Posit inner products for convergence improvement |

### Phase 5D: Algorithmic Re-Expression

The ported kernels are re-expressed in functional Clef with the full type system:

**Dimensional type integration**: Every physical quantity carries dimensional annotations through the compilation pipeline. Material properties, geometric dimensions, load magnitudes, and solution values are type-checked at every composition boundary.

**Deterministic memory model**: MFEM's `DenseMatrix` heap allocations become arena-scoped allocations tied to element computation actors. BAREWire handles zero-copy handoff between GPU integration and FPGA accumulation passes.

**Heterogeneous lowering**: The C++ inner loop `for (i) for (j) elmat(i,j) += w * shape(i) * shape(j)` is a multiply-accumulate kernel. In MFEM, it lowers to IEEE 754 regardless of target. In Clef, the same computation expressed functionally lowers to SIMT on GPU for the parallel element sweep, b-posit with quire accumulation on FPGA for the assembly reduction, or conventional FP64 on CPU for debugging. The algorithm is unchanged; the lowering is target-aware.

**Actor-model orchestration**: The FEM solve lifecycle (mesh partitioning → element integration → assembly → solve → post-processing) maps to Prospero/Olivier actor supervision. Each phase is an actor with defined message protocols, fault isolation, and resource ownership.

---

## 4. Farscape Evolution Milestones

The MFEM case study drives specific Farscape capability milestones:

### 4.1 Header Parsing (Complete after Phase 4)

- C header parsing via clang
- Type mapping (C primitives → Clef NTU types)
- Function signature extraction and binding generation
- Opaque handles, bitmask enums, error text pipeline
- BAREWire struct descriptors
- XML protocol parsing (Wayland path)

### 4.2 C++ Class and Template Parsing (Phase 5B)

- Class hierarchy extraction with virtual dispatch analysis
- Template instantiation enumeration
- Namespace and scope resolution
- Operator overload mapping to Clef operators
- RAII pattern detection and lifetime mapping
- Plugify ABI Analysis Engine integration (vtable layouts, name mangling, platform-specific calling conventions)

### 4.3 Implementation Analysis (Phase 5C)

- Control flow graph extraction from function bodies
- Loop nest analysis (iteration bounds, dependencies, accumulation patterns)
- Data flow analysis (def-use chains, aliasing)
- Idiom recognition (multiply-accumulate, reduction, stencil, scatter-gather)
- Side effect classification for functional re-expression

### 4.4 Algorithmic Re-Expression (Phase 5D)

- C/C++ loop nests → Clef `fold`, `map`, `scan` compositions
- Mutable state → copy-and-update with arena allocation
- Pointer arithmetic → typed memory region access via BAREWire
- IEEE 754 arithmetic → precision-aware numeric types with target selection
- C++ template specialization → Clef SRTP-based static resolution

### 4.5 Composer Integration (Long-Term)

Farscape's parsing, analysis, and re-expression capabilities fold into the Composer compiler. Foreign source becomes a first-class input:

```
Foreign Source (C/C++/Rust/Python)
    ↓
Farscape Analysis (Composer frontend)
    ↓
Clef IR (typed, dimensionally annotated)
    ↓
PSG (Program Semantic Graph)
    ↓
Alex (platform-aware middle-end)
    ↓
MLIR (dialect selection per target)
    ↓
Native Binary / FPGA Bitstream / GPU Kernel
```

At this stage, the distinction between "binding" and "port" dissolves. Composer reads foreign source and produces the same IR that native Clef code produces. Interface/wrapper libraries are a subset of this capability, not the primary output.

---

## 5. The Transcribe Connection

The implementation analysis and algorithmic re-expression capabilities developed for MFEM are exactly what the Transcribe feature area in Atelier needs. Transcribe is the Atelier Pro/Enterprise feature that opens non-Clef source files and shows developers what Fidelity can do with their code. For C/C++, Transcribe consumes Farscape's infrastructure:

| Farscape Capability | Transcribe Feature |
|---|---|
| Header parsing + binding generation | "Generate Clef bindings for this library" |
| Class hierarchy extraction | Conversion feasibility report: what can be ported, what should stay as binding |
| Loop nest analysis | "This accumulation loop is a quire candidate for FPGA" |
| Idiom recognition | "This stencil pattern maps to GPU tile dispatch" |
| Side effect classification | "This function is pure; direct port. This function mutates global state; restructure required." |
| Dimensional type suggestion | "This `double` represents thermal conductivity in W/(m·K); Clef can enforce this" |

The developer opens a `.cpp` file in Atelier, Transcribe lights up, and they see inline diagnostics. Farscape's clang infrastructure is the engine; Transcribe is the interface. The MFEM maturation track builds the engine capabilities that make Transcribe's C/C++ analysis substantive.

---

## 6. MFEM Component Priority

| Component | LOC (approx) | Phase | Product |
|---|---|---|---|
| Mesh I/O, topology, refinement | ~30K | 5A | Binding (link against compiled MFEM) |
| Visualization hooks | ~5K | 5A | Binding |
| Element definitions (`fem/fe/`) | ~15K | 5C/5D | Port (dimensional types, pure functions) |
| Quadrature rules (`fem/intrules.cpp`) | ~3K | 5C/5D | Port (quire accumulation path) |
| Bilinear integrators (`fem/bilininteg.cpp`) | ~8K | 5C/5D | Port (GPU + FPGA lowering) |
| Linear form (`fem/linearform.cpp`) | ~2K | 5C/5D | Port |
| Sparse matrix (`linalg/sparsemat.cpp`) | ~6K | 5C/5D | Port (posit SpMV) |
| Solvers (`linalg/solvers.cpp`) | ~4K | 5C/5D | Port (inner product precision) |
| Parallel mesh (`mesh/pmesh.cpp`) | ~12K | 5A | Binding (MPI infrastructure) |
| General utilities | ~10K | 5A | Binding |

The binding-phase components (~60K LOC) use Phase 4 capabilities. The port-phase components (~38K LOC) require Phase 5B-5D capabilities.

---

## 7. Strategic Position

The MFEM port positions Fidelity in a space no existing tool occupies:

**For the FEM community**: A safer, more expressive frontend to battle-tested algorithms, with precision capabilities (b-posit on FPGA) that IEEE 754 cannot match for ill-conditioned problems. Engineers keep the numerical methods they trust; they gain dimensional safety and heterogeneous acceleration.

**For the Fidelity ecosystem**: A deep validation of Farscape's algorithmic ingestion capability against one of the most template-heavy, numerically sophisticated C++ codebases in the DOE portfolio. If Farscape can ingest MFEM, it can ingest the broader numerical C/C++ ecosystem (BLAS, LAPACK, PETSc, FFTW, Eigen).

**For the Atelier product**: The analysis capabilities developed for MFEM are the same capabilities that make Transcribe's C/C++ analysis valuable in the IDE. Every diagnostic, every idiom classification, every dimensional suggestion that works on MFEM code works on any C/C++ codebase the developer opens. The MFEM investment compounds across the entire Transcribe feature surface.

**For the investor narrative**: DOE-funded mesh infrastructure, ported to a language with dimensional type safety, compiled to heterogeneous targets including FPGA-accelerated posit arithmetic. The person-centuries of LLNL optimization are preserved. The precision limitations are removed. The type safety is added.

---

## 8. Dependency Chain

```
Phases 0-3 (HelloWayland)
    │
    ▼
Phase 4 (XRT/XDNA binding)
    │
    ▼
Phase 5A: MFEM selective binding
    │
    ▼
Phase 5B: C++ class + template parsing
    │
    ▼
Phase 5C: Implementation analysis
    │ (feeds into)
    ├──────────────────┐
    ▼                  ▼
Phase 5D:          Transcribe
MFEM algorithmic   (Atelier C/C++
re-expression       analysis feature)
    │
    ▼
Composer integration
(foreign source as first-class input)
```

---

*Companion documents: "Farscape Maturation Plan: Phases 0-3 Through HelloWayland" and "Farscape Phase 4: NPU Binding via XRT/XDNA"*

*SpeakEZ Technologies | Fidelity Framework*
