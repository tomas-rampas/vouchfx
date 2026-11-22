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


if __name__ == "__main__":
    unittest.main()
