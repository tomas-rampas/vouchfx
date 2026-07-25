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
    does ``build()`` write ``<out>/llms.txt`` (llms.txt.org shape: an H1,
    the summary as a ``>`` blockquote, the index/docs.html portal links,
    then one ``## {group}`` section per ``config.docs`` group).

EDGE-002 back-compatibility coverage is split across TWO tests, each
proving something genuinely different (a correction from this file's first
version, which had only the first of these and over-claimed it as "the"
EDGE-002 proof — a determinism check where both sides call the SAME,
already-changed module can never actually catch a default-behaviour
regression, since both sides are the thing under test):

* ``test_new_fields_unset_is_deterministic_and_defaults_are_pinned`` pins
  (1) the two dataclass defaults are still ``False``/``None`` and (2)
  ``build()`` is deterministic — two builds from equivalent configs (fields
  omitted vs. passed explicitly as their defaults) produce a byte-identical
  tree. Mirrors ``test_site_url_contract.py``'s own honest framing
  (``test_site_url_unset_is_byte_identical_to_the_dataclass_default``):
  this does NOT prove the new code path left the OLD module's behaviour
  unchanged — both sides here run the identical, already-patched function
  bodies, so a shared bug would pass this test just as easily as no bug at
  all.
* ``test_new_fields_unset_matches_pre_change_module_byte_for_byte`` is the
  REAL cross-version proof: it extracts this module exactly as it existed
  at ``origin/main`` (the base this branch diverged from, via ``git show``,
  into an isolated import that never touches ``sys.modules
  ["vouchfx_site_tools"]``) and builds the same fixture with THAT module
  and with the current one, asserting byte-for-byte tree equality. This is
  the only test in this file that can actually catch B1/B2/B3 having
  altered the pre-existing default output. It ``pytest.skip``s (not fails)
  when ``origin/main`` cannot be resolved — e.g. a shallow CI checkout with
  no such ref — since that is an environment limitation, not evidence of a
  regression.

Every test drives the module's real public API (``SiteConfig`` + ``build()``)
end to end against a throw-away repo skeleton under ``tmp_path`` — never a
private helper — matching this suite's sibling file's own convention. All
four ``DEFAULT_FACT_FETCHERS`` are overridden with fixed values so no test
performs network I/O.

Run (from the repo root):
    python -m pytest scripts/site-tools/tests -q
"""

from __future__ import annotations

import importlib.util
import re
import subprocess
import sys
import uuid
from pathlib import Path

import pytest

from vouchfx_site_tools import SiteConfig, build

# scripts/site-tools/tests/this_file.py -> tests -> site-tools -> scripts -> repo root
REPO_ROOT = Path(__file__).resolve().parents[3]
BASELINE_REF = "origin/main"
INIT_REL_PATH = "scripts/site-tools/src/vouchfx_site_tools/__init__.py"

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


def _git_ref_resolves(ref: str) -> bool:
    """True iff `ref` resolves to a commit in this checkout. Used to decide
    whether the real cross-version EDGE-002 test can run at all — a shallow
    CI checkout may not have `origin/main` fetched."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--verify", "--quiet", ref],
            cwd=REPO_ROOT,
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            timeout=10,
        )
    except (OSError, subprocess.TimeoutExpired):
        return False
    return result.returncode == 0


