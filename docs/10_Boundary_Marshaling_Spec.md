# Boundary Marshaling Specification

## The Rule

**No C idioms may appear in Layer 3 or application code.** Layer 2 is the membrane: it converts native representations and ownership into Clef values and operations before invoking application code. Moving a declaration into a binding package does not by itself discharge that conversion.

The governing language contract is `clef-lang-spec/spec/ffi-boundary.md`. Interior Clef has no raw pointer type. A C pointer crosses through an opaque `CHandle<'T>`, and a declared native entry through `FnPtr<'F>`; neither is ever null in interior Clef. Null is converted only at the foreign boundary. Source integers and reals are `int` and `float`; ABI widths belong in descriptors, never in width-named source types, numeric address casts, or literal suffixes.

## Layer Model

| Layer | Owner | Contains native boundary details? | Consumer |
|---|---|---|---|
| **Layer 1** — Extern declarations and ABI descriptors | Farscape | Yes: native symbols, opaque handles, complete C representation contracts | Layer 2 |
| **Layer 2** — Wrappers, protocol marshaling, callback adapters and lifecycle | Farscape and binding authors | Yes: this is the membrane | Layer 3 and applications |
| **Layer 3** — Composition libraries | Library authors | No: Clef values and operations | Applications |
| **Application** — End-user program | Application authors | No: Clef values and operations | End user |

Layer 2 includes generated wrappers and protocol support, and authored binding overlays where a library-specific contract needs conversion or lifecycle code. An authored overlay belongs to the binding package, with explicit native declarations and tests; it is not an application-local workaround. Generation must supply the declarations it consumes. The compiler validates those declarations, but package location alone does not prove that the application-facing surface is clean.

A logical record containing a handle is not the native pointer it contains. The record stays inside Clef; Layer 2 extracts its opaque handle when making the declared foreign call. A record that intentionally crosses by value or reference needs its own measured representation and supported marshaling contract, as described in `Typed_Display_Bindings.md`.

## What Must Not Appear in Layer 3

### 1. No C Type Names

C typedef and marker names belong to the boundary. Application-facing types describe the application operation or value. Numeric values use `int` and `float`; the descriptor beside each extern declares the C signedness, width and calling convention.

| C concept | Boundary responsibility | Application-facing form |
|---|---|---|
| Error enum | Interpret its declared integer value | A Clef error case or `Result<unit, string>` |
| Native object | Own and retire its opaque handle | A binding-owned type such as `Window` |
| Nullable return | Convert absence at the boundary | An `Option` or `Result` of the binding-owned type |

### 2. No C Memory Idioms

Applications do not call native allocation, release, pointer conversion or memory-copy primitives. Layer 2 pairs each acquired native resource with its release point and converts payloads into bounded Clef values. No layer writes `NativePtr`, `.Pointer`, `nativeptr`, `voidptr`, or `nativeint`-as-pointer as a source mechanism; the current language forbids that interior pointer model.

### 3. No C Resolution Idioms

Applications do not declare `[<FidelityExtern>]`, ABI descriptors, `dlsym` lookups, symbol-name callback registration, or native protocol-name dispatch. Layer 2 accepts a Clef operation or handler. A native entry is obtained from its declared module function through `FnPtr.ofFunction`, not by asking the application to publish a C symbol.

### 4. No Native Callback Plumbing

Native instance arguments, userdata, release notifications, `FnPtr` listener records and C handle payloads stay in Layer 2. The application handler receives converted values such as `int array` plus a valid count. An opaque handle is safer than a raw pointer, but passing an unexplained native handle into the application still leaves the marshaling work unfinished.

A binding-owned type may privately retain opaque handles. Its public operations must enforce the resource's usable lifetime and stop invoking application handlers after retirement. A private record representation does not prevent a stale public value from being reused; the binding must establish and test its retirement behavior.

### 5. No C String Patterns

Layer 2 distinguishes NUL-terminated inputs from byte sequences with an explicit length. It accepts Clef text or bounded bytes, performs the required conversion, and releases any native allocation. Application code never supplies an address or remembers a native terminator convention. A message containing an embedded NUL must not silently become a different message because a boundary copy used `strlen`.

## The Static Binding Elision Test

Every dynamic binding pattern must answer: **What happens when this library is statically linked?** A native symbol that can be declared directly should use its extern declaration. Changing the link strategy must not require the application to change its callback or payload code.

| Boundary operation | Dynamic linking | Static linking |
|---|---|---|
| Declared native function | Linker resolves the shared-library symbol | Linker resolves the archive symbol |
| Declared native callback entry | Address of the ABI-correct entry | Address of the same ABI-correct entry |
| Binding-owned payload conversion | Declared native calls and Clef conversion | The same conversion, eligible for ordinary optimization |
| Future captured callback | Native trampoline plus owned environment | The same ABI and lifetime contract |

