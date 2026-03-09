# TypeScript Ingestion — Architecture

> **Part 1 of 3** — See also: [Fearless JavaScript](./13b_Fearless_JavaScript.md) | [Output & Integration](./13c_TypeScript_Output_and_Integration.md)

## Context

Farscape generates Clef bindings from C headers. This document extends Farscape to **ingest TypeScript declarations** and produce two distinct output targets:

1. **JavaScript/JSX Bindings** — strongly-typed Clef interop bindings for calling JavaScript libraries (replacing Glutinum/ts2fable)
2. **Clef Source** — direct translation of TypeScript type structure into Clef code for native compilation (no JavaScript runtime)

This is a separate concern from [OpenAPI Binding Generation](./12_OpenAPI_Binding_Generation.md), which ingests REST API specifications. TypeScript ingestion operates on the type system and module structure of JavaScript/TypeScript libraries directly.

### Why This Exists

Three tools currently occupy this space, each with fundamental limitations:

**ts2fable** (fable-compiler/ts2fable): The original TypeScript-to-F# binding generator. Uses the TypeScript compiler API for parsing but only performs syntax-level analysis (no type checker). Unmaintained.

**Glutinum CLI** (glutinum-org/cli): The successor to ts2fable. Significant improvement — uses the TypeScript type checker, introduces a two-layer AST (GlueAST → FSharpAST), and handles utility types (`Partial<T>`, `Record<K,V>`, `Omit<T,K>`). However:

1. **Runtime dependency**: Compiles via Fable to JavaScript, runs in Node.js, calls the TypeScript compiler API via JS interop. The entire tool is a Fable application that requires Node.js.
2. **Mutable transform context**: `TransformContext` uses `ResizeArray`, mutable dictionaries, and parent-pointer trees. Non-deterministic output ordering is possible.
3. **StringBuilder printer**: `Printer` class with `Indent`/`Unindent` mutation and direct string assembly. No typed AST between transform and output.
4. **Fable-only output**: Generates Fable-specific attributes (`[<Emit>]`, `[<Import>]`, `[<StringEnum>]`, `[<Erase>]`). No path to native compilation.

**Fable** (fable-compiler/Fable): The F#-to-JavaScript compiler. Three-stage AST pipeline (F# AST → Fable AST → Babel AST → JS). Depends on a custom fork of FSharp.Compiler.Service. Multi-target (JS, Python, Rust, Dart, PHP, Beam) but architecturally coupled to the .NET F# compiler.

### What This Design Replaces

| Current Tool | Role | Farscape Replacement |
|-------------|------|---------------------|
| Glutinum CLI | `.d.ts` → F# Fable bindings | TypeScript ingestion → ClefDecl → Clef source |
| ts2fable | `.d.ts` → F# Fable bindings (legacy) | Same |
| Fable (partial) | F# → JS transpilation with type-erased interop | Clef → JS compilation via Composer (js_of_ocaml model) |
| Glutinum bindings library | Hand-crafted F# bindings for npm packages | Generated Clef bindings, Pilot-managed |

### Self-Hosting Trajectory

This design follows the same self-hosting trajectory as Farscape Core and the OpenAPI extension:

1. **Phase 1**: Clef implementation using Farscape's four patterns, TypeScript AST parsed via two-pass external tooling
2. **Phase 2**: TypeScript parsing moves to XParsec (`.d.ts` subset — declaration files are syntactically simpler than full TypeScript), tsc metadata pass becomes optional
3. **Phase 3**: Entire pipeline self-hosted in Clef, running inside Composer

At Phase 3, Clef programs can directly import TypeScript declaration files and Composer generates native bindings — no Node.js, no Fable, no FSharp.Compiler.Service.

## Design Principles

### 1. Same Four Patterns as Farscape Core

| Pattern | C Header Binding | TypeScript Binding |
|---------|------------------|--------------------|
| **XParsec Parsers** | C type strings, macro values | TypeScript type expressions, generic constraints, mapped types |
| **Active Patterns** | Type classification, keyword quoting | TS type classification, identifier sanitization, JS interop attribute inference |
| **Catamorphism** | `Declaration` DU fold | `TsDeclaration` DU fold |
| **Typed Code AST** | `ClefDecl` → Clef source | `ClefDecl` → Clef source (same AST, renderer-configured per target) |

