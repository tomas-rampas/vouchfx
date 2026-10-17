"""Admit documentation names before scanning approved Markdown snippet sources."""
from __future__ import annotations

import json
import os
import re
import stat
import unicodedata
from pathlib import Path, PurePosixPath
from typing import Any, Iterable, Iterator

MANIFEST_NAME = "site-publication-inputs.json"
SOURCE_DIRS = ("docs", "site", "overrides")
MARKDOWN_SUFFIXES = frozenset({".markdown", ".mdown", ".mkdn", ".mkd", ".md"})
SNIPPET_INPUTS = frozenset({"README.md", "CHANGELOG.md", "GOVERNANCE.md"})
PRIVATE_DOC = "docs/03_MVP_Project_Plan.md"
PRIVATE_DIR = "docs/reviews"
MAX_MANIFEST_BYTES = 64 * 1024


class PublicationInputError(RuntimeError):
    """Only input paths/categories belong in these diagnostics, never file content."""


def _is_private(path: str) -> bool:
    folded = path.casefold()
    private_doc, private_dir = PRIVATE_DOC.casefold(), PRIVATE_DIR.casefold()
    return folded == private_doc or folded == private_dir or folded.startswith(private_dir + "/")


def _canonical_path(value: object) -> str:
    if not isinstance(value, str) or not value:
        raise PublicationInputError("manifest: input path must be a non-empty string")
    path = PurePosixPath(value)
    if (
        not path.parts or path.is_absolute() or path.as_posix() != value
        or "\\" in value or any(c in value for c in ':*?<>|')
        or any(ord(c) < 32 or ord(c) == 127 for c in value)
        or unicodedata.normalize("NFC", value) != value
        or any(p in {".", ".."} or p.rstrip(". ") != p for p in path.parts)
    ):
        raise PublicationInputError("manifest: input path is not canonical")
    return value


