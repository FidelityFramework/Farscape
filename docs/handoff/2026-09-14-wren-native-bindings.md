# Handoff: WREN native bindings, Farscape signal bridge, WrenHello numeric-model migration

**Historical handoff.** The [landing report](2026-09-14-wren-native-landing.md) records the subsequent architectural repairs, actual working-tree baseline, acceptance evidence, remaining findings, and revised commit lines. The state and verification claims below describe the earlier session.

State as of 2026-09-14. Everything described here is **uncommitted** in the working trees named below. Nothing has been committed or pushed. The owner reviews and commits all changes.

This document is written for an engineering agent taking over the work. Read sections 1 and 2 before touching anything. Section 5 lists defects in the current uncommitted work that must be fixed before any of it is proposed for commit.

## 1. Owner's standing rules

- The owner makes every commit. Never commit, push or tag. When a changeset is ready, supply exactly one line as the proposed commit message: `type(scope): what`.
- The framework repositories and sample projects (Fidelity.*, Farscape, BAREWire, WrenHello, HelloWayland, the Ariel tests) are the oracle. Do not modify them while probing. Probe in a scratch directory.
- Read the design documents before reading or changing code. Assessment of what is in place, missing and wrong comes before edits.
- No workarounds. A gap in a binding is fixed in the generator or the declaration that owns it, not in application code.
- Marshaling at the boundary is critical. No C pattern may leak above Layer 2 of a binding: no `[<FidelityExtern>]`, descriptors, `dlsym`, `NativePtr`, `.Pointer`, `nativeint`, C type names or user-data plumbing in application code.
- **Null exists only at the FFI boundary and is converted there.** Interior Clef never represents null. The compiler will never admit a nullable value at a callback entry. Do not describe this as a limitation, a gap, or something a future compiler change will relax.
- Plain technical register. When raising a tangent, state which question it answers and close it.
- Markdown: one logical line per paragraph or bullet, no hard wraps.

## 2. Architecture you must hold

Normative sources, read in this order:

1. `~/repos/clef-lang-spec/spec/ffi-boundary.md`: `CHandle<'T>` and `FnPtr<'F>` are never null in interior Clef; null is converted through `Option` only at the boundary; `FnPtr.ofFunction` takes a module-level function without captures.
2. `~/repos/clef-lang-spec/spec/numeric-selection.md`, `width-inference.md`, `platform-bindings.md`: source code spells `int` and `float` only; widths come from range analysis, the platform description's representations, and the descriptor beside each extern. Width-named types (`byte`, `uint32`, `nativeint`) and literal suffixes are rejected (CCS8706, CCS8018).
3. `~/repos/Farscape/docs/10_Boundary_Marshaling_Spec.md`: The Rule (no C idioms in Layer 3 or application code), the layer model, and the Representation section: callback tiers A (userdata plus destroy hook, the GLib signal shape), B (userdata, no destroy hook, Wayland listeners) and C (no userdata, closed functions); per-typedef trampolines; boundary integrity. Trampolines and closure-as-userdata are future Composer work (`~/repos/Composer/docs/PRDs/C-01-Closures.md` §6.7, §10.8), so every tier runs today in the Tier C form: a closed module function.
4. `~/repos/Farscape/docs/Typed_Display_Bindings.md`: the typed native binding form (one-kind signatures, `FunctionDescriptor` beside every extern, `CallbackDescriptor` binding a listener record field to its C entry signature, projections).
5. `~/repos/Farscape/docs/07_Pilot_Project_Setup.md` (pilot TOML schema) and `docs/08_Nullable_Pointer_Architecture.md`.
6. `~/repos/BAREWire/src/Descriptors/Bindings.fs`: the descriptor vocabulary (`FunctionDescriptor`, `CallbackDescriptor`, `ParameterInfo`, `TypeRef`, `PassBy`).

Compiler readers of those declarations (clef tree at `~/repos/clef/src/Compiler`):

