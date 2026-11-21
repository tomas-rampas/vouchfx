"""Pins the security property of the Docker failure-diagnostics capture step.

The acceptance artefact is public and not secret-masked, so the step must never print a command line, an
environment, a health-check log or the whole of `.Config`. The checks are allowlists: each Docker invocation must be
one of a few read-only listings, and every Go template field it prints must be one this file names for that
invocation. Workflow source is parsed the way the neighbouring tests do (`docker_ci_support.workflow`, i.e. yq), not
with another parser.
"""

import os
import re
import shlex
import shutil
import subprocess
import tempfile
import unittest

from docker_ci_support import step, workflow

CAPTURE = "Capture Docker state for failure diagnosis"
VERIFY = "Verify owned topology acceptance counts and closure"
UPLOAD = "Upload first-attempt owned topology evidence"
RETENTION = "${{ job.status != 'success' && 30 || 7 }}"

TEMPLATE = re.compile(r"\{\{.*?\}\}", re.DOTALL)
MANAGEMENT = {"builder", "buildx", "compose", "config", "container", "context", "image", "manifest", "network", "node", "plugin",
              "secret", "service", "stack", "swarm", "system", "trust", "volume"}
LISTINGS = {"ps", "container ls", "container list", "container ps"}
CONTAINER_INSPECTS = {"inspect", "container inspect"}
EVENTS = {"events", "system events"}
ALLOWED_COMMANDS = LISTINGS | CONTAINER_INSPECTS | EVENTS | {"network ls", "network inspect", "volume ls"}
LISTING_FIELDS = {"{{.ID}}", "{{.Image}}", "{{.CreatedAt}}", "{{.Status}}", "{{.Ports}}", "{{.Names}}"}
CONTAINER_INSPECT_FIELDS = {"{{.Name}}", "{{.Id}}", "{{.State.Status}}", "{{.State.ExitCode}}", "{{.State.Error}}", "{{.State.StartedAt}}",
                            "{{.State.FinishedAt}}", "{{.State.OOMKilled}}", "{{if .State.Health}}", "{{.State.Health.Status}}", "{{end}}"}
NETWORK_INSPECT_FIELDS = {"{{.Name}}", "{{.Id}}", "{{.Created}}", "{{json .Containers}}"}
EVENT_FIELDS = {"{{.TimeNano}}", "{{.Type}}", "{{.Actor.ID}}", '{{index .Actor.Attributes "name"}}', "{{json .Action}}"}
EVENT_FILTER = "grep -E '^[0-9]{19} ' \"$events\" | sed -E 's/ \"(exec_[a-z]+):.*$/ \\1/'"


def commands(run, prefix):
    """Logical command lines (continuations joined) that contain `prefix`."""
    joined = re.sub(r"\\\n\s*", " ", run)
    return [line.strip() for line in joined.splitlines() if prefix in line and not line.lstrip().startswith("#")]


def docker_invocations(run):
    """(subcommand, arguments) for every `docker` word that starts a simple command or follows a wrapper such as `timeout`."""
    found = []
    for line in commands(run, ""):
        lexer = shlex.shlex(line, posix=True, punctuation_chars=True)
        lexer.whitespace_split = True
        lexer.commenters = ""
        simple, commands_on_line = [], []
        for token in lexer:
            if token and all(character in "();<>|&" for character in token):
                commands_on_line.append(simple)
                simple = []
            else:
                simple.append(token)
        commands_on_line.append(simple)
        for words in commands_on_line:
            if "docker" not in words:
                continue
            arguments = words[words.index("docker") + 1:]
            first = arguments[0] if arguments else ""
            if first in MANAGEMENT and len(arguments) > 1:
                found.append((first + " " + arguments[1], arguments[2:]))
            else:
                found.append((first, arguments[1:]))
    return found


def has_format(arguments):
    return any(argument in ("--format", "-f") or argument.startswith("--format=") for argument in arguments)


def is_quiet(arguments):
    return any(argument == "--quiet" or (re.fullmatch(r"-[a-zA-Z]+", argument) is not None and "q" in argument) for argument in arguments)


class FailureDiagnosticsCaptureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = workflow()
        cls.steps = cls.data["jobs"]["integration"]["steps"]
        cls.capture = step(CAPTURE, cls.data)
        cls.script = cls.capture["run"]
        cls.invocations = docker_invocations(cls.script)

    def test_runs_only_on_failure_after_the_verifier_and_before_the_upload(self):
        self.assertEqual("failure() && steps.topology-evidence-preparation.outcome == 'success'", self.capture["if"])
        verify, upload = step(VERIFY, self.data), step(UPLOAD, self.data)
        self.assertLess(self.steps.index(verify), self.steps.index(self.capture))
        self.assertLess(self.steps.index(self.capture), self.steps.index(upload))

    def test_the_upload_keeps_failed_or_cancelled_runs_for_thirty_days_and_passing_runs_for_seven(self):
        self.assertEqual(RETENTION, step(UPLOAD, self.data)["with"]["retention-days"])

    def test_only_read_only_docker_commands_run(self):
        self.assertGreaterEqual(len(self.invocations), 8, self.invocations)
        for name, arguments in self.invocations:
            self.assertIn(name, ALLOWED_COMMANDS, arguments)

    def test_every_template_field_is_allowed_for_its_command(self):
        seen = 0
        for name, arguments in self.invocations:
            templates = [template for argument in arguments for template in TEMPLATE.findall(argument)]
            seen += len(templates)
            allowed = (LISTING_FIELDS if name in LISTINGS else CONTAINER_INSPECT_FIELDS if name in CONTAINER_INSPECTS
                       else NETWORK_INSPECT_FIELDS if name == "network inspect" else EVENT_FIELDS if name in EVENTS else set())
            for template in templates:
                self.assertIn(template, allowed, (name, arguments))
        # A template held anywhere else outside a comment (a variable, a heredoc) would escape the per-command check above.
        self.assertEqual(len(TEMPLATE.findall("\n".join(commands(self.script, "")))), seen)

    def test_every_inspect_has_an_explicit_format(self):
        inspects = [(name, arguments) for name, arguments in self.invocations if name in CONTAINER_INSPECTS or name == "network inspect"]
        self.assertEqual(2, len(inspects), inspects)
        for name, arguments in inspects:
            self.assertTrue(has_format(arguments), (name, arguments))

    def test_container_listings_print_no_command_column(self):
        listings = [(name, arguments) for name, arguments in self.invocations if name in LISTINGS]
        self.assertGreaterEqual(len(listings), 2, listings)
        for name, arguments in listings:
            self.assertNotIn("--no-trunc", arguments, (name, arguments))
            self.assertTrue(has_format(arguments) or is_quiet(arguments), (name, arguments))
        self.assertNotRegex(self.script, r"\.Command\b")

    def test_each_event_is_one_line_and_exec_command_lines_are_stripped(self):
        events = [arguments for name, arguments in self.invocations if name in EVENTS]
        self.assertEqual(1, len(events), events)
        templates = [template for argument in events[0] for template in TEMPLATE.findall(argument)]
        self.assertEqual("{{json .Action}}", templates[-1])
        self.assertIn(EVENT_FILTER, self.script)

    @unittest.skipUnless(shutil.which("bash") and shutil.which("grep") and shutil.which("sed"), "bash, grep and sed are needed")
    def test_the_event_filter_drops_commands_and_continuation_lines(self):
        sample = "\n".join([
            '1763700000000000001 container 0123abcd web "exec_start: sh -c \\"x\\nPGPASSWORD=s3cret psql\\""',
            '1763700000000000002 container 0123abcd web "exec_start: sh -c "x',
            'PGPASSWORD=s3cret psql"',
            '1763700000000000003 container 0123abcd web "health_status: healthy"',
            '1763700000000000004 container 0123abcd web "destroy"',
            "Error response from daemon: s3cret",
        ]) + "\n"
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "events.txt")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(sample)
            result = subprocess.run(["bash", "-c", EVENT_FILTER], env={**os.environ, "events": path}, capture_output=True, text=True, check=True)
        self.assertEqual([
            "1763700000000000001 container 0123abcd web exec_start",
            "1763700000000000002 container 0123abcd web exec_start",
            '1763700000000000003 container 0123abcd web "health_status: healthy"',
            '1763700000000000004 container 0123abcd web "destroy"',
        ], result.stdout.splitlines())

    def test_values_reach_the_script_through_env_not_expressions(self):
        self.assertNotIn("${{", self.script)
        self.assertEqual({"VOUCHFX_TOPOLOGY_ACCEPTANCE_RESULTS"}, set(self.capture["env"]))


if __name__ == "__main__":
    unittest.main()
