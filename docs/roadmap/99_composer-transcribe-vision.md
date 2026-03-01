# Composer Transcribe

## Vision for Full Algorithmic Port Capability

**SpeakEZ Technologies | Fidelity Framework**
**February 2026 | Speculative Design Document**

---

## 1. Premise

The C and C++ ecosystems contain the accumulated optimization work of the entire computing industry. Numerical libraries, operating system interfaces, cryptographic implementations, protocol stacks, hardware drivers, graphics engines, physics simulators, database kernels: decades of engineering distilled into source code that is simultaneously invaluable and trapped in languages that cannot express the safety properties, dimensional semantics, or heterogeneous targeting that modern systems require.

Farscape demonstrated that type-safe bindings can be generated from C/C++ headers, giving Clef programs access to native libraries without manual interop code. The MFEM case study revealed that bindings are insufficient when the computational core needs to participate in Clef's type system and precision-aware lowering. The logical conclusion: Composer should be able to read foreign source, understand the algorithm it expresses, and produce Clef source that preserves the computation while gaining everything the Fidelity pipeline provides.

Transcribe is that capability. It is not a transpiler. Transpilers perform syntactic translation, mapping constructs in one language to superficially equivalent constructs in another. Transcribe performs *algorithmic comprehension*: extracting the computational intent from foreign source, representing it in Clef's typed intermediate form, and re-expressing it in idiomatic Clef that the Composer pipeline can lower to any supported target with full dimensional type propagation, deterministic memory management, and precision-aware arithmetic.

The name reflects the analogy to musical transcription: hearing a performance (the C/C++ source), understanding its structure (harmony, rhythm, voicing), and writing it down in a different notation (Clef) that captures the musical intent while enabling new interpretations (heterogeneous target lowering, posit arithmetic, actor-model concurrency).

---

## 2. The Multi-Pre-Process Method

### 2.1 The Problem with Direct Scraping

A C or C++ source file as written is not the code that the compiler sees. The preprocessor transforms the source before parsing: expanding macros, evaluating conditional compilation directives, including headers, and performing token pasting. A direct scrape of the source text encounters:

- **Conditional compilation**: `#ifdef`, `#if defined(...)`, `#elif` blocks that select platform-specific, configuration-specific, or feature-specific code paths. A single preprocessing pass reveals only one path. The others are invisible.
- **Macro expansion**: Function-like macros that generate code at preprocessing time. The macro definition and its expansion are different texts with different semantics. A direct scrape sees the macro invocation; it does not see the generated code.
- **Include resolution**: Header files that define types, constants, and inline functions. The include graph may be deep (MFEM headers include PETSc headers which include MPI headers which include platform headers) and order-dependent.
- **Template instantiation** (C++): Templates exist as patterns until instantiated with specific type arguments. The compiler generates concrete code for each instantiation. A header scrape sees the template definition; it does not see the instantiated code.

Farscape's header parsing handles the common case: well-structured headers with explicit function declarations and type definitions. For full algorithmic port, this is insufficient. The computational kernels live in `.cpp` implementation files, behind preprocessor conditionals, inside template instantiations, wrapped in macros.

### 2.2 Multi-Pass Preprocessing

Transcribe addresses this through systematic multi-pass preprocessing of the same source under controlled variations of the preprocessor state. The method:

**Step 1: Enumerate the define space.** Scan the source and its transitive include graph for all `#ifdef`, `#if defined(...)`, `#ifndef`, and `#elif` directives. Extract the set of preprocessor symbols that govern conditional compilation. For MFEM, this includes symbols like `MFEM_USE_CUDA`, `MFEM_USE_HIP`, `MFEM_USE_OPENMP`, `MFEM_USE_MPI`, `MFEM_USE_PETSC`, `MFEM_USE_SUNDIALS`, `MFEM_DEBUG`, and target-specific defines.

**Step 2: Generate define configurations.** Produce a combinatorial set of define configurations that cover the relevant code paths. Not all combinations are meaningful; static analysis of the directive structure prunes contradictory or redundant configurations. For MFEM, the GPU backend defines are mutually exclusive (`MFEM_USE_CUDA` xor `MFEM_USE_HIP` xor neither), while `MFEM_USE_MPI` is orthogonal to the GPU selection.

**Step 3: Preprocess each configuration.** Run the C/C++ preprocessor (via libclang or a standalone `cpp` invocation) for each meaningful configuration. Each pass produces a fully expanded, macro-resolved, conditionally-selected translation unit. The output is plain C/C++ with no preprocessor directives remaining.

**Step 4: Differential AST construction.** Parse each preprocessed output into an AST. Compare the ASTs across configurations to identify:

- **Invariant code**: Present identically in all configurations. This is the core algorithm.
- **Platform-variant code**: Present in some configurations, absent or different in others. This maps to Clef's platform-targeting mechanism.
- **Feature-variant code**: Gated by feature flags. This maps to Clef's conditional compilation or module-level feature selection.
- **Debug/release variants**: Gated by debug defines. The debug path often contains assertions and range checks that inform Clef's type constraints.

