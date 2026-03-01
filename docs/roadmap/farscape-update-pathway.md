# Farscape: Current Document Body Sync

## Assessment for Agent-Assisted Update

**SpeakEZ Technologies | Fidelity Framework**
**February 2026**

---

## 1. Purpose

This document assesses the current Farscape documentation against the maturation plan (Phases 0-5+) and identifies what needs to change, when, and how. The goal is to prepare an agent to touch these files in a coordinated pass that brings the documentation current with the project's vision and trajectory.

---

## 2. Systemic Considerations

### 2.1 Moya → Pilot Rename

The Pilot rename is a new decision introduced in the maturation plan. The existing documentation correctly reflects the current state: the project system is called Moya, the files are `MoyaTypes.fs`, `MoyaAnalyzer.fs`, `MoyaSerializer.fs`, and project files use `.moya.toml`. These references are accurate today.

The doc updates for Moya → Pilot should happen **in the same pass** as the source file renames (Phase 0 implementation). Updating docs before the code ships creates inconsistency; updating docs after creates a window where the code and docs disagree. The agent should treat the rename as a single atomic operation across source and documentation.

### 2.2 Composer Pipeline References

Several docs reference the compilation pipeline as "FNCS → Baker → Alex → MLIR → LLVM." The current naming is: **Composer** (the compilation app), **CCS (Clef Compiler Service)** (type-checking), **Baker** (saturation, within CCS), and **Alex** (Composer's middle-end, MLIR emission). The docs over-specify the downstream pipeline in ways that couple Farscape's documentation to Composer's internal architecture. As Composer evolves, these references will drift. The fix is to refer to "Composer" as the compilation consumer where possible, keeping internal stage names only where the integration contract genuinely depends on them (e.g., the `[<FidelityExtern>]` attribute discussion, where the metadata flow through the PSG is the point).

### 2.3 Plugify and C++ ABI Intelligence

Referenced in `01_Architecture_Overview.md` roadmap as "C++ support via Plugify ABI intelligence."

[Plugify](https://github.com/untrustedmodders/plugify) is a modern C++ plugin manager with multi-language support (C++, C#/.NET, Go, Python, Rust, D, Lua, JavaScript). Its relevance to Farscape is not as a runtime plugin system; it is the ABI knowledge base. Plugify has accumulated battle-tested understanding of C++ ABI mechanics across platforms and compilers: virtual table layouts, name mangling schemes, calling conventions, struct packing rules, RTTI formats, exception handling interop, and the platform-specific variations between Clang, GCC, MSVC, and Apple Clang on x86 and ARM. Because Plugify solves the cross-language binary interface problem at runtime for its plugin modules, it has already mapped the ABI terrain that Farscape needs to navigate at codegen time.

The SpeakEZ Technologies blog post "Binding F# to C++ in Farscape" (September 2025) outlined the architectural integration:

1. **ABI Analysis Engine**: A Farscape component that ingests clang's parsed AST and enriches it with Plugify's ABI knowledge. Understands platform-specific vtable layouts, compiler quirks, and C++ standard variations that affect binary interfaces.
2. **Extended Type Mapping**: Beyond C primitive conversions to handle C++ templates, inheritance hierarchies, and RAII patterns, translating them into idiomatic F# representations.
3. **Virtual Method Dispatch**: Plugify's vtable layout knowledge enables F# bindings that correctly call through C++ virtual methods without requiring the library author to provide a C shim intermediary.
4. **Metadata Preservation**: ABI information carried through the Composer compilation pipeline so LLVM can make informed optimization decisions during LTO.

**How this bears on Pilot**: Today, Pilot routes C headers to clang and (with Phase 1.5) Wayland XML to an XParsec parser. The Plugify integration adds a third dimension: when Pilot encounters a C++ header with classes, virtual methods, and templates, the ABI Analysis Engine (informed by Plugify) enriches the clang AST with the knowledge needed to generate correct bindings without a C shim.

This is the capability gap between Phase 4 (C header bindings for HIP, XRT, libdrm, libgbm, Wayland; all of which provide C shim APIs) and Phase 5B (C++ class and template parsing for MFEM, which is a native C++ library with deep class hierarchies, virtual dispatch, and template specialization). The jump from "parse C headers" to "generate correct bindings for C++ class hierarchies" is where Plugify's ABI intelligence provides leverage.

**Disposition in documentation**: The Plugify reference in `01_Architecture_Overview.md` should not be removed. It should be recontextualized with this fuller explanation, positioned on the late-phase roadmap with forward pointers to the MFEM track (Phase 5B) and early Transcribe capability. In the immediate phases (0-4), Farscape targets C APIs and XML protocols; Plugify becomes central when C++ interface parsing is the work.

---

## 3. Per-File Assessment

### 3.1 `docs/README.md` (Documentation Index)

**Status**: Needs rewrite to serve as effective entry point.

**What's accurate**:
- Two-layer binding model description
- Output modes table (fidelity, fidelity-wrappers, pinvoke)
- "What's Implemented" list (largely correct)
- Dependency descriptions (BAREWire, Composer)

**What needs updating**:
- The "Core Infrastructure Under Development" section emphasizes CMSIS qualifier mapping and `[<FidelityExtern>]` attributes as the primary development priorities. The maturation plan shifts priority to GPU/NPU/Wayland binding generation capabilities (opaque handles, bitmask enums, enum error codes, struct layout, XML parsing). CMSIS remains valuable but is not the forcing function.
- The roadmap section lists items that no longer reflect current trajectory.
- The Mermaid pipeline diagram should simplify downstream references.
- No mention of the error text infrastructure (`ErrnoModuleGenerator`), which is implemented and architecturally significant.
- No mention of target binding surfaces (ROCm/HIP, XRT, libdrm, libgbm, Wayland).
- No reference to the maturation plan documents.

**What's missing**:
- The Pilot project system's polyglot routing capability (C headers + XML protocols + future formats)
- HelloWayland as the near-term milestone
- The broader binding target inventory
- Forward references to companion maturation plan documents

**Recommendation**: **Rewrite.** Keep the structural descriptions (two-layer model, output modes, four patterns). Replace the roadmap and infrastructure priority sections with content reflecting the current trajectory. Add a brief "Target Libraries" section and forward references to the maturation plan docs. Update as part of Phase 0.

---

### 3.2 `docs/01_Architecture_Overview.md`

**Status**: Structurally sound. The four-pattern architecture is evergreen. Needs targeted updates, not a rewrite.

**What's accurate**:
- The four architectural patterns (XParsec, Active Patterns, Catamorphism, Typed Code AST): correct and stable
- All module descriptions (CppParser, CTypeParser, ActivePatterns, DeclarationAlgebra, CodeAST, CodeRenderer, FidelityCodeGenerator, TypeMapper): accurate
- The pipeline flowchart: correct for the current clang-based path
- Design principles (closed-loop pipeline, deterministic output): stable

**What needs updating**:
- File compile order lists `MoyaAnalyzer.fs`, `MoyaSerializer.fs` (update with Phase 0 rename)
- "FNCS type-checking, Baker saturation, Alex MLIR emission" pipeline string: simplify to "Composer" where the internal stages aren't the point; use "CCS" when referring specifically to the type-checking service, "Alex" when referring to the middle-end
- Roadmap section: recontextualize Plugify with the full ABI Analysis Engine story (see Section 2.3); position it as the late-phase capability that bridges C header binding (Phases 0-4) to C++ class/template binding (Phase 5B / MFEM / Transcribe). Remove `fsnx` interactive mode reference. Add Phase 1 extensions and HelloWayland trajectory.
- "Core Infrastructure Under Development" section: rebalance to include the immediate priorities alongside CMSIS

**What's missing**:
- `ErrnoModuleGenerator.fs` is not in the module inventory despite being an implemented, architecturally significant module
- Phase 1 code generator extensions (opaque handles, bitmask enums, enum error codes, explicit struct layout) modify modules described in this doc; the doc should anticipate these as planned extensions
- The Wayland XML parser as a second input path alongside clang
- The `[sources]` routing concept in the Pilot project system

**Recommendation**: **Targeted update.** The four-pattern architecture section is evergreen and should not change. Update: file compile order (with Phase 0 rename), simplify pipeline strings, replace roadmap section, add `ErrnoModuleGenerator` to module inventory, add a brief "Planned Extensions" section covering Phase 1 capabilities since they modify modules this doc describes. Reframe Plugify reference with the full ABI Analysis Engine context (Section 2.3), positioning it as the bridge from C binding (current) to C++ binding (Phase 5B / MFEM / Transcribe). Remove `fsnx` reference. Update as part of Phase 0.

---

### 3.3 `docs/02_BAREWire_Integration.md`

**Status**: Accurate and thorough for its current scope (CMSIS/embedded peripherals). Incomplete relative to the maturation plan's BAREWire story.

**What's accurate**:
- The entire CMSIS peripheral descriptor pipeline: header parsing → macro extraction → layout calculation → descriptor assembly → CMSIS qualifier mapping → output format → consumption by Composer
- `CStructDef`, `CFieldDef`, `CQualifier` type definitions
- `calculateLayout` function and `mapQualifiersToAccess` mapping
- Bit field extraction from `_Pos`/`_Msk` macros
- The generated output format example (GPIO descriptor)

**What needs updating**:
- "FNCS type checking" references: simplify to Composer (or CCS where the type-checking service is specifically meant)
- Development sequence section ("Immediate", "Near-term", "Subsequent"): reflects CMSIS-first priority; should include system-level struct layout as a parallel track

**What's missing**: This is the substantive gap. The maturation plan introduces BAREWire descriptors for a different purpose: ABI-critical struct layout for libdrm ioctl structs, and zero-copy memory exchange contracts between CPU/GPU/NPU. Specifically:

- `LayoutKind.Explicit` with `[<FieldOffset>]` for structs like `drm_prime_handle` (12 bytes, must match kernel ABI)
- BAREWire descriptors generated from clang AST field offset data (not CMSIS qualifiers)
- The UMA exchange pattern: the same descriptor type that validates ioctl struct layout also validates body state buffer layout shared across three processors
- The connection between Phase 1.4 (struct layout extensions) and the BAREWire descriptor infrastructure

The doc currently treats BAREWire as exclusively a CMSIS peripheral concern. The maturation plan positions it as a general-purpose memory layout contract mechanism.

**Recommendation**: **Extend significantly.** The existing CMSIS content is correct and should remain. Add a new top-level section: "System-Level Struct Layout and Cross-Processor Exchange." This section covers ABI-critical structs (libdrm), BAREWire descriptors from clang field offsets, and the UMA pointer handoff pattern. Reframe the doc's introduction to cover both use cases. Update development sequence to include system-level work. Update when Phase 1.4 is implemented.

---

### 3.4 `docs/03_fsnative_Integration.md`

**Status**: Most coupled to Composer internals. Needs rewrite and rename.

**What's accurate**:
- The conceptual story: Farscape generates Clef source with `[<FidelityExtern>]` binding declarations that feed the Composer compilation pipeline; binding metadata flows through the PSG to MLIR emission
- The sliced package architecture (Fidelity.libc.IO, Fidelity.libc.Memory, etc.)
- The `[<FidelityExtern>]` attribute description and its role in carrying library name + symbol metadata

**What needs updating**:
- The document is structured around "FNCS" as a named component with its own section ("What FNCS Is"), describing union-find constraint solving, NTU types, and SRTP resolution. These are Composer/CCS compiler internals that Farscape's documentation does not need to expose. The integration contract between Farscape and Composer is: Farscape produces Clef source files with specific attributes and patterns; Composer compiles them.
- "Baker saturates intrinsic operations" and similar internal pipeline detail
- "FNCS lives at `~/repos/fsnative/`" is development-environment detail referencing stale project names
- Quotation semantic carriers section describes a speculative mechanism
- "Current State vs Target" table reflects an outdated priority set

**What's missing**:
- The expanded library target inventory: `Fidelity.ROCm.Device`, `Fidelity.DRM`, `Fidelity.GBM`, `Fidelity.Wayland.Core`, `Fidelity.XRT.Device`, etc.
- Error text pipeline: how `ErrorModuleGenerator` output (error structs, describe jump tables) integrates with Composer
- Clarification that Composer doesn't care whether declarations originated from clang headers or Wayland XML; the integration point is the generated Clef source

**Recommendation**: **Rewrite and rename** to `03_Composer_Integration.md`. Focus on the interface contract: what Farscape generates, what Composer expects, how binding metadata reaches MLIR. Remove internal Composer/CCS implementation details. Add the expanded library target inventory. Keep the CMSIS access constraints section as one example of metadata flow. Update as part of Phase 0.

---

### 3.5 `docs/04_XParsec_Architecture.md`

**Status**: Accurate and stable. The most self-contained document in the set.

**What's accurate**: Everything. The four-pattern description, the generic class pattern, the parser inventory (pCType, pMacroLine, pIntegerLiteral, pArraySize), the XParsec API notes, the active patterns with code examples, the catamorphism algebra, the typed code AST, the flow diagram. All correct.

**What needs updating**: Nothing is stale. The patterns described are architecturally stable.

**What's missing**:
- The Wayland protocol XML parser (Phase 1.5) is a new XParsec consumer. A brief forward reference: "The same XParsec infrastructure will support Wayland protocol XML parsing, producing `Declaration` types that feed the same downstream pipeline."
- The `OpaqueHandleTypedef` active pattern (Phase 1.1) is a planned addition to `ActivePatterns.fs`.

**Recommendation**: **Light touch.** Add a brief note about the planned XML parser path and the `OpaqueHandleTypedef` active pattern when those capabilities are implemented (Phases 1.1 and 1.5). No structural changes.

---

### 3.6 `docs/05_Wrapper_Generation.md`

**Status**: Accurate for the current errno-based wrapper pipeline. Missing the EnumErrorCode generalization and the error text pipeline documentation.

**What's accurate**:
- Two-layer architecture description
- Clang attribute extraction pipeline (12 attributes)
- Return semantic classification (7 patterns): all correct
- Parameter role classification: all correct
- Generated wrapper pattern examples: all correct
- File map: all correct
- "Same Four Patterns" architecture note: correct

**What needs updating**:
- CLI examples use `farscape project --project libc.moya.toml --wrappers` (update with Phase 0 rename)
- "FNCS → PSG → Baker → Alex → MLIR → LLVM" pipeline string: simplify to Composer (CCS type-checks → Baker saturates → Alex emits MLIR)

**What's missing**:
- The `EnumReturnError` return semantic (Phase 1.3): an 8th pattern for the classification table, handling APIs that return typed error enums (HIP `hipError_t`, XRT `xrt_error_code`)
- The error text pipeline: `ErrnoModuleGenerator` generates `CError` struct + `describe` jump table + `captureError` helper. This is *already implemented* (`ErrnoModuleGenerator.fs`, `WrapperCodeGenerator.fs` lines 238-259) but completely undocumented. The existing code deserves documentation now; the `ErrorModuleGenerator` generalization builds on it.
- The `HipError.capture` / `XrtError.capture` wrapper pattern
- The compile-time vs. runtime error text distinction (describe jump table vs. `hipGetErrorString` binding)

**Recommendation**: **Extend.** Add a section documenting the existing error text pipeline (errno implementation). Then add a forward-looking subsection on the `EnumErrorCode` generalization. Add `EnumReturnError` to the classification table. Update CLI examples with Phase 0 rename. Simplify pipeline references. The error text pipeline section should be added as soon as possible, since it documents existing implemented functionality. The `EnumErrorCode` extension documentation should land with Phase 1.3 implementation.

---

### 3.7 `docs/06_libc_Library_Standardization.md`

**Status**: Accurate and thorough. The content (function tables, decomposition rationale, multi-header examples) is correct in substance.

**What needs updating**:
- ~30 references to "Moya" throughout (`.moya.toml`, Moya filtering, Moya namespace, etc.): update with Phase 0 rename
- "Proposed Enhancement: Multi-Header Moya" section title: rename to Pilot
- All `.moya.toml` examples: update file extension
- TOML examples should migrate to the `[sources].headers` schema from the maturation plan, replacing the `[library].headers` field

**What's missing**:
- A brief contextual note that libc is the first binding surface, with ROCm/HIP, libdrm, libgbm, Wayland, and XRT as the next targets, gives the reader context for why the standardization work matters beyond libc itself.

**Recommendation**: **Find-and-replace rename + schema update.** No structural rewrite. Replace "Moya" → "Pilot", `.moya.toml` → `.pilot.toml`, update TOML examples to `[sources]` syntax. Add a one-paragraph contextual note about the broader binding target inventory. Update as part of Phase 0.

---

### 3.8 `README.md` (Top-Level)

**Status**: The architecture section and four-pattern descriptions are strong. Needs targeted updates.

**What's accurate**:
- Architecture diagram
- Four-pattern descriptions with code examples (XParsec, Active Patterns, Catamorphism, Typed Code AST)
- "How it all composes" section
- Output mode descriptions

**What needs updating**:
- "FNCS → Baker → Alex → MLIR" in the Fidelity Mode section: update to Composer pipeline naming (CCS, Baker, Alex)
- No mention of the Pilot project system or the error text pipeline

**What's missing**:
- The project's trajectory (HelloWayland, then NPU, then MFEM) would orient visitors
- Reference to the maturation plan documents
- Brief mention of the target binding inventory beyond libc

**Recommendation**: **Targeted update.** The architecture section and code examples are good public-facing content. Update the Fidelity Mode section, add a brief "Roadmap" or "Trajectory" section pointing to the maturation plan, simplify pipeline strings. This is a public-facing file; keep it concise. Update as part of Phase 0.

---

### 3.9 `Commercial.md`, `PATENTS.md`, `LICENSE`

**Status**: Legal/commercial documents. No technical content.

**Recommendation**: **Do not touch.**

---

## 4. Files Not Needing Deletion

No documents need deletion. The `03_fsnative_Integration.md` needs a rename (`03_Composer_Integration.md`) and rewrite, but the file slot (doc 03 covering downstream pipeline integration) remains a valid documentation need.

---

## 5. Execution Plan

### Pass 1: Phase 0 (Pilot Rename)

Atomic operation across source and documentation. One commit.

**Source files**:
- `MoyaTypes.fs` → `PilotTypes.fs`
- `MoyaAnalyzer.fs` → `PilotAnalyzer.fs`
- `MoyaSerializer.fs` → `PilotSerializer.fs`
- `Program.fs`: CLI command rename, `.pilot.toml` acceptance, deprecation warning for `.moya.toml`

**Documentation files** (all touched in same commit):
- `docs/README.md`: Rewrite (Section 3.1)
- `docs/01_Architecture_Overview.md`: Targeted update (Section 3.2)
- `docs/03_fsnative_Integration.md` → `docs/03_Composer_Integration.md`: Rewrite (Section 3.4)
- `docs/05_Wrapper_Generation.md`: CLI example rename only (full extension in Pass 2)
- `docs/06_libc_Library_Standardization.md`: Find-and-replace + schema update (Section 3.7)
- `README.md` (top-level): Targeted update (Section 3.8)

### Pass 2: Phase 1 Extensions

Each sub-task updates the relevant documentation alongside the code change.

| Phase | Code Change | Doc Update |
|---|---|---|
| 1.1 Opaque handles | `ActivePatterns.fs`, `FidelityCodeGenerator.fs`, `TypeMapper.fs` | `01_Architecture_Overview.md`: add to module inventory |
| 1.2 Bitmask enums | `FidelityCodeGenerator.fs`, `CodeAST.fs`, `CodeRenderer.fs` | `01_Architecture_Overview.md`: note in CodeAST description |
| 1.3 EnumErrorCode | `ErrnoModuleGenerator.fs` → `ErrorModuleGenerator.fs`, `WrapperTypes.fs`, `WrapperPatternAnalyzer.fs`, `WrapperCodeGenerator.fs` | `05_Wrapper_Generation.md`: add error text pipeline section + EnumReturnError pattern |
| 1.4 Struct layout | `CodeAST.fs`, `CodeRenderer.fs`, `FidelityCodeGenerator.fs`, `DescriptorGenerator.fs` | `02_BAREWire_Integration.md`: add system-level struct layout section |
| 1.5 Wayland XML parser | New: `WaylandProtocolParser.fs` | `04_XParsec_Architecture.md`: add XML parser note |

### Pass 3: Phase 2-3 (Library Bindings + HelloWayland)

No doc file changes expected beyond what Pass 1 and Pass 2 cover. The maturation plan documents themselves (`farscape-maturation-plan.md`, `farscape-phase4-npu-xrt-binding.md`, `farscape-phase5-mfem-ingestion.md`) serve as the design documentation for these phases.

---

## 6. Document Priority Matrix

| File | Action | Effort | When | Blocks |
|---|---|---|---|---|
| `docs/README.md` | Rewrite | Medium | Phase 0 | Nothing; but is the entry point |
| `docs/01_Architecture_Overview.md` | Targeted update | Low-Medium | Phase 0 | Nothing |
| `docs/02_BAREWire_Integration.md` | Extend | Medium | Phase 1.4 | Nothing directly; BAREWire descriptor usage |
| `docs/03_fsnative_Integration.md` | Rewrite + rename | Medium | Phase 0 | Nothing |
| `docs/04_XParsec_Architecture.md` | Light touch | Low | Phase 1.5 | Nothing |
| `docs/05_Wrapper_Generation.md` | Extend | Medium | Phase 1.3 | Error text documentation |
| `docs/06_libc_Library_Standardization.md` | Find-and-replace | Low | Phase 0 | Nothing |
| `README.md` (top-level) | Targeted update | Low | Phase 0 | Nothing |

---

*This assessment supports the Farscape Maturation Plan (Phases 0-5+) and should be consumed by an agent performing the Phase 0 documentation pass.*

*SpeakEZ Technologies | Fidelity Framework*
