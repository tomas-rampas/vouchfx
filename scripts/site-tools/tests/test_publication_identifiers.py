"""Regression coverage for the public prose/schema boundary (issue #381)."""
from __future__ import annotations

import gzip
from html import escape
import importlib.util
import json
import shutil
import subprocess
import sys
import uuid
from pathlib import Path

import pytest

CHECK_SITE_PATH = Path(__file__).resolve().parents[2] / "check_site.py"
SCHEMA_PATHS = (
    "src/Engine/Vouchfx.Engine.Compilation/Schema/root-language-schema.json",
    "tests/Vouchfx.Engine.Compilation.Tests/Golden/composed-schema.v1.json",
    "tools/vscode-vouchfx/src/schema/composed-schema.v1.json",
    "src/Engine/Vouchfx.Engine.Orchestration/Environments/compose-environment-schema.json",
    "tools/vscode-vouchfx/src/schema/compose-environment-schema.v1.json",
)
GUARD_NAMES = {
    "check_no_private_identifiers",
    "check_schema_descriptions_no_private_identifiers",
}
TEXT_LEAK = "Published text contains an internal planning identifier."
TEXT_INVALID = "Published text could not be read or parsed safely."
SCHEMA_LEAK = "A public schema description contains an internal planning identifier."
SCHEMA_INVALID = "A public schema could not be read or parsed safely."


@pytest.fixture
def gate():
    name = f"_publication_identifiers_{uuid.uuid4().hex}"
    spec = importlib.util.spec_from_file_location(name, CHECK_SITE_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        spec.loader.exec_module(module)
        yield module
    finally:
        sys.modules.pop(name, None)


@pytest.fixture
def public_schemas(tmp_path, gate, monkeypatch):
    root = tmp_path / "schema-source-canary"
    for relative in SCHEMA_PATHS:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"description": "Public capability."}), encoding="utf-8")
    monkeypatch.setattr(gate, "REPO_ROOT", root)
    return root


def _site(tmp_path, text, name="index.html"):
    site = tmp_path / "built-site-canary"
    site.mkdir(exist_ok=True)
    (site / name).write_text(text, encoding="utf-8")
    return site


def _main_with_real_registered_guards(gate, monkeypatch, site):
    # Preserve the real registration: removing either CHECKS entry must let
    # that leak through and fail these executable main() assertions.
    monkeypatch.setattr(
        gate, "CHECKS",
        tuple(check if check.__name__ in GUARD_NAMES else lambda _site: None
              for check in gate.CHECKS),
    )
    return gate.main(["check_site.py", str(site)])


@pytest.mark.parametrize("identifier", ["REQ-004", "REQ-018", "EDGE-001", "S12-A-07", "S1-Z-0"])
def test_registered_main_refuses_visible_identifiers(
    gate, public_schemas, tmp_path, monkeypatch, capsys, identifier
):
    site = _site(tmp_path, f"<p>payload-canary {identifier}</p>")
    assert _main_with_real_registered_guards(gate, monkeypatch, site) == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == f"FAIL [check_no_private_identifiers]: {TEXT_LEAK}\n"
    assert "payload-canary" not in captured.err
    assert str(tmp_path) not in captured.err


@pytest.mark.parametrize("text", [
    "<p>REQ-&#48;04</p>",
    "<p>EDGE-&#x30;01</p>",
    "<p>S12-&#65;-07</p>",
    "<p><span>REQ-</span><code>004</code></p>",
    "<p>RE<span>Q-0</span>04</p>",
    '<p>REQ-<span title="unrelated">004</span></p>',
    "<p>REQ-<script>unused()</script>004</p>",
    "<p>REQ-<style>unused { color: red; }</style>004</p>",
])
def test_entities_and_inline_markup_cannot_hide_identifiers(gate, tmp_path, text):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, text))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("tag", ["p", "div", "li", "td", "h2", "pre", "section", "br"])
def test_block_boundaries_do_not_invent_identifiers(gate, tmp_path, tag):
    text = f"<{tag}>REQ-</{tag}><{tag}>004</{tag}>"
    gate.check_no_private_identifiers(_site(tmp_path, text))


@pytest.mark.parametrize("text", [
    "<!-- REQ-004 --> <p>Public capability.</p>",
    '<script>const comment = "REQ-004";</script><p>Public capability.</p>',
    "<style>/* EDGE-001 */</style><p>Public capability.</p>",
    "<p>PreREQ-004Suffix req-004 S12-aa-07 2026-08-05</p>",
    "<p>REQ-&amp;#48;04</p>",
])
def test_comments_sources_and_noncanonical_words_are_allowed(gate, tmp_path, text):
    gate.check_no_private_identifiers(_site(tmp_path, text))


@pytest.mark.parametrize("text", [
    '<img alt="REQ-004">',
    '<span title="EDGE-001">Public capability</span>',
    '<button aria-label="S12-A-07">Public capability</button>',
    '<meta name="description" content="REQ-004">',
    '<meta property="og:description" content="REQ-&#48;04">',
])
def test_public_labels_and_metadata_are_checked(gate, tmp_path, text):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, text))
    assert str(error.value) == TEXT_LEAK


def test_source_javascript_is_outside_the_prose_guard(gate, tmp_path):
    gate.check_no_private_identifiers(_site(tmp_path, '// REQ-004\n', "bundle.js"))


@pytest.mark.parametrize("text", ["REQ-004", "EDGE-&#48;01", "S12-&#65;-07"])
def test_plain_output_is_checked(gate, tmp_path, text):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, text, "llms.txt"))
    assert str(error.value) == TEXT_LEAK


def test_xml_element_boundaries_do_not_join_unrelated_values(gate, tmp_path):
    gate.check_no_private_identifiers(_site(tmp_path, "<x><v>REQ-</v><v>004</v></x>", "site.xml"))


def test_xml_visible_identifier_is_refused(gate, tmp_path):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, "<x>REQ-004</x>", "site.xml"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("text", [
    '{"docs":[{"text":"REQ-\\u003004"}]}',
    json.dumps({"docs": [{"text": "<span>REQ-</span><b>004</b>"}]}),
    json.dumps({"docs": [{"text": "EDGE-&#x30;01"}]}),
])
def test_search_json_decoding_and_page_markup_are_checked(gate, tmp_path, text):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, text, "search_index.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("relative", SCHEMA_PATHS)
def test_registered_main_checks_each_canonical_schema(
    gate, public_schemas, tmp_path, monkeypatch, capsys, relative
):
    schema = {"$defs": {"nested": {"anyOf": [
        {"description": "Public capability."},
        {"properties": {"value": {"description": "payload-canary S12-A-07"}}},
    ]}}}
    (public_schemas / relative).write_text(json.dumps(schema), encoding="utf-8")
    site = _site(tmp_path, "<p>Public capability.</p>")
    assert _main_with_real_registered_guards(gate, monkeypatch, site) == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == f"FAIL [check_schema_descriptions_no_private_identifiers]: {SCHEMA_LEAK}\n"
    assert "payload-canary" not in captured.err
    assert str(public_schemas) not in captured.err


def test_schema_comments_and_other_string_values_remain_allowed(gate, public_schemas, tmp_path):
    schema = {
        "$comment": "REQ-004 EDGE-001 S12-A-07",
        "description": "Public capability.",
        "title": "REQ-004",
        "$defs": {"value": {"$comment": "REQ-018", "description": "Public value."}},
        "examples": [{"text": "REQ-004"}],
    }
    for relative in SCHEMA_PATHS:
        (public_schemas / relative).write_text(json.dumps(schema), encoding="utf-8")
    before = {path: (public_schemas / path).read_bytes() for path in SCHEMA_PATHS}
    gate.check_schema_descriptions_no_private_identifiers(tmp_path)
    assert before == {path: (public_schemas / path).read_bytes() for path in SCHEMA_PATHS}


