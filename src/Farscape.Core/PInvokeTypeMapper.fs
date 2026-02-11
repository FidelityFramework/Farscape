namespace Farscape.Core

/// Separate CLR type dictionary for P/Invoke output.
///
/// P/Invoke types are CONCRETE — they must match the CLR's marshalling expectations
/// for a specific platform's ABI. This is fundamentally different from the NTU type
/// dictionary (TypeMapper), which emits platform-abstract types for Fidelity/FNCS.
///
/// Key differences from TypeMapper:
///   - C long → platform-specific (int64 on LP64, int32 on LLP64/ILP32)
///   - C int → int32 on all modern platforms (but parameterized for IP16)
///   - char* → string (CLR handles marshalling), not nativeptr<byte>
///   - No nativeint for C long (nativeint is pointer-width, C long is not on LLP64)
module PInvokeTypeMapper =

    /// Platform ABI determines concrete widths for platform-dependent types.
    /// LP64:  int=32, long=64, ptr=64 (Linux, macOS, most Unix)
    /// LLP64: int=32, long=32, ptr=64 (Windows x64)
    /// ILP32: int=32, long=32, ptr=32 (32-bit systems)
    /// IP16:  int=16, long=32, ptr=16/32 (AVR, MSP430, some embedded)
    type PlatformABI =
        | LP64
        | LLP64
        | ILP32
        | IP16

    let private intWidth = function
        | LP64 | LLP64 | ILP32 -> 32
        | IP16 -> 16

    let private longWidth = function
        | LP64 -> 64
        | LLP64 | ILP32 | IP16 -> 32

    let private fsharpIntType bits =
        match bits with
        | 16 -> "int16"
        | 32 -> "int32"
        | 64 -> "int64"
        | _ -> "int32"

    let private fsharpUintType bits =
        match bits with
        | 16 -> "uint16"
        | 32 -> "uint32"
        | 64 -> "uint64"
        | _ -> "uint32"

    let private makeTypeMap (model: PlatformABI) =
        let intType = fsharpIntType (intWidth model)
        let uintType = fsharpUintType (intWidth model)
        let longType = fsharpIntType (longWidth model)
        let ulongType = fsharpUintType (longWidth model)
        dict [
            // Primitive types — widths determined by platform ABI
            "void", "unit"
            "bool", "bool"
            "char", "byte"
            "signed char", "sbyte"
            "unsigned char", "byte"
            "short", "int16"
            "unsigned short", "uint16"
            "int", intType
            "unsigned int", uintType
            "long", longType
            "long int", longType
            "unsigned long", ulongType
            "unsigned long int", ulongType
            "long long", "int64"
            "unsigned long long", "uint64"
            "float", "single"
            "double", "double"
            "long double", "double"
            // Fixed-width types — same on all platforms
            "int8_t", "sbyte"
            "uint8_t", "byte"
            "int16_t", "int16"
            "uint16_t", "uint16"
            "int32_t", "int32"
            "uint32_t", "uint32"
            "int64_t", "int64"
            "uint64_t", "uint64"
            // Pointer-width types — CLR nativeint
            "size_t", "unativeint"
            "ssize_t", "nativeint"
            "ptrdiff_t", "nativeint"
            "intptr_t", "nativeint"
            "uintptr_t", "unativeint"
            // POSIX types (concrete widths for LP64, same across modern platforms)
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
            // glibc internal __ prefixed variants
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
            "locale_t", "nativeint"
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
            // Character types
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

    /// Cached type maps per data model.
    let private typeMaps =
        Map.ofList [
            LP64, makeTypeMap LP64
            LLP64, makeTypeMap LLP64
            ILP32, makeTypeMap ILP32
            IP16, makeTypeMap IP16
        ]

    /// Get the CLR F# type for a C type on the given platform data model.
    /// Uses the same cleanTypeName/pointer logic as TypeMapper but with
    /// platform-aware concrete widths.
    let getFSharpType (model: PlatformABI) (cppType: string) : string =
        let typeMap = typeMaps.[model]
        let cleaned = TypeMapper.cleanTypeName cppType

        if typeMap.ContainsKey(cleaned) then
            typeMap.[cleaned]
        elif typeMap.ContainsKey(cppType) then
            typeMap.[cppType]
        elif TypeMapper.isPointerType cppType then
            if cppType.Contains("char*") || cppType.Contains("const char*") then
                "string"
            else
                "nativeint"
        else
            cleaned
