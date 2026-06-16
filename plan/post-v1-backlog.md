# Post-v1 Feature Backlog

Items deferred *past* the v1.x engine series. These are either blocked by a frozen contract (the v1 schema,
provider SDK, and event-wire contracts are frozen byte-for-byte and enforced by `SchemaFreezeTests`,
`SdkContractFreezeTests`, and `EventContractFreezeTests`; evolution is additive-only via new optional
interfaces, never a mutation of a v1 contract), or deferred because they are not needed for the pilot and
v1.0 release. Items carry no sprint commitment; they are candidates for v2 planning.

---

## PB-01 · Per-file `metadata.telemetry` opt-out flag

**Deferred to:** v2 (requires a schema change)
**Originally deferred in:** S10-G-04 (Sprint 10)
**Spec:** DSL §3 (document structure / metadata section); MVP §9.3

### Why this is postponed (not a time cut — a frozen-contract constraint)

A per-file `metadata.telemetry: false` flag was considered during S10-G-04 and deliberately not built,
because adding a field to the `metadata` section would change the **frozen v1 language schema**. The v1
schema is frozen byte-for-byte (the `metadata` object is `additionalProperties: false`, and the composed
schema is pinned by a golden-file gate, `SchemaFreezeTests`). The freeze is a deliberate trust commitment
for the entire v1.x engine series — providers, the editor tooling, and downstream consumers all rely on a
stable contract — and the rule is *additive-only via new optional interfaces, never a mutation of a v1
contract*. A new `metadata` property mutates the v1 schema, so it cannot ship inside v1.x; it is exactly the
kind of change a v2 schema-version bump exists for. Until then the three v1 suppression mechanisms — global
consent, the `--no-telemetry` flag, and the `VOUCHFX_NO_TELEMETRY` environment variable — fully cover the
privacy requirement.

### Intent

Let an author suppress telemetry for a specific scenario file via `metadata.telemetry: false`, useful for
scenarios run against sensitive SUT addresses or image names (even though those field *values* are never
collected by the engine).

### Constraints

- Requires a schema-version increment (v1 → v2 at minimum), coordinated with the `SchemaFreezeTests` gate;
  the v1 schema `$id` is unaffected and v2 introduces a new `$id`.
- The three v1 suppression mechanisms remain the complete suppression surface for v1.x.
- A v2 implementation must evaluate the flag at step-execution time (not compile time), consistent with the
  resolve-at-execution-time principle for sensitive data (BP §17).

### Acceptance criteria (v2 planning)

- [ ] `metadata.telemetry: false` is a valid field in the v2 schema (JSON Schema + `SdkContractFreezeTests`
      updated for v2).
- [ ] When the flag is `false`, no telemetry events are emitted for that scenario regardless of global consent.
- [ ] The v1 schema `$id` is unaffected; v2 introduces a new `$id`.
- [ ] The v1 YAML DSL specification (DSL §3) documents the field in the v2 docs.
