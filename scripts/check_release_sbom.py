#!/usr/bin/env python3
"""Reject a release SBOM with stale root identity or dangling dependency references."""

import argparse
import json
from pathlib import Path
import re


def validate(data, version):
    if not isinstance(version, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?", version):
        raise ValueError("Expected a release version such as 1.2.3 or 1.2.3-rc.1")
    root = data.get("metadata", {}).get("component", {})
    root_ref = f"vouchfx@{version}"
    if root.get("name") != "vouchfx" or root.get("version") != version or root.get("bom-ref") != root_ref:
        raise ValueError(f"SBOM root must identify {root_ref}")
    refs = {root_ref}

    def components(items):
        for component in items:
            ref = component.get("bom-ref")
            if not isinstance(ref, str) or not ref or ref in refs:
                raise ValueError("SBOM components require unique nonempty bom-ref values")
            refs.add(ref)
            components(component.get("components", []))

    components(data.get("components", []))
    dependencies = data.get("dependencies", [])
    if sum(item.get("ref") == root_ref for item in dependencies) != 1:
        raise ValueError("SBOM must contain one dependency entry for the release root")
    for item in dependencies:
        for ref in [item.get("ref"), *item.get("dependsOn", []), *item.get("provides", [])]:
            if ref not in refs:
                raise ValueError(f"SBOM dependency reference does not identify a component: {ref}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sbom", type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    try:
        validate(json.loads(args.sbom.read_text(encoding="utf-8")), args.version)
    except (ValueError, KeyError, TypeError, AttributeError) as error:
        parser.exit(1, f"Invalid release SBOM: {error}\n")


if __name__ == "__main__":
    main()
