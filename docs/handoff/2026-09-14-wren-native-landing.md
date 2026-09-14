# WREN native landing — 2026-09-14

The application now consumes `Fidelity.WebKit.Desktop`: `create`, `onMessage`, `evaluate`, `maximize`, `restore`, `close`, and `run` over a private `Window`. Its handler receives copied `int array` bytes and a count. Native handles, function pointers, userdata, descriptors, and allocation/release operations are confined to the binding. No commits, pushes, or tags were made.

This report supersedes the initial handoff's working-tree status and acceptance claims. Much of that handoff had already been committed by the owner before this landing began. Baseline heads, status, and diffs were captured in `/tmp/wren-landing-20260914-uhf_q_xp/baseline`; existing unrelated changes remain outside this changeset.

## Architectural decisions

- Native callback entries are nonnull and descriptor-bound. Every direct callback type used by an emitted extern gets a namespace-stable listener record and `CallbackDescriptor`; private externs preserve the complete C arity and public Layer 2 wrappers extract the record field. Release notifications also have explicit native descriptors.
- `ClosedCallbackDescriptor` names a binding-owned factory and adapter template. Clef specializes an immutable closed module handler into an ordinary closed native entry, validated by the existing `CallbackDescriptor` path. There is no callback registry, captured environment, or interior nullable function pointer. Full Tier A/B environment retention and release remain future work.
- Inline expansion preserves definition-site scope, declared argument domains, fresh generic annotations, and exactly-once eager operand evaluation. These compiler fixes make the public binding API lawful; application code does not work around compiler calling conventions.
- Generated signal helpers remove native instance and userdata from handler arguments. Native payload handles still require Layer 2 conversion. The authored desktop overlay owns that conversion and lives in a separate manifest so native regeneration cannot discard it.
- Messages use JavaScriptCore's length-preserving `GBytes` conversion. The binding rejects more than 4096 bytes before copying and releases the native bytes before invoking the handler. Embedded NUL reaches the codec intact and is rejected; C-string truncation can no longer turn malformed text into a valid command.
- One named message registration is supported per window. Repeat registration is rejected before connecting; registration failure disconnects the attempted entry. Retirement clears dispatch before native destruction. Evaluation retains the view through completion, consumes and releases its result, and balances the retained reference. Release drains pending completions and is idempotent.
- Narrow native packages remain the migration boundary. The March packages and accepted Platform binding sources were not regenerated wholesale. WrenHello's application manifest depends only on its platform profile and desktop package; native library identities flow through binding dependencies.

## Initial handoff defects

| Item | Resolution |
|---|---|
| 5.1 Nullable callback entry carriers | Native pointer arguments/results are nonnull `CHandle`; WebKit passes a retained view as real userdata. |
| 5.2 Missing entry/release descriptors | Direct and release callbacks have declared native entries; namespace identity is preserved. |
| 5.3 Signal identity collides with C symbol | Signal identities use `ClassCType::signal`. GTK's real destroy function compiles alongside its signal. |
| 5.4 Floating signal entries | Generation rejects unsupported floating parameters/results with a precise diagnostic. |
| 5.5 Nullable signal results | GIR result nullability is read and rejected, including `allow-none`. |
| 5.6 Ambiguous signal selection | Qualified GIR selectors are supported; ambiguous unqualified selectors fail. |
| 5.7 Lost scalar/enum/result tests | Coverage restored and expanded. |
| 5.8 Raw GObject connect | Removed the unusable projection and inert callback stanza; stale generated `.GLib` output retired. |
| 5.9 Documentation | Inline callback tables deserialize; conditional linking, protocol cases, rodata size, and ordinary `Format.int` binding location corrected. Nullability discrepancies are recorded accurately. |

The boundary specification itself was also corrected: it no longer prescribes `nativeint`/width-named source types, conflates closure code pointers with native entries, or presents future captured-environment lifetime machinery as implemented.

## Verification

- Farscape: **604 tests passed**, none failed or skipped. Final log: `/tmp/wren-landing-20260914-uhf_q_xp/farscape-final-tests.log`.
- Native direct callback gate: both single-namespace and duplicate-namespace executables pass. C supplies unsigned 4294967295 and real handles; callbacks inspect every argument, return signed -7 or record the void result, and C checks them. Retained IR checks the scalar native signature and void thunk. Reproduce with `python3 tests/native-callbacks/run.py`.
- Accepted pilots: Display, GBM, resvg, and Wayland are byte-identical between preserved pre-repair generator output and repaired output. Pre-existing drift against committed output is recorded separately. Pthread's only new API change is the required declared callback record; a fresh CreateJoin probe and a stronger joined-result identity probe both pass. The accepted originals remain unchanged. Evidence: `/tmp/wren-landing-20260914-uhf_q_xp/pthread-probe/ACCEPTANCE.md`.
- All applied GTK/GObject/WebKit generated files exactly match reviewed scratch candidates; no stale generated source remains in those native output directories.
- The actual `WrenHello.fidproj` builds successfully with its transitive desktop dependencies. The native UI gate compiles that actual dependency graph, adds automation to the actual embedded frontend, and checks malformed/NUL/overflow/oversize rejection, real counter buttons and DOM updates, reset, maximize/restore acknowledgments, and close with queued commands. Reproduce from WrenHello with `python3 tests/native-ui/run.py`.

