# Farscape Phase 4C: PipeWire Audio Binding

**SpeakEZ Technologies | Fidelity Framework**
**2026-03-10**

> **Perishable information**: Library versions, header paths, and API surfaces in this document were verified on an Arch Linux (Omarchy) system on 2026-03-10. PipeWire is under active development. Re-verify before beginning implementation.

---

## 1. Purpose

This document covers the Farscape binding for PipeWire, the audio/video infrastructure that provides low-latency capture and playback on the target system. PipeWire replaces PulseAudio and JACK on modern Linux; it is the audio I/O foundation for the Strix Halo voice-guided assistant.

The binding enables:
- **Microphone capture**: Low-latency audio input for speech recognition (Whisper)
- **Speaker playback**: Low-latency audio output for text-to-speech (Piper/Kokoro)
- **Stream routing**: Programmatic control over audio graph topology
- **Zero-copy buffer exchange**: PipeWire's buffer sharing model aligns with the DMA-BUF patterns established in Phases 3-4

---

## 2. System Context (Verified 2026-03-10)

### 2.1 Installed Stack

| Component | Version | Package | Status |
|---|---|---|---|
| PipeWire | 1.2.x (verify with `pipewire --version`) | `pipewire` | **Running** (systemd user service) |
| WirePlumber | 0.5.x | `wireplumber` | **Running** (session manager) |
| pipewire-pulse | bridge | `pipewire-pulse` | Active (PulseAudio compat) |
| pipewire-alsa | bridge | `pipewire-alsa` | Active (ALSA compat) |
| SPA plugins | built-in | `pipewire` | Audio/video/MIDI support modules |

### 2.2 Header Locations

| Header | Path | Purpose |
|---|---|---|
| `pipewire/pipewire.h` | `/usr/include/pipewire-0.3/pipewire/pipewire.h` | Top-level umbrella |
| `pipewire/stream.h` | `/usr/include/pipewire-0.3/pipewire/stream.h` | Stream API (primary binding target) |
| `pipewire/filter.h` | `/usr/include/pipewire-0.3/pipewire/filter.h` | Filter API (processing nodes) |
| `pipewire/core.h` | `/usr/include/pipewire-0.3/pipewire/core.h` | Core connection, registry |
| `pipewire/loop.h` | `/usr/include/pipewire-0.3/pipewire/loop.h` | Event loop |
| `pipewire/properties.h` | `/usr/include/pipewire-0.3/pipewire/properties.h` | Key-value property dictionaries |
| `spa/param/audio/format-utils.h` | `/usr/include/spa-0.2/spa/param/audio/format-utils.h` | Audio format negotiation |
| `spa/pod/builder.h` | `/usr/include/spa-0.2/spa/pod/builder.h` | SPA POD construction |

**Include paths**: `/usr/include/pipewire-0.3`, `/usr/include/spa-0.2`
**Link library**: `libpipewire-0.3.so`

### 2.3 API Architecture

PipeWire's C API follows a consistent pattern:

```
pw_init() → pw_main_loop_new() → pw_context_new() → pw_core_connect()
  → pw_stream_new() → pw_stream_connect() → event loop → pw_stream_destroy()
```

Key patterns relevant to Farscape:
- **Opaque handles**: `pw_main_loop`, `pw_context`, `pw_core`, `pw_stream`, `pw_filter` — all forward-declared struct pointers
- **Callback structs**: `pw_stream_events`, `pw_registry_events` — struct of function pointers (same pattern as Wayland listeners from Phase 1.5)
- **SPA POD system**: Binary serialization format for parameter negotiation (audio format, buffer size). PODs are built via a builder API, not direct struct manipulation
- **Properties**: Key-value dictionaries (`pw_properties`) for metadata

---

## 3. API Surface

### 3.1 Core Types

| C Type | Pattern | Farscape Mapping |
|---|---|---|
| `struct pw_main_loop *` | Opaque handle | `[<Struct>] type pw_main_loop = { Handle: nativeint }` |
| `struct pw_context *` | Opaque handle | `[<Struct>] type pw_context = { Handle: nativeint }` |
| `struct pw_core *` | Opaque handle | `[<Struct>] type pw_core = { Handle: nativeint }` |
| `struct pw_stream *` | Opaque handle | `[<Struct>] type pw_stream = { Handle: nativeint }` |
| `struct pw_filter *` | Opaque handle | `[<Struct>] type pw_filter = { Handle: nativeint }` |
| `struct pw_proxy *` | Opaque handle | `[<Struct>] type pw_proxy = { Handle: nativeint }` |
| `struct pw_properties *` | Opaque handle | `[<Struct>] type pw_properties = { Handle: nativeint }` |
| `struct pw_buffer *` | Public struct (buffer pointer + metadata) | ABI-critical struct with explicit layout |
| `struct pw_stream_events` | Callback struct (function pointers) | Struct with delegate fields |
| `enum pw_stream_state` | Standard enum | Standard enum |
| `enum pw_stream_flags` | Bitmask enum | `[<Flags>]` enum |
| `enum pw_direction` | Standard enum (INPUT=0, OUTPUT=1) | Standard enum |
| `enum spa_audio_format` | Standard enum (S16LE, F32LE, etc.) | Standard enum |

