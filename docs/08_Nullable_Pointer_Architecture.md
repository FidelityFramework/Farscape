# 08 — Nullable Pointer Architecture

Null exists only at the foreign boundary. Interior `CHandle` and `FnPtr` values
are nonnull; absence is represented by an ordinary Clef `Option`. Converting a
native nullable word into that option requires an admitted boundary adapter.
The [2026-10-03 checkpoint](handoff/2026-10-03-ffi-boundary-checkpoint.md) records
the completed generator correction and the separate native realization limits.

## Data-pointer calls

The generator and the updated `clef-lang-spec/spec/ffi-boundary.md` agree on
conservative defaults: unannotated data-pointer arguments and returns use
`Option`, including opaque typedef aliases, callback inputs/results and
value-record fields. Clang annotations and explicit pilot assertions can narrow
the native contract to nonnull. Missing annotations provide no such evidence.

| Native use | Typed Clef boundary surface |
|---|---|
| Nullable opaque object or data pointer | `option<CHandle<T>>` |
| Proven nonnull opaque pointer | `CHandle<T>` |
| Nullable text input with `string_parameters` | `option<string>` |
| Proven nonnull text input with `string_parameters` | `string` |
| Writable pointer output cell with a reference projection | `option<CHandle<T>> array`, with its native representation declared beside the extern |
| Unannotated callback data-pointer input or result | `option<CHandle<T>>`, requiring a native-to-interior adapter |
| Explicitly nonnull callback data-pointer input or result | `CHandle<T>`, with the declared native signature |

Widths and C storage layouts belong to descriptors. Signatures use `int`, `float`, sanctioned handles and Clef containers. `nativeint`, `nativeptr`, pointer arithmetic and width-named numeric types are not source-language alternatives.

## Nonnull evidence

Clang `NonNullAttr` supplies zero-based parameter indices, and
`ReturnsNonNullAttr` asserts a nonnull result. `_Nonnull`, `_Nullable` and
`_Null_unspecified` evidence is retained through aliases and nested signatures.
The parser obtains semantic attributes from clang's text AST where the JSON AST
omits their argument positions. Pilots can declare contracts the header omits:

```toml
[annotations.nonnull]
resvg_render = [0, 4]
resvg_options_set_dpi = [0]
nonnull_returns = ["resvg_options_create"]
```

These annotations are native-contract assertions. They do not check an arbitrary
C return value or establish liveness, ownership or spatial bounds. A checked
nonnull conversion must be declared if the foreign value cannot be trusted.
Typedefs are resolved before pointer classification, and opaque typedef identity
is preserved in the carrier.

## Conversion and representation

The boundary contract requires outgoing `None` to become native null and a
nullable native result to become the appropriate interior option. Optional text,
reference cells and logical records require their declared conversion, bounds,
access, retention and copyback obligations. Metadata describes those obligations;
it does not establish that every adapter has been implemented. Demanded
unsupported foreign calls are rejected by the compiler. See the checkpoint and
`10_Boundary_Marshaling_Spec.md` before accepting regenerated bindings.

No generated binding may assume an interior generic option has the layout of one
C pointer word. The specification distinguishes a desired optional native
carrier from the interior option layout. Native callback arguments must be
adapted to interior values; renaming a parameter or ignoring it does not supply
that conversion.

## Callback entries

A callback's `CallbackDescriptor` declares every native input and the result.
Its typed record field uses `FnPtr`; `FnPtr.ofFunction` accepts a closed module
function. Unannotated data pointers in that logical signature remain optional,
including instance and user-data arguments. Native scalar widths and C `void`
come from the descriptor. Complete parameter/result adaptation and the selected
target ABI must be settled by Baker and published in the PSG before native
witnessing; that publication remains incomplete at this checkpoint.

GObject signal generation retains conservative nullable data-pointer inputs and
results. It checks native connection/release slots against the actual header,
uses the selected model's pointer width, and requires known nominal declarations
for payloads. Unsupported or contradictory demanded contracts are rejected
before binding output is written. Generated connection and release declarations
remain in Layer 2; no captured environment is allocated or destroyed by the
closed callback form.

A nullable function-pointer slot differs from nullable data received by a
callback. The specification permits `Option<FnPtr<F>>` at that boundary, but an
unsupported optional entry adapter cannot be replaced with a nonnull assertion.
Unknown/nullable foreign entry results and nested entry slots are refused where
the adapter is missing. A supplied nonnull callback parameter is a narrower
input contract; its native realization still requires the full ABI publication.

## Application boundary

Native instance parameters, user data, string copying and native release belong to the binding. The application consumes the converted payload and domain operations. A descriptor that establishes a C calling signature alone does not establish that public adaptation; `10_Boundary_Marshaling_Spec.md` governs both containment and lifetime ownership.
