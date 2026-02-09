# Farscape-FNCS Integration Contract

## The Closed-Loop Pipeline

Farscape generates F# source → FNCS type-checks in NTU → Baker saturates intrinsics → Alex emits MLIR → LLVM → native binary.

This is a closed system. Every component carries binding intent forward.

## What Farscape Generates

### 1. `[<FidelityExtern>]` Attributed Stubs (core infrastructure)

```fsharp
[<FidelityExtern("libc", "memcpy")>]
let memcpy (dest: nativeint) (src: nativeint) (n: nativeint) : nativeint =
    Unchecked.defaultof<nativeint>
```

FNCS recognizes `[<FidelityExtern>]` and carries library name + symbol through the PSG. Alex emits MLIR with `fidelity.binding_strategy` and `fidelity.library_name` attributes. The linker auto-collects all referenced libraries and generates appropriate flags (`-lc`, etc.).

**Current state**: Stubs generate without the attribute; Alex infers from naming conventions. Adding `[<FidelityExtern>]` is core infrastructure that closes the pipeline loop.

**There is NO P/Invoke in the Fidelity framework. Only FidelityExtern.**

### 2. MemoryModel Record (for embedded/CMSIS targets)

```fsharp
type MemoryModel = {
    TargetFamily: string
    PeripheralDescriptors: Expr<PeripheralDescriptor> list
    RegisterConstraints: Expr<RegisterConstraint> list
    Regions: Expr<RegionDescriptor list>
    Recognize: PSGNode -> MemoryOperation option
    CacheTopology: Expr<CacheLevel list> option
    CoherencyModel: Expr<CoherencyPolicy> option
}
```

### 3. Quotations for Nanopass Consumption

Quotations carry hardware memory layout that FNCS nanopasses can inspect:

```fsharp
let gpioQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "GPIO"
      Instances = Map.ofList [("GPIOA", 0x48000000un)]
      Layout = { Size = 0x400; Alignment = 4; Fields = [...] }
      MemoryRegion = Peripheral }
@>
```

Quotations must be decomposable (record literals, not function calls).

### 4. Active Patterns for PSG Recognition

FNCS uses the `Recognize` function during PSG traversal:

```fsharp
let enrichMemorySemantics (model: MemoryModel) (node: PSGNode) =
    match model.Recognize node with
    | Some (PeripheralOp op) -> node |> withVolatile |> withAccessKind op.Access
    | Some (DmaOp op) -> node |> withDmaMarker op.Channel
    | None -> node
```

## What FNCS Expects

### Type Definitions (provided by BAREWire)

```fsharp
type PeripheralDescriptor = { ... }
type FieldDescriptor = { ... }
type AccessKind = ReadOnly | WriteOnly | ReadWrite
type MemoryRegionKind = Flash | SRAM | Peripheral | SystemControl | DMA | CCM
```

### PSG Memory Operations

```fsharp
type MemoryOperation =
    | PeripheralOp of PeripheralAccessInfo
    | DmaOp of DmaOperationInfo
    | SystemOp of SystemControlInfo
    | FlashRead of FlashReadInfo
```

## The Dependency Chain

```
BAREWire (types) ← Farscape (quotations + FidelityExtern stubs) ← FNCS (consumption + type checking)
```

1. **BAREWire** provides type definitions (PeripheralDescriptor, etc.)
2. **Farscape** generates quotations using those types + FidelityExtern stubs
3. **FNCS** type-checks stubs, pattern-matches quotations in nanopasses

## Registration Flow

```fsharp
module STM32L5.Registration =
    let memoryModel: MemoryModel = {
        TargetFamily = "STM32L5"
        PeripheralDescriptors = [gpioQuotation; usartQuotation]
        RegisterConstraints = [accessConstraints]
        Regions = regionQuotation
        Recognize = recognizeSTM32L5Operation
        CacheTopology = None
        CoherencyModel = None
    }
```

## Error Reporting

Farscape-generated patterns provide hardware-aware error context:

```fsharp
let (|WriteToReadOnly|_|) node =
    match node with
    | RegisterWrite { Register = reg } when reg.Access = ReadOnly ->
        Some { Register = reg.Name; Peripheral = reg.Peripheral
               Message = $"Cannot write to read-only register {reg.Name}"
               Suggestion = "Use a read-write register or check hardware documentation" }
    | _ -> None
```

## Alex's Role

Alex works ONLY with MLIR. It receives binding metadata via MLIR attributes emitted from the PSG:
- `fidelity.binding_strategy` (static or dynamic)
- `fidelity.library_name` (library identifier)

Alex does NOT work with F# source or P/Invoke. It transforms MLIR based on binding strategy configuration.

## Canonical Reference

See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md` for the complete integration architecture.
