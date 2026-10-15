"""Release event contract and actual version-step regressions; no publication."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from docker_ci_support import BASH, ROOT, workflow


class ReleaseWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = workflow(ROOT / ".github/workflows/release.yml")
        cls.jobs = cls.data["jobs"]

    def test_one_public_trigger_and_manual_preflight(self):
        self.assertEqual({"release", "workflow_dispatch"}, set(self.data["on"]))
        self.assertEqual(["published"], self.data["on"]["release"]["types"])
        version = self.data["on"]["workflow_dispatch"]["inputs"]["version"]
        self.assertTrue(version["required"])
        self.assertEqual("string", version["type"])
        self.assertIs(False, self.data["concurrency"]["cancel-in-progress"])

    def test_dispatch_cannot_publish_or_require_cosign(self):
        self.assertEqual("github.event_name != 'workflow_dispatch'",
                         self.jobs["publish-release"]["if"])
        self.assertEqual("github.event_name != 'workflow_dispatch' && "
                         "github.repository == 'tomas-rampas/vouchfx'",
                         self.jobs["publish-marketplace"]["if"])
        signers = [s for s in self.jobs["sign-and-attest"]["steps"]
                   if s.get("name") in ("Install cosign", "Sign artifacts (cosign keyless)")]
        self.assertEqual(2, len(signers))
        for step in signers:
            self.assertEqual("github.event_name != 'workflow_dispatch'", step["if"])

    def test_publication_still_requires_all_acceptance_jobs(self):
        self.assertEqual({"tls-transport", "build-dotnet", "sign-and-attest",
                          "smoke-test-packaged-tool", "smoke-test-distribution-platforms"},
                         set(self.jobs["publish-release"]["needs"]))
        self.assertIn("publish-release", self.jobs["publish-marketplace"]["needs"])
        commands = [s.get("run", "") for j in self.jobs.values() for s in j.get("steps", [])]
        self.assertFalse(any("gh release create" in command for command in commands))

    def test_actual_version_step_accepts_release_and_dispatch_only(self):
        step = next(s for s in self.jobs["build-dotnet"]["steps"]
                    if s.get("name") == "Determine release version")
        self.assertEqual({"EV_TAG": "${{ github.event.release.tag_name }}",
                          "EV_INPUT": "${{ inputs.version }}"}, step["env"])
        cases = (("release", "v1.2.3", "", "1.2.3", 0),
                 ("release", "v1.2.3-rc.1", "", "1.2.3-rc.1", 0),
                 ("workflow_dispatch", "", "0.0.0-test", "0.0.0-test", 0),
                 ("push", "v1.2.3", "", "", 1))
        for event, tag, version, expected_version, expected_exit in cases:
            with self.subTest(event=event, tag=tag), tempfile.TemporaryDirectory() as scratch:
                output = Path(scratch) / "github-output"
                env = os.environ.copy()
                env.update(GITHUB_EVENT_NAME=event, GITHUB_REF_NAME="v9.9.9",
                           EV_TAG=tag, EV_INPUT=version, GITHUB_OUTPUT=output.as_posix())
                result = subprocess.run([BASH, "--noprofile", "--norc", "-c", step["run"]],
                                        cwd=scratch, env=env, capture_output=True, timeout=10)
                self.assertEqual(expected_exit, result.returncode, result.stdout + result.stderr)
                if expected_exit:
                    self.assertFalse(output.exists())
                else:
                    self.assertEqual([f"tag=v{expected_version}", f"version={expected_version}"],
                                     output.read_text(encoding="utf-8").splitlines())


class ReleaseSbomTests(unittest.TestCase):
    def fixture(self, version="1.0.3"):
        return {"metadata": {"component": {"name": "vouchfx", "version": version,
                                           "bom-ref": f"vouchfx@{version}"}},
                "components": [{"name": "dependency", "version": "2.0.0",
                                "bom-ref": "dependency@2.0.0"}],
                "dependencies": [{"ref": f"vouchfx@{version}", "dependsOn": ["dependency@2.0.0"]},
                                 {"ref": "dependency@2.0.0", "dependsOn": []}]}

    def check(self, data, version="1.0.3", expected=0):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "release.bom.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            command = [sys.executable, "-I", "-B", str(ROOT / "scripts/check_release_sbom.py"), str(path)]
            if version is not None:
                command.extend(["--version", version])
            result = subprocess.run(command, capture_output=True, timeout=10)
            self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

    def test_generator_sets_version_and_validates_before_upload(self):
        steps = workflow(ROOT / ".github/workflows/release.yml")["jobs"]["build-dotnet"]["steps"]
        step = next(s for s in steps if s.get("name") == "Generate SBOM (CycloneDX JSON, spec 1.7)")
        self.assertEqual({"VERSION": "${{ steps.version.outputs.version }}"}, step["env"])
        self.assertIn('--set-version "$VERSION"', step["run"])
        self.assertIn('python scripts/check_release_sbom.py "artifacts/sbom/vouchfx-${VERSION}.bom.json" --version "$VERSION"', step["run"])
        self.assertLess(steps.index(step), next(i for i, s in enumerate(steps) if s.get("name") == "Upload SBOM"))

    def test_stable_and_prerelease_root_identity_and_references(self):
        for version in ("1.0.3", "1.0.3-rc.1"):
            with self.subTest(version=version):
                self.check(self.fixture(version), version)

    def test_default_version_or_stale_root_ref_refused(self):
        self.check(self.fixture("0.0.0"), expected=1)
        data = self.fixture()
        data["metadata"]["component"]["bom-ref"] = "vouchfx@0.0.0"
        self.check(data, expected=1)
        data = self.fixture()
        del data["metadata"]["component"]["version"]
        self.check(data, expected=1)

    def test_missing_root_and_dangling_dependency_references_refused(self):
        for field in ("ref", "dependsOn", "provides"):
            with self.subTest(field=field):
                data = self.fixture()
                data["dependencies"][0][field] = "vouchfx@0.0.0" if field == "ref" else ["missing@1.0.0"]
                self.check(data, expected=1)
        data = self.fixture()
        data["dependencies"] = []
        self.check(data, expected=1)

    def test_invalid_empty_and_missing_expected_version_refused(self):
        for version, expected in (("", 1), ("bad", 1), (None, 2)):
            with self.subTest(version=version):
                self.check(self.fixture(), version, expected)


if __name__ == "__main__":
    unittest.main()
