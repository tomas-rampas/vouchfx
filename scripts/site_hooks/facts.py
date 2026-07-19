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

mkdocs.yml must therefore list this AFTER landing.py (and before
redirects.py and sitemap.py, though those hooks have no dependency on this one running
first — see redirects.py's own docstring):

    hooks:
        - scripts/site_hooks/landing.py
        - scripts/site_hooks/facts.py
        - scripts/site_hooks/redirects.py
        - scripts/site_hooks/sitemap.py

Scope note (revised): this applies facts to every TEXT-LIKE file under
site_dir — html/js/json/xml/txt, the exact same surface
scripts/check_site.py's confidentiality and unresolved-fact checks scan
(scripts/site_hooks/_text_like.py is the single source of truth for that
surface, shared with check_site.py). This used to be html-only, mirroring
the old builder's own `out.glob("**/*.html")` pass — but that parity
argument only ever applied to files the old builder actually produced.
Material's generated search index (search/search_index.json) is not one of
those: it is built from page text *before* this post_build hook fires, so
without patching it too, a token could resolve correctly in a page's own
.html while staying stale/unresolved forever in the index. Applying
apply_facts() to JSON reuses its HTML-escaping of substituted values
verbatim (see apply_facts' own docstring) — harmless for every fact value
this project actually has today (plain version strings; none contain `&`,
`<`, `>`, or quote characters), but worth knowing if a future fact value
ever could contain one, since HTML-escaping is not JSON-escaping.

Offline/no-network operation: fetch_facts() itself already degrades
gracefully per-key on any fetcher exception (see above) — a build with no
network at all still succeeds, just entirely from
site/facts-fallback.json. But `mkdocs serve`'s watch-and-rebuild loop runs
this hook on every save, and paying for up to four live-fetch attempts
(each with its own timeout) on every keystroke-triggered rebuild is not
something a local editing session should have to wait through. Set
VOUCHFX_SITE_FACTS=offline to skip every live fetch outright and go
straight to the fallback file — this is an explicit opt-*out*, so a plain
`mkdocs build`/`mkdocs serve` with the variable unset behaves exactly as
before (live fetch attempted, matching CI's current behaviour with zero
workflow edits needed anywhere).
"""
from __future__ import annotations

import dataclasses
import os
import sys
from pathlib import Path
from typing import Any

# scripts/site_hooks/facts.py -> scripts/site_hooks -> scripts -> repo root
REPO_ROOT = Path(__file__).resolve().parents[2]
SITE_SOURCE = REPO_ROOT / "site"

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _text_like import iter_text_like_files  # noqa: E402

sys.path.insert(0, str(REPO_ROOT / "scripts" / "site-tools" / "src"))
from vouchfx_site_tools import DEFAULT_FACT_FETCHERS, SiteConfig, apply_facts, fetch_facts  # noqa: E402

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

# Opt-OUT of live fetching (default behaviour is unchanged: live fetch
# attempted, exactly matching CI today, with zero workflow edits required).
_OFFLINE_ENV_VAR = "VOUCHFX_SITE_FACTS"
_OFFLINE_ENV_VALUE = "offline"

# Every DEFAULT_FACT_FETCHERS key overridden with a fetcher that fails
# instantly — reusing fetch_facts()'s own fallback/merge/summary-printing
# logic verbatim (guaranteed identical to "every live fetch failed") rather
# than duplicating its two-line "read the fallback JSON" body here and
# risking that copy silently drifting from the real thing.
def _offline_fetcher_override() -> str:
    raise RuntimeError(f"{_OFFLINE_ENV_VAR}={_OFFLINE_ENV_VALUE}: live fetch skipped")


_OFFLINE_CONFIG = dataclasses.replace(
    _FACTS_CONFIG,
    fact_overrides={key: _offline_fetcher_override for key in DEFAULT_FACT_FETCHERS},
)


def _resolve_facts() -> dict[str, str]:
    if os.environ.get(_OFFLINE_ENV_VAR, "").strip().lower() == _OFFLINE_ENV_VALUE:
        return fetch_facts(_OFFLINE_CONFIG, SITE_SOURCE)
    return fetch_facts(_FACTS_CONFIG, SITE_SOURCE)


def on_post_build(config: dict[str, Any], **kwargs: Any) -> None:
    """Resolve {{fact:KEY}} tokens across every built text-like file."""
    site_dir = Path(config["site_dir"])

    # Read from SOURCE (site/facts-fallback.json), not site_dir's copy of it
    # — this is what the old builder does too (fetch_facts(config, site)
    # where `site` is the source site/ directory). It means this call has no
    # dependency on landing.py's copy having succeeded, only on it having
    # already run (see module docstring for why the substitution step below
    # still needs that).
    facts = _resolve_facts()

    for text_file in iter_text_like_files(site_dir):
        # errors="replace": a non-UTF-8 byte sequence in some built output
        # must degrade to a mangled character, never crash the whole build
        # (matches scripts/check_site.py's own read_text convention).
        original = text_file.read_text(encoding="utf-8", errors="replace")
        substituted = apply_facts(original, facts)
        if substituted != original:
            text_file.write_text(substituted, encoding="utf-8")

    # facts-fallback.json is build tooling, not a page — landing.py's copy of
    # site/ drags it into site_dir verbatim, but it has no business being
    # served. The old builder deletes it too: SiteConfig.delete_facts_fallback
    # defaults to True, and scripts/build_site.py never overrides it for this
    # repo (that override exists only for vouchfx-telemetry-backend, whose
    # published site has always shipped the file).
    (site_dir / "facts-fallback.json").unlink(missing_ok=True)
