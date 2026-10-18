"""Independent metadata, asset and actual packaged-CLI contract controls."""
from __future__ import annotations

import io
import os
import subprocess
import sys
from pathlib import Path

import pytest
from PIL import Image

from vouchfx_site_tools.check_social_card import SocialCardError, check_social_card

ORIGIN = "https://example.test/project/"
PNG_LIMIT = 307200


def png(width=1200, height=630):
    # Create a complete image independently of the checker's decoder.
    image = Image.new("RGB", (width, height), (11, 15, 26))
    image.putpixel((0, 0), (129, 140, 248))
    image.putpixel((1, 0), (34, 211, 238))
    output = io.BytesIO()
    image.save(output, format="PNG")
    return output.getvalue()


def metadata(**overrides):
    values = {
        "og:image": ORIGIN + "card.png",
        "twitter:image": ORIGIN + "card.png",
        "og:image:width": "1200",
        "og:image:height": "630",
        "og:image:alt": "A test card",
        "twitter:card": "summary_large_image",
    }
    values.update(overrides)
    return values


def write_index(site, values=None):
    values = metadata() if values is None else values
    content = "<html><head>" + "".join(
        f"<meta content='{value}' {'property' if key.startswith('og:') else 'name'}='{key}'>"
        for key, value in values.items()
    ) + "</head><body>Test</body></html>"
    (site / "index.html").write_text(content, encoding="utf-8")


@pytest.fixture
def site(tmp_path):
    result = tmp_path / "site"
    result.mkdir()
    (result / "card.png").write_bytes(png())
    (result / "styles.css").write_text(
        ":root { --bg: #0b0f1a; --violet: #818cf8; --cyan: #22d3ee; }",
        encoding="utf-8",
    )
    write_index(result)
    return result


def test_same_asset_and_reordered_single_quoted_metadata_pass(site):
    assert check_social_card(site, ORIGIN) == 1


def test_two_distinct_assets_are_both_checked(site):
    (site / "second.png").write_bytes(png())
    write_index(site, metadata(**{"twitter:image": ORIGIN + "second.png"}))
    assert check_social_card(site, ORIGIN) == 2
    (site / "second.png").write_bytes(png(600, 315))
    with pytest.raises(SocialCardError, match="1200x630"):
        check_social_card(site, ORIGIN)


