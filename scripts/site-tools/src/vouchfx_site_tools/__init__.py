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
    docs: markdown files to render, in sidebar order — 3-tuples of (source
        path relative to root, nav group, label). An OPTIONAL 4th element
        (a one-line description) may be appended to any entry — 3-tuples
        remain fully valid everywhere (backwards compatible); the
        description, when present, is used only by `write_llms_txt` in
        place of its generic `"{meta_description_prefix} — {label}"` text
        (see `llms_summary`). Every consumer that iterates `docs`
        normalises through `_doc_entry`, so a mixed list of 3- and 4-tuples
        is fine.
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
    semantic_headings: OPTIONAL and additive (specs/seo-fleet-audit.md,
        B1/B2). False (the default) is the pre-existing behaviour,
        byte-for-byte: `render_markdown()`'s `TocExtension` keeps
        `baselevel=2` (a source Markdown `# Title` renders `<h2>`), and
        `sidebar()` renders each nav group label as `<h4>{group}</h4>`.
        True switches both: `TocExtension(baselevel=1)` so a page's own
        `# Title` renders as a real `<h1>` (previously every rendered page
        had NO `<h1>` at all — the audit's D-09 finding), and `sidebar()`
        renders each group label as `<p class="doc-side__group">{group}</p>`
        instead — taking it out of the document heading outline entirely,
        since it was never semantically a heading in the first place (it is
        chrome: sidebar navigation, not page content). Unlike `site_url`,
        this is a genuine DEFAULT BEHAVIOUR CHANGE the first time a
        consumer sets it `True` (every existing heading in the rendered
        page's DOM shifts down one level) — every heading class in a
        consuming repo's own `site/docs.css` that targets the sidebar
        group label (e.g. `.doc-side h4`) must be renamed to match
        (`.doc-side__group`) in lockstep, or the visual result regresses.
        See this package's README for the full satellite roll-out sequence.
    llms_summary: OPTIONAL and additive (specs/seo-fleet-audit.md, B3). None
        (the default) is the pre-existing behaviour, byte-for-byte: `build()`
        never writes `llms.txt`. Set together with `site_url` (BOTH must be
        truthy — `llms_summary` alone, with `site_url` unset, still emits no
        file, since every link in the file must be site_url-absolute),
        `build()` additionally writes `<out>/llms.txt` per the llms.txt.org
        convention: an H1 project name (derived from `default_repo`), this
        text as a `>` blockquote summary, the portal (`docs.html`) page,
        then one `## {group}` section per `docs` group (the site's "key
        pages" — not every auto-discovered stray markdown file) with a
        linked list of that group's pages, every link absolute on
        `site_url`. See `write_llms_txt`.

        PRECEDENCE, same as `robots.txt`/`sitemap.xml` above: `build()`'s
        initial `shutil.copytree(site, out)` always copies a hand-authored
        `site/llms.txt` companion verbatim first; when `write_llms_txt`
        then runs (site_url AND llms_summary both set), its generated
        `llms.txt` unconditionally overwrites that companion.
    llms_curated: OPTIONAL and additive (vouchfx issue #299 — follow-ups
        from the July 2026 SEO fleet wave's peer-review-critic + gatekeeper
        reviews). False (the default) is the pre-existing behaviour,
        byte-for-byte: the portal bullet still reads
        "{meta_description_prefix} documentation portal" (the awkward
        concatenation the issue was filed against), the two intro bullets
        sit above the first `## {group}` heading with no heading of their
        own, and `## {group}` sections — and the pages within each — are
        alphabetised. This flag has NO effect at all unless `write_llms_txt`
        actually runs, i.e. `site_url` AND `llms_summary` are ALSO both set
        (build()'s own guard, unchanged); it likewise has no effect on
        anything else `build()` emits (sidebar, portal, sitemap, robots.txt).

        True changes three things together, all in `write_llms_txt`, per
        llms.txt.org's own grammar (file lists belong under H2 sections):
        (1) the portal bullet becomes a grammatical sentence — "The
        documentation portal for {name} — every guide and reference on this
        site." — instead of the bare concatenation, where `{name}` is the
        same `default_repo`-derived project name used for the H1; (2) both
        intro bullets (the site's own index-page bullet and the portal
        bullet) move under a new `## Overview` heading, which sits above
        every `## {group}` section, so every file list in the document sits
        under an H2; (3) `## {group}` sections appear in `config.docs`' own
        first-appearance order, and the pages within each group appear in
        `config.docs`' own declaration order, rather than being sorted —
        the curated nav order a repo's `docs` list already encodes (e.g. a
        deliberately-first "Start" group leads the file, not whichever
        group name happens to sort first alphabetically). These three
        changes are deliberately a single flag, not three: they were filed
        and reviewed together as one coherent llms.txt readability fix,
        and no consumer has asked to opt into only one of them.

        RESERVED GROUP NAME: when True, a `config.docs` group whose name
        is "Overview" under a case- and whitespace-insensitive comparison
        (`group.strip().casefold() == "overview"` — so "overview",
        "OVERVIEW", and " Overview " all match, not just an exact literal
        "Overview") collides with the intro section's own new `##
        Overview` heading above — `write_llms_txt` raises `ValueError`
        naming the offending group and `config.docs` entry, rather than
        silently emitting two same-named `## Overview` headings. This only
        constrains the new opt-in path: any such group name is harmless
        (and unchanged, rendered verbatim) when this flag is False.
    """

    root: Path
    default_repo: str
    docs: list[tuple[str, ...]]  # each entry: (rel, group, label) or (rel, group, label, description)
    page_template: str
    portal_html: str
    meta_description_prefix: str
    extra: list[str] = field(default_factory=list)
    skip: set[str] = field(default_factory=set)
    skip_prefixes: tuple[str, ...] = ()
    fact_overrides: dict[str, Callable[[], str]] = field(default_factory=dict)
    delete_facts_fallback: bool = True
    site_url: str | None = None
    semantic_headings: bool = False
    llms_summary: str | None = None
    llms_curated: bool = False


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


def _doc_entry(entry: tuple[str, ...]) -> tuple[str, str, str, str | None]:
    """Normalise one `SiteConfig.docs` entry to (rel, group, label,
    description). The pre-existing 3-tuple shape (rel, group, label)
    normalises to a `None` description; the OPTIONAL 4th element, when
    present, is that page's one-line description (`write_llms_txt` is
    today's only consumer of it). Every call site that iterates
    `config.docs` goes through this helper so a mixed list of 3- and
    4-tuples works everywhere, not just in the one call site a given
    feature needed it for.

    Validates arity explicitly: exactly 3 or 4 elements. Without this, an
    entry with 5+ elements would previously be silently truncated (`entry[0]`,
    `entry[1]`, `entry[2]`, `entry[3]` simply ignore anything past index 3) —
    a typo'd extra element (e.g. a stray trailing comma turning one entry
    into a nested tuple) would silently drop data rather than fail loudly.
    Also rejects a too-short entry (0, 1 or 2 elements) with the same
    named-entry error, rather than letting the 3-tuple unpack below raise a
    generic, entry-less ValueError.
    """
    if len(entry) not in (3, 4):
        raise ValueError(
            f"SiteConfig.docs entry must have exactly 3 (rel, group, label) or "
            f"4 (rel, group, label, description) elements, got {len(entry)}: {entry!r}"
        )
    if len(entry) == 4:
        return entry[0], entry[1], entry[2], entry[3]
    rel, group, label = entry
    return rel, group, label, None


# ---------------------------------------------------------------------------
# Publication scoping and link rewriting
# ---------------------------------------------------------------------------


def compute_published(config: SiteConfig) -> set[str]:
    rels = {_doc_entry(entry)[0] for entry in config.docs} | set(config.extra)
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


def sidebar(active_rel: str, root: str, docs: list[tuple[str, ...]], *, semantic_headings: bool = False) -> str:
    """Render the doc-page sidebar nav.

    `semantic_headings` (SiteConfig.semantic_headings, default False, see
    its docstring) controls only the group-label element: `<h4>{group}</h4>`
    (pre-existing, byte-identical default) when False; the non-heading
    `<p class="doc-side__group">{group}</p>` when True — the sidebar label
    is chrome, not document content, so it has no business appearing in the
    page's heading outline (specs/seo-fleet-audit.md, D-09/B2).
    """
    groups: dict[str, list[str]] = {}
    for entry in docs:
        rel, group, label, _desc = _doc_entry(entry)
        href = root + rel[:-3] + ".html"
        cls = ' class="active"' if rel == active_rel else ""
        groups.setdefault(group, []).append(f'<a href="{href}"{cls}>{html.escape(label)}</a>')
    parts = [f'<a href="{root}docs.html">← All documentation</a>']
    for group, links in groups.items():
        if semantic_headings:
            parts.append(f'<p class="doc-side__group">{html.escape(group)}</p>')
        else:
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
            TocExtension(
                permalink=True,
                permalink_class="headerlink",
                permalink_title="",
                baselevel=1 if config.semantic_headings else 2,
            ),
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
        sidebar=sidebar(rel, root_str, config.docs, semantic_headings=config.semantic_headings),
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
# llms.txt — additive, only when BOTH SiteConfig.site_url AND
# SiteConfig.llms_summary are set (specs/seo-fleet-audit.md, REQ-005/B3).
# `build()` only calls this function from inside an
# `if config.site_url and config.llms_summary:` guard, so its very
# existence has no effect on the (site_url-set, llms_summary-unset) or
# (site_url-unset) output paths — both remain byte-identical to today.
# ---------------------------------------------------------------------------


def _project_name(config: SiteConfig) -> str:
    """Best-effort project/site name for the llms.txt H1: the repo-name
    segment of `SiteConfig.default_repo` (e.g. "tomas-rampas/vouchfx-samples"
    -> "vouchfx-samples") — every vouchfx-ecosystem repository is already
    named directly after the site it publishes, so this needs no additional
    SiteConfig field of its own.

    Deliberately `config.default_repo`, NOT `os.environ.get("GITHUB_REPOSITORY",
    config.default_repo)` the way `build()`'s own `github_url` is derived a
    few lines below: `GITHUB_REPOSITORY` names whichever checkout is
    actually running the build (a fork included, e.g. "someone/vouchfx-
    samples-fork" in a fork's own CI run) — fine for a GitHub blob/tree URL,
    which should point at the repository that produced it, but wrong for
    published, PUBLIC page content like llms.txt, which must stay the same
    regardless of which fork or checkout happens to be building it.
    `default_repo` is this package's one fork-stable identity for a site.
    """
    return config.default_repo.rsplit("/", 1)[-1]


def write_llms_txt(config: SiteConfig, out: Path, rendered: set[str]) -> None:
    """Emit llms.txt following the llms.txt.org convention: an H1 project
    name, `config.llms_summary` as a `>` blockquote, a short intro list
    (the site's own index page and the `docs.html` portal), then one
    `## {group}` section per `config.docs` group with a linked list of that
    group's pages — `config.docs` is the site's curated, labelled nav, not
    every auto-discovered stray markdown file `build()` also renders.
    Every link is absolute on `config.site_url`.

    Per-page description: the OPTIONAL 4th element of a `config.docs` entry
    (see `_doc_entry`/`SiteConfig.docs`), when present; otherwise the
    pre-existing generic `"{meta_description_prefix} — {label}"` text.

    Modelled on `write_robots_and_sitemap()` above: same "index.html is the
    bare origin" convention via `_sitemap_loc`, same site_url-join via
    `site_url_join`, same `assert`-both-truthy contract (only ever called
    from build()'s own `if config.site_url and config.llms_summary:` guard).

    `config.llms_curated` (issue #299) selects between two output shapes —
    see the field's own docstring for the full rationale, summarised here:

    * False (the default): the pre-#299 shape, byte-for-byte — the intro
      bullets sit above the first `## {group}` heading with no heading of
      their own, the portal bullet is the bare
      `"{meta_description_prefix} documentation portal"` concatenation, and
      `## {group}` sections (and the pages within each) are alphabetised.
    * True: a grammatical portal bullet naming the site, both intro bullets
      moved under a new `## Overview` heading (so every file list sits
      under an H2, per llms.txt.org's own grammar), and `## {group}`
      sections plus their pages kept in `config.docs`' own declaration
      order — the curated nav order — instead of being sorted.

    Raises `ValueError` when `llms_curated` is True and any `config.docs`
    entry's group matches "Overview" under a case- and whitespace-
    insensitive comparison (`group.strip().casefold() == "overview"`) —
    it would collide with the intro section's own `## Overview` heading,
    silently producing two same-named headings. The message names both
    the offending group and the full `config.docs` entry, mirroring
    `_doc_entry`'s own `{entry!r}`-bearing `ValueError`. See
    `SiteConfig.llms_curated`'s own docstring.
    """
    assert config.site_url and config.llms_summary  # only ever called when both are truthy — see build()

    name = _project_name(config)
    lines = [f"# {name}", "", f"> {config.llms_summary}", ""]

    index_bullet = f"- [{name}]({_sitemap_loc(config, 'index.html')}): {config.meta_description_prefix}"
    if config.llms_curated:
        portal_bullet = (
            f"- [Documentation]({site_url_join(config.site_url, 'docs.html')}): "
            f"The documentation portal for {name} — every guide and reference on this site."
        )
        lines.append("## Overview")
        lines.append(index_bullet)
        lines.append(portal_bullet)
    else:
        portal_bullet = (
            f"- [Documentation]({site_url_join(config.site_url, 'docs.html')}): "
            f"{config.meta_description_prefix} documentation portal"
        )
        lines.append(index_bullet)
        lines.append(portal_bullet)
    lines.append("")

    # Grouped by config.docs' own `group` field (each group rendered as its
    # own `## {group}` section, per the llms.txt.org convention).
    # `group_order` records each group's FIRST appearance in `config.docs`
    # — consulted only when `llms_curated` is True (the curated order);
    # the default path still sorts, exactly as before. Auto-discovered
    # pages (not in config.docs) are deliberately excluded: they carry no
    # curated label/group.
    groups: dict[str, list[tuple[str, str]]] = {}
    group_order: list[str] = []
    for entry in config.docs:
        rel, group, label, description = _doc_entry(entry)
        if config.llms_curated and group.strip().casefold() == "overview":
            raise ValueError(
                f"SiteConfig.docs group {group!r} collides with the llms_curated intro section "
                f"'Overview' (case- and whitespace-insensitive match) — rename the group "
                f"(entry {entry!r})"
            )
        if rel not in rendered:
            continue  # defensive only — build() pre-renders every config.docs rel
        url = site_url_join(config.site_url, out_path(rel, out).relative_to(out).as_posix())
        text = description if description is not None else f"{config.meta_description_prefix} — {label}"
        if group not in groups:
            groups[group] = []
            group_order.append(group)
        groups[group].append((label, f"- [{label}]({url}): {text}"))

    doc_count = 0
    group_names = group_order if config.llms_curated else sorted(groups)
    for group in group_names:
        lines.append(f"## {group}")
        entries = groups[group] if config.llms_curated else sorted(groups[group])
        for _label, line in entries:
            lines.append(line)
            doc_count += 1
        lines.append("")

    (out / "llms.txt").write_text("\n".join(lines).rstrip("\n") + "\n", encoding="utf-8")
    print(f"  wrote llms.txt ({doc_count} doc page(s) across {len(groups)} group(s)) for {config.site_url}")


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
    for entry in config.docs:
        rel, _group, label, _desc = _doc_entry(entry)
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
    if config.site_url and config.llms_summary:
        write_llms_txt(config, out, rendered)

    print(f"done -> {out}")