def _load_baseline_module(ref: str, rel_path: str, tmp_path: Path):
    """Extract `rel_path` exactly as it existed at `ref` (via `git show`)
    and import it as an isolated module — a distinct `sys.modules` entry
    under a UUID-suffixed name, loaded from a UUID-suffixed temp file, so
    it can never collide with (or shadow) the currently-imported
    `vouchfx_site_tools` this test file's top-level `import` already
    holds. Returns `None` if the ref/path cannot be extracted (caller
    decides whether that's a hard failure or a skip)."""
    try:
        result = subprocess.run(
            ["git", "show", f"{ref}:{rel_path}"],
            cwd=REPO_ROOT,
            capture_output=True,
            # git's blob content is UTF-8 (this module's own source uses
            # non-ASCII punctuation — em dashes, arrows). Decoding via the
            # platform's default locale codec (text=True alone) silently
            # corrupts/crashes on a non-UTF-8 Windows console codepage;
            # encoding="utf-8" pins the correct decode regardless of the
            # host's locale.
            encoding="utf-8",
            errors="replace",
            timeout=10,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0 or not result.stdout.strip():
        return None

    module_name = f"_vouchfx_site_tools_baseline_{uuid.uuid4().hex}"
    module_path = tmp_path / f"{module_name}.py"
    module_path.write_text(result.stdout, encoding="utf-8")

    spec = importlib.util.spec_from_file_location(module_name, module_path)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


# ---------------------------------------------------------------------------
# EDGE-002 back-compatibility: see the module docstring for why this is TWO
# tests, each proving something genuinely different.
# ---------------------------------------------------------------------------


def test_new_fields_unset_is_deterministic_and_defaults_are_pinned(tmp_path: Path) -> None:
    """Pins two things, mirroring test_site_url_contract.py's own honest
    framing: (1) the two dataclass defaults are still False/None — a silent
    default change would flip the sanity assertions below — and (2)
    build() is deterministic: two independent builds from equivalent
    configs (fields omitted vs. passed explicitly as their defaults)
    produce a byte-identical tree.

    This does NOT prove the new B1/B2/B3 code path left the PRE-EXISTING
    module's default behaviour unchanged — both builds here run the
    identical, already-patched function bodies, so a shared bug (or a
    genuine, undocumented default-behaviour change) would pass this test
    just as easily as no bug at all. See
    test_new_fields_unset_matches_pre_change_module_byte_for_byte for the
    test that actually catches that.
    """
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

    # Legacy shape, at minimum: no llms.txt, and the sidebar group label is
    # still a literal <h4>.
    assert "llms.txt" not in tree_omitted
    page = (out_omitted / "docs" / "getting-started.html").read_text(encoding="utf-8")
    assert "<h4>Guides</h4>" in page
    assert 'doc-side__group' not in page


@pytest.mark.parametrize("site_url", [SITE_URL, None], ids=["site_url-set", "site_url-unset"])
def test_new_fields_unset_matches_pre_change_module_byte_for_byte(tmp_path: Path, site_url: str | None) -> None:
    """The REAL cross-version EDGE-002 proof (see module docstring): builds
    the same fixture with the CURRENT module (this test file's normal,
    top-level `import vouchfx_site_tools`) and with the module exactly as
    it existed at `origin/main` — the base this branch diverged from,
    extracted via `git show` into an isolated import that never touches
    `sys.modules["vouchfx_site_tools"]` — both with `semantic_headings`/
    `llms_summary` left unset. Parametrized over `site_url` set and unset,
    per this work order's explicit instruction, since `render_markdown()`'s
    canonical-kwarg branch and `write_robots_and_sitemap()` are each only
    exercised on one side of that split.

    If the two builds are not byte-identical, this branch's B1/B2/B3 change
    altered the pre-existing module's default output — exactly what
    EDGE-002 forbids. Skips (does not fail) when `origin/main` cannot be
    resolved, e.g. a shallow CI checkout with no such ref: that is an
    environment limitation, not evidence of a regression.
    """
    if not _git_ref_resolves(BASELINE_REF):
        pytest.skip(f"{BASELINE_REF!r} does not resolve in this checkout (shallow clone?) — skipping")

    baseline = _load_baseline_module(BASELINE_REF, INIT_REL_PATH, tmp_path)
    if baseline is None:
        pytest.skip(f"could not extract {INIT_REL_PATH!r} from {BASELINE_REF!r} — skipping")

    root = _make_repo(tmp_path / "repo_src")
    out_current = root / "_out_current"
    out_baseline = root / "_out_baseline"

    kwargs = _base_kwargs(root)
    if site_url is not None:
        kwargs["site_url"] = site_url

    current_config = SiteConfig(**kwargs)
    build(current_config, out_current)

    baseline_config = baseline.SiteConfig(**kwargs)
    baseline.build(baseline_config, out_baseline)

    tree_current = _tree(out_current)
    tree_baseline = _tree(out_baseline)
    assert set(tree_current) == set(tree_baseline)
    assert tree_current == tree_baseline


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
    """llms.txt.org shape: H1, `>` blockquote summary, then a `## {group}`
    section (critic M-3) — every link absolute on site_url."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY)

    build(config, out)

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    lines = llms.splitlines()

    assert lines[0] == "# vouchfx-example"  # default_repo's repo-name segment
    assert lines[1] == ""
    assert lines[2] == f"> {LLMS_SUMMARY}"  # blockquote, not a bare paragraph
    assert "## Guides" in llms  # config.docs' group name, as a real section heading

    urls = re.findall(r"\((https://[^)]+)\)", llms)
    assert urls, "expected at least one markdown link"
    for url in urls:
        assert url.startswith(SITE_URL), f"{url!r} is not absolute on {SITE_URL!r}"

    assert f"{SITE_URL}" in llms  # index entry, bare origin
    assert f"{SITE_URL}docs/getting-started.html" in llms
    assert f"{SITE_URL}docs/recipes.html" in llms
    assert "Getting started" in llms
    assert "Recipes" in llms


def test_llms_txt_includes_docs_html_portal_page(tmp_path: Path) -> None:
    """The docs.html portal page — copied verbatim by build()'s initial
    site/ copytree, never a config.docs entry — must be listed too (critic
    m-7: the field docstring already promised this; the original
    implementation did not deliver it)."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY)

    build(config, out)

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    assert f"{SITE_URL}docs.html" in llms


def test_llms_txt_uses_optional_fourth_tuple_element_as_description(tmp_path: Path) -> None:
    """A `config.docs` entry may carry an OPTIONAL 4th element — a
    per-page description — which llms.txt uses verbatim in place of the
    generic `"{meta_description_prefix} — {label}"` text. A 3-tuple
    sibling entry in the same list must still fall back to the generic
    text unaffected, proving 3- and 4-tuples coexist in one list."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    kwargs = _base_kwargs(root)
    kwargs["docs"] = [
        ("docs/getting-started.md", "Guides", "Getting started", "A hand-written, real description."),
        ("docs/recipes.md", "Guides", "Recipes"),  # still a plain 3-tuple
    ]
    config = SiteConfig(**kwargs, site_url=SITE_URL, llms_summary=LLMS_SUMMARY)

    build(config, out)

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    assert "A hand-written, real description." in llms
    assert "vouchfx example site — Recipes" in llms  # unaffected 3-tuple fallback
