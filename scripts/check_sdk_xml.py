#!/usr/bin/env python3
"""Check actual NuGet XML publication surfaces (issue #508); standard library only.

The release closure must contain five library documentation files and only the
CLI tool-settings XML. Local audits can additionally require Planning, which is
packable but is not part of the release workflow. Never rewrite generated XML:
fix source documentation while preserving the compiler's documentation gates.
"""

import argparse
import lzma
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
from xml.parsers import expat
import zipfile
import zlib

RELEASE_LIBRARIES = (
    "Vouchfx.Engine.Abstractions", "Vouchfx.Sdk", "Vouchfx.Engine.Authoring",
    "Vouchfx.Engine.Compilation", "Vouchfx.Sdk.Testing",
)
PLANNING = "Vouchfx.Engine.Planning"
TOOL_SETTINGS = "tools/net8.0/any/DotnetToolSettings.xml"
MAX_ARCHIVE_BYTES = 250_000_000
MAX_ENTRIES = 10_000
MAX_ENTRY_BYTES = 256_000_000
MAX_EXPANDED_BYTES = 1_000_000_000
MAX_XML_BYTES = 2_000_000
MAX_XML_NODES = 50_000
MAX_XML_DEPTH = 128
# Internal sprint/task identifiers and forensic review register, not "peer" in
# technical prose or names such as PeerCertificate / PeerReviewPolicy.
INTERNAL_PROSE = re.compile(
    r"\bS[0-9]{2}\b|\bsprint[\s-]+[0-9]+\b|\bREQ-[0-9]{3}\b|\bUS-S[0-9]+"
    r"|\bEDGE-[0-9]{3}\b|\bF-[0-9]{2}\b|\bG-(?:MAJOR|MINOR)-[0-9]+\b"
    r"|\bfix[\s_-]*round\b|\bpeer[\s_-]*review\b|\bgatekeeper\b"
    r"|\b(?:plan/|docs/03_MVP_Project_Plan|docs/reviews/|HUMAN_TODO\b)", re.I,
)
# Compiler symbol references are contracts, not prose. Displayed langword
# values, other attributes and all text (including examples/comments) are checked.
SYMBOL_ATTRIBUTES = {
    ("member", "name"), ("param", "name"), ("typeparam", "name"),
    ("paramref", "name"), ("typeparamref", "name"), ("see", "cref"),
    ("seealso", "cref"), ("exception", "cref"),
}
PROSE_BLOCKS = {"doc", "assembly", "members", "member", "summary", "remarks",
                "returns", "param", "typeparam", "exception", "example", "para",
                "list", "listheader", "item", "term", "description", "code"}


class InvalidPublication(ValueError):
    """A fixed diagnostic, without package paths or rejected content."""


def parse_xml(data):
    """Bound input/tree size and reject DTDs before any entity is expanded."""
    if len(data) > MAX_XML_BYTES:
        raise InvalidPublication("XML input exceeds the publication limit")
    builder = ET.TreeBuilder(insert_comments=True, insert_pis=True)
    parser = expat.ParserCreate()
    depth = nodes = 0

    def start(name, attrs):
        nonlocal depth, nodes
        depth += 1
        nodes += 1
        if depth > MAX_XML_DEPTH or nodes > MAX_XML_NODES:
            raise InvalidPublication("XML tree exceeds the publication limit")
        builder.start(name, attrs)

    def end(name):
        nonlocal depth
        builder.end(name)
        depth -= 1

    def comment(text):
        nonlocal nodes
        nodes += 1
        if nodes > MAX_XML_NODES:
            raise InvalidPublication("XML tree exceeds the publication limit")
        if INTERNAL_PROSE.search(text):
            raise InvalidPublication("XML contains internal publication prose")
        builder.comment(text)

    def processing_instruction(target, text):
        nonlocal nodes
        nodes += 1
        if nodes > MAX_XML_NODES:
            raise InvalidPublication("XML tree exceeds the publication limit")
        if INTERNAL_PROSE.search(target + " " + text):
            raise InvalidPublication("XML contains internal publication prose")
        builder.pi(target, text)

    def forbid(*_args):
        raise InvalidPublication("DTD and entity declarations are forbidden")

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.CharacterDataHandler = builder.data
    parser.CommentHandler = comment
    parser.ProcessingInstructionHandler = processing_instruction
    parser.StartDoctypeDeclHandler = forbid
    parser.EntityDeclHandler = forbid
    parser.ExternalEntityRefHandler = forbid
    try:
        parser.Parse(data, True)
        return builder.close()
    except (expat.ExpatError, ET.ParseError, ValueError) as error:
        if isinstance(error, InvalidPublication):
            raise
        raise InvalidPublication("malformed XML in package") from None


