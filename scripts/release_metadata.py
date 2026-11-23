#!/usr/bin/env python3
"""Read release metadata from CHANGELOG.md and Directory.Build.props.

`notes` prints the CHANGELOG.md section for a version (its body, without the
heading). The release run puts it in front of GitHub's generated notes. A
section longer than MAX_NOTES_CHARS is refused. GitHub is believed to cap a
Release body at 125,000 characters; that limit is not in the REST API
documentation, and the generated notes need room too.

`check` confirms that the release commit was prepared for this version: the
newest versioned CHANGELOG.md heading and the `VouchfxEngineVersion` fallback in
Directory.Build.props must both equal it (RELEASING.md, step 1).

Exit 0: success. Exit 1: the files do not match the version, or cannot be read.
Usage errors return argparse's exit 2.
"""

import argparse
from pathlib import Path
import re
import sys

HEADING = re.compile(r"## \[([^\]]+)\]")
FALLBACK = re.compile(
    r"<VouchfxEngineVersion\s+Condition=\"'\$\(VouchfxEngineVersion\)'\s*==\s*''\">"
    r"([^<]*)</VouchfxEngineVersion>")
UNRELEASED = "Unreleased"
MAX_NOTES_CHARS = 100_000


class MetadataError(ValueError):
    """A fixed diagnostic for a file that does not match the version."""


def read(path):
    try:
        return Path(path).read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise MetadataError(f"cannot read {path}") from error


def headings(text):
    """Yields (index, name) for each `## [name]` line, in file order."""
    lines = text.splitlines()
    for index, line in enumerate(lines):
        match = HEADING.match(line)
        if match:
            yield index, match.group(1)


def newest_version(text):
    for _, name in headings(text):
        if name != UNRELEASED:
            return name
    raise MetadataError("CHANGELOG.md has no versioned heading")


def section(text, version):
    """The body under `## [version]`, up to the next `## [` heading."""
    lines = text.splitlines()
    found = [index for index, name in headings(text) if name == version]
    if len(found) != 1:
        raise MetadataError(f"CHANGELOG.md must have exactly one heading for {version}, found {len(found)}")
    start = found[0] + 1
    end = next((index for index, _ in headings(text) if index > found[0]), len(lines))
    body = "\n".join(lines[start:end]).strip("\n")
    if not body.strip():
        raise MetadataError(f"the CHANGELOG.md section for {version} is empty")
    return body + "\n"


def props_fallback(text):
    found = FALLBACK.findall(text)
    if len(found) != 1:
        raise MetadataError("Directory.Build.props must have exactly one VouchfxEngineVersion fallback")
    return found[0].strip()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    notes = commands.add_parser("notes", help="print the CHANGELOG.md section for a version")
    notes.add_argument("--changelog", required=True, type=Path)
    notes.add_argument("--version", required=True)
    check = commands.add_parser("check", help="confirm the commit was prepared for a version")
    check.add_argument("--changelog", required=True, type=Path)
    check.add_argument("--props", required=True, type=Path)
    check.add_argument("--version", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "notes":
            notes = section(read(args.changelog), args.version)
            if len(notes) > MAX_NOTES_CHARS:
                raise MetadataError(
                    f"the CHANGELOG.md section for {args.version} has {len(notes)} characters; the limit is "
                    f"{MAX_NOTES_CHARS}, so that GitHub's generated notes still fit in the Release body "
                    "(believed to be capped at 125000 characters)")
            sys.stdout.write(notes)
            return 0
        newest = newest_version(read(args.changelog))
        fallback = props_fallback(read(args.props))
        if newest != args.version:
            raise MetadataError(f"the newest CHANGELOG.md heading is {newest}, not {args.version}")
        if fallback != args.version:
            raise MetadataError(f"the VouchfxEngineVersion fallback is {fallback}, not {args.version}")
        print(f"CHANGELOG.md and Directory.Build.props are prepared for {args.version}")
        return 0
    except MetadataError as error:
        print("Release metadata check failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