### 2. Two Output Targets, One Pipeline

The pipeline diverges at the **code generation** stage, not the parsing or classification stage:

```
TypeScript .d.ts
  │
  ▼
[TsDeclaration AST]     ── shared intermediate representation
  │
  ├──▶ [JS Binding Generator]  → ClefDecl → Clef with JS interop attributes
  │                                         ([<JsImport>], [<JsEmit>], [<JsErase>])
  │
  └──▶ [Native Generator]      → ClefDecl → Clef source
                                            (native types, Composer compilation)
```

Both generators consume the same `TsDeclaration` AST and produce `ClefDecl` trees. The only difference is which ClefDecl nodes they emit and how the CodeRenderer is configured.

### 3. Two-Pass Architecture (Clang-Parallel)

Farscape Core uses a two-pass clang strategy for C headers:
- **Pass 1**: `clang -Xclang -ast-dump=json` → structural AST (functions, structs, enums)
- **Pass 2**: `clang -dM -E` → semantic metadata (macros, defines)

TypeScript ingestion follows the same architecture. Raw `.d.ts` syntax gives you the **shape** of declarations, but the TypeScript type checker gives you **semantic resolution** that syntax alone cannot provide.

#### What syntax parsing gives you

XParsec can parse `.d.ts` structure perfectly:

```typescript
interface Request<P = ParamsDictionary> extends core.Request<P> { }
type Partial<T> = { [P in keyof T]?: T[P] };
```

#### What requires the type checker

| Question | Why syntax isn't enough |
|----------|----------------------|
| What does `Partial<Express.Request>` resolve to? | Need to instantiate the generic, walk the mapped type, resolve `keyof` |
| Does `ReadableStream` come from `lib.dom.d.ts` or `@types/node`? | Module resolution graph, `/// <reference>` chains |
| Are `Foo` in file A and `Foo` in file B the same symbol? | Declaration merging across files, fully qualified symbol identity |
| What's the effective type of `typeof import("express")`? | Need the module's export shape as a type |
| Which overload of `createElement` matches `createElement("div")`? | Overload resolution with literal type narrowing |
| What does `T extends string ? A : B` collapse to for a concrete `T`? | Conditional type evaluation |

This is exactly what Glutinum does better than ts2fable — Glutinum calls `checker.getTypeAtLocation()` and gets resolved types. ts2fable only has syntax, which is why it silently falls through to `obj` on anything complex.

#### The two passes

```
Pass 1: .d.ts text  → XParsec           → TsDeclaration AST (syntactic structure)
Pass 2: .d.ts files → tsc metadata dump → symbol resolution, type instantiation, declaration origins
```

**Pass 1** (XParsec — pure Clef, self-hostable):
- Parses `.d.ts` syntax into `TsDeclaration` AST
- Handles all structural information: interfaces, classes, enums, type aliases, functions, modules
- Produces a complete, usable AST on its own

**Pass 2** (tsc metadata extractor — small Node.js script, disposable):
- Runs the TypeScript type checker over the same `.d.ts` files
- Emits a JSON metadata file with resolved semantic information
- Analogous to `clang -dM -E` for macro extraction

```javascript
// ts-metadata-dump.js — the "clang pass 2" equivalent
const ts = require("typescript");
const program = ts.createProgram(entryFiles, compilerOptions);
const checker = program.getTypeChecker();

for (const sourceFile of program.getSourceFiles()) {
    ts.forEachChild(sourceFile, node => {
        if (ts.isInterfaceDeclaration(node)) {
            const symbol = checker.getSymbolAtLocation(node.name);
            emit({
                kind: "interface",
                name: node.name.text,
                fullyQualifiedName: checker.getFullyQualifiedName(symbol),
                sourceFile: sourceFile.fileName,
                extends: getResolvedBaseTypes(checker, node),
                declarationFiles: symbol.declarations.map(d => d.getSourceFile().fileName),
            });
        }
        // ... similar for type aliases, classes, etc.
    });
}
```

