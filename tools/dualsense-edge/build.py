#!/usr/bin/env python3
"""Build Apollo's optional Windows Edge component, without installing drivers."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parents[2]
DEPENDENCY = ROOT / "third-party" / "hidmaestro"
COMPONENT = ROOT / "src" / "platform" / "windows" / "dualsense_edge"


def file_hash(path, algorithm="sha256"):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def fetch(asset, destination, algorithm):
    if not destination.exists():
        print(f"Downloading {asset['url']}", flush=True)
        request = urllib.request.Request(asset["url"], headers={"User-Agent": "Apollo-build"})
        with tempfile.NamedTemporaryFile(dir=destination.parent, delete=False) as temporary:
            partial = Path(temporary.name)
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    shutil.copyfileobj(response, temporary)
            except BaseException:
                temporary.close()
                partial.unlink()
                raise
        try:
            if file_hash(partial, algorithm) != asset[algorithm]:
                raise RuntimeError(f"Checksum mismatch downloading {asset['url']}")
            partial.replace(destination)
        finally:
            partial.unlink(missing_ok=True)
    if file_hash(destination, algorithm) != asset[algorithm]:
        raise RuntimeError(f"Checksum mismatch in cache: {destination}")


def prepare_source(work, asset):
    archive = work / "hidmaestro-source.tar.gz"
    fetch(asset, archive, "sha256")
    patches = sorted(DEPENDENCY.glob("*.patch"))
    fingerprint = hashlib.sha256(asset["sha256"].encode())
    for patch in patches:
        fingerprint.update(patch.read_bytes())
    source = work / ("hidmaestro-" + fingerprint.hexdigest()[:16])
    if source.is_dir():
        return source

    with tempfile.TemporaryDirectory(prefix="hidmaestro-", dir=work) as staging:
        staging = Path(staging)
        with tarfile.open(archive, "r:gz") as package:
            for member in package:
                path = PurePosixPath(member.name)
                if path.is_absolute() or ".." in path.parts:
                    raise RuntimeError("Invalid SDK archive path")
                relative = PurePosixPath(*path.parts[1:])
                # Only managed sources and their licenses are required. The
                # component uses an externally installed USB/IP driver; it
                # contains no native SDK drivers, installers or signing tools.
                if not member.isfile() or not (
                    relative == PurePosixPath("LICENSE") or
                    relative.parts[:2] == ("sdk", "HIDMaestro.Core") and
                    (relative.suffix == ".cs" or relative.name == "THIRD-PARTY-NOTICES.txt")
                ):
                    continue
                destination = staging.joinpath(*relative.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                with package.extractfile(member) as input_file, destination.open("wb") as output:
                    shutil.copyfileobj(input_file, output)
        # Keep git apply independent of any repository above the build folder.
        environment = dict(os.environ, GIT_CEILING_DIRECTORIES=str(work))
        for patch in patches:
            subprocess.run(["git", "apply", "--no-index", "--whitespace=error", str(patch)],
                           cwd=staging, env=environment, check=True)
        staging.rename(source)
    return source


def build(args, work, output, dependencies):
    source = prepare_source(work, dependencies["hidmaestro"])
    properties = [f"-p:HidMaestroSource={source}", "--artifacts-path", str(work / "dotnet")]
    subprocess.run([args.dotnet, "publish", str(COMPONENT / "Apollo.ControllerHost.csproj"),
                    "-c", "Release", "-r", "win-x64", "-o", str(output), *properties], check=True)
    if args.test:
        tests = ROOT / "tests" / "dualsense-edge" / "ControllerTests.csproj"
        subprocess.run([args.dotnet, "run", "--project", str(tests),
                        "-c", "Release", *properties], check=True)

    runtime = dependencies["runtime"]
    archive = work / f"dotnet-runtime-{runtime['version']}-win-x64.zip"
    fetch(runtime, archive, "sha512")
    with zipfile.ZipFile(archive) as package:
        for member in package.infolist():
            path = PurePosixPath(member.filename)
            if path.is_absolute() or ".." in path.parts or "\\" in member.filename or ":" in member.filename:
                raise RuntimeError("Invalid runtime archive path")
        package.extractall(output / "runtime")
    if not (output / "runtime" / "host" / "fxr" / runtime["version"] / "hostfxr.dll").is_file():
        raise RuntimeError("The private .NET runtime has an unexpected layout")
    shutil.copyfile(source / "LICENSE", output / "HIDMaestro-LICENSE.txt")
    shutil.copyfile(source / "sdk" / "HIDMaestro.Core" / "THIRD-PARTY-NOTICES.txt",
                    output / "HIDMaestro-NOTICES.txt")
    # Content identity is useful when checking a packaged installation.
    (output / "component-build.json").write_text(json.dumps({
        "component": "Apollo.DualSenseEdge", "abi_version": 1, "dependencies": dependencies,
        "patches": {path.name: file_hash(path) for path in sorted(DEPENDENCY.glob("*.patch"))},
    }, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--test", action="store_true",
                        help="Also run the controller report tests; no devices are created")
    args = parser.parse_args()
    work, output = args.work.resolve(), args.output.resolve()
    if output == work or output in work.parents or work in output.parents:
        raise RuntimeError("Work and output must be separate directories, not nested inside one another")
    if output.is_dir() and any(output.iterdir()):
        marker = output / "component-build.json"
        if not marker.is_file() or json.loads(marker.read_text()).get("component") != "Apollo.DualSenseEdge":
            raise RuntimeError(f"Refusing to replace an unrecognized output directory: {output}")
    work.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    dependencies = json.loads((DEPENDENCY / "dependencies.json").read_text())
    # Publish a complete fresh directory so removed assemblies or old runtime
    # versions cannot leak into the next package. A failed build keeps the last
    # complete output; only a directory marked by this builder is replaced.
    with tempfile.TemporaryDirectory(prefix="edge-package-", dir=output.parent) as temporary:
        temporary = Path(temporary)
        candidate = temporary / "controller"
        build(args, work, candidate, dependencies)
        previous = temporary / "previous"
        if output.exists():
            output.rename(previous)
        try:
            candidate.rename(output)
        except BaseException:
            if previous.exists():
                previous.rename(output)
            raise
    print(f"Built DualSense Edge component: {output}", flush=True)


if __name__ == "__main__":
    main()
