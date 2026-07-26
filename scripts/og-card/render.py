#!/usr/bin/env python3
"""Render a vouchfx fleet og:image social card from template.html.

This is the committed source for the five hand-produced 1200x630 social
cards (`site/og-image.png` in each fleet repo) tracked as issue #300 item 3.
PR #298 hand-patched the engine card's missing accent swatch because there
was no source to regenerate from; this script plus template.html are that
source going forward.

Usage (engine defaults shown; closely approximates the current engine card
— see the "Reproduction fidelity" caveat below and in README.md):

    pip install playwright
    playwright install chromium
    python scripts/og-card/render.py --force

Per-site usage (see README.md for the full per-site table):

    python scripts/og-card/render.py \\
        --site-name "vouchfx samples" \\
        --tagline "Four production-shaped applications in four stacks..." \\
        --domain samples.vouchfx.io \\
        --out /path/to/vouchfx-samples/site/og-image.png

What it does:
  1. Fills the {{PLACEHOLDER}} tokens in template.html (see that file's
     header comment for the exact placeholder set).
  2. Opens the filled markup in headless Chromium at exactly 1200x630
     (device_scale_factor=1) via Playwright and screenshots it to a
     TEMPORARY sibling file next to --out (never --out itself).
     Refuses to overwrite an existing --out file unless --force is passed
     — checked up front, before this step.
  3. Self-checks the temporary screenshot and reports conformance with
     REQ-004:
       - IHDR dimensions are exactly 1200x630;
       - file size is <= 300 KB;
       - the --accent colour is actually present in the rendered pixels
         (a full scan of every decoded pixel, not a sample).
     Also asserts, before the screenshot is taken, that the rendered
     content did not overflow the 1200x630 box (template.html's
     `overflow: hidden` would otherwise silently clip long text instead of
     failing loudly).
     Only once the self-check PASSES is the temporary file promoted to
     --out (an atomic `os.replace()`). Any failure exits non-zero with a
     clear message, the temporary file is removed, and --out is left
     completely untouched — a non-conforming render can never clobber a
     previously committed, conformant card, `--force` or not.

Reproduction fidelity: this closely approximates the committed cards, not a
byte-for-byte reproduction. template.html uses the `system-ui` font-stack
that site/styles.css itself ships with, and exact glyph metrics (advance
widths, hinting, kerning) vary with the OS/Chromium font substitution on
the machine running this script — the same markup can render with slightly
different line-wrap points on a different machine. Layout widths in
template.html were sized against measurements of the committed
site/og-image.png (see its "MAJOR fix" comment on `.card__tagline`) and
re-verified with a Playwright MCP browser screenshot, but this is not a
guarantee across every OS/Chromium combination — see README.md's
"Reproduction fidelity" section.

Dependencies: Python 3.9+ stdlib, plus the `playwright` pip package for the
render step only. The self-check step (dimensions/size/pixel-scan) is
stdlib-only (zlib + struct decode a PNG's IDAT stream directly) so it can
run, and be tested, without Playwright installed.
"""
from __future__ import annotations

import argparse
import html
import os
import re
import struct
import sys
import zlib
from pathlib import Path

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

SCRIPT_DIR = Path(__file__).resolve().parent
TEMPLATE_PATH = SCRIPT_DIR / "template.html"

CARD_WIDTH = 1200
CARD_HEIGHT = 630
MAX_BYTES = 300 * 1024  # REQ-004: PNG MUST be <= 300 KB.
COLOUR_TOLERANCE = 2  # +/- per RGB channel when matching --accent in the scan.

# Engine defaults — regenerating with no flags closely approximates the
# current engine card (see the module docstring's "Reproduction fidelity"
# note; not a byte-for-byte guarantee). Sourced from specs/seo-fleet-audit.md
# REQ-004, site/index.html's og:image:alt, and site/styles.css's --cyan
# token. Note --out defaults to a path that already has a committed card
# (this repo's own site/og-image.png), so a bare run also needs --force.
DEFAULT_SITE_NAME = "vouchfx"
DEFAULT_TAGLINE = "End-to-end testing for distributed systems, authored in YAML."
DEFAULT_ACCENT = "#22d3ee"
DEFAULT_DOMAIN = "vouchfx.io"
# Repo root is two levels up from this file (scripts/og-card/render.py).
DEFAULT_OUT = SCRIPT_DIR.parents[1] / "site" / "og-image.png"