def test_real_five_schema_sources_are_description_clean_and_unchanged(gate, tmp_path):
    assert tuple(gate._PUBLIC_SCHEMA_PATHS) == SCHEMA_PATHS
    paths = [gate.REPO_ROOT / relative for relative in SCHEMA_PATHS]
    before = {path: path.read_bytes() for path in paths}
    descriptions = 0
    comments = 0
    pending = [json.loads(data) for data in before.values()]
    while pending:
        node = pending.pop()
        if isinstance(node, dict):
            descriptions += isinstance(node.get("description"), str)
            comments += isinstance(node.get("$comment"), str)
            pending.extend(node.values())
        elif isinstance(node, list):
            pending.extend(node)
    assert descriptions > 0
    assert comments > 0
    gate.check_schema_descriptions_no_private_identifiers(tmp_path)
    assert before == {path: path.read_bytes() for path in paths}


@pytest.mark.parametrize("kind", ["missing", "malformed", "utf8", "deep", "decoder-depth", "not-object"])
def test_schema_unreadable_malformed_and_deep_inputs_fail_safely(
    gate, public_schemas, tmp_path, monkeypatch, capsys, kind
):
    path = public_schemas / SCHEMA_PATHS[-1]
    if kind == "missing":
        path.unlink()
    elif kind == "malformed":
        path.write_text('{"payload-canary":', encoding="utf-8")
    elif kind == "utf8":
        path.write_bytes(b"payload-canary\xff")
    elif kind == "not-object":
        path.write_text('["payload-canary"]', encoding="utf-8")
    else:
        depth = 130 if kind == "deep" else 10_000
        path.write_text('{"x":' * depth + '"payload-canary"' + '}' * depth, encoding="utf-8")
    site = _site(tmp_path, "<p>Public capability.</p>")
    assert _main_with_real_registered_guards(gate, monkeypatch, site) == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == f"FAIL [check_schema_descriptions_no_private_identifiers]: {SCHEMA_INVALID}\n"


@pytest.mark.parametrize("kind", ["malformed", "utf8", "deep", "decoder-depth"])
def test_built_json_invalid_inputs_fail_safely(gate, tmp_path, kind):
    site = _site(tmp_path, "{}", "search_index.json")
    path = site / "search_index.json"
    if kind == "malformed":
        path.write_text('{"payload-canary":', encoding="utf-8")
    elif kind == "utf8":
        path.write_bytes(b"payload-canary\xff")
    else:
        depth = 130 if kind == "deep" else 10_000
        path.write_text("[" * depth + '"payload-canary"' + "]" * depth, encoding="utf-8")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_INVALID


def test_registered_main_accepts_clean_prose_and_comment_only_schemas(
    gate, public_schemas, tmp_path, monkeypatch, capsys
):
    for relative in SCHEMA_PATHS:
        (public_schemas / relative).write_text(
            json.dumps({"$comment": "REQ-004", "description": "Public capability."}), encoding="utf-8"
        )
    site = _site(tmp_path, "<p>Public capability.</p><!-- EDGE-001 -->")
    assert _main_with_real_registered_guards(gate, monkeypatch, site) == 0
    assert capsys.readouterr().err == ""



def test_schema_permission_error_diagnostics_do_not_expose_exception_payload(
    gate, public_schemas, tmp_path, monkeypatch
):
    target = public_schemas / SCHEMA_PATHS[-1]
    original = Path.read_text

    def unreadable(path, *args, **kwargs):
        if path == target:
            raise PermissionError("payload-canary", str(target))
        return original(path, *args, **kwargs)

    monkeypatch.setattr(Path, "read_text", unreadable)
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_schema_descriptions_no_private_identifiers(tmp_path)
    assert str(error.value) == SCHEMA_INVALID


def test_schema_work_limit_fails_closed(gate, public_schemas, tmp_path, monkeypatch):
    monkeypatch.setattr(gate, "_PUBLICATION_JSON_MAX_NODES", 2)
    (public_schemas / SCHEMA_PATHS[-1]).write_text(
        json.dumps({"anyOf": [{"description": "Public value."}] * 3}), encoding="utf-8"
    )
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_schema_descriptions_no_private_identifiers(tmp_path)
    assert str(error.value) == SCHEMA_INVALID


def test_built_html_parser_failure_diagnostics_are_fixed(gate, tmp_path):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, "<![payload-canary[>"))
    assert str(error.value) == TEXT_INVALID


def test_published_read_error_diagnostics_are_fixed(gate, tmp_path, monkeypatch):
    site = _site(tmp_path, "<p>Public capability.</p>")
    target = site / "index.html"
    original = Path.open

    def unreadable(path, *args, **kwargs):
        if path == target:
            raise PermissionError("payload-canary", str(target))
        return original(path, *args, **kwargs)

    monkeypatch.setattr(Path, "open", unreadable)
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_INVALID


def test_published_json_schema_source_comments_remain_allowed(gate, tmp_path):
    value = {"$schema": "https://json-schema.org/draft/2020-12/schema",
             "$comment": "REQ-004 EDGE-001 S12-A-07",
             "$defs": {"value": {"$comment": "REQ-018", "description": "Public value."}}}
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "schema.json"))


def test_search_text_cannot_disguise_visible_identifier_as_a_comment_string(gate, tmp_path):
    value = {"docs": [{"text": '{"$comment": "REQ-004"}'}]}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "search_index.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("check", ["schema", "published"])
def test_property_named_comment_cannot_hide_nested_description(
    gate, public_schemas, tmp_path, check
):
    schema = {"$schema": "https://json-schema.org/draft/2020-12/schema",
              "properties": {"$comment": {"type": "string", "description": "REQ-004"}}}
    if check == "schema":
        (public_schemas / SCHEMA_PATHS[-1]).write_text(json.dumps(schema), encoding="utf-8")
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_schema_descriptions_no_private_identifiers(tmp_path)
        assert str(error.value) == SCHEMA_LEAK
    else:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(_site(tmp_path, json.dumps(schema), "schema.json"))
        assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("value", [
    {"$comment": "REQ-004"},
    {"records": [{"$comment": "EDGE-001"}]},
    {"properties": {"entry": {"$comment": "S12-A-07"}}},
    {"nested": {"$schema": "https://json-schema.org/draft/2020-12/schema", "$comment": "REQ-004"}},
])
def test_registered_main_refuses_generic_json_comment_fields(
    gate, public_schemas, tmp_path, monkeypatch, capsys, value
):
    site = _site(tmp_path, json.dumps(value), "public-data.json")
    assert _main_with_real_registered_guards(gate, monkeypatch, site) == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err == f"FAIL [check_no_private_identifiers]: {TEXT_LEAK}\n"


@pytest.mark.parametrize("field", ["examples", "default", "const", "enum", "unknown-extension"])
def test_declared_schema_instance_data_does_not_inherit_comment_exemption(gate, tmp_path, field):
    value = {"$schema": "https://json-schema.org/draft/2020-12/schema",
             "$comment": "REQ-004", field: {"$comment": "EDGE-001"}}
    if field in {"examples", "enum"}:
        value[field] = [value[field]]
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "schema.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("dialect", [
    "https://json-schema.org/draft/2020-12/schema",
    "https://json-schema.org/draft/2020-12/schema#",
])
@pytest.mark.parametrize("keyword", ["$defs", "properties", "patternProperties", "dependentSchemas"])
def test_declared_schema_map_annotations_remain_allowed(gate, tmp_path, dialect, keyword):
    value = {"$schema": dialect, "$comment": "REQ-004",
             keyword: {"entry": {"$comment": "EDGE-001", "description": "Public capability."}}}
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "schema.json"))


@pytest.mark.parametrize("keyword", ["allOf", "anyOf", "oneOf", "prefixItems"])
def test_declared_schema_array_annotations_remain_allowed(gate, tmp_path, keyword):
    value = {"$schema": "https://json-schema.org/draft/2020-12/schema",
             keyword: [{"$comment": "REQ-004", "description": "Public capability."}]}
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "schema.json"))


