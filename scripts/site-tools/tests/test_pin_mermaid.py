"""Regression tests for scripts/site_hooks/pin_mermaid.py — the MkDocs
build hook that rewrites Material's unpinned unpkg.com mermaid CDN
reference, baked into the built theme bundle, to a pinned jsdelivr URL
(issue #200 / #311 / PR #320).

Before this file, `on_post_build`'s three fail-closed branches — no bundle
file found, more than one bundle file found, and the source URL's
occurrence count not matching EXPECTED_OCCURRENCES — were exercised only by
a single manual verification against a real `mkdocs build` (recorded in the
hook's own module docstring), the same gap check_site.py itself had before
scripts/site-tools/tests/test_check_site.py (issue #300, items 1-2). This
file closes it for the hook the same way: a synthesised tmp_path `_site`
directory drives the real `on_post_build` function directly, with no
dependency on a live Material build.

Loaded via the same isolated `importlib.util.spec_from_file_location`
pattern test_check_site.py's own `_load_check_site_module` uses (see that
file's own docstring for the full rationale) — pin_mermaid.py is a
standalone MkDocs hook script, not part of the vouchfx_site_tools package
this tests/ directory's sibling test files exercise, and it lives under
scripts/site_hooks/ rather than scripts/site-tools/.

Covers, against a synthesised site_dir:

  * no assets/javascripts/bundle*.min.js under site_dir — RuntimeError;
  * more than one such bundle file — RuntimeError, without rewriting
    either;
  * a single bundle that never mentions UNPKG_MERMAID_URL at all
    (occurrence count 0, not EXPECTED_OCCURRENCES) — RuntimeError;
  * a single bundle mentioning UNPKG_MERMAID_URL twice (occurrence count 2,
    not EXPECTED_OCCURRENCES) — RuntimeError;
  * the success path: a single bundle with exactly one occurrence has that
    occurrence rewritten to PINNED_MERMAID_URL, with the rest of the
    file's content left untouched.

Run (from the repo root):
    python -m pytest scripts/site-tools/tests -q
or (cwd scripts/site-tools, where [tool.pytest.ini_options] pins testpaths):
    python -m pytest -q
"""

from __future__ import annotations

import importlib.util
import sys
import uuid
from pathlib import Path

import pytest

# scripts/site-tools/tests/test_pin_mermaid.py -> tests -> site-tools -> scripts
PIN_MERMAID_PATH = Path(__file__).resolve().parents[2] / "site_hooks" / "pin_mermaid.py"


