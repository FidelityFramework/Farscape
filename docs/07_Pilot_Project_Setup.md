# Pilot Project Setup and Multi-File SDK Binding

Farscape uses `.pilot.toml` project files to drive scoped, multi-namespace binding generation from C/C++ SDK headers. A single pilot recipe produces a structured tree of `.clef` binding files: shared types, namespace-local types, Layer 1 extern declarations, Layer 2 idiomatic wrappers, and error modules. One command generates all of it.

This document covers the complete pilot TOML schema, the transitive header mechanism for multi-file SDKs, the output directory structure, namespace design, and integration with Fidelity.Platform fidproj packages.

## Why Pilot Exists

Real-world SDK headers are not self-contained. A library like HIP (ROCm GPU compute) declares its public API across multiple files:

```
hip_runtime_api.h              ← 462 functions, references types from:
  └── #include driver_types.h  ← hipMemcpyKind, hipPitchedPtr, hipExtent, hipArray_t
  └── #include hip_common.h    ← hipDeviceAttribute_t values
```

Farscape's clang-based parser extracts declarations only from the **target file** by default, to avoid pulling in thousands of system header types (`stdlib.h`, `stdint.h`, and so on) that would pollute the binding surface. Types defined in `driver_types.h`, which the API functions reference in their signatures, are silently dropped by this filter.

A large SDK like HIP also has hundreds of functions spanning device management, memory allocation, stream scheduling, event timing, module loading, and error handling. Generating all of these into a single flat file produces a monolithic binding that is difficult to navigate and maintain. Functions reference different subsets of the type definitions, and emitting all types into every namespace file causes significant duplication.

Pilot addresses both of these problems:

1. **Transitive headers** tell the parser which included files contain API-relevant types, without opening the floodgate to all system headers.
2. **Namespace-scoped generation** partitions functions and types into logical modules, with a type-dependency analysis that separates shared types from namespace-local types and eliminates duplication.

## Pilot TOML Schema

A `.pilot.toml` file is the complete recipe for binding generation. It declares the native library, its source files, the output location, error handling conventions, and the namespace decomposition.

### `[library]`: Source Declaration

```toml
[library]
name = "amdhip64"
headers = ["/opt/rocm/include/hip/hip_runtime_api.h"]
include_paths = ["/opt/rocm/include"]
defines = ["__HIP_PLATFORM_AMD__"]
transitive_headers = ["driver_types.h"]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Native library name for linking (e.g., `"libc"`, `"amdhip64"`, `"wayland-client"`). This becomes the first argument to `[<FidelityExtern>]` attributes. |
| `headers` | string array | yes | Absolute paths to C/C++ header files. Each is parsed independently via clang, then declarations are merged with deduplication. For single-header libraries, use a one-element array. |
| `include_paths` | string array | no | Additional `-I` paths passed to clang. Required when headers reference other SDK directories. |
| `defines` | string array | no | Preprocessor `-D` definitions passed to clang. Used for platform-conditional compilation (e.g., `__HIP_PLATFORM_AMD__`). |
| `xml_protocols` | string array | no | Paths to XML protocol definition files (e.g., Wayland `.xml`). Parsed by `WaylandProtocolParser`, merged with C header declarations. |
| `transitive_headers` | string array | no | **Filenames** (not paths) of headers transitively included by the primary headers, whose declarations should also be extracted. See [Transitive Headers](#transitive-headers-for-multi-file-sdk-apis) below. |
| `macro_prefixes` | string array | no | Name prefixes for macro constants to include (e.g., `["WL_", "WAYLAND_"]`). When set, only `#define` constants whose names start with a listed prefix are extracted. When omitted or empty, all user-defined macros pass through, which can pull in constants from system headers like `stdint.h` or `math.h`. Recommended for any library whose headers transitively include system headers with many `#define` constants. |

When multiple `headers` are listed, each is parsed as a separate clang invocation and the resulting declaration lists are merged via `DeclarationAlgebra.mergeDeclarations`, which deduplicates by declaration name and kind.

