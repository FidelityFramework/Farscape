#!/usr/bin/env python3
"""Generate bindings, compile native witnesses, and validate their C entry ABI."""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
SIBLINGS = REPO.parent


def run(command, work, label, env=None, timeout=180):
    log = work / f"{label}.log"
    print(f"{label}: {log}", flush=True)
    with log.open("w") as output:
        result = subprocess.run(command, cwd=work, env=env, stdout=output,
                                stderr=subprocess.STDOUT, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"{label} exited {result.returncode}:\n{log.read_text()}")
    return log


def generate(generator, work, output, namespace):
    template = (HERE / "probe.pilot.toml").read_text()
    values = {"HEADER": str(work / "probe.h"), "INCLUDE": str(work),
              "OUTPUT": f"generated/{output}", "NAMESPACE": namespace}
    for name, value in values.items():
        template = template.replace(f'"@{name}@"', json.dumps(value))
    pilot = work / f"{output}.pilot.toml"
    pilot.write_text(template)
    run(["dotnet", str(generator), "project", "--project", str(pilot)],
        work, f"generate-{output}")
    return [f"generated/{output}/Types.clef", f"generated/{output}/Boundary/Boundary.clef"]


def check_entries(mlir, modules):
    text = mlir.read_text()
    for module in modules:
        scalar = re.search(r"func.func @" + re.escape(module) +
                           r"\.scalarEntry\(([^)]*)\) -> i32", text)
        if scalar is None or re.findall(r": (\w+)", scalar.group(1)) != ["index", "i32", "index"]:
            raise RuntimeError(f"Wrong scalar callback ABI for {module}; see {mlir}")
        thunks = re.finditer(r"func.func @(__clef_callback_\d+)\(([^)]*)\) -> \(\) \{(.*?)^[ \t]*\}",
                            text, re.MULTILINE | re.DOTALL)
        for thunk in thunks:
            if f"func.call @{module}.voidEntry(" in thunk.group(3):
                address = f"func.constant @{thunk.group(1)} : (index, index) -> ()"
                if re.findall(r": (\w+)", thunk.group(2)) == ["index", "index"] and address in text:
                    break
        else:
            raise RuntimeError(f"Missing declared native void address for {module}; see {mlir}")


def compile_run(args, work, label, sources, modules):
    project = work / f"{label}.fidproj"
    project.write_text(f'''[package]
name = "{label}"
version = "0.1.0"
[compilation]
target = "cpu"
[dependencies]
platform = {{ path = {json.dumps(str(args.platform))} }}
metadata = {{ path = {json.dumps(str(args.metadata))} }}
[build]
sources = {json.dumps(sources)}
output_kind = "console"
[link]
libraries = ["landingprobe"]
''')
    binary = work / label
    log = run(["dotnet", str(args.composer), "compile", str(project),
               "--link-library-path", str(work), "-k", "-o", str(binary)],
              work, f"build-{label}")
    if re.search(r"\b(?:warning|error)\b", log.read_text(), re.IGNORECASE):
        raise RuntimeError(f"Compiler reported a warning or error; inspect {log}")
    mlir = work / f"{label}.mlir"
    shutil.copyfile(work / "targets/intermediates/10_output.mlir", mlir)
    check_entries(mlir, modules)
    env = os.environ.copy()
    env["LD_LIBRARY_PATH"] = str(work) + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    run([str(binary)], work, f"run-{label}", env, timeout=15)
    print(f"PASS: {label}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--composer", type=Path, default=Path(os.environ.get(
        "COMPOSER_DLL", SIBLINGS / "Composer/src/bin/Debug/net10.0/Composer.dll")))
    parser.add_argument("--platform", type=Path, default=Path(os.environ.get(
        "FIDELITY_PLATFORM_PROJECT", SIBLINGS / "Fidelity.Platform/Environments/Linux/x86_64/Fidelity.Platform.CompilerSurface.fidproj")))
    parser.add_argument("--metadata", type=Path, default=Path(os.environ.get(
        "BAREWIRE_METADATA_PROJECT", SIBLINGS / "BAREWire/src/BAREWire.BindingMetadata.fidproj")))
    parser.add_argument("--generator", type=Path, help="Use an existing Farscape CLI DLL; default builds the current source")
    parser.add_argument("--cc", default=os.environ.get("CC", "cc"), help="C compiler executable")
    args = parser.parse_args()
    for name in ["composer", "platform", "metadata"]:
        path = getattr(args, name).expanduser().resolve()
        if not path.is_file():
            parser.error(f"--{name} does not exist: {path}")
        setattr(args, name, path)
    work = Path(tempfile.mkdtemp(prefix="farscape-native-callbacks-"))
    print(f"Evidence directory: {work}", flush=True)
    generator = args.generator
    if generator is None:
        run(["dotnet", "build", str(REPO / "src/Farscape.Cli/Farscape.Cli.fsproj"),
             "-c", "Debug", "--nologo"], work, "build-generator")
        generator = REPO / "src/Farscape.Cli/bin/Debug/net10.0/Farscape.Cli.dll"
    generator = generator.expanduser().resolve()
    for name in ["probe.h", "probe.c", "Boundary.clef", "Main.clef", "DuplicateMain.clef"]:
        shutil.copyfile(HERE / name, work / name)
    run([args.cc, "-shared", "-fPIC", "-Wall", "-Wextra", "-Werror",
         str(work / "probe.c"), "-o", str(work / "liblandingprobe.so")], work, "build-c")
    first = generate(generator, work, "Bindings", "Landing.Boundary")
    compile_run(args, work, "single-entry", first + ["Boundary.clef", "Main.clef"],
                ["Landing.Acceptance.Boundary"])
    second = generate(generator, work, "DuplicateBindings", "Landing.Other.Boundary")
    other = (work / "Boundary.clef").read_text().replace(
        "Landing.Acceptance.Boundary", "Landing.Acceptance.OtherBoundary").replace(
        "Landing.Boundary", "Landing.Other.Boundary").replace(
        "Callback_Landing_002E_Boundary", "Callback_Landing_002E_Other_002E_Boundary")
    (work / "OtherBoundary.clef").write_text(other)
    compile_run(args, work, "duplicate-entry", first + second +
                ["Boundary.clef", "OtherBoundary.clef", "DuplicateMain.clef"],
                ["Landing.Acceptance.Boundary", "Landing.Acceptance.OtherBoundary"])
    print(f"Native callback gates passed; evidence retained in {work}", flush=True)


if __name__ == "__main__":
    main()
