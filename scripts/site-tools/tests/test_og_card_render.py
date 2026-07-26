"""Regression tests for scripts/og-card/render.py's stdlib-only half
(vouchfx issue #300 gatekeeper re-review MINOR: render.py had zero tests).

Placement: this file lives under scripts/site-tools/tests/, not next to
render.py itself — the SAME reasoning as test_check_site.py's own module
docstring (see that file): CI's blocking `site-tools-tests` job already
runs `python -m pytest scripts/site-tools/tests -q` unconditionally, so
dropping this file into that existing directory rides the existing job
with ZERO workflow changes. render.py is loaded via
`importlib.util.spec_from_file_location` against its real on-disk path,
under a UUID-suffixed `sys.modules` name (mirroring
test_semantic_headings_and_llms_txt.py's `_load_baseline_module` and
test_check_site.py's `_load_check_site_module`), since it is a standalone
script two directories over (scripts/og-card/render.py), not a package
member.

render.py's own module docstring makes an explicit, load-bearing claim:
"the self-check step (dimensions/size/pixel-scan) is stdlib-only ... so it
can run, and be tested, without Playwright installed." This suite proves
that claim rather than merely repeating it: `from playwright.sync_api
import sync_playwright` is deferred INSIDE `render_png` (imported lazily,
only when actually rendering), not at module top level — confirmed here by
importing the module in this environment (which does not have playwright
installed) and asserting the import itself does not raise and
`"playwright"` never lands in `sys.modules`. Every test below drives real
functions from the module's non-rendering half: `validate_hex_colour`,
`fill_template`, `read_png_ihdr`, `scan_for_colour`, `self_check`, and
`_self_check_and_promote` (the temp-file-then-`os.replace` promotion that
stops a conformance failure from clobbering a previously committed,
conformant card at `--out` — the gatekeeper review's MINOR finding this
last group specifically regression-tests).

Run (from the repo root):
    python -m pytest scripts/site-tools/tests -q
"""

from __future__ import annotations

import importlib.util
import struct
import sys
import uuid
import zlib
from pathlib import Path

import pytest

# scripts/site-tools/tests/test_og_card_render.py -> tests -> site-tools -> scripts
RENDER_PY_PATH = Path(__file__).resolve().parents[2] / "og-card" / "render.py"

# The PNG magic bytes (PNG spec §5.2), defined independently of
# render.PNG_SIGNATURE-equivalent literal in read_png_ihdr — a fixture
# that instead imported and reused the module's own literal could pass
# even if the module under test had the wrong bytes there.
_PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# REQ-004's fixed canvas size and size cap, defined independently of
# render.CARD_WIDTH/CARD_HEIGHT/MAX_BYTES for the same reason.
_CARD_WIDTH = 1200
_CARD_HEIGHT = 630
_MAX_BYTES = 300 * 1024

_ACCENT_HEX = "#22d3ee"
_ACCENT_RGB = (0x22, 0xD3, 0xEE)


# ---------------------------------------------------------------------------
# Module loading
# ---------------------------------------------------------------------------