- `PSGSaturation/SemanticGraph/CallbackDeclarations.fs`: binds a `CallbackDescriptor { Record; Field; Signature }` to reachable record constructions whose field is `FnPtr.ofFunction f`. An entry input must be a scalar with a declared width or an opaque handle declared `Pointer 64`; anything else is CCS8207 ("A native callback parameter needs a matching scalar declaration or an opaque handle representation"). Record names match exactly, else by short name; two records with the same short name produce an ambiguity finding.
- `PSGSaturation/SemanticGraph/FunctionPointers.fs`: a descriptor-bound void entry gets a native entry thunk symbol; any other `FnPtr.ofFunction` target is emitted under its Clef symbol with its Clef parameter types.
- `PSGSaturation/SemanticGraph/PlatformResolution.fs`: `readParameter`, `readTypeRef`, `readFunctionForBinding`, `recordTypesNamed`.

Observed compiler facts (verified by compiling on the September Composer):

- An extern argument typed `option<FnPtr<_>>` is rejected: "Nullable foreign arguments require an opaque handle or a string adapter". `option<CHandle<_>>` and `option<string>` extern arguments are converted at the call site.
- On CPU an `option` is a tagged memref. A C caller invoking a Clef entry whose parameter is `option<CHandle<_>>` delivers a raw pointer word into that tagged parameter. No diagnostic is emitted on the descriptor-less path. A review verifier reproduced a SIGSEGV with this shape.

## 3. Toolchain and commands

- **Composer**: use `dotnet ~/repos/Composer/src/bin/Debug/net10.0/Composer.dll compile <fidproj> -o <out>` (version `0.0.2+e8cd06e`, built from the current tree; enforces the numeric model). The global `composer` dotnet tool is the March 2026 build and cannot compile any of this work.
- **Farscape**: build `dotnet build ~/repos/Farscape/src/Farscape.Cli/Farscape.Cli.fsproj -c Debug`; generate `dotnet ~/repos/Farscape/src/Farscape.Cli/bin/Debug/net10.0/Farscape.Cli.dll project --project <pilot.toml>` (output directory is relative to the pilot file); test `dotnet test ~/repos/Farscape/tests/Farscape.Tests/Farscape.Tests.fsproj`. Current result: 593 passed.
- **Running GUI binaries** from a shell: `XDG_RUNTIME_DIR=/run/user/1000 WAYLAND_DISPLAY=wayland-1 DISPLAY=:0`.
- **Regression check for generator changes**: copy each accepted pilot in `~/repos/Fidelity.Platform/Environments/Linux/x86_64/pilot/` (`display.native`, `gbm.native`, `pthread`, `resvg.native`, `wayland.native`) to a scratch directory with `directory` rewritten to scratch, generate, and `diff -r` against the committed `Bindings/<Name>`. Before the signal work, resvg differed from the committed package only in one marker doc comment and a trailing blank line. The other four have not been diff-checked against the current generator.
- **Acceptance path for a binding change**: generate, compile a small probe on the September Composer, run it. A probe must exercise every callback argument it receives; a callback that ignores its arguments hides entry ABI defects (see 5.1).
- A bridge probe exists at `/tmp/claude-1000/-home-hhh-repos-Xantham/39cd6d6f-f85d-49e0-a75a-de09ab43c81b/scratchpad/wren-native/probe2/` (`WrenProbe2.fidproj`, `Probe.clef`): a GTK window and WebKit view whose page posts `A|5`, `A|-2`, `X`; the native side sums to 3 and exits 0. It is in `/tmp` and may be gone.
- The adversarial review output (38 findings, 20 confirmed) is at `/tmp/claude-1000/-home-hhh-repos-Xantham/39cd6d6f-f85d-49e0-a75a-de09ab43c81b/tasks/wrfppb925.output` (JSON, `result.confirmed`). Also in `/tmp`.

## 4. What was done, by repository