**Step 5: Unified algorithmic representation.** Merge the differential ASTs into a single representation that captures all code paths with their activation conditions. This is the input to the transcription phase proper.

### 2.3 Template Instantiation Discovery

C++ templates add a second axis of hidden code. Transcribe handles this through:

**Explicit instantiation enumeration**: Scanning the source for explicit template instantiations (`template class Foo<int>;`) and recording the type arguments.

**Implicit instantiation discovery**: Building the source with libclang in semantic analysis mode, which triggers implicit template instantiation for all types used in the translation unit. The resulting AST contains fully instantiated class and function bodies.

**Pattern extraction**: Comparing multiple template instantiations to identify the generic algorithm (the template pattern) and the specialization points (where type-specific behavior diverges). This is the inverse of template instantiation: recovering the polymorphic structure from its monomorphic expansions. In Clef, this maps to SRTP (statically resolved type parameters) constraints.

**Specialization detection**: Identifying explicit and partial template specializations that override the primary template for specific type arguments. These map to Clef's constrained type resolution, where specific implementations are selected based on type properties (dimensional annotations, numeric precision, memory region).

### 2.4 What Multi-Pre-Process Reveals That Direct Scraping Cannot

Consider MFEM's `BilinearFormIntegrator::AssembleElementMatrix`. A direct scrape of the header shows a virtual function signature. A direct scrape of the implementation file, preprocessed once, shows the CPU code path. Multi-pre-process reveals:

- The CPU path with serial loop nests (no GPU defines)
- The CUDA device kernel path (`MFEM_USE_CUDA` defined)
- The HIP device kernel path (`MFEM_USE_HIP` defined)
- The OpenMP-annotated loop path (`MFEM_USE_OPENMP` defined)
- The debug path with element matrix symmetry assertions (`MFEM_DEBUG` defined)

Transcribe sees all five simultaneously. It can identify the invariant algorithm (the element-level multiply-accumulate) and the target-variant dispatch (how that algorithm is parallelized or accelerated). In Clef, this becomes a single function definition whose target-specific realization is resolved not by the library, but by the consuming application's platform declaration. This is a fundamental departure from C/C++ conditional compilation, where the target is baked into the library at build time. In Fidelity, it is deferred to the point of application composition.

The debug assertions are equally valuable. An `MFEM_DEBUG` block that checks `elmat.IsSymmetric()` tells Transcribe that the element matrix has a symmetry invariant. In Clef, this becomes a type constraint: the matrix type carries a `Symmetric` property that the compiler enforces, eliminating the need for a runtime assertion.

---

## 3. The Transcription Pipeline

### 3.1 Foreign Source Ingestion

```
Foreign Source (.c, .cpp, .h, .hpp)
    │
    ├── Preprocessor Symbol Extraction
    │       └── Enumerate #ifdef/#if defined space
    │
    ├── Configuration Generation
    │       └── Prune contradictory combinations
    │
    ├── Multi-Pass Preprocessing
    │       └── One fully-expanded TU per configuration
    │
    ├── Per-Configuration AST Construction (via libclang)
    │       └── Full semantic analysis including template instantiation
    │
    └── Differential AST Merge
            └── Unified representation with activation predicates
```

### 3.2 Algorithmic Comprehension

The merged AST enters a series of analysis passes that extract computational intent:

**Data flow analysis**: Def-use chains, reaching definitions, live variable analysis. Identifies which values flow into which computations, independent of the C/C++ variable naming.

**Loop analysis**: Iteration space extraction (bounds, stride, dependencies). Classification of loop bodies (map, fold/reduce, scan, stencil, scatter, gather). Detection of accumulation patterns (the `+=` idiom that signals potential quire accumulation benefit).

**Aliasing analysis**: Pointer aliasing classification (must-alias, may-alias, no-alias). This is the hardest problem in C/C++ analysis and the most consequential for Clef port quality. Aliasing uncertainty forces conservative assumptions; Clef's ownership model eliminates the ambiguity.

**Side effect classification**: Pure functions (no side effects), functions with local mutation only (convertible to copy-and-update), functions with I/O or global state (requiring actor-model wrapping). Most numerical kernels fall into the first two categories.

**Memory pattern analysis**: Stack allocation, heap allocation (malloc/new), arena allocation, pool allocation, RAII-guarded resources. Each maps to a specific Clef memory strategy (stack, arena-scoped, actor-owned, BAREWire region).

**Idiom recognition**: Higher-level pattern detection built on the foundational analyses:

