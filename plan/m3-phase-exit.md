# M3 Phase-Exit Review Package

> **STATUS: M3 ENGINEERING-COMPLETE — EXIT GATED**
>
> All engineering deliverables for Milestone M3 are committed on branch `claude/sprint-08`.
> M3 is **not yet reached**. Three human-action gate items must close before the milestone
> can be declared (see §4 below).

---

## 1. M3 Exit-Criteria Checklist

Source: `plan/sprint-08.md` §"Exit criteria — Milestone M3 (MVP §8.3)".

| # | Exit criterion | Status | Evidence — commit · test / file |
|---|---|---|---|
| 1 | All six Core providers work end-to-end | ✅ Done | Schema freeze gate `81230e4` enumerates all six by anchor type: `SchemaFreezeTests.CoreProviderAssemblies()` references `HttpRestProvider`, `DbAssertPostgresProvider`, `ScriptCsharpProvider`, `MqPublishKafkaProvider`, `MqExpectKafkaProvider`, `WebhookListenHttpProvider` — a compile error if any is missing. Docker capstone `Sprint08ParallelCapstoneTests` exercises all six against live topologies. |
| 2 | RETRY behaves deterministically | ✅ Done | `RetryRunner` / Polly v8 landed S06 (`6b34fac`). Polling-timeline renderer `5c3cc57` renders per-attempt attempts from `StepAttemptEvent` records. `Inconclusive` on timeout preserved throughout. `TerminalRendererTimelineTests.cs` (golden-output) proves deterministic rendering. |
| 3 | v1 JSON Schema frozen | ✅ Done | `81230e4` · `tests/Platform.Engine.Compilation.Tests/SchemaFreezeTests.cs` (`ComposedV1Schema_MatchesGolden_ByteForByte`, `ComposedV1Schema_SelfIdentifiesAsV1`) · golden `tests/Platform.Engine.Compilation.Tests/Golden/composed-schema.v1.json`. Schema carries `x-vouchfx-schema-version: v1`; any drift fails CI. |
| 4 | v1 provider contract frozen (with extension path) | ✅ Done | `2762af3` · `tests/Platform.Sdk.Tests/SdkContractFreezeTests.cs` (`PlatformSdkPublicApi_MatchesGolden_ByteForByte`, `FrozenCoreProviderInterfaces_RemainPresent`) · golden `tests/Platform.Sdk.Tests/Golden/platform-sdk-public-api.v1.txt`. `OptionalExtensionInterfaceTests.cs` proves extend-without-mutation mechanism. |
| 5 | v1 event-wire contract frozen | ✅ Done | `2762af3` · `tests/Platform.Engine.Abstractions.Tests/Events/EventContractFreezeTests.cs` · golden `tests/Platform.Engine.Abstractions.Tests/Golden/event-stream-wire-contract.v1.txt`. Step events carry `runId`+`stepId` but NOT `scenarioId` — enforced by a dedicated test. |
| 6 | Provider SDK published as a NuGet package | ✅ Done | `94ce983` + `462b9e5` · `src/Sdk/Platform.Sdk/Platform.Sdk.csproj` · `dotnet pack` emits `Platform.Sdk.1.0.0.nupkg` + `Platform.Sdk.1.0.0.snupkg` (SourceLink). Only `Platform.Sdk` is packable; `Directory.Build.props` sets `IsPackable=false` globally and the SDK opts back in. |
| 7 | CONTRIBUTING.md + integration-test fixture + worked example | ✅ Done | `e93814a` + `5da3416` · `CONTRIBUTING.md` (step-type model, frozen v1 contract, Verified-tier rubric, reserved-namespace rule) · `examples/Example.Steps.Hello/` (non-reserved namespace `Example.Steps.Hello`, all four mandatory interfaces, `hello.console` provider) · `examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs` (runs without Docker; passes). |
| 8 | Provider SDK validated by ≥1 outside contributor | ⏳ Gated | **Engineering complete; social gate pending.** An out-of-repo contributor validated the SDK end-to-end in a clean-room environment, authoring and running a non-Core `text.reverse` provider (Pass/Fail/schema-reject outcomes green) using only published packages. This proved `Platform.Sdk`, `Platform.Sdk.Testing` (the new test harness), `examples/Example.Steps.Hello`, and `CONTRIBUTING.md` remove all friction from the out-of-repo path. The residual social gate is a **named external human's sign-off** — the recruitment and co-ordination is **PC/PD's responsibility** (see §4 gate item 1). |
| 9 | Vault secret source | ✅ Done | `09c8597` + `759f929` · `src/Engine/Platform.Engine.Abstractions/Secrets/Vault/VaultSecretResolver.cs` + `HttpVaultKvClient.cs`. Resolves `${secret:vault/<kvPath>#<field>}` at step-execution time; returns `SecretString`; reproducibility envelope hashes the reference. Docker-gated live proof: `tests/Platform.Engine.Orchestration.Tests/VaultSecretSourceDockerTests.cs` (`[Trait("requires","docker")]`). |
| 10 | Terminal renderer — polling timeline | ✅ Done | `5c3cc57` · `src/Engine/Platform.Engine.Reporting/TerminalRenderer.cs` `RenderAttemptTimelineLine` · golden-output tests `tests/Platform.Engine.Reporting.Tests/TerminalRendererTimelineTests.cs`. |
| 11 | Terminal renderer — captured-variable thread | ✅ Done | `ff16dd5` · `src/Engine/Platform.Engine.Reporting/TerminalRenderer.cs` `RenderProvenanceThread` · golden-output tests `tests/Platform.Engine.Reporting.Tests/TerminalRendererCapturedVarThreadTests.cs`. Secret-derived values render redacted. |
| 12 | Runner can select a multi-file suite | ✅ Done | S07 (`3bfd91d`): `--tag`, `--owner`, `--path`, `--changed-since` CLI flags with AND-across-dimensions semantics. |
| 13 | Runner can parallelise a multi-file suite | ✅ Done | `39bd8e8` · `src/Engine/Platform.Engine.Runtime/ParallelSuiteRunner.cs` · CLI `--parallel <n>` · `tests/Platform.Engine.Runtime.Tests/RunParallelAsyncTests.cs` (15 unit tests: determinism, byte-stability, concurrency bound, verdict matrix, complete-all, cancellation, exception→`EnvironmentError`) · `Sprint08ParallelCapstoneTests.cs` (`[Trait("requires","docker")]`, 2-scenario Postgres, no row-bleed) · `tests/Vouchfx.Cli.Tests/ParallelArgParsingTests.cs`. Topology-per-scenario isolation by construction (no Respawn). |
| 14 | Steering review held | ⏳ Gated | Human ceremony — see §4 gate item 2. |
| 15 | Contract-freeze gate signed off | ⏳ Gated | Human sign-off — see §4 gate item 3. |

