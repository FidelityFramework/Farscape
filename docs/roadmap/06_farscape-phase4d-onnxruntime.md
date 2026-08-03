# Farscape Phase 4D: ONNX Runtime C API Binding

**SpeakEZ Technologies | Fidelity Framework**
**2026-03-10**

> **Perishable information**: Library versions, execution providers, header paths, and API surface counts in this document were verified on a specific Strix Halo system on 2026-03-10. ONNX Runtime releases frequently. Re-verify before beginning implementation.

---

## 1. Purpose

This document covers the Farscape binding for the ONNX Runtime C API, the inference engine that provides the pragmatic 2026 path for running ML models (Whisper, TTS, BitNet router, MoE experts) on the Strix Halo APU's CPU and GPU substrates.

The ONNX Runtime binding is the bridge between Clef host orchestration and pre-trained models. It does not replace the native Composer→MLIR-AIE compilation path for NPU kernels (that is the 2027+ vision). It provides immediate, production-quality inference for the audio agent while the native pipeline matures.

---

## 2. System Context (Verified 2026-03-10)

### 2.1 Installed Stack

| Component | Version | Package | Status |
|---|---|---|---|
| ONNX Runtime | 1.24.3 | `onnxruntime-opt-rocm` (AUR) | **Working** |
| ROCm | 7.2 | system packages | **Working** |
| GPU | gfx1151 (RDNA 3.5) | `amdgpu` driver | **Working** |

### 2.2 Available Execution Providers (Verified 2026-03-10)

Confirmed via `OrtGetAvailableProviders()` test program:

| Provider | Substrate | Status | Use Case |
|---|---|---|---|
| `MIGraphXExecutionProvider` | GPU (ROCm/MIGraphX) | **Available** | Dense model inference (Whisper, TTS, MoE experts) |
| `DnnlExecutionProvider` | CPU (oneDNN/AVX-512) | **Available** | BitNet router, lightweight models, fallback |
| `CPUExecutionProvider` | CPU (baseline) | **Available** | Universal fallback |
| `VitisAIExecutionProvider` | NPU (XDNA 2/AIE2P) | **Not yet available** | Equal-opportunity target for any ONNX model (requires custom ORT build with `vai_ep`) |

> **No XDNA execution provider yet**: ONNX Runtime does not ship a Vitis AI EP for the XDNA 2 NPU on Linux as of 2026-03-10. AMD's `vai_ep` code exists in their ONNX Runtime fork but has been Windows/Ryzen AI SW focused. Building ONNX Runtime with the Vitis AI EP compiled against the XDNA 2 target would add NPU as an equal-opportunity substrate alongside GPU and CPU — the same `SessionOptionsAppendExecutionProvider` pattern, the same affinity model, no separate programming path. This is a high-value target: once the EP exists, every ONNX model becomes NPU-eligible through the same binding surface.

### 2.3 Header and Library Locations

| File | Path | Notes |
|---|---|---|
| `onnxruntime_c_api.h` | `/usr/include/onnxruntime/onnxruntime_c_api.h` | Single-header C API (388 functions) |
| `libonnxruntime.so` | `/usr/lib/libonnxruntime.so` → `libonnxruntime.so.1.24.3` | Runtime library |

**Include path**: `/usr/include/onnxruntime`
**Link library**: `onnxruntime`

---

## 3. API Architecture

### 3.1 The OrtApi Table Pattern

ONNX Runtime's C API uses a **function table** pattern, not direct exported symbols. All API functions are accessed through a single global `OrtApi` struct obtained at initialization:

```c
const OrtApi* g_ort = OrtGetApiBase()->GetApi(ORT_API_VERSION);
// All subsequent calls go through the table:
g_ort->CreateEnv(ORT_LOGGING_LEVEL_WARNING, "app", &env);
g_ort->CreateSessionOptions(&session_options);
g_ort->CreateSession(env, model_path, session_options, &session);
```

This pattern is **new for Farscape**. Previous bindings (HIP, libdrm, PipeWire) target directly exported functions. ONNX Runtime's function table requires:

1. Parse the `OrtApi` struct definition to extract ~388 function pointer fields
2. Emit a struct with typed function pointer fields (same as Wayland listener delegates from Phase 1.5, but at much larger scale)
3. Generate Layer 2 wrappers that call through the table rather than via direct P/Invoke

This is a meaningful Farscape extension, not a trivial application of existing patterns.

### 3.2 Core Types

