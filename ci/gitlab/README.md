# vouchfx — GitLab CI template

`vouchfx-run.gitlab-ci.yml` is an **`include`-able GitLab CI/CD template** that runs a
vouchfx `.e2e.yaml` suite end-to-end against an orchestrated container topology and
publishes the JUnit + HTML reports as job artefacts (with the JUnit results surfaced
natively in the GitLab pipeline / merge-request UI).

It is the **GitLab equivalent of the reusable GitHub Actions workflow**
[`.github/workflows/vouchfx-run.yml`](../../.github/workflows/vouchfx-run.yml)
and is **behaviourally equivalent** to it — same inputs (as
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
point the job's Docker client at it. The default SDK image lacks a Docker client, so
the template installs Docker CLI 27.5.1 from Docker's official static archive after
checking the archive against a pinned SHA-256. Custom images that already provide a
client retain it. A job that overrides `before_script` must keep the template's steps
first, as `- !reference [.vouchfx-run, before_script]`, because GitLab replaces the list
rather than merging it and the default image then has no Docker client (exit 127). The
[CI integration reference](../../docs/ci-integration.md#docker-in-docker-and-the-privileged-runner-requirement)
shows the full form.

The template copies the prepared checkout into a second SDK container and builds and
runs vouchfx there with host networking and the daemon's Unix socket. Setup scripts run
before that copy, so generated fixture paths remain available at the same absolute paths
inside the runner. Aspire/DCP's loopback proxy endpoints then share the daemon's network
namespace; merely setting `TESTCONTAINERS_HOST_OVERRIDE` in the sibling job does not
provide this. The outer connection keeps TLS verification enabled. The inner runner
receives the job's exported environment through a private temporary file, resets Docker
access to the local socket, and is removed when the script exits. The build runs before
the suite in its own `docker exec`; if it fails, the job exits with the build's code,
copies no reports and prints no verdict legend. JUnit and HTML reports are copied back
before the original engine exit code gates the job. A report the engine did not write, as
when it discovers no scenario and exits 0, produces a warning and leaves that exit code
unchanged.

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

vouchfx is published as a **NuGet global tool** and can be installed locally with
`dotnet tool install --global vouchfx` (the latest stable release, recommended for running
suites locally). This template nevertheless **deliberately builds vouchfx from source** at a
pinned reference: it `git clone`s `VOUCHFX_REPO_URL` at `VOUCHFX_REF` into `.vouchfx-src`,
runs `dotnet build … -c Release`, then invokes the CLI with `dotnet run --project … --no-build`.
Pinning the `include:` `ref:` and `VOUCHFX_REF` together keeps the engine and template in
lock-step; a SHA or tag-pinned source build is an immutable supply-chain reference, exactly as
in the GitHub template. A consumer tracking a released engine pins `VOUCHFX_REF` to the release
tag (e.g. the stable `v1.0.0` baseline), still as a source build. A packaged-tool install could become an
optional mode later; the install step is intentionally the only thing that would change.

## Exit-code gating (verdict taxonomy §12.1)

| Exit | Verdict | Gates CI? |
|---|---|---|
| 0 | Pass (or un-gated EnvError / Inconclusive) | no |
| 1 | **Fail** | **always** |
| 2 | usage error | yes (bad arguments) |
| 3 | EnvironmentError | with `VOUCHFX_FAIL_ON_ENV_ERROR` truthy, or unconditionally for unconfirmed declared security |
| 4 | Inconclusive / refusal | with `VOUCHFX_FAIL_ON_INCONCLUSIVE` truthy, or an unconditional refusal/security check |

A non-zero exit fails the job — so a product **Fail** gates the consumer's CI by default,
genuine runtime infra errors / inconclusives gate only on opt-in. Parse failures, typed authoring/schema/compilation
refusals, an Inconclusive suite with no executed scenario, provider/engine-surface refusals and unconfirmed
declared security prevent exit 0 regardless of those flags; an already-selected non-zero code is preserved.
See the [exit-code policy](../../docs/ci-integration.md) for precedence. The run step also echoes the vouchfx exit code
and its taxonomy legend on every run, pass or fail, so the job log always records both the
verdict token and the numeric code.

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
| Docker for topology | runner provides Docker (`ubuntu-latest`) | `docker:dind` service + `DOCKER_HOST`/TLS for the job; the engine runs in an owned SDK container on the daemon's network and Unix socket (privileged runner; socket-bind alternative) |
| Install | checkout vouchfx → `dotnet build -c Release` | `git clone` vouchfx → `dotnet build -c Release` |
| Image pre-warm | `prewarm-images`, per-image `docker pull \|\| echo` (best-effort) | `VOUCHFX_PREWARM_IMAGES`, per-image `docker pull \|\| echo` (best-effort) |
| Run | `dotnet run --project … -c Release --no-build -- run "$SCENARIO_PATH" --junit … --html …` | identical `dotnet run … -- run "$VOUCHFX_SCENARIO_PATH" --junit … --html …` |
| `--fail-on-env-error` | `${{ inputs.fail-on-env-error && '--flag' \|\| '' }}` | appended when `VOUCHFX_FAIL_ON_ENV_ERROR` is truthy |
| `--fail-on-inconclusive` | `${{ inputs.fail-on-inconclusive && '--flag' \|\| '' }}` | appended when `VOUCHFX_FAIL_ON_INCONCLUSIVE` is truthy |
| Gating | non-zero exit fails the job; only Fail gates by default | non-zero exit fails the job; only Fail gates by default |
| Injection-safety | inputs bound to `env:`, dereferenced (not `${{ }}`-spliced) | values dereferenced as `"$VAR"` / iterated via env (not spliced) |
| Artefacts | `if: always()` upload `results.xml`+`report.html`, `retention-days: 7` | `when: always` `paths: [results.xml, report.html]`, `expire_in: 7 days` |
| Native test report | (consumer wires `dorny/test-reporter` etc.) | `reports: junit: results.xml` (native) |
| Packaging note | build-from-source by design; packaged NuGet tool available for local use | build-from-source by design; packaged NuGet tool available for local use |

## Validation status

- **yamllint:** clean against [`.yamllint.yml`](.yamllint.yml) (line-length 120;
  `document-start` and `comments-indentation` disabled — see that file for the rationale).
- **GitLab CI JSON schema:** validates clean against the official GitLab CI schema
  (`app/assets/javascripts/editor/schema/ci.json`, JSON Schema draft-07) using `ajv`; the
  realistic consumer `include` form validates too.
- **Live GitLab verification, 20 October 2026 (UTC):** the `/ci/lint` API accepted
  the reference consumer with zero errors and warnings on GitLab SaaS `19.6.0-pre`
  (revision `27a841f4bf2`). The consumer imported the published template at
  `69cc2a2e4747415a3cea7ac6d1a92b266a67358b` and built source candidate
  `574863fa6242aa6dff2b87ac7d6a5a87b1c84521` on privileged SaaS Linux
  `saas-linux-small-amd64` Docker-in-Docker runners.
  [Positive reference pipeline 2969645952](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/pipelines/2969645952)
  succeeded: its sole smoke job `17331882403` produced PASS and exit 0, with downloaded
  JUnit counts `(tests=1, failures=0, errors=0)` and a nonempty HTML report. The consumer
  explicitly excluded the negative controls for this positive-only run.
  [Negative-control pipeline 2969340980](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/pipelines/2969340980)
  failed as intended: smoke produced PASS/0, the product control produced FAIL/1,
  and the invalid-image environment control produced ENV_ERROR/0 under default gating.
  All three retained downloadable JUnit and HTML reports; downloaded JUnit counts were
  respectively `(1, 0, 0)`, `(1, 1, 0)`, and `(1, 0, 1)`.
  The private acceptance project requires authenticated access. These runs validated a
  reviewed candidate. Official-source acceptance, recorded in
  [PR #638](https://github.com/tomas-rampas/vouchfx/pull/638), then built official Core 1.1.0
  commit `576780f6eba00d6919757b240ebdac0b8767d849` with the same template:
  [positive pipeline 2972649303](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/pipelines/2972649303)
  and [negative-control pipeline 2972649378](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/pipelines/2972649378)
  verified PASS/0, FAIL/1 and ENV_ERROR/0 with downloadable HTML and JUnit reports.
- **Shipped template:** the template file at `69cc2a2e` is byte-identical to the one Core
  1.2.0 (`0af21de5bd0a4e91ca002e03e6c256bc4e8ca4e8`) released, which closes the Docker-client
  bootstrap and daemon loopback differences recorded in
  [#639](https://github.com/tomas-rampas/vouchfx/issues/639). Since then the template keeps
  the engine's exit code when a report is missing, instead of exiting 2; builds vouchfx in
  its own `docker exec` before the suite, so a failed build keeps its exit code and prints
  no verdict legend; and no longer sets `TESTCONTAINERS_HOST_OVERRIDE`, which the runner
  script already cleared before starting the engine. These three post-release changes
  remain under `Unreleased`; the live acceptance below does not make them part of Core 1.2.0.

### Live verification, 23 October 2026 (UTC)

Template and engine source were both pinned to
`93881722b5ccba2631814394d76f10e7dd36568e`; the private acceptance consumer was
`647309016848428670698e66601d8feda91e6ba6`. GitLab CI lint returned zero errors and warnings.
The consumer retained the native template's preparation and execution scripts, adding only
control selection, disposable source changes for build failures, and evidence collection.

[Positive pipeline 2982288601](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/pipelines/2982288601)
succeeded with all three controls.
[Negative-control pipeline 2982296539](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/pipelines/2982296539)
failed as intended, with all five controls completed. The project and evidence links require
authenticated access. JUnit counts below are `(tests, failures, errors)`.

| Control | Job | Measured result | Downloaded reports |
|---|---|---|---|
| Smoke | [17424152429](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424152429) | PASS; engine/job exit 0 | JUnit `(1, 0, 0)`; nonempty HTML |
| No scenarios | [17424152430](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424152430) | Engine/job exit 0; two missing-report warnings | No JUnit or HTML, as expected |
| Host-override sentinel | [17424152431](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424152431) | PASS; engine/job exit 0 | JUnit `(1, 0, 0)`; nonempty HTML |
| Product assertion failure | [17424221269](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424221269) | FAIL; engine/job exit 1 | JUnit `(1, 1, 0)`; nonempty HTML |
| Environment error, default policy | [17424221270](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424221270) | ENV_ERROR; engine/job exit 0 | JUnit `(1, 0, 1)`; nonempty HTML |
| Environment error, strict policy | [17424221271](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424221271) | ENV_ERROR; engine/job exit 3 | JUnit `(1, 0, 1)`; nonempty HTML |
| Restore failure | [17424221272](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424221272) | NuGet `NU1101`; build/job exit 1; no suite run or verdict legend | No JUnit or HTML, as expected |
| Compile failure | [17424221273](https://gitlab.com/mcquant/vouchfx-ci-acceptance/-/jobs/17424221273) | C# `CS1029`; build/job exit 1; no suite run or verdict legend | No JUnit or HTML, as expected |

The restore control added an unavailable package and an empty local restore source; the compile
control added an intentional `#error`. Both changed only the job's disposable engine clone.
Downloaded evidence retained the source change and hash. Neither control substituted an executable
or a synthetic exit code.

All eight jobs recorded GitLab `19.6.0-pre`, SaaS Linux `saas-linux-small-amd64` Docker-in-Docker
runners (`19.6.0~pre.2364.gcc7d9535`, Linux/amd64), SDK `8.0.426`, and Docker client/server
`27.5.1`. The authenticated GitLab version probe reported revision `dbcf8f9a71a` before the runs.
The SDK image was
`mcr.microsoft.com/dotnet/sdk:8.0@sha256:738f72aff955d5c5690e06b6fc8ecd1b90ead564099a257ac528ff720b8da85f`.

After the native template's cleanup, all eight jobs' container, network and volume ID snapshots
exactly matched their pre-run baselines. All six remaining/missing-baseline counts were zero at
the first post-job sample; this does not establish behaviour after that sample. The observer
deleted nothing. Cleanup evidence was checked separately because a failing GitLab `after_script`
does not change the main job's result.

The sentinel smoke establishes compatibility with a consumer-set host override, not direct
observation of the inner environment. The pinned template separately unsets that variable before
both native execs. Self-managed Kubernetes and socket-bind runners remain unmeasured.
