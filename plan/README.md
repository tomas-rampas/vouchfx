# vouchfx MVP — Delivery Plan (Milestones, Sprints, Tasks)

The execution plan for the vouchfx MVP, owned by the solution architect. It decomposes the
[MVP Project Plan](../docs/03_MVP_Project_Plan.md) into a milestone → sprint → task hierarchy so
that delivery can be tracked at the level of the smallest sensible unit of work.

This index is the entry point. Every sprint has its own file (`sprint-01.md` … `sprint-12.md`);
read the relevant sprint file for the full task list, and read this file for the conventions,
milestones, and cross-sprint picture.

> **Calendar is a guide, milestones are the truth.** Per MVP §7.2, the 24-week / 12-sprint figure
> sizes the effort and sequences the risk; a sprint is *done* when its exit demo is green, whatever
> week that falls in. Do not treat dates as commitments.

---

## 1. How this plan is structured

```
Phase   (5)  — risk-ordered themes from MVP §7 (Foundations → … → Pilot & release)
 └─ Milestone (5, M1–M5) — the demonstrable exit gate of each phase (MVP §7.1)
     └─ Sprint (12)       — a 2-week increment (two exceptions, noted below)
         └─ Task          — the smallest trackable unit, owned by one role, ≤ ~3 ideal days
```

- **Phases** come straight from MVP §7.1 and §8. They are sequenced by *risk*, not visibility.
- **Milestones** are the phase-exit gates. A milestone is met only when its demo is reproducible.
- **Sprints** are 2-week increments. Two deliberately deviate to honour the doc's phase weeks:
  Sprint 11 is a **1-week hardening sprint** (week 21), and Sprint 12 is a **3-week pilot & release
  sprint** (weeks 22–24).
- **Tasks** are decomposed to the smallest unit a delivery lead can track. Each names one owner, an
  estimate, its dependencies, acceptance criteria, and a back-reference to the authoritative docs.

## 2. Conventions

### Task identifiers
`S<NN>-<W>-<nn>` — sprint number, workstream letter, task sequence. Example: `S03-B-02` is the
second compiler/runtime task in Sprint 3. IDs are stable; if a task moves sprint it keeps its ID
in a "carried from" note rather than being renumbered.

### Estimates
Ideal engineering days (`d`), where one day is uninterrupted focus. A 2-week sprint yields roughly
**8 effective ideal days per person** after ceremonies and review. Estimates above 3d are a smell —
split them. Spikes are time-boxed, not estimated to completion.

### Workstreams (MVP §6) and their owning role (MVP §5.1)

| W | Workstream | Owning role | Abbrev. |
|---|---|---|---|
| **A** | Orchestration foundation | Orchestration engineer | OR |
| **B** | Compiler & runtime | Compiler/runtime engineers ×2 (+ architect shared) | CR1, CR2 |
| **C** | Authoring tooling (extension, CLI, runner) | Extension/tooling engineer | TX |
| **D** | Integration & hardening | Technical lead / architect | TL |
| **E** | Pilot & feedback | Product / delivery lead | PD |
| **F** | Provider SDK & Core providers | Platform / community engineer | PC |
| **G** | Result reporting & diagnostics | Platform / community engineer | PC |

> PC owns both **F** and **G** by design (MVP §5.1). The plan watches this load: sprints never
> schedule F and G work at full width in the same increment, and the architect (TL) backstops
> reporting when provider work peaks.

### Roles
TL = technical lead/architect · CR1/CR2 = compiler-runtime engineers · OR = orchestration engineer ·
TX = extension/tooling engineer · PC = platform/community engineer · PD = product/delivery lead.

## 3. Team & capacity

Seven people (MVP §5.1). Per 2-week sprint: ~7 × 8 = **56 ideal days** nominal, planned to ~**45 days**
of committed task work to leave slack for review, support, and the unplanned. Sprint 11 (1 week) and
Sprint 12 (3 weeks, pilot-heavy and non-code-dominated) are budgeted accordingly in their files.

## 4. Milestones

