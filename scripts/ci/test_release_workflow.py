"""Release workflow contract and its actual step scripts, run with fixtures; no publication."""

import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest

from docker_ci_support import BASH, ROOT, workflow


GATE = "docker-integration"
PREPARE = "prepare"
REPOSITORY = "tomas-rampas/vouchfx"
MODE_PUBLISH = "inputs.mode == 'publish'"
# The exact job-level conditions of the publishing jobs. None uses a status-check
# function, so a failed, cancelled or skipped dependency skips the job.
PUBLISH_RELEASE_IF = ("inputs.mode != 'validate' && github.ref == 'refs/heads/main' && "
                      "github.repository == 'tomas-rampas/vouchfx'")
PUBLISH_MARKETPLACE_IF = "inputs.mode == 'publish' && github.repository == 'tomas-rampas/vouchfx'"
MOVE_TAG_IF = "inputs.mode == 'publish'"
# The one step-level status function in publish-release: the rehearsal clean-up.
REHEARSAL_CLEANUP_IF = "always() && inputs.mode == 'rehearse' && steps.state.outputs.state == 'none'"
CONCURRENCY_GROUP = ("${{ (inputs.mode == 'rehearse' || inputs.mode == 'publish') && "
                     "github.ref == 'refs/heads/main' && 'release-publish' || "
                     "format('release-run-{0}', github.run_id) }}")
# A `gh api` call that writes: an explicit method, any field or input (with which
# `gh api` sends a POST by default), or any GraphQL call (a mutation cannot be told
# from a query here).
GH_API_WRITE = (r"gh\s+api\b[^\n]*(?:-X|--method)[\s=]*(?:POST|PATCH|PUT|DELETE)|"
                r"gh\s+api\b[^\n]*\s(?:-f|-F|--field|--raw-field|--input)(?=[\s=]|$)|"
                r"gh\s+api\b[^\n]*\bgraphql\b")
# Commands that create, change or delete a Release, log in to or push to NuGet.org.
RELEASE_COMMANDS = re.compile(
    r"gh\s+release\s+(?:create|upload|edit|delete)|" + GH_API_WRITE + r"|nuget\s+push",
    re.IGNORECASE | re.MULTILINE)
# publish-release, step by step: (name, keys, step-level env keys). The behaviour
# tests run each `run:` with their own environment and evaluate only these exact
# `if` strings, so everything else about a step is pinned here.
GH_ENV = {"GH_TOKEN": "${{ github.token }}", "GH_REPO": "${{ github.repository }}"}
GH_STATE_ENV = {**GH_ENV, "STATE": "${{ steps.state.outputs.state }}"}
PUBLISH_RELEASE_STEPS = [
    ("Checkout the run's commit", {"name", "uses", "with"}, None),
    ("Test the release helper scripts", {"name", "run"}, None),
    ("Classify the release state", {"name", "id", "env", "run"}, GH_ENV),
    ("Download nupkg", {"name", "uses", "with"}, None),
    ("Download publish archives", {"name", "uses", "with"}, None),
    ("Download SBOM", {"name", "uses", "with"}, None),
    ("Download .vsix", {"name", "uses", "with"}, None),
    ("Download signing material (cosign bundles, GPG signatures)", {"name", "uses", "with"}, None),
    ("Download MSI installer", {"name", "uses", "with"}, None),
    ("Download .deb package", {"name", "uses", "with"}, None),
    ("Download .pkg installer", {"name", "uses", "with"}, None),
    ("List release artifacts", {"name", "run"}, None),
    ("Create the draft release and upload the assets", {"name", "id", "env", "run"},
     {**GH_STATE_ENV, "DRAFT_ID": "${{ steps.state.outputs.draft_id }}"}),
    ("NuGet login (OIDC → short-lived API key)", {"name", "id", "uses", "with"}, None),
    ("Rehearsal — delete the draft release", {"name", "if", "env", "run"},
     {**GH_ENV, "CREATED": "${{ steps.create.outputs.created }}"}),
    ("Publish nupkg to NuGet.org — Trusted Publishing", {"name", "if", "env", "run"},
     {"NUGET_API_KEY": "${{ steps.nuget-login.outputs.NUGET_API_KEY }}"}),
    ("Publish the draft release", {"name", "if", "env", "run"}, GH_STATE_ENV),
    ("Verify the tag and the published release", {"name", "if", "env", "run"}, GH_ENV),
]
CREATE_STEP, CLEANUP_STEP, PUSH_STEP, PUBLISH_STEP, VERIFY_STEP = (
    PUBLISH_RELEASE_STEPS[12][0], PUBLISH_RELEASE_STEPS[14][0], PUBLISH_RELEASE_STEPS[15][0],
    PUBLISH_RELEASE_STEPS[16][0], PUBLISH_RELEASE_STEPS[17][0])
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
    r"cosign\s+sign|gh\s+release\s+(?:create|upload|edit|delete)|nuget\s+push|" + GH_API_WRITE + "|"
    r"vsce\s+publish|notarytool|signtool|--detach-sign|gh\s+attestation|"
    r"\bovsx\b|npm\s+publish|docker\s+push|gpg\b.*--(?:clear)?sign\b", re.IGNORECASE | re.MULTILINE)
# The exact `gh` command forms (see `gh_forms`) each job may run. A new call, or a
# changed filter, method or field on an existing one, fails the test.
LIST_RELEASES = "gh api --paginate repos/${GH_REPO}/releases?per_page=100 --jq "
PUBLISH_RELEASE_GH_FORMS = {
    LIST_RELEASES + ".[] | [.tag_name, (.draft | tostring), (.id | tostring), .target_commitish] | @tsv",
    LIST_RELEASES + ".[] | [.tag_name, (.draft | tostring), (.id | tostring)] | @tsv",
    "gh api repos/${GH_REPO}/releases/${DRAFT_ID} --jq [.tag_name, (.draft | tostring), .target_commitish] | @tsv",
    "gh api -X DELETE repos/${GH_REPO}/releases/${DRAFT_ID}",
    "gh api -X DELETE repos/${GH_REPO}/releases/${id}",
    "gh api repos/${GH_REPO}/releases/tags/${TAG} --jq .assets[].name",
    "gh api repos/${GH_REPO}/releases/tags/${TAG} --jq .draft",
    "gh release create $TAG --draft --target $GITHUB_SHA --title $TAG --notes-file $notes --generate-notes "
    "${prerelease[@]}",
    "gh release upload $TAG ${files[@]} --clobber",
    "gh release edit $TAG --draft=false --target $GITHUB_SHA ${latest[@]}",
}
PREPARE_GH_FORMS = {
    "gh api --paginate repos/${GITHUB_REPOSITORY}/releases?per_page=100 --jq .[] | select(.draft == false) | .tag_name",
}
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
    is therefore not recognised. Nor is a `curl` (or any other HTTP client) that uses
    the job token, `GH_TOKEN` or `GITHUB_TOKEN`: only `gh` calls are matched. A `gh
    api` call counts as a write when it names a method, passes a field or an input
    (`-f`, `-F`, `--field`, `--raw-field`, `--input`), or is a `graphql` call. A job that calls a reusable workflow is judged on
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