def _load_pin_mermaid_module():
    """Load scripts/site_hooks/pin_mermaid.py as an isolated module under a
    UUID-suffixed sys.modules name.

    Mirrors test_check_site.py's own `_load_check_site_module`: a fixed
    name like "pin_mermaid" would linger in sys.modules for the rest of the
    pytest session under a generic name that could later collide with (or
    be accidentally reused by) anything else that does `import pin_mermaid`
    or looks it up by that name; a UUID-suffixed name cannot.
    """
    assert PIN_MERMAID_PATH.is_file(), f"expected pin_mermaid.py at {PIN_MERMAID_PATH}"
    module_name = f"_pin_mermaid_{uuid.uuid4().hex}"
    spec = importlib.util.spec_from_file_location(module_name, PIN_MERMAID_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def pin_mermaid():
    return _load_pin_mermaid_module()


@pytest.fixture()
def site_dir(tmp_path: Path) -> Path:
    out = tmp_path / "_site"
    out.mkdir()
    return out


def _js_dir(site_dir: Path) -> Path:
    js_dir = site_dir / "assets" / "javascripts"
    js_dir.mkdir(parents=True, exist_ok=True)
    return js_dir


def _config(site_dir: Path) -> dict:
    """A minimal MkDocs `config` mapping — `on_post_build` only ever reads
    `config["site_dir"]` (see the hook's own body)."""
    return {"site_dir": str(site_dir)}


# ---------------------------------------------------------------------------
# Fail-closed branch 1: no bundle file found
# ---------------------------------------------------------------------------


def test_no_bundle_file_fails_closed(pin_mermaid, site_dir: Path) -> None:
    """Material's theme asset layout may have changed — the hook must fail
    loudly rather than silently no-op and ship an unpinned build."""
    _js_dir(site_dir)  # directory exists, but no bundle*.min.js inside it

    with pytest.raises(RuntimeError, match=r"no assets/javascripts/bundle\*\.min\.js found"):
        pin_mermaid.on_post_build(_config(site_dir))


# ---------------------------------------------------------------------------
# Fail-closed branch 2: more than one bundle file found
# ---------------------------------------------------------------------------


def test_multiple_bundle_files_fails_closed(pin_mermaid, site_dir: Path) -> None:
    """The hook cannot pick the right bundle blindly, so it must refuse
    rather than rewrite an arbitrary one — or every match."""
    js_dir = _js_dir(site_dir)
    first = js_dir / "bundle.aaaaaaaa.min.js"
    second = js_dir / "bundle.bbbbbbbb.min.js"
    first.write_text(f'watchScript("{pin_mermaid.UNPKG_MERMAID_URL}")', encoding="utf-8")
    second.write_text(f'watchScript("{pin_mermaid.UNPKG_MERMAID_URL}")', encoding="utf-8")

    with pytest.raises(RuntimeError, match=r"expected exactly one"):
        pin_mermaid.on_post_build(_config(site_dir))

    # Neither file was rewritten — the hook must refuse before touching disk.
    assert pin_mermaid.UNPKG_MERMAID_URL in first.read_text(encoding="utf-8")
    assert pin_mermaid.UNPKG_MERMAID_URL in second.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# Fail-closed branch 3: unexpected occurrence count
# ---------------------------------------------------------------------------


def test_zero_occurrences_fails_closed(pin_mermaid, site_dir: Path) -> None:
    """A single bundle exists but never mentions UNPKG_MERMAID_URL at all —
    Material may have changed how, or whether, it calls watchScript(). Must
    fail rather than silently leave a (nonexistent) reference "pinned"."""
    js_dir = _js_dir(site_dir)
    (js_dir / "bundle.deadbeef.min.js").write_text(
        "some other bundle content with no mermaid reference at all",
        encoding="utf-8",
    )

    with pytest.raises(RuntimeError, match=r"expected exactly 1 occurrence"):
        pin_mermaid.on_post_build(_config(site_dir))


def test_duplicate_occurrences_fails_closed(pin_mermaid, site_dir: Path) -> None:
    """The bundle mentions UNPKG_MERMAID_URL twice — a Material bundle
    shape this hook was never verified against (its own docstring: the
    single occurrence was confirmed against a real build). A blind
    text-replace could do the wrong thing if the two call sites ever
    diverge in meaning; fail instead of guessing."""
    js_dir = _js_dir(site_dir)
    bundle = js_dir / "bundle.deadbeef.min.js"
    bundle.write_text(
        f'watchScript("{pin_mermaid.UNPKG_MERMAID_URL}"); '
        f'watchScript("{pin_mermaid.UNPKG_MERMAID_URL}")',
        encoding="utf-8",
    )

    with pytest.raises(RuntimeError, match=r"expected exactly 1 occurrence"):
        pin_mermaid.on_post_build(_config(site_dir))

    # The failure fires before any rewrite — both occurrences are untouched.
    assert bundle.read_text(encoding="utf-8").count(pin_mermaid.UNPKG_MERMAID_URL) == 2


# ---------------------------------------------------------------------------
# Success path
# ---------------------------------------------------------------------------


def test_clean_single_bundle_rewrites_url_and_leaves_rest_untouched(
    pin_mermaid, site_dir: Path
) -> None:
    """Exactly one bundle, exactly one occurrence — the hook rewrites that
    occurrence to PINNED_MERMAID_URL and leaves the rest of the file's
    content byte-for-byte untouched."""
    js_dir = _js_dir(site_dir)
    bundle = js_dir / "bundle.deadbeef.min.js"
    bundle.write_text(
        f'before;watchScript("{pin_mermaid.UNPKG_MERMAID_URL}");after',
        encoding="utf-8",
    )

    pin_mermaid.on_post_build(_config(site_dir))  # must not raise

    rewritten = bundle.read_text(encoding="utf-8")
    assert pin_mermaid.UNPKG_MERMAID_URL not in rewritten
    assert rewritten == f'before;watchScript("{pin_mermaid.PINNED_MERMAID_URL}");after'