### 4.1 Farscape (`~/repos/Farscape`)

Pre-existing owner edits, not part of this work, leave untouched: `docs/09_Library_Verification.md`, `docs/Native_Carrier_Bindings.md`, `docs/roadmap/00_farscape-maturation-plan.md`, and two path lines in `src/Farscape.Core/BindingGenerator.fs` (`CPU/Linux/x86_64` to `Environments/Linux/x86_64`).

Changes made:

1. `FidelityCodeGenerator.fs`: `string_parameters`, `parameter_handles` and `return_handle` checks and pointer nullability now resolve typedefs (`isDataPointerIn`, `isCharPointerIn`), so `const gchar *` and `gpointer` project. An enum-typed parameter is spelled `int` (`clefTypeOf` `CEnum` case).
2. `CppParser.fs`: an anonymous `EnumDecl` takes the name of its typedef (clang `EnumType`), as records already did. Fixes `GtkWindowType`/`GConnectFlags` arriving as undeclared types.
3. `TypeMapper.fs`: scalar rows for `signed long`, `signed long int`, `signed long long`, `signed long long int` (`gssize`).
4. New GObject signal path, the source of a signal's entry signature, since `GCallback` is `void (*)(void)`:
   - `PilotTypes.fs`, `PilotSerializer.fs`: `[library] introspection = ["*.gir"]` and `[[namespace]] signals = ["Class::signal-name"]`. Construction sites updated in `Program.fs`, `PilotDiscovery.fs`, `PilotAnalyzer.fs` and tests.
   - `PilotDiscovery.fs`: `GObjectIntrospection` for a `<repository>` root.
   - New `IntrospectionParser.fs`: reads the first `<namespace>` of each `.gir`; maps class/interface/record/union names to `c:type*`, enumeration/bitfield/alias names to `c:type`; reads `<glib:signal>` on classes and interfaces with parameter `nullable`/`allow-none`; `select` resolves `Class::signal` to C spellings through the listed namespaces.
   - New `TypedSignalGenerator.fs`: writes `Bridge/Signals.clef` per package. Per signal: a listener record with one field named after the signal (`{ ScriptMessageReceived: FnPtr<CHandle<unit> -> CHandle<unit> -> CHandle<unit> -> unit> }`: instance, parameters, user data); a `CallbackDescriptor`; a private `[<FidelityExtern("gobject-2.0","g_signal_connect_data")>]` with descriptor; a public `connect<Class><Signal> (instance) (listener) : int` that passes the instance handle as user data and `FnPtr.ofFunction releaseHandler` as the destroy notify. A signal whose GIR marks a parameter nullable is rejected at generation.
   - `BindingGenerator.fs`: loads introspection files, emits `Signals.clef` into the manifest, adds `gobject-2.0` to `[link]` when `link_libraries = true` and signals are selected.
5. Tests: `IntrospectionTests.fs` (new, 3 tests), a typedef-projection test in `PthreadBindingTests.fs`, an anonymous-enum test in `CppParserDetectionTests.fs`.
6. Docs: `docs/07_Pilot_Project_Setup.md` new section "`[library] introspection` and `[[namespace]] signals`"; `docs/14_Binding_Generation_Gaps.md` "Addendum 2026-09-14" (fixed items 1-5, a "By design" paragraph, open items).

### 4.2 Typed native packages (new, untracked, beside the March 2026 packages)

Each is generated from a `*.native.pilot.toml` in its repository with `c_header_mode`, `descriptor_only_dependencies`, `link_libraries`, `/usr` first in `include_paths`, and GLib headers listed explicitly.

