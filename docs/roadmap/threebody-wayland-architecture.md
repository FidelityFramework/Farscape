# ThreeBody: Native Wayland Framebuffer Architecture

**SpeakEZ Technologies | Fidelity Framework**
**February 2026 | Technical Design Document**

---

## 1. Overview

The ThreeBody demonstration is a native Fidelity application running on Omarchy Linux (Arch-based, Hyprland compositor) targeting the ASUS ROG Flow Z13 with AMD Ryzen AI Max+ 395 (Strix Halo). The application renders a gravitational N-body simulation with side-by-side comparison of b-posit quire accumulation versus IEEE 754 Kahan-compensated float, culminating in a trajectory reversal test that visually demonstrates precision divergence.

The rendering path is a native Wayland framebuffer produced by HIP compute kernels writing to a DMA-BUF-backed pixel buffer. The simulation, the b-posit arithmetic, and the 3D rendering all compile through the same Composer pipeline and dispatch through ROCm/HIP to the RDNA 3.5 compute units. No browser, no WebView, no separate graphics API.

Beyond the precision demonstration, the ThreeBody project is the first serious consumer of Farscape's binding generation capabilities. The system-level C libraries required (ROCm/HIP, XRT/XDNA, GBM, libdrm, Wayland client protocols) exercise the full complexity spectrum of foreign library integration. Building these bindings is the forcing function that will drive Farscape's evolution from header parser toward the Transcribe, Transpose, and Annotate capabilities described in the Composer vision.

---

## 2. Platform Declaration

```toml
[platform]
cpu = "zen5"
cpu.features = ["avx512", "bposit-quire"]
memory = "unified-lpddr5x"

[platform.gpu]
target = "gfx1151"
compute_units = 40
api = "rocm-hip"
gfx_override = "gfx1100"   # HSA_OVERRIDE_GFX_VERSION fallback

[platform.npu]
target = "xdna2"
tops = 50
api = "xrt-xdna"
l2_capacity_kb = 4096

[platform.display]
compositor = "wayland"
renderer = "hip-compute"
presentation = "gbm-drm-prime-dmabuf"
```

The GPU and NPU use different dispatch APIs. The GPU dispatches HIP kernels through ROCr/HSA hardware queues; the NPU loads pre-compiled xclbin overlays through the XRT shim. Composer resolves this at lowering time based on which platform target a kernel is assigned to.

---

## 3. Driver Binding Stack

### 3.1 Kernel Drivers

Three kernel drivers are active on Strix Halo, all in-tree on Omarchy's rolling kernel (6.14+):

**`amdgpu`**: Unified kernel driver for RDNA 3.5 GPU. Provides the DRM device at `/dev/dri/renderD128`. Handles GEM (Graphics Execution Manager) buffer objects, DMA-BUF export/import via DRM PRIME, KMS modesetting, and the GPU command submission interface that ROCm's userspace builds on. On Strix Halo, the GTT (Graphics Translation Table) domain maps to system LPDDR5X. There is no discrete VRAM domain. GPU memory allocations come from the same physical pool the CPU uses, with GPU-visible page table entries managed by amdgpu's GPUVM.

**`amdxdna`**: Mainlined in kernel 6.14 for XDNA 2 NPU. Provides the accelerator device at `/dev/accel/accel0`. Handles AIE tile partition management, overlay loading (xclbin containers), ctrlcode submission, and DMA buffer descriptor dispatch. Uses the `accel` subsystem, not DRM. The NPU's memory tiles (4,096 KB of on-chip L2 across the tile array) are managed through XRT buffer object allocation; host DDR access is through DMA engines that move data between system memory and memory tiles.

**Unified memory implications**: The CPU, GPU, and NPU all access the same LPDDR5X physical memory. The GPU accesses it through amdgpu's GPUVM page tables. The NPU accesses it through its DMA engines (reading/writing system memory addresses directly). The CPU accesses it through the MMU. A single physical page can be read by all three processors without any copy, provided the virtual address mappings and cache coherency are managed correctly.

### 3.2 GPU Userspace: ROCm/HIP

