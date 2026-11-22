"""Release event contract and actual version-step regressions; no publication."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

from docker_ci_support import BASH, ROOT, workflow


GATE = "docker-integration"
# Jobs that sign, notarise or contact an external service at build time and are
# deliberately NOT behind the Docker gate: the owner chose to gate only
# `sign-and-attest` and `publish-release`. Each entry pins the job's exact set of
# side-effect reasons (as `side_effects` derives them), so a new secret, a new
# permission, a new action, or a new distinct side-effect command match (even inside
# an existing step) in an exempt job fails the test. This is the tripwire described
# in the `side_effects` docstring, not proof that the job has no other side effect.
UNGATED_BUILD_SIGNING = {
    # Authenticode signing with signtool and the DigiCert timestamp call.
    "build-msi": {"secret:CODESIGN_PFX_BASE64", "secret:CODESIGN_PFX_PASSWORD", "condition:HAS_CODESIGN",
                  "command:Sign vouchfx.exe (Authenticode) — secret-gated:signtool",
                  "command:Sign MSI (Authenticode) — secret-gated:signtool"},
    # GPG detached signature, no external call.
    "build-deb": {"secret:GPG_SIGNING_KEY", "secret:GPG_PASSPHRASE", "condition:HAS_GPG",
                  "command:Sign .deb (GPG detached signature) — secret-gated:--detach-sign"},
    # Apple Notary Service submission and stapling.
    "build-pkg": {"secret:APPLE_ID", "secret:APPLE_TEAM_ID", "secret:APPLE_APP_SPECIFIC_PASSWORD",
                  "condition:HAS_APPLE", "command:Notarise .pkg (Apple Notary Service) — secret-gated:notarytool"},
}
SIDE_EFFECT_ACTIONS = re.compile(
    r"attest-build-provenance|cosign-installer|NuGet/login|vsce|"
    r"upload-release-asset|action-gh-release|softprops/", re.IGNORECASE)
# Matched in any letter case: action names are case-insensitive, and so are the
# Windows tools among the commands (signtool).
SIDE_EFFECT_COMMANDS = re.compile(
    r"cosign\s+sign|gh\s+release\s+(?:create|upload|edit)|nuget\s+push|"
    r"vsce\s+publish|notarytool|signtool|--detach-sign|gh\s+attestation|"
    r"\bovsx\b|npm\s+publish|docker\s+push|gpg\b.*--(?:clear)?sign\b", re.IGNORECASE)
# Any status-check function in a job-level `if` replaces the implicit success(), so a job
# downstream of the gate may use none. Expression function names are case-insensitive.
DEFEATS_NEEDS = re.compile(r"\b(?:always|cancelled|failure|success)\s*\(", re.IGNORECASE)


def needs_of(job):
    needs = job.get("needs", [])
    return {needs} if isinstance(needs, str) else set(needs)


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


def side_effects(job):
    """Reasons a job has an external side effect, derived from the parsed job.

    A reason is a secret the job reads (any `secrets` reference inside an expression
    counts, including `secrets['NAME']` and `toJSON(secrets)`), a `HAS_*` condition
    derived from one, any write permission (which covers the OIDC token, attestation
    and release content), or a step that uses a signing, attestation or publishing
    action or command.

    Secret names are kept as written (`Secrets.NUGET_API_KEY` and
    `secrets.nuget_api_key` give two different reasons); any new reason in an
    allowlisted job fails the exact-set check either way. A command reason carries
    the step name and each distinct matched command text, so a second command added
    to an existing step is a new reason.

    This is a tripwire for the ordinary ways a side effect is added (a secret, a
    write permission, a known signing or publishing action or command), not a proof
    that no side effect exists. It deliberately does not see: an unauthenticated
    network call, an effect inside a called reusable workflow, or a command that is
    not in `SIDE_EFFECT_COMMANDS` and uses no secret. A read-only
    `${{ secrets.GITHUB_TOKEN }}` in an ungated build job counts as a side effect,
    deliberately, because the check cannot tell a read from a write; when that
    happens, gate the job behind `docker-integration`, or add its reasons to
    `UNGATED_BUILD_SIGNING` with a comment saying why, rather than weakening the rule.

    Limits. There is no rule for an unauthenticated network side effect (`curl`,
    `gh api` without a token): the check relies on any authenticated effect needing a
    secret, a write permission or `id-token`. A `curl -T` upload step with no secret
    is therefore not recognised. A job that calls a reusable workflow is judged on
    the call only (its `permissions`, `with` and `secrets`), not on the workflow it
    calls. Workflow-level `env` and `permissions` are checked separately, by
    `test_top_level_has_no_secret_env_and_read_only_permissions`.
    """
    reasons = set()
    for text in strings(job):
        reasons.update(f"secret:{name}" for name in re.findall(r"\bsecrets\.(\w+)", text, re.IGNORECASE))
        # String literals are skipped as the runner does, so a `}}` inside one
        # (format('{{{0}}}', toJSON(secrets))) does not end the expression early.
        if re.search(r"\$\{\{(?:'[^']*'|(?!\}\})[^'])*?\bsecrets\b(?!\.)", text, re.IGNORECASE | re.DOTALL):
            reasons.add("secret:<context>")
    permissions = job.get("permissions") or {}
    if isinstance(permissions, str):
        # The shorthand forms: write-all grants every scope, read-all none to write.
        permissions = {"*": "write" if permissions == "write-all" else "read"}
    for permission, level in permissions.items():
        if level == "write":
            reasons.add(f"permission:{permission}")
    for step in job.get("steps", []):
        if SIDE_EFFECT_ACTIONS.search(step.get("uses", "")):
            reasons.add(f"action:{step['uses'].split('@')[0]}")
        for match in SIDE_EFFECT_COMMANDS.finditer(step.get("run", "")):
            reasons.add(f"command:{step.get('name', '?')}:{' '.join(match.group(0).lower().split())}")
        for name in re.findall(r"\benv\.(HAS_\w+)", str(step.get("if", ""))):
            reasons.add(f"condition:{name}")
    return reasons


def ancestors(jobs, name):
    seen, pending = set(), list(needs_of(jobs[name]))
    while pending:
        current = pending.pop()
        if current not in seen:
            seen.add(current)
            pending.extend(needs_of(jobs[current]))
    return seen


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
        self.assertEqual({GATE, "tls-transport", "build-dotnet", "sign-and-attest",
                          "smoke-test-packaged-tool", "smoke-test-distribution-platforms"},
                         set(self.jobs["publish-release"]["needs"]))
        self.assertIn("publish-release", self.jobs["publish-marketplace"]["needs"])
        commands = [s.get("run", "") for j in self.jobs.values() for s in j.get("steps", [])]
        self.assertFalse(any("gh release create" in command for command in commands))

    def test_docker_gate_is_an_unconditional_call_of_the_lane_at_the_run_commit(self):
        gate = self.jobs[GATE]
        self.assertEqual({"name", "permissions", "uses", "with"}, set(gate))
        self.assertEqual("Docker integration", gate["name"])
        self.assertEqual("./.github/workflows/docker-integration.yml", gate["uses"])
        self.assertEqual({"reviewed_sha": "${{ github.sha }}"}, gate["with"])
        self.assertEqual({"contents": "read"}, gate["permissions"])
        # No `if`, `needs`, `secrets` or `continue-on-error`: the gate starts with the run
        # on every event and a failure or skip of the lane must be visible downstream.
        for key in ("if", "needs", "secrets", "continue-on-error", "strategy"):
            self.assertNotIn(key, gate)

    def test_signing_and_publication_need_the_docker_gate(self):
        for name in ("sign-and-attest", "publish-release"):
            with self.subTest(job=name):
                self.assertIn(GATE, needs_of(self.jobs[name]))

    def test_every_checkout_builds_the_commit_the_gate_tested(self):
        checkouts = 0
        for name, job in self.jobs.items():
            for index, step in enumerate(job.get("steps", [])):
                if not str(step.get("uses", "")).startswith("actions/checkout@"):
                    continue
                checkouts += 1
                with self.subTest(job=name, step=step.get("name", index)):
                    ref = step.get("with", {}).get("ref")
                    self.assertTrue(ref is None or ref == "${{ github.sha }}",
                                    "the Docker gate tests the run's commit, so every checkout must "
                                    f"build that commit; found ref {ref!r}")
        self.assertGreaterEqual(checkouts, 1)

    def test_every_side_effect_job_is_reachable_from_the_gate(self):
        effects = {name: side_effects(job) for name, job in self.jobs.items() if name != GATE}
        with_effects = {name for name, reasons in effects.items() if reasons}
        # Sanity: the derivation sees the jobs it is meant to protect.
        self.assertTrue({"sign-and-attest", "publish-release", "publish-marketplace"} <= with_effects)
        ungated = {name for name in with_effects if GATE not in ancestors(self.jobs, name)}
        self.assertEqual(set(UNGATED_BUILD_SIGNING), ungated,
                         "a job with an external side effect is outside the Docker gate, or the "
                         "build-time signing allowlist is stale")
        for name, expected in UNGATED_BUILD_SIGNING.items():
            with self.subTest(job=name):
                self.assertEqual(expected, effects[name])

    def test_top_level_has_no_secret_env_and_read_only_permissions(self):
        # A workflow-level `env` reaches every job, so a secret there is outside the
        # per-job derivation; workflow-level permissions are the default for every job.
        self.assertEqual([], [text for text in strings(self.data.get("env", {}))
                              if re.search(r"\bsecrets\b", text, re.IGNORECASE)])
        self.assertEqual({"contents": "read"}, self.data["permissions"])

    def test_nothing_after_the_gate_can_run_when_the_gate_did_not_pass(self):
        downstream = {name for name in self.jobs if GATE in ancestors(self.jobs, name)}
        self.assertTrue({"sign-and-attest", "publish-release", "publish-marketplace"} <= downstream)
        for name in sorted(downstream):
            with self.subTest(job=name):
                self.assertIsNone(DEFEATS_NEEDS.search(str(self.jobs[name].get("if", ""))),
                                  "status functions defeat the gate's needs")
                self.assertNotIn("continue-on-error", self.jobs[name])

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


MOVE_WORKFLOW = ROOT / ".github/workflows/move-floating-tag.yml"
MOVE_PATH = "./.github/workflows/move-floating-tag.yml"
EXPECTED_SHA = "a" * 40
OTHER_SHA = "b" * 40
SHA_B = "1" * 40
SHA_C = "2" * 40
# Tag objects of annotated tags, which differ from the commits they peel to.
SHA_X = "3" * 40
SHA_Y = "4" * 40


def workflow_files():
    directory = ROOT / ".github/workflows"
    return sorted({*directory.glob("*.yml"), *directory.glob("*.yaml")})


def triggers_of(data):
    # `on` may be a name, a list of names or a map of names.
    trigger = data["on"]
    return {trigger} if isinstance(trigger, str) else set(trigger)


class MoveFloatingTagStructureTests(unittest.TestCase):
    """The floating tags move only as the last job of a release run, after publication."""

    @classmethod
    def setUpClass(cls):
        cls.release = workflow(ROOT / ".github/workflows/release.yml")
        cls.move = workflow(MOVE_WORKFLOW)

    def test_called_workflow_has_exactly_the_call_and_dispatch_triggers(self):
        self.assertEqual({"workflow_call", "workflow_dispatch"}, triggers_of(self.move))
        inputs = self.move["on"]["workflow_call"]["inputs"]
        self.assertEqual({"tag", "expected_sha"}, set(inputs))
        for name, spec in inputs.items():
            with self.subTest(input=name):
                self.assertIs(True, spec["required"])
                self.assertEqual("string", spec["type"])
        self.assertEqual({"tag"}, set(self.move["on"]["workflow_dispatch"]["inputs"]))

    def test_only_release_yml_triggers_on_release(self):
        triggering = {path.name for path in workflow_files() if "release" in triggers_of(workflow(path))}
        self.assertEqual({"release.yml"}, triggering)

    def test_only_release_yml_calls_the_called_workflow(self):
        callers = set()
        for path in workflow_files():
            for name, job in workflow(path)["jobs"].items():
                if "move-floating-tag.yml" in str(job.get("uses", "")):
                    callers.add((path.name, name))
        self.assertEqual({("release.yml", "move-floating-tag")}, callers)

    def test_release_job_calls_it_last_behind_publication(self):
        job = self.release["jobs"]["move-floating-tag"]
        self.assertEqual(MOVE_PATH, job["uses"])
        self.assertTrue({"publish-release", "publish-marketplace", "build-dotnet"} <= needs_of(job))
        self.assertEqual({"tag": "${{ needs.build-dotnet.outputs.tag }}",
                          "expected_sha": "${{ github.sha }}"}, job["with"])
        # The same tag value `publish-release` publishes under.
        self.assertEqual("${{ needs.build-dotnet.outputs.tag }}",
                         self.release["jobs"]["publish-release"]["env"]["TAG"])
        self.assertEqual({"contents": "write"}, job["permissions"])
        self.assertEqual("github.event_name != 'workflow_dispatch'", job["if"])
        self.assertIsNone(DEFEATS_NEEDS.search(job["if"]))
        for key in ("secrets", "continue-on-error", "strategy", "concurrency", "steps", "runs-on"):
            self.assertNotIn(key, job)
        # Last job: nothing in the run waits for it.
        for name, other in self.release["jobs"].items():
            self.assertNotIn("move-floating-tag", needs_of(other), name)

    def test_release_job_is_a_side_effect_job_behind_the_gate(self):
        jobs = self.release["jobs"]
        self.assertIn("permission:contents", side_effects(jobs["move-floating-tag"]))
        self.assertIn(GATE, ancestors(jobs, "move-floating-tag"))
        self.assertNotIn("move-floating-tag", UNGATED_BUILD_SIGNING)

    def test_write_permission_is_on_the_calling_and_called_jobs_only(self):
        for label, data in (("release.yml", self.release), ("move-floating-tag.yml", self.move)):
            with self.subTest(file=label):
                self.assertEqual({"contents": "read"}, data["permissions"])
        def writes(permissions):
            # The shorthand forms count too: `write-all` grants every write scope.
            if isinstance(permissions, str):
                return permissions != "read-all"
            return isinstance(permissions, dict) and permissions.get("contents") == "write"

        def writers(data):
            return {name for name, job in data["jobs"].items() if writes(job.get("permissions"))}

        # In release.yml the calling job and publish-release (which also holds id-token).
        self.assertEqual({"move-floating-tag", "publish-release"}, writers(self.release))
        self.assertEqual({"move-tag"}, writers(self.move))
        self.assertEqual({"contents": "write"}, self.move["jobs"]["move-tag"]["permissions"])

    def test_tag_moves_are_serialised_on_the_called_job(self):
        job = self.move["jobs"]["move-tag"]
        self.assertEqual({"group": "move-floating-tag", "cancel-in-progress": False}, job["concurrency"])
        self.assertNotIn("concurrency", self.move)
        # The release run must not hold the group its own called job waits for.
        # Group names are case-insensitive.
        self.assertNotIn("move-floating-tag", str(self.release["concurrency"]).lower())

    def test_called_job_reads_both_inputs_and_passes_them_through_env(self):
        env = self.move["jobs"]["move-tag"]["env"]
        self.assertEqual({"EV_TAG": "${{ inputs.tag }}", "EXPECTED_SHA": "${{ inputs.expected_sha }}"}, env)
        for step in self.move["jobs"]["move-tag"]["steps"]:
            self.assertNotIn("${{", step.get("run", ""), "context values go through env:")

    def test_called_workflow_shape_is_fixed(self):
        # The script tests run each step's `run:` text with their own environment and in
        # their own order, so everything else about the job is pinned here: one job, its
        # keys, the three steps in order and the keys of each. A step cannot gain an `if`,
        # a `continue-on-error`, its own `env` (which would override EXPECTED_SHA) or a
        # neighbour without failing this test.
        for key in ("defaults", "env"):
            self.assertNotIn(key, self.move)
        self.assertEqual({"move-tag"}, set(self.move["jobs"]))
        job = self.move["jobs"]["move-tag"]
        self.assertEqual({"name", "runs-on", "if", "permissions", "concurrency", "env", "steps"}, set(job))
        steps = job["steps"]
        self.assertEqual(["Checkout at the published tag", "Refuse a missing or draft release",
                          "Decide the target floating tag and move it"], [step.get("name") for step in steps])
        self.assertEqual({"name", "uses", "with"}, set(steps[0]))
        self.assertEqual({"ref", "fetch-depth"}, set(steps[0]["with"]))
        self.assertEqual({"name", "env", "run"}, set(steps[1]))
        self.assertEqual({"GH_TOKEN": "${{ github.token }}"}, steps[1]["env"])
        self.assertEqual({"name", "run"}, set(steps[2]))
        for step in steps[1:]:
            with self.subTest(step=step["name"]):
                self.assertTrue(step["run"].startswith("set -euo pipefail\n"))

    def test_called_job_is_pinned_to_the_canonical_repository(self):
        self.assertEqual("github.repository == 'tomas-rampas/vouchfx'", self.move["jobs"]["move-tag"]["if"])
        self.assertNotIn("continue-on-error", self.move["jobs"]["move-tag"])

    def test_no_step_of_the_called_job_is_conditional_or_allowed_to_fail(self):
        # The script tests run `run:` directly, so step-level keys are checked here.
        for step in self.move["jobs"]["move-tag"]["steps"]:
            with self.subTest(step=step["name"]):
                self.assertNotIn("if", step)
                self.assertNotIn("continue-on-error", step)

    def test_checkout_is_pinned_to_the_full_tag_ref(self):
        checkout = self.move["jobs"]["move-tag"]["steps"][0]
        self.assertRegex(checkout["uses"], r"^actions/checkout@[0-9a-f]{40}$")
        # A short name would let a branch of the same name win over the tag.
        self.assertEqual("refs/tags/${{ env.EV_TAG }}", checkout["with"]["ref"])

    def test_decide_step_reads_tags_with_git_only(self):
        step = next(s for s in self.move["jobs"]["move-tag"]["steps"]
                    if s["name"] == "Decide the target floating tag and move it")
        self.assertIsNone(re.search(r"(?<![\w-])gh(?![\w-])", step["run"]), "no gh in the decide step")
        self.assertNotIn("jq", step["run"])
        self.assertNotIn("GH_TOKEN", step.get("env", {}))
        self.assertIn("git ls-remote --tags origin", step["run"])


class MoveFloatingTagScriptTests(unittest.TestCase):
    """Runs the actual step scripts with fixture `git` and `gh`; nothing touches the network.

    The fixture `gh` serves only the release check. The fixture `git` answers
    `ls-remote --tags origin` from a per-test listing. Step-level YAML (`if`,
    `continue-on-error`, the checkout `ref`) is not run here: the structure tests cover it.
    """

    # GitHub's `GET /releases/tags/{tag}` returns published releases only, so a
    # draft (state "draft") and a missing release both give a 404 (exit 1).
    # State "draft-visible" is not what GitHub does: it exists only to reach the
    # script's own draft branch, kept as a second line of defence.
    GH = """#!/usr/bin/env bash
