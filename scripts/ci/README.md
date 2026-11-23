# Workflow and Docker CI checks

The blocking `workflow-contract` job lints every workflow and executes the literal
pre-pull script from `.github/workflows/docker-integration.yml` (the reusable
workflow holding the Docker / Aspire lane's steps) and the prewarm script from
`.github/workflows/vouchfx-run.yml`. Source extraction must agree byte-for-byte with
the parsed YAML blocks. The pre-pull tests substitute its image-list boundary; the
prewarm tests bind `PREWARM_IMAGES` as the workflow does. Both use fixture Docker
commands, so no containers or images are changed. The pre-pull checks cover retry,
classification, registry backoff and tally; the prewarm checks cover whitespace
trimming, unmatched quotes, annotation escaping and log fencing. Negative mutation
controls confirm the pre-pull checks detect a lost fatal exit and changed YAML
representation, and that the prewarm fence check detects a missing newline before
the resume command.

The same file pins how the Docker lane is called. `docker-integration.yml` has only
the `workflow_call` trigger with one required `reviewed_sha` input, maps no secrets,
grants `contents: read`, and runs one job with no `if` or `needs` that checks out
`reviewed_sha` and stops unless HEAD is that commit. In `build.yml`, the
`integration` job calls it with `reviewed_sha` only and keeps the lane's `needs` and
`if`, and `integration-gate` carries the required check name and fails when the lane
failed or was cancelled. The lane job also has a 120-minute `timeout-minutes`, and the
artefact names the lane uploads must not clash with those uploaded in either calling
workflow. No workflow uses `secrets: inherit`. No job or step `if` in the lane
reads the `github`, `env`, `vars` or `inputs` context, because the lane has callers on
different events and must run the same steps for each.

The release workflow is the lane's second caller. `test_release_workflow.py` pins
the `docker-integration` job in `release.yml` (the same `uses`, `reviewed_sha`
and `contents: read`, `needs: prepare` and no `if`), requires `sign-and-attest` and
`publish-release` to need it, and derives from the parsed workflow which jobs
have a side effect (a secret, any `secrets` reference inside an expression, a
`HAS_*` condition, any `write` permission, or an attest, cosign or publish step or
command). Each such job must be reachable from the gate through `needs`, except
`build-msi`, `build-deb` and `build-pkg`, which sign or notarise at build time and
are allowlisted by name with their exact set of reasons; the test fails if a set
changes, and it also fails when a step gains a further distinct matched command.
The recogniser is a tripwire for the ordinary ways a side effect is added (a secret,
a write permission, a known signing or publishing action or command), not a proof
that no side effect exists: it does not see an unauthenticated network call, an
effect inside a called reusable workflow, or a command not on its list that uses no
secret. A separate test pins
the workflow-level `permissions` to `contents: read` and requires no secret in the
workflow-level `env`. No job downstream of the gate may use any status-check
function (`always()`, `success()`, `failure()`, `cancelled()`, in any letter case)
in its job-level `if`. Every `actions/checkout` step in `release.yml` must omit `ref` or
set it to `${{ github.sha }}`, because the gate tests the run's commit and every build
must ship that same commit.

The release contract also requires that manual dispatch (`workflow_dispatch`) is the
only trigger of `release.yml`, with the inputs `mode` (`validate`, `rehearse`,
`publish`; `validate` by default), `version` and `commit`. It pins the first job,
`prepare`, as the read-only root that every other job needs, and the source of the
version and tag for the later jobs. It pins the exact `if` of `publish-release`,
`publish-marketplace` and `move-floating-tag`, and requires that none of them uses a
status-check function. The `release` environment must be on `publish-release` and on
no other job. Attestation and cosign signing steps must run in `publish` mode only.
`sign-and-attest` and `publish-marketplace` each have an exact `needs` list, and no
job may join the `release-publish` concurrency group. The steps of `publish-release`
must keep their order (classify the state, create the draft and upload the assets,
NuGet login, delete the rehearsal draft, push, publish the draft, verify the tag,
last). The exact set of `gh` command forms in `prepare` and in `publish-release` is
pinned. A `gh api` call with a method flag in either spelling, a field or input
flag, or `graphql` counts as a write. A `curl` or other HTTP client that uses the
token is not seen. The shape of that job is fixed: its keys, its permissions, its
`env`, and each step's keys and own `env`, so a step cannot gain an `if`, a
`continue-on-error` or an `env` without failing a test. Only the clean-up step of the
rehearsal draft may use a status-check function. No step may make a tag with `git`.

The tests also run the actual step scripts of `prepare` and `publish-release` under
Bash, with fixture `git`, `gh`, `python3` and `dotnet` commands, for each mode. They
cover the refusal of a malformed version, a rehearsal version that is not of the
form `0.0.0-rehearsal.N`, a `0.0.0-` version in `publish` mode, a wrong ref, a commit that is not the run's
commit, another repository, an existing tag or published Release, a version already
on NuGet and an unprepared changelog, and the order and result of the draft, the
upload, the NuGet push, the publication and the check of the tag. They also cover
the recovery states: an existing draft at the run's commit, which is deleted and
replaced by a new one; a published Release that holds other assets than the run
would upload; a higher stable version that is already published; a package on
NuGet with no draft; a tag or draft at another commit; and a failed push that
leaves only the draft. They cover the clean-up of a rehearsal draft, also after a
failed step, including its second list of the releases. The test image has no `gh`
or `jq` here either: the fixture `gh` emulates six `--jq` filters (three list
filters, the read of one release by its id, and the tag endpoint's `.draft` and
`.assets[].name`). A call that the
fixtures do not know is recorded and fails the test. The real `gh` and the jq
expressions are not run.

The same file pins the floating-tag move (`MoveFloatingTagStructureTests` and
`MoveFloatingTagScriptTests`). `move-floating-tag.yml` has only the
`workflow_call` and `workflow_dispatch` triggers and the inputs of each.
No workflow triggers on `release` or on a tag push, and `release.yml` is the only
caller of `move-floating-tag.yml`. The calling job's `needs` includes `prepare`,
`publish-release` and `publish-marketplace`; it takes the tag from `prepare`'s
output and the commit from `github.sha`. Its `if`, `permissions` and `with` are
fixed. In
`release.yml` only that job and `publish-release` hold `contents: write`; in
`move-floating-tag.yml` only the called job does (the `write-all` shorthand
counts as write). The called workflow has exactly one job, with its repository
condition and a job-level concurrency group that `release.yml` does not use at
workflow level. Its keys, its three steps in order and the keys of each step are
fixed, so a step cannot gain an `if`, a `continue-on-error` or an `env` of its own
without failing a test. The checkout uses the full tag ref, and the step that
moves the tag does not call `gh`. These tests pin the called workflow's
current shape and the calling job's wiring. They are not a proof that no other edit
could move a tag: for example, a new step in a job that already holds
`contents: write`, or a job that starts the manual run, is not detected. The tests also run the actual step scripts under
Bash with fixture `git` and `gh` commands. They cover the format check on
`expected_sha`, the commit check, the no-downgrade check (it compares with the
release the floating tag is on, read with `git ls-remote`: numeric version order,
per release line, annotated tags, failing closed when the tags cannot be read or
the floating tag is on no release of its line), the refusal of a missing or draft
release on both paths, the manual move to an older release and the routing of each
release to its tag. The test image has no `gh` or `jq`, so the fixture `gh` stands
in for the draft check's `--jq` filter and the jq expression itself is not run.
Nothing here runs on GitHub: how a real release run behaves is not tested.

Run from the repository root with Python 3.12, Bash, yq 4.53.2 and ShellCheck 0.11.0:

```sh
python -B -m unittest discover -s scripts/ci -p 'test_*.py' -v
python -B scripts/ci/lint_workflows.py
```

On Windows, use `py -3.12` and Git Bash. The harness selects the standard Git Bash
installation; `VOUCHFX_CI_BASH` can name another Git Bash executable. Fixtures,
extracted scripts, command logs and integer exit files remain under TEMP.

The lint wrapper runs actionlint's structure checks and every inline Bash/sh block
through Bash syntax checks and ShellCheck. It uses actionlint's existing exclusions
for expressions and workflow environment variables; the standalone pre-pull check
uses no exclusions. This separate file transport avoids [actionlint 1.7.12's
pre-start pipe write](https://github.com/rhysd/actionlint/blob/914e7df21a07ef503a81201c76d2b11c789d3fca/process.go),
which can deadlock when a script exceeds the OS pipe capacity. Tools have bounded
waits, and any non-zero lint exit fails the gate. Python syntax is checked too.

`install-workflow-tools.sh` installs into the runner's temporary directory and
checks exact official release-asset SHA-256 values. Each download retries
transient release-host failures, including HTTP 504, before that check: [actionlint 1.7.12](https://github.com/rhysd/actionlint/releases/tag/v1.7.12)
(source commit `914e7df21a07ef503a81201c76d2b11c789d3fca`),
[ShellCheck 0.11.0](https://github.com/koalaman/shellcheck/releases/tag/v0.11.0) and
[yq 4.53.2](https://github.com/mikefarah/yq/releases/tag/v4.53.2).

The Docker test command uses `-m:1` to serialise test projects across assemblies.
Per-assembly xUnit settings alone do not prevent cross-assembly overlap. This
scheduling bound preserves `requires=docker`, mini crash dumps, coverage and
project-specific TRX files. It does not change intentional concurrency inside the
engine's `ParallelSuiteRunner` or its parallel capstone.

To measure that scheduling boundary with two real .NET 10 test assemblies:

```sh
python -B scripts/ci/probe_docker_serialization.py
```

This uses the actual workflow arguments and exact repository test-package versions
in TEMP, with feeds disabled; those packages must already be cached. One run omits
only `-m:1` and must overlap; the other must not. Two selected facts, distinct host
PIDs, monotonic intervals and per-project TRX counters are required for each run.
A non-overlapping control returns non-zero rather than claiming a reproduction.
The probe establishes local scheduling behaviour; hosted Docker execution and
branch protection require separate evidence. The default-root capture guard
remains intact.

## Integration capture artefacts

The Docker/Aspire job keeps the recorder enabled at its normal per-user root,
`${XDG_DATA_HOME:-$HOME/.local/share}/vouchfx`. It snapshots that directory before
the test step and collects newly appearing DCP captures afterwards, including when
the test fails. No job-wide capture-directory override is set: the READY-path
Docker test still checks the real default root.

The `integration-dcp-captures` artefact contains matching capture files and a
manifest with run, attempt, job, test outcome, filenames, sizes and SHA-256 values.
It expires after seven days. A run with no captures uploads only a zero-count
manifest. Snapshot state and resolved source paths are excluded from the artefact;
existing captures are not copied or deleted. Changed old files, links/reparse
points, invalid headers and unsafe paths fail collection. Upload requires successful
collection, so a refused destination cannot publish unrelated existing files.

Collection is bounded to 4,096 directory entries, 64 inventory captures, 256 KiB
per file, 12 new captures and 3 MiB of payload. It rejects excess rather than
truncating. The engine may already have pruned older captures before collection;
this copies the retained evidence, not a complete history of every topology start.

Capture bodies are verbatim, **not redacted**. They can contain generated dependency
passwords, fixed local test credentials, host values routed via `${env:NAME}` and
absolute host paths. Public Actions
artefact contents are not secret-masked; this integration job must continue mapping
no secrets or credential-bearing host values into its suites. Temporal inventory
attribution identifies files appearing during this job's test window, not individual
topology ownership. The native fixture tests prove collection and workflow contracts;
hosted upload and real Docker execution need separate evidence.