- `~/repos/Fidelity.Gtk3/CPU/Linux/x86_64/`: `pilot/gtk-3.native.pilot.toml`, `Bindings/Gtk3Native/` (Types, Native, Bridge/Signals), `Fidelity.Gtk3.Native.fidproj`. Functions: `gtk_init`, `gtk_window_new`, `gtk_window_set_title`, `gtk_window_set_default_size`, `gtk_window_maximize`, `gtk_window_unmaximize`, `gtk_container_add`, `gtk_widget_show_all`, `gtk_main`, `gtk_main_quit`. Signal: `Widget::destroy`.
- `~/repos/Fidelity.GObject/CPU/Linux/x86_64/`: `pilot/gobject-2.0.native.pilot.toml`, `Bindings/GObjectNative/`, `Fidelity.GObject.Native.fidproj`. Namespace `Fidelity.GObject.Native` (`g_signal_connect_data`, raw form) and `Fidelity.GObject.Native.GLib` (`g_free`, `copyString` = `g_strlcpy` with a `uint8_t` reference buffer). The pilot also carries an inert `[[callbacks.registrations]]` stanza.
- `~/repos/Fidelity.WebKit/CPU/Linux/x86_64/`: `pilot/webkit2gtk.native.pilot.toml`, `Bindings/WebKitNative/` (Types, Native, Jsc, Bridge/Signals), `Fidelity.WebKit.Native.fidproj`. Functions: user content manager, view, settings, script-message handler registration, `load_html`, `evaluate_javascript`, `javascript_result_get_js_value`, `jsc_value_to_string`. Signal: `UserContentManager::script-message-received`.
- Each repository's `README.md` gained a "Typed native package" section. The March packages and their pre-existing modified fidprojs and `.gitattributes` were not touched.

### 4.3 Fidelity.Platform (`~/repos/Fidelity.Platform`)

- Relocation of the pure RFC 6455 protocol source from `Environments/Linux/x86_64/WebSocket/` to `Protocols/WebSocket/` (`Frame.clef`, `Handshake.clef`, `Types.clef`, new `README.md`); the socket-bound `WebSocketServer` and `SockAddrIn` split into a new `Environments/Linux/x86_64/WebSocket/Types.clef`; `Server.clef` header and opens updated; `PLATFORM_STRUCTURE.md` and `Protocols/README.md` updated. None of these files is in a fidproj and none compiles under the numeric model (see 7.2).
- New profile `Profiles/Linux_x86_64_WrenHello/` (`Description.clef`, `Fidelity.Platform.fidproj`, `README.md`): the default Linux budgets with `rodata` capacity 131072 (32 pages) because WrenHello's embedded HTML literal overflows the default one-page budget (CCS8206). A row was added to `PLATFORM_STRUCTURE.md`.

### 4.4 WrenHello (`~/repos/WrenHello`)

- `WrenHello.fidproj` (version 0.3.0): depends on the WrenHello profile and the three native packages; `[link]` lists `gtk-3`, `gobject-2.0`, `glib-2.0`, `webkit2gtk-4.1`, `javascriptcoregtk-4.1`.
- `src/Backend/Codec.fs`: decode from a bounded `int array` of message bytes; clamped counter; its own decimal formatter.
- `src/Backend/Main.fs`: module-level mutable state; annotated module-level listener records; `connectUserContentManagerScriptMessageReceived manager messageListener` and `connectWidgetDestroy w destroyListener`; message text copied with `copyString` then released with `g_free`; events pushed with `webkit_web_view_evaluate_javascript` and a completion callback `evaluateDone`.
- `README.md` toolchain and bridge sections and `docs/composer-findings.md` (new 2026-09-14 section) updated.

### 4.5 Other repositories

- `~/repos/BAREWire/docs/Readiness Audit.md`: WebSocket source path updated.
- `~/repos/Conclave/docs/roadmap/W-03-localhost-websocket-bridge.md` and `docs/11-sources.md`: WebSocket paths updated. The Conclave tree also carries unrelated owner changes.
- `~/repos/HelloWayland`, `~/repos/clef`, `~/repos/Composer` carry owner modifications that are not part of this work.

### 4.6 What is verified and what is not

