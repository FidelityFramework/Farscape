module Farscape.Tests.PilotSerializerTests

open Xunit
open Farscape.Core

// =============================================================================
// PilotSerializer Tests: TOML round-trip and deserialization
// =============================================================================

let private sampleProject : PilotTypes.PilotProject = {
    Library = {
        Name = "libc"
        Headers = ["/usr/include/string.h"]
        IncludePaths = ["/usr/include"]
        Defines = ["_GNU_SOURCE"]
    }
    Output = { Mode = "fidelity"; Directory = "./bindings" }
    Namespaces = [
        { Name = "Fidelity.libc.Memory"
          Description = "Memory operations"
          Library = "libc"
          Prefixes = ["mem"; "str"]
          Functions = [] }
        { Name = "Fidelity.libc.IO"
          Description = "I/O operations"
          Library = "libc"
          Prefixes = ["read"; "write"]
          Functions = ["pipe"] }
    ]
    ErrorConventions = None
}

[<Fact>]
let ``serialize produces valid TOML with all sections`` () =
    let toml = PilotSerializer.toTomlString sampleProject
    Assert.Contains("name = \"libc\"", toml)
    Assert.Contains("header = \"/usr/include/string.h\"", toml)
    Assert.Contains("mode = \"fidelity\"", toml)
    Assert.Contains("directory = \"./bindings\"", toml)
    Assert.Contains("[[namespace]]", toml)
    Assert.Contains("Fidelity.libc.Memory", toml)
    Assert.Contains("Fidelity.libc.IO", toml)

[<Fact>]
let ``round-trip serialize then deserialize produces identical project`` () =
    let toml = PilotSerializer.toTomlString sampleProject
    match Fidelity.Toml.Toml.parse toml with
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Ok roundTripped ->
            Assert.Equal(sampleProject.Library.Name, roundTripped.Library.Name)
            Assert.Equal(sampleProject.Library.Header, roundTripped.Library.Header)
            Assert.Equal<string list>(sampleProject.Library.IncludePaths, roundTripped.Library.IncludePaths)
            Assert.Equal<string list>(sampleProject.Library.Defines, roundTripped.Library.Defines)
            Assert.Equal(sampleProject.Output.Mode, roundTripped.Output.Mode)
            Assert.Equal(sampleProject.Output.Directory, roundTripped.Output.Directory)
            Assert.Equal(sampleProject.Namespaces.Length, roundTripped.Namespaces.Length)
            Assert.Equal(sampleProject.Namespaces[0].Name, roundTripped.Namespaces[0].Name)
            Assert.Equal<string list>(sampleProject.Namespaces[0].Prefixes, roundTripped.Namespaces[0].Prefixes)
            Assert.Equal<string list>(sampleProject.Namespaces[1].Functions, roundTripped.Namespaces[1].Functions)
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
    | Error e -> Assert.Fail $"Parse failed: {e}"

[<Fact>]
let ``deserialize returns Error for missing library section`` () =
    let doc = Fidelity.Toml.Toml.parseOrFail "[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    match PilotSerializer.deserialize doc with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should return Error for missing [library]"

[<Fact>]
let ``deserialize returns Error for missing output section`` () =
    let doc = Fidelity.Toml.Toml.parseOrFail "[library]\nname = \"test\"\nheader = \"test.h\""
    match PilotSerializer.deserialize doc with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should return Error for missing [output]"

[<Fact>]
let ``deserialize handles empty namespace array`` () =
    let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    let doc = Fidelity.Toml.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok project -> Assert.Empty(project.Namespaces)
    | Error e -> Assert.Fail $"Should succeed with no namespaces: {e}"

[<Fact>]
let ``deserialize handles optional functions field`` () =
    let toml = """
[library]
name = "test"
header = "test.h"
[output]
mode = "fidelity"
directory = "./out"
[[namespace]]
name = "Test.Str"
description = "String ops"
library = "test"
prefixes = ["str"]
"""
    let doc = Fidelity.Toml.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok project ->
        Assert.Equal(1, project.Namespaces.Length)
        Assert.Empty(project.Namespaces[0].Functions)
    | Error e -> Assert.Fail $"Should succeed: {e}"

