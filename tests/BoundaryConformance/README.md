# Generator/compiler boundary conformance

The [2026-10-03 checkpoint](https://forge.spkez.dev/FidelityFramework/Farscape/wiki/History-20261003-ffi-boundary-checkpoint)
records the accepted **649/649** source tests and **17/17** conformance gates,
their artifact hashes, current generation scope and regeneration procedure.
These gates do not execute native callbacks or establish foreign lifetimes.

Run from the Farscape checkout:

```sh
dotnet run --project tests/BoundaryConformance/BoundaryConformance.fsproj
```

The project builds Farscape.Core and the sibling Clef compiler from source. To
select another compiler checkout, pass
`-p:ClefCompilerProject=/absolute/path/to/Clef.Compiler.Service.fsproj`.
Optional runner arguments select the Platform CompilerSurface and BAREWire
BindingMetadata `.fidproj` files. `clang` must be available.

The driver parses the actual fixture C header, loads its pilot's nullability and
ownership declarations, generates Clef, and checks complete consumer projects
with the selected Linux platform declarations. The scalar libc `abs` call must
be reachable, admitted and retained in the public PSG revision. Opaque typedefs
must become nominal phantom markers carried by `CHandle`; their generated
signatures are inspected in the compiler graph, including nullable defaults,
explicit nonnull annotations and the callback record's typed `FnPtr` signature.
Unannotated callback pointer arguments and value-record pointer fields must also
retain their nullable handle contracts; a `_Nonnull` value-record field retains
its nonnull handle contract.
Generated source must contain no raw pointer
types. An ownership spelling check verifies release metadata survives generation;
it does not establish ownership enforcement.

A separate scalar pilot goes through `BindingGenerator.generateFromProject` and
its returned source files are consumed by Clef. Generated manifests must name
existing source dependencies. Consumer manifests select the platform and binding
metadata explicitly. Legacy native-pointer and untyped XML pilots, and errno
wrappers requiring an opaque-pointer dereference or an undeclared error-text
return contract, must fail before writing binding output. The single-header
generator must likewise return an explicit unsupported
object-ABI error for the actual C++ fixture. Each negative regeneration gate also
preserves an existing source sentinel.

An LLP64 pilot must also be rejected before writes while project generation
declares a Linux x86_64 LP64 target. This prevents emitting a mislabeled future
Windows binding package; it is not cross-target ABI support.

Separate negative gates require owning compiler diagnostics for demanded forged
handles, forged entries, generic-union transport of a fabricated handle factory,
raw address formation, generated opaque foreign calls
and a generated scalar import from an unsupported library. Parse failures and
unrelated errors cannot satisfy these gates. Expected rejection verifies that an
unsupported contract fails explicitly; it does not prove handle lifetime,
nonnull validation, callbacks or general desktop FFI support. The fixture includes
callback declarations and release metadata, but this driver does not execute C
or a native Clef binary, verify a callback ABI, or discharge release obligations.

Generated inputs, full diagnostics, parse results, assembly hashes and gate
results remain in the printed unique temporary directory. Each gate failure is
caught and reported; the driver returns exit 1 if any gate or setup fails, without
an unhandled top-level exception. Compilation and native execution outcomes must
be reported separately; no native execution occurs in this driver.

To reproduce source errors in an existing consumer without making them expected
suite outcomes, run:

```sh
dotnet run --project tests/BoundaryConformance/BoundaryConformance.fsproj -- --check-project /absolute/path/to/project.fidproj
```

This separate mode records all diagnostics, captured input and compiler assembly
hashes, and returns exit 1 for actual source errors. It does not launch a native
program or change the conformance suite's expectations.