```bash
# Arch/Omarchy installation
sudo pacman -S rocm-hip-sdk rocminfo

# Verify
rocminfo | grep gfx      # Reports gfx1151

# gfx1100 kernel fallback if gfx1151 library coverage is incomplete
export HSA_OVERRIDE_GFX_VERSION=11.0.0
```

The userspace call chain:

```
HIP API call (hipLaunchKernel, hipMalloc, hipMemcpy, etc.)
  → libamdhip64.so (HIP runtime)
    → libhsa-runtime64.so (ROCr / HSA runtime)
      → libhsakmt.so (Thunk, kernel interface)
        → amdgpu kernel driver
          → GPU hardware queues → RDNA 3.5 CU execution
```

The HIP runtime manages device enumeration, memory allocation, kernel dispatch, stream synchronization, and event timing. ROCr translates these into HSA AQL (Architected Queuing Language) packets submitted to hardware queues. The kernel driver maps these queues to the GPU's command processor.

**APU-specific HIP behavior**: On integrated GPUs, HIP supports zero-copy mapped memory through `hipHostMalloc` with the `hipHostMallocMapped` flag. The runtime detects the integrated device via `hipDeviceAttributeIntegrated` and, when true, the allocated pointer is directly accessible by both CPU and GPU without any `hipMemcpy`. This is the foundation for the BAREWire UMA regions described in Section 5.

```c
// APU detection and zero-copy allocation (C-level, what the Clef binding calls)
int integrated;
hipDeviceGetAttribute(&integrated, hipDeviceAttributeIntegrated, device);
if (integrated) {
    hipHostMalloc(&ptr, size, hipHostMallocMapped);
    // ptr is now CPU-readable and GPU-readable, same physical pages
}
```

For Fidelity, Composer replaces HIP-Clang in the kernel compilation path. Clef source compiles through MLIR, lowering to the AMDGPU LLVM backend to produce a code object (`.co`) in the same format HIP-Clang produces. ROCr loads this code object identically; it does not know or care which compiler produced it. The host-side dispatch code (kernel launch, memory management, synchronization) compiles to native x86-64 through Composer's CPU backend and calls into the HIP runtime through Farscape-generated bindings.

### 3.3 NPU Userspace: XRT/XDNA

```bash
# Build from source (Ubuntu-oriented deps mapped to Arch)
git clone https://github.com/amd/xdna-driver.git
cd xdna-driver && git submodule update --init --recursive
sudo pacman -S cmake ninja gcc python python-pip libdrm mesa boost boost-libs
cd build && ./build.sh -release

# Verify
xrt-smi examine    # Should detect XDNA 2 device
ls /dev/accel/     # Should show accel0
```

The NPU dispatch model differs fundamentally from GPU:

```
XRT API call (xrt::device, xrt::bo, xrt::kernel)
  → libxrt_coreutil.so (XRT runtime)
    → XDNA shim (libxrt_driver_xdna.so)
      → amdxdna kernel driver (/dev/accel/accel0)
        → NPU: overlay load → ctrlcode → DMA → AIE tile execution
```

The NPU does not execute arbitrary compute kernels. It executes pre-compiled overlays: xclbin containers that package AIE tile ELF binaries, stream switch routing tables, and DMA buffer descriptors. The workflow:

1. Compile the workload to an xclbin offline through MLIR-AIE
2. Open the XRT device and load the xclbin onto a partition of AIE tiles
3. Allocate buffer objects for input/output through the XRT BO API
4. Submit execution; ctrlcode orchestrates DMA transfers and tile activation
5. Synchronize and read results

For Fidelity, the NPU compilation path:

```
Clef force accumulation kernel (b-posit integer MAC operations)
  → MLIR (tensor/linalg dialects)
    → MLIR-AIE dialect (tile assignments, DMA descriptors, stream switch config)
      → xclbin overlay container
        → XRT loads overlay to NPU partition at dispatch time
```

The host-side orchestration (buffer allocation, execution submission, synchronization) calls into XRT through a separate set of Farscape-generated bindings.

