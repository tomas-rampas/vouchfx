# Sprint 04 — Core compiler: the provider-mediated pipeline

| | |
|---|---|
| **Phase** | 2 — Core compiler (MVP §8.2) |
| **Weeks** | 7–8 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M2** (closes Sprint 5) |
| **Theme** | Generalise from one provider to a true provider-mediated compiler — resolve, bind, validate, plan, emit — with two more Core providers, cross-step variable capture, and state reset. |

## Delivery status

**Implemented on `feat/sprint-04-pipeline`** (PR pending; date 2026-06-08) — all **10 tasks** delivered
(B-01/02/03, F-01/02/03, A-01/02, G-01, capstone) across 8 commits. Build is 0-warning under
`TreatWarningsAsErrors`, `dotnet format` is clean, the full non-docker suite is green (**390 tests**
across ten projects), and the Docker capstone + Respawn reset-proof + db-assert suites are green
locally. The **sprint exit criterion is met**: a suite chaining `http.rest` (capture `$.hostname`) →
`script.csharp` (INSERT keyed by the capture) → `db-assert.postgres` (`{hostname}` param + `{var}`
expect) compiles through the provider-mediated pipeline and runs as a 2-scenario suite via
`RunSuiteAsync` against a build-once topology with Respawn reset between scenarios — both scenarios
PASS (Respawn is load-bearing: the captured hostname is the PRIMARY KEY, so without reset scenario 2
would violate it), independently re-verified.

Key proofs:

1. **Pipeline (B-01):** `ProviderPipeline.Compile` — resolve → bind → validate → plan → emit, assembling
   fragments with deduped usings/helpers and ordered statement blocks; extracted from `ScenarioRunner`
   as a pure, unit-testable compile stage.
2. **Three Core providers in separate assemblies:** `db-assert.postgres` (F-01/F-02, parameterised
   Npgsql query + `IResourceContributor` targeting the DB + `ICompileReferenceContributor`),
   `script.csharp` (F-03, author C# spliced verbatim via StringBuilder into an engine-owned wrapper that
   isolates the body inside an `async` local function — `return;` exits the local function only, not the
   Roslyn submission; a malformed brace-injection still fails to compile → Inconclusive), alongside `http.rest`.
3. **Capture + substitution (B-02/B-03):** JSONPath capture into `Vars` (no-match → Inconclusive);
   `{placeholder}` substitution resolved at runtime against `Vars` — identifier-safe for SQL query text,
   arbitrary for parameter/expect values; the `variables:` block is staged into `Vars`.
4. **Isolation (A-01/A-02):** `IScenarioIsolation` seam + `RespawnPostgresIsolation` (checkpoint
   re-created each scenario) + `RunSuiteAsync` (build-once topology, reset between scenarios); `RunAsync`
   unchanged.
5. **Provenance (G-01):** `StepCompletedEvent.Captured`/`.Substitutions` carry names/paths/origins only —
   never values (secret-safe by construction).

The 5,000-iteration provider-closure memory gate stays green (NetDelta +2.2 KB) with Npgsql and
JsonPath.Net as compile-only references. The capstone surfaced and closed three real integration gaps
(Respawn empty-DB-at-suite-start; db-assert expect-value substitution; `variables:` not staged).
Security review (SAFE-WITH-FIXES) cleared: H1 SQL-injection sink fixed (identifier-safe query-text
substitution with step-scoped blast-radius — `ResolveIdentifier` now called inside the helper's own
`try`, so an unsafe identifier yields `Verdict.EnvironmentError` for this step only and downstream
steps continue), M2 cross-scenario state bleed fixed (re-checkpoint each scenario), M3 `script.csharp`
`return;` containment fixed (author body now runs inside an `async` local function, so `return;` cannot
skip the engine outcome write or abort downstream steps; the previously documented "only structural
guarantee is brace-balance" claim was inaccurate for a brace-balanced `return;` — the local-function
wrapper is the accurate guarantee). M-A reserved-prefix enforcement added: `AstBuilder` now rejects
capture keys and `variables:` entries that begin with engine-reserved prefixes (`svc::`, `conn::`,
`__outcome::`, `__capture_status::`) at build time. Carry-forward to Sprint 5: §17 secrets/`SecretString`
redaction (L1/L2), `seed`, RETRY/Polly, observation redaction at the §14 event seam.

## Sprint goal

The compiler generates CSX by flowing every step through the provider contract's five stages, with
`db-assert.postgres` and `script.csharp` joining `http.rest`; a value captured from one step threads
into the next via JSONPath + brace substitution; and Respawn resets database state between scenarios.

## Entry assumptions

- Sprint 3 delivered: parser/AST/validation, production Roslyn pipeline, `http.rest`, build-once fixture.

## Tasks

### Workstream B — Compiler & runtime

