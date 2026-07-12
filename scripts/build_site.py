#!/usr/bin/env python3
"""Build the vouchfx GitHub Pages site.

Copies the static landing page (site/) into the output directory, then renders
the repository's markdown design docs and user guides into styled HTML that
matches the landing page. The markdown files remain the single source of truth;
this generates their HTML on every run, so a CI deploy keeps the published docs
current with every push.

    python scripts/build_site.py [output_dir]   # default: _site

Requires: markdown, pygments  (pip install markdown pygments)
"""
from __future__ import annotations

import html
import json
import os
import posixpath
import re
import shutil
import sys
import urllib.request
from pathlib import Path

import markdown
from markdown.extensions.codehilite import CodeHiliteExtension
from markdown.extensions.toc import TocExtension
from pygments.formatters import HtmlFormatter

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "site"
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "_site"

# Markdown files to render, in sidebar order. (source path relative to ROOT, nav group, label)
DOCS: list[tuple[str, str, str]] = [
    ("docs/01_Technical_Architecture_and_Engineering_Blueprint.md", "Design docs", "01 · Architecture Blueprint"),
    ("docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md", "Design docs", "02 · YAML DSL Specification"),
    ("docs/getting-started.md", "User guides", "Getting started (60-minute path)"),
    ("docs/recipes.md", "User guides", "Recipes"),
    ("docs/common-patterns.md", "User guides", "Common patterns"),
    ("docs/troubleshooting.md", "User guides", "Troubleshooting"),
    ("docs/telemetry.md", "User guides", "Telemetry & privacy"),
    ("docs/language-reference.md", "User guides", "Language reference (generated)"),
    ("docs/memory-harness.md", "User guides", "The memory-leak harness"),
    ("docs/accessibility.md", "User guides", "Accessibility"),
    ("docs/kb/dcp-orchestrator-portability.md", "Knowledge base", "KB: DCP orchestrator not found"),
    ("docs/ecosystem.md", "Ecosystem", "The ecosystem"),
    ("docs/roadmap.md", "Project", "Roadmap"),
    ("CHANGELOG.md", "Project", "Changelog"),
    ("GOVERNANCE.md", "Project", "Governance"),
    ("docs/decisions/dotnet-tool-packaging.md", "Project", "Decision: dotnet tool packaging"),
    ("README.md", "Project", "Project README"),
]

# Any additional markdown that is link-reachable but not in the sidebar.
EXTRA: list[str] = []

# Markdown that must never be published, even when present on a maintainer's
# disk (internal planning/review material kept out of the public site).
SKIP = {"docs/03_MVP_Project_Plan.md"}
SKIP_PREFIXES = ("docs/reviews/",)

def out_path(rel: str) -> Path:
    """Mirror the repo layout under OUT, with .html extension."""
    return OUT / (rel[:-3] + ".html")


def rel_root(target: Path) -> str:
    """Relative path from a generated file back to OUT root, e.g. '../'.
    Forward slashes always, so Windows and CI builds emit identical HTML."""
    rp = os.path.relpath(OUT, target.parent).replace(os.sep, "/")
    return "" if rp == "." else rp + "/"


GITHUB_URL = f"https://github.com/{os.environ.get('GITHUB_REPOSITORY', 'tomas-rampas/vouchfx')}/"
PUBLISHED: set[str] = set()


def compute_published() -> set[str]:
    rels = {rel for rel, _group, _label in DOCS} | set(EXTRA)
    for src in ROOT.glob("docs/**/*.md"):
        rel = src.relative_to(ROOT).as_posix()
        if rel not in SKIP and not rel.startswith(SKIP_PREFIXES):
            rels.add(rel)
    return rels


def rewrite_links(body: str, src_rel: str) -> str:
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
        if path.endswith(".md") and target in PUBLISHED:
            return f'href="{path[:-3] + ".html"}{sep}{frag}"'
        kind = "tree" if (ROOT / target).is_dir() else "blob"
        return f'href="{GITHUB_URL}{kind}/main/{target}{sep}{frag}"'

    return re.sub(r'href="([^"]+)"', repl, body)


