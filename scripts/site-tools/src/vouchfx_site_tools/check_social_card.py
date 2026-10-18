"""Validate a built site's social-card metadata and decoded PNG pixels.

Run: python -m vouchfx_site_tools.check_social_card SITE_DIR SITE_URL
The card must contain opaque pixels for the built stylesheet's colour tokens.
"""
from __future__ import annotations

import argparse
import re
import struct
import sys
import zlib
from html import unescape
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import SplitResult, unquote, urlsplit

_REQUIRED_META = frozenset({
    "og:image", "twitter:image", "og:image:width", "og:image:height",
    "og:image:alt", "twitter:card",
})
_MAX_INDEX_BYTES = 1024 * 1024
_MAX_IMAGE_BYTES = 300 * 1024
_PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
_COLOUR_TOKENS = ("bg", "violet", "cyan")


class SocialCardError(ValueError):
    """A fixed, bounded description of a failed social-card contract."""



# Inspect original attributes so validation cannot accept lossy numeric references.
_RAW_ATTRIBUTE = re.compile(r"""([^\s/>=]+)(?:\s*=+\s*('[^']*'|"[^"]*"|[^>\s]*))?\s*""")
_NUMERIC_REFERENCE = re.compile(r"&#(?:[xX](?P<hex>[0-9a-fA-F]+)|(?P<decimal>[0-9]+));?")


def _raw_attributes(tag: str) -> list[tuple[str, str | None]]:
    opening = re.match(r"<[^\s/>]+\s*", tag)
    if opening is None:
        raise SocialCardError("Social-card metadata attributes are malformed.")
    position = opening.end()
    attributes: list[tuple[str, str | None]] = []
    while position < len(tag) and not tag.startswith((">", "/>"), position):
        match = _RAW_ATTRIBUTE.match(tag, position)
        if match is None:
            raise SocialCardError("Social-card metadata attributes are malformed.")
        name, value = match.groups()
        if value is not None and value[:1] in ("'", '"'):
            value = value[1:-1]
        attributes.append((name.lower(), value))
        position = match.end()
    if tag[position:] not in (">", "/>"):
        raise SocialCardError("Social-card metadata attributes are malformed.")
    return attributes


def _has_invalid_numeric_reference(value: str) -> bool:
    for match in _NUMERIC_REFERENCE.finditer(value):
        hexadecimal = match.group("hex")
        digits = (hexadecimal if hexadecimal is not None else match.group("decimal")).lstrip("0") or "0"
        # Avoid constructing arbitrarily large integers; leading zeroes are harmless to this bound.
        if len(digits) > (6 if hexadecimal is not None else 7):
            return True
        codepoint = int(digits, 16 if hexadecimal is not None else 10)
        if codepoint > 0x10FFFF or 0xD800 <= codepoint <= 0xDFFF:
            return True
        if not unescape(f"&#{codepoint};"):
            return True
        # Ordinary HTML whitespace stays valid in text; URLs reject it after normal decoding.
        if codepoint == 0 or (codepoint < 32 and codepoint not in (9, 10, 12, 13)) or codepoint == 127:
            return True
    return False