| C Type | Pattern | Farscape Mapping |
|---|---|---|
| `OrtEnv *` | Opaque handle | `[<Struct>] type OrtEnv = { Handle: nativeint }` |
| `OrtSession *` | Opaque handle | `[<Struct>] type OrtSession = { Handle: nativeint }` |
| `OrtSessionOptions *` | Opaque handle | `[<Struct>] type OrtSessionOptions = { Handle: nativeint }` |
| `OrtRunOptions *` | Opaque handle | `[<Struct>] type OrtRunOptions = { Handle: nativeint }` |
| `OrtValue *` | Opaque handle (tensor container) | `[<Struct>] type OrtValue = { Handle: nativeint }` |
| `OrtMemoryInfo *` | Opaque handle | `[<Struct>] type OrtMemoryInfo = { Handle: nativeint }` |
| `OrtAllocator *` | Opaque handle | `[<Struct>] type OrtAllocator = { Handle: nativeint }` |
| `OrtStatus *` | Error handle (NULL = success) | Special: NULL-success pattern |
| `OrtApi` | Function table struct (~388 fields) | Struct of `FnPtr<'F>` delegates |
| `ONNXTensorElementDataType` | Standard enum | Standard enum |
| `OrtLoggingLevel` | Standard enum | Standard enum |
| `OrtErrorCode` | Standard enum | Standard enum |

### 3.3 Error Convention

ONNX Runtime uses a **null-success, handle-error** pattern:

```c
OrtStatus* status = g_ort->CreateEnv(...);
if (status != NULL) {
    const char* msg = g_ort->GetErrorMessage(status);
    OrtErrorCode code = g_ort->GetErrorCode(status);
    g_ort->ReleaseStatus(status);
}
```

This is a new error convention for Farscape — not errno, not enum return code, but a nullable error handle. The Pilot project needs a new `error_convention` variant:

```toml
[error_convention]
default = "nullable_error_handle"
error_type = "OrtStatus"
get_message_fn = "GetErrorMessage"     # via OrtApi table
get_code_fn = "GetErrorCode"           # via OrtApi table
release_fn = "ReleaseStatus"           # via OrtApi table
```

The generated wrapper:

```fsharp
[<Struct>]
type OrtError = {
    Code: OrtErrorCode
    Message: string
}

let createEnv (level: OrtLoggingLevel) (name: string) : Result<OrtEnv, OrtError> =
    let mutable env = OrtEnv.zero
    let status = g_ort.CreateEnv(level, name, &&env)
    if NativeInterop.NativePtr.isNull status then Ok env
    else
        let code = g_ort.GetErrorCode(status)
        let msg = NativeString.read (g_ort.GetErrorMessage(status))
        g_ort.ReleaseStatus(status)
        Error { Code = code; Message = msg }
```

### 3.4 API Groups (~388 functions)

The full ONNX Runtime C API has ~388 functions. For the audio agent, the critical subset is much smaller (~40-50 functions):

**Environment + Session** (~10 functions):
- `CreateEnv`, `ReleaseEnv`
- `CreateSessionOptions`, `ReleaseSessionOptions`
- `SetSessionGraphOptimizationLevel`, `SetIntraOpNumThreads`
- `CreateSession`, `ReleaseSession`
- `SessionAppendExecutionProvider` (for GPU routing)

**Tensor I/O** (~15 functions):
- `CreateTensorWithDataAsOrtValue`, `CreateTensorAsOrtValue`
- `GetTensorMutableData`, `GetTensorTypeAndShape`
- `GetTensorElementType`, `GetDimensionsCount`, `GetDimensions`
- `CreateMemoryInfo`, `ReleaseMemoryInfo`
- `ReleaseTensorTypeAndShapeInfo`
- `ReleaseValue`

**Inference** (~5 functions):
- `Run` (the core inference call)
- `CreateRunOptions`, `ReleaseRunOptions`
- `SessionGetInputName`, `SessionGetOutputName`
- `SessionGetInputCount`, `SessionGetOutputCount`

**Execution Providers** (~5 functions):
- `SessionOptionsAppendExecutionProvider_MIGraphX` (GPU)
- `SessionOptionsAppendExecutionProvider_DNNL` (CPU/AVX)
- `GetAvailableProviders`, `ReleaseAvailableProviders`

**Allocator** (~5 functions):
- `GetAllocatorWithDefaultOptions`
- `AllocatorAlloc`, `AllocatorFree`
- `AllocatorGetInfo`

---

## 4. Pilot Project

