"""Publication decisions exercise actual archives and mocked bounded HTTP reads."""

import contextlib
import importlib.util
from http.client import IncompleteRead
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "check_nuget_presence.py"
SPEC = importlib.util.spec_from_file_location("check_nuget_presence", SCRIPT)
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)
URL = gate.BASE_ADDRESS + "vouchfx.sdk/index.json"


class Response(io.BytesIO):
    status = 200

    def geturl(self):
        return URL


class PresenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.package = Path(self.temp.name) / "untrusted-filename.nupkg"

    def archive(self, xml=None, entries=None):
        if entries is None:
            xml = xml or (
                '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
                '<metadata><id>Vouchfx.Sdk</id><version>1.0.0</version></metadata></package>'
            )
            entries = [("Vouchfx.Sdk.nuspec", xml)]
        with zipfile.ZipFile(self.package, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, data in entries:
                archive.writestr(name, data)
        return self.package

    def cli(self, response=None, error=None):
        self.archive()
        kwargs = {"side_effect": error} if error else {"return_value": response}
        with patch.object(gate, "urlopen", **kwargs), \
                contextlib.redirect_stdout(io.StringIO()) as stdout, \
                contextlib.redirect_stderr(io.StringIO()) as stderr:
            code = gate.main([str(self.package)])
        return code, stdout.getvalue(), stderr.getvalue()

    def test_present_version_skips_upload(self):
        code, stdout, stderr = self.cli(Response(b'{"versions":["0.9.0","1.0.0"]}'))
        self.assertEqual(0, code)
        self.assertIn("already public: Vouchfx.Sdk 1.0.0", stdout)
        self.assertEqual("", stderr)

    def test_missing_version_allows_upload_with_distinct_exit(self):
        code, stdout, stderr = self.cli(Response(b'{"versions":["0.9.0"]}'))
        self.assertEqual(3, code)
        self.assertIn("missing: Vouchfx.Sdk 1.0.0", stdout)
        self.assertEqual("", stderr)

    def test_new_package_404_allows_upload(self):
        self.assertEqual(3, self.cli(error=HTTPError(URL, 404, "not found", {}, None))[0])

    def test_http_errors_are_not_absence(self):
        for status in (400, 401, 403, 429, 500, 503):
            with self.subTest(status=status):
                code, stdout, stderr = self.cli(error=HTTPError(URL, status, "rejected", {}, None))
                self.assertEqual(1, code)
                self.assertEqual("", stdout)
                self.assertIn("HTTP error", stderr)

    def test_404_from_another_url_is_not_absence(self):
        error = HTTPError("https://example.invalid/index.json", 404, "not found", {}, None)
        self.assertEqual(1, self.cli(error=error)[0])

    def test_timeout_is_not_absence(self):
        self.assertEqual(1, self.cli(error=TimeoutError("timed out"))[0])

    def test_connection_failure_is_not_absence(self):
        self.assertEqual(1, self.cli(error=URLError("connection failed"))[0])

    def test_read_failure_is_not_absence(self):
        class BrokenResponse(Response):
            def read(self, size=-1):
                raise OSError("read failed")
        self.assertEqual(1, self.cli(BrokenResponse())[0])

    def test_incomplete_http_read_is_not_absence(self):
        class BrokenResponse(Response):
            def read(self, size=-1):
                raise IncompleteRead(b"partial")
        self.assertEqual(1, self.cli(BrokenResponse())[0])

    def test_invalid_json_is_not_absence(self):
        for data in (b'not JSON', b'{"versions":', b'\xff'):
            with self.subTest(data=data):
                self.assertEqual(1, self.cli(Response(data))[0])

    def test_invalid_versions_metadata_is_not_absence(self):
        for data in ([], {}, {"versions": None}, {"versions": "1.0.0"},
                     {"versions": [1]}, {"versions": ["invalid"]},
                     {"versions": ["1.0.0", None]}, {"versions": ["1.0.0+build"]},
                     {"versions": ["1.0"]}, {"versions": ["1.0.0-RC"]}):
            with self.subTest(data=data):
                self.assertEqual(1, self.cli(Response(json.dumps(data).encode()))[0])

    def test_large_response_is_rejected_with_bounded_read(self):
        class LargeResponse(Response):
            def read(self, size=-1):
                self.requested = size
                return b" " * size
        response = LargeResponse()
        self.assertEqual(1, self.cli(response)[0])
        self.assertEqual(gate.MAX_INDEX_BYTES + 1, response.requested)

    def test_request_uses_actual_id_and_finite_timeout(self):
        with patch.object(gate, "urlopen", return_value=Response(b'{"versions":["1.0.0"]}')) as open_url:
            self.assertTrue(gate.is_public("Vouchfx.Sdk", "1.0.0"))
        request = open_url.call_args.args[0]
        self.assertEqual(URL, request.full_url)
        self.assertEqual("GET", request.get_method())
        self.assertEqual({"timeout": 15}, open_url.call_args.kwargs)
        open_url.assert_called_once()

    def test_unexpected_status_and_redirect_are_rejected(self):
        for status, url in ((204, URL), (200, "http://api.nuget.org/v3-flatcontainer/vouchfx.sdk/index.json")):
            with self.subTest(status=status, url=url):
                response = Response(b'{"versions":["1.0.0"]}')
                response.status = status
                response.geturl = lambda: url
                self.assertEqual(1, self.cli(response)[0])

    def test_identity_comes_from_manifest_not_filename(self):
        self.assertEqual(("Vouchfx.Sdk", "1.0.0"), gate.package_identity(self.archive()))

    def test_namespace_free_manifest_is_supported(self):
        xml = '<package><metadata><id>Vouchfx.Sdk</id><version>1.0.0</version></metadata></package>'
        self.assertEqual(("Vouchfx.Sdk", "1.0.0"), gate.package_identity(self.archive(xml)))

    def test_version_identity_uses_nuget_normalization(self):
        for source, expected in (("01.02.03", "1.2.3"), ("1.0", "1.0.0"),
                                 ("1.0.0.0", "1.0.0"), ("1.0.0.2", "1.0.0.2"),
                                 ("1.0.0-RC.1+Build", "1.0.0-rc.1")):
            with self.subTest(source=source):
                self.assertEqual(expected, gate.normalized_version(source))

    def test_invalid_package_identity_is_rejected_before_network(self):
        fields = ("<id>../../other</id><version>1.0.0</version>",
                  "<id>Vouchfx.Sdk</id><version>1.0.0\nother</version>",
                  "<id>Vouchfx.Sdk</id><version>latest</version>",
                  "<id>Vouchfx.Sdk</id>",
                  "<id>Vouchfx.Sdk</id><id>Other</id><version>1.0.0</version>",
                  "<id><nested>Vouchfx.Sdk</nested></id><version>1.0.0</version>")
        for field in fields:
            with self.subTest(field=field):
                self.archive("<package><metadata>" + field + "</metadata></package>")
                with patch.object(gate, "urlopen") as open_url, \
                        contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(1, gate.main([str(self.package)]))
                open_url.assert_not_called()

    def test_invalid_xml_or_structure_is_rejected(self):
        for xml in ("<package>", "<other/>", "<package/>",
                    "<package><metadata/><metadata/></package>"):
            with self.subTest(xml=xml):
                with self.assertRaises(gate.PresenceError):
                    gate.package_identity(self.archive(xml))

    def test_dtd_is_rejected_in_utf8_and_utf16(self):
        xml = ('<!DOCTYPE package [<!ENTITY id "Vouchfx.Sdk">]><package><metadata>'
               '<id>&id;</id><version>1.0.0</version></metadata></package>')
        for encoding in ("utf-8", "utf-16"):
            with self.subTest(encoding=encoding):
                with self.assertRaisesRegex(gate.PresenceError, "DTD"):
                    gate.package_identity(self.archive(xml.encode(encoding)))

    def test_manifest_size_is_bounded(self):
        with self.assertRaisesRegex(gate.PresenceError, "size limit"):
            gate.package_identity(self.archive(" " * (gate.MAX_NUSPEC_BYTES + 1)))

    def test_missing_nested_or_multiple_manifests_are_rejected(self):
        xml = "<package/>"
        for entries in ([], [("nested/a.nuspec", xml)],
                        [("a.nuspec", xml), ("b.nuspec", xml)]):
            with self.subTest(entries=entries):
                with self.assertRaisesRegex(gate.PresenceError, "one root nuspec"):
                    gate.package_identity(self.archive(entries=entries))

    def test_unreadable_package_has_error_exit(self):
        for contents in (None, b"not a ZIP"):
            with self.subTest(contents=contents):
                if contents is not None:
                    self.package.write_bytes(contents)
                with contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(1, gate.main([str(self.package)]))

    def test_usage_error_exit_is_not_missing(self):
        process = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True)
        self.assertEqual(2, process.returncode)
        self.assertNotEqual(gate.MISSING, process.returncode)


if __name__ == "__main__":
    unittest.main()
