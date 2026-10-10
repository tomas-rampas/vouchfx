#!/usr/bin/env python3
"""Validate installed DCP payloads before publishing; Python standard library only."""

import argparse
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

SUPPORTED_RIDS = {"win-x64", "linux-x64", "osx-x64", "osx-arm64"}
# NuGet.org documents a 250 MB maximum. Use decimal MB conservatively and explicitly.
# https://learn.microsoft.com/en-us/nuget/nuget-org/nuget-org-faq
MAX_PACKAGE_BYTES = 250_000_000


class InvalidDistribution(ValueError):
    """The artifact cannot be distributed as a complete supported installation."""


def sha256(stream):
    hasher = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        hasher.update(block)
    return hasher.hexdigest()


def validate_bundle(names, read, open_file, prefix, version, expected_rids, nuget_cache=None):
    root = f"{prefix}dcp/{version}/"
    manifest = prefix + "dcp/supported-rids.txt"
    if manifest not in names:
        raise InvalidDistribution(f"Missing DCP distribution manifest: {manifest}")
    declared = set(read(manifest).decode("utf-8").splitlines())
    if declared != SUPPORTED_RIDS:
        raise InvalidDistribution(f"Unexpected supported RID manifest: {sorted(declared)}")
    actual_rids = {name[len(root):].split("/")[0] for name in names
                   if name.startswith(root) and "/tools/" in name}
    if actual_rids != expected_rids:
        raise InvalidDistribution(f"Expected DCP RIDs {sorted(expected_rids)}, found {sorted(actual_rids)}")
    versions = {name[len(prefix + "dcp/"):].split("/")[0] for name in names
                if name.startswith(prefix + "dcp/") and name != manifest}
    if versions != {version}:
        raise InvalidDistribution(f"DCP version mismatch: expected {version}, found {sorted(versions)}")

    for rid in sorted(expected_rids):
        tools = root + rid + "/tools/"
        executable = "dcp.exe" if rid == "win-x64" else "dcp"
        required = [executable, "dcptun_c", "LICENSE", "NOTICE",
                    "_manifest/spdx_2.2/manifest.spdx.json"]
        for filename in required:
            if tools + filename not in names:
                raise InvalidDistribution(f"Incomplete {rid} DCP: missing {tools + filename}")
            with open_file(tools + filename) as stream:
                if not stream.read(1):
                    raise InvalidDistribution(f"Empty DCP payload: {tools + filename}")
        sbom = json.loads(read(tools + required[-1]))
        hashes = {}
        for entry in sbom["files"]:
            filename = entry["fileName"].removeprefix("./")
            for checksum in entry["checksums"]:
                if checksum["algorithm"] == "SHA256":
                    hashes[filename] = checksum["checksumValue"].lower()
        # Upstream macOS/Windows manifests omit dcptun_c. The osx-arm64 13.4.2
        # manifest also has a wrong NOTICE hash, even in the original NuGet package.
        # Native hashes remain strict; --nuget-cache verifies EVERY original file,
        # including licences, notices and upstream manifests, byte for byte.
        if executable not in hashes:
            raise InvalidDistribution(f"Missing SHA256 in {rid} upstream DCP manifest: {executable}")
        for filename in [executable, "dcptun_c"]:
            if filename not in hashes:
                continue
            with open_file(tools + filename) as stream:
                digest = sha256(stream)
            if digest != hashes[filename]:
                raise InvalidDistribution(f"DCP SHA256 mismatch for {rid}/{filename}")
        if nuget_cache:
            source = nuget_cache / f"aspire.hosting.orchestration.{rid}" / version / "tools"
            source_files = {file.relative_to(source).as_posix() for file in source.rglob("*") if file.is_file()}
            shipped_files = {name[len(tools):] for name in names if name.startswith(tools)}
            if not source_files or source_files != shipped_files:
                raise InvalidDistribution(f"DCP {rid} tools tree differs from restored NuGet package {source}")
            for filename in source_files:
                with open_file(tools + filename) as shipped, (source / filename).open("rb") as original:
                    if sha256(shipped) != sha256(original):
                        raise InvalidDistribution(f"DCP payload differs from restored NuGet package: {rid}/{filename}")
        with open_file(tools + executable) as stream:
            header = stream.read(256)
        if rid == "win-x64":
            pe_offset = int.from_bytes(header[60:64], "little")
            with open_file(tools + executable) as stream:
                stream.seek(pe_offset)
                pe_header = stream.read(6)
            valid = header[:2] == b"MZ" and pe_header == b"PE\0\0\x64\x86"
        elif rid == "linux-x64":
            valid = header[:6] == b"\x7fELF\x02\x01" and header[18:20] == b"\x3e\0"
        else:
            cpu = b"\x07\0\0\x01" if rid == "osx-x64" else b"\x0c\0\0\x01"
            valid = header[:8] == b"\xcf\xfa\xed\xfe" + cpu
        if not valid:
            raise InvalidDistribution(f"DCP executable does not match declared platform {rid}")


