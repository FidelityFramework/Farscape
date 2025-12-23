# XParsec-Based Header Parsing

## Architecture

Farscape uses XParsec (the same parser combinator library that powers Firefly's PSG traversal) for parsing C/C++ headers. This provides:

- Type-safe parsing with compile-time guarantees
- Composable parsers for complex constructs
- Excellent error messages with precise locations
- Pure F# implementation

## Parser Structure

### Lexical Layer

```fsharp
// Basic tokens
let identifier = regex @"[a-zA-Z_][a-zA-Z0-9_]*"
let number = regex @"[0-9]+" |>> int
let hexNumber = regex @"0x[0-9a-fA-F]+" |>> parseHex

// Keywords
let kwStruct = keyword "struct"
let kwTypedef = keyword "typedef"
let kwVolatile = keyword "volatile"
let kwConst = keyword "const"

// CMSIS qualifiers
let cmsis_I = keyword "__I" >>% CMSIS_I
let cmsis_O = keyword "__O" >>% CMSIS_O
let cmsis_IO = keyword "__IO" >>% CMSIS_IO
```

### Type Parsing

```fsharp
// C type specifiers
let typeSpecifier =
    choice [
        keyword "uint32_t" >>% CType.UInt32
        keyword "uint16_t" >>% CType.UInt16
        keyword "uint8_t" >>% CType.UInt8
        keyword "int32_t" >>% CType.Int32
        keyword "void" >>% CType.Void
        identifier |>> CType.Named
    ]

// Pointer types with qualifiers
let pointerType =
    pipe3 (many qualifier) typeSpecifier (many1 (symbol "*"))
        (fun quals baseType ptrs -> CType.Pointer(baseType, quals, List.length ptrs))
```

### Structure Parsing

```fsharp
// Field with CMSIS qualifiers
let fieldDecl =
    parse {
        let! quals = many (cmsis_I <|> cmsis_O <|> cmsis_IO <|> kwVolatile <|> kwConst)
        let! fieldType = typeSpecifier
        let! name = identifier
        let! arraySize = optional (between (symbol "[") (symbol "]") number)
        do! symbol ";"
        return { Qualifiers = quals; Type = fieldType; Name = name; ArraySize = arraySize }
    }

// Struct definition
let structDef =
    parse {
        do! kwStruct
        let! name = optional identifier
        do! symbol "{"
        let! fields = many fieldDecl
        do! symbol "}"
        return { Name = name; Fields = fields }
    }

// Typedef struct
let typedefStruct =
    parse {
        do! kwTypedef
        let! struct' = structDef
        let! aliasName = identifier
        do! symbol ";"
        return { Struct = struct'; Alias = aliasName }
    }
```

### Macro Parsing

```fsharp
// Base address macros: #define GPIOA_BASE 0x48000000UL
let baseAddressMacro =
    parse {
        do! symbol "#define"
        let! name = identifier
        let! addr = hexNumber
        do! optional (keyword "UL" <|> keyword "U")
        return MacroDecl.BaseAddress(name, addr)
    }

// Bit position macros: #define USART_CR1_UE_Pos 0U
let bitPosMacro =
    parse {
        do! symbol "#define"
        let! name = regex @"[A-Z0-9_]+_Pos"
        let! pos = number
        do! optional (keyword "U")
        return MacroDecl.BitPosition(name, pos)
    }

// Bit mask macros: #define USART_CR1_UE_Msk (0x1UL << USART_CR1_UE_Pos)
let bitMaskMacro =
    parse {
        do! symbol "#define"
        let! name = regex @"[A-Z0-9_]+_Msk"
        do! symbol "("
        let! value = hexNumber
        do! keyword "UL" <|> keyword "U"
        do! symbol "<<"
        let! posRef = identifier
        do! symbol ")"
        return MacroDecl.BitMask(name, value, posRef)
    }
```

### Function Declaration Parsing

```fsharp
// Function parameter
let parameter =
    parse {
        let! paramType = pointerType <|> (typeSpecifier |>> fun t -> CType.Value t)
        let! name = identifier
        return { Type = paramType; Name = name }
    }

// Function declaration
let functionDecl =
    parse {
        let! retType = typeSpecifier
        let! name = identifier
        do! symbol "("
        let! params = sepBy parameter (symbol ",")
        do! symbol ")"
        do! symbol ";"
        return { ReturnType = retType; Name = name; Parameters = params }
    }
```

## Output Types

Parsing produces intermediate types that map to quotation generation:

```fsharp
type ParsedStruct = {
    Name: string option
    Alias: string option
    Fields: ParsedField list
}

type ParsedField = {
    Qualifiers: Qualifier list  // Including CMSIS __I, __O, __IO
    Type: CType
    Name: string
    ArraySize: int option
}

type ParsedMacro =
    | BaseAddress of name: string * address: uint64
    | BitPosition of name: string * position: int
    | BitMask of name: string * value: uint64 * positionRef: string
    | PeripheralInstance of name: string * typeName: string * baseRef: string

type ParsedFunction = {
    ReturnType: CType
    Name: string
    Parameters: ParsedParameter list
}
```

## CMSIS Qualifier Extraction

The key transformation - CMSIS qualifiers become `AccessKind`:

```fsharp
let extractAccessKind (quals: Qualifier list) : AccessKind =
    if List.contains CMSIS_I quals then ReadOnly
    elif List.contains CMSIS_O quals then WriteOnly
    elif List.contains CMSIS_IO quals then ReadWrite
    elif List.contains Const quals && List.contains Volatile quals then ReadOnly
    elif List.contains Volatile quals then ReadWrite
    else ReadWrite  // Default for non-volatile
```

## Canonical Reference

See `~/repos/Firefly/docs/Quotation_Based_Memory_Architecture.md` for how parsed output feeds into quotation generation.
