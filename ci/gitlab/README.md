# vouchfx — GitLab CI template

`vouchfx-run.gitlab-ci.yml` is an **`include`-able GitLab CI/CD template** that runs a
vouchfx `.e2e.yaml` suite end-to-end against an orchestrated container topology and
publishes the JUnit + HTML reports as job artefacts (with the JUnit results surfaced
natively in the GitLab pipeline / merge-request UI).

It is the **GitLab equivalent of the reusable GitHub Actions workflow**
[`.github/workflows/vouchfx-run.yml`](../../.github/workflows/vouchfx-run.yml)
(Sprint-10 task **S10-C-02**) and is **behaviourally equivalent** to it — same inputs (as
CI/CD variables), same build-from-source install, same best-effort image pre-warm, same
`vouchfx run … --junit results.xml --html report.html [--fail-on-*]` invocation, same
exit-code gating (only a product **Fail** breaks CI by default), and the same
always-upload-the-reports behaviour.

## Quick start

In the consuming repository's `.gitlab-ci.yml`:

```yaml
stages:
  - test

include:
  - project: "your-org/vouchfx"
    ref: "<40-char-commit-sha>"          # PIN to a SHA, not a branch/tag (supply chain)
    file: "/ci/gitlab/vouchfx-run.gitlab-ci.yml"

# Configure the imported job by re-declaring it with variables:
vouchfx-run:
  variables:
    VOUCHFX_SCENARIO_PATH: "./tests/e2e"
    VOUCHFX_FAIL_ON_ENV_ERROR: "true"
```

Or extend the hidden base into your own job name:

```yaml
e2e:
  extends: .vouchfx-run
  stage: test
  variables:
    VOUCHFX_SCENARIO_PATH: "./tests/e2e"
```

The template ships **both** a hidden `.vouchfx-run` base (all the behaviour) and a
ready-to-run `vouchfx-run` job that `extends:` it.

## Configuration variables

| Variable | Default | Purpose |
|---|---|---|
| `VOUCHFX_SCENARIO_PATH` | `.` | Directory (relative to the cloned project root) to search recursively for `*.e2e.yaml`. |
| `VOUCHFX_REPO_URL` | `$CI_REPOSITORY_URL` | Git URL to clone vouchfx from for the build-from-source install (defaults to the including project, whose CI job token clones a private repo without extra credentials). |
| `VOUCHFX_REF` | `$CI_COMMIT_SHA` | Git ref (branch, tag, or — recommended — a full commit SHA) of `VOUCHFX_REPO_URL` to build. |
| `VOUCHFX_DOTNET_IMAGE` | `mcr.microsoft.com/dotnet/sdk:8.0` | .NET 8 SDK image the job runs in. Pin to a digest in production. |
| `VOUCHFX_FAIL_ON_ENV_ERROR` | `"false"` | When truthy, append `--fail-on-env-error` (Environment-error verdict fails the job, exit 3). |
| `VOUCHFX_FAIL_ON_INCONCLUSIVE` | `"false"` | When truthy, append `--fail-on-inconclusive` (Inconclusive verdict fails the job, exit 4). |
| `VOUCHFX_PREWARM_IMAGES` | `""` | Optional whitespace/newline-separated list of images to `docker pull` before the run (best-effort, non-fatal). Pin each entry to a digest. |

Truthy = `true` / `1` / `yes` / `on` (any case). Anything else (including the `"false"`
default) contributes no flag.

## Docker is required (the topology) — and the privileged-runner caveat

vouchfx stands up an Aspire/Testcontainers container topology, so the job needs a Docker
daemon. The template uses the standard GitLab **Docker-in-Docker (dind)** pattern: a
`docker:dind` `services:` entry provides the daemon, and `DOCKER_HOST` + the TLS variables
point the build's Docker client and Testcontainers/Aspire/DCP at it.
`TESTCONTAINERS_HOST_OVERRIDE=docker` tells Testcontainers the reachable hostname for the
ports the topology publishes on the sibling dind service.

> **dind needs a PRIVILEGED runner.** The `docker:dind` service only starts on a GitLab
> Runner configured with `privileged = true` (Docker executor) or an equivalently
> privileged Kubernetes executor. gitlab.com's shared SaaS Linux runners provide this; a
> self-managed runner must be configured for it.
>
> **Alternative for a non-privileged runner: socket-bind.** If your runner cannot run
> privileged dind, mount the host daemon socket into the build
> (`volumes = ["/var/run/docker.sock:/var/run/docker.sock", …]` in the runner config),
> then drop the `services:` block and the dind/TLS variables and set
> `DOCKER_HOST: "unix:///var/run/docker.sock"`. Socket-bind trades isolation for not
> needing privileged mode — choose per your security posture.

## Build-from-source install

vouchfx is **not yet** a published `dotnet tool` or container — it is an Aspire-host
executable (`src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj`, `OutputType Exe`, `IsAspireHost`). So
the template **builds it from source**: it `git clone`s `VOUCHFX_REPO_URL` at `VOUCHFX_REF`
into `.vouchfx-src`, runs `dotnet build … -c Release`, then invokes the CLI with
`dotnet run --project … --no-build`. Real packaging (a published binary / container image)
is a **forward-ready Sprint-11 dependency** — exactly as in the GitHub template. When it
lands, a consumer keeps this same template contract and overrides `VOUCHFX_REPO_URL` /
`VOUCHFX_REF` to track the packaged release; the install step is intentionally the only
thing that changes.

