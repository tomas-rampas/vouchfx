#!/usr/bin/env python3
"""Post-build publication gate for the vouchfx MkDocs site.

Run this against the built output directory straight after `mkdocs build`,
before anything is deployed:

    python scripts/check_site.py _site

It asserts the handful of things that, if wrong, are silent until a real
user hits them in production:

  (a) every pymdownx.snippets `--8<--` directive in the docs/ SOURCE tree,
      AND inside the three allowlisted embed targets themselves (pymdownx
      expands snippets recursively), targets one of README.md,
      CHANGELOG.md, GOVERNANCE.md — see `check_snippet_allowlist`;
  (b) the bespoke landing page (site/index.html) published as the site
      root, not a Material-generated homepage;
  (b2) every internal href on every built page — not just the landing
      page — resolves CASE-SENSITIVELY against the build tree, since this
      machine's filesystem is not case-sensitive and Path.exists() would
      lie about a case-mismatched link — see
      `check_internal_links_case_sensitive`;
  (c) .nojekyll shipped, so GitHub Pages doesn't run Jekyll over the build
      and mangle underscore-prefixed paths;
  (d) nothing from five maintainer-local, confidential surfaces reached
      the build, in any path form or embedded in any built page's
      content — see `check_boundary` for the two-tier design;
  (e) no published text-like output still carries an unresolved
      {{fact:KEY}} token — see `check_no_unresolved_facts` for why this is
      deliberately stricter than the fact-injection machinery it backstops;
  (e2) site/facts-fallback.json (build tooling, not a page) never ships —
      see `check_no_facts_fallback_leak`;
  (f) every legacy .html URL the outgoing custom generator used to publish
      still resolves, via a redirect stub, to its new MkDocs URL — see
      `check_legacy_redirects`;
  (g) the key user-facing pages exist at their new directory-URL
      locations;
  (h) no built text-like output references the retired
      tomas-rampas.github.io Pages host — see `check_no_legacy_domain`;
  (i) robots.txt and sitemap.xml both exist at the build root, and every
      sitemap.xml <loc> (including the site-root entry EDGE-001 requires)
      is on the configured custom-domain origin — see
      `check_sitemap_and_robots`.

Four further checks close the REQ-007 gate extensions from
specs/seo-fleet-audit.md (the fleet-wide SEO audit):

  (j) 404.html exists at the build root — see `check_404_page`;
  (k) llms.txt exists at the build root — see `check_llms_txt`;
  (l) the landing page's og:image references an asset that actually exists
      in the build, and the URL is domain-absolute — see
      `check_og_image_asset`;
  (m) sitemap.xml lists neither 404.html nor any legacy-redirect stub page
      — see `check_sitemap_excludes_404_and_stubs`.

Exit 0: the build is safe to publish. Exit 1: a check failed; the printed
message says which one and why it matters, so a CI failure is actionable
without re-deriving the reasoning here.

--------------------------------------------------------------------------
Confidential-surface boundary — how it actually works (read this before
touching `check_boundary` or `_confidential_surfaces`)
--------------------------------------------------------------------------
This script is PUBLIC (it ships in the repository). It must never contain
any confidential *content* — no prose, no distinctive phrases, no excerpts
copy-pasted from the files it protects. It only ever hard-codes the five
surfaces' *names* (filenames/directory names such as "03_MVP_Project_Plan",
"reviews", "HUMAN_TODO", "plan", "specs") — these are structural identifiers
the project's own conventions already use in the open (CLAUDE.md, .gitignore,
commit history), not confidential material in themselves.

The boundary check runs in two tiers:

  Tier 1 — path/name matching (always active, in CI and locally alike).
  Flags any built file or directory whose path contains one of the five
  surface identifiers above. This also flags the same identifiers as
  literal, case-insensitive substrings inside any built text-like file's
  *content* — but only for the two file-shaped surfaces
  (03_MVP_Project_Plan.md, HUMAN_TODO.md), whose names are distinctive
  enough not to collide with ordinary prose. It deliberately does NOT do a
  bare content-substring check for "reviews", "plan" or "specs": all three
  are common English words and would drown the check in false positives.

  Tier 2 — content fingerprinting (active only when a confidential source
  exists on disk). On a maintainer checkout, docs/03_MVP_Project_Plan.md,
  docs/reviews/, HUMAN_TODO.md, plan/ and specs/ all exist. In CI, none of
  them do — they are untracked/gitignored — so this tier has nothing to read
  and silently does nothing there; Tier 1 still runs regardless. When active,
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

import gzip
import os
import posixpath
import re
import sys
import urllib.parse
from collections.abc import Iterator
from dataclasses import dataclass
from html.parser import HTMLParser
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
DOCS_DIR = REPO_ROOT / "docs"

# Shared, first-party helpers under scripts/site_hooks/ — not third-party
# dependencies, just internal code reuse so the two things that must never
# silently drift apart (the redirect table and the "text-like" file
# definition) have exactly one definition each, shared with the hooks that
# actually produce/consume them (scripts/site_hooks/redirects.py and
# scripts/site_hooks/facts.py respectively).
sys.path.insert(0, str(SCRIPT_DIR / "site_hooks"))
from _redirect_table import build_redirect_table, relative_target  # noqa: E402
from _text_like import TEXT_LIKE_SUFFIXES, iter_text_like_files  # noqa: E402

LANDING_MARKER = "brand__name"

# The retired GitHub Pages host every vouchfx site canonicalised on before
# the custom-domain migration (specs/seo-custom-domains.md). Matched
# case-insensitively over built output content by `check_no_legacy_domain`.
LEGACY_DOMAIN = "tomas-rampas.github.io"

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

# Tuned empirically against this repository's real content — see the module
# docstring's Tier 2 section for why 64 chars was too short (56 coincidental
# hits from shared technical vocabulary) and 512/128 gives zero.
FINGERPRINT_WINDOW = 512
FINGERPRINT_STRIDE = 128
MAX_FINGERPRINTS_PER_SURFACE = 3000

# Mirrors vouchfx_site_tools.FACT_TOKEN exactly (scripts/site-tools/src/
# vouchfx_site_tools/__init__.py) — the same pattern scripts/site_hooks/
# facts.py's apply_facts() call resolves at build time. Kept as a literal
# copy, not an import, so this check has no runtime dependency on that
# package to do its job of catching the package's own output going missing.
FACT_TOKEN = re.compile(r"\{\{fact:([A-Za-z0-9_]+)\}\}")


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
        ConfidentialSurface(
            name="approved feature specs",
            source=REPO_ROOT / "specs",
            is_dir=True,
            path_fragment="specs",
            match_mode="segment",
            # None, matching "reviews"/"plan" above: "specs" is a generic
            # English word too common in ordinary prose (this project's own
            # docs talk about "the DSL spec", "JSON Schema spec", etc.) for a
            # bare content-substring check not to drown in false positives —
            # Tier 2 fingerprinting (whole-paragraph verbatim matching) is
            # the safe way to catch an actual spec-content leak.
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


def _scan_file_for_disallowed_directives(path: Path) -> list[str]:
    """Read `path` and return one violation message per --8<-- directive
    whose target is not in ALLOWED_SNIPPET_TARGETS (empty list if clean or
    the file has no directives)."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        return [
            f"{path.relative_to(REPO_ROOT)}: could not read file to scan for "
            f"snippet directives ({exc})"
        ]
    found: list[str] = []
    for lineno, target in _iter_snippet_directives(text):
        if target not in ALLOWED_SNIPPET_TARGETS:
            rel = path.relative_to(REPO_ROOT)
            found.append(
                f"{rel}:{lineno}: --8<-- references {target!r}, which is not in the "
                f"allowlist {sorted(ALLOWED_SNIPPET_TARGETS)}"
            )
    return found