def _unique_members(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise PublicationInputError("manifest: duplicate JSON member")
        result[key] = value
    return result


def _check_regular(path: Path, relative: str, *, directory: bool = False) -> None:
    try:
        info = path.lstat()
    except OSError as exc:
        raise PublicationInputError(f"unreadable input: {relative} ({type(exc).__name__})") from None
    reparse = getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    if stat.S_ISLNK(info.st_mode) or reparse:
        raise PublicationInputError(f"symlink/reparse input: {relative}")
    expected = stat.S_ISDIR if directory else stat.S_ISREG
    if not expected(info.st_mode):
        raise PublicationInputError(f"non-regular input: {relative}")


def load_manifest(repo_root: Path) -> frozenset[str]:
    path = repo_root / MANIFEST_NAME
    _check_regular(path, MANIFEST_NAME)
    try:
        with path.open("rb") as source:
            raw = source.read(MAX_MANIFEST_BYTES + 1)
        if len(raw) > MAX_MANIFEST_BYTES:
            raise PublicationInputError("manifest: exceeds 64 KiB limit")
        manifest = json.loads(raw, object_pairs_hook=_unique_members)
    except (OSError, UnicodeError, ValueError, RecursionError) as exc:
        raise PublicationInputError(f"manifest: cannot read/parse ({type(exc).__name__})") from None
    if (
        not isinstance(manifest, dict) or set(manifest) != {"version", "inputs"}
        or type(manifest["version"]) is not int or manifest["version"] != 1
        or not isinstance(manifest["inputs"], list) or not manifest["inputs"]
    ):
        raise PublicationInputError("manifest: expected version 1 and a non-empty inputs list")
    inputs: set[str] = set()
    aliases: set[str] = set()
    for item in manifest["inputs"]:
        relative = _canonical_path(item)
        if relative not in SNIPPET_INPUTS and PurePosixPath(relative).parts[0] not in SOURCE_DIRS:
            raise PublicationInputError(f"manifest: unsupported source path: {relative}")
        if relative in SOURCE_DIRS or _is_private(relative):
            raise PublicationInputError(f"manifest: excluded/non-file source path: {relative}")
        alias = relative.casefold()
        if alias in aliases:
            raise PublicationInputError(f"manifest: duplicate/case-alias input: {relative}")
        aliases.add(alias)
        inputs.add(relative)
    if not SNIPPET_INPUTS.issubset(inputs) or not any(p.startswith("docs/") and p.endswith(".md") for p in inputs):
        raise PublicationInputError("manifest: root snippet inputs and public Markdown are required")
    return frozenset(inputs)


def validate_source_inputs(repo_root: Path) -> frozenset[str]:
    """Inspect names/metadata only; tracked and untracked inputs obey the same policy."""
    inputs = load_manifest(repo_root)
    actual: set[str] = set()
    aliases: set[str] = set()

    def record(path: Path, *, directory: bool = False) -> str:
        relative = path.relative_to(repo_root).as_posix()
        _canonical_path(relative)
        _check_regular(path, relative, directory=directory)
        alias = relative.casefold()
        if alias in aliases:
            raise PublicationInputError(f"source case-alias: {relative}")
        aliases.add(alias)
        return relative

    def walk_error(exc: OSError) -> None:
        raise PublicationInputError(f"source directory: cannot enumerate ({type(exc).__name__})") from None

    for name in SOURCE_DIRS:
        source = repo_root / name
        record(source, directory=True)
        for directory, dirs, files in os.walk(source, followlinks=False, onerror=walk_error):
            for child in sorted(dirs):
                relative = record(Path(directory) / child, directory=True)
                if _is_private(relative):
                    dirs.remove(child)
            for child in sorted(files):
                relative = record(Path(directory) / child)
                if _is_private(relative):
                    continue
                if relative not in inputs:
                    category = "Markdown" if relative.endswith(".md") else "asset/metadata"
                    raise PublicationInputError(f"unregistered {category}: {relative}")
                actual.add(relative)
    for relative in sorted(SNIPPET_INPUTS):
        record(repo_root / relative)
        actual.add(relative)
    missing = sorted(inputs - actual)
    if missing:
        raise PublicationInputError(f"manifest input missing/case-mismatched: {missing[0]}")
    return inputs


def validate_mkdocs_files(
    files: Iterable[Any], repo_root: Path, inputs: frozenset[str], theme_dirs: Iterable[str],
) -> None:
    """Check the final collection after blog generation, before render/copy events."""
    docs_dir = repo_root / "docs"
    theme_roots = tuple(Path(d).resolve() for d in theme_dirs)
    public_pages = 0
    for file in files:
        relative = _canonical_path(file.src_uri)
        source_dir = Path(file.src_dir) if file.src_dir is not None else None
        if source_dir == docs_dir:
            source = "docs/" + relative
            if _is_private(source):
                # EXCLUDED (-3) is never read, including by `mkdocs serve`.
                if file.inclusion.value != -3:
                    raise PublicationInputError(f"private source included in collection: {source}")
                continue
            if source not in inputs:
                raise PublicationInputError(f"unregistered collection input: {source}")
            public_pages += int(file.is_documentation_page() and file.inclusion.value > -2)
            continue
        # Material derives archive/category/pagination pages from registered posts.
        # Its generated files are temporary, not additional repository inputs.
        if file.generated_by == "material/blog" and relative.startswith("blog/") and relative.endswith(".md"):
            continue
        physical = Path(file.abs_src_path).resolve() if file.abs_src_path else None
        if physical is not None and any(physical.is_relative_to(root) for root in theme_roots):
            continue  # Installed theme assets/templates are dependency code.
        raise PublicationInputError(f"unsupported collection input: {relative}")
    if not public_pages:
        raise PublicationInputError("collection: no registered public Markdown pages")


# Shared pymdownx.snippets grammar. SnippetExtension registers priority 32,
# before Markdown's NormalizeWhitespace (30), so source lines are split on LF,
# not Python's broader splitlines() separators. Locally opened snippets also
# use universal-newline text iteration, not splitlines(). Path.read_text below
# performs universal-newline decoding before this scanner sees the source.
# Block paths retain quotes; only inline paths lose their surrounding quotes.
# A suffix is removed only if RE_SNIPPET_FILE recognises the entire spelling;
# an arbitrary colon can be part of a literal filename and must stay visible
# to the exact root-file allowlist.
_RE_ALL_SNIPPETS = re.compile(
    r"""(?x)
    ^[ \t]*
    (?P<escape>;*)
    (?:
        -{1,}8<-{1,}[ \t]+
        (?P<inline_snippet>"(?:\\"|[^"\n\r])+?"|'(?:\\'|[^'\n\r])+?')(?![ \t]) |
        (?P<block_marker>-{1,}8<-{1,})(?![ \t])
    )\r?$
    """
)


_RE_SNIPPET = re.compile(r"^[ \t]*(?P<snippet>.*?)\r?$")
_RE_SNIPPET_FILE = re.compile(
    r"(?i)(.*?)(?:((?::-?[0-9]*){1,2}(?:(?:,(?=[-0-9:])-?[0-9]*)(?::-?[0-9]*)?)*)|(:[a-z][-_0-9a-z]*))?$"
)


# Partial targets may contain standalone section metadata, but no other marker
# token anywhere: selection can change block context or expose literal syntax.
_RE_SNIPPET_TOKEN = re.compile(r"-{1,}8<-{1,}")
_RE_SECTION_BOUNDARY = re.compile(
    r"(?i)[ \t]*-{1,}8<-{1,}[ \t]+\[[ \t]*(?:start|end)[ \t]*:"
    r"[ \t]*[a-z][-_0-9a-z]*[ \t]*\][ \t]*\r?"
)


def _has_nested_snippet_syntax(text: str) -> bool:
    return any(
        _RE_SNIPPET_TOKEN.search(line) and not _RE_SECTION_BOUNDARY.fullmatch(line)
        for line in text.split("\n")
    )


# MkDocs removes leading page metadata before running Markdown/snippets. Marker
# tokens there are unsupported: removing them could change a later block state.
# Match its header/key/continuation syntax without parsing YAML or reading files.
_RE_YAML_METADATA = re.compile(r"^-{3}[ \t]*\n(.*?\n)(?:\.{3}|-{3})[ \t]*\n", re.UNICODE | re.DOTALL)
_RE_METADATA_KEY = re.compile(r"^[ ]{0,3}(?P<key>[A-Za-z0-9_-]+):\s*(?P<value>.*)")
_RE_METADATA_MORE = re.compile(r"^([ ]{4}|\t)(\s*)(?P<value>.*)")


def _validate_metadata_snippet_markers(relative: str, text: str) -> None:
    yaml_header = _RE_YAML_METADATA.match(text)
    if yaml_header:
        # Even invalid YAML is conservatively refused if this candidate header
        # contains a marker. No YAML dependency is needed for early admission.
        has_marker = _RE_SNIPPET_TOKEN.search(yaml_header.group()) is not None
    else:
        has_key = False
        has_marker = False
        for line in text.split("\n"):
            if not line.strip():
                break
            if _RE_METADATA_KEY.match(line):
                has_key = True
            elif not (has_key and _RE_METADATA_MORE.match(line)):
                break
            if _RE_SNIPPET_TOKEN.search(line):
                has_marker = True
                break
    if has_marker:
        raise PublicationInputError(
            f"{relative}: snippet marker in document metadata; move snippet syntax into the page body"
        )


def _iter_snippet_references(text: str) -> Iterator[tuple[int, str, bool]]:
    """Yield line/target/partial triples using the extension's exact grammar."""
    in_block = False
    for lineno, raw_line in enumerate(text.split("\n"), start=1):
        match = _RE_ALL_SNIPPETS.match(raw_line)
        if match:
            if match.group("escape"):
                continue
            if match.group("block_marker"):
                in_block = not in_block
                continue
            if in_block:
                continue  # Inline notation inside a block is ignored.
            candidate = match.group("inline_snippet")[1:-1].strip()
        elif in_block:
            block_path = _RE_SNIPPET.match(raw_line)
            if block_path is None:
                continue
            candidate = block_path.group("snippet").strip()
        else:
            continue
        if not candidate or candidate.startswith(";"):
            continue
        path_match = _RE_SNIPPET_FILE.match(candidate)
        if path_match is not None:
            target = path_match.group(1).strip()
            if target:
                yield lineno, target, bool(path_match.group(2) or path_match.group(3))


def _iter_snippet_directives(text: str) -> Iterator[tuple[int, str]]:
    """Expose target names for callers that do not need selection metadata."""
    for lineno, target, _partial in _iter_snippet_references(text):
        yield lineno, target


def validate_snippet_inputs(repo_root: Path, inputs: frozenset[str]) -> None:
    """Scan admitted Markdown and root snippets; never open a directive target.

    Call only after validate_source_inputs has admitted every name and metadata.
    Whole root snippets may recurse. Partial includes may only select ordinary
    content: selecting nested directives could change their original block state.
    """
    bodies: dict[str, str] = {}
    for relative in sorted(inputs):
        if relative not in SNIPPET_INPUTS and not (
            relative.startswith("docs/") and Path(relative).suffix.lower() in MARKDOWN_SUFFIXES
        ):
            continue
        try:
            # MkDocs File.content_string strips a page BOM; pymdownx's root
            # snippet reader uses plain UTF-8 and keeps it.
            encoding = "utf-8-sig" if relative.startswith("docs/") else "utf-8"
            bodies[relative] = (repo_root / relative).read_text(encoding=encoding)
        except (OSError, UnicodeError) as exc:
            raise PublicationInputError(
                f"snippet source unreadable: {relative} ({type(exc).__name__})"
            ) from None
    for relative, text in sorted(bodies.items()):
        if relative.startswith("docs/"):
            _validate_metadata_snippet_markers(relative, text)
    nested_targets = {
        relative for relative in SNIPPET_INPUTS
        if _has_nested_snippet_syntax(bodies[relative])
    }
    for relative, text in sorted(bodies.items()):
        for lineno, target, partial in _iter_snippet_references(text):
            if target not in SNIPPET_INPUTS:
                raise PublicationInputError(
                    f"{relative}:{lineno}: --8<-- references {target!r}, which is not in the "
                    f"allowlist {sorted(SNIPPET_INPUTS)}"
                )
            if partial and target in nested_targets:
                raise PublicationInputError(
                    f"{relative}:{lineno}: partial snippet include of {target!r} contains "
                    "nested snippet syntax; use a whole-file include"
                )
