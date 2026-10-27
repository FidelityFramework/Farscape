# FFI Definition of Done and Completion Ledger

**Status: proposed, pending owner ratification (2026-10-27).** This document is the
shared yardstick for C and C++ binding work across Farscape, BAREWire,
Fidelity.Platform, CCS/Baker, Fidelity.PSG, Alex, Composer and Bozzetto. Implementer,
auditor and owner grade progress against it. The Bozzetto
`docs/ffi-correction-compendium.md` remains the increment narrative and evidence
index; completion status lives here.

## Rules

1. No percentages, weighted counts or "N of M families" summaries.
2. A gate is **Done** only for the area's declared scope, and only with evidence of
   the type the gate names, linked from the ledger row.
3. An accepted bounded profile is not a completed area.
4. A status changes in the same changeset as its evidence, and the independent
   auditor's verdict for that increment confirms the change.
5. Undeployed source work, a red baseline and an undecided owner question are
   conditions on completion, not footnotes.

## Gates

Every area passes every gate for its declared scope.

| Gate | Done means | Required evidence |
|---|---|---|
| D1 Contract | clef-lang-spec covers every in-scope shape; every owner decision the area needs is decided; out-of-scope shapes list the refusal each must produce | spec revision and lines; decision record |
| D2 Generation | Farscape emits the contract for each in-scope shape from real headers with provenance strata, and refuses out-of-scope shapes before writing | per-shape positive and exact-refusal tests; one killed mutant per rule; unfiltered Farscape suite green at a pushed head |
| D3 Admission | CCS/Baker admits each in-scope shape and derives its obligations with actual participants; refuses others with their codes | clef tests per shape, positive and exact-code negative; killed mutants |
| D4 Publication | the PSG revision carries every fact consumers need; Integrity passes; schema recorded | PSG and clef publication tests |
| D5 Witnessing | Alex reads published facts only; no reconstruction | Alex tests per shape; analyzer gate run on touched code with no new findings |
| D6 Lowering | Composer lowers each shape and artifact correspondence checks pass | Composer tests per shape |
| D7 Native | a real Clef program per shape builds through open, reserve, build and run_current on each claimed target with the expected result; refusal programs fail with the expected code | leased native journey receipts: exit code, output, artifact digest, toolchain identity |
| D8 Obligations | every obligation is Discharged at a stated tier, with SAT(H) and UNSAT(H and not C) for refutations, or Pending with its external hypothesis; trusted base listed | obligation census receipt from the graph |
| D9 Real consumer | one maintained library that needs the area, non-libc where the area applies, is regenerated with the accepted generator and runs natively; spec, Platform, samples and editor consumers synchronized | regeneration diff, native receipt, sync record |
| D10 Differential | evidence reuse is correct, or the owner has accepted the area as non-reusable for now with the follow-up recorded | differential journey receipts or owner record |
| D11 Shipped | pushed clean heads; matching daemon and worker pair installed from them; LAN checkouts synchronized; auditor accepted at stated scope | release id, audit verdict, sync receipts |

Status values: **Done**, **Partial** (name the shapes), **Open**, **Refused** (code),
**Blocked** (decision id), **NR** (owner-accepted non-reusable), **N/A** (justified).

## Areas and provisional status

Provisional: taken from the 2026-10-27 assessment. No cell counts until it is
re-graded with an evidence link. Native evidence today covers libc-shaped calls on
Linux x86-64 only; the compiler admits libc plus one explicitly selected library per
program (clef `6c574a39`, `BoundaryRecipes.fs:1443-1449`).

| Area | Family | D1 | D2 | D3 | D4 | D5 | D6 | D7 | D8 | D9 | D10 | D11 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Scalar and extern calls | T1 | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Open | NR | Partial |
| Closed callbacks | T2 | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Open | NR | Partial |
| Optional callbacks | T4 | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Open | NR | Partial |
| Resources and handles | T3 | Blocked | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Open | NR | Partial |
| Buffers and text | T6 | Blocked | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Open | NR | Partial |
| Captured user data | T5 | Blocked | Open | Refused | Open | Open | Open | Open | Open | Open | Open | Open |
| Native callback tables | T6-T | Blocked | Open | Open | Open | Open | Open | Open | Open | Open | Open | Open |
| Loading and ABI | T7 | Blocked | Partial | Partial | Partial | Partial | Partial | Partial | Partial | Open | NR | Partial |
| Device access, MMIO | — | Partial | Partial | Partial | Partial | Partial | Partial | Open | Partial | Open | NR | Open |

Scope to be declared per area at ratification: the in-scope shapes, the deferred
shapes with their required refusals, and the blocking decisions. Known blockers:
T3 LT-1 to LT-4; T6 LT-10 to LT-12; T5 LT-9 and LT-18; T6-T LT-7 and LT-8; T7 LT-13,
LT-14 and LT-17; Phase B Q2 to Q4.

## Cross-cutting tracks

| Track | Exit criteria | Status 2026-10-27 |
|---|---|---|
| Farscape generation and provenance | every measured fact paired with its declaring unit; no lower stratum overrides a higher one; C++ either generated or refused | multi-header correction uncommitted; four further gaps: inferred enum widths, profile scalar table before unit typedefs, C++ headers probed as C, macros merged by name |
| Real-library consumers | each maintained binding regenerated by the accepted generator, raw pointer surface removed, one non-libc library native end to end | pthread source-checked only; Wayland, DRM, GBM, resvg from a March generation; desktop frozen |
| Integer types in source | Phase B Q3 decided and integrated | open; several fixed-width spellings refused |
| Alex passivity | combined analyzer gate green or every finding adjudicated | red, 452 findings unadjudicated; next candidate paused |
| Proofs and trusted base | proof toolchain consistent; trusted base enumerated per area | Rocq 9.3 against Stdlib 9.2 blocks proof-backed cases |
| Differential reuse | owner records non-reusable acceptance, or reuse proven | evidence non-reusable; SSA reassignment unsettled |
| Toolchain and LAN | installed pair matches pushed heads; LAN checkouts synchronized | installed pair dates from Oct 17; LAN behind |
| Test baseline | every failing test attributed to an owner and a tracked item | Composer artifact classes fail 116 of 487 at HEAD; T4 kept 28 attributed failures |
| Owner decisions | each blocker above decided | LT-1 to LT-18, OQ1 to OQ9, Q2 to Q4 open |

## Next planning steps

1. Owner ratifies the gates and status values, or amends them.
2. For each area, the owner fixes the in-scope and deferred shapes.
3. Each provisional cell is re-graded with an evidence link; the auditor confirms.
4. Work resumes only against the first non-Done gate of a ratified area.
