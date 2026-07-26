"""Regression tests for additive `SiteConfig` fields introduced by
specs/seo-fleet-audit.md's fix work order, plus vouchfx issue #299's
follow-up refinements to the same llms.txt surface:

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

``llms_curated`` (issue #299)
    False (the default) MUST reproduce the pre-#299 ``llms.txt`` shape
    byte-for-byte: the awkward concatenated portal bullet, no ``##
    Overview`` heading, and alphabetised groups/pages. True — only once
    ``site_url`` AND ``llms_summary`` are ALSO set — rewrites the portal
    bullet as a grammatical sentence, moves both intro bullets under a new
    ``## Overview`` heading, and keeps ``## {group}`` sections/pages in
    ``config.docs``' own declaration order instead of alphabetising them.
    A ``config.docs`` group literally named ``"Overview"`` raises
    ``ValueError`` when this flag is True (it would collide with the new
    heading), but is harmless when it is False.

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
* ``test_new_fields_unset_matches_pre_merge_origin_main_byte_for_byte`` is
  the REAL cross-version proof, WHILE this branch has genuinely diverged
  from ``origin/main``: it extracts this module exactly as it existed at
  ``origin/main`` (the base this branch diverged from, via ``git show``,
  into an isolated import that never touches ``sys.modules
  ["vouchfx_site_tools"]``) and builds the same fixture with THAT module
  and with the current one, asserting byte-for-byte tree equality —
  parametrised over ``site_url`` set/unset AND a default vs. a
  curated-shaped ``docs`` list, with ``llms_summary`` always set, so the
  comparison actually exercises ``write_llms_txt``'s ordering-sensitive
  content (not just robots.txt/sitemap.xml/rendered pages) whenever
  ``site_url`` is also set. This is the only test in this file that can
  actually catch B1/B2/B3/#299 having altered the pre-existing default
  output — but only PRE-MERGE: the moment this branch merges,
  ``origin/main`` becomes this same commit, so the "baseline" is
  thereafter identical to the current module and the comparison passes
  trivially, proving nothing, until this module changes again. This is a
  deliberate trade-off (no separately pinned baseline) — see the test's
  own docstring for the full reasoning — not an ongoing backward-
  compatibility guarantee. This DOES require a full-history checkout —
  ``refs/remotes/origin/main`` must resolve — which the ``site-tools-
  tests`` CI job provides via ``fetch-depth: 0`` on its Checkout step
  (``.github/workflows/build.yml``) specifically so these cases RUN in CI
  rather than silently skip; a plain default ``fetch-depth: 1`` checkout
  (or a detached PR merge-ref checkout without that override) never has
  the ref. It ``pytest.skip``s (not fails) only for a genuinely
  incomplete LOCAL checkout — e.g. a shallow clone taken without
  ``--fetch-depth 0`` — which is an environment limitation, not evidence
  of a regression.

Every test drives the module's real public API (``SiteConfig`` + ``build()``)
end to end against a throw-away repo skeleton under ``tmp_path`` — never a
private helper — matching this suite's sibling file's own convention. Every
fixture's fact fetchers are overridden with the shared ``FACT_OVERRIDES``
constant so no test performs network I/O.

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
from vouchfx_site_tools import _doc_entry  # private helper, tested directly — see below

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

# Overrides every DEFAULT_FACT_FETCHERS entry with a fixed value. Shared by
# every fixture below (previously duplicated verbatim between
# `_base_kwargs` and `_curated_kwargs`) so the suite performs no network
# I/O and there is exactly one place to update if a new fact fetcher is
# ever added to the module.
FACT_OVERRIDES = {
    "engine_release": lambda: "v0.0.0-test",
    "sdk_version": lambda: "0.0.0-test",
    "community_jsonrpc_version": lambda: "0.0.0-test",
    "community_provider_count": lambda: "0",
}


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
        fact_overrides=FACT_OVERRIDES,
    )


def _tree(out: Path) -> dict[str, bytes]:
    return {p.relative_to(out).as_posix(): p.read_bytes() for p in sorted(out.rglob("*")) if p.is_file()}


# ---------------------------------------------------------------------------
# Shared fixture for the llms_curated (issue #299) tests below, hoisted up
# here (rather than living next to those tests) because
# test_new_fields_unset_matches_pre_merge_origin_main_byte_for_byte, further
# down, also needs a curated-shaped `docs` list to prove the cross-version
# byte-identity comparison covers ordering-sensitive llms.txt content.
# ---------------------------------------------------------------------------


def _make_curated_repo(tmp_path: Path) -> Path:
    """A repo skeleton with FOUR docs across THREE groups, deliberately
    ordered so config.docs' declaration order differs from both
    alphabetical group order and alphabetical within-group label order:
    "Zeta Group" is declared before "Alpha Group" (alphabetically the other
    way around), and within "Zeta Group", "Zeta Second" is declared before
    "Zeta Page" (alphabetically "Zeta Page" sorts first). This is what
    proves llms_curated's ordering behaviour actually follows config.docs
    rather than merely reproducing alphabetical order by coincidence. The
    fourth doc is a ROOT-level file (`README.md`, mirroring the engine's
    own real `scripts/build_site.py` DOCS list), proving `out_path`'s
    root-doc mapping (`README.md` -> `README.html`, not nested under
    `docs/`) works through this same code path."""
    root = tmp_path / "repo"
    site = root / "site"
    site.mkdir(parents=True)
    (site / "facts-fallback.json").write_text("{}", encoding="utf-8")
    (site / "index.html").write_text("<html><body>Landing</body></html>", encoding="utf-8")

    docs = root / "docs"
    docs.mkdir()
    (docs / "zeta-second.md").write_text("# Zeta Second\n\nSecond zeta page.\n", encoding="utf-8")
    (docs / "alpha-page.md").write_text("# Alpha Page\n\nAlpha page body.\n", encoding="utf-8")
    (docs / "zeta-page.md").write_text("# Zeta Page\n\nFirst zeta page.\n", encoding="utf-8")
    (root / "README.md").write_text(
        "# Project README\n\nRepository readme used only by this test fixture.\n", encoding="utf-8"
    )

    return root


# Declaration order: Zeta Group first (twice, out of alphabetical order
# relative to Alpha Group), and within Zeta Group, "Zeta Second" before
# "Zeta Page" (out of alphabetical label order). One entry (Alpha Page)
# carries a per-page description; the other three fall back to the generic
# "{meta_description_prefix} — {label}" text — exercising both branches of
# _doc_entry's optional 4th element in the same fixture. The trailing
# root-level "README.md" entry (mirroring the engine's own real DOCS list,
# see scripts/build_site.py) is declared LAST, so it also proves the
# curated group order follows first-APPEARANCE order of "Project" as a
# distinct group, not merely "root files sort last".
CURATED_DOCS: list[tuple[str, ...]] = [
    ("docs/zeta-second.md", "Zeta Group", "Zeta Second"),
    ("docs/alpha-page.md", "Alpha Group", "Alpha Page", "Custom alpha description."),
    ("docs/zeta-page.md", "Zeta Group", "Zeta Page"),
    ("README.md", "Project", "Project README"),
]


def _curated_kwargs(root: Path) -> dict[str, object]:
    return dict(
        root=root,
        default_repo="tomas-rampas/vouchfx-curated-example",
        docs=CURATED_DOCS,
        page_template=PAGE_TEMPLATE,
        portal_html=PORTAL_HTML,
        meta_description_prefix="vouchfx curated example site",
        fact_overrides=FACT_OVERRIDES,
    )


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
    try:
        spec.loader.exec_module(module)
    except Exception:
        # Do not leave a half-initialised module registered under
        # sys.modules — a later, unrelated `uuid.uuid4()` collision is
        # astronomically unlikely, but a dangling broken entry lingering
        # for the rest of the test session serves no purpose and could
        # only ever confuse a future import of the same (impossible)
        # name; pop it before propagating the original failure.
        sys.modules.pop(module_name, None)
        raise
    return module


# ---------------------------------------------------------------------------
# EDGE-002 back-compatibility: see the module docstring for why this is TWO
# tests, each proving something genuinely different.
# ---------------------------------------------------------------------------


def test_new_fields_unset_is_deterministic_and_defaults_are_pinned(tmp_path: Path) -> None:
    """Pins two things, mirroring test_site_url_contract.py's own honest
    framing: (1) the three dataclass defaults are still False/None/False —
    a silent default change would flip the sanity assertions below — and
    (2) build() is deterministic: two independent builds from equivalent
    configs (fields omitted vs. passed explicitly as their defaults)
    produce a byte-identical tree.

    This does NOT prove the new B1/B2/B3/#299 code path left the
    PRE-EXISTING module's default behaviour unchanged — both builds here
    run the identical, already-patched function bodies, so a shared bug
    (or a genuine, undocumented default-behaviour change) would pass this
    test just as easily as no bug at all. See
    test_new_fields_unset_matches_pre_merge_origin_main_byte_for_byte for
    the test that actually catches that (pre-merge only — see its own
    docstring).
    """
    root = _make_repo(tmp_path)
    out_omitted = root / "_out_omitted"
    out_explicit_default = root / "_out_explicit_default"

    config_omitted = SiteConfig(**_base_kwargs(root), site_url=SITE_URL)
    assert config_omitted.semantic_headings is False
    assert config_omitted.llms_summary is None
    assert config_omitted.llms_curated is False
    build(config_omitted, out_omitted)

    config_explicit = SiteConfig(
        **_base_kwargs(root),
        site_url=SITE_URL,
        semantic_headings=False,
        llms_summary=None,
        llms_curated=False,
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
@pytest.mark.parametrize("docs_variant", ["default", "curated"], ids=["docs-default", "docs-curated"])
def test_new_fields_unset_matches_pre_merge_origin_main_byte_for_byte(
    tmp_path: Path, site_url: str | None, docs_variant: str
) -> None:
    """The REAL cross-version EDGE-002 proof (see module docstring) — WHILE
    this branch has genuinely diverged from `origin/main`. Builds the same
    fixture with the CURRENT module (this test file's normal, top-level
    `import vouchfx_site_tools`) and with the module exactly as it existed
    at `origin/main`, extracted via `git show` into an isolated import that
    never touches `sys.modules["vouchfx_site_tools"]`, both with
    `semantic_headings`/`llms_curated` left unset (the baseline module
    predates `llms_curated` entirely, so it is never passed to either
    side — leaving it unset on the current side exercises exactly its
    False default, which is the EDGE-002 claim under test).

    Parametrized over TWO axes:

    * `site_url` set and unset, per this work order's explicit
      instruction, since `render_markdown()`'s canonical-kwarg branch and
      `write_robots_and_sitemap()` are each only exercised on one side of
      that split.
    * `docs_variant` — "default" (the original two-doc, one-group fixture)
      or "curated" (the four-doc, three-group `CURATED_DOCS` fixture used
      by the llms_curated tests below). `llms_summary` is ALWAYS set here
      (issue #299 gatekeeper fix): without it, `build()`'s own `if
      config.site_url and config.llms_summary:` guard skips
      `write_llms_txt` on BOTH sides whenever `site_url` is set, so the
      byte-identity claim would never actually touch llms.txt's
      ordering-sensitive content — only robots.txt/sitemap.xml/rendered
      pages. The "curated" variant in particular is what makes this a real
      machine check of the *default* (unsorted-input-still-alphabetised)
      llms.txt shape against `origin/main`, rather than relying solely on
      this file's own hand-transcribed golden string.

    PRE-MERGE vs. POST-MERGE, stated plainly — do not oversell what this
    proves once this branch lands: it is meaningful ONLY while
    `origin/main` still points at the commit this branch diverged from. If
    the two builds are not byte-identical at that point, this branch's
    change altered the pre-existing module's default output — exactly
    what EDGE-002 forbids, and exactly the class of bug this test exists
    to catch before merge. The MOMENT this branch merges, `origin/main`
    becomes (or fast-forwards to) this same commit, so the "baseline" this
    test extracts is thereafter byte-identical to the current module by
    construction — the comparison degenerates to comparing the module with
    itself and passes trivially, proving nothing, for every commit after
    the merge until this module changes again (at which point
    `origin/main` again differs from the working tree, and the test
    becomes meaningful again, comparing against whatever was last merged
    rather than this specific PR's starting point). This is a DELIBERATE
    trade-off, not an oversight: no separately pinned/maintained baseline
    SHA/tag — see this file's own git history for the reasoning — so this
    test's ongoing pass/fail after merge is NOT an enforced backward-
    compatibility guarantee for future changes to this module; it only
    ever verified THIS branch's changes against THIS branch's own starting
    point.

    REQUIRES a full-history checkout: `refs/remotes/origin/main` must
    resolve, which needs `fetch-depth: 0` (a plain default `fetch-depth: 1`
    checkout, or a detached PR merge-ref checkout, never has it). The
    `site-tools-tests` CI job's Checkout step sets exactly this
    (`.github/workflows/build.yml`), so these 4 parametrized cases RUN —
    not skip — in that job. Skips (does not fail) only when `origin/main`
    genuinely cannot be resolved in the checkout under test, e.g. a local
    shallow clone: that is an environment limitation, not evidence of a
    regression.
    """
    if not _git_ref_resolves(BASELINE_REF):
        pytest.skip(f"{BASELINE_REF!r} does not resolve in this checkout (shallow clone?) — skipping")

    baseline = _load_baseline_module(BASELINE_REF, INIT_REL_PATH, tmp_path)
    if baseline is None:
        pytest.skip(f"could not extract {INIT_REL_PATH!r} from {BASELINE_REF!r} — skipping")

    if docs_variant == "curated":
        root = _make_curated_repo(tmp_path / "repo_src")
        kwargs = _curated_kwargs(root)
    else:
        root = _make_repo(tmp_path / "repo_src")
        kwargs = _base_kwargs(root)

    out_current = root / "_out_current"
    out_baseline = root / "_out_baseline"

    # llms_summary set unconditionally (issue #299 MAJOR fix) so
    # write_llms_txt actually runs, on both sides, whenever site_url is
    # also set below — see the docstring above for why this matters.
    # llms_curated is deliberately NEVER set: the baseline module has no
    # such field.
    kwargs["llms_summary"] = LLMS_SUMMARY
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

    # Sanity: when site_url IS set, llms.txt must actually have been
    # produced on both sides — otherwise the byte-identity assertion above
    # would trivially pass without ever comparing llms.txt content at all.
    if site_url is not None:
        assert "llms.txt" in tree_current
        assert "llms.txt" in tree_baseline


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


# ---------------------------------------------------------------------------
# (4) llms_curated (issue #299): follow-ups from the July 2026 SEO fleet
#     wave's reviews. False (the default), or llms.txt not emitted at all,
#     stays byte-identical to today. True — only once site_url AND
#     llms_summary are ALSO set — changes three things together: a
#     grammatical portal bullet (no more "{prefix} documentation portal"
#     concatenation), the intro bullets moving under a `## Overview` H2
#     heading (llmstxt.org's file-lists-under-H2 grammar), and `##
#     {group}` sections/pages appearing in config.docs' own declaration
#     order (curated nav order) instead of being alphabetised. A
#     config.docs group literally named "Overview" raises ValueError when
#     the flag is True (see test_llms_curated_true_rejects_overview_group_
#     collision), since it would collide with that new heading.
#
#     Shared fixtures (_make_curated_repo/CURATED_DOCS/_curated_kwargs) are
#     hoisted up near _base_kwargs/_tree, above, since
#     test_new_fields_unset_matches_pre_merge_origin_main_byte_for_byte
#     also needs them.
# ---------------------------------------------------------------------------


# The FULL, verbatim llms.txt text produced by today's (pre-#299) algorithm
# for CURATED_DOCS: groups and within-group pages alphabetised, the intro
# bullets NOT under any heading, and the awkward concatenated portal
# description. Locked byte-for-byte — see
# test_llms_curated_unset_is_byte_identical_to_pre_change_output.
EXPECTED_LLMS_TXT_CURATED_UNSET = (
    "# vouchfx-curated-example\n"
    "\n"
    "> Example site is a fixture used only by this test suite.\n"
    "\n"
    "- [vouchfx-curated-example](https://example-site.vouchfx.io/): vouchfx curated example site\n"
    "- [Documentation](https://example-site.vouchfx.io/docs.html): "
    "vouchfx curated example site documentation portal\n"
    "\n"
    "## Alpha Group\n"
    "- [Alpha Page](https://example-site.vouchfx.io/docs/alpha-page.html): Custom alpha description.\n"
    "\n"
    "## Project\n"
    "- [Project README](https://example-site.vouchfx.io/README.html): "
    "vouchfx curated example site — Project README\n"
    "\n"
    "## Zeta Group\n"
    "- [Zeta Page](https://example-site.vouchfx.io/docs/zeta-page.html): "
    "vouchfx curated example site — Zeta Page\n"
    "- [Zeta Second](https://example-site.vouchfx.io/docs/zeta-second.html): "
    "vouchfx curated example site — Zeta Second\n"
)

# The FULL, verbatim llms.txt text produced with llms_curated=True for the
# SAME CURATED_DOCS fixture: the grammatical portal bullet, both intro
# bullets under "## Overview", and groups/pages in config.docs' own
# declaration order (Zeta Group, then Alpha Group, then Project; within
# Zeta Group, "Zeta Second" before "Zeta Page").
EXPECTED_LLMS_TXT_CURATED_TRUE = (
    "# vouchfx-curated-example\n"
    "\n"
    "> Example site is a fixture used only by this test suite.\n"
    "\n"
    "## Overview\n"
    "- [vouchfx-curated-example](https://example-site.vouchfx.io/): vouchfx curated example site\n"
    "- [Documentation](https://example-site.vouchfx.io/docs.html): "
    "The documentation portal for vouchfx-curated-example — every guide and reference on this site.\n"
    "\n"
    "## Zeta Group\n"
    "- [Zeta Second](https://example-site.vouchfx.io/docs/zeta-second.html): "
    "vouchfx curated example site — Zeta Second\n"
    "- [Zeta Page](https://example-site.vouchfx.io/docs/zeta-page.html): "
    "vouchfx curated example site — Zeta Page\n"
    "\n"
    "## Alpha Group\n"
    "- [Alpha Page](https://example-site.vouchfx.io/docs/alpha-page.html): Custom alpha description.\n"
    "\n"
    "## Project\n"
    "- [Project README](https://example-site.vouchfx.io/README.html): "
    "vouchfx curated example site — Project README\n"
)


def test_llms_curated_default_is_false() -> None:
    """The dataclass default — a silent default change would flip every
    other assertion in this section without any of them noticing why. A
    plain dataclass-construction check: no on-disk repo skeleton, no
    build() call — `root` is never dereferenced for this assertion."""
    config = SiteConfig(**_curated_kwargs(Path("unused")), site_url=SITE_URL, llms_summary=LLMS_SUMMARY)
    assert config.llms_curated is False


def test_llms_curated_unset_is_byte_identical_to_pre_change_output(tmp_path: Path) -> None:
    """issue #299's hard contract: leaving llms_curated unset (its False
    default) must reproduce today's output byte-for-byte, groups and all —
    the awkward concatenated portal bullet, no `## Overview` heading, and
    alphabetised group/page order. Unlike the module-docstring's EDGE-002
    cross-version mechanism (which decays to a trivial pass once this
    branch merges), this hard-coded expected string is a permanent pin."""
    root = _make_curated_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_curated_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY)
    assert config.llms_curated is False  # explicitly unset — the default path under test

    build(config, out)

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    assert llms == EXPECTED_LLMS_TXT_CURATED_UNSET


