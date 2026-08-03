module PilotDiscoveryTests

open Xunit
open Farscape.Core.PilotDiscovery

// =========================================================================
// Internal Path Detection
// =========================================================================

module InternalPathTests =

    [<Fact>]
    let ``isInternalPath detects _internal.h suffix`` () =
        Assert.True(isInternalPath "foo/bar_internal.h")

    [<Fact>]
    let ``isInternalPath detects _private.h suffix`` () =
        Assert.True(isInternalPath "hip_runtime_private.h")

    [<Fact>]
    let ``isInternalPath detects _impl.h suffix`` () =
        Assert.True(isInternalPath "codec_impl.h")

    [<Fact>]
    let ``isInternalPath detects detail directory`` () =
        Assert.True(isInternalPath "include/detail/helpers.h")

    [<Fact>]
    let ``isInternalPath detects internal directory`` () =
        Assert.True(isInternalPath "src/internal/core.h")

    [<Fact>]
    let ``isInternalPath detects private directory`` () =
        Assert.True(isInternalPath "lib/private/secret.hpp")

    [<Fact>]
    let ``isInternalPath returns false for public header`` () =
        Assert.False(isInternalPath "include/wayland-client.h")

    [<Fact>]
    let ``isInternalPath returns false for normal nested header`` () =
        Assert.False(isInternalPath "hip/hip_runtime_api.h")

    [<Fact>]
    let ``isInternalPath handles backslash paths`` () =
        Assert.True(isInternalPath "include\\detail\\helpers.h")

// =========================================================================
// Header Content Classification
// =========================================================================

module HeaderClassificationTests =

    [<Fact>]
    let ``classifyHeaderContent detects pure C header`` () =
        let content = """
#ifndef MY_LIB_H
#define MY_LIB_H
typedef struct my_handle my_handle;
int my_func(my_handle *h, int value);
void my_free(my_handle *h);
#endif
"""
        let isCpp, hasExternC = classifyHeaderContent content
        Assert.False(isCpp)
        Assert.False(hasExternC)

    [<Fact>]
    let ``classifyHeaderContent detects C++ namespace`` () =
        let content = """
namespace hip {
  class Stream { };
}
"""
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects extern C in C++ header`` () =
        let content = """
#include <stdint.h>
namespace hip { class Device { }; }
extern "C" {
  int hipInit(int flags);
  int hipGetDeviceCount(int *count);
}
"""
        let isCpp, hasExternC = classifyHeaderContent content
        Assert.True(isCpp)
        Assert.True(hasExternC)

    [<Fact>]
    let ``classifyHeaderContent detects template as C++`` () =
        let content = """
template<typename T>
T* allocate(size_t n);
template <class U>
void deallocate(U* p);
"""
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects std:: as C++`` () =
        let content = """
#include <string>
std::string getName();
"""
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

// =========================================================================
// C++ Standard Library Include Detection
// =========================================================================

