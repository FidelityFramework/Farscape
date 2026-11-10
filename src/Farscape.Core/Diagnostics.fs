namespace Farscape.Core

/// Farscape generator diagnostic codes, in the Farscape wiki's `FS` plus four digits format
/// (02_BAREWire_Integration's `FS8001`). FS8xxx is taken by Clef compiler diagnostics the wiki
/// documents (FS8001, FS8020), so generator refusals are allocated sequentially from FS9101.
///
/// | Code   | Refusal                                                                        |
/// |--------|--------------------------------------------------------------------------------|
/// | FS9101 | a macro is defined differently by two of a pilot's translation units          |
/// | FS9102 | a unit's typedef chain disagrees with the profile's scalar row for that name  |
/// | FS9103 | a C/C++ enum is used without a clang measurement of its representation        |
/// | FS9104 | a clang enum measurement contradicts the enum's declared underlying type      |
/// | FS9105 | an enumerator's value is outside the signed 64-bit carrier                    |
/// | FS9106 | a protocol enum is used without one declared wire type for it                 |
/// | FS9107 | a protocol enum's value does not fit its protocol's declared wire type        |
/// | FS9108 | a GIR-named C type is declared by no pilot unit                               |
/// | FS9109 | a GIR-named C type is declared differently by two pilot units                 |
/// | FS9110 | a C/C++ enumerator's value does not fit clang's measured representation       |
/// | FS9111 | a C/C++ enum's declared underlying type has no integer representation         |
/// | FS9112 | an enum is declared differently by two of a pilot's translation units         |
/// | FS9113 | an anonymous nested record has no clang-measured layout in its unit           |
/// | FS9114 | a generated anonymous record name names another declaration of its unit       |
/// | FS9115 | a by-value type resolves through no typedef, record or profile entry          |
/// | FS9116 | a record is defined differently by two of a pilot's translation units         |
/// | FS9117 | a clang built-in type alias in a result has no canonical type clang can give  |
/// | FS9118 | clang's measurement of an alias's canonical type contradicts its profile row  |
/// | FS9119 | a descriptor cites a record the pilot declares no value type for              |
/// | FS9120 | a result alias is unreadable: its function takes an incomplete type by value  |
/// | FS9121 | a layout-neutral attribute is unconfirmed by clang's measured record layout   |
/// | FS9122 | an anonymous struct or union member has no representation in its record       |
/// | FS9123 | a generated anonymous member name names another member of its record          |
/// | FS9124 | a pilot define is not NAME or NAME=VALUE, so it could carry a clang option    |
/// | FS9125 | a pkg-config module name is not a module name, so it could carry an option    |
/// | FS9126 | pkg-config --cflags word that is no include path, system include path or define |
/// | FS9127 | a field's type reaches an attributed typedef, and clang has no layout of it   |
/// | FS9128 | a typedef naming a record carries an attribute its record layout dump omits   |
/// | FS9129 | a record carries an unnamed bit-field, and clang has no layout of it          |
/// | FS9130 | a protocol enum entry's value is no (negated) C integer constant in int64     |
/// | FS9131 | a header parameter name is the positional name of an unnamed sibling          |
/// | FS9132 | a pilot binding contract list names one native parameter twice                |
/// | FS9133 | a pilot binding key names a parameter outside the native arity                |
/// | FS9134 | a pilot hypothesis has an empty identity, version or source                   |
/// | FS9135 | a pilot contract needs the untranslated parameter list its binding translates |
/// | FS9136 | a callback invocation's scope is not during_call, retained or process_exit    |
/// | FS9137 | a callback invocation carries a field its declared scope forbids or lacks     |
/// | FS9138 | a callback that may be NULL has a contract outside during_call and retained   |
/// | FS9139 | a callback's absence hypothesis reuses its activation identity                |
/// | FS9140 | a process_exit callback is no zero-argument void entry with signed32 result   |
/// | FS9141 | a contract names a binding that is no selected generated declaration          |
/// | FS9142 | a pilot callback key names a parameter with no native function-pointer type   |
/// | FS9143 | a zero-argument native entry is no explicit (void) prototype returning void   |
/// | FS9144 | a callback entry argument or result type is outside the admitted profile      |
/// | FS9145 | nonnull evidence for a pointer contradicts nullable evidence for it           |
/// | FS9146 | a native function pointer that may be NULL has no admitted absence contract   |
/// | FS9147 | a reference projection's cell is no scalar or opaque pointer cell             |
/// | FS9148 | a string projection names a parameter that is no char pointer                 |
/// | FS9149 | a handle projection names a parameter or result that is no data pointer       |
/// | FS9150 | a constant projection is no integer literal for a scalar integer parameter    |
/// | FS9151 | a result range bounds a non-integer result or by a non-integer argument       |
/// | FS9152 | a result range's constant bounds are contradictory                            |
/// | FS9153 | an owned nullable return is no option<CHandle> released through parameter 0   |
/// | FS9154 | a release binding is no unary matching CHandle Value parameter, void result   |
/// | FS9155 | a writable buffer names one parameter as both buffer and count                |
/// | FS9156 | a writable buffer is no writable unsigned8 byte reference                     |
/// | FS9157 | a writable buffer's count is no scalar integer Value parameter                |
/// | FS9158 | a pilot binding declares one parameter both writable and read-only reference  |
/// | FS9159 | a C function a pilot key names or requires is declared by no parsed header    |
/// | FS9160 | two pilot binding entries carrying a contract name one binding                |
/// | FS9161 | a pilot binding carrying a contract is not exactly one selected declaration   |
/// | FS9162 | a callback parameter's function-pointer type yields no native entry signature   |
/// | FS9163 | wrapper generation meets a listener struct; no generic listener builder exists  |
/// | FS9164 | a pilot key names a parameter its native function does not declare              |
/// | FS9165 | a callback registration's environment parameter is no data pointer              |
/// | FS9166 | a mapped return's acquisition or release signature lacks the mapped shape       |
/// | FS9167 | a mapped return's declared value is outside its admitted set                    |
/// | FS9168 | a signal value's native pointee has no carrier in the binding Types module      |
/// | FS9169 | GIR nullability evidence names a value that is no native data pointer           |
/// | FS9170 | g_signal_connect_data's declaration lacks the shape signal binding requires     |
/// | FS9171 | a GIR signal's callback parameter names are missing or repeated                 |
/// | FS9172 | a namespace lists signals but the pilot names no introspection files            |
/// | FS9173 | the typed protocol projection is requested under a profile other than LP64      |
/// | FS9174 | a C function the typed protocol calls has another representation than declared  |
/// | FS9175 | a record the typed protocol projection reads has no clang measurement           |
/// | FS9176 | a record the typed protocol reads is measured with another layout than declared |
/// | FS9177 | a typed protocol constructor carries string arguments with no ownership profile |
/// | FS9178 | a by-value type reaches an attributed typedef clang measures unlike its claim |
/// | FS9179 | a record of only unnamed bit-fields measures zero bytes, which no descriptor has |
/// | FS9180 | a null function-pointer sentinel is fixed with no declared borrow lifetime      |
/// | FS9181 | a constant on a function-pointer parameter names no sentinel macro of its type  |
/// | FS9182 | a pilot selects the retired experimental native-pointer surface                 |
/// | FS9183 | a value struct is no plain record of named scalar or pointer fields             |
/// | FS9184 | a C++ POD class has no admitted object storage, ownership and method ABI contract |
/// | FS9185 | an abi_critical_structs entry names no declared record or scalar typedef        |
/// | FS9186 | an abi_critical_structs entry names another record kind than it declares        |
/// | FS9187 | an abi_critical_structs record has no clang-measured layout                     |
/// | FS9188 | an abi_critical_structs record's measuring unit disagrees with the pilot's      |
/// | FS9189 | an abi_critical_structs record measures differently in two of a pilot's units   |
/// | FS9190 | a pilot introspection file does not exist                                       |
/// | FS9191 | a pilot introspection file is no GIR repository document with a namespace       |
/// | FS9192 | a GIR signal declares no name, or a signal parameter or result no type          |
/// | FS9193 | a pilot signal selection is not spelled [Namespace.]Class::signal-name          |
/// | FS9194 | a pilot signal selection names no signal the introspection files declare        |
/// | FS9195 | a pilot signal selection names a signal several namespaces declare              |
/// | FS9196 | a selected signal's parameter or result type has no C spelling the files give   |
/// | FS9197 | a pilot file cannot be read as a TOML document                                  |
/// | FS9198 | a pilot key its table requires is absent                                        |
/// | FS9199 | a pilot key's value has another TOML type than its key requires                 |
/// | FS9200 | a pilot table carries a key that table does not admit                           |
/// | FS9201 | a pilot key's value is outside the values its key admits                        |
/// | FS9202 | a pilot array declares one record's entry more than once                        |
/// | FS9203 | a callback context is no untyped pointer entry argument and receiving parameter |
/// | FS9204 | a counted entry array is no pointer array of its element and integer count      |
/// | FS9205 | a pointer-to-pointer callback entry argument has no counted-array declaration   |
/// | FS9206 | a retained callback's owner, redefinition key or release does not fit it        |
/// | FS9207 | a retained callback is registered with a captured environment                   |
/// | FS9208 | a borrowed result is no pointer to its content's bytes, or is a handle or resource |
/// | FS9209 | a borrowed result's extent is no integer companion call on its own arguments     |
/// | FS9210 | a borrowed result's owner or invalidation does not fit its declaration           |
/// | FS9211 | a C++ pimpl class has no admitted object storage, ownership and method ABI contract |
/// | FS9212 | a C++ value class has no admitted object storage, ownership and method ABI contract |
/// | FS9213 | a C++ abstract interface class has no admitted object storage, ownership and method ABI contract |
/// | FS9214 | a C++ opaque class has no admitted object storage, ownership and method ABI contract |
/// | FS9215 | a requested C++ template instantiation is not exported by the library build      |
/// | FS9216 | a C++ ABI fact lacks provenance or conflicts with the library build or AST      |
/// | FS9217 | a marshaled C++ object is used after its D1 release                            |
/// | FS9218 | a marshaled C++ object receives its D1 release more than once                   |
///
/// Allocation continues past FS9199 as FS92xx: the codes are sequential, four digits after FS.
/// Each refusal reason has exactly one code and each code one reason.
///
/// Every refusal is built as a `Diagnostic` (#184): its code, its message, its severity, where
/// in a header or protocol file its cause is declared (or the exact reason that is unknown), and
/// the pilot file and key whose input caused it. A `Refusal` carries the diagnostics of one
/// failure with the exact text Farscape prints for it; `RefusalException` carries a refusal
/// raised from inside generation. The text is `render` of each diagnostic, so the structure
/// changes nothing a caller reads as text.
module Diagnostics =

    [<Literal>]
    let MacroDivergent = "FS9101"          // macro defined differently across a pilot's units (#56)

    [<Literal>]
    let TypedefProfileConflict = "FS9102"  // unit typedef chain vs profile scalar row (#54)

    [<Literal>]
    let EnumUnmeasured = "FS9103"          // C/C++ enum without a clang measurement (#53)

    [<Literal>]
    let EnumReprConflict = "FS9104"        // clang enum measurement vs declared underlying type (#53)

    [<Literal>]
    let EnumValueRange = "FS9105"          // enumerator outside the signed 64-bit carrier (#53)

    [<Literal>]
    let ProtocolEnumUndeclared = "FS9106"  // protocol enum without one declared wire type (#53)

    [<Literal>]
    let ProtocolEnumValueConflict = "FS9107" // protocol enum value outside its declared wire type (#53)

    [<Literal>]
    let GirTypeUndeclared = "FS9108"       // GIR-named C type declared by no pilot unit (#54)

    [<Literal>]
    let GirTypeDivergent = "FS9109"        // GIR-named C type declared differently by two units (#54)

    [<Literal>]
    let EnumValueConflict = "FS9110"       // C/C++ enumerator outside clang's measured representation (#53)

    [<Literal>]
    let EnumUnderlyingUnrepresented = "FS9111" // C/C++ enum underlying type with no integer representation (#53)

    [<Literal>]
    let EnumDivergent = "FS9112"           // enum declared differently by two of a pilot's units (#53, #54)

    [<Literal>]
    let AnonymousRecordUnmeasured = "FS9113" // anonymous nested record without a clang-measured layout (#159)

    [<Literal>]
    let AnonymousNameCollision = "FS9114"  // generated anonymous record name names another declaration (#159)

    [<Literal>]
    let ByValueTypeUnresolved = "FS9115"   // by-value type resolving nowhere, so no descriptor width (#162)

    [<Literal>]
    let RecordDivergent = "FS9116"         // record defined differently by two of a pilot's units (#161)

    [<Literal>]
    let AliasUncanonicalized = "FS9117"    // clang built-in alias without a canonical type clang gives (#171)

    [<Literal>]
    let AliasMeasurementConflict = "FS9118" // alias canonical type: clang measurement vs profile row (#171)

    [<Literal>]
    let RecordWithoutValueType = "FS9119"  // descriptor cites a record with no emitted value type (#162)

    [<Literal>]
    let AliasIncompleteParameter = "FS9120" // result alias unreadable: function takes an incomplete type by value (#171)

    [<Literal>]
    let LayoutNeutralUnconfirmed = "FS9121" // layout-neutral attribute without clang's agreeing measurement (#174)

    [<Literal>]
    let AnonymousMemberUnrepresented = "FS9122" // C11 anonymous member with no representation in its record (#173)

    [<Literal>]
    let AnonymousMemberNameCollision = "FS9123" // generated anonymous member name names another member (#173)

    [<Literal>]
    let DefineMalformed = "FS9124"         // pilot define that is not NAME or NAME=VALUE (#178)

    [<Literal>]
    let PkgConfigModuleMalformed = "FS9125" // pkg-config module name that is not a module name (#178)

    [<Literal>]
    let PkgConfigFlagRefused = "FS9126"   // pkg-config --cflags word other than -I, -isystem or -D (#178, #179)

    [<Literal>]
    let TypedefAttributeUnmeasured = "FS9127" // field type reaches an attributed typedef, no clang layout (#175)

    [<Literal>]
    let TypedefNamingAttribute = "FS9128" // typedef naming a record carries an attribute its layout dump omits (#175)

    [<Literal>]
    let UnnamedBitFieldUnmeasured = "FS9129" // unnamed bit-field in a record without a clang layout (#176)

    [<Literal>]
    let ProtocolEnumValueMalformed = "FS9130" // protocol enum entry value no (negated) decimal, hex or octal C constant in int64 (#160)

    [<Literal>]
    let ParameterNameCollision = "FS9131" // header parameter name equals an unnamed sibling's positional name (#194)

    [<Literal>]
    let ContractDuplicateParameter = "FS9132" // pilot contract list naming one native parameter twice (#195)

    [<Literal>]
    let ContractParameterOutOfRange = "FS9133" // pilot binding key naming a parameter outside the native arity (#195)

    [<Literal>]
    let HypothesisFieldEmpty = "FS9134" // pilot hypothesis with an empty identity, version or source (#195)

    [<Literal>]
    let ContractRequiresUntranslated = "FS9135" // pilot contract on a binding whose projection translates its parameters (#195)

    [<Literal>]
    let CallbackScopeUnknown = "FS9136" // callback invocation scope not during_call, retained or process_exit (#195, #191)

    [<Literal>]
    let CallbackScopeFieldConflict = "FS9137" // callback invocation field its scope forbids or lacks (#195)

    [<Literal>]
    let NullableCallbackScope = "FS9138" // callback that may be NULL with a contract outside during_call and retained (#195, #191)

    [<Literal>]
    let AbsenceIdentityReused = "FS9139" // absence hypothesis identity equal to the activation identity (#195)

    [<Literal>]
    let ProcessExitSignature = "FS9140" // process_exit callback not a zero-argument void entry with a signed32 registration (#195)

    [<Literal>]
    let ContractBindingUnselected = "FS9141" // contract naming a binding that is no selected generated declaration (#195)

    [<Literal>]
    let CallbackNotFunctionPointer = "FS9142" // pilot callback key on a parameter with no native function-pointer type or signature (#195)

    [<Literal>]
    let CallbackZeroArgumentPrototype = "FS9143" // zero-argument native entry without an explicit (void) prototype and void result (#195)

    [<Literal>]
    let CallbackEntryTypeUnadmitted = "FS9144" // callback entry argument or result type outside the admitted profile (#195)

    [<Literal>]
    let NonnullContradiction = "FS9145" // nonnull evidence contradicting nullable evidence for one pointer (#195)

    [<Literal>]
    let NullableCallbackUnadmitted = "FS9146" // native function pointer that may be NULL, with no admitted absence contract (#195)

    [<Literal>]
    let ReferenceCellUnsupported = "FS9147" // reference projection whose cell is no scalar or opaque pointer cell (#195)

    [<Literal>]
    let StringProjectionNotCharPointer = "FS9148" // string projection on a parameter that is no char pointer (#195)

    [<Literal>]
    let HandleProjectionNotDataPointer = "FS9149" // handle projection on a parameter or result that is no data pointer (#195)

    [<Literal>]
    let ConstantProjectionInvalid = "FS9150" // constant projection that is no integer literal for a scalar integer parameter (#195)

    [<Literal>]
    let ResultRangeNotInteger = "FS9151" // result range on a non-integer result or bounded by a non-integer argument (#195)

    [<Literal>]
    let ResultRangeBoundsContradictory = "FS9152" // result range with contradictory constant bounds (#195)

    [<Literal>]
    let ResourceReturnShape = "FS9153" // owned nullable return not option<CHandle> released through parameter 0 (#195)

    [<Literal>]
    let ResourceReleaseShape = "FS9154" // release binding not a unary matching CHandle Value parameter with a void result (#195)

    [<Literal>]
    let WritableBufferSameParameter = "FS9155" // writable buffer naming one parameter as buffer and count (#195)

    [<Literal>]
    let WritableBufferNotByteReference = "FS9156" // writable buffer that is no writable unsigned8 byte reference (#195)

    [<Literal>]
    let WritableBufferCountNotInteger = "FS9157" // writable buffer count that is no scalar integer Value parameter (#195)

    [<Literal>]
    let ReferenceAccessConflict = "FS9158" // pilot binding parameter both writable and read-only reference (#195)

    [<Literal>]
    let BindingSymbolUndeclared = "FS9159" // C function a pilot key names or requires, declared by no parsed header (#195)

    [<Literal>]
    let BindingDuplicate = "FS9160"        // two pilot binding entries with a contract naming one binding (#195)

    [<Literal>]
    let ContractBindingNotSelectedOnce = "FS9161" // pilot binding with a contract not exactly one selected declaration (#195)

    [<Literal>]
    let CallbackSignatureUnrecovered = "FS9162" // a callback parameter's function-pointer type yields no native entry signature (#195)

    [<Literal>]
    let GenericListenerUnadmitted = "FS9163" // wrapper generation meets a listener struct; no generic listener builder exists (#195)

    [<Literal>]
    let ParameterNameUndeclared = "FS9164" // a pilot key names a parameter its native function does not declare (#195)

    [<Literal>]
    let CallbackEnvironmentNotDataPointer = "FS9165" // a callback registration's environment parameter is no data pointer (#195)

    [<Literal>]
    let MappedSignatureShape = "FS9166" // a mapped return's acquisition or release signature lacks the mapped shape (#195)

    [<Literal>]
    let MappedValueInvalid = "FS9167" // a mapped return's declared value is outside its admitted set (#195)

    [<Literal>]
    let SignalCarrierUndeclared = "FS9168" // a signal value's native pointee has no carrier in the binding Types module (#195)

    [<Literal>]
    let NullabilityOnNonPointer = "FS9169" // GIR nullability evidence names a value that is no native data pointer (#195)

    [<Literal>]
    let SignalConnectShape = "FS9170" // g_signal_connect_data's declaration lacks the shape signal binding requires (#195)

    [<Literal>]
    let SignalParameterNamesInvalid = "FS9171" // a GIR signal's callback parameter names are missing or repeated (#195)

    [<Literal>]
    let SignalsWithoutIntrospection = "FS9172" // a namespace lists signals but the pilot names no introspection files (#195)

    [<Literal>]
    let TypedProtocolProfile = "FS9173" // the typed protocol projection is requested under a profile other than LP64 (#195)

    [<Literal>]
    let TypedProtocolSignatureMismatch = "FS9174" // a C function the typed protocol calls has another representation than declared (#195)

    [<Literal>]
    let TypedProtocolLayoutUnmeasured = "FS9175" // a record the typed protocol projection reads has no clang measurement (#195)

    [<Literal>]
    let TypedProtocolLayoutMismatch = "FS9176" // a record the typed protocol reads is measured with another layout than declared (#195)

    [<Literal>]
    let TypedProtocolConstructorStrings = "FS9177" // a typed protocol constructor carries string arguments with no ownership profile (#195)

    [<Literal>]
    let ByValueTypedefAttribute = "FS9178" // by-value type reaches an attributed typedef, unmeasured or measured unlike its descriptor's type (#181)

    [<Literal>]
    let PaddingOnlyRecordEmpty = "FS9179" // record of only unnamed bit-fields clang measures at zero bytes (#183)

    [<Literal>]
    let SentinelBorrowUndeclared = "FS9180" // null function-pointer sentinel fixed with no declared borrow lifetime (#192)

    [<Literal>]
    let SentinelProjectionInvalid = "FS9181" // constant on a function-pointer parameter naming no sentinel macro of its type (#192)

    [<Literal>]
    let NativePointerSurfaceRetired = "FS9182" // pilot selecting the retired experimental native-pointer surface (#199)

    [<Literal>]
    let ValueStructUnrepresentable = "FS9183" // value struct that is no plain record of named scalar or pointer fields (#199)

    [<Literal>]
    let CppClassAbiUnadmitted = "FS9184" // C++ POD class without an admitted storage, ownership and method ABI contract (#199, #116)

    [<Literal>]
    let AbiStorageUndeclared = "FS9185" // abi_critical_structs entry naming no declared record or scalar typedef (#199)

    [<Literal>]
    let AbiStorageKindMismatch = "FS9186" // abi_critical_structs entry naming another record kind than declared (#199)

    [<Literal>]
    let AbiStorageUnmeasured = "FS9187" // abi_critical_structs record without a clang-measured layout (#199)

    [<Literal>]
    let AbiStorageUnitDisagrees = "FS9188" // abi_critical_structs record whose measuring unit disagrees with the pilot's declarations (#199)

    [<Literal>]
    let AbiStorageMeasurementDivergent = "FS9189" // abi_critical_structs record measuring differently in two units (#199)

    [<Literal>]
    let IntrospectionFileMissing = "FS9190" // pilot introspection file that does not exist (#199)

    [<Literal>]
    let IntrospectionNotGir = "FS9191" // pilot introspection file that is no GIR repository document with a namespace (#199)

    [<Literal>]
    let SignalDeclarationIncomplete = "FS9192" // GIR signal without a name, or a signal parameter or result without a type (#199)

    [<Literal>]
    let SignalSelectionMalformed = "FS9193" // pilot signal selection not spelled [Namespace.]Class::signal-name (#199)

    [<Literal>]
    let SignalUndeclared = "FS9194" // pilot signal selection naming no declared signal (#199)

    [<Literal>]
    let SignalAmbiguous = "FS9195" // pilot signal selection naming a signal several namespaces declare (#199)

    [<Literal>]
    let SignalTypeUnspelled = "FS9196" // selected signal parameter or result type without a C spelling the files give (#199)

    [<Literal>]
    let PilotDocumentUnreadable = "FS9197" // pilot file that cannot be read as a TOML document (#199)

    [<Literal>]
    let PilotKeyMissing = "FS9198" // pilot key its table requires, absent (#199)

    [<Literal>]
    let PilotValueType = "FS9199" // pilot key value of another TOML type than its key requires (#199)

    [<Literal>]
    let PilotKeyUnknown = "FS9200" // pilot table key that table does not admit (#199)

    [<Literal>]
    let PilotValueUnadmitted = "FS9201" // pilot key value outside the values its key admits (#199)

    [<Literal>]
    let PilotEntryRepeated = "FS9202" // pilot array declaring one record's entry more than once (#199)

    [<Literal>]
    let CallbackContextInvalid = "FS9203" // callback context naming no untyped pointer entry argument and receiving parameter (#189)

    [<Literal>]
    let CountedArrayInvalid = "FS9204" // counted entry array that is no pointer array of its element with an integer count (#190)

    [<Literal>]
    let PointerArrayUndeclared = "FS9205" // pointer-to-pointer callback entry argument without a counted-array declaration (#190)

    [<Literal>]
    let RetentionInvalid = "FS9206" // retained callback whose owner, redefinition key or release does not fit its registration (#191)

    [<Literal>]
    let RetainedCallbackCaptures = "FS9207" // retained callback registered with a captured environment (#191)

    [<Literal>]
    let BorrowedResultShape = "FS9208" // borrowed result that is no single-level pointer to its content's bytes, or also a handle or resource (#196)

    [<Literal>]
    let BorrowedExtentInvalid = "FS9209" // borrowed result extent that is no integer companion call on the borrowing call's arguments (#196)

    [<Literal>]
    let BorrowedInvalidationInvalid = "FS9210" // borrowed result whose owner is no opaque handle, or an invalidation that does not receive it (#196)

    [<Literal>]
    let CppPimplClassUnadmitted = "FS9211" // C++ pimpl class without an admitted storage, ownership and method ABI contract (#116)

    [<Literal>]
    let CppValueClassUnadmitted = "FS9212" // C++ value class without an admitted storage, ownership and method ABI contract (#116)

    [<Literal>]
    let CppInterfaceClassUnadmitted = "FS9213" // C++ abstract interface class without an admitted storage, ownership and method ABI contract (#116)

    [<Literal>]
    let CppOpaqueClassUnadmitted = "FS9214" // C++ opaque class without an admitted storage, ownership and method ABI contract (#116)

    [<Literal>]
    let CppTemplateInstantiationMissing = "FS9215" // requested C++ template instantiation is not exported by the inspected library build (#118)

    [<Literal>]
    let CppAbiFactInvalidOrConflicting = "FS9216" // C++ ABI fact from Plugify is missing, lacks provenance, or conflicts with the inspected library build or clang AST (#119)

    [<Literal>]
    let CppObjectUseAfterRelease = "FS9217" // marshaled C++ object used after its D1 release (#115)

    [<Literal>]
    let CppObjectDoubleRelease = "FS9218" // marshaled C++ object's D1 release called more than once (#115)

    [<Literal>]
    let CppContainerOutOfRange = "FS9219" // template container access out of range or invalid container operation contract (#120)

    // =========================================================================
    // Structured diagnostics (#184)
    // =========================================================================

    /// How a diagnostic stops generation. Every FS91xx refusal is an `Error`.
    [<RequireQualifiedAccess>]
    type Severity =
        | Error
        | Warning

    /// A fact a diagnostic carries, or the exact reason it is unknown. A location is never
    /// invented: where clang (or the reader) gives none, the reason says so.
    [<RequireQualifiedAccess>]
    type Located<'T> =
        | Known of 'T
        | Unknown of reason: string

    /// A position in a source file as clang gives it: 1-based line and byte column.
    type SourcePosition = { Line: int; Column: int }

    /// Where a refusal's cause is declared: the header (or protocol file) path, and the
    /// position in it when one is known.
    type SourceLocation = { Path: string; Position: Located<SourcePosition> }

    /// The pilot input that caused a refusal: the `.pilot.toml` file, the TOML key (dotted,
    /// `library.defines`; a binding entry as `options.bindings[<name>]`, a key of it as
    /// `options.bindings[<name>].string_parameters`) whose value the refusal names, and the native
    /// parameter index the refusal names under that key, when it names one (#195). Generation
    /// knows the key but not the pilot file: it leaves `PilotFile` empty, and the project step
    /// that read the pilot binds it (`BindingGenerator`).
    type PilotLocation = { PilotFile: string; Key: string; Parameter: int option }

    /// One structured refusal (#184).
    type Diagnostic = {
        /// The FS91xx code.
        Code: string
        /// The refusal's text after its code; `render` gives the text Farscape prints.
        Message: string
        Severity: Severity
        /// Where the declaration that caused the refusal is written, or why that is unknown.
        Source: Located<SourceLocation>
        /// Further declarations the refusal names, in the order it names them (a record or enum
        /// declared differently by several headers names each of them).
        Related: SourceLocation list
        /// The pilot file and key, when pilot input caused the refusal; None otherwise.
        Pilot: PilotLocation option
    }

    /// The text Farscape prints for a diagnostic: `CODE: message`.
    let render (d: Diagnostic) = d.Code + ": " + d.Message

    /// Where a diagnostic's cause is declared and which pilot input caused it, one line each, as
    /// the CLI prints them under the refusal's text (#195): `CODE declared at path:line:column`
    /// (or the file and why no position is known, or why no source is known), then, when pilot
    /// input caused it, `CODE pilot key KEY[, parameter N] in FILE`.
    let renderLocation (d: Diagnostic) : string list =
        let source =
            match d.Source with
            | Located.Known { Path = path; Position = Located.Known p } -> $"{d.Code} declared at {path}:{p.Line}:{p.Column}"
            | Located.Known { Path = path; Position = Located.Unknown reason } -> $"{d.Code} declared in {path} ({reason})"
            | Located.Unknown reason -> $"{d.Code} source unknown: {reason}"
        let pilot =
            d.Pilot |> Option.map (fun p ->
                let parameter = p.Parameter |> Option.map (fun i -> $", parameter {i}") |> Option.defaultValue ""
                let file = if p.PilotFile = "" then "" else $" in {p.PilotFile}"
                // A refusal of the whole pilot file (FS9197) names the file and no key.
                if p.Key = "" then $"{d.Code} pilot file {p.PilotFile}"
                else $"{d.Code} pilot key {p.Key}{parameter}{file}")
        source :: Option.toList pilot

    /// An FS91xx refusal with the given source and no pilot location.
    let diagnostic (code: string) (message: string) (source: Located<SourceLocation>) : Diagnostic =
        { Code = code; Message = message; Severity = Severity.Error; Source = source; Related = []; Pilot = None }

    /// A source that is known only by its file: `reason` says why no position is known.
    let inFile (path: string) (reason: string) : Located<SourceLocation> =
        Located.Known { Path = path; Position = Located.Unknown reason }

    /// The diagnostic with the given pilot cause.
    let withPilot (pilot: PilotLocation) (d: Diagnostic) = { d with Pilot = Some pilot }

    /// The diagnostic with the given pilot key and parameter index, its pilot file left for the
    /// project step to bind (#195).
    let withPilotKey (key: string) (parameter: int option) (d: Diagnostic) =
        { d with Pilot = Some { PilotFile = ""; Key = key; Parameter = parameter } }

    /// The diagnostic with further named declarations.
    let withRelated (related: SourceLocation list) (d: Diagnostic) = { d with Related = related }

    /// The diagnostics of one failure and the exact text Farscape prints for it. A failure
    /// that is no FS91xx refusal carries no diagnostics, only its text.
    type Refusal = { Diagnostics: Diagnostic list; Text: string }

    /// One diagnostic's refusal: its text is `render` of it.
    let refusalOf (d: Diagnostic) : Refusal = { Diagnostics = [ d ]; Text = render d }

    /// Several diagnostics' refusal, as the pilot checks report them: each rendered, joined by
    /// spaces, followed by `suffix`.
    let refusalsOf (ds: Diagnostic list) (suffix: string) : Refusal =
        { Diagnostics = ds; Text = String.concat " " (ds |> List.map render) + suffix }

    /// A failure that is no FS91xx refusal: its text only.
    let uncoded (text: string) : Refusal = { Diagnostics = []; Text = text }

    /// The refusal with `prefix` before its text, as an enclosing step reports it.
    let prefixed (prefix: string) (r: Refusal) = { r with Text = prefix + r.Text }

    /// The refusal with `suffix` after its text.
    let suffixed (suffix: string) (r: Refusal) = { r with Text = r.Text + suffix }

    /// The diagnostics of several refusals, with their texts joined by `separator`.
    let joined (separator: string) (rs: Refusal list) : Refusal =
        { Diagnostics = rs |> List.collect _.Diagnostics; Text = rs |> List.map _.Text |> String.concat separator }

    /// Each diagnostic of the refusal mapped by `f`; the text is unchanged.
    let mapDiagnostics (f: Diagnostic -> Diagnostic) (r: Refusal) = { r with Diagnostics = r.Diagnostics |> List.map f }

    /// A refusal raised from inside generation; its message is the refusal's text.
    type RefusalException(refusal: Refusal) =
        inherit System.Exception(refusal.Text)
        member _.Refusal = refusal

    /// Raise a refusal.
    let raiseRefusal (r: Refusal) : 'T = raise (RefusalException r)

    /// Raise one diagnostic's refusal.
    let refuse (d: Diagnostic) : 'T = raiseRefusal (refusalOf d)

    /// Where a declaration is written, as clang (or the reader that produced it) gives it. It
    /// is the declaration's provenance, not part of its shape: two declarations that differ only
    /// in where they are written compare equal, so every check that compares declarations (the
    /// divergence checks, the merge) compares what they declare, as before locations were kept.
    [<CustomEquality; CustomComparison>]
    type Provenance =
        | Provenance of Located<SourceLocation>
        member this.Location = match this with Provenance location -> location
        override _.Equals(other: obj) = other :? Provenance
        override _.GetHashCode() = 0
        interface System.IComparable with
            member _.CompareTo(other: obj) =
                match other with
                | :? Provenance -> 0
                | _ -> invalidArg "other" "a Provenance compares only with a Provenance"

    /// The provenance of a declaration no reader located; `reason` says why.
    let unlocated (reason: string) = Provenance (Located.Unknown reason)
