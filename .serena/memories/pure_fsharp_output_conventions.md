# Farscape Pure F# Output Conventions

## Core Principle: Generate Pure F#

All Farscape output must use pure F# idioms:
- Records, not interfaces
- Discriminated unions, not enums with attributes
- Module functions, not static methods
- Active patterns, not visitor patterns

## What Farscape Generates

### ✅ Record Types
```fsharp
// Generated MemoryModel
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

### ✅ Discriminated Unions
```fsharp
// Generated from C enum
type GPIO_PinState =
    | Reset
    | Set

// Generated operation type
type GpioOperation =
    | Init of GpioInitInfo
    | WritePin of GpioWritePinInfo
    | ReadPin of GpioReadPinInfo
    | TogglePin of GpioTogglePinInfo
```

### ✅ Active Patterns
```fsharp
// Generated recognition patterns
let (|HalGpioWritePin|_|) (node: PSGNode) : GpioWritePinInfo option = ...
let (|PeripheralAccess|_|) (node: PSGNode) : PeripheralAccessInfo option = ...
```

### ✅ Module Functions
```fsharp
// Generated API module
module GPIO =
    let writePin port pin state = ...
    let readPin port pin = ...
    let togglePin port pin = ...
```

### ✅ Quotations
```fsharp
// Generated memory descriptors
let gpioQuotation: Expr<PeripheralDescriptor> = <@ ... @>
```

## What Farscape Does NOT Generate

### ❌ Interfaces
```fsharp
// WRONG - don't generate this
type IPeripheralProvider =
    abstract GetDescriptor: unit -> PeripheralDescriptor
```

### ❌ Abstract Classes
```fsharp
// WRONG - don't generate this
[<AbstractClass>]
type PeripheralBase() =
    abstract GetRegisters: unit -> Register list
```

### ❌ I-Prefix Names
```fsharp
// WRONG
type IMemoryModel = ...

// RIGHT
type MemoryModel = ...
```

### ❌ BCL Attributes (unnecessary ones)
```fsharp
// WRONG
[<System.Serializable>]
type GpioConfig = ...

// RIGHT - just the record
type GpioConfig = { ... }
```

## Acceptable Attributes

```fsharp
[<Struct>]              // Value type optimization - OK
[<RequireQualifiedAccess>]  // Module access control - OK
[<AutoOpen>]            // Convenience - OK
[<Measure>]             // Units of measure - OK
```

## Platform Bindings Convention

For extern declarations, use the module convention (not DllImport):

```fsharp
// Generated Platform.Bindings module
module Platform.Bindings =
    let halGpioInit gpio init : unit = Unchecked.defaultof<unit>
    let halGpioWritePin gpio pin state : unit = Unchecked.defaultof<unit>
    let halGpioReadPin gpio pin : GPIO_PinState = Unchecked.defaultof<GPIO_PinState>
```

Alex recognizes this pattern and provides platform-specific implementations.

## Why Pure F#?

1. **Native Compilation**: BCL patterns require runtime that doesn't exist in Fidelity
2. **F* Compatibility**: Future proof annotations will be attributes - minimize attribute clutter
3. **Quotation Friendly**: Records and DUs quote cleanly; interfaces don't
4. **Composition**: Pure F# composes better than OO hierarchies

## Canonical Reference

See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md`, "Design Principle 4: Pure F# Idioms"