An interior closure code address is not sufficient evidence for a C callback address. Its parameter representations, environment convention and logical result must first be adapted to the exact native signature, including C `void`.

## Extern Data Declarations (Roadmap)

Some legacy bindings resolve exported data, such as Wayland interface symbols, through `dlsym`. A declared extern-data form would allow direct linker resolution. That remains a separate compiler capability: a source value carrying `[<FidelityExtern>]` is not evidence that extern-data lowering exists. Any eventual surface must carry an opaque handle or another declared native representation, not a source integer address. Applications see only the Layer 2 operation that uses the data.

## Closure Roadmap for Callbacks

The implemented native callback path accepts named module functions without captures. `FnPtr.ofFunction` supplies the entry through a listener record whose `CallbackDescriptor` declares the complete C ABI. For application-facing callbacks, a binding can statically specialize a closed adapter using `ClosedCallbackDescriptor`, described below. Neither path allocates a captured environment or installs a runtime callback registry.

General captured callbacks require more: a typed environment, a lifetime spanning native registration and invocation, and a native trampoline that converts the C arguments before invoking the interior Clef callable. The closure's interior code pointer must not be substituted directly into a C callback slot merely because its environment is empty. Exact parameter and result representations remain a separate obligation.

## Representation

Idiom containment identifies which layer owns an operation. Representation specifies the value that actually crosses the native call boundary, its extent, its owner and its release point. Both are required.

### Callback Tiers

The native API shape determines the lifetime contract a complete captured-callback implementation must discharge:

| Tier | C surface shape | Required contract |
|---|---|---|
| **A** | Userdata plus destroy hook, such as GLib signals | Retain the actual captured environment; native teardown invokes a declared release entry exactly once |
| **B** | Userdata without a destroy hook, such as Wayland listeners | Retain the environment through registration and every invocation; explicit disconnect or destruction retires and releases it |
| **C** | No userdata, such as `qsort` or `atexit` | Closed module functions only, exposed through an entry matching the complete native ABI |

Tier A/B captured-environment retention, release and linear registration ownership remain future work in Composer's `docs/PRDs/C-01-Closures.md` §6.7 and §10.8. Current bindings use the closed Tier C form even when the C API has Tier A/B plumbing. They may supply a real binding-owned nonnull token as userdata and a declared release notification, but no captured environment exists for that notification to release. Native objects still require their ordinary binding-owned cleanup.

### The Destroy Hook Gets the Full Loop

A future Tier A implementation must pair successful registration with retention of the actual environment and wire the C destroy hook to its release entry. A failed registration must undo any retention already performed. A no-op destroy callback is valid for a closed entry with no owned environment; it does not demonstrate captured-closure lifecycle support.

A future Tier B implementation must connect registration, every invocation, retirement and release to one owner. An explicit registration handle is useful only if its consumption and the callback's last use establish that lifecycle. Automatic linearity or leak freedom must not be claimed without compiler and native evidence. Current closed bindings separately retain any native instance or token for as long as C can invoke their entries.

### Per-Typedef Trampolines

Each callback typedef supplies one complete native ABI contract. A trampoline must receive that exact C signature, convert native payloads inside Layer 2, call the interior Clef handler, and convert its result back to the declared native result. In particular, a logical Clef `unit` result requires a C `void` entry when the descriptor declares `Void`.

Closed specialization can emit a distinct entry for each selected handler while sharing the typedef's ABI contract. Captured callbacks would additionally recover a real retained environment from userdata and call the interior closure using its own calling convention. This is a native-to-interior adaptation; neither the closure record nor its code pointer is automatically a C-compatible argument or entry. A transient address of a Clef slot cannot stand in for an owned native handle or retained environment.

### Closed Application Handler Adapters

`BAREWire.Descriptors.ClosedCallbackDescriptor` declares the supported static adaptation using four literal fields:

| Field | Meaning |
|---|---|
| `Binding` | Fully qualified one-argument factory accepting an ordinary handler and returning the native listener record |
| `Adapter` | Fully qualified binding-owned module function taking the handler first, followed by every native entry argument |
| `Record` | The listener record identified by its existing `CallbackDescriptor` |
| `Field` | The record's sole typed `FnPtr` field |

The factory and adapter are immutable module functions without captures. The adapter's type after removing its first handler parameter must equal the listener field's complete entry type. An inline public wrapper exposes the ordinary handler while keeping the factory, adapter and native record inside the binding. A reachable factory call must identify a known immutable module handler; the compiler specializes the adapter for that factory/handler pair and constructs the ordinary listener record with `FnPtr.ofFunction` of the resulting entry. A matching module-level `CallbackDescriptor` remains mandatory: it validates the complete native parameter and result representations. `ClosedCallbackDescriptor` does not replace or relax that ABI contract.