#### What each pass contributes

| | Pass 1: XParsec (syntactic) | Pass 2: tsc metadata (semantic) |
|---|---|---|
| **Tool** | Pure Clef | Node.js script (small, disposable) |
| **Input** | `.d.ts` file text | Same `.d.ts` files |
| **Output** | `TsDeclaration` AST | JSON: symbol resolution, type instantiation |
| **Self-hostable** | Yes (XParsec → Clef) | No — but replaceable at Phase 3 |
| **Deterministic** | Yes | Yes (tsc is deterministic given same inputs) |

#### Enrichment merge

```fsharp
/// Enrich syntactic declarations with type-checker metadata
let enrichDeclarations
    (syntactic: TsDeclaration list)
    (metadata: TsMetadata)
    : TsDeclaration list =
    // Resolve fully qualified names
    // Merge augmented interfaces
    // Instantiate utility types (Partial, Omit, etc.)
    // Attach declaration origin information (standard library vs. user code)
    // Flag standard library types (lib.dom.d.ts, lib.es2015.d.ts, etc.)
```

#### Self-hosting trajectory

The key insight is that Pass 2 is **metadata enrichment**, not structural parsing. The `TsDeclaration` AST from Pass 1 is complete and usable on its own — Pass 2 makes it better. This means:

- **Phase 1**: Both passes. XParsec for syntax, tsc for semantics.
- **Phase 2**: XParsec only, with a growing resolution engine that handles the common cases (module lookup, simple generic instantiation, declaration merging by name). The tsc script becomes optional.
- **Phase 3**: Clef self-hosted. The resolution engine handles everything tsc gave us, or accepts graceful degradation for exotic types (conditional types, complex mapped types).

### 4. Deterministic Output

Same guarantee as Farscape Core: byte-identical generation across runs. No `ResizeArray`-dependent ordering, no mutable `TransformContext`.

## Architecture

### Pipeline

```
TypeScript .d.ts File(s)
  │
  ▼
[Declaration Reader]               ── Parse .d.ts into TsDeclaration AST
  │                                   Phase 1: tsc JSON output + XParsec
  │                                   Phase 2: XParsec direct parsing
  ▼
[Type Resolver]                    ── Resolve type references, flatten intersections
  │                                   Merge interface augmentations
  │                                   Detect utility types (Partial, Omit, Record)
  │                                   Build type dependency graph
  ▼
[TsDeclaration AST]                ── Typed intermediate representation
  │                                   Interface | Class | Enum | TypeAlias |
  │                                   Function | Variable | Module | Namespace
  ▼
[Active Patterns]                  ── Classify: PrimitiveType | ObjectType | UnionType
  │                                   Detect: StringEnum | NumericEnum | TaggedUnion
  │                                   Identify: CallableObject | IndexableObject
  │                                   Sanitize: NeedsQuoting | ValidIdent
  ▼
[Catamorphism]                     ── Single fold over TsDeclaration DU
  │                                   Produces: type definitions, member bindings, module structure
  ▼
[Target-Specific Generator]        ── JS Binding: TsDeclaration → ClefDecl (with JS interop attributes)
  │                                   Clef Native: TsDeclaration → ClefDecl (native types)
  ▼
[ClefDecl AST]                       ── Same typed AST as Farscape Core
  │                                   (reused: NTUType, ClefExpr, ClefDecl, ClefModule)
  ▼
[CodeRenderer]                     ── ClefDecl → Clef source string
                                      (same StringBuilder-only renderer, configured per target)
```

### Core Types

