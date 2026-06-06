# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state: specification, not yet code

This repository currently contains **only design documents and a stub README** — there is no source code, no build system, and no tests. The three documents under `docs/` are the authoritative specification for a platform that has not yet been implemented. When asked to "build", "test", or "implement" something here, understand that you are starting from a blank slate against a fully-specified design, not modifying existing code.

There are no build/lint/test commands yet. The design commits the implementation to a **.NET solution** (engine targets .NET 8 LTS), distributed as a `dotnet` global tool (the CLI runner) plus a VSCode Marketplace extension. Once a solution exists, expect the conventional `dotnet build` / `dotnet test` / `dotnet run`, and a headless runner CLI with deterministic exit codes invoked from CI. Do not invent commands before the corresponding projects exist.

## The authoritative documents

The `docs/` files are large (the architecture blueprint alone is ~1200 lines). **Navigate them with Grep by section number rather than reading end-to-end.**

- `docs/01_Technical_Architecture_and_Engineering_Blueprint.md` — the engineering architecture. The single source of truth for how the system is built. Section index: §3 five-layer architecture; §4 Aspire/Testcontainers orchestration; §5 the Roslyn compiler and memory model; §6 cloud execution fabric; §11 security; §12 verdict taxonomy; §13 the provider plugin architecture; §14 reporting/event stream; §16 the test runner; §17 secrets; §18 risks; §19 technology reference table.
- `docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md` — the `.e2e.yaml` language grammar and the VSCode/LSP tooling. §3 document structure; §4 common step fields; §5 step families; §6 capture/placeholder syntax; §7 verifyMode; §8 JSON Schema; §10 the extension.
- `docs/03_MVP_Project_Plan.md` — scope, the seven workstreams (A orchestration, B compiler/runtime, C tooling, D integration, E pilot, F Provider SDK, G reporting), phasing, and what ships in the MVP vs. later.

## What the platform is

**vouchfx** compiles declarative YAML integration tests into Turing-complete C# scripts (CSX) executed through Roslyn, wrapped in fully automated infrastructure orchestration (.NET Aspire + Testcontainers). It tests *distributed* .NET systems end-to-end: a single business transaction that crosses a REST call, a Kafka event, a database mutation, and an outbound webhook. It is **not** a unit-test framework and **not** a UI/browser tool.

The flow is: **author** `.e2e.yaml` → **validate** against JSON Schema → **compile** YAML→AST→CSX→Roslyn delegate (once) → **orchestrate** the container topology via Aspire → **execute** the delegate against discovered endpoints → **verify and reclaim** (collect a structured verdict, unload the load context, reset state).

### The five layers (§3)

1. **Authoring** — YAML DSL, JSON Schema, VSCode extension, LSP.
2. **Compilation** — YAML parser → AST → CSX generation → Roslyn Scripting API.
3. **Orchestration** — Aspire AppHost, Testcontainers, health-gated startup.
4. **Execution** — Roslyn delegate host, collectible `AssemblyLoadContext`, Polly.
5. **Infrastructure fabric** — where containers physically run: local Docker socket, SaaS cloud, or on-prem Kubernetes.

Each layer exposes a narrow typed contract upward. The single contract that makes "topological parity" (one suite runs unchanged local/SaaS/CI) work is that the compiled delegate depends only on a typed `ScriptGlobalVariables` host object, and the orchestration layer is its sole producer.

## Non-obvious invariants any implementation must honor

These are the constraints that span multiple sections and are easy to violate. Most were discovered in real spikes (recorded in §1.2 of the blueprint) and are non-negotiable.

**Memory model (§5) — the central hard problem.** Dynamically compiled scripts must be fully reclaimable. Never call `CSharpScript.EvaluateAsync()` (it leaks an uncollectable assembly per invocation). The mandated pattern is *compile-once, isolate, unload*: `CSharpScript.Create<T>()` → `.Emit()` to a `MemoryStream` once → load into a custom `AssemblyLoadContext(isCollectible: true)` → invoke the delegate N times → `.Unload()`. The script reaches the environment **only** through `ScriptGlobalVariables`; no static handles may bridge the boundary. A CI memory-leak regression test against the full transitive closure of every Core provider is a Phase 1 deliverable, not an afterthought.

**The provider model (§13).** Step types are `<family>.<provider>` (e.g. `db-assert.postgres`, `mq-publish.kafka`) — borrowed from Terraform/kubectl. A family is the intent; a provider is the concrete technology. Providers are **compile-time, source-level plugins**: there is no runtime plugin loader, no dynamic assembly loading, no sandbox. A contributor adds a project, implements the `IStepProvider` / `IStepBinder<T>` / `IStepValidator<T>` / `IStepCompiler<T>` / `IResourceContributor<T>` interfaces, and the reflective `StepKindRegistry` (frozen at startup) discovers it via the `[StepProvider]` attribute. Models are strongly-typed records, never `Dictionary<string,object>`. The v1 contract is **frozen for the v1.x engine series**.

**`CsxFragment` composition contract (§13.3.1)** — providers emit C#, and these rules prevent collisions:
- A `CsxFragment` is three separate fields: `RequiredUsings` (namespace strings, never inline `using` lines), `RequiredHelpers` (nested static classes prefixed with the provider id, e.g. `DbAssertPostgres_Helpers`), and a single brace-enclosed `StatementBlock`.
- **`using var` is illegal inside a Roslyn script body** — it is a parse error regardless of language version. Use plain `var` + explicit `.Dispose()` in a `finally`.
- Step ids may contain hyphens; emitted C# variable names may not. Sanitise with `CsxFragment.SanitiseId` (`-` → `_`) before splicing.
- Emit fragment bodies with **C# 11 double-dollar raw strings** `$$"""…"""`: `{{ }}` is a literal brace, `{id}` is an interpolation hole. The single-dollar form inverts these and fails.
- Cross-step state passes only through the `Vars` global context — a provider may not assume variables declared by another provider.

