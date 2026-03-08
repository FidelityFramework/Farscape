namespace Farscape.Core

open Fidelity.Data.XML

/// Parses XML protocol definitions into the Farscape pipeline.
///
/// Libraries that define their API surface via XML protocol schemas (message-passing
/// over a core dispatch ABI) use this module. The XML carries semantic information —
/// which args are object creation, which are file descriptor passing, event/request
/// directionality — that the code generator uses to produce implementation bodies
/// calling the core marshal function.
///
/// The protocol IR is generic. XML format parsing can be format-specific (e.g. Wayland
/// protocol XML), but the IR and code generation are library-agnostic.
///
/// Architecture: Fidelity.Data.XML handles XML syntax. This module handles protocol
/// semantics and produces two outputs:
///   1. CppParser.Declaration list — types, enums, delegates, listener structs (→ FidelityCodeGenerator)
///   2. CodeAST.FsDecl list — request implementation bodies calling core marshal function
module ProtocolParser =

    // =========================================================================
    // Protocol Intermediate Representation (generic, not library-specific)
    // =========================================================================

    /// Protocol argument types (from XML `type` attribute).
    type ProtocolArgType =
        | Int | Uint | Fixed | String | Object | NewId | Fd | Array

    /// A single argument in a request or event message.
    type ProtocolArg = {
        Name: string
        Type: ProtocolArgType
        /// Interface name for typed object/new_id args (e.g. "wl_surface")
        Interface: string option
        /// Enum reference for constrained int/uint args
        Enum: string option
        AllowNull: bool
        Summary: string option
    }

    /// A request or event message within an interface.
    type ProtocolMessage = {
        Name: string
        Args: ProtocolArg list
        IsDestructor: bool
        Since: int
        Documentation: string option
    }

    /// A single entry in an enum (named constant).
    type ProtocolEnumEntry = {
        Name: string
        Value: string
        Summary: string option
    }

    /// An enumeration within an interface.
    type ProtocolEnum = {
        Name: string
        Entries: ProtocolEnumEntry list
        IsBitfield: bool
        Documentation: string option
    }

    /// An interface in a protocol definition.
    type ProtocolInterface = {
        Name: string
        Version: int
        Requests: ProtocolMessage list
        Events: ProtocolMessage list
        Enums: ProtocolEnum list
        Documentation: string option
    }

    /// A complete protocol definition (one XML file).
    type Protocol = {
        Name: string
        Interfaces: ProtocolInterface list
    }

    /// Alias for the protocol dispatch config from PilotTypes.
    type MarshalConfig = PilotTypes.ProtocolConfig

    // =========================================================================
    // XML Parsing (format-specific parsers produce generic Protocol IR)
    // =========================================================================

    /// Helper: get attribute value or default.
    let private attr (name: string) (defaultValue: string) (el: XmlNode) =
        XmlNode.attrDefault name defaultValue el

    /// Helper: get optional attribute (None for missing or empty).
    let private attrOpt (name: string) (el: XmlNode) : string option =
        match XmlNode.attr name el with
        | Some v when v <> "" -> Some v
        | _ -> None

    /// Helper: get first child <description> summary text.
    /// Normalizes whitespace (collapses newlines and runs of spaces into single spaces).
    let private descriptionOf (el: XmlNode) : string option =
        XmlNode.element "description" el
        |> Option.bind (fun desc -> attrOpt "summary" desc)
        |> Option.map (fun s ->
            System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim())

    /// Parse an arg type string to ProtocolArgType.
    let private parseArgType (s: string) : ProtocolArgType =
        match s with
        | "int" -> Int
        | "uint" -> Uint
        | "fixed" -> Fixed
        | "string" -> String
        | "object" -> Object
        | "new_id" -> NewId
        | "fd" -> Fd
        | "array" -> Array
        | _ -> Uint  // fallback

    /// Parse an <arg> element.
    let private parseArg (el: XmlNode) : ProtocolArg = {
        Name = attr "name" "" el
        Type = parseArgType (attr "type" "uint" el)
        Interface = attrOpt "interface" el
        Enum = attrOpt "enum" el
        AllowNull = (attr "allow-null" "false" el) = "true"
        Summary = attrOpt "summary" el
    }

    /// Parse a <request> or <event> element.
    let private parseMessage (el: XmlNode) : ProtocolMessage = {
        Name = attr "name" "" el
        Args = XmlNode.elementsNamed "arg" el |> List.map parseArg
        IsDestructor = (attr "type" "" el) = "destructor"
        Since = match System.Int32.TryParse(attr "since" "1" el) with true, v -> v | _ -> 1
        Documentation = descriptionOf el
    }

    /// Parse an <entry> element within an <enum>.
    let private parseEnumEntry (el: XmlNode) : ProtocolEnumEntry = {
        Name = attr "name" "" el
        Value = attr "value" "0" el
        Summary = attrOpt "summary" el
    }

    /// Parse an <enum> element.
    let private parseEnum (el: XmlNode) : ProtocolEnum = {
        Name = attr "name" "" el
        Entries = XmlNode.elementsNamed "entry" el |> List.map parseEnumEntry
        IsBitfield = (attr "bitfield" "false" el) = "true"
        Documentation = descriptionOf el
    }

    /// Parse an <interface> element.
    let private parseInterface (el: XmlNode) : ProtocolInterface = {
        Name = attr "name" "" el
        Version = match System.Int32.TryParse(attr "version" "1" el) with true, v -> v | _ -> 1
        Requests = XmlNode.elementsNamed "request" el |> List.map parseMessage
        Events = XmlNode.elementsNamed "event" el |> List.map parseMessage
        Enums = XmlNode.elementsNamed "enum" el |> List.map parseEnum
        Documentation = descriptionOf el
    }

    /// Parse a protocol XML string to intermediate representation.
    let parseProtocolXml (xmlContent: string) : Result<Protocol, string> =
        match Xml.parse xmlContent with
        | Error e ->
            Error $"XML parse error: {e}"
        | Ok doc ->
            let root = doc.Root
            match XmlNode.name root with
            | Some "protocol" ->
                Ok {
                    Name = attr "name" "" root
                    Interfaces =
                        XmlNode.elementsNamed "interface" root
                        |> List.map parseInterface
                }
            | _ ->
                Error "Root element must be <protocol>"

    // =========================================================================
    // Type Declaration Mapping (Protocol IR → CppParser.Declaration list)
    // These are genuine type definitions that flow through FidelityCodeGenerator.
    // =========================================================================

    /// Convert a snake_case name to PascalCase.
    let private toPascalCase (s: string) =
        s.Split('_')
        |> Array.map (fun part ->
            if part.Length = 0 then ""
            else (string (System.Char.ToUpper part[0])) + part[1..])
        |> String.concat ""

    /// Map a ProtocolArgType to its C-level type string.
    let private argTypeToC (arg: ProtocolArg) : string =
        match arg.Type with
        | Int -> "int32_t"
        | Uint -> "uint32_t"
        | Fixed -> "wl_fixed_t"
        | String -> "const char *"
        | Object ->
            match arg.Interface with
            | Some iface -> $"{iface} *"
            | None -> "void *"
        | NewId ->
            match arg.Interface with
            | Some iface -> $"{iface} *"
            | None -> "void *"
        | Fd -> "int32_t"
        | Array -> "struct wl_array *"

    /// Generate the opaque handle typedef for an interface.
    /// Uses "struct {name}_opaque *" so isOpaqueHandleTypedef detects it:
    /// pointer to struct not in defined struct set → opaque handle record type.
    let private interfaceHandleTypedef (iface: ProtocolInterface) : CppParser.Declaration =
        CppParser.Declaration.Typedef {
            Name = iface.Name
            UnderlyingType = $"struct {iface.Name}_opaque *"
            Documentation = iface.Documentation
        }

    /// Generate delegate declarations for each event in an interface.
    let private eventDelegates (iface: ProtocolInterface) : CppParser.Declaration list =
        iface.Events |> List.map (fun event ->
            let ifacePascal = toPascalCase iface.Name
            let eventPascal = toPascalCase event.Name
            let delegateName = $"{ifacePascal}{eventPascal}Handler"
            let parameters =
                ("data", "void *")
                :: (iface.Name, $"{iface.Name} *")
                :: (event.Args |> List.map (fun a -> (a.Name, argTypeToC a)))
            CppParser.Declaration.Delegate {
                Name = delegateName
                Parameters = parameters
                ReturnType = "void"
                Documentation = event.Documentation
            })

    /// Generate the listener struct for an interface (one field per event).
    let private listenerStruct (iface: ProtocolInterface) : CppParser.Declaration option =
        if iface.Events.IsEmpty then None
        else
            let fields : CppParser.FieldDecl list =
                iface.Events |> List.map (fun event ->
                    let ifacePascal = toPascalCase iface.Name
                    let eventPascal = toPascalCase event.Name
                    let delegateTypeName = $"{ifacePascal}{eventPascal}Handler"
                    { Name = event.Name
                      Type = delegateTypeName
                      IsVolatile = false; IsConst = false
                      IsArray = false; ArraySize = None
                      IsBitfield = false; BitWidth = None })
            Some (CppParser.Declaration.Struct {
                Name = $"{iface.Name}_listener"
                Fields = fields
                Documentation = Some $"Event listener for {iface.Name}"
                IsUnion = false
            })

    /// Generate enum declarations for an interface.
    let private interfaceEnums (iface: ProtocolInterface) : CppParser.Declaration list =
        iface.Enums |> List.map (fun enum ->
            let enumName = $"{iface.Name}_{enum.Name}"
            let values =
                enum.Entries |> List.map (fun entry ->
                    let value =
                        if entry.Value.StartsWith("0x") || entry.Value.StartsWith("0X") then
                            match System.Int64.TryParse(entry.Value[2..], System.Globalization.NumberStyles.HexNumber, null) with
                            | true, v -> v
                            | _ -> 0L
                        else
                            match System.Int64.TryParse(entry.Value) with
                            | true, v -> v
                            | _ -> 0L
                    { CppParser.EnumValue.Name = entry.Name
                      CppParser.EnumValue.Value = value
                      CppParser.EnumValue.Documentation = entry.Summary })
            CppParser.Declaration.Enum {
                Name = enumName
                Values = values
                Documentation = enum.Documentation
                UnderlyingType = Some "uint32_t"
            })

    /// Extract type declarations from an interface (typedefs, enums, delegates, listener structs).
    /// These are genuine type definitions — NOT request functions.
    let interfaceTypeDeclarations (iface: ProtocolInterface) : CppParser.Declaration list =
        let handle = [ interfaceHandleTypedef iface ]
        let enums = interfaceEnums iface
        let delegates = eventDelegates iface
        let listener = listenerStruct iface |> Option.toList
        handle @ enums @ delegates @ listener

    /// Extract all type declarations from a protocol (no request functions).
    let toTypeDeclarations (protocol: Protocol) : CppParser.Declaration list =
        protocol.Interfaces |> List.collect interfaceTypeDeclarations

    // =========================================================================
    // Request Implementation Code Generation (Protocol IR → FsDecl list)
    // These are NOT extern declarations. They are implementation bodies that
    // call the core marshal function with the correct opcode and arguments.
    // =========================================================================

    open Farscape.Core.CodeAST

    /// Map a protocol arg type to the Clef FsType used in the generated function signature.
    /// All handles are nativeint in protocol dispatch (CCS FieldLabels workaround).
    let private argToFsType (_opaqueHandles: Set<string>) (arg: ProtocolArg) : FsType =
        match arg.Type with
        | Int -> Named "int32"
        | Uint -> Named "uint32"
        | Fixed -> Named "int32"  // wl_fixed_t is int32
        | String -> Named "nativeint"  // string pointer, passed as nativeint in arg array
        | Object -> Named "nativeint"  // all handles nativeint (CCS FieldLabels workaround)
        | NewId -> Named "nativeint"
        | Fd -> Named "int32"
        | Array -> Named "nativeint"  // wl_array pointer

    /// Convert a protocol arg value to nativeint for the argument array.
    let private argToNativeint (_opaqueHandles: Set<string>) (arg: ProtocolArg) : FsExpr =
        match arg.Type with
        | Int | Uint | Fixed | Fd ->
            TypeConversion("nativeint", Identifier arg.Name)
        | String | Array ->
            Identifier arg.Name
        | Object ->
            // All handles are nativeint in protocol dispatch (CCS FieldLabels workaround)
            Identifier arg.Name
        | NewId ->
            // NewId in the args array: value 0n (the C function assigns the real ID).
            // wl_closure_marshal reads args[i] for new_id but overwrites with allocated ID.
            Literal "0n"

    /// Generate FsDecl for a single protocol request.
    /// opcode = index of the request within the interface's request list.
    let private generateRequestDecl (iface: ProtocolInterface) (opcode: int) (request: ProtocolMessage) (config: MarshalConfig) (opaqueHandles: Set<string>) : FsDecl list =
        let funcName = $"{iface.Name}_{request.Name}"
        let interfaceSymbol = $"{iface.Name}_interface"

        // Determine if this is a constructor (has new_id arg)
        let newIdArg = request.Args |> List.tryFind (fun a -> a.Type = NewId)
        let isConstructor = newIdArg.IsSome
        let isUntypedNewId =
            match newIdArg with
            | Some a -> a.Interface.IsNone
            | None -> false

        // Build parameters: self + non-new_id args (+ interface/version for untyped new_id)
        // NOTE: All handles are nativeint in protocol dispatch to avoid CCS FieldLabels
        // pollution (28 records with { Handle: nativeint } cause "last definition wins"
        // inference cascade). Type safety is provided by function names + return types.
        // This will be reverted when .clefi signature-based loading lands.
        let selfType = Named "nativeint"
        let selfParam = { Name = "self"; Type = selfType }
        let regularArgs = request.Args |> List.filter (fun a -> a.Type <> NewId)
        let regularParams = regularArgs |> List.map (fun a -> { Name = a.Name; Type = argToFsType opaqueHandles a })

        let extraParams =
            if isUntypedNewId then
                // wl_registry_bind pattern: caller provides interface + version
                [ { Name = "``interface``"; Type = Named "nativeint" }
                  { Name = "version"; Type = Named "uint32" } ]
            else []

        let allParams = selfParam :: regularParams @ extraParams

        // Return type: all constructors return option<nativeint> (CCS FieldLabels workaround)
        let returnType =
            if isConstructor then Generic("option", Named "nativeint")
            else Named "unit"

        // Build the body
        let flags =
            if request.IsDestructor then TypeConversion("uint32", Literal $"{config.DestroyFlag}")
            else TypeConversion("uint32", Literal "0")

        let opcodeExpr = TypeConversion("uint32", Literal $"{opcode}")

        // Interface pointer: resolve via dlsym for typed new_id, or use caller-provided for untyped
        let interfaceExpr =
            if isConstructor && not isUntypedNewId then
                match newIdArg with
                | Some a ->
                    let targetIface = a.Interface |> Option.defaultValue iface.Name
                    MatchExpr(
                        FunctionCall("Fidelity.Libc.DynamicLink", "dlsym",
                            [FunctionCall("", "Some", [Literal "0n"])
                             FunctionCall("", "Some", [Literal $"\"{targetIface}_interface\".Pointer"])]),
                        [("Some v", Identifier "v"); ("None", Literal "0n")])
                | None -> Literal "0n"
            elif isUntypedNewId then
                Identifier "``interface``"
            else
                Literal "0n"

        // Self is always nativeint in protocol dispatch (no .Handle unwrap needed)
        let selfRaw = Identifier "self"

        // Version: get from proxy for normal requests, from caller for untyped new_id
        let versionExpr =
            if isUntypedNewId then
                Identifier "version"
            else
                FunctionCall("", config.VersionFunction, [FunctionCall("", "Some", [selfRaw])])

        // Build the args array from ALL args in their original positions.
        // wl_closure_marshal reads args[i] for every argument type including new_id,
        // so NewId entries must be present (value 0n — the C function assigns the real ID).
        // Only truly argument-less requests (no args at all) can pass None.
        let marshalArgs = request.Args

        // Constructor result expression: all return option<nativeint> (CCS FieldLabels workaround)
        let constructorResultExpr =
            MatchExpr(Identifier "result",
                [ ("Some v", FunctionCall("", "Some", [Identifier "v"]))
                  ("None", Identifier "None") ])

        let body =
            if marshalArgs.IsEmpty then
                // Truly no arguments (no regular args, no new_id): pass None for args array
                let marshalCall =
                    FunctionCall("", config.MarshalFunction,
                        [ FunctionCall("", "Some", [selfRaw])
                          opcodeExpr
                          FunctionCall("", "Some", [interfaceExpr])
                          versionExpr
                          flags
                          Identifier "None" ])
                if isConstructor then
                    LetIn("result", marshalCall, constructorResultExpr)
                else
                    // Void: call and ignore result
                    LetIn("_", marshalCall, Literal "()")
            elif isUntypedNewId then
                // Special: untyped new_id (e.g. wl_registry_bind)
                // libwayland internally prepends 3 extra args for untyped new_id:
                //   args[0..N-1] = regular args (e.g. 'name' for bind)
                //   args[N]     = interface_name (string pointer)
                //   args[N+1]   = interface_version (uint)
                //   args[N+2]   = 0 (new_id placeholder)
                let totalArgCount = regularArgs.Length + 3
                let allocSize = $"{totalArgCount * 8}"

                let writeRegularArgs =
                    regularArgs |> List.mapi (fun i arg ->
                        (i, FunctionCall("NativePtr", "set",
                            [ Identifier "argsPtr"; Literal $"{i}"; argToNativeint opaqueHandles arg ])))

                let baseIdx = regularArgs.Length
                // interface_name: cast interface pointer to get name string
                let writeIfaceName =
                    (baseIdx, FunctionCall("NativePtr", "set",
                        [ Identifier "argsPtr"; Literal $"{baseIdx}"; Identifier "``interface``" ]))
                // interface_version
                let writeIfaceVersion =
                    (baseIdx + 1, FunctionCall("NativePtr", "set",
                        [ Identifier "argsPtr"; Literal $"{baseIdx + 1}";
                          TypeConversion("nativeint", Identifier "version") ]))
                // new_id placeholder
                let writeNewId =
                    (baseIdx + 2, FunctionCall("NativePtr", "set",
                        [ Identifier "argsPtr"; Literal $"{baseIdx + 2}"; Literal "0n" ]))

                let allWrites = writeRegularArgs @ [ writeIfaceName; writeIfaceVersion; writeNewId ]

                let mallocCall =
                    FunctionCall("Fidelity.Libc.Memory", "malloc",
                        [ TypeConversion("unativeint", Literal allocSize) ])
                let allocExpr =
                    MatchExpr(mallocCall,
                        [ ("Some v", Identifier "v")
                          ("None", Literal "0n") ])
                let castExpr =
                    FunctionCall("NativePtr", "ofNativeInt",
                        [ Identifier "argsRaw" ])

                let marshalCall =
                    FunctionCall("", config.MarshalFunction,
                        [ FunctionCall("", "Some", [selfRaw])
                          opcodeExpr
                          FunctionCall("", "Some", [Identifier "``interface``"])
                          Identifier "version"
                          flags
                          FunctionCall("", "Some", [Identifier "argsRaw"]) ])

                let freeCall =
                    FunctionCall("Fidelity.Libc.Memory", "free",
                        [ FunctionCall("", "Some", [Identifier "argsRaw"]) ])

                let innerBody =
                    LetIn("result", marshalCall,
                        LetIn("_", freeCall, constructorResultExpr))

                let withWrites =
                    List.foldBack (fun (_, writeExpr) body ->
                        LetIn("_", writeExpr, body)) allWrites innerBody

                LetIn("argsRaw", allocExpr,
                    LetIn("argsPtr", castExpr, withWrites))
            else
                // Has arguments: need to construct wl_argument array
                // Each wl_argument is 8 bytes on LP64, all args fit in nativeint
                let argCount = marshalArgs.Length
                let allocSize = $"{argCount * 8}"
                // malloc → write each arg → marshal → free
                let writeArgs =
                    marshalArgs |> List.mapi (fun i arg ->
                        let offset = i * 8
                        // NativePtr.set on the buffer cast to nativeptr<nativeint>
                        let writeExpr =
                            FunctionCall("NativePtr", "set",
                                [ Identifier "argsPtr"; Literal $"{i}"; argToNativeint opaqueHandles arg ])
                        (i, writeExpr))

                // Build the sequential expression: alloc, write args, marshal, free
                // L1 malloc returns Option<nativeint> — unwrap for internal use
                let mallocCall =
                    FunctionCall("Fidelity.Libc.Memory", "malloc",
                        [ TypeConversion("unativeint", Literal allocSize) ])
                let allocExpr =
                    MatchExpr(mallocCall,
                        [ ("Some v", Identifier "v")
                          ("None", Literal "0n") ])
                let castExpr =
                    FunctionCall("NativePtr", "ofNativeInt",
                        [ Identifier "argsRaw" ])

                // Chain: let argsRaw = malloc(...) in let argsPtr = cast in write0; write1; ... marshal; free
                let marshalCall =
                    FunctionCall("", config.MarshalFunction,
                        [ FunctionCall("", "Some", [selfRaw])
                          opcodeExpr
                          FunctionCall("", "Some", [interfaceExpr])
                          versionExpr
                          flags
                          FunctionCall("", "Some", [Identifier "argsRaw"]) ])

                let freeCall =
                    FunctionCall("Fidelity.Libc.Memory", "free",
                        [ FunctionCall("", "Some", [Identifier "argsRaw"]) ])

                // Build nested let expressions for arg writes, then marshal + free
                let innerBody =
                    if isConstructor then
                        LetIn("result", marshalCall,
                            LetIn("_", freeCall, constructorResultExpr))
                    else
                        LetIn("_", marshalCall,
                            LetIn("_", freeCall, Literal "()"))

                // Wrap with arg writes
                let withWrites =
                    List.foldBack (fun (_, writeExpr) body ->
                        LetIn("_", writeExpr, body)) writeArgs innerBody

                // Wrap with cast and alloc
                LetIn("argsRaw", allocExpr,
                    LetIn("argsPtr", castExpr, withWrites))

        // Documentation
        let doc =
            let docText =
                match request.Documentation with
                | Some d -> d
                | None -> $"{iface.Name} request: {request.Name}"
            [ XmlDoc docText ]

        doc @ [ LetBinding(funcName, allParams, returnType, body, []) ]

    /// Generate all request implementation FsDecl for an interface.
    let interfaceRequestDecls (iface: ProtocolInterface) (config: MarshalConfig) (opaqueHandles: Set<string>) : FsDecl list =
        iface.Requests |> List.mapi (fun opcode request ->
            generateRequestDecl iface opcode request config opaqueHandles)
        |> List.concat

    /// Generate all request implementations for a protocol.
    let toRequestDecls (protocol: Protocol) (config: MarshalConfig) (opaqueHandles: Set<string>) : FsDecl list =
        protocol.Interfaces |> List.collect (fun iface ->
            interfaceRequestDecls iface config opaqueHandles)

    // =========================================================================
    // Combined Output
    // =========================================================================

    /// Produce both streams from a protocol:
    /// 1. Type declarations (→ CppParser.Declaration pipeline → FidelityCodeGenerator)
    /// 2. Request implementations (→ FsDecl directly, with marshal call bodies)
    let protocolToOutput (protocol: Protocol) (config: MarshalConfig) (opaqueHandles: Set<string>)
        : CppParser.Declaration list * FsDecl list =
        let typeDecls = toTypeDeclarations protocol
        let requestDecls = toRequestDecls protocol config opaqueHandles
        (typeDecls, requestDecls)

    // =========================================================================
    // Backward Compatibility (used by existing tests and toDeclarations callers)
    // =========================================================================

    /// Generate request function declarations for an interface.
    /// DEPRECATED: produces CppParser.Declaration.Function that becomes incorrect FidelityExtern.
    /// Use interfaceRequestDecls + MarshalConfig instead for correct output.
    let private requestFunctions (iface: ProtocolInterface) : CppParser.Declaration list =
        iface.Requests |> List.map (fun request ->
            let funcName = $"{iface.Name}_{request.Name}"
            let selfParam = ("self", $"{iface.Name} *")
            let newIdArg = request.Args |> List.tryFind (fun a -> a.Type = NewId)
            let returnType, otherArgs =
                match newIdArg with
                | Some a ->
                    let retType =
                        match a.Interface with
                        | Some iface -> $"{iface} *"
                        | None -> "void *"
                    retType, request.Args |> List.filter (fun arg -> arg.Type <> NewId)
                | None ->
                    "void", request.Args
            let parameters =
                selfParam :: (otherArgs |> List.map (fun a -> (a.Name, argTypeToC a)))
            CppParser.Declaration.Function {
                Name = funcName
                ReturnType = returnType
                Parameters = parameters
                Documentation = request.Documentation
                IsVirtual = false; IsStatic = false; IsInline = false
                Attributes = []
            })

    /// Convert a single interface to its complete set of declarations (types + request functions).
    /// DEPRECATED: request functions will produce incorrect FidelityExtern declarations.
    /// Use protocolToOutput with MarshalConfig for correct output.
    let interfaceToDeclarations (iface: ProtocolInterface) : CppParser.Declaration list =
        let handle = [ interfaceHandleTypedef iface ]
        let enums = interfaceEnums iface
        let delegates = eventDelegates iface
        let listener = listenerStruct iface |> Option.toList
        let requests = requestFunctions iface
        handle @ enums @ delegates @ listener @ requests

    /// Convert a complete protocol to Declaration list.
    /// DEPRECATED: see interfaceToDeclarations note.
    let toDeclarations (protocol: Protocol) : CppParser.Declaration list =
        protocol.Interfaces |> List.collect interfaceToDeclarations

    // =========================================================================
    // File Entry Point
    // =========================================================================

    /// Parse a protocol XML file and return the Protocol IR.
    /// Callers use protocolToOutput to get both type declarations and request implementations.
    let parseFile (xmlPath: string) : Result<Protocol, string> =
        try
            let content = System.IO.File.ReadAllText(xmlPath)
            parseProtocolXml content
        with ex ->
            Error $"Failed to read XML file '{xmlPath}': {ex.Message}"
