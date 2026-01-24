# FsNative Build Status (January 2026)

## Status: PRODUCTION MATURE

**FNCS has reached production maturity.** The compiler pipeline is complete and validated:

| Milestone | Status |
|-----------|--------|
| Firefly samples 01-09 | ✅ All passing |
| Baker decomposition | ✅ Complete for List, Map, Set, Seq, Option |
| Coeffect system | ✅ NodeSSAAllocation, ClosureLayout, DULayout |
| DU infrastructure | ✅ Homogeneous (inline) + Heterogeneous (arena) |
| Closure infrastructure | ✅ Flat closures with capture analysis |

## Historical Context

Phase 1.1 pruning was completed earlier. Build infrastructure issues were resolved.

## Build Infrastructure (Resolved)

Historical issues with Arcade SDK and multi-TFM builds were resolved. The working configuration:

```bash
# Bootstrap tools
dotnet build buildtools/fslex/fslex.fsproj -p:RuntimeIdentifier=linux-x64 --configuration Release
dotnet build buildtools/fsyacc/fsyacc.fsproj -p:RuntimeIdentifier=linux-x64 --configuration Release

# Build FNCS
dotnet build src/Compiler/FSharpNative.Compiler.Service.fsproj --configuration Proto
```

## What's Now Available for Farscape

With FNCS mature, Farscape can now leverage:

| Feature | Status | Farscape Use Case |
|---------|--------|-------------------|
| Quotation decomposition | ✅ Ready | Hardware descriptor pattern matching |
| Active patterns | ✅ Ready | PSG recognition for memory operations |
| Record types | ✅ Ready | PeripheralDescriptor, FieldDescriptor |
| DU types | ✅ Ready | AccessKind, MemoryRegionKind |
| Collection operations | ✅ Ready | Map.ofList, List.map for descriptor processing |

## Integration Path

1. **BAREWire** provides type definitions (PeripheralDescriptor, etc.)
2. **Farscape** generates quotations using those types
3. **FNCS** pattern-matches on quotations in nanopasses

All three layers are now ready for integration.
