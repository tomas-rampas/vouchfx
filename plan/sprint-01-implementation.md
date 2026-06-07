# Sprint 01 — Technical Implementation Plan

*A pre-code design for review (MVP §8.1). No engine logic is written until this is signed off.*
This expands the Sprint 01 task cards in [`sprint-01.md`](sprint-01.md) into concrete file
layout, pinned dependencies, and the design of the memory-model proof of concept — the single
most important task in the whole plan.

> **Authority & caveats.** Per [`CLAUDE.md`](../CLAUDE.md): some snippets in the blueprint are
> *illustrative*; every Roslyn/Aspire API named below is **verified against the pinned package at
> implementation time**, not trusted from the docs. British English throughout. The container is
> ephemeral — commit early.

---

## 0. What needs your sign-off before coding (see §11)

1. **Aspire major version** — recommend **9.x stable** (supports .NET 8 LTS).
2. **Provider-SDK namespace** — recommend `Platform.Engine.Abstractions` in a packable `Platform.Sdk` project.
3. **.NET install path** — recommend a **SessionStart hook** so every web session can build/test.
4. **Test stack** — recommend **xUnit**.
5. **Leak-test pass thresholds** — proposed numbers in §4.4.

Everything else below is a recommendation I'll proceed with unless you say otherwise.

---

## 1. Environment prerequisites (`S01-D-02` groundwork)

The container has Docker 29.3.1 and reaches NuGet, but **no .NET SDK**. Two pieces of setup:

- **`.devcontainer`/SessionStart hook** that installs the **.NET 8 LTS SDK** (via `dotnet-install.sh`,
  pinned channel `8.0`) on session start, so `dotnet build`/`test` work in web sessions. This is the
  recommended fix for the ephemeral container; I can scaffold it with the `session-start-hook` skill.
- **Docker daemon** must be running for the orchestration spike (`S01-A-02/03`). The memory PoC
  (`S01-B-*`) needs **only the SDK** — so it proceeds even if Docker is unavailable.

Deliverable: a documented one-command bootstrap and a green `dotnet build` on an empty solution.

### 1.1 SDK pinning via `global.json` (required — multiple SDKs present)

Dev machines (and CI) carry side-by-side SDKs — the reference box has `8.0.409`, `8.0.421`, `9.0.300`,
`9.0.314`, **and `10.0.108`**. Without a `global.json`, `dotnet build` selects the **newest** SDK (10.0)
and applies its MSBuild/analyzer defaults even against a `net8.0` target — non-deterministic and a source
of subtle drift. We pin the **SDK** (distinct from the **target framework**, which stays `net8.0`):

```json
{
  "sdk": {
    "version": "8.0.400",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

- `rollForward: latestFeature` resolves to the highest installed **8.0** SDK (e.g. `8.0.421` locally, the
  latest `8.0` SDK the SessionStart hook installs in the container) and **never** rolls to 9.x/10.x.
- The engine still **targets `net8.0`** via `Directory.Build.props`; `global.json` only fixes which SDK
  builds it, so local, container, and CI builds are byte-for-byte comparable.
- If a build host lacks any `8.0` SDK, the pin **fails fast** with a clear message — which is what we want,
  rather than silently building on .NET 10.

> *Aside on .NET 10:* it is also LTS, but the engine is committed to **.NET 8 LTS** (CLAUDE.md, MVP §1).
> Re-targeting is a deliberate design change, not a default — flag it if you want to revisit, otherwise we
> hold 8 LTS and Aspire 9.x.

## 2. Solution & project layout (`S01-D-01`)

```
vouchfx.sln
global.json                    # pins the .NET SDK to the 8.0.4xx band (see §1.1)
Directory.Build.props          # LangVersion=11, Nullable=enable, ImplicitUsings=enable,
                               # TreatWarningsAsErrors=true, deterministic build
Directory.Packages.props       # central package management (all versions pinned here)
.editorconfig                  # style + analyzer severities
BannedSymbols.txt              # bans CSharpScript.EvaluateAsync (see §4.5)
src/
  Engine/
    Platform.Engine.csproj                 # ns Platform.Engine.* — orchestrator-facing engine
  Sdk/
    Platform.Sdk.csproj                    # ns Platform.Engine.Abstractions — the frozen provider contract (packable)
  Providers/Core/                          # (populated from Sprint 03; empty placeholder now)
tests/
  Platform.Engine.Tests/                   # unit tests (xUnit)
  Platform.Engine.MemoryTests/             # the collectible-unload leak harness  ← Sprint 1 centrepiece
  Platform.Orchestration.SpikeTests/       # Aspire stub-topology spike
samples/
  reference-provider/                      # throwaway provider exercising the contract (S02, stubbed here)
