namespace Farscape.Core

open CodeAST
open PilotTypes

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
    /// wl_pointer_listener → buildPointerListener
    let private toBuilderName (structName: string) : string =
        let parts = structName.Split('_') |> Array.toList
        let meaningful =
            match parts with
            | p :: rest when p.Length <= 3 && rest.Length >= 2 -> rest
            | _ -> parts
        let pascal =
            meaningful |> List.map (fun s ->
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
            | "int" | "int32_t" -> Named "int32"
            | "uint" | "uint32_t" | "guint" -> Named "uint32"
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
        (bindingsModuleName: string)
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

        // Body: let handler = dlsym 0n handlerSymbol in originalFunc arg1 handler arg2 ...
        let body =
            LetIn("handler",
                FunctionCall("Fidelity.Libc.DynamicLink", "dlsym", [Literal "0n"; Identifier "handlerSymbol"]),
                FunctionCall(bindingsModuleName, reg.Function,
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

    /// Generate a builder function for a listener struct.
    /// Each callback field becomes a string parameter (symbol name).
    /// The builder resolves all symbols via dlsym and returns the populated struct.
    let generateListenerBuilder
        (ls: ListenerStruct)
        (structDecl: CppParser.StructDecl)
        (delegateNames: Set<string>)
        : FsDecl list =

        let builderName = toBuilderName ls.Name

        /// A field is a callback if it's a C function pointer or a known delegate type.
        let isCallbackField (f: CppParser.FieldDecl) =
            f.Type.Contains("(*)") || f.Type.Contains("(**)") || Set.contains f.Type delegateNames

        let callbackFields = structDecl.Fields |> List.filter isCallbackField

        if callbackFields.IsEmpty then []
        else
            let params' =
                callbackFields |> List.map (fun f ->
                    { Name = f.Name + "Sym"; Type = Named "string" })

            // Build the struct record with dlsym-resolved fields
            let fields =
                structDecl.Fields |> List.map (fun f ->
                    if isCallbackField f then
                        (f.Name, FunctionCall("Fidelity.Libc.DynamicLink", "dlsym",
                            [Literal "0n"; Identifier (f.Name + "Sym")]))
                    else
                        // Non-callback field: zero-init
                        (f.Name, Literal "Unchecked.defaultof<_>"))

            let body = RecordConstruction fields

            [ XmlDoc $"Build a {ls.Name} by resolving C symbol names via dlsym(RTLD_DEFAULT)."
              XmlDoc "Each parameter is a C symbol name resolved at runtime."
              LetBinding(builderName, params', Named ls.Name, body, []) ]

    // =========================================================================
    // Complete Module Generation
    // =========================================================================

    /// Generate callback FsDecl list from a CallbackSpec and available declarations.
    let generateDecls
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (bindingsModuleName: string)
        (model: Types.PlatformABI)
        : FsDecl list =

        // Collect delegate names for listener field type matching
        let delegateNames =
            declarations |> List.choose (function
                | CppParser.Declaration.Delegate d -> Some d.Name
                | _ -> None)
            |> Set.ofList

        let registrationDecls =
            spec.Registrations |> List.collect (fun reg ->
                // Look up the full function declaration
                let funcDecl =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Function f when f.Name = reg.Function -> Some f
                        | _ -> None)
                match funcDecl with
                | Some f -> generateRegistrationWrapper reg f bindingsModuleName model
                | None -> [])

        let listenerDecls =
            spec.ListenerStructs |> List.collect (fun ls ->
                let structDecl =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Struct s when s.Name = ls.Name -> Some s
                        | _ -> None)
                match structDecl with
                | Some s -> generateListenerBuilder ls s delegateNames
                | None -> [])

        match registrationDecls, listenerDecls with
        | [], [] -> []
        | regs, [] -> regs
        | [], listeners -> listeners
        | regs, listeners -> regs @ [ BlankLine ] @ listeners

    /// Generate the complete callback wrappers module as a rendered source string.
    let generate
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (bindingsModuleName: string)
        (model: Types.PlatformABI)
        : string option =

        let decls = generateDecls spec declarations bindingsModuleName model
        if decls.IsEmpty then None
        else
            let moduleDecl = Module(namespace', "Callback wrappers — dlsym-based runtime symbol resolution", decls)
            Some (CodeRenderer.render moduleDecl)