- Verified: Farscape tests pass (593). WrenHello compiles on the September Composer and the binary stays up with its window. The bridge probe through the generated connect ran the full message loop and exited 0.
- Not verified: WrenHello was not exercised through its real UI. The probe's completion callback ignores its arguments, so it cannot detect the entry defect in 5.1. The four accepted Fidelity.Platform native pilots other than resvg were not regenerated and diffed after the generator changes.

## 5. Confirmed defects in the uncommitted work

From an adversarial review (five lenses, two refuters per finding). Ordered by severity. Line numbers refer to the current working trees.

### 5.1 Blocker: callback typedef inputs are generated as `option<CHandle<_>>`

- Where: `Farscape/src/Farscape.Core/FidelityCodeGenerator.fs:173-178`. `nullableClef` wraps every pointer parameter and pointer result of a C callback typedef in `option`; `functionPointer` applies it. Only the `nonnull_callbacks` projection strips it, per extern parameter. The code predates this work (commit d29ba7c) but the new packages are the first to exercise it.
- Effect: any Clef function supplied for such a `FnPtr` has tagged-memref option parameters; C delivers raw pointer words. This is null entering the interior, and it crashes (reproduced: SIGSEGV).
- Instances: `Fidelity.WebKit/.../WebKitNative/Native/Native.clef:140` (`callback: FnPtr<option<CHandle<GObject>> -> option<CHandle<GAsyncResult>> -> option<CHandle<unit>> -> unit>`, and `user_data: option<CHandle<unit>>`); `Fidelity.GObject/.../GObjectNative/Native/Native.clef:10` (`destroy_data`); `WrenHello/src/Backend/Main.fs:35-48` (`evaluateDone` with option parameters, called with `None` user data, so NULL reaches its third parameter).
- Tests asserting the defective shape: `tests/Farscape.Tests/PthreadBindingTests.fs:67`, `tests/Farscape.Tests/FidelityCodeGeneratorTests.fs:71`.
- Fix direction: callback entry inputs and results are `CHandle<_>`, never `option`. Update the two tests. In the WebKit pilot, declare `evaluate_javascript`'s `user_data` nonnull and have WrenHello pass a real handle (the view). Diff the accepted Fidelity.Platform native pilots before and after; pthread already uses `nonnull_callbacks` and should be unchanged.

### 5.2 C-invoked entries passed directly to an extern have no `CallbackDescriptor`