def check_snippet_allowlist(_site_dir: Path) -> None:
    """Scan docs/**/*.md SOURCE (not built output), PLUS the three
    allowlisted embed targets themselves, for --8<-- directives.

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
    skipped when scanning docs/ (see `_is_excluded_from_snippet_scan`)
    since they never reach a build regardless of what they say.

    NESTED-DIRECTIVE HARDENING: pymdownx.snippets expands snippets
    recursively — confirmed by reading pymdownx/snippets.py's
    parse_snippets, which calls itself on the freshly-read content of every
    snippet it resolves. That means a --8<-- directive placed INSIDE
    README.md, CHANGELOG.md or GOVERNANCE.md would also get expanded when
    docs/project-readme.md, docs/changelog.md or docs/governance.md embed
    that file — reaching wherever IT points, entirely bypassing a
    docs/-only scan. So this check also scans the three allowlisted root
    files themselves for --8<-- directives, with the exact same allowlist
    applied (today, that means they must contain none at all — nothing
    they could legitimately point at is itself allowlisted).
    """
    violations: list[str] = []

    for md_file in sorted(DOCS_DIR.rglob("*.md")):
        if _is_excluded_from_snippet_scan(md_file):
            continue
        violations.extend(_scan_file_for_disallowed_directives(md_file))

    for target_name in sorted(ALLOWED_SNIPPET_TARGETS):
        target_path = REPO_ROOT / target_name
        if target_path.is_file():
            violations.extend(_scan_file_for_disallowed_directives(target_path))

    if violations:
        raise CheckFailed(
            "Disallowed pymdownx.snippets directive(s) found:\n"
            + "\n".join(f"  {v}" for v in violations)
        )


# --- confidential-surface boundary (deliverable 1a/1b/1c) ------------------