@pytest.mark.parametrize("keyword", [
    "items", "contains", "additionalProperties", "unevaluatedProperties",
    "unevaluatedItems", "propertyNames", "not", "if", "then", "else", "contentSchema",
])
def test_declared_single_subschema_annotations_remain_allowed(gate, tmp_path, keyword):
    value = {"$schema": "https://json-schema.org/draft/2020-12/schema",
             keyword: {"$comment": "REQ-004", "description": "Public capability."}}
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "schema.json"))


@pytest.mark.parametrize("relative", SCHEMA_PATHS)
def test_actual_published_schema_sources_keep_annotations_allowed_and_bytes_unchanged(
    gate, tmp_path, relative
):
    source = gate.REPO_ROOT / relative
    original = source.read_bytes()
    site = tmp_path / "actual-schema-output"
    site.mkdir()
    (site / "schema.json").write_bytes(original)
    gate.check_no_private_identifiers(site)
    assert source.read_bytes() == original
    assert (site / "schema.json").read_bytes() == original


@pytest.mark.parametrize("dialect", [None, 123, {}, [], "https://example.invalid/schema"])
def test_nonschema_dialect_values_do_not_exempt_public_comment_fields(gate, tmp_path, dialect):
    value = {"$schema": dialect, "$comment": "REQ-004"}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "public-data.json"))
    assert str(error.value) == TEXT_LEAK


# These cases execute the ordinary CLI with all 21 checks registered. The
# synthetic site supplies existing publication prerequisites without a build
# dependency or replacement checks; separate review probes use real MkDocs output.
@pytest.fixture
def full_cli_site(tmp_path, gate):
    site = tmp_path / "full-publication-site"
    site.mkdir()
    origin, _ = gate._read_site_url_prefix()
    (site / "index.html").write_text(
        '<html><head><title>Public capability</title>'
        f'<meta property="og:image" content="{origin}og-image.png">'
        f'<meta name="twitter:image" content="{origin}og-image.png">'
        '<meta property="og:image:width" content="1200">'
        '<meta property="og:image:height" content="630">'
        '<meta property="og:image:alt" content="Public capability">'
        '<meta name="twitter:card" content="summary_large_image">'
        '</head><body><span class="brand__name">Public capability</span> </body></html>',
        encoding="utf-8",
    )
    shutil.copyfile(gate.REPO_ROOT / "site/og-image.png", site / "og-image.png")
    for name in (".nojekyll", "robots.txt", "llms.txt", "404.html"):
        (site / name).write_text("", encoding="utf-8")
    sitemap = f'<urlset><url><loc>{origin}</loc></url></urlset>'.encode()
    (site / "sitemap.xml").write_bytes(sitemap)
    (site / "sitemap.xml.gz").write_bytes(gzip.compress(sitemap))
    for legacy, slug in gate.build_redirect_table():
        page = site / slug / "index.html"
        page.parent.mkdir(parents=True, exist_ok=True)
        page.write_text("<p>Public capability</p>", encoding="utf-8")
        stub = site / legacy
        stub.parent.mkdir(parents=True, exist_ok=True)
        target = gate.relative_target(legacy, slug)
        stub.write_text(
            f'<meta http-equiv="refresh" content="0;url={target}">'
            f'<link rel="canonical" href="{origin}{slug}/">',
            encoding="utf-8",
        )
    for slug in (*gate.KEY_PAGE_SLUGS, "04_AI_Companion_Feasibility_and_Design"):
        page = site / slug / "index.html"
        page.parent.mkdir(parents=True, exist_ok=True)
        page.write_text('<pre class="mermaid">flowchart TD; A-->B;</pre>', encoding="utf-8")
    bundle = site / "assets/javascripts/bundle.test.min.js"
    bundle.parent.mkdir(parents=True, exist_ok=True)
    bundle.write_text(
        'const diagram = "https://cdn.jsdelivr.net/npm/mermaid@11.16.0/dist/mermaid.min.js";',
        encoding="utf-8",
    )
    return site


def _full_cli(site, expected):
    result = subprocess.run(
        [sys.executable, str(CHECK_SITE_PATH), str(site)], capture_output=True,
    )
    assert result.returncode == expected, result.stdout + result.stderr
    if expected:
        assert result.stdout == b""
        assert result.stderr.replace(b"\r\n", b"\n") == f"FAIL [check_no_private_identifiers]: {TEXT_LEAK}\n".encode()
        assert str(site).encode() not in result.stderr
        assert b"payload-canary" not in result.stderr
    else:
        assert result.stderr == b""
        assert b"passed all publication checks." in result.stdout


