module Farscape.Tests.MappedReturnTests

open Xunit
open Farscape.Core

let private project =
    """
[library]
name="sample"
headers=["sample.h"]
[output]
mode="fidelity"
directory="out"
[options]
c_header_mode=true
[[options.mapped_returns]]
name="withMapping"
acquire="acquire"
release="release"
schema="Pixels"
element="u32"
alignment=4
access="wo"
owner_parameter="owner"
stride_parameter="stride"
rows_parameter="height"
width_parameter="width"
cookie_parameter="cookie"
failure_status=-1
[[options.bindings]]
name="acquire"
reference_parameters=[3,4]
[annotations.nonnull]
acquire=[0,3,4]
release=[0]
[[namespace]]
name="Sample.Native"
library="sample"
description="mapped fixture"
functions=["acquire","release"]
"""
    |> Fidelity.Data.TOML.Toml.parseOrFail |> PilotSerializer.deserialize
    |> function Ok project -> project | Error error -> failwith error

let private declarations =
    [ TestHelpers.mkFunc "acquire" "void *" ["owner","struct owner *";"width","uint32_t";"height","uint32_t";"stride","uint32_t *";"cookie","void **"]
      TestHelpers.mkFunc "release" "void" ["owner","struct owner *";"cookie","void *"] ]
    |> List.map CppParser.Declaration.Function

let private generate (project: PilotTypes.PilotProject) =
    let ctx = { FidelityCodeGenerator.buildGenerationContext declarations Types.LP64 Map.empty with
                    NonnullAnnotations = project.Nonnull
                    Bindings = project.Options.Value.Bindings |> List.map (fun b -> b.Name,b) |> Map.ofList }
    MappedReturnGenerator.generate project ctx declarations project.Namespaces.Head "Sample.Native.Types"
    |> List.map CodeRenderer.render |> String.concat "\n"

[<Fact>]
let ``mapped projection preserves native arity and declares its bounded scope`` () =
    let source = generate project
    Assert.Contains("(work: BorrowedView<Pixels> -> int)", source)
    Assert.DoesNotContain("FidelityExtern", source)
    Assert.Contains("Acquire = \"Sample.Native.acquire\"", source)
    Assert.Contains("RowStride = Output \"stride\"", source)
    Assert.Contains("RowCount = Input \"height\"", source)
    Assert.Contains("Output \"cookie\"", source)
    Assert.Contains("Schema = \"Sample.Native.Types.Pixels\"", source)
    Assert.Contains("Element = \"u32\"", source)
    Assert.Contains("ScopedCallbackDescriptor", source)
    Assert.DoesNotContain("nativeint", source)
    Assert.DoesNotContain("NativePtr", source)
    let restored = PilotSerializer.serialize project |> PilotSerializer.deserialize |> function Ok p -> p | Error e -> failwith e
    Assert.True(project.Options.Value.MappedReturns = restored.Options.Value.MappedReturns)

[<Fact>]
let ``mapped projection rejects a pointer cookie used as row stride`` () =
    let options = project.Options.Value
    let mapping = { options.MappedReturns.Head with StrideParameter = "cookie" }
    Assert.ThrowsAny<System.Exception>(fun () -> generate { project with Options = Some { options with MappedReturns = [mapping] } } |> ignore) |> ignore

// Mapped-return refusals are structured (#195): each names its [[options.mapped_returns]] key
// and the native declaration it concerns.
[<Theory>]
[<InlineData("undeclared", "FS9159", "release", "Mapped projection requires parsed native function 'missing'")>]
[<InlineData("constant", "FS9135", "acquire", "Mapped acquisition 'acquire' cannot also hide native parameters through constant projections")>]
[<InlineData("parameter", "FS9164", "owner_parameter", "Mapped projection 'withMapping' requires native parameter 'nope'")>]
[<InlineData("shape", "FS9166", "stride_parameter", "Mapped projection 'withMapping': row extent must use declared scalar inputs and one stride output")>]
[<InlineData("value", "FS9167", "failure_status", "Mapped projection 'withMapping': adapter failure status must be negative")>]
[<InlineData("namespace", "FS9141", "release", "Mapped projection 'withMapping': release must be emitted in the same namespace")>]
let ``each mapped-return refusal carries its code and its mapped_returns key`` shape (code: string) (field: string) (message: string) =
    let options = project.Options.Value
    let mapping = options.MappedReturns.Head
    let changed =
        match shape with
        | "undeclared" -> { project with Options = Some { options with MappedReturns = [{ mapping with Release = "missing" }] } }
        | "constant" ->
            let bindings = options.Bindings |> List.map (fun b -> if b.Name = "acquire" then { b with ConstantParameters = [""; "7"] } else b)
            { project with Options = Some { options with Bindings = bindings } }
        | "parameter" -> { project with Options = Some { options with MappedReturns = [{ mapping with OwnerParameter = "nope" }] } }
        | "shape" -> { project with Options = Some { options with MappedReturns = [{ mapping with StrideParameter = "cookie" }] } }
        | "value" -> { project with Options = Some { options with MappedReturns = [{ mapping with FailureStatus = 1 }] } }
        | _ -> { project with Namespaces = [{ project.Namespaces.Head with Functions = ["acquire"] }] }
    let error = Assert.Throws<Diagnostics.RefusalException>(fun () -> generate changed |> ignore)
    let d = error.Refusal.Diagnostics |> List.exactlyOne
    Assert.Equal(code, d.Code)
    Assert.Equal(message, d.Message)
    Assert.Equal(Some { Diagnostics.PilotLocation.PilotFile = ""; Diagnostics.PilotLocation.Key = $"options.mapped_returns[withMapping].{field}"; Diagnostics.PilotLocation.Parameter = None }, d.Pilot)
    if shape <> "undeclared" then Assert.Equal(TestHelpers.constructed.Location, d.Source)
