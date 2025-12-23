# Farscape-fsnative Integration Contract

## The Integration Surface

Farscape generates output that fsnative's nanopass pipeline consumes. The integration point is the `MemoryModel` record type.

## What Farscape Provides

### 1. MemoryModel Record

```fsharp
type MemoryModel = {
    /// Target family identifier (e.g., "STM32L5", "NRF52")
    TargetFamily: string
    
    /// Quotations encoding peripheral memory layout
    PeripheralDescriptors: Expr<PeripheralDescriptor> list
    
    /// Quotations encoding access constraints
    RegisterConstraints: Expr<RegisterConstraint> list
    
    /// Quotation encoding memory regions
    Regions: Expr<RegionDescriptor list>
    
    /// Active pattern function for PSG recognition
    Recognize: PSGNode -> MemoryOperation option
    
    /// Optional cache topology (for optimization)
    CacheTopology: Expr<CacheLevel list> option
    
    /// Optional coherency model (for multi-core)
    CoherencyModel: Expr<CoherencyPolicy> option
}
```

### 2. Quotations for Nanopass Consumption

Quotations carry information that fsnative nanopasses can inspect:

```fsharp
// Farscape generates
let gpioQuotation: Expr<PeripheralDescriptor> = <@
    { Name = "GPIO"
      Instances = Map.ofList [("GPIOA", 0x48000000un)]
      Layout = { Size = 0x400; Alignment = 4; Fields = [...] }
      MemoryRegion = Peripheral }
@>

// fsnative nanopass can decompose
match gpioQuotation with
| <@ { Name = name; MemoryRegion = Peripheral; _ } @> ->
    // Mark all access as volatile
| <@ { MemoryRegion = Flash; _ } @> ->
    // Emit read-only constraints
```

### 3. Active Patterns for Recognition

fsnative uses the `Recognize` function during PSG traversal:

```fsharp
// In fsnative nanopass
let enrichMemorySemantics (model: MemoryModel) (node: PSGNode) =
    match model.Recognize node with
    | Some (PeripheralOp op) ->
        // Attach volatile semantics
        node |> withVolatile |> withAccessKind op.Access
    | Some (DmaOp op) ->
        // Attach DMA semantics
        node |> withDmaMarker op.Channel
    | None ->
        // Normal memory operation
        node
```

## What fsnative Expects

### Type Definitions in Scope

fsnative expects these types to be defined (by BAREWire):

```fsharp
type PeripheralDescriptor = { ... }
type FieldDescriptor = { ... }
type AccessKind = ReadOnly | WriteOnly | ReadWrite
type MemoryRegionKind = Flash | SRAM | Peripheral | SystemControl | DMA | CCM
type RegisterConstraint = { ... }
type RegionDescriptor = { ... }
```

### PSGNode Pattern Matching

The `Recognize` function receives `PSGNode` and must handle:

```fsharp
type MemoryOperation =
    | PeripheralOp of PeripheralAccessInfo
    | DmaOp of DmaOperationInfo
    | SystemOp of SystemControlInfo
    | FlashRead of FlashReadInfo
```

### Quotation Structure

Quotations must be decomposable. Use record literals, not function calls:

```fsharp
// ✅ Decomposable
<@ { Name = "GPIO"; MemoryRegion = Peripheral } @>

// ❌ Not decomposable
<@ createPeripheral "GPIO" Peripheral @>
```

## The Dependency Chain

```
BAREWire (types) ← Farscape (quotations) ← fsnative (consumption)
```

1. **BAREWire** provides type definitions
2. **Farscape** generates quotations using those types
3. **fsnative** pattern-matches on quotations in nanopasses

## Registration Flow

```fsharp
// Farscape generates a registration module
module STM32L5.Registration =
    let memoryModel: MemoryModel = {
        TargetFamily = "STM32L5"
        PeripheralDescriptors = [gpioQuotation; usartQuotation; ...]
        RegisterConstraints = [accessConstraints]
        Regions = regionQuotation
        Recognize = recognizeSTM32L5Operation
        CacheTopology = None
        CoherencyModel = None
    }

// fsnative loads at compile time
let models = [
    STM32L5.Registration.memoryModel
    NRF52.Registration.memoryModel
    // ...
]
```

## Error Reporting

Farscape-generated patterns should provide good error context:

```fsharp
let (|WriteToReadOnly|_|) node =
    match node with
    | RegisterWrite { Register = reg } when reg.Access = ReadOnly ->
        Some {
            Register = reg.Name
            Peripheral = reg.Peripheral
            Message = $"Cannot write to read-only register {reg.Name}"
            Suggestion = "Use a read-write register or check hardware documentation"
        }
    | _ -> None
```

## Canonical Reference

See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md` for the complete integration architecture.
