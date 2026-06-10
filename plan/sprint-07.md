# Sprint 07 — Full step set: capture breadth + a real runner

| | |
|---|---|
| **Phase** | 3 — Full step set (MVP §8.3) |
| **Weeks** | 13–14 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M3** (closes Sprint 8) |
| **Theme** | Complete the sixth Core provider and the variable mechanism, and turn a single-suite executor into a runner that scales to a real test estate — selection, bounded parallelism, and a safe isolation contract. Incorporate the vendor entity. |

## Sprint goal

`webhook-listen.http` works with its ephemeral local listener; XPath capture and full substitution work
across all step kinds; and the runner can select tests (tag/owner/path/change-set) and run scenarios in
parallel under a concurrency budget with a per-scenario isolation contract. The vendor entity is
incorporated.

## Entry assumptions

- Sprint 6 delivered: Kafka providers, RETRY, recruitment, naming underway.

## Tasks

### Workstream F — Provider SDK & Core providers

#### S07-F-01 · `webhook-listen.http` provider — ephemeral listener
- **Owner:** PC · **Estimate:** 2.5d · **Depends on:** S04-B-01 · **Spec:** BP §13; DSL §5; MVP §8.3 (webhook-listen with ephemeral listener)
- Implement `webhook-listen.http` (`Platform.Steps.WebhookListen.Http`): stand up an ephemeral local
  listener, expose its URL to `Vars`, and assert an inbound webhook arrives (a RETRY consumer).
- **Acceptance:**
  - The listener captures an inbound request and asserts on it; its URL threads via `Vars` to earlier
    steps; the listener is torn down per scenario.
  - Fragment passes the CsxFragment lint; no `using var`; resources disposed in `finally`.

### Workstream B — Compiler & runtime

#### S07-B-01 · XPath capture
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S04-B-02 · **Spec:** DSL §6; MVP §8.3 (XPath capture)
- Add XPath extraction alongside JSONPath for XML result formats.
- **Acceptance:**
  - An XPath `capture` extracts a value from an XML response into the shared context.

#### S07-B-02 · Full substitution across all step kinds & result formats
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S07-B-01, S04-B-03 · **Spec:** DSL §6; MVP §8.3
- Complete `{placeholder}` substitution uniformly across every provider and capture source.
- **Acceptance:**
  - Substitution works identically across http/db/mq/webhook/script steps; secret-derived values stay
    redacted in any trace.

### Workstream C — Authoring tooling (runner)

#### S07-C-01 · Headless CLI runner skeleton
- **Owner:** TX · **Estimate:** 1.5d · **Depends on:** S05-D-01 · **Spec:** MVP §6.3, §8.4 (CLI); BP §16
- Stand up the CLI that discovers `.e2e.yaml` files, invokes the engine, and streams events to the
  renderer. (Deterministic exit codes are finalised in Sprint 9.)
- **Acceptance:**
  - `run` executes a suite from the command line and prints the terminal report.

#### S07-C-02 · Selection language — tag / owner / path / change-set
- **Owner:** TX · **Estimate:** 2.5d · **Depends on:** S07-C-01 · **Spec:** BP §16; MVP §8.3, §6.3, §10 (runner-scale risk)
- Implement a composable selection language over `metadata` tags, ownership, file path, and change-set
  (driven by `metadata`, which has no execution effect but drives selection).
- **Acceptance:**
  - A multi-file suite can be filtered by any combination of tag/owner/path/change-set.

#### S07-C-03 · Scenario-level parallelism with concurrency budget + isolation contract
- **Owner:** TX · **Estimate:** 2.5d · **Depends on:** S07-C-02, S04-A-02 · **Spec:** BP §16; MVP §8.3, §6.3, §10
- Run scenarios in parallel bounded by a concurrency budget, with the conservative default isolation
  contract (private/reset topology per parallel scenario) to prevent shared-state flakiness.
- **Acceptance:**
  - Parallel scenarios do not interfere; the concurrency budget is respected; isolation default is the
    safe one.

### Workstream G — Result reporting & diagnostics

#### S07-G-01 · `IStepDiffRenderer` contract
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S02-G-01 · **Spec:** BP §14; MVP §8.3, §6.7
- Define the `IStepDiffRenderer` contract so providers supply faithful expected-vs-observed rendering for
  their data model (relational for postgres, document for mongo, etc.). Implement it for
  `db-assert.postgres`.
