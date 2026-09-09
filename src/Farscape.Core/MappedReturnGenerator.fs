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
        let parsed name =
            declarations |> List.tryPick (function CppParser.Declaration.Function f when f.Name = name -> Some f | _ -> None)
            |> Option.defaultWith (fun () -> failwith $"Mapped projection requires parsed native function '{name}'")
        let signature func =
            FidelityCodeGenerator.generateFunctionDecls ctx ns.Library func
            |> List.tryPick (function LetBinding(name, parameters, result, _, _) when name = func.Name -> Some(parameters, result) | _ -> None)
            |> Option.defaultWith (fun () -> failwith $"Mapped acquisition '{func.Name}' cannot also hide native parameters through constant projections")
        mappings project
        |> List.filter (fun mapping -> ns.Functions |> List.contains mapping.Acquire)
        |> List.collect (fun mapping ->
            let acquire = parsed mapping.Acquire
            let release = parsed mapping.Release
            let parameters, returnType = signature acquire
            let releaseParameters, releaseType = signature release
            let parameter name =
                parameters |> List.tryFind (fun p -> p.Name = name)
                |> Option.defaultWith (fun () -> failwith $"Mapped projection '{mapping.Name}' requires native parameter '{name}'")
            let owner = parameter mapping.OwnerParameter
            let stride = parameter mapping.StrideParameter
            let cookie = parameter mapping.CookieParameter
            let rows = parameter mapping.RowsParameter
            let width = parameter mapping.WidthParameter
            let fail message = failwith $"Mapped projection '{mapping.Name}': {message}"
            let opaque = function Generic("CHandle", _) -> true | _ -> false
            if not (opaque owner.Type) then fail "the owner must be a nonnull opaque handle"
            if stride.Type <> Named "int array" || rows.Type <> Named "int" || width.Type <> Named "int" then fail "row extent must use declared scalar inputs and one stride output"
            let cookieText = CodeRenderer.renderType cookie.Type
            if not (cookieText.StartsWith("option<CHandle<") && cookieText.EndsWith(" array")) then fail "the release cookie must be one nullable opaque pointer output"
            match returnType with Generic("option", Generic("CHandle", _)) -> () | _ -> fail "the acquisition must return a nullable native pointer"
            if CodeRenderer.renderType releaseType <> "unit" || releaseParameters.Length <> 2 then fail "release must be a native void binding taking owner and cookie"
            if releaseParameters.[0].Type <> owner.Type || CodeRenderer.renderType releaseParameters.[1].Type + " array" <> cookieText then fail "release parameters must match the original owner and output cookie"
            if mapping.FailureStatus >= 0 then fail "adapter failure status must be negative"
            if not (["u8"; "u16"; "u32"; "u64"; "i8"; "i16"; "i32"; "i64"] |> List.contains mapping.Element) then fail "an integer scalar element representation is required"
            if mapping.Alignment <= 0 || (mapping.Alignment &&& (mapping.Alignment - 1)) <> 0 then fail "alignment must be a positive power of two"
            if not (["ro"; "wo"; "rw"] |> List.contains mapping.Access) then fail "access must be ro, wo, or rw"
            if mapping.Schema = "" || mapping.Schema.Contains('.') || not (System.Char.IsUpper mapping.Schema.[0]) then fail "schema must be a local nominal marker beginning with an uppercase letter"
            if not (ns.Functions |> List.contains mapping.Release) then fail "release must be emitted in the same namespace"
            let inputs = parameters |> List.filter (fun p -> p.Name <> stride.Name && p.Name <> cookie.Name)
            if inputs |> List.exists (fun p -> (CodeRenderer.renderType p.Type).EndsWith(" array")) then fail "all native output cells must be declared by this mapped projection"
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