**NPU memory access to UMA**: XRT buffer objects allocated with the `host_only` flag reside in system memory and are accessible to both CPU and NPU DMA engines. The NPU's DMA descriptors reference physical addresses in LPDDR5X; the XRT shim handles the virtual-to-physical translation through the amdxdna driver. For the pointer exchange pattern, the CPU writes body state into a BAREWire region backed by an XRT buffer object, and the NPU DMA engines read it directly.

---

## 4. GBM / DRM PRIME / DMA-BUF Presentation Pipeline

### 4.1 The Problem

HIP compute kernels produce pixels in a GPU-accessible memory region. The Wayland compositor (Hyprland) needs to composite those pixels into its display output. The connection between these two is DMA-BUF: a Linux kernel mechanism for sharing buffer handles across subsystems without copying data.

### 4.2 The Pipeline

```
1. Allocate: GBM creates a buffer object on the DRM render node
   gbm_bo_create(gbm_device, width, height, GBM_FORMAT_ARGB8888,
                  GBM_BO_USE_RENDERING | GBM_BO_USE_LINEAR)

2. Export: GBM exports the buffer as a DMA-BUF file descriptor
   fd = gbm_bo_get_fd(gbm_bo)

3. Import into HIP: The DMA-BUF fd is imported as HIP external memory
   hipExternalMemoryHandleDesc desc;
   desc.type = hipExternalMemoryHandleTypeOpaqueFd;
   desc.handle.fd = fd;
   desc.size = stride * height;
   hipImportExternalMemory(&extMem, &desc);
   hipExternalMemoryGetMappedBuffer(&devPtr, extMem, &bufferDesc);
   // devPtr is now a HIP device pointer to the GBM buffer's backing pages

4. Render: HIP compute kernel writes RGBA pixels to devPtr
   hipLaunchKernel(renderKernel, grid, block, args, 0, stream);
   hipStreamSynchronize(stream);

5. Present: Wayland client imports the same DMA-BUF fd
   zwp_linux_buffer_params_v1 *params =
       zwp_linux_dmabuf_v1_create_params(dmabuf);
   zwp_linux_buffer_params_v1_add(params, fd, 0, 0, stride,
       DRM_FORMAT_MOD_LINEAR >> 32,
       DRM_FORMAT_MOD_LINEAR & 0xFFFFFFFF);
   wl_buffer *buffer = zwp_linux_buffer_params_v1_create_immed(
       params, width, height, DRM_FORMAT_ARGB8888, 0);
   wl_surface_attach(surface, buffer, 0, 0);
   wl_surface_commit(surface);

6. Compositor reads the same physical pages through its own GPU context.
   No copy occurs.
```

### 4.3 Why GBM, Not hipMalloc

On an APU with unified memory, `hipMalloc` and `hipHostMalloc` allocate from the same physical LPDDR5X pool. But the Wayland compositor cannot import an arbitrary HIP allocation as a surface buffer. The compositor speaks `zwp_linux_dmabuf_v1`, which requires a DMA-BUF file descriptor with format and modifier metadata. GBM (Generic Buffer Manager) is the standard mechanism for creating DRM-backed buffer objects that export as DMA-BUFs with the metadata the compositor expects.

The allocation path: GBM allocates through the amdgpu DRM driver → amdgpu allocates from the GTT domain (system LPDDR5X on Strix Halo) → the GEM buffer object exports as a DMA-BUF fd → HIP imports the fd as external memory → the HIP device pointer and the compositor's buffer reference resolve to the same physical pages.

Zero copies in this pipeline.

### 4.4 Double Buffering

Two GBM buffer objects, two DMA-BUF fds, two HIP device pointers. The rendering kernel writes to the back buffer while the compositor reads the front buffer. The `wl_buffer.release` event from the compositor signals that it has finished reading; only then can the application reclaim it as the next back buffer.

```
Frame N:   HIP writes buffer[0],  compositor reads buffer[1]
Frame N+1: HIP writes buffer[1],  compositor reads buffer[0]  (after release)
```

---

## 5. BAREWire Regions in Unified Memory

### 5.1 The UMA Pointer Handoff Problem

