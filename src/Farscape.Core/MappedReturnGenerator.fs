namespace Farscape.Core

open PilotTypes
open CodeAST

/// A mapped result is projected at its actual foreign acquisition boundary.
/// This emitter creates only source contracts; the compiler realizes the loan,
/// callback, native output cells, and matching release without a C shim.
module MappedReturnGenerator =
    let private quoted (value: string) = Literal ("\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
    let private input name = FunctionCall("", "Input", [quoted name])
    let private output name = FunctionCall("", "Output", [quoted name])
    let private declaration name typ fields = ValueBinding(name, Generic("Expr", Named typ), Quoted (RecordBlock fields))

    let mappings (project: PilotProject) =
        project.Options |> Option.map (fun options -> options.MappedReturns) |> Option.defaultValue []

    let markers project = mappings project |> List.map (fun mapping -> OpaqueMarker mapping.Schema) |> List.distinct

    let generate (project: PilotProject) (ctx: FidelityCodeGenerator.GenerationContext)
                 (declarations: CppParser.Declaration list) (ns: NamespaceSpec) (typesModule: string) =
        // Each refusal names its [[options.mapped_returns]] entry and key (#195), and is located
        // at the native declaration it concerns (the acquisition unless the release is meant).
        let refuseAt code (source: Diagnostics.Located<Diagnostics.SourceLocation>) key parameter message =
            Diagnostics.diagnostic code message source |> Diagnostics.withPilotKey key parameter |> Diagnostics.refuse
        mappings project
        |> List.filter (fun mapping -> ns.Functions |> List.contains mapping.Acquire)
        |> List.collect (fun mapping ->
            let key field = $"options.mapped_returns[{mapping.Name}].{field}"
            let parsed field name =
                declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = name -> Some f | _ -> None)
                |> Option.defaultWith (fun () ->
                    refuseAt Diagnostics.BindingSymbolUndeclared (Diagnostics.Located.Unknown $"no parsed header declares C symbol '{name}'")
                             (key field) None $"Mapped projection requires parsed native function '{name}'")
            // A constant projection would hide native parameters behind the public binding this
            // reads (its wrapper is named func.Name), so it is refused from the binding itself.
            // generateFunctionDecls always emits a LetBinding named func.Name: the extern, or the
            // constant-projection wrapper.
            let signature field (func: CppParser.FunctionDecl) =
                let hidden = ctx.Bindings |> Map.tryFind func.Name |> Option.exists (fun b -> b.ConstantParameters |> List.exists (System.String.IsNullOrWhiteSpace >> not))
                if hidden then
                    refuseAt Diagnostics.ContractRequiresUntranslated func.Location.Location (key field) None
                             $"Mapped acquisition '{func.Name}' cannot also hide native parameters through constant projections"
                FidelityCodeGenerator.generateFunctionDecls ctx ns.Library func
                |> List.pick (function LetBinding(name, parameters, result, _, _) when name = func.Name -> Some(parameters, result) | _ -> None)
            let acquire = parsed "acquire" mapping.Acquire
            let release = parsed "release" mapping.Release
            let parameters, returnType = signature "acquire" acquire
            let releaseParameters, releaseType = signature "release" release
            let parameter field name =
                match parameters |> List.tryFindIndex (fun p -> p.Name = name) with
                | Some index -> parameters[index]
                | None ->
                    refuseAt Diagnostics.ParameterNameUndeclared acquire.Location.Location (key field) None
                             $"Mapped projection '{mapping.Name}' requires native parameter '{name}'"
            let owner = parameter "owner_parameter" mapping.OwnerParameter
            let stride = parameter "stride_parameter" mapping.StrideParameter
            let cookie = parameter "cookie_parameter" mapping.CookieParameter
            let rows = parameter "rows_parameter" mapping.RowsParameter
            let width = parameter "width_parameter" mapping.WidthParameter
            let refuse code (func: CppParser.FunctionDecl) field message =
                refuseAt code func.Location.Location (key field) None $"Mapped projection '{mapping.Name}': {message}"
            let shape field message = refuse Diagnostics.MappedSignatureShape acquire field message
            let releaseShape message = refuse Diagnostics.MappedSignatureShape release "release" message
            let invalid field message = refuse Diagnostics.MappedValueInvalid acquire field message
            let opaque = function Generic("CHandle", _) -> true | _ -> false
            if not (opaque owner.Type) then shape "owner_parameter" "the owner must be a nonnull opaque handle"
            if stride.Type <> Named "int array" || rows.Type <> Named "int" || width.Type <> Named "int" then shape "stride_parameter" "row extent must use declared scalar inputs and one stride output"
            let cookieText = CodeRenderer.renderType cookie.Type
            if not (cookieText.StartsWith("option<CHandle<") && cookieText.EndsWith(" array")) then shape "cookie_parameter" "the release cookie must be one nullable opaque pointer output"
            match returnType with Generic("option", Generic("CHandle", _)) -> () | _ -> shape "acquire" "the acquisition must return a nullable native pointer"
            if CodeRenderer.renderType releaseType <> "unit" || releaseParameters.Length <> 2 then releaseShape "release must be a native void binding taking owner and cookie"
            if releaseParameters.[0].Type <> owner.Type || CodeRenderer.renderType releaseParameters.[1].Type + " array" <> cookieText then releaseShape "release parameters must match the original owner and output cookie"
            if mapping.FailureStatus >= 0 then invalid "failure_status" "adapter failure status must be negative"
            if not (["u8"; "u16"; "u32"; "u64"; "i8"; "i16"; "i32"; "i64"] |> List.contains mapping.Element) then invalid "element" "an integer scalar element representation is required"
            if mapping.Alignment <= 0 || (mapping.Alignment &&& (mapping.Alignment - 1)) <> 0 then invalid "alignment" "alignment must be a positive power of two"
            if not (["ro"; "wo"; "rw"] |> List.contains mapping.Access) then invalid "access" "access must be ro, wo, or rw"
            if mapping.Schema = "" || mapping.Schema.Contains('.') || not (System.Char.IsUpper mapping.Schema.[0]) then invalid "schema" "schema must be a local nominal marker beginning with an uppercase letter"
            if not (ns.Functions |> List.contains mapping.Release) then refuse Diagnostics.ContractBindingUnselected release "release" "release must be emitted in the same namespace"
            let inputs = parameters |> List.filter (fun p -> p.Name <> stride.Name && p.Name <> cookie.Name)
            if inputs |> List.exists (fun p -> (CodeRenderer.renderType p.Type).EndsWith(" array")) then shape "acquire" "all native output cells must be declared by this mapped projection"
            let fullName = ns.Name + "." + mapping.Name
            let schema = typesModule + "." + mapping.Schema
            let work = { Name = "work"; Type = FunctionType([Generic("BorrowedView", Named mapping.Schema)], Named "int") }
            [ XmlDoc "Borrow native mapped storage only until work and every synchronous child callback retire. The compiler performs the paired release; no interior address or pixel copy is exposed."
              LetBinding(mapping.Name, inputs @ [work], Named "int", NativeZeroed, [])
              BlankLine
              declaration (mapping.Name + "Layout") "ViewLayoutDescriptor"
                [ "Schema", quoted schema; "Element", quoted mapping.Element
                  "Alignment", Literal (string mapping.Alignment); "Access", quoted mapping.Access ]
              declaration (mapping.Name + "MappedReturnDescriptor") "MappedReturnDescriptor"
                [ "Binding", quoted fullName; "Acquire", quoted (ns.Name + "." + mapping.Acquire)
                  "Release", quoted (ns.Name + "." + mapping.Release); "Layout", quoted schema
                  "CallbackParameter", quoted work.Name; "Owner", input owner.Name
                  "RowStride", output stride.Name; "RowCount", input rows.Name; "RowWidth", input width.Name
                  "ReleaseArguments", ArrayBlock [input owner.Name; output cookie.Name]
                  "FailureStatus", Literal (string mapping.FailureStatus) ]
              declaration (mapping.Name + "ScopedCallbackDescriptor") "ScopedCallbackDescriptor"
                [ "Binding", quoted fullName; "Parameter", quoted work.Name ]
              BlankLine ])