def gh_forms(job):
    """The `gh` commands in a job's `run:` texts, each as one normalised string.

    Comment lines are dropped and backslash-continued lines joined. Each command is
    read with shell quoting rules from `gh` up to the first shell operator (`)`, `|`,
    `;`, `>`, `&` and the like), and its words are joined with single spaces, so a
    quoted jq filter stays one word.
    """
    forms = set()
    for step in job.get("steps", []):
        text = step.get("run", "").replace("\\\n", " ")
        for line in text.splitlines():
            if line.lstrip().startswith("#"):
                continue
            # A `gh` after a slash counts too (`/usr/bin/gh`): it must not hide a call.
            for match in re.finditer(r"(?<![\w.$-])gh\s", line):
                lexer = shlex.shlex(line[match.start():], posix=True, punctuation_chars=True)
                words = []
                while True:
                    try:
                        word = lexer.get_token()
                    except ValueError:  # an unclosed quote after the command
                        break
                    if word is None or (word and set(word) <= set("();<>|&")):
                        break
                    words.append(word)
                forms.add(" ".join(words))
    return forms


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

    def test_manual_dispatch_is_the_only_trigger_with_three_modes(self):
        self.assertEqual(["workflow_dispatch"], list(self.data["on"]))
        inputs = self.data["on"]["workflow_dispatch"]["inputs"]
        self.assertEqual({"mode", "version", "commit"}, set(inputs))
        self.assertEqual({"description", "required", "default", "type", "options"}, set(inputs["mode"]))
        self.assertEqual("choice", inputs["mode"]["type"])
        self.assertEqual(["validate", "rehearse", "publish"], inputs["mode"]["options"])
        self.assertEqual("validate", inputs["mode"]["default"])
        self.assertIs(True, inputs["version"]["required"])
        self.assertEqual("string", inputs["version"]["type"])
        self.assertEqual("0.0.0-test", inputs["version"]["default"])
        self.assertIs(False, inputs["commit"]["required"])
        self.assertEqual(("string", ""), (inputs["commit"]["type"], inputs["commit"]["default"]))

    def test_concurrency_serialises_rehearse_and_publish_runs_on_main_only(self):
        self.assertEqual({"group": CONCURRENCY_GROUP, "cancel-in-progress": False}, self.data["concurrency"])
        # No job of this workflow, and no other workflow, can join the shared group
        # and so take the place of a pending release run.
        for name, job in self.jobs.items():
            with self.subTest(job=name):
                self.assertNotIn("concurrency", job)
        for path in workflow_files():
            if path.name != "release.yml":
                with self.subTest(workflow=path.name):
                    self.assertNotIn("release-publish", path.read_text(encoding="utf-8").lower())

    def test_gh_api_writes_are_recognised_in_every_spelling(self):
        for command in ('gh api -X DELETE "repos/o/r/releases/1"', 'gh api --method=DELETE "repos/o/r/releases/1"',
                        'gh api --method PATCH x', "gh api -XPOST x", 'gh api "repos/o/r/releases" -f tag_name="$TAG"',
                        "gh api repos/o/r/releases -F draft=false", "gh api x --field a=b", "gh api x --raw-field a=b",
                        "gh api x --input body.json", "gh api x --input=body.json",
                        "gh api graphql -f query='mutation { x }'", "gh api graphql --paginate"):
            with self.subTest(command=command):
                self.assertIsNotNone(RELEASE_COMMANDS.search(command))
                self.assertIsNotNone(SIDE_EFFECT_COMMANDS.search(command))
        for command in ("gh api --paginate repos/o/r/releases?per_page=100 --jq '.[] | .tag_name'",
                        "gh api repos/o/r/releases/tags/v1 --jq .draft"):
            with self.subTest(command=command):
                self.assertIsNone(RELEASE_COMMANDS.search(command))
                self.assertIsNone(SIDE_EFFECT_COMMANDS.search(command))

    def test_each_job_runs_exactly_the_expected_gh_commands(self):
        self.assertEqual(PUBLISH_RELEASE_GH_FORMS, gh_forms(self.jobs["publish-release"]))
        self.assertEqual(PREPARE_GH_FORMS, gh_forms(self.jobs[PREPARE]))
        for name, job in self.jobs.items():
            if name not in ("publish-release", PREPARE):
                with self.subTest(job=name):
                    self.assertEqual(set(), gh_forms(job))

    def test_both_nuget_checks_cover_every_package_id(self):
        loops = [re.findall(r"^\s*for \w+ in ([^;\n]+); do$", step.get("run", ""), re.MULTILINE)
                 for job in (PREPARE, "publish-release") for step in self.jobs[job]["steps"]
                 if "check_nuget_presence.py --id" in step.get("run", "")]
        self.assertEqual([[" ".join(PACKAGE_IDS)], [" ".join(PACKAGE_IDS)]], loops)

    def test_signing_waits_for_the_gate_the_builds_and_both_smoke_tests(self):
        job = self.jobs["sign-and-attest"]
        self.assertEqual([GATE, "build-dotnet", "build-vsix", "build-msi", "build-deb", "build-pkg",
                          "smoke-test-packaged-tool", "smoke-test-distribution-platforms"], job["needs"])
        # The job runs in every mode; only its signing steps are limited to publish.
        for key in ("if", "environment", "continue-on-error"):
            self.assertNotIn(key, job)

    def test_marketplace_waits_for_signing_both_smoke_tests_and_the_release(self):
        self.assertEqual(["build-dotnet", "build-vsix", "sign-and-attest", "smoke-test-packaged-tool",
                          "smoke-test-distribution-platforms", "publish-release"],
                         self.jobs["publish-marketplace"]["needs"])

    def test_publishing_jobs_have_exact_conditions_without_status_functions(self):
        for name, expected in (("publish-release", PUBLISH_RELEASE_IF),
                               ("publish-marketplace", PUBLISH_MARKETPLACE_IF),
                               ("move-floating-tag", MOVE_TAG_IF)):
            with self.subTest(job=name):
                self.assertEqual(expected, self.jobs[name]["if"])
                self.assertIsNone(DEFEATS_NEEDS.search(self.jobs[name]["if"]))

    def test_release_environment_is_on_publish_release_only(self):
        self.assertEqual("release", self.jobs["publish-release"]["environment"])
        self.assertEqual({"contents": "write", "id-token": "write"}, self.jobs["publish-release"]["permissions"])
        others = {name for name, job in self.jobs.items() if name != "publish-release" and "environment" in job}
        self.assertEqual(set(), others)

    def test_prepare_is_the_read_only_root_of_every_other_job(self):
        job = self.jobs[PREPARE]
        for key in ("needs", "if", "environment", "secrets", "continue-on-error", "strategy"):
            self.assertNotIn(key, job)
        self.assertEqual({"contents": "read"}, job["permissions"])
        self.assertEqual(set(), side_effects(job))
        self.assertEqual({"version": "${{ steps.request.outputs.version }}",
                          "tag": "${{ steps.request.outputs.tag }}",
                          "mode": "${{ steps.request.outputs.mode }}"}, job["outputs"])
        for name in self.jobs:
            if name != PREPARE:
                with self.subTest(job=name):
                    self.assertIn(PREPARE, ancestors(self.jobs, name))
        for name in ("tls-transport", GATE):
            with self.subTest(job=name):
                self.assertEqual(PREPARE, self.jobs[name]["needs"])
                self.assertNotIn("if", self.jobs[name])

    def test_version_and_tag_come_from_prepare(self):
        build = self.jobs["build-dotnet"]
        self.assertIn(PREPARE, needs_of(build))
        self.assertEqual({"version": "${{ needs.prepare.outputs.version }}",
                          "tag": "${{ needs.prepare.outputs.tag }}"}, build["outputs"])
        self.assertNotIn("Determine release version", [s.get("name") for s in build["steps"]])
        self.assertEqual("${{ needs.prepare.outputs.version }}", self.jobs["publish-release"]["env"]["VERSION"])
        self.assertEqual("${{ needs.prepare.outputs.tag }}", self.jobs["publish-release"]["env"]["TAG"])
        self.assertEqual("${{ inputs.mode }}", self.jobs["publish-release"]["env"]["MODE"])
        self.assertNotIn("github.event", json.dumps(self.data))

    def test_attestation_and_signing_steps_run_in_publish_mode_only(self):
        steps = self.jobs["sign-and-attest"]["steps"]
        attest = [s for s in steps if "attest-build-provenance" in str(s.get("uses", ""))]
        cosign = [s for s in steps if "cosign-installer" in str(s.get("uses", ""))
                  or re.search(r"cosign\s+sign", s.get("run", ""))]
        gpg = [s for s in steps if "--detach-sign" in s.get("run", "")]
        self.assertEqual(5, len(attest))
        self.assertEqual(2, len(cosign))
        self.assertEqual(1, len(gpg))
        for step in attest + cosign:
            with self.subTest(step=step["name"]):
                self.assertEqual(MODE_PUBLISH, step["if"])
        self.assertEqual("${{ inputs.mode == 'publish' && env.HAS_GPG == 'true' }}", gpg[0]["if"])
        # No other job attests or cosign-signs.
        for name, job in self.jobs.items():
            if name != "sign-and-attest":
                for step in job.get("steps", []):
                    self.assertNotRegex(str(step.get("uses", "")), "attest-build-provenance|cosign-installer")
                    self.assertNotRegex(step.get("run", ""), r"cosign\s+sign|gh\s+attestation")

    def test_signing_material_has_a_known_shape_in_every_mode(self):
        steps = self.jobs["sign-and-attest"]["steps"]
        names = [s.get("name") for s in steps]
        check = steps[names.index("Check the signing material for this mode")]
        upload = steps[names.index("Upload cosign bundles + GPG signatures")]
        self.assertLess(names.index("Sign nupkg (GPG) — secret-gated"), names.index(check["name"]))
        self.assertLess(names.index(check["name"]), names.index(upload["name"]))
        self.assertNotIn("if", check)
        self.assertEqual("${{ inputs.mode }}", self.jobs["sign-and-attest"]["env"]["MODE"])
        self.assertEqual("error", upload["with"]["if-no-files-found"])
        self.assertEqual(["artifacts/signing-mode", "artifacts/**/*.cosign.bundle", "artifacts/nuget/*.asc"],
                         upload["with"]["path"].split())
        for name in ("sign-and-attest", "publish-release"):
            for step in self.jobs[name]["steps"]:
                with self.subTest(job=name, step=step.get("name")):
                    self.assertNotIn("continue-on-error", step)

    def test_signing_material_check_behaviour(self):
        step = next(s for s in self.jobs["sign-and-attest"]["steps"]
                    if s.get("name") == "Check the signing material for this mode")
        subjects = ["nuget/vouchfx.1.5.0.nupkg", "vouchfx-1.5.0-linux-x64.tar.gz", "installers/x.deb"]
        for label, mode, extra, expected in (
                ("publish, all signed", "publish", [s + ".cosign.bundle" for s in subjects], 0),
                ("publish, one unsigned", "publish", [s + ".cosign.bundle" for s in subjects[1:]], 1),
                ("validate, unsigned", "validate", ["installers/x.deb.asc"], 0),
                ("validate, cosign bundle", "validate", [subjects[0] + ".cosign.bundle"], 1),
                ("rehearse, nupkg signature", "rehearse", ["nuget/vouchfx.1.5.0.nupkg.asc"], 1)):
            with self.subTest(case=label), tempfile.TemporaryDirectory() as scratch:
                for relative in subjects + extra:
                    path = Path(scratch) / "artifacts" / relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text("x", encoding="utf-8")
                result = subprocess.run([BASH, "--noprofile", "--norc", "-c", step["run"]], cwd=scratch,
                                        env=dict(os.environ, MODE=mode), capture_output=True, timeout=10)
                self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
                mode_file = Path(scratch) / "artifacts/signing-mode"
                if expected == 0:
                    self.assertEqual(mode + "\n", mode_file.read_text(encoding="utf-8"))
                else:
                    self.assertFalse(mode_file.exists())

    def test_publication_still_requires_all_acceptance_jobs(self):
        self.assertEqual({PREPARE, GATE, "tls-transport", "build-dotnet", "sign-and-attest",
                          "smoke-test-packaged-tool", "smoke-test-distribution-platforms"},
                         set(self.jobs["publish-release"]["needs"]))
        self.assertIn("publish-release", self.jobs["publish-marketplace"]["needs"])

    def test_only_publish_release_creates_changes_or_deletes_releases_or_touches_nuget(self):
        for name, job in self.jobs.items():
            if name == "publish-release":
                continue
            for step in job.get("steps", []):
                with self.subTest(job=name, step=step.get("name")):
                    self.assertIsNone(RELEASE_COMMANDS.search(step.get("run", "")))
                    self.assertNotIn("nuget/login", str(step.get("uses", "")).lower())
        runs = "\n".join(s.get("run", "") for s in self.jobs["publish-release"]["steps"])
        self.assertEqual(1, len(re.findall(r"gh\s+release\s+create", runs)))
        self.assertEqual(1, len(re.findall(r"gh\s+release\s+edit", runs)))

    def test_release_creation_is_always_a_draft_at_the_run_commit(self):
        steps = {s["name"]: s for s in self.jobs["publish-release"]["steps"]}
        create = steps[CREATE_STEP]["run"]
        # The command with its backslash-continued lines.
        command = re.search(r"gh release create (?:[^\n]*\\\n)*[^\n]*", create).group(0)
        self.assertIn('--draft ', command)
        self.assertIn('--target "$GITHUB_SHA"', command)
        self.assertIn('--notes-file "$notes" --generate-notes', command)
        self.assertNotRegex(command, r"--draft=|--latest|--verify-tag")
        edit = re.search(r"gh release edit [^\n]*", steps[PUBLISH_STEP]["run"]).group(0)
        self.assertIn('--draft=false --target "$GITHUB_SHA"', edit)
        # Every --target anywhere in the workflow is the run's full commit SHA.
        runs = "\n".join(s.get("run", "") for j in self.jobs.values() for s in j.get("steps", []))
        self.assertEqual({'"$GITHUB_SHA"'}, set(re.findall(r"--target[ =](\S+)", runs)))

    def test_publish_release_shape_is_fixed(self):
        # The behaviour tests run each step's `run:` with their own environment, and
        # evaluate only the exact `if` strings below, so the rest is pinned here: the
        # steps in order, the keys of each, and each step's own `env` (which could
        # otherwise override MODE, VERSION or TAG). A step cannot gain an `if`, a
        # `continue-on-error`, an `env` or a neighbour without failing this test.
        job = self.jobs["publish-release"]
        self.assertEqual({"name", "runs-on", "needs", "if", "environment", "permissions", "env", "steps"}, set(job))
        self.assertEqual({"MODE", "VERSION", "TAG"}, set(job["env"]))
        steps = job["steps"]
        self.assertEqual([name for name, _, _ in PUBLISH_RELEASE_STEPS], [s.get("name") for s in steps])
        for (name, keys, env), step in zip(PUBLISH_RELEASE_STEPS, steps):
            with self.subTest(step=name):
                self.assertEqual(keys, set(step))
                self.assertEqual(env, step.get("env"))
                if "run" in step:
                    self.assertTrue(step["run"].startswith(("set -euo pipefail\n", "echo ")), step["run"][:40])
                    self.assertNotIn("${{", step["run"], "context values go through env:")
        by_name = {s["name"]: s for s in steps}
        self.assertEqual({"ref": "${{ github.sha }}", "persist-credentials": False},
                         by_name["Checkout the run's commit"]["with"])
        self.assertEqual("state", by_name["Classify the release state"]["id"])
        self.assertEqual("nuget-login", by_name["NuGet login (OIDC → short-lived API key)"]["id"])
        self.assertEqual("signing-material/",
                         by_name["Download signing material (cosign bundles, GPG signatures)"]["with"]["path"])

    def test_publish_release_step_conditions_and_order(self):
        steps = self.jobs["publish-release"]["steps"]
        conditions = {s["name"]: s.get("if") for s in steps}
        for name in (PUSH_STEP, PUBLISH_STEP, VERIFY_STEP):
            with self.subTest(step=name):
                self.assertEqual(MODE_PUBLISH, conditions[name])
        self.assertEqual(REHEARSAL_CLEANUP_IF, conditions[CLEANUP_STEP])
        # The clean-up is the only step-level status function in the job.
        with_status = [s["name"] for s in steps if DEFEATS_NEEDS.search(str(s.get("if", "")))]
        self.assertEqual([CLEANUP_STEP], with_status)
        names = [s["name"] for s in steps]
        order = [names.index(n) for n in ("Classify the release state", CREATE_STEP,
                                          "NuGet login (OIDC → short-lived API key)", CLEANUP_STEP,
                                          PUSH_STEP, PUBLISH_STEP, VERIFY_STEP)]
        self.assertEqual(sorted(order), order)
        self.assertEqual(len(steps) - 1, names.index(VERIFY_STEP))

    def test_no_step_makes_a_tag_with_git(self):
        runs = "\n".join(s.get("run", "") for j in self.jobs.values() for s in j.get("steps", []))
        self.assertIsNone(re.search(r"\bgit\s+(?:push|tag)\b", runs))

    def test_docker_gate_is_an_unconditional_call_of_the_lane_at_the_run_commit(self):
        gate = self.jobs[GATE]
        self.assertEqual({"name", "needs", "permissions", "uses", "with"}, set(gate))
        self.assertEqual("Docker integration", gate["name"])
        self.assertEqual("./.github/workflows/docker-integration.yml", gate["uses"])
        self.assertEqual({"reviewed_sha": "${{ github.sha }}"}, gate["with"])
        self.assertEqual({"contents": "read"}, gate["permissions"])
        self.assertEqual(PREPARE, gate["needs"])
        # No `if`, `secrets` or `continue-on-error`: the gate runs in every mode once the
        # request is checked, and a failure or skip of the lane must be visible downstream.
        for key in ("if", "secrets", "continue-on-error", "strategy"):
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

    def test_prepare_shape_is_fixed(self):
        steps = self.jobs[PREPARE]["steps"]
        self.assertEqual(["Checkout (release metadata and helper scripts)", "Test the release helper scripts",
                          "Check the version, ref, commit and what already exists"],
                         [s.get("name") for s in steps])
        self.assertEqual({"ref": "${{ github.sha }}", "persist-credentials": False}, steps[0]["with"])
        request = steps[2]
        self.assertEqual({"name", "id", "env", "run"}, set(request))
        self.assertEqual("request", request["id"])
        self.assertEqual({"MODE": "${{ inputs.mode }}", "VERSION": "${{ inputs.version }}",
                          "COMMIT": "${{ inputs.commit }}", "GH_TOKEN": "${{ github.token }}"}, request["env"])
        for step in steps[1:]:
            with self.subTest(step=step["name"]):
                self.assertNotIn("if", step)
                self.assertNotIn("continue-on-error", step)
                self.assertTrue(step["run"].startswith("set -euo pipefail\n"))
                self.assertNotIn("${{", step["run"])


