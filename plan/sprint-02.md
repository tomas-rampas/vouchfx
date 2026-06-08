# Sprint 02 — Foundations: close the risk (Milestone M1)

| | |
|---|---|
| **Phase** | 1 — Foundations (MVP §8.1) |
| **Weeks** | 3–4 |
| **Length** | 2 weeks |
| **Milestone** | **M1 — Foundations proven** closes at end of sprint |
| **Theme** | Turn the Sprint 1 proofs into durable gates: a provider-closure leak test in CI, a reference provider that exercises the whole contract, and the reporting and schema substrates the later phases build on. |

## Delivery status

**Implemented on `feat/sprint-02-foundations`** (PR #128 pending; date 2026-06-08) — all **13 tasks** delivered
with their acceptance criteria met. Build is 0-warning under `TreatWarningsAsErrors`, `dotnet format`
is clean, and the full non-docker unit suite is green (176 tests across five projects); the Docker
orchestration suite is green locally. Milestone **M1 "Foundations proven" is reached** — the
phase-exit evidence and a reproducible demo are recorded (see below).

Key proofs:

1. **Memory model over the full Core-provider closure, gated in CI.** The closure harness
   (`--mode closure --iterations 5000`) returns to baseline (NetDelta ≈ +1.7 KB against a 2 MB
   threshold, 0 collectible ALCs leaked, `ContextReclaimed=true`, exit 0). The memory-leak job is now
   **blocking** and **scheduled weekly** (S02-B-01, S02-B-02, S02-D-01).
2. **Provider contract exercised end-to-end** by a reflectively-discovered reference provider; the
   reflective `StepKindRegistry` is frozen at startup; a reserved-namespace guard refuses squatters
   (S02-F-01, S02-F-02, S02-F-03).
3. **Event-stream schema + first JSON Schema draft** with a verdict taxonomy, typed payloads, a stub
   terminal renderer, and provider-driven schema composition (S02-G-01, S02-G-02, S02-C-01, S02-C-02).
4. **Stub topology reliable + environment errors classified distinctly** (≥50-run clean streak;
   Environment-error verdict carrying registry host + auth status, never Fail) (S02-A-01, S02-A-02).

Reproduce from a clean checkout: `pwsh -File scripts/m1-demo.ps1` (pillars 0–3) or
`pwsh -File scripts/m1-demo.ps1 -IncludeDocker` (adds the orchestration pillar) — both pass (exit 0).
Full M1 evidence: [`docs/reviews/m1-exit-evidence.md`](../docs/reviews/m1-exit-evidence.md).

## Sprint goal

The memory model is proven not just for a trivial script but across the **full transitive dependency
closure of every Core provider** and is enforced as a permanent CI gate; the provider contract has been
exercised end-to-end by a throwaway reference provider discovered reflectively at startup; and the
event-stream schema plus the first JSON Schema draft are in place. At sprint end the project either
clears M1 or pauses for a design correction (MVP §8.1 exit criterion).

## Entry assumptions

- Sprint 1 delivered: compile-once/unload PoC, baseline memory harness, stub topology, contract draft,
  event-stream envelope.

## Tasks

### Workstream B — Compiler & runtime

#### S02-B-01 · Provider-closure memory leak test
- **Owner:** CR1 · **Estimate:** 3d · **Depends on:** S01-B-03 · **Spec:** BP §5, §5.6; MVP §8.1 (provider-closure memory test), §4.2, §10
- Extend the baseline harness to compile-and-unload a script whose dependency closure pulls in Npgsql,
  Confluent.Kafka, MongoDB.Driver, StackExchange.Redis and their transitive packages. Static state in
  library singletons (HttpClient handler pools, Npgsql pools, Confluent producer caches, OpenTelemetry
  tracers) can pin objects across the collectible boundary; only a closure-level test catches it.
- **Acceptance:**
  - A 5,000-iteration run over the full Core closure returns to baseline within tolerance (MVP §4.2).
  - Any pinning singleton is identified and either reset per-cycle or documented as a known constraint.
  - The test is deterministic on the team's standardised CI hardware.

#### S02-B-02 · Dynamic assembly resolution for the script context
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S01-B-02 · **Spec:** BP §5.6; CLAUDE.md assembly-graph hygiene
- Resolve provider/customer assemblies into the *same* collectible context as the generated script, and
  fail fast at suite start on version conflicts rather than at runtime.
- **Acceptance:**
  - Provider assemblies load into the script's collectible context and unload with it.
  - A version conflict produces a clear suite-start error naming the conflicting assemblies.

### Workstream D — Integration & hardening

#### S02-D-01 · Wire the leak test as a permanent CI gate
- **Owner:** TL · **Estimate:** 1d · **Depends on:** S02-B-01, S01-D-02 · **Spec:** MVP §8.1, §4.2, §10 (top risk)
- Promote the provider-closure leak test from non-blocking to a **blocking** CI stage; this is a Phase 1
  deliverable, not an afterthought.
- **Acceptance:**
  - A regression that reintroduces a leak fails the build.
  - The gate runs on the standardised hardware profile and is scheduled weekly through Phases 2–5.

#### S02-D-02 · M1 phase-exit review package
- **Owner:** TL · **Estimate:** 1d · **Depends on:** all S02 tasks · **Spec:** MVP §5.5, §7.1, §8.1
- Assemble the demonstrable M1 evidence for the steering review: memory proof, orchestration reliability,
  reference-provider walkthrough.
- **Acceptance:**
  - A reproducible-from-clean-checkout demo script exists; steering review held; go-forward recorded.

### Workstream A — Orchestration foundation

#### S02-A-01 · Health-gate hardening & flake budget
- **Owner:** OR · **Estimate:** 1.5d · **Depends on:** S01-A-02 · **Spec:** BP §4; MVP §4.2 (suite startup), §10
- Tighten `WaitFor` semantics with generous-but-bounded timeouts, and capture a startup-time figure
  toward the < 90s diagnostic (one service + three dependencies, measured later).
- **Acceptance:**
  - ≥ 50 consecutive clean startups with zero engine-attributable flakes.
  - A failed health gate is classifiable as an **environment error**, never an assertion failure (§12.1).

#### S02-A-02 · Environment-error classification hook
- **Owner:** OR · **Estimate:** 1d · **Depends on:** S01-G-02 · **Spec:** BP §12.1; MVP §10 (seeding/orchestration risks)
- Emit a distinct **Environment error** signal (not Fail) for pull/health/discovery failures, carrying
  registry hostname and auth status for image-pull failures.
- **Acceptance:**
  - An induced unhealthy container yields an Environment-error event, never a Fail.

### Workstream F — Provider SDK & Core providers

#### S02-F-01 · Throwaway reference provider exercising the full lifecycle
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S01-F-01 · **Spec:** BP §13; MVP §8.1 (provider contract draft), §10
- Implement a disposable reference provider that drives every contract stage (resolve→bind→validate→
  plan→emit) so the compiler workstream can build against a stable contract from the start of Phase 2.
- **Acceptance:**
  - The reference provider compiles a real (if trivial) CSX fragment end-to-end through the contract.
  - Its emitted fragment obeys the CsxFragment rules (three fields, no `using var`, `$$"""…"""`,
    `SanitiseId`, `Vars`-only state) — reviewed against BP §13.3.1.

#### S02-F-02 · Reflective StepKindRegistry, frozen at startup
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S02-F-01 · **Spec:** BP §13; CLAUDE.md provider model
- Build the reflection-based registry that discovers `[StepProvider]` types at startup and then freezes;
  no runtime/dynamic plugin loading.
- **Acceptance:**
  - The registry discovers the reference provider via reflection and rejects post-freeze mutation.
  - Step type keys follow `<family>.<provider>`.

#### S02-F-03 · Reserved-namespace startup guard
- **Owner:** PC · **Estimate:** 1d · **Depends on:** S02-B-02 · **Spec:** BP §5.6; CLAUDE.md assembly-graph hygiene
- Refuse, at startup, any customer DLL declaring types under `Platform.Engine.*` or `Platform.Steps.*`.
- **Acceptance:**
  - A DLL squatting a reserved namespace is refused with a clear startup error.

### Workstream G — Result reporting & diagnostics

#### S02-G-01 · Full event-stream schema
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S01-G-01 · **Spec:** BP §14; MVP §8.1 (event-stream schema draft), §6.7
- Define the event types the renderers and Healer consume: `scenario-started`, `step-started`,
  `step-attempt`, `step-completed`, `scenario-completed`. Each `step-attempt` is recorded individually
  so the RETRY polling timeline is renderable without re-running.
- **Acceptance:**
  - All five event types serialise/deserialise; `step-attempt` carries attempt index, timing, outcome.
  - Verdict category (Pass/Fail/Environment error/Inconclusive) is a first-class field (§12.1).

#### S02-G-02 · Minimal terminal renderer stub
- **Owner:** PC · **Estimate:** 1d · **Depends on:** S02-G-01 · **Spec:** BP §14; MVP §8.1, §6.7
- A stub renderer that consumes the JSON Lines stream and prints per-step verdicts. It becomes the
  compiler workstream's own integration-test surface from Phase 2 (so reporting is exercised
  continuously, not retro-fitted — MVP §10 reporting risk).