## Exit-code gating (verdict taxonomy §12.1)

| Exit | Verdict | Gates CI? |
|---|---|---|
| 0 | Pass (or un-gated EnvError / Inconclusive) | no |
| 1 | **Fail** | **always** |
| 2 | usage error | yes (bad arguments) |
| 3 | EnvironmentError | only with `VOUCHFX_FAIL_ON_ENV_ERROR` truthy |
| 4 | Inconclusive | only with `VOUCHFX_FAIL_ON_INCONCLUSIVE` truthy |

A non-zero exit fails the job — so a product **Fail** gates the consumer's CI by default,
infra errors / inconclusives only on opt-in.

## Artefacts

`artifacts:` is configured with `when: always` (the analogue of the GitHub workflow's
`if: always()`), so the `results.xml` + `report.html` are published even when the run
failed — those reports are most valuable precisely when the suite did not pass.
`reports: junit: results.xml` additionally surfaces the JUnit results natively in the
GitLab pipeline / MR test report. `expire_in: 7 days` mirrors the GitHub `retention-days: 7`.

## Supply-chain hygiene

- **Pin the `include:` `ref:` to a full commit SHA**, not a moving branch/tag.
- **Pin `VOUCHFX_REF` to a commit SHA or release tag**, never a branch.
- **Pin every `VOUCHFX_PREWARM_IMAGES` entry to an image digest** (`name@sha256:…`).
- **Pin `VOUCHFX_DOTNET_IMAGE` to a digest** (`mcr.microsoft.com/dotnet/sdk:8.0@sha256:…`).

(The defaults in the template use tags only to stay legible; production callers should
supply digests — mirroring the GitHub header guidance.)

## Behavioural equivalence vs the GitHub workflow

| Concern | GitHub (`vouchfx-run.yml`) | GitLab (`vouchfx-run.gitlab-ci.yml`) |
|---|---|---|
| Distribution | reusable workflow (`on: workflow_call`, called via `uses:`) | `include`-able template (`.vouchfx-run` base + `vouchfx-run` job) |
| Config knobs | `inputs:` | CI/CD `variables:` |
| Scenario path | `scenario-path` (default `.`) | `VOUCHFX_SCENARIO_PATH` (default `.`) |
| vouchfx source | `vouchfx-repo` (default `${{ github.repository }}`) | `VOUCHFX_REPO_URL` (default `$CI_REPOSITORY_URL`) |
| vouchfx ref | `vouchfx-ref` (default `${{ github.sha }}`) | `VOUCHFX_REF` (default `$CI_COMMIT_SHA`) |
| .NET SDK | `dotnet-version` (default `8.0.x`) via `setup-dotnet` | `VOUCHFX_DOTNET_IMAGE` (default `mcr.microsoft.com/dotnet/sdk:8.0`) as job image |
| Docker for topology | runner provides Docker (`ubuntu-latest`) | `docker:dind` service + `DOCKER_HOST`/TLS + Testcontainers host override (privileged runner; socket-bind alternative) |
| Install | checkout vouchfx → `dotnet build -c Release` | `git clone` vouchfx → `dotnet build -c Release` |
| Image pre-warm | `prewarm-images`, per-image `docker pull \|\| echo` (best-effort) | `VOUCHFX_PREWARM_IMAGES`, per-image `docker pull \|\| echo` (best-effort) |
| Run | `dotnet run --project … -c Release --no-build -- run "$SCENARIO_PATH" --junit … --html …` | identical `dotnet run … -- run "$VOUCHFX_SCENARIO_PATH" --junit … --html …` |
| `--fail-on-env-error` | `${{ inputs.fail-on-env-error && '--flag' \|\| '' }}` | appended when `VOUCHFX_FAIL_ON_ENV_ERROR` is truthy |
| `--fail-on-inconclusive` | `${{ inputs.fail-on-inconclusive && '--flag' \|\| '' }}` | appended when `VOUCHFX_FAIL_ON_INCONCLUSIVE` is truthy |
| Gating | non-zero exit fails the job; only Fail gates by default | non-zero exit fails the job; only Fail gates by default |
| Injection-safety | inputs bound to `env:`, dereferenced (not `${{ }}`-spliced) | values dereferenced as `"$VAR"` / iterated via env (not spliced) |
| Artefacts | `if: always()` upload `results.xml`+`report.html`, `retention-days: 7` | `when: always` `paths: [results.xml, report.html]`, `expire_in: 7 days` |
| Native test report | (consumer wires `dorny/test-reporter` etc.) | `reports: junit: results.xml` (native) |
| Packaging note | build-from-source; Sprint-11 packaging forward-ready | build-from-source; Sprint-11 packaging forward-ready |

## Validation status

- **yamllint:** clean against [`.yamllint.yml`](.yamllint.yml) (line-length 120;
  `document-start` and `comments-indentation` disabled — see that file for the rationale).
- **GitLab CI JSON schema:** validates clean against the official GitLab CI schema
  (`app/assets/javascripts/editor/schema/ci.json`, JSON Schema draft-07) using `ajv`; the
  realistic consumer `include` form validates too.
- **Live verification (a real GitLab pipeline run, or the GitLab `/ci/lint` API):**
  **NOT performed — it requires a GitLab instance + token, which is not available in this
  environment.** The template is therefore **static-validated only** (yamllint + JSON
  schema + point-by-point behavioural cross-check against the GitHub workflow). A live
  pipeline / `ci/lint` run is an **infrastructure-gated follow-up**.
