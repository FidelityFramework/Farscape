# Native Callback ABI Gate

Native callback entry validation runs as typed F# tests in `tests/Farscape.Tests/NativeCallbackRunnerTests.fs` (`#207`), replacing the retired `tests/native-callbacks/run.py` script.

## Execution under a Bozzetto Lease

```bash
BOZZETTO_AGENT=<agent> BOZZETTO_PROJECT=Farscape \
  dotnet fsi ../Bozzetto/tools/build/lease.fsx test_suite_run \
  dotnet test tests/Farscape.Tests/Farscape.Tests.fsproj --no-build --nologo \
  --filter "FullyQualifiedName~NativeCallbackRunnerTests"
```

Missing prerequisites (`Composer.dll`, `Fidelity.Platform.CompilerSurface.fidproj`, `BAREWire.BindingMetadata.fidproj`, `clang`, `llvm-ar`, `dotnet`, `git`) fail the requiring test directly; no test is skipped.

## Toolchain Boundary (`BACKEND-ROUTE`)

- Foreign C probe translation units (`probe.c`, `other_probe.c`) are compiled and archived into `liblandingprobe.a` exclusively through `ClangToolchain` (`FIDELITY_CLANG` or `clang` on `PATH`; `FIDELITY_LLVM_AR`, `llvm-ar` beside the resolved `clang`, or `llvm-ar` on `PATH`).
- `ClangToolchain` never reads `FIDELITY_LLVM_BIN`, which belongs strictly to Composer's backend manifest (`opt`, `ld.lld`, `lld`, `llvm-dis`).
- Sibling project overrides: `COMPOSER_DLL`, `FIDELITY_PLATFORM_PROJECT`, and `BAREWIRE_METADATA_PROJECT`.

## Verified Journeys and Refusal Controls

All journey work directories live beneath the checkout under `test-results/native-callbacks/<label>/` and are retained on disk so every recorded manifest path in `test-results/native-callbacks/<label>.sha256` remains byte-verifiable via `sha256sum -c`.

1. `single-entry`: generates bindings for `probe.h`, compiles `Loading.clef`, `Boundary.clef`, and `Main.clef` with `Composer.dll compile ... -k`, links static `liblandingprobe.a`, verifies parsed `10_output.mlir` callback entry signatures, `func.constant` symbol addresses, and every invoke `func.call` operand alongside `06c_psg_discharge.json` (`psg`) and `09_mlir_discharge.json` (`mlir`) `unsat` receipts against `06a_obligations.json`, runs the linked native binary, and verifies all recorded SHA-256 digests under `test-results/native-callbacks/single-entry.sha256`.
2. `duplicate-entry`: generates identical callback typedef names (`LandingCallback`, `LandingVoid`) from `probe.h` and `other_probe.h` under distinct namespaces (`Landing.Boundary` and `Landing.Other.Boundary`), links both witnesses into one executable, and verifies disjoint entry symbols, invoke operands, discharge receipts, native execution, and `test-results/native-callbacks/duplicate-entry.sha256`.
3. `refused-witness`: verifies the unmutated control compiles and links cleanly, mutates callback return and argument types in `Boundary.clef`, and verifies `CCS8003` refusal at the record construction line with zero emitted binary.
4. `closure-conversion`: verifies both the unmutated control and a local-binding control (`let _offset = invokeVoid ()` with closed `scalarEntry`) compile and run cleanly, then verifies nested-function and lambda arguments to `FnPtr.ofFunction` fail closed with exact `CCS8096` and `CCS8207` diagnostics at the record construction line and zero emitted binary.
5. `unsettled-entry`: generates opaque-handle and `void *` `CallbackContextDescriptor` declarations (`#189`) and verifies Composer fails closed (`CCS8403` / `CCS8413`) pending `#211`.
6. Repository `.py` check: verifies `git ls-files '*.py'` and repository source trees hold zero Python files.
