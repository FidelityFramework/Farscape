# Farscape Maturation Plan: Phases 0-3

## Through HelloWayland

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

> **Handle-record caveat (added 2026-08-03).** Validation criteria in this document that
> require opaque handles to emit as distinct wrapper structs rather than `nativeint` are
> **inverted**. A single-word `{ Handle: nativeint }` record is memref-backed under the
> current compiler and reaches a C callee as the address of a slot rather than as the
> handle; `ofHandle` also allocates and leaks eight bytes per construction. Bare `nativeint`
> is the correct Layer 1 output. These gates would pass while producing miscompiling code —
> see `docs/14_Binding_Generation_Gaps.md` §3.

## 1. Scope

This document sequences the work required to mature Farscape from its current state (C header parsing via clang, libc bindings via Moya project system) into a tool capable of producing the system-level bindings needed for a native Wayland application on Strix Halo. The culminating milestone is **HelloWayland**: a Clef application, compiled by Composer, that opens a Wayland toplevel surface via DMA-BUF presentation and displays a message on screen, closable through standard Hyprland/Wayland window management conventions.

No WebView. No WebSocket bridge. No indirection. The Farscape-generated bindings for libwayland-client, libgbm, libdrm, and ROCm/HIP *are* the UI model.

Four tracks within this plan:

1. **Pilot rename** (housekeeping; do first)
2. **Code generator extensions** (opaque handles, bitmask enums, error text infrastructure, struct layout)
3. **Library binding progression** (ROCm/HIP → libdrm/libgbm → Wayland)
4. **Wayland protocol XML parser** (architectural capability, not shortcut)

Subsequent phases (NPU binding via XRT/XDNA, MFEM algorithmic ingestion) are covered in companion documents.

---

## 2. Current State

### 2.1 What Farscape Can Do

- Parse C/C++ headers via clang two-pass (JSON AST + macro extraction)
- Map C types to Clef types with platform ABI awareness (LP64/LLP64/ILP32/IP16)
- Generate `[<FidelityExtern>]` bindings (Layer 1) and idiomatic wrappers (Layer 2)
- Detect return semantics (CountOrError, ZeroSuccessOrError, AllocatedPointer, OpaqueHandleReturn)
- Infer parameter roles (InputBuffer, OutputBuffer, FileDescriptor, BufferLength)
- **Error text infrastructure**: `ErrnoModuleGenerator` extracts `E*` macros from headers with documentation comments, generates `CError` struct (`Code: int32`, `Description: string`), builds a `describe` jump table that compiles to O(1) rodata lookup with zero heap allocation. The `captureError` helper in `WrapperCodeGenerator` reads `__errno_location()` and hydrates the description at the call site. Error descriptions flow from C header comments at codegen time into the binary as rodata.
- Moya project system with multi-header support, namespace scoping, declaration merging
- Error convention support: Errno, ReturnCode

### 2.2 What It Cannot Do Yet

- Distinguish opaque handle types from generic `nativeint` pointers
- Emit `[<Flags>]` bitmask enums
- Handle `EnumErrorCode` convention with compile-time and runtime error text
- Emit struct layouts with explicit field offsets and BAREWire descriptors
- Generate delegate types from function pointer struct fields
- Parse Wayland protocol XML (or any non-C input format)

---

## 3. Phase 0: Pilot Rename

**Scope**: Rename Moya → Pilot across the codebase. Short-lived work, but doing it now prevents downstream code from accumulating references to the old name.

### 3.1 File Changes

| Current | New |
|---|---|
| `MoyaTypes.fs` | `PilotTypes.fs` |
| `MoyaAnalyzer.fs` | `PilotAnalyzer.fs` |
| `MoyaSerializer.fs` | `PilotSerializer.fs` |
| `.moya.toml` file extension | `.pilot.toml` |
| `MoyaProject` type | `PilotProject` type |
| `MoyaTypes.NamespaceSpec` | `PilotTypes.NamespaceSpec` |
| `MoyaTypes.LibrarySpec` | `PilotTypes.LibrarySpec` |
| `MoyaTypes.AnalysisResult` | `PilotTypes.AnalysisResult` |

### 3.2 CLI Changes

```bash
# Current
farscape project --project lib.moya.toml

# New
farscape pilot --project lib.pilot.toml

# Backward compat: accept .moya.toml with deprecation warning for one release cycle
```

### 3.3 Pilot Project Schema Extension

The rename is an opportunity to extend the `.pilot.toml` schema with capabilities needed by subsequent phases:

```toml
# New top-level section for pre-process directives
[sources]
# C/C++ headers parsed via clang (existing path)
headers = ["/usr/include/wayland-client.h"]
# XML protocol definitions (new path, Phase 1.5)
xml_protocols = ["/usr/share/wayland/wayland.xml"]
# Future: Rust crate manifests, Python stubs, etc.
```

