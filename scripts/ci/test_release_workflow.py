"""Release event contract and actual version-step regressions; no publication."""

import os
from pathlib import Path
import subprocess
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


if __name__ == "__main__":
    unittest.main()
