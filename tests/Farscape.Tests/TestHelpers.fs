module TestHelpers

open Farscape.Core

// ─── Function declaration helpers ───

let mkFunc name retType parms : CppParser.FunctionDecl =
    { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
      IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }

let mkFuncWithAttrs name retType parms attrs : CppParser.FunctionDecl =
    { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
      IsVirtual = false; IsStatic = false; IsInline = false; Attributes = attrs; MangledName = None }

let mkFuncWithDoc name retType parms doc : CppParser.FunctionDecl =
    { Name = name; ReturnType = retType; Parameters = parms; Documentation = doc
      IsVirtual = false; IsStatic = false; IsInline = false; Attributes = []; MangledName = None }

// ─── Typedef / Enum / Struct helpers ───

let mkTypedef name underlying : CppParser.TypedefInfo =
    { Name = name; UnderlyingType = underlying; Documentation = None }

let mkEnum name values doc : CppParser.EnumDecl =
    { Name = name; Values = values; Documentation = doc; UnderlyingType = None; Representation = CppParser.ClangUnmeasured }

/// A synthetic C enum carrying a clang measurement, for tests that are not about measurement;
/// it cannot evidence the production measurement path (`CppParser.measureEnums`).
let mkMeasuredEnum name values bits isSigned : CppParser.EnumDecl =
    { mkEnum name values None with Representation = CppParser.ClangMeasured { Bits = bits; IsSigned = isSigned } }

let mkEnumSimple name : CppParser.EnumDecl =
    { Name = name; Values = []; Documentation = None; UnderlyingType = None; Representation = CppParser.ClangUnmeasured }

let mkEnumVal name value : CppParser.EnumValue =
    { Name = name; Value = value; Documentation = None }

let mkEnumValWithDoc name value doc : CppParser.EnumValue =
    { Name = name; Value = value; Documentation = Some doc }

let mkStruct name fields doc : CppParser.StructDecl =
    { Name = name; Fields = fields; Documentation = doc; IsUnion = false; Attributes = []; Anonymous = None; Measured = None; Unattributed = None }

let mkStructSimple name : CppParser.StructDecl =
    { Name = name; Fields = []; Documentation = None; IsUnion = false; Attributes = []; Anonymous = None; Measured = None; Unattributed = None }

let mkField name typ : CppParser.FieldDecl =
    { Name = name; Type = typ; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None
      IsBitfield = false; BitWidth = None; Attributes = [] }

let mkBitField name typ width : CppParser.FieldDecl =
    { Name = name; Type = typ; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None
      IsBitfield = true; BitWidth = Some width; Attributes = [] }

// ─── Layout helpers ───

let mkLayout name sizeBits alignBits fieldOffsets : CppParser.StructLayoutInfo =
    { Name = name; SizeBits = sizeBits; DataSizeBits = sizeBits
      AlignmentBits = alignBits; FieldOffsetsBits = fieldOffsets }

// ─── Attribute helpers ───

let mkAttr kind args strArg : CppParser.AttributeData =
    { Kind = kind; Args = args; StringArg = strArg }

// ─── Class declaration helpers ───

let mkClass name fields methods ctors isAbstract hasDtor hasCopyCtor hasMoveCtor dtorMangled : CppParser.ClassDecl =
    { Name = name; Fields = fields; Methods = methods; Constructors = ctors
      Documentation = None; IsAbstract = isAbstract
      HasUserDestructor = hasDtor; HasUserCopyConstructor = hasCopyCtor
      HasUserMoveConstructor = hasMoveCtor; DestructorMangledName = dtorMangled
      BaseClasses = [] }

/// Minimal empty class (opaque/forward-declared)
let mkClassEmpty name : CppParser.ClassDecl =
    mkClass name [] [] [] false false false false None

/// Pimpl class: single shared_ptr field, user destructor
let mkClassPimpl name implType : CppParser.ClassDecl =
    let field = mkField "m_impl" ($"std::shared_ptr<{implType}>")
    mkClass name [field] [] [] false true false false None

/// Pimpl class with unique_ptr
let mkClassPimplUnique name implType : CppParser.ClassDecl =
    let field = mkField "m_impl" ($"std::unique_ptr<{implType}>")
    mkClass name [field] [] [] false true false false None

/// POD class: trivially copyable, no user special members
let mkClassPod name fields : CppParser.ClassDecl =
    mkClass name fields [] [] false false false false None

/// Value class: non-trivial (user destructor), with fields
let mkClassValue name fields : CppParser.ClassDecl =
    mkClass name fields [] [] false true true false None

/// Abstract class (pure virtual interface)
let mkClassAbstract name : CppParser.ClassDecl =
    mkClass name [] [] [] true false false false None

/// Class with methods (as Declaration.Function list)
let mkClassWithMethods name fields methods hasDtor : CppParser.ClassDecl =
    let methodDecls = methods |> List.map CppParser.Declaration.Function
    mkClass name fields methodDecls [] false hasDtor false false None

/// Class with constructors
let mkClassWithCtors name fields ctors hasDtor : CppParser.ClassDecl =
    let ctorDecls = ctors |> List.map CppParser.Declaration.Function
    mkClass name fields [] ctorDecls false hasDtor false false None

