namespace Farscape.Core

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Text.RegularExpressions

module TypeMapper =
    type TypeMapping = {
        OriginalName: string
        FSharpName: string
        MarshalAs: MarshalAsAttribute option
        IsPointer: bool
        IsConst: bool
        IsPrimitive: bool
        IsArray: bool
        ArrayLength: int option
    }
    
    let private typeMap = 
        dict [
            // Primitive types
            "void", "unit" 
            "bool", "bool"
            "char", "byte"
            "signed char", "sbyte"
            "unsigned char", "byte"
            "short", "int16"
            "unsigned short", "uint16"
            "int", "int32"
            "unsigned int", "uint32"
            "long", "int32" // This can be platform dependent
            "unsigned long", "uint32"
            "long long", "int64"
            "unsigned long long", "uint64"
            "float", "single"
            "double", "double"
            "int8_t", "sbyte"
            "uint8_t", "byte"
            "int16_t", "int16"
            "uint16_t", "uint16"
            "int32_t", "int32"
            "uint32_t", "uint32"
            "int64_t", "int64"
            "uint64_t", "uint64"
            "size_t", "nativeint"
            "ptrdiff_t", "nativeint"
            "intptr_t", "nativeint"
            "uintptr_t", "unativeint"
            "wchar_t", "char"
            "char16_t", "char"
            "char32_t", "uint32"
            // Pointer types
            "void*", "nativeint"
            "char*", "string"
            "const char*", "string"
            "wchar_t*", "string"
            "const wchar_t*", "string"
        ]
    
    let isPrimitiveType (typeName: string) =
        typeMap.ContainsKey(typeName) || 
        typeName.EndsWith("*") && typeMap.ContainsKey(typeName)
    
    let isPointerType (typeName: string) =
        typeName.Contains("*") || typeName.EndsWith("&")

    let isConstType (typeName: string) =
        typeName.StartsWith("const ") || typeName.Contains(" const")

    let isArrayType (typeName: string) =
        typeName.Contains("[") && typeName.Contains("]")

    let getArrayLength (typeName: string) =
        let match' = Regex.Match(typeName, @"\[(\d+)\]")
        if match'.Success then
            Some(Int32.Parse(match'.Groups.[1].Value))
        else
            None

    let cleanTypeName (typeName: string) =
        typeName
            .Replace("const ", "")
            .Replace(" const", "")
            .Replace("&", "")
            .Replace("*", "")
            .Replace("struct ", "")
            .Replace("class ", "")
            .Replace("enum ", "")
            .Replace("union ", "")
            .Trim()

    let getFSharpType (cppType: string) : string =
        let cleaned = cleanTypeName cppType
        
        if typeMap.ContainsKey(cleaned) then
            typeMap.[cleaned]
        elif typeMap.ContainsKey(cppType) then
            typeMap.[cppType]
        elif isPointerType cppType then
            if cppType.Contains("char*") || cppType.Contains("const char*") then
                "string"
            else
                "nativeint"
        else
            cleaned

    let getMarshalAs (cppType: string) : MarshalAsAttribute option =
        let marshalType = 
            if cppType.Contains("char*") || cppType.Contains("const char*") then
                Some(UnmanagedType.LPStr)
            elif cppType.Contains("wchar_t*") || cppType.Contains("const wchar_t*") then
                Some(UnmanagedType.LPWStr)
            elif isArrayType cppType then
                Some(UnmanagedType.LPArray)
            else
                None
                
        marshalType |> Option.map (fun t -> MarshalAsAttribute(t))

    let mapType (cppType: string) : TypeMapping =
        {
            OriginalName = cppType
            FSharpName = getFSharpType cppType
            MarshalAs = getMarshalAs cppType
            IsPointer = isPointerType cppType
            IsConst = isConstType cppType
            IsPrimitive = isPrimitiveType cppType
            IsArray = isArrayType cppType
            ArrayLength = getArrayLength cppType
        }

    let mapTypes (declarations: CppParser.Declaration list) : TypeMapping list =
        let rec collectTypes (decls: CppParser.Declaration list) =
            let mutable types = []
            
            for decl in decls do
                match decl with
                | CppParser.Declaration.Function f ->
                    types <- mapType f.ReturnType :: types
                    for (_, paramType) in f.Parameters do
                        types <- mapType paramType :: types
                        
                | CppParser.Declaration.Struct s ->
                    types <- mapType s.Name :: types
                    for field in s.Fields do
                        types <- mapType field.Type :: types

                | CppParser.Declaration.Macro _ ->
                    () // Macros don't contribute to type mappings
                        
                | CppParser.Declaration.Enum e ->
                    types <- mapType e.Name :: types
                    
                | CppParser.Declaration.Typedef t ->
                    types <- mapType t.Name :: types
                    types <- mapType t.UnderlyingType :: types
                    
                | CppParser.Declaration.Namespace ns ->
                    types <- collectTypes ns.Declarations @ types
                    
                | CppParser.Declaration.Class c ->
                    types <- mapType c.Name :: types
                    for field in c.Fields do
                        types <- mapType field.Type :: types
                    types <- collectTypes c.Methods @ types
            
            types
            
        collectTypes declarations
        |> List.distinctBy (fun t -> t.OriginalName)