HEX_COLOUR_RE = re.compile(r"#[0-9a-fA-F]{6}")
PLACEHOLDER_TOKENS = ("{{SITE_NAME}}", "{{TAGLINE}}", "{{ACCENT}}", "{{DOMAIN}}")
# Matches any one of the four known tokens, for a single-pass substitution
# (see fill_template).
_TOKEN_RE = re.compile("|".join(re.escape(t) for t in PLACEHOLDER_TOKENS))


class ConformanceError(SystemExit):
    """Raised (as a SystemExit subclass) for any REQ-004 non-conformance or
    malformed input — every raise site sets a clear, actionable message and
    a non-zero exit code."""

    def __init__(self, message: str) -> None:
        super().__init__(f"error: {message}")


# ---------------------------------------------------------------------------
# Template filling
# ---------------------------------------------------------------------------


def validate_hex_colour(value: str, flag: str) -> str:
    # fullmatch, not `^...$` with .match(): Python's `$` also matches just
    # before a single trailing newline, so a "strict" ^...$ pattern would
    # let "#22d3ee\n" through match() and splice a newline into the CSS
    # custom property. fullmatch requires the ENTIRE string to match with
    # no anchors needed.
    if not HEX_COLOUR_RE.fullmatch(value):
        raise ConformanceError(
            f"{flag} must be a 6-digit hex colour like #22d3ee (got {value!r})"
        )
    return value


def fill_template(site_name: str, tagline: str, accent: str, domain: str) -> str:
    """Substitute the four {{PLACEHOLDER}} tokens in template.html.

    site_name/tagline/domain are HTML-escaped (they are arbitrary text);
    accent is validated as a strict 6-digit hex colour and spliced verbatim
    into a CSS custom property, which is the only placeholder position that
    is not inside a text node.
    """
    validate_hex_colour(accent, "--accent")
    text = TEMPLATE_PATH.read_text(encoding="utf-8")

    # Drift guard, checked against the RAW template — deliberately BEFORE
    # substitution. Checking the substituted OUTPUT for leftover
    # "{{...}}"-shaped text (as an earlier version of this function did)
    # false-positives on legitimate user input: a --site-name of
    # "vouchfx {{ACCENT}}" is valid, inert text once HTML-escaped, and
    # must be allowed to survive in the output untouched (see the
    # single-pass substitution note below). Checking the template file
    # itself instead answers the real question — did template.html and
    # this function's PLACEHOLDER_TOKENS drift apart? — without depending
    # on what any particular caller passes in.
    missing = [token for token in PLACEHOLDER_TOKENS if token not in text]
    if missing:
        raise ConformanceError(
            f"template.html is missing placeholder(s) {missing!r} that render.py expects "
            "to fill — render.py's placeholder set is out of sync with the template"
        )
    # [^}]+ , not [A-Z_]+: the narrower class would silently miss a
    # mis-cased or digit-bearing token (e.g. "{{Site_Name}}", "{{V2}}") that
    # a template edit introduced by mistake — this check exists precisely
    # to catch that kind of drift, so it must not assume the token it's
    # looking for is well-formed.
    unknown = sorted(set(re.findall(r"\{\{[^}]+\}\}", text)) - set(PLACEHOLDER_TOKENS))
    if unknown:
        raise ConformanceError(
            f"template.html contains placeholder(s) {unknown!r} that render.py does not "
            "know how to fill — render.py's placeholder set is out of sync with the template"
        )

    replacements = {
        "{{SITE_NAME}}": html.escape(site_name),
        "{{TAGLINE}}": html.escape(tagline),
        "{{ACCENT}}": accent,
        "{{DOMAIN}}": html.escape(domain),
    }
    # Single-pass substitution via re.sub, NOT four sequential str.replace()
    # calls: re.sub scans the template text once and never re-scans a
    # replacement's own output, so a --site-name/--tagline/--domain value
    # that happens to contain the literal text of another token (e.g.
    # --site-name "vouchfx {{ACCENT}}") is inserted as inert text and
    # cannot get silently re-substituted by a later replacement in the pass.
    return _TOKEN_RE.sub(lambda m: replacements[m.group(0)], text)


