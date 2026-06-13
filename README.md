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

> **Milestone M3 — full step set & SDK — is engineering-complete and in phase-exit review** (see [exit criteria](plan/m3-phase-exit.md)). The engine compiles `.e2e.yaml` declarative integration
> tests into memory-safe, Turing-complete C# (CSX) via Roslyn, orchestrates distributed topologies
> with Aspire and Testcontainers, executes all six Core providers (`http.rest`, `db-assert.postgres`,
> `script.csharp`, `mq-publish.kafka`, `mq-expect.kafka`, `webhook-listen.http`) end-to-end with
> declarative seeding, `${secret:env/…}` and `${secret:vault/…}` resolution, engine-owned RETRY polling (Polly v8)
> with per-attempt timeline and captured-variable provenance rendering, and emits a schema-versioned JSON Lines event stream rendered to the terminal. The v1 JSON Schema and v1 provider/event contract are frozen, the Provider SDK is published as a NuGet package (`Platform.Sdk`) with developer guidance and worked-example providers, and scenarios can run in parallel with topology-per-scenario isolation (`vouchfx run --parallel <n>`) or in watch mode for local iteration (`vouchfx run --watch`). A headless CLI runner
> discovers and selects scenarios by tag, owner, path, or git change-set, with per-scenario isolation.
> Still to come: VSCode extension/LSP, HTML report and JUnit XML renderers, the full verdict taxonomy surface, and community provider tiers (Verified and Community governance) — see
> the [delivery plan](plan/README.md) and [roadmap](plan/roadmap.md). The engine targets **.NET 8 LTS**,
> shipped as a `dotnet` global tool plus a VSCode extension.

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
loader, no sandbox. All six **Core** providers are now delivered: `http.rest`, `db-assert.postgres`,
`script.csharp`, `mq-publish.kafka`, `mq-expect.kafka`, and `webhook-listen.http`. All are governed
across three tiers (Core / Verified / Community), all Apache-2.0.

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
(build + format + unit tests), a **memory-leak** job that runs the heap-measurement harness over
5,000 load-unload cycles (non-blocking until Sprint 2), and a forward-looking **integration**
(Docker) job.

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

## Sprint 1 de-risking results

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
  Providers/Core/
    Platform.Steps.Core.HttpRest      reference HTTP provider (stub; built out in Sprint 2)
tests/                                4 xUnit projects + the memory-leak measurement harness
docs/                                 the authoritative design — single source of truth (see below)
plan/                                 MVP delivery plan: 5 milestones, 12 sprints, 108 tasks
CLAUDE.md                             operating rules and hard invariants for this repository
```

### The authoritative documents

- [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](docs/01_Technical_Architecture_and_Engineering_Blueprint.md)
  — how the system is built (layers, Aspire/Testcontainers, Roslyn + memory model, security, verdict
  taxonomy, provider architecture, reporting, secrets).
- [`docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md`](docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)
  — the `.e2e.yaml` grammar, JSON Schema, and the VSCode/LSP extension design.
- [`docs/03_MVP_Project_Plan.md`](docs/03_MVP_Project_Plan.md) — scope, the seven workstreams, phasing,
  and what is in the MVP versus later.
- [`plan/README.md`](plan/README.md) — the execution plan that decomposes the MVP into milestones,
  sprints, and tasks; [`plan/sprint-01.md`](plan/sprint-01.md) is the delivered Foundations sprint.

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

## Contributing

**Writing a provider?** See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the step-type model, the frozen v1 contract from the `Platform.Sdk` NuGet package, composition rules, and the Verified-tier rubric. The [`examples/Example.Steps.Hello`](examples/Example.Steps.Hello) provider is a copyable template demonstrating all four mandatory interfaces on a minimal, dependency-free step.

**Contributing to the platform engine?** The entry point is the [delivery plan](plan/README.md), which sequences work by risk (memory model and orchestration first). Anyone working in this repository — human or agent — must honour the **hard invariants** in [`CLAUDE.md`](CLAUDE.md). Documentation prose is British English.

## Licence

Apache-2.0 — see [`LICENSE`](LICENSE).