This `[sources]` section replaces the current `[library].headers` field and opens the door for Pilot to route different source formats to different parsers within the same project. The architectural point: Pilot is a navigator across input formats, not a wrapper around a single clang invocation.

### 3.4 Metaphor

Pilot is the navigator. It reads the project file, decides the route (which headers, which protocols, which namespaces, which functions, which error conventions), and steers the parsing and generation pipeline. Farscape is the ship, Pilot navigates it through the binding space.

### 3.5 Pilot Discovery: Generalized Source Asset Discovery

**Problem**: Every `[sources]` and `[library].headers` entry in this document is a manually specified absolute path. For an SDK with 50 headers, a Wayland install with 30 protocol XMLs, or a vendor tree with nested include directories, this is untenable. Pilot claims to be a navigator, but a navigator that requires the crew to hand-draw the map first is not navigating — it is following instructions.

A navigator discovers. Given a directory, Pilot should scan for everything it knows how to parse, classify what it finds, flag ambiguities, and produce a curated `.pilot.toml`. The discovery is driven by Pilot's available parsers, not by foreknowledge of the target library.

#### 3.5.1 Discovery Model

```
farscape pilot discover /opt/rocm/include --library amdhip64
farscape pilot discover /usr/share/wayland-protocols
farscape pilot discover /opt/xilinx/xrt/include --library xrt
```

Pilot walks the directory tree recursively and classifies files by what it can process:

| File Pattern | Classification | Parser |
|---|---|---|
| `*.h` | C header candidate | CppParser (clang) |
| `*.hpp`, `*.hxx`, `*.hh` | C++ header candidate | CppParser (clang, C++ mode) |
| `*.xml` with `<protocol>` root | Wayland protocol XML | WaylandProtocolParser |
| `*.xml` with `<node>` root | D-Bus introspection XML | Future parser |
| `*.xml` with `<registry>` root | Vulkan/OpenCL registry | Future parser |
| `*.pc` | pkg-config metadata | Metadata extraction |
| `CMakeLists.txt`, `meson.build` | Build system metadata | Metadata extraction |

Discovery is parser-driven: if Pilot has no parser for a file type, it does not discover it. As parsers are added (Phase 1.5 adds XML protocols), the discovery surface grows automatically.

#### 3.5.2 C vs C++ Heuristics

Not all `.h` files are equal. Discovery classifies headers by inspecting content:

**C indicators**: No `namespace`, no `class`, no templates. May contain `extern "C"` guards. Functions declared at file scope. Typical of POSIX, kernel, driver APIs.

**C++ indicators**: `namespace` declarations, `class`/`struct` with methods, template parameters, `std::` references, `*.hpp`/`*.hxx` extension. Typical of HIP, XRT, Qt.

**Hybrid**: `extern "C" { }` blocks wrapping C-linkage functions inside otherwise C++ headers. Common in HIP (`hip_runtime_api.h` is C++ but the API surface is `extern "C"`). Discovery should flag these and note the bindable C surface.

**Umbrella detection**: A header that has a high `#include` count relative to its own declaration count is likely an umbrella. Discovery should identify these as preferred entry points (e.g., `hip_runtime.h` includes `hip_runtime_api.h` and others).

**Skip heuristics**: Files matching `*_internal.h`, `*_private.h`, `*_impl.h`, `detail/*`, `internal/*` are typically not public API. Discovery includes them in the scan but flags them for exclusion.

#### 3.5.3 Metadata Extraction

When discovery finds pkg-config `.pc` files or build system files in the tree, it extracts hints:

- **pkg-config**: Library name (`Name:`), include paths (`Cflags: -I...`), link flags (`Libs: -l...`). This can auto-populate `[library].name`, `[library].include_paths`.
- **CMake**: `find_package` target names, `target_include_directories` paths.
- **meson**: `dependency()` names, include directory declarations.

These are suggestions, not authoritative. Discovery reports them as hints that the user (or TUI) can accept or override.

#### 3.5.4 Warning and Error Classification

Discovery results include typed diagnostics:

**Errors** (discovery cannot proceed or results are empty):
- `NoParseableFiles`: "No files found that Pilot knows how to parse in {directory}"
- `DirectoryNotFound`: "Directory {path} does not exist"

**Warnings** (discovery succeeded but results may need curation):
- `NoUmbrellaHeader`: "Found {n} headers but no obvious umbrella — consider specifying entry points"
- `InternalHeadersFound`: "Found {n} headers matching internal/private patterns — excluded by default"
- `MixedLanguage`: "Directory contains both C and C++ headers — verify intended API surface"
- `LargeHeaderCount`: "Found {n} headers — consider using an umbrella header or filtering by prefix"
- `OrphanIncludes`: "Headers reference include paths not found in the scanned tree"

