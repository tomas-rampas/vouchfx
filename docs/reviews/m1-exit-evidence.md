# M1 Phase-Exit Evidence Document

| | |
|---|---|
| **Milestone** | M1 — Foundations proven |
| **Phase** | 1 — Foundations |
| **Closes** | Sprint 2 (end 2026-06-08) |
| **Branch** | `feat/sprint-02-foundations` |
| **Status** | Complete; steering review recommended |

## Verdict

**M1 reached. Go-forward recommended to Phase 2 (Sprint 3).**

---

## Exit Criteria & Evidence

### 1. Memory model demonstrably sound over full Core-provider closure, gated in CI

**Evidence:** The standalone harness `tests/Platform.Engine.Compilation.MemoryHarness` exercises the transitive dependency closure of all Core canonical clients (Npgsql, Confluent.Kafka, MongoDB.Driver, StackExchange.Redis, HttpClient) over 5,000 iterations.

**Verified result:**
- NetDelta = +1,712 bytes against a 2 MB threshold (~1,000× headroom)
- CollectibleBefore = 0, CollectibleAfter = 0, ContextReclaimed = true
- Exit code 0
- Pinning singletons identified and handled per-cycle:
  - Npgsql connection pools cleared via `ClearAllPools()`
  - HttpClient disposed at each cycle boundary
  - Confluent.Kafka, MongoDB.Driver, StackExchange.Redis allocate no persistent handles at probe level
  - OpenTelemetry not in closure for Phase 1

The harness is now a **blocking CI gate** (`.github/workflows/build.yml`, job `memory-leak`, `continue-on-error` removed) and **scheduled weekly on Mondays 06:00 UTC** for ongoing assurance through Phases 2–5.

**References:** BP §5, §5.6; MVP §4.2, §8.1, §10.
**Tasks:** S02-B-01, S02-B-02, S02-D-01.

---

### 2. Provider contract exercised end-to-end by reference provider, discovered reflectively

**Evidence:** A throwaway reference provider (`NoopEcho`, consolidated into a single `[StepProvider]` class implementing all five contract interfaces) drives the full lifecycle: resolve → bind → validate → plan → emit.

**Verified behaviour:**
- The reflective `StepKindRegistry` discovers the provider by `<family>.<provider>` key at startup
- The provider's emitted CSX fragment compiles without error
- Fragment execution writes to the shared `Vars` global through the collectible-`AssemblyLoadContext` pipeline
- Registry is frozen by construction (no post-startup mutation API)
- Reserved-namespace guard refuses customer DLLs declaring types under `Platform.Engine.*` or `Platform.Steps.*` with a clear startup error

The reference provider is validated against the CsxFragment rules (BP §13.3.1):
- Three fields only: `RequiredUsings`, `RequiredHelpers`, `StatementBlock`
- No `using var` (parse error in CSX)
- ID sanitisation applied (`-` → `_`)
- Double-dollar raw strings (`$$"""…"""`) for body interpolation
- Cross-step state via `Vars` only

**References:** BP §13, §13.3.1, §5.6; MVP §8.1, §10.
**Tasks:** S02-F-01, S02-F-02, S02-F-03.
**Test:** `ReferenceProviderEndToEndTests`.

---

### 3. Event-stream schema and first JSON Schema draft exist

**Evidence:** The schema-versioned JSON Lines event stream now carries typed payloads and the Verdict taxonomy as first-class concepts.

**Event stream (BP §14):**
- Five event types: `scenario-started`, `step-started`, `step-attempt`, `step-completed`, `scenario-completed`
- `step-attempt` records attempt index, timing, and outcome individually (enables RETRY polling timeline rendering without re-running)
- Verdict taxonomy (BP §12.1): four outcomes (Pass / Fail / Environment error / Inconclusive) as wire tokens (PASS / FAIL / ENV_ERROR / INCONCLUSIVE)
- Flat envelope structure allows renderers to tolerate and round-trip unknown fields
- Serialisation/deserialisation via `System.Text.Json`

**JSON Schema (draft 2020-12):**
- Covers four top-level sections: `metadata`, `environment`, `variables`, `steps` (only `steps` mandatory)
- Schema-composition mechanism demonstrated: `http.rest` provider fragment originates from the provider's own definition, not a hand-maintained central file
- Minimal valid test `.e2e.yaml` validates; malformed inputs rejected with useful error messages

**Minimal terminal renderer stub:**
- Consumes JSON Lines stream and prints per-step verdicts
- Tolerates unknown event types and fields without error
- Serves as the compiler workstream's integration-test surface from Phase 2 onward (reporting exercised continuously, not retrofitted — mitigates MVP §10 reporting risk)

**References:** BP §12.1, §14, §13.6; DSL §3, §5, §8.
**Tasks:** S02-G-01, S02-G-02, S02-C-01, S02-C-02.

---

### 4. Stub topology starts reliably and classifies environment errors distinctly

**Evidence:** Health-gate hardening with bounded per-resource timeouts and startup-timing capture; Docker-gated reliability suite performs ≥50 consecutive clean startups.