# Fixture commands for the step scripts. Nothing touches the network.
# `git ls-remote --tags origin` lists $FIXTURE_DIR/tags. `gh` keeps the releases in
# $FIXTURE_DIR/releases.json and emulates only the exact --jq filters the workflow
# uses (any other filter exits 97, so a changed filter fails the tests here and must
# be checked against real gh). Publishing a draft creates its tag at the draft's
# target, as GitHub is expected to. `python3` answers the NuGet presence check from
# $FIXTURE_DIR/nuget and runs anything else with the real interpreter. `dotnet`
# records pushes. Every call is logged to $FIXTURE_DIR/calls. A call the fixtures
# do not know is also written to $FIXTURE_DIR/unexpected, and the tests fail when
# that file exists, even if the step script ignored the exit code.
FIXTURE_GIT = """#!/usr/bin/env bash
echo "git $*" >> "$FIXTURE_DIR/calls"
[[ "$*" == "ls-remote --tags origin" ]] || { echo "git $*" >> "$FIXTURE_DIR/unexpected"; exit 97; }
[[ -f "$FIXTURE_DIR/ls-fails" ]] && exit 128
# From the second listing on, a test may show other tags (`late-tags`) or an
# unreadable remote (`late-ls-fails`): the state of the remote changed since the first look.
n=$(( $(cat "$FIXTURE_DIR/ls-count" 2>/dev/null || echo 0) + 1 ))
echo "$n" > "$FIXTURE_DIR/ls-count"
if [[ "$n" -ge 2 ]]; then
  [[ -f "$FIXTURE_DIR/late-ls-fails" ]] && exit 128
  [[ -f "$FIXTURE_DIR/late-tags" ]] && { cat "$FIXTURE_DIR/late-tags"; exit 0; }
fi
cat "$FIXTURE_DIR/tags"
"""
FIXTURE_PYTHON = """#!/usr/bin/env bash
echo "python3 $*" >> "$FIXTURE_DIR/calls"
if [[ "$1" == "scripts/check_nuget_presence.py" ]]; then
  [[ "$2" == "--id" && "$4" == "--version" && $# -eq 5 ]] || [[ $# -eq 2 ]] || {
    echo "python3 $*" >> "$FIXTURE_DIR/unexpected"; exit 2; }
  [[ -f "$FIXTURE_DIR/nuget-fails" ]] && exit 1
  key="$3 $5"; [[ $# -eq 2 ]] && key="$(basename "$2")"
  grep -Fxq -- "$key" "$FIXTURE_DIR/nuget" && exit 0
  exit 3
fi
exec "$REAL_PYTHON" "$@"
"""
FIXTURE_DOTNET = """#!/usr/bin/env bash
echo "dotnet $*" >> "$FIXTURE_DIR/calls"
[[ -f "$FIXTURE_DIR/push-fails" ]] && exit 1
exit 0
"""
FIXTURE_GH = '''
import json, os, sys
from pathlib import Path

d = Path(os.environ["FIXTURE_DIR"])
args = sys.argv[1:]
with open(d / "calls", "a", encoding="utf-8") as calls:
    calls.write("gh " + " ".join(args) + "\\n")
path = d / "releases.json"
releases = json.loads(path.read_text(encoding="utf-8"))
repo = "repos/" + os.environ["GH_REPO_EXPECTED"] + "/"

def save():
    path.write_text(json.dumps(releases), encoding="utf-8")

def flag(name):
    return args[args.index(name) + 1] if name in args else None

def by_tag(tag):
    found = [r for r in releases if r["tag_name"] == tag]
    if len(found) != 1:
        sys.exit(1)
    return found[0]

def unexpected(code):
    with open(d / "unexpected", "a", encoding="utf-8") as record:
        record.write("gh " + " ".join(args) + "\\n")
    sys.exit(code)

def tsv(*values):
    # As jq's @tsv: a null becomes an empty field.
    return "\\t".join("" if value is None else value for value in values)

FILTERS = {
    ".[] | select(.draft == false) | .tag_name":
        lambda rs: [r["tag_name"] for r in rs if r["draft"] is False],
    ".[] | [.tag_name, (.draft | tostring), (.id | tostring), .target_commitish] | @tsv":
        lambda rs: [tsv(r["tag_name"], json.dumps(r["draft"]), str(r["id"]), r["target_commitish"]) for r in rs],
    ".[] | [.tag_name, (.draft | tostring), (.id | tostring)] | @tsv":
        lambda rs: [tsv(r["tag_name"], json.dumps(r["draft"]), str(r["id"])) for r in rs],
}
# The one filter the workflow uses to read a release by its id.
ID_FILTERS = {
    "[.tag_name, (.draft | tostring), .target_commitish] | @tsv":
        lambda r: [tsv(r["tag_name"], json.dumps(r["draft"]), r["target_commitish"])],
}
# The two endpoints GitHub serves for a tag; a draft is not returned by it.
TAG_FILTERS = {".draft": lambda r: [json.dumps(r["draft"])], ".assets[].name": lambda r: r["assets"]}

if (d / "gh-fails").exists():
    sys.exit(1)

def count_read():
    # A list of the releases and a read of one release by id are both reads. On
    # the second read the releases become `late-releases.json`, if a test wrote
    # one: the releases changed after the classification listed them. Later
    # reads see the state that results.
    global releases
    n_path = d / "read-count"
    n = int(n_path.read_text(encoding="utf-8")) + 1 if n_path.exists() else 1
    n_path.write_text(str(n), encoding="utf-8")
    if n == 2 and (d / "late-releases.json").exists():
        releases = json.loads((d / "late-releases.json").read_text(encoding="utf-8"))
        save()

if args[:2] == ["api", "--paginate"]:
    if len(args) != 5 or args[2] != repo + "releases?per_page=100" or args[3] != "--jq":
        unexpected(96)
    if args[4] not in FILTERS:
        unexpected(97)
    count_read()
    # `list-fails-after` holds N: list calls after the Nth fail. `list-hides-drafts`
    # shows a list that lags behind: it has no drafts.
    count_path = d / "list-count"
    count = int(count_path.read_text(encoding="utf-8")) + 1 if count_path.exists() else 1
    count_path.write_text(str(count), encoding="utf-8")
    after = d / "list-fails-after"
    if after.exists() and count > int(after.read_text(encoding="utf-8")):
        sys.exit(1)
    shown = [r for r in releases if not ((d / "list-hides-drafts").exists() and r["draft"])]
    for line in FILTERS[args[4]](shown):
        print(line)
elif args[:2] == ["api", "-X"] and len(args) == 4 and args[2] == "DELETE":
    prefix = repo + "releases/"
    if not args[3].startswith(prefix) or not args[3][len(prefix):].isdigit():
        unexpected(96)
    rid = int(args[3][len(prefix):])
    if rid not in [r["id"] for r in releases]:
        sys.exit(1)  # GitHub answers 404
    if not (d / "delete-ignored").exists():
        releases[:] = [r for r in releases if r["id"] != rid]
        save()
elif (len(args) == 4 and args[0] == "api" and args[1].startswith(repo + "releases/")
      and args[1][len(repo + "releases/"):].isdigit()):
    if args[2] != "--jq" or args[3] not in ID_FILTERS:
        unexpected(97)
    count_read()
    if (d / "release-read-fails").exists():
        sys.exit(1)
    found = [r for r in releases if str(r["id"]) == args[1][len(repo + "releases/"):]]
    if not found:
        sys.exit(1)  # GitHub answers 404
    for line in ID_FILTERS[args[3]](found[0]):
        print(line)
elif len(args) == 4 and args[0] == "api" and args[1].startswith(repo + "releases/tags/"):
    if args[2] != "--jq" or args[3] not in TAG_FILTERS:
        unexpected(97)
    if (d / "tag-endpoint-fails").exists():
        sys.exit(1)
    tag = args[1][len(repo + "releases/tags/"):]
    visible = [r for r in releases if r["tag_name"] == tag
               and (r["draft"] is False or (d / "tags-endpoint-shows-drafts").exists())]
    if not visible:
        sys.exit(1)
    for line in TAG_FILTERS[args[3]](visible[0]):
        print(line)
elif args[:2] == ["release", "create"]:
    if (d / "create-fails").exists():
        sys.exit(1)
    # `create-target` holds the target that GitHub is made to report for the new draft,
    # whatever `--target` said. `create-adds-draft` makes a second draft for the tag.
    target = ((d / "create-target").read_text(encoding="utf-8") if (d / "create-target").exists()
              else flag("--target") or "main")
    releases.append({"id": max([r["id"] for r in releases] + [100]) + 1, "tag_name": args[2],
                     "draft": "--draft" in args, "target_commitish": target,
                     "prerelease": "--prerelease" in args, "title": flag("--title"),
                     "generate_notes": "--generate-notes" in args,
                     "body": Path(flag("--notes-file")).read_text(encoding="utf-8"), "assets": []})
    with open(d / "bodies", "a", encoding="utf-8") as bodies:
        bodies.write(releases[-1]["body"])
    if (d / "create-adds-draft").exists():
        releases.append(dict(releases[-1], id=releases[-1]["id"] + 1))
    save()
elif args[:2] == ["release", "upload"]:
    if (d / "upload-fails").exists():
        sys.exit(1)
    release = by_tag(args[2])
    release["assets"] = sorted(set(release["assets"]) | {Path(a).name for a in args[3:] if a != "--clobber"})
    save()
elif args[:2] == ["release", "edit"]:
    release = by_tag(args[2])
    if "--draft=false" in args:
        # `edit-keeps-draft`: the tag is made but the Release stays a draft.
        release["draft"] = (d / "edit-keeps-draft").exists()
        release["latest"] = [a for a in args if a.startswith("--latest")]
        target = flag("--target") or release["target_commitish"]
        if (d / "tag-lands-elsewhere").exists():
            target = "f" * 40
        with open(d / "tags", "a", encoding="utf-8") as tags:
            tags.write(target + "\\trefs/tags/" + release["tag_name"] + "\\n")
    save()
else:
    unexpected(98)
'''
RUN_SHA = "c" * 40
ID_READ = "gh api repos/tomas-rampas/vouchfx/releases/"
ID_READ_FILTER = "[.tag_name, (.draft | tostring), .target_commitish] | @tsv"
STALE_DRAFT = ("Release 42 could not be read, or is no longer the draft for v1.5.0 at " + "c" * 40
               + "; nothing was deleted.")
