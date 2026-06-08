# Sprint 03 — Core compiler: make the language real

| | |
|---|---|
| **Phase** | 2 — Core compiler (MVP §8.2) |
| **Weeks** | 5–6 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M2** (closes Sprint 5) |
| **Theme** | Promote the Phase 1 proofs into a production compiler skeleton: parse a real `.e2e.yaml` to an AST, validate it, run the production Roslyn pipeline, and execute the first real provider against a build-once topology. |

## Delivery status

**Merged to `main`** via PR #131 (2026-06-08) — all **9 tasks** delivered
(plus the integration spine). Build is 0-warning under `TreatWarningsAsErrors`, `dotnet format` is
clean, and the full non-docker suite is green (**265 tests** across seven projects); the Docker
capstone suite is green locally. The **sprint exit criterion is met**: a real `.e2e.yaml` with one
`http.rest` step parses → validates → compiles once → runs against a build-once Aspire topology →
renders a verdict (`step 'get-root': PASS (12 ms)` → `Verdict.Pass`), independently re-verified.

Key proofs:

1. **Front-end:** `Platform.Engine.Authoring` — typed document model + `YamlDocumentParser` (S03-B-01)
   and `AstBuilder` with alias resolution (single-provider family → default; bare `db-assert` rejected;
   ambiguous rejected) (S03-B-02).
2. **Compiler:** `CsxAssembler` (dedup usings/helpers, splice in step order) + production compile-once
   pipeline (S03-B-04); pre-compile schema validation with line context (S03-B-03).
3. **First real provider:** `http.rest` issues an HTTP GET and reports a verdict via `StepOutcome`
   (Pass/Fail/EnvironmentError) (S03-F-01/F-02).
4. **Orchestration:** `EnvironmentMapper` (environment → Aspire resources, gate the database) (S03-A-02)
   and the build-once `SuiteTopology` fixture (S03-A-01).
5. **Reporting:** terminal renderer v0 with per-step verdicts + durations (S03-G-01).
6. **Spine:** `Platform.Engine.Runtime.ScenarioRunner` wires the whole slice and aggregates the verdict
   (EnvironmentError > Fail > Inconclusive > Pass); topology/compile failures map to Environment
   error/Inconclusive, never Fail.

The 5,000-iteration provider-closure memory gate stays green (NetDelta +1.7 KB). Carry-forward to later
sprints: RETRY/Polly + per-step timeouts (rejected with a clear message until Sprint 6), richer
image-pull registry/auth fidelity, inline request bodies, and capture/placeholder threading.

## Sprint goal

A `.e2e.yaml` file parses into an AST, validates against the Phase 1 schema, and a single `http.rest`
step compiles through the production Roslyn pipeline (compile-once, collectible context, global host
object) and executes against a build-once Aspire topology, with results flowing into the terminal
renderer.

## Entry assumptions

- M1 cleared: memory model gated, contract exercised, schema + event stream drafted.

## Tasks

### Workstream B — Compiler & runtime

#### S03-B-01 · YAML deserialisation to typed document model
- **Owner:** CR1 · **Estimate:** 2d · **Depends on:** S02-C-01 · **Spec:** DSL §3; MVP §8.2 (YAML parser and AST)
- Deserialise the four top-level sections with `YamlDotNet` into strongly-typed records (never
  `Dictionary<string,object>`).
- **Acceptance:**
  - `metadata`/`environment`/`variables`/`steps` round-trip into typed records; only `steps` required.

#### S03-B-02 · AST construction & step normalisation
- **Owner:** CR1 · **Estimate:** 2d · **Depends on:** S03-B-01 · **Spec:** DSL §3, §4; MVP §8.2
- Build the AST: ordered steps each carrying `id`, `type` (`<family>.<provider>`), `capture`,
  `verifyMode`, `timeout`, `continueOnFailure`; resolve bare-family aliases to their default provider
  (and reject `db-assert` with no provider — it has no default).
- **Acceptance:**
  - Step `type` parses into family/provider; single-provider families accept the bare alias;
    `db-assert` without a provider is rejected with a clear error.