Final frozen-compiler results:

- **238 Clef compiler tests passed**, including 16 new closed-callback and inline regression cases. The separate native adapter witness exits 0 and verifies real C userdata remains inside the binding, the application receives 37, unused effectful operands run once in order (trace 123), generic inline calls instantiate independently, and caller shadowing does not alter definition-site lookup. Evidence: `/tmp/wren-closed-entry.5FbMUg`.
- The final direct ABI gate passes both executables with the frozen compiler. Evidence: `/tmp/farscape-native-callbacks-zfd6k7ia`.
- The final enhanced UI gate compiles and exits 0, with an empty runtime log and unchanged original source hashes. It additionally checks exact 4096-byte acceptance, refusal of repeated same/different channel registration, and repeated operations on a retired window. Evidence: `/tmp/wren-native-ui-4b1tct30/result.json`. Maximize/restore acknowledgments pass; the corresponding compositor geometry change was not established on this Hyprland session, including when the test window was made floating.
- Composer DLL SHA-256: `f39b4147cb57b0fa4fc4dee05af8c21eb4d8907d6428273ca0f140c3763581dd`; copied Clef compiler service SHA-256: `44e24aede7d0a81865c3f9a4fb464f40ede28e0909a9da62420ca150fd1d2022`.
- The broader legacy sample runner is **not a passing gate**: samples 01/02 compiled; 07/08/12/13/18/19 failed on retired Platform `Format.clef` width-named `byte` and suffixed literals. The remaining 16 samples were stopped to honor the owner's capacity limit; no runner execution phase was reached. These sources were not migrated as part of WREN. Logs: `/tmp/wren-closed-entry.5FbMUg/regression-{1,2,3,4}.log`.
- `git diff --check` passes in all nine changed repositories. Application source has no native handles, function pointers, descriptors, userdata, or copy/free operations.

## Compatibility and remaining independent findings

Regenerated direct callback APIs now take an annotated entry record rather than a bare `FnPtr`. Existing binding-owned callers adopting regenerated pthread output must migrate that argument. This source change is required to establish native ABI evidence. The checked-in accepted pthread package was deliberately left unchanged.

`Option<FnPtr<_>>` in extern argument position remains a spec/compiler discrepancy; it does not change the settled prohibition on interior null. Default pointer nullability and the spec's one-word option wording also need separate reconciliation. See `docs/08_Nullable_Pointer_Architecture.md`. These APIs use explicit nonnull contracts and supported actual-boundary option conversions.

Detailed signal connection remains unprojected; the desktop API enforces one registered channel per manager. Floating and nullable signal entries are rejected. General captured callbacks remain unsupported. The desktop API runs on the GTK event thread and does not claim concurrent or nested event-loop support.

A separate compiler defect was observed for a mutable scalar record field initialized with zero and later assigned a native 64-bit id: the emitter selected byte storage and produced a mismatched store. The desktop registration state uses interior `option<int>` for absence and passes native lifecycle tests. The original failed IR location and details are recorded in WrenHello's `docs/composer-findings.md`; the underlying scalar-field issue remains open.

## Proposed owner commit lines

Apply each line to the named repository after reviewing its diff. Clef already contained substantial owner edits; this work's files and portions must be distinguished from that baseline. Composer was rebuilt but its existing JavaScript documentation edits were not changed by this work.

The compiler additions are `Nanopass/ClosedCallbacks.fs`, `ClosedCallbackCases.fs`, their project registrations, and the closed-adapter documentation. Shared-file changes cover specialization integration/diagnostics and module parents, internal access to `cloneSubtree`, and inline scope/type/evaluation semantics in `Applications.fs`, `Bindings.fs`, and `NameResolution.fs`. `Expressions/Types.fs` allocates CCS8096. Preserve other owner changes in those files and the pre-existing compiler work elsewhere; the baseline snapshots distinguish them. BAREWire changes are the descriptor vocabulary and its documentation/index; the language-spec change is the CCS8096 entry.

| Repository | Proposed commit line |
|---|---|
| BAREWire | `feat(descriptors): declare binding-owned closed callback adapters` |
| clef | `feat(callbacks): specialize closed native adapters with lawful inline calls` |
| clef-lang-spec | `docs(diagnostics): define closed callback declaration errors` |
| Farscape | `fix(bindings): declare native callbacks and preserve the Clef boundary` |
| Fidelity.Gtk3 | `fix(native): declare signal entries and support window teardown` |
| Fidelity.GObject | `fix(native): own byte transfer and object lifetime operations` |
| Fidelity.WebKit | `feat(desktop): expose a Clef window and message API` |
| Fidelity.Platform | `docs(profiles): correct the WREN embedded payload size` |
| WrenHello | `refactor(backend): consume the desktop API and verify native UI behavior` |