| Milestone | Phase | Closes after | Week | Demonstrable gate (MVP §7.1, §8) |
|---|---|---|---|---|
| **M1 — Foundations proven** | 1 | Sprint 2 | 4 | Compile-once/collectible-unload memory model returns to baseline over the full Core provider dependency closure; stub topology starts health-gated; provider contract exercised end-to-end by a throwaway reference provider; event-stream schema drafted. |
| **M2 — Core compiler runs** | 2 | Sprint 5 | 10 | `http.rest`, `db-assert.postgres`, `script.csharp` compile and run end-to-end against a real local topology through the provider-mediated pipeline, with seeding, env-var secret resolution, captured-variable threading, reflective registry discovery, and a minimal terminal renderer. |
| **M3 — Full step set & SDK** | 3 | Sprint 8 | 16 | All six Core providers + RETRY working deterministically; v1 schema and provider contract frozen; Provider SDK + CONTRIBUTING.md published and validated by an outside contributor; runner selection/parallelism + Vault source; polling timeline and captured-variable thread render. |
| **M4 — Tooling & hardening** | 4 | Sprint 11 | 21 | VSCode extension and CLI feature-complete; reference scenario green from editor and CLI; memory-leak test a permanent CI gate; HTML report + JUnit XML feature-complete; SDK dry-run passed; CI templates and the documentation set published; release manifest (signing, SBOM, packaging) ready. |
| **M5 — Pilot & v1.0 release** | 5 | Sprint 12 | 24 | Pilot cohort onboarded; success criteria instrumented and measured; v1.0 released across all channels; demand-signal / go-no-go assessment written; community provider repository opened. |

## 5. Sprint calendar

| Sprint | Phase | Weeks | Length | Theme | Primary goal |
|---|---|---|---|---|---|
| [01](sprint-01.md) | 1 | 1–2 | 2w | Foundations — de-risk | Solution + CI scaffold; memory-model PoC; orchestration spike; provider contract draft begins. |
| [02](sprint-02.md) | 1 | 3–4 | 2w | Foundations — close risk | Provider-closure leak test in CI; reference provider exercises the contract; event-stream schema + first JSON Schema drafted. **→ M1** |
| [03](sprint-03.md) | 2 | 5–6 | 2w | Compiler real | Parser→AST→validation; production Roslyn pipeline; `http.rest`; build-once Aspire fixture; renderer v0. |
| [04](sprint-04.md) | 2 | 7–8 | 2w | Provider-mediated pipeline | Resolve/bind/validate/plan/emit pipeline; `db-assert.postgres` + `script.csharp`; JSONPath capture; Respawn reset. |
| [05](sprint-05.md) | 2 | 9–10 | 2w | Seed, secrets, integrate | `environment.seed`; `${secret:…}` env source + redaction; three-provider end-to-end. **→ M2** |
| [06](sprint-06.md) | 3 | 11–12 | 2w | Async providers + RETRY | `mq-publish.kafka`, `mq-expect.kafka` (Avro/registry); Polly v8 RETRY; pilot recruitment + naming begin. |
| [07](sprint-07.md) | 3 | 13–14 | 2w | Capture & runner breadth | `webhook-listen.http`; XPath + full substitution; runner selection + parallelism/isolation; `IStepDiffRenderer`; vendor entity. |
| [08](sprint-08.md) | 3 | 15–16 | 2w | Freeze & publish | Freeze v1 schema + contract; publish Provider SDK + fixture + worked example; Vault source; polling timeline + captured-variable thread; watch mode. **→ M3** |
| [09](sprint-09.md) | 4 | 17–18 | 2w | Editor & report surface | VSCode schema YAML + embedded C# IntelliSense; CLI exit codes; HTML report + reproducibility envelope; JUnit XML. |
| [10](sprint-10.md) | 4 | 19–20 | 2w | Trust & contribution | Test Explorer; full verdict taxonomy (incl. inconclusive); SDK dry-run; CI templates; documentation set; accessibility review. |
| [11](sprint-11.md) | 4 | 21 | 1w | Hardening | Reference scenario green end-to-end; security/redaction penetration; release manifest (signing, SBOM, packaging). **→ M4** |
| [12](sprint-12.md) | 5 | 22–24 | 3w | Pilot & release | Onboard cohort; porting examples; instrument criteria; release v1.0; launch; assess gates; open community repo. **→ M5** |

## 6. Workstream activity by sprint

`●` primary focus · `○` active · `·` quiet

| W | S01 | S02 | S03 | S04 | S05 | S06 | S07 | S08 | S09 | S10 | S11 | S12 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| A Orchestration | ● | ○ | ● | ○ | ● | · | · | · | · | · | ○ | · |
| B Compiler/runtime | ● | ● | ● | ● | ● | ● | ● | ○ | · | · | ○ | · |
| C Tooling/runner | · | ○ | ○ | · | · | · | ● | ● | ● | ○ | · | ○ |
| D Integration/hardening | ○ | ○ | · | · | ○ | · | · | · | · | ○ | ● | ● |
| E Pilot/feedback | · | · | · | · | · | ○ | ○ | ○ | · | ○ | · | ● |
| F Provider SDK | ○ | ● | ● | ● | ○ | ● | ● | ● | · | ● | · | ○ |
| G Reporting | ○ | ● | ● | ○ | ○ | · | ○ | ● | ● | ● | · | · |

