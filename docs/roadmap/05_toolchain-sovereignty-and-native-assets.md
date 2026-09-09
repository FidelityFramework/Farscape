# Farscape Horizon: Toolchain Sovereignty and Native Asset Production

## Clang confined to Farscape; Composer links through LLD alone

**SpeakEZ Technologies | Fidelity Framework**
**September 2026** (corrects and supersedes the September 2026 technical memorandum draft)

---

## 1. Summary

Native interop has historically forced one of two costs on the application developer: a heavy runtime with a dynamic bridge, or a host C/C++ toolchain that must be installed and understood. Fidelity takes neither. The compiler and the binding tool split the work by time:

- **Composer** compiles Clef through MLIR to LLVM IR, emits objects through LLVM's own code generator, and links through LLD. No C compiler participates in the application build.
- **Farscape** runs offline, at package-authoring time. It parses C and C++ headers, flattens template specializations into `extern "C"` entry points, captures memory layout, compiles the resulting wrappers into per-target static archives, and packages the archives with the generated Clef bindings. Clang is used here, and only here.

This document sets out that division as a sequence of horizons along one trajectory. The standalone Farscape CLI, the native self-hosted Farscape, and the Transpose and Transcribe capabilities inside Composer and Atelier are the same core at different hosting stages, not competing fates. Static inclusion of libclang, dynamic hosting of the clang executable, and tree-sitter frontends for non-C syntaxes are one body of work reached at different points of temporal and platform maturity.