@pytest.mark.parametrize("value", [
    {"$comment": "<!-- payload-canary REQ-004 -->"},
    {"$comment": "<script>payload-canary REQ-004</script>"},
    {"$comment": "<style>/* payload-canary REQ-004 */</style>"},
    {"message": "<REQ-004>"},
    {"REQ-004": "Public capability."},
    {"records": [{"EDGE-001": "Public capability."}]},
    {"$schema": "https://json-schema.org/draft/2020-12/schema",
     "examples": [{"$comment": "<!-- payload-canary REQ-004 -->"}]},
    {"$schema": "https://json-schema.org/draft/2020-12/schema",
     "default": {"$comment": "<script>REQ-004</script>"}},
    {"$schema": "https://json-schema.org/draft/2020-12/schema",
     "properties": {"REQ-004": {"description": "Public capability."}}},
    {"$schema": "https://json-schema.org/draft/2020-12/schema",
     "properties": {"$comment": {"description": "<style>REQ-004</style>"}}},
    {"message": "REQ-&#48;04"},
    {"message": "<span>REQ-</span><code>004</code>"},
    {"<span>REQ-</span><code>004</code>": "Public capability."},
])
def test_full_cli_json_data_cannot_acquire_html_source_exemptions(full_cli_site, value):
    (full_cli_site / "public-data.json").write_text(json.dumps(value), encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("value", [
    {"$schema": "https://json-schema.org/draft/2020-12/schema",
     "$comment": "<!-- REQ-004 -->",
     "$defs": {"value": {"$comment": "<script>EDGE-001</script>",
                        "description": "Public capability."}}},
    {"message": "<p>REQ-</p><p>004</p>"},
    {"PreREQ-004Suffix": "Public capability."},
])
def test_full_cli_preserves_schema_annotations_and_json_text_boundaries(full_cli_site, value):
    (full_cli_site / "public-data.json").write_text(json.dumps(value), encoding="utf-8")
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("markup", [
    *[f'<input type="{kind}" {attribute}="REQ-004">'
      for kind in ("text", "search", "tel", "url", "email")
      for attribute in ("value", "placeholder")],
    *[f'<input type="{kind}" value="REQ-004">' for kind in ("submit", "reset", "button")],
    *[f'<input type="{kind}" {attribute}="REQ-004">'
      for kind in ("", "invalid-type", " text ", "TeXT")
      for attribute in ("value", "placeholder")],
    '<input value="REQ-004">',
    '<input placeholder="REQ-004">',
    '<input type="password" placeholder="REQ-004">',
    '<input type="number" placeholder="REQ-004">',
    '<textarea placeholder="REQ-004"></textarea>',
    '<INPUT TYPE="SUBMIT" VALUE="REQ-&#48;04">',
    '<input type="text" type="hidden" value="REQ-004">',
    '<code>REQ-004</code>',
    '<pre>REQ-004</pre>',
])
def test_full_cli_checks_visible_control_text_and_code(full_cli_site, markup):
    index = full_cli_site / "index.html"
    index.write_text(index.read_text(encoding="utf-8").replace("</body>", markup + "</body>"),
                     encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("markup", [
    *[f'<input type="{kind}" value="REQ-004">'
      for kind in ("hidden", "password", "checkbox", "radio", "color", "date", "month",
                   "week", "time", "datetime-local", "range", "number", "file", "image")],
    '<input type="hidden" type="text" value="REQ-004">',
    '<input type="checkbox" placeholder="REQ-004">',
    '<textarea value="REQ-004">Public capability</textarea>',
    '<button value="REQ-004">Public capability</button>',
    '<option value="REQ-004">Public capability</option>',
    '<!-- <input value="REQ-004"> -->',
    '<script>const source = \'<input value="REQ-004">\';</script>',
    '<style>/* REQ-004 */</style>',
])
def test_full_cli_keeps_opaque_values_and_actual_html_sources_allowed(full_cli_site, markup):
    index = full_cli_site / "index.html"
    index.write_text(index.read_text(encoding="utf-8").replace("</body>", markup + "</body>"),
                     encoding="utf-8")
    _full_cli(full_cli_site, 0)


def _append_html(site, markup):
    page = site / "index.html"
    page.write_text(page.read_text(encoding="utf-8").replace("</body>", markup + "</body>"),
                    encoding="utf-8")


def _srcdoc(content, depth=1):
    for _ in range(depth):
        content = f'<iframe srcdoc="{escape(content, quote=True)}"></iframe>'
    return content


def _full_cli_invalid(site):
    result = subprocess.run([sys.executable, str(CHECK_SITE_PATH), str(site)], capture_output=True)
    assert result.returncode == 1
    assert result.stdout == b""
    assert result.stderr.replace(b"\r\n", b"\n") == (
        f"FAIL [check_no_private_identifiers]: {TEXT_INVALID}\n".encode()
    )


@pytest.mark.parametrize("tag", ["textarea", "title", "xmp", "plaintext"])
@pytest.mark.parametrize("content", [
    "<!-- REQ-004 -->", "<script>REQ-004</script>",
    "<style>REQ-004</style>", "<REQ-004>", "REQ-004",
])
def test_full_cli_literal_contexts_keep_source_spellings_visible(full_cli_site, tag, content):
    _append_html(full_cli_site, f"<{tag}>{content}</{tag}>")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("tag", ["textarea", "title", "xmp", "plaintext"])
@pytest.mark.parametrize("ending", ["", "</wrong>", "</ textarea>", "</textareax>"])
def test_literal_context_unclosed_or_wrong_end_tags_preserve_text(gate, tmp_path, tag, ending):
    with pytest.raises(gate.CheckFailed, match=TEXT_LEAK):
        gate.check_no_private_identifiers(_site(tmp_path, f"<{tag}>Public {ending}<!-- REQ-004 -->"))


@pytest.mark.parametrize("tag", ["textarea", "title", "xmp"])
@pytest.mark.parametrize("ending", ["normal", "uppercase", "space", "slash", "attributes"])
def test_literal_matching_end_tag_restores_real_source_exemptions(gate, tmp_path, tag, ending):
    ends = {"normal": f"</{tag}>", "uppercase": f"</{tag.upper()}>",
            "space": f"</{tag}\t >", "slash": f"</{tag}/>",
            "attributes": f'</{tag} ignored="greater > sign">'}
    gate.check_no_private_identifiers(_site(tmp_path, f"<{tag}>Public{ends[ending]}<!-- REQ-004 -->"))


@pytest.mark.parametrize("tag", ["textarea", "title"])
@pytest.mark.parametrize("content", ["REQ-&#48;04", "&lt;!-- REQ-004 --&gt;"])
def test_rcdata_entities_decode_once(gate, tmp_path, tag, content):
    with pytest.raises(gate.CheckFailed, match=TEXT_LEAK):
        gate.check_no_private_identifiers(_site(tmp_path, f"<{tag}>{content}</{tag}>"))
    gate.check_no_private_identifiers(_site(tmp_path, f"<{tag}>REQ-&amp;#48;04</{tag}>"))


@pytest.mark.parametrize("tag", ["xmp", "plaintext"])
def test_raw_literal_entities_stay_literal_and_plaintext_never_closes(gate, tmp_path, tag):
    gate.check_no_private_identifiers(_site(tmp_path, f"<{tag}>REQ-&#48;04</{tag}>"))
    if tag == "plaintext":
        with pytest.raises(gate.CheckFailed, match=TEXT_LEAK):
            gate.check_no_private_identifiers(_site(tmp_path, "<plaintext>Public</plaintext><!-- REQ-004 -->"))


@pytest.mark.parametrize("tag", ["textarea", "title", "xmp", "plaintext"])
def test_self_closing_literal_element_still_consumes_text(gate, tmp_path, tag):
    with pytest.raises(gate.CheckFailed, match=TEXT_LEAK):
        gate.check_no_private_identifiers(_site(tmp_path, f"<{tag}/><!-- REQ-004 -->"))


def test_rcdata_chunked_feed_and_eof_do_not_drop_entities(gate):
    parser = gate._PublishedText()
    for chunk in ("<text", "area>REQ-&#", "48;04</text", "area><p>Public</p>"):
        parser.feed(chunk)
    parser.close()
    assert parser.contains_identifier()
    parser = gate._PublishedText()
    parser.feed("<textarea>REQ-&#48;04")
    parser.close()
    assert parser.contains_identifier()


@pytest.mark.parametrize("markup", [
    '<select><option label="REQ-004">Public</option></select>',
    '<select><optgroup label="EDGE-001"><option>Public</option></optgroup></select>',
    '<video><track kind="captions" label="S12-A-07"></video>',
    '<track label="REQ-&#48;04">',
    '<option label="REQ-004" label="Public">Public</option>',
])
def test_full_cli_visible_selection_and_track_labels(full_cli_site, markup):
    _append_html(full_cli_site, markup)
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("markup", [
    '<option value="REQ-004">Public</option>',
    '<option label="Public" label="REQ-004">Public</option>',
    '<input type="hidden" value="REQ-004">',
    '<input type="radio" value="REQ-004">',
    '<input type="password" value="REQ-004">',
    '<input type="checkbox" value="REQ-004">',
    '<input value="Public" value="REQ-004">',
])
def test_full_cli_labels_do_not_turn_opaque_or_duplicate_values_into_text(full_cli_site, markup):
    _append_html(full_cli_site, markup)
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("depth", [1, 2, 3, 8])
@pytest.mark.parametrize("content", ["<p>REQ-004</p>", "<p>REQ-<span>004</span></p>",
                                     "<textarea><!-- REQ-004 -->", "<p>REQ-&#48;04</p>"])
def test_full_cli_embedded_document_text_is_checked(full_cli_site, depth, content):
    _append_html(full_cli_site, _srcdoc(content, depth))
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("depth", [1, 2, 8])
@pytest.mark.parametrize("content", ["<!-- REQ-004 --><p>Public</p>",
                                     "<script>REQ-004</script><style>REQ-004</style>",
                                     "<p>REQ-</p><p>004</p>"])
def test_full_cli_embedded_sources_and_block_boundaries_remain_allowed(full_cli_site, depth, content):
    _append_html(full_cli_site, _srcdoc(content, depth))
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("markup", [
    'REQ-<iframe srcdoc="004"></iframe>',
    '<iframe srcdoc="REQ-"></iframe>004',
    '<iframe srcdoc="REQ-"></iframe><iframe srcdoc="004"></iframe>',
    '<iframe srcdoc="&lt;!-- REQ-004 --&gt;" srcdoc="REQ-004"></iframe>',
    '<!-- <iframe srcdoc="REQ-004"></iframe> -->',
    '<script>const x=\'<iframe srcdoc="REQ-004"></iframe>\';</script>',
])
def test_full_cli_embedded_context_is_isolated_from_parent_and_sources(full_cli_site, markup):
    _append_html(full_cli_site, markup)
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("depth", [0, 1, 2, 8])
def test_full_cli_invalid_outer_and_embedded_declarations_have_fixed_diagnostics(full_cli_site, depth):
    _append_html(full_cli_site, _srcdoc("<![payload-canary[>", depth))
    _full_cli_invalid(full_cli_site)


@pytest.mark.parametrize("depth", [8, 9])
def test_full_cli_embedded_depth_at_and_over_budget(full_cli_site, depth):
    _append_html(full_cli_site, _srcdoc("<p>Public</p>", depth))
    if depth == 8:
        _full_cli(full_cli_site, 0)
    else:
        _full_cli_invalid(full_cli_site)


@pytest.mark.parametrize("over", [False, True])
@pytest.mark.parametrize("embedded", [False, True])
def test_full_cli_document_size_at_and_over_budget(full_cli_site, gate, over, embedded):
    if embedded:
        _append_html(full_cli_site, _srcdoc("x" * (gate._PUBLICATION_EMBEDDED_MAX_CHARS + over)))
    else:
        page = full_cli_site / "index.html"
        text = page.read_text(encoding="utf-8")
        _append_html(full_cli_site, "x" * (gate._PUBLICATION_HTML_MAX_CHARS - len(text) + over))
    if over:
        _full_cli_invalid(full_cli_site)
    else:
        _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("over", [False, True])
def test_full_cli_wide_embedding_at_and_over_shared_document_budget(full_cli_site, gate, over):
    # Count includes the outer document. Empty siblings still consume work.
    _append_html(full_cli_site, _srcdoc("") * (gate._PUBLICATION_HTML_MAX_DOCUMENTS - 1 + over))
    if over:
        _full_cli_invalid(full_cli_site)
    else:
        _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("over", [False, True])
def test_shared_character_work_at_and_over_budget_uses_all_siblings(gate, monkeypatch, tmp_path, over):
    markup = _srcdoc("<p>Public</p>") + _srcdoc("<p>Capability</p>")
    # Small deterministic budgets exercise the production shared accumulator,
    # rather than generating a 32 Mi-character fixture for each unit case.
    work = len(markup) + len("<p>Public</p>") + len("<p>Capability</p>")
    monkeypatch.setattr(gate, "_PUBLICATION_HTML_MAX_WORK", work - over)
    if over:
        with pytest.raises(gate.CheckFailed, match=TEXT_INVALID):
            gate.check_no_private_identifiers(_site(tmp_path, markup))
    else:
        gate.check_no_private_identifiers(_site(tmp_path, markup))


def test_embedded_eof_and_parent_literal_context_do_not_share_tokenizer_state(gate, tmp_path):
    # Embedded open script only suppresses that child; parent text remains text.
    with pytest.raises(gate.CheckFailed, match=TEXT_LEAK):
        gate.check_no_private_identifiers(_site(tmp_path, _srcdoc("<script>Public") + "<p>REQ-004</p>"))
    gate.check_no_private_identifiers(_site(tmp_path, "<textarea>" + _srcdoc("REQ-&#48;04")))


@pytest.mark.parametrize("content", [
    "<!-- REQ-<span>004</span> -->", "<script>REQ-<span>004</span></script>",
    "<style>REQ-<span>004</span></style>", "<!-- REQ-<span>004</span>",
])
@pytest.mark.parametrize("key", [False, True])
def test_full_cli_json_data_projection_has_no_html_source_exemptions(full_cli_site, content, key):
    value = {content: "Public"} if key else {"message": content}
    (full_cli_site / "public-data.json").write_text(json.dumps(value), encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("content", [
    "<!-- <p>REQ-</p><p>004</p> -->", "<script><p>REQ-</p><p>004</p></script>",
    "<style><p>REQ-</p><p>004</p></style>", "REQ-<!-- Public -->004",
])
def test_full_cli_json_data_projection_keeps_real_text_boundaries(full_cli_site, content):
    (full_cli_site / "public-data.json").write_text(json.dumps({"message": content}), encoding="utf-8")
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("over", [False, True])
def test_full_cli_shared_character_work_at_and_over_production_budget(full_cli_site, gate, over):
    # Four wide, eight-deep chains exercise the real aggregate ceiling while
    # every individual document remains below its own size/depth ceiling.
    chain = "x" * 900_000
    child_work = 0
    for _ in range(8):
        child_work += len(chain)
        chain = _srcdoc(chain)
    markup = chain * 4
    page = full_cli_site / "index.html"
    root_size = len(page.read_text(encoding="utf-8")) + len(markup)
    remaining = gate._PUBLICATION_HTML_MAX_WORK - (root_size + 4 * child_work)
    assert 0 < remaining and root_size + remaining + over <= gate._PUBLICATION_HTML_MAX_CHARS
    _append_html(full_cli_site, markup + "x" * (remaining + over))
    if over:
        _full_cli_invalid(full_cli_site)
    else:
        _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("markup", [
    '<input type="hidden" value="REQ-<span>004</span>">',
    '<span data-label="REQ-<span>004</span>"></span>',
    '<input type="password" value="EDGE-<b>001</b>">',
    '<input type="checkbox" value="S12-A-<i>07</i>">',
    '<input value="Public" value="REQ-<span>004</span>">',
])
@pytest.mark.parametrize("key", [False, True])
def test_full_cli_json_all_attribute_strings_are_data(full_cli_site, markup, key):
    value = {markup: "Public"} if key else {"message": markup}
    (full_cli_site / "public-data.json").write_text(json.dumps(value), encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("markup", [
    '<span data-a="REQ-" data-b="004"></span>',
    '<span data-a="<p>REQ-</p><p>004</p>"></span>',
    '<input value="REQ-&amp;#48;04">',
    '<input value="REQ-&amp;#99;004">',
    'REQ-<input value="004">',
])
def test_full_cli_json_attribute_projection_preserves_boundaries_and_literal_entities(full_cli_site, markup):
    (full_cli_site / "public-data.json").write_text(json.dumps({"message": markup}), encoding="utf-8")
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("tag", ["script", "style"])
@pytest.mark.parametrize("close", [' bogus>', '/>', ' data-clean="greater > sign">',
                                   ' data-clean=unquoted"quote>'])
def test_full_cli_appropriate_source_end_tags_restore_prose(full_cli_site, tag, close):
    _append_html(full_cli_site, f"<{tag}>Public source</{tag}{close}REQ-004")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("tag", ["script", "style"])
@pytest.mark.parametrize("content", ["REQ-004", "Public</ wrong>REQ-004", "Public</scriptx>REQ-004"])
def test_full_cli_nonmatching_source_closes_do_not_make_source_prose(full_cli_site, tag, content):
    _append_html(full_cli_site, f"<{tag} title='REQ-004'>{content}</{tag}>")
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("serialized", [
    '{"message":"payload-canary REQ-004","message":"Public capability."}',
    '{"message":"Public capability.","message":"payload-canary REQ-004"}',
    '{"message":"Public.","message":"EDGE-018","message":"Public."}',
    '{"nested":{"message":"REQ-004","message":"Public."}}',
    '[{"message":"REQ-004","message":"Public."}]',
    '{"record":{"message":"REQ-004"},"record":{"message":"Public."}}',
    r'{"message":"REQ-004","messa\u0067e":"Public."}',
    '{"message":"REQ-&#48;04","message":"Public."}',
    '{"message":"REQ-<span>004</span>","message":"Public."}',
    '{"message":"<!-- REQ-<span>004</span> -->","message":"Public."}',
    '{"message":' + json.dumps('<input type=hidden value="REQ-<span>004</span>">') + ',"message":"Public."}',
])
def test_full_cli_duplicate_json_keeps_every_serialized_value(full_cli_site, serialized):
    (full_cli_site / "public-data.json").write_text(serialized, encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("serialized", [
    '{"message":"Public.","message":"Also public."}',
    '{"message":"REQ-","message":"004"}',
    '{"REQ-":"004","REQ-":"Public."}',
    '{"message":"<span>REQ-</span>","message":"<span>004</span>"}',
    '{"message":"<p>REQ-</p><p>004</p>","message":"Public."}',
    '{"record":{"message":"REQ-"},"record":{"message":"004"}}',
    r'{"message":"Public.","messa\u0067e":"Also public."}',
])
def test_full_cli_duplicate_json_keeps_clean_fields_independent(full_cli_site, serialized):
    (full_cli_site / "public-data.json").write_text(serialized, encoding="utf-8")
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("reversed_order", [False, True])
@pytest.mark.parametrize("nested", [False, True])
def test_full_cli_duplicate_schema_conflicting_declarations_are_data(full_cli_site, reversed_order, nested):
    declarations = '"$schema":"https://json-schema.org/draft/2020-12/schema","$schema":"https://example.invalid/schema"'
    if reversed_order:
        declarations = ','.join(reversed(declarations.split(',')))
    serialized = '{' + declarations + ',"$comment":"REQ-004"}'
    if nested:
        serialized = '{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"record":' + serialized + '}}'
    (full_cli_site / "schema.json").write_text(serialized, encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("serialized", [
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","$schema":"https://json-schema.org/draft/2020-12/schema#","$comment":"REQ-004","$comment":"EDGE-018"}',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"record":{"$comment":"REQ-004","$comment":"EDGE-018"}}}',
    '{"$schema":"https://example.invalid/schema","$schema":"https://json-schema.org/draft/2020-12/schema","$comment":"Public."}',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","$schema":null,"$comment":"Public."}',
])
def test_full_cli_duplicate_schema_clean_data_and_true_annotations_remain_allowed(full_cli_site, serialized):
    (full_cli_site / "schema.json").write_text(serialized, encoding="utf-8")
    _full_cli(full_cli_site, 0)


@pytest.mark.parametrize("serialized", [
    '{"$comment":"REQ-004","$comment":"Public."}',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","examples":[{"$comment":"REQ-004","$comment":"Public."}]}',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"$comment":{"description":"REQ-004","description":"Public."}}}',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","$comment":{"message":"REQ-004"},"$comment":"Public."}',
    '{"$schema":"https://json-schema.org/draft/2020-12/schema","$defs":{"record":{"description":"REQ-004"}},"$defs":{"record":{"description":"Public."}}}',
])
def test_full_cli_duplicate_json_cannot_hide_nonannotation_data(full_cli_site, serialized):
    (full_cli_site / "schema.json").write_text(serialized, encoding="utf-8")
    _full_cli(full_cli_site, 1)


@pytest.mark.parametrize("relative", SCHEMA_PATHS)
@pytest.mark.parametrize("nested", [False, True])
def test_source_duplicate_description_is_checked_in_each_canonical_schema(gate, public_schemas, tmp_path, relative, nested):
    serialized = '{"description":"payload-canary REQ-004","description":"Public."}'
    if nested:
        serialized = '{"anyOf":[' + serialized + ']}'
    (public_schemas / relative).write_text(serialized, encoding="utf-8")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_schema_descriptions_no_private_identifiers(tmp_path)
    assert str(error.value) == SCHEMA_LEAK


def test_source_duplicate_description_keeps_annotations_allowed(gate, public_schemas, tmp_path):
    serialized = '{"description":"Public.","description":"Also public.","$comment":"REQ-004","$comment":"EDGE-018"}'
    for relative in SCHEMA_PATHS:
        (public_schemas / relative).write_text(serialized, encoding="utf-8")
    gate.check_schema_descriptions_no_private_identifiers(tmp_path)


@pytest.mark.parametrize("over", [False, True])
@pytest.mark.parametrize("annotation", [False, True])
def test_duplicate_json_and_preserved_annotation_values_count_toward_node_budget(gate, tmp_path, monkeypatch, over, annotation):
    serialized = '{"message":"Public.","message":"Also public."}'
    nodes = 3
    if annotation:
        serialized = '{"$schema":"https://json-schema.org/draft/2020-12/schema","$comment":"REQ-004","$comment":"EDGE-018"}'
        nodes = 4
    monkeypatch.setattr(gate, "_PUBLICATION_JSON_MAX_NODES", nodes - int(over))
    site = _site(tmp_path, serialized, "public-data.json")
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(site)
        assert str(error.value) == TEXT_INVALID
    else:
        gate.check_no_private_identifiers(site)


@pytest.mark.parametrize("over", [False, True])
def test_duplicate_description_values_count_toward_source_node_budget(gate, public_schemas, tmp_path, monkeypatch, over):
    serialized = '{"description":"Public.","description":"Also public."}'
    (public_schemas / SCHEMA_PATHS[-1]).write_text(serialized, encoding="utf-8")
    monkeypatch.setattr(gate, "_PUBLICATION_JSON_MAX_NODES", 3 - int(over))
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_schema_descriptions_no_private_identifiers(tmp_path)
        assert str(error.value) == SCHEMA_INVALID
    else:
        gate.check_schema_descriptions_no_private_identifiers(tmp_path)


@pytest.mark.parametrize("annotation", [False, True])
@pytest.mark.parametrize("over", [False, True])
def test_duplicate_json_storage_does_not_add_depth_and_annotations_count(gate, tmp_path, monkeypatch, annotation, over):
    serialized = '{"message":"Public.","message":"Also public."}'
    depth = 1
    if annotation:
        serialized = '{"$schema":"https://json-schema.org/draft/2020-12/schema","properties":{"record":{"$comment":"REQ-004"}}}'
        depth = 3
    monkeypatch.setattr(gate, "_PUBLICATION_JSON_MAX_DEPTH", depth - int(over))
    site = _site(tmp_path, serialized, "schema.json")
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(site)
        assert str(error.value) == TEXT_INVALID
    else:
        gate.check_no_private_identifiers(site)


@pytest.mark.parametrize("kind", ["characters", "documents"])
@pytest.mark.parametrize("over", [False, True])
def test_duplicate_json_strings_share_existing_projection_budgets(gate, tmp_path, monkeypatch, kind, over):
    content = "<span>Public</span>"
    serialized = '{"x":' + json.dumps(content) + ',"x":' + json.dumps(content) + '}'
    limit = (len("x") + len(content)) * 2 if kind == "characters" else 4
    constant = "_PUBLICATION_HTML_MAX_WORK" if kind == "characters" else "_PUBLICATION_HTML_MAX_DOCUMENTS"
    monkeypatch.setattr(gate, constant, limit - int(over))
    site = _site(tmp_path, serialized, "public-data.json")
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(site)
        assert str(error.value) == TEXT_INVALID
    else:
        gate.check_no_private_identifiers(site)


@pytest.mark.parametrize("identifier", ["REQ-004", "EDGE-001", "S12-A-07"])
@pytest.mark.parametrize("template", [
    "<article><![CDATA[{identifier}]]></article>",
    "<article>{prefix}<![CDATA[{suffix}]]></article>",
    "<article><![CDATA[{prefix}]]>{suffix}</article>",
    "<article><![CDATA[{prefix}]]><![CDATA[{suffix}]]></article>",
    "<script><![CDATA[{identifier}]]></script>",
    "<style><![CDATA[{identifier}]]></style>",
    "<title><![CDATA[{identifier}]]></title>",
])
def test_xml_cdata_is_literal_character_data_with_adjacent_text(gate, tmp_path, identifier, template):
    prefix, suffix = identifier.rsplit("-", 1)
    text = template.format(identifier=identifier, prefix=prefix + "-", suffix=suffix)
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, text, "public.xml"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("text", [
    "<article><![CDATA[Public capability.]]></article>",
    "<article><![CDATA[REQ-<b>004</b>]]></article>",
    "<article><![CDATA[REQ-&#48;04]]></article>",
    "<article><![CDATA[REQ-]]> <!-- Public --> 004</article>",
    "<article><a><![CDATA[REQ-]]></a><b><![CDATA[004]]></b></article>",
    "<article><!-- REQ-004 --><![CDATA[Public capability.]]></article>",
    "<article><![CDATA[REQ-]]>public<![CDATA[004]]></article>",
])
def test_xml_cdata_keeps_literal_markup_entities_comments_and_element_boundaries(gate, tmp_path, text):
    gate.check_no_private_identifiers(_site(tmp_path, text, "public.xml"))


@pytest.mark.parametrize("kind", ["CDATA", "IGNORE", "INCLUDE", "RCDATA", "TEMP"])
@pytest.mark.parametrize("template", [
    "<![{kind}[REQ-<b>004</b>]]>",
    "REQ-<![{kind}[<b>004</b>]]>",
    "<![{kind}[REQ-<b>00</b>]]>4",
    "<![{kind}[<!-- EDGE-<i>001</i> -->]]>",
    '<![{kind}[<input type="hidden" value="S12-A-<b>07</b>">]]>',
])
@pytest.mark.parametrize("key", [False, True])
@pytest.mark.parametrize("terminated", [False, True])
def test_json_marked_sections_keep_bodies_and_inline_projection_as_data(gate, tmp_path, kind, template, key, terminated):
    markup = template.format(kind=kind)
    if not terminated:
        markup = markup.replace("]]>", "")
    value = {markup: "Public"} if key else {"message": markup}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "public-data.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("markup", [
    "<![if REQ-<b>004</b> [Public]>",
    "<![else EDGE-<i>001</i>]>",
    "<![endif S12-A-<i>07</i>]>",
    "<![if REQ-<b>004</b>",
])
@pytest.mark.parametrize("key", [False, True])
def test_json_marked_sections_keep_conditional_text_before_any_bracket(gate, tmp_path, markup, key):
    value = {markup: "Public"} if key else {"message": markup}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(value), "public-data.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("serialized", [
    json.dumps({"first": "<![CDATA[REQ-]]>", "second": "<![CDATA[004]]>"}),
    json.dumps({"first": "<![CDATA[REQ-]]>", "first-copy": "<![IGNORE[004]]>"}),
    json.dumps({"message": "<![CDATA[<p>REQ-</p><p>004</p>]]>"}),
    json.dumps({"message": "<![IGNORE[Public capability.]]>"}),
    json.dumps({"message": '<![CDATA[<input data-a="REQ-" data-b="004">]]>'}),
    json.dumps({"message": '<input value="<![CDATA[REQ-&amp;#48;04]]>">'}),
])
def test_json_marked_sections_preserve_fields_blocks_attributes_and_decoding_boundaries(gate, tmp_path, serialized):
    gate.check_no_private_identifiers(_site(tmp_path, serialized, "public-data.json"))


@pytest.mark.parametrize("text", [
    "<!REQ-<b>004</b>><p>Public capability.</p>",
    "<!-- REQ-004 --><p>Public capability.</p>",
    "<script><![CDATA[REQ-004]]></script><p>Public capability.</p>",
    "<style><![CDATA[REQ-004]]></style><p>Public capability.</p>",
])
def test_marked_sections_do_not_revoke_actual_html_source_exemptions(gate, tmp_path, text):
    gate.check_no_private_identifiers(_site(tmp_path, text))


@pytest.mark.parametrize("kind", ["characters", "documents", "depth"])
@pytest.mark.parametrize("over", [False, True])
def test_json_marked_sections_charge_existing_shared_projection_budgets(gate, tmp_path, monkeypatch, kind, over):
    body = "<span>Public</span>"
    markup = f"<![CDATA[{body}]]>"
    serialized = json.dumps({"x": markup})
    limits = {
        "characters": ("_PUBLICATION_HTML_MAX_WORK", len("x") + len(markup) + len(body)),
        "documents": ("_PUBLICATION_HTML_MAX_DOCUMENTS", 3),
        "depth": ("_PUBLICATION_HTML_MAX_DEPTH", 1),
    }
    constant, limit = limits[kind]
    monkeypatch.setattr(gate, constant, limit - int(over))
    site = _site(tmp_path, serialized, "public-data.json")
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(site)
        assert str(error.value) == TEXT_INVALID
    else:
        gate.check_no_private_identifiers(site)


@pytest.mark.parametrize("over", [False, True])
def test_json_marked_sections_charge_the_production_decoded_document_limit(gate, tmp_path, over):
    body = "x" * (gate._PUBLICATION_EMBEDDED_MAX_CHARS + int(over))
    markup = f"<![CDATA[{body}]]>"
    site = _site(tmp_path, json.dumps({"message": markup}), "public-data.json")
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(site)
        assert str(error.value) == TEXT_INVALID
    else:
        gate.check_no_private_identifiers(site)


@pytest.mark.parametrize("text", ["<article><![IGNORE[Public]]></article>", "<article><![cdata[Public]]></article>"])
def test_xml_cdata_invalid_marked_section_spellings_are_refused_safely(gate, tmp_path, text):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, text, "public.xml"))
    assert str(error.value) == TEXT_INVALID