**Suggestions** (actionable intelligence):
- `PkgConfigFound`: "Found {name}.pc — library name '{lib}', include path '{path}'"
- `UmbrellaDetected`: "{file} appears to be an umbrella header ({n} includes, {m} own declarations)"
- `ProtocolsFound`: "Found {n} Wayland protocol XML files"
- `ExternCDetected`: "{file} is C++ with extern \"C\" API surface — bindable"

#### 3.5.5 Output Modes

**Non-interactive** (default): Discovery runs, prints classified results with diagnostics to stdout, writes a `.pilot.toml` with discovered sources pre-populated. The user edits the TOML and runs `farscape project`.

```
farscape pilot discover /opt/rocm/include --library amdhip64 --output ./rocm
```

**Interactive / TUI** (future, via Thuja): Discovery results are presented in an MVU-driven terminal interface. The user can select/deselect files, accept/reject suggestions, mark headers as umbrella vs individual, and preview the resulting `.pilot.toml` before saving. This replaces the discover-then-edit-TOML workflow with a single interactive session.

```
farscape pilot discover /opt/rocm/include --library amdhip64 --interactive
```

#### 3.5.6 Architecture

The discovery logic is pure — no IO decisions, no TOML writing. It returns a typed result that the CLI/TUI layer consumes.

**New module**: `PilotDiscovery.fs` in `Farscape.Core`, positioned after `PilotAnalyzer.fs`. Same pattern: `PilotAnalyzer.analyze` returns `AnalysisResult`, `PilotDiscovery.discover` returns `DiscoveryResult`.

```fsharp
type FileClassification =
    | CHeader of path: string * isUmbrella: bool * isInternal: bool
    | CppHeader of path: string * hasExternC: bool * isInternal: bool
    | ProtocolXml of path: string * format: XmlProtocolFormat
    | PkgConfig of path: string * metadata: PkgConfigInfo
    | BuildSystemFile of path: string * kind: BuildSystemKind

type Diagnostic =
    | Error of DiscoveryError
    | Warning of DiscoveryWarning
    | Suggestion of DiscoverySuggestion

type DiscoveryResult = {
    RootDirectory: string
    Files: FileClassification list
    Diagnostics: Diagnostic list
    SuggestedLibraryName: string option
    SuggestedIncludePaths: string list
}
```

**CLI integration**: New `pilot discover` subcommand in `Program.fs`. Non-interactive mode calls `PilotDiscovery.discover`, formats output, optionally writes `.pilot.toml` via `PilotSerializer`.

**TUI integration** (future): `PilotInteractive.fs` in `Farscape.Cli` consumes `DiscoveryResult` as the initial MVU model state. The TUI renders the file list with diagnostics, allows curation, and persists on exit.

| Module | Location | Responsibility |
|---|---|---|
| `PilotDiscovery.fs` | `Farscape.Core` (after `PilotAnalyzer.fs`) | Pure discovery logic: directory → `DiscoveryResult` |
| `pilot discover` command | `Farscape.Cli/Program.fs` | CLI entry point, non-interactive output + TOML generation |
| `PilotInteractive.fs` (future) | `Farscape.Cli` | Thuja MVU TUI for interactive curation |

#### 3.5.7 Relationship to Other Phases

Discovery is not a hard prerequisite for any phase in this plan — every phase can proceed with manually specified paths. However, discovery dramatically improves the developer experience for Phases 2 and 3, where the SDK structures are well-known and the header counts are non-trivial. The TUI mode is a natural companion to `pilot analyze` (Section 3.2) and can be introduced incrementally.

Discovery also scales beyond this plan: when Farscape gains parsers for Rust crate manifests, Python stubs, or GStreamer plugin registries, the discovery infrastructure is already in place. Point Pilot at a directory, let it find what it can.

---

## 4. Phase 1: Code Generator Extensions

No new parser work for 4.1-4.4; the clang parser already extracts everything needed. Section 4.5 introduces the Wayland XML parser as a new Pilot-routed input format.

### 4.1 Opaque Handle Types

**Problem**: All non-char, non-void pointer types collapse to `nativeint`. HIP has a dozen opaque handles (`hipStream_t`, `hipEvent_t`, `hipModule_t`, etc.) that must be type-distinct across the FFI.

**Detection**: The typedef algebra already extracts `(name, underlyingType)` pairs. When the underlying type matches the pattern `struct i<n>_t*` or is a forward-declared struct pointer, classify it as an opaque handle.

**Output**: Emit a zero-cost wrapper struct per opaque handle:

```fsharp
/// Opaque handle for HIP stream. Wraps a native pointer.
[<Struct>]
type hipStream_t = { Handle: nativeint }

module hipStream_t =
    let zero = { Handle = 0n }
    let isNull (h: hipStream_t) = h.Handle = 0n
```