OTHER = "d" * 40
REHEARSAL = "0.0.0-rehearsal.1"
PACKAGE_IDS = ("vouchfx", "Vouchfx.Sdk", "Vouchfx.Sdk.Testing", "Vouchfx.Engine.Abstractions",
               "Vouchfx.Engine.Authoring", "Vouchfx.Engine.Compilation")
FIXTURE_CHANGELOG = """# Changelog

## [Unreleased]

## [{0}] — 2026-12-01

### Added

- Feature for {0}.

## [1.4.0] — 2026-11-21

- Older.
"""
FIXTURE_PROPS = ("<Project><PropertyGroup><VouchfxEngineVersion Condition=\"'$(VouchfxEngineVersion)' == ''\">"
                 "{0}</VouchfxEngineVersion></PropertyGroup></Project>\n")


class Fixture:
    """A scratch working directory with fixture commands and remote state."""

    def __init__(self, scratch, tags=(), releases=(), nuget=(), markers=(), changelog="1.5.0", props="1.5.0",
                 late_tags=None, changelog_text=None, list_fails_after=None, late_releases=None,
                 create_target=None):
        self.root = Path(scratch)
        self.dir = self.root / "fixture"
        self.bin = self.dir / "bin"
        self.bin.mkdir(parents=True)
        for name, text in (("git", FIXTURE_GIT), ("python3", FIXTURE_PYTHON), ("dotnet", FIXTURE_DOTNET),
                           ("gh", f"#!{sys.executable}\n" + FIXTURE_GH)):
            (self.bin / name).write_text(text, encoding="utf-8", newline="\n")
            (self.bin / name).chmod(0o755)
        (self.dir / "tags").write_text("".join(f"{sha}\trefs/tags/{name}\n" for name, sha in tags),
                                       encoding="utf-8", newline="\n")
        (self.dir / "releases.json").write_text(json.dumps(list(releases)), encoding="utf-8")
        (self.dir / "nuget").write_text("".join(f"{line}\n" for line in nuget), encoding="utf-8")
        (self.dir / "calls").write_text("", encoding="utf-8")
        if late_tags is not None:
            (self.dir / "late-tags").write_text("".join(f"{sha}\trefs/tags/{name}\n" for name, sha in late_tags),
                                                encoding="utf-8", newline="\n")
        if late_releases is not None:
            (self.dir / "late-releases.json").write_text(json.dumps(list(late_releases)), encoding="utf-8")
        for marker in markers:
            (self.dir / marker).write_text("", encoding="utf-8")
        if create_target is not None:
            (self.dir / "create-target").write_text(create_target, encoding="utf-8")
        if list_fails_after is not None:
            (self.dir / "list-fails-after").write_text(str(list_fails_after), encoding="utf-8")
        self.work = self.root / "work"
        (self.work / "scripts").mkdir(parents=True)
        shutil.copy(ROOT / "scripts/release_metadata.py", self.work / "scripts/release_metadata.py")
        (self.work / "CHANGELOG.md").write_text(
            FIXTURE_CHANGELOG.format(changelog) if changelog_text is None else changelog_text, encoding="utf-8")
        (self.work / "Directory.Build.props").write_text(FIXTURE_PROPS.format(props), encoding="utf-8")
        self.temp = self.root / "runner-temp"
        self.temp.mkdir()

    def env(self, **values):
        env = os.environ.copy()
        env.update(FIXTURE_DIR=self.dir.as_posix(), REAL_PYTHON=sys.executable, GITHUB_SHA=RUN_SHA,
                   GITHUB_REF="refs/heads/main", GITHUB_REPOSITORY=REPOSITORY, GH_REPO_EXPECTED=REPOSITORY,
                   RUNNER_TEMP=self.temp.as_posix(), GH_TOKEN="fixture")
        env.update(values)
        return env

    def run(self, script, env):
        output = self.root / "github-output"
        if output.exists():
            output.unlink()
        env = dict(env, GITHUB_OUTPUT=output.as_posix())
        result = subprocess.run([BASH, "--noprofile", "--norc", "-c",
                                 'export PATH="$FIXTURE_DIR/bin:$PATH"\n' + script],
                                cwd=self.work, env=env, capture_output=True, timeout=60)
        outputs = {}
        if output.exists():
            for line in output.read_text(encoding="utf-8").splitlines():
                key, _, value = line.partition("=")
                outputs[key] = value
        return result.returncode, (result.stdout + result.stderr).decode(errors="replace"), outputs

    @property
    def calls(self):
        return (self.dir / "calls").read_text(encoding="utf-8")

    @property
    def releases(self):
        return json.loads((self.dir / "releases.json").read_text(encoding="utf-8"))

    @property
    def tags(self):
        return (self.dir / "tags").read_text(encoding="utf-8")

    @property
    def unexpected(self):
        path = self.dir / "unexpected"
        return path.read_text(encoding="utf-8") if path.exists() else ""


def release(tag, draft, target=RUN_SHA, rid=7, assets=()):
    return {"id": rid, "tag_name": tag, "draft": draft, "target_commitish": target,
            "prerelease": "-" in tag, "assets": list(assets), "body": ""}


def run_assets(version="1.5.0"):
    """The asset names `PublishReleaseScriptTests.run_job` prepares in publish mode."""
    return sorted([f"Vouchfx.Sdk.{version}.nupkg", f"vouchfx-{version}-linux-x64.tar.gz",
                   f"vouchfx.{version}.nupkg", f"vouchfx.{version}.nupkg.cosign.bundle"])


