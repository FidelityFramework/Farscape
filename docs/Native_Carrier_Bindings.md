# Typed pthread boundary and experimental ABI probe

Current Clef source exposes opaque, non-arithmetic `CHandle<'T>` values and
compiler-owned `FnPtr<'F>` entries. Nullable C data pointers use `option<CHandle<_>>`.
All C integers are spelled `int`; their sign and width live in each generated
`FunctionDescriptor`. Raw address arithmetic is not a supported source boundary.

The production pthread pilot is a minimal hosted x86-64/glibc 2.34+ profile.
Farscape parses the installed pthread.h, stdlib.h and unistd.h as C and measures
the synchronization unions with Clang. The generated namespace library `c`
selects the verified pthread exports in libc on this host. Other host profiles
may select separate pthread linkage. There is no generated or handwritten C shim.

The pilot's explicit `[[options.bindings]]` projections add these typed surfaces:

- `reference_parameters = [0]` on `pthread_create` maps its single `pthread_t *`
  output cell to `int array`, with `Integer (Unsigned, 64); PassBy = Reference`.
  The compiler must guarantee at least one writable element with the declared
  representation, including when the extern is called directly. Farscape rejects
  projections of non-scalar pointees or multiple pointer levels.
- `nonnull_callbacks = [2]` declares this profile's callback contract as
  `FnPtr<CHandle<unit> -> CHandle<unit>>`. The caller supplies a live, nonnull opaque
  context and the callback returns it. This is an explicit profile restriction;
  ordinary C callbacks remain nullable where the header allows null.
- `symbol`, `return_handle` and `parameter_handles` specialize malloc/free into
  `mallocMutex`, `mallocCond`, `mallocContext` and corresponding free functions.
  The native symbols and pointer representations remain those of the parsed C
  declarations. Nominal marker types distinguish mutex and condition storage.
  Their measured layout companions supply the allocation sizes and alignment.
  These aliases do not add a source cast or dereference operation.
  Explicit `ownership` tags emit `CallerOwns` for malloc specializations and
  `CalleeOwns` for consuming free aliases. These are lifecycle declarations for
  compiler analysis; this generator does not itself prove handle lifetimes.

The production project includes Types, Thread, Mutex, Cond, Storage and Affinity.
Storage also includes generated `exitProcess` (the C `_exit` symbol) and `usleep`.
Affinity keeps the mask opaque: generated `mallocAffinity`/`freeAffinity` use the
measured `cpu_set_t` layout, `sched_getaffinity` fills it, and `cpuCount` calls the
actual header-declared `__sched_cpucount` symbol used by glibc's `CPU_COUNT_S`.
No source array view is needed. A fixed mask can be too small for a larger host;
the scheduling layer falls back to one calling carrier when allocation or query fails.

Pthread status returns are the direct error codes, so the scheduling layer must examine
them without reading errno. Older return-code wrapper generation retains that
same rule. Optional wrappers consume the same resolved source signature as the
extern, including Reference arrays and opaque allocation specializations.

`descriptor_only_dependencies = true` selects the actual BAREWire descriptor
sources through `BAREWire.BindingMetadata.fidproj`, avoiding the full codec
source closure. A native executable still supplies its platform description
through its own platform package.

The reusable generator repairs retain anonymous typedef record identities,
accept `typedef name` layout probes, read both Clang output streams, expose
synchronization unions as opaque measured storage, and repair declaration
indentation and keyword escaping. Missing requested record measurements reject
generation. Scalar layout facts come from the parsed type and target ABI profile.

The former raw-address pool and bindings remain only under
`Fidelity.Platform/CPU/Linux/x86_64/Experimental/`. Their option is explicitly
`experimental_native_pointer_surface`; those files are legacy ABI experiments,
not evidence of current Clef source acceptance. Current typed native acceptance
must be established by compiling and running the separate Ariel native probes.
General CHandle-to-bounded-array views (such as mapped GBM pixels), buffer extent
relations and arbitrary callback capture transport are not supplied by these
single-cell and opaque-handle projections.

From the Farscape checkout, with sibling repositories:

```sh
dotnet run --project src/Farscape.Cli -- project --project ../Fidelity.Platform/CPU/Linux/x86_64/pilot/pthread.pilot.toml
dotnet test tests/Farscape.Tests
python ../Fidelity.Platform/tests/Pthread/verify_abi.py
```

The Python probe uses guarded storage to observe the host ABI, checks a nonnull
callback environment round trip, direct EBUSY return handling and allowed CPU
affinity. It is not a Clef compiler acceptance test. The scheduling lifecycle
model is likewise separate from the compiled carrier lifecycle tests.
