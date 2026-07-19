"""Shared static-site generator for the vouchfx ecosystem's GitHub Pages sites.

Four sibling repositories (vouchfx, vouchfx-providers, vouchfx-samples,
vouchfx-telemetry-backend) each publish a GitHub Pages site that is the same
shape: a static landing page in `site/` plus the repository's own markdown
rendered to matching styled HTML on every push. This module is the machinery
those four `scripts/build_site.py` wrappers share; each wrapper supplies its
own `SiteConfig` (doc set, page/portal templates, publication scoping) and
calls `build(config, out)`. See vouchfx issue #200.

Public API: `SiteConfig` + `build`.
"""
from __future__ import annotations

import html
import json
import os
import posixpath
import re
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import markdown
from markdown.extensions.codehilite import CodeHiliteExtension
from markdown.extensions.toc import TocExtension
from pygments.formatters import HtmlFormatter
import urllib.request

__all__ = ["SiteConfig", "build"]

# ---------------------------------------------------------------------------
# Configuration surface
# ---------------------------------------------------------------------------


@dataclass
class SiteConfig:
    """Everything a repository-specific `build_site.py` wrapper supplies.

    root: the repository root (the wrapper computes this as
        ``Path(__file__).resolve().parent.parent``).
    default_repo: the ``owner/repo`` used to build GitHub blob/tree links when
        the ``GITHUB_REPOSITORY`` environment variable is unset (it always
        wins when present, matching GitHub Actions' own convention).
    docs: markdown files to render, in sidebar order — (source path relative
        to root, nav group, label).
    page_template: the full per-page HTML template (``PAGE`` in the old
        per-repo scripts), formatted with title/desc/root/sidebar/crumb/body/
        toc/mermaid_script.
    portal_html: the full docs.html portal template. It is never
        ``str.format``-ed — only passed through ``apply_facts`` — so any
        ``{{fact:KEY}}`` tokens it carries are literal until build time.
    meta_description_prefix: the text before " — {label}" in each rendered
        page's `<meta name="description">`.
    extra: additional markdown that is link-reachable but not in the sidebar.
    skip / skip_prefixes: markdown that must never be published even when
        present on disk (internal planning material).
    fact_overrides: per-repo replacements for one or more of the default fact
        fetchers in DEFAULT_FACT_FETCHERS (e.g. the provider hub reads
        community_provider_count from its own working copy instead of the
        network).
    delete_facts_fallback: whether to remove the copied
        site/facts-fallback.json from the output tree once read. True for
        every repo except vouchfx-telemetry-backend, whose current published
        site has always shipped that file — preserved here as a deliberate,
        named exception rather than silently changing that repo's output.
    site_url: the site's canonical absolute origin (e.g.
        "https://vouchfx.io/"), OPTIONAL and additive (vouchfx issue
        specs/seo-custom-domains.md, REQ-005). Unset (None, the default) is
        the pre-existing behaviour, byte-for-byte: `build()` never writes
        robots.txt/sitemap.xml, and `render_markdown()` never adds a
        `canonical` `str.format()` kwarg. When set, `build()` emits
        robots.txt (allow-all + a `Sitemap:` line) and sitemap.xml (one
        `<loc>` per portal/index/rendered page, all on `site_url`), and
        `render_markdown()` additionally passes `canonical=<absolute page
        URL>` into `page_template.format(...)` — a template with no
        `{canonical}` placeholder still renders identically, since an
        unused `str.format()` keyword argument is simply ignored.

        NOTE — site/ companion passthrough is unconditional, regardless of
        site_url: `build()`'s `shutil.copytree(site, out)` (its very first
        step) always copies every file under a repo's `site/` directory
        verbatim, including any hand-authored `site/robots.txt` or
        `site/llms.txt` a wrapper chooses to ship itself — this predates
        REQ-005 and is deliberately left as-is (adding delete/skip logic
        for a same-named companion would be a behavioural change no
        acceptance criterion asks for, and would break a wrapper relying on
        its own hand-authored file when site_url is unset). When site_url
        IS set, `write_robots_and_sitemap()` runs after `build()` has
        already rendered every page — i.e. strictly after that initial
        copytree — so its `robots.txt`/`sitemap.xml` unconditionally
        overwrite same-named `site/` companions, and are what wins.
    """

    root: Path
    default_repo: str
    docs: list[tuple[str, str, str]]
    page_template: str
    portal_html: str
    meta_description_prefix: str
    extra: list[str] = field(default_factory=list)
    skip: set[str] = field(default_factory=set)
    skip_prefixes: tuple[str, ...] = ()
    fact_overrides: dict[str, Callable[[], str]] = field(default_factory=dict)
    delete_facts_fallback: bool = True
    site_url: str | None = None


