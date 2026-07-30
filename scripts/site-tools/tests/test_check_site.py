"""Regression tests for scripts/check_site.py — the post-build publication
gate (vouchfx issue #300, items 1-2).

Before this file, check_site.py had zero unit tests: every one of its
checks was only ever exercised end to end, by running it against a real
`mkdocs build` output as part of a manual review or a CI deploy.

What that gap actually let through, stated accurately (an earlier version
of this paragraph got the incident wrong): before issue #300,
`check_og_image_asset` asserted ONLY the og:image/twitter:image tag's
presence, a domain-absolute URL, and the referenced file's mere
existence — no pixel-level property whatsoever, not dimensions, not size,
not content. #297's engine card was NOT the wrong size: it was already a
conformant 1200x630, 49,818-byte PNG. Its real defect was that it had no
literal accent-hex pixel anywhere in the rendered image (an anti-aliased
gradient only) — the specific thing REQ-004's acceptance line requires.
#298 fixed it with a re-render that added the literal swatch (49,818 ->
40,744 bytes), not a pixel-by-pixel image-editor hand-edit. The
dimension/size/IHDR assertions added by this branch close the class of
defect that was completely ungated before (ANY pixel-level property); the
specific literal-accent-pixel criterion that actually failed in #297 is
still not gated here — it remains tracked separately as issue #306 (see
`check_site.check_og_image_asset`'s own docstring for the same
correction, and scripts/og-card/render.py's `self_check` for where that
pixel-scan criterion actually lives today).

It is also how the sitemap-exclusion check's slug-collision
exemption (see `check_sitemap_excludes_404_and_stubs`'s own docstring, and
`_normalise_sitemap_stem`'s) came to be verified only by an ad hoc probe
matrix recorded in a commit body, rather than by anything that runs again
on every future change.

This file lives under scripts/site-tools/tests/ rather than next to
check_site.py itself. That placement is DELIBERATE, not a copy-paste
accident: CI's blocking `site-tools-tests` job already runs
`python -m pytest scripts/site-tools/tests -q` unconditionally (see
.github/workflows/*.yml), so dropping this file into that existing
directory rides the existing job with ZERO workflow changes — no new job,
no new trigger paths, nothing else to keep in sync.

check_site.py is loaded via `importlib.util.spec_from_file_location`
against its real on-disk path (`scripts/check_site.py`, resolved relative
to this file — see `CHECK_SITE_PATH`/`_load_check_site_module`), rather
than a package import: check_site.py is a standalone script, not part of
the vouchfx_site_tools package this tests/ directory's sibling test files
exercise, and it lives one directory further up
(scripts/, not scripts/site-tools/). This mirrors the isolated-module-load
pattern test_semantic_headings_and_llms_txt.py's own
`_load_baseline_module` already uses for the same reason (loading a
standalone .py file that isn't an installed/importable package member).

Three areas are covered: the two check_site.py gaps issue #300 originally
called out, plus `_check_one_image_meta`'s branches (added during the
gatekeeper re-review):

check_og_image_asset (REQ-004, specs/seo-fleet-audit.md)
    Drives the real `check_og_image_asset` function against a synthesised
    tmp_path `_site` directory whose index.html carries hand-built
    `<meta>` tags and whose "asset" files are minimal PNG byte strings
    built in-test with `struct.pack` — no imaging library, no new
    dependency. Covers: a fully conformant 1200x630 small PNG passing;
    wrong dimensions failing; an oversized (>300 KB) file failing; non-PNG
    bytes failing; a truncated file (signature but no complete IHDR)
    failing; a PNG-signed file whose first chunk isn't a well-formed IHDR
    (wrong type or declared length) failing; a mismatched or non-integer
    og:image:width/height meta failing; og:image:width/height,
    og:image:alt and twitter:card each being REQUIRED metas (REQ-004) —
    their absence, blankness (alt), or wrong value (twitter:card) all
    fail; and two distinct, independently conformant og:image/twitter:image
    assets both passing.

    `_read_site_url_prefix()` reads mkdocs.yml from the REAL repository
    root (see its own docstring) — nothing to do with any tmp_path fixture
    site directory. Rather than restructure check_site.py to make that
    injectable, the `site_url_prefix` fixture below monkeypatches the
    function itself to return a fixed test origin — the least invasive
    option available without changing the module under test.

_check_one_image_meta (shared by both og:image and twitter:image)
    Pins the pre-existing failure branches directly (absent meta tag,
    non-domain-absolute URL, missing on-disk asset) and the function's
    return contract (the resolved asset Path on success), plus a NEW
    path-escape guard: a crafted URL suffix (e.g. a ".." traversal) that
    would otherwise let `site_dir / suffix` resolve outside the built
    output entirely — and get read as if it were the social-share asset —
    is now rejected with a clear CheckFailed.

check_sitemap_excludes_404_and_stubs (REQ-007d, specs/seo-fleet-audit.md)
    Drives the real function against a synthesised sitemap.xml. Covers a
    sitemap listing a disallowed non-indexable page (404.html) failing, a
    clean sitemap passing, and — the case that actually false-positived
    during this check's own authoring — a REAL, legitimately indexable
    page whose slug collides with a legacy stub's naively-derived
    directory-slug form NOT being flagged. That collision is not
    synthesised: it is the real CHANGELOG.md -> "changelog" entry
    `build_redirect_table()` derives from this repository's own
    scripts/build_site.py DOCS list today (mirrored in
    `_normalise_sitemap_stem`'s and `check_sitemap_excludes_404_and_stubs`'s
    own docstrings), driven through the real, unmocked
    `check_site.build_redirect_table` so this test can never silently drift
    from what the check itself actually disallows.

check_mermaid_diagram_rendered (issue #311 / PR #320)
    Added alongside `check_mermaid_diagram_rendered` itself, not backfilled
    later: a synthesised page at the real AI Companion slug covers a
    correctly rendered `<pre class="mermaid">` container passing; the page
    missing entirely failing; the container absent (fence fell back to an
    ordinary `<div class="highlight">` block of raw `flowchart` source)
    failing; the container present AND a highlighted fallback ALSO present
    elsewhere on the page (a partial regression) still failing; and an
    unrelated highlighted code block that does not mention "flowchart" NOT
    false-positiving.

check_no_unpkg_mermaid_reference (issue #200 / #311 / PR #320)
    Covers a clean build (theme bundle mentions only the pinned jsdelivr
    URL) passing; a build where scripts/site_hooks/pin_mermaid.py's rewrite
    never happened (theme bundle still calls out to unpkg.com) failing; and
    a differently-cased unpkg.com reference still being caught, since a
    naive exact-case substring match would silently miss it.

Run (from the repo root):
    python -m pytest scripts/site-tools/tests -q
or (cwd scripts/site-tools, where [tool.pytest.ini_options] pins testpaths):
    python -m pytest -q
"""