#### S03-B-03 · Schema validation pass over the AST
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S03-B-02, S02-C-02 · **Spec:** DSL §8; MVP §8.2
- Validate the parsed document against the unified JSON Schema (`JsonSchema.Net`, draft 2020-12) before
  compilation, reporting violations with file/line context.
- **Acceptance:**
  - Invalid documents fail validation pre-compile with actionable, line-located messages.

#### S03-B-04 · Production Roslyn pipeline with global host object
- **Owner:** CR2 · **Estimate:** 2.5d · **Depends on:** S01-B-02 · **Spec:** BP §5; MVP §8.2 (Roslyn pipeline in production form); CLAUDE.md memory model
- Promote the PoC: `CSharpScript.Create<ScriptGlobalVariables>()` → `.Emit()` once → collectible
  `AssemblyLoadContext` → delegate invoke → `.Unload()`, wired with dynamic assembly resolution. Still
  no `EvaluateAsync`.
- **Acceptance:**
  - A generated script compiles once and runs via the delegate against a `ScriptGlobalVariables` host.
  - The memory gate (S02-D-01) stays green against the production pipeline.

### Workstream F — Provider SDK & Core providers

#### S03-F-01 · `http.rest` provider — model & binder
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S02-F-01 · **Spec:** BP §13; DSL §5; MVP §8.2 (three Core providers)
- Implement the `http.rest` provider's typed request record and `IStepBinder`/`IStepValidator`, in its
  own assembly (`Platform.Steps.Http.Rest`) so reflective discovery is genuinely tested.
- **Acceptance:**
  - The provider binds a YAML `http.rest` step to its typed model and validates required fields.

#### S03-F-02 · `http.rest` provider — CSX emitter
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S03-F-01, S03-B-04 · **Spec:** BP §13.3.1; CLAUDE.md CsxFragment rules
- Emit the `http.rest` `CsxFragment`: `RequiredUsings` namespaces, `Http_Rest_Helpers` nested class,
  one `StatementBlock`. Use C# 11 `$$"""…"""` raw strings, `SanitiseId` on the step id, plain `var` +
  `.Dispose()` in `finally` (no `using var`), state only via `Vars`.
- **Acceptance:**
  - Emitted fragment passes the CsxFragment lint (three fields, no inline `using`, no `using var`,
    hyphen-free variable names, raw-string brace correctness).
  - An `http.rest` GET against a stub endpoint executes and reports a verdict.

### Workstream A — Orchestration foundation

#### S03-A-01 · Build-once-per-suite fixture
- **Owner:** OR · **Estimate:** 2d · **Depends on:** S02-A-01 · **Spec:** BP §4; MVP §8.2 (Aspire orchestration production form)
- Promote the spike into a fixture that builds the topology **once per suite**, exposes discovered
  endpoints/connection strings to `ScriptGlobalVariables`, and tears down cleanly.
- **Acceptance:**
  - One topology build serves many scenarios in a suite; endpoints surface on the host object.

#### S03-A-02 · `environment.services` / `dependencies` → Aspire resources
- **Owner:** OR · **Estimate:** 1.5d · **Depends on:** S03-A-01, S03-B-02 · **Spec:** DSL §3; BP §4
- Map the parsed `environment` block to `AddContainer`/`AddProject` resources (`image:` preferred,
  `project:` csproj supported), wiring `WaitFor` on the most-specific dependency.
- **Acceptance:**
  - A two-resource `environment` block provisions and health-gates from YAML alone.

### Workstream G — Result reporting & diagnostics

#### S03-G-01 · Terminal renderer v0 — step verdicts & durations
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S02-G-02 · **Spec:** BP §14; MVP §8.2 (minimal terminal renderer), §6.7
- Promote the stub to render per-step verdicts and basic durations from the live event stream; this is
  the compiler workstream's primary feedback surface for the rest of Phase 2.
- **Acceptance:**
  - Running a single-step suite prints a legible verdict line with duration, driven by the stream.

## Exit criteria (sprint demo)

- A real `.e2e.yaml` with one `http.rest` step parses, validates, compiles once, runs against a
  build-once topology, and renders a verdict in the terminal.

## Risks mitigated this sprint (MVP §10)

- Memory model carried into production form without regressing the gate.
- Provider contract exercised by the first *real* Core provider (not the throwaway).