class PrepareScriptTests(unittest.TestCase):
    """Runs the actual `prepare` check with fixture `git`, `gh` and `python3`."""

    @classmethod
    def setUpClass(cls):
        steps = workflow(ROOT / ".github/workflows/release.yml")["jobs"][PREPARE]["steps"]
        cls.script = next(s for s in steps if s.get("id") == "request")["run"]

    def check(self, mode="publish", version="1.5.0", commit=RUN_SHA, ref="refs/heads/main",
              repository=REPOSITORY, **fixture):
        with tempfile.TemporaryDirectory() as scratch:
            f = Fixture(scratch, **fixture)
            code, output, outputs = f.run(self.script, f.env(MODE=mode, VERSION=version, COMMIT=commit,
                                                             GITHUB_REF=ref, GITHUB_REPOSITORY=repository))
            self.assertEqual("", f.unexpected, "a call the fixtures do not know")
            return code, output, outputs, f.calls

    def assertAccepted(self, result, version="1.5.0", mode="publish"):
        code, output, outputs, _ = result
        self.assertEqual(0, code, output)
        self.assertEqual({"version": version, "tag": f"v{version}", "mode": mode}, outputs)

    def assertRefused(self, result, text):
        code, output, outputs, _ = result
        self.assertNotEqual(0, code, output)
        self.assertIn("::error::", output)
        self.assertIn(text, output)
        self.assertEqual({}, outputs)

    def test_publish_request_on_main_at_the_run_commit_is_accepted(self):
        result = self.check(releases=[release("v1.4.0", False, OTHER)], tags=[("v1.4.0", OTHER)],
                            nuget=["vouchfx 1.4.0"])
        self.assertAccepted(result)
        calls = result[3]
        self.assertIn("git ls-remote --tags origin\n", calls)
        for package_id in PACKAGE_IDS:
            self.assertIn(f"python3 scripts/check_nuget_presence.py --id {package_id} --version 1.5.0\n", calls)
        self.assertIn("python3 scripts/release_metadata.py check", calls)

    def test_malformed_versions_are_refused_in_every_mode(self):
        for version in ("1.2.3.rc.1", "1.2.3+build", "1.2.3-rc.1+build.7", "v1.2.3", "1.2", "1.2.3.4",
                        "01.2.3", "1.2.3-", "1.2.3-rc..1", "1.2.3 ", " 1.2.3", "", "1.2.3\n", "1.2.3-rc_1",
                        # A numeric pre-release part has no leading zero.
                        "1.2.3-01", "1.2.3-00", "1.2.3-rc.01", "1.2.3-0.007", "1.2.3-rc.1.02"):
            for mode in ("validate", "rehearse", "publish"):
                with self.subTest(version=version, mode=mode):
                    result = self.check(mode=mode, version=version, changelog=version, props=version)
                    self.assertRefused(result, "is not MAJOR.MINOR.PATCH")
                    self.assertEqual("", result[3], "no lookup before the version is valid")

    def test_well_formed_versions_are_accepted(self):
        for version in ("0.0.0-test", "1.2.3", "1.0.0-alpha.4", "1.5.0-rc.1", "10.20.30-x-y.0",
                        "1.2.3-0", "1.2.3-0a", "1.2.3-01a", "1.2.3-rc.0", "1.2.3--", "1.2.3-0-1", "1.2.3-x.10"):
            with self.subTest(version=version):
                self.assertAccepted(self.check(mode="validate", version=version), version, "validate")

    def test_unknown_mode_is_refused(self):
        self.assertRefused(self.check(mode="Publish"), "Unknown mode")

    def test_validate_mode_skips_the_main_only_checks(self):
        result = self.check(mode="validate", ref="refs/heads/feature", commit="", repository="someone/fork",
                            tags=[("v1.5.0", OTHER)], releases=[release("v1.5.0", False, OTHER)],
                            nuget=["vouchfx 1.5.0"], changelog="1.4.9", props="1.4.9",
                            markers=["ls-fails", "gh-fails", "nuget-fails"])
        self.assertAccepted(result, mode="validate")
        self.assertEqual("", result[3])

    def test_wrong_ref_is_refused(self):
        for mode, version in (("rehearse", REHEARSAL), ("publish", "1.5.0")):
            for ref in ("refs/heads/feature", "refs/tags/v1.5.0", "refs/heads/main-old", "refs/pull/1/merge"):
                with self.subTest(mode=mode, ref=ref):
                    self.assertRefused(self.check(mode=mode, version=version, ref=ref), "runs only on refs/heads/main")

    def test_commit_must_be_the_full_run_commit(self):
        for mode, version in (("rehearse", REHEARSAL), ("publish", "1.5.0")):
            for commit in ("", OTHER, RUN_SHA[:12], RUN_SHA.upper(), RUN_SHA + " ", " " + RUN_SHA, RUN_SHA + "\n"):
                with self.subTest(mode=mode, commit=commit):
                    self.assertRefused(self.check(mode=mode, version=version, commit=commit), "must be the full SHA")

    def test_other_repository_is_refused(self):
        self.assertRefused(self.check(mode="rehearse", version=REHEARSAL, repository="someone/vouchfx"), "runs only in")

    def test_existing_tag_is_refused(self):
        for mode, version in (("rehearse", REHEARSAL), ("publish", "1.5.0")):
            for tags in ([(f"v{version}", RUN_SHA)], [(f"v{version}", OTHER), (f"v{version}^{{}}", RUN_SHA)]):
                with self.subTest(tags=tags, mode=mode):
                    self.assertRefused(self.check(mode=mode, version=version, tags=tags), "already exists")
        # A tag of a longer version is not this tag.
        self.assertAccepted(self.check(tags=[("v1.5.0-rc.1", OTHER), ("v1.5.00", OTHER)]))

    def test_unreadable_tags_or_releases_are_refused(self):
        self.assertRefused(self.check(markers=["ls-fails"]), "Could not read the remote tags")
        self.assertRefused(self.check(markers=["gh-fails"]), "Could not read the releases")

    def test_existing_published_release_is_refused_and_a_draft_is_not(self):
        for mode, version in (("rehearse", REHEARSAL), ("publish", "1.5.0")):
            with self.subTest(mode=mode):
                self.assertRefused(self.check(mode=mode, version=version, releases=[release(f"v{version}", False)]),
                                   f"A published Release for 'v{version}' already exists")
        self.assertAccepted(self.check(releases=[release("v1.5.0", True)]))

    def test_published_release_of_a_longer_tag_does_not_stop_the_version(self):
        # `v1.5.0-rc.1` contains `v1.5.0`; only a whole-line match is this tag.
        self.assertAccepted(self.check(releases=[release("v1.5.0-rc.1", False, OTHER)],
                                       tags=[("v1.5.0-rc.1", OTHER)]))
        self.assertRefused(self.check(releases=[release("v1.5.0", False, OTHER)]),
                           "A published Release for 'v1.5.0' already exists")
        self.assertRefused(self.check(releases=[release("v1.5.0-rc.1", False, OTHER), release("v1.5.0", False, OTHER)],
                                      tags=[("v1.5.0-rc.1", OTHER)]),
                           "A published Release for 'v1.5.0' already exists")

    def test_version_already_on_nuget_is_refused(self):
        for package_id in PACKAGE_IDS:
            with self.subTest(package_id=package_id):
                self.assertRefused(self.check(nuget=[f"{package_id} 1.5.0"]), "already on NuGet.org")
        self.assertRefused(self.check(mode="rehearse", version=REHEARSAL, nuget=[f"Vouchfx.Sdk {REHEARSAL}"]),
                           "already on NuGet.org")
        self.assertRefused(self.check(markers=["nuget-fails"]), "Could not check")

    def test_publish_requires_release_notes_it_can_use(self):
        empty = FIXTURE_CHANGELOG.format("1.5.0").replace("### Added\n\n- Feature for 1.5.0.\n", "")
        long = FIXTURE_CHANGELOG.format("1.5.0").replace("- Feature for 1.5.0.", "x" * 100_001)
        for label, text in (("empty section", empty), ("section too long", long)):
            with self.subTest(case=label):
                result = self.check(changelog_text=text)
                self.assertRefused(result, "The CHANGELOG.md section for 1.5.0 cannot be used as the Release notes")
                self.assertIn("python3 scripts/release_metadata.py notes --changelog CHANGELOG.md --version 1.5.0",
                              result[3])
        result = self.check(changelog_text=long)
        self.assertIn("characters; the limit is 100000", result[1])
        # Rehearse and validate do not read the notes.
        for mode, version in (("rehearse", REHEARSAL), ("validate", "1.5.0")):
            with self.subTest(mode=mode):
                result = self.check(mode=mode, version=version, changelog_text=empty)
                self.assertAccepted(result, version, mode)
                self.assertNotIn("release_metadata.py notes", result[3])

    def test_publish_requires_the_changelog_and_props_to_be_prepared(self):
        self.assertRefused(self.check(changelog="1.4.9"), "Merge the release pull request")
        self.assertRefused(self.check(props="1.4.9"), "Merge the release pull request")
        # Rehearse and validate do not read them.
        self.assertAccepted(self.check(mode="rehearse", version=REHEARSAL, changelog="1.4.9", props="1.4.9"),
                            REHEARSAL, "rehearse")

    def test_rehearsal_versions_of_the_reserved_form_are_accepted(self):
        for version in ("0.0.0-rehearsal.1", "0.0.0-rehearsal.12"):
            with self.subTest(version=version):
                result = self.check(mode="rehearse", version=version)
                self.assertAccepted(result, version, "rehearse")
                self.assertIn("git ls-remote --tags origin\n", result[3])

    def test_rehearsal_with_any_other_version_is_refused_before_any_lookup(self):
        for version in ("1.5.0", "1.5.0-rc.1", "0.0.0-test", "0.0.0-rehearsal", "0.0.0-rehearsal.x",
                        "0.0.1-rehearsal.1", "0.0.0-rehearsal.1.2", "0.0.0-rehearsal.1-x",
                        "0.0.0-Rehearsal.1"):
            with self.subTest(version=version):
                result = self.check(mode="rehearse", version=version)
                self.assertRefused(result, "A rehearsal must use a version of the form 0.0.0-rehearsal.N")
                self.assertEqual("", result[3], "no lookup before the rehearsal version is checked")

    def test_publish_refuses_versions_reserved_for_validation_and_rehearsal(self):
        for version in ("0.0.0-test", "0.0.0-rehearsal.1", "0.0.0-anything"):
            with self.subTest(version=version):
                result = self.check(mode="publish", version=version, changelog=version, props=version)
                self.assertRefused(result, "reserved for validation and rehearsal")
                self.assertEqual("", result[3], "no lookup before the reserved version is refused")
        # Only the 0.0.0- prefix is reserved.
        self.assertAccepted(self.check(version="0.0.1-rc.1", changelog="0.0.1-rc.1", props="0.0.1-rc.1"),
                            "0.0.1-rc.1")

    def test_validate_keeps_the_default_version(self):
        self.assertAccepted(self.check(mode="validate", version="0.0.0-test"), "0.0.0-test", "validate")


