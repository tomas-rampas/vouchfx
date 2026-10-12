"""Actual-YAML pull regressions. Python 3.12, Bash, yq 4.53.2, ShellCheck 0.11.0.

No Docker or network access: only the image-list boundary is substituted. All
fixtures, extracted bytes, stdout/stderr and integer exits remain under TEMP.
"""

import base64
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest

from docker_ci_support import BASH, PULL_NAME, ROOT, TEST_NAME, WORKFLOW, extract_pull, fixture_images, image_boundary, sha, step, workflow


SCRATCH = Path(tempfile.mkdtemp(prefix="vouchfx-docker-ci-contract-"))
STUB = Path(__file__).with_name("pull_stub.py")


def reply(code=0, stdout=b"pulled", stderr=b""):
    return {"exit": code, "stdout": base64.b64encode(stdout).decode(),
            "stderr": base64.b64encode(stderr).decode()}


def interpreted_commands(output):
    """Runner-shaped CR/LF framing and stop/resume state, not escape recreation."""
    stopped = None
    tokens = []
    commands = []
    for line in re.split(rb"[\r\n]", output):
        if stopped:
            if line == b"::" + stopped + b"::":
                stopped = None
            continue
        if line.startswith(b"::stop-commands::"):
            stopped = line.split(b"::stop-commands::", 1)[1]
            if not re.fullmatch(rb"[0-9a-f]{32}", stopped):
                raise AssertionError(f"Unsafe stop token: {stopped!r}")
            tokens.append(stopped)
        elif line.startswith(b"::"):
            commands.append(line)
    if stopped:
        raise AssertionError("A completed pull left workflow commands stopped")
    if len(set(tokens)) != len(tokens):
        raise AssertionError("Log-fencing token reused across images")
    return commands, tokens


class DockerCiContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not BASH or not Path(BASH).is_file():
            raise AssertionError("Bash is mandatory; Windows uses Git Bash, never WSL")
        cls.script = extract_pull()
        cls.workflow_bytes = WORKFLOW.read_bytes()
        (SCRATCH / "actual-pull.sh").write_bytes(cls.script)
        (SCRATCH / "extraction.json").write_text(json.dumps({
            "workflow_sha256": sha(cls.workflow_bytes), "script_sha256": sha(cls.script),
            "script_bytes": len(cls.script), "parser_equals_source": True,
        }, indent=2))
        print(f"Retained pull fixtures and integer exits: {SCRATCH}", flush=True)

    def run_case(self, name, scenario, expected=0, tally=None, fail_token=False, script=None):
        root = Path(tempfile.mkdtemp(prefix=name + "-", dir=SCRATCH))
        binary = root / "bin"
        binary.mkdir()
        (root / "scenario.json").write_text(json.dumps(scenario), encoding="utf-8")
        actual = self.script if script is None else script
        fixture = fixture_images(actual, list(scenario))
        target = root / "pull.sh"
        target.write_bytes(fixture)
        for command in ["docker", "sleep", "timeout"] + (["head"] if fail_token else []):
            stub = binary / command
            stub.write_text('#!/usr/bin/env bash\nexec "$VOUCHFX_CI_PYTHON" "$VOUCHFX_CI_STUB" ' + command + ' "$@"\n', encoding="utf-8")
            stub.chmod(0o755)
        env = os.environ.copy()
        env.update(PATH=str(binary) + os.pathsep + env["PATH"],
                   VOUCHFX_CI_CASE=str(root), VOUCHFX_CI_PYTHON=Path(sys.executable).as_posix(),
                   VOUCHFX_CI_STUB=STUB.as_posix(), VOUCHFX_CI_BASH=Path(BASH).as_posix(),
                   VOUCHFX_CI_BIN=binary.as_posix(), VOUCHFX_CI_WINDOWS="1" if os.name == "nt" else "0")
        # Git Bash prepends /usr/bin on startup. Set PATH INSIDE that shell and
        # verify every fixture executable before reading the unmodified script.
        launcher = '''set -euo pipefail
fixture_bin="$VOUCHFX_CI_BIN"
if [ "$VOUCHFX_CI_WINDOWS" = 1 ]; then fixture_bin=$(cygpath -u "$fixture_bin"); fi
export PATH="$fixture_bin:$PATH"
for command in docker timeout sleep; do
  [ "$(command -v "$command")" = "$fixture_bin/$command" ] || exit 91
done
source "$1"
'''
        process = subprocess.Popen([BASH, "--noprofile", "--norc", "-c", launcher, "pull-fixture", str(target)],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env,
                                   start_new_session=os.name != "nt")
        timed_out = False
        try:
            stdout, stderr = process.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            timed_out = True
            # Kill only this owned fixture's tree: Git Bash's wrapper can leave a
            # child holding the pipes open when just the wrapper is terminated.
            if os.name == "nt":
                cleanup = subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                                         capture_output=True, timeout=10)
                (root / "timeout-cleanup.exit").write_text(str(cleanup.returncode))
                (root / "timeout-cleanup.stdout").write_bytes(cleanup.stdout)
                (root / "timeout-cleanup.stderr").write_bytes(cleanup.stderr)
            else:
                os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate(timeout=5)
        result = subprocess.CompletedProcess(process.args, process.returncode, stdout, stderr)
        (root / "stdout").write_bytes(stdout)
        (root / "stderr").write_bytes(stderr)
        (root / "script.exit").write_text(str(process.returncode))
        (root / "result.json").write_text(json.dumps({
            "expected_exit": expected, "actual_exit": result.returncode,
            "source_script_sha256": sha(actual), "fixture_script_sha256": sha(fixture),
            "image_boundary_only": True, "timed_out": timed_out,
        }, indent=2))
        self.assertFalse(timed_out, f"Fixture exceeded its 30-second deadline: {root}")
        self.assertEqual(expected, result.returncode,
                         f"script exit: expected {expected}, got {result.returncode}; {root}; {result.stderr!r}")
        calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()] if (root / "calls.jsonl").exists() else []
        commands, tokens = interpreted_commands(result.stdout)
        if not fail_token:
            self.assertEqual(len(scenario), len(tokens))
            self.assertEqual(len(scenario), sum(c.startswith(b"::group::") for c in commands))
            self.assertEqual(len(scenario), commands.count(b"::endgroup::"))
            self.assertEqual(["120", "docker", "pull"], calls[0]["args"][:3])
            self.assertTrue(all(c["command"] in ["docker", "timeout", "sleep"] for c in calls))
        if tally is not None:
            self.assertIn(f"pre-pull complete: examined {len(scenario)} pinned images; {tally[0]} failed, {tally[1]} rate-limited and skipped.".encode(), result.stdout)
        return root, result, calls, commands

    def test_source_extraction_and_bash_shellcheck(self):
        images = shlex.split(image_boundary(self.script).group().decode().replace("for image in", "", 1).rsplit("; do", 1)[0].replace("\\\n", ""))
        self.assertEqual(19, len(images))
        self.assertEqual(19, len(set(images)))
        for command, args in [(BASH, ["-n"]), (shutil.which("shellcheck"), ["--shell=bash"])]:
            self.assertTrue(command, "ShellCheck 0.11.0 is mandatory")
            if args == ["--shell=bash"]:
                version = subprocess.run([command, "--version"], capture_output=True, timeout=10)
                self.assertEqual(0, version.returncode)
                self.assertIn(b"version: 0.11.0", version.stdout)
            result = subprocess.run([command, *args, str(SCRATCH / "actual-pull.sh")], capture_output=True, timeout=30)
            stem = "bash-n" if args == ["-n"] else "shellcheck"
            (SCRATCH / (stem + ".stdout")).write_bytes(result.stdout)
            (SCRATCH / (stem + ".stderr")).write_bytes(result.stderr)
            (SCRATCH / (stem + ".exit")).write_text(str(result.returncode))
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_blocking_lane_preserves_execution_and_default_capture_root(self):
        data = workflow()
        integration = data["jobs"]["integration"]
        self.assertFalse(integration.get("continue-on-error", False))
        command = shlex.split(step(TEST_NAME, data)["run"])
        for value in ["-m:1", "--no-build", "requires=docker", "--blame-crash", "mini", "--collect:XPlat Code Coverage"]:
            self.assertIn(value, command)
        self.assertEqual("TestResults", command[command.index("--results-directory") + 1])
        self.assertNotIn("VOUCHFX_DCP_CAPTURE_DIR", json.dumps(integration))
        self.assertNotIn("secrets.", json.dumps(integration))
        self.assertEqual(25, step(PULL_NAME, data)["timeout-minutes"])
        gate = data["jobs"]["workflow-contract"]
        self.assertNotIn("if", gate)
        self.assertNotIn("continue-on-error", gate)
        for current in gate["steps"]:
            self.assertNotIn("if", current)
            self.assertNotIn("continue-on-error", current)
        self.assertIn("python -B scripts/ci/lint_workflows.py", [s.get("run") for s in gate["steps"]])
        self.assertIn("python -B -m unittest discover -s scripts/ci -p test_docker_ci_contract.py -v",
                      [s.get("run") for s in gate["steps"]])
        self.assertNotIn("paths", data["on"]["pull_request"])

    def test_success_visits_every_image(self):
        _, _, calls, _ = self.run_case("success", {"one": [reply()], "quay.io/two": [reply()]}, tally=(0, 0))
        self.assertEqual(2, sum(c["command"] == "docker" for c in calls))
        self.assertFalse(any(c["command"] == "sleep" for c in calls))

    def test_recovering_unknown_or_absent_failure_is_never_classified(self):
        for label, output in [("unknown", b"connection reset"), ("absent", b"manifest unknown"), ("rate", b"TOOMANYREQUESTS")]:
            with self.subTest(label=label):
                _, _, calls, commands = self.run_case("retry-" + label, {"alpha": [reply(1, output), reply()]}, tally=(0, 0))
                self.assertEqual(2, sum(c["command"] == "docker" for c in calls))
                self.assertEqual([["15"]], [c["args"] for c in calls if c["command"] == "sleep"])
                self.assertFalse(any(c.startswith((b"::error::", b"::warning::")) for c in commands))

    def test_absent_auth_manifest_and_connection_classification(self):
        rows = [
            ("access", b"pull access denied for alpha", b"Fix the pin"),
            ("repository", b"repository does not exist", b"Fix the pin"),
            ("manifest", b"MANIFEST UNKNOWN", b"Fix the pin"),
            ("tag", b"manifest for alpha not found", b"Fix the pin"),
            ("containerd", b'failed to resolve reference "alpha": not found', b"Fix the pin"),
            ("auth", b"unauthorized: access to the requested resource is not authorized", b"Fix the pin"),
            ("401", b"401 Unauthorized", b"Fix the pin"),
            ("requested", b"requested access to the resource is denied", b"Fix the pin"),
            ("connection", b'failed to resolve reference "alpha": dial tcp: i/o timeout', b"matches neither"),
            ("daemon", b"Got permission denied while trying to connect to the Docker daemon socket", b"matches neither"),
            ("unknown", b"unrecognised client failure", b"matches neither"),
        ]
        for label, output, diagnostic in rows:
            with self.subTest(label=label):
                _, _, calls, commands = self.run_case("bucket-" + label, {"alpha": [reply(1, stderr=output)]}, expected=1, tally=(1, 0))
                self.assertEqual(3, sum(c["command"] == "docker" for c in calls))
                self.assertEqual([["15"], ["30"]], [c["args"] for c in calls if c["command"] == "sleep"])
                errors = [c for c in commands if c.startswith(b"::error::")]
                self.assertEqual(1, len(errors))
                self.assertIn(diagnostic, errors[0])

    def test_timeout_status_overrides_absent_and_rate_text(self):
        for output in [b"pull access denied", b"TOOMANYREQUESTS", b""]:
            with self.subTest(output=output):
                _, _, calls, commands = self.run_case("stall", {"alpha": [reply(124, output)]}, expected=1, tally=(1, 0))
                self.assertEqual(3, sum(c["command"] == "timeout" for c in calls))
                error = next(c for c in commands if c.startswith(b"::error::"))
                self.assertIn(b"timed out after 3 attempts of 120s", error)
                self.assertIn(b"NOT A BAD PIN", error)

    def test_exhausted_rate_limit_spellings_remain_nonfatal(self):
        for output in [b"TOOMANYREQUESTS", b"rate limit exceeded", b"Too Many Requests"]:
            with self.subTest(output=output):
                _, _, calls, commands = self.run_case("rate", {"alpha": [reply(1, output)]}, tally=(0, 1))
                self.assertEqual(3, sum(c["command"] == "docker" for c in calls))
                self.assertEqual(1, sum(c.startswith(b"::warning::") for c in commands))

    def test_registry_budget_is_a_set_and_never_reduces_attempts(self):
        scenario = {image: [reply(1, b"rate limit")] for image in
                    ["alpha", "quay.io/a", "bravo", "quay.io/b", "myquay.io/c", "localhost/d", "host:5000/e"]}
        _, _, calls, _ = self.run_case("registries", scenario, tally=(0, 7))
        self.assertEqual(21, sum(c["command"] == "docker" for c in calls))
        self.assertEqual(10, sum(c["command"] == "sleep" for c in calls))  # 5 distinct registries, 2 sleeps each.

    def test_rate_capped_later_unknown_retries_and_can_recover(self):
        _, _, calls, commands = self.run_case("capped-recovery", {
            "alpha": [reply(1, b"rate limit")], "bravo": [reply(1, b"TLS handshake timeout"), reply()],
        }, tally=(0, 1))
        self.assertEqual(5, sum(c["command"] == "docker" for c in calls))
        self.assertEqual(2, sum(c["command"] == "sleep" for c in calls))
        self.assertFalse(any(c.startswith(b"::error::") for c in commands))

    def test_rate_capped_later_stall_keeps_three_attempts(self):
        _, _, calls, _ = self.run_case("capped-stall", {
            "alpha": [reply(1, b"rate limit")], "bravo": [reply(124)],
        }, expected=1, tally=(1, 1))
        self.assertEqual(6, sum(c["command"] == "docker" for c in calls))
        self.assertEqual(2, sum(c["command"] == "sleep" for c in calls))

    def test_later_image_is_attempted_after_fatal_and_tally_is_complete(self):
        _, _, calls, _ = self.run_case("later", {"bad": [reply(1, b"manifest unknown")], "good": [reply()]}, expected=1, tally=(1, 0))
        self.assertEqual(["bad", "bad", "bad", "good"], [c["args"][1] for c in calls if c["command"] == "docker"])

    def test_annotations_escape_controls_and_percent_while_raw_output_is_fenced(self):
        raw = b"oops\r::error::INJECTED\n::set-output name=x::EVIL\r\n%0A\t\x01\x1b\x1f tail"
        _, result, _, commands = self.run_case("annotation", {"alpha": [reply(1, raw)]}, expected=1, tally=(1, 0))
        self.assertIn(raw, result.stdout)
        errors = [c for c in commands if c.startswith(b"::error::")]
        self.assertEqual(1, len(errors))
        self.assertIn(b"oops ::error::INJECTED ::set-output name=x::EVIL  %250A     tail", errors[0])
        self.assertFalse(any(c.startswith(b"::set-output") or c == b"::error::INJECTED" for c in commands))

    def test_successful_malicious_output_and_image_name_do_not_inject_commands(self):
        image = "bad%0A\r\n::error::IMAGE & literal"
        raw = b"::warning::OUTPUT\r::set-output name=x::EVIL"
        _, _, calls, commands = self.run_case("names", {image: [reply(0, raw)]}, tally=(0, 0))
        self.assertEqual(image, next(c["args"][1] for c in calls if c["command"] == "docker"))
        self.assertIn(b"::group::docker pull bad%250A  ::error::IMAGE & literal", commands)
        self.assertFalse(any(c.startswith((b"::error::", b"::warning::", b"::set-output")) for c in commands))

    def test_failed_malformed_image_annotations_remain_safe(self):
        image = "bad%0D\r\n::error::IMAGE\t\x1b & literal"
        _, _, calls, commands = self.run_case("failed-name", {image: [reply(1, b"manifest unknown")]}, expected=1, tally=(1, 0))
        self.assertEqual([image] * 3, [c["args"][1] for c in calls if c["command"] == "docker"])
        errors = [c for c in commands if c.startswith(b"::error::")]
        self.assertEqual(1, len(errors))
        self.assertIn(b"pinned image bad%250D  ::error::IMAGE   & literal", errors[0])

    def test_random_token_failure_refuses_before_any_pull_without_false_tally(self):
        _, result, calls, commands = self.run_case("token-refusal", {"alpha": [reply()]}, expected=1, fail_token=True)
        self.assertEqual(["head"], [c["command"] for c in calls])
        self.assertIn(b"could not generate a random log-fencing token", commands[0])
        self.assertNotIn(b"pre-pull complete", result.stdout)

    def test_source_representation_drift_is_detected(self):
        mutated = SCRATCH / "folded-workflow.yml"
        source = WORKFLOW.read_bytes()
        anchor = source.index(f"      - name: {PULL_NAME}\n".encode())
        position = source.index(b"        run: |\n", anchor)
        mutated.write_bytes(source[:position] + source[position:].replace(b"        run: |\n", b"        run: >\n", 1))
        with self.assertRaisesRegex(AssertionError, "literal run"):
            extract_pull(mutated)
        (SCRATCH / "extraction-mutation-desired-assertion.exit").write_text("1")

    def test_behavioral_mutation_of_fatal_exit_is_detected(self):
        mutant = self.script.replace(b'[ "$fatal_count" -eq 0 ] || exit 1', b'[ "$fatal_count" -eq 0 ] || exit 0')
        self.assertNotEqual(self.script, mutant)
        with self.assertRaisesRegex(AssertionError, "script exit: expected 1, got 0"):
            self.run_case("fatal-exit-mutation", {"alpha": [reply(1, b"manifest unknown")]}, expected=1, script=mutant)
        (SCRATCH / "behavior-mutation-desired-assertion.exit").write_text("1")

    @classmethod
    def tearDownClass(cls):
        if WORKFLOW.read_bytes() != cls.workflow_bytes:
            raise AssertionError("Harness mutated its workflow source")


class WorkflowLintExtensionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        print(f"Retained workflow extension fixtures: {SCRATCH}", flush=True)

    def run_lint_fixture(self, invalid_yaml):
        root = Path(tempfile.mkdtemp(prefix="workflow-extensions-", dir=SCRATCH))
        scripts = root / "scripts/ci"
        scripts.mkdir(parents=True)
        for name in ["lint_workflows.py", "docker_ci_support.py", "install-workflow-tools.sh"]:
            shutil.copyfile(ROOT / "scripts/ci" / name, scripts / name)
        workflows = root / ".github/workflows"
        workflows.mkdir(parents=True)
        # Create in reverse name order; execute the actual wrapper and native
        # tools against both GitHub-supported suffixes, without mocked discovery.
        source = """name: Extension fixture
on: push
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - run: echo extension fixture
"""
        (workflows / "z.yml").write_text(source, encoding="utf-8", newline="\n")
        yaml_source = source.replace("    runs-on:", "    unexpected-field: true\n    runs-on:") if invalid_yaml else source
        (workflows / "a.yaml").write_text(yaml_source, encoding="utf-8", newline="\n")
        command = [sys.executable, "-B", str(scripts / "lint_workflows.py")]
        result = subprocess.run(command, cwd=root, capture_output=True, timeout=30)
        (root / "wrapper.stdout").write_bytes(result.stdout)
        (root / "wrapper.stderr").write_bytes(result.stderr)
        (root / "wrapper.exit").write_text(str(result.returncode))
        (root / "command.json").write_text(json.dumps(command, indent=2))
        match = re.search(rb"integer exits: ([^\r\n]+)", result.stdout)
        self.assertIsNotNone(match, result.stdout + result.stderr)
        report_root = Path(match[1].decode())
        (root / "report-location.txt").write_text(str(report_root))
        report = json.loads((report_root / "report.json").read_text())
        return result, report

    def test_valid_yaml_workflow_is_checked_by_actual_tools(self):
        result, report = self.run_lint_fixture(False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(2, report["workflows"])
        self.assertEqual(2, report["inline_scripts"])
        self.assertTrue(all(item["exit"] == 0 for item in report["commands"]))
        self.assertEqual([".github/workflows/a.yaml", ".github/workflows/z.yml"],
                         [item["workflow"] for item in report["sources"]])

    def test_invalid_yaml_workflow_fails_actual_wrapper_and_actionlint(self):
        result, report = self.run_lint_fixture(True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        actionlint = next(item for item in report["commands"] if item["label"] == "actionlint")
        self.assertEqual(1, actionlint["exit"])
        self.assertEqual(2, report["workflows"])
        self.assertIn(b"unexpected-field", result.stdout)


class ActionlintVersionTests(unittest.TestCase):
    class VersionAccepted(Exception):
        pass

    def check_version_gate(self, stdout, code=0, accepted=False):
        import lint_workflows
        from unittest import mock

        # Exercise the actual wrapper's version gate, stopping at the next
        # tool so this format regression has no external-tool dependency.
        with tempfile.TemporaryDirectory(dir=SCRATCH) as directory:
            version = subprocess.CompletedProcess(["actionlint", "-version"], code, stdout, b"")
            with mock.patch.object(lint_workflows.tempfile, "mkdtemp", return_value=directory), \
                    mock.patch.object(lint_workflows.shutil, "which", side_effect=["actionlint", "shellcheck"]), \
                    mock.patch.object(lint_workflows, "BASH", "bash"), \
                    mock.patch.object(lint_workflows.subprocess, "run", side_effect=[version, self.VersionAccepted()]):
                expected = self.VersionAccepted if accepted else AssertionError
                with self.assertRaises(expected):
                    lint_workflows.main()

    def test_release_and_source_build_banners_accept_the_exact_pin(self):
        for banner in [b"1.7.12\nbuilt with go compiler\n", b"v1.7.12\ninstalled by building from source\n"]:
            with self.subTest(banner=banner):
                self.check_version_gate(banner, accepted=True)

    def test_wrong_versions_and_malformed_banners_are_refused(self):
        for banner in [b"", b"1.7.11\n", b"v1.7.13\n", b"vv1.7.12\n",
                       b"1.7.12 extra\n", b"warning\n1.7.12\n", b" 1.7.12\n"]:
            with self.subTest(banner=banner):
                self.check_version_gate(banner)

    def test_matching_banner_cannot_hide_a_failed_version_command(self):
        self.check_version_gate(b"1.7.12\n", code=1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