```toml
# onnxruntime.pilot.toml
# Verified 2026-03-10 on Arch Linux / Omarchy
# ONNX Runtime 1.24.3, onnxruntime-opt-rocm AUR package

[library]
name = "onnxruntime"

[sources]
headers = ["/usr/include/onnxruntime/onnxruntime_c_api.h"]
include_paths = ["/usr/include/onnxruntime"]

[output]
mode = "fidelity"
directory = "./bindings/onnxruntime"

[error_convention]
default = "nullable_error_handle"
error_type = "OrtStatus"
get_message_fn = "GetErrorMessage"
get_code_fn = "GetErrorCode"
release_fn = "ReleaseStatus"

[options]
opaque_handles = true
function_table = "OrtApi"  # NEW: tells Farscape to parse function table struct

[[namespace]]
name = "Fidelity.OnnxRuntime.Core"
library = "onnxruntime"
functions = ["OrtGetApiBase"]  # Only directly exported function

[[namespace]]
name = "Fidelity.OnnxRuntime.Environment"
library = "onnxruntime"
table = "OrtApi"
prefixes = ["CreateEnv", "ReleaseEnv",
            "CreateSessionOptions", "ReleaseSessionOptions",
            "SetSessionGraphOptimizationLevel", "SetIntraOpNumThreads"]

[[namespace]]
name = "Fidelity.OnnxRuntime.Session"
library = "onnxruntime"
table = "OrtApi"
prefixes = ["CreateSession", "ReleaseSession", "Run",
            "SessionGetInput", "SessionGetOutput",
            "CreateRunOptions", "ReleaseRunOptions"]

[[namespace]]
name = "Fidelity.OnnxRuntime.Tensor"
library = "onnxruntime"
table = "OrtApi"
prefixes = ["CreateTensor", "GetTensor", "ReleaseValue",
            "CreateMemoryInfo", "ReleaseMemoryInfo",
            "ReleaseTensorTypeAndShapeInfo"]

[[namespace]]
name = "Fidelity.OnnxRuntime.Provider"
library = "onnxruntime"
table = "OrtApi"
prefixes = ["SessionOptionsAppendExecutionProvider",
            "GetAvailableProviders", "ReleaseAvailableProviders"]

[[namespace]]
name = "Fidelity.OnnxRuntime.Allocator"
library = "onnxruntime"
table = "OrtApi"
prefixes = ["GetAllocator", "Allocator"]
```

---

## 5. Farscape Extensions Required

### 5.1 Function Table Parsing

**New capability**: Parse a struct of function pointers and generate typed call-through wrappers.

The `OrtApi` struct has ~388 function pointer fields. Each field is a function pointer typedef:

```c
struct OrtApi {
    OrtStatus*(ORT_API_CALL* CreateEnv)(OrtLoggingLevel level, const char* logid, OrtEnv** out);
    OrtStatus*(ORT_API_CALL* CreateSessionOptions)(OrtSessionOptions** options);
    // ... 386 more
};
```

Farscape needs to:
1. Identify `OrtApi` as a function table struct (via `[options].function_table` in pilot.toml)
2. Parse each field's function pointer type signature
3. Generate a Clef struct with `FnPtr<'F>` typed fields
4. Generate Layer 2 wrappers that call through the table (not direct P/Invoke)

This is structurally similar to Wayland listener delegate generation (Phase 1.5), but:
- Much larger scale (388 vs ~10 fields per listener)
- Callee direction (we call the functions, not the runtime calling us)
- The table is obtained at runtime via `OrtGetApiBase()->GetApi(version)`

### 5.2 Nullable Error Handle Convention

**New error convention**: `nullable_error_handle` in `ErrorConvention` DU.

Pattern:
- Function returns `OrtStatus*`
- NULL = success
- Non-NULL = error handle, query for code + message, then release

Module changes:
- `PilotTypes.fs`: Add `NullableErrorHandle` to `ErrorConvention`
- `ErrorModuleGenerator.fs`: Generate `OrtError` struct and capture pattern
- `WrapperCodeGenerator.fs`: Generate null-check + error extraction + release pattern

---

## 6. Audio Agent Model Inventory

These models run through the ONNX Runtime binding on the target system:

| Model | Task | Format | Preferred EP | Can Also Run On |
|---|---|---|---|---|
| Whisper (base/small) | Speech-to-text | ONNX | MIGraphX (GPU) | VitisAI (NPU), DNNL (CPU) |
| Piper/Kokoro | Text-to-speech | ONNX | DNNL (CPU) | MIGraphX (GPU), VitisAI (NPU) |
| BitNet (1.58b) | Categorical routing | ONNX | DNNL (CPU) | CPU (L2-resident, sub-ms) |
| MoE expert(s) | Domain inference | ONNX | MIGraphX (GPU) | VitisAI (NPU), DNNL (CPU) |