**Verified behaviour:**
- ≥50 consecutive clean startups with zero engine-attributable flakes (locally observed ~13–18 s/startup, well under the <90 s diagnostic target)
- Failed health gates classified as **Environment error**, never assertion failure
- Distinct `Environment-error` signal (verdict EnvironmentError, never Fail) emitted for pull/health/discovery failures
- Registry hostname and authentication status captured for image-pull failures
- Induced bad-image scenario yields `OrchestrationException` classified as `EnvironmentError`
- `WaitFor` targets the **most specific** resource (database, not server) to avoid fast-hardware race

**References:** BP §4, §12.1; MVP §8.1, §10.
**Tasks:** S02-A-01, S02-A-02.

---

## Reproducible Demo

### Running the demo

Both single-command demos are reproducible from a clean checkout:

```powershell
# Pillars 0–3 (no Docker required)
pwsh -File scripts/m1-demo.ps1

# All four pillars including orchestration
pwsh -File scripts/m1-demo.ps1 -IncludeDocker
```

### What each pillar proves

- **Pillar 0: Build.** `dotnet restore` + `dotnet build -c Release` (the 0-warning gate); skipped with `-SkipBuild`.
- **Pillar 1: Memory (provider closure).** The closure harness loads the emitted script into a collectible `AssemblyLoadContext`, invokes it 5,000 times over the full Core-client closure, and unloads to baseline.
- **Pillar 2: Provider contract.** The non-docker `Platform.Sdk.Tests` exercise the reference provider end-to-end (resolve → bind → validate → plan → emit) and the reflective `StepKindRegistry`.
- **Pillar 3: Schema & reporting.** The non-docker suite covers the event-stream schema + Verdict taxonomy, the draft-2020-12 JSON Schema, provider-driven schema composition, and the terminal renderer.
- **Pillar 4 (Docker):** the Aspire stub topology starts health-gated deterministically and environment errors are classified distinctly from failures; **skipped** unless `-IncludeDocker` is supplied.

### Observed result

Both runs complete with **exit 0** and emit a per-pillar summary. Without `-IncludeDocker`, pillars 0–3
**PASS** and pillar 4 is **SKIPPED**; with `-IncludeDocker`, all four active pillars **PASS**:

```
Pillar 0: Build                  PASS
Pillar 1: Memory (Closure)       PASS    netDelta=1712 bytes (threshold 2 MB)
Pillar 2: Provider Contract      PASS
Pillar 3: Schema & Reporting     PASS
Pillar 4: Orchestration (Docker) PASS    (SKIPPED without -IncludeDocker)
```

A machine-readable JSON summary is also emitted (per-pillar status + metrics + overall verdict), and the
script exits non-zero if any non-skipped pillar fails.

---

## CI Gates Now Permanent

- **Memory-leak regression test** (S02-D-01): blocking gate in `.github/workflows/build.yml`, job `memory-leak`; weekly schedule Mondays 06:00 UTC.
- **Build/format/unit gates:** existing CI suite remains green.
- **Docker integration job** (orchestration test suite): included in build pipeline; exercises Pillar 4 on standardised hardware.

---

## Risks Retired This Sprint

Per MVP §10, the following risks now carry permanent mitigation:

1. **Memory model reliability** — Proven across full transitive closure of Core providers and gated in CI permanently. The hard invariant (compile-once, isolate, unload) is enforced by a mechanically-checked test harness that regresses the entire codebase if violated.

2. **Provider-contract shape uncertainty** — Exercised end-to-end by a real (though throwaway) reference provider implementing all five contract stages. The frozen `StepKindRegistry` and reserved-namespace guard prevent accidental contract violations at scale.

3. **Orchestration flakiness** — 50+ consecutive clean startups achieved; health-gated startup deterministic on standardised hardware. Startup timing captured toward the <90 s diagnostic.

4. **Reporting as afterthought** — Event-stream substrate and minimal renderer live; reporting is exercised continuously from Phase 2 onward (not retrofitted), preventing late-stage surprises.

---

## Carry-Forward into Phase 2 (Sprint 3)

M1 deliberately defers to Phase 2 and beyond:

- **Full http.rest runtime** (currently schema only) — Phase 2 (Sprint 3).
- **Parser → AST → production Roslyn pipeline** (the deterministic YAML-to-C# compilation proper) — Phase 2.
- **Core providers db-assert and script.csharp** — Phase 2.
- **Seeding, secrets resolution** — Phase 2 (Secrets runner via `Vars.Secrets.Resolve`; DSL §5, §17).
- **RETRY/Polly resilience** — Phase 3.
- **v1 contract/schema freeze** — Milestone M1.5 (end Phase 2).

These deferments were planned (MVP §8.1) and carry no latent risk; the hard invariants that would be violated by them are already proven sound.

---

## Go-Forward Assessment

**Recommendation: Go to Phase 2 (Sprint 3).**

All four M1 exit criteria are demonstrably met, all risks retired per plan, and both reproducible demos run cleanly from a fresh checkout. The memory model is gated permanently, the provider contract is proven, the reporting substrate is live, and the orchestration baseline is established. Phase 2 proceeds with high confidence in the foundations.

---

**Document date:** 2026-06-08  
**Phase-exit review:** package prepared; steering review pending sign-off  
**On sign-off:** record M1 status in `plan/roadmap.md` and merge `feat/sprint-02-foundations`
