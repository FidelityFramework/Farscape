# Posit and Universal Numbers Design

## Overview

Posit arithmetic (John Gustafson's Type III Unum) provides an alternative to IEEE 754 floating point with:
- **Tapered precision**: More bits near 1.0, fewer at extremes
- **No NaN/Inf proliferation**: Single "Not a Real" (NaR) value
- **Exact accumulation**: Quire enables fused operations without intermediate rounding

## Posit Specification

Posits are parameterized by `(nbits, es)`:
- `nbits`: Total bit width
- `es`: Exponent size (0-3 typical)
- `useed = 2^(2^es)`: Base for regime scaling

Common configurations:
- Posit8 (8, 0): No exponent, pure tapered - good for ML weights
- Posit16 (16, 1): IEEE half replacement
- Posit32 (32, 2): IEEE float replacement
- Posit64 (64, 3): IEEE double replacement

## Bit Layout

```
[sign][regime][exponent][fraction]
  1    variable   es      remaining
```

The regime is run-length encoded:
- `1111...10` means k-1 (where k is count of 1s)
- `0000...01` means -k (where k is count of 0s)

This gives more precision near 1.0 (small regime) and less at extremes.

## Quire Accumulator

The quire is a large fixed-point register (512 bits for Posit32) that:
- Holds exact products without rounding
- Accumulates sums exactly
- Rounds only on final conversion to posit

Essential for:
- Dot products
- Matrix multiplication
- Any sum-of-products computation

### Explicit Field Design

Use explicit fields rather than arrays to ensure deterministic layout and avoid heap allocation:

```fsharp
[<Struct>]
type Quire32 =
    val Q0: uint64
    val Q1: uint64
    val Q2: uint64
    val Q3: uint64
    val Q4: uint64
    val Q5: uint64
    val Q6: uint64
    val Q7: uint64  // 8 x 64-bit = 512 bits

module Quire32 =
    let zero : Quire32 =
        { Q0 = 0UL; Q1 = 0UL; Q2 = 0UL; Q3 = 0UL
          Q4 = 0UL; Q5 = 0UL; Q6 = 0UL; Q7 = 0UL }

    let inline fma (q: Quire32) (a: Posit32) (b: Posit32) : Quire32 = ...
    let inline toPosit (q: Quire32) : Posit32 = ...
```

This pattern:
- Maps to `!llvm.struct<(i64, i64, i64, i64, i64, i64, i64, i64)>` in MLIR
- Fits in one 64-byte cache line
- Avoids array reference type overhead

## fsnative Lowering Strategy

### Path 1: Pure F# (Recommended Initial Approach)

Posit operations expressed as bit manipulation on uint32/uint64:
```
Posit32.add → PSG → Alex/Zipper → MLIR (i32 ops) → LLVM → native
```

No special compiler support needed in principle, but this understates the complexity.

**Complexity Warning**: Posit arithmetic is NOT trivial. A single addition requires:
- Decode both operands (variable-length regime extraction)
- Align fractions based on combined scale
- Perform operation with sign handling  
- Normalize (find leading bit, recompute regime)
- Encode result

This is dozens of integer operations with conditionals, fundamentally more complex than IEEE 754's fixed-field layout. Performance may require native bindings to Universal Numbers C++ library.

### Path 2: Native Binding (Performance Path)

Bind to Stillwater's Universal Numbers C++ library:
```fsharp
module Platform.Bindings.Posit =
    let add32 (a: uint32) (b: uint32) : uint32 = Unchecked.defaultof<uint32>
    let mul32 (a: uint32) (b: uint32) : uint32 = Unchecked.defaultof<uint32>
```

Alex provides platform-specific implementation.

## MLIR Representation

Posits lower to integer operations:
- Storage: `i8`, `i16`, `i32`, `i64` depending on posit size
- Arithmetic: Bit manipulation sequences
- Quire: `!llvm.struct<(i64, i64, i64, i64, i64, i64, i64, i64)>` (explicit 8-field struct)

No special MLIR dialect needed initially - standard integer ops suffice.

## Use Cases in Fidelity

From blog articles, posits are valuable for:
- Neural network inference (weight distributions cluster near small values)
- Neuromorphic membrane dynamics
- Quantum amplitude representation
- Scientific computing with exact accumulation
- Any domain where IEEE 754's uniform precision wastes bits

## Implementation Priority

1. **Full posit family** (Posit8, Posit16, Posit32, Posit64) - not just Posit32
2. **Corresponding quires** (Quire8: 32-bit, Quire16: 128-bit, Quire32: 512-bit, Quire64: 2048-bit)
3. **Basic arithmetic** for each type (+, -, *, /)
4. **Mixed-precision utilities** (widen8to16, narrow32to16, etc.)
5. **SRTP integration** for generic algorithms across the family
6. **Conversions** (float ↔ posit, cross-type)
7. **Phantom-typed parameterization** for exotic (nbits, es) configurations

## Mixed-Precision Workflows

Real numerical computing uses multiple precisions:
- Store weights in Posit8 (memory efficient)
- Compute activations in Posit16 (adequate precision)
- Accumulate in quire (exact)
- Convert back to storage type

```fsharp
module MixedPrecision =
    let inline widen8to16 (p: Posit8) : Posit16 = ...
    let inline narrow16to8 (p: Posit16) : Posit8 = ...

    let forwardLayer (weights: Posit8 array) (inputs: Posit16 array) : Posit16 =
        let mutable q = Quire16.zero
        for i = 0 to weights.Length - 1 do
            q <- Quire16.fma q (widen8to16 weights.[i]) inputs.[i]
        Quire16.toPosit q
```

F#'s type system makes precision transitions explicit and safe.

## Hardware Acceleration Landscape

### FPGA-Based Acceleration
- **PACoGen**: Open-source hardware posit core generator (any N, ES), Virtex-7 demonstrated
- **PERCIVAL**: Complete posit RISC-V core with 512-bit quire on CVA6
- **CLARINET**: Quire-enabled RISC-V framework
- AMD/Xilinx FPGAs viable as posit coprocessors in heterogeneous systems

### Emerging Architectures
- **NextSilicon Maverick-2**: Dataflow architecture, John Gustafson advising
- **Xposit**: RISC-V extension with LLVM support for posit instructions
- Research shows posit64 achieves up to 4 orders of magnitude lower MSE than doubles

### Fidelity Integration
Alex binding mechanism can target these hardware paths as they mature, emitting appropriate instructions for posit-capable cores.

## References

- Gustafson, "Beating Floating Point at its Own Game" (2017)
- Gustafson, "Every Bit Counts: Posit Computing" (December 2024)
- Stillwater Universal Numbers C++ library
- Posit Standard 2022
- PACoGen: https://github.com/manish-kj/PACoGen
- PERCIVAL: CVA6-based posit RISC-V core