class PublishReleaseScriptTests(unittest.TestCase):
    """Runs publish-release's actual steps in order, with fixture commands.

    `uses:` steps are emulated: the checkout and downloads by the prepared working
    directory, NuGet/login by success or failure. A step's `if` is evaluated only
    when it is one of the exact strings the structure tests pin; any other string
    fails the test. Step `env` expressions are resolved from a fixed table.
    """

    @classmethod
    def setUpClass(cls):
        cls.job = workflow(ROOT / ".github/workflows/release.yml")["jobs"]["publish-release"]

    def run_job(self, mode="publish", version="1.5.0", login_fails=False, signing_mode=None, no_files=False,
                duplicate_asset=False, **fixture):
        # `no_files` prepares no release files at all; `duplicate_asset` adds a second
        # file named like the first, in another directory.
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        f = Fixture(scratch.name, changelog=version, props=version, **fixture)
        (f.work / "release-upload").mkdir(exist_ok=True)
        for name in (() if no_files else (f"vouchfx.{version}.nupkg", f"Vouchfx.Sdk.{version}.nupkg",
                                          f"vouchfx-{version}-linux-x64.tar.gz")):
            (f.work / "release-upload" / name).write_text("x", encoding="utf-8")
        if duplicate_asset:
            (f.work / "release-upload/other").mkdir()
            (f.work / "release-upload/other" / f"vouchfx.{version}.nupkg").write_text("y", encoding="utf-8")
        (f.work / "signing-material/nuget").mkdir(parents=True)
        (f.work / "signing-material/signing-mode").write_text((signing_mode or mode) + "\n", encoding="utf-8")
        if mode == "publish" and not no_files:
            (f.work / f"signing-material/nuget/vouchfx.{version}.nupkg.cosign.bundle").write_text("x", encoding="utf-8")
        base = f.env(MODE=mode, VERSION=version, TAG=f"v{version}")
        outputs = {"state": {}, "create": {}}
        failed, ran, log = False, [], ""
        for step in self.job["steps"]:
            condition = step.get("if")
            if condition is None:
                runs = not failed
            elif condition == MODE_PUBLISH:
                runs = not failed and mode == "publish"
            elif condition == REHEARSAL_CLEANUP_IF:
                runs = mode == "rehearse" and outputs["state"].get("state") == "none"
            else:
                self.fail(f"unexpected condition {condition!r}")
            if not runs:
                continue
            ran.append(step["name"])
            if "uses" in step:
                if step["uses"].startswith("NuGet/login@") and login_fails:
                    failed = True
                continue
            if step["name"] == "Test the release helper scripts":
                continue  # the unit tests run on their own
            # A step's outputs are taken even when it failed, as GitHub is expected
            # to process the output file of a failed step.
            table = {"${{ github.token }}": "fixture", "${{ github.repository }}": REPOSITORY,
                     "${{ steps.state.outputs.state }}": outputs["state"].get("state", ""),
                     "${{ steps.state.outputs.draft_id }}": outputs["state"].get("draft_id", ""),
                     "${{ steps.create.outputs.created }}": outputs["create"].get("created", ""),
                     "${{ steps.nuget-login.outputs.NUGET_API_KEY }}": "fixture-key"}
            env = dict(base, **{k: table[v] for k, v in step.get("env", {}).items()})
            code, output, step_outputs = f.run(step["run"], env)
            log += f"--- {step['name']} (exit {code})\n{output}"
            if "id" in step:
                outputs[step["id"]] = step_outputs
            if code:
                failed = True
        self.assertEqual("", f.unexpected, "a call the fixtures do not know\n" + log)
        bodies = f.dir / "bodies"
        return {"failed": failed, "ran": ran, "log": log, "calls": f.calls, "releases": f.releases, "outputs": outputs,
                "tags": f.tags, "bodies": bodies.read_text(encoding="utf-8") if bodies.exists() else ""}

    def assertReadBackBetweenCreateAndUpload(self, result, tag):
        # The classification lists the releases first. The read-back is a second list,
        # after the create and before the upload.
        calls = result["calls"]
        create = calls.find(f"gh release create {tag} ")
        upload = calls.find(f"gh release upload {tag} ")
        self.assertNotIn(-1, (create, upload), calls)
        readback = calls.find("gh api --paginate", create)
        self.assertNotEqual(-1, readback, calls)
        self.assertLess(readback, upload, calls)

    def calls_in_order(self, result, *needles):
        positions = [result["calls"].find(n) for n in needles]
        self.assertNotIn(-1, positions, result["calls"])
        self.assertEqual(sorted(positions), positions, result["calls"])

    def test_publish_from_nothing_drafts_pushes_publishes_and_verifies_in_that_order(self):
        result = self.run_job()
        self.assertFalse(result["failed"], result["log"])
        self.calls_in_order(result, "gh release create v1.5.0 --draft --target " + RUN_SHA,
                            "gh release upload v1.5.0", "dotnet nuget push release-upload/",
                            "gh release edit v1.5.0 --draft=false --target " + RUN_SHA + " --latest",
                            "gh api repos/tomas-rampas/vouchfx/releases/tags/v1.5.0 --jq .draft")
        self.assertReadBackBetweenCreateAndUpload(result, "v1.5.0")
        # The tags are read three times: to classify, just before the edit, and to verify.
        listing = "git ls-remote --tags origin\n"
        self.assertEqual(3, result["calls"].count(listing), result["calls"])
        second = result["calls"].find(listing, result["calls"].find(listing) + 1)
        self.assertLess(result["calls"].find("dotnet nuget push release-upload/"), second)
        self.assertLess(second, result["calls"].find("gh release edit v1.5.0"))
        [published] = result["releases"]
        self.assertIs(False, published["draft"])
        self.assertEqual(RUN_SHA, published["target_commitish"])
        self.assertIs(False, published["prerelease"])
        self.assertIs(True, published["generate_notes"])
        self.assertEqual("### Added\n\n- Feature for 1.5.0.\n", published["body"])
        self.assertEqual(["Vouchfx.Sdk.1.5.0.nupkg", "vouchfx-1.5.0-linux-x64.tar.gz", "vouchfx.1.5.0.nupkg",
                          "vouchfx.1.5.0.nupkg.cosign.bundle"], published["assets"])
        self.assertNotIn("signing-mode", published["assets"])
        self.assertEqual(f"{RUN_SHA}\trefs/tags/v1.5.0\n", result["tags"])

    def test_pre_release_is_marked_and_never_latest(self):
        result = self.run_job(version="1.5.0-rc.1")
        self.assertFalse(result["failed"], result["log"])
        self.assertIn("--generate-notes --prerelease\n", result["calls"])
        self.assertIn("--target " + RUN_SHA + " --latest=false\n", result["calls"])
        self.assertIs(True, result["releases"][0]["prerelease"])

    def test_existing_draft_at_the_run_commit_is_deleted_and_made_again(self):
        old = dict(release("v1.5.0", True, rid=42, assets=["stale.zip", "vouchfx.1.5.0.nupkg"]),
                   body="Old notes.", title="Old title", prerelease=True)
        result = self.run_job(releases=[old], nuget=["vouchfx 1.5.0"])
        self.assertFalse(result["failed"], result["log"])
        self.calls_in_order(result, ID_READ + "42 --jq " + ID_READ_FILTER + "\n",
                            "gh api -X DELETE repos/tomas-rampas/vouchfx/releases/42\n",
                            "gh release create v1.5.0 --draft --target " + RUN_SHA,
                            "gh release upload v1.5.0", "dotnet nuget push", "gh release edit v1.5.0")
        self.assertIn("Deleted the earlier draft 42 for v1.5.0; this run replaces it with a new draft.",
                      result["log"])
        [published] = result["releases"]
        self.assertNotEqual(42, published["id"])
        self.assertEqual(run_assets(), published["assets"])
        self.assertEqual(("### Added\n\n- Feature for 1.5.0.\n", "v1.5.0", False),
                         (published["body"], published["title"], published["prerelease"]))
        # A draft means "continue": a package already on NuGet.org does not stop it.
        self.assertNotIn("check_nuget_presence.py --id", result["calls"])

    def assertDraftKeptAfterRead(self, result, text, kept):
        # The read of the release by id happened, and nothing was changed after it.
        self.assertTrue(result["failed"], result["log"])
        self.assertIn(f"::error::{text}", result["log"])
        self.assertEqual(CREATE_STEP, result["ran"][-1])
        self.assertIn(ID_READ + "42 --jq ", result["calls"])
        for absent in ("-X DELETE", "gh release create", "gh release upload", "gh release edit", "dotnet"):
            self.assertNotIn(absent, result["calls"])
        self.assertEqual(kept, result["releases"])
        self.assertEqual("", result["tags"])

    def test_draft_made_a_release_since_the_classification_is_not_deleted(self):
        old = release("v1.5.0", True, rid=42)
        now = release("v1.5.0", False, rid=42)
        result = self.run_job(releases=[old], late_releases=[now], nuget=["vouchfx 1.5.0"])
        self.assertDraftKeptAfterRead(result, STALE_DRAFT, [now])

    def test_draft_whose_target_or_tag_changed_since_the_classification_is_not_deleted(self):
        for label, now in (("target", release("v1.5.0", True, target=OTHER, rid=42)),
                           ("target now null", release("v1.5.0", True, target=None, rid=42)),
                           ("tag", release("v1.5.1", True, rid=42)),
                           ("release gone", release("v1.5.0", True, rid=43))):
            with self.subTest(case=label):
                result = self.run_job(releases=[release("v1.5.0", True, rid=42)], late_releases=[now])
                self.assertDraftKeptAfterRead(result, STALE_DRAFT, [now])

    def test_unreadable_draft_is_not_deleted(self):
        draft = release("v1.5.0", True, rid=42)
        result = self.run_job(releases=[draft], markers=["release-read-fails"])
        self.assertDraftKeptAfterRead(result, STALE_DRAFT, [draft])

    def test_notes_failure_leaves_the_earlier_draft_in_place(self):
        draft = release("v1.5.0", True, rid=42)
        result = self.run_job(releases=[draft], changelog_text="# Changelog\n\n## [Unreleased]\n")
        self.assertTrue(result["failed"], result["log"])
        self.assertEqual(CREATE_STEP, result["ran"][-1])
        self.assertIn("python3 scripts/release_metadata.py notes", result["calls"])
        for absent in ("-X DELETE", ID_READ, "gh release create", "gh release upload", "dotnet"):
            self.assertNotIn(absent, result["calls"])
        self.assertEqual([draft], result["releases"])

    def test_draft_without_a_usable_id_is_refused_before_anything_is_changed(self):
        for rid in ("abc", "1 2", "-1"):
            with self.subTest(rid=rid):
                result = self.run_job(releases=[release("v1.5.0", True, rid=rid)])
                self.assertTrue(result["failed"])
                self.assertIn("has no usable id", result["log"])
                for absent in ("-X DELETE", "gh release create", "dotnet"):
                    self.assertNotIn(absent, result["calls"])

    def test_draft_with_a_null_target_is_refused_as_another_commit(self):
        # jq's @tsv prints a null target as an empty field.
        for mode, version in (("publish", "1.5.0"), ("rehearse", REHEARSAL)):
            with self.subTest(mode=mode):
                result = self.run_job(mode=mode, version=version, releases=[release(f"v{version}", True, target=None)])
                self.assertStoppedAtClassify(result, f"The draft for 'v{version}' targets '', not this run's commit")

    def test_published_at_the_run_commit_completes_without_changing_the_release(self):
        result = self.run_job(releases=[release("v1.5.0", False, assets=run_assets())], tags=[("v1.5.0", RUN_SHA)],
                              nuget=["vouchfx.1.5.0.nupkg", "Vouchfx.Sdk.1.5.0.nupkg"])
        self.assertFalse(result["failed"], result["log"])
        for absent in ("gh release create", "gh release upload", "gh release edit", "dotnet nuget push", "-X DELETE"):
            self.assertNotIn(absent, result["calls"])
        self.assertIn("releases/tags/v1.5.0 --jq .assets[].name", result["calls"])
        self.assertIn("releases/tags/v1.5.0 --jq .draft", result["calls"])
        self.assertIn("already published with an asset of each name this run would upload", result["log"])
        self.assertNotIn("uploaded before publication", result["log"])

    def test_published_release_without_this_runs_assets_stops_before_nuget(self):
        for label, assets in (("no assets", []), ("one missing", run_assets()[1:]),
                              ("one extra", run_assets() + ["other.zip"]),
                              ("one renamed", run_assets()[1:] + ["Vouchfx.Sdk.1.5.0.nupkg.old"])):
            with self.subTest(case=label):
                result = self.run_job(releases=[release("v1.5.0", False, assets=assets)], tags=[("v1.5.0", RUN_SHA)])
                self.assertTrue(result["failed"])
                self.assertIn("::error::The published Release v1.5.0 does not hold this run's assets", result["log"])
                self.assertEqual(CREATE_STEP, result["ran"][-1])
                for absent in ("gh release upload", "gh release edit", "dotnet", "-X DELETE"):
                    self.assertNotIn(absent, result["calls"])

    def test_unreadable_assets_of_a_published_release_stop_before_nuget(self):
        result = self.run_job(releases=[release("v1.5.0", False, assets=run_assets())], tags=[("v1.5.0", RUN_SHA)],
                              markers=["tag-endpoint-fails"])
        self.assertTrue(result["failed"])
        self.assertIn("::error::Could not read the assets of the published Release v1.5.0.", result["log"])
        self.assertEqual(CREATE_STEP, result["ran"][-1])
        self.assertNotIn("dotnet", result["calls"])

    def test_higher_stable_release_stops_a_stable_publication(self):
        for label, version, other in (("minor", "1.5.0", "v1.6.0"), ("numeric, not text", "1.9.0", "v1.10.0"),
                                      ("major", "1.5.0", "v2.0.0"), ("patch", "1.5.0", "v1.5.1")):
            for state, fixture in (("none", {}), ("draft", {"releases": [release(f"v{version}", True)]})):
                with self.subTest(case=label, state=state):
                    releases = fixture.get("releases", []) + [release(other, False, OTHER, rid=3)]
                    result = self.run_job(version=version, releases=releases, tags=[(other, OTHER)])
                    self.assertStoppedAtClassify(
                        result, f"::error::Refusing to publish 'v{version}': the published Release '{other}' is "
                                f"a higher stable version")

    def test_highest_of_several_stable_releases_is_found_numerically(self):
        # With one published Release a text sort and a numeric sort agree; with several they do not.
        releases = [release("v1.9.0", False, OTHER, rid=3), release("v1.10.0", False, OTHER, rid=4),
                    release("v1.0.0", False, OTHER, rid=5)]
        result = self.run_job(version="1.9.5", releases=releases)
        self.assertStoppedAtClassify(result, "the published Release 'v1.10.0' is a higher stable version")

    def test_lower_pre_release_or_draft_releases_do_not_stop_a_publication(self):
        for label, version, others in (
                ("lower stable", "1.5.0", [release("v1.4.0", False, OTHER, rid=3)]),
                ("lower by number", "1.10.0", [release("v1.9.0", False, OTHER, rid=3)]),
                ("higher pre-release", "1.5.0", [release("v1.6.0-rc.1", False, OTHER, rid=3)]),
                ("higher draft", "1.5.0", [release("v1.6.0", True, OTHER, rid=3)]),
                ("higher non-version tag", "1.5.0", [release("v9.0.0.1", False, OTHER, rid=3)]),
                ("pre-release publication", "1.5.0-rc.1", [release("v1.6.0", False, OTHER, rid=3)])):
            with self.subTest(case=label):
                result = self.run_job(version=version, releases=others)
                self.assertFalse(result["failed"], result["log"])
                self.assertIn(f"gh release edit v{version}", result["calls"])

    def test_package_already_on_nuget_with_no_draft_stops_before_the_draft(self):
        for package_id in PACKAGE_IDS:
            with self.subTest(package_id=package_id):
                result = self.run_job(nuget=[f"{package_id} 1.5.0"])
                self.assertStoppedAtClassify(
                    result, f"::error::{package_id} 1.5.0 is already on NuGet.org, but no draft or Release exists "
                            "for 'v1.5.0'. Either an earlier run pushed it and its draft was deleted, or a re-run "
                            "deleted its own draft and failed before making a new one (see RELEASING.md, case 7).")
        result = self.run_job(markers=["nuget-fails"])
        self.assertStoppedAtClassify(result, "::error::Could not check vouchfx 1.5.0 on NuGet.org.")
        # Every package ID is checked when none is present.
        calls = self.run_job()["calls"]
        for package_id in PACKAGE_IDS:
            self.assertIn(f"python3 scripts/check_nuget_presence.py --id {package_id} --version 1.5.0\n", calls)

    def test_rehearse_does_not_check_nuget_or_newer_releases_in_classify(self):
        result = self.run_job(mode="rehearse", version=REHEARSAL, nuget=[f"vouchfx {REHEARSAL}"],
                              releases=[release("v9.0.0", False, OTHER, rid=3)])
        self.assertFalse(result["failed"], result["log"])
        self.assertNotIn("check_nuget_presence", result["calls"])

    def assertStoppedAtClassify(self, result, text):
        self.assertTrue(result["failed"])
        self.assertIn(text, result["log"])
        self.assertEqual("Classify the release state", result["ran"][-1])
        for absent in ("gh release create", "gh release upload", "gh release edit", "dotnet", "-X DELETE"):
            self.assertNotIn(absent, result["calls"])

    def test_tag_or_release_at_another_commit_fails_before_anything_is_changed(self):
        for mode in ("publish", "rehearse"):
            for label, fixture, text in (
                    ("tag elsewhere", {"tags": [("v1.5.0", OTHER)]}, "resolves to " + OTHER),
                    ("annotated tag peels elsewhere", {"tags": [("v1.5.0", RUN_SHA), ("v1.5.0^{}", OTHER)]},
                     "resolves to " + OTHER),
                    ("published elsewhere", {"tags": [("v1.5.0", OTHER)], "releases": [release("v1.5.0", False, OTHER)]},
                     "resolves to " + OTHER),
                    ("draft targets a branch", {"releases": [release("v1.5.0", True, "main")]}, "targets 'main'"),
                    ("draft targets another commit", {"releases": [release("v1.5.0", True, OTHER)]}, "targets '" + OTHER),
                    ("published without a tag", {"releases": [release("v1.5.0", False)]}, "its tag cannot be found"),
                    ("two drafts", {"releases": [release("v1.5.0", True, rid=1), release("v1.5.0", True, rid=2)]},
                     "2 releases or drafts")):
                with self.subTest(mode=mode, case=label):
                    self.assertStoppedAtClassify(self.run_job(mode=mode, **fixture), text)

    def test_unreadable_state_fails_closed(self):
        self.assertStoppedAtClassify(self.run_job(markers=["ls-fails"]), "Could not read the remote tags")
        self.assertStoppedAtClassify(self.run_job(markers=["gh-fails"]), "Could not read the releases")

    def test_rehearse_drafts_uploads_logs_in_and_deletes_the_draft_without_pushing_or_publishing(self):
        result = self.run_job(mode="rehearse", version="0.0.0-rehearsal.1")
        self.assertFalse(result["failed"], result["log"])
        self.calls_in_order(result, "gh release create v0.0.0-rehearsal.1 --draft --target " + RUN_SHA,
                            "gh release upload v0.0.0-rehearsal.1", "gh api -X DELETE")
        self.assertReadBackBetweenCreateAndUpload(result, "v0.0.0-rehearsal.1")
        self.assertIn("NuGet login (OIDC → short-lived API key)", result["ran"])
        for step in (PUSH_STEP, PUBLISH_STEP, VERIFY_STEP):
            self.assertNotIn(step, result["ran"])
        for absent in ("dotnet", "gh release edit", "releases/tags/"):
            self.assertNotIn(absent, result["calls"])
        self.assertEqual([], result["releases"])
        self.assertEqual("", result["tags"])

    def test_rehearse_notes_are_a_placeholder(self):
        result = self.run_job(mode="rehearse", version="0.0.0-rehearsal.1")
        self.assertFalse(result["failed"], result["log"])
        self.assertEqual("Rehearsal draft made by the release workflow. It is deleted when the rehearsal ends "
                         "and is never published.\n", result["bodies"])

    def test_rehearse_deletes_its_draft_when_a_later_step_fails(self):
        for label, kwargs in (("login fails", {"login_fails": True}),
                              ("upload fails", {"markers": ["upload-fails"]})):
            with self.subTest(case=label):
                result = self.run_job(mode="rehearse", version="0.0.0-rehearsal.1", **kwargs)
                self.assertTrue(result["failed"])
                self.assertIn(CLEANUP_STEP, result["ran"])
                self.assertIn("gh api -X DELETE", result["calls"])
                self.assertEqual([], result["releases"])

    def test_rehearse_refuses_an_existing_draft_and_leaves_it_alone(self):
        draft = release("v0.0.0-rehearsal.1", True)
        result = self.run_job(mode="rehearse", version="0.0.0-rehearsal.1", releases=[draft])
        self.assertStoppedAtClassify(result, "A rehearsal needs no existing tag, draft or Release")
        self.assertNotIn(CLEANUP_STEP, result["ran"])
        self.assertEqual([draft], result["releases"])

    def test_rehearse_refuses_an_existing_tag_at_the_run_commit(self):
        result = self.run_job(mode="rehearse", version=REHEARSAL, tags=[(f"v{REHEARSAL}", RUN_SHA)])
        self.assertStoppedAtClassify(result, "A rehearsal needs no existing tag, draft or Release")
        self.assertNotIn(CLEANUP_STEP, result["ran"])

    def test_rehearsal_clean_up_fails_when_it_cannot_read_the_releases(self):
        # The first list is the classification and the second is the read-back after
        # the create; the clean-up's own list fails.
        result = self.run_job(mode="rehearse", version=REHEARSAL, list_fails_after=2)
        self.assertTrue(result["failed"])
        self.assertIn(f"--- {CLEANUP_STEP} (exit 1)", result["log"])
        self.assertIn(f"::error::Could not read the releases; delete the rehearsal draft for 'v{REHEARSAL}' by hand.",
                      result["log"])

    def test_rehearsal_clean_up_lists_again_after_the_deletion(self):
        for label, kwargs, text in (
                ("deletion did not take", {"markers": ["delete-ignored"]},
                 f"::error::A release or draft for 'v{REHEARSAL}' remains after the deletion. Delete it by hand."),
                ("second list unreadable", {"list_fails_after": 3},
                 "::error::Could not read the releases after the deletion; check by hand that no draft for "
                 f"'v{REHEARSAL}' remains.")):
            with self.subTest(case=label):
                result = self.run_job(mode="rehearse", version=REHEARSAL, **kwargs)
                self.assertTrue(result["failed"])
                self.assertIn(f"--- {CLEANUP_STEP} (exit 1)", result["log"])
                self.assertIn(text, result["log"])
                # Classify, read-back, and the clean-up's two lists.
                self.assertEqual(4, result["calls"].count("gh api --paginate"), result["calls"])

    def test_rehearsal_clean_up_fails_when_its_own_draft_is_not_listed(self):
        result = self.run_job(mode="rehearse", version=REHEARSAL, markers=["list-hides-drafts"])
        self.assertTrue(result["failed"])
        self.assertIn(f"::error::This job created a rehearsal draft for 'v{REHEARSAL}', but the list of releases "
                      "does not show it. Look for the draft by hand and delete it.", result["log"])
        # When the draft step made no draft, an empty list is the expected answer.
        result = self.run_job(mode="rehearse", version=REHEARSAL, markers=["create-fails"])
        self.assertTrue(result["failed"])
        self.assertIn(f"--- {CLEANUP_STEP} (exit 0)", result["log"])
        self.assertIn(f"No rehearsal draft for v{REHEARSAL} to delete.", result["log"])

    def test_failed_push_leaves_only_the_draft(self):
        result = self.run_job(markers=["push-fails"])
        self.assertTrue(result["failed"])
        self.assertIn("dotnet nuget push", result["calls"])
        self.assertNotIn("gh release edit", result["calls"])
        self.assertIs(True, result["releases"][0]["draft"])
        self.assertEqual("", result["tags"])

    def assertStoppedBeforeEdit(self, result, text):
        self.assertTrue(result["failed"])
        self.assertIn(text, result["log"])
        self.assertEqual(PUBLISH_STEP, result["ran"][-1])
        self.assertNotIn("gh release edit", result["calls"])
        self.assertIs(True, result["releases"][0]["draft"])
        self.assertEqual("", result["tags"])

    def test_tag_made_at_another_commit_after_classification_stops_the_publication(self):
        for state, fixture in (("none", {}), ("draft", {"releases": [release("v1.5.0", True)]})):
            for label, late in (("tag elsewhere", [("v1.5.0", OTHER)]),
                                ("annotated tag peels elsewhere", [("v1.5.0", RUN_SHA), ("v1.5.0^{}", OTHER)])):
                with self.subTest(state=state, case=label):
                    result = self.run_job(late_tags=late, **fixture)
                    self.assertStoppedBeforeEdit(result, f"Tag 'v1.5.0' now resolves to {OTHER}, not to this run's commit {RUN_SHA}")
                    self.assertIn("dotnet nuget push", result["calls"])
                    self.assertEqual(2, result["calls"].count("git ls-remote --tags origin\n"))

    def test_unreadable_tags_just_before_the_publication_stop_it(self):
        result = self.run_job(markers=["late-ls-fails"])
        self.assertStoppedBeforeEdit(result, "Could not read the remote tags; nothing was published.")

    def test_late_tag_at_the_run_commit_or_no_tag_lets_the_publication_continue(self):
        for label, late in (("no tag", []), ("tag at the run commit", [("v1.5.0", RUN_SHA)]),
                            ("another version's tag", [("v1.5.0-rc.1", OTHER), ("v1.5.00", OTHER)])):
            with self.subTest(case=label):
                result = self.run_job(late_tags=late)
                # The fixture shows the late listing to the verification step too, so
                # only the publication step is judged here.
                self.assertIn("--- Publish the draft release (exit 0)", result["log"])
                self.assertIn("gh release edit v1.5.0", result["calls"])

    def test_already_published_state_does_not_read_the_tags_again_in_the_publication_step(self):
        result = self.run_job(releases=[release("v1.5.0", False, assets=run_assets())], tags=[("v1.5.0", RUN_SHA)],
                              nuget=["vouchfx.1.5.0.nupkg", "Vouchfx.Sdk.1.5.0.nupkg"])
        self.assertFalse(result["failed"], result["log"])
        # Classify and verify read the tags; the publication step exits early.
        self.assertEqual(2, result["calls"].count("git ls-remote --tags origin\n"), result["calls"])

    def test_verification_fails_when_the_tag_lands_on_another_commit(self):
        result = self.run_job(markers=["tag-lands-elsewhere"])
        self.assertTrue(result["failed"])
        self.assertEqual(VERIFY_STEP, result["ran"][-1])
        self.assertIn("not to this run's commit", result["log"])

    def test_verification_fails_when_the_release_is_still_a_draft(self):
        # GitHub's tag endpoint gives a 404 for a draft; the second case reaches the
        # script's own `.draft` comparison, as a second line of defence.
        for markers in (["edit-keeps-draft"], ["edit-keeps-draft", "tags-endpoint-shows-drafts"]):
            with self.subTest(markers=markers):
                result = self.run_job(markers=markers)
                self.assertTrue(result["failed"])
                self.assertEqual(VERIFY_STEP, result["ran"][-1])
                self.assertIn(f"--- {VERIFY_STEP} (exit 1)", result["log"])
                self.assertIn("::error::The Release v1.5.0 is not published.", result["log"])

    def test_presence_check_error_refuses_the_push(self):
        # A draft, so that the classification does not read NuGet.org itself.
        result = self.run_job(releases=[release("v1.5.0", True)], markers=["nuget-fails"])
        self.assertTrue(result["failed"])
        self.assertEqual(PUSH_STEP, result["ran"][-1])
        self.assertIn("::error::Presence check failed for release-upload/", result["log"])
        self.assertNotIn("dotnet nuget push", result["calls"])
        self.assertNotIn("gh release edit", result["calls"])

    def assertReadBackRefused(self, result, found, tag="v1.5.0"):
        # The draft was made and `created` was reported, then the read-back refused it:
        # nothing was uploaded, pushed or published.
        self.assertTrue(result["failed"], result["log"])
        self.assertEqual("true", result["outputs"]["create"].get("created"))
        self.assertIn(f"::error::The new draft for {tag} did not read back as expected: entries found for the "
                      f"tag: {found}; expected exactly one draft with target {RUN_SHA}. Nothing was uploaded and "
                      "nothing was pushed to NuGet.org.", result["log"])
        self.assertIn(f"gh release create {tag} ", result["calls"])
        for absent in ("gh release upload", "gh release edit", "dotnet", "releases/tags/"):
            self.assertNotIn(absent, result["calls"])
        for step in (PUSH_STEP, PUBLISH_STEP, VERIFY_STEP):
            self.assertNotIn(step, result["ran"])

    def test_draft_that_reads_back_with_another_target_stops_before_the_upload(self):
        for target in ("main", RUN_SHA[:7]):
            with self.subTest(target=target):
                result = self.run_job(create_target=target)
                self.assertReadBackRefused(result, f"1, draft flag 'true', target '{target}'")
                self.assertEqual(CREATE_STEP, result["ran"][-1])
                self.assertNotIn("-X DELETE", result["calls"])
                [draft] = result["releases"]
                self.assertEqual(target, draft["target_commitish"])
                self.assertEqual([], draft["assets"])
                self.assertEqual("", result["tags"])

    def test_read_back_failure_in_a_rehearsal_still_deletes_the_draft(self):
        for label, fixture, found in (
                ("another target", {"create_target": "main"}, "1, draft flag 'true', target 'main'"),
                ("a second draft", {"markers": ["create-adds-draft"]}, "2, draft flag '', target ''")):
            with self.subTest(case=label):
                result = self.run_job(mode="rehearse", version=REHEARSAL, **fixture)
                self.assertReadBackRefused(result, found, tag=f"v{REHEARSAL}")
                self.assertIn(f"--- {CLEANUP_STEP} (exit 0)", result["log"])
                self.assertIn("gh api -X DELETE", result["calls"])
                self.assertEqual([], result["releases"])

    def test_a_second_draft_at_the_read_back_stops_before_the_upload(self):
        result = self.run_job(markers=["create-adds-draft"])
        self.assertReadBackRefused(result, "2, draft flag '', target ''")
        self.assertEqual(2, len(result["releases"]))

    def test_unreadable_list_at_the_read_back_fails_closed(self):
        # The first list is the classification; the read-back list fails.
        result = self.run_job(list_fails_after=1)
        self.assertTrue(result["failed"], result["log"])
        self.assertEqual(CREATE_STEP, result["ran"][-1])
        self.assertIn("::error::Could not read the releases back after creating the draft for v1.5.0. Nothing was "
                      "uploaded and nothing was pushed to NuGet.org.", result["log"])
        for absent in ("gh release upload", "gh release edit", "dotnet", "-X DELETE"):
            self.assertNotIn(absent, result["calls"])
        # In a rehearsal the clean-up still runs (and fails on its own unreadable list).
        result = self.run_job(mode="rehearse", version=REHEARSAL, list_fails_after=1)
        self.assertTrue(result["failed"], result["log"])
        self.assertIn(CLEANUP_STEP, result["ran"])
        self.assertNotIn("gh release upload", result["calls"])

    def test_draft_state_is_read_back_too(self):
        draft = release("v1.5.0", True, rid=42)
        result = self.run_job(releases=[draft], create_target="main")
        self.assertTrue(result["failed"], result["log"])
        self.assertIn("did not read back as expected", result["log"])
        self.assertIn("-X DELETE repos/tomas-rampas/vouchfx/releases/42", result["calls"])
        self.assertNotIn("gh release upload", result["calls"])
        self.assertNotIn("dotnet", result["calls"])

    def test_two_release_files_with_one_name_are_refused_before_the_draft(self):
        result = self.run_job(duplicate_asset=True)
        self.assertTrue(result["failed"], result["log"])
        self.assertEqual(CREATE_STEP, result["ran"][-1])
        self.assertIn("::error::Two assets share a name: vouchfx.1.5.0.nupkg", result["log"])
        for absent in ("gh release create", "gh release upload", "dotnet", "-X DELETE"):
            self.assertNotIn(absent, result["calls"])
        self.assertEqual([], result["releases"])

    def test_no_release_files_are_refused_before_the_draft(self):
        for mode, version in (("publish", "1.5.0"), ("rehearse", REHEARSAL)):
            with self.subTest(mode=mode):
                result = self.run_job(mode=mode, version=version, no_files=True)
                self.assertTrue(result["failed"], result["log"])
                self.assertIn("::error::No release files were found.", result["log"])
                for absent in ("gh release create", "gh release upload", "dotnet"):
                    self.assertNotIn(absent, result["calls"])
                self.assertEqual([], result["releases"])

    def test_signing_material_from_another_mode_is_refused_before_the_draft(self):
        result = self.run_job(signing_mode="validate")
        self.assertTrue(result["failed"])
        self.assertIn("made for another mode", result["log"])
        self.assertNotIn("gh release create", result["calls"])


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
        self.assertEqual({"VERSION": "${{ needs.prepare.outputs.version }}"}, step["env"])
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

    def test_no_workflow_triggers_on_a_release_or_a_tag_push(self):
        for path in workflow_files():
            data = workflow(path)
            with self.subTest(workflow=path.name):
                self.assertNotIn("release", triggers_of(data))
                trigger = data["on"]
                push = trigger.get("push") if isinstance(trigger, dict) else None
                if "push" in triggers_of(data):
                    # A bare `push` (or one with no branch or tag filter) also fires on tags.
                    self.assertIsInstance(push, dict, "a bare push trigger fires on tag pushes")
                    self.assertTrue({"tags", "tags-ignore"}.isdisjoint(push))
                    self.assertTrue({"branches", "branches-ignore"} & set(push),
                                    "a push trigger without a branch filter fires on tag pushes")

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
        self.assertTrue({"publish-release", "publish-marketplace", PREPARE} <= needs_of(job))
        self.assertEqual({"tag": "${{ needs.prepare.outputs.tag }}",
                          "expected_sha": "${{ github.sha }}"}, job["with"])
        # The same tag value `publish-release` publishes under.
        self.assertEqual("${{ needs.prepare.outputs.tag }}",
                         self.release["jobs"]["publish-release"]["env"]["TAG"])
        self.assertEqual({"contents": "write"}, job["permissions"])
        self.assertEqual(MOVE_TAG_IF, job["if"])
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
        # release.yml's `prepare` refuses a `+build` version, so only a `+build` tag in
        # the listing can reach this path from a release run.
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
