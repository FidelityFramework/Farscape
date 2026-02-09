# Thuja TUI Integration Strategy

## What is Thuja?

Minimalist F# TUI library at `FidelityFramework/Thuja` (soft fork). Elm-inspired MVU architecture on Tutu (crossterm) backend.

## Core Architecture

- **MVU Pattern**: `model → view → update` cycle with `Cmd<'msg>` for side effects
- **Backend Abstraction**: `IBackend` interface (terminal size, execute commands, input events)
- **Differential Rendering**: Only re-renders changed elements via `ViewTree.difference`
- **Async Event Loop**: MailboxProcessor-based, supports subscriptions

## Key Types

```fsharp
type Cmd<'msg> = Async<'msg> list
type Program<'model, 'msg> = {
  Backend: unit -> IBackend; Model: 'model
  View: 'model -> Region -> ViewTree
  Update: 'model -> 'msg -> 'model * Cmd<'msg>
  KeyBindings: KeyInput * KeyModifiers -> Cmd<'msg>
}
```

## Available Widgets

| Widget | Module | Purpose |
|--------|--------|---------|
| `text` | Text.fs | Styled text with wrap/clip/ellipsis, alignment |
| `list` | List.fs | Selectable item list with highlighting, pagination |
| `panel` | Panel.fs | Border container (Normal/Rounded/Thick/Double) |
| `table` | Table.fs | Headers + data rows with column layout |
| `columns` | Layout.fs | Fraction/Absolute column layout |
| `rows` | Layout.fs | Fraction/Absolute row layout |
| `grid` | Layout.fs | 2D grid layout |
| `hr`/`vr` | Rule.fs | Horizontal/vertical separators |

## Program API

```fsharp
Program.make model view update
|> Program.withKeyBindings handler
|> Program.withTutuBackend
|> Program.run
```

## Moya Interactive TUI Design (Stretch Goal)

### Model
```fsharp
type MoyaModel = {
    AnalysisResult: AnalysisResult
    Groups: NamespaceSpec list
    SelectedGroup: int
    SelectedFunction: int
    UngroupedFunctions: string list
    Mode: ViewMode  // GroupList | FunctionList | Preview | Confirm
}
```

### View Layout
```
┌─ Groups ─────────────┐┌─ Functions ───────────┐
│ > Memory (str, mem)   ││ strlen               │
│   I/O (read, write)   ││ strcmp               │
│   Alloc (malloc...)   ││ strcpy               │
│                       ││ memcpy               │
│                       ││ memset               │
└───────────────────────┘└───────────────────────┘
┌─ Actions ─────────────────────────────────────┐
│ [a]dd group  [m]ove fn  [r]ename  [w]rite     │
└───────────────────────────────────────────────┘
```

### Interaction Model
- Navigate groups with ↑/↓, switch panes with Tab
- `a` = add new namespace group, `d` = delete group
- `m` = move selected function to different group
- `r` = rename group, `p` = edit prefixes
- `w` = write .moya.toml and exit
- `q` = quit without saving

### Key Insight: Backend Abstraction for Fidelity Native
The `IBackend` interface makes Thuja backend-swappable:
- **Current**: Tutu (crossterm, .NET runtime)
- **Future**: Native terminal backend compiled with Firefly
- The MVU pattern + typed views are already F#-first
- No BCL dependencies in the core types (just `string`, `int`, `byte`)
- Could target bare ANSI escape codes for freestanding environments

## Broader Fidelity Framework TUI Vision

1. **Phase 1 (Now)**: Use Thuja via NuGet in Farscape CLI for Moya interactive mode
2. **Phase 2**: Build Thuja-based TUIs for other Fidelity tools (Firefly debugger, build status)
3. **Phase 3**: Port Thuja core to Fidelity-native (compile with Firefly, no .NET runtime)
4. **Phase 4**: Thuja as the standard TUI framework for Fidelity framework projects

The architecture is already aligned for this progression because:
- IBackend is the only runtime-dependent piece
- Core types (Color, Style, Region, ViewTree) are value types / DUs
- MVU pattern is pure functional (no mutable state in user code)
- Layout calculations are pure math on Region structs