def check_boundary(site_dir: Path) -> int:
    """Nothing from the five confidential surfaces may reach the built
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
        for file in iter_text_like_files(site_dir):
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


# --- unresolved {{fact:KEY}} token check (facts.py's backstop) -------------


def check_no_unresolved_facts(site_dir: Path) -> int:
    """No published text-like output may contain an unresolved {{fact:KEY}}
    token.

    scripts/site_hooks/facts.py substitutes these at build time using the
    exact same machinery scripts/build_site.py has always used
    (vouchfx_site_tools.apply_facts). That upstream function's own
    behaviour for an UNKNOWN key is to leave the token untouched in the
    output rather than fail the build — a deliberate, unchanged design
    choice there (a typo'd or retired fact key degrading to visible
    literal text beats a broken deploy).

    This check is DELIBERATELY STRICTER than that upstream behaviour, and
    that is a documented, intentional deviation, not an oversight: for
    THIS site, any {{fact:KEY}} token surviving to the published output is
    always a regression worth failing loudly for — either facts.py didn't
    run at all, ran before landing.py (see facts.py's docstring on hook
    ordering), or someone referenced a key that was never a real fact.
    There is no equivalent of the old builder's "ship it as literal text
    and move on" tolerance here; this check exists specifically so that
    tolerance can never happen silently on this pipeline.

    Scans the same text-like surface as `check_boundary`
    (html/js/json/xml/txt) — the exact surface facts.py itself now patches
    (it was originally html-only, mirroring the old builder, and was
    widened so Material's search_index.json — serialised from page text
    before the hook fires — gets patched too; see facts.py's "Scope note
    (revised)"). With application and verification covering the same
    surface, a hit here always means a real regression: facts.py did not
    run, ran before landing.py, or the key was never a real fact. Returns
    the number of built files that could not be read (0 normally); callers
    surface this count rather than swallowing it, matching
    `check_boundary`'s convention.
    """
    hits: list[tuple[str, str]] = []
    skipped = 0
    for file in iter_text_like_files(site_dir):
        try:
            text = file.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            skipped += 1
            print(
                f"WARN [check_no_unresolved_facts]: could not read {file} ({exc}); "
                "this built file was NOT checked and may hide an unresolved token.",
                file=sys.stderr,
            )
            continue
        rel = file.relative_to(site_dir).as_posix()
        for match in FACT_TOKEN.finditer(text):
            hits.append((rel, match.group(0)))

    if hits:
        lines = ["Unresolved {{fact:KEY}} token(s) survived to the published build:"]
        for rel, token in sorted(set(hits)):
            lines.append(f"  {rel}: {token}")
        lines.append(
            "This must never ship. Check that scripts/site_hooks/facts.py is "
            "registered AFTER scripts/site_hooks/landing.py in mkdocs.yml's "
            "`hooks:` list; that the key is spelled correctly and is one facts.py "
            "actually resolves (see vouchfx_site_tools.DEFAULT_FACT_FETCHERS and "
            "site/facts-fallback.json); and — if the token lives in "
            "search_index.json rather than the page's own .html — that the "
            "surrounding .html has already been fixed (see facts.py's docstring, "
            "'Scope note')."
        )
        raise CheckFailed("\n".join(lines))

    return skipped


def check_no_facts_fallback_leak(site_dir: Path) -> None:
    """site/facts-fallback.json must NOT appear in the built output.

    It is build tooling, not a page: landing.py's copy of site/ drags it
    into the build verbatim, and facts.py deletes it again once it has
    read the fallback values from it (matching the old builder's
    SiteConfig.delete_facts_fallback default of True — see facts.py's own
    docstring). If it turns up here, that deletion regressed — either
    facts.py isn't registered, isn't running, or its unlink(missing_ok=True)
    call was removed — and a build-tooling file with no business being
    served would ship.
    """
    leaked = site_dir / "facts-fallback.json"
    if leaked.is_file():
        raise CheckFailed(
            f"{leaked} exists in the built output. scripts/site_hooks/facts.py is "
            "supposed to delete it after reading the fallback values (it is build "
            "tooling, not a page) — check that hook is registered in mkdocs.yml's "
            "`hooks:` list, ran, and its final unlink(missing_ok=True) call executed."
        )


# --- legacy-URL redirect stubs (todo 1 / check 2a) --------------------------


def check_legacy_redirects(site_dir: Path) -> None:
    """Every legacy .html path from the outgoing custom generator's
    mirror-layout must still resolve, via a redirect stub, to its new
    MkDocs URL.

    Uses build_redirect_table()/relative_target() from
    scripts/site_hooks/_redirect_table.py — the exact same table
    scripts/site_hooks/redirects.py writes stubs from, so this check can
    never silently drift from what that hook actually produces. For each
    (legacy_path, new_slug) pair: the stub file must exist, its meta-refresh
    must reference the expected root-relative target, its canonical must be
    the expected DOMAIN-ABSOLUTE target (specs/seo-fleet-audit.md, D-04 —
    unlike the meta-refresh/fallback anchor, the canonical is deliberately
    not root-relative), and that target must resolve to a real page that
    actually exists in this same build (not just a stub pointing at a stub,
    or at a slug that got renamed/removed).
    """
    missing: list[str] = []
    bad_content: list[str] = []
    dangling: list[str] = []
    site_url_prefix, _ = _read_site_url_prefix()

    for legacy_path, new_slug in build_redirect_table():
        stub = site_dir / legacy_path
        if not stub.is_file():
            missing.append(legacy_path)
            continue

        expected_target = relative_target(legacy_path, new_slug)
        expected_canonical = f"{site_url_prefix}{new_slug}/"
        text = stub.read_text(encoding="utf-8", errors="replace")
        has_refresh = f'url={expected_target}"' in text
        has_canonical = f'href="{expected_canonical}"' in text and "canonical" in text
        if not (has_refresh and has_canonical):
            bad_content.append(
                f"{legacy_path} does not reference the expected refresh target "
                f"{expected_target!r} and/or the expected absolute canonical {expected_canonical!r}"
            )
            continue

        new_page = site_dir / new_slug / "index.html"
        if not new_page.is_file():
            dangling.append(f"{legacy_path} points at {new_slug}/, but {new_page} does not exist")

    if missing or bad_content or dangling:
        lines = ["Legacy-URL redirect stub problem(s):"]
        for m in missing:
            lines.append(f"  MISSING stub: {m}")
        for b in bad_content:
            lines.append(f"  WRONG TARGET: {b}")
        for d in dangling:
            lines.append(f"  DANGLING: {d}")
        lines.append(
            "Every path the outgoing custom generator used to publish must keep "
            "resolving — via scripts/site_hooks/redirects.py — or an external link "
            "or satellite site pointing at the old URL breaks silently."
        )
        raise CheckFailed("\n".join(lines))


# --- site-wide, case-sensitive internal-link check --------------------------
#
# Supersedes an earlier, narrower version of this check that only scanned
# the landing page (site/index.html) and used Path.exists()/is_file() for
# resolution. Folded into one, broader check rather than kept alongside it:
# this scans every built page (a strict superset of index.html alone), and
# case-sensitive resolution is itself a strict superset of plain existence
# checking — anything Path.is_file() would have caught, this still catches,
# plus a case mismatch it would have missed entirely on this machine. So
# folding is a consolidation, not a reduction in what's asserted.


class _HrefCollector(HTMLParser):
    """Collects every `href` attribute value from any tag."""

    def __init__(self) -> None:
        super().__init__()
        self.hrefs: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        for name, value in attrs:
            if name == "href" and value:
                self.hrefs.append(value)


def _read_site_url_prefix() -> tuple[str, str]:
    """Extract mkdocs.yml's site_url and return (absolute_prefix,
    path_prefix) — e.g. ("https://vouchfx.io/", "/"). A light regex, not a
    full YAML parse, keeping this script stdlib-only, so absolute- and
    root-relative-internal-link detection below can never silently drift
    from the real config. Falls back to the known current value if
    mkdocs.yml is unreadable or the key is missing, degrading gracefully to
    document-relative-only detection rather than crashing.

    Both prefixes matter, for two DIFFERENT real cases seen in this
    project's own build: a page's own self-referential canonical-style
    link is site_url-ABSOLUTE (e.g.
    "https://vouchfx.io/getting-started/"); MkDocs' 404.html — generated
    with no "current page" to compute a relative path from — falls back to
    a ROOT-RELATIVE path prefixed with just the site's mount path (e.g.
    "/getting-started/" now that the custom domain carries no path prefix
    of its own — see specs/seo-custom-domains.md), not the full absolute
    URL.
    """
    fallback_absolute = "https://vouchfx.io/"
    fallback_path = "/"
    try:
        text = (REPO_ROOT / "mkdocs.yml").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return fallback_absolute, fallback_path
    match = re.search(r"^site_url:\s*(\S+)\s*$", text, re.MULTILINE)
    if not match:
        return fallback_absolute, fallback_path
    value = match.group(1).strip("'\"")
    absolute = value if value.endswith("/") else value + "/"
    path_prefix = urllib.parse.urlparse(absolute).path or "/"
    if not path_prefix.endswith("/"):
        path_prefix += "/"
    return absolute, path_prefix


def _is_checkable_internal_href(href: str, site_url_prefix: str, site_url_path_prefix: str) -> bool:
    """True for an href that is site_url-absolute (a self-referential
    absolute link back into this same site — real examples exist: every
    page's own canonical-style self-link), root-relative under the site's
    own mount path (e.g. "/vouchfx/getting-started/" — what MkDocs emits
    for pages with no relative-path context, like 404.html), or plain
    document-relative (no scheme, no leading slash). False for any other
    scheme (http:, https: to a different host, mailto:, tel:, data:, ...),
    a protocol-relative //host/... URL, a root-relative path OUTSIDE the
    site's own mount path (points at a different site entirely — not this
    build's concern to verify), or a bare #fragment.
    """
    if href.startswith(site_url_prefix):
        return True
    if href.startswith("//"):
        return False  # protocol-relative: always some other host
    if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", href):  # any scheme: http:, https:, mailto:, tel:, data:, ...
        return False
    if href.startswith("/"):
        return href.startswith(site_url_path_prefix)
    if href.startswith("#"):
        return False
    path = href.split("#", 1)[0].split("?", 1)[0]
    return bool(path)


def _case_sensitive_resolve(
    base: Path, posix_relative_path: str, listdir_cache: dict[Path, set[str]]
) -> Path | None:
    """Walk `posix_relative_path` (forward-slash, already normalised — no
    leading '/', no '..' segments) from `base`, requiring an EXACT,
    case-sensitive match at every segment via a real directory listing.

    Path.exists()/is_file() cannot be trusted for this: this machine's
    filesystem is case-insensitive (NTFS/APFS default), so a lowercase
    variant of a real mixed-case path satisfies Path.exists() while it
    would 404 on GitHub Pages' case-sensitive filesystem — exactly the bug
    class behind a prior review round's README fix (five lowercase
    design-doc URLs that "worked" locally). `listdir_cache` memoises
    directory listings within one check run, since many hrefs share the
    same ancestor directories.
    """
    current = base
    if posix_relative_path in ("", "."):
        return current
    for part in posix_relative_path.split("/"):
        if not part:
            continue
        if current not in listdir_cache:
            try:
                listdir_cache[current] = set(os.listdir(current))
            except OSError:
                listdir_cache[current] = set()
        if part not in listdir_cache[current]:
            return None
        current = current / part
    return current


def _case_insensitive_exists(
    base: Path, posix_relative_path: str, listdir_cache: dict[Path, set[str]]
) -> bool:
    """Best-effort, case-INSENSITIVE existence check — used only to
    classify a case-sensitive resolution failure (see
    `_case_sensitive_resolve`) as a TRUE case mismatch (exists under some
    OTHER casing) versus genuinely missing (doesn't exist under any casing
    at all). This check's hard failures stay scoped to the case-sensitivity
    bug class it targets — "closes the class of bug behind the README
    404s" — rather than becoming a general internal-link checker: a
    genuinely broken/missing link is a different, pre-existing content
    issue this check still reports (see
    `check_internal_links_case_sensitive`) but does not fail the gate on,
    since fixing arbitrary broken content links is outside this task's
    (and this script's editable-file) scope.
    """
    current = base
    if posix_relative_path in ("", "."):
        return True
    for part in posix_relative_path.split("/"):
        if not part:
            continue
        if current not in listdir_cache:
            try:
                listdir_cache[current] = set(os.listdir(current))
            except OSError:
                listdir_cache[current] = set()
        lowered = part.lower()
        match = next((entry for entry in listdir_cache[current] if entry.lower() == lowered), None)
        if match is None:
            return False
        current = current / match
    return True


def _resolve_internal_href(
    href: str,
    page_file: Path,
    site_dir: Path,
    site_url_prefix: str,
    site_url_path_prefix: str,
    listdir_cache: dict[Path, set[str]],
) -> tuple[str, Path | None]:
    """Resolve `href` (found on `page_file`) against `site_dir`,
    case-sensitively. Returns (status, path): status is "ok", "escapes",
    "case_mismatch" (the target exists, but only under different casing —
    THIS check's actual target bug class) or "missing" (doesn't exist
    under any casing — a different, pre-existing content-link problem this
    check reports but does not fail on; see `_case_insensitive_exists`).
    `path` is the furthest point reached, for error messages.
    """
    path_part = href.split("#", 1)[0].split("?", 1)[0]

    if path_part.startswith(site_url_prefix):
        remainder = path_part[len(site_url_prefix) :]
        base_posix = ""
    elif path_part.startswith(site_url_path_prefix):
        # Root-relative under the site's own mount path (e.g. what MkDocs
        # emits in 404.html: "/vouchfx/getting-started/") — strip just the
        # mount path, not the whole leading slash, so this resolves the
        # same as the site_url-absolute form above.
        remainder = path_part[len(site_url_path_prefix) :]
        base_posix = ""
    else:
        remainder = path_part
        page_dir_rel = page_file.parent.relative_to(site_dir).as_posix()
        base_posix = "" if page_dir_rel == "." else page_dir_rel

    joined = posixpath.normpath(posixpath.join(base_posix, remainder)) if remainder else (base_posix or ".")

    if joined == ".." or joined.startswith("../") or posixpath.isabs(joined):
        return "escapes", None

    resolved = _case_sensitive_resolve(site_dir, joined, listdir_cache)

    if resolved is None:
        if _case_insensitive_exists(site_dir, joined, listdir_cache):
            return "case_mismatch", None
        return "missing", None

    if resolved.is_dir():
        # Directory-URL style hrefs (e.g. "../getting-started/") resolve to
        # a directory — MkDocs' use_directory_urls convention means the
        # real page is that directory's index.html.
        index_file = _case_sensitive_resolve(resolved, "index.html", listdir_cache)
        if index_file is not None and index_file.is_file():
            return "ok", index_file
        if _case_insensitive_exists(resolved, "index.html", listdir_cache):
            return "case_mismatch", None
        return "missing", resolved

    if resolved.is_file():
        return "ok", resolved

    return "missing", resolved


def check_internal_links_case_sensitive(site_dir: Path) -> None:
    """Every internal href (site_url-absolute, root-relative, or
    document-relative) on every built HTML page must resolve
    CASE-SENSITIVELY against the actual build tree.

    Why case-sensitively, concretely: this filesystem is case-insensitive,
    so Path.exists()/Path.is_file() would report a lowercase variant of a
    real mixed-case path (e.g.
    "01_technical_architecture_and_engineering_blueprint/" vs the real
    "01_Technical_Architecture_and_Engineering_Blueprint/") as PRESENT — it
    silently lies. GitHub Pages serves from a case-sensitive filesystem, so
    the same link 404s there while looking completely fine in every local
    build+check. See `_case_sensitive_resolve` for how this walks each
    href's target one real directory listing at a time instead.

    SCOPE, DELIBERATELY: this check's hard FAILURE is reserved for true
    case mismatches — a target that exists, but only under different
    casing (see `_resolve_internal_href`/`_case_insensitive_exists` for how
    that's distinguished from the alternative below). A target that
    doesn't exist under ANY casing at all is a different, pre-existing bug
    class — a genuinely broken/missing content link, unrelated to case —
    and is only reported (printed) here, not failed on: fixing arbitrary
    broken links in already-published markdown is outside what this check
    (and this script's own editable-file scope) can respond to.
    """
    site_url_prefix, site_url_path_prefix = _read_site_url_prefix()
    listdir_cache: dict[Path, set[str]] = {}

    broken: list[str] = []
    missing_not_case: list[str] = []
    for page in sorted(site_dir.rglob("*.html")):
        try:
            text = page.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            broken.append(f"{page.relative_to(site_dir).as_posix()}: could not read file ({exc})")
            continue

        collector = _HrefCollector()
        collector.feed(text)
        rel_page = page.relative_to(site_dir).as_posix()

        for href in sorted(set(collector.hrefs)):
            if not _is_checkable_internal_href(href, site_url_prefix, site_url_path_prefix):
                continue
            status, resolved = _resolve_internal_href(
                href, page, site_dir, site_url_prefix, site_url_path_prefix, listdir_cache
            )
            if status == "escapes":
                broken.append(f"{rel_page}: href={href!r} escapes site_dir")
            elif status == "case_mismatch":
                broken.append(f"{rel_page}: href={href!r} exists only under a different casing")
            elif status == "missing":
                where = f" (found as far as {resolved})" if resolved is not None else ""
                missing_not_case.append(f"{rel_page}: href={href!r} does not exist under any casing{where}")

    if missing_not_case:
        print(
            "WARN [check_internal_links_case_sensitive]: found internal href(s) that "
            "do not exist under ANY casing (a different, pre-existing content-link "
            "problem, not a case mismatch — reported here but not failing the gate):",
            file=sys.stderr,
        )
        for m in missing_not_case:
            print(f"  {m}", file=sys.stderr)

    if broken:
        lines = ["Internal link(s) fail case-sensitive resolution against the build tree:"]
        for b in broken:
            lines.append(f"  {b}")
        lines.append(
            "This machine's filesystem is case-insensitive, so a case-mismatched "
            "internal link works in every local build+check and 404s on GitHub "
            "Pages, which is case-sensitive. Fix the href to match the real, "
            "on-disk slug's exact casing."
        )
        raise CheckFailed("\n".join(lines))


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


# --- custom-domain migration hardening (REQ-007, EDGE-001/002) -------------


def check_no_legacy_domain(site_dir: Path) -> int:
    """No built text-like output may reference the retired
    tomas-rampas.github.io Pages host (REQ-007a).

    The five vouchfx sites migrated to custom domains (vouchfx.io and its
    four subdomains) — every self-referential link, canonical tag, JSON-LD
    URL, fact-injected ecosystem link and legacy-redirect stub must point at
    a custom domain from here on. A leftover github.io reference (a stale
    doc-card href, an unmigrated canonical, a fact that resolved before the
    satellites bumped their site-tools pin) splits link equity across two
    hosts and confuses search engines about which is authoritative. Scans
    the same text-like surface as `check_boundary`/`check_no_unresolved_facts`
    (html/js/json/xml/txt, including robots.txt/llms.txt — see
    scripts/site_hooks/_text_like.py). Returns the number of built files
    that could not be read (0 normally); callers surface this count rather
    than swallowing it, matching this module's other content-scan checks.
    """
    hits: list[str] = []
    skipped = 0
    for file in iter_text_like_files(site_dir):
        try:
            text = file.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            skipped += 1
            print(
                f"WARN [check_no_legacy_domain]: could not read {file} ({exc}); "
                "this built file was NOT checked and may hide a stale domain reference.",
                file=sys.stderr,
            )
            continue
        # Case-insensitive: a case-varied host (e.g. "Tomas-Rampas.GitHub.io")
        # still resolves to the same retired Pages host (DNS is
        # case-insensitive), and would otherwise sail through a bare
        # case-sensitive substring match. LEGACY_DOMAIN is already
        # lower-case, matching check_boundary's own lowered comparisons.
        count = text.lower().count(LEGACY_DOMAIN)
        if count:
            rel = file.relative_to(site_dir).as_posix()
            hits.append(f"{rel}: {count} occurrence(s)")

    if hits:
        lines = [f"Built output still references the retired {LEGACY_DOMAIN} host:"]
        for h in sorted(hits):
            lines.append(f"  {h}")
        lines.append(
            "Every surface migrated to a custom domain (vouchfx.io and its four "
            "subdomains) — fix the source (docs markdown, site/ templates, "
            "scripts/build_site.py's PORTAL/footer, or a stale fact) so the built "
            "output never mentions the old host."
        )
        raise CheckFailed("\n".join(lines))

    return skipped


def check_sitemap_and_robots(site_dir: Path) -> None:
    """robots.txt, sitemap.xml and sitemap.xml.gz MUST all exist at the
    build root; every sitemap.xml `<loc>` MUST be on the configured
    site_url origin (including the site-root entry itself — REQ-007b/c,
    EDGE-001); and sitemap.xml.gz MUST gunzip to exactly sitemap.xml's
    bytes.

    site_url is read the same way `check_internal_links_case_sensitive`
    reads it (`_read_site_url_prefix`, a light regex over mkdocs.yml — this
    stays stdlib-only and can never silently drift from the real config).
    robots.txt is a static site/ companion copied by
    scripts/site_hooks/landing.py; sitemap.xml is MkDocs' own built-in
    template (populated whenever mkdocs.yml's site_url is set), topped up
    with the missing site-root entry by scripts/site_hooks/sitemap.py — see
    that hook's docstring for why MkDocs' own generator can't produce the
    root entry unaided (there is no docs/index.md for the bespoke landing
    page). MkDocs also writes a gzip companion, sitemap.xml.gz, at the same
    build step — Google (and other crawlers) may fetch either — so
    sitemap.xml.gz must stay byte-for-byte the same document as sitemap.xml
    once decompressed, or a crawler reading the stale/uncorrected .gz could
    silently miss the EDGE-001 root entry while sitemap.xml itself passes
    every check above. scripts/site_hooks/sitemap.py regenerates the .gz
    alongside its .xml edit for exactly this reason; this check is the
    backstop that catches a future hook or build-step change that edits one
    without the other.
    """
    robots = site_dir / "robots.txt"
    sitemap = site_dir / "sitemap.xml"
    sitemap_gz = site_dir / "sitemap.xml.gz"
    missing = [str(p) for p in (robots, sitemap, sitemap_gz) if not p.is_file()]
    if missing:
        raise CheckFailed(
            "Missing required SEO surface(s) at the build root:\n"
            + "\n".join(f"  {m}" for m in missing)
            + "\nrobots.txt ships as a site/ companion; sitemap.xml/sitemap.xml.gz are "
            "written by MkDocs itself once mkdocs.yml's site_url is set — check all "
            "three are present on disk and that scripts/site_hooks/landing.py ran."
        )

    site_url_prefix, _ = _read_site_url_prefix()
    sitemap_bytes = sitemap.read_bytes()
    text = sitemap_bytes.decode("utf-8", errors="replace")
    locs = re.findall(r"<loc>(.*?)</loc>", text)
    off_origin = sorted({loc for loc in locs if not loc.startswith(site_url_prefix)})
    if off_origin:
        raise CheckFailed(
            f"{sitemap} contains <loc> entries not on {site_url_prefix!r}:\n"
            + "\n".join(f"  {loc}" for loc in off_origin)
        )

    if f"<loc>{site_url_prefix}</loc>" not in text:
        raise CheckFailed(
            f"{sitemap} is missing the site-root entry <loc>{site_url_prefix}</loc> "
            "(EDGE-001) — MkDocs' own sitemap template only covers docs/-sourced "
            "pages, and there is no docs/index.md for the bespoke landing page, so "
            "scripts/site_hooks/sitemap.py must append it; check that hook is "
            "registered in mkdocs.yml's `hooks:` list and ran."
        )

    try:
        gunzipped = gzip.decompress(sitemap_gz.read_bytes())
    except OSError as exc:
        raise CheckFailed(
            f"{sitemap_gz} could not be decompressed ({exc}) — it should be a valid "
            f"gzip of {sitemap}."
        ) from exc
    if gunzipped != sitemap_bytes:
        raise CheckFailed(
            f"{sitemap_gz} does not gunzip to the same bytes as {sitemap}. Google (and "
            "other crawlers) may fetch the .gz variant directly — if it's stale it can "
            "silently miss the EDGE-001 root entry, or advertise off-origin <loc> "
            "entries, even though sitemap.xml itself passes every check above. Check "
            "that scripts/site_hooks/sitemap.py regenerated sitemap.xml.gz when it "
            "edited sitemap.xml (see that hook's on_post_build)."
        )


# --- REQ-007 gate extensions (specs/seo-fleet-audit.md) --------------------
#
# Four additional publication checks the fleet-wide SEO audit's fix work
# order requires of the engine's own gate: a missing branded 404.html or
# llms.txt, a landing-page og:image that doesn't actually exist in the
# build, and a sitemap that leaks a non-indexable page (404.html or a
# legacy-redirect stub). Each is deliberately narrow — it fails loudly on
# exactly the regression it targets and otherwise defers to the checks
# already covering the surface in more depth (check_landing_page,
# check_sitemap_and_robots, check_legacy_redirects).

OG_IMAGE_RE = re.compile(r'<meta\s+property="og:image"\s+content="([^"]+)"')


def check_404_page(site_dir: Path) -> None:
    """_site/404.html must exist (REQ-007a). GitHub Pages serves exactly
    this path automatically for any unmatched URL (EDGE-006); see
    overrides/404.html for the branded, noindexed template this check
    backstops."""
    page = site_dir / "404.html"
    if not page.is_file():
        raise CheckFailed(
            f"{page} does not exist. GitHub Pages relies on this exact path to serve "
            "a branded 404 for any unmatched URL (specs/seo-fleet-audit.md, REQ-006). "
            "Check that mkdocs.yml's theme.custom_dir is set to 'overrides' and that "
            "overrides/404.html exists and extends Material's base.html."
        )


def check_llms_txt(site_dir: Path) -> None:
    """_site/llms.txt must exist (REQ-007b) — the llms.txt convention this
    site's site/llms.txt companion satisfies (specs/seo-fleet-audit.md,
    REQ-005 parity)."""
    page = site_dir / "llms.txt"
    if not page.is_file():
        raise CheckFailed(
            f"{page} does not exist. Check that site/llms.txt is present in the "
            "repository and that scripts/site_hooks/landing.py's site/ copytree ran."
        )


def check_og_image_asset(site_dir: Path) -> None:
    """The landing page's og:image MUST reference an asset that actually
    exists in the built output (REQ-007c) — a dangling og:image URL is
    silently useless to every social-share scraper. Also asserts the URL is
    domain-absolute (EDGE-004): a relative og:image is not reliably
    resolved by scrapers."""
    index = site_dir / "index.html"
    if not index.is_file():
        return  # check_landing_page already fails loudly for this case
    text = index.read_text(encoding="utf-8", errors="replace")
    match = OG_IMAGE_RE.search(text)
    if not match:
        raise CheckFailed(
            f'{index} has no <meta property="og:image" content="..."> tag '
            "(specs/seo-fleet-audit.md, REQ-004) — the landing page must reference a "
            "branded social-share image."
        )
    og_image_url = match.group(1)
    site_url_prefix, _ = _read_site_url_prefix()
    if not og_image_url.startswith(site_url_prefix):
        raise CheckFailed(
            f"{index}'s og:image {og_image_url!r} is not absolute on {site_url_prefix!r} "
            "(EDGE-004) — social scrapers do not reliably resolve a relative og:image URL."
        )
    relative = og_image_url[len(site_url_prefix) :]
    asset = site_dir / relative
    if not asset.is_file():
        raise CheckFailed(
            f"{index} references og:image {og_image_url!r}, but {asset} does not exist "
            "in the built output. Check that the asset is committed under site/ and that "
            "the URL in site/index.html matches its actual filename."
        )


def check_sitemap_excludes_404_and_stubs(site_dir: Path) -> None:
    """sitemap.xml MUST NOT list 404.html or any legacy-redirect stub page
    (REQ-007d) — neither is an indexable page (EDGE-003 for 404.html;
    stubs exist purely to 301-equivalent legacy URLs onward, per D-04's own
    canonical-vs-target distinction, and were never meant to be crawled as
    destinations in their own right)."""
    sitemap = site_dir / "sitemap.xml"
    if not sitemap.is_file():
        return  # check_sitemap_and_robots already fails loudly for this case
    text = sitemap.read_text(encoding="utf-8", errors="replace")
    locs = set(re.findall(r"<loc>(.*?)</loc>", text))
    site_url_prefix, _ = _read_site_url_prefix()

    disallowed = {f"{site_url_prefix}404.html"}
    disallowed.update(f"{site_url_prefix}{legacy_path}" for legacy_path, _new_slug in build_redirect_table())

    present = sorted(locs & disallowed)
    if present:
        raise CheckFailed(
            f"{sitemap} lists {len(present)} non-indexable page(s) that must never "
            "appear in a sitemap (specs/seo-fleet-audit.md, REQ-007d):\n"
            + "\n".join(f"  {p}" for p in present)
            + "\n404.html and every legacy-URL redirect stub are excluded from indexing "
            "by design — check scripts/site_hooks/sitemap.py and MkDocs' own sitemap "
            "generation for a regression."
        )


CHECKS = (
    check_snippet_allowlist,
    check_landing_page,
    check_internal_links_case_sensitive,
    check_nojekyll,
    check_boundary,
    check_no_unresolved_facts,
    check_no_facts_fallback_leak,
    check_legacy_redirects,
    check_key_pages,
    check_no_legacy_domain,
    check_sitemap_and_robots,
    check_404_page,
    check_llms_txt,
    check_og_image_asset,
    check_sitemap_excludes_404_and_stubs,
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
            f" ({skipped_total} file(s) could not be read during a content scan "
            "(check_boundary and/or check_no_unresolved_facts) — see WARN lines above; "
            "re-run after fixing the underlying read error before trusting this result "
            "for those files)"
        )
    print(f"OK: {site_dir} passed all publication checks.{suffix}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