def test_llms_curated_true_yields_grammatical_overview_and_curated_order(tmp_path: Path) -> None:
    """All three issue #299 refinements together, asserted both as a full
    byte-for-byte match AND as individually named, human-readable claims —
    the latter make a future failure easy to diagnose without diffing the
    whole file."""
    root = _make_curated_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(
        **_curated_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY, llms_curated=True
    )

    build(config, out)

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    assert llms == EXPECTED_LLMS_TXT_CURATED_TRUE

    # (1) grammatical portal bullet — no "{prefix} documentation portal"
    # concatenation, but the phrase "documentation portal" and the site
    # name both still appear.
    assert "vouchfx curated example site documentation portal" not in llms
    assert "documentation portal" in llms
    assert "vouchfx-curated-example" in llms

    # (2) "## Overview" heading, holding both intro bullets, sitting above
    # the first `## {group}` section.
    overview_index = llms.index("## Overview")
    first_group_index = llms.index("## Zeta Group")
    assert overview_index < first_group_index
    overview_block = llms[overview_index:first_group_index]
    assert "[vouchfx-curated-example](https://example-site.vouchfx.io/)" in overview_block
    assert "[Documentation](https://example-site.vouchfx.io/docs.html)" in overview_block

    # (3) curated (declaration) order, not alphabetised: "Zeta Group"
    # before "Alpha Group" before "Project", and within "Zeta Group",
    # "Zeta Second" before "Zeta Page".
    zeta_group_index = llms.index("## Zeta Group")
    alpha_group_index = llms.index("## Alpha Group")
    project_group_index = llms.index("## Project")
    assert zeta_group_index < alpha_group_index < project_group_index
    zeta_second_index = llms.index("[Zeta Second]", zeta_group_index)
    zeta_page_index = llms.index("[Zeta Page]", zeta_group_index)
    assert zeta_second_index < zeta_page_index


