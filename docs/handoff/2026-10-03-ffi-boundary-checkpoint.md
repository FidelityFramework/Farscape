# FFI boundary checkpoint — 2026-10-03

The pointer-surface correction is complete in the Farscape generator and covered
by the source tests and generator/compiler conformance gates below. Current
generation preserves nominal handles, conservative nullability and declared ABI
metadata, and rejects the retired raw-address generation routes. This checkpoint
does not establish general desktop FFI execution or native callback readiness.

The governing contract is
[`clef-lang-spec/spec/ffi-boundary.md`](../../../clef-lang-spec/spec/ffi-boundary.md).
The compiler and Calque guards reject unsupported or fabricated boundary values;
they do not supply missing native adapters. An interior flat closure and a
proof-bearing `Ptr<'T, 'Region, 'Access>` retain their own obligations. A
`CHandle<'T>` grants no dereference, offset, integer conversion or generic view.

## Completed generator work

- Opaque pointer typedefs produce nominal marker types used through `CHandle<T>`.
  The former integer-handle records and zero/address constructors are removed.
- Unannotated data pointers default to `option<CHandle<T>>`, including typedef
  aliases, callback parameters/results and value-record fields. Explicit header
  or pilot nonnull evidence narrows the contract. Nonnull does not establish
  liveness, ownership or validity of the foreign value.
- Clang nullability and semantic nonnull annotations are retained, including
  attributes obtained from the real header parsing path. Contradictory or
  incomplete demanded callback contracts are refused.
- Typed callback records carry `FnPtr` and complete native signature descriptors.
  GObject signals preserve nullable instance, payload and user-data contracts;
  descriptors use the selected ABI model. Signal-only nominal types are emitted
  in the shared type module. Closed module callbacks allocate no captured
  environment; a no-op destroy notification therefore proves no environment
  destruction mechanism.
- Reference projections, ownership metadata and measured layouts remain explicit
  contracts. Error descriptions use header data rather than an undeclared native
  text read. Returned-error-code wrappers retain their direct status convention.
- Legacy native-pointer output, synthetic C++ object/method projection, generic
  listener builders, and untyped XML request generation are retired. Direct
  public `ProtocolParser` request-generation APIs also refuse request-bearing
  protocols; parsing and `toTypeDeclarations` retain useful protocol metadata.
- Unsupported project contracts are checked before output writes. Regression
  sentinels verify that these refusals preserve existing output. Generated
  manifests reference discovered, existing source dependencies.

## Supported and refused scope

| Operation | Current checkpoint |
|---|---|
| Linux x86_64 LP64 C project generation | Typed declarations and descriptors; each demanded consumer call still needs compiler admission |
| Reachable libc `abs` scalar call | Admitted and retained in the public PSG by the conformance driver |
| Opaque handles, callback records and nullable value-record fields | Generated and inspected in the compiler graph; native execution is separate |
| Native function address or indirect invocation | Native target ABI realization is still missing from the source-published Baker/PSG contract; Alex refuses it |
| Owned/borrowed/static foreign resource lifetimes | Metadata and obligations are retained; no general release/alias-retirement implementation is claimed |
| `experimental_native_pointer_surface` | Refused; there is no source address alternative |
| Legacy XML request projection | Refused; use an admitted `typed_protocol` contract |
| Generic listener builder and unsupported C++ object projection | Refused pending actual native entry/object contracts |
| `errno` wrappers | Refused pending a declared native scalar accessor |
| `null_with_reason` wrappers | Refused pending the reason return type and native text extent/ownership projection |
| Nullable/unknown foreign function-pointer results or nested slots | Refused where no boundary adapter is admitted |
| Windows LLP64, macOS and other project targets | Future explicit target/header/sysroot/link contracts; the project CLI currently selects Linux LP64 |

Native callback source checking and native compilation have different outcomes.
The separate live NativeCallbacks investigation reached source checking without
source errors, then received the missing-ABI-publication refusal during native
witnessing. It did not establish native execution. The repair belongs to Baker's
settled ABI/adaptation rows and their PSG publication; Alex must consume those
facts rather than infer an ABI or restore casts.