Three processors need to access the same body state data. On Strix Halo, the physical memory is shared, but each processor uses a different virtual address space and a different allocation API:

- CPU: standard `mmap` or heap allocation
- GPU: `hipHostMalloc` with `hipHostMallocMapped` (returns a CPU pointer; GPU accesses same pages)
- NPU: XRT `xrt::bo` with `host_only` flag (XRT manages the DMA-visible allocation)

There is no copy. The challenge is that each allocation API returns a different pointer (or handle) to the same physical memory, and each processor has different alignment, cache coherency, and access ordering requirements.

### 5.2 BAREWire Region Descriptor for UMA

```fsharp
/// BAREWire schema for the N-body state region shared across CPU, GPU, and NPU.
/// All three processors read the same physical pages through different virtual mappings.

type ProcessorAccess =
    | CpuOnly
    | CpuGpu
    | CpuGpuNpu

type CoherencyDomain =
    | SystemCoherent       // CPU cache-coherent; GPU/NPU see writes after fence
    | DeviceCoherent       // GPU cache-coherent; CPU sees writes after hipDeviceSynchronize
    | Uncached             // No caching; all writes immediately visible (highest latency)

type UmaRegionDescriptor = {
    /// Total size in bytes
    Size: uint64
    /// Alignment requirement (128 bytes for HIP coalesced access)
    Alignment: uint32
    /// Which processors access this region
    Access: ProcessorAccess
    /// Cache coherency domain
    Coherency: CoherencyDomain
    /// CPU virtual address (from hipHostMalloc or mmap)
    CpuPtr: nativeint
    /// GPU device pointer (from hipHostGetDevicePointer; same physical pages)
    GpuPtr: nativeint
    /// XRT buffer object handle (if NPU access required)
    XrtBoHandle: uint32 option
    /// DMA-BUF fd (if this region is also exported for compositor presentation)
    DmaBufFd: int option
}
```

### 5.3 CPU+GPU Allocation (Phase 1 and 2)

```fsharp
let allocateCpuGpuRegion (size: uint64) : UmaRegionDescriptor =
    // 1. Detect APU
    let integrated = hipDeviceGetAttribute hipDeviceAttributeIntegrated device
    assert integrated  // Strix Halo is always integrated

    // 2. Allocate mapped pinned memory
    //    hipHostMallocMapped: both CPU and GPU can access
    //    hipHostMallocCoherent: fine-grained system coherency
    //    (CPU writes visible to GPU without explicit flush;
    //     higher per-access latency, but no sync calls needed)
    let cpuPtr =
        hipHostMalloc size (hipHostMallocMapped ||| hipHostMallocCoherent)

    // 3. Get the GPU-side device pointer for the same physical pages
    let gpuPtr = hipHostGetDevicePointer cpuPtr 0u

    { Size = size
      Alignment = 128u
      Access = CpuGpu
      Coherency = SystemCoherent
      CpuPtr = cpuPtr
      GpuPtr = gpuPtr
      XrtBoHandle = None
      DmaBufFd = None }
```

### 5.4 CPU+GPU+NPU Allocation (Phase 3)

When the NPU is involved, the allocation must be visible to all three processors. HIP and XRT use different allocation APIs with no direct sharing path. The solution is DMA-BUF as the interop primitive:

```fsharp
let allocateCpuGpuNpuRegion (size: uint64) : UmaRegionDescriptor =
    // 1. Allocate GBM buffer object on DRM render node
    //    GBM_FORMAT_R32 for raw data (not pixel-formatted)
    let gbmBo =
        gbm_bo_create gbmDevice width 1u GBM_FORMAT_R32 GBM_BO_USE_RENDERING

    // 2. Export as DMA-BUF fd
    let dmaBufFd = gbm_bo_get_fd gbmBo

    // 3. Import into HIP as external memory
    let extMem = hipImportExternalMemory dmaBufFd size
    let gpuPtr = hipExternalMemoryGetMappedBuffer extMem

    // 4. mmap the DMA-BUF fd for CPU access
    let cpuPtr = mmap size PROT_READ_WRITE MAP_SHARED dmaBufFd 0

    // 5. Create XRT buffer object from the same DMA-BUF fd
    let xrtBo = xrt_bo_import xrtDevice dmaBufFd size

    { Size = size
      Alignment = 128u
      Access = CpuGpuNpu
      Coherency = SystemCoherent
      CpuPtr = cpuPtr
      GpuPtr = gpuPtr
      XrtBoHandle = Some xrtBo.handle
      DmaBufFd = Some dmaBufFd }
```