```fsharp
/// Intermediate representation for TypeScript declarations.
/// Follows TypeScript semantics but uses Clef discriminated unions
/// for structured decomposition (analogous to Glutinum's GlueAST,
/// but immutable and fold-compatible).
type TsDeclaration =
    | Interface of TsInterface
    | Class of TsClass
    | Enum of TsEnum
    | TypeAlias of TsTypeAlias
    | Function of TsFunction
    | Variable of TsVariable
    | Module of TsModule
    | ExportDefault of TsDeclaration

and TsInterface = {
    Name: string
    FullName: string
    Members: TsMember list
    TypeParameters: TsTypeParam list
    Extends: TsTypeRef list
    Docs: TsDoc list
}

and TsClass = {
    Name: string
    Constructors: TsConstructor list
    Members: TsMember list
    TypeParameters: TsTypeParam list
    Extends: TsTypeRef option
    Implements: TsTypeRef list
    Docs: TsDoc list
}

and TsEnum = {
    Name: string
    Members: TsEnumMember list
}

and TsEnumMember = {
    Name: string
    Value: TsLiteral
}

and TsTypeAlias = {
    Name: string
    Type: TsType
    TypeParameters: TsTypeParam list
    Docs: TsDoc list
}

and TsFunction = {
    Name: string
    Parameters: TsParam list
    ReturnType: TsType
    TypeParameters: TsTypeParam list
    IsDeclared: bool
    Docs: TsDoc list
}

and TsVariable = {
    Name: string
    Type: TsType
    Docs: TsDoc list
}

and TsModule = {
    Name: string
    IsNamespace: bool
    Declarations: TsDeclaration list
}

and TsConstructor = {
    Parameters: TsParam list
    Docs: TsDoc list
}
```

### Type System

```fsharp
/// TypeScript type representation.
/// Covers the full type surface of .d.ts files.
type TsType =
    | Primitive of TsPrimitive
    | Literal of TsLiteral
    | TypeRef of TsTypeRef
    | Array of TsType
    | ReadonlyArray of TsType
    | Tuple of TsType list
    | NamedTuple of (string * TsType) list
    | Union of TsType list
    | Intersection of TsMember list
    | Function of TsFunctionType
    | TypeLiteral of TsMember list
    | Mapped of TsMappedType
    | Conditional of TsConditionalType
    | IndexedAccess of objectType: TsType * indexType: TsType
    | KeyOf of TsType
    | TypeParameter of string
    | UtilityType of TsUtilityType
    | TemplateLiteral
    | This of TsThisType
    | Unknown

and TsPrimitive =
    | String | Number | Boolean | Void | Null | Undefined
    | Any | Never | Object | Symbol | BigInt

and TsLiteral =
    | StringLit of string
    | NumberLit of float
    | BoolLit of bool
    | NullLit

and TsTypeRef = {
    Name: string
    FullName: string
    TypeArguments: TsType list
    IsStandardLibrary: bool
}

and TsFunctionType = {
    Parameters: TsParam list
    ReturnType: TsType
    TypeParameters: TsTypeParam list
}

and TsMappedType = {
    TypeParameter: TsTypeParam
    Type: TsType option
}

and TsConditionalType = {
    CheckType: TsType
    ExtendsType: TsType
    TrueType: TsType
    FalseType: TsType
}

/// TypeScript utility types that require semantic understanding.
and TsUtilityType =
    | Partial of TsInterface
    | Required of TsInterface
    | Readonly of TsType
    | Record of keyType: TsType * valueType: TsType
    | Pick of TsInterface * keys: string list
    | Omit of TsMember list
    | Exclude of TsType * TsType
    | Extract of TsType * TsType
    | ReturnType of TsType
    | Parameters of TsType
    | ThisParameterType of TsType
    | InstanceType of TsType

and TsParam = {
    Name: string
    Type: TsType
    IsOptional: bool
    IsSpread: bool
}

and TsTypeParam = {
    Name: string
    Constraint: TsType option
    Default: TsType option
}

and TsDoc =
    | Summary of string list
    | ParamDoc of name: string * description: string
    | Returns of string
    | Deprecated of string option
    | Example of string
    | TypeParamDoc of name: string * description: string
```

### Member System