@pytest.mark.parametrize(
    "colliding_group",
    ["Overview", "overview", "OVERVIEW", " Overview "],
    ids=["exact-case", "lowercase", "uppercase", "padded-whitespace"],
)
def test_llms_curated_true_rejects_overview_group_collision(tmp_path: Path, colliding_group: str) -> None:
    """A `config.docs` group matching "Overview" would silently produce
    two `## Overview` sections once curated mode adds its own —
    write_llms_txt raises ValueError instead, naming both the offending
    group and the full config.docs entry. The guard normalises via
    `group.strip().casefold() == "overview"` rather than an exact `==`
    comparison, so this is parametrized over an exact-case match plus
    lowercase/uppercase/padded-whitespace variants that a naive exact
    comparison would have let slip through. Only constrains the new
    opt-in path: the same group name is harmless when llms_curated is
    left at its False default (proven by the second assertion below)."""
    root = _make_curated_repo(tmp_path)
    out = root / "_out"
    kwargs = _curated_kwargs(root)
    kwargs["docs"] = [*CURATED_DOCS, ("docs/zeta-page.md", colliding_group, "Colliding entry")]

    config_curated = SiteConfig(**kwargs, site_url=SITE_URL, llms_summary=LLMS_SUMMARY, llms_curated=True)
    with pytest.raises(ValueError) as exc_info:
        build(config_curated, out)
    message = str(exc_info.value)
    assert repr(colliding_group) in message  # N1: names the offending group verbatim
    assert "'Overview'" in message  # names the colliding intro-section heading

    # Same colliding group name, llms_curated left at its False default:
    # must NOT raise — the constraint is new-opt-in-path-only.
    config_default = SiteConfig(**kwargs, site_url=SITE_URL, llms_summary=LLMS_SUMMARY)
    build(config_default, out)  # must not raise
    assert (out / "llms.txt").exists()