| C/C++ Idiom | Clef Expression |
|---|---|
| `for (i=0; i<n; i++) out[i] = f(in[i])` | `Array.map f input` |
| `for (i=0; i<n; i++) acc += a[i] * b[i]` | `Array.fold2 (+) 0 (Array.map2 (*) a b)` with quire annotation |
| `for (i=0; i<n; i++) out[i] = f(out[i-1], in[i])` | `Array.scan f init input` |
| `if (p) { ... } else { ... }` on error codes | `Result.bind` / computation expression |
| `try { ... } catch (...) { ... }` | `Result` type with typed error cases |
| `mutex.lock(); ...; mutex.unlock()` | Actor message passing (no shared mutable state) |
| `new T(...); ...; delete p;` | Arena-scoped allocation or actor-owned resource |
| `memcpy(dst, src, n)` | BAREWire zero-copy transfer or typed copy |
| Virtual dispatch on base pointer | SRTP static resolution (where possible) or DU-based dispatch |

### 3.3 Dimensional Inference

For physics and engineering code, Transcribe performs dimensional analysis on the extracted algorithm:

**Comment and documentation mining**: Variable names, function documentation, and inline comments frequently contain dimensional information. `// thermal conductivity [W/(m*K)]`, `double E; // Young's modulus (Pa)`, `float dt; // time step (seconds)`. These annotations are heuristic, not authoritative, but provide strong initial dimensional assignments.

**Dimensional propagation**: Given initial dimensional assignments (from comments, naming conventions, or user-provided annotations), propagate dimensions through the data flow graph. Addition requires dimensional equality; multiplication composes dimensions; comparison requires dimensional equality. Inconsistencies flag either incorrect initial assignments or genuine dimensional bugs in the original code.

**Dimensional ambiguity resolution**: Where propagation leaves ambiguity (a bare `double` with no contextual information), Transcribe marks the value with an unresolved dimensional variable. The developer resolves these during port review. The count of unresolved variables is a quality metric for the transcription.

**Constant identification**: Numeric literals that appear in physics code often represent physical constants or conversion factors. `9.81` is likely gravitational acceleration (`m/s²`). `1.380649e-23` is Boltzmann's constant (`J/K`). Transcribe maintains a constant database for automated identification and dimensional annotation.

### 3.4 Clef Source Generation

The analyzed, dimensionally-annotated algorithmic representation generates idiomatic Clef source:

**Function signatures**: C/C++ parameter lists become Clef function signatures with dimensional type annotations. `void computeForce(double* x, double* y, double* f, int n, double G)` becomes a function accepting `Array<Position<m>>`, `Array<Mass<kg>>`, returning `Array<Force<N>>`, parameterized by gravitational constant `G : GravitationalConstant`.

**Loop nests to functional combinators**: Imperative loop structures become `map`, `fold`, `scan`, `filter`, and their parallel variants. The accumulation pattern determines whether quire annotation is applied.

**Mutable state to copy-and-update**: Local mutable variables become `let` bindings with copy-and-update syntax where the mutation pattern permits. Accumulator variables become fold state.

**Error handling**: C error codes and C++ exceptions become Clef `Result` types with domain-specific error cases. The `taskResult` computation expression pattern from the Norwegian FEM presentation is the model: composing effectful computations with typed error propagation.

**Memory management**: C `malloc`/`free` and C++ `new`/`delete` become arena-scoped allocations. RAII patterns map to Clef's deterministic lifetime model. Pointer arithmetic becomes typed region access through BAREWire semantics.

**Target annotations**: Where multi-pre-process revealed GPU/OpenMP/SIMD code paths, Transcribe generates target-agnostic Clef source with platform capability requirements expressed as type constraints. The function does not say "run this on CUDA"; it says "this kernel requires parallel map over N elements with multiply-accumulate reduction." Platform binding resolution, the selection of GPU, NPU, FPGA, or CPU to satisfy that requirement, is deferred to the application's platform declaration. See Section 4.

### 3.5 Port Review Interface

Transcribe does not produce final code in a single pass. It produces a draft with confidence annotations:

- **High confidence**: Pure functions with clear data flow, well-matched idiom patterns, confirmed dimensional annotations. These can be accepted without modification.
- **Medium confidence**: Functions with resolved aliasing but unconfirmed dimensional assignments, or functions where the idiom match is approximate. These require review.
- **Low confidence**: Functions with unresolved aliasing, pointer casting, inline assembly, or heavy macro usage. These require manual intervention.
- **Untranscribable**: `setjmp`/`longjmp`, computed goto, self-modifying code, platform-specific intrinsics with no Clef equivalent. These are flagged for manual port or routed to Transpose for typed dynamic binding (see Section 8.2).

The confidence distribution across a library provides a project-level quality metric. For MFEM's element integration kernels (pure numerical computation), high confidence coverage should exceed 80%. For MFEM's mesh I/O code (file format parsing with heavy pointer manipulation), coverage will be lower, and a Transpose binding is the pragmatic choice.

---

## 4. Declaration-Dependent Targeting and the BAREWire Contract

### 4.1 The Fundamental Departure from C/C++ Build Models

In C/C++, multi-targeting is resolved at library build time. MFEM compiled with `MFEM_USE_CUDA` produces a CUDA-linked binary. MFEM compiled without it produces a CPU-only binary. The target selection is baked into the `.so` or `.a` artifact. The consuming application inherits whatever the library was compiled for.

