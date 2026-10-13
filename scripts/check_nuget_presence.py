#!/usr/bin/env python3
"""Check an actual nupkg before NuGet.org publication, using the standard library.

Exit 0: this ID/version is already public. Exit 3: it is missing. Exit 1: the
package or lookup is invalid. Usage errors return argparse's distinct exit 2.
The flat-container index includes unlisted versions, which are also immutable:
https://learn.microsoft.com/en-us/nuget/api/package-base-address-resource
"""

import argparse
from http.client import HTTPException
import json
from pathlib import Path
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET
import zipfile

MISSING = 3
TIMEOUT_SECONDS = 15
MAX_NUSPEC_BYTES = 64_000
MAX_INDEX_BYTES = 1_000_000
BASE_ADDRESS = "https://api.nuget.org/v3-flatcontainer/"
PACKAGE_ID = re.compile(r"[A-Za-z0-9_.-]{1,100}\Z")
VERSION = re.compile(
    r"([0-9]+(?:\.[0-9]+){1,3})"
    r"(-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z"
)


class PresenceError(ValueError):
    """A fixed diagnostic that never echoes response bodies or rejected metadata."""


class NoDtdTreeBuilder(ET.TreeBuilder):
    def doctype(self, name, public_id, system_id):
        raise PresenceError("nuspec DTDs are forbidden")


def normalized_version(value):
    """NuGet identity excludes build metadata and redundant numeric zeros."""
    if not isinstance(value, str) or len(value) > 64:
        raise PresenceError("invalid package version")
    match = VERSION.fullmatch(value)
    if match is None:
        raise PresenceError("invalid package version")
    numbers = [str(int(part)) for part in match.group(1).split(".")]
    numbers += ["0"] * (3 - len(numbers))
    if len(numbers) == 4 and numbers[-1] == "0":
        numbers.pop()
    return ".".join(numbers) + (match.group(2) or "").lower()


def package_identity(path):
    """Read bounded identity metadata without trusting the package filename."""
    try:
        with zipfile.ZipFile(path) as archive:
            manifests = [entry for entry in archive.infolist()
                         if entry.filename.lower().endswith(".nuspec")]
            if (len(manifests) != 1 or "/" in manifests[0].filename
                    or "\\" in manifests[0].filename):
                raise PresenceError("package must contain one root nuspec")
            manifest = manifests[0]
            if manifest.file_size > MAX_NUSPEC_BYTES:
                raise PresenceError("nuspec exceeds the size limit")
            with archive.open(manifest) as stream:
                data = stream.read(MAX_NUSPEC_BYTES + 1)
            if len(data) > MAX_NUSPEC_BYTES:
                raise PresenceError("nuspec exceeds the size limit")
        tree = ET.fromstring(data, parser=ET.XMLParser(target=NoDtdTreeBuilder()))
    except (OSError, zipfile.BadZipFile, RuntimeError, ET.ParseError) as error:
        raise PresenceError("cannot read package nuspec") from error
    if tree.tag.split("}")[-1] != "package":
        raise PresenceError("invalid nuspec package element")
    metadata = tree.findall("{*}metadata")
    if len(metadata) != 1:
        raise PresenceError("nuspec must contain one metadata element")

    def text(name):
        fields = metadata[0].findall("{*}" + name)
        if len(fields) != 1 or list(fields[0]) or not fields[0].text:
            raise PresenceError("nuspec must contain one package " + name)
        return fields[0].text.strip()

    package_id, version = text("id"), text("version")
    if PACKAGE_ID.fullmatch(package_id) is None:
        raise PresenceError("invalid package ID")
    return package_id, normalized_version(version)


def is_public(package_id, version):
    """Only a 404 or a valid index missing this version means it may be pushed."""
    url = BASE_ADDRESS + package_id.lower() + "/index.json"
    request = Request(url, headers={"Accept": "application/json"})
    try:
        with urlopen(request, timeout=TIMEOUT_SECONDS) as response:
            if response.status != 200 or response.geturl() != url:
                raise PresenceError("unexpected NuGet response")
            data = response.read(MAX_INDEX_BYTES + 1)
    except HTTPError as error:
        with error:
            if error.code == 404 and error.url == url:
                return False
            raise PresenceError("NuGet lookup returned an HTTP error") from error
    except (URLError, TimeoutError, OSError, HTTPException) as error:
        raise PresenceError("NuGet lookup failed") from error
    if len(data) > MAX_INDEX_BYTES:
        raise PresenceError("NuGet index exceeds the size limit")
    try:
        index = json.loads(data)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise PresenceError("invalid NuGet JSON") from error
    if not isinstance(index, dict) or not isinstance(index.get("versions"), list):
        raise PresenceError("invalid NuGet versions index")
    versions = index["versions"]
    for entry in versions:
        if normalized_version(entry) != entry:
            raise PresenceError("NuGet index contains a non-normalized version")
    return version in versions


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path, help="actual .nupkg to publish")
    args = parser.parse_args(argv)
    try:
        package_id, version = package_identity(args.package)
        exists = is_public(package_id, version)
    except PresenceError as error:
        print("NuGet presence check failed: " + str(error), file=sys.stderr)
        return 1
    state = "already public" if exists else "missing"
    print(f"NuGet version {state}: {package_id} {version}")
    return 0 if exists else MISSING


if __name__ == "__main__":
    sys.exit(main())
