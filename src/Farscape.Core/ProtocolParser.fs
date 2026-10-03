namespace Farscape.Core

open Fidelity.Data.XML

/// Parses XML protocol definitions into protocol metadata.
///
/// Fidelity.Data.XML handles XML syntax; this module retains argument kinds,
/// event and request direction, interface names, and enum metadata. Type-only
/// declarations remain available for the typed boundary pipeline.
///
/// Request implementations require an admitted typed protocol dispatch contract.
/// The legacy raw marshal and synthetic extern projections are diagnostic-only;
/// typed implementations belong to TypedProtocolGenerator.
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
    /// A pointer to an undeclared struct is recognized as an opaque nominal
    /// marker by FidelityCodeGenerator; the boundary carrier is CHandle<marker>.
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
    // Retired Request Projection Entry Points
    // =========================================================================

    // The former generator and synthetic extern projection have been removed;
    // neither retired representation admits a typed protocol dispatch contract.

    let private requireTypeOnly (entryPoint: string) (interfaces: ProtocolInterface list) =
        match interfaces |> List.tryPick (fun iface ->
            iface.Requests |> List.tryHead |> Option.map (fun request -> iface.Name, request.Name)) with
        | Some (interfaceName, requestName) ->
            invalidOp
                $"ProtocolParser.{entryPoint} cannot generate XML request '{interfaceName}.{requestName}': legacy marshal and synthetic extern projection is retired. Use TypedProtocolGenerator with an admitted typed protocol dispatch contract, or toTypeDeclarations for protocol type metadata."
        | None -> ()

    /// Retired request implementation projection. Request-bearing interfaces
    /// fail before returning an AST; request-free interfaces have no bodies.
    let interfaceRequestDecls (iface: ProtocolInterface) (_config: MarshalConfig) (_opaqueHandles: Set<string>) : CodeAST.FsDecl list =
        requireTypeOnly "interfaceRequestDecls" [ iface ]
        []

    /// Retired request implementation projection. Validate the entire protocol
    /// before returning any AST, including protocols whose first interface is request-free.
    let toRequestDecls (protocol: Protocol) (_config: MarshalConfig) (_opaqueHandles: Set<string>) : CodeAST.FsDecl list =
        requireTypeOnly "toRequestDecls" protocol.Interfaces
        []

    /// Retired combined projection. Request-free protocols retain their type
    /// metadata; request-bearing protocols require the typed dispatch generator.
    let protocolToOutput (protocol: Protocol) (_config: MarshalConfig) (_opaqueHandles: Set<string>)
        : CppParser.Declaration list * CodeAST.FsDecl list =
        requireTypeOnly "protocolToOutput" protocol.Interfaces
        (toTypeDeclarations protocol, [])

    /// Retired synthetic request extern projection. Request-free interfaces
    /// retain metadata; XML request names must not be invented as native symbols.
    let interfaceToDeclarations (iface: ProtocolInterface) : CppParser.Declaration list =
        requireTypeOnly "interfaceToDeclarations" [ iface ]
        interfaceTypeDeclarations iface

    /// Retired synthetic request extern projection. Validate every interface
    /// before returning metadata; use toTypeDeclarations for request-bearing XML.
    let toDeclarations (protocol: Protocol) : CppParser.Declaration list =
        requireTypeOnly "toDeclarations" protocol.Interfaces
        toTypeDeclarations protocol

    // =========================================================================
    // File Entry Point
    // =========================================================================

    /// Parse a protocol XML file and return the Protocol IR.
    /// Callers retain the IR or extract type metadata with toTypeDeclarations.
    let parseFile (xmlPath: string) : Result<Protocol, string> =
        try
            let content = System.IO.File.ReadAllText(xmlPath)
            parseProtocolXml content
        with ex ->
            Error $"Failed to read XML file '{xmlPath}': {ex.Message}"
