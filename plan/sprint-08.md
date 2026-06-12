# Sprint 08 — Full step set: freeze & publish (Milestone M3)

| | |
|---|---|
| **Phase** | 3 — Full step set (MVP §8.3) |
| **Weeks** | 15–16 |
| **Length** | 2 weeks |
| **Milestone** | **M3 — Full step set & SDK** closes at end of sprint |
| **Theme** | Freeze the contract a community is about to build against, publish the Provider SDK, add the second secret source, and ship the two reporting features that most repay the asynchronous-first design. |

## Sprint goal

All six Core providers work; the v1 JSON Schema and provider contract are frozen; the Provider SDK +
CONTRIBUTING.md + integration-test fixture + worked example are published and validated by an outside
contributor; the Vault secret source lands; and the terminal renderer shows the polling timeline and the
captured-variable thread. This is the M3 exit criterion (MVP §8.3).

## Entry assumptions

- Sprint 7 delivered: six providers complete, capture/substitution complete, runner selection +
  parallelism, vendor entity.

## Tasks

### Workstream B / F — Freeze the contracts

#### S08-F-01 · Freeze the v1 JSON Schema ✓
- **Owner:** TX · **Estimate:** 1.5d · **Depends on:** S02-C-02, S07-F-01 · **Spec:** DSL §8; MVP §8.3 (freeze v1 schema), §10 (contract-freeze gate)
- Finalise and version-stamp the unified JSON Schema (draft 2020-12) for v1 so tooling builds against a
  stable contract.
- **Acceptance:**
  - The schema is tagged `v1`, composed from all six providers' fragments, and frozen against change. ✓

#### S08-F-02 · Freeze the v1 provider contract (with extension path) ✓
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S05-F-01 · **Spec:** BP §13.8.1; MVP §8.3, §10 (engine-breaks-contract risk)
- Freeze the provider interfaces for the v1.x engine series; new optional capabilities arrive via
  extension interfaces (`IStepProviderV1_1`), never by mutating existing ones. This is the contract-freeze
  gate reviewed before the SDK is published.
- **Acceptance:**
  - The v1 contract is tagged and documented as frozen; the extension-interface mechanism is demonstrated. ✓

### Workstream F — Publish the Provider SDK

#### S08-F-03 · Publish the Provider SDK as a NuGet package
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S08-F-02 · **Spec:** BP §13; MVP §8.3 (publish the Provider SDK), §6.6
- Release the frozen C# contract as a NuGet package under Apache 2.0.
- **Acceptance:**
  - The SDK package installs cleanly in a fresh project and resolves the contract types.

#### S08-F-04 · CONTRIBUTING.md, integration-test fixture, worked example provider
- **Owner:** PC · **Estimate:** 2.5d · **Depends on:** S08-F-03 · **Spec:** BP §13; MVP §8.3, §6.6, §9.6 (Verified rubric), §10 (community-pathway risk)
- Write `CONTRIBUTING.md` with scope guardrails, document the integration-test fixture every Verified
  provider must pass, and publish the blueprint's worked example provider as a public reference.
- **Acceptance:**
  - The fixture runs against the worked example and passes; CONTRIBUTING.md states the Verified-tier
    rubric (MVP §9.6) and the reserved-namespace rule.

#### S08-F-05 · Outside-contributor validation of the SDK
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S08-F-04 · **Spec:** MVP §8.3 (validated by an outside contributor), §4.2 (community gate)
- Invite a small number of pre-pilot contributors to implement a non-Core provider end-to-end against the
  surface; treat any friction as a documentation/contract bug.
- **Acceptance:**
  - At least one outside contributor compiles and runs a non-Core provider end-to-end without
    platform-team help (the M3 SDK-validation gate).

### Workstream B — Compiler & runtime

#### S08-B-01 · Vault secret source ✓
- **Owner:** CR1 · **Estimate:** 2d · **Depends on:** S05-B-02 · **Spec:** BP §17; MVP §8.3 (Vault secret source), §10 (secrets risk)
- Add the HashiCorp Vault source alongside `env`, through the pluggable-source seam, resolving at
  step-execution time and returning `SecretString`.
- **Acceptance:**
  - A credential resolves from Vault at execution time; redaction holds; the reproducibility envelope
    still hashes only the reference. ✓

### Workstream G — Result reporting & diagnostics

#### S08-G-01 · Polling timeline renderer for RETRY steps ✓
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S06-B-02, S03-G-01 · **Spec:** BP §14; MVP §8.3 (polling timeline), §2.5 (positioning claim)
- Render the per-attempt polling timeline for RETRY steps from the individual `step-attempt` events —
  the feature that explains asynchronous failures rather than reducing them to timeouts.
- **Acceptance:**
  - A RETRY step renders a legible attempt-by-attempt timeline (timing + per-attempt outcome). ✓

#### S08-G-02 · Captured-variable thread renderer ✓
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S04-G-01 · **Spec:** BP §14; MVP §8.3 (captured-variable thread), §2.5
- Render the captured-variable thread showing where each value in a scenario originated; secret-derived
  values render redacted.
- **Acceptance:**
  - The thread shows provenance for every captured/substituted value; no secret value is shown. ✓

### Workstream C — Authoring tooling

#### S08-C-01 · Watch mode for local iteration
- **Owner:** TX · **Estimate:** 1.5d · **Depends on:** S07-C-01 · **Spec:** MVP §6.3 (watch mode), §4.2 (loop-time)
- Re-run affected suites on file change to compress the author→run loop.
- **Acceptance:**
  - Editing a `.e2e.yaml` re-runs it automatically without a full topology rebuild where possible.

#### S08-C-03 · Scenario-level parallelism (re-instated from S07)
- **Owner:** TL · **Estimate:** 2.5d · **Depends on:** S07-C-01 · **Spec:** BP §16.2; MVP §6.3 (parallelism)
- Implement concurrent scenario execution using the topology-per-scenario slot model: each scenario builds, owns, and disposes its own Aspire topology with isolation by construction (no Respawn). Bounded concurrency via `--parallel <n>` CLI flag with conservative default (min(cores,4)); deterministic render-in-declaration-order; complete-all cancellation semantics.
- **Acceptance:**
  - `vouchfx run --parallel 2` runs two scenarios concurrently against separate topologies; each scenario's topology is independent (no cross-scenario state); output is byte-stable regardless of which finishes first; all topologies dispose on cancellation.

### Workstream D — Integration

#### S08-D-01 · M3 phase-exit review package
- **Owner:** TL · **Estimate:** 0.5d · **Depends on:** all S08 tasks · **Spec:** MVP §5.5, §7.1, §8.3
- Assemble the M3 demo: six providers, RETRY determinism, frozen schema, SDK validated, polling timeline,
  multi-file parallel runner.
- **Acceptance:**
  - Reproducible demo recorded; steering review held; contract-freeze gate signed off.

## Exit criteria — Milestone M3 (MVP §8.3)

All six Core providers work, RETRY behaves deterministically, the schema is frozen for v1, the Provider
SDK has been validated by at least one outside contributor implementing a non-Core provider end-to-end,
the terminal renderer shows the polling timeline and captured-variable thread, and the runner can select
and parallelise a multi-file suite.

## Risks mitigated this sprint (MVP §10)

- Engine breaks the provider contract on a routine release (frozen v1.x + extension-interface path).
- Community pathway fails to materialise (SDK + fixture + worked example + outside-contributor validation).