- **Acceptance:**
  - The stub prints verdicts from a recorded stream and ignores unknown fields without erroring.

### Workstream C — Authoring tooling (schema bootstrap)

#### S02-C-01 · JSON Schema — four top-level sections
- **Owner:** TX · **Estimate:** 1.5d · **Depends on:** S01-F-01 · **Spec:** DSL §3, §8; MVP §8.1 (schema first draft)
- Draft the JSON Schema (draft 2020-12, `JsonSchema.Net`) for `metadata`, `environment`, `variables`,
  `steps`, with only `steps` mandatory.
- **Acceptance:**
  - A minimal valid `.e2e.yaml` validates; a malformed one is rejected with a useful message.

#### S02-C-02 · JSON Schema — http.rest fragment + composition mechanism
- **Owner:** TX · **Estimate:** 1d · **Depends on:** S02-C-01, S02-F-02 · **Spec:** DSL §5, §8; MVP §8.1, §6.6
- Demonstrate the schema-composition mechanism by contributing the `http.rest` provider's fragment into
  the unified schema from the provider's own definition.
- **Acceptance:**
  - The composed schema validates an `http.rest` step; the fragment originates from the provider, not
    a hand-maintained central file.

## Exit criteria — Milestone M1 (MVP §8.1)

- The memory model is demonstrably sound over the **full Core provider dependency closure**, gated in CI.
- The stub topology starts reliably and classifies environment errors distinctly.
- The provider contract has been exercised end-to-end by a reference provider discovered reflectively.
- The event-stream schema and first JSON Schema draft exist.
- **If any of these is in doubt, the plan pauses here for a design correction rather than carrying risk
  forward.**

## Risks mitigated this sprint (MVP §10)

- Memory model (now a permanent CI gate) · Provider contract shape (exercised by a real provider) ·
  Orchestration flakiness (50-run clean streak) · Reporting-as-afterthought (stream + stub renderer live).