**Module changes**:
- `ActivePatterns.fs`: Add `OpaqueHandleTypedef` active pattern
- `FidelityCodeGenerator.fs`: Check typedef map for opaque patterns before falling through to generic `nativeint`
- `TypeMapper.fs`: Register opaque handle names so they survive type resolution

### 4.2 Bitmask Enum Detection

**Problem**: HIP flag enums (`hipHostMallocMapped = 0x02`, `hipHostMallocCoherent = 0x40`) need `[<Flags>]` to support `|||` composition in Clef.

**Detection heuristic**: If more than half the non-zero values in an enum are exact powers of 2, or if explicit hex patterns with single-bit values dominate, emit `[<Flags>]`.

**Output**:

```fsharp
[<Flags>]
type hipHostMallocFlags =
    | Default        = 0x00u
    | Portable       = 0x01u
    | Mapped         = 0x02u
    | WriteCombined  = 0x04u
    | Coherent       = 0x40u
    | NonCoherent    = 0x80u
```

**Module changes**:
- `FidelityCodeGenerator.fs`: `generateEnumDecl` checks for bitmask pattern, conditionally emits `[<Flags>]`
- `CodeAST.fs`: `EnumType` gains an `IsFlags: bool` field
- `CodeRenderer.fs`: Renders `[<Flags>]` attribute when `IsFlags = true`

### 4.3 EnumErrorCode Convention with Error Text

**Problem**: HIP and XRT APIs return a typed error enum (`hipError_t`, `xrt_error_code`) where one value means success and all others are error codes. The existing `ErrorConvention` type supports `Errno` and `ReturnCode` but not named enum errors. Critically, the existing errno pipeline must be preserved and generalized, not replaced.

#### 4.3.1 The Existing Pattern (Preserved)

Farscape's errno infrastructure is the template for this extension. The pipeline:

1. `ErrnoModuleGenerator.filterErrnoMacros` extracts `E*` macros from parsed headers with their documentation comments
2. `generateCErrorType` emits `[<Struct>] type CError = { Code: int32; Description: string }`
3. `generateErrnoDecls` builds a `describe` function: a match expression over errno codes that returns header comment text. Compiles to a jump table over rodata strings.
4. `WrapperCodeGenerator` generates a `captureError` helper that reads `__errno_location()`, calls `Errno.describe`, and constructs a `CError` value.
5. Every `Result`-returning wrapper uses `captureError` on the error path.

The result: error descriptions flow from C header comments, through the parser at codegen time, into the binary as rodata. Zero heap allocation. O(1) lookup. The developer sees `Error { Code = 2; Description = "No such file or directory" }` without any runtime string formatting.

This pattern is correct and stays intact for all errno-based libraries.

#### 4.3.2 Generalization to Enum Error Codes

For HIP, the error type is an enum, not a set of macros. The same architecture applies with two sources of error text:

**Compile-time path** (same architecture as errno): Extract `hipError_t` enum values from the header. Each value has a doc comment in `hip_runtime_api.h` (e.g., `hipErrorInvalidValue` is documented as "Invalid Value"). Generate a `describe` function that maps enum values to description strings from header comments. Same jump table, same rodata, same zero-allocation guarantee.

**Runtime path** (new, additive): HIP provides `hipGetErrorString(hipError_t) → const char*` and `hipGetErrorName(hipError_t) → const char*`. These return driver-specific messages that may be more detailed than header comments, and they cover error codes added in newer driver versions that were not present in the header at codegen time. Bind these as fallback functions.

**Combined error type**:

```fsharp
/// Stack-allocated HIP error: enum code + human-readable description.
[<Struct>]
type HipError = {
    Code: hipError_t
    Description: string
}

module HipError =
    /// Compile-time description from header comments. Zero allocation.
    let describe (code: hipError_t) : string =
        match code with
        | hipError_t.hipErrorInvalidValue      -> "Invalid Value"
        | hipError_t.hipErrorOutOfMemory       -> "Out of Memory"
        | hipError_t.hipErrorNotInitialized    -> "Driver not initialized"
        // ... generated from header enum doc comments
        | _ -> "Unknown HIP error"

    /// Runtime description from HIP driver. Allocates (C string copy).
    let describeRuntime (code: hipError_t) : string =
        let ptr = Bindings.hipGetErrorString(code)
        NativeString.read ptr

    /// Capture error with compile-time description (preferred hot path).
    let capture (code: hipError_t) : HipError =
        { Code = code; Description = describe code }
```

**Wrapper output**:

```fsharp
let streamCreate () : Result<hipStream_t, HipError> =
    let mutable stream = hipStream_t.zero
    match Bindings.hipStreamCreate(&stream) with
    | hipError_t.hipSuccess -> Ok stream
    | err -> Error (HipError.capture err)
```

#### 4.3.3 Pilot Project Configuration

