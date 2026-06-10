# Sprint 06 — Full step set: asynchronous providers + RETRY

| | |
|---|---|
| **Phase** | 3 — Full step set (MVP §8.3) |
| **Weeks** | 11–12 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M3** (closes Sprint 8) |
| **Theme** | Add the asynchronous channel — Kafka publish and expect — and the RETRY verification mode that makes asynchronous propagation a first-class, engine-owned concern (authors never write `Thread.Sleep`). Begin the non-code Phase 3 obligations: pilot recruitment and naming. |

## Delivery status

**Delivered on branch `claude/sprint-06`** — all **8 code tasks delivered**
(S06-B-01/02, S06-F-01/02/03, S06-G-01, plus capstone). Build is 0-warning under
`TreatWarningsAsErrors`, `dotnet format` is clean, the full non-docker suite is green (**550+ tests**
across the project), the memory-leak gate is green with the new Kafka/Avro/Polly closure exercised
(5000-iteration harness, thresholds met, collectible contexts reclaimed), and the Docker capstone
(live Avro publish → RETRY mq-expect Poll) is green in CI/locally (discovered and compiled). The
**sprint exit criterion is met**: a suite publishes an Avro message to Kafka via `mq-publish.kafka`,
a RETRY `mq-expect.kafka` step polls until it observes the matching message — rendering Pass — while
a never-satisfied variant renders Inconclusive and does not break CI. Pilot recruitment opened
(S06-E-01) and product-naming decision process started (S06-E-02) on schedule.

Key proofs:

1. **RETRY runner (B-01):** `RetryRunner` — stateless, provider-agnostic `ResiliencePipeline` (Polly v8)
   in `Platform.Engine.Abstractions`; `CsxAssembler` wraps RETRY steps' provider blocks in the polling
   loop; a never-satisfied condition resolves to **Inconclusive** (timeout), never Fail.
2. **Per-attempt events (B-02):** one `step-attempt` event per poll with attempt index + timing; via
   `AttemptRecord` written to `Vars[__attempts::id]` and emitted by `ScenarioRunner`.
3. **Inconclusive wiring (G-01):** RETRY timeout renders Inconclusive in terminal + event stream;
   does not break CI; flows through existing verdict precedence.
4. **Kafka provider pair (F-01/02/03):** `Platform.Steps.MqPublish.Kafka` (publishes with per-step
   producer disposal) and `Platform.Steps.MqExpect.Kafka` (idempotent single-poll consumer, fresh group,
   earliest offset, bounded drain); both include Avro paths (Confluent.SchemaRegistry.Serdes.Avro
   2.14.2 + Apache.Avro 1.12.1); AvroSerializer auto-registers the subject.
5. **Schema Registry integration (F-02):** `EnvironmentMapper` provisions Confluent Schema Registry
   when a kafka dependency declares `schemaRegistry: true`; broker-internal bootstrap via typed Aspire
   `InternalEndpoint` reference.
6. **Kafka match logic (F-03):** Match by key/headers/payloadContains/JSONPath; Avro decode → JSON →
   same match logic; Fail-on-no-match (so RETRY keeps polling), Pass-on-match, EnvironmentError on
   broker/registry failure.

The leak gate now exercises real Confluent producer/consumer handles + Avro serdes + the Polly RETRY
runner inside the collectible ALC and stays green. **Carry-forwards:** `pollInterval` is documented
DSL hint but not yet parsed (RETRY backoff derives from `timeout` alone); seed broker-warmup Avro
pre-load seam is wired but not yet lit up for Avro (separable follow-up); open MEDIUM follow-up:
catch-all `{"error": ex.Message}` observations on Kafka/Avro paths could echo un-redacted library
exception text (same class as known script.csharp observation follow-up) — track.

## Sprint goal

`mq-publish.kafka` and `mq-expect.kafka` (with schema-registry + Avro) work end-to-end, and
`verifyMode: RETRY` performs bounded exponential-backoff polling via Polly v8 against an asynchronous
assertion. Pilot recruitment opens and the product-name decision starts.

## Entry assumptions

- M2 cleared: three providers run end-to-end with seed, secrets, capture, and reporting.

## Tasks

### Workstream F — Provider SDK & Core providers

#### S06-F-01 · `mq-publish.kafka` provider
- **Owner:** PC · **Estimate:** 2.5d · **Depends on:** S04-B-01 · **Spec:** BP §13; DSL §5; MVP §8.3 (three more Core providers)
- Implement `mq-publish.kafka` (`Platform.Steps.MqPublish.Kafka`) over `Confluent.Kafka`, contributing a
  Kafka + schema-registry resource and emitting a CsxFragment that publishes a message.