def validate_package(path, version, nuget_cache=None):
    size = path.stat().st_size
    if size > MAX_PACKAGE_BYTES:
        raise InvalidDistribution(
            f"Tool package is {size} bytes; ceiling is {MAX_PACKAGE_BYTES} bytes (decimal 250 MB). "
            "Use a reviewed distribution strategy; do not remove native helpers, licences or notices.")
    with zipfile.ZipFile(path) as package:
        names = {name for name in package.namelist() if not name.endswith("/")}
        validate_bundle(names, package.read, package.open, "tools/net8.0/any/", version, SUPPORTED_RIDS, nuget_cache)
    return f"Tool package: {size}/{MAX_PACKAGE_BYTES} bytes, four complete DCP {version} bundles"


def validate_publish(path, version, rid, nuget_cache=None):
    names = {file.relative_to(path).as_posix() for file in path.rglob("*") if file.is_file()}
    validate_bundle(names, lambda name: (path / name).read_bytes(),
                    lambda name: (path / name).open("rb"), "", version, {rid}, nuget_cache)
    if rid != "win-x64" and sys.platform != "win32":
        tools = path / "dcp" / version / rid / "tools"
        for file in [path / "vouchfx", tools / "dcp", tools / "dcptun_c"]:
            if not file.stat().st_mode & 0o111:
                raise InvalidDistribution(f"Missing Unix executable permissions: {file}")
    return f"Publish directory: complete {rid} DCP {version} bundle"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--package", type=Path)
    group.add_argument("--publish", type=Path)
    parser.add_argument("--rid", choices=sorted(SUPPORTED_RIDS))
    parser.add_argument("--nuget-cache", type=Path, help="Verify complete payload against exact restored NuGet tools trees")
    args = parser.parse_args()
    props = ET.parse(Path(__file__).resolve().parents[1] / "Directory.Packages.props").getroot()
    version = props.findtext(".//VouchfxDcpVersion")
    hosting = props.find(".//PackageVersion[@Include='Aspire.Hosting']").get("Version")
    cli = ET.parse(Path(__file__).resolve().parents[1] / "src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj").getroot()
    host_sdk = cli.find("Sdk[@Name='Aspire.AppHost.Sdk']").get("Version")
    if not version or version != hosting or version != host_sdk:
        parser.error(f"DCP {version} must match pinned Aspire.Hosting {hosting} and AppHost SDK {host_sdk}")
    try:
        if args.package:
            result = validate_package(args.package, version, args.nuget_cache)
        elif args.rid:
            result = validate_publish(args.publish, version, args.rid, args.nuget_cache)
        else:
            parser.error("--publish requires --rid")
        print(result)
        return 0
    except (InvalidDistribution, OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        print(f"Distribution rejected: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