- Where: `Fidelity.WebKit/.../Native.clef:140` (`GAsyncReadyCallback`), and `releaseHandler` in both generated `Bridge/Signals.clef` files.
- Effect (from reading `FunctionPointers.fs:50-53` and Composer's witnesses; not reproduced): without a descriptor the entry is emitted under its Clef symbol with Clef parameter types and never checked against the C signature. Handle-only entries are pointer-width either way; scalar inputs are at risk.
- Fix direction, needs owner confirmation of shape: docs/10 specifies one trampoline per callback typedef. Generate an entry record plus `CallbackDescriptor` per callback typedef used by an emitted extern parameter, and for the signal release notify (`GClosureNotify`: two data pointers, void). The application constructs the record with `FnPtr.ofFunction`. Before choosing record names, verify whether the compiler's record type names are module-qualified (`NativeTypedTree/NativeService.fs`, `mkRecordTypeConRef typeName ctx.Path`); `recordTypesNamed` matches exactly then by short name, so short names shared across packages are ambiguous.

### 5.3 Signal descriptor `CName` collides with real C symbols

- Where: `TypedSignalGenerator.fs` (`entryName = SymbolPrefix + "_" + snake name`); e.g. `Gtk3Native/Bridge/Signals.clef:13` declares `CName = "gtk_widget_destroy"`, a real one-argument GTK function. Also collides for `show`, `hide`, `realize` and others.
- Fix direction: a form that cannot be a C identifier. GObject's own spelling is `GtkWidget::destroy`; the Wayland generator uses `wl_registry.global`.

### 5.4 Float-bearing signals produce declarations the compiler rejects

- Where: `TypedSignalGenerator.fs:33` emits `float`/`Float 64` carriers; `PlatformResolution.readTypeRef` yields no declared parameter for `Float`, so `CallbackDeclarations` rejects the entry.
- Fix direction: reject at generation with a message naming the parameter, as the nullable case does.

### 5.5 Nullable signal return values are not read

- Where: `IntrospectionParser.fs:94` reads only the `<type>` of `<return-value>`; its `nullable` attribute is ignored. Example: `Gdk-3.0.gir` `pick-embedded-child` returns nullable `Window`.
- Fix direction: carry return nullability and reject such signals at generation.

### 5.6 Signal selection is ambiguous across namespaces

- Where: `IntrospectionParser.fs:181-183` picks the first namespace in map order declaring `Class::signal`; the selector admits no namespace.
- Fix direction: accept `Namespace.Class::signal-name`; report an error when an unqualified selector matches more than one listed namespace.

### 5.7 Lost test coverage

- Where: `tests/Farscape.Tests/IntrospectionTests.fs:94`. The last edit replaced the assertions for an enum parameter, a scalar parameter and a non-void result with a nullable-rejection check. `carrierOf`'s `CEnum` and `Scalar` branches are untested.
- Fix direction: add a fixture signal with an enum parameter and a `gboolean` result and no nullable parameter; add tests for 5.3 through 5.6.

### 5.8 GObject package publishes an unusable raw connect surface

- Where: `Fidelity.GObject/.../GObjectNative/Native/Native.clef:10` (`g_signal_connect_data` with `FnPtr<unit -> unit>`, option user data, destroy notify, flags); the pilot's `[[callbacks.registrations]]` stanza and its comment at `gobject-2.0.native.pilot.toml:45-46` ("each signal's entry signature is declared by the application"), which is now false.
- Fix direction: remove the raw namespace and the inert stanza; keep a single namespace for `g_free` and `copyString` (update WrenHello's `open`). Delete the untracked generated output directory before regenerating so stale files do not remain.

### 5.9 Documentation inaccuracies

- `docs/07_Pilot_Project_Setup.md:246`: says the manifest links `gobject-2.0`; true only with `[options] link_libraries = true`.
- `docs/07_Pilot_Project_Setup.md:188`: the `[callbacks]` example uses inline tables, which `PilotSerializer.deserializeCallbacks` silently drops (it matches `TomlValue.Table` only; Fidelity.Data parses `{ ... }` as `TomlValue.InlineTable`). Either the serializer reads inline tables (treating the doc as the interface) or the example changes; owner's call.
- `docs/14_Binding_Generation_Gaps.md:494` "By design, not open" paragraph asserts that a function pointer has no absence representation. `clef-lang-spec/spec/ffi-boundary.md` §3.1 lists `Option<FnPtr<'F>>` and §5.5 says generators MUST emit it for nullable callback parameters, while the compiler rejects `option<FnPtr<_>>` extern arguments. Replace the paragraph with the verified facts and raise the discrepancy to the owner (section 6). Do not frame it as the compiler admitting nulls later.
- `WrenHello/README.md:85-92`: the protocol snippet shows named payloads and a one-case `Event`; `src/Shared/Protocol.fs` has positional payloads and `CounterChanged | Acknowledged`.
- `Fidelity.Platform/Profiles/Linux_x86_64_WrenHello/README.md:3` and `Description.clef:10`: "about 49 KiB"; the literal is 48,928 bytes (47.8 KiB).
- `WrenHello/docs/composer-findings.md:114`: "`Format.int` ... is not provided natively" should say that the implementation is `Fidelity.Platform/Environments/Linux/x86_64/Format.clef` in the unmigrated Linux bindings package, which does not compile under the numeric model and is outside WrenHello's dependencies.

## 6. Decisions that belong to the owner

1. The spec/compiler discrepancy on `Option<FnPtr<'F>>` as an extern argument (5.9). The owner's ruling that interior Clef and callback entries never admit null is settled and not part of this question.
2. The shape of declared entries for callbacks passed directly to externs (5.2).
3. Whether narrow `*.native` packages beside the March packages is the intended topology, or the March packages should be regenerated wholesale. Wholesale regeneration needs per-function projections, and Composer's type check did not scale to the full GTK set in August.
4. Whether GObject signal connection and its release entry belong once in the GObject package, with the GTK and WebKit bridges depending on it, instead of private externs per package.
5. The docs/07 inline-table question (5.9).

## 7. Context from earlier in the session

### 7.1 Numeric model assessment

- Platform description (`Environments/Linux/x86_64/Environment.clef`): pointer and register widths and eleven representations are declared and read by the compiler. Complete for bindings. The declared websocket `Transport` is read by nothing in the compiler.
- Accepted migrated bindings: Pthread, DisplayNative, GBMNative, ResvgNative, WaylandNative, and `tests/Ariel/native/CreateJoin`; HelloWayland consumes them.
- Unmigrated: the Linux bindings package `Environments/Linux/x86_64/Fidelity.Platform.fidproj` (`Format.clef`, `WebView.clef` and others), the March Fidelity.Gtk3/GObject/WebKit packages, the `Fidelity.Libc` package.
- The spec's bounded stack array form has no compiler implementation; `int array` plus a descriptor element width is the working byte-buffer idiom.

### 7.2 Remaining roadmap after the defects are fixed

1. WebSocket protocol tier into the model: `Protocols/WebSocket/*.clef` use `nativeptr`, `NativePtr`, width-named types, suffixes and BCL strings. SHA-1 needs explicit 32-bit masking so ranges stay bounded. Put the tier in its own fidproj with handshake and frame round-trip tests.
2. Socket syscall bindings in the typed form, then the Linux server (`Server.clef` calls a `Sockets` module that exists nowhere).
3. WrenHello transport swap: native side as WebSocket server, the WebView's built-in client.
4. BAREWire framing inside binary messages once BAREWire's Encoding tier compiles. Blocked by a design issue: `Cursor.Fault = -1` folded into offsets and unsigned 64-bit lengths gives a range no representation covers (CCS8012). Fault must become a case, not a magnitude, per the boundary-sentinel invariant in ffi-boundary.md.

## 8. Mistakes made in this session, so they are not repeated

- Reasoning from March-era sample code and generator output instead of reading Farscape's docs (docs/10 Representation, docs/07, Typed_Display_Bindings) first.
- Putting per-signal C declarations in WrenHello as a "workaround" instead of fixing the generator.
- Describing non-nullable callback inputs as something the compiler might admit later. It will not.
- First version of the signal bridge passed `None` as user data and typed entry inputs as options; later corrected in the bridge, but the older generator path in 5.1 still does it.
- Accepting a probe whose callbacks ignore their arguments as evidence of entry correctness.

## 9. Proposed commit lines, only after section 5 is fixed and re-verified

- Farscape: `fix(generator): resolve typedefs in projections and name typedef'd enums` and `feat(signals): project GObject signals from introspection data`
- Fidelity.Gtk3: `feat(native): typed GTK 3 window surface with the destroy signal`
- Fidelity.GObject: `feat(native): typed GLib release and string copy surface`
- Fidelity.WebKit: `feat(native): typed WebKitGTK view surface with the script-message signal`
- Fidelity.Platform: `refactor(protocols): move RFC 6455 protocol source to Protocols/WebSocket` and `feat(profiles): Linux x86-64 WrenHello selection with a 32-page rodata budget`
- WrenHello: `feat(backend): migrate to the numeric model on typed native bindings`
- BAREWire: `docs(audit): point WebSocket source reference at Protocols/WebSocket`
- Conclave: `docs: update WebSocket draft paths to Protocols/WebSocket`