def extract_mermaid(text: str) -> tuple[str, list[str]]:
    """Pull ```mermaid fenced blocks out before markdown processing."""
    blocks: list[str] = []

    def grab(m: re.Match) -> str:
        blocks.append(m.group(1))
        return f"\n@@MERMAID{len(blocks) - 1}@@\n"

    text = re.sub(r"```mermaid\n(.*?)```", grab, text, flags=re.DOTALL)
    return text, blocks


def sidebar(active_rel: str, root: str) -> str:
    groups: dict[str, list[str]] = {}
    for rel, group, label in DOCS:
        href = root + rel[:-3] + ".html"
        cls = ' class="active"' if rel == active_rel else ""
        groups.setdefault(group, []).append(f'<a href="{href}"{cls}>{html.escape(label)}</a>')
    parts = [f'<a href="{root}docs.html">← All documentation</a>']
    for group, links in groups.items():
        parts.append(f"<h4>{html.escape(group)}</h4>")
        parts.extend(links)
    return "\n".join(parts)


# ---------------------------------------------------------------------------
# Fact injection — self-healing volatile facts
#
# A handful of numbers on the landing page (the latest engine release, the
# published SDK/community-provider versions, the community registry size)
# change on a cadence this repository doesn't control. Rather than let them
# silently drift out of date in hand-written HTML, any page can carry a
# {{fact:KEY}} token that fetch_facts() resolves at build time. Each source
# is independently best-effort: a network hiccup or API shape change falls
# back to the last known-good value in site/facts-fallback.json rather than
# failing the build — a stale fact is a much smaller problem than a broken
# Pages deploy.
# ---------------------------------------------------------------------------

FACT_TOKEN = re.compile(r"\{\{fact:([A-Za-z0-9_]+)\}\}")
FACTS: dict[str, str] = {}


def _fetch_json(url: str, headers: dict[str, str] | None = None):
    req = urllib.request.Request(url, headers=headers or {"User-Agent": "vouchfx-build-site"})
    with urllib.request.urlopen(req, timeout=5) as resp:  # nosec B310 - fixed https URLs only
        return json.loads(resp.read().decode("utf-8"))


def fetch_facts() -> dict[str, str]:
    fallback = json.loads((SITE / "facts-fallback.json").read_text(encoding="utf-8"))
    facts = dict(fallback)
    live: list[str] = []

    try:
        gh_headers = {"User-Agent": "vouchfx-build-site", "Accept": "application/vnd.github+json"}
        token = os.environ.get("GITHUB_TOKEN")
        if token:
            gh_headers["Authorization"] = f"Bearer {token}"
        releases = _fetch_json("https://api.github.com/repos/tomas-rampas/vouchfx/releases", gh_headers)
        # Deliberately keeps pre-releases (the whole alpha series IS the release
        # line today); only drafts are skipped. Do not add a prerelease filter.
        facts["engine_release"] = next(r["tag_name"] for r in releases if not r.get("draft"))
        live.append("engine_release")
    except Exception:
        pass

    try:
        data = _fetch_json("https://api.nuget.org/v3-flatcontainer/vouchfx.sdk/index.json")
        facts["sdk_version"] = data["versions"][-1]
        live.append("sdk_version")
    except Exception:
        pass

    try:
        data = _fetch_json("https://api.nuget.org/v3-flatcontainer/vouchfx.community.jsonrpc/index.json")
        facts["community_jsonrpc_version"] = data["versions"][-1]
        live.append("community_jsonrpc_version")
    except Exception:
        pass

    try:
        data = _fetch_json(
            "https://raw.githubusercontent.com/tomas-rampas/vouchfx-providers/main/registry/community-providers.json"
        )
        if not isinstance(data, list):
            raise ValueError("registry JSON is no longer a top-level array")
        facts["community_provider_count"] = str(len(data))
        live.append("community_provider_count")
    except Exception:
        pass

    fallback_used = sorted(set(facts) - set(live))
    print(f"facts: live={sorted(live) or ['-']} fallback={fallback_used or ['-']}")
    return facts


