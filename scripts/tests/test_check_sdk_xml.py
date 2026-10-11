"""Publication checks exercise real ZIP members, including hostile XML encodings."""

import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "check_sdk_xml.py"
SPEC = importlib.util.spec_from_file_location("check_sdk_xml", SCRIPT)
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)

VERSION = "0.0.0-test.508"
LIBRARY = gate.RELEASE_LIBRARIES[0]
LIB_XML = f"lib/net8.0/{LIBRARY}.xml"
SETTINGS = ('<DotNetCliTool Version="1"><Commands><Command Name="vouchfx" '
            'EntryPoint="vouchfx.dll" Runner="dotnet" /></Commands></DotNetCliTool>')


def documentation(prose="A typed contract.", library=LIBRARY):
    return (f'<doc><assembly><name>{library}</name></assembly><members>'
            f'<member name="T:Example.PeerReviewPolicy"><summary>{prose}</summary>'
            '</member></members></doc>').encode()


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.path = self.directory / "candidate.nupkg"

    def package(self, entries, path=None):
        target = path or self.path
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr("[Content_Types].xml", "<Types />")
                for name, content in entries:
                    archive.writestr(name, content)
        return target

    def library(self, prose="A typed contract."):
        return self.package([(LIB_XML, documentation(prose))])

    def refused(self, entries, message, library=LIBRARY):
        with self.assertRaisesRegex(gate.InvalidPublication, message):
            gate.check_package(self.package(entries), library)

    def test_clean_technical_prose_and_symbol_references(self):
        prose = ('Validate the TLS peer certificate. <see cref="T:Sdk.S08" /> '
                 '<c>PeerReviewPolicy</c> preserves the public symbol name. '
                 'A requirements map is not an internal task identifier.')
        gate.check_package(self.library(prose), LIBRARY)

    def test_published_patterns_are_refused_in_actual_zip(self):
        for prose in ("S08-F-03", "Sprint 8", "fix round 2", "peer-review MAJOR",
                      "REQ-014", "US-S08-1", "gatekeeper finding",
                      "docs/reviews/review.md", "plan/sprint.md"):
            with self.subTest(prose=prose):
                self.refused([(LIB_XML, documentation(prose))], "internal publication prose")

    def test_langword_display_values_are_checked_in_actual_zip(self):
        for value in ("REQ-004", "REQ&#45;004", "peer review", "S08"):
            with self.subTest(value=value):
                self.refused([(LIB_XML, documentation(f'<see langword="{value}"/>'))],
                             "internal publication prose")
        prose = ('<see langword="null"/> <see langword="true"/> <see langword="internal"/> '
                 '<see cref="T:Example.S08"/> <seealso cref="T:Example.S08"/> '
                 '<paramref name="S08"/> <typeparamref name="S08"/>')
        xml = documentation(prose).replace(b'T:Example.PeerReviewPolicy', b'T:Example.S08')
        xml = xml.replace(b'</member>', b'<param name="S08">Parameter.</param>'
                          b'<typeparam name="S08">Type parameter.</typeparam>'
                          b'<exception cref="T:Example.S08">Exception contract.</exception></member>')
        gate.check_package(self.package([(LIB_XML, xml)]), LIBRARY)

    def test_normal_command_rejects_langword_prose_and_accepts_keywords_and_symbols(self):
        for library in gate.RELEASE_LIBRARIES:
            self.package([(f"lib/net8.0/{library}.xml", documentation(library=library))],
                         self.directory / f"{library}.{VERSION}.nupkg")
        self.package([(gate.TOOL_SETTINGS, SETTINGS)], self.directory / f"vouchfx.{VERSION}.nupkg")
        target = self.directory / f"{LIBRARY}.{VERSION}.nupkg"
        controls = ('<see langword="null"/> <see langword="true"/> <see langword="internal"/> '
                    '<see cref="T:Example.S08"/> <paramref name="S08"/> '
                    '<typeparamref name="S08"/>')
        for prose, expected in (('<see langword="REQ-004"/>', 1),
                                ('<see langword="REQ&#45;004"/>', 1), (controls, 0)):
            with self.subTest(expected=expected, prose=prose):
                self.package([(LIB_XML, documentation(prose))], target)
                result = subprocess.run([sys.executable, str(SCRIPT), "--packages-dir",
                                         str(self.directory), "--version", VERSION],
                                        capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode, expected)
                if expected:
                    self.assertEqual(result.stdout, "")
                    self.assertEqual(result.stderr, "FAIL: XML contains internal publication prose\n")
                else:
                    self.assertEqual(result.stderr, "")
                    self.assertEqual(result.stdout,
                                     "OK: required library XML and CLI XML publication contracts passed\n")

    def test_entities_inline_elements_and_comments_cannot_hide_patterns(self):
        for prose in ('S&#48;8', '<c>S</c><em>08</em>', 'S<!--separator-->08',
                      'fix <c>round</c> 2', 'peer<!--separator--> review',
                      '<!--REQ-014-->', 'REQ<!--separator-->-004',
                      '<code>{"note":"REQ-014"}</code>',
                      '<note title="fix round 2">contract</note>'):
            with self.subTest(prose=prose):
                self.refused([(LIB_XML, documentation(prose))], "internal publication prose")

    def test_utf16_entity_and_inline_split(self):
        xml = documentation('S<em>&#48;</em>8').decode().encode("utf-16")
        self.refused([(LIB_XML, xml)], "internal publication prose")

    def test_xml_comments_and_processing_instructions_outside_root_are_not_exempt(self):
        for prefix, suffix in ((b"<!--REQ-014-->", b""), (b"", b"<!--REQ-014-->"),
                               (b"<?note fix round 2?>", b"")):
            self.refused([(LIB_XML, prefix + documentation() + suffix)], "internal publication prose")
        gate.check_package(self.package([(LIB_XML, b"<!--TLS peer certificate-->" + documentation())]), LIBRARY)

    def test_separate_members_and_block_paragraphs_do_not_form_identifiers(self):
        xml = (f'<doc><assembly><name>{LIBRARY}</name></assembly><members>'
               '<member name="T:One"><summary>S</summary></member>'
               '<member name="T:Two"><summary>08</summary>'
               '<remarks><para>REQ</para><para>-004</para>'
               '<code>S</code><code>08</code></remarks>'
               '</member></members></doc>').encode()
        gate.check_package(self.package([(LIB_XML, xml)]), LIBRARY)

    def test_missing_library_xml_and_wrong_assembly_are_refused(self):
        self.refused([], "required XML package member is missing")
        self.refused([(LIB_XML, documentation(library="Other"))], "library documentation contract")
        self.refused([(LIB_XML, f'<doc><assembly><name>{LIBRARY}</name></assembly><members /></doc>'.encode())],
                     "library documentation contract")

    def test_duplicate_and_case_alias_zip_members_are_refused(self):
        for alias in (LIB_XML, LIB_XML.lower()):
            self.refused([(LIB_XML, documentation()), (alias, documentation())], "duplicate package member")

    def test_malformed_and_dtd_xml_are_refused(self):
        for xml, message in ((b"<doc>", "malformed XML"),
                             (b'<!DOCTYPE doc [<!ENTITY x "S08">]><doc>&x;</doc>', "DTD"),
                             (b'<!DOCTYPE doc SYSTEM "file:///private"><doc/>', "DTD")):
            with self.subTest(xml=xml):
                self.refused([(LIB_XML, xml)], message)

    def test_xml_size_is_bounded_before_expansion(self):
        with patch.object(gate, "MAX_XML_BYTES", 128):
            self.refused([(LIB_XML, documentation("a" * 512))], "XML input exceeds")

    def test_zip_entry_count_and_expanded_sizes_are_bounded(self):
        with patch.object(gate, "MAX_ENTRIES", 1):
            self.refused([(LIB_XML, documentation())], "entry count exceeds")
        with patch.object(gate, "MAX_ENTRY_BYTES", 32):
            self.refused([(LIB_XML, documentation())], "expanded package exceeds")
        with patch.object(gate, "MAX_EXPANDED_BYTES", 32):
            self.refused([(LIB_XML, documentation())], "expanded package exceeds")
        with patch.object(gate, "MAX_ARCHIVE_BYTES", 32):
            self.refused([(LIB_XML, documentation())], "package exceeds")

    def test_xml_tree_depth_and_node_count_are_bounded(self):
        with patch.object(gate, "MAX_XML_DEPTH", 4):
            self.refused([(LIB_XML, documentation("<a><b><c>text</c></b></a>"))], "XML tree exceeds")
        with patch.object(gate, "MAX_XML_NODES", 4):
            self.refused([(LIB_XML, documentation())], "XML tree exceeds")
        with patch.object(gate, "MAX_XML_NODES", 7):
            self.refused([(LIB_XML, documentation('<!--a--><!--b--><!--c-->'))], "XML tree exceeds")

    def test_cli_keeps_only_the_exact_tool_settings_contract(self):
        gate.check_package(self.package([(gate.TOOL_SETTINGS, SETTINGS)]))
        self.refused([(gate.TOOL_SETTINGS, SETTINGS), ("tools/net8.0/any/vouchfx.xml", documentation())],
                     "CLI package contains unexpected XML", library=None)
        self.refused([(gate.TOOL_SETTINGS, SETTINGS.replace("vouchfx.dll", "other.dll"))],
                     "CLI tool-settings contract", library=None)
        self.refused([], "required XML package member is missing", library=None)

    def test_other_xml_members_are_not_exempt(self):
        self.refused([(LIB_XML, documentation()), ("content/schema.xml", b'<source>{"note":"REQ-014"}</source>')],
                     "internal publication prose")

    def test_expected_release_closure_and_optional_planning_are_required(self):
        for library in gate.RELEASE_LIBRARIES:
            self.package([(f"lib/net8.0/{library}.xml", documentation(library=library))],
                         self.directory / f"{library}.{VERSION}.nupkg")
        self.package([(gate.TOOL_SETTINGS, SETTINGS)], self.directory / f"vouchfx.{VERSION}.nupkg")
        gate.check_packages(self.directory, VERSION)
        with self.assertRaises(FileNotFoundError):
            gate.check_packages(self.directory, VERSION, include_planning=True)
        library = gate.PLANNING
        self.package([(f"lib/net8.0/{library}.xml", documentation(library=library))],
                     self.directory / f"{library}.{VERSION}.nupkg")
        gate.check_packages(self.directory, VERSION, include_planning=True)

    def test_cli_diagnostics_never_disclose_paths_or_offending_content(self):
        for content in (b"not a zip", documentation("unique-private-REQ-014"),
                        b'<doc private="secret-path">',
                        b'<?xml version="1.0" encoding="private-unsupported"?><doc />'):
            target = self.directory / f"{LIBRARY}.{VERSION}.nupkg"
            if content == b"not a zip":
                target.write_bytes(content)
            else:
                self.package([(LIB_XML, content)], target)
            errors = io.StringIO()
            with contextlib.redirect_stderr(errors):
                self.assertEqual(gate.main(["--packages-dir", str(self.directory), "--version", VERSION]), 1)
            self.assertNotIn(str(self.directory), errors.getvalue())
            self.assertNotIn("unique-private", errors.getvalue())
            self.assertNotIn("secret-path", errors.getvalue())
            self.assertNotIn("private-unsupported", errors.getvalue())
            self.assertNotIn("Traceback", errors.getvalue())
            self.assertTrue(errors.getvalue().startswith("FAIL: "))

    def test_corrupted_zip_payload_has_a_fixed_diagnostic(self):
        target = self.directory / f"{LIBRARY}.{VERSION}.nupkg"
        self.package([(LIB_XML, documentation())], target)
        with zipfile.ZipFile(target) as archive:
            entry = archive.getinfo(LIB_XML)
            offset = entry.header_offset
        payload = bytearray(target.read_bytes())
        name_size = int.from_bytes(payload[offset + 26:offset + 28], "little")
        extra_size = int.from_bytes(payload[offset + 28:offset + 30], "little")
        data_offset = offset + 30 + name_size + extra_size
        payload[data_offset] ^= 0xFF
        target.write_bytes(payload)
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            self.assertEqual(gate.main(["--packages-dir", str(self.directory), "--version", VERSION]), 1)
        self.assertEqual(errors.getvalue(), "FAIL: package is missing or unreadable\n")


if __name__ == "__main__":
    unittest.main()
