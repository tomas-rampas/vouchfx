"""MkDocs hook: pin Material's lazily-loaded mermaid CDN fetch.

The problem this hook exists to solve is a genuine conflict between two
requirements, not a simple bug:

  * Material for MkDocs does not bundle mermaid. Its minified theme bundle
    (assets/javascripts/bundle.*.min.js) only calls its internal
    watchScript() lazy-loader — fetching mermaid from a CDN — the moment a
    page actually contains a `<pre class="mermaid">` element (see
    mkdocs.yml's markdown_extensions comment for how a ```mermaid fence
    becomes that element). This is deliberately LAZY: a site with one
    diagram on one page never fetches mermaid on the other 107 pages.

  * The URL Material's bundle calls watchScript() with is
    "https://unpkg.com/mermaid@11/dist/mermaid.min.js" — an UNPINNED major
    version, on an origin (unpkg.com) this project does not control, baked
    into the minified bundle with no mkdocs.yml knob to override it. Issue
    #200 already established the project's standing rule against exactly
    this shape of dependency (see scripts/site-tools/src/
    vouchfx_site_tools/__init__.py's MERMAID_CDN_URL): a major-version-only
    CDN URL silently picks up every new minor/patch release on the next
    build, across every site that references it.

An earlier version of this fix (PR #320, first iteration) solved the
pinning half with an `extra_javascript` entry loading a pinned mermaid build
ahead of Material's own guard — correct in isolation, but it made
`extra_javascript` (a site-wide MkDocs mechanism with no per-page
conditional) eagerly fetch a ~1 MB script on every one of this site's pages,
not just the one with a diagram. That traded an unpinned-but-lazy dependency
for a pinned-but-eager one — a real performance and SEO regression on a site
where page-load performance and crawlability are a deliberate product
requirement, not an afterthought (see CHANGELOG.md's "Project sites migrated
to custom domains" entry for the public record of that investment).

This hook gets BOTH properties instead, by rewriting the dependency at its
actual source: the literal URL string inside Material's own built theme
bundle. Material's watchScript() call — the lazy, conditional-on-page-
content fetch itself — is left completely untouched; only the URL argument
it is called with changes. A page without a `.mermaid` element still never
calls watchScript() at all, exactly as before this hook existed.

Verified against a real build (mkdocs-material==9.7.7, the exact pin in
requirements-docs.txt): UNPKG_MERMAID_URL appears exactly once in
assets/javascripts/bundle.d7400e89.min.js. That is also how this hook was
proven correct — not asserted from reading minified source alone, but by
running `mkdocs build --strict` before and after, diffing the rewritten
bundle, then driving the built site with a real browser (PR #320's review)
to confirm: (a) a page WITH a diagram makes exactly one mermaid request, to
the pinned jsdelivr URL, zero to unpkg.com; (b) a page WITHOUT one makes
zero mermaid-related requests at all.

Registered 5th (last) in mkdocs.yml's `hooks:` list — see that file's own
ordering comment. Independent of the other four (landing/facts/redirects/
sitemap): this hook only touches MkDocs' own theme static asset, which
MkDocs copies into site_dir as part of its own build, before any
on_post_build hook runs (same timing as sitemap.xml) — it neither reads nor
writes anything the other four produce.

Fails the build loudly, on purpose, in two situations rather than silently
no-op'ing in either:

  1. The expected bundle file cannot be found (zero or more than one
     assets/javascripts/bundle*.min.js under site_dir) — Material's theme
     asset layout may have changed.
  2. UNPKG_MERMAID_URL's occurrence count in that file does not match
     EXPECTED_OCCURRENCES — the literal string this hook depends on may
     have changed shape, moved, or disappeared.

Both are exactly the failure modes a `mkdocs-material` version bump could
trigger (requirements-docs.txt pins it at 9.7.7 today): silently doing
nothing on either failure would let a future upgrade quietly resurrect an
unpinned, uncontrolled unpkg.com fetch with nobody noticing until a CDN
incident or a supply-chain concern made it someone's emergency. Re-verify
UNPKG_MERMAID_URL and EXPECTED_OCCURRENCES against the real built bundle
whenever that pin moves deliberately.

    mkdocs.yml:
        hooks:
            - scripts/site_hooks/landing.py
            - scripts/site_hooks/facts.py
            - scripts/site_hooks/redirects.py
            - scripts/site_hooks/sitemap.py
            - scripts/site_hooks/pin_mermaid.py
"""
from __future__ import annotations