# ---------------------------------------------------------------------------
# Rendering (requires the playwright pip package)
# ---------------------------------------------------------------------------


def render_png(html_text: str, out_path: Path, *, force: bool) -> Path:
    """Screenshot `html_text` and return the path of a TEMPORARY sibling
    file — never `out_path` itself. `out_path` is not touched by this
    function at all beyond the exists/--force guard below; the caller
    (`render_and_self_check`) only promotes the temp file to `out_path`,
    via `os.replace()`, after `self_check` has passed. This is what stops
    a conformance failure from leaving a non-conformant PNG at `out_path`:
    previously the screenshot was written directly to `out_path` and
    self-checked only afterwards, so by the time a failure (wrong
    dimensions, oversized, missing accent) was detected, `out_path` —
    including a previously committed, conformant card, if `--force` was
    passed — had ALREADY been overwritten.
    """
    # Checked before importing playwright: a bare run must not silently
    # clobber a committed card, and this should fail fast even if
    # playwright isn't installed at all.
    if out_path.exists() and not force:
        raise ConformanceError(
            f"{out_path} already exists. Refusing to overwrite it without --force "
            "(a bare run must not silently clobber a committed card)."
        )

    try:
        from playwright.sync_api import sync_playwright  # type: ignore[import-not-found]
    except ImportError as exc:
        raise ConformanceError(
            "the 'playwright' package is not installed.\n"
            "  Run:\n"
            "    pip install playwright\n"
            "    playwright install chromium\n"
            f"  (import error: {exc})"
        ) from exc

    out_path.parent.mkdir(parents=True, exist_ok=True)
    # Same directory as out_path (not e.g. the system tempdir): guarantees
    # os.replace() below is an atomic same-filesystem rename rather than a
    # cross-filesystem copy, and keeps the temp artifact trivially
    # findable/cleanable if this process is hard-killed mid-render. The
    # leading "." plus the PID makes collisions with a concurrent run (or
    # a real committed asset) effectively impossible.
    tmp_path = out_path.with_name(f".{out_path.name}.{os.getpid()}.tmp")
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        try:
            page = browser.new_page(
                viewport={"width": CARD_WIDTH, "height": CARD_HEIGHT},
                device_scale_factor=1,
            )
            # set_content(), not goto(file://...): template.html fetches
            # nothing external, so there is no need for a temp HTML file on
            # disk at all — this removes that file's whole lifecycle (and
            # the risk of a hard kill leaving a stray *.render-tmp.html in
            # a tree that may be a committed site checkout).
            page.set_content(html_text)

            # Overflow guard: template.html's html/body have
            # `overflow: hidden`, which would otherwise silently CLIP any
            # content that doesn't fit the 1200x630 box (e.g. an
            # over-length --site-name/--tagline/--domain) instead of
            # visibly breaking. The self-check after this function only
            # ever sees the already-clipped 1200x630 screenshot, so it
            # cannot catch that on its own — assert here, before the
            # screenshot is taken, that nothing overflowed.
            #
            # MUST read document.body.scrollHeight/scrollWidth, NOT
            # document.documentElement's: html and body both have an
            # explicit `height: 630px` + `overflow: hidden`, which fixes
            # html's own box to exactly 630px regardless of what overflows
            # *inside* body — document.documentElement.scrollHeight is
            # therefore pinned at 630 even when content massively overflows
            # (measured: a 29-line tagline still reported 630 there). Body's
            # own scrollHeight/scrollWidth is unaffected by body's own
            # `overflow: hidden` (that property only clips rendering, it
            # does not change what scrollHeight/scrollWidth report) and
            # correctly reflects its absolutely-positioned children's real
            # extent (measured: the same 29-line tagline reports 2229 via
            # document.body.scrollHeight). Verified live via the Playwright
            # MCP browser before relying on this — see README.md /
            # render.py's change history for the two probe results.
            scroll_height = page.evaluate("document.body.scrollHeight")
            scroll_width = page.evaluate("document.body.scrollWidth")
            if scroll_height > CARD_HEIGHT or scroll_width > CARD_WIDTH:
                raise ConformanceError(
                    f"rendered content overflows the card: document.body.scrollHeight is "
                    f"{scroll_height}px (required <= {CARD_HEIGHT}px), scrollWidth is "
                    f"{scroll_width}px (required <= {CARD_WIDTH}px) — text is being silently "
                    "clipped by template.html's `overflow: hidden`; shorten "
                    "--site-name/--tagline/--domain or widen the layout"
                )

            page.screenshot(
                path=str(tmp_path),
                type="png",
                clip={"x": 0, "y": 0, "width": CARD_WIDTH, "height": CARD_HEIGHT},
            )
        finally:
            browser.close()

    return tmp_path


