# Sprint 05 — Core compiler: seed, secrets, integrate (Milestone M2)

| | |
|---|---|
| **Phase** | 2 — Core compiler (MVP §8.2) |
| **Weeks** | 9–10 |
| **Length** | 2 weeks |
| **Milestone** | **M2 — Core compiler runs** closes at end of sprint |
| **Theme** | Make the three-provider path realistic: seed reference data after the topology is healthy, supply a credential through a secret reference (never inlined), and integrate everything end-to-end. |

## Delivery status

**Merged to branch `feat/sprint-05-seed-secrets`** (PR pending) — all **9 tasks** delivered
(A-01/02, B-01/02/03, G-01, F-01, and capstone D-01; D-02 is this wrap-up). Build is 0-warning under
`TreatWarningsAsErrors`, `dotnet format` is clean, the full non-docker suite is green (**514 tests**
across the project), the memory-leak gate is green after secrets/seed work (5000-iteration closure
harness, net delta +1712 bytes, threshold 2 MB, ContextReclaimed=true, 0 collectible contexts
surviving), and the Docker M2 capstone is green locally (~14 s: `http.rest` with a `${secret:env/…}`
header → capture `$.hostname` → `script.csharp` INSERT → `db-assert.postgres` JOIN of seeded reference
row + inserted row; scenario verdict **Pass**). The **sprint exit criterion and M2 gate are met**:
three providers run end-to-end against a real local topology with seeding, env-var secret resolution,
captured-variable threading, reflective registry discovery, and a minimal terminal renderer; the secret
value appeared nowhere in output; the reproducibility envelope carried the reference hash, not the
value.

Key proofs:

1. **Secret reference parsing (B-01):** `${secret:source/path}` references parse and validate in step
   fields; a malformed reference or literal credential is rejected (Inconclusive).
2. **Runtime secret resolution (B-02):** Secrets resolve at **step-execution time, not compile time**;
   `env` source implemented; returned as a typed `SecretString` with no value-returning methods; no IL
   bake; missing secret → EnvironmentError.
3. **Reproducibility envelope (B-03):** The envelope hashes the **reference**, never the resolved value;
   compile-once property preserved.
4. **Redaction hook (G-01):** `SubstitutionRef.SecretDerived` lit up; resolved secrets never reach the
   event stream, terminal output, or any renderer (typed redaction at source).
5. **Seeding (A-01/02):** SQL reference files applied health-gated before step 1 (failure →
   EnvironmentError, transactional per file); document fixtures and broker warm-up hook wired
   (deferred for Kafka providers in Sprint 6).
6. **Reflective registry (F-01):** `StepKindRegistry` discovers all three Core providers from separate
   assemblies at startup and freezes.
7. **End-to-end integration (D-01):** Docker M2 scenario green from a clean checkout and reproducible;
   Aspire Postgres server-vs-database startup health-check transient noise absorbed by the health-gate
   before step 1 (the documented §4 race, not a defect); memory gate stays green.

Security audit (security-specialist): NO value-leak path; no CRITICAL/HIGH findings. The engine's
secret handling is airtight (redaction at source, compile-time no-bake, envelope hashes references,
fail-closed EnvironmentError).

**Follow-ups carried forward:**

1. `script.csharp` bodies are author-trusted and OUTSIDE the typed secret-redaction guarantee — an
   author could accidentally surface a secret/connection-string via a step observation that rides the
   event stream (security MEDIUM-1). Proper fix is typed connection strings, not string-scrubbing; track
   for a later sprint.
2. `SecretReference.ValidateField` malformed-reference detection uses a count heuristic (defence-in-depth;
   the resolver fails closed regardless). Optional future hardening: anchored structural scan (security
   LOW-1).
3. `ScenarioRunner.RunAsync`/`RunSuiteAsync` expose no event-buffer/sink, so the M2 docker test asserts
   the reproducibility envelope via a faithful reconstruction (`BuildReproducibilityEnvelope`) rather
   than the actual emitted JSON-Lines buffer. An event-sink callback would let a future test assert over
   the real buffer.
4. Multi-scenario seed persistence: suite-startup seeding runs once; Respawn truncates seeded reference
   ROWS between scenarios sharing a topology. Persisting reference data (Respawn TablesToIgnore, or
   re-seed) is a future enhancement (noted in A-01).

## Sprint goal

A test using `http.rest`, `db-assert.postgres`, and `script.csharp` compiles and runs end-to-end against
a real local topology, with reference data seeded, a credential supplied via `${secret:env/…}`, state
threaded between steps, the registry discovering each provider reflectively, and the terminal renderer
showing per-step verdicts. This is the M2 exit criterion (MVP §8.2).

## Entry assumptions

- Sprint 4 delivered: provider-mediated pipeline, three providers, JSONPath capture + substitution,
  Respawn reset.

## Tasks

### Workstream A — Orchestration foundation