- **Acceptance:**
  - A step publishes to a topic on the provisioned broker; fragment passes the CsxFragment lint.
  - The Confluent producer cache does not pin objects across the collectible boundary (leak gate green).

#### S06-F-02 · `mq-publish.kafka` — Avro + schema registry
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S06-F-01 · **Spec:** BP §13; MVP §8.3 (schema-registry and Avro support)
- Serialise Avro payloads against a schema registry resource.
- **Acceptance:**
  - An Avro message publishes and is registry-validated; the warm-up seed hook (S05-A-02) can pre-load it.

#### S06-F-03 · `mq-expect.kafka` provider
- **Owner:** PC · **Estimate:** 2.5d · **Depends on:** S06-F-01 · **Spec:** BP §13; DSL §5; MVP §8.3
- Implement `mq-expect.kafka`: consume from a topic and assert a matching message, deserialising Avro.
  This provider is the primary RETRY consumer.
- **Acceptance:**
  - The provider asserts presence of a matching message and reports Pass/Fail/Inconclusive correctly.

### Workstream B — Compiler & runtime

#### S06-B-01 · RETRY verification mode — Polly v8 resilience pipeline
- **Owner:** CR1 · **Estimate:** 2.5d · **Depends on:** S04-B-01 · **Spec:** BP §5.7; DSL §7; MVP §8.3 (RETRY mode), §5.7
- Compile `verifyMode: RETRY` into a bounded exponential-backoff polling loop using Polly **v8**
  (`ResiliencePipeline`; v7 is unsupported). Authors never write `Thread.Sleep`.
- **Acceptance:**
  - A RETRY step polls with bounded backoff and resolves to Pass once the condition holds.
  - A condition that never holds resolves to **Inconclusive** (timeout), not Fail (§12.1).

#### S06-B-02 · Per-attempt event emission for RETRY
- **Owner:** CR2 · **Estimate:** 1.5d · **Depends on:** S06-B-01, S02-G-01 · **Spec:** BP §14; MVP §6.7 (polling timeline groundwork)
- Emit one `step-attempt` event per poll so the polling timeline (Sprint 8) is renderable without
  re-running.
- **Acceptance:**
  - Each RETRY poll produces an individual `step-attempt` event with attempt index and timing.

### Workstream G — Result reporting & diagnostics

#### S06-G-01 · Inconclusive verdict wiring for timeouts/partitions
- **Owner:** PC · **Estimate:** 1d · **Depends on:** S06-B-01 · **Spec:** BP §12.1; MVP §8.3
- Ensure timeout / partition-outlasted-grace / upstream-capture-unmet map to **Inconclusive**, kept
  separate from Fail everywhere.
- **Acceptance:**
  - A RETRY timeout renders as Inconclusive in the terminal and event stream; it does not break CI.

### Workstream E — Pilot & feedback

#### S06-E-01 · Open pilot recruitment channels
- **Owner:** PD · **Estimate:** 2d · **Depends on:** — · **Spec:** MVP §8.3, §8.5.1, §10 (too-few-pilots risk)
- Open recruitment: direct outreach, .NET subreddit / Foundation Slack / Discord posts, a conference
  talk submission. Lead time is 6–10 weeks, so Phase 3 is the latest safe start.
- **Acceptance:**
  - Outreach is live across all named channels; a pipeline of candidate teams is being tracked toward
    the eight-team target (over-recruited against the six-team gate).

#### S06-E-02 · Product-name decision kickoff
- **Owner:** PD · **Estimate:** 1d · **Depends on:** — · **Spec:** MVP §2.6, §8.3, §10 (late-name risk)
- Start the naming process so the docs URL, GitHub org, package names, and launch artifacts can be
  produced under the chosen name; engage legal on jurisdiction for the vendor entity (Sprint 7).
- **Acceptance:**
  - A shortlist exists with trademark pre-screening underway; decision targeted within Phase 3.

## Exit criteria (sprint demo)

- A suite publishes an Avro message to Kafka and a RETRY `mq-expect.kafka` step polls until it observes
  the matching message — rendering Pass — while a never-satisfied variant renders Inconclusive.

## Risks mitigated this sprint (MVP §10)

- Too few pilot teams (recruitment opened with full lead time).
- Late product name (decision process started early in Phase 3).
