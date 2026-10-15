"""Inert installer policy checks; no native installer, registry, CLI or process execution."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("check_native_installer", Path(__file__).parents[1] / "check_native_installer.py")
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class NativeInstallerPolicyTests(unittest.TestCase):
    def test_only_fresh_github_hosted_context_is_admitted(self):
        checker.require_hosted({"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"})
        for environment in ({}, {"GITHUB_ACTIONS": "true"}, {"RUNNER_ENVIRONMENT": "github-hosted"},
                            {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "self-hosted"},
                            {"GITHUB_ACTIONS": "false", "RUNNER_ENVIRONMENT": "github-hosted"}):
            with self.subTest(environment=environment), self.assertRaises(checker.InvalidJourney):
                checker.require_hosted(environment)

    def test_cli_version_requires_exact_candidate_and_optional_build_suffix(self):
        for value in ("1.0.2", "1.0.2+abc123\n", "0.0.0-test+abc123"):
            checker.assert_cli_version(value, "0.0.0-test" if value.startswith("0.") else "1.0.2")
        for value in ("1.0.20", "1.0.1+abc123", "1.0.2-rc.1", "1.0.2+", "noise\n1.0.2+abc123", "1.0.2+abc;command"):
            with self.subTest(value=value), self.assertRaises(checker.InvalidJourney):
                checker.assert_cli_version(value, "1.0.2")

    def test_version_intake_rejects_shell_syntax_and_build_metadata(self):
        for version in ("1.0.2", "0.0.0-test", "1.0.2-rc.1"):
            checker.validate_version(version)
        for version in ("1.2", "v1.0.2", "1.0.2+build", "1.0.2;touch x", "1.0.2\n", "1.0.2-$(x)"):
            with self.subTest(version=version), self.assertRaises(checker.InvalidJourney):
                checker.validate_version(version)

    def test_receipt_excludes_shared_directories_from_removal(self):
        text = "usr\nusr/local\nusr/local/bin\nusr/local/lib\nusr/local/bin/vouchfx\nusr/local/lib/vouchfx\nusr/local/lib/vouchfx/vouchfx\n"
        self.assertEqual({checker.MAC_LINK, checker.MAC_ROOT, checker.MAC_ROOT / "vouchfx"}, checker.mac_receipt_paths(text))

    def test_receipt_refuses_escaping_foreign_missing_and_duplicate_paths(self):
        prefix = "usr/local/bin/vouchfx\nusr/local/lib/vouchfx\n"
        for entry in ("/usr/local/lib/vouchfx", "usr/local/lib/vouchfx/../foreign", "usr/local/lib/foreign", ".",
                      "usr/local/lib/vouchfx//dcp", "usr/local/lib/vouchfx", "usr/local/lib/vouchfx/./dcp"):
            with self.subTest(entry=entry), self.assertRaises(checker.InvalidJourney):
                checker.mac_receipt_paths(prefix + entry + "\n")
        for text in ("", "usr/local/bin/vouchfx", "usr/local/lib/vouchfx"):
            with self.subTest(text=text), self.assertRaises(checker.InvalidJourney):
                checker.mac_receipt_paths(text)

    def test_payload_requires_complete_identical_file_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first, second = root / "first", root / "second"
            first.mkdir()
            second.mkdir()
            for payload in (first, second):
                (payload / "vouchfx").write_bytes(b"inert candidate")
                (payload / "dcp").mkdir()
                (payload / "dcp/NOTICE").write_text("inert notice", encoding="utf8")
            self.assertEqual(checker.payload_manifest(first), checker.payload_manifest(second))
            (second / "dcp/NOTICE").write_text("changed notice", encoding="utf8")
            self.assertNotEqual(checker.payload_manifest(first), checker.payload_manifest(second))
            (second / "extra").write_bytes(b"extra")
            self.assertNotEqual(checker.payload_manifest(first), checker.payload_manifest(second))

    def test_preexisting_command_is_refused_before_mutation(self):
        with tempfile.TemporaryDirectory() as temporary:
            journey = checker.Journey(Path(temporary) / "evidence")
            with mock.patch.object(checker.shutil, "which", return_value="foreign/vouchfx"):
                with self.assertRaisesRegex(checker.InvalidJourney, "already exists"):
                    journey.assert_absent(Path(temporary) / "absent")
            self.assertEqual([], journey.commands)

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaises(checker.InvalidJourney):
            checker.Journey(Path(temporary))


    def test_failed_msi_version_validation_still_removes_only_candidate(self):
        with tempfile.TemporaryDirectory() as temporary:
            journey = checker.Journey(Path(temporary) / "evidence")
            calls = []
            def inert_run(arguments, **_kwargs):
                calls.append(arguments)
                return mock.Mock(returncode=0, stdout="")
            with mock.patch.dict(checker.os.environ, {"ProgramFiles": "C:/Program Files", "SystemRoot": "C:/Windows"}), \
                 mock.patch.object(journey, "assert_absent"), mock.patch.object(journey, "refresh_windows_path"), \
                 mock.patch.object(journey, "windows_paths", return_value=("C:/Windows", "")), \
                 mock.patch.object(journey, "windows_products", side_effect=[[], ["owned"], ["owned"]]), \
                 mock.patch.object(journey, "windows_registry_proof", side_effect=[None, 1]), \
                 mock.patch.object(journey, "assert_installed", side_effect=checker.InvalidJourney("wrong candidate")), \
                 mock.patch.object(journey, "run", side_effect=inert_run):
                with self.assertRaisesRegex(checker.InvalidJourney, "wrong candidate"):
                    journey.windows("candidate.msi", Path("inert-publish"), "1.0.2")
            self.assertEqual(["/i", "/x"], [arguments[1] for arguments in calls])
            self.assertTrue(all(arguments[2] == "candidate.msi" and "/qn" in arguments and "/norestart" in arguments
                                for arguments in calls))

    def test_ambiguous_msi_product_registration_refuses_removal(self):
        with tempfile.TemporaryDirectory() as temporary:
            journey = checker.Journey(Path(temporary) / "evidence")
            calls = []
            def inert_run(arguments, **_kwargs):
                calls.append(arguments)
                return mock.Mock(returncode=0, stdout="")
            with mock.patch.dict(checker.os.environ, {"ProgramFiles": "C:/Program Files", "SystemRoot": "C:/Windows"}), \
                 mock.patch.object(journey, "assert_absent"), mock.patch.object(journey, "refresh_windows_path"), \
                 mock.patch.object(journey, "windows_paths", return_value=("C:/Windows", "")), \
                 mock.patch.object(journey, "windows_products", side_effect=[[], ["owned", "foreign"], ["owned", "foreign"]]), \
                 mock.patch.object(journey, "windows_registry_proof", return_value=None), \
                 mock.patch.object(journey, "run", side_effect=inert_run):
                with self.assertRaisesRegex(checker.InvalidJourney, "ambiguous MSI"):
                    journey.windows("candidate.msi", Path("inert-publish"), "1.0.2")
            self.assertEqual(["/i"], [arguments[1] for arguments in calls])

    def test_mac_foreign_receipt_path_refuses_file_or_receipt_removal(self):
        with tempfile.TemporaryDirectory() as temporary:
            journey = checker.Journey(Path(temporary) / "evidence")
            calls = []
            def inert_run(arguments, **_kwargs):
                calls.append(arguments)
                if "--pkg-info-plist" in arguments:
                    import plistlib
                    text = plistlib.dumps({"pkg-version": "1.0.2", "volume": "/", "install-location": "/"}).decode("utf8")
                elif "--files" in arguments:
                    text = "usr/local/bin/vouchfx\nusr/local/lib/vouchfx\nusr/local/lib/foreign\n"
                else:
                    text = "foreign.receipt\n"
                return mock.Mock(returncode=0, stdout=text)
            with mock.patch.object(journey, "assert_absent"), \
                 mock.patch.object(journey, "assert_installed", side_effect=checker.InvalidJourney("wrong candidate")), \
                 mock.patch.object(journey, "run", side_effect=inert_run):
                with self.assertRaisesRegex(checker.InvalidJourney, "foreign path"):
                    journey.macos("candidate.pkg", Path("inert-publish"), "1.0.2")
            self.assertFalse(any("/bin/rm" in arguments or "/bin/rmdir" in arguments or "--forget" in arguments
                                 for arguments in calls))

    def test_inherited_only_command_refuses_before_path_refresh_or_native_calls(self):
        with tempfile.TemporaryDirectory() as temporary:
            journey = checker.Journey(Path(temporary) / "evidence")
            inherited = "inert-inherited-only"
            events = []
            def inert_which(_command):
                events.append("command-check")
                return "inert-foreign/vouchfx.exe" if checker.os.environ["PATH"] == inherited else None
            def inert_refresh():
                events.append("path-refresh")
                checker.os.environ["PATH"] = "inert-registry-only"
            with mock.patch.dict(checker.os.environ, {"PATH": inherited, "ProgramFiles": "inert-program-files",
                                                      "SystemRoot": "inert-system-root"}), \
                 mock.patch.object(checker.os.path, "lexists", return_value=False), \
                 mock.patch.object(checker.shutil, "which", side_effect=inert_which), \
                 mock.patch.object(journey, "windows_paths", return_value=("inert-registry-only", "")) as paths, \
                 mock.patch.object(journey, "refresh_windows_path", side_effect=inert_refresh) as refresh, \
                 mock.patch.object(journey, "windows_products", return_value=[]) as products, \
                 mock.patch.object(journey, "windows_registry_proof", return_value=None) as registry, \
                 mock.patch.object(journey, "run", side_effect=AssertionError("Native invocation reached")) as native:
                with self.assertRaisesRegex(checker.InvalidJourney, "already exists"):
                    journey.windows("inert-candidate.msi", Path("inert-publish"), "1.0.2")
                self.assertEqual(inherited, checker.os.environ["PATH"])
                self.assertEqual(["command-check"], events)
                paths.assert_not_called()
                refresh.assert_not_called()
                products.assert_not_called()
                registry.assert_not_called()
                native.assert_not_called()
            self.assertEqual([], journey.commands)


if __name__ == "__main__":
    unittest.main()