Fidelity inverts this. A transcribed library produces Clef source containing the invariant algorithm and its platform capability requirements. The consuming *application* declares its available platforms in the `.fidproj` file (CPU, GPU model, NPU, FPGA sidecar, MCU target). Composer resolves the capability requirements against the declared platforms during compilation of the application, not during compilation of the library. The library is not "built for CUDA" or "built for CPU." It is built as typed, target-agnostic computation that the application's platform declaration resolves to concrete lowering.

This is what enables the NTU (Native Type Universe) and dimensional type system to carry type widths and precision requirements all the way to final lowering. A transcribed accumulation kernel does not commit to IEEE float64 at library compile time. It carries a precision requirement (e.g., "lossless accumulation of N multiply-add operations on values with dimension `Pressure`") that Composer resolves against the application's platform capabilities:

- If the application declares an FPGA sidecar with b-posit quire: route to FPGA synthesis
- If the application targets a CPU with b-posit register support (per the 2026 draft specification, standard CPU registers can achieve parity with or exceed IEEE 754 acceleration on most SoCs): route to b-posit CPU instructions
- If the application targets a conventional CPU without posit support: fall back to IEEE float64 with compiler-inserted Kahan summation or similar compensated accumulation
- If the application targets an MCU: select the appropriate fixed-point or reduced-precision path

The library author does not make this decision. The application author does not manually wire it. The type system carries the requirement; the platform declaration provides the capability; Composer resolves the match.

### 4.2 Platform Binding as Dependency Graph

Platform-specific code for GPU, NPU, MCU, and FPGA targets enters the compilation as binding dependencies that flow through the application's dependency graph. These bindings are not compiled into the transcribed library; they are resolved when the library is composed into an application with a concrete platform declaration.

```
Transcribed Library (target-agnostic Clef)
    │
    ├── Declares: capability requirements
    │       "parallel-map over elements"
    │       "lossless accumulation"
    │       "memory region: shared"
    │
    └── Consumed by Application
            │
            ├── .fidproj platform declaration
            │       targets: [cpu, gpu:rdna3.5, npu:xdna2, fpga:arty-a7]
            │
            ├── Composer resolves requirements → platform bindings
            │       parallel-map → GPU kernel (RDNA 3.5 dispatch)
            │       lossless accumulation → FPGA bitstream OR b-posit CPU
            │       shared memory → BAREWire region descriptor
            │
            └── Alex middle-end lowers per resolved binding
                    → MLIR dialects per target
                    → Native binary + FPGA bitstream + GPU kernel
```

This is why transcribed libraries are fundamentally different from conventionally-bound foreign libraries. A traditional FFI binding links to a pre-compiled C/C++ artifact with a fixed target. A transcribed library exists as typed computation that acquires its target realization from the application context. Even Transpose bindings (see Section 8.2), which preserve the foreign library's compiled artifact, gain NTU type awareness, BAREWire memory contracts, and escape analysis that a raw FFI binding cannot provide. The platform bindings for each target (GPU dispatch code, NPU dataflow configuration, MCU peripheral setup, FPGA synthesis constraints) are bootstrapped into the processor as part of the application build, not pre-compiled into the library.

### 4.3 BAREWire as the Shared Library Memory Contract

When a transcribed library is consumed by an application that targets multiple processors, the computational kernels execute on different devices. An element integration kernel runs on GPU; its accumulation reduction runs on FPGA (or b-posit CPU); the orchestration runs on the primary CPU. Data must flow between these execution contexts.

BAREWire is the memory layout and over-the-wire contract that governs this flow. It defines:

- **Memory region descriptors**: Where a value lives (CPU DRAM, GPU VRAM, FPGA block RAM, MCU SRAM, shared memory mapped region). The descriptor is part of the value's type in the NTU.
- **Layout contracts**: How a value is arranged in memory for each region (row-major vs. column-major, padding and alignment for SIMT access, bit-serial layout for FPGA streaming, packed layout for MCU constraints). The layout is determined by the target platform binding, not by the library.
- **Transfer protocols**: How a value moves between regions (DMA, PCIe, USB-C for FPGA sidecar, network socket for distributed execution). The protocol selection follows from the region descriptors of source and destination.
- **Type preservation across boundaries**: The dimensional type, precision annotation, and tensor rank of a value are encoded in the BAREWire schema header. A `Tensor<Stress<Pa>, Symmetric, rank=2>` in GPU VRAM is still a `Tensor<Stress<Pa>, Symmetric, rank=2>` when it arrives in FPGA block RAM. The type is invariant; the layout transforms.

For transcribed multi-target libraries, BAREWire's role is to maintain the typed memory contract at every boundary where computation crosses from one processor to another. The library's Clef source expresses the algorithm; the platform bindings determine where each part executes; BAREWire defines how data flows between the parts.

### 4.4 Decomposing the Conditional: From Hand-Curation to Automation