### 3.2 API Groups

**Initialization**: `pw_init`, `pw_deinit`, `pw_get_library_version`

**Main loop**: `pw_main_loop_new`, `pw_main_loop_destroy`, `pw_main_loop_run`, `pw_main_loop_quit`, `pw_main_loop_get_loop`

**Context + Core**: `pw_context_new`, `pw_context_destroy`, `pw_context_connect`, `pw_core_disconnect`

**Stream** (primary binding target for audio I/O):
- `pw_stream_new`, `pw_stream_destroy`
- `pw_stream_connect` (direction, flags, format params)
- `pw_stream_dequeue_buffer`, `pw_stream_queue_buffer`
- `pw_stream_set_active`, `pw_stream_get_state`
- `pw_stream_get_time` (timing/latency info)

**Properties**: `pw_properties_new`, `pw_properties_set`, `pw_properties_get`, `pw_properties_free`

**Registry** (for device enumeration): `pw_core_get_registry`, registry events for node/port/link discovery

### 3.3 Callback Structs

The `pw_stream_events` struct is the critical callback interface:

```c
struct pw_stream_events {
    uint32_t version;
    void (*destroy)(void *data);
    void (*state_changed)(void *data, enum pw_stream_state old,
                          enum pw_stream_state state, const char *error);
    void (*control_info)(void *data, uint32_t id, const struct pw_stream_control *control);
    void (*io_changed)(void *data, uint32_t id, void *area, uint32_t size);
    void (*param_changed)(void *data, uint32_t id, const struct spa_pod *param);
    void (*add_buffer)(void *data, struct pw_buffer *buffer);
    void (*remove_buffer)(void *data, struct pw_buffer *buffer);
    void (*process)(void *data);  // ← THE hot path: called per audio quantum
    void (*drained)(void *data);
    void (*command)(void *data, const struct spa_command *command);
    void (*trigger_done)(void *data);
};
```

This is structurally identical to the Wayland listener pattern from Phase 1.5. Farscape emits delegate types for each callback and a struct with delegate fields. The `process` callback is the audio hot path — called once per audio quantum (typically every 5-10ms at 48kHz).

---

## 4. Pilot Project

```toml
# pipewire.pilot.toml
# Verified 2026-03-10 on Arch Linux / Omarchy
# PipeWire 1.2.x, headers at /usr/include/pipewire-0.3/

[library]
name = "pipewire-0.3"

[sources]
headers = [
    "/usr/include/pipewire-0.3/pipewire/pipewire.h",
    "/usr/include/pipewire-0.3/pipewire/stream.h",
    "/usr/include/pipewire-0.3/pipewire/filter.h",
    "/usr/include/pipewire-0.3/pipewire/core.h",
    "/usr/include/pipewire-0.3/pipewire/loop.h",
    "/usr/include/pipewire-0.3/pipewire/properties.h"
]
include_paths = [
    "/usr/include/pipewire-0.3",
    "/usr/include/spa-0.2"
]

[output]
mode = "fidelity"
directory = "./bindings/pipewire"

[error_convention]
default = "errno"  # PipeWire functions return -errno on failure

[options]
opaque_handles = true
flags_enums = true

[[namespace]]
name = "Fidelity.PipeWire.Core"
library = "pipewire-0.3"
prefixes = ["pw_init", "pw_deinit", "pw_get"]
functions = ["pw_init", "pw_deinit", "pw_get_library_version"]

[[namespace]]
name = "Fidelity.PipeWire.MainLoop"
library = "pipewire-0.3"
prefixes = ["pw_main_loop"]
functions = [
    "pw_main_loop_new", "pw_main_loop_destroy",
    "pw_main_loop_run", "pw_main_loop_quit",
    "pw_main_loop_get_loop"
]

[[namespace]]
name = "Fidelity.PipeWire.Context"
library = "pipewire-0.3"
prefixes = ["pw_context"]
functions = [
    "pw_context_new", "pw_context_destroy",
    "pw_context_connect"
]

[[namespace]]
name = "Fidelity.PipeWire.Stream"
library = "pipewire-0.3"
prefixes = ["pw_stream"]
functions = [
    "pw_stream_new", "pw_stream_new_simple",
    "pw_stream_destroy", "pw_stream_connect",
    "pw_stream_disconnect", "pw_stream_set_active",
    "pw_stream_get_state", "pw_stream_get_time",
    "pw_stream_dequeue_buffer", "pw_stream_queue_buffer",
    "pw_stream_set_error", "pw_stream_update_params",
    "pw_stream_get_node_id"
]

[[namespace]]
name = "Fidelity.PipeWire.Properties"
library = "pipewire-0.3"
prefixes = ["pw_properties"]
functions = [
    "pw_properties_new", "pw_properties_new_string",
    "pw_properties_copy", "pw_properties_free",
    "pw_properties_set", "pw_properties_get",
    "pw_properties_setf"
]
```

