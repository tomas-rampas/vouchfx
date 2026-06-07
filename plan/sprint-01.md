# Sprint 01 — Foundations: de-risk the hardest unknowns

| | |
|---|---|
| **Phase** | 1 — Foundations (MVP §8.1) |
| **Weeks** | 1–2 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M1** (closes Sprint 2) |
| **Theme** | Stand up the scaffolding and attack the two project-defining risks: memory-safe dynamic compilation and predictable Aspire orchestration. |

## Delivery status

**Delivered on PR #126** (branch `feat/sprint-01-foundations`) — pending CI and human review/merge.
All 11 tasks implemented and their acceptance criteria met locally; per the plan's Definition of Done
([README §8](README.md#8-definition-of-done-a-task-is-complete-when)) the sprint is not formally closed
until the PR is reviewed by another engineer and merged. Milestone **M1 "Foundations proven" is not yet
reached** — it closes after **Sprint 2** (the full Core-provider-closure leak gate and the reference
provider land there). Both headline de-risking exit criteria are met:

1. **Memory model verified:** Trivial script compiles once, runs 5,000× in a collectible `AssemblyLoadContext`, unloads with ~1.3 KB net heap delta (threshold 2 MB).
2. **Orchestration stability:** Stub topology (Postgres + container service) starts health-gated deterministically across 20/20 runs; connection strings and HTTP endpoints correctly resolved.

Carried forward into Sprint 2: make the memory-leak CI job blocking; reference provider implementation; reflective step-kind registry; full event-schema specification; first JSON Schema draft (see "[Carry-out into Sprint 2](#carry-out-into-sprint-2)" below).

## Sprint goal

A clean solution and CI pipeline exist; a trivial script compiles **once**, runs many thousands of
times in a collectible load context, and unloads with memory returning to baseline; a stub topology
starts health-gated through a programmatic Aspire AppHost; and the provider contract and event-stream
schema are under draft. This sprint exists to convert the plan's two largest uncertainties into early
confidence — or a cheap course correction (MVP §7.2, §10).

## Entry assumptions

- Repository exists with `docs/` and `CLAUDE.md` only; no source yet.
- Team is staffed per MVP §5.1 and fluent in .NET 8 LTS, Roslyn basics, Docker, and Aspire previews.
- An early security-and-compliance conversation is booked this phase (MVP §5.5).

## Tasks

### Workstream D — Integration & foundations

#### S01-D-01 · Solution & repository scaffold
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** — · **Spec:** CLAUDE.md "Planned repository structure"; MVP §8.1
- Create the .NET 8 LTS solution and the reserved project layout: `src/Engine` (`Platform.Engine.*`),
  `src/Providers/Core/*` (`Platform.Steps.*`), `src/Sdk`, `tests/`. Add `Directory.Build.props` pinning
  the language version to C# 11 (for `$$"""…"""` raw strings) and central package management.
- **Acceptance:**
  - `dotnet build` succeeds on an empty solution with the reserved namespace roots in place.
  - Reserved namespaces `Platform.Engine.*` and `Platform.Steps.*` are documented in the README as
    engine/provider-only (BP §5.6).
  - Central package versions include the §5.7 library set pinned (Polly v8, System.Text.Json,
    JsonPath.Net, JsonSchema.Net, YamlDotNet, Aspire pinned minor).

#### S01-D-02 · Engineering CI pipeline skeleton
- **Owner:** TL · **Estimate:** 1d · **Depends on:** S01-D-01 · **Spec:** MVP §8.1 ("Engineering CI"), §5.5
- Stand up the team's own build pipeline (build + unit-test + format check) into which the memory-leak
  regression gate is wired in Sprint 2.
- **Acceptance:**
  - CI runs build and test on every push; status visible on the engineering dashboard (MVP §5.5).
  - A placeholder `memory-leak` job stage exists, marked non-blocking until S02-D-01 fills it.

### Workstream B — Compiler & runtime

#### S01-B-01 · Memory-model PoC — compile a trivial script once
- **Owner:** CR1 · **Estimate:** 1.5d · **Depends on:** S01-D-01 · **Spec:** BP §5; MVP §8.1 (the single most important task), §10
- Build the minimal Roslyn path: `CSharpScript.Create<T>()` → `.Emit()` to a `MemoryStream`, **once**.
  No `CSharpScript.EvaluateAsync()` anywhere — that path is forbidden (it leaks an uncollectable
  assembly per call).
- **Acceptance:**
  - A trivial script is compiled exactly once and emitted to an in-memory assembly image.
  - A guard/test asserts `EvaluateAsync` is never referenced in the engine assembly.

#### S01-B-02 · Collectible load context: load, invoke N times, unload
- **Owner:** CR1 · **Estimate:** 2d · **Depends on:** S01-B-01 · **Spec:** BP §5, §5.6; CLAUDE.md memory model
- Load the emitted image into a custom `AssemblyLoadContext(isCollectible: true)`, obtain the delegate,
  invoke it many thousands of times against a `ScriptGlobalVariables` instance, then `.Unload()`. No
  static handle may bridge the boundary.
- **Acceptance:**
  - Delegate invoked ≥ 5,000 times from a single emitted assembly without recompilation.
  - The script reaches the host **only** through `ScriptGlobalVariables`; reviewed for static bridges.
  - After `.Unload()` + `GC` the context is collected (weak-reference assertion passes).

#### S01-B-03 · Baseline memory measurement harness
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S01-B-02 · **Spec:** MVP §4.2 (memory stability), §8.1
- A measurement harness that samples managed heap before/after a load-unload cycle and asserts return
  to baseline within tolerance. This is the seed of the permanent CI leak gate.
- **Acceptance:**
  - Harness reports net heap delta across a 5,000-iteration cycle and fails above a set threshold.
  - Output is machine-readable so CI can gate on it in Sprint 2.

#### S01-B-04 · Library bootstrap & version-conflict fail-fast spike
- **Owner:** CR2 · **Estimate:** 1d · **Depends on:** S01-D-01 · **Spec:** BP §5.6, §5.7
- Confirm the §5.7 client libraries load inside the collectible context and prototype the
  fail-fast-at-suite-start behaviour for version conflicts in the shared script context.
- **Acceptance:**
  - Each pinned client package referenced from a test assembly resolves at runtime.
  - A deliberately conflicting version surfaces a clear startup-time error, not a runtime surprise.

### Workstream A — Orchestration foundation

#### S01-A-01 · Programmatic headless Aspire AppHost
- **Owner:** OR · **Estimate:** 1.5d · **Depends on:** S01-D-01 · **Spec:** BP §4, §19; CLAUDE.md Aspire invariants
- Construct a `DistributedApplication` with `DistributedApplicationOptions { DisableDashboard = true }`
  (the dashboard needs env vars only `aspire run` injects). Suppress
  `Microsoft.Extensions.Diagnostics.HealthChecks` logs below `Warning`.
- **Acceptance:**
  - AppHost builds and starts headless with the dashboard disabled.
  - HealthChecks log noise below `Warning` is suppressed.

#### S01-A-02 · Stub topology, health-gated startup
- **Owner:** OR · **Estimate:** 2d · **Depends on:** S01-A-01 · **Spec:** BP §4; MVP §8.1 (orchestration spike), §10
- Add one container service plus one Postgres dependency via `AddContainer`/`AddProject` (never the
  generic `AddProject<T>()`). Gate startup with `WaitFor` on the **most specific** resource — the
  *database*, not the server — to avoid the fast-hardware race where the server returns before the
  lifecycle script creates the DB.
- **Acceptance:**
  - Topology reaches healthy state deterministically across ≥ 20 consecutive runs.
  - `WaitFor` targets the database resource; reviewed against the server-vs-database race note.

#### S01-A-03 · Endpoint & connection-string resolution check
- **Owner:** OR · **Estimate:** 1d · **Depends on:** S01-A-02 · **Spec:** BP §4; CLAUDE.md (connection strings ≠ endpoints)
- Prove the two distinct discovery paths: connection strings via `app.GetConnectionString(name)` for
  managed dependencies, and HTTP endpoints via the retained `IResourceBuilder`'s
  `.GetEndpoint("http").Url` after `StartAsync`. Confirm `app.GetEndpoint(name, scheme)` is **not** used.
- **Acceptance:**
  - A test resolves both a Postgres connection string and a service HTTP endpoint and connects.
  - No call to the non-existent `app.GetEndpoint(name, scheme)` exists.

### Workstream F — Provider SDK & Core providers

#### S01-F-01 · Draft the provider contract interfaces
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S01-D-01 · **Spec:** BP §13; MVP §8.1, §6.6, §10
- Write the C# interfaces — `IStepProvider`, `IStepBinder<T>`, `IStepValidator<T>`, `IStepCompiler<T>`,
  `IResourceContributor<T>` — and the `[StepProvider]` attribute, with strongly-typed record models
  (never `Dictionary<string,object>`). Agree the resolve→bind→validate→plan→emit shape with workstream
  B early (the compiler is provider-mediated by design, MVP §6.2).
- **Acceptance:**
  - Interfaces compile in `src/Sdk` with XML doc comments stating the v1.x freeze intent (BP §13.8.1).
  - The compile pipeline contract (the five stages) is reviewed and signed off by TL and CR1.

### Workstream G — Result reporting & diagnostics

#### S01-G-01 · Event-stream schema skeleton
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** — · **Spec:** BP §14; MVP §8.1, §6.7
- Define the schema-versioned JSON Lines event envelope (schema version, event type, timestamp,
  correlation ids) as the substrate every renderer and the Healer agent will consume. Full event types
  land in Sprint 2.
- **Acceptance:**
  - A versioned envelope is defined; a sample stream serialises/deserialises with `System.Text.Json`.
  - The design records that renderers must tolerate unknown fields (BP §14).

## Exit criteria (sprint demo)

- A trivial script compiles once, runs ≥ 5,000 times in a collectible context, and unloads to baseline.
- The stub topology starts health-gated and resolves both a connection string and an HTTP endpoint.
- The provider contract interfaces and the event-stream envelope are drafted and reviewed.

## Risks mitigated this sprint (MVP §10)

- *Collectible-load-context memory model proves unreliable* — front-loaded here (S01-B-01..03).
- *Orchestration flakiness on diverse hardware* — health gating proven from the first sprint (S01-A-02).
- *Provider contract proves wrong in shape* — drafted now, exercised by a reference provider in Sprint 2.

## Carry-out into Sprint 2

Provider-closure leak test, the throwaway reference provider, the reflective registry, the full event
schema, and the first JSON Schema draft.