This supersedes earlier pthread/display handoff claims about nonnull callback
defaults or completed native callback realization. Existing desktop libraries
have not been regenerated or accepted wholesale by this checkpoint. Future
desktop bindings belong in `Fidelity.Platform` and must pass their own gates.

## Generate and regenerate

Keep the pilot, native header/library versions, target and generated source under
review together. For C pilots, set `c_header_mode = true`; declare namespace
selection, reference/text projections, nonnull assertions and ownership only
where the library contract supplies the evidence. The `project` command has no
cross-target `--data-model` flag.

From this checkout, build the current CLI and generate with its supported flags:

```sh
dotnet build src/Farscape.Cli/Farscape.Cli.fsproj
dotnet run --no-build --project src/Farscape.Cli/Farscape.Cli.fsproj -- project --project /absolute/path/to/library.pilot.toml --verbose
# Add --wrappers only when the selected wrapper contracts are supported.
```

For regeneration, use a copy of the pilot whose `[output].directory` names a fresh
review directory. That path is resolved relative to the pilot file. Compare the
returned source files and manifest with the owned package; keep authored Layer 2
overlays separate and include them explicitly in the consumer manifest. Farscape
does not remove stale files, so reconcile the reviewed generated file set before
replacing package output. Preserve the accepted package while investigating a
refusal; do not remove the old output before a new generation is accepted.

Run the complete source suite and the durable conformance driver against source:

```sh
dotnet build tests/Farscape.Tests/Farscape.Tests.fsproj
dotnet test tests/Farscape.Tests/Farscape.Tests.fsproj --no-build --no-restore
dotnet run --project tests/BoundaryConformance/BoundaryConformance.fsproj
dotnet run --no-build --project tests/BoundaryConformance/BoundaryConformance.fsproj -- --check-project /absolute/path/to/consumer.fidproj
```

Use the shared daemon's corresponding build/test leases when working in its
coordinated workspace. The conformance project builds the sibling Clef compiler;
its [README](../../tests/BoundaryConformance/README.md) documents compiler and
platform selection. The consumer check records actual source errors without
turning them into expected negative-suite outcomes. After source admission, each
library still needs supported native compilation, measured ABI and lifecycle
checks, and native execution evidence before the bindings are accepted.

## Accepted validation

The final source rebuild succeeded; the full unfiltered Farscape suite passed
**649/649**, with no failures or skips. The durable generator/compiler driver
passed **17/17** gates using actual clang-parsed headers and current-source Clef.
It retains generated input, parse results, full diagnostics and loaded assembly
hashes. Its positive scalar gate and expected unsupported-contract gates are
separate. It executes no C or native Clef program and proves no callback ABI or
foreign lifetime.

| Accepted artifact/evidence | SHA-256 or location |
|---|---|
| Farscape.Core | `f7d5a08c7ee6b5e458502caca8f3302f38cd2bba6735b1503c489ec4fc443d8d` |
| Farscape.Tests | `fb073b840ed034e147bacfc801d2892254d60c275e306fa5e7e2ad0dda492631` |
| Accepted source manifest | `dca57b666a97fffb2d8d2646758f661c37c921feeee4f462017bd33eee739252` |
| Loaded Clef.Compiler.Service | `71b86f131b4930d2bb026c606d57e386a0ba6734955e0b535206d44c9a7954fc` |
| BoundaryConformance runner | `bc6dc8a38e992dc79ae276c26d291d81522a67276809246074324a552c11d5b8` |
| Source build log | `/tmp/farscape-protocol-public-guard-build.log` |
| Source test report and hashes | `/tmp/farscape-protocol-public-guard-validation/` |
| Final driver build log | `/tmp/farscape-boundary-conformance-protocol-final-build.log` |
| Final captured inputs/results | `/tmp/farscape-boundary-conformance-08610afbee9249b09b1d9b6edde2a0be/` |
| Before/after snapshots and full run log | `/tmp/farscape-boundary-conformance-protocol-final-validation/` |

The temporary evidence paths identify this local accepted run; they are not
portable repository artifacts. The checked-in driver and fixtures are the
reproduction path. Rebuild and retain fresh evidence after changing the compiler,
generator, pilot, native headers or selected target.