```toml
[error_convention]
default = "enum_error_code"
error_type = "hipError_t"
success_value = "hipSuccess"
# Optional: runtime error string functions for driver fallback
error_string_fn = "hipGetErrorString"
error_name_fn = "hipGetErrorName"
```

#### 4.3.4 Module Changes

| Module | Change |
|---|---|
| `ErrnoModuleGenerator.fs` → `ErrorModuleGenerator.fs` | Generalize: factor the common pattern (extract constants → generate struct → generate describe → generate capture) so it works for both errno macros and error enums. The errno path remains a specialization of the general pattern. |
| `PilotTypes.fs` | Add `EnumErrorCode of errorType: string * successValue: string * errorStringFn: string option * errorNameFn: string option` to `ErrorConvention` |
| `PilotSerializer.fs` | Parse `error_type`, `success_value`, `error_string_fn`, `error_name_fn` from TOML |
| `WrapperTypes.fs` | Add `EnumReturnError of enumType: string` to `ReturnSemantic` |
| `WrapperPatternAnalyzer.fs` | Detect enum error return when convention is `EnumErrorCode` |
| `WrapperCodeGenerator.fs` | Generate `Result<T, ErrorStruct>` wrappers using `ErrorStruct.capture` on the error path, parallel to the existing `captureError` pattern for errno |
| `CodeAST.fs` | Ensure `MatchExpr` can handle enum case patterns (not just integer literals) |

The architectural point: the error text pipeline is not a feature bolted onto the wrapper layer. It is a codegen-time enrichment that flows header documentation into the binary. The same pipeline handles POSIX errno, HIP error enums, XRT error enums, and any future API that reports errors through named codes. The pattern is: extract at codegen time, generate jump table, carry description with the error value, optionally bind a runtime fallback for driver-versioned messages.

### 4.4 Struct Layout with Explicit Offsets and BAREWire Descriptors

**Problem**: libdrm ioctl structs must match kernel ABI byte-for-byte. The current `[<StructLayout(LayoutKind.Sequential)>]` output is correct for most cases, but ABI-critical structs need explicit offsets and parallel BAREWire descriptors.

**Detection**: Configured per-struct in the `.pilot.toml` via `abi_critical_structs`, or inferred when a struct is used in an ioctl-style call pattern.

**Output**: When flagged as ABI-critical:

```fsharp
[<Struct; StructLayout(LayoutKind.Explicit, Size = 12)>]
type drm_prime_handle =
    [<FieldOffset(0)>] val handle: uint32
    [<FieldOffset(4)>] val flags:  uint32
    [<FieldOffset(8)>] val fd:     int32
```

Plus a BAREWire descriptor:

```fsharp
let drmPrimeHandleDescriptor = {
    Name = "drm_prime_handle"
    Size = 12u
    Alignment = 4u
    Fields = [
        { Name = "handle"; Offset = 0u; Size = 4u; Type = U32 }
        { Name = "flags";  Offset = 4u; Size = 4u; Type = U32 }
        { Name = "fd";     Offset = 8u; Size = 4u; Type = I32 }
    ]
}
```

The BAREWire descriptor matters beyond correctness verification. In the UMA pointer handoff pattern (CPU ↔ GPU ↔ NPU), BAREWire descriptors provide the contract that all processors agree on the memory layout. When a `drm_prime_handle` is passed to a kernel ioctl, the struct must match the kernel's layout. When body state buffers are shared between CPU integration, GPU rendering, and NPU acceleration, the BAREWire descriptor guarantees all three access the same field at the same offset. A naive initial approach (manual struct definitions, `memcpy` at boundaries) is acceptable for early work. But the BAREWire descriptor generated here for ioctl structs is the same mechanism that scales to zero-copy UMA exchange. Getting the descriptor infrastructure right for libdrm validates it for the simulation data path.

**Module changes**:
- `CodeAST.fs`: `RecordType` gains optional `ExplicitLayout` with field offsets
- `CodeRenderer.fs`: Render `LayoutKind.Explicit` with `[<FieldOffset>]` when present
- `FidelityCodeGenerator.fs`: Extract field offset information from clang AST (available in the JSON dump)
- `DescriptorGenerator.fs`: Extended to emit BAREWire struct descriptors from parsed declarations

### 4.5 Wayland Protocol XML Parser

**Problem**: Wayland protocols are defined in XML files (`wayland.xml`, `xdg-shell.xml`, `linux-dmabuf-unstable-v1.xml`), not C headers. The standard approach is to run `wayland-scanner` to generate C stubs and then parse those stubs. This works but discards the semantic information in the XML: which arguments are `new_id` (object creation), which are `fd` (file descriptor passing), which are enum references, and the event/request directionality.

