"""Accept/refuse table for the session-start hook's global.json gate.

The function under test is cut out of the real hook file at test time (never a copy)
and sourced alone through bash: nothing else in the hook runs. The gate exists because
the dotnet host honours `sdk.paths` in global.json, so a branch could otherwise point it
at a stub SDK; it accepts only a plain `sdk` object with version, rollForward and
allowPrerelease. A symlinked file is refused too; the symlink cases below can only
exercise the function, not the hook's scratch-directory handling around it.
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


def padded(length):
    body = b'{"sdk":{"version":"10.0.100"}}'
    return body + b" " * (length - len(body))


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

    def run_gate(self, path):
        script = "set -euo pipefail\n" + self.function + (
            'if global_json_ok "$1"; then printf "ACCEPTED\\n%s" "$GLOBAL_JSON_BYTES"; '
            'else printf "REFUSED\\n%s" "$GLOBAL_JSON_BYTES"; fi\n')
        done = subprocess.run([BASH, "-c", script, "hook-gate", str(path)], capture_output=True, check=False)
        self.assertEqual(done.returncode, 0, done.stderr.decode("utf-8", "replace"))
        verdict, _, staged = done.stdout.decode("utf-8", "replace").partition("\n")
        return verdict, staged

    def write(self, name, data):
        path = self.root / name
        path.write_bytes(data)
        return path

    def test_hook_function_is_extracted_from_the_real_file(self):
        self.assertIn("sdk", self.function)
        self.assertNotIn("dotnet\" --version", self.function)

    def test_accepts(self):
        for label, data in ACCEPT.items():
            with self.subTest(label):
                verdict, staged = self.run_gate(self.write("accept.json", data))
                self.assertEqual(verdict, "ACCEPTED")
                # The staged text is what was validated: the file's own content.
                self.assertEqual(staged.strip(), data.decode("utf-8").strip())

    def test_refuses(self):
        for label, data in REFUSE.items():
            with self.subTest(label):
                verdict, staged = self.run_gate(self.write("refuse.json", data))
                self.assertEqual(verdict, "REFUSED")
                self.assertEqual(staged, "{}")

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