@pytest.mark.parametrize("identifier", ["REQ-004", "EDGE-001", "S12-A-07"])
@pytest.mark.parametrize("opener", ["<!DOCTYPE ", "<!", "<?"])
@pytest.mark.parametrize("wrapper", ["{}", "<![CDATA[{}]]>", "<![IGNORE[{}]]>"])
@pytest.mark.parametrize("as_key", [False, True])
@pytest.mark.parametrize("terminated", [False, True])
def test_data_lexical_bodies_remain_projected(
    gate, tmp_path, identifier, opener, wrapper, as_key, terminated
):
    left, right = identifier.rsplit("-", 1)
    body = opener + left + "-<span>" + right + "</span>" + (">" if terminated else "")
    fragment = wrapper.format(body)
    payload = {fragment: "Public capability"} if as_key else {"value": fragment}
    site = _site(tmp_path, json.dumps(payload), "data.json")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("opener", ["<!doctype ", "<!CUSTOM ", "<!? ", "<!- ", "<?target "])
def test_data_lexical_bodies_have_no_declaration_name_whitelist(gate, tmp_path, opener):
    site = _site(tmp_path, json.dumps({"value": opener + "REQ-<b>004</b>>"}), "data.json")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("fragment", [
    "</tag REQ-<b>004</b>>", "</REQ-<b>004</b>>", "</!REQ-<b>004</b>>",
    "</tag REQ-<b>004</b>", "REQ-</span data-clean='Public'>004",
])
@pytest.mark.parametrize("as_key", [False, True])
def test_data_lexical_malformed_end_tags_are_refused(gate, tmp_path, fragment, as_key):
    payload = {fragment: "Public capability"} if as_key else {"value": fragment}
    site = _site(tmp_path, json.dumps(payload), "data.json")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_INVALID