```
┌────────────────────────────────────────────────────────────────────────┐
│               OFFLINE EXTRACTION AND ASSET PRODUCTION                   │
│                     (package author / CI)                              │
│                                                                        │
│   C/C++ headers (.h / .hpp)   +   specialization manifest              │
│         │                                                              │
│         ▼                                                              │
│   ┌────────────────────────────────────────────────────────────────┐   │
│   │ Farscape                                                       │   │
│   │   - AST extraction (clang, four passes; libclang at Horizon 3) │   │
│   │   - Template flattening: synthesized TU + extern "C" wrappers  │   │
│   │   - Layout capture: measured offsets → layout module +         │   │
│   │     StructDescriptor                                           │   │
│   │   - Archive compilation per target tuple (clang executable)    │   │
│   │   - Closure computation: builtins, C++ runtime, init_array     │   │
│   └───────────────────────────────┬────────────────────────────────┘   │
│                                   │ Emits: .clef bindings              │
│                                   │        + native/<tuple>/*.a        │
│                                   │        + native manifest           │
└───────────────────────────────────┼────────────────────────────────────┘
                                    │ Packaged as a fidproj dependency
┌───────────────────────────────────▼────────────────────────────────────┐
│                  APPLICATION BUILD (Composer)                          │
│                                                                        │
│   Clef source  →  CCS/PSG  →  Alex  →  MLIR  →  LLVM IR               │
│         →  object (llc / LLVM-C in-process)                            │
│         →  ld.lld  ( app.o + native/<tuple>/*.a + closure )            │
│         →  ELF / Mach-O / PE-COFF                                      │
│                                                                        │
│   No clang. No C compiler. The archives are data the dependency        │
│   walk collects.                                                       │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. What Is True Today (verified 2026-09-08)

The horizon plan below is measured against these facts. Each is load-bearing for a correction in §3.

**Farscape does not link libclang.** It shells out to the `clang` executable four times per header and parses stdout: `-ast-dump=json` for declarations, `-E -dM` for macros, `-H -E` for the include tree, and `-fdump-record-layouts-simple` for measured struct offsets when `[options] abi_critical_structs` is non-empty (`docs/14_Binding_Generation_Gaps.md` §5). This is the dynamic hosting stage of the trajectory, already in place.

**Composer's LLVM backend runs four subprocesses.** `mlir-opt`, `mlir-translate`, `llc -filetype=obj`, then `clang` as a link driver (`~/repos/Composer/src/BackEnd/LLVM/Codegen.fs`). No C is compiled anywhere. For `Freestanding` and `Embedded` output kinds the clang invocation carries `-nostdlib -static -ffreestanding -Wl,-e,_start`, so clang contributes nothing but a process hop over `ld.lld`. For `Console` it silently supplies the C runtime start files, the dynamic linker path, `-lc`, and the compiler builtins archive.

**A static archive is already linked by hand.** `HelloWayland/lib/libwayland-protocols-data.a` is checked in with no documented provenance, and the fidproj declares `[link] libraries = [...]`, a section the fidproj loader does not read. Linker library names reach Composer from `[<FidelityExtern>]` attributes on the generated bindings, not from the fidproj. The archive obligation this document introduces must be a key the loader actually reads.

**Fidelity libraries are source packages.** `docs/09_Library_Verification.md` states that the ecosystem does not use binary assemblies and that library compilation is verification, not artifact production. That remains true for Clef code. This horizon adds one class of binary content, foreign compiled code as per-tuple archives, and keeps Clef bindings as source that the dependency walk re-checks on every build.

**Struct layout is emitted as a layout module plus a `StructDescriptor`.** As of 2026-09-05 a C struct is never a Clef record. `DescriptorGenerator.fs` emits a module of literal `<field>Offset` bindings and a `Descriptor : StructDescriptor` value, each width fact naming its stratum (measured, declared, inferred). The template-flattening path in §4 produces measured offsets for every instantiated specialization through the same pass.

**C++ ABI analysis has begun in-house.** `CppClassAnalysis.fs` detects the pimpl pattern and classifies triviality to select `sret` versus register return. This is the first step of the C++ path that `04_farscape-phase5-mfem-ingestion.md` §Phase 5B assigns to a Plugify-backed ABI engine; both feed the same `Declaration` types.

---

## 3. Corrections to the September Draft

The draft's central claim holds: Composer to ELF, Mach-O, and PE/COFF through LLD needs no C compiler, LLD consumes objects and `ar` archives with no C dependency, and Farscape can produce those archives. Four claims in the draft were wrong or under-specified and are corrected throughout this document.

1. **libclang does not generate code.** The draft wrote "Clang CodeGen + llvm-ar" and "Farscape queries libclang" as one library. libclang is the AST-only C API. Emitting an object requires the clang executable or the C++ `CompilerInstance` API, which has no stable C surface. The self-hosted Farscape of Horizon 3 therefore links libclang statically for extraction and layout, and hosts the clang executable dynamically for archive compilation. Both roles are offline. The Phase 3 fixed-point proof covers the extraction path; the archive path is validated separately (§9).

2. **LTO needs bitcode, and bitcode pins a version order.** A plain archive of native objects links statically and gets no cross-module optimization. For LTO the members must carry LLVM bitcode, and bitcode is forward-compatible only, so Farscape's clang must be at or below Composer's LLVM. Fat LTO objects (`-ffat-lto-objects`, clang 17 and later on ELF) carry both native and bitcode sections in one archive; LLD selects whichever the link mode asks for. Archives are produced fat.

3. **One archive does not close the symbol graph.** An `extern "C"` wrapper around a C++ template still pulls whatever the template body uses: `libm` for anything in `<cmath>`, `libc++` and `libc++abi` for any `std::` container, `libunwind` if exceptions are enabled, and the compiler builtins archive for whatever the target ISA lacks. On freestanding targets, C++ static initializers land in `.init_array` and the startup code must walk it. The binding contract carries a per-tuple closure and an `init_array` obligation, not a single archive name.

4. **Apple targets stop at the link.** LLD reads Apple `.tbd` stubs, so Mach-O links from Linux and Windows runners. An unsigned Mach-O will not launch on iOS, and arm64 macOS needs at least an ad-hoc signature, which LLD applies by default. iOS deployment needs a separate signing step with a cross-platform signer.

One further point the draft left unstated: LLD has no C API. Embedding it in-process means a small C shim over `lld::lldMain`, compiled once at Composer build time. That is build machinery for the compiler, not for the user, and does not contradict the sovereignty claim. Repeated in-process invocation has been safe since LLVM 16. Subprocess `ld.lld` is the equivalent first step.

---

## 4. Farscape as Native Asset Producer

The binding pipeline gains four stages after extraction. Each composes from standing art in the corpus.

### 4.1 AST and type inspection (existing)

Four clang passes as in §2. Declarations, macros, include attribution, and measured layouts feed the existing `Declaration` catamorphism. Nothing changes here at Horizon 2.

### 4.2 Template flattening

C++ templates have no ABI until instantiated. libclang and the AST dump report layout only for a specialization that exists in a translation unit, never for a dependent type. Flattening is therefore the only route, not one option among several:

1. A **specialization manifest** in the pilot file names each concrete instantiation to expose, for example `tmpl::Matrix<float, 4, 4>`.
2. Farscape **synthesizes a translation unit** that explicitly instantiates each named specialization and declares an `extern "C"` wrapper per exposed method, with C-representable parameter and return types. Methods whose signatures cannot be made C-representable are reported, not silently dropped (the discipline of `docs/14` §4).
3. The synthesized TU is **parsed by the same four passes**. The record-layout pass now sees the instantiated types, so every exposed specialization gets measured offsets and a `StructDescriptor` through `DescriptorGenerator` unchanged. `CppClassAnalysis` classifies each wrapper's return convention.

TMPL, the astrodynamics component library, is the "Mt. Everest" target for this stage: the largest template inventory in the portfolio, and the high-water mark against which the flattening pass is judged complete. MFEM (`04_farscape-phase5-mfem-ingestion.md`) is the Kilimanjaro of the same range, the proving ground climbed on the way up.

### 4.3 Archive compilation

For each target tuple the package declares, Farscape invokes the hosted clang executable on the synthesized TU with `-ffat-lto-objects` and the tuple's triple and ABI flags, then packs the objects with `llvm-ar` (or `llvm::writeArchive` once Farscape is native). Output lands under `native/<tuple>/`. The digest of the input headers and manifest is recorded so a stale archive is detectable.

### 4.4 Closure computation

Farscape resolves the wrapper archive's undefined symbols against the tuple's runtime archives and records the transitive set. The closure differs per tuple: hosted Linux resolves `libm` and `libc++` from the system; a Cortex-M or ESP32-P4 tuple must ship `libclang_rt.builtins-<arch>.a` and, if the wrappers use any `std::` type, `libc++.a`, `libc++abi.a`, and `libunwind.a` for that triple. A wrapper compiled with `-fno-exceptions -fno-rtti` and no `std::` use has a closure of builtins alone, which is the recommended shape for embedded tuples and a manifest option. Whether the archive contributes `.init_array` entries is detected and recorded.

### 4.5 Packaging

The package is a fidproj dependency like any other. Clef bindings are source, re-checked by CCS on every build. The archives and manifest are binary content resolved by tuple at link time.

```
Bindings.TMPL/
  Bindings.TMPL.fidproj
  src/
    Types.clef              -- layout modules + StructDescriptor per exposed type
    Matrix.clef             -- Layer 1 [<FidelityExtern>] + Layer 2 wrappers
  native/
    manifest.toml           -- per-tuple archives, closure, digest, init_array
    x86_64-unknown-linux-gnu/libtmpl_clef.a
    thumbv8m.main-none-eabi/libtmpl_clef.a
    riscv32-esp-elf/libtmpl_clef.a
