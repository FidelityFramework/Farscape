# Farscape Phase 4: NPU Binding via XRT/XDNA

**SpeakEZ Technologies | Fidelity Framework**
**February 2026**

---

## 1. Prerequisites

This document assumes Phases 0-3 of the Farscape Maturation Plan are complete:

- **Phase 0**: Pilot rename is done; `.pilot.toml` project files are the standard.
- **Phase 1**: Opaque handle types, bitmask enums, `EnumErrorCode` with error text, struct layout with BAREWire descriptors, and the Wayland protocol XML parser are all in place.
- **Phase 2**: ROCm/HIP binding generates and compiles.
- **Phase 3**: libdrm, libgbm, and Wayland bindings generate and compile. **HelloWayland** runs: a native Wayland toplevel surface with HIP-computed pixels, closable via standard Hyprland conventions.

At this point, Farscape has demonstrated end-to-end binding generation for five native libraries, Pilot has routed both C headers and XML protocols through a single project, and the DMA-BUF presentation pipeline works. Phase 4 extends the binding surface to the NPU.

---

## 2. NPU Context on Strix Halo

### 2.1 Hardware

The XDNA 2 NPU on Strix Halo is an array of AI Engine (AIE) tiles with dedicated memory tiles (4,096 KB of on-chip L2 across the tile array). It does not execute arbitrary compute kernels. It executes pre-compiled overlays: xclbin containers that package AIE tile ELF binaries, stream switch routing tables, and DMA buffer descriptors.

The NPU shares the same LPDDR5X as the CPU and GPU, but accesses it through DMA engines that reference physical addresses. XRT manages the virtual-to-physical translation through the `amdxdna` kernel driver.

### 2.2 Driver Stack

```
XRT API call (xrt::device, xrt::bo, xrt::kernel)
  → libxrt_coreutil.so (XRT runtime)
    → XDNA shim (libxrt_driver_xdna.so)
      → amdxdna kernel driver (/dev/accel/accel0)
        → NPU: overlay load → ctrlcode → DMA → AIE tile execution
```

The `amdxdna` driver is mainlined in kernel 6.14 for XDNA 2. It uses the `accel` subsystem (not DRM). The device appears at `/dev/accel/accel0`.

### 2.3 Programming Model

The NPU programming model differs fundamentally from GPU:

1. **Compile offline**: The workload is compiled to an xclbin container through MLIR-AIE. This happens before runtime; the xclbin is a build artifact.
2. **Load overlay**: XRT opens the device and loads the xclbin onto a partition of AIE tiles.
3. **Allocate buffers**: XRT buffer objects (`xrt_bo`) are allocated for input/output.
4. **Sync and execute**: Input data is synced to device-visible memory, execution is submitted, output is synced back.
5. **Read results**: The host reads results from the output buffer.

For Fidelity, the NPU compilation path runs through Composer:

```
Clef source → PSG → Alex → MLIR-AIE → AIE tile ELF → xclbin overlay
```

The host-side orchestration (buffer allocation, execution submission, synchronization) calls into XRT through Farscape-generated bindings. This document covers those host-side bindings, not the NPU kernel compilation path.

---

## 3. XRT C API Surface

XRT is a C++ library (`xrt::device`, `xrt::bo`, `xrt::kernel`, `xrt::run`). For Farscape binding, we target the C shim API that wraps the C++ classes. The C shim provides a flat function surface with opaque handle types, structurally identical to the HIP API that Phase 2 already handles.

### 3.1 Core Types

| C Type | Pattern | Farscape Mapping |
|---|---|---|
| `xrt_device` | Opaque handle (forward-declared struct pointer) | `[<Struct>] type xrt_device = { Handle: nativeint }` |
| `xrt_bo` | Opaque handle | `[<Struct>] type xrt_bo = { Handle: nativeint }` |
| `xrt_kernel` | Opaque handle | `[<Struct>] type xrt_kernel = { Handle: nativeint }` |
| `xrt_run` | Opaque handle | `[<Struct>] type xrt_run = { Handle: nativeint }` |
| `xrt_error_code` | Error enum (success = 0) | Standard enum + error text pipeline |
| `xrt_bo_flags` | Bitmask enum | `[<Flags>]` enum |

Every type here matches a pattern that Phase 1 already handles. The opaque handle detection, bitmask flags, and `EnumErrorCode` convention apply directly. No new code generator extensions are required for XRT binding generation itself.

### 3.2 API Groups

The XRT C API clusters into five functional groups:

**Device management**: Open/close device, query properties, load xclbin.

**Buffer objects**: Allocate, map, sync, import (DMA-BUF), export. This is the critical group for UMA pointer exchange. The `xrt_bo_import` function accepts a DMA-BUF fd and creates an XRT buffer object from it, enabling the three-processor sharing pattern.

