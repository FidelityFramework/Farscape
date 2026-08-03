# Binding Generation Gaps

**2026-08-03**

> **Verified against**: Farscape commit `a0abe2b`, Composer `1bea3f7`, clang 20 on Arch
> Linux. Findings were reached by reading committed sources and by disassembling
> generated binaries. Farscape's working tree carried 37 uncommitted files when this was
> written; every claim below is stated against `HEAD`. Re-verify before acting.

This document records defects in binding generation that are **silent** — they produce no
diagnostic, they type-check cleanly, and in two cases they miscompile into code that runs
and returns wrong answers. They were found while attempting to use the generated FreeType
binding to rasterise a glyph, which is the first time any consumer needed to read through
a handle rather than merely pass one.

The existing corpus documents intent. This documents the delta between intent and
behaviour. It is deliberately joint across layer and representation, because the central
finding cannot be stated in either vocabulary alone: `docs/10_Boundary_Marshaling_Spec.md`
owns idiom containment and says nothing about representation, while
`.serena/memories/TYPE_MAPPING_ARCHITECTURE_ANALYSIS.md` owns representation and says
nothing about layers. The handle defect below is a statement in both at once.

## Summary

| # | Defect | Symptom | Diagnostic emitted |
|---|---|---|---|
| 1 | Forward declaration shadows its own definition | Struct absent from output; an opaque handle appears in its place | None |
| 2 | Clef records cannot overlay C structs | Field reads return wrong bytes | None — type-checks |
| 3 | Single-word handle records miscompile | Callee receives a slot address, not the handle; 8 bytes leaked per construction | None — type-checks |
| 4 | Fixed arrays, bitfields and unions are parsed and discarded | Wrong struct size and field offsets | None |

Defects 1 and 4 are generation defects and are fixable in Farscape. Defects 2 and 3 are
consequences of how Composer lowers Clef records, and are fixable in Farscape only by
changing what it emits.

## 1. A forward declaration permanently shadows its own definition

`DeclarationAlgebra.mergeDeclarations` (`DeclarationAlgebra.fs:98-106`) deduplicates
declarations by bare name with first-occurrence-wins:

```fsharp
let seen = System.Collections.Generic.HashSet<string>()
declLists
|> List.concat
|> List.filter (fun decl ->
    match cataDeclarations declarationNameAlgebra [decl] with
    | [Some name] -> seen.Add(name)
    | _ -> true)
```

There is no declaration kind in the key, and no notion of completeness. When a header uses
the standard C idiom of typedef-before-definition:

```c
typedef struct FT_FaceRec_*  FT_Face;   /* freetype.h:641  */
struct FT_FaceRec_ { /* 31 fields */ }; /* freetype.h:1229 */
```

clang emits **both** as `RecordDecl FT_FaceRec_`, the zero-field one first. The merge keeps
the empty one and discards the definition.

The consequence compounds. `FidelityCodeGenerator.generateStructDecl`
(`FidelityCodeGenerator.fs:295`) drops fieldless structs with no diagnostic, because an
empty Clef record is invalid syntax. Separately,
`DeclarationAlgebra.definedStructNameAlgebra` (`DeclarationAlgebra.fs:70-79`) excludes
fieldless structs from `knownStructNames`, so `ActivePatterns.isOpaqueHandleTypedef`
(`ActivePatterns.fs:101-107`) sees a pointer to an unknown struct and returns true —
manufacturing `FT_Face = { Handle: nativeint }`.

**One predicate produces both symptoms.** The missing record and the handle standing in its
place are the same failure, not two design choices. This matters because the handle looks
deliberate, and `docs/roadmap/00_farscape-maturation-plan.md:237-258` documents a design
that produces exactly that shape — so a reader concludes the opaque handle was intended
when in fact it is debris.

### The control case

`FT_Size_RequestRec_` is *defined* at `freetype.h:3031` and forward-typedef'd afterwards at
`:3050` — definition first. It is the **only** `*Rec_` struct present in the generated
FreeType binding. Every `*Rec_` whose typedef precedes its definition is absent; every
struct with no preceding forward declaration is present. Declaration order alone partitions
the output.

### Blast radius

A shadowed-struct count over each pilot's own header set:

| Header set | Shadowed structs |
|---|---|
| `webkit2gtk` | 932 |
| `gtk-3` | 847 |
| `gobject-2.0` | 103 |
| `freetype2` | 12 |
| `libdrm`, `wayland`, `libgbm`, `libc`, `pthread`, `rocm-hip` | 0 |

