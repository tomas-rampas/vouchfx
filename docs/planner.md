# Planner: Coverage and gap analysis

The **Planner** is a deterministic, read-only analysis tool that intersects three engine-side sources — the **declared** universe (the `.e2e.yaml` suite folder), the **exercised** reality (your run history), and the **available** vocabulary (the registered step catalogue) — and emits a coverage-and-gap report. It answers the question: **what should I test next?**

Every gap finding carries the structured hints the [`scaffold`](getting-started.md#generator-suite-scaffold) tool needs, so you can pick a gap, generate a skeleton step, and validate it — all without re-deriving anything. The Planner never writes a suite file, never calls a model, and never claims coverage it cannot evidence from its declared inputs.

## What the Planner reads

The Planner reads three things:

1. **The declared suite set** — all `.e2e.yaml` files (recursively) at or below a path you specify, or a single file. It parses each one (failing gracefully if the YAML is malformed) and extracts the suite structure: services, dependencies, step ids, step types, and which services/dependencies each step targets.

2. **The event history** — optional JSON Lines event archive(s) from previous `vouchfx run` invocations (the `--events` artefact). If you omit this, every declared suite and step is reported never-run; that is a valid, successful analysis.

3. **The live step catalogue** — the set of registered providers available in this build of vouchfx. The Planner asks: which providers *could* assert the dependencies I've declared, and which ones actually *did*?

## What the Planner deliberately does not read

- **Production traffic** — the Planner knows nothing of your live systems or production behaviour.
- **OpenAPI / AsyncAPI contracts** — the Planner does not inspect API documentation.
- **Docker Compose or cloud-infrastructure definitions** — no IaC parsing.
- **Git history** — no blame, no commits-per-file, no commit metadata of any kind.
- **Version-control metadata** — the declared suites are the source of truth, as they exist on disk right now.

The Planner is a **declared-vs-exercised-vs-available** gap detector, not a coverage inference engine.

## Finding kinds

The Planner classifies findings into ten kinds. Five are **coverage gaps** (what `--fail-on-gap` counts):

### Coverage gaps

**`suite-never-run`** — A discovered suite that appears in no run of your history. **No hints.** The remedy is to run an existing suite, not to author a step.

**`step-never-exercised`** — A declared step id that has no step event attributed to it in any run. This might mean the suite never ran, or the suite ran but that step was skipped (e.g. via `continueOnFailure`). **Hint:** suggested step type and id. **Target:** the step's service or dependency.

**`dependency-not-asserted`** — A dependency that is targeted by at least one declared step (so it is exercised), but by no step of an **asserting** family. For example, you publish to Kafka with `mq-publish.kafka` but never consume with `mq-expect.kafka`. The seam is exercised but never verified. **Hint:** suggested asserting step type. **Target:** the dependency name.

**`dependency-missing-step-type`** — A declared dependency has registered candidate step type(s) that would assert it, and **none of them** is used against it. For example, you declare a Redis dependency but never call `cache-assert.redis`. This is a vocabulary gap: the provider exists, but you are not using it. **Hint:** the missing asserting step type(s). **Target:** the dependency name.

**`service-missing-http-step`** — A declared service has no `http.*` step (HTTP REST or SOAP) targeting it. If a service runs in your topology, you should verify at least one call to it. **Hint:** `http.rest`. **Target:** the service name.

### History-health findings

These are not gaps; they are health signals from your history. They do **not** count towards `--fail-on-gap`.

**`step-stale`** — A step's last observed `step-completed` event is more than **30 days** (configurable, `--stale-days`) before the newest event in your history. The step exists and once ran, but you have not exercised it recently. **No hints.** Remedy: run the suite.

**`step-flaky`** — A step shows at least one `Pass` and at least one `Fail` `step-completed` verdict across at least **2 distinct runs** (configurable, `--flaky-min-runs`). The step is non-deterministic. **No hints.** Remedy: investigate root cause, fix or quarantine.

**`step-fragile`** — A step shows at least **2** `EnvironmentError` outcomes (configurable, `--fragile-min-env-errors`) — things like unhealthy containers, image-pull failures, or seed failures. The infrastructure around this step is unreliable. **No hints.** Remedy: check container health, seed robustness, or network stability.

**`step-inconclusive-prone`** — A step shows at least **2** `Inconclusive` outcomes (configurable, `--inconclusive-min`) — timeouts, unmet upstream captures, or partition hangs. The step is timing out or the conditions it depends on are flaky. **No hints.** Remedy: increase timeout, debug upstream dependencies, or fix race conditions.

### Identity findings

**`suite-identity-ambiguous`** — A historical run's suite identity could not be resolved to exactly one currently-declared suite. This can happen if (a) two suites on disk now declare the same `metadata.name`, or (b) your history references a file that was renamed or deleted since the run. The Planner reports it honestly rather than silently claiming coverage or non-coverage. **No hints.** Remedy: rename suites to have unique identities, or re-run with the current file structure.

## Thresholds

The Planner applies documented defaults to classify history-health findings. You can override all of them:

| Finding kind | Default threshold | Override flag | Meaning |
|---|---|---|---|
| `step-stale` | 30 days | `--stale-days <n>` | A step last observed more than `n` days ago (relative to the newest event in your history, not wall-clock now). |
| `step-flaky` | 2 runs | `--flaky-min-runs <n>` | A step shows Pass and Fail verdicts across at least `n` distinct run IDs. |
| `step-fragile` | 2 errors | `--fragile-min-env-errors <n>` | A step shows at least `n` EnvironmentError outcomes (across any number of runs). |
| `step-inconclusive-prone` | 2 outcomes | `--inconclusive-min <n>` | A step shows at least `n` Inconclusive outcomes (across any number of runs). |

**Important:** "now" is the **newest event timestamp in your analysed history**, never wall-clock time. This means a given input always produces the same report, no matter when you run the command. A stale step is not "stale today" but "stale *relative to the most-recent data you have*".

Only `step-completed` events count toward these tallies — never `step-attempt` events. This matters for `RETRY` steps: a step that polls three times before passing is one run with three attempts, not three runs.

## The runId correlation rule

Steps are attributed to scenarios by **exact `runId` equality** — never by position in the history file, which would misattribute under parallel runs. Each scenario execution receives a distinct `runId` (blueprint §16.2), so `runId` is the join key.

A suite counts as **exercised** when a `scenario-started` event's `scenarioId` matches a declared suite. The `scenarioId` is derived as `metadata.name ?? filename stem`.

**Ambiguity caveat:** The engine does not yet populate the `file` field on scenario-started events (planned for a follow-up). History matching is by `scenarioId` only. If two of your suites share the same `metadata.name`, the Planner reports them as ambiguous and marks findings involving them. Name them uniquely or rename one of the files.

## Exit codes

| Exit code | Meaning | When |
|---|---|---|
| **0** | Successful analysis — regardless of how many gaps or health issues were found | Always, unless `--fail-on-gap` is set and gaps exist |
| **2** | UsageError — bad/missing suite path, empty suite folder, a threshold out of range (`--stale-days` ≥ 0, `--flaky-min-runs` ≥ 1, `--fragile-min-env-errors` ≥ 1, `--inconclusive-min` ≥ 1), or a missing `--output` parent directory; also when an event history file exceeds 64 MiB | Usage mistakes |
| **3** | Incomplete catalogue metadata — a registered provider lacks a schema fragment (rare; the same class of failure `vouchfx list` and `vouchfx schema` map to) | Engine build problem |
| **5** | Gaps found — at least one `suite-never-run`, `step-never-exercised`, `dependency-not-asserted`, `dependency-missing-step-type`, or `service-missing-http-step` finding is present | Only when `--fail-on-gap` is set AND gaps exist |

**Key principle:** Gaps are data, not defects. Without `--fail-on-gap`, a report full of gaps still exits 0, mirroring the verdict taxonomy's rule that only a genuine `Fail` breaks CI by default.

**A caveat on exit 2:** one exit-2 case is not actually a bad argument. If your suite path resolves to a real, existing directory but a subdirectory beneath it becomes locked, is deleted mid-scan, or is access-denied while the Planner is walking it, discovery still fails closed with exit 2 — there is no partial-enumeration fallback. This is an environment/infrastructure fault, not a typo'd path, and the error message says so explicitly (it never claims the path itself is invalid) so it is not mistaken for a usage mistake when triaging a failure.

## Library API

In-process hosts (CLI tools, MCP servers, custom orchestrators) call the public library entry points directly and never shell out:

```csharp
using Vouchfx.Engine.Planning;

// The main analysis: suite set + registry + optional history + thresholds → report
var request = new PlanRequest(
    SuitePath: "./tests/e2e",
    EventsPath: "./run-history.jsonl",  // or null for no history
    Thresholds: PlanThresholds.Defaults  // or custom thresholds
);

var report = PlanExport.BuildPlan(request, registry, engineVersion: "1.0.0");

// Serialise to JSON (used by --json and --output)
string json = PlanExport.SerializePlan(report);
```

Both entry points are in the `Vouchfx.Engine.Planning` library (the same one the CLI uses), available in-tree in this repository. The report is a `PlanReportDocument` with frozen v1 wire shape.

## The workflow

The pattern is: **plan → pick a gap → scaffold → validate → run**.

```bash
# Analyse your declared suites against history.
vouchfx plan ./tests/e2e --events ./run-history.jsonl

# If there are gaps, pick one and use its hints to scaffold a skeleton.
# Example gap finding:
#   "kind": "dependency-missing-step-type",
#   "target": "orders-db",
#   "suggestedTypes": ["db-assert.postgres"],
#   "suggestedStepId": "assert-orders-db"
#
# Maps directly to scaffold intent. The finding's target names a DEPENDENCY, so the
# intent must declare one for the scaffolded step to bind against — a step alone
# would emit an unbindable placeholder target and fail validation.
echo '{
  "steps": [
    { "id": "assert-orders-db", "type": "db-assert.postgres" }
  ],
  "dependencies": [
    { "name": "orders-db", "type": "postgres" }
  ]
}' | vouchfx scaffold --intent - --output ./draft.e2e.yaml

# Validate the scaffold before authoring.
vouchfx validate ./draft.e2e.yaml

# Fill in the details (query, assertions, etc.) manually or via your AI host.
# Then run it to make sure it passes.
vouchfx run ./draft.e2e.yaml
```

**Important caveat:** The gap hints (`suggestedTypes`, `suggestedStepId`, `target`) are structured data that the Planner produces and `scaffold` consumes directly as-is. Free-text conversation ("create a step that tests login") lives only in your AI host, never as a tool parameter. The Planner and scaffold are deterministic, read-only, and do not call a model.

## Three honest caveats

### 1. Placeholders and secrets in targets are unresolvable

A step's `target` field names a service or dependency — but if that field contains a `{placeholder}` or `${secret:…}` reference, it cannot be resolved structurally at analysis time. The Planner treats it as targeting nothing.

```yaml
steps:
  - id: call-api
    type: http.rest
    target: "{apiName}"  # Unresolvable — treated as targeting nothing.
    method: GET
    path: /health
```

This is correct behaviour: the Planner knows only what is declared, not what placeholders will become. If you want the gap analysis to see this step, hard-code the target or use capture from a prior step to populate it at runtime.

### 2. Listeners and receivers are not dependencies

`webhook-listen.http` and `trace-expect.otlp` have `listener` and `receiver` fields that name host-owned resources, not declared dependencies. The Planner does not treat them as targets, so it will not falsely report the webhook or trace dependency as unasserted.

### 3. Run count counts scenario executions, not invocations

The `runCount` in the report counts distinct `runId`s observed. Each scenario execution receives its own `runId` (blueprint §16.2). A single `vouchfx run ./tests` over three `.e2e.yaml` files yields a run count of **3**, not **1**. This is correct: you ran three scenarios, so three scenario instances are present in the history.

## Example invocation and output

```bash
vouchfx plan ./tests/e2e --events ./run-history
```

Human-readable summary:

```
Suites analysed:         2 (0 unanalysable)
Services declared:       1
Dependencies declared:   2 (0 unmappable)
Distinct step types:     4
Runs analysed:           5
Event history span:      2026-07-15T10:30:00Z .. 2026-07-27T14:22:15Z
Skipped event lines:     0
Unmatched observations:  0
Thresholds:              stale>30d, flaky>=2 runs, fragile>=2 env-errors, inconclusive>=2

Findings: 3 total (2 gap(s)).
  dependency-missing-step-type     1
  service-missing-http-step        1
  step-flaky                       1
```

Requesting JSON output:

```bash
vouchfx plan ./tests/e2e --events ./run-history --json
```

```json
{
  "schemaVersion": 1,
  "engineVersion": "1.0.0-rc.1",
  "thresholds": {
    "staleDays": 30,
    "flakyMinRuns": 2,
    "fragileMinEnvErrors": 2,
    "inconclusiveMin": 2
  },
  "inventory": {
    "suites": [
      {
        "path": "orders.e2e.yaml",
        "scenarioId": "checkout-flow",
        "name": "checkout-flow",
        "stepCount": 3
      },
      {
        "path": "refund.e2e.yaml",
        "scenarioId": "refund",
        "name": null,
        "stepCount": 1
      }
    ],
    "services": ["orders-api"],
    "dependencies": [
      { "name": "events", "type": "kafka", "suite": "orders.e2e.yaml" },
      { "name": "orders-db", "type": "postgres", "suite": "orders.e2e.yaml" }
    ],
    "stepTypes": ["db-assert.postgres", "http.rest", "mq-expect.kafka", "mq-publish.kafka"],
    "runCount": 5,
    "firstEventTs": "2026-07-15T10:30:00+00:00",
    "lastEventTs": "2026-07-27T14:22:15+00:00",
    "skippedEventLines": 0,
    "unmatchedObservations": 0,
    "unanalysableSuites": [],
    "unmappableDependencies": []
  },
  "findings": [
    {
      "kind": "dependency-missing-step-type",
      "suite": "orders.e2e.yaml",
      "stepId": null,
      "target": "orders-db",
      "targetKind": "dependency",
      "suggestedTypes": ["db-assert.postgres"],
      "suggestedStepId": "assert-orders-db",
      "ambiguous": false,
      "ambiguityReason": null,
      "history": null,
      "detail": "Dependency 'orders-db' (postgres) has no analysed step of a candidate asserting type.",
      "relatedSuites": []
    },
    {
      "kind": "service-missing-http-step",
      "suite": "orders.e2e.yaml",
      "stepId": null,
      "target": "orders-api",
      "targetKind": "service",
      "suggestedTypes": ["http.rest"],
      "suggestedStepId": "assert-orders-api",
      "ambiguous": false,
      "ambiguityReason": null,
      "history": null,
      "detail": "Service 'orders-api' has no http.* step targeting it.",
      "relatedSuites": []
    },
    {
      "kind": "step-flaky",
      "suite": "orders.e2e.yaml",
      "stepId": "create-order",
      "target": null,
      "targetKind": null,
      "suggestedTypes": [],
      "suggestedStepId": null,
      "ambiguous": false,
      "ambiguityReason": null,
      "history": {
        "passCount": 3,
        "failCount": 2,
        "envErrorCount": 0,
        "inconclusiveCount": 0,
        "distinctRuns": 2,
        "lastObserved": "2026-07-27T14:22:15+00:00",
        "ageDays": 1
      },
      "detail": "Step 'create-order' passed 3 and failed 2 times across 2 distinct runs.",
      "relatedSuites": []
    }
  ]
}
```

With `--fail-on-gap`, the presence of the first two findings (gaps) causes exit code 5:

```bash
vouchfx plan ./tests/e2e --events ./run-history --fail-on-gap
# Output: summary + message
# --fail-on-gap: 2 gap finding(s) present — exiting 5.
# Exit code: 5
```

---

**Learn more:** Read the [Technical Architecture Blueprint § 16](01_Technical_Architecture_and_Engineering_Blueprint.md#164-exit-codes) (particularly § 16.4 for the verdict taxonomy) for the runner design, and the [Language Reference](language-reference.md) for the declared DSL that the Planner analyses.
