"""Release notes extraction and the release-preparation check, on fixture files."""

import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "release_metadata.py"
ROOT = SCRIPT.parents[1]
SPEC = importlib.util.spec_from_file_location("release_metadata", SCRIPT)
metadata = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(metadata)

CHANGELOG = """# Changelog

Intro text.

## [Unreleased]

### Fixed

- Not released yet.

## [1.5.0] — 2026-12-01

### Added

- A feature.
- Another feature.

## [1.4.0] — 2026-11-21

- Older entry.
"""
PROPS = """<Project>
  <PropertyGroup>
    <VouchfxEngineVersion Condition="'$(Version)' != ''">$(Version)</VouchfxEngineVersion>
    <VouchfxEngineVersion Condition="'$(VouchfxEngineVersion)' == ''">{}</VouchfxEngineVersion>
  </PropertyGroup>
</Project>
"""


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.changelog = Path(self.temp.name) / "CHANGELOG.md"
        self.props = Path(self.temp.name) / "Directory.Build.props"
        self.write(CHANGELOG, "1.5.0")

    def write(self, changelog, fallback):
        self.changelog.write_text(changelog, encoding="utf-8")
        self.props.write_text(PROPS.format(fallback), encoding="utf-8")

    def cli(self, *argv):
        with contextlib.redirect_stdout(io.StringIO()) as stdout, \
                contextlib.redirect_stderr(io.StringIO()) as stderr:
            code = metadata.main(list(argv))
        return code, stdout.getvalue(), stderr.getvalue()

    def notes(self, version):
        return self.cli("notes", "--changelog", str(self.changelog), "--version", version)

    def check(self, version):
        return self.cli("check", "--changelog", str(self.changelog), "--props", str(self.props),
                        "--version", version)

    def test_notes_are_the_section_body_without_the_heading(self):
        code, stdout, stderr = self.notes("1.5.0")
        self.assertEqual(0, code, stderr)
        self.assertEqual("### Added\n\n- A feature.\n- Another feature.\n", stdout)

    def test_notes_of_the_last_section_run_to_the_end_of_the_file(self):
        self.assertEqual((0, "- Older entry.\n", ""), self.notes("1.4.0"))

    def test_notes_refuse_a_missing_empty_or_duplicated_section(self):
        cases = (("missing", CHANGELOG, "1.6.0"),
                 ("prefix only", CHANGELOG, "1.5"),
                 ("empty", CHANGELOG.replace("### Added\n\n- A feature.\n- Another feature.\n", ""), "1.5.0"),
                 ("duplicated", CHANGELOG + "\n## [1.5.0] — again\n\n- x\n", "1.5.0"))
        for label, text, version in cases:
            with self.subTest(case=label):
                self.write(text, "1.5.0")
                code, stdout, stderr = self.notes(version)
                self.assertEqual(1, code)
                self.assertEqual("", stdout)
                self.assertIn("Release metadata check failed", stderr)

    def test_notes_up_to_the_length_limit_are_printed_and_longer_ones_refused(self):
        limit = metadata.MAX_NOTES_CHARS
        self.assertEqual(100_000, limit)
        for label, length, expected in (("at the limit", limit, 0), ("one over", limit + 1, 1)):
            with self.subTest(case=label):
                # The printed notes are the body and a final newline.
                body = "x" * (length - 1)
                self.write(CHANGELOG.replace("### Added\n\n- A feature.\n- Another feature.\n", body + "\n"), "1.5.0")
                code, stdout, stderr = self.notes("1.5.0")
                self.assertEqual(expected, code, stderr)
                if expected == 0:
                    self.assertEqual(body + "\n", stdout)
                else:
                    self.assertEqual("", stdout)
                    self.assertIn(f"has {limit + 1} characters; the limit is {limit}", stderr)

    def test_notes_limit_counts_characters_not_bytes(self):
        body = "é" * (metadata.MAX_NOTES_CHARS - 1)
        self.write(CHANGELOG.replace("### Added\n\n- A feature.\n- Another feature.\n", body + "\n"), "1.5.0")
        self.assertEqual(0, self.notes("1.5.0")[0])

    def test_notes_do_not_read_the_unreleased_section_for_a_version(self):
        self.write(CHANGELOG.replace("## [1.5.0] — 2026-12-01", "## [1.5.0-rc.1] — 2026-12-01"), "1.5.0")
        self.assertEqual(1, self.notes("1.5.0")[0])

    def test_check_accepts_a_prepared_commit(self):
        code, stdout, stderr = self.check("1.5.0")
        self.assertEqual(0, code, stderr)
        self.assertIn("prepared for 1.5.0", stdout)

    def test_check_refuses_an_older_heading_or_fallback(self):
        for label, changelog, fallback, version, text in (
                ("heading not renamed", CHANGELOG, "1.6.0", "1.6.0", "newest CHANGELOG.md heading is 1.5.0"),
                ("fallback not set", CHANGELOG, "1.4.0", "1.5.0", "fallback is 1.4.0"),
                ("only Unreleased", "## [Unreleased]\n\n- x\n", "1.5.0", "1.5.0", "no versioned heading"),
                ("pre-release heading", CHANGELOG, "1.5.0", "1.5.0-rc.1", "newest CHANGELOG.md heading")):
            with self.subTest(case=label):
                self.write(changelog, fallback)
                code, _, stderr = self.check(version)
                self.assertEqual(1, code)
                self.assertIn(text, stderr)

    def test_check_refuses_a_missing_or_doubled_fallback(self):
        for props in ("<Project/>", PROPS.format("1.5.0") + PROPS.format("1.5.0")):
            with self.subTest(props=props[:20]):
                self.props.write_text(props, encoding="utf-8")
                code, _, stderr = self.check("1.5.0")
                self.assertEqual(1, code)
                self.assertIn("exactly one VouchfxEngineVersion fallback", stderr)

    def test_unreadable_file_is_an_error_not_a_crash(self):
        self.changelog.unlink()
        self.assertEqual(1, self.check("1.5.0")[0])
        self.assertEqual(1, self.notes("1.5.0")[0])

    def test_the_repository_files_have_the_shape_the_check_reads(self):
        # The real files parse: one fallback, and a versioned heading that equals it.
        changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
        self.assertEqual(metadata.newest_version(changelog), metadata.props_fallback(props))

    def test_usage_error_exit_is_two(self):
        process = subprocess.run([sys.executable, str(SCRIPT), "notes"], capture_output=True)
        self.assertEqual(2, process.returncode)


if __name__ == "__main__":
    unittest.main()