module CppStdlibDetectionTests =

    [<Fact>]
    let ``classifyHeaderContent detects cerrno include as C++`` () =
        let content = """
#ifndef UTIL_H
#define UTIL_H
#include <cerrno>
int get_error();
#endif
"""
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects cstring include as C++`` () =
        let content = "#include <cstring>\nvoid copy(char *dst, const char *src);\n"
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects vector include as C++`` () =
        let content = "#include <vector>\n"
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects memory include as C++`` () =
        let content = "#include <memory>\n"
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects optional include as C++`` () =
        let content = "#include <optional>\nstruct Config {};\n"
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent does not flag C stdlib includes as C++`` () =
        let content = """
#include <stdlib.h>
#include <string.h>
#include <errno.h>
void init();
"""
        let isCpp, _ = classifyHeaderContent content
        Assert.False(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects unordered_map as C++`` () =
        let content = "#include <unordered_map>\n"
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

    [<Fact>]
    let ``classifyHeaderContent detects string_view as C++`` () =
        let content = "#include <string_view>\nvoid process(std::string_view sv);\n"
        let isCpp, _ = classifyHeaderContent content
        Assert.True(isCpp)

// =========================================================================
// Forwarding Header Detection
// =========================================================================

module ForwardingHeaderTests =

    [<Fact>]
    let ``isForwardingHeader detects single-include zero-declaration header`` () =
        let content = """
// Forwarding shim for compat
#include "../actual_header.h"
"""
        Assert.True(isForwardingHeader content)

    [<Fact>]
    let ``isForwardingHeader rejects header with declarations`` () =
        let content = """
#include "base.h"
int my_func(int x);
"""
        Assert.False(isForwardingHeader content)

    [<Fact>]
    let ``isForwardingHeader rejects header with multiple includes`` () =
        let content = """
#include "a.h"
#include "b.h"
"""
        Assert.False(isForwardingHeader content)

    [<Fact>]
    let ``isForwardingHeader rejects header with zero includes`` () =
        let content = "int standalone_func(void);\n"
        Assert.False(isForwardingHeader content)

// =========================================================================
// countDeclarations Comment Exclusion
// =========================================================================

module CountDeclarationsTests =

    [<Fact>]
    let ``countDeclarations counts function signatures`` () =
        let content = "int read(int fd, void *buf, size_t count);\nvoid write(int fd);\n"
        Assert.Equal(2, countDeclarations content)

    [<Fact>]
    let ``countDeclarations counts typedefs and structs`` () =
        let content = "typedef unsigned long size_t;\nstruct foo { int x; };\nenum bar { A, B };\n"
        Assert.Equal(3, countDeclarations content)

    [<Fact>]
    let ``countDeclarations excludes single-line comments`` () =
        let content = "// int not_a_decl(void);\nint real_decl(int x);\n"
        Assert.Equal(1, countDeclarations content)

    [<Fact>]
    let ``countDeclarations excludes block comment start lines`` () =
        let content = "/* Copyright (c) 2024 */\nint func(void);\n"
        Assert.Equal(1, countDeclarations content)

    [<Fact>]
    let ``countDeclarations excludes block comment continuation lines with parens`` () =
        // This was the bug: lines like "* Copyright (c) ..." matched as declarations
        let content = """
/*
 * Copyright (c) 2024 Xilinx, Inc.
 * Licensed under the Apache License, Version 2.0 (the "License")
 */
int actual_func(void);
"""
        Assert.Equal(1, countDeclarations content)

    [<Fact>]
    let ``countDeclarations excludes preprocessor directives`` () =
        let content = "#include <stdio.h>\n#define FOO(x) (x+1)\nint func(void);\n"
        Assert.Equal(1, countDeclarations content)

// =========================================================================
// Umbrella Header Detection
// =========================================================================

module UmbrellaDetectionTests =

    [<Fact>]
    let ``isUmbrellaHeader detects header with many includes and few declarations`` () =
        let content = """
#include "a.h"
#include "b.h"
#include "c.h"
#include "d.h"
#include "e.h"
#include "f.h"
#include "g.h"
"""
        Assert.True(isUmbrellaHeader content)

    [<Fact>]
    let ``isUmbrellaHeader rejects header with many own declarations`` () =
        let content = """
#include "types.h"
typedef int my_int;
struct foo { int x; };
enum bar { A, B, C };
int func_a(int x);
int func_b(int y);
int func_c(int z);
int func_d(int w);
void func_e(void);
void func_f(void);
"""
        Assert.False(isUmbrellaHeader content)

    [<Fact>]
    let ``isUmbrellaHeader rejects header with few includes`` () =
        let content = """
#include <stdint.h>
int my_func(int x);
"""
        Assert.False(isUmbrellaHeader content)

    [<Fact>]
    let ``countIncludes counts include directives`` () =
        let content = """
#include <stdio.h>
#include "mylib.h"
  #include <stdlib.h>
// not an include
"""
        Assert.Equal(3, countIncludes content)

// =========================================================================
// XML Protocol Classification
// =========================================================================

module XmlClassificationTests =

    [<Fact>]
    let ``classifyXml detects Wayland protocol`` () =
        let xml = """<?xml version="1.0" encoding="UTF-8"?>
<protocol name="wayland">
  <interface name="wl_display" version="1">
    <request name="sync"/>
  </interface>
</protocol>"""
        Assert.Equal(Some WaylandProtocol, classifyXml xml)

    [<Fact>]
    let ``classifyXml detects D-Bus introspection`` () =
        let xml = """<?xml version="1.0" encoding="UTF-8"?>
<node name="/org/freedesktop/DBus">
  <interface name="org.freedesktop.DBus"/>
</node>"""
        Assert.Equal(Some DBusIntrospection, classifyXml xml)

    [<Fact>]
    let ``classifyXml detects Vulkan registry`` () =
        let xml = """<?xml version="1.0" encoding="UTF-8"?>
<registry>
  <types><type name="VkInstance"/></types>
</registry>"""
        Assert.Equal(Some VulkanRegistry, classifyXml xml)

    [<Fact>]
    let ``classifyXml returns None for unknown root`` () =
        let xml = """<?xml version="1.0"?><catalog><book/></catalog>"""
        Assert.Equal(None, classifyXml xml)

    [<Fact>]
    let ``classifyXml returns None for invalid XML`` () =
        Assert.Equal(None, classifyXml "this is not xml {{{")

// =========================================================================
// Pkg-Config Parsing
// =========================================================================

module PkgConfigTests =

    [<Fact>]
    let ``parsePkgConfig extracts library name and include paths`` () =
        let pc = """
prefix=/usr/local
exec_prefix=${prefix}
libdir=${exec_prefix}/lib
includedir=${prefix}/include

Name: libwayland-client
Description: Wayland client library
Version: 1.22.0
Cflags: -I${includedir}
Libs: -L${libdir} -lwayland-client
"""
        let info = parsePkgConfig pc
        Assert.Equal("libwayland-client", info.Name)
        Assert.Equal(Some "wayland-client", info.LibraryName)
        Assert.Contains("/usr/local/include", info.IncludePaths)

    [<Fact>]
    let ``parsePkgConfig handles multiple include paths`` () =
        let pc = """
prefix=/opt/rocm
includedir=${prefix}/include

Name: hip
Cflags: -I${includedir} -I${includedir}/hip
Libs: -L${prefix}/lib -lamdhip64
"""
        let info = parsePkgConfig pc
        Assert.Equal("hip", info.Name)
        Assert.Equal(Some "amdhip64", info.LibraryName)
        Assert.Equal(2, info.IncludePaths.Length)

    [<Fact>]
    let ``parsePkgConfig handles minimal file`` () =
        let pc = """
Name: simple
Version: 1.0
"""
        let info = parsePkgConfig pc
        Assert.Equal("simple", info.Name)
        Assert.Equal(None, info.LibraryName)
        Assert.Empty(info.IncludePaths)

// =========================================================================
// File Classification (with mock IO)
// =========================================================================

module FileClassificationTests =

    let mockReader (files: Map<string, string>) (path: string) : string option =
        Map.tryFind path files

    [<Fact>]
    let ``classifyFile classifies C header`` () =
        let files = Map.ofList ["/root/include/api.h", "int api_init(void);"]
        let result = classifyFile "/root" "/root/include/api.h" (mockReader files)
        match result with
        | Some (CHeader (path, _, _)) -> Assert.Equal("include/api.h", path)
        | _ -> Assert.Fail("Expected CHeader")

    [<Fact>]
    let ``classifyFile classifies C++ header with extern C`` () =
        let content = """
namespace hip { class Device {}; }
extern "C" { int hipInit(int flags); }
"""
        let files = Map.ofList ["/root/hip.h", content]
        let result = classifyFile "/root" "/root/hip.h" (mockReader files)
        match result with
        | Some (CppHeader (_, hasExternC, _)) -> Assert.True(hasExternC)
        | _ -> Assert.Fail("Expected CppHeader with extern C")

    [<Fact>]
    let ``classifyFile classifies .hpp as C++ header`` () =
        let files = Map.ofList ["/root/api.hpp", "class Foo {};"]
        let result = classifyFile "/root" "/root/api.hpp" (mockReader files)
        match result with
        | Some (CppHeader _) -> ()
        | _ -> Assert.Fail("Expected CppHeader")

    [<Fact>]
    let ``classifyFile classifies Wayland protocol XML`` () =
        let xml = """<?xml version="1.0"?><protocol name="test"><interface name="t" version="1"/></protocol>"""
        let files = Map.ofList ["/root/test.xml", xml]
        let result = classifyFile "/root" "/root/test.xml" (mockReader files)
        match result with
        | Some (ProtocolXml (_, WaylandProtocol)) -> ()
        | _ -> Assert.Fail("Expected ProtocolXml with WaylandProtocol format")

    [<Fact>]
    let ``classifyFile classifies pkg-config`` () =
        let pc = "Name: test\nLibs: -ltest\n"
        let files = Map.ofList ["/root/test.pc", pc]
        let result = classifyFile "/root" "/root/test.pc" (mockReader files)
        match result with
        | Some (PkgConfig (_, info)) ->
            Assert.Equal("test", info.Name)
            Assert.Equal(Some "test", info.LibraryName)
        | _ -> Assert.Fail("Expected PkgConfig")

    [<Fact>]
    let ``classifyFile classifies CMakeLists.txt`` () =
        let result = classifyFile "/root" "/root/CMakeLists.txt" (fun _ -> None)
        match result with
        | Some (BuildSystemFile (_, CMake)) -> ()
        | _ -> Assert.Fail("Expected BuildSystemFile CMake")

    [<Fact>]
    let ``classifyFile classifies meson.build`` () =
        let result = classifyFile "/root" "/root/meson.build" (fun _ -> None)
        match result with
        | Some (BuildSystemFile (_, Meson)) -> ()
        | _ -> Assert.Fail("Expected BuildSystemFile Meson")

    [<Fact>]
    let ``classifyFile returns None for unknown extension`` () =
        let result = classifyFile "/root" "/root/readme.txt" (fun _ -> None)
        Assert.Equal(None, result)

    [<Fact>]
    let ``classifyFile marks internal headers`` () =
        let files = Map.ofList ["/root/detail/impl.h", "int internal_func();"]
        let result = classifyFile "/root" "/root/detail/impl.h" (mockReader files)
        match result with
        | Some (CHeader (_, _, isInternal)) -> Assert.True(isInternal)
        | _ -> Assert.Fail("Expected CHeader with isInternal=true")

    [<Fact>]
    let ``classifyFile detects umbrella header`` () =
        let content = """
#include "a.h"
#include "b.h"
#include "c.h"
#include "d.h"
#include "e.h"
#include "f.h"
"""
        let files = Map.ofList ["/root/umbrella.h", content]
        let result = classifyFile "/root" "/root/umbrella.h" (mockReader files)
        match result with
        | Some (CHeader (_, isUmbrella, _)) -> Assert.True(isUmbrella)
        | _ -> Assert.Fail("Expected CHeader with isUmbrella=true")

    [<Fact>]
    let ``classifyFile marks forwarding header as internal`` () =
        let content = "// Forwarding shim\n#include \"../real_header.h\"\n"
        let files = Map.ofList ["/root/experimental/compat.h", content]
        let result = classifyFile "/root" "/root/experimental/compat.h" (mockReader files)
        match result with
        | Some (CHeader (_, _, isInternal)) -> Assert.True(isInternal)
        | _ -> Assert.Fail("Expected CHeader with isInternal=true for forwarding header")

    [<Fact>]
    let ``classifyFile marks C++ forwarding header as internal`` () =
        let content = "// Forwarding shim\n#include \"../real_header.hpp\"\nnamespace foo {}\n"
        let files = Map.ofList ["/root/experimental/compat.hpp", content]
        let result = classifyFile "/root" "/root/experimental/compat.hpp" (mockReader files)
        match result with
        | Some (CppHeader (_, _, isInternal)) -> Assert.True(isInternal)
        | _ -> Assert.Fail("Expected CppHeader with isInternal=true for forwarding header")

    [<Fact>]
    let ``classifyFile detects C++ stdlib include in .h file`` () =
        let content = "#include <cerrno>\n#include <vector>\nint get_error();\n"
        let files = Map.ofList ["/root/util.h", content]
        let result = classifyFile "/root" "/root/util.h" (mockReader files)
        match result with
        | Some (CppHeader _) -> ()
        | _ -> Assert.Fail("Expected CppHeader for .h file with C++ stdlib includes")

    [<Fact>]
    let ``classifyFile ignores non-protocol XML`` () =
        let xml = """<?xml version="1.0"?><catalog><book title="Test"/></catalog>"""
        let files = Map.ofList ["/root/data.xml", xml]
        let result = classifyFile "/root" "/root/data.xml" (mockReader files)
        Assert.Equal(None, result)

// =========================================================================
// Diagnostic Generation
// =========================================================================

module DiagnosticTests =

    [<Fact>]
    let ``generateDiagnostics warns about no umbrella with many headers`` () =
        let files = [
            for i in 1..8 do
                CHeader ($"h{i}.h", false, false)
        ]
        let diags = generateDiagnostics files
        let hasNoUmbrella = diags |> List.exists (function
            | DiagWarning (NoUmbrellaHeader _) -> true | _ -> false)
        Assert.True(hasNoUmbrella)

    [<Fact>]
    let ``generateDiagnostics warns about internal headers`` () =
        let files = [
            CHeader ("api.h", false, false)
            CHeader ("detail/impl.h", false, true)
        ]
        let diags = generateDiagnostics files
        let hasInternal = diags |> List.exists (function
            | DiagWarning (InternalHeadersFound _) -> true | _ -> false)
        Assert.True(hasInternal)

    [<Fact>]
    let ``generateDiagnostics warns about mixed language`` () =
        let files = [
            CHeader ("pure_c.h", false, false)
            CppHeader ("cpp_api.hpp", false, false)
        ]
        let diags = generateDiagnostics files
        let hasMixed = diags |> List.exists (function
            | DiagWarning MixedLanguage -> true | _ -> false)
        Assert.True(hasMixed)

    [<Fact>]
    let ``generateDiagnostics suggests extern C`` () =
        let files = [
            CppHeader ("hip_runtime.h", true, false)
        ]
        let diags = generateDiagnostics files
        let hasExternC = diags |> List.exists (function
            | DiagSuggestion (ExternCDetected _) -> true | _ -> false)
        Assert.True(hasExternC)

    [<Fact>]
    let ``generateDiagnostics suggests protocol XML found`` () =
        let files = [
            ProtocolXml ("wayland.xml", WaylandProtocol)
            ProtocolXml ("xdg-shell.xml", WaylandProtocol)
        ]
        let diags = generateDiagnostics files
        let hasProtocol = diags |> List.exists (function
            | DiagSuggestion (ProtocolsFound 2) -> true | _ -> false)
        Assert.True(hasProtocol)

    [<Fact>]
    let ``generateDiagnostics suggests pkg-config info`` () =
        let files = [
            PkgConfig ("lib.pc", { Name = "mylib"; LibraryName = Some "mylib"; IncludePaths = ["/usr/include"]; LinkFlags = ["-lmylib"] })
        ]
        let diags = generateDiagnostics files
        let hasPkgConfig = diags |> List.exists (function
            | DiagSuggestion (PkgConfigFound ("mylib", Some "mylib", _)) -> true | _ -> false)
        Assert.True(hasPkgConfig)

    [<Fact>]
    let ``generateDiagnostics detects umbrella`` () =
        let files = [
            CHeader ("umbrella.h", true, false)
            CHeader ("a.h", false, false)
        ]
        let diags = generateDiagnostics files
        let hasUmbrella = diags |> List.exists (function
            | DiagSuggestion (UmbrellaDetected _) -> true | _ -> false)
        Assert.True(hasUmbrella)

    [<Fact>]
    let ``generateDiagnostics warns about large header count`` () =
        let files = [ for i in 1..25 do CHeader ($"h{i}.h", false, false) ]
        let diags = generateDiagnostics files
        let hasLarge = diags |> List.exists (function
            | DiagWarning (LargeHeaderCount _) -> true | _ -> false)
        Assert.True(hasLarge)

// =========================================================================
// Full Discovery (with mock IO)
// =========================================================================

module DiscoverTests =

    let mockWalker (files: string list) (_root: string) : string list = files

    let mockReader (contents: Map<string, string>) (path: string) : string option =
        Map.tryFind path contents

    [<Fact>]
    let ``discover classifies mixed directory`` () =
        let files = ["/sdk/api.h"; "/sdk/wayland.xml"; "/sdk/lib.pc"; "/sdk/CMakeLists.txt"]
        let contents = Map.ofList [
            "/sdk/api.h", "int init(void);"
            "/sdk/wayland.xml", """<?xml version="1.0"?><protocol name="wl"><interface name="wl_display" version="1"/></protocol>"""
            "/sdk/lib.pc", "Name: sdk\nLibs: -lsdk\n"
        ]
        let result = discover "/sdk" (Some "sdk") (mockWalker files) (mockReader contents)

        Assert.Equal(4, result.Files.Length)
        Assert.Equal(Some "sdk", result.SuggestedLibraryName)

        let hasCHeader = result.Files |> List.exists (function CHeader _ -> true | _ -> false)
        let hasProtocol = result.Files |> List.exists (function ProtocolXml _ -> true | _ -> false)
        let hasPkgConfig = result.Files |> List.exists (function PkgConfig _ -> true | _ -> false)
        let hasBuildFile = result.Files |> List.exists (function BuildSystemFile _ -> true | _ -> false)

        Assert.True(hasCHeader)
        Assert.True(hasProtocol)
        Assert.True(hasPkgConfig)
        Assert.True(hasBuildFile)

    [<Fact>]
    let ``discover reports error for empty directory`` () =
        let result = discover "/empty" None (fun _ -> []) (fun _ -> None)
        let hasError = result.Diagnostics |> List.exists (function
            | DiagError (NoParseableFiles _) -> true | _ -> false)
        Assert.True(hasError)

    [<Fact>]
    let ``discover suggests library name from pkg-config`` () =
        let files = ["/root/lib.pc"]
        let contents = Map.ofList ["/root/lib.pc", "Name: wayland\nLibs: -lwayland-client\n"]
        let result = discover "/root" None (mockWalker files) (mockReader contents)
        Assert.Equal(Some "wayland-client", result.SuggestedLibraryName)

    [<Fact>]
    let ``discover suggests include paths from pkg-config`` () =
        let files = ["/root/lib.pc"]
        let contents = Map.ofList ["/root/lib.pc", "Name: test\nCflags: -I/opt/include -I/opt/include/sub\n"]
        let result = discover "/root" None (mockWalker files) (mockReader contents)
        Assert.Equal<string list>(["/opt/include"; "/opt/include/sub"], result.SuggestedIncludePaths)

    [<Fact>]
    let ``discover uses directory name as fallback library name`` () =
        let files = ["/mylib/api.h"]
        let contents = Map.ofList ["/mylib/api.h", "void init();"]
        let result = discover "/mylib" None (mockWalker files) (mockReader contents)
        Assert.Equal(Some "mylib", result.SuggestedLibraryName)

    [<Fact>]
    let ``discover prefers explicit library hint`` () =
        let files = ["/root/lib.pc"]
        let contents = Map.ofList ["/root/lib.pc", "Name: other\nLibs: -lother\n"]
        let result = discover "/root" (Some "mylib") (mockWalker files) (mockReader contents)
        Assert.Equal(Some "mylib", result.SuggestedLibraryName)

// =========================================================================
// Discovery → PilotProject Conversion
// =========================================================================

module ToPilotProjectTests =

    [<Fact>]
    let ``toPilotProject uses non-internal C headers`` () =
        let result = {
            RootDirectory = "/sdk"
            Files = [
                CHeader ("api.h", false, false)
                CHeader ("internal/core.h", false, true)
            ]
            Diagnostics = []
            SuggestedLibraryName = Some "sdk"
            SuggestedIncludePaths = []
        }
        let project = toPilotProject "sdk" "fidelity" "./out" result
        Assert.Equal(1, project.Library.Headers.Length)
        Assert.Contains("/sdk/api.h", project.Library.Headers.[0])

    [<Fact>]
    let ``toPilotProject prefers umbrella headers`` () =
        let result = {
            RootDirectory = "/sdk"
            Files = [
                CHeader ("sdk.h", true, false)
                CHeader ("a.h", false, false)
                CHeader ("b.h", false, false)
            ]
            Diagnostics = []
            SuggestedLibraryName = Some "sdk"
            SuggestedIncludePaths = []
        }
        let project = toPilotProject "sdk" "fidelity" "./out" result
        Assert.Equal(1, project.Library.Headers.Length)
        Assert.Contains("sdk.h", project.Library.Headers.[0])

    [<Fact>]
    let ``toPilotProject includes Wayland protocol XMLs`` () =
        let result = {
            RootDirectory = "/usr/share"
            Files = [
                ProtocolXml ("wayland/wayland.xml", WaylandProtocol)
                ProtocolXml ("wayland-protocols/xdg-shell.xml", WaylandProtocol)
                ProtocolXml ("dbus/introspect.xml", DBusIntrospection)
            ]
            Diagnostics = []
            SuggestedLibraryName = Some "wayland"
            SuggestedIncludePaths = []
        }
        let project = toPilotProject "wayland-client" "fidelity" "./out" result
        // Only Wayland protocols, not D-Bus
        Assert.Equal(2, project.Library.XmlProtocols.Length)

    [<Fact>]
    let ``toPilotProject includes bindable C++ headers`` () =
        let result = {
            RootDirectory = "/opt/rocm"
            Files = [
                CppHeader ("include/hip/hip_runtime_api.h", true, false)
                CppHeader ("include/hip/hip_internal.hpp", false, true)
            ]
            Diagnostics = []
            SuggestedLibraryName = Some "amdhip64"
            SuggestedIncludePaths = ["/opt/rocm/include"]
        }
        let project = toPilotProject "amdhip64" "fidelity" "./out" result
        // Only the extern "C" non-internal header
        Assert.Equal(1, project.Library.Headers.Length)
        Assert.Contains("hip_runtime_api.h", project.Library.Headers.[0])
        Assert.Equal<string list>(["/opt/rocm/include"], project.Library.IncludePaths)

    [<Fact>]
    let ``toPilotProject sets library name and output`` () =
        let result = {
            RootDirectory = "/sdk"
            Files = [CHeader ("api.h", false, false)]
            Diagnostics = []
            SuggestedLibraryName = Some "sdk"
            SuggestedIncludePaths = []
        }
        let project = toPilotProject "mylib" "fidelity-wrappers" "./bindings" result
        Assert.Equal("mylib", project.Library.Name)
        Assert.Equal("fidelity-wrappers", project.Output.Mode)
        Assert.Equal("./bindings", project.Output.Directory)

    [<Fact>]
    let ``toPilotProject creates default Core namespace`` () =
        let result = {
            RootDirectory = "/sdk"
            Files = [CHeader ("api.h", false, false)]
            Diagnostics = []
            SuggestedLibraryName = Some "sdk"
            SuggestedIncludePaths = []
        }
        let project = toPilotProject "sdk" "fidelity" "./out" result
        Assert.Equal(1, project.Namespaces.Length)
        Assert.Equal("Fidelity.sdk.Core", project.Namespaces.[0].Name)

// =========================================================================
// discoverFromDirectory (IO boundary)
// =========================================================================

module DiscoverFromDirectoryTests =

    [<Fact>]
    let ``discoverFromDirectory returns error for nonexistent directory`` () =
        let result = discoverFromDirectory "/nonexistent/path/that/does/not/exist" None
        let hasError = result.Diagnostics |> List.exists (function
            | DiagError (DirectoryNotFound _) -> true | _ -> false)
        Assert.True(hasError)
        Assert.Empty(result.Files)