```fsharp
/// TypeScript member representation.
/// Covers interface members, class members, and type literal members.
type TsMember =
    | Method of TsMethod
    | Property of TsProperty
    | GetAccessor of TsGetAccessor
    | SetAccessor of TsSetAccessor
    | CallSignature of TsCallSignature
    | ConstructSignature of TsConstructSignature
    | IndexSignature of TsIndexSignature

and TsMethod = {
    Name: string
    Parameters: TsParam list
    ReturnType: TsType
    TypeParameters: TsTypeParam list
    IsOptional: bool
    IsStatic: bool
    Docs: TsDoc list
}

and TsProperty = {
    Name: string
    Type: TsType
    IsOptional: bool
    IsReadonly: bool
    IsStatic: bool
    Docs: TsDoc list
}

and TsGetAccessor = {
    Name: string
    ReturnType: TsType
    IsStatic: bool
}

and TsSetAccessor = {
    Name: string
    ArgumentType: TsType
    IsStatic: bool
}

and TsCallSignature = {
    Parameters: TsParam list
    ReturnType: TsType
}

and TsConstructSignature = {
    Parameters: TsParam list
    ReturnType: TsType
}

and TsIndexSignature = {
    Parameters: TsParam list
    ReturnType: TsType
    IsReadonly: bool
}
```

### Active Patterns

```fsharp
// =========================================================================
// TypeScript Type Classification
// =========================================================================

/// Classify a TypeScript type into Clef binding strategy
let (|StringEnum|NumericEnum|MixedEnum|) (enum: TsEnum) =
    let allString = enum.Members |> List.forall (fun m ->
        match m.Value with StringLit _ -> true | _ -> false)
    let allNumeric = enum.Members |> List.forall (fun m ->
        match m.Value with NumberLit _ -> true | _ -> false)
    if allString then StringEnum
    elif allNumeric then NumericEnum
    else MixedEnum

/// Detect whether a union of string literals should become a StringEnum
let (|StringLiteralUnion|ObjectUnion|PrimitiveUnion|MixedUnion|) (types: TsType list) =
    let allStringLit = types |> List.forall (fun t ->
        match t with Literal (StringLit _) -> true | _ -> false)
    let allPrimitive = types |> List.forall (fun t ->
        match t with Primitive _ -> true | _ -> false)
    let allObject = types |> List.forall (fun t ->
        match t with TypeRef _ | TypeLiteral _ -> true | _ -> false)
    if allStringLit then StringLiteralUnion
    elif allPrimitive then PrimitiveUnion
    elif allObject then ObjectUnion
    else MixedUnion

/// Detect callable objects (interfaces/classes used as functions)
let (|CallableObject|RegularInterface|) (iface: TsInterface) =
    let hasCallSignature = iface.Members |> List.exists (fun m ->
        match m with CallSignature _ -> true | _ -> false)
    if hasCallSignature then CallableObject
    else RegularInterface

/// Detect indexable objects (dictionary-like interfaces)
let (|IndexableObject|_|) (iface: TsInterface) =
    iface.Members |> List.tryFind (fun m ->
        match m with IndexSignature _ -> true | _ -> false)

/// TypeScript identifier sanitization for Clef
let (|TsNeedsQuoting|TsValidIdent|) (name: string) =
    if containsSpecialChars name
        || isFSharpKeyword name
        || name.StartsWith("$")
        || name.Contains("-")
        || System.Char.IsDigit(name.[0]) then
        TsNeedsQuoting name
    else
        TsValidIdent name

// =========================================================================
// JS Interop Attribute Inference
// =========================================================================

/// Determine import strategy for a declaration
let (|DefaultExport|NamedExport|GlobalValue|) (decl: TsDeclaration) =
    match decl with
    | ExportDefault _ -> DefaultExport
    | Variable v when v.IsDeclared -> GlobalValue
    | _ -> NamedExport

/// Detect TypeScript tagged union pattern (discriminated union)
let (|TaggedUnion|_|) (types: TsType list) =
    // Look for a common discriminant property across all object types
    // e.g., { kind: "circle"; radius: number } | { kind: "square"; side: number }
    let objectMembers = types |> List.choose (fun t ->
        match t with
        | TypeRef ref -> None  // Would need type resolution
        | TypeLiteral members -> Some members
        | _ -> None)
    if objectMembers.Length = types.Length && objectMembers.Length >= 2 then
        findCommonDiscriminant objectMembers
    else None
```

### Catamorphism