@pytest.mark.parametrize("fragment", [
    "<!DOCTYPE Public capability>", "<!CUSTOM Public capability>", "<?Public capability>",
    "<!DOCTYPE Public capability", "<!CUSTOM Public capability", "<?Public capability",
    "REQ-<!DOCTYPE 004>", "REQ-<?004>", "REQ-<!CUSTOM 004>",
    "<!DOCTYPE REQ-<div>004</div>>", "<?REQ-<div>004</div>>",
    "<!DOCTYPE REQ-&amp;#48;04>", "<?REQ-&amp;#48;04>",
    """<input value="<!DOCTYPE <x data='REQ-&amp;#48;04'>">""",
    """<input value="<?<x data='REQ-&amp;#48;04'>">""",
    """<input value="<![CDATA[<x data='REQ-&amp;#48;04'>]]>">""",
])
def test_data_lexical_legal_boundaries_and_single_decoding(gate, tmp_path, fragment):
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps({"value": fragment}), "data.json"))


@pytest.mark.parametrize("fragment", [
    "REQ-</span>004", "REQ-</SPAN >004", "REQ-</>004",
    "<!DOCTYPE REQ-&#48;04>", "<?REQ-&#48;04>",
    """<input value="<!DOCTYPE <x data='REQ-&#48;04'>">""",
])
def test_data_lexical_adjacency_and_once_decoded_identifiers(gate, tmp_path, fragment):
    site = _site(tmp_path, json.dumps({"value": fragment}), "data.json")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("opener", ["<!DOCTYPE ", "<!CUSTOM ", "<?"])
