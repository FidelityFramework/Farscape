# Farscape: From Binding Generator to Algorithmic Ingestion Pipeline

## A Roadmap Vision Through the MFEM Case Study

**SpeakEZ Technologies | Fidelity Framework**
**February 2026**

---

## 1. The Long-Term Trajectory

Farscape's current capability, parsing C/C++ headers and generating type-safe Clef bindings, is the first notch point on a longer arc. The end state is full algorithmic ingestion: reading C/C++ (and eventually Rust, Python, Go) implementations, understanding the computation they express, and producing Clef source that captures the algorithm with enhanced type information, deterministic memory semantics, and heterogeneous target lowering.

The eventual integration path folds Farscape's parsing and analysis capabilities into the Composer compiler itself. At that point, the distinction between "binding" and "port" dissolves. Composer reads foreign source, produces Clef IR, and lowers it through the same PSG → Alex → MLIR pipeline that native Clef code uses. Wrapper/interface libraries fall out as a byproduct of the full port capability, not as the primary output.

MFEM, the finite element discretization library maintained by Lawrence Livermore National Laboratory, is the ideal case study for maturing Farscape toward this goal. It provides a deep, well-designed C++ template inventory, mathematically well-specified algorithms, and a problem domain (physical simulation) where Clef's dimensional type system and heterogeneous compute model have maximum payoff.

---

## 2. Why MFEM

### 2.1 Template Maturity and Variety

MFEM's codebase exercises the full spectrum of C++ template patterns that Farscape must eventually handle:

- **Class template hierarchies**: `FiniteElement` → `ScalarFiniteElement` → `H1_SegmentElement`, with virtual dispatch at each level
- **Template specialization**: Element types specialized by dimension (1D, 2D, 3D) and polynomial order
- **Expression templates**: Operator composition in the `BilinearForm` / `LinearForm` system
- **CRTP patterns**: Static polymorphism in integrator dispatch
- **Template metaprogramming**: Compile-time dimension selection in mesh and element space construction
- **Policy-based design**: Memory management and device dispatch configured through template parameters

This is not a toy target. Successfully ingesting MFEM's template inventory validates Farscape's C++ understanding across the patterns most commonly encountered in production numerical libraries.

### 2.2 Algorithm Suitability

FEM algorithms are mathematically compact at the kernel level. A Gauss-Legendre quadrature routine, shape function evaluation, local stiffness matrix assembly, sparse matrix-vector product, and Krylov solver iteration loop: each is individually small, well-specified, and amenable to functional re-expression. The challenge is not in understanding what the algorithm does; it is in preserving the numerical insights (quadrature point selection, element shape function optimizations, solver convergence techniques) that represent person-centuries of DOE-funded refinement.

### 2.3 Dimensional Type Payoff

MFEM's `DiffusionIntegrator` computes `∫ k ∇u · ∇v dx` where `k` is a coefficient, `u` and `v` are trial/test functions, and `x` is spatial position. In C++, every quantity is `double`. In Clef:

- `k` carries its physical dimension (thermal conductivity: `W/(m·K)`, elastic modulus: `Pa`, or any user-defined material property)
- The gradient operator introduces `1/length`
- Spatial integration introduces `length^d`
- The resulting stiffness matrix entry has dimensions the compiler verifies against the force vector

The algorithm is identical. The type information that exists only in comments, documentation, or the tacit knowledge of LLNL engineers becomes machine-checked invariant. This is the class of improvement that a binding cannot provide. Only ingestion and re-expression delivers it.

### 2.4 Heterogeneous Compute Mapping

MFEM already separates concerns in a way that maps to Fidelity's heterogeneous dispatch model:

| MFEM Component | Clef Target | Rationale |
|---|---|---|
| `Mesh`, `FiniteElementSpace` | CPU (Prospero orchestration) | Combinatorial topology; IEEE adequate |
| `BilinearFormIntegrator` element loops | GPU (SIMT) | Parallel element-level computation |
| Assembly accumulation, solver inner products | FPGA (b-posit quire) | Lossless multiply-accumulate |
| Problem setup, I/O, visualization | CPU | Lifecycle management |

The C++ inner loop `for (int i = 0; i < dof; i++) for (int j = 0; j < dof; j++) elmat(i,j) += w * shape(i) * shape(j);` is a multiply-accumulate kernel. In MFEM it lowers to IEEE 754 regardless of target. In Clef, the same computation expressed functionally lowers to SIMT on GPU for the parallel element sweep, b-posit with quire accumulation on FPGA for the assembly reduction, or conventional FP64 on CPU for debugging and validation. The algorithm does not change; the lowering does.