This uses the DMA-BUF subsystem for exactly its designed purpose: sharing buffer handles across subsystems managed by different drivers.

### 5.5 Body State Layout

```fsharp
/// Per-body state, 64 bytes, packed for coalesced GPU access.
/// BAREWire schema: fixed-size record, no pointers, no indirection.
[<Struct; StructLayout(LayoutKind.Sequential, Size = 64)>]
type BodyState = {
    PositionX: float64      // offset  0
    PositionY: float64      // offset  8
    PositionZ: float64      // offset 16
    VelocityX: float64      // offset 24
    VelocityY: float64      // offset 32
    VelocityZ: float64      // offset 40
    Mass:      float64      // offset 48
    BodyId:    uint32        // offset 56
    _padding:  uint32        // offset 60 (pad to 64-byte boundary)
}
```

The 64-byte stride ensures consecutive bodies map to different 128-byte GPU cache lines (two bodies per line), enabling coalesced reads when a wavefront loads body state for the pairwise force kernel. The same layout is read by the CPU integration step and, at scale, by the NPU DMA engines. No marshaling or format conversion at any processor boundary.

Total region size: N × 64 bytes. At 32 bodies: 2,048 bytes. At 256 bodies: 16,384 bytes.

### 5.6 Synchronization Contract

**CPU writes, GPU reads** (integration actor writes state, rendering kernel reads it): On a system-coherent APU allocation (`hipHostMallocCoherent`), CPU writes are visible to the GPU without explicit flush. For 2 KB of body state, the coherency fabric latency is negligible.

**GPU writes, CPU reads** (GPU force kernel writes accumulated forces): Requires `hipStreamSynchronize` or `hipEventSynchronize` before the CPU reads.

**NPU DMA reads** (NPU force accumulation reads body state): `xrt::bo::sync` with `XCL_BO_SYNC_BO_TO_DEVICE` before execution submission. After NPU completion, `xrt::bo::sync` with `XCL_BO_SYNC_BO_FROM_DEVICE` makes results visible to CPU.

The double-buffer pattern applies to body state as well as pixel buffers. The integration actor writes to the back state buffer; the rendering kernel reads from the front. Pointer swap is atomic. No lock contention between actors.

---

## 6. HIP Compute Rendering

### 6.1 Rendering Kernels

All 3D rendering is HIP compute writing directly to the DMA-BUF pixel buffer from Section 4. No vertex buffers, no index buffers, no rasterization state.

**Clear kernel**: Background fill. One thread per pixel.

**Sphere splatting kernel**: Projects each body's sphere onto the pixel grid. Reads position, mass, velocity from the BAREWire state snapshot. Computes screen-space projection from the camera transform. Writes RGBA with Lambertian shading. Sphere radius: cube-root of mass (volume proportional to mass). Color: kinetic energy mapping (blue = slow, red = fast). Each body is an independent work item.

**Trail kernel**: Anti-aliased line segments between consecutive positions in each body's ring buffer (circular buffer, fixed length, oldest overwritten each frame). 2-3 seconds of trajectory history.

**Overlay kernel**: Wireframe starting-position markers (reversal phase), energy conservation plot, reversal error metric.

**Compositing kernel**: Depth-sorts per pixel, composites back-to-front, writes final RGBA to the DMA-BUF.

### 6.2 Camera

View and projection matrices (128 bytes: two 4×4 float64) computed on CPU from input events. Written to a small BAREWire region. Rendering kernels read it each frame. The presenter orbits, zooms, and tracks bodies during the simulation without interrupting integration.

### 6.3 Side-by-Side