def test_data_lexical_fields_and_attributes_stay_independent(gate, tmp_path, opener):
    payload = {"first": opener + "REQ->", "second": opener + "004>",
               "attrs": "<x first='" + opener + "REQ->' second='" + opener + "004>'>"}
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps(payload), "data.json"))


@pytest.mark.parametrize("fragment", [
    "<!DOCTYPE REQ-<b>004</b>>", "<?REQ-<b>004</b>>", "<!REQ-<b>004</b>>",
])
def test_data_lexical_actual_html_sources_keep_exemptions(gate, tmp_path, fragment):
    gate.check_no_private_identifiers(_site(tmp_path, fragment + "<p>Public capability</p>"))


@pytest.mark.parametrize("chunks", [
    ["<!", "DOCTYPE REQ-", "<span>004</span>>"],
    ["<?", "target REQ-", "<span>004</span>>"],
    ["REQ-</sp", "an>004"],
    ["<!DOCTYPE REQ-&#", "48;04>"],
])
def test_data_lexical_streaming_preserves_body_and_end_tag_semantics(gate, chunks):
    projected = gate._PublishedText(data=True)
    for chunk in chunks:
        projected.feed(chunk)
    projected.close()
    assert projected.contains_identifier()


@pytest.mark.parametrize("limit", ["_PUBLICATION_HTML_MAX_WORK", "_PUBLICATION_HTML_MAX_DOCUMENTS"])
def test_data_lexical_nested_attributes_share_work_limits(gate, tmp_path, monkeypatch, limit):
    monkeypatch.setattr(gate, limit, 1)
    fragment = """<input value="<!DOCTYPE <x data='Public capability'>">"""
    site = _site(tmp_path, json.dumps({"value": fragment}), "data.json")
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(site)
    assert str(error.value) == TEXT_INVALID


