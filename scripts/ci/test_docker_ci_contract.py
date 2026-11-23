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

from docker_ci_support import (BASH, DOCKER_JOB, DOCKER_WORKFLOW, PREWARM_WORKFLOW, PULL_NAME, ROOT, TEST_NAME,
                               WORKFLOW, extract_prewarm, extract_pull, fixture_images, image_boundary, sha, step,
                               workflow)


SCRATCH = Path(tempfile.mkdtemp(prefix="vouchfx-docker-ci-contract-"))
STUB = Path(__file__).with_name("pull_stub.py")
COMPATIBILITY_WORKFLOW = ROOT / ".github/workflows/dotnet-compatibility.yml"
CONCURRENCY_GROUP = "${{ github.workflow }}-${{ github.ref }}-${{ github.event_name }}"
DOCKER_JOB_IF = "github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'"


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
        cls.workflow_bytes = DOCKER_WORKFLOW.read_bytes()
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
        # Actual default parity is enforced by ManagedDefaultImagePullCoverageTests;
        # this boundary check also retains the explicit Docker-test inventory.
        self.assertEqual(23, len(images))
        self.assertEqual(23, len(set(images)))
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
        docker = workflow(DOCKER_WORKFLOW)
        # The lane's steps live in the reusable workflow; when it runs (`needs`,
        # `if`) is decided by its caller in build.yml.
        integration = docker["jobs"][DOCKER_JOB]
        caller = data["jobs"]["integration"]
        self.assertFalse(integration.get("continue-on-error", False))
        self.assertFalse(caller.get("continue-on-error", False))
        command = shlex.split(step(TEST_NAME, docker)["run"])
        for value in ["-m:1", "--no-build", "requires=docker", "--blame-crash", "mini"]:
            self.assertIn(value, command)
        # Docker tests execute the canonical CLI; collectors alter its PE bytes.
        self.assertFalse(any(value.lower() == "--collect" or
                             value.lower().startswith(("--collect:", "--collect="))
                             for value in command))
        build = data["jobs"]["build"]
        self.assertFalse(build.get("continue-on-error", False))
        unit_steps = [current for current in build["steps"]
                      if current.get("name") == "Unit tests (requires!=docker)"]
        self.assertEqual(1, len(unit_steps))
        self.assertNotIn("if", unit_steps[0])
        self.assertNotIn("continue-on-error", unit_steps[0])
        unit_command = shlex.split(unit_steps[0]["run"])
        for value in ["-m:1", "--no-build", "requires!=docker", "--collect:XPlat Code Coverage"]:
            self.assertIn(value, unit_command)
        # A hung test must name itself, and a stuck job is bounded at 40 minutes.
        self.assertEqual("10m", unit_command[unit_command.index("--blame-hang-timeout") + 1])
        self.assertEqual("none", unit_command[unit_command.index("--blame-hang-dump-type") + 1])
        self.assertIn("--blame-hang", unit_command)
        self.assertEqual(40, build["timeout-minutes"])
        self.assertEqual("TestResults", command[command.index("--results-directory") + 1])
        self.assertNotIn("VOUCHFX_DCP_CAPTURE_DIR", json.dumps({"env": docker.get("env", {}), "job": integration}))
        self.assertNotIn("secrets.", json.dumps({"env": docker.get("env", {}), "job": integration}))
        self.assertNotIn("secrets.", json.dumps(caller))
        self.assertEqual(25, step(PULL_NAME, docker)["timeout-minutes"])
        gate = data["jobs"]["workflow-contract"]
        self.assertNotIn("if", gate)
        self.assertNotIn("continue-on-error", gate)
        for current in gate["steps"]:
            self.assertNotIn("if", current)
            self.assertNotIn("continue-on-error", current)
        self.assertIn("python -B scripts/ci/lint_workflows.py", [s.get("run") for s in gate["steps"]])
        self.assertIn("python -B -m unittest discover -s scripts/ci -p test_docker_ci_contract.py -v",
                      [s.get("run") for s in gate["steps"]])
        self.assertEqual([{"cron": "17 6 * * *"}], data["on"]["schedule"])
        self.assertIn("workflow_dispatch", data["on"])
        self.assertEqual("build", caller["needs"])
        for concurrent in [data, workflow(COMPATIBILITY_WORKFLOW)]:
            self.assertEqual(CONCURRENCY_GROUP, concurrent["concurrency"]["group"])
            self.assertIs(True, concurrent["concurrency"]["cancel-in-progress"])
        self.assertEqual(DOCKER_JOB_IF, caller["if"])
        compatibility = workflow(COMPATIBILITY_WORKFLOW)
        self.assertEqual([{"cron": "47 6 * * *"}], compatibility["on"]["schedule"])
        self.assertIn("workflow_dispatch", compatibility["on"])
        # The Docker, memory and TLS lanes are run only from build.yml; repeating them
        # here would run each twice on the same runtime.
        self.assertEqual(["compatibility", "roll-forward"], sorted(compatibility["jobs"]))
        self.assertEqual(["main"], compatibility["on"]["pull_request"]["branches"])
        self.assertEqual(["main"], compatibility["on"]["push"]["branches"])
        for always_job in ["build", "windows-unit", "memory-leak", "tls-transport", "docs-build"]:
            with self.subTest(job=always_job):
                self.assertNotIn("if", data["jobs"][always_job])
        self.assertEqual("build", data["jobs"]["memory-leak"]["needs"])
        badge_concurrency = data["jobs"]["coverage-badge"]["concurrency"]
        self.assertEqual("coverage-badge-main", badge_concurrency["group"])
        self.assertIs(False, badge_concurrency["cancel-in-progress"])
        for per_pr_job in ["compatibility", "roll-forward"]:
            with self.subTest(job=per_pr_job):
                self.assertNotIn("if", compatibility["jobs"][per_pr_job])
        self.assertNotIn("if", step(PULL_NAME, docker))
        self.assertNotIn("continue-on-error", step(PULL_NAME, docker))
        self.assertNotIn("paths", data["on"]["push"])
        self.assertNotIn("paths", data["on"]["pull_request"])

    def test_all_solution_test_lanes_bound_cross_project_contention(self):
        compatibility = ROOT / ".github/workflows/dotnet-compatibility.yml"
        lanes = [
            (WORKFLOW, "build", "Unit tests (requires!=docker)"),
            (WORKFLOW, "windows-unit", "Unit tests (requires!=docker)"),
            (DOCKER_WORKFLOW, DOCKER_JOB, TEST_NAME),
            (compatibility, "compatibility", "Docker-free tests"),
        ]
        for path, job, name in lanes:
            with self.subTest(workflow=path.name, job=job):
                current = step(name, workflow(path), job_name=job)
                command = shlex.split(current["run"])
                self.assertEqual(["dotnet", "test", "vouchfx.sln"], command[:3])
                self.assertIn("-m:1", command)
                self.assertIn("--no-build", command)
                self.assertNotIn("if", current)
                self.assertNotIn("continue-on-error", current)
                if job in ("build", "compatibility"):
                    self.assertIn("--blame-hang", command)
                    self.assertEqual("10m", command[command.index("--blame-hang-timeout") + 1])
                    self.assertEqual("none", command[command.index("--blame-hang-dump-type") + 1])

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
        source = DOCKER_WORKFLOW.read_bytes()
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
        if DOCKER_WORKFLOW.read_bytes() != cls.workflow_bytes:
            raise AssertionError("Harness mutated its workflow source")


