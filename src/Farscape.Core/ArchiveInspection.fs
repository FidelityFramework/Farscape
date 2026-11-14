namespace Farscape.Core

open System
open Fidelity.Data.TOML

/// Static archive inspection (#204). A pilot's `[library.artifact]` names a static archive by
/// path and SHA-256. `inspect` reads the file as a GNU `ar` archive, classifies each member
/// (ELF64 little-endian relocatable object, LLVM bitcode, LLVM bitcode wrapper), reads the
/// symbols each member defines, and returns them with the archive's and each member's SHA-256.
/// ELF members are read in managed code; bitcode members are read by `llvm-nm`. Input the
/// inspector does not admit is refused with its own code (FS9224 to FS9230): no member is
/// skipped and no symbol is assumed.
module ArchiveInspection =

    /// The object format of one archive member.
    type MemberFormat =
        /// An ELF64 little-endian relocatable object (`ET_REL`).
        | ElfRelocatable
        /// Raw LLVM bitcode (`BC 0xC0DE`).
        | LlvmBitcode
        /// LLVM bitcode in its wrapper (`0x0B17C0DE`).
        | LlvmBitcodeWrapper

    /// How a member defines a symbol: `STB_GLOBAL` (or common), or `STB_WEAK`.
    type SymbolBinding =
        | GlobalStrong
        | Weak

    /// One symbol a member defines, with the member that defines it.
    type DefinedSymbol = { Name: string; MemberName: string; MemberIndex: int; Binding: SymbolBinding }

    /// One archive member. `Index` counts object members from 0 in archive order; the symbol
    /// index and extended name table are not members. `Offset` and `Size` locate the member's
    /// payload in the archive file (after its header and any BSD inline name), and `Sha256` is
    /// the lowercase hex SHA-256 of those payload bytes.
    type ArchiveMember = { Index: int; Name: string; Offset: int64; Size: int64; Format: MemberFormat; Sha256: string; DefinedSymbols: DefinedSymbol list }

    /// A tool by its absolute path and the lowercase hex SHA-256 of its file.
    type ToolIdentity = { Path: string; Sha256: string }

    /// How a companion archive was built: its source, the compiler and archiver by identity,
    /// the compiler arguments and the output archive.
    type CompanionArchiveRecipe = { Source: string; SourceSha256: string; Compiler: ToolIdentity; Arguments: string list; Archiver: ToolIdentity; Output: string }

    /// What inspection found. `Path` is the path as given, `ResolvedPath` its absolute form,
    /// `Sha256` the archive file's lowercase hex SHA-256. `Symbols` holds one definition per
    /// symbol name: the strong definition when one exists, otherwise the first weak one.
    /// `BitcodeTool` is the `llvm-nm` that read the bitcode members; None when there are none.
    type ArchiveProvenance = { Path: string; ResolvedPath: string; Sha256: string; Members: ArchiveMember list; Symbols: Map<string, DefinedSymbol>; BitcodeTool: ToolIdentity option }

    // =========================================================================
    // Refusals
    // =========================================================================

    /// An archive refusal's source is the archive file; a binary file has no line or column.
    let private archiveSource (archive: string) : Diagnostics.Located<Diagnostics.SourceLocation> =
        Diagnostics.inFile archive "a static archive is a binary file with no line or column"

    let private refuseAt (code: string) (archive: string) (message: string) : 'T =
        Diagnostics.diagnostic code message (archiveSource archive) |> Diagnostics.refuse

    let private malformed (archive: string) (message: string) : 'T =
        refuseAt Diagnostics.ArchiveMalformed archive message

    let private memberMalformed (archive: string) (memberName: string) (message: string) : 'T =
        malformed archive $"archive member '{memberName}': {message}"

    let private memberUnsupported (archive: string) (memberName: string) (message: string) : 'T =
        refuseAt Diagnostics.ArchiveMemberFormatUnsupported archive $"archive member '{memberName}' {message}"

    let private toolRefused (archive: string) (message: string) : 'T =
        refuseAt Diagnostics.ArchiveBitcodeToolUnavailable archive message

    // =========================================================================
    // llvm-nm resolution
    // =========================================================================

    /// Resolve the `llvm-nm` that reads LLVM bitcode members.
    module LlvmNmToolchain =

        let private unavailable (message: string) : Result<ToolIdentity, Diagnostics.Refusal> =
            Diagnostics.diagnostic Diagnostics.ArchiveBitcodeToolUnavailable message
                (Diagnostics.Located.Unknown "llvm-nm is selected by the environment, not by a declaration")
            |> Diagnostics.refusalOf
            |> Error

        /// `FIDELITY_LLVM_NM` selects the tool file, else `FIDELITY_LLVM_BIN` selects the
        /// directory holding `llvm-nm`, else `PATH` is searched. Explicit configuration must
        /// identify an existing file: a configured path that does not exist is FS9230, and no
        /// later source is consulted in its place.
        let resolveWith (environment: string -> string option) (exists: string -> bool) : Result<ToolIdentity, Diagnostics.Refusal> =
            let configured name = environment name |> Option.filter (String.IsNullOrWhiteSpace >> not)
            let identified (path: string) : Result<ToolIdentity, Diagnostics.Refusal> =
                try Ok { ToolIdentity.Path = IO.Path.GetFullPath path; Sha256 = ClangToolchain.sha256 path }
                with
                | :? IO.IOException as ex -> unavailable $"llvm-nm '{path}' cannot be read: {ex.Message}"
                | :? UnauthorizedAccessException as ex -> unavailable $"llvm-nm '{path}' cannot be read: {ex.Message}"
            let selected (name: string) (path: string) : Result<ToolIdentity, Diagnostics.Refusal> =
                if exists path then identified path
                else unavailable $"{name} selects '{path}', which does not exist; no other llvm-nm is searched for."
            match configured "FIDELITY_LLVM_NM" with
            | Some path -> selected "FIDELITY_LLVM_NM" path
            | None ->
                match configured "FIDELITY_LLVM_BIN" with
                | Some directory -> selected "FIDELITY_LLVM_BIN" (IO.Path.Combine(directory, "llvm-nm"))
                | None ->
                    let directories =
                        match configured "PATH" with
                        | Some path -> path.Split(IO.Path.PathSeparator) |> List.ofArray
                        | None -> []
                    let found =
                        directories
                        |> List.map (fun directory -> IO.Path.Combine((if directory = "" then "." else directory), "llvm-nm"))
                        |> List.tryFind exists
                    match found with
                    | Some path -> identified path
                    | None -> unavailable "llvm-nm was not found on PATH; set FIDELITY_LLVM_NM or FIDELITY_LLVM_BIN."

        /// `resolveWith` over the process environment and the file system.
        let resolve () : Result<ToolIdentity, Diagnostics.Refusal> =
            resolveWith (fun name -> Environment.GetEnvironmentVariable name |> Option.ofObj) IO.File.Exists

    // =========================================================================
    // Bytes
    // =========================================================================

    let private archiveMagic = "!<arch>\n"B
    let private thinMagic = "!<thin>\n"B
    let private elfMagic = [| 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy |]
    let private bitcodeMagic = [| 0x42uy; 0x43uy; 0xC0uy; 0xDEuy |]
    let private bitcodeWrapperMagic = [| 0xDEuy; 0xC0uy; 0x17uy; 0x0Buy |]

    /// Whether the `count` bytes at `start` begin with `prefix`.
    let private hasPrefix (bytes: byte[]) (start: int) (count: int) (prefix: byte[]) : bool =
        count >= prefix.Length && Array.forall2 (=) prefix (Array.sub bytes start prefix.Length)

    let private u16At (bytes: byte[]) (at: int) : uint16 =
        Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(bytes, at, 2))

    let private u32At (bytes: byte[]) (at: int) : uint32 =
        Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(bytes, at, 4))

    let private u64At (bytes: byte[]) (at: int) : uint64 =
        Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(bytes, at, 8))

    /// The lowercase hex SHA-256 of the `count` bytes at `start`.
    let private sha256Of (bytes: byte[]) (start: int) (count: int) : string =
        Security.Cryptography.SHA256.HashData(ReadOnlySpan<byte>(bytes, start, count))
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    let private isSha256Hex (text: string) : bool =
        text.Length = 64 && text |> Seq.forall Uri.IsHexDigit

    /// A decoder that raises on bytes that are not UTF-8, so no name is silently altered.
    let private strictUtf8 = Text.UTF8Encoding(false, true)

    let private textOf (archive: string) (what: string) (bytes: byte[]) (start: int) (count: int) : string =
        try strictUtf8.GetString(bytes, start, count)
        with :? Text.DecoderFallbackException -> malformed archive $"{what} is not UTF-8 text"

    /// An unsigned decimal of at most ten digits: the width of an `ar` size field.
    let private isDecimal (text: string) : bool =
        text.Length > 0 && text.Length <= 10 && text |> Seq.forall (fun c -> c >= '0' && c <= '9')

    let private decimalOf (text: string) : int64 =
        Int64.Parse(text, Globalization.CultureInfo.InvariantCulture)

    // =========================================================================
    // GNU ar member walk
    // =========================================================================

    /// The text of a fixed-width, space-padded member header field.
    let private headerField (archive: string) (bytes: byte[]) (header: int) (offset: int) (width: int) (what: string) : string =
        let mutable count = width
        while count > 0 && bytes[header + offset + count - 1] = 0x20uy do
            count <- count - 1
        textOf archive $"the {what} field of the member header at byte {header}" bytes (header + offset) count

    /// Each object member's name and payload (start and byte count in `bytes`), in archive
    /// order. The symbol index (`/`, `/SYM64/`) and the extended name table (`//`) are read
    /// past; `/<offset>` names resolve through the extended name table and `#1/<length>` names
    /// through the payload's first bytes. A truncated or malformed header, a payload outside
    /// the file, or a name that does not resolve is FS9224.
    let private readMembers (archive: string) (bytes: byte[]) : (string * int * int) list =
        let length = int64 bytes.Length
        let members = ResizeArray<string * int * int>()
        let mutable extendedNames : (int * int) option = None
        let mutable position = 8L
        while position < length do
            if length - position < 60L then
                malformed archive $"the member header at byte {position} is truncated: {length - position} of 60 bytes remain"
            let header = int position
            if bytes[header + 58] <> 0x60uy || bytes[header + 59] <> 0x0Auy then
                malformed archive $"the member header at byte {position} does not end with the 0x60 0x0A magic"
            let nameField = headerField archive bytes header 0 16 "name"
            let sizeField = headerField archive bytes header 48 10 "size"
            if not (isDecimal sizeField) then
                malformed archive $"the member header at byte {position} has size field '{sizeField}', which is no decimal byte count"
            let size = decimalOf sizeField
            let dataStart = position + 60L
            if size > length - dataStart then
                malformed archive $"the member '{nameField}' at byte {position} declares {size} payload bytes, but {length - dataStart} remain in the archive"
            let payloadStart = int dataStart
            let payloadCount = int size
            if nameField = "/" || nameField = "/SYM64/" || nameField = "__.SYMDEF" || nameField = "__.SYMDEF SORTED" then
                // The archive symbol index (SysV/GNU or BSD ranlib): a lookup aid, not an object member.
                ()
            elif nameField = "//" then
                if extendedNames.IsSome then
                    malformed archive $"the archive has a second extended name table at byte {position}"
                extendedNames <- Some (payloadStart, payloadCount)
            elif nameField.StartsWith("#1/", StringComparison.Ordinal) then
                // BSD extended name: the name is the payload's first <length> bytes, NUL-padded.
                let lengthText = nameField.Substring 3
                if not (isDecimal lengthText) then
                    malformed archive $"the member header at byte {position} has BSD extended name '{nameField}', whose length is no decimal byte count"
                let nameLength = decimalOf lengthText
                if nameLength = 0L || nameLength > size then
                    malformed archive $"the member header at byte {position} has BSD extended name length {nameLength}, outside its {size}-byte payload"
                let nameCount = int nameLength
                let mutable visible = nameCount
                while visible > 0 && bytes[payloadStart + visible - 1] = 0uy do
                    visible <- visible - 1
                if visible = 0 then
                    malformed archive $"the member header at byte {position} has an empty BSD extended name"
                let name = textOf archive $"the BSD extended name of the member at byte {position}" bytes payloadStart visible
                if name = "__.SYMDEF" || name = "__.SYMDEF SORTED" then
                    // BSD ranlib symbol table stored with an extended member name.
                    ()
                else
                    members.Add((name, payloadStart + nameCount, payloadCount - nameCount))
            elif nameField.StartsWith("/", StringComparison.Ordinal) then
                // GNU extended name: /<offset> into the extended name table.
                let offsetText = nameField.Substring 1
                if not (isDecimal offsetText) then
                    malformed archive $"the member header at byte {position} has name '{nameField}', which is no member name, symbol index or extended name reference"
                match extendedNames with
                | None ->
                    malformed archive $"the member header at byte {position} names extended name '{nameField}', but no extended name table precedes it"
                | Some (tableStart, tableCount) ->
                    let offset = decimalOf offsetText
                    if offset >= int64 tableCount then
                        malformed archive $"the member header at byte {position} names extended name offset {offset}, outside the {tableCount}-byte extended name table"
                    let first = tableStart + int offset
                    let limit = tableStart + tableCount
                    let mutable finish = first
                    while finish < limit && bytes[finish] <> 0x0Auy do
                        finish <- finish + 1
                    if finish = limit then
                        malformed archive $"the extended name at table offset {offset} is not terminated by a newline"
                    let last = if finish > first && bytes[finish - 1] = 0x2Fuy then finish - 1 else finish
                    if last = first then
                        malformed archive $"the extended name at table offset {offset} is empty"
                    let name = textOf archive $"the extended name at table offset {offset}" bytes first (last - first)
                    members.Add((name, payloadStart, payloadCount))
            else
                // An ordinary name; GNU ar terminates it with one '/'.
                let name =
                    if nameField.EndsWith("/", StringComparison.Ordinal) then nameField.Substring(0, nameField.Length - 1)
                    else nameField
                if name = "" then
                    malformed archive $"the member header at byte {position} has an empty name"
                members.Add((name, payloadStart, payloadCount))
            // Each member starts at an even offset; an odd-sized payload is followed by one pad byte.
            let dataEnd = dataStart + size
            position <- dataEnd + (dataEnd &&& 1L)
        List.ofSeq members

    // =========================================================================
    // ELF64 relocatable objects
    // =========================================================================

    /// The start in `bytes` of the member-relative range [offset, offset + size), refused as
    /// FS9224 when the range leaves the member.
    let private memberRange (archive: string) (memberName: string) (what: string) (start: int) (count: int) (offset: uint64) (size: uint64) : int =
        if offset > uint64 count || size > uint64 count - offset then
            memberMalformed archive memberName $"{what} (offset {offset}, size {size}) lies outside the {count}-byte member"
        else start + int offset

    /// The NUL-terminated name at `nameIndex` of the string table at `strings`.
    let private elfSymbolName (archive: string) (memberName: string) (bytes: byte[]) (strings: int) (stringSize: uint64) (nameIndex: uint32) : string =
        if uint64 nameIndex >= stringSize then
            memberMalformed archive memberName $"a symbol's name index {nameIndex} lies outside its {stringSize}-byte string table"
        let first = strings + int nameIndex
        let limit = strings + int stringSize
        let mutable finish = first
        while finish < limit && bytes[finish] <> 0uy do
            finish <- finish + 1
        if finish = limit then
            memberMalformed archive memberName $"the symbol name at string table index {nameIndex} is not NUL-terminated"
        if finish = first then
            memberMalformed archive memberName $"a defined global or weak symbol has an empty name (string table index {nameIndex})"
        textOf archive $"a symbol name of archive member '{memberName}'" bytes first (finish - first)

    /// Every symbol an ELF member defines with global or weak binding, in symbol table order:
    /// each `SHT_SYMTAB` entry whose section index is not `SHN_UNDEF`, whose type is neither
    /// `STT_SECTION` nor `STT_FILE`, and whose binding is `STB_GLOBAL` or `STB_WEAK`. C names
    /// and C++ `_Z` names are both kept. An ELF object that is not 64-bit, little-endian and
    /// relocatable is FS9227; a truncated header or a table outside the member is FS9224.
    let private elfDefinedSymbols (archive: string) (memberName: string) (bytes: byte[]) (start: int) (count: int) : (string * SymbolBinding) list =
        if count < 6 then
            memberMalformed archive memberName $"the ELF identification is truncated: {count} of 16 bytes"
        let elfClass = bytes[start + 4]
        let elfData = bytes[start + 5]
        if elfClass <> 2uy || elfData <> 1uy then
            memberUnsupported archive memberName $"is an ELF object of class {elfClass} and data encoding {elfData}; only ELF64 little-endian relocatable objects (class 2, encoding 1, e_type 1) are admitted"
        if count < 64 then
            memberMalformed archive memberName $"the ELF64 header is truncated: {count} of 64 bytes"
        let elfType = u16At bytes (start + 16)
        if elfType <> 1us then
            memberUnsupported archive memberName $"is an ELF64 object of e_type {elfType}; only relocatable objects (ET_REL, e_type 1) are admitted"
        let sectionOffset = u64At bytes (start + 40)
        // e_shoff 0: the object has no section header table, so it has no symbol table.
        if sectionOffset = 0UL then []
        else
            let entrySize = u16At bytes (start + 58)
            if entrySize <> 64us then
                memberMalformed archive memberName $"e_shentsize is {entrySize}, not the 64 bytes of an ELF64 section header"
            let declaredCount = u16At bytes (start + 60)
            // e_shnum 0 with a section header table: the count is sh_size of section 0.
            let sectionCount =
                if declaredCount <> 0us then uint64 declaredCount
                else
                    let first = memberRange archive memberName "section header 0" start count sectionOffset 64UL
                    u64At bytes (first + 32)
            if sectionOffset > uint64 count || sectionCount > (uint64 count - sectionOffset) / 64UL then
                memberMalformed archive memberName $"the section header table ({sectionCount} entries at offset {sectionOffset}) lies outside the {count}-byte member"
            let table = start + int sectionOffset
            let symbols = ResizeArray<string * SymbolBinding>()
            for index in 0 .. int sectionCount - 1 do
                let header = table + index * 64
                // SHT_SYMTAB
                if u32At bytes (header + 4) = 2u then
                    let symbolOffset = u64At bytes (header + 24)
                    let symbolSize = u64At bytes (header + 32)
                    let link = u32At bytes (header + 40)
                    let symbolEntrySize = u64At bytes (header + 56)
                    if symbolEntrySize <> 24UL || symbolSize % 24UL <> 0UL then
                        memberMalformed archive memberName $"symbol table section {index} has entry size {symbolEntrySize} and size {symbolSize}; ELF64 symbols are 24 bytes each"
                    let entries = memberRange archive memberName $"symbol table section {index}" start count symbolOffset symbolSize
                    if uint64 link >= sectionCount then
                        memberMalformed archive memberName $"symbol table section {index} links string table section {link}, but the object has {sectionCount} sections"
                    let stringHeader = table + int link * 64
                    // SHT_STRTAB
                    if u32At bytes (stringHeader + 4) <> 3u then
                        memberMalformed archive memberName $"symbol table section {index} links section {link}, which is no string table"
                    let stringSize = u64At bytes (stringHeader + 32)
                    let strings = memberRange archive memberName $"string table section {link}" start count (u64At bytes (stringHeader + 24)) stringSize
                    let symbolCount = symbolSize / 24UL
                    let extendedSectionIndices =
                        let shndxSections =
                            [ for shIdx in 0 .. int sectionCount - 1 do
                                  let shHeader = table + shIdx * 64
                                  // SHT_SYMTAB_SHNDX (18) linking this SHT_SYMTAB section
                                  if u32At bytes (shHeader + 4) = 18u && u32At bytes (shHeader + 40) = uint32 index then
                                      yield shIdx, shHeader ]
                        match shndxSections with
                        | [] -> None
                        | [ (shIdx, shHeader) ] ->
                            let shndxOffset = u64At bytes (shHeader + 24)
                            let shndxSize = u64At bytes (shHeader + 32)
                            let shndxEntrySize = u64At bytes (shHeader + 56)
                            if (shndxEntrySize <> 4UL && shndxEntrySize <> 0UL) || shndxSize <> symbolCount * 4UL then
                                memberMalformed archive memberName $"SHT_SYMTAB_SHNDX section {shIdx} has entry size {shndxEntrySize} and size {shndxSize}; expected {symbolCount * 4UL} bytes (4 bytes per symbol)"
                            Some (memberRange archive memberName $"SHT_SYMTAB_SHNDX section {shIdx}" start count shndxOffset shndxSize)
                        | _ ->
                            memberMalformed archive memberName $"symbol table section {index} has multiple SHT_SYMTAB_SHNDX sections"
                    for entry in 0 .. int symbolCount - 1 do
                        let at = entries + entry * 24
                        let info = bytes[at + 4]
                        let sectionIndex = u16At bytes (at + 6)
                        let symbolType = info &&& 0xfuy
                        let bind = info >>> 4
                        if sectionIndex <> 0us then
                            if sectionIndex < 0xFF00us then
                                if uint64 sectionIndex >= sectionCount then
                                    memberMalformed archive memberName $"symbol {entry} in section {index} names section {sectionIndex}, but the object has {sectionCount} sections"
                            elif sectionIndex = 0xFFF1us || sectionIndex = 0xFFF2us then
                                () // SHN_ABS (0xFFF1) and SHN_COMMON (0xFFF2)
                            elif sectionIndex = 0xFFFFus then
                                match extendedSectionIndices with
                                | None ->
                                    memberMalformed archive memberName $"symbol {entry} in section {index} uses SHN_XINDEX (0xFFFF), but no SHT_SYMTAB_SHNDX section links section {index}"
                                | Some shndxBase ->
                                    let extSection = u32At bytes (shndxBase + entry * 4)
                                    if extSection = 0u || uint64 extSection >= sectionCount then
                                        memberMalformed archive memberName $"symbol {entry} in section {index} has extended section index {extSection}, outside valid sections 1..{sectionCount - 1UL}"
                            else
                                memberMalformed archive memberName $"symbol {entry} in section {index} carries unsupported reserved section index 0x{sectionIndex:X4}"
                        // Defined (not SHN_UNDEF), neither STT_SECTION nor STT_FILE, and
                        // STB_GLOBAL (1), STB_WEAK (2) or STB_GNU_UNIQUE (10, coalesced by LLD like weak).
                        // A symbol of any other binding is not a definition another object can link against.
                        let binding =
                            if sectionIndex = 0us || symbolType = 3uy || symbolType = 4uy then None
                            elif bind = 1uy then Some GlobalStrong
                            elif bind = 2uy || bind = 10uy then Some Weak
                            else None
                        match binding with
                        | Some binding ->
                            let name = elfSymbolName archive memberName bytes strings stringSize (u32At bytes at)
                            symbols.Add((name, binding))
                        | None -> ()
            List.ofSeq symbols

    // =========================================================================
    // LLVM bitcode members
    // =========================================================================

    let private writeSlice (path: string) (bytes: byte[]) (start: int) (count: int) : unit =
        use stream = IO.File.Create path
        stream.Write(bytes, start, count)

    /// One `llvm-nm --format=posix` line, `name type [value [size]]`: a weak or GNU-unique
    /// coalesced definition (`W`, `w`, `V`, `v`, `u`), a strong or indirect-function definition
    /// (`T`, `D`, `R`, `B`, `C`, `i`, `I`) or an undefined symbol (`U`), which is no definition.
    /// A line of another shape or type is FS9230: the tool's answer is not read in part.
    let private posixSymbol (archive: string) (memberName: string) (tool: ToolIdentity) (line: string) : (string * SymbolBinding) option =
        match line.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
        | name :: kind :: _ ->
            match kind with
            | "W" | "w" | "V" | "v" | "u" -> Some (name, Weak)
            | "T" | "D" | "R" | "B" | "C" | "i" | "I" -> Some (name, GlobalStrong)
            | "U" -> None
            | other ->
                toolRefused archive $"llvm-nm '{tool.Path}' reports symbol '{name}' of bitcode member '{memberName}' with type '{other}', which the inspector does not classify"
        | _ ->
            toolRefused archive $"llvm-nm '{tool.Path}' printed '{line}' for bitcode member '{memberName}', which is no POSIX-format symbol line"

    /// Every symbol a bitcode member defines with external linkage, as `llvm-nm` reports it.
    /// The member's bytes are written to a temporary file that is deleted whether or not the
    /// tool succeeds. A tool that cannot be started or exits nonzero is FS9230.
    let private bitcodeDefinedSymbols (archive: string) (memberName: string) (tool: ToolIdentity) (bytes: byte[]) (start: int) (count: int) : (string * SymbolBinding) list =
        let directory = ScratchDirectory.root ()
        IO.Directory.CreateDirectory directory |> ignore
        let tempFile = IO.Path.Combine(directory, "farscape-archive-member-" + Guid.NewGuid().ToString("N") + ".bc")
        try
            writeSlice tempFile bytes start count
            let info = ProcessLaunch.startInfo tool.Path [ "--extern-only"; "--defined-only"; "--format=posix"; tempFile ]
            use child = new System.Diagnostics.Process(StartInfo = info)
            try child.Start() |> ignore
            with :? ComponentModel.Win32Exception as ex ->
                toolRefused archive $"llvm-nm '{tool.Path}' could not be started for bitcode member '{memberName}': {ex.Message}"
            let errors = child.StandardError.ReadToEndAsync()
            let output = child.StandardOutput.ReadToEnd()
            child.WaitForExit()
            if child.ExitCode <> 0 then
                toolRefused archive $"llvm-nm '{tool.Path}' exited with code {child.ExitCode} on bitcode member '{memberName}': {errors.Result.Trim()}"
            output.Split('\n')
            |> List.ofArray
            |> List.map (fun line -> line.Trim())
            |> List.filter (fun line -> line <> "")
            |> List.choose (posixSymbol archive memberName tool)
        finally
            IO.File.Delete tempFile

    // =========================================================================
    // Inspection
    // =========================================================================

    /// One member classified by its leading bytes, with its digest and defined symbols. A
    /// member that is neither ELF nor LLVM bitcode is FS9227.
    let private inspectMember (archive: string) (bitcodeTool: Lazy<ToolIdentity>) (bytes: byte[]) (index: int) (name: string, start: int, count: int) : ArchiveMember =
        let format, symbols =
            if hasPrefix bytes start count elfMagic then
                (ElfRelocatable, elfDefinedSymbols archive name bytes start count)
            elif hasPrefix bytes start count bitcodeMagic then
                (LlvmBitcode, bitcodeDefinedSymbols archive name bitcodeTool.Value bytes start count)
            elif hasPrefix bytes start count bitcodeWrapperMagic then
                (LlvmBitcodeWrapper, bitcodeDefinedSymbols archive name bitcodeTool.Value bytes start count)
            else
                memberUnsupported archive name $"({count} bytes) is neither an ELF64 relocatable object nor LLVM bitcode"
        { ArchiveMember.Index = index
          Name = name
          Offset = int64 start
          Size = int64 count
          Format = format
          Sha256 = sha256Of bytes start count
          DefinedSymbols =
            symbols |> List.map (fun (symbol, binding) ->
                { DefinedSymbol.Name = symbol; MemberName = name; MemberIndex = index; Binding = binding }) }

    /// The archive's one definition per symbol name. Strong definitions in two members are
    /// FS9229; a strong definition replaces a weak one; of several weak definitions and no
    /// strong one, the first in archive order stands.
    let private symbolTable (archive: string) (members: ArchiveMember list) : Map<string, DefinedSymbol> =
        members
        |> List.collect (fun (archiveMember: ArchiveMember) -> archiveMember.DefinedSymbols)
        |> List.fold (fun (table: Map<string, DefinedSymbol>) (symbol: DefinedSymbol) ->
            match Map.tryFind symbol.Name table, symbol.Binding with
            | None, _ -> Map.add symbol.Name symbol table
            | Some existing, GlobalStrong when existing.Binding = GlobalStrong ->
                if existing.MemberIndex = symbol.MemberIndex then
                    malformed archive $"archive member '{symbol.MemberName}' (index {symbol.MemberIndex}) defines strong global symbol '{symbol.Name}' more than once"
                else
                    refuseAt Diagnostics.ArchiveDuplicateStrongSymbol archive
                        $"symbol '{symbol.Name}' has strong global definitions in archive members '{existing.MemberName}' (index {existing.MemberIndex}) and '{symbol.MemberName}' (index {symbol.MemberIndex})"
            | Some _, GlobalStrong -> Map.add symbol.Name symbol table
            | Some _, Weak -> table) Map.empty

    /// The file's digest, or FS9224 when the file cannot be read.
    let private fileDigest (archive: string) : string =
        try ClangToolchain.sha256 archive
        with
        | :? IO.IOException as ex -> malformed archive $"static archive '{archive}' cannot be read: {ex.Message}"
        | :? UnauthorizedAccessException as ex -> malformed archive $"static archive '{archive}' cannot be read: {ex.Message}"

    /// The file's bytes, or FS9224 when the file cannot be read.
    let private fileBytes (archive: string) : byte[] =
        try IO.File.ReadAllBytes archive
        with
        | :? IO.IOException as ex -> malformed archive $"static archive '{archive}' cannot be read: {ex.Message}"
        | :? UnauthorizedAccessException as ex -> malformed archive $"static archive '{archive}' cannot be read: {ex.Message}"

    /// `inspect` with its effects supplied. `resolveBitcodeTool` is called at most once, and
    /// only when the archive has a bitcode member. `afterRead` receives the resolved archive
    /// path after every member is read and before the post-inspection digest is taken.
    ///
    /// The order of checks: the file exists and is not empty (FS9224); its SHA-256 equals
    /// `expectedSha256` when one is given (FS9225); the bytes read are the bytes that were
    /// digested (FS9225); the magic is `!<arch>\n`, not `!<thin>\n` (FS9226) or anything else
    /// (FS9224); each member header, name and payload is well formed (FS9224); each member is
    /// an ELF64 relocatable object or LLVM bitcode (FS9227, FS9230); the file's SHA-256 after
    /// inspection equals its SHA-256 before (FS9225); no symbol has two strong definitions
    /// (FS9229).
    let inspectWith (resolveBitcodeTool: unit -> Result<ToolIdentity, Diagnostics.Refusal>) (afterRead: string -> unit)
                    (archivePath: string) (expectedSha256: string option) : Result<ArchiveProvenance, Diagnostics.Refusal> =
        try
            if String.IsNullOrWhiteSpace archivePath then
                Diagnostics.diagnostic Diagnostics.ArchiveMalformed "no static archive path is given"
                    (Diagnostics.Located.Unknown "the static archive path is empty")
                |> Diagnostics.refuse
            let resolvedPath = IO.Path.GetFullPath archivePath
            if not (IO.File.Exists resolvedPath) then
                malformed resolvedPath $"static archive '{resolvedPath}' does not exist"
            let digest = fileDigest resolvedPath
            let bytes = fileBytes resolvedPath
            if bytes.Length = 0 then
                malformed resolvedPath $"static archive '{resolvedPath}' is empty"
            match expectedSha256 with
            | Some expected when not (isSha256Hex expected) ->
                refuseAt Diagnostics.ArchiveDigestMismatch resolvedPath
                    $"the expected SHA-256 '{expected}' of static archive '{resolvedPath}' is not 64 hexadecimal digits"
            | Some expected when not (String.Equals(expected, digest, StringComparison.OrdinalIgnoreCase)) ->
                refuseAt Diagnostics.ArchiveDigestMismatch resolvedPath
                    $"static archive '{resolvedPath}' has SHA-256 {digest}, not the expected {expected.ToLowerInvariant()}"
            | Some _ | None -> ()
            // The members inspected below are the bytes the digest covers.
            let readDigest = sha256Of bytes 0 bytes.Length
            if readDigest <> digest then
                refuseAt Diagnostics.ArchiveDigestMismatch resolvedPath
                    $"static archive '{resolvedPath}' changed while it was read: the bytes read have SHA-256 {readDigest}, not the pre-inspection {digest}"
            if hasPrefix bytes 0 bytes.Length thinMagic then
                refuseAt Diagnostics.ArchiveThinUnsupported resolvedPath
                    $"static archive '{resolvedPath}' is a thin archive: its members' bytes are in other files, outside the archive's SHA-256"
            if not (hasPrefix bytes 0 bytes.Length archiveMagic) then
                malformed resolvedPath $"'{resolvedPath}' is no GNU ar archive: it does not begin with the 8-byte magic '!<arch>' and a newline"
            let bitcodeTool =
                lazy (
                    match resolveBitcodeTool () with
                    | Ok tool -> tool
                    | Error refusal -> Diagnostics.raiseRefusal refusal)
            let members = readMembers resolvedPath bytes |> List.mapi (inspectMember resolvedPath bitcodeTool bytes)
            afterRead resolvedPath
            let finalDigest =
                try ClangToolchain.sha256 resolvedPath
                with
                | :? IO.IOException as ex ->
                    refuseAt Diagnostics.ArchiveDigestMismatch resolvedPath $"static archive '{resolvedPath}' could not be read again after inspection: {ex.Message}"
                | :? UnauthorizedAccessException as ex ->
                    refuseAt Diagnostics.ArchiveDigestMismatch resolvedPath $"static archive '{resolvedPath}' could not be read again after inspection: {ex.Message}"
            if finalDigest <> digest then
                refuseAt Diagnostics.ArchiveDigestMismatch resolvedPath
                    $"static archive '{resolvedPath}' changed during inspection: its SHA-256 is now {finalDigest}, not the pre-inspection {digest}"
            let symbols = symbolTable resolvedPath members
            Ok { ArchiveProvenance.Path = archivePath
                 ResolvedPath = resolvedPath
                 Sha256 = digest
                 Members = members
                 Symbols = symbols
                 BitcodeTool = if bitcodeTool.IsValueCreated then Some bitcodeTool.Value else None }
        with :? Diagnostics.RefusalException as refused -> Error refused.Refusal

    /// Inspect the static archive at `archivePath`, requiring its SHA-256 to equal
    /// `expectedSha256` when one is given. Bitcode members are read by the `llvm-nm`
    /// `LlvmNmToolchain.resolve` selects.
    let inspect (archivePath: string) (expectedSha256: string option) : Result<ArchiveProvenance, Diagnostics.Refusal> =
        inspectWith LlvmNmToolchain.resolve ignore archivePath expectedSha256

    /// Each required symbol with the archive definition it resolves to, in the order given
    /// (each name once). A symbol no member defines is FS9228, one diagnostic per symbol.
    let verifyRequiredSymbols (provenance: ArchiveProvenance) (requiredSymbols: string seq) : Result<(string * DefinedSymbol) list, Diagnostics.Refusal> =
        let resolved =
            requiredSymbols
            |> List.ofSeq
            |> List.distinct
            |> List.map (fun symbol -> symbol, Map.tryFind symbol provenance.Symbols)
        let missing =
            resolved |> List.choose (fun (symbol, definition) ->
                match definition with
                | None -> Some symbol
                | Some _ -> None)
        match missing with
        | [] -> Ok (resolved |> List.choose (fun (symbol, definition) -> definition |> Option.map (fun found -> symbol, found)))
        | _ :: _ ->
            let diagnostics =
                missing |> List.map (fun symbol ->
                    Diagnostics.diagnostic Diagnostics.ArchiveMissingSymbol
                        $"extern symbol '{symbol}' is defined by no member of static archive '{provenance.ResolvedPath}' ({provenance.Members.Length} members, {provenance.Symbols.Count} defined symbols)"
                        (archiveSource provenance.ResolvedPath))
            Error (Diagnostics.refusalsOf diagnostics "")

    // =========================================================================
    // Link provenance record
    // =========================================================================

    let private formatName (format: MemberFormat) : string =
        match format with
        | ElfRelocatable -> "elf64-relocatable"
        | LlvmBitcode -> "llvm-bitcode"
        | LlvmBitcodeWrapper -> "llvm-bitcode-wrapper"

    let private bindingName (binding: SymbolBinding) : string =
        match binding with
        | GlobalStrong -> "global-strong"
        | Weak -> "weak"

    /// The link provenance record of one library as TOML: `[artifact]` (the library, the
    /// archive's path and SHA-256, and the `llvm-nm` identity when bitcode members were read),
    /// one `[[members]]` entry per member (index, name, payload offset and size, format,
    /// SHA-256, strong and weak symbol names), one `[[bindings]]` entry per resolved extern
    /// symbol (the member that defines it, by name, index and SHA-256), and one
    /// `[[companion_recipes]]` entry per recipe. The text is `Toml.serialize` of the document,
    /// so `Toml.parse` reads it back.
    let renderLinkProvenanceToml (libraryName: string) (provenance: ArchiveProvenance) (resolvedBindings: (string * DefinedSymbol) list)
                                 (companionRecipes: CompanionArchiveRecipe list) : string =
        let text (value: string) = TomlValue.String value
        let tool (identity: ToolIdentity) =
            TomlValue.InlineTable (Map.ofList [ "path", text identity.Path; "sha256", text identity.Sha256 ])
        let symbolsOf (binding: SymbolBinding) (archiveMember: ArchiveMember) =
            archiveMember.DefinedSymbols
            |> List.filter (fun (symbol: DefinedSymbol) -> symbol.Binding = binding)
            |> List.map (fun (symbol: DefinedSymbol) -> text symbol.Name)
            |> TomlValue.Array
        let artifact =
            let table =
                TomlTable.empty
                |> TomlTable.add "library" (text libraryName)
                |> TomlTable.add "kind" (text "static-archive")
                |> TomlTable.add "path" (text provenance.Path)
                |> TomlTable.add "resolved_path" (text provenance.ResolvedPath)
                |> TomlTable.add "sha256" (text provenance.Sha256)
            match provenance.BitcodeTool with
            | Some identity -> TomlTable.add "bitcode_tool" (tool identity) table
            | None -> table
        let members =
            provenance.Members |> List.map (fun (archiveMember: ArchiveMember) ->
                TomlTable.empty
                |> TomlTable.add "index" (TomlValue.Integer (int64 archiveMember.Index))
                |> TomlTable.add "name" (text archiveMember.Name)
                |> TomlTable.add "offset" (TomlValue.Integer archiveMember.Offset)
                |> TomlTable.add "size" (TomlValue.Integer archiveMember.Size)
                |> TomlTable.add "format" (text (formatName archiveMember.Format))
                |> TomlTable.add "sha256" (text archiveMember.Sha256)
                |> TomlTable.add "strong_symbols" (symbolsOf GlobalStrong archiveMember)
                |> TomlTable.add "weak_symbols" (symbolsOf Weak archiveMember)
                |> TomlValue.Table)
        let bindings =
            resolvedBindings |> List.map (fun (symbol, definition) ->
                let memberDigest =
                    match provenance.Members |> List.tryFind (fun (archiveMember: ArchiveMember) -> archiveMember.Index = definition.MemberIndex) with
                    | Some archiveMember -> archiveMember.Sha256
                    | None ->
                        Diagnostics.raiseRefusal (Diagnostics.uncoded $"symbol '{symbol}' is bound to member index {definition.MemberIndex}, which the provenance does not list")
                TomlTable.empty
                |> TomlTable.add "symbol" (text symbol)
                |> TomlTable.add "member" (text definition.MemberName)
                |> TomlTable.add "member_index" (TomlValue.Integer (int64 definition.MemberIndex))
                |> TomlTable.add "member_sha256" (text memberDigest)
                |> TomlTable.add "binding" (text (bindingName definition.Binding))
                |> TomlValue.Table)
        let recipes =
            companionRecipes |> List.map (fun (recipe: CompanionArchiveRecipe) ->
                TomlTable.empty
                |> TomlTable.add "source" (text recipe.Source)
                |> TomlTable.add "source_sha256" (text recipe.SourceSha256)
                |> TomlTable.add "compiler" (tool recipe.Compiler)
                |> TomlTable.add "arguments" (TomlValue.Array (recipe.Arguments |> List.map text))
                |> TomlTable.add "archiver" (tool recipe.Archiver)
                |> TomlTable.add "output" (text recipe.Output)
                |> TomlValue.Table)
        let document =
            TomlTable.empty
            |> TomlTable.add "artifact" (TomlValue.Table artifact)
            |> TomlTable.add "members" (TomlValue.Array members)
            |> TomlTable.add "bindings" (TomlValue.Array bindings)
        let document =
            if recipes.IsEmpty then document
            else TomlTable.add "companion_recipes" (TomlValue.Array recipes) document
        Toml.serialize document
