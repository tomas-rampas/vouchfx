"""Pins the security property of the Docker failure-diagnostics capture step.

The acceptance artefact is public and not secret-masked, so the step must never print a command line, an
environment, a health-check log or the whole of `.Config`. Workflow source is parsed the way the neighbouring
tests do (`docker_ci_support.workflow`, i.e. yq), not with another parser.
"""

import re
import unittest

from docker_ci_support import step, workflow

CAPTURE = "Capture Docker state for failure diagnosis"
VERIFY = "Verify owned topology acceptance counts and closure"
UPLOAD = "Upload first-attempt owned topology evidence"
RETENTION = "${{ job.status == 'failure' && 30 || 7 }}"


def commands(run, prefix):
    """Logical command lines (continuations joined) that contain `prefix`."""
    joined = re.sub(r"\\\n\s*", " ", run)
    return [line.strip() for line in joined.splitlines() if prefix in line and not line.lstrip().startswith("#")]


class FailureDiagnosticsCaptureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = workflow()
        cls.steps = cls.data["jobs"]["integration"]["steps"]
        cls.capture = step(CAPTURE, cls.data)
        cls.script = cls.capture["run"]

    def test_runs_only_on_failure_after_the_verifier_and_before_the_upload(self):
        self.assertEqual("failure() && steps.topology-evidence-preparation.outcome == 'success'", self.capture["if"])
        verify, upload = step(VERIFY, self.data), step(UPLOAD, self.data)
        self.assertLess(self.steps.index(verify), self.steps.index(self.capture))
        self.assertLess(self.steps.index(self.capture), self.steps.index(upload))

    def test_the_upload_keeps_failed_runs_for_thirty_days_and_passing_runs_for_seven(self):
        self.assertEqual(RETENTION, step(UPLOAD, self.data)["with"]["retention-days"])

    def test_every_inspect_has_an_explicit_format_and_none_prints_config(self):
        inspects = commands(self.script, "inspect ")
        self.assertEqual(2, len(inspects), inspects)
        for line in inspects:
            self.assertIn("--format", line)
            self.assertNotIn(".Config", line)
        self.assertNotIn(".Config", self.script)
        self.assertNotIn("json .State", self.script)
        self.assertNotIn(".State.Health.Log", self.script)

    def test_docker_ps_prints_no_command_column(self):
        listings = commands(self.script, "docker ps")
        self.assertGreaterEqual(len(listings), 2)
        for line in listings:
            self.assertNotIn("--no-trunc", line)
        self.assertTrue(any("--format" in line for line in listings if "-q" not in line), listings)
        self.assertNotRegex(self.script, r"\.Command\b")

    def test_exec_command_lines_are_stripped_from_events(self):
        self.assertEqual(1, len(commands(self.script, "docker events")))
        self.assertRegex(self.script, r"\{\{\.Action\}\}'")
        self.assertIn("sed -E 's/ (exec_[a-z]+):.*$/ \\1/'", self.script)

    def test_values_reach_the_script_through_env_not_expressions(self):
        self.assertNotIn("${{", self.script)
        self.assertEqual({"VOUCHFX_TOPOLOGY_ACCEPTANCE_RESULTS"}, set(self.capture["env"]))


if __name__ == "__main__":
    unittest.main()
