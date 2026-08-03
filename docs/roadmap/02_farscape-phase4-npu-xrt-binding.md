# Farscape Phase 4: NPU Binding via DRM UAPI + XRT

**SpeakEZ Technologies | Fidelity Framework**
**Updated 2026-03-10** (supersedes February 2026 draft)

> **Perishable information**: Hardware specs, driver versions, header paths, and API surfaces in this document were verified on a specific Strix Halo system on 2026-03-10. The XDNA driver stack is under active development by AMD. Version numbers, ioctl interfaces, and XRT compatibility will change. Re-verify before beginning implementation.

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

### 2.1 Hardware (Verified 2026-03-10)

**Target system**: ASUS ROG Flow Z13 (GZ302EA), AMD Ryzen AI MAX+ 395 (Strix Halo APU).

The XDNA 2 (AIE2P) NPU is an array of AI Engine tiles with dedicated memory tiles. It does not execute arbitrary compute kernels. It executes pre-compiled overlays: xclbin containers that package AIE tile ELF binaries, stream switch routing tables, and DMA buffer descriptors.

**Verified hardware parameters** (from DRM ioctl `AMDXDNA_GET_INFO` on 2026-03-10):

| Parameter | Value | Source |
|---|---|---|
| AIE version | 1.1 (AIE2P) | `DRM_AMDXDNA_QUERY_AIE_VERSION` |
| Firmware | 1.1.2.65 | `DRM_AMDXDNA_QUERY_FIRMWARE_VERSION` |
| Tile grid | 8 columns × 6 rows | `DRM_AMDXDNA_QUERY_AIE_METADATA` |
| Compute tiles | 32 | `aie_metadata.core.row_count × col_count` |
| Memory tiles | 8 | `aie_metadata.mem.row_count × col_count` |
| Shim tiles | 8 | `aie_metadata.shim.row_count × col_count` |
| L2 on-chip | 4,096 KB across tile array | Per-tile memory × tile count |
| Peak performance | 58 TOPS (INT8) | AMD specification |
| H-clock | 1,800 MHz | `DRM_AMDXDNA_QUERY_CLOCK_METADATA` |

The NPU shares the same LPDDR5X (128 GB) as the CPU and GPU, but accesses it through DMA engines that reference physical addresses. The kernel driver manages virtual-to-physical translation.

### 2.2 Driver Stack (Verified 2026-03-10)

```
Application
  ├── Path A: DRM ioctls (working NOW)
  │     └── open("/dev/accel/accel0") → ioctl(DRM_IOCTL_AMDXDNA_*) → NPU
  │
  └── Path B: XRT C API (BLOCKED — version skew)
        └── xrt::device → libxrt_coreutil.so → libxrt_driver_xdna.so
              → mmap(len=64MB, off=0x100000000) → EAGAIN from DKMS 0.6.0
```

**Installed driver components** (2026-03-10):

| Component | Version | Package | Status |
|---|---|---|---|
| `amdxdna` kernel module | 7.0 (DKMS) | `amdxdna-dkms` (AUR, superm1) | **Working** |
| linux-firmware NPU FW | 20260221-1 | `linux-firmware` | **Working** |
| XRT userspace | 2.21.75 | `xrt` (AUR) | **Broken** (mmap skew) |
| XRT XDNA plugin | 2.21.75 | `xrt-plugin-amdxdna` (AUR) | **Broken** (depends on XRT) |
| Kernel | 6.19.6-arch1-1 | `linux` | **Working** (accel subsystem) |
| Device node | `/dev/accel/accel0` | udev | **Present** |

**Critical issue**: XRT 2.21 userspace attempts a 64 MB `mmap` at offset `0x100000000` that the DKMS 0.6.0 kernel module rejects with `EAGAIN`. This means `xrt::device` construction fails and all XRT C/C++ API functions are non-functional. Direct DRM ioctls work perfectly.