def test_second_asset_must_have_literal_pixels(site):
    image = Image.new("RGB", (1200, 630), (11, 15, 26))
    image.putpixel((0, 0), (129, 140, 248))
    image.save(site / "second.png", format="PNG")
    write_index(site, metadata(**{"twitter:image": ORIGIN + "second.png"}))
    with pytest.raises(SocialCardError, match="--cyan"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("mode", ["RGB", "RGBA", "P"])
def test_literal_opaque_pixels_across_png_modes(site, mode):
    rgb = Image.new("RGB", (1200, 630), (11, 15, 26))
    rgb.putpixel((0, 0), (129, 140, 248))
    rgb.putpixel((1, 0), (34, 211, 238))
    if mode == "P":
        image = Image.new("P", rgb.size, 0)
        image.putpalette([11, 15, 26, 129, 140, 248, 34, 211, 238] + [0] * 759)
        image.putpixel((0, 0), 1)
        image.putpixel((1, 0), 2)
    else:
        image = rgb.convert(mode)
    image.save(site / "card.png", format="PNG")
    assert check_social_card(site, ORIGIN) == 1


def test_changed_stylesheet_token_requires_changed_pixel(site):
    (site / "styles.css").write_text(
        ":root { --bg: #0b0f1a; --violet: #818cf8; --cyan: #00ffff; }",
        encoding="utf-8",
    )
    with pytest.raises(SocialCardError, match="--cyan"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("css", [
    "/* --cyan: #22d3ee; */ :root { --bg: #0b0f1a; --violet: #818cf8; }",
    ":root { --bg: #0b0f1a; --violet: #818cf8; --cyan: #22d3ee; --cyan: #22d3ee; }",
])
def test_missing_or_duplicate_token_is_refused(site, css):
    (site / "styles.css").write_text(css, encoding="utf-8")
    with pytest.raises(SocialCardError, match="--cyan"):
        check_social_card(site, ORIGIN)


def test_missing_stylesheet_is_refused(site):
    (site / "styles.css").unlink()
    with pytest.raises(SocialCardError, match="stylesheet could not be read"):
        check_social_card(site, ORIGIN)


def test_transparent_accent_and_header_only_png_are_refused(site):
    image = Image.new("RGBA", (1200, 630), (11, 15, 26, 255))
    image.putpixel((0, 0), (129, 140, 248, 255))
    image.putpixel((1, 0), (34, 211, 238, 0))
    image.save(site / "card.png", format="PNG")
    with pytest.raises(SocialCardError, match="--cyan"):
        check_social_card(site, ORIGIN)
    (site / "card.png").write_bytes(png()[:33])
    with pytest.raises(SocialCardError, match="cannot be decoded"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("key", list(metadata()))
def test_every_required_meta_is_required(site, key):
    values = metadata()
    del values[key]
    write_index(site, values)
    with pytest.raises(SocialCardError, match="metadata is missing"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("extra", [
    "<meta property='og:image' content='https://example.test/project/card.png'>",
    "<meta property='og:image' name='og:image' content='https://example.test/project/card.png'>",
    "<meta property='og:image' content='one' content='two'>",
    "<meta property='og:image' property='og:image' content='one'>",
])
def test_duplicate_or_ambiguous_meta_is_refused(site, extra):
    with (site / "index.html").open("a", encoding="utf-8") as stream:
        stream.write(extra)
    with pytest.raises(SocialCardError, match="duplicated|ambiguous"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("overrides,reason", [
    ({"og:image:alt": "  "}, "alternative text is empty"),
    ({"twitter:card": "summary"}, "summary_large_image"),
    ({"og:image:width": "1199"}, "metadata dimensions"),
    ({"og:image:height": "630.0"}, "metadata dimensions"),
])
def test_meta_value_controls(site, overrides, reason):
    write_index(site, metadata(**overrides))
    with pytest.raises(SocialCardError, match=reason):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("image", [
    b"not a PNG",
    png()[:7],
    png()[:32],
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dTEXT" + b"z" * 17,
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0cIHDR" + b"z" * 17,
    png(1199, 630),
    png(1200, 631),
    png()[:29] + b"\x00\x00\x00\x00" + png()[33:],
    png() + b"z" * PNG_LIMIT,
], ids=["not-png", "short-signature", "short-header", "wrong-first-chunk",
        "wrong-ihdr-length", "wrong-width", "wrong-height", "wrong-crc", "oversized"])
def test_invalid_png_controls(site, image):
    (site / "card.png").write_bytes(image)
    with pytest.raises(SocialCardError):
        check_social_card(site, ORIGIN)


def test_exact_file_size_limit_passes_and_one_byte_more_fails(site):
    image = png()
    (site / "card.png").write_bytes(image + b"z" * (PNG_LIMIT - len(image)))
    assert check_social_card(site, ORIGIN) == 1
    with (site / "card.png").open("ab") as stream:
        stream.write(b"z")
    with pytest.raises(SocialCardError, match="307200-byte"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("url", [
    "/project/card.png",
    "https://other.test/project/card.png",
    "https://example.test/card.png",
    "https://user:password@example.test/project/card.png",
    "https://example.test/project/card.png?x=1",
    "https://example.test/project/card.png?",
    "https://example.test/project/card.png#image",
    "https://example.test/project/card.png#",
    "https://example.test/project/../card.png",
    "https://example.test/project/%2e%2e/card.png",
    "https://example.test/project/%2E%2E%2Fcard.png",
    "https://example.test/project/%5ccard.png",
    "https://example.test/project/%00card.png",
    "https://example.test/project/%0acard.png",
    "https://example.test/project/%FFcard.png",
    "https://example.test/project/%ZZcard.png",
    "https://example.test:bad/project/card.png",
    "https://example.test:0/project/card.png",
    " https://example.test/project/card.png",
    "https://example.test/project/card.png ",
    "https://example.test/project//card.png",
    "https://example.test/project/C:card.png",
])
def test_unsafe_or_malformed_urls_are_refused(site, url):
    write_index(site, metadata(**{"og:image": url}))
    with pytest.raises(SocialCardError):
        check_social_card(site, ORIGIN)


def test_hostname_case_and_explicit_default_port_pass(site):
    write_index(site, metadata(**{"og:image": "https://EXAMPLE.test:443/project/card.png"}))
    assert check_social_card(site, ORIGIN) == 1


def test_absent_asset_and_absent_index_fail(site):
    (site / "card.png").unlink()
    with pytest.raises(SocialCardError, match="asset is missing"):
        check_social_card(site, ORIGIN)
    (site / "index.html").unlink()
    with pytest.raises(SocialCardError, match="index.html is missing"):
        check_social_card(site, ORIGIN)


def test_outside_symlink_cannot_supply_a_card(site, tmp_path):
    outside = tmp_path / "outside.png"
    outside.write_bytes(png())
    link = site / "outside.png"
    try:
        link.symlink_to(outside)
    except OSError:
        pytest.skip("Host cannot create a filesystem symlink.")
    write_index(site, metadata(**{"og:image": ORIGIN + "outside.png"}))
    with pytest.raises(SocialCardError, match="outside the built directory"):
        check_social_card(site, ORIGIN)


def test_unreadable_utf8_and_index_budget_are_bounded(site):
    (site / "index.html").write_bytes(b"\xff")
    with pytest.raises(SocialCardError, match="could not be read or decoded"):
        check_social_card(site, ORIGIN)
    (site / "index.html").write_bytes(b"z" * (1048576 + 1))
    with pytest.raises(SocialCardError, match="1048576-byte"):
        check_social_card(site, ORIGIN)


def test_actual_module_cli_returns_zero_then_one_and_no_input_echo(site):
    env = dict(os.environ, PYTHONPATH=str(Path(__file__).resolve().parents[1] / "src"))
    command = [sys.executable, "-m", "vouchfx_site_tools.check_social_card", str(site), ORIGIN]
    good = subprocess.run(command, env=env, text=True, capture_output=True, timeout=10)
    assert good.returncode == 0, good.stderr
    assert "1 distinct PNG" in good.stdout
    canary = "private-input-canary"
    write_index(site, metadata(**{"og:image": "https://" + canary + ".test/card.png"}))
    bad = subprocess.run(command, env=env, text=True, capture_output=True, timeout=10)
    assert bad.returncode == 1
    assert bad.stdout == ""
    assert canary not in bad.stderr
    assert len(bad.stderr) < 200

def test_built_index_cannot_be_an_outside_symlink(site, tmp_path):
    outside = tmp_path / "private-index.html"
    outside.write_bytes((site / "index.html").read_bytes())
    (site / "index.html").unlink()
    try:
        (site / "index.html").symlink_to(outside)
    except OSError:
        pytest.skip("Host cannot create a filesystem symlink.")
    with pytest.raises(SocialCardError, match="outside the built directory"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("control", range(32), ids=lambda value: f"U+{value:04X}")
@pytest.mark.parametrize("location", ["metadata", "canonical"])
def test_every_raw_c0_prefix_is_refused_before_url_parsing(site, control, location):
    malformed = chr(control) + ORIGIN
    canonical = ORIGIN
    if location == "metadata":
        write_index(site, metadata(**{"og:image": malformed + "card.png"}))
    else:
        canonical = malformed
    with pytest.raises(SocialCardError, match="invalid character"):
        check_social_card(site, canonical)


def run_cli(site, canonical):
    # Use the currently imported package so isolated source mutants reach the actual CLI too.
    package_root = Path(sys.modules[check_social_card.__module__].__file__).resolve().parent.parent
    env = dict(os.environ, PYTHONPATH=str(package_root))
    return subprocess.run(
        [sys.executable, "-m", "vouchfx_site_tools.check_social_card", str(site), canonical],
        env=env, text=True, capture_output=True, timeout=10,
    )


@pytest.mark.parametrize("control,location", [
    (0, "metadata"), (1, "metadata"), (8, "metadata"),
    (14, "metadata"), (27, "metadata"),
    (1, "canonical"), (8, "canonical"), (14, "canonical"), (27, "canonical"),
], ids=["nul-meta", "soh-meta", "bs-meta", "so-meta", "esc-meta",
        "soh-base", "bs-base", "so-base", "esc-base"])
def test_actual_cli_refuses_raw_controls_without_echo(site, control, location):
    # NUL cannot occur in OS argv; its canonical case is covered through the API above.
    canary = "private-control-canary"
    malformed = chr(control) + "https://" + canary + ".test/project/"
    canonical = ORIGIN
    if location == "metadata":
        write_index(site, metadata(**{"og:image": malformed + "card.png"}))
    else:
        canonical = malformed
    result = run_cli(site, canonical)
    assert result.returncode == 1, result.stdout
    assert result.stdout == ""
    assert "invalid character" in result.stderr
    assert canary not in result.stderr
    assert len(result.stderr) < 200


@pytest.mark.parametrize("path", [
    "/project/../", "/project/%2e%2e/", "/project/./", "/project/%2E/",
    "/project//", "/project/%2f/", "/project/%2E%2E%2F/",
    "/project/%ZZ/", "/project/%/", "/project/%00/", "/project/%5c/",
    "/project/C:/",
], ids=["dotdot", "encoded-dotdot", "dot", "encoded-dot", "empty-segment",
        "encoded-empty", "encoded-dotdot-slash", "bad-escape", "incomplete-escape",
        "encoded-nul", "encoded-backslash", "colon"])
@pytest.mark.parametrize("location", ["canonical", "metadata", "both"])
def test_actual_cli_validates_whole_decoded_base_and_image_paths(site, path, location):
    malformed = "https://example.test" + path
    canonical = malformed if location in ("canonical", "both") else ORIGIN
    image = malformed + "card.png" if location in ("metadata", "both") else ORIGIN + "card.png"
    write_index(site, metadata(**{"og:image": image, "twitter:image": image}))
    with pytest.raises(SocialCardError):
        check_social_card(site, canonical)
    result = run_cli(site, canonical)
    assert result.returncode == 1, result.stdout
    assert result.stdout == ""
    assert result.stderr.startswith("Social card check failed: ")
    assert len(result.stderr) < 200


@pytest.mark.parametrize("canonical,image,filename", [
    ("https://example.test/%70roject/", "https://example.test/project/card.png", "card.png"),
    (ORIGIN, ORIGIN + "card%20name.png", "card name.png"),
    ("https://example.test/pr%C3%B6ject/", "https://example.test/pr%C3%B6ject/card.png", "card.png"),
    ("https://example.test", "https://example.test/card.png", "card.png"),
], ids=["encoded-base", "encoded-space-in-filename", "encoded-unicode-base", "root-base"])
def test_actual_cli_preserves_valid_encoded_and_root_paths(site, canonical, image, filename):
    (site / filename).write_bytes(png())
    write_index(site, metadata(**{"og:image": image, "twitter:image": image}))
    assert check_social_card(site, canonical) == 1
    result = run_cli(site, canonical)
    assert result.returncode == 0, result.stderr
    assert result.stdout == "Social card check passed: 1 distinct PNG asset(s).\n"
    assert result.stderr == ""


@pytest.mark.parametrize("control", range(32), ids=lambda value: f"U+{value:04X}")
@pytest.mark.parametrize("encoding", ["decimal", "hex"])
def test_numeric_c0_image_prefixes_are_refused_before_lossy_decoding(site, control, encoding):
    reference = f"&#{control};" if encoding == "decimal" else f"&#x{control:02X};"
    write_index(site, metadata(**{"og:image": reference + ORIGIN + "card.png"}))
    with pytest.raises(SocialCardError):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("key", list(metadata()))
@pytest.mark.parametrize("control", [1, 8, 14, 27, 127], ids=lambda value: f"U+{value:04X}")
def test_every_required_meta_identity_refuses_lossy_references(site, key, control):
    values = metadata()
    value = values.pop(key)
    values[key[:3] + f"&#{control};" + key[3:]] = value
    write_index(site, values)
    with pytest.raises(SocialCardError, match="invalid numeric reference"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("key", list(metadata()))
@pytest.mark.parametrize("control", [0, 1, 8, 14, 27, 127], ids=lambda value: f"U+{value:04X}")
def test_every_required_meta_value_refuses_invalid_control_references(site, key, control):
    value = metadata()[key]
    write_index(site, metadata(**{key: value[:3] + f"&#x{control:02X};" + value[3:]}))
    with pytest.raises(SocialCardError, match="invalid numeric reference"):
        check_social_card(site, ORIGIN)


@pytest.mark.parametrize("overrides", [
    {"og:image": "&#1;" + ORIGIN + "card.png"},
    {"og:image": "&#8;" + ORIGIN + "card.png"},
    {"og:image": "&#14;" + ORIGIN + "card.png"},
    {"og:image": "&#27;" + ORIGIN + "card.png"},
    {"og:image": "&#127;" + ORIGIN + "card.png"},
    {"og:image": ORIGIN + "ca&#x01;rd.png"},
    {"twitter:image": ORIGIN + "ca&#x01;rd.png"},
    {"og:image": ORIGIN + "ca&#127;rd.png"},
    {"og:im&#1;age": ORIGIN + "card.png", "og:image": None},
    {"og:image:width": "12&#1;00"},
    {"twitter:card": "summary_larg&#1;e_image"},
    {"og:image": ORIGIN + "ca&#000000000000000000001rd.png"},
    {"og:image": ORIGIN + "ca&#x000000000000000000001rd.png"},
    {"og:image": ORIGIN + "ca&#xFDD0;rd.png"},
    {"og:image": ORIGIN + "ca&#xFFFE;rd.png"},
    {"og:image": ORIGIN + "ca&#x1FFFF;rd.png"},
    {"og:im&#xFFFE;age": ORIGIN + "card.png", "og:image": None},
    {"og:image:width": "12&#xFDD0;00"},
], ids=["soh-prefix", "bs-prefix", "so-prefix", "esc-prefix", "del-prefix",
        "og-interior", "twitter-interior", "del-interior", "identity",
        "width", "card-type", "decimal-no-semicolon", "hex-no-semicolon",
        "noncharacter-fdd0", "noncharacter-fffe", "noncharacter-plane-end",
        "noncharacter-identity", "noncharacter-width"])
def test_actual_cli_refuses_lossy_numeric_metadata_references(site, overrides):
    values = metadata(**overrides)
    values = {key: value for key, value in values.items() if value is not None}
    write_index(site, values)
    result = run_cli(site, ORIGIN)
    assert result.returncode == 1, result.stdout
    assert result.stdout == ""
    assert "invalid numeric reference" in result.stderr
    assert len(result.stderr) < 200


@pytest.mark.parametrize("quote", ["'", '"', None], ids=["single", "double", "unquoted"])
def test_actual_cli_preserves_legitimate_numeric_named_and_text_references(site, quote):
    (site / "card&name.png").write_bytes(png())
    values = metadata(**{
        "og:image": ORIGIN + "ca&#114;d&amp;name.png",
        "twitter:image": ORIGIN + "ca&#x72;d&amp;name.png",
        "og:image:width": "12&#48;0",
        "og:image:height": "6&#51;0",
        "og:image:alt": "Card&amp;text&#10;second-line",
        "twitter:card": "summary&lowbar;large&lowbar;image",
    })
    values["og:im&#97;ge"] = values.pop("og:image")
    values["unrelated&#1;meta"] = "unrelated&#1;value"
    attributes = []
    for key, value in values.items():
        attribute = "property" if key.startswith("og:") else "name"
        wrapped = lambda text: text if quote is None else quote + text + quote
        attributes.append(f"<META content={wrapped(value)} {attribute}={wrapped(key)} data-other='&#1;text'>")
    (site / "index.html").write_text("".join(attributes), encoding="utf-8")
    assert check_social_card(site, ORIGIN) == 1
    result = run_cli(site, ORIGIN)
    assert result.returncode == 0, result.stderr
    assert result.stderr == ""


def test_numeric_reference_integer_work_is_bounded(site):
    # HTMLParser would remove these forbidden references despite their long spelling.
    reference = "&#x" + "0" * 50000 + "01;"
    write_index(site, metadata(**{"og:image": ORIGIN + "ca" + reference + "rd.png"}))
    result = run_cli(site, ORIGIN)
    assert result.returncode == 1
    assert "invalid numeric reference" in result.stderr
