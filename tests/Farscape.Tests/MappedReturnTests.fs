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