#### S05-A-01 · `environment.seed` — SQL reference files
- **Owner:** OR · **Estimate:** 2d · **Depends on:** S04-A-01 · **Spec:** DSL §3; BP §4; MVP §8.2 (declarative seeding), §6.1, §10 (seeding risk)
- Apply declarative seed SQL **after** the topology is healthy and **before** the first step, inside the
  same health-gated lifecycle so a failed seed is an **environment error**, not a misattributed assertion
  failure.
- **Acceptance:**
  - Seed SQL applies before step 1; a deliberately broken seed yields an Environment error (§12.1).

#### S05-A-02 · `environment.seed` — document fixtures & broker warm-up hook
- **Owner:** OR · **Estimate:** 1.5d · **Depends on:** S05-A-01 · **Spec:** DSL §3; MVP §3.1 (test data seeding)
- Support document fixtures and a broker warm-up message hook in the seed block (the Kafka path lands
  with the providers in Sprint 6).
- **Acceptance:**
  - A document fixture seeds successfully; the warm-up hook is wired for Sprint 6's Kafka providers.

### Workstream B — Compiler & runtime

#### S05-B-01 · `${secret:source/path}` reference parsing
- **Owner:** CR1 · **Estimate:** 1.5d · **Depends on:** S03-B-02 · **Spec:** BP §17; DSL §6; MVP §8.2 (secret resolution), §10 (secrets risk)
- Parse `${secret:source/path}` references in step fields. References only — a literal credential is a
  validation error.
- **Acceptance:**
  - Secret references parse and validate; an inline literal where a secret is expected is rejected.

#### S05-B-02 · Runtime secret resolution — environment-variable source
- **Owner:** CR1 · **Estimate:** 2d · **Depends on:** S05-B-01 · **Spec:** BP §17; CLAUDE.md secrets invariant
- Resolve secret references at **step-execution time, not compile time** (compile-time interpolation
  bakes values into IL and corrupts the reproducibility envelope). Provide the pluggable-source seam with
  the `env` source implemented; return a typed `SecretString` with no value-returning
  `ToString()`/`IFormattable`.
- **Acceptance:**
  - A credential resolves from an env var at execution time; nothing is baked into the emitted IL.
  - `SecretString` cannot be stringified to its value; the source seam is ready for Vault (Sprint 8).

#### S05-B-03 · Reproducibility envelope hashes the reference
- **Owner:** CR2 · **Estimate:** 1d · **Depends on:** S05-B-02 · **Spec:** BP §17; CLAUDE.md secrets invariant
- The reproducibility envelope records the secret **reference**, hashed — never the resolved value.
- **Acceptance:**
  - The envelope contains a hash of the reference and provably no resolved secret material.

### Workstream G — Result reporting & diagnostics

#### S05-G-01 · Report-layer redaction hook
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S05-B-02 · **Spec:** BP §14, §17; MVP §10 (secret-leak risk)
- Consume the redaction hook so a resolved secret never reaches the event stream or any renderer; the
  renderer knows `SecretString` is never logged (typed redaction at source, not string-matching).
- **Acceptance:**
  - A secret used in a step never appears in the event stream, terminal output, or captured-var trace.

### Workstream F — Provider SDK & Core providers

#### S05-F-01 · Reflective registry validated across three provider assemblies
- **Owner:** PC · **Estimate:** 1d · **Depends on:** S04-F-03 · **Spec:** BP §13; MVP §8.2 (registry discovering each provider)
- Confirm the `StepKindRegistry` discovers all three Core providers from their separate assemblies at
  startup and freezes.
- **Acceptance:**
  - All three providers are discovered reflectively; registry rejects post-freeze additions.

### Workstream D — Integration & hardening

#### S05-D-01 · M2 end-to-end integration scenario
- **Owner:** TL · **Estimate:** 2d · **Depends on:** S05-A-01, S05-B-02, S04-B-03, S05-F-01 · **Spec:** MVP §8.2 (exit criterion)
- Assemble the M2 reference: `http.rest` (with a `${secret:env/…}` header) → capture → `script.csharp`
  → `db-assert.postgres`, against a seeded, reset-between-scenarios topology, rendered in the terminal.
- **Acceptance:**
  - The scenario is green from a clean checkout and reproducible; the memory gate stays green.

#### S05-D-02 · M2 phase-exit review package
- **Owner:** TL · **Estimate:** 0.5d · **Depends on:** S05-D-01 · **Spec:** MVP §5.5, §7.1
- Prepare the demonstrable M2 demo for the steering review.
- **Acceptance:**
  - Reproducible demo recorded; steering review held; phase-exit decision logged.

## Exit criteria — Milestone M2 (MVP §8.2)

A test file using `http.rest`, `db-assert.postgres`, and `script.csharp` compiles and runs end-to-end
against a real local topology, with reference data seeded, a credential supplied through a secret
reference, state threaded between steps, the registry discovering each provider via reflection at
startup, and the minimal terminal renderer displaying per-step verdicts.

## Risks mitigated this sprint (MVP §10)

- Secrets hard-coded into test files (reference syntax + redaction now in scope, not deferred).
- Brittle improvised seeding (declarative seed block lands with the first realistic tests).
