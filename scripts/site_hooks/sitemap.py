"""MkDocs hook: top up sitemap.xml with the bespoke landing page's root URL.

MkDocs' built-in sitemap.xml template (mkdocs/templates/sitemap.xml, shipped
with the mkdocs package itself) is populated from `nav`-registered
*documentation* pages — each entry comes from a `File.page` derived from
something under docs_dir. It is written by
mkdocs.commands.build._build_theme_template BEFORE any on_post_build hook
runs (this project's own hooks — landing.py, facts.py, redirects.py, this
one — all fire at on_post_build). The bespoke landing page this repo serves
at the root URL (site/index.html, spliced in by
scripts/site_hooks/landing.py) has no docs/index.md counterpart — see
landing.py's own docstring for why — so MkDocs' sitemap generator has no
Page object for "/" and the root URL is silently absent from its output.
This hook appends it directly (EDGE-001, specs/seo-custom-domains.md).

Independent of the other three hooks — no read/write dependency either way
— so registration order relative to them doesn't matter; listed last in
mkdocs.yml's `hooks:` purely by convention.

A no-op (returns immediately) when site_url is unset: MkDocs itself only
writes sitemap.xml when site_url is configured, and scripts/check_site.py's
check_sitemap_and_robots requires it always be present for this repo's own
build, so in practice this repo always has site_url set — but this hook
stays defensive rather than assuming that of every future caller.

    mkdocs.yml:
        hooks:
            - scripts/site_hooks/landing.py
            - scripts/site_hooks/facts.py
            - scripts/site_hooks/redirects.py
            - scripts/site_hooks/sitemap.py
"""
from __future__ import annotations

import gzip
import re
from pathlib import Path
from typing import Any


def on_post_build(config: dict[str, Any], **kwargs: Any) -> None:
    """Insert <url><loc>{site_url}</loc></url> into the built sitemap.xml,
    unless it is already present (e.g. a future docs/index.md makes MkDocs'
    own generator cover the root itself)."""
    site_url = config.get("site_url")
    if not site_url:
        return  # no canonical origin configured — nothing to append against

    # Normalise to a trailing slash — mirrors vouchfx_site_tools.site_url_join's
    # same tolerance (scripts/site-tools/src/vouchfx_site_tools/__init__.py)
    # and, more importantly, scripts/check_site.py's own _read_site_url_prefix,
    # which always returns mkdocs.yml's site_url with a trailing slash
    # appended if missing. Without this, an un-slashed mkdocs.yml site_url
    # (e.g. "https://vouchfx.io") would make this hook insert
    # <loc>https://vouchfx.io</loc> while check_sitemap_and_robots's EDGE-001
    # check looks for <loc>https://vouchfx.io/</loc> — a spurious CI failure
    # today, and a duplicate root entry if the slash convention ever changed
    # between builds (the idempotency check below also needs the normalised
    # form, or it would fail to recognise its own unslashed entry as already
    # present).
    if not site_url.endswith("/"):
        site_url += "/"

    site_dir = Path(config["site_dir"])
    sitemap = site_dir / "sitemap.xml"
    if not sitemap.is_file():
        # MkDocs only skips writing this template when site_url is unset
        # (already ruled out above) — a missing file here means something
        # upstream changed; fail loudly rather than publish silently
        # without a sitemap at all.
        raise RuntimeError(
            f"sitemap hook: {sitemap} does not exist, but site_url is configured "
            f"({site_url!r}) so MkDocs should have generated it — check that no "
            "other configuration disables the built-in sitemap.xml template."
        )

    # MkDocs itself writes sitemap.xml in binary mode (plain "\n", no
    # platform translation — mkdocs.utils.write_file), and the .gz
    # regeneration below encodes `updated` directly with no translation
    # either. read_bytes().decode() (no universal-newline pass, unlike
    # read_text()) plus write_text(..., newline="") (Path.read_text() has
    # no `newline` param, but write_text() does — this reads raw and writes
    # raw) keeps this hook a byte-for-byte no-op on line endings; using
    # read_text()/write_text() defaults instead would, on Windows, silently
    # translate "\n" -> "\r\n" on write, corrupting sitemap.xml relative to
    # both MkDocs' own convention and the .gz sibling — exactly the
    # mismatch check_site.py's check_sitemap_and_robots (.gz byte-equality)
    # exists to catch.
    root_loc = f"<loc>{site_url}</loc>"
    text = sitemap.read_bytes().decode("utf-8")
    if root_loc in text:
        return  # already present — don't duplicate

    entry = f"<url>\n<loc>{site_url}</loc>\n</url>\n"
    updated = re.sub(r"(?=</urlset>)", entry, text, count=1)
    if updated == text:
        raise RuntimeError(
            f"sitemap hook: could not find </urlset> to insert the root entry into "
            f"{sitemap} — has MkDocs' sitemap.xml template changed shape?"
        )
    sitemap.write_text(updated, encoding="utf-8", newline="")

    # sitemap.xml.gz is MkDocs' own gzip of the pre-hook sitemap.xml (see
    # mkdocs.commands.build._build_theme_template) — regenerate it from the
    # now-corrected content so the compressed variant a crawler might fetch
    # instead never serves a sitemap silently missing the root URL.
    gz = site_dir / "sitemap.xml.gz"
    if gz.is_file():
        with gzip.open(gz, "wb") as fh:
            fh.write(updated.encode("utf-8"))