```

- **Reserved namespaces** `Platform.Engine.*` and `Platform.Steps.*` are documented as engine/provider-only
  in the solution README (BP §5.6); the startup guard that *enforces* refusal lands in Sprint 02 (`S02-F-03`).
- Engine targets **`net8.0`**. The SDK targets `net8.0` (and may multi-target `netstandard2.0` later for
  broad provider reach — deferred).

## 3. Pinned dependencies (`Directory.Packages.props`)

Recommended version lines (exact patch confirmed at first `restore`; majors are the decision):

| Package | Pin (major.minor) | Used by | Notes |
|---|---|---|---|
| `Microsoft.CodeAnalysis.CSharp.Scripting` | 4.x (latest stable) | Engine | The Roslyn scripting API; PoC core |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 3.3.x | Engine (analyzer) | Bans `EvaluateAsync` at build time (§4.5) |
| `Aspire.Hosting.AppHost` | **9.x** | Orchestration | Headless `DistributedApplication` |
| `Aspire.Hosting.PostgreSQL` | 9.x | Orchestration | Stub-topology dependency |
| `Polly` (Polly.Core) | **8.x** | Engine | `ResiliencePipeline` (v7 unsupported) |
| `System.Text.Json` | in-box `net8.0` | Engine | never Newtonsoft to providers |
| `JsonPath.Net` | latest | Engine | capture (Sprint 4) |
| `JsonSchema.Net` | 7.x | Tooling | draft 2020-12 (Sprint 2) |
| `YamlDotNet` | 16.x | Engine | parser (Sprint 3) |
| `Npgsql` | 8.x | MemoryTests | closure leak test |
| `Confluent.Kafka` | 2.x | MemoryTests | closure leak test |
| `MongoDB.Driver` | 3.x | MemoryTests | closure leak test |
| `StackExchange.Redis` | 2.x | MemoryTests | closure leak test |
| `Respawn` | 6.x | Orchestration | state reset (Sprint 4) |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | current | tests | test stack |

Sprint 1 only *references* Npgsql/Confluent/Mongo/Redis from `MemoryTests` to establish the closure;
their providers arrive in Sprints 3–4. Aspire is pinned **per engine release** (BP §5.7), advanced at
engine cadence — not Aspire's.

## 4. Memory-model PoC — the centrepiece (`S01-B-01/02/03`)

The make-or-break proof: a dynamically compiled script must be **fully reclaimable**. This is built in
three layers, all in `Platform.Engine` with the harness in `Platform.Engine.MemoryTests`.

### 4.1 `ScriptGlobalVariables` — the sole boundary
A typed host object; the script touches the environment **only** through it (no statics bridge the
boundary). Sprint-1 shape is minimal (a `Vars` dictionary + a sink for results); it grows in later sprints.

### 4.2 Compile **once** (`S01-B-01`)
```
Script<T> script = CSharpScript.Create<T>(code, ScriptOptions.Default
        .WithReferences(...).WithImports(...), globalsType: typeof(ScriptGlobalVariables));
Compilation compilation = script.GetCompilation();
using var ms = new MemoryStream();
EmitResult emit = compilation.Emit(ms);     // once
byte[] image = ms.ToArray();
```
**No `CSharpScript.EvaluateAsync` / `RunAsync`** — those JIT into the default `AssemblyLoadContext` and
leak an uncollectable assembly per call. We emit an image we control.

### 4.3 Isolate, invoke N×, unload (`S01-B-02`)
- `CollectibleScriptContext : AssemblyLoadContext(isCollectible: true)`; load the image via
  `LoadFromStream(new MemoryStream(image))`.
- Locate the emitted script entry point. A Roslyn script compiles to a generated `Submission#0` type
  with a static factory taking an `object[]` submission array (`[0]`=globals, `[1]`=result slot). We
  resolve it by reflection **once**, build a delegate, and invoke it **N times**, passing a fresh
  submission array carrying the `ScriptGlobalVariables` instance each call.
  *⚠ Verify the exact factory signature/name against the pinned `Microsoft.CodeAnalysis.CSharp.Scripting`
  — this is precisely the kind of detail CLAUDE.md says not to trust from memory.*
- After the run: drop all references, `context.Unload()`.

### 4.4 Measure baseline return (`S01-B-03`)
- Capture `GC.GetTotalMemory(forceFullCollection: true)` before and after a full load→invoke(N)→unload
  cycle; assert net delta ≤ threshold.
- Assert **collectibility** with the canonical pattern: hold a `WeakReference` to the context, then loop
  `GC.Collect(); GC.WaitForPendingFinalizers();` up to e.g. 10 iterations and assert `!weakRef.IsAlive`.
- **Proposed thresholds (for sign-off):** N = 5,000 iterations; net managed-heap growth ≤ 1 MB over the
  cycle; context collected within 10 GC iterations. Output is machine-readable (JSON/console) so Sprint 2
  can gate CI on it (`S02-D-01`).