The GLib family forward-declares then defines universally. The consequence is already
observable:

```
$ find ~/repos/Fidelity.GObject -name '*.clef' -path '*Bindings*' | xargs grep -h '^type ' | wc -l
1
$ find ~/repos/Fidelity.Gtk3   -name '*.clef' -path '*Bindings*' | xargs grep -h '^type ' | wc -l
2
```

The generated GObject binding declares one type. Gtk3 declares two. These are not thin
bindings; they are empty ones. FreeType, at 81 types emitted and 12 lost, is the mild case.

The libraries with zero shadowing are precisely those already in production use, which is
why this survived undetected.

**Fix**: key the deduplication on name *and* completeness, preferring the complete
declaration regardless of arrival order. Note that this alone changes generated output for
every GLib-family binding, and that there is no verification command to check the result —
see §6.

## 2. A Clef record cannot overlay a C struct

This is the harder finding, and it is not a Farscape defect. Under Composer `1bea3f7`,
three properties compound:

| Property | Consequence | Source |
|---|---|---|
| Clef records are **packed** — no natural-alignment padding | Field offsets are the running sum of field sizes | `Alex/CodeGeneration/TypeSizing.fs:16`, `TypeMapping.fs:49` |
| Clef `int`/`uint` are **register width**, 8 bytes on x86-64 | A C `int` field advances the offset by 8, not 4 | `clef NativeTypes.fs:1378`; `TypeMapper.fs:57-58` maps C `int` to `int` deliberately |
| Nested record fields lower through **memref descriptors**, 4 words | A struct-by-value field is not inline at all | `TypeSizing.fs:53-57` |

Measured against the installed FreeType, `FT_Bitmap_` has this C layout:

| Field | C offset | Clef record (`uint`/`int`) | Clef record (`uint32`/`int32`) |
|---|---|---|---|
| `rows` | 0 | 0 | 0 |
| `width` | 4 | 8 | 4 |
| `pitch` | 8 | 16 | 8 |
| `buffer` | 16 | 24 | **12** |

Register-width mapping misplaces every field after the first. Switching to fixed-width
types fixes the first three and then misplaces `buffer`, because C inserts four bytes of
alignment padding before the pointer and a packed record does not. **No `TypeMapper` change
alone can fix this**, and the third property kills the nested cases regardless — a
`FT_FaceRec_` record could never place `glyph` correctly, because the `bbox` and `generic`
fields ahead of it are memref descriptors rather than inline structs.

The corpus currently plans in the opposite direction.
`docs/roadmap/00_farscape-maturation-plan.md:376-413` specifies
`[<StructLayout(LayoutKind.Explicit)>]` plus `[<FieldOffset(N)>]` output, and
`docs/02_BAREWire_Integration.md:96-128` specifies natural-alignment layout arithmetic
(`alignUp offset align`). Neither can be reproduced by an emitted record under the
properties above. `docs/roadmap/01_threebody-wayland-architecture.md:329-341` and
`docs/roadmap/02_farscape-phase4-npu-xrt-binding.md:141-145` both carry hand-annotated
offset tables that are assertions rather than guarantees.

### The correct output is a layout module, not a record

For any struct a consumer must read through a pointer, the generator should emit offsets
and accessors:

```clef
module FT_GlyphSlotRec =
    [<Literal>]
    let Size = 304
    [<Literal>]
    let BitmapOffset = 152

    /// Interior pointer — nested structs are projected, never copied.
    let bitmapPtr (p: nativeint) : nativeint = p + 152n

    let bitmapLeft (p: nativeint) : int32 =
        NativePtr.read (NativePtr.ofNativeInt<int32> (p + 192n))
```

Farscape already measures the offsets. `CppParser.extractStructLayouts`
(`CppParser.fs:166-179`) shells out to `clang -Xclang -fdump-record-layouts-simple` and
returns `StructLayoutInfo { Name; SizeBits; DataSizeBits; AlignmentBits; FieldOffsetsBits }`.
The data is correct and currently feeds attributes on a record that cannot use them. What
is missing is an emitter, plus a **fixed-width ABI type map** distinct from `TypeMapper`'s
register-width map — `unsigned int → uint32`, `int → int32`, `long → int64`.