#### S04-B-01 · Provider-mediated CSX generation pipeline
- **Owner:** CR1 · **Estimate:** 3d · **Depends on:** S03-B-04, S03-F-02 · **Spec:** BP §13; MVP §8.2 (provider-mediated CSX generation), §6.2
- Promote the Phase 1 contract into the production compiler: each step is resolved to its provider,
  bound to a typed model, validated, has its resources planned, and emits its `CsxFragment`; the
  compiler assembles fragments into one script (deduplicated `RequiredUsings`, collected
  `RequiredHelpers`, ordered `StatementBlock`s).
- **Acceptance:**
  - A multi-step, multi-provider suite assembles into one valid script with no helper/usings collisions.
  - Fragment assembly enforces the CsxFragment rules across providers (BP §13.3.1).

#### S04-B-02 · Variable capture — JSONPath extraction
- **Owner:** CR2 · **Estimate:** 2d · **Depends on:** S04-B-01 · **Spec:** DSL §6; MVP §8.2 (variable capture first form)
- Implement `capture` via `JsonPath.Net` into the shared `ScriptGlobalVariables` context.
- **Acceptance:**
  - A JSONPath `capture` on an `http.rest` response writes a typed value into the shared context.

#### S04-B-03 · Brace-syntax placeholder substitution
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S04-B-02 · **Spec:** DSL §6; MVP §8.2
- Substitute `{placeholder}` references from the shared context into subsequent step fields at the
  correct stage (captured values thread forward only through `Vars`).
- **Acceptance:**
  - A value captured in step 1 substitutes into step 2's fields and reaches the emitted CSX correctly.

### Workstream F — Provider SDK & Core providers

#### S04-F-01 · `db-assert.postgres` provider — model, binder, validator
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S04-B-01 · **Spec:** BP §13; DSL §5; MVP §8.2
- Implement `db-assert.postgres` (assembly `Platform.Steps.DbAssert.Postgres`) over Npgsql with a typed
  query/expectation record.
- **Acceptance:**
  - Binds and validates a `db-assert.postgres` step; rejects a malformed expectation clearly.

#### S04-F-02 · `db-assert.postgres` provider — CSX emitter & resource contributor
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S04-F-01 · **Spec:** BP §13.3.1; CLAUDE.md CsxFragment + Aspire
- Emit the assertion fragment (`DbAssertPostgres_Helpers`, `Vars`-only state, `var`+`finally` dispose,
  `$$"""…"""`) and contribute its Postgres resource needs so orchestration can `WaitFor` the database.
- **Acceptance:**
  - A row-level assertion executes against the seeded DB and reports Pass/Fail correctly.
  - Emitted fragment passes the CsxFragment lint; the resource contributor targets the DB, not the server.

#### S04-F-03 · `script.csharp` provider — the escape hatch
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S04-B-01 · **Spec:** BP §13; DSL §5; MVP §8.2 (the escape hatch)
- Implement `script.csharp`, splicing author-supplied C# into a `StatementBlock` that interacts with the
  environment **only** through `Vars`; author code is sanitised and brace-escaped per the raw-string rules.
- **Acceptance:**
  - An author `script.csharp` step reads a captured var, performs logic, and writes a result var.
  - The provider cannot reach the environment except via `Vars` (reviewed for static bridges).

### Workstream A — Orchestration foundation

#### S04-A-01 · Respawn-based state reset
- **Owner:** OR · **Estimate:** 2d · **Depends on:** S03-A-01 · **Spec:** BP §4; MVP §8.2 (Respawn reset), §6.1
- Reset mutable dependency state (Postgres) between scenarios via Respawn so scenarios are isolated
  within a build-once topology.
- **Acceptance:**
  - Two scenarios mutating the same table do not see each other's data; reset is deterministic.

#### S04-A-02 · Per-scenario isolation seam
- **Owner:** OR · **Estimate:** 1d · **Depends on:** S04-A-01 · **Spec:** BP §4; MVP §6.3 (isolation contract — full form Sprint 7)
- Establish the isolation seam (reset hook around each scenario) that the runner's parallelism will
  build on in Phase 3.
- **Acceptance:**
  - Each scenario runs against reset state; the seam is documented for the runner work in Sprint 7.

### Workstream G — Result reporting & diagnostics

#### S04-G-01 · Captured-variable provenance in the event stream
- **Owner:** PC · **Estimate:** 1d · **Depends on:** S04-B-02, S02-G-01 · **Spec:** BP §14; MVP §6.7 (captured-variable thread groundwork)
- Emit `step-attempt`/`step-completed` events carrying which variables a step captured and where
  substituted values originated — the data the captured-variable thread renders in Phase 3.
- **Acceptance:**
  - The event stream records capture provenance; secret-derived values are never emitted (S05 enforces).

## Exit criteria (sprint demo)

- A suite chaining `http.rest` → capture → `db-assert.postgres`, with a `script.csharp` step in the
  middle, compiles through the provider-mediated pipeline and runs against a reset-between-scenarios
  topology, rendering per-step verdicts.

## Risks mitigated this sprint (MVP §10)

- Provider contract shape validated across three real providers in separate assemblies (registry +
  contract proven before Phase 4).
- Shared-state flakiness pre-empted by Respawn reset and the isolation seam.