```

---

## 5. The Binding Contract

The draft placed the linker obligation in a `[<PlatformBinding>]` attribute naming one archive. The corrected contract has three parts, each in the place the existing pipeline already reads.

### 5.1 Types: layout module plus descriptor (existing shape)

```fsharp
/// C struct `tmpl_matrix4x4f`: layout measured.
module tmpl_matrix4x4f =
    /// Offset of `data` in bytes (measured).
    let dataOffset = 0
    let Descriptor : StructDescriptor =
        { Name = "tmpl_matrix4x4f"; Size = 64; Alignment = 16
          Fields = [ (* data F32 x16 @0, with its provenance note *) ] }
```

This is what `DescriptorGenerator` emits today for a C struct. The flattened specialization is a C struct from the generator's point of view.

### 5.2 Functions: Layer 1 extern (existing shape)

```fsharp
/// C signature: void tmpl_matrix4x4f_mul(const tmpl_matrix4x4f *a, const tmpl_matrix4x4f *b, tmpl_matrix4x4f *out)
[<FidelityExtern("tmpl_clef", "tmpl_matrix4x4f_mul")>]
let tmpl_matrix4x4f_mul (a: nativeint) (b: nativeint) (out: nativeint) : unit =
    NativeDefault.zeroed ()
```

The library name `tmpl_clef` is how Composer's collection of extern library names already works. What changes is what that name resolves to at link time: a static archive from the package rather than a `-l` flag on the system search path.

### 5.3 Native assets: fidproj section (proposed; the loader reads none of these keys today)

```toml
# Bindings.TMPL.fidproj
[native]
lto = "fat"
digest = "sha256:7e8b2c..."

