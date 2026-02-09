# Control Flow Witnessing Order Architecture

## Problem
Operations in control flow scope bodies (while loops, if-then-else branches) accumulated in reverse order due to post-order traversal, violating SSA dominance in MLIR output.

**Symptom:** `scf.if %v107` appeared in MLIR before `%v107 = arith.cmpi ...` was defined, causing "value does not dominate this use" verification errors.

## Root Cause
- `visitAllNodes` uses post-order traversal (children before parents)
- Operations naturally accumulate in reverse program order during recursion
- `LambdaWitness` correctly reverses with `List.rev` ✅
- `ControlFlowWitness.witnessBranchScope` missing reversal ❌

## Solution Pattern
Apply operation reversal at **scope boundaries** before returning to parent:

```fsharp
let scopeResult = visitAllNodes scopeZipper acc witnessNode
let ops = MLIRAccumulator.getOperations scopeResult
let reversedOps = List.rev ops  // Restore correct SSA order
let correctedAcc = MLIRAccumulator.withOperations scopeResult reversedOps
```

## Scope Boundaries (Need Reversal)
- Lambda bodies (`LambdaWitness.witnessLambdaBody`) ✅ Already correct
- While loop bodies (`ControlFlowWitness.witnessBranchScope`) ❌ Missing reversal
- If-then-else branches (`ControlFlowWitness.witnessIfThenElse`) ❌ Missing reversal

## Non-Boundaries (No Reversal)
- Sequential nodes (already in correct order from Builder)
- Single-operation witnesses (no accumulation occurs)

## Architectural Principle
**Post-order traversal is correct** for PSG structure modeling (dependencies flow correctly). Reversal is a **presentation concern** at MLIR emission time, NOT a traversal concern.

```
PSG Structure (post-order) → Accumulator (reversed) → MLIR Emission (List.rev) → Correct SSA order
```

## Verification Steps
1. MLIR output: `%value = op ...` appears BEFORE any `use %value`
2. `mlir-opt --verify-each` passes without SSA dominance errors
3. Binary execution succeeds without segfaults

## Discovered During
FFI architecture cleanup (January 2026) when Sample 02 showed correct FFI conversion but broken control flow MLIR output. This was discovered as a "breadcrumb" during architectural normalization.

## Reference Implementation
`LambdaWitness.fs` lines 86-91 provides the canonical reversal pattern that should be applied to all control flow scope boundaries.

## Future Control Flow Constructs
When implementing for loops, try-catch, pattern matching, or any new control flow construct with scope boundaries, apply this same reversal pattern at emission time.
