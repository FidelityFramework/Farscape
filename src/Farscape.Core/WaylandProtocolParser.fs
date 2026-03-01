namespace Farscape.Core

open Fidelity.Data.XML

/// Parses Wayland protocol XML files into the existing Declaration pipeline.
///
/// Wayland protocols are defined in XML (not C headers). The XML carries semantic
/// information — which args are `new_id` object creation, which are `fd` file descriptor
/// passing, event/request directionality — that would be lost by parsing wayland-scanner
/// C output. This module parses the XML directly, producing Declaration lists compatible
/// with the existing FidelityCodeGenerator pipeline.
///
/// Architecture: Fidelity.Data.XML handles XML syntax (same principle as CppParser using
/// System.Text.Json for clang JSON). This module handles Wayland protocol semantics.
module WaylandProtocolParser =

    // =========================================================================
    // Intermediate Representation (parsed from XML, before Declaration mapping)
    // =========================================================================

    /// Wayland protocol argument types (from XML `type` attribute).
    type WaylandArgType =
        | Int | Uint | Fixed | String | Object | NewId | Fd | Array

    /// A single argument in a request or event message.
    type WaylandArg = {
        Name: string
        Type: WaylandArgType
        /// Interface name for typed object/new_id args (e.g. "wl_surface")
        Interface: string option
        /// Enum reference for constrained int/uint args
        Enum: string option
        AllowNull: bool
        Summary: string option
    }

    /// A request or event message within an interface.
    type WaylandMessage = {
        Name: string
        Args: WaylandArg list
        IsDestructor: bool
        Since: int
        Documentation: string option
    }

    /// A single entry in an enum (named constant).
    type WaylandEnumEntry = {
        Name: string
        Value: string
        Summary: string option
    }

    /// An enumeration within an interface.
    type WaylandEnum = {
        Name: string
        Entries: WaylandEnumEntry list
        IsBitfield: bool
        Documentation: string option
    }

    /// A Wayland interface (e.g. wl_display, wl_surface).
    type WaylandInterface = {
        Name: string
        Version: int
        Requests: WaylandMessage list
        Events: WaylandMessage list
        Enums: WaylandEnum list
        Documentation: string option
    }

    /// A complete Wayland protocol (one .xml file).
    type WaylandProtocol = {
        Name: string
        Interfaces: WaylandInterface list
    }

    // =========================================================================
    // XML Parsing (Fidelity.Data.XML → Intermediate Representation)
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
    let private descriptionOf (el: XmlNode) : string option =
        XmlNode.element "description" el
        |> Option.bind (fun desc -> attrOpt "summary" desc)

    /// Parse an arg type string to WaylandArgType.
    let private parseArgType (s: string) : WaylandArgType =
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
    let private parseArg (el: XmlNode) : WaylandArg = {
        Name = attr "name" "" el
        Type = parseArgType (attr "type" "uint" el)
        Interface = attrOpt "interface" el
        Enum = attrOpt "enum" el
        AllowNull = (attr "allow-null" "false" el) = "true"
        Summary = attrOpt "summary" el
    }

    /// Parse a <request> or <event> element.
    let private parseMessage (el: XmlNode) : WaylandMessage = {
        Name = attr "name" "" el
        Args = XmlNode.elementsNamed "arg" el |> List.map parseArg
        IsDestructor = (attr "type" "" el) = "destructor"
        Since = match System.Int32.TryParse(attr "since" "1" el) with true, v -> v | _ -> 1
        Documentation = descriptionOf el
    }

    /// Parse an <entry> element within an <enum>.
    let private parseEnumEntry (el: XmlNode) : WaylandEnumEntry = {
        Name = attr "name" "" el
        Value = attr "value" "0" el
        Summary = attrOpt "summary" el
    }

    /// Parse an <enum> element.
    let private parseEnum (el: XmlNode) : WaylandEnum = {
        Name = attr "name" "" el
        Entries = XmlNode.elementsNamed "entry" el |> List.map parseEnumEntry
        IsBitfield = (attr "bitfield" "false" el) = "true"
        Documentation = descriptionOf el
    }

    /// Parse an <interface> element.
    let private parseInterface (el: XmlNode) : WaylandInterface = {
        Name = attr "name" "" el
        Version = match System.Int32.TryParse(attr "version" "1" el) with true, v -> v | _ -> 1
        Requests = XmlNode.elementsNamed "request" el |> List.map parseMessage
        Events = XmlNode.elementsNamed "event" el |> List.map parseMessage
        Enums = XmlNode.elementsNamed "enum" el |> List.map parseEnum
        Documentation = descriptionOf el
    }

    /// Parse a Wayland protocol XML string to intermediate representation.
    let parseProtocolXml (xmlContent: string) : Result<WaylandProtocol, string> =
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
    // Declaration Mapping (Intermediate Repr → CppParser.Declaration list)
    // =========================================================================

    /// Convert a snake_case name to PascalCase.
    /// "wl_surface" → "WlSurface", "enter" → "Enter"
    let private toPascalCase (s: string) =
        s.Split('_')
        |> Array.map (fun part ->
            if part.Length = 0 then ""
            else (string (System.Char.ToUpper part[0])) + part[1..])
        |> String.concat ""

    /// Map a WaylandArgType to its C-level type string.
    /// These strings are consumed by mapCTypeToFidelityType in the generation pipeline.
    let private argTypeToC (arg: WaylandArg) : string =
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
    /// e.g. wl_surface → Typedef { Name = "wl_surface"; UnderlyingType = "void *" }
    let private interfaceHandleTypedef (iface: WaylandInterface) : CppParser.Declaration =
        CppParser.Declaration.Typedef {
            Name = iface.Name
            UnderlyingType = "void *"
            Documentation = iface.Documentation
        }

    /// Generate delegate declarations for each event in an interface.
    /// Event "enter" on wl_surface → delegate WlSurfaceEnterHandler
    let private eventDelegates (iface: WaylandInterface) : CppParser.Declaration list =
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
    /// wl_surface with events [enter, leave] → struct wl_surface_listener { enter: ...; leave: ... }
    let private listenerStruct (iface: WaylandInterface) : CppParser.Declaration option =
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
    /// wl_display enum "error" → enum wl_display_error
    let private interfaceEnums (iface: WaylandInterface) : CppParser.Declaration list =
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

    /// Generate request function declarations for an interface.
    /// wl_surface request "attach" → Function wl_surface_attach(self, buffer, x, y)
    let private requestFunctions (iface: WaylandInterface) : CppParser.Declaration list =
        iface.Requests |> List.map (fun request ->
            let funcName = $"{iface.Name}_{request.Name}"
            // First parameter is always self (the interface handle)
            let selfParam = ("self", $"{iface.Name} *")
            // Determine return type: if request has a new_id arg with interface, that's the return
            let newIdArg = request.Args |> List.tryFind (fun a -> a.Type = NewId)
            let returnType, otherArgs =
                match newIdArg with
                | Some a ->
                    let retType =
                        match a.Interface with
                        | Some iface -> $"{iface} *"
                        | None -> "void *"
                    // Remove the new_id arg from the parameter list (it's the return)
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

    /// Convert a single interface to its complete set of declarations.
    /// Order: handle typedef, enums, delegates, listener struct, request functions.
    let interfaceToDeclarations (iface: WaylandInterface) : CppParser.Declaration list =
        let handle = [ interfaceHandleTypedef iface ]
        let enums = interfaceEnums iface
        let delegates = eventDelegates iface
        let listener = listenerStruct iface |> Option.toList
        let requests = requestFunctions iface
        handle @ enums @ delegates @ listener @ requests

    /// Convert a complete WaylandProtocol to Declaration list.
    let toDeclarations (protocol: WaylandProtocol) : CppParser.Declaration list =
        protocol.Interfaces |> List.collect interfaceToDeclarations

    // =========================================================================
    // File Entry Point
    // =========================================================================

    /// Parse a Wayland protocol XML file and return Declaration list.
    /// Main entry point for the BindingGenerator pipeline.
    let parseFile (xmlPath: string) : Result<CppParser.Declaration list, string> =
        try
            let content = System.IO.File.ReadAllText(xmlPath)
            match parseProtocolXml content with
            | Ok protocol -> Ok (toDeclarations protocol)
            | Error e -> Error e
        with ex ->
            Error $"Failed to read XML file '{xmlPath}': {ex.Message}"