def apply_facts(text: str) -> str:
    """Substitute {{fact:KEY}} tokens. Called on site/ HTML right after it is
    copied, and on every rendered page's HTML before it is written."""
    return FACT_TOKEN.sub(lambda m: html.escape(FACTS[m.group(1)]) if m.group(1) in FACTS else m.group(0), text)


PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>{title} · vouchfx docs</title>
<meta name="description" content="{desc}" />
<meta name="theme-color" content="#0b0f1a" />
<link rel="icon" href="{root}favicon.svg" type="image/svg+xml" />
<link rel="stylesheet" href="{root}styles.css" />
<link rel="stylesheet" href="{root}docs.css" />
<link rel="stylesheet" href="{root}pygments.css" />
</head>
<body>
<header class="nav">
  <div class="nav__inner">
    <a class="brand" href="{root}index.html" aria-label="vouchfx home">
      <span class="brand__mark" aria-hidden="true"></span>
      <span class="brand__name">vouchfx</span>
    </a>
    <nav class="nav__links" aria-label="Primary">
      <a href="{root}index.html">Home</a>
      <a href="{root}docs.html">Docs</a>
      <a href="{root}index.html#architecture">Architecture</a>
      <a href="{root}index.html#roadmap">Roadmap</a>
    </nav>
    <a class="btn btn--ghost nav__gh" href="https://github.com/tomas-rampas/vouchfx" target="_blank" rel="noopener noreferrer">GitHub</a>
  </div>
</header>
<div class="doc-shell">
  <aside class="doc-side">{sidebar}</aside>
  <main class="doc-main">
    <div class="doc-breadcrumb"><a href="{root}docs.html">Documentation</a> / {crumb}</div>
    <article class="prose">{body}</article>
  </main>
  <nav class="doc-toc"><h4>On this page</h4>{toc}</nav>