Left half of the pixel buffer: b-posit simulation. Right half: IEEE 754 simulation. Same camera transform. Both viewports render in a single kernel dispatch; block assignment maps thread blocks to left or right based on block index.

---

## 7. Simulation-Rendering Decoupling

**Posit integration actor** (Olivier worker): 3,000 timesteps/second. B-posit quire accumulation (CPU AVX-512 or NPU at scale). Writes state snapshots at 60 Hz into double-buffered BAREWire region.

**Float integration actor** (second Olivier worker): Identical physics, IEEE 754 with Kahan compensation. Separate double-buffered region.

**Rendering actor** (Olivier worker): Reads both front state buffers at 60 Hz. Dispatches HIP kernels to produce the DMA-BUF frame. Commits to Wayland compositor. Never blocks on integration actors.

**Prospero supervisor**: Actor lifecycle management. Restart on failure without losing simulation state.

At 32 bodies, 50 substeps per frame: 50 × 496 pairwise × 3 components = 74,400 multiply-accumulates per frame. Well within the 16.7 ms budget on a single Zen 5 core.

---

## 8. Farscape Binding Requirements

### 8.1 Libraries

| Library | Binding Challenge |
|---|---|
| `libamdhip64.so` | Opaque handle types (`hipDevice_t`, `hipStream_t`, `hipEvent_t`); enum bitmask flags with invalid combinations; APU-vs-discrete branching in memory semantics |
| `libhsa-runtime64.so` | AQL packet struct layout must match hardware queue format; signal objects with acquire/release ordering |
| `libxrt_coreutil.so` | C++ API accessed through C shim; xclbin container lifecycle; buffer object alloc/map/sync/execute/unmap/free sequence |
| `libgbm.so` | DRM device fd management; format+modifier negotiation; DMA-BUF fd export where fd must outlive the GBM BO in some patterns |
| `libdrm.so` | ioctl structs must match kernel ABI byte-for-byte; GEM handle and DRM PRIME fd management |
| `libwayland-client.so` | Generated from XML protocol; proxy object destroy semantics; listener callback dispatch tables; event loop integration |

### 8.2 What This Forces Farscape to Learn

**Opaque handle lifecycle**: HIP handles are created by one API call and destroyed by another. Farscape must generate bindings that encode create/destroy pairing and prevent use-after-free. This is the seed of lifetime inference.

**DMA-BUF fd escape analysis**: `gbm_bo_get_fd` returns an fd referencing the same kernel buffer as the GBM BO. The fd must outlive the BO in some patterns; the BO must outlive the fd in others. Farscape must track which fds escape the caller's scope. This is escape analysis across an FFI boundary.

**Kernel ABI struct fidelity**: libdrm ioctl structs (`drm_mode_create_dumb`, `drm_prime_handle`, etc.) pass directly to the kernel. Offsets, sizes, alignment, and padding must match exactly. Farscape parses kernel headers and generates BAREWire descriptors that reproduce the layout. This is the seed of schema annotation for binary structures.

**Protocol XML → callback tables**: Wayland client code is generated from XML protocol definitions. Each interface has a listener struct of function pointers. Farscape must parse the XML (not just C headers), generate Clef listener record types, and produce dispatch glue. A multi-format parsing problem.

**C++ through C shim**: XRT's native API is C++. The C shim (`xrt.h`) provides callable entry points but loses RAII lifecycle semantics. Farscape generates bindings from the C shim, then must reconstruct the lifecycle contracts the C++ wrappers encode implicitly.

**Flag combinatorics**: `hipHostMalloc` accepts a bitmask where certain combinations are invalid or produce undefined behavior on specific hardware classes. Farscape should generate typed wrappers that make invalid combinations unrepresentable, or at minimum document the valid combinations in binding metadata.

### 8.3 Maturation Path

**Phase 1** (3-body comparison): Minimal HIP surface (device query, `hipHostMalloc`, `hipLaunchKernel`, `hipStreamSynchronize`), minimal GBM surface (`gbm_create_device`, `gbm_bo_create`, `gbm_bo_get_fd`), Wayland `xdg-shell` and `zwp_linux_dmabuf_v1` protocols. Sufficient for CPU integration + HIP rendering + Wayland presentation.

