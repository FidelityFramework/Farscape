namespace Farscape.Core

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open XParsec
open XParsec.Parsers
open XParsec.Combinators
open XParsec.CharParsers

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
            "long", "int64" // LP64: long is 64-bit on Linux/macOS x86_64
            "long int", "int64"
            "unsigned long", "uint64"
            "unsigned long int", "uint64"
            "long long", "int64"
            "unsigned long long", "uint64"
            "float", "single"
            "double", "double"
            "long double", "double" // F# has no 80-bit float; best approximation
            "int8_t", "sbyte"
            "uint8_t", "byte"
            "int16_t", "int16"
            "uint16_t", "uint16"
            "int32_t", "int32"
            "uint32_t", "uint32"
            "int64_t", "int64"
            "uint64_t", "uint64"
            "size_t", "nativeint"
            "ssize_t", "nativeint"
            "ptrdiff_t", "nativeint"
            "intptr_t", "nativeint"
            "uintptr_t", "unativeint"
            // POSIX types (LP64)
            "off_t", "int64"
            "pid_t", "int32"
            "uid_t", "uint32"
            "gid_t", "uint32"
            "mode_t", "uint32"
            "dev_t", "uint64"
            "ino_t", "uint64"
            "nlink_t", "uint64"
            "blksize_t", "int64"
            "blkcnt_t", "int64"
            "time_t", "int64"
            "clockid_t", "int32"
            "suseconds_t", "int64"
            "useconds_t", "uint32"
            // glibc internal __ prefixed variants (same as POSIX types above)
            // clang AST uses these for parameters in system headers
            "__off_t", "int64"
            "__off64_t", "int64"
            "__pid_t", "int32"
            "__uid_t", "uint32"
            "__gid_t", "uint32"
            "__mode_t", "uint32"
            "__dev_t", "uint64"
            "__ino_t", "uint64"
            "__ino64_t", "uint64"
            "__nlink_t", "uint64"
            "__blksize_t", "int64"
            "__blkcnt_t", "int64"
            "__blkcnt64_t", "int64"
            "__time_t", "int64"
            "__clockid_t", "int32"
            "__suseconds_t", "int64"
            "__useconds_t", "uint32"
            "__ssize_t", "nativeint"
            "__intptr_t", "nativeint"
            "__socklen_t", "uint32"
            "locale_t", "nativeint" // opaque struct pointer
            "__locale_t", "nativeint"
            // glibc internal fixed-width variants
            "__int8_t", "sbyte"
            "__uint8_t", "byte"
            "__int16_t", "int16"
            "__uint16_t", "uint16"
            "__int32_t", "int32"
            "__uint32_t", "uint32"
            "__int64_t", "int64"
            "__uint64_t", "uint64"
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
        let pArrayLen =
            skipMany (satisfyL (fun c -> c <> '[') "non-bracket")
            >>. (skipChar '[' >>. pint32 .>> skipChar ']')
        let reader = Reader.ofString typeName ()
        match pArrayLen reader with
        | Ok result -> Some result.Parsed
        | Error _ -> None

    let cleanTypeName (typeName: string) =
        typeName
            .Replace("const ", "")
            .Replace(" const", "")
            .Replace("volatile ", "")
            .Replace(" volatile", "")
            .Replace("__restrict", "")
            .Replace("restrict", "")
            .Replace("__extension__ ", "")
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
        let rec collectTypes (decls: CppParser.Declaration list) : TypeMapping list =
            decls |> List.collect (fun decl ->
                match decl with
                | CppParser.Declaration.Function f ->
                    mapType f.ReturnType :: (f.Parameters |> List.map (fun (_, pt) -> mapType pt))
                | CppParser.Declaration.Struct s ->
                    mapType s.Name :: (s.Fields |> List.map (fun f -> mapType f.Type))
                | CppParser.Declaration.Macro _ -> []
                | CppParser.Declaration.Enum e -> [mapType e.Name]
                | CppParser.Declaration.Typedef t -> [mapType t.Name; mapType t.UnderlyingType]
                | CppParser.Declaration.Namespace ns -> collectTypes ns.Declarations
                | CppParser.Declaration.Class c ->
                    mapType c.Name
                    :: (c.Fields |> List.map (fun f -> mapType f.Type))
                    @ collectTypes c.Methods)
        collectTypes declarations
        |> List.distinctBy (fun t -> t.OriginalName)