@pytest.mark.parametrize("kind", ["CDATA", "IGNORE", "INCLUDE", "RCDATA", "TEMP"])
@pytest.mark.parametrize("delimiter", [" ", ":", "/"])
@pytest.mark.parametrize("terminated", [False, True])
@pytest.mark.parametrize("placement", ["direct", "marked", "attribute"])
@pytest.mark.parametrize("key", [False, True])
def test_marked_structure_refuses_unsupported_standard_headers(
    gate, tmp_path, kind, delimiter, terminated, placement, key
):
    fragment = "<![" + kind + delimiter + "REQ-<em>004</em>" + ("]]>" if terminated else "")
    if placement == "marked":
        fragment = "<![CDATA[" + fragment + "]]>"
    elif placement == "attribute":
        fragment = '<i a="' + escape(fragment, quote=True) + '">Public</i>'
    payload = {fragment: "Public"} if key else {"message": fragment}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(payload), "data.json"))
    assert str(error.value) == TEXT_INVALID


@pytest.mark.parametrize("marker", ["if", "else", "endif"])
@pytest.mark.parametrize("position", ["before", "after"])
@pytest.mark.parametrize("placement", ["direct", "marked", "attribute"])
@pytest.mark.parametrize("key", [False, True])
def test_marked_structure_conditional_names_cannot_hide_adjacent_identifiers(
    gate, tmp_path, marker, position, placement, key
):
    token = "<![" + marker + "]>"
    fragment = token + "REQ-<em>004</em>" if position == "before" else "REQ-<em>004</em>" + token
    if placement == "marked":
        fragment = "<![CDATA[" + fragment + "]]>"
    elif placement == "attribute":
        fragment = '<i a="' + escape(fragment, quote=True) + '">Public</i>'
    payload = {fragment: "Public"} if key else {"message": fragment}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(payload), "data.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("marker", ["if", "else", "endif"])
@pytest.mark.parametrize("terminated", [False, True])
@pytest.mark.parametrize("placement", ["direct", "marked", "attribute"])
@pytest.mark.parametrize("key", [False, True])
def test_marked_structure_projects_conditional_payloads_under_all_contexts(
    gate, tmp_path, marker, terminated, placement, key
):
    fragment = "<![" + marker + " REQ-<em>004</em>" + ("]>" if terminated else "")
    if placement == "marked":
        fragment = "<![CDATA[" + fragment + "]]>"
    elif placement == "attribute":
        fragment = '<i a="' + escape(fragment, quote=True) + '">Public</i>'
    payload = {fragment: "Public"} if key else {"message": fragment}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(payload), "data.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("fragment", [
    "<![", "<![CDAT", "<![CDATA", "<![CDATA ", "<![if", "<![else/", "<![UNKNOWN[Public]]>",
])
def test_marked_structure_refuses_partial_or_unknown_headers_at_eof(gate, tmp_path, fragment):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps({"message": fragment}), "data.json"))
    assert str(error.value) == TEXT_INVALID


@pytest.mark.parametrize("fragment", [
    '<![if <x a="REQ-<span>004</span>]>" >Public]>',
    '<![CDATA[<x a="REQ-<span>004</span>]]>" >Public',
    "<![CDATA[<span",
])
@pytest.mark.parametrize("key", [False, True])
def test_marked_structure_refuses_unfinished_child_markup(gate, tmp_path, fragment, key):
    payload = {fragment: "Public"} if key else {"message": fragment}
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps(payload), "data.json"))
    assert str(error.value) == TEXT_INVALID


@pytest.mark.parametrize("fragment", [
    "REQ-<![if]>004", "REQ-<![else]>004", "REQ-<![endif]>004",
    "<![if Public capability]>Public", "<![else]>Public", "<![endif]>Public",
    "<![if Public capability", "<![else Public capability", "<![endif Public capability",
    "<![if REQ-&amp;#48;04]>",
    '<i a="&lt;![if REQ-&amp;amp;#48;04]&gt;">Public</i>',
    "<![CDATA[<![if REQ-&amp;#48;04]>]]>",
])
def test_marked_structure_clean_conditions_boundaries_and_entities(gate, tmp_path, fragment):
    gate.check_no_private_identifiers(_site(tmp_path, json.dumps({"message": fragment}), "data.json"))


@pytest.mark.parametrize("fragment", ["<![if REQ-&#48;04]>", "<![else REQ-&#x30;04]>" ])
def test_marked_structure_decodes_condition_entities_once(gate, tmp_path, fragment):
    with pytest.raises(gate.CheckFailed) as error:
        gate.check_no_private_identifiers(_site(tmp_path, json.dumps({"message": fragment}), "data.json"))
    assert str(error.value) == TEXT_LEAK


@pytest.mark.parametrize("markup, expected", [
    ("REQ-<![CDATA[<b>004</b>]]>", True),
    ("<![CDATA[REQ-]]><b>004</b>", True),
    ("REQ-<b>004</b><![endif]>", True),
    ("<![else]>REQ-<b>004</b>", True),
    ("REQ-<!--004-->", False),
])
def test_marked_structure_streaming_every_split_and_single_character(gate, markup, expected):
    feeds = [[markup[:split], markup[split:]] for split in range(len(markup) + 1)] + [list(markup)]
    for chunks in feeds:
        projected = gate._PublishedText(data=True)
        for chunk in chunks:
            projected.feed(chunk)
        projected.close()
        assert projected.contains_identifier() is expected, chunks


@pytest.mark.parametrize("kind", ["characters", "documents", "depth"])
@pytest.mark.parametrize("over", [False, True])
def test_marked_structure_conditional_payloads_share_existing_budgets(gate, tmp_path, monkeypatch, kind, over):
    body = " Public capability"
    markup = "<![if" + body + "]>"
    name, limit = {
        "characters": ("_PUBLICATION_HTML_MAX_WORK", len("message") + len(markup) + len(body)),
        "documents": ("_PUBLICATION_HTML_MAX_DOCUMENTS", 3),
        "depth": ("_PUBLICATION_HTML_MAX_DEPTH", 1),
    }[kind]
    monkeypatch.setattr(gate, name, limit - int(over))
    site = _site(tmp_path, json.dumps({"message": markup}), "data.json")
    if over:
        with pytest.raises(gate.CheckFailed) as error:
            gate.check_no_private_identifiers(site)
        assert str(error.value) == TEXT_INVALID
    else:
        gate.check_no_private_identifiers(site)
