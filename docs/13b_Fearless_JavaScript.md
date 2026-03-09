# Fearless JavaScript — Clef Superset Constraints on Web Runtime Expression

> **Part 2 of 3** — See also: [Architecture](./13a_TypeScript_Ingestion_Architecture.md) | [Output & Integration](./13c_TypeScript_Output_and_Integration.md)

Clef is not F#. It shares F# syntax but extends the language with features designed for native compilation, deterministic memory management, and heterogeneous platform targeting. When generating Clef output from TypeScript declarations — particularly for a web/JavaScript runtime target via Composer — these superset features both **constrain** and **shape** how JavaScript concepts are expressed.

## Features That Constructively Constrain JS/JSX

### 1. Null Freedom — The JavaScript FFI Boundary

Clef enforces absolute null-freedom. Null exists only at the FFI boundary. This is the same principle as the C FFI boundary (see `ffi-boundary.md`) but JavaScript makes it harder because:

1. **Two null-like values**: JavaScript has both `null` and `undefined` (C has only `NULL`)
2. **Pervasive nullability**: In C, pointers are nullable but values are not. In JavaScript, *any* value can be `null` or `undefined` — strings, numbers, objects, arrays
3. **Implicit undefined**: Missing properties, missing function arguments, and array holes all produce `undefined` silently
4. **No annotations**: C has `_Nonnull`, `_Nullable`, SAL annotations, and `NonNullAttr` from clang. TypeScript has `strictNullChecks` but no equivalent attribute system for binding generators

#### Lessons from the C Boundary

Farscape Core's C nullable handling was the largest single point of friction. The solution was:

1. **Nullable-by-default**: All pointer parameters are `Option<nativeptr<'T>>` unless proven non-null
2. **Two override mechanisms**: Clang `NonNullAttr` (from AST dump) and Pilot TOML `[annotations.nonnull]` (developer-asserted)
3. **Clean interior**: Once past the boundary, `nativeptr<'T>` and `FnPtr<'F>` are NEVER null — no defensive checks needed inside Clef code

The JavaScript boundary applies the same architecture:

#### Phase 1: Boundary marshaling (JS ↔ Clef)

```
                    ┌──────────────────────────────────┐
                    │       JavaScript Runtime          │
                    │  null, undefined everywhere       │
                    └──────────┬───────────────────────┘
                               │
                    ┌──────────▼───────────────────────┐
                    │    JS FFI Boundary (marshaling)   │
                    │  null/undefined → ValueNone       │
                    │  value → ValueSome value          │
                    │  ValueNone → null                 │
                    │  ValueSome value → value           │
                    └──────────┬───────────────────────┘
                               │
                    ┌──────────▼───────────────────────┐
                    │       Clef Interior               │
                    │  NO null. NO undefined.            │
                    │  voption for explicit optionality  │
                    └──────────────────────────────────┘
```

#### Phase 2: Nullability classification from TypeScript types

TypeScript's type system (with `strictNullChecks`) tells us *exactly* what's nullable — this is better than C, where we often have to guess:

| TypeScript Type | Nullable? | Clef Binding Type |
|----------------|-----------|-------------------|
| `string` | No | `string` |
| `string \| null` | Yes | `string voption` |
| `string \| undefined` | Yes | `string voption` |
| `string \| null \| undefined` | Yes | `string voption` |
| `string?` (optional property) | Yes | `string voption` |
| `number` | No | `float64` |
| `T \| null` | Yes | `'T voption` |
| `T` (generic, unconstrained) | Non-null by default | `'T` |

**Key insight**: TypeScript with `strictNullChecks` is actually *more* informative than C annotations. If a TypeScript type doesn't include `| null` or `| undefined`, it is non-nullable. We don't need a nullable-by-default policy — we can trust the type declarations.

#### Null and undefined collapse to one discriminant

JavaScript distinguishes `null` from `undefined`, but Clef does not. Both map to `ValueNone`:

```clef
// At the JS boundary, both null and undefined become ValueNone
// This is intentional — Clef code should not care WHY a value is absent
let fromJs (jsValue: JsRef) : string voption =
    if JsBoundary.isNullOrUndefined jsValue then ValueNone
    else ValueSome (JsBoundary.toString jsValue)
```

This avoids the infection problem. JavaScript's dual-null system (`null` vs `undefined`) does not leak into Clef. One discriminant, one check, one type.

#### Optional parameters and properties

TypeScript optional parameters (`foo?: string`) and optional properties (`{ bar?: number }`) are syntactic sugar for `T | undefined`. They map to `voption`:

```clef
// TypeScript: interface Options { timeout?: number; retries?: number }
type Options = {
    timeout: int32 voption
    retries: int32 voption
}
```

#### Preventing null infection

The boundary marshaling ensures null never enters the Clef interior. This parallels the C FFI guarantee:

| C FFI | JS FFI |
|-------|--------|
| `nativeptr<'T>` is NEVER null | Values crossing the boundary are NEVER null |
| `Option<nativeptr<'T>>` at boundary only | `voption` at boundary only |
| `None` → `NULL` (outgoing) | `ValueNone` → `null` (outgoing) |
| `NULL` → `None` (incoming) | `null`/`undefined` → `ValueNone` (incoming) |
| Null pointer optimization (zero overhead) | Value option optimization (zero overhead) |

The critical point: `voption` wrapping happens in the **generated binding code**, not in user Clef code. When a developer writes Clef that calls a JavaScript API, they see `voption` parameters and know they must handle absence — but inside their own functions, there are no nulls to check for.

### 2. Memory Regions

Clef types carry region information: `Ptr<'T, 'Region, 'Access>` where `'Region` is `Stack`, `Arena`, `Peripheral`, `Sram`, or `Flash`.

**Constraint for JS**: The JavaScript runtime has only one "region" — the GC-managed heap. When Clef targets WebAssembly via Composer, region types resolve to:

| Clef Region | WebAssembly Mapping |
|------------|-------------------|
| `Stack` | Wasm linear memory (stack pointer) |
| `Arena` | Wasm linear memory (bump allocator) |
| `Sram` | Wasm linear memory (general) |
| `Peripheral` | N/A (no hardware I/O in browser) |
| `Flash` | Wasm data segment (read-only) |

TypeScript declarations that describe DOM or Web API objects live in the JavaScript GC heap, not in Wasm linear memory. The Clef generator must mark these types as **opaque handles** — pointer-sized references into the JS runtime, not Clef-managed memory. This is analogous to how Farscape Core handles opaque C handles (`typedef struct X_t* X_t`).

### 3. Access Kinds (ReadOnly / WriteOnly / ReadWrite)

Clef pointers carry access permissions derived from CMSIS hardware qualifiers (`__I`, `__O`, `__IO`).

**Constructive constraint for JS**: TypeScript's `readonly` modifier maps naturally to `ReadOnly` access:

```typescript
// TypeScript
interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; }
```

```clef
// Clef: readonly is a type-level constraint, not just documentation
type ReadonlyArray<'T> = {
    length: Ptr<int, Heap, ReadOnly>
    Item: Ptr<'T, Heap, ReadOnly>   // Index access is ReadOnly
}
```

TypeScript's `readonly` is advisory (casts can bypass it). Clef's `ReadOnly` is enforced by the compiler — attempting to write through a `ReadOnly` pointer is a compile error (FS8020). This makes the generated bindings **safer** than the TypeScript originals.

### 4. Native Type Universe (NTU) — Dimensional Types

Clef's numeric types carry dimensional metadata: `NTUint(NTUWidth)` where width is `Fixed of int` or `Resolved of WidthDimension`.

**Constraint for JS**: JavaScript has exactly one numeric type (`number` = IEEE 754 float64) plus `BigInt`. When targeting JS/Wasm:

| TypeScript | Clef NTU Type | JS Runtime |
|-----------|--------------|-----------|
| `number` | `NTUfloat(Fixed 64)` = `float64` | JS `number` |
| `bigint` | `NTUint(Fixed 64)` = `int64` | JS `BigInt` |
| Integer usage patterns | `NTUint(Fixed 32)` = `int32` | Wasm `i32` (if targeting Wasm) |

The dimensional type system means that if a TypeScript API declares `number` but the usage is integer (e.g., array indices, bitwise operations), the Clef binding can carry `int32` with the dimension `Fixed 32` and Composer can generate `i32` Wasm instructions instead of float64 operations. This is a **performance advantage** that TypeScript/JavaScript cannot express.

### 5. Reactive Signals (SolidJS-Parallel)

Clef specifies native reactive signals inspired by SolidJS fine-grained reactivity:

```clef
let count = Signal.create 0
Signal.set count 5
Signal.update count ((+) 1)

let doubled = Memo.create (FnPtr.ofFunction (fun () -> Signal.get count * 2))
let logger = Effect.create (FnPtr.ofFunction (fun () ->
    Console.writeln (sprintf "Count: %d" (Signal.get count))))
```

**Constructive constraint for JS/JSX**: When generating Clef bindings for React or SolidJS components, the signal system provides a native-side reactivity model that compiles to the target framework's primitives:

| Clef Signal | SolidJS Target | React Target |
|------------|---------------|-------------|
| `Signal.create` | `createSignal()` | `useState()` |
| `Signal.get` | signal accessor `count()` | state value `count` |
| `Signal.set` | signal setter `setCount()` | state setter `setCount()` |
| `Memo.create` | `createMemo()` | `useMemo()` |
| `Effect.create` | `createEffect()` | `useEffect()` |

This means TypeScript component declarations can be ingested and the Clef output uses the native signal model, which Composer then compiles to the target framework's primitives. The signal semantics are **stronger** than either React or SolidJS because Clef's escape analysis ensures that closures passed to `Effect.create` or `Memo.create` cannot capture stale mutable state — the class of bug that plagues React's `useEffect` is a compile-time error in Clef.

### 6. Closures and Escape Analysis

Clef has closures — but they work fundamentally differently from JavaScript or F#/.NET closures. This is one of Clef's most distinctive features and it directly shapes how TypeScript callbacks are expressed.

#### Three tiers of function values

| Tier | Clef Construct | Captures? | Allocated Where | Escapes Scope? |
|------|---------------|-----------|----------------|---------------|
| **FnPtr** | `FnPtr<'T, 'R>` | No | Nothing to allocate | N/A |
| **Flat Closure** | `fun x -> x + n` | Yes (flat struct) | Stack or Region | Escape-checked |
| **Nested Named** | `let rec loop acc i = ...` | Yes (as params) | Nothing to allocate | No |

F# on .NET has one model: all closures are heap-allocated, GC-managed objects. JavaScript has one model: all closures capture by reference, live on the GC heap, escape freely. Clef has three tiers, each with different costs and guarantees.

#### Flat closures (MLKit heritage)

Clef's escaping closures use flat closure representation — a struct containing a code pointer followed by captured values, allocated on the stack or in a region, **never on a GC heap**:

```
Closure: { code_ptr: ptr, cap₁: T₁, cap₂: T₂, ... }
```

Captures are by-value for immutable bindings, by-reference for mutable bindings. The critical invariant: **mutable captures cannot escape their defining scope** — the compiler enforces this at compile time.

```clef
// This is LEGAL — immutable capture, closure can escape
let makeAdder n =
    fun x -> x + n    // 'n' captured by value into flat struct

// This is a COMPILE ERROR — mutable capture escaping scope
let bad () =
    let mutable count = 0
    fun () -> count <- count + 1  // ERROR: mutable 'count' cannot escape
```

#### Inline expansion and escape analysis

The `inline` keyword lifts function bodies to call sites, enabling stack allocations to remain valid:

```clef
let inline map (f: 'T -> 'U) (arr: array<'T>) : array<'U> =
    // f is expanded at call site — if f is a lambda, its closure is
    // allocated in the CALLER's frame, not in map's frame
    ...

// At call site:
let doubled = map (fun x -> x * 2) numbers
// After inline expansion: lambda's closure lives in caller's stack frame
```

`[<InlineIfLambda>]` marks parameters where lambda arguments should be inlined — eliminating the closure allocation entirely. The lambda body is spliced into the call site.

#### What this means for TypeScript callbacks

TypeScript callbacks are pervasively closure-based:

```typescript
element.addEventListener("click", (e) => { /* captures outer scope */ });
const doubled = numbers.map(x => x * 2);
```

In F# via Fable, these compile to JavaScript closures — inheriting all of JavaScript's closure problems (GC pressure, stale capture, no escape safety). In Clef, the TypeScript ingestion pipeline generates different constructs depending on the callback pattern:

| TypeScript Pattern | Clef Native Output | Why |
|-------------------|-------------------|-----|
| `arr.map(x => x * 2)` | Inline lambda (zero allocation) | `[<InlineIfLambda>]` eliminates closure |
| `el.addEventListener("click", handler)` | `FnPtr` (top-level function) | Event handler doesn't capture — use function pointer |
| `el.addEventListener("click", (e) => update(state))` | Flat closure (stack-allocated) | Captures `state` by value, escape-checked |
| `makeHandler(config)` returning `(e) => ...` | Flat closure (region-allocated) | Escaping closure with immutable capture |

The key advantage over both JavaScript and F#:

| | JavaScript | F# (.NET) | Clef |
|--|-----------|-----------|------|
| **Allocation** | GC heap | GC heap | Stack/Region (never heap) |
| **Capture mode** | By reference (always) | By reference (.NET closure) | By value (immutable) or by reference (mutable, escape-checked) |
| **Escape safety** | None — closures escape freely | None — closures escape freely | Compile-time: mutable captures cannot escape scope |
| **Stale closure** | Common (React `useEffect`) | Possible | Impossible for mutable state (compile error) |
| **GC pressure** | High (every lambda allocates) | High (every closure allocates) | Zero (stack/region, no GC) |
| **Inline elimination** | JIT may inline | JIT may inline | Guaranteed by `inline` / `[<InlineIfLambda>]` |

This is the "fearless" part of closures: Clef doesn't ban them — it makes them **safe by construction**. The escape analysis catches the bugs that JavaScript developers discover at runtime (stale closures, memory leaks from captured references), and the stack/region allocation eliminates the GC pressure that makes JavaScript closures expensive.

For the JS binding target, callbacks map to Clef closures which Composer then compiles back to JavaScript closures — but with the escape analysis already verified at compile time. The generated JavaScript is *known safe* because the Clef type checker already proved the closure doesn't capture stale mutable state.

### 7. BAREWire Serialization

Clef's `[<BAREWireSchema>]` attribute marks types for deterministic binary serialization with known memory layout.

**Constructive constraint for JS**: When TypeScript declares data transfer types (WebSocket messages, `postMessage` payloads, `SharedArrayBuffer` views), the Clef binding can be annotated with `[<BAREWireSchema>]` to enable zero-copy serialization between Wasm linear memory and JavaScript:

```clef
[<BAREWireSchema>]
type WorkerMessage = {
    Kind: MessageKind
    Payload: array<byte>
    Timestamp: int64
}
```

This allows Wasm↔JS data transfer without JSON serialization — the BAREWire layout is read directly from the `SharedArrayBuffer`.

## Summary: Fearless JavaScript

Rust gave us "Fearless Concurrency" — the ownership system makes data races a compile error, so developers stop fearing threads. Clef gives us **Fearless JavaScript** — the type system makes the classes of bugs JavaScript developers live in fear of into compile errors:

| Fear | JavaScript Reality | Clef Guarantee |
|------|-------------------|---------------|
| Null dereference | `TypeError: Cannot read property 'x' of null` | Null exists only at the FFI boundary; `voption` forces explicit handling |
| Stale closures | `useEffect` captures stale state silently | Escape analysis — mutable captures cannot escape scope (compile error) |
| Implicit coercion | `"5" + 3 === "53"` | NTU dimensional types — `float64` and `string` are incompatible |
| Undefined property access | `obj.foo` returns `undefined` silently | Record types — missing fields are compile errors |
| Mutable shared state | Race conditions in async code | Memory regions + access kinds — ownership is typed |
| Serialization mismatch | JSON.parse returns `any` | BAREWire — schema-driven, zero-copy, layout-guaranteed |
| Framework lock-in | React hooks, Angular DI, Vue reactivity | Native signals — framework-agnostic reactivity compiled to target primitives |

Fable's tagline was "JavaScript you can be proud of." That's about developer experience — F# syntax is nicer than JavaScript syntax. Clef's promise is different: **the compiler prevents the bugs**, not just the syntax. The superset features are not obstacles to JS targeting — they are the reason JavaScript *becomes safe*.

| Clef Feature | Constraint on JS | What It Eliminates |
|-------------|-----------------|-------------------|
| Null freedom | Must handle null at boundary | Null dereference crashes |
| Memory regions | JS heap is opaque handles | Ownership confusion |
| Access kinds | `readonly` is enforced | Mutation of immutable data |
| NTU dimensions | Numeric precision explicit | Implicit coercion, Wasm `i32` vs `f64` |
| Reactive signals | Native signal model | Framework coupling, stale closure bugs |
| Closures + escape analysis | Callbacks are escape-checked, stack/region allocated | Stale closures, GC pressure, capture-by-reference bugs |
| BAREWire | Schema-driven serialization | Serialization mismatches, `any` casts |

The TypeScript ingestion pipeline's Clef generator must be aware of these constraints and produce output that satisfies them. Where TypeScript is permissive (nullable everywhere, closures everywhere, untyped arrays), Clef is precise — and the precision is what enables both native compilation and fearless JavaScript.

## Related Documents

- [Architecture](./13a_TypeScript_Ingestion_Architecture.md): Pipeline, types, and parsing strategy
- [Output & Integration](./13c_TypeScript_Output_and_Integration.md): Output targets, integration, and comparison
- [Closure Representation](https://github.com/user/clef-lang-spec/blob/main/spec/closure-representation.md): MLKit flat closure specification
- [FFI Boundary](https://github.com/user/clef-lang-spec/blob/main/spec/ffi-boundary.md): C FFI null safety specification