echo "gh $*" >> "$FIXTURE_DIR/calls"
case "$*" in
  *"/releases/tags/"*)
    all="$*"; tag="${all#*/releases/tags/}"; tag="${tag%% *}"
    [[ -f "$FIXTURE_DIR/state-$tag" ]] || exit 1
    case "$(cat "$FIXTURE_DIR/state-$tag")" in
      published) echo false ;;
      draft-visible) echo true ;;
      *) exit 1 ;;
    esac ;;
  *) exit 98 ;;
esac
"""
    GIT = """#!/usr/bin/env bash
echo "git $*" >> "$FIXTURE_DIR/calls"
case "$1" in
  rev-parse) echo "$FIXTURE_HEAD" ;;
  ls-remote)
    [[ "$*" == "ls-remote --tags origin" ]] || exit 97
    [[ -f "$FIXTURE_DIR/ls-fails" ]] && exit 128
    cat "$FIXTURE_DIR/tags" ;;
  push) ;;
  *) exit 99 ;;
esac
"""

    @classmethod
    def setUpClass(cls):
        steps = workflow(MOVE_WORKFLOW)["jobs"]["move-tag"]["steps"]
        cls.check = next(s for s in steps if s["name"] == "Refuse a missing or draft release")
        cls.decide = next(s for s in steps if s["name"] == "Decide the target floating tag and move it")

    def run_job(self, tag, expected_sha, tags=(), head=EXPECTED_SHA, ls_fails=False, state="published"):
        """Runs the two steps in order, as the job does. Returns (exit, output, calls).

        `tags` is the remote listing as (name, commit) pairs; a name ending in `^{}` is a
        peeled entry. `state` is the release state the fixture `gh` reports for `tag`.
        """
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            binary = root / "bin"
            binary.mkdir()
            for name, text in (("gh", self.GH), ("git", self.GIT)):
                (binary / name).write_text(text, encoding="utf-8", newline="\n")
                (binary / name).chmod(0o755)
            (root / "tags").write_text("".join(f"{sha}\trefs/tags/{name}\n" for name, sha in tags),
                                       encoding="utf-8", newline="\n")
            if state is not None:
                (root / f"state-{tag}").write_text(state, encoding="utf-8")
            if ls_fails:
                (root / "ls-fails").write_text("", encoding="utf-8")
            env = os.environ.copy()
            env.update(FIXTURE_DIR=root.as_posix(), FIXTURE_BIN=binary.as_posix(), FIXTURE_HEAD=head,
                       EV_TAG=tag, EXPECTED_SHA=expected_sha, GH_TOKEN="fixture",
                       GITHUB_REPOSITORY="tomas-rampas/vouchfx")
            output = b""
            for step in (self.check, self.decide):
                result = subprocess.run(
                    [BASH, "--noprofile", "--norc", "-c", 'export PATH="$FIXTURE_BIN:$PATH"\n' + step["run"]],
                    cwd=scratch, env=env, capture_output=True, timeout=20)
                output += result.stdout + result.stderr
                if result.returncode:
                    break
            calls = (root / "calls").read_text(encoding="utf-8") if (root / "calls").exists() else ""
            return result.returncode, output.decode(errors="replace"), calls

    def assertMoved(self, calls, target, sha=EXPECTED_SHA):
        self.assertIn(f"git push --force origin {sha}:refs/tags/{target}\n", calls)

    def assertNotMoved(self, calls):
        self.assertNotIn("git push", calls)

    def assertRefused(self, result, text="::error::"):
        code, output, calls = result
        self.assertNotEqual(0, code, output)
        self.assertIn(text, output)
        self.assertNotMoved(calls)

    def assertLeftAlone(self, result, *named):
        code, output, calls = result
        self.assertEqual(0, code, output)
        self.assertIn("::warning::", output)
        for name in named:
            self.assertIn(name, output)
        self.assertNotMoved(calls)

    def assertMovedOk(self, result, target, sha=EXPECTED_SHA):
        code, output, calls = result
        self.assertEqual(0, code, output)
        self.assertNotIn("::warning::", output)
        self.assertMoved(calls, target, sha)

    def test_called_path_refuses_a_tag_that_resolves_to_another_commit(self):
        self.assertRefused(self.run_job("v1.4.9", EXPECTED_SHA, head=OTHER_SHA))

    def test_called_path_moves_when_the_floating_tag_is_absent_on_the_remote(self):
        tags = [("v1.4.9", EXPECTED_SHA), ("v1.4.8", SHA_B), ("v1-rc", SHA_C)]
        self.assertMovedOk(self.run_job("v1.4.9", EXPECTED_SHA, tags), "v1")
        self.assertMovedOk(self.run_job("v1.4.9", EXPECTED_SHA, []), "v1")

    def test_called_path_moves_when_the_floating_tag_is_on_an_older_release(self):
        tags = [("v1", SHA_B), ("v1.4.8", SHA_B), ("v1.4.9", EXPECTED_SHA), ("v1.5.0-rc.1", SHA_C)]
        self.assertMovedOk(self.run_job("v1.4.9", EXPECTED_SHA, tags), "v1")

    def test_called_path_moves_when_the_floating_tag_is_on_the_same_release(self):
        tags = [("v1", EXPECTED_SHA), ("v1.4.9", EXPECTED_SHA)]
        self.assertMovedOk(self.run_job("v1.4.9", EXPECTED_SHA, tags), "v1")

    def test_called_path_leaves_the_tag_when_it_is_on_a_newer_release(self):
        tags = [("v1", SHA_B), ("v1.4.10", SHA_B), ("v1.4.9", EXPECTED_SHA)]
        self.assertLeftAlone(self.run_job("v1.4.9", EXPECTED_SHA, tags), "'v1'", "v1.4.10")

    def test_called_path_two_releases_case_moves_the_older_run_when_the_tag_is_older_still(self):
        # v1 is on v1.4.8. Both v1.4.9 and v1.4.10 exist; the run is for v1.4.9.
        tags = [("v1", SHA_B), ("v1.4.8", SHA_B), ("v1.4.9", EXPECTED_SHA), ("v1.4.10", SHA_C)]
        self.assertMovedOk(self.run_job("v1.4.9", EXPECTED_SHA, tags), "v1")

    def test_called_path_uses_the_highest_version_tag_on_the_floating_commit(self):
        tags = [("v1", SHA_B), ("v1.4.2", SHA_B), ("v1.4.10", SHA_B), ("v1.4.9", SHA_B)]
        self.assertLeftAlone(self.run_job("v1.4.9", EXPECTED_SHA, tags), "v1.4.10")

    def test_called_path_compares_versions_numerically_and_ignores_build_metadata(self):
        # release.yml's own version check refuses a `+build` release tag, so only a
        # `+build` tag in the listing can reach this path from a release run.
        for tag, on_floating, moves in (
                ("v1.4.9", "v1.4.10", False), ("v1.4.10", "v1.4.9", True),
                ("v1.9.0", "v1.10.0", False), ("v1.10.0", "v1.9.0", True),
                ("v1.4.9", "v1.4.9+build.7", True), ("v1.4.9+build.1", "v1.4.10+build.2", False),
                ("v1.4.9+build.1", "v1.4.9+build.2", True)):
            with self.subTest(tag=tag, on_floating=on_floating):
                result = self.run_job(tag, EXPECTED_SHA, [("v1", SHA_B), (on_floating, SHA_B), (tag, EXPECTED_SHA)])
                if moves:
                    self.assertMovedOk(result, "v1")
                else:
                    self.assertLeftAlone(result)

    def test_called_path_reads_the_peeled_commit_of_annotated_tags(self):
        # An annotated tag is listed as its tag object, then as the peeled `^{}` commit.
        for label, tags, moves in (
                ("both annotated, newer on floating",
                 [("v1", SHA_X), ("v1^{}", SHA_B), ("v1.4.10", SHA_Y), ("v1.4.10^{}", SHA_B)], False),
                ("floating annotated, version lightweight, newer on floating",
                 [("v1", SHA_X), ("v1^{}", SHA_B), ("v1.4.10", SHA_B)], False),
                ("floating lightweight, version annotated, newer on floating",
                 [("v1", SHA_B), ("v1.4.10", SHA_Y), ("v1.4.10^{}", SHA_B)], False),
                ("both annotated, older on floating",
                 [("v1", SHA_X), ("v1^{}", SHA_B), ("v1.4.8", SHA_Y), ("v1.4.8^{}", SHA_B)], True)):
            with self.subTest(case=label):
                result = self.run_job("v1.4.9", EXPECTED_SHA, [*tags, ("v1.4.9", EXPECTED_SHA)])
                if moves:
                    self.assertMovedOk(result, "v1")
                else:
                    self.assertLeftAlone(result, "v1.4.10")

    def test_called_path_refuses_when_the_floating_tag_is_on_no_release_of_its_line(self):
        for label, tags in (
                ("no tag on the commit", [("v1", SHA_B)]),
                ("only a tag of another line", [("v1", SHA_B), ("v1.0.0-rc.1", SHA_B)]),
                ("only a post-GA pre-release", [("v1", SHA_B), ("v1.5.0-rc.1", SHA_B)]),
                ("version tags are elsewhere", [("v1", SHA_B), ("v1.4.8", SHA_C)]),
                # awk would compare these as numbers, and find them equal, without the
                # string comparison in the script.
                ("all-digit commits that agree in their first 20 digits",
                 [("v1", "1234567890" * 4), ("v1.4.10", "1234567890" * 2 + "9" * 20)])):
            with self.subTest(case=label):
                result = self.run_job("v1.4.9", EXPECTED_SHA, [*tags, ("v1.4.9", EXPECTED_SHA)])
                self.assertRefused(result, "cannot tell which release")
                self.assertIn("workflow_dispatch", result[1])

    def test_called_path_fails_closed_when_the_remote_tags_cannot_be_read(self):
        result = self.run_job("v1.4.9", EXPECTED_SHA, [("v1", SHA_B), ("v1.4.10", SHA_B)], ls_fails=True)
        self.assertRefused(result, "Could not read the remote tags")

    def test_called_path_never_reads_the_release_list(self):
        _, _, calls = self.run_job("v1.4.9", EXPECTED_SHA, [("v1", SHA_B), ("v1.4.8", SHA_B)])
        self.assertIn("git ls-remote --tags origin\n", calls)
        self.assertNotIn("releases?", calls)
        self.assertEqual(1, calls.count("gh "), "gh serves only the release check")

    def test_pre_ga_lines_each_use_their_own_pattern(self):
        for label, tag, tags, target, moves in (
                ("rc: newer rc on floating", "v1.0.0-rc.9", [("v1-rc", SHA_B), ("v1.0.0-rc.10", SHA_B)], "v1-rc", False),
                ("rc: older rc on floating", "v1.0.0-rc.10", [("v1-rc", SHA_B), ("v1.0.0-rc.9", SHA_B)], "v1-rc", True),
                ("rc: a GA tag on the commit is not of the rc line",
                 "v1.0.0-rc.3", [("v1-rc", SHA_B), ("v1.0.0-rc.2", SHA_B), ("v1.9.0", SHA_B)], "v1-rc", True),
                ("alpha: beta on floating is newer than alpha.4",
                 "v1.0.0-alpha.4", [("v1-alpha", SHA_B), ("v1.0.0-beta.1", SHA_B)], "v1-alpha", False),
                ("alpha: beta moves over alpha",
                 "v1.0.0-beta.1", [("v1-alpha", SHA_B), ("v1.0.0-alpha.4", SHA_B)], "v1-alpha", True),
                ("alpha: an rc tag on the commit is not of the alpha line",
                 "v1.0.0-alpha.5", [("v1-alpha", SHA_B), ("v1.0.0-alpha.4", SHA_B), ("v1.0.0-rc.1", SHA_B)],
                 "v1-alpha", True)):
            with self.subTest(case=label):
                result = self.run_job(tag, EXPECTED_SHA, [*tags, (tag, EXPECTED_SHA)])
                if moves:
                    self.assertMovedOk(result, target)
                else:
                    self.assertLeftAlone(result, f"'{target}'")
        # An rc tag alone does not place a floating alpha tag, and the reverse.
        self.assertRefused(self.run_job("v1.0.0-alpha.5", EXPECTED_SHA,
                                        [("v1-alpha", SHA_B), ("v1.0.0-rc.1", SHA_B)]), "cannot tell which release")
        self.assertRefused(self.run_job("v1.0.0-rc.3", EXPECTED_SHA,
                                        [("v1-rc", SHA_B), ("v1.0.0-alpha.1", SHA_B)]), "cannot tell which release")

    def test_called_path_refuses_a_malformed_expected_sha(self):
        tags = [("v1.4.9", EXPECTED_SHA)]
        for bad in ("abc", "a" * 39, "a" * 41, "A" * 40, " " + "a" * 40, "a" * 40 + " ", "a" * 39 + "g", "a" * 40 + "\n"):
            with self.subTest(expected_sha=bad):
                code, output, calls = self.run_job("v1.4.9", bad, tags)
                self.assertNotEqual(0, code, output)
                self.assertIn("::error::expected_sha must be 40 lower-case hex", output)
                self.assertNotMoved(calls)
                self.assertNotIn("ls-remote", calls)

    def test_manual_path_may_move_a_tag_backwards_and_never_reads_the_tags(self):
        tags = [("v1", SHA_B), ("v1.4.10", SHA_B), ("v1.4.8", OTHER_SHA)]
        for ls_fails in (False, True):
            with self.subTest(ls_fails=ls_fails):
                code, output, calls = self.run_job("v1.4.8", "", tags, head=OTHER_SHA, ls_fails=ls_fails)
                self.assertEqual(0, code, output)
                self.assertMoved(calls, "v1", sha=OTHER_SHA)
                self.assertNotIn("ls-remote", calls)

    def test_draft_release_is_refused_on_both_paths(self):
        # On GitHub a draft is not returned by `releases/tags/{tag}`: it gives a 404.
        for expected in (EXPECTED_SHA, ""):
            with self.subTest(expected_sha=expected):
                self.assertRefused(self.run_job("v1.4.9", expected, state="draft"), "No release found")

    def test_missing_release_is_refused_on_both_paths(self):
        for expected in (EXPECTED_SHA, ""):
            with self.subTest(expected_sha=expected):
                self.assertRefused(self.run_job("v1.4.9", expected, state=None), "No release found")

    def test_script_draft_branch_is_a_second_line_of_defence(self):
        # Not GitHub's behaviour (it answers 404 for a draft): the fixture reports a
        # visible draft only to reach the script's own check.
        for expected in (EXPECTED_SHA, ""):
            with self.subTest(expected_sha=expected):
                self.assertRefused(self.run_job("v1.4.9", expected, state="draft-visible"), "DRAFT")

    def test_post_ga_pre_release_moves_nothing_on_both_paths(self):
        for expected in (EXPECTED_SHA, ""):
            with self.subTest(expected_sha=expected):
                code, output, calls = self.run_job("v1.5.0-rc.1", expected, [("v1.5.0-rc.1", EXPECTED_SHA)])
                self.assertEqual(0, code, output)
                self.assertNotMoved(calls)

    def test_routing_is_unchanged(self):
        for tag, target in (("v1.0.0-alpha.4", "v1-alpha"), ("v1.0.0-beta.2", "v1-alpha"),
                            ("v1.0.0-rc.1", "v1-rc"), ("v1.0.0", "v1"), ("v1.2.3+build.7", "v1"),
                            # A hyphen inside build metadata must not read as a pre-release.
                            ("v1.2.3+build-7", "v1")):
            with self.subTest(tag=tag):
                code, output, calls = self.run_job(tag, EXPECTED_SHA, [(tag, EXPECTED_SHA)])
                self.assertEqual(0, code, output)
                self.assertMoved(calls, target)
        for tag in ("v2.0.0", "v1.0.0-preview.1"):
            with self.subTest(tag=tag):
                code, output, calls = self.run_job(tag, EXPECTED_SHA, [(tag, EXPECTED_SHA)])
                self.assertEqual(0, code, output)
                self.assertNotMoved(calls)
        self.assertRefused(self.run_job("v1.2.3.rc.1", EXPECTED_SHA, [("v1.2.3.rc.1", EXPECTED_SHA)]))


if __name__ == "__main__":
    unittest.main()