def check_prose(tree):
    """Join inline text, but never invent identifiers across separate contracts."""
    parts = []

    def inspect(text):
        if INTERNAL_PROSE.search(text):
            raise InvalidPublication("XML contains internal publication prose")

    def flush():
        inspect("".join(parts))
        parts.clear()

    def walk(element):
        block = element.tag in PROSE_BLOCKS
        if block:
            flush()
        if not isinstance(element.tag, str):
            # Published XML comments/PIs are checked independently; their removal
            # must also not defeat a token split in the surrounding visible text.
            inspect(element.text or "")
            return
        if element.tag in {"see", "seealso"}:
            # Displayed labels participate in the surrounding inline prose in
            # document order; symbol references remain independently exempt.
            parts.append(element.attrib.get("langword", ""))
        parts.append(element.text or "")
        for name, value in element.attrib.items():
            if (element.tag, name) not in SYMBOL_ATTRIBUTES:
                inspect(value)
        for child in element:
            walk(child)
            parts.append(child.tail or "")
        if block:
            flush()

    walk(tree)
    flush()


def check_tool_settings(tree):
    commands = list(tree)
    if (tree.tag != "DotNetCliTool" or tree.attrib != {"Version": "1"}
            or len(commands) != 1 or commands[0].tag != "Commands"
            or commands[0].attrib or len(commands[0]) != 1):
        raise InvalidPublication("CLI tool-settings contract is invalid")
    command = commands[0][0]
    if (command.tag != "Command" or list(command)
            or command.attrib != {"Name": "vouchfx", "EntryPoint": "vouchfx.dll", "Runner": "dotnet"}
            or any(text.strip() for text in tree.itertext())):
        raise InvalidPublication("CLI tool-settings contract is invalid")


def check_package(path, library=None):
    if path.stat().st_size > MAX_ARCHIVE_BYTES:
        raise InvalidPublication("package exceeds the publication size limit")
    with zipfile.ZipFile(path) as package:
        entries = package.infolist()
        if len(entries) > MAX_ENTRIES:
            raise InvalidPublication("package entry count exceeds the publication limit")
        names = [entry.filename for entry in entries]
        if len(set(name.casefold() for name in names)) != len(names):
            raise InvalidPublication("duplicate package member")
        if (sum(entry.file_size for entry in entries) > MAX_EXPANDED_BYTES
                or any(entry.file_size > MAX_ENTRY_BYTES for entry in entries)):
            raise InvalidPublication("expanded package exceeds the publication limit")
        xml_entries = [entry for entry in entries if entry.filename.lower().endswith(".xml")]
        expected = f"lib/net8.0/{library}.xml" if library else TOOL_SETTINGS
        if expected not in names:
            raise InvalidPublication("required XML package member is missing")
        if not library and {entry.filename for entry in xml_entries} != {"[Content_Types].xml", TOOL_SETTINGS}:
            raise InvalidPublication("CLI package contains unexpected XML")
        for entry in xml_entries:
            if entry.file_size > MAX_XML_BYTES:
                raise InvalidPublication("XML input exceeds the publication limit")
            with package.open(entry) as stream:
                data = stream.read(MAX_XML_BYTES + 1)
            tree = parse_xml(data)
            check_prose(tree)
            if entry.filename == expected:
                if library:
                    if (tree.tag != "doc" or tree.findtext("assembly/name") != library
                            or not tree.findall("members/member")):
                        raise InvalidPublication("library documentation contract is invalid")
                else:
                    check_tool_settings(tree)


def check_packages(directory, version, include_planning=False):
    libraries = RELEASE_LIBRARIES + ((PLANNING,) if include_planning else ())
    for library in libraries:
        check_package(directory / f"{library}.{version}.nupkg", library)
    check_package(directory / f"vouchfx.{version}.nupkg")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages-dir", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--include-planning", action="store_true")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?", args.version):
        print("FAIL: invalid validation version", file=sys.stderr)
        return 1
    try:
        check_packages(args.packages_dir, args.version, args.include_planning)
    except InvalidPublication as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    except (OSError, ValueError, LookupError, EOFError, zipfile.BadZipFile,
            RuntimeError, zlib.error, lzma.LZMAError):
        print("FAIL: package is missing or unreadable", file=sys.stderr)
        return 1
    print("OK: required library XML and CLI XML publication contracts passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
