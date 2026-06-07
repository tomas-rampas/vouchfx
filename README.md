# vouchfx

**End-to-end integration testing for distributed .NET systems, authored in YAML.**

vouchfx compiles declarative `.e2e.yaml` tests into Turing-complete C# (CSX), runs them through
Roslyn, and orchestrates the required container topology with **.NET Aspire + Testcontainers**. It
tests one business transaction as it crosses a REST call, a Kafka event, a database mutation and an
outbound webhook — the seams where distributed systems actually break.

It is **not** a unit-test framework and **not** a UI/browser tool.

```
author .e2e.yaml → validate vs JSON Schema → compile YAML→AST→CSX→Roslyn delegate (once)
   → orchestrate topology via Aspire → execute delegate against discovered endpoints
   → collect verdict → unload context → reset
```

## Status

> **Specification and delivery plan — pre-code.** This repository currently contains the authoritative
> design (`docs/`), the MVP delivery plan (`plan/`), and shared Claude Code configuration (`.claude/`).
> There is **no source, build, or test yet**; the engine targets **.NET 8 LTS**, shipped as a `dotnet`
> global tool plus a VSCode extension. The repository layout under `src/` described in the docs does
> not exist until implementation begins (see the plan).

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
loader, no sandbox. The six planned **Core** providers are `http.rest`, `mq-publish.kafka`,
`mq-expect.kafka`, `db-assert.postgres`, `webhook-listen.http`, and `script.csharp`, governed across
three tiers (Core / Verified / Community), all Apache-2.0.

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

## Repository layout

| Path | What it is |
|---|---|
| [`docs/`](docs/) | The authoritative design — single source of truth (see below). |
| [`plan/`](plan/) | MVP delivery plan: 5 milestones, 12 sprints, 108 tasks, 7 workstreams. |
| [`.claude/`](.claude/) | Shared, project-scoped Claude Code configuration (agents, skills, commands, settings). |
| [`CLAUDE.md`](CLAUDE.md) | Operating rules and hard invariants for working in this repository. |

### The authoritative documents

- [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](docs/01_Technical_Architecture_and_Engineering_Blueprint.md)
  — how the system is built (layers, Aspire/Testcontainers, Roslyn + memory model, security, verdict
  taxonomy, provider architecture, reporting, secrets).
- [`docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md`](docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)
  — the `.e2e.yaml` grammar, JSON Schema, and the VSCode/LSP extension design.
- [`docs/03_MVP_Project_Plan.md`](docs/03_MVP_Project_Plan.md) — scope, the seven workstreams, phasing,
  and what is in the MVP versus later.
- [`plan/README.md`](plan/README.md) — the execution plan that decomposes the MVP into milestones,
  sprints, and tasks.

## Contributing

Implementation has not started; the entry point is the [delivery plan](plan/README.md), which
sequences work by risk (memory model and orchestration first). Anyone working in this repository —
human or agent — must honour the **hard invariants** in [`CLAUDE.md`](CLAUDE.md). Documentation prose
is British English.

## Licence

Apache-2.0 (intended), so providers can move between governance tiers without IP friction.
