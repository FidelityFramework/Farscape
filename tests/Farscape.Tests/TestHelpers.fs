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
    { Name = name; Values = values; Documentation = doc; UnderlyingType = None }

let mkEnumSimple name : CppParser.EnumDecl =
    { Name = name; Values = []; Documentation = None; UnderlyingType = None }

let mkEnumVal name value : CppParser.EnumValue =
    { Name = name; Value = value; Documentation = None }

let mkEnumValWithDoc name value doc : CppParser.EnumValue =
    { Name = name; Value = value; Documentation = Some doc }

let mkStruct name fields doc : CppParser.StructDecl =
    { Name = name; Fields = fields; Documentation = doc; IsUnion = false }

let mkStructSimple name : CppParser.StructDecl =
    { Name = name; Fields = []; Documentation = None; IsUnion = false }

let mkField name typ : CppParser.FieldDecl =
    { Name = name; Type = typ; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None
      IsBitfield = false; BitWidth = None }

let mkBitField name typ width : CppParser.FieldDecl =
    { Name = name; Type = typ; IsConst = false; IsVolatile = false; IsArray = false; ArraySize = None
      IsBitfield = true; BitWidth = Some width }

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
