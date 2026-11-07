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
        /// The `type` attribute exactly as the XML declares it (None when absent); `Type`
        /// falls back to Uint for a missing or unknown attribute, so wire-type evidence reads this.
        TypeAttribute: string option
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
        /// The `value` attribute exactly as the XML writes it; `entryValue` reads it (#160).
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
        TypeAttribute = attrOpt "type" el
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

    /// Parse an <entry> element within an <enum>. Its value is kept as written: an entry
    /// without one is refused before any protocol is built (`requireEntryValues`), never read
    /// as 0.
    let private parseEnumEntry (el: XmlNode) : ProtocolEnumEntry = {
        Name = attr "name" "" el
        Value = attr "value" "" el
        Summary = attrOpt "summary" el
    }

    /// The Wayland message definition language gives an enum entry's value as "An integer
    /// value. The value can be given in decimal, hexadecimal, or octal representation."
    /// (https://wayland.pages.freedesktop.org/wayland.freedesktop.org/docs/book/Message_XML.html),
    /// and wayland-scanner emits it verbatim as a C enumerator's value (`FX_K = -010,`), so an
    /// entry value is a C integer constant, optionally negated by unary minus, and its value is
    /// the one C gives it:
    /// - decimal: `0`, or a nonzero digit followed by digits;
    /// - hexadecimal: `0x` or `0X` followed by hexadecimal digits;
    /// - octal: `0` followed by octal digits 0-7 (`010` is eight); a leading `0` followed by
    ///   any other digit is malformed octal;
    /// - each with an optional leading `-` (`-010` is -8, `-0x1` is -1, `-0` is 0).
    /// C has no negative constants: `-` applies to the constant's type (C11 6.5.3.3), and an
    /// unsuffixed constant's type is the first that holds it (C11 6.4.4.1): int, long, long long
    /// for decimal; int, unsigned int, long, unsigned long, long long, unsigned long long for
    /// hexadecimal and octal. Negating an unsigned constant wraps (C11 6.2.5), so `-0x80000000`
    /// is 2147483648 and `-0x8000000000000000` is 9223372036854775808 in the C wayland-scanner
    /// emits, not a negative value; a decimal constant above 2^63-1 has no standard type. Such a
    /// negation is refused rather than read as the value its digits suggest, so the most negative
    /// value a single constant gives is -(2^63-1). With int 32 bits, as Wayland's C requires, a
    /// hexadecimal or octal magnitude is signed exactly when it is at most 2^31-1, or from 2^32
    /// to 2^63-1 (long or long long under LP64, LLP64 and ILP32 alike).
    /// The value is read into the signed 64-bit carrier enum values have; a value outside it is
    /// refused, never wrapped. Anything else is the reason it cannot be read.
    let entryValue (text: string) : Result<int64, string> =
        let negative = text.StartsWith "-"
        let constant = if negative then text.Substring 1 else text
        let matches (pattern: string) = System.Text.RegularExpressions.Regex.IsMatch(constant, pattern)
        let int64Max = bigint System.Int64.MaxValue
        let outside = Error $"value '{text}', outside the signed 64-bit range enum values carry"
        let magnitudeOf (radix: int) (digits: string) =
            digits |> Seq.fold (fun (acc: bigint) c -> acc * bigint radix + bigint (System.Convert.ToInt32(string c, 16))) 0I
        // The constant's magnitude, negated when C's type for it is signed.
        let read (decimal: bool) (magnitude: bigint) =
            if not negative then
                if magnitude > int64Max then outside else Ok (int64 magnitude)
            elif decimal then
                if magnitude > int64Max then
                    Error $"value '{text}', whose constant '{constant}' has no standard C type (an unsuffixed decimal constant is int, long or long long, C11 6.4.4.1), so the value wayland-scanner's C gives it is not -{constant}"
                else Ok (int64 (-magnitude))
            elif magnitude <= bigint System.Int32.MaxValue || (magnitude >= 4294967296I && magnitude <= int64Max) then
                Ok (int64 (-magnitude))
            elif magnitude > 18446744073709551615I then outside
            else
                Error $"value '{text}', whose constant '{constant}' C types as unsigned (C11 6.4.4.1), so the negation wayland-scanner emits wraps (C11 6.2.5) instead of giving a negative value"
        if matches @"\A(0|[1-9][0-9]*)\z" then read true (magnitudeOf 10 constant)
        elif matches @"\A0[xX][0-9a-fA-F]+\z" then read false (magnitudeOf 16 (constant.Substring 2))
        elif matches @"\A0[0-7]+\z" then read false (magnitudeOf 8 (constant.Substring 1))
        elif matches @"\A0[0-9]+\z" then
            Error $"value '{text}', which begins with 0 as an octal integer does but has a digit outside 0-7"
        else
            Error $"value '{text}', which is not a decimal, 0x hexadecimal or 0-prefixed octal integer, optionally negated, the forms the Wayland protocol definition gives an entry value in"

    /// The refusal of an entry whose value cannot be read (FS9130), naming the protocol, the
    /// enum (qualified by its interface), the entry and why. Fidelity.Data.XML keeps no line
    /// positions, so the source is the protocol file alone, when one is known.
    let private entryValueRefusal (source: string option) (protocol: string) (iface: string) (enum: string) (entry: string) (why: string) =
        let located =
            match source with
            | Some path -> Diagnostics.inFile path "Fidelity.Data.XML keeps no line positions for protocol elements"
            | None -> Diagnostics.Located.Unknown $"protocol '{protocol}' was read from text with no file"
        Diagnostics.diagnostic Diagnostics.ProtocolEnumValueMalformed
            $"protocol '{protocol}' enum '{iface}.{enum}' entry '{entry}' has {why}; an entry value is never read as 0."
            located

    /// Every enum entry of a protocol document has a value `entryValue` reads, or the refusals
    /// of those that do not, in document order.
    let private requireEntryValues (source: string option) (protocol: string) (root: XmlNode) : Diagnostics.Diagnostic list =
        [ for iface in XmlNode.elementsNamed "interface" root do
            for enum in XmlNode.elementsNamed "enum" iface do
                for entry in XmlNode.elementsNamed "entry" enum do
                    let refuse = entryValueRefusal source protocol (attr "name" "" iface) (attr "name" "" enum) (attr "name" "" entry)
                    match XmlNode.attr "value" entry with
                    | None -> yield refuse "no value attribute, which wayland.dtd requires (<!ATTLIST entry value CDATA #REQUIRED>)"
                    | Some text ->
                        match entryValue text with
                        | Ok _ -> ()
                        | Error why -> yield refuse why ]

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

    /// Parse protocol XML read from `source` (its file, when known) to intermediate
    /// representation. A protocol with an enum entry whose value cannot be read is refused
    /// (FS9130, #160), every such entry named.
    let parseProtocolXmlFrom (source: string option) (xmlContent: string) : Result<Protocol, Diagnostics.Refusal> =
        match Xml.parse xmlContent with
        | Error e ->
            Error (Diagnostics.uncoded $"XML parse error: {e}")
        | Ok doc ->
            let root = doc.Root
            match XmlNode.name root with
            | Some "protocol" ->
                let name = attr "name" "" root
                match requireEntryValues source name root with
                | [] ->
                    Ok {
                        Name = name
                        Interfaces =
                            XmlNode.elementsNamed "interface" root
                            |> List.map parseInterface
                    }
                | refusals -> Error (Diagnostics.refusalsOf refusals "")
            | _ ->
                Error (Diagnostics.uncoded "Root element must be <protocol>")

    /// Parse a protocol XML string to intermediate representation.
    let parseProtocolXml (xmlContent: string) : Result<Protocol, string> =
        parseProtocolXmlFrom None xmlContent |> Result.mapError _.Text

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
            Attributes = []
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
                      IsBitfield = false; BitWidth = None; Attributes = [] })
            Some (CppParser.Declaration.Struct {
                Name = $"{iface.Name}_listener"
                Fields = fields
                Documentation = Some $"Event listener for {iface.Name}"
                IsUnion = false
                Attributes = []
                Anonymous = None
                Measured = None
                Unattributed = None
                UnnamedBitFields = []
                TypedefAttributes = []
                Location = Diagnostics.unlocated $"listener record of protocol interface '{iface.Name}', which Farscape synthesizes from its events"
            })

    /// The wire representation of a protocol enum, read from the protocol's own declarations.
    /// The Wayland wire format carries an enum only as the argument that references it
    /// (`enum="name"` within its interface, `enum="iface.name"` elsewhere), and that argument's
    /// declared `type` fixes the wire value: `uint` is 32-bit unsigned, `int` 32-bit signed.
    /// Every referencing argument across `interfaces` must declare the same one of these;
    /// otherwise, or with no referencing argument, the enum has no declared wire type.
    let private protocolEnumRepresentation (interfaces: ProtocolInterface list)
                                           (iface: ProtocolInterface) (enum: ProtocolEnum) : CppParser.EnumRepresentationSource =
        let qualified = $"{iface.Name}.{enum.Name}"
        let references =
            interfaces |> List.collect (fun owner ->
                (owner.Requests @ owner.Events) |> List.collect (fun message ->
                    message.Args |> List.choose (fun arg ->
                        match arg.Enum with
                        | Some name when name = qualified || (owner.Name = iface.Name && name = enum.Name) ->
                            Some ($"{owner.Name}.{message.Name} arg '{arg.Name}'", arg.TypeAttribute)
                        | _ -> None)))
        match references |> List.map snd |> List.distinct with
        | [] -> CppParser.ProtocolUndeclared $"no protocol argument references enum '{qualified}'"
        | [ Some ("uint" | "int" as wire) ] ->
            let first, _ = List.head references
            let evidence = $"{references.Length} protocol argument(s) reference enum '{qualified}', first {first}, each with type=\"{wire}\""
            CppParser.ProtocolWire ({ Bits = 32; IsSigned = (wire = "int") }, evidence)
        | declared ->
            let spelled = declared |> List.map (function Some t -> $"type=\"{t}\"" | None -> "no type attribute") |> String.concat ", "
            CppParser.ProtocolUndeclared $"the protocol arguments referencing enum '{qualified}' declare {spelled}, not one of type=\"uint\" or type=\"int\""

    /// Generate enum declarations for an interface; wire-type evidence is read from `interfaces`.
    let private interfaceEnums (protocolName: string) (interfaces: ProtocolInterface list) (iface: ProtocolInterface) : CppParser.Declaration list =
        iface.Enums |> List.map (fun enum ->
            let enumName = $"{iface.Name}_{enum.Name}"
            let values =
                enum.Entries |> List.map (fun entry ->
                    // A parsed protocol's entries all read (`requireEntryValues`); an entry of a
                    // protocol built otherwise is refused the same way, never read as 0.
                    let value =
                        match entryValue entry.Value with
                        | Ok v -> v
                        | Error why -> Diagnostics.refuse (entryValueRefusal None protocolName iface.Name enum.Name entry.Name why)
                    { CppParser.EnumValue.Name = entry.Name
                      CppParser.EnumValue.Value = value
                      CppParser.EnumValue.Documentation = entry.Summary })
            CppParser.Declaration.Enum {
                Name = enumName
                Values = values
                Documentation = enum.Documentation
                // The protocol declares no C underlying type; its wire type is the representation.
                UnderlyingType = None
                Representation = protocolEnumRepresentation interfaces iface enum
                Location = Diagnostics.unlocated $"protocol enum '{iface.Name}.{enum.Name}' of protocol '{protocolName}': Fidelity.Data.XML keeps no line positions for protocol elements"
            })

    /// Extract type declarations from an interface (typedefs, enums, delegates, listener structs).
    /// These are genuine type definitions — NOT request functions.
    let interfaceTypeDeclarationsIn (protocol: Protocol) (iface: ProtocolInterface) : CppParser.Declaration list =
        let handle = [ interfaceHandleTypedef iface ]
        let enums = interfaceEnums protocol.Name protocol.Interfaces iface
        let delegates = eventDelegates iface
        let listener = listenerStruct iface |> Option.toList
        handle @ enums @ delegates @ listener

    /// Type declarations of a lone interface: its enums' wire-type evidence is limited to that
    /// interface's own arguments, which then stand as the whole protocol scope.
    let interfaceTypeDeclarations (iface: ProtocolInterface) : CppParser.Declaration list =
        interfaceTypeDeclarationsIn { Name = iface.Name; Interfaces = [ iface ] } iface

    /// Extract all type declarations from a protocol (no request functions).
    let toTypeDeclarations (protocol: Protocol) : CppParser.Declaration list =
        protocol.Interfaces |> List.collect (interfaceTypeDeclarationsIn protocol)

    // =========================================================================
    // Retired Request Projection Entry Points
    // =========================================================================

    // The removed raw marshal generator typed every protocol object as nativeint
    // and expected to restore handle records once signature-based loading landed.
    // That expectation is withdrawn: namespace-scoped inference is separate
    // compiler design work; it does not authorize changing the boundary
    // representation to a handle record or an integer address. A protocol object
    // crosses as CHandle<_>, or option<CHandle<_>> where it may be absent, and its
    // pointer width and passing mode belong to the descriptor (clef-lang-spec
    // ffi-boundary.md §5.3). Neither retired projection admits a typed protocol
    // dispatch contract; the former generator and synthetic extern projection
    // have been removed.

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
    let parseFileDiagnosed (xmlPath: string) : Result<Protocol, Diagnostics.Refusal> =
        match (try Ok (System.IO.File.ReadAllText(xmlPath)) with ex -> Error ex.Message) with
        | Error message -> Error (Diagnostics.uncoded $"Failed to read XML file '{xmlPath}': {message}")
        | Ok content -> parseProtocolXmlFrom (Some (System.IO.Path.GetFullPath xmlPath)) content

    /// `parseFileDiagnosed`, reporting a failure as its text.
    let parseFile (xmlPath: string) : Result<Protocol, string> =
        parseFileDiagnosed xmlPath |> Result.mapError _.Text
