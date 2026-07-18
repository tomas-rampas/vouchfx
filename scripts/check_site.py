#!/usr/bin/env python3
"""Post-build publication gate for the vouchfx MkDocs site.

Run this against the built output directory straight after `mkdocs build`,
before anything is deployed:

    python scripts/check_site.py _site

It asserts the handful of things that, if wrong, are silent until a real
user hits them in production:

  (a) every pymdownx.snippets `--8<--` directive in the docs/ SOURCE tree
      targets one of the three root files this scaffold deliberately
      embeds (README.md, CHANGELOG.md, GOVERNANCE.md) — see
      `check_snippet_allowlist` for why this has to be checked at the
      directive level, before a build even runs;
  (b) the bespoke landing page (site/index.html) published as the site
      root, not a Material-generated homepage;
  (c) .nojekyll shipped, so GitHub Pages doesn't run Jekyll over the build
      and mangle underscore-prefixed paths;
  (d) nothing from four maintainer-local, confidential surfaces reached
      the build, in any path form or embedded in any built page's
      content — see `check_boundary` for the two-tier design;
  (e) the key user-facing pages exist at their new directory-URL
      locations.

Exit 0: the build is safe to publish. Exit 1: a check failed; the printed
message says which one and why it matters, so a CI failure is actionable
without re-deriving the reasoning here.

--------------------------------------------------------------------------
Confidential-surface boundary — how it actually works (read this before
touching `check_boundary` or `_confidential_surfaces`)
--------------------------------------------------------------------------
This script is PUBLIC (it ships in the repository). It must never contain
any confidential *content* — no prose, no distinctive phrases, no excerpts
copy-pasted from the files it protects. It only ever hard-codes the four
surfaces' *names* (filenames/directory names such as "03_MVP_Project_Plan",
"reviews", "HUMAN_TODO", "plan") — these are structural identifiers the
project's own conventions already use in the open (CLAUDE.md, .gitignore,
commit history), not confidential material in themselves.

The boundary check runs in two tiers:

  Tier 1 — path/name matching (always active, in CI and locally alike).
  Flags any built file or directory whose path contains one of the four
  surface identifiers above. This also flags the same identifiers as
  literal, case-insensitive substrings inside any built text-like file's
  *content* — but only for the two file-shaped surfaces
  (03_MVP_Project_Plan.md, HUMAN_TODO.md), whose names are distinctive
  enough not to collide with ordinary prose. It deliberately does NOT do a
  bare content-substring check for "reviews" or "plan": both are common
  English words and would drown the check in false positives.

  Tier 2 — content fingerprinting (active only when a confidential source
  exists on disk). On a maintainer checkout, docs/03_MVP_Project_Plan.md,
  docs/reviews/, HUMAN_TODO.md and plan/ all exist. In CI, none of them do
  — they are untracked/gitignored — so this tier has nothing to read and
  silently does nothing there; Tier 1 still runs regardless. When active,
  it reads each confidential source file, collapses whitespace, and slices
  it into overlapping 512-character windows sampled at a 128-character
  stride. It then checks whether any window reappears verbatim (after the
  same whitespace normalisation) inside any built text-like output file.
  No confidential prose is ever written into this script, printed to a
  log, or held longer than one process's memory — fingerprints are derived
  fresh from disk on every run, compared in memory, and a match is
  reported by naming the offending built file and which surface it
  matched, never by echoing the matched text itself.

  The window is deliberately large (roughly a paragraph, not a sentence).
  This project's internal planning docs and its public docs are written by
  the same author about the same system, so they legitimately share
  identical short phrases, technology lists and even whole boilerplate
  sentences (a real one found while tuning this: a ~250-character telemetry
  privacy sentence appears verbatim in both docs/03_MVP_Project_Plan.md and
  the public landing page). A 64-character window flagged 56 such
  coincidental matches against this repository's actual current content;
  512 characters flags zero, while still catching the kind of accidental
  paragraph-or-larger copy-paste this check exists to catch. This is a
  heuristic, not a proof of absence: with a 512-character window and
  128-character stride, a verbatim run of roughly 640+ normalised
  characters is guaranteed to be caught regardless of its alignment in the
  source; shorter runs may or may not be, depending on luck of alignment.
  Paraphrased content, or content with markup interleaved tightly enough
  that no ~640-character run survives contiguously, will not be caught.

Stdlib only — no dependencies to install.
"""
from __future__ import annotations

import re
import sys
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
DOCS_DIR = REPO_ROOT / "docs"

LANDING_MARKER = "brand__name"

