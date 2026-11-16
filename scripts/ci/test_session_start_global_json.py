"""Accept/refuse table for the session-start hook's global.json gate.

The function under test is cut out of the real hook file at test time (never a copy)
and sourced alone through bash: nothing else in the hook runs. The gate exists because
the dotnet host honours `sdk.paths` in global.json, so a branch could otherwise point it
at a stub SDK; it accepts only a plain `sdk` object with version, rollForward and
allowPrerelease. A symlinked file is refused too; the symlink cases below can only
exercise the function, not the hook's scratch-directory handling around it. Every case
runs under both the C and the C.UTF-8 locale, because the byte cap and the whitespace
set must not depend on the caller's locale. A refusal writes nothing on stdout: Claude
Code adds SessionStart stdout to the session context only when the hook exits 0, so the
hook reports a refusal on stderr and through its exit status instead.
"""

import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HOOK = ROOT / ".claude/hooks/session-start.sh"
BASH = os.environ.get("BASH", "bash")

REPO_GLOBAL_JSON = (ROOT / "global.json").read_bytes()
PRETTY = b'{\r\n  "sdk": {\r\n    "version": "10.0.100",\r\n    "rollForward": "latestFeature",\r\n    "allowPrerelease": false\r\n  }\r\n}\r\n'
PADDING = 4096
LOCALES = ("C", "C.UTF-8")
EVIL = b'{"sdk":{"paths":["/x"]}}'


def padded(length):
    body = b'{"sdk":{"version":"10.0.100"}}'
    return body + b" " * (length - len(body))


def multibyte_cap_file():
    """The first 4097 bytes are valid JSON, spaces and U+3000 (3 bytes each), 4097 bytes
    but only 1387 characters, followed by a second JSON object: a character count would
    let the file through as if it were under the cap."""
    head = b'{"sdk":{"version":"10.0.100"}}' + "\u3000".encode("utf-8") * 1355 + b"  "
    assert len(head) == 4097
    return head + EVIL


ACCEPT = {
    "the repository's own global.json": REPO_GLOBAL_JSON,
    "pretty-printed with CRLF": PRETTY,
    "empty object": b"{}",
    "empty sdk object": b'{"sdk":{}}',
    "allowPrerelease true": b'{"sdk":{"version":"10.0.100","allowPrerelease":true}}',
    "exactly 4 KiB": padded(PADDING),
}

REFUSE = {
    "sdk.paths": b'{"sdk":{"version":"10.0.100","paths":["/tmp/evil"]}}',
    "sdk.paths only": b'{"sdk":{"paths":["/tmp/evil"]}}',
    "top-level paths": b'{"paths":["/tmp/evil"],"sdk":{"version":"10.0.100"}}',
    "top-level errorMessage": b'{"errorMessage":"x"}',
    "sdk.errorMessage": b'{"sdk":{"errorMessage":"x"}}',
    "msbuild-sdks": b'{"msbuild-sdks":{"Some.Sdk":"1.0.0"},"sdk":{"version":"10.0.100"}}',
    "test runner": b'{"test":{"runner":"Microsoft.Testing.Platform"}}',
    "line comment hiding paths": b'{"sdk":{"version":"10.0.100"} // "paths": ["/tmp/evil"]\n}',
    "block comment": b'{"sdk":{"version":"10.0.100"} /* c */}',
    "trailing content": b'{"sdk":{"version":"10.0.100"}} {}',
    "trailing text": b"{}x",
    "duplicate sdk": b'{"sdk":{},"sdk":{"paths":["/tmp/evil"]}}',
    "SDK in capitals": b'{"SDK":{"version":"10.0.100"}}',
    "byte order mark": b"\xef\xbb\xbf" + REPO_GLOBAL_JSON,
    "over 4 KiB": padded(PADDING + 1),
    "4097 bytes that are fewer characters": multibyte_cap_file(),
    "4096 valid bytes, a newline, then a paths object": padded(PADDING) + b"\n" + EVIL,
    "5000 NUL bytes, then a paths object": b"\x00" * 5000 + EVIL,
    "NUL byte after a valid object": b'{"sdk":{"version":"10.0.100"}}\x00',
    "NUL byte before a valid object": b'\x00{"sdk":{"version":"10.0.100"}}',
    "vertical tab as whitespace": b'{"sdk":{"version":"10.0.100"}}\x0b',
    "form feed as whitespace": b'{\x0c"sdk":{"version":"10.0.100"}}',
    "ideographic space between tokens": b'{"sdk":{"version":"10.0.100"}\xe3\x80\x80}',
    "no-break space between tokens": b'{"sdk":{"version":"10.0.100"}\xc2\xa0}',
    "sdk as a string": b'{"sdk":"10.0.100"}',
    "sdk as an array": b'{"sdk":[]}',
    "unknown sdk key": b'{"sdk":{"version":"10.0.100","other":"x"}}',
    "escaped key": b'{"sdk":{"vers\\u0069on":"10.0.100"}}',
    "empty file": b"",
    "not JSON": b"sdk",
}