### `[output]`: Where Generated Files Land

```toml
[output]
mode = "fidelity"
directory = "../Bindings"
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `mode` | string | yes | Always `"fidelity"` for Farscape's Clef output. |
| `directory` | string | yes | Output directory, **relative to the pilot.toml file location**. Farscape resolves relative paths from the TOML file's parent directory, not from the working directory. |

### `[error_conventions]`: Error Handling Strategy

```toml
[error_conventions]
default = "enum_error_code"
error_type = "hipError_t"
success_value = "hipSuccess"
error_string_fn = "hipGetErrorString"
error_name_fn = "hipGetErrorName"
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `default` | string | yes | One of: `"errno"` (POSIX errno pattern), `"return_code"` (nonzero = error), `"enum_error_code"` (typed enum return), `"none"`. |
| `error_type` | string | for `enum_error_code` | Name of the error enum type (e.g., `"hipError_t"`). |
| `success_value` | string | for `enum_error_code` | Enum variant representing success (e.g., `"hipSuccess"`). |
| `error_string_fn` | string | no | Library function that converts error code to description string. |
| `error_name_fn` | string | no | Library function that converts error code to symbolic name. |

When `enum_error_code` is configured, Farscape generates:

- An error struct (e.g., `HipError`) with `Code` and `Description` fields
- A `describe` function that maps each enum value to a human-readable string
- A `capture` function that wraps a raw error code with its description
- Layer 2 wrappers that return `Result<T, HipError>` instead of raw error codes

For `errno`, Farscape generates a `CError` struct with `captureError` that reads the C errno global. See `docs/05_Wrapper_Generation.md` for the full error handling pipeline.

### Libraries Without Error Conventions