KEY_PAGE_SLUGS = (
    "getting-started",
    "changelog",
    "roadmap",
    "governance",
    "project-readme",
    "01_Technical_Architecture_and_Engineering_Blueprint",
)

# Only these three root files may be pulled into docs/ via pymdownx.snippets.
# mkdocs.yml's base_path includes the repo root, so without this allowlist a
# --8<-- directive could reach anything in the repository, confidential
# material included.
ALLOWED_SNIPPET_TARGETS = {"README.md", "CHANGELOG.md", "GOVERNANCE.md"}

# Text-like built-output suffixes worth scanning for boundary leaks. Material
# serialises full page text into its search index JSON, so json/js/xml/txt
# matter just as much as html.
TEXT_LIKE_SUFFIXES = {".html", ".js", ".json", ".xml", ".txt"}

# Tuned empirically against this repository's real content — see the module
# docstring's Tier 2 section for why 64 chars was too short (56 coincidental
# hits from shared technical vocabulary) and 512/128 gives zero.
FINGERPRINT_WINDOW = 512
FINGERPRINT_STRIDE = 128
MAX_FINGERPRINTS_PER_SURFACE = 3000


class CheckFailed(Exception):
    """A publication check failed; the message explains what and why it matters."""


@dataclass(frozen=True)
class ConfidentialSurface:
    """A maintainer-local surface that must never reach the published build.

    `path_fragment` and `literal_content_marker` are structural identifiers
    (filenames/dirnames) — not confidential content — so hard-coding them
    here is safe. Anything derived from the *actual contents* of these
    surfaces (see Tier 2 in the module docstring) is computed at runtime
    from disk, never stored here.
    """

    name: str
    source: Path
    is_dir: bool
    path_fragment: str  # lower-case; matched per `match_mode`
    match_mode: str  # "substring" (files) or "segment" (directories)
    literal_content_marker: str | None  # None for surfaces too generic to content-match


def _confidential_surfaces() -> tuple[ConfidentialSurface, ...]:
    return (
        ConfidentialSurface(
            name="the internal MVP project plan",
            source=DOCS_DIR / "03_MVP_Project_Plan.md",
            is_dir=False,
            path_fragment="03_mvp_project_plan",
            match_mode="substring",
            literal_content_marker="03_mvp_project_plan",
        ),
        ConfidentialSurface(
            name="internal review evidence",
            source=DOCS_DIR / "reviews",
            is_dir=True,
            path_fragment="reviews",
            match_mode="segment",
            literal_content_marker=None,
        ),
        ConfidentialSurface(
            name="the maintainer's human-only TODO list",
            source=REPO_ROOT / "HUMAN_TODO.md",
            is_dir=False,
            path_fragment="human_todo",
            match_mode="substring",
            literal_content_marker="human_todo",
        ),
        ConfidentialSurface(
            name="the internal delivery plan",
            source=REPO_ROOT / "plan",
            is_dir=True,
            path_fragment="plan",
            match_mode="segment",
            literal_content_marker=None,
        ),
    )


def _normalise(text: str) -> str:
    """Collapse all whitespace runs to single spaces and strip.

    Applied identically to confidential source text and built output text
    so that markdown line-wrapping and HTML's own reflow/indentation don't
    defeat an otherwise-verbatim fingerprint match.
    """
    return " ".join(text.split())


def _iter_source_files(surface: ConfidentialSurface) -> Iterator[Path]:
    if not surface.source.exists():
        return
    if surface.is_dir:
        for path in sorted(surface.source.rglob("*")):
            if path.is_file():
                yield path
    else:
        yield surface.source


def _fingerprints_for(surface: ConfidentialSurface) -> tuple[set[str], int]:
    """Derive this surface's content fingerprints fresh from disk.

    Returns (fingerprints, files_skipped). Never called for a surface whose
    `source` doesn't exist — callers check that first.
    """
    chunks: set[str] = set()
    skipped = 0
    for file in _iter_source_files(surface):
        try:
            raw = file.read_text(encoding="utf-8", errors="ignore")
        except OSError as exc:
            skipped += 1
            print(
                f"WARN [check_boundary]: could not read {file} while fingerprinting "
                f"{surface.name} ({exc}); this source file was not fingerprinted, so a "
                "leak of exactly its content could be missed this run.",
                file=sys.stderr,
            )
            continue
        normalised = _normalise(raw)
        if not normalised:
            continue
        if len(normalised) < FINGERPRINT_WINDOW:
            chunks.add(normalised)
            continue
        for i in range(0, len(normalised) - FINGERPRINT_WINDOW + 1, FINGERPRINT_STRIDE):
            chunks.add(normalised[i : i + FINGERPRINT_WINDOW])

    if len(chunks) > MAX_FINGERPRINTS_PER_SURFACE:
        # Safety valve for a pathologically large surface (e.g. a big plan/
        # tree): keep coverage spread across the whole corpus rather than
        # just its first N characters.
        ordered = sorted(chunks)
        step = len(ordered) / MAX_FINGERPRINTS_PER_SURFACE
        chunks = {ordered[int(i * step)] for i in range(MAX_FINGERPRINTS_PER_SURFACE)}

    return chunks, skipped