The `NativePtr.read (NativePtr.ofNativeInt<'T> …)` idiom is proven under the pin; it is in
shipping use at `HelloWayland/src/Gpu/Fill.clef:83`.

Two constraints on such an emitter:

- **Suppress the record** for any struct that gets a layout module. A record that looks
  usable and reads the wrong bytes is the worst artifact this generator can produce.
- **Emit a mismatch warning** for every struct that *does* get a record: compare clang's
  measured `FieldOffsetsBits` against the naive packed Clef layout and emit a visible
  `// ABI MISMATCH` comment on disagreement. This is worth doing independently of
  everything else — it converts today's silent wrong-byte reads into a diagnostic across
  every binding in the ecosystem.

The layout dump currently runs **only** when `[options] abi_critical_structs` is non-empty
(`BindingGenerator.fs:275-290`). Any library that omits the key gets zero measured offsets,
and therefore zero mismatch detection.

## 3. Single-word handle records miscompile at the call boundary

Composer maps a Clef record to `TStruct` (`Alex/CodeGeneration/TypeMapping.fs:476-492`), and
a record value is memref-backed — `TypeSizing.fs:53-57` sizes a memref as a four-word
descriptor. A single-word `{ Handle: nativeint }` therefore reaches the callee as the
address of a slot rather than as the handle itself.

This is not theoretical. It is documented in production at
`HelloWayland/src/Gpu/Fill.clef:41-50`, where `hipModuleGetFunction` returned
`hipErrorNotFound` (500) until the application declared its own raw externs taking
`nativeint` directly; the four `*Raw` externs at `:52-74` exist solely because of it.
Disassembly of the generated FreeType binding shows `FT_Library.ofHandle` emitting a
`malloc(8)`, a store, and a five-word descriptor build — so every handle construction also
leaks eight bytes.

### The mitigation belongs in Layer 1

- **Layer 1's contract is the C ABI, transcribed.** An extern whose declared parameter type
  makes the compiler pass the address of a slot where C expects the slot's contents is not a
  faithful transcription. `FT_Face` *is* a pointer; the record is a nominal-typing nicety
  invented at `FidelityCodeGenerator.fs:79-100` and propagated into every signature by
  `mapCTypeToFidelityType` (`:117-145`). Providing that nicety is Layer 2's job.
- **Layer 2 provably cannot fix it.** A Layer 2 wrapper calls Layer 1. If Layer 1's
  declaration miscompiles, wrapping changes nothing. To fix it, Layer 2 would have to
  declare its own externs — at which point it is no longer Layer 2.
- **Layer 3 must not own it.** An overlay that redeclares the entry points has forked
  Layer 1 for a whole library, and every consumer that does not open the overlay is still
  broken. That is the state HelloWayland lives in today, and `Fill.clef:49` is an apology
  for it rather than a design.

**Fix**: emit `nativeint` in extern parameter and return positions for pointer typedefs.
Keep the nominal record and its `zero`/`isNull`/`ofHandle` companion in `Types.clef` for
Layer 2 and Layer 3 to use, where it never crosses a call boundary.