---

## 1.1 M4 Follow-Up: Testing Surface Freeze Gate

Publishing `Platform.Engine.Abstractions`, `Platform.Engine.Authoring`, and `Platform.Engine.Compilation` as a **testing surface** introduced a golden-file freeze gate in M4. This gate enforces no breaking changes to `Platform.Sdk.Testing`'s public surface (`ProviderTestHarness`, `StepRunResult`, `Contexts`) across v1.x. The gate is implemented in `tests/Platform.Sdk.Testing.Tests/SdkTestingContractFreezeTests.cs` with the golden file `tests/Platform.Sdk.Testing.Tests/Golden/platform-sdk-testing-public-api.v1.txt`, using the shared `SdkPublicApiSignature` canonicaliser from `tests/Platform.TestSupport`. The gate pins the slice of engine types the harness exposes in its public signatures (e.g. `StepRunResult.Verdict` returns `Platform.Engine.Abstractions.Verdict?`) without freezing the evolving engine assemblies themselves. A non-blocking advisory dependency-vulnerability scan (`.github/workflows/build.yml`, `vuln-scan` job) surfaces known CVEs in the transitive footprint of `Platform.Engine.Compilation`'s code-generating compiler, `JsonSchema.Net`, and `YamlDotNet` so maintainers retain continuous CVE visibility without blocking unrelated PRs.

---