class _MetaCollector(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.metas: dict[str, str] = {}

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag != "meta":
            return
        keys = [value for name, value in attrs if name in ("name", "property")]
        if not any(key in _REQUIRED_META for key in keys):
            return
        names = [name for name, _value in attrs if name in ("name", "property", "content")]
        if len(keys) != 1 or len(names) != len(set(names)):
            raise SocialCardError("Social-card metadata attributes are ambiguous.")
        raw = _raw_attributes(self.get_starttag_text())
        if [name for name, _value in raw] != [name for name, _value in attrs]:
            raise SocialCardError("Social-card metadata attributes are ambiguous.")
        if any(_has_invalid_numeric_reference(value)
               for name, value in raw if name in ("name", "property", "content") and value is not None):
            raise SocialCardError("Social-card metadata contains an invalid numeric reference.")
        key = keys[0]
        if key in self.metas:
            raise SocialCardError("Social-card metadata is duplicated.")
        content = next((value for name, value in attrs if name == "content"), None)
        if content is None:
            raise SocialCardError("Social-card metadata has no content.")
        self.metas[key] = content


def _decoded_path(value: str) -> str:
    if re.search(r"%(?![0-9A-Fa-f]{2})", value):
        raise SocialCardError("Social-card URL path has malformed escaping.")
    path = unquote(value, errors="strict")
    if "\\" in path or any(ord(char) < 32 or ord(char) == 127 for char in path):
        raise SocialCardError("Social-card URL path contains an invalid character.")
    if not path:
        return path
    if not path.startswith("/"):
        raise SocialCardError("Social-card URL path is invalid.")
    segments = path.split("/")[1:]
    # A single trailing slash is valid for a canonical base, not an empty interior segment.
    if segments[-1] == "":
        segments.pop()
    if any(segment in ("", ".", "..") or ":" in segment for segment in segments):
        raise SocialCardError("Social-card URL path is invalid.")
    return path


def _url_parts(value: str) -> tuple[SplitResult, tuple[str, str, int]]:
    if any(ord(char) < 32 or char.isspace() or ord(char) == 127 for char in value) or "\\" in value:
        raise SocialCardError("Social-card URL contains an invalid character.")
    try:
        parts = urlsplit(value)
        # Accessing port validates it; urlsplit alone does not.
        port = parts.port
        if parts.scheme not in ("http", "https") or not parts.hostname or parts.username is not None:
            raise SocialCardError("Social-card URL must be an absolute HTTP(S) URL.")
        if "?" in value or "#" in value:
            raise SocialCardError("Social-card URL must have no query or fragment.")
        effective_port = port if port is not None else (443 if parts.scheme == "https" else 80)
        authority = (parts.scheme.lower(), parts.hostname.lower(), effective_port)
        return parts, authority
    except ValueError as failure:
        if isinstance(failure, SocialCardError):
            raise
        raise SocialCardError("Social-card URL is malformed.") from None


def _asset_path(site_dir: Path, value: str, site_url: str) -> Path:
    parts, authority = _url_parts(value)
    origin, origin_authority = _url_parts(site_url)
    if authority != origin_authority:
        raise SocialCardError("Social-card image URL is outside the configured origin.")
    path = _decoded_path(parts.path)
    base_path = _decoded_path(origin.path).rstrip("/") + "/"
    if not path.startswith(base_path):
        raise SocialCardError("Social-card image URL is outside the configured base path.")
    relative = path[len(base_path):]
    segments = relative.split("/")
    if not relative or any(segment in ("", ".", "..") or ":" in segment for segment in segments):
        raise SocialCardError("Social-card image path is invalid.")
    candidate = (site_dir / Path(*segments)).resolve()
    if not candidate.is_relative_to(site_dir.resolve()):
        raise SocialCardError("Social-card image asset is outside the built directory.")
    if not candidate.is_file():
        raise SocialCardError("Social-card image asset is missing.")
    return candidate


def _check_png(path: Path) -> tuple[int, int]:
    if path.stat().st_size > _MAX_IMAGE_BYTES:
        raise SocialCardError("Social-card PNG exceeds the 307200-byte limit.")
    with path.open("rb") as stream:
        header = stream.read(33)
    if len(header) != 33 or header[:8] != _PNG_SIGNATURE:
        raise SocialCardError("Social-card image has no complete PNG header.")
    if header[8:12] != b"\x00\x00\x00\x0d" or header[12:16] != b"IHDR":
        raise SocialCardError("Social-card PNG must begin with a 13-byte IHDR chunk.")
    if zlib.crc32(header[12:29]) != struct.unpack(">I", header[29:33])[0]:
        raise SocialCardError("Social-card PNG IHDR checksum is invalid.")
    dimensions = struct.unpack(">II", header[16:24])
    if dimensions != (1200, 630):
        raise SocialCardError("Social-card PNG dimensions must be 1200x630.")
    return dimensions


def _token_colours(site_dir: Path) -> dict[str, tuple[int, int, int]]:
    styles = site_dir / "styles.css"
    if not styles.resolve().is_relative_to(site_dir.resolve()):
        raise SocialCardError("Social-card stylesheet is outside the built directory.")
    try:
        css = styles.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        raise SocialCardError("Social-card stylesheet could not be read.") from None
    css = re.sub(r"/\*.*?\*/", "", css, flags=re.DOTALL)
    colours = {}
    for name in _COLOUR_TOKENS:
        matches = re.findall(rf"--{name}\s*:\s*(#[0-9a-fA-F]{{6}})\s*(?=;|}})", css)
        if len(matches) != 1:
            raise SocialCardError(
                f"Social-card stylesheet must define exactly one literal six-digit --{name} hex token."
            )
        colours[name] = tuple(bytes.fromhex(matches[0][1:]))
    return colours


def _check_token_pixels(asset: Path, colours: dict[str, tuple[int, int, int]]) -> None:
    from PIL import Image, UnidentifiedImageError

    try:
        with Image.open(asset) as source:
            if source.format != "PNG":
                raise SocialCardError("Social-card asset is not a decoded PNG image.")
            image = source.convert("RGBA")
            image.load()
    except (OSError, ValueError, UnidentifiedImageError):
        raise SocialCardError("Social-card asset cannot be decoded as a complete PNG image.") from None
    required = set(colours.values())
    found = {
        pixel[:3] for pixel in image.get_flattened_data()
        if pixel[3] == 255 and pixel[:3] in required
    }
    missing = [name for name, rgb in colours.items() if rgb not in found]
    if missing:
        names = ", ".join("--" + name for name in missing)
        raise SocialCardError(
            f"Social-card asset has no opaque literal pixel for CSS token(s) {names}."
        )


def check_social_card(site_dir: Path, site_url: str) -> int:
    """Raise SocialCardError on refusal; return the number of distinct checked assets."""
    try:
        canonical, _authority = _url_parts(site_url)
        _decoded_path(canonical.path)
        index = site_dir / "index.html"
        if not index.resolve().is_relative_to(site_dir.resolve()):
            raise SocialCardError("Built index.html is outside the built directory.")
        if not index.is_file():
            raise SocialCardError("Built index.html is missing.")
        with index.open("rb") as stream:
            encoded = stream.read(_MAX_INDEX_BYTES + 1)
        if len(encoded) > _MAX_INDEX_BYTES:
            raise SocialCardError("Built index.html exceeds the 1048576-byte limit.")
        collector = _MetaCollector()
        collector.feed(encoded.decode("utf-8"))
        if set(collector.metas) != _REQUIRED_META:
            raise SocialCardError("Required social-card metadata is missing.")
        metas = collector.metas
        if not metas["og:image:alt"].strip():
            raise SocialCardError("Social-card image alternative text is empty.")
        if metas["twitter:card"] != "summary_large_image":
            raise SocialCardError("Twitter card must be summary_large_image.")
        assets = {_asset_path(site_dir, metas[key], site_url) for key in ("og:image", "twitter:image")}
        colours = _token_colours(site_dir)
        for asset in assets:
            _check_png(asset)
            _check_token_pixels(asset, colours)
        if metas["og:image:width"].strip() != "1200" or metas["og:image:height"].strip() != "630":
            raise SocialCardError("Social-card metadata dimensions must match the PNG.")
        return len(assets)
    except SocialCardError:
        raise
    except (OSError, UnicodeError, ValueError, RuntimeError):
        raise SocialCardError("Social-card inputs could not be read or decoded.") from None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site_dir", type=Path)
    parser.add_argument("site_url")
    args = parser.parse_args(argv)
    try:
        checked = check_social_card(args.site_dir, args.site_url)
    except SocialCardError as failure:
        print(f"Social card check failed: {failure}", file=sys.stderr)
        return 1
    print(f"Social card check passed: {checked} distinct PNG asset(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