def function_source():
    """The global_json_ok function, cut from the real hook file."""
    text = HOOK.read_text(encoding="utf-8")
    match = re.search(r"^global_json_ok\(\) \{\n.*?^\}\n", text, re.MULTILINE | re.DOTALL)
    if match is None:
        raise AssertionError(f"global_json_ok not found in {HOOK}")
    return match.group(0)


class GlobalJsonGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.function = function_source()
        cls.scratch = tempfile.TemporaryDirectory(prefix="vouchfx-global-json-gate-")
        cls.root = Path(cls.scratch.name)

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def run_gate(self, path, locale="C", with_reason=False):
        # The function's own stdout and stderr go to a file, so the test can show it writes none.
        script = "set -euo pipefail\n" + self.function + (
            'if global_json_ok "$1" >"$2" 2>&1; then printf "ACCEPTED\\n%s" "$GLOBAL_JSON_BYTES"; '
            'else printf "REFUSED\\n%s" "$GLOBAL_JSON_BYTES"; fi\n'
            'printf "\\n%s" "${GLOBAL_JSON_REFUSAL-}" >"$3"\n')
        own = self.root / "gate-own-output"
        reason = self.root / "gate-reason"
        env = {**os.environ, "LC_ALL": locale}
        done = subprocess.run([BASH, "-c", script, "hook-gate", str(path), str(own), str(reason)],
                              capture_output=True, check=False, env=env)
        self.assertEqual(done.returncode, 0, done.stderr.decode("utf-8", "replace"))
        self.assertEqual(own.read_bytes(), b"", "the gate itself must print nothing")
        verdict, _, staged = done.stdout.decode("utf-8", "replace").partition("\n")
        if with_reason:
            return verdict, staged, reason.read_text(encoding="utf-8", errors="replace")[1:]
        return verdict, staged

    def write(self, name, data):
        path = self.root / name
        path.write_bytes(data)
        return path

    def test_hook_function_is_extracted_from_the_real_file(self):
        self.assertIn("sdk", self.function)
        self.assertNotIn("dotnet\" --version", self.function)

    def test_accepts(self):
        for locale in LOCALES:
            for label, data in ACCEPT.items():
                with self.subTest(label, locale=locale):
                    verdict, staged = self.run_gate(self.write("accept.json", data), locale)
                    self.assertEqual(verdict, "ACCEPTED")
                    # The staged text is what was validated: the file's own content.
                    self.assertEqual(staged.strip(), data.decode("utf-8").strip())

    def test_refuses(self):
        for locale in LOCALES:
            for label, data in REFUSE.items():
                with self.subTest(label, locale=locale):
                    verdict, staged, reason = self.run_gate(self.write("refuse.json", data), locale, with_reason=True)
                    self.assertEqual(verdict, "REFUSED")
                    self.assertEqual(staged, "{}")
                    # One reason per refusal, and never any of the file's content.
                    self.assertNotEqual(reason, "")
                    self.assertNotIn("\n", reason)
                    self.assertNotIn("/x", reason)
                    self.assertNotIn("evil", reason)

    def test_refusal_names_the_reason_for_the_size_and_nul_gates(self):
        _, _, size = self.run_gate(self.write("size.json", padded(PADDING + 1)), with_reason=True)
        _, _, nul = self.run_gate(self.write("nul.json", b'{}\x00'), with_reason=True)
        self.assertIn("4096 bytes", size)
        self.assertIn("NUL", nul)

    def test_the_hook_reports_a_refusal_on_stderr_only(self):
        text = HOOK.read_text(encoding="utf-8")
        match = re.search(r"^if ! global_json_ok .*?^fi\n", text, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match, "the refusal branch was not found")
        branch = match.group(0)
        self.assertIn("log \"ERROR:", branch)
        self.assertIn("exit 1", branch)
        self.assertNotRegex(branch, r"\b(echo|printf|cat)\b")
        self.assertNotIn("in the session context", text)

    def test_missing_file_is_accepted_and_stages_an_empty_object(self):
        verdict, staged = self.run_gate(self.root / "missing" / "global.json")
        self.assertEqual((verdict, staged), ("ACCEPTED", "{}"))

    def test_directory_is_refused(self):
        directory = self.root / "a-directory"
        directory.mkdir()
        self.assertEqual(self.run_gate(directory)[0], "REFUSED")

    def test_symlink_is_refused_even_to_an_acceptable_file(self):
        target = self.write("target.json", REPO_GLOBAL_JSON)
        link = self.root / "link.json"
        link.symlink_to(target)
        self.assertEqual(self.run_gate(target)[0], "ACCEPTED")
        self.assertEqual(self.run_gate(link)[0], "REFUSED")

    def test_dangling_symlink_is_refused(self):
        link = self.root / "dangling.json"
        link.symlink_to(self.root / "nowhere.json")
        self.assertEqual(self.run_gate(link)[0], "REFUSED")


if __name__ == "__main__":
    unittest.main()