def _iter_text_like_files(site_dir: Path) -> Iterator[Path]:
    for path in site_dir.rglob("*"):
        if path.is_file() and path.suffix.lower() in TEXT_LIKE_SUFFIXES:
            yield path


def check_landing_page(site_dir: Path) -> None:
    index = site_dir / "index.html"
    if not index.is_file():
        raise CheckFailed(
            f"{index} does not exist. This is the site root — without it the "
            "deploy serves nothing at '/'. Check that scripts/site_hooks/landing.py "
            "is registered under mkdocs.yml's `hooks:` and that site/index.html "
            "exists."
        )
    text = index.read_text(encoding="utf-8", errors="replace")
    if LANDING_MARKER not in text:
        raise CheckFailed(
            f"{index} exists but is missing the landing-page marker "
            f"'{LANDING_MARKER}'. The MkDocs-generated homepage has likely shadowed "
            "the bespoke site/index.html instead of being overwritten by it. Check "
            "that there is still no docs/index.md, and that the on_post_build hook "
            "in scripts/site_hooks/landing.py actually ran."
        )


def check_nojekyll(site_dir: Path) -> None:
    nojekyll = site_dir / ".nojekyll"
    if not nojekyll.is_file():
        raise CheckFailed(
            f"{nojekyll} does not exist. Without it, GitHub Pages runs Jekyll over "
            "the build output, which silently drops any path starting with an "
            "underscore — including Material's generated asset directories."
        )


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


def _is_excluded_from_snippet_scan(md_file: Path) -> bool:
    """True if `md_file` is one of the confidential surfaces mkdocs.yml's own
    `exclude_docs` already keeps out of every build (docs/03_MVP_Project_Plan.md,
    docs/reviews/**). Those files legitimately may document --8<-- syntax in
    prose (for instance, explaining this very check) without that prose being
    a real embedding risk, since the files themselves never reach a build.
    Scanning them anyway would produce a spurious allowlist failure on a
    maintainer checkout that CI — where these paths don't exist — could never
    reproduce or catch. Only surfaces that live under docs/ are relevant here;
    HUMAN_TODO.md and plan/ sit outside docs_dir and are never matched by the
    docs/**/*.md glob this feeds in the first place.
    """
    for surface in _confidential_surfaces():
        if not surface.source.is_relative_to(DOCS_DIR):
            continue
        if surface.is_dir:
            if md_file.is_relative_to(surface.source):
                return True
        elif md_file == surface.source:
            return True
    return False


def check_snippet_allowlist(_site_dir: Path) -> None:
    """Scan docs/**/*.md SOURCE (not built output) for --8<-- directives.

    mkdocs.yml configures pymdownx.snippets with base_path including the
    repo root (needed so docs/changelog.md etc. can reach the root-level
    README.md/CHANGELOG.md/GOVERNANCE.md). That same base_path means, absent
    this check, any --8<-- directive anywhere in docs/ could pull in ANY
    file in the repository — including docs/03_MVP_Project_Plan.md,
    docs/reviews/**, HUMAN_TODO.md or plan/**. This check closes that hole
    at the directive level, before a build ever gets the chance to embed
    something it shouldn't: every directive's target(s) must be exactly one
    of README.md, CHANGELOG.md or GOVERNANCE.md, or the check fails naming
    the offending file and line. The confidential surfaces themselves are
    skipped when scanning (see `_is_excluded_from_snippet_scan`) since they
    never reach a build regardless of what they say.
    """
    violations: list[str] = []
    for md_file in sorted(DOCS_DIR.rglob("*.md")):
        if _is_excluded_from_snippet_scan(md_file):
            continue
        try:
            text = md_file.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            violations.append(
                f"{md_file.relative_to(REPO_ROOT)}: could not read file to scan for "
                f"snippet directives ({exc})"
            )
            continue
        for lineno, target in _iter_snippet_directives(text):
            if target not in ALLOWED_SNIPPET_TARGETS:
                rel = md_file.relative_to(REPO_ROOT)
                violations.append(
                    f"{rel}:{lineno}: --8<-- references {target!r}, which is not in the "
                    f"allowlist {sorted(ALLOWED_SNIPPET_TARGETS)}"
                )

    if violations:
        raise CheckFailed(
            "Disallowed pymdownx.snippets directive(s) found in docs/ source:\n"
            + "\n".join(f"  {v}" for v in violations)
        )