This is the b-posit precision argument that makes a pure Farscape binding insufficient. MFEM's numerical stack is IEEE 754 from the foundation. Every `SparseMatrix`, every Krylov iteration, every element integration accumulates in `double`. Wrapping MFEM preserves its arithmetic limitations. Porting to Clef replaces them with target-aware precision, including lossless quire accumulation on FPGA for the operations where IEEE 754 loses bits through catastrophic cancellation.

---

## 3. Phased Approach

### Phase 1: Selective Binding (Near-Term)

Farscape generates conventional call-through bindings for MFEM components that gain nothing from Clef's type system or compute targeting:

- **Mesh I/O**: Reading Gmsh, MFEM, VTK mesh formats
- **Mesh topology**: Element connectivity, adjacency graphs, boundary markers
- **Adaptive refinement**: Conforming and non-conforming mesh refinement algorithms
- **Visualization hooks**: Integration with GLVis or ParaView for result inspection

These components are combinatorial or I/O-bound. They operate on integer topology and floating-point coordinates where double precision is more than adequate. The Farscape header parser handles their C++ interfaces; the generated bindings link against MFEM's compiled library.

This phase exercises Farscape's existing capabilities (XParsec-based C/C++ header parsing, type mapping, binding code generation) and produces immediately usable FEM mesh infrastructure for Clef programs.

### Phase 2: Kernel Port (Medium-Term)

Farscape's analysis capability extends from headers to implementation files. The target is MFEM's element-level computational kernels:

**Shape function evaluation**: Polynomial basis functions (Lagrange, Bernstein, Nédélec, Raviart-Thomas) evaluated at quadrature points. These are pure functions: given element geometry and a reference point, return function values and gradients. Their port to Clef is direct, with dimensional type annotations on the geometric inputs and gradient outputs.

**Quadrature rules**: Gauss-Legendre, Gauss-Lobatto, and specialized quadrature for triangles and tetrahedra. Pure data (points and weights) with accumulation logic. The quire accumulation path on FPGA applies directly to the weighted summation.

**Local matrix assembly**: The element-level stiffness, mass, and load computations. These are the multiply-accumulate kernels where posit arithmetic changes the precision story. In Clef, they lower to GPU for parallel evaluation across elements and FPGA for the accumulation phase.

**Solver kernels**: Conjugate gradient, GMRES, MINRES iteration loops. Each iteration performs sparse matrix-vector products and inner product accumulations. Porting to Clef enables the quire accumulation path for inner products, potentially improving convergence for ill-conditioned problems.

This phase is where Farscape matures from binding generator to algorithmic ingestion tool. It must parse C++ source, identify the computational structure (loop nests, accumulation patterns, data dependencies), map C++ types to Clef types with dimensional annotations, and produce Clef source that preserves the algorithm while enabling target-aware lowering.

### Phase 3: Full Re-Expression (Longer-Term)

The ported kernels are re-expressed in functional terms with Clef's full type system:

**Dimensional type integration**: Every physical quantity in the FEM computation carries its dimensional annotation through the compilation pipeline. Material properties, geometric dimensions, load magnitudes, and solution values are type-checked for dimensional consistency at every composition boundary.

**Deterministic memory model**: MFEM's `DenseMatrix` heap allocations become arena-scoped allocations tied to element computation actors. BAREWire handles zero-copy handoff between the GPU integration pass and the FPGA accumulation pass. Memory lifetime is a compilation concern, not a runtime one.

**Actor-model orchestration**: The FEM solve lifecycle (mesh partitioning → element integration → assembly → solve → post-processing) maps to Prospero/Olivier actor supervision. Each phase is an actor with defined message protocols, fault isolation, and resource ownership.

**Composable computation expressions**: Following the patterns demonstrated in production Clef FEM code (result computation expressions for material lookups, Kleisli composition for geometric transformations, pipe operators for mesh traversal), the Clef FEM API exposes FEM workflows as composable typed computations, not imperative solver calls.

---

## 4. Farscape Evolution Milestones

The MFEM case study drives specific capability milestones in Farscape's development:

### 4.1 Header Parsing (Current)

- C header parsing via XParsec
- Type mapping (C primitives → Clef NTU types)
- Function signature extraction and binding generation
- CMSIS HAL and peripheral header support

### 4.2 C++ Class and Template Parsing

- Class hierarchy extraction with virtual dispatch analysis
- Template instantiation enumeration
- Namespace and scope resolution
- Operator overload mapping to Clef operators
- RAII pattern detection and mapping to Clef lifetime semantics

### 4.3 Implementation Analysis

- Control flow graph extraction from C/C++ function bodies
- Loop nest analysis (iteration bounds, dependencies, accumulation patterns)
- Data flow analysis (def-use chains, aliasing)
- Idiom recognition (multiply-accumulate, reduction, stencil, scatter-gather)
- Side effect classification for functional re-expression