[<Fact>]
let ``loadFromFile returns Error for nonexistent file`` () =
    match PilotSerializer.loadFromFile "/nonexistent/path.pilot.toml" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should return Error for missing file"

[<Fact>]
let ``multi-header project serializes with headers array`` () =
    let project = { sampleProject with
                      Library = { sampleProject.Library with
                                    Headers = ["/usr/include/unistd.h"; "/usr/include/fcntl.h"] } }
    let toml = PilotSerializer.toTomlString project
    Assert.Contains("headers", toml)
    Assert.Contains("/usr/include/unistd.h", toml)
    Assert.Contains("/usr/include/fcntl.h", toml)

[<Fact>]
let ``multi-header round-trip preserves all headers`` () =
    let project = { sampleProject with
                      Library = { sampleProject.Library with
                                    Headers = ["/usr/include/unistd.h"; "/usr/include/fcntl.h"] } }
    let toml = PilotSerializer.toTomlString project
    match Fidelity.Toml.Toml.parse toml with
    | Ok doc ->
        match PilotSerializer.deserialize doc with
        | Ok roundTripped ->
            Assert.Equal<string list>(project.Library.Headers, roundTripped.Library.Headers)
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
    | Error e -> Assert.Fail $"Parse failed: {e}"

[<Fact>]
let ``single header backward compat still works`` () =
    let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    let doc = Fidelity.Toml.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Ok project -> Assert.Equal<string list>(["test.h"], project.Library.Headers)
    | Error e -> Assert.Fail $"Should parse single header: {e}"

[<Fact>]
let ``deserialize rejects empty headers array`` () =
    let toml = "[library]\nname = \"test\"\nheaders = []\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
    let doc = Fidelity.Toml.Toml.parseOrFail toml
    match PilotSerializer.deserialize doc with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Should reject empty headers"

// =============================================================================
// Error Convention TOML Tests
// =============================================================================

module ErrorConventionTomlTests =

    open PilotTypes

    [<Fact>]
    let ``error conventions round-trip through TOML`` () =
        let project : PilotProject = {
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; IncludePaths = []; Defines = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some {
                Default = Errno
                Overrides = Map.ofList [("pthread_create", ReturnCode); ("strtol", NoErrorConvention)]
            }
        }
        let toml = PilotSerializer.toTomlString project
        Assert.Contains("error_conventions", toml)
        Assert.Contains("errno", toml)
        match Fidelity.Toml.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok roundTripped ->
                Assert.True(roundTripped.ErrorConventions.IsSome)
                let spec = roundTripped.ErrorConventions.Value
                Assert.Equal(Errno, spec.Default)
                Assert.Equal(ReturnCode, spec.Overrides.["pthread_create"])
                Assert.Equal(NoErrorConvention, spec.Overrides.["strtol"])

    [<Fact>]
    let ``missing error_conventions deserializes as None`` () =
        let toml = "[library]\nname = \"test\"\nheader = \"test.h\"\n[output]\nmode = \"fidelity\"\ndirectory = \"./out\""
        let doc = Fidelity.Toml.Toml.parseOrFail toml
        match PilotSerializer.deserialize doc with
        | Error e -> Assert.Fail $"Deserialize failed: {e}"
        | Ok project -> Assert.True(project.ErrorConventions.IsNone)

    [<Fact>]
    let ``error conventions with no overrides`` () =
        let project : PilotProject = {
            Library = { Name = "libc"; Headers = ["/usr/include/stdio.h"]; IncludePaths = []; Defines = [] }
            Output = { Mode = "fidelity"; Directory = "./out" }
            Namespaces = []
            ErrorConventions = Some { Default = Errno; Overrides = Map.empty }
        }
        let toml = PilotSerializer.toTomlString project
        match Fidelity.Toml.Toml.parse toml with
        | Error e -> Assert.Fail $"Parse failed: {e}"
        | Ok doc ->
            match PilotSerializer.deserialize doc with
            | Error e -> Assert.Fail $"Deserialize failed: {e}"
            | Ok rt ->
                Assert.True(rt.ErrorConventions.IsSome)
                Assert.Equal(Errno, rt.ErrorConventions.Value.Default)
                Assert.True(rt.ErrorConventions.Value.Overrides.IsEmpty)
