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

    /// Class size in bytes: clang's measurement when the parse measured the class (#182), else
    /// an estimate from visible field types. The estimate is a conservative lower bound; actual size may include padding,
    /// vtable pointer, or base class storage. For binding generation, the
    /// precise size comes from the pilot.toml [cpp.class] size override
    /// or from clang's -fdump-record-layouts-simple output.
    let estimateClassSize (cls: CppParser.ClassDecl) : int =
        match cls.Measured with
        // Clang's measurement of the class in its declaring unit (#182): an unnamed bit-field
        // or a typedef's attribute moves bytes the visible field types cannot place.
        | Some layout -> layout.SizeBits / 8
        | None ->
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
                | "uuid_t" | "xuid_t" -> 16
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
        // A class of only unnamed bit-fields (#183) has storage clang lays out: not opaque.
        elif cls.Fields.IsEmpty && cls.UnnamedBitFields.IsEmpty && cls.Methods.IsEmpty && cls.Constructors.IsEmpty &&
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

    /// The refusal code belonging to the class's detected shape. A pilot override cannot
    /// turn an abstract, inherited or otherwise unsupported native shape into another ABI.
    let marshaledClassShapeCode (cls: CppParser.ClassDecl) =
        match classifyClass cls with
        | PODClass _ -> Diagnostics.CppClassAbiUnadmitted
        | PimplClass _ -> Diagnostics.CppPimplClassUnadmitted
        | ValueClass _ -> Diagnostics.CppValueClassUnadmitted
        | InterfaceClass -> Diagnostics.CppInterfaceClassUnadmitted
        | OpaqueClass -> Diagnostics.CppOpaqueClassUnadmitted

    /// Whether a class specification opts into the generated native C++ shim (#117)
    /// for virtual dispatch, inheritance casts, and exception boundary catching.
    let usesNativeShim (spec: PilotTypes.CppClassSpec) : bool =
        match spec.KindOverride with
        | Some ("shim" | "native_shim") -> true
        | _ -> false

    /// Whether a class specification opts into the companion C1/D1 storage adapter (#115)
    /// which wraps C1/D1/method asm calls in an extern "C" noexcept boundary.
    let usesStorageAdapter (spec: PilotTypes.CppClassSpec) : bool =
        match spec.KindOverride with
        | Some ("pimpl" | "value") -> true
        | _ -> false

    /// Whether a C++ constructor or method carries a `noexcept` attribute.
    let hasNoExcept (f: CppParser.FunctionDecl) : bool =
        f.Attributes |> List.exists (fun a -> a.Kind = "NoExceptAttr")

    /// Validate the native shape before generating any class declaration. This is a call
    /// contract check; it grants neither storage allocation nor compiler lifetime evidence.
    let validateMarshaledClass (spec: PilotTypes.CppClassSpec) (cls: CppParser.ClassDecl) =
        let refuse code reason =
            Diagnostics.diagnostic code $"C++ class '{cls.Name}': {reason}" cls.Location.Location
            |> Diagnostics.refuse
        let shape = classifyClass cls
        let code = marshaledClassShapeCode cls
        let methods = cls.Methods |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        let ctors = cls.Constructors |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        if not spec.Bind then refuse code "has no explicitly admitted marshaled binding."
        if spec.Name <> cls.Name && Some spec.Name <> cls.QualifiedName then
            refuse code $"admission for '{spec.Name}' does not select this class."
        if usesNativeShim spec then
            if shape = OpaqueClass && not cls.IsAbstract && cls.Measured.IsNone && spec.SizeOverride.IsNone then
                refuse Diagnostics.CppOpaqueClassUnadmitted "opaque storage requires a measured or overridden size."
            match spec.SizeOverride with
            | Some size when size <= 0 -> refuse code "storage size must be positive."
            | _ -> ()
            if not cls.IsAbstract && ctors.IsEmpty then
                refuse code "concrete shimmed class requires at least one constructor."
            if cls.DestructorMangledName |> Option.forall System.String.IsNullOrWhiteSpace then
                refuse code "shimmed class requires a destructor symbol."
            for f in ctors @ methods do
                if f.MangledName |> Option.forall System.String.IsNullOrWhiteSpace then
                    refuse code $"'{f.Name}' has no mangled symbol."
        else
            if cls.IsAbstract || methods |> List.exists _.IsVirtual then
                refuse Diagnostics.CppInterfaceClassUnadmitted "abstract or virtual dispatch requires a native adapter."
            if not cls.BaseClasses.IsEmpty then
                let pimplBase (name: string) = name.Contains("detail::pimpl") || name.Contains("shared_ptr") || name.Contains("unique_ptr")
                if not (cls.BaseClasses |> List.forall pimplBase) then
                    refuse code "non-pimpl base class hierarchy is unsupported."
            let hasC1 (f: CppParser.FunctionDecl) =
                f.MangledName |> Option.exists (fun s ->
                    (s.StartsWith("_Z") && s.Contains("C1")) ||
                    (s.StartsWith("farscape_cpp_") && s.Contains("_ctor")))
            if ctors.IsEmpty || not (ctors |> List.forall hasC1) then
                refuse code "requires complete-object C1 constructor symbols."
            if not (cls.DestructorMangledName |> Option.exists (fun s ->
                (s.StartsWith("_Z") && s.Contains("D1")) ||
                (s.StartsWith("farscape_cpp_") && s.EndsWith("_dtor")))) then
                refuse code "requires a complete-object D1 destructor symbol."
            if shape = OpaqueClass && cls.Measured.IsNone && spec.SizeOverride.IsNone then
                refuse Diagnostics.CppOpaqueClassUnadmitted "opaque storage requires a measured or overridden size."
            match spec.SizeOverride with
            | Some size when size <= 0 -> refuse code "storage size must be positive."
            | _ -> ()
            for f in ctors @ methods do
                if not (hasNoExcept f) && not (usesStorageAdapter spec) then
                    refuse code $"'{f.Name}' may throw across the native boundary."
                if f.MangledName |> Option.forall System.String.IsNullOrWhiteSpace then
                    refuse code $"'{f.Name}' has no mangled symbol."

    /// A single path of operations on handle identities. Aliases must retain the same
    /// identity; callers must verify every admitted path, including early exits.
    [<RequireQualifiedAccess>]
    type CppObjectOperation =
        | Construct of handle: string
        | MethodCall of handle: string * symbol: string
        | Release of handle: string
        | AcquireErrorPayload of errorHandle: string
        | ReadErrorPayload of errorHandle: string
        | ReleaseErrorPayload of errorHandle: string
        | ScopeExit

    /// Check a declared operation path against the same class contract generation uses.
    /// ScopeExit requires explicit cleanup; this verifier never invents a destructor call
    /// or substitutes for the compiler's ownership proof over a Clef program.
    let verifyCppObjectLifecycle (spec: PilotTypes.CppClassSpec) (cls: CppParser.ClassDecl)
                                 (operations: CppObjectOperation list) : unit =
        validateMarshaledClass spec cls
        let refuse code reason =
            Diagnostics.diagnostic code $"C++ class '{cls.Name}': {reason}" cls.Location.Location
            |> Diagnostics.refuse
        let shapeCode = marshaledClassShapeCode cls
        let safeName = (cls.QualifiedName |> Option.defaultValue cls.Name).Replace("::", "_")
        let methods = cls.Methods |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        let mangledSymbols = methods |> List.choose _.MangledName
        let shimSymbols =
            if usesNativeShim spec then
                methods |> List.mapi (fun i f ->
                    let suffix = if (methods |> List.filter (fun other -> other.Name = f.Name)).Length > 1 then "_" + string i else ""
                    "farscape_cpp_" + safeName + "_" + f.Name + suffix)
            else []
        let symbols = Set.ofList (mangledSymbols @ shimSymbols)
        let requireLive states handle =
            match Map.tryFind handle states with
            | Some true -> ()
            | Some false -> refuse Diagnostics.CppObjectUseAfterRelease $"handle '{handle}' is used after its D1 release."
            | None -> refuse shapeCode $"handle '{handle}' has no successful construction."
        let requireLiveError errorStates errorHandle =
            match Map.tryFind errorHandle errorStates with
            | Some true -> ()
            | Some false -> refuse Diagnostics.CppObjectUseAfterRelease $"error payload handle '{errorHandle}' is used after its release."
            | None -> refuse shapeCode $"error payload handle '{errorHandle}' has no acquisition."
        let requireClean states errorStates =
            match states |> Map.toList |> List.tryFind snd with
            | Some (handle, _) -> refuse shapeCode $"handle '{handle}' has no D1 release on this exit."
            | None ->
                match errorStates |> Map.toList |> List.tryFind snd with
                | Some (errorHandle, _) -> refuse shapeCode $"error payload handle '{errorHandle}' has no release on this exit."
                | None -> ()
        let rec verify states errorStates = function
            | [] -> requireClean states errorStates
            | CppObjectOperation.Construct handle :: rest ->
                if Map.containsKey handle states then refuse shapeCode $"handle identity '{handle}' is constructed more than once."
                verify (Map.add handle true states) errorStates rest
            | CppObjectOperation.MethodCall (handle, symbol) :: rest ->
                requireLive states handle
                if not (symbols.Contains symbol) then refuse shapeCode $"method symbol '{symbol}' is outside the admitted class contract."
                verify states errorStates rest
            | CppObjectOperation.Release handle :: rest ->
                match Map.tryFind handle states with
                | Some false -> refuse Diagnostics.CppObjectDoubleRelease $"handle '{handle}' receives its D1 release more than once."
                | _ -> requireLive states handle
                verify (Map.add handle false states) errorStates rest
            | CppObjectOperation.AcquireErrorPayload errorHandle :: rest ->
                if not (usesNativeShim spec) then refuse shapeCode "exception payload operations require a generated native shim."
                if Map.containsKey errorHandle errorStates then refuse shapeCode $"error payload handle '{errorHandle}' is acquired more than once."
                verify states (Map.add errorHandle true errorStates) rest
            | CppObjectOperation.ReadErrorPayload errorHandle :: rest ->
                if not (usesNativeShim spec) then refuse shapeCode "exception payload operations require a generated native shim."
                requireLiveError errorStates errorHandle
                verify states errorStates rest
            | CppObjectOperation.ReleaseErrorPayload errorHandle :: rest ->
                if not (usesNativeShim spec) then refuse shapeCode "exception payload operations require a generated native shim."
                match Map.tryFind errorHandle errorStates with
                | Some false -> refuse Diagnostics.CppObjectDoubleRelease $"error payload handle '{errorHandle}' receives its release more than once."
                | _ -> requireLiveError errorStates errorHandle
                verify states (Map.add errorHandle false errorStates) rest
            | CppObjectOperation.ScopeExit :: rest ->
                requireClean states errorStates
                if not rest.IsEmpty then refuse shapeCode "operations follow a terminal scope exit."
        verify Map.empty Map.empty operations

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
        // A pointer or reference returns the address, even when its pointee has a
        // non-trivial destructor. It is not a by-value object and never needs sret.
        let returnedClass =
            if retType.Contains("*") || retType.Contains("&") then None
            else Map.tryFind baseName knownClasses
        match returnedClass with
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
        let mutable map : Map<string, CppParser.ClassDecl> = Map.empty
        let register key (c: CppParser.ClassDecl) =
            let empty = c.Fields.IsEmpty && c.Methods.IsEmpty && c.Constructors.IsEmpty && c.BaseClasses.IsEmpty && not c.IsAbstract
            if not (empty && map.ContainsKey key) then map <- map.Add(key, c)
        let rec collect = function
            | CppParser.Declaration.Class c ->
                register c.Name c
                c.QualifiedName |> Option.iter (fun q -> register q c)
            | CppParser.Declaration.Namespace ns ->
                for d in ns.Declarations do
                    collect d
                    match d with
                    | CppParser.Declaration.Class c ->
                        register (ns.Name + "::" + c.Name) c
                    | _ -> ()
            | _ -> ()
        decls |> List.iter collect
        map

    /// Explicit storage contracts for reference parameters; no stdlib object is passed by value.
    let tryResolveStdlibParameterType (cType: string) : (string * string * int * int) option =
        if cType.Contains("basic_string_view") || cType.Contains("string_view") then
            Some ("std::string_view", "std_string_view", 16, 8)
        elif cType.Contains("basic_string") || cType.Contains("std::string") then
            Some ("std::string", "std_string", 32, 8)
        elif cType.Contains("chrono::milliseconds") || (cType.Contains("chrono::duration") && cType.Contains("1000")) then
            Some ("std::chrono::milliseconds", "std_chrono_milliseconds", 8, 8)
        else None

    let defaultXrtStdlibContracts : Map<string, PilotTypes.CppClassSpec> =
        [ "std::string_view", "std_string_view", 16
          "std::string", "std_string", 32
          "std::chrono::milliseconds", "std_chrono_milliseconds", 8 ]
        |> List.collect (fun (qualified, safe, size) ->
            [ qualified; safe ] |> List.map (fun name ->
                let spec : PilotTypes.CppClassSpec =
                    { Name = name; Namespace = "Xrt"; Bind = true; KindOverride = None; SizeOverride = Some size }
                name, spec))
        |> Map.ofList

    /// Native owned-storage adapter for the admitted C1/D1 operations. Its C entry points
    /// use generated Clef binding names; asm labels retain the library's exact symbols.
    /// Compile with the declaring header and link against the inspected C++ library.
    let renderCppObjectStorageShimC (header: string) (knownClasses: Map<string, CppParser.ClassDecl>)
                                    (spec: PilotTypes.CppClassSpec) (cls: CppParser.ClassDecl) =
        validateMarshaledClass spec cls
        let qualified (c: CppParser.ClassDecl) = c.QualifiedName |> Option.defaultValue c.Name
        let className = qualified cls
        let safeName = className.Replace("::", "_")
        let quote (s: string) =
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let nsPrefix =
            match cls.QualifiedName with
            | Some q when q.Contains("::") -> Some (q.Substring(0, q.LastIndexOf("::")))
            | _ -> None
        let nativeType (typ: string) =
            match knownClasses.TryFind typ with
            | Some c -> qualified c
            | None ->
                match nsPrefix with
                | Some ns when typ.StartsWith(cls.Name + "::", System.StringComparison.Ordinal) ->
                    ns + "::" + typ
                | _ -> typ
        let funcs declarations = declarations |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        let parameterParts (f: CppParser.FunctionDecl) =
            f.Parameters |> List.mapi (fun i (_, typ) -> nativeType typ + " arg" + string i),
            f.Parameters |> List.mapi (fun i _ -> "arg" + string i)
        let joined = String.concat ", "
        let size = spec.SizeOverride |> Option.defaultWith (fun () -> estimateClassSize cls)
        let align =
            match cls.Measured with
            | Some m ->
                let measuredAlign = max 1 (m.AlignmentBits / 8)
                if usesStorageAdapter spec && size >= 16 then max 8 measuredAlign else measuredAlign
            | None -> 8
        let methods = funcs cls.Methods
        if usesNativeShim spec then
            let prefix = "farscape_cpp_" + safeName
            let errStruct = prefix + "_error"
            let lastErr = prefix + "_last_error"
            let clearErr = prefix + "_clear_error"
            let recordErr = prefix + "_record_error"
            let staticAsserts =
                if cls.IsAbstract then []
                else
                    [ sprintf "static_assert(sizeof(%s) <= %d, \"class storage size mismatch\");" className size
                      sprintf "static_assert(alignof(%s) <= %d, \"class storage alignment mismatch\");" className align ]
            let inlineErr = prefix + "_inline_error"
            let inlineInUse = prefix + "_inline_in_use"
            let errorRuntime =
                [ sprintf "struct %s { int status; int length; char message[256]; bool dynamic; };" errStruct
                  sprintf "static thread_local %s %s = {0, 0, {0}, false};" errStruct inlineErr
                  sprintf "static thread_local bool %s = false;" inlineInUse
                  sprintf "static thread_local %s* %s = nullptr;" errStruct lastErr
                  sprintf "extern \"C\" void %s_error_dtor(void* err) noexcept {" prefix
                  "  if (!err) return;"
                  sprintf "  %s* e = static_cast<%s*>(err);" errStruct errStruct
                  sprintf "  if (e == &%s) { %s = false; }" inlineErr inlineInUse
                  "  else if (e->dynamic) { std::free(e); }"
                  "}"
                  sprintf "static void %s() noexcept {" clearErr
                  sprintf "  if (%s) { %s_error_dtor(%s); %s = nullptr; }" lastErr prefix lastErr lastErr
                  "}"
                  sprintf "static void %s(int status, const char* msg) noexcept {" recordErr
                  sprintf "  %s();" clearErr
                  sprintf "  %s* slot = nullptr;" errStruct
                  sprintf "  if (!%s) { %s = true; slot = &%s; slot->dynamic = false; }" inlineInUse inlineInUse inlineErr
                  sprintf "  else { slot = static_cast<%s*>(std::malloc(sizeof(%s))); if (slot) slot->dynamic = true; }" errStruct errStruct
                  "  if (!slot) return;"
                  "  slot->status = status;"
                  "  int len = 0;"
                  "  if (msg) {"
                  "    while (msg[len] != '\\0' && len < 255) { slot->message[len] = msg[len]; ++len; }"
                  "  }"
                  "  slot->message[len] = '\\0';"
                  "  slot->length = len;"
                  sprintf "  %s = slot;" lastErr
                  "}"
                  sprintf "extern \"C\" void* %s_take_error() noexcept {" prefix
                  sprintf "  %s* err = %s;" errStruct lastErr
                  sprintf "  %s = nullptr;" lastErr
                  "  return err;"
                  "}"
                  sprintf "extern \"C\" int %s_error_code(void* err) noexcept {" prefix
                  sprintf "  return err ? static_cast<%s*>(err)->status : 0;" errStruct
                  "}"
                  sprintf "extern \"C\" int %s_error_len(void* err) noexcept {" prefix
                  sprintf "  return err ? static_cast<%s*>(err)->length : 0;" errStruct
                  "}"
                  sprintf "extern \"C\" int %s_error_byte(void* err, int index) noexcept {" prefix
                  "  if (!err || index < 0) return -1;"
                  sprintf "  const %s* e = static_cast<const %s*>(err);" errStruct errStruct
                  "  return index < e->length ? static_cast<unsigned char>(e->message[index]) : -1;"
                  "}" ]
            let shimCtors =
                if cls.IsAbstract then []
                else
                    funcs cls.Constructors
                    |> List.mapi (fun i f ->
                        let suffix = if i = 0 then "" else "_" + string i
                        let name = prefix + "_ctor" + suffix
                        let declarations, arguments = parameterParts f
                        [ sprintf "extern \"C\" void* %s(%s) noexcept {" name (joined declarations)
                          sprintf "  %s();" clearErr
                          "  try {"
                          sprintf "    void* obj = static_cast<void*>(new (std::nothrow) %s(%s));" className (joined arguments)
                          "    if (!obj) {"
                          sprintf "      %s(3, \"object allocation failed\");" recordErr
                          "      return nullptr;"
                          "    }"
                          "    return obj;"
                          "  } catch (const std::exception& ex) {"
                          sprintf "    %s(1, ex.what());" recordErr
                          "    return nullptr;"
                          "  } catch (...) {"
                          sprintf "    %s(2, \"non-std C++ exception\");" recordErr
                          "    return nullptr;"
                          "  }"
                          "}" ])
                    |> List.concat
            let shimDtor =
                [ sprintf "extern \"C\" void %s_dtor(void* self) noexcept {" prefix
                  "  if (!self) return;"
                  sprintf "  %s();" clearErr
                  "  try {"
                  sprintf "    delete static_cast<%s*>(self);" className
                  "  } catch (const std::exception& ex) {"
                  sprintf "    %s(1, ex.what());" recordErr
                  "  } catch (...) {"
                  sprintf "    %s(2, \"non-std C++ exception\");" recordErr
                  "  }"
                  "}" ]
            let shimUpcasts =
                cls.BaseClasses
                |> List.choose (fun baseName ->
                    let cleanBase = baseName.Replace("class ", "").Replace("struct ", "").Trim()
                    knownClasses.TryFind cleanBase)
                |> List.collect (fun baseCls ->
                    let baseQualified = qualified baseCls
                    let baseSafe = baseQualified.Replace("::", "_")
                    [ sprintf "extern \"C\" void* %s_as_%s(void* self) noexcept {" prefix baseSafe
                      "  if (!self) return nullptr;"
                      sprintf "  return static_cast<void*>(static_cast<%s*>(static_cast<%s*>(self)));" baseQualified className
                      "}" ])
            let shimMethods =
                methods
                |> List.mapi (fun i f ->
                    let suffix = if (methods |> List.filter (fun other -> other.Name = f.Name)).Length > 1 then "_" + string i else ""
                    let name = prefix + "_" + f.Name + suffix
                    let declarations, arguments = parameterParts f
                    let receivers = if f.IsStatic then [] else [ "void* self" ]
                    let ret = nativeType f.ReturnType
                    let call =
                        if f.IsStatic then sprintf "%s::%s(%s)" className f.Name (joined arguments)
                        else sprintf "static_cast<%s*>(self)->%s(%s)" className f.Name (joined arguments)
                    if hasNoExcept f then
                        match (analyzeMethodReturn knownClasses f).ReturnConvention with
                        | SretReturn ->
                            [ sprintf "extern \"C\" void %s(%s) noexcept {" name (joined ("void* retStorage" :: receivers @ declarations))
                              sprintf "  ::new (retStorage) %s(%s);" ret call
                              "}" ]
                        | RegisterReturn ->
                            if ret = "void" then
                                [ sprintf "extern \"C\" void %s(%s) noexcept { %s; }" name (joined (receivers @ declarations)) call ]
                            else
                                [ sprintf "extern \"C\" %s %s(%s) noexcept { return %s; }" ret name (joined (receivers @ declarations)) call ]
                    else
                        let nullCheck =
                            if f.IsStatic then []
                            else [ "  if (!self) return -1;" ]
                        let catchBlocks =
                            [ "  } catch (const std::exception& ex) {"
                              sprintf "    %s(1, ex.what());" recordErr
                              "    return 1;"
                              "  } catch (...) {"
                              sprintf "    %s(2, \"non-std C++ exception\");" recordErr
                              "    return 2;"
                              "  }"
                              "}" ]
                        match (analyzeMethodReturn knownClasses f).ReturnConvention with
                        | SretReturn ->
                            [ sprintf "extern \"C\" int %s(%s) noexcept {" name (joined ("void* outResult" :: receivers @ declarations))
                              "  if (!outResult) return -1;" ]
                            @ nullCheck
                            @ [ sprintf "  %s();" clearErr
                                "  try {"
                                sprintf "    ::new (outResult) %s(%s);" ret call
                                "    return 0;" ]
                            @ catchBlocks
                        | RegisterReturn ->
                            if ret = "void" then
                                [ sprintf "extern \"C\" int %s(%s) noexcept {" name (joined (receivers @ declarations)) ]
                                @ nullCheck
                                @ [ sprintf "  %s();" clearErr
                                    "  try {"
                                    sprintf "    %s;" call
                                    "    return 0;" ]
                                @ catchBlocks
                            else
                                [ sprintf "extern \"C\" int %s(%s) noexcept {" name (joined ((ret + "* outResult") :: receivers @ declarations))
                                  "  if (!outResult) return -1;" ]
                                @ nullCheck
                                @ [ sprintf "  %s();" clearErr
                                    "  try {"
                                    sprintf "    *outResult = %s;" call
                                    "    return 0;" ]
                                @ catchBlocks)
                |> List.concat
            [ "#include <new>"
              "#include <cstdlib>"
              "#include <exception>"
              "#include <string>"
              "#include " + quote header ]
            @ staticAsserts
            @ errorRuntime
            @ shimCtors
            @ shimDtor
            @ shimUpcasts
            @ shimMethods
            |> String.concat "\n"
        else
            let constructors =
                funcs cls.Constructors
                |> List.mapi (fun i f ->
                    let suffix = if i = 0 then "" else "_" + string i
                    let name = safeName + "_ctor" + suffix
                    let declarations, arguments = parameterParts f
                    let raw = name + "_c1"
                    let symbol = f.MangledName.Value
                    let companion = symbol.StartsWith("farscape_cpp_")
                    let entry = if companion then symbol else name
                    let noexceptSpec = if hasNoExcept f then " noexcept" else ""
                    (if companion then [] else
                        [ sprintf "extern \"C\" void %s(%s)%s asm(%s);" raw (joined ("void* self" :: declarations)) noexceptSpec (quote symbol) ]) @
                    (if hasNoExcept f then
                        [ sprintf "extern \"C\" void* %s(%s) noexcept {" entry (joined declarations)
                          sprintf "  void* storage = ::operator new(%d, std::align_val_t(%d), std::nothrow);" size align
                          "  if (!storage) return nullptr;"
                          (if companion then sprintf "  ::new (storage) %s(%s);" className (joined arguments)
                           else sprintf "  %s(%s);" raw (joined ("storage" :: arguments)))
                          "  return storage;"
                          "}" ]
                     else
                        [ sprintf "extern \"C\" void* %s(%s) noexcept {" entry (joined declarations)
                          sprintf "  void* storage = ::operator new(%d, std::align_val_t(%d), std::nothrow);" size align
                          "  if (!storage) return nullptr;"
                          "  try {"
                          (if companion then sprintf "    ::new (storage) %s(%s);" className (joined arguments)
                           else sprintf "    %s(%s);" raw (joined ("storage" :: arguments)))
                          "    return storage;"
                          "  } catch (...) {"
                          sprintf "    ::operator delete(storage, std::align_val_t(%d));" align
                          "    return nullptr;"
                          "  }"
                          "}" ]) @
                    (if entry = name then [] else
                        [ sprintf "extern \"C\" void* %s(%s) noexcept { return %s(%s); }" name (joined declarations) entry (joined arguments) ]))
                |> List.concat
            let destructor =
                let symbol = cls.DestructorMangledName.Value
                let companion = symbol.StartsWith("farscape_cpp_")
                let name = safeName + "_dtor"
                let entry = if companion then symbol else name
                (if companion then [] else
                    [ sprintf "extern \"C\" void %s_d1(void* self) noexcept asm(%s);" safeName (quote symbol) ]) @
                [ sprintf "extern \"C\" void %s(void* self) noexcept {" entry
                  "  if (!self) return;"
                  (if companion then sprintf "  static_cast<%s*>(self)->~%s();" className cls.Name
                   else sprintf "  %s_d1(self);" safeName)
                  sprintf "  ::operator delete(self, std::align_val_t(%d));" align
                  "}" ] @
                (if entry = name then [] else
                    [ sprintf "extern \"C\" void %s(void* self) noexcept { %s(self); }" name entry ])
            let forwards =
                methods
                |> List.mapi (fun i f ->
                    let suffix = if (methods |> List.filter (fun other -> other.Name = f.Name)).Length > 1 then "_" + string i else ""
                    let name = safeName + "_" + f.Name + suffix
                    let raw = name + "_method"
                    let declarations, arguments = parameterParts f
                    let receivers = if f.IsStatic then [] else [ "void* self" ]
                    let received = if f.IsStatic then [] else [ "self" ]
                    let ret = nativeType f.ReturnType
                    let call = raw + "(" + joined (received @ arguments) + ")"
                    let noexceptSpec = if hasNoExcept f then " noexcept" else ""
                    let declaration = sprintf "extern \"C\" %s %s(%s)%s asm(%s);" ret raw (joined (receivers @ declarations)) noexceptSpec (quote f.MangledName.Value)
                    match (analyzeMethodReturn knownClasses f).ReturnConvention with
                    | SretReturn ->
                        if hasNoExcept f then
                            [ declaration
                              sprintf "extern \"C\" void %s(%s) noexcept {" name (joined ("void* retStorage" :: receivers @ declarations))
                              sprintf "  ::new (retStorage) %s(%s);" ret call
                              "}" ]
                        else
                            [ declaration
                              sprintf "extern \"C\" void %s(%s) noexcept {" name (joined ("void* retStorage" :: receivers @ declarations))
                              "  if (!retStorage) return;"
                              "  try {"
                              sprintf "    ::new (retStorage) %s(%s);" ret call
                              "  } catch (...) {}"
                              "}" ]
                    | RegisterReturn ->
                        if hasNoExcept f then
                            [ declaration
                              sprintf "extern \"C\" %s %s(%s) noexcept { return %s; }" ret name (joined (receivers @ declarations)) call ]
                        elif ret = "void" then
                            [ declaration
                              sprintf "extern \"C\" void %s(%s) noexcept { try { %s; } catch (...) {} }" name (joined (receivers @ declarations)) call ]
                        else
                            [ declaration
                              sprintf "extern \"C\" %s %s(%s) noexcept { try { return %s; } catch (...) { return {}; } }" ret name (joined (receivers @ declarations)) call ])
                |> List.concat
            [ "#include <new>"
              "#include " + quote header ]
            @ (match nsPrefix with Some ns -> [ sprintf "using namespace %s;" ns ] | None -> [])
            @ [ sprintf "static_assert(sizeof(%s) <= %d, \"class storage size mismatch\");" className size
                sprintf "static_assert(alignof(%s) <= %d, \"class storage alignment mismatch\");" className align ]
            @ constructors
            @ destructor
            @ forwards
            |> String.concat "\n"

    /// Identity of the exact artifact whose exported symbols were inspected (#118).
    type CppLibraryBuild = {
        LibraryPath: string
        ResolvedPath: string
        Sha256: string
        Soname: string option
    }

    type CppTemplateInstantiation = {
        TemplateName: string
        TemplateArguments: string list
        InstantiationName: string
        /// Parallel lists, ordered by mangled symbol; signatures remain paired.
        MangledSymbols: string list
        DemangledSymbols: string list
        AstDeclared: bool
    }

    type CppTemplateInventory = {
        Build: CppLibraryBuild
        Instantiations: CppTemplateInstantiation list
        Declarations: CppParser.TemplateDeclaration list
    }

    /// Normalize spelling only, never aliases or semantically different C++ types.
    let private normalize (text: string) =
        System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ")
        |> fun text -> System.Text.RegularExpressions.Regex.Replace(text, @"\s*([<>,])\s*", "$1")

    let instantiationName templateName arguments =
        normalize templateName + "<" + (arguments |> List.map normalize |> String.concat ",") + ">"

    /// Split only at the outer argument-list level, including function/array arguments.
    let private splitArguments (text: string) =
        let mutable angles, parentheses, brackets = 0, 0, 0
        let mutable start = 0
        [
            for i in 0 .. text.Length - 1 do
                match text.[i] with
                | '<' -> angles <- angles + 1
                | '>' -> angles <- angles - 1
                | '(' -> parentheses <- parentheses + 1
                | ')' -> parentheses <- parentheses - 1
                | '[' -> brackets <- brackets + 1
                | ']' -> brackets <- brackets - 1
                | ',' when angles = 0 && parentheses = 0 && brackets = 0 ->
                    yield normalize text.[start .. i - 1]
                    start <- i + 1
                | _ -> ()
            if start < text.Length then yield normalize text.[start ..]
        ]

    let private operatorAngle (text: string) index operatorIndex =
        operatorIndex >= 0 &&
        (text.Substring(operatorIndex + 8, index - operatorIndex - 8)
         |> Seq.forall (fun c -> "<>=!+-*/%^&|~".Contains c))

    /// Extract the symbol's own qualified name (excluding return type and parameter list).
    let private callableQualifiedName (signature: string) =
        let prefixes = [
            "non-virtual thunk to "; "virtual thunk to "; "covariant return thunk to "
            "vtable for "; "VTT for "; "typeinfo for "; "typeinfo name for "; "guard variable for "
        ]
        let signature =
            prefixes |> List.fold (fun (text: string) prefix ->
                if text.StartsWith(prefix, System.StringComparison.Ordinal) then text.Substring prefix.Length else text) signature
        let mutable depth, nameStart, finish = 0, 0, signature.Length
        let mutable operatorStart = -1
        let mutable i = 0
        while i < finish do
            if depth = 0 && signature.Substring(i).StartsWith("operator", System.StringComparison.Ordinal) then
                operatorStart <- i
            match signature.[i] with
            | '<' when not (operatorAngle signature i operatorStart) -> depth <- depth + 1
            | '>' when depth > 0 -> depth <- depth - 1
            | '(' when depth = 0 && operatorStart >= 0 && i = operatorStart + 8
                       && i + 1 < signature.Length && signature.[i + 1] = ')' -> i <- i + 1
            | '(' when depth = 0 -> finish <- i
            | c when System.Char.IsWhiteSpace c && depth = 0 && operatorStart < 0 -> nameStart <- i + 1
            | _ -> ()
            i <- i + 1
        signature.Substring(nameStart, finish - nameStart)

    /// Only templates in the symbol's own qualified name count. Return types and parameter
    /// types mentioning a template do not export that template's operations.
    let private symbolInstantiations (signature: string) =
        let name = callableQualifiedName signature
        let mutable opening = -1
        let mutable operatorStart = -1
        let mutable depth = 0
        [
            for i in 0 .. name.Length - 1 do
                if depth = 0 && name.Substring(i).StartsWith("operator", System.StringComparison.Ordinal) then
                    operatorStart <- i
                match name.[i] with
                | '<' when not (operatorAngle name i operatorStart)
                           && not (operatorStart >= 0 && name.[operatorStart + 8] = ' ') ->
                    if depth = 0 then opening <- i
                    depth <- depth + 1
                | '>' when depth > 0 ->
                    depth <- depth - 1
                    if depth = 0 then
                        let templateName = normalize name.[0 .. opening - 1]
                        let arguments = splitArguments name.[opening + 1 .. i - 1]
                        yield templateName, arguments
                | _ -> ()
        ]

    /// Pure symbol-to-inventory join. AST recipes annotate exports; they never add exports.
    let inventoryOfSymbols (build: CppLibraryBuild) (declarations: CppParser.TemplateDeclaration list)
                           (symbols: (string * string) list) : CppTemplateInventory =
        let declared = declarations |> List.map _.TemplateName |> Set.ofList
        let instantiations =
            symbols
            |> List.filter (fun (mangled, demangled) -> mangled.StartsWith("_Z", System.StringComparison.Ordinal) && demangled <> mangled)
            |> List.collect (fun (mangled, demangled) ->
                symbolInstantiations demangled |> List.map (fun identity -> identity, (mangled, demangled)))
            |> List.groupBy fst
            |> List.map (fun ((name, arguments), entries) ->
                let pairs = entries |> List.map snd |> List.distinct |> List.sortBy fst
                {
                    TemplateName = name
                    TemplateArguments = arguments
                    InstantiationName = instantiationName name arguments
                    MangledSymbols = pairs |> List.map fst
                    DemangledSymbols = pairs |> List.map snd
                    AstDeclared = Set.contains name declared
                })
            |> List.sortBy _.InstantiationName
        { Build = build; Instantiations = instantiations; Declarations = declarations }

    let private tryRunSymbolTool program arguments (input: string option) : Result<string, Diagnostics.Refusal> =
        try
            let info = ProcessLaunch.startInfo program arguments
            info.Environment.["LC_ALL"] <- "C"
            info.RedirectStandardInput <- input.IsSome
            use child = new System.Diagnostics.Process(StartInfo = info)
            if not (child.Start()) then
                Error (Diagnostics.uncoded $"Could not start {program}.")
            else
                let output = child.StandardOutput.ReadToEndAsync()
                let errors = child.StandardError.ReadToEndAsync()
                match input with
                | Some text ->
                    child.StandardInput.Write text
                    child.StandardInput.Close()
                | None -> ()
                child.WaitForExit()
                let output = output.GetAwaiter().GetResult()
                let errors = errors.GetAwaiter().GetResult()
                if child.ExitCode <> 0 then
                    Error (Diagnostics.uncoded $"{program} failed ({child.ExitCode}): {errors.Trim()}")
                else
                    Ok output
        with
        | :? System.ComponentModel.Win32Exception
        | :? System.InvalidOperationException
        | :? System.IO.IOException as ex ->
            Error (Diagnostics.uncoded $"{program} launch failed ({ex.Message}).")

    let private runSymbolTool program arguments (input: string option) =
        match tryRunSymbolTool program arguments input with
        | Ok output -> output
        | Error refusal -> Diagnostics.raiseRefusal refusal

    type CppSymbolMetadata = {
        Mangled: string
        Demangled: string
        Address: uint64
        SizeBytes: int
    }

    let private readBinaryVtableWords (resolvedPath: string) (vtableAddr: uint64) (vtableSize: int) : uint64 list =
        if vtableAddr = 0UL || vtableSize < 24 then []
        else
            let startHex = sprintf "0x%x" vtableAddr
            let stopHex = sprintf "0x%x" (vtableAddr + uint64 vtableSize)
            let dump = runSymbolTool "objdump" ["-s"; $"--start-address={startHex}"; $"--stop-address={stopHex}"; "--"; resolvedPath] None
            let bytes = ResizeArray<byte>()
            for rawLine in dump.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) do
                if rawLine.StartsWith(" ", System.StringComparison.Ordinal) then
                    let trimmed = rawLine.TrimStart()
                    let firstSpace = trimmed.IndexOf(' ')
                    if firstSpace > 0 then
                        let hexAddr = trimmed.Substring(0, firstSpace)
                        let mutable parsedAddr = 0UL
                        if System.UInt64.TryParse(hexAddr, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, &parsedAddr) then
                            let afterAddr = trimmed.Substring(firstSpace + 1)
                            let asciiGap = afterAddr.IndexOf("  ", System.StringComparison.Ordinal)
                            let hexPart = if asciiGap >= 0 then afterAddr.Substring(0, asciiGap) else afterAddr
                            for group in hexPart.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) do
                                let mutable idx = 0
                                while idx + 1 < group.Length do
                                    let mutable b = 0uy
                                    if System.Byte.TryParse(group.Substring(idx, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, &b) then
                                        bytes.Add b
                                    idx <- idx + 2
            let raw = bytes.ToArray()
            [
                let mutable offset = 0
                while offset + 8 <= raw.Length do
                    yield System.BitConverter.ToUInt64(raw, offset)
                    offset <- offset + 8
            ]

    let private exportDetailCache =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string option * (string * string) list * Map<string, (int * string * string) list>>()

    /// Inspect one ELF shared object or static archive and return its build provenance,
    /// paired (mangled, demangled) exported C++ symbols, per-symbol metadata, and binary vtable slots.
    let private inspectLibraryExportsDetailed (libraryPath: string) : CppLibraryBuild * (string * string) list * Map<string, (int * string * string) list> =
        let file = System.IO.FileInfo(System.IO.Path.GetFullPath libraryPath)
        let resolved =
            match file.ResolveLinkTarget true with
            | null -> file.FullName
            | target -> target.FullName
        let hash = ClangToolchain.sha256 resolved
        let soname, paired, vtableSlots =
            exportDetailCache.GetOrAdd((resolved, hash), fun _ ->
                let archive =
                    use stream = System.IO.File.OpenRead resolved
                    let magic = Array.zeroCreate<byte> 8
                    stream.ReadExactly magic
                    List.contains (System.Text.Encoding.ASCII.GetString magic) ["!<arch>\n"; "!<thin>\n"]
                let lines (text: string) = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                let soname, rawEntries =
                    if archive then
                        let output = runSymbolTool "nm" ["--extern-only"; "--defined-only"; "--format=posix"; "--"; resolved] None
                        None,
                        (lines output |> List.choose (fun line ->
                            let columns = line.Split([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
                            if columns.Length >= 2 && columns.[0].StartsWith("_Z") then
                                let mutable addr = 0UL
                                let mutable sz = 0
                                if columns.Length >= 3 then System.UInt64.TryParse(columns.[2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, &addr) |> ignore
                                if columns.Length >= 4 then System.Int32.TryParse(columns.[3], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, &sz) |> ignore
                                Some (columns.[0], addr, sz)
                            else None))
                    else
                        let dynamic = runSymbolTool "readelf" ["-d"; "--wide"; "--"; resolved] None
                        let soname =
                            lines dynamic |> List.tryPick (fun line ->
                                if line.Contains("(SONAME)") then
                                    let opening, closing = line.IndexOf('['), line.LastIndexOf(']')
                                    if opening >= 0 && closing > opening then Some (line.Substring(opening + 1, closing - opening - 1)) else None
                                else None)
                        let output = runSymbolTool "readelf" ["--dyn-syms"; "--wide"; "--"; resolved] None
                        soname,
                        (lines output |> List.choose (fun line ->
                            let columns = line.Split([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
                            if columns.Length >= 8
                               && List.contains columns.[4] ["GLOBAL"; "WEAK"; "UNIQUE"]
                               && List.contains columns.[5] ["DEFAULT"; "PROTECTED"]
                               && columns.[6] <> "UND"
                               && columns.[7].StartsWith("_Z") then
                                let mutable addr = 0UL
                                System.UInt64.TryParse(columns.[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, &addr) |> ignore
                                let sizeText = columns.[2]
                                let mutable sz = 0
                                if sizeText.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) then
                                    System.Int32.TryParse(sizeText.Substring 2, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, &sz) |> ignore
                                else
                                    System.Int32.TryParse(sizeText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, &sz) |> ignore
                                Some (columns.[7].Split('@').[0], addr, sz)
                            else None))
                let byMangled =
                    rawEntries
                    |> List.groupBy (fun (m, _, _) -> m)
                    |> List.map (fun (m, entries) ->
                        let _, addr, sz = entries.Head
                        m, (addr, sz))
                    |> Map.ofList
                let symbols = byMangled |> Map.keys |> Seq.toList |> List.sort
                let demangled =
                    if symbols.IsEmpty then []
                    else runSymbolTool "c++filt" ["--no-strip-underscore"] (Some (String.concat "\n" symbols + "\n")) |> lines
                if symbols.Length <> demangled.Length then
                    Diagnostics.raiseRefusal (Diagnostics.uncoded "c++filt did not return one signature per exported symbol.")
                let paired = List.zip symbols demangled
                let demangledMap = Map.ofList paired
                let byAddr =
                    paired
                    |> List.choose (fun (m, _) ->
                        match Map.tryFind m byMangled with
                        | Some (addr, _) when addr > 0UL -> Some (addr, m)
                        | _ -> None)
                    |> List.groupBy fst
                    |> List.map (fun (addr, items) ->
                        let candidates =
                            items |> List.map snd |> List.sortBy (fun m ->
                                if m.EndsWith("D1Ev", System.StringComparison.Ordinal) then 0
                                elif m.EndsWith("D0Ev", System.StringComparison.Ordinal) then 1
                                elif m.EndsWith("D2Ev", System.StringComparison.Ordinal) then 2
                                elif m.Contains("C1") then 3
                                elif m.Contains("C2") then 4
                                else 5)
                        addr, candidates.Head)
                    |> Map.ofList
                let vtableSlots =
                    if archive then Map.empty
                    else
                        paired
                        |> List.choose (fun (m, _) ->
                            if m.StartsWith("_ZTV", System.StringComparison.Ordinal) then
                                match Map.tryFind m byMangled with
                                | Some (addr, sz) when addr > 0UL && sz >= 24 ->
                                    let words = readBinaryVtableWords resolved addr sz
                                    if words.Length >= 3 then
                                        let slots =
                                            words
                                            |> List.skip 2
                                            |> List.mapi (fun slotIndex targetAddr ->
                                                match Map.tryFind targetAddr byAddr with
                                                | Some targetSym ->
                                                    let qname =
                                                        Map.tryFind targetSym demangledMap
                                                        |> Option.map (callableQualifiedName >> normalize)
                                                        |> Option.defaultValue targetSym
                                                    Some (slotIndex, qname, targetSym)
                                                | None -> None)
                                            |> List.choose id
                                        if not slots.IsEmpty then Some (m, slots) else None
                                    else None
                                | _ -> None
                            else None)
                        |> Map.ofList
                if ClangToolchain.sha256 resolved <> hash then
                    Diagnostics.raiseRefusal (Diagnostics.uncoded $"Library artifact changed while inspecting '{libraryPath}'.")
                soname, paired, vtableSlots)
        { LibraryPath = libraryPath; ResolvedPath = resolved; Sha256 = hash; Soname = soname },
        paired,
        vtableSlots

    /// Inspect one ELF shared object or static archive and return its build provenance and
    /// paired (mangled, demangled) exported C++ symbols. Tool failures are loud, with no
    /// replacement tool, header-only instantiation, or synthesized wrapper.
    let inspectLibraryExports (libraryPath: string) : CppLibraryBuild * (string * string) list =
        let build, symbols, _ = inspectLibraryExportsDetailed libraryPath
        build, symbols

    /// Scope installed declarations to the HelloNappy XRT surface, preserving clang's
    /// exception attributes and measured layouts and requiring every direct ELF symbol.
    let loadXrtCppSurface (xrtIncludeDir: string) (libraryPath: string) (includeStdlibContracts: bool)
        : CppParser.Declaration list * Map<string, PilotTypes.CppClassSpec> =
        let header = System.IO.Path.Combine(xrtIncludeDir, "xrt_kernel.h")
        let refuse code message =
            Diagnostics.diagnostic code message (Diagnostics.Located.Unknown header) |> Diagnostics.refuse
        let options =
            { CppParser.defaultOptions header with
                CppMode = true; IncludeMacros = false; IncludeRoot = Some xrtIncludeDir
                IncludePaths = ["/usr/include"; xrtIncludeDir] }
        let parsed =
            match CppParser.parseHeaderFull options with
            | Ok parsed -> parsed
            | Error message -> refuse Diagnostics.CppPimplClassUnadmitted message
        let classes = buildClassMap (parsed.Declarations @ parsed.Included)
        let surface = [
            "uuid", [], "farscape_cpp_xrt_uuid_dtor", []
            "xclbin",
                [ "_ZN3xrt6xclbinC1EPK4axlf"
                  "_ZN3xrt6xclbinC1ERKSt17basic_string_viewIcSt11char_traitsIcEE"
                  "_ZN3xrt6xclbinC1ERKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEE" ],
                "farscape_cpp_xrt_xclbin_dtor", []
            "device", ["_ZN3xrt6deviceC1Ej"], "_ZN3xrt6deviceD1Ev",
                ["_ZN3xrt6device15register_xclbinERKNS_6xclbinE"]
            "hw_context", ["_ZN3xrt10hw_contextC1ERKNS_6deviceERKNS_4uuidENS0_11access_modeE"], "_ZN3xrt10hw_contextD1Ev", []
            "kernel", ["_ZN3xrt6kernelC1ERKNS_10hw_contextERKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEE"], "_ZN3xrt6kernelD1Ev",
                ["_ZNK3xrt6kernel8group_idEi"]
            "bo", ["_ZN3xrt2boC1ERKNS_6deviceEmNS0_5flagsEj"], "_ZN3xrt2boD1Ev",
                ["_ZN3xrt2bo3mapEv"; "_ZN3xrt2bo4syncE18xclBOSyncDirectionmm"; "_ZNK3xrt2bo4sizeEv"]
            "run", ["_ZN3xrt3runC1ERKNS_6kernelE"], "_ZN3xrt3runD1Ev",
                [ "_ZN3xrt3run16set_arg_at_indexEiPKvm"; "_ZN3xrt3run16set_arg_at_indexEiRKNS_2boE"
                  "_ZN3xrt3run5startEv"; "_ZNK3xrt3run4waitERKNSt6chrono8durationIlSt5ratioILl1ELl1000EEEE" ] ]
        let names = surface |> List.map (fun (name, _, _, _) -> "xrt::" + name)
        let layouts =
            match CppParser.extractStructLayoutsForModeDiagnosed header options.IncludePaths [] [] names true false with
            | Ok layouts -> layouts
            | Error error -> refuse Diagnostics.CppPimplClassUnadmitted error.Text
        let _, symbols =
            try inspectLibraryExports libraryPath
            with ex -> refuse Diagnostics.CppPimplClassUnadmitted $"Cannot inspect XRT library '{libraryPath}': {ex.Message}"
        let exported = symbols |> List.map fst |> Set.ofList
        let funcs declarations = declarations |> List.choose (function CppParser.Declaration.Function f -> Some f | _ -> None)
        let scoped =
            surface |> List.map (fun (shortName, ctorSymbols, destructor, methodSymbols) ->
                let name = "xrt::" + shortName
                let code = if shortName = "uuid" then Diagnostics.CppValueClassUnadmitted else Diagnostics.CppPimplClassUnadmitted
                let cls = classes.TryFind name |> Option.defaultWith (fun () -> refuse code $"XRT class '{name}' is missing from '{header}'.")
                let layout = layouts.TryFind name |> Option.defaultWith (fun () -> refuse code $"XRT class '{name}' has no clang record layout.")
                if layout.SizeBits <> 128 || layout.AlignmentBits > 64 || layout.AlignmentBits <= 0 then
                    refuse code $"XRT class '{name}' requires 16-byte storage with at most 8-byte alignment; clang measured {layout.SizeBits / 8} bytes and alignment {layout.AlignmentBits / 8}."
                let requireExport symbol =
                    if not (exported.Contains symbol) then
                        refuse code $"XRT class '{name}' requires symbol '{symbol}' missing from '{libraryPath}' .dynsym."
                let select declarations symbol =
                    requireExport symbol
                    funcs declarations |> List.tryFind (fun f -> f.MangledName = Some symbol)
                    |> Option.defaultWith (fun () -> refuse code $"XRT symbol '{symbol}' has no declaration in '{header}'.")
                    |> CppParser.Declaration.Function
                let ctors =
                    if shortName = "uuid" then
                        let ctor =
                            funcs cls.Constructors |> List.tryFind (fun f -> f.Parameters.IsEmpty)
                            |> Option.defaultWith (fun () -> refuse code "XRT uuid has no default constructor declaration.")
                        [ CppParser.Declaration.Function { ctor with MangledName = Some "farscape_cpp_xrt_uuid_ctor" } ]
                    else ctorSymbols |> List.map (select cls.Constructors)
                if not (destructor.StartsWith("farscape_cpp_")) then
                    requireExport destructor
                    if cls.DestructorMangledName <> Some destructor then
                        refuse code $"XRT class '{name}' has no matching complete-object destructor declaration '{destructor}'."
                CppParser.Declaration.Class
                    { cls with Constructors = ctors; Methods = methodSymbols |> List.map (select cls.Methods)
                               DestructorMangledName = Some destructor; Measured = Some layout })
        let availableSupport =
            parsed.Declarations @ parsed.Included |> List.choose (function
                | CppParser.Declaration.Enum e ->
                    let measuredEnum =
                        match e.Name, e.UnderlyingType with
                        | "flags", Some ("uint32_t" | "unsigned int") ->
                            { e with Representation = CppParser.ClangMeasured { Bits = 32; IsSigned = false } }
                        | "access_mode", Some ("uint8_t" | "unsigned char") ->
                            { e with Representation = CppParser.ClangMeasured { Bits = 8; IsSigned = false } }
                        | _ -> e
                    Some (CppParser.Declaration.Enum measuredEnum)
                | CppParser.Declaration.Typedef t -> Some (CppParser.Declaration.Typedef t)
                | CppParser.Declaration.Struct s when s.Name = "axlf" ->
                    Some (CppParser.Declaration.Struct { s with Fields = []; UnnamedBitFields = []; Measured = None })
                | CppParser.Declaration.Class c when c.Name = "axlf" ->
                    // Clang emits a C-compatible struct as CXXRecordDecl in this unit.
                    // The surface borrows axlf storage and exposes only its forward marker.
                    Some (CppParser.Declaration.Struct
                        { Name = c.Name; Fields = []; Documentation = c.Documentation; IsUnion = false
                          Attributes = []; Anonymous = None; Measured = None; Unattributed = None
                          UnnamedBitFields = []; TypedefAttributes = []; Location = c.Location })
                | _ -> None) |> List.distinct
        // Retain the referenced enum/typedef closure, including declarations reached
        // through includes. Unrelated stdlib implementation enums are not this surface.
        let identifiers (typ: string) =
            System.Text.RegularExpressions.Regex.Matches(typ, @"[A-Za-z_][A-Za-z0-9_]*")
            |> Seq.cast<System.Text.RegularExpressions.Match> |> Seq.map _.Value |> Set.ofSeq
        let usedTypes =
            scoped |> List.collect (function
                | CppParser.Declaration.Class cls ->
                    funcs (cls.Constructors @ cls.Methods)
                    |> List.collect (fun f -> f.ReturnType :: (f.Parameters |> List.map snd))
                | _ -> [])
        let rec closure used =
            let expanded =
                availableSupport |> List.fold (fun acc -> function
                    | CppParser.Declaration.Typedef t when Set.contains t.Name used -> Set.union acc (identifiers t.UnderlyingType)
                    | _ -> acc) used
            if expanded = used then used else closure expanded
        let used = usedTypes |> List.map identifiers |> List.fold Set.union Set.empty |> closure
        let support =
            availableSupport
            |> List.filter (function
                | CppParser.Declaration.Enum e -> used.Contains e.Name
                | CppParser.Declaration.Typedef t -> used.Contains t.Name
                | CppParser.Declaration.Struct _ -> true
                | _ -> false)
            |> List.distinctBy (function
                | CppParser.Declaration.Enum e -> "enum:" + e.Name
                | CppParser.Declaration.Typedef t -> "typedef:" + t.Name
                | CppParser.Declaration.Struct s -> "struct:" + s.Name
                | other -> string other)
        let contracts =
            surface |> List.collect (fun (shortName, _, _, _) ->
                let name = "xrt::" + shortName
                let kind = if shortName = "uuid" then "value" else "pimpl"
                let spec : PilotTypes.CppClassSpec =
                    { Name = name; Namespace = "Xrt"; Bind = true; KindOverride = Some kind; SizeOverride = Some 16 }
                [ name, spec; shortName, spec ]) |> Map.ofList
        let contracts =
            if includeStdlibContracts then Map.fold (fun acc key value -> Map.add key value acc) contracts defaultXrtStdlibContracts
            else contracts
        support @ scoped, contracts

    /// Inspect one ELF shared object or static archive for exported C++ template instantiations (#118).
    let inspectTemplateInventory (libraryPath: string) (declarations: CppParser.TemplateDeclaration list) =
        let build, symbols = inspectLibraryExports libraryPath
        inventoryOfSymbols build declarations symbols

    /// Resolve only an exported instantiation and return its exact build provenance.
    let requireInstantiation (inventory: CppTemplateInventory) templateName arguments source =
        let name = instantiationName templateName arguments
        match inventory.Instantiations |> List.tryFind (fun candidate -> candidate.InstantiationName = name) with
        | Some matched -> matched, inventory.Build
        | None ->
            let declaration = inventory.Declarations |> List.tryFind (fun declaration -> declaration.TemplateName = normalize templateName)
            let library =
                inventory.Build.LibraryPath
                + (inventory.Build.Soname |> Option.map (fun soname -> $" ({soname})") |> Option.defaultValue "")
            let note =
                if declaration.IsSome then " The template is declared in the header AST, but this instantiation is not exported by the binary."
                else ""
            let source =
                source
                |> Option.orElseWith (fun () -> declaration |> Option.map _.Location)
                |> Option.defaultValue (Diagnostics.Located.Unknown "No header source location was supplied for the requested template instantiation.")
            Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.CppTemplateInstantiationMissing
                $"C++ template instantiation '{name}' is not exported by library build '{library}' (SHA-256 {inventory.Build.Sha256}).{note}" source)

    /// An admitted std::vector instantiation; its representation remains native (#120).
    type CppTemplateContainerSpec = {
        TemplateName: string
        ElementType: string
        ElementClefType: string
        SafeName: string
        Namespace: string
        LibraryName: string
        FactorySymbol: string option
        Source: Diagnostics.Located<Diagnostics.SourceLocation> option
    }

    [<RequireQualifiedAccess>]
    type CppContainerOperation =
        | Acquire of handle: string * count: int
        | Size of handle: string
        | Get of handle: string * index: int
        | Iterate of handle: string * startIndex: int * count: int
        | Release of handle: string
        | ScopeExit

    let private refuseContainer (spec: CppTemplateContainerSpec) code reason =
        let source = spec.Source |> Option.defaultValue (Diagnostics.Located.Unknown "No header location was supplied for the container contract.")
        Diagnostics.refuse (Diagnostics.diagnostic code $"C++ container '{spec.SafeName}': {reason}" source)

    /// This adapter admits vector's default allocator only. It never invents a binary export.
    let validateTemplateContainerContract (inventory: CppTemplateInventory) (spec: CppTemplateContainerSpec) =
        let refuse reason = refuseContainer spec Diagnostics.CppContainerOutOfRange reason
        let identifier (text: string) =
            not (System.String.IsNullOrWhiteSpace text)
            && (System.Char.IsLetter text[0] || text[0] = '_')
            && (text |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_'))
        let rawFields = [ "_M_start"; "_M_finish"; "_M_end_of_storage"; "nativeint"; "nativeptr" ]
        let spellings = [ spec.TemplateName; spec.ElementType; spec.ElementClefType; spec.SafeName; spec.Namespace; spec.LibraryName ] @ Option.toList spec.FactorySymbol
        if spellings |> List.exists (fun text -> System.String.IsNullOrWhiteSpace text || rawFields |> List.exists text.Contains) then
            refuse "empty types and raw pointer/address fields are outside the opaque container contract."
        if not (spec.ElementType.Split([| "::" |], System.StringSplitOptions.None) |> Array.forall (fun part -> part.Split(' ') |> Array.forall identifier))
           || not (identifier spec.ElementClefType) || not (identifier spec.SafeName)
           || not (spec.Namespace.Split('.') |> Array.forall identifier)
           || (spec.FactorySymbol |> Option.exists (identifier >> not)) then
            refuse "invalid element type or binding identifier in the container operation contract."
        requireInstantiation inventory spec.TemplateName [ spec.ElementType; $"std::allocator<{spec.ElementType}>" ] spec.Source |> ignore
        if normalize spec.TemplateName <> "std::vector" || spec.ElementType = "bool" || spec.ElementType = "void" then
            refuse "only std::vector<T> with a bound value element and the default allocator is admitted."

    /// Validate one declared path; compiler-owned liveness proof remains a separate obligation.
    let verifyCppContainerLifecycle inventory spec (operations: CppContainerOperation list) =
        validateTemplateContainerContract inventory spec
        let refuse reason = refuseContainer spec Diagnostics.CppContainerOutOfRange reason
        let requireLive states handle =
            match Map.tryFind handle states with
            | Some (Some count) -> count
            | Some None -> refuseContainer spec Diagnostics.CppObjectUseAfterRelease $"handle '{handle}' is used after release."
            | None -> refuse $"handle '{handle}' has no successful acquisition."
        let requireClean states =
            match states |> Map.toList |> List.tryFind (snd >> Option.isSome) with
            | Some (handle, _) -> refuse $"handle '{handle}' has no release on this exit."
            | None -> ()
        let rec verify states = function
            | [] -> requireClean states
            | CppContainerOperation.Acquire (handle, count) :: rest ->
                if System.String.IsNullOrWhiteSpace handle || count < 0 || Map.containsKey handle states then
                    refuse $"invalid acquisition of handle '{handle}' with count {count}."
                verify (Map.add handle (Some count) states) rest
            | CppContainerOperation.Size handle :: rest ->
                requireLive states handle |> ignore
                verify states rest
            | CppContainerOperation.Get (handle, index) :: rest ->
                let size = requireLive states handle
                if index < 0 || index >= size then refuse $"index {index} is outside handle '{handle}' of size {size}."
                verify states rest
            | CppContainerOperation.Iterate (handle, startIndex, count) :: rest ->
                let size = requireLive states handle
                // Subtraction avoids overflowing startIndex + count.
                if startIndex < 0 || count < 0 || startIndex > size || count > size - startIndex then
                    refuse $"iteration ({startIndex}, {count}) is outside handle '{handle}' of size {size}."
                verify states rest
            | CppContainerOperation.Release handle :: rest ->
                match Map.tryFind handle states with
                | Some None -> refuseContainer spec Diagnostics.CppObjectDoubleRelease $"handle '{handle}' is released more than once."
                | _ -> requireLive states handle |> ignore
                verify (Map.add handle None states) rest
            | CppContainerOperation.ScopeExit :: rest ->
                requireClean states
                if not rest.IsEmpty then refuse "operations follow a terminal scope exit."
        verify Map.empty operations

    /// Companion native operations for exactly the inventory-bound vector instantiation.
    let renderCppTemplateContainerShimC (header: string) inventory (spec: CppTemplateContainerSpec) : string =
        validateTemplateContainerContract inventory spec
        let prefix = "farscape_cpp_container_" + spec.SafeName
        let container = $"{spec.TemplateName}<{spec.ElementType}, std::allocator<{spec.ElementType}>>"
        let escapedHeader = header.Replace("\\", "\\\\").Replace("\"", "\\\"")
        String.concat "\n" [
            $"#include \"{escapedHeader}\""
            "#include <vector>"
            "#include <exception>"
            "#include <new>"
            "#include <climits>"
            "#include <cstddef>"
            $"namespace {{ using Container = {container};"
            "enum { StatusOk = 0, StatusNullHandle = -1, StatusStdException = 1, StatusUnknownException = 2, StatusAllocationFailed = 3, StatusOutOfRange = 4 }; }"
            match spec.FactorySymbol with
            | Some factory ->
                $"extern \"C\" void* {prefix}_acquire() noexcept {{ try {{ return {factory}(); }} catch (...) {{ return nullptr; }} }}"
            | None -> ()
            $"extern \"C\" void {prefix}_release(void* self) noexcept {{ try {{ delete static_cast<Container*>(self); }} catch (...) {{ }} }}"
            $"extern \"C\" int {prefix}_size(int* outSize, void* self) noexcept {{"
            "  if (!outSize || !self) return StatusNullHandle;"
            "  try { auto* vec = static_cast<Container*>(self); if (vec->size() > static_cast<std::size_t>(INT_MAX)) return StatusOutOfRange; *outSize = static_cast<int>(vec->size()); return StatusOk; }"
            "  catch (const std::bad_alloc&) { return StatusAllocationFailed; } catch (const std::exception&) { return StatusStdException; } catch (...) { return StatusUnknownException; } }"
            $"extern \"C\" int {prefix}_get({spec.ElementType}* outElem, void* self, int index) noexcept {{"
            "  if (!outElem || !self) return StatusNullHandle;"
            "  auto* vec = static_cast<Container*>(self);"
            "  if (index < 0 || static_cast<std::size_t>(index) >= vec->size()) return StatusOutOfRange;"
            "  try { *outElem = (*vec)[static_cast<std::size_t>(index)]; return StatusOk; }"
            "  catch (const std::bad_alloc&) { return StatusAllocationFailed; } catch (const std::exception&) { return StatusStdException; } catch (...) { return StatusUnknownException; } }"
            $"extern \"C\" int {prefix}_iter_next({spec.ElementType}* outElem, int* inoutIndex, void* self) noexcept {{"
            "  if (!outElem || !inoutIndex || !self) return StatusNullHandle;"
            "  auto* vec = static_cast<Container*>(self);"
            "  if (*inoutIndex < 0 || *inoutIndex == INT_MAX || static_cast<std::size_t>(*inoutIndex) >= vec->size()) return StatusOutOfRange;"
            "  try { *outElem = (*vec)[static_cast<std::size_t>(*inoutIndex)]; ++*inoutIndex; return StatusOk; }"
            "  catch (const std::bad_alloc&) { return StatusAllocationFailed; } catch (const std::exception&) { return StatusStdException; } catch (...) { return StatusUnknownException; } }"
            ""
        ]

    // =========================================================================
    // Fidelity.Platform and BAREWire Zero-Copy Shared Buffers (#123)
    // =========================================================================

    type StructLayout = CppParser.StructLayoutInfo

    type SharedMemorySpaceKind =
        | HostShared
        | DeviceUnified
        | FileMapped

    type SharedAccessMode =
        | SharedReadOnly
        | SharedWriteOnly
        | SharedReadWrite
        | SharedExclusiveMutable

    type SharedBufferFieldSpec = {
        Name: string
        CType: string
        ClefType: string
        ByteOffset: int
        ByteWidth: int
        IsVolatile: bool
    }

    type SharedBufferOperation =
        | MapRegion
        | ClefRead of elementIndex: int * fieldName: string
        | ClefWrite of elementIndex: int * fieldName: string
        | ShareToLibrary of mode: SharedAccessMode * elementCount: int
        | LibraryMutate of elementIndex: int
        | SyncFromLibrary of elementIndex: int
        | UnshareFromLibrary
        | UnmapRegion
        | ScopeExit

    type SharedBufferSpec = {
        StructName: string
        SafeName: string
        Namespace: string
        LibraryName: string
        HeaderFile: string
        MemorySpace: SharedMemorySpaceKind
        SpaceAccess: SharedAccessMode
        ByteOrder: string
        SpaceOffset: int64
        MeasuredSize: int
        MeasuredAlignment: int
        ElementCount: int
        RegionByteExtent: int
        RegionAlignment: int
        RegionLifetimeDepth: int
        LibraryRetentionDepth: int
        Lifecycle: SharedBufferOperation list
        VerifiedLibraryPath: string option
        Fields: SharedBufferFieldSpec list
        LibraryTransformSymbol: string option
        Source: Diagnostics.Located<Diagnostics.SourceLocation> option
    }

    type SharedBufferDerivedPlacementFacts = {
        MappedDepth: int
        FirstUseDepth: int
        LastUseDepth: int
        LibraryRetentionEndDepth: int
        UnmapDepth: int
        LeakedAtExit: bool
        ConcurrentClefMutation: bool
        LibraryMutatesBuffer: bool
        OverlappingMutableShare: bool
        MaxAccessedElementIndex: int
        HasVerifiedExports: bool
    }

    let private refuseSharedBuffer (spec: SharedBufferSpec) code reason =
        let source = spec.Source |> Option.defaultValue (Diagnostics.Located.Unknown "No header location was supplied for the shared buffer contract.")
        Diagnostics.refuse (Diagnostics.diagnostic code $"Shared buffer '{spec.SafeName}': {reason}" source)

    let private verifySharedBufferLifecycleCore (spec: SharedBufferSpec) (ops: SharedBufferOperation list) : int * int * int * int * int * int * bool =
        let fieldNames = spec.Fields |> List.map _.Name |> Set.ofList
        let requireField fieldName =
            if not (Set.contains fieldName fieldNames) then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"field '{fieldName}' is not declared in the measured BAREWire descriptor for '{spec.StructName}'."
        let requireInBounds opName index limitCount =
            if index < 0 || index >= limitCount || index >= spec.ElementCount then
                refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                    $"{opName} element index {index} is out of bounds for shared buffer of {limitCount} elements."
            let endByte = int64 (index + 1) * int64 spec.MeasuredSize
            if endByte > int64 spec.RegionByteExtent then
                refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                    $"{opName} byte span {endByte} exceeds region byte extent {spec.RegionByteExtent}."
        let requireCleanExit (mapped: bool) (activeShare: (SharedAccessMode * int) option) =
            if activeShare.IsSome then
                refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid
                    "shared buffer region exits scope while the targeted library still retains an active shared view."
            if mapped then
                refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid
                    "shared buffer region is leaked at scope exit without UnmapRegion."
        let recordUse (step: int) (idx: int) (firstUse: int option) (lastUse: int option) (maxIdx: int option) =
            let firstUse' = Some (match firstUse with Some d -> min d step | None -> step)
            let lastUse' = Some (match lastUse with Some d -> max d step | None -> step)
            let maxIdx' = Some (match maxIdx with Some m -> max m idx | None -> idx)
            firstUse', lastUse', maxIdx'
        let rec loop
            (step: int)
            (mapped: bool)
            (everMapped: bool)
            (activeShare: (SharedAccessMode * int) option)
            (mappedDepth: int option)
            (firstUseDepth: int option)
            (lastUseDepth: int option)
            (lastUnshareDepth: int option)
            (unmapDepth: int option)
            (maxElemIdx: int option)
            (libMutates: bool)
            (remaining: SharedBufferOperation list) =
            let requireMapped opName =
                if not mapped then
                    if everMapped then
                        refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid $"{opName} after UnmapRegion is forbidden."
                    else
                        refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid $"{opName} before MapRegion is forbidden."
            match remaining with
            | [] ->
                requireCleanExit mapped activeShare
                match mappedDepth, firstUseDepth, lastUseDepth, lastUnshareDepth, unmapDepth, maxElemIdx with
                | Some mDepth, Some fDepth, Some lDepth, Some retentionEnd, Some uDepth, Some maxIdx ->
                    if mDepth < 0 || mDepth > fDepth || fDepth > lDepth || lDepth > retentionEnd || retentionEnd >= uDepth then
                        refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid
                            $"shared buffer lifecycle step ordering (MappedDepth={mDepth}, FirstUseDepth={fDepth}, LastUseDepth={lDepth}, LibraryRetentionEndDepth={retentionEnd}, UnmapDepth={uDepth}) violates 0 <= MappedDepth <= FirstUseDepth <= LastUseDepth <= LibraryRetentionEndDepth < UnmapDepth."
                    mDepth, fDepth, lDepth, retentionEnd, uDepth, maxIdx, libMutates
                | _ ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid
                        "shared buffer lifecycle must map the region, access and share at least one element window in bounds, unshare, and unmap the region before scope exit."
            | SharedBufferOperation.MapRegion :: rest ->
                if mapped || everMapped then
                    refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid "shared buffer region cannot be mapped more than once in the same lifecycle."
                loop (step + 1) true true None (Some step) firstUseDepth lastUseDepth lastUnshareDepth unmapDepth maxElemIdx libMutates rest
            | SharedBufferOperation.ClefRead (index, fieldName) :: rest ->
                requireMapped "ClefRead"
                if spec.SpaceAccess = SharedWriteOnly then
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "ClefRead is forbidden on a SharedWriteOnly memory space placement."
                match activeShare with
                | Some ((SharedReadWrite | SharedExclusiveMutable | SharedWriteOnly), _) ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "ClefRead conflicts with an active mutable library share."
                | Some (SharedReadOnly, _) | None -> ()
                requireField fieldName
                requireInBounds "ClefRead" index spec.ElementCount
                let fUse, lUse, mIdx = recordUse step index firstUseDepth lastUseDepth maxElemIdx
                loop (step + 1) mapped everMapped activeShare mappedDepth fUse lUse lastUnshareDepth unmapDepth mIdx libMutates rest
            | SharedBufferOperation.ClefWrite (index, fieldName) :: rest ->
                requireMapped "ClefWrite"
                if spec.SpaceAccess = SharedReadOnly then
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "ClefWrite is forbidden on a SharedReadOnly memory space placement."
                match activeShare with
                | Some _ ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "ClefWrite is forbidden while the targeted library holds an active shared view."
                | None -> ()
                requireField fieldName
                requireInBounds "ClefWrite" index spec.ElementCount
                let fUse, lUse, mIdx = recordUse step index firstUseDepth lastUseDepth maxElemIdx
                loop (step + 1) mapped everMapped activeShare mappedDepth fUse lUse lastUnshareDepth unmapDepth mIdx libMutates rest
            | SharedBufferOperation.ShareToLibrary (mode, count) :: rest ->
                requireMapped "ShareToLibrary"
                if activeShare.IsSome then
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "ShareToLibrary is forbidden while the targeted library already holds an active shared view."
                match spec.SpaceAccess, mode with
                | SharedReadOnly, (SharedWriteOnly | SharedReadWrite | SharedExclusiveMutable) ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "requested mutable library share mode exceeds SharedReadOnly memory space placement."
                | SharedWriteOnly, (SharedReadOnly | SharedReadWrite | SharedExclusiveMutable) ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "requested library share mode conflicts with SharedWriteOnly memory space placement."
                | _ -> ()
                if count <= 0 || count > spec.ElementCount then
                    refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                        $"ShareToLibrary element count {count} is out of bounds for capacity {spec.ElementCount}."
                let shareBytes = int64 count * int64 spec.MeasuredSize
                if shareBytes > int64 spec.RegionByteExtent then
                    refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                        $"ShareToLibrary byte extent {shareBytes} exceeds region byte extent {spec.RegionByteExtent}."
                let fUse, lUse, mIdx = recordUse step (count - 1) firstUseDepth lastUseDepth maxElemIdx
                loop (step + 1) mapped everMapped (Some (mode, count)) mappedDepth fUse lUse lastUnshareDepth unmapDepth mIdx libMutates rest
            | SharedBufferOperation.LibraryMutate index :: rest ->
                requireMapped "LibraryMutate"
                if spec.SpaceAccess = SharedReadOnly then
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "LibraryMutate is forbidden on a SharedReadOnly memory space placement."
                match activeShare with
                | None ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "LibraryMutate requires an active mutable library share."
                | Some (SharedReadOnly, _) ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "LibraryMutate is forbidden while the library holds a SharedReadOnly view."
                | Some ((SharedWriteOnly | SharedReadWrite | SharedExclusiveMutable), sharedCount) ->
                    requireInBounds "LibraryMutate" index sharedCount
                    let fUse, lUse, mIdx = recordUse step index firstUseDepth lastUseDepth maxElemIdx
                    loop (step + 1) mapped everMapped activeShare mappedDepth fUse lUse lastUnshareDepth unmapDepth mIdx true rest
            | SharedBufferOperation.SyncFromLibrary index :: rest ->
                requireMapped "SyncFromLibrary"
                if spec.SpaceAccess = SharedWriteOnly then
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "SyncFromLibrary is forbidden on a SharedWriteOnly memory space placement."
                match activeShare with
                | Some (SharedExclusiveMutable, _) ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess "SyncFromLibrary is forbidden while the library holds a SharedExclusiveMutable view."
                | Some (_, sharedCount) ->
                    requireInBounds "SyncFromLibrary" index sharedCount
                | None ->
                    requireInBounds "SyncFromLibrary" index spec.ElementCount
                let fUse, lUse, mIdx = recordUse step index firstUseDepth lastUseDepth maxElemIdx
                loop (step + 1) mapped everMapped activeShare mappedDepth fUse lUse lastUnshareDepth unmapDepth mIdx libMutates rest
            | SharedBufferOperation.UnshareFromLibrary :: rest ->
                requireMapped "UnshareFromLibrary"
                match activeShare with
                | None ->
                    refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid "UnshareFromLibrary called when no library share is active."
                | Some _ ->
                    loop (step + 1) mapped everMapped None mappedDepth firstUseDepth lastUseDepth (Some step) unmapDepth maxElemIdx libMutates rest
            | SharedBufferOperation.UnmapRegion :: rest ->
                requireMapped "UnmapRegion"
                if activeShare.IsSome then
                    refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid "UnmapRegion is forbidden while the targeted library still retains an active shared view."
                loop (step + 1) false everMapped None mappedDepth firstUseDepth lastUseDepth lastUnshareDepth (Some step) maxElemIdx libMutates rest
            | SharedBufferOperation.ScopeExit :: rest ->
                requireCleanExit mapped activeShare
                if not rest.IsEmpty then
                    refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid "operations follow a terminal ScopeExit."
                loop (step + 1) mapped everMapped activeShare mappedDepth firstUseDepth lastUseDepth lastUnshareDepth unmapDepth maxElemIdx libMutates rest
        loop 1 false false None None None None None None None false ops

    let private validateSharedBufferSpecShape (spec: SharedBufferSpec) =
        let identifier (text: string) =
            not (System.String.IsNullOrWhiteSpace text)
            && (System.Char.IsLetter text[0] || text[0] = '_')
            && (text |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_'))
        let isPowerOfTwo (n: int) = n > 0 && (n &&& (n - 1)) = 0
        let rawTokens = [ "nativeint"; "nativeptr"; "*"; "&"; "Ptr<" ]
        let topStrings = [ spec.StructName; spec.SafeName; spec.Namespace; spec.LibraryName; spec.HeaderFile; spec.ByteOrder ] @ Option.toList spec.LibraryTransformSymbol
        if topStrings |> List.exists (fun s -> System.String.IsNullOrWhiteSpace s || rawTokens |> List.exists s.Contains) then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid "empty names and raw pointer/address tokens are forbidden in shared buffer contracts."
        if not (identifier spec.StructName)
           || not (identifier spec.SafeName)
           || not (spec.Namespace.Split('.') |> Array.forall identifier)
           || (spec.LibraryTransformSymbol |> Option.exists (identifier >> not)) then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid "invalid struct, module, or symbol identifier in shared buffer contract."
        if spec.Fields.IsEmpty then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid "shared buffer descriptor must declare at least one measured field."
        let fieldNames = spec.Fields |> List.map _.Name
        if (fieldNames |> List.distinct).Length <> fieldNames.Length then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid "shared buffer descriptor contains duplicate field names."
        for field in spec.Fields do
            if [ field.Name; field.CType; field.ClefType ] |> List.exists (fun s -> System.String.IsNullOrWhiteSpace s || rawTokens |> List.exists s.Contains) then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid $"field '{field.Name}' uses an empty spelling or forbidden raw pointer/address token."
            if not (identifier field.Name) || not (identifier field.ClefType) then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid $"field '{field.Name}' has an invalid identifier or Clef type '{field.ClefType}'."
            if field.ByteOffset < 0 || field.ByteWidth <= 0 then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid $"field '{field.Name}' has invalid offset {field.ByteOffset} or width {field.ByteWidth}."
        if spec.MeasuredSize <= 0 then
            refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid $"measured struct size {spec.MeasuredSize} must be positive."
        if not (isPowerOfTwo spec.MeasuredAlignment)
           || not (isPowerOfTwo spec.RegionAlignment)
           || spec.RegionAlignment < spec.MeasuredAlignment
           || spec.RegionAlignment > 4096
           || spec.MeasuredSize % spec.MeasuredAlignment <> 0
           || spec.RegionAlignment % spec.MeasuredAlignment <> 0 then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"Fidelity.Platform placement alignment {spec.RegionAlignment} is incompatible with measured struct size {spec.MeasuredSize} and alignment {spec.MeasuredAlignment} (both alignments must be powers of two with MeasuredAlignment <= RegionAlignment <= 4096 and MeasuredSize divisible by MeasuredAlignment)."
        if spec.SpaceOffset < 0L || spec.SpaceOffset % int64 spec.RegionAlignment <> 0L then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"shared buffer space offset {spec.SpaceOffset} must be non-negative and aligned to RegionAlignment ({spec.RegionAlignment})."
        if spec.ByteOrder <> "LittleEndian" && spec.ByteOrder <> "little" then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"shared buffer byte order '{spec.ByteOrder}' mismatches the targeted library build byte-order contract ('LittleEndian' / 'little')."
        for field in spec.Fields do
            if not (isPowerOfTwo field.ByteWidth) || field.ByteWidth > 8 || field.ByteOffset % field.ByteWidth <> 0 || spec.MeasuredAlignment < field.ByteWidth then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"field '{field.Name}' at offset {field.ByteOffset} (width {field.ByteWidth}) violates scalar field alignment against struct alignment {spec.MeasuredAlignment}."
            if int64 field.ByteOffset + int64 field.ByteWidth > int64 spec.MeasuredSize then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"field '{field.Name}' at offset {field.ByteOffset} (width {field.ByteWidth}) exceeds measured struct size {spec.MeasuredSize}."
        let sortedFields = spec.Fields |> List.sortBy _.ByteOffset
        for a, b in List.pairwise sortedFields do
            if int64 a.ByteOffset + int64 a.ByteWidth > int64 b.ByteOffset then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"fields '{a.Name}' and '{b.Name}' overlap in shared buffer layout."
        if spec.ElementCount <= 0 || spec.RegionByteExtent <= 0 then
            refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                $"shared buffer element count ({spec.ElementCount}) and region byte extent ({spec.RegionByteExtent}) must be positive."
        let requiredBytes = int64 spec.ElementCount * int64 spec.MeasuredSize
        if requiredBytes > int64 System.Int32.MaxValue then
            refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                $"shared buffer required byte extent ({spec.ElementCount} * {spec.MeasuredSize}) overflows 32-bit descriptor bounds."
        if int64 spec.RegionByteExtent < requiredBytes then
            refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                $"shared buffer region byte extent {spec.RegionByteExtent} is smaller than required extent {requiredBytes} ({spec.ElementCount} * {spec.MeasuredSize} bytes)."
        if spec.SpaceOffset > System.Int64.MaxValue - int64 spec.RegionByteExtent then
            refuseSharedBuffer spec Diagnostics.SharedBufferExtentInvalid
                $"shared buffer space offset {spec.SpaceOffset} plus region byte extent {spec.RegionByteExtent} overflows 64-bit address bounds."
        if spec.RegionLifetimeDepth <= 0 || spec.LibraryRetentionDepth <= 0 || spec.RegionLifetimeDepth > spec.LibraryRetentionDepth then
            refuseSharedBuffer spec Diagnostics.SharedBufferLifetimeInvalid
                $"Fidelity.Platform region lifetime depth {spec.RegionLifetimeDepth} and library retention depth {spec.LibraryRetentionDepth} must both be positive with RegionLifetimeDepth <= LibraryRetentionDepth."
        if spec.SpaceAccess = SharedReadOnly && spec.LibraryTransformSymbol.IsSome then
            refuseSharedBuffer spec Diagnostics.SharedBufferConflictingAccess
                "SharedReadOnly memory space placement cannot declare a mutating LibraryTransformSymbol."
        let lifecycleFacts = verifySharedBufferLifecycleCore spec spec.Lifecycle
        let _, _, _, _, _, _, libMutates = lifecycleFacts
        let hasForeignCall =
            libMutates
            || (spec.Lifecycle |> List.exists (function SharedBufferOperation.SyncFromLibrary _ -> true | _ -> false))
        if (libMutates && spec.LibraryTransformSymbol.IsNone) || (not hasForeignCall && spec.LibraryTransformSymbol.IsSome) then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"shared buffer lifecycle mutation evidence (LibraryMutate={libMutates}) disagrees with LibraryTransformSymbol ({spec.LibraryTransformSymbol.IsSome})."
        lifecycleFacts

    let private validateSharedBufferContractCore (measuredStructs: Map<string, StructLayout>) (spec: SharedBufferSpec) =
        let lifecycleFacts = validateSharedBufferSpecShape spec
        let measured =
            match Map.tryFind spec.StructName measuredStructs with
            | Some m -> m
            | None ->
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"struct '{spec.StructName}' has no clang measured layout in the targeted library build."
        if measured.SizeBits <= 0 || measured.SizeBits % 8 <> 0 || measured.AlignmentBits <= 0 || measured.AlignmentBits % 8 <> 0 then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"clang measured layout for '{spec.StructName}' is not whole-byte aligned ({measured.SizeBits} bits, alignment {measured.AlignmentBits} bits)."
        let actualSize = measured.SizeBits / 8
        let actualAlignment = measured.AlignmentBits / 8
        if spec.MeasuredSize <> actualSize || spec.MeasuredAlignment <> actualAlignment then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"descriptor layout ({spec.MeasuredSize} bytes, alignment {spec.MeasuredAlignment}) mismatches clang measured build layout ({actualSize} bytes, alignment {actualAlignment}) for '{spec.StructName}'."
        if spec.Fields.Length <> measured.FieldOffsetsBits.Length then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"descriptor declares {spec.Fields.Length} fields for '{spec.StructName}', but clang measured {measured.FieldOffsetsBits.Length} field offsets."
        for field, offsetBits in List.zip spec.Fields measured.FieldOffsetsBits do
            if offsetBits < 0 || offsetBits % 8 <> 0 || field.ByteOffset <> offsetBits / 8 then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"field '{field.Name}' offset {field.ByteOffset} mismatches clang measured offset {offsetBits / 8} bytes in '{spec.StructName}'."
        lifecycleFacts

    /// Validate a BAREWire and Fidelity.Platform shared-buffer contract against clang's measured struct layouts (#123).
    let validateSharedBufferContract (measuredStructs: Map<string, StructLayout>) (spec: SharedBufferSpec) =
        validateSharedBufferContractCore measuredStructs spec |> ignore

    /// Verify shared-buffer lifecycle, extent bounds, retention lifetime, and access exclusivity (#123).
    let verifySharedBufferLifecycle (measuredStructs: Map<string, StructLayout>) (spec: SharedBufferSpec) (ops: SharedBufferOperation list) =
        validateSharedBufferContractCore measuredStructs spec |> ignore
        verifySharedBufferLifecycleCore spec ops |> ignore

    /// Deterministic contract-fingerprint symbol binding a compiled C++ zero-copy shim to its SharedBufferSpec and Lifecycle (#205).
    let sharedBufferContractSymbol (spec: SharedBufferSpec) : string =
        let spaceTag =
            match spec.MemorySpace with
            | HostShared -> "HostShared"
            | DeviceUnified -> "DeviceUnified"
            | FileMapped -> "FileMapped"
        let accessTag =
            match spec.SpaceAccess with
            | SharedReadOnly -> "SharedReadOnly"
            | SharedWriteOnly -> "SharedWriteOnly"
            | SharedReadWrite -> "SharedReadWrite"
            | SharedExclusiveMutable -> "SharedExclusiveMutable"
        let formatAccess = function
            | SharedReadOnly -> "RO"
            | SharedWriteOnly -> "WO"
            | SharedReadWrite -> "RW"
            | SharedExclusiveMutable -> "EM"
        let byteOrderTag =
            if spec.ByteOrder = "LittleEndian" || spec.ByteOrder = "little" then "little" else spec.ByteOrder
        let fieldsPayload =
            spec.Fields
            |> List.map (fun f ->
                let normalizedClefType = if f.ClefType = "uint" then "int" else f.ClefType
                $"{f.Name}:{f.CType}:{normalizedClefType}:{f.ByteOffset}:{f.ByteWidth}:{f.IsVolatile}")
            |> String.concat ";"
        let lifecyclePayload =
            spec.Lifecycle
            |> List.map (function
                | SharedBufferOperation.MapRegion -> "Map"
                | SharedBufferOperation.ClefRead (idx, fld) -> $"R({idx},{fld})"
                | SharedBufferOperation.ClefWrite (idx, fld) -> $"W({idx},{fld})"
                | SharedBufferOperation.ShareToLibrary (m, cnt) -> $"S({formatAccess m},{cnt})"
                | SharedBufferOperation.LibraryMutate idx -> $"M({idx})"
                | SharedBufferOperation.SyncFromLibrary idx -> $"Y({idx})"
                | SharedBufferOperation.UnshareFromLibrary -> "U"
                | SharedBufferOperation.UnmapRegion -> "X"
                | SharedBufferOperation.ScopeExit -> "E")
            |> String.concat ";"
        let transformTag = Option.defaultValue "" spec.LibraryTransformSymbol
        let payload =
            $"{spec.StructName}|{spec.SafeName}|{spec.Namespace}|{spec.LibraryName}|{spaceTag}|{accessTag}|{byteOrderTag}|{spec.SpaceOffset}|{spec.MeasuredSize}|{spec.MeasuredAlignment}|{spec.ElementCount}|{spec.RegionByteExtent}|{spec.RegionAlignment}|{spec.RegionLifetimeDepth}|{spec.LibraryRetentionDepth}|{fieldsPayload}|{lifecyclePayload}|{transformTag}"
        let hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes payload)
        let hex16 = System.Convert.ToHexStringLower(hashBytes).[0 .. 15]
        $"farscape_shared_buffer_{spec.SafeName}_contract_{hex16}"

    /// Expected exported C symbols for a shared buffer contract (#123).
    let sharedBufferExportedSymbols (spec: SharedBufferSpec) : string list =
        let prefix = "farscape_shared_buffer_" + spec.SafeName
        let core =
            [ prefix + "_map"
              prefix + "_unmap"
              prefix + "_share_to_library"
              prefix + "_unshare_from_library" ]
            @ (if spec.LibraryTransformSymbol.IsSome then [ prefix + "_library_transform_in_place" ] else [])
            @ [ prefix + "_clef_address_identity"
                prefix + "_library_address_identity"
                prefix + "_copy_count"
                prefix + "_memory_space_kind"
                sharedBufferContractSymbol spec ]
        let fields =
            spec.Fields |> List.collect (fun f ->
                (if spec.SpaceAccess <> SharedWriteOnly then [ prefix + "_clef_read_" + f.Name ] else [])
                @ (if spec.SpaceAccess <> SharedReadOnly then [ prefix + "_clef_write_" + f.Name ] else []))
        core @ fields @ Option.toList spec.LibraryTransformSymbol

    let private validateSharedBufferLibraryExportsCoreWithTool (toolOverride: string option) (libraryPath: string) (spec: SharedBufferSpec) =
        if System.String.IsNullOrWhiteSpace libraryPath || not (System.IO.File.Exists libraryPath) then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"verified shared-buffer library path '{libraryPath}' does not exist."
        let resolved, sha256, archive =
            try
                let file = System.IO.FileInfo(System.IO.Path.GetFullPath libraryPath)
                let res =
                    match file.ResolveLinkTarget true with
                    | null -> file.FullName
                    | target -> target.FullName
                let hash = ClangToolchain.sha256 res
                use stream = System.IO.File.OpenRead res
                let magic = Array.zeroCreate<byte> 8
                let readBytes = stream.Read(magic, 0, 8)
                if readBytes < 8 then
                    refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                        $"verified shared-buffer library '{res}' is truncated ({readBytes} bytes)."
                let isArch = List.contains (System.Text.Encoding.ASCII.GetString magic) ["!<arch>\n"; "!<thin>\n"]
                let isElf = magic.[0] = 0x7Fuy && magic.[1] = 0x45uy && magic.[2] = 0x4Cuy && magic.[3] = 0x46uy
                if not isArch && not isElf then
                    refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                        $"verified shared-buffer library '{res}' is neither an ELF binary nor a static archive."
                res, hash, isArch
            with
            | :? System.IO.IOException
            | :? System.UnauthorizedAccessException as ex ->
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"verified shared-buffer library '{libraryPath}' could not be read ({ex.Message})."
        let runInspectionTool defaultTool arguments =
            let tool = toolOverride |> Option.defaultValue defaultTool
            match tryRunSymbolTool tool arguments None with
            | Ok output -> output
            | Error refusal ->
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"verified shared-buffer library '{resolved}' symbol inspection failed: {refusal.Text}"
        let lines (text: string) = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
        let exported =
            if archive then
                match toolOverride with
                | None ->
                    match ArchiveInspection.inspect resolved (Some sha256) with
                    | Ok provenance -> provenance.Symbols |> Map.keys |> Set.ofSeq
                    | Error refusal ->
                        refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                            $"verified shared-buffer archive '{resolved}' inspection failed: {refusal.Text}"
                | Some _ ->
                    let output = runInspectionTool "nm" ["--extern-only"; "--defined-only"; "--format=posix"; "--"; resolved]
                    lines output
                    |> List.choose (fun line ->
                        let columns = line.Split([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
                        if columns.Length >= 2 then Some columns.[0] else None)
                    |> Set.ofList
            else
                let output = runInspectionTool "readelf" ["--dyn-syms"; "--wide"; "--"; resolved]
                lines output
                |> List.choose (fun line ->
                    let columns = line.Split([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
                    if columns.Length >= 8
                       && List.contains columns.[4] ["GLOBAL"; "WEAK"; "UNIQUE"]
                       && List.contains columns.[5] ["DEFAULT"; "PROTECTED"]
                       && columns.[6] <> "UND" then
                        Some (columns.[7].Split('@').[0])
                    else None)
                |> Set.ofList
        for sym in sharedBufferExportedSymbols spec do
            if not (Set.contains sym exported) then
                refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                    $"required shared-buffer symbol '{sym}' is not exported by library build '{resolved}' (SHA-256 {sha256})."
        let fileName = System.IO.Path.GetFileName resolved
        let expectedShared = $"lib{spec.LibraryName}.so"
        let expectedStatic = $"lib{spec.LibraryName}.a"
        let isVersionedShared =
            if fileName.StartsWith(expectedShared + ".", System.StringComparison.Ordinal) then
                let suffix = fileName.Substring(expectedShared.Length + 1)
                suffix.Length > 0
                && (suffix.Split('.') |> Array.forall (fun seg -> seg.Length > 0 && seg |> Seq.forall System.Char.IsAsciiDigit))
            else
                false
        if fileName <> expectedShared
           && fileName <> expectedStatic
           && not isVersionedShared then
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                $"verified library artifact '{fileName}' does not match declared LibraryName '{spec.LibraryName}' (expected '{expectedShared}' or '{expectedStatic}')."

    let private validateSharedBufferLibraryExportsCore (libraryPath: string) (spec: SharedBufferSpec) =
        validateSharedBufferLibraryExportsCoreWithTool None libraryPath spec

    /// Validate that a compiled target library binary exports every symbol required by a shared-buffer contract (#123).
    let validateSharedBufferLibraryExports (libraryPath: string) (measuredStructs: Map<string, StructLayout>) (spec: SharedBufferSpec) =
        validateSharedBufferContractCore measuredStructs spec |> ignore
        validateSharedBufferLibraryExportsCore libraryPath spec

    /// Validate shared-buffer library exports using an explicit symbol-inspection tool path.
    let validateSharedBufferLibraryExportsWithTool (toolPath: string) (libraryPath: string) (measuredStructs: Map<string, StructLayout>) (spec: SharedBufferSpec) =
        validateSharedBufferContractCore measuredStructs spec |> ignore
        validateSharedBufferLibraryExportsCoreWithTool (Some toolPath) libraryPath spec

    /// Derive verified BAREWire SharedBufferPlacement execution-sequence depths, element bounds,
    /// mutation/exclusivity evidence, and symbol export proof from the contract's Lifecycle and VerifiedLibraryPath (#205).
    let deriveSharedBufferPlacementFacts (measuredStructs: Map<string, StructLayout>) (spec: SharedBufferSpec) : SharedBufferDerivedPlacementFacts =
        let mDepth, fDepth, lDepth, retentionEnd, uDepth, maxIdx, libMutates =
            validateSharedBufferContractCore measuredStructs spec
        match spec.VerifiedLibraryPath with
        | Some libraryPath ->
            validateSharedBufferLibraryExportsCore libraryPath spec
        | None ->
            refuseSharedBuffer spec Diagnostics.SharedBufferContractInvalid
                "shared buffer placement requires a compiled target library binary (VerifiedLibraryPath) with verified FFI exports."
        // verifySharedBufferLifecycleCore above has already refused any lifecycle with leaked regions,
        // concurrent Clef mutation during sharing, or overlapping mutable shares.
        { MappedDepth = mDepth
          FirstUseDepth = fDepth
          LastUseDepth = lDepth
          LibraryRetentionEndDepth = retentionEnd
          UnmapDepth = uDepth
          LeakedAtExit = false
          ConcurrentClefMutation = false
          LibraryMutatesBuffer = libMutates
          OverlappingMutableShare = false
          MaxAccessedElementIndex = maxIdx
          HasVerifiedExports = true }

    /// Render the companion extern "C" C++ zero-copy shim for a BAREWire / Fidelity.Platform shared buffer (#123).
    let renderSharedBufferZeroCopyShimC (spec: SharedBufferSpec) : string =
        let _, _, _, _, _, maxAccessedIdx, libMutates = validateSharedBufferSpecShape spec
        let prefix = "farscape_shared_buffer_" + spec.SafeName
        let escapedHeader = spec.HeaderFile.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let memorySpaceCode =
            match spec.MemorySpace with
            | HostShared -> 1
            | DeviceUnified -> 2
            | FileMapped -> 3
        let spaceAccessCode =
            match spec.SpaceAccess with
            | SharedReadOnly -> 1
            | SharedWriteOnly -> 2
            | SharedReadWrite -> 3
            | SharedExclusiveMutable -> 4
        let staticAsserts =
            [ $"static_assert(sizeof(SharedElement) == {spec.MeasuredSize}, \"BAREWire measured sizeof mismatch\");"
              $"static_assert(alignof(SharedElement) == {spec.MeasuredAlignment}, \"BAREWire measured alignof mismatch\");" ]
            @ (spec.Fields |> List.collect (fun f ->
                [ $"static_assert(offsetof(SharedElement, {f.Name}) == {f.ByteOffset}, \"BAREWire measured offsetof({f.Name}) mismatch\");"
                  $"static_assert(sizeof(static_cast<SharedElement*>(nullptr)->{f.Name}) == {f.ByteWidth}, \"BAREWire measured width({f.Name}) mismatch\");" ]))
        // The C++ shim exports both read and write entrypoints for every field so native refusal probes
        // can verify runtime StatusAccessConflict enforcement even when the Clef binding surface omits disallowed accessors.
        let fieldAccessors =
            spec.Fields |> List.collect (fun f ->
                [ $"extern \"C\" int {prefix}_clef_write_{f.Name}(void* self, int index, {f.CType} value) noexcept {{"
                  "  RegionLockGuard lock;"
                  "  auto* region = find_region(self);"
                  "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                  "  if (region->space_access == AccessReadOnly || region->share_mode != AccessNone) return StatusAccessConflict;"
                  $"  if (index < 0 || index > {maxAccessedIdx} || index >= {spec.ElementCount}) return StatusOutOfRange;"
                  $"  region->elements[index].{f.Name} = value;"
                  "  region->clef_addr = reinterpret_cast<std::uintptr_t>(&region->elements[0]);"
                  "  return StatusOk;"
                  "}"
                  $"extern \"C\" int {prefix}_clef_read_{f.Name}({f.CType}* outValue, void* self, int index) noexcept {{"
                  "  if (!outValue) return StatusNullHandle;"
                  "  RegionLockGuard lock;"
                  "  auto* region = find_region(self);"
                  "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                  "  if (region->space_access == AccessWriteOnly || region->share_mode == AccessReadWrite || region->share_mode == AccessExclusiveMutable || region->share_mode == AccessWriteOnly) return StatusAccessConflict;"
                  $"  if (index < 0 || index > {maxAccessedIdx} || index >= {spec.ElementCount}) return StatusOutOfRange;"
                  $"  *outValue = region->elements[index].{f.Name};"
                  "  region->clef_addr = reinterpret_cast<std::uintptr_t>(&region->elements[0]);"
                  "  return StatusOk;"
                  "}" ])
        let transformEntrySource =
            match spec.LibraryTransformSymbol with
            | Some sym ->
                let shareCheck =
                    if libMutates then
                        "region->space_access == AccessReadOnly || (region->share_mode != AccessReadWrite && region->share_mode != AccessExclusiveMutable && region->share_mode != AccessWriteOnly)"
                    else
                        "region->share_mode == AccessNone"
                [ $"extern \"C\" int {prefix}_library_transform_in_place(void* self, int index, int delta) noexcept {{"
                  "  SharedElement* elem_base = nullptr;"
                  "  int shared_cnt = 0;"
                  "  std::uintptr_t obs_lib_addr = 0;"
                  "  int obs_copies = 0;"
                  "  {"
                  "    RegionLockGuard lock;"
                  "    auto* region = find_region(self);"
                  "    if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                  $"    if ({shareCheck}) return StatusAccessConflict;"
                  $"    if (index < 0 || index >= region->shared_count || index > {maxAccessedIdx} || index >= {spec.ElementCount}) return StatusOutOfRange;"
                  "    region->clef_addr = reinterpret_cast<std::uintptr_t>(self);"
                  "    elem_base = &region->elements[0];"
                  "    shared_cnt = region->shared_count;"
                  "    obs_lib_addr = region->library_addr;"
                  "    obs_copies = region->copy_count;"
                  "  }"
                  $"  int rc = {sym}(elem_base, shared_cnt, index, delta, &obs_lib_addr, &obs_copies);"
                  "  RegionLockGuard lock;"
                  "  auto* region = find_region(self);"
                  "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                  "  region->library_addr = obs_lib_addr;"
                  "  region->copy_count = obs_copies;"
                  "  if (rc != StatusOk) return rc;"
                  "  if (region->library_addr != region->clef_addr || region->copy_count != 0) return StatusAccessConflict;"
                  "  return StatusOk;"
                  "}" ]
            | None -> []
        let requiredSpanBytes = uint64 (spec.SpaceOffset + int64 spec.RegionByteExtent)
        let contractSymbol = sharedBufferContractSymbol spec
        let mapEntrySource =
            match spec.MemorySpace with
            | FileMapped ->
                [ $"extern \"C\" int {contractSymbol}(void* self) noexcept;"
                  $"extern \"C\" void* {prefix}_try_map(int fd) noexcept {{"
                  "  if (fd < 0) return nullptr;"
                  "  long page = ::sysconf(_SC_PAGESIZE);"
                  $"  if (page <= 0 || static_cast<std::size_t>(page) < {spec.RegionAlignment}u || (static_cast<std::size_t>(page) & ({spec.RegionAlignment}u - 1u)) != 0u) return nullptr;"
                  "  struct ::stat st{};"
                  $"  if (::fstat(fd, &st) != 0 || st.st_size < 0 || static_cast<std::uint64_t>(st.st_size) < {requiredSpanBytes}ULL) return nullptr;"
                  $"  std::size_t map_bytes = static_cast<std::size_t>({requiredSpanBytes}ULL);"
                  $"  int init_prot = ({spaceAccessCode} == AccessReadOnly) ? PROT_READ : (PROT_READ | PROT_WRITE);"
                  "  void* mem = ::mmap(nullptr, map_bytes, init_prot, MAP_SHARED, fd, 0);"
                  "  if (mem == MAP_FAILED || !mem) return nullptr;"
                  $"  auto* elem_base = reinterpret_cast<SharedElement*>(static_cast<char*>(mem) + {spec.SpaceOffset}LL);"
                  "  SharedRegionState* slot_ptr = nullptr;"
                  "  {"
                  "    RegionLockGuard lock;"
                  "    slot_ptr = alloc_region();"
                  "    if (!slot_ptr) { ::munmap(mem, map_bytes); return nullptr; }"
                  "    auto& slot = *slot_ptr;"
                  "    slot.mapped_base = mem;"
                  "    slot.elements = elem_base;"
                  "    slot.mapped_bytes = map_bytes;"
                  "    slot.backing_fd = fd;"
                  "    slot.mapped = true;"
                  $"    slot.memory_space = {memorySpaceCode};"
                  $"    slot.space_access = {spaceAccessCode};"
                  "    slot.share_mode = AccessNone;"
                  "    slot.shared_count = 0;"
                  "    slot.clef_addr = 0;"
                  "    slot.library_addr = 0;"
                  "    slot.copy_count = 0;"
                  "  }"
                  $"  if ({contractSymbol}(elem_base) != StatusOk) {{ RegionLockGuard lock; (void)::mmap(mem, map_bytes, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0); slot_ptr->mapped = false; return nullptr; }}"
                  "  return elem_base;"
                  "}"
                  $"extern \"C\" void* {prefix}_map(int fd) noexcept {{"
                  $"  void* p = {prefix}_try_map(fd);"
                  "  if (!p) ::abort();"
                  "  return p;"
                  "}" ]
            | HostShared
            | DeviceUnified ->
                [ $"extern \"C\" int {contractSymbol}(void* self) noexcept;"
                  $"extern \"C\" void* {prefix}_try_map() noexcept {{"
                  "  long page = ::sysconf(_SC_PAGESIZE);"
                  $"  if (page <= 0 || static_cast<std::size_t>(page) < {spec.RegionAlignment}u || (static_cast<std::size_t>(page) & ({spec.RegionAlignment}u - 1u)) != 0u) return nullptr;"
                  $"  std::size_t map_bytes = align_page_extent(static_cast<std::size_t>({requiredSpanBytes}ULL), {spec.RegionAlignment}u);"
                  "  if (map_bytes == 0u) return nullptr;"
                  $"  int init_prot = ({spaceAccessCode} == AccessReadOnly) ? PROT_READ : (PROT_READ | PROT_WRITE);"
                  "  void* mem = ::mmap(nullptr, map_bytes, init_prot, MAP_SHARED | MAP_ANONYMOUS, -1, 0);"
                  "  if (mem == MAP_FAILED || !mem) return nullptr;"
                  $"  auto* elem_base = reinterpret_cast<SharedElement*>(static_cast<char*>(mem) + {spec.SpaceOffset}LL);"
                  "  SharedRegionState* slot_ptr = nullptr;"
                  "  {"
                  "    RegionLockGuard lock;"
                  "    slot_ptr = alloc_region();"
                  "    if (!slot_ptr) { ::munmap(mem, map_bytes); return nullptr; }"
                  "    auto& slot = *slot_ptr;"
                  "    slot.mapped_base = mem;"
                  "    slot.elements = elem_base;"
                  "    slot.mapped_bytes = map_bytes;"
                  "    slot.backing_fd = -1;"
                  "    slot.mapped = true;"
                  $"    slot.memory_space = {memorySpaceCode};"
                  $"    slot.space_access = {spaceAccessCode};"
                  "    slot.share_mode = AccessNone;"
                  "    slot.shared_count = 0;"
                  "    slot.clef_addr = 0;"
                  "    slot.library_addr = 0;"
                  "    slot.copy_count = 0;"
                  "  }"
                  $"  if ({contractSymbol}(elem_base) != StatusOk) {{ RegionLockGuard lock; (void)::mmap(mem, map_bytes, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0); slot_ptr->mapped = false; return nullptr; }}"
                  "  return elem_base;"
                  "}"
                  $"extern \"C\" void* {prefix}_map() noexcept {{"
                  $"  void* p = {prefix}_try_map();"
                  "  if (!p) ::abort();"
                  "  return p;"
                  "}" ]
        let setLibAddrOnShare =
            if spec.LibraryTransformSymbol.IsNone then
                "  region->library_addr = reinterpret_cast<std::uintptr_t>(&region->elements[0]);"
            else
                "  // library_addr is populated by the foreign transform symbol upon in-place access."
        String.concat "\n" (
            [ $"#include \"{escapedHeader}\""
              "#include <cstddef>"
              "#include <cstdint>"
              "#include <cstdlib>"
              "#include <cstring>"
              "#include <pthread.h>"
              "#include <sys/mman.h>"
              "#include <sys/stat.h>"
              "#include <unistd.h>"
              "namespace {"
              $"using SharedElement = {spec.StructName};"
              "enum { StatusOk = 0, StatusNullHandle = -1, StatusOutOfRange = 4, StatusLifetimeError = 5, StatusAccessConflict = 6 };"
              "enum { AccessNone = 0, AccessReadOnly = 1, AccessWriteOnly = 2, AccessReadWrite = 3, AccessExclusiveMutable = 4 };"
              "enum { SpaceHostShared = 1, SpaceDeviceUnified = 2, SpaceFileMapped = 3 };" ]
            @ staticAsserts
            @ [ "struct SharedRegionState {"
                "  SharedRegionState* next;"
                "  void* mapped_base;"
                "  SharedElement* elements;"
                "  std::size_t mapped_bytes;"
                "  int backing_fd;"
                "  bool mapped;"
                "  int memory_space;"
                "  int space_access;"
                "  int share_mode;"
                "  int shared_count;"
                "  std::uintptr_t clef_addr;"
                "  std::uintptr_t library_addr;"
                "  int copy_count;"
                "};"
                "static pthread_mutex_t g_regions_mutex = PTHREAD_MUTEX_INITIALIZER;"
                "struct RegionLockGuard {"
                "  RegionLockGuard() noexcept { ::pthread_mutex_lock(&g_regions_mutex); }"
                "  ~RegionLockGuard() noexcept { ::pthread_mutex_unlock(&g_regions_mutex); }"
                "  RegionLockGuard(const RegionLockGuard&) = delete;"
                "  RegionLockGuard& operator=(const RegionLockGuard&) = delete;"
                "};"
                "static SharedRegionState* g_regions_head = nullptr;"
                "static SharedRegionState* alloc_region() noexcept {"
                "  auto* fresh = static_cast<SharedRegionState*>(::calloc(1u, sizeof(SharedRegionState)));"
                "  if (!fresh) return nullptr;"
                "  fresh->next = g_regions_head;"
                "  g_regions_head = fresh;"
                "  return fresh;"
                "}"
                "static SharedRegionState* find_region(void* self) noexcept {"
                "  if (!self) return nullptr;"
                "  for (auto* slot = g_regions_head; slot != nullptr; slot = slot->next) {"
                "    if (slot->mapped && slot->elements == self) return slot;"
                "  }"
                "  return nullptr;"
                "}"
                "[[maybe_unused]] static std::size_t align_page_extent(std::size_t bytes, std::size_t req_align) noexcept {"
                "  long page = ::sysconf(_SC_PAGESIZE);"
                "  if (page <= 0) return 0u;"
                "  std::size_t gran = static_cast<std::size_t>(page);"
                "  if (req_align > gran) gran = req_align;"
                "  return ((bytes + gran - 1u) / gran) * gran;"
                "}"
                "}" ]
            @ mapEntrySource
            @ [ $"extern \"C\" int {prefix}_unmap(void* self) noexcept {{"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  if (!region->mapped_base || region->share_mode != AccessNone) return StatusLifetimeError;"
                "  if (::mmap(region->mapped_base, region->mapped_bytes, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0) == MAP_FAILED) return StatusLifetimeError;"
                "  region->backing_fd = -1;"
                "  region->mapped = false;"
                "  return StatusOk;"
                "}" ]
            @ fieldAccessors
            @ [ $"extern \"C\" int {prefix}_share_to_library(void* self, int accessMode, int elementCount) noexcept {{"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  if (!region->mapped_base) return StatusLifetimeError;"
                "  if (region->share_mode != AccessNone) return StatusAccessConflict;"
                "  if (accessMode < AccessReadOnly || accessMode > AccessExclusiveMutable) return StatusAccessConflict;"
                "  if (region->space_access == AccessReadOnly && accessMode != AccessReadOnly) return StatusAccessConflict;"
                "  if (region->space_access == AccessWriteOnly && accessMode != AccessWriteOnly) return StatusAccessConflict;"
                $"  if (elementCount <= 0 || elementCount > {spec.ElementCount} || (elementCount - 1) > {maxAccessedIdx} || static_cast<std::size_t>(elementCount) * sizeof(SharedElement) > {spec.RegionByteExtent}u) return StatusOutOfRange;"
                "  if (accessMode == AccessReadOnly && ::mprotect(region->mapped_base, region->mapped_bytes, PROT_READ) != 0) return StatusAccessConflict;"
                "  region->share_mode = accessMode;"
                "  region->shared_count = elementCount;"
                "  region->clef_addr = reinterpret_cast<std::uintptr_t>(self);"
                setLibAddrOnShare
                "  return StatusOk;"
                "}"
                $"extern \"C\" int {prefix}_unshare_from_library(void* self) noexcept {{"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  if (!region->mapped_base || region->share_mode == AccessNone) return StatusLifetimeError;"
                "  int restore_prot = (region->space_access == AccessReadOnly) ? PROT_READ : (PROT_READ | PROT_WRITE);"
                "  if (::mprotect(region->mapped_base, region->mapped_bytes, restore_prot) != 0) return StatusAccessConflict;"
                "  region->clef_addr = reinterpret_cast<std::uintptr_t>(self);"
                "  region->share_mode = AccessNone;"
                "  region->shared_count = 0;"
                "  return StatusOk;"
                "}" ]
            @ transformEntrySource
            @ [ $"extern \"C\" int {prefix}_clef_address_identity(std::uintptr_t* outAddr, void* self) noexcept {{"
                "  if (!outAddr) return StatusNullHandle;"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  *outAddr = region->clef_addr;"
                "  return StatusOk;"
                "}"
                $"extern \"C\" int {prefix}_library_address_identity(std::uintptr_t* outAddr, void* self) noexcept {{"
                "  if (!outAddr) return StatusNullHandle;"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  *outAddr = region->library_addr;"
                "  return StatusOk;"
                "}"
                $"extern \"C\" int {prefix}_copy_count(int* outCopies, void* self) noexcept {{"
                "  if (!outCopies) return StatusNullHandle;"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  *outCopies = region->copy_count;"
                "  return StatusOk;"
                "}"
                $"extern \"C\" int {prefix}_memory_space_kind(int* outSpaceKind, void* self) noexcept {{"
                "  if (!outSpaceKind) return StatusNullHandle;"
                "  RegionLockGuard lock;"
                "  auto* region = find_region(self);"
                "  if (!region) return self ? StatusLifetimeError : StatusNullHandle;"
                "  *outSpaceKind = region->memory_space;"
                "  return StatusOk;"
                "}"
                $"extern \"C\" int {contractSymbol}(void* self) noexcept {{ RegionLockGuard lock; return find_region(self) ? StatusOk : (self ? StatusLifetimeError : StatusNullHandle); }}"
                "" ])

    // =========================================================================
    // Plugify C++ ABI Facts with Provenance (#119)
    // =========================================================================

    /// Tool identity for Plugify C++ ABI fact extraction (#119).
    type CppAbiFactToolProvenance = {
        ToolName: string
        ToolVersion: string
        ManifestSource: string
    }

    /// Complete tool and library-build provenance carried by every recorded C++ ABI fact (#119).
    type CppAbiFactProvenance = {
        Tool: CppAbiFactToolProvenance
        Build: CppLibraryBuild
    }

    /// ABI facts state call mechanics only; liveness, extent, and ownership obligations are
    /// always discharged on the PSG (#119).
    type ProofAuthority =
        | CallMechanicsOnly_PsgDischargesProof
        member _.DischargesProofObligations = false

    type VirtualTableAbiFact = {
        ClassName: string
        VtableSymbol: string option
        TypeinfoSymbol: string option
        /// (slotIndex, methodName, mangledSymbol)
        VirtualMethods: (int * string * string) list
    }

    type TemplateInstanceAbiFact = {
        InstantiationName: string
        TemplateName: string
        TemplateArguments: string list
        MangledSymbols: string list
    }

    type RaiiAbiFact = {
        ClassName: string
        ConstructorSymbols: string list
        DestructorSymbol: string
        StorageSizeBytes: int option
        StorageAlignBytes: int option
    }

    type ExceptionBoundaryAbiFact = {
        CallableName: string
        MangledSymbol: string
        IsNoExcept: bool
    }

    type CppAbiFactKind =
        | VirtualTableFact of VirtualTableAbiFact
        | TemplateInstanceFact of TemplateInstanceAbiFact
        | RaiiFact of RaiiAbiFact
        | ExceptionBoundaryFact of ExceptionBoundaryAbiFact

    type CppRecordedAbiFact = {
        Fact: CppAbiFactKind
        Provenance: CppAbiFactProvenance
        ProofAuthority: ProofAuthority
        Source: Diagnostics.Located<Diagnostics.SourceLocation>
    } with
        member this.DischargesProofObligations = this.ProofAuthority.DischargesProofObligations

    type CppAbiFactBundle = {
        Provenance: CppAbiFactProvenance
        ProofAuthority: ProofAuthority
        RequestedClasses: string list
        RequestedTemplates: (string * string list) list
        Facts: CppRecordedAbiFact list
    } with
        member this.DischargesProofObligations =
            this.ProofAuthority.DischargesProofObligations
            || (this.Facts |> List.exists _.DischargesProofObligations)

    /// Parsed Plugify (.pplugin) manifest specification (#119).
    type PlugifyManifest = {
        Tool: CppAbiFactToolProvenance
        ManifestSha256: string
        Classes: string list
        Templates: (string * string list) list
    }

    let private refuseAbiFact message source =
        Diagnostics.refuse (Diagnostics.diagnostic Diagnostics.CppAbiFactInvalidOrConflicting message source)

    let private unknownAbiSource reason =
        Diagnostics.Located.Unknown reason

    let private sha256Text (content: string) =
        let bytes = System.Text.Encoding.UTF8.GetBytes content
        System.Security.Cryptography.SHA256.HashData bytes |> System.Convert.ToHexString |> fun h -> h.ToLowerInvariant()

    let private validateToolProvenance (tool: CppAbiFactToolProvenance) source =
        if not (System.String.Equals(tool.ToolName.Trim(), "plugify", System.StringComparison.OrdinalIgnoreCase)) then
            refuseAbiFact $"C++ ABI fact provenance tool must be 'plugify', got '{tool.ToolName}'." source
        if System.String.IsNullOrWhiteSpace tool.ToolVersion then
            refuseAbiFact "C++ ABI fact provenance from Plugify is missing a tool version." source
        if System.String.IsNullOrWhiteSpace tool.ManifestSource then
            refuseAbiFact "C++ ABI fact provenance from Plugify is missing a manifest source." source

    /// Parse a Plugify (.pplugin) JSON manifest and establish its SHA-256 provenance (#119).
    let parsePlugifyManifestJson (manifestSource: string) (manifestJson: string) : PlugifyManifest =
        let source = unknownAbiSource $"Plugify manifest '{manifestSource}'"
        if System.String.IsNullOrWhiteSpace manifestSource then
            refuseAbiFact "C++ ABI fact provenance from Plugify is missing a manifest source." source
        if System.String.IsNullOrWhiteSpace manifestJson then
            refuseAbiFact $"Plugify manifest '{manifestSource}' is empty." source
        let root =
            try FSharp.Data.JsonValue.Parse manifestJson
            with ex -> refuseAbiFact $"Plugify manifest '{manifestSource}' is not valid JSON: {ex.Message}" source
        let prop (node: FSharp.Data.JsonValue) (name: string) =
            match node with
            | FSharp.Data.JsonValue.Record props -> props |> Array.tryPick (fun (k, v) -> if k = name then Some v else None)
            | _ -> None
        let strProp (node: FSharp.Data.JsonValue) (name: string) =
            match prop node name with
            | Some (FSharp.Data.JsonValue.String s) -> s
            | _ -> ""
        let generator =
            match prop root "generator" with
            | Some g -> g
            | None -> refuseAbiFact $"Plugify manifest '{manifestSource}' is missing required 'generator' provenance object." source
        let toolName = strProp generator "name"
        let toolVersion = strProp generator "version"
        let digest = sha256Text manifestJson
        match prop root "manifestSha256" with
        | Some (FSharp.Data.JsonValue.String claimedDigest) when claimedDigest.Trim().ToLowerInvariant() <> digest ->
            refuseAbiFact $"Plugify manifest '{manifestSource}' digest mismatch (claimed {claimedDigest}, actual {digest})." source
        | _ -> ()
        let tool = { ToolName = toolName; ToolVersion = toolVersion; ManifestSource = $"{manifestSource}#sha256={digest}" }
        validateToolProvenance tool source
        let classes =
            match prop root "classes" with
            | Some (FSharp.Data.JsonValue.Array arr) ->
                [ for item in arr do
                    match item with
                    | FSharp.Data.JsonValue.String c when not (System.String.IsNullOrWhiteSpace c) -> yield normalize c
                    | _ -> refuseAbiFact $"Plugify manifest '{manifestSource}' contains a malformed entry in 'classes'." source ]
            | Some _ -> refuseAbiFact $"Plugify manifest '{manifestSource}' field 'classes' must be a JSON array." source
            | None -> []
        let templates =
            match prop root "templates" with
            | Some (FSharp.Data.JsonValue.Array arr) ->
                [ for item in arr do
                    let tName = strProp item "template"
                    let tArgs =
                        match prop item "arguments" with
                        | Some (FSharp.Data.JsonValue.Array argArr) ->
                            [ for a in argArr do
                                match a with
                                | FSharp.Data.JsonValue.String s when not (System.String.IsNullOrWhiteSpace s) -> yield normalize s
                                | _ -> refuseAbiFact $"Plugify manifest '{manifestSource}' contains a malformed template argument." source ]
                        | _ -> []
                    if System.String.IsNullOrWhiteSpace tName || tArgs.IsEmpty then
                        refuseAbiFact $"Plugify manifest '{manifestSource}' contains a template entry missing 'template' or 'arguments'." source
                    yield normalize tName, tArgs ]
            | Some _ -> refuseAbiFact $"Plugify manifest '{manifestSource}' field 'templates' must be a JSON array." source
            | None -> []
        if classes.IsEmpty && templates.IsEmpty then
            refuseAbiFact $"Plugify manifest '{manifestSource}' declares no C++ classes or template instantiations." source
        { Tool = tool; ManifestSha256 = digest; Classes = classes; Templates = templates }

    let private classLeafName (normClass: string) =
        let withoutTemplate =
            match normClass.IndexOf('<') with
            | idx when idx > 0 -> normClass.Substring(0, idx)
            | _ -> normClass
        match withoutTemplate.LastIndexOf("::", System.StringComparison.Ordinal) with
        | idx when idx >= 0 -> withoutTemplate.Substring(idx + 2)
        | _ -> withoutTemplate

    let private collectAstContext (parsed: CppParser.ParseResult option) =
        match parsed with
        | None -> Map.empty, Map.empty, Map.empty, []
        | Some result ->
            let mutable classes = Map.empty<string, CppParser.ClassDecl>
            let mutable orderedClassNames = ResizeArray<string>()
            let mutable funcsByMangled = Map.empty<string, CppParser.FunctionDecl>
            let recordFunc (f: CppParser.FunctionDecl) =
                match f.MangledName with
                | Some m when m <> "" -> funcsByMangled <- Map.add m f funcsByMangled
                | _ -> ()
            let rec walk scope (decl: CppParser.Declaration) =
                match decl with
                | CppParser.Declaration.Function f -> recordFunc f
                | CppParser.Declaration.Class c ->
                    let qname =
                        c.QualifiedName
                        |> Option.defaultWith (fun () -> String.concat "::" (scope @ [c.Name]))
                        |> normalize
                    classes <- classes |> Map.add (normalize c.Name) c |> Map.add qname c
                    if not (orderedClassNames.Contains qname) then orderedClassNames.Add qname
                    for m in c.Methods do walk (scope @ [c.Name]) m
                    for ctor in c.Constructors do walk (scope @ [c.Name]) ctor
                | CppParser.Declaration.Namespace ns ->
                    for d in ns.Declarations do walk (scope @ [ns.Name]) d
                | _ -> ()
            for d in result.Declarations do walk [] d
            let templates =
                result.Templates
                |> List.fold (fun acc t -> Map.add (normalize t.TemplateName) t acc) Map.empty
            classes, templates, funcsByMangled, List.ofSeq orderedClassNames

    let private isFunctionNoExcept (func: CppParser.FunctionDecl) =
        func.Attributes |> List.exists (fun a -> a.Kind = "NoExceptAttr")

    let private generatePlugifyAbiFactsCore
        (build: CppLibraryBuild)
        (symbols: (string * string) list)
        (binaryVtableSlots: Map<string, (int * string * string) list>)
        (tool: CppAbiFactToolProvenance)
        (parsed: CppParser.ParseResult option)
        (classNames: string list)
        (templateRequests: (string * string list) list)
        : CppAbiFactBundle =
        let defaultSource = unknownAbiSource "No header source location was supplied for the Plugify ABI fact bundle."
        validateToolProvenance tool defaultSource
        if classNames.IsEmpty && templateRequests.IsEmpty then
            refuseAbiFact "Plugify ABI fact generation requires at least one C++ class or template instantiation target." defaultSource
        let provenance : CppAbiFactProvenance = { Tool = tool; Build = build }
        let astClasses, astTemplates, astFuncsByMangled, _ = collectAstContext parsed
        let inventory = inventoryOfSymbols build (parsed |> Option.map _.Templates |> Option.defaultValue []) symbols
        let recorded = ResizeArray<CppRecordedAbiFact>()
        let emittedBoundaries = System.Collections.Generic.HashSet<string>()
        let addFact fact source =
            recorded.Add {
                Fact = fact
                Provenance = provenance
                ProofAuthority = CallMechanicsOnly_PsgDischargesProof
                Source = source
            }
        let addBoundary callableName mangled isNoExcept source =
            if emittedBoundaries.Add mangled then
                addFact (ExceptionBoundaryFact { CallableName = callableName; MangledSymbol = mangled; IsNoExcept = isNoExcept }) source

        for rawClass in classNames do
            let normClass = normalize rawClass
            let leaf = classLeafName normClass
            let templateBase =
                match normClass.IndexOf('<') with
                | idx when idx > 0 -> Some (normClass.Substring(0, idx))
                | _ -> None
            let astCls =
                Map.tryFind normClass astClasses
                |> Option.orElseWith (fun () -> Map.tryFind leaf astClasses)
            let astTemplate = templateBase |> Option.bind (fun b -> Map.tryFind b astTemplates)
            if parsed.IsSome && astCls.IsNone && astTemplate.IsNone then
                refuseAbiFact $"C++ class '{normClass}' is not declared in the parsed header AST." defaultSource
            let source =
                astCls |> Option.map _.Location.Location
                |> Option.orElseWith (fun () -> astTemplate |> Option.map _.Location)
                |> Option.defaultWith (fun () -> unknownAbiSource $"No header source location was supplied for C++ class '{normClass}'.")

            let ctorTarget = normClass + "::" + leaf
            let dtorTarget = normClass + "::~" + leaf
            let vtableTarget = "vtable for " + normClass
            let typeinfoTarget = "typeinfo for " + normClass

            let ctorSymbols =
                symbols
                |> List.choose (fun (mangled, demangled) ->
                    if normalize (callableQualifiedName demangled) = ctorTarget && mangled.Contains("C1") then Some mangled else None)
                |> List.distinct
                |> List.sort

            let dtorSymbolsD1 =
                symbols
                |> List.choose (fun (mangled, demangled) ->
                    if normalize (callableQualifiedName demangled) = dtorTarget && mangled.EndsWith("D1Ev", System.StringComparison.Ordinal) then Some mangled else None)
                |> List.distinct
                |> List.sort

            let dtorSymbolsD0 =
                symbols
                |> List.choose (fun (mangled, demangled) ->
                    if normalize (callableQualifiedName demangled) = dtorTarget && mangled.EndsWith("D0Ev", System.StringComparison.Ordinal) then Some mangled else None)
                |> List.distinct
                |> List.sort

            let vtableSymbol =
                symbols
                |> List.tryPick (fun (mangled, demangled) ->
                    if normalize demangled = vtableTarget && mangled.StartsWith("_ZTV", System.StringComparison.Ordinal) then Some mangled else None)

            let typeinfoSymbol =
                symbols
                |> List.tryPick (fun (mangled, demangled) ->
                    if normalize demangled = typeinfoTarget && mangled.StartsWith("_ZTI", System.StringComparison.Ordinal) then Some mangled else None)

            let directMethods =
                symbols
                |> List.choose (fun (mangled, demangled) ->
                    if mangled.StartsWith("_ZN", System.StringComparison.Ordinal) || mangled.StartsWith("_ZNK", System.StringComparison.Ordinal) then
                        let qname = normalize (callableQualifiedName demangled)
                        let prefix = normClass + "::"
                        if qname.StartsWith(prefix, System.StringComparison.Ordinal) then
                            let memberName = qname.Substring prefix.Length
                            if not (memberName.Contains("::")) && memberName <> leaf && memberName <> ("~" + leaf) then
                                Some (memberName, qname, mangled)
                            else None
                        else None
                    else None)
                |> List.distinctBy (fun (_, _, m) -> m)
                |> List.sortBy (fun (_, _, m) -> m)

            if dtorSymbolsD1.IsEmpty then
                refuseAbiFact $"Plugify RAII ABI fact for C++ class '{normClass}' requires an exported complete-object destructor (D1) in library build '{build.LibraryPath}' (SHA-256 {build.Sha256})." source
            let dtorSymbol = dtorSymbolsD1.Head
            match astCls |> Option.bind _.DestructorMangledName with
            | Some astDtor when astDtor <> dtorSymbol ->
                refuseAbiFact $"Plugify RAII ABI fact destructor '{dtorSymbol}' for C++ class '{normClass}' conflicts with clang AST destructor '{astDtor}'." source
            | _ -> ()

            let storageSizeBytes, storageAlignBytes =
                match astCls with
                | Some cls ->
                    match cls.Measured with
                    | Some layout -> Some (layout.SizeBits / 8), Some (layout.AlignmentBits / 8)
                    | None ->
                        let sz = estimateClassSize cls
                        (if sz > 0 then Some sz else None), None
                | None -> None, None

            addFact
                (RaiiFact {
                    ClassName = normClass
                    ConstructorSymbols = ctorSymbols
                    DestructorSymbol = dtorSymbol
                    StorageSizeBytes = storageSizeBytes
                    StorageAlignBytes = storageAlignBytes
                })
                source

            let astVirtualMethods =
                match astCls with
                | Some cls ->
                    cls.Methods
                    |> List.choose (function
                        | CppParser.Declaration.Function f when f.IsVirtual -> Some f
                        | _ -> None)
                | None -> []

            let isPolymorphic =
                vtableSymbol.IsSome || not dtorSymbolsD0.IsEmpty
                || (astCls |> Option.exists (fun c -> c.IsAbstract || not astVirtualMethods.IsEmpty))

            if isPolymorphic then
                let exportedByMethodName =
                    directMethods |> List.groupBy (fun (mName, _, _) -> mName) |> Map.ofList
                for f in astVirtualMethods do
                    let hasExport =
                        match f.MangledName with
                        | Some m -> symbols |> List.exists (fun (s, _) -> s = m)
                        | None -> Map.containsKey f.Name exportedByMethodName
                    if not hasExport then
                        refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' is missing exported symbol for virtual method '{f.Name}' in library build '{build.LibraryPath}' (SHA-256 {build.Sha256})." source
                let vtableSym =
                    match vtableSymbol with
                    | Some sym -> sym
                    | None ->
                        refuseAbiFact $"Plugify virtual table ABI fact for polymorphic C++ class '{normClass}' requires an exported vtable symbol (_ZTV*) in library build '{build.LibraryPath}' (SHA-256 {build.Sha256})." source
                let virtualMethods =
                    match Map.tryFind vtableSym binaryVtableSlots with
                    | Some slotsFromBinary when not slotsFromBinary.IsEmpty -> slotsFromBinary
                    | _ ->
                        refuseAbiFact $"Plugify virtual table ABI fact for polymorphic C++ class '{normClass}' could not extract binary vtable layout for '{vtableSym}' in '{build.LibraryPath}'." source
                addFact
                    (VirtualTableFact {
                        ClassName = normClass
                        VtableSymbol = Some vtableSym
                        TypeinfoSymbol = typeinfoSymbol
                        VirtualMethods = virtualMethods
                    })
                    source
                for _, qname, m in virtualMethods do
                    let isDtor = qname.Contains("::~")
                    let noExcept = isDtor || (Map.tryFind m astFuncsByMangled |> Option.exists isFunctionNoExcept)
                    addBoundary qname m noExcept source

            for m in ctorSymbols do
                let noExcept = Map.tryFind m astFuncsByMangled |> Option.exists isFunctionNoExcept
                addBoundary ctorTarget m noExcept source
            addBoundary dtorTarget dtorSymbol true source
            for _, qname, m in directMethods do
                let noExcept = Map.tryFind m astFuncsByMangled |> Option.exists isFunctionNoExcept
                addBoundary qname m noExcept source

        for templateName, templateArgs in templateRequests do
            let normTemplate = normalize templateName
            let instName = instantiationName normTemplate templateArgs
            let templateSource =
                Map.tryFind normTemplate astTemplates
                |> Option.map _.Location
                |> Option.defaultWith (fun () -> unknownAbiSource $"No header source location was supplied for C++ template '{normTemplate}'.")
            match inventory.Instantiations |> List.tryFind (fun candidate -> candidate.InstantiationName = instName) with
            | None ->
                refuseAbiFact $"Plugify template ABI fact for '{instName}' is missing from library build '{build.LibraryPath}' (SHA-256 {build.Sha256})." templateSource
            | Some matched ->
                if parsed.IsSome && not matched.AstDeclared then
                    refuseAbiFact $"Plugify template ABI fact for '{instName}' is not declared as a template in the parsed header AST." templateSource
                addFact
                    (TemplateInstanceFact {
                        InstantiationName = matched.InstantiationName
                        TemplateName = matched.TemplateName
                        TemplateArguments = matched.TemplateArguments
                        MangledSymbols = matched.MangledSymbols
                    })
                    templateSource
                for mangled, demangled in List.zip matched.MangledSymbols matched.DemangledSymbols do
                    if mangled.StartsWith("_ZN", System.StringComparison.Ordinal) || mangled.StartsWith("_ZNK", System.StringComparison.Ordinal) then
                        let qname = normalize (callableQualifiedName demangled)
                        let isDtor = qname.Contains("::~")
                        let noExcept = isDtor || (Map.tryFind mangled astFuncsByMangled |> Option.exists isFunctionNoExcept)
                        addBoundary qname mangled noExcept templateSource

        {
            Provenance = provenance
            ProofAuthority = CallMechanicsOnly_PsgDischargesProof
            RequestedClasses = classNames |> List.map normalize |> List.distinct
            RequestedTemplates = templateRequests |> List.map (fun (t, args) -> normalize t, args |> List.map normalize) |> List.distinct
            Facts = List.ofSeq recorded
        }

    /// Generate Plugify C++ ABI facts with tool and build provenance from inspected symbol pairs (#119).
    let generatePlugifyAbiFactsFromSymbols
        (build: CppLibraryBuild)
        (symbols: (string * string) list)
        (tool: CppAbiFactToolProvenance)
        (parsed: CppParser.ParseResult option)
        (classNames: string list)
        (templateRequests: (string * string list) list)
        : CppAbiFactBundle =
        generatePlugifyAbiFactsCore build symbols Map.empty tool parsed classNames templateRequests

    /// Generate Plugify C++ ABI facts with tool and build provenance from a library binary (#119).
    let generatePlugifyAbiFacts
        (libraryPath: string)
        (tool: CppAbiFactToolProvenance)
        (parsed: CppParser.ParseResult option)
        (classNames: string list)
        (templateRequests: (string * string list) list)
        : CppAbiFactBundle =
        let build, symbols, binaryVtableSlots = inspectLibraryExportsDetailed libraryPath
        generatePlugifyAbiFactsCore build symbols binaryVtableSlots tool parsed classNames templateRequests

    /// Generate Plugify C++ ABI facts by consuming a Plugify (.pplugin) JSON manifest and cross-verifying the binary and AST (#119).
    let generatePlugifyAbiFactsFromManifestJson
        (libraryPath: string)
        (manifestSource: string)
        (manifestJson: string)
        (parsed: CppParser.ParseResult option)
        : CppAbiFactBundle =
        let manifest = parsePlugifyManifestJson manifestSource manifestJson
        generatePlugifyAbiFacts libraryPath manifest.Tool parsed manifest.Classes manifest.Templates

    let private validatePlugifyAbiFactsCore
        (bundle: CppAbiFactBundle)
        (actualBuild: CppLibraryBuild)
        (symbols: (string * string) list)
        (binaryVtableSlots: Map<string, (int * string * string) list>)
        (parsed: CppParser.ParseResult option)
        : CppAbiFactBundle =
        let bundleSource =
            bundle.Facts |> List.tryHead |> Option.map _.Source
            |> Option.defaultWith (fun () -> unknownAbiSource "No header source location was supplied for the Plugify ABI fact bundle.")
        validateToolProvenance bundle.Provenance.Tool bundleSource
        let claimedBuild = bundle.Provenance.Build
        if claimedBuild.ResolvedPath <> actualBuild.ResolvedPath
           || claimedBuild.Sha256 <> actualBuild.Sha256
           || claimedBuild.Soname <> actualBuild.Soname then
            let claimedSoname = claimedBuild.Soname |> Option.defaultValue "<none>"
            let actualSoname = actualBuild.Soname |> Option.defaultValue "<none>"
            refuseAbiFact
                $"C++ ABI fact build provenance for '{claimedBuild.LibraryPath}' conflicts with inspected binary '{actualBuild.ResolvedPath}' (claimed SHA-256 {claimedBuild.Sha256}, actual SHA-256 {actualBuild.Sha256}; claimed SONAME '{claimedSoname}', actual SONAME '{actualSoname}')."
                bundleSource
        if bundle.RequestedClasses.IsEmpty && bundle.RequestedTemplates.IsEmpty then
            refuseAbiFact "Plugify ABI fact bundle declares no requested C++ classes or template instantiations." bundleSource
        if bundle.Facts.IsEmpty then
            refuseAbiFact "Plugify ABI fact bundle contains no recorded facts." bundleSource

        let exportedSet = symbols |> List.map fst |> Set.ofList
        let astClasses, astTemplates, astFuncsByMangled, _ = collectAstContext parsed
        let inventory = inventoryOfSymbols actualBuild (parsed |> Option.map _.Templates |> Option.defaultValue []) symbols

        let mutable raiiClasses = Set.empty<string>
        let mutable vtableClasses = Set.empty<string>
        let mutable templateInstances = Set.empty<string>
        let mutable boundarySymbols = Set.empty<string>
        let mutable requiredBoundarySymbols = Set.empty<string>

        for recorded in bundle.Facts do
            validateToolProvenance recorded.Provenance.Tool recorded.Source
            if recorded.Provenance <> bundle.Provenance then
                refuseAbiFact "Recorded C++ ABI fact provenance conflicts with its enclosing Plugify bundle provenance." recorded.Source
            match recorded.Fact with
            | RaiiFact raii ->
                let normClass = normalize raii.ClassName
                raiiClasses <- Set.add normClass raiiClasses
                let leaf = classLeafName normClass
                let templateBase =
                    match normClass.IndexOf('<') with
                    | idx when idx > 0 -> Some (normClass.Substring(0, idx))
                    | _ -> None
                let astCls = Map.tryFind normClass astClasses |> Option.orElseWith (fun () -> Map.tryFind leaf astClasses)
                let astTemplate = templateBase |> Option.bind (fun b -> Map.tryFind b astTemplates)
                if parsed.IsSome && astCls.IsNone && astTemplate.IsNone then
                    refuseAbiFact $"Plugify RAII ABI fact names C++ class '{normClass}', which is not declared in the parsed header AST." recorded.Source
                if System.String.IsNullOrWhiteSpace raii.DestructorSymbol
                   || not (raii.DestructorSymbol.EndsWith("D1Ev", System.StringComparison.Ordinal))
                   || not (Set.contains raii.DestructorSymbol exportedSet) then
                    refuseAbiFact $"Plugify RAII ABI fact for C++ class '{normClass}' claims destructor symbol '{raii.DestructorSymbol}', which is not an exported complete-object destructor (D1) in '{actualBuild.LibraryPath}'." recorded.Source
                requiredBoundarySymbols <- Set.add raii.DestructorSymbol requiredBoundarySymbols
                for ctorSym in raii.ConstructorSymbols do
                    if not (ctorSym.Contains("C1")) || not (Set.contains ctorSym exportedSet) then
                        refuseAbiFact $"Plugify RAII ABI fact for C++ class '{normClass}' claims constructor symbol '{ctorSym}', which is not an exported complete-object constructor (C1) in '{actualBuild.LibraryPath}'." recorded.Source
                    requiredBoundarySymbols <- Set.add ctorSym requiredBoundarySymbols
                match astCls |> Option.bind _.DestructorMangledName with
                | Some astDtor when astDtor <> raii.DestructorSymbol ->
                    refuseAbiFact $"Plugify RAII ABI fact destructor '{raii.DestructorSymbol}' for C++ class '{normClass}' conflicts with clang AST destructor '{astDtor}'." recorded.Source
                | _ -> ()
                match astCls |> Option.bind _.Measured with
                | Some layout ->
                    let expectedSize = layout.SizeBits / 8
                    let expectedAlign = layout.AlignmentBits / 8
                    if (raii.StorageSizeBytes.IsSome && raii.StorageSizeBytes <> Some expectedSize)
                       || (raii.StorageAlignBytes.IsSome && raii.StorageAlignBytes <> Some expectedAlign) then
                        refuseAbiFact $"Plugify RAII ABI fact storage layout for C++ class '{normClass}' conflicts with clang's measured layout ({expectedSize} bytes, align {expectedAlign} bytes)." recorded.Source
                | None -> ()

            | VirtualTableFact vt ->
                let normClass = normalize vt.ClassName
                vtableClasses <- Set.add normClass vtableClasses
                let leaf = classLeafName normClass
                let templateBase =
                    match normClass.IndexOf('<') with
                    | idx when idx > 0 -> Some (normClass.Substring(0, idx))
                    | _ -> None
                let astCls = Map.tryFind normClass astClasses |> Option.orElseWith (fun () -> Map.tryFind leaf astClasses)
                let astTemplate = templateBase |> Option.bind (fun b -> Map.tryFind b astTemplates)
                if parsed.IsSome && astCls.IsNone && astTemplate.IsNone then
                    refuseAbiFact $"Plugify virtual table ABI fact names C++ class '{normClass}', which is not declared in the parsed header AST." recorded.Source
                let expectedVtableTarget = "vtable for " + normClass
                let expectedVtableSymbol =
                    symbols
                    |> List.tryPick (fun (mangled, demangled) ->
                        if normalize demangled = expectedVtableTarget && mangled.StartsWith("_ZTV", System.StringComparison.Ordinal) then Some mangled else None)
                match vt.VtableSymbol with
                | None ->
                    refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' is missing required exported vtable symbol." recorded.Source
                | Some sym when not (sym.StartsWith("_ZTV", System.StringComparison.Ordinal)) || not (Set.contains sym exportedSet) ->
                    refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' claims vtable symbol '{sym}', which is not exported by '{actualBuild.LibraryPath}'." recorded.Source
                | Some sym when expectedVtableSymbol.IsSome && expectedVtableSymbol <> Some sym ->
                    refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' claims vtable symbol '{sym}', which does not match exported vtable symbol '{expectedVtableSymbol.Value}'." recorded.Source
                | Some sym ->
                    match Map.tryFind sym binaryVtableSlots with
                    | Some actualSlots when not actualSlots.IsEmpty ->
                        if vt.VirtualMethods <> actualSlots then
                            refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' virtual slots {vt.VirtualMethods} conflict with binary vtable '{sym}' layout {actualSlots}." recorded.Source
                    | _ ->
                        refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' could not verify binary vtable layout for '{sym}' in '{actualBuild.LibraryPath}'." recorded.Source
                match vt.TypeinfoSymbol with
                | Some sym when not (sym.StartsWith("_ZTI", System.StringComparison.Ordinal)) || not (Set.contains sym exportedSet) ->
                    refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' claims typeinfo symbol '{sym}', which is not exported by '{actualBuild.LibraryPath}'." recorded.Source
                | _ -> ()
                let slots = vt.VirtualMethods |> List.map (fun (slot, _, _) -> slot)
                if slots <> [0 .. vt.VirtualMethods.Length - 1] then
                    refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' has non-sequential virtual slot indices: {slots}." recorded.Source
                for _, methodName, sym in vt.VirtualMethods do
                    if not (Set.contains sym exportedSet) then
                        refuseAbiFact $"Plugify virtual table ABI fact for C++ class '{normClass}' claims virtual method '{methodName}' symbol '{sym}', which is not exported by '{actualBuild.LibraryPath}'." recorded.Source
                    requiredBoundarySymbols <- Set.add sym requiredBoundarySymbols

            | TemplateInstanceFact ti ->
                let expectedInstName = instantiationName ti.TemplateName ti.TemplateArguments
                if normalize ti.InstantiationName <> expectedInstName || ti.MangledSymbols.IsEmpty then
                    refuseAbiFact $"Plugify template ABI fact '{ti.InstantiationName}' is malformed or lists no mangled symbols." recorded.Source
                templateInstances <- Set.add expectedInstName templateInstances
                match inventory.Instantiations |> List.tryFind (fun candidate -> candidate.InstantiationName = expectedInstName) with
                | None ->
                    refuseAbiFact $"Plugify template ABI fact for '{expectedInstName}' is not exported by library build '{actualBuild.LibraryPath}' (SHA-256 {actualBuild.Sha256})." recorded.Source
                | Some matched ->
                    if parsed.IsSome && not matched.AstDeclared then
                        refuseAbiFact $"Plugify template ABI fact for '{expectedInstName}' is not declared as a template in the parsed header AST." recorded.Source
                    if (ti.MangledSymbols |> List.sort) <> matched.MangledSymbols then
                        refuseAbiFact $"Plugify template ABI fact symbols for '{expectedInstName}' conflict with the exported symbols in '{actualBuild.LibraryPath}'." recorded.Source
                    for sym in matched.MangledSymbols do
                        if sym.StartsWith("_ZN", System.StringComparison.Ordinal) || sym.StartsWith("_ZNK", System.StringComparison.Ordinal) then
                            requiredBoundarySymbols <- Set.add sym requiredBoundarySymbols

            | ExceptionBoundaryFact eb ->
                if System.String.IsNullOrWhiteSpace eb.MangledSymbol || not (Set.contains eb.MangledSymbol exportedSet) then
                    refuseAbiFact $"Plugify exception boundary ABI fact for '{eb.CallableName}' claims symbol '{eb.MangledSymbol}', which is not exported by '{actualBuild.LibraryPath}'." recorded.Source
                boundarySymbols <- Set.add eb.MangledSymbol boundarySymbols
                match Map.tryFind eb.MangledSymbol astFuncsByMangled with
                | Some astFunc ->
                    let astNoExcept = isFunctionNoExcept astFunc
                    if eb.IsNoExcept <> astNoExcept then
                        let claimedText = if eb.IsNoExcept then "true" else "false"
                        let astText = if astNoExcept then "true" else "false"
                        refuseAbiFact $"Plugify exception boundary ABI fact for '{eb.CallableName}' ({eb.MangledSymbol}) claims noexcept={claimedText}, which conflicts with clang AST noexcept={astText}." recorded.Source
                | None ->
                    if eb.CallableName.Contains("::~") && not eb.IsNoExcept then
                        refuseAbiFact $"Plugify exception boundary ABI fact for destructor '{eb.CallableName}' ({eb.MangledSymbol}) claims noexcept=false, conflicting with C++ destructor noexcept semantics." recorded.Source

        let requestedClassSet = bundle.RequestedClasses |> List.map normalize |> Set.ofList
        let allRequiredClasses = Set.union requestedClassSet (Set.union raiiClasses vtableClasses)
        for normClass in allRequiredClasses do
            if not (Set.contains normClass raiiClasses) then
                refuseAbiFact $"Plugify ABI fact bundle is incomplete: missing RAII ABI fact for C++ class '{normClass}'." bundleSource
            let vtableTarget = "vtable for " + normClass
            let dtorTarget = normClass + "::~" + classLeafName normClass
            let hasBinaryPolymorphism =
                symbols |> List.exists (fun (m, d) ->
                    let nd = normalize d
                    (nd = vtableTarget && m.StartsWith("_ZTV", System.StringComparison.Ordinal))
                    || (normalize (callableQualifiedName d) = dtorTarget && m.EndsWith("D0Ev", System.StringComparison.Ordinal)))
            let hasAstPolymorphism =
                Map.tryFind normClass astClasses
                |> Option.exists (fun c ->
                    c.IsAbstract
                    || (c.Methods |> List.exists (function CppParser.Declaration.Function f -> f.IsVirtual | _ -> false)))
            if (hasBinaryPolymorphism || hasAstPolymorphism) && not (Set.contains normClass vtableClasses) then
                refuseAbiFact $"Plugify ABI fact bundle is incomplete: missing virtual table ABI fact for polymorphic C++ class '{normClass}'." bundleSource

        for reqTemplate, reqArgs in bundle.RequestedTemplates do
            let reqInstName = instantiationName reqTemplate reqArgs
            if not (Set.contains reqInstName templateInstances) then
                refuseAbiFact $"Plugify ABI fact bundle is incomplete: missing template ABI fact for requested instantiation '{reqInstName}'." bundleSource

        for reqSym in requiredBoundarySymbols do
            if not (Set.contains reqSym boundarySymbols) then
                refuseAbiFact $"Plugify ABI fact bundle is incomplete: missing exception boundary ABI fact for symbol '{reqSym}'." bundleSource

        bundle

    /// Validate a Plugify C++ ABI fact bundle against an inspected library build and optional clang AST (#119).
    let validatePlugifyAbiFactsAgainstSymbols
        (bundle: CppAbiFactBundle)
        (actualBuild: CppLibraryBuild)
        (symbols: (string * string) list)
        (parsed: CppParser.ParseResult option)
        : CppAbiFactBundle =
        validatePlugifyAbiFactsCore bundle actualBuild symbols Map.empty parsed

    /// Validate a Plugify C++ ABI fact bundle against a library binary on disk and optional clang AST (#119).
    let validatePlugifyAbiFacts
        (bundle: CppAbiFactBundle)
        (libraryPath: string)
        (parsed: CppParser.ParseResult option)
        : CppAbiFactBundle =
        let actualBuild, symbols, binaryVtableSlots = inspectLibraryExportsDetailed libraryPath
        validatePlugifyAbiFactsCore bundle actualBuild symbols binaryVtableSlots parsed