Not all native libraries report errors through a single, uniform mechanism. Some use object-scoped error queries (Wayland's `wl_display_get_error()`), out-parameters, or callback-based notification. These patterns are too varied for automated wrapper generation.

When no `[error_conventions]` section is present, or when `default = "none"` is set, Farscape generates Layer 2 wrappers as **direct passthrough** functions. There is no `Result<T, E>` wrapping, no null-pointer checks, and no error capture logic. The wrapper simply delegates to the Layer 1 extern declaration and returns whatever it returns.

This is intentional. A wrong error convention is worse than no convention, because it silently misinterprets return values. Direct passthrough is always correct, even if incomplete.

When `--wrappers` is enabled and no error convention is defined, the CLI emits an advisory:

```
Advisory: No [error_conventions] defined for 'wayland-client'. Layer 2 wrappers
use direct passthrough. If this library reports errors through a query function,
out-parameter, or other mechanism, add error handling in an Overlay module.
```

The advisory directs the developer to write error handling in an Overlay module, which is scaffolded once and then developer-owned. Overlay modules can call the library's error query functions, wrap results in `Result<T, E>`, and add domain-specific context that automated generation cannot infer.

For a survey of common error patterns and which ones Farscape handles automatically, see the error convention inventory in `docs/05_Wrapper_Generation.md`.

### `[options]`: Generation Options

```toml
[options]
opaque_handles = true
flags_enums = true
abi_critical_structs = ["drm_mode_create_dumb"]
generate_descriptors = true
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `opaque_handles` | bool | false | Detect `typedef struct X* X_t` patterns and generate distinct `[<Struct>]` wrapper types with `Handle: nativeint`, `zero()`, and `isNull()` instead of raw `nativeint`. |
| `flags_enums` | bool | false | Detect power-of-two enum value patterns and emit `[<System.Flags>]` attribute. |
| `abi_critical_structs` | string array | `[]` | Struct names requiring ABI-exact layout. Farscape runs `clang -fdump-record-layouts-simple` to extract byte offsets and generates `[<StructLayout(LayoutKind.Explicit, Size=N)>]` with per-field `[<FieldOffset(N)>]`. |
| `generate_descriptors` | bool | false | Generate BAREWire `StructDescriptor` values for ABI-critical structs (requires `abi_critical_structs` to be non-empty). |

### `[[namespace]]`: Namespace Decomposition

```toml
[[namespace]]
name = "Fidelity.ROCm.Device"
description = "Device management and properties"
library = "amdhip64"
prefixes = ["hipDevice", "hipGetDevice"]
functions = ["hipInit", "hipDriverGetVersion", "hipRuntimeGetVersion"]
```

Note the TOML array-of-tables syntax: `[[namespace]]` (double brackets), and singular `namespace` not plural.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Fully qualified Clef module name. Convention: `Fidelity.{Library}.{Subsystem}`. |
| `description` | string | yes | Human-readable description; appears as a comment in generated code. |
| `library` | string | yes | Native library name for `[<FidelityExtern>]`. Usually matches `[library].name` but can differ for multi-library packages. |
| `prefixes` | string array | no | Function name prefixes. A function matches if its name starts with any prefix. `prefixes = ["hipDevice"]` matches `hipDeviceGet`, `hipDeviceGetCount`, and so on. |
| `functions` | string array | no | Explicit function names. Use this for functions that do not follow a prefix pattern (e.g., `"hipInit"`). |
| `xml_interfaces` | string array | no | XML protocol interface names to include (e.g., `["wl_surface", "wl_compositor"]`). Functions and types from parsed XML protocols matching these interface names are included. |

A function is included in a namespace if it matches **any** of: a prefix, an explicit function name, or an XML interface. Non-function declarations (structs, enums, typedefs) are distributed automatically by the type-dependency analysis; they do not need to be listed in namespaces.

## Transitive Headers for Multi-File SDK APIs

### The Problem

Farscape's clang parser extracts declarations only from the target file specified in `headers`. When clang processes `hip_runtime_api.h`, it sees all types from `driver_types.h` through the `#include` resolution, but the parser's file-origin filter rejects them because they do not originate from the target file.

This filter is intentional. Without it, parsing `/usr/include/stdio.h` would pull in every type from `stdlib.h`, `stddef.h`, `bits/types.h`, and hundreds of other system headers. The binding surface would be full of implementation details that have no place in the generated API.

For SDK headers that deliberately split their public API across multiple files, this filter becomes too aggressive. The types in `driver_types.h` are part of the HIP public API: functions in `hip_runtime_api.h` reference them directly in their parameter and return types. Dropping them has several consequences:

- Function parameters that should be typed enums (e.g., `hipMemcpyKind`) become raw `nativeint`
- Struct types used in API calls (`hipPitchedPtr`, `hipExtent`) are never defined
- The type-dependency analysis classifies these as "local" to a namespace but cannot find declarations to emit
- Namespace `Types.clef` files that should contain these definitions come out empty

### The Solution

The `transitive_headers` field tells the parser: when parsing any header in `headers`, also accept declarations from these included files.

```toml
[library]
headers = ["/opt/rocm/include/hip/hip_runtime_api.h"]
transitive_headers = ["driver_types.h"]
```

During clang AST processing, the `walkAst` function in `CppParser.fs` tracks the source file of each declaration node. Normally it only accepts nodes whose source file matches the primary target (e.g., `hip_runtime_api.h`). With `transitive_headers`, it also accepts nodes from any file whose name matches an entry in the list.

The entries are **filenames**, not paths. The parser matches by `file.EndsWith(entry)`, so `"driver_types.h"` matches `/opt/rocm/include/hip/driver_types.h` regardless of the include path structure.

### Identifying Which Headers to Include

When generated bindings are missing expected types (function parameters showing as `nativeint` instead of named types, or a namespace's `Types.clef` containing only boilerplate), the types are likely defined in a transitively-included header.

To find where a missing type is defined:

```bash
# Search for the typedef or struct/enum definition
grep -rn 'typedef.*hipMemcpyKind\|enum hipMemcpyKind' /opt/rocm/include/hip/

# Check what the primary header includes
grep '#include' /opt/rocm/include/hip/hip_runtime_api.h
```

Add the filename of the header containing the missing types to `transitive_headers`. Only add headers that are part of the SDK's public API surface. System headers like `stdint.h` or `stdlib.h` should never appear in this list.

### How the Parser Implements This

The `walkAst` function processes clang's JSON AST output. For each declaration node, it reads the source file from the `loc` field:

```
Node from hip_runtime_api.h:  loc.file = "hip_runtime_api.h"
Node from driver_types.h:     loc.file = "driver_types.h"  (with loc.includedFrom)
Node from stdlib.h:           loc.file = "stdlib.h"         (with loc.includedFrom)
```

The acceptance check matches `currentFile` against all accepted filenames: the primary target plus any transitive headers. Only the primary target and explicitly listed transitive headers pass the filter. System headers remain excluded.

The `updateFileTracking` function prefers the direct `file` field (the actual source location) over the `includedFrom` field (which points to the file that issued the `#include`). This ensures `currentFile` reflects where the declaration actually lives, so the filename comparison works correctly even for deeply nested include chains.

## Output Directory Structure

For a multi-namespace pilot project, Farscape generates a structured directory tree. Each namespace gets its own subdirectory containing up to three files: local types, function declarations, and Layer 2 wrappers.

### Concrete Example: ROCm/HIP

Given the pilot TOML with 6 namespaces, Farscape generates:

```
Bindings/
├── Types.clef                  ← Shared types (referenced by 2+ namespaces, or orphans)
├── HipError.clef               ← Error struct + describe function
├── Device/
│   ├── Types.clef              ← Types local to Device (hipDeviceArch_t, hipDeviceProp_t, ...)
│   ├── Device.clef             ← [<FidelityExtern>] declarations (hipInit, hipGetDevice, ...)
│   └── DeviceWrappers.clef     ← Result<T, HipError> wrappers
├── Memory/
│   ├── Types.clef              ← Types local to Memory (hipMemcpyKind, hipPitchedPtr, ...)
│   ├── Memory.clef             ← [<FidelityExtern>] declarations (hipMalloc, hipMemcpy, ...)
│   └── MemoryWrappers.clef     ← Result<T, HipError> wrappers
├── Stream/
│   ├── Types.clef              ← Types local to Stream (hipGraph_t, hipGraphNode_t, ...)
│   ├── Stream.clef
│   └── StreamWrappers.clef
├── Event/
│   ├── Event.clef              ← No local types; no Types.clef generated
│   └── EventWrappers.clef
├── Module/
│   ├── Types.clef              ← Types local to Module (hipFunction_t, hipModule_t, dim3)
│   ├── Module.clef
│   └── ModuleWrappers.clef
└── Error/
    ├── Error.clef
    └── ErrorWrappers.clef
```

### Type Classification

Farscape runs a type-dependency analysis before generating any files. For each namespace, it traces from function signatures through parameter and return types to determine which declared types each namespace references.

Types fall into three categories:

- **Shared**: Referenced by 2 or more namespaces. Emitted in root `Types.clef`. Examples: `hipError_t`, `hipStream_t`, `hipEvent_t`.
- **Local**: Referenced by exactly 1 namespace. Emitted in that namespace's `Types.clef`. Examples: `hipMemcpyKind` (only Memory), `hipDeviceProp_t` (only Device).
- **Orphan**: Declared but not referenced by any namespace's functions. These go into shared `Types.clef`, since they may be needed by application code even if no API function directly references them.

This analysis uses `CTypeParser.tryParseCType` to extract base type names from C type strings, handling `const`, pointers, and arrays correctly. It intersects the referenced names against the set of declared type names to determine actual usage per namespace.

### Module Dependencies via `open` Directives

Each function file includes `open` directives to import the types it references:

```clef
module Fidelity.ROCm.Memory

open Fidelity.ROCm.Types         // shared types (hipError_t, hipStream_t, ...)
open Fidelity.ROCm.Memory.Types  // local types (hipMemcpyKind, hipPitchedPtr, ...)

[<FidelityExtern("amdhip64", "hipMalloc")>]
let hipMalloc (devPtr: nativeint) (size: nativeint) : hipError_t =
    Unchecked.defaultof<hipError_t>
```

The shared types module uses the namespace prefix derived from the project's namespace names. For namespaces `Fidelity.ROCm.Device`, `Fidelity.ROCm.Memory`, and so on, the common prefix is `Fidelity.ROCm`, so the shared types module is `Fidelity.ROCm.Types`.

## Namespace Design

### Choosing Prefixes vs. Explicit Functions

Prefixes work well for libraries with systematic naming:

```toml
# All hipDevice*, hipGetDevice* functions
prefixes = ["hipDevice", "hipGetDevice"]
```

Explicit function lists cover irregularities:

```toml
# hipInit doesn't match any Device prefix, but logically belongs here
functions = ["hipInit", "hipDriverGetVersion", "hipRuntimeGetVersion"]
```

A function matches a namespace if it matches any prefix OR appears in the explicit functions list. Both can be used together in the same namespace.

### Prefix Specificity

Prefixes match by `StartsWith`, so specificity matters. If one namespace has `prefixes = ["hipMem"]` and another has `prefixes = ["hipMemcpy"]`, a function like `hipMemcpyAsync` matches both. The function appears in whichever namespace it first matches; namespaces are processed in TOML declaration order.

To avoid ambiguity, use the most specific prefixes possible:

```toml
# Good: specific prefixes, no overlap
prefixes = ["hipMalloc", "hipFree", "hipMemcpy", "hipMemset"]

# Risky: "hipMem" would match hipMemcpy, hipMemset, hipMemPool, hipMemAdvise
prefixes = ["hipMem"]
```

### XML Interface Namespaces

For Wayland and similar XML-protocol libraries, `xml_interfaces` selects declarations by protocol interface name rather than C function prefix:

```toml
[[namespace]]
name = "Fidelity.Wayland.XdgShell"
description = "XDG shell window management"
library = "wayland-client"
xml_interfaces = ["xdg_wm_base", "xdg_surface", "xdg_toplevel"]
```

This includes functions whose names start with `xdg_wm_base_`, `xdg_surface_`, or `xdg_toplevel_`, plus non-function declarations whose names match the interface name patterns (both snake_case and PascalCase variants for delegate types).

## CLI Workflow

### Discovery (Optional)

For an unfamiliar SDK directory, Pilot Discovery scans the filesystem to identify headers, classify them (C vs. C++, umbrella vs. internal), find XML protocols, and suggest include paths:

```bash
farscape pilot discover --directory /opt/rocm/include/hip --library amdhip64
```

This produces a draft `.pilot.toml` with suggested headers and include paths. The developer then curates the namespace layout manually. Automated prefix analysis works well for libc-style libraries, but SDK-specific domain knowledge drives better namespace design for complex APIs like HIP or Wayland.

### Generation

```bash
# From any directory (the output path is relative to the TOML file, not CWD)
farscape project --project path/to/rocm-hip.pilot.toml --wrappers

# Verbose output shows type classification and per-file details
farscape project --project path/to/rocm-hip.pilot.toml --wrappers --verbose
```

The `--wrappers` flag (`-w`) enables Layer 2 wrapper generation. Without it, only Layer 1 extern declarations are generated.

### Re-generation

Layer 1 and Layer 2 files are regenerated from scratch on every run. There is no incremental generation. To regenerate after changing the pilot TOML:

```bash
# Clean old output first (Farscape does not auto-clean)
rm -rf Bindings/
farscape project --project pilot/rocm-hip.pilot.toml --wrappers
```

## fidproj Integration

Generated binding files must be listed in the Fidelity.Platform fidproj package that owns the substrate. The fidproj's `[build].sources` array references all generated `.clef` files:

```toml
[package]
name = "Fidelity.Platform.GPU.AMD.RDNA3_5.StrixHalo_iGPU"
version = "0.1.0"

[build]
sources = [
    "Platform.clef",
    # Generated HIP bindings (Farscape)
    "Bindings/Types.clef",
    "Bindings/HipError.clef",
    "Bindings/Device/Types.clef",
    "Bindings/Device/Device.clef",
    "Bindings/Device/DeviceWrappers.clef",
    "Bindings/Memory/Types.clef",
    "Bindings/Memory/Memory.clef",
    "Bindings/Memory/MemoryWrappers.clef",
    # ... remaining namespace files
]
output = "fidelity-platform-gpu-strixhalo"
output_kind = "library"
```

### Substrate Placement Rule

Where a binding's fidproj lives depends on what hardware substrate it serves:

| Library | Binding Surface | Substrate Location |
|---------|-----------------|-------------------|
| HIP runtime | GPU compute API | `GPU/AMD/RDNA3_5/StrixHalo_iGPU/` |
| libdrm | Kernel DRM interface | `CPU/Linux/x86_64/` |
| libgbm | Buffer management | `CPU/Linux/x86_64/` |
| Wayland | Display protocol | `CPU/Linux/x86_64/` |

HIP is under the GPU substrate because it is semantically a GPU API, even though the host-side code runs on the CPU. The GPU fidproj already declares `runtime_model = "rocm"`. The HIP binding is the host-side entry point to that runtime model.

### Pilot TOML Placement

Pilot TOML files are platform-bound recipes that reference absolute SDK paths for the target machine. They belong alongside the fidproj they serve:

```
GPU/AMD/RDNA3_5/StrixHalo_iGPU/
├── Fidelity.Platform.fidproj
├── Platform.clef
├── pilot/
│   └── rocm-hip.pilot.toml      ← recipe
└── Bindings/                      ← generated output
    ├── Types.clef
    ├── HipError.clef
    └── Device/ Memory/ Stream/ ...
```

The `[output].directory = "../Bindings"` path is relative to the pilot.toml location, so `pilot/rocm-hip.pilot.toml` outputs to `Bindings/` in the parent directory.

## Complete Example: ROCm/HIP Pilot TOML

This is the production pilot recipe for AMD's HIP runtime API binding, targeting the Strix Halo integrated RDNA 3.5 GPU:

```toml
[library]
name = "amdhip64"
headers = ["/opt/rocm/include/hip/hip_runtime_api.h"]
include_paths = ["/opt/rocm/include"]
defines = ["__HIP_PLATFORM_AMD__"]
transitive_headers = ["driver_types.h"]

[output]
mode = "fidelity"
directory = "../Bindings"

[error_conventions]
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
description = "Device management and properties"
library = "amdhip64"
prefixes = ["hipDevice", "hipGetDevice"]
functions = ["hipInit", "hipDriverGetVersion", "hipRuntimeGetVersion"]

[[namespace]]
name = "Fidelity.ROCm.Memory"
description = "Memory allocation and transfer"
library = "amdhip64"
prefixes = ["hipMalloc", "hipFree", "hipMemcpy", "hipMemset"]
functions = ["hipHostMalloc", "hipHostFree", "hipHostGetDevicePointer",
             "hipImportExternalMemory", "hipExternalMemoryGetMappedBuffer",
             "hipDestroyExternalMemory"]

[[namespace]]
name = "Fidelity.ROCm.Stream"
description = "Async command streams"
library = "amdhip64"
prefixes = ["hipStream"]

[[namespace]]
name = "Fidelity.ROCm.Event"
description = "Timing and synchronization events"
library = "amdhip64"
prefixes = ["hipEvent"]

[[namespace]]
name = "Fidelity.ROCm.Module"
description = "Kernel module loading and launch"
library = "amdhip64"
prefixes = ["hipModule"]
functions = ["hipLaunchKernel", "hipFuncGetAttributes"]

[[namespace]]
name = "Fidelity.ROCm.Error"
description = "Error query functions"
library = "amdhip64"
functions = ["hipGetErrorString", "hipGetErrorName",
             "hipGetLastError", "hipPeekAtLastError"]
```

This recipe parses 1,525 declarations from `hip_runtime_api.h` and its transitive dependency `driver_types.h`, classifies them across 6 namespaces, and generates 18 `.clef` files totaling approximately 5,500 lines of binding code across Layer 1, Layer 2, types, and the error module.

## Example: Wayland (XML Protocols, Macro Filtering, No Error Convention)

This example demonstrates three features not shown in the HIP recipe: XML protocol parsing, macro prefix filtering, and generation without an error convention.

```toml
[library]
name = "wayland-client"
headers = ["/usr/include/wayland-client-core.h"]
include_paths = ["/usr/include"]
transitive_headers = ["wayland-util.h"]
macro_prefixes = ["WL_", "WAYLAND_"]
xml_protocols = [
    "/usr/share/wayland/wayland.xml",
    "/usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml",
    "/usr/share/wayland-protocols/stable/linux-dmabuf/linux-dmabuf-v1.xml"
]

[output]
mode = "fidelity"
directory = "../Bindings"

[options]
opaque_handles = true

[[namespace]]
name = "Fidelity.Wayland.Core"
description = "Display connection, proxy management, and event dispatch"
library = "wayland-client"
prefixes = ["wl_display", "wl_proxy", "wl_event_queue"]
functions = ["wl_log_set_handler_client"]

[[namespace]]
name = "Fidelity.Wayland.Protocol"
description = "Core Wayland protocol interfaces"
library = "wayland-client"
xml_interfaces = [
    "wl_compositor", "wl_surface", "wl_buffer", "wl_registry",
    "wl_callback", "wl_shm", "wl_shm_pool", "wl_region",
    "wl_output", "wl_seat", "wl_pointer", "wl_keyboard", "wl_touch",
    "wl_data_offer", "wl_data_source", "wl_data_device",
    "wl_data_device_manager", "wl_subcompositor", "wl_subsurface"
]

[[namespace]]
name = "Fidelity.Wayland.XdgShell"
description = "XDG shell window management protocol"
library = "wayland-client"
xml_interfaces = [
    "xdg_wm_base", "xdg_surface", "xdg_toplevel",
    "xdg_popup", "xdg_positioner"
]

[[namespace]]
name = "Fidelity.Wayland.DmaBuf"
description = "Linux DMA-BUF buffer sharing protocol"
library = "wayland-client"
xml_interfaces = [
    "zwp_linux_dmabuf_v1", "zwp_linux_buffer_params_v1",
    "zwp_linux_dmabuf_feedback_v1"
]
```

Notable differences from HIP:

- **No `[error_conventions]`**: Wayland reports errors through `wl_display_get_error()`, an object-scoped query that does not fit the errno or enum-return patterns. Layer 2 wrappers are generated as direct passthrough, and the CLI emits an advisory directing the developer to add error handling in an Overlay module.
- **`macro_prefixes`**: Without this filter, `wayland-client-core.h` pulls in hundreds of `#define` constants from system headers (`FP_INFINITE`, `INT8_MIN`, and so on). The `["WL_", "WAYLAND_"]` filter restricts extraction to Wayland-specific constants.
- **`xml_protocols`**: Three XML protocol files are parsed by `WaylandProtocolParser` and merged with the C header declarations. Protocol namespaces use `xml_interfaces` to select by interface name rather than C function prefix.
- **`transitive_headers`**: `wayland-util.h` defines core types (`wl_interface`, `wl_argument`, `wl_list`, `wl_array`) that `wayland-client-core.h` references but does not define itself.

## Case Study: Overlay Module for Object-Scoped Errors

When a library reports errors through a mechanism that Farscape cannot automate, the developer writes an Overlay module. This section walks through the Wayland overlay as a concrete example of the pattern.

### The Problem

Wayland's `wl_display_dispatch()`, `wl_display_roundtrip()`, `wl_display_flush()`, and related functions return `-1` on error. The actual error code is not the return value itself. Instead, the caller must make a secondary call to `wl_display_get_error(display)`, which returns the errno stored on the display connection object.

This is structurally equivalent to POSIX errno. The difference is scope: errno is a thread-local global, while Wayland's error state lives on the display object. The secondary lookup is the same pattern, just with an explicit receiver.

Farscape's automated error conventions handle two patterns: global errno (`[error_conventions] default = "errno"`) and enum return codes (`default = "enum_error_code"`). Object-scoped queries do not fit either. The automated wrappers generate direct passthrough, and the CLI advisory tells the developer to handle errors in an Overlay.

### The Overlay

The overlay module lives alongside the generated files:

```
Bindings/Core/
├── Types.clef            ← generated (Farscape)
├── Core.clef             ← generated (Farscape) — Layer 1 externs
├── CoreWrappers.clef     ← generated (Farscape) — Layer 2 passthrough
└── CoreOverlay.clef      ← hand-written — Layer 2 error handling
```

The overlay opens the Layer 1 module and wraps the functions that can fail:

```clef
module Fidelity.Wayland.Core.Overlay

open Fidelity.Wayland.Core

type WaylandError = {
    Code: int32
    Description: string
}

let private captureError (display: nativeint) : WaylandError =
    let code = wl_display_get_error display
    { Code = code; Description = "wayland display error" }

/// Dispatch incoming events, blocking until events are available.
let dispatch (display: nativeint) : Result<int32, WaylandError> =
    let result = wl_display_dispatch display
    if result >= 0l then Ok result
    else Error (captureError display)

/// Block until all pending requests are processed by the server.
let roundtrip (display: nativeint) : Result<int32, WaylandError> =
    let result = wl_display_roundtrip display
    if result >= 0l then Ok result
    else Error (captureError display)

/// Flush buffered requests to the server.
let flush (display: nativeint) : Result<int32, WaylandError> =
    let result = wl_display_flush display
    if result >= 0l then Ok result
    else Error (captureError display)
```

### Design Decisions

**Overlay calls Layer 1, not Layer 2.** The generated Layer 2 wrappers are direct passthrough under NoErrors, so calling them is equivalent to calling Layer 1. The overlay opens `Fidelity.Wayland.Core` (Layer 1) directly. Application code that needs error handling imports the Overlay module. Code that does not need error handling can use either Layer 1 or the passthrough Layer 2.

**Connect functions return `ValueOption`, not `Result`.** `wl_display_connect` and `wl_display_connect_to_fd` return NULL on failure, but there is no display object to query for an errno. The failure mode is simply "connection failed." `ValueNone` communicates this without inventing an error value.

**captureError is private.** The error query is an implementation detail of the wrapper. Callers work with `Result<T, WaylandError>` and do not need to know that a secondary lookup occurred. This is the same hiding that the errno and enum-error conventions apply in the generated wrappers.

**Description is a placeholder.** The `WaylandError.Description` field currently contains a static string. When `Fidelity.Errno` is available, this can be replaced with `Errno.describe code` to produce human-readable descriptions like "Connection reset by peer" or "Broken pipe." The struct shape does not change.

### When to Write an Overlay

The CLI advisory is the signal. When Farscape generates wrappers without an `[error_conventions]` section, it prints:

```
Advisory: No [error_conventions] defined for 'wayland-client'. Layer 2 wrappers
use direct passthrough. If this library reports errors through a query function,
out-parameter, or other mechanism, add error handling in an Overlay module.
```

Not every library without error conventions needs an overlay. Some libraries genuinely do not report errors (e.g., `libm` math functions). The advisory is informational, not prescriptive.

Libraries that do need overlays share a common trait: a secondary lookup is required to get the actual error information after an operation fails. Examples beyond Wayland include OpenGL (`glGetError()`), Vulkan instance/device creation (error codes returned from the creation call itself, but validation layer errors come through a callback), and ALSA (`snd_strerror()`). Each of these has a different enough shape that automated generation would produce wrong results.