# --- confidential-surface boundary (deliverable 1a/1b/1c) ------------------


def check_boundary(site_dir: Path) -> int:
    """Nothing from the four confidential surfaces may reach the built
    site. See the module docstring for the full two-tier design. Returns
    the number of built files that could not be read during the content
    scan (0 normally); callers surface this count rather than swallowing
    it."""
    surfaces = _confidential_surfaces()

    leaked_paths: list[tuple[str, str]] = []
    for path in site_dir.rglob("*"):
        rel = path.relative_to(site_dir).as_posix()
        lowered = rel.lower()
        for surface in surfaces:
            if surface.match_mode == "substring":
                hit = surface.path_fragment in lowered
            else:  # "segment"
                hit = surface.path_fragment in Path(lowered).parts
            if hit:
                leaked_paths.append((rel, surface.name))

    literal_markers = [
        (surface.literal_content_marker, surface.name)
        for surface in surfaces
        if surface.literal_content_marker
    ]

    skipped = 0
    fingerprints: dict[str, set[str]] = {}
    for surface in surfaces:
        if not surface.source.exists():
            continue  # Tier 2 no-op for this surface: nothing on disk to fingerprint.
        chunks, surface_skipped = _fingerprints_for(surface)
        skipped += surface_skipped
        if chunks:
            fingerprints[surface.name] = chunks

    leaked_content: list[tuple[str, str]] = []
    if literal_markers or fingerprints:
        for file in _iter_text_like_files(site_dir):
            try:
                text = file.read_text(encoding="utf-8", errors="replace")
            except OSError as exc:
                skipped += 1
                print(
                    f"WARN [check_boundary]: could not read {file} for the confidential-"
                    f"material scan ({exc}); this built file was NOT checked and may hide "
                    "a leak.",
                    file=sys.stderr,
                )
                continue

            rel = file.relative_to(site_dir).as_posix()
            lowered_text = text.lower()
            for marker, name in literal_markers:
                if marker in lowered_text:
                    leaked_content.append((rel, name))

            if fingerprints:
                normalised = _normalise(text)
                for name, chunks in fingerprints.items():
                    if any(chunk in normalised for chunk in chunks):
                        leaked_content.append((rel, name))

    if leaked_paths or leaked_content:
        lines = ["Maintainer-local internal material leaked into the published build:"]
        for rel, name in sorted(set(leaked_paths)):
            lines.append(f"  path:    {rel}  [{name}]")
        for rel, name in sorted(set(leaked_content)):
            lines.append(f"  content: {rel}  [{name}]")
        lines.append(
            "This must never ship. Check mkdocs.yml's `exclude_docs`, the "
            "snippet-directive allowlist (check_snippet_allowlist), and that no "
            "published page links or embeds any of this back in."
        )
        raise CheckFailed("\n".join(lines))

    return skipped


def check_key_pages(site_dir: Path) -> None:
    missing = [
        str(candidate)
        for slug in KEY_PAGE_SLUGS
        if not (candidate := site_dir / slug / "index.html").is_file()
    ]
    if missing:
        raise CheckFailed(
            "Expected key pages missing at their directory-URL locations:\n"
            + "\n".join(f"  {m}" for m in missing)
        )


CHECKS = (
    check_snippet_allowlist,
    check_landing_page,
    check_nojekyll,
    check_boundary,
    check_key_pages,
)


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(f"usage: {argv[0]} <site_dir>", file=sys.stderr)
        return 2

    site_dir = Path(argv[1])
    if not site_dir.is_dir():
        print(f"FAIL: {site_dir} is not a directory — run the build first.", file=sys.stderr)
        return 1

    skipped_total = 0
    for check in CHECKS:
        try:
            result = check(site_dir)
        except CheckFailed as exc:
            print(f"FAIL [{check.__name__}]: {exc}", file=sys.stderr)
            return 1
        if isinstance(result, int):
            skipped_total += result

    suffix = ""
    if skipped_total:
        suffix = (
            f" ({skipped_total} file(s) could not be read during the confidential-content "
            "scan — see WARN lines above; re-run after fixing the underlying read error "
            "before trusting this result for those files)"
        )
    print(f"OK: {site_dir} passed all publication checks.{suffix}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
