# Architectural Discoveries - FFI Cleanup (Jan 2026)

## Discovery Context

During FFI architecture realignment, we removed legacy helper functions (`Console.readLineInto`) that existed before PSG enrichment and Baker saturation. This revealed hidden bugs that the indirection was masking.

## Discovery: Control Flow Witnessing Order Bug

**Date:** January 2026
**Context:** Removed `Console.readLineInto` from Fidelity.Platform, inlined logic into `readln()` using `Sys.read` intrinsic directly

**Symptom:** While loop bodies with nested conditionals emit MLIR operations in wrong order
- `scf.if %v107` uses undefined SSA value
- The `@read` call that produces the value is emitted AFTER the if statement that checks it

**Root Cause:** Zipper traversal or witnessing order for while loop bodies is incorrect

**Expected Order:**
```mlir
1. %bytesRead = func.call @read(...)
2. %condition = arith.cmpi sle, %bytesRead, 0
3. scf.if %condition { ... }
```

**Actual Order:**
```mlir
1. scf.if %v107 { ... }    // ERROR: %v107 undefined
2. %bytesRead = func.call @read(...)
```

**Location:** While loop in `Console.readln()` (Console.fs:33-44)
**MLIR Output:** `/samples/.../target/intermediates/07_output.mlir:47,50`

## Architectural Lessons

1. **Legacy indirection masked bugs** - Helper functions that pre-date PSG enrichment can hide structural issues
2. **Cleanup reveals truth** - Removing workarounds exposes where the architecture needs strengthening
3. **FFI works correctly** - The 3-stage conversion (memref → index → platform-word) is functioning as designed
4. **Control flow needs attention** - While/if nesting has traversal order issues separate from FFI

## Related Discoveries

- Branch scope binding (fixed - accumulators now inherit parent bindings)
- Comparison operand types (fixed - use operand type not result type)
- LLVM→MLIR memref.load semantics (fixed - indices required)

## Next Steps

Investigate control flow witnessing order in:
- `src/MiddleEnd/Alex/Witnesses/ControlFlowWitness.fs`
- `src/MiddleEnd/Alex/Traversal/PSGZipper.fs`
- While loop body traversal order