def _load_render_module():
    """Load scripts/og-card/render.py as an isolated module under a
    UUID-suffixed sys.modules name — see the module docstring above for
    why (mirrors test_check_site.py's `_load_check_site_module`)."""
    assert RENDER_PY_PATH.is_file(), f"expected render.py at {RENDER_PY_PATH}"
    module_name = f"_og_card_render_{uuid.uuid4().hex}"
    spec = importlib.util.spec_from_file_location(module_name, RENDER_PY_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def render():
    return _load_render_module()


def test_render_module_imports_without_playwright_installed(render) -> None:
    """Proves render.py's own docstring claim rather than merely trusting
    it: importing the module (which this fixture just did) must not
    require playwright to be installed, and playwright must not have been
    imported as a side effect of that import — i.e. the
    `from playwright.sync_api import sync_playwright` inside `render_png`
    is genuinely deferred, not accidentally hoisted to module level."""
    assert "playwright" not in sys.modules
    assert "playwright.sync_api" not in sys.modules
    # render_png itself must exist and be callable — it just must not have
    # been INVOKED (which is what would trigger the deferred import).
    assert callable(render.render_png)


# ---------------------------------------------------------------------------
# validate_hex_colour
# ---------------------------------------------------------------------------


def test_validate_hex_colour_accepts_valid_hex(render) -> None:
    assert render.validate_hex_colour("#22d3ee", "--accent") == "#22d3ee"


def test_validate_hex_colour_rejects_invalid_hex(render) -> None:
    with pytest.raises(render.ConformanceError):
        render.validate_hex_colour("not-a-colour", "--accent")


def test_validate_hex_colour_rejects_trailing_newline(render) -> None:
    """The exact gotcha the code comment calls out: `fullmatch` is used
    deliberately instead of `.match()` with a `^...$` pattern — Python's
    `$` also matches just before a single trailing newline, so a "strict"
    `^...$` pattern would let "#22d3ee\\n" through `.match()` and splice a
    newline into the CSS custom property. Pins that this rejection
    actually happens."""
    with pytest.raises(render.ConformanceError):
        render.validate_hex_colour("#22d3ee\n", "--accent")


# ---------------------------------------------------------------------------
# fill_template
# ---------------------------------------------------------------------------


def test_fill_template_html_escapes_arbitrary_text(render) -> None:
    html_text = render.fill_template(
        site_name="<b>vouchfx</b>",
        tagline="Testing & verifying",
        accent="#22d3ee",
        domain="cafe's.example",
    )
    assert "&lt;b&gt;vouchfx&lt;/b&gt;" in html_text
    assert "Testing &amp; verifying" in html_text
    assert "cafe&#x27;s.example" in html_text
    # The raw, unescaped forms must NOT appear anywhere in the output.
    assert "<b>vouchfx</b>" not in html_text
    assert "Testing & verifying" not in html_text


def test_fill_template_single_pass_does_not_resubstitute(render) -> None:
    """A --site-name value that happens to contain the literal text of
    ANOTHER token (e.g. "{{ACCENT}}") must survive as inert text, not get
    silently re-substituted by a later replacement in the same pass — the
    exact reason `fill_template` uses one `re.sub` pass via `_TOKEN_RE`
    rather than four sequential `str.replace()` calls."""
    html_text = render.fill_template(
        site_name="vouchfx {{ACCENT}}",
        tagline="a tagline",
        accent="#22d3ee",
        domain="vouchfx.io",
    )
    assert "vouchfx {{ACCENT}}" in html_text
    # The real accent hex must still have been substituted at its OWN
    # (CSS custom property) placeholder position elsewhere in the output.
    assert "--accent:  #22d3ee;" in html_text or "#22d3ee" in html_text


def test_fill_template_missing_token_drift_guard(
    render, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """template.html missing one of render.py's four expected tokens (here
    {{DOMAIN}}) must be caught as a drift regression, not silently render
    with an unfilled/absent field."""
    fixture_template = tmp_path / "template.html"
    fixture_template.write_text(
        "<div>{{SITE_NAME}}</div><p>{{TAGLINE}}</p>"
        "<span style='color:{{ACCENT}}'></span>",
        encoding="utf-8",
    )
    monkeypatch.setattr(render, "TEMPLATE_PATH", fixture_template)

    with pytest.raises(render.ConformanceError, match=r"missing placeholder"):
        render.fill_template(
            site_name="x", tagline="y", accent="#22d3ee", domain="z"
        )


def test_fill_template_unknown_token_drift_guard(
    render, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """template.html containing an extra, unrecognised {{TOKEN}} (here
    {{FOO}}) alongside all four known ones must be caught as a drift
    regression — render.py's placeholder set silently out of sync with
    the template, in the OTHER direction from the missing-token case
    above."""
    fixture_template = tmp_path / "template.html"
    fixture_template.write_text(
        "{{SITE_NAME}}{{TAGLINE}}{{ACCENT}}{{DOMAIN}}{{FOO}}",
        encoding="utf-8",
    )
    monkeypatch.setattr(render, "TEMPLATE_PATH", fixture_template)

    with pytest.raises(render.ConformanceError, match=r"\{\{FOO\}\}"):
        render.fill_template(
            site_name="x", tagline="y", accent="#22d3ee", domain="z"
        )


# ---------------------------------------------------------------------------
# read_png_ihdr
# ---------------------------------------------------------------------------


def _make_ihdr_only_bytes(
    width: int,
    height: int,
    *,
    chunk_type: bytes = b"IHDR",
    declared_length: int = 13,
    bit_depth: int = 8,
    colour_type: int = 6,
) -> bytes:
    """Signature + one chunk header + a real 13-byte IHDR payload — enough
    for `read_png_ihdr`, which never reads past byte 29. `chunk_type`/
    `declared_length` are overridable so the SAME helper builds both the
    valid case and the two malformed-header cases below."""
    payload = struct.pack(">IIBBBBB", width, height, bit_depth, colour_type, 0, 0, 0)
    header = struct.pack(">I", declared_length) + chunk_type
    return _PNG_SIGNATURE + header + payload


def test_read_png_ihdr_valid(render) -> None:
    data = _make_ihdr_only_bytes(_CARD_WIDTH, _CARD_HEIGHT, colour_type=2)
    width, height, bit_depth, colour_type, interlace = render.read_png_ihdr(data)
    assert (width, height, bit_depth, colour_type, interlace) == (
        _CARD_WIDTH,
        _CARD_HEIGHT,
        8,
        2,
        0,
    )


def test_read_png_ihdr_truncated_fails(render) -> None:
    truncated = _PNG_SIGNATURE + b"\x00\x00"  # far short of a full chunk header
    with pytest.raises(render.ConformanceError):
        render.read_png_ihdr(truncated)


def test_read_png_ihdr_24_to_28_byte_boundary_with_correct_header_fails(render) -> None:
    """Defensive mirror test: scripts/check_site.py's `_read_png_ihdr_
    dimensions` is documented as a twin of this function, and a Copilot PR
    review caught check_site.py's copy accepting a 24-28-byte file (a real
    signature, a CORRECT chunk header, a plausible width/height, but an
    INCOMPLETE 13-byte IHDR payload) because it read width/height from a
    narrower `data[16:24]` slice that didn't need the full payload.

    render.py's `read_png_ihdr` does NOT have this gap — verified here,
    not merely asserted — because it unpacks ALL FIVE IHDR fields (width,
    height, bit depth, colour type, interlace) in ONE
    `struct.unpack(">IIBBBBB", data[16:29])` call, which needs the full
    13-byte payload (through byte 29) and raises `struct.error` (caught
    and converted to ConformanceError) the instant that slice is short.
    This test pins that correctness at the EXACT same 28-byte boundary
    check_site.py's own regression test now covers, so the two parsers
    cannot silently drift apart on this point in the future."""
    width, height = _CARD_WIDTH, _CARD_HEIGHT
    real_payload = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    incomplete_payload = real_payload[:-1]  # 12 of 13 payload bytes
    header = struct.pack(">I", 13) + b"IHDR"  # a CORRECT chunk header
    truncated = _PNG_SIGNATURE + header + incomplete_payload
    assert len(truncated) == 28  # sanity: the exact 24-28-byte gap in question

    with pytest.raises(render.ConformanceError):
        render.read_png_ihdr(truncated)


def test_read_png_ihdr_non_ihdr_first_chunk_fails(render) -> None:
    data = _make_ihdr_only_bytes(_CARD_WIDTH, _CARD_HEIGHT, chunk_type=b"IDAT")
    with pytest.raises(render.ConformanceError, match=r"IHDR"):
        render.read_png_ihdr(data)


def test_read_png_ihdr_wrong_declared_length_fails(render) -> None:
    data = _make_ihdr_only_bytes(_CARD_WIDTH, _CARD_HEIGHT, declared_length=14)
    with pytest.raises(render.ConformanceError, match=r"IHDR"):
        render.read_png_ihdr(data)


# ---------------------------------------------------------------------------
# scan_for_colour
# ---------------------------------------------------------------------------


def test_scan_for_colour_present(render) -> None:
    rows = [bytes([10, 20, 30, 200, 200, 200])]  # width=2, bpp=3
    assert render.scan_for_colour(2, rows, 3, (10, 20, 30), tolerance=0) is True


def test_scan_for_colour_absent(render) -> None:
    rows = [bytes([10, 20, 30, 200, 200, 200])]
    assert render.scan_for_colour(2, rows, 3, (1, 1, 1), tolerance=0) is False


def test_scan_for_colour_tolerance_boundary(render) -> None:
    """`abs(channel_diff) <= tolerance` is inclusive: a diff of exactly
    `tolerance` matches, `tolerance + 1` does not — pins the boundary in
    both directions with the same fixture pixel."""
    rows = [bytes([10, 20, 30])]  # width=1, bpp=3
    assert render.scan_for_colour(1, rows, 3, (12, 20, 30), tolerance=2) is True  # diff==2
    assert render.scan_for_colour(1, rows, 3, (13, 20, 30), tolerance=2) is False  # diff==3


# ---------------------------------------------------------------------------
# _self_check_and_promote — temp-then-replace (gatekeeper MINOR: a
# conformance failure previously left the non-conformant PNG, or a
# clobbered committed card, sitting at --out).
# ---------------------------------------------------------------------------


def _crc32_chunk(chunk_type: bytes, data: bytes) -> bytes:
    return struct.pack(">I", zlib.crc32(chunk_type + data) & 0xFFFFFFFF)


def _make_chunk(chunk_type: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + chunk_type + data + _crc32_chunk(chunk_type, data)


def _build_full_png(
    width: int, height: int, accent_rgb: tuple[int, int, int], background_rgb: tuple[int, int, int] = (0, 0, 0)
) -> bytes:
    """A REAL, fully valid, minimal 8-bit RGB (colour type 2) PNG — a
    genuine zlib-compressed IDAT, not a hand-crafted IHDR-only stub — so
    self_check's full decode-and-pixel-scan path actually runs end to end.
    Every pixel is `background_rgb` except pixel (0, 0), which is
    `accent_rgb`."""
    ihdr_payload = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter type: None
        for x in range(width):
            raw.extend(accent_rgb if (x == 0 and y == 0) else background_rgb)
    compressed = zlib.compress(bytes(raw))
    return (
        _PNG_SIGNATURE
        + _make_chunk(b"IHDR", ihdr_payload)
        + _make_chunk(b"IDAT", compressed)
        + _make_chunk(b"IEND", b"")
    )


def test_self_check_and_promote_success_publishes_to_out_path(
    render, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    out_path = tmp_path / "og-image.png"
    tmp_render_path = tmp_path / ".og-image.png.111.tmp"
    tmp_render_path.write_bytes(_build_full_png(_CARD_WIDTH, _CARD_HEIGHT, _ACCENT_RGB))

    render._self_check_and_promote(tmp_render_path, out_path, _ACCENT_HEX)

    assert out_path.is_file()
    assert not tmp_render_path.exists()  # promoted (renamed), not copied-and-left
    captured = capsys.readouterr()
    assert str(out_path) in captured.out  # printed OK message names the REAL destination
    assert str(tmp_render_path) not in captured.out  # never the internal temp filename


def test_self_check_and_promote_failure_leaves_committed_file_untouched(
    render, tmp_path: Path
) -> None:
    """THE regression test for this fix: a conformance failure (here,
    wrong dimensions) must not clobber a previously committed, conformant
    card already sitting at out_path — before this fix, the screenshot was
    written directly to out_path and self-checked only afterwards, so by
    the time the failure was detected out_path had already been
    overwritten."""
    out_path = tmp_path / "og-image.png"
    committed_bytes = b"PRE-EXISTING COMMITTED CARD BYTES (not really a PNG, doesn't matter)"
    out_path.write_bytes(committed_bytes)

    tmp_render_path = tmp_path / ".og-image.png.222.tmp"
    tmp_render_path.write_bytes(_build_full_png(10, 8, _ACCENT_RGB))  # wrong dimensions

    with pytest.raises(render.ConformanceError):
        render._self_check_and_promote(tmp_render_path, out_path, _ACCENT_HEX)

    assert out_path.read_bytes() == committed_bytes  # untouched — NOT clobbered
    assert not tmp_render_path.exists()  # temp artifact cleaned up, not left stray


def test_self_check_and_promote_failure_creates_no_out_path_when_absent(
    render, tmp_path: Path
) -> None:
    """The other half of the same fix: when out_path does NOT already
    exist, a conformance failure must not create it either — a
    non-conforming render leaves no file at --out at all, not a
    non-conforming one."""
    out_path = tmp_path / "og-image.png"
    tmp_render_path = tmp_path / ".og-image.png.333.tmp"
    tmp_render_path.write_bytes(_build_full_png(10, 8, _ACCENT_RGB))  # wrong dimensions

    with pytest.raises(render.ConformanceError):
        render._self_check_and_promote(tmp_render_path, out_path, _ACCENT_HEX)

    assert not out_path.exists()
    assert not tmp_render_path.exists()


def test_self_check_and_promote_missing_accent_fails_without_publishing(
    render, tmp_path: Path
) -> None:
    """A card that IS the right dimensions and size but has no literal
    accent pixel anywhere — the exact #297 regression class REQ-004's
    acceptance line targets — must also fail here and must not publish."""
    out_path = tmp_path / "og-image.png"
    tmp_render_path = tmp_path / ".og-image.png.444.tmp"
    # Background-only PNG: accent_rgb == background_rgb, so no pixel
    # actually differs from the (wrong) target colour searched for below.
    tmp_render_path.write_bytes(
        _build_full_png(_CARD_WIDTH, _CARD_HEIGHT, accent_rgb=(0, 0, 0), background_rgb=(0, 0, 0))
    )

    with pytest.raises(render.ConformanceError, match=r"accent colour"):
        render._self_check_and_promote(tmp_render_path, out_path, _ACCENT_HEX)

    assert not out_path.exists()
    assert not tmp_render_path.exists()