from pathlib import Path
from typing import Any

# The literal URL Material's minified theme bundle passes to its internal
# watchScript() lazy-loader (recovered from
# src/templates/assets/javascripts/components/content/mermaid/index.ts via
# bundle.*.min.js.map during PR #320's review). An unpinned major version,
# on an origin this project does not control.
UNPKG_MERMAID_URL = "https://unpkg.com/mermaid@11/dist/mermaid.min.js"

# Pinned from that floating `@11` per issue #200 — see this module's own
# docstring, and scripts/site-tools/src/vouchfx_site_tools/__init__.py's
# MERMAID_CDN_URL for the sibling pin the four satellite sites already use.
# Re-pin this version deliberately, not by accident, and re-verify
# UNPKG_MERMAID_URL/EXPECTED_OCCURRENCES against a real build whenever it
# moves.
PINNED_MERMAID_URL = "https://cdn.jsdelivr.net/npm/mermaid@11.16.0/dist/mermaid.min.js"

# How many occurrences of UNPKG_MERMAID_URL this hook expects to rewrite in
# the theme bundle — verified against a real build (this module's own
# docstring), asserted rather than assumed so a future Material bundle that
# calls watchScript() a different number of times fails loudly instead of
# silently rewriting an unexpected count.
EXPECTED_OCCURRENCES = 1


def on_post_build(config: dict[str, Any], **kwargs: Any) -> None:
    """Rewrite every literal UNPKG_MERMAID_URL occurrence in Material's
    built theme bundle to PINNED_MERMAID_URL.

    Only the URL string passed to watchScript() changes. The lazy,
    conditional-on-page-content call itself is untouched — a page with no
    `.mermaid` element still triggers zero mermaid-related requests.
    """
    site_dir = Path(config["site_dir"])
    bundles = sorted(site_dir.glob("assets/javascripts/bundle*.min.js"))

    if not bundles:
        raise RuntimeError(
            "pin_mermaid hook: no assets/javascripts/bundle*.min.js found under "
            f"{site_dir} — Material's theme bundle naming/layout may have changed. "
            "Check the mkdocs-material pin in requirements-docs.txt and update this "
            "hook's glob/expectations to match the new build deliberately."
        )
    if len(bundles) > 1:
        raise RuntimeError(
            "pin_mermaid hook: expected exactly one assets/javascripts/bundle*.min.js "
            f"under {site_dir}, found {len(bundles)}: "
            + ", ".join(str(b) for b in bundles)
            + " — pick the right one deliberately rather than rewriting every match blindly."
        )
    bundle = bundles[0]

    text = bundle.read_text(encoding="utf-8")
    occurrences = text.count(UNPKG_MERMAID_URL)
    if occurrences != EXPECTED_OCCURRENCES:
        raise RuntimeError(
            f"pin_mermaid hook: expected exactly {EXPECTED_OCCURRENCES} occurrence(s) "
            f"of {UNPKG_MERMAID_URL!r} in {bundle}, found {occurrences}. This hook "
            "exists to pin mermaid to a fixed version and origin (issue #200) "
            "without losing Material's lazy, per-page loading (issue #311/#320) — "
            "failing loudly here, instead of silently rewriting zero or a "
            "surprising count, is what stops a future mkdocs-material upgrade "
            "(see the pin in requirements-docs.txt) from quietly restoring an "
            "unpinned unpkg.com fetch, or this hook from rewriting something it "
            "was never verified against."
        )

    rewritten = text.replace(UNPKG_MERMAID_URL, PINNED_MERMAID_URL)
    bundle.write_text(rewritten, encoding="utf-8")
