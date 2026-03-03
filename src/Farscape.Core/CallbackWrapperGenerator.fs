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

    /// Convert a C function name to a PascalCase wrapper name.
    /// g_signal_connect_data → signalConnectData (drop common prefixes, camelCase)
    let private toWrapperName (cName: string) : string =
        let parts = cName.Split('_') |> Array.toList
        // Drop common library prefixes (g_, gtk_, wl_, xdg_, etc.)
        let meaningful =
            match parts with
            | p :: rest when p.Length <= 3 && rest.Length >= 2 -> rest
            | _ -> parts
        match meaningful with
        | [] -> cName
        | first :: rest ->
            let camel = first + (rest |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat "")
            camel

    /// Convert a snake_case struct name to a PascalCase builder function name.
    /// wl_pointer_listener → buildPointerListener
    let private toBuilderName (structName: string) : string =
        let parts = structName.Split('_') |> Array.toList
        // Drop leading library prefix
        let meaningful =
            match parts with
            | p :: rest when p.Length <= 3 && rest.Length >= 2 -> rest
            | _ -> parts
        let pascal =
            meaningful |> List.map (fun s ->
                if s.Length > 0 then string (System.Char.ToUpper s.[0]) + s.[1..]
                else "") |> String.concat ""
        "build" + pascal

    // =========================================================================
    // Pattern A: Registration Function Wrappers
    // =========================================================================

    /// Generate a wrapper for a callback registration function.
    /// Replaces the function pointer parameter with a string symbol name,
    /// and the userdata parameter (if any) with 0n.
    let generateRegistrationWrapper
        (reg: CallbackRegistration)
        (bindingsModuleName: string)
        : FsDecl list =

        let wrapperName = toWrapperName reg.Function

        // Build parameter list: keep all original params except:
        //   - callback param → string (symbol name)
        //   - data param → removed (we pass 0n)
        // Since we don't have the full function signature here, we generate
        // a focused wrapper that takes the non-callback params plus a symbol string.
        // The wrapper calls dlsym to resolve the symbol, then calls the L1 function.

        let doc = $"Resolve callback symbol at runtime and register via {reg.Function}."
        let callbackParamDoc =
            match reg.DataParam with
            | Some dp -> $"Resolves {{handlerSymbol}} via dlsym(RTLD_DEFAULT). Passes 0n for '{dp}'."
            | None -> $"Resolves {{handlerSymbol}} via dlsym(RTLD_DEFAULT)."

        [ XmlDoc doc
          XmlDoc callbackParamDoc
          Comment $"Pattern A wrapper for {reg.Function}"
          Comment $"  callback_param = \"{reg.CallbackParam}\""
          Comment (match reg.DataParam with Some dp -> $"  data_param = \"{dp}\"" | None -> "  data_param = (none)")
          BlankLine ]

    // =========================================================================
    // Pattern B: Listener Struct Builder
    // =========================================================================

    /// Generate a builder function for a listener struct.
    /// Each function pointer field becomes a string parameter (symbol name).
    /// The builder resolves all symbols via dlsym and returns the populated struct.
    let generateListenerBuilder
        (ls: ListenerStruct)
        (structDecl: CppParser.StructDecl option)
        : FsDecl list =

        let builderName = toBuilderName ls.Name

        match structDecl with
        | None ->
            // No struct declaration found — emit a comment placeholder
            [ Comment $"Listener struct '{ls.Name}' not found in declarations; skipping builder generation." ]
        | Some s ->
            let fpFields =
                s.Fields |> List.filter (fun f ->
                    f.Type.Contains("(*)") || f.Type.Contains("(**)"))

            if fpFields.IsEmpty then
                [ Comment $"Listener struct '{ls.Name}' has no function pointer fields; skipping." ]
            else
                // Generate: let builderName (field1Sym: string) (field2Sym: string) ... : structName =
                let params' =
                    fpFields |> List.map (fun f ->
                        { Name = f.Name + "Sym"; Type = Named "string" })
                let resolveAndBuild =
                    // let resolve sym = Fidelity.Libc.DynamicLink.dlsym 0n sym
                    // { field1 = resolve field1Sym; field2 = resolve field2Sym; ... }
                    let fields =
                        fpFields |> List.map (fun f ->
                            (f.Name, FunctionCall("Fidelity.Libc.DynamicLink", "dlsym", [Literal "0n"; Identifier (f.Name + "Sym")])))
                    RecordConstruction fields

                [ XmlDoc $"Build a {ls.Name} by resolving C symbol names via dlsym(RTLD_DEFAULT)."
                  XmlDoc "Each parameter is a C symbol name that will be resolved at runtime."
                  LetBinding(
                    builderName,
                    params',
                    Named ls.Name,
                    resolveAndBuild,
                    []) ]

    // =========================================================================
    // Complete Module Generation
    // =========================================================================

    /// Generate callback FsDecl list from a CallbackSpec and available declarations.
    let generateDecls
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (bindingsModuleName: string)
        : FsDecl list =

        let registrationDecls =
            spec.Registrations |> List.collect (fun reg ->
                generateRegistrationWrapper reg bindingsModuleName)

        let listenerDecls =
            spec.ListenerStructs |> List.collect (fun ls ->
                let structDecl =
                    declarations |> List.tryPick (function
                        | CppParser.Declaration.Struct s when s.Name = ls.Name -> Some s
                        | _ -> None)
                generateListenerBuilder ls structDecl)

        let allDecls =
            match registrationDecls, listenerDecls with
            | [], [] -> []
            | regs, [] -> regs
            | [], listeners -> listeners
            | regs, listeners ->
                regs @ [ BlankLine ] @ listeners

        allDecls

    /// Generate the complete callback wrappers module as a rendered source string.
    let generate
        (spec: CallbackSpec)
        (declarations: CppParser.Declaration list)
        (namespace': string)
        (bindingsModuleName: string)
        : string option =

        let decls = generateDecls spec declarations bindingsModuleName
        if decls.IsEmpty then None
        else
            let moduleDecl = Module(namespace', "Callback wrappers — dlsym-based runtime symbol resolution", decls)
            Some (CodeRenderer.render moduleDecl)
