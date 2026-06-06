# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

## What vouchfx is

**vouchfx** compiles declarative `.e2e.yaml` integration tests into Turing-complete C# (CSX), runs them through Roslyn, and orchestrates the required container topology with .NET Aspire + Testcontainers. It tests **distributed .NET systems end-to-end** — one business transaction crossing a REST call, a Kafka event, a DB mutation and an outbound webhook. It is **not** a unit-test framework and **not** a UI/browser tool.

Pipeline: author `.e2e.yaml` → validate vs JSON Schema → compile YAML→AST→CSX→Roslyn delegate (**once**) → orchestrate topology via Aspire → execute delegate against discovered endpoints → collect verdict, unload context, reset.

Five layers (§3), each exposing a narrow typed contract upward: **1 Authoring** (YAML/Schema/VSCode/LSP) · **2 Compilation** (parser→AST→CSX→Roslyn) · **3 Orchestration** (Aspire AppHost, Testcontainers, health-gated startup) · **4 Execution** (delegate host, collectible `AssemblyLoadContext`, Polly) · **5 Infrastructure fabric** (local Docker / SaaS / on-prem K8s). The one contract enabling topological parity (a suite runs unchanged local/SaaS/CI): the compiled delegate depends **only** on a typed `ScriptGlobalVariables`, and orchestration is its sole producer.

## How to work here — read first

- **This repo is a specification, not code yet.** Only `docs/` and a stub README exist: no source, no build, no tests. "Build / implement / test X" means *start from a blank slate against a fully-specified design*, not modify existing code.
- **Do not invent commands.** No build/lint/test commands exist yet. The target is a .NET solution (engine on **.NET 8 LTS**) shipped as a `dotnet` global tool (CLI runner, deterministic CI exit codes) + a VSCode extension. Once projects exist, use standard `dotnet build`/`test`/`run`. Never reference a command before its project exists.
- **Navigate the docs with Grep by section number.** They are large (blueprint ~1200 lines). Do not read end-to-end.
- **Distrust illustrative snippets.** The docs mark some code "illustrative" and record corrections from real spikes (§1.2). Verify the actual Aspire/Roslyn API against the pinned version before trusting any snippet.
- **Honour the hard invariants below.** They span multiple sections, were discovered in real spikes, and are non-negotiable.
- **Follow the MVP build order (`docs/03`):** seven workstreams — A orchestration, B compiler/runtime, C tooling, D integration, E pilot, F Provider SDK, G reporting. Check phasing there before starting; the memory-leak regression test is a **Phase 1** deliverable, not an afterthought.
- **Documentation prose is British English.** Match it when editing docs.

## The authoritative documents

- `docs/01_Technical_Architecture_and_Engineering_Blueprint.md` — single source of truth for how the system is built. §3 five layers; §4 Aspire/Testcontainers; §5 Roslyn compiler + memory model; §6 cloud fabric; §11 security; §12 verdict taxonomy; §13 provider architecture; §14 reporting/event stream; §16 test runner; §17 secrets; §18 risks; §19 technology table.
- `docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md` — the `.e2e.yaml` grammar + VSCode/LSP. §3 document structure; §4 common step fields; §5 step families; §6 capture/placeholder syntax; §7 verifyMode; §8 JSON Schema; §10 extension.
- `docs/03_MVP_Project_Plan.md` — scope, the seven workstreams, phasing, MVP vs. later.

## Hard invariants (non-negotiable)

**Memory model (§5) — the central problem.**
- **NEVER** `CSharpScript.EvaluateAsync()` — it leaks an uncollectable assembly per call.
- **ALWAYS** compile-once, isolate, unload: `CSharpScript.Create<T>()` → `.Emit()` to a `MemoryStream` once → load into a custom `AssemblyLoadContext(isCollectible: true)` → invoke the delegate N times → `.Unload()`.
- The script touches the environment **only** through `ScriptGlobalVariables`; no static handle may bridge the boundary.
- Ship a CI memory-leak regression test over the full transitive closure of every Core provider (Phase 1).

**Provider model (§13).**
- Step type = `<family>.<provider>` (`db-assert.postgres`, `mq-publish.kafka`). Family = intent, provider = technology.
- Providers are **compile-time, source-level plugins** — no runtime loader, no dynamic assembly loading, no sandbox. Add a project, implement `IStepProvider`/`IStepBinder<T>`/`IStepValidator<T>`/`IStepCompiler<T>`/`IResourceContributor<T>`, mark `[StepProvider]`; the reflective `StepKindRegistry` (frozen at startup) discovers it.
- Models are **strongly-typed records**, never `Dictionary<string,object>`.
- The v1 interface contract is **frozen for the v1.x engine series**.