The multi-pre-process method (Section 2) extracts the invariant algorithm and the platform-variant code paths from C/C++ source. But the Fidelity targeting model (Sections 4.1-4.3) requires a further decomposition that the C/C++ source does not directly express.

C/C++ conditional compilation blocks are monolithic: the entire CUDA code path is one `#ifdef` block. Fidelity's declaration-dependent model requires this to be decomposed into finer-grained elements:

- **The capability requirement**: "This loop nest benefits from SIMT parallelism" (extracted from the presence of a CUDA kernel version)
- **The dispatch binding**: How to launch work on a specific GPU architecture (RDNA, Ampere, Xe, Mali)
- **The memory layout adaptation**: How the element matrix is arranged for coalesced GPU memory access
- **The reduction strategy**: Whether accumulation uses warp shuffle, shared memory atomic, or is offloaded to a separate device (FPGA quire)
- **The data transfer protocol**: How results flow from GPU to the next computation stage

In C++, these are tangled inside the `#ifdef MFEM_USE_CUDA` block. Transcribe must separate them. The capability requirement becomes a type constraint on the Clef function. The dispatch binding becomes a platform binding resolved at application composition. The memory layout becomes a BAREWire region descriptor. The reduction strategy becomes a separate computation with its own capability requirement. The data transfer becomes a BAREWire protocol selection.

**This decomposition will initially require hand-curation.** The first MFEM transcription will produce the invariant algorithm automatically (high confidence) and flag the platform-variant blocks for manual decomposition into capability requirements, dispatch bindings, layout adaptations, and transfer protocols. The developer examines each flagged block, identifies which parts are algorithmic invariants expressed in platform-specific syntax (and should be re-expressed as capability requirements) and which parts are genuinely platform-specific (and should become platform bindings).

**Automation emerges from accumulated examples.** As Transcribe processes more libraries and the hand-curated decompositions accumulate, patterns will emerge:

- CUDA kernel launches with `<<<gridDim, blockDim>>>` consistently decompose into a parallel-map capability requirement plus a GPU dispatch binding
- `cudaMemcpy` calls consistently decompose into BAREWire region transfers
- Shared memory usage (`__shared__`) consistently maps to a "local scratchpad" memory region descriptor
- Warp-level primitives (`__shfl_down_sync`) consistently indicate a reduction capability requirement

Each curated example teaches Transcribe a decomposition rule. After sufficient coverage (likely 20-30 hand-curated CUDA kernels, a similar count for HIP, OpenMP, and MPI patterns), the rules generalize. Transcribe begins proposing decompositions for new platform-variant blocks, initially at medium confidence (requiring review), eventually at high confidence for well-matched patterns.

This maturation path, hand-curation first, pattern recognition second, automated decomposition third, is consistent with how the broader Transcribe capability develops. The algorithmic comprehension (Section 3.2) follows the same trajectory: early transcriptions require more manual intervention; later transcriptions benefit from accumulated pattern knowledge. The platform decomposition problem is simply the most target-specific instance of this general learning curve.

### 4.5 The B-Posit Flexibility

The 2026 draft specification for b-posit arithmetic changes the targeting calculus. Earlier Fidelity documents positioned FPGA as the primary (or only) path for posit-accelerated computation, requiring a sidecar device and USB-C or PCIe interconnect. The 2026 draft demonstrates that standard CPU registers can equal or surpass IEEE 754 acceleration on most SoCs for b-posit operations.

This means the platform resolution for a "lossless accumulation" capability requirement is no longer binary (FPGA or fallback to IEEE). The resolution space includes:

| Platform Capability | Accumulation Strategy | Trade-off |
|---|---|---|
| FPGA with quire | Hardware quire, exact accumulation | Highest precision; requires sidecar hardware; transfer latency |
| CPU with b-posit registers | Register-width posit, software quire emulation or bounded accumulation | Near-FPGA precision; no sidecar; in-pipeline | 
| GPU with b-posit software | Posit arithmetic in shader/compute units | Parallel but reduced quire width; precision vs. throughput |
| Conventional CPU (IEEE 754) | Compensated summation (Kahan, pairwise) | Lowest precision; widest deployment; no special hardware |
| MCU (fixed-point) | Fixed-point accumulation with overflow detection | Constrained environments; deterministic timing |

Transcribe's output does not select from this table. It expresses the capability requirement ("lossless accumulation of N multiply-add operations"). The application's platform declaration determines which row applies. The NTU carries the precision semantics through lowering, and Alex selects the appropriate MLIR dialect and instruction sequence.

The practical consequence: transcribed libraries gain wider deployment reach without sacrificing precision guarantees where hardware supports them. A Fidelity.MFEM application running on a laptop with no FPGA sidecar still benefits from b-posit CPU arithmetic. The same application running on the ThreeBody hardware (Strix Halo + Arty A7) benefits from FPGA quire for the most precision-critical kernels. The library source is identical in both cases.

---

## 5. Beyond C and C++

### 5.1 Rust Ingestion

Rust is the most natural secondary target for Transcribe. Its ownership model, trait system, and algebraic data types map more directly to Clef than C/C++ constructs do:

