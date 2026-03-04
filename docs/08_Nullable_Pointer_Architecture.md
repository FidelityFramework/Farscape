# 08 — Nullable Pointer Architecture

## Principled Default

C has no non-null guarantee unless explicitly annotated. Absence of proof is not proof of absence.

**Therefore: unannotated pointer parameters default to nullable (`Option<>`).**

`NonNullAttr` is the opt-in to proven non-null, not the other way around.

## Type Mappings

| C Type | Default (unannotated) | With NonNullAttr |
|---|---|---|
| `const char *` | `Option<nativeptr<byte>>` | `nativeptr<byte>` |
| `void *` | `Option<nativeint>` | `nativeint` |
| `struct foo *` | `Option<nativeint>` | `nativeint` |
| `int **` | `Option<nativeint>` | `nativeint` |
| Opaque handle (e.g. `hipStream_t`) | `Option<HandleType>` | `HandleType` |
| Function pointer `void (*)(...)` | `nativeint` | `nativeint` (not a data pointer) |

Return types follow the same rule: pointer returns are `Option<>` unless proven non-null.

## Three Proof Sources for Non-Null

### 1. Clang `NonNullAttr`

Extracted from `clang -Xclang -ast-dump=json`. The attribute carries 0-based parameter indices:

```json
{ "kind": "NonNullAttr", "args": [0, 2] }
```

Parameters at indices 0 and 2 are proven non-null by the compiler.

### 2. Clang `ReturnsNonNullAttr`

For return values. If present, the return type emits without `Option<>`.

### 3. Pilot TOML `[annotations.nonnull]`

Developer-asserted non-null for parameters that clang cannot prove:

```toml
[annotations.nonnull]
# Per-function: list of 0-based parameter indices proven non-null
resvg_render = [0, 4]
resvg_options_set_dpi = [0]

# Return types proven non-null
nonnull_returns = ["resvg_options_create"]
```

Functions not listed: all pointer params are nullable (the default).

## How It Works

### Layer 1 (FidelityExtern declarations)

`FidelityCodeGenerator.generateFunctionDecls` collects non-null indices from both clang attributes
and pilot TOML annotations into a combined set. Any pointer parameter NOT in this set gets
`wrapOption` applied:

```fsharp
let isNullable = isPointer && not (nonnullIndices.Contains idx)
let finalType = if isNullable then wrapOption fsType else fsType
```

### Layer 2 (Idiomatic wrappers)

`WrapperCodeGenerator.generateWrapperDecls` applies identical nullable logic. Wrapper parameters
match Layer 1 signatures exactly, forwarding `Option<>` values directly to the underlying
FidelityExtern call.

### What `isCDataPointer` excludes

Function pointers (`(*)`, `(**)`) are NOT data pointers. They map to `nativeint` without
nullable wrapping. Only actual data pointer types (detected by `*` in the C type string,
excluding function pointer patterns) are subject to nullability.

## Integration with Composer/Clef

The Clef FFI null safety architecture (Composer memory `ffi_null_safety_architecture`) defines:

- `nativeptr<'T>` is **non-nullable** within Clef code
- `Option<nativeptr<'T>>` represents nullable pointers at the FFI boundary
- `None` marshals to `NULL`, `NULL` marshals to `None`
- Null exists ONLY at the FFI boundary — within Clef, pointers are never null

Farscape generates the FFI boundary declarations. By defaulting to `Option<>`, Farscape
correctly expresses that C pointers may be null unless proven otherwise. The Clef type system
then enforces null checking at every use site.

## NTU Type Path

```
nativeptr<byte> → TNativePtr(byte) → NTUKind.NTUptr → TIndex → MLIR index → LLVM i64
Option<nativeptr<byte>> → same path, with None ↔ NULL at FFI boundary
```

The `Option<>` wrapping is resolved at the FFI boundary by CCS/Alex. No runtime overhead —
it compiles to a null check at the call site.