**CsxFragment composition (§13.3.1) — these rules prevent collisions.**
- Three fields only: `RequiredUsings` (namespace strings, never inline `using` lines), `RequiredHelpers` (nested static classes prefixed with the provider id, e.g. `DbAssertPostgres_Helpers`), one brace-enclosed `StatementBlock`.
- **`using var` is illegal in a Roslyn script body** (parse error, any language version). Use plain `var` + explicit `.Dispose()` in a `finally`.
- Sanitise step ids before splicing: `CsxFragment.SanitiseId` (`-` → `_`). Emitted variable names may not contain hyphens.
- Emit bodies as **C# 11 double-dollar raw strings** `$$"""…"""`: `{{ }}` = literal brace, `{id}` = interpolation hole. The single-dollar form inverts these and fails.
- Cross-step state passes **only** through the `Vars` global; never assume variables declared by another provider.

**Aspire (§4, §19).**
- Headless runner: construct with `DistributedApplicationOptions { DisableDashboard = true }` (the dashboard needs env vars only `aspire run` injects).
- Use `AddContainer(name, image)` / `AddProject(name, csprojPath)` — **not** the generic `AddProject<T>()` (compile-time coupling that breaks the YAML-first premise).
- Connection strings (`app.GetConnectionString(name)`, for managed dependencies) ≠ endpoints (retained `IResourceBuilder`'s `.GetEndpoint("http").Url` after `StartAsync`). `app.GetEndpoint(name, scheme)` does **not** exist.
- `WaitFor` the **most specific** resource a step needs — the *database*, not the *server* (the server returns before Aspire's lifecycle script creates the DB → intermittent failures on fast hardware).
- Suppress `Microsoft.Extensions.Diagnostics.HealthChecks` logs below `Warning` (cosmetic startup noise).

**Verdict taxonomy (§12.1) — four outcomes, kept separate everywhere (taxonomy, reporting, exit codes).** **Pass** · **Fail** · **Environment error** (infra: unhealthy container, pull/tunnel/seed failure) · **Inconclusive** (timeout / partition-outlasted-grace / upstream-capture-unmet). **Only `Fail` breaks CI by default.** Conflating an env error with a defect destroys trust in the tool.

**Reporting (§14).** One **schema-versioned JSON Lines event stream** feeds every renderer (terminal, HTML, JUnit XML, dashboard) and the Healer agent. Record each `step-attempt` individually (this makes the RETRY polling timeline renderable without re-running). Renderers tolerate unknown fields. Never build per-audience pipelines — render the one stream differently.

**Assembly-graph hygiene (§5.6).** Reserved namespaces: `Platform.Engine.*` (engine), `Platform.Steps.*` (providers, e.g. `Platform.Steps.DbAssert.Postgres`). Customer DLLs declaring these are refused at startup. Customer DLLs share the generated script's collectible context. Version conflicts fail fast at suite start, not at runtime.

**Secrets (§17).** References only: `${secret:source/path}`, never literals. Resolve **at step-execution time, not compile time** (compile-time interpolation bakes values into IL, defeats compile-once, corrupts the reproducibility envelope). `Vars.Secrets.Resolve` returns a typed `SecretString` with no value-returning `ToString()`/`IFormattable` (redaction at the source). The reproducibility envelope hashes the *reference*, never the value. MVP sources: `env`, `vault`.

**Libraries (§5.7).** Polly **v8** (`ResiliencePipeline`; v7 unsupported) behind `verifyMode: RETRY`. `System.Text.Json` only (never return Newtonsoft to providers). `JsonPath.Net` + `JsonSchema.Net` (JsonEverything, draft 2020-12). `YamlDotNet`. Canonical clients: `Npgsql`/`Confluent.Kafka`/`MongoDB.Driver`/`StackExchange.Redis`/NEST. Pin Aspire to a stable minor **per engine release**; advance at engine cadence, not Aspire cadence.

## The `.e2e.yaml` shape (DSL §3)

Files use the `.e2e.yaml` extension. Four top-level sections, only `steps` is mandatory:
- `metadata` — `name`/`owner`/`tags`/`description`; drives runner selection (by tag/owner/path/change-set/prior-verdict, §16) and reporting. No execution effect.
- `environment` — `services` (system under test: `image:` preferred, or `project:` csproj), `dependencies` (managed Aspire resources: postgres, kafka, …), optional `seed` (SQL/fixtures/warm-up applied after the topology is healthy), `imageRegistry`/`imagePullPolicy` overrides.
- `variables` — constants pre-loaded into the shared context.
- `steps` — ordered; each has `id`, `type`, optional `capture` (JSONPath/XPath → vars), `verifyMode` (`IMMEDIATE` default / `RETRY` for engine-owned polling with backoff — authors never write `Thread.Sleep`), `timeout`, `continueOnFailure`.

Steps share one mutable dictionary (`ScriptGlobalVariables`); state threads forward via `capture` and `{placeholder}` substitution. Test doubles (WireMock/Mountebank) are ordinary containers in `environment`, not a built-in mocking feature.

## Planned repository structure (does not exist yet)

Six **Core providers** at `src/Providers/Core`: `http.rest`, `mq-publish.kafka`, `mq-expect.kafka`, `db-assert.postgres`, `webhook-listen.http`, `script.csharp`. Community-reviewed providers under `src/Providers/Verified`. Three governance tiers (Core / Verified / Community), all Apache-2.0 so providers move tiers without IP friction. `db-assert` has **no** default provider (no sensible lowest common denominator); other single-provider families accept the bare family name as an alias.
