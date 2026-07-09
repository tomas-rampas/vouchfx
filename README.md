# vouchfx

**End-to-end integration testing for distributed systems, authored in YAML.**

## About

vouchfx is a .NET 8 testing platform for distributed systems that lets teams author end-to-end tests in
declarative YAML, compile them into memory-safe executable workflows, and run them against real
containerised topologies with deterministic orchestration and clear verdict reporting.

vouchfx compiles declarative `.e2e.yaml` tests into Turing-complete C# (CSX), runs them memory-safely
through Roslyn, and orchestrates the required container topology with **.NET Aspire + Testcontainers**.
It tests one business transaction as it crosses a REST call, a Kafka event, a database mutation and an
outbound webhook — the seams where distributed systems actually break.

It is **not** a unit-test framework and **not** a UI/browser tool.

```
author .e2e.yaml → validate vs JSON Schema → compile YAML→AST→CSX→Roslyn delegate (once)
   → orchestrate topology via Aspire → execute delegate against discovered endpoints
   → collect verdict → unload context → reset
```

## Status

> **vouchfx is in public pre-release: `v1.0.0-alpha.x` builds (currently `v1.0.0-alpha.2`) are live on [NuGet.org](https://www.nuget.org/packages/vouchfx/) and [GitHub Releases](https://github.com/tomas-rampas/vouchfx/releases), and v1.0 GA is in preparation.** Install the CLI with `dotnet tool install --global vouchfx --prerelease`. The engine compiles `.e2e.yaml` declarative integration
> tests into memory-safe, Turing-complete C# (CSX) via Roslyn, orchestrates distributed topologies
> with Aspire and Testcontainers, executes all twenty-five Core providers across database (PostgreSQL, SQL Server, MySQL, MongoDB, DynamoDB),
> cache and search (Redis, Elasticsearch), messaging (Kafka, RabbitMQ, NATS, Azure Service Bus, Redis Streams), metrics (Prometheus), storage (S3), HTTP (REST, SOAP), distributed tracing (OTLP), webhooks, and scripts end-to-end with
> declarative seeding, `${secret:env/…}` and `${secret:vault/…}` resolution, engine-owned RETRY polling (Polly v8)
> with per-attempt timeline and captured-variable provenance rendering, and emits a schema-versioned JSON Lines event stream persisted to a file (`--events`) and rendered to the terminal (with a plain-text `--no-decorations` mode for WCAG 1.4.1 screen-reader compatibility), a WCAG 2.1 AA self-contained HTML report, and JUnit XML for CI. The v1 JSON Schema and v1 provider/event contract are frozen, the Provider SDK (`Platform.Sdk`) is packable with developer guidance and worked-example providers (publication to NuGet.org is wired into the release pipeline and ships with the next tagged pre-release), and scenarios can run in parallel with topology-per-scenario isolation (`vouchfx run --parallel <n>`) or in watch mode for local iteration (`vouchfx run --watch`). A headless CLI runner discovers and selects scenarios by tag, owner, path, or git change-set, with per-scenario isolation and taxonomy-aware exit codes (0 = Pass/EnvironmentError/Inconclusive by default; 1 = Fail; 3 = EnvironmentError if `--fail-on-env-error`; 4 = Inconclusive if `--fail-on-inconclusive`). A VSCode extension provides schema-driven YAML autocomplete and validation, C# syntax highlighting in `script.csharp` blocks, and Test Explorer integration with per-step verdicts and line-level failure decoration (see [`docs/accessibility.md`](docs/accessibility.md) for the WCAG 2.1 AA conformance record; full in-block C# IntelliSense is a documented fast-follow). The four-technology reference scenario (REST, Kafka, PostgreSQL, webhook) is green from both the VSCode editor and the CLI; the secret-redaction path has passed a penetration test; and the release pipeline has shipped the `v1.0.0-alpha.1`/`v1.0.0-alpha.2` pre-releases for real — a signed nupkg on NuGet.org (Trusted Publishing, no long-lived keys), per-OS self-contained archives and MSI/deb/pkg installers, CycloneDX SBOMs, SLSA provenance and keyless cosign signatures on every artefact, with certificate-based signing (Authenticode, notarisation, GPG) secret-gated until those certificates are provisioned.
> The [community provider hub](https://github.com/tomas-rampas/vouchfx-providers) is live with the community registry, hub-hosted community providers, and the maintainer-awarded Vouched badge, and the [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples) repository provides real-world sample applications and end-to-end test suites. Remaining for v1.0 GA: validation with pilot teams, and stabilisation of the Provider SDK at 1.0.0 final (see the [roadmap](docs/roadmap.md)). The engine targets **.NET 8 LTS**, shipped as a `dotnet` global tool plus a VSCode extension.

## How it works

Five layers, each exposing a narrow typed contract upward:

1. **Authoring** — the `.e2e.yaml` grammar, JSON Schema, VSCode/LSP tooling.
2. **Compilation** — parser → AST → CSX → Roslyn delegate, compiled **once**.
3. **Orchestration** — Aspire AppHost, Testcontainers, health-gated startup.
4. **Execution** — delegate host, collectible `AssemblyLoadContext`, Polly v8 resilience.
5. **Infrastructure fabric** — local Docker, SaaS, or on-prem Kubernetes.

The contract that makes a suite run unchanged across local / SaaS / CI: the compiled delegate depends
**only** on a typed `ScriptGlobalVariables`, and orchestration is its sole producer.

### Providers

Steps are typed `<family>.<provider>` — *family* is intent, *provider* is technology
(`db-assert.postgres`, `mq-publish.kafka`). Providers are **compile-time, source-level plugins**: add
a project, implement the contract, and a reflective registry discovers it at startup — no runtime
loader, no sandbox. Twenty-five **Core** providers are now delivered across eleven step families:
`http.rest`, `http.soap`; `db-assert.postgres`, `db-assert.sqlserver`, `db-assert.mysql`, `db-assert.mongodb`,
`db-assert.dynamodb`; `cache-assert.redis`, `cache-assert.elasticsearch`; `mq-publish.kafka`, `mq-publish.rabbitmq`,
`mq-publish.nats`, `mq-publish.azureservicebus`, `mq-publish.redis`; `mq-expect.kafka`, `mq-expect.rabbitmq`,
`mq-expect.nats`, `mq-expect.azureservicebus`, `mq-expect.redis`; `webhook-listen.http`; `mail-expect.smtp`;
`metrics-assert.prometheus`; `storage-assert.s3`; `trace-expect.otlp`; and `script.csharp`. All are governed across two tiers (Core / Community), with the maintainer-awarded Vouched badge — all Apache-2.0.

## A test, in shape

A `.e2e.yaml` file has four top-level sections; only `steps` is mandatory:

- **`metadata`** — name / owner / tags / description (drives runner selection and reporting).
- **`environment`** — `services` (system under test), `dependencies` (managed Aspire resources), an
  optional `seed`, and registry/pull overrides.
- **`variables`** — constants pre-loaded into the shared context.
- **`steps`** — ordered, each with an `id`, `type`, optional `capture`, `verifyMode`
  (`IMMEDIATE` / `RETRY`), `timeout`, `continueOnFailure`.

State threads forward between steps through `capture` and `{placeholder}` substitution. RETRY polling
is engine-owned — authors never write `Thread.Sleep`.

## Verdicts

Four outcomes are kept distinct everywhere (taxonomy, reporting, exit codes): **Pass**, **Fail**,
**Environment error** (infrastructure), **Inconclusive** (timeout / unmet capture). **Only `Fail`
breaks CI by default** — conflating an environment error with a defect destroys trust in the tool.

### Accessibility

Each of the four verdicts is **always** rendered with a distinct text token (`PASS`, `FAIL`, `ENV_ERROR`, `INCONCLUSIVE`) — a WCAG 1.4.1 guarantee that verdicts are never distinguished by colour alone. When the output is an interactive terminal *and* the `NO_COLOR` environment variable is unset *and* `--no-decorations` is not passed, each verdict also receives an optional ASCII shape glyph and ANSI colour: Pass `[+]` green, Fail `[x]` red, Environment-error `[!]` yellow, Inconclusive `[?]` blue. The glyph is a shape cue independent of colour (for colour-blind readers); the colour is a redundant, sighted-only convenience. Piped, redirected, CI, and test output is plain text by default. Pass `--no-decorations` or set `NO_COLOR=1` to force plain text on any terminal. See `docs/accessibility.md` for the complete WCAG 2.1 AA conformance record covering both the terminal and HTML report renderers.

## Building and testing

**Prerequisites:** the **.NET 8 SDK** (pinned in `global.json`; install
[.NET 8.0 LTS](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)) and, for the Aspire
integration tests only, a running **Docker** daemon (the unit tests need neither).

```bash
# Build — C# 11, nullable enabled, warnings-as-errors (output has zero warnings).
dotnet build vouchfx.sln

# Unit tests — fast, no Docker.
dotnet test vouchfx.sln --filter "requires!=docker"

# Integration tests — require a running Docker daemon (Aspire topology + collectible loading).
dotnet test vouchfx.sln --filter "requires=docker"

# Formatting gate.
dotnet format --verify-no-changes
```

Continuous integration (GitHub Actions, `.github/workflows/build.yml`) runs a blocking **build** job
(build + format + unit tests), a blocking **memory-leak** job that runs the heap-measurement harness
over 5,000 load-unload cycles (see [`docs/memory-harness.md`](docs/memory-harness.md) for the tool, how
to run it locally, and a sample passing report), and a forward-looking **integration** (Docker) job.

## Getting started

**New to vouchfx?** Install the CLI from NuGet.org:

```bash
dotnet tool install --global vouchfx --prerelease
```

Then start with the [Getting Started guide](docs/getting-started.md), which walks you through
your first test in 60 minutes: checking prerequisites, installing the tool (or building from source),
authoring a minimal `.e2e.yaml` file, running it, and interpreting the verdict. It covers the four verdict types, how to
generate HTML and JUnit reports, and where to find the full DSL spec, recipes, and architecture docs.

### Example scenarios

The repository includes worked scenarios demonstrating core features:

- **[`examples/reference/reference.e2e.yaml`](examples/reference/reference.e2e.yaml)** — **The canonical four-technology reference scenario.** One business transaction spanning all engine capabilities: REST call (`http.rest` + `capture`), database mutation (`script.csharp` + `db-assert.postgres` over a seeded table), Kafka publish-and-consume (`mq-publish.kafka` + `mq-expect.kafka` with `verifyMode: RETRY`), and outbound webhook simulation (`script.csharp` → `webhook-listen.http` with RETRY). It also threads a `${secret:env/…}` bearer token and demonstrates cross-step placeholder substitution. See [`examples/reference/README.md`](examples/reference/README.md) for a walkthrough.
- **[`examples/ci-reference/smoke.e2e.yaml`](examples/ci-reference/smoke.e2e.yaml)** — A minimal happy-path scenario used in CI integration tests.
- **[`examples/getting-started/hello-world.e2e.yaml`](examples/getting-started/hello-world.e2e.yaml)** — A first-time introductory scenario from the Getting Started guide.

**For real-world sample applications and comprehensive end-to-end test suites**, see the **[vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples)** repository, which includes working microservices in C#, Python, and Java with corresponding test suites demonstrating provider usage across multiple technologies.

### CI integration with GitHub Actions

vouchfx ships a **reusable GitHub Actions workflow** (`.github/workflows/vouchfx-run.yml`) that runs a vouchfx `.e2e.yaml` suite end-to-end against an orchestrated container topology and publishes JUnit and HTML artefacts. Any repository can call this workflow to integrate vouchfx tests into its CI pipeline.

**Quick start.** In your repository's workflow, add:

```yaml
jobs:
  vouchfx-e2e:
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@<commit-sha>
    with:
      scenario-path: ./tests/e2e
      fail-on-env-error: false
```

Replace `<commit-sha>` with a full 40-character commit SHA (not a branch or tag, for supply-chain hygiene).

**Workflow inputs.** The reusable workflow accepts these configuration inputs:

| Input | Type | Default | Purpose |
|---|---|---|---|
| `scenario-path` | string | `.` | Directory (relative to the caller's checkout) to search for `.e2e.yaml` scenarios. Searched recursively. |
| `vouchfx-repo` | string | `${{ github.repository }}` | The `owner/repo` of the vouchfx repository to build from source. Override to track a fork, or to pin a released version (e.g. the `v1.0.0-alpha.2` tag). |
| `vouchfx-ref` | string | `${{ github.sha }}` | The git ref (commit SHA, tag, or branch) of `vouchfx-repo` to build. Recommended: a full commit SHA for supply-chain repeatability. |
| `dotnet-version` | string | `8.0.x` | The .NET SDK version to install. vouchfx targets .NET 8 LTS. |
| `fail-on-env-error` | boolean | `false` | When `true`, an environment-error verdict (unhealthy container, image-pull/seed failure) fails the job with exit code 3. Off by default — only `Fail` breaks CI. |
| `fail-on-inconclusive` | boolean | `false` | When `true`, an inconclusive verdict (timeout, unmet captures) fails the job with exit code 4. Off by default — only `Fail` breaks CI. |
| `prewarm-images` | string | (empty) | Optional newline-separated list of container images (one per line) to `docker pull` before the run, to warm the Docker cache and mitigate Aspire/DCP's ~20 second per-resource cold-start watchdog. Each pull is best-effort and non-fatal. Syntax: one image per line (e.g., `traefik/whoami:latest`). |
| `runs-on` | string | `ubuntu-latest` | The GitHub Actions runner label to use. Must provide Docker; `ubuntu-latest` does. |

**Installation.** The vouchfx CLI is packaged as a `dotnet global tool` (`ToolCommandName: vouchfx`), live on NuGet.org: `dotnet tool install --global vouchfx --prerelease` (or from a private feed). For development builds, the workflow can alternatively build from source by checking out `vouchfx-repo` at the requested `vouchfx-ref` and running `dotnet build -c Release`. The tool depends on the Aspire orchestration packages being present in the per-user NuGet cache; any developer or CI environment that has built an Aspire app or resolved the engine's dependencies has them. For machines with only the OS (no .NET SDK), use the self-contained per-OS archives and installers attached to each [GitHub release](https://github.com/tomas-rampas/vouchfx/releases).

**Exit-code gating semantics.** The workflow respects the verdict taxonomy (§12.1 of the Architecture Blueprint) to distinguish infrastructure breakage from product defects:

- **Exit 0 (success)** — By default, a passing suite or one with only EnvironmentError / Inconclusive verdicts.
- **Exit 1 (Fail)** — One or more scenarios failed. **Always breaks CI** — this is the default gating.
- **Exit 3 (EnvironmentError)** — Infrastructure breakage (unhealthy container, image-pull failure, seed failure). Breaks CI only when `fail-on-env-error: true`.
- **Exit 4 (Inconclusive)** — Engine could not decide (timeout, partition, unmet capture). Breaks CI only when `fail-on-inconclusive: true`.

The distinction lets CI systems handle each outcome independently: fail the build on a product `Fail`, page on-call for `EnvironmentError`, and escalate `Inconclusive` to reliability engineering.

**Artefacts.** The workflow always runs the suite and **always publishes the reports** (via `if: always()`) even when the run fails, so artefacts are available precisely when a suite does not pass. Reports are stored under the job's `vouchfx-reports` artefact name and include:

- **`results.xml`** — JUnit XML results for CI ingestion; the four verdicts map to distinct JUnit primitives (Fail → `<failure>`, EnvironmentError → `<error>`, Inconclusive → `<skipped>`).
- **`report.html`** — A self-contained HTML report with polling timelines, captured-variable provenance, failed-step diffs, and the reproducibility envelope, with no secret values embedded.

**Supply-chain hygiene.** For production use, follow these pinning recommendations:

1. **Pin the `uses:` reference to a full commit SHA**, not a moving branch or tag:
   ```yaml
   uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0
   ```
   A branch/tag ref lets the workflow definition change underneath you; a SHA is immutable.

2. **Pin `vouchfx-ref` to a commit SHA or release tag**, never a branch:
   ```yaml
   vouchfx-ref: a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0
   ```

3. **Pin each `prewarm-images` entry to an immutable image digest**, not a floating tag:
   ```yaml
   prewarm-images: |
     traefik/whoami@sha256:abc123...
     postgres@sha256:def456...
   ```
   A digest guarantees you pull the exact image you reviewed; `:latest` can change.

**Example.** See [`.github/workflows/vouchfx-run-reference.yml`](.github/workflows/vouchfx-run-reference.yml) for a worked example that calls the reusable workflow against this repository's own minimal reference suite (`examples/ci-reference/smoke.e2e.yaml`), proving the workflow runs a real suite green and publishes artefacts end-to-end.

### CI integration with GitLab CI

vouchfx ships an **`include`-able GitLab CI/CD template** (`ci/gitlab/vouchfx-run.gitlab-ci.yml`) that runs a vouchfx `.e2e.yaml` suite end-to-end against an orchestrated container topology and publishes JUnit and HTML artefacts with native GitLab test-report rendering. It is the **GitLab equivalent of the reusable GitHub Actions workflow**, with identical inputs (as CI/CD variables), build-from-source install, exit-code gating, and artefact-upload behaviour.

**Quick start.** In your repository's `.gitlab-ci.yml`, add:

```yaml
include:
  - project: tomas-rampas/vouchfx
    ref: <40-char-commit-sha>
    file: /ci/gitlab/vouchfx-run.gitlab-ci.yml

vouchfx-run:
  variables:
    VOUCHFX_SCENARIO_PATH: ./tests/e2e
    VOUCHFX_FAIL_ON_ENV_ERROR: "false"
```

Replace `<40-char-commit-sha>` with a full 40-character commit SHA (not a branch or tag, for supply-chain hygiene).

**Configuration variables.** The template accepts these configuration variables (the GitLab analogue of GitHub workflow inputs):

| Variable | Type | Default | Purpose |
|---|---|---|---|
| `VOUCHFX_SCENARIO_PATH` | string | `.` | Directory (relative to the project root) to search recursively for `.e2e.yaml` scenarios. |
| `VOUCHFX_REPO_URL` | string | `$CI_REPOSITORY_URL` | Git URL of the vouchfx repository to build from source. Defaults to the calling project; override to track a fork. |
| `VOUCHFX_REF` | string | `$CI_COMMIT_SHA` | Git ref (commit SHA, tag, or branch) of `VOUCHFX_REPO_URL` to build. Recommended: a full commit SHA for supply-chain repeatability. |
| `VOUCHFX_DOTNET_IMAGE` | string | `mcr.microsoft.com/dotnet/sdk:8.0` | .NET 8 SDK container image the job runs in. vouchfx targets .NET 8 LTS. |
| `VOUCHFX_FAIL_ON_ENV_ERROR` | string | `"false"` | When truthy, an environment-error verdict (unhealthy container, image-pull/seed failure) fails the job with exit code 3. Off by default — only `Fail` breaks CI. |
| `VOUCHFX_FAIL_ON_INCONCLUSIVE` | string | `"false"` | When truthy, an inconclusive verdict (timeout, unmet captures) fails the job with exit code 4. Off by default — only `Fail` breaks CI. |
| `VOUCHFX_PREWARM_IMAGES` | string | (empty) | Optional whitespace/newline-separated list of container images to `docker pull` before the run, to warm the Docker cache and mitigate Aspire/DCP's ~20 second per-resource cold-start watchdog. Each pull is best-effort and non-fatal. Pin each entry to an immutable image digest. |

**Docker-in-Docker and the privileged-runner requirement.** vouchfx stands up an Aspire/Testcontainers container topology, so the job needs a Docker daemon. The template uses the standard GitLab **Docker-in-Docker (dind)** pattern with a `docker:dind` service. **Important caveat:** dind requires a **privileged runner**. The `docker:dind` service only starts on a GitLab Runner configured with `privileged = true` (Docker executor) or an equivalently-privileged Kubernetes executor; gitlab.com's shared SaaS Linux runners provide this. A self-managed runner must be explicitly configured for it.

**Alternative for a non-privileged runner.** If your runner cannot run privileged dind, use a **socket-bind runner**: mount the host daemon socket into the build (`volumes = ["/var/run/docker.sock:/var/run/docker.sock", …]` in the runner config), then drop the `services:` block and `dind`/TLS variables and set `DOCKER_HOST: "unix:///var/run/docker.sock"`. Socket-bind trades isolation for not needing privileged mode — choose per your security posture.

**Installation.** The vouchfx CLI is packaged as a `dotnet global tool` (`ToolCommandName: vouchfx`). The template installs it via `dotnet tool install` from a NuGet feed (once released to nuget.org or a private feed). For development or pre-release builds, the template can alternatively build from source by cloning `VOUCHFX_REPO_URL` at the requested `VOUCHFX_REF` and running `dotnet build -c Release`. The tool depends on the Aspire orchestration packages being present in the per-user NuGet cache; any CI environment with an Aspire/Testcontainers dependency graph has them. For machines with only the OS (no .NET SDK), use the self-contained per-OS executable (forthcoming).

**Exit-code gating semantics and artefacts.** The template respects the verdict taxonomy (§12.1 of the Architecture Blueprint) and always publishes reports (via `when: always`) even when the run fails — artefacts are available precisely when a suite does not pass:

- **Exit 0 (success)** — By default, a passing suite or one with only EnvironmentError / Inconclusive verdicts.
- **Exit 1 (Fail)** — One or more scenarios failed. **Always breaks CI** — this is the default gating.
- **Exit 3 (EnvironmentError)** — Infrastructure breakage. Breaks CI only when `VOUCHFX_FAIL_ON_ENV_ERROR` is truthy.
- **Exit 4 (Inconclusive)** — Engine could not decide. Breaks CI only when `VOUCHFX_FAIL_ON_INCONCLUSIVE` is truthy.

Reports are stored under the job's default artefact path and include:

- **`results.xml`** — JUnit XML results for CI ingestion; surfaced natively in GitLab's pipeline and merge-request test-report UI.
- **`report.html`** — A self-contained HTML report with polling timelines, captured-variable provenance, failed-step diffs, and the reproducibility envelope, with no secret values embedded.

**Supply-chain hygiene.** For production use, follow these pinning recommendations:

1. **Pin the `include:` `ref:` to a full commit SHA**, not a moving branch or tag.
2. **Pin `VOUCHFX_REF` to a commit SHA or release tag**, never a branch.
3. **Pin each `VOUCHFX_PREWARM_IMAGES` entry to an immutable image digest** (`name@sha256:…`), not a floating tag.
4. **Pin `VOUCHFX_DOTNET_IMAGE` to a digest** (`mcr.microsoft.com/dotnet/sdk:8.0@sha256:…`) rather than the floating tag.

**Verification status (important).** The GitLab template is **static-validated only** (yamllint + GitLab CI JSON schema + behavioural-equivalence cross-check against the GitHub workflow), but has **not been run on a live GitLab instance** — a live pipeline / `ci/lint` run is an infrastructure-gated follow-up. The one substantive risk to verify when running live is whether vouchfx's **Aspire/DCP-managed containers are reachable under sibling Docker-in-Docker** (the template sets `TESTCONTAINERS_HOST_OVERRIDE=docker`, but DCP may resolve endpoints differently than raw Testcontainers) — that dind-to-DCP networking is the primary unknown.

See [`ci/gitlab/vouchfx-run.gitlab-ci.yml`](ci/gitlab/vouchfx-run.gitlab-ci.yml) and [`ci/gitlab/README.md`](ci/gitlab/README.md) for the complete reference and implementation details.

## Running tests with the CLI

Once built, the `vouchfx` command discovers and runs tests. Place `.e2e.yaml` files anywhere in your project and run:

```bash
# Run all scenarios in the current directory and below
vouchfx run

# Run scenarios in a specific directory
vouchfx run ./tests/e2e

# Select by metadata: tag (repeatable, OR within the option)
vouchfx run --tag smoke --tag integration

# Select by owner (repeatable, OR within the option)
vouchfx run --owner alice --owner bob

# Select by file path (glob pattern: *, **, or substring match)
vouchfx run --path "**/orders/*"

# Select scenarios that changed since a git reference (plus dirty tree)
vouchfx run --changed-since main

# Combine filters (all must match — AND across dimensions)
vouchfx run ./tests --tag integration --owner team-a --changed-since HEAD~1

# Run scenarios in parallel (each owning its own container topology)
vouchfx run --parallel 2

# Watch a single file for changes and re-run automatically (topology re-used for steps-only edits)
vouchfx run ./tests/users.e2e.yaml --watch

# Write a self-contained HTML report to disk
vouchfx run ./tests --html ./report.html

# Write a JUnit XML results file for CI ingestion
vouchfx run ./tests --junit ./results.xml

# Write the raw JSON Lines event stream to disk for machine-readable consumption
vouchfx run ./tests --events ./events.jsonl

# Render the terminal report as plain text (no colour / shape glyph) — WCAG 1.4.1 compliant
vouchfx run ./tests --no-decorations

# Run with both reports and taxonomy-aware CI gating (fail on environment errors or inconclusive results)
vouchfx run ./tests --html ./report.html --junit ./results.xml --fail-on-env-error --fail-on-inconclusive

# Run with selective CI gating (for example, fail on infra breakage but not timeouts)
vouchfx run ./tests --fail-on-env-error
```

The runner exits with a code that reflects the verdict taxonomy:

| Exit code | Verdict | Condition | Opt-in flag |
|---|---|---|---|
| **0** | Success | Pass, or EnvironmentError/Inconclusive (off by default) | – |
| **1** | Fail | One or more scenarios failed (a genuine defect) | – |
| **2** | UsageError | Bad arguments, missing path, `--watch`+`--parallel` | – |
| **3** | EnvironmentError | Infrastructure breakage (unhealthy container, image-pull/seed failure) | `--fail-on-env-error` |
| **4** | Inconclusive | Engine could not decide (timeout, partition outlasted grace, upstream capture unmet) | `--fail-on-inconclusive` |

By default, **only Fail (1) breaks CI** — environment errors and inconclusive results exit 0 unless you opt in via the flags above. This distinction lets you tell infrastructure breakage apart from a product defect.

```bash
# Fail breaks CI; environment errors and inconclusive results exit 0
vouchfx run ./tests

# Also gate on infrastructure failure
vouchfx run ./tests --fail-on-env-error

# Also gate on inconclusive results (timeout, unmet captures, etc.)
vouchfx run ./tests --fail-on-inconclusive

# Gate on both
vouchfx run ./tests --fail-on-env-error --fail-on-inconclusive
```

The output is a terminal report with colour-coded verdicts.

## Telemetry

vouchfx can collect **anonymous, aggregate usage telemetry** (tool/engine/.NET versions, step and scenario verdict counts, which built-in Core step kinds ran, and startup timings) to help prioritise the engine. **Telemetry is OFF by default — nothing is collected or sent unless you explicitly opt in.** Your test contents, captured values, secrets, URLs, image names, scenario names, and step IDs are **never** collected. Custom-provider step kinds are bucketed under a constant `"custom"` key so author-chosen provider ids never leave the machine. This privacy guarantee is enforced by permanent CI gates that prevent sensitive fields from being added to the telemetry allowlist.

**Three commands manage consent:**

```bash
# Opt in to telemetry
vouchfx telemetry enable

# Opt out (deletes install id and clears local outbox immediately)
vouchfx telemetry disable

# Check current consent state and where data is stored
vouchfx telemetry status
```

**Per-run suppression:**

```bash
# Suppress telemetry for this run only
vouchfx run ./tests --no-telemetry

# Or set the environment variable (e.g. for CI automation)
export VOUCHFX_NO_TELEMETRY=1
vouchfx run ./tests
```

In v1, events are persisted to a local JSON Lines file in your per-user config directory (`%APPDATA%\vouchfx\telemetry-outbox.jsonl` on Windows, `~/.config/vouchfx/telemetry-outbox.jsonl` on Linux/macOS) and are fully under your control. Optionally, you can drain the outbox to a backend via `VOUCHFX_TELEMETRY_ENDPOINT` and `VOUCHFX_TELEMETRY_TOKEN` (opt-in, HTTPS recommended, fail-silent, same allowlist); see [`docs/telemetry.md`](docs/telemetry.md) for details.

For complete information — the exact allowlist, where data is stored, the install-identifier lifecycle, backend configuration, and troubleshooting — see [`docs/telemetry.md`](docs/telemetry.md).

### Documentation roadmap

**New to vouchfx?** Here is a recommended reading order:

1. **[Getting Started](docs/getting-started.md)** — Your first test in 60 minutes.
2. **[vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples)** — Real-world sample applications and complete end-to-end test suites demonstrating common patterns across multiple languages.
3. **[Recipes](docs/recipes.md)** — Task-oriented examples: seeding with SQL, test doubles (WireMock), injecting secrets, CI integration.
4. **[Common Patterns](docs/common-patterns.md)** — Authoring patterns: the file structure, state threading with captures and placeholders, selecting scenarios, multi-step workflows, configuring services from the environment.
5. **[Troubleshooting](docs/troubleshooting.md)** — Real failure modes and how to fix them (Docker not running, the Aspire 20s cold-start gotcha, path resolution, verdicts, etc.).
6. **[Language Reference](docs/language-reference.md)** — Per-step-type field reference (required/optional, types, descriptions). Auto-generated from the schema and always in sync.
7. **[Technical Architecture Blueprint](docs/01_Technical_Architecture_and_Engineering_Blueprint.md)** — How the system works (layers, Aspire/Testcontainers, Roslyn + memory model, verdict taxonomy, provider architecture, secrets, security).
8. **[YAML DSL Specification](docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)** — The complete `.e2e.yaml` grammar and JSON Schema.
9. **[Community Provider Hub](https://github.com/tomas-rampas/vouchfx-providers)** — Community providers with the Vouched badge, with examples and the provider authoring rubric.

### Report formats

By default, `vouchfx run` outputs a terminal report only. You can optionally write a self-contained HTML report, a JUnit XML results file, and/or the raw JSON Lines event stream:

- **`--html <path>`** — writes a self-contained HTML report (polling timeline, captured-variable provenance, failed-step diffs, and the reproducibility envelope) with no secret values embedded. The HTML report is rendered from the same event stream as the terminal output, so the two never disagree.
- **`--junit <path>`** — writes a JUnit XML results file for CI integration. The four verdicts map to distinct JUnit primitives (Fail → `<failure>`, Environment-error → `<error>`, Inconclusive → `<skipped>`), so CI systems can distinguish infrastructure breakage from product defects.
- **`--events <path>`** or **`--json`** — writes the raw buffered JSON Lines event stream to a file (one JSON object per line, UTF-8 without a BOM). This re-emits the same frozen v1 event stream that the terminal, HTML, and JUnit reports are rendered from, for consumption by downstream tooling such as the VSCode Test Explorer. **Security note:** The engine applies a defence-in-depth scrub to step observations before they enter the stream, redacting any verbatim occurrence of a secret value revealed during execution (e.g., from a thrown exception message in a `script.csharp` step). Authors remain responsible for values they deliberately invoke `Reveal()` on and then transform (encode, sign, or substring) — those reshapes are not in scope for redaction. Not wired into `--watch` mode.

All three flags accept `--parallel` and sequential runs; none works with `--watch` (which re-renders on each iteration rather than buffering one suite-wide stream). Parent directories are created as needed; existing files are overwritten.

## VSCode extension

A VSCode extension lives at [`tools/vscode-vouchfx/`](tools/vscode-vouchfx/). It binds the frozen v1
JSON Schema to `*.e2e.yaml` files (via the `redhat.vscode-yaml` language server), giving step-type-aware
**autocomplete, hover, and inline validation** as you author a suite — a `.NET` CI gate keeps the editor's
schema byte-for-byte in step with what the compiler accepts, so the editor can never suggest a construct
the engine would reject. It also provides **C# syntax highlighting** inside `script.csharp` blocks and
**Test Explorer integration** that discovers `.e2e.yaml` files in the workspace and runs scenarios/steps
with per-step verdicts and failing-line decoration in the editor. Full in-block C# IntelliSense
(completion/diagnostics) is a documented fast-follow — see
[`tools/vscode-vouchfx/docs/csharp-intellisense.md`](tools/vscode-vouchfx/docs/csharp-intellisense.md).

## De-risking results

- **Memory model verified** — a trivial script compiles once, runs 5,000 times in a collectible
  `AssemblyLoadContext`, and unloads with only ~1.3 KB net heap delta (2 MB threshold). The central
  risk — uncollectable Roslyn assemblies — is empirically retired, with a CI guard that forbids
  `CSharpScript.EvaluateAsync`/`RunAsync`.
- **Orchestration stability** — the stub topology (Postgres + container service) starts health-gated
  deterministically across 20/20 consecutive runs, resolving both a connection string and an HTTP
  endpoint.
- **Provider contract frozen** — the v1.x `IStepProvider` / `IStepBinder<T>` / `IStepValidator<T>` /
  `IStepCompiler<T>` / `IResourceContributor<T>` set and the `[StepProvider]` attribute.
- **Event-stream envelope** — the schema-versioned JSON Lines substrate every renderer and the Healer
  agent will consume.

## Repository layout

```
src/
  Engine/
    Platform.Engine.Abstractions      ScriptGlobalVariables, the JSON Lines event envelope
    Platform.Engine.Compilation       compile-once Roslyn path, collectible context, leak guards
    Platform.Engine.Orchestration     headless Aspire AppHost, health-gated topology
  Sdk/
    Platform.Sdk                      the frozen v1.x provider contract
  Providers/Core/                     twenty-five providers across eleven families
    Platform.Steps.Http.*             HTTP family (REST, SOAP)
    Platform.Steps.DbAssert.*         database assertions (Postgres, MySQL, SQL Server, MongoDB, DynamoDB)
    Platform.Steps.MqPublish.*        message publishing (Kafka, RabbitMQ, NATS, Azure Service Bus, Redis Streams)
    Platform.Steps.MqExpect.*         message consumption & assertions (same 5 brokers)
    Platform.Steps.CacheAssert.*      cache/search assertions (Redis, Elasticsearch)
    Platform.Steps.MetricsAssert.*    metrics assertions (Prometheus)
    Platform.Steps.StorageAssert.*    object-storage assertions (S3)
    Platform.Steps.TraceExpect.*      distributed-trace assertions (OTLP)
    Platform.Steps.MailExpect.*       email assertions (SMTP)
    Platform.Steps.WebhookListen.*    webhook listening (HTTP)
    Platform.Steps.Script.*           embedded code (C#)
tests/                                per-component and per-provider xUnit projects + the memory-leak measurement harness
docs/                                 the authoritative design — single source of truth (see below)
CLAUDE.md                             operating rules and hard invariants for this repository
```

### The authoritative documents

- [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](docs/01_Technical_Architecture_and_Engineering_Blueprint.md)
  — how the system is built (layers, Aspire/Testcontainers, Roslyn + memory model, security, verdict
  taxonomy, provider architecture, reporting, secrets).
- [`docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md`](docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)
  — the `.e2e.yaml` grammar, JSON Schema, and the VSCode/LSP extension design.
- [`docs/language-reference.md`](docs/language-reference.md) — the per-step-type field reference
  (required/optional fields, types, descriptions). Auto-generated from the composed v1 JSON Schema and
  frozen by a golden gate, so it can never drift from what the compiler accepts.
- [`docs/roadmap.md`](docs/roadmap.md) — the public roadmap: what has shipped, what v1.0 still
  needs, what v1.x adds next, and the permanent open-source feature boundary.
- [`CHANGELOG.md`](CHANGELOG.md) — the delivered-capability record, seeding each release's notes.

## Reserved namespaces

Two namespace prefixes are reserved (see §5.6 of the Architecture Blueprint); customer assemblies
declaring them are refused at suite start-up, and version conflicts fail fast at suite start rather
than at runtime:

| Prefix | Owner | Purpose |
|---|---|---|
| `Platform.Engine.*` | Engine | Core engine internals — compilation, orchestration, execution host, verdict taxonomy, reporting. |
| `Platform.Steps.*` | Providers | Step providers — e.g. `Platform.Steps.Core.HttpRest`, `Platform.Steps.DbAssert.Postgres`. |

`Platform.Sdk` is the public provider-authoring contract — consumed by providers, not part of the
engine internals.

## Related repositories

- **[vouchfx-providers](https://github.com/tomas-rampas/vouchfx-providers)** — The community provider hub. Hosts community providers, with PR-gated conformance testing and the Vouched badge rubric.
- **[vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples)** — Real-world sample applications (C#, Python, Java) and complete end-to-end test suites demonstrating common patterns and provider usage.

## Contributing

**Writing a provider?** See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the step-type model, the frozen v1 contract in the `Platform.Sdk` project (ships with the next tagged pre-release; until then it is packable locally, or via the provider hub's local-feed setup), composition rules, and the Vouched rubric. The [`examples/Example.Steps.Echo`](examples/Example.Steps.Echo) provider is a worked example demonstrating all four mandatory interfaces and the contributor's friction log; [`Example.Steps.Hello`](examples/Example.Steps.Hello) is an even more minimal template. The hub's first Community-tier provider, [`community/Community.Steps.JsonRpc`](https://github.com/tomas-rampas/vouchfx-providers/tree/main/community/Community.Steps.JsonRpc), is the canonical reference implementation — a complete JSON-RPC 2.0 protocol provider with substitution, capture, negative testing, four-verdict mapping and Docker-free test harness; see the hub's [implementation guide](https://tomas-rampas.github.io/vouchfx-providers/docs/implementing-a-provider.html) for an end-to-end walkthrough.

**Contributing to the platform engine?** Start with [`CONTRIBUTING.md`](CONTRIBUTING.md), [`GOVERNANCE.md`](GOVERNANCE.md), and the [public roadmap](docs/roadmap.md) for where the project is heading. Anyone working in this repository — human or agent — must honour the **hard invariants** in [`CLAUDE.md`](CLAUDE.md). Documentation prose is British English.

## AI assistance

Portions of vouchfx were written with AI assistance (Claude, via Claude Code), used in the manner of a junior engineer working under close review — never as an unsupervised author. The architecture, the hard invariants in [`CLAUDE.md`](CLAUDE.md), the provider contract, and every non-trivial design decision are the maintainer's; AI-drafted code and docs were reviewed, tested against real spikes, and frequently corrected or rejected before merge. §1.2 of the [Architecture Blueprint](docs/01_Technical_Architecture_and_Engineering_Blueprint.md) records several such corrections — cases where a plausible-looking snippet (Aspire APIs, Roslyn script constraints) turned out to be wrong and had to be fixed against the pinned library versions. That scepticism toward AI output is deliberate, ongoing policy, not a one-off caveat.

## Security

For information on how to report a security vulnerability, please refer to [`SECURITY.md`](SECURITY.md). vouchfx takes security seriously and operates a private coordinated-disclosure process via GitHub security advisories.

Releases are signed keylessly using [Sigstore](https://sigstore.dev/) (OIDC/Fulcio) with verifiable provenance attestations, and the nupkg is published to NuGet.org via Trusted Publishing (OIDC); no long-lived signing or publishing keys are managed. Consumers can verify release artefacts with `gh attestation verify` or `cosign verify-blob`. The signing and publishing pipeline (`.github/workflows/release.yml`) produces every release.

### Verifying a release

Every release artefact carries **keyless cosign signature** and **SLSA build-provenance attestation**, alongside a **CycloneDX software bill of materials**. Verification requires the `gh` and `cosign` CLIs:

**SLSA provenance (GitHub attestation):**
```bash
gh attestation verify vouchfx.1.2.3.nupkg --repo tomas-rampas/vouchfx
```

**Keyless cosign signature:**
```bash
cosign verify-blob vouchfx.1.2.3.nupkg \
  --bundle vouchfx.1.2.3.nupkg.cosign.bundle \
  --certificate-identity-regexp '^https://github\.com/tomas-rampas/vouchfx/\.github/workflows/release\.yml@.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Replace the filename with whichever artefact you are verifying (e.g. `vouchfx-1.2.3-linux-x64.tar.gz`, `vouchfx-1.2.3-win-x64.msi`). For complete verification procedures, including optional GPG signatures and SBOM inspection, see [`RELEASING.md`](RELEASING.md).

**Distribution note:** vouchfx is distributed as the `dotnet` global tool (nupkg, primary), plus multi-file self-contained per-OS executables (`.tar.gz`, `.msi`, `.deb`, `.pkg`). Single-file self-contained builds are not produced — the Roslyn compiler discovers provider assemblies via `Assembly.Location`, which returns an empty string in single-file mode, breaking provider loading.

## Licence

Apache-2.0 — see [`LICENSE`](LICENSE).
