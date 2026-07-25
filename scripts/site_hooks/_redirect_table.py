"""Shared derivation of the legacy-URL -> new-MkDocs-URL redirect table.

Used by both scripts/site_hooks/redirects.py (which writes the actual stub
files at build time) and scripts/check_site.py (which asserts every one of
them exists in the built output and points at the right place) — a single
source of truth so the two can never silently drift apart.

The table is derived from scripts/build_site.py's DOCS list — READ ONLY,
never modified, never executed (see `_load_docs_table` for why static AST
parsing is used instead of importing/exec'ing that module) — plus three
special-case root files whose MkDocs slug doesn't match a 1:1
strip-"docs/"-and-".md" transform of their upstream filename
(CHANGELOG.md/GOVERNANCE.md/README.md -> changelog/governance/
project-readme, the slugs chosen when those stub pages were scaffolded),
plus the old portal page docs.html, which was never rendered from a single
markdown file at all (build_portal() hand-assembled it), so it has no DOCS
entry to derive from.
"""
from __future__ import annotations

import ast
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
BUILD_SITE_PY = REPO_ROOT / "scripts" / "build_site.py"

# rel (as it appears in build_site.py's DOCS) -> the MkDocs slug it maps to,
# for the root files whose slug isn't derivable by stripping a "docs/"
# prefix (they have none) and lower-casing/renaming.
_ROOT_FILE_SLUGS = {
    "CHANGELOG.md": "changelog",
    "GOVERNANCE.md": "governance",
    "README.md": "project-readme",
}

# (legacy_html_path, new_slug) pairs with no DOCS entry to derive from at
# all. docs.html was the old custom generator's hand-built portal page
# (build_portal() in vouchfx_site_tools), not a rendered markdown file;
# it redirects to the same target as the landing page's own new "Docs"
# link (getting-started/).
_EXTRA_LEGACY_REDIRECTS: tuple[tuple[str, str], ...] = (
    ("docs.html", "getting-started"),
)


def _load_docs_table() -> list[tuple[str, ...]]:
    """Extract build_site.py's DOCS list via static AST parsing.

    This never executes build_site.py — so it has no dependency on
    vouchfx_site_tools being importable, no exposure to that module's own
    top-level code (which reads sys.argv), and no risk of accidentally
    triggering any future side effect a naive `import`/`exec` might pick
    up. build_site.py is read-only input to this repo's docs scaffold;
    this function only ever reads it as text.

    Return type is deliberately `list[tuple[str, ...]]`, not
    `list[tuple[str, str, str]]`: build_site.py's own `DOCS` entries are
    (rel, group, label) 3-tuples today, but vouchfx_site_tools.SiteConfig's
    `docs` field (the shape build_site.py's DOCS mirrors) supports an
    OPTIONAL 4th "description" element (specs/seo-fleet-audit.md, B4/critic
    M-3) — a fixed 3-tuple annotation here would silently lie about what
    `ast.literal_eval` can actually return the moment DOCS gains one such
    entry. `build_redirect_table` below unpacks defensively (`rel,
    *_rest`), not by fixed arity, for the same reason.
    """
    source = BUILD_SITE_PY.read_text(encoding="utf-8", errors="replace")
    tree = ast.parse(source, filename=str(BUILD_SITE_PY))
    for node in ast.walk(tree):
        target_names: list[str] = []
        value_node = None
        if isinstance(node, ast.Assign):
            target_names = [t.id for t in node.targets if isinstance(t, ast.Name)]
            value_node = node.value
        elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
            target_names = [node.target.id]
            value_node = node.value
        if "DOCS" in target_names and value_node is not None:
            return ast.literal_eval(value_node)
    raise RuntimeError(f"could not find a 'DOCS' assignment in {BUILD_SITE_PY}")


def _new_slug_for(rel: str) -> str:
    """The MkDocs slug (no leading/trailing slash) a DOCS entry's `rel`
    maps to."""
    if rel in _ROOT_FILE_SLUGS:
        return _ROOT_FILE_SLUGS[rel]
    if rel.startswith("docs/") and rel.endswith(".md"):
        return rel[len("docs/") : -len(".md")]
    raise ValueError(
        f"don't know the new MkDocs slug for DOCS entry {rel!r} — it is neither a "
        f"known root-file special case {sorted(_ROOT_FILE_SLUGS)} nor a docs/*.md path"
    )


def _validate_entry(legacy_path: str, new_slug: str) -> None:
    """Reject a table entry whose legacy_path or new_slug is an absolute
    path, or contains a '..' segment.

    Defence in depth: build_redirect_table()'s inputs are trusted today
    (a static AST read of build_site.py's DOCS list, plus a small literal
    tuple), but a stub's destination is a real filesystem write derived
    from these two strings (see redirects.py) — this guards against a
    future DOCS entry, or a bug in `_new_slug_for`, silently producing
    something that could escape site_dir when joined, rather than that
    only ever being caught (or not) at write time.
    """
    for label, value in (("legacy_path", legacy_path), ("new_slug", new_slug)):
        normalised = value.replace("\\", "/")
        is_absolute = normalised.startswith("/") or (len(normalised) > 1 and normalised[1] == ":")
        has_dotdot = ".." in normalised.split("/")
        if is_absolute or has_dotdot:
            reason = "is an absolute path" if is_absolute else "contains a '..' segment"
            raise ValueError(
                f"redirect table entry rejected: {label}={value!r} {reason} — "
                "refusing to derive a stub destination from it"
            )


def build_redirect_table() -> list[tuple[str, str]]:
    """Return [(legacy_html_path, new_slug), ...].

    `legacy_html_path` is relative to the site root and mirrors
    build_site.py's/vouchfx_site_tools' own out_path() exactly
    (`rel[:-3] + ".html"` — i.e. the source repo's own directory layout,
    with a .html extension). `new_slug` has no leading or trailing slash
    (e.g. "01_Technical_Architecture_and_Engineering_Blueprint",
    "kb/dcp-orchestrator-portability"). Every entry is validated (see
    `_validate_entry`) before being included.
    """
    table: list[tuple[str, str]] = []
    for rel, *_rest in _load_docs_table():
        # *_rest tolerates a 3- OR 4-element DOCS entry (see
        # _load_docs_table's docstring) — only `rel` (the first element)
        # is ever used to derive a redirect-table row; a fixed 3-name
        # unpack here would raise ValueError the moment a DOCS entry
        # gained the optional 4th "description" element, taking down this
        # module's on_post_build hook and check_site.py's
        # check_legacy_redirects with it.
        legacy = rel[: -len(".md")] + ".html"
        new_slug = _new_slug_for(rel)
        _validate_entry(legacy, new_slug)
        table.append((legacy, new_slug))
    for legacy, new_slug in _EXTRA_LEGACY_REDIRECTS:
        _validate_entry(legacy, new_slug)
        table.append((legacy, new_slug))
    return table


def relative_target(legacy_path: str, new_slug: str) -> str:
    """Root-relative (never domain-absolute) path from a redirect stub at
    `legacy_path` back to the site root, then forward to `new_slug`/ — this
    is what makes the stub work unmodified under whatever base path or
    domain the site is served from (GitHub Pages' /vouchfx/ prefix, a
    future custom domain, or a local `mkdocs serve` preview at /), without
    ever hard-coding site_url.
    """
    depth = legacy_path.count("/")
    prefix = "../" * depth
    return f"{prefix}{new_slug}/"
