# Operation Fusion Pattern - Compound Operations in Alex Witnesses

## Problem Domain

Some F# operations are compound - one operation's result is only meaningful when consumed by another operation, and the intermediate representation doesn't map directly to MLIR primitives.

Examples:
- `NativePtr.add(base, offset)` + `NativePtr.write(ptr, value)` - pointer arithmetic + dereference
- `LazyExpr(body)` + `LazyForce(lazy)` - lazy construction + evaluation
- Builder patterns - intermediate builder state consumed by final Build operation

## The Generalizable Pattern

**Consumer-Producer Fusion:** The consuming operation recognizes the producing operation in the PSG, extracts needed data, marks producer as visited, and emits fused MLIR.

### Architecture

```
Producer Operation (e.g., NativePtr.add)
    ↓ Creates PSG node, but doesn't witness independently
Consumer Operation (e.g., NativePtr.write)
    ↓ Recognizes producer pattern in argument
    ↓ Extracts producer's arguments (base, offset)
    ↓ Marks producer node as visited (operation fusion)
    ↓ Binds producer result (for correctness)
    ↓ Emits fused MLIR operation
```

### Implementation Pattern (from NativePtr.write)

```fsharp
| IntrinsicModule.NativePtr, "write", [ptrSSA; valueSSA] ->
    let ptrNodeId = argIds.[0]  // Get producer node ID
    
    match SemanticGraph.tryGetNode ptrNodeId ctx.Graph with
    | Some ptrNode ->
        // Check if producer is NativePtr.add
        match ptrNode.Kind with
        | SemanticKind.Application (funcId, addArgIds) ->
            match SemanticGraph.tryGetNode funcId ctx.Graph with
            | Some funcNode ->
                match funcNode.Kind with
                | SemanticKind.Intrinsic info when 
                    info.Module = IntrinsicModule.NativePtr && 
                    info.Operation = "add" ->
                    
                    // FUSION: Extract producer's arguments
                    let baseId = addArgIds.[0]
                    let offsetId = addArgIds.[1]
                    
                    match MLIRAccumulator.recallNode baseId ctx.Accumulator,
                          MLIRAccumulator.recallNode offsetId ctx.Accumulator with
                    | Some (baseSSA, _), Some (offsetSSA, _) ->
                        // Mark producer as visited (consumed by fusion)
                        ctx.GlobalVisited.Value <- Set.add ptrNodeId ctx.GlobalVisited.Value
                        
                        // Bind producer result (for PSG correctness)
                        MLIRAccumulator.bindNode ptrNodeId offsetSSA TIndex ctx.Accumulator
                        
                        // Emit fused operation: write to base[offset]
                        emitWrite(valueSSA, baseSSA, offsetSSA)
```

### Key Techniques

1. **Producer Recognition:** Consumer checks if its argument is the expected producer operation
2. **Argument Extraction:** Consumer extracts producer's arguments from PSG
3. **Visited Marking:** Consumer marks producer as visited to prevent duplicate witnessing
4. **Result Binding:** Consumer binds producer's result to maintain PSG invariants
5. **Fused Emission:** Consumer emits single MLIR operation incorporating both operations

### When to Apply

Use operation fusion when:
- Producer operation has no direct MLIR equivalent
- Producer result is only meaningful to specific consumers
- Fusion reduces intermediate values and improves code quality
- Producer-consumer relationship is 1:1 or 1:N (predictable)

### Benefits

- **Reduced Intermediate Values:** No need to represent producer's abstract result in MLIR
- **Cleaner MLIR:** Compound F# operations → single MLIR operations
- **Type Safety:** Consumer validates producer's semantics at witness time
- **Architectural Clarity:** Producer is conceptual, consumer is concrete

### Related Patterns

- **Compound Pattern from ControlFlowWitness:** Branch scopes extract operations via list diff
- **Shared Accumulator Pattern:** All witnesses use same accumulator (no nesting)
- **Y-Combinator Pattern:** Recursive witnesses handle nested structures

### Reference Implementation

**File:** `src/MiddleEnd/Alex/Witnesses/ApplicationWitness.fs`
**Lines:** 77-118 (NativePtr.write with NativePtr.add fusion)

### Future Applications

- `LazyExpr` + `LazyForce` fusion
- `SeqExpr` + `ForEach` fusion  
- Builder pattern fusions (when we add those)
- Farscape peripheral access fusions (CMSIS HAL)

## Anti-Patterns to Avoid

❌ **Don't create standalone witness for producer** - leads to type representation problems
❌ **Don't use separate accumulator** - errors and bindings get lost
❌ **Don't forget to mark producer as visited** - causes duplicate witnessing errors
❌ **Don't forget to bind producer result** - breaks PSG recall invariants

## Verification Checklist

When implementing operation fusion:
- [ ] Consumer recognizes producer pattern in PSG
- [ ] Producer's arguments extracted via PSG traversal
- [ ] Producer node marked as visited (GlobalVisited.Value)
- [ ] Producer result bound in accumulator (bindNode)
- [ ] Fused MLIR operation emitted correctly
- [ ] Error handling: consumer fails gracefully if producer arguments not witnessed
- [ ] Build succeeds with 0 warnings
- [ ] Sample compilation shows no errors for the fusion pattern