from __future__ import annotations

import importlib.util
import struct
import sys
import uuid
from pathlib import Path

import pytest

# scripts/site-tools/tests/test_check_site.py -> tests -> site-tools -> scripts
CHECK_SITE_PATH = Path(__file__).resolve().parents[2] / "check_site.py"

# The PNG magic bytes (PNG spec §5.2), defined here independently of
# check_site.PNG_SIGNATURE — a fixture that instead imported and reused
# that constant could pass even if the module under test defined the wrong
# bytes, since both sides would then silently agree with each other.
_PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# REQ-004's fixed canvas size, defined independently of
# check_site.OG_IMAGE_EXPECTED_WIDTH/HEIGHT for the same reason.
_EXPECTED_WIDTH = 1200
_EXPECTED_HEIGHT = 630

# REQ-004's size cap, defined independently of check_site.OG_IMAGE_MAX_BYTES
# for the same reason: 300 KB, binary (KiB) interpretation.
_MAX_BYTES = 300 * 1024

_SITE_URL_PREFIX = "https://example.vouchfx.test/"

# The AI Companion design doc's real directory-URL slug, defined here
# independently of check_site.MERMAID_TRUST_BOUNDARY_SLUG for the same
# reason as _PNG_SIGNATURE above: a fixture that instead imported and
# reused that constant could pass even if the module under test pointed at
# the wrong page.
_MERMAID_TRUST_BOUNDARY_SLUG = "04_AI_Companion_Feasibility_and_Design"


# ---------------------------------------------------------------------------
# Module loading
# ---------------------------------------------------------------------------


