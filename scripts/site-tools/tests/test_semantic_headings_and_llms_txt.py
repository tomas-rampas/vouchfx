"""Regression tests for two additive `SiteConfig` fields introduced by
specs/seo-fleet-audit.md's fix work order:

``semantic_headings`` (B1/B2)
    False (the default) MUST be byte-identical to today's output: a
    rendered page's own ``# Title`` still renders ``<h2>`` (TocExtension's
    pre-existing ``baselevel=2``), and the sidebar's nav-group label is
    still a literal ``<h4>``. True flips both: a page's own ``# Title``
    becomes a real ``<h1>`` (``baselevel=1``), and the sidebar group label
    becomes a non-heading ``<p class="doc-side__group">`` — taken out of
    the document heading outline entirely, since it was never page content.

``llms_summary`` (B3)
    None (the default) MUST emit no ``llms.txt``, regardless of
    ``site_url``. Only when BOTH ``site_url`` and ``llms_summary`` are set
    does ``build()`` write ``<out>/llms.txt``: an H1 project name, the
    summary paragraph, then a linked list of ``config.docs``, every link
    absolute on ``site_url``.

Test (1) below is the EDGE-002 byte-identical proof this work order's
quality bar requires: it builds the same fixture repo twice — once with
both new fields fully omitted from the ``SiteConfig(...)`` call, once with
both explicitly passed their dataclass defaults (``False`` /``None``) — and
asserts the two output trees are byte-for-byte identical, AND that neither
tree contains ``llms.txt`` nor a semantic (non-``<h4>``) sidebar group
label. This mirrors ``test_site_url_contract.py``'s own
``test_site_url_unset_is_byte_identical_to_the_dataclass_default`` pattern
exactly, extended to cover both new knobs at once.

Every test drives the module's real public API (``SiteConfig`` + ``build()``)
end to end against a throw-away repo skeleton under ``tmp_path`` — never a
private helper — matching this suite's sibling file's own convention. All
four ``DEFAULT_FACT_FETCHERS`` are overridden with fixed values so no test
performs network I/O.

Run (from the repo root):
    python -m pytest scripts/site-tools/tests -q
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

from vouchfx_site_tools import SiteConfig, build

PAGE_TEMPLATE = (
    "<html><head><title>{title}</title>"
    '<meta name="description" content="{desc}"></head>'
    "<body>{sidebar}<main>{body}</main>{toc}{mermaid_script}</body></html>"
)

PORTAL_HTML = "<html><body>Docs portal</body></html>"

SITE_URL = "https://example-site.vouchfx.io/"
LLMS_SUMMARY = "Example site is a fixture used only by this test suite."


@pytest.fixture(autouse=True)
def _no_github_repository_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("GITHUB_REPOSITORY", raising=False)


def _make_repo(tmp_path: Path) -> Path:
    """A repo skeleton with TWO nav-grouped docs (so the sidebar renders at
    least one group label, and llms.txt lists more than one page) —
    everything `build()` touches: `site/facts-fallback.json`,
    `site/index.html`, and two markdown docs under `docs/`."""
    root = tmp_path / "repo"
    site = root / "site"
    site.mkdir(parents=True)
    (site / "facts-fallback.json").write_text("{}", encoding="utf-8")
    (site / "index.html").write_text("<html><body>Landing</body></html>", encoding="utf-8")

    docs = root / "docs"
    docs.mkdir()
    (docs / "getting-started.md").write_text("# Getting started\n\nHello world.\n", encoding="utf-8")
    (docs / "recipes.md").write_text("# Recipes\n\nSome recipes.\n", encoding="utf-8")

    return root


def _base_kwargs(root: Path) -> dict[str, object]:
    return dict(
        root=root,
        default_repo="tomas-rampas/vouchfx-example",
        docs=[
            ("docs/getting-started.md", "Guides", "Getting started"),
            ("docs/recipes.md", "Guides", "Recipes"),
        ],
        page_template=PAGE_TEMPLATE,
        portal_html=PORTAL_HTML,
        meta_description_prefix="vouchfx example site",
        fact_overrides={
            "engine_release": lambda: "v0.0.0-test",
            "sdk_version": lambda: "0.0.0-test",
            "community_jsonrpc_version": lambda: "0.0.0-test",
            "community_provider_count": lambda: "0",
        },
    )


def _tree(out: Path) -> dict[str, bytes]:
    return {p.relative_to(out).as_posix(): p.read_bytes() for p in sorted(out.rglob("*")) if p.is_file()}


# ---------------------------------------------------------------------------
# (1) EDGE-002 proof: both new fields unset (omitted vs. explicit defaults)
#     -> byte-identical trees, no llms.txt, sidebar still emits <h4>.
# ---------------------------------------------------------------------------


def test_new_fields_unset_is_byte_identical_and_legacy_shaped(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out_omitted = root / "_out_omitted"
    out_explicit_default = root / "_out_explicit_default"

    config_omitted = SiteConfig(**_base_kwargs(root), site_url=SITE_URL)
    assert config_omitted.semantic_headings is False
    assert config_omitted.llms_summary is None
    build(config_omitted, out_omitted)

    config_explicit = SiteConfig(
        **_base_kwargs(root), site_url=SITE_URL, semantic_headings=False, llms_summary=None
    )
    build(config_explicit, out_explicit_default)

    tree_omitted = _tree(out_omitted)
    tree_explicit = _tree(out_explicit_default)
    assert set(tree_omitted) == set(tree_explicit)
    assert tree_omitted == tree_explicit

    # Legacy shape, at minimum (per this work order's quality bar): no
    # llms.txt, and the sidebar group label is still a literal <h4>.
    assert "llms.txt" not in tree_omitted
    page = (out_omitted / "docs" / "getting-started.html").read_text(encoding="utf-8")
    assert "<h4>Guides</h4>" in page
    assert 'doc-side__group' not in page


def test_new_fields_unset_matches_pre_existing_baselevel_two(tmp_path: Path) -> None:
    """A page's own `# Title` still renders `<h2>` (TocExtension's
    pre-existing baselevel=2 default) when semantic_headings is unset —
    the exact pre-B1 behaviour, not a real <h1>."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root))

    build(config, out)

    page = (out / "docs" / "getting-started.html").read_text(encoding="utf-8")
    assert '<h2 id="getting-started">Getting started' in page
    assert "<h1" not in page