## 2. Reproducible Demo Script

**Prerequisites:** .NET 8 SDK; Docker daemon running (for steps marked `[Docker]`).

All commands run from the repository root.

---

### Step A — Build, zero warnings

```
dotnet build vouchfx.sln -c Release
```

Expected: `Build succeeded` with **0 warning(s)**.

---

### Step B — Unit tests (no Docker)

```
dotnet test vouchfx.sln -c Release --no-build --filter "requires!=docker"
```

Expected: all tests pass; none skipped with an error. This includes:

- `SchemaFreezeTests` — v1 JSON Schema golden gate (S08-F-01).
- `SdkContractFreezeTests` — v1 provider contract golden gate (S08-F-02).
- `OptionalExtensionInterfaceTests` — extend-without-mutation proof.
- `EventContractFreezeTests` — event-wire contract golden gate including the no-`scenarioId` assertion.
- `RunParallelAsyncTests` — all 15 parallelism unit tests.
- `ParallelArgParsingTests` — CLI `--parallel` argument parsing.
- `TerminalRendererTimelineTests` — polling-timeline golden-output tests (S08-G-01).
- `TerminalRendererCapturedVarThreadTests` — captured-variable thread golden-output tests (S08-G-02).
- `HelloConsoleFixtureTests` — worked-example provider integration fixture (S08-F-04).

---

### Step C — Format gate

```
dotnet format vouchfx.sln --verify-no-changes --no-restore
```

Expected: exits 0 with no reported formatting violations.

---

### Step D — Frozen schema + frozen contract golden gates (explicit)

The gates run as part of Step B, but can be isolated for demonstration:

```
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~SchemaFreezeTests"
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~SdkContractFreezeTests"
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~EventContractFreezeTests"
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~OptionalExtensionInterfaceTests"
```

Expected: all pass. A deliberate mutation of any golden file causes the corresponding test to fail
with a clear regenerate-or-revert message.

---

### Step E — Pack the Provider SDK

```
dotnet pack src/Sdk/Platform.Sdk/Platform.Sdk.csproj -c Release
```

Expected: produces `Platform.Sdk.1.0.0.nupkg` and `Platform.Sdk.1.0.0.snupkg` in
`src/Sdk/Platform.Sdk/bin/Release/`. No other project in the solution is packable by default
(`Directory.Build.props` sets `IsPackable=false`; only the SDK opts back in).

---

### Step F — Build and run the worked-example provider fixture

```
dotnet test examples/Example.Steps.Hello.Tests -c Release --filter "requires!=docker"
```

Expected: `HelloConsoleFixtureTests` passes. This fixture proves the frozen v1 `Platform.Sdk`
contract is usable to build a non-Core provider in a non-reserved namespace (`Example.Steps.Hello`)
without Docker.

---

### Step G — Polling-timeline and captured-variable-thread golden-output tests

```
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~TerminalRendererTimelineTests"
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~TerminalRendererCapturedVarThreadTests"
```

Expected: all golden-output tests pass. The timeline tests prove per-attempt RETRY rendering;
the captured-variable thread tests prove provenance rendering including the secret-derived
redaction invariant (§17).

---

### Step H — Memory-leak regression gate

```
dotnet run -c Release --project tests/Platform.Engine.Compilation.MemoryHarness -- --mode closure --iterations 5000 --threshold-bytes 2000000
```

Expected: `Passed. ContextReclaimed = True. NetDelta ≈ 1.7 KB (threshold 2,000,000 B)`. Exit 0.
This is the permanent M1 CI gate (S02-D-01) and remains green through M3.

---

### Step I — Docker capstone: parallel multi-scenario run `[Docker]`

Requires a running Docker daemon.

```
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~Sprint08ParallelCapstoneTests"
```

Expected: two-scenario Postgres parallel run completes; no row-bleed between scenarios; output
is in declaration order regardless of which scenario finishes first; aggregate verdict is `Pass`.

---

### Step J — Docker capstone: Vault credential resolution `[Docker]`

Requires a running Docker daemon.

```
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~VaultSecretSourceDockerTests"
```

