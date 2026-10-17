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


# --- pymdownx.snippets source-tree allowlist (deliverable 1d) -------------
#
# This mirrors the REAL pymdownx.snippets extension's directive-detection and
# path-extraction logic (verified against the installed package's source,
# site-packages/pymdownx/snippets.py — SnippetPreprocessor.RE_ALL_SNIPPETS /
# RE_SNIPPET / parse_snippets — and against its actual runtime behaviour: see
# the empirical probe referenced in this project's review history). Getting
# this wrong in the permissive direction is how a MAJOR finding happened
# here before: an earlier version of this parser only recognised *quoted*
# lines inside a `--8<--` block as targets, but pymdownx's block form does
# the OPPOSITE — a bare, unquoted line is the real target; a quoted line's
# quotes become part of a (bogus, non-existent) filename, which
# pymdownx silently ignores when check_paths is off, or — as in this
# project's actual mkdocs.yml (check_paths: true) — fails the whole build
# outright with SnippetMissingError. Concretely, confirmed by feeding both
# forms through `markdown.Markdown(extensions=['pymdownx.snippets'])`:
#
#   --8<--          <- embeds the real file: BLOCK BARE is the live form.
#   plan/leak.md
#   --8<--
#
#   --8<--          <- embeds nothing (quotes become part of the literal,
#   "plan/leak.md"     nonexistent path "plan/leak.md" including the quote
#   --8<--             characters) — BLOCK QUOTED is not a live target form.
#
# So: every non-marker, non-blank, non-";"-comment line inside a block IS a
# target, taken essentially as-is. We additionally strip OPTIONAL surrounding
# quotes before comparing against the allowlist — a deliberate, one-directional
# safety bias: pymdownx itself wouldn't honour a quoted block line as
# "file.md", but flagging it as a candidate target anyway can only make this
# check STRICTER (catch something pymdownx would ignore), never looser (miss
# something pymdownx would actually embed).
#
# Also confirmed empirically: pymdownx.snippets has NO comma-separated
# multi-file syntax. `--8<-- "a.md, b.md"` is parsed as a single literal path
# "a.md, b.md" (which then fails to resolve), not as two targets. An earlier
# version of this parser split on commas as if that were a real feature; it
# has been removed — only an optional trailing `:start:end` / `:section`
# suffix is stripped now, matching pymdownx's actual RE_SNIPPET_FILE.

# Marker detection, mirroring pymdownx's RE_ALL_SNIPPETS: 1+ dashes around
# "8<-" (not just exactly "--8<--"), an optional leading ";" escape (which
# defuses the whole directive), and an inline form that requires its quoted
# argument to be the entire rest of the line.
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


def _strip_range_or_section_suffix(path: str) -> str:
    """Strip pymdownx's optional trailing :line:line[,line:line...] or
    :section suffix (RE_SNIPPET_FILE). Only the portion before the first ':'
    ever matters here, since none of the three allowed targets contain one.
    """
    return path.split(":", 1)[0]


def _strip_optional_quotes(candidate: str) -> str:
    if len(candidate) >= 2 and candidate[0] == candidate[-1] and candidate[0] in "\"'":
        return candidate[1:-1].strip()
    return candidate


def _iter_snippet_directives(text: str) -> Iterator[tuple[int, str]]:
    """Yield (1-based line number, target path) for every effective
    --8<-- directive target in `text` — see the block comment above this
    function for exactly what "effective" means and how it was verified."""
    in_block = False
    for lineno, raw_line in enumerate(text.splitlines(), start=1):
        match = _RE_ALL_SNIPPETS.match(raw_line)
        if match:
            if match.group("escape"):
                continue  # ";--8<--..." — escaped, not a real directive
            if match.group("block_marker"):
                in_block = not in_block
                continue
            if in_block:
                # pymdownx ignores an inline marker found inside a block.
                continue
            target = _strip_range_or_section_suffix(match.group("inline_snippet")[1:-1].strip())
            if target:
                yield lineno, target
            continue

        if not in_block:
            continue  # ordinary prose line

        candidate = raw_line.strip()
        if not candidate or candidate.startswith(";"):
            continue  # blank or commented-out block line — not a target
        candidate = _strip_optional_quotes(candidate)  # defensive superset, see above
        target = _strip_range_or_section_suffix(candidate)
        if target:
            yield lineno, target


def validate_snippet_inputs(repo_root: Path, inputs: frozenset[str]) -> None:
    """Scan admitted Markdown and root snippets; never open a directive target.

    Call only after validate_source_inputs has admitted every name and metadata.
    Scanning all three root snippets also covers recursive snippet expansion.
    """
    for relative in sorted(inputs):
        if relative not in SNIPPET_INPUTS and not (
            relative.startswith("docs/") and Path(relative).suffix.lower() in {".md", ".markdown"}
        ):
            continue
        try:
            text = (repo_root / relative).read_text(encoding="utf-8")
        except (OSError, UnicodeError) as exc:
            raise PublicationInputError(
                f"snippet source unreadable: {relative} ({type(exc).__name__})"
            ) from None
        for lineno, target in _iter_snippet_directives(text):
            if target not in SNIPPET_INPUTS:
                raise PublicationInputError(
                    f"{relative}:{lineno}: --8<-- references {target!r}, which is not in the "
                    f"allowlist {sorted(SNIPPET_INPUTS)}"
                )