def _load_check_site_module():
    """Load scripts/check_site.py as an isolated module under a
    UUID-suffixed sys.modules name.

    Uses spec_from_file_location against the real on-disk path rather than
    a package import, matching test_semantic_headings_and_llms_txt.py's
    own `_load_baseline_module` pattern for loading a standalone script
    that is not an installed package member. The UUID suffix mirrors that
    same helper's `module_name` construction: registering under a fixed
    name like "check_site" would leave it lingering in sys.modules under
    that generic name for the rest of the pytest session, available for
    (and confusable with) anything else that later does `import
    check_site` or looks it up by that name — a UUID-suffixed name can
    never collide with, or be accidentally reused by, anything else.
    """
    assert CHECK_SITE_PATH.is_file(), f"expected check_site.py at {CHECK_SITE_PATH}"
    module_name = f"_check_site_{uuid.uuid4().hex}"
    spec = importlib.util.spec_from_file_location(module_name, CHECK_SITE_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def check_site():
    return _load_check_site_module()


@pytest.fixture()
def site_url_prefix(monkeypatch: pytest.MonkeyPatch, check_site) -> str:
    """Patch `_read_site_url_prefix` to a fixed test origin.

    The real function reads mkdocs.yml from the actual repository root
    (see its own docstring) — unrelated to any tmp_path fixture site
    directory a test builds. check_site.py is not restructured to make
    this injectable (per the task's own "at most extract a pure helper"
    ceiling); monkeypatching the function itself, on the freshly-loaded
    module object, is the least invasive option that still exercises the
    real `check_og_image_asset`/`check_sitemap_excludes_404_and_stubs`
    bodies unchanged.
    """
    monkeypatch.setattr(check_site, "_read_site_url_prefix", lambda: (_SITE_URL_PREFIX, "/"))
    return _SITE_URL_PREFIX


@pytest.fixture()
def site_dir(tmp_path: Path) -> Path:
    out = tmp_path / "_site"
    out.mkdir()
    return out


# ---------------------------------------------------------------------------
# Fixture builders
# ---------------------------------------------------------------------------


def _make_png_bytes(width: int, height: int, *, min_size: int = 0) -> bytes:
    """Minimal PNG byte string: a real signature plus a real IHDR chunk
    (length, type, width, height, then IHDR's fixed remaining fields) —
    exactly what `check_site._read_png_ihdr_dimensions` reads (bytes
    [16:20] and [20:24]). No IDAT/IEND/CRC is included: check_site.py's own
    PNG handling never parses past IHDR (a real image decode is out of
    scope for a signature+dimensions+size gate), so including them would
    only obscure what is actually being exercised here.

    Optionally right-padded with zero bytes to reach `min_size` — used by
    the oversized-file test. Padding is appended AFTER the IHDR chunk, so
    it can never perturb the width/height this function encodes.
    """
    ihdr_payload = struct.pack(
        ">IIBBBBB", width, height, 8, 6, 0, 0, 0
    )  # bit depth 8, colour type 6 (RGBA), compression/filter/interlace 0 — a
    # real IHDR shape; check_site.py never reads or validates these fields.
    ihdr_chunk = struct.pack(">I", len(ihdr_payload)) + b"IHDR" + ihdr_payload + b"\x00\x00\x00\x00"
    data = _PNG_SIGNATURE + ihdr_chunk
    if len(data) < min_size:
        data += b"\x00" * (min_size - len(data))
    return data


def _write_index_html(
    site_dir: Path,
    *,
    og_image_url: str,
    twitter_image_url: str,
    og_width: str | None = None,
    og_height: str | None = None,
    og_alt: str | None = "A descriptive alt text for the social-share card.",
    twitter_card: str | None = "summary_large_image",
) -> None:
    """A minimal index.html carrying only the metas
    `check_og_image_asset`/`_MetaCollector` actually read.

    `og_width`/`og_height` default to None (omitted) — most tests either
    exercise a failure that fires before these are even reached, or supply
    them explicitly. `og_alt`/`twitter_card` default to REQ-004-conformant
    values instead, since they are the LAST checks `check_og_image_asset`
    runs: leaving them at their conformant default lets a test that is
    exercising an earlier failure (or a genuine pass) ignore them entirely,
    while a test that specifically exercises the alt/twitter:card checks
    passes an explicit (possibly None, to omit the meta) override.
    """
    metas = [
        f'<meta property="og:image" content="{og_image_url}">',
        f'<meta name="twitter:image" content="{twitter_image_url}">',
    ]
    if og_width is not None:
        metas.append(f'<meta property="og:image:width" content="{og_width}">')
    if og_height is not None:
        metas.append(f'<meta property="og:image:height" content="{og_height}">')
    if og_alt is not None:
        metas.append(f'<meta property="og:image:alt" content="{og_alt}">')
    if twitter_card is not None:
        metas.append(f'<meta name="twitter:card" content="{twitter_card}">')
    html = "<html><head>" + "".join(metas) + "</head><body>Landing</body></html>"
    (site_dir / "index.html").write_text(html, encoding="utf-8")


def _write_sitemap_xml(site_dir: Path, locs: list[str]) -> None:
    entries = "".join(f"<url><loc>{loc}</loc></url>" for loc in locs)
    xml = (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'
        f"{entries}</urlset>"
    )
    (site_dir / "sitemap.xml").write_text(xml, encoding="utf-8")


# ---------------------------------------------------------------------------
# check_og_image_asset — REQ-004 conformance
# ---------------------------------------------------------------------------


def test_conformant_1200x630_png_passes(check_site, site_url_prefix: str, site_dir: Path) -> None:
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
    )

    check_site.check_og_image_asset(site_dir)  # must not raise


def test_wrong_dimensions_fails_mentioning_dimensions(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(1200, 628))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed) as excinfo:
        check_site.check_og_image_asset(site_dir)
    message = str(excinfo.value)
    assert "1200x628" in message
    assert "1200x630" in message


def test_oversized_png_fails_mentioning_the_cap(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    oversized = _make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT, min_size=_MAX_BYTES + 1024)
    assert len(oversized) > _MAX_BYTES  # sanity: the fixture really exceeds the cap
    (site_dir / "og-image.png").write_bytes(oversized)
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed) as excinfo:
        check_site.check_og_image_asset(site_dir)
    message = str(excinfo.value)
    assert "300 KB" in message or "cap" in message
    assert str(len(oversized)) in message