**Phase 2** (32-body disc formation): Same bindings under sustained 60 fps. Double-buffer swap and `wl_buffer.release` event handling must work correctly. Any latency in Farscape-generated bindings becomes visible as frame drops.

**Phase 3** (NPU-accelerated): Farscape binds XRT through the C shim. xclbin loading, BO allocation from DMA-BUF fd, execution submission, synchronization. The three-processor DMA-BUF sharing pattern exercises Farscape's ability to coordinate bindings across two foreign libraries (HIP and XRT) sharing a common kernel resource.

Each phase produces concrete binding code that works or doesn't. Failures identify exactly where Farscape's parsing, type inference, or lifetime tracking falls short.

---

## 9. B-Posit Quire on AVX-512

The b-posit specification (es = 5, rS ≤ 6) defines an 800-bit quire, uniform across precisions for n > 12. Gustafson's decomposition: "a vector of 25 32-bit integers." On Zen 5 AVX-512, the quire spans two ZMM registers (ZMM0: lanes 0-15, ZMM1: lanes 16-24). Accumulation is `VPADDD` with carry propagation.

With 32 ZMM registers and 2 per quire, 8-10 concurrent quires after register reservation. For 32 bodies (31 contributions × 3 components = 93 accumulations per body), 3 bodies accumulate simultaneously with 9 quires. Full evaluation in ~11 rounds.

Fidelity.Platform.CPU intrinsics: `quireAccumulate` (VPADDD + carry), `quireToPosit` (rounding), `positDecode`/`positEncode` (five-way MUX, shift-and-mask), `positMultiply` (integer fraction multiply + exponent add).

---

## 10. Demo Progression

### 10.1 Phase 1: Three-Body Comparison

Three masses, known chaotic configuration, published Lyapunov exponent. Side-by-side. Forward 45 seconds (indistinguishable trajectories). Reverse 45 seconds (135,000 timesteps). B-posit retraces to starting configuration. IEEE 754 drifts.

### 10.2 Phase 2: 32-Body Disc Formation

Gravitational softening, 2-3 massive bodies, net angular momentum, Gaussian velocity dispersion. Disc forms within 30-45 seconds. Mass-proportional radii. Kinetic energy colors. Interactive camera.

### 10.3 Phase 3: NPU-Accelerated (256+ Bodies)

NPU: pairwise force accumulation (integer MACs via xclbin overlay). CPU: timestep integration, quire finalization. GPU: HIP compute rendering. Three processors, one SoC, BAREWire regions backed by shared DMA-BUFs.

### 10.4 Phase 4: FPGA Sidecar (Optional)

Arty A7 via USB-C. 15-20 streaming quire accumulations within ~100 Mbit/s bandwidth. Combined with CPU quires: 25-32 total. B-posit precision is CPU-native on any AVX-512 machine; the FPGA extends capacity.

---

## 11. Summary

ThreeBody is a single native executable compiled by Composer, targeting three Strix Halo processors through two dispatch APIs (ROCm/HIP for GPU, XRT/XDNA for NPU) with shared data regions backed by DMA-BUFs in unified LPDDR5X. The Wayland framebuffer is a GBM-allocated DMA-BUF written by HIP compute kernels and presented through `zwp_linux_dmabuf_v1`. The binding layer for all five foreign libraries is generated by Farscape.

The architectural coherence is structural: one memory pool, one compilation pipeline, one set of BAREWire region descriptors defining data layout for all three processors. The demo progression maps directly to the Farscape maturation path, with each phase requiring deeper binding capabilities than the last.

---

*Implementation is contingent on Fidelity.Platform.CPU (b-posit AVX-512 intrinsics), Fidelity.Platform.GPU (HIP compute dispatch), Farscape binding generation (ROCm/HIP, XRT, GBM, libdrm, Wayland protocols), and Composer's MLIR lowering pipeline.*

*SpeakEZ Technologies | Fidelity Framework*
*License: MIT*