# ---------------------------------------------------------------------------
# Self-check: stdlib-only PNG decode + conformance scan
# ---------------------------------------------------------------------------


def read_png_ihdr(data: bytes) -> tuple[int, int, int, int, int]:
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ConformanceError("output is not a valid PNG (bad signature)")
    try:
        length, chunk_type = struct.unpack(">I4s", data[8:16])
        if chunk_type != b"IHDR" or length != 13:
            raise ConformanceError("PNG IHDR chunk missing or malformed")
        # compression method and filter method are unpacked but unused: PNG
        # only defines one value (0) for each, there is nothing to branch on.
        width, height, bit_depth, colour_type, _, _, interlace = struct.unpack(
            ">IIBBBBB", data[16:29]
        )
    except struct.error as exc:
        raise ConformanceError(f"malformed PNG: could not parse IHDR ({exc})") from exc
    return width, height, bit_depth, colour_type, interlace


def _paeth_predictor(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def decode_png_rgb_rows(data: bytes) -> tuple[int, int, list[bytes], int]:
    """Decode a non-interlaced, 8-bit-per-channel PNG (colour type 2/RGB or
    6/RGBA) into a list of un-filtered scanlines, using only zlib + struct.

    This is deliberately narrow: it covers exactly what Chromium's
    `page.screenshot(type="png")` produces. Anything else (palette images,
    16-bit depth, Adam7 interlacing) raises ConformanceError rather than
    silently mis-decoding — those formats are out of scope for this check,
    not something this script should ever be asked to render itself.
    """
    width, height, bit_depth, colour_type, interlace = read_png_ihdr(data)
    if interlace != 0:
        raise ConformanceError("interlaced PNG not supported by the pixel-scan self-check")
    if bit_depth != 8 or colour_type not in (2, 6):
        raise ConformanceError(
            f"unsupported PNG format for pixel-scan self-check "
            f"(bit depth {bit_depth}, colour type {colour_type}); expected 8-bit RGB/RGBA"
        )
    bpp = 3 if colour_type == 2 else 4

    # The module docstring promises "any failure exits non-zero with a
    # clear message" — a <16-byte file, a chunk-length that overruns the
    # buffer, or corrupt IDAT data would otherwise surface as a raw
    # struct.error/zlib.error/IndexError traceback instead. Wrap the whole
    # chunk-walk + zlib-inflate + unfilter pass and translate any of those
    # into a ConformanceError.
    try:
        pos = 8
        idat = bytearray()
        n = len(data)
        while pos < n:
            if pos + 8 > n:
                raise ConformanceError("truncated PNG (chunk header cut off)")
            length, ctype = struct.unpack(">I4s", data[pos : pos + 8])
            chunk_data = data[pos + 8 : pos + 8 + length]
            if ctype == b"IDAT":
                idat += chunk_data
            elif ctype == b"IEND":
                break
            pos += 8 + length + 4  # 4 length + 4 type + data + 4 crc

        raw = zlib.decompress(bytes(idat))
        stride = width * bpp
        rows: list[bytes] = []
        prev = bytes(stride)
        offset = 0
        for y in range(height):
            filt = raw[offset]
            offset += 1
            line = bytearray(raw[offset : offset + stride])
            if len(line) != stride:
                raise ConformanceError(f"truncated PNG scanline data at row {y}")
            offset += stride
            if filt == 0:  # None
                pass
            elif filt == 1:  # Sub
                for i in range(bpp, stride):
                    line[i] = (line[i] + line[i - bpp]) & 0xFF
            elif filt == 2:  # Up
                for i in range(stride):
                    line[i] = (line[i] + prev[i]) & 0xFF
            elif filt == 3:  # Average
                for i in range(stride):
                    a = line[i - bpp] if i >= bpp else 0
                    line[i] = (line[i] + ((a + prev[i]) // 2)) & 0xFF
            elif filt == 4:  # Paeth
                for i in range(stride):
                    a = line[i - bpp] if i >= bpp else 0
                    c = prev[i - bpp] if i >= bpp else 0
                    line[i] = (line[i] + _paeth_predictor(a, prev[i], c)) & 0xFF
            else:
                raise ConformanceError(f"unknown PNG filter type {filt} on row {y}")
            rows.append(bytes(line))
            prev = line
    except (struct.error, zlib.error, IndexError) as exc:
        raise ConformanceError(f"malformed PNG: could not decode pixel data ({exc})") from exc

    return width, height, rows, bpp


def hex_to_rgb(value: str) -> tuple[int, int, int]:
    return int(value[1:3], 16), int(value[3:5], 16), int(value[5:7], 16)


def scan_for_colour(
    width: int, rows: list[bytes], bpp: int, target: tuple[int, int, int], tolerance: int
) -> bool:
    """Full scan of every decoded pixel (not a sample) for a match to
    `target` within `tolerance` per channel. `len(rows)` is the row count
    (== image height by construction of decode_png_rgb_rows) — no separate
    height parameter is needed."""
    tr, tg, tb = target
    for row in rows:
        for i in range(0, width * bpp, bpp):
            r, g, b = row[i], row[i + 1], row[i + 2]
            if abs(r - tr) <= tolerance and abs(g - tg) <= tolerance and abs(b - tb) <= tolerance:
                return True
    return False


def self_check(out_path: Path, accent_hex: str, *, report_path: Path | None = None) -> None:
    """Verify the PNG at `out_path` against REQ-004 and report the result.
    Raises ConformanceError (non-zero exit, clear message) on any failure.

    `report_path`: the path named in printed/error messages; defaults to
    `out_path` itself. `render_and_self_check` self-checks a TEMPORARY
    file before promoting it to a different final destination (see its
    own docstring) and passes that eventual destination here, so messages
    describe the path the caller actually asked for rather than an
    internal temp filename that means nothing to them.
    """
    if report_path is None:
        report_path = out_path
    if not out_path.is_file():
        raise ConformanceError(f"{report_path} was not written")
    data = out_path.read_bytes()
    width, height, bit_depth, colour_type, interlace = read_png_ihdr(data)

    failures: list[str] = []
    if (width, height) != (CARD_WIDTH, CARD_HEIGHT):
        failures.append(
            f"IHDR dimensions are {width}x{height}, required exactly {CARD_WIDTH}x{CARD_HEIGHT}"
        )

    size = len(data)
    if size > MAX_BYTES:
        failures.append(
            f"file size {size:,} bytes exceeds the REQ-004 cap of {MAX_BYTES:,} bytes (300 KB)"
        )

    if bit_depth == 8 and colour_type in (2, 6) and interlace == 0:
        _, _, rows, bpp = decode_png_rgb_rows(data)
        accent_found = scan_for_colour(
            width, rows, bpp, hex_to_rgb(accent_hex), COLOUR_TOLERANCE
        )
        if not accent_found:
            failures.append(
                f"accent colour {accent_hex} was not found in any pixel of the rendered "
                "PNG (REQ-004 requires the sampled accent to occur literally) — this is "
                "the exact regression PR #298 fixed by hand"
            )
    else:
        failures.append(
            f"could not pixel-scan for the accent colour: unsupported PNG encoding "
            f"(bit depth {bit_depth}, colour type {colour_type}, interlace {interlace})"
        )

    if failures:
        raise ConformanceError(
            "og-image conformance check FAILED for "
            + str(report_path)
            + "\n  - "
            + "\n  - ".join(failures)
        )

    print(
        f"OK: {report_path}\n"
        f"  dimensions: {width}x{height} (required {CARD_WIDTH}x{CARD_HEIGHT})\n"
        f"  size:       {size:,} bytes (cap {MAX_BYTES:,} bytes / 300 KB)\n"
        f"  accent:     {accent_hex} found in pixel scan"
    )


def _self_check_and_promote(tmp_path: Path, out_path: Path, accent_hex: str) -> None:
    """Self-check `tmp_path` (a freshly-rendered screenshot at a temporary
    sibling location, see `render_png`), reporting as `out_path`, and only
    on success atomically replace `out_path` with it.

    On failure, `tmp_path` is removed (best-effort — `missing_ok=True`
    tolerates it already being gone) before the exception propagates, so a
    conformance failure leaves NEITHER a stray temp artifact NOR a
    non-conforming (or clobbered) file at `out_path`: whatever was at
    `out_path` before this call — including nothing, or a previously
    committed conformant card — is untouched. This is the fix for the
    exact bug this function replaces: writing the screenshot directly to
    `out_path` and self-checking only afterwards, which meant a failure
    (or a `--force` run) had already overwritten `out_path` by the time
    the check could reject it.
    """
    try:
        self_check(tmp_path, accent_hex, report_path=out_path)
    except BaseException:
        tmp_path.unlink(missing_ok=True)
        raise
    os.replace(tmp_path, out_path)


def render_and_self_check(html_text: str, out_path: Path, accent_hex: str, *, force: bool) -> None:
    """Render `html_text`, self-check the result, and publish to
    `out_path` only if the self-check passes (see `render_png` and
    `_self_check_and_promote`)."""
    tmp_path = render_png(html_text, out_path, force=force)
    _self_check_and_promote(tmp_path, out_path, accent_hex)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Render a vouchfx fleet og:image social card from template.html.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--site-name",
        default=DEFAULT_SITE_NAME,
        help=f"wordmark text next to the logo mark (default: {DEFAULT_SITE_NAME!r})",
    )
    parser.add_argument(
        "--tagline",
        default=DEFAULT_TAGLINE,
        help="descriptive line under the wordmark — reuse the site's existing "
        "og:image:alt / meta-description opening verbatim, REQ-004 forbids new copy "
        f"(default: {DEFAULT_TAGLINE!r})",
    )
    parser.add_argument(
        "--accent",
        default=DEFAULT_ACCENT,
        help="6-digit hex colour for the right half of the swatch and the end stop "
        "of the logo-mark gradient; MUST occur literally in the target site's "
        f"site/styles.css (default: {DEFAULT_ACCENT!r}, the engine's --cyan token)",
    )
    parser.add_argument(
        "--domain",
        default=DEFAULT_DOMAIN,
        help=f"small caption bottom-right, e.g. samples.vouchfx.io (default: {DEFAULT_DOMAIN!r})",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=DEFAULT_OUT,
        help=f"output PNG path (default: {DEFAULT_OUT})",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="overwrite --out if it already exists. Without this flag, render.py "
        "refuses to run if the target file is already present, so a bare invocation "
        "cannot silently clobber a committed card.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        html_text = fill_template(args.site_name, args.tagline, args.accent, args.domain)
        render_and_self_check(html_text, args.out, args.accent, force=args.force)
    except ConformanceError as exc:
        print(exc, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