[native."x86_64-unknown-linux-gnu"]
archives = ["native/x86_64-unknown-linux-gnu/libtmpl_clef.a"]
closure  = ["m"]                      # resolved from the host sysroot
init_array = false

[native."thumbv8m.main-none-eabi"]
archives = ["native/thumbv8m.main-none-eabi/libtmpl_clef.a"]
closure  = ["native/thumbv8m.main-none-eabi/libclang_rt.builtins-armv8m.main.a"]
init_array = true
```

Composer's `SourceResolver` already walks dependency fidproj files recursively. Collecting `[native]` for the active tuple during that walk places the archives and closure on the LLD command line next to `app.o`. This replaces the unread `[link] libraries` key that `HelloWayland.fidproj` carries today, and gives `lib/libwayland-protocols-data.a` a declared home.

**Provenance.** Per `docs/14` §8, each claim in this section names its stratum: §5.1 and §5.2 are shipped shapes; §5.3 is a proposed schema awaiting a loader change in `~/repos/clef/src/Compiler/Project/FidprojLoader.fs` and a collection step in Composer's orchestrator.

---

## 6. LTO and Bitcode Rules

| Rule | Consequence |
|---|---|
| Archives are built fat (`-ffat-lto-objects`) | One archive serves both a plain static link and an LTO link |
| Bitcode is forward-compatible only | Farscape's clang major version ≤ Composer's LLVM major version; the manifest records the producing version and Composer refuses a newer one with a clear diagnostic |
| Triple and data layout must match | Archives are keyed by full tuple, never by architecture alone |
| LTO inlines across the boundary | A wrapper such as `tmpl_matrix4x4f_mul` can vanish into its Clef caller; validation checks for its absence from the final symbol table |
| GCC fat-LTO objects are not LLVM bitcode | System archives built with GCC LTO link natively but never participate in Composer's LTO |

---

## 7. Composer Prerequisites

This is the compiler's half of the horizon. Each item is measured against `Codegen.fs` as of 2026-09-08.

1. **Freestanding and Embedded go LLD-direct first.** The current flags already exclude every runtime object. Replacing the clang hop with `ld.lld -e _start -static` plus the tuple's linker script is the natural move for the ESP32-P4 and Cortex-M legs, which need the linker script regardless.
2. **Console needs its runtime plumbing as binding data.** The start files (`crt1.o`, `crti.o`, `crtn.o`, `crtbegin.o`, `crtend.o`), the dynamic linker path, the library search paths, and the builtins archive currently arrive implicitly from the clang driver. They move into the platform binding for the hosted tuple. Nothing about the `-rdynamic` and `dlsym` callback model changes.
3. **A libcall policy per tuple.** LLVM code generation may emit calls to symbols it does not define: `memcpy` and `memset` for non-constant sizes, `__aeabi_*` on Cortex-M, `__adddf3` and friends on ESP32-P4 (single-precision FPU only), `__atomic_*` where the core lacks them. The freestanding samples pass today because nothing in them triggers a libcall. The tuple either ships the builtins archive in its closure or the platform binding provides those symbols from Clef, treating "ISA lacks this operation" as a coeffect the front end observes. The second is on-thesis; the first is the immediate unblock.
4. **Object emission in-process is optional.** `llc` as a subprocess is sufficient. `LLVMTargetMachineEmitToFile` through the LLVM-C API removes the hop when convenient.
5. **LLD in-process is optional.** A C shim over `lld::lldMain` at Composer build time, or `ld.lld` as a subprocess. Either satisfies the sovereignty claim.

---

## 8. Multi-Target Linker Matrix

| Target | Container | LLD driver | Links against | Note |
|---|---|---|---|---|
| MCU freestanding | ELF `ET_EXEC` | `ld.lld` | wrapper archives + builtins + linker script | Startup walks `.init_array` when the manifest says so |
| Cloud unikernel | ELF | `ld.lld` | pure static kernel objects | No libc |
| Linux hosted | ELF | `ld.lld` | libc.so or musl.a, crt objects, wrapper archives | crt and search paths from the platform binding (§7.2) |
| Windows | PE/COFF | `lld-link` | CRT import libraries, wrapper archives | Import libs from the Windows SDK or MinGW-w64; static CRT licensing is MSVC-bound |
| macOS | Mach-O | `ld64.lld` | Apple `.tbd` stubs | arm64 ad-hoc signature applied by LLD by default |
| iOS | Mach-O | `ld64.lld` | Apple `.tbd` stubs | Link succeeds off-Apple; deployment needs a real signing step |
| Android | ELF `.so` | `ld.lld` | NDK libc stubs | JNI entry conventions as data |

---

## 9. Horizon Roadmap

```
┌────────────────────────────────────────────────────────────────────────┐
│ Horizon 1 (in place): .NET Farscape, clang hosted as a subprocess      │
│   • Four-pass extraction; .clef bindings; layout modules + descriptors │
│   • Composer links via clang-as-driver                                 │
├────────────────────────────────────────────────────────────────────────┤
│ Horizon 2: native asset production; Composer links via LLD alone       │
│   2.1 Freestanding/Embedded LLD-direct in Composer (§7.1)              │
│   2.2 [native] fidproj section read by the loader; collected by the    │
│       dependency walk (§5.3)                                           │
│   2.3 Farscape archive compilation + closure + manifest (§4.3–4.5)     │
│   2.4 Console LLD-direct with runtime plumbing as binding data (§7.2)  │
│   2.5 Template flattening against TMPL (§4.2); "Mt. Everest" gate      │
├────────────────────────────────────────────────────────────────────────┤
│ Horizon 3: self-hosted native Farscape                                 │
│   3.1 Dogfood: current Farscape on clang-c/Index.h → LibClang bindings │
│       (the first libclang dependency Farscape has ever taken)          │
│   3.2 Farscape.Core in Clef against static libclang.a; its own closure │
│       (clang C++ libs, LLVM, C++ runtime, zlib/zstd) is the first      │
│       real test of §4.4 on a large archive set                         │
│   3.3 Fixed point: native farscape on Index.h yields byte-identical    │
│       LibClang bindings (extraction path only; §3.1 explains why)      │
│   3.4 Archive path validated by a second oracle: native farscape and   │
│       .NET farscape produce archives with identical symbol tables and  │
│       identical measured layouts for TMPL                              │
├────────────────────────────────────────────────────────────────────────┤
│ Horizon 4: frontends beyond C, hosted inside Composer                  │
│   4.1 tree-sitter frontends for TypeScript (06), Rust/Python/Go (08)   │
│       feeding the same Declaration and, later, Transcribe IR           │
│   4.2 The Clef core of Farscape linked into Composer as Transpose      │
│       (typed binding) and Transcribe (algorithmic port), surfaced in   │
│       Atelier; the standalone CLI remains the CI/package-author entry  │
└────────────────────────────────────────────────────────────────────────┘
```

Horizon 4.2 is the "Farscape folds into Composer" statement of `06` §2.6, `07` §1, and `08` §8.2. It is reached by linking the Horizon 3 core, not by rewriting it, which is why the self-hosting work is prerequisite rather than parallel.

---

## 10. Validation Criteria

In the style of the per-phase criteria of `00_farscape-maturation-plan.md`.

**Horizon 2.1 / 2.4 (Composer)**
- A Freestanding sample links with `ld.lld` invoked directly and runs, with no `clang` on `PATH`
- A Console sample links the same way, its crt objects and search paths supplied by the platform binding
- A sample that forces a `memcpy` libcall on thumbv8m links only when the tuple's closure is present, and the failure message names the missing symbol and the closure key

**Horizon 2.2 / 2.3 (contract and archives)**
- `FidprojLoader` reads `[native]`; an unknown tuple produces a diagnostic, not silence
- `HelloWayland` declares `lib/libwayland-protocols-data.a` under `[native]` and the unread `[link]` section is removed
- A package's archive is rebuilt when its header digest changes and reused when it does not

**Horizon 2.5 (TMPL)**
- Every specialization in the manifest yields a layout module whose offsets are stratum `measured`
- A Clef program calls `tmpl_matrix4x4f_mul` through the Layer 2 wrapper and links with no C compiler present
- Under LTO the wrapper symbol is absent from the final binary; under plain static link it is present; both produce identical results on the TMPL regression set
- A method whose signature is not C-representable appears in the generation report, not as a silent omission

**Horizon 3**
- Byte-identical `LibClang` bindings from the .NET and native tools (3.3)
- Identical symbol tables and layouts for TMPL archives from both tools (3.4)
- The native `farscape` binary's own closure is declared in its manifest and links with `ld.lld` alone

---

## 11. Relationship to the Rest of the Roadmap

| Document | Relationship |
|---|---|
| `00_farscape-maturation-plan.md` §9 | The regeneration horizon and the `FnPtr<'F>` gate apply to every binding this document produces; Layer 3 criteria are inherited per package |
| `04_farscape-phase5-mfem-ingestion.md` §Phase 5B | The C++ class and template path; `CppClassAnalysis.fs` is its first shipped piece, and §4.2 here is the instantiation mechanism it needs |
| `06_typescript-openapi-ingestion-roadmap.md` | Horizon 4.1: the TypeScript frontend moves to tree-sitter under the same `Declaration` catamorphism |
| `07_type-provider-clef-fidelity.md`, `08_composer-transcribe-vision.md` | Horizon 4.2: Transpose and Transcribe are the Farscape core hosted inside Composer |
| `docs/09_Library_Verification.md` | Clef packages stay source-only; `[native]` is the one binary class and is scoped to foreign compiled code |
| `docs/14_Binding_Generation_Gaps.md` §5, §8 | The four-pass clang model this plan builds on, and the provenance discipline its claims follow |

---

*Companion documents: "Farscape Maturation Plan: Phases 0-3 Through HelloWayland", "Farscape Phase 5+: MFEM Algorithmic Ingestion", "Composer Transcribe"*

*SpeakEZ Technologies | Fidelity Framework*
