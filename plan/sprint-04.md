# Sprint 04 — Core compiler: the provider-mediated pipeline

| | |
|---|---|
| **Phase** | 2 — Core compiler (MVP §8.2) |
| **Weeks** | 7–8 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M2** (closes Sprint 5) |
| **Theme** | Generalise from one provider to a true provider-mediated compiler — resolve, bind, validate, plan, emit — with two more Core providers, cross-step variable capture, and state reset. |

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