</div>
{mermaid_script}
</body>
</html>
"""


def render_markdown(rel: str, label: str) -> None:
    src = ROOT / rel
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
    body = rewrite_links(body, rel)

    # Re-insert mermaid blocks as divs.
    for i, block in enumerate(mermaid):
        body = body.replace(f"<p>@@MERMAID{i}@@</p>", f'<div class="mermaid">{html.escape(block)}</div>')
        body = body.replace(f"@@MERMAID{i}@@", f'<div class="mermaid">{html.escape(block)}</div>')

    toc = getattr(md, "toc", "") or ""
    has_mermaid = bool(mermaid)
    mermaid_script = (
        '<script type="module">import mermaid from "https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs";'
        'mermaid.initialize({startOnLoad:true,theme:"dark"});</script>'
        if has_mermaid
        else ""
    )

    dst = out_path(rel)
    dst.parent.mkdir(parents=True, exist_ok=True)
    root = rel_root(dst)
    desc = f"vouchfx documentation — {label}"
    page = PAGE.format(
        title=html.escape(label),
        desc=html.escape(desc),
        root=root,
        sidebar=sidebar(rel, root),
        crumb=html.escape(label),
        body=body,
        toc=toc,
        mermaid_script=mermaid_script,
    )
    dst.write_text(apply_facts(page), encoding="utf-8")
    print(f"  rendered {rel} -> {dst.relative_to(OUT)}")


PORTAL = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>Documentation · vouchfx</title>
<meta name="description" content="vouchfx documentation — the architecture blueprint, the YAML DSL specification, and the user guides." />
<meta name="theme-color" content="#0b0f1a" />
<link rel="icon" href="favicon.svg" type="image/svg+xml" />
<link rel="stylesheet" href="styles.css" />
<link rel="stylesheet" href="docs.css" />
</head>
<body>
<header class="nav">
  <div class="nav__inner">
    <a class="brand" href="index.html" aria-label="vouchfx home">
      <span class="brand__mark" aria-hidden="true"></span>
      <span class="brand__name">vouchfx</span>
    </a>
    <nav class="nav__links" aria-label="Primary">
      <a href="index.html">Home</a>
      <a href="index.html#architecture">Architecture</a>
      <a href="index.html#dsl">The DSL</a>
      <a href="index.html#roadmap">Roadmap</a>
    </nav>
    <a class="btn btn--ghost nav__gh" href="https://github.com/tomas-rampas/vouchfx" target="_blank" rel="noopener noreferrer">GitHub</a>
  </div>
</header>
<div class="container portal">
  <div class="portal__head">
    <p class="eyebrow">Documentation</p>
    <h1 class="section__title">The design is the source of truth.</h1>
    <p class="section__lede">vouchfx is a fully-specified system. These pages are rendered straight from the
      repository's markdown on every push, so they never drift from the code.</p>
  </div>

  <section class="portal__group">
    <h2>Design docs</h2>
    <p>How the system is built, and the language it runs.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/01_Technical_Architecture_and_Engineering_Blueprint.html">
        <span class="doc-card__k">01</span><h3>Technical Architecture &amp; Engineering Blueprint</h3>
        <p>The single source of truth: the five layers, Aspire/Testcontainers, the Roslyn compiler and memory model, security, the verdict taxonomy, provider architecture, reporting and secrets.</p>
      </a>
      <a class="doc-card" href="docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.html">
        <span class="doc-card__k">02</span><h3>YAML DSL Specification &amp; VSCode Extension</h3>
        <p>The <code>.e2e.yaml</code> grammar — document structure, step families, capture/placeholder syntax, verification modes, the JSON Schema contract, and the editor tooling.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>User guides</h2>
    <p>Author, run and debug your first suites — and understand the language, telemetry and accessibility surface.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/getting-started.html">
        <span class="doc-card__k">START</span><h3>Getting started</h3>
        <p>A 60-minute path from a ready environment to your first PASS: build vouchfx, author a minimal <code>.e2e.yaml</code>, run it, and read the verdict.</p>
      </a>
      <a class="doc-card" href="docs/recipes.html">
        <span class="doc-card__k">RECIPES</span><h3>Recipes</h3>
        <p>Task-oriented, runnable patterns — SQL seeding, WireMock test doubles, environment secrets, capture and substitution, RETRY polling, and more.</p>
      </a>
      <a class="doc-card" href="docs/common-patterns.html">
        <span class="doc-card__k">PATTERNS</span><h3>Common patterns</h3>
        <p>The structural and compositional shapes most test files share: the four top-level sections, test selection, services vs. dependencies, and step composition.</p>
      </a>
      <a class="doc-card" href="docs/troubleshooting.html">
        <span class="doc-card__k">FIXES</span><h3>Troubleshooting</h3>
        <p>Real failure modes and how to fix them — Docker reachability, health-gate timeouts, discovery-path gotchas, and reading each verdict class.</p>
      </a>
      <a class="doc-card" href="docs/telemetry.html">
        <span class="doc-card__k">PRIVACY</span><h3>Telemetry &amp; privacy</h3>
        <p>The privacy-first, opt-in telemetry design: what is collected, how to enable or disable it, where data goes, and the guarantees that protect you.</p>
      </a>
      <a class="doc-card" href="docs/language-reference.html">
        <span class="doc-card__k">REFERENCE</span><h3>Language reference</h3>
        <p>Every field, generated straight from the composed <code>v1</code> JSON Schema the compiler validates against — so it can never drift from what vouchfx accepts.</p>
      </a>
      <a class="doc-card" href="docs/memory-harness.html">
        <span class="doc-card__k">GATE</span><h3>The memory-leak harness</h3>
        <p>The heap-measurement tool behind the permanent CI gate: what it exercises across all twenty-five providers, how to run it locally, and a real passing report.</p>
      </a>
      <a class="doc-card" href="docs/accessibility.html">
        <span class="doc-card__k">A11Y</span><h3>Accessibility</h3>
        <p>The WCAG 2.1 AA conformance record for the terminal and HTML report renderers — the audit, findings, and remediation.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Knowledge base</h2>
    <p>Incident write-ups for notable defects: symptom, root cause, resolution, and how regression is prevented.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/kb/dcp-orchestrator-portability.html">
        <span class="doc-card__k">KB</span><h3>DCP orchestrator not found</h3>
        <p>Why every NuGet install of the tool up to 1.0.0-alpha.5 failed its first run — the baked build-machine DCP path, the same-machine smoke-test blind spot, and the runtime self-heal that fixed it.</p>
      </a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Ecosystem</h2>
    <p>Community providers, sample applications, and related projects.</p>
    <p class="note">Live: engine {{fact:engine_release}} · <code>Vouchfx.Sdk</code> {{fact:sdk_version}} on NuGet ·
      the hub registry lists {{fact:community_provider_count}} community provider(s).</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/ecosystem.html"><span class="doc-card__k">MAP</span><h3>The ecosystem</h3><p>One engine, four repositories: what each is for, where its site lives, and where to ask questions.</p></a>
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx-providers/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">PROVIDERS</span><h3>Community Provider Hub</h3><p>The community provider registry and the Vouched badge, with conformance testing, examples, and the provider authoring rubric.</p></a>
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx-samples/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">SAMPLES</span><h3>Sample Applications</h3><p>Four production-grade sample applications in C#, Python, Node.js and Java with complete end-to-end test suites demonstrating vouchfx patterns.</p></a>
      <a class="doc-card" href="https://tomas-rampas.github.io/vouchfx-telemetry-backend/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">TELEMETRY</span><h3>Telemetry Backend</h3><p>Optional, privacy-first, self-hostable telemetry ingest — why telemetry, deployment, outbox verification, and privacy guarantees.</p></a>
    </div>
  </section>

  <section class="portal__group">
    <h2>Project</h2>
    <p>Where the project is heading, what has shipped, and how it is run.</p>
    <div class="doc-cards">
      <a class="doc-card" href="docs/roadmap.html"><span class="doc-card__k">ROADMAP</span><h3>Roadmap</h3><p>What has shipped, what v1.0 still needs, what v1.x adds next — and what stays free permanently.</p></a>
      <a class="doc-card" href="CHANGELOG.html"><span class="doc-card__k">CHANGES</span><h3>Changelog</h3><p>The delivered-capability record, in Keep-a-Changelog format, seeding each release's notes.</p></a>
      <a class="doc-card" href="GOVERNANCE.html"><span class="doc-card__k">GOV</span><h3>Governance</h3><p>Who decides what enters Core, how providers earn the Vouched badge, and how disputes are resolved.</p></a>
      <a class="doc-card" href="docs/decisions/dotnet-tool-packaging.html"><span class="doc-card__k">ADR</span><h3>Decision: dotnet tool packaging</h3><p>Why the CLI ships as a dotnet global tool, how DCP metadata resolution works, and the portability trade-off.</p></a>
      <a class="doc-card" href="README.html"><span class="doc-card__k">README</span><h3>Project README</h3><p>What vouchfx is, how it works, building &amp; testing, and the repository layout.</p></a>
      <a class="doc-card" href="https://github.com/tomas-rampas/vouchfx/blob/main/CONTRIBUTING.md" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">SDK</span><h3>Contributing &amp; provider authoring</h3><p>The provider-authoring guide: the frozen v1 contract, CsxFragment rules, testing, and the Vouched rubric.</p></a>
      <a class="doc-card" href="https://github.com/tomas-rampas/vouchfx/blob/main/SECURITY.md" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">SEC</span><h3>Security policy</h3><p>How to report a vulnerability, the disclosure process, and the supported-versions table.</p></a>
    </div>
  </section>
</div>

<footer class="footer">
  <div class="container footer__inner">
    <div class="footer__brand">
      <span class="brand__mark" aria-hidden="true"></span>
      <div><strong>vouchfx</strong><p>End-to-end integration testing for distributed systems, authored in YAML.</p></div>
    </div>
    <div class="footer__links">
      <a href="index.html">Home</a>
      <a href="https://github.com/tomas-rampas/vouchfx" target="_blank" rel="noopener noreferrer">Repository</a>
      <a href="https://tomas-rampas.github.io/vouchfx-providers/" target="_blank" rel="noopener noreferrer">Provider Hub</a>
      <a href="https://tomas-rampas.github.io/vouchfx-samples/" target="_blank" rel="noopener noreferrer">Sample Applications</a>
      <a href="https://tomas-rampas.github.io/vouchfx-telemetry-backend/" target="_blank" rel="noopener noreferrer">Telemetry Backend</a>
      <a href="https://github.com/tomas-rampas/vouchfx/blob/main/LICENSE" target="_blank" rel="noopener noreferrer">Licence (Apache-2.0)</a>
    </div>
  </div>
</footer>
</body>
</html>
"""


