namespace Farscape.Core

open System.IO
open ProtocolParser

/// Typed Wayland projection. XML defines messages; installed headers supply the
/// union and metadata layouts. Owned native tables remain live until shutdown.
module TypedProtocolGenerator =
    let private q (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
    let private pascal (s: string) = s.Split('_') |> Array.map (fun p -> if p = "" then "" else p.Substring(0,1).ToUpperInvariant() + p.Substring(1)) |> String.concat ""
    let private line xs = String.concat "\n" xs + "\n"
    let private handle = "CHandle<unit>"
    let private optional = "option<CHandle<unit>>"
    let private integer = "Integer (Signed, 32)"
    let private unsigned = "Integer (Unsigned, 32)"
    let private pointer = "Pointer 64"
    let private parameter (name, typ, passing) = $"{{ Name = {q name}; Type = {typ}; PassBy = {passing} }}"
    let private functionRecord symbol parameters ret ownership =
        "{ CName = " + q symbol + "; Parameters = [| " + (parameters |> List.map parameter |> String.concat "; ") + " |]; ReturnType = " + ret + "; CallingConvention = CDecl; OwnershipTransfer = " + ownership + " }"
    let private externDecl library (name: string) symbol (parameters: (string * string * string * string) list) ret retRef ownership =
        let args = if parameters.IsEmpty then "()" else parameters |> List.map (fun (n,t,_,_) -> $"({n}: {t})") |> String.concat " "
        let bindingName = if name.EndsWith("Native", System.StringComparison.Ordinal) then "private " + name else name
        [ $"[<FidelityExtern({q library}, {q symbol})>]"
          $"let {bindingName} {args} : {ret} = NativeDefault.zeroed ()"
          "let " + name + "Descriptor: Expr<FunctionDescriptor> = <@ " + functionRecord symbol (parameters |> List.map (fun(n,_,r,p)->n,r,p)) retRef ownership + " @>"
          "" ]
    let private recordDecl name size alignment (fields: (string * string * int * string) list) =
        [ "type " + name + " = { " + (fields |> List.map (fun(n,t,_,_)->n+": "+t) |> String.concat "; ") + " }"
          "module " + name + " ="
          $"    let Size = {size}"
          $"    let Alignment = {alignment}"
          "    let Descriptor: StructDescriptor ="
          "        { Name = " + q name
          $"          Layout = {{ Size = {size}; Alignment = {alignment}; Fields = [|"
          ] @ (fields |> List.map (fun(n,_,o,r)-> $"              {{ Name = {q n}; Offset = {o}; Repr = {r}; Count = 1; Access = AccessKind.ReadWrite; BitFields = [||]; Documentation = Some \"XML field projected through measured native layout\" }}")) @ [
          "          |] }"
          "          Documentation = Some \"Generated from installed Wayland XML and measured header layouts\" }"
          "" ]
    let private argRef arg = match arg.Type with Int|Fixed|Fd -> integer | Uint|NewId -> unsigned | _ -> pointer
    let private argRepr arg = match arg.Type with Int|Fixed|Fd -> "Repr.I32" | Uint|NewId -> "Repr.U32" | _ -> "Repr.Pointer"
    let private callbackType arg = match arg.Type with Int|Fixed|Fd|Uint -> "int" | _ -> if arg.AllowNull then optional else handle
    let private signature (m: ProtocolMessage) =
        let code a =
            let typ = match a.Type with Int->"i"|Uint->"u"|Fixed->"f"|String->"s"|Object->"o"|NewId->(if a.Interface.IsNone then "sun" else "n")|Fd->"h"|Array->"a"
            (if a.AllowNull then "?" else "") + typ
        (if m.Since > 1 then string m.Since else "") + (m.Args |> List.map code |> String.concat "")

    let generate (project: PilotTypes.PilotProject) (protocols: Protocol list) (declarations: CppParser.Declaration list) (ctx: FidelityCodeGenerator.GenerationContext) outputDir =
        if ctx.DataModel <> Types.LP64 then failwith "Typed Wayland currently requires the measured LP64 host profile"
        // Reject drift or missing parsing instead of silently emitting hand-assumed
        // native declarations. Only the record/array projection is profile-defined.
        let functions = declarations |> List.choose (function CppParser.Declaration.Function f -> Some(f.Name,f) | _ -> None) |> Map.ofList
        let representation cType =
            let resolved = FidelityCodeGenerator.resolveCType ctx.TypedefMap ctx.DataModel ctx.OpaqueHandles ctx.Delegates ctx.Enums cType
            FidelityCodeGenerator.abiReprOf ctx.DataModel cType resolved
            |> Option.map (fun r -> r.Family, r.Bits)
        let verify symbol expectedParams expectedReturn =
            let f = functions |> Map.tryFind symbol |> Option.defaultWith(fun()->failwith $"Typed protocol requires parsed C declaration '{symbol}'")
            let actual = f.Parameters |> List.map (snd >> representation)
            if actual <> List.map Some expectedParams || representation f.ReturnType <> Some expectedReturn then
                failwith $"Typed protocol C signature '{symbol}' does not match the declared LP64 projection"
        let ptr = TypeMapper.Pointer, 64
        let u32 = TypeMapper.Unsigned, 32
        let i32 = TypeMapper.Signed, 32
        let size = TypeMapper.Unsigned, 64
        verify "calloc" [size;size] ptr
        verify "strdup" [ptr] ptr
        verify "free" [ptr] (TypeMapper.Void,0)
        verify "mempcpy" [ptr;ptr;size] ptr
        verify "wl_proxy_marshal_array_flags" [ptr;u32;ptr;u32;u32;ptr] ptr
        verify "wl_proxy_add_listener" [ptr;ptr;ptr] i32
        verify "wl_proxy_get_version" [ptr] u32
        let layout name = ctx.StructLayouts |> Map.tryFind name |> Option.defaultWith (fun()->failwith $"Typed protocol requires measured {name}")
        let argument, message, ifaceLayout = layout "wl_argument", layout "wl_message", layout "wl_interface"
        if argument.SizeBits <> 64 || argument.AlignmentBits <> 64 then failwith "Typed Wayland requires 64-bit measured wl_argument cells"
        let allInterfaces = protocols |> List.collect (fun p -> p.Interfaces)
        let wanted = project.Namespaces |> List.collect (fun n -> n.XmlInterfaces) |> Set.ofList
        let interfaces = allInterfaces |> List.filter (fun i -> wanted.Contains i.Name)
        let dir = Path.Combine(outputDir, "Bridge")
        Directory.CreateDirectory dir |> ignore
        let save file contents = let path = Path.Combine(dir,file+".clef") in File.WriteAllText(path,line contents); path
        let header name = [$"module Fidelity.Wayland.Bridge.{name}"; ""; "// Generated by Farscape from installed protocol XML and native header layouts."; "open BAREWire.Hardware"; "open BAREWire.Descriptors"; ""]
        let measuredFields (l:CppParser.StructLayoutInfo) names = List.zip names l.FieldOffsetsBits |> List.map (fun((n,t,r),o)->n,t,o/8,r)
        let typeFile = save "Types" (header "Types" @
            recordDecl "WlMessage" (message.SizeBits/8) (message.AlignmentBits/8) (measuredFields message ["Name",optional,"Repr.Pointer";"Signature",optional,"Repr.Pointer";"Types",optional,"Repr.Pointer"]) @
            recordDecl "WlInterface" (ifaceLayout.SizeBits/8) (ifaceLayout.AlignmentBits/8) (measuredFields ifaceLayout ["Name",optional,"Repr.Pointer";"Version","int","Repr.I32";"MethodCount","int","Repr.I32";"Methods",optional,"Repr.Pointer";"EventCount","int","Repr.I32";"Events",optional,"Repr.Pointer"]))
        let storageFile = save "NativeStorage" (header "NativeStorage" @ ["open Fidelity.Wayland.Bridge.Types";""] @
            externDecl "c" "allocate" "calloc" ["count","int","Integer (Unsigned, 64)","Value";"size","int","Integer (Unsigned, 64)","Value"] optional pointer "CallerOwns" @
            externDecl "c" "duplicateString" "strdup" ["value","string",pointer,"Value"] optional pointer "CallerOwns" @
            externDecl "c" "release" "free" ["value",handle,pointer,"Value"] "unit" "Void" "CalleeOwns" @
            externDecl "c" "copyMessageNative" "mempcpy" ["destination",handle,pointer,"Value";"source","WlMessage","Named \"WlMessage\"","ReadOnlyReference";"count","int","Integer (Unsigned, 64)","Value"] optional pointer "Borrowed" @
            externDecl "c" "copyInterfaceNative" "mempcpy" ["destination",handle,pointer,"Value";"source","WlInterface","Named \"WlInterface\"","ReadOnlyReference";"count","int","Integer (Unsigned, 64)","Value"] optional pointer "Borrowed" @
            externDecl "c" "copyInterfacePointerNative" "mempcpy" ["destination",handle,pointer,"Value";"source",optional+" array",pointer,"ReadOnlyReference";"count","int","Integer (Unsigned, 64)","Value"] optional pointer "Borrowed" @
            [ $"let copyMessage (destination: {handle}) (source: WlMessage) : {optional} = copyMessageNative destination source WlMessage.Size"
              $"let copyInterface (destination: {handle}) (source: WlInterface) : {optional} = copyInterfaceNative destination source WlInterface.Size"
              $"let copyInterfacePointer (destination: {handle}) (source: {optional} array) : {optional} = copyInterfacePointerNative destination source 8" ])
        let capacity = interfaces |> List.sumBy (fun i -> 4 + (i.Requests.Length+i.Events.Length)*3)
        let tables =
            header "ProtocolInterfaces" @ ["open Fidelity.Wayland.Bridge.Types";"open Fidelity.Wayland.Bridge.NativeStorage";"";
            $"let private owned: {optional} array = Array.zeroCreate {capacity}"; "let mutable private used = 0"; "let mutable private failed = false"; "let mutable private initialized = false";"";
            "let private retain (value: option<CHandle<unit>>) : option<CHandle<unit>> =";
            "    match value with"; "    | Some _ -> Array.set owned used value; used <- used + 1; value"; "    | None -> failed <- true; None";"";
            "let private text (value: string) : option<CHandle<unit>> = retain (duplicateString value)";
            "let private block (size: int) : option<CHandle<unit>> = retain (allocate 1 size)";""] @
            (interfaces |> List.collect (fun i -> [$"let mutable {i.Name}: {optional} = None"; $"let mutable {i.Name}Name: {optional} = None"])) @ ["";
            "let shutdown () : unit ="; "    while used > 0 do"; "        used <- used - 1"; "        match Array.get owned used with"; "        | Some value -> release value; Array.set owned used None"; "        | None -> ()";
            ] @ (interfaces |> List.collect (fun i -> [$"    {i.Name} <- None";$"    {i.Name}Name <- None"])) @ [
            "    initialized <- false"; "";
            "let initialize () : bool ="; "    if initialized then true"; "    else"; "        failed <- false";
            ] @ (interfaces |> List.collect (fun i -> [$"        {i.Name} <- block WlInterface.Size"; $"        {i.Name}Name <- text {q i.Name}"])) @
            (interfaces |> List.collect (fun i ->
                let messages label (ms: ProtocolMessage list) =
                    let var = i.Name + label
                    [ $"        let {var}: {optional} = " + (if ms.IsEmpty then "None" else $"block ({ms.Length} * WlMessage.Size)")
                      if not ms.IsEmpty then
                          $"        let mutable {var}Cursor = {var}"
                          yield! ms |> List.collect (fun m ->
                              let slots = m.Args |> List.collect (fun a -> if a.Type=NewId && a.Interface.IsNone then [None;None;None] else [a.Interface])
                              let typesVar = var + "_" + m.Name + "Types"
                              [ $"        let {typesVar}: {optional} = " + (if slots.IsEmpty then "None" else $"block {slots.Length * 8}") ] @
                              (if slots.IsEmpty then [] else
                                  [ $"        let mutable {typesVar}Cursor = {typesVar}" ] @
                                  (slots |> List.mapi (fun index target ->
                                      let value = target |> Option.filter wanted.Contains |> Option.defaultValue "None"
                                      [ $"        let {typesVar}Cell{index}: {optional} array = [| {value} |]"
                                        $"        match {typesVar}Cursor with"
                                        $"        | Some destination -> {typesVar}Cursor <- copyInterfacePointer destination {typesVar}Cell{index}"
                                        "        | None -> failed <- true" ]) |> List.concat)) @
                              [ $"        let {var}_{m.Name}: WlMessage = {{ Name = text {q m.Name}; Signature = text {q (signature m)}; Types = {typesVar} }}"
                                $"        match {var}Cursor with"
                                $"        | Some destination -> {var}Cursor <- copyMessage destination {var}_{m.Name}"
                                "        | None -> failed <- true" ]) ]

                messages "Methods" i.Requests @ messages "Events" i.Events @ [
                    $"        let {i.Name}Record: WlInterface = {{ Name = {i.Name}Name; Version = {i.Version}; MethodCount = {i.Requests.Length}; Methods = {i.Name}Methods; EventCount = {i.Events.Length}; Events = {i.Name}Events }}"
                    $"        match {i.Name} with"
                    $"        | Some destination -> copyInterface destination {i.Name}Record |> ignore"
                    "        | None -> failed <- true" ])) @ [
            "        if failed then shutdown (); false"; "        else initialized <- true; true" ]
        let interfacesFile = save "ProtocolInterfaces" tables
        let listenerNames = Map.ofList ["wl_registry","WlRegistryListener";"wl_callback","WlCallbackListener";"wl_buffer","WlBufferListener";"xdg_wm_base","XdgWmBaseListener";"xdg_surface","XdgSurfaceListener";"xdg_toplevel","XdgToplevelListener";"zwp_linux_dmabuf_v1","ZwpLinuxDmabufListener"]
        let callbacksFile = save "Callbacks" (header "Callbacks" @ (interfaces |> List.collect (fun i ->
            match Map.tryFind i.Name listenerNames with
            | None -> []
            | Some name ->
                // Keep the complete native event table from the installed XML.
                let events = i.Events
                recordDecl name (events.Length*8) 8 (events |> List.mapi (fun ix e -> pascal e.Name,"FnPtr<"+(handle::handle::(e.Args |> List.map callbackType) @ ["unit"] |> String.concat " -> ")+">",ix*8,"Repr.Pointer")) @
                (events |> List.collect (fun e ->
                    let parameters = ["data",pointer,"Value";i.Name,pointer,"Value"] @ (e.Args |> List.map(fun a->a.Name,argRef a,"Value"))
                    ["let " + name + pascal e.Name + "Descriptor: Expr<CallbackDescriptor> = <@ { Record = " + q name + "; Field = " + q(pascal e.Name) + "; Signature = " + functionRecord (i.Name+"."+e.Name) parameters "Void" "Borrowed" + " } @>";""])) @
                externDecl "wayland-client" ("add"+name) "wl_proxy_add_listener" ["proxy",handle,pointer,"Value";"listener",name,"Named "+q name,"ReadOnlyReference";"data",handle,pointer,"Value"] "int" integer "Borrowed")))
        let requestFiles = project.Namespaces |> List.choose (fun ns ->
            if ns.XmlInterfaces.IsEmpty then None else
            let requests = interfaces |> List.filter(fun i->List.contains i.Name ns.XmlInterfaces) |> List.collect(fun i-> i.Requests |> List.mapi(fun opcode r->i,opcode,r) |> List.filter(fun(_,_,r)->ns.Functions.IsEmpty || List.contains(i.Name+"_"+r.Name) ns.Functions))
            let contents = requests |> List.collect (fun (i,opcode,r) ->
                let name = i.Name+"_"+r.Name
                let ctor = r.Args |> List.tryFind(fun a->a.Type=NewId)
                if ctor |> Option.exists(fun a->a.Interface.IsNone) then [] else
                let argsName = pascal name + "Args"
                let args = r.Args |> List.filter(fun a->a.Type<>NewId)
                let fields = r.Args |> List.mapi(fun ix a ->
                    let field = pascal a.Name
                    match a.Type with
                    | Int|Uint|Fixed|Fd|NewId -> [field,"int",ix*8,argRepr a; field+"Padding","int",ix*8+4,"Repr.U32"]
                    | _ -> [field,optional,ix*8,"Repr.Pointer"]) |> List.concat
                let nativeArgs = ["proxy",handle,pointer,"Value";"opcode","int",unsigned,"Value";"interfaceType",optional,pointer,"Value";"version","int",unsigned,"Value";"flags","int",unsigned,"Value";"args",(if fields.IsEmpty then optional else argsName),(if fields.IsEmpty then pointer else "Named "+q argsName),(if fields.IsEmpty then "Value" else "ReadOnlyReference")]
                let result = if ctor.IsSome then optional else "unit"
                let paramsSource = ("(proxy: "+handle+")") :: (args |> List.map(fun a -> let t = match a.Type with Int|Uint|Fixed|Fd->"int"|String->"string"|_ -> if a.AllowNull then optional else handle in "("+a.Name+": "+t+")")) |> String.concat " "
                let strings = args |> List.filter(fun a->a.Type=String)
                if ctor.IsSome && not strings.IsEmpty then failwith "Typed constructor string arguments need an explicit ownership profile"
                let indent = if strings.IsEmpty then "    " else "        "
                let initializers =
                    r.Args |> List.collect(fun a ->
                        let n = pascal a.Name
                        match a.Type with
                        | NewId -> [n+" = 0";n+"Padding = 0"]
                        | Int|Uint|Fixed|Fd -> [n+" = "+a.Name;n+"Padding = 0"]
                        | String -> [n+" = Some string_"+a.Name]
                        | _ -> [n+" = "+(if a.AllowNull then a.Name else "Some "+a.Name)])
                    |> String.concat "; "
                (if fields.IsEmpty then [] else recordDecl argsName (r.Args.Length*8) 8 fields) @
                externDecl "wayland-client" (name+"Native") "wl_proxy_marshal_array_flags" nativeArgs optional pointer (if ctor.IsSome then "CallerOwns" elif r.IsDestructor then "CalleeOwns" else "Borrowed") @
                [$"let {name} {paramsSource} : {result} ="] @
                (strings |> List.collect(fun a -> [$"    match NativeStorage.duplicateString {a.Name} with";"    | None -> ()";$"    | Some string_{a.Name} ->"])) @
                (if fields.IsEmpty then [] else [indent + $"let args: {argsName} = {{ {initializers} }}"]) @
                [indent+ (if strings.IsEmpty then "" else "let result = ") + name+"Native proxy "+string opcode+" "+(ctor |> Option.bind(fun a->a.Interface) |> Option.map(fun n->"ProtocolInterfaces."+n) |> Option.defaultValue "None")+" (Fidelity.Wayland.Core.wl_proxy_get_version proxy) "+(if r.IsDestructor then "1" else "0")+" "+(if fields.IsEmpty then "None" else "args")+(if ctor.IsNone then " |> ignore" else "") ] @
                (strings |> List.map(fun a->indent+"NativeStorage.release string_"+a.Name)) @ (if strings.IsEmpty then [] else [indent+"result"]) @ [""])
            let bind =
              if ns.XmlInterfaces |> List.contains "wl_registry" then
                let n="RegistryBindArgs"
                recordDecl n 32 8 ["Name","int",0,"Repr.U32";"NamePadding","int",4,"Repr.U32";"Interface",optional,8,"Repr.Pointer";"Version","int",16,"Repr.U32";"VersionPadding","int",20,"Repr.U32";"Id","int",24,"Repr.U32";"IdPadding","int",28,"Repr.U32"] @
                externDecl "wayland-client" "registryBindNative" "wl_proxy_marshal_array_flags" ["proxy",handle,pointer,"Value";"opcode","int",unsigned,"Value";"interfaceType",optional,pointer,"Value";"version","int",unsigned,"Value";"flags","int",unsigned,"Value";"args",n,"Named "+q n,"ReadOnlyReference"] optional pointer "CallerOwns" @
                (["compositor","wl_compositor";"xdg_wm_base","xdg_wm_base";"dmabuf","zwp_linux_dmabuf_v1"] |> List.collect(fun(suffix,target)->[$"let wl_registry_bind_{suffix} (proxy: {handle}) (name: int) (version: int) : {optional} =";$"    let args: RegistryBindArgs = {{ Name = name; NamePadding = 0; Interface = ProtocolInterfaces.{target}Name; Version = version; VersionPadding = 0; Id = 0; IdPadding = 0 }}";$"    registryBindNative proxy 0 ProtocolInterfaces.{target} version 0 args";""]))
              else []
            let name = ns.Name.Split('.') |> Array.last
            Some(save name (header name @ ["open Fidelity.Wayland.Bridge";""] @ contents @ bind)))
        [typeFile;storageFile;interfacesFile;callbacksFile] @ requestFiles