**Why not the shortcut**: The ThreeBody Wayland surface uses only three protocols and a handful of interfaces. Parsing `wayland-scanner` output would be faster for this specific case. But Pilot's value proposition is routing different source formats to appropriate parsers within a single project file. If Pilot can only process C headers, it is a C binding tool. If it can process XML protocols, it becomes a polyglot API navigator. This is an investment in architectural capability. The ThreeBody surface is the forcing function, but the resulting parser infrastructure serves every Wayland compositor/client binding, every D-Bus introspection XML binding, and any future structured API definition that Fidelity needs to consume.

The same reasoning applies to other XML-defined APIs: GStreamer plugin registries, Vulkan XML specifications (`vk.xml`), OpenCL API definitions. Pilot's ability to route XML to an appropriate parser is a capability multiplier.

**Implementation**: XParsec-based XML parser. The Wayland protocol XML schema is compact and well-defined:

```xml
<protocol name="wayland">
  <interface name="wl_display" version="1">
    <request name="sync">
      <arg name="callback" type="new_id" interface="wl_callback"/>
    </request>
    <event name="error">
      <arg name="object_id" type="object"/>
      <arg name="code" type="uint"/>
      <arg name="message" type="string"/>
    </event>
    <enum name="error">
      <entry name="invalid_object" value="0"/>
      <entry name="invalid_method" value="1"/>
      <entry name="no_memory" value="2"/>
    </enum>
  </interface>
</protocol>
```

Each `<interface>` produces:
- An opaque handle type (the proxy pointer for that interface)
- Request functions (client → compositor), with typed parameters
- A listener struct with delegate fields for events (compositor → client)
- Associated enums

The parser produces the same `Declaration` types that the clang path produces. Everything downstream (TypeMapper, CodeGenerator, CodeRenderer) remains unchanged. The only new module is the parser itself.

**Pilot integration**:

```toml
[sources]
xml_protocols = [
    "/usr/share/wayland/wayland.xml",
    "/usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml",
    "/usr/share/wayland-protocols/unstable/linux-dmabuf/linux-dmabuf-unstable-v1.xml"
]
```

**Output for event listeners** (typed delegates):

```fsharp
type WlPointerEnterHandler =
    delegate of data: nativeint * pointer: wl_pointer
              * serial: uint32 * surface: wl_surface
              * surfaceX: int32 * surfaceY: int32 -> unit

[<Struct; StructLayout(LayoutKind.Sequential)>]
type wl_pointer_listener = {
    enter:  WlPointerEnterHandler
    leave:  WlPointerLeaveHandler
    motion: WlPointerMotionHandler
    button: WlPointerButtonHandler
    axis:   WlPointerAxisHandler
}
```

**Module changes**:
- New: `WaylandProtocolParser.fs` (XParsec-based XML parser producing `Declaration` list)
- `PilotSerializer.fs`: Parse `[sources].xml_protocols` field
- `BindingGenerator.fs`: Route XML protocols to `WaylandProtocolParser`, merge resulting declarations with header-sourced declarations via `DeclarationAlgebra.mergeDeclarations`

---

## 5. Phase 2: ROCm/HIP Binding

**Goal**: Generate a complete, type-safe Clef binding for the HIP runtime API surface needed by ThreeBody.

### 5.1 Pilot Project

```toml
# rocm-hip.pilot.toml

[library]
name = "amdhip64"

[sources]
headers = ["/opt/rocm/include/hip/hip_runtime_api.h"]
include_paths = ["/opt/rocm/include"]
defines = ["__HIP_PLATFORM_AMD__"]

[output]
mode = "fidelity"
directory = "./bindings/rocm"

[error_convention]
default = "enum_error_code"
error_type = "hipError_t"
success_value = "hipSuccess"
error_string_fn = "hipGetErrorString"
error_name_fn = "hipGetErrorName"

[options]
opaque_handles = true
flags_enums = true

[[namespace]]
name = "Fidelity.ROCm.Device"
library = "amdhip64"
prefixes = ["hipDevice", "hipGetDevice"]
functions = ["hipInit", "hipDriverGetVersion", "hipRuntimeGetVersion"]

[[namespace]]
name = "Fidelity.ROCm.Memory"
library = "amdhip64"
prefixes = ["hipMalloc", "hipFree", "hipMemcpy", "hipMemset"]
functions = [
    "hipHostMalloc", "hipHostFree", "hipHostGetDevicePointer",
    "hipImportExternalMemory", "hipExternalMemoryGetMappedBuffer",
    "hipDestroyExternalMemory"
]

[[namespace]]
name = "Fidelity.ROCm.Stream"
library = "amdhip64"
prefixes = ["hipStream"]

[[namespace]]
name = "Fidelity.ROCm.Event"
library = "amdhip64"
prefixes = ["hipEvent"]

[[namespace]]
name = "Fidelity.ROCm.Module"
library = "amdhip64"
prefixes = ["hipModule"]
functions = ["hipLaunchKernel", "hipFuncGetAttributes"]

[[namespace]]
name = "Fidelity.ROCm.Error"
library = "amdhip64"
functions = ["hipGetErrorString", "hipGetErrorName",
             "hipGetLastError", "hipPeekAtLastError"]
```