| Rust Construct | Clef Equivalent | Transcription Complexity |
|---|---|---|
| Ownership / borrowing | Clef lifetime inference | Low (structurally similar) |
| Trait bounds | SRTP constraints | Medium (trait objects need DU mapping) |
| `enum` (algebraic types) | Discriminated unions | Low (direct mapping) |
| `Result<T, E>` | `Result<'T, 'E>` | Trivial |
| `Option<T>` | `Option<'T>` | Trivial |
| `async`/`await` | Delimited continuations | Medium (runtime model differs) |
| `unsafe` blocks | BAREWire raw access | Medium (requires manual review) |
| Macro rules | Clef metaprogramming | High (Rust macros are Turing-complete) |
| Procedural macros | Not directly portable | Flagged for manual port |

Rust's advantage as a transcription source is that its type system already encodes much of the safety information that Transcribe must infer from C/C++. Ownership is explicit; mutability is marked; error handling uses `Result`. The dimensional type layer is still absent (Rust has no units of measure), but the structural mapping is close enough that confidence coverage will be significantly higher than for C/C++ of equivalent complexity.

Rust's platform-targeting story (feature flags, `cfg` attributes, target-specific modules) is more structured than C/C++ preprocessor conditionals. The decomposition problem from Section 4.4 is less severe: Rust code already separates platform-specific implementations into distinct modules gated by `cfg(target_arch)` or `cfg(feature)` attributes. Transcribe can map these directly to Clef's platform capability model with less hand-curation than the C/C++ case requires.

### 5.2 Python Ingestion

Python presents a different challenge. It is dynamically typed, garbage collected, and operates at a level of abstraction that obscures the underlying computation. The target for Python transcription is not general Python code; it is the numerical/scientific Python ecosystem (NumPy, SciPy, PyTorch, JAX) where the actual computation is:

- Array operations that map to typed tensor operations in Clef
- Linear algebra calls that map to BLAS/LAPACK equivalents (which Transcribe can also ingest from C)
- Neural network layer definitions that map to Clef's dataflow graph representation
- Data pipeline descriptions that map to actor-model stream processing

Python transcription requires type inference (via mypy annotations, runtime profiling, or NumPy dtype analysis) to recover the type information that the language does not require. This is inherently lower confidence than Rust or C++ transcription, but the computational patterns in numerical Python are sufficiently constrained that idiom recognition can achieve useful coverage.

### 5.3 Go Ingestion

Go's concurrency model (goroutines and channels) maps to Clef's actor model with channel-based message passing. Go's interface system maps to Clef's SRTP constraints (structural typing). Go's lack of generics (prior to Go 1.18) and its use of `interface{}` for polymorphism create type recovery challenges similar to Python's dynamic typing.

The primary value of Go transcription is infrastructure code: network servers, distributed systems, container orchestration tools. These are not physics or numerical applications; they are the systems code that Clef's actor model and BAREWire networking address from a different direction.

### 5.4 Language-Specific Analysis Frontends

Each source language requires a dedicated analysis frontend:

```
                    ┌─── C/C++ Frontend (libclang + multi-pre-process)
                    │
                    ├─── Rust Frontend (rust-analyzer / syn crate)
                    │
Foreign Source ─────┼─── Python Frontend (mypy + AST module)
                    │
                    ├─── Go Frontend (go/ast + go/types)
                    │
                    └─── [future language frontends]
                    │
                    ▼
            Unified Algorithmic IR
                    │
                    ▼
            Dimensional Inference
                    │
                    ▼
            Clef Source Generation
                    │
                    ├── Target-agnostic algorithm (invariant)
                    │
                    ├── Capability requirements (type constraints)
                    │
                    └── Platform-variant blocks (flagged for decomposition)
                    │
                    ▼
            Port Review Interface
                    │
                    ├── High confidence → auto-accept
                    ├── Medium confidence → review
                    ├── Platform decomposition → hand-curate (early) / auto-propose (mature)
                    └── Untranscribable → Transpose binding (typed, with NTU + BAREWire)
```

The frontends are language-specific; everything downstream of the Unified Algorithmic IR is shared. This is the same architectural principle as Composer's use of MLIR: many frontends, one middle-end, many backends. Transcribe adds "many foreign frontends" to the Composer pipeline. The platform decomposition review step (Section 4.4) is integrated into the port review interface, with its own maturation trajectory from hand-curation to automation.

---

## 6. Composer Integration

### 6.1 The Transcribe Command

In its integrated form, Transcribe is a Composer subcommand:

```bash
# Transcribe a single C++ file
composer transcribe --source mfem/fem/bilininteg.cpp \
                    --includes mfem/include \
                    --defines MFEM_USE_CUDA,MFEM_USE_MPI \
                    --output src/Fidelity.MFEM/Integrators.clef

# Transcribe an entire library
composer transcribe --project mfem/ \
                    --config transcribe.toml \
                    --output src/Fidelity.MFEM/

# Transcribe with dimensional annotations
composer transcribe --source solver.cpp \
                    --physics-hints hints.toml \
                    --output src/Solver.clef
```