REQUIRED_DOCKER_CHECK = "Integration tests — Docker / Aspire"
DOCKER_GATE_JOB = "integration-gate"
DOCKER_GATE_IF = "always() && needs.integration.result != 'skipped'"
REVIEWED_SHA = "${{ inputs.reviewed_sha }}"
DOCKER_ARTEFACTS = {"docker-integration-test-results", "owned-environment-acceptance",
                    "integration-dcp-captures", "integration-crash-dumps"}


def walk(value, path=()):
    """Yield (path, key, value) for every mapping entry and (path, None, value) for every scalar."""
    if isinstance(value, dict):
        for key, item in value.items():
            yield path, key, item
            yield from walk(item, path + (key,))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from walk(item, path + (index,))
    else:
        yield path, None, value


class DockerIntegrationReusableWorkflowTests(unittest.TestCase):
    """The Docker / Aspire lane is a reusable workflow with one unconditional job.

    build.yml calls it at the commit under test, and a separate gate job carries the
    required check name, because a caller reports as "<caller> / <called job>" and a
    skipped caller only under its own name.
    """

    @classmethod
    def setUpClass(cls):
        cls.build = workflow()
        cls.docker = workflow(DOCKER_WORKFLOW)
        cls.caller = cls.build["jobs"]["integration"]
        cls.gate = cls.build["jobs"][DOCKER_GATE_JOB]
        cls.lane = cls.docker["jobs"][DOCKER_JOB]

    def test_callee_is_called_only_with_one_required_reviewed_sha_input(self):
        self.assertEqual(["workflow_call"], list(self.docker["on"]))
        call = self.docker["on"]["workflow_call"]
        self.assertEqual(["inputs"], list(call))
        self.assertEqual(["reviewed_sha"], list(call["inputs"]))
        reviewed = call["inputs"]["reviewed_sha"]
        self.assertIs(True, reviewed["required"])
        self.assertEqual("string", reviewed["type"])
        self.assertNotIn("default", reviewed)

    def test_callee_maps_no_secrets_and_no_workflow_inherits_them(self):
        for path, key, value in walk(self.docker):
            self.assertNotEqual("secrets", key, path)
            if isinstance(value, str):
                # Any use of the secrets context inside an expression, in any letter case, with
                # string literals skipped as the runner does: secrets.X, secrets[...],
                # toJSON(secrets), format('{{{0}}}', toJSON(secrets)).
                self.assertIsNone(re.search(r"\$\{\{(?:'[^']*'|(?!\}\})[^'])*?\bsecrets\b", value,
                                            re.IGNORECASE | re.DOTALL), path)
        workflows = sorted((ROOT / ".github/workflows").glob("*.y*ml"))
        self.assertIn(DOCKER_WORKFLOW, workflows)
        for path in workflows:
            with self.subTest(workflow=path.name):
                source = path.read_text(encoding="utf-8")
                self.assertIsNone(re.search(r"(?m)^[^#\n]*\bsecrets:\s*inherit\b", source))
                for job_id, job in workflow(path).get("jobs", {}).items():
                    self.assertNotEqual("inherit", job.get("secrets"), job_id)

    def test_every_caller_passes_only_the_run_commit_and_no_secrets(self):
        callers = 0
        for path in sorted((ROOT / ".github/workflows").glob("*.y*ml")):
            for job_id, job in workflow(path).get("jobs", {}).items():
                if not str(job.get("uses", "")).endswith("docker-integration.yml"):
                    continue
                callers += 1
                with self.subTest(workflow=path.name, job=job_id):
                    self.assertNotIn("secrets", job)
                    self.assertEqual(["reviewed_sha"], list(job.get("with", {})))
                    tls = self.build["jobs"]["tls-transport"]["with"]["reviewed_sha"]
                    self.assertIn(job["with"]["reviewed_sha"], {"${{ github.sha }}", tls})
        self.assertGreaterEqual(callers, 1)

    def test_callee_run_scripts_never_interpolate_expressions(self):
        # Values reach scripts through `env`; an inline ${{ }} in a run script is script injection.
        scripts = [step["run"] for step in self.lane["steps"] if "run" in step]
        self.assertTrue(scripts)
        for script in scripts:
            self.assertNotIn("${{", script)

    def test_callee_permissions_env_and_concurrency(self):
        self.assertEqual({"contents": "read"}, self.docker["permissions"])
        self.assertNotIn("permissions", self.lane)
        self.assertNotIn("concurrency", self.docker)
        self.assertNotIn("concurrency", self.lane)
        self.assertNotIn("defaults", self.docker)
        self.assertNotIn("defaults", self.build)
        # A caller's top-level env does not reach a called workflow: carry it verbatim.
        self.assertEqual(self.build["env"], self.docker["env"])

    def test_callee_has_one_unconditional_job_on_the_same_runner(self):
        self.assertEqual([DOCKER_JOB], list(self.docker["jobs"]))
        for key in ["if", "needs", "uses", "strategy", "continue-on-error"]:
            self.assertNotIn(key, self.lane)
        self.assertEqual("ubuntu-22.04", self.lane["runs-on"])
        self.assertEqual({"VOUCHFX_TOPOLOGY_PR_HEAD_SHA": REVIEWED_SHA}, self.lane["env"])
        self.assertNotEqual(REQUIRED_DOCKER_CHECK, self.lane["name"])
        self.assertNotIn("${{", self.lane["name"])

    def test_no_callee_condition_depends_on_the_event_or_ref(self):
        # The lane has callers on different events (schedule and manual dispatch in build.yml;
        # manual dispatch in three modes in release.yml) and must run the same
        # steps for each: a condition that reads the run's context would skip steps for one
        # caller while the lane still reported success.
        conditions = [("job", self.lane["if"])] if "if" in self.lane else []
        conditions += [(step.get("name", index), step["if"])
                       for index, step in enumerate(self.lane["steps"]) if "if" in step]
        self.assertTrue(conditions)
        for where, condition in conditions:
            with self.subTest(where=where):
                self.assertIsNone(re.search(r"(?<![\w.-])(github|env|vars|inputs)(?![\w-])|event_name",
                                            str(condition), re.IGNORECASE),
                                  "the lane has callers on different events and must run the same "
                                  "steps for each")

    def test_lane_job_has_a_120_minute_timeout_and_the_callee_stays_one_unconditional_job(self):
        # The release workflow gates signing and publication on this job. A hang must
        # end in a failure well before GitHub's 360-minute default, and the callee must
        # keep exactly one job without an `if`: a call whose inner jobs are all skipped
        # can report success, which would open the gate.
        self.assertEqual(120, self.lane["timeout-minutes"])
        self.assertEqual([DOCKER_JOB], list(self.docker["jobs"]))
        self.assertNotIn("if", self.lane)

    def test_callee_checks_out_and_asserts_the_reviewed_sha(self):
        checkout, confirm = self.lane["steps"][:2]
        self.assertRegex(checkout["uses"], r"\Aactions/checkout@[0-9a-f]{40}\Z")
        self.assertEqual({"persist-credentials": False, "ref": REVIEWED_SHA}, checkout["with"])
        self.assertEqual({"REVIEWED_SHA": REVIEWED_SHA}, confirm["env"])
        self.assertNotIn("if", confirm)
        self.assertNotIn("continue-on-error", confirm)
        lines = confirm["run"].splitlines()
        self.assertEqual("set -euo pipefail", lines[0])
        # `|| exit 1` stops the script on bash older than 4.1, where a failed [[ =~ ]] does not trip `set -e`.
        self.assertIn('[[ "$REVIEWED_SHA" =~ ^[0-9a-f]{40}$ ]] || exit 1', lines)
        self.assertIn('test "$(git rev-parse HEAD)" = "$REVIEWED_SHA"', lines)
        # Fail fast: the lane's evidence records GITHUB_SHA, so the input must be the run's own commit.
        self.assertIn('if [ "$GITHUB_SHA" != "$REVIEWED_SHA" ]; then', lines)
        self.assertLess(lines.index('if [ "$GITHUB_SHA" != "$REVIEWED_SHA" ]; then'),
                        lines.index('test "$(git rev-parse HEAD)" = "$REVIEWED_SHA"'))
        mismatch = [line for line in lines if line.lstrip().startswith("echo ")]
        self.assertEqual(1, len(mismatch))
        self.assertIn("::error::", mismatch[0])
        self.assertIn("run's own commit", mismatch[0])
        self.assertIn("must be that commit", mismatch[0])
        self.assertLess(lines.index('if [ "$GITHUB_SHA" != "$REVIEWED_SHA" ]; then'),
                        lines.index(mismatch[0]))
        self.assertIn("github.sha", self.docker["on"]["workflow_call"]["inputs"]["reviewed_sha"]["description"])
        checkouts = [s for s in self.lane["steps"] if str(s.get("uses", "")).startswith("actions/checkout@")]
        self.assertEqual([checkout], checkouts)

    def test_confirm_step_script_behaviour(self):
        confirm = self.lane["steps"][1]
        commit = "0123456789abcdef0123456789abcdef01234567"
        repo = SCRATCH / "confirm-repo"
        repo.mkdir()
        git = ["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false"]
        subprocess.run(git + ["init", "-q"], cwd=repo, check=True, capture_output=True)
        subprocess.run(git + ["commit", "-q", "--allow-empty", "-m", "x"], cwd=repo, check=True, capture_output=True)
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, check=True, capture_output=True,
                              text=True).stdout.strip()
        cases = [("matching", head, head, 0), ("run commit differs", head, commit, 1),
                 ("not a commit id", "main", "main", 1)]
        for label, github_sha, reviewed, expected in cases:
            with self.subTest(case=label):
                env = dict(os.environ, GITHUB_SHA=github_sha, REVIEWED_SHA=reviewed)
                completed = subprocess.run([BASH, "--noprofile", "--norc", "-e", "-c", confirm["run"]],
                                           cwd=repo, env=env, capture_output=True, text=True, timeout=30)
                self.assertEqual(expected, completed.returncode, completed.stderr)
                if label == "run commit differs":
                    self.assertIn("::error::", completed.stdout)
                    self.assertIn(github_sha, completed.stdout)
                    self.assertIn(reviewed, completed.stdout)

    def test_artefact_names_stay_unique_in_the_calling_run(self):
        uploads = [s["with"]["name"] for s in self.lane["steps"]
                   if str(s.get("uses", "")).startswith("actions/upload-artifact@")]
        self.assertEqual(len(uploads), len(set(uploads)))
        self.assertEqual(DOCKER_ARTEFACTS, set(uploads))
        # Every workflow that calls the lane shares a run with its four artefact names.
        callers = {}
        for path in sorted((ROOT / ".github/workflows").glob("*.y*ml")):
            data = workflow(path)
            if any(job.get("uses") == "./.github/workflows/docker-integration.yml"
                   for job in data.get("jobs", {}).values()):
                callers[path.name] = data
        self.assertEqual({"build.yml", "release.yml"}, set(callers))
        for name, data in callers.items():
            with self.subTest(workflow=name):
                others = [s["with"]["name"] for job in data["jobs"].values() for s in job.get("steps", [])
                          if str(s.get("uses", "")).startswith("actions/upload-artifact@")]
                self.assertFalse(DOCKER_ARTEFACTS & set(others))

    def test_caller_passes_only_reviewed_sha_and_keeps_its_schedule(self):
        self.assertEqual({"name", "needs", "if", "uses", "with"}, set(self.caller))
        self.assertEqual("./.github/workflows/docker-integration.yml", self.caller["uses"])
        self.assertEqual("build", self.caller["needs"])
        self.assertEqual(DOCKER_JOB_IF, self.caller["if"])
        tls = self.build["jobs"]["tls-transport"]["with"]["reviewed_sha"]
        self.assertEqual({"reviewed_sha": tls}, self.caller["with"])
        self.assertNotIn("secrets", self.caller)

    def test_gate_carries_the_required_name_and_fails_on_failure_or_cancellation(self):
        names = [job.get("name") for job in self.build["jobs"].values()]
        self.assertEqual(1, names.count(REQUIRED_DOCKER_CHECK))
        self.assertEqual(REQUIRED_DOCKER_CHECK, self.gate["name"])
        # Neither the skipped caller nor "<caller> / <called job>" may report the required name.
        self.assertNotEqual(REQUIRED_DOCKER_CHECK, self.caller["name"])
        self.assertNotEqual(REQUIRED_DOCKER_CHECK, f"{self.caller['name']} / {self.lane['name']}")
        self.assertEqual("integration", self.gate["needs"])
        # Skipped exactly when the lane was skipped; otherwise it must run, even after a cancellation.
        self.assertEqual(DOCKER_GATE_IF, self.gate["if"])
        self.assertNotIn("continue-on-error", self.gate)
        (only,) = self.gate["steps"]
        self.assertEqual({"LANE_RESULT": "${{ needs.integration.result }}"}, only["env"])
        self.assertNotIn("if", only)
        self.assertNotIn("continue-on-error", only)
        for result, expected in [("success", 0), ("failure", 1), ("cancelled", 1), ("skipped", 1), ("", 1)]:
            with self.subTest(result=result):
                env = dict(os.environ, LANE_RESULT=result)
                completed = subprocess.run([BASH, "--noprofile", "--norc", "-e", "-c", only["run"]],
                                           env=env, capture_output=True, text=True, timeout=10)
                self.assertEqual(expected, completed.returncode, completed.stderr)
                if expected:
                    # The red check explains itself: the message names the result it saw.
                    self.assertIn("::error::", completed.stdout)
                    self.assertIn(f"lane result: {result} (", completed.stdout)
                else:
                    self.assertEqual("", completed.stdout)


class ReusableWorkflowPrewarmTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.script = extract_prewarm()
        cls.workflow_bytes = PREWARM_WORKFLOW.read_bytes()
        (SCRATCH / "actual-prewarm.sh").write_bytes(cls.script)
        (SCRATCH / "prewarm-extraction.json").write_text(json.dumps({
            "workflow_sha256": sha(cls.workflow_bytes), "script_sha256": sha(cls.script),
            "script_bytes": len(cls.script), "parser_equals_source": True,
        }, indent=2))

    def run_prewarm_case(self, name, prewarm_images, scenario, fail_token=False, script=None):
        root = Path(tempfile.mkdtemp(prefix="prewarm-" + name + "-", dir=SCRATCH))
        binary = root / "bin"
        binary.mkdir()
        (root / "scenario.json").write_text(json.dumps(scenario), encoding="utf-8")
        actual = self.script if script is None else script
        target = root / "prewarm.sh"
        target.write_bytes(actual)
        commands = ["docker"] + (["head"] if fail_token else [])
        for command in commands:
            stub = binary / command
            stub.write_text('#!/usr/bin/env bash\nexec "$VOUCHFX_CI_PYTHON" "$VOUCHFX_CI_STUB" ' + command + ' "$@"\n',
                            encoding="utf-8")
            stub.chmod(0o755)
        env = os.environ.copy()
        env.update(PATH=str(binary) + os.pathsep + env["PATH"], PREWARM_IMAGES=prewarm_images,
                   VOUCHFX_CI_CASE=str(root), VOUCHFX_CI_PYTHON=Path(sys.executable).as_posix(),
                   VOUCHFX_CI_STUB=STUB.as_posix(), VOUCHFX_CI_BASH=Path(BASH).as_posix(),
                   VOUCHFX_CI_BIN=binary.as_posix(), VOUCHFX_CI_WINDOWS="1" if os.name == "nt" else "0")
        launcher = '''set -euo pipefail
fixture_bin="$VOUCHFX_CI_BIN"
if [ "$VOUCHFX_CI_WINDOWS" = 1 ]; then fixture_bin=$(cygpath -u "$fixture_bin"); fi
export PATH="$fixture_bin:$PATH"
for command in docker; do
  [ "$(command -v "$command")" = "$fixture_bin/$command" ] || exit 91
done
if [ "${VOUCHFX_CI_FAIL_TOKEN:-0}" = 1 ]; then
  [ "$(command -v head)" = "$fixture_bin/head" ] || exit 92
fi
source "$1"
'''
        if fail_token:
            env["VOUCHFX_CI_FAIL_TOKEN"] = "1"
        process = subprocess.Popen([BASH, "--noprofile", "--norc", "-c", launcher, "prewarm-fixture", str(target)],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env,
                                   start_new_session=os.name != "nt")
        timed_out = False
        try:
            stdout, stderr = process.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                cleanup = subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                                         capture_output=True, timeout=10)
                (root / "timeout-cleanup.exit").write_text(str(cleanup.returncode))
                (root / "timeout-cleanup.stdout").write_bytes(cleanup.stdout)
                (root / "timeout-cleanup.stderr").write_bytes(cleanup.stderr)
            else:
                os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate(timeout=5)
        (root / "stdout").write_bytes(stdout)
        (root / "stderr").write_bytes(stderr)
        (root / "script.exit").write_text(str(process.returncode))
        (root / "result.json").write_text(json.dumps({
            "expected_exit": 0, "actual_exit": process.returncode,
            "source_script_sha256": sha(actual), "workflow_extracted_script_sha256": sha(self.script),
            "is_unmodified_workflow_block": actual == self.script, "timed_out": timed_out,
        }, indent=2))
        self.assertFalse(timed_out, f"Fixture exceeded its 30-second deadline: {root}")
        self.assertEqual(0, process.returncode, f"prewarm exit {process.returncode}; {root}; {stderr!r}")
        calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()] if (root / "calls.jsonl").exists() else []
        try:
            commands, tokens = interpreted_commands(stdout)
        except AssertionError as error:
            (root / "interpreter.exit").write_text("1")
            (root / "interpreter.stderr").write_text(str(error), encoding="utf-8")
            raise
        (root / "interpreter.exit").write_text("0")
        if not fail_token:
            self.assertEqual(len(calls), len(tokens))
            self.assertEqual(len(calls), sum(command.startswith(b"::group::") for command in commands))
            self.assertEqual(len(calls), commands.count(b"::endgroup::"))
        return root, subprocess.CompletedProcess(process.args, process.returncode, stdout, stderr), calls, commands

    def test_source_extraction_and_bash_shellcheck(self):
        for command, args in [(BASH, ["-n"]), (shutil.which("shellcheck"), ["--shell=bash"])]:
            self.assertTrue(command, "ShellCheck 0.11.0 is mandatory")
            if args == ["--shell=bash"]:
                version = subprocess.run([command, "--version"], capture_output=True, timeout=10)
                self.assertEqual(0, version.returncode)
                self.assertIn(b"version: 0.11.0", version.stdout)
            result = subprocess.run([command, *args, str(SCRATCH / "actual-prewarm.sh")], capture_output=True, timeout=30)
            stem = "prewarm-bash-n" if args == ["-n"] else "prewarm-shellcheck"
            (SCRATCH / (stem + ".stdout")).write_bytes(result.stdout)
            (SCRATCH / (stem + ".stderr")).write_bytes(result.stderr)
            (SCRATCH / (stem + ".exit")).write_text(str(result.returncode))
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_normal_quote_trimming_and_blank_rows(self):
        images = ["postgres:18", "alpine@sha256:0123456789"]
        source = "\n  postgres:18 \t\n \t\nalpine@sha256:0123456789  \n"
        _, _, calls, commands = self.run_prewarm_case("trim", source, {image: [reply()] for image in images})
        self.assertEqual(images, [call["args"][1] for call in calls])
        self.assertFalse(any(command.startswith(b"::warning::") for command in commands))

    def test_unbalanced_quote_is_passed_verbatim_and_failure_stays_nonfatal(self):
        image = 'foo"bar:1'
        _, result, calls, commands = self.run_prewarm_case("unbalanced-quote", f"  {image} \t\n",
                                                            {image: [reply(1, b"manifest unknown")]})
        self.assertEqual([image], [call["args"][1] for call in calls])
        self.assertEqual(0, result.returncode)
        warnings = [command for command in commands if command.startswith(b"::warning::")]
        self.assertEqual(1, len(warnings))
        self.assertIn(b"pre-pull of foo\"bar:1 failed", warnings[0])

    def test_pre_warm_image_annotations_are_escaped_and_pull_output_is_fenced(self):
        image = "registry.example/app%0A:1"
        output = b"::warning::INJECTED\r::set-output name=x::EVIL"
        _, result, calls, commands = self.run_prewarm_case("fence", f"{image}\n", {image: [reply(0, output)]})
        self.assertEqual([image], [call["args"][1] for call in calls])
        self.assertIn(output, result.stdout)
        self.assertIn(b"::group::docker pull registry.example/app%250A:1", commands)
        self.assertFalse(any(command.startswith((b"::warning::", b"::set-output")) for command in commands))

    def test_token_failure_skips_pull_without_failing_best_effort_step(self):
        image = "registry.example/app:1"
        _, result, calls, commands = self.run_prewarm_case("token-refusal", f"{image}\n",
                                                             {image: [reply()]}, fail_token=True)
        self.assertEqual(["head"], [call["command"] for call in calls])
        self.assertIn(b"could not generate a random log-fencing token", commands[0])
        self.assertNotIn(b"::group::", commands)
        self.assertEqual(0, result.returncode)

    def test_fence_regression_control_detects_missing_newline_separator(self):
        separator = next(line for line in self.script.splitlines(keepends=True)
                         if line.strip() == b"printf " + bytes((39, 92, 110, 39)))
        self.assertEqual(1, self.script.count(separator))
        mutant = self.script.replace(separator, b"", 1)
        self.assertNotEqual(self.script, mutant)
        with self.assertRaisesRegex(AssertionError, "left workflow commands stopped"):
            self.run_prewarm_case("missing-resume-separator", "registry.example/app:1\n",
                                  {"registry.example/app:1": [reply()]}, script=mutant)
        cases = list(SCRATCH.glob("prewarm-missing-resume-separator-*"))
        self.assertEqual(1, len(cases))
        self.assertEqual("1", (cases[0] / "interpreter.exit").read_text())
        self.assertIn("left workflow commands stopped", (cases[0] / "interpreter.stderr").read_text())

    @classmethod
    def tearDownClass(cls):
        if PREWARM_WORKFLOW.read_bytes() != cls.workflow_bytes:
            raise AssertionError("Harness mutated its reusable workflow source")


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