---

## 5. Audio Agent Integration

### 5.1 Capture Pipeline (Microphone → Whisper)

```
PipeWire source (mic) → pw_stream (INPUT, F32LE, 16kHz mono)
    → process callback: dequeue_buffer → copy to ring buffer
    → ring buffer → Whisper inference (ONNX Runtime, see Phase 4D)
```

The `process` callback fires per quantum. At 16kHz mono F32, each quantum is ~960 bytes (5ms × 16000 × 4). The callback copies into a lock-free ring buffer; the inference thread consumes asynchronously.

### 5.2 Playback Pipeline (TTS → Speaker)

```
TTS inference → audio samples (F32LE, 22.05kHz or 24kHz)
    → ring buffer → process callback: dequeue_buffer → fill → queue_buffer
    → pw_stream (OUTPUT) → PipeWire sink (speaker)
```

### 5.3 Latency Budget

For conversational audio, the end-to-end latency target is <500ms (user speech end → assistant speech begin). The PipeWire contribution should be <20ms total (capture + playback). At the default quantum of 1024 samples / 48kHz ≈ 21ms, this is achievable with a single-quantum buffer. PipeWire allows requesting smaller quanta via `pw_properties_set(props, PW_KEY_NODE_LATENCY, "256/48000")` for ~5ms.

---

## 6. SPA POD Considerations

PipeWire uses SPA POD (Plain Old Data) for parameter negotiation — audio format, buffer size, channel layout. PODs are built via a builder API, not direct struct construction. The builder functions are in `spa/pod/builder.h`.

For the audio agent, the POD surface is small:
- `spa_format_audio_raw_build`: Specify sample format (F32LE), sample rate, channels
- `spa_pod_builder_init`, `spa_pod_builder_add_*`: General POD construction

This is a small, well-defined subset. The Farscape binding can either:
1. Bind the SPA builder functions directly (straightforward C functions)
2. Provide a Layer 3 helper that constructs common audio format PODs

Option 1 is sufficient for Phase 4C. Option 2 is a convenience refinement.

---

## 7. Validation Criteria

- All opaque handles (`pw_main_loop`, `pw_context`, `pw_core`, `pw_stream`, `pw_properties`) emit as distinct wrapper structs
- `pw_stream_events` emits as a struct with delegate fields for all callbacks
- `pw_stream_flags` emits with `[<Flags>]`
- `pw_stream_state`, `pw_direction`, `spa_audio_format` emit as standard enums
- Layer 3 wrapper can: initialize PipeWire, create a stream, connect to default sink, play a tone
- Generated code compiles

### 7.1 Integration Test: Capture + Playback Loopback

1. `pw_init` → create main loop → create context → connect core
2. Create INPUT stream (F32LE, 48kHz, mono), connect to default source
3. Create OUTPUT stream (F32LE, 48kHz, mono), connect to default sink
4. In process callbacks: copy captured samples to playback buffer
5. Run for 5 seconds, verify audio passes through
6. Clean shutdown (destroy streams, disconnect, deinit)

---

## 8. What Phase 4C Does NOT Require

- No new code generator extensions (callback structs are covered by Phase 1.5's delegate pattern)
- No PipeWire session manager integration (WirePlumber handles routing; the binding talks to PipeWire directly)
- No JACK compatibility layer (PipeWire's native API is the target)
- No video/screen capture (audio only for the voice agent)

---

## 9. Dependency Graph

```
Phase 1.5 (delegate/callback struct generation)
    │
    ▼
Phase 4C: PipeWire Audio Binding
    ├── 4C.1 Generate binding from pipewire.pilot.toml
    ├── 4C.2 Validate opaque handles + callback structs
    ├── 4C.3 Layer 3: capture/playback stream helpers
    └── 4C.4 Loopback integration test
            │
            ▼
    Audio Agent: Capture → Whisper → LLM → TTS → Playback
    (requires Phase 4D: ONNX Runtime for inference)
```

---

*Companion documents: "Farscape Phase 4: NPU Binding via DRM UAPI + XRT", "Farscape Phase 4D: ONNX Runtime Binding"*

*SpeakEZ Technologies | Fidelity Framework*