def test_non_png_bytes_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    (site_dir / "og-image.png").write_bytes(b"not actually a png file, just some plain bytes")
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed, match=r"PNG"):
        check_site.check_og_image_asset(site_dir)


def test_truncated_png_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    """A real PNG signature followed by only a handful of bytes — far too
    short to contain even a complete chunk header, let alone a full IHDR
    payload (signature 8 + chunk header 8 + IHDR payload 13 = 29 bytes
    minimum for a genuinely complete IHDR chunk). Exercises the
    `len(data) < 29` guard in `_read_png_ihdr_dimensions`, distinct from
    the "not a PNG at all" and "malformed IHDR" paths covered by the tests
    either side of this one, and from the tighter 24-28-byte boundary case
    covered by the test immediately below."""
    truncated = _PNG_SIGNATURE + b"\x00\x00\x00"
    assert len(truncated) < 29  # sanity: genuinely too short to hold a complete IHDR
    (site_dir / "og-image.png").write_bytes(truncated)
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed, match=r"too short"):
        check_site.check_og_image_asset(site_dir)


def test_truncated_png_with_correct_header_but_incomplete_ihdr_payload_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """The exact gap a Copilot PR review caught: a 24-28-byte file — a
    real signature, a CORRECT chunk header (type b"IHDR", declared length
    13), and a plausible width/height — but whose 13-byte IHDR payload is
    cut off before its final byte(s) (bit depth/colour type/compression/
    filter/interlace) and its CRC.

    Because width/height alone only need bytes [16:24] (24 bytes total),
    a `len(data) < 24` guard would have let this through: `struct.unpack`
    for width/height succeeds purely by coincidence, so the corrupt,
    truncated file would have shipped as if it were a genuinely complete,
    valid IHDR chunk. The 29-byte minimum-length guard must reject this
    BEFORE ever reaching the chunk-header/dimension checks. Built at
    exactly 28 bytes — one byte short of the 29-byte minimum — the
    tightest possible boundary probe of the fix."""
    real_payload = struct.pack(">IIBBBBB", _EXPECTED_WIDTH, _EXPECTED_HEIGHT, 8, 6, 0, 0, 0)
    incomplete_payload = real_payload[:-1]  # 12 of 13 payload bytes — missing only "interlace"
    header = struct.pack(">I", 13) + b"IHDR"  # a CORRECT chunk header, unlike the sibling test
    truncated = _PNG_SIGNATURE + header + incomplete_payload
    assert len(truncated) == 28  # sanity: exactly the 24-28-byte gap Copilot identified
    (site_dir / "og-image.png").write_bytes(truncated)
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed, match=r"too short"):
        check_site.check_og_image_asset(site_dir)


def test_png_with_non_ihdr_first_chunk_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    """A real PNG signature, long enough (>=24 bytes) to pass the
    truncation guard, but whose first chunk is NOT a well-formed IHDR
    (wrong chunk type, here "IDAT" — a chunk type that IS a real PNG chunk
    type, just never legally the first one). Without asserting the chunk
    header, `_read_png_ihdr_dimensions` would silently misread bytes
    [16:24] of this payload as a garbage width/height instead of failing
    with a clear reason — the exact gap the gatekeeper review's MINOR
    finding on this function called out."""
    fake_chunk = struct.pack(">I", 13) + b"IDAT" + b"\x00" * 13 + b"\x00\x00\x00\x00"
    malformed = _PNG_SIGNATURE + fake_chunk
    assert len(malformed) >= 24  # sanity: not caught by the truncation guard instead
    (site_dir / "og-image.png").write_bytes(malformed)
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed, match=r"malformed IHDR"):
        check_site.check_og_image_asset(site_dir)


def test_png_with_wrong_ihdr_declared_length_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """The OTHER half of the IHDR chunk-header guard: the chunk type IS
    the correct b"IHDR" this time, but its declared length is wrong (14
    instead of the spec-mandated 13). `test_png_with_non_ihdr_first_chunk_
    fails` above only exercises the chunk-TYPE half of the `chunk_type !=
    b"IHDR" or length != 13` guard; this test exercises the length half
    independently, so a regression that dropped either half of the `or`
    would still be caught."""
    payload = b"\x00" * 14  # one byte too many for a real IHDR payload
    wrong_length_chunk = struct.pack(">I", len(payload)) + b"IHDR" + payload + b"\x00\x00\x00\x00"
    malformed = _PNG_SIGNATURE + wrong_length_chunk
    assert len(malformed) >= 24  # sanity: not caught by the truncation guard instead
    (site_dir / "og-image.png").write_bytes(malformed)
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
    )

    with pytest.raises(check_site.CheckFailed, match=r"malformed IHDR"):
        check_site.check_og_image_asset(site_dir)