The `transcribe.toml` configuration file specifies:

- Which source files to process
- Preprocessor configurations to enumerate
- Known dimensional annotations for key types and constants
- Confidence thresholds for automatic acceptance vs. review flagging
- Accumulated platform decomposition rules (hand-curated patterns from previous transcriptions)
- BAREWire schema templates for cross-device data transfer patterns

### 6.2 Incremental Transcription

Libraries evolve. MFEM releases new versions with new element types, solver improvements, and bug fixes. Transcribe supports incremental operation:

- **Diff-based re-transcription**: Given a new version of the foreign source and the previous transcription, identify changed functions and re-transcribe only the delta.
- **Annotation preservation**: Developer-added dimensional annotations, confidence overrides, platform decomposition decisions, and manual corrections persist across re-transcription. Changes to the foreign source that do not affect the algorithmic structure preserve the existing Clef annotations.
- **Conflict detection**: When a foreign source change alters the algorithm in a way that conflicts with existing Clef annotations (e.g., a new parameter whose dimension cannot be inferred, or a new platform code path that doesn't match existing decomposition rules), Transcribe flags the conflict for manual resolution.
- **Decomposition rule refinement**: Re-transcription of an updated library with new platform-variant code tests existing decomposition rules against new examples. Rules that produce correct decompositions for the new code are strengthened; rules that fail are flagged for review and potential revision.

### 6.3 Bidirectional Verification

Transcribed code must produce the same results as the original for the same inputs. Composer provides verification infrastructure:

- **Test transcription**: If the foreign source includes unit tests, Transcribe ports the tests alongside the implementation. The ported tests run against the Clef implementation; numerical agreement within specified tolerance confirms the transcription's correctness.
- **Reference oracle**: For libraries without comprehensive tests, Transcribe generates harness code that calls both the original C/C++ library (via Transpose binding) and the transcribed Clef implementation on the same inputs, comparing results.
- **Precision divergence tracking**: When the Clef implementation uses posit arithmetic (on FPGA, b-posit CPU, or GPU) where the original used IEEE 754, results will differ. Transcribe's verification distinguishes *precision improvement* (the Clef result is more accurate, verifiable against high-precision reference) from *transcription error* (the algorithm was incorrectly translated).
- **Cross-target consistency**: The same transcribed library compiled for different platform declarations (FPGA+GPU vs. CPU-only vs. MCU) must produce results consistent with each platform's precision characteristics. Divergences beyond expected precision differences indicate a platform binding or BAREWire contract error.

---

## 7. The MFEM Milestone

MFEM is the first large-scale validation target for Transcribe. Success criteria:

| Metric | Target |
|---|---|
| Element integration kernels (high-confidence transcription) | > 80% |
| Solver kernels (high-confidence) | > 70% |
| Mesh infrastructure (binding, not transcription) | 100% via Transpose |
| Dimensional annotations resolved automatically | > 60% |
| Test parity (numerical agreement within IEEE tolerance) | 100% of ported tests |
| Precision improvement demonstrated (posit vs. IEEE) | At least one ill-conditioned benchmark |
| Platform decomposition rules extracted | > 15 reusable CUDA decomposition patterns |
| BAREWire contracts validated (GPU↔CPU↔FPGA) | Full data flow for element integration pipeline |

Achieving these metrics validates Transcribe's capability against a production-grade, template-heavy C++ codebase. It also produces a usable Fidelity.MFEM library as a concrete deliverable, validates the declaration-dependent targeting model against real multi-platform computation, and seeds the decomposition rule database for subsequent library transcriptions.

---

## 8. Implications

### 8.1 The C/C++ Ecosystem Becomes Input

If Transcribe can ingest MFEM, it can ingest BLAS, LAPACK, PETSc, SuiteSparse, Eigen, OpenBLAS, FFTW, and the broader numerical C/C++ ecosystem. Each transcription adds to Clef's library ecosystem while preserving the optimization work of the original authors. Each transcription also contributes platform decomposition rules that improve the next transcription's automation level.

The strategic position is significant: Fidelity does not need to build numerical libraries from scratch, nor does it need to accept the limitations of FFI wrappers. It ingests the best available implementations, adds dimensional safety and precision-aware targeting, and produces Clef libraries that are simultaneously more correct (type-checked) and more capable (heterogeneous lowering with declaration-dependent platform resolution) than the originals.

### 8.2 From Farscape to Transpose

Farscape was the right tool for its moment. It emerged from the practical need to generate F# bindings from C/C++ headers, and it deliberately avoided the type provider machinery of F# because it was unclear whether an equivalent mechanism would emerge in Clef. Farscape drew from other binding generator libraries (CppSharp, SWIG) that treat foreign library integration as a build-time code generation step: parse headers, emit glue code, link.

That design served its purpose. It proved that Clef programs could access native C/C++ libraries with type safety. It established XParsec as the parsing infrastructure. It validated the Plugify integration path for C++ ABI intelligence. But Farscape as a standalone tool represents an intermediate stage, not a destination.

The destination is two complementary capabilities within Composer, both accessible through the Atelier editor:

**Transcribe** performs full algorithmic port. It reads foreign source, comprehends the computation, and re-expresses it in Clef with dimensional types, deterministic memory, and declaration-dependent platform targeting. The foreign code is absorbed; the algorithm lives in Clef from that point forward.

**Transpose** performs typed dynamic binding. It reads foreign library interfaces (headers, module signatures, type definitions) and generates Clef binding types that carry the full Fidelity machinery: NTU type widths, BAREWire memory layout contracts, escape analysis across the FFI boundary, and memory lifetime inference for foreign-allocated resources. The foreign library remains a compiled artifact; the Clef binding wraps it with safety guarantees that a raw FFI cannot provide.

The musical analogy extends naturally. Transcription rewrites the piece in a new notation. Transposition preserves the piece but shifts its tonal center. Transpose preserves the foreign library's implementation but shifts its type foundation into the NTU, where dimensional annotations, precision requirements, memory region descriptors, and lifetime constraints become first-class properties of the binding interface.

**Why "type provider" undersells it.** F#'s type providers generate types from external schemas at compile time, a powerful mechanism for database access, web APIs, and configuration files. Transpose does this and more. A type provider generates type signatures. Transpose generates:

- **Type signatures with NTU widths**: The foreign `double*` is not just `nativeptr<float>`. It is a typed array with dimensional annotation, numeric precision metadata, and memory region classification.
- **BAREWire memory layout contracts**: When a Transpose-bound function returns a pointer to foreign-allocated memory, the binding includes a layout descriptor (alignment, stride, endianness) that enables zero-copy access from Clef code and safe transfer across device boundaries.
- **Escape analysis across the FFI boundary**: Transpose tracks which foreign-allocated values escape the binding call, which are consumed, and which are borrowed. This informs Clef's lifetime inference for resources that straddle the native/foreign boundary.
- **Platform-aware binding variants**: A Transpose binding to a library that provides both CPU and GPU implementations (as MFEM does) generates platform-parameterized bindings that resolve against the application's platform declaration. The binding is not locked to one target.

Transpose subsumes everything Farscape does today, and extends it with the machinery that the Fidelity compilation pipeline makes possible. Farscape's XParsec parser, its C/C++ header analysis, its Plugify ABI integration: all of this folds into the Transpose capability within Composer. Farscape retires as a standalone tool; its work continues as the foundation of Transpose.

The per-component decision for MFEM generalizes cleanly under this model: Transcribe the computation (element integrators, solver kernels), Transpose the infrastructure (mesh I/O, file format parsers, visualization hooks). Both operate within the same Atelier editing environment, share the same multi-pre-process analysis pipeline, and produce output that participates fully in Composer's declaration-dependent platform resolution.

Over time, the boundary between Transcribe and Transpose may blur. A Transpose binding that wraps a foreign function today may be promoted to a Transcribe port tomorrow, as confidence in the algorithmic comprehension improves or as the application's precision requirements demand posit arithmetic that the foreign library cannot provide. The Atelier editor can present this as a continuum: "this function is currently Transposed (bound); click to attempt Transcription (port)." The tooling supports the developer's judgment about where on the continuum each component belongs.

### 8.3 Language Migration at Industrial Scale

The long-term vision positions Clef as a migration target for safety-critical systems currently written in C/C++. Aerospace, automotive, medical device, energy infrastructure, and defense codebases contain millions of lines of C/C++ that must eventually be modernized for safety, security, and maintainability. The Transcribe/Transpose continuum offers a migration path that does not require a single all-or-nothing rewrite. Components can enter the Fidelity ecosystem as Transpose bindings (immediate access, typed safety at the boundary) and graduate to Transcribe ports (full algorithmic integration) as confidence and requirements justify the deeper commitment.

The declaration-dependent targeting model is central to this migration story. Legacy C/C++ systems are locked to the platforms they were compiled for. Transcribed Clef libraries acquire new platform capabilities (b-posit arithmetic, FPGA acceleration, NPU inference offload, MCU deployment) without source modification, simply by changing the application's platform declaration. Even Transpose-bound components gain platform awareness through their BAREWire memory contracts and NTU type annotations. This is not recompilation for a new target; it is the same typed computation resolving against a different capability set, with BAREWire maintaining the memory contract across whatever device boundaries the new platform introduces.

This is not a near-term market. It is the horizon that the MFEM case study, the ThreeBody demonstration, and the Southern Company engagement collectively point toward: a framework where the world's existing computational infrastructure becomes the raw material for a new generation of safer, more precise, heterogeneously-targeted systems.

---

*This document is speculative and represents a long-term architectural vision. Implementation timelines and specific capabilities are subject to revision as Composer matures.*

*SpeakEZ Technologies | Fidelity Framework*
*License: MIT*