This inverts a validation criterion that several roadmap phases carry — for example
`docs/roadmap/00_farscape-maturation-plan.md:562` ("emit as distinct wrapper structs, not
`nativeint`") and `docs/roadmap/02_farscape-phase4-npu-xrt-binding.md:316-319`. Those gates
would pass while producing miscompiling code.

It also removes the motivating problem of `docs/11_Namespace_Scoped_PSG_Design.md:10-13`,
whose headline complaint is that 28 handle records pollute `FieldLabels`. If the records are
not emitted, that problem does not arise. Problems 2 and 3 in that document survive.

## 4. Parsed and discarded: arrays, bitfields, unions

Three C constructs are parsed into the declaration model and never consumed by emission:

| Construct | Parsed at | Consumed | Result |
|---|---|---|---|
| Fixed arrays | `CppParser.fs:367-378` (`IsArray`, `ArraySize`) | Never — `generateStructDecl` uses only `f.Type` | Collapses to one element |
| Bitfields | `CppParser.fs:468-483` (`BitWidth`) | Never | Widened to storage width |
| Unions | `CppParser.fs:489` (`StructDecl.IsUnion`) | Never | Rendered as a sequential record |

Each independently produces a wrong struct size and wrong offsets for every field after the
affected one. These are a second, independent reason why the layout-module approach in §2 is
correct rather than merely convenient: offsets are how you read a `char name[32]`, a
bitfield, or a union in any case, and the record form cannot represent any of the three.

`docs/02_BAREWire_Integration.md:295-314` specifies bitfield extraction from `_Pos`/`_Msk`
macro pairs, and `:59-64` shows an IR carrying `ArraySize: int option`. Both describe
capabilities the parser supplies and the emitter ignores.

## 5. What the toolchain actually does

No document in the corpus states this, and it is load-bearing for everything above.

Farscape does **not** link libclang. It shells out to `clang` four times per header and
parses stdout:

| Pass | Invocation | Produces | Gate |
|---|---|---|---|
| 1 | `-Xclang -ast-dump=json -fsyntax-only -fparse-all-comments` | Declarations, via `FSharp.Data` `JsonValue` | Always |
| 2 | `-E -dM` | Macro constants, filtered by `macro_prefixes` (`CppParser.fs:886-892`) | Always |
| 3 | `-H -E -o /dev/null` | Include tree, for macro doc-comment attribution | Always |
| 4 | `-Xclang -fdump-record-layouts-simple` | `StructLayoutInfo` — real measured offsets | **Only when `[options] abi_critical_structs` is non-empty** (`BindingGenerator.fs:275-290`) |

`walkAst` tracks the current file in a mutable variable, because clang emits `loc.file` only
when it changes, and keeps a declaration only if its path starts with the include root. The
include root is compared as a raw string, so a relative `include_paths` entry such as `"."`
disables library-boundary scoping entirely.

`docs/01_Architecture_Overview.md:66-72` and `docs/04_XParsec_Architecture.md` describe only
passes 1 and 2. Pass 3 is documented only in
`.serena/memories/xparsec_header_parsing.md:80`. Pass 4 is documented nowhere.

## 6. Type-checking is not a sufficient correctness gate

`docs/09_Library_Verification.md` builds its argument on generated libraries type-checking
cleanly under CCS with zero errors. That gate is necessary and it is not sufficient. Every
defect in this document is type-clean:

- A dropped struct (§1) produces no node to type-check.
- A packed record with wrong offsets (§2) type-checks and reads the wrong bytes.
- A memref-backed handle (§3) type-checks and passes the wrong pointer.
- A collapsed fixed array (§4) is a valid one-element record field.

A library can pass verification with all four live. The remediation cycle described at
`docs/09:62-70` matures the generator against the class of defects that surface as type
errors, which is a real class and not this one.

Two additional facts bear on that document. The `farscape verify` command **does not exist**
— the CLI root is `generate | pilot | project` (`Program.fs:508-516`). And the known-issues
table at `docs/09:74-84` states the `int` mapping backwards: `TypeMapper.fs:57-58` maps C
`int` to Clef `int` with the explicit comment `// NTU register-width dimensional — NOT
int32`, which is the deliberate decision recorded in
`.serena/memories/TYPE_MAPPING_ARCHITECTURE_ANALYSIS.md:12-13`. Applying that row's
prescribed fix would undo it.

## 7. Marshalling is decided in several places that do not agree

The repository specifies marshalling rules in at least seven locations, three of them
undocumented, and they conflict. This section records the state; unification is future work.

| Site | Rule asserted | Status |
|---|---|---|
| `docs/10_Boundary_Marshaling_Spec.md:5, 13-16` | Layer 2 is the membrane; C idioms terminate there | Sound, but silent on representation |
| `docs/10:59-65` | Layer 3 signatures use typed records; `nativeint` appears only in Layers 1–2 | Conflicts with §3 |
| `docs/10:103-117` | Callbacks today: top-level function + `dlsym` + malloc'd state. Target: flat closure `code_ptr` as the function pointer, closure struct as the `data` pointer | The "today" column matches `CallbackWrapperGenerator.fs:10-18`. The target passes `&closure` — the same address-of-slot mechanic §3 identifies as the defect, here proposed as the feature |
| `docs/08_Nullable_Pointer_Architecture.md:13-22` | `Option<>` by default on every pointer; `None ↔ NULL` | `Option<HandleRecord>` layers option lowering over a memref-backed record. Unowned by any document |
| `docs/03_fsnative_Integration.md:82-89` | Binding intent travels as MLIR attributes | Accurate, and specifies metadata only. There is no statement anywhere of what a Clef record looks like when it reaches a C callee — which is why §3 was discoverable only by disassembly |
| `.serena/memories/farscape_barewire_integration.md:36-74` | Farscape computes natural-alignment BAREWire descriptors | Conflicts with packed record lowering (§2) |
| `PilotTypes.fs:197-201`, `ProtocolParser.fs:423` | `ProtocolConfig { MarshalFunction; MarshalModule }`, configurable via `[protocol]` | **Undocumented anywhere in the corpus**, and the only place "flat Clef values → C argument array" is actually implemented. The most relevant prior art for a unification |

The through-line: `docs/10` owns idiom containment and knows nothing about representation;
the type-mapping and BAREWire artifacts own representation and know nothing about layers.
The conclusion in §3 — Layer 1 emits `nativeint`, Layer 2 cannot fix it, Layer 3 must not
fork it — is a statement in both vocabularies at once, and until now there was no document
in which such a statement could be filed. A future `docs/10` gains a Representation section
rather than a sibling document being created.

`.serena/memories/farscape_binding_architecture.md:106` asserts that callback struct fields
use `FnPtr<'F>` resolved via `FnPtr.fromSymbol`. `FnPtr` appears in the source only in two
comments (`FidelityCodeGenerator.fs:122, 171`) and is never emitted;
`CallbackWrapperGenerator.fs:111-121` implements `dlsym` resolution passing `nativeint`.
`docs/roadmap/06_farscape-phase4d-onnxruntime.md:87, 266` plans against the same
non-existent primitive.

## 8. A nominated goal: provenance-ordered claims

This section states a goal, not a design. The path is not fully clear and the open questions
at the end are real. It is recorded here because every defect above shares one property —
an assertion existed with nothing able to contradict it — and the generalisation of the fix
is worth naming before it is worth building.

The target is a formalism in which **structure overrides inference by construction** rather
than by convention, so that Layer 3 can be mostly derived and the part that cannot be
derived is explicitly marked as taste.

### Strata

Every claim about a binding carries a provenance, and provenance is ordered:

| Stratum | Source | Example claim |
|---|---|---|
| **Measured** | clang record-layout dump; actual byte offsets | `FT_GlyphSlotRec_.bitmap` is at offset 152 |
| **Derived** | graph traversal over measured facts | `FT_Face → glyph → bitmap` is a valid typed path |
| **Declared** | the human, in the pilot file | that projection is named `glyphBitmap`, grouped under `Face` |
| **Inferred** | heuristic or model proposal | `FT_New_Face` / `FT_Done_Face` are an acquire/release pair |

The rule: a lower stratum may **refine** where a higher one is silent, and may never
**contradict** it. Structural override stops being a special case, and "the human outranks
the model" falls out of the same rule as "measurement outranks everyone."

### Admissibility is decidable per claim kind

What makes this a formalism rather than a policy is that each claim kind carries a decision
procedure against the measured stratum:

| Claim kind | Admissible iff |
|---|---|
| Projection path | the path exists in the measured struct graph |
| Resource lifecycle | acquire's out-param type and release's parameter type are the same handle type |
| Out-param inversion | the parameter is a trailing pointer-to-`T` and the return is the library's error type |
| Layout module | every named field offset matches the clang dump |

These are checks, not judgements.

### The partition that matters

Claims divide into **structurally constrained** — where inference can be refuted — and
**structurally free**, which is naming and grouping. Structure has nothing to say about
whether an accessor is called `glyphBitmap` or `bitmapOf`. A formalism that admits it has
nothing to say about names is stronger than one that pretends otherwise, because it
localises taste instead of diffusing it.

Most of what a hand-written Layer 3 overlay carries is **usage protocol** — that
`FT_Load_Char` precedes `FT_Render_Glyph` precedes reading `face->glyph->bitmap`. That
sequencing appears in no signature today, which is why generating it feels like guesswork:
the input is absent, not the method. Resource lifecycle and projection paths recover most of
it structurally once the layout graph is retained rather than discarded.

### Refutability is the load-bearing property

An inference that cannot be wrong is not a proposal; it is a decision wearing a proposal's
clothes. Every inferred claim must be expressible in a form the measured stratum can
contradict.

This is precisely what the standing corpus lacked. Eight documented claims were internally
coherent, mutually consistent, and false, because none of them stood in any admissibility
relation to the code. §1 through §7 are the cost of that.

### Consequence for Atelier

Each proposed binding element carries its provenance and its status: derived and checked;
inferred and admissible but unverified; inferred and **refuted by structure**. A human edit
is recorded as a *declared* claim, which outranks the inference and is still checked against
measurement — so "rolling your own" FFI means overriding the guess, never escaping the
check.

Because claims key on structural identity rather than file position, regeneration after a
header change either preserves a claim or invalidates it loudly. A hand-written overlay
cannot do this; it goes stale silently, which is the failure mode this whole document
describes.

### Open questions

1. **Measured-versus-declared conflict needs a policy, not an error.** Sometimes the human
   legitimately knows the header lies — a variable-length struct, a vendor-reserved field.
   The top of the lattice likely needs an explicit recorded escape carrying a justification,
   rather than being inviolable.
2. **Claim identity under C-level renames is unsolved.** Keying on type name plus path is
   stable across field reordering and fragile across renaming.
3. **Whether grouping heuristics generalise is unknown.** FreeType is well-behaved. A
   convention-heavy surface such as GObject is the real test, and until that experiment runs
   this is a formalism-shaped hypothesis rather than a formalism.
4. **Where the model proposal runs.** Proposing pilot stanzas for human review keeps
   inference inside the check; proposing Clef directly does not.

## 9. Corrections applied to the corpus

Applied in the same pass as this document:

| File | Correction |
|---|---|
| `docs/07_Pilot_Project_Setup.md` | Removed the phantom `[options]` keys `opaque_handles` and `flags_enums`; corrected `[output] mode` from Required to parsed-and-ignored; corrected the `mergeDeclarations` description; documented `[callbacks]`, `[protocol]`, `[error_conventions.overrides]` and the absence of a validation pass |
| `docs/09_Library_Verification.md` | Marked `farscape verify` as designed-not-implemented; replaced the known-issues table with a pointer here and corrected the inverted `int` row |
| `docs/README.md` | Extended the table of contents to documents 10–14 |
| `docs/02_BAREWire_Integration.md` | Cross-reference from the layout section to §2 here |
| `docs/11_Namespace_Scoped_PSG_Design.md` | Cross-reference noting that problem 1 is superseded by §3 here |

A second pass corrected the drift the first pass catalogued:

| Correction | Scope |
|---|---|
| `Unchecked.defaultof<T>` → `NativeDefault.zeroed ()` as the Layer 1 body | 22 occurrences across 11 files. The two statements that correctly note `Unchecked.defaultof` is BCL and rejected by CCS were left intact |
| `[<FidelityExtern>]` reclassified from under-development to shipped | 6 files, plus added to the implemented list in `docs/README.md` |
| `farscape verify` marked not-implemented | 5 memories, with a status blockquote above each specification of the command |
| Root `README.md` type table corrected to the register-width NTU mapping; `T *` row corrected to `option<nativeint>`; stale test count removed | 1 file |
| Schema-caveat banner on roadmap recipes using `[sources]` and singular `[error_convention]` | 5 roadmap documents |
| Handle-record validation criteria flagged as inverted | 4 roadmap documents, plus the `Option<HandleType>` row in `docs/08` |
| `FnPtr<'F>` marked aspirational; the implemented path is `dlsym` → `nativeint` | 2 files |
| `FS8001` write-only-register diagnostic marked not-implemented | `docs/02` |
| Layer 3 marked a convention rather than a mechanism, and `docs/10` flagged as specifying containment without representation | `docs/10` |

Deliberately not attempted: rewriting the roadmap example TOMLs themselves. Those documents
carry other staleness beyond section names, and a banner prevents the copy-paste failure
without pretending the recipes have been validated.

## Related Documents

| Document | Purpose |
|---|---|
| `docs/01_Architecture_Overview.md` | The generation pipeline and the declaration catamorphism |
| `docs/02_BAREWire_Integration.md` | Layout calculation — see §2 here before implementing |
| `docs/07_Pilot_Project_Setup.md` | Pilot TOML schema reference |
| `docs/09_Library_Verification.md` | Verification strategy — see §6 here for its limits |
| `docs/10_Boundary_Marshaling_Spec.md` | Layer discipline — see §7 here for the representation gap |
| `docs/11_Namespace_Scoped_PSG_Design.md` | PSG scoping; its problem 1 is superseded by §3 here |
| `docs/roadmap/00_farscape-maturation-plan.md` | §4.1 and §4.4 are the designs §2 and §3 here revise |
| `~/repos/Composer/docs/NTU_Architecture.md` | Register-width dimensional types |
