#!/usr/bin/env python3
"""Legacy builder for the vouchfx GitHub Pages site — RETIRED for the engine.

The engine's Pages site is now built by MkDocs Material (see mkdocs.yml and
.github/workflows/pages.yml); this script no longer builds or deploys it.
It remains in-tree for two reasons only:

1. The three satellite repos' build_site.py wrappers still follow this file's
   pattern and consume scripts/site-tools/ (the vouchfx-site-tools package,
   vouchfx issue #200) pinned by SHA — nothing here may break for them.
2. The DOCS list below is the authoritative source for the legacy-URL
   redirect table: scripts/site_hooks/_redirect_table.py AST-parses it at
   build time to emit a redirect stub at every URL this builder used to
   publish. Editing DOCS therefore still changes the deployed site (the
   redirect set), which is why pages.yml keeps this file in its triggers.

Running it still works (python scripts/build_site.py [output_dir]) but the
output is not what CI deploys for the engine.

Requires: markdown, pygments  (pip install markdown pygments)
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "site-tools" / "src"))

from vouchfx_site_tools import SiteConfig, build  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "_site"

# Markdown files to render, in sidebar order. (source path relative to ROOT, nav group, label)
DOCS: list[tuple[str, str, str]] = [
    ("docs/01_Technical_Architecture_and_Engineering_Blueprint.md", "Design docs", "01 · Architecture Blueprint"),
    ("docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md", "Design docs", "02 · YAML DSL Specification"),
    ("docs/04_AI_Companion_Feasibility_and_Design.md", "Design docs", "04 · AI Companion (vouchfxai)"),
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
      <a class="doc-card" href="https://providers.vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">PROVIDERS</span><h3>Community Provider Hub</h3><p>The community provider registry and the Vouched badge, with conformance testing, examples, and the provider authoring rubric.</p></a>
      <a class="doc-card" href="https://samples.vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">SAMPLES</span><h3>Sample Applications</h3><p>Four production-grade sample applications in C#, Python, Node.js and Java with complete end-to-end test suites, plus worked migration examples porting Postman, xUnit and SpecFlow assets onto vouchfx.</p></a>
      <a class="doc-card" href="https://telemetry.vouchfx.io/" target="_blank" rel="noopener noreferrer"><span class="doc-card__k">TELEMETRY</span><h3>Telemetry Backend</h3><p>Optional, privacy-first, self-hostable telemetry ingest — why telemetry, deployment, outbox verification, and privacy guarantees.</p></a>
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
      <a href="https://providers.vouchfx.io/" target="_blank" rel="noopener noreferrer">Provider Hub</a>
      <a href="https://samples.vouchfx.io/" target="_blank" rel="noopener noreferrer">Sample Applications</a>
      <a href="https://telemetry.vouchfx.io/" target="_blank" rel="noopener noreferrer">Telemetry Backend</a>
      <a href="https://github.com/tomas-rampas/vouchfx/blob/main/LICENSE" target="_blank" rel="noopener noreferrer">Licence (Apache-2.0)</a>
    </div>
  </div>
</footer>
</body>
</html>
"""

CONFIG = SiteConfig(
    root=ROOT,
    default_repo="tomas-rampas/vouchfx",
    docs=DOCS,
    page_template=PAGE,
    portal_html=PORTAL,
    meta_description_prefix="vouchfx documentation",
    extra=EXTRA,
    skip=SKIP,
    skip_prefixes=SKIP_PREFIXES,
    # REQ-005 (specs/seo-custom-domains.md): opts this legacy builder into
    # emitting robots.txt + sitemap.xml. This file no longer builds the
    # engine's deployed site (mkdocs.yml/scripts/site_hooks/ do) but must
    # still exercise the new SiteConfig knob per REQ-005's own acceptance
    # criterion.
    site_url="https://vouchfx.io/",
)


def main() -> None:
    build(CONFIG, OUT)


if __name__ == "__main__":
    main()
