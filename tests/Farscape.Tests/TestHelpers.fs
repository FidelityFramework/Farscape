module TestHelpers

open Farscape.Core

// ─── Function declaration helpers ───

let mkFunc name retType parms : CppParser.FunctionDecl =
    { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
      IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

let mkFuncWithAttrs name retType parms attrs : CppParser.FunctionDecl =
    { Name = name; ReturnType = retType; Parameters = parms; Documentation = None
      IsVirtual = false; IsStatic = false; IsInline = false; Attributes = attrs }

let mkFuncWithDoc name retType parms doc : CppParser.FunctionDecl =
    { Name = name; ReturnType = retType; Parameters = parms; Documentation = doc
      IsVirtual = false; IsStatic = false; IsInline = false; Attributes = [] }

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

// ─── Declaration wrappers ───

let mkDecl name retType parms attrs =
    CppParser.Declaration.Function (mkFuncWithAttrs name retType parms attrs)

let mkDeclWithDoc name retType parms doc =
    CppParser.Declaration.Function (mkFuncWithDoc name retType parms doc)
