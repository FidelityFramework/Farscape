namespace Farscape.Core

open CodeAST
open PilotTypes
open ActivePatterns

/// Generates Layer 2 callback wrappers that wire C callback parameters
/// by resolving symbol names at runtime via dlsym(RTLD_DEFAULT, name).
///
/// Two patterns:
///   Pattern A (registration function): replaces function pointer param with string symbol name,
///     resolves via dlsym, passes nativeint to Layer 1 function.
///   Pattern B (listener struct): builds a struct of function pointers by resolving
///     each field's symbol name via dlsym, returns the populated struct.
///
/// Both patterns depend on Fidelity.Libc.DynamicLink.dlsym being available.
module CallbackWrapperGenerator =

    /// Convert a C function name to a camelCase wrapper name.
    /// g_idle_add → idleAdd, wl_proxy_add_listener → proxyAddListener
    let private toWrapperName (cName: string) : string =
        let parts = cName.Split('_') |> Array.toList
        // Drop common short library prefixes (g_, wl_, xdg_, etc.)
        let meaningful =
            match parts with
            | p :: rest when p.Length <= 3 && rest.Length >= 2 -> rest
            | _ -> parts
        match meaningful with
        | [] -> cName
        | first :: rest ->
            first + (rest |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat "")

    /// Convert a snake_case struct name to a PascalCase builder function name.
    /// wl_pointer_listener → buildWlPointerListener
    /// xdg_surface_listener → buildXdgSurfaceListener
    let private toBuilderName (structName: string) : string =
        let parts = structName.Split('_') |> Array.toList
        let pascal =
            parts |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat ""
        "build" + pascal

    /// Map a C parameter type to a Clef type for wrapper signatures.
    /// Simplified version — pointers become nativeint, basic types map directly.
    let private mapParamType (cType: string) (model: Types.PlatformABI) : FsType =
        if cType.Contains("(*)") || cType.Contains("(**)") then Named "nativeint"
        elif cType.Contains("*") then Named "nativeint"
        elif cType.Contains("void") && not (cType.Contains("*")) then Unit
        else
            let trimmed = cType.Replace("const ", "").Replace("unsigned ", "u").Replace("signed ", "").Trim()
            match trimmed with
            | "int" -> Named "int"          // NTU register-width dimensional
            | "int32_t" -> Named "int32"
            | "uint" -> Named "uint"        // NTU register-width dimensional
            | "uint32_t" | "guint" -> Named "uint32"
            | "long" -> Named "int64"
            | "ulong" | "unsigned long" -> Named "uint64"
            | "uint64_t" | "gulong" -> Named "uint64"
            | "int64_t" -> Named "int64"
            | "char" | "uchar" | "gchar" -> Named "byte"
            | "short" | "int16_t" -> Named "int16"
            | "ushort" | "uint16_t" -> Named "uint16"
            | "float" -> Named "float32"
            | "double" -> Named "float"
            | "size_t" -> Named "unativeint"
            | "ssize_t" -> Named "nativeint"
            | _ -> Named "nativeint"

    /// Map a C return type to a Clef type for wrapper signatures.
    let private mapReturnType (cType: string) (model: Types.PlatformABI) : FsType =
        let trimmed = cType.Trim()
        if trimmed = "void" then Unit
        elif trimmed.Contains("*") then Named "nativeint"
        else mapParamType trimmed model

    // =========================================================================
    // Pattern A: Registration Function Wrappers
    // =========================================================================

    /// Generate a wrapper for a callback registration function.
    /// Looks up the full function signature from declarations, then:
    ///   - Replaces callback param with handlerSymbol: string
    ///   - Removes userdata param (passes 0n)
    ///   - Resolves symbol via dlsym, calls original L1 function
    let generateRegistrationWrapper
        (reg: CallbackRegistration)
        (funcDecl: CppParser.FunctionDecl)
        (model: Types.PlatformABI)
        : FsDecl list =

        let wrapperName = toWrapperName reg.Function

        // Build wrapper parameters: replace callback with string, remove userdata
        let wrapperParams =
            funcDecl.Parameters |> List.choose (fun (name, cType) ->
                if name = reg.CallbackParam then
                    Some { Name = "handlerSymbol"; Type = Named "string" }
                elif reg.DataParam = Some name then
                    None  // userdata removed — we pass 0n
                else
                    Some { Name = name; Type = mapParamType cType model })

        let returnType = mapReturnType funcDecl.ReturnType model

        // Body: let handler = Fidelity.Libc.DynamicLink.dlsym 0n handlerSymbol in originalFunc arg1 handler arg2 ...
        // Fully qualified to avoid L2 wrapper shadowing (L2 dlsym returns Result<nativeint, CError>)
        let body =
            LetIn("handler",
                FunctionCall("Fidelity.Libc.DynamicLink", "dlsym", [Literal "0n"; Identifier "handlerSymbol"]),
                FunctionCall("", reg.Function,
                    funcDecl.Parameters |> List.map (fun (name, _) ->
                        if name = reg.CallbackParam then Identifier "handler"
                        elif reg.DataParam = Some name then Literal "0n"
                        else Identifier name)))

        let doc =
            match reg.DataParam with
            | Some dp -> $"Register callback by symbol name. Resolves via dlsym; passes 0n for {dp}."
            | None -> $"Register callback by symbol name. Resolves via dlsym."

        [ XmlDoc doc
          LetBinding(wrapperName, wrapperParams, returnType, body, []) ]

    // =========================================================================
    // Pattern B: Listener Struct Builder
    // =========================================================================

    /// A field is a callback if it's a C function pointer or a known delegate type.
    let private isCallbackField (delegateNames: Set<string>) (f: CppParser.FieldDecl) =
        f.Type.Contains("(*)") || f.Type.Contains("(**)") || Set.contains f.Type delegateNames

    /// Generate a direct builder function for a listener struct (Layer 2).
    /// Each callback field becomes a nativeint parameter. Record construction
    /// happens here — in the same package where the struct types are defined,
    /// avoiding CCS cross-package record field resolution issues.
    let generateListenerDirectBuilder
        (ls: ListenerStruct)
        (structDecl: CppParser.StructDecl)
        (delegateNames: Set<string>)
        : FsDecl list =

        let builderName = toBuilderName ls.Name

        let callbackFields = structDecl.Fields |> List.filter (isCallbackField delegateNames)

        if callbackFields.IsEmpty then []
        else
            let params' =
                callbackFields |> List.map (fun f ->
                    { Name = f.Name; Type = Named "nativeint" })

            let fields =
                structDecl.Fields |> List.map (fun f ->
                    if isCallbackField delegateNames f then
                        (f.Name, Identifier (cleanParamName f.Name))
                    else
                        (f.Name, Literal "NativeDefault.zeroed ()"))

            let body = RecordConstruction fields

            [ XmlDoc $"Build a {ls.Name} from resolved function pointers."
              LetBinding(builderName, params', Named ls.Name, body, []) ]

    /// Generate a bridge builder function for a listener struct (Layer 3).
    /// Each callback field becomes a string parameter (symbol name).
    /// The builder resolves symbols via dlsym then delegates to the L2 direct builder.
    let generateListenerBuilder
        (ls: ListenerStruct)
        (structDecl: CppParser.StructDecl)
        (delegateNames: Set<string>)
        (l2CallbackModule: string)
        : FsDecl list =

        let builderName = toBuilderName ls.Name

        let callbackFields = structDecl.Fields |> List.filter (isCallbackField delegateNames)

        if callbackFields.IsEmpty then []
        else
            let params' =
                callbackFields |> List.map (fun f ->
                    { Name = f.Name + "Sym"; Type = Named "string" })

            // Resolve each symbol via dlsym, then call the L2 direct builder
            let dlsymArgs =
                callbackFields |> List.map (fun f ->
                    FunctionCall("Fidelity.Libc.DynamicLink", "dlsym",
                        [Literal "0n"; Identifier (f.Name + "Sym")]))

            let body = FunctionCall(l2CallbackModule, builderName, dlsymArgs)

            [ XmlDoc $"Build a {ls.Name} by resolving C symbol names via dlsym(RTLD_DEFAULT)."
              XmlDoc "Each parameter is a C symbol name resolved at runtime."
              LetBinding(builderName, params', Named ls.Name, body, []) ]

    // =========================================================================
    // Complete Module Generation
    // =========================================================================

    /// Collect delegate names from declarations for listener field type matching.
    let private collectDelegateNames (declarations: CppParser.Declaration list) =
        declarations |> List.choose (function
            | CppParser.Declaration.Delegate d -> Some d.Name
            | _ -> None)
        |> Set.ofList

    /// Look up a struct declaration by name.
    let private findStructDecl (declarations: CppParser.Declaration list) (name: string) =
        declarations |> List.tryPick (function
            | CppParser.Declaration.Struct s when s.Name = name -> Some s
            | _ -> None)

    /// Generate Layer 2 direct listener builder decls (record construction with nativeint params).
    /// These belong in the main package where the struct types are defined.
    let generateL2Decls
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        : FsDecl list =

        let delegateNames = collectDelegateNames declarations

        spec.ListenerStructs |> List.collect (fun ls ->
            match findStructDecl declarations ls.Name with
            | Some s -> generateListenerDirectBuilder ls s delegateNames
            | None -> [])

    /// Generate Layer 3 bridge callback decls (registration wrappers + listener builders with dlsym).
    let generateDecls
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (model: Types.PlatformABI)
        (l2CallbackModule: string)
        : FsDecl list =

        let delegateNames = collectDelegateNames declarations

        let registrationDecls =
            spec.Registrations |> List.collect (fun reg ->
                let funcDecl =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Function f when f.Name = reg.Function -> Some f
                        | _ -> None)
                match funcDecl with
                | Some f -> generateRegistrationWrapper reg f model
                | None -> [])

        let listenerDecls =
            spec.ListenerStructs |> List.collect (fun ls ->
                match findStructDecl declarations ls.Name with
                | Some s -> generateListenerBuilder ls s delegateNames l2CallbackModule
                | None -> [])

        match registrationDecls, listenerDecls with
        | [], [] -> []
        | regs, [] -> regs
        | [], listeners -> listeners
        | regs, listeners -> regs @ [ BlankLine ] @ listeners

    /// Generate the complete Layer 3 bridge callback wrappers module as a rendered source string.
    let generate
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (model: Types.PlatformABI)
        (openModules: string list)
        (l2CallbackModule: string)
        : string option =

        let decls = generateDecls spec declarations model l2CallbackModule
        if decls.IsEmpty then None
        else
            let opens = openModules |> List.map OpenModule
            let moduleDecl = Module(namespace', "Callback wrappers — dlsym-based runtime symbol resolution", opens @ decls)
            Some (CodeRenderer.render moduleDecl)

    /// Generate the Layer 2 direct listener builders module as a rendered source string.
    /// This goes in the main package where struct types are in scope.
    let generateL2
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (openModules: string list)
        : string option =

        let decls = generateL2Decls spec declarations
        if decls.IsEmpty then None
        else
            let opens = openModules |> List.map OpenModule
            let moduleDecl = Module(namespace', "Listener builder functions — direct record construction", opens @ decls)
            Some (CodeRenderer.render moduleDecl)