### 4.4 Algorithmic Re-Expression

- C/C++ loop nests → Clef `fold`, `map`, `scan` compositions
- Mutable state → copy-and-update with arena allocation
- Pointer arithmetic → typed memory region access via BAREWire
- IEEE 754 arithmetic → precision-aware numeric types with target selection
- C++ template specialization → Clef SRTP-based static resolution

### 4.5 Multi-Language Ingestion

- Rust: Ownership model maps to Clef lifetimes; trait bounds map to SRTP constraints
- Python: NumPy/SciPy computational patterns → typed Clef equivalents
- Go: Goroutine/channel patterns → Prospero/Olivier actor equivalents
- C: Subset of C++ path; simpler but wider applicability

### 4.6 Composer Integration

Farscape's parsing, analysis, and re-expression capabilities fold into the Composer compiler. Foreign source becomes a first-class input to the compilation pipeline:

```
Foreign Source (C/C++/Rust/Python/Go)
    ↓
Farscape Analysis (now a Composer frontend)
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

At this stage, the distinction between "binding" and "port" no longer exists. Composer reads foreign source and produces the same intermediate representation that native Clef code produces. Interface/wrapper libraries are a subset of this capability, generated when the user wants call-through access instead of full re-expression.

---

## 5. The Fidelity.Physics Foundation

Orthogonal to Farscape's evolution, Fidelity.Physics provides the dimensional type vocabulary that FEM computation requires. This library is a compiler-level and Alloy-level concern, not an MFEM-specific component:

**Dimensional primitives**: SI base dimensions (Length, Mass, Time, Temperature, Current, Amount, Luminous Intensity) and derived dimensions (Force, Pressure, Energy, Power, etc.) composed via the type system. Clef's intrinsic units of measure, extended beyond F#'s numeric-only constraint to support non-numeric dimensional types.

**Physical property types**: Scalar, vector, and tensor fields with dimensional annotations. Material property records (Young's modulus, Poisson's ratio, thermal conductivity, density) with compile-time unit enforcement.

**Precision-aware numeric types**: Integration with b-posit arithmetic. Numeric operations carry precision requirements that inform backend selection during lowering.

**Algebraic structures**: Monadic/comonadic patterns for field computations over spatial domains. Composable physical transformations verified for dimensional consistency at every composition boundary.

Fidelity.Physics is equally useful for FEM, finite difference, boundary element, spectral, particle-based (SPH, DEM), and gravitational (ThreeBody) simulations. It is the shared foundation; specific numerical methods are consumers.

---

## 6. Strategic Position

The MFEM port positions Fidelity in a space no existing tool occupies:

**For the FEM community**: A safer, more expressive frontend to battle-tested algorithms, with precision capabilities (b-posit on FPGA) that IEEE 754 cannot match for ill-conditioned problems. Engineers keep the numerical methods they trust; they gain dimensional safety and heterogeneous acceleration.

**For the Fidelity ecosystem**: A deep validation of Farscape's algorithmic ingestion capability against one of the most template-heavy, numerically sophisticated C++ codebases in the DOE portfolio. If Farscape can ingest MFEM, it can ingest BLAS, LAPACK, PETSc, and the broader numerical C/C++ ecosystem.

**For the investor narrative**: DOE-funded mesh infrastructure, ported to a language with dimensional type safety, compiled to heterogeneous targets including FPGA-accelerated posit arithmetic. The person-centuries of optimization are preserved. The precision limitations are removed. The type safety is added. Each of these alone is incremental; together they represent a qualitative shift in how physical simulation software is built.

---

## Appendix: MFEM Components and Port Priority

| Component | LOC (approx) | Port Priority | Rationale |
|---|---|---|---|
| `fem/fe/` (element definitions) | ~15K | High | Pure functions; dimensional types high-value |
| `fem/intrules.cpp` (quadrature) | ~3K | High | Accumulation kernels; quire path |
| `fem/bilininteg.cpp` | ~8K | High | Element integration; GPU + FPGA |
| `fem/linearform.cpp` | ~2K | High | Load assembly; same pattern |
| `linalg/sparsemat.cpp` | ~6K | Medium | SpMV kernels; posit accumulation |
| `linalg/solvers.cpp` | ~4K | Medium | CG/GMRES; inner product precision |
| `mesh/mesh.cpp` | ~20K | Low (bind) | Topology; IEEE adequate |
| `mesh/pmesh.cpp` | ~12K | Low (bind) | Parallel mesh; MPI infrastructure |
| `general/` (utilities) | ~10K | Low (bind) | I/O, timing, memory management |

---

*This document is maintained as part of the Farscape project within the Fidelity Framework.*
*License: MIT*
