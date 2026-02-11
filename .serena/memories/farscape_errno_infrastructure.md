# Farscape Errno Infrastructure (Feb 2026)

## Core Design
Layer 2 wrappers return `Result<T, CError>` where CError is a `[<Struct>]` record carrying both the
errno code (int32) and human-readable description (string pointer to rodata). Zero heap allocation.

## Components
- **CodeAST.fs**: `Generic2` (two-param generics), `RecordConstruction` (struct literals), `MatchExpr` (match expressions), `RecordType` now has `attributes: string list` for `[<Struct>]`
- **ErrnoModuleGenerator.fs**: Generates CError struct + Errno module with `[<Literal>]` constants + `describe` function from parsed macros enriched with raw header comments
- **WrapperCodeGenerator.fs**: `generate` takes `errnoModuleName: string option`; when `Some`, adds `captureError` helper and uses `Generic2("Result", ..., Named "CError")` in error-returning patterns
- **MoyaTypes.fs**: `ErrorConvention` DU (Errno/ReturnCode/NoErrorConvention), `ErrorConventionSpec` with default + per-function overrides, added to `MoyaProject`
- **MoyaSerializer.fs**: TOML `[error_conventions]` section serialize/deserialize
- **BindingGenerator.fs**: Reads `ErrorConventions` from project, derives errno module name, passes to wrapper generator
- **CppParser.fs**: `MacroDecl.Documentation`, raw header comment extraction via `clang -H` include tree discovery + file-level comment parsing

## Data Flow
```
errno-base.h comment → XParsec raw header parse → MacroDecl.Documentation →
  → ErrnoModuleGenerator: [<Literal>] + describe match arms →
    → NTU: jump table over rodata strings → runtime CError.Description
```

## Key APIs
- `ErrnoModuleGenerator.generate macros namespace library` → `string option` (rendered F# source)
- `ErrnoModuleGenerator.filterErrnoMacros macros` → `ErrnoConstant list` (sorted by value)
- `WrapperCodeGenerator.generate decls ns lib bindings errnoModule` → `string`
- `CppParser.parseHeaderFull options` → enriches macros with documentation from raw headers

## Moya TOML
```toml
[error_conventions]
default = "errno"
[error_conventions.overrides]
pthread_create = "return_code"
```

## captureError helper (generated in wrapper module when errno enabled)
```fsharp
let captureError () : CError =
    let code = NativePtr.read (Bindings.__errno_location ())
    { Code = code; Description = Errno.describe code }
```
