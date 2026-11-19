# Rules for work in this repository

These are owner requirements. They apply to every change, by a person or an agent.

## Authority and programme ownership

- The [Farscape wiki](https://forge.spkez.dev/FidelityFramework/Farscape/wiki) and issue list are authoritative for the Fidelity Framework FFI programme: [Definition of Done](https://forge.spkez.dev/FidelityFramework/Farscape/wiki/Definition-of-Done), [Architectural Decision Ledger](https://forge.spkez.dev/FidelityFramework/Farscape/wiki/Architectural-Decision-Ledger), [FFI Working Agreements](https://forge.spkez.dev/FidelityFramework/Farscape/wiki/FFI-Working-Agreements), and [Course Correction 2026 11 19](https://forge.spkez.dev/FidelityFramework/Farscape/wiki/Course-Correction-2026-11-19).
- Every issue created across Fidelity repositories must carry a milestone at creation time (`MILESTONE-ON-CREATE`). An implementer never closes an issue it implemented (`AUDITED-CLOSURE`).

## Binding and boundary rules

- Farscape generates `[<FidelityExtern>]` binding declarations and checked BAREWire descriptors only. No P/Invoke (`DllImport`), no raw pointers or C idioms past Layer 2, no invented ownership (`Transfer.Undeclared` when unknown), and no comment-narrowed nullable callbacks (`Option<FnPtr>`).
- Respect the three-layer output model (`LAYER3-COMPOSITION`): Layer 1 and Layer 2 output for a library never depends on another generated library; cross-library composition belongs in a Layer 3 package.
- Provenance strata hold strictly: `measured` (clang record-layout dump) > `derived` > `declared` (pilot file) > `inferred`. Lower strata may refine higher strata and may never contradict measurement.
- Clang is used here strictly for C/C++ header parsing, macro evaluation, layout measurement, and compiling independent C/C++ fixtures or archives; generated Clef/Composer IR is never routed through Clang or `llc` (`BACKEND-ROUTE`).

## Code quality and Zone A/B discipline

- `Farscape.Core` pure analysis, codegen, and substantiation modules follow Zone A discipline (`fidelity-clef-core-boundary`): closed `Result` values, `Fidelity.Data.JSON` explicit codecs, zero CLR throws, zero `System.Reflection` or `Microsoft.FSharp.Reflection`, zero `ConditionalWeakTable`, and cohesive domain-separated modules (do not grow monolithic multi-domain files).
- Host CLI, filesystem, and clang/archive process spawning belong cleanly at the Zone B edge (`Farscape.Cli` / pinned toolchain adapters).
- Use .NET tooling (`dotnet`, `dotnet fsi`). Do not introduce or run Python automation.

## Lookup and audit discipline

- Load `fidelity-lookup` before the first lookup of a session, `fidelity-clef-core-boundary` before modifying core modules, and `fidelity-blind-audit` before auditing or signing off on a tranche.
- Every rule or refusal needs a positive control, an exact-reason negative control, and a fault-injection negative control (`PLAIN-ENGINEERING-TERMS`), backed by real library compilation/link evidence rather than synthetic mocks alone.