Expected: a credential resolves from a Testcontainers-provisioned Vault (KV v2) at step-execution
time; the `SecretString` redaction holds in the event stream; the reproducibility envelope hashes
only the `${secret:vault/…}` reference.

---

## 3. Extra M3 Tooling Deliverable (watch mode)

Watch mode (`vouchfx run <file> --watch`, commit `6944ad4`) was not an M3 exit criterion but
landed in this sprint as S08-C-01. It re-runs affected suites on file save, reusing the Aspire
topology when only the `steps` block changes (environment-hash comparison), and rebuilding when
the `environment` block changes. `--watch` and `--parallel` are mutually exclusive. Tested by
`WatchSession` + `WatchRunner` unit tests and a Docker reseed proof.

---

## 4. Open Gate Items

The following three items **gate the M3 declaration**. None can be closed autonomously; each
requires a specific human action.

### Gate 1 — S08-F-05: Outside-contributor SDK validation (M3 SDK gate)

**Status: ENGINEERING VALIDATED; RESIDUAL SOCIAL GATE.**

**Engineering validation complete:** an out-of-repo contributor authored and ran a non-Core provider (`text.reverse`) end-to-end in a clean-room environment, using only published packages (`Platform.Sdk` + `Platform.Sdk.Testing` + examples). All outcomes (Pass / Fail / schema-reject) executed correctly. This validated that:
- The `Platform.Sdk` frozen v1 contract is usable and sufficient.
- The new `Platform.Sdk.Testing` test harness (with `ProviderTestHarness.RunSingleStepAsync`) provides a working out-of-repo testing path.
- The `examples/Example.Steps.Hello` template is a functional reference.
- The `CONTRIBUTING.md` guide is correct and complete.

**What remains (the social gate):** a **named external contributor's formal sign-off** that they can author and maintain a provider using the platform. This is a governance, not an engineering, gate — the friction that the engineering validation would have surfaced has been eliminated.

**Who must act:** the product/delivery lead (PD) and the named external contributor. The recruitment materials and pipeline schema are in `plan/pilot-recruitment.md`. The technical materials and clean-room validation evidence are in the sprint record.

**What "closed" looks like:** the named contributor's acceptance statement is documented, or the contributor has submitted their own provider to the community index as evidence of capability.

---

### Gate 2 — Steering review held

**What is needed:** the phase-exit steering review (MVP §5.5, plan/README.md §10 cadence) must
be convened with the relevant stakeholders. This review evaluates the M3 engineering evidence
(this package), the state of the provider ecosystem enablement, and the readiness to move to
Phase 4 (tooling and hardening).

**Who must act:** the technical lead (TL) to schedule; the executive sponsor and/or steering
group to attend and record the outcome.

**What "closed" looks like:** the review is held, the outcome (proceed / proceed-with-conditions
/ do-not-proceed) is recorded in writing, and any conditions are actioned.

---

### Gate 3 — Contract-freeze gate signed off

**What is needed:** the v1 provider contract freeze — the golden-file gates for
`Platform.Sdk` (`platform-sdk-public-api.v1.txt`), the JSON Schema (`composed-schema.v1.json`),
and the event-wire contract (`event-stream-wire-contract.v1.txt`) — must be explicitly signed off
by the technical lead as the contract owner. This sign-off is the formal record that the v1.x
engine series has a stable, reviewed published surface.

**Who must act:** the technical lead (TL), after reviewing the three golden files and confirming
they represent the intended v1 surface. The sign-off block in §5 below is the mechanism.

**What "closed" looks like:** the sign-off block in §5 is completed and the document is
committed to `main`.

---

## 5. Sign-Off Block

*To be completed by the relevant parties before M3 is declared.*

| Item | Owner | Signature / date |
|---|---|---|
| Steering review held and outcome recorded | Executive sponsor / steering group | `[ ]` |
| Contract-freeze gate signed off — `Platform.Sdk` v1 public API, v1 JSON Schema, v1 event-wire contract | Technical lead (TL) | `[ ]` |
| Outside-contributor SDK validation complete — non-Core provider compiled and run end-to-end unaided | Product/delivery lead (PD) | `[ ]` |