def test_llms_curated_true_allows_group_name_distinct_from_overview(tmp_path: Path) -> None:
    """A group name that merely CONTAINS "overview" as a substring, or
    otherwise differs from it, must NOT trip the guard — the comparison
    is a full `strip().casefold()` equality, not a substring/contains
    check. A legitimately distinct passing case, alongside the colliding
    variants above."""
    root = _make_curated_repo(tmp_path)
    out = root / "_out"
    kwargs = _curated_kwargs(root)
    kwargs["docs"] = [*CURATED_DOCS, ("docs/zeta-page.md", "Overview Extras", "Distinct entry")]

    config = SiteConfig(**kwargs, site_url=SITE_URL, llms_summary=LLMS_SUMMARY, llms_curated=True)
    build(config, out)  # must not raise

    llms = (out / "llms.txt").read_text(encoding="utf-8")
    assert "## Overview Extras" in llms


@pytest.mark.parametrize(
    "extra_kwargs",
    [
        {"llms_summary": LLMS_SUMMARY},  # site_url left unset
        {"site_url": SITE_URL},  # llms_summary left unset
    ],
    ids=["site_url-unset", "llms_summary-unset"],
)
def test_llms_curated_true_has_no_effect_when_site_url_or_summary_unset(
    tmp_path: Path, extra_kwargs: dict[str, str]
) -> None:
    """llms_curated=True does not loosen write_llms_txt's own `site_url AND
    llms_summary` guard — still no llms.txt when either is missing."""
    root = _make_curated_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_curated_kwargs(root), llms_curated=True, **extra_kwargs)

    build(config, out)

    assert not (out / "llms.txt").exists()