/// Pimpl class via base-class inheritance (e.g., detail::pimpl<T>).
/// No fields on the derived class; the smart_ptr lives in the base.
let mkClassPimplBase name baseType : CppParser.ClassDecl =
    { mkClass name [] [] [] false true false false None with
        BaseClasses = [baseType] }

// ─── Declaration wrappers ───

let mkDecl name retType parms attrs =
    CppParser.Declaration.Function (mkFuncWithAttrs name retType parms attrs)

let mkDeclWithDoc name retType parms doc =
    CppParser.Declaration.Function (mkFuncWithDoc name retType parms doc)

// ─── Pilot project fixtures ───

/// The header fixture tree: minimal headers, one directory per concern.
let fixturePath (relative: string) =
    System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "fixtures", relative))

/// Generate a fidelity-mode pilot over `headers` in a fresh directory, apply `check` to the
/// generation result and the output directory, then delete the directory. `options` are extra
/// `[options]` lines; `functions` are the selected namespace functions.
/// `withPilot` with extra `[library]` lines, extra `[[namespace]]` lines and trailing sections.
let rec withPilotSections (headers: string list) (library: string list) (options: string list) (functions: string list)
                          (ns: string list) (trailing: string list)
                          (check: Result<BindingGenerator.GenerationResult, string> -> string -> unit) =
    withPilotGenerating (fun project -> BindingGenerator.generateFromProject project false false Types.LP64)
        headers library options functions ns trailing check

/// `withPilotSections` generating through `generate` (the written pilot's path to a result).
and withPilotGenerating (generate: string -> Result<BindingGenerator.GenerationResult, string>)
                        (headers: string list) (library: string list) (options: string list) (functions: string list)
                        (ns: string list) (trailing: string list)
                        (check: Result<BindingGenerator.GenerationResult, string> -> string -> unit) =
    let dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "artifacts", "pilot-fixture-tests", System.Guid.NewGuid().ToString("N")))
    let output = System.IO.Path.Combine(dir, "output")
    System.IO.Directory.CreateDirectory(output) |> ignore
    try
        let quoted (items: string list) = items |> List.map (fun item -> "\"" + item + "\"") |> String.concat ", "
        let pilot =
            [ "[library]"; "name = \"fixture\""; "headers = [" + quoted headers + "]" ] @ library @
            [ "[output]"; "mode = \"fidelity\""; "directory = \"" + output + "\""
              "[options]" ] @ options @
            [ "[[namespace]]"; "name = \"Test.Fixture\""; "description = \"fixture\""; "library = \"fixture\""
              "functions = [" + quoted functions + "]" ] @ ns @ trailing
        let project = System.IO.Path.Combine(dir, "fixture.pilot.toml")
        System.IO.File.WriteAllText(project, String.concat "\n" pilot)
        check (generate project) output
    finally
        System.IO.Directory.Delete(dir, true)

/// Generate a fidelity-mode pilot over `headers` in a fresh directory (see `withPilotSections`).
and withPilot (headers: string list) (options: string list) (functions: string list)
              (check: Result<BindingGenerator.GenerationResult, string> -> string -> unit) =
    withPilotSections headers [] options functions [] [] check

/// Clang's own answer, independent of its record layout dump: compile `header` with each
/// `assertion` as a `_Static_assert` (`sizeof`, `_Alignof`, `offsetof`) and fail with clang's
/// diagnostics unless every one holds.
let clangConfirms (header: string) (assertions: string list) =
    let probe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farscape-confirm-" + System.Guid.NewGuid().ToString("N") + ".c")
    try
        let body = assertions |> List.map (fun a -> $"_Static_assert({a}, \"{a.Replace('\"', '\'')}\");") |> String.concat "\n"
        System.IO.File.WriteAllText(probe, $"#include <stddef.h>\n#include \"{header}\"\n{body}\n")
        let info = System.Diagnostics.ProcessStartInfo("clang", $"-fsyntax-only {probe}")
        info.RedirectStandardError <- true
        info.RedirectStandardOutput <- true
        use proc = System.Diagnostics.Process.Start info
        let errors = proc.StandardError.ReadToEnd()
        proc.StandardOutput.ReadToEnd() |> ignore
        proc.WaitForExit()
        if proc.ExitCode <> 0 then failwithf "clang does not confirm the layout of %s: %s" header errors
    finally
        try System.IO.File.Delete probe with _ -> ()

/// Every emitted `Types.clef` of a generated package, its lines trimmed, in path order.
let allTypesOf (output: string) =
    System.IO.Directory.GetFiles(output, "Types.clef", System.IO.SearchOption.AllDirectories)
    |> Array.sort
    |> Array.collect System.IO.File.ReadAllLines
    |> Array.map (fun line -> line.Trim())
    |> String.concat "\n"

/// The text of one emitted layout module, from its headline to the next record's.
let recordModule (types: string) (name: string) =
    let at = types.IndexOf($"module {name} =")
    Xunit.Assert.True(at >= 0, $"module {name} is not emitted:\n{types}")
    let next = types.IndexOf("\nmodule ", at + 1)
    if next < 0 then types.Substring(at) else types.Substring(at, next - at)