def test_og_image_meta_dimensions_disagreeing_with_ihdr_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT - 1),  # stale/wrong claim vs the real IHDR
    )

    with pytest.raises(check_site.CheckFailed) as excinfo:
        check_site.check_og_image_asset(site_dir)
    message = str(excinfo.value)
    assert "og:image:height" in message
    assert str(_EXPECTED_HEIGHT - 1) in message
    assert str(_EXPECTED_HEIGHT) in message


def test_og_image_width_meta_absent_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    """og:image:width is a REQUIRED meta (REQ-004), not merely nice-to-
    cross-check-when-present — supersedes what used to be
    test_og_image_meta_dimensions_absent_does_not_fail before the
    gatekeeper review's REQ-004 presence-enforcement finding: absence is
    now itself a failure."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=None,
        og_height=str(_EXPECTED_HEIGHT),
    )

    with pytest.raises(check_site.CheckFailed, match=r"og:image:width"):
        check_site.check_og_image_asset(site_dir)


def test_og_image_height_meta_absent_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    """og:image:height is likewise REQUIRED (REQ-004); checked
    independently of the width case above since
    `_check_og_image_meta_dimensions_match` iterates the two metas
    separately and either could regress without the other."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=None,
    )

    with pytest.raises(check_site.CheckFailed, match=r"og:image:height"):
        check_site.check_og_image_asset(site_dir)


@pytest.mark.parametrize(
    "raw_width",
    [
        "1200px",  # a stray unit suffix
        "1_200",  # PEP 515 underscore digit grouping — int("1_200") == 1200 in real Python
        "١٢٠٠",  # Arabic-Indic digits for "1200" — int() accepts these too
    ],
    ids=["unit-suffix", "underscore-grouping", "non-ascii-digits"],
)
def test_og_image_width_meta_non_integer_fails(
    check_site, site_url_prefix: str, site_dir: Path, raw_width: str
) -> None:
    """og:image:width present but not a plain ASCII-digit integer.
    Exercises the ASCII-digit `fullmatch` guard in
    `_check_og_image_meta_dimensions_match`, distinct from both the
    absent-meta and the wrong-value cases covered elsewhere. The latter
    two parametrized values are deliberately NOT things a bare
    `int(raw)` would reject: Python's int() constructor accepts both PEP
    515 underscore grouping and any Unicode decimal-digit script, neither
    of which is "a plain pixel count" as REQ-004 intends — this is the
    gap the gatekeeper review's MINOR finding on this parsing called
    out."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=raw_width,
        og_height=str(_EXPECTED_HEIGHT),
    )

    with pytest.raises(check_site.CheckFailed, match=r"not a plain integer") as excinfo:
        check_site.check_og_image_asset(site_dir)
    assert "og:image:width" in str(excinfo.value)


@pytest.mark.parametrize("og_alt", [None, "   "], ids=["absent", "blank"])
def test_og_image_alt_meta_absent_or_blank_fails(
    check_site, site_url_prefix: str, site_dir: Path, og_alt: str | None
) -> None:
    """og:image:alt is REQUIRED (REQ-004) and must be non-empty once
    stripped of whitespace — a bare "   " content attribute is just as
    unhelpful to an accessibility tool as a missing meta, so both are
    covered by this one parametrized test."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
        og_alt=og_alt,
    )

    with pytest.raises(check_site.CheckFailed, match=r"og:image:alt"):
        check_site.check_og_image_asset(site_dir)


def test_twitter_card_meta_absent_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
        twitter_card=None,
    )

    with pytest.raises(check_site.CheckFailed, match=r"twitter:card"):
        check_site.check_og_image_asset(site_dir)


def test_twitter_card_meta_wrong_value_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
        twitter_card="summary",  # a real twitter:card value, just not the large-image one
    )

    with pytest.raises(check_site.CheckFailed) as excinfo:
        check_site.check_og_image_asset(site_dir)
    message = str(excinfo.value)
    assert "twitter:card" in message
    assert "summary_large_image" in message


def test_twitter_card_meta_with_incidental_whitespace_passes(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """A stray leading/trailing space around an otherwise-correct
    twitter:card value must not fail the check — only the VALUE matters,
    not incidental whitespace. Guards the gatekeeper NIT fix: before it,
    this would have failed with a confusing "'summary_large_image '"
    message even though the meta is, for all practical purposes, correct."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
        twitter_card=" summary_large_image ",  # leading AND trailing whitespace
    )

    check_site.check_og_image_asset(site_dir)  # must not raise


def test_twitter_card_meta_wrong_value_error_shows_raw_value(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """When twitter:card is genuinely wrong, the CheckFailed message must
    show the RAW, unstripped content attribute value — so a
    whitespace-related typo (e.g. a stray trailing space someone thought
    made it "close enough") stays visible in the diagnostic instead of
    being silently cleaned up before being reported, which would make the
    discrepancy invisible."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "og-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
        twitter_card="summary ",  # wrong value, WITH a trailing space
    )

    with pytest.raises(check_site.CheckFailed) as excinfo:
        check_site.check_og_image_asset(site_dir)
    assert "'summary '" in str(excinfo.value)  # raw, unstripped value stays visible