This mechanism retains no handler environment and uses no callback registry. It does not admit captured application functions or nullable native entry values. The binding author must still convert native payload handles, copy bounded bytes where necessary, release native allocations, and retire message dispatch with the underlying resource. The application receives the resulting Clef values through its ordinary handler.

The precise declaration constraints and `CCS8096` rejection cases are documented in the sibling clef repository's `docs/fidelity/Closed_Native_Callback_Adapters.md`. Supported native entries remain closed; future captured-environment work must satisfy the separate Tier A/B contracts above.

### String Contracts

For each native text API the binding must identify whether length is explicit, whether a terminator is required, and who owns any returned buffer. `string_parameters` and bounded reference projections express supported crossings; they do not authorize truncating a logical message at an embedded NUL. Native payloads that may contain NUL need a length-preserving conversion before protocol validation. Bounds include any native terminator when that API requires one, and rejection must use the original payload length rather than the number of bytes successfully copied.

### Boundary Integrity

- Every extern has a complete `FunctionDescriptor`; source signatures use `int`, `float`, opaque handles and supported structured projections. Pointer and numeric widths are descriptor facts.
- Every C-invoked entry has a matching `CallbackDescriptor`, including completion and release entries. Its selected handle inputs and results are nonnull; unsupported nullable entry contracts are rejected rather than represented as option-typed native entry parameters.
- Logical records, arrays and options cross only through the compiler's declared marshaling operations. No global nullable representation is assumed for interior Clef values.
- C handle payloads and callback plumbing remain in Layer 2 until converted into the application's values and operations.
- Native resource ownership, failure cleanup and retirement are part of the binding contract, not facts inferred from a successful type check.

### The Composer-Side Contract

Composer's `docs/PRDs/C-01-Closures.md` §6.7 describes the full future boundary crossing as a joint constraint over the Clef value, C ABI and lifetime owner. Current `CallbackDescriptor` and closed-adapter validation discharge the declared closed-entry subset. They do not establish captured-environment retention, arbitrary higher-order function-pointer transport, or a complete lifetime proof. Native acceptance must test the actual registration and invocation route used by the binding.

## The Binding-Port Spectrum

From the MFEM roadmap: libraries exist on a spectrum from pure binding (opaque service) to full port (algorithm re-expressed in Clef).

| Library | Product | Boundary implication |
|---|---|---|
| libdrm, libgbm | Binding | C stays inside Layer 2; Layer 3 receives Clef values |
| ROCm/HIP | Binding | The same boundary around an opaque GPU runtime |
| Wayland | Binding | Protocol dispatch belongs to Layer 2 |
| DuckDB (static) | Binding | The same declaration with a static link strategy |
| MFEM mesh I/O | Binding | Native I/O remains behind the membrane |
| MFEM kernels | Port | The algorithm is re-expressed in Clef |

For bindings, Layer 2 must discharge the boundary contract before passing values into Layer 3. During a partial port, every remaining native operation still needs the same contract; moving other code into Clef does not remove that obligation.

## Verification Checklist

For each generated binding and authored Layer 2 overlay:

- [ ] Native declarations have complete descriptors with exact parameter count, signedness, widths, passing modes and return convention.
- [ ] Clef source uses `int` and `float`, opaque boundary handles and supported structured projections; it contains no width-named numeric types, literal suffixes or raw-pointer source operations.
- [ ] Every C-invoked callback, completion and release entry is tied to its complete `CallbackDescriptor`.
- [ ] A closed adapter uses immutable module declarations and a known closed handler, and still has the matching native ABI descriptor.
- [ ] Application-facing handlers receive converted Clef payloads, with no native userdata, listener records, C marker names or release plumbing.
- [ ] The binding preserves message length, validates bounds and handles embedded NUL before protocol decoding.
- [ ] Acquired native objects and buffers are released on success and failure; retirement stops application dispatch before handles become unusable.
- [ ] Native probes consume every callback argument and verify scalar widths, nonvoid results, void entries and the actual wrapper-to-extern route.
- [ ] Declared lifecycle behavior has native teardown evidence; closed-handler support is not presented as captured-environment support.
- [ ] Generated and authored files identify their layer, and accepted consumer changes are recorded when regenerated signatures change.

`tests/native-callbacks/run.py` supplies the direct-callback ABI gate. Library-specific UI, payload and teardown gates remain necessary for the corresponding Layer 2 overlay.