### 5.2 Validation Criteria

- `hipStream_t`, `hipEvent_t`, `hipModule_t` emit as distinct wrapper structs, not `nativeint`
- `hipHostMallocFlags` emits with `[<Flags>]`
- `hipError_t` emits as a standard enum with all values
- `HipError` struct generated with `describe` jump table from header enum comments
- `HipError.describeRuntime` binds `hipGetErrorString` for driver fallback
- All API functions return `Result<T, HipError>` in the Layer 2 wrappers
- `hipDeviceAttributeIntegrated` is accessible for APU detection
- The generated code compiles

---

## 6. Phase 3: Native Wayland UI Frame

**Goal**: Generate bindings sufficient to open a Wayland toplevel surface, allocate GBM-backed DMA-BUFs, and present frames to the compositor. This is the UI model for HelloWayland.

### 6.1 The Minimal Surface

The frame on screen requires bindings for three libraries in a specific dependency order:

```
libdrm  →  libgbm  →  libwayland-client
  │            │              │
  DRM device   GBM buffer     Wayland surface
  render node  DMA-BUF fd     zwp_linux_dmabuf_v1
```

Plus the HIP external memory import from Phase 2.

### 6.2 libdrm Binding

```toml
# libdrm.pilot.toml
[library]
name = "drm"

[sources]
headers = ["/usr/include/xf86drm.h", "/usr/include/xf86drmMode.h"]
include_paths = ["/usr/include/libdrm"]

[output]
mode = "fidelity"
directory = "./bindings/drm"

[options]
abi_critical_structs = ["drm_prime_handle", "drm_mode_create_dumb"]

[[namespace]]
name = "Fidelity.DRM"
library = "drm"
functions = [
    "drmOpen", "drmClose",
    "drmPrimeHandleToFD", "drmPrimeFDToHandle",
    "drmGetDevices2", "drmFreeDevices",
    "drmGetRenderDeviceNameFromFd"
]
```

### 6.3 libgbm Binding

```toml
# libgbm.pilot.toml
[library]
name = "gbm"

[sources]
headers = ["/usr/include/gbm.h"]

[output]
mode = "fidelity"
directory = "./bindings/gbm"

[options]
opaque_handles = true

[[namespace]]
name = "Fidelity.GBM"
library = "gbm"
functions = [
    "gbm_create_device", "gbm_device_destroy",
    "gbm_bo_create", "gbm_bo_destroy",
    "gbm_bo_get_fd", "gbm_bo_get_stride",
    "gbm_bo_get_width", "gbm_bo_get_height",
    "gbm_bo_get_format", "gbm_bo_get_modifier"
]
```

### 6.4 Wayland Binding via Protocol XML

The Phase 1.5 XML parser pays off here. The Wayland binding is generated from protocol XML, not from `wayland-scanner` C output.

```toml
# wayland.pilot.toml
[library]
name = "wayland-client"

[sources]
headers = ["/usr/include/wayland-client-core.h"]
include_paths = ["/usr/include"]
xml_protocols = [
    "/usr/share/wayland/wayland.xml",
    "/usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml",
    "/usr/share/wayland-protocols/unstable/linux-dmabuf/linux-dmabuf-unstable-v1.xml"
]

[output]
mode = "fidelity"
directory = "./bindings/wayland"

[options]
opaque_handles = true

[[namespace]]
name = "Fidelity.Wayland.Core"
library = "wayland-client"
functions = [
    "wl_display_connect", "wl_display_disconnect",
    "wl_display_dispatch", "wl_display_roundtrip",
    "wl_display_flush", "wl_display_get_fd",
    "wl_proxy_marshal_flags",
    "wl_proxy_add_listener", "wl_proxy_destroy"
]

[[namespace]]
name = "Fidelity.Wayland.Protocol"
library = "wayland-client"
xml_interfaces = ["wl_compositor", "wl_surface", "wl_buffer",
                   "wl_registry", "wl_callback", "wl_shm"]

[[namespace]]
name = "Fidelity.Wayland.XdgShell"
library = "wayland-client"
xml_interfaces = ["xdg_wm_base", "xdg_surface", "xdg_toplevel"]

[[namespace]]
name = "Fidelity.Wayland.DmaBuf"
library = "wayland-client"
xml_interfaces = ["zwp_linux_dmabuf_v1", "zwp_linux_buffer_params_v1"]
```

The hybrid `[sources]` section demonstrates Pilot's routing: C headers for core library functions, XML protocols for interface definitions. Both produce `Declaration` lists that merge through the existing pipeline.