**Root cause**: The `amdxdna-dkms` 7.0 package (from superm1/AMD's `drm-misc-fixes` branch) fixes the SMU power-on bug in the in-tree kernel 6.19 driver but ships a DKMS module version (0.6.0) that does not match the mmap expectations of XRT 2.21. The in-tree kernel 6.14+ `amdxdna` driver and XRT 2.21 are designed to work together; the DKMS backport diverges.

**Resolution paths** (in priority order):

1. **Phase 4A (DRM UAPI direct)**: Bind the 10 DRM ioctls directly via Farscape. No XRT dependency. Works today.
2. **Build matched XRT from source**: Clone `amd/xdna-driver`, build XRT against the DKMS module's ABI. Unblocks XRT C API.
3. **Wait for kernel convergence**: As the in-tree `amdxdna` driver matures in kernel 6.14+, the DKMS backport becomes unnecessary and XRT 2.21+ will work directly.

### 2.3 Programming Model

The NPU programming model differs fundamentally from GPU:

1. **Compile offline**: The workload is compiled to an xclbin container through MLIR-AIE. This happens before runtime; the xclbin is a build artifact.
2. **Load overlay**: The host opens the device and loads the xclbin onto a partition of AIE tiles (creates a hardware context).
3. **Allocate buffers**: Buffer objects are allocated for input/output (DRM BO or XRT BO).
4. **Sync and execute**: Input data is synced to device-visible memory, execution is submitted, output is synced back.
5. **Read results**: The host reads results from the output buffer.

For Fidelity, the NPU compilation path runs through Composer:

```
Clef source → PSG → Alex → MLIR-AIE → AIE tile ELF → xclbin overlay
```

The host-side orchestration (buffer allocation, execution submission, synchronization) calls into either the DRM UAPI or XRT through Farscape-generated bindings. This document covers those host-side bindings, not the NPU kernel compilation path.

---

## 3. Phase 4A: DRM UAPI Direct Binding (Priority Path)

### 3.1 Rationale

The `amdxdna` DRM UAPI is the kernel-stable interface. It is:
- **Working today** on the target system (verified 2026-03-10)
- **Independent of XRT** version skew
- **Structurally identical** to the libdrm ioctl pattern that Phase 3 already handles
- **A forcing function for Farscape**: The UAPI header changes with new hardware revisions. Regenerating bindings from `amdxdna_accel.h` validates Farscape's "fire and forget" binding regeneration model — keeping the Layer 3 → Layer 2 surface consistent release over release.

### 3.2 UAPI Header (Verified 2026-03-10)

**Location**: `/usr/include/drm/amdxdna_accel.h` (706 lines)
**Installed by**: `amdxdna-dkms` 7.0 package (copies to `/usr/src/amdxdna-7.0/include/uapi/drm/`)

### 3.3 API Surface

10 DRM ioctls, defined via `DRM_IOCTL_DEF_DRV` macros:

| Ioctl | Struct | Purpose |
|---|---|---|
| `DRM_IOCTL_AMDXDNA_CREATE_HWCTX` | `amdxdna_drm_create_hwctx` | Create hardware context (load xclbin partition) |
| `DRM_IOCTL_AMDXDNA_DESTROY_HWCTX` | `amdxdna_drm_destroy_hwctx` | Destroy hardware context |
| `DRM_IOCTL_AMDXDNA_CONFIG_HWCTX` | `amdxdna_drm_config_hwctx` | Configure context (power mode, etc.) |
| `DRM_IOCTL_AMDXDNA_CREATE_BO` | `amdxdna_drm_create_bo` | Create buffer object |
| `DRM_IOCTL_AMDXDNA_GET_BO_INFO` | `amdxdna_drm_get_bo_info` | Query buffer object info |
| `DRM_IOCTL_AMDXDNA_SYNC_BO` | `amdxdna_drm_sync_bo` | Sync buffer (CPU↔device) |
| `DRM_IOCTL_AMDXDNA_EXEC_CMD` | `amdxdna_drm_exec_cmd` | Submit execution command |
| `DRM_IOCTL_AMDXDNA_GET_INFO` | `amdxdna_drm_get_info` | Query device info (AIE metadata, clocks, FW, sensors) |
| `DRM_IOCTL_AMDXDNA_SET_STATE` | `amdxdna_drm_set_state` | Set device state (power mode) |
| `DRM_IOCTL_AMDXDNA_GET_ARRAY` | `amdxdna_drm_get_array` | Batch get-info for arrays |

### 3.4 Core Types

| C Type | Pattern | Farscape Mapping |
|---|---|---|
| `enum amdxdna_bo_type` | Standard enum (SHMEM=0, DEV_HEAP=1, DEV=2, CMD=3) | Standard enum |
| `enum amdxdna_cmd_type` | Standard enum | Standard enum |
| `enum amdxdna_power_mode_type` | Standard enum (DEFAULT, LOW, MEDIUM, HIGH, TURBO) | Standard enum |
| `enum amdxdna_drm_get_param` | Query parameter enum (22 values) | Standard enum |
| `enum amdxdna_drm_set_param` | Set parameter enum | Standard enum |
| `struct amdxdna_drm_create_hwctx` | ABI-critical ioctl struct | `[<Struct; StructLayout(Explicit)>]` with BAREWire descriptor |
| `struct amdxdna_drm_create_bo` | ABI-critical ioctl struct | Same |
| `struct amdxdna_drm_query_aie_metadata` | Nested struct with sub-structs | Same |

All ioctl structs are ABI-critical — they must match the kernel layout byte-for-byte. This is the same pattern as Phase 3's libdrm structs (`drm_prime_handle`, etc.), exercising the `abi_critical_structs` infrastructure from Phase 1.4.

### 3.5 Query Parameter Surface

The `DRM_IOCTL_AMDXDNA_GET_INFO` ioctl is polymorphic — the `param` field selects the query type, and `buffer` receives the result. 22 query parameters defined:

| Param | Result Type | Purpose |
|---|---|---|
| `DRM_AMDXDNA_QUERY_AIE_VERSION` | `amdxdna_drm_query_aie_version` | AIE version (major.minor) |
| `DRM_AMDXDNA_QUERY_AIE_METADATA` | `amdxdna_drm_query_aie_metadata` | Tile grid layout (rows, cols, types) |
| `DRM_AMDXDNA_QUERY_AIE_STATUS` | status blob | AIE tile status |
| `DRM_AMDXDNA_QUERY_CLOCK_METADATA` | `amdxdna_drm_query_clock` | Clock name + frequency |
| `DRM_AMDXDNA_QUERY_SENSORS` | `amdxdna_drm_query_sensor` | Power/thermal sensors |
| `DRM_AMDXDNA_QUERY_HW_CONTEXTS` | context list | Active hardware contexts |
| `DRM_AMDXDNA_QUERY_FIRMWARE_VERSION` | `amdxdna_drm_query_firmware_version` | FW version (major.minor.patch.build) |
| `DRM_AMDXDNA_QUERY_RESOURCE_INFO` | `amdxdna_drm_query_resource_info` | Available compute resources |
| `DRM_AMDXDNA_QUERY_TELEMETRY` | telemetry blob | Performance telemetry |

### 3.6 Pilot Project

```toml
# amdxdna.pilot.toml
# Verified 2026-03-10 on Strix Halo (Ryzen AI MAX+ 395)
# amdxdna-dkms 7.0 / kernel 6.19.6-arch1-1

[library]
name = "drm"  # ioctl interface, linked via libdrm for DRM_IOCTL macros

[sources]
headers = ["/usr/include/drm/amdxdna_accel.h"]
include_paths = ["/usr/include", "/usr/include/libdrm"]

[output]
mode = "fidelity"
directory = "./bindings/amdxdna"

[error_convention]
default = "errno"  # ioctl returns -1 + errno

[options]
abi_critical_structs = [
    "amdxdna_drm_create_hwctx",
    "amdxdna_drm_destroy_hwctx",
    "amdxdna_drm_config_hwctx",
    "amdxdna_drm_create_bo",
    "amdxdna_drm_get_bo_info",
    "amdxdna_drm_sync_bo",
    "amdxdna_drm_exec_cmd",
    "amdxdna_drm_get_info",
    "amdxdna_drm_set_state",
    "amdxdna_drm_get_array",
    "amdxdna_drm_query_aie_version",
    "amdxdna_drm_query_aie_metadata",
    "amdxdna_drm_query_aie_tile_info",
    "amdxdna_drm_query_clock",
    "amdxdna_drm_query_sensor",
    "amdxdna_drm_query_firmware_version",
    "amdxdna_drm_query_resource_info"
]

[[namespace]]
name = "Fidelity.XDNA.Context"
library = "drm"
prefixes = ["amdxdna_drm_create_hwctx", "amdxdna_drm_destroy_hwctx",
            "amdxdna_drm_config_hwctx"]

[[namespace]]
name = "Fidelity.XDNA.BufferObject"
library = "drm"
prefixes = ["amdxdna_drm_create_bo", "amdxdna_drm_get_bo_info",
            "amdxdna_drm_sync_bo"]

[[namespace]]
name = "Fidelity.XDNA.Execution"
library = "drm"
prefixes = ["amdxdna_drm_exec_cmd"]

[[namespace]]
name = "Fidelity.XDNA.Query"
library = "drm"
prefixes = ["amdxdna_drm_get_info", "amdxdna_drm_get_array",
            "amdxdna_drm_query"]

[[namespace]]
name = "Fidelity.XDNA.Config"
library = "drm"
prefixes = ["amdxdna_drm_set_state", "amdxdna_power_mode"]
```

### 3.7 Layer 3 Wrapper Design

The DRM UAPI is an ioctl interface, not a function library. Layer 2 emits the struct types and ioctl constants. Layer 3 (hand-written or annotated) provides the typed wrapper:

```fsharp
module Fidelity.XDNA.Api =
    /// Open the NPU device. Returns a file descriptor.
    let openDevice () : Result<int, CError> =
        let fd = Bindings.drmOpen "/dev/accel/accel0"
        if fd < 0 then Error (CError.capture ())
        else Ok fd

    /// Query AIE metadata (tile grid layout).
    let queryAieMetadata (fd: int) : Result<amdxdna_drm_query_aie_metadata, CError> =
        let mutable meta = NativeDefault.zeroed<amdxdna_drm_query_aie_metadata> ()
        let mutable info = {
            param = DRM_AMDXDNA_QUERY_AIE_METADATA
            buffer_size = uint32 (sizeof<amdxdna_drm_query_aie_metadata>)
            buffer = NativeInterop.NativePtr.toNativeInt &&meta |> uint64
        }
        match ioctl fd DRM_IOCTL_AMDXDNA_GET_INFO &&info with
        | 0 -> Ok meta
        | _ -> Error (CError.capture ())

    /// Create a hardware context (load xclbin overlay onto AIE tiles).
    let createHwContext (fd: int) (xclbinPath: string) : Result<uint32, CError> =
        // ... map xclbin, populate amdxdna_drm_create_hwctx, ioctl
```

### 3.8 Farscape Extension: Ioctl Binding Pattern

Phase 4A may require a small Farscape extension: the ability to detect ioctl struct patterns in a UAPI header and generate the `ioctl` call wrappers automatically. The pattern:

1. Parse `DRM_IOCTL_DEF_DRV(NAME, func, flags)` macros or `#define DRM_IOCTL_AMDXDNA_*` constants
2. Associate each ioctl constant with its argument struct (by naming convention or explicit annotation)
3. Generate a typed wrapper that allocates the struct, fills fields, calls `ioctl`, and returns `Result`

This is a **new Farscape capability** not covered by Phase 1. It applies broadly: every DRM subsystem (amdgpu, i915, nouveau, amdxdna) uses the same ioctl pattern. Building this for `amdxdna` validates it for the entire DRM ecosystem.

If the extension is deferred, the Layer 3 wrappers are hand-written (as shown above). The Layer 1 + Layer 2 types and constants still generate automatically.

---

## 4. Phase 4B: XRT C API Binding (When Unblocked)

### 4.1 Current Status (2026-03-10)

**Blocked** by XRT 2.21 / DKMS 0.6.0 mmap version skew. The `xrt*` C API surface in `libxrt_coreutil.so` is non-functional.

### 4.2 Unblock Path

Build matched XRT from AMD's `xdna-driver` repository against the installed DKMS module ABI:

```bash
git clone https://github.com/amd/xdna-driver.git
cd xdna-driver
# Build XRT with DKMS-compatible shim
cmake -B build -DXRT_DKMS_COMPAT=ON  # exact flags TBD
cmake --build build
```

Once XRT and the kernel module agree on the mmap protocol, the C API becomes functional and Phase 4B proceeds.

### 4.3 XRT C API Surface

XRT is a C++ library (`xrt::device`, `xrt::bo`, `xrt::kernel`, `xrt::run`). For Farscape binding, we target the C shim API that wraps the C++ classes. The C shim provides a flat function surface with opaque handle types, structurally identical to the HIP API that Phase 2 already handles.

**Header locations** (verified 2026-03-10):

| Header | Path | Notes |
|---|---|---|
| `xrt.h` | `/usr/include/xrt/xrt.h` | Top-level umbrella (C++) |
| `xrt_bo.h` | `/usr/include/xrt/xrt_bo.h` | Buffer objects |
| `xrt_device.h` | `/usr/include/xrt/xrt_device.h` | Device management |
| `xrt_kernel.h` | `/usr/include/xrt/xrt_kernel.h` | Kernel/run management |

> **Note**: Previous draft referenced `/opt/xilinx/xrt/include/`. On Arch Linux with the `xrt` AUR package, headers install to `/usr/include/xrt/`. Verify paths on your system.

#### Core Types

| C Type | Pattern | Farscape Mapping |
|---|---|---|
| `xrt_device` | Opaque handle (forward-declared struct pointer) | `[<Struct>] type xrt_device = { Handle: nativeint }` |
| `xrt_bo` | Opaque handle | `[<Struct>] type xrt_bo = { Handle: nativeint }` |
| `xrt_kernel` | Opaque handle | `[<Struct>] type xrt_kernel = { Handle: nativeint }` |
| `xrt_run` | Opaque handle | `[<Struct>] type xrt_run = { Handle: nativeint }` |
| `xrt_error_code` | Error enum (success = 0) | Standard enum + error text pipeline |
| `xrt_bo_flags` | Bitmask enum | `[<Flags>]` enum |

Every type here matches a pattern that Phase 1 already handles. No new code generator extensions are required for XRT binding generation itself.

### 4.4 Pilot Project

```toml
# xrt.pilot.toml
# NOTE: Header paths verified 2026-03-10 on Arch Linux (xrt 2.21.75 AUR package)
# Paths may differ on Ubuntu/RHEL where XRT installs to /opt/xilinx/xrt/

[library]
name = "xrt_coreutil"

[sources]
headers = [
    "/usr/include/xrt/xrt.h",
    "/usr/include/xrt/xrt_bo.h",
    "/usr/include/xrt/xrt_device.h",
    "/usr/include/xrt/xrt_kernel.h"
]
include_paths = ["/usr/include/xrt"]
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

### 4.5 Error Text Pipeline for XRT

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

## 5. DMA-BUF Interop: The Three-Processor Sharing Pattern

The critical capability that Phase 4 unlocks is CPU+GPU+NPU buffer sharing through DMA-BUF. On Strix Halo's UMA architecture (128 GB shared LPDDR5X), all three processors access the same physical memory. The allocation path:

```
1. HIP: hipMalloc → GPU-visible allocation in LPDDR5X
2. DRM: export HIP allocation as DMA-BUF fd (via hipExternalMemoryGetMappedBuffer or GBM path)
3. XDNA: DRM_IOCTL_AMDXDNA_CREATE_BO (type=DEV) or xrt_bo_import → NPU-visible buffer
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

The wrapper layer should encode the buffer lifecycle as a state progression. This is not something Farscape can infer from the C header alone; it is domain knowledge that belongs in the wrapper layer or in manual annotations:

```
Allocated → Mapped → Written → Synced(ToDevice) → Executing → Synced(ToHost) → Read
```

The initial binding (Layer 1 + Layer 2 from Farscape) provides the raw functions. A higher-level wrapper (hand-written or generated from a lifecycle annotation in the `.pilot.toml`) could enforce the state progression at the type level. This is a future refinement, not a Phase 4 requirement.

---

## 6. Validation Criteria

### 6.1 Phase 4A (DRM UAPI)

- All 10 ioctl argument structs emit with `[<StructLayout(Explicit)>]` and BAREWire descriptors
- All enums (`amdxdna_bo_type`, `amdxdna_cmd_type`, `amdxdna_power_mode_type`, `amdxdna_drm_get_param`) emit correctly
- Nested query structs (`amdxdna_drm_query_aie_metadata` with sub-structs) emit with correct offsets
- Layer 3 wrappers can open `/dev/accel/accel0`, query AIE metadata, and return verified results
- Generated code compiles

### 6.2 Phase 4B (XRT)

- `xrt_device`, `xrt_bo`, `xrt_kernel`, `xrt_run` emit as distinct opaque handle wrapper structs
- `xrt_bo_flags` emits with `[<Flags>]`
- `xrt_error_code` emits as standard enum; `XrtError` struct generated with describe jump table
- All API functions return `Result<T, XrtError>` in Layer 2 wrappers
- `xrt_bo_import` is bindable and accepts a DMA-BUF fd from the GBM/DRM pipeline
- Generated code compiles

### 6.3 Integration Test: CPU+GPU+NPU Buffer

The first end-to-end validation after HelloWayland:

1. Allocate a body state buffer via HIP (`hipHostMalloc` with `hipHostMallocCoherent`)
2. Export the underlying DMA-BUF fd
3. Import into NPU via `DRM_IOCTL_AMDXDNA_CREATE_BO` (Phase 4A) or `xrt_bo_import` (Phase 4B)
4. Write body state from CPU
5. Launch HIP kernel that reads the same buffer
6. Submit NPU execution that reads the same buffer via DMA
7. Verify all three processors see consistent data

This test validates the BAREWire descriptor, the DMA-BUF interop path, and the coherency model. It does not require a functional NPU overlay (the execution can target a passthrough or identity overlay for testing).

---

## 7. What Phase 4 Does NOT Require

- No new parser capabilities for Phase 4B (XRT C shim headers are standard C)
- No Pilot schema extensions (the existing `.pilot.toml` format handles everything)
- No NPU kernel compilation (that is a Composer/MLIR-AIE concern, not a Farscape concern)

Phase 4A may require a modest Farscape extension for ioctl binding patterns (Section 3.8), which generalizes to all DRM subsystems.

Phase 4 is a validation phase for the binding infrastructure. If the code generator extensions from Phase 1 work correctly for HIP (Phase 2), they work for XRT. The novel elements are the DRM UAPI ioctl pattern and the multi-library buffer sharing pattern, which exercises the BAREWire descriptor infrastructure in a cross-device context.

---

## 8. ThreeBody Integration

With Phase 4 complete, ThreeBody can progress to its NPU-accelerated configuration:

- **CPU**: Symplectic integration, lifecycle orchestration, event loop
- **GPU**: Pairwise force calculation (HIP compute kernel), pixel rendering to DMA-BUF surface
- **NPU**: Acceleration overlay for force accumulation at scale (256+ bodies)

The binding surface is: Fidelity.ROCm (Phase 2) + Fidelity.DRM + Fidelity.GBM + Fidelity.Wayland (Phase 3) + Fidelity.XDNA + Fidelity.XRT (Phase 4). Six libraries, three processors, one shared memory region, zero copies.

---

## 9. Dependency Graph

```
Phases 0-3 (HelloWayland)
    │
    ▼
Phase 4A: DRM UAPI Direct Binding (READY NOW)
    ├── 4A.1 Generate binding from amdxdna.pilot.toml
    ├── 4A.2 Validate ioctl structs + BAREWire descriptors
    ├── 4A.3 Layer 3 wrappers: open device, query metadata
    └── 4A.4 DMA-BUF import integration test (HIP→XDNA)
            │
            ├── Phase 4B: XRT C API Binding (when unblocked)
            │     ├── 4B.1 Build matched XRT from source
            │     ├── 4B.2 Generate binding from xrt.pilot.toml
            │     ├── 4B.3 Validate opaque handles + error text
            │     └── 4B.4 xrt_bo_import integration test
            │
            ▼
    ThreeBody Phase 3
    (NPU-accelerated, 256+ bodies)
```

---

## 10. Companion Phases

Phase 4 is part of a cluster of binding phases that extend Farscape's reach for the Strix Halo audio agent:

| Phase | Target | Document |
|---|---|---|
| 4A | amdxdna DRM UAPI | This document |
| 4B | XRT C API | This document |
| 4C | PipeWire audio I/O | `05_farscape-phase4c-pipewire-audio.md` |
| 4D | ONNX Runtime C API | `06_farscape-phase4d-onnxruntime.md` |
| 5 | MFEM algorithmic ingestion | `03_farscape-phase5-mfem-ingestion.md` |

---

## 11. Architectural Vision: Supervised Spatial Scheduling

This section captures the north star that Phases 4A-4D collectively build toward. It is the arc that connects binding generation to a production system.

### 11.1 The Premise

Strix Halo puts three processors on one die sharing one memory. The NPU is not an accelerator you "offload to" — it is a peer substrate alongside CPU and GPU. The binding infrastructure should reflect this: **uniform dispatch, spatial partitioning, supervisor-driven hydration**.

### 11.2 Uniform Dispatch

Every inference workload — STT, TTS, routing, expert models — is an ONNX model that can run on any substrate. The same `SessionOptionsAppendExecutionProvider` call selects CPU (DNNL), GPU (MIGraphX), or NPU (VitisAI). The same `OrtApi.Run` executes. The Clef host orchestrator uses `Prefer`/`Require` affinity hints, not hard assignments. No model is substrate-locked; placement is a scheduling decision.

### 11.3 Spatial Partitioning on the NPU

The XDNA 2 NPU is an 8-column × 6-row tile array (58 TOPS). Each hardware context (`CREATE_HWCTX`) claims a column partition. Multiple contexts coexist simultaneously on disjoint partitions:

```
┌────────┬────────┬────────┬────────┬────────┬────────┬────────┬────────┐
│ Col 0  │ Col 1  │ Col 2  │ Col 3  │ Col 4  │ Col 5  │ Col 6  │ Col 7  │
│        │        │        │        │        │        │        │        │
│◄─── hwctx_0: Whisper STT ──►│◄── hwctx_1: TTS ──►│◄── available ───►│
│    ~15 TOPS, REALTIME        │   ~15 TOPS, NORMAL  │    ~28 TOPS      │
└────────┴────────┴────────┴────────┴────────┴────────┴────────┴────────┘
```

This is not static partitioning. The driver supports:
- **Migration**: Move a context to a different column partition at runtime
- **Preemption**: A REALTIME context can preempt a NORMAL context on the same partition
- **QoS hints**: GOPS, FPS, DMA bandwidth, latency targets guide driver placement
- **Live capacity query**: `QUERY_RESOURCE_INFO` returns `npu_tops_curr` / `npu_tops_max`

A 10 TOPS STT model + a 10 TOPS TTS model leaves ~38 TOPS for additional workloads. The supervisor knows the budget because the hardware tells it.

### 11.4 CPU-Supervised Hydration

The CPU actor supervisor (BitNet router + orchestration logic) drives the spatial schedule:

1. **Query capacity**: `QUERY_RESOURCE_INFO` → available TOPS and task slots
2. **Route decision**: BitNet router categorizes incoming request → selects expert model
3. **Hydrate**: `CREATE_HWCTX` with appropriate `num_tiles` and `priority` → overlay loaded onto free columns
4. **Execute**: `EXEC_CMD` on the partitioned context, concurrent with other active contexts
5. **Dehydrate**: `DESTROY_HWCTX` when workload completes → columns returned to pool

When you stop talking, the Whisper context can be destroyed, freeing 2-3 columns. TTS migrates to a larger partition for faster generation, or a MoE expert overlay hydrates into the freed space. Priorities resolve contention — Whisper at REALTIME preempts a NORMAL expert if you start speaking mid-inference.

### 11.5 Two Binding Paths, One Programming Model

```
2026:  Clef host → ONNX Runtime (Phase 4D) → CPU / GPU / NPU (equal opportunity)
                 → DRM UAPI (Phase 4A)     → NPU spatial control (hwctx, capacity, priority)

2027+: Clef host → ONNX Runtime             → ecosystem ONNX models on any substrate
                 → Composer → MLIR-AIE      → first-party Clef kernels → NPU overlays (zero-copy)
                 → DRM UAPI                 → spatial supervision for both paths
```

ONNX Runtime provides the uniform model runner. The DRM UAPI provides the spatial control plane. Composer provides the native compilation path for first-party kernels. All three coexist under one Clef host orchestrator that treats CPU, GPU, and NPU as equal-opportunity substrates with supervisor-driven spatial scheduling.

The Farscape binding surface that enables this: **Fidelity.ROCm** (GPU) + **Fidelity.XDNA** (NPU ioctls) + **Fidelity.XRT** (NPU high-level) + **Fidelity.OnnxRuntime** (inference) + **Fidelity.PipeWire** (audio I/O) + **Fidelity.DRM** + **Fidelity.GBM** + **Fidelity.Wayland** (presentation). Eight binding targets, three processors, one shared memory, zero copies.

---

*Companion documents: "Farscape Maturation Plan: Phases 0-3 Through HelloWayland", "Farscape Phase 4C: PipeWire Audio Binding", "Farscape Phase 4D: ONNX Runtime Binding", and "Farscape Phase 5+: MFEM Algorithmic Ingestion"*

*SpeakEZ Technologies | Fidelity Framework*