- **Acceptance:**
  - A failed `db-assert.postgres` step renders a relational expected-vs-observed diff via the contract.

### Workstream E — Pilot & feedback

#### S07-E-01 · Incorporate the vendor entity & confirm jurisdiction
- **Owner:** PD · **Estimate:** 1.5d · **Depends on:** S06-E-02 · **Spec:** MVP §1.2, §8.3, §9.6, §10 (vendor-entity risk)
- With legal advice, incorporate the corporate entity (working assumption CZ/EU) as the vendor of record
  — needed even for the free Indie tier in enterprise procurement.
- **Acceptance:**
  - Entity incorporated (or filing in progress with a firm date); jurisdiction/structure confirmed;
    trademark application prepared for early Sprint 9.

## Exit criteria (sprint demo)

- A multi-file suite is selected by tag and run in parallel under a concurrency budget; a
  `webhook-listen.http` step captures an inbound webhook; an XPath capture threads a value forward; a
  failed `db-assert.postgres` shows a relational diff.

## Delivery status

**Delivered:**
- **S07-B-01** (XPath capture) — `CaptureFormat.XPath` + parser support; a `capture:` entry may be a bare scalar (JSONPath, back-compat) or an explicit single-key mapping (`{ xpath: … }`). XPath evaluation is provider-specific (`http.rest` against XML bodies).
- **S07-B-02** (Full substitution) — `{placeholder}` and `${secret:…}` tokens resolved uniformly across http/db/mq/webhook steps at execution time. Secret-derived values stay redacted in every context.
- **S07-C-01** (CLI runner skeleton) — `vouchfx run [<path>]` discovers, parses, and executes `.e2e.yaml` scenarios end-to-end. Exit codes: 0 (pass/inconclusive), 1 (fail), 2 (usage).
- **S07-C-02** (Selection language) — `--tag`, `--owner`, `--path <glob>`, `--changed-since <ref>` filters applied before execution. AND across dimensions, OR within.
- **S07-F-01** (`webhook-listen.http` provider) — Ephemeral host-owned HTTP listener declared via `listener` name; URL staged at `Vars[<listener>]` and `svc::<listener>`; RETRY consumer; match criteria (method/path/headers/bodyContains) with full substitution support. **Sharp edge documented:** captured path includes query string; `path: /cb` does not match `/cb?x=1`.
- **S07-G-01** (`IStepDiffRenderer` contract) — Provider-facing contract for expected-vs-observed diffs; `db-assert.postgres` emits relational diffs on failure.
- **S07-E-01** (Vendor entity) — incorporation **plan** delivered as a planning artefact (`plan/vendor-entity.md`): CZ s.r.o. jurisdiction recommendation, structure, trademark-application prep for early Sprint 9, and the incorporation/legal-engagement checklist. The actual incorporation + legal engagement are the PD's to execute (acceptance: filing in progress with a firm date — to be set after the legal consultation).

**Deferred to Sprint 8:**
- **S07-C-03** (Scenario-level parallelism) — Only the no-render-core foundation landed; bounded concurrency and isolation contract deferred.

**Proof tasks:**
- Compile companion green (non-Docker).
- **Full docker integration suite run locally — 28/28 `requires=docker` tests green** (~11 min), including `Sprint07Capstone_WebhookCallbackCapturedByRetryListener_Pass`, `Sprint07Capstone_FailingDbAssert_RendersRelationalDiff_Fail`, the Sprint 6 Avro capstones, M2, and the S04 Respawn capstone. The Sprint 7 webhook capstone simulates the callback with a `script.csharp` step POSTing to the host listener; a real containerised-SUT callback over `host.docker.internal` is the one path not yet automated as a test.
- **Finding (follow-up):** the Aspire topology teardown leaks containers — a clean exit-0 docker run left `web-*`/`pg-*` containers + an orphaned `aspire-session-network-*-testhost` network behind (and a prior run's `events-sr-*`/`events-*` lingered for hours). DCP/topology dispose does not reliably reap containers after the test host process exits; worth fixing in the topology lifecycle or documenting as a known DCP limitation.

## Risks mitigated this sprint (MVP §10)

- Runner does not scale to a real estate (selection lands; parallelism deferred to S08).
- Enterprise procurement blocked by no named vendor (incorporation plan + jurisdiction recommendation prepared; actual incorporation is the PD's to execute).