def test_distinct_og_and_twitter_assets_must_both_individually_pass(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """og:image and twitter:image referencing two DIFFERENT, independently
    conformant files: proves the dedup-by-Path logic in
    check_og_image_asset (keyed on the resolved asset Path, not the meta
    key) only skips re-validating an asset when it is truly the SAME path
    under both keys, and otherwise validates each one. NOTE: this test
    alone cannot catch a regression that silently skipped the second
    file's check entirely — both assets here are valid, so such a bug
    would leave this test green too. See
    test_distinct_twitter_asset_wrong_dimensions_still_fails immediately
    below for the test that actually catches that (a broken twitter:image
    asset that must still be caught even though og:image is fine)."""
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    (site_dir / "twitter-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "twitter-image.png",
        og_width=str(_EXPECTED_WIDTH),
        og_height=str(_EXPECTED_HEIGHT),
    )

    check_site.check_og_image_asset(site_dir)  # must not raise


def test_distinct_twitter_asset_wrong_dimensions_still_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    (site_dir / "og-image.png").write_bytes(_make_png_bytes(_EXPECTED_WIDTH, _EXPECTED_HEIGHT))
    (site_dir / "twitter-image.png").write_bytes(_make_png_bytes(600, 315))
    _write_index_html(
        site_dir,
        og_image_url=site_url_prefix + "og-image.png",
        twitter_image_url=site_url_prefix + "twitter-image.png",
    )

    with pytest.raises(check_site.CheckFailed, match=r"600x315"):
        check_site.check_og_image_asset(site_dir)


# ---------------------------------------------------------------------------
# _check_one_image_meta — pins the pre-existing failure branches (this
# change altered the function's return contract from None to the resolved
# Path, so its behaviour is worth pinning directly, not only indirectly via
# check_og_image_asset above) plus the new path-escape guard.
# ---------------------------------------------------------------------------


def test_check_one_image_meta_absent_tag_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    with pytest.raises(check_site.CheckFailed, match=r"og:image"):
        check_site._check_one_image_meta(
            site_dir, site_dir / "index.html", {}, "og:image", site_url_prefix
        )


def test_check_one_image_meta_non_absolute_url_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    metas = {"og:image": "og-image.png"}  # relative, not domain-absolute
    with pytest.raises(check_site.CheckFailed, match=r"absolute"):
        check_site._check_one_image_meta(
            site_dir, site_dir / "index.html", metas, "og:image", site_url_prefix
        )


def test_check_one_image_meta_missing_asset_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    metas = {"og:image": site_url_prefix + "does-not-exist.png"}
    with pytest.raises(check_site.CheckFailed, match=r"does not exist"):
        check_site._check_one_image_meta(
            site_dir, site_dir / "index.html", metas, "og:image", site_url_prefix
        )


def test_check_one_image_meta_success_returns_resolved_asset_path(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """Pins the function's RETURN contract directly: on success it returns
    the resolved on-disk asset Path (not None, which is what it returned
    before check_og_image_asset needed to reuse it for PNG conformance
    checks)."""
    asset = site_dir / "og-image.png"
    asset.write_bytes(b"fake-but-present")
    metas = {"og:image": site_url_prefix + "og-image.png"}
    result = check_site._check_one_image_meta(
        site_dir, site_dir / "index.html", metas, "og:image", site_url_prefix
    )
    assert result == asset


def test_check_one_image_meta_rejects_path_escaping_site_dir(
    check_site, site_url_prefix: str, tmp_path: Path
) -> None:
    """A crafted og:image URL whose suffix (after stripping the
    site-absolute prefix) escapes `site_dir` — here via a ".." traversal —
    must be rejected, not silently resolved to (and, by a later caller,
    read as) a file OUTSIDE the built output. `site_dir / suffix` alone
    does not guarantee containment: a suffix with ".." segments, or one
    pathlib treats as already "anchored" (e.g. a platform-specific
    absolute-looking form), makes `/` climb out of or entirely discard
    the left operand.

    Deliberately uses a portable ".." traversal into a sibling directory
    rather than a real OS file (e.g. C:\\Windows\\win.ini, the concrete
    example that surfaced this gap) — this test also runs on CI's
    ubuntu-latest, where no such path exists, and a fixture-created
    "secret" file exercises exactly the same containment failure without
    depending on anything OS-specific being present.
    """
    site_dir = tmp_path / "_site"
    site_dir.mkdir()
    outside_dir = tmp_path / "outside"
    outside_dir.mkdir()
    secret = outside_dir / "secret.png"
    secret.write_bytes(b"should never be read by check_og_image_asset")

    escaped_url = site_url_prefix + "../outside/secret.png"
    metas = {"og:image": escaped_url}

    with pytest.raises(check_site.CheckFailed, match=r"OUTSIDE") as excinfo:
        check_site._check_one_image_meta(
            site_dir, site_dir / "index.html", metas, "og:image", site_url_prefix
        )
    # The secret file's own path appears in the failure message (proving
    # the escape was actually detected against THIS file, not some other
    # coincidental rejection reason such as "does not exist").
    assert str(secret.resolve()) in str(excinfo.value)


# ---------------------------------------------------------------------------
# check_sitemap_excludes_404_and_stubs — REQ-007d, incl. the slug-collision
# exemption that false-positived during this check's own authoring.
# ---------------------------------------------------------------------------


def test_sitemap_listing_404_fails(check_site, site_url_prefix: str, site_dir: Path) -> None:
    _write_sitemap_xml(site_dir, [f"{site_url_prefix}404/"])

    with pytest.raises(check_site.CheckFailed, match=r"non-indexable"):
        check_site.check_sitemap_excludes_404_and_stubs(site_dir)


def test_sitemap_listing_a_legacy_redirect_stub_fails(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """docs/getting-started.html is a real legacy-redirect stub path (see
    build_redirect_table(), derived from build_site.py's own DOCS list) —
    listing its literal stub URL in the sitemap must fail."""
    _write_sitemap_xml(site_dir, [f"{site_url_prefix}docs/getting-started.html"])

    with pytest.raises(check_site.CheckFailed, match=r"non-indexable"):
        check_site.check_sitemap_excludes_404_and_stubs(site_dir)


def test_clean_sitemap_passes(check_site, site_url_prefix: str, site_dir: Path) -> None:
    _write_sitemap_xml(
        site_dir,
        [
            f"{site_url_prefix}",
            f"{site_url_prefix}getting-started/",
            f"{site_url_prefix}recipes/",
        ],
    )

    check_site.check_sitemap_excludes_404_and_stubs(site_dir)  # must not raise


def test_real_page_colliding_with_legacy_stub_slug_is_not_flagged(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """The slug-collision exemption (see `_normalise_sitemap_stem`'s and
    `check_sitemap_excludes_404_and_stubs`'s own docstrings): the legacy
    stub CHANGELOG.html maps to the real target slug "changelog"
    (`_ROOT_FILE_SLUGS` in scripts/site_hooks/_redirect_table.py) — the
    SAME string CHANGELOG.html's own naive directory-slug form
    ("changelog.html" minus ".html") would otherwise produce. Without the
    exemption, listing the real, legitimately indexable "changelog/" page
    in the sitemap would be wrongly flagged as if it were the stub itself.

    Driven through the REAL, unmocked `check_site.build_redirect_table` —
    not a synthesised redirect table — so this test tracks whatever this
    repository's actual build_site.py DOCS list produces, exactly as
    `check_sitemap_excludes_404_and_stubs` itself does.
    """
    table = dict(check_site.build_redirect_table())
    assert table.get("CHANGELOG.html") == "changelog", (
        "expected CHANGELOG.html -> 'changelog' in the real redirect table; "
        "this test's whole premise (the slug-collision exemption) depends on it"
    )

    _write_sitemap_xml(site_dir, [f"{site_url_prefix}changelog/"])

    check_site.check_sitemap_excludes_404_and_stubs(site_dir)  # must not raise


def test_literal_changelog_stub_path_is_still_disallowed(
    check_site, site_url_prefix: str, site_dir: Path
) -> None:
    """The exemption above is narrow: it only exempts the real target's
    OWN directory-slug form ("changelog"). The literal stub path itself
    ("CHANGELOG.html") must still be disallowed unconditionally — it is
    never itself an indexable page, collision or not."""
    _write_sitemap_xml(site_dir, [f"{site_url_prefix}CHANGELOG.html"])

    with pytest.raises(check_site.CheckFailed, match=r"non-indexable"):
        check_site.check_sitemap_excludes_404_and_stubs(site_dir)


# ---------------------------------------------------------------------------
# check_mermaid_diagram_rendered — issue #311 / PR #320
# ---------------------------------------------------------------------------


def _write_mermaid_page(site_dir: Path, *, body: str) -> None:
    """A minimal built page at the real AI Companion slug
    (`_MERMAID_TRUST_BOUNDARY_SLUG`), with `body` spliced in as the
    section 3.3 content `check_mermaid_diagram_rendered` inspects."""
    page_dir = site_dir / _MERMAID_TRUST_BOUNDARY_SLUG
    page_dir.mkdir(parents=True, exist_ok=True)
    html = f"<html><body><h3>3.3 Trust boundary</h3>{body}</body></html>"
    (page_dir / "index.html").write_text(html, encoding="utf-8")


def test_rendered_mermaid_container_passes(check_site, site_dir: Path) -> None:
    _write_mermaid_page(
        site_dir,
        body='<pre class="mermaid"><code>flowchart LR\n    A --&gt; B\n</code></pre>',
    )

    check_site.check_mermaid_diagram_rendered(site_dir)  # must not raise


def test_missing_page_fails(check_site, site_dir: Path) -> None:
    with pytest.raises(check_site.CheckFailed, match=r"does not exist"):
        check_site.check_mermaid_diagram_rendered(site_dir)


def test_raw_flowchart_source_without_mermaid_container_fails(
    check_site, site_dir: Path
) -> None:
    """The regression this check exists to catch: a broken custom_fences
    mapping (or a fence whose language tag drifted off 'mermaid') falls
    back to pymdownx.highlight's ordinary wrapper, rendering the raw source
    as an innocuous-looking code block — no mermaid container anywhere on
    the page."""
    _write_mermaid_page(
        site_dir,
        body=(
            '<div class="highlight"><pre><span></span>'
            "<code>flowchart LR\n    A --&gt; B\n</code></pre></div>"
        ),
    )

    with pytest.raises(check_site.CheckFailed, match=r'<pre class="mermaid">'):
        check_site.check_mermaid_diagram_rendered(site_dir)


def test_mermaid_container_alongside_raw_fallback_still_fails(
    check_site, site_dir: Path
) -> None:
    """A partial regression: the real mermaid container is present, but a
    highlighted code block elsewhere on the page ALSO still carries the
    raw 'flowchart' source. The container's mere presence must not be
    enough to pass."""
    _write_mermaid_page(
        site_dir,
        body=(
            '<pre class="mermaid"><code>flowchart LR\n    A --&gt; B\n</code></pre>'
            '<div class="highlight"><pre><span></span>'
            "<code>flowchart LR\n    A --&gt; B\n</code></pre></div>"
        ),
    )

    with pytest.raises(check_site.CheckFailed, match=r"highlighted code block"):
        check_site.check_mermaid_diagram_rendered(site_dir)


def test_unrelated_highlight_block_does_not_false_positive(
    check_site, site_dir: Path
) -> None:
    """An ordinary, unrelated highlighted code block (e.g. a snippet
    elsewhere on the same page) must not trip the raw-fallback check
    merely for existing — only one that actually contains the word
    'flowchart' should."""
    _write_mermaid_page(
        site_dir,
        body=(
            '<pre class="mermaid"><code>flowchart LR\n    A --&gt; B\n</code></pre>'
            '<div class="highlight"><pre><span></span>'
            "<code>var x = 1;</code></pre></div>"
        ),
    )

    check_site.check_mermaid_diagram_rendered(site_dir)  # must not raise


# ---------------------------------------------------------------------------
# check_no_unpkg_mermaid_reference — issue #200 / #311 / PR #320
# ---------------------------------------------------------------------------


def test_clean_build_has_no_unpkg_reference(check_site, site_dir: Path) -> None:
    """A build where scripts/site_hooks/pin_mermaid.py did its job — the
    theme bundle only mentions the pinned jsdelivr URL — must pass."""
    js_dir = site_dir / "assets" / "javascripts"
    js_dir.mkdir(parents=True)
    (js_dir / "bundle.deadbeef.min.js").write_text(
        'watchScript("https://cdn.jsdelivr.net/npm/mermaid@11.16.0/dist/mermaid.min.js")',
        encoding="utf-8",
    )

    check_site.check_no_unpkg_mermaid_reference(site_dir)  # must not raise


def test_unpinned_unpkg_reference_fails(check_site, site_dir: Path) -> None:
    """The regression this check exists to catch: pin_mermaid.py didn't run
    (or was bypassed), and the theme bundle still calls out to unpkg.com's
    unpinned mermaid CDN."""
    js_dir = site_dir / "assets" / "javascripts"
    js_dir.mkdir(parents=True)
    (js_dir / "bundle.deadbeef.min.js").write_text(
        'watchScript("https://unpkg.com/mermaid@11/dist/mermaid.min.js")',
        encoding="utf-8",
    )

    with pytest.raises(check_site.CheckFailed, match=r"unpkg\.com"):
        check_site.check_no_unpkg_mermaid_reference(site_dir)


def test_unpkg_reference_match_is_case_insensitive(check_site, site_dir: Path) -> None:
    """A CDN hostname is case-insensitive DNS regardless of how a build
    tool happens to case it — a differently-cased reference must still be
    caught, not silently ignored by an exact-case substring match."""
    js_dir = site_dir / "assets" / "javascripts"
    js_dir.mkdir(parents=True)
    (js_dir / "bundle.deadbeef.min.js").write_text(
        'watchScript("https://UNPKG.COM/Mermaid@11/dist/mermaid.min.js")',
        encoding="utf-8",
    )

    with pytest.raises(check_site.CheckFailed, match=r"unpkg\.com"):
        check_site.check_no_unpkg_mermaid_reference(site_dir)