**Substrate scheduling**: The Clef host orchestrator uses `Prefer`/`Require` affinity hints to route models to execution providers. CPU, GPU, and NPU are equal-opportunity targets — the same `SessionOptionsAppendExecutionProvider` call selects the substrate, the same `OrtApi.Run` executes inference. The BitNet router prefers CPU (L2-resident, sub-millisecond latency). Dense models prefer GPU via MIGraphX but can float to NPU when the Vitis AI EP is available. TTS can run on any substrate based on load. See `StrixHalo_Voice_Guided_Assistant.md` for the full scheduling model.

---

## 7. Validation Criteria

- `OrtGetApiBase` binds as a direct P/Invoke (the only directly exported function)
- `OrtApi` struct emits with ~388 typed function pointer fields
- All opaque handles (`OrtEnv`, `OrtSession`, `OrtValue`, etc.) emit as distinct wrapper structs
- `OrtErrorCode`, `ONNXTensorElementDataType`, `OrtLoggingLevel` emit as standard enums
- Nullable error handle convention generates correct null-check + extract + release pattern
- Layer 3 wrapper can: load a model, create input tensor, run inference, read output tensor
- Generated code compiles

### 7.1 Integration Test: Whisper Inference

1. Initialize ONNX Runtime, get `OrtApi` table
2. Create environment with WARNING log level
3. Create session options, append MIGraphX EP (GPU)
4. Load Whisper ONNX model
5. Create input tensor (F32, 16kHz mono audio, 30s window)
6. Run inference
7. Read output tokens, decode to text
8. Release all handles, verify no leaks

---

## 8. What Phase 4D Does NOT Require

- No training infrastructure (inference only)
- No model conversion (models are pre-exported to ONNX format)
- No custom ONNX operators (standard operators cover Whisper, TTS, BitNet)
- No NPU EP *initially* (the Vitis AI EP build is a separate workstream; the binding surface is EP-agnostic)
- No full 388-function binding (the critical subset is ~40-50 functions; bind incrementally)

---

## 9. Dependency Graph

```
Phase 1 (Code Generator Extensions)
    │
    ├── 5.1 Function table parsing (NEW)
    └── 5.2 Nullable error handle convention (NEW)
            │
            ▼
Phase 4D: ONNX Runtime Binding
    ├── 4D.1 Generate binding from onnxruntime.pilot.toml
    ├── 4D.2 Validate OrtApi function table + opaque handles
    ├── 4D.3 Layer 3: session creation + inference helpers
    └── 4D.4 Whisper inference integration test
            │
            ▼
    Audio Agent: PipeWire capture → Whisper → LLM → TTS → PipeWire playback
    (requires Phase 4C: PipeWire for audio I/O)
```

---

## 10. Relationship to Native Pipeline (2027+)

The ONNX Runtime binding serves two roles that evolve over time:

**2026 (all substrates via ONNX Runtime)**:
```
Clef host → ONNX Runtime C API → CPU (DNNL/AVX-512)
                                → GPU (MIGraphX/ROCm)
                                → NPU (VitisAI EP, when built)
```

All three substrates are equal-opportunity targets through a single binding surface. The NPU is not a special case — it's another execution provider. This is the pragmatic path: ONNX models run where the scheduler says, using the same API regardless of substrate.

**2027+ (ONNX Runtime + native Composer kernels)**:
```
Clef host → ONNX Runtime C API → CPU/GPU/NPU (third-party ONNX models)
          → Composer → MLIR-AIE → NPU overlays (first-party Clef kernels, zero-copy)
```

Both paths coexist. ONNX Runtime handles models distributed as ONNX files — the vast ecosystem of pre-trained models becomes immediately available on all three substrates. Composer handles models compiled from Clef source to NPU overlays, with zero-copy DMA-BUF buffer sharing.

The ONNX Runtime binding does not become obsolete. It becomes the universal model runner alongside Composer's native compilation path. The Clef host orchestrator routes to whichever backend is appropriate: ONNX Runtime for ecosystem models, Composer for first-party kernels.

---

*Companion documents: "Farscape Phase 4: NPU Binding via DRM UAPI + XRT", "Farscape Phase 4C: PipeWire Audio Binding"*

*SpeakEZ Technologies | Fidelity Framework*