**Kernel**: Open kernel by name from loaded xclbin, set arguments, get group ID for memory bank association.

**Run**: Start kernel execution, wait for completion, query state.

**Error**: Error code to string conversion (same pattern as HIP's `hipGetErrorString`).

---

## 4. Pilot Project

```toml
# xrt.pilot.toml

[library]
name = "xrt_coreutil"

[sources]
headers = [
    "/opt/xilinx/xrt/include/xrt.h",
    "/opt/xilinx/xrt/include/xrt/xrt_bo.h",
    "/opt/xilinx/xrt/include/xrt/xrt_device.h",
    "/opt/xilinx/xrt/include/xrt/xrt_kernel.h"
]
include_paths = ["/opt/xilinx/xrt/include"]
defines = ["XRT_API_SOURCE_C"]

[output]
mode = "fidelity"
directory = "./bindings/xrt"

[error_convention]
default = "enum_error_code"
error_type = "xrt_error_code"
success_value = "XRT_SUCCESS"
error_string_fn = "xrt_error_to_string"

[options]
opaque_handles = true
flags_enums = true

[[namespace]]
name = "Fidelity.XRT.Device"
library = "xrt_coreutil"
prefixes = ["xrt_device"]
functions = ["xrt_device_open", "xrt_device_close",
             "xrt_device_load_xclbin", "xrt_device_get_info"]

[[namespace]]
name = "Fidelity.XRT.BufferObject"
library = "xrt_coreutil"
prefixes = ["xrt_bo"]
functions = [
    "xrt_bo_alloc", "xrt_bo_free",
    "xrt_bo_map", "xrt_bo_sync",
    "xrt_bo_import", "xrt_bo_export",
    "xrt_bo_size", "xrt_bo_address"
]

[[namespace]]
name = "Fidelity.XRT.Kernel"
library = "xrt_coreutil"
prefixes = ["xrt_kernel"]
functions = ["xrt_kernel_open", "xrt_kernel_close",
             "xrt_kernel_group_id"]

[[namespace]]
name = "Fidelity.XRT.Run"
library = "xrt_coreutil"
prefixes = ["xrt_run"]
functions = [
    "xrt_run_start", "xrt_run_wait",
    "xrt_run_state", "xrt_run_set_arg"
]

[[namespace]]
name = "Fidelity.XRT.Error"
library = "xrt_coreutil"
functions = ["xrt_error_to_string"]
```

---

## 5. DMA-BUF Interop: The Three-Processor Sharing Pattern

The critical capability that Phase 4 unlocks is CPU+GPU+NPU buffer sharing through DMA-BUF. The allocation path:

```
1. HIP: hipMalloc → GPU-visible allocation in LPDDR5X
2. DRM: export HIP allocation as DMA-BUF fd (via hipExternalMemoryGetMappedBuffer or GBM path)
3. XRT: xrt_bo_import(device, fd, size) → NPU-visible buffer from same physical pages
4. CPU: the host pointer from hipHostMalloc or mmap of the DMA-BUF fd
```

After this sequence, a single physical memory region is accessible to CPU, GPU, and NPU without any copy. The BAREWire descriptor generated for the shared struct guarantees all three processors agree on field layout.

### 5.1 BAREWire Region for ThreeBody State

The body state struct shared across all three processors:

```fsharp
/// BAREWire descriptor for 64-byte aligned body state.
/// Identical layout read by CPU integration, GPU rendering, and NPU acceleration.
let bodyStateDescriptor = {
    Name = "BodyState"
    Size = 64u
    Alignment = 64u
    Fields = [
        { Name = "px";   Offset =  0u; Size = 8u; Type = F64 }  // position x
        { Name = "py";   Offset =  8u; Size = 8u; Type = F64 }  // position y
        { Name = "pz";   Offset = 16u; Size = 8u; Type = F64 }  // position z
        { Name = "mass"; Offset = 24u; Size = 8u; Type = F64 }  // mass
        { Name = "vx";   Offset = 32u; Size = 8u; Type = F64 }  // velocity x
        { Name = "vy";   Offset = 40u; Size = 8u; Type = F64 }  // velocity y
        { Name = "vz";   Offset = 48u; Size = 8u; Type = F64 }  // velocity z
        { Name = "pad";  Offset = 56u; Size = 8u; Type = U64 }  // pad to 64B cache line
    ]
}
```

The 64-byte stride maps consecutive bodies to different GPU cache lines. The same layout is read by the CPU symplectic integrator, the GPU force calculation kernel, and (at scale) the NPU DMA engines. No marshaling at any processor boundary. The BAREWire descriptor is the contract.

### 5.2 Buffer Lifecycle

The wrapper layer should encode the XRT buffer lifecycle as a state progression. This is not something Farscape can infer from the C header alone; it is domain knowledge that belongs in the wrapper layer or in manual annotations:

```
Allocated → Mapped → Written → Synced(ToDevice) → Executing → Synced(ToHost) → Read
```

The initial binding (Layer 1 + Layer 2 from Farscape) provides the raw functions. A higher-level wrapper (hand-written or generated from a lifecycle annotation in the `.pilot.toml`) could enforce the state progression at the type level. This is a future refinement, not a Phase 4 requirement.

---

## 6. Error Text Pipeline for XRT

The same pattern from Phase 1.3 applies. XRT provides `xrt_error_to_string` for runtime error descriptions. The compile-time path extracts `xrt_error_code` enum values from the header with their doc comments and generates the describe jump table:

```fsharp
[<Struct>]
type XrtError = {
    Code: xrt_error_code
    Description: string
}

module XrtError =
    let describe (code: xrt_error_code) : string =
        match code with
        | xrt_error_code.XRT_ERROR_INVALID_DEVICE -> "Invalid device handle"
        | xrt_error_code.XRT_ERROR_BO_NOT_MAPPED  -> "Buffer object not mapped"
        // ... generated from header comments
        | _ -> "Unknown XRT error"

    let describeRuntime (code: xrt_error_code) : string =
        let ptr = Bindings.xrt_error_to_string(code)
        NativeString.read ptr

    let capture (code: xrt_error_code) : XrtError =
        { Code = code; Description = describe code }
```

No new infrastructure. The `ErrorModuleGenerator` (refactored from `ErrnoModuleGenerator` in Phase 1.3) handles this directly.

---

## 7. Validation Criteria

- `xrt_device`, `xrt_bo`, `xrt_kernel`, `xrt_run` emit as distinct opaque handle wrapper structs
- `xrt_bo_flags` emits with `[<Flags>]`
- `xrt_error_code` emits as standard enum; `XrtError` struct generated with describe jump table
- All API functions return `Result<T, XrtError>` in Layer 2 wrappers
- `xrt_bo_import` is bindable and accepts a DMA-BUF fd from the GBM/DRM pipeline
- Generated code compiles

### 7.1 Integration Test: CPU+GPU+NPU Buffer

The first end-to-end validation after HelloWayland:

1. Allocate a body state buffer via HIP (`hipHostMalloc` with `hipHostMallocCoherent`)
2. Export the underlying DMA-BUF fd
3. Import into XRT via `xrt_bo_import`
4. Write body state from CPU
5. Launch HIP kernel that reads the same buffer
6. Submit XRT run that reads the same buffer via NPU DMA
7. Verify all three processors see consistent data

This test validates the BAREWire descriptor, the DMA-BUF interop path, and the coherency model. It does not require a functional NPU overlay (the XRT run can target a passthrough or identity overlay for testing).

---

## 8. What Phase 4 Does NOT Require

- No new code generator extensions (all patterns covered by Phase 1)
- No new parser capabilities (XRT C shim headers are standard C)
- No Pilot schema extensions (the existing `.pilot.toml` format handles everything)
- No NPU kernel compilation (that is a Composer/MLIR-AIE concern, not a Farscape concern)

Phase 4 is a validation phase for the binding infrastructure. If the code generator extensions from Phase 1 work correctly for HIP (Phase 2), they work for XRT. The novel element is the multi-library buffer sharing pattern, which exercises the BAREWire descriptor infrastructure in a cross-device context.

---

## 9. ThreeBody Integration

With Phase 4 complete, ThreeBody can progress to its NPU-accelerated configuration:

- **CPU**: Symplectic integration, lifecycle orchestration, event loop
- **GPU**: Pairwise force calculation (HIP compute kernel), pixel rendering to DMA-BUF surface
- **NPU**: Acceleration overlay for force accumulation at scale (256+ bodies)

The binding surface is: Fidelity.ROCm (Phase 2) + Fidelity.DRM + Fidelity.GBM + Fidelity.Wayland (Phase 3) + Fidelity.XRT (Phase 4). Five libraries, three processors, one shared memory region, zero copies.

---

## 10. Dependency on Phases 0-3

```
Phases 0-3 (HelloWayland)
    │
    ▼
Phase 4: XRT/XDNA Binding
    ├── 4.1 Generate binding from xrt.pilot.toml
    ├── 4.2 Validate opaque handles + error text
    ├── 4.3 DMA-BUF import integration test
    └── 4.4 CPU+GPU+NPU buffer coherency test
            │
            ▼
    ThreeBody Phase 3
    (NPU-accelerated, 256+ bodies)
```

---

*Companion documents: "Farscape Maturation Plan: Phases 0-3 Through HelloWayland" and "Farscape Phase 5+: MFEM Algorithmic Ingestion"*

*SpeakEZ Technologies | Fidelity Framework*
