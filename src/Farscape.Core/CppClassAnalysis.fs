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
    let private estimateClassSize (cls: CppParser.ClassDecl) : int =
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

    /// Only templates in the symbol's own qualified name count. Return types and parameter
    /// types mentioning a template do not export that template's operations.
    let private symbolInstantiations (signature: string) =
        let prefixes = [
            "non-virtual thunk to "; "virtual thunk to "; "covariant return thunk to "
            "vtable for "; "VTT for "; "typeinfo for "; "typeinfo name for "; "guard variable for "
        ]
        let signature =
            prefixes |> List.fold (fun (text: string) prefix ->
                if text.StartsWith(prefix, System.StringComparison.Ordinal) then text.Substring prefix.Length else text) signature
        let mutable depth, nameStart, finish = 0, 0, signature.Length
        let mutable operatorStart = -1
        let operatorAngle (text: string) index operatorIndex =
            operatorIndex >= 0 &&
            (text.Substring(operatorIndex + 8, index - operatorIndex - 8)
             |> Seq.forall (fun c -> "<>=!+-*/%^&|~".Contains c))
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
        let name = signature.Substring(nameStart, finish - nameStart)
        let mutable opening = -1
        operatorStart <- -1
        depth <- 0
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

    let private runSymbolTool program arguments (input: string option) =
        let info = ProcessLaunch.startInfo program arguments
        info.Environment.["LC_ALL"] <- "C"
        info.RedirectStandardInput <- input.IsSome
        use child = new System.Diagnostics.Process(StartInfo = info)
        if not (child.Start()) then invalidOp $"Could not start {program}."
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
        if child.ExitCode <> 0 then invalidOp $"{program} failed ({child.ExitCode}): {errors.Trim()}"
        output

    /// Inspect one ELF shared object or static archive. Tool failures are loud, with no
    /// replacement tool, header-only instantiation, or synthesized wrapper.
    let inspectTemplateInventory (libraryPath: string) (declarations: CppParser.TemplateDeclaration list) =
        let file = System.IO.FileInfo(System.IO.Path.GetFullPath libraryPath)
        let resolved =
            match file.ResolveLinkTarget true with
            | null -> file.FullName
            | target -> target.FullName
        let hash = ClangToolchain.sha256 resolved
        let archive =
            use stream = System.IO.File.OpenRead resolved
            let magic = Array.zeroCreate<byte> 8
            stream.ReadExactly magic
            List.contains (System.Text.Encoding.ASCII.GetString magic) ["!<arch>\n"; "!<thin>\n"]
        let lines (text: string) = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList
        let soname, symbols =
            if archive then
                let output = runSymbolTool "nm" ["--extern-only"; "--defined-only"; "--format=posix"; "--"; resolved] None
                None,
                (lines output |> List.choose (fun line ->
                    let columns = line.Split([|' '; '\t'|], System.StringSplitOptions.RemoveEmptyEntries)
                    if columns.Length >= 2 && columns.[0].StartsWith("_Z") then Some columns.[0] else None))
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
                        Some (columns.[7].Split('@').[0])
                    else None))
        let symbols = symbols |> List.distinct |> List.sort
        let demangled =
            if symbols.IsEmpty then []
            else runSymbolTool "c++filt" ["--no-strip-underscore"] (Some (String.concat "\n" symbols + "\n")) |> lines
        if symbols.Length <> demangled.Length then invalidOp "c++filt did not return one signature per exported symbol."
        if ClangToolchain.sha256 resolved <> hash then invalidOp $"Library artifact changed while inspecting '{libraryPath}'."
        inventoryOfSymbols
            { LibraryPath = libraryPath; ResolvedPath = resolved; Sha256 = hash; Soname = soname }
            declarations (List.zip symbols demangled)

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