### 4.5 `EvaluateAsync` guard
Add `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` banning
`M:Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.EvaluateAsync...`, so any reintroduction **fails
the build**, not a runtime review.

## 5. Library bootstrap & version-conflict fail-fast (`S01-B-04`)
A small harness loading the §5.7 client libraries inside the collectible context, plus a prototype that
detects an assembly-version conflict in the shared script context and throws a clear **suite-start**
error (productionised in `S02-B-02`). Establishes that the real client closures load and unload.

## 6. Orchestration spike (`S01-A-01/02/03`) — *needs Docker daemon*
- `S01-A-01`: build a headless `DistributedApplication` with `DisableDashboard = true`; suppress
  `Microsoft.Extensions.Diagnostics.HealthChecks` logs below `Warning`. *Verify the Aspire 9.x way to set
  `DisableDashboard` — the option surface shifted between Aspire 8 and 9.*
- `S01-A-02`: one container service + one Postgres via `AddContainer`/`AddProject` (**never**
  `AddProject<T>()`); `WaitFor` the **database**, not the server. Assert ≥ 20 clean startups.
- `S01-A-03`: resolve a connection string via `GetConnectionString(name)` **and** an HTTP endpoint via the
  retained `IResourceBuilder`'s `.GetEndpoint("http").Url` after `StartAsync`; confirm
  `app.GetEndpoint(name, scheme)` is never used (it doesn't exist).

## 7. Provider contract draft (`S01-F-01`)
In `Platform.Sdk`: `IStepProvider`, `IStepBinder<T>`, `IStepValidator<T>`, `IStepCompiler<T>`,
`IResourceContributor<T>`, the `[StepProvider]` attribute, and the `CsxFragment` record (three fields:
`RequiredUsings`, `RequiredHelpers`, one `StatementBlock`; with `SanitiseId`). Strongly-typed records, no
`Dictionary<string,object>`. XML-doc the **v1.x freeze** intent (BP §13.8.1). The resolve→bind→validate→
plan→emit pipeline contract is reviewed and signed off by TL + a compiler engineer before Sprint 2.

## 8. Event-stream schema skeleton (`S01-G-01`)
A versioned JSON Lines envelope record (`schemaVersion`, `eventType`, `timestamp`, correlation ids) in
`Platform.Engine`, serialised with `System.Text.Json`. Full event types (`step-attempt`, etc.) land in
`S02-G-01`. Design note records that renderers must tolerate unknown fields.

## 9. CI skeleton (`S01-D-02`)
A GitHub Actions workflow: restore → build → test → format check on every push, plus a **non-blocking**
`memory-leak` job placeholder that `S02-D-01` promotes to blocking.

## 10. Build order & PR breakdown
Recommended as **three small PRs** (reviewable, each independently green):

1. **PR-1 Scaffold + CI** — `S01-D-01`, `S01-D-02`, `global.json` SDK pin, banned-API analyzer, `Directory.*.props`. *(No Docker.)*
2. **PR-2 Memory model** — `S01-B-01/02/03/04` + `ScriptGlobalVariables`. The de-risking proof. *(No Docker.)*
3. **PR-3 Spike + contracts** — `S01-A-01/02/03`, `S01-F-01`, `S01-G-01`. *(Orchestration needs Docker.)*

PR-2 is the gate: if the memory model doesn't return to baseline over the real closure, **we pause and
correct the design** (MVP §8.1 exit criterion) before investing further.

## 11. Open decisions for sign-off
| # | Decision | Recommendation |
|---|---|---|
| 1 | Aspire major | **9.x stable** (supports .NET 8) |
| 2 | SDK namespace/project | `Platform.Engine.Abstractions` in packable `Platform.Sdk` |
| 3 | .NET install + SDK pin | **SessionStart hook** installs .NET 8 SDK; `global.json` pins SDK to `8.0.400` / `rollForward: latestFeature` |
| 4 | Test framework | **xUnit** |
| 5 | Leak thresholds | N=5,000; ≤1 MB net growth; collected ≤10 GC iters |
| 6 | PR granularity | three PRs as in §10 |

## 12. Sprint 01 Definition of Done (mirrors `sprint-01.md` exit criteria)
- A trivial script compiles **once**, runs ≥ 5,000× in a collectible context, unloads to baseline, and the
  context is provably collected; the `EvaluateAsync` ban is enforced at build time.
- The stub topology starts health-gated and resolves both a connection string and an HTTP endpoint.
- The provider contract interfaces and the event-stream envelope are drafted and reviewed.
- CI builds and tests on every push; the memory-leak stage exists (non-blocking).
- **If the memory model is in doubt, the sprint pauses for a design correction rather than carrying risk forward.**
