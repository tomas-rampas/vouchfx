#!/usr/bin/env python3
"""Verify the exact VSIX identity and optional build digest before publication."""

import argparse
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

METADATA_LIMIT = 1_000_000
VSIX_NAMESPACE = "{http://schemas.microsoft.com/developer/vsx-schema/2011}"


def check_package(path, version, sha256=None):
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if sha256 is not None and digest != sha256:
        raise ValueError("VSIX digest differs from the verified build artifact")
    with zipfile.ZipFile(path) as package:
        names = package.namelist()
        if len({name.casefold() for name in names}) != len(names):
            raise ValueError("VSIX contains duplicate package members")
        if package.testzip() is not None:
            raise ValueError("VSIX contains a corrupt package member")

        def metadata(name):
            if package.getinfo(name).file_size > METADATA_LIMIT:
                raise ValueError("VSIX metadata exceeds the size limit")
            return package.read(name)

        manifest = json.loads(metadata("extension/package.json"))
        identity = ET.fromstring(metadata("extension.vsixmanifest")).find(
            f"{VSIX_NAMESPACE}Metadata/{VSIX_NAMESPACE}Identity")
        if (manifest.get("name"), manifest.get("publisher"), manifest.get("version")) != (
                "vouchfx", "vouchfx", version):
            raise ValueError("VSIX package manifest does not match the release identity")
        if identity is None or (
                identity.get("Id"), identity.get("Publisher"), identity.get("Version")) != (
                "vouchfx", "vouchfx", version):
            raise ValueError("VSIX XML manifest does not match the release identity")
    return {"version": version, "publisher": "vouchfx", "name": "vouchfx", "sha256": digest}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--sha256", help="SHA-256 recorded by the build-vsix job")
    args = parser.parse_args()
    try:
        result = check_package(args.package, args.version, args.sha256)
    except (OSError, ValueError, KeyError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"VSIX publication check failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