**Aspire specifics (§4, §19).** Construct the host with `DistributedApplicationOptions { DisableDashboard = true }` for the headless runner (the dashboard needs env vars only `aspire run` injects). Use `AddContainer(name, image)` or `AddProject(name, csprojPath)` — *not* the strongly-typed `AddProject<T>()` generic (it imports compile-time coupling that contradicts the YAML-first premise). Distinguish **connection strings** (`app.GetConnectionString(name)`, for managed resources/dependencies) from **endpoints** (resolved through the retained `IResourceBuilder`'s `.GetEndpoint("http").Url` after `StartAsync` — `app.GetEndpoint(name, scheme)` does not exist). Always `WaitFor`/wait on the **most specific** resource a step depends on — the *database* resource, not the *server* (waiting on the server returns before Aspire's lifecycle script has created the database, causing intermittent failures on fast hardware). Suppress `Microsoft.Extensions.Diagnostics.HealthChecks` logs below `Warning` (cosmetic startup noise).

**Verdict taxonomy (§12.1) — four outcomes, four counters, four colours.** Pass, Fail, **Environment error** (infra failed — container unhealthy, pull failed, tunnel collapsed, seed failed), **Inconclusive** (timeout / partition-outlasted-grace / upstream-capture-unmet). **Only `Fail` breaks a CI build by default.** Conflating an environment failure with a defect destroys trust in the tool — keep them separate everywhere (taxonomy, reporting, exit codes).

**Reporting substrate (§14).** Every renderer (terminal, HTML, JUnit XML, dashboard) and the Healer agent consume one **schema-versioned JSON Lines event stream**. Each `step-attempt` is recorded individually (this is what makes the RETRY "polling timeline" renderable without re-running). Renderers tolerate unknown fields. Don't build per-audience report pipelines — render the one stream differently.

**Assembly-graph hygiene (§5.6).** Reserved namespaces: engine types under `Platform.Engine.*`, provider service types under `Platform.Steps.*` (e.g. `Platform.Steps.DbAssert.Postgres`). Customer DLLs declaring types here are refused at startup. Customer DLLs load into the *same* collectible context as the generated script. Version conflicts fail fast at suite start, not at runtime.

**Secrets (§17).** Written as `${secret:source/path}` references, never literals. Resolved **at step-execution time, not compile time** (compile-time interpolation would bake values into IL, defeat compile-once, and corrupt the reproducibility envelope). `Vars.Secrets.Resolve` returns a typed `SecretString` that does not implement value-returning `ToString()`/`IFormattable` — redaction at the source. The reproducibility envelope hashes the *reference*, never the value. MVP sources: `env` and `vault`.

**Library commitments (§5.7).** Polly **v8** (`ResiliencePipeline`; v7 is unsupported) behind `verifyMode: RETRY`; `System.Text.Json` (engine never returns Newtonsoft types to providers); `JsonPath.Net` + `JsonSchema.Net` (JsonEverything, draft 2020-12); `YamlDotNet`; canonical clients `Npgsql` / `Confluent.Kafka` / `MongoDB.Driver` / `StackExchange.Redis` / NEST. Pin Aspire to a known-stable minor **per engine release** and move it forward at engine cadence, not Aspire cadence.

## Planned repository structure (from the docs — does not exist yet)

The six **Core providers** the MVP ships live at `src/Providers/Core`: `http.rest`, `mq-publish.kafka`, `mq-expect.kafka`, `db-assert.postgres`, `webhook-listen.http`, `script.csharp`. Community-reviewed providers go under `src/Providers/Verified`. Three governance tiers exist (Core / Verified / Community), all Apache-2.0 so providers can move between tiers without IP friction. `db-assert` has no default provider (no sensible lowest common denominator); other single-provider families accept the bare family name as an alias.

## The `.e2e.yaml` shape (DSL doc §3)

Files use the `.e2e.yaml` extension. Four top-level sections, only `steps` is mandatory:

- `metadata` — `name`, `owner`, `tags`, `description`; drives runner selection (by tag/owner/path/change-set/prior-verdict, §16) and reporting. No execution effect.
- `environment` — `services` (system under test: `image:` form preferred, or `project:` csproj path), `dependencies` (managed Aspire resources: postgres, kafka, etc.), optional `seed` block (declarative reference SQL / fixtures / warm-up messages, applied after the topology is healthy), `imageRegistry` / `imagePullPolicy` overrides.
- `variables` — constants pre-loaded into the shared context.
- `steps` — ordered list; each has `id`, `type`, optional `capture` (JSONPath/XPath extractors → shared vars), `verifyMode` (`IMMEDIATE` default / `RETRY` for engine-owned polling with backoff — authors never write `Thread.Sleep`), `timeout`, `continueOnFailure`.

Steps communicate through one mutable variable dictionary (the same `ScriptGlobalVariables`). State threads forward via `capture` and `{placeholder}` substitution. Test doubles (WireMock/Mountebank) are provisioned as ordinary containers in `environment`, not as a built-in mocking feature.

## Working conventions

- The blueprint validates itself against working code through time-boxed spikes (§1.2). When you implement something the docs marked "illustrative", verify the real Aspire/Roslyn API surface against the pinned version rather than trusting the illustrative snippet verbatim — the docs themselves record several corrections from spikes.
- Prose in the docs is British-English spelling. Match it when editing documentation.