# ---------------------------------------------------------------------------
# Mermaid — fence extraction and the CDN script tag
# ---------------------------------------------------------------------------

# CRLF-tolerant: a fence saved with Windows line endings (\r\n) must still be
# recognised, or the block is left as an unrendered code fence instead of a
# diagram.
MERMAID_FENCE_RE = re.compile(r"```mermaid\r?\n(.*?)```", re.DOTALL)

# Pinned from a floating `@11` per #200 (site-tools consolidation): a
# major-version-only CDN URL silently picks up every new minor/patch release
# of mermaid on the next build, across all four sites at once. Vendoring the
# asset instead of pointing at a CDN at all is deferred. Re-pin deliberately.
MERMAID_CDN_URL = "https://cdn.jsdelivr.net/npm/mermaid@11.16.0/dist/mermaid.esm.min.mjs"


def extract_mermaid(text: str) -> tuple[str, list[str]]:
    """Pull ```mermaid fenced blocks out before markdown processing."""
    blocks: list[str] = []

    def grab(m: re.Match) -> str:
        blocks.append(m.group(1))
        return f"\n@@MERMAID{len(blocks) - 1}@@\n"

    text = MERMAID_FENCE_RE.sub(grab, text)
    return text, blocks


# ---------------------------------------------------------------------------
# Fact injection — self-healing volatile facts
#
# A handful of numbers on these sites (the latest engine release, the
# published SDK/community-provider versions, the community registry size)
# change on a cadence no single repository controls. Rather than let them
# drift out of date in hand-written HTML, any page may carry a
# {{fact:KEY}} token that fetch_facts() resolves at build time. Each source is
# independently best-effort: a network hiccup or API shape change falls back
# to the last known-good value in site/facts-fallback.json rather than
# failing the build — a stale fact is a much smaller problem than a broken
# Pages deploy.
# ---------------------------------------------------------------------------

FACT_TOKEN = re.compile(r"\{\{fact:([A-Za-z0-9_]+)\}\}")

# Single ecosystem-wide identifier for outbound fact-fetch requests. The four
# repos previously each sent their own repo-named User-Agent; the header has
# no bearing on any output byte, so consolidating it is a pure simplification.
_USER_AGENT = "vouchfx-site-tools"


def _fetch_json(url: str, headers: dict[str, str] | None = None) -> object:
    req = urllib.request.Request(url, headers=headers or {"User-Agent": _USER_AGENT})
    with urllib.request.urlopen(req, timeout=5) as resp:  # nosec B310 - fixed https URLs only
        return json.loads(resp.read().decode("utf-8"))


