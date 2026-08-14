# CI integration reference

vouchfx ships two first-party CI integrations that run a `.e2e.yaml` suite end-to-end against an
orchestrated container topology and publish JUnit and HTML artefacts:

- a **reusable GitHub Actions workflow** (`.github/workflows/vouchfx-run.yml`), and
- an **`include`-able GitLab CI/CD template** (`ci/gitlab/vouchfx-run.gitlab-ci.yml`).

They are behavioural equivalents: the same inputs (as workflow inputs or CI/CD variables), the same
build-from-source install, the same exit-code gating, and the same artefact-upload behaviour.

For a minimal copy-paste starting point, see the CI recipes in
[Recipes](recipes.md#ci-integration-with-github-actions). This page is the complete reference.

---

## Shared behaviour

### Exit-code gating (verdict taxonomy §12.1)

Both integrations respect the four-verdict taxonomy so CI can tell infrastructure breakage apart from
a product defect:

| Exit code | Verdict | Breaks CI? | When |
|---|---|---|---|
| **0** | Success — Pass, or EnvironmentError/Inconclusive when not opted in | – | `run` always; `plan` when no gaps or `--fail-on-gap` not set |
| **1** | **Fail** — one or more scenarios failed (a genuine defect) | **Always** | `run` only |
| **2** | UsageError — unrecognised option, bad arguments, missing path | Always | All commands |
| **3** | EnvironmentError (run) or catalogue error (tools) — unhealthy container, image-pull/seed failure, or incomplete provider metadata | Only when opted in (`run`) — **except** an unconfirmable `security:` declaration, which always breaks CI (see below) | `run` with `--fail-on-env-error`; `list`, `schema`, `validate`, `scaffold`, `plan` on metadata failure |
| **4** | Inconclusive — timeout, partition outlasted grace, unmet capture | Only when opted in — **except** a rejected `security:` declaration, which always breaks CI (see below) | `run` with `--fail-on-inconclusive`; `run` unconditionally if every scenario fails to parse |
| **5** | Gaps found — the Planner detected at least one coverage or vocabulary gap AND the caller opted in | Only when opted in | `plan` with `--fail-on-gap` |

The distinction lets CI systems handle each outcome independently: fail the build on a product `Fail`,
page on-call for `EnvironmentError`, and escalate `Inconclusive` to reliability engineering.

**Unconditional exceptions.** Two outcomes break CI whatever the opt-in flags say.

A `run` in which *every* discovered scenario fails to parse (malformed YAML, unknown step types across
the board) is classified Inconclusive and exits 4 regardless of the opt-in flag, matching the behaviour
of `vouchfx validate`.

A suite that declares a `security:` block the engine **cannot confirm** exits non-zero with no
`--fail-on-env-error` and no `--fail-on-inconclusive`. Each cause keeps the exit code its own
verdict names:

- the post-health-gate **secured-confirmation probe** fails. The probe connects to the declared
  endpoint with exactly the material a step would use, so *anything* that stops it completing that
  connection lands here rather than in a list that has to be kept current: the endpoint refuses the
  connection or does not speak TLS, its certificate does not chain to the declared `caCert`, it
  refuses the declared client certificate, or the declared client identity cannot be **loaded** at
  all — which covers a well-formed `clientKeyPassword` that cannot be resolved, that resolves to an
  empty value, or that does not decrypt the key. The run aborts before any step executes and exits
  **3**;
- a pre-topology **security preflight** rejects the declaration — a certificate or artefact path that
  escapes the suite directory or does not exist, an artefact `target` that is not an absolute
  in-container file path, or a `profile` with no wiring for the target's kind. No container
  starts and the run exits **4**. One measured exception applies to this arm, on the default
  (non-`--parallel`) path only, and it is a defect rather than a design — pre-existing, tracked as
  issue #399. The preflight can only refuse a document that reaches it, and on that path a
  **step-level** secret reference the engine refuses (a `${secret:...}` naming a source it cannot
  resolve, for instance) is judged first and stops the document short of the preflight. A scenario
  carrying both faults therefore loses the security refusal altogether — the printed diagnostic as
  well as the exit code — and the run exits **0** on the step fault's own ordinary terms, which are
  Inconclusive and gated by `--fail-on-inconclusive` like any other authoring error. Both faults
  must sit in the **same** scenario document to collide. A security fault with no step fault beside
  it exits 4 as described, on either path, and `--parallel` reaches the preflight first and exits 4
  either way;
- the **schema** rejects the document first — and for a document that declares a `security:` block
  at all, *any* schema error counts, wherever in the document it sits. The reason is that nothing
  downstream of a schema rejection runs, so the declaration is never validated, never probed and
  therefore never confirmed; a rejected secured document is unconfirmable whatever the rejection
  was about. An error inside the block itself is one case of that — a mistyped field name, a scalar
  where a list belongs, `caCert: [a, b]`, or the per-kind narrowing of `profile` (a `profile` the
  target's kind has no wiring for is refused by the root schema before the preflight sees it) — but
  so is a missing `method:` on an unrelated step. No container starts and the run exits **4**. Note
  the practical consequence: in a suite that declares `security:`, a schema error anywhere now
  reddens the run, where the same typo in an unsecured suite still exits 0. The run prints a line
  saying so, rather than leaving the exit code to be guessed at;
- the **secret reference** a `security:` block declares is refused before the topology is built:
  `clientKeyPassword` naming a source the engine cannot resolve, or holding anything other than one
  whole, well-formed `${secret:<source>/<path>}` reference. A declaration the engine cannot honour is
  one it cannot confirm. No container starts and the run exits **4**. Mind the boundary against the
  probe bullet above, because the two read alike and carry different codes: this door judges the
  reference's **form and source** from the YAML alone, before anything starts; a well-formed
  reference naming a known source that then fails to *resolve*, resolves empty, or does not open the
  key is the probe's case and exits **3**. The same fault in a *step's* field is an ordinary
  authoring error and carries no unconditional exit at all;
- a secured multi-scenario suite is refused over its **directory layout** — its scenarios live in
  different directories, so a relative path such as `caCert: ./certs/ca.pem` would name a different
  file per scenario and the pre-run probe could no longer be evidence about every scenario's steps.
  No container starts and the run exits **4**. Suites declaring no `security:` block are unaffected,
  as is `--parallel`, where each scenario owns its own topology and its own directory.

Every *other* cause of an environment error — an unhealthy container, an unpullable image, a seed
failure unrelated to security — is unaffected and still exits 0 by default. The reasoning behind the
carve-out: an unconfirmable security assertion is not an infrastructure flake the way a failed image
pull is. It is an assertion the author explicitly wrote into the suite, and treating it as opt-in-only
would hand a team that forgot a flag a green pipeline on a security suite that verified nothing.

### Artefacts

Both integrations **always publish reports** — via `if: always()` on GitHub Actions and `when: always`
on GitLab — so artefacts are available precisely when a suite does not pass:

- **`results.xml`** — JUnit XML for CI ingestion. The four verdicts map to distinct JUnit primitives
  (Fail → `<failure>`, EnvironmentError → `<error>`, Inconclusive → `<skipped>`). On GitLab this is
  surfaced natively in the pipeline and merge-request test-report UI.
- **`report.html`** — a self-contained HTML report with polling timelines, captured-variable
  provenance, failed-step diffs and the reproducibility envelope, with no secret values embedded.

### Installation model

The vouchfx CLI is packaged as a `dotnet` global tool (`ToolCommandName: vouchfx`), live on NuGet.org:

```bash
dotnet tool install --global vouchfx --prerelease
```

That is the recommended path for local use. **Both CI integrations deliberately build from source** at
the pinned ref instead, keeping the runner's engine in lock-step with the workflow/template definition
you pinned — pin a release tag or SHA to track a released engine.

The tool depends on the Aspire orchestration packages being present in the per-user NuGet cache; any
developer or CI environment that has built an Aspire app or resolved the engine's dependencies has
them. For machines with only the OS and no .NET SDK, use the self-contained per-OS archives and
installers attached to each [GitHub release](https://github.com/tomas-rampas/vouchfx/releases).

### The floating convenience tags

Three maintainer-moved tags exist as a convenience tier, maintained by
[`.github/workflows/move-floating-tag.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/.github/workflows/move-floating-tag.yml),
which force-moves them to each published release's commit:

| Tag | Tracks | State |
|---|---|---|
| `v1-alpha` | `v1.0.0-alpha.N` and `v1.0.0-beta.N` | Retired at `v1.0.0-alpha.10` — never deleted, simply no longer moved. |
| `v1-rc` | `v1.0.0-rc.N` | Current pre-GA tag. Tracks the latest release candidate; currently points at `v1.0.0-rc.3`. Moving to v1 at GA is an explicit edit. |
| `v1` | `v1.y.z` GA releases only | Starts moving once v1.0.0 ships. |

**Each pre-GA line gets its own tag on purpose.** A consumer who pinned `v1-alpha` chose the alpha
line; force-moving them onto a release candidate would change the engine under them without a ref
change on their side, and leave the tag name describing something it no longer points at. Moving
between lines is therefore an opt-in edit: switch `v1-alpha` → `v1-rc` when you are ready, and
`v1-rc` → `v1` at GA.

None of the three is a production-grade pin — see [Supply-chain hygiene](#supply-chain-hygiene).

---

## GitHub Actions

### Quick start

```yaml
jobs:
  vouchfx-e2e:
    # Convenience tier — good for a first try or a low-stakes repo.
    # See "Supply-chain hygiene" for the SHA-pinned production tier.
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@v1-rc
    with:
      scenario-path: ./tests/e2e
      fail-on-env-error: false
```

### Workflow inputs

| Input | Type | Default | Purpose |
|---|---|---|---|
| `scenario-path` | string | `.` | Directory (relative to the caller's checkout) to search recursively for `.e2e.yaml` scenarios. |
| `vouchfx-repo` | string | `${{ github.repository }}` | The `owner/repo` of the vouchfx repository to build from source. Override to track a fork, or to pin a released version. |
| `vouchfx-ref` | string | `${{ github.sha }}` | The git ref (commit SHA, tag or branch) of `vouchfx-repo` to build. Recommended: a full commit SHA for supply-chain repeatability. |
| `dotnet-version` | string | `8.0.x` | The .NET SDK version to install. vouchfx targets .NET 8 LTS. |
| `fail-on-env-error` | boolean | `false` | When `true`, an environment-error verdict fails the job with exit code 3. |
| `fail-on-inconclusive` | boolean | `false` | When `true`, an inconclusive verdict fails the job with exit code 4. |
| `prewarm-images` | string | (empty) | Optional newline-separated list of container images (one per line) to `docker pull` before the run, warming the Docker cache and mitigating Aspire/DCP's ~20-second per-resource cold-start watchdog. Each pull is best-effort and non-fatal. |
| `setup-script` | string | (empty) | Optional path (relative to the checkout) of a shell script run on the same runner before the suite, to produce fixture files the suite declares but does not ship — most often the certificates and key or trust stores a `security:` block names. It runs as `bash <path>`, so no executable bit is needed; a named-but-missing script fails the job. See [the mutual-TLS example](https://github.com/tomas-rampas/vouchfx/blob/main/examples/security-mtls.e2e.yaml). |
| `runs-on` | string | `ubuntu-latest` | The runner label to use. Must provide Docker; `ubuntu-latest` does. |

### Worked example

[`.github/workflows/vouchfx-run-reference.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/.github/workflows/vouchfx-run-reference.yml)
calls the reusable workflow against this repository's own minimal reference suite
(`examples/ci-reference/smoke.e2e.yaml`), proving the workflow runs a real suite green and publishes
artefacts end-to-end.

---

## GitLab CI

### Quick start

```yaml
include:
  - project: tomas-rampas/vouchfx
    # Convenience tier — see "Supply-chain hygiene" for the SHA-pinned tier.
    ref: v1-rc
    file: /ci/gitlab/vouchfx-run.gitlab-ci.yml

vouchfx-run:
  variables:
    VOUCHFX_SCENARIO_PATH: ./tests/e2e
    VOUCHFX_FAIL_ON_ENV_ERROR: "false"
```

### Configuration variables

| Variable | Type | Default | Purpose |
|---|---|---|---|
| `VOUCHFX_SCENARIO_PATH` | string | `.` | Directory (relative to the project root) to search recursively for `.e2e.yaml` scenarios. |
| `VOUCHFX_REPO_URL` | string | `$CI_REPOSITORY_URL` | Git URL of the vouchfx repository to build from source. Defaults to the calling project; override to track a fork. |
| `VOUCHFX_REF` | string | `$CI_COMMIT_SHA` | Git ref (commit SHA, tag or branch) of `VOUCHFX_REPO_URL` to build. |
| `VOUCHFX_DOTNET_IMAGE` | string | `mcr.microsoft.com/dotnet/sdk:8.0` | .NET 8 SDK container image the job runs in. |
| `VOUCHFX_FAIL_ON_ENV_ERROR` | string | `"false"` | When truthy, an environment-error verdict fails the job with exit code 3. |
| `VOUCHFX_FAIL_ON_INCONCLUSIVE` | string | `"false"` | When truthy, an inconclusive verdict fails the job with exit code 4. |
| `VOUCHFX_PREWARM_IMAGES` | string | (empty) | Optional whitespace/newline-separated list of container images to pre-warm. Pin each entry to an immutable digest. |
| `VOUCHFX_SETUP_SCRIPT` | string | (empty) | Optional path (relative to the project root) of a shell script run before the suite, to produce fixture files the suite declares but does not ship — the GitLab counterpart of the `setup-script` input above. A named-but-missing script fails the job. |

### Docker-in-Docker and the privileged-runner requirement

vouchfx stands up an Aspire/Testcontainers container topology, so the job needs a Docker daemon. The
template uses the standard GitLab **Docker-in-Docker (dind)** pattern with a `docker:dind` service.

**Caveat:** dind requires a **privileged runner**. The `docker:dind` service only starts on a GitLab
Runner configured with `privileged = true` (Docker executor) or an equivalently-privileged Kubernetes
executor. gitlab.com's shared SaaS Linux runners provide this; a self-managed runner must be
explicitly configured for it.

**Alternative for a non-privileged runner.** Use a **socket-bind runner**: mount the host daemon socket
into the build (`volumes = ["/var/run/docker.sock:/var/run/docker.sock", …]` in the runner config),
then drop the `services:` block and the dind/TLS variables, and set
`DOCKER_HOST: "unix:///var/run/docker.sock"`. Socket-bind trades isolation for not needing privileged
mode — choose per your security posture.

### Verification status (important)

The GitLab template is **static-validated only** — yamllint, the GitLab CI JSON schema, and a
behavioural-equivalence cross-check against the GitHub workflow. It has **not been run on a live
GitLab instance**; a live pipeline / `ci/lint` run is an infrastructure-gated follow-up.

The one substantive risk to verify when running live is whether vouchfx's **Aspire/DCP-managed
containers are reachable under sibling Docker-in-Docker**. The template sets
`TESTCONTAINERS_HOST_OVERRIDE=docker`, but DCP may resolve endpoints differently than raw
Testcontainers — that dind-to-DCP networking is the primary unknown.

See [`ci/gitlab/vouchfx-run.gitlab-ci.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/vouchfx-run.gitlab-ci.yml)
and [`ci/gitlab/README.md`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/README.md)
for implementation details.

---

## Supply-chain hygiene

For production use, pin everything to something immutable.

**GitHub Actions:**

1. **Pin the `uses:` reference to a full commit SHA**, not a moving branch or tag:
   ```yaml
   uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1.0.0-rc.3
   ```
   A branch or tag ref — including the `v1-alpha`/`v1-rc`/`v1` convenience tags — lets the workflow definition
   change underneath you; a SHA is immutable. The trailing `# vX.Y.Z` comment is not decorative: it is
   what lets Dependabot track and bump the pin automatically.
2. **Pin `vouchfx-ref` to a commit SHA or release tag**, never a branch.
3. **Pin each `prewarm-images` entry to an immutable image digest**:
   ```yaml
   prewarm-images: |
     traefik/whoami@sha256:abc123...
     postgres@sha256:def456...
   ```

**GitLab CI:** the same four rules — pin the `include:` `ref:` to a full commit SHA, pin `VOUCHFX_REF`,
pin each `VOUCHFX_PREWARM_IMAGES` entry to a digest, and pin `VOUCHFX_DOTNET_IMAGE` to a digest
(`mcr.microsoft.com/dotnet/sdk:8.0@sha256:…`) rather than the floating tag.

### Keeping the pin current

**On GitHub**, add a `github-actions` entry to your own `.github/dependabot.yml`:

```yaml
version: 2
updates:
  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"
```

Dependabot resolves the trailing `# vX.Y.Z` comment on the SHA pin, watches this repository's tags, and
opens a PR bumping both the SHA and the comment whenever vouchfx cuts a release — nobody runs a lookup
by hand, and the pin never drifts silently.

**On GitLab**, there is no built-in equivalent for `include: ref:` entries. The closest automation is a
[Renovate](https://docs.renovatebot.com/) custom regex manager watching this repository's tags and
opening an MR to bump the pinned SHA and its trailing comment.

**Resolving a SHA by hand** (the first pin, or without Dependabot/Renovate):

```bash
git ls-remote --tags https://github.com/tomas-rampas/vouchfx v1.0.0-rc.3
```

Depending on how the release tag was created (both kinds are documented in
[RELEASING.md](https://github.com/tomas-rampas/vouchfx/blob/main/RELEASING.md)), this prints either
*two* lines — `refs/tags/v1.0.0-rc.3` (an annotated tag object's own SHA) and
`refs/tags/v1.0.0-rc.3^{}` (the commit it points at, "peeled") — or a *single* line for a lightweight
tag, whose SHA already **is** the commit. **If a `^{}` line is present, take that one**; otherwise the
single line's SHA is the commit SHA to pin.

---

## See also

- **[Recipes → CI integration](recipes.md#ci-integration-with-github-actions)** — the minimal copy-paste starting point.
- **[Getting started](getting-started.md)** — your first test in 60 minutes.
- **[Troubleshooting](troubleshooting.md)** — Docker not running, the Aspire 20-second cold-start gotcha, and other real failure modes.
