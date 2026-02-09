# Nested Accumulator Antipattern - Architectural Mistake

## Problem
Witnesses creating nested `MLIRAccumulator` instances caused errors to accumulate in child accumulators that were never merged back to the root. Result: errors silently lost, broken MLIR passed to mlir-opt.

## Discovery Method
Hash code logging revealed multiple accumulator instances:
- Accumulator 58661226: Created by executeNanopasses, checked by MLIRTransfer (0 errors)
- Accumulator 45787398: Created by LambdaWitness, received 10 errors
- Different objects → errors in wrong accumulator → silent failure

## Root Cause
F# record value semantics mean `{ ctx with ... }` creates NEW records. While the Accumulator field is a reference (class), calling `MLIRAccumulator.empty()` creates a NEW object, breaking the shared reference chain.

## Antipattern (NEVER DO THIS)
```fsharp
let witnessScope ctx node =
    let nestedAcc = MLIRAccumulator.empty()  // ❌ Creates NEW accumulator
    let nestedCtx = { ctx with Accumulator = nestedAcc }
    visitAllNodes witness nestedCtx node ctx.GlobalVisited
    nestedAcc.AllOps  // ❌ Errors in nestedAcc.Errors are LOST
```

## Correct Pattern
```fsharp
let witnessScope ctx node =
    let opsBefore = List.length ctx.Accumulator.AllOps
    let scopeCtx = { ctx with Zipper = scopeZipper }  // Only change zipper, NOT accumulator
    visitAllNodes witness scopeCtx node ctx.GlobalVisited

    // Extract operations by list diff
    let opsAfter = List.length ctx.Accumulator.AllOps
    let scopeOpsReversed = ctx.Accumulator.AllOps |> List.take (opsAfter - opsBefore)
    List.rev scopeOpsReversed  // ✅ Errors automatically in shared ctx.Accumulator
```

## Key Principle
**ONE accumulator for ALL witnesses.** Never call `MLIRAccumulator.empty()` except in executeNanopasses.

- **Operations**: Scoped (extracted by diff for wrapping in FuncDef/SCFOp)
- **Errors**: Global (single list in shared accumulator)
- **Bindings**: Global (single NodeAssoc map in shared accumulator)
- **Visited**: Global (single ref<Set<NodeId>> in WitnessContext)

## Affected Files
- LambdaWitness.fs: Creates nested accumulators (lines 68, 172) ❌
- ControlFlowWitness.fs: Fixed to use shared accumulator ✅

## Verification
Hash code logging shows all mutations target same accumulator instance:
```bash
Firefly compile ... 2>&1 | grep "accumulator.*hash" | sort -u
# Should show ONLY ONE unique hash code
```

## Reference
- Plan: `/home/hhh/.claude/plans/witness-accumulator-architecture-fix.md`
- Reference implementation: `ControlFlowWitness.fs:witnessBranchScope` (lines 39-74)

## Discovered During
Sample 02 FFI cleanup (February 2026) - errors were being returned but showing as "0 errors" in final count, revealing the nested accumulator antipattern.