def _fetch_engine_release() -> str:
    headers = {"User-Agent": _USER_AGENT, "Accept": "application/vnd.github+json"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    releases = _fetch_json("https://api.github.com/repos/tomas-rampas/vouchfx/releases", headers)
    # Deliberately keeps pre-releases (the whole alpha series IS the release
    # line today); only drafts are skipped. Do not add a prerelease filter.
    return next(r["tag_name"] for r in releases if not r.get("draft"))  # type: ignore[union-attr]


def _fetch_sdk_version() -> str:
    data = _fetch_json("https://api.nuget.org/v3-flatcontainer/vouchfx.sdk/index.json")
    return data["versions"][-1]  # type: ignore[index]


def _fetch_community_jsonrpc_version() -> str:
    data = _fetch_json("https://api.nuget.org/v3-flatcontainer/vouchfx.community.jsonrpc/index.json")
    return data["versions"][-1]  # type: ignore[index]


def _fetch_community_provider_count() -> str:
    data = _fetch_json(
        "https://raw.githubusercontent.com/tomas-rampas/vouchfx-providers/main/registry/community-providers.json"
    )
    if not isinstance(data, list):
        raise ValueError("registry JSON is no longer a top-level array")
    return str(len(data))


# Module defaults, ecosystem-wide. A repo overrides individual entries via
# SiteConfig.fact_overrides (e.g. the provider hub reads
# community_provider_count off its own disk instead of over the network).
DEFAULT_FACT_FETCHERS: dict[str, Callable[[], str]] = {
    "engine_release": _fetch_engine_release,
    "sdk_version": _fetch_sdk_version,
    "community_jsonrpc_version": _fetch_community_jsonrpc_version,
    "community_provider_count": _fetch_community_provider_count,
}


def fetch_facts(config: SiteConfig, site: Path) -> dict[str, str]:
    fallback = json.loads((site / "facts-fallback.json").read_text(encoding="utf-8"))
    facts = dict(fallback)
    live: list[str] = []

    fetchers: dict[str, Callable[[], str]] = {**DEFAULT_FACT_FETCHERS, **config.fact_overrides}
    for key, fetcher in fetchers.items():
        try:
            facts[key] = fetcher()
            live.append(key)
        except Exception:
            pass

    fallback_used = sorted(set(facts) - set(live))
    print(f"facts: live={sorted(live) or ['-']} fallback={fallback_used or ['-']}")
    return facts


def apply_facts(text: str, facts: dict[str, str]) -> str:
    """Substitute {{fact:KEY}} tokens. Called on site/ HTML right after it is
    copied, on every rendered page's HTML before it is written, and on the
    docs.html portal."""
    return FACT_TOKEN.sub(lambda m: html.escape(facts[m.group(1)]) if m.group(1) in facts else m.group(0), text)


# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------


def out_path(rel: str, out: Path) -> Path:
    """Mirror the repo layout under out, with .html extension."""
    return out / (rel[:-3] + ".html")


def rel_root(out: Path, target: Path) -> str:
    """Relative path from a generated file back to the out root, e.g. '../'.
    Forward slashes always, so Windows and CI builds emit identical HTML."""
    rp = os.path.relpath(out, target.parent).replace(os.sep, "/")
    return "" if rp == "." else rp + "/"


def site_url_join(site_url: str, relative_html_path: str) -> str:
    """Join a SiteConfig.site_url with a build-output-relative path (e.g.
    "docs/getting-started.html"), tolerating a site_url with or without a
    trailing slash so a wrapper's `SiteConfig(site_url="https://x/")` and
    `SiteConfig(site_url="https://x")` behave identically."""
    base = site_url if site_url.endswith("/") else site_url + "/"
    return base + relative_html_path


def derive_label(src: Path) -> str:
    """Best-effort page label from the first heading, else the file stem."""
    for line in src.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "):
            return line[2:].strip()
    return src.stem


# ---------------------------------------------------------------------------
# Publication scoping and link rewriting
# ---------------------------------------------------------------------------


def compute_published(config: SiteConfig) -> set[str]:
    rels = {rel for rel, _group, _label in config.docs} | set(config.extra)
    # The auto-render glob is deliberately hard-coded to docs/**/*.md — this
    # scoping, together with skip/skip_prefixes, is the publication boundary
    # that keeps maintainer-local material (plan/, HUMAN_TODO.md, docs/reviews
    # in the engine repo, etc.) off every public site. Do NOT make this glob
    # configurable.
    for src in config.root.glob("docs/**/*.md"):
        rel = src.relative_to(config.root).as_posix()
        if rel not in config.skip and not rel.startswith(config.skip_prefixes):
            rels.add(rel)
    return rels


def rewrite_links(body: str, src_rel: str, *, root: Path, github_url: str, published: set[str]) -> str:
    """Rewrite relative links: published .md pages become .html; any other
    repo-relative target becomes an absolute GitHub URL (it has no page on the
    site). Absolute URLs, anchors and mailto links pass through untouched."""
    src_dir = posixpath.dirname(src_rel)

    def repl(m: re.Match) -> str:
        href = m.group(1)
        if re.match(r"[a-z]+://", href) or href.startswith("#") or href.startswith("mailto:"):
            return m.group(0)
        path, sep, frag = href.partition("#")
        target = posixpath.normpath(posixpath.join(src_dir, path))
        if path.endswith(".md") and target in published:
            return f'href="{path[:-3] + ".html"}{sep}{frag}"'
        kind = "tree" if (root / target).is_dir() else "blob"
        return f'href="{github_url}{kind}/main/{target}{sep}{frag}"'

    return re.sub(r'href="([^"]+)"', repl, body)


def sidebar(active_rel: str, root: str, docs: list[tuple[str, str, str]]) -> str:
    groups: dict[str, list[str]] = {}
    for rel, group, label in docs:
        href = root + rel[:-3] + ".html"
        cls = ' class="active"' if rel == active_rel else ""
        groups.setdefault(group, []).append(f'<a href="{href}"{cls}>{html.escape(label)}</a>')
    parts = [f'<a href="{root}docs.html">← All documentation</a>']
    for group, links in groups.items():
        parts.append(f"<h4>{html.escape(group)}</h4>")
        parts.extend(links)
    return "\n".join(parts)


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------


def render_markdown(
    rel: str,
    label: str,
    *,
    config: SiteConfig,
    out: Path,
    github_url: str,
    published: set[str],
    facts: dict[str, str],
) -> None:
    src = config.root / rel
    text = src.read_text(encoding="utf-8")
    text, mermaid = extract_mermaid(text)

    md = markdown.Markdown(
        extensions=[
            "extra",
            "sane_lists",
            "admonition",
            TocExtension(permalink=True, permalink_class="headerlink", permalink_title="", baselevel=2),
            CodeHiliteExtension(css_class="codehilite", guess_lang=False),
        ]
    )
    body = md.convert(text)
    body = rewrite_links(body, rel, root=config.root, github_url=github_url, published=published)

    # Re-insert mermaid blocks as divs.
    for i, block in enumerate(mermaid):
        body = body.replace(f"<p>@@MERMAID{i}@@</p>", f'<div class="mermaid">{html.escape(block)}</div>')
        body = body.replace(f"@@MERMAID{i}@@", f'<div class="mermaid">{html.escape(block)}</div>')

    toc = getattr(md, "toc", "") or ""
    has_mermaid = bool(mermaid)
    mermaid_script = (
        f'<script type="module">import mermaid from "{MERMAID_CDN_URL}";'
        'mermaid.initialize({startOnLoad:true,theme:"dark"});</script>'
        if has_mermaid
        else ""
    )

    dst = out_path(rel, out)
    dst.parent.mkdir(parents=True, exist_ok=True)
    root_str = rel_root(out, dst)
    desc = f"{config.meta_description_prefix} — {label}"
    format_kwargs: dict[str, str] = dict(
        title=html.escape(label),
        desc=html.escape(desc),
        root=root_str,
        sidebar=sidebar(rel, root_str, config.docs),
        crumb=html.escape(label),
        body=body,
        toc=toc,
        mermaid_script=mermaid_script,
    )
    if config.site_url:
        # Additive only when site_url is set (REQ-005) — a template with no
        # {canonical} placeholder still renders correctly (an unused
        # str.format() kwarg is a no-op), so the site_url-unset path below
        # never even constructs this key and stays byte-identical to the
        # pre-REQ-005 format() call.
        format_kwargs["canonical"] = site_url_join(config.site_url, dst.relative_to(out).as_posix())
    page = config.page_template.format(**format_kwargs)
    dst.write_text(apply_facts(page, facts), encoding="utf-8")
    print(f"  rendered {rel} -> {dst.relative_to(out)}")


def build_portal(config: SiteConfig, out: Path, facts: dict[str, str]) -> None:
    (out / "docs.html").write_text(apply_facts(config.portal_html, facts), encoding="utf-8")
    print("  built docs.html portal")


# ---------------------------------------------------------------------------
# robots.txt / sitemap.xml — additive, only when SiteConfig.site_url is set
# (specs/seo-custom-domains.md, REQ-005). `build()` only calls this function
# from inside an `if config.site_url:` guard, so its very existence has no
# effect on the site_url-unset output path.
# ---------------------------------------------------------------------------


def _sitemap_loc(config: SiteConfig, entry: str) -> str:
    """The <loc> URL for one sitemap entry. "index.html" is special-cased to
    the bare site_url origin (e.g. "https://samples.vouchfx.io/") rather than
    "{site_url}index.html" — canonical URL hygiene: the root page's own
    <link rel="canonical"> is the bare origin (see render_markdown/index.html
    conventions across every wrapper's PAGE/PORTAL templates), and a sitemap
    that advertised a different URL form for the same page would be sending
    search engines a self-contradictory signal for "/"."""
    if entry == "index.html":
        return config.site_url if config.site_url.endswith("/") else config.site_url + "/"
    return site_url_join(config.site_url, entry)


def write_robots_and_sitemap(config: SiteConfig, out: Path, rendered: set[str]) -> None:
    """Emit robots.txt (allow-all + Sitemap: line) and sitemap.xml (<loc>
    for the portal, the copied index.html — as the bare origin, see
    `_sitemap_loc` — and every rendered doc page)."""
    assert config.site_url  # only ever called when truthy — see build()
    (out / "robots.txt").write_text(
        f"User-agent: *\nAllow: /\n\nSitemap: {site_url_join(config.site_url, 'sitemap.xml')}\n",
        encoding="utf-8",
    )

    # index.html/docs.html are written directly (shutil.copytree + build_portal
    # above), never via out_path()/render_markdown(), so they're added by name
    # rather than derived from `rendered`.
    entries = {"index.html", "docs.html"}
    entries.update(out_path(rel, out).relative_to(out).as_posix() for rel in rendered)

    # Sorted by relative-path identifier (unaffected by the index.html ->
    # bare-origin substitution below), so entry order stays deterministic.
    urls = "\n".join(f"  <url><loc>{_sitemap_loc(config, entry)}</loc></url>" for entry in sorted(entries))
    sitemap = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
        f"{urls}\n"
        "</urlset>\n"
    )
    (out / "sitemap.xml").write_text(sitemap, encoding="utf-8")
    print(f"  wrote robots.txt + sitemap.xml ({len(entries)} url(s)) for {config.site_url}")


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def build(config: SiteConfig, out: Path) -> None:
    """Build one repository's site into `out`. See SiteConfig for the knobs.

    Write order for two same-named surfaces worth knowing: the
    `shutil.copytree(site, out)` below runs first and is unconditional
    (any `site/robots.txt`/`site/llms.txt` a wrapper hand-authors always
    passes through, regardless of `config.site_url`); `write_robots_and_
    sitemap()` — only called when `config.site_url` is set — runs last, so
    its generated robots.txt/sitemap.xml overwrite same-named `site/`
    companions when both exist. See `SiteConfig.site_url`'s docstring for
    the full rationale.
    """
    root = config.root
    site = root / "site"

    # Safety: only ever build into a subdirectory of the repo, never root or
    # an outside path — this removes `out` with rmtree before rebuilding.
    if out == root or root not in out.parents:
        raise SystemExit(f"refusing to build into {out}: must be a subdirectory of {root}")
    if out.exists():
        shutil.rmtree(out)
    shutil.copytree(site, out)
    print(f"copied {site.relative_to(root)}/ -> {out.name}/")

    github_url = f"https://github.com/{os.environ.get('GITHUB_REPOSITORY', config.default_repo)}/"

    # Resolve facts, then substitute {{fact:KEY}} tokens into whatever HTML
    # site/ just copied verbatim (index.html, if it carries any tokens).
    # OUT.glob("**/*.html") is a superset of a top-level-only glob; at this
    # point in the build OUT only contains what copytree just placed there, so
    # for every repo today that is byte-equal to a top-level-only pass.
    facts = fetch_facts(config, site)
    for html_file in out.glob("**/*.html"):
        html_file.write_text(apply_facts(html_file.read_text(encoding="utf-8"), facts), encoding="utf-8")
    # site/facts-fallback.json itself is build tooling, not a page — it ships
    # inside site/ so it copies above, but has no business being served.
    if config.delete_facts_fallback:
        (out / "facts-fallback.json").unlink(missing_ok=True)

    # Pygments stylesheet (dark) for fenced code blocks.
    (out / "pygments.css").write_text(
        HtmlFormatter(style="monokai").get_style_defs(".codehilite") + "\n.codehilite{background:transparent}",
        encoding="utf-8",
    )

    published = compute_published(config)

    rendered: set[str] = set()
    for rel, _group, label in config.docs:
        render_markdown(rel, label, config=config, out=out, github_url=github_url, published=published, facts=facts)
        rendered.add(rel)
    for rel in config.extra:
        if (root / rel).exists():
            render_markdown(
                rel,
                derive_label(root / rel),
                config=config,
                out=out,
                github_url=github_url,
                published=published,
                facts=facts,
            )
            rendered.add(rel)

    # Auto-render any markdown under docs/ not explicitly listed, so a newly
    # added file is published (linkable) rather than silently omitted. skip
    # and skip_prefixes hold internal material that must never reach the
    # site, even when the files exist on a maintainer's disk.
    for src in sorted(root.glob("docs/**/*.md")):
        rel = src.relative_to(root).as_posix()
        if rel in rendered or rel in config.skip or rel.startswith(config.skip_prefixes):
            continue
        print(f"  (auto) {rel} not in DOCS — rendering with derived label")
        render_markdown(
            rel, derive_label(src), config=config, out=out, github_url=github_url, published=published, facts=facts
        )
        rendered.add(rel)

    build_portal(config, out, facts)

    if config.site_url:
        write_robots_and_sitemap(config, out, rendered)

    print(f"done -> {out}")
