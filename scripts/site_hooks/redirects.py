"""MkDocs hook: legacy-URL redirect stubs for the outgoing custom generator.

scripts/build_site.py (the generator MkDocs is replacing) published every
page at a mirror-of-the-repo .html path — docs/getting-started.md became
docs/getting-started.html, CHANGELOG.md became CHANGELOG.html, and so on.
External links and satellite sites (vouchfx-providers, vouchfx-samples,
vouchfx-telemetry-backend) point at those URLs. MkDocs' directory-URL pages
live at different paths (docs/getting-started.html -> getting-started/), so
without this hook every one of those old links would 404 the moment the
outgoing generator is retired.

on_post_build writes a small static HTML redirect stub at every legacy
path, each pointing at the page's new MkDocs URL: a meta-refresh (works
with JS disabled), a <link rel="canonical"> (correct SEO signal — search
engines re-index at the new URL instead of penalising this as duplicate
content), and a plain fallback anchor (works with a refresh timeout
disabled/blocked). All three targets are root-relative (see
_redirect_table.relative_target) rather than domain-absolute, so the same
stub content works unmodified whether the site is served under GitHub
Pages' /vouchfx/ prefix, a future custom domain, or a local `mkdocs serve`
preview.

The (legacy path -> new slug) table itself is NOT duplicated here — see
scripts/site_hooks/_redirect_table.py, the single source of truth this
hook shares with scripts/check_site.py's own verification of the same
table.

Registered third of four in mkdocs.yml's hooks: list (after landing.py and
facts.py, before sitemap.py). Not because correctness requires it — this hook's stub
content has no dependency on the other three hooks having run — but so
its collision check below (refuse to overwrite anything already present)
sees the complete, final build: everything MkDocs itself wrote, everything
landing.py copied in, and everything facts.py substituted, all before this
hook decides whether writing a stub anywhere would clobber something real.

    mkdocs.yml:
        hooks:
            - scripts/site_hooks/landing.py
            - scripts/site_hooks/facts.py
            - scripts/site_hooks/redirects.py
            - scripts/site_hooks/sitemap.py
"""
from __future__ import annotations

import sys
from pathlib import Path
from typing import Any

# scripts/site_hooks/redirects.py -> scripts/site_hooks -> scripts -> repo root
REPO_ROOT = Path(__file__).resolve().parents[2]

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _redirect_table import build_redirect_table, relative_target  # noqa: E402

STUB_TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="refresh" content="0; url={target}">
<link rel="canonical" href="{target}">
<title>Redirecting…</title>
</head>
<body>
<p>This page has moved. If you are not redirected automatically, follow <a href="{target}">this link</a>.</p>
</body>
</html>
"""


def on_post_build(config: dict[str, Any], **kwargs: Any) -> None:
    """Write a legacy-URL redirect stub for every entry in the redirect table."""
    site_dir = Path(config["site_dir"])
    site_dir_resolved = site_dir.resolve()
    table = build_redirect_table()

    # Plan every write first, validating each one, and refuse outright (no
    # partial write) if ANY problem is found.
    planned: list[tuple[Path, str]] = []
    collisions: list[str] = []
    escapes: list[str] = []
    for legacy_path, new_slug in table:
        destination = site_dir / legacy_path

        # Write containment: build_redirect_table() already rejects an
        # absolute or '..'-bearing legacy_path/new_slug at the source (see
        # _redirect_table._validate_entry) — this is the second, independent
        # layer, applied to the ACTUAL destination this hook is about to
        # write, using the exact resolve()+relative_to() try/except pattern
        # scripts/check_site.py's check_landing_outbound_links uses to
        # detect an href escaping site_dir. Belt and braces: a future
        # _redirect_table.py change that bypasses validation, or a
        # platform-specific path quirk validation didn't anticipate, still
        # can't result in a write outside site_dir.
        resolved_destination = destination.resolve()
        try:
            resolved_destination.relative_to(site_dir_resolved)
        except ValueError:
            escapes.append(f"{legacy_path!r} resolves to {resolved_destination}, outside {site_dir_resolved}")
            continue

        if destination.exists():
            collisions.append(legacy_path)
        planned.append((destination, relative_target(legacy_path, new_slug)))

    if escapes:
        listing = "\n".join(f"  {e}" for e in escapes)
        raise RuntimeError(
            "redirects hook: refusing to write — the following redirect table "
            f"entry/entries would write OUTSIDE {site_dir_resolved}:\n{listing}\n"
            "This must never happen; check scripts/site_hooks/_redirect_table.py's "
            "build_redirect_table() output and _validate_entry."
        )

    # docs/ as a stub DIRECTORY vs docs.html as a stub FILE is fine —
    # different names entirely; MkDocs itself never emits a literal
    # top-level "docs" slug, since docs_dir is a SOURCE directory name, not
    # reflected in any output path.
    if collisions:
        listing = "\n".join(f"  {c}" for c in sorted(collisions))
        raise RuntimeError(
            "redirects hook: refusing to write — the following legacy redirect "
            f"path(s) already exist under {site_dir} (written by MkDocs or an "
            f"earlier hook), so writing a stub there would silently clobber real "
            f"content instead of failing loudly:\n{listing}"
        )

    for destination, target in planned:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(STUB_TEMPLATE.format(target=target), encoding="utf-8")