## 7. Definition of Ready (a task may be started when…)

- Its acceptance criteria are unambiguous and testable.
- Its upstream dependencies (`Depends on`) are complete or stubbed behind a stable contract.
- It names a single owning role and fits the estimate guidance (≤ ~3d, or is an explicit spike).
- Any hard invariant it touches (§9 below) is referenced in its acceptance criteria.

## 8. Definition of Done (a task is complete when…)

- Code is merged to the integration branch with review by at least one other engineer; **every
  compiler/runtime change is additionally reviewed by the architect** (MVP §10 key-person mitigation).
- Automated tests cover the new behaviour and run green in the team CI.
- It honours every applicable hard invariant in §9 — verified, not assumed.
- User-facing strings, errors, and docs are British English and localisable where cheap (MVP §9.5).
- The sprint's tracking board reflects reality; a half-done task is not closed.

## 9. Hard-invariant gate (non-negotiable — see [CLAUDE.md](../CLAUDE.md))

Every task whose work touches one of these areas must prove compliance in its acceptance criteria.
This list is the architect's standing review checklist:

- **Memory model (§5):** never `CSharpScript.EvaluateAsync()`; always compile-once → `.Emit()` to a
  `MemoryStream` → collectible `AssemblyLoadContext` → invoke N times → `.Unload()`. No static handle
  bridges the script boundary. The Core-provider-closure leak test is a permanent CI gate from M1.
- **Provider model (§13):** `<family>.<provider>` step types; compile-time source-level plugins only
  (no runtime loader, no sandbox); strongly-typed records, never `Dictionary<string,object>`; the v1
  contract is frozen for the v1.x engine series (extend via `IStepProviderV1_1`, never mutate).
- **CsxFragment (§13.3.1):** three fields (`RequiredUsings` / `RequiredHelpers` / one `StatementBlock`);
  no `using var` in a script body; `SanitiseId` (`-`→`_`) before splicing; C# 11 `$$"""…"""` raw
  strings; cross-step state only through the `Vars` global.
- **Aspire (§4, §19):** `DisableDashboard = true`; `AddContainer`/`AddProject` (never `AddProject<T>()`);
  connection strings ≠ endpoints; `WaitFor` the *most specific* resource; suppress HealthChecks logs
  below `Warning`.
- **Verdict taxonomy (§12.1):** Pass / Fail / Environment error / Inconclusive kept separate
  everywhere; only `Fail` breaks CI by default.
- **Reporting (§14):** one schema-versioned JSON Lines event stream feeds every renderer; each
  `step-attempt` recorded individually; renderers tolerate unknown fields.
- **Assembly-graph hygiene (§5.6):** `Platform.Engine.*` / `Platform.Steps.*` reserved; customer DLLs
  share the script's collectible context; version conflicts fail fast at suite start.
- **Secrets (§17):** references only, resolved at *step-execution* time; `SecretString` with no
  value-returning `ToString()`/`IFormattable`; the reproducibility envelope hashes the reference.
- **Libraries (§5.7):** Polly v8 (`ResiliencePipeline`); `System.Text.Json` only; `JsonPath.Net` +
  `JsonSchema.Net` (draft 2020-12); `YamlDotNet`; Aspire pinned per engine release.

## 10. Cadence (ceremonies)

- **Sprint planning** (start): confirm Definition of Ready, commit the task slice.
- **Daily sync** (15 min): blockers, not status theatre.
- **Mid-sprint architecture review** (TL): compiler/runtime + provider-contract changes.
- **Sprint demo** (end): the milestone or sprint-goal demo, reproducible from a clean checkout.
- **Retrospective** (end): one process change carried into the next sprint.
- **Phase-exit steering review** (at M1–M5): stakeholder review per MVP §5.5.

## 11. Traceability

Every task cites its source under `Spec:` — `MVP §x` is the
[MVP Project Plan](../docs/03_MVP_Project_Plan.md), `BP §x` is the
[Architecture Blueprint](../docs/01_Technical_Architecture_and_Engineering_Blueprint.md), and
`DSL §x` is the [YAML DSL Specification](../docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md).
The risk register (MVP §10) is linked from the sprint that first mitigates each risk.