def build_portal() -> None:
    (OUT / "docs.html").write_text(apply_facts(PORTAL), encoding="utf-8")
    print("  built docs.html portal")


def derive_label(src: Path) -> str:
    """Best-effort page label from the first heading, else the file stem."""
    for line in src.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "):
            return line[2:].strip()
    return src.stem


def main() -> None:
    # Safety: only ever build into a subdirectory of the repo, never ROOT or an
    # outside path — main() removes OUT with rmtree before rebuilding.
    if OUT == ROOT or ROOT not in OUT.parents:
        raise SystemExit(f"refusing to build into {OUT}: must be a subdirectory of {ROOT}")
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(SITE, OUT)
    print(f"copied {SITE.relative_to(ROOT)}/ -> {OUT.name}/")

    # Resolve facts, then substitute {{fact:KEY}} tokens into whatever HTML
    # site/ just copied verbatim (index.html). site/facts-fallback.json itself
    # is build tooling, not a page — it ships inside site/ so it copies above,
    # but has no business being served, so remove the copy once read.
    global FACTS
    FACTS = fetch_facts()
    for html_file in OUT.glob("*.html"):
        html_file.write_text(apply_facts(html_file.read_text(encoding="utf-8")), encoding="utf-8")
    (OUT / "facts-fallback.json").unlink(missing_ok=True)

    # Pygments stylesheet (dark) for fenced code blocks.
    (OUT / "pygments.css").write_text(
        HtmlFormatter(style="monokai").get_style_defs(".codehilite") + "\n.codehilite{background:transparent}",
        encoding="utf-8",
    )

    PUBLISHED.update(compute_published())

    rendered: set[str] = set()
    for rel, _group, label in DOCS:
        render_markdown(rel, label)
        rendered.add(rel)
    for rel in EXTRA:
        if (ROOT / rel).exists():
            render_markdown(rel, derive_label(ROOT / rel))
            rendered.add(rel)

    # Auto-render any markdown under docs/ not explicitly listed, so a newly
    # added file is published (linkable) rather than silently omitted. SKIP and
    # SKIP_PREFIXES hold internal material that must never reach the site, even
    # when the files exist on a maintainer's disk.
    for src in sorted(ROOT.glob("docs/**/*.md")):
        rel = src.relative_to(ROOT).as_posix()
        if rel in rendered or rel in SKIP or rel.startswith(SKIP_PREFIXES):
            continue
        print(f"  (auto) {rel} not in DOCS — rendering with derived label")
        render_markdown(rel, derive_label(src))
        rendered.add(rel)

    build_portal()
    print(f"done -> {OUT}")


if __name__ == "__main__":
    main()
