# Reference Scenario — Four Technologies in One Test

This directory contains **`reference.e2e.yaml`**, the canonical reference scenario for vouchfx. It is the **first single scenario that composes all four Core step families** and demonstrates every engine feature.

## What it tests

One coherent business transaction spanning:

1. **REST call** (`http.rest`) — GET from a containerised service, with secret bearer token and JSONPath capture.
2. **Database mutation** (`script.csharp` + `db-assert.postgres`) — C# script inserts a row into a seeded Postgres table; subsequent assertion verifies both the seed and the insertion.
3. **Kafka publish and RETRY-consume** (`mq-publish.kafka` + `mq-expect.kafka`) — publishes a JSON event, then polls with engine-owned backoff until it is decoded and matched via JSONPath.
4. **Outbound webhook** (`script.csharp` → `webhook-listen.http`) — simulated callback to the host-owned webhook listener with RETRY capture and assertion.

## Features demonstrated

- **Capture and placeholder substitution** — the hostname captured from the REST response feeds into both the database INSERT and Kafka event payload.
- **Secret resolution** — the REST Authorization header carries `${secret:env/VOUCHFX_REF_BEARER_TOKEN}`, resolved at execution time (never compiled into the delegate).
- **Environment seeding** — `fixtures/seed.sql` populates a reference row before step 1 executes, health-gated.
- **RETRY polling** — Kafka consume and webhook listen both use `verifyMode: RETRY` with bounded backoff, so no manual polling code is needed.
- **Cross-step state** — data flows through `capture`, variable substitution, and the shared `Vars` dictionary.

## Prerequisites

- **.NET 8 SDK** (pinned in `global.json`).
- **Docker** running locally (for the Aspire/Testcontainers topology).
- **`VOUCHFX_REF_BEARER_TOKEN`** environment variable set to a test value (any non-empty string; the whoami service ignores it, but it must be present to resolve the secret).

## Running the scenario

### Via the engine API (compile proof, no Docker)

The non-Docker compile test verifies the scenario parses and compiles without topology:

```bash
dotnet test --filter "FullyQualifiedName~Sprint11ReferenceCompile"
```

### Via the engine API (full Docker capstone)

The Docker integration test runs the complete scenario against a live topology:

```bash
dotnet test --filter "requires=docker&FullyQualifiedName~Sprint11Reference"
```

### Via the `vouchfx` CLI

With the `vouchfx` global tool installed (see the [getting-started guide](../../docs/getting-started.md)), run:

```bash
# Set the bearer token (required for the scenario to execute)
export VOUCHFX_REF_BEARER_TOKEN="test-token-any-value"

# Run the scenario
vouchfx run examples/reference/reference.e2e.yaml

# Generate HTML, JUnit, and event stream reports
vouchfx run examples/reference/reference.e2e.yaml --html report.html --junit results.xml --events events.jsonl

# Render as plain text (WCAG 1.4.1 accessible, no colour)
vouchfx run examples/reference/reference.e2e.yaml --no-decorations
```

The `--events` flag writes a schema-versioned JSON Lines event stream; secret values never appear in its structured fields — only references and hashes — and any resolved secret value that surfaces verbatim in a step's observation text is automatically redacted as well.

## Topology

The scenario stands up:

- **whoami** (traefik/whoami) — HTTP service that echoes the container hostname.
- **refdb** (Postgres) — managed by Aspire; pre-populated with a seed fixture (`fixtures/seed.sql`).
- **events** (Kafka) — managed by Aspire; no schema registry (plain JSON).
- **Webhook listener** — host-owned, bound to `0.0.0.0` on an unguessable path, with token authentication.

## Files

- **`reference.e2e.yaml`** — the main scenario definition.
- **`fixtures/seed.sql`** — the seed fixture applied to Postgres after topology health-check.
- **`../../../tools/vscode-vouchfx/src/test/fixtures/reference-four-tech.e2e.yaml`** — a VSCode Test Explorer fixture mirror (same scenario, used in extension tests).

## Interpreting the verdict

The scenario has four steps, each with a distinct intent:

1. **rest-get-whoami** — Succeeds if the REST call returns 200 and the hostname is successfully captured.
2. **db-insert-row** — Succeeds if the C# script executes without exception.
3. **db-assert-rows** — Succeeds if the query returns exactly 1 row (the script-inserted row), with status matching the captured hostname substitution.
4. **kafka-publish-order** — Succeeds immediately; publishes a JSON event.
5. **kafka-expect-order** — Polls with RETRY until the event is found (or timeout at 30s).
6. **webhook-trigger** — Executes the script that simulates the SUT callback.
7. **webhook-await** — Polls with RETRY until the POST is captured and the path matches the placeholder-substituted URL.

A **Pass** verdict means all steps succeeded. An **Environment error** means a container failed to become healthy or the seed did not apply. An **Inconclusive** verdict means a RETRY step timed out or an upstream capture was not met.

## Further reading

- **[Getting Started](../../docs/getting-started.md)** — your first test in 60 minutes.
- **[Architecture Blueprint](../../docs/01_Technical_Architecture_and_Engineering_Blueprint.md)** — how all the pieces fit together.
- **[YAML DSL Specification](../../docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md)** — the complete reference scenario grammar and JSON Schema.
- **[Recipes](../../docs/recipes.md)** — task-oriented examples: seeding, secrets, test doubles, CI integration.