def test_llms_curated_true_does_not_affect_other_build_outputs(tmp_path: Path) -> None:
    """The flag's only documented effect is on write_llms_txt's output —
    sidebar, portal, sitemap, and robots.txt must be untouched. Compares
    the full output tree with llms_curated True vs False, excluding only
    llms.txt itself (which is expected, and separately proven, to differ)."""
    root = _make_curated_repo(tmp_path)
    out_false = root / "_out_false"
    out_true = root / "_out_true"

    config_false = SiteConfig(**_curated_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY)
    build(config_false, out_false)

    config_true = SiteConfig(
        **_curated_kwargs(root), site_url=SITE_URL, llms_summary=LLMS_SUMMARY, llms_curated=True
    )
    build(config_true, out_true)

    tree_false = {k: v for k, v in _tree(out_false).items() if k != "llms.txt"}
    tree_true = {k: v for k, v in _tree(out_true).items() if k != "llms.txt"}
    assert set(tree_false) == set(tree_true)
    assert tree_false == tree_true


def test_doc_entry_accepts_3_and_4_tuples_and_rejects_5() -> None:
    """`_doc_entry` validates arity explicitly (review re-review, item 2):
    a 3-tuple (rel, group, label) and a 4-tuple (+ description) both
    normalise correctly; anything else — a 5-tuple here — raises
    ValueError naming the offending entry, rather than silently truncating
    it (entry[0]/[1]/[2]/[3] would otherwise just ignore a 5th+ element)."""
    assert _doc_entry(("docs/x.md", "Group", "Label")) == ("docs/x.md", "Group", "Label", None)
    assert _doc_entry(("docs/x.md", "Group", "Label", "Desc")) == ("docs/x.md", "Group", "Label", "Desc")

    bad_entry = ("docs/x.md", "Group", "Label", "Desc", "one too many")
    with pytest.raises(ValueError) as exc_info:
        _doc_entry(bad_entry)
    message = str(exc_info.value)
    assert repr(bad_entry) in message
    assert "3" in message and "4" in message