# ---------------------------------------------------------------------------
# (2) semantic_headings=True -> exactly one <h1> per rendered page, zero
#     <h4> sidebar group labels.
# ---------------------------------------------------------------------------


def test_semantic_headings_true_yields_one_h1_and_no_h4_sidebar_labels(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), semantic_headings=True)

    build(config, out)

    for rel in ("getting-started", "recipes"):
        page = (out / "docs" / f"{rel}.html").read_text(encoding="utf-8")
        assert len(re.findall(r"<h1[ >]", page)) == 1, f"{rel}.html: expected exactly one <h1>"
        assert "<h4>" not in page, f"{rel}.html: sidebar group label must not render as <h4>"
        assert 'class="doc-side__group"' in page


def test_semantic_headings_true_keeps_sidebar_links_and_active_class(tmp_path: Path) -> None:
    """The sidebar's actual navigation content (links, the active-page
    class) is unaffected by the group-label element swap — only the
    group-label tag itself changes."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), semantic_headings=True)

    build(config, out)

    page = (out / "docs" / "getting-started.html").read_text(encoding="utf-8")
    assert '<a href="../docs/getting-started.html" class="active">Getting started</a>' in page
    assert '<a href="../docs/recipes.html">Recipes</a>' in page


# ---------------------------------------------------------------------------
# (3) llms.txt: emitted with correct H1/absolute links when configured,
#     absent when llms_summary is unset (even with site_url set).
# ---------------------------------------------------------------------------


def test_llms_txt_absent_when_summary_unset_even_with_site_url(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), site_url=SITE_URL)  # llms_summary left at its None default
    assert config.llms_summary is None

    build(config, out)

    assert not (out / "llms.txt").exists()


def test_llms_txt_absent_when_site_url_unset_even_with_summary_set(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), llms_summary=LLMS_SUMMARY)  # site_url left at its None default
    assert config.site_url is None

    build(config, out)

    assert not (out / "llms.txt").exists()


def test_llms_txt_emitted_with_h1_and_absolute_links_when_both_set(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY)

    build(config, out)

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    lines = llms.splitlines()

    assert lines[0] == "# vouchfx-example"  # default_repo's repo-name segment
    assert lines[1] == ""
    assert LLMS_SUMMARY in llms

    urls = re.findall(r"\((https://[^)]+)\)", llms)
    assert urls, "expected at least one markdown link"
    for url in urls:
        assert url.startswith(SITE_URL), f"{url!r} is not absolute on {SITE_URL!r}"

    assert f"{SITE_URL}" in llms  # portal/index entry, bare origin
    assert f"{SITE_URL}docs/getting-started.html" in llms
    assert f"{SITE_URL}docs/recipes.html" in llms
    assert "Getting started" in llms
    assert "Recipes" in llms
