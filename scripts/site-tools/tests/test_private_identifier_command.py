"""The reusable publication command preserves the core guard's semantics."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys

import pytest

from vouchfx_site_tools.check_private_identifiers import (
    PrivateIdentifierError,
    check_private_identifiers,
)

PACKAGE_SOURCE = Path(__file__).resolve().parents[1] / "src"
CLI = "vouchfx_site_tools.check_private_identifiers"
PASS = "Private identifier check passed.\n"
LEAK = "Private identifier check failed: Published text contains an internal planning identifier.\n"
INVALID = "Private identifier check failed: Published text could not be read or parsed safely.\n"


def _run(*arguments):
    environment = os.environ.copy()
    environment["PYTHONPATH"] = str(PACKAGE_SOURCE)
    # The generator's third-party imports must not be needed by this command.
    return subprocess.run(
        [sys.executable, "-S", "-B", "-m", CLI, *map(str, arguments)],
        capture_output=True, text=True, encoding="utf-8", env=environment,
        timeout=30,
    )


@pytest.mark.parametrize("name,text", [
    ("index.html", "<p>Public capability.</p>"),
    ("index.html", "<!-- REQ-004 --><script>REQ-004</script><p>Public.</p>"),
    ("index.html", "<p>REQ-</p><p>004</p>"),
    ("message.txt", "REQ-&amp;#48;04"),
    ("schema.json", json.dumps({"$schema": "https://json-schema.org/draft/2020-12/schema", "$comment": "REQ-004", "$defs": {"value": {"$comment": "EDGE-001"}}})),
    ("schema.json", json.dumps({"$schema": "https://json-schema.org/draft/2020-12/schema", "properties": {"value": {"$comment": "REQ-004", "description": "Public value."}}})),
    ("search.json", json.dumps({"text": "REQ-&amp;#48;04"})),
    ("source.js", 'const privateSourceComment = "REQ-004";'),
])
def test_stdlib_cli_accepts_public_output_and_real_source_exemptions(tmp_path, name, text):
    (tmp_path / name).write_text(text, encoding="utf-8")
    before = {path.name: path.read_bytes() for path in tmp_path.iterdir()}
    result = _run(tmp_path)
    assert (result.returncode, result.stdout, result.stderr) == (0, PASS, "")
    assert before == {path.name: path.read_bytes() for path in tmp_path.iterdir()}
    assert check_private_identifiers(tmp_path) is None


@pytest.mark.parametrize("name,text", [
    ("index.html", "<p>REQ-004</p>"),
    ("index.html", "<p>RE<span>Q-0</span>04</p>"),
    ("index.html", '<iframe srcdoc="&lt;p&gt;REQ-004&lt;/p&gt;"></iframe>'),
    ("index.html", '<input placeholder="REQ-004">'),
    ("message.txt", "REQ-&#48;04"),
    ("document.xml", "<p>RE<![CDATA[Q-004]]></p>"),
    ("search.json", '{"$comment":"REQ-004"}'),
    ("search.json", '{"message":"<!-- RE<span>Q-0</span>04"}'),
    ("search.json", '{"message":"<![if RE<span>Q-0</span>04]>Public<![endif]>"}'),
    ("search.json", '{"message":"<![CDATA[RE<span>Q-0</span>04"}'),
    ("search.json", '{"message":"<?RE<span>Q-0</span>04>"}'),
    ("search.json", '{"message":"Public","message":"REQ-004"}'),
    ("schema.json", json.dumps({"$schema": "https://json-schema.org/draft/2020-12/schema", "examples": [{"$comment": "REQ-004"}]})),
    ("schema.json", json.dumps({"$schema": "https://json-schema.org/draft/2020-12/schema", "properties": {"$comment": {"description": "REQ-004"}}})),
    ("schema.json", '{"$schema":"https://json-schema.org/draft/2020-12/schema","$schema":"unknown","$comment":"REQ-004"}'),
])
def test_stdlib_cli_refuses_private_data_without_echoing_content_or_paths(tmp_path, name, text):
    (tmp_path / name).write_text(text, encoding="utf-8")
    result = _run(tmp_path)
    assert (result.returncode, result.stdout, result.stderr) == (1, "", LEAK)
    assert str(tmp_path) not in result.stderr
    assert "REQ-004" not in result.stderr
    with pytest.raises(PrivateIdentifierError) as refused:
        check_private_identifiers(tmp_path)
    assert str(refused.value) == "Published text contains an internal planning identifier."


@pytest.mark.parametrize("name,encoded", [
    ("index.html", b"<![unsupported[>"),
    ("index.html", b"\xff"),
    ("search.json", b"{"),
    ("search.json", b'{"message":"<![unsupported[Public]]>"}'),
    ("search.json", b'{"message":"<![CDATA[<p unfinished"}'),
])
def test_stdlib_cli_refuses_unsafe_inputs_with_fixed_diagnostics(tmp_path, name, encoded):
    (tmp_path / name).write_bytes(encoded)
    result = _run(tmp_path)
    assert (result.returncode, result.stdout, result.stderr) == (1, "", INVALID)


def test_cli_refuses_a_missing_directory_without_echoing_it(tmp_path):
    result = _run(tmp_path / "input-path-canary")
    assert (result.returncode, result.stdout, result.stderr) == (1, "", INVALID)


@pytest.mark.parametrize("arguments", [(), ("argument-canary", "second-canary")])
def test_cli_usage_is_fixed_and_bounded(arguments):
    result = _run(*arguments)
    assert (result.returncode, result.stdout, result.stderr) == (
        2, "", "usage: python -m vouchfx_site_tools.check_private_identifiers SITE_DIR\n",
    )


@pytest.mark.parametrize("kind", ["missing", "file", "invalid"])
def test_callable_api_refuses_an_invalid_directory_with_fixed_diagnostics(tmp_path, kind):
    target = tmp_path / "input-path-canary"
    if kind == "file":
        target.write_text("Public.", encoding="utf-8")
    elif kind == "invalid":
        target = Path("nul-path-canary\x00")
    with pytest.raises(PrivateIdentifierError) as refused:
        check_private_identifiers(target)
    assert str(refused.value) == "Published text could not be read or parsed safely."
