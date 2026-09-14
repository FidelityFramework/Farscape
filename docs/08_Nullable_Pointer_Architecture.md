# 08 — Nullable Pointer Architecture

Null exists only at the foreign boundary. Interior `CHandle` and `FnPtr` values are nonnull; an `Option` is an ordinary Clef value whose absence is converted at the actual foreign call. A binding must never pass a raw native word to a callback parameter expecting an interior option value.

## Data-pointer calls

The current generator defaults unannotated data-pointer arguments and returns to `Option`. Clang nonnull attributes and explicit pilot declarations remove that wrapper when the native contract establishes nonnullability. This implementation policy differs from `clef-lang-spec/spec/ffi-boundary.md` §5.2, which specifies a nonnull default. Until that policy discrepancy is reconciled, migrated pilots declare the required contracts explicitly; missing annotations are not evidence that a C callback can safely receive null.

| Native use | Typed Clef boundary surface |
|---|---|
| Nullable opaque object or data pointer | `option<CHandle<T>>` |
| Proven nonnull opaque pointer | `CHandle<T>` |
| Nullable text input with `string_parameters` | `option<string>` |
| Proven nonnull text input with `string_parameters` | `string` |
| Writable pointer output cell with a reference projection | `option<CHandle<T>> array`, with its native representation declared beside the extern |
| Native callback entry pointer input or result | `CHandle<T>` under an explicit nonnull native contract |

Widths and C storage layouts belong to descriptors. Signatures use `int`, `float`, sanctioned handles and Clef containers. `nativeint`, `nativeptr`, pointer arithmetic and width-named numeric types are not source-language alternatives.

## Nonnull evidence

Clang `NonNullAttr` supplies zero-based parameter indices, and `ReturnsNonNullAttr` establishes a nonnull result. Pilots can declare contracts that the header annotations omit:

```toml
[annotations.nonnull]
resvg_render = [0, 4]
resvg_options_set_dpi = [0]
nonnull_returns = ["resvg_options_create"]
```

These annotations are native-contract assertions. They do not turn a potentially null C value into a valid interior handle. Typedefs are resolved before deciding whether a parameter or result is a data pointer.

## Conversion and representation

For outgoing optional handles, the compiler converts `None` to `NULL` and `Some handle` to its native pointer. For nullable native results, it constructs the corresponding interior option. Optional text inputs use the string adapter. Reference arrays and records may require native temporaries and copyback according to their descriptors; see `Typed_Display_Bindings.md`.

No generated binding may assume that an interior option has the same layout as one C pointer word. The one-word wording in `ffi-boundary.md` §2.3 must be reconciled with the compiler's rich interior option representation; it is not an entry ABI contract. The conversion belongs at the boundary, independently of any interior representation optimization.

## Callback entries

A callback's native signature is declared with a `CallbackDescriptor`, including every input and the result. The record field supplied through `FnPtr.ofFunction` names a closed module function. Native pointer inputs and results are nonnull handles; scalar widths come from the descriptor. Optional handles are not native callback parameters. A nullable incoming native value requires a real boundary adapter before admission to an interior handler; a signature change or ignored argument does not supply that conversion.

GObject signal generation rejects nullable inputs and nullable results, and rejects floating-point entries that the current callback declaration reader cannot establish. Every rejection identifies the affected signal and value. Generated connection and release entries remain inside Layer 2.

A nullable callback slot is a separate question from nullable callback inputs. The language specification requires `Option<FnPtr<F>>` for a nullable slot, while the September compiler rejects that shape as an extern argument. The current emitted typed surface requires a real declared callback. This is an implementation/specification discrepancy, not a claim that a function pointer lacks an absence type, and it never permits null in an interior entry.

## Application boundary

Native instance parameters, user data, string copying and native release belong to the binding. The application consumes the converted payload and domain operations. A descriptor that establishes a C calling signature alone does not establish that public adaptation; `10_Boundary_Marshaling_Spec.md` governs both containment and lifetime ownership.