```fsharp
/// Fold algebra for TypeScript declarations.
/// One function per TsDeclaration variant.
type TsDeclarationAlgebra<'R> = {
    OnInterface:    TsInterface -> 'R
    OnClass:        TsClass -> 'R
    OnEnum:         TsEnum -> 'R
    OnTypeAlias:    TsTypeAlias -> 'R
    OnFunction:     TsFunction -> 'R
    OnVariable:     TsVariable -> 'R
    OnModule:       TsModule -> 'R
    OnExportDefault: TsDeclaration -> 'R
}

/// Single canonical fold over TsDeclaration — the ONLY traversal
let cataTsDeclarations (algebra: TsDeclarationAlgebra<'R>) (decls: TsDeclaration list) : 'R list =
    decls |> List.map (fun decl ->
        match decl with
        | Interface i    -> algebra.OnInterface i
        | Class c        -> algebra.OnClass c
        | Enum e         -> algebra.OnEnum e
        | TypeAlias t    -> algebra.OnTypeAlias t
        | Function f     -> algebra.OnFunction f
        | Variable v     -> algebra.OnVariable v
        | Module m       -> algebra.OnModule m
        | ExportDefault d -> algebra.OnExportDefault d)
```

### Type Mapping: TypeScript → Clef

The type mapper is target-aware. The same `TsType` maps differently depending on output target:

```fsharp
/// TypeScript → Clef (JS binding) type mapping
let rec mapTypeForJsBinding (tsType: TsType) : NTUType =
    match tsType with
    | Primitive String -> Named "string"
    | Primitive Number -> Named "float"
    | Primitive Boolean -> Named "bool"
    | Primitive Void -> Unit
    | Primitive Null -> Generic ("option", Named "obj")
    | Primitive Undefined -> Generic ("option", Named "obj")
    | Primitive Any -> Named "obj"
    | Primitive Never -> Named "obj"
    | Primitive Object -> Named "obj"
    | Primitive Symbol -> Named "obj"
    | Array elemType -> Generic ("ResizeArray", mapTypeForJsBinding elemType)
    | ReadonlyArray elemType -> Generic ("ResizeArray", mapTypeForJsBinding elemType)
    | Tuple types -> TupleType (types |> List.map mapTypeForJsBinding)
    | Union types ->
        match types with
        | StringLiteralUnion -> Named "string"  // Will get [<StringEnum>] attribute
        | _ -> erasedUnionType types            // U2<T1, T2>, U3<T1, T2, T3>, etc.
    | Function ft -> delegateType ft
    | TypeRef ref -> resolveTypeRef ref
    | TypeParameter name -> Named $"'{name}"
    | _ -> Named "obj"

/// TypeScript → Clef (native compilation) type mapping
let rec mapTypeForNative (tsType: TsType) : NTUType =
    match tsType with
    | Primitive String -> Named "string"
    | Primitive Number -> Named "float64"
    | Primitive Boolean -> Named "bool"
    | Primitive Void -> Unit
    | Primitive Null -> Generic ("option", Named "obj")
    | Primitive Any -> Named "obj"
    | Primitive Never -> Named "unit"      // Never = bottom type = unit in Clef
    | Array elemType -> Generic ("array", mapTypeForNative elemType)
    | ReadonlyArray elemType -> Generic ("ImmutableArray", mapTypeForNative elemType)
    | Union types -> synthesizeDU types     // Real DU, not erased
    | Function ft -> Named "FunctionRef"    // Clef function reference
    | TypeRef ref -> resolveTypeRef ref
    | TypeParameter name -> Named $"'{name}"
    | _ -> Named "obj"
```

## Declaration Reading Strategy

### Pass 1: XParsec Syntactic Parsing

Parse `.d.ts` files directly — either from npm packages (`node_modules/@types/express/index.d.ts`) or produced by `tsc --declaration --emitDeclarationOnly`.

The `.d.ts` parser uses XParsec combinators:

```fsharp
/// Parse a TypeScript type annotation
let rec pTsType : Parser<TsType> =
    choice [
        pPrimitive                              // string, number, boolean, void, any, never
        pArrayType                              // T[] or Array<T>
        pTupleType                              // [T1, T2, ...]
        pFunctionType                           // (a: T1, b: T2) => T3
        pUnionType                              // T1 | T2 | T3
        pIntersectionType                       // T1 & T2
        pTypeLiteral                            // { foo: string; bar: number }
        pMappedType                             // { [K in keyof T]: V }
        pConditionalType                        // T extends U ? X : Y
        pKeyOfType                              // keyof T
        pIndexedAccessType                      // T[K]
        pTypeReference                          // Foo<T1, T2>
        pStringLiteralType                      // "hello"
        pNumberLiteralType                      // 42
        pBoolLiteralType                        // true | false
        pTemplateStringType                     // `prefix${T}suffix`
        pParenthesized                          // (T)
        pTypeParameter                          // T (bare identifier)
    ]

/// Parse a complete interface declaration
let pInterfaceDecl : Parser<TsDeclaration> =
    parser {
        do! skipOptional pExportKeyword
        do! skipString "interface"
        do! spaces1
        let! name = pIdentifier
        let! typeParams = pOptional pTypeParamList
        let! extends = pOptional pExtendsClause
        do! spaces >>. skipChar '{'
        let! members = many pMember
        do! spaces >>. skipChar '}'
        return Interface {
            Name = name
            FullName = name
            Members = members
            TypeParameters = typeParams |> Option.defaultValue []
            Extends = extends |> Option.defaultValue []
            Docs = []
        }
    }
```

### Pass 2: tsc Semantic Metadata

The optional second pass runs the tsc metadata extractor (see "Two-Pass Architecture" above) to enrich the syntactic AST with resolved type information, fully qualified names, and declaration origins.

### Why `.d.ts` is Tractable for XParsec

Declaration files are a strict subset of TypeScript containing only:

- Type declarations (interface, type, class, enum)
- Function signatures (no bodies)
- Variable declarations with type annotations
- Module/namespace declarations
- Import/export statements
- Comments and documentation

No expressions, no control flow, no executable code. This is a **type-level language**, well within XParsec's capabilities.

### Type Resolution

```fsharp
/// Resolve type references across declaration files.
/// Handles: imports, re-exports, augmented modules, global declarations.
type TypeRegistry = {
    Types: Map<string, TsDeclaration>
    Modules: Map<string, TsModule>
}

/// Merge interface augmentations (TypeScript module augmentation pattern)
let mergeAugmentations (registry: TypeRegistry) : TypeRegistry =
    // TypeScript allows re-opening interfaces:
    //   interface Array<T> { customMethod(): void }
    // Merge all declarations with the same fully-qualified name
    registry.Types
    |> Map.toList
    |> List.groupBy fst
    |> List.map (fun (name, decls) -> name, mergeInterfaces (decls |> List.map snd))
    |> Map.ofList
    |> fun types -> { registry with Types = types }

/// Resolve utility types to concrete types
let resolveUtilityType (registry: TypeRegistry) (ut: TsUtilityType) : TsType =
    match ut with
    | Partial iface ->
        // All properties become optional
        Interface { iface with Members = iface.Members |> List.map makeOptional }
    | Omit members -> TypeLiteral members
    | Record (keyType, valueType) ->
        // Generate index signature interface
        TypeLiteral [ IndexSignature {
            Parameters = [{ Name = "key"; Type = keyType; IsOptional = false; IsSpread = false }]
            ReturnType = valueType
            IsReadonly = false
        }]
    | ReturnType fnType ->
        match fnType with
        | Function ft -> ft.ReturnType
        | _ -> Unknown
    | _ -> Unknown
```

## Related Documents

- [Fearless JavaScript](./13b_Fearless_JavaScript.md): Clef superset constraints on JS expression
- [Output & Integration](./13c_TypeScript_Output_and_Integration.md): Output targets, integration, and comparison
- [Architecture Overview](./01_Architecture_Overview.md): Farscape's four patterns
- [XParsec Architecture](./04_XParsec_Architecture.md): How XParsec is used throughout
- [Pilot Project Setup](./07_Pilot_Project_Setup.md): TOML-driven project system
- [OpenAPI Binding Generation](./12_OpenAPI_Binding_Generation.md): REST API binding generation (companion design)
