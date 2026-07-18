"""MkDocs hook: resolve {{fact:KEY}} tokens in the built site.

Replicates the SEMANTICS of the outgoing generator's fact-injection
machinery (scripts/build_site.py + scripts/site-tools/ — the
vouchfx_site_tools package shared by all four vouchfx*-site repos,
READ-ONLY here, pinned by SHA in the satellites) for the MkDocs pipeline,
rather than reinventing it. See
scripts/site-tools/src/vouchfx_site_tools/__init__.py for the canonical
implementation this hook calls into directly, imported exactly the way
scripts/build_site.py does: a sys.path insert of scripts/site-tools/src, no
pip install — the package isn't published, and satellites pin it by SHA
rather than by version.

What a {{fact:KEY}} token is (unchanged from the old builder): a handful of
numbers that change on a cadence no single repo controls — the latest
engine release, the published SDK/community-provider versions, the
community registry size. Any page may carry `{{fact:KEY}}`; fetch_facts()
resolves it at build time, starting from the last known-good values in
site/facts-fallback.json and overwriting individual keys with a live fetch
only where that succeeds (GITHUB_TOKEN, if set, only raises the GitHub API
rate limit for the engine_release fetch — every fetcher works fine without
it). Any fetcher exception — offline, rate-limited, an API shape change —
is swallowed per-key, silently keeping that key's fallback value: a stale
fact beats a broken deploy. apply_facts() leaves an UNKNOWN key's token
completely untouched in the output (this is upstream, deliberate, unchanged
behaviour — see apply_facts' own docstring) rather than failing the build.
scripts/check_site.py's check_no_unresolved_facts is the backstop that
turns "an unknown/unresolved token silently shipped" into a hard failure
for THIS site specifically — deliberately stricter than the old builder
ever was; see that check's own docstring for why.

MUST run after scripts/site_hooks/landing.py, whose on_post_build copies
site/index.html (today's only page carrying a {{fact:...}} token) into the
built output — this hook only ever substitutes into files that already
exist at MkDocs' post_build time, so if it ran first, index.html wouldn't
exist yet to substitute into and its tokens would ship unresolved.

MkDocs fires same-event hooks (on_post_build here) in the exact order
they're listed under mkdocs.yml's `hooks:` — confirmed two ways, not just
assumed:
  (1) by reading mkdocs/config/config_options.py: `Hooks.post_validation`
      iterates the hook list in order and appends each module into the
      same PluginCollection regular `plugins:` entries use
      (`plugins[name] = hook`), and PluginCollection dispatches an event to
      every registered plugin in insertion order.
  (2) empirically: two throwaway hook modules, each appending a marker to a
      shared log file from on_post_build, produced output in list order —
      tested both list orders (A-then-B and B-then-A), output matched the
      listed order both times.

mkdocs.yml must therefore list this AFTER landing.py:

    hooks:
        - scripts/site_hooks/landing.py
        - scripts/site_hooks/facts.py

Scope note: this applies facts to every *.html under site_dir — mirroring
the old builder's own `out.glob("**/*.html")` pass (see
vouchfx_site_tools.build) — not the broader text-like surface
scripts/check_site.py's confidentiality scan covers. No docs/ markdown
carries a {{fact:...}} token today; if one ever does, MkDocs renders it to
.html before this hook runs, so this same glob substitutes it there too.
The one gap: Material's own generated search index (search_index.json) is
built from page text *before* this post_build hook fires, so it could then
carry a stale/unresolved token even after the .html is fixed. That gap is
deliberately left for check_site.py's stricter, broader
(html/js/json/xml/txt) check to catch, rather than this hook reaching into
plugin-owned JSON that isn't its concern.
"""
from __future__ import annotations

import sys
from pathlib import Path
from typing import Any

# scripts/site_hooks/facts.py -> scripts/site_hooks -> scripts -> repo root
REPO_ROOT = Path(__file__).resolve().parents[2]
SITE_SOURCE = REPO_ROOT / "site"

sys.path.insert(0, str(REPO_ROOT / "scripts" / "site-tools" / "src"))
from vouchfx_site_tools import SiteConfig, apply_facts, fetch_facts  # noqa: E402

# fetch_facts() only ever reads `config.fact_overrides` off the SiteConfig it
# is given (see its source: `{**DEFAULT_FACT_FETCHERS, **config.fact_overrides}`)
# — every other field exists purely to satisfy the dataclass's required
# arguments. scripts/build_site.py's own CONFIG doesn't set fact_overrides
# either (no repo-specific fetcher for the engine repo), so leaving it at the
# default (empty dict) here is the faithful match, not a shortcut.
_FACTS_CONFIG = SiteConfig(
    root=REPO_ROOT,
    default_repo="tomas-rampas/vouchfx",
    docs=[],
    page_template="",
    portal_html="",
    meta_description_prefix="",
)


def on_post_build(config: dict[str, Any], **kwargs: Any) -> None:
    """Resolve {{fact:KEY}} tokens across every built .html page."""
    site_dir = Path(config["site_dir"])

    # Read from SOURCE (site/facts-fallback.json), not site_dir's copy of it
    # — this is what the old builder does too (fetch_facts(config, site)
    # where `site` is the source site/ directory). It means this call has no
    # dependency on landing.py's copy having succeeded, only on it having
    # already run (see module docstring for why the substitution step below
    # still needs that).
    facts = fetch_facts(_FACTS_CONFIG, SITE_SOURCE)

    for html_file in site_dir.rglob("*.html"):
        original = html_file.read_text(encoding="utf-8")
        substituted = apply_facts(original, facts)
        if substituted != original:
            html_file.write_text(substituted, encoding="utf-8")

    # facts-fallback.json is build tooling, not a page — landing.py's copy of
    # site/ drags it into site_dir verbatim, but it has no business being
    # served. The old builder deletes it too: SiteConfig.delete_facts_fallback
    # defaults to True, and scripts/build_site.py never overrides it for this
    # repo (that override exists only for vouchfx-telemetry-backend, whose
    # published site has always shipped the file).
    (site_dir / "facts-fallback.json").unlink(missing_ok=True)
