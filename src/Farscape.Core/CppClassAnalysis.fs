namespace Farscape.Core

/// Analysis of C++ class declarations for binding generation.
///
/// Classifies classes by their ABI characteristics: pimpl pattern detection,
/// triviality analysis (determines sret vs register return convention),
/// and method return type cross-referencing.
///
/// The SysV x86_64 ABI classifies a type as MEMORY (returned via hidden sret
/// pointer) when it has a non-trivial copy constructor, move constructor, or
/// destructor. This shifts all argument registers by one position, which is
/// a common source of silent corruption in FFI bindings. This module detects
/// the condition and signals it to the code generator.
///
/// Architecture: Pure analysis functions over CppParser.ClassDecl. No code
/// generation, no mutable state. Consumed by FidelityCodeGenerator.OnClass
/// and PilotAnalyzer diagnostics.
module CppClassAnalysis =

    // =========================================================================
    // Return Convention Classification (SysV x86_64 ABI)
    // =========================================================================

    /// How a C++ type is returned from a function under SysV x86_64 ABI.
    ///
    /// RegisterReturn: the value is returned in rax (<=8 bytes) or rax:rdx
    /// (<=16 bytes). The caller reads the register(s) directly.
    ///
    /// SretReturn: the caller allocates return storage and passes a hidden
    /// pointer as the first argument (rdi). All other arguments shift by one
    /// register position. The callee writes the return value through the sret
    /// pointer and returns the same pointer in rax.
    type ReturnConvention =
        | RegisterReturn
        | SretReturn

    /// Estimate class size in bytes from visible field types.
    /// This is a conservative lower bound; actual size may include padding,
    /// vtable pointer, or base class storage. For binding generation, the
    /// precise size comes from the pilot.toml [cpp.class] size override
    /// or from clang's -fdump-record-layouts-simple output.
    let private estimateClassSize (cls: CppParser.ClassDecl) : int =
        if cls.Fields.IsEmpty then 0
        else
            cls.Fields
            |> List.sumBy (fun f ->
                match f.Type.Trim() with
                | t when t.Contains("*") -> 8    // pointer
                | "int" | "int32_t" | "uint32_t" | "float" -> 4
                | "long" | "long long" | "int64_t" | "uint64_t"
                | "size_t" | "ptrdiff_t" | "double" | "intptr_t" | "uintptr_t" -> 8
                | "char" | "unsigned char" | "int8_t" | "uint8_t" | "bool" -> 1
                | "short" | "unsigned short" | "int16_t" | "uint16_t" -> 2
                | _ when f.IsArray ->
                    match f.ArraySize with
                    | Some n -> n  // conservative: 1 byte per element
                    | None -> 8
                | _ -> 8)  // unknown: assume pointer-sized

    /// Determine whether a C++ class type uses sret for return-by-value.
    ///
    /// Under SysV x86_64 / Itanium ABI:
    /// - A type with a non-trivial destructor, copy constructor, or move
    ///   constructor is classified as MEMORY, regardless of size.
    /// - A trivially-copyable type > 16 bytes is classified as MEMORY.
    /// - A trivially-copyable type <= 16 bytes with INTEGER-class eightbytes
    ///   is returned in registers.
    /// - Abstract types cannot be returned by value.
    ///
    /// Empirically confirmed: xrt::uuid (16 bytes, user-defined constructors)
    /// triggers SretReturn despite being only 16 bytes.
    let classifyReturnConvention (cls: CppParser.ClassDecl) : ReturnConvention =
        if cls.IsAbstract then
            SretReturn
        elif cls.HasUserDestructor || cls.HasUserCopyConstructor || cls.HasUserMoveConstructor then
            SretReturn
        else
            // Trivially copyable. Check size.
            // Fields give a lower bound; actual size depends on padding and base classes.
            // For classes with no visible fields (opaque or forward-declared), assume
            // RegisterReturn and rely on .dynsym validation to catch mismatches.
            let estimatedSize = estimateClassSize cls
            if estimatedSize > 16 then SretReturn
            else RegisterReturn

    // =========================================================================
    // C++ Class Kind Classification
    // =========================================================================

    /// Structural classification of a C++ class for binding strategy selection.
    type CppClassKind =
        /// Pimpl idiom: single smart pointer field (shared_ptr, unique_ptr, detail::pimpl).
        /// Size is always 16 bytes (8 for unique_ptr). Requires constructor/destructor binding.
        | PimplClass of implType: string * size: int
        /// Value class with non-trivial members. May use sret for return.
        | ValueClass of size: int * returnConvention: ReturnConvention
        /// Pure virtual interface (abstract). Cannot be instantiated directly.
        | InterfaceClass
        /// No visible fields, not abstract. Likely opaque or forward-declared.
        | OpaqueClass
        /// Trivially copyable, no destructor obligation. Safe to bitwise copy.
        | PODClass of size: int

    /// Smart pointer type patterns in field types.
    let private isSmartPointerField (fieldType: string) : bool =
        fieldType.Contains("shared_ptr") ||
        fieldType.Contains("unique_ptr") ||
        fieldType.Contains("detail::pimpl")

    /// Check whether any base class type string indicates an inherited pimpl pattern.
    /// Matches patterns like "xrt::detail::pimpl<xrt::hw_context_impl>" or
    /// "detail::pimpl<xclbin_impl>".
    let private tryInheritedPimpl (baseClasses: string list) : (string * int) option =
        baseClasses
        |> List.tryPick (fun baseType ->
            if baseType.Contains("detail::pimpl") || baseType.Contains("shared_ptr") then
                Some (baseType, 16)
            elif baseType.Contains("unique_ptr") then
                Some (baseType, 8)
            else
                None)

    /// Classify a C++ class by its structural characteristics.
    let classifyClass (cls: CppParser.ClassDecl) : CppClassKind =
        if cls.IsAbstract then
            InterfaceClass
        elif cls.Fields.IsEmpty && cls.Methods.IsEmpty && cls.Constructors.IsEmpty &&
             cls.BaseClasses.IsEmpty then
            OpaqueClass
        else
            // Check for pimpl pattern: single smart pointer field
            let smartPtrFields =
                cls.Fields |> List.filter (fun f -> isSmartPointerField f.Type)
            if smartPtrFields.Length = 1 && cls.Fields.Length = 1 then
                // Pimpl: shared_ptr = 16 bytes, unique_ptr = 8 bytes
                let size =
                    if smartPtrFields.[0].Type.Contains("unique_ptr") then 8 else 16
                PimplClass(smartPtrFields.[0].Type, size)
            else
                // Check for inherited pimpl: no fields on this class, but base class
                // carries the smart pointer (e.g., detail::pimpl<T> CRTP base).
                match tryInheritedPimpl cls.BaseClasses with
                | Some (implType, size) ->
                    PimplClass(implType, size)
                | None ->
                    if not cls.HasUserDestructor && not cls.HasUserCopyConstructor && not cls.HasUserMoveConstructor then
                        let size = estimateClassSize cls
                        PODClass size
                    else
                        let size = estimateClassSize cls
                        let rc = classifyReturnConvention cls
                        ValueClass(size, rc)

    // =========================================================================
    // Method Return Type Analysis
    // =========================================================================

    /// Result of analyzing a method's return type for ABI convention.
    type MethodReturnInfo = {
        /// Method name
        MethodName: string
        /// C++ return type string
        ReturnType: string
        /// Whether this return type requires sret
        ReturnConvention: ReturnConvention
        /// If sret, the class name of the return type (for diagnostic)
        SretTypeName: string option
    }

    /// Analyze a method's return type against a set of known class declarations.
    /// If the return type matches a known class that requires sret, flag it.
    let analyzeMethodReturn
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (method: CppParser.FunctionDecl)
        : MethodReturnInfo =
        let retType = method.ReturnType.Trim()
        // Strip qualifiers and references for lookup
        let baseName =
            retType
                .Replace("const ", "").Replace("const", "")
                .Replace("&", "").Replace("*", "")
                .Trim()
        match Map.tryFind baseName knownClasses with
        | Some cls ->
            let rc = classifyReturnConvention cls
            { MethodName = method.Name
              ReturnType = retType
              ReturnConvention = rc
              SretTypeName = if rc = SretReturn then Some baseName else None }
        | None ->
            // Not a known class return type; assume register return.
            // Scalars, pointers, enums, and unknown types all use registers.
            { MethodName = method.Name
              ReturnType = retType
              ReturnConvention = RegisterReturn
              SretTypeName = None }

    /// Analyze all methods in a class for sret return types.
    let analyzeClassMethods
        (knownClasses: Map<string, CppParser.ClassDecl>)
        (cls: CppParser.ClassDecl)
        : MethodReturnInfo list =
        cls.Methods
        |> List.choose (function
            | CppParser.Declaration.Function f -> Some f
            | _ -> None)
        |> List.map (analyzeMethodReturn knownClasses)

    /// Build a lookup map from class name to ClassDecl for return type analysis.
    /// Handles nested namespace names: "xrt::uuid" maps from both "uuid" and "xrt::uuid".
    let buildClassMap (decls: CppParser.Declaration list) : Map<string, CppParser.ClassDecl> =
        let mutable map = Map.empty
        let rec collect = function
            | CppParser.Declaration.Class c ->
                map <- Map.add c.Name c map
            | CppParser.Declaration.Namespace ns ->
                for d in ns.Declarations do
                    match d with
                    | CppParser.Declaration.Class c ->
                        map <- Map.add c.Name c map
                        map <- Map.add (ns.Name + "::" + c.Name) c map
                    | _ -> ()
            | _ -> ()
        decls |> List.iter collect
        map