### 6.5 HelloWayland Milestone

A Fidelity project that:

1. Connects to Wayland display
2. Binds `wl_compositor`, `xdg_wm_base`, `zwp_linux_dmabuf_v1` via registry listener
3. Opens DRM render node, creates GBM device
4. Allocates GBM buffer (e.g., 640×480 ARGB8888)
5. Exports as DMA-BUF fd
6. Imports into HIP as external memory
7. HIP kernel writes "Hello, Wayland" text (simple bitmap font) on a solid background
8. Creates `xdg_toplevel` with title "HelloWayland"
9. Imports DMA-BUF fd via `zwp_linux_buffer_params_v1`, creates `wl_buffer`
10. Attaches buffer to surface, commits
11. Enters event loop: dispatches Wayland events, handles `xdg_toplevel.close` → clean exit

**Closable by standard Hyprland conventions**: The `xdg_toplevel.close` event fires when the user presses the compositor's close binding (e.g., `$mod+Q`). The application handles this event and performs clean shutdown: destroy surfaces, free GBM buffers, release HIP memory, disconnect from display.

**What this proves**:
- Farscape generates working bindings for five native libraries
- Pilot routes C headers and XML protocols through a single project
- The DMA-BUF presentation pipeline works end-to-end
- HIP compute writes to compositor-presented buffers with zero copies
- The Fidelity compilation toolchain produces a native binary that participates in the Wayland protocol correctly

This is the structural foundation. Everything ThreeBody adds after this is additive.

---

## 7. Schedule Dependency Graph

```
Phase 0: Pilot Rename
    │
    ▼
Phase 1: Code Generator Extensions
    ├── 1.1 Opaque handle types
    ├── 1.2 Bitmask enum detection
    ├── 1.3 EnumErrorCode with error text
    ├── 1.4 Struct layout with BAREWire descriptors
    └── 1.5 Wayland protocol XML parser
            │
            ▼
    ┌───────┴───────┐
    │               │
Phase 2:        Phase 3:
ROCm/HIP        Native Wayland UI Frame
binding          (libdrm + libgbm + Wayland XML)
    │               │
    └───────┬───────┘
            │
            ▼
    HelloWayland (milestone)
            │
            ▼
    ThreeBody Phase 1
    (3-body comparison, HIP rendering)
```

---

## 8. Module Change Summary

| Module | Phase | Change |
|---|---|---|
| `MoyaTypes.fs` → `PilotTypes.fs` | 0 | Rename; add `EnumErrorCode` to `ErrorConvention`; add `[sources]` schema |
| `MoyaAnalyzer.fs` → `PilotAnalyzer.fs` | 0 | Rename |
| `MoyaSerializer.fs` → `PilotSerializer.fs` | 0 | Rename; parse new fields |
| `ActivePatterns.fs` | 1.1 | Add `OpaqueHandleTypedef` active pattern |
| `TypeMapper.fs` | 1.1 | Register opaque handle names in type resolution |
| `CodeAST.fs` | 1.2, 1.4 | `EnumType` gains `IsFlags`; `RecordType` gains optional `ExplicitLayout` |
| `CodeRenderer.fs` | 1.2, 1.4 | Render `[<Flags>]`; render `LayoutKind.Explicit` with `[<FieldOffset>]` |
| `FidelityCodeGenerator.fs` | 1.1, 1.2, 1.4 | Opaque handle emission; bitmask detection; field offset extraction |
| `ErrnoModuleGenerator.fs` → `ErrorModuleGenerator.fs` | 1.3 | Generalize: common pattern for errno macros and error enums; errno path remains a specialization |
| `WrapperTypes.fs` | 1.3 | Add `EnumReturnError of enumType: string` to `ReturnSemantic` |
| `WrapperPatternAnalyzer.fs` | 1.3 | Detect `EnumErrorCode` return pattern |
| `WrapperCodeGenerator.fs` | 1.3 | Generate `Result<T, ErrorStruct>` wrappers using `ErrorStruct.capture` |
| New: `WaylandProtocolParser.fs` | 1.5 | XParsec-based XML parser producing `Declaration` list |
| `BindingGenerator.fs` | 0, 1.5 | Use `PilotProject`; route XML protocols to new parser; merge declarations |
| New: `PilotDiscovery.fs` | 0 | Generalized source asset discovery: directory → classified file list with diagnostics |
| `Program.fs` (CLI) | 0 | `pilot` subcommand; accept `.pilot.toml`; `pilot discover` for asset discovery |

---

*Companion documents: "Farscape Phase 4: NPU Binding via DRM UAPI + XRT", "Farscape Phase 4C: PipeWire Audio Binding", "Farscape Phase 4D: ONNX Runtime Binding", and "Farscape Phase 5+: MFEM Algorithmic Ingestion"*

*SpeakEZ Technologies | Fidelity Framework